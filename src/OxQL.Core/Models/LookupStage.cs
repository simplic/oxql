using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// A backward join along a declared reference: <c>from</c> is the child entity, <c>path</c>
/// the child's member that references the parent, the result an array under <c>as</c> ordered
/// by <c>sort</c> (the child's key by default), or with <c>first</c> the first child or null.
/// The parent is the current entity, or with <c>on</c> an entity alias of the row.
/// </summary>
[JsonConverter(typeof(LookupStageConverter))]
public sealed record LookupStage
{
    /// <summary>The child entity id.</summary>
    public string? From { get; init; }

    /// <summary>The child's member carrying the reference to the current entity.</summary>
    public string? Path { get; init; }

    /// <summary>The alias the array is placed under.</summary>
    public string? As { get; init; }

    /// <summary>Wire paths on the child to keep; the child's key and display members by default.</summary>
    public IReadOnlyList<string>? Select { get; init; }

    /// <summary>A condition on the child.</summary>
    public MatchStage? Filter { get; init; }

    /// <summary>The most children per parent, under the host cap.</summary>
    public int? Limit { get; init; }

    /// <summary>The order of the children, bound against the child; the child's key completes it. Contract 2.</summary>
    public IReadOnlyList<SortField>? Sort { get; init; }

    /// <summary>The first child by <see cref="Sort"/> as an object, or null, instead of the array. Contract 2.</summary>
    public bool? First { get; init; }

    /// <summary>The alias of the parent row; the implicit root when absent. Contract 2.</summary>
    public string? On { get; init; }

    /// <summary>The one target of a union alias the stage belongs to, on a stage continued under it. Contract 2.</summary>
    public string? ForTarget { get; init; }

    /// <summary>Members the caller wrote with a value of the wrong JSON kind, such as a <c>first</c> that is not a boolean.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Malformed { get; init; } = [];

    /// <summary>Member names the caller wrote that the stage does not have: the v1 <c>localPath</c>, <c>foreignPath</c>, <c>convert</c> among them.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Unknown { get; init; } = [];
}

/// <summary>A forward join along a declared reference: <c>path</c> carries the reference, the target row is placed under <c>as</c>, or null.</summary>
[JsonConverter(typeof(ResolveStageConverter))]
public sealed record ResolveStage
{
    /// <summary>The member carrying the reference.</summary>
    public string? Path { get; init; }

    /// <summary>The alias the object is placed under.</summary>
    public string? As { get; init; }

    /// <summary>Wire paths on the target to keep.</summary>
    public IReadOnlyList<string>? Select { get; init; }

    /// <summary>A condition on the target; a non-match yields null.</summary>
    public MatchStage? Filter { get; init; }

    /// <summary>The raw filter, for a remote owner that binds it itself.</summary>
    [JsonIgnore]
    public JsonElement? RawFilter { get; init; }

    /// <summary>
    /// <c>"first"</c> or <c>"all"</c>: how a path that crosses one collection which is not unwound
    /// resolves, to the first element whose key resolves or to every resolved target. Contract 2.
    /// </summary>
    public string? Elements { get; init; }

    /// <summary>The one target entity of a typed or union reference to resolve to; the others are excluded. Contract 2.</summary>
    public string? Target { get; init; }

    /// <summary>The alias the owning row of an item target is placed under. Contract 2.</summary>
    public string? ParentAs { get; init; }

    /// <summary>Wire paths of the owning row to keep under <see cref="ParentAs"/>; its key and display members by default. Contract 2.</summary>
    public IReadOnlyList<string>? ParentSelect { get; init; }

    /// <summary><c>"null"</c>, <c>"report"</c> or <c>"refuse"</c>: what a reference that names nothing does. Contract 2.</summary>
    public string? OnMissing { get; init; }

    /// <summary>The one target of a union alias the stage belongs to, on a stage continued under it. Contract 2.</summary>
    public string? ForTarget { get; init; }

    /// <summary>Members the caller wrote with a value of the wrong JSON kind or outside their values, such as an <c>elements</c> of <c>"some"</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Malformed { get; init; } = [];

    /// <summary>Member names the caller wrote that the stage does not have: the v1 <c>source</c>, <c>localPath</c> among them.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Unknown { get; init; } = [];
}

