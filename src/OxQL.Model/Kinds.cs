namespace OxQL.Model;

/// <summary>
/// The kind vocabulary of the model, the same one the Ox Schema publishes. Scalars are what a
/// filter operand can be encoded for; <see cref="Object"/>, <see cref="Array"/> and
/// <see cref="Dictionary"/> are composites the path index walks through; <see cref="Unknown"/>
/// is a member whose shape neither the registry nor the document can describe.
/// </summary>
public enum Kind
{
    /// <summary>A string.</summary>
    String,

    /// <summary>A 32-bit or narrower integer.</summary>
    Int,

    /// <summary>A 64-bit integer, a JSON string on the wire.</summary>
    Long,

    /// <summary>A floating-point number.</summary>
    Double,

    /// <summary>A decimal, a JSON string on the wire.</summary>
    Decimal,

    /// <summary>A boolean.</summary>
    Bool,

    /// <summary>A GUID string.</summary>
    Guid,

    /// <summary>A calendar date, <c>YYYY-MM-DD</c> on the wire.</summary>
    Date,

    /// <summary>A date and time, ISO-8601 UTC on the wire.</summary>
    DateTime,

    /// <summary>A duration, ISO-8601 on the wire.</summary>
    TimeSpan,

    /// <summary>An enum; the descriptor points at the pooled enum entry.</summary>
    Enum,

    /// <summary>Binary data, base64 on the wire.</summary>
    Binary,

    /// <summary>An object; the descriptor points at the pooled entry.</summary>
    Object,

    /// <summary>An array; the descriptor carries the element shape.</summary>
    Array,

    /// <summary>A dictionary with caller-controlled keys; the descriptor carries the value shape.</summary>
    Dictionary,

    /// <summary>A member the model cannot describe: projectable as is, never filterable or sortable.</summary>
    Unknown,
}

/// <summary>The wire spellings of <see cref="Kind"/> and the CLR mapping the schema defines.</summary>
public static class Kinds
{
    private static readonly Dictionary<Kind, string> Names = new()
    {
        [Kind.String] = "string",
        [Kind.Int] = "int",
        [Kind.Long] = "long",
        [Kind.Double] = "double",
        [Kind.Decimal] = "decimal",
        [Kind.Bool] = "bool",
        [Kind.Guid] = "guid",
        [Kind.Date] = "date",
        [Kind.DateTime] = "dateTime",
        [Kind.TimeSpan] = "timeSpan",
        [Kind.Enum] = "enum",
        [Kind.Binary] = "binary",
        [Kind.Object] = "object",
        [Kind.Array] = "array",
        [Kind.Dictionary] = "dictionary",
        [Kind.Unknown] = "unknown",
    };

    private static readonly Dictionary<string, Kind> ByName =
        Names.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>
    /// The leaf boundary and the scalar vocabulary in one table, identical to the schema's:
    /// narrow integers collapse onto <c>int</c>, unsigned 64-bit onto <c>long</c>, <c>object</c>
    /// is <c>unknown</c>, a time of day has no kind.
    /// </summary>
    private static readonly Dictionary<Type, Kind> ScalarKinds = new()
    {
        [typeof(string)] = Kind.String,
        [typeof(char)] = Kind.String,
        [typeof(Uri)] = Kind.String,
        [typeof(bool)] = Kind.Bool,
        [typeof(sbyte)] = Kind.Int,
        [typeof(byte)] = Kind.Int,
        [typeof(short)] = Kind.Int,
        [typeof(ushort)] = Kind.Int,
        [typeof(int)] = Kind.Int,
        [typeof(uint)] = Kind.Long,
        [typeof(long)] = Kind.Long,
        [typeof(ulong)] = Kind.Long,
        [typeof(float)] = Kind.Double,
        [typeof(double)] = Kind.Double,
        [typeof(decimal)] = Kind.Decimal,
        [typeof(Guid)] = Kind.Guid,
        [typeof(DateOnly)] = Kind.Date,
        [typeof(DateTime)] = Kind.DateTime,
        [typeof(DateTimeOffset)] = Kind.DateTime,
        [typeof(TimeSpan)] = Kind.TimeSpan,
        [typeof(byte[])] = Kind.Binary,
        [typeof(TimeOnly)] = Kind.Unknown,
        [typeof(object)] = Kind.Unknown,
    };

    /// <summary>Namespaces holding serializer and runtime plumbing rather than model shape; a type in one is <c>unknown</c>.</summary>
    private static readonly string[] OpaqueNamespaces = ["MongoDB.Bson", "System.Text.Json", "System.Reflection", "System.IO"];

    /// <summary>The wire spelling of a kind.</summary>
    public static string NameOf(Kind kind) => Names[kind];

    /// <summary>Parses a wire spelling; null when it is not a kind.</summary>
    public static Kind? Parse(string? name) =>
        name is not null && ByName.TryGetValue(name, out var kind) ? kind : null;

    /// <summary>Whether a filter operand can be encoded for the kind: every scalar and <see cref="Kind.Enum"/>.</summary>
    public static bool IsScalar(Kind kind) => kind is not (Kind.Object or Kind.Array or Kind.Dictionary or Kind.Unknown);

    /// <summary>
    /// The scalar kind of a CLR type, or null for a composite the walk descends into. A
    /// <c>Nullable&lt;T&gt;</c> must be unwrapped first; an enum is <see cref="Kind.Enum"/>.
    /// </summary>
    public static Kind? ScalarKindOf(Type type)
    {
        if (type.IsEnum)
            return Kind.Enum;

        if (ScalarKinds.TryGetValue(type, out var kind))
            return kind;

        if (type.IsPrimitive || type.IsPointer || type.IsByRef || type.IsGenericParameter
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(Type).IsAssignableFrom(type)
            || IsOpaqueNamespace(type))
            return Kind.Unknown;

        return null;
    }

    private static bool IsOpaqueNamespace(Type type) =>
        type.Namespace is { } ns
        && OpaqueNamespaces.Any(opaque =>
            ns.Equals(opaque, StringComparison.Ordinal) || ns.StartsWith(opaque + ".", StringComparison.Ordinal));
}
