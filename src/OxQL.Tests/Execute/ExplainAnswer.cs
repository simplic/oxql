using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.Tests.Execute;

/// <summary>Reads an explain answer the way a consumer does: stages, aliases, owners, the type table and the flags of a member at a stage.</summary>
internal static class ExplainAnswer
{
    /// <summary>Every include: the shape, the notes and the plan (the bound form, the emitted stages, the owner queries).</summary>
    public static readonly IReadOnlyList<string> WithPlan = [ExplainRequest.IncludeShape, ExplainRequest.IncludeNotes, ExplainRequest.IncludePlan];

    /// <summary>The request that also asks for the plan.</summary>
    public static ExplainRequest Planned(this QueryRequest query) => new() { Query = query, Include = WithPlan, IsEnvelope = true };

    public static ExplainStage Stage(this ExplainResult answer, int index) => answer.Stages.Single(stage => stage.Index == index);

    public static JsonObject Alias(this ExplainResult answer, string alias) =>
        answer.Aliases[alias]?.AsObject() ?? throw new InvalidOperationException($"The answer has no alias '{alias}'; it has {string.Join(", ", answer.Aliases.Select(pair => pair.Key))}.");

    /// <summary>The targets of an alias, by target name.</summary>
    public static JsonObject Target(this ExplainResult answer, string alias, string target) =>
        answer.Alias(alias)["targets"]!.AsArray().Select(node => node!.AsObject()).Single(each => each["target"]!.GetValue<string>() == target);

    /// <summary>The owner a stage's placement points to, or null.</summary>
    public static JsonObject? Owner(this ExplainResult answer, int stage) =>
        answer.Stage(stage).Placement?.Owner is { } index ? answer.Owners[index].AsObject() : null;

    public static JsonObject OwnerOf(this ExplainResult answer, string service) =>
        answer.Owners.Select(owner => owner.AsObject()).Single(owner => owner["service"]!.GetValue<string>() == service && owner["via"] is null);

    /// <summary>The owner queries of a keyed stage (needs <c>include: "plan"</c>), in target order.</summary>
    public static List<JsonObject> Queries(this ExplainResult answer, int stage) =>
        answer.Owners.SelectMany(owner => owner["queries"]?.AsArray().Select(query => query!.AsObject()) ?? [])
            .Where(query => query["stage"]!.GetValue<int>() == stage)
            .ToList();

    /// <summary>The owner query of one target of a keyed stage (the only one when no target is named).</summary>
    public static JsonObject Query(this ExplainResult answer, int stage, string? target = null) =>
        answer.Queries(stage).Single(query => target is null || query["target"]!.GetValue<string>() == target)["query"]!.AsObject();

    public static JsonObject Type(this ExplainResult answer, string key) =>
        answer.Types[key]?.AsObject() ?? throw new InvalidOperationException($"The answer has no type '{key}'; it has {string.Join(", ", answer.Types.Select(pair => pair.Key))}.");

    public static List<JsonArray> Members(this ExplainResult answer, string type) =>
        answer.Type(type)["members"]!.AsArray().Select(row => row!.AsArray()).ToList();

    public static List<string> Paths(this ExplainResult answer, string type) =>
        answer.Members(type).Select(row => row[ExplainTypes.Row.Path]!.GetValue<string>()).ToList();

    public static JsonArray? Member(this ExplainResult answer, string type, string path) =>
        answer.Members(type).SingleOrDefault(row => row[ExplainTypes.Row.Path]!.GetValue<string>() == path);

    /// <summary>A fact of a member row by its position, or null when the row ends before it.</summary>
    public static JsonNode? Fact(this JsonArray row, int position) => row.Count > position ? row[position] : null;

    /// <summary>The <c>more</c> block of a member row, empty when it has none.</summary>
    public static JsonObject More(this JsonArray row) => row.Fact(ExplainTypes.Row.More)?.AsObject() ?? [];

