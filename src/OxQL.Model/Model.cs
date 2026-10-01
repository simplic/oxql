using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Options;

namespace OxQL.Model;

/// <summary>
/// One immutable model per host: every queryable entity with its wire view, its storage view
/// and the pooled type structure the schema publishes under the same ids. Built once, after
/// every service registration and before the first request.
/// </summary>
/// <remarks>
/// The model is a graph with cycles (a type can reach itself through a member), so its nodes
/// are classes compared by reference, never records compared structurally.
/// </remarks>
public sealed class EntityModel
{
    internal EntityModel(
        IReadOnlyDictionary<string, EntityDef> entities,
        IReadOnlyDictionary<string, string> retiredIds,
        IReadOnlyDictionary<string, TypeDef> typePool,
        IReadOnlyList<BuildFinding> findings,
        DateTimeOffset builtAt,
        string fingerprint)
    {
        Entities = entities;
        RetiredIds = retiredIds;
        TypePool = typePool;
        Findings = findings;
        BuiltAt = builtAt;
        Fingerprint = fingerprint;
    }

    /// <summary>The entities by id, ordinal and case-sensitive, ordinally sorted.</summary>
    public IReadOnlyDictionary<string, EntityDef> Entities { get; }

    /// <summary>Retired id to the current entity id, for the ids a host declared through its schema options.</summary>
    public IReadOnlyDictionary<string, string> RetiredIds { get; }

    /// <summary>
    /// The pooled type structure under the schema's ids: entities under their entity id,
    /// structural types under <c>t_</c> ids, enums included. Ordinally sorted.
    /// </summary>
    public IReadOnlyDictionary<string, TypeDef> TypePool { get; }

    /// <summary>What the build could not describe, in build order. Empty on a clean build.</summary>
    public IReadOnlyList<BuildFinding> Findings { get; }

    /// <summary>When the build finished.</summary>
    public DateTimeOffset BuiltAt { get; }

    /// <summary>A SHA-256 over the canonical form of every entity's path index and pool ids; equal models have equal fingerprints.</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Resolves an entity id exactly and case-sensitively, then through the retired-id table.
    /// <paramref name="retired"/> is true when the id was a retired one and the current entity
    /// is returned in its place.
    /// </summary>
    public bool TryResolve(string id, out EntityDef entity, out bool retired)
    {
        retired = false;

        if (Entities.TryGetValue(id, out entity!))
            return true;

        if (RetiredIds.TryGetValue(id, out var current) && Entities.TryGetValue(current, out entity!))
        {
            retired = true;
            return true;
        }

        entity = null!;
        return false;
    }
}

/// <summary>One queryable entity: where it is stored, its root type and its complete path index.</summary>
[DebuggerDisplay("{Id} ({Collection})")]
public sealed class EntityDef
{
    internal EntityDef(
        string id,
        string declaredId,
        Type? clrType,
        string collection,
        string? database,
        bool extendable,
        string? displayName,
        TypeDef root)
    {
        Id = id;
        DeclaredId = declaredId;
        ClrType = clrType;
        Collection = collection;
        Database = database;
        Extendable = extendable;
        DisplayName = displayName;
        Root = root;
    }

    /// <summary>The entity id as the schema publishes it: trimmed and lower-cased.</summary>
    public string Id { get; }

    /// <summary>The id as the declaration spelled it, trimmed; the v1 registry's key.</summary>
    public string DeclaredId { get; }

    /// <summary>The first segment of the id: the service that owns the entity.</summary>
    public string Namespace => Id.Split('.')[0];

    /// <summary>The CLR type the entity is read from, or null when the model came from a document.</summary>
    public Type? ClrType { get; }

    /// <summary>The MongoDB collection name.</summary>
    public string Collection { get; }

    /// <summary>The database override, or null for the host's default database.</summary>
    public string? Database { get; }

    /// <summary>Whether the entity carries an addon bag an organisation can define keys for.</summary>
    public bool Extendable { get; }

    /// <summary>The human label of the entity.</summary>
    public string? DisplayName { get; }

    /// <summary>The root type; its members are the entity's own.</summary>
    public TypeDef Root { get; }

    /// <summary>The root path the driver stores under <c>_id</c>, or null when the entity has no stored key (a build finding).</summary>
    public PathDef? Key { get; internal set; }

