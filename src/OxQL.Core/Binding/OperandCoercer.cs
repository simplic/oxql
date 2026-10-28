using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Core.Binding;

/// <summary>
/// Encodes an operand for storage from the member's kind and representation. Nothing is
/// guessed from the JSON alone: a wrong JSON kind is <c>INVALID_OPERAND</c> naming the expected
/// kind. Tolerant forms (decimal in two storage forms, optionally guids, the GUID of a defined
/// addon key, any defined addon key through a collection) come out as alternatives the compiler
/// turns into an <c>$or</c>.
/// </summary>
public sealed class OperandCoercer
{
    private const string VarKey = "$var";

    /// <summary>The JSON literal <c>null</c>, detached from the document it was parsed from.</summary>
    internal static readonly JsonElement JsonNull = ParseNull();

    private readonly OxQLOptions options;
    private readonly QueryVariables? variables;
    private readonly List<Diagnostic>? diagnostics;
    private readonly bool contract2;

    /// <summary>
    /// The collection whose elements the operands being read are compared with (inside <c>any</c>);
    /// null elsewhere. A message names a member under it, as the row has it.
    /// </summary>
    public string? Under { get; set; }

    private string Shown(ResolvedPath path) => Under is null ? path.Wire : Under + "." + path.Wire;

    /// <summary>
    /// The coercer of one request. Under <paramref name="contract2"/> an operand is read as written or
    /// refused: a decimal is digits with one optional sign, point and exponent, and a member holding a
    /// single character takes one character.
    /// </summary>
    public OperandCoercer(OxQLOptions options, QueryVariables? variables, List<Diagnostic>? diagnostics = null, bool contract2 = true)
    {
        this.options = options;
        this.variables = variables;
        this.diagnostics = diagnostics;
        this.contract2 = contract2;
    }