internal sealed class LookupStageConverter : JsonConverter<LookupStage>
{
    public override LookupStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var stage = new LookupStage();
        var unknown = new List<string>();
        var malformed = new List<string>();

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "from": stage = stage with { From = property.Value.GetString() }; break;
                case "path": stage = stage with { Path = property.Value.GetString() }; break;
                case "as": stage = stage with { As = property.Value.GetString() }; break;
                case "select": stage = stage with { Select = StageJson.ReadStrings(property.Value) }; break;
                case "filter": stage = stage with { Filter = JsonSerializer.Deserialize<MatchStage>(property.Value.GetRawText(), options) }; break;
                case "limit": stage = stage with { Limit = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt32() : null }; break;
                case "sort":
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        stage = stage with { Sort = JsonSerializer.Deserialize<IReadOnlyList<SortField>>(property.Value.GetRawText(), options) };
                    else
                        malformed.Add(property.Name);
                    break;
                case "first":
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        stage = stage with { First = property.Value.GetBoolean() };
                    else
                        malformed.Add(property.Name);
                    break;
                case "on":
                    if (property.Value.ValueKind == JsonValueKind.String)
                        stage = stage with { On = property.Value.GetString() };
                    else
                        malformed.Add(property.Name);
                    break;
                case "forTarget":
                    if (property.Value.ValueKind == JsonValueKind.String)
                        stage = stage with { ForTarget = property.Value.GetString() };
                    else
                        malformed.Add(property.Name);
                    break;
                default: unknown.Add(property.Name); break;
            }
        }

        return stage with { Unknown = unknown, Malformed = malformed };
    }

    public override void Write(Utf8JsonWriter writer, LookupStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.From is not null) writer.WriteString("from", value.From);
        if (value.Path is not null) writer.WriteString("path", value.Path);
        if (value.As is not null) writer.WriteString("as", value.As);
        if (value.Select is not null) { writer.WritePropertyName("select"); JsonSerializer.Serialize(writer, value.Select, options); }
        if (value.Filter is not null) { writer.WritePropertyName("filter"); JsonSerializer.Serialize(writer, value.Filter, options); }
        if (value.Limit is not null) writer.WriteNumber("limit", value.Limit.Value);
        if (value.Sort is not null) { writer.WritePropertyName("sort"); JsonSerializer.Serialize(writer, value.Sort, options); }
        if (value.First is not null) writer.WriteBoolean("first", value.First.Value);
        if (value.On is not null) writer.WriteString("on", value.On);
        if (value.ForTarget is not null) writer.WriteString("forTarget", value.ForTarget);
        writer.WriteEndObject();
    }
}

internal sealed class ResolveStageConverter : JsonConverter<ResolveStage>
{
    public override ResolveStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var stage = new ResolveStage();
        var unknown = new List<string>();
        var malformed = new List<string>();

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "path": stage = stage with { Path = property.Value.GetString() }; break;
                case "as": stage = stage with { As = property.Value.GetString() }; break;
                case "select": stage = stage with { Select = StageJson.ReadStrings(property.Value) }; break;
                case "filter":
                    stage = stage with
                    {
                        Filter = JsonSerializer.Deserialize<MatchStage>(property.Value.GetRawText(), options),
                        RawFilter = property.Value.Clone(),
                    };
                    break;
                case "elements":
                    if (StageJson.OneOf(property.Value, "first", "all") is { } elements)
                        stage = stage with { Elements = elements };
                    else
                        malformed.Add(property.Name);
                    break;
                case "onMissing":
                    if (StageJson.OneOf(property.Value, "null", "report", "refuse") is { } onMissing)
                        stage = stage with { OnMissing = onMissing };
                    else
                        malformed.Add(property.Name);
                    break;
                case "target":
                    if (property.Value.ValueKind == JsonValueKind.String)
                        stage = stage with { Target = property.Value.GetString() };
                    else
                        malformed.Add(property.Name);
                    break;
                case "parentAs":
                    if (property.Value.ValueKind == JsonValueKind.String)
                        stage = stage with { ParentAs = property.Value.GetString() };
                    else
                        malformed.Add(property.Name);
                    break;
                case "parentSelect":
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array)
                        stage = stage with { ParentSelect = StageJson.ReadStrings(property.Value) };
                    else
                        malformed.Add(property.Name);
                    break;
                case "forTarget":
                    if (property.Value.ValueKind == JsonValueKind.String)
                        stage = stage with { ForTarget = property.Value.GetString() };
                    else
                        malformed.Add(property.Name);
                    break;
                default: unknown.Add(property.Name); break;
            }
        }

        return stage with { Unknown = unknown, Malformed = malformed };
    }

    public override void Write(Utf8JsonWriter writer, ResolveStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Path is not null) writer.WriteString("path", value.Path);
        if (value.As is not null) writer.WriteString("as", value.As);
        if (value.Select is not null) { writer.WritePropertyName("select"); JsonSerializer.Serialize(writer, value.Select, options); }
        if (value.Filter is not null) { writer.WritePropertyName("filter"); JsonSerializer.Serialize(writer, value.Filter, options); }
        if (value.Elements is not null) writer.WriteString("elements", value.Elements);
        if (value.Target is not null) writer.WriteString("target", value.Target);
        if (value.ParentAs is not null) writer.WriteString("parentAs", value.ParentAs);
        if (value.ParentSelect is not null) { writer.WritePropertyName("parentSelect"); JsonSerializer.Serialize(writer, value.ParentSelect, options); }
        if (value.OnMissing is not null) writer.WriteString("onMissing", value.OnMissing);
        if (value.ForTarget is not null) writer.WriteString("forTarget", value.ForTarget);
        writer.WriteEndObject();
    }
}

internal static class StageJson
{
    public static IReadOnlyList<string> ReadStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return [element.GetString() ?? ""];

        if (element.ValueKind != JsonValueKind.Array)
            return [];

        return element.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText()).ToList();
    }

    /// <summary>The string when the element is one of <paramref name="values"/>, compared exactly; null otherwise.</summary>
    public static string? OneOf(JsonElement element, params string[] values) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { } text && values.Contains(text, StringComparer.Ordinal) ? text : null;
}
