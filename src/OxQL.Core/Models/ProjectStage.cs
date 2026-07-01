using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a projection stage for selecting (or excluding) output fields.
/// <para>
/// Flat syntax:   <c>{ "id": 1, "MatchCode": 1 }</c><br/>
/// Nested syntax: <c>{ "RegistrationPlate": { "RegistrationIdentifier": 1 } }</c>
///   — nested objects are flattened to dot-notation paths internally.<br/>
/// Arrays: include the whole array with <c>"Appointments": 1</c>,
///   or project a sub-field from every element with <c>"Appointments": { "NextDate": 1 }</c>.
/// </para>
/// </summary>
[JsonConverter(typeof(ProjectStageConverter))]
public sealed record ProjectStage
{
    /// <summary>
    /// Flat dot-notation path → projection value. <c>1</c> = include, <c>0</c> = exclude.
    /// Nested input is flattened here during deserialization.
    /// </summary>
    public required IReadOnlyDictionary<string, int> Fields { get; init; }

    /// <summary>
    /// Convenience list of field paths whose projection value is <c>1</c> (included).
    /// Used by downstream builders and validators — no JSON mapping needed.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Include =>
        Fields.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
}

/// <summary>
/// Converts <see cref="ProjectStage"/> to/from the projection wire format.
/// Accepts both flat and nested input; always writes flat dot-notation.
/// </summary>
internal sealed class ProjectStageConverter : JsonConverter<ProjectStage>
{
    public override ProjectStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        Flatten(doc.RootElement, prefix: null, fields);
        return new ProjectStage { Fields = fields };
    }

    /// <summary>
    /// Recursively walks a JSON object and emits flat dot-notation entries.
    /// </summary>
    private static void Flatten(JsonElement element, string? prefix, Dictionary<string, int> fields)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var path = prefix is null ? prop.Name : $"{prefix}.{prop.Name}";

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    // Nested object → recurse (e.g. "RegistrationPlate": { "RegistrationIdentifier": 1 })
                    Flatten(prop.Value, path, fields);
                    break;

                case JsonValueKind.Number:
                    fields[path] = prop.Value.GetInt32();
                    break;

                default:
                    fields[path] = 1; // treat any non-numeric leaf as include
                    break;
            }
        }
    }

    public override void Write(Utf8JsonWriter writer, ProjectStage value, JsonSerializerOptions options)
    {
        // Always write flat dot-notation — unambiguous and works with all downstream tools
        writer.WriteStartObject();
        foreach (var (path, projection) in value.Fields)
            writer.WriteNumber(path, projection);
        writer.WriteEndObject();
    }
}
