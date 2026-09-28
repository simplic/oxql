// The graphs the reference-case tests build from (DESIGN §3.3). None carries [OxQLType]: the probe
// build scans this whole assembly, so the entities are declared by hand. The resource variants'
// class maps are registered once, before any build looks them up, as a host registers its maps.

using MongoDB.Bson.Serialization;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;

namespace OxQL.Tests.Model.Fixtures.References;

/// <summary>The source entity: every attribute form on its lines, typed references on two embedded objects.</summary>
public class RefInvoice
{
    public Guid Id { get; set; }
    public List<RefInvoiceLine> Lines { get; set; } = [];
    public RefSource? Source { get; set; }
    public RefBilling? Billing { get; set; }
}

/// <summary>Every <c>[OxQLReference]</c> form, valid and invalid.</summary>
public class RefInvoiceLine
{
    public Guid Id { get; set; }

    [OxQLReference("refs.shipment")]
    public Guid ShipmentId { get; set; }

    [OxQLReference("refs.shipment", "id", Item = "billingLines")]
    public Guid ShipmentBillingLineId { get; set; }

    [OxQLReference("refs.shipment", Item = "billingLines")]
    public Guid DefaultedItemId { get; set; }

    [OxQLReference("refs.shipment", "code", Item = "billingLines")]
    public string? ItemCode { get; set; }

    [OxQLReference("transport.shipment", "id", KeyAs = OxQLKeyAs.Guid)]
    public string? RemoteShipmentId { get; set; }

    [OxQLReference("refs.shipment", KeyAs = OxQLKeyAs.Guid)]
    public string? LocalShipmentKey { get; set; }

    [OxQLReference("refs.shipment")]
    public string? UnconvertedShipmentId { get; set; }

    [OxQLReference("refs.shipment", "number", KeyAs = OxQLKeyAs.Guid)]
    public string? ConvertedToText { get; set; }

    [OxQLReference("staff.employee", "id", KeyAs = OxQLKeyAs.Guid)]
    public Guid ConvertedGuid { get; set; }

    [OxQLReference("refs.shipment", Item = "notes")]
    public Guid NoteId { get; set; }

    [OxQLReference("refs.shipment", Item = "missing")]
    public Guid MissingItemId { get; set; }

    [OxQLReference("refs.shipment", "nope", Item = "billingLines")]
    public Guid MissingItemFieldId { get; set; }
}

/// <summary>ERP's <c>SourceBillingLineReference</c>: a local union of item targets and a remote item case.</summary>
public class RefSource
{
    public string Type { get; set; } = "";

    [OxQLReferenceWhen("type", "logistics", "refs.shipment#billingLines", "refs.tour#billingLines")]
    [OxQLReferenceWhen("type", "remote", "transport.shipment#billingLines", Field = "id")]
    public Guid Id { get; set; }
}

/// <summary>ERP's <c>BillingLineReference</c>: a string id converted to a guid, one remote target per data type.</summary>
public class RefBilling
{
    public string DataType { get; set; } = "";

    [OxQLReferenceWhen("dataType", "shipment", "transport.shipment", Field = "id", KeyAs = OxQLKeyAs.Guid)]
    [OxQLReferenceWhen("dataType", "tour", "transport.tour", Field = "id", KeyAs = OxQLKeyAs.Guid)]
    public string? ReferenceId { get; set; }
}

/// <summary>Declarations a build refuses, one member each.</summary>
public class RefBroken
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string Other { get; set; } = "";
    public int Count { get; set; }

    [OxQLReference("refs.shipment")]
    [OxQLReferenceWhen("kind", "a", "refs.tour")]
    public Guid Both { get; set; }

    [OxQLReferenceWhen("kind", "a", "refs.tour")]
    [OxQLReferenceWhen("other", "b", "refs.tour")]
    public Guid DifferentPaths { get; set; }

    [OxQLReferenceWhen("kind", "a", "refs.tour")]
    [OxQLReferenceWhen("kind", "a", "refs.shipment")]
    public Guid SameValueTwice { get; set; }

    [OxQLReferenceWhen("count", "1", "refs.tour")]
    public Guid NumberSibling { get; set; }

    [OxQLReferenceWhen("missing", "1", "refs.tour")]
    public Guid MissingSibling { get; set; }

    [OxQLReferenceWhen("kind", "a", "refs.nothing")]
    [OxQLReferenceWhen("kind", "b", "refs.tour")]
    public Guid UnknownCaseTarget { get; set; }

    [OxQLReferenceWhen("kind", "a", "transport.shipment")]
    public Guid RemoteWithoutField { get; set; }
}

