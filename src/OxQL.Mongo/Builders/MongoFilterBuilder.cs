using OxQL.Core.Models;
using MongoDB.Bson;
using System.Text.Json;
using MongoDB.Bson.IO;

namespace OxQL.Mongo.Builders;

/// <summary>
/// Builds MongoDB filter documents from OxQL filter conditions.
/// </summary>
public sealed class MongoFilterBuilder
{
    private readonly QueryVariables? _variables;

    public MongoFilterBuilder(QueryVariables? variables)
    {
        _variables = variables;
    }

    /// <summary>
    /// Builds a MongoDB filter from a MatchStage.
    /// </summary>
    public BsonDocument Build(MatchStage match)
    {
        if (match.And is not null)
            return new BsonDocument("$and", new BsonArray(match.And.Select(BuildCondition)));

        if (match.Or is not null)
            return new BsonDocument("$or", new BsonArray(match.Or.Select(BuildCondition)));

        if (match.Not is not null)
            return new BsonDocument("$nor", new BsonArray { BuildCondition(match.Not) });

        if (match.Condition is not null)
            return BuildCondition(match.Condition);

        return new BsonDocument();
    }

    /// <summary>
    /// Builds a MongoDB filter from a FilterCondition.
    /// </summary>
    public BsonDocument BuildCondition(FilterCondition condition)
    {
        if (condition.And is not null)
            return new BsonDocument("$and", new BsonArray(condition.And.Select(BuildCondition)));

        if (condition.Or is not null)
            return new BsonDocument("$or", new BsonArray(condition.Or.Select(BuildCondition)));

        if (condition.Not is not null)
            return new BsonDocument("$nor", new BsonArray { BuildCondition(condition.Not) });

        var path = TranslatePath(condition.Path!);
        var value = ResolveValue(condition.Value);

        return condition.Op?.ToLowerInvariant() switch
        {
            "eq"  => BuildEqualityFilter(path, value, negate: false, condition.Options),
            "neq" => BuildEqualityFilter(path, value, negate: true, condition.Options),
            "gt" => new BsonDocument(path, new BsonDocument("$gt", value)),
            "gte" => new BsonDocument(path, new BsonDocument("$gte", value)),
            "lt" => new BsonDocument(path, new BsonDocument("$lt", value)),
            "lte" => new BsonDocument(path, new BsonDocument("$lte", value)),
            "in" => new BsonDocument(path, new BsonDocument("$in", value.AsBsonArray)),
            "nin" => new BsonDocument(path, new BsonDocument("$nin", value.AsBsonArray)),
            "contains" => BuildContainsFilter(path, value, condition.Options),
            "startswith" => new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression($"^{EscapeRegex(value.AsString)}"))),
            "endswith" => new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression($"{EscapeRegex(value.AsString)}$"))),
            "exists" => new BsonDocument(path, new BsonDocument("$exists", value.AsBoolean)),
            "regex" => new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression(value.AsString))),
            _ => throw new InvalidOperationException($"Unknown operator: {condition.Op}")
        };
    }

    /// <summary>
    /// Builds an equality (or inequality) filter that handles GUID strings by also
    /// matching the binary UUID representations MongoDB may store them as (subtype 3 and 4).
    /// </summary>
    private static BsonDocument BuildEqualityFilter(string path, BsonValue value, bool negate, FilterConditionOptions? options = null)
    {
        var ignoreCase = options?.IgnoreCase == true;

        // Case-insensitive string equality: use a regex anchored at both ends
        if (ignoreCase && value is BsonString strVal)
        {
            var pattern = $"^{EscapeRegex(strVal.Value)}$";
            var regex = new BsonRegularExpression(pattern, "i");
            var regexDoc = new BsonDocument(path, new BsonDocument("$regex", regex));
            return negate
                ? new BsonDocument("$nor", new BsonArray { regexDoc })
                : regexDoc;
        }

        if (value is BsonString stringValue && Guid.TryParse(stringValue.Value, out var guid))
        {
            // MongoDB can store the same logical GUID as:
            //   – BsonBinaryData subtype 4 (RFC UUID, used by most drivers)
            //   – BsonBinaryData subtype 3 (legacy C# UUID, byte-swapped)
            //   – A plain string (stored as-is)
            var candidates = new BsonArray
            {
                new BsonDocument(path, new BsonDocument("$eq",
                    new BsonBinaryData(guid, GuidRepresentation.Standard))),    // subtype 4
                new BsonDocument(path, new BsonDocument("$eq",
                    new BsonBinaryData(guid, GuidRepresentation.CSharpLegacy))), // subtype 3
                new BsonDocument(path, new BsonDocument("$eq", value))           // plain string
            };

            return negate
                ? new BsonDocument("$nor", candidates)  // none of the three
                : new BsonDocument("$or",  candidates); // any of the three
        }

        var op = negate ? "$ne" : "$eq";
        return new BsonDocument(path, new BsonDocument(op, value));
    }

    private BsonValue ResolveValue(JsonElement? valueElement)
    {
        if (!valueElement.HasValue) return BsonNull.Value;

        var element = valueElement.Value;

        // Check for $var reference
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$var", out var varName))
        {
            var name = varName.GetString()!;
            var resolved = _variables?.GetValue(name);
            return ConvertToBson(resolved);
        }

        return JsonElementToBson(element);
    }

    private static BsonValue JsonElementToBson(JsonElement element)
    {
        // Type-hint object: { "$type": value }
        // Recognised hints:
        //   { "$uuid":    "7feec12f-870f-4087-a676-27e411d570a8" }  → BsonBinaryData subtype 4 (standard UUID)
        //   { "$uuid3":   "7feec12f-..."                          }  → BsonBinaryData subtype 3 (C# legacy UUID)
        //   { "$date":    "2024-01-15T10:30:00Z"                  }  → BsonDateTime (UTC)
        //   { "$oid":     "507f1f77bcf86cd799439011"              }  → BsonObjectId
        //   { "$long":    "9007199254740993"                      }  → BsonInt64
        //   { "$decimal": "123.456"                               }  → BsonDecimal128
        //   { "$regex":   "^abc"                                  }  → BsonRegularExpression
        //   { "$null":    true                                     }  → BsonNull
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("$uuid", out var uuidEl)
                && Guid.TryParse(uuidEl.GetString(), out var uuidStd))
                return new BsonBinaryData(uuidStd, GuidRepresentation.Standard);

            if (element.TryGetProperty("$uuid3", out var uuid3El)
                && Guid.TryParse(uuid3El.GetString(), out var uuidLeg))
                return new BsonBinaryData(uuidLeg, GuidRepresentation.CSharpLegacy);

            if (element.TryGetProperty("$date", out var dateEl))
            {
                var raw = dateEl.GetString()!;
                if (DateTimeOffset.TryParse(raw,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dto))
                    return new BsonDateTime(dto.UtcDateTime);
            }

            if (element.TryGetProperty("$oid", out var oidEl)
                && ObjectId.TryParse(oidEl.GetString(), out var oid))
                return new BsonObjectId(oid);

            if (element.TryGetProperty("$long", out var longEl))
            {
                var raw = longEl.ValueKind == JsonValueKind.String
                    ? longEl.GetString()!
                    : longEl.GetRawText();
                if (long.TryParse(raw, out var lng))
                    return new BsonInt64(lng);
            }

            if (element.TryGetProperty("$decimal", out var decEl))
            {
                var raw = decEl.ValueKind == JsonValueKind.String
                    ? decEl.GetString()!
                    : decEl.GetRawText();
                if (Decimal128.TryParse(raw, out var d128))
                    return new BsonDecimal128(d128);
            }

            if (element.TryGetProperty("$regex", out var rxEl))
                return new BsonRegularExpression(rxEl.GetString()!);

            if (element.TryGetProperty("$null", out _))
                return BsonNull.Value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => new BsonString(element.GetString()!),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? new BsonInt64(l) : new BsonDouble(element.GetDouble()),
            JsonValueKind.True => BsonBoolean.True,
            JsonValueKind.False => BsonBoolean.False,
            JsonValueKind.Null => BsonNull.Value,
            JsonValueKind.Array => new BsonArray(element.EnumerateArray().Select(JsonElementToBson)),
            _ => new BsonString(element.GetRawText())
        };
    }

    private static BsonValue ConvertToBson(object? value)
    {
        if (value is null) return BsonNull.Value;
        if (value is JsonElement je) return JsonElementToBson(je);
        if (value is string s) return new BsonString(s);
        if (value is int i) return new BsonInt32(i);
        if (value is long l) return new BsonInt64(l);
        if (value is double d) return new BsonDouble(d);
        if (value is bool b) return b ? BsonBoolean.True : BsonBoolean.False;
        if (value is DateTime dt) return new BsonDateTime(dt);
        if (value is DateTimeOffset dto) return new BsonDateTime(dto.UtcDateTime);

        return new BsonString(value.ToString()!);
    }

    private static BsonDocument BuildContainsFilter(string path, BsonValue value, FilterConditionOptions? options = null)
    {
        var flags = options?.IgnoreCase == true ? "i" : "";
        return new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression(EscapeRegex(value.AsString), flags)));
    }

    private static string TranslatePath(string path)
    {
        // Map "id" to "_id" for MongoDB
        if (path == "id") return "_id";
        return path;
    }

    private static string EscapeRegex(string input)
    {
        return System.Text.RegularExpressions.Regex.Escape(input);
    }
}
