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
/// </summary>
public static class BoundCanonical
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    /// <summary>The canonical JSON of the stages, page included.</summary>
    public static JsonObject Render(BoundStage.Scope scope, IReadOnlyList<BoundStage> stages, BoundStage.Page? page, PagingMode mode)
    {
        var node = new JsonObject
        {
            ["entity"] = scope.Entity.Id,
            ["scope"] = new JsonObject { ["path"] = scope.OrganisationStorage, ["organisation"] = scope.Organisation.ToString() },
            ["stages"] = new JsonArray(stages.Select(stage => (JsonNode)RenderStage(stage)).ToArray()),
            ["paging"] = mode == PagingMode.Keyset ? "keyset" : "offset",
        };

        if (page is not null)
            node["page"] = new JsonObject
            {
                ["limit"] = page.Limit,
                ["offset"] = page.Offset,
                ["cursor"] = page.Cursor is null ? null : "…",
                ["includeTotalCount"] = page.IncludeTotalCount,
            };

        return node;
    }

    /// <summary>The fingerprint: SHA-256 over the canonical form without the page.</summary>
    public static string Fingerprint(BoundStage.Scope scope, IReadOnlyList<BoundStage> stages, PagingMode mode)
    {
        var text = Render(scope, stages.Where(stage => stage is not BoundStage.Page).ToList(), null, mode).ToJsonString();

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static JsonObject RenderStage(BoundStage stage) => stage switch
    {
        BoundStage.Match match => new JsonObject { ["match"] = RenderCondition(match.Condition) },
        BoundStage.Lookup lookup => new JsonObject
        {
            ["lookup"] = new JsonObject
            {
                ["from"] = lookup.From.Id,
                ["localField"] = lookup.ParentKeyStorage,
                ["foreignField"] = lookup.ChildKeyStorage,
                ["as"] = lookup.As,
                ["select"] = new JsonArray(lookup.Select.Select(path => (JsonNode)path.Storage!).ToArray()),
                ["filter"] = lookup.Filter is null ? null : RenderCondition(lookup.Filter),
                ["limit"] = lookup.Limit,
            },
        },
        BoundStage.Resolve resolve => new JsonObject
        {
            ["resolve"] = new JsonObject
            {
                ["path"] = resolve.Reference.Storage,
                ["as"] = resolve.As,
                ["target"] = resolve.TargetEntity,
                ["targetField"] = resolve.TargetField,
                ["remote"] = resolve.IsRemote,
                ["select"] = resolve.IsRemote
                    ? new JsonArray((resolve.RemoteSelect ?? []).Select(path => (JsonNode)path).ToArray())
                    : new JsonArray((resolve.Select ?? []).Select(path => (JsonNode)path.Storage!).ToArray()),
                ["filter"] = resolve.IsRemote
                    ? (resolve.RemoteFilter is { } raw ? JsonNode.Parse(raw.GetRawText()) : null)
                    : (resolve.Filter is null ? null : RenderCondition(resolve.Filter)),
            },
        },
        BoundStage.Unwind unwind => new JsonObject
        {
            ["unwind"] = new JsonObject
            {
                ["path"] = unwind.Path.Storage,
                ["as"] = unwind.As,
                ["preserveNull"] = unwind.PreserveNull,
                ["includeIndex"] = unwind.IncludeIndex,
            },
        },
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
        BoundStage.Page page => new JsonObject
        {
            ["page"] = new JsonObject { ["limit"] = page.Limit, ["offset"] = page.Offset, ["includeTotalCount"] = page.IncludeTotalCount },
        },
        BoundStage.Scope scope => new JsonObject { ["scope"] = scope.OrganisationStorage },
        _ => new JsonObject { ["unknown"] = stage.GetType().Name },
    };

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
        BoundCondition.Leaf leaf => new JsonObject
        {
            ["path"] = leaf.Path.Storage ?? leaf.Path.Wire,
            ["op"] = leaf.Op,
            ["operand"] = RenderOperand(leaf.Operand),
            // A remote leaf the caller wrote no option for is the owner's default, which the
            // owner reports as folding; it renders as the fold.
            ["caseSensitive"] = leaf.IgnoreCase is false,
            ["semiJoin"] = leaf.IsSemiJoin,
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
