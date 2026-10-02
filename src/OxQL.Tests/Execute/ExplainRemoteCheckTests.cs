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

    // ---- the cached tier -------------------------------------------------------------------------------

    [Fact]
    public async Task Remote_cached_asks_no_owner_and_says_what_it_therefore_does_not_know()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync(Continued, client, envelope: """ "remote": "cached" """);

        result.Valid.Should().BeTrue("what was not checked is never an error");
        client.ExplainCalls.Should().BeEmpty("the cached tier asks no owner");
        client.Reachability.Should().Be(0, "and reads no owner's health");
        result.Cache.Complete.Should().BeFalse();

        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Subject;
        note.Params!["reason"].Should().Be(RemoteExplain.Cached);
        note.Params["service"].Should().Be("crm");
        note.Message.Should().Contain("remote \"check\"");
        result.Notes.Should().NotContain(note => note.Code == Notes.ExplainLimit);

        var owner = result.OwnerOf("crm");
        owner["answered"].Should().BeNull("it was never asked");
        owner["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Cached);
        owner["calls"]!.GetValue<int>().Should().Be(0);
        result.Alias("co")["complete"]!.GetValue<bool>().Should().BeFalse();
        result.Alias("co")["type"].Should().BeNull("no owner answered what the continued stage reaches, and nothing stands in");
    }

    [Fact]
    public async Task Remote_cached_answers_what_a_check_kept_as_that_check_answered_it_and_never_asks_again()
    {
        var client = OwnerFleet.Client();
        var engine = Engine(client);

        var checkedAnswer = await ExplainAsync(Continued, engine: engine);

        client.ExplainCalls.Should().ContainSingle();

        var cached = await ExplainAsync(Continued, engine: engine, envelope: """ "remote": "cached" """);

        client.ExplainCalls.Should().ContainSingle("the kept answer is read, the owner is not asked");
        cached.Valid.Should().BeTrue();
        cached.Cache.Complete.Should().BeTrue();
        cached.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        cached.OwnerOf("crm").Should().Match<JsonObject>(owner => owner["answered"]!.GetValue<bool>() && owner["cached"]!.GetValue<bool>() && owner["calls"]!.GetValue<int>() == 0);
        cached.Alias("co").ToJsonString().Should().Be(checkedAnswer.Alias("co").ToJsonString());
        cached.Etag.Should().Be(checkedAnswer.Etag, "a complete cached answer is the answer a check gives, so a client that holds it is told so (304)");

        // Another query: nothing is kept for it, so the cached tier says so and still asks nobody.
        var other = await ExplainAsync(Continued.Replace("\"title\"", "\"name\""), engine: engine, envelope: """ "remote": "cached" """);

        client.ExplainCalls.Should().ContainSingle();
        other.Cache.Complete.Should().BeFalse();
        other.Etag.Should().NotBe(cached.Etag);
    }

    [Fact]
    public async Task An_answer_that_was_not_complete_is_not_kept_so_the_next_check_asks_the_owner_again()
    {
        // The owner could not check a part itself (it was short of time or calls, or an owner of its own
        // did not answer): what it says with more of either is another answer.
        var complete = false;
        var client = new FakeRemoteClient
        {
            Explains = (_, _) => new JsonObject
            {
                ["valid"] = true,
                ["errors"] = new JsonArray(),
                ["cache"] = new JsonObject { ["complete"] = complete },
            },
        };
        var engine = Engine(client);

        (await ExplainAsync(Continued, engine: engine)).Cache.Complete.Should().BeFalse();

        var cached = await ExplainAsync(Continued, engine: engine, envelope: """ "remote": "cached" """);

        cached.Cache.Complete.Should().BeFalse();
        cached.Notes.Should().Contain(note => note.Code == Notes.RemoteUnchecked && Equals(note.Params!["reason"], RemoteExplain.Cached), "nothing was kept of the answer that was cut short");
        client.ExplainCalls.Should().ContainSingle("the cached tier asks nobody");

        // The owner now answers in full: the next check asks it, and that answer is kept.
        complete = true;
        await ExplainAsync(Continued, engine: engine);
        client.ExplainCalls.Should().HaveCount(2, "the answer that was cut short did not stand in for 30 seconds");
        await ExplainAsync(Continued, engine: engine);
        client.ExplainCalls.Should().HaveCount(2, "a whole answer is kept");
        (await ExplainAsync(Continued, engine: engine, envelope: """ "remote": "cached" """)).Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
    }

    [Fact]
    public async Task An_answer_naming_another_schema_revision_of_an_owner_retires_what_was_kept_for_the_old_one()
    {
        var revision = "sha256:one";
        var client = new FakeRemoteClient
        {
            Explains = (_, _) => new JsonObject
            {
                ["valid"] = true,
                ["errors"] = new JsonArray(),
                ["revision"] = new JsonObject { ["schema"] = new JsonObject { ["crm"] = revision } },
            },
        };
        var engine = Engine(client);
        var other = Continued.Replace("\"title\"", "\"name\"");

        await ExplainAsync(Continued, engine: engine);
        await ExplainAsync(Continued, engine: engine);
        client.ExplainCalls.Should().ContainSingle("the answer is kept under the revision it named");

        // The owner's model changed; an answer to another body says so.
        revision = "sha256:two";
        await ExplainAsync(other, engine: engine);
        client.ExplainCalls.Should().HaveCount(2);

        (await ExplainAsync(Continued, engine: engine)).Revision.Schema["crm"].Should().Be("sha256:two", "what was kept for the old revision is not answered from any more");
        client.ExplainCalls.Should().HaveCount(3);
        (await ExplainAsync(Continued, engine: engine, envelope: """ "remote": "cached" """)).Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked, "the answer of the new revision is kept");
        client.ExplainCalls.Should().HaveCount(3);
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

    // ---- the owner side: one call carries a round's checks -------------------------------------------

    private static OxQLQueryService Service(Action<OxQLOptions>? configure = null)
    {
        var options = BindHost.Options(options =>
        {
            options.Compat.Enabled = false;
            configure?.Invoke(options);
        });
        var models = new StaticEntityModelProvider(ResolveModel.Model);

        return new OxQLQueryService(new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, options), new Scope(), options, models);
    }

    private static ExplainRequest Check(string query) => ExplainAnswer.Envelope($$"""{ "query": {{query}} }""");

    [Fact]
    public async Task An_explain_batch_answers_every_check_in_order_and_slim()
    {
        var batch = new ExplainBatchRequest
        {
            Checks =
            [
                Check("""{ "entityType": "rc.customer", "keyedBy": { "path": "code", "keys": ["A"] }, "pipeline": [{ "project": { "code": 1, "name": 1 } }] }"""),
                Check("""{ "entityType": "rc.customer", "pipeline": [{ "project": { "nothing": 1 } }] }"""),
            ],
            Budget = new ExplainBudget(1_000, 3),
        };

        var answers = (await Service().ExplainBatchAsync(batch)).Should().BeOfType<ExplainBatchOutcome.Success>().Subject.Response.Answers;

        answers.Should().HaveCount(2);

        var first = answers[0]!.AsObject();

        first["valid"]!.GetValue<bool>().Should().BeTrue("a check is an internal call: it may carry keyedBy");
        answers[1]!["valid"]!.GetValue<bool>().Should().BeFalse("a check that does not bind is an answer, as it is for one explain");
        answers[1]!["errors"]!.AsArray().Single()!["code"]!.GetValue<string>().Should().Be(Codes.UnknownPath);

        // What an origin reads is there; what it would compute itself and drop is not.
        first.Select(pair => pair.Key).Should().BeEquivalentTo(
            ["valid", "contract", "engine", "revision", "cache", "errors", "diagnostics", "notes", "stages", "aliases", "types", "rules", "owners", "catalog"]);
        first["diagnostics"]!.AsArray().Should().BeEmpty();
        first["notes"]!.AsArray().Should().BeEmpty("only the notes about the answer itself travel");
        first["rules"]!.AsObject().Count.Should().Be(0);
        first["types"]!.AsObject().Select(pair => pair.Key).Should().Contain("t:rc.customer", "the types of the owner's targets are what the origin asked for");
        first["stages"]!.AsArray().Single()!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["index", "kind", "status", "reads", "creates"]);

        // The same check asked alone on the public surface is the whole answer.
        var whole = (await Service().ExplainAsync(batch.Checks[1])).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        whole.Etag.Should().NotBeNull();
        whole.Entry.Should().NotBeNull();
        whole.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task An_explain_batch_is_bounded_before_anything_is_bound()
    {
        var check = Check("""{ "entityType": "rc.customer", "pipeline": [] }""");

        var tooMany = (await Service(options => options.Explain.MaxBatchChecks = 2).ExplainBatchAsync(new ExplainBatchRequest { Checks = [check, check, check] }))
            .Should().BeOfType<ExplainBatchOutcome.Refused>().Subject.Refusal;

        tooMany.Status.Should().Be(400);
        tooMany.Errors!.Single().Code.Should().Be(Codes.ExplainLimit);
        tooMany.Errors!.Single().Params!["limit"].Should().Be("checks");

        (await Service().ExplainBatchAsync(new ExplainBatchRequest())).Should().BeOfType<ExplainBatchOutcome.Refused>().Which.Refusal.Status.Should().Be(400);

        // A check past the explain bounds leaves its own entry empty; the others are answered.
        var stages = string.Join(",", Enumerable.Repeat("""{ "match": { "code": { "eq": "a" } } }""", 31));
        var answers = (await Service().ExplainBatchAsync(new ExplainBatchRequest { Checks = [Check($$"""{ "entityType": "rc.customer", "pipeline": [{{stages}}] }"""), check] }))
            .Should().BeOfType<ExplainBatchOutcome.Success>().Subject.Response.Answers;

        answers[0].Should().BeNull();
        answers[1].Should().NotBeNull();

        // A batch whose origin has no time left answers nothing and binds nothing.
        var spent = (await Service().ExplainBatchAsync(new ExplainBatchRequest { Checks = [check, check], Budget = new ExplainBudget(0, 5) }))
            .Should().BeOfType<ExplainBatchOutcome.Success>().Subject.Response.Answers;

        spent.Should().Equal([null, null]);
    }

    [Fact]
    public void An_explain_batch_travels_as_checks_and_budget_and_its_answer_as_answers()
    {
        var batch = new ExplainBatchRequest { Checks = [Check("""{ "entityType": "rc.customer", "pipeline": [] }""")], Budget = new ExplainBudget(750, 3) };
        var wire = JsonSerializer.SerializeToNode(batch, OxQLJson.Wire)!.AsObject();

        wire.Select(pair => pair.Key).Should().Equal("checks", "budget");
        wire["budget"]!.ToJsonString().Should().Be("""{"ms":750,"calls":3}""");
        wire["checks"]![0]!.AsObject().Select(pair => pair.Key).Should().Contain("query").And.NotContain("budget", "a check is an explain envelope; the budget is the batch's");

        var read = JsonSerializer.Deserialize<ExplainBatchRequest>(wire.ToJsonString(), OxQLJson.Wire)!;

        read.Budget.Should().Be(new ExplainBudget(750, 3));
        read.Checks.Single().Query.EntityType.Should().Be("rc.customer");

        JsonSerializer.Serialize(new ExplainBatchResponse { Answers = [new JsonObject { ["valid"] = true }, null] }, OxQLJson.Wire)
            .Should().Be("""{"answers":[{"valid":true},null]}""");
        ExplainBatchRequest.CallsOf(JsonNode.Parse("""{ "owners": [{ "service": "a", "remote": true, "calls": 2 }, { "service": "b", "remote": false, "calls": 9 }, { "service": "c", "remote": true, "via": "a", "calls": 1 }] }""")!.AsObject())
            .Should().Be(3, "the calls an answer cost are those of the remote owners it reached, the ones reached through others included");
    }

    private sealed class BatchingClient(FakeRemoteClient inner) : IRemoteQueryClient
    {
        public List<(string Service, ExplainBatchRequest Batch)> Batches { get; } = [];

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) => inner.BatchAsync(serviceKey, request, budget, cancellationToken);

        public bool IsConfigured(string serviceKey) => inner.IsConfigured(serviceKey);

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => inner.IsReachableAsync(serviceKey, cancellationToken);

        public Task<IReadOnlyList<JsonObject?>?> ExplainBatchAsync(string serviceKey, ExplainBatchRequest batch, TimeSpan budget, CancellationToken cancellationToken)
        {
            lock (Batches)
                Batches.Add((serviceKey, batch));

            return Task.FromResult<IReadOnlyList<JsonObject?>?>(batch.Checks.Select(check => inner.Explains!(serviceKey, check with { Budget = batch.Budget })).ToList());
        }
    }

    [Fact]
    public async Task One_round_asks_each_owner_once_whatever_it_asks_it()
    {
        var client = new BatchingClient(OwnerFleet.Client());
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), client);

        var result = await ExplainAsync(ThreeChecks.TrimEnd().TrimEnd(']') + """, { "resolve": { "path": "source.id", "as": "line" } }]""", engine: engine,
            envelope: """ "catalog": [{ "id": "x", "entity": "crm.company" }, { "id": "y", "entity": "crm.contact" }] """);

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Cache.Complete.Should().BeTrue();
        client.Batches.Select(call => $"{call.Service}:{call.Batch.Checks.Count}").Should().BeEquivalentTo(["crm:3", "transport:1", "crm:2"],
            "the three checks of crm are one call, transport's is another in the same round, and the two lookups ride together after them");
        client.Batches.Should().OnlyContain(call => call.Batch.Budget != null && call.Batch.Checks.All(check => check.Budget == null), "the budget is the batch's");
        result.OwnerOf("crm")["calls"]!.GetValue<int>().Should().Be(2);
        result.OwnerOf("transport")["calls"]!.GetValue<int>().Should().Be(1);
        result.Catalog.Should().OnlyContain(answer => answer["forwarded"]!.GetValue<bool>() && answer["type"] != null);
    }

    [Fact]
    public async Task What_an_ask_again_learned_a_target_lacks_is_not_asked_again_by_the_next_explain_and_is_still_said()
    {
        var client = TransportOwnerLacking("name");
        var engine = Engine(client);
        var pipeline = """
            [{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } },
             { "resolve": { "path": "owner.carrierId", "as": "c", "forTarget": "transport.shipment" } },
             { "project": { "line": 1, "owner.id": 1, "owner.number": 1, "owner.name": 1, "c": 1 } }]
            """;

        var cold = await ExplainAsync(pipeline, engine: engine);

        cold.Valid.Should().BeTrue(string.Join("; ", cold.Errors.Select(error => error.Message)));
        client.ExplainCalls.Should().HaveCount(2, "a cold explain asks, and asks again without the path the target lacks");

        // Another query that asks the same path: the forward cache holds no answer for it, the drop is known.
        var warm = await ExplainAsync(pipeline.Replace("\"owner.id\": 1, ", ""), engine: engine);

        warm.Valid.Should().BeTrue(string.Join("; ", warm.Errors.Select(error => error.Message)));
        client.ExplainCalls.Should().HaveCount(3, "the path is dropped before the first round, as a run drops it: one round");
        client.ExplainCalls[2].Request.Query.Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys.Should().NotContain("name");
        warm.OwnerOf("transport")["calls"]!.GetValue<int>().Should().Be(1);
        warm.Notes.Should().Contain(note => note.Code == Notes.SelectPathNotOnTarget && note.Path == "name", "what the target lacks is said whether it was learned now or before");
        warm.Alias("c")["type"]!.GetValue<string>().Should().Be("t:transport.carrier");
    }

    // ---- a branch of a union join whose owner does not answer ------------------------------------------

    [Fact]
    public async Task A_branch_of_a_union_join_whose_owner_does_not_answer_is_unanswered_and_the_alias_is_not_complete()
    {
        // The source line is a local shipment's or a remote (transport) shipment's; each has its own driver or carrier.
        const string UnionJoin = """
            [{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } },
             { "resolve": { "as": "who", "byTarget": { "rc.shipment": "owner.driverId", "transport.shipment": "owner.carrierId" } } }]
            """;
        var client = OwnerFleet.Client();

        client.Unreachable.Add("transport");

        var result = await ExplainAsync(UnionJoin, client, envelope: """ "include": ["shape", "notes"] """);

        result.Valid.Should().BeTrue("a branch nobody checked is not an error");
        result.Cache.Complete.Should().BeFalse();

        var branches = result.Alias("who")["branches"]!.AsArray().Select(branch => branch!.AsObject()).ToList();

        branches.Select(branch => (branch["anchorTarget"]!.GetValue<string>(), branch["status"]!.GetValue<string>()))
            .Should().Equal(("rc.shipment", "ok"), ("transport.shipment", "unanswered"));
        branches[1]["entities"]!.AsArray().Should().BeEmpty("what the branch reaches is its owner's to say");
        branches[1]["types"]!.AsArray().Should().BeEmpty();
        branches[1]["heldBy"]!.GetValue<string>().Should().Be("transport");
        branches[0]["entities"].Strings().Should().Equal("crm.contact");
        branches[0]["heldBy"]!.GetValue<string>().Should().Be("rc");

        result.Alias("who")["complete"]!.GetValue<bool>().Should().BeFalse("one branch's owner did not say what it reaches");
        result.Alias("who")["type"]!.GetValue<string>().Should().Be("t:crm.contact", "what the answered branch reaches is known all the same");
        result.Stage(1).Status.Should().Be("ok");
        result.Notes.Should().Contain(note => note.Code == Notes.RemoteUnchecked && Equals(note.Params!["service"], "transport") && Equals(note.Params["reason"], RemoteExplain.Unreachable));
    }

    // ---- a lookup of an entity only another owner reaches ----------------------------------------------

    [Fact]
    public async Task A_catalog_entry_of_a_service_this_host_does_not_know_goes_to_the_owner_that_reached_it()
    {
        var client = new FakeRemoteClient
        {
            Configured = ["crm", "transport"],
            Explains = (service, request) => request.Catalog.Count == 0
                // The check: crm binds the continued stage and reached hr for it.
                ? new JsonObject
                {
                    ["valid"] = true,
                    ["errors"] = new JsonArray(),
                    ["owners"] = new JsonArray(new JsonObject { ["service"] = "hr", ["remote"] = true, ["answered"] = true, ["calls"] = 1 }),
                }
                // The lookup: crm answers hr's entity as hr answered it.
                : new JsonObject
                {
                    ["valid"] = false,
                    ["errors"] = new JsonArray(),
                    ["types"] = new JsonObject { ["t:hr.person"] = new JsonObject { ["entity"] = "hr.person", ["service"] = "hr" } },
                    ["catalog"] = new JsonArray(new JsonObject { ["id"] = "forwarded", ["entity"] = "hr.person", ["type"] = "t:hr.person", ["forwarded"] = true }),
                    ["owners"] = new JsonArray(new JsonObject { ["service"] = "hr", ["remote"] = true, ["answered"] = true, ["calls"] = 1 }),
                },
        };
        var engine = Engine(client);
        const string Lookup = """ "include": ["shape", "notes"], "catalog": [{ "id": "p", "entity": "hr.person" }] """;

        // Nothing reached hr yet: there is no way to it.
        var unknown = await ExplainAsync("[]", engine: engine, envelope: Lookup);

        unknown.Catalog.Single()["error"]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownEntity);
        client.ExplainCalls.Should().BeEmpty();

        // In the explain that reaches hr through crm, the lookup goes the same way.
        var reached = await ExplainAsync(Continued, engine: engine, envelope: Lookup);
        var answer = reached.Catalog.Single();

        answer["error"].Should().BeNull(answer.ToJsonString());
        answer["id"]!.GetValue<string>().Should().Be("p");
        answer["forwarded"]!.GetValue<bool>().Should().BeTrue();
        answer["type"]!.GetValue<string>().Should().Be("t:hr.person");
        reached.Types["t:hr.person"]!["service"]!.GetValue<string>().Should().Be("hr");
        client.ExplainCalls.Select(call => call.Service).Should().Equal(["crm", "crm"], "this host knows no route to hr; crm does");
        client.ExplainCalls[1].Request.Catalog.Single()["entity"]!.GetValue<string>().Should().Be("hr.person");
        client.ExplainCalls[1].Request.Budget!.Calls.Should().BeGreaterThan(0, "the owner needs a call of its own to ask hr");
        reached.Owners.Select(owner => $"{owner["service"]!.GetValue<string>()}<{owner["via"]?.GetValue<string>()}").Should().Equal("crm<", "hr<crm");

        // Later the route is remembered: the lookup alone goes to crm too.
        var later = await ExplainAsync("[]", engine: engine, envelope: """ "include": ["shape", "notes"], "catalog": [{ "id": "q", "entity": "hr.person", "referencing": true }] """);

        later.Catalog.Single()["error"].Should().BeNull();
        client.ExplainCalls.Should().HaveCount(3);
        client.ExplainCalls[2].Service.Should().Be("crm");
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
    public async Task What_a_check_kept_is_one_users_of_one_organisation_through_the_explain_itself()
    {
        var client = OwnerFleet.Client();
        var engine = Engine(client);
        var userA = BindHost.Context(BindHost.Options()) with { UserId = "user-a" };
        var userB = BindHost.Context(BindHost.Options()) with { UserId = "user-b" };
        var otherOrganisation = BindHost.Context(BindHost.Options(), organisation: Guid.NewGuid()) with { UserId = "user-a" };
        const string CachedTier = """ "remote": "cached" """;

        (await ExplainAsync(Continued, engine: engine, context: userA)).Cache.Complete.Should().BeTrue();
        client.ExplainCalls.Should().ContainSingle();

        // Another user of the organisation, and the same user id in another organisation: nothing is kept for them.
        foreach (var stranger in new[] { userB, otherOrganisation })
        {
            var cached = await ExplainAsync(Continued, engine: engine, envelope: CachedTier, context: stranger);

            cached.Cache.Complete.Should().BeFalse("what one identity's check kept answers no other identity");
            cached.Alias("co")["type"].Should().BeNull("nothing of the other identity's owner answer is in this one");
            cached.Notes.Should().Contain(note => note.Code == Notes.RemoteUnchecked && Equals(note.Params!["reason"], RemoteExplain.Cached));
        }

        client.ExplainCalls.Should().ContainSingle("the cached tier asked nobody");
        (await ExplainAsync(Continued, engine: engine, envelope: CachedTier, context: userA)).Cache.Complete.Should().BeTrue("the one who checked is answered from what was kept");

        // Their own checks ask the owner themselves.
        await ExplainAsync(Continued, engine: engine, context: userB);
        client.ExplainCalls.Should().HaveCount(2);
        await ExplainAsync(Continued, engine: engine, context: otherOrganisation);
        client.ExplainCalls.Should().HaveCount(3);
        client.ExplainCalls.Select(call => call.Service).Should().OnlyContain(service => service == "crm");
    }

    [Fact]
    public void The_kept_answers_are_bounded_by_their_bytes_as_well_as_their_number()
    {
        using var cache = new ExplainForwardCache();
        var request = new ExplainRequest { Query = BindHost.Request("crm.contact", "[]") };
        // An owner's answer with its types written out: a quarter of a megabyte each.
        var large = new JsonObject { ["valid"] = true, ["pad"] = new string('x', 250_000) };

        for (var user = 0; user < 200; user++)
            cache.Set("crm", ExplainForwardCache.KeyOf(BindHost.Organisation, "user-" + user, "crm", request), (JsonObject)large.DeepClone());

        cache.Bytes.Should().BeLessThanOrEqualTo(ExplainForwardCache.MaxBytes, "200 answers of 250 KB are 50 MB; what is kept of them stays within the byte bound");
        cache.Count.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo((int)(ExplainForwardCache.MaxBytes / 250_000));

        using var small = new ExplainForwardCache();

        for (var user = 0; user < 3 * ExplainForwardCache.MaxEntries; user++)
            small.Set("crm", ExplainForwardCache.KeyOf(BindHost.Organisation, "user-" + user, "crm", request), new JsonObject { ["valid"] = true });

        small.Count.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(ExplainForwardCache.MaxEntries, "small answers are bounded by their number");
        small.Bytes.Should().BeLessThanOrEqualTo(ExplainForwardCache.MaxBytes);
    }

    [Fact]
    public void The_cache_key_holds_the_organisation_the_user_the_service_and_the_bodys_hash_and_not_the_budget()
    {
        var request = new ExplainRequest { Query = BindHost.Request("crm.contact", "[]") };
        var key = ExplainForwardCache.KeyOf(BindHost.Organisation, null, "crm", request);

        key.Should().StartWith(BindHost.Organisation.ToString("N") + "|").And.Contain("|crm|");
        ExplainForwardCache.KeyOf(BindHost.Organisation, "user-a", "crm", request).Should().NotBe(ExplainForwardCache.KeyOf(BindHost.Organisation, "user-b", "crm", request),
            "an owner may refuse one user what it answers another (RE-11)");
        ExplainForwardCache.KeyOf(Guid.NewGuid(), null, "crm", request).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, null, "hr", request).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, null, "crm", request with { ShapeDepth = 3 }).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, null, "crm", request with { Budget = new ExplainBudget(10, 1) }).Should().Be(key, "what is left of an origin's explain changes nothing a whole answer says");
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
        client.ExplainCalls[0].Request.Budget!.Calls.Should().Be(0, "an owner asked only for paths of its own entity asks no owner in turn, so it is given no call");
        client.ExplainCalls[0].Request.Budget!.Ms.Should().BeInRange(1, 1_500, "the owners' time, which is within the explain's wall time");

        // A query that carries a continued stage may reach a further owner: it gets what is left.
        var nesting = OwnerFleet.Client();

        await ExplainAsync(Continued, nesting, options => options.Explain.MaxOwnerCalls = 5);

        nesting.ExplainCalls[0].Request.Budget!.Calls.Should().Be(4, "the call itself is spent before it is sent");
        JsonSerializer.SerializeToNode(nesting.ExplainCalls[0].Request, OxQLJson.Wire)!["budget"]!["calls"]!.GetValue<int>().Should().Be(4, "it rides the body to the owner");
    }

    [Fact]
    public async Task The_calls_left_are_shared_among_the_owners_asked_at_once_that_may_ask_owners_of_their_own()
    {
        var client = OwnerFleet.Client();

        // crm continues a stage and so does transport: one round, two calls.
        await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } },
             { "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } },
             { "resolve": { "path": "owner.carrierId", "as": "c", "forTarget": "transport.shipment" } }]
            """, client, options => options.Explain.MaxOwnerCalls = 7);

        var budgets = client.ExplainCalls.GroupBy(call => call.Service).ToDictionary(group => group.Key, group => group.First().Request.Budget!.Calls);

        budgets.Keys.Should().BeEquivalentTo(["crm", "transport"]);
        budgets.Values.Sum().Should().Be(5, "two calls are spent on the round, and the five left are shared out: no more than that can be caused in all");
        budgets.Values.Should().OnlyContain(calls => calls >= 2);
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

        further.ExplainCalls.Should().ContainSingle().Which.Request.Budget!.Calls.Should().Be(0, "its own owner is asked for paths of its entity only, and asks no owner in turn");
        within["owners"]!.AsArray().Single()!["calls"]!.GetValue<int>().Should().Be(1);

        // The origin: its own call to the owner, and the owner's call counted as reached through it.
        var origin = new FakeRemoteClient { Explains = (_, request) => OwnerFleet.Answer(OwnerFleet.Engine(ResolveModel.Model, client: OwnerFleet.Client()), request with { Query = check.Query }) };
        var result = await ExplainAsync(Continued, origin);

        result.Owners.Select(each => $"{each["service"]!.GetValue<string>()}<{each["via"]?.GetValue<string>()}:{each["calls"]!.GetValue<int>()}").Should().Equal("crm<:1", "crm<crm:1");
        result.Cache.DependsOn.Should().Equal("crm");
    }

    [Fact]
    public async Task An_explain_past_its_wall_time_asks_no_further_owner_and_says_EXPLAIN_LIMIT()
    {
        var client = OwnerFleet.Client();
        client.Silent.Add("crm");

        // The wall time is below the owners' own budget, so it is what runs out. The check of the
        // resolve is the first round; the lookup of the transport entity comes after it.
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } }]
            """, client, options => { options.Explain.TimeoutMs = 150; options.Explain.RemoteTimeoutMs = 1_500; },
            envelope: """ "catalog": [{ "id": "t", "entity": "transport.shipment" }] """);

        result.Valid.Should().BeTrue();
        client.ExplainCalls.Should().ContainSingle("the lookup finds the time spent, and its owner is not asked");
        result.Notes.Where(note => note.Code == Notes.ExplainLimit).Select(note => (note.Params!["limit"], note.Params["service"])).Should().BeEquivalentTo([((object?)"time", (object?)"crm"), ("time", "transport")]);
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        result.Cache.Complete.Should().BeFalse();
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be("limit");
        result.Catalog.Single()["error"]!["params"]!["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Limit);
    }

    [Fact]
    public async Task The_owners_of_one_round_are_asked_at_once_and_a_silent_one_does_not_keep_the_other_from_answering()
    {
        var client = OwnerFleet.Client();
        client.Silent.Add("crm");

        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } },
             { "resolve": { "path": "source.id", "as": "line" } }]
            """, client, options => options.Explain.RemoteTimeoutMs = 150);

        client.ExplainCalls.Select(call => call.Service).Distinct().Should().BeEquivalentTo(["crm", "transport"], "both are asked in the one round");
        result.OwnerOf("transport")["answered"]!.GetValue<bool>().Should().BeTrue("the owner that answers is not held up by the one that does not");
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Timeout);
        result.Alias("line")["complete"]!.GetValue<bool>().Should().BeTrue();
        result.Alias("ct")["complete"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_owner_calls_share_the_remote_budget_and_a_silent_owner_times_out_into_a_note()
    {
        var client = new FakeRemoteClient();
        client.Silent.Add("crm");

        var result = await ExplainAsync("[]", client, options => options.Explain.RemoteTimeoutMs = 150,
            envelope: """ "catalog": [{ "id": "e", "entity": "crm.contact" }, { "id": "f", "entity": "crm.company" }] """);

        result.Catalog.Select(answer => answer["error"]!["params"]!["reason"]!.GetValue<string>()).Should().Equal(RemoteExplain.Timeout, RemoteExplain.Timeout);
        client.ExplainCalls.Should().ContainSingle("both lookups ride in the one call, which the first of them never answers");
        result.OwnerOf("crm")["calls"]!.GetValue<int>().Should().Be(1);
        result.Cache.Complete.Should().BeFalse();
    }
}
