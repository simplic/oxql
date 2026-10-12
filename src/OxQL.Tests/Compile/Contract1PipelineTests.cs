using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using OxQL.Core.Engine;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// The rewrite path of contract 1 (a host with <c>Compat:Legacy:Always</c> off, and every internal
/// call): the aggregates a contract 1 request sends through the contract 2 engine are what they
/// were before the engine began to place stages for cost: every stage in the order written, a
/// projection before the sort it was written before, unwound rows and groups paged by offset, the
/// keyset predicate without a leading range. (By default a contract 1 request never reaches this
/// engine: the legacy pipeline answers it with the 1.1 engine's own aggregate, which
/// <c>LegacyGoldenTests</c> holds by its answers.)
/// <para>
/// The recorded pipelines (<c>Fixtures/contract1/rewrite/pipelines.json</c>) were captured by this test from
/// the engine as it was before that work, through the whole engine: the page
/// and the count aggregate of each request, the cursor it mints, and the aggregate of the page that
/// cursor continues. They are not re-recorded when contract 2 changes: a difference here is a
/// contract 1 caller's database load or cursor changing. <c>OXQL_RECORD_CONTRACT1=1</c> rewrites the
/// file, which is right only on a commit whose contract 1 output is known to be the reference.
/// </para>
/// </summary>
public class Contract1PipelineTests
{
    private const string RecordVariable = "OXQL_RECORD_CONTRACT1";

    private const string Order = "probe.order";

    private const string Customer = "probe.customer";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    /// <summary>The requests: a name, the entity, the stages before the page, the page.</summary>
    public static readonly TheoryData<string, string, string, string> Requests = new()
    {
        { "match-sort", Order, """{ "match": { "number": { "eq": "x" } } }, { "sort": [{ "number": "asc" }] }""", """{ "limit": 2 }""" },
        { "project-before-sort", Order, """{ "project": { "number": 1, "state": 1 } }, { "sort": [{ "number": "desc" }] }""", """{ "limit": 2 }""" },
        { "sort-before-project", Order, """{ "sort": [{ "when": "desc" }, { "number": "asc" }] }, { "project": { "number": 1, "shipTo.city": 1 } }""", """{ "limit": 2 }""" },
        { "project-drops-the-key", Order, """{ "project": { "addon": 0, "id": 0 } }""", """{ "limit": 2 }""" },
        { "project-match-count", Order, """{ "project": { "id": 1, "number": 1, "flag": 1 } }, { "match": { "flag": { "eq": false } } }""", """{ "limit": 2, "includeTotalCount": true }""" },
        { "offset", Order, """{ "sort": [{ "when": "desc" }] }""", """{ "limit": 2, "offset": 4 }""" },
        { "fold-option", Order, """{ "match": { "number": { "eq": "a", "options": { "ignoreCase": true } } } }, { "sort": [{ "number": "asc" }] }""", """{ "limit": 2 }""" },
        { "unwind-in-place", Order, """{ "unwind": { "path": "items" } }""", """{ "limit": 2 }""" },
        { "unwind-sorted", Order, """{ "unwind": { "path": "items", "as": "it", "includeIndex": "i", "preserveNull": true } }, { "sort": [{ "it.quantity": "desc" }] }""", """{ "limit": 2 }""" },
        { "unwind-match-project", Order, """{ "unwind": { "path": "items", "as": "it" } }, { "match": { "it.quantity": { "gt": 2 } } }, { "project": { "number": 1, "it.quantity": 1 } }, { "sort": [{ "number": "asc" }] }""", """{ "limit": 2, "includeTotalCount": true }""" },
        { "group", Order, """{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }""", """{ "limit": 2 }""" },
        { "group-sorted-count", Order, """{ "group": { "by": [{ "path": "state", "as": "st" }, { "path": "number", "as": "no" }], "fields": { "n": { "count": true }, "total": { "sum": "amount" } } } }, { "sort": [{ "n": "desc" }] }, { "project": { "st": 1, "n": 1 } }""", """{ "limit": 2, "includeTotalCount": true }""" },
        { "lookup-projected", Customer, """{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number"] } }, { "project": { "name": 1, "orders": 1 } }""", """{ "limit": 2 }""" },
        { "lookup-matched", Customer, """{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number"] } }, { "match": { "orders.number": { "eq": "x" } } }, { "project": { "id": 1, "name": 1 } }""", """{ "limit": 2, "includeTotalCount": true }""" },
        { "lookup-unwound", Customer, """{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number"] } }, { "unwind": { "path": "orders" } }, { "sort": [{ "name": "asc" }] }""", """{ "limit": 2 }""" },
        { "resolve-sorted", Order, """{ "resolve": { "path": "customerId", "as": "cust", "select": ["name"] } }, { "sort": [{ "cust.name": "asc" }] }""", """{ "limit": 2 }""" },
        { "resolve-projected", Order, """{ "resolve": { "path": "customerId", "as": "cust", "select": ["name"] } }, { "project": { "id": 1, "cust": 1 } }, { "sort": [{ "id": "asc" }] }""", """{ "limit": 2 }""" },
    };

