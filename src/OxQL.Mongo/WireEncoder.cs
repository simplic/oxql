using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Options;
using OxQL.Core.Binding;
using OxQL.Model;

namespace OxQL.Mongo;

/// <summary>
/// The output pass: walks the final shape, not the document, rendering storage names to wire
/// names and every value by its kind in the schema's wire encoding. Members the document
/// carries that the shape does not are omitted; <c>unknown</c> members pass through verbatim.
/// </summary>
public static class WireEncoder
{
    /// <summary>Encodes one row of the final shape.</summary>
    public static JsonObject Encode(BsonDocument row, BoundPipeline bound) => Encode(row, bound, null);

    /// <summary>Encodes one row of the final shape, with the objects a remote resolve produced for it under their aliases.</summary>
    public static JsonObject Encode(BsonDocument row, BoundPipeline bound, IReadOnlyDictionary<string, JsonNode?>? remote)
    {
        var shape = bound.FinalShape;
        var result = new JsonObject();

        foreach (var (name, node) in shape.Roots)
        {
            switch (node)
            {
                case ShapeNode.Entity entity when name == Shape.ImplicitRoot:
                    EncodeMembers(row, entity.Def.Root, result, "", shape, Shape.ImplicitRoot);
                    break;

                case ShapeNode.Entity entity:
                    result[name] = row.TryGetValue(name, out var resolved) && resolved is BsonDocument resolvedDocument
                        ? EncodeObject(resolvedDocument, entity.Def.Root, name + ".", shape, name)
                        : null;
                    break;

                case ShapeNode.Element element:
                    if (row.TryGetValue(name, out var elementValue))
                        result[name] = EncodeValue(elementValue, ElementShape(element.Source), name, shape, name);
                    break;

                case ShapeNode.Array array:
                    if (row.TryGetValue(name, out var arrayValue) && arrayValue is BsonArray items)
                        result[name] = new JsonArray(items.Select(item => item is BsonDocument document
                            ? (JsonNode?)EncodeObject(document, array.Target.Root, name + ".", shape, name)
                            : Verbatim(item)).ToArray());
                    else
                        result[name] = new JsonArray();
                    break;

                case ShapeNode.Remote:
                    result[name] = remote is not null && remote.TryGetValue(name, out var resolvedRemote)
                        ? resolvedRemote
                        : row.TryGetValue(name, out var remoteValue) ? Verbatim(remoteValue) : null;
                    break;

                case ShapeNode.Scalar scalar:
                    if (row.TryGetValue(name, out var scalarValue))
                        result[name] = EncodeScalar(scalarValue, scalar.Kind, null);
                    break;

                case ShapeNode.GroupOutput output:
                    if (row.TryGetValue(name, out var outputValue))
                        result[name] = output.Shape is not null
                            ? EncodeValue(outputValue, output.Shape, name, shape, name)
                            : EncodeScalar(outputValue, output.Kind, null);
                    break;
            }
        }

        return result;
    }

    private static JsonObject EncodeObject(BsonDocument document, TypeDef type, string wirePrefix, Shape shape, string root)
    {
        var result = new JsonObject();

        EncodeMembers(document, type, result, wirePrefix, shape, root);

        return result;
    }

    private static void EncodeMembers(BsonDocument document, TypeDef type, JsonObject into, string wirePrefix, Shape shape, string root)
    {
        foreach (var member in type.Members)
        {
            if (!member.Stored || member.StorageName is null || !document.TryGetValue(member.StorageName, out var value))
                continue;

            var wire = wirePrefix + member.WireName;

            if (root == Shape.ImplicitRoot && !shape.IsVisible(wire))
                continue;

            var memberShape = shape.Unwound.Contains(Shape.UnwoundKey(root, WithoutRoot(wire, root)))
                ? ElementShapeOf(member)
                : member;

            into[member.WireName] = EncodeValue(value, memberShape, wire, shape, root);
        }
    }

    private static string WithoutRoot(string wire, string root) =>
        root.Length > 0 && wire.StartsWith(root + ".", StringComparison.Ordinal) ? wire[(root.Length + 1)..] : wire;

    private static ShapeDef? ElementShape(PathDef path) => ElementShapeOf(path.Shape);

    private static ShapeDef? ElementShapeOf(ShapeDef shape) => shape.Kind switch
    {
        Kind.Array => shape.Of,
        Kind.Dictionary => shape.Value,
        _ => shape,
    };

