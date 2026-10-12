using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// The result columns of a projected member below an object, on the fleet: a grid that shows
/// <c>customer.address.companyName</c> reads what that member can off its own column, and what the
/// column says holds on a run: the values stage it allows answers, the sort it allows orders.
/// </summary>
[Trait("Category", "Integration")]
public class ExplainNestedColumnsTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static JsonObject Query(string pipeline) =>
        JsonNode.Parse($$"""{ "entityType": "{{Corpus.Shipment}}", "pipeline": {{pipeline}} }""")!.AsObject();

    [Fact]
    public async Task A_projected_member_of_the_customers_address_is_a_column_and_a_run_does_what_it_says()
    {
        var client = await Transport();
        var answer = await client.ExplainHereAsync(Query("""[{ "project": { "shipmentNumber": 1, "customer.address.companyName": 1, "customer.address.city": 1 } }, { "page": { "limit": 5 } }]"""));
        var body = answer.Body!;

        body["valid"]!.GetValue<bool>().Should().BeTrue(answer.Text);

        var columns = body["result"]!["columns"]!.AsArray().Select(column => column!.AsObject()).ToList();

        columns.Select(column => column["path"]!.GetValue<string>()).Should().BeEquivalentTo(["id", "shipmentNumber", "customer.address.companyName", "customer.address.city"]);

        var company = columns.Single(column => column["path"]!.GetValue<string>() == "customer.address.companyName");

        company["kind"]!.GetValue<string>().Should().Be("string");
        company["root"]!.GetValue<string>().Should().Be("customer");

        var can = body["result"]!["capabilities"]![company["can"]!.GetValue<string>()]!.AsObject();

        can["can"]!["values"]!.GetValue<bool>().Should().BeTrue(can.ToJsonString());
        can["can"]!["sort"]!.GetValue<bool>().Should().BeTrue();
        can.ContainsKey("why").Should().BeFalse();

        // The values stage the column allows answers, and the sort it allows orders the rows.
        (await client.QueryAsync(Query("""[{ "values": { "path": "customer.address.companyName", "limit": 5 } }]"""))).ShouldBeOk();
        (await client.QueryAsync(Query("""[{ "project": { "customer.address.companyName": 1 } }, { "sort": [{ "customer.address.companyName": "asc" }] }, { "page": { "limit": 5 } }]"""))).ShouldBeOk();
    }

    [Fact]
    public async Task Without_a_projection_the_address_is_one_column_that_has_no_values()
    {
        var answer = await (await Transport()).ExplainHereAsync(Query("""[{ "page": { "limit": 5 } }]"""));
        var body = answer.Body!;
        var address = body["result"]!["columns"]!.AsArray().Select(column => column!.AsObject()).Single(column => column["path"]!.GetValue<string>() == "customer.address");

        address["kind"]!.GetValue<string>().Should().Be("object");
        body["result"]!["capabilities"]![address["can"]!.GetValue<string>()]!["why"]!["values"]!.GetValue<string>().Should().Be("notScalar");
    }
}
