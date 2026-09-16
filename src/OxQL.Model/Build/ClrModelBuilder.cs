using System.Collections;
using System.Globalization;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
/// GeoJSON point, an interface, a custom scalar serializer) is <see cref="Kind.Unknown"/>.
/// </para>
/// </remarks>
public sealed class ClrModelBuilder
{
    /// <summary>The simple name of the base package's navigation-property attribute, read by name.</summary>
    public const string ReferenceIdAttributeName = "ReferenceIdAttribute";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoRetiredIds =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private readonly List<BuildFinding> findings = [];
    private readonly List<PendingReference> references = [];
    private readonly Dictionary<Type, TypeDef> pool = [];
    private readonly HashSet<string> reportedOpaque = new(StringComparer.Ordinal);
    private readonly NullabilityInfoContext nullability = new();

    private ClrModelBuilder()
    {
    }

    /// <summary>
    /// Builds the model from the entities of <paramref name="assemblies"/>.
    /// <paramref name="retiredIds"/> maps a current entity id to the ids it retired, as the
    /// host's schema options declare them.
    /// </summary>
    public static EntityModel Build(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<string, IReadOnlyList<string>>? retiredIds = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return new ClrModelBuilder().BuildCore(assemblies.ToList(), retiredIds ?? NoRetiredIds);
    }

    /// <summary>Builds the model from already scanned declarations; the overload tests and tooling use.</summary>
    public static EntityModel Build(IReadOnlyList<EntityDeclaration> declarations, IReadOnlyDictionary<string, IReadOnlyList<string>>? retiredIds = null)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        return new ClrModelBuilder().BuildCore(declarations, retiredIds ?? NoRetiredIds);
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

        foreach (var property in PublishedProperties(owner))
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

            if (property.GetCustomAttribute<OxQLReferenceAttribute>(inherit: false) is { } declared)
                references.Add(new PendingReference(member, $"{label}#{wire}", declared.Entity, declared.Field, ReferenceSource.Attribute));
            else if (declaredTargets.TryGetValue(wire, out var target))
                references.Add(new PendingReference(member, $"{label}#{wire}", target, null, ReferenceSource.ReferenceId));

            members.Add(member);
        }

        type.Members = members;
        type.Discriminator = DiscriminatorOf(owner);
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
            EnumValues =
            [
                .. type
                    .GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(field => field.IsLiteral)
                    .OrderBy(field => field.MetadataToken)
                    .Select(field => new EnumValueDef(
                        field.Name,
                        ConstantValue(field),
                        !field.IsDefined(typeof(ObsoleteAttribute), inherit: false))),
            ],
        };

        pool[type] = entry;

        return entry;
    }

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
            return BsonSerializer.LookupSerializer(type);
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
