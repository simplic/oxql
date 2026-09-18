using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// A match stage: one condition, which may be a logical group. The wire form
/// <c>{ "a": { "eq": 1 }, "b": { "gt": 2, "lt": 5 } }</c> is an <c>and</c> of every path
/// and every operator; <c>and</c>, <c>or</c>, <c>not</c> nest freely; <c>{}</c> matches everything.
/// </summary>
[JsonConverter(typeof(MatchStageConverter))]
public sealed record MatchStage
{
    /// <summary>The condition, or null for match-all.</summary>
    public FilterCondition? Condition { get; init; }

    /// <summary>Logical AND conditions (the condition when it is an <c>and</c> group).</summary>
    [JsonIgnore]
    public IReadOnlyList<FilterCondition>? And => Condition?.And;

    /// <summary>Logical OR conditions (the condition when it is an <c>or</c> group).</summary>
    [JsonIgnore]
    public IReadOnlyList<FilterCondition>? Or => Condition?.Or;

    /// <summary>Logical NOT (the condition when it is a <c>not</c> group).</summary>
    [JsonIgnore]
    public FilterCondition? Not => Condition?.Not;

    /// <summary>Returns <c>true</c> when this stage carries no condition (match everything).</summary>
    [JsonIgnore]
    public bool IsMatchAll => Condition is null;
}

/// <summary>Options that modify a condition.</summary>
public sealed record FilterConditionOptions
{
    /// <summary>Case-insensitive comparison on string members, for <c>eq neq in nin contains startsWith endsWith</c>.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>The option names the caller wrote that the engine does not know.</summary>
    public IReadOnlyList<string>? Unknown { get; init; }
}

/// <summary>
/// One condition: a field condition (<c>Path</c>, <c>Op</c>, <c>Value</c>), an <c>any</c>
/// correlation (<c>Path</c>, <c>Any</c>), or a logical group (<c>And</c>, <c>Or</c>, <c>Not</c>).
/// </summary>
[JsonConverter(typeof(FilterConditionConverter))]
public sealed record FilterCondition
{
    /// <summary>The wire path of a field condition or an <c>any</c> condition.</summary>
    public string? Path { get; init; }

    /// <summary>The operator, as written; matched case-sensitively by the binder.</summary>
    public string? Op { get; init; }

    /// <summary>The operand, as written.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>The options, when any.</summary>
    public FilterConditionOptions? Options { get; init; }

    /// <summary>The inner condition of an <c>any</c>: evaluated against one element of the collection at <see cref="Path"/>.</summary>
    public FilterCondition? Any { get; init; }

    /// <summary>The operands of an <c>and</c> group.</summary>
    public IReadOnlyList<FilterCondition>? And { get; init; }

    /// <summary>The operands of an <c>or</c> group.</summary>
    public IReadOnlyList<FilterCondition>? Or { get; init; }

    /// <summary>The operand of a <c>not</c> group.</summary>
    public FilterCondition? Not { get; init; }

    /// <summary>True for a logical group.</summary>
    [JsonIgnore]
    public bool IsLogical => And is not null || Or is not null || Not is not null;

    /// <summary>True for an <c>any</c> condition.</summary>
    [JsonIgnore]
    public bool IsAny => Any is not null;

    /// <summary>
    /// True for a group the caller wrote with no conditions in it. The binder refuses it and
    /// names the spelling that is there: an empty <c>not</c> reads as <c>not</c>.
    /// </summary>
    [JsonIgnore]
    public bool IsEmptyGroup => (And is { Count: 0 }) || (Or is { Count: 0 }) || (Not is not null && ReferenceEquals(Not, EmptyGroup));

    /// <summary>The sentinel an empty <c>not</c> carries, so the refusal can name <c>not</c>.</summary>
    internal static readonly FilterCondition EmptyGroup = new() { And = [] };
}

/// <summary>
/// Reads the compact wire form. Every non-logical property is a path; inside it every key is
/// an operator (or <c>options</c>, or <c>any</c>); several become one <c>and</c>. A bare value
/// under a path is an implicit <c>eq</c>. Nothing is validated here: unknown operators and
/// options are carried through for the binder to refuse with a code.
/// </summary>
internal sealed class FilterConditionConverter : JsonConverter<FilterCondition>
{
    private const string AndKey = "and";
    private const string OrKey = "or";
    private const string NotKey = "not";
    private const string OptionsKey = "options";
    private const string AnyKey = "any";

