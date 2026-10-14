using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Options;
using OxQL.Core.Binding;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Model.Build;

namespace OxQL.Mongo;

/// <summary>
/// The output pass: walks the final shape, not the document, rendering storage names to wire
/// names and every value by its kind in the schema's wire encoding. Members the document
/// carries that the shape does not are omitted; <c>unknown</c> members pass through verbatim.
/// So does a stored value its kind cannot express — a <c>Decimal128</c> <c>NaN</c>, a date outside
/// the calendar, a duration outside <see cref="TimeSpan"/>: one such value never costs the page
/// it is on.
/// <para>
/// A <c>long</c>, a <c>decimal</c> and an enum backed by a long are JSON numbers, written with
/// every digit they hold (<c>Number</c>): exact on the wire, as the services' REST API writes
/// them. A reader that parses a JSON number into an IEEE double keeps 15 to 17 significant digits
/// of it. Every number the encoder writes for those kinds goes through the two <c>Number</c>
/// methods, so a choice of another spelling is made there and nowhere else. What is compared, not
/// shown (the key of a reference against the key of an owner's row), is text and is read through
/// <see cref="KeyText(BsonValue, Kind, ShapeDef?)"/>.
/// </para>
/// </summary>
internal static class WireEncoder
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
            // A join alias, an unwind alias or an index a projection after it did not keep is
            // not in the row: an empty array or a null there would say the join found nothing.
            if (name != Shape.ImplicitRoot && shape.Dropped.Contains(name))
                continue;

            switch (node)
            {
                case ShapeNode.Entity entity when name == Shape.ImplicitRoot:
                    EncodeMembers(row, entity.Def.Root, result, "", shape, Shape.ImplicitRoot, unfit, entity.Def);
                    break;

                case ShapeNode.Entity entity:
                    result[name] = row.TryGetValue(name, out var resolved) && resolved is BsonDocument resolvedDocument
                        ? EncodeObject(resolvedDocument, entity.Def.Root, name + ".", shape, name, unfit, entity.Def)
                        : null;
                    break;

                case ShapeNode.Element element:
                    if (row.TryGetValue(name, out var elementValue))
                        result[name] = Projected(EncodeValue(elementValue, ElementShape(element.Source), name, shape, name, unfit), name, shape, bound);
                    break;

                case ShapeNode.Array array:
                    if (row.TryGetValue(name, out var arrayValue) && arrayValue is BsonArray items)
                        result[name] = new JsonArray(items.Select(item => item is BsonDocument document
                            ? (JsonNode?)EncodeObject(document, array.Target.Root, name + ".", shape, name, unfit, array.Target)
                            : Verbatim(item)).ToArray());
                    else
                        result[name] = new JsonArray();
                    break;

                // The owner's row (a keyed alias: this host's own, from its SelfOwner), or null
                // when the owner had none: the local document holds nothing under the alias.
                case ShapeNode.Remote:
                case ShapeNode.Keyed:
                    result[name] = remote is not null && remote.TryGetValue(name, out var resolvedRemote) ? resolvedRemote : null;
                    break;

                case ShapeNode.Scalar scalar:
                    if (row.TryGetValue(name, out var scalarValue))
                        result[name] = EncodeScalar(scalarValue, scalar.Kind, null, name, unfit);
                    break;

                // A join's outcome under its name: the aggregate wrote it into the row, or the keyed fetch says it after the page.
                case ShapeNode.Outcome outcome:
                    if (outcome.InAggregate)
                    {
                        if (row.TryGetValue(name, out var told) && told is BsonString)
                            result[name] = told.AsString;
                    }
                    else if (remote is not null && remote.TryGetValue(name, out var said))
                    {
                        result[name] = said?.DeepClone();
                    }
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
    /// The element of an unwind as the projection leaves it. The element is a part of the row like
    /// the row's own members: a projection that names paths under its alias decides what it shows,
    /// and a reference a join kept in storage beside them (a join reads its key off the stored row
    /// after the projection) is not in the row. An alias no projection names shows what it holds,
    /// and so does the element of an owner's answer per key, whose matched member travels with it.
    /// A contract 1 row is what it has been.
    /// </summary>
    private static JsonNode? Projected(JsonNode? element, string alias, Shape shape, BoundPipeline bound)
    {
        if (!shape.Derives || bound.KeyedBy is not null || shape.Included is null || !shape.IsVisible(alias))
            return element;

        Prune(element, alias, shape);

        return element;
    }

    private static void Prune(JsonNode? node, string wire, Shape shape)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var name in members.Select(member => member.Key).ToList())
                {
                    if (!shape.IsVisible(wire + "." + name))
                        members.Remove(name);
                    else
                        Prune(members[name], wire + "." + name, shape);
                }

                break;

            // The elements of a collection are read under the collection's own path.
            case JsonArray elements:
                foreach (var each in elements)
                    Prune(each, wire, shape);

                break;
        }
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

    /// <summary>
    /// An object's members. <paramref name="entity"/> is the entity whose root row this is, the
    /// one place an addon bag lives; a nested object has none.
    /// </summary>
    private static JsonObject EncodeObject(BsonDocument document, TypeDef type, string wirePrefix, Shape shape, string root, ICollection<string>? unfit, EntityDef? entity = null)
    {
        var result = new JsonObject();

        EncodeMembers(document, type, result, wirePrefix, shape, root, unfit, entity);

        return result;
    }

    /// <summary>
    /// A polymorphic value's members by the variant its discriminator names: each member of the
    /// type in the type's order, read as the variant describes it (a member the variants
    /// disagree on is unknown, or unstored, on the merged type and typed on the variant), then
    /// any member only the variant has. A value with no discriminator, or one naming no
    /// registered variant, is read by the type itself.
    /// </summary>
    private static void EncodeMembers(BsonDocument document, TypeDef type, JsonObject into, string wirePrefix, Shape shape, string root, ICollection<string>? unfit, EntityDef? entity = null)
    {
        var variant = VariantOf(document, type);

        foreach (var member in type.Members)
        {
            if (member.ByVariant is { } values && !member.Stored && type.Variants.Count > 0 && shape.Derives)
                EncodeDerived(document, type, member, values, into, wirePrefix, shape, root);
            else
                EncodeMember(document, variant?.Member(member.WireName) ?? member, into, wirePrefix, shape, root, unfit, entity);
        }

        if (variant is null)
            return;

        foreach (var member in variant.Members)
            if (type.Member(member.WireName) is null)
                EncodeMember(document, member, into, wirePrefix, shape, root, unfit, entity);
    }

    /// <summary>The variant a stored value's discriminator names (the last one of a hierarchical discriminator), or null.</summary>
    private static TypeDef? VariantOf(BsonDocument document, TypeDef type)
    {
        if (type.Variants.Count == 0 || type.DiscriminatorElement is not { } element || !document.TryGetValue(element, out var stored))
            return null;

        var discriminator = stored is BsonArray { Count: > 0 } hierarchy ? hierarchy[^1] : stored;

        if (discriminator is not BsonString { Value: var name })
            return null;

        return type.Variants.FirstOrDefault(candidate => string.Equals(candidate.Discriminator, name, StringComparison.Ordinal))?.Type;
    }

    /// <summary>
    /// The model's own type member (<see cref="MemberDef.ByVariant"/>): not stored, and written from
    /// the discriminator the object is stored with, as the value its class answers for it. So a row
    /// shows what the service's REST API shows for the member. An object without a discriminator is
    /// the type's own (a concrete base); one whose discriminator names no variant this model knows
    /// has no value to show, and the member is left out of it.
    /// </summary>
    private static void EncodeDerived(BsonDocument document, TypeDef type, MemberDef member, IReadOnlyDictionary<string, string> values, JsonObject into, string wirePrefix, Shape shape, string root)
    {
        var wire = wirePrefix.Length == 0 ? member.WireName : wirePrefix + member.WireName;

        if (!Visible(wire, shape, root))
            return;

        string? variant = null;

        if (type.DiscriminatorElement is { } element && document.TryGetValue(element, out var stored))
        {
            var discriminator = stored is BsonArray { Count: > 0 } hierarchy ? hierarchy[^1] : stored;

            if (discriminator is not BsonString { Value: var named })
                return;

            variant = type.Variants.FirstOrDefault(candidate => string.Equals(candidate.Discriminator, named, StringComparison.Ordinal))?.Name
                ?? (OperandCoercer.ConcreteBaseName(type) is { } own && string.Equals(type.Discriminator ?? own, named, StringComparison.Ordinal) ? own : null);
        }
        else
        {
            variant = OperandCoercer.ConcreteBaseName(type);
        }

        if (variant is not null && values.TryGetValue(variant, out var value))
            into[member.WireName] = JsonValue.Create(value);
    }

    /// <summary>Whether the row shows the member at a wire path: the projection kept it, and under a join's alias the output set names it.</summary>
    private static bool Visible(string wire, Shape shape, string root)
    {
        if (root == Shape.ImplicitRoot && !shape.IsVisible(wire))
            return false;

        // Under a join's alias the row shows the output set and nothing else: what the join loaded
        // for a stage that only reads it, or the aggregate kept for a join after the page, is not in
        // the row. The projection alone decides the row's shape.
        return !(root != Shape.ImplicitRoot && Shown(shape, root) is { } shown && (shape.IsRemoved(wire) || !RootOutput.Shows(shown, wire[(root.Length + 1)..])));
    }

    private static void EncodeMember(BsonDocument document, MemberDef member, JsonObject into, string wirePrefix, Shape shape, string root, ICollection<string>? unfit, EntityDef? entity)
    {
        if (!member.Stored || member.StorageName is null || !document.TryGetValue(member.StorageName, out var value))
            return;

        // A member of the row itself is its own wire path: nothing is concatenated for it.
        var wire = wirePrefix.Length == 0 ? member.WireName : wirePrefix + member.WireName;

        if (!Visible(wire, shape, root))
            return;

        // Only a shape that unwound something has a member to read as its element.
        var memberShape = shape.Unwound.Count > 0 && shape.Unwound.Contains(Shape.UnwoundKey(root, WithoutRoot(wire, root)))
            ? ElementShapeOf(member)
            : member;

        into[member.WireName] = EncodeValue(value, memberShape, wire, shape, root, unfit);

        if (entity is not null && member.WireName == WireNames.AddonWire && entity.Path(WireNames.AddonWire) is { IsAddonRoot: true }
            && value is BsonDocument bag && into[member.WireName] is JsonObject encodedBag)
            EncodeDefinedDates(bag, encodedBag, entity, wire, shape, unfit);
    }

    /// <summary>
    /// The bag is untyped storage and renders by BSON type, except where the organisation
    /// defined a key as a <c>date</c>: the bag holds it as the midnight-UTC instant the driver
    /// writes for a date, and it travels as <c>YYYY-MM-DD</c> like every other date, which is
    /// also the only operand a date key accepts. A live definition decides; a retired, an
    /// undefined or an otherwise kinded key stays what the bag holds.
    /// </summary>
    private static void EncodeDefinedDates(BsonDocument bag, JsonObject encoded, EntityDef entity, string bagWire, Shape shape, ICollection<string>? unfit)
    {
        if (!shape.Addons.TryGetValue(entity.Id, out var definitions))
            return;

        foreach (var definition in definitions)
        {
            if (definition.Retired || definition.Kind != AddonKind.Date)
                continue;

            var segments = definition.Path.Split('.');
            BsonValue? stored = bag;
            JsonNode? target = encoded;

            for (var index = 0; index < segments.Length - 1 && stored is BsonDocument storedParent && target is JsonObject; index++)
            {
                stored = storedParent.TryGetValue(segments[index], out var inner) ? inner : null;
                target = target[segments[index]];
            }

            if (stored is BsonDocument parent && target is JsonObject into && parent.TryGetValue(segments[^1], out var value) && into.ContainsKey(segments[^1]))
                into[segments[^1]] = EncodeScalar(value, Kind.Date, null, bagWire + "." + definition.Path, unfit);
        }
    }

    /// <summary>The output set of a join alias in the final shape; null for a root that shows whatever it holds.</summary>
    private static IReadOnlyList<string>? Shown(Shape shape, string root) => shape.Roots.GetValueOrDefault(root) switch
    {
        ShapeNode.Entity { Join: not null } entity => entity.Select,
        ShapeNode.Array { Join: not null } array => array.Select,
        _ => null,
    };

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
        catch (Exception exception) when (exception is OverflowException or ArgumentException or FormatException)
        {
            // Every conversion above that leaves its range says so with one of these: a Decimal128
            // no decimal holds (NaN, an infinity, more digits or a larger magnitude), a double no
            // long or decimal holds, text stored under a decimal member that is no number.
            unfit?.Add(wire ?? Kinds.NameOf(kind));

            return Verbatim(value);
        }
    }

    /// <summary>
    /// The text a scalar is compared by where a value of this host meets one another host wrote: the
    /// key of a reference against the key of the owner's row. It is the digits of a number and the
    /// text of everything else, the same for a number written as a JSON number and for one an
    /// earlier build wrote as a string, so two builds find each other's rows. Null for a null.
    /// </summary>
    public static string? KeyText(BsonValue value, Kind kind, ShapeDef? shape) => KeyText(EncodeScalar(value, kind, shape));

    /// <summary>The text a value of a row is compared by: of a JSON string its text, of a number its canonical digits.</summary>
    public static string? KeyText(JsonNode? encoded) => encoded switch
    {
        null => null,
        JsonValue scalar when scalar.GetValueKind() == System.Text.Json.JsonValueKind.Number && scalar.TryGetValue<decimal>(out var number) => DecimalText.Canonical(number),
        JsonValue scalar => scalar.ToString(),
        _ => encoded.ToJsonString(),
    };

    /// <summary>A 64-bit integer on the wire: a JSON number with every digit.</summary>
    private static JsonNode Number(long value) => JsonValue.Create(value);

    /// <summary>
    /// A decimal on the wire: a JSON number in the canonical digits (<see cref="DecimalText.Canonical"/>:
    /// no exponent, no trailing fractional zeros), every one of them. The scale a value was stored
    /// with is not kept: 125000.00 and 125000 are one number.
    /// </summary>
    private static JsonNode Number(decimal value) => JsonValue.Create(decimal.Parse(DecimalText.Canonical(value), NumberStyles.Number, CultureInfo.InvariantCulture));

    /// <summary>The most characters a number written from the database's own digits may have; past it the runtime's decimal is written.</summary>
    private const int MaxExactDigits = 48;

    /// <summary>
    /// A stored <c>Decimal128</c> on the wire. The database computes in 34 significant digits and the
    /// runtime's decimal holds 28 or 29: a sum or a mean can have more than the runtime keeps. Such a
    /// value is written from the database's own digits, plain and without trailing fractional zeros,
    /// so the wire is exact there too. NaN, an infinity and a magnitude no decimal holds leave their
    /// range (<see cref="OverflowException"/>) and stay text.
    /// </summary>
    private static JsonNode Number(Decimal128 stored)
    {
        var held = Decimal128.ToDecimal(stored);

        if (Decimal128.Compare(new Decimal128(held), stored) == 0)
            return Number(held);

        return Plain(stored.ToString()) is { Length: <= MaxExactDigits } digits && JsonNode.Parse(digits) is { } exact ? exact : Number(held);
    }

    /// <summary>The plain spelling of a decimal's text that may carry an exponent: no exponent, no trailing fractional zeros. Null when the text is no number.</summary>
    private static string? Plain(string text)
    {
        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;
        var at = body.IndexOfAny(['E', 'e']);
        var exponent = 0;

        if (at >= 0 && !int.TryParse(body[(at + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            return null;

        var mantissa = at >= 0 ? body[..at] : body;
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var whole = (point < 0 ? mantissa.Length : point) + exponent;

        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || Math.Abs(whole) > MaxExactDigits)
            return null;

        var integer = whole <= 0 ? "0" : whole >= digits.Length ? digits.PadRight(whole, '0') : digits[..whole];
        var fraction = (whole >= digits.Length ? "" : whole <= 0 ? new string('0', -whole) + digits : digits[whole..]).TrimEnd('0');

        integer = integer.TrimStart('0');

        var plain = (integer.Length == 0 ? "0" : integer) + (fraction.Length == 0 ? "" : "." + fraction);

        return negative && plain != "0" ? "-" + plain : plain;
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
                    BsonDouble d when double.IsFinite(d.Value) => JsonValue.Create(d.Value),
                    BsonDouble => throw new ArgumentException("A double that is no number is no integer."),
                    BsonDecimal128 m => JsonValue.Create((double)Decimal128.ToDecimal(m.Value)),
                    _ => Verbatim(value),
                };

            case Kind.Long:
                return value switch
                {
                    BsonInt32 i => Number((long)i.Value),
                    BsonInt64 l => Number(l.Value),
                    BsonDouble d => Number(checked((long)d.Value)),
                    BsonDecimal128 m => Number(Decimal128.ToDecimal(m.Value)),
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
                // The typed bracket and the text bracket read back as one number in one
                // canonical spelling, the digits an operand is built with, so a value read out
                // of a row finds the row it came from. Text that is not a decimal at all is no
                // number: it stays the text the row holds, and is said like every value that
                // does not fit its kind.
                return value switch
                {
                    BsonDecimal128 m => Number(m.Value),
                    BsonString text => Number(decimal.Parse(text.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)),
                    BsonInt32 i => Number((decimal)i.Value),
                    BsonInt64 l => Number((decimal)l.Value),
                    BsonDouble d => Number((decimal)d.Value),
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
                // An enum's numeric value is a number whatever backs it, as a long is.
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

    /// <summary>An enum's numeric value: a JSON number, also where the enum is backed by a long.</summary>
    private static JsonNode? Enumeral(long value) =>
        value is >= int.MinValue and <= int.MaxValue
            ? JsonValue.Create((int)value)
            : Number(value);

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

    /// <summary>Whether a document is the driver's wrapper of an object-typed value: exactly a type name under <c>_t</c> and the value under <c>_v</c>.</summary>
    private static bool Wrapped(BsonDocument document, out string type, out BsonValue inner)
    {
        type = "";
        inner = BsonNull.Value;

        if (document.ElementCount != 2 || !document.TryGetValue("_t", out var named) || !named.IsString || !document.TryGetValue("_v", out var held))
            return false;

        type = named.AsString;
        inner = held;

        return true;
    }

    /// <summary>The decimal a wrapped value holds, as a Decimal128 or as the text an older serializer wrote; null when it is neither.</summary>
    private static decimal? DecimalOf(BsonValue value) => value switch
    {
        BsonDecimal128 stored when TryDecimal(stored.Value, out var number) => number,
        BsonString text when decimal.TryParse(text.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

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
                // What the driver writes for a value held as System.Object that BSON has no type of its
                // own for (a decimal, a dictionary, a list in an addon bag): { _t: <type>, _v: <value> }.
                // The row shows the value, never the wrapper; a decimal as every decimal travels.
                if (Wrapped(value.AsBsonDocument, out var type, out var inner))
                    return type == "System.Decimal" && DecimalOf(inner) is { } wrapped ? Number(wrapped) : Verbatim(inner);

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
                // is rendered by its BSON type alone: a 64-bit integer as the number a long is.
                return Number(value.AsInt64);

            case BsonType.Double:
                return double.IsFinite(value.AsDouble) ? JsonValue.Create(value.AsDouble) : null;

            case BsonType.Decimal128:
                // A number, as a decimal member's is. NaN, the infinities and what a decimal
                // cannot hold are no JSON number: they keep the database's own spelling, as text.
                return TryDecimal(value.AsDecimal128, out _)
                    ? Number(value.AsDecimal128)
                    : JsonValue.Create(value.AsDecimal128.ToString());

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
