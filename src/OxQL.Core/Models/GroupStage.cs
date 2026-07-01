using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a group/aggregation stage.
/// </summary>
public sealed record GroupStage
{
    /// <summary>
    /// The fields to group by.
    /// </summary>
    [JsonPropertyName("by")]
    public required IReadOnlyList<GroupByField> By { get; init; }

    /// <summary>
    /// The aggregation fields to compute.
    /// </summary>
    [JsonPropertyName("fields")]
    public required IReadOnlyDictionary<string, AggregationExpression> Fields { get; init; }
}

/// <summary>
/// Represents a field in the group-by clause.
/// </summary>
public sealed record GroupByField
{
    /// <summary>
    /// The field path to group by.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Date truncation expression for date-based grouping.
    /// </summary>
    [JsonPropertyName("dateTrunc")]
    public DateTruncExpression? DateTrunc { get; init; }

    /// <summary>
    /// The output alias for this group field.
    /// </summary>
    [JsonPropertyName("as")]
    public required string As { get; init; }
}

/// <summary>
/// Represents a date truncation expression.
/// </summary>
public sealed record DateTruncExpression
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }
}

/// <summary>
/// Represents an aggregation expression (sum, avg, count, etc.).
/// </summary>
[JsonConverter(typeof(AggregationExpressionConverter))]
public sealed record AggregationExpression
{
    public string? Function { get; init; }
    public QueryExpression? Argument { get; init; }
    public bool IsCount { get; init; }
}

/// <summary>
/// Represents an expression that can be a path reference, literal, variable, or arithmetic.
/// </summary>
[JsonConverter(typeof(QueryExpressionConverter))]
public sealed record QueryExpression
{
    public string? Path { get; init; }
    public object? Literal { get; init; }
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
}

internal sealed class AggregationExpressionConverter : JsonConverter<AggregationExpression>
{
    private static readonly HashSet<string> AggFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum", "avg", "min", "max", "first", "last", "push", "count", "countDistinct"
    };

    public override AggregationExpression? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        foreach (var prop in root.EnumerateObject())
        {
            var funcName = prop.Name.ToLowerInvariant();
            if (!AggFunctions.Contains(funcName)) continue;

            if (funcName == "count" && prop.Value.ValueKind == JsonValueKind.True)
            {
                return new AggregationExpression { Function = "count", IsCount = true };
            }

            var argument = JsonSerializer.Deserialize<QueryExpression>(prop.Value.GetRawText(), options);
            return new AggregationExpression { Function = funcName, Argument = argument };
        }

        throw new JsonException("Unknown aggregation function.");
    }

    public override void Write(Utf8JsonWriter writer, AggregationExpression value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.IsCount)
        {
            writer.WriteBoolean(value.Function!, true);
        }
        else
        {
            writer.WritePropertyName(value.Function!);
            JsonSerializer.Serialize(writer, value.Argument, options);
        }
        writer.WriteEndObject();
    }
}

internal sealed class QueryExpressionConverter : JsonConverter<QueryExpression>
{
    private static readonly HashSet<string> ArithmeticOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "subtract", "multiply", "divide", "coalesce"
    };

    public override QueryExpression? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new QueryExpression { Path = reader.GetString() };
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("path", out var pathEl))
        {
            return new QueryExpression { Path = pathEl.GetString() };
        }

        if (root.TryGetProperty("$var", out var varEl))
        {
            return new QueryExpression { Var = varEl.GetString() };
        }

        if (root.TryGetProperty("literal", out var litEl))
        {
            return new QueryExpression { Literal = GetLiteralValue(litEl) };
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (ArithmeticOps.Contains(prop.Name))
            {
                var operands = new List<QueryExpression>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        var operand = JsonSerializer.Deserialize<QueryExpression>(item.GetRawText(), options);
                        if (operand is not null) operands.Add(operand);
                    }
                }
                return new QueryExpression { Operator = prop.Name.ToLowerInvariant(), Operands = operands };
            }
        }

        // Treat as literal
        return new QueryExpression { Literal = GetLiteralValue(root) };
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
        else
        {
            JsonSerializer.Serialize(writer, value.Literal, options);
        }
    }

    private static object? GetLiteralValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
