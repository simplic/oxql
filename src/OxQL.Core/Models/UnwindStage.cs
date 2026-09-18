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
    /// Member names the caller wrote that the stage does not have. System.Text.Json skips an
    /// unmapped member by default, so <c>preserveNulls</c> for <c>preserveNull</c> bound with
    /// the member dropped and the default applied, silently.
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
        writer.WriteEndObject();
    }
}
