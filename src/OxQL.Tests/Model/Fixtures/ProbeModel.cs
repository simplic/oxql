using System.Text.Json;
using MongoDB.Bson.Serialization.Options;
using OxQL.Model;
using OxQL.Model.Build;

namespace OxQL.Tests.Model.Fixtures;

/// <summary>The models the tests share: one CLR build of the fixture assembly, one document build per vendored document.</summary>
internal static class ProbeModel
{
    /// <summary>The retired ids the fixture host declares, in the shape the base package's schema options hand over.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RetiredIds =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["probe.order"] = ["probe.orders_old", "orders"],
        };

    /// <summary>Where the fixture entities are stored, so the document build carries the same collections as the attributes.</summary>
    public static readonly IReadOnlyDictionary<string, EntityStorage> Storage =
        new Dictionary<string, EntityStorage>(StringComparer.Ordinal)
        {
            ["probe.order"] = new("orders"),
            ["probe.customer"] = new("customers"),
            ["probe.supplier"] = new("suppliers", "purchasing"),
            ["probe.base_only"] = new("bases"),
            ["probe.shared_base"] = new("shared"),
            ["probe.keyless"] = new("keyless"),
        };

    /// <summary>
    /// The storage facts the document cannot carry and only the registry knows: the
    /// <c>[BsonIgnore]</c> member, the two get-only members and the <c>[BsonElement]</c> rename.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> MemberStorage =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["probe.order#hidden"] = null,
            ["probe.order#computed"] = null,
            ["t_figure#figureKind"] = null,
            ["probe.order#renamed"] = "x",
        };

    public static readonly IReadOnlyDictionary<string, DictionaryRepresentation> DictionaryRepresentations =
        new Dictionary<string, DictionaryRepresentation>(StringComparer.Ordinal)
        {
            ["probe.order#priceList"] = DictionaryRepresentation.ArrayOfDocuments,
        };

    private static readonly Lazy<EntityModel> clr = new(() => ClrModelBuilder.Build([typeof(OrderModel).Assembly], RetiredIds));

    /// <summary>The CLR build of the fixture assembly.</summary>
    public static EntityModel Clr => clr.Value;

    /// <summary>The document build of <c>probe.json</c> with the registry facts supplied as overrides.</summary>
    public static EntityModel Document() => DocumentModelBuilder.Build(ReadFixture("probe.json"), new DocumentModelOptions
    {
        Storage = Storage,
        MemberStorage = MemberStorage,
        DictionaryRepresentations = DictionaryRepresentations,
    });

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "schemas", name));

    public static JsonDocument ParseFixture(string name) => JsonDocument.Parse(ReadFixture(name));

    /// <summary>The four vendored service documents.</summary>
    public static readonly string[] VendoredDocuments = ["erp.json", "hr.json", "logistics.json", "vehicle.json"];
}
