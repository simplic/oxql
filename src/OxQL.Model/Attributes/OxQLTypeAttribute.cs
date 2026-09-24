// The namespace is the one entity classes import the attribute from. The type lives in this
// assembly because the scan that reads it does; OxQL.Core forwards it here, so an entity
// compiled against either assembly binds to this one type.
namespace OxQL.Core.Attributes;

/// <summary>
/// Marks a class as an OxQL entity and maps it to a backing collection.
/// </summary>
/// <example>
/// <code>
/// [OxQLType("vehicle.vehicle", "vehicle", "vehicle")]
/// public class Vehicle { }
///
/// [OxQLType("customers", "customers")]
/// public class Customer { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class OxQLTypeAttribute : Attribute
{
    /// <summary>
    /// The OxQL type name used as the <c>entityType</c> value in query requests.
    /// May contain dots and is treated as an opaque identifier.
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// The MongoDB collection name.
    /// </summary>
    public string CollectionName { get; }

    /// <summary>
    /// The optional database name override.
    /// <c>null</c> means the adapter's default database is used.
    /// </summary>
    public string? DatabaseName { get; }

    /// <summary>
    /// When <c>true</c>, the entity type can be extended with additional fields at runtime.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool Extendable { get; set; } = false;

    /// <summary>Declares the entity <paramref name="typeName"/>, stored in <paramref name="collectionName"/> of <paramref name="databaseName"/> or of the default database.</summary>
    public OxQLTypeAttribute(string typeName, string collectionName, string? databaseName = null)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            throw new ArgumentException("typeName must not be empty.", nameof(typeName));
        if (string.IsNullOrWhiteSpace(collectionName))
            throw new ArgumentException("collectionName must not be empty.", nameof(collectionName));

        TypeName       = typeName.Trim();
        CollectionName = collectionName.Trim();
        DatabaseName   = string.IsNullOrWhiteSpace(databaseName) ? null : databaseName.Trim();
    }
}
