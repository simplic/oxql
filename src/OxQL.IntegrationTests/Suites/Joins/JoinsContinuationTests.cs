using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Health;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// Remote continuation on the fleet (DESIGN §3.5.3, §3.5.4, scenarios A1/A2b/A5): the stages that
/// continue from a record another service returned ride in the owner query the keyed fetch already
/// sends over the internal batch route, and the owner runs them for the keys it was sent — the
/// latest delivery attempt of each shipment line of the mixed invoice under <c>strict</c>, a chain
/// ledger → transport → transport (in process) → fleet, an owner's refusal of a continued stage
/// mapped back to the caller's stage, <c>MAX_CONTINUED_STAGES_EXCEEDED</c>, <c>NOT_CONTINUABLE</c>,
/// and <c>OWNER_NOT_CAPABLE</c> for an owner whose health reports an older engine. The rows are the
/// report seeds of organisation R.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsContinuationTests
{
    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>
    /// A1 with A2b appended after the second resolve (DESIGN §2), A1's flat <c>parentSelect</c> as
    /// written: each of the two remote targets gets only the paths its row has.
    /// </summary>
    private static string A1WithLatestAttempt => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
          "strict": true,
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine",
                           "select": ["id", "text", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine",
                           "select": ["id", "type", "status", "singlePrice", "totalPrice", "quantity.value", "quantity.quantityUnit"],
                           "parentAs": "sourceParent", "parentSelect": ["id", "shipmentNumber", "referenceNumber", "number"] } },
            { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "sourceParent",
                          "forTarget": "transport.shipment", "as": "lastAttempt",
                          "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["dateTime", "status"] } },
            { "project": { "number": 1, "date": 1, "position": 1, "item.text": 1, "item.quantity": 1,
                           "item.totalPriceNet": 1, "erpLine": 1, "sourceLine": 1, "sourceParent": 1, "lastAttempt": 1 } },
            { "sort": [ { "position": "asc" } ] },
            { "page": { "limit": 5000 } }
          ]
        }
        """;

    [Fact]
    public async Task A2b_the_latest_attempt_of_each_shipment_line_continues_at_transport_and_a_tour_line_is_not_applicable_under_strict()
    {
        var answer = await (await LedgerClient()).QueryAsync(A1WithLatestAttempt);

        answer.ShouldBeOk();
        var rows = answer.Items.OfType<JsonObject>().ToList();
        rows.Should().NotBeEmpty();

        var shipmentRows = rows.Where(row => row["sourceParent"]?["entity"]?.GetValue<string>() == ReportSeed.Shipment).ToList();
        var tourRows = rows.Where(row => row["sourceParent"]?["entity"]?.GetValue<string>() == ReportSeed.Tour).ToList();

        shipmentRows.Should().NotBeEmpty("the mixed invoice has shipment lines");
        tourRows.Should().NotBeEmpty("and a tour line");

        var scenario = shipmentRows.Where(row => row["sourceParent"]!["id"]!.GetValue<string>() == Id(ReportSeed.ShipmentId)).ToList();

        scenario.Should().NotBeEmpty("the scenario shipment's freight and waiting-time lines are on the invoice");

        foreach (var row in scenario)
        {
            row["lastAttempt"]!["dateTime"]!.GetValue<string>().Should().StartWith("2026-04-08T14:30", "the latest attempt of the scenario shipment");
            row["lastAttempt"]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "dateTime", "status"], "the select, and the key");
        }

        tourRows.Should().OnlyContain(row => row["lastAttempt"] == null, "the lookup applies to shipment lines only");
        shipmentRows.Should().OnlyContain(row => row["sourceParent"]!["shipmentNumber"] != null && !row["sourceParent"]!.AsObject().ContainsKey("number"),
            "a shipment row carries the shipment's paths of the flat select, and not the tour's");
        tourRows.Should().OnlyContain(row => row["sourceParent"]!["number"] != null && !row["sourceParent"]!.AsObject().ContainsKey("shipmentNumber"),
            "a tour row the tour's");
        rows.Should().OnlyContain(row => !row["sourceParent"]!.AsObject().ContainsKey("lastAttempt"), "the continued alias is lifted to the row, not left on the owning row");
    }

    [Fact]
    public async Task A5_a_chain_continues_from_ledger_at_transport_in_process_at_transport_and_on_at_fleet()
    {
        var answer = await (await LedgerClient()).QueryAsync($$"""
            {
              "entityType": "ledger.transaction",
              "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
              "pipeline": [
                { "match": { "id": { "eq": { "$var": "transactionId" } } } },
                { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
                { "match": { "item": { "is": "BillingLineTransactionItem" } } },
                { "resolve": { "path": "item.references.referenceId", "as": "lineShipment", "elements": "first", "target": "transport.shipment",
                               "select": ["shipmentNumber"] } },
                { "resolve": { "path": "lineShipment.tours.tourId", "as": "deliveringTour", "elements": "first", "select": ["number"] } },
                { "resolve": { "path": "deliveringTour.resource.id", "as": "tourVehicle", "target": "fleet.vehicle", "onMissing": "null",
                               "select": ["registrationPlate.registrationIdentifier", "matchCode"] } },
                { "project": { "position": 1, "lineShipment": 1, "deliveringTour": 1, "tourVehicle": 1 } },
                { "sort": [ { "position": "asc" } ] }
              ]
            }
            """);

        answer.ShouldBeOk();
        var chained = answer.Items.OfType<JsonObject>().Where(row => row["deliveringTour"] is JsonObject).ToList();

        chained.Should().NotBeEmpty("a shipment line of the invoice has a delivering tour");
        chained.Should().OnlyContain(row => row["lineShipment"] is JsonObject, "a tour is only reached through its shipment");
        chained.Should().Contain(row => row["tourVehicle"] != null && row["tourVehicle"]!["matchCode"] != null, "the tour's tractor, resolved at fleet two hops away");
        answer.Items.OfType<JsonObject>().Where(row => row["lineShipment"] == null).Should().OnlyContain(row => row["deliveringTour"] == null && row["tourVehicle"] == null);
    }

    [Fact]
    public async Task A5_the_full_invoice_as_written_runs_under_strict()
    {
        var answer = await (await LedgerClient()).QueryAsync($$"""
            {
              "entityType": "ledger.transaction",
              "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
              "strict": true,
              "pipeline": [
                { "match": { "id": { "eq": { "$var": "transactionId" } } } },
                { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact",
                               "select": ["primaryEmailAddress.email", "primaryPhoneNumber.number", "address.companyName"] } },
                { "resolve": { "path": "createUserId", "as": "clerk", "onMissing": "null",
                               "select": ["address.firstName", "address.lastName", "primaryEmailAddress.email"] } },
                { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
                { "match": { "item": { "is": "BillingLineTransactionItem" } } },
                { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": ["id", "text", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
                { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine",
                               "select": ["id", "type", "status", "singlePrice", "totalPrice", "quantity.value", "quantity.quantityUnit"],
                               "parentAs": "sourceParent", "parentSelect": ["id", "shipmentNumber", "referenceNumber", "number"] } },
                { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "sourceParent", "forTarget": "transport.shipment",
                              "as": "lastAttempt", "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["dateTime", "status"] } },
                { "resolve": { "path": "item.references.referenceId", "as": "lineShipment", "elements": "first", "target": "transport.shipment",
                               "select": ["shipmentNumber", "referenceNumber", "loadAddress", "deliveryAddress", "effectiveDeliveryEnd",
                                          "deliveryNoteNumber", "items.weightNotes.number", "items.weightNotes.quantity"] } },
                { "resolve": { "path": "lineShipment.tours.tourId", "as": "deliveringTour", "elements": "first", "select": ["number"] } },
                { "resolve": { "path": "deliveringTour.resource.id", "as": "tourVehicle", "target": "fleet.vehicle", "onMissing": "null",
                               "select": ["registrationPlate.registrationIdentifier", "matchCode"] } },
                { "resolve": { "path": "deliveringTour.attachedResources.resource.id", "as": "driver", "elements": "first", "target": "staff.employee",
                               "onMissing": "null", "select": ["address.firstName", "address.lastName", "primaryEmailAddress.email"] } },
                { "resolve": { "path": "item.references.referenceId", "as": "lineTour", "elements": "first", "target": "transport.tour",
                               "select": ["number", "startDateTime", "endDateTime", "actions"] } },
                { "project": { "number": 1, "date": 1, "dueDate": 1, "invoiceRecipient": 1, "termsOfPayment.formattedText": 1,
                               "totalPriceNet": 1, "totalPriceGross": 1, "taxKeyTotalPrices": 1, "recipientContact": 1, "clerk": 1,
                               "position": 1, "item.text": 1, "item.quantity": 1, "item.totalPriceNet": 1, "erpLine": 1,
                               "sourceLine": 1, "sourceParent": 1, "lastAttempt": 1, "lineShipment": 1, "deliveringTour": 1,
                               "tourVehicle": 1, "driver": 1, "lineTour": 1 } },
                { "sort": [ { "position": "asc" } ] },
                { "page": { "limit": 5000 } }
              ]
            }
            """);

        answer.ShouldBeOk();
        var rows = answer.Items.OfType<JsonObject>().ToList();

        rows.Should().NotBeEmpty();
        rows.Where(row => row["sourceParent"]?["entity"]?.GetValue<string>() == ReportSeed.Tour).Should().OnlyContain(row => row["lastAttempt"] == null);
        rows.Should().Contain(row => row["lastAttempt"] is JsonObject, "a shipment line's latest attempt");
        rows.Should().Contain(row => row["tourVehicle"] is JsonObject, "a tour's vehicle, two services away");
    }

    [Fact]
    public async Task A_lookup_on_a_remote_alias_continues_at_its_owner_and_the_owners_refusal_maps_back_to_the_callers_stage()
    {
        var ledger = await LedgerClient();
        var refused = await ledger.SendAsync(ReportSeed.Transaction, """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact" } },
              { "lookup": { "from": "ledger.billing_line", "path": "assignedTransactionId", "on": "recipientContact", "as": "lines" } } ]
            """);

        refused.ShouldRefuse("RESOLVE_REFUSED", 422)["stage"]!.GetValue<int>().Should().Be(1, "the continued lookup the owner refused");
        refused.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "UNKNOWN_ENTITY");

        var mapped = refused.Errors[1];
        mapped["stage"]!.GetValue<int>().Should().Be(1);
        mapped["params"]!["owner"]!["service"]!.GetValue<string>().Should().Be("directory");
        mapped["params"]!["owner"]!["entity"]!.GetValue<string>().Should().Be(ReportSeed.Contact);
        mapped["params"]!["owner"]!["stage"]!.GetValue<int>().Should().Be(1, "the owner query holds its key match first");
    }

    [Fact]
    public async Task More_stages_continued_under_one_alias_than_MaxContinuedStages_is_MAX_CONTINUED_STAGES_EXCEEDED()
    {
        var shared = await CorpusFleet.SharedAsync();
        var ledger = shared.Variant(LabService.Ledger, "e11-max-continued-1", new Dictionary<string, string?> { ["OxQL:Limits:MaxContinuedStages"] = "1" }, Org.R);

        var refused = await ledger.SendAsync(ReportSeed.Transaction, """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "c" } },
              { "resolve": { "path": "c.address.id", "as": "c1" } },
              { "resolve": { "path": "c.address.id", "as": "c2" } } ]
            """);

        refused.ShouldRefuse("MAX_CONTINUED_STAGES_EXCEEDED", 400)["stage"]!.GetValue<int>().Should().Be(2);
        refused.ErrorCodes.Should().Equal(["MAX_CONTINUED_STAGES_EXCEEDED"]);
    }

    [Fact]
    public async Task An_unwind_under_a_remote_alias_is_NOT_CONTINUABLE()
    {
        var refused = await (await LedgerClient()).SendAsync(ReportSeed.Transaction, """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact" } },
              { "unwind": { "path": "recipientContact.tags" } } ]
            """);

        refused.ShouldRefuse("NOT_CONTINUABLE", 400)["stage"]!.GetValue<int>().Should().Be(1);
        refused.ErrorCodes.Should().Equal(["NOT_CONTINUABLE"]);
    }

    /// <summary>An owner of <c>owner.widget</c> whose shallow health reports OxQL 2.0; it refuses every batch, which none must reach.</summary>
    private sealed class OldOwner : HttpMessageHandler
    {
        public int Batches;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/OxQL/health", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "status": "healthy", "engine": { "version": "2.0.126.924", "contract": 2 }, "limits": { "maxBatchQueries": 10 } }""", Encoding.UTF8, "application/json"),
                });

            Interlocked.Increment(ref Batches);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    [Fact]
    public async Task A_continued_stage_for_an_owner_whose_health_reports_OxQL_2_0_is_OWNER_NOT_CAPABLE_before_anything_is_sent()
    {
        await using var fleet = LabFleet.Create("e11_owner_capable");
        var owner = new OldOwner();
        fleet.Mount(LabFleet.ExternalOwner, owner);

        var host = await fleet.HostAsync(LabService.Conformance);
        using var client = host.Client();

        // The host learns the owner's engine where it measures reachability, behind its health.
        (await client.GetAsync("OxQL/health")).IsSuccessStatusCode.Should().BeTrue();
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;

        using var content = new StringContent($$"""
            { "entityType": "{{Corpus.Conformance}}", "pipeline": [
                { "resolve": { "path": "widgetCodeExplicit", "as": "w" } },
                { "resolve": { "path": "w.code", "as": "again" } } ] }
            """, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var response = await client.PostAsync("OxQL/query", content);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        ((int)response.StatusCode).Should().Be(422, body.ToJsonString());
        var error = body["errors"]!.AsArray().Should().ContainSingle().Subject!;
        error["code"]!.GetValue<string>().Should().Be("OWNER_NOT_CAPABLE");
        error["stage"]!.GetValue<int>().Should().Be(1);
        error["message"]!.GetValue<string>().Should().Be("owner runs OxQL 2.0.126.924; this stage needs 2.1.");
        owner.Batches.Should().Be(0);
    }
}
