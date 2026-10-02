using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>The requests a grid and a report send, each held against the commands it may cost cold and warm.</summary>
public partial class CommandBudgetTests
{
    [Fact]
    public Task S1_a_plain_page_with_its_total_is_the_page_and_the_count() => MeasureAsync(
        "S1",
        fleet => fleet.Client(LabService.Transport).SendAsync(Corpus.Shipment,
            """[ { "match": { "id": { "neq": null } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5, "includeTotalCount": true } } ]"""),
        cold: 2,
        warm: 2);

    [Fact]
    public Task S2_an_inline_resolve_adds_no_command_to_the_page_and_the_count() => MeasureAsync(
        "S2",
        fleet => fleet.Client(LabService.Fleet).SendAsync(Corpus.Equipment,
            """[ { "resolve": { "path": "vehicle.id", "as": "veh" } }, { "project": { "id": 1, "name": 1, "veh": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10, "includeTotalCount": true } } ]"""),
        cold: 2,
        warm: 2);

    [Fact]
    public Task S3_a_grid_page_with_two_keyed_joins_to_two_owners_is_one_fetch_per_owner_cold_and_none_warm() => MeasureAsync(
        "S3",
        fleet => fleet.Client(LabService.Ledger, Org.R).SendAsync(ReportSeed.Transaction,
            """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact", "select": ["address.companyName"] } },
              { "resolve": { "path": "createUserId", "as": "clerk", "onMissing": "null", "select": ["address.lastName"] } },
              { "project": { "number": 1, "date": 1, "recipientContact": 1, "clerk": 1 } },
              { "sort": [ { "number": "asc" } ] }, { "page": { "limit": 20, "includeTotalCount": true } } ]
            """),
        cold: 4,
        warm: 2);

    [Fact]
    public async Task S3_every_aggregate_of_the_request_carries_its_correlation_id_at_every_owner()
    {
        await using var fleet = await CorpusFleet.CreateAsync("commands_comment");
        using var capture = await MongoFixture.Commands.WatchAsync(fleet.Fleet);

        var answer = await fleet.Client(LabService.Ledger, Org.R).WithHeader(LabIdentity.CorrelationHeader, "corr-s3").SendAsync(ReportSeed.Transaction,
            """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact", "select": ["address.companyName"] } },
              { "resolve": { "path": "createUserId", "as": "clerk", "onMissing": "null", "select": ["address.lastName"] } },
              { "project": { "number": 1, "recipientContact": 1, "clerk": 1 } },
              { "sort": [ { "number": "asc" } ] }, { "page": { "limit": 20, "includeTotalCount": true } } ]
            """);
        var commands = capture.Drain().Engine;

        answer.StatusCode.Should().Be(200, answer.Text);
        commands.Should().HaveCount(4);
        commands.Select(command => command.Comment).Should().OnlyContain(comment => comment == "corr-s3", "the page, the count and each owner's fetch are found in a profiler from the one id");
    }

    [Fact]
    public Task S4_a_union_join_with_its_outcome_is_one_fetch_per_branch_target_and_the_page_alone_warm() => MeasureAsync(
        "S4",
        fleet => fleet.Client(LabService.Ledger, Org.R).QueryAsync(Lines($$"""
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first" } },
            { "resolve": { "as": "vehicle", "target": "fleet.vehicle", "onMissing": "report", "outcomeAs": "vehicleOutcome",
                           "byTarget": { "{{ReportSeed.Shipment}}": "deliveringTour.resource.id", "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } }
            """, """ "position": 1, "sourceParent.id": 1, "vehicle.matchCode": 1, "vehicleOutcome": 1 """)),
        cold: 5,
        warm: 1);

    [Fact]
    public Task S5_a_continued_chain_is_one_fetch_per_level_and_the_page_alone_warm() => MeasureAsync(
        "S5",
        fleet => fleet.Client(LabService.Ledger, Org.R).QueryAsync($$"""
            {
              "entityType": "ledger.transaction",
              "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
              "pipeline": [
                { "match": { "id": { "eq": { "$var": "transactionId" } } } },
                { "resolve": { "path": "items.billingLineId", "as": "billingLine", "elements": "first", "select": ["id"] } },
                { "resolve": { "path": "billingLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent", "select": ["id"] } },
                { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first", "forTarget": "{{ReportSeed.Shipment}}", "select": ["id", "number"] } },
                { "project": { "number": 1, "billingLine": 1, "sourceLine": 1, "sourceParent.id": 1, "deliveringTour": 1 } }
              ]
            }
            """),
        cold: 5,
        warm: 1);

    [Fact]
    public Task S6_the_invoice_report_with_eight_joins_to_four_owners_stays_within_eleven_commands_cold_and_four_warm() => MeasureAsync(
        "S6",
        fleet => fleet.Client(LabService.Ledger, Org.R).QueryAsync(InvoiceReport),
        cold: 11,
        warm: 4);

    [Fact]
    public Task S7_explain_of_the_invoice_report_sends_no_command() => MeasureAsync(
        "S7",
        fleet => fleet.Client(LabService.Ledger, Org.R).ExplainAsync(JsonNode.Parse(InvoiceReport)!),
        cold: 0,
        warm: 0);

    [Fact]
    public Task S7b_explain_with_the_index_advisory_reads_each_collections_index_list_once_and_none_warm() => MeasureAsync(
        "S7b",
        fleet => fleet.Client(LabService.Ledger, Org.R).ExplainAsync(JsonNode.Parse($$"""{ "query": {{InvoiceReport}}, "include": ["shape", "notes", "plan", "indexes"] }""")!),
        cold: 2,
        warm: 0);
}
