using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The remote check of an explain (DESIGN §4.3) and what bounds it (improvement plan §3.E protection):
/// the parts continued at an owner are bound by the owner's internal explain and its errors mapped back;
/// <c>REMOTE_UNCHECKED</c> where no owner answered; the 30-second forwarding cache; and the cost limits
/// of one explain — owner services, owner calls in all (transitive ones included, carried to the owner
/// as its budget) and the wall time — which leave a part out with <c>EXPLAIN_LIMIT</c>, never retried.
/// </summary>
public class ExplainRemoteCheckTests
{
    private const string Invoice = ResolveModel.Invoice;

    /// <summary>A stage continued at the remote owner of <c>r</c>.</summary>
    private const string Continued = """
        [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
         { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }]
        """;

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static MongoQueryEngine Engine(FakeRemoteClient? client = null, Action<OxQLOptions>? configure = null, ExplainForwardCache? cache = null) =>
        new(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(configure), client,
            explainCache: cache);

    private static async Task<ExplainResult> ExplainAsync(string pipeline, FakeRemoteClient? client = null, Action<OxQLOptions>? configure = null,
        string entity = Invoice, string? envelope = null, MongoQueryEngine? engine = null, RequestContext? context = null)
    {
        var body = $$"""{ "query": { "entityType": "{{entity}}", "pipeline": {{pipeline}} }{{(envelope is null ? "" : ", " + envelope)}} }""";
        var outcome = await (engine ?? Engine(client, configure)).ExplainAsync(ExplainAnswer.Envelope(body), context ?? BindHost.Context(BindHost.Options(configure)));

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    // ---- the remote check -----------------------------------------------------------------------------

    [Fact]
    public async Task A_continued_stage_the_owner_refuses_is_an_error_at_the_callers_stage_and_the_answer_is_not_valid()
    {
        var client = new FakeRemoteClient
        {
            Explains = (_, _) => new JsonObject
            {
                ["valid"] = false,
                ["errors"] = new JsonArray(
                    new JsonObject { ["code"] = "UNKNOWN_PATH", ["message"] = "'companyId' is not a path of crm.contact.", ["stage"] = 1, ["path"] = "companyId", ["params"] = new JsonObject { ["reason"] = "notAMember", ["entity"] = "crm.contact" } },
                    new JsonObject { ["code"] = "INVALID_OPERAND", ["message"] = "the key", ["stage"] = 0, ["path"] = "id" }),
            },
        };

        var result = await ExplainAsync(Continued, client, envelope: """ "include": ["shape", "notes", "plan"] """);

        result.Valid.Should().BeFalse();
        result.Plan.Should().BeNull();
        var error = result.Errors.Should().ContainSingle("an error at the owner query's own stages is this host's check key, not the caller's").Subject;
        error.Code.Should().Be("UNKNOWN_PATH");
        error.Stage.Should().Be(1);
        error.Path.Should().Be("r.companyId");
        JsonSerializer.SerializeToNode(error.Params!["owner"], OxQLJson.Wire)!.ToJsonString().Should().Be(
            """{"service":"crm","entity":"crm.contact","target":"crm.contact","stage":1,"path":"companyId"}""");
        error.Params["reason"]!.ToString().Should().Be("notAMember", "the owner's own params travel with its error");
        result.Stage(1).Status.Should().Be("error");

        var sent = client.ExplainCalls.Single();
        sent.Service.Should().Be("crm");
        sent.Request.Remote.Should().Be(ExplainRequest.RemoteCheck, "the owner checks what it continues further");
        JsonSerializer.Serialize(sent.Request.Query.Pipeline[0], OxQLJson.Wire).Should().Contain(KeyedFetch.CheckKey);
        sent.Request.Query.Pipeline[1].Resolve!.Path.Should().Be("companyId");
    }

    [Fact]
    public async Task An_owner_that_binds_the_continued_stage_leaves_the_answer_valid_and_passes_its_own_unchecked_parts_on()
    {
        var client = new FakeRemoteClient
        {
            Explains = (_, _) => new JsonObject
            {
                ["valid"] = true,
                ["errors"] = new JsonArray(),
                ["notes"] = new JsonArray(new JsonObject { ["code"] = Notes.RemoteUnchecked, ["message"] = "further", ["stage"] = 1, ["params"] = new JsonObject { ["service"] = "hr" } }),
            },
        };

        var result = await ExplainAsync(Continued, client);

        result.Valid.Should().BeTrue();
        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Subject;
        note.Stage.Should().Be(1);
        note.Params!["service"]!.ToString().Should().Contain("hr");
        result.Cache.Complete.Should().BeFalse("what an owner could not check is missing here too");
    }

    [Theory]
    [InlineData("none", "unsupported")]
    [InlineData("throw", "unreachable")]
    public async Task A_continued_part_no_owner_checked_is_a_REMOTE_UNCHECKED_note_and_no_error(string owner, string reason)
    {
        var client = new FakeRemoteClient();

        if (owner == "throw")
            client.Unreachable.Add("crm");

        var result = await ExplainAsync(Continued, client);

        result.Valid.Should().BeTrue();
        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Subject;
        note.Stage.Should().Be(1);
        note.Params!["reason"].Should().Be(reason);
        note.Params!["service"].Should().Be("crm");
        client.ExplainCalls.Should().HaveCount(1);
        result.OwnerOf("crm")["answered"]!.GetValue<bool>().Should().BeFalse();
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be(reason);
    }

    [Fact]
    public async Task Remote_cached_is_accepted_and_answered_as_check_until_the_cached_tier_exists()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync(Continued, client, envelope: """ "remote": "cached" """);

        result.Valid.Should().BeTrue();
        client.ExplainCalls.Should().ContainSingle("the owner is asked as under check");
        result.Cache.Complete.Should().BeTrue();
    }