    /// <summary>Encodes a value by its shape.</summary>
    public static JsonNode? EncodeValue(BsonValue value, ShapeDef? shape, string wire, Shape? context, string root)
    {
        if (value is null || value.IsBsonNull || value.IsBsonUndefined)
            return null;

        if (shape is null)
            return Verbatim(value);

        switch (shape.Kind)
        {
            case Kind.Object:
                return value is BsonDocument document && shape.Type is not null
                    ? EncodeObject(document, shape.Type, wire + ".", context ?? Shape.ForEntity(EntityOf(shape)), root)
                    : Verbatim(value);

            case Kind.Array:
                return value is BsonArray array
                    ? new JsonArray(array.Select(item => EncodeValue(item, shape.Of, wire, context, root)).ToArray())
                    : Verbatim(value);

            case Kind.Dictionary:
                return EncodeDictionary(value, shape, wire, context, root);

            case Kind.Unknown:
                return Verbatim(value);

            default:
                return EncodeScalar(value, shape.Kind, shape);
        }
    }

    private static EntityDef EntityOf(ShapeDef shape) => throw new InvalidOperationException("An object shape needs a context.");

    private static JsonNode? EncodeDictionary(BsonValue value, ShapeDef shape, string wire, Shape? context, string root)
    {
        var result = new JsonObject();
        var keyWire = wire + ".*";

        switch (shape.DictionaryRepresentation ?? DictionaryRepresentation.Document)
        {
            case DictionaryRepresentation.Document when value is BsonDocument document:
                foreach (var element in document)
                    result[element.Name] = EncodeValue(element.Value, shape.Value, keyWire, context, root);
                return result;

            case DictionaryRepresentation.ArrayOfDocuments when value is BsonArray pairs:
                foreach (var pair in pairs.OfType<BsonDocument>())
                    if (pair.TryGetValue("k", out var key))
                        result[key.ToString() ?? ""] = pair.TryGetValue("v", out var item) ? EncodeValue(item, shape.Value, keyWire, context, root) : null;
                return result;

            case DictionaryRepresentation.ArrayOfArrays when value is BsonArray tuples:
                foreach (var tuple in tuples.OfType<BsonArray>())
                    if (tuple.Count == 2)
                        result[tuple[0].ToString() ?? ""] = EncodeValue(tuple[1], shape.Value, keyWire, context, root);
                return result;

            default:
                return Verbatim(value);
        }
    }

