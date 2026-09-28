using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// What explain says about where each step runs and what the rows look like (DESIGN §4.3): the
/// executor and phase of every join, the reference it follows, the owner and its queries with the keys
/// elided, the stages continued under a keyed stage with <c>forTarget</c> and <c>not_applicable</c>,
/// and <c>result.columns</c> grouped by the root each column lies under.
/// </summary>
public class ExplainStepsTests
{
    private const string Invoice = ResolveModel.Invoice;

    /// <summary>The ERP source reference: shipment and tour lines here, a transport line remote; every target an item.</summary>
    private const string Union = """{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }""";

    internal static async Task<ExplainResult> ExplainAsync(string pipeline, string entity = Invoice, bool strict = false, bool remote = true)
    {
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, remote ? new FakeRemoteClient() : null);
        var outcome = await engine.ExplainAsync(BindHost.Request(entity, pipeline) with { Strict = strict ? true : null }, BindHost.Context());

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    private static List<string?> Strings(JsonNode? array) => array!.AsArray().Select(node => node?.GetValue<string>()).ToList();

    // ---- executor and phase ---------------------------------------------------------------------------

    [Fact]
    public async Task An_inline_join_runs_before_the_page_when_a_later_stage_reads_it_and_after_the_page_otherwise()
    {
        var read = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }, { "match": { "c.name": { "eq": "Alice" } } }]""");
        var shown = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }]""");

        read.Steps[0].Should().Match<ExplainStep>(step => step.Executor == "inline" && step.Phase == "beforePage" && step.Owner == null);
        read.Steps[1].Executor.Should().BeNull("a match is no join");
        shown.Steps[0].Should().Match<ExplainStep>(step => step.Executor == "inline" && step.Phase == "afterPage");
        shown.Steps[0].Reference!["cases"]![0]!["targets"]![0]!["entity"]!.GetValue<string>().Should().Be("rc.customer");
    }

    [Fact]
    public async Task A_lookup_is_inline_with_its_phase()
    {
        var explain = await ExplainAsync("""[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices" } }]""", "rc.customer");

        explain.Steps[0].Should().Match<ExplainStep>(step => step.Executor == "inline" && step.Phase == "afterPage" && step.Owner == null && step.Reference == null);
    }

    [Fact]
    public async Task A_local_keyed_resolve_runs_after_the_page_at_this_host_with_its_grouped_owner_query_keys_elided()
    {
        var explain = await ExplainAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }]""");
        var step = explain.Steps.Single();

        step.Executor.Should().Be("keyed-local");
        step.Phase.Should().Be("afterPage");

        var owner = step.Owner!.AsObject();
        owner["service"]!.GetValue<string>().Should().Be("rc");
        owner["route"]!.ToJsonString().Should().Be("""{"apiName":"rc-api","apiVersion":null}""");
        owner["query"]!["entityType"]!.GetValue<string>().Should().Be("rc.shipment");
        Strings(owner["query"]!["keyedBy"]!["keys"]).Should().Equal(KeyedFetch.ElidedKey);
        owner["query"]!["pipeline"]!.AsArray().Last()!["page"]!["limit"]!.GetValue<string>().Should().Be(KeyedFetch.ElidedKey, "the page is keys times rows per key");

        var target = owner["targets"]!.AsArray().Should().ContainSingle().Subject!;
        target["target"]!.GetValue<string>().Should().Be("rc.shipment#billingLines");
        target["remote"]!.GetValue<bool>().Should().BeFalse();
        target["grouped"]!.GetValue<bool>().Should().BeTrue();
        step.Continued.Should().BeNull();
    }

    [Fact]
    public async Task A_remote_resolve_names_its_owner_and_the_plain_key_match_it_is_sent()
    {
        var step = (await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""")).Steps.Single();

        step.Executor.Should().Be("keyed-remote");
        step.Phase.Should().Be("afterPage");
        step.Owner!["service"]!.GetValue<string>().Should().Be("crm");
        step.Owner!["route"]!["apiName"]!.GetValue<string>().Should().Be("crm-api");

        Strings(step.Owner!["query"]!["pipeline"]![0]!["match"]!["id"]!["in"]).Should().Equal(KeyedFetch.ElidedKey);
        step.Owner!["query"]!["pipeline"]!.AsArray().Select(stage => stage!.AsObject().Single().Key).Should().Equal("match", "project", "page");
    }

    [Fact]
    public async Task A_stage_continued_at_the_owner_is_placed_there_and_listed_under_its_keyed_stage()
    {
        var explain = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }]
            """);

        explain.Valid.Should().BeTrue();
        explain.Steps[0].Continued!.Select(node => node.ToJsonString()).Should().Equal("""{"index":1,"forTarget":null}""");
        explain.Steps[1].Should().Match<ExplainStep>(step => step.Executor == "continued" && step.Phase == "owner");
        explain.Steps[1].Owner!["service"]!.GetValue<string>().Should().Be("crm");

        var sent = explain.Steps[0].Owner!["query"]!["pipeline"]!.AsArray();
        sent.Select(stage => stage!.AsObject().Single().Key).Should().Equal("match", "resolve", "project", "page");
        sent[1]!["resolve"]!["path"]!.GetValue<string>().Should().Be("companyId", "the owner is sent the stage rewritten onto its row");
        explain.Steps[0].Owner!["targets"]![0]!["continued"]!.ToJsonString().Should().Be("[1]");
    }

    [Fact]
    public async Task A_stage_for_one_target_rides_only_in_that_targets_query_and_reads_not_applicable_for_the_others()
    {
        var explain = await ExplainAsync($$"""
            [{{Union}},
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "as": "invoices" } }]
            """);

        var keyed = explain.Steps[0];
        keyed.Executor.Should().Be("keyed-remote", "one target of the union lives at another service");
        keyed.Continued!.Single().ToJsonString().Should().Be("""{"index":1,"forTarget":"rc.shipment"}""");

        var targets = keyed.Owner!["targets"]!.AsArray().ToDictionary(target => target!["target"]!.GetValue<string>());
        targets.Keys.Should().Equal("rc.shipment#billingLines", "rc.tour#billingLines", "transport.shipment#billingLines");
        targets["rc.shipment#billingLines"]!["continued"]!.ToJsonString().Should().Be("[1]");
        targets["rc.tour#billingLines"]!["notApplicable"]!.ToJsonString().Should().Be("[1]");
        targets["transport.shipment#billingLines"]!["notApplicable"]!.ToJsonString().Should().Be("[1]");
        keyed.Owner!["service"]!.GetValue<string>().Should().Be("transport", "the owner block leads with a remote target");

        var continued = explain.Steps[1];
        continued.Executor.Should().Be("continued");
        continued.Owner!["targets"]!.AsArray().Select(target => target!["target"]!.GetValue<string>()).Should().Equal("rc.shipment#billingLines");
    }

    [Fact]
    public async Task The_reference_lists_each_selected_case_with_its_condition_and_targets()
    {
        var reference = (await ExplainAsync($"[{Union}]")).Steps[0].Reference!;

        reference["cases"]![0]!["when"]!.ToJsonString().Should().Be("""{"path":"type","equals":["logistics"]}""");
        reference["cases"]![0]!["targets"]!.AsArray().Select(target => target!["item"]!.GetValue<string>()).Should().Equal("billingLines", "billingLines");
        reference["cases"]![1]!["targets"]![0]!["remote"]!.GetValue<bool>().Should().BeTrue();
        reference["keyAs"].Should().BeNull();
        reference["elements"].Should().BeNull();

        var converted = (await ExplainAsync("""[{ "resolve": { "path": "billing.referenceId", "as": "b" } }]""")).Steps[0].Reference!;
        converted["keyAs"]!.GetValue<string>().Should().Be("guid");
        converted["cases"]![0]!["keyAs"]!.GetValue<string>().Should().Be("guid");
    }

    [Fact]
    public async Task A_request_that_does_not_bind_has_no_placement_and_no_columns()
    {
        var explain = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "match": { "nothing": { "eq": 1 } } }]""");

        explain.Valid.Should().BeFalse();
        explain.Steps.Should().OnlyContain(step => step.Executor == null && step.Phase == null && step.Owner == null);
        explain.Result!.Columns.Should().BeEmpty();
        explain.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_step_serialises_with_every_placement_member()
    {
        var step = (await ExplainAsync("""[{ "match": { "number": { "eq": "x" } } }]""")).Steps[0];

        var json = JsonSerializer.SerializeToNode(step)!.AsObject();

        json.Select(pair => pair.Key).Should().Equal("index", "kind", "status", "executor", "phase", "owner", "creates", "shapeAfter");
    }

    // ---- result.columns ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_columns_are_the_visible_members_under_their_root_and_each_join_under_its_alias()
    {
        var columns = (await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "resolve": { "path": "contactId", "as": "r", "select": ["name", "email"] } },
             { "project": { "number": 1, "c": 1, "r": 1 } }]
            """)).Result!.Columns;

        columns.Select(column => (column.Path, column.Root)).Should().Equal([("id", ""), ("number", ""), ("c.id", "c"), ("c.name", "c"), ("r.name", "r"), ("r.email", "r")],
            "a local target's select carries its key; a remote one is what the caller wrote");
        columns.Single(column => column.Path == "number").Should().Match<ExplainColumn>(column => column.Kind == "string" && column.Stage == null && !column.Nullable);
        columns.Single(column => column.Path == "c.name").Should().Match<ExplainColumn>(column => column.Kind == "string" && column.Stage == 0 && column.Nullable);
        columns.Single(column => column.Path == "r.email").Should().Match<ExplainColumn>(column => column.Kind == "unknown" && column.Stage == 1, "the owner knows a remote member's kind");
    }

    [Fact]
    public async Task A_lookup_is_one_array_column_under_its_alias_and_an_unwound_element_its_members()
    {
        var lookup = (await ExplainAsync("""[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices" } }, { "project": { "name": 1, "invoices": 1 } }]""", "rc.customer")).Result!.Columns;

        lookup.Select(column => (column.Path, column.Kind, column.Root)).Should().Equal(("id", "guid", ""), ("name", "string", ""), ("invoices", "array", "invoices"));

        var unwound = (await ExplainAsync("""[{ "unwind": { "path": "lines", "as": "line", "includeIndex": "at" } }, { "project": { "number": 1, "line.customerId": 1, "at": 1 } }]""")).Result!;

        unwound.Paging.Should().Be("offset");
        unwound.Columns.Select(column => (column.Path, column.Root)).Should().Equal(("id", ""), ("number", ""), ("line.customerId", "line"), ("at", ""));
    }

    [Fact]
    public async Task An_owning_row_carries_the_entity_it_is_and_its_select_under_its_own_alias()
    {
        var columns = (await ExplainAsync("""[{ "resolve": { "path": "localSource.id", "as": "line", "target": "rc.shipment", "parentAs": "owner", "parentSelect": ["number"] } }, { "project": { "line": 1, "owner": 1 } }]""")).Result!.Columns;

        columns.Where(column => column.Root == "owner").Select(column => column.Path).Should().Equal("owner.entity", "owner.id", "owner.number");
        columns.Where(column => column.Root == "line").Select(column => column.Path).Should().Contain("line.id");
    }
}
