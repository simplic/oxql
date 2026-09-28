namespace OxQL.Model.Attributes;

/// <summary>
/// Declares that an id member references an entity: the target entity id, and optionally the
/// wire path on the target the value matches (the target's key by default). The target may
/// live on another service; a remote id is one whose first segment is not a namespace of the
/// declaring host, and then the field is required.
/// </summary>
/// <remarks>
/// <see cref="Item"/> makes the value name an element of a keyed item collection on the target
/// instead of the entity; the field is then read on the element. <see cref="KeyAs"/> declares a
/// string member holding a guid key. Typed references (one target per sibling value or stored
/// variant) use <see cref="OxQLReferenceWhenAttribute"/>, never together with this attribute.
/// </remarks>
/// <example>
/// <code>
/// [OxQLReference("vehicle.vehicle")]
/// public Guid VehicleId { get; set; }
///
/// [OxQLReference("crm.contact", "number")]
/// public string ContactNumber { get; set; }
///
/// [OxQLReference("transport.shipment", "id", Item = "billingLines")]
/// public Guid ShipmentBillingLineId { get; set; }
///
/// [OxQLReference("transport.shipment", "id", KeyAs = OxQLKeyAs.Guid)]
/// public string ShipmentId { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class OxQLReferenceAttribute : Attribute
{
    /// <summary>Declares a reference to <paramref name="entity"/>, matching <paramref name="field"/> or the target's key.</summary>
    public OxQLReferenceAttribute(string entity, string? field = null)
    {
        if (string.IsNullOrWhiteSpace(entity))
            throw new ArgumentException("entity must not be empty.", nameof(entity));

        Entity = entity.Trim();
        Field = string.IsNullOrWhiteSpace(field) ? null : field.Trim();
    }

    /// <summary>The target entity id.</summary>
    public string Entity { get; }

    /// <summary>The wire path on the target (or on its element under <see cref="Item"/>), or null for the key.</summary>
    public string? Field { get; }

    /// <summary>The wire path of the keyed item collection on the target whose element the value names; null for the entity itself.</summary>
    public string? Item { get; init; }

    /// <summary>How the stored value becomes the target's key.</summary>
    public OxQLKeyAs KeyAs { get; init; }
}