/// <summary>The local item target: keyed billing lines, unkeyed notes.</summary>
public class RefShipment
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public List<RefShipmentBillingLine> BillingLines { get; set; } = [];
    public List<RefNote> Notes { get; set; } = [];
}

public class RefShipmentBillingLine
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
}

public class RefNote
{
    public string Text { get; set; } = "";
}

public class RefTour
{
    public Guid Id { get; set; }
    public List<RefShipmentBillingLine> BillingLines { get; set; } = [];
}

public class RefVehicle
{
    public Guid Id { get; set; }
}

/// <summary>
/// The host-declaration graph: <see cref="RefResource"/> and its two registered variants inherit
/// <c>Id</c> from <see cref="RefBase"/>, which <see cref="RefDepot"/> inherits too without being one
/// of them.
/// </summary>
public class RefPlan
{
    public Guid Id { get; set; }
    public RefResource? Resource { get; set; }
    public List<RefResource> Pool { get; set; } = [];
    public RefDepot? Depot { get; set; }
    public Guid DepotVehicleId { get; set; }

    [OxQLReference("refs.vehicle")]
    public Guid Attributed { get; set; }
}

public class RefBase
{
    public Guid Id { get; set; }
}

public class RefResource : RefBase
{
    public string? Name { get; set; }

    [OxQLReferenceWhen(OxQLReferenceWhenAttribute.Variant, "RefDriverResource", "staff.employee", Field = "id")]
    public Guid OwnerId { get; set; }
}

public class RefDriverResource : RefResource
{
    public Guid EmployeeId { get; set; }
}

public class RefVehicleResource : RefResource
{
    public string Plate { get; set; } = "";
}

public class RefDepot : RefBase
{
    public string? City { get; set; }
}

/// <summary>A type no entity reaches.</summary>
public class RefUnused
{
    public Guid Id { get; set; }
}

/// <summary>The hand-made declarations of the fixture entities and the host's reference declarations.</summary>
internal static class ReferenceModel
{
    public const string Invoice = "refs.invoice";
    public const string Broken = "refs.broken";
    public const string Plan = "refs.plan";

    /// <summary>
    /// Registers the resource variants; <see cref="ModelTestSetup"/> calls it when the test
    /// assembly loads, before any test runs.
    /// </summary>
    internal static void Register()
    {
        BsonClassMap.RegisterClassMap<RefDriverResource>();
        BsonClassMap.RegisterClassMap<RefVehicleResource>();
    }

    public static IReadOnlyList<EntityDeclaration> Entities =>
    [
        new(Invoice, Invoice, typeof(RefInvoice), "invoices", null, false),
        new(Broken, Broken, typeof(RefBroken), "broken", null, false),
        new(Plan, Plan, typeof(RefPlan), "plans", null, false),
        new("refs.shipment", "refs.shipment", typeof(RefShipment), "shipments", null, false),
        new("refs.tour", "refs.tour", typeof(RefTour), "tours", null, false),
        new("refs.vehicle", "refs.vehicle", typeof(RefVehicle), "vehicles", null, false),
    ];

    /// <summary>The resource declarations of DESIGN §3.3.2, one unconditional declaration, and three the build cannot apply.</summary>
    public static ReferenceDeclarations Declarations()
    {
        var declarations = new ReferenceDeclarations();

        declarations.For<RefResource>("id")
            .When(ReferenceDeclarations.Variant, "RefDriverResource", "staff.employee", field: "id")
            .When(ReferenceDeclarations.Variant, "RefVehicleResource", "refs.vehicle");
        declarations.For<RefPlan>("depotVehicleId").To("refs.vehicle");
        declarations.For<RefPlan>("attributed").To("refs.vehicle");
        declarations.For<RefDepot>("missing").To("refs.vehicle");
        declarations.For<RefUnused>("id").To("refs.vehicle");

        return declarations;
    }

    public static EntityModel Build(ReferenceDeclarations? declarations = null)
    {
        return ClrModelBuilder.Build(Entities, retiredIds: null, declarations ?? Declarations());
    }
}
