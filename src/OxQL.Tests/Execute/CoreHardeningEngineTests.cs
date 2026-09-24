using System.Net;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>What the engine does when the database or the stored data misbehaves: a coded refusal or a verbatim value, never a fault.</summary>
public class CoreHardeningEngineTests
{
    private const string Order = "probe.order";

    private static readonly ConnectionId Connection = new(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017)));

    private static MongoCommandException CommandFailure(int code, string message) =>
        new(Connection, message, new BsonDocument("aggregate", "orders"), new BsonDocument { ["ok"] = 0, ["code"] = code, ["errmsg"] = message });

    private static async Task<Refusal> Refused(FakeAggregateRunner runner, string pipeline)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), BindHost.Context());

        return outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;
    }

    [Theory]
    [InlineData(146, "Exceeded memory limit for $group")]
    [InlineData(10334, "BSONObj size is invalid")]
    public async Task A_value_the_server_cannot_build_is_too_expensive_not_a_fault(int code, string message)
    {
        var refusal = await Refused(new FakeAggregateRunner { Fail = CommandFailure(code, message) }, "[]");

        refusal.Status.Should().Be(422);
        refusal.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.QueryTooExpensive);
    }

    [Theory]
    [InlineData(51091, "Regular expression is invalid: lookbehind assertion is not fixed length")]
    [InlineData(2, "Regular expression is too long")]
    public async Task A_pattern_the_server_cannot_compile_is_an_invalid_regex_not_a_fault(int code, string message)
    {
        var refusal = await Refused(new FakeAggregateRunner { Fail = CommandFailure(code, message) }, """[{ "match": { "number": { "regex": "(?<=a+)b" } } }]""");

        refusal.Status.Should().Be(400);
        refusal.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.InvalidRegex);
    }

    /// <summary>Fails the page asynchronously and leaves the count running until its token is cancelled or the test releases it.</summary>
    private sealed class FailingPageRunner(Exception pageFailure) : IAggregateRunner
    {
        public TaskCompletionSource<IReadOnlyList<BsonDocument>> Count { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CountToken { get; private set; }

        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            if (stages[^1].Contains("$count"))
            {
                CountToken = cancellationToken;
                return await Count.Task;
            }

            await Task.Yield();
            throw pageFailure;
        }
    }

    private const string Counted = """[{ "page": { "limit": 1, "includeTotalCount": true } }]""";

    [Fact]
    public async Task A_failed_page_cancels_the_count_that_runs_beside_it()
    {
        var runner = new FailingPageRunner(CommandFailure(292, "Sort exceeded memory limit"));
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());

        using var caller = new CancellationTokenSource();
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, Counted), BindHost.Context(), caller.Token);

        outcome.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Status.Should().Be(422);
        runner.CountToken.IsCancellationRequested.Should().BeTrue("the count has nobody left to answer");
        caller.IsCancellationRequested.Should().BeFalse("the caller's own token is not the engine's to cancel");
    }

    [Fact]
    public async Task A_count_that_fails_after_the_page_did_is_observed()
    {
        const string marker = "count failed after the page";
        var unobserved = new List<string>();

        void Record(object? sender, UnobservedTaskExceptionEventArgs args) =>
            unobserved.AddRange(args.Exception.InnerExceptions.Select(inner => inner.Message));

        TaskScheduler.UnobservedTaskException += Record;

        try
        {
            await RunAndAbandonAsync(marker);

            for (var pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Record;
        }

        unobserved.Should().NotContain(marker);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task RunAndAbandonAsync(string marker)
    {
        var runner = new FailingPageRunner(CommandFailure(292, "Sort exceeded memory limit"));
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());

        await engine.ExecuteAsync(BindHost.Request(Order, Counted), BindHost.Context());

        runner.Count.SetException(new InvalidOperationException(marker));

        // The continuation that reads the failure runs on the pool.
        await Task.Delay(50);
    }

    [Fact]
    public async Task A_bad_value_that_is_not_about_a_pattern_stays_a_fault()
    {
        var refusal = await Refused(new FakeAggregateRunner { Fail = CommandFailure(2, "timezone is not recognised") }, "[]");

        refusal.Status.Should().Be(500);
        refusal.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.InternalError);
    }
}
