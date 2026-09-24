namespace OxQL.Model.Attributes;

/// <summary>
/// Declares that an id member references an entity: the target entity id, and optionally the
/// wire path on the target the value matches (the target's key by default). The target may
/// live on another service; a remote id is one whose first segment is not a namespace of the
/// declaring host.
/// </summary>
/// <example>
/// <code>
/// [OxQLReference("vehicle.vehicle")]
/// public Guid VehicleId { get; set; }
///
/// [OxQLReference("crm.contact", "number")]
/// public string ContactNumber { get; set; }
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

    /// <summary>The wire path on the target, or null for the target's key.</summary>
    public string? Field { get; }
}
