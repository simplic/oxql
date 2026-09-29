using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// The chains the studio's route finder builds (ledger → billing line → transport billing line and its
/// owning row → tour → vehicle), which nest continued stages under continued stages: <c>forTarget</c>
/// on a stage continued under a nested alias names a target of that alias, not of the first keyed
/// alias; a select member inside a collection that a continued hop resolves again at the owner merges
/// with it rather than colliding in the owner's projection; and explain checks the continued stages of
/// a local keyed stage as a run binds them, so explain and the run agree on a union's other target.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsRouteChainTests
{
    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>
    /// The billing line is resolved element-wise (keyed at this host), the transport line continues
    /// under it, and the delivering tour continues under the transport line's owning row with
    /// <paramref name="forTarget"/>, or none.
    /// </summary>
    private static string NestedChain(string? target, string? forTarget) => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "resolve": { "path": "items.billingLineId", "as": "billingLine", "elements": "first", "select": ["id"] } },
            { "resolve": { "path": "billingLine.sourceBillingLineReference.id", "as": "sourceLine"{{(target is null ? "" : $", \"target\": \"{target}\"")}},
                           "parentAs": "sourceParent", "select": ["id"], "parentSelect": ["id"] } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first"{{(forTarget is null ? "" : $", \"forTarget\": \"{forTarget}\"")}},
                           "select": ["id", "number"] } },
            { "project": { "number": 1, "billingLine": 1, "sourceLine": 1, "sourceParent": 1, "deliveringTour": 1 } }
          ]
        }
        """;

    [Fact]
    public async Task ForTarget_on_a_stage_continued_under_a_nested_alias_names_a_target_of_that_alias_and_runs()
    {
        var ledger = await LedgerClient();
        var request = NestedChain(target: null, forTarget: ReportSeed.Shipment);

        var explained = await ledger.ExplainHereAsync(request);
        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);

        var answer = await ledger.QueryAsync(request);
        answer.ShouldBeOk();

        var row = answer.Items.OfType<JsonObject>().Should().ContainSingle().Subject;
        row["sourceParent"]!["entity"]!.GetValue<string>().Should().Be(ReportSeed.Shipment, "the first billing line of the invoice is a shipment line");
        row["deliveringTour"].Should().BeOfType<JsonObject>("the shipment's first tour, continued at transport for the shipment target");
    }

    [Fact]
    public async Task ForTarget_on_a_nested_alias_naming_no_target_of_it_is_refused_by_its_owner_at_the_callers_stage()
    {
        var ledger = await LedgerClient();
        var request = NestedChain(target: null, forTarget: ReportSeed.Vehicle);

        var explained = await ledger.ExplainHereAsync(request);
        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body!["errors"]!.AsArray().OfType<JsonObject>().Should().Contain(error => error["code"]!.GetValue<string>() == "OPTION_NOT_APPLICABLE" && error["stage"]!.GetValue<int>() == 3);

        var refused = await ledger.QueryAsync(request);
        refused.ShouldRefuse("RESOLVE_REFUSED", 422);
        refused.Errors.OfType<JsonObject>().Should().Contain(error => error["code"]!.GetValue<string>() == "OPTION_NOT_APPLICABLE" && error["stage"]!.GetValue<int>() == 3);
    }

    [Fact]
    public async Task Explain_checks_a_union_stage_continued_under_a_local_keyed_alias_at_every_target_as_the_run_binds_it()
    {
        var ledger = await LedgerClient();
        var request = NestedChain(target: null, forTarget: null);

        var explained = await ledger.ExplainHereAsync(request);
        var refused = await ledger.QueryAsync(request);

        // tours.tourId is a path of the shipment, not of the tour: both say so, at the caller's stage.
        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body!["errors"]!.AsArray().OfType<JsonObject>().Should().Contain(error => error["code"]!.GetValue<string>() == "UNKNOWN_PATH" && error["stage"]!.GetValue<int>() == 3);

        refused.ShouldRefuse("RESOLVE_REFUSED", 422);
        refused.Errors.OfType<JsonObject>().Should().Contain(error => error["code"]!.GetValue<string>() == "UNKNOWN_PATH" && error["stage"]!.GetValue<int>() == 3);
    }

    [Fact]
    public async Task Explain_and_the_run_agree_on_a_chain_narrowed_by_target_under_a_local_keyed_alias()
    {
        var ledger = await LedgerClient();
        var request = NestedChain(target: ReportSeed.Shipment, forTarget: null);

        var explained = await ledger.ExplainHereAsync(request);
        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);

        (await ledger.QueryAsync(request)).ShouldBeOk();
    }

    [Fact]
    public async Task A_select_member_inside_a_collection_that_a_continued_hop_resolves_is_merged_at_the_owner_not_an_internal_error()
    {
        var ledger = await LedgerClient();
        var request = $$"""
            {
              "entityType": "ledger.transaction",
              "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
              "pipeline": [
                { "match": { "id": { "eq": { "$var": "transactionId" } } } },
                { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
                { "match": { "item": { "is": "BillingLineTransactionItem" } } },
                { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": ["id", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
                { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "target": "{{ReportSeed.Shipment}}",
                               "parentAs": "sourceParent", "select": ["id"], "parentSelect": ["id", "tours.tourId"] } },
                { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first", "select": ["id", "number"] } },
                { "project": { "position": 1, "sourceLine": 1, "sourceParent": 1, "deliveringTour": 1 } },
                { "sort": [ { "position": "asc" } ] }
              ]
            }
            """;

        var explained = await ledger.ExplainHereAsync(request);
        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);

        var answer = await ledger.QueryAsync(request);
        answer.ShouldBeOk();

        var scenario = answer.Items.OfType<JsonObject>()
            .Where(row => row["sourceParent"]?["id"]?.GetValue<string>() == Id(ReportSeed.ShipmentId))
            .ToList();

        scenario.Should().NotBeEmpty("the scenario shipment's lines are on the invoice");
        scenario.Should().OnlyContain(row => row["sourceParent"]!["tours"] is JsonArray && row["sourceParent"]!["tours"]!.AsArray().Count > 0,
            "the owning row keeps the selected member inside the collection");
        scenario.Should().OnlyContain(row => row["sourceParent"]!["tours"]!.AsArray().OfType<JsonObject>().All(tour => tour.Select(pair => pair.Key).SequenceEqual(new[] { "tourId" })),
            "each element carries the selected member, and only it");
        scenario.Should().OnlyContain(row => row["deliveringTour"] is JsonObject, "and the continued hop resolves the first tour from it");
    }
}
