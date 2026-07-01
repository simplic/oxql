using OxQL.Core.Models;
using MongoDB.Bson;

namespace OxQL.Mongo.Builders;

/// <summary>
/// Builds MongoDB cursor-based paging filter predicates.
/// </summary>
public sealed class MongoCursorPagingBuilder
{
    /// <summary>
    /// Builds a cursor filter predicate for multi-field sort.
    /// For sort by (createdAt desc, id asc), generates:
    /// { $or: [
    ///   { createdAt: { $lt: lastCreatedAt } },
    ///   { createdAt: lastCreatedAt, id: { $gt: lastId } }
    /// ] }
    /// </summary>
    public BsonDocument BuildCursorFilter(CursorPayload cursor)
    {
        if (cursor.Fields.Count == 0)
            return new BsonDocument();

        if (cursor.Fields.Count == 1)
            return BuildSingleFieldCursor(cursor.Fields[0]);

        return BuildMultiFieldCursor(cursor.Fields);
    }

    private static BsonDocument BuildSingleFieldCursor(CursorField field)
    {
        var path = TranslatePath(field.Path);
        var value = ConvertToBson(field.Value);
        var op = field.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase) ? "$lt" : "$gt";

        return new BsonDocument(path, new BsonDocument(op, value));
    }

    private static BsonDocument BuildMultiFieldCursor(IReadOnlyList<CursorField> fields)
    {
        var orConditions = new BsonArray();

        // Build progressive conditions for each field
        for (var i = 0; i < fields.Count; i++)
        {
            var condition = new BsonDocument();

            // All preceding fields must be equal
            for (var j = 0; j < i; j++)
            {
                var prevPath = TranslatePath(fields[j].Path);
                condition[prevPath] = ConvertToBson(fields[j].Value);
            }

            // Current field uses comparison
            var currentPath = TranslatePath(fields[i].Path);
            var currentValue = ConvertToBson(fields[i].Value);
            var op = fields[i].Direction.Equals("desc", StringComparison.OrdinalIgnoreCase) ? "$lt" : "$gt";
            condition[currentPath] = new BsonDocument(op, currentValue);

            orConditions.Add(condition);
        }

        return new BsonDocument("$or", orConditions);
    }

    private static BsonValue ConvertToBson(object? value)
    {
        if (value is null) return BsonNull.Value;
        if (value is System.Text.Json.JsonElement je) return JsonElementToBson(je);
        if (value is string s) return new BsonString(s);
        if (value is int i) return new BsonInt32(i);
        if (value is long l) return new BsonInt64(l);
        if (value is double d) return new BsonDouble(d);
        if (value is bool b) return b ? BsonBoolean.True : BsonBoolean.False;
        if (value is DateTime dt) return new BsonDateTime(dt);
        if (value is DateTimeOffset dto) return new BsonDateTime(dto.UtcDateTime);
        return new BsonString(value.ToString()!);
    }

    private static BsonValue JsonElementToBson(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => new BsonString(element.GetString()!),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? new BsonInt64(l) : new BsonDouble(element.GetDouble()),
            System.Text.Json.JsonValueKind.True => BsonBoolean.True,
            System.Text.Json.JsonValueKind.False => BsonBoolean.False,
            System.Text.Json.JsonValueKind.Null => BsonNull.Value,
            _ => new BsonString(element.GetRawText())
        };
    }

    private static string TranslatePath(string path)
    {
        if (path == "id") return "_id";
        return path;
    }
}
