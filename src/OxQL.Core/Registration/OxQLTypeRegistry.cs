using System.Reflection;
using OxQL.Core.Attributes;

namespace OxQL.Core.Registration;

/// <summary>
/// Holds all entity registrations discovered from <see cref="OxQLTypeAttribute"/> attributes.
/// </summary>
public sealed class OxQLTypeRegistry
{
    private readonly Dictionary<string, OxQLTypeRegistration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered entity types.</summary>
    public IReadOnlyCollection<OxQLTypeRegistration> Registrations => _registrations.Values;

    /// <summary>
    /// Scans the given assemblies for classes decorated with <see cref="OxQLTypeAttribute"/>
    /// and registers them. When the attribute is on a base class, the most-derived
    /// concrete subclass found in the same assemblies is used as the <c>ClrType</c>
    /// so that all properties are visible for reflection.
    /// </summary>
    public OxQLTypeRegistry ScanAssemblies(params Assembly[] assemblies)
    {
        var allTypes = assemblies.SelectMany(a => a.GetTypes()).ToList();

        // Build a lookup: base-type → all concrete subclasses in the scanned set
        var subclassMap = allTypes
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => GetInheritanceChain(t).Skip(1).Select(b => (Base: b, Derived: t)))
            .GroupBy(x => x.Base, x => x.Derived)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var type in allTypes)
        {
            var attr = type.GetCustomAttribute<OxQLTypeAttribute>();
            if (attr is null) continue;

            // Prefer the most-derived concrete subclass, falling back to the attributed type
            var representativeType = subclassMap.TryGetValue(type, out var subs) && subs.Count > 0
                ? subs.OrderByDescending(t => InheritanceDepth(t)).First()
                : type;

            _registrations[attr.TypeName] = new OxQLTypeRegistration(
                ClrType:        representativeType,
                TypeName:       attr.TypeName,
                CollectionName: attr.CollectionName,
                DatabaseName:   attr.DatabaseName);
        }

        return this;
    }

    private static IEnumerable<Type> GetInheritanceChain(Type t)
    {
        var current = t;
        while (current is not null)
        {
            yield return current;
            current = current.BaseType;
        }
    }

    private static int InheritanceDepth(Type t)
    {
        int depth = 0;
        var current = t.BaseType;
        while (current is not null) { depth++; current = current.BaseType; }
        return depth;
    }

    /// <summary>
    /// Manually registers an entity type without assembly scanning.
    /// </summary>
    public OxQLTypeRegistry Register(string typeName, string collectionName, string? databaseName = null)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            throw new ArgumentException("typeName must not be empty.", nameof(typeName));
        if (string.IsNullOrWhiteSpace(collectionName))
            throw new ArgumentException("collectionName must not be empty.", nameof(collectionName));

        _registrations[typeName.Trim()] = new OxQLTypeRegistration(
            ClrType:        null,
            TypeName:       typeName.Trim(),
            CollectionName: collectionName,
            DatabaseName:   databaseName);

        return this;
    }

    /// <summary>
    /// Tries to resolve a registration by <c>entityType</c> name (case-insensitive).
    /// </summary>
    public bool TryGet(string entityType, out OxQLTypeRegistration registration)
    {
        return _registrations.TryGetValue(entityType, out registration!);
    }

    /// <summary>
    /// Returns the collection name for the given <c>entityType</c>, or <c>null</c>
    /// if no registration exists.
    /// </summary>
    public string? GetCollectionName(string entityType) =>
        TryGet(entityType, out var reg) ? reg.CollectionName : null;

    /// <summary>
    /// Returns the database name override for the given <c>entityType</c>, or <c>null</c>
    /// when the adapter's default database should be used.
    /// </summary>
    public string? GetDatabaseName(string entityType) =>
        TryGet(entityType, out var reg) ? reg.DatabaseName : null;
}

/// <summary>
/// Describes a single OxQL entity type registration.
/// </summary>
/// <param name="ClrType">The .NET type decorated with <see cref="OxQLTypeAttribute"/> (may be <c>null</c> for manual registrations).</param>
/// <param name="TypeName">The OxQL entity type name used in query requests.</param>
/// <param name="CollectionName">The backing MongoDB collection name.</param>
/// <param name="DatabaseName">Optional database override; <c>null</c> means use the adapter default.</param>
public sealed record OxQLTypeRegistration(
    Type?   ClrType,
    string  TypeName,
    string  CollectionName,
    string? DatabaseName);
