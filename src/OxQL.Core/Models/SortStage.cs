using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a single sort field with path and direction.
/// <para>
/// Wire format: <c>{ "My.Field": "asc" }</c>, <c>{ "My.Field": "desc" }</c>, or the object
/// form <c>{ "My.Field": { "direction": "asc", "caseSensitive": true } }</c> for a string member
/// that is to order by its exact value rather than folding case.
/// </para>
/// </summary>
[JsonConverter(typeof(SortFieldConverter))]
public sealed record SortField
{
    public required string Path { get; init; }
    public required string Direction { get; init; }

    /// <summary>Whether a string member orders by its exact value; null when the entry does not say.</summary>
    public bool? CaseSensitive { get; init; }

    /// <summary>The members of the object form the engine does not know, for the binder to refuse.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>
    /// The keys of the entry beyond the first. A sort entry is a single-key object, so
    /// <c>{"a":"asc","b":"desc"}</c> is carried through for the binder to refuse rather than
    /// ordered by <c>a</c> alone.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Extra { get; init; } = [];
}

/// <summary>
/// Converts <see cref="SortField"/> to/from the compact wire format
/// <c>{ "Field.Path": "asc" }</c> and its object form.
/// </summary>
internal sealed class SortFieldConverter : JsonConverter<SortField>
{
    private const string DirectionKey = "direction";
    private const string CaseSensitiveKey = "caseSensitive";

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
                first = prop.Value.ValueKind == JsonValueKind.Object
                    ? ReadObjectForm(prop.Name, prop.Value)
                    : new SortField { Path = prop.Name, Direction = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : prop.Value.ToString() };
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

    /// <summary>The object form: a direction and, optionally, <c>caseSensitive</c>; anything else is recorded for the binder.</summary>
    private static SortField ReadObjectForm(string path, JsonElement value)
    {
        var direction = "";
        bool? caseSensitive = null;
        var unknown = new List<string>();

        foreach (var member in value.EnumerateObject())
        {
            if (member.Name == DirectionKey && member.Value.ValueKind == JsonValueKind.String)
                direction = member.Value.GetString()!;
            else if (member.Name == CaseSensitiveKey && member.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                caseSensitive = member.Value.ValueKind == JsonValueKind.True;
            else
                unknown.Add(member.Name);
        }

        return new SortField { Path = path, Direction = direction, CaseSensitive = caseSensitive, Unknown = unknown };
    }

    public override void Write(Utf8JsonWriter writer, SortField value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.CaseSensitive is { } caseSensitive)
        {
            writer.WritePropertyName(value.Path);
            writer.WriteStartObject();
            writer.WriteString(DirectionKey, value.Direction);
            writer.WriteBoolean(CaseSensitiveKey, caseSensitive);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteString(value.Path, value.Direction);
        }

        writer.WriteEndObject();
    }
}