    /// <summary>The root path that names an instance: the first of <c>name</c>, <c>matchCode</c>, <c>number</c> the entity has as a string.</summary>
    public PathDef? Display { get; internal set; }

    /// <summary>The ids this entity retired, ordinally sorted.</summary>
    public IReadOnlyList<string> RetiredIds { get; internal set; } = [];

    private IReadOnlyList<PathDef> paths = [];
    private Lazy<IReadOnlyDictionary<string, IReadOnlyList<PathDef>>> children = new(() => new Dictionary<string, IReadOnlyList<PathDef>>(StringComparer.Ordinal));

    /// <summary>Every reachable wire path in walk order: members first, then the paths under each member, depth-first.</summary>
    public IReadOnlyList<PathDef> Paths
    {
        get => paths;
        internal set
        {
            paths = value;
            children = new Lazy<IReadOnlyDictionary<string, IReadOnlyList<PathDef>>>(() => ChildrenIndex(value), LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    /// <summary>
    /// The per-entity children index (DESIGN §4.5): the paths one segment below each path, keyed by
    /// the parent's wire spelling and <c>""</c> for the root members, in walk order. A dictionary's
    /// value is its <c>*</c> child. Built once from <see cref="Paths"/>, outside the fingerprint.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PathDef>> Children => children.Value;

    /// <summary>The paths one segment below <paramref name="wire"/> (<c>""</c>: the root members), in walk order; empty for a leaf or an unknown path.</summary>
    public IReadOnlyList<PathDef> ChildrenOf(string wire) => Children.TryGetValue(wire, out var found) ? found : [];

    private static IReadOnlyDictionary<string, IReadOnlyList<PathDef>> ChildrenIndex(IReadOnlyList<PathDef> paths)
    {
        var index = new Dictionary<string, List<PathDef>>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var dot = path.Wire.LastIndexOf('.');
            var parent = dot < 0 ? "" : path.Wire[..dot];

            if (!index.TryGetValue(parent, out var list))
                index[parent] = list = [];

            list.Add(path);
        }

        return index.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<PathDef>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Every reachable wire path by its spelling, ordinal.</summary>
    public IReadOnlyDictionary<string, PathDef> PathIndex { get; internal set; } = new Dictionary<string, PathDef>(StringComparer.Ordinal);

    /// <summary>The path at a wire spelling, or null.</summary>
    public PathDef? Path(string wire) => PathIndex.GetValueOrDefault(wire);
}

/// <summary>One pooled type: an object with members, or an enum with values.</summary>
[DebuggerDisplay("{PoolId}")]
public sealed class TypeDef
{
    internal TypeDef(string poolId, Type? clrType, bool isEntity)
    {
        PoolId = poolId;
        ClrType = clrType;
        IsEntity = isEntity;
    }

    /// <summary>The pool key: the entity id for an entity, a <c>t_</c> id for a structural type.</summary>
    public string PoolId { get; internal set; }

    /// <summary>The CLR type, or null when the model came from a document.</summary>
    public Type? ClrType { get; }

    /// <summary>True on an entity's root type.</summary>
    public bool IsEntity { get; }

    /// <summary>The members in the order the schema publishes them: most derived type first, declaration order within a type. Empty on an enum.</summary>
    public IReadOnlyList<MemberDef> Members { get; internal set; } = [];

    /// <summary>True on an enum entry.</summary>
    public bool IsEnum { get; internal set; }

    /// <summary>Whether an enum entry is a flags enum.</summary>
    public bool EnumFlags { get; internal set; }

    /// <summary>The members of an enum entry in declaration order. Empty on an object.</summary>
    public IReadOnlyList<EnumValueDef> EnumValues { get; internal set; } = [];

    /// <summary>
    /// The discriminator value the driver writes for this type when it is stored under a base
    /// nominal type (<c>_t</c>, the short type name). Null when the type is not polymorphic or
    /// the model came from a document.
    /// </summary>
    public string? Discriminator { get; internal set; }

    /// <summary>
    /// The concrete types a value of this type can hold, by the class maps the host registered:
    /// every registered, non-abstract class assignable to this type other than itself, ordinally
    /// by name. Their members that this type lacks are merged into <see cref="Members"/> with
    /// <see cref="MemberDef.OnlyFor"/> set. Empty when the type is not polymorphic.
    /// </summary>
    public IReadOnlyList<VariantDef> Variants { get; internal set; } = [];

    /// <summary>The element the driver stores the discriminator under (<c>_t</c> by default); null when the type has no variants.</summary>
    public string? DiscriminatorElement { get; internal set; }

    /// <summary>How the discriminator is stored; null when the type has no variants.</summary>
    public DiscriminatorForm? DiscriminatorForm { get; internal set; }

    /// <summary>
    /// The name a value stored as the polymorphic type itself goes by, as a schema document publishes it
    /// (<c>baseVariant</c>); set only on a model read from a document, where no CLR type says whether the
    /// type is concrete. Outside the fingerprint.
    /// </summary>
    public string? BaseVariant { get; internal set; }

    /// <summary>The normalised English description of the type (see <see cref="Build.XmlDocs"/>); null when nothing describes it. Outside the fingerprint.</summary>
    public string? Description { get; internal set; }

    /// <summary>The member with the given wire name, or null.</summary>
    public MemberDef? Member(string wireName)
    {
        foreach (var member in Members)
            if (string.Equals(member.WireName, wireName, StringComparison.Ordinal))
                return member;

        return null;
    }
}

/// <summary>One concrete type a polymorphic type's values can hold.</summary>
/// <param name="Name">The variant's name: the CLR type name without a generic arity suffix; what <c>onlyFor</c> lists.</param>
/// <param name="Discriminator">The discriminator value the driver writes for the variant; the name when the model came from a document.</param>
/// <param name="Type">The variant's pooled type.</param>
public sealed record VariantDef(string Name, string Discriminator, TypeDef Type);

/// <summary>How a polymorphic type's discriminator is stored.</summary>
public enum DiscriminatorForm
{
    /// <summary>One value: the concrete type's discriminator.</summary>
    Scalar,

    /// <summary>An array of the discriminators from the root class down to the concrete type.</summary>
    Hierarchical,
}

/// <summary>One enum member.</summary>
/// <param name="Name">The member's name.</param>
/// <param name="Value">The declared value.</param>
/// <param name="Active">False when the member is <c>[Obsolete]</c>.</param>
/// <param name="Description">The normalised English description; null when nothing describes it. Outside the fingerprint.</param>
public sealed record EnumValueDef(string Name, long Value, bool Active, string? Description = null);

/// <summary>
/// A shape without member facts: the kind, its storage representation, and what it contains.
/// A member is a shape with a name; an array's element and a dictionary's value are shapes only.
/// </summary>
[DebuggerDisplay("{Kind}")]
public class ShapeDef
{
    internal ShapeDef()
    {
    }

    /// <summary>The kind.</summary>
    public Kind Kind { get; internal set; }

    /// <summary>The BSON representation the registry reports for a scalar; <see cref="Representation.None"/> where none is known.</summary>
    public Representation Representation { get; internal set; } = Representation.None;

    /// <summary>The pooled type of an <see cref="Kind.Object"/> or <see cref="Kind.Enum"/>.</summary>
    public TypeDef? Type { get; internal set; }

    /// <summary>The element shape of an <see cref="Kind.Array"/>.</summary>
    public ShapeDef? Of { get; internal set; }

    /// <summary>The value shape of a <see cref="Kind.Dictionary"/>.</summary>
    public ShapeDef? Value { get; internal set; }

    /// <summary>How a dictionary is stored; null when the model came from a document (treated as <see cref="DictionaryRepresentation.Document"/>).</summary>
    public DictionaryRepresentation? DictionaryRepresentation { get; internal set; }

    /// <summary>The entity this shape is an embedded copy of, when its pooled type is an entity.</summary>
    public string? SnapshotOf { get; internal set; }

    /// <summary>The kind once array traversal is accounted for: an array of guids is a guid leaf.</summary>
    public Kind LeafKind
    {
        get
        {
            var current = this;

            while (current.Kind == Kind.Array && current.Of is not null)
                current = current.Of;

            return current.Kind;
        }
    }

    /// <summary>The shape once array traversal is accounted for.</summary>
    public ShapeDef Leaf
    {
        get
        {
            var current = this;

            while (current.Kind == Kind.Array && current.Of is not null)
                current = current.Of;

            return current;
        }
    }
}

/// <summary>One member of a pooled type: a shape with a wire name, a storage name and nullability.</summary>
[DebuggerDisplay("{WireName} ({Kind}) -> {StorageName}")]
public sealed class MemberDef : ShapeDef
{
    internal MemberDef(string wireName)
    {
        WireName = wireName;
    }

    /// <summary>The camelCase wire name.</summary>
    public string WireName { get; }

    /// <summary>The CLR property name, or null when the model came from a document.</summary>
    public string? ClrName { get; internal set; }

    /// <summary>The element name the driver stores the member under; null when the member is not stored.</summary>
    public string? StorageName { get; internal set; }

    /// <summary>
    /// Whether the driver stores the member at all. False for <c>[BsonIgnore]</c>, computed
    /// and get-only members: they are in the wire view and refused by the engine.
    /// </summary>
    public bool Stored { get; internal set; }

    /// <summary>Whether a client can read null out of the member.</summary>
    public bool Nullable { get; internal set; }

    /// <summary>The human label where it is not the de-camelCased wire name; null otherwise.</summary>
    public string? DisplayName { get; internal set; }

    /// <summary>
    /// The declared reference this member carries when it is a <see cref="ReferenceDef.IsSimple">simple</see>
    /// one: one unconditional case naming one entity, without item or conversion. Null
    /// otherwise, also when <see cref="References"/> holds typed, item or converted cases.
    /// </summary>
    public ReferenceDef? Reference { get; internal set; }

    /// <summary>
    /// Every case of the reference this member carries, in declaration order: the simple one
    /// (also in <see cref="Reference"/>), or the typed cases of <c>[OxQLReferenceWhen]</c> and
    /// host-side declarations, item targets and converted keys. Empty when the member is no
    /// foreign key.
    /// </summary>
    public IReadOnlyList<ReferenceDef> References { get; internal set; } = [];

    /// <summary>
    /// The variants that carry the member, when it is merged into a polymorphic type from its
    /// variants rather than declared by the type itself; null on a member every value has.
    /// </summary>
    public IReadOnlyList<string>? OnlyFor { get; internal set; }

    /// <summary>The normalised English description (see <see cref="Build.XmlDocs"/>); null when nothing describes it. Outside the fingerprint.</summary>
    public string? Description { get; internal set; }

    /// <summary>The member's deprecation, from <c>[Obsolete]</c>; null when it is not deprecated. Outside the fingerprint.</summary>
    public DeprecationDef? Deprecated { get; internal set; }

    /// <summary>The value constraints the member declares through DataAnnotations; null when it declares none. Outside the fingerprint.</summary>
    public ConstraintsDef? Constraints { get; internal set; }
}

/// <summary>A member's deprecation: the schema's <c>deprecated</c> member.</summary>
public sealed record DeprecationDef
{
    /// <summary>The version the member was deprecated in; a CLR build never knows it.</summary>
    public string? Since { get; init; }

    /// <summary>The path that replaces the member; a CLR build never knows it.</summary>
    public string? ReplacedBy { get; init; }

    /// <summary>A note for the reader: the <c>[Obsolete]</c> message.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// A member's value constraints: the schema's <c>constraints</c> member, read from
/// <c>[MaxLength]</c>/<c>[StringLength]</c>, <c>[Range]</c> and <c>[RegularExpression]</c>.
/// Bounds are strings, invariant culture, because a JSON number is a double.
/// </summary>
public sealed record ConstraintsDef
{
    /// <summary>The longest string the member accepts; the smaller of <c>[MaxLength]</c> and <c>[StringLength]</c>, on string members only.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The smallest value the member accepts.</summary>
    public string? Min { get; init; }

    /// <summary>The largest value the member accepts.</summary>
    public string? Max { get; init; }

    /// <summary>A regular expression the member's value satisfies.</summary>
    public string? Pattern { get; init; }
}

/// <summary>One reachable wire path of an entity with everything the binder needs at that path.</summary>
[DebuggerDisplay("{Wire} -> {Storage} ({Kind})")]
public sealed class PathDef
{
    internal PathDef(string wire, string? storage, MemberDef member, ShapeDef shape, int depth, int collectionAncestors)
    {
        Wire = wire;
        Storage = storage;
        Member = member;
        Shape = shape;
        Depth = depth;
        CollectionAncestors = collectionAncestors;
    }

    /// <summary>The dot-separated wire path; a dictionary key segment is <c>*</c>.</summary>
    public string Wire { get; }

    /// <summary>The dot-separated storage path, or null when the path or one of its ancestors is not stored.</summary>
    public string? Storage { get; }

    /// <summary>The member the path ends in or passes through last.</summary>
    public MemberDef Member { get; }

    /// <summary>The shape at this path: the member itself, or a dictionary's value shape under a <c>*</c> segment.</summary>
    public ShapeDef Shape { get; }

    /// <summary>The kind at this path.</summary>
    public Kind Kind => Shape.Kind;

    /// <summary>The kind once array traversal is accounted for.</summary>
    public Kind LeafKind => Shape.LeafKind;

    /// <summary>The number of segments minus one.</summary>
    public int Depth { get; }

    /// <summary>How many arrays lie above this path; a filter through one means "some element".</summary>
    public int CollectionAncestors { get; }

    /// <summary>Whether the driver stores the path.</summary>
    public bool Stored => Storage is not null;

    /// <summary>Whether a filter operand can be encoded for this path: stored, and a scalar or enum leaf.</summary>
    public bool Filterable { get; internal set; }

    /// <summary>Whether a sort can use this path: filterable, not an array, and under no array.</summary>
    public bool Sortable { get; internal set; }

    /// <summary>True on the addon bag of an extendable entity; keys under it are typed per organisation, not by the model.</summary>
    public bool IsAddonRoot { get; internal set; }

    /// <summary>The declared simple reference this path carries.</summary>
    public ReferenceDef? Reference => Member.Reference;

    /// <summary>Every declared reference case this path carries.</summary>
    public IReadOnlyList<ReferenceDef> References => Member.References;
}

/// <summary>
/// One case of a declared foreign key: the targets a member's value names, the condition under
/// which it names them, and how the stored value becomes the targets' key.
/// </summary>
public sealed record ReferenceDef
{
    /// <summary>The targets in declaration order. Never empty.</summary>
    public required IReadOnlyList<ReferenceTarget> Targets { get; init; }

    /// <summary>When the case applies; null when it always does.</summary>
    public ReferenceCondition? When { get; init; }

    /// <summary>How the stored value becomes the targets' key.</summary>
    public KeyAs KeyAs { get; init; }

    /// <summary>Where the declaration came from.</summary>
    public required ReferenceSource DeclaredBy { get; init; }

    /// <summary>The first target's entity id, normalised.</summary>
    public string TargetEntity => Targets[0].Entity;

    /// <summary>The wire path on the first target the member's value matches.</summary>
    public string TargetField => Targets[0].Field;

    /// <summary>True when the first target lives on another host: its namespace is not one of this model's.</summary>
    public bool IsRemote => Targets[0].IsRemote;

    /// <summary>
    /// True for the one form a 1.0 reader understands: unconditional, one entity target, no
    /// item, no conversion. Only a simple case is published under <c>references</c> and set on
    /// <see cref="MemberDef.Reference"/>; every other one goes under <c>referenceCases</c>.
    /// </summary>
    public bool IsSimple => When is null && KeyAs == KeyAs.None && Targets is [{ Item: null }];

    /// <summary>The direction; every declaration in this version is forward, from the id member to the target.</summary>
    public ReferenceDirection Direction => ReferenceDirection.Forward;

    /// <summary>The case as one line: condition, conversion and targets; what the fingerprint and messages print.</summary>
    public override string ToString()
    {
        var when = When switch
        {
            ReferenceCondition.PathEquals equals => $"when {equals.Path}={string.Join("|", equals.Values)} ",
            ReferenceCondition.Variant variant => $"when $variant={string.Join("|", variant.Names)} ",
            _ => "",
        };

        return $"{when}{(KeyAs == KeyAs.None ? "" : "keyAs guid ")}-> {string.Join(", ", Targets.Select(target => $"{target}.{target.Field}{(target.IsRemote ? " (remote)" : "")}"))}";
    }
}

/// <summary>One target of a reference case.</summary>
/// <param name="Entity">The target entity id, normalised.</param>
/// <param name="Field">
/// The wire path the value matches: on the entity, or on the element of <paramref name="Item"/>.
/// The key of what it is read on unless declared otherwise.
/// </param>
/// <param name="Item">
/// The wire path of the keyed item collection on the target whose element the value names; null
/// when the value names the entity itself.
/// </param>
/// <param name="IsRemote">True when the entity lives on another host.</param>
/// <param name="FieldIsKey">
/// True when <paramref name="Field"/> is the key of what it is read on (the entity, or the
/// element), so a value names at most one. A remote target's key is <c>id</c> by the fleet's
/// convention.
/// </param>
public sealed record ReferenceTarget(string Entity, string Field, string? Item, bool IsRemote, bool FieldIsKey)
{
    /// <summary>The target as a declaration spells it: <c>entity</c> or <c>entity#item</c>.</summary>
    public override string ToString() => Item is null ? Entity : $"{Entity}#{Item}";
}

/// <summary>When a reference case applies to a row.</summary>
public abstract record ReferenceCondition
{
    private ReferenceCondition()
    {
    }

    /// <summary>
    /// A stored string or enum sibling of the id member holds one of <paramref name="Values"/>,
    /// compared exactly: the schema's <c>{ "path", "equals" }</c>. (DESIGN §3.3.1 names it
    /// <c>Equals</c>, which a record cannot nest beside its own <c>Equals</c> method.)
    /// </summary>
    /// <param name="Path">The sibling's wire name.</param>
    /// <param name="Values">The values, in declaration order, distinct.</param>
    public sealed record PathEquals(string Path, IReadOnlyList<string> Values) : ReferenceCondition;

    /// <summary>
    /// The object holding the id member is stored as one of the named variants (by its
    /// discriminator): the schema's <c>{ "variant" }</c>.
    /// </summary>
    /// <param name="Names">The variant names as <see cref="VariantDef.Name"/> spells them.</param>
    public sealed record Variant(IReadOnlyList<string> Names) : ReferenceCondition;
}

/// <summary>How a reference's stored value becomes its targets' key.</summary>
public enum KeyAs
{
    /// <summary>The value is the key as stored.</summary>
    None,

    /// <summary>A string holding a guid in any .NET format, sent as the lower-case <c>D</c> form: the schema's <c>"keyAs": "guid"</c>.</summary>
    Guid,
}

/// <summary>How a reference was declared.</summary>
public enum ReferenceSource
{
    /// <summary><c>[OxQLReference]</c> on the id member.</summary>
    Attribute,

    /// <summary><c>[ReferenceId]</c> on the navigation property naming the id member.</summary>
    ReferenceId,

    /// <summary>A declared (not inferred) <c>references</c> or <c>referenceCases</c> member of a schema document.</summary>
    Document,

    /// <summary>A host-side declaration (<see cref="Build.ReferenceDeclarations"/>) on a pooled type's wire member.</summary>
    Declaration,
}

/// <summary>The direction of a reference.</summary>
public enum ReferenceDirection
{
    /// <summary>From the member holding the key to the entity it names.</summary>
    Forward,
}

/// <summary>The BSON representation a scalar is stored in.</summary>
public readonly record struct Representation(BsonType? BsonType, GuidRepresentation? GuidRepresentation)
{
    /// <summary>No representation is known.</summary>
    public static readonly Representation None = new(null, null);

    /// <summary>A representation with a BSON type only.</summary>
    public static Representation Of(BsonType type) => new(type, null);

    /// <inheritdoc/>
    public override string ToString() =>
        BsonType is null ? "none" : GuidRepresentation is null ? BsonType.ToString()! : $"{BsonType}/{GuidRepresentation}";
}

/// <summary>One thing a build could not describe: a stable kebab-case code, a wire-term target and a sentence.</summary>
public sealed record BuildFinding(string Code, string Target, string Message, string? Detail = null)
{
    /// <inheritdoc/>
    public override string ToString() => Detail is null ? $"{Code} {Target}: {Message}" : $"{Code} {Target}: {Message} [{Detail}]";
}

/// <summary>The closed set of build finding codes.</summary>
public static class BuildCodes
{
    /// <summary>Two declarations claim one entity id; both are dropped.</summary>
    public const string DuplicateEntityId = "duplicate-entity-id";

    /// <summary>One CLR type is declared under two ids; the second is dropped.</summary>
    public const string EntityTypeShared = "entity-type-shared";

    /// <summary>No assembly was named to scan.</summary>
    public const string EntityAssembliesMissing = "entity-assemblies-missing";

    /// <summary>The assembly scan threw; the model has no entities.</summary>
    public const string EntityScanFailed = "entity-scan-failed";

    /// <summary>A retired id is also a live entity id or is claimed by two current ids; it is dropped.</summary>
    public const string RetiredIdAmbiguous = "retired-id-ambiguous";

    /// <summary>The entity has no member the driver stores under <c>_id</c>.</summary>
    public const string EntityKeyMissing = "entity-key-missing";

    /// <summary>A member's serializer is not a document serializer, so the member is <c>unknown</c>.</summary>
    public const string MemberSerializerOpaque = "member-serializer-opaque";

    /// <summary>A member's serializer could not be resolved, so the member is <c>unknown</c>.</summary>
    public const string MemberSerializerUnavailable = "member-serializer-unavailable";

    /// <summary>A collection or dictionary declares no element type, so its values are <c>unknown</c>.</summary>
    public const string CollectionUntyped = "collection-untyped";

    /// <summary>
    /// A path went deeper than the walk allows, or the entity has more paths than the index
    /// holds (<c>Detail</c> is then <c>path-count</c>); what lies beyond is not indexed.
    /// </summary>
    public const string PathDepthExceeded = "path-depth-exceeded";

    /// <summary>A reference names a local entity id the model does not have; the reference is dropped.</summary>
    public const string ReferenceTargetUnknown = "reference-target-unknown";

    /// <summary>A <c>[ReferenceId]</c> declaration names a type that is not an entity or a property that does not exist.</summary>
    public const string ReferenceDeclarationUnresolved = "reference-declaration-unresolved";

    /// <summary>
    /// A reference into another service names no target field, and this host cannot know the
    /// target's key; or a reference names a field of a local target the entity does not have. The
    /// reference (or the case) is dropped.
    /// </summary>
    public const string ReferenceTargetFieldUnknown = "reference-target-field-unknown";

    /// <summary>A schema document's format version is not one this reader understands.</summary>
    public const string DocumentVersionUnsupported = "document-version-unsupported";

    /// <summary>A schema document pointer has no target; the member is <c>unknown</c>.</summary>
    public const string DanglingTypePointer = "dangling-type-pointer";

    /// <summary>Variants of one polymorphic type carry a member under one wire name with a different kind, storage name or representation; the merged member is <c>unknown</c>.</summary>
    public const string PolymorphicMemberConflict = "polymorphic-member-conflict";

    /// <summary>A concrete subclass of a polymorphic type has no registered class map, so the model does not describe it as a variant.</summary>
    public const string PolymorphicSubtypeUnregistered = "polymorphic-subtype-unregistered";

    /// <summary>
    /// Two variants of one polymorphic type have the same name (the simple CLR name without its
    /// generic arity), which <c>is</c>, a <c>$variant</c> case and <c>onlyFor</c> could not tell
    /// apart; neither is described as a variant.
    /// </summary>
    public const string PolymorphicVariantNameConflict = "polymorphic-variant-name-conflict";

    /// <summary>
    /// A reference's stored kind does not fit its target: a string member naming a local guid
    /// field without <c>KeyAs = Guid</c>, or <c>KeyAs = Guid</c> on a member that is not a string
    /// or towards a local field that is not a guid. The case is dropped.
    /// </summary>
    public const string ReferenceKeyKindMismatch = "reference-key-kind-mismatch";

    /// <summary>A typed or multi-target reference case names a local entity id the model does not have; the case is dropped.</summary>
    public const string ReferenceCaseTargetUnknown = "reference-case-target-unknown";

    /// <summary>An item target's path is not an array of keyed objects on the target entity, or its field is not a member of the element; the case is dropped.</summary>
    public const string ReferenceItemUnknown = "reference-item-unknown";

    /// <summary>
    /// Reserved for the log-only hint that a guid member named <c>*Id</c> matches an entity but
    /// declares no reference; never published. No builder of this version emits it.
    /// </summary>
    public const string ReferenceCandidateUndeclared = "reference-candidate-undeclared";
}
