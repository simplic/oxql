using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Compile;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// Remote resolve and the semi-join (design §11) against a fake owner: the batch per service,
/// the merge under the alias, the cache, chunking and the key cap, the owner's refusal, the
/// owner's silence, and the ids substituted into the page and count filters.
/// </summary>
public class RemoteResolverTests
{
    private const string Order = "probe.order";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Id3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Vehicle1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, FakeRemoteClient Client) Host(Action<OxQLOptions>? configure = null)
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options(configure);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new ResolveCache(options));

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

    private static async Task<QueryResult> Success(MongoQueryEngine engine, string pipeline)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static async Task<Refusal> Refused(MongoQueryEngine engine, string pipeline)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), BindHost.Context());

        return outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;
    }

    private const string ResolveContact = """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }, { "page": { "limit": 10 } }]""";

    // ---- resolve --------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_rows_are_merged_under_the_alias_with_one_batch_per_service()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1"), Row(Id2, "b", "c2"), Row(Id3, "c", "c1"), Row(Guid.NewGuid(), "d", null)];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(
            FakeRemoteClient.Row("number", "c1", ("name", "Alice")),
            FakeRemoteClient.Row("number", "c2", ("name", "Bob")));

        var result = await Success(engine, ResolveContact);

        result.Items.Should().HaveCount(4);
        result.Items[0]!["contact"]!["name"]!.GetValue<string>().Should().Be("Alice");
        result.Items[1]!["contact"]!["name"]!.GetValue<string>().Should().Be("Bob");
        result.Items[2]!["contact"]!["name"]!.GetValue<string>().Should().Be("Alice", "the same key is resolved once and written to every row");
        result.Items[3]!["contact"].Should().BeNull("a row without a key resolves to null");
        result.Items[0]!["number"]!.GetValue<string>().Should().Be("a", "the row's own members stay");
        result.Diagnostics.Should().BeNull();

        var (service, request, budget) = client.Calls.Should().ContainSingle().Subject;

        service.Should().Be("crm", "the service key is the target's namespace");
        budget.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(2000), "the resolve timeout caps the budget");
        request.MaxTimeMs.Should().Be((int)budget.TotalMilliseconds, "the owner gets the remaining budget as its ceiling");

        var query = request.Queries.Should().ContainSingle().Subject;

        query.EntityType.Should().Be("crm.contact");
        query.Pipeline[0].Match!.Condition!.Path.Should().Be("number");
        query.Pipeline[0].Match!.Condition!.Op.Should().Be("in");
        query.Pipeline[0].Match!.Condition!.Value!.Value.EnumerateArray().Select(item => item.GetString()).Should().Equal("c1", "c2");
        query.Pipeline[1].Project!.Fields.Keys.Should().BeEquivalentTo(["name", "number"], "the target field is always selected so rows can be keyed");
        query.Pipeline[2].Page!.Limit.Should().Be(2);
    }

    [Fact]
    public async Task A_warm_cache_answers_the_next_page_without_a_call()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1")];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c1", ("name", "Alice")));

        await Success(engine, ResolveContact);
        var again = await Success(engine, ResolveContact);

        client.Calls.Should().ContainSingle("the second page hits the cache");
        again.Items[0]!["contact"]!["name"]!.GetValue<string>().Should().Be("Alice");

        // A different select is a different cache entry.
        await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name", "number"] } }]""");
        client.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Keys_are_chunked_and_capped_with_a_partial_diagnostic()
    {
        var (engine, runner, client) = Host(options =>
        {
            options.Limits.ResolveKeyChunk = 1;
            options.Limits.MaxResolveKeys = 2;
        });
        runner.PageRows = [Row(Id1, "a", "c1"), Row(Id2, "b", "c2"), Row(Id3, "c", "c3")];
        client.Script = (_, query, _) =>
        {
            var key = query.Pipeline[0].Match!.Condition!.Value!.Value[0].GetString()!;
            return new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", key, ("name", key.ToUpperInvariant())));
        };

        var result = await Success(engine, ResolveContact);
        var request = client.Calls.Should().ContainSingle().Subject.Request;

        request.Queries.Should().HaveCount(2, "one query per chunk of one key, capped at two keys");
        result.Items[0]!["contact"]!["name"]!.GetValue<string>().Should().Be("C1");
        result.Items[1]!["contact"]!["name"]!.GetValue<string>().Should().Be("C2");
        result.Items[2]!["contact"].Should().BeNull("the third key is beyond the cap");
        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Codes.ResolvePartial);
    }

    [Fact]
    public async Task More_queries_than_the_batch_cap_go_to_the_owner_in_several_batches()
    {
        var (engine, runner, client) = Host(options =>
        {
            options.Limits.ResolveKeyChunk = 1;
            options.Limits.MaxBatchQueries = 2;
        });
        runner.PageRows = [Row(Id1, "a", "c1"), Row(Id2, "b", "c2"), Row(Id3, "c", "c3")];
        client.Script = (_, query, _) =>
        {
            var key = query.Pipeline[0].Match!.Condition!.Value!.Value[0].GetString()!;
            return new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", key, ("name", key.ToUpperInvariant())));
        };

        var result = await Success(engine, ResolveContact);

        client.Calls.Should().HaveCount(2, "three one-key chunks under a cap of two need two batches");
        client.Calls.Select(call => call.Request.Queries.Count).Should().Equal(2, 1);
        result.Items.Select(item => item!["contact"]!["name"]!.GetValue<string>()).Should().Equal("C1", "C2", "C3");
    }

    [Fact]
    public async Task An_owner_refusal_is_the_callers_refusal_at_the_resolve_stage()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1")];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Refused(Codes.UnknownPath, "'name' is not a path of crm.contact.");

        var refusal = await Refused(engine, """[{ "match": { "number": { "eq": "a" } } }, { "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }]""");

        refusal.Status.Should().Be(422);
        refusal.Errors![0].Code.Should().Be(Codes.ResolveRefused);
        refusal.Errors[0].Stage.Should().Be(1, "the resolve is the caller's second stage");
        refusal.Errors[1].Code.Should().Be(Codes.UnknownPath, "the owner's errors travel with it");
    }

    [Fact]
    public async Task An_unreachable_owner_yields_null_rows_and_a_diagnostic()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1")];
        client.Unreachable.Add("crm");

        var result = await Success(engine, ResolveContact);

        result.Items[0]!["contact"].Should().BeNull();
        result.Items[0]!["number"]!.GetValue<string>().Should().Be("a", "the page never fails because a resolve did");
        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Codes.ResolveUnreachable);
    }

    [Fact]
    public async Task A_silent_owner_is_a_timeout_diagnostic_within_the_resolve_budget()
    {
        var (engine, runner, client) = Host(options => options.Execution.ResolveTimeoutMs = 50);
        runner.PageRows = [Row(Id1, "a", "c1")];
        client.Silent.Add("crm");

        var started = DateTime.UtcNow;
        var result = await Success(engine, ResolveContact);

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        result.Items[0]!["contact"].Should().BeNull();
        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Codes.ResolveTimeout);
        client.Calls.Single().Budget.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Two_targets_in_two_services_are_two_parallel_batches()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1", Vehicle1)];
        client.Script = (service, _, _) => service == "crm"
            ? new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c1", ("name", "Alice")))
            : new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString(), ("matchCode", "V-1")));

        var result = await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "resolve": { "path": "vehicleId", "as": "vehicle", "select": ["matchCode"] } }]""");

        client.Calls.Select(call => call.Service).Should().BeEquivalentTo(["crm", "vehicle"]);
        result.Items[0]!["contact"]!["name"]!.GetValue<string>().Should().Be("Alice");
        result.Items[0]!["vehicle"]!["matchCode"]!.GetValue<string>().Should().Be("V-1");

        var vehicleQuery = client.Calls.Single(call => call.Service == "vehicle").Request.Queries.Single();

        vehicleQuery.Pipeline[0].Match!.Condition!.Value!.Value[0].GetString().Should().Be(Vehicle1.ToString(), "a guid key travels as the wire string");
    }

    [Fact]
    public async Task A_projection_without_the_reference_member_still_resolves_and_the_wire_omits_the_member()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", "c1")];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c1", ("name", "Alice")));

        var result = await Success(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }, { "project": { "number": 1 } }, { "page": { "limit": 10 } }]""");

        var row = result.Items.Should().ContainSingle().Subject!.AsObject();

        row["contact"]!["name"]!.GetValue<string>().Should().Be("Alice");
        row["number"]!.GetValue<string>().Should().Be("a");
        row.ContainsKey("contactNumber").Should().BeFalse("the projection dropped it from the wire view");
    }

    // ---- semi-join ------------------------------------------------------------------------

    private const string SemiJoin = """[{ "resolve": { "path": "vehicleId", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "V-1", "options": { "ignoreCase": true } } } }, { "page": { "limit": 10, "includeTotalCount": true } }]""";

    [Fact]
    public async Task A_semi_join_asks_the_owner_for_the_ids_and_substitutes_them_into_the_page_and_count_filters()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", null, Vehicle1)];
        runner.Count = 1;
        client.Script = (_, query, _) => query.Pipeline[0].Match!.Condition!.Op == "in"
            ? new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString(), ("matchCode", "V-1")))
            : new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString()));

        var result = await Success(engine, SemiJoin);

        result.Items.Should().ContainSingle();
        result.PageInfo.TotalCount.Should().Be(1);

        // The owner was asked before the page ran, with the condition rebased onto the target.
        var (service, request, _) = client.Calls[0];

        service.Should().Be("vehicle");

        var ask = request.Queries.Single();

        ask.EntityType.Should().Be("vehicle.vehicle");
        ask.Pipeline[0].Match!.Condition!.Path.Should().Be("matchCode");
        ask.Pipeline[0].Match!.Condition!.Options!.IgnoreCase.Should().BeTrue();
        ask.Pipeline[1].Project!.Fields.Keys.Should().Equal("id");
        ask.Pipeline[2].Page!.Limit.Should().Be(500, "the owner is asked page by page, never for more than a page ceiling at once");
        ask.Pipeline[2].Page!.Cursor.Should().BeNull();

        var expected = new BsonDocument("$match", new BsonDocument("VehicleId", new BsonDocument("$in", new BsonArray { new BsonBinaryData(Vehicle1, GuidRepresentation.Standard) })));

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Stages[1].ShouldBeBson(expected, "the ids are typed as the reference member stores them");
        runner.Calls[1].Stages[1].ShouldBeBson(expected, "the count pipeline reuses the id list");
    }

    [Fact]
    public async Task A_semi_join_above_the_cap_is_refused_before_the_page_runs()
    {
        var (engine, runner, client) = Host(options => options.Limits.MaxSemiJoinIds = 1);
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString()), FakeRemoteClient.Row("id", Guid.NewGuid().ToString()));

        var refusal = await Refused(engine, SemiJoin);

        refusal.Status.Should().Be(422);
        refusal.Errors![0].Code.Should().Be(Codes.SemiJoinTooLarge);
        refusal.Errors[0].Stage.Should().Be(1);
        runner.Calls.Should().BeEmpty();
    }

    private const string SemiJoinOneRow = """[{ "resolve": { "path": "vehicleId", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "V-1" } } }, { "page": { "limit": 1 } }]""";

    private static readonly Guid Vehicle2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    [Fact]
    public async Task A_semi_join_reads_the_ids_that_span_pages_by_offset()
    {
        var (engine, runner, client) = Host(options => options.Limits.MaxPageSize = 1);
        runner.PageRows = [Row(Id1, "a", null, Vehicle1)];
        client.Script = (_, query, _) => query.Pipeline[0].Match!.Condition!.Op == "in"
            ? new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString(), ("matchCode", "V-1")))
            : query.Pipeline[2].Page!.Offset is null
                ? new FakeRemoteClient.Answer.Counted(2, false, true, FakeRemoteClient.Row("id", Vehicle1.ToString()))
                : new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle2.ToString()));

        var result = await Success(engine, SemiJoinOneRow);

        result.Items.Should().ContainSingle();

        var asks = client.Calls.Where(call => call.Request.Queries[0].Pipeline[0].Match!.Condition!.Op != "in").ToList();

        asks.Should().HaveCount(2, "the count comes with the first page and the second page is addressed by offset");
        asks[0].Request.Queries[0].Pipeline[2].Page!.IncludeTotalCount.Should().BeTrue("the first call asks how large the answer is");
        asks[0].Request.Queries[0].Pipeline[2].Page!.Offset.Should().BeNull();
        asks[1].Request.Queries[0].Pipeline[2].Page!.Offset.Should().Be(1);
        asks[1].Request.Queries[0].Pipeline[2].Page!.IncludeTotalCount.Should().BeFalse("the count was answered once");

        var expected = new BsonDocument("$match", new BsonDocument("VehicleId", new BsonDocument("$in", new BsonArray
        {
            new BsonBinaryData(Vehicle1, GuidRepresentation.Standard),
            new BsonBinaryData(Vehicle2, GuidRepresentation.Standard),
        })));

        runner.Calls[0].Stages[1].ShouldBeBson(expected, "both pages' ids are substituted");
    }

    [Fact]
    public async Task A_semi_join_above_the_cap_is_refused_on_the_count_without_reading_a_page()
    {
        var (engine, runner, client) = Host(options => options.Limits.MaxSemiJoinIds = 10);
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Counted(11, false, true, FakeRemoteClient.Row("id", Vehicle1.ToString()));

        var refusal = await Refused(engine, SemiJoinOneRow);

        refusal.Errors![0].Code.Should().Be(Codes.SemiJoinTooLarge);
        client.Calls.Should().ContainSingle("the count refuses the condition after one round trip");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_semi_join_whose_count_the_owner_capped_walks_its_pages_and_still_refuses()
    {
        var (engine, runner, client) = Host(options =>
        {
            options.Limits.MaxPageSize = 1;
            options.Limits.MaxSemiJoinIds = 1;
        });

        // A capped count below our own cap says nothing, so the pages decide.
        client.Script = (_, query, _) => query.Pipeline[2].Page!.Offset is null
            ? new FakeRemoteClient.Answer.Counted(1, true, true, FakeRemoteClient.Row("id", Vehicle1.ToString()))
            : new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle2.ToString()));

        var refusal = await Refused(engine, SemiJoinOneRow);

        refusal.Errors![0].Code.Should().Be(Codes.SemiJoinTooLarge);
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_page_of_the_same_filter_asks_the_owner_nothing()
    {
        var (engine, runner, client) = Host();
        runner.PageRows = [Row(Id1, "a", null, Vehicle1)];
        client.Script = (_, query, _) => query.Pipeline[0].Match!.Condition!.Op == "in"
            ? new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString(), ("matchCode", "V-1")))
            : new FakeRemoteClient.Answer.Counted(1, false, false, FakeRemoteClient.Row("id", Vehicle1.ToString()));

        await Success(engine, SemiJoinOneRow);

        var asked = client.Calls.Count(call => call.Request.Queries[0].Pipeline[0].Match!.Condition!.Op != "in");

        await Success(engine, SemiJoinOneRow);

        client.Calls.Count(call => call.Request.Queries[0].Pipeline[0].Match!.Condition!.Op != "in")
            .Should().Be(asked, "the id list is cached per entity, organisation and condition");
    }

    [Fact]
    public async Task A_semi_join_owner_that_does_not_answer_refuses_the_request()
    {
        var (engine, runner, client) = Host();
        client.Unreachable.Add("vehicle");

        var refusal = await Refused(engine, SemiJoin);

        refusal.Status.Should().Be(422);
        refusal.Errors![0].Code.Should().Be(Codes.ResolveUnavailable);
        runner.Calls.Should().BeEmpty("without the ids the filter cannot be evaluated");
    }

    [Fact]
    public async Task A_semi_join_owner_refusal_wraps_the_owners_errors()
    {
        var (engine, _, client) = Host();
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Refused(Codes.UnknownPath, "'matchCode' is not a path of vehicle.vehicle.");

        var refusal = await Refused(engine, SemiJoin);

        refusal.Errors![0].Code.Should().Be(Codes.ResolveRefused);
        refusal.Errors[1].Code.Should().Be(Codes.UnknownPath);
    }

    [Fact]
    public void The_cache_is_bounded_and_keyed_by_entity_organisation_key_select_and_filter()
    {
        var options = BindHost.Options(o => o.Cache.ResolveCacheMaxEntries = 2);
        using var cache = new ResolveCache(options);
        var row = new JsonObject { ["number"] = "c1" };

        cache.Set(ResolveCache.KeyOf("crm.contact", BindHost.Organisation, "c1", "s", "f"), row);
        cache.TryGet(ResolveCache.KeyOf("crm.contact", BindHost.Organisation, "c1", "s", "f"), out var hit).Should().BeTrue();
        hit.Should().NotBeSameAs(row, "a hit is a clone: a node cannot have two parents");
        cache.TryGet(ResolveCache.KeyOf("crm.contact", Guid.NewGuid(), "c1", "s", "f"), out _).Should().BeFalse("another organisation never sees the row");
        cache.TryGet(ResolveCache.KeyOf("crm.contact", BindHost.Organisation, "c1", "other", "f"), out _).Should().BeFalse("another select is another entry");

        cache.Set("k2", row);
        cache.Set("k3", row);
        cache.Count.Should().BeLessThanOrEqualTo(2, "the entry count is bounded");
    }
}
