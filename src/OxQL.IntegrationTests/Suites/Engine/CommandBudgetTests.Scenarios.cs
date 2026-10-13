using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
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
            """[ { "match": { "id": { "neq": null } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5, "count": true } } ]"""),
        cold: 2,
        warm: 2);

    [Fact]
    public Task S2_an_inline_resolve_adds_no_command_to_the_page_and_the_count() => MeasureAsync(
        "S2",
        fleet => fleet.Client(LabService.Fleet).SendAsync(Corpus.Equipment,
            """[ { "resolve": { "path": "vehicle.id", "as": "veh" } }, { "project": { "id": 1, "name": 1, "veh": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10, "count": true } } ]"""),
        cold: 2,
        warm: 2);

    /// <summary>
    /// A page with a next one whose order no index serves (the server sorts in memory, so it cannot
    /// tell the cursor is at its end from the limit alone), over the entity's rows, over unwound rows
    /// and over groups: one aggregate whose first reply closes the cursor, no <c>getMore</c>.
    /// </summary>
    [Theory]
    [InlineData("rows", """[ { "sort": [ { "referenceNumber": "desc" } ] }, { "project": { "id": 1, "referenceNumber": 1 } }, { "page": { "limit": 3 } } ]""")]
    [InlineData("unwound", """[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "sort": [ { "item.orderNumber": "desc" } ] }, { "project": { "id": 1, "position": 1 } }, { "page": { "limit": 3 } } ]""")]
    [InlineData("groups", """[ { "group": { "by": [ { "path": "shipmentNumber", "as": "number" } ], "fields": { "total": { "count": true } } } }, { "page": { "limit": 3 } } ]""")]
    public Task S2b_a_page_the_server_sorts_in_memory_is_one_aggregate_without_a_getMore(string label, string pipeline) => MeasureAsync(
        "S2b_" + label,
        async fleet =>
        {
            var answer = await fleet.Client(LabService.Transport).SendAsync(Corpus.Shipment, pipeline);

            answer.HasNextPage.Should().BeTrue("the case is a page with a next one: {0}", answer);

            return answer;
        },
        cold: 1,
        warm: 1);

    /// <summary>
    /// A <c>resolve</c> after a <c>group</c>: the record a group's key names. Of this service it is
    /// joined in the page's own aggregate, on the page's rows: no command more. Of another service it
    /// is one owner query for the page's keys, and none while that answer is kept.
    /// </summary>
    [Theory]
    [InlineData("local", 1, 1)]
    [InlineData("remote", 2, 1)]
    public Task S2d_the_record_a_groups_key_names_costs_nothing_here_and_one_owner_query_elsewhere_once(string where, int cold, int warm) => MeasureAsync(
        "S2d_" + where,
        async fleet =>
        {
            var answer = where == "local"
                ? await fleet.Client(LabService.Fleet).SendAsync(Corpus.Equipment, """
                    [ { "group": { "by": [ { "path": "vehicle.id", "as": "vehicle" } ], "fields": { "n": { "count": true } } } },
                      { "resolve": { "path": "vehicle", "as": "veh" } }, { "project": { "vehicle": 1, "n": 1, "veh.matchCode": 1 } },
                      { "sort": [ { "n": "desc" } ] }, { "page": { "limit": 100 } } ]
                    """)
                : await fleet.Client(LabService.Transport).SendAsync(Corpus.Shipment, """
                    [ { "group": { "by": [ { "path": "department.id", "as": "department" } ], "fields": { "n": { "count": true } } } },
                      { "resolve": { "path": "department", "as": "dep" } }, { "project": { "department": 1, "n": 1, "dep.name": 1 } },
                      { "sort": [ { "n": "desc" } ] }, { "page": { "limit": 100 } } ]
                    """);

            answer.ShouldBeOk();
            answer.Items.Should().Contain(row => row![where == "local" ? "veh" : "dep"] != null, "the case joins records: {0}", answer);

            return answer;
        },
        cold,
        warm);

    /// <summary>
    /// A page of groups with <c>count: true</c>. Not full, it is its own exact count and the groups are
    /// computed once: one aggregate. Full, the count follows the page: two aggregates, one after the other.
    /// </summary>
    [Theory]
    [InlineData(100, 1, false)]
    [InlineData(2, 2, true)]
    public Task S2c_a_page_of_groups_is_counted_only_when_it_comes_back_full(int limit, int commands, bool full) => MeasureAsync(
        "S2c_" + limit,
        async fleet =>
        {
            var answer = await fleet.Client(LabService.Transport).SendAsync(Corpus.Shipment,
                $$"""[ { "group": { "by": [ { "path": "status.name", "as": "status" } ], "fields": { "total": { "count": true } } } }, { "page": { "limit": {{limit}}, "count": true } } ]""");

            answer.HasNextPage.Should().Be(full, answer.ToString());
            answer.CountState.Should().Be("exact", answer.ToString());

            return answer;
        },
        cold: commands,
        warm: commands);

    /// <summary>The count alone: a first step that proves a lower bound, then one sized to the time left. Two aggregates above the first step's bound, one below it.</summary>
    [Theory]
    [InlineData(Org.C, 2)]
    [InlineData(Org.B, 1)]
    public Task S2d_the_count_alone_is_one_aggregate_up_to_its_first_step_and_two_above_it(Org org, int commands) => MeasureAsync(
        "S2d_" + org,
        async fleet =>
        {
            var answer = await fleet.Client(LabService.Transport, org).CountOnlyAsync(Corpus.Template, "[]", budgetMs: 5_000);

            answer.CountState.Should().Be("exact", answer.ToString());

            return answer;
        },
        cold: commands,
        warm: commands,
        extra: org == Org.C ? SeedTemplatesAsync : null);

    [Fact]
    public Task S3_a_grid_page_with_two_keyed_joins_to_two_owners_is_one_fetch_per_owner_cold_and_none_warm() => MeasureAsync(
        "S3",
        fleet => fleet.Client(LabService.Ledger, Org.R).SendAsync(ReportSeed.Transaction,
            """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact" } },
              { "resolve": { "path": "createUserId", "as": "clerk", "onMissing": "null" } },
              { "project": { "number": 1, "date": 1, "recipientContact.id": 1, "recipientContact.address.companyName": 1, "clerk.id": 1, "clerk.address.lastName": 1 } },
              { "sort": [ { "number": "asc" } ] }, { "page": { "limit": 20, "count": true } } ]
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
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact" } },
              { "resolve": { "path": "createUserId", "as": "clerk", "onMissing": "null" } },
              { "project": { "number": 1, "recipientContact.id": 1, "recipientContact.address.companyName": 1, "clerk.id": 1, "clerk.address.lastName": 1 } },
              { "sort": [ { "number": "asc" } ] }, { "page": { "limit": 20, "count": true } } ]
            """);
        var commands = capture.Drain().Engine;

        answer.StatusCode.Should().Be(200, answer.Text);

        // The page and each owner's fetch are always sent. The count runs beside the page, and this page ends the list:
        // the rows served are the count then, and a count the database has not been sent by that time is not sent.
        commands.Count.Should().BeInRange(3, 4, string.Join(", ", commands.Select(command => $"{Service(command.Database)}.{command.Collection}")));
        commands.Select(command => Service(command.Database)).Distinct().Should().HaveCount(3, "the page and the fetch of each of the two owners");
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

    /// <summary>
    /// A chain whose owner reports something about the rows it answers (a tour whose vehicle no record
    /// holds): the tours are kept with that report, so the same page again is the page alone and says
    /// the same. Before, an answer with a report was not kept and the tours were fetched on every repeat.
    /// </summary>
    [Fact]
    public async Task S5b_a_chain_whose_owner_reports_a_missing_record_is_the_page_alone_warm_and_says_the_same()
    {
        var answers = new List<WireAnswer>();

        await MeasureAsync(
            "S5b",
            async fleet =>
            {
                var answer = await fleet.Client(LabService.Transport, Org.C).SendAsync(Corpus.Shipment, """
                    [ { "resolve": { "path": "tours.tourId", "as": "tour", "elements": "first" } },
                      { "resolve": { "path": "tour.resource.id", "as": "vehicle", "target": "fleet.vehicle", "onMissing": "report" } },
                      { "project": { "shipmentNumber": 1, "tour.number": 1, "vehicle.matchCode": 1 } },
                      { "sort": [ { "shipmentNumber": "asc" } ] }, { "page": { "limit": 10 } } ]
                    """);

                answers.Add(answer);

                return answer;
            },
            cold: 3,
            warm: 1,
            extra: SeedDanglingVehicleAsync);

        answers[0].ShouldHaveDiagnostic("RESOLVE_MISSING")["params"]!["count"]!.GetValue<long>().Should().Be(2, "both shipments ride the tour whose vehicle is missing");
        answers[1].Text.Should().Be(answers[0].Text, "the page answered from the cache says what the owners' answers said");
    }

    /// <summary>Three shipments of organisation C on two tours, one of which names a vehicle no record holds.</summary>
    private static async Task SeedDanglingVehicleAsync(LabFleet fleet)
    {
        var organisation = Org.C.Id();
        var transport = await fleet.DatabaseAsync(LabService.Transport);
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var vehicle = Ids.Of("20000001", Org.C, 1);

        await (await fleet.DatabaseAsync(LabService.Fleet)).GetCollection<BsonDocument>(LabService.Fleet.Model.Entities["fleet.vehicle"].Collection)
            .InsertOneAsync(new Fleet.Models.Fleet.Vehicle { Id = vehicle, OrganizationId = organisation, MatchCode = "V-0001" }.ToBsonDocument());

        await transport.GetCollection<BsonDocument>(LabService.Transport.Model.Entities["transport.tour"].Collection).InsertManyAsync(new[] { (1, vehicle), (2, Ids.Dangling(7, Org.C)) }.Select(tour =>
            new Fleet.Models.Transport.Tour
            {
                Id = Ids.Of(Spaces.Tour, Org.C, tour.Item1),
                OrganizationId = organisation,
                Number = "T-" + tour.Item1,
                Resource = new Fleet.Models.Transport.TractorUnitResource { Id = tour.Item2, OrganizationId = organisation },
                CreateDateTime = at,
                UpdateDateTime = at,
            }.ToBsonDocument()));

        await transport.GetCollection<BsonDocument>(Corpus.Entity(Corpus.Shipment).Collection).InsertManyAsync(Enumerable.Range(1, 3).Select(n =>
            new Fleet.Models.Transport.Shipment
            {
                Id = Ids.Of(Spaces.Shipment, Org.C, n),
                OrganizationId = organisation,
                ShipmentNumber = "S-" + n,
                Tours = [new Fleet.Models.Transport.ShipmentTour { Id = Ids.Of(Spaces.ShipmentTourEntry, Org.C, n), TourId = Ids.Of(Spaces.Tour, Org.C, n == 1 ? 1 : 2) }],
                CreateDateTime = at,
                UpdateDateTime = at,
            }.ToBsonDocument()));
    }

    [Fact]
    public Task S5_a_continued_chain_is_one_fetch_per_level_and_the_page_alone_warm() => MeasureAsync(
        "S5",
        fleet => fleet.Client(LabService.Ledger, Org.R).QueryAsync($$"""
            {
              "entityType": "ledger.transaction",
              "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
              "pipeline": [
                { "match": { "id": { "eq": { "$var": "transactionId" } } } },
                { "resolve": { "path": "items.billingLineId", "as": "billingLine", "elements": "first" } },
                { "resolve": { "path": "billingLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
                { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first", "forTarget": "{{ReportSeed.Shipment}}" } },
                { "project": { "number": 1, "billingLine.id": 1, "sourceLine.id": 1, "sourceParent.id": 1, "deliveringTour.id": 1, "deliveringTour.number": 1 } }
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
