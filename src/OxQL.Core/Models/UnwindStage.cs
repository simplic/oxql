using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents an unwind stage that deconstructs an array field.
/// </summary>
[JsonConverter(typeof(UnwindStageConverter))]
public sealed record UnwindStage
{
    /// <summary>
    /// The array field path to unwind.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// The alias for each unwound element.
    /// </summary>
    [JsonPropertyName("as")]
    public string? As { get; init; }

    /// <summary>
    /// Whether to preserve documents where the array is null or empty.
    /// </summary>
    [JsonPropertyName("preserveNull")]
    public bool PreserveNull { get; init; }

    /// <summary>
    /// Optional field name to include the array index of the unwound element.
    /// </summary>
    [JsonPropertyName("includeIndex")]
    public string? IncludeIndex { get; init; }

    /// <summary>
    /// A member of the element that nests the same kind of element (a group item's items): the
    /// unwind then yields one row per element and per descendant, in pre-order, each without
    /// its nested collection, down to the host's <c>MaxFlattenDepth</c>. Contract 2 only.
    /// </summary>
    [JsonPropertyName("flatten")]
    public string? Flatten { get; init; }

    /// <summary>
    /// Whether the row keeps the unwound collection beside the alias (the default).
    /// <c>false</c> takes it out of the row, so the element is only under <c>as</c>; it needs
    /// <c>as</c> and a member collection. Contract 2 only.
    /// </summary>
    [JsonPropertyName("keepPath")]
    public bool KeepPath { get; init; } = true;

    /// <summary>Whether the caller wrote <c>keepPath</c> (contract 1 does not have it).</summary>
    [JsonIgnore]
    public bool KeepPathWritten { get; init; }

    /// <summary>Whether the caller wrote <c>keepPath</c> as something other than <c>true</c> or <c>false</c>; the binder refuses it.</summary>
    [JsonIgnore]
    public bool KeepPathInvalid { get; init; }

    /// <summary>
    /// Member names the caller wrote that the stage does not have. System.Text.Json skips an
    /// unmapped member by default, so they are recorded here and the binder refuses them:
    /// <c>preserveNulls</c> for <c>preserveNull</c> is an error, not the default applied.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Unknown { get; init; } = [];
}

internal sealed class UnwindStageConverter : JsonConverter<UnwindStage>
{
    public override UnwindStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var stage = new UnwindStage();
        var unknown = new List<string>();

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return stage;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "path": stage = stage with { Path = property.Value.GetString() }; break;
                case "as": stage = stage with { As = property.Value.GetString() }; break;
                case "preserveNull": stage = stage with { PreserveNull = property.Value.ValueKind == JsonValueKind.True }; break;
                case "includeIndex": stage = stage with { IncludeIndex = property.Value.GetString() }; break;
                // "flatten": null is the member left out, not a member named "null".
                case "flatten": stage = stage with { Flatten = property.Value.ValueKind switch { JsonValueKind.String => property.Value.GetString(), JsonValueKind.Null => null, _ => property.Value.GetRawText() } }; break;
                case "keepPath": stage = property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? stage with { KeepPath = property.Value.ValueKind == JsonValueKind.True, KeepPathWritten = true }
                    : stage with { KeepPathWritten = true, KeepPathInvalid = true }; break;
                default: unknown.Add(property.Name); break;
            }
        }

        return stage with { Unknown = unknown };
    }

    public override void Write(Utf8JsonWriter writer, UnwindStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Path is not null) writer.WriteString("path", value.Path);
        if (value.As is not null) writer.WriteString("as", value.As);
        if (value.PreserveNull) writer.WriteBoolean("preserveNull", true);
        if (value.IncludeIndex is not null) writer.WriteString("includeIndex", value.IncludeIndex);
        if (value.Flatten is not null) writer.WriteString("flatten", value.Flatten);
        if (!value.KeepPath) writer.WriteBoolean("keepPath", false);
        writer.WriteEndObject();
    }
}