    /// <summary>The id of the flags a member has in its type's own entry shape; a union's member has those of the first target that has it.</summary>
    public static string OwnFlagsId(this ExplainResult answer, string type, string path)
    {
        if (type.StartsWith("u:", StringComparison.Ordinal))
        {
            var union = answer.Type(type);
            var row = union["members"]!.AsArray().Select(each => each!.AsArray()).Single(each => each[0]!.GetValue<string>() == path);

            type = union["of"]![row.Count > 1 ? row[1]![0]!.GetValue<int>() : 0]!.GetValue<string>();
        }

        var member = answer.Member(type, path) ?? throw new InvalidOperationException($"'{type}' has no member '{path}'.");

        return member[ExplainTypes.Row.Flags]!.GetValue<string>();
    }

    /// <summary>The flags a member has in its type's own entry shape.</summary>
    public static JsonObject OwnFlags(this ExplainResult answer, string type, string path) =>
        answer.FlagSets[answer.OwnFlagsId(type, path)]!.AsObject();

    /// <summary>The variants that carry a member, as its row points to them in the type's <c>onlyFor</c> list; null on a member every value has.</summary>
    public static List<string>? OnlyFor(this ExplainResult answer, string type, string path) =>
        answer.Member(type, path)!.Fact(ExplainTypes.Row.OnlyFor) is JsonValue index ? answer.Type(type)["onlyFor"]![index.GetValue<int>()].Strings() : null;

    /// <summary>The shape after stage <paramref name="stage"/>, or the entry shape for -1.</summary>
    public static ExplainShape ShapeAt(this ExplainResult answer, int stage) =>
        stage < 0 ? answer.Entry!.Shape : answer.Stage(stage).Shape!;

    /// <summary>The type a root points to after a stage (-1: at the entry).</summary>
    public static string? RootType(this ExplainResult answer, int stage, string root) => answer.ShapeAt(stage).Roots[root]?.GetValue<string>();

    /// <summary>The override set of a root after a stage, or null when its members have their own flags there.</summary>
    public static JsonObject? Overrides(this ExplainResult answer, int stage, string root) =>
        answer.ShapeAt(stage).Flags[root] is JsonValue pointer ? answer.FlagSets[pointer.GetValue<string>()]!.AsObject() : null;

    /// <summary>
    /// The flags of a member of <paramref name="root"/> after a stage (-1: at the entry), as a consumer
    /// reads them: the member's own entry of the stage's override set, else the set's entry for the
    /// member's own flags (<c>~</c>), else what every member has (<c>*</c>), else the type's own; null
    /// when the member is not in the row there. The path <c>""</c> is the root itself.
    /// </summary>
    public static JsonObject? FlagsAt(this ExplainResult answer, int stage, string root, string path)
    {
        var shape = answer.ShapeAt(stage);

        if (!shape.Roots.ContainsKey(root))
            throw new InvalidOperationException($"The row after stage {stage} has no root '{root}'; it has {string.Join(", ", shape.Roots.Select(pair => $"'{pair.Key}'"))}.");

        var overrides = answer.Overrides(stage, root);

        JsonObject? Read(JsonNode? pointer) => pointer is null ? null : answer.FlagSets[pointer.GetValue<string>()]!.AsObject();

        if (overrides is not null && overrides.TryGetPropertyValue(path, out var here))
            return Read(here);

        var own = path.Length == 0 ? null : answer.OwnFlagsId(shape.Roots[root]!.GetValue<string>(), path);

        if (own is not null && overrides?[ExplainTypes.ByOwn] is JsonObject byOwn && byOwn.TryGetPropertyValue(own, out var mapped))
            return Read(mapped);

        if (overrides is not null && overrides.TryGetPropertyValue(ExplainTypes.Every, out var every))
            return Read(every);

        return own is null ? null : answer.FlagSets[own]!.AsObject();
    }

    public static List<string> Operators(this JsonObject flags) => flags["operators"]!.AsArray().Select(op => op!.GetValue<string>()).ToList();

    public static List<string> Strings(this JsonNode? array) => array!.AsArray().Select(node => node!.GetValue<string>()).ToList();

    /// <summary>An explain envelope from its JSON text.</summary>
    public static ExplainRequest Envelope(string body) => JsonSerializer.Deserialize<ExplainRequest>(body, OxQLJson.Wire)!;
}
