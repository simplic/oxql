using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The answer of a request that ends in <c>values</c> (DG2 set filters), over the one document its
/// aggregate answers: each value written as a row writes the member, one more than the limit as
/// <c>hasNextPage</c> without a cursor, the rows read against the bound as <c>VALUES_SCAN_CAPPED</c>,
/// one command of kind <c>values</c> under the request's time ceiling.
/// </summary>
public class ValuesExecutionTests
{
    private const string Order = "probe.order";

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner) Host(BsonDocument? answer, Action<FakeAggregateRunner>? configure = null)
    {
        var runner = new FakeAggregateRunner { PageRows = answer is null ? [] : [answer] };

        configure?.Invoke(runner);

        return (new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options(), includeErrorDetails: true), runner);
    }

    /// <summary>The document the aggregate answers: the rows read, and the values with the rows of each.</summary>
    private static BsonDocument Answer(long read, params (BsonValue Value, int Count)[] values) => new()
    {
        ["t"] = read < 0 ? [] : new BsonArray { new BsonDocument { ["_id"] = BsonNull.Value, ["r"] = read } },
        ["v"] = new BsonArray(values.Select(value => new BsonDocument { ["_id"] = value.Value, ["c"] = value.Count })),
    };

    private static async Task<QueryResult> Run(MongoQueryEngine engine, string values, string? extra = null, RequestContext? context = null)
    {
        var request = BindHost.Parse($$"""{ "entityType": "{{Order}}", {{extra}} "pipeline": [{ "values": { {{values}} } }] }""");
        var outcome = await engine.ExecuteAsync(request, context ?? BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static string Items(QueryResult result) => new JsonArray(result.Items.Select(item => item?.DeepClone()).ToArray()).ToJsonString();

    [Fact]
    public async Task Each_value_is_a_row_of_value_and_count_and_a_list_that_ends_is_its_own_count()
    {
        var (engine, runner) = Host(Answer(40, ("Open", 25), (BsonNull.Value, 10), ("closed", 5)));
        var result = await Run(engine, """ "path": "number", "counts": true """);

        Items(result).Should().Be("""[{"value":"Open","count":25},{"value":null,"count":10},{"value":"closed","count":5}]""", "null is a value like any other, and a count is a JSON number");
        result.PageInfo.HasNextPage.Should().BeFalse();
        result.PageInfo.NextCursor.Should().BeNull();
        result.PageInfo.Count.Should().BeEquivalentTo(new CountInfo { State = CountInfo.Exact, Value = 3 }, "the envelope counts what it lists: the values");
        result.PageInfo.TotalCount.Should().BeNull();
        result.Diagnostics.Should().BeNull();

        runner.Calls.Should().ContainSingle("one aggregate answers it").Which.Stages[^1].Contains("$facet").Should().BeTrue();
    }

    [Fact]
    public async Task Without_counts_a_row_carries_the_value_alone()
    {
        var (engine, _) = Host(Answer(40, ("a", 30), ("b", 10)));

        Items(await Run(engine, """ "path": "number" """)).Should().Be("""[{"value":"a"},{"value":"b"}]""");
    }

    [Fact]
    public async Task A_value_is_written_as_a_row_writes_the_member()
    {
        var day = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Items(await Run(Host(Answer(5, (1, 3), (0, 2))).Engine, """ "path": "state", "counts": true """)).Should().Be("""[{"value":1,"count":3},{"value":0,"count":2}]""", "an enum travels as its number");
        Items(await Run(Host(Answer(5, (day, 5))).Engine, """ "path": "day" """)).Should().Be("""[{"value":"2026-03-01"}]""");
        Items(await Run(Host(Answer(5, (day.AddHours(8), 5))).Engine, """ "path": "when" """)).Should().Be("""[{"value":"2026-03-01T08:00:00Z"}]""");
        Items(await Run(Host(Answer(5, (new BsonBinaryData(id, GuidRepresentation.Standard), 5))).Engine, """ "path": "customerId" """)).Should().Be($$"""[{"value":"{{id}}"}]""");
        Items(await Run(Host(Answer(5, (BsonDecimal128.Create(12.5m), 5))).Engine, """ "path": "amount" """)).Should().Be("""[{"value":"12.5"}]""", "a decimal is its canonical text");
        Items(await Run(Host(Answer(5, (5_000_000_000L, 5))).Engine, """ "path": "big" """)).Should().Be("""[{"value":"5000000000"}]""", "a long is a string");
        Items(await Run(Host(Answer(5, (true, 4), (false, 1))).Engine, """ "path": "flag", "counts": true """)).Should().Be("""[{"value":true,"count":4},{"value":false,"count":1}]""");
        Items(await Run(Host(Answer(7, (2, 4), (BsonNull.Value, 3))).Engine, """ "path": "items.quantity", "counts": true """)).Should().Be("""[{"value":2,"count":4},{"value":null,"count":3}]""");
    }

    [Fact]
    public async Task One_value_more_than_the_limit_says_the_list_goes_on_and_no_cursor_continues_it()
    {
        var (engine, runner) = Host(Answer(40, ("a", 9), ("b", 8), ("c", 7)));
        var result = await Run(engine, """ "path": "number", "limit": 2 """);

        Items(result).Should().Be("""[{"value":"a"},{"value":"b"}]""");
        result.PageInfo.HasNextPage.Should().BeTrue();
        result.PageInfo.NextCursor.Should().BeNull("the caller narrows with search; nothing pages a list of values");
        result.PageInfo.Count.Should().BeEquivalentTo(new CountInfo { State = CountInfo.AtLeast, Value = 3, StoppedBy = CountInfo.ByPage });
        runner.Calls.Single().Stages[^1]["$facet"]["v"].AsBsonArray[^1].ShouldBeBson(new BsonDocument("$limit", 3));
    }

    [Fact]
    public async Task As_many_rows_read_as_the_bound_lets_through_is_said_and_fewer_is_not()
    {
        var (capped, _) = Host(Answer(300, ("a", 200), ("b", 100)));
        var result = await Run(capped, """ "path": "number", "counts": true, "scan": 300 """);
        var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;

        diagnostic.Code.Should().Be(Codes.ValuesScanCapped);
        diagnostic.Stage.Should().Be(0);
        diagnostic.Path.Should().Be("number");
        diagnostic.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["scan"] = 300 });
        diagnostic.Message.Should().Be("The values are those among the first 300 rows that reach the stage; more rows may match, and a count is the count within them. Narrow the request's filter to see the values of fewer rows.");
        Items(result).Should().Be("""[{"value":"a","count":200},{"value":"b","count":100}]""", "the rows are answered all the same");
        Basis.OfCode[Codes.ValuesScanCapped].Should().Be(Basis.Data);

        (await Run(Host(Answer(299, ("a", 299))).Engine, """ "path": "number", "scan": 300 """)).Diagnostics.Should().BeNull();

        // The default bound, and a search that keeps no value: the rows read are known whatever the list holds.
        var silent = await Run(Host(Answer(50_000)).Engine, """ "path": "number", "search": "zz" """);

        silent.Items.Should().BeEmpty();
        silent.Diagnostics.Should().ContainSingle().Which.Message.Should().Be("The values are those among the first 50000 rows that reach the stage; more rows may match. Narrow the request's filter to see the values of fewer rows.");
    }

    [Fact]
    public async Task No_row_at_all_answers_an_empty_list()
    {
        foreach (var answer in new[] { null, Answer(-1), new BsonDocument() })
        {
            var result = await Run(Host(answer).Engine, """ "path": "number", "counts": true """);

            result.Items.Should().BeEmpty();
            result.PageInfo.HasNextPage.Should().BeFalse();
            result.PageInfo.Count.Should().BeEquivalentTo(new CountInfo { State = CountInfo.Exact, Value = 0 });
            result.Diagnostics.Should().BeNull();
        }
    }

    [Fact]
    public async Task It_is_one_command_of_kind_values_under_the_requests_time_ceiling()
    {
        var (engine, runner) = Host(Answer(4, ("a", 4)));
        var result = await Run(engine, """ "path": "number" """, extra: """ "maxTimeMs": 750, "timing": true, """);
        var list = result.Timing!["db"]!["list"]!.AsArray();

        list.Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("values");
        list[0]!["collection"]!.GetValue<string>().Should().Be(Order);
        list[0]!["docs"]!.GetValue<int>().Should().Be(1, "the aggregate answers one document");
        result.Timing["db"]!["commands"]!.GetValue<int>().Should().Be(1);
        result.Timing["rows"]!.GetValue<int>().Should().Be(1);
        runner.Calls.Single().Options.MaxTimeMs.Should().Be(750, "the request's own ceiling");
        result.Measured!.ServerTiming().Should().Contain("db;dur=").And.Contain("desc=\"1 cmd / 1 rt\"");

        // Without one the host's ceiling holds, and a batch's rest lowers it.
        (engine, runner) = Host(Answer(4, ("a", 4)));
        await Run(engine, """ "path": "number" """);
        await Run(engine, """ "path": "number" """, context: BindHost.Context() with { MaxTimeMs = 1_200, CorrelationId = "corr-7" });

        runner.Calls.Select(call => call.Options.MaxTimeMs).Should().Equal(10_000, 1_200);
        runner.Calls[1].Options.Comment.Should().Be("corr-7");
        runner.Calls[0].Options.Collation.Should().NotBeNull("a string's values fold under the collation");
    }

    [Fact]
    public async Task What_the_database_refuses_is_the_refusal_of_any_request()
    {
        var connection = new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017)));

        async Task<Refusal> Refused(Exception failure)
        {
            var (engine, _) = Host(null, runner => runner.Fail = failure);
            var outcome = await engine.ExecuteAsync(BindHost.Request(Order, """[{ "values": { "path": "number" } }]"""), BindHost.Context());

            return outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;
        }

        var timeout = await Refused(new MongoExecutionTimeoutException(connection, "operation exceeded time limit"));

        timeout.Status.Should().Be(504);
        timeout.Errors![0].Code.Should().Be(Codes.QueryTimeout);

        var memory = await Refused(new MongoCommandException(connection, "Exceeded memory limit", new BsonDocument("aggregate", "orders"), new BsonDocument { ["ok"] = 0, ["code"] = 16945 }));

        memory.Errors![0].Code.Should().Be(Codes.QueryTooExpensive);
        (await Refused(new InvalidOperationException("boom"))).Errors![0].Code.Should().Be(Codes.InternalError);
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_a_cancellation_and_never_a_refusal()
    {
        using var gone = new CancellationTokenSource();
        var (engine, _) = Host(null, runner => runner.Fail = new MongoConnectionException(new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017))), "reset"));

        await gone.CancelAsync();

        var run = () => engine.ExecuteAsync(BindHost.Request(Order, """[{ "values": { "path": "number" } }]"""), BindHost.Context(), gone.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Contract_1_runs_nothing_for_it()
    {
        var (engine, runner) = Host(Answer(4, ("a", 4)));
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, """[{ "values": { "path": "number" } }]"""), BindHost.Context(contract: 1));

        outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal.Errors!.Single().Code.Should().Be(Codes.UnknownStage);
        runner.Calls.Should().BeEmpty();
    }
}
