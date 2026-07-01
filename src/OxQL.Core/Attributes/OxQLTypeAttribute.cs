namespace OxQL.Core.Attributes;

/// <summary>
/// Marks a class as an OxQL entity and maps it to a backing collection.
/// </summary>
/// <remarks>
/// The <paramref name="typeName"/> format is either:
/// <list type="bullet">
///   <item><c>"collection"</c> — uses the default database configured in the adapter.</item>
///   <item><c>"database.collection"</c> — overrides the database for this entity.</item>
/// </list>
/// The part after the last dot is used as the MongoDB collection name.
/// The full <paramref name="typeName"/> (lowercased) is used as the <c>entityType</c> in OxQL queries.
/// </remarks>
/// <example>
/// <code>
/// [OxQLType("vehicle.vehicle")]
/// public class Vehicle { }
///
/// [OxQLType("customers")]
/// public class Customer { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class OxQLTypeAttribute : Attribute
{
    /// <summary>
    /// The fully qualified OxQL type name, e.g. <c>"vehicle.vehicle"</c> or <c>"customers"</c>.
    /// Used as the <c>entityType</c> value in query requests.
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// The MongoDB collection name derived from <see cref="TypeName"/>
    /// (the segment after the last dot, or the whole name if no dot is present).
    /// </summary>
    public string CollectionName { get; }

    /// <summary>
    /// The optional database name prefix (segment before the last dot).
    /// <c>null</c> when no dot is present — the adapter's default database is used.
    /// </summary>
    public string? DatabaseName { get; }

    public OxQLTypeAttribute(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            throw new ArgumentException("typeName must not be empty.", nameof(typeName));

        TypeName = typeName.Trim();

        var lastDot = TypeName.LastIndexOf('.');
        if (lastDot >= 0)
        {
            DatabaseName   = TypeName[..lastDot];
            CollectionName = TypeName[(lastDot + 1)..];
        }
        else
        {
            DatabaseName   = null;
            CollectionName = TypeName;
        }
    }
}
