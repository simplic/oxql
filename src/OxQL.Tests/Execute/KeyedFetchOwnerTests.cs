using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// What the keyed fetch asks owners and keeps of their answers (DESIGN §3.5.2 steps 2–4, §3.5.5,
/// §3.5.6): the existence probe that tells <c>excluded</c> from <c>not_found</c>, an owner answer
/// with a next page as <c>RESOLVE_PARTIAL</c>, the request's key budget, the plan-hash cache key with
/// substituted variables, the negative TTL and its strict bypass, and the owner's own batch cap.
/// </summary>
public class KeyedFetchOwnerTests
{
    private const string Order = "probe.order";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Id3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Vehicle1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, FakeRemoteClient Client) Host(Action<OxQLOptions>? configure = null, TimeProvider? time = null)
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options(configure);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options, time));

        return (engine, runner, client);
    }

    private static BsonDocument Row(Guid id, string number, string? contact, Guid? vehicle = null) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["Number"] = number,
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
        ["ContactNumber"] = contact is null ? BsonNull.Value : new BsonString(contact),
        ["VehicleId"] = vehicle is null ? BsonNull.Value : new BsonBinaryData(vehicle.Value, GuidRepresentation.Standard),
    };

    private static async Task<QueryOutcome> RunAsync(MongoQueryEngine engine, QueryRequest request) =>
        await engine.ExecuteAsync(request, BindHost.Context());

    private static async Task<QueryResult> Success(MongoQueryEngine engine, string pipeline, string? variables = null, bool strict = false)
    {
        var outcome = await RunAsync(engine, BindHost.Request(Order, pipeline, variables) with { Strict = strict ? true : null });

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    /// <summary>
    /// The keys an owner query asks for: a plain query's key match, or a grouped one's <c>keyedBy</c>
    /// (a resolve onto <c>contactNumber</c>, which is not the contact's key, is grouped once it reads
    /// its outcomes under <c>onMissing</c> or strict).
    /// </summary>
    private static List<string> KeysOf(QueryRequest query) =>
        (query.KeyedBy is { } keyedBy ? keyedBy.Keys!.Value : query.Pipeline[0].Match!.Condition!.Value!.Value).EnumerateArray().Select(key => key.GetString()!).ToList();

    /// <summary>Whether an owner query carries the target's filter, i.e. is not the probe.</summary>
    private static bool Filtered(QueryRequest query) => query.Pipeline.Count(stage => stage.Match is not null) > (query.KeyedBy is null ? 1 : 0);

    private static JsonObject Diagnostic(QueryResult result, string code) =>
        JsonSerializer.SerializeToNode(result.Diagnostics!.Single(diagnostic => diagnostic.Code == code), OxQLJson.Wire)!.AsObject();

    private const string ReportActive = """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"], "filter": { "active": { "eq": true } }, "onMissing": "report" } }, { "page": { "limit": 10 } }]""";

    // ---- the existence probe ------------------------------------------------------------------

    [Fact]
    public async Task The_probe_tells_a_key_the_filter_left_out_from_a_missing_one()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1"), Row(Id2, "b", "c2"), Row(Id3, "c", "c3")];
        client.Script = (_, query, _) => Filtered(query)
            ? new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c1", ("name", "Alice")))
            : new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c2"));

        var result = await Success(engine, ReportActive);

        result.Items[0]!["contact"]!["name"]!.GetValue<string>().Should().Be("Alice");
        result.Items[1]!["contact"].Should().BeNull();
        result.Items[2]!["contact"].Should().BeNull();

        client.Calls.Should().HaveCount(2, "the filtered round, then the probe of the keys it did not return");
        var probe = client.Calls[1].Request.Queries.Should().ContainSingle().Subject;

        KeysOf(probe).Should().Equal("c2", "c3");
        Filtered(probe).Should().BeFalse("the probe asks without the target's filter");
        probe.Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys.Should().Equal(["number"], "the probe projects the keyed member alone");

        var missing = Diagnostic(result, Codes.ResolveMissing);
        missing["params"]!["count"]!.GetValue<int>().Should().Be(1, "c2 is excluded by the filter, only c3 is missing");
        missing["params"]!["rows"]![0]!["key"]!.GetValue<string>().Should().Be("c3");
        missing["params"]!["rows"]![0]!["outcome"]!.GetValue<string>().Should().Be("not_found");
    }

    [Fact]
    public async Task Without_a_filter_or_under_onMissing_null_no_probe_is_sent()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1")];

        await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "filter": { "active": { "eq": true } } } }]""");
        await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "onMissing": "report" } }]""");

        client.Calls.Should().HaveCount(2, "one filtered query each; neither needs to tell excluded from missing");
    }

    [Fact]
    public async Task An_excluded_answer_is_cached_as_excluded_and_not_as_missing()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c2")];
        client.Script = (_, query, _) => Filtered(query) ? new FakeRemoteClient.Answer.Rows() : new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c2"));

        await Success(engine, ReportActive);
        var again = await Success(engine, ReportActive);

        client.Calls.Should().HaveCount(2, "the second page reads the probed answer from the cache");
        again.Diagnostics.Should().BeNull("an excluded key is not missing, from the cache either");
    }

    // ---- partial owner answers ----------------------------------------------------------------

    [Fact]
    public async Task An_owner_answer_with_a_next_page_is_partial_and_its_open_keys_are_unanswered_not_missing()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1"), Row(Id2, "b", "c2")];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Page("next", FakeRemoteClient.Row("number", "c1", ("name", "Alice")));

        var result = await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"], "onMissing": "report" } }]""");

        result.Items[0]!["contact"]!["name"]!.GetValue<string>().Should().Be("Alice");
        result.Items[1]!["contact"].Should().BeNull();

        var partial = Diagnostic(result, Codes.ResolvePartial);
        partial["params"]!["alias"]!.GetValue<string>().Should().Be("contact");
        partial["params"]!["keys"]!.GetValue<int>().Should().Be(2);
        partial["params"]!["unanswered"]!.GetValue<int>().Should().Be(1);
        partial["params"]!["service"]!.GetValue<string>().Should().Be("crm");

        var missing = Diagnostic(result, Codes.ResolveMissing);
        missing["params"]!["rows"]!.AsArray().Should().ContainSingle().Which!["outcome"]!.GetValue<string>().Should().Be("owner_unanswered", "a cut answer never says not_found");

        await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"], "onMissing": "report" } }]""");
        client.Calls.Should().HaveCount(2, "a partial chunk is not cached");
    }

    [Fact]
    public async Task A_partial_owner_answer_refuses_under_strict()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1"), Row(Id2, "b", "c2")];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Page("next", FakeRemoteClient.Row("number", "c1", ("name", "Alice")));

        var outcome = await RunAsync(engine, BindHost.Request(Order, """[{ "resolve": { "path": "contactNumber", "as": "contact", "onMissing": "report" } }, { "page": { "limit": 10 } }]""") with { Strict = true });

        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;
        refusal.Status.Should().Be(422);
        refusal.Errors!.Select(error => error.Code).Should().Contain(Codes.ResolvePartial);
    }

    // ---- the key budget -----------------------------------------------------------------------

    [Fact]
    public async Task The_key_budget_is_one_per_request_across_the_keyed_stages()
    {
        var (engine, runner, client) = Host(options => options.Limits.MaxResolveKeys = 2);
        runner.PageRows = [Row(Id1, "a", "c1", Vehicle1), Row(Id2, "b", "c2", Vehicle1)];
        client.Script = (service, query, _) => service == "crm"
            ? new FakeRemoteClient.Answer.Rows(KeysOf(query).Select(key => FakeRemoteClient.Row("number", key, ("name", key))).ToArray())
            : new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString("D"), ("matchCode", "V-1")));

        var result = await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }, { "resolve": { "path": "vehicleId", "as": "vehicle", "select": ["matchCode"] } }, { "page": { "limit": 10 } }]""");

        result.Items.Select(item => item!["contact"]!["name"]!.GetValue<string>()).Should().Equal("c1", "c2");
        result.Items.Should().AllSatisfy(item => item!["vehicle"].Should().BeNull("the first stage spent the request's two keys"));
        client.Calls.Should().ContainSingle("the second stage asks no owner").Which.Service.Should().Be("crm");

        var partial = Diagnostic(result, Codes.ResolvePartial);
        partial["params"]!["alias"]!.GetValue<string>().Should().Be("vehicle");
        partial["params"]!["keys"]!.GetValue<int>().Should().Be(1);
        partial["params"]!["max"]!.GetValue<int>().Should().Be(2);
    }

    // ---- variables and the cache key ----------------------------------------------------------

    private const string ActiveVariable = """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"], "filter": { "active": { "eq": { "$var": "on" } } } } }, { "page": { "limit": 10 } }]""";

    [Fact]
    public async Task A_remote_filter_is_sent_with_its_variables_substituted_and_two_values_are_two_cache_entries()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1")];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c1", ("name", "Alice")));

        await Success(engine, ActiveVariable, """{ "on": true }""");
        await Success(engine, ActiveVariable, """{ "on": false }""");
        await Success(engine, ActiveVariable, """{ "on": true }""");

        client.Calls.Should().HaveCount(2, "two values inside one TTL are two plans; the third request repeats the first");

        var sent = client.Calls.Select(call => call.Request.Queries.Single()).ToList();
        sent.Select(query => JsonSerializer.Serialize(query, OxQLJson.Wire)).Should().AllSatisfy(json => json.Should().NotContain("$var", "an owner is never sent a variable"));
        sent.Should().AllSatisfy(query => query.Variables.Should().BeNull("an owner never receives variables"));
        sent.Select(query => query.Pipeline[1].Match!.Condition!.Value!.Value.GetBoolean()).Should().Equal(true, false);
    }

    [Fact]
    public async Task An_unbound_variable_in_a_remote_filter_is_refused_at_the_origin()
    {
        var refusal = await BindHost.RefusedAsync(BindHost.Probe, Order, ActiveVariable);

        refusal.Errors!.Should().ContainSingle().Which.Code.Should().Be(Codes.UnboundVariable);
        refusal.Errors![0].Stage.Should().Be(0);
    }

    [Fact]
    public void Substitution_leaves_a_value_without_variables_as_it_is_and_replaces_every_wrapper()
    {
        var coercer = new OperandCoercer(BindHost.Options(), new QueryVariables { Values = { ["on"] = JsonSerializer.SerializeToElement(true), ["codes"] = JsonSerializer.SerializeToElement(new[] { "a", "b" }) } });
        var errors = new List<QueryValidationError>();
        var plain = JsonDocument.Parse("""{ "active": { "eq": 1.50 } }""").RootElement;
        var wrapped = JsonDocument.Parse("""{ "and": [{ "active": { "eq": { "$var": "on" } } }, { "code": { "in": { "$var": "codes" } } }] }""").RootElement;

        coercer.SubstituteVariables(plain, 0, null, errors).GetRawText().Should().Be(plain.GetRawText(), "no wrapper, no rewrite: the render stays byte-identical");
        coercer.SubstituteVariables(wrapped, 0, null, errors).GetRawText().Should().Be("""{"and":[{"active":{"eq":true}},{"code":{"in":["a","b"]}}]}""");
        errors.Should().BeEmpty();

        coercer.SubstituteVariables(JsonDocument.Parse("""{ "x": { "eq": { "$var": "none" } } }""").RootElement, 3, "p", errors);
        errors.Should().ContainSingle().Which.Should().Match<QueryValidationError>(error => error.Code == Codes.UnboundVariable && error.Stage == 3);
    }

    // ---- the negative cache -------------------------------------------------------------------

    [Fact]
    public void A_negative_answer_lives_NegativeResolveTtlSeconds_and_a_strict_read_skips_it()
    {
        var time = new ManualTime();
        using var cache = new OwnerFetchCache(BindHost.Options(), time);
        var positive = new OwnerAnswer([new JsonObject { ["number"] = "c1" }]);

        cache.Set("found", positive);
        cache.Set("missing", new OwnerAnswer([]));
        cache.Set("excluded", new OwnerAnswer([], Excluded: true));

        cache.TryGet("missing", strict: false, out var negative).Should().BeTrue();
        negative!.IsNegative.Should().BeTrue();
        cache.TryGet("missing", strict: true, out _).Should().BeFalse("a strict request never reads a negative entry");
        cache.TryGet("found", strict: true, out _).Should().BeTrue();
        cache.TryGet("excluded", strict: true, out _).Should().BeTrue("an excluded key exists; it is not a negative entry");

        time.Advance(TimeSpan.FromSeconds(11));

        cache.TryGet("missing", strict: false, out _).Should().BeFalse("the negative TTL is 10 s");
        cache.TryGet("found", strict: false, out _).Should().BeTrue("the positive TTL is 60 s");

        time.Advance(TimeSpan.FromSeconds(50));

        cache.TryGet("found", strict: false, out _).Should().BeFalse();
    }

    [Fact]
    public void A_negative_TTL_of_zero_keeps_no_negative_answer()
    {
        using var cache = new OwnerFetchCache(BindHost.Options(options => options.Cache.NegativeResolveTtlSeconds = 0));

        cache.Set("missing", new OwnerAnswer([]));

        cache.TryGet("missing", strict: false, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_strict_request_asks_the_owner_again_for_a_key_a_moment_ago_not_found()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c9")];
        const string Pipeline = """[{ "resolve": { "path": "contactNumber", "as": "contact", "onMissing": "report" } }, { "page": { "limit": 10 } }]""";

        await Success(engine, Pipeline);
        await Success(engine, Pipeline);
        client.Calls.Should().ContainSingle("a lenient request reads the negative entry");

        await Success(engine, Pipeline, strict: true);
        client.Calls.Should().HaveCount(2, "a strict request bypasses the negative entry");
    }

    // ---- the owner's batch cap ----------------------------------------------------------------

    [Fact]
    public async Task An_owners_batch_goes_out_split_at_the_owners_own_cap()
    {
        var (engine, runner, client) = Host(options =>
        {
            options.Limits.ResolveKeyChunk = 1;
            options.Limits.MaxBatchQueries = 10;
        });
        runner.PageRows = [Row(Id1, "a", "c1"), Row(Id2, "b", "c2"), Row(Id3, "c", "c3")];
        client.Owners["crm"] = new RemoteOwnerInfo("2.1.0.0", 2, MaxBatchQueries: 2);
        client.Script = (_, query, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", KeysOf(query)[0], ("name", KeysOf(query)[0])));

        var result = await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }, { "page": { "limit": 10 } }]""");

        client.Calls.Select(call => call.Request.Queries.Count).Should().Equal([2, 1], "the owner takes two queries per batch, below this host's ten");
        result.Items.Select(item => item!["contact"]!["name"]!.GetValue<string>()).Should().Equal("c1", "c2", "c3");
    }

    [Fact]
    public void The_owner_facts_are_read_off_its_shallow_health()
    {
        var health = JsonNode.Parse("""{ "status": "healthy", "engine": { "version": "2.1.0.0", "contract": 2 }, "limits": { "maxBatchQueries": 5 } }""");

        RemoteOwnerInfo.FromShallowHealth(health).Should().Be(new RemoteOwnerInfo("2.1.0.0", 2, 5));
        RemoteOwnerInfo.FromShallowHealth(JsonNode.Parse("""{ "status": "healthy" }""")).Should().Be(new RemoteOwnerInfo(null, null, null));
        RemoteOwnerInfo.FromShallowHealth(null).Should().BeNull();
    }
}