    /// <summary>Encodes a scalar by kind; a value whose BSON type does not fit the kind passes through verbatim.</summary>
    public static JsonNode? EncodeScalar(BsonValue value, Kind kind, ShapeDef? shape)
    {
        if (value is null || value.IsBsonNull || value.IsBsonUndefined)
            return null;

        switch (kind)
        {
            case Kind.String:
                return value switch
                {
                    BsonString text => JsonValue.Create(text.Value),
                    BsonInt32 code when shape?.Representation.BsonType == BsonType.Int32 => JsonValue.Create(((char)code.Value).ToString()),
                    _ => Verbatim(value),
                };

            case Kind.Int:
                return value switch
                {
                    BsonInt32 i => JsonValue.Create(i.Value),
                    BsonInt64 l when l.Value is >= int.MinValue and <= int.MaxValue => JsonValue.Create((int)l.Value),
                    BsonInt64 l => JsonValue.Create(l.Value),
                    BsonDouble d => JsonValue.Create(d.Value),
                    BsonDecimal128 m => JsonValue.Create((double)Decimal128.ToDecimal(m.Value)),
                    _ => Verbatim(value),
                };

            case Kind.Long:
                return value switch
                {
                    BsonInt32 i => JsonValue.Create(i.Value.ToString(CultureInfo.InvariantCulture)),
                    BsonInt64 l => JsonValue.Create(l.Value.ToString(CultureInfo.InvariantCulture)),
                    BsonDouble d => JsonValue.Create(((long)d.Value).ToString(CultureInfo.InvariantCulture)),
                    BsonDecimal128 m => JsonValue.Create(Decimal128.ToDecimal(m.Value).ToString("G29", CultureInfo.InvariantCulture)),
                    _ => Verbatim(value),
                };

            case Kind.Double:
                return value switch
                {
                    BsonDouble d => double.IsFinite(d.Value) ? JsonValue.Create(d.Value) : null,
                    BsonInt32 i => JsonValue.Create((double)i.Value),
                    BsonInt64 l => JsonValue.Create((double)l.Value),
                    BsonDecimal128 m => JsonValue.Create((double)Decimal128.ToDecimal(m.Value)),
                    _ => Verbatim(value),
                };

            case Kind.Decimal:
                return value switch
                {
                    BsonDecimal128 m => JsonValue.Create(Decimal128.ToDecimal(m.Value).ToString("G29", CultureInfo.InvariantCulture)),
                    BsonString text => JsonValue.Create(text.Value),
                    BsonInt32 i => JsonValue.Create(i.Value.ToString(CultureInfo.InvariantCulture)),
                    BsonInt64 l => JsonValue.Create(l.Value.ToString(CultureInfo.InvariantCulture)),
                    BsonDouble d => JsonValue.Create(((decimal)d.Value).ToString("G29", CultureInfo.InvariantCulture)),
                    _ => Verbatim(value),
                };

            case Kind.Bool:
                return value switch
                {
                    BsonBoolean b => JsonValue.Create(b.Value),
                    BsonString text when bool.TryParse(text.Value, out var parsed) => JsonValue.Create(parsed),
                    _ => Verbatim(value),
                };

            case Kind.Guid:
                return value switch
                {
                    BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } binary => JsonValue.Create(binary.ToGuid(GuidRepresentation.Standard).ToString()),
                    BsonBinaryData { SubType: BsonBinarySubType.UuidLegacy } binary => JsonValue.Create(binary.ToGuid(GuidRepresentation.CSharpLegacy).ToString()),
                    BsonString text => JsonValue.Create(text.Value),
                    _ => Verbatim(value),
                };

            case Kind.Date:
                return value switch
                {
                    BsonDateTime date => JsonValue.Create(date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    BsonString text => JsonValue.Create(text.Value),
                    _ => Verbatim(value),
                };

            case Kind.DateTime:
                return value switch
                {
                    BsonDateTime date => JsonValue.Create(Iso(date.ToUniversalTime())),
                    BsonDocument offset when offset.TryGetValue("DateTime", out var inner) && inner is BsonDateTime innerDate =>
                        JsonValue.Create(offset.TryGetValue("Offset", out var minutes) && minutes.IsNumeric
                            ? new DateTimeOffset(innerDate.ToUniversalTime()).ToOffset(TimeSpan.FromMinutes(minutes.ToDouble())).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture)
                            : Iso(innerDate.ToUniversalTime())),
                    BsonString text => JsonValue.Create(text.Value),
                    _ => Verbatim(value),
                };

            case Kind.TimeSpan:
                return value switch
                {
                    BsonString text when TimeSpan.TryParseExact(text.Value, "c", CultureInfo.InvariantCulture, out var span) => JsonValue.Create(XmlConvert.ToString(span)),
                    BsonString text => JsonValue.Create(text.Value),
                    BsonInt64 ticks => JsonValue.Create(XmlConvert.ToString(TimeSpan.FromTicks(ticks.Value))),
                    BsonDouble ms => JsonValue.Create(XmlConvert.ToString(TimeSpan.FromMilliseconds(ms.Value))),
                    _ => Verbatim(value),
                };

            case Kind.Enum:
                return value switch
                {
                    BsonInt32 i => JsonValue.Create(i.Value),
                    BsonInt64 l => JsonValue.Create(l.Value),
                    BsonString name when shape?.Type?.EnumValues.FirstOrDefault(member => member.Name == name.Value) is { } member =>
                        member.Value is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)member.Value) : JsonValue.Create(member.Value),
                    _ => Verbatim(value),
                };

            case Kind.Binary:
                return value switch
                {
                    BsonBinaryData binary => JsonValue.Create(Convert.ToBase64String(binary.Bytes)),
                    _ => Verbatim(value),
                };

            default:
                return Verbatim(value);
        }
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>A value the shape does not describe, rendered by its BSON type alone.</summary>
    public static JsonNode? Verbatim(BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                var result = new JsonObject();

                foreach (var element in value.AsBsonDocument)
                    result[element.Name] = Verbatim(element.Value);

                return result;

            case BsonType.Array:
                return new JsonArray(value.AsBsonArray.Select(Verbatim).ToArray());

            case BsonType.String:
                return JsonValue.Create(value.AsString);

            case BsonType.Boolean:
                return JsonValue.Create(value.AsBoolean);

            case BsonType.Int32:
                return JsonValue.Create(value.AsInt32);

            case BsonType.Int64:
                return JsonValue.Create(value.AsInt64);

            case BsonType.Double:
                return double.IsFinite(value.AsDouble) ? JsonValue.Create(value.AsDouble) : null;

            case BsonType.Decimal128:
                return JsonValue.Create(Decimal128.ToDecimal(value.AsDecimal128).ToString("G29", CultureInfo.InvariantCulture));

            case BsonType.DateTime:
                return JsonValue.Create(Iso(value.ToUniversalTime()));

            case BsonType.ObjectId:
                return JsonValue.Create(value.AsObjectId.ToString());

            case BsonType.Binary:
                var binary = value.AsBsonBinaryData;

                return binary.SubType switch
                {
                    BsonBinarySubType.UuidStandard => JsonValue.Create(binary.ToGuid(GuidRepresentation.Standard).ToString()),
                    BsonBinarySubType.UuidLegacy => JsonValue.Create(binary.ToGuid(GuidRepresentation.CSharpLegacy).ToString()),
                    _ => JsonValue.Create(Convert.ToBase64String(binary.Bytes)),
                };

            case BsonType.RegularExpression:
                return JsonValue.Create(value.AsRegex.ToString());

            case BsonType.Null:
            case BsonType.Undefined:
                return null;

            default:
                return JsonValue.Create(value.ToString());
        }
    }
}
