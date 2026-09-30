// The graph the resolve binding tests bind against (DESIGN §3.4.1): every reference form a
// resolve meets — simple local and remote, a collection, typed cases with item targets, a
// converted key, a variant condition — on entities with an organisation member, so a request
// scopes. None carries [OxQLType]; the entities are declared by hand. The slot variants' class
// maps are registered when the test assembly loads (ModelTestSetup), as a host registers its maps.

using MongoDB.Bson.Serialization;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;

namespace OxQL.Tests.Bind.Fixtures.Resolve;

/// <summary>The root: every reference form a resolve binds.</summary>
public class RcInvoice
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = "";

    [OxQLReference("rc.customer")]
    public Guid CustomerId { get; set; }

    /// <summary>A simple local reference onto a member that is not the key.</summary>
    [OxQLReference("rc.customer", "code")]
    public string? CustomerCode { get; set; }

    /// <summary>A plain remote reference.</summary>
    [OxQLReference("crm.contact", "id")]
    public Guid ContactId { get; set; }

    /// <summary>A collection of references: the member itself is the collection.</summary>
    [OxQLReference("rc.customer")]
    public List<Guid> CustomerIds { get; set; } = [];

    /// <summary>An item target: the element of the shipment's billing lines.</summary>
    [OxQLReference("rc.shipment", "id", Item = "billingLines")]
    public Guid BillingLineId { get; set; }

    /// <summary>A converted key onto a local guid key.</summary>
    [OxQLReference("rc.shipment", KeyAs = OxQLKeyAs.Guid)]
    public string? ShipmentKey { get; set; }

    public List<RcLine> Lines { get; set; } = [];
    public RcSource? Source { get; set; }
    public RcLocalSource? LocalSource { get; set; }
    public RcBilling? Billing { get; set; }
    public RcSlot? Slot { get; set; }
}

/// <summary>A line: a reference under one collection, and one under two.</summary>
public class RcLine
{
    public Guid Id { get; set; }

    [OxQLReference("rc.customer")]
    public Guid CustomerId { get; set; }

    public List<RcPart> Parts { get; set; } = [];
}

public class RcPart
{
    [OxQLReference("rc.customer")]
    public Guid CustomerId { get; set; }
}

/// <summary>ERP's source billing line reference: a local union of item targets and a remote item case.</summary>
public class RcSource
{
    public string Type { get; set; } = "";

    [OxQLReferenceWhen("type", "logistics", "rc.shipment#billingLines", "rc.tour#billingLines")]
    [OxQLReferenceWhen("type", "remote", "transport.shipment#billingLines", Field = "id")]
    public Guid Id { get; set; }
}

/// <summary>A local union: two item targets in one case, an entity target in another.</summary>
public class RcLocalSource
{
    public string Type { get; set; } = "";

    [OxQLReferenceWhen("type", "logistics", "rc.shipment#billingLines", "rc.tour#billingLines")]
    [OxQLReferenceWhen("type", "customer", "rc.customer")]
    public Guid Id { get; set; }
}

/// <summary>ERP's billing line reference: a string id converted to a guid, one local target per data type.</summary>
public class RcBilling
{
    public string DataType { get; set; } = "";

    [OxQLReferenceWhen("dataType", "shipment", "rc.shipment", KeyAs = OxQLKeyAs.Guid)]
    [OxQLReferenceWhen("dataType", "tour", "rc.tour", KeyAs = OxQLKeyAs.Guid)]
    public string? ReferenceId { get; set; }
}

/// <summary>A polymorphic holder whose reference applies to one variant only.</summary>
public class RcSlot
{
    public string? Name { get; set; }

    [OxQLReferenceWhen(OxQLReferenceWhenAttribute.Variant, "RcDriverSlot", "rc.customer")]
    public Guid HolderId { get; set; }
}

public class RcDriverSlot : RcSlot
{
    public string? Licence { get; set; }
}

public class RcVehicleSlot : RcSlot
{
    public string? Plate { get; set; }
}

public class RcCustomer
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
}

public class RcShipment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = "";
    public List<RcBillingLine> BillingLines { get; set; } = [];

    /// <summary>A driver of the shipment's own: a remote contact, where the tour's is a local customer.</summary>
    [OxQLReference("crm.contact", "id")]
    public Guid? DriverId { get; set; }
}

public class RcTour
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public List<RcBillingLine> BillingLines { get; set; } = [];

    /// <summary>The tour's driver: a local customer, where the shipment's is a remote contact.</summary>
    [OxQLReference("rc.customer")]
    public Guid? DriverId { get; set; }
}

public class RcBillingLine
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public decimal Amount { get; set; }
}

/// <summary>The declarations of the resolve graph, and its model, built once.</summary>
internal static class ResolveModel
{
    public const string Invoice = "rc.invoice";

    /// <summary>Registers the slot variants; <see cref="Model.ModelTestSetup"/> calls it when the test assembly loads.</summary>
    internal static void Register()
    {
        BsonClassMap.RegisterClassMap<RcDriverSlot>();
        BsonClassMap.RegisterClassMap<RcVehicleSlot>();
    }

    private static readonly Lazy<EntityModel> model = new(() => ClrModelBuilder.Build(
    new EntityDeclaration[]
    {
        new(Invoice, Invoice, typeof(RcInvoice), "invoices", null, false),
        new("rc.customer", "rc.customer", typeof(RcCustomer), "customers", null, false),
        new("rc.shipment", "rc.shipment", typeof(RcShipment), "shipments", null, false),
        new("rc.tour", "rc.tour", typeof(RcTour), "tours", null, false),
    }, retiredIds: null, new ReferenceDeclarations()));

    public static EntityModel Model => model.Value;
}
