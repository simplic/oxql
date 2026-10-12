using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
/// An owner that was reached and answered with an HTTP error is reported as that, under the
/// same codes as one that could not be reached: a rejected internal key or a body over the
/// owner's cap is not a network that is down.
/// </summary>
public class HostHardeningOwnerStatusTests
{
    private const string Resolve = """[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "page": { "limit": 10 } }]""";
    private const string SemiJoin = """[{ "resolve": { "path": "vehicleId", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "V-1" } } }, { "page": { "limit": 10 } }]""";

    /// <summary>An owner that answers every batch with one HTTP status, as the host's client reports it.</summary>
    private sealed class AnsweringWith(HttpStatusCode? status) : IRemoteQueryClient
    {
        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The owner did not answer with success.", null, status);

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static async Task<QueryOutcome> RunAsync(HttpStatusCode? status, string pipeline)
    {
        var options = BindHost.Options();
        var runner = new FakeAggregateRunner
        {
            PageRows =
            [
                new BsonDocument
                {
                    ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
                    ["Number"] = "a",
                    ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
                    ["ContactNumber"] = "c1",
                },
            ],
        };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, new AnsweringWith(status), cache: new OwnerFetchCache(options));

        return await engine.ExecuteAsync(BindHost.Request("probe.order", pipeline), BindHost.Context());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "413")]
    [InlineData(HttpStatusCode.BadRequest, "400")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task A_resolve_whose_owner_answered_with_an_error_names_the_status(HttpStatusCode status, string text)
    {
        var result = (await RunAsync(status, Resolve)).Should().BeOfType<QueryOutcome.Success>().Subject.Result;
        var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;

        diagnostic.Code.Should().Be(Codes.ResolveUnreachable, "the code list is closed; the message tells the two causes apart");
        diagnostic.Message.Should().Contain("answered with HTTP " + text).And.NotContain("could not be reached");
        diagnostic.Params!.Keys.Should().BeEquivalentTo(["service", "aliases"]);
        result.Items[0]!["contact"].Should().BeNull();
    }

    [Fact]
    public async Task A_resolve_whose_owner_gave_no_answer_at_all_is_still_unreachable()
    {
        var result = (await RunAsync(null, Resolve)).Should().BeOfType<QueryOutcome.Success>().Subject.Result;

        result.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("could not be reached");
    }

    [Fact]
    public async Task A_semi_join_whose_owner_answered_with_an_error_names_the_status()
    {
        var refusal = (await RunAsync(HttpStatusCode.Unauthorized, SemiJoin)).Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Errors![0].Code.Should().Be(Codes.ResolveUnavailable);
        refusal.Errors[0].Message.Should().Contain("answered the semi-join with HTTP 401");
    }

    [Fact]
    public async Task A_semi_join_whose_owner_gave_no_answer_at_all_says_so()
    {
        var refusal = (await RunAsync(null, SemiJoin)).Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Errors![0].Code.Should().Be(Codes.ResolveUnavailable);
        refusal.Errors[0].Message.Should().Contain("did not answer the semi-join");
    }

    /// <summary>An owner client that fails with a text nobody may read in a log.</summary>
    private sealed class Faulting : IRemoteQueryClient
    {
        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the answer of https://owner/?key=secret-token was no JSON");

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Recorder : ILogger<MongoQueryEngine>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    [Fact]
    public async Task An_owner_call_that_faults_is_logged_by_service_and_kind_of_fault_and_never_by_its_text()
    {
        var options = BindHost.Options();
        var log = new Recorder();
        var runner = new FakeAggregateRunner
        {
            PageRows = [new BsonDocument { ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard), ["Number"] = "a", ["ContactNumber"] = "c1" }],
        };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, new Faulting(), log, cache: new OwnerFetchCache(options));

        var outcome = await engine.ExecuteAsync(BindHost.Request("probe.order", Resolve), BindHost.Context() with { CorrelationId = "corr-7" });

        outcome.Should().BeOfType<QueryOutcome.Success>().Which.Result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Codes.ResolveUnreachable);

        var (level, message, exception) = log.Entries.Should().ContainSingle(entry => entry.Message.StartsWith("OxQL owner call failed")).Subject;

        level.Should().Be(LogLevel.Warning);
        message.Should().Contain("service=crm").And.Contain("fault=InvalidOperationException").And.Contain("correlation=corr-7").And.Contain(BindHost.Organisation.ToString());
        message.Should().NotContain("secret-token").And.NotContain("c1", "neither the client's text nor a key is written");
        exception.Should().BeNull("another service's client may carry what it sent in its exception");
    }
}
