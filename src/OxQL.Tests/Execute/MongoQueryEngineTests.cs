using System.Net;
using System.Text.Json;
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

/// <summary>The executor over fixture rows: limit + 1, the count cap, error mapping, cursors, and the wire encoding of the rows.</summary>
public class MongoQueryEngineTests
{
    private const string Order = "probe.order";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Id3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner) Host(Action<FakeAggregateRunner>? configure = null, Action<OxQLOptions>? options = null, bool details = false)
    {
        var runner = new FakeAggregateRunner();

        configure?.Invoke(runner);

        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options(options), includeErrorDetails: details);

        return (engine, runner);
    }

    private static BsonDocument Row(Guid id, string number, int count = 1) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["Number"] = number,
        ["Count"] = count,
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
    };

    private static async Task<QueryResult> Success(MongoQueryEngine engine, string pipeline, RequestContext? context = null)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), context ?? BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static async Task<Refusal> Refused(MongoQueryEngine engine, string pipeline, RequestContext? context = null)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), context ?? BindHost.Context());

        return outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;
    }

    [Fact]
    public async Task Limit_plus_one_decides_the_next_page_and_the_cursor_carries_the_last_row()
    {
        var (engine, runner) = Host(runner => runner.PageRows = [Row(Id1, "a"), Row(Id2, "b"), Row(Id3, "c")]);
        var result = await Success(engine, """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]""");

        result.Items.Should().HaveCount(2);
        result.PageInfo.HasNextPage.Should().BeTrue();
        result.PageInfo.NextCursor.Should().NotBeNull();
        result.PageInfo.TotalCount.Should().BeNull();
        runner.Calls.Should().ContainSingle().Which.Stages[^1].ShouldBeBson(new BsonDocument("$limit", 3));

        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]""");
        var payload = BindHost.Cursors.Decode(result.PageInfo.NextCursor!, bound.Fingerprint);

        payload.Should().NotBeNull("the cursor is bound to this query");
        payload!.Fields.Select(field => field.Wire).Should().Equal("number", "_id");
        payload.Fields[0].Value.Should().Be(new BsonString("b"));
        payload.Fields[1].Value.Should().Be(new BsonBinaryData(Id2, GuidRepresentation.Standard));

        // The next page binds the cursor and compiles the keyset predicate into the scope match.
        runner.PageRows = [Row(Id3, "c")];
        var next = await Success(engine, $$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{result.PageInfo.NextCursor}}" } }]""");

        next.PageInfo.HasNextPage.Should().BeFalse();
        next.PageInfo.NextCursor.Should().BeNull();
        runner.Calls[1].Stages[0]["$match"].AsBsonDocument.Contains("$and").Should().BeTrue();
    }

    [Fact]
    public async Task A_grouped_page_continues_by_offset()
    {
        var (engine, runner) = Host(runner => runner.PageRows = [new BsonDocument("st", 0), new BsonDocument("st", 1), new BsonDocument("st", 2)]);
        var result = await Success(engine, """[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 2, "includeTotalCount": true } }]""");

        result.Items.Should().HaveCount(2);
        result.Items[0]!["st"]!.GetValue<int>().Should().Be(0);
        result.PageInfo.HasNextPage.Should().BeTrue();

        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 2, "includeTotalCount": true } }]""");
        var payload = BindHost.Cursors.Decode(result.PageInfo.NextCursor!, bound.Fingerprint)!;

        payload.Mode.Should().Be(PagingMode.Offset);
        payload.Offset.Should().Be(2);

        runner.PageRows = [new BsonDocument("st", 2)];
        var next = await Success(engine, $$"""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 2, "includeTotalCount": true, "cursor": "{{result.PageInfo.NextCursor}}" } }]""");

        next.Items.Should().ContainSingle();
        next.PageInfo.HasNextPage.Should().BeFalse();
        runner.Calls.Last(call => !call.Stages[^1].Contains("$count")).Stages.Should().Contain(new BsonDocument("$skip", 2));
    }

    [Fact]
    public async Task The_count_runs_beside_the_page_and_is_capped()
    {
        var (engine, runner) = Host(runner =>
        {
            runner.PageRows = [Row(Id1, "a")];
            runner.Count = 42;
        });
        var result = await Success(engine, """[{ "page": { "includeTotalCount": true } }]""");

        result.PageInfo.TotalCount.Should().Be(42);
        result.PageInfo.TotalCountCapped.Should().BeFalse();
        result.Diagnostics.Should().BeNull();
        runner.Calls.Should().HaveCount(2);
        runner.Calls[1].Stages[^1].ShouldBeBson(new BsonDocument("$count", "n"));
        runner.Calls[1].Stages[^2].ShouldBeBson(new BsonDocument("$limit", 100_001));

        runner.Count = 100_001;
        var capped = await Success(engine, """[{ "page": { "includeTotalCount": true } }]""");

        capped.PageInfo.TotalCount.Should().Be(100_000);
        capped.PageInfo.TotalCountCapped.Should().BeTrue();
        capped.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.TotalCountCapped);

        runner.Count = null;
        (await Success(engine, """[{ "page": { "includeTotalCount": true } }]""")).PageInfo.TotalCount.Should().Be(0, "no count row means no match");
    }

    [Fact]
    public async Task Execution_bounds_come_from_the_options_and_the_request()
    {
        var (engine, runner) = Host(options: options =>
        {
            options.Execution.MaxTimeMs = 120_000;
            options.Execution.AllowDiskUse = false;
        });

        await Success(engine, "[]");
        runner.Calls[0].Options.Should().Be(new AggregateRunOptions(60_000, false), "the ceiling clamps");

        await Success(engine, "[]", BindHost.Context(BindHost.Options(), organisation: BindHost.Organisation) with { MaxTimeMs = 500 });
        runner.Calls[1].Options.MaxTimeMs.Should().Be(500, "a batch ceiling narrows");
    }

    [Fact]
    public async Task Driver_errors_map_to_the_refusal_classes()
    {
        var connection = new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017)));

        var (timeout, _) = Host(runner => runner.Fail = new MongoExecutionTimeoutException(connection, "operation exceeded time limit"));
        var refusal = await Refused(timeout, "[]");

        refusal.Status.Should().Be(504);
        refusal.Errors![0].Code.Should().Be(Codes.QueryTimeout);

        var (code50, _) = Host(runner => runner.Fail = new MongoCommandException(connection, "MaxTimeMSExpired", new BsonDocument("aggregate", "orders"), new BsonDocument { ["ok"] = 0, ["code"] = 50, ["codeName"] = "MaxTimeMSExpired" }));
        (await Refused(code50, "[]")).Errors![0].Code.Should().Be(Codes.QueryTimeout);

        var (memory, _) = Host(runner => runner.Fail = new MongoCommandException(connection, "QueryExceededMemoryLimitNoDiskUseAllowed", new BsonDocument("aggregate", "orders"), new BsonDocument { ["ok"] = 0, ["code"] = 292 }));
        var expensive = await Refused(memory, "[]");

        expensive.Status.Should().Be(422);
        expensive.Errors![0].Code.Should().Be(Codes.QueryTooExpensive);

        var (fault, _) = Host(runner => runner.Fail = new InvalidOperationException("boom"));
        var internalError = await Refused(fault, "[]");

        internalError.Status.Should().Be(500);
        internalError.Errors.Should().BeNull("details stay inside unless the host allows");

        var (verbose, _) = Host(runner => runner.Fail = new InvalidOperationException("boom"), details: true);
        (await Refused(verbose, "[]")).Errors![0].Message.Should().Be("boom");
    }

    [Fact]
    public async Task A_remote_resolve_is_refused_without_a_remote_client()
    {
        var (engine, _) = Host();
        var refusal = await Refused(engine, """[{ "resolve": { "path": "contactNumber", "as": "contact" } }]""");

        refusal.Status.Should().Be(422);
        refusal.Errors![0].Code.Should().Be(Codes.ResolveUnavailable);
        engine.RemoteResolve.Should().BeFalse();
    }

    [Fact]
    public async Task A_binding_refusal_never_reaches_the_database()
    {
        var (engine, runner) = Host();
        var refusal = await Refused(engine, """[{ "match": { "nothing": { "eq": 1 } } }]""");

        refusal.Status.Should().Be(400);
        runner.Calls.Should().BeEmpty();

        var denied = await engine.ExecuteAsync(BindHost.Request(Order, "[]"), BindHost.Context(organisation: Guid.Empty));

        denied.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Status.Should().Be(403);
    }

    [Fact]
    public async Task Explain_returns_the_bound_form_and_the_stages_without_running()
    {
        var (engine, runner) = Host();
        var outcome = await engine.ExplainAsync(BindHost.Request(Order, """[{ "match": { "number": { "eq": "x" } } }, { "page": { "includeTotalCount": true } }]"""), BindHost.Context());
        var explain = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        explain.Bound["entity"]!.GetValue<string>().Should().Be(Order);
        explain.Stages.Should().HaveCount(4);
        explain.Count.Should().HaveCount(4);
        runner.Calls.Should().BeEmpty();

        var refused = await engine.ExplainAsync(BindHost.Request(Order, """[{ "match": { "nothing": { "eq": 1 } } }]"""), BindHost.Context());

        refused.Should().BeOfType<ExplainOutcome.Refused>();
    }

    [Fact]
    public async Task Rows_are_encoded_in_the_wire_encoding_at_every_depth()
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Id1, GuidRepresentation.Standard),
            ["Number"] = "N-1",
            ["Count"] = 3,
            ["Big"] = 9007199254740993L,
            ["Ratio"] = 0.5,
            ["Amount"] = new BsonDecimal128(new Decimal128(12.50m)),
            ["Flag"] = true,
            ["Day"] = new BsonDateTime(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            ["When"] = new BsonDateTime(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            ["Span"] = "01:30:00",
            ["Initial"] = 97,
            ["State"] = 1,
            ["StateName"] = "Shipped",
            ["MoneyText"] = "1.5",
            ["GuidText"] = Id2.ToString(),
            ["Blob"] = new BsonBinaryData([1, 2, 3]),
            ["ShipTo"] = new BsonDocument { ["Street"] = "Main", ["City"] = "Bonn" },
            ["Items"] = new BsonArray
            {
                new BsonDocument { ["_id"] = new BsonBinaryData(Id3, GuidRepresentation.Standard), ["Quantity"] = 2, ["Price"] = new BsonDocument { ["Net"] = new BsonDecimal128(new Decimal128(3m)), ["Currency"] = "EUR" }, ["Notes"] = new BsonArray { "a" } },
            },
            ["Tags"] = new BsonArray { "x", "y" },
            ["Prices"] = new BsonDocument { ["eur"] = new BsonDocument { ["Net"] = "4.5", ["Currency"] = "EUR" } },
            ["PriceList"] = new BsonArray { new BsonDocument { ["k"] = "usd", ["v"] = new BsonDocument { ["Net"] = new BsonDecimal128(new Decimal128(5m)) } } },
            ["Addon"] = new BsonDocument { ["SoloplanNr"] = 7L, ["When"] = new BsonDateTime(new DateTime(2024, 5, 6, 0, 0, 0, DateTimeKind.Utc)), ["Dec"] = new BsonDocument { ["_t"] = "System.Decimal", ["_v"] = new BsonDecimal128(new Decimal128(1m)) } },
            ["x"] = "renamed",
            ["QRCode"] = "qr",
            ["Location"] = new BsonArray { 8.5, 50.1 },
            ["Anything"] = new BsonDocument("k", 1),
            ["Note"] = BsonNull.Value,
            ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
            ["Extra"] = "not in the shape",
        };
        var (engine, _) = Host(runner => runner.PageRows = [row]);
        var result = await Success(engine, "[]");
        var item = result.Items.Should().ContainSingle().Subject!.AsObject();
        var json = item.ToJsonString();

        item["id"]!.GetValue<string>().Should().Be(Id1.ToString());
        item["number"]!.GetValue<string>().Should().Be("N-1");
        item["count"]!.GetValue<int>().Should().Be(3);
        item["big"]!.GetValue<string>().Should().Be("9007199254740993", "long is a string on the wire");
        item["ratio"]!.GetValue<double>().Should().Be(0.5);
        item["amount"]!.GetValue<string>().Should().Be("12.5", "decimal is a string on the wire");
        item["flag"]!.GetValue<bool>().Should().BeTrue();
        item["day"]!.GetValue<string>().Should().Be("2024-01-02");
        item["when"]!.GetValue<string>().Should().Be("2024-01-02T03:04:05Z");
        item["span"]!.GetValue<string>().Should().Be("PT1H30M");
        item["initial"]!.GetValue<string>().Should().Be("a", "a char is stored as its code point");
        item["state"]!.GetValue<int>().Should().Be(1, "enum as number");
        item["stateName"]!.GetValue<int>().Should().Be(1, "an enum stored as its name still travels as the number");
        item["moneyText"]!.GetValue<string>().Should().Be("1.5");
        item["guidText"]!.GetValue<string>().Should().Be(Id2.ToString());
        item["blob"]!.GetValue<string>().Should().Be("AQID");
        item["shipTo"]!["city"]!.GetValue<string>().Should().Be("Bonn");
        item["items"]![0]!["id"]!.GetValue<string>().Should().Be(Id3.ToString());
        item["items"]![0]!["price"]!["net"]!.GetValue<string>().Should().Be("3");
        item["items"]![0]!["notes"]![0]!.GetValue<string>().Should().Be("a");
        item["tags"]!.AsArray().Should().HaveCount(2);
        item["prices"]!["eur"]!["net"]!.GetValue<string>().Should().Be("4.5", "a decimal stored as a string stays a string");
        item["priceList"]!["usd"]!["net"]!.GetValue<string>().Should().Be("5", "the array-of-documents dictionary is an object on the wire");
        item["addon"]!["SoloplanNr"]!.GetValue<long>().Should().Be(7, "the bag is verbatim");
        item["addon"]!["When"]!.GetValue<string>().Should().Be("2024-05-06T00:00:00Z");
        item["addon"]!["Dec"]!["_v"]!.GetValue<string>().Should().Be("1");
        item["renamed"]!.GetValue<string>().Should().Be("renamed", "the wire name, not the element name");
        item["qrCode"]!.GetValue<string>().Should().Be("qr");
        item["location"]!.AsArray().Should().HaveCount(2, "unknown members pass through verbatim");
        item["anything"]!["k"]!.GetValue<int>().Should().Be(1);
        item.ContainsKey("note").Should().BeTrue();
        item["note"].Should().BeNull();
        item["organizationId"]!.GetValue<string>().Should().Be(BindHost.Organisation.ToString());
        json.Should().NotContain("Extra").And.NotContain("QRCode").And.NotContain("\"x\":").And.NotContain("_id");
    }

    [Fact]
    public async Task Shaped_rows_follow_the_fold()
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Id1, GuidRepresentation.Standard),
            ["Number"] = "N-1",
            ["Items"] = new BsonDocument { ["_id"] = new BsonBinaryData(Id3, GuidRepresentation.Standard), ["Quantity"] = 2 },
            ["it"] = new BsonDocument { ["_id"] = new BsonBinaryData(Id3, GuidRepresentation.Standard), ["Quantity"] = 2 },
            ["i"] = 0L,
            ["cust"] = new BsonDocument { ["_id"] = new BsonBinaryData(Id2, GuidRepresentation.Standard), ["Name"] = "Acme" },
            ["orders"] = new BsonArray { new BsonDocument { ["_id"] = new BsonBinaryData(Id2, GuidRepresentation.Standard), ["Number"] = "O" } },
        };
        var (engine, _) = Host(runner => runner.PageRows = [row]);
        var result = await Success(engine, """[{ "resolve": { "path": "customerId", "as": "cust" } }, { "unwind": { "path": "items", "as": "it", "includeIndex": "i" } }]""");
        var item = result.Items.Single()!.AsObject();

        item["items"]!["quantity"]!.GetValue<int>().Should().Be(2, "after the unwind the path means the element");
        item["it"]!["id"]!.GetValue<string>().Should().Be(Id3.ToString());
        item["i"]!.GetValue<int>().Should().Be(0, "the index is an int");
        item["cust"]!["name"]!.GetValue<string>().Should().Be("Acme");
        item.ContainsKey("orders").Should().BeFalse("no such root in this shape");

        var grouped = await Success(engine, """[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true }, "total": { "sum": "amount" } } } }]""");

        // The fake returns the same row; the encoder renders only the outputs it finds.
        grouped.Items.Single()!.AsObject().Count.Should().Be(0);

        var (engine2, _) = Host(runner => runner.PageRows = [new BsonDocument { ["st"] = 1, ["n"] = 4, ["total"] = new BsonDecimal128(new Decimal128(9.5m)) }]);
        var outputs = (await Success(engine2, """[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true }, "total": { "sum": "amount" } } } }]""")).Items.Single()!.AsObject();

        outputs["st"]!.GetValue<int>().Should().Be(1);
        outputs["n"]!.GetValue<string>().Should().Be("4", "a count is a long, and longs are strings on the wire");
        outputs["total"]!.GetValue<string>().Should().Be("9.5");
    }

    [Fact]
    public async Task A_projection_removes_members_from_the_rows_and_the_key_stays_unless_excluded()
    {
        var (engine, _) = Host(runner => runner.PageRows = [Row(Id1, "a", 5)]);
        var included = (await Success(engine, """[{ "project": { "number": 1 } }]""")).Items.Single()!.AsObject();

        included.Select(pair => pair.Key).Should().BeEquivalentTo("id", "number");

        var excluded = (await Success(engine, """[{ "project": { "number": 0, "id": 0 } }]""")).Items.Single()!.AsObject();

        excluded.Select(pair => pair.Key).Should().BeEquivalentTo("count", "organizationId");
    }

    [Fact]
    public void The_refusal_envelope_has_no_status_member_and_errors_carry_the_stage()
    {
        var refusal = Refusal.Validation([new QueryValidationError { Code = Codes.UnknownPath, Message = "m", Stage = 2, Path = "items.quantity" }]);
        var json = JsonSerializer.Serialize(refusal, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var node = JsonNode.Parse(json)!.AsObject();

        node["type"]!.GetValue<string>().Should().Be("validation_error");
        node.ContainsKey("status").Should().BeFalse();
        node["errors"]![0]!["stage"]!.GetValue<int>().Should().Be(2);
        node["errors"]![0]!["path"]!.GetValue<string>().Should().Be("items.quantity");
    }
}
