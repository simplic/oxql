using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The queries of one internal batch run side by side, a few at once
/// (<c>Execution:BatchConcurrency</c>): at the owner (<see cref="OxQLQueryService"/>'s internal
/// batch) and at this host as its own owner (<see cref="SelfOwner"/>). What must not change with it:
/// the order of the answers, an entry's failure staying its own, the batch's one time budget, the
/// caller's cancellation, the host's per-request collaborators being called one at a time, and what
/// the batch costs its own owners.
/// </summary>
public class BatchConcurrencyTests
{
    private const string Order = "probe.order";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- BatchRun ------------------------------------------------------------------------------

    [Fact]
    public async Task The_results_come_in_the_order_of_the_items_and_at_most_the_degree_run_at_once()
    {
        var running = 0;
        var peak = 0;

        var results = await BatchRun.RunAsync(Enumerable.Range(0, 10).ToList(), 3, async (item, token) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));

            // The later an item, the sooner it ends: an order by completion would be the reverse.
            await Task.Delay(TimeSpan.FromMilliseconds(5 * (10 - item)), token);
            Interlocked.Decrement(ref running);

            return item * 2;
        }, CancellationToken.None);

        results.Should().Equal(Enumerable.Range(0, 10).Select(item => item * 2));
        peak.Should().Be(3);
    }

    [Fact]
    public async Task A_degree_of_one_runs_the_items_one_after_another()
    {
        var running = 0;
        var peak = 0;

        await BatchRun.RunAsync(Enumerable.Range(0, 4).ToList(), 1, async (item, token) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            await Task.Delay(5, token);
            Interlocked.Decrement(ref running);

            return item;
        }, CancellationToken.None);

        peak.Should().Be(1);
    }

    [Fact]
    public async Task A_run_that_throws_travels_as_itself_and_nothing_further_starts()
    {
        var started = 0;

        var run = () => BatchRun.RunAsync(Enumerable.Range(0, 10).ToList(), 2, async (item, token) =>
        {
            Interlocked.Increment(ref started);

            if (item == 0)
            {
                await Task.Yield();
                throw new InvalidDataException("the first one failed");
            }

            // The one beside the failure runs until it is stopped; nothing behind them gets a place.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);

            return item;
        }, CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidDataException>()).WithMessage("the first one failed");
        started.Should().Be(2, "the items behind the failure were not started, and the one beside it was stopped");
    }

    [Fact]
    public async Task The_callers_cancellation_travels_as_a_cancellation()
    {
        using var caller = new CancellationTokenSource();

        var run = BatchRun.RunAsync(Enumerable.Range(0, 6).ToList(), 2, async (item, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);

            return item;
        }, caller.Token);

        await caller.CancelAsync();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- the fixtures ---------------------------------------------------------------------------

    /// <summary>
    /// Answers every aggregate after <see cref="Delay"/> and knows how many ran at once, and under which
    /// time limit. With <see cref="Together"/> set, the first aggregates wait for that many to be in
    /// flight before any of them answers (or for five seconds, when they never are), so how many run
    /// side by side is read off a count, not off a clock.
    /// </summary>
    private sealed class SlowRunner : IAggregateRunner
    {
        private readonly TaskCompletionSource together = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int running;

        public int Together { get; set; }

        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(40);

        public int Peak { get; private set; }

        public int Started
        {
            get
            {
                lock (Calls)
                    return Calls.Count;
            }
        }

        public List<(string Entity, int MaxTimeMs)> Calls { get; } = [];

        public Func<EntityDef, IReadOnlyList<BsonDocument>> Rows { get; set; } = _ => [];

        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((entity.Id, options.MaxTimeMs));
                Peak = Math.Max(Peak, ++running);

                if (running >= Together)
                    together.TrySetResult();
            }

            try
            {
                if (Together > 0)
                    await Task.WhenAny(together.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));

                await Task.Delay(Delay, cancellationToken);

                return Rows(entity);
            }
            finally
            {
                lock (Calls)
                    running--;
            }
        }
    }

    /// <summary>A scope provider that counts how often it is asked and fails when it is asked by two at once, as a provider over a unit of work would.</summary>
    private sealed class CountingScope : IOxQLScopeProvider
    {
        private int inside;

        public int Asked { get; private set; }

        public bool Overlapped { get; private set; }

        public async ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken)
        {
            Asked++;

            if (Interlocked.Increment(ref inside) > 1)
                Overlapped = true;

            await Task.Delay(5, cancellationToken);
            Interlocked.Decrement(ref inside);

            return BindHost.Organisation;
        }
    }

    /// <summary>An addon source that says whether it was ever read by two at once.</summary>
    private sealed class CountingAddons : IAddonDefinitionSource
    {
        private int inside;

        public int Asked { get; private set; }

        public bool Overlapped { get; private set; }

        public async ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref inside) > 1)
                Overlapped = true;

            Asked++;
            await Task.Delay(5, cancellationToken);
            Interlocked.Decrement(ref inside);

            return [];
        }
    }

    private static (OxQLQueryService Service, SlowRunner Runner, FakeRemoteClient Client, CountingScope Scope, CountingAddons Addons) Host(Action<OxQLOptions>? configure = null)
    {
        var options = BindHost.Options(options =>
        {
            options.Compat.Enabled = false;
            configure?.Invoke(options);
        });
        var models = new StaticEntityModelProvider(BindHost.Probe);
        var runner = new SlowRunner();
        var client = new FakeRemoteClient();
        var scope = new CountingScope();
        var addons = new CountingAddons();
        var engine = new MongoQueryEngine(models, runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        return (new OxQLQueryService(engine, scope, options, models, addons), runner, client, scope, addons);
    }

    private static BatchRequest Batch(int queries, int? maxTimeMs = null, string pipeline = "[]") => new()
    {
        Queries = [.. Enumerable.Range(0, queries).Select(_ => BindHost.Request(Order, pipeline))],
        MaxTimeMs = maxTimeMs,
    };

    private static async Task<IReadOnlyList<JsonNode?>> ResultsAsync(Task<BatchOutcome> batch) =>
        (await batch).Should().BeOfType<BatchOutcome.Success>().Subject.Response.Results;

    // ---- the owner's internal batch -------------------------------------------------------------

    [Fact]
    public async Task The_queries_of_an_internal_batch_run_side_by_side_up_to_the_configured_degree()
    {
        var (service, runner, _, _, _) = Host();
        runner.Together = 4;

        var results = await ResultsAsync(service.BatchAsync(Batch(6), internalCall: true));

        results.Should().HaveCount(6).And.OnlyContain(result => result!["items"] != null);
        runner.Peak.Should().Be(4, "Execution:BatchConcurrency is 4 by default");
    }

    [Fact]
    public async Task The_degree_is_the_hosts_to_set_and_one_is_the_batch_as_it_always_ran()
    {
        var (two, twoRunner, _, _, _) = Host(options => options.Execution.BatchConcurrency = 2);
        var (one, oneRunner, _, oneScope, _) = Host(options => options.Execution.BatchConcurrency = 1);

        twoRunner.Together = 2;

        await ResultsAsync(two.BatchAsync(Batch(5), internalCall: true));
        await ResultsAsync(one.BatchAsync(Batch(3), internalCall: true));

        twoRunner.Peak.Should().Be(2);
        oneRunner.Peak.Should().Be(1);
        oneScope.Asked.Should().Be(3, "one after another, each query reads its own context");
    }

    [Fact]
    public async Task A_public_batch_runs_one_query_after_another()
    {
        var (service, runner, _, _, _) = Host();

        await ResultsAsync(service.BatchAsync(Batch(4)));

        runner.Peak.Should().Be(1, "the parallelism of a public batch is the caller's, not the host's");
    }

    [Fact]
    public async Task The_answers_are_in_the_order_asked_and_a_refused_or_empty_entry_is_its_own()
    {
        var (service, runner, _, _, _) = Host();
        var batch = new BatchRequest
        {
            Queries =
            [
                BindHost.Request(Order, """[{ "page": { "limit": 1 } }]"""),
                BindHost.Request(Order, """[{ "match": { "nothing": { "eq": 1 } } }]"""),
                null!,
                BindHost.Request("probe.nowhere", "[]"),
                BindHost.Request(Order, """[{ "page": { "limit": 2 } }]"""),
            ],
        };

        var results = await ResultsAsync(service.BatchAsync(batch, internalCall: true));

        results[0]!["items"].Should().NotBeNull();
        results[1]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownPath);
        results[2]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownStage);
        results[3]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownEntity);
        results[4]!["items"].Should().NotBeNull();
        runner.Calls.Should().HaveCount(2, "the refused entries ran nothing and stopped nothing");
    }

    [Fact]
    public async Task The_scope_is_read_once_for_the_batch_and_the_addon_definitions_one_at_a_time()
    {
        var (service, _, _, scope, addons) = Host();

        await ResultsAsync(service.BatchAsync(Batch(6, pipeline: """[{ "match": { "addon.weight": { "gte": 1 } } }]"""), internalCall: true));

        scope.Asked.Should().Be(1);
        scope.Overlapped.Should().BeFalse();
        addons.Asked.Should().Be(6, "every query binds its own addon path");
        addons.Overlapped.Should().BeFalse("the source is the host's and lives per request");
    }

    [Fact]
    public async Task The_batchs_time_is_one_deadline_and_a_query_that_starts_later_has_less_of_it()
    {
        var (service, runner, _, _, _) = Host(options => options.Execution.BatchConcurrency = 2);
        runner.Together = 2;
        runner.Delay = TimeSpan.FromMilliseconds(120);

        await ResultsAsync(service.BatchAsync(Batch(4, maxTimeMs: 5_000), internalCall: true));

        var limits = runner.Calls.Select(call => call.MaxTimeMs).ToList();

        limits.Should().HaveCount(4).And.OnlyContain(limit => limit <= 5_000);
        limits.Skip(2).Should().OnlyContain(limit => limit <= 5_000 - 100, "the second pair started a delay later, under what was left");
    }

    [Fact]
    public async Task The_callers_cancellation_ends_the_batch_as_a_cancellation()
    {
        var (service, runner, _, _, _) = Host();
        runner.Delay = Timeout.InfiniteTimeSpan;
        using var caller = new CancellationTokenSource();

        var batch = service.BatchAsync(Batch(6), internalCall: true, caller.Token);

        // Binding takes its turn at the addon source, so the four start a moment apart.
        for (var waited = 0; waited < 200 && runner.Started < 4; waited++)
            await Task.Delay(10);

        await caller.CancelAsync();

        await FluentActions.Awaiting(() => batch).Should().ThrowAsync<OperationCanceledException>();
        runner.Calls.Should().HaveCount(4, "the two behind the first four never started");
    }

    // ---- what the batch costs its own owners ----------------------------------------------------

    private static BsonDocument Row(Guid id, string contact) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["Number"] = "a",
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
        ["ContactNumber"] = contact,
    };

    [Fact]
    public async Task Two_queries_of_a_batch_that_would_send_an_owner_the_same_query_send_it_once()
    {
        var (service, runner, client, _, _) = Host();
        runner.Delay = TimeSpan.FromMilliseconds(5);
        runner.Rows = _ => [Row(Id1, "c1")];
        client.Delay = TimeSpan.FromMilliseconds(60);
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("number", "c1", ("name", "Alice")));

        var results = await ResultsAsync(service.BatchAsync(
            Batch(3, pipeline: """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }, { "page": { "limit": 10 } }]"""), internalCall: true));

        results.Should().HaveCount(3).And.OnlyContain(result => result!["items"]![0]!["contact"]!["name"]!.GetValue<string>() == "Alice");
        client.Calls.Should().ContainSingle("run one after another the second and third read the first one's rows from the cache; side by side they wait for its call instead of asking again");
    }

    [Fact]
    public async Task An_owner_call_that_failed_is_the_failure_of_the_queries_waiting_for_it()
    {
        var (service, runner, client, _, _) = Host();
        runner.Delay = TimeSpan.FromMilliseconds(5);
        runner.Rows = _ => [Row(Id1, "c1")];
        client.Unreachable.Add("crm");

        var results = await ResultsAsync(service.BatchAsync(
            Batch(3, pipeline: """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }, { "page": { "limit": 10 } }]"""), internalCall: true));

        results.Should().HaveCount(3).And.OnlyContain(result => result!["diagnostics"]![0]!["code"]!.GetValue<string>() == Codes.ResolveUnreachable);
        results.Should().OnlyContain(result => result!["items"]![0]!["contact"] == null);
    }

    // ---- this host as its own owner -------------------------------------------------------------

    [Fact]
    public async Task This_host_runs_the_queries_it_sends_itself_side_by_side_and_answers_in_order()
    {
        var options = BindHost.Options();
        var runner = new SlowRunner { Rows = entity => [Row(Id1, entity.Id)], Together = 4, Delay = TimeSpan.FromMilliseconds(120) };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options);
        var owner = new SelfOwner(engine, BindHost.Context(options));
        var limits = new[] { 1, 2, 3, 4, 5, 6 };

        var response = await owner.BatchAsync("", new BatchRequest
        {
            Queries = [.. limits.Select(limit => BindHost.Request(Order, $$"""[{ "page": { "limit": {{limit}} } }]"""))],
            MaxTimeMs = 4_000,
        }, TimeSpan.FromSeconds(4), CancellationToken.None);

        response.Results.Should().HaveCount(6).And.OnlyContain(result => result!["items"] != null);
        runner.Peak.Should().Be(4);
        runner.Calls.Select(call => call.MaxTimeMs).Should().OnlyContain(limit => limit <= 4_000);
        runner.Calls.Skip(4).Select(call => call.MaxTimeMs).Should().OnlyContain(limit => limit <= 4_000 - 100, "the batch's time is one deadline, not a budget per query");
    }
}
