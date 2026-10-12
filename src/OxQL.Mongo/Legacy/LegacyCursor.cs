// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using System.Globalization;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using OxQL.Core.Models;

namespace OxQL.Mongo.Legacy;

/// <summary>One sort field of a cursor with the value the last row of the page held there.</summary>
internal sealed record LegacyCursorField(string Path, string Direction, object? Value);

/// <summary>
/// The contract 1 cursor of the legacy pipeline: the sort fields and the last row's values as JSON, base64url.
/// A cursor only ever becomes values in a comparison on the request's own sort fields.
/// </summary>
internal static class LegacyCursor
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record LegacyCursorPayload(IReadOnlyList<LegacyCursorField>? Fields);

    /// <summary>The cursor after <paramref name="last"/>, the last row of a page.</summary>
    public static string Write(IReadOnlyList<LegacySortField> sort, BsonDocument last)
    {
        var payload = new LegacyCursorPayload(sort.Select(field => new LegacyCursorField(field.Path, field.Direction, ValueAt(last, field.Path))).ToList());

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Options))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a cursor and holds it against the request's sort; what does not fit is the 1.1 engine's refusal.</summary>
    public static IReadOnlyList<LegacyCursorField> Read(string cursor, IReadOnlyList<LegacySortField> sort)
    {
        LegacyCursorPayload? payload;

        try
        {
            var text = cursor.Replace('-', '+').Replace('_', '/');

            text += (text.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            payload = JsonSerializer.Deserialize<LegacyCursorPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(text)), Options);
        }
        catch (FormatException)
        {
            throw new LegacyRefusalException("INVALID_CURSOR", "Cursor is not valid base64url.");
        }
        catch (JsonException)
        {
            throw new LegacyRefusalException("INVALID_CURSOR", "Cursor contains invalid JSON.");
        }

        if (payload?.Fields is not { Count: > 0 } fields)
            throw new LegacyRefusalException("INVALID_CURSOR", "Cursor payload is empty.");

        if (fields.Count != sort.Count)
            throw new LegacyRefusalException("CURSOR_SORT_MISMATCH", $"Cursor has {fields.Count} fields but sort has {sort.Count} fields.");

        for (var index = 0; index < sort.Count; index++)
        {
            if (!string.Equals(fields[index].Path, sort[index].Path, StringComparison.OrdinalIgnoreCase))
                throw new LegacyRefusalException("CURSOR_SORT_MISMATCH", $"Cursor field '{fields[index].Path}' does not match expected sort field '{sort[index].Path}'.");

            if (!string.Equals(fields[index].Direction, sort[index].Direction, StringComparison.OrdinalIgnoreCase))
                throw new LegacyRefusalException("CURSOR_SORT_MISMATCH", $"Cursor direction for '{fields[index].Path}' does not match sort direction.");
        }

        return fields;
    }

    /// <summary>The rows after the cursor: per field, the fields before it equal and this one beyond.</summary>
    public static BsonDocument Filter(IReadOnlyList<LegacyCursorField> fields)
    {
        static string Beyond(LegacyCursorField field) => field.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase) ? "$lt" : "$gt";

        if (fields.Count == 1)
            return new BsonDocument(LegacyAggregate.Translate(fields[0].Path), new BsonDocument(Beyond(fields[0]), Bson(fields[0].Value)));

        var alternatives = new BsonArray();

        for (var index = 0; index < fields.Count; index++)
        {
            var alternative = new BsonDocument();

            for (var before = 0; before < index; before++)
                alternative[LegacyAggregate.Translate(fields[before].Path)] = Bson(fields[before].Value);

            alternative[LegacyAggregate.Translate(fields[index].Path)] = new BsonDocument(Beyond(fields[index]), Bson(fields[index].Value));
            alternatives.Add(alternative);
        }

        return new BsonDocument("$or", alternatives);
    }

    /// <summary>The value a row holds at a sort path, in the form the cursor carries: identifiers keep their type through a hint.</summary>
    private static object? ValueAt(BsonDocument row, string path)
    {
        BsonValue current = row;

        foreach (var segment in LegacyAggregate.Translate(path).Split('.'))
        {
            if (current is BsonDocument document && document.Contains(segment))
                current = document[segment];
            else
                return null;
        }

        return current switch
        {
            BsonString text => text.Value,
            BsonInt32 number => number.Value,
            BsonInt64 number => number.Value,
            BsonDouble number => number.Value,
            BsonDateTime instant => instant.ToUniversalTime(),
            BsonBoolean flag => flag.Value,
            BsonNull => null,
            BsonObjectId id => new Dictionary<string, object?> { ["$oid"] = id.Value.ToString() },
            BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } binary => new Dictionary<string, object?> { ["$uuid"] = binary.ToGuid(GuidRepresentation.Standard).ToString() },
            BsonBinaryData { SubType: BsonBinarySubType.UuidLegacy } binary => new Dictionary<string, object?> { ["$uuid3"] = binary.ToGuid(GuidRepresentation.CSharpLegacy).ToString() },
            _ => current.ToString(),
        };
    }

    private static BsonValue Bson(object? value)
    {
        if (value is not JsonElement element)
            return value is null ? BsonNull.Value : new BsonString(value.ToString()!);

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("$uuid", out var uuid) && Guid.TryParse(uuid.GetString(), out var standard))
                return new BsonBinaryData(standard, GuidRepresentation.Standard);

            if (element.TryGetProperty("$uuid3", out var uuid3) && Guid.TryParse(uuid3.GetString(), out var legacy))
                return new BsonBinaryData(legacy, GuidRepresentation.CSharpLegacy);

            if (element.TryGetProperty("$oid", out var oid) && ObjectId.TryParse(oid.GetString(), out var objectId))
                return new BsonObjectId(objectId);

            if (element.TryGetProperty("$date", out var date) && date.GetString() is { } raw
                && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hinted))
                return new BsonDateTime(hinted.UtcDateTime);

            if (element.TryGetProperty("$long", out var whole)
                && long.TryParse(whole.ValueKind == JsonValueKind.String ? whole.GetString() : whole.GetRawText(), out var number))
                return new BsonInt64(number);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;

            // A text that carries its offset is an instant the cursor wrote; any other text is a text.
            return HasOffset(text) && element.TryGetDateTimeOffset(out var instant) ? new BsonDateTime(instant.UtcDateTime) : new BsonString(text);
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? new BsonInt64(integer) : new BsonDouble(element.GetDouble()),
            JsonValueKind.True => BsonBoolean.True,
            JsonValueKind.False => BsonBoolean.False,
            JsonValueKind.Null => BsonNull.Value,
            _ => new BsonString(element.GetRawText()),
        };
    }

    private static bool HasOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
            return true;

        if (value.Length < 6)
            return false;

        var offset = value[^6..];

        return (offset[0] == '+' || offset[0] == '-') && char.IsDigit(offset[1]) && char.IsDigit(offset[2]) && offset[3] == ':' && char.IsDigit(offset[4]) && char.IsDigit(offset[5]);
    }
}

/// <summary>A refusal the 1.1 engine raised while it built the aggregate: a cursor that does not fit.</summary>
internal sealed class LegacyRefusalException(string code, string message, string? path = null) : Exception(message)
{
    public QueryValidationError Error { get; } = new() { Code = code, Message = message, Path = path };
}
