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

    /// <summary>
    /// The keys of the entry beyond the first. A sort entry is a single-key object; the
    /// converter used to return the first key and drop the rest, so
    /// <c>{"a":"asc","b":"desc"}</c> ordered by <c>a</c> alone with a 200 and no diagnostic —
    /// while the converter's own error message calls that shape invalid.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Extra { get; init; } = [];

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

        if (root.ValueKind != JsonValueKind.Object)
            return new SortField { Path = "", Direction = "", Extra = [root.ValueKind == JsonValueKind.Null ? "null" : root.ToString()] };

        SortField? first = null;
        var extra = new List<string>();

        foreach (var prop in root.EnumerateObject())
        {
            if (first is null)
            {
                first = new SortField { Path = prop.Name, Direction = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : prop.Value.ToString() };
                continue;
            }

            extra.Add(prop.Name);
        }

        // An empty entry and an over-full one are both refused by the binder with a code;
        // throwing here would leave the caller ProblemDetails instead of a refusal envelope.
        return first is null
            ? new SortField { Path = "", Direction = "" }
            : first with { Extra = extra };
    }

    public override void Write(Utf8JsonWriter writer, SortField value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(value.Path, value.Direction);
        writer.WriteEndObject();
    }
}