    [Fact]
    public void The_describe_and_skip_of_the_former_envelope_are_no_members_of_it()
    {
        var describe = () => ExplainAnswer.Envelope("""{ "query": { "entityType": "rc.invoice", "pipeline": [] }, "describe": [] }""");

        describe.Should().Throw<JsonException>().WithMessage("*'describe' is not a member of an explain envelope*");
        ExplainAnswer.Envelope("""{ "query": { "entityType": "rc.invoice", "pipeline": [] }, "remote": "skip" }""").UnknownValues.Should().ContainSingle()
            .Which.Should().Be(new ExplainUnknownValue("remote", "skip"));
    }

    // ---- the owner side: the internal explain overload -----------------------------------------------

    private sealed class Scope : IOxQLScopeProvider
    {
        public ValueTask<Guid?> OrganisationAsync(Microsoft.AspNetCore.Http.HttpContext? httpContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Guid?>(BindHost.Organisation);
    }

    [Fact]
    public async Task Only_the_internal_explain_overload_binds_the_keyedBy_an_origin_forwards_for_its_check_and_takes_its_budget()
    {
        var options = BindHost.Options(configure => configure.Compat.Enabled = false);
        var models = new StaticEntityModelProvider(ResolveModel.Model);
        var service = new OxQLQueryService(new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, options), new Scope(), options, models);
        var request = ExplainAnswer.Envelope("""{ "query": { "entityType": "rc.customer", "keyedBy": { "path": "code", "keys": ["A"] }, "pipeline": [] }, "remote": "check" }""");

        var internalCall = (await service.ExplainAsync(request, internalCall: true)).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        var publicCall = (await service.ExplainAsync(request)).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        internalCall.Valid.Should().BeTrue(string.Join("; ", internalCall.Errors.Select(error => error.Code)));
        publicCall.Valid.Should().BeFalse();
        publicCall.Errors.Select(error => error.Code).Should().Equal(Codes.UnknownRequestMember);

        var budgeted = ExplainAnswer.Envelope("""{ "query": { "entityType": "rc.customer", "pipeline": [] }, "budget": { "ms": 500, "calls": 3 } }""");

        budgeted.Budget.Should().Be(new ExplainBudget(500, 3));
        (await service.ExplainAsync(budgeted, internalCall: true)).Should().BeOfType<ExplainOutcome.Success>();

        var refused = (await service.ExplainAsync(budgeted)).Should().BeOfType<ExplainOutcome.Refused>("what is left of an origin's explain rides only the internal route").Subject.Refusal;

        refused.Status.Should().Be(400);
        refused.Errors!.Single().Code.Should().Be(Codes.UnknownRequestMember);
    }

