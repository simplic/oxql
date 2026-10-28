using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace OxQL.Core.Binding;

/// <summary>
/// The canonical JSON form of a bound pipeline: what explain shows and what the cursor is
/// fingerprinted over (without the page). Storage paths and typed operands are inside it, so
/// two requests that bind to the same storage work have the same fingerprint.
/// <para>
/// A contract 2 cursor is fingerprinted over what decides which rows there are and in which order:
/// the stages a filter, a sort, an unwind or a group reads. What only shapes the row is
/// left out (<see cref="DisplayOnly"/>): the trailing projection, a join nothing but the row's
/// display reads, and every <c>select</c>. Showing or hiding a column keeps a cursor valid.
/// Contract 1 fingerprints the whole form, as it always has.
/// </para>
/// </summary>
public static class BoundCanonical
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    /// <summary>The canonical JSON of the stages, page included.</summary>
    /// <remarks>
    /// The internal <c>keyedBy</c> is written only when the request carried it, so every other
    /// request renders as before.
    /// </remarks>
    public static JsonObject Render(BoundStage.Scope scope, IReadOnlyList<BoundStage> stages, BoundStage.Page? page, PagingMode mode, BoundKeyedBy? keyedBy = null) =>
        Render(scope, stages, page, mode, keyedBy, contract2: false, rows: false);

    /// <summary>
    /// The canonical JSON of the stages. <paramref name="contract2"/> writes the page as contract 2 has
    /// it (<c>limit</c>, <c>cursor</c>, <c>count</c>; an internal call's <c>offset</c> and
    /// <c>includeTotalCount</c> only when it carries them). <paramref name="rows"/> is the form a
    /// contract 2 cursor is fingerprinted over: no <c>select</c> of any join.
    /// </summary>
    public static JsonObject Render(BoundStage.Scope scope, IReadOnlyList<BoundStage> stages, BoundStage.Page? page, PagingMode mode, BoundKeyedBy? keyedBy, bool contract2, bool rows = false)
    {
        // A request that ends in values has no page: the one the binder supplied only carries the stage's limit.
        if (stages.Any(stage => stage is BoundStage.Values))
        {
            stages = stages.Where(stage => stage is not BoundStage.Page).ToList();
            page = null;
        }

        var node = new JsonObject
        {
            ["entity"] = scope.Entity.Id,
            ["scope"] = new JsonObject { ["path"] = scope.OrganisationStorage, ["organisation"] = scope.Organisation.ToString() },
            ["stages"] = new JsonArray(stages.Select(stage => (JsonNode)(rows ? WithoutSelects(RenderStage(stage, contract2)) : RenderStage(stage, contract2))).ToArray()),
            ["paging"] = mode == PagingMode.Keyset ? "keyset" : "offset",
        };

        // A contract 2 cursor is of another form than a contract 1 cursor of the same stages: it
        // carries the rows served, and is cut to what decides the rows.
        if (rows)
            node["cursor"] = "rows";

        if (keyedBy is not null)
            node["keyedBy"] = new JsonObject
            {
                ["path"] = keyedBy.Path.Storage,
                ["item"] = keyedBy.ItemStorage,
                ["keys"] = new JsonArray(keyedBy.Keys.Select(value => JsonNode.Parse(value.ToJson(Canonical))).ToArray()),
                ["perKey"] = keyedBy.PerKey,
            };

        // What a remote lookup adds to its owner query is written only when it is there, so a
        // resolve's owner query renders as before.
        if (keyedBy?.References is { } references)
            node["keyedBy"]!["references"] = references;

        if (keyedBy?.KeyAlias is not null)
            node["keyedBy"]!["rows"] = "entity";

        if (page is not null && contract2)
        {
            var rendered = RenderPage(page);

            rendered.Insert(1, "cursor", page.Cursor is null ? null : "…");
            node["page"] = rendered;
        }
        else if (page is not null)
        {
            node["page"] = new JsonObject
            {
                ["limit"] = page.Limit,
                ["offset"] = page.Offset,
                ["cursor"] = page.Cursor is null ? null : "…",
                ["includeTotalCount"] = IncludeTotalCount(page),
            };
        }

        return node;
    }

    /// <summary>
    /// A contract 2 page: its limit (0 for a count-only request) and the count step it asked for.
    /// An internal call's <c>offset</c> and <c>includeTotalCount</c> are written only when it carries them.
    /// </summary>
    private static JsonObject RenderPage(BoundStage.Page page)
    {
        var node = new JsonObject { ["limit"] = page.CountOnly ? 0 : page.Limit };

        if (page.Offset != 0)
            node["offset"] = page.Offset;

        if (page.IncludeTotalCount)
            node["includeTotalCount"] = IncludeTotalCount(page);

        if (page.Count is { } count)
            node["count"] = new JsonObject { ["rows"] = count.Rows, ["upTo"] = count.Ceiling, ["budgetMs"] = count.BudgetMs };

        return node;
    }

    /// <summary>The fingerprint: SHA-256 over the canonical form without the page.</summary>
    public static string Fingerprint(BoundStage.Scope scope, IReadOnlyList<BoundStage> stages, PagingMode mode, BoundKeyedBy? keyedBy = null)
    {
        var text = Render(scope, stages.Where(stage => stage is not BoundStage.Page).ToList(), null, mode, keyedBy).ToJsonString();

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// The fingerprint of a contract 2 cursor: SHA-256 over the canonical form without the
    /// page, without the stages that only shape the row (<see cref="DisplayOnly"/>) and without any
    /// join's <c>select</c>.
    /// </summary>
    public static string RowsFingerprint(BoundStage.Scope scope, IReadOnlyList<BoundStage> stages, IReadOnlyList<PathRead> reads, PagingMode mode, BoundKeyedBy? keyedBy = null)
    {
        var shaping = DisplayOnly(stages, reads);
        var text = Render(scope, stages.Where(stage => stage is not BoundStage.Page && !shaping.Contains(stage)).ToList(), null, mode, keyedBy, contract2: true, rows: true).ToJsonString();

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// The stages that decide neither which rows there are nor their order:
    /// <list type="bullet">
    /// <item>a join (<c>lookup</c>, <c>resolve</c>) whose alias, owning row and outcome no stage reads
    /// but a projection: nothing matches, sorts, unwinds, groups or joins on it, so it only fills the
    /// row. The read ledger says so (<paramref name="reads"/>): a join read for anything but
    /// <c>project</c> stays in, unless the read is such a join's own (the key it follows, the
    /// condition that picks its case), so a chain of them is outside as one;</item>
    /// <item>a projection that only such joins, sorts and the page follow: the row's last cut.</item>
    /// </list>
    /// A stage continued at an owner stays in: what it reads is the owner's to know.
    /// </summary>
    public static IReadOnlySet<BoundStage> DisplayOnly(IReadOnlyList<BoundStage> stages, IReadOnlyList<PathRead> reads)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(reads);

        var shaping = new HashSet<BoundStage>(ReferenceEqualityComparer.Instance);
        var joins = stages.Where(stage => stage is BoundStage.Lookup or BoundStage.Resolve).ToList();

        // A join that only fills the row reads its own key off another join without making that one
        // decide a row, so the joins are found from the last of a chain back: until none is added.
        while (true)
        {
            var own = shaping.Select(stage => stage switch { BoundStage.Lookup lookup => lookup.Stage, BoundStage.Resolve resolve => resolve.Stage, _ => -1 }).Where(stage => stage >= 0).ToHashSet();
            var read = new HashSet<string>(StringComparer.Ordinal);

            // What a stage other than a projection reads: the join it loads from, and the root it names.
            foreach (var entry in reads)
            {
                if (entry.Use == ReadUse.Project || (entry.Use is ReadUse.ResolveKey or ReadUse.LookupOn or ReadUse.CaseCondition && own.Contains(entry.Stage)))
                    continue;

                if (entry.Alias is { } alias)
                    read.Add(alias);

                read.Add(entry.Path.Split('.', 2)[0]);
            }

            // A continued stage names its anchor, and the aliases it adds are read like any other.
            foreach (var continued in stages.OfType<ContinuedStage>())
                read.Add(continued.Anchor);

            var before = shaping.Count;

            foreach (var stage in joins)
            {
                IEnumerable<string?> aliases = stage switch
                {
                    BoundStage.Lookup lookup => [lookup.As],
                    BoundStage.Resolve resolve => [resolve.As, resolve.ParentAs, resolve.OutcomeAs],
                    _ => [],
                };

                if (!aliases.Any(alias => alias is not null && read.Contains(alias)))
                    shaping.Add(stage);
            }

            if (shaping.Count == before)
                break;
        }

        for (var index = stages.Count - 1; index >= 0; index--)
        {
            var stage = stages[index];

            if (stage is BoundStage.Project)
                shaping.Add(stage);
            else if (stage is not (BoundStage.Sort or BoundStage.Page) && !shaping.Contains(stage))
                break;
        }

        return shaping;
    }

    /// <summary>A rendered stage without the <c>select</c> and <c>parentSelect</c> of its join, at any depth: what a join loads never decides a row.</summary>
    private static JsonObject WithoutSelects(JsonObject stage)
    {
        static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject item:
                    item.Remove("select");
                    item.Remove("parentSelect");

                    foreach (var member in item.ToList())
                        if (member.Key is not ("filter" or "match"))
                            Strip(member.Value);
                    break;

                case JsonArray list:
                    foreach (var element in list)
                        Strip(element);
                    break;
            }
        }

        if (stage["lookup"] is not null || stage["resolve"] is not null)
            Strip(stage);

        return stage;
    }

    private static JsonObject RenderStage(BoundStage stage, bool contract2 = false) => stage switch
    {
        BoundStage.Match match => new JsonObject { ["match"] = RenderCondition(match.Condition) },
        BoundStage.Lookup lookup => new JsonObject { ["lookup"] = RenderLookup(lookup) },
        BoundStage.Resolve { RemoteLookup: { } remoteLookup } resolve => new JsonObject { ["lookup"] = RenderRemoteLookup(resolve, remoteLookup) },
        BoundStage.Resolve resolve => new JsonObject { ["resolve"] = RenderResolve(resolve) },
        BoundStage.Unwind unwind => new JsonObject { ["unwind"] = RenderUnwind(unwind) },
        BoundStage.Group group => new JsonObject
        {
            ["group"] = new JsonObject
            {
                ["by"] = new JsonArray(group.Keys.Select(key => (JsonNode)new JsonObject
                {
                    ["as"] = key.As,
                    ["path"] = key.Path?.Storage,
                    ["dateTrunc"] = key.Trunc is null ? null : new JsonObject
                    {
                        ["path"] = key.Trunc.Path.Storage,
                        ["unit"] = key.Trunc.Unit,
                        ["timezone"] = key.Trunc.Timezone,
                        ["weekStart"] = key.Trunc.WeekStart,
                    },
                }).ToArray()),
                ["fields"] = new JsonArray(group.Fields.Select(field => (JsonNode)new JsonObject
                {
                    ["as"] = field.As,
                    ["function"] = field.Function,
                    ["argument"] = field.Argument is null ? null : RenderExpression(field.Argument),
                }).ToArray()),
            },
        },
        BoundStage.Project project => new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["mode"] = project.Inclusion ? "include" : "exclude",
                ["paths"] = new JsonArray(project.Paths.Select(path => (JsonNode)(path.Storage ?? path.Wire)).ToArray()),
                ["id"] = project.IncludeId,
            },
        },
        BoundStage.Sort sort => new JsonObject
        {
            ["sort"] = new JsonArray(sort.Fields.Select(field => (JsonNode)new JsonObject
            {
                ["path"] = field.Path.Storage,
                ["direction"] = field.Ascending ? "asc" : "desc",
                ["caseSensitive"] = !field.IgnoreCase,
            }).ToArray()),
        },
        BoundStage.Values values => new JsonObject
        {
            ["values"] = new JsonObject
            {
                ["path"] = values.Path.Storage,
                ["unwind"] = new JsonArray(values.Collections.Select(collection => (JsonNode)collection).ToArray()),
                ["search"] = values.Search is null ? null : RenderCondition(values.Search),
                ["limit"] = values.Limit,
                ["counts"] = values.Counts,
                ["scan"] = values.Scan,
            },
        },
        BoundStage.Page page when contract2 => new JsonObject { ["page"] = RenderPage(page) },
        BoundStage.Page page => new JsonObject
        {
            ["page"] = new JsonObject { ["limit"] = page.Limit, ["offset"] = page.Offset, ["includeTotalCount"] = IncludeTotalCount(page) },
        },
        BoundStage.Scope scope => new JsonObject { ["scope"] = scope.OrganisationStorage },
        ContinuedStage continued => new JsonObject { ["continued"] = RenderContinued(continued) },
        _ => new JsonObject { ["unknown"] = stage.GetType().Name },
    };

    /// <summary>
    /// A stage continued at the owner of <c>anchor</c>: the anchor, the one target it
    /// applies to, the aliases it adds and the stage as the owner is sent it before the per-target
    /// rewrite, in wire form with every variable substituted. The owner binds it, so there is no
    /// storage form here; two requests continuing different stages never share a fingerprint. No
    /// request without a continued stage renders one, so a cursor of such a request stays valid.
    /// </summary>
    private static JsonObject RenderContinued(ContinuedStage continued)
    {
        var node = new JsonObject
        {
            ["anchor"] = continued.Anchor,
            ["kind"] = continued.Kind,
            ["forTarget"] = continued.ForTarget,
            ["aliases"] = new JsonArray(continued.Aliases.Select(alias => (JsonNode)alias).ToArray()),
            ["stage"] = JsonSerializer.SerializeToNode(continued.Stage, Models.OxQLJson.Wire),
        };

        // Written only for a stage that applies to several targets and not all (a union join, or a stage
        // under its alias), so every other continued stage renders as it did.
        if (continued.Targets is { } targets)
            node["targets"] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray());

        return node;
    }

    /// <summary>
    /// A lookup. <c>sort</c>, <c>first</c> and <c>on</c> are written only when they differ from
    /// what a lookup without them does (children by key, an array, the entity itself as the parent), so a
    /// lookup without them renders without them and its cursors stay valid. The stage
    /// index is bound-only and never written.
    /// </summary>
    private static JsonObject RenderLookup(BoundStage.Lookup lookup)
    {
        var node = new JsonObject
        {
            ["from"] = lookup.From.Id,
            ["localField"] = lookup.ParentKeyStorage,
            ["foreignField"] = lookup.ChildKeyStorage,
            ["as"] = lookup.As,
            ["select"] = new JsonArray(lookup.Select.Select(path => (JsonNode)path.Storage!).ToArray()),
            ["filter"] = lookup.Filter is null ? null : RenderCondition(lookup.Filter),
            ["limit"] = lookup.Limit,
        };

        if (lookup.ChildSort is { Count: > 0 } sort)
            node["sort"] = new JsonArray(sort.Select(field => (JsonNode)new JsonObject
            {
                ["path"] = field.Path.Storage,
                ["direction"] = field.Ascending ? "asc" : "desc",
                ["caseSensitive"] = !field.IgnoreCase,
            }).ToArray());

        if (lookup.First)
            node["first"] = true;

        if (lookup.On is not null)
            node["on"] = lookup.On;

        return node;
    }

    /// <summary>
    /// A lookup of another service's entity: the child as written, the parent key's
    /// storage it joins on, and the members the owner binds — path, select, filter (every variable
    /// substituted), sort, owning-row select — as written, since the owner binds them and this host
    /// has no storage form of them. A request without one renders none, so its cursors stay valid.
    /// </summary>
    private static JsonObject RenderRemoteLookup(BoundStage.Resolve resolve, BoundRemoteLookup lookup)
    {
        var node = new JsonObject
        {
            ["from"] = lookup.From,
            ["localField"] = resolve.Reference.Storage,
            ["path"] = lookup.Path,
            ["as"] = resolve.As,
            ["remote"] = true,
            ["select"] = lookup.Select is null ? null : new JsonArray(lookup.Select.Select(path => (JsonNode)path).ToArray()),
            ["filter"] = lookup.Filter is { } raw ? JsonNode.Parse(raw.GetRawText()) : null,
            ["limit"] = lookup.Limit,
        };

        if (lookup.Sort.Count > 0)
            node["sort"] = JsonSerializer.SerializeToNode(lookup.Sort, Models.OxQLJson.Wire);

        if (lookup.First)
            node["first"] = true;

        if (lookup.On is not null)
            node["on"] = lookup.On;

        if (resolve.ParentAs is not null)
        {
            node["parentAs"] = resolve.ParentAs;
            node["parentSelect"] = lookup.ParentSelect is null ? null : new JsonArray(lookup.ParentSelect.Select(path => (JsonNode)path).ToArray());
        }

        return node;
    }

    /// <summary>
    /// A resolve. The plain members describe the first target of the first case. <c>elements</c>,
    /// <c>collection</c>, <c>narrowedTo</c>, <c>parentAs</c> and <c>cases</c> are written only when
    /// they differ from the plain resolve (one simple case, one value per row, no owning row),
    /// so a plain resolve renders without them and its cursors stay valid. The executor, the stage index
    /// and <c>onMissing</c> never change rows and are never written.
    /// </summary>
    private static JsonObject RenderResolve(BoundStage.Resolve resolve)
    {
        var firstIsRemote = resolve.Cases is [{ Targets: [{ IsRemote: true }, ..] }, ..] || (resolve.Cases is null && resolve.IsRemote);
        var node = new JsonObject
        {
            ["path"] = resolve.Reference.Storage,
            ["as"] = resolve.As,
            ["target"] = resolve.TargetEntity,
            ["targetField"] = resolve.TargetField,
            ["remote"] = resolve.IsRemote,
            ["select"] = firstIsRemote
                ? new JsonArray((resolve.RemoteSelect ?? []).Select(path => (JsonNode)path).ToArray())
                : new JsonArray((resolve.Select ?? []).Select(path => (JsonNode)path.Storage!).ToArray()),
            ["filter"] = firstIsRemote
                ? (resolve.RemoteFilter is { } raw ? JsonNode.Parse(raw.GetRawText()) : null)
                : (resolve.Filter is null ? null : RenderCondition(resolve.Filter)),
        };

        if (resolve.Elements is { } elements)
        {
            node["elements"] = elements == ResolveElements.First ? "first" : "all";
            node["collection"] = resolve.CollectionStorage;
        }

        if (resolve.NarrowedTo is not null)
            node["narrowedTo"] = resolve.NarrowedTo;

        if (resolve.ParentAs is not null)
            node["parentAs"] = resolve.ParentAs;

        // The row carries one more member, so a cursor of the request without it is another request's.
        if (resolve.OutcomeAs is not null)
            node["outcomeAs"] = resolve.OutcomeAs;

        if (resolve.Cases is { } cases && !(cases is [{ Declared.IsSimple: true }] && resolve.ParentAs is null))
            node["cases"] = new JsonArray(cases.Select(bound => (JsonNode)RenderCase(bound)).ToArray());

        return node;
    }

    /// <summary>One case of a resolve: its condition in stored form, its conversion, and each target as bound.</summary>
    private static JsonObject RenderCase(BoundResolveCase bound) => new()
    {
        ["when"] = bound.When is null ? null : new JsonObject
        {
            ["path"] = bound.When.Storage,
            ["variant"] = bound.When.IsVariant,
            ["values"] = new JsonArray(bound.When.Values.Select(value => JsonNode.Parse(value.ToJson(Canonical))).ToArray()),
        },
        ["keyAs"] = bound.KeyAs == Model.KeyAs.Guid ? "guid" : null,
        ["targets"] = new JsonArray(bound.Targets.Select(target => (JsonNode)new JsonObject
        {
            ["entity"] = target.Declared.Entity,
            ["item"] = target.ItemStorage ?? target.Declared.Item,
            ["field"] = target.FieldStorage ?? target.Declared.Field,
            ["remote"] = target.IsRemote,
            ["select"] = target.IsRemote
                ? new JsonArray((target.RemoteSelect ?? []).Select(path => (JsonNode)path).ToArray())
                : new JsonArray((target.Select ?? []).Select(path => (JsonNode)path.Storage!).ToArray()),
            ["filter"] = target.IsRemote
                ? (target.RemoteFilter is { } raw ? JsonNode.Parse(raw.GetRawText()) : null)
                : (target.Filter is null ? null : RenderCondition(target.Filter)),
            ["parentSelect"] = target.IsRemote
                ? (target.RemoteParentSelect is null ? null : new JsonArray(target.RemoteParentSelect.Select(path => (JsonNode)path).ToArray()))
                : (target.ParentSelect is null ? null : new JsonArray(target.ParentSelect.Select(path => (JsonNode)path.Storage!).ToArray())),
        }).ToArray()),
    };

    /// <summary>
    /// An unwind. <c>flatten</c> and its depth are written only when the unwind flattens, so an
    /// unwind without it renders without it and its cursors stay valid.
    /// </summary>
    private static JsonObject RenderUnwind(BoundStage.Unwind unwind)
    {
        var node = new JsonObject
        {
            ["path"] = unwind.Path.Storage,
            ["as"] = unwind.As,
            ["preserveNull"] = unwind.PreserveNull,
            ["includeIndex"] = unwind.IncludeIndex,
        };

        if (unwind.Flatten is { } flatten)
        {
            node["flatten"] = flatten.Storage;
            node["flattenDepth"] = flatten.Depth;
        }

        return node;
    }

    /// <summary>The count request as the caller wrote it: the request's own cap as a number, otherwise the boolean.</summary>
    private static JsonNode IncludeTotalCount(BoundStage.Page page) =>
        page.CountCap is { } cap ? JsonValue.Create(cap) : JsonValue.Create(page.IncludeTotalCount);

    /// <summary>
    /// One condition in its canonical form. Public so a caller can key a cache by the condition
    /// it is about to ask an owner, without inventing a second rendering of the same tree.
    /// </summary>
    public static JsonNode RenderCondition(BoundCondition condition) => condition switch
    {
        BoundCondition.And and => new JsonObject { ["and"] = new JsonArray(and.Conditions.Select(RenderCondition).ToArray()) },
        BoundCondition.Or or => new JsonObject { ["or"] = new JsonArray(or.Conditions.Select(RenderCondition).ToArray()) },
        BoundCondition.Not not => new JsonObject { ["not"] = RenderCondition(not.Condition) },
        BoundCondition.Any any => new JsonObject { ["any"] = new JsonObject { ["path"] = any.Path.Storage, ["inner"] = RenderCondition(any.Inner) } },
        // A variant test: the discriminator element and the stored values it admits, the null
        // standing for a value stored without one.
        BoundCondition.Leaf { Op: "is", IsSemiJoin: false } leaf => new JsonObject
        {
            ["path"] = leaf.Path.Storage ?? leaf.Path.Wire,
            ["op"] = leaf.Op,
            ["element"] = OperandCoercer.VariantHolder(leaf.Path)?.DiscriminatorElement,
            ["operand"] = RenderOperand(leaf.Operand),
        },
        BoundCondition.Leaf leaf => new JsonObject
        {
            ["path"] = leaf.Path.Storage ?? leaf.Path.Wire,
            ["op"] = leaf.Op,
            ["operand"] = RenderOperand(leaf.Operand),
            // A remote leaf the caller wrote no option for is the owner's default, which the
            // owner reports as folding; it renders as the fold.
            ["caseSensitive"] = leaf.IgnoreCase is false,
            ["semiJoin"] = leaf.IsSemiJoin,
            // Every condition renders it, so it is part of every fingerprint a cursor in circulation
            // was minted under (the contract 1 golden's among them): it stays, whatever the compiler
            // now makes of a defined key.
            ["addonDecimal"] = leaf.Path.Addon is not null && leaf.Path.Kind == Model.Kind.Decimal,
        },
        _ => new JsonObject(),
    };

    private static JsonNode? RenderOperand(BoundOperand operand) => operand switch
    {
        BoundOperand.Single single => JsonNode.Parse(single.Value.ToJson(Canonical)),
        BoundOperand.Set set => new JsonObject { ["set"] = new JsonArray(set.Values.Select(value => JsonNode.Parse(value.ToJson(Canonical))).ToArray()) },
        BoundOperand.Tolerant tolerant => new JsonObject { ["tolerant"] = new JsonArray(tolerant.Alternatives.Select(value => JsonNode.Parse(value.ToJson(Canonical))).ToArray()) },
        BoundOperand.Raw raw => new JsonObject { ["raw"] = JsonNode.Parse(raw.Value.GetRawText()) },
        _ => null,
    };

    private static JsonNode RenderExpression(BoundExpression expression) => expression switch
    {
        BoundExpression.Path path => new JsonObject { ["path"] = path.Resolved.Storage },
        BoundExpression.Literal literal => new JsonObject { ["literal"] = JsonNode.Parse(literal.Value.ToJson(Canonical)) },
        BoundExpression.Arithmetic arithmetic => new JsonObject { [arithmetic.Operator] = new JsonArray(arithmetic.Operands.Select(RenderExpression).ToArray()) },
        _ => new JsonObject(),
    };
}
