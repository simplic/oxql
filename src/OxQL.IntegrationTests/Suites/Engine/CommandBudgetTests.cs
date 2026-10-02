using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What a request costs the database, counted: the commands the engine sends (aggregate, getMore,
/// listIndexes) for the requests a grid and a report send, cold and warm, against a ceiling. A
/// change that adds a round trip to one of them fails here instead of showing up as latency against
/// a hosted database.
/// <para>
/// Each case runs on a fleet of its own, so its caches are cold and no other test's commands reach
/// its databases; the commands are read off the run's client (<see cref="MongoFixture.Commands"/>).
/// The test addon source reads its definitions with an uncached <c>find</c> on every bind, which a
/// production host caches, so those reads are not the engine's and are not counted.
/// </para>
/// <para>
/// <c>OXQL_COMMAND_REPORT=&lt;file&gt;</c> appends what each case saw (the commands and the most of
/// them one database answered one after another) to that file.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public partial class CommandBudgetTests
{
    internal const string ReportVariable = "OXQL_COMMAND_REPORT";

    internal static readonly object ReportGate = new();

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>Sends the request twice on a fresh fleet and holds both runs against their ceilings; no run may cost a getMore.</summary>
    private static async Task MeasureAsync(string label, Func<CorpusFleet, Task<WireAnswer>> send, int cold, int warm, Func<LabFleet, Task>? extra = null)
    {
        await using var fleet = await CorpusFleet.CreateAsync("commands_" + label, extra: extra);
        using var capture = await MongoFixture.Commands.WatchAsync(fleet.Fleet);

        var first = await send(fleet);
        var coldRun = capture.Drain();
        var second = await send(fleet);
        var warmRun = capture.Drain();

        Report(label, "cold", coldRun);
        Report(label, "warm", warmRun);

        first.StatusCode.Should().Be(200, first.Text);
        second.StatusCode.Should().Be(200, second.Text);
        coldRun.Count("getMore").Should().Be(0, $"no page or keyed fetch pays a second round trip for its rows; cold: {coldRun}");
        warmRun.Count("getMore").Should().Be(0, $"no page or keyed fetch pays a second round trip for its rows; warm: {warmRun}");
        coldRun.Engine.Count.Should().BeLessThanOrEqualTo(cold, $"cold: {coldRun}");
        warmRun.Engine.Count.Should().BeLessThanOrEqualTo(warm, $"warm: {warmRun}");
    }

    private static void Report(string label, string run, CommandBatch batch)
    {
        if (Environment.GetEnvironmentVariable(ReportVariable) is not { Length: > 0 } file)
            return;

        var perDatabase = batch.Engine
            .GroupBy(command => command.Database)
            .Select(group => $"{Service(group.Key)}={group.Count()}/depth {batch.DepthByDatabase[group.Key]}")
            .OrderBy(text => text, StringComparer.Ordinal);
        var origin = batch.Engine.Count == 0 ? 0 : batch.Engine.Min(command => command.Started);
        var timeline = batch.Engine.Select(command =>
            $"{Service(command.Database)}.{command.Collection} {System.Diagnostics.Stopwatch.GetElapsedTime(origin, command.Started).TotalMilliseconds:F1}-{System.Diagnostics.Stopwatch.GetElapsedTime(origin, command.Ended).TotalMilliseconds:F1}");
        var line = $"{label} | {run} | engine={batch.Engine.Count} | aggregate={batch.Count("aggregate")} getMore={batch.Count("getMore")} listIndexes={batch.Count("listIndexes")} | depth={batch.Depth} | {string.Join(", ", perDatabase)} | ms: {string.Join("; ", timeline)}";

        lock (ReportGate)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    /// <summary>The service a fleet database belongs to: its name ends in the service key and a sequence number.</summary>
    private static string Service(string database) =>
        LabService.All.Select(service => service.Key).FirstOrDefault(key => database.Contains($"_{key}_", StringComparison.Ordinal)) ?? database;

    // ---- the requests the cases and the depth cases share ------------------------------------

    internal static string Lines(string stages, string project) => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine" } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
            {{stages}},
            { "project": { {{project}} } },
            { "sort": [ { "position": "asc" } ] }
          ]
        }
        """;

    internal static readonly string InvoiceReport = $$"""
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
                           "parentAs": "sourceParent" } },
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
                           "sourceLine": 1, "sourceParent.id": 1, "sourceParent.shipmentNumber": 1, "sourceParent.referenceNumber": 1, "sourceParent.number": 1, "lastAttempt": 1, "lineShipment": 1, "deliveringTour": 1,
                           "tourVehicle": 1, "driver": 1, "lineTour": 1 } },
            { "sort": [ { "position": "asc" } ] },
            { "page": { "limit": 5000 } }
          ]
        }
        """;

    /// <summary>
    /// A page above the driver's default first batch (101 documents) arrives in the one reply of its
    /// aggregate: the cursor's batch is sized to the page, so no <c>getMore</c> follows. The count is
    /// the second command.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(250)]
    [InlineData(500)]
    public Task S8_a_page_above_the_default_first_batch_costs_no_getMore(int limit) => MeasureAsync(
        "S8_" + limit,
        async fleet =>
        {
            var answer = await fleet.Client(LabService.Transport, Org.C).SendAsync(Corpus.Template,
                $$"""[ { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1 } }, { "page": { "limit": {{limit}}, "includeTotalCount": true } } ]""");

            answer.Items.Should().HaveCount(limit);

            return answer;
        },
        cold: 2,
        warm: 2,
        extra: SeedTemplatesAsync);

    /// <summary>600 thin shipment templates in organisation C: more than the largest page asks for.</summary>
    private static async Task SeedTemplatesAsync(LabFleet fleet)
    {
        var collection = (await fleet.DatabaseAsync(LabService.Transport)).GetCollection<BsonDocument>(Corpus.Entity(Corpus.Template).Collection);
        var at = new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var organisation = new BsonBinaryData(Org.C.Id(), GuidRepresentation.Standard);

        await collection.InsertManyAsync(Enumerable.Range(1, 600).Select(n => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(BulkRows.IdOf(n), GuidRepresentation.Standard),
            ["OrganizationId"] = organisation,
            ["TemplateName"] = BulkRows.NameOf(n),
            ["ShipmentNumber"] = $"ZB-{n:D6}",
            ["IsShipmentConversionDisabled"] = false,
            ["Items"] = new BsonArray(),
            ["Documents"] = new BsonArray(),
            ["Tags"] = new BsonArray(),
            ["BillingLines"] = new BsonArray(),
            ["CreateDateTime"] = at,
            ["UpdateDateTime"] = at,
            ["CreateUserName"] = "bulk",
            ["UpdateUserName"] = "bulk",
            ["IsDeleted"] = false,
        }), new InsertManyOptions { IsOrdered = false });
    }
}
