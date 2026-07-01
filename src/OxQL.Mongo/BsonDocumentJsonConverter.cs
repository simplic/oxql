using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;

namespace OxQL.Mongo;

/// <summary>
/// STJ converter for <see cref="BsonDocument"/> that walks the BSON value tree
/// directly, emitting idiomatic JSON without touching any typed BSON cast-getters.
/// GUIDs (binary sub-types 3 and 4) are rendered as standard
/// <c>"xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"</c> strings.
/// </summary>
public sealed class BsonDocumentJsonConverter : JsonConverter<BsonDocument>
{
    /// <inheritdoc />
    public override BsonDocument? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return BsonDocument.Parse(doc.RootElement.GetRawText());
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        BsonDocument value,
        JsonSerializerOptions options)
    {
        WriteDocument(writer, value);
    }

    // ── internal recursive writers ────────────────────────────────────────

    private static void WriteDocument(Utf8JsonWriter writer, BsonDocument doc)
    {
        writer.WriteStartObject();
        foreach (var element in doc)
        {
            writer.WritePropertyName(element.Name);
            WriteValue(writer, element.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                WriteDocument(writer, value.AsBsonDocument);
                break;

            case BsonType.Array:
                writer.WriteStartArray();
                foreach (var item in value.AsBsonArray)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;

            case BsonType.String:
                writer.WriteStringValue(value.AsString);
                break;

            case BsonType.Boolean:
                writer.WriteBooleanValue(value.AsBoolean);
                break;

            case BsonType.Int32:
                writer.WriteNumberValue(value.AsInt32);
                break;

            case BsonType.Int64:
                writer.WriteNumberValue(value.AsInt64);
                break;

            case BsonType.Double:
                var d = value.AsDouble;
                if (double.IsNaN(d) || double.IsInfinity(d))
                    writer.WriteNullValue();   // JSON has no NaN / Infinity
                else
                    writer.WriteNumberValue(d);
                break;

            case BsonType.Decimal128:
                if (decimal.TryParse(value.AsDecimal128.ToString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var dec))
                    writer.WriteNumberValue(dec);
                else
                    writer.WriteStringValue(value.AsDecimal128.ToString());
                break;

            case BsonType.DateTime:
                // Emit as ISO-8601 UTC string
                writer.WriteStringValue(
                    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                        System.Globalization.CultureInfo.InvariantCulture));
                break;

            case BsonType.ObjectId:
                writer.WriteStringValue(value.AsObjectId.ToString());
                break;

            case BsonType.Binary:
                var bin = value.AsBsonBinaryData;
                // Sub-type 3 (legacy UUID) and 4 (standard UUID) → readable GUID string
                if (bin.SubType is BsonBinarySubType.UuidLegacy or BsonBinarySubType.UuidStandard)
                {
                    writer.WriteStringValue(bin.ToGuid().ToString());
                }
                else
                {
                    writer.WriteStringValue(Convert.ToBase64String(bin.Bytes));
                }
                break;

            case BsonType.RegularExpression:
                writer.WriteStringValue(value.AsRegex.ToString());
                break;

            case BsonType.Symbol:
                writer.WriteStringValue(value.AsBsonSymbol.Name);
                break;

            case BsonType.JavaScript:
                writer.WriteStringValue(value.AsBsonJavaScript.Code);
                break;

            case BsonType.Timestamp:
                writer.WriteNumberValue(value.AsBsonTimestamp.Value);
                break;

            case BsonType.Null:
            case BsonType.Undefined:
                writer.WriteNullValue();
                break;

            default:
                // Unknown / unsupported type – emit its string representation
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