    // ---- the forwarding cache --------------------------------------------------------------------------

    [Fact]
    public async Task An_owners_answer_is_kept_30_seconds_per_organisation_owner_and_body()
    {
        var time = new ManualTime();
        var client = new FakeRemoteClient { Explains = (_, _) => new JsonObject { ["valid"] = true, ["errors"] = new JsonArray() } };
        var engine = Engine(client, cache: new ExplainForwardCache(time));

        (await ExplainAsync(Continued, engine: engine)).OwnerOf("crm").Should().Match<JsonObject>(owner => owner["calls"]!.GetValue<int>() == 1 && !owner["cached"]!.GetValue<bool>());

        var second = await ExplainAsync(Continued, engine: engine);

        client.ExplainCalls.Should().HaveCount(1, "the second explain is answered from the cache");
        second.OwnerOf("crm").Should().Match<JsonObject>(owner => owner["calls"]!.GetValue<int>() == 0 && owner["cached"]!.GetValue<bool>() && owner["answered"]!.GetValue<bool>());

        await ExplainAsync(Continued.Replace("\"title\"", "\"name\""), engine: engine);
        client.ExplainCalls.Should().HaveCount(2, "another body is another entry");

        time.Now += ExplainForwardCache.Ttl;
        await ExplainAsync(Continued, engine: engine);
        client.ExplainCalls.Should().HaveCount(3, "an entry lives 30 seconds");
    }

    [Fact]
    public async Task A_failed_owner_call_is_not_kept()
    {
        var client = new FakeRemoteClient();
        client.Unreachable.Add("crm");
        var engine = Engine(client);

        await ExplainAsync(Continued, engine: engine);
        await ExplainAsync(Continued, engine: engine);

        client.ExplainCalls.Should().HaveCount(2);
    }

