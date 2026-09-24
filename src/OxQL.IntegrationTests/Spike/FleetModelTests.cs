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
            "transport.shipment", "transport.shipment_template",
            "ledger.transaction",
            "conformance.entity", "conformance.ref", "conformance.child");
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
        // department reference of the shipment (it once listed root members only).
        LabService.All
            .SelectMany(service => RemoteReferences.Of(service.Model)
                .Select(reference => $"{reference.Entity}#{reference.Path} -> {reference.TargetEntity}"))
            .Should().BeEquivalentTo(
                "transport.shipment#department.id -> fleet.department",
                "transport.shipment_template#createUserId -> fleet.vehicle",
                "conformance.entity#employeeId -> staff.employee",
                "conformance.entity#widgetCodeExplicit -> owner.widget");
    }
}
