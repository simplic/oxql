using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// Explain checks a semi-join's condition at its owner (explain ≡ execution). A condition on a member
/// of another service's rows is bound by that owner when the query runs, before the page; explain has
/// the owner bind the very query the run sends first, in the call the owner is asked in anyway. What
/// the owner refuses is the request's error where the caller wrote the condition; what no owner was
/// asked for is noted unchecked and never valid by silence.
/// </summary>
public class ExplainConditionCheckTests
{
    private const string Invoice = ResolveModel.Invoice;

    private const string Join = """{ "resolve": { "path": "contactId", "as": "r" } }""";

    private static MongoQueryEngine Engine(FakeRemoteClient client, Action<OxQLOptions>? configure = null, ExplainForwardCache? cache = null) =>
        new(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(configure), client, explainCache: cache);

    private static async Task<ExplainResult> ExplainAsync(string query, FakeRemoteClient client, string? envelope = null, MongoQueryEngine? engine = null, Action<OxQLOptions>? configure = null)
    {
        var body = $$"""{ "query": { "entityType": "{{Invoice}}", {{query}} }{{(envelope is null ? "" : ", " + envelope)}} }""";
        var outcome = await (engine ?? Engine(client, configure)).ExplainAsync(ExplainAnswer.Plain(body), BindHost.Context(BindHost.Options(configure)));

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    private static Task<ExplainResult> MatchAsync(string condition, FakeRemoteClient client, string? envelope = null, MongoQueryEngine? engine = null) =>
        ExplainAsync($$""" "pipeline": [{{Join}}, { "match": {{condition}} }] """, client, envelope, engine);

    /// <summary>Whether an owner query is a semi-join's (the condition) and not a keyed stage's check, which carries the check key.</summary>
    private static bool IsCondition(QueryRequest query) => !JsonSerializer.Serialize(query, OxQLJson.Wire).Contains(KeyedFetch.CheckKey, StringComparison.Ordinal);

    private static string Owner(QueryValidationError error) => JsonSerializer.SerializeToNode(error.Params!["owner"], OxQLJson.Wire)!.ToJsonString();

    // ---- the lab's case ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_operator_the_owner_refuses_for_the_members_kind_is_an_error_at_the_match_stage_with_the_owners_code()
    {
        var client = OwnerFleet.Client();
        var result = await MatchAsync("""{ "r.id": { "contains": "x" } }""", client);

        result.Valid.Should().BeFalse("the run is 422 RESOLVE_REFUSED with the owner's INVALID_OPERAND");
        result.Executable.Should().Be(Basis.Never);

        var error = result.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.InvalidOperand);
        error.Stage.Should().Be(1);
        error.Filter.Should().BeNull();
        error.Path.Should().Be("r.id", "the path as the caller wrote it");
        error.Basis.Should().Be(Basis.Model, "the owner's model speaks; an owner's own error keeps the basis of its code");
        error.Message.Should().Contain("contains").And.Contain("guid");
        Owner(error).Should().Be("""{"service":"crm","entity":"crm.contact","target":"crm.contact","stage":0,"path":"id"}""");
        result.Stage(1).Status.Should().Be("error");
        result.Stage(0).Status.Should().Be("ok");
        result.Cache.Complete.Should().BeTrue("the owner answered");
        result.OwnerOf("crm")["calls"]!.GetValue<int>().Should().Be(1, "the condition rides in the call the owner is asked in for its target");
    }

    [Fact]
    public async Task A_condition_the_owner_binds_leaves_the_answer_valid_and_executable()
    {
        var client = OwnerFleet.Client();
        var result = await MatchAsync("""{ "r.name": { "contains": "x" } }""", client);

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Executable.Should().Be(Basis.Yes);
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        result.OwnerOf("crm")["calls"]!.GetValue<int>().Should().Be(1);
    }

