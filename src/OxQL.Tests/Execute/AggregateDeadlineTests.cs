using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Legacy;
using OxQL.TestCases;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The deadline an aggregate has on the host's side beside the time limit the database applies: when
/// nothing comes back by the limit and the margin after it, the aggregate is given up on and the
/// request is answered as the limit's own expiry is (504 <c>QUERY_TIMEOUT</c>), under contract 2
/// and on the legacy pipeline alike. The clock is the test's: nothing here waits for real time.
/// </summary>
public class AggregateDeadlineTests
{
    private const string Order = "probe.order";

    /// <summary>A database that says nothing: an aggregate ends only when it is given up on.</summary>
    private sealed class SilentRunner : IAggregateRunner
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AggregateRunOptions> Calls { get; } = [];

        /// <summary>Ends once the first aggregate has been sent.</summary>
        public Task Started => started.Task;

        /// <summary>The aggregates whose cancellation was asked for.</summary>
        public int GivenUp;

        /// <summary>Whether an aggregate ends when it is given up on; a runner that does not hear the cancellation never ends.</summary>
        public bool HearsCancellation { get; init; } = true;

        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add(options);

            started.TrySetResult();

            var never = new TaskCompletionSource<IReadOnlyList<BsonDocument>>(TaskCreationOptions.RunContinuationsAsynchronously);

            cancellationToken.Register(() =>
            {
                Interlocked.Increment(ref GivenUp);

                if (HearsCancellation)
                    never.TrySetCanceled(cancellationToken);
            });

