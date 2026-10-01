using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The report scenarios of DESIGN §2 (A1, A2a, A2b, A3, A4, A5) as the studio builds them: the
/// request files its acceptance specs write (<c>Fixtures/scenarios/&lt;id&gt;.request.json</c>, a
/// copy of the studio's <c>testing/fixtures/scenarios</c>). The scenario ids are the studio's:
/// <c>A2</c> is DESIGN's A2a. A run of the fixture export reads them from the export directory
/// instead, so a re-export follows the studio's current requests. Every scenario reads organisation R's report seeds.
/// </summary>
internal static class ReportScenarios
{
    /// <summary>The scenario ids, in the studio's order.</summary>
    public static readonly IReadOnlyList<string> Ids = ["A1", "A2", "A2b", "A3", "A4", "A5"];

    /// <summary>The directory holding the committed request files.</summary>
    public static string FixturesDirectory([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures");

    /// <summary>A scenario's request, read from <paramref name="directory"/> (the committed fixtures when null).</summary>
    public static JsonObject Request(string id, string? directory = null) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory ?? FixturesDirectory(), "scenarios", $"{id}.request.json")))!.AsObject();

    /// <summary>The service a scenario starts at: its root entity's namespace.</summary>
    public static LabService Service(JsonObject request) =>
        LabService.All.Single(service => service.Key == request["entityType"]!.GetValue<string>().Split('.')[0]);

    /// <summary>A client of the scenario's root service as organisation R, under contract 2 unless told otherwise.</summary>
    public static Task<LabClient> ClientAsync(JsonObject request, int? contract = 2) => Lab.ClientAsync(Service(request), Org.R, contract);

    /// <summary>The request with one variable set to another id: the same scenario over another seeded record.</summary>
    public static JsonObject WithVariable(JsonObject request, string name, Guid value)
    {
        var copy = request.DeepClone().AsObject();
        copy["variables"]![name] = value.ToString("D");
        return copy;
    }

    /// <summary>The request with one pipeline stage replaced.</summary>
    public static JsonObject WithStage(JsonObject request, int index, string stage)
    {
        var copy = request.DeepClone().AsObject();
        copy["pipeline"]![index] = JsonNode.Parse(stage);
        return copy;
    }

    /// <summary>The member a scenario alias's reference matches at its owner when it is not the key: the clerk is an employee found by its user.</summary>
    private static readonly IReadOnlyDictionary<string, string> Matched = new Dictionary<string, string>(StringComparer.Ordinal) { ["clerk"] = "userId" };

    /// <summary>
    /// The request without any <c>select</c> (improvement plan §3.S): every join's hint is removed, and
    /// where the projection kept the join's alias whole it names instead the paths the hint showed, with
    /// the key a whole alias carries beside its hint. The joins then load what the projection and the
    /// later stages read, and the rows are those of the request with the selects. A whole alias of
    /// another service's rows also carries the member its reference matches, where that is not the key
    /// (<see cref="Matched"/>).
    /// </summary>
    public static JsonObject WithoutSelects(JsonObject request)
    {
        var copy = request.DeepClone().AsObject();
        var shown = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var stage in copy["pipeline"]!.AsArray().OfType<JsonObject>())
            if ((stage["resolve"] ?? stage["lookup"]) is JsonObject join && join["select"] is JsonArray select && join["as"]?.GetValue<string>() is { } alias)
            {
                shown[alias] = ["id", .. select.Select(path => path!.GetValue<string>()).Where(path => path != "id"), .. Matched.TryGetValue(alias, out var member) ? [member] : Array.Empty<string>()];
                join.Remove("select");
            }

        foreach (var stage in copy["pipeline"]!.AsArray().OfType<JsonObject>().Where(stage => stage["project"] is JsonObject))
        {
            var fields = new JsonObject();

            foreach (var (name, value) in stage["project"]!.AsObject())
            {
                if (!shown.TryGetValue(name, out var paths))
                {
                    fields[name] = value!.DeepClone();
                    continue;
                }

                foreach (var path in paths)
                    fields[name + "." + path] = 1;
            }

            stage["project"] = fields;
        }

        return copy;
    }

    /// <summary>The index of the stage creating <paramref name="alias"/> (a resolve's or lookup's <c>as</c>).</summary>
    public static int IndexOf(JsonObject request, string alias) =>
        request["pipeline"]!.AsArray().Select((stage, index) => (stage, index))
            .Single(entry => entry.stage!.AsObject().Single().Value is JsonObject body && body["as"]?.GetValue<string>() == alias).index;

    // ── DESIGN §2, verbatim: what the studio's requests must equal ──────────────────────────

    public const string DesignA1 = """
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "7d0e4c1a-3b9e-4d55-9a57-3f1f1b0f2c11" },
          "strict": true,
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine",
                           "select": ["id", "text", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine",
                           "select": ["id", "type", "status", "singlePrice", "totalPrice", "quantity.value", "quantity.quantityUnit"],
                           "parentAs": "sourceParent" } },
            { "project": { "number": 1, "date": 1, "position": 1, "item.text": 1, "item.quantity": 1,
                           "item.totalPriceNet": 1, "erpLine": 1, "sourceLine": 1,
                           "sourceParent.id": 1, "sourceParent.shipmentNumber": 1, "sourceParent.referenceNumber": 1, "sourceParent.number": 1 } },
            { "sort": [ { "position": "asc" } ] },
            { "page": { "limit": 5000 } }
          ]
        }
        """;

    public const string DesignA2a = """
        {
          "entityType": "transport.shipment",
          "variables": { "shipmentId": "5b2a7e10-0c1d-4a5e-8f7b-9a1c2d3e4f50" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "shipmentId" } } } },
            { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "lastAttempt",
                          "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["id", "dateTime", "status", "text"] } },
            { "project": { "shipmentNumber": 1, "lastAttempt": 1 } }
          ]
        }
        """;

    /// <summary>A2b: the stage DESIGN appends to A1 after the second resolve.</summary>
    public const string DesignA2bStage = """
        { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "sourceParent",
                      "forTarget": "transport.shipment", "as": "lastAttempt",
                      "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["dateTime", "status"] } }
        """;

    public const string DesignA3 = """
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "7d0e4c1a-3b9e-4d55-9a57-3f1f1b0f2c11" },
          "strict": true,
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "resolve": { "path": "invoiceRecipient.address.id", "as": "recipientContact",
                           "select": ["primaryEmailAddress.email", "primaryPhoneNumber.number", "address.companyName"] } },
            { "project": { "number": 1, "invoiceRecipient": 1, "recipientContact": 1 } }
          ]
        }
        """;

    public const string DesignA5 = """
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "7d0e4c1a-3b9e-4d55-9a57-3f1f1b0f2c11" },
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
                           "sourceLine": 1, "sourceParent.id": 1, "sourceParent.shipmentNumber": 1, "sourceParent.referenceNumber": 1, "sourceParent.number": 1,
                           "lastAttempt": 1, "lineShipment": 1, "deliveringTour": 1,
                           "tourVehicle": 1, "driver": 1, "lineTour": 1 } },
            { "sort": [ { "position": "asc" } ] },
            { "page": { "limit": 5000 } }
          ]
        }
        """;
}