    /// <summary>What an owner's binder refuses of a pushed-down condition, each with the code the owner gives it.</summary>
    [Theory]
    [InlineData("""{ "r.id": { "contains": "x" } }""", Codes.InvalidOperand, "r.id")]            // the operator does not apply to the kind
    [InlineData("""{ "r.id": { "gt": "00000000-0000-0000-0000-000000000001" } }""", Codes.InvalidOperand, "r.id")]
    [InlineData("""{ "r.id": { "eq": "x" } }""", Codes.InvalidOperand, "r.id")]                  // the operand is not of the kind
    [InlineData("""{ "r.id": { "in": ["x"] } }""", Codes.InvalidOperand, "r.id")]
    [InlineData("""{ "r.nope": { "eq": "x" } }""", Codes.UnknownPath, "r.nope")]                 // no such member under the alias
    [InlineData("""{ "r.phones": { "eq": "x" } }""", Codes.NotFilterable, "r.phones")]           // not filterable at the owner
    [InlineData("""{ "r.phones.number.nope": { "eq": "x" } }""", Codes.UnknownPath, "r.phones.number.nope")]
    [InlineData("""{ "r.id": { "eq": "00000000-0000-0000-0000-000000000001", "options": { "caseSensitive": true } } }""", Codes.OptionNotApplicable, "r.id")]
    [InlineData("""{ "r.name": { "regex": "(" } }""", Codes.InvalidRegex, "r.name")]
    [InlineData("""{ "r.id": { "in": "x" } }""", Codes.OperandNotArray, "r.id")]                 // a set operator without an array
    [InlineData("""{ "r.name": { "is": "X" } }""", Codes.InvalidOperand, "r.name")]              // a variant test on a scalar
    public async Task What_the_owners_binder_refuses_of_the_condition_explain_refuses_with_the_same_code(string condition, string code, string path)
    {
        var client = OwnerFleet.Client();
        var result = await MatchAsync(condition, client);

        // What the owner answers when it is asked the run's query directly is what explain says.
        var relative = condition.Replace("\"r.", "\"", StringComparison.Ordinal);
        var direct = OwnerFleet.Answer(OwnerFleet.Engine(OwnerFleet.Crm), ExplainAnswer.Plain($$"""{ "query": { "entityType": "crm.contact", "pipeline": [{ "match": {{relative}} }] } }"""))!;

        direct["valid"]!.GetValue<bool>().Should().BeFalse();
        direct["errors"]!.AsArray().Select(error => error!["code"]!.GetValue<string>()).Should().Contain(code);

        result.Valid.Should().BeFalse();
        result.Errors.Select(error => error.Code).Should().Equal(direct["errors"]!.AsArray().Select(error => error!["code"]!.GetValue<string>()));

        var error = result.Errors.First(error => error.Code == code);

        error.Stage.Should().Be(1);
        error.Path.Should().Be(path);
        error.Params.Should().ContainKey("owner");
        error.Basis.Should().Be(Basis.OfCode[code]);
        result.Executable.Should().Be(Basis.Never);
    }

    [Fact]
    public async Task The_owners_params_travel_with_its_error()
    {
        var client = OwnerFleet.Client();
        var result = await MatchAsync("""{ "r.nope": { "eq": "x" } }""", client);
        var direct = OwnerFleet.Answer(OwnerFleet.Engine(OwnerFleet.Crm), ExplainAnswer.Plain("""{ "query": { "entityType": "crm.contact", "pipeline": [{ "match": { "nope": { "eq": "x" } } }] } }"""))!;
        var theirs = direct["errors"]![0]!["params"]!.AsObject();
        var error = result.Errors.Single();

        theirs.Count.Should().BePositive();

        foreach (var (key, value) in theirs)
            JsonSerializer.SerializeToNode(error.Params![key], OxQLJson.Wire)?.ToJsonString().Should().Be(value?.ToJsonString(), $"the owner's '{key}'");
    }

