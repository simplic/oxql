using System.Reflection;
using OxQL.Core.Attributes;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// One entity of the corpus: the service that owns it, the collection and model class the rows
/// are stored through, whether it carries an addon bag and which definitions its organisations
/// declare, and its rows per organisation.
/// </summary>
public sealed class CorpusEntity
{
    private readonly Func<Org, IReadOnlyList<CorpusRow>> rows;

    internal CorpusEntity(
        LabService service, Type modelType, bool hasAddonBag,
        Func<Org, IReadOnlyList<CorpusRow>> rows,
        IReadOnlyDictionary<Org, IReadOnlyList<AddonDef>>? addonDefinitions = null)
    {
        var declared = modelType.GetCustomAttribute<OxQLTypeAttribute>()
            ?? throw new ArgumentException($"{modelType.Name} carries no [OxQLType].", nameof(modelType));

        Id = declared.TypeName;
        Collection = declared.CollectionName;
        Extendable = declared.Extendable;
        Service = service;
        ModelType = modelType;
        HasAddonBag = hasAddonBag;
        AddonDefinitions = addonDefinitions ?? new Dictionary<Org, IReadOnlyList<AddonDef>>();
        this.rows = rows;
    }

    /// <summary>The entity id: what a request names in <c>entityType</c>.</summary>
    public string Id { get; }

    public LabService Service { get; }

    /// <summary>The collection the rows are stored in, from the model's <c>[OxQLType]</c>.</summary>
    public string Collection { get; }

    public Type ModelType { get; }

    /// <summary>The wire name of the key member: <c>id</c>, or <c>code</c> for <c>conformance.ref</c>.</summary>
    public string KeyPath => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(
        MongoDB.Bson.Serialization.BsonClassMap.LookupClassMap(ModelType).IdMemberMap.MemberName);

    public bool Extendable { get; }

    /// <summary>Whether the entity has a root <c>addon</c> member the corpus fills.</summary>
    public bool HasAddonBag { get; }

    /// <summary>The addon definitions each organisation declares, in the order they are stored.</summary>
    public IReadOnlyDictionary<Org, IReadOnlyList<AddonDef>> AddonDefinitions { get; }

    /// <summary>The rows of one organisation, in ordinal order.</summary>
    public IReadOnlyList<CorpusRow> Rows(Org org = Org.A) => rows(org);

    public override string ToString() => Id;
}