    private static bool Recording => Environment.GetEnvironmentVariable(RecordVariable) is "1" or "true";

    private static string FixturePath([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.Combine(Path.GetDirectoryName(file)!, "..", "Fixtures", "contract1", "rewrite", "pipelines.json");

    private static readonly object Gate = new();

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task A_contract_1_request_sends_the_aggregates_it_always_sent(string name, string entity, string stages, string page)
    {
        var sent = await SentAsync(entity, stages, page);

        if (Recording)
        {
            lock (Gate)
            {
                var path = FixturePath();
                var file = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : [];

                file[name] = sent.DeepClone();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, new JsonObject(file.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }

        var recorded = JsonNode.Parse(File.ReadAllText(FixturePath()))!.AsObject();

        recorded.ContainsKey(name).Should().BeTrue($"'{name}' is recorded; a new case is recorded on the reference commit with {RecordVariable}=1");
        sent.ToJsonString().Should().Be(recorded[name]!.ToJsonString(), $"the aggregates and the cursor of '{name}' under contract 1 are what they were");
    }

    /// <summary>What the engine sends for the request and for the page its cursor continues: every aggregate as canonical extended JSON, and the cursor.</summary>
    private static async Task<JsonObject> SentAsync(string entity, string stages, string page)
    {
        var runner = new FakeAggregateRunner
        {
            Count = 40,
            PageRows = Enumerable.Range(1, 3).Select(index => new BsonDocument
            {
                ["_id"] = new BsonBinaryData(new Guid(index, 0, 0, new byte[8]), GuidRepresentation.Standard),
                ["Number"] = "n" + index,
                ["Name"] = "name " + index,
                ["When"] = new BsonDateTime(new DateTime(2026, 1, index, 0, 0, 0, DateTimeKind.Utc)),
                ["State"] = index,
                ["st"] = index,
                ["no"] = "n" + index,
                ["n"] = 10 - index,
                ["i"] = index,
                ["it"] = new BsonDocument("Quantity", index * 2),
                ["cust"] = new BsonDocument("Name", "customer " + index),
            }).ToList(),
        };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());
        var context = BindHost.Context(contract: 1) with { Internal = true };
        var first = await engine.ExecuteAsync(BindHost.Request(entity, $"[{stages}, {{ \"page\": {page} }}]"), context);

        first.Should().BeOfType<QueryOutcome.Success>(first is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        var cursor = ((QueryOutcome.Success)first).Result.PageInfo.NextCursor;
        var answer = new JsonObject { ["first"] = Calls(runner), ["cursor"] = cursor };

        if (cursor is not null)
        {
            runner.Calls.Clear();

            var next = await engine.ExecuteAsync(BindHost.Request(entity, $"[{stages}, {{ \"page\": {{ \"limit\": 2, \"cursor\": \"{cursor}\" }} }}]"), context);

            next.Should().BeOfType<QueryOutcome.Success>(next is QueryOutcome.Refused again ? BindHost.Describe(again.Refusal) : "");
            answer["next"] = Calls(runner);
        }

        return answer;
    }

    private static JsonArray Calls(FakeAggregateRunner runner) => new(runner.Calls.Select(call => (JsonNode)new JsonObject
    {
        ["collection"] = call.Entity.Collection,
        ["collation"] = call.Options.Collation?.ToJson(Canonical),
        ["stages"] = new JsonArray(call.Stages.Select(stage => (JsonNode)stage.ToJson(Canonical)).ToArray()),
    }).ToArray());
}