    /// <summary>The operators, case-sensitive.</summary>
    public static readonly IReadOnlySet<string> Operators = new HashSet<string>(StringComparer.Ordinal)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex", "is",
    };

    private static readonly IReadOnlySet<string> StringOperators = new HashSet<string>(StringComparer.Ordinal) { "contains", "startsWith", "endsWith", "regex" };
    private static readonly IReadOnlySet<string> OrderedOperators = new HashSet<string>(StringComparer.Ordinal) { "gt", "gte", "lt", "lte" };
    private static readonly IReadOnlySet<string> SetOperators = new HashSet<string>(StringComparer.Ordinal) { "in", "nin" };
    private static readonly IReadOnlySet<string> ClosedListOperators = new HashSet<string>(StringComparer.Ordinal) { "eq", "neq", "in", "nin" };
    private static readonly IReadOnlySet<string> IgnoreCaseOperators = new HashSet<string>(StringComparer.Ordinal) { "eq", "neq", "in", "nin", "contains", "startsWith", "endsWith" };
    private static readonly IReadOnlySet<string> FoldingOperators = new HashSet<string>(StringComparer.Ordinal) { "eq", "neq", "in", "nin", "gt", "gte", "lt", "lte", "contains", "startsWith", "endsWith" };

    /// <summary>Whether <paramref name="op"/> applies to <paramref name="kind"/>.</summary>
    public static bool Applies(string op, Kind kind)
    {
        if (StringOperators.Contains(op))
            return kind == Kind.String;

        if (OrderedOperators.Contains(op))
            return kind is Kind.Int or Kind.Long or Kind.Double or Kind.Decimal or Kind.Date or Kind.DateTime or Kind.TimeSpan or Kind.String or Kind.Enum;

        return true;
    }

    /// <summary>
    /// Whether the case option (<c>caseSensitive</c>, or its alias <c>ignoreCase</c>) applies
    /// to <paramref name="op"/> on <paramref name="kind"/>: the comparisons a string can opt
    /// out of. An ordered comparison orders under the collation of the whole aggregate and
    /// cannot; a pattern is the caller's own.
    /// </summary>
    public static bool IgnoreCaseApplies(string op, Kind kind) => kind == Kind.String && IgnoreCaseOperators.Contains(op);

    /// <summary>
    /// Whether a contract 2 comparison folds case unless it opts out: every comparison of a
    /// string, the ordered ones included. On a member holding a single character, stored as its
    /// code point, only the four value comparisons fold, by code point; its order is the code
    /// point's.
    /// </summary>
    public static bool FoldsByDefault(string op, ResolvedPath path) =>
        path.LeafKind == Kind.String && (IsCharRepresented(path) ? ClosedListOperators.Contains(op) : FoldingOperators.Contains(op));

    /// <summary>
    /// A <c>char</c> member: published as a <c>string</c> because that is what it is on the
    /// wire, stored as its code point. It compares by value, so <c>eq</c>, <c>neq</c>,
    /// <c>in</c>, <c>nin</c> and the ordered operators work on it; the four operators that
    /// need text — <c>contains</c>, <c>startsWith</c>, <c>endsWith</c>, <c>regex</c> — have no
    /// text to work on, and the binder refuses them.
    /// </summary>
    public static bool IsCharRepresented(ResolvedPath path) =>
        path is not null && path.LeafKind == Kind.String && path.Leaf?.Representation.BsonType == BsonType.Int32;

    /// <summary>Whether <paramref name="op"/> needs the member to carry text rather than a value.</summary>
    public static bool NeedsText(string op) => StringOperators.Contains(op);

    /// <summary>Coerces one operand; null with errors added when it cannot be.</summary>
    public BoundOperand? Coerce(JsonElement? raw, ResolvedPath path, string op, int stage, List<QueryValidationError> errors)
    {
        var element = raw ?? JsonNull;

        // The variable wrapper is the only object operand.
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!TryVariable(element, out var name))
            {
                if (path.IsRemote)
                    return new BoundOperand.Raw(element);

                errors.Add(Operand($"'{Shown(path)}' expects {Expected(path)}, not an object.", stage, path, op, Kinds.NameOf(path.LeafKind), element));
                return null;
            }

            if (!TryResolveVariable(name!, out element))
            {
                errors.Add(Unbound(name, stage, path.Wire));
                return null;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                errors.Add(Error(Codes.InvalidVariable, $"The variable '{name}' holds an object; a variable holds a value or an array.", stage, path.Wire));
                return null;
            }
        }

        if (path.IsRemote)
            return new BoundOperand.Raw(element);

        var kind = path.LeafKind;

        switch (op)
        {
            case "exists":
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return new BoundOperand.Single(BsonBoolean.Create(element.ValueKind == JsonValueKind.True));

                errors.Add(Operand("'exists' takes a boolean.", stage, path, op, "boolean", element));
                return null;

            case "in":
            case "nin":
                if (element.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(Error(Codes.OperandNotArray, $"'{op}' takes an array of {Kinds.NameOf(kind)}.", stage, path.Wire));
                    return null;
                }

                var values = new List<BsonValue>();
                var index = 0;

                foreach (var rawItem in element.EnumerateArray())
                {
                    var item = rawItem;
                    var label = $"{Shown(path)}[{index++}]";

                    // An element may be a variable wrapper of its own: the whole-array form and
                    // the per-element form are both part of the contract.
                    if (item.ValueKind == JsonValueKind.Object && TryVariable(item, out var itemName))
                    {
                        if (!TryResolveVariable(itemName!, out item))
                        {
                            errors.Add(Unbound(itemName, stage, path.Wire));
                            continue;
                        }

                        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            errors.Add(Error(Codes.InvalidVariable, $"The variable '{itemName}' holds {(item.ValueKind == JsonValueKind.Array ? "an array" : "an object")}; an element of '{op}' holds a value.", stage, path.Wire));
                            continue;
                        }
                    }

                    if (item.ValueKind == JsonValueKind.Null)
                    {
                        values.Add(BsonNull.Value);
                        continue;
                    }

                    if (!InClosedList(item, path, kind, op, stage, errors, label))
                        continue;

                    var alternatives = CoerceScalar(item, path, kind, op, stage, errors, label);

                    if (alternatives is not null)
                        values.AddRange(alternatives);
                }

                return errors.Count > 0 && values.Count == 0 && element.GetArrayLength() > 0 ? null : new BoundOperand.Set(values);

            case "regex":
                if (element.ValueKind != JsonValueKind.String)
                {
                    errors.Add(Operand("'regex' takes a pattern string.", stage, path, op, "string", element));
                    return null;
                }

                var pattern = element.GetString()!;
                var check = RegexGuard.Check(pattern, options.Limits.RegexMaxLength);

                if (check is { } failure)
                {
                    // A pattern over the host's length carries the limit, as every limit a request can meet does.
                    errors.Add(Error(failure.Code, failure.Message, stage, path.Wire) with
                    {
                        Params = contract2 && failure.Code == Codes.RegexTooLong && pattern.Length > options.Limits.RegexMaxLength
                            ? new Dictionary<string, object?> { ["limit"] = "regexMaxLength", ["max"] = options.Limits.RegexMaxLength, ["count"] = pattern.Length }
                            : null,
                    });
                    return null;
                }

                return new BoundOperand.Single(new BsonString(pattern));

            case "contains":
            case "startsWith":
            case "endsWith":
                if (element.ValueKind != JsonValueKind.String)
                {
                    errors.Add(Operand($"'{op}' takes a string.", stage, path, op, "string", element));
                    return null;
                }

                return new BoundOperand.Single(new BsonString(element.GetString()!));
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            if (op is "eq" or "neq")
                return BoundOperand.NullValue;

            errors.Add(Operand($"'{op}' does not take null.", stage, path, op, Kinds.NameOf(kind), element));
            return null;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            errors.Add(Operand($"'{Shown(path)}' expects {Expected(path)}, not an array; use 'in' for a set.", stage, path, op, Kinds.NameOf(kind), element));
            return null;
        }

        if (!InClosedList(element, path, kind, op, stage, errors, Shown(path)))
            return null;

        var scalar = CoerceScalar(element, path, kind, op, stage, errors, Shown(path));

        if (scalar is null)
            return null;

        return scalar.Count == 1 ? new BoundOperand.Single(scalar[0]) : new BoundOperand.Tolerant(scalar);
    }

    /// <summary>
    /// The pooled type whose variant an <c>is</c> tests at <paramref name="path"/>: the object the
    /// path holds, the element type of a collection of objects, or the entity of a local join
    /// alias. Null for a scalar, a remote path, or a path whose type is not known here. Whether
    /// the type has variants is the caller's check.
    /// </summary>
    public static TypeDef? VariantHolder(ResolvedPath path)
    {
        if (path is null || path.IsRemote)
            return null;

        // The object a derived member is compared on, named by the member's own description of it.
        if (path.Derived is { } derived && path.Path?.Derived is null)
            return derived.Holder;

        if (path.Shape is { } shape)
            return shape.Leaf is { Kind: Kind.Object, Type: { IsEnum: false } type } ? type : null;

        if (path.Path is not null)
            return null;

        return path.Root switch
        {
            ShapeNode.Entity entity => entity.Def.Root,
            ShapeNode.Array array => array.Target.Root,
            _ => null,
        };
    }

    /// <summary>
    /// The name <c>is</c> accepts for a polymorphic type's own values: the type's name when it is
    /// a concrete class, whose values are stored without a discriminator under their own nominal
    /// type. Null for an abstract class or an interface; for a type the model read from a document,
    /// the name the document publishes (<c>baseVariant</c>).
    /// </summary>
    public static string? ConcreteBaseName(TypeDef type)
    {
        if (type.ClrType is null)
            return type.BaseVariant;

        if (type.ClrType is not { IsAbstract: false, IsInterface: false } clr)
            return null;

        var arity = clr.Name.IndexOf('`', StringComparison.Ordinal);

        return arity > 0 ? clr.Name[..arity] : clr.Name;
    }

    /// <summary>
    /// The operand of an <c>is</c>: the discriminator values of every named variant and of every
    /// registered descendant of it, in the type's variant order, as a set. A named concrete base
    /// adds its own discriminator where the stored form is hierarchical, and <see cref="BsonNull"/>,
    /// which stands for a value stored without a discriminator (the compiler matches it as a
    /// missing discriminator element on an object). Null with errors added when a name is not
    /// one of the type's.
    /// </summary>
    public BoundOperand? CoerceVariants(JsonElement? raw, ResolvedPath path, TypeDef type, int stage, List<QueryValidationError> errors)
    {
        var element = raw ?? JsonNull;

        if (element.ValueKind == JsonValueKind.Object && TryVariable(element, out var variable))
        {
            if (!TryResolveVariable(variable!, out element))
            {
                errors.Add(Unbound(variable, stage, path.Wire));
                return null;
            }
        }

        var names = new List<string>();

        if (element.ValueKind == JsonValueKind.String)
            names.Add(element.GetString()!);
        else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0 && element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            names.AddRange(element.EnumerateArray().Select(item => item.GetString()!));
        else
        {
            errors.Add(Operand("'is' takes a variant name or a non-empty array of variant names.", stage, path, "is", "string", element));
            return null;
        }

        var baseName = ConcreteBaseName(type);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var includeBase = false;
        var failed = false;

        foreach (var name in names)
        {
            if (baseName is not null && string.Equals(name, baseName, StringComparison.Ordinal))
            {
                includeBase = true;
                selected.UnionWith(type.Variants.Select(variant => variant.Name));
                continue;
            }

            var named = type.Variants.FirstOrDefault(variant => string.Equals(variant.Name, name, StringComparison.Ordinal));

            if (named is null)
            {
                var known = type.Variants.Select(variant => variant.Name);

                if (baseName is not null)
                    known = known.Prepend(baseName);

                errors.Add(Error(Codes.UnknownVariant, $"'{name}' is not a variant of '{Shown(path)}'; its variants are {string.Join(", ", known)}.", stage, path.Wire) with
                {
                    Params = contract2 ? new Dictionary<string, object?> { ["path"] = path.Wire, ["variant"] = name, ["variants"] = known.ToList() } : null,
                });
                failed = true;
                continue;
            }

            selected.Add(named.Name);

            foreach (var descendant in type.Variants.Where(candidate => IsDescendant(candidate, named)))
                selected.Add(descendant.Name);
        }

        if (failed)
            return null;

        var values = new List<BsonValue>();

        if (includeBase && type.DiscriminatorForm == DiscriminatorForm.Hierarchical)
            values.Add(new BsonString(type.Discriminator ?? baseName!));

        foreach (var variant in type.Variants)
            if (selected.Contains(variant.Name) && !values.Contains(new BsonString(variant.Discriminator)))
                values.Add(new BsonString(variant.Discriminator));

        if (includeBase)
            values.Add(BsonNull.Value);

        return new BoundOperand.Set(values);
    }

    /// <summary>Whether <paramref name="candidate"/> derives from <paramref name="ancestor"/> (see the private rule <c>is</c> selects by).</summary>
    public static bool Descends(VariantDef candidate, VariantDef ancestor) => IsDescendant(candidate, ancestor);

    /// <summary>Whether <paramref name="candidate"/> derives from <paramref name="ancestor"/>: by the CLR types, or by the ancestor's own variants when the model came from a document.</summary>
    private static bool IsDescendant(VariantDef candidate, VariantDef ancestor)
    {
        if (ReferenceEquals(candidate, ancestor))
            return false;

        if (ancestor.Type.ClrType is { } ancestorType && candidate.Type.ClrType is { } candidateType)
            return candidateType != ancestorType && ancestorType.IsAssignableFrom(candidateType);

        return ancestor.Type.Variants.Any(variant => string.Equals(variant.Name, candidate.Name, StringComparison.Ordinal));
    }

    /// <summary>The literal of a group expression, converted by JSON kind alone: it is not compared against a member.</summary>
    public static BsonValue Literal(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new BsonString(element.GetString()!),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? new BsonInt64(integer) : new BsonDouble(element.GetDouble()),
        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,
        JsonValueKind.Null => BsonNull.Value,
        JsonValueKind.Array => new BsonArray(element.EnumerateArray().Select(Literal)),
        _ => BsonNull.Value,
    };

    /// <summary>
    /// A raw JSON value another binder receives — a remote resolve's filter, a continued stage —
    /// with every variable wrapper <c>{ "$var": name }</c> in it replaced by the bound value:
    /// the owner never receives <c>variables</c>, so what it is sent must hold
    /// none. A value without a wrapper comes back as it is, so its render is unchanged. An unbound
    /// name is <c>UNBOUND_VARIABLE</c>, one holding an object <c>INVALID_VARIABLE</c>, both at
    /// <paramref name="stage"/> and <paramref name="path"/>; the wrapper then stays in place.
    /// </summary>
    public JsonElement SubstituteVariables(JsonElement raw, int stage, string? path, List<QueryValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (!HoldsVariable(raw))
            return raw;

        // A filter nests as deep as a request may, which is deeper than the serializer's own default.
        return JsonSerializer.SerializeToElement(Substituted(raw, stage, path, errors), OxQLJson.Wire);
    }

    /// <summary>Whether a raw JSON value holds a variable wrapper anywhere.</summary>
    public static bool HoldsVariable(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.Object => TryVariable(raw, out _) || raw.EnumerateObject().Any(property => HoldsVariable(property.Value)),
        JsonValueKind.Array => raw.EnumerateArray().Any(HoldsVariable),
        _ => false,
    };

    private JsonNode? Substituted(JsonElement element, int stage, string? path, List<QueryValidationError> errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when TryVariable(element, out var name):
                if (!TryResolveVariable(name!, out var value))
                {
                    errors.Add(Unbound(name, stage, path));
                    return JsonNode.Parse(element.GetRawText());
                }

                if (value.ValueKind == JsonValueKind.Object)
                {
                    errors.Add(Error(Codes.InvalidVariable, $"The variable '{name}' holds an object; a variable holds a value or an array.", stage, path));
                    return JsonNode.Parse(element.GetRawText());
                }

                return JsonNode.Parse(value.GetRawText());

            case JsonValueKind.Object:
                var node = new JsonObject();

                foreach (var property in element.EnumerateObject())
                    node[property.Name] = Substituted(property.Value, stage, path, errors);

                return node;

            case JsonValueKind.Array:
                return new JsonArray(element.EnumerateArray().Select(item => Substituted(item, stage, path, errors)).ToArray());

            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }

    /// <summary>Resolves a variable for a group expression.</summary>
    public bool TryResolveVariable(string name, out JsonElement value)
    {
        value = default;

        if (variables is null || !variables.Values.TryGetValue(name, out var raw))
            return false;

        value = raw switch
        {
            JsonElement element => element,
            null => JsonNull,
            _ => JsonSerializer.SerializeToElement(raw),
        };

        return true;
    }

    private static JsonElement ParseNull()
    {
        using var document = JsonDocument.Parse("null");

        return document.RootElement.Clone();
    }

    private static bool TryVariable(JsonElement element, out string? name)
    {
        name = null;
        var count = 0;

        foreach (var property in element.EnumerateObject())
        {
            count++;

            if (property.Name == VarKey && property.Value.ValueKind == JsonValueKind.String)
                name = property.Value.GetString();
        }

        return count == 1 && name is not null;
    }

    private IReadOnlyList<BsonValue>? CoerceScalar(JsonElement element, ResolvedPath path, Kind kind, string op, int stage, List<QueryValidationError> errors, string label)
    {
        var representation = path.Leaf?.Representation ?? Representation.None;

        // A defined addon key at the top of the row is compared by the compiler over its value
        // read wrapped or plain and converted to the key's kind, so its operand is the one typed
        // value. Through a collection (a looked-up child's bag), where no expression can be asked,
        // the key is matched as stored: against each form a value of the kind is written in.
        var addon = path.Addon is not null && path.CollectionAncestors > 0;
        string? failure = null;

        switch (kind)
        {
            case Kind.String:
                if (element.ValueKind != JsonValueKind.String)
                    break;

                var text = element.GetString()!;

                // A char is stored as its code point.
                if (representation.BsonType == BsonType.Int32 && text.Length == 1)
                    return [new BsonInt32(text[0])];

                if (representation.BsonType == BsonType.Int32 && contract2)
                {
                    failure = "a single character";
                    break;
                }

                return [new BsonString(text)];

            case Kind.Int:
            case Kind.Long:
                if (TryInteger(element, out var integer))
                    return addon ? [new BsonInt64(integer), new BsonString(integer.ToString(CultureInfo.InvariantCulture))] : [new BsonInt64(integer)];

                failure = "an integer";
                break;

            case Kind.Double:
                if (TryDouble(element, out var floating))
                    return addon ? [new BsonDouble(floating), new BsonString(element.ValueKind == JsonValueKind.String ? element.GetString()! : floating.ToString("R", CultureInfo.InvariantCulture))] : [new BsonDouble(floating)];

                failure = "a number";
                break;

            case Kind.Decimal:
                if (TryDecimal(element, contract2, out var money))
                    return Decimal(money, path, op, representation, addon, stage, errors, label);

                failure = "a decimal number";
                break;

            case Kind.Bool:
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return Bool(element.ValueKind == JsonValueKind.True, addon);

                if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed))
                    return Bool(parsed, addon);

                failure = "a boolean";
                break;

            case Kind.Guid:
                if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var guid))
                {
                    if (representation.BsonType == BsonType.String)
                        return [new BsonString(guid.ToString())];

                    var standard = new BsonBinaryData(guid, representation.GuidRepresentation is { } declared && declared != GuidRepresentation.Unspecified ? declared : GuidRepresentation.Standard);

                    // The database converts no text to a GUID before 8.0: a defined key is matched
                    // as either form wherever it lies.
                    if (path.Addon is not null)
                        return [standard, new BsonString(guid.ToString())];

                    return options.Representation.GuidTolerant
                        ? [standard, new BsonBinaryData(guid, GuidRepresentation.CSharpLegacy), new BsonString(guid.ToString())]
                        : [standard];
                }

                failure = "a GUID string";
                break;

            case Kind.Date:
                if (element.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    if (representation.BsonType == BsonType.String)
                        return [new BsonString(element.GetString()!)];

                    var midnight = new BsonDateTime(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

                    return [midnight];
                }

                // Written as a date and no day of the calendar (a 30th of February) is said as that.
                failure = element.ValueKind == JsonValueKind.String && IsDateShaped(element.GetString()!)
                    ? $"a day of the calendar; '{element.GetString()}' is written as a date and names none"
                    : "a date as YYYY-MM-DD";
                break;

            case Kind.DateTime:
                if (element.ValueKind == JsonValueKind.String && TryDateTime(element.GetString()!, out var instant))
                {
                    var stored = new BsonDateTime(instant);

                    return addon ? [stored, new BsonString(element.GetString()!)] : [stored];
                }

                failure = element.ValueKind == JsonValueKind.String && HasExplicitOffset(element.GetString()!) && IsDateShaped(element.GetString()![..10])
                    ? $"a date and time that exist; '{element.GetString()}' is written as an ISO-8601 date-time and names no day of the calendar or no time of day"
                    : "an ISO-8601 date-time with 'Z' or an offset";
                break;

            case Kind.TimeSpan:
                if (element.ValueKind == JsonValueKind.String && TryDuration(element.GetString()!, out var span))
                    return representation.BsonType switch
                    {
                        BsonType.Int64 => [new BsonInt64(span.Ticks)],
                        BsonType.Double => [new BsonDouble(span.TotalMilliseconds)],
                        _ => [new BsonString(span.ToString("c", CultureInfo.InvariantCulture))],
                    };

                failure = "an ISO-8601 duration";
                break;

            case Kind.Enum:
                var type = path.Leaf?.Type;

                if (TryInteger(element, out var number))
                {
                    // The membership check does not depend on the storage: a number that names
                    // no declared member is as wrong on a numerically stored enum as on a
                    // string-stored one. Passed on unchecked it matches nothing, or an
                    // undeclared stored value, with a 200.
                    var byValue = type?.EnumValues.FirstOrDefault(value => value.Value == number);

                    if (byValue is null)
                    {
                        errors.Add(NoMember($"'{number}' is not a member of the enum at '{label}'.", stage, path, op, number, type?.EnumValues.Select(value => value.Name)));
                        return null;
                    }

                    if (representation.BsonType == BsonType.String)
                        return [new BsonString(byValue.Name)];

                    return Enumeral(byValue.Value, representation, path, op, stage, errors, label, element);
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    var name = element.GetString()!;
                    var member = type?.EnumValues.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.Ordinal));

                    if (member is null)
                    {
                        errors.Add(NoMember($"'{name}' is not a member of the enum at '{label}'.", stage, path, op, name, type?.EnumValues.Select(value => value.Name)));
                        return null;
                    }

                    if (representation.BsonType == BsonType.String)
                        return [new BsonString(member.Name)];

                    return Enumeral(member.Value, representation, path, op, stage, errors, label, element);
                }

                failure = "an enum member name or number";
                break;

            case Kind.Binary:
                if (element.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        return [new BsonBinaryData(Convert.FromBase64String(element.GetString()!))];
                    }
                    catch (FormatException)
                    {
                        // falls through to the failure
                    }
                }

                failure = "a base64 string";
                break;

            default:
                failure = "a scalar";
                break;
        }

        errors.Add(Operand($"'{label}' expects {failure ?? Expected(path)}.", stage, path, op, Kinds.NameOf(kind), element));
        return null;
    }

    /// <summary>Whether a text is written as a date: four digits, two and two, with hyphens between.</summary>
    private static bool IsDateShaped(string text) =>
        text.Length == 10 && text[4] == '-' && text[7] == '-' && text.Where((_, index) => index is not (4 or 7)).All(char.IsAsciiDigit);

    /// <summary>
    /// The alternatives a decimal operand is compared against.
    /// <para>
    /// The typed bracket is a <c>Decimal128</c> and is exact. The text bracket — rows written
    /// before the migration — is reached by an anchored regex over every scale the driver
    /// could have written the value with, because one canonical spelling cannot match
    /// <c>"125000.00"</c> and <c>"125000"</c> at once and the value the service itself returns
    /// must find the row it came from.
    /// </para>
    /// <para>
    /// An <b>ordered</b> comparison gets no text bracket at all. Mongo compares a string
    /// bracket lexicographically, which agrees with numeric order only while every value
    /// shares one integer width and one scale: as text, <c>lt "100000"</c> drops
    /// <c>"99999.99"</c> and <c>gte "99999.99"</c> drops <c>"125000.00"</c>. Numbers only is what <c>$sum</c>,
    /// <c>$min</c> and <c>$max</c> already answer over the same member, it keeps the index,
    /// and it cannot return a row on the wrong side of the bound; the rows it does not reach
    /// are named in a diagnostic rather than silently mis-ordered. Where <em>every</em> row is
    /// text — the member's declared representation is <c>String</c> — there is no typed
    /// bracket to fall back to and no answer that is not a guess, so the range is refused.
    /// </para>
    /// </summary>
    private IReadOnlyList<BsonValue>? Decimal(
        decimal money,
        ResolvedPath path,
        string op,
        Representation representation,
        bool addon,
        int stage,
        List<QueryValidationError> errors,
        string label)
    {
        var text = new BsonRegularExpression(DecimalText.ScalePattern(money));
        var ordered = OrderedOperators.Contains(op);

        if (representation.BsonType == BsonType.String)
        {
            if (!ordered)
                return [text];

            errors.Add(Error(
                Codes.DecimalTextNotOrderable,
                $"'{label}' stores its decimal as text, which orders by characters rather than by value; '{op}' over it cannot be answered correctly. Compare it with eq, neq, in or nin, or migrate the member to Decimal128.",
                stage,
                path.Wire));

            return null;
        }

        var typed = new BsonDecimal128(new Decimal128(money));

        // A defined key compared over its converted value has no text to reach.
        if (path.Addon is not null && !addon)
            return [typed];

        if (!addon && !options.Representation.DecimalTolerant)
            return [typed];

        if (!ordered)
            return [typed, text];

        diagnostics?.Add(new Diagnostic
        {
            Code = Codes.DecimalTextExcluded,
            Message = $"'{label}' may hold decimals written as text; '{op}' covers the numerically stored rows only, because text does not order by value.",
            Stage = stage,
            Path = path.Wire,
        });

        return [typed];
    }

    /// <summary>
    /// An enum's stored number. <c>TryInteger</c> accepts the operand as a <c>long</c>, so a
    /// value outside <c>Int32</c> under an <c>Int32</c> representation is checked here and
    /// refused, the way a value above <c>long.MaxValue</c> is refused one parse earlier.
    /// </summary>
    private IReadOnlyList<BsonValue>? Enumeral(long value, Representation representation, ResolvedPath path, string op, int stage, List<QueryValidationError> errors, string label, JsonElement written)
    {
        if (representation.BsonType == BsonType.Int64)
            return [new BsonInt64(value)];

        if (value is < int.MinValue or > int.MaxValue)
        {
            errors.Add(Operand(
                $"'{value}' is outside the range the enum at '{label}' is stored in ({int.MinValue} to {int.MaxValue}).",
                stage,
                path,
                op,
                Kinds.NameOf(Kind.Enum),
                written));

            return null;
        }

        return [new BsonInt32((int)value)];
    }

    /// <summary>
    /// A defined addon key with a closed value list accepts only its values on <c>eq</c>,
    /// <c>neq</c>, <c>in</c> and <c>nin</c>: a string as written, an integer by its digits.
    /// Anything else is <c>UNKNOWN_ENUM_MEMBER</c>, the same refusal an enum member off the list gets.
    /// </summary>
    private bool InClosedList(JsonElement element, ResolvedPath path, Kind kind, string op, int stage, List<QueryValidationError> errors, string label)
    {
        if (path.Addon?.Values is not { Count: > 0 } allowed || !ClosedListOperators.Contains(op))
            return true;

        var written = kind switch
        {
            Kind.Int or Kind.Long => TryInteger(element, out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : null,
            _ => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? element.GetRawText() : null,
        };

        if (written is not null && allowed.Any(value => string.Equals(value.Value, written, StringComparison.Ordinal)))
            return true;

        errors.Add(NoMember(
            $"'{written ?? element.GetRawText()}' is not one of the values defined for '{label}': {string.Join(", ", allowed.Select(value => value.Value))}.",
            stage,
            path,
            op,
            written ?? element.GetRawText(),
            allowed.Select(value => value.Value)));

        return false;
    }

    private static IReadOnlyList<BsonValue> Bool(bool value, bool addon) =>
        addon ? [BsonBoolean.Create(value), new BsonString(value ? "true" : "false")] : [BsonBoolean.Create(value)];

    private static string Expected(ResolvedPath path) => path.LeafKind switch
    {
        Kind.String => "a string",
        Kind.Int or Kind.Long => "an integer",
        Kind.Double => "a number",
        Kind.Decimal => "a decimal number",
        Kind.Bool => "a boolean",
        Kind.Guid => "a GUID string",
        Kind.Date => "a date as YYYY-MM-DD",
        Kind.DateTime => "an ISO-8601 date-time with 'Z' or an offset",
        Kind.TimeSpan => "an ISO-8601 duration",
        Kind.Enum => "an enum member name or number",
        Kind.Binary => "a base64 string",
        _ => "a scalar",
    };

    private static bool TryInteger(JsonElement element, out long value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryDouble(JsonElement element, out double value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>A decimal as text under contract 2: no group separator, no white space, no trailing sign.</summary>
    private const NumberStyles StrictDecimal = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    private static bool TryDecimal(JsonElement element, bool contract2, out decimal value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(element.GetString(), contract2 ? StrictDecimal : NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>An ISO-8601 date-time with an explicit 'Z' or offset; a local time is refused.</summary>
    public static bool TryDateTime(string text, out DateTime utc)
    {
        utc = default;

        if (!HasExplicitOffset(text))
            return false;

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset))
            return false;

        utc = offset.UtcDateTime;
        return true;
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.Length < 11 || value[10] != 'T')
            return false;

        if (value.EndsWith('Z') || value.EndsWith('z'))
            return true;

        if (value.Length < 6)
            return false;

        var tail = value[^6..];

        return (tail[0] == '+' || tail[0] == '-') && char.IsDigit(tail[1]) && char.IsDigit(tail[2]) && tail[3] == ':' && char.IsDigit(tail[4]) && char.IsDigit(tail[5]);
    }

    /// <summary>
    /// An ISO-8601 duration. A year in it counts as 365 days and a month as 30. It is kept to 100
    /// nanoseconds, the precision a stored duration has; finer digits of a second are dropped.
    /// </summary>
    public static bool TryDuration(string text, out TimeSpan span)
    {
        span = default;

        if (!text.StartsWith('P') && !text.StartsWith("-P", StringComparison.Ordinal))
            return false;

        try
        {
            span = XmlConvert.ToTimeSpan(text);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            // A duration that is not one, or one a TimeSpan cannot hold.
            return false;
        }
    }

    private static QueryValidationError Error(string code, string message, int stage, string? path) =>
        new() { Code = code, Message = message, Stage = stage, Path = path };

    /// <summary>A variable the request does not bind. Under contract 2 it carries the variable's <c>name</c>.</summary>
    private QueryValidationError Unbound(string? name, int stage, string? path) =>
        Error(Codes.UnboundVariable, $"The variable '{name}' is not bound.", stage, path) with
        {
            Params = contract2 ? new Dictionary<string, object?> { ["name"] = name } : null,
        };

    /// <summary>
    /// An operand of another kind than the member or the operator takes. Under contract 2 it carries
    /// what a client renders it from: the path, the operator, the kind the place takes (a kind of
    /// the schema: <c>string</c>, <c>int</c>, <c>dateTime</c>, <c>enum</c>; <c>boolean</c> for
    /// <c>exists</c>) and the JSON kind that was written.
    /// </summary>
    private QueryValidationError Operand(string message, int stage, ResolvedPath path, string op, string expected, JsonElement actual) =>
        Error(Codes.InvalidOperand, message, stage, path.Wire) with
        {
            Params = contract2 ? new Dictionary<string, object?> { ["path"] = path.Wire, ["operator"] = op, ["expected"] = expected, ["actual"] = JsonKind(actual) } : null,
        };

    /// <summary>A value that is none of a closed list: an enum's members, or the values an organisation defined for an addon key. Under contract 2 it carries the list.</summary>
    private QueryValidationError NoMember(string message, int stage, ResolvedPath path, string op, object value, IEnumerable<string>? values) =>
        Error(Codes.UnknownEnumMember, message, stage, path.Wire) with
        {
            Params = contract2 ? new Dictionary<string, object?> { ["path"] = path.Wire, ["operator"] = op, ["value"] = value, ["values"] = (values ?? []).ToList() } : null,
        };

    /// <summary>The JSON kind of a written value, as an error names it.</summary>
    internal static string JsonKind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };
}
