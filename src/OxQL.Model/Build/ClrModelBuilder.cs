using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using OxQL.Model.Attributes;

namespace OxQL.Model.Build;

/// <summary>
/// Builds the model from CLR types through the MongoDB driver's serializer registry, the one
/// the repositories serialise with. Storage names, representations and whether a member is
/// stored at all are observed from the registry, never inferred from the CLR name.
/// </summary>
/// <remarks>
/// <para>
/// The walk must run after every serializer and class-map registration of the host and never
/// during service registration: the first <c>LookupSerializer</c> of a type freezes the answer,
/// and a registration for that type afterwards throws. The base package runs the build from
/// its schema startup filter for that reason. The builder itself registers nothing.
/// </para>
/// <para>
/// The wire view is every public readable instance property, most derived type first and in
/// declaration order within a type, the schema's rule. A member the registry has no
/// serialization info for (<c>[BsonIgnore]</c>, get-only) is in the wire view with
/// <see cref="MemberDef.Stored"/> false. A member whose serializer cannot describe members (a
/// GeoJSON point, a custom scalar serializer) is <see cref="Kind.Unknown"/>.
/// </para>
/// <para>
/// A polymorphic type's variants are the class maps the host registered (and the known types
/// they and <c>[BsonKnownTypes]</c> name) that are concrete and assignable to it. Their members
/// the type lacks are merged into it with <see cref="MemberDef.OnlyFor"/>; an interface or
/// abstract type the driver cannot describe but that has variants becomes a pooled object of
/// their members. A class map the build itself caused to be registered, by looking a type up,
/// never counts: the answer must not depend on how often or in which order models are built.
/// </para>
/// </remarks>
public sealed class ClrModelBuilder
{
    /// <summary>The simple name of the base package's navigation-property attribute, read by name.</summary>
    public const string ReferenceIdAttributeName = "ReferenceIdAttribute";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoRetiredIds =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The class maps a model build registered as a side effect of looking a type up; never variants.</summary>
    private static readonly ConcurrentDictionary<Type, byte> RegisteredByBuild = new();

    private static readonly Lock LookupGate = new();

    private readonly List<BuildFinding> findings = [];
    private readonly List<PendingReference> references = [];
    private readonly Dictionary<Type, TypeDef> pool = [];
    private readonly HashSet<string> reportedOpaque = new(StringComparer.Ordinal);
    private readonly HashSet<string> reportedUnregistered = new(StringComparer.Ordinal);
    private readonly NullabilityInfoContext nullability = new();
    private readonly List<(TypeDef Type, Type Owner, string Label, IReadOnlyList<Type> Variants)> pendingMerges = [];
    private readonly HashSet<Type> registered;
    private readonly IReadOnlyList<Assembly> candidateAssemblies;
    private Dictionary<Type, List<Type>>? subclassIndex;

    private ClrModelBuilder(IReadOnlyList<Assembly> candidateAssemblies)
    {
        registered = RegisteredVariantCandidates();
        this.candidateAssemblies = candidateAssemblies;
    }

    /// <summary>
    /// Builds the model from the entities of <paramref name="assemblies"/>.
    /// <paramref name="retiredIds"/> maps a current entity id to the ids it retired, as the
    /// host's schema options declare them.
    /// </summary>
    public static EntityModel Build(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<string, IReadOnlyList<string>>? retiredIds = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var list = assemblies.ToList();

        return new ClrModelBuilder(list).BuildCore(list, retiredIds ?? NoRetiredIds);
    }

    /// <summary>Builds the model from already scanned declarations; the overload tests and tooling use.</summary>
    public static EntityModel Build(IReadOnlyList<EntityDeclaration> declarations, IReadOnlyDictionary<string, IReadOnlyList<string>>? retiredIds = null)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        var assemblies = declarations.Select(declaration => declaration.ClrType.Assembly).Distinct().ToList();

