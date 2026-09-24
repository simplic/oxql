using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// A backward join along a declared reference: <c>from</c> is the child entity, <c>path</c>
/// the child's member that references the current entity, the result an array under
/// <c>as</c> ordered by the child's key.
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
                default: unknown.Add(property.Name); break;
            }
        }

        return stage with { Unknown = unknown };
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
                default: unknown.Add(property.Name); break;
            }
        }

        return stage with { Unknown = unknown };
    }

    public override void Write(Utf8JsonWriter writer, ResolveStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Path is not null) writer.WriteString("path", value.Path);
        if (value.As is not null) writer.WriteString("as", value.As);
        if (value.Select is not null) { writer.WritePropertyName("select"); JsonSerializer.Serialize(writer, value.Select, options); }
        if (value.Filter is not null) { writer.WritePropertyName("filter"); JsonSerializer.Serialize(writer, value.Filter, options); }
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
}
