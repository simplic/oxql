using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Seed;

/// <summary>
/// The report fleet is in place: the models mirror the real services' shapes (the variants of
/// the ERP items, the logistics resources and tour actions; the reference cases the scenarios
/// follow; the directory service), and the report rows of organisation R are in the shared fleet,
/// readable through the engine, stored in the driver's polymorphic forms, and invisible to
/// organisation A. The scenarios themselves (DESIGN §2) are the report suites' cases.
/// </summary>
[Trait("Category", "Integration")]
public class ReportSeedTests
{
    private static EntityDef Entity(string entityId) => LabService.Of(entityId.Split('.')[0]).Model.Entities[entityId];

    private static IEnumerable<string> VariantNames(string entityId, string path)
    {
        var type = path.Length == 0 ? Entity(entityId).Root : Entity(entityId).Path(path)!.Shape switch
        {
            { Kind: Kind.Array } array => array.Of!.Type!,
            var shape => shape.Type!,
        };

        return type.Variants.Select(variant => variant.Name);
    }

    private static IReadOnlyList<string> Cases(string entityId, string path) =>
        Entity(entityId).Path(path)!.References.Select(reference => reference.ToString()).ToList();

    [Fact]
    public void R01_the_directory_service_serves_the_contact_and_reports_only_the_opaque_geojson_location()
    {
        var model = LabService.Directory.Model;

        model.Entities.Keys.Should().Equal("directory.contact");
        model.Findings.Should().ContainSingle().Which.Code.Should().Be(BuildCodes.MemberSerializerOpaque);
        Entity(ReportSeed.Contact).Path("address.location")!.Kind.Should().Be(Kind.Unknown);

        foreach (var path in new[] { "primaryEmailAddress.email", "primaryPhoneNumber.number", "address.companyName" })
            Entity(ReportSeed.Contact).Path(path).Should().NotBeNull(path);
    }

    [Fact]
    public void R02_the_polymorphic_members_carry_the_real_services_variants()
    {
        VariantNames(ReportSeed.Transaction, "items").Should().BeEquivalentTo(
            "ArticleTransactionItem", "BasicDiscountSurchargeOperationItem", "BillingLineTransactionItem", "CashDiscountOperationItem",
            "GeneralLedgerAccountTransactionItem", "GroupTransactionItem", "TextTransactionItem");
        VariantNames(ReportSeed.Resource, "").Should().BeEquivalentTo(
            "CarResource", "CarrierResource", "ContainerResource", "DriverResource", "EquipmentResource", "TrailerResource", "TractorUnitResource");
        VariantNames(ReportSeed.Tour, "actions").Should().BeEquivalentTo(
            "AttachResourceAction", "AttachShipmentAction", "CheckVehicleAction", "CleaningAction", "DetachResourceAction", "DetachShipmentAction", "TaskAction");
        VariantNames(ReportSeed.Tour, "resource").Should().HaveCount(7);
        VariantNames(ReportSeed.Shipment, "tours.resource").Should().HaveCount(7);

        foreach (var path in new[] { "items.billingLineId", "items.references.referenceId", "items.items", "items.quantity.value", "items.totalPriceNet", "termsOfPayment.formattedText", "taxKeyTotalPrices.totalPrice" })
            Entity(ReportSeed.Transaction).Path(path).Should().NotBeNull(path);

        Entity(ReportSeed.Transaction).Path("items.billingLineId")!.Member.OnlyFor.Should().Equal("BillingLineTransactionItem");
    }