    // ---- the query checked is the query run --------------------------------------------------------------

    [Fact]
    public async Task The_owner_is_asked_the_query_the_run_sends_it_first_bound_and_never_run()
    {
        var client = OwnerFleet.Client();
        var engine = Engine(client);
        var body = $$"""{ "entityType": "{{Invoice}}", "pipeline": [{{Join}}, { "match": { "r.name": { "startsWith": "A", "options": { "caseSensitive": true } } } }] }""";

        (await engine.ExplainAsync(ExplainAnswer.Plain($$"""{ "query": {{body}} }"""), BindHost.Context())).Should().BeOfType<ExplainOutcome.Success>();

        client.Calls.Should().BeEmpty("explain never runs a batch at an owner");

        var checkedQueries = client.ExplainCalls.Select(call => JsonSerializer.Serialize(call.Request.Query, OxQLJson.Wire)).ToList();

        var ran = await engine.ExecuteAsync(JsonSerializer.Deserialize<QueryRequest>(body, OxQLJson.Wire)!, BindHost.Context());

        client.Calls.Should().NotBeEmpty(JsonSerializer.Serialize(ran, OxQLJson.Wire));

        var run = JsonSerializer.Serialize(client.Calls.First().Request.Queries[0], OxQLJson.Wire);

        run.Should().Contain("startsWith").And.Contain("\"caseSensitive\":true");
        checkedQueries.Should().Contain(run, "the condition's check is the semi-join's first owner query, byte for byte");

        var sent = client.ExplainCalls.Single(call => JsonSerializer.Serialize(call.Request.Query, OxQLJson.Wire) == run).Request;

        sent.Slim.Should().BeTrue();
        sent.Include.Should().Equal([ExplainRequest.IncludeNotes], "only the verdict is asked for: no shape, no types");
    }

    [Fact]
    public async Task Two_conditions_that_are_one_owner_query_are_asked_once_and_both_refused()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync($$""" "pipeline": [{{Join}}, { "match": { "r.id": { "contains": "x" } } }, { "match": { "r.id": { "contains": "x" } } }] """, client);

