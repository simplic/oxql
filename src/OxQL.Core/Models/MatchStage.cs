using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a match/filter stage in the pipeline.
/// </summary>
[JsonConverter(typeof(MatchStageConverter))]
public sealed record MatchStage
{
    /// <summary>
    /// Logical AND conditions.
    /// </summary>
    public IReadOnlyList<FilterCondition>? And { get; init; }

    /// <summary>
    /// Logical OR conditions.
    /// </summary>
    public IReadOnlyList<FilterCondition>? Or { get; init; }

    /// <summary>
    /// Logical NOT condition.
    /// </summary>
    public FilterCondition? Not { get; init; }

    /// <summary>
    /// A single filter condition (when no logical wrapper is used).
    /// </summary>
    public FilterCondition? Condition { get; init; }

    /// <summary>
    /// Returns <c>true</c> when this stage carries no filter conditions (match everything).
    /// </summary>
    [JsonIgnore]
    public bool IsMatchAll => And is null && Or is null && Not is null && Condition is null;
}

/// <summary>
/// Represents a single filter condition or a nested logical group.
/// <para>
/// Wire format for a field condition:
/// <c>{ "My.Field": { "eq": "value" } }</c>
/// </para>
/// <para>
/// Wire format for logical groups:
/// <c>{ "and": [ ... ] }</c>, <c>{ "or": [ ... ] }</c>, <c>{ "not": { ... } }</c>
/// </para>
/// </summary>
[JsonConverter(typeof(FilterConditionConverter))]
public sealed record FilterCondition
{
    public string? Path { get; init; }
    public string? Op { get; init; }
    public JsonElement? Value { get; init; }

    public IReadOnlyList<FilterCondition>? And { get; init; }
    public IReadOnlyList<FilterCondition>? Or { get; init; }
    public FilterCondition? Not { get; init; }

    /// <summary>
    /// Returns true if this is a logical group rather than a field condition.
    /// </summary>
    [JsonIgnore]
    public bool IsLogical => And is not null || Or is not null || Not is not null;
}

/// <summary>
/// Converts <see cref="FilterCondition"/> between the compact wire format
/// <c>{ "Field.Path": { "op": value } }</c> and the internal model.
/// </summary>
internal sealed class FilterConditionConverter : JsonConverter<FilterCondition>
{
    private static readonly HashSet<string> LogicalKeys =
        new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not" };

    public override FilterCondition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return ReadFromElement(doc.RootElement, options);
    }

    internal static FilterCondition ReadFromElement(JsonElement root, JsonSerializerOptions options)
    {
        // Logical group: { "and": [...] } / { "or": [...] } / { "not": {...} }
        if (root.TryGetProperty("and", out var andEl))
        {
            var conditions = andEl.EnumerateArray()
                .Select(e => ReadFromElement(e, options))
                .ToList();
            return new FilterCondition { And = conditions };
        }

        if (root.TryGetProperty("or", out var orEl))
        {
            var conditions = orEl.EnumerateArray()
                .Select(e => ReadFromElement(e, options))
                .ToList();
            return new FilterCondition { Or = conditions };
        }

        if (root.TryGetProperty("not", out var notEl))
        {
            return new FilterCondition { Not = ReadFromElement(notEl, options) };
        }

        // Field condition: { "My.Field": { "op": value } }
        // Find the first property that is not a logical key — that is the field path.
        foreach (var prop in root.EnumerateObject())
        {
            if (LogicalKeys.Contains(prop.Name))
                continue;

            var path = prop.Name;
            var opObject = prop.Value;

            // The value of the property is a one-key object: { "op": value }
            if (opObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var opProp in opObject.EnumerateObject())
                {
                    return new FilterCondition
                    {
                        Path = path,
                        Op = opProp.Name,
                        Value = opProp.Value.Clone()
                    };
                }
            }

            // Fallback: bare value with implicit "eq"
            return new FilterCondition
            {
                Path = path,
                Op = "eq",
                Value = opObject.Clone()
            };
        }

        return new FilterCondition();
    }

    public override void Write(Utf8JsonWriter writer, FilterCondition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.And is not null)
        {
            writer.WritePropertyName("and");
            writer.WriteStartArray();
            foreach (var c in value.And)
                JsonSerializer.Serialize(writer, c, options);
            writer.WriteEndArray();
        }
        else if (value.Or is not null)
        {
            writer.WritePropertyName("or");
            writer.WriteStartArray();
            foreach (var c in value.Or)
                JsonSerializer.Serialize(writer, c, options);
            writer.WriteEndArray();
        }
        else if (value.Not is not null)
        {
            writer.WritePropertyName("not");
            JsonSerializer.Serialize(writer, value.Not, options);
        }
        else if (value.Path is not null && value.Op is not null)
        {
            writer.WritePropertyName(value.Path);
            writer.WriteStartObject();
            writer.WritePropertyName(value.Op);
            if (value.Value.HasValue)
                value.Value.Value.WriteTo(writer);
            else
                writer.WriteNullValue();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}

internal sealed class MatchStageConverter : JsonConverter<MatchStage>
{
    public override MatchStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("and", out var andEl))
        {
            var conditions = andEl.EnumerateArray()
                .Select(e => FilterConditionConverter.ReadFromElement(e, options))
                .ToList();
            return new MatchStage { And = conditions };
        }

        if (root.TryGetProperty("or", out var orEl))
        {
            var conditions = orEl.EnumerateArray()
                .Select(e => FilterConditionConverter.ReadFromElement(e, options))
                .ToList();
            return new MatchStage { Or = conditions };
        }

        if (root.TryGetProperty("not", out var notEl))
        {
            return new MatchStage { Not = FilterConditionConverter.ReadFromElement(notEl, options) };
        }

        // Empty object {} means "match everything" — all properties remain null.
        if (!root.EnumerateObject().Any())
            return new MatchStage();

        // Single field condition
        return new MatchStage { Condition = FilterConditionConverter.ReadFromElement(root, options) };
    }

    public override void Write(Utf8JsonWriter writer, MatchStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.And is not null)
        {
            writer.WritePropertyName("and");
            writer.WriteStartArray();
            foreach (var c in value.And)
                JsonSerializer.Serialize(writer, c, options);
            writer.WriteEndArray();
        }
        else if (value.Or is not null)
        {
            writer.WritePropertyName("or");
            writer.WriteStartArray();
            foreach (var c in value.Or)
                JsonSerializer.Serialize(writer, c, options);
            writer.WriteEndArray();
        }
        else if (value.Not is not null)
        {
            writer.WritePropertyName("not");
            JsonSerializer.Serialize(writer, value.Not, options);
        }
        else if (value.Condition is not null)
        {
            // Inline the single condition's fields directly into this object
            JsonSerializer.Serialize(writer, value.Condition, options);
        }

        writer.WriteEndObject();
    }
}
