namespace OxQL.Model.Addon;

/// <summary>
/// One organisation's definition of a key under an entity's addon bag: a hint that the value
/// is most likely of <see cref="Kind"/>. Nothing is refused on write and nothing is normalised;
/// the engine carries the uncertainty on the read side: it reads the key wrapped or plain and
/// converts the value to the kind, and a value that does not convert counts as empty.
/// </summary>
public sealed record AddonDefinition
{
    /// <summary>The definition's own id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The extendable entity id the key lives under.</summary>
    public required string Entity { get; init; }

    /// <summary>
    /// The storage path under the bag, verbatim, dot-separated; segments may contain spaces and
    /// the driver's <c>_t</c>/<c>_v</c> wrappers are ordinary segments. May point inside a subobject.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>The kind: a scalar, or <see cref="AddonKind.Object"/> for an explicit container with no typing of its own.</summary>
    public required AddonKind Kind { get; init; }

    /// <summary>An optional closed value list with labels on <c>string</c> and <c>int</c> keys.</summary>
    public IReadOnlyList<AddonValue>? Values { get; init; }

    /// <summary>The human label.</summary>
    public string? DisplayName { get; init; }

    /// <summary>A description.</summary>
    public string? Description { get; init; }

    /// <summary>Soft delete: a retired key is opaque <c>unknown</c> again.</summary>
    public bool Retired { get; init; }
}

/// <summary>One entry of a closed value list.</summary>
public sealed record AddonValue(string Value, string? Label);

/// <summary>The kinds an addon definition may declare.</summary>
public enum AddonKind
{
    /// <summary>A string.</summary>
    String,

    /// <summary>A 32-bit integer; any number, or text holding an integer, converts to it.</summary>
    Int,

    /// <summary>A 64-bit integer; any number, or text holding an integer, converts to it.</summary>
    Long,

    /// <summary>A floating-point number; any number, or text holding a number, converts to it.</summary>
    Double,

    /// <summary>A decimal; any number, or text holding a number, converts to it.</summary>
    Decimal,

    /// <summary>A boolean, by the database's conversion: a number is true unless it is zero, and any text is true, <c>"false"</c> included.</summary>
    Bool,

    /// <summary>A date, which a bag holds as an instant: it binds as a date and time (<see cref="AddonKinds.ToKind"/>).</summary>
    Date,

    /// <summary>A date and time; a BSON date, or text holding an ISO-8601 date and time or date, converts to it.</summary>
    DateTime,

    /// <summary>A GUID; matched as binary or as its text, which the database does not convert into one another.</summary>
    Guid,

    /// <summary>An explicit container with no typing of its own; its defined descendants are typed, it is not.</summary>
    Object,
}

/// <summary>Maps addon kinds onto the model's kind vocabulary.</summary>
public static class AddonKinds
{
    /// <summary>
    /// The model kind an addon kind binds as; <see cref="AddonKind.Object"/> is <see cref="Model.Kind.Unknown"/>.
    /// A <see cref="AddonKind.Date"/> is a <see cref="Model.Kind.DateTime"/>: a bag stores a date as the
    /// instant its writer had, which is the midnight of no zone in particular, so the key binds,
    /// compares, sorts and is published as that instant, and a caller that means a day of its own
    /// calendar asks for the instants the day spans.
    /// </summary>
    public static Kind ToKind(AddonKind kind) => kind switch
    {
        AddonKind.String => Model.Kind.String,
        AddonKind.Int => Model.Kind.Int,
        AddonKind.Long => Model.Kind.Long,
        AddonKind.Double => Model.Kind.Double,
        AddonKind.Decimal => Model.Kind.Decimal,
        AddonKind.Bool => Model.Kind.Bool,
        AddonKind.Date => Model.Kind.DateTime,
        AddonKind.DateTime => Model.Kind.DateTime,
        AddonKind.Guid => Model.Kind.Guid,
        _ => Model.Kind.Unknown,
    };
}