        result.Errors.Select(error => (error.Code, error.Stage)).Should().Equal((Codes.InvalidOperand, 1), (Codes.InvalidOperand, 2));
        client.ExplainCalls.Count(call => IsCondition(call.Request.Query)).Should().Be(1);
    }

    // ---- the request's filter ----------------------------------------------------------------------------

    [Fact]
    public async Task A_condition_of_the_requests_filter_is_refused_at_its_entry_by_id_and_without_a_stage()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync($$""" "pipeline": [{{Join}}], "filter": [ { "id": "ok", "where": { "r.name": { "eq": "x" } } }, { "id": "byContact", "where": { "r.id": { "contains": "x" } } }, { "where": { "r.email": { "gt": 1 } } } ] """, client);

        result.Valid.Should().BeFalse();
        result.Errors.Select(error => (error.Code, error.Stage, error.Filter, error.Path)).Should().Equal(
            (Codes.InvalidOperand, null, "byContact", "r.id"),
            (Codes.InvalidOperand, null, 2, "r.email"));
        result.Filter!.Select(entry => (entry.Id, entry.Status)).Should().Equal(("ok", "ok"), ("byContact", "error"), (null, "error"));
        result.Stages.Should().OnlyContain(stage => stage.Status == "ok", "a filter entry is no stage");
        result.Executable.Should().Be(Basis.Never);
    }

    // ---- not asked is not valid by silence ---------------------------------------------------------------

    [Fact]
    public async Task The_cached_tier_without_a_kept_answer_says_the_condition_is_unchecked_and_does_not_call_it_executable()
    {
        var client = OwnerFleet.Client();
        var result = await MatchAsync("""{ "r.id": { "contains": "x" } }""", client, """ "remote": "cached" """);

        client.ExplainCalls.Should().BeEmpty("the cached tier asks no owner");
        result.Valid.Should().BeTrue("what was not checked is never an error");
        result.Cache.Complete.Should().BeFalse();
        result.Executable.Should().Be(Basis.Unknown, "never yes for a condition no owner bound");

        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked && note.Path == "r.id").Subject;

        note.Stage.Should().Be(1);
        note.Basis.Should().Be(Basis.Owner);
        note.Params!["reason"].Should().Be(RemoteExplain.Cached);
        note.Params["service"].Should().Be("crm");
        note.Params["target"].Should().Be("crm.contact");
        note.Params["condition"].Should().Be("r.id");
        note.Message.Should().Contain("remote \"check\"");
    }

    [Fact]
    public async Task The_cached_tier_answers_the_owners_refusal_once_a_check_kept_it()
    {
        var client = OwnerFleet.Client();
        var engine = Engine(client, cache: new ExplainForwardCache());
        var check = await MatchAsync("""{ "r.id": { "contains": "x" } }""", client, engine: engine);
        var calls = client.ExplainCalls.Count;
        var cached = await MatchAsync("""{ "r.id": { "contains": "x" } }""", client, """ "remote": "cached" """, engine);

        client.ExplainCalls.Should().HaveCount(calls, "nothing is asked again");
        cached.Valid.Should().BeFalse();
        cached.Errors.Select(error => (error.Code, error.Stage, error.Path)).Should().Equal(check.Errors.Select(error => (error.Code, error.Stage, error.Path)));
        cached.Executable.Should().Be(Basis.Never);

        // Another operand is another owner query: not kept, so not known, and said so.
        var other = await MatchAsync("""{ "r.id": { "contains": "y" } }""", client, """ "remote": "cached" """, engine);

        other.Valid.Should().BeTrue();
        other.Executable.Should().Be(Basis.Unknown);
        other.Notes.Should().Contain(note => note.Code == Notes.RemoteUnchecked && note.Path == "r.id");
    }

    [Theory]
    [InlineData("none", RemoteExplain.Unsupported)]
    [InlineData("throw", RemoteExplain.Unreachable)]
    public async Task A_condition_whose_owner_does_not_answer_is_unchecked_and_the_verdict_unknown(string owner, string reason)
    {
        var client = new FakeRemoteClient();

        if (owner == "throw")
            client.Unreachable.Add("crm");

        var result = await MatchAsync("""{ "r.id": { "contains": "x" } }""", client);

        result.Valid.Should().BeTrue();
        result.Executable.Should().Be(Basis.Unknown);
        result.Notes.Should().Contain(note => note.Code == Notes.RemoteUnchecked && note.Path == "r.id" && note.Stage == 1 && Equals(note.Params!["reason"], reason));
    }

    [Fact]
    public async Task A_condition_the_batch_has_no_room_for_is_left_out_by_the_limit_and_never_called_valid()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync($$""" "pipeline": [{{Join}}, { "match": { "r.id": { "contains": "x" } } }] """, client, configure: options => options.Explain.MaxBatchChecks = 1);

        result.Valid.Should().BeTrue("a limit is never an error");
        result.Cache.Complete.Should().BeFalse();
        result.Executable.Should().Be(Basis.Unknown);
        result.Notes.Should().Contain(note => note.Code == Notes.ExplainLimit && Equals(note.Params!["limit"], "checks") && Equals(note.Params["service"], "crm"));
        result.OwnerOf("crm")["calls"]!.GetValue<int>().Should().Be(1, "never a second call in a round");
    }

    // ---- what an owner's answer may carry ----------------------------------------------------------------

    [Fact]
    public async Task An_error_at_a_stage_this_host_added_to_the_owner_query_is_the_conditions_too()
    {
        // The run is refused for it all the same: the target field this host projects, the page it asks.
        var client = new FakeRemoteClient
        {
            Explains = (_, request) => !IsCondition(request.Query)
                ? new JsonObject { ["valid"] = true, ["errors"] = new JsonArray() }
                : new JsonObject
                {
                    ["valid"] = false,
                    ["errors"] = new JsonArray(new JsonObject { ["code"] = Codes.PageSizeExceeded, ["message"] = "The page is larger than this host answers.", ["stage"] = 2, ["params"] = new JsonObject { ["max"] = 10 } }),
                },
        };

        var result = await MatchAsync("""{ "r.name": { "eq": "x" } }""", client);
        var error = result.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.PageSizeExceeded);
        error.Stage.Should().Be(1);
        error.Path.Should().Be("r.name");
        error.Basis.Should().Be(Basis.Limits);
        Owner(error).Should().Be("""{"service":"crm","entity":"crm.contact","target":"crm.contact","stage":2,"path":null}""");
        error.Params!["max"]!.ToString().Should().Be("10");
    }

    [Fact]
    public async Task An_owners_error_under_its_addon_bag_is_the_organisations_to_change_as_the_owner_said()
    {
        var client = new FakeRemoteClient
        {
            Explains = (_, request) => !IsCondition(request.Query)
                ? new JsonObject { ["valid"] = true, ["errors"] = new JsonArray() }
                : new JsonObject
                {
                    ["valid"] = false,
                    ["errors"] = new JsonArray(new JsonObject
                    {
                        ["code"] = Codes.NotFilterable, ["message"] = "'addon.weight' is unknown to the model; it can be projected, not filtered.", ["stage"] = 0, ["path"] = "addon.weight",
                        ["basis"] = Basis.Organisation, ["params"] = new JsonObject { ["addon"] = new JsonObject { ["key"] = "weight", ["state"] = "undefined" } },
                    }),
                },
        };

        var result = await MatchAsync("""{ "r.addon.weight": { "gt": 1 } }""", client);
        var error = result.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.NotFilterable);
        error.Path.Should().Be("r.addon.weight");
        error.Basis.Should().Be(Basis.Organisation);
        JsonSerializer.SerializeToNode(error.Params!["addon"], OxQLJson.Wire)!.ToJsonString().Should().Be("""{"key":"weight","state":"undefined"}""");
        result.Executable.Should().Be(Basis.Unknown, "a definition of the organisation changes it, no deployment");
    }

    [Fact]
    public async Task An_owner_that_answers_the_condition_incomplete_leaves_this_answer_incomplete()
    {
        var client = new FakeRemoteClient
        {
            Explains = (_, request) => !IsCondition(request.Query)
                ? new JsonObject { ["valid"] = true, ["errors"] = new JsonArray() }
                : new JsonObject { ["valid"] = true, ["errors"] = new JsonArray(), ["cache"] = new JsonObject { ["complete"] = false } },
        };

        var result = await MatchAsync("""{ "r.name": { "eq": "x" } }""", client);

        result.Valid.Should().BeTrue();
        result.Cache.Complete.Should().BeFalse();
        result.Executable.Should().Be(Basis.Unknown);
    }

    // ---- what the origin plans ---------------------------------------------------------------------------

    [Fact]
    public async Task The_page_of_the_checked_query_is_the_runs_sized_by_the_owners_facts()
    {
        var client = OwnerFleet.Client();

        client.Owners["crm"] = new RemoteOwnerInfo(null, 2, null, 40);

        await MatchAsync("""{ "r.name": { "eq": "x" } }""", client);

        var condition = client.ExplainCalls.Single(call => IsCondition(call.Request.Query)).Request.Query;

        condition.Pipeline[^1].Page!.Limit.Should().Be(40, "the smallest page the owner answers, as a run asks it");
        condition.Pipeline[^1].Page!.IncludeTotalCount.Should().BeTrue("the first page carries the count");
        condition.Pipeline[1].Project!.Fields.Keys.Should().Equal("id");
    }
}
