using System.Text.Json;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// The one builder of the ordinary OxQL queries a keyed fetch sends an owner. Both modes ask the
/// owner through its public query vocabulary, so the owner binds, authorises and compiles them
/// like any caller's query:
/// <list type="bullet">
///   <item><b>by keys</b> (after the page), per target: <c>match &lt;target field&gt; in keys</c>, the
///   target's filter, the projection, one page as large as the chunk; or the same grouped per key
///   through the internal <c>keyedBy</c> for an item or a member that is not the target's key;</item>
///   <item><b>by condition</b> (before the page, the semi-join): the caller's condition rebased onto
///   the target, the target field projected, paged by offset.</item>
/// </list>
/// </summary>
public static class OwnerQueryBuilder
{
    /// <summary>The target entity a by-condition slot reaches through.</summary>
    public static string TargetOf(SemiJoinSlot slot) => RemoteOf(slot).TargetEntity;

    /// <summary>The member of the target a by-condition slot keys by: the reference's target field, <c>id</c> when undeclared.</summary>
    public static string TargetFieldOf(SemiJoinSlot slot) => RemoteOf(slot).Reference.Path?.Reference?.TargetField ?? "id";

    /// <summary>
    /// The owner query of one by-keys chunk for one target of a resolve. A plain query (no
    /// <paramref name="perKey"/>) matches the target field against the keys, applies the
    /// target's filter and projection, and takes one page as large as the chunk: the form of an
    /// entity target keyed by its own key, where no key has two rows. A grouped query carries
    /// the keys in the internal <c>keyedBy</c> instead, at most <paramref name="perKey"/> rows per
    /// key and a page of <c>keys × perKey</c>; for an item target the key path runs through the
    /// item collection and the element travels under <see cref="BoundKeyedBy.Element"/>, which the
    /// filter and the select are rebased onto and beside which the owning row's members travel
    /// when the resolve names <c>parentAs</c>. The projection always travels and always carries
    /// the member the rows are keyed by. The filter travels as the binder left it: every variable
    /// substituted (DESIGN §3.5.5), since an owner is never sent <c>variables</c>.
    /// <para>
    /// A <paramref name="probe"/> is the existence probe (DESIGN §3.5.2 step 3): the same query
    /// without the target's filter, projecting only the member the rows are keyed by, so a key the
    /// filtered query did not return tells <c>excluded</c> (the probe finds it) from
    /// <c>not_found</c> (it does not).
    /// </para>
    /// <para>
    /// A plain query onto a member that is not the target's key, sent to an owner before 2.1 for a
    /// request that reads its outcomes, pages <paramref name="plainRowsPerKey"/> rows per key, so a
    /// second row under a key arrives or the answer has a next page.
    /// </para>
    /// </summary>
    public static QueryRequest ByKeys(BoundStage.Resolve stage, BoundResolveTarget target, IReadOnlyList<string> keys, int? perKey, bool probe = false, int plainRowsPerKey = 1)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keys);

        var field = target.Declared.Field;
        var item = target.Declared.Item;
        var element = item is null ? "" : BoundKeyedBy.Element + ".";
        var pipeline = new List<PipelineStage>();

        // A key is an id and compares exactly: this host's own string target is asked so, and never
        // folds a key into another's record. A remote owner's field kind is not known here, and it
        // may refuse the option on a member that holds no text; this host keys its answer rows
        // exactly again either way.
        var exact = !target.IsRemote && target.Entity?.Path(field)?.LeafKind == Kind.String;

        if (perKey is null)
            pipeline.Add(new PipelineStage
            {
                Match = new MatchStage
                {
                    Condition = new FilterCondition
                    {
                        Path = field, Op = "in", Value = JsonSerializer.SerializeToElement(keys),
                        Options = exact ? new FilterConditionOptions { CaseSensitive = true } : null,
                    },
                },
                Keys = ["match"],
            });

        if (!probe && target.RemoteFilter is { } filter && filter.ValueKind == JsonValueKind.Object)
        {
            var match = JsonSerializer.Deserialize<MatchStage>(filter.GetRawText(), OxQLJson.Wire)!;

            pipeline.Add(new PipelineStage { Match = element.Length == 0 ? match : match with { Condition = Rebased(match.Condition, element) }, Keys = ["match"] });
        }

        // With a select it is the caller's (a local target's as bound, the paths it has); without
        // one the reserved $default key, which the owner expands to its own entity's key and
        // display members — the pair the local half of this stage keeps — or, for an item, the
        // matched member. Without a projection the owner answers with whole documents,
        // organizationId and every other member included, to a caller that wanted a label.
        // A probe projects the keyed member alone.
        IReadOnlyList<string>? select = target.IsRemote ? target.RemoteSelect : target.Select?.Select(path => path.Wire).ToList();
        var projection = new Dictionary<string, int>(StringComparer.Ordinal);

        if (!probe && select is { Count: > 0 })
            foreach (var path in select)
                projection[element + path] = 1;
        else if (!probe && item is null)
            projection["$default"] = 1;

        projection[element + field] = 1;

        if (!probe && item is not null && stage.ParentAs is not null)
        {
            if (target.RemoteParentSelect is { Count: > 0 } parent)
                foreach (var path in parent)
                    projection[path] = 1;
            else
                projection["$default"] = 1;
        }

        pipeline.Add(new PipelineStage { Project = new ProjectStage { Fields = projection }, Keys = ["project"] });
        pipeline.Add(new PipelineStage { Page = new PageStage { Limit = keys.Count * (perKey ?? Math.Max(1, plainRowsPerKey)) }, Keys = ["page"] });

        return new QueryRequest
        {
            EntityType = target.Declared.Entity,
            Pipeline = pipeline,
            KeyedBy = perKey is { } rows
                ? new KeyedByMember { Path = item is null ? field : item + "." + field, Keys = JsonSerializer.SerializeToElement(keys), PerKey = rows }
                : null,
        };
    }

    /// <summary>A condition on an item's members rebased onto the element's alias; an <c>any</c>'s inner condition stays relative to its own element.</summary>
    private static FilterCondition? Rebased(FilterCondition? condition, string prefix) => condition is null ? null : condition with
    {
        Path = condition.Path is null ? null : prefix + condition.Path,
        And = condition.And?.Select(inner => Rebased(inner, prefix)!).ToList(),
        Or = condition.Or?.Select(inner => Rebased(inner, prefix)!).ToList(),
        Not = condition.Not is null ? null : Rebased(condition.Not, prefix),
    };

    /// <summary>
    /// The owner query of one by-condition page: the slot's condition relative to the target, the
    /// target field projected, the page addressed by offset; <paramref name="count"/> asks for the
    /// total the first call uses to refuse above the cap.
    /// </summary>
    public static QueryRequest ByCondition(SemiJoinSlot slot, int pageSize, int offset, bool count)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var remote = RemoteOf(slot);
        var alias = remote.StoragePrefix;
        var relative = slot.Leaf.Path.Wire.StartsWith(alias + ".", StringComparison.Ordinal) ? slot.Leaf.Path.Wire[(alias.Length + 1)..] : slot.Leaf.Path.Wire;
        var field = TargetFieldOf(slot);
        var operand = slot.Leaf.Operand is BoundOperand.Raw raw ? raw.Value : JsonSerializer.SerializeToElement((object?)null);

        var condition = new FilterCondition
        {
            Path = relative,
            Op = slot.Leaf.Op,
            Value = operand,
            // The owner binds the comparison under its own default; only what the caller wrote travels.
            Options = slot.Leaf.IgnoreCase switch
            {
                true => new FilterConditionOptions { IgnoreCase = true },
                false => new FilterConditionOptions { CaseSensitive = true },
                null => null,
            },
        };

        return new QueryRequest
        {
            EntityType = remote.TargetEntity,
            Pipeline =
            [
                new PipelineStage { Match = new MatchStage { Condition = condition }, Keys = ["match"] },
                new PipelineStage { Project = new ProjectStage { Fields = new Dictionary<string, int>(StringComparer.Ordinal) { [field] = 1 } }, Keys = ["project"] },
                new PipelineStage { Page = new PageStage { Limit = pageSize, Offset = offset == 0 ? null : offset, IncludeTotalCount = count }, Keys = ["page"] },
            ],
        };
    }

    private static ShapeNode.Remote RemoteOf(SemiJoinSlot slot) => (ShapeNode.Remote)slot.Leaf.Path.Root;
}
