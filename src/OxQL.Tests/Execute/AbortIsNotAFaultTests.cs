using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.AspNetCore;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// An abort is not a fault. Once the caller has given up, a call in flight may end in something
/// other than a cancellation: the transport resets the connection and throws what it throws. The
/// engine then logs no fault and builds no <c>INTERNAL_ERROR</c> for a caller that is gone; the
/// cancellation travels, carrying what happened as its inner exception.
/// </summary>
public class AbortIsNotAFaultTests
{
    private const string Order = "probe.order";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>A runner whose aggregate ends as a reset connection would end it, the moment the caller gives up.</summary>
    private sealed class ResetRunner(CancellationTokenSource caller, bool reset) : IAggregateRunner
    {
        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (!reset)
                return
                [
                    new BsonDocument
                    {
                        ["_id"] = new BsonBinaryData(Id1, GuidRepresentation.Standard),
                        ["Number"] = "a",
                        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
                        ["ContactNumber"] = "c1",
                    },
                ];

            await caller.CancelAsync();

            throw new IOException("Unable to read data from the transport connection.");
        }
    }

    /// <summary>An owner client whose call ends as a reset connection would end it, the moment the caller gives up.</summary>
    private sealed class ResetClient(CancellationTokenSource caller) : IRemoteQueryClient
    {
        public async Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            await caller.CancelAsync();

            throw new HttpRequestException("The connection was reset.");
        }

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static (MongoQueryEngine Engine, LogCapture Logs) Host(IAggregateRunner runner, IRemoteQueryClient? client = null)
    {
        var options = BindHost.Options();
        var logs = new LogCapture();
        var engine = new MongoQueryEngine(
            new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client,
            LoggerFactory.Create(logging => logging.AddProvider(logs)).CreateLogger<MongoQueryEngine>(), cache: new OwnerFetchCache(options));

        return (engine, logs);
    }

    [Fact]
    public async Task An_aggregate_the_abort_tore_down_is_the_callers_cancellation_not_an_engine_fault()
    {
        using var caller = new CancellationTokenSource();
        var (engine, logs) = Host(new ResetRunner(caller, reset: true));

        var run = () => engine.ExecuteAsync(BindHost.Request(Order, """[{ "page": { "includeTotalCount": true } }]"""), BindHost.Context(), caller.Token);

        (await run.Should().ThrowAsync<OperationCanceledException>()).WithInnerException<IOException>();
        logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task The_same_failure_without_an_abort_is_still_the_engines_fault()
    {
        using var caller = new CancellationTokenSource();
        var (engine, logs) = Host(new FakeAggregateRunner { Fail = new IOException("Unable to read data from the transport connection.") });

        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, "[]"), BindHost.Context(), caller.Token);

        outcome.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Status.Should().Be(500);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task An_owner_call_the_abort_tore_down_is_the_callers_cancellation_not_an_unreachable_owner()
    {
        using var caller = new CancellationTokenSource();
        var (engine, logs) = Host(new ResetRunner(caller, reset: false), new ResetClient(caller));

        var run = () => engine.ExecuteAsync(
            BindHost.Request(Order, """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }, { "page": { "limit": 10 } }]"""),
            BindHost.Context(),
            caller.Token);

        (await run.Should().ThrowAsync<OperationCanceledException>()).WithInnerException<HttpRequestException>();
        logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }
}
