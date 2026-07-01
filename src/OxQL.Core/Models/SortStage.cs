using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a sort stage containing multiple sort fields.
/// </summary>
public sealed record SortStage
{
    [JsonPropertyName("sort")]
    public required IReadOnlyList<SortField> Fields { get; init; }
}

/// <summary>
/// Represents a single sort field with path and direction.
/// <para>
/// Wire format: <c>{ "My.Field": "asc" }</c> or <c>{ "My.Field": "desc" }</c>
/// </para>
/// </summary>
[JsonConverter(typeof(SortFieldConverter))]
public sealed record SortField
{
    public required string Path { get; init; }
    public required string Direction { get; init; }

    [JsonIgnore]
    public bool IsAscending => string.Equals(Direction, "asc", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsDescending => string.Equals(Direction, "desc", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Converts <see cref="SortField"/> to/from the compact wire format
/// <c>{ "Field.Path": "asc" }</c>.
/// </summary>
internal sealed class SortFieldConverter : JsonConverter<SortField>
{
    public override SortField? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        foreach (var prop in root.EnumerateObject())
        {
            return new SortField
            {
                Path = prop.Name,
                Direction = prop.Value.GetString() ?? "asc"
            };
        }

        throw new JsonException("SortField object must have exactly one property: { \"fieldPath\": \"asc\" | \"desc\" }");
    }

    public override void Write(Utf8JsonWriter writer, SortField value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(value.Path, value.Direction);
        writer.WriteEndObject();
    }
}
