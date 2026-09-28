using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The <c>invalid_key</c> outcome (DESIGN §3.3.4, §3.6) on the fleet: an ERP billing line whose
/// shipment reference selects the <c>shipment</c> case, whose key converts as a guid, and holds
/// <c>SHIP-12</c>. No shared seed holds such a line, so a private fleet with the ledger and
/// transport report rows gets one more line, a copy of the scenario's freight line with the bad key.
/// Outside strict the alias is null and silent; <c>onMissing: "report"</c> lists the row as
/// <c>invalid_key</c> beside a line that resolves; strict refuses.
/// </summary>
[Trait("Category", "Integration")]
public class ReportInvalidKeyTests : IAsyncLifetime
{
    private static readonly Guid BadKeyLineId = ReportSeed.Id(Spaces.LedgerBillingLine, 50);

    private CorpusFleet? fleet;

    public async Task InitializeAsync() =>
        fleet = await CorpusFleet.CreateAsync("e14b_invalid_key", [LabService.Ledger, LabService.Transport], async lab =>
        {
            var line = ReportSeed.Row(ReportSeed.BillingLine, "erp-line-shipment-freight").Stored.DeepClone().AsBsonDocument;

            line["_id"] = new BsonBinaryData(BadKeyLineId, GuidRepresentation.Standard);
            line["Text"] = "Bad shipment key";
            line["References"][0]["ReferenceId"] = "SHIP-12";

            var ledger = await lab.DatabaseAsync(LabService.Ledger);
            await ledger.GetCollection<BsonDocument>(LabService.Ledger.Model.Entities[ReportSeed.BillingLine].Collection).InsertOneAsync(line);
        });

    public async Task DisposeAsync()
    {
        if (fleet is not null)
            await fleet.DisposeAsync();
    }

    private static JsonObject Request(string onMissing, bool strict = false)
    {
        var body = Json.Request(ReportSeed.BillingLine, JsonNode.Parse($$"""
            [ { "match": { "id": { "in": ["{{ReportSeed.ErpLineIds[0]:D}}", "{{BadKeyLineId:D}}"] } } },
              { "sort": [ { "text": "asc" } ] },
              { "resolve": { "path": "references.referenceId", "as": "source", "elements": "first", "select": ["id"]{{onMissing}} } },
              { "project": { "text": 1, "source": 1 } } ]
            """)!);

        if (strict)
            body["strict"] = true;

        return body;
    }

    [Fact]
    public async Task A_shipment_key_that_is_no_guid_is_null_reported_invalid_key_and_refused_under_strict()
    {
        var ledger = fleet!.Client(LabService.Ledger, Org.R);

        var plain = await ledger.QueryAsync(Request(""));

        plain.ShouldBeOk().ShouldHaveNoDiagnostics("onMissing null reports only owner failures");
        plain.Strings("text").Should().Equal("Bad shipment key", "Freight Hamburg - Bremen");
        plain.Strings("source.id").Should().Equal(null, ReportSeed.ShipmentId.ToString("D"));

        var reported = await ledger.QueryAsync(Request(""", "onMissing": "report" """));
        var missing = reported.ShouldBeOk().ShouldHaveDiagnostic(Codes.ResolveMissing);

        missing["params"]!["rows"]!.ToJsonString().Should().Be("""[{"row":0,"element":0,"key":"SHIP-12","outcome":"invalid_key"}]""");
        reported.DiagnosticCodes.Should().Equal([Codes.ResolveMissing]);

        var refused = await ledger.QueryAsync(Request("", strict: true));

        refused.ShouldRefuse(Codes.ResolveMissing, 422)["params"]!["rows"]![0]!["outcome"]!.GetValue<string>().Should().Be("invalid_key");
        refused.ErrorCodes.Should().Equal([Codes.ResolveMissing]);
    }
}
