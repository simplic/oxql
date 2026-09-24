using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Model.Addon;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// An organisation's addon definitions, read per request from the host's own database in the
/// shape the base package stores them (<see cref="Collection"/>, PascalCase members, Guids as
/// subtype 4). Unlike the base package it caches nothing, so a test that writes a definition
/// sees it on the next query.
/// </summary>
public sealed class TestAddonSource(IMongoDatabase database) : IAddonDefinitionSource
{
    /// <summary>Where the base package stores addon definitions, in every service's own database.</summary>
    public const string Collection = "model_definition.addon_definition";

    private static readonly Dictionary<string, AddonKind> Kinds = new(StringComparer.Ordinal)
    {
        ["string"] = AddonKind.String,
        ["int"] = AddonKind.Int,
        ["long"] = AddonKind.Long,
        ["double"] = AddonKind.Double,
        ["decimal"] = AddonKind.Decimal,
        ["bool"] = AddonKind.Bool,
        ["date"] = AddonKind.Date,
        ["dateTime"] = AddonKind.DateTime,
        ["guid"] = AddonKind.Guid,
        ["object"] = AddonKind.Object,
    };

    public async ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument
        {
            ["Entity"] = entity,
            ["OrganizationId"] = new BsonBinaryData(organisation, GuidRepresentation.Standard),
            ["IsDeleted"] = false,
        };

        var documents = await database.GetCollection<BsonDocument>(Collection).Find(filter).ToListAsync(cancellationToken);

        return documents.Select(ToDefinition).ToList();
    }

    /// <summary>A stored kind the engine does not know reads as an untyped container, as in the base package.</summary>
    private static AddonDefinition ToDefinition(BsonDocument document) => new()
    {
        Id = document["_id"].AsGuid,
        Entity = document["Entity"].AsString,
        Path = document["Path"].AsString,
        Kind = Kinds.TryGetValue(document.GetValue("Kind", "").AsString, out var kind) ? kind : AddonKind.Object,
        Values = document.GetValue("Values", BsonNull.Value) is BsonArray { Count: > 0 } values
            ? [.. values.Select(value => new AddonValue(value["Value"].AsString, NullableString(value.AsBsonDocument.GetValue("Label", BsonNull.Value))))]
            : null,
        DisplayName = NullableString(document.GetValue("DisplayName", BsonNull.Value)),
        Description = NullableString(document.GetValue("Description", BsonNull.Value)),
        Retired = document.GetValue("Retired", false).AsBoolean,
    };

    private static string? NullableString(BsonValue value) => value.IsBsonNull ? null : value.AsString;

    /// <summary>A stored definition in the base package's document shape; what a seeder writes.</summary>
    public static BsonDocument Document(
        Guid id, Guid organisation, string entity, string path, string kind,
        string? displayName = null, bool retired = false, IReadOnlyList<(string Value, string? Label)>? values = null) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["OrganizationId"] = new BsonBinaryData(organisation, GuidRepresentation.Standard),
        ["IsDeleted"] = false,
        ["Entity"] = entity,
        ["Path"] = path,
        ["Kind"] = kind,
        ["Values"] = values is null ? BsonNull.Value : new BsonArray(values.Select(value => new BsonDocument { ["Value"] = value.Value, ["Label"] = (BsonValue?)value.Label ?? BsonNull.Value })),
        ["DisplayName"] = (BsonValue?)displayName ?? BsonNull.Value,
        ["Description"] = BsonNull.Value,
        ["Retired"] = retired,
    };
}
