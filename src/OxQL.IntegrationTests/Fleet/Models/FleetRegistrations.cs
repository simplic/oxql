using MongoDB.Bson.Serialization;
using OxQL.Model.Build;
using Ledger = OxQL.IntegrationTests.Fleet.Models.Ledger;
using Transport = OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Fleet.Models;

/// <summary>
/// The class maps of the fleet's polymorphic types, registered once per process when the assembly
/// loads, as a real service registers them during its startup (the ERP and logistics
/// <c>BsonClassMapRegister</c>): before any model build or serialization looks one up, so every
/// variant is known to the build and stored with its class name as <c>_t</c>. Registering a map
/// looks serializers up, so <see cref="StorageConventions"/> calls this right after it registers
/// the Guid convention, never before.
/// </summary>
internal static class FleetClassMaps
{
    internal static void Register()
    {
        // ledger: the transaction items (ERP Simplic.OxS.ERP.Data.DB/BsonClassMapRegister.cs).
        BsonClassMap.RegisterClassMap<Ledger.TransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.QuantityTransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.PriceTransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.OperationTransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.GeneralLedgerAccountTransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.ArticleTransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.BasicDiscountSurchargeOperationItem>();
        BsonClassMap.RegisterClassMap<Ledger.GroupTransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.CashDiscountOperationItem>();
        BsonClassMap.RegisterClassMap<Ledger.BillingLineTransactionItem>();
        BsonClassMap.RegisterClassMap<Ledger.TextTransactionItem>();

        // transport: the resources and the tour actions (logistics Repository/BsonClassMapRegister.cs).
        BsonClassMap.RegisterClassMap<Transport.DriverResource>();
        BsonClassMap.RegisterClassMap<Transport.TrailerResource>();
        BsonClassMap.RegisterClassMap<Transport.TractorUnitResource>();
        BsonClassMap.RegisterClassMap<Transport.ContainerResource>();
        BsonClassMap.RegisterClassMap<Transport.CarrierResource>();
        BsonClassMap.RegisterClassMap<Transport.EquipmentResource>();
        BsonClassMap.RegisterClassMap<Transport.CarResource>();

        BsonClassMap.RegisterClassMap<Transport.AttachShipmentAction>();
        BsonClassMap.RegisterClassMap<Transport.DetachShipmentAction>();
        BsonClassMap.RegisterClassMap<Transport.AttachResourceAction>();
        BsonClassMap.RegisterClassMap<Transport.DetachResourceAction>();
        BsonClassMap.RegisterClassMap<Transport.CheckVehicleAction>();
        BsonClassMap.RegisterClassMap<Transport.CleaningAction>();
        BsonClassMap.RegisterClassMap<Transport.TaskAction>();
    }
}

/// <summary>
/// The host-side reference declarations of each lab service: references on members its classes
/// cannot annotate. <c>transport.resource</c>'s <c>id</c> is inherited from the organisation
/// document; which service it names depends on the stored variant (DESIGN §3.3.2, Appendix A).
/// </summary>
public static class FleetReferences
{
    /// <summary>The resource variants whose id is a vehicle of the fleet service.</summary>
    public static readonly IReadOnlyList<string> VehicleVariants =
        ["TractorUnitResource", "TrailerResource", "CarResource", "ContainerResource", "EquipmentResource"];

    /// <summary>The declarations of the service <paramref name="serviceKey"/>; null when it has none.</summary>
    public static ReferenceDeclarations? For(string serviceKey)
    {
        if (serviceKey != "transport")
            return null;

        var declarations = new ReferenceDeclarations();
        var id = declarations.For<Transport.Resource>("id")
            .When(ReferenceDeclarations.Variant, "DriverResource", "staff.employee", field: "id");

        foreach (var variant in VehicleVariants)
            id.When(ReferenceDeclarations.Variant, variant, "fleet.vehicle", field: "id");

        return declarations;
    }
}
