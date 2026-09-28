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
///   <item><b>by keys</b> (after the page): <c>match &lt;target field&gt; in keys</c>, the stage's
///   remote filter, the projection, one page as large as the chunk;</item>
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
    /// The owner query of one by-keys chunk: the keys, the stage's remote filter, and a projection
    /// that always travels and always carries the target field so the rows can be keyed.
    /// </summary>
    public static QueryRequest ByKeys(BoundStage.Resolve stage, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(keys);

        var pipeline = new List<PipelineStage>
        {
            new()
            {
                Match = new MatchStage { Condition = new FilterCondition { Path = stage.TargetField, Op = "in", Value = JsonSerializer.SerializeToElement(keys) } },
                Keys = ["match"],
            },
        };

        if (stage.RemoteFilter is { } filter && filter.ValueKind == JsonValueKind.Object)
            pipeline.Add(new PipelineStage { Match = JsonSerializer.Deserialize<MatchStage>(filter.GetRawText(), OxQLJson.Wire)!, Keys = ["match"] });

        // A projection always travels. With a select it is the caller's; without one it is
        // the reserved $default key, which the owner expands to its own entity's key and
        // display members — the pair the local half of this stage keeps. Without a projection
        // the owner answers with whole documents, organizationId and every other member
        // included, to a caller that wanted a label.
        var projection = stage.RemoteSelect is { Count: > 0 } select
            ? select.ToDictionary(path => path, _ => 1, StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal) { ["$default"] = 1 };

        projection[stage.TargetField] = 1;
        pipeline.Add(new PipelineStage { Project = new ProjectStage { Fields = projection }, Keys = ["project"] });

        pipeline.Add(new PipelineStage { Page = new PageStage { Limit = keys.Count }, Keys = ["page"] });

        return new QueryRequest { EntityType = stage.TargetEntity, Pipeline = pipeline };
    }

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
