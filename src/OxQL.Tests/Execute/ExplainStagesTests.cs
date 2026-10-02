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
/// What explain says about where each stage runs and what the rows look like: the placement of every
/// join (executor, phase, host, owner), the alias table with the reference a join follows, its targets
/// and the stages continued under them (<c>forTarget</c>, <c>not_applicable</c>), the owners with the
/// queries a run sends them (keys elided, on request), the remote check of what the caller wrote under
/// a remote alias, and <c>result.columns</c> with when a row carries each key.
/// </summary>
public class ExplainStagesTests
{
    private const string Invoice = ResolveModel.Invoice;

    /// <summary>The ERP source reference: shipment and tour lines here, a transport line remote; every target an item.</summary>
    private const string Union = """{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }""";

    internal static async Task<ExplainResult> ExplainAsync(string pipeline, string entity = Invoice, bool strict = false, bool remote = true, FakeRemoteClient? client = null)
    {
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, remote ? client ?? new FakeRemoteClient() : null);
        var outcome = await engine.ExplainAsync((BindHost.Request(entity, pipeline) with { Strict = strict ? true : null }).Planned(), BindHost.Context());

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    // ---- placement ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_inline_join_runs_before_the_page_when_a_later_stage_reads_it_and_after_the_page_otherwise()
    {
        var read = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }, { "match": { "c.name": { "eq": "Alice" } } }]""");
        var shown = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }]""");

        read.Stage(0).Placement.Should().Match<ExplainPlacement>(placed => placed.Executor == "inline" && placed.Phase == "beforePage" && placed.Host == "rc" && placed.Owner == null);
        read.Stage(1).Placement.Should().BeNull("a match is no join");
        shown.Stage(0).Placement.Should().Match<ExplainPlacement>(placed => placed.Executor == "inline" && placed.Phase == "afterPage");
        shown.Alias("c")["reference"]!["cases"]![0]!["targets"]![0]!["entity"]!.GetValue<string>().Should().Be("rc.customer");
        shown.Alias("c")["reference"]!["path"]!.GetValue<string>().Should().Be("customerId");
        shown.Owners.Should().BeEmpty("an inline join has no owner");
    }

    [Fact]
    public async Task A_lookup_is_inline_with_its_phase_and_follows_no_reference()
    {
        var explain = await ExplainAsync("""[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices" } }]""", "rc.customer");

        explain.Stage(0).Placement.Should().Match<ExplainPlacement>(placed => placed.Executor == "inline" && placed.Phase == "afterPage" && placed.Owner == null);
        explain.Alias("invoices")["node"]!.GetValue<string>().Should().Be("array");
        explain.Alias("invoices").ContainsKey("reference").Should().BeFalse();
        explain.Stage(0).Creates.Should().Equal("invoices");
    }

    [Fact]
    public async Task A_local_keyed_resolve_runs_after_the_page_at_this_host_with_its_grouped_owner_query_keys_elided()
    {
        var explain = await ExplainAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }]""");
        var placement = explain.Stage(0).Placement!;

        placement.Executor.Should().Be("keyed-local");
        placement.Phase.Should().Be("afterPage");
        placement.Host.Should().Be("rc");

        var owner = explain.Owner(0)!;
        owner["service"]!.GetValue<string>().Should().Be("rc");
        owner["remote"]!.GetValue<bool>().Should().BeFalse();
        owner["answered"]!.GetValue<bool>().Should().BeTrue("this host answers its own keyed stages in process");
        owner["route"]!.ToJsonString().Should().Be("""{"apiName":"rc-api"}""", "the client names no version for it");

        var query = explain.Query(0);
        query["entityType"]!.GetValue<string>().Should().Be("rc.shipment");
        query["keyedBy"]!["keys"].Strings().Should().Equal(KeyedFetch.ElidedKey);
        query["pipeline"]!.AsArray().Last()!["page"]!["limit"]!.GetValue<string>().Should().Be(KeyedFetch.ElidedKey, "the page is keys times rows per key");

        var target = explain.Alias("r")["targets"]!.AsArray().Should().ContainSingle().Subject!;
        target["target"]!.GetValue<string>().Should().Be("rc.shipment#billingLines");
        target["remote"]!.GetValue<bool>().Should().BeFalse();
        target["grouped"]!.GetValue<bool>().Should().BeTrue();
        target["continued"]!.AsArray().Should().BeEmpty();
        target["type"]!.GetValue<string>().Should().Be("t:rc.shipment#billingLines");
    }

    [Fact]
    public async Task A_remote_resolve_names_its_owner_and_the_plain_key_match_it_is_sent()
    {
        var explain = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""");
        var placement = explain.Stage(0).Placement!;

        placement.Executor.Should().Be("keyed-remote");
        placement.Phase.Should().Be("afterPage");
        placement.Host.Should().Be("crm", "the owner's engine runs the stage");
        explain.Owner(0)!["service"]!.GetValue<string>().Should().Be("crm");
        explain.Owner(0)!["remote"]!.GetValue<bool>().Should().BeTrue();
        explain.Owner(0)!["route"]!["apiName"]!.GetValue<string>().Should().Be("crm-api");
        explain.Alias("r")["heldBy"]!.GetValue<string>().Should().Be("crm");

        explain.Query(0)["pipeline"]![0]!["match"]!["id"]!["in"].Strings().Should().Equal(KeyedFetch.ElidedKey);
        explain.Query(0)["pipeline"]!.AsArray().Select(stage => stage!.AsObject().Single().Key).Should().Equal("match", "project", "page");
    }

    [Fact]
    public async Task The_owner_queries_come_with_the_plan_only()
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var outcome = await engine.ExplainAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""), BindHost.Context());
        var plain = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        plain.Owners.Should().ContainSingle().Which.AsObject().ContainsKey("queries").Should().BeFalse("an owner query is part of the plan");
        plain.Plan.Should().BeNull();
        plain.Alias("r")["targets"]![0]!["owner"]!.GetValue<int>().Should().Be(0, "who is asked is said without the plan");
    }

    [Fact]
    public async Task A_stage_continued_at_the_owner_is_placed_there_and_listed_under_its_target()
    {
        var explain = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }]
            """);

        explain.Valid.Should().BeTrue();
        explain.Target("r", "crm.contact")["continued"]!.ToJsonString().Should().Be("[1]");
        explain.Stage(1).Placement.Should().Match<ExplainPlacement>(placed => placed.Executor == "continued" && placed.Phase == "owner" && placed.Host == "crm");
        explain.Owner(1)!["service"]!.GetValue<string>().Should().Be("crm");
        explain.Alias("co")["continuedFrom"]!.ToJsonString().Should().Be("""{"alias":"r"}""");
        explain.Alias("co")["heldBy"]!.GetValue<string>().Should().Be("crm");

        var sent = explain.Query(0)["pipeline"]!.AsArray();
        sent.Select(stage => stage!.AsObject().Single().Key).Should().Equal("match", "resolve", "project", "page");
        sent[1]!["resolve"]!["path"]!.GetValue<string>().Should().Be("companyId", "the owner is sent the stage rewritten onto its row");
        explain.Queries(0).Single()["continued"]!.ToJsonString().Should().Be("[1]");
    }

    [Fact]
    public async Task A_stage_for_one_target_rides_only_in_that_targets_query_and_reads_not_applicable_for_the_others()
    {
        var explain = await ExplainAsync($$"""
            [{{Union}},
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "as": "invoices" } }]
            """);

        explain.Stage(0).Placement!.Executor.Should().Be("keyed-remote", "one target of the union lives at another service");

        var targets = explain.Alias("line")["targets"]!.AsArray().ToDictionary(target => target!["target"]!.GetValue<string>());
        targets.Keys.Should().Equal("rc.shipment#billingLines", "rc.tour#billingLines", "transport.shipment#billingLines");
        targets["rc.shipment#billingLines"]!["continued"]!.ToJsonString().Should().Be("[1]");
        targets["rc.tour#billingLines"]!["notApplicable"]!.ToJsonString().Should().Be("[1]");
        targets["transport.shipment#billingLines"]!["notApplicable"]!.ToJsonString().Should().Be("[1]");
        explain.Owner(0)!["service"]!.GetValue<string>().Should().Be("transport", "the placement leads with a remote target's owner");
        explain.Owners.Select(owner => owner["service"]!.GetValue<string>()).Should().Equal("rc", "transport");

        // The owner refuses the lookup (rc.invoice declares no reference to the shipment): the stage is
        // listed under the one target it is for, and a stage its owner refused has no placement.
        explain.Valid.Should().BeFalse();
        explain.Stage(1).Status.Should().Be("error");
        explain.Stage(1).Placement.Should().BeNull("a stage its owner refused runs nowhere");
        explain.Stage(0).Status.Should().Be("ok", "the join that binds keeps its placement on a request that is not valid");
        explain.Owners[targets["rc.shipment#billingLines"]!["owner"]!.GetValue<int>()]["service"]!.GetValue<string>().Should().Be("rc", "the stage continues at the one target it is for");
        explain.Alias("invoices")["continuedFrom"]!.ToJsonString().Should().Be("""{"alias":"owner","target":"rc.shipment"}""");
    }

    [Fact]
    public async Task The_reference_lists_each_selected_case_with_its_condition_and_targets()
    {
        var reference = (await ExplainAsync($"[{Union}]")).Alias("line")["reference"]!;

        reference["cases"]![0]!["when"]!.ToJsonString().Should().Be("""{"path":"type","equals":["logistics"]}""");
        reference["cases"]![0]!["targets"]!.AsArray().Select(target => target!["item"]!.GetValue<string>()).Should().Equal("billingLines", "billingLines");
        reference["cases"]![1]!["targets"]![0]!["remote"]!.GetValue<bool>().Should().BeTrue();
        reference["keyAs"].Should().BeNull();
        reference["elements"].Should().BeNull();

        var converted = (await ExplainAsync("""[{ "resolve": { "path": "billing.referenceId", "as": "b" } }]""")).Alias("b")["reference"]!;
        converted["keyAs"]!.GetValue<string>().Should().Be("guid");
        converted["cases"]![0]!["keyAs"]!.GetValue<string>().Should().Be("guid");
    }

    [Fact]
    public async Task A_request_that_does_not_bind_has_no_placement_no_columns_and_no_plan_but_its_aliases_and_shapes()
    {
        var explain = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "match": { "nothing": { "eq": 1 } } }]""");

        explain.Valid.Should().BeFalse();
        explain.Stages.Should().OnlyContain(stage => stage.Placement == null);
        explain.Stages.Select(stage => stage.Status).Should().Equal("ok", "error");
        explain.Result!.Columns.Should().BeEmpty();
        explain.Notes.Should().BeEmpty();
        explain.Plan.Should().BeNull();
        explain.Alias("c")["node"]!.GetValue<string>().Should().Be("entity");
        explain.RootType(1, "c").Should().Be("t:rc.customer", "the part that binds is shaped");
    }

    [Fact]
    public async Task A_stage_serialises_with_every_member_and_no_placement_where_it_is_no_join()
    {
        var stage = (await ExplainAsync("""[{ "match": { "number": { "eq": "x" } } }]""")).Stage(0);

        var json = JsonSerializer.SerializeToNode(stage, OxQLJson.Wire)!.AsObject();

        json.Select(pair => pair.Key).Should().Equal(["index", "kind", "status", "reads", "creates", "shape"], "a stage that is no join has no placement");
        json["reads"]!.ToJsonString().Should().Be("""[{"path":"number","use":"match"}]""", "the read ledger: what the stage reads off the row, and no alias for a path of the entity row");
        json["shape"]!.AsObject().Select(pair => pair.Key).Should().Equal(["paging", "grouped", "unwound", "roots", "rules", "flags"], "no projection ran, nothing left the row, and the overrides were asked for with the types");
    }

    [Fact]
    public async Task A_remote_union_alias_holds_every_target_and_its_owning_row_every_owning_entity()
    {
        var explain = await ExplainAsync($"[{Union}]");

        explain.Stage(0).Creates.Should().Equal("line", "owner");
        explain.Alias("line")["node"]!.GetValue<string>().Should().Be("remote");
        explain.Alias("line")["entities"].Strings().Should().Equal("rc.shipment#billingLines", "rc.tour#billingLines", "transport.shipment#billingLines");
        explain.Alias("owner")["entities"].Strings().Should().Equal("rc.shipment", "rc.tour", "transport.shipment");
        explain.Alias("owner")["parentOf"]!.GetValue<string>().Should().Be("line");
        explain.Alias("line")["stage"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task An_alias_says_the_stage_whose_projection_dropped_it_and_whether_a_lookup_may_start_at_it()
    {
        var explain = await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "unwind": { "path": "lines", "as": "line", "includeIndex": "at" } },
             { "project": { "number": 1, "line": 1 } }]
            """);

        explain.Alias("c")["droppedAt"]!.GetValue<int>().Should().Be(2);
        explain.Alias("at")["droppedAt"]!.GetValue<int>().Should().Be(2);
        explain.Alias("line").ContainsKey("droppedAt").Should().BeFalse();
        explain.Alias("c")["lookupOn"]!.GetValue<bool>().Should().BeTrue("a resolved row is an entity row");
        explain.Alias("line")["lookupOn"]!.GetValue<bool>().Should().BeFalse("an element is no entity row");
        explain.Alias("at")["kind"]!.GetValue<string>().Should().Be("int");
        explain.ShapeAt(2).Roots.Select(pair => pair.Key).Should().Equal("", "line");
    }

    [Fact]
    public async Task Each_join_that_may_lose_data_says_its_outcomes()
    {
        var explain = await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "resolve": { "path": "contactId", "as": "r", "select": ["name"], "onMissing": "report" } }]
            """);

        explain.Alias("c")["outcome"]!.ToJsonString().Should().Be("""{"values":["not_found"]}""");
        explain.Alias("c")["stage"]!.GetValue<int>().Should().Be(0);
        explain.Alias("r")["outcome"]!.ToJsonString().Should().Be("""{"values":["ambiguous","not_found","invalid_key","owner_unanswered"]}""");
        JsonSerializer.SerializeToNode(explain.Result, OxQLJson.Wire)!.AsObject().Select(pair => pair.Key).Should().Equal(["paging", "columns"], "a join's outcomes are its alias's, said once");
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

        var error = explain.Errors.Single(error => error.Path == "r.statuz");
        var owner = (IReadOnlyDictionary<string, object?>)error.Params!["owner"]!;
        owner["service"].Should().Be("crm");
        owner["target"].Should().Be("crm.contact");
        owner["path"].Should().Be("statuz");
        error.Params["reason"].Should().Be(PathReasons.NoTarget);
        error.Params["alias"].Should().Be("r");
        explain.Stage(1).Status.Should().Be("error");

        client.ExplainCalls.Should().ContainSingle().Which.Request.Query.Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys
            .Should().Contain(["name", "statuz"], "the projected path is asked of the owner beside the select");

        (await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "project": { "r.name": 1 } }]""", client: Owner("statuz"))).Valid.Should().BeTrue();

        var selected = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name", "nope"] } }]""", client: Owner("nope"));
        selected.Errors.Should().ContainSingle().Which.Should().Match<QueryValidationError>(error => error.Stage == 0 && error.Path == "r.nope", "a select path is the resolve stage's");
    }

    [Fact]
    public async Task On_a_union_a_path_some_target_has_is_dropped_for_the_others_and_one_no_target_has_is_an_error()
    {
        var dropped = await ExplainAsync("""[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }, { "project": { "line": 1, "owner.id": 1, "owner.number": 1, "owner.name": 1 } }]""", client: Owner("name"));

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
        refused.Stage(1).Status.Should().Be("error");

        var client = new FakeRemoteClient();
        var valid = await ExplainAsync("""
            [{ "resolve": { "path": "customerIds", "as": "r", "elements": "first" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "first": true, "as": "invoice" } }]
            """, client: client);

        valid.Valid.Should().BeTrue(string.Join("; ", valid.Errors.Select(each => each.Message)));
        valid.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked, "this host always answers its own check");
        client.ExplainCalls.Should().BeEmpty("nothing is forwarded for a local owner");
        valid.Alias("invoice")["entities"].Strings().Should().Equal("rc.invoice");
        valid.Alias("invoice")["type"]!.GetValue<string>().Should().Be("t:rc.invoice", "this host's own answer says what the continued alias holds");
        valid.Alias("invoice")["complete"]!.GetValue<bool>().Should().BeTrue();
        valid.Cache.Complete.Should().BeTrue();
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
        result.Cache.Complete.Should().BeFalse("an owner did not answer");
        result.OwnerOf("crm")["answered"]!.GetValue<bool>().Should().BeFalse();
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be("timeout");
    }

    [Fact]
    public async Task A_call_the_check_at_this_host_had_cut_spent_the_budget_whatever_the_clock_reads()
    {
        // The timer that cuts a call runs on a coarser clock than the one the budget is read from, and
        // may fire before that one has reached the budget: here the call is cut at once, with nearly
        // the whole budget still on the clock. The time was the nested explain's to spend, so the
        // explain it ran for asks no owner after it either.
        var client = new FakeRemoteClient();
        client.CutEarly.Add("crm");

        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, client);
        var outcome = await engine.ExplainAsync(BindHost.Request(Invoice, """
            [{ "resolve": { "path": "contactId", "as": "c", "select": ["name"] } },
             { "resolve": { "path": "customerIds", "as": "r", "elements": "first" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "first": true, "as": "invoice", "select": ["contactId"] } },
             { "resolve": { "path": "invoice.contactId", "as": "ic", "select": ["name"] } }]
            """), BindHost.Context(options));

        var result = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        client.ExplainCalls.Select(call => call.Service + ":" + call.Budget.TotalMilliseconds).Should().ContainSingle("the nested explain's cut call spent the budget of the explain it ran for");
        result.Cache.Complete.Should().BeFalse("an owner did not answer");
        result.OwnerOf("crm")["answered"]!.GetValue<bool>().Should().BeFalse();
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be("timeout");
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
             { "resolve": { "path": "invoice.number", "as": "ic" } }]
            """), BindHost.Context(options));

        var result = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        result.Valid.Should().BeFalse("an invoice's number declares no reference, which this host's own binding finds without any owner");
        result.Errors.Should().ContainSingle().Which.Stage.Should().Be(3);
    }

    [Fact]
    public async Task A_remote_target_whose_owner_does_not_answer_is_noted_unchecked_and_its_alias_is_not_complete()
    {
        var explain = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""");

        explain.Valid.Should().BeTrue();
        explain.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Which.Stage.Should().Be(0);
        explain.Alias("r")["complete"]!.GetValue<bool>().Should().BeFalse();
        explain.Alias("r")["type"].Should().BeNull("nothing stands in for an owner that did not answer");
        explain.RootType(0, "r").Should().BeNull();
        explain.Cache.Complete.Should().BeFalse();
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
            "kept whole, a local alias shows its hint with its key; a remote one what its owner is asked for, the hint");
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
    public async Task An_owning_row_carries_the_entity_it_is_and_its_key_and_display_under_its_own_alias()
    {
        var columns = (await ExplainAsync("""[{ "resolve": { "path": "localSource.id", "as": "line", "target": "rc.shipment", "parentAs": "owner" } }, { "project": { "line": 1, "owner": 1 } }]""")).Result!.Columns;

        columns.Where(column => column.Root == "owner").Select(column => column.Path).Should().Equal("owner.entity", "owner.id", "owner.number");
        columns.Where(column => column.Root == "line").Select(column => column.Path).Should().Contain("line.id");
    }

    [Fact]
    public async Task A_column_says_when_a_row_carries_its_key()
    {
        var columns = (await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "c", "as": "others", "select": ["number"] } },
             { "unwind": { "path": "lines", "as": "line", "includeIndex": "at" } },
             { "project": { "number": 1, "customerCode": 1, "slot": 1, "c": 1, "others": 1, "line.id": 1, "at": 1 } }]
            """)).Result!.Columns.ToDictionary(column => column.Path, column => column.Present);

        columns["id"].Should().Be(ExplainColumn.Always);
        columns["number"].Should().Be(ExplainColumn.Always, "a member every record stores");
        columns["customerCode"].Should().Be(ExplainColumn.IfStored, "a nullable member a record may not hold");
        columns["slot.name"].Should().Be(ExplainColumn.IfStored, "under an object that may be null");
        columns["slot.licence"].Should().Be(ExplainColumn.IfVariant, "only a driver slot has a licence");
        columns["slot.plate"].Should().Be(ExplainColumn.IfVariant);
        columns["c.name"].Should().Be(ExplainColumn.IfJoined, "absent on a row whose join found nothing");
        columns["others"].Should().Be(ExplainColumn.Always, "a lookup's array is empty, never absent");
        columns["line.id"].Should().Be(ExplainColumn.Always);
        columns["at"].Should().Be(ExplainColumn.Always);
    }
}
