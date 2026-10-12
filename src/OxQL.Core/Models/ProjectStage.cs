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
    /// Flat dot-notation path → projection value. <c>1</c> = include, <c>0</c> = exclude;
    /// <c>true</c> and <c>false</c> read as 1 and 0. Nested input is flattened here during deserialization.
    /// </summary>
    public required IReadOnlyDictionary<string, int> Fields { get; init; }

    /// <summary>The paths written with <c>false</c>, which contract 1 reads as included (<see cref="AsContract1"/>).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> WrittenFalse { get; init; } = [];

    /// <summary>
    /// The paths written with a value that is neither a number nor a boolean. They read as included,
    /// and contract 2 refuses them.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Invalid { get; init; } = [];

    /// <summary>The stage as contract 1 reads it: every path whose value is no number is included.</summary>
    public ProjectStage AsContract1()
    {
        if (WrittenFalse.Count == 0)
            return this;

        var fields = new Dictionary<string, int>(Fields, StringComparer.Ordinal);

        foreach (var path in WrittenFalse)
            fields[path] = 1;

        return this with { Fields = fields, WrittenFalse = [] };
    }
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
        var writtenFalse = new List<string>();
        var invalid = new List<string>();
        Flatten(doc.RootElement, prefix: null, fields, writtenFalse, invalid);
        return new ProjectStage { Fields = fields, WrittenFalse = writtenFalse, Invalid = invalid };
    }

    /// <summary>
    /// Recursively walks a JSON object and emits flat dot-notation entries.
    /// </summary>
    private static void Flatten(JsonElement element, string? prefix, Dictionary<string, int> fields, List<string> writtenFalse, List<string> invalid)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var path = prefix is null ? prop.Name : $"{prefix}.{prop.Name}";

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    // Nested object → recurse (e.g. "RegistrationPlate": { "RegistrationIdentifier": 1 })
                    Flatten(prop.Value, path, fields, writtenFalse, invalid);
                    break;

                case JsonValueKind.Number:
                    fields[path] = prop.Value.GetInt32();
                    break;

                case JsonValueKind.True:
                    fields[path] = 1;
                    break;

                case JsonValueKind.False:
                    fields[path] = 0;
                    writtenFalse.Add(path);
                    break;

                default:
                    fields[path] = 1;
                    invalid.Add(path);
                    break;
            }
        }
    }

    public override void Write(Utf8JsonWriter writer, ProjectStage value, JsonSerializerOptions options)
    {
        // Always write flat dot-notation — unambiguous and works with all downstream tools
        writer.WriteStartObject();
        foreach (var (path, projection) in value.Fields)
        {
            // A path written with false travels as written, so each contract reads it its own way.
            if (value.WrittenFalse.Contains(path))
                writer.WriteBoolean(path, false);
            else
                writer.WriteNumber(path, projection);
        }
        writer.WriteEndObject();
    }
}