        return new ClrModelBuilder(assemblies).BuildCore(declarations, retiredIds ?? NoRetiredIds);
    }

    private EntityModel BuildCore(IReadOnlyList<Assembly> assemblies, IReadOnlyDictionary<string, IReadOnlyList<string>> retiredIds) =>
        BuildCore(EntityScanner.Scan(assemblies, findings), retiredIds);

    private EntityModel BuildCore(IReadOnlyList<EntityDeclaration> declarations, IReadOnlyDictionary<string, IReadOnlyList<string>> retiredIds)
    {
        var entities = new List<EntityDef>();

        // Every entity is in the pool before any member is walked, so a member typed as
        // another entity points at that entity rather than at a second, structural copy.
        foreach (var declaration in declarations)
            pool[declaration.ClrType] = new TypeDef(declaration.Id, declaration.ClrType, isEntity: true);

        foreach (var declaration in declarations)
        {
            var root = pool[declaration.ClrType];

            DescribeMembers(root, declaration.ClrType, declaration.Id);

            entities.Add(new EntityDef(
                declaration.Id,
                declaration.DeclaredId,
                declaration.ClrType,
                declaration.Collection,
                declaration.Database,
                declaration.Extendable,
                WireNames.TypeLabel(declaration.ClrType),
                root));
        }

        // Merged last, once every type reachable from the entities is described: a variant can
        // reach its base through a member, and its own members must be complete when copied.
        // Merging pools variants not seen yet, which can queue merges of their own.
        for (var index = 0; index < pendingMerges.Count; index++)
            Merge(pendingMerges[index].Type, pendingMerges[index].Owner, pendingMerges[index].Label, pendingMerges[index].Variants);

        StructuralIds.Assign(pool);

        return ModelAssembler.Finish(entities, pool.Values, retiredIds, references, findings);
    }

    /// <summary>
    /// The public instance properties of a type in the order the document publishes them:
    /// the most derived type first, then each base type in turn, declaration order within a
    /// type, read off the metadata token.
    /// </summary>
    public static IEnumerable<PropertyInfo> PublishedProperties(Type type)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            var declared = current
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(property => property.MetadataToken);

            foreach (var property in declared)
                yield return property;
        }
    }

    private void DescribeMembers(TypeDef type, Type owner, string label)
    {
        var documentSerializer = TryLookup(owner, label) as IBsonDocumentSerializer;
        var declaredTargets = DeclaredTargets(owner, label);
        var members = new List<MemberDef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var variants = VariantsOf(owner, registered);

        // An interface, or an abstract type the driver cannot describe, is known only through
        // its variants: its members are theirs, every one merged with `onlyFor`. The driver's
        // interface serializer is a document serializer, but it stores none of the interface's
        // own properties, so they would be unstored copies of what every variant stores.
        var synthetic = variants.Count > 0 && (owner.IsInterface || documentSerializer is null);

        foreach (var property in synthetic ? Enumerable.Empty<PropertyInfo>() : PublishedProperties(owner))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            var wire = WireNames.Wire(property.Name);

            if (!seen.Add(wire))
                continue;

            BsonSerializationInfo? info = null;
            var stored = documentSerializer is not null && documentSerializer.TryGetMemberSerializationInfo(property.Name, out info);

            var member = new MemberDef(wire)
            {
                ClrName = property.Name,
                Stored = stored,
                StorageName = stored ? info!.ElementName : null,
                Nullable = IsNullable(property),
                DisplayName = WireNames.PropertyLabelOf(property.Name, wire),
            };

            FillShape(member, property.PropertyType, stored ? info!.Serializer : null, $"{label}#{wire}");

            member.Description = XmlDocs.Beside.Description(property);
            member.Deprecated = DeprecationOf(property);
            member.Constraints = ConstraintsOf(property, member.Kind);

            if (property.GetCustomAttribute<OxQLReferenceAttribute>(inherit: false) is { } declared)
                references.Add(new PendingReference(member, $"{label}#{wire}", declared.Entity, declared.Field, ReferenceSource.Attribute));
            else if (declaredTargets.TryGetValue(wire, out var target))
                references.Add(new PendingReference(member, $"{label}#{wire}", target, null, ReferenceSource.ReferenceId));

            members.Add(member);
        }

        type.Members = members;
        type.Discriminator = DiscriminatorOf(owner);
        type.Description = XmlDocs.Beside.Description(owner);

        if (variants.Count > 0)
            pendingMerges.Add((type, owner, label, variants));

        ReportUnregisteredSubtypes(owner, label);
    }

    /// <summary>
    /// Appends every wire name some variant has and the type has not, as a nullable copy
    /// carrying <see cref="MemberDef.OnlyFor"/>: variants in their ordinal order, members in the
    /// variant's order. Variants that disagree on a member's shape or storage name make it
    /// <see cref="Kind.Unknown"/>.
    /// </summary>
    private void Merge(TypeDef type, Type owner, string label, IReadOnlyList<Type> variants)
    {
        var members = type.Members.ToList();
        var own = new HashSet<string>(members.Select(member => member.WireName), StringComparer.Ordinal);
        var merged = new Dictionary<string, (MemberDef Copy, MemberDef First, List<string> Carriers)>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        var variantDefs = new List<VariantDef>();

        foreach (var variant in variants)
        {
            var name = VariantName(variant);
            var variantType = PoolObject(variant, label);

            variantDefs.Add(new VariantDef(name, DiscriminatorValueOf(variant), variantType));

            // A variant's own members only: the members merged into it come from its own
            // variants, which are variants of this type too and contribute themselves.
            foreach (var member in variantType.Members)
            {
                if (member.OnlyFor is not null || own.Contains(member.WireName))
                    continue;

                if (merged.TryGetValue(member.WireName, out var entry))
                {
                    entry.Carriers.Add(name);

                    if (!SameShape(entry.First, member) && !conflicts.Contains(member.WireName))
                        conflicts.Add(member.WireName);

                    continue;
                }

                var copy = CopyForVariants(member);

                merged[member.WireName] = (copy, member, [name]);
                members.Add(copy);

                foreach (var pending in references.Where(pending => ReferenceEquals(pending.Member, member)).ToList())
                    references.Add(pending with { Member = copy, OwnerLabel = $"{label}#{member.WireName}" });
            }
        }

        foreach (var (copy, _, carriers) in merged.Values)
            copy.OnlyFor = carriers;

        foreach (var wire in conflicts)
        {
            var (copy, first, carriers) = merged[wire];
            var storageDiffers = variantDefs
                .Select(variant => variant.Type.Member(wire))
                .Any(member => member is { OnlyFor: null } && (member.Stored != first.Stored || !string.Equals(member.StorageName, first.StorageName, StringComparison.Ordinal)));

            copy.Kind = Kind.Unknown;
            copy.Representation = Representation.None;
            copy.Type = null;
            copy.Of = null;
            copy.Value = null;
            copy.DictionaryRepresentation = null;
            copy.SnapshotOf = null;

            if (storageDiffers)
            {
                copy.Stored = false;
                copy.StorageName = null;
            }

            references.RemoveAll(pending => ReferenceEquals(pending.Member, copy));

            findings.Add(new BuildFinding(
                BuildCodes.PolymorphicMemberConflict,
                $"{label}#{wire}",
                storageDiffers
                    ? "The variants store the member under different element names, so the merged member is unknown and not stored."
                    : "The variants describe the member with different kinds or representations, so the merged member is unknown: projectable, not filterable.",
                string.Join(", ", carriers)));
        }

        type.Members = members;
        type.Variants = variantDefs;
        type.DiscriminatorElement = DiscriminatorElementOf(owner);
        type.DiscriminatorForm = variants.Any(IsHierarchical) ? DiscriminatorForm.Hierarchical : DiscriminatorForm.Scalar;
    }

    private static MemberDef CopyForVariants(MemberDef member) => new(member.WireName)
    {
        ClrName = member.ClrName,
        Stored = member.Stored,
        StorageName = member.StorageName,
        Nullable = true,
        DisplayName = member.DisplayName,
        Description = member.Description,
        Deprecated = member.Deprecated,
        Constraints = member.Constraints,
        Kind = member.Kind,
        Representation = member.Representation,
        Type = member.Type,
        Of = member.Of,
        Value = member.Value,
        DictionaryRepresentation = member.DictionaryRepresentation,
        SnapshotOf = member.SnapshotOf,
    };

    /// <summary>Whether two variants describe one wire name the same way: stored alike, and the same shape all the way down.</summary>
    private static bool SameShape(MemberDef left, MemberDef right) =>
        left.Stored == right.Stored
        && string.Equals(left.StorageName, right.StorageName, StringComparison.Ordinal)
        && SameShape((ShapeDef)left, (ShapeDef)right);

    private static bool SameShape(ShapeDef? left, ShapeDef? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.Kind == right.Kind
            && left.Representation == right.Representation
            && ReferenceEquals(left.Type, right.Type)
            && left.DictionaryRepresentation == right.DictionaryRepresentation
            && SameShape(left.Of, right.Of)
            && SameShape(left.Value, right.Value);
    }

    /// <summary>
    /// The types whose class maps count as registered for variant discovery: every class map
    /// registered other than by a model build's own lookups, and every known type such a map
    /// names, transitively.
    /// </summary>
    internal static HashSet<Type> RegisteredVariantCandidates()
    {
        var candidates = new HashSet<Type>();
        var queue = new Queue<BsonClassMap>();

        foreach (var map in BsonClassMap.GetRegisteredClassMaps())
            if (!RegisteredByBuild.ContainsKey(map.ClassType) && candidates.Add(map.ClassType))
                queue.Enqueue(map);

        while (queue.Count > 0)
            foreach (var known in queue.Dequeue().KnownTypes)
                if (candidates.Add(known) && BsonClassMap.IsClassMapRegistered(known))
                    queue.Enqueue(BsonClassMap.LookupClassMap(known));

        return candidates;
    }

    /// <summary>
    /// The variants of a type: the concrete classes among <paramref name="registered"/>, and the
    /// <c>[BsonKnownTypes]</c> the type, its bases and those known types declare, that are
    /// assignable to it; the type itself excluded; ordinally by name.
    /// </summary>
    internal static IReadOnlyList<Type> VariantsOf(Type owner, IReadOnlySet<Type> registered)
    {
        if (!(owner.IsClass || owner.IsInterface) || owner == typeof(object) || owner == typeof(string) || owner.ContainsGenericParameters)
            return [];

        var candidates = new HashSet<Type>(registered.Where(owner.IsAssignableFrom));
        var queue = new Queue<Type>();
        var visited = new HashSet<Type>();

        for (var current = owner; current is not null && current != typeof(object); current = current.BaseType)
            queue.Enqueue(current);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();

            if (!visited.Add(type))
                continue;

            foreach (var attribute in type.GetCustomAttributes<BsonKnownTypesAttribute>(inherit: false))
                foreach (var known in attribute.KnownTypes)
                    if (owner.IsAssignableFrom(known) && candidates.Add(known))
                        queue.Enqueue(known);
        }

        return candidates
            .Where(type => type != owner && type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
            .OrderBy(VariantName, StringComparer.Ordinal)
            .ThenBy(StructuralIds.ClrIdentity, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A variant's name: its CLR name without a generic arity suffix.</summary>
    internal static string VariantName(Type type)
    {
        var name = type.Name;
        var arity = name.IndexOf('`', StringComparison.Ordinal);

        return arity > 0 ? name[..arity] : name;
    }

    /// <summary>
    /// Reports every concrete subclass (or implementation) of a described type, found in the
    /// scanned assemblies, that is not a variant because the host registered no class map for it.
    /// </summary>
    private void ReportUnregisteredSubtypes(Type owner, string label)
    {
        if (!(owner.IsClass || owner.IsInterface) || owner == typeof(object) || !reportedUnregistered.Add(label))
            return;

        subclassIndex ??= SubclassIndex(candidateAssemblies);

        if (!subclassIndex.TryGetValue(owner, out var subtypes))
            return;

        var variants = VariantsOf(owner, registered);

        foreach (var subtype in subtypes.Where(subtype => !variants.Contains(subtype)).OrderBy(StructuralIds.ClrIdentity, StringComparer.Ordinal))
            findings.Add(new BuildFinding(
                BuildCodes.PolymorphicSubtypeUnregistered,
                label,
                $"The subtype '{VariantName(subtype)}' has no registered class map, so its members are not described as a variant. Register its class map before the model is built.",
                subtype.FullName));
    }

    /// <summary>Every concrete, closed class of the assemblies under each base class and interface it has.</summary>
    internal static Dictionary<Type, List<Type>> SubclassIndex(IEnumerable<Assembly> assemblies)
    {
        var index = new Dictionary<Type, List<Type>>();

        foreach (var assembly in assemblies.Distinct())
        {
            Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = [.. exception.Types.Where(type => type is not null).Select(type => type!)];
            }

            foreach (var type in types)
            {
                if (type is not { IsClass: true, IsAbstract: false } || type.ContainsGenericParameters)
                    continue;

                foreach (var supertype in Supertypes(type))
                {
                    if (!index.TryGetValue(supertype, out var list))
                        index[supertype] = list = [];

                    list.Add(type);
                }
            }
        }

        return index;
    }

    private static IEnumerable<Type> Supertypes(Type type)
    {
        for (var current = type.BaseType; current is not null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
            yield return current;

        foreach (var contract in type.GetInterfaces())
            yield return contract;
    }

    /// <summary>Describes one shape. <c>Nullable&lt;T&gt;</c> and the driver's nullable serializer are unwrapped first; composites recurse, scalars stop.</summary>
    private void FillShape(ShapeDef shape, Type type, IBsonSerializer? serializer, string label)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        serializer = UnwrapNullable(serializer);

        if (Kinds.ScalarKindOf(type) is { } scalar)
        {
            shape.Kind = scalar;
            shape.Representation = RepresentationOf(serializer, type);

            if (scalar == Kind.Enum)
                shape.Type = PoolEnum(type);

            return;
        }

        if (TryGetDictionaryValueType(type, out var valueType, out var untypedDictionary))
        {
            if (untypedDictionary)
                ReportUntyped(label, "dictionary");

            var dictionary = serializer as IBsonDictionarySerializer;

            shape.Kind = Kind.Dictionary;
            shape.DictionaryRepresentation = dictionary?.DictionaryRepresentation;
            shape.Value = new ShapeDef();

            FillShape(shape.Value, valueType, dictionary?.ValueSerializer, label);
            return;
        }

        if (TryGetElementType(type, out var elementType, out var untypedCollection))
        {
            if (untypedCollection)
                ReportUntyped(label, "collection");

            IBsonSerializer? itemSerializer = null;

            if (serializer is IBsonArraySerializer array && array.TryGetItemSerializationInfo(out var itemInfo))
                itemSerializer = itemInfo.Serializer;

            shape.Kind = Kind.Array;
            shape.Of = new ShapeDef();

            FillShape(shape.Of, elementType, itemSerializer, label);
            return;
        }

        // An object: describable only through a document serializer. The member's own
        // serializer decides for a stored member; the registry's decides for the wire view of
        // an unstored one.
        var effective = serializer ?? TryLookup(type, label);

        if (effective is null)
        {
            shape.Kind = Kind.Unknown;
            return;
        }

        if (effective is not IBsonDocumentSerializer)
        {
            // An interface or abstract type the host registered implementations of: described
            // as the pooled union of its variants.
            if ((type.IsInterface || type.IsAbstract) && VariantsOf(type, registered).Count > 0)
            {
                var pooled = PoolObject(type, label);

                shape.Kind = Kind.Object;
                shape.Type = pooled;
                shape.SnapshotOf = pooled.IsEntity ? pooled.PoolId : null;
                return;
            }

            shape.Kind = Kind.Unknown;

            if (reportedOpaque.Add(label))
                findings.Add(new BuildFinding(
                    BuildCodes.MemberSerializerOpaque,
                    label,
                    "The member's serializer does not describe members, so the member is unknown: projectable, not filterable.",
                    $"{type.FullName}: {effective.GetType().Name}"));

            return;
        }

        var target = PoolObject(type, label);

        shape.Kind = Kind.Object;
        shape.Type = target;
        shape.SnapshotOf = target.IsEntity ? target.PoolId : null;
    }

    /// <summary>The pooled entry of an object type, describing it first when it is new.</summary>
    private TypeDef PoolObject(Type type, string label)
    {
        if (pool.TryGetValue(type, out var existing))
            return existing;

        var entry = new TypeDef($"working:{pool.Count}", type, isEntity: false);

        pool[type] = entry;
        DescribeMembers(entry, type, StructuralIds.ReadableId(type));

        return entry;
    }

    private TypeDef PoolEnum(Type type)
    {
        if (pool.TryGetValue(type, out var existing))
            return existing;

        var entry = new TypeDef($"working:{pool.Count}", type, isEntity: false)
        {
            IsEnum = true,
            EnumFlags = type.IsDefined(typeof(FlagsAttribute), inherit: false),
            Description = XmlDocs.Beside.Description(type),
            EnumValues =
            [
                .. type
                    .GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(field => field.IsLiteral)
                    .OrderBy(field => field.MetadataToken)
                    .Select(field => new EnumValueDef(
                        field.Name,
                        ConstantValue(field),
                        !field.IsDefined(typeof(ObsoleteAttribute), inherit: false),
                        XmlDocs.Beside.Description(field))),
            ],
        };

        pool[type] = entry;

        return entry;
    }

    /// <summary>The member's deprecation from <c>[Obsolete]</c>, its message as the note.</summary>
    private static DeprecationDef? DeprecationOf(PropertyInfo property) =>
        property.GetCustomAttribute<ObsoleteAttribute>(inherit: false) is { } obsolete
            ? new DeprecationDef { Note = string.IsNullOrWhiteSpace(obsolete.Message) ? null : obsolete.Message.Trim() }
            : null;

    /// <summary>
    /// The member's DataAnnotations constraints: the smaller of <c>[MaxLength]</c> and
    /// <c>[StringLength]</c> on a string member, the <c>[Range]</c> bounds (a non-finite bound is
    /// no bound) and the <c>[RegularExpression]</c> pattern. Null when there are none.
    /// </summary>
    private static ConstraintsDef? ConstraintsOf(PropertyInfo property, Kind kind)
    {
        int? maxLength = null;

        if (kind == Kind.String)
        {
            if (property.GetCustomAttribute<MaxLengthAttribute>(inherit: false) is { Length: > 0 } max)
                maxLength = max.Length;

            if (property.GetCustomAttribute<StringLengthAttribute>(inherit: false) is { MaximumLength: > 0 } length)
                maxLength = maxLength is { } other ? Math.Min(other, length.MaximumLength) : length.MaximumLength;
        }

        var range = property.GetCustomAttribute<RangeAttribute>(inherit: false);
        var pattern = property.GetCustomAttribute<RegularExpressionAttribute>(inherit: false)?.Pattern;

        var constraints = new ConstraintsDef
        {
            MaxLength = maxLength,
            Min = range is null ? null : Bound(range.Minimum),
            Max = range is null ? null : Bound(range.Maximum),
            Pattern = string.IsNullOrEmpty(pattern) ? null : pattern,
        };

        return constraints is { MaxLength: null, Min: null, Max: null, Pattern: null } ? null : constraints;
    }

    /// <summary>A <c>[Range]</c> bound as invariant text: a number formatted round-trippably, a string bound as written.</summary>
    private static string? Bound(object? bound) => bound switch
    {
        null => null,
        double number when !double.IsFinite(number) => null,
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    /// <summary>The declared value of an enum member as a signed 64-bit integer; a value past <c>long.MaxValue</c> wraps.</summary>
    private static long ConstantValue(FieldInfo field) =>
        field.GetRawConstantValue() switch
        {
            null => 0L,
            ulong unsigned => unchecked((long)unsigned),
            var raw => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
        };

    private IBsonSerializer? TryLookup(Type type, string label)
    {
        try
        {
            return Tracked(() => BsonSerializer.LookupSerializer(type));
        }
        catch (Exception exception)
        {
            findings.Add(new BuildFinding(
                BuildCodes.MemberSerializerUnavailable,
                label,
                "The driver has no serializer for the type, so the member is unknown: projectable, not filterable.",
                $"{type.FullName}: {exception.GetType().Name}: {exception.Message}"));

            return null;
        }
    }

    /// <summary>The serializer behind the driver's nullable wrapper, or the serializer itself.</summary>
    private static IBsonSerializer? UnwrapNullable(IBsonSerializer? serializer)
    {
        while (serializer is IChildSerializerConfigurable child
               && serializer.GetType().IsGenericType
               && serializer.GetType().GetGenericTypeDefinition() == typeof(NullableSerializer<>))
            serializer = child.ChildSerializer;

        return serializer;
    }

    /// <summary>
    /// The representation a scalar's serializer reports. The driver's enum serializer reports
    /// zero for "the underlying type", which is read as the integer type it writes.
    /// </summary>
    private static Representation RepresentationOf(IBsonSerializer? serializer, Type type)
    {
        switch (serializer)
        {
            case null:
                return Representation.None;

            case GuidSerializer guid:
                return new Representation(guid.Representation, guid.GuidRepresentation);

            case IRepresentationConfigurable configurable:
                var representation = configurable.Representation;

                if (representation == 0 && type.IsEnum)
                    representation = Enum.GetUnderlyingType(type) == typeof(long) || Enum.GetUnderlyingType(type) == typeof(ulong)
                        ? BsonType.Int64
                        : BsonType.Int32;

                return representation == 0 ? Representation.None : Representation.Of(representation);

            default:
                return Representation.None;
        }
    }

    /// <summary>
    /// Runs a registry call and records every class map it registered as a build's own, so no
    /// build reads it as a registration of the host's. The gate keeps concurrent builds' records
    /// apart; a registration by other code inside that window is recorded too, which can only
    /// ever leave a variant out.
    /// </summary>
    private static T Tracked<T>(Func<T> call)
    {
        lock (LookupGate)
        {
            var before = BsonClassMap.GetRegisteredClassMaps().Select(map => map.ClassType).ToHashSet();

            try
            {
                return call();
            }
            finally
            {
                foreach (var map in BsonClassMap.GetRegisteredClassMaps())
                    if (!before.Contains(map.ClassType))
                        RegisteredByBuild.TryAdd(map.ClassType, 0);
            }
        }
    }

    /// <summary>The discriminator value the driver writes for a registered variant.</summary>
    private static string DiscriminatorValueOf(Type variant) =>
        Tracked(() => BsonClassMap.LookupClassMap(variant).Discriminator) ?? VariantName(variant);

    /// <summary>The element a polymorphic type's discriminator is stored under, by the convention the driver looks up for it.</summary>
    private static string DiscriminatorElementOf(Type type)
    {
        try
        {
            return Tracked(() => BsonSerializer.LookupDiscriminatorConvention(type)).ElementName;
        }
        catch (Exception)
        {
            return "_t";
        }
    }

    /// <summary>Whether a variant is stored in the hierarchical form: its class map or one above it is a root class.</summary>
    private static bool IsHierarchical(Type variant) =>
        Tracked(() =>
        {
            var map = BsonClassMap.LookupClassMap(variant);

            return map.IsRootClass || map.HasRootClass;
        });

    /// <summary>The discriminator value of a polymorphic type: abstract, or derived from a class other than <c>object</c>.</summary>
    private static string? DiscriminatorOf(Type type)
    {
        var polymorphic = type.IsAbstract || (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType));

        if (!polymorphic)
            return null;

        return BsonClassMap.IsClassMapRegistered(type) ? BsonClassMap.LookupClassMap(type).Discriminator : type.Name;
    }

    /// <summary>
    /// Whether a client can read null out of a member: the annotation's read state, or what
    /// the runtime guarantees when the declaring assembly carries no annotations.
    /// </summary>
    private bool IsNullable(PropertyInfo property) =>
        nullability.Create(property).ReadState switch
        {
            NullabilityState.Nullable => true,
            NullabilityState.NotNull => false,
            _ => !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null,
        };

    /// <summary>
    /// The wire names of the id members a type declares a target for through <c>[ReferenceId]</c>,
    /// each mapped to the entity id of the navigation property's type. The attribute is read by
    /// name: it sits on the navigation property and names the paired id property.
    /// </summary>
    private IReadOnlyDictionary<string, string> DeclaredTargets(Type owner, string label)
    {
        Dictionary<string, string>? targets = null;

        foreach (var navigation in PublishedProperties(owner))
        {
            var declaration = navigation.GetCustomAttributes(inherit: false).OfType<Attribute>().FirstOrDefault(candidate => candidate.GetType().Name == ReferenceIdAttributeName);

            if (declaration is null)
                continue;

            var idPropertyName = declaration.GetType().GetProperty("ReferenceIdPropertyName")?.GetValue(declaration) as string;
            var navigationType = Nullable.GetUnderlyingType(navigation.PropertyType) ?? navigation.PropertyType;
            var target = $"{label}#{WireNames.Wire(navigation.Name)}";

            if (!pool.TryGetValue(navigationType, out var entity) || !entity.IsEntity)
            {
                findings.Add(new BuildFinding(
                    BuildCodes.ReferenceDeclarationUnresolved,
                    target,
                    "The navigation property's type is not an entity of this host, so no reference is emitted.",
                    $"{owner.FullName}.{navigation.Name}: {navigationType.FullName}"));

                continue;
            }

            var idProperty = idPropertyName is null ? null : owner.GetProperty(idPropertyName, BindingFlags.Public | BindingFlags.Instance);

            if (idProperty is null)
            {
                findings.Add(new BuildFinding(
                    BuildCodes.ReferenceDeclarationUnresolved,
                    target,
                    $"The declaration names the id property '{idPropertyName}', which the type does not have.",
                    $"{owner.FullName}.{navigation.Name}"));

                continue;
            }

            targets ??= new Dictionary<string, string>(StringComparer.Ordinal);
            targets[WireNames.Wire(idProperty.Name)] = entity.PoolId;
        }

        return targets ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
    }

    private void ReportUntyped(string label, string shape) =>
        findings.Add(new BuildFinding(
            BuildCodes.CollectionUntyped,
            label,
            $"The {shape} declares no element type, so its values are described as unknown."));

    /// <summary>Whether the type is a dictionary, and its value type; a non-generic dictionary is untyped and its values are <c>object</c>.</summary>
    private static bool TryGetDictionaryValueType(Type type, out Type valueType, out bool untyped)
    {
        untyped = false;

        foreach (var candidate in Interfaces(type))
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();

            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                valueType = candidate.GetGenericArguments()[1];
                return true;
            }
        }

        valueType = typeof(object);
        untyped = typeof(IDictionary).IsAssignableFrom(type);

        return untyped;
    }

    /// <summary>Whether the type is a collection, and its element type; a non-generic collection is untyped and its elements are <c>object</c>.</summary>
    private static bool TryGetElementType(Type type, out Type elementType, out bool untyped)
    {
        untyped = false;

        if (type.IsArray)
        {
            elementType = type.GetElementType() ?? typeof(object);
            return true;
        }

        foreach (var candidate in Interfaces(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = candidate.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = typeof(object);
        untyped = typeof(IEnumerable).IsAssignableFrom(type);

        return untyped;
    }

    /// <summary>The type's interfaces, the type itself included when it is one.</summary>
    private static IEnumerable<Type> Interfaces(Type type)
    {
        if (type.IsInterface)
            yield return type;

        foreach (var candidate in type.GetInterfaces())
            yield return candidate;
    }
}
