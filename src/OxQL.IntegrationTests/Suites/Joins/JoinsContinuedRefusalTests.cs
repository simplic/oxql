using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Report;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// A stage continued under a union alias without <c>forTarget</c> binds on every target, so a path one
/// target lacks refuses the request at that stage (R3 F2). The owning-row select paths the same target
/// lacks are dropped for it, as a run drops them, and are no part of the refusal: it carries only the
/// continued stage's own errors, never a stray <c>UNKNOWN_PATH</c> without a stage.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsContinuedRefusalTests
{
    private static string Id(Guid id) => id.ToString("D");

    /// <summary>A1's transport line with owning-row paths only one target has in part, and the shipment's tour continued under it for every target.</summary>
    private static string Request => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": ["id", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine",
                           "parentAs": "sourceParent", "select": ["id"] } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first", "select": ["id", "number"] } },
            { "project": { "position": 1, "sourceLine": 1, "sourceParent.id": 1, "sourceParent.shipmentNumber": 1, "sourceParent.number": 1, "deliveringTour": 1 } }
          ]
        }
        """;

    [Fact]
    public async Task A_refused_continued_stage_carries_only_its_own_errors_not_the_owning_row_paths_its_target_lacks()
    {
        var ledger = await Lab.ClientAsync(LabService.Ledger, Org.R);

        var refused = await ledger.QueryAsync(Request);

        refused.ShouldRefuse("RESOLVE_REFUSED", 422);
        refused.Errors.OfType<JsonObject>().Should().OnlyContain(error => error["stage"] != null && error["stage"]!.GetValue<int>() == 5,
            "every error is the continued stage's; 'shipmentNumber', which the tour lacks, is dropped for it, not refused");
        refused.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "UNKNOWN_PATH");
        refused.Errors.OfType<JsonObject>().Last()["path"]!.GetValue<string>().Should().Be("sourceParent.tours.tourId");

        var explained = await ledger.ExplainHereAsync(Request);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body!["errors"]!.AsArray().OfType<JsonObject>().Select(error => (error["code"]!.GetValue<string>(), error["stage"]?.GetValue<int>(), error["path"]!.GetValue<string>()))
            .Should().Equal([("UNKNOWN_PATH", 5, "sourceParent.tours.tourId")], "explain says what the run refuses, and only that");
    }
}
