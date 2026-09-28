using FluentAssertions;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Spike;

/// <summary>The lab model: synthetic names only, one namespace per service, the references as declared.</summary>
[Trait("Category", "Integration")]
public class FleetModelTests
{
    [Fact]
    public void Every_entity_uses_a_lab_namespace()
    {
        var entities = EntityScanner.Scan([typeof(LabService).Assembly], []);

        entities.Select(entity => entity.Id.Split('.')[0]).Distinct()
            .Should().BeSubsetOf(LabService.All.Select(service => service.Key));
        entities.Select(entity => entity.Id).Should().BeEquivalentTo(
            "staff.employee",
            "fleet.vehicle", "fleet.equipment", "fleet.department", "fleet.status",
            "transport.shipment", "transport.shipment_template", "transport.tour", "transport.delivery_attempt", "transport.resource",
            "ledger.transaction", "ledger.billing_line",
            "conformance.entity", "conformance.ref", "conformance.child",
            "directory.contact");
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("fleet")]
    [InlineData("transport")]
    [InlineData("ledger")]
    public void A_business_service_builds_without_findings(string key) =>
        LabService.Of(key).Model.Findings.Should().BeEmpty();

    [Fact]
    public void The_conformance_model_reports_only_the_fieldless_remote_reference() =>
        LabService.Conformance.Model.Findings.Should().ContainSingle()
            .Which.Code.Should().Be(BuildCodes.ReferenceTargetFieldUnknown);

    [Fact]
    public void The_remote_references_leave_the_service_they_are_declared_in()
    {
        // RemoteReferences.Of is what the startup check and health read; it includes the nested
        // department reference of the shipment (it once listed root members only). The report
        // fleet adds the ledger's contact, clerk and billing-line references and the resource id
        // every variant case names (host-side, on the pooled resource type wherever it is held).
        string[] resource = ["staff.employee", "fleet.vehicle"];
        string[] logistics = ["transport.shipment", "transport.tour"];
        string[] contacts = ["financialPartner", "deliveryAddress", "invoiceRecipient", "payer", "creator", "responsible", "representative"];

        string[] expected =
            [
                "transport.shipment#department.id -> fleet.department",
                "transport.shipment_template#createUserId -> fleet.vehicle",
                "conformance.entity#employeeId -> staff.employee",
                "conformance.entity#widgetCodeExplicit -> owner.widget",
                .. resource.Select(target => $"transport.shipment#tours.resource.id -> {target}"),
                .. new[] { "resource.id", "actions.resource.id", "attachedResources.resource.id", "attachedResources.attachAction.resource.id", "attachedResources.detachAction.resource.id" }
                    .SelectMany(path => resource.Select(target => $"transport.tour#{path} -> {target}")),
                .. resource.Select(target => $"transport.resource#id -> {target}"),
                "ledger.transaction#createUserId -> staff.employee",
                .. contacts.Select(contact => $"ledger.transaction#{contact}.address.id -> directory.contact"),
                .. logistics.Select(target => $"ledger.transaction#items.references.referenceId -> {target}"),
                "ledger.billing_line#financialPartner.address.id -> directory.contact",
                .. logistics.Select(target => $"ledger.billing_line#references.referenceId -> {target}"),
                .. logistics.Select(target => $"ledger.billing_line#sourceBillingLineReference.id -> {target}"),
            ];

        LabService.All
            .SelectMany(service => RemoteReferences.Of(service.Model)
                .Select(reference => $"{reference.Entity}#{reference.Path} -> {reference.TargetEntity}"))
            .Should().BeEquivalentTo(expected);
    }
}
