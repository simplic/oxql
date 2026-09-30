using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
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

    internal static async Task<ExplainResult> ExplainAsync(string pipeline, string entity = Invoice, bool strict = false, bool remote = true, FakeRemoteClient? client = null)
    {
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, remote ? client ?? new FakeRemoteClient() : null);
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

    [Fact]
    public async Task A_remote_union_alias_creates_every_target_and_its_owning_row_every_owning_entity()
    {
        var creates = (await ExplainAsync($"[{Union}]")).Steps[0].Creates;

        creates.Select(created => (created.Alias, created.Node)).Should().Equal(("line", "remote"), ("owner", "remote"));
        creates[0].Entities.Should().Equal("rc.shipment#billingLines", "rc.tour#billingLines", "transport.shipment#billingLines");
        creates[1].Entities.Should().Equal("rc.shipment", "rc.tour", "transport.shipment");
    }

    // ---- the remote check of the paths the caller wrote under a remote alias -----------------------

    /// <summary>An owner whose internal explain refuses the named members at the check query's projection, as a real owner binds them.</summary>
    private static FakeRemoteClient Owner(params string[] lacking) => new()
    {
        Explains = (_, request) =>
        {
            var pipeline = request.Query.Pipeline;
            var at = pipeline.ToList().FindLastIndex(stage => stage.Project is not null);
            var errors = new JsonArray(pipeline[at].Project!.Fields.Keys.Where(lacking.Contains)
                .Select(path => (JsonNode)new JsonObject { ["code"] = Codes.UnknownPath, ["message"] = $"'{path}' is not a path.", ["stage"] = at, ["path"] = path }).ToArray());

            return new JsonObject { ["valid"] = errors.Count == 0, ["errors"] = errors, ["notes"] = new JsonArray() };
        },
    };

    [Fact]
    public async Task A_projected_or_selected_path_the_remote_target_lacks_is_an_error_where_the_caller_wrote_it()
    {
        var client = Owner("statuz", "nope");
        var explain = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name", "nope"] } },
             { "project": { "number": 1, "r.name": 1, "r.statuz": 1 } }]
            """, client: client);

        explain.Valid.Should().BeFalse();
        explain.Errors.Select(error => (error.Code, error.Stage, error.Path)).Should().Equal([(Codes.UnknownPath, (int?)1, "r.statuz")],
            "the projection narrows the select to what it keeps, so 'nope' is never sent");

        var owner = (IReadOnlyDictionary<string, object?>)explain.Errors.Single(error => error.Path == "r.statuz").Params!["owner"]!;
        owner["service"].Should().Be("crm");
        owner["target"].Should().Be("crm.contact");
        owner["path"].Should().Be("statuz");
        explain.Steps[1].Status.Should().Be("error");

        client.ExplainCalls.Should().ContainSingle().Which.Request.Query.Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys
            .Should().Contain(["name", "statuz"], "the projected path is asked of the owner beside the select");

        (await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "project": { "r.name": 1 } }]""", client: Owner("statuz"))).Valid.Should().BeTrue();

        var selected = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name", "nope"] } }]""", client: Owner("nope"));
        selected.Errors.Should().ContainSingle().Which.Should().Match<QueryValidationError>(error => error.Stage == 0 && error.Path == "r.nope", "a select path is the resolve stage's");
    }

    [Fact]
    public async Task On_a_union_a_path_some_target_has_is_dropped_for_the_others_and_one_no_target_has_is_an_error()
    {
        var dropped = await ExplainAsync("""[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner", "parentSelect": ["id", "number", "name"] } }]""", client: Owner("name"));

        dropped.Valid.Should().BeTrue(string.Join("; ", dropped.Errors.Select(error => error.Message)));
        dropped.Notes.Where(note => note.Code == Notes.SelectPathNotOnTarget).Select(note => (note.Path, (string)note.Params!["target"]!))
            .Should().Contain(("name", "transport.shipment#billingLines"), "the tour's owning row has a name");

        var none = await ExplainAsync($$"""[{{Union}}, { "project": { "line.statuz": 1, "owner": 1 } }]""", client: Owner("oxEl.statuz"));

        none.Valid.Should().BeFalse();
        none.Errors.Should().ContainSingle().Which.Should().Match<QueryValidationError>(error => error.Code == Codes.UnknownPath && error.Stage == 1 && error.Path == "line.statuz");
    }

    [Fact]
    public async Task The_stages_continued_under_a_local_keyed_alias_are_checked_at_this_host_as_its_SelfOwner_binds_them()
    {
        // The billing line is keyed at this host; a resolve under its owning row runs at this host's
        // own SelfOwner, which binds it with the shipment's model: 'number' is no reference there.
        var refused = await ExplainAsync("""
            [{ "resolve": { "path": "billingLineId", "as": "r", "parentAs": "s" } },
             { "resolve": { "path": "s.number", "as": "n" } }]
            """);

        refused.Valid.Should().BeFalse();
        var error = refused.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(Codes.ResolveNotDeclared);
        error.Stage.Should().Be(1, "the owner's error maps back to the caller's stage");
        error.Path.Should().Be("s.number");
        ((IReadOnlyDictionary<string, object?>)error.Params!["owner"]!)["service"].Should().BeNull("this host is the owner");
        refused.Steps[1].Status.Should().Be("error");

        var client = new FakeRemoteClient();
        var valid = await ExplainAsync("""
            [{ "resolve": { "path": "customerIds", "as": "r", "elements": "first" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "first": true, "as": "invoice" } }]
            """, client: client);

        valid.Valid.Should().BeTrue(string.Join("; ", valid.Errors.Select(each => each.Message)));
        valid.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked, "this host always answers its own check");
        client.ExplainCalls.Should().BeEmpty("nothing is forwarded for a local owner");
    }

    [Fact]
    public async Task The_check_at_this_host_asks_its_owners_only_within_what_is_left_of_the_budget()
    {
        var client = new FakeRemoteClient();
        client.Silent.Add("crm");

        var options = BindHost.Options(options => options.Explain.RemoteTimeoutMs = 100);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, client);

        // The silent owner of 'c' spends the budget; the stages continued under the local 'r' are
        // still bound at this host, and the owner the nested explain would ask for 'ic' is not asked.
        var outcome = await engine.ExplainAsync(BindHost.Request(Invoice, """
            [{ "resolve": { "path": "contactId", "as": "c", "select": ["name"] } },
             { "resolve": { "path": "customerIds", "as": "r", "elements": "first" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "first": true, "as": "invoice", "select": ["contactId"] } },
             { "resolve": { "path": "invoice.contactId", "as": "ic", "select": ["name"] } }]
            """), BindHost.Context(options));

        var result = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        client.ExplainCalls.Select(call => call.Service + ":" + call.Budget.TotalMilliseconds).Should().ContainSingle("the nested explain gets what is left of the budget, which the silent owner spent");
    }

    [Fact]
    public async Task The_check_at_this_host_still_binds_when_the_owners_spent_the_budget()
    {
        var client = new FakeRemoteClient();
        client.Silent.Add("crm");

        var options = BindHost.Options(options => options.Explain.RemoteTimeoutMs = 100);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, client);
        var outcome = await engine.ExplainAsync(BindHost.Request(Invoice, """
            [{ "resolve": { "path": "contactId", "as": "c", "select": ["name"] } },
             { "resolve": { "path": "customerIds", "as": "r", "elements": "first" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "first": true, "as": "invoice" } },
             { "resolve": { "path": "invoice.contactId", "as": "ic", "select": ["name"] } }]
            """), BindHost.Context(options));

        var result = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        result.Valid.Should().BeFalse("the lookup did not select 'contactId', which this host's own binding finds without any owner");
        result.Errors.Should().ContainSingle().Which.Stage.Should().Be(3);
    }

    [Fact]
    public async Task A_written_path_whose_owner_does_not_answer_is_noted_unchecked()
    {
        var explain = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""");

        explain.Valid.Should().BeTrue();
        explain.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Which.Stage.Should().Be(0);
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
