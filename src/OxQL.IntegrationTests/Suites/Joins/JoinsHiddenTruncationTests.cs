using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// Strict refuses a truncation a later match hides (RE-3, DESIGN §3.4.3): a lookup cut at its limit,
/// or a flatten cut at its depth, whose truncated row a later match filters out, is not on the page,
/// yet the answer depends on what was cut. Outside strict a page is what it is; under strict the rows
/// up to the truncation are checked and the request is refused.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsHiddenTruncationTests
{
    private static string Id(Guid id) => id.ToString("D");

    private static JsonObject Request(string entity, string pipeline, bool strict)
    {
        var body = Json.Request(entity, JsonNode.Parse(pipeline)!);

        if (strict)
            body["strict"] = true;

        return body;
    }

    [Fact]
    public async Task H01_a_match_on_a_child_the_lookup_cut_filters_the_parent_out_and_strict_refuses_LOOKUP_TRUNCATED()
    {
        // The scenario shipment's attempts by date: Recipient absent, Gate closed, Delivered to gate 3.
        // A limit of two cuts the delivered one, which the match asks for.
        var client = await Lab.ClientAsync(LabService.Transport, Org.R);
        var pipeline = $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.ShipmentId)}}" } } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts", "limit": 2, "sort": [ { "dateTime": "asc" } ], "select": ["text"] } },
              { "match": { "attempts.text": { "eq": "Delivered to gate 3" } } },
              { "project": { "id": 1 } } ]
            """;

        var lenient = await client.QueryAsync(Request(ReportSeed.Shipment, pipeline, strict: false));

        lenient.ShouldBeOk().Items.Should().BeEmpty("the delivered attempt is past the limit, so the shipment does not match");

        var refused = await client.QueryAsync(Request(ReportSeed.Shipment, pipeline, strict: true));
        var error = refused.ShouldRefuse("LOOKUP_TRUNCATED", 422);

        error["stage"]!.GetValue<int>().Should().Be(1);
        error["params"]!["filtered"]!.GetValue<bool>().Should().BeTrue();
        error["params"]!["limit"]!.GetValue<int>().Should().Be(2);

        // A match the cut cannot hide passes strict.
        var kept = await client.QueryAsync(Request(ReportSeed.Shipment, pipeline.Replace("Delivered to gate 3", "Recipient absent").Replace("\"limit\": 2", "\"limit\": 3"), strict: true));

        kept.ShouldBeOk().Items.Should().ContainSingle();
    }

    [Fact]
    public async Task H02_a_match_on_an_item_below_the_flatten_depth_filters_every_row_out_and_strict_refuses_UNWIND_DEPTH_TRUNCATED()
    {
        var client = await Lab.ClientAsync(LabService.Ledger, Org.R);
        var pipeline = $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.DeepNestingTransactionId)}}" } } },
              { "unwind": { "path": "items", "flatten": "items", "as": "item" } },
              { "match": { "item.text": { "eq": "Billing line 59" } } },
              { "project": { "number": 1 } } ]
            """;

        (await client.QueryAsync(Request(ReportSeed.Transaction, pipeline, strict: false))).ShouldBeOk().Items.Should().BeEmpty("the line seven groups deep is below the depth of five");

        var refused = await client.QueryAsync(Request(ReportSeed.Transaction, pipeline, strict: true));

        refused.ShouldRefuse("UNWIND_DEPTH_TRUNCATED", 422)["params"]!["filtered"]!.GetValue<bool>().Should().BeTrue();
    }
}