    public override FilterCondition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return ReadFromElement(doc.RootElement, options);
    }

    /// <summary>Reads a condition object; null for an empty object.</summary>
    internal static FilterCondition? ReadFromElement(JsonElement root, JsonSerializerOptions options)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A condition is a JSON object.");

        var parts = new List<FilterCondition>();

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case AndKey:
                    parts.Add(new FilterCondition { And = ReadGroup(property.Value, options) });
                    break;

                case OrKey:
                    parts.Add(new FilterCondition { Or = ReadGroup(property.Value, options) });
                    break;

                case NotKey:
                    parts.Add(new FilterCondition
                    {
                        // An empty `not` is kept as an empty `not`, not as a substituted `and`:
                        // the binder reports the spelling it refuses, and reporting "'and' has
                        // no conditions" sent the caller looking for an `and` they never wrote.
                        Not = property.Value.ValueKind == JsonValueKind.Object
                            ? ReadFromElement(property.Value, options) ?? new FilterCondition { Not = FilterCondition.EmptyGroup }
                            : throw new JsonException("'not' takes a condition object."),
                    });
                    break;

                default:
                    parts.AddRange(ReadFieldConditions(property.Name, property.Value, options));
                    break;
            }
        }

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new FilterCondition { And = parts },
        };
    }

    private static List<FilterCondition> ReadGroup(JsonElement element, JsonSerializerOptions options)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException("'and' and 'or' take an array of conditions.");

        var group = new List<FilterCondition>();

        foreach (var item in element.EnumerateArray())
        {
            var condition = ReadFromElement(item, options);

            // An empty object inside a group is kept as an empty group so the binder can refuse it.
            group.Add(condition ?? new FilterCondition { And = [] });
        }

        return group;
    }

    /// <summary>The conditions one path carries: one per operator key, plus an <c>any</c> when present.</summary>
    private static IEnumerable<FilterCondition> ReadFieldConditions(string path, JsonElement operand, JsonSerializerOptions options)
    {
        if (operand.ValueKind != JsonValueKind.Object)
        {
            yield return new FilterCondition { Path = path, Op = "eq", Value = operand.Clone() };
            yield break;
        }

        FilterConditionOptions? conditionOptions = null;
        var operators = new List<(string Op, JsonElement Value)>();
        FilterCondition? any = null;

        foreach (var property in operand.EnumerateObject())
        {
            if (property.Name == OptionsKey)
            {
                conditionOptions = ReadOptions(property.Value);
                continue;
            }

            if (property.Name == AnyKey && property.Value.ValueKind == JsonValueKind.Object)
            {
                any = ReadFromElement(property.Value, options) ?? new FilterCondition { And = [] };
                continue;
            }

            operators.Add((property.Name, property.Value.Clone()));
        }

        // A bare operand object with no operator key is an implicit eq on the object; the
        // binder refuses it as an invalid operand unless it is a variable wrapper.
        if (operators.Count == 0 && any is null)
        {
            yield return new FilterCondition { Path = path, Op = "eq", Value = operand.Clone(), Options = conditionOptions };
            yield break;
        }

        foreach (var (op, value) in operators)
            yield return new FilterCondition { Path = path, Op = op, Value = value, Options = conditionOptions };

        if (any is not null)
            yield return new FilterCondition { Path = path, Any = any, Options = conditionOptions };
    }

    private static FilterConditionOptions ReadOptions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new FilterConditionOptions { Unknown = ["options"] };

        var ignoreCase = false;
        List<string>? unknown = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "ignoreCase" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ignoreCase = property.Value.ValueKind == JsonValueKind.True;
            else
                (unknown ??= []).Add(property.Name);
        }

        return new FilterConditionOptions { IgnoreCase = ignoreCase, Unknown = unknown };
    }

    public override void Write(Utf8JsonWriter writer, FilterCondition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteBody(writer, value, options);
        writer.WriteEndObject();
    }

    internal static void WriteBody(Utf8JsonWriter writer, FilterCondition value, JsonSerializerOptions options)
    {
        if (value.And is not null)
        {
            writer.WritePropertyName(AndKey);
            WriteGroup(writer, value.And, options);
        }
        else if (value.Or is not null)
        {
            writer.WritePropertyName(OrKey);
            WriteGroup(writer, value.Or, options);
        }
        else if (value.Not is not null)
        {
            writer.WritePropertyName(NotKey);
            JsonSerializer.Serialize(writer, value.Not, options);
        }
        else if (value.Path is not null)
        {
            writer.WritePropertyName(value.Path);
            writer.WriteStartObject();

            if (value.Any is not null)
            {
                writer.WritePropertyName(AnyKey);
                JsonSerializer.Serialize(writer, value.Any, options);
            }
            else if (value.Op is not null)
            {
                writer.WritePropertyName(value.Op);

                if (value.Value.HasValue)
                    value.Value.Value.WriteTo(writer);
                else
                    writer.WriteNullValue();
            }

            if (value.Options?.IgnoreCase == true)
            {
                writer.WritePropertyName(OptionsKey);
                writer.WriteStartObject();
                writer.WriteBoolean("ignoreCase", true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }
    }

    private static void WriteGroup(Utf8JsonWriter writer, IReadOnlyList<FilterCondition> group, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var condition in group)
            JsonSerializer.Serialize(writer, condition, options);

        writer.WriteEndArray();
    }
}

internal sealed class MatchStageConverter : JsonConverter<MatchStage>
{
    public override MatchStage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);

        return new MatchStage { Condition = FilterConditionConverter.ReadFromElement(doc.RootElement, options) };
    }

    public override void Write(Utf8JsonWriter writer, MatchStage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.Condition is not null)
            FilterConditionConverter.WriteBody(writer, value.Condition, options);

        writer.WriteEndObject();
    }
}
