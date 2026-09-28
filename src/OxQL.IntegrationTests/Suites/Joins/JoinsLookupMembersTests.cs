using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// The OxQL 2.1 lookup members on a real server (DESIGN §3.4.2, scenario A2): "the latest
/// delivery attempt" as <c>first</c> with a <c>sort</c>, the children in the caller's order, a lookup
/// cut at its limit reported as <c>LOOKUP_TRUNCATED</c>, a lookup <c>on</c> a resolved alias, and the
/// refusals of an <c>on</c> that names no local entity row. The rows are the report seeds of
/// organisation R on the transport service: the scenario shipment has three attempts (failed on
/// 7 April, failed and then delivered on 8 April, the delivered one stored second), the third
/// shipment one, the second none.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsLookupMembersTests
{
    private static Task<LabClient> TransportClient() => Lab.ClientAsync(LabService.Transport, Org.R);

    private static string Id(Guid id) => id.ToString("D");

    private static IReadOnlyList<string?> TextsIn(JsonNode? array) =>
        (array as JsonArray ?? throw new InvalidOperationException($"not an array: {array?.ToJsonString()}")).Select(item => item?["text"]?.GetValue<string>()).ToList();

    [Fact]
    public async Task A2a_first_with_a_descending_sort_is_the_latest_attempt_and_null_where_there_is_none()
    {
        var answer = await (await TransportClient()).SendAsync(ReportSeed.Shipment, $$"""
            [ { "match": { "id": { "in": ["{{Id(ReportSeed.ShipmentId)}}", "{{Id(ReportSeed.ShipmentWithoutAttemptsId)}}"] } } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "lastAttempt",
                            "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["id", "dateTime", "status", "text"] } },
              { "project": { "id": 1, "lastAttempt": 1 } },
              { "sort": [ { "id": "asc" } ] } ]
            """);

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();

        var rows = answer.Items.ToDictionary(item => Guid.Parse(item!["id"]!.GetValue<string>()), item => item!["lastAttempt"]);

        rows.Should().HaveCount(2);
        rows[ReportSeed.ShipmentId]!["text"]!.GetValue<string>().Should().Be("Delivered to gate 3");
        rows[ReportSeed.ShipmentId]!["status"]!["displayName"]!.GetValue<string>().Should().Be("delivered");
        rows[ReportSeed.ShipmentWithoutAttemptsId].Should().BeNull("a first lookup without children is null, not an empty array");
    }

    [Fact]
    public async Task The_children_come_in_the_callers_order_and_a_match_reads_the_first_one()
    {
        var client = await TransportClient();
        var sorted = await client.SendAsync(ReportSeed.Shipment, $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.ShipmentId)}}" } } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts", "sort": [ { "dateTime": "desc" } ], "select": ["text"] } } ]
            """);

        TextsIn(sorted.ShouldBeOk().Items[0]!["attempts"]).Should().Equal("Delivered to gate 3", "Gate closed", "Recipient absent");

        var delivered = await client.SendAsync(ReportSeed.Shipment, """
            [ { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "lastAttempt", "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["status"] } },
              { "match": { "lastAttempt.status.displayName": { "eq": "delivered" } } },
              { "project": { "id": 1 } } ]
            """);

        delivered.ShouldHaveIdsInAnyOrder([ReportSeed.ShipmentId, ReportSeed.ShipmentWithDuplicateLineId], "both shipments whose latest attempt was delivered, and not the one without attempts");
    }

    [Fact]
    public async Task A_lookup_over_its_limit_is_cut_to_the_limit_and_reported_as_LOOKUP_TRUNCATED()
    {
        var client = await TransportClient();
        var pipeline = $$"""
            [ { "match": { "id": { "in": ["{{Id(ReportSeed.ShipmentId)}}", "{{Id(ReportSeed.ShipmentWithDuplicateLineId)}}"] } } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts", "limit": LIMIT, "sort": [ { "dateTime": "desc" } ], "select": ["text"] } },
              { "project": { "id": 1, "attempts": 1 } },
              { "sort": [ { "id": "asc" } ] } ]
            """;

        var cut = await client.SendAsync(ReportSeed.Shipment, pipeline.Replace("LIMIT", "2"));
        var rows = cut.ShouldBeOk().Items.ToDictionary(item => Guid.Parse(item!["id"]!.GetValue<string>()), item => item!["attempts"]);

        TextsIn(rows[ReportSeed.ShipmentId]).Should().Equal("Delivered to gate 3", "Gate closed");
        TextsIn(rows[ReportSeed.ShipmentWithDuplicateLineId]).Should().Equal("Delivered");

        var truncated = cut.ShouldHaveDiagnostic("LOOKUP_TRUNCATED");

        truncated["stage"]!.GetValue<int>().Should().Be(1);
        truncated["params"]!["alias"]!.GetValue<string>().Should().Be("attempts");
        truncated["params"]!["limit"]!.GetValue<int>().Should().Be(2);
        truncated["params"]!["rows"]!.GetValue<int>().Should().Be(1, "only the scenario shipment has more than two attempts");
        cut.Text.Should().NotContain("__oxLk", "the flag never reaches the wire");

        (await client.SendAsync(ReportSeed.Shipment, pipeline.Replace("LIMIT", "3"))).ShouldBeOk().ShouldHaveNoDiagnostics("three attempts under a limit of three are all there");
    }

    [Fact]
    public async Task A_truncated_lookup_under_a_group_is_still_reported()
    {
        var answer = await (await TransportClient()).SendAsync(ReportSeed.Shipment, """
            [ { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts", "limit": 1 } },
              { "unwind": { "path": "attempts" } },
              { "group": { "by": [ { "path": "attempts.status.displayName", "as": "status" } ], "fields": { "n": { "count": true } } } } ]
            """);

        answer.ShouldBeOk().ShouldHaveDiagnostic("LOOKUP_TRUNCATED")["params"]!["rows"]!.GetValue<int>().Should().Be(1, "one group holds the cut shipment's attempt");
    }

    [Fact]
    public async Task On_joins_the_children_of_a_resolved_alias()
    {
        var answer = await (await TransportClient()).SendAsync(ReportSeed.DeliveryAttempt, $$"""
            [ { "match": { "text": { "in": ["Recipient absent", "Delivered"] } } },
              { "resolve": { "path": "shipmentId", "as": "ship", "select": ["id", "shipmentNumber"] } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "ship", "as": "latest",
                            "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["text"] } },
              { "project": { "text": 1, "latest": 1 } } ]
            """);

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();

        var latest = answer.Items.ToDictionary(item => item!["text"]!.GetValue<string>(), item => item!["latest"]?["text"]?.GetValue<string>());

        latest.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Recipient absent"] = "Delivered to gate 3",
            ["Delivered"] = "Delivered",
        }, "each attempt's shipment's latest attempt");
    }

    [Fact]
    public async Task On_a_parent_the_row_does_not_hold_joins_no_children()
    {
        var answer = await (await TransportClient()).SendAsync(ReportSeed.DeliveryAttempt, """
            [ { "match": { "text": { "eq": "Recipient absent" } } },
              { "resolve": { "path": "shipmentId", "as": "ship", "filter": { "shipmentNumber": { "eq": "no such shipment" } } } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "ship", "as": "siblings", "select": ["text"] } },
              { "project": { "text": 1, "ship": 1, "siblings": 1 } } ]
            """);

        var row = answer.ShouldBeOk().Items.Should().ContainSingle().Which!;

        row["ship"].Should().BeNull();
        TextsIn(row["siblings"]).Should().BeEmpty("a missing parent is no key, not the null key");
    }

    [Fact]
    public async Task On_a_remote_alias_is_NOT_CONTINUABLE()
    {
        var ledger = await Lab.ClientAsync(LabService.Ledger, Org.R);
        var refused = await ledger.SendAsync(ReportSeed.Transaction, """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact" } },
              { "lookup": { "from": "ledger.billing_line", "path": "assignedTransactionId", "on": "recipientContact", "as": "lines" } } ]
            """);

        refused.ShouldRefuse("NOT_CONTINUABLE", 400)["stage"]!.GetValue<int>().Should().Be(1);
        refused.ErrorCodes.Should().Equal(["NOT_CONTINUABLE"]);
    }

    [Fact]
    public async Task On_a_lookup_array_is_LOOKUP_ON_NOT_ENTITY()
    {
        var refused = await (await TransportClient()).SendAsync(ReportSeed.Shipment, """
            [ { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts" } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "attempts", "as": "again" } } ]
            """);

        refused.ShouldRefuse("LOOKUP_ON_NOT_ENTITY", 400);
        refused.ErrorCodes.Should().Equal(["LOOKUP_ON_NOT_ENTITY"]);
    }
}
