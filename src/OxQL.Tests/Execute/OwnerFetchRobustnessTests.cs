using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// How the keyed fetch talks to owners when something is off: a union target's select path its
/// owner lacks, learned from any chunk and kept in the cache (RE-5, RE-6, PRE-3); split batches
/// sharing one budget and an owner ceiling below it (RE-7, RS-3, PRE-4); an empty semi-join answer
/// kept only as long as a negative one (RE-17); a local target without this host's engine (RE-21);
/// a batch's maxTimeMs bounding the whole batch (RS-3).
/// </summary>
public class OwnerFetchRobustnessTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static readonly Guid Invoice1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Invoice2 = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Line1 = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Line2 = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonDocument SourceRow(Guid invoice, Guid line) => new()
    {
        ["_id"] = Id(invoice),
        ["OrganizationId"] = Id(BindHost.Organisation),
        ["Number"] = "RE",
        ["Source"] = new BsonDocument { ["Type"] = "remote", ["_id"] = Id(line) },
    };

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, FakeRemoteClient Client, OwnerFetchCache Cache) Host(Action<OxQLOptions>? configure = null)
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options(configure);
        var cache = new OwnerFetchCache(options);

        return (new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: cache), runner, client, cache);
    }

    /// <summary>transport.shipment#billingLines, lacking the owning-row member <c>name</c>: a refusal at the projection, or one row per key asked.</summary>
    private static FakeRemoteClient.Answer TransportOwner(QueryRequest query)
    {
        var projectAt = query.Pipeline.ToList().FindLastIndex(stage => stage.Project is not null);

        if (query.Pipeline[projectAt].Project!.Fields.ContainsKey("name"))
            return new FakeRemoteClient.Answer.Refused(Codes.UnknownPath, "'name' is not a path of transport.shipment.", Stage: projectAt, Path: "name");

        var keys = query.KeyedBy!.Keys!.Value.EnumerateArray().Select(key => key.GetString()!).ToList();

        return new FakeRemoteClient.Answer.Rows(keys.Select(key => new JsonObject
        {
            ["entity"] = "transport.shipment", ["id"] = Guid.NewGuid().ToString(), ["number"] = "S-" + key[^1],
            ["oxEl"] = new JsonObject { ["id"] = key },
        }).ToArray());
    }

    private const string OwningRow = """[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner", "parentSelect": ["id", "number", "name"] } }]""";

    private static QueryResult Succeeded(QueryOutcome outcome)
    {
        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    // ---- a union target's missing select path (RE-5, RE-6) ----------------------------------------

    [Fact]
    public async Task Every_chunk_that_carried_a_path_the_owner_lacks_is_asked_again_without_it()
    {
        var (engine, runner, client, _) = Host(options => options.Limits.ResolveKeyChunk = 1);
        runner.PageRows = [SourceRow(Invoice1, Line1), SourceRow(Invoice2, Line2)];
        client.Script = (_, query, _) => TransportOwner(query);

        var result = Succeeded(await engine.ExecuteAsync(BindHost.Request(Invoice, OwningRow), BindHost.Context()));

        result.Items.Select(item => item!["owner"]!["number"]!.GetValue<string>()).Should().Equal("S-1", "S-2");
        client.Calls.Select(call => call.Request.Queries.Count).Should().Equal([2, 2], "both chunks carried the path and both are asked again");
        result.Diagnostics!.Should().ContainSingle(diagnostic => diagnostic.Code == Notes.SelectPathNotOnTarget);
    }

    [Fact]
    public async Task A_path_an_owner_said_the_target_lacks_is_not_sent_again_and_is_reported_from_the_cache_too()
    {
        var (engine, runner, client, _) = Host();
        runner.PageRows = [SourceRow(Invoice1, Line1)];
        client.Script = (_, query, _) => TransportOwner(query);

        var cold = Succeeded(await engine.ExecuteAsync(BindHost.Request(Invoice, OwningRow), BindHost.Context()));
        client.Calls.Should().HaveCount(2);

        // Another key of the same plan: the owner is asked once, without the path.
        runner.PageRows = [SourceRow(Invoice2, Line2)];
        var other = Succeeded(await engine.ExecuteAsync(BindHost.Request(Invoice, OwningRow), BindHost.Context()));

        client.Calls.Should().HaveCount(3, "the drop is known, so no round is spent to learn it again");
        client.Calls[2].Request.Queries.Single().Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys.Should().NotContain("name");

        // The first key again: answered from the cache, and the note is still there.
        runner.PageRows = [SourceRow(Invoice1, Line1)];
        var warm = Succeeded(await engine.ExecuteAsync(BindHost.Request(Invoice, OwningRow), BindHost.Context()));

        client.Calls.Should().HaveCount(3);

        foreach (var answer in new[] { cold, other, warm })
            answer.Diagnostics!.Where(diagnostic => diagnostic.Code == Notes.SelectPathNotOnTarget).Select(diagnostic => diagnostic.Path).Should().Equal("name");
    }

    // ---- budgets (RE-7, RS-3, PRE-4) ---------------------------------------------------------------

    [Fact]
    public async Task Split_batches_share_one_budget_so_the_request_ends_within_its_ceiling()
    {
        var (engine, runner, client, _) = Host(options =>
        {
            options.Limits.ResolveKeyChunk = 1;
            options.Execution.MaxTimeMs = 300;
            options.Execution.ResolveTimeoutMs = 4_000;
        });
        runner.PageRows = [SourceRow(Invoice1, Line1), SourceRow(Invoice2, Line2), SourceRow(Guid.NewGuid(), Guid.NewGuid()), SourceRow(Guid.NewGuid(), Guid.NewGuid())];
        client.Owners["transport"] = new RemoteOwnerInfo("2.1.0.0", 2, MaxBatchQueries: 1);
        client.Delay = TimeSpan.FromMilliseconds(120);
        client.Script = (_, query, _) => TransportOwner(query);

        var watch = Stopwatch.StartNew();
        var result = Succeeded(await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "source.id", "as": "line" } }]"""), BindHost.Context()));
        watch.Stop();

        client.Calls.Select(call => call.Budget).Should().BeInDescendingOrder("each split batch gets what is left, not the whole budget again");
        client.Calls.Should().OnlyContain(call => call.Request.MaxTimeMs == KeyedFetch.OwnerCeilingMs(call.Budget));
        client.Calls.Should().OnlyContain(call => call.Request.MaxTimeMs < (int)call.Budget.TotalMilliseconds || call.Budget < TimeSpan.FromMilliseconds(10),
            "the owner stops before this host stops waiting (a budget under 10 ms has no margin left to take)");
        for (var next = 1; next < client.Calls.Count; next++)
            client.Calls[next].Budget.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(Math.Max(1, (client.Calls[next - 1].Budget - TimeSpan.FromMilliseconds(100)).TotalMilliseconds)),
                "a later batch has what the earlier ones (120 ms each) left");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1_000), "four batches of 120 ms in a request of 300 ms end with the request");

        var timeout = result.Diagnostics!.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.ResolveTimeout).Subject;
        timeout.Message.Should().MatchRegex(@"within \d+ ms").And.NotContain("4000", "the message names the time the failing batch had, not the per-resolve ceiling");
    }

    [Fact]
    public void The_owner_ceiling_is_the_budget_less_a_tenth_at_most_250_ms()
    {
        KeyedFetch.OwnerCeilingMs(TimeSpan.FromMilliseconds(4_000)).Should().Be(3_750);
        KeyedFetch.OwnerCeilingMs(TimeSpan.FromMilliseconds(300)).Should().Be(270);
        KeyedFetch.OwnerCeilingMs(TimeSpan.FromMilliseconds(1)).Should().Be(1);
    }

    private sealed class Scope : IOxQLScopeProvider
    {
        public ValueTask<Guid?> OrganisationAsync(Microsoft.AspNetCore.Http.HttpContext? httpContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Guid?>(BindHost.Organisation);
    }

    /// <summary>Records each query's maxTimeMs and takes a while.</summary>
    private sealed class SlowEngine : IQueryEngine
    {
        public List<int?> Ceilings { get; } = [];

        public async Task<QueryOutcome> ExecuteAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken = default)
        {
            Ceilings.Add(context.MaxTimeMs);
            await Task.Delay(100, cancellationToken);

            return QueryOutcome.Of(new QueryResult { Items = [], PageInfo = new PageInfo { HasNextPage = false } });
        }

        public Task<ExplainOutcome> ExplainAsync(ExplainRequest request, RequestContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task A_batch_maxTimeMs_bounds_the_whole_batch_each_query_running_under_what_is_left()
    {
        var options = BindHost.Options(configure => configure.Compat.Enabled = false);
        var engine = new SlowEngine();
        var service = new OxQLQueryService(engine, new Scope(), options, new StaticEntityModelProvider(ResolveModel.Model));
        var query = BindHost.Request(Invoice, "[]");

        await service.BatchAsync(new BatchRequest { Queries = [query, query, query], MaxTimeMs = 1_000 });

        engine.Ceilings.Should().HaveCount(3);
        engine.Ceilings[0].Should().BeInRange(900, 1_000);
        engine.Ceilings[1].Should().BeLessThan(engine.Ceilings[0]!.Value - 50);
        engine.Ceilings[2].Should().BeLessThan(engine.Ceilings[1]!.Value - 50);
    }

    // ---- the semi-join's empty answer (RE-17) ------------------------------------------------------

    [Fact]
    public void An_empty_semi_join_answer_lives_only_as_long_as_a_negative_one()
    {
        using var none = new OwnerFetchCache(BindHost.Options(configure => configure.Cache.NegativeResolveTtlSeconds = 0));

        none.SetKeys("empty", []);
        none.SetKeys("full", ["a"]);

        none.TryGetKeys("empty", out _).Should().BeFalse("a negative TTL of 0 keeps no empty answer");
        none.TryGetKeys("full", out var values).Should().BeTrue();
        values.Should().Equal("a");

        using var some = new OwnerFetchCache(BindHost.Options());

        some.SetKeys("empty", []);
        some.TryGetKeys("empty", out _).Should().BeTrue();
    }

    // ---- the owner's page and the key budget (RE-13, RE-14) ---------------------------------------

    private static readonly Guid ContactA = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    private static readonly Guid ContactB = Guid.Parse("c0000000-0000-0000-0000-00000000000b");
    private static readonly Guid ContactC = Guid.Parse("c0000000-0000-0000-0000-00000000000c");

    private static BsonDocument ContactRow(Guid contact) => new()
    {
        ["_id"] = Id(Guid.NewGuid()),
        ["OrganizationId"] = Id(BindHost.Organisation),
        ["ContactId"] = Id(contact),
    };

    [Fact]
    public async Task An_owner_with_a_smaller_page_than_this_host_is_asked_chunks_its_page_holds()
    {
        var (engine, runner, client, _) = Host();
        runner.PageRows = [ContactRow(ContactA), ContactRow(ContactB), ContactRow(ContactC)];
        client.Owners["crm"] = new RemoteOwnerInfo("2.1.0.0", 2, MaxBatchQueries: 10, MaxPageSize: 2);
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows();

        Succeeded(await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""), BindHost.Context()));

        client.Calls.Single().Request.Queries.Select(query => query.Pipeline.Single(stage => stage.Page is not null).Page!.Limit)
            .Should().Equal([2, 1], "the owner answers pages of two, so no query asks for more");
    }

    [Fact]
    public void The_owners_page_is_read_off_its_shallow_health()
    {
        RemoteOwnerInfo.FromShallowHealth(JsonNode.Parse("""{ "limits": { "maxBatchQueries": 5, "maxPageSize": 200 } }"""))!.MaxPageSize.Should().Be(200);
    }

    private sealed class EntityRunner : IAggregateRunner
    {
        public Dictionary<string, List<BsonDocument>> Rows { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(OxQL.Model.EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BsonDocument>>(Rows.TryGetValue(entity.Id, out var rows) ? rows.Select(row => row.DeepClone().AsBsonDocument).ToList() : []);
    }

    [Fact]
    public async Task A_key_asked_of_both_targets_of_a_union_counts_once_against_the_budget()
    {
        var runner = new EntityRunner();
        var options = BindHost.Options(configure => configure.Limits.MaxResolveKeys = 1);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, new FakeRemoteClient(), cache: new OwnerFetchCache(options));

        runner.Rows[Invoice] = [new BsonDocument
        {
            ["_id"] = Id(Invoice1), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "RE",
            ["LocalSource"] = new BsonDocument { ["Type"] = "logistics", ["_id"] = Id(Line1) },
        }];

        var result = Succeeded(await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "localSource.id", "as": "line" } }]"""), BindHost.Context()));

        (result.Diagnostics ?? []).Should().NotContain(diagnostic => diagnostic.Code == Codes.ResolvePartial, "one key of one row is one key, whether the shipment or the tour holds it");
    }

    // ---- the log line's counters (RE-19) -----------------------------------------------------------

    [Fact]
    public async Task A_split_batch_counts_each_part_and_a_semi_join_the_cache_answers_counts_none()
    {
        var options = BindHost.Options(configure => configure.Limits.ResolveKeyChunk = 1);
        var cache = new OwnerFetchCache(options);
        var client = new FakeRemoteClient();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, client, cache: cache);
        var fetch = new KeyedFetch(client, engine, cache, options);

        client.Owners["crm"] = new RemoteOwnerInfo("2.1.0.0", 2, MaxBatchQueries: 1);
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Counted(0, false, false);

        var keyed = MongoCompiler.Compile(await BindHost.BoundAsync(ResolveModel.Model, Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""), new CompileOptions(5_000, null, 10_000));
        var resolved = await fetch.ByKeysAsync(keyed, [ContactRow(ContactA), ContactRow(ContactB), ContactRow(ContactC)], BindHost.Context(), TimeSpan.FromSeconds(5), strict: false, CancellationToken.None);

        resolved.Calls.Should().Be(3, "three parts of one owner's batch are three calls");

        var filtered = MongoCompiler.Compile(await BindHost.BoundAsync(ResolveModel.Model, Invoice,
            """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "match": { "r.name": { "eq": "x" } } }]"""), new CompileOptions(5_000, null, 10_000));

        (await fetch.ByConditionCountedAsync(filtered, BindHost.Context(), TimeSpan.FromSeconds(5), CancellationToken.None)).Calls.Should().Be(1);

        var again = MongoCompiler.Compile(await BindHost.BoundAsync(ResolveModel.Model, Invoice,
            """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "match": { "r.name": { "eq": "x" } } }]"""), new CompileOptions(5_000, null, 10_000));

        (await fetch.ByConditionCountedAsync(again, BindHost.Context(), TimeSpan.FromSeconds(5), CancellationToken.None)).Calls.Should().Be(0, "the cache answered");
    }

    // ---- a local target without this host's engine (RE-21) -----------------------------------------

    [Fact]
    public async Task A_fetch_without_this_hosts_engine_refuses_a_local_keyed_target_instead_of_throwing()
    {
        var client = new FakeRemoteClient();
        var fetch = new KeyedFetch(client, new OwnerFetchCache(BindHost.Options()), BindHost.Options());
        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, """[{ "resolve": { "path": "customerIds", "as": "c", "elements": "first" } }]""");
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));
        var page = new[] { new BsonDocument { ["_id"] = Id(Invoice1), ["CustomerIds"] = new BsonArray { Id(Guid.NewGuid()) } } };

        var result = await fetch.ByKeysAsync(compiled, page, BindHost.Context(), TimeSpan.FromSeconds(5), strict: false, CancellationToken.None);

        result.Refusal.Should().NotBeNull();
        result.Refusal!.Errors!.Single().Code.Should().Be(Codes.ResolveUnavailable);
        client.Calls.Should().BeEmpty();
    }
}
