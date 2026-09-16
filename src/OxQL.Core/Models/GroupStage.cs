using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>A group stage: keys and aggregates, which replace the shape.</summary>
public sealed record GroupStage
{
    /// <summary>The keys.</summary>
    [JsonPropertyName("by")]
    public required IReadOnlyList<GroupByField> By { get; init; }

    /// <summary>The aggregates by alias.</summary>
    [JsonPropertyName("fields")]
    public required IReadOnlyDictionary<string, AggregationExpression> Fields { get; init; }
}

/// <summary>One group key: a scalar path, or a truncated date.</summary>
public sealed record GroupByField
{
    /// <summary>The wire path to group by.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>A date truncation to group by instead of a path.</summary>
    [JsonPropertyName("dateTrunc")]
    public DateTruncExpression? DateTrunc { get; init; }

    /// <summary>The output alias.</summary>
    [JsonPropertyName("as")]
    public required string As { get; init; }
}

/// <summary>A date truncation: the local boundary in <c>timezone</c>, emitted as the UTC instant.</summary>
public sealed record DateTruncExpression
{
    /// <summary>The wire path of the date.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The unit: <c>year quarter month week day hour minute second</c>.</summary>
    [JsonPropertyName("unit")]
    public required string Unit { get; init; }

    /// <summary>An IANA timezone; UTC by default.</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    /// <summary>The first day of the week for <c>unit: week</c>; Monday by default.</summary>
    [JsonPropertyName("weekStart")]
    public string? WeekStart { get; init; }
}

/// <summary>An aggregate: a function and its argument. The function name is carried as written for the binder to check.</summary>
[JsonConverter(typeof(AggregationExpressionConverter))]
public sealed record AggregationExpression
{
    /// <summary>The function, as written.</summary>
    public string? Function { get; init; }

    /// <summary>The argument; null for <c>count: true</c>.</summary>
    public QueryExpression? Argument { get; init; }

    /// <summary>True for <c>count: true</c>.</summary>
    public bool IsCount { get; init; }
}

/// <summary>An expression: a path, a literal, a variable, or arithmetic over expressions.</summary>
[JsonConverter(typeof(QueryExpressionConverter))]
public sealed record QueryExpression
{
    public string? Path { get; init; }
    public JsonElement? Literal { get; init; }
    public string? Var { get; init; }
    public string? Operator { get; init; }
    public IReadOnlyList<QueryExpression>? Operands { get; init; }

    [JsonIgnore]
    public bool IsPath => Path is not null;

    [JsonIgnore]
    public bool IsVar => Var is not null;

    [JsonIgnore]
    public bool IsLiteral => Literal is not null && !IsPath && !IsVar && Operator is null;

    [JsonIgnore]
    public bool IsArithmetic => Operator is not null;

    /// <summary>The arithmetic operators.</summary>
    public static readonly IReadOnlySet<string> ArithmeticOperators =
        new HashSet<string>(StringComparer.Ordinal) { "add", "subtract", "multiply", "divide", "coalesce" };
}

internal sealed class AggregationExpressionConverter : JsonConverter<AggregationExpression>
{
    public override AggregationExpression? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("An aggregate is an object of one function.");

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "count" && property.Value.ValueKind == JsonValueKind.True)
                return new AggregationExpression { Function = "count", IsCount = true };

            var argument = JsonSerializer.Deserialize<QueryExpression>(property.Value.GetRawText(), options);

            return new AggregationExpression { Function = property.Name, Argument = argument };
        }

        return new AggregationExpression();
    }

    public override void Write(Utf8JsonWriter writer, AggregationExpression value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.IsCount)
        {
            writer.WriteBoolean(value.Function ?? "count", true);
        }
        else if (value.Function is not null)
        {
            writer.WritePropertyName(value.Function);
            JsonSerializer.Serialize(writer, value.Argument, options);
        }

        writer.WriteEndObject();
    }
}

internal sealed class QueryExpressionConverter : JsonConverter<QueryExpression>
{
    public override QueryExpression? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new QueryExpression { Path = reader.GetString() };

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("path", out var pathElement))
                return new QueryExpression { Path = pathElement.GetString() };

            if (root.TryGetProperty("$var", out var varElement))
                return new QueryExpression { Var = varElement.GetString() };

            if (root.TryGetProperty("literal", out var literalElement))
                return new QueryExpression { Literal = literalElement.Clone() };

            foreach (var property in root.EnumerateObject())
            {
                if (!QueryExpression.ArithmeticOperators.Contains(property.Name))
                    continue;

                var operands = new List<QueryExpression>();

                if (property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        var operand = JsonSerializer.Deserialize<QueryExpression>(item.GetRawText(), options);

                        if (operand is not null)
                            operands.Add(operand);
                    }

                return new QueryExpression { Operator = property.Name, Operands = operands };
            }
        }

        return new QueryExpression { Literal = root.Clone() };
    }

    public override void Write(Utf8JsonWriter writer, QueryExpression value, JsonSerializerOptions options)
    {
        if (value.IsPath)
        {
            writer.WriteStartObject();
            writer.WriteString("path", value.Path);
            writer.WriteEndObject();
        }
        else if (value.IsVar)
        {
            writer.WriteStartObject();
            writer.WriteString("$var", value.Var);
            writer.WriteEndObject();
        }
        else if (value.IsArithmetic)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(value.Operator!);
            JsonSerializer.Serialize(writer, value.Operands, options);
            writer.WriteEndObject();
        }
        else if (value.Literal.HasValue)
        {
            value.Literal.Value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
