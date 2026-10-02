using System.Net;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// A strict request's truncation probes (DESIGN §3.4.3) are sent with the page and the count, not
/// after them: a probe reads the rows up to a truncation and needs nothing of the page. Sent at the
/// same moment under the same <c>maxTimeMS</c>, they share the request's deadline, so the time limit
/// is a ceiling for the whole request and not a budget per probe; and a probe that runs out of it is
/// the request's <c>QUERY_TIMEOUT</c>.
/// </summary>
public class StrictProbeTests
{
    private const string Customer = "probe.customer";

    private const string HiddenByAMatch = """
        [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "limit": 2 } },
         { "match": { "orders.number": { "eq": "x" } } },
         { "page": { "includeTotalCount": true } }]
        """;

    /// <summary>Holds the page back until the probe has been sent, and answers each aggregate by what it is.</summary>
    private sealed class ProbeRunner : IAggregateRunner
    {
        private readonly TaskCompletionSource probeSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Kind, AggregateRunOptions Options)> Calls { get; } = [];

        public Exception? ProbeFails { get; set; }

        public bool ProbeFinds { get; set; }

        /// <summary>Whether the page and the count wait for the probe; off for a request that sends none.</summary>
        public bool Hold { get; set; } = true;

        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            var kind = stages[^1].Contains("$count") ? "count" : stages[^1].Contains("$project") && stages[^2].Contains("$limit") && stages[^2]["$limit"] == 1 ? "probe" : "page";

            lock (Calls)
                Calls.Add((kind, options));

            if (kind == "probe")
            {
                probeSent.TrySetResult();

                if (ProbeFails is not null)
                    throw ProbeFails;

                return ProbeFinds ? [new BsonDocument("_id", 1)] : [];
            }

            // The page answers only once the probe is on its way: sent after the page, it never would be.
            if (Hold)
                await probeSent.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            return kind == "count" ? [new BsonDocument("n", 0)] : [];
        }
    }

    private static (MongoQueryEngine Engine, ProbeRunner Runner) Host()
    {
        var runner = new ProbeRunner();
        var options = BindHost.Options();

        return (new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options), runner);
    }

    private static Task<QueryOutcome> RunAsync(MongoQueryEngine engine, bool strict = true) =>
        engine.ExecuteAsync(BindHost.Request(Customer, HiddenByAMatch) with { Strict = strict ? true : null }, BindHost.Context());

    [Fact]
    public async Task The_probe_is_sent_with_the_page_and_the_count_under_the_same_time_limit()
    {
        var (engine, runner) = Host();

        var outcome = await RunAsync(engine);

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");
        runner.Calls.Select(call => call.Kind).Should().BeEquivalentTo(["page", "count", "probe"]);
        runner.Calls.Select(call => call.Options.MaxTimeMs).Distinct().Should().ContainSingle("one deadline for the request, not a budget per probe");
        runner.Calls.Single(call => call.Kind == "probe").Options.BatchSize.Should().BeNull("a probe returns one row");
    }

    [Fact]
    public async Task A_request_that_is_not_strict_sends_no_probe()
    {
        var (engine, runner) = Host();

        runner.Hold = false;

        (await RunAsync(engine, strict: false)).Should().BeOfType<QueryOutcome.Success>();

        runner.Calls.Select(call => call.Kind).Should().BeEquivalentTo(["page", "count"]);
    }

    [Fact]
    public async Task A_probe_that_finds_a_truncated_row_refuses_the_request()
    {
        var (engine, runner) = Host();
        runner.ProbeFinds = true;

        var refusal = (await RunAsync(engine)).Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Status.Should().Be(422);
        refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.LookupTruncated);
    }

    [Fact]
    public async Task A_probe_that_runs_out_of_time_is_the_requests_timeout()
    {
        var (engine, runner) = Host();
        runner.ProbeFails = new MongoExecutionTimeoutException(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))), "operation exceeded time limit");

        var refusal = (await RunAsync(engine)).Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Status.Should().Be(504);
        refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.QueryTimeout);
    }
}
