using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Tests.Bind;

namespace OxQL.Tests.Execute;

/// <summary>The contact the resolve graph's invoices and shipments reference at another service.</summary>
public class CrmContact
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }

    [OxQLReference("crm.company")]
    public Guid? CompanyId { get; set; }

    public List<CrmPhone> Phones { get; set; } = [];
}

public class CrmPhone
{
    public string Number { get; set; } = "";
    public string? Label { get; set; }
}

public class CrmCompany
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Title { get; set; } = "";
}

/// <summary>The shipment of the transport service, whose billing lines the resolve graph's source reference names.</summary>
public class TrShipment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = "";
    public List<TrBillingLine> BillingLines { get; set; } = [];
}

public class TrBillingLine
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public decimal Weight { get; set; }
}

/// <summary>
/// The owners of the resolve graph's remote targets as real engines over their own models: an
/// origin's explain is answered by the owner's explain, as the internal explain route answers it, so
/// what an origin reads of an owner's answer (types, aliases, errors) is what an owner answers.
/// </summary>
internal static class OwnerFleet
{
    private static readonly Lazy<EntityModel> crm = new(() => ClrModelBuilder.Build(
    [
        new EntityDeclaration("crm.contact", "crm.contact", typeof(CrmContact), "contacts", null, false),
        new EntityDeclaration("crm.company", "crm.company", typeof(CrmCompany), "companies", null, false),
    ], retiredIds: null, new ReferenceDeclarations()));

    private static readonly Lazy<EntityModel> transport = new(() => ClrModelBuilder.Build(
    [
        new EntityDeclaration("transport.shipment", "transport.shipment", typeof(TrShipment), "shipments", null, false),
    ], retiredIds: null, new ReferenceDeclarations()));

    public static EntityModel Crm => crm.Value;

    public static EntityModel Transport => transport.Value;

    /// <summary>An engine of one owner, with a remote client of its own when it has owners itself.</summary>
    public static MongoQueryEngine Engine(EntityModel model, OxQLOptions? options = null, IRemoteQueryClient? client = null) =>
        new(new StaticEntityModelProvider(model), new FakeAggregateRunner(), BindHost.Cursors, options ?? BindHost.Options(), client);

    /// <summary>A remote client whose owners <c>crm</c> and <c>transport</c> answer explains with their own engines.</summary>
    public static FakeRemoteClient Client(OxQLOptions? options = null)
    {
        var engines = new Dictionary<string, MongoQueryEngine>(StringComparer.Ordinal)
        {
            ["crm"] = Engine(Crm, options),
            ["transport"] = Engine(Transport, options),
        };

        return new FakeRemoteClient { Explains = (service, request) => engines.TryGetValue(service, out var engine) ? Answer(engine, request, options) : null };
    }

    /// <summary>An owner's answer to an internal explain, in wire form.</summary>
    public static JsonObject? Answer(MongoQueryEngine engine, ExplainRequest request, OxQLOptions? options = null)
    {
        // The request travels as wire JSON, as over the internal route.
        var sent = JsonSerializer.Deserialize<ExplainRequest>(JsonSerializer.SerializeToUtf8Bytes(request, OxQLJson.Wire), OxQLJson.Wire)!;
        var outcome = engine.ExplainAsync(sent, BindHost.Context(options) with { Internal = true }).GetAwaiter().GetResult();

        return outcome is ExplainOutcome.Success success ? JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire)!.AsObject() : null;
    }
}