    [Fact]
    public void R03_the_references_are_declared_where_the_scenarios_follow_them()
    {
        Cases(ReportSeed.Transaction, "items.billingLineId").Should().Equal("-> ledger.billing_line.id");
        Cases(ReportSeed.Transaction, "items.references.referenceId").Should().Equal(
            "when dataType=shipment keyAs guid -> transport.shipment.id (remote)", "when dataType=tour keyAs guid -> transport.tour.id (remote)");
        Cases(ReportSeed.Transaction, "invoiceRecipient.address.id").Should().Equal("-> directory.contact.id (remote)");
        Cases(ReportSeed.Transaction, "createUserId").Should().Equal("-> staff.employee.userId (remote)");
        Cases(ReportSeed.BillingLine, "sourceBillingLineReference.id").Should().Equal(
            "when type=logistics -> transport.shipment#billingLines.id (remote), transport.tour#billingLines.id (remote)");
        Cases(ReportSeed.Shipment, "tours.tourId").Should().Equal("-> transport.tour.id");
        Cases(ReportSeed.DeliveryAttempt, "shipmentId").Should().Equal("-> transport.shipment.id");

        foreach (var path in new[] { "resource.id", "attachedResources.resource.id" })
            Cases(ReportSeed.Tour, path).Should().Equal(
            [
                "when $variant=DriverResource -> staff.employee.id (remote)",
                .. FleetReferences.VehicleVariants.Select(variant => $"when $variant={variant} -> fleet.vehicle.id (remote)"),
            ], path);
    }

    [Fact]
    public async Task R04_every_report_entity_answers_exactly_its_rows_in_organisation_R_and_none_in_A()
    {
        const string Pipeline = """[{ "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 500, "includeTotalCount": true } }]""";

        foreach (var (entityId, service, rows) in ReportSeed.Entities)
        {
            var answer = await (await Lab.ClientAsync(service, Org.R)).SendAsync(entityId, Pipeline);

            answer.ShouldHaveTotal(rows.Count);
            answer.Strings("id").Should().BeEquivalentTo(rows.Select(row => row.WireId), entityId);

            var inA = await (await Lab.ClientAsync(service)).SendAsync(entityId, Pipeline);

            inA.Strings("id").Should().NotIntersectWith(rows.Select(row => row.WireId), entityId);
        }
    }

    [Fact]
    public void R05_the_scenario_rows_are_stored_in_the_driver_polymorphic_forms_and_carry_the_named_hazards()
    {
        var invoice = ReportSeed.Row(ReportSeed.Transaction, "mixed-invoice").Stored;
        var items = invoice["Items"].AsBsonArray;

        items.Select(item => item["_t"].AsString).Should().Equal(
            "TextTransactionItem", "BillingLineTransactionItem", "GroupTransactionItem", "BillingLineTransactionItem",
            "ArticleTransactionItem", "GeneralLedgerAccountTransactionItem", "BasicDiscountSurchargeOperationItem", "CashDiscountOperationItem");
        items[2]["Items"][1]["Items"][0]["BillingLineId"].Should().Be(new BsonBinaryData(ReportSeed.ErpLineIds[1], GuidRepresentation.Standard), "a billing line two groups deep");
        items[1]["References"][0]["ReferenceId"].Should().Be(new BsonString(ReportSeed.ShipmentId.ToString("D")), "the self-reference holds the id as a string");

        var deep = ReportSeed.Row(ReportSeed.Transaction, "deep-nesting").Stored["Items"][1];
        var depth = 1;

        for (; deep["_t"] == "GroupTransactionItem"; depth++)
            deep = deep["Items"][0];

        depth.Should().Be(8, "seven groups above the billing line item");

        var tour = ReportSeed.Row(ReportSeed.Tour, "tour-tractor").Stored;

        tour["Actions"].AsBsonArray.Select(action => action["_t"].AsString).Should().HaveCount(7).And.OnlyHaveUniqueItems();
        tour["Resource"]["_t"].AsString.Should().Be("TractorUnitResource");

        ReportSeed.Row(ReportSeed.Tour, "tour-carrier").Stored["BillingLines"][0]["_id"]
            .Should().Be(ReportSeed.Row(ReportSeed.Shipment, "shipment-dup-line").Stored["BillingLines"][0]["_id"], "one billing line id in two parents");
        ReportSeed.Rows(ReportSeed.Employee).Count(row => row.Stored.Contains("UserId") && row.Stored["UserId"] == new BsonBinaryData(ReportSeed.DuplicateUserId, GuidRepresentation.Standard))
            .Should().Be(2, "two employees share one userId");
        ReportSeed.Row(ReportSeed.Resource, "carrier").Stored["_t"].AsString.Should().Be("CarrierResource");
        ReportSeed.Rows(ReportSeed.BillingLine).Select(row => row.Stored["SourceBillingLineReference"]["_id"].AsGuid)
            .Should().Contain(ReportSeed.DeletedSourceLineId, "a deleted source line");
    }
}
