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
/// So does a stored value its kind cannot express — a <c>Decimal128</c> <c>NaN</c>, a date outside
/// the calendar, a duration outside <see cref="TimeSpan"/>: one such value never costs the page
/// it is on.
/// </summary>
public static class WireEncoder
{
    /// <summary>Encodes one row of the final shape.</summary>
    public static JsonObject Encode(BsonDocument row, BoundPipeline bound) => Encode(row, bound, null);

    /// <summary>Encodes one row of the final shape, with the objects a remote resolve produced for it under their aliases.</summary>
    public static JsonObject Encode(BsonDocument row, BoundPipeline bound, IReadOnlyDictionary<string, JsonNode?>? remote) => Encode(row, bound, remote, null);

    /// <summary>
    /// Encodes one row of the final shape, with the objects a remote resolve produced for it under
    /// their aliases. <paramref name="unfit"/> collects the wire path of every value that did not
    /// fit its kind and was rendered verbatim instead, so the caller can say so once.
    /// </summary>
    public static JsonObject Encode(BsonDocument row, BoundPipeline bound, IReadOnlyDictionary<string, JsonNode?>? remote, ICollection<string>? unfit)
    {
        var shape = bound.FinalShape;
        var result = new JsonObject();

        foreach (var (name, node) in shape.Roots)
        {
            switch (node)
            {
                case ShapeNode.Entity entity when name == Shape.ImplicitRoot:
                    EncodeMembers(row, entity.Def.Root, result, "", shape, Shape.ImplicitRoot, unfit);
                    break;

                case ShapeNode.Entity entity:
                    result[name] = row.TryGetValue(name, out var resolved) && resolved is BsonDocument resolvedDocument
                        ? EncodeObject(resolvedDocument, entity.Def.Root, name + ".", shape, name, unfit)
                        : null;
                    break;

                case ShapeNode.Element element:
                    if (row.TryGetValue(name, out var elementValue))
                        result[name] = EncodeValue(elementValue, ElementShape(element.Source), name, shape, name, unfit);
                    break;

                case ShapeNode.Array array:
                    if (row.TryGetValue(name, out var arrayValue) && arrayValue is BsonArray items)
                        result[name] = new JsonArray(items.Select(item => item is BsonDocument document
                            ? (JsonNode?)EncodeObject(document, array.Target.Root, name + ".", shape, name, unfit)
                            : Verbatim(item)).ToArray());
                    else
                        result[name] = new JsonArray();
                    break;

                // The owner's row, or null when the owner had none: the local document holds
                // nothing under the alias.
                case ShapeNode.Remote:
                    result[name] = remote is not null && remote.TryGetValue(name, out var resolvedRemote) ? resolvedRemote : null;
                    break;

                case ShapeNode.Scalar scalar:
                    if (row.TryGetValue(name, out var scalarValue))
                        result[name] = EncodeScalar(scalarValue, scalar.Kind, null, name, unfit);
                    break;

                case ShapeNode.GroupOutput output:
                    if (row.TryGetValue(name, out var outputValue))
                        result[name] = EncodeOutput(outputValue, output, name, shape, unfit);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// A group output: a pushed array element by element in its element's encoding, so a
    /// pushed value reads back as the row's own value would; any other output by its shape,
    /// or by its kind where it has none.
    /// </summary>
    private static JsonNode? EncodeOutput(BsonValue value, ShapeNode.GroupOutput output, string name, Shape shape, ICollection<string>? unfit)
    {
        if (output.Kind == Kind.Array && value is BsonArray items)
            return new JsonArray(items.Select(Element).ToArray());

        return output.Shape is not null
            ? EncodeValue(value, output.Shape, name, shape, name, unfit)
            : EncodeScalar(value, output.Kind, null, name, unfit);

        // A member under a collection pushes one array per row.
        JsonNode? Element(BsonValue item) => item switch
        {
            BsonArray nested => new JsonArray(nested.Select(Element).ToArray()),
            _ when output.ElementShape is not null => EncodeValue(item, output.ElementShape, name, shape, name, unfit),
            _ => EncodeScalar(item, output.ElementKind, null, name, unfit),
        };
    }

    private static JsonObject EncodeObject(BsonDocument document, TypeDef type, string wirePrefix, Shape shape, string root, ICollection<string>? unfit)
    {
        var result = new JsonObject();

        EncodeMembers(document, type, result, wirePrefix, shape, root, unfit);

        return result;
    }

    private static void EncodeMembers(BsonDocument document, TypeDef type, JsonObject into, string wirePrefix, Shape shape, string root, ICollection<string>? unfit)
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

            into[member.WireName] = EncodeValue(value, memberShape, wire, shape, root, unfit);
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

    /// <summary>Encodes a value by its shape, at <paramref name="wire"/> under <paramref name="root"/> of the row's shape <paramref name="context"/>.</summary>
    public static JsonNode? EncodeValue(BsonValue value, ShapeDef? shape, string wire, Shape context, string root) =>
        EncodeValue(value, shape, wire, context, root, null);

    private static JsonNode? EncodeValue(BsonValue value, ShapeDef? shape, string wire, Shape context, string root, ICollection<string>? unfit)
    {
        if (value is null || value.IsBsonNull || value.IsBsonUndefined)
            return null;

        if (shape is null)
            return Verbatim(value);

        switch (shape.Kind)
        {
            case Kind.Object:
                return value is BsonDocument document && shape.Type is not null
                    ? EncodeObject(document, shape.Type, wire + ".", context, root, unfit)
                    : Verbatim(value);

            case Kind.Array:
                return value is BsonArray array
                    ? new JsonArray(array.Select(item => EncodeValue(item, shape.Of, wire, context, root, unfit)).ToArray())
                    : Verbatim(value);

            case Kind.Dictionary:
                return EncodeDictionary(value, shape, wire, context, root, unfit);

            case Kind.Unknown:
                return Verbatim(value);

            default:
                return EncodeScalar(value, shape.Kind, shape, wire, unfit);
        }
    }

    private static JsonNode? EncodeDictionary(BsonValue value, ShapeDef shape, string wire, Shape context, string root, ICollection<string>? unfit)
    {
        var result = new JsonObject();
        var keyWire = wire + ".*";

        switch (shape.DictionaryRepresentation ?? DictionaryRepresentation.Document)
        {
            case DictionaryRepresentation.Document when value is BsonDocument document:
                foreach (var element in document)
                    result[element.Name] = EncodeValue(element.Value, shape.Value, keyWire, context, root, unfit);
                return result;

            case DictionaryRepresentation.ArrayOfDocuments when value is BsonArray pairs:
                foreach (var pair in pairs.OfType<BsonDocument>())
                    if (pair.TryGetValue("k", out var key))
                        result[key.ToString() ?? ""] = pair.TryGetValue("v", out var item) ? EncodeValue(item, shape.Value, keyWire, context, root, unfit) : null;
                return result;

            case DictionaryRepresentation.ArrayOfArrays when value is BsonArray tuples:
                foreach (var tuple in tuples.OfType<BsonArray>())
                    if (tuple.Count == 2)
                        result[tuple[0].ToString() ?? ""] = EncodeValue(tuple[1], shape.Value, keyWire, context, root, unfit);
                return result;

            default:
                return Verbatim(value);
        }
    }

    /// <summary>
    /// Encodes a scalar by kind. A value whose BSON type does not fit the kind passes through
    /// verbatim, and so does one whose type fits and whose value does not: what is stored is
    /// the database's to hold, and a row that holds it is still a row.
    /// </summary>
    public static JsonNode? EncodeScalar(BsonValue value, Kind kind, ShapeDef? shape) => EncodeScalar(value, kind, shape, null, null);

    private static JsonNode? EncodeScalar(BsonValue value, Kind kind, ShapeDef? shape, string? wire, ICollection<string>? unfit)
    {
        if (value is null || value.IsBsonNull || value.IsBsonUndefined)
            return null;

        try
        {
            return EncodeInRange(value, kind, shape);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentException)
        {
            // Every conversion above that leaves its range says so with one of these two.
            unfit?.Add(wire ?? Kinds.NameOf(kind));

            return Verbatim(value);
        }
    }

    private static JsonNode? EncodeInRange(BsonValue value, Kind kind, ShapeDef? shape)
    {
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
                // The typed bracket and the text bracket read back in one canonical spelling,
                // the one an operand is built with, so a value read out of a row finds the
                // row it came from. Text that is not a decimal at all stays verbatim:
                // normalising it would invent a number the row does not hold.
                return value switch
                {
                    BsonDecimal128 m => JsonValue.Create(DecimalText.Canonical(Decimal128.ToDecimal(m.Value))),
                    BsonString text => JsonValue.Create(decimal.TryParse(text.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsedText)
                        ? DecimalText.Canonical(parsedText)
                        : text.Value),
                    BsonInt32 i => JsonValue.Create(i.Value.ToString(CultureInfo.InvariantCulture)),
                    BsonInt64 l => JsonValue.Create(l.Value.ToString(CultureInfo.InvariantCulture)),
                    BsonDouble d => JsonValue.Create(DecimalText.Canonical((decimal)d.Value)),
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
                // An enum backed by a long carries values JSON's number cannot hold: a
                // JavaScript caller reads 9007199254740993 back as ...992, a value that
                // matches nothing. A value inside Int32 stays a number; above it the value
                // travels as a string, the way Kind.Long does and for the same reason.
                return value switch
                {
                    BsonInt32 i => JsonValue.Create(i.Value),
                    BsonInt64 l => Enumeral(l.Value),
                    BsonString name when shape?.Type?.EnumValues.FirstOrDefault(member => member.Name == name.Value) is { } member => Enumeral(member.Value),
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

    /// <summary>An enum's numeric value: a JSON number while it is one a caller can read, a string above that.</summary>
    private static JsonNode? Enumeral(long value) =>
        value is >= int.MinValue and <= int.MaxValue
            ? JsonValue.Create((int)value)
            : JsonValue.Create(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Whether a 64-bit integer survives a JSON round trip through an IEEE-754 double.</summary>
    private static bool SafeInteger(long value) => value is >= -9007199254740991 and <= 9007199254740991;

    private static bool TryDecimal(Decimal128 stored, out decimal number)
    {
        try
        {
            number = Decimal128.ToDecimal(stored);
            return true;
        }
        catch (OverflowException)
        {
            number = default;
            return false;
        }
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A value the shape does not describe, rendered by its BSON type alone. It renders every
    /// value the database can hold: it is what the encoder falls back to, so it has nothing to
    /// fall back to itself.
    /// </summary>
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
                // A member the shape does not describe — the addon bag's values above all —
                // is rendered by its BSON type alone. As a JSON number a 64-bit integer loses
                // precision in a JavaScript caller: 9007199254740993 arrives as ...992, a
                // value that matches nothing. Kind.Long stringifies for exactly this reason;
                // here the kind is not known, so the range decides: inside it the value is
                // exact as a number and stays one.
                return SafeInteger(value.AsInt64)
                    ? JsonValue.Create(value.AsInt64)
                    : JsonValue.Create(value.AsInt64.ToString(CultureInfo.InvariantCulture));

            case BsonType.Double:
                return double.IsFinite(value.AsDouble) ? JsonValue.Create(value.AsDouble) : null;

            case BsonType.Decimal128:
                // NaN, the infinities and the magnitudes a decimal cannot hold keep the
                // database's own spelling.
                return JsonValue.Create(TryDecimal(value.AsDecimal128, out var number)
                    ? number.ToString("G29", CultureInfo.InvariantCulture)
                    : value.AsDecimal128.ToString());

            case BsonType.DateTime:
                // A date outside the calendar has no ISO form; its milliseconds since the epoch do.
                var date = (BsonDateTime)value;

                return JsonValue.Create(date.IsValidDateTime
                    ? Iso(date.ToUniversalTime())
                    : date.MillisecondsSinceEpoch.ToString(CultureInfo.InvariantCulture));

            case BsonType.ObjectId:
                return JsonValue.Create(value.AsObjectId.ToString());

            case BsonType.Binary:
                var binary = value.AsBsonBinaryData;

                return binary.SubType switch
                {
                    BsonBinarySubType.UuidStandard when binary.Bytes.Length == 16 => JsonValue.Create(binary.ToGuid(GuidRepresentation.Standard).ToString()),
                    BsonBinarySubType.UuidLegacy when binary.Bytes.Length == 16 => JsonValue.Create(binary.ToGuid(GuidRepresentation.CSharpLegacy).ToString()),
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
