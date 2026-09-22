using System.Globalization;
using System.Text.Json;
using System.Xml;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Core.Binding;

/// <summary>
/// Encodes an operand for storage from the member's kind and representation. Nothing is
/// guessed from the JSON alone: a wrong JSON kind is <c>INVALID_OPERAND</c> naming the expected
/// kind. Tolerant forms (decimal in two storage forms, defined addon keys, optionally guids)
/// come out as alternatives the compiler turns into an <c>$or</c>.
/// </summary>
public sealed class OperandCoercer
{
    private const string VarKey = "$var";

    /// <summary>The JSON literal <c>null</c>, detached from the document it was parsed from.</summary>
    internal static readonly JsonElement JsonNull = ParseNull();

    private readonly OxQLOptions options;
    private readonly QueryVariables? variables;
    private readonly List<Diagnostic>? diagnostics;

    public OperandCoercer(OxQLOptions options, QueryVariables? variables, List<Diagnostic>? diagnostics = null)
    {
        this.options = options;
        this.variables = variables;
        this.diagnostics = diagnostics;
    }

    /// <summary>The operators, case-sensitive.</summary>
    public static readonly IReadOnlySet<string> Operators = new HashSet<string>(StringComparer.Ordinal)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex",
    };

    private static readonly IReadOnlySet<string> StringOperators = new HashSet<string>(StringComparer.Ordinal) { "contains", "startsWith", "endsWith", "regex" };
    private static readonly IReadOnlySet<string> OrderedOperators = new HashSet<string>(StringComparer.Ordinal) { "gt", "gte", "lt", "lte" };
    private static readonly IReadOnlySet<string> SetOperators = new HashSet<string>(StringComparer.Ordinal) { "in", "nin" };
    private static readonly IReadOnlySet<string> ClosedListOperators = new HashSet<string>(StringComparer.Ordinal) { "eq", "neq", "in", "nin" };
    private static readonly IReadOnlySet<string> IgnoreCaseOperators = new HashSet<string>(StringComparer.Ordinal) { "eq", "neq", "in", "nin", "contains", "startsWith", "endsWith" };

    /// <summary>Whether <paramref name="op"/> applies to <paramref name="kind"/>.</summary>
    public static bool Applies(string op, Kind kind)
    {
        if (StringOperators.Contains(op))
            return kind == Kind.String;

        if (OrderedOperators.Contains(op))
            return kind is Kind.Int or Kind.Long or Kind.Double or Kind.Decimal or Kind.Date or Kind.DateTime or Kind.TimeSpan or Kind.String or Kind.Enum;

        return true;
    }

    /// <summary>Whether <c>ignoreCase</c> applies to <paramref name="op"/> on <paramref name="kind"/>.</summary>
    public static bool IgnoreCaseApplies(string op, Kind kind) => kind == Kind.String && IgnoreCaseOperators.Contains(op);

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

                errors.Add(Error(Codes.InvalidOperand, $"'{path.Wire}' expects {Expected(path)}, not an object.", stage, path.Wire));
                return null;
            }

            if (!TryResolveVariable(name!, out element))
            {
                errors.Add(Error(Codes.UnboundVariable, $"The variable '{name}' is not bound.", stage, path.Wire));
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

                errors.Add(Error(Codes.InvalidOperand, $"'exists' takes a boolean.", stage, path.Wire));
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
                    var label = $"{path.Wire}[{index++}]";

                    // An element may be a variable wrapper of its own: the whole-array form and
                    // the per-element form are both part of the contract.
                    if (item.ValueKind == JsonValueKind.Object && TryVariable(item, out var itemName))
                    {
                        if (!TryResolveVariable(itemName!, out item))
                        {
                            errors.Add(Error(Codes.UnboundVariable, $"The variable '{itemName}' is not bound.", stage, path.Wire));
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
                    errors.Add(Error(Codes.InvalidOperand, "'regex' takes a pattern string.", stage, path.Wire));
                    return null;
                }

                var pattern = element.GetString()!;
                var check = RegexGuard.Check(pattern, options.Limits.RegexMaxLength);

                if (check is { } failure)
                {
                    errors.Add(Error(failure.Code, failure.Message, stage, path.Wire));
                    return null;
                }

                return new BoundOperand.Single(new BsonString(pattern));

            case "contains":
            case "startsWith":
            case "endsWith":
                if (element.ValueKind != JsonValueKind.String)
                {
                    errors.Add(Error(Codes.InvalidOperand, $"'{op}' takes a string.", stage, path.Wire));
                    return null;
                }

                return new BoundOperand.Single(new BsonString(element.GetString()!));
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            if (op is "eq" or "neq")
                return BoundOperand.NullValue;

            errors.Add(Error(Codes.InvalidOperand, $"'{op}' does not take null.", stage, path.Wire));
            return null;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            errors.Add(Error(Codes.InvalidOperand, $"'{path.Wire}' expects {Expected(path)}, not an array; use 'in' for a set.", stage, path.Wire));
            return null;
        }

        if (!InClosedList(element, path, kind, op, stage, errors, path.Wire))
            return null;

        var scalar = CoerceScalar(element, path, kind, op, stage, errors, path.Wire);

        if (scalar is null)
            return null;

        return scalar.Count == 1 ? new BoundOperand.Single(scalar[0]) : new BoundOperand.Tolerant(scalar);
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
        var addon = path.Addon is not null;
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
                if (TryDecimal(element, out var money))
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

                    if (addon)
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

                    return addon ? [midnight, new BsonString(element.GetString()!)] : [midnight];
                }

                failure = "a date as YYYY-MM-DD";
                break;

            case Kind.DateTime:
                if (element.ValueKind == JsonValueKind.String && TryDateTime(element.GetString()!, out var instant))
                {
                    var stored = new BsonDateTime(instant);

                    return addon ? [stored, new BsonString(element.GetString()!)] : [stored];
                }

                failure = "an ISO-8601 date-time with 'Z' or an offset";
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
                        errors.Add(Error(Codes.UnknownEnumMember, $"'{number}' is not a member of the enum at '{label}'.", stage, path.Wire));
                        return null;
                    }

                    if (representation.BsonType == BsonType.String)
                        return [new BsonString(byValue.Name)];

                    return Enumeral(byValue.Value, representation, path, stage, errors, label);
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    var name = element.GetString()!;
                    var member = type?.EnumValues.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.Ordinal));

                    if (member is null)
                    {
                        errors.Add(Error(Codes.UnknownEnumMember, $"'{name}' is not a member of the enum at '{label}'.", stage, path.Wire));
                        return null;
                    }

                    if (representation.BsonType == BsonType.String)
                        return [new BsonString(member.Name)];

                    return Enumeral(member.Value, representation, path, stage, errors, label);
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

        errors.Add(Error(Codes.InvalidOperand, $"'{label}' expects {failure ?? Expected(path)}.", stage, path.Wire));
        return null;
    }

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
    private static IReadOnlyList<BsonValue>? Enumeral(long value, Representation representation, ResolvedPath path, int stage, List<QueryValidationError> errors, string label)
    {
        if (representation.BsonType == BsonType.Int64)
            return [new BsonInt64(value)];

        if (value is < int.MinValue or > int.MaxValue)
        {
            errors.Add(Error(
                Codes.InvalidOperand,
                $"'{value}' is outside the range the enum at '{label}' is stored in ({int.MinValue} to {int.MaxValue}).",
                stage,
                path.Wire));

            return null;
        }

        return [new BsonInt32((int)value)];
    }

    /// <summary>
    /// A defined addon key with a closed value list accepts only its values on <c>eq</c>,
    /// <c>neq</c>, <c>in</c> and <c>nin</c>: a string as written, an integer by its digits.
    /// Anything else is <c>UNKNOWN_ENUM_MEMBER</c>, the same refusal an enum member off the list gets.
    /// </summary>
    private static bool InClosedList(JsonElement element, ResolvedPath path, Kind kind, string op, int stage, List<QueryValidationError> errors, string label)
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

        errors.Add(Error(
            Codes.UnknownEnumMember,
            $"'{written ?? element.GetRawText()}' is not one of the values defined for '{label}': {string.Join(", ", allowed.Select(value => value.Value))}.",
            stage,
            path.Wire));

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

    private static bool TryDecimal(JsonElement element, out decimal value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value),
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

    /// <summary>An ISO-8601 duration.</summary>
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
}
