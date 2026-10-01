using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Report;
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
                           "parentAs": "sourceParent", "select": ["id"] } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first"{{(forTarget is null ? "" : $", \"forTarget\": \"{forTarget}\"")}},
                           "select": ["id", "number"] } },
            { "project": { "number": 1, "billingLine": 1, "sourceLine": 1, "sourceParent.id": 1, "deliveringTour": 1 } }
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
    public async Task A_projected_member_inside_a_collection_that_a_continued_hop_resolves_is_merged_at_the_owner_not_an_internal_error()
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
                               "parentAs": "sourceParent", "select": ["id"] } },
                { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first", "select": ["id", "number"] } },
                { "project": { "position": 1, "sourceLine": 1, "sourceParent.id": 1, "sourceParent.tours.tourId": 1, "deliveringTour": 1 } },
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
            "the owning row keeps the projected member inside the collection");
        scenario.Should().OnlyContain(row => row["sourceParent"]!["tours"]!.AsArray().OfType<JsonObject>().All(tour => tour.Select(pair => pair.Key).SequenceEqual(new[] { "tourId" })),
            "each element carries the projected member, and only it");
        scenario.Should().OnlyContain(row => row["deliveringTour"] is JsonObject, "and the continued hop resolves the first tour from it");
    }
    /// <summary>
    /// A resolve continued under the union's owning row for each of its targets: the shipment's first
    /// tour, and the tour's resource. The explain of the continued stages at their stages, with a
    /// describe of each new alias in the final shape.
    /// </summary>
    private static string ContinuedPerTarget() => $$"""
        {
          "query": {
            "entityType": "ledger.transaction",
            "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
            "pipeline": [
              { "match": { "id": { "eq": { "$var": "transactionId" } } } },
              { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
              { "match": { "item": { "is": "BillingLineTransactionItem" } } },
              { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": ["id", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
              { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent", "select": ["id"] } },
              { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first", "select": ["id", "number"] } },
              { "resolve": { "path": "sourceParent.resource.id", "as": "tourResource", "forTarget": "{{ReportSeed.Tour}}" } }
            ]
          }
        }
        """;

    /// <summary>The entities the alias a stage creates may hold.</summary>
    private static IReadOnlyList<string> Created(JsonNode explained, int stage, string alias)
    {
        explained["stages"]!.AsArray().Single(each => each!["index"]!.GetValue<int>() == stage)!["creates"]!.AsArray().Select(created => created!.GetValue<string>()).Should().Contain(alias);

        return explained["aliases"]![alias]!["entities"]!.AsArray().Select(entity => entity!.GetValue<string>()).ToList();
    }

    /// <summary>The types the alias points to: its own, or the targets of its union.</summary>
    private static IReadOnlyList<string> Typed(JsonNode explained, string alias)
    {
        var type = explained["aliases"]![alias]!["type"]!.GetValue<string>();

        explained["aliases"]![alias]!["complete"]!.GetValue<bool>().Should().BeTrue(alias);

        return type.StartsWith("u:", StringComparison.Ordinal)
            ? explained["types"]![type]!["of"]!.AsArray().Select(target => target!.GetValue<string>()[2..]).ToList()
            : [type[2..]];
    }

    [Fact]
    public async Task A_resolve_continued_under_a_union_owning_row_creates_the_target_of_the_reference_it_follows_for_its_forTarget()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(ContinuedPerTarget());

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        Created(explained.Body!, 5, "deliveringTour").Should().Equal([ReportSeed.Tour], "the shipment's tours.tourId references a tour, not the union's first target");
        Created(explained.Body!, 6, "tourResource").Should().Equal([ReportSeed.Employee, ReportSeed.Vehicle], "the tour's resource.id references an employee or a vehicle by its variant");
        Typed(explained.Body!, "deliveringTour").Should().Equal([ReportSeed.Tour], "the alias is typed as its owner bound it");
        Typed(explained.Body!, "tourResource").Should().Equal([ReportSeed.Employee, ReportSeed.Vehicle]);
    }

    [Fact]
    public async Task A_resolve_continued_under_a_nested_alias_creates_the_target_its_owner_binds()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(NestedChain(target: null, forTarget: ReportSeed.Shipment));

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        Created(explained.Body!, 3, "deliveringTour").Should().Equal([ReportSeed.Tour]);

        var chained = await ledger.ExplainHereAsync(ReportScenarios.Request("A4"));
        Created(chained.Body!, 5, "deliveringTour").Should().Equal([ReportSeed.Tour], "A4's delivering tour, continued under the line's shipment");
        Created(chained.Body!, 6, "tourVehicle").Should().Equal([ReportSeed.Vehicle], "a continued resolve with a target creates that target");
    }

    /// <summary>
    /// The fleet analogue of the studio's reference query: the shipment's delivering tour continued under
    /// the union's owning row, and the vehicle continued under that continued alias without a target
    /// (EXAMPLE-CASE's <c>shipmentVehicle</c>), next to the tour's own resource. The projection names a
    /// member of each target under the owning row (the shipment's number, the tour's), so the check query the owner is sent
    /// does not bind at its projection for either target, as in the studio's reference query.
    /// </summary>
    private static string ContinuedUnderContinued() => $$"""
        {
          "query": {
            "entityType": "ledger.transaction",
            "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
            "pipeline": [
              { "match": { "id": { "eq": { "$var": "transactionId" } } } },
              { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
              { "match": { "item": { "is": "BillingLineTransactionItem" } } },
              { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": ["id", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
              { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent", "select": ["id"] } },
              { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first", "select": ["id", "number", "resource.id"] } },
              { "resolve": { "path": "deliveringTour.resource.id", "as": "shipmentVehicle", "onMissing": "report" } },
              { "resolve": { "path": "sourceParent.resource.id", "as": "tourVehicle", "forTarget": "{{ReportSeed.Tour}}", "onMissing": "report" } },
              { "project": { "position": 1, "sourceLine": 1, "sourceParent.id": 1, "sourceParent.shipmentNumber": 1, "sourceParent.number": 1, "deliveringTour": 1, "shipmentVehicle": 1, "tourVehicle": 1 } }
            ]
          }
        }
        """;

    [Fact]
    public async Task A_resolve_continued_without_a_target_under_a_continued_alias_creates_the_target_its_owner_binds()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(ContinuedUnderContinued());

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        Created(explained.Body!, 5, "deliveringTour").Should().Equal([ReportSeed.Tour]);
        Created(explained.Body!, 6, "shipmentVehicle").Should().Equal([ReportSeed.Employee, ReportSeed.Vehicle], "the delivering tour's resource.id, bound at its owner, the owner answered");
        Created(explained.Body!, 7, "tourVehicle").Should().Equal([ReportSeed.Employee, ReportSeed.Vehicle]);
        Typed(explained.Body!, "shipmentVehicle").Should().Equal([ReportSeed.Employee, ReportSeed.Vehicle], "the alias is typed as its owner's owner answered: a union of both");

        var request = ReportScenarios.Request("A4");
        var pipeline = request["pipeline"]!.AsArray();
        pipeline.OfType<JsonObject>().Single(stage => stage["resolve"]?["as"]?.GetValue<string>() == "tourVehicle")["resolve"]!.AsObject().Remove("target");

        var chained = await ledger.ExplainHereAsync(request.ToJsonString());
        chained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(chained.Text);
        Created(chained.Body!, 6, "tourVehicle").Should().Equal([ReportSeed.Employee, ReportSeed.Vehicle], "A4's vehicle without its target, continued under the continued delivering tour");
    }
}