            return never.Task;
        }
    }

    private static MongoQueryEngine Engine(IAggregateRunner runner, TimeProvider time, Action<OxQLOptions>? configure = null) =>
        new(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options(configure), time: time);

    private static QueryValidationError TimedOut(QueryOutcome outcome)
    {
        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Status.Should().Be(504);
        refusal.Type.Should().Be("timeout");

        return refusal.Errors!.Should().ContainSingle().Subject;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_aggregate_the_database_says_nothing_of_is_given_up_on_after_its_limit_and_the_margin(bool hearsCancellation)
    {
        var time = new ManualTime();
        var runner = new SilentRunner { HearsCancellation = hearsCancellation };
        var engine = Engine(runner, time);

        var running = engine.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "limit": 5 } }]"""), BindHost.Context());

        await time.Armed();

        time.Due.Should().Equal([15_000L], "the host's 10 000 ms and the margin of 5 000 ms after them");

        time.Advance(TimeSpan.FromMilliseconds(14_999));
        await Task.Yield();
        running.IsCompleted.Should().BeFalse("the deadline has not passed");
        runner.GivenUp.Should().Be(0);

        time.Advance(TimeSpan.FromMilliseconds(1));

        var error = TimedOut(await running);

        error.Code.Should().Be(Codes.QueryTimeout);
        error.Message.Should().Be("The query exceeded its time budget.", "the answer of the limit's own expiry");
        JsonSerializer.Serialize(error.Params).Should().Be("""{"maxTimeMs":10000}""");
        runner.GivenUp.Should().Be(1, "the aggregate's cancellation was asked for");
        runner.Calls.Should().ContainSingle().Which.MaxTimeMs.Should().Be(10_000, "the limit the database applies is as it was");
    }

    [Fact]
    public async Task The_deadline_follows_the_requests_own_ceiling_and_the_hosts_margin()
    {
        var time = new ManualTime();
        var runner = new SilentRunner();
        var engine = Engine(runner, time, options => options.Execution.ClientDeadlineMarginMs = 250);

        var running = engine.ExecuteAsync(BindHost.Parse($$"""{ "entityType": "{{Order}}", "maxTimeMs": 400, "pipeline": [{ "page": { "limit": 5 } }] }"""), BindHost.Context());

        await time.Armed();

        time.Due.Should().Equal(650L);
        time.Advance(TimeSpan.FromMilliseconds(650));

        JsonSerializer.Serialize(TimedOut(await running).Params).Should().Be("""{"maxTimeMs":400}""");
    }

    [Fact]
    public async Task A_values_list_has_the_same_deadline()
    {
        var time = new ManualTime();
        var runner = new SilentRunner();
        var engine = Engine(runner, time);

        var running = engine.ExecuteAsync(BindHost.Request(Order, """[{ "values": { "path": "number" } }]"""), BindHost.Context());

        await time.Armed();
        time.Advance(TimeSpan.FromSeconds(15));

        TimedOut(await running).Code.Should().Be(Codes.QueryTimeout);
        runner.GivenUp.Should().Be(1);
    }

    private static BsonDocument SampleRow(int index) => new()
    {
        ["_id"] = new BsonBinaryData(new Guid(index + 1, 0, 0, new byte[8]), GuidRepresentation.Standard),
        ["Number"] = "n" + index,
    };

    [Fact]
    public async Task An_answer_within_the_deadline_is_the_answer_and_a_failure_of_the_database_is_its_own()
    {
        var time = new ManualTime();
        var runner = new FakeAggregateRunner { PageRows = [SampleRow(0)] };
        var engine = Engine(runner, time);

        (await engine.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "limit": 5 } }]"""), BindHost.Context())).Should().BeOfType<QueryOutcome.Success>();

        // The driver's own timeout (no server could be selected, say) is no expiry of the query: it stays a fault.
        var failing = Engine(new FakeAggregateRunner { Fail = new TimeoutException("A timeout occurred selecting a server.") }, time);
        var fault = (await failing.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "limit": 5 } }]"""), BindHost.Context())).Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        fault.Status.Should().Be(500);
        fault.Errors![0].Code.Should().Be(Codes.InternalError);

        // The limit's own expiry, said by the database, is answered as it always was, now with the limit it had.
        var expired = Engine(new FakeAggregateRunner { Fail = new MongoExecutionTimeoutException(new MongoDB.Driver.Core.Connections.ConnectionId(new MongoDB.Driver.Core.Servers.ServerId(new MongoDB.Driver.Core.Clusters.ClusterId(), new System.Net.DnsEndPoint("localhost", 27017))), "operation exceeded time limit") }, time);
        var error = TimedOut(await expired.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "limit": 5 } }]"""), BindHost.Context()));

        JsonSerializer.Serialize(error.Params).Should().Be("""{"maxTimeMs":10000}""");
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_a_cancellation_not_a_timeout()
    {
        var time = new ManualTime();
        var runner = new SilentRunner();
        var engine = Engine(runner, time);
        using var caller = new CancellationTokenSource();

        var running = engine.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "limit": 5 } }]"""), BindHost.Context(), caller.Token);

        await time.Armed();
        await caller.CancelAsync();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        runner.GivenUp.Should().Be(1, "the aggregate hears the caller's cancellation as it did");
    }

    [Fact]
    public async Task A_margin_below_zero_switches_the_deadline_off()
    {
        var time = new ManualTime();
        var runner = new SilentRunner();
        var engine = Engine(runner, time, options => options.Execution.ClientDeadlineMarginMs = -1);
        using var caller = new CancellationTokenSource();

        var running = engine.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "limit": 5 } }]"""), BindHost.Context(), caller.Token);

        await runner.Started.WaitAsync(TimeSpan.FromSeconds(30));
        runner.Calls.Should().ContainSingle();

        time.Advance(TimeSpan.FromHours(1));
        time.Due.Should().BeEmpty("no deadline was armed");
        running.IsCompleted.Should().BeFalse();

        await caller.CancelAsync();
        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>A database whose aggregate ends when the test says so, in a fault.</summary>
    private sealed class FaultingRunner : IAggregateRunner
    {
        private readonly TaskCompletionSource<IReadOnlyList<BsonDocument>> ending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GivenUp;

        public void Fault(Exception exception) => ending.TrySetException(exception);

        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => Interlocked.Increment(ref GivenUp));

            return ending.Task;
        }
    }

    /// <summary>A clock whose timers do something of the test's in the instant they fire, before the one who waits is told.</summary>
    private sealed class InstantTime(Action onFire) : TimeProvider
    {
        private readonly TaskCompletionSource<Action> armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The firing of the first timer that was started.</summary>
        public Task<Action> Armed => armed.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            armed.TrySetResult(() =>
            {
                onFire();
                callback(state);
            });

            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static MongoExecutionTimeoutException Expiry() =>
        new(new MongoDB.Driver.Core.Connections.ConnectionId(new MongoDB.Driver.Core.Servers.ServerId(new MongoDB.Driver.Core.Clusters.ClusterId(), new System.Net.DnsEndPoint("localhost", 27017))), "operation exceeded time limit");

    [Fact]
    public async Task What_the_runner_ends_in_the_instant_the_deadline_passes_is_what_surfaces()
    {
        var entity = BindHost.Probe.Entities.Values.First();

        foreach (var fault in new Exception[] { Expiry(), new InvalidOperationException("the connection was closed"), new TimeoutException("A timeout occurred selecting a server.") })
        {
            var runner = new FaultingRunner();
            var time = new InstantTime(() => runner.Fault(fault));
            var deadline = new AggregateDeadline(time, 5_000);

            var running = deadline.RunAsync(runner, entity, [], new AggregateRunOptions(10_000, null), CancellationToken.None);

            (await time.Armed)();

            (await FluentActions.Awaiting(() => running).Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(fault, "the runner's own end stands, and no timeout of the wait takes its place");
            runner.GivenUp.Should().Be(0, "an aggregate that has ended is not given up on");
        }
    }

    [Fact]
    public async Task The_limits_expiry_said_in_the_instant_the_deadline_passes_is_answered_as_the_expiry()
    {
        var runner = new FaultingRunner();
        var time = new InstantTime(() => runner.Fault(Expiry()));
        var engine = Engine(runner, time);

        var running = engine.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "limit": 5 } }]"""), BindHost.Context());

        (await time.Armed)();

        var error = TimedOut(await running);

        error.Code.Should().Be(Codes.QueryTimeout);
        JsonSerializer.Serialize(error.Params).Should().Be("""{"maxTimeMs":10000}""");
    }

    // ---- the legacy pipeline --------------------------------------------------------------------

    [Fact]
    public async Task The_legacy_pipeline_keeps_its_own_ceiling_and_gives_an_aggregate_up_after_it_and_the_margin()
    {
        var time = new ManualTime();
        var runner = new SilentRunner();
        var options = BindHost.Options(configure => configure.Compat.Legacy.MaxTimeMs = 120_000);
        var pipeline = new LegacyPipeline(new StaticEntityModelProvider(BindHost.Probe), runner, options, NullLogger<LegacyPipeline>.Instance, time);
        var request = JsonDocument.Parse("""{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 5 } }] }""").RootElement;

        var running = pipeline.ExecuteAsync(request, BindHost.Context(options, contract: 1), CancellationToken.None);

        await time.Armed();

        runner.Calls.Should().ContainSingle().Which.MaxTimeMs.Should().BeInRange(119_000, 120_000, "the ceiling is the pipeline's own, not the engine's 10 000 ms");
        time.Due.Single().Should().BeInRange(124_000, 125_000);

        time.Advance(TimeSpan.FromMilliseconds(123_000));
        await Task.Yield();
        running.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromMilliseconds(2_000));

        var error = TimedOut(await running);

        error.Code.Should().Be(Codes.QueryTimeout);
        error.Message.Should().Be("The query did not finish within 120000 ms.", "the words a contract 1 caller reads for the limit's own expiry");
        error.Params.Should().BeNull("a contract 1 answer is what it was");
        runner.GivenUp.Should().Be(1);
    }
}