    [Fact]
    public void The_cache_key_holds_the_organisation_the_user_the_service_and_the_bodys_hash_and_not_the_budget()
    {
        var request = new ExplainRequest { Query = BindHost.Request("crm.contact", "[]") };
        var key = ExplainForwardCache.KeyOf(BindHost.Organisation, "crm", request);

        key.Should().StartWith(BindHost.Organisation.ToString("N") + "|").And.Contain("|crm|");
        ExplainForwardCache.KeyOf(BindHost.Organisation, "user-a", "crm", request).Should().NotBe(ExplainForwardCache.KeyOf(BindHost.Organisation, "user-b", "crm", request),
            "an owner may refuse one user what it answers another (RE-11)");
        ExplainForwardCache.KeyOf(Guid.NewGuid(), "crm", request).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, "hr", request).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, "crm", request with { ShapeDepth = 3 }).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, "crm", request with { Budget = new ExplainBudget(10, 1) }).Should().Be(key, "what is left of an origin's explain changes nothing an owner answers");
    }

    // ---- explain quality (RE-25) ------------------------------------------------------------------

    [Fact]
    public async Task A_join_the_compiler_leaves_out_is_placed_nowhere()
    {
        var result = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "project": { "number": 1 } }]""");

        result.Stage(0).Placement.Should().BeNull("nothing reads 'c' and the row does not show it, so it is not joined");
        result.Notes!.Should().NotContain(note => note.Code == Notes.JoinBeforePage || note.Code == Notes.JoinAfterPage);
    }

    private sealed class FailingIndexes : IIndexSource
    {
        public Task<IReadOnlyList<MongoDB.Bson.BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken) =>
            throw new MongoDB.Driver.MongoException("listIndexes failed");
    }

    [Fact]
    public async Task Index_lists_that_cannot_be_read_are_a_note_not_a_failed_explain()
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), indexes: new FailingIndexes());
        var result = await ExplainAsync("[]", envelope: """ "include": ["notes", "indexes"] """, engine: engine);

        result.Valid.Should().BeTrue();
        result.Notes!.Should().Contain(note => note.Code == Notes.IndexAdvice && note.Message.Contains("could not be read"));
    }

    [Fact]
    public async Task An_inline_resolve_names_only_the_outcomes_it_can_have()
    {
        var result = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c", "onMissing": "report" } }, { "resolve": { "path": "customerCode", "as": "k", "onMissing": "report" } }]""");

        var policies = result.Notes!.Where(note => note.Code == Notes.MissingPolicy).ToDictionary(note => note.Stage!.Value);
        ((IEnumerable<string>)policies[0].Params!["dataLoss"]!).Should().Equal("not_found");
        ((IEnumerable<string>)policies[1].Params!["dataLoss"]!).Should().Equal("ambiguous", "not_found");
        policies[0].Message.Should().NotContain("owner");
        policies[1].Message.Should().Contain("RESOLVE_AMBIGUOUS");
    }

    [Fact]
    public async Task A_request_that_does_not_bind_carries_the_diagnostics_binding_produced()
    {
        var result = await ExplainAsync("""[{ "match": { "number": { "regex": "abc" } } }, { "match": { "nothing": { "eq": 1 } } }]""");

        result.Valid.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Codes.RegexUnanchored, "what binding said up to where it stopped is answered beside the errors");
    }

    // ---- the check asked again, as the run asks again ------------------------------------------------

    /// <summary>
    /// The owner of the union's remote target (transport) as a real one answers the check: a projection
    /// naming a path the shipment lacks (<c>name</c>) does not bind, so nothing is checked past it and its
    /// continued alias reaches nothing; without it the continued resolve binds and reaches the carrier.
    /// </summary>
    private static readonly MemberFlags CarrierId = new(Operators: 1, Sortable: true, Groupable: true, Unwindable: false, Projectable: true, Folds: false, UnderCollection: 0, Follow: 0);

    private static FakeRemoteClient TransportOwnerLacking(string lacking) => new()
    {
        Explains = (_, request) =>
        {
            var pipeline = request.Query.Pipeline.ToList();
            var projectAt = pipeline.FindLastIndex(stage => stage.Project is not null);
            var continuedAt = pipeline.FindIndex(stage => stage.Resolve?.As == "c");
            var binds = !pipeline[projectAt].Project!.Fields.ContainsKey(lacking);

            return new JsonObject
            {
                ["valid"] = binds,
                ["errors"] = binds ? new JsonArray() : new JsonArray(new JsonObject { ["code"] = Codes.UnknownPath, ["message"] = $"'{lacking}' is not a path.", ["stage"] = projectAt, ["path"] = lacking }),
                ["notes"] = new JsonArray(),
                ["stages"] = new JsonArray(new JsonObject { ["index"] = continuedAt, ["creates"] = new JsonArray("c") }),
                ["aliases"] = new JsonObject
                {
                    ["c"] = new JsonObject { ["stage"] = continuedAt, ["node"] = "remote", ["entities"] = binds ? new JsonArray("transport.carrier") : new JsonArray(), ["type"] = binds ? "t:transport.carrier" : null },
                },
                ["types"] = new JsonObject
                {
                    ["t:transport.carrier"] = new JsonObject { ["entity"] = "transport.carrier", ["members"] = new JsonArray(new JsonArray("id", "guid", 0, CarrierId.Id)) },
                },
                ["flagSets"] = new JsonObject { [CarrierId.Id] = CarrierId.ToJson() },
            };
        },
    };

    [Fact]
    public async Task An_owner_check_refused_at_a_path_its_union_target_lacks_is_asked_again_without_it_as_the_run_is_and_its_continued_alias_gets_its_target()
    {
        var client = TransportOwnerLacking("name");
        var explain = await ExplainAsync("""
            [{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } },
             { "resolve": { "path": "owner.carrierId", "as": "c", "forTarget": "transport.shipment" } },
             { "project": { "line": 1, "owner.id": 1, "owner.number": 1, "owner.name": 1, "c": 1 } }]
            """, client);

        explain.Valid.Should().BeTrue(string.Join("; ", explain.Errors.Select(error => error.Message)));
        explain.Alias("c")["entities"].Strings().Should().Equal(["transport.carrier"], "the check asked again binds the continued resolve at its owner, as the run's query asked again does");
        explain.Alias("c")["type"]!.GetValue<string>().Should().Be("t:transport.carrier");
        explain.OwnFlags("t:transport.carrier", "id").Operators().Should().Equal(["eq"], "the flags an owner's rows point to are sets of this answer too");
        explain.Notes!.Should().Contain(note => note.Code == Notes.SelectPathNotOnTarget && note.Path == "name", "what the target lacks is still said");

        client.ExplainCalls.Should().HaveCount(2);
        client.ExplainCalls[1].Request.Query.Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys.Should().NotContain("name").And.Contain("number");
        explain.OwnerOf("transport")["calls"]!.GetValue<int>().Should().Be(2, "the first ask and the ask-again are two rounds");
    }

    [Fact]
    public async Task A_public_explain_that_does_not_bind_holds_no_entity_for_a_continued_resolve()
    {
        var explain = await ExplainAsync("""
            [{ "resolve": { "path": "billing.referenceId", "as": "b" } },
             { "resolve": { "path": "b.driverId", "as": "d", "forTarget": "rc.shipment" } },
             { "project": { "b": 1, "d": 1, "nope": 1 } }]
            """);

        explain.Valid.Should().BeFalse();
        explain.Alias("d")["entities"]!.AsArray().Should().BeEmpty("a request that does not bind asks no owner for what a continued stage reaches");
        explain.Alias("d")["complete"]!.GetValue<bool>().Should().BeFalse();
    }

    // ---- the cost limits of one explain --------------------------------------------------------------

    /// <summary>Three remote resolves into one owner, each with a continued stage: three checks, one service, one round.</summary>
    private const string ThreeChecks = """
        [{ "resolve": { "path": "contactId", "as": "a", "select": ["name"] } },
         { "resolve": { "path": "contactId", "as": "b", "select": ["email"] } },
         { "resolve": { "path": "contactId", "as": "c", "select": ["name", "email"] } }]
        """;

    [Fact]
    public async Task The_checks_of_one_service_in_one_round_are_one_owner_call()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync(ThreeChecks, client, options => options.Explain.MaxOwnerCalls = 1);

        result.Valid.Should().BeTrue();
        client.ExplainCalls.Should().HaveCount(3);
        result.OwnerOf("crm")["calls"]!.GetValue<int>().Should().Be(1, "a call is a service and a round, which is what one batched request will carry");
        result.Notes.Should().NotContain(note => note.Code == Notes.ExplainLimit);
        result.Cache.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task An_owner_service_past_MaxOwnerServices_is_not_asked_and_the_answer_says_what_is_known()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } },
             { "resolve": { "path": "source.id", "as": "line" } }]
            """, client, options => options.Explain.MaxOwnerServices = 1);

        result.Valid.Should().BeTrue("a limit is never an error");
        client.ExplainCalls.Select(call => call.Service).Should().Equal(["crm"], "transport is the second service");

        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.ExplainLimit).Subject;
        note.Params!["limit"].Should().Be("ownerServices");
        note.Params["max"].Should().Be(1);
        note.Params["service"].Should().Be("transport");
        note.Stage.Should().Be(1);
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked, "the limit says it once");
        result.Cache.Complete.Should().BeFalse();
        result.Alias("ct")["complete"]!.GetValue<bool>().Should().BeTrue();
        result.Alias("line")["complete"]!.GetValue<bool>().Should().BeFalse();
        result.OwnerOf("transport")["answered"].Should().BeNull("it was never asked");
    }

    [Fact]
    public async Task Owner_calls_past_MaxOwnerCalls_are_not_made_and_nothing_is_retried()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } },
             { "resolve": { "path": "source.id", "as": "line" } }]
            """, client, options => options.Explain.MaxOwnerCalls = 1);

        client.ExplainCalls.Select(call => call.Service).Should().Equal("crm");
        result.Notes.Should().ContainSingle(note => note.Code == Notes.ExplainLimit).Which.Params!["limit"].Should().Be("ownerCalls");
        result.Cache.Complete.Should().BeFalse();

        var none = OwnerFleet.Client();
        var refusedAll = await ExplainAsync(Continued, none, options => options.Explain.MaxOwnerCalls = 0);

        none.ExplainCalls.Should().BeEmpty();
        refusedAll.Valid.Should().BeTrue();
        refusedAll.Alias("co")["complete"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task An_internal_explain_carries_what_is_left_of_the_origins_time_and_calls()
    {
        var client = OwnerFleet.Client();

        await ExplainAsync(ThreeChecks + "", client, options => options.Explain.MaxOwnerCalls = 5);

        client.ExplainCalls.Should().OnlyContain(call => call.Request.Budget != null);
        client.ExplainCalls[0].Request.Budget!.Calls.Should().Be(4, "the call itself is spent before it is sent");
        client.ExplainCalls[0].Request.Budget!.Ms.Should().BeInRange(1, 1_500, "the owners' time, which is within the explain's wall time");
        JsonSerializer.SerializeToNode(client.ExplainCalls[0].Request, OxQLJson.Wire)!["budget"]!["calls"]!.GetValue<int>().Should().Be(4, "it rides the body to the owner");
    }

    [Fact]
    public async Task An_owner_asks_its_own_owners_only_within_the_budget_it_was_given_and_the_origin_counts_them()
    {
        // rc is the origin; an owner (also rc's model here) answers a check that reaches crm in turn.
        var further = OwnerFleet.Client();
        var owner = OwnerFleet.Engine(ResolveModel.Model, client: further);
        var check = ExplainAnswer.Envelope("""
            { "query": { "entityType": "rc.shipment", "pipeline": [{ "resolve": { "path": "driverId", "as": "d", "select": ["name"] } }] }, "budget": { "ms": 1000, "calls": 0 } }
            """);

        var spent = OwnerFleet.Answer(owner, check)!;

        further.ExplainCalls.Should().BeEmpty("the origin has no call left, so the owner starts none");
        spent["notes"]!.AsArray().Should().ContainSingle(note => note!["code"]!.GetValue<string>() == Notes.ExplainLimit);
        spent["cache"]!["complete"]!.GetValue<bool>().Should().BeFalse();

        var within = OwnerFleet.Answer(owner, check with { Budget = new ExplainBudget(1_000, 2) })!;

        further.ExplainCalls.Should().ContainSingle().Which.Request.Budget!.Calls.Should().Be(1);
        within["owners"]!.AsArray().Single()!["calls"]!.GetValue<int>().Should().Be(1);

        // The origin: its own call to the owner, and the owner's call counted as reached through it.
        var origin = new FakeRemoteClient { Explains = (_, request) => OwnerFleet.Answer(OwnerFleet.Engine(ResolveModel.Model, client: OwnerFleet.Client()), request with { Query = check.Query }) };
        var result = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } }]""", origin);

        result.Owners.Select(each => $"{each["service"]!.GetValue<string>()}<{each["via"]?.GetValue<string>()}:{each["calls"]!.GetValue<int>()}").Should().Equal("crm<:1", "crm<crm:1");
        result.Cache.DependsOn.Should().Equal("crm");
    }

    [Fact]
    public async Task An_explain_past_its_wall_time_asks_no_further_owner_and_says_EXPLAIN_LIMIT()
    {
        var client = OwnerFleet.Client();
        client.Silent.Add("crm");

        // The wall time is below the owners' own budget, so it is what runs out.
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } },
             { "resolve": { "path": "source.id", "as": "line" } }]
            """, client, options => { options.Explain.TimeoutMs = 60; options.Explain.RemoteTimeoutMs = 1_500; });

        result.Valid.Should().BeTrue();
        client.ExplainCalls.Should().ContainSingle("the second owner finds the time spent");
        result.Notes.Where(note => note.Code == Notes.ExplainLimit).Select(note => (note.Params!["limit"], note.Params["service"])).Should().Equal(("time", "crm"), ("time", "transport"));
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        result.Cache.Complete.Should().BeFalse();
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be("limit");
    }

    [Fact]
    public async Task The_owner_calls_share_the_remote_budget_and_a_silent_owner_times_out_into_a_note()
    {
        var client = new FakeRemoteClient();
        client.Silent.Add("crm");

        var result = await ExplainAsync("[]", client, options => options.Explain.RemoteTimeoutMs = 50,
            envelope: """ "catalog": [{ "id": "e", "entity": "crm.contact" }, { "id": "f", "entity": "crm.company" }] """);

        result.Catalog.Select(answer => answer["error"]!["params"]!["reason"]!.GetValue<string>()).Should().Equal(RemoteExplain.Timeout, RemoteExplain.Timeout);
        client.ExplainCalls.Should().ContainSingle("the second lookup finds the budget spent");
        result.Cache.Complete.Should().BeFalse();
    }
}
