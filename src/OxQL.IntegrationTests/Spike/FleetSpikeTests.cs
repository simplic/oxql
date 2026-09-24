using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fleet;
using Xunit;

namespace OxQL.IntegrationTests.Spike;

/// <summary>
/// The design proven end to end on a real server: a page through one host, a resolve into
/// another host, an addon filter, and the organisation scope.
/// </summary>
[Trait("Category", "Integration")]
public class FleetSpikeTests(SpikeFleetFixture fixture) : IClassFixture<SpikeFleetFixture>
{
    [Fact]
    public async Task A_filtered_sorted_page_answers_the_matching_rows_in_order()
    {
        var staff = await fixture.Fleet.HostAsync(LabService.Staff);

        var response = await staff.QueryAsync("""
            { "entityType": "staff.employee", "pipeline": [
                { "match": { "address.city": { "eq": "Berlin" }, "isDeleted": { "eq": false } } },
                { "sort": [ { "matchCode": "desc" } ] },
                { "page": { "limit": 2 } } ] }
            """);

        response.Status.Should().Be(HttpStatusCode.OK, response.Text);
        response.Items.Select(item => item!["matchCode"]!.GetValue<string>()).Should().Equal("DELTA", "BRAVO");
        response.Body!["pageInfo"]!["hasNextPage"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_remote_resolve_reads_the_department_from_the_owning_host()
    {
        var transport = await fixture.Fleet.HostAsync(LabService.Transport);

        var response = await transport.QueryAsync("""
            { "entityType": "transport.shipment", "pipeline": [
                { "sort": [ { "shipmentNumber": "asc" } ] },
                { "resolve": { "path": "department.id", "as": "owner", "select": ["id", "name"] } },
                { "page": { "limit": 10 } } ] }
            """);

        response.Status.Should().Be(HttpStatusCode.OK, response.Text);
        response.Items.Should().HaveCount(2);
        response.Items[0]!["owner"]!["name"]!.GetValue<string>().Should().Be("Fleet");
        response.Items[0]!["owner"]!["id"]!.GetValue<string>().Should().Be(SpikeFleetFixture.DepartmentA.ToString());
        response.Items[1]!["owner"].Should().BeNull("the second shipment's department names nothing");
    }

    [Fact]
    public async Task An_addon_filter_matches_the_defined_key()
    {
        var staff = await fixture.Fleet.HostAsync(LabService.Staff);

        var response = await staff.QueryAsync("""
            { "entityType": "staff.employee", "pipeline": [
                { "match": { "addon.shiftModel": { "eq": "night" } } },
                { "sort": [ { "matchCode": "asc" } ] } ] }
            """);

        response.Status.Should().Be(HttpStatusCode.OK, response.Text);
        response.Items.Select(item => item!["matchCode"]!.GetValue<string>()).Should().Equal("BRAVO", "CHARLIE");
    }

    [Fact]
    public async Task A_row_of_another_organisation_is_invisible()
    {
        var fleet = await fixture.Fleet.HostAsync(LabService.Fleet);
        const string Departments = """{ "entityType": "fleet.department", "pipeline": [ { "sort": [ { "name": "asc" } ] } ] }""";

        var asA = await fleet.QueryAsync(Departments, LabIdentity.OrganisationA);
        var asB = await fleet.QueryAsync(Departments, LabIdentity.OrganisationB);

        asA.Status.Should().Be(HttpStatusCode.OK, asA.Text);
        asA.Items.Select(item => item!["name"]!.GetValue<string>()).Should().Equal("Fleet");
        asB.Items.Select(item => item!["name"]!.GetValue<string>()).Should().Equal("Fleet B");
    }

    [Fact]
    public async Task A_variant_runs_under_its_own_limits_and_is_created_once()
    {
        var limits = new Dictionary<string, string?> { ["OxQL:Limits:MaxPageSize"] = "7" };

        var variant = await fixture.Fleet.VariantAsync(LabService.Staff, "page-7", limits);
        var again = await fixture.Fleet.VariantAsync(LabService.Staff, "page-7", limits);
        var standard = await fixture.Fleet.HostAsync(LabService.Staff);

        again.Should().BeSameAs(variant);
        (await MaxPageSize(variant)).Should().Be(7);
        (await MaxPageSize(standard)).Should().NotBe(7);

        static async Task<int> MaxPageSize(FleetHost host)
        {
            using var client = host.Client();
            var health = JsonNode.Parse(await client.GetStringAsync("OxQL/health?shallow=true"));

            return health!["limits"]!["maxPageSize"]!.GetValue<int>();
        }
    }

    [Fact]
    public async Task A_remote_resolve_is_scoped_to_the_caller_at_the_owner()
    {
        var transport = await fixture.Fleet.HostAsync(LabService.Transport);

        // Organisation B's shipment names B's department; the owner answers it only because the
        // organisation travelled with the batch.
        var response = await transport.QueryAsync("""
            { "entityType": "transport.shipment", "pipeline": [
                { "resolve": { "path": "department.id", "as": "owner", "select": ["name"] } } ] }
            """, LabIdentity.OrganisationB);

        response.Status.Should().Be(HttpStatusCode.OK, response.Text);
        response.Items.Should().ContainSingle().Which!["owner"]!["name"]!.GetValue<string>().Should().Be("Fleet B");
    }
}
