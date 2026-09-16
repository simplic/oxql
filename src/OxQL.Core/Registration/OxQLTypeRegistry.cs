using System.Reflection;
using OxQL.Core.Attributes;
using OxQL.Model;
using OxQL.Model.Build;

namespace OxQL.Core.Registration;

/// <summary>
/// The v1 face of the entity set: a thin adapter over the model's entity discovery, kept for
/// the callers that still resolve entities by name until they move onto <see cref="EntityModel"/>.
/// </summary>
/// <remarks>
/// Discovery goes through <see cref="EntityScanner"/>, so the registry and the model describe
/// exactly the same entities under the same rules: a declaration on a base class resolves to
/// the most derived subclass, and an id two declarations claim is dropped for both. The scan
/// touches no serializer, so it is safe during service registration; only the model's walk
/// must wait for the host's registrations.
/// </remarks>
public sealed class OxQLTypeRegistry
{
    private readonly Dictionary<string, OxQLTypeRegistration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty registry.</summary>
    public OxQLTypeRegistry()
    {
    }

    /// <summary>Creates a registry describing every entity of a model.</summary>
    public OxQLTypeRegistry(EntityModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var entity in model.Entities.Values)
            _registrations[entity.DeclaredId] = new OxQLTypeRegistration(
                ClrType:        entity.ClrType,
                TypeName:       entity.DeclaredId,
                CollectionName: entity.Collection,
                DatabaseName:   entity.Database,
                Extendable:     entity.Extendable);
    }

    /// <summary>All registered entity types.</summary>
    public IReadOnlyCollection<OxQLTypeRegistration> Registrations => _registrations.Values;

    /// <summary>What the last scan could not describe: a duplicate id, a shared type, a failed scan.</summary>
    public IReadOnlyList<BuildFinding> Findings { get; private set; } = [];

    /// <summary>
    /// Scans the given assemblies for classes decorated with <see cref="OxQLTypeAttribute"/>
    /// and registers them. When the attribute is on a base class, the most-derived
    /// concrete subclass found in the same assemblies is used as the <c>ClrType</c>
    /// so that all properties are visible for reflection.
    /// </summary>
    public OxQLTypeRegistry ScanAssemblies(params Assembly[] assemblies)
    {
        var findings = new List<BuildFinding>();

        foreach (var declaration in EntityScanner.Scan(assemblies, findings))
            _registrations[declaration.DeclaredId] = new OxQLTypeRegistration(
                ClrType:        declaration.ClrType,
                TypeName:       declaration.DeclaredId,
                CollectionName: declaration.Collection,
                DatabaseName:   declaration.Database,
                Extendable:     declaration.Extendable);

        Findings = findings;

        return this;
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

    /// <summary>
    /// Returns whether the given <c>entityType</c> is extendable, or <c>false</c>
    /// if no registration exists.
    /// </summary>
    public bool IsExtendable(string entityType) =>
        TryGet(entityType, out var reg) && reg.Extendable;
}

/// <summary>
/// Describes a single OxQL entity type registration.
/// </summary>
/// <param name="ClrType">The .NET type decorated with <see cref="OxQLTypeAttribute"/> (may be <c>null</c> for manual registrations).</param>
/// <param name="TypeName">The OxQL entity type name used in query requests.</param>
/// <param name="CollectionName">The backing MongoDB collection name.</param>
/// <param name="DatabaseName">Optional database override; <c>null</c> means use the adapter default.</param>
/// <param name="Extendable">When <c>true</c>, the entity type can be extended with additional fields at runtime.</param>
public sealed record OxQLTypeRegistration(
    Type?   ClrType,
    string  TypeName,
    string  CollectionName,
    string? DatabaseName,
    bool    Extendable = false);
