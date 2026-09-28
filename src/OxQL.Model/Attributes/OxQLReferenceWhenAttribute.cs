namespace OxQL.Model.Attributes;

/// <summary>
/// Declares one case of a typed reference: the id member names <paramref name="targets"/> when
/// the sibling <paramref name="path"/> holds <paramref name="equals"/>, or, with
/// <see cref="Variant"/> as the path, when the object holding the member is stored as the
/// variant named <paramref name="equals"/>. Repeat the attribute once per case; every case of
/// one member names the same path.
/// </summary>
/// <remarks>
/// <para>
/// A target is <c>entity</c>, or <c>entity#itemPath</c> for an element of a keyed item
/// collection on the entity. Several targets of one case are tried in order. The field the
/// value matches is <see cref="Field"/> (on the entity, or on the element), the key when it is
/// not set; it is required when any target lives on another service.
/// </para>
/// <para>
/// The path is a stored string or enum member of the same object; values compare exactly.
/// Combined with <see cref="OxQLReferenceAttribute"/> on one member, both are dropped with a
/// <c>reference-declaration-unresolved</c> finding.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class SourceBillingLineReference
/// {
///     public string Type { get; set; }
///
///     [OxQLReferenceWhen("type", "logistics", "transport.shipment#billingLines", "transport.tour#billingLines", Field = "id")]
///     public Guid Id { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class OxQLReferenceWhenAttribute : Attribute
{
    /// <summary>The path that conditions a case on the stored variant of the object holding the member.</summary>
    public const string Variant = "$variant";

    /// <summary>Declares that the member names <paramref name="targets"/> when <paramref name="path"/> holds <paramref name="equals"/>.</summary>
    public OxQLReferenceWhenAttribute(string path, string equals, params string[] targets)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path must not be empty.", nameof(path));

        if (equals is null)
            throw new ArgumentNullException(nameof(equals));

        if (targets is null || targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A case names at least one target, none empty.", nameof(targets));

        Path = path.Trim();
        Value = equals;
        Targets = [.. targets.Select(target => target.Trim())];
    }

    /// <summary>The sibling's wire name, or <see cref="Variant"/>.</summary>
    public string Path { get; }

    /// <summary>The value the sibling holds, or the variant's name, for which this case applies.</summary>
    public string Value { get; }

    /// <summary>The targets, <c>entity</c> or <c>entity#itemPath</c>, in the order they are tried.</summary>
    public IReadOnlyList<string> Targets { get; }

    /// <summary>The wire path the value matches on each target (or its element); the key when not set. Required when any target is remote.</summary>
    public string? Field { get; init; }

    /// <summary>How the stored value becomes the targets' key.</summary>
    public OxQLKeyAs KeyAs { get; init; }
}
