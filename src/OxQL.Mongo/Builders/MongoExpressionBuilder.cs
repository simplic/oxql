using OxQL.Core.Models;
using MongoDB.Bson;

namespace OxQL.Mongo.Builders;

/// <summary>
/// Builds MongoDB expressions from OxQL expression models.
/// </summary>
public sealed class MongoExpressionBuilder
{
    private readonly QueryVariables? _variables;

    public MongoExpressionBuilder(QueryVariables? variables)
    {
        _variables = variables;
    }

    /// <summary>
    /// Builds a MongoDB aggregation expression from a QueryExpression.
    /// </summary>
    public BsonValue Build(QueryExpression expression)
    {
        if (expression.IsPath)
            return new BsonString($"${TranslatePath(expression.Path!)}");

        if (expression.IsVar)
        {
            var value = _variables?.GetValue(expression.Var!);
            return ConvertToBson(value);
        }

        if (expression.IsLiteral)
            return new BsonDocument("$literal", ConvertToBson(expression.Literal));

        if (expression.IsArithmetic)
            return BuildArithmetic(expression);

        return BsonNull.Value;
    }

    /// <summary>
    /// Builds a MongoDB group _id expression from GroupByFields.
    /// </summary>
    public BsonValue BuildGroupId(IReadOnlyList<GroupByField> byFields)
    {
        if (byFields.Count == 1)
            return BuildGroupByField(byFields[0]);

        var idDoc = new BsonDocument();
        foreach (var field in byFields)
        {
            idDoc[field.As] = BuildGroupByField(field);
        }
        return idDoc;
    }

    /// <summary>
    /// Builds a MongoDB aggregation accumulator expression.
    /// </summary>
    public BsonValue BuildAccumulator(AggregationExpression aggExpr)
    {
        var func = aggExpr.Function?.ToLowerInvariant();

        return func switch
        {
            "count" => new BsonDocument("$sum", 1),
            "countdistinct" => BuildCountDistinct(aggExpr),
            "sum" => new BsonDocument("$sum", BuildAccumulatorArg(aggExpr.Argument)),
            "avg" => new BsonDocument("$avg", BuildAccumulatorArg(aggExpr.Argument)),
            "min" => new BsonDocument("$min", BuildAccumulatorArg(aggExpr.Argument)),
            "max" => new BsonDocument("$max", BuildAccumulatorArg(aggExpr.Argument)),
            "first" => new BsonDocument("$first", BuildAccumulatorArg(aggExpr.Argument)),
            "last" => new BsonDocument("$last", BuildAccumulatorArg(aggExpr.Argument)),
            "push" => new BsonDocument("$push", BuildAccumulatorArg(aggExpr.Argument)),
            _ => throw new InvalidOperationException($"Unknown aggregation function: {func}")
        };
    }

    private BsonValue BuildGroupByField(GroupByField field)
    {
        if (field.DateTrunc is not null)
        {
            return new BsonDocument("$dateTrunc", new BsonDocument
            {
                ["date"] = new BsonString($"${TranslatePath(field.DateTrunc.Path)}"),
                ["unit"] = new BsonString(field.DateTrunc.Unit)
            });
        }

        return new BsonString($"${TranslatePath(field.Path!)}");
    }

    private BsonValue BuildAccumulatorArg(QueryExpression? arg)
    {
        if (arg is null) return new BsonInt32(1);
        return Build(arg);
    }

    private BsonValue BuildCountDistinct(AggregationExpression aggExpr)
    {
        // countDistinct uses $addToSet then $size in a subsequent stage
        // For simplicity in a single $group, we use $addToSet
        return new BsonDocument("$addToSet", BuildAccumulatorArg(aggExpr.Argument));
    }

    private BsonValue BuildArithmetic(QueryExpression expression)
    {
        var op = expression.Operator!.ToLowerInvariant();
        var operands = expression.Operands!.Select(Build).ToList();

        return op switch
        {
            "add" => new BsonDocument("$add", new BsonArray(operands)),
            "subtract" => new BsonDocument("$subtract", new BsonArray(operands)),
            "multiply" => new BsonDocument("$multiply", new BsonArray(operands)),
            "divide" => new BsonDocument("$divide", new BsonArray(operands)),
            "coalesce" => new BsonDocument("$ifNull", new BsonArray(operands)),
            _ => throw new InvalidOperationException($"Unknown arithmetic operator: {op}")
        };
    }

    private static BsonValue ConvertToBson(object? value)
    {
        if (value is null) return BsonNull.Value;
        if (value is string s) return new BsonString(s);
        if (value is int i) return new BsonInt32(i);
        if (value is long l) return new BsonInt64(l);
        if (value is double d) return new BsonDouble(d);
        if (value is bool b) return b ? BsonBoolean.True : BsonBoolean.False;
        return new BsonString(value.ToString()!);
    }

    private static string TranslatePath(string path)
    {
        if (path == "id") return "_id";
        return path;
    }
}
