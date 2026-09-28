using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Options;

namespace OxQL.Model.Build;

/// <summary>The inputs a document build takes beside the document.</summary>
public sealed record DocumentModelOptions
{
    /// <summary>The collection and database per entity id; an entity without an entry is stored under its id.</summary>
    public IReadOnlyDictionary<string, EntityStorage>? Storage { get; init; }

    /// <summary>
    /// Storage names the document cannot carry, keyed <c>poolId#wireName</c>: a storage name
    /// overrides the derivation, null marks the member as not stored.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? MemberStorage { get; init; }

    /// <summary>
    /// How a dictionary member is stored, keyed <c>poolId#wireName</c>; the document cannot
    /// carry it and every dictionary in the fleet is stored as a document, the default.
    /// </summary>
    public IReadOnlyDictionary<string, DictionaryRepresentation>? DictionaryRepresentations { get; init; }

    /// <summary>Whether a <c>references</c> member the document marks <c>inferred</c> is read; declared references only by default.</summary>
    public bool IncludeInferredReferences { get; init; }
}

/// <summary>Where an entity is stored.</summary>
public sealed record EntityStorage(string Collection, string? Database = null);

/// <summary>
/// Builds the model from an Ox Schema document (format 1.x). Storage names follow the
/// document's rule: <c>storageName</c> where published, otherwise the wire name with its first
/// letter upper-cased and <c>id</c> as <c>_id</c> at every depth. Representations are the
/// defaults the services store each kind in. Tests, tooling and the Studio use it; a host uses
/// <see cref="ClrModelBuilder"/>.
/// </summary>
public sealed class DocumentModelBuilder
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoRetiredIds =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private readonly DocumentModelOptions options;
    private readonly List<BuildFinding> findings = [];
    private readonly List<PendingReference> references = [];
    private readonly Dictionary<string, TypeDef> pool = new(StringComparer.Ordinal);

    private DocumentModelBuilder(DocumentModelOptions options)
    {
        this.options = options;
    }

    /// <summary>Builds the model from the document text.</summary>
    /// <exception cref="InvalidDataException">The text is not a format 1.x schema document.</exception>
    public static EntityModel Build(string json, DocumentModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);

        return Build(document.RootElement, options);
    }

    /// <summary>Builds the model from a parsed document.</summary>
    /// <exception cref="InvalidDataException">The element is not a format 1.x schema document.</exception>
    public static EntityModel Build(JsonElement document, DocumentModelOptions? options = null) =>
        new DocumentModelBuilder(options ?? new DocumentModelOptions()).BuildCore(document);

    /// <summary>The representation a kind is stored in when nothing pins another.</summary>
    public static Representation DefaultRepresentation(Kind kind) => kind switch
    {
        Kind.String => Representation.Of(BsonType.String),
        Kind.Int => Representation.Of(BsonType.Int32),
        Kind.Long => Representation.Of(BsonType.Int64),
        Kind.Double => Representation.Of(BsonType.Double),
        Kind.Decimal => Representation.Of(BsonType.Decimal128),
        Kind.Bool => Representation.Of(BsonType.Boolean),
        Kind.Guid => new Representation(BsonType.Binary, GuidRepresentation.Standard),
        Kind.Date => Representation.Of(BsonType.DateTime),
        Kind.DateTime => Representation.Of(BsonType.DateTime),
        Kind.TimeSpan => Representation.Of(BsonType.String),
        Kind.Enum => Representation.Of(BsonType.Int32),
        Kind.Binary => Representation.Of(BsonType.Binary),
        _ => Representation.None,
    };

    private EntityModel BuildCore(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A schema document is a JSON object.");

        var version = document.TryGetProperty("schemaVersion", out var versionElement) ? versionElement.GetString() : null;

        if (version is null || !version.StartsWith("1.", StringComparison.Ordinal))
            throw new InvalidDataException($"The schema document format '{version ?? "(none)"}' is not one this reader understands (1.x).");

        if (!document.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The schema document has no 'types' member.");

        // Two phases, so a pointer resolves before the entry it points at is filled.
        foreach (var entry in types.EnumerateObject())
            pool[entry.Name] = CreateEntry(entry.Name, entry.Value);

        foreach (var entry in types.EnumerateObject())
            if (!pool[entry.Name].IsEnum)
            {
                pool[entry.Name].Members = DescribeMembers(entry.Name, entry.Value);
                DescribeVariants(pool[entry.Name], entry.Value);
            }

        var entities = new List<EntityDef>();
        var retired = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var entry in types.EnumerateObject())
        {
            var type = pool[entry.Name];

            if (!type.IsEntity)
                continue;

            var storage = options.Storage?.GetValueOrDefault(entry.Name);

            entities.Add(new EntityDef(
                entry.Name,
                entry.Name,
                clrType: null,
                storage?.Collection ?? entry.Name,
                storage?.Database,
                ReadBool(entry.Value, "extendable"),
                ReadString(entry.Value, "displayName"),
                type));

            // The retired ids are the aliases that are not legacy `$ClassName` model ids.
            if (entry.Value.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                retired[entry.Name] = aliases
                    .EnumerateArray()
                    .Select(alias => alias.GetString())
                    .Where(alias => !string.IsNullOrWhiteSpace(alias) && !alias.StartsWith('$'))
                    .Select(alias => alias!)
                    .ToList();
        }

        return ModelAssembler.Finish(entities, pool.Values, retired, references, findings);
    }

    private static TypeDef CreateEntry(string id, JsonElement entry)
    {
        var isEntity = ReadBool(entry, "entity");
        var type = new TypeDef(id, clrType: null, isEntity);

        if (ReadString(entry, "kind") == Kinds.NameOf(Kind.Enum))
        {
            type.IsEnum = true;
            type.EnumFlags = ReadBool(entry, "flags");

            if (entry.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                type.EnumValues = values.EnumerateArray().Select(value => new EnumValueDef(
                    ReadString(value, "name") ?? "",
                    ReadLong(value, "value"),
                    !value.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False)).ToList();
        }

        return type;
    }

    private List<MemberDef> DescribeMembers(string poolId, JsonElement entry)
    {
        var members = new List<MemberDef>();

        if (!entry.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
            return members;

        foreach (var descriptor in properties.EnumerateArray())
        {
            var wire = ReadString(descriptor, "name");

            if (string.IsNullOrEmpty(wire))
                continue;

            var label = $"{poolId}#{wire}";
            var member = new MemberDef(wire)
            {
                Nullable = ReadBool(descriptor, "nullable"),
                DisplayName = ReadString(descriptor, "displayName"),
            };

            if (options.MemberStorage is not null && options.MemberStorage.TryGetValue(label, out var overridden))
            {
                member.Stored = overridden is not null;
                member.StorageName = overridden;
            }
            else
            {
                member.Stored = true;
                member.StorageName = ReadString(descriptor, "storageName") ?? WireNames.DerivedStorage(wire);
            }

            FillShape(member, descriptor, label);

            if (descriptor.TryGetProperty("onlyFor", out var onlyFor) && onlyFor.ValueKind == JsonValueKind.Array)
                member.OnlyFor = onlyFor
                    .EnumerateArray()
                    .Where(name => name.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(name.GetString()))
                    .Select(name => name.GetString()!)
                    .ToList();

            if (member.Kind == Kind.Dictionary && options.DictionaryRepresentations is not null
                && options.DictionaryRepresentations.TryGetValue(label, out var representation))
                member.DictionaryRepresentation = representation;

            if (descriptor.TryGetProperty("references", out var reference) && reference.ValueKind == JsonValueKind.Object)
            {
                var inferred = ReadBool(reference, "inferred");
                var target = ReadString(reference, "entity");

                if (!string.IsNullOrEmpty(target) && (!inferred || options.IncludeInferredReferences))
                    references.Add(new PendingReference(member, label, target, ReadString(reference, "field"), ReferenceSource.Document));
            }

            members.Add(member);
        }

        return members;
    }

    /// <summary>
    /// A polymorphic entry's <c>discriminator</c> and <c>variants</c> (format 1.1). A document
    /// carries no discriminator value per variant, so the variant's name stands in for it; a
    /// variant whose pointer has no target is a dangling pointer and left out.
    /// </summary>
    private void DescribeVariants(TypeDef type, JsonElement entry)
    {
        if (!entry.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
            return;

        var described = new List<VariantDef>();

        foreach (var variant in variants.EnumerateArray())
        {
            var name = ReadString(variant, "name");
            var pointer = ReadString(variant, "type");

            if (string.IsNullOrEmpty(name))
                continue;

            if (pointer is null || !pool.TryGetValue(StripPointer(pointer), out var target))
            {
                findings.Add(new BuildFinding(
                    BuildCodes.DanglingTypePointer,
                    $"{type.PoolId}@{name}",
                    $"The variant's pointer '{pointer}' has no target in the pool, so the variant is left out."));

                continue;
            }

            described.Add(new VariantDef(name, name, target));
        }

        if (described.Count == 0)
            return;

        type.Variants = described;

        if (entry.TryGetProperty("discriminator", out var discriminator) && discriminator.ValueKind == JsonValueKind.Object)
        {
            type.DiscriminatorElement = ReadString(discriminator, "element") ?? "_t";
            type.DiscriminatorForm = ReadString(discriminator, "form") == "hierarchical" ? DiscriminatorForm.Hierarchical : DiscriminatorForm.Scalar;
        }
        else
        {
            type.DiscriminatorElement = "_t";
            type.DiscriminatorForm = DiscriminatorForm.Scalar;
        }
    }

    private void FillShape(ShapeDef shape, JsonElement descriptor, string label)
    {
        var kind = Kinds.Parse(ReadString(descriptor, "kind")) ?? Kind.Unknown;

        shape.Kind = kind;
        shape.Representation = DefaultRepresentation(kind);
        shape.SnapshotOf = ReadString(descriptor, "snapshotOf");

        switch (kind)
        {
            case Kind.Object:
            case Kind.Enum:
                var pointer = ReadString(descriptor, "type");
                var target = pointer is null ? null : pool.GetValueOrDefault(StripPointer(pointer));

                if (target is null)
                {
                    shape.Kind = Kind.Unknown;
                    shape.Representation = Representation.None;
                    findings.Add(new BuildFinding(
                        BuildCodes.DanglingTypePointer,
                        label,
                        $"The pointer '{pointer}' has no target in the pool, so the member is unknown."));

                    return;
                }

                shape.Type = target;
                return;

            case Kind.Array when descriptor.TryGetProperty("of", out var of) && of.ValueKind == JsonValueKind.Object:
                shape.Of = new ShapeDef();
                FillShape(shape.Of, of, label);
                return;

            case Kind.Dictionary when descriptor.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object:
                shape.Value = new ShapeDef();
                FillShape(shape.Value, value, label);
                return;

            default:
                return;
        }
    }

    private static string StripPointer(string pointer) =>
        pointer.StartsWith("#/types/", StringComparison.Ordinal) ? pointer["#/types/".Length..] : pointer;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>An enum value: a JSON number, or a JSON string for a value past 2⁵³.</summary>
    private static long ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }
}
