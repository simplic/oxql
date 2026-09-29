using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// What describe offers the studio to follow on the fleet (found by the frontend's acceptance run):
/// the member <c>unwind.flatten</c> follows for A1 is the collection's own recursion (<c>items</c>
/// under <c>items</c>), with every candidate listed; and a collection below a remote alias keeps its
/// depth and <c>followable.elements</c>, as it has at the owner, so a continued resolve can take
/// <c>elements</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ExplainDescribeFollowTests
{
    private static JsonObject Answer(ExplainResult result, string id) =>
        result.Describe.Select(node => node.AsObject()).Single(entry => entry["id"]!.GetValue<string>() == id);

    private static JsonObject ChildAt(JsonObject described, string path) =>
        described["children"]!.AsArray().Select(node => node!.AsObject()).Single(child => child["path"]!.GetValue<string>() == path);

    private static JsonObject Envelope(string step) => ExplainDescribePlanTests.Plan.Value["scenarios"]!.AsArray()
        .SelectMany(scenario => scenario!["steps"]!.AsArray())
        .Single(entry => entry!["id"]!.GetValue<string>() == step)!["envelope"]!.AsObject();

    [Fact]
    public async Task D01_the_flatten_describe_advertises_for_a_ledgers_items_is_items_with_every_candidate_listed()
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>("""
            { "query": { "entityType": "ledger.transaction", "pipeline": [] },
              "describe": [{ "id": "roots", "at": 0, "prefix": "", "usage": "unwind" }] }
            """, OxQLJson.Wire)!;

        var answer = await new ExplainDescribePlanTests.InProcessFleet().ExplainAsync(request);
        var items = ChildAt(Answer(answer, "roots"), "items");

        items["flatten"]!.GetValue<string>().Should().Be("items", "A1 flattens the groups' own items");
        items["flattenMembers"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal("items", "assignedTransactionItems");
    }

    [Theory]
    [InlineData("A4.10", "lineShipment.tours.tourId", true)]
    [InlineData("A4.11", "deliveringTour.attachedResources.resource", false)]
    public async Task D02_a_collection_below_a_remote_alias_keeps_its_depth_and_offers_elements(string step, string path, bool reference)
    {
        var envelope = Envelope(step);
        var request = JsonSerializer.Deserialize<ExplainRequest>(envelope.ToJsonString(), OxQLJson.Wire)!;

        var answer = await new ExplainDescribePlanTests.InProcessFleet().ExplainAsync(request);
        var described = Answer(answer, "focus");
        var child = described["children"]!.AsArray().Select(node => node!.AsObject()).FirstOrDefault(entry => entry["path"]!.GetValue<string>() == path);

        child.Should().NotBeNull(described.ToJsonString());
        child!["underCollection"]!.GetValue<int>().Should().Be(1, child.ToJsonString());

        if (!reference)
            return;

        child["reference"]!["followable"]!["elements"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal("first", "all");
        child["reference"]!["followable"]!["one"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData("""["id", "sourceBillingLineReference.id"]""", false)]
    [InlineData("""["id", "sourceBillingLineReference.id", "sourceBillingLineReference.type"]""", true)]
    [InlineData("""["id", "sourceBillingLineReference"]""", true)]
    public async Task D03_a_reference_with_cases_under_a_join_needs_its_case_member_in_the_select_and_says_so(string select, bool valid)
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>($$"""
            { "query": { "entityType": "ledger.transaction", "pipeline": [
                { "unwind": { "path": "items", "as": "item" } },
                { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": {{select}} } },
                { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "shipment", "target": "transport.shipment" } } ] } }
            """, OxQLJson.Wire)!;

        var answer = await new ExplainDescribePlanTests.InProcessFleet().ExplainAsync(request);
        var what = string.Join("; ", answer.Errors.Select(error => $"{error.Code}@{error.Stage} {error.Path}: {error.Message}"));

        answer.Valid.Should().Be(valid, what);

        if (valid)
            return;

        var error = answer.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("UNKNOWN_PATH", "the reference is declared; the join did not fetch the member its case tests");
        error.Stage.Should().Be(2);
        error.Path.Should().Be("erpLine.sourceBillingLineReference.type");
        error.Message.Should().Contain("not in the select of 'erpLine'").And.Contain("add 'sourceBillingLineReference.type' to that select");
    }

    [Fact]
    public async Task D04_after_an_unwind_with_keep_path_false_describe_lists_the_alias_and_not_the_collection()
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>("""
            { "query": { "entityType": "ledger.transaction", "pipeline": [ { "unwind": { "path": "items", "as": "item", "keepPath": false } } ] },
              "describe": [{ "id": "after", "at": 1, "prefix": "", "usage": "project" }, { "id": "kept", "at": 0, "prefix": "", "usage": "project" }] }
            """, OxQLJson.Wire)!;

        var answer = await new ExplainDescribePlanTests.InProcessFleet().ExplainAsync(request);
        answer.Valid.Should().BeTrue(string.Join("; ", answer.Errors.Select(error => error.Message)));

        var after = Answer(answer, "after")["children"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()).ToList();
        after.Should().Contain("item").And.NotContain("items");
        Answer(answer, "kept")["children"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()).Should().Contain("items");
    }
}
