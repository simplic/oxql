namespace OxQL.Model.Addon;

/// <summary>
/// One organisation's definition of a key under an entity's addon bag: a hint that the value
/// is most likely of <see cref="Kind"/>. Nothing is refused on write and nothing is normalised;
/// the engine carries the uncertainty on the read side by matching tolerantly.
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

    /// <summary>A 32-bit integer; matched tolerantly against any numeric type or a numeric string.</summary>
    Int,

    /// <summary>A 64-bit integer; matched tolerantly against any numeric type or a numeric string.</summary>
    Long,

    /// <summary>A floating-point number; matched tolerantly against any numeric type or a numeric string.</summary>
    Double,

    /// <summary>A decimal; matched at the key and at the key's <c>_v</c> wrapper.</summary>
    Decimal,

    /// <summary>A boolean; matched against a boolean or the strings <c>"true"</c>/<c>"false"</c>.</summary>
    Bool,

    /// <summary>A calendar date; matched against a BSON date or an ISO string.</summary>
    Date,

    /// <summary>A date and time; matched against a BSON date or an ISO string.</summary>
    DateTime,

    /// <summary>A GUID; matched against binary or string.</summary>
    Guid,

    /// <summary>An explicit container with no typing of its own; its defined descendants are typed, it is not.</summary>
    Object,
}

/// <summary>Maps addon kinds onto the model's kind vocabulary.</summary>
public static class AddonKinds
{
    /// <summary>The model kind an addon kind binds as; <see cref="AddonKind.Object"/> is <see cref="Model.Kind.Unknown"/>.</summary>
    public static Kind ToKind(AddonKind kind) => kind switch
    {
        AddonKind.String => Model.Kind.String,
        AddonKind.Int => Model.Kind.Int,
        AddonKind.Long => Model.Kind.Long,
        AddonKind.Double => Model.Kind.Double,
        AddonKind.Decimal => Model.Kind.Decimal,
        AddonKind.Bool => Model.Kind.Bool,
        AddonKind.Date => Model.Kind.Date,
        AddonKind.DateTime => Model.Kind.DateTime,
        AddonKind.Guid => Model.Kind.Guid,
        _ => Model.Kind.Unknown,
    };
}
