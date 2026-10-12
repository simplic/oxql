using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Transport;
using StoredShipment = OxQL.IntegrationTests.Fleet.Models.Transport.Shipment;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What the database does with a block of unwound rows whose order is the parents', read off its own
/// execution statistics: the parents are filtered by a plain condition, ordered and cut to what the
/// page can reach, and only those are unwound. Held for the three requests a grid sends (no condition
/// on the element, one an absent element fails, one it passes), over a collection stored as its type
/// says and over one that also holds what it does not (a null and a scalar in the array, one object
/// and a scalar in the array's place), without an index and with one on the order:
/// <list type="bullet">
/// <item>nothing asked before the unwind is an expression (<c>$expr</c>);</item>
/// <item>the unwind is handed a page's worth of parents, however many pass;</item>
/// <item>without an index the database keeps the first of the order while it scans (a sort bounded
/// by the limit), and with one it reads the index in order and stops: it examines the parents up to
/// the last one the page reaches, not the collection;</item>
/// <item>the count asks the same of the parents, unwinds those with a passing row and no other, and
/// bounds them by its own bound: a step of ten rows is handed eleven parents;</item>
/// <item>the rows are those of the stages as written.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class UnwindPlanServerTests
{
    private const int Shipments = 3_000;

    private const int Limit = 20;

    /// <summary>The parents a first page of <see cref="Limit"/> rows can reach: one each, and one more for the row that says a next page exists.</summary>
    private const int Reach = Limit + 1;

    private static readonly EngineDirect Direct = new(LabService.Transport.Model);

    private static string Collection => LabService.Transport.Model.Entities[Corpus.Shipment].Collection;

    private const string Unwind = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""";

    private const string Order = """{ "sort": [ { "shipmentNumber": "asc" }, { "id": "asc" }, { "position": "asc" } ] }""";

    /// <summary>The three requests: the stages before the page, and how many of the regular parents hold a row that passes, in a hundred.</summary>
    public static readonly TheoryData<string, string, bool> Requests = new()
    {
        { "a: no condition on the element", $$"""[ {{Unwind}}, {{Order}} ]""", false },
        { "a: no condition on the element", $$"""[ {{Unwind}}, {{Order}} ]""", true },
        { "b: a condition an absent element passes", $$"""[ {{Unwind}}, { "match": { "item.shippingUnit": { "neq": "Palette" } } }, {{Order}} ]""", false },
        { "b: a condition an absent element passes", $$"""[ {{Unwind}}, { "match": { "item.shippingUnit": { "neq": "Palette" } } }, {{Order}} ]""", true },
        { "b: not in", $$"""[ {{Unwind}}, { "match": { "item.shippingUnit": { "nin": ["Palette", "Fass"] } } }, {{Order}} ]""", true },
        { "c: a condition an absent element fails", $$"""[ {{Unwind}}, { "match": { "item.shippingUnit": { "eq": "Palette" } } }, {{Order}} ]""", false },
        { "c: a condition an absent element fails", $$"""[ {{Unwind}}, { "match": { "item.shippingUnit": { "eq": "Palette" } } }, {{Order}} ]""", true },
        { "c: two conditions on one element", $$"""[ {{Unwind}}, { "match": { "item.shippingUnit": { "eq": "Palette" }, "item.orderNumber": { "gte": 1 } } }, {{Order}} ]""", true },
    };

    private static readonly Lazy<Task<IMongoDatabase>> Regular = new(() => SeedAsync(irregular: false), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Task<IMongoDatabase>> Irregular = new(() => SeedAsync(irregular: true), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 3 000 shipments in no order of their numbers: three in four hold one to three items, most of
    /// them on a pallet, the others an empty array, a null or no member. With
    /// <paramref name="irregular"/> one in twenty holds something else instead.
    /// </summary>
    private static async Task<IMongoDatabase> SeedAsync(bool irregular)
    {
        var database = (await MongoFixture.CreateDatabaseAsync(irregular ? "unwind_plan_irregular" : "unwind_plan")).Database;
        var shipments = new List<BsonDocument>();

        for (var n = 1; n <= Shipments; n++)
        {
            BsonDocument Item(int position) => new ShipmentItem
            {
                Id = Ids.Of(Spaces.ShipmentItem, Org.A, 400_000 + n * 10 + position),
                Text = $"Position {position}",
                OrderNumber = position,
                ShippingUnit = (n * 7 + position) % 6 == 0 ? "Kiste" : "Palette",
            }.ToBsonDocument();

            var document = new StoredShipment
            {
                Id = Ids.Of(Spaces.Shipment, Org.A, 40_000 + n),
                OrganizationId = EngineDirect.Organisation,
                ShipmentNumber = $"S-{(long)n * 7_919 % Shipments:D5}",
                Notes = new string('x', 400),
            }.ToBsonDocument();

            switch (n % 8)
            {
                case 0: document["Items"] = new BsonArray(); break;
                case 4 when n % 16 == 4: document["Items"] = BsonNull.Value; break;
                case 4: document.Remove("Items"); break;
                default: document["Items"] = new BsonArray(Enumerable.Range(0, 1 + n % 3).Select(Item)); break;
            }

            if (irregular && n % 20 == 3)
                document["Items"] = (n / 20 % 5) switch
                {
                    0 => new BsonArray { Item(0), BsonNull.Value },
                    1 => new BsonArray { BsonNull.Value },
                    2 => Item(0),
                    3 => 7,
                    _ => new BsonArray { "Palette", Item(1) },
                };

            shipments.Add(document);
        }

        await database.GetCollection<BsonDocument>(Collection).InsertManyAsync(shipments);

        return database;
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task The_parents_are_asked_a_plain_condition_ordered_and_cut_before_the_unwind(string id, string stages, bool irregular)
    {
        var database = await (irregular ? Irregular : Regular).Value;
        var because = $"{id}, {(irregular ? "with values that are not stored as the type says" : "stored as the type says")}";
        var pipeline = stages.TrimEnd().TrimEnd(']') + $$""", { "page": { "limit": {{Limit}}, "count": {} } } ]""";
        var bound = await Direct.BindAsync(Corpus.Shipment, pipeline);
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(30_000, true, 100_000) { KeepDiscriminators = true });
        var written = MongoCompiler.Compile(bound, new CompileOptions(30_000, true, 100_000) { KeepDiscriminators = true, AsWritten = true });
        var conditioned = stages.Contains("\"match\"", StringComparison.Ordinal);

        // ---- the stages ----
        var kinds = compiled.PageStages.Select(stage => stage.GetElement(0).Name).ToList();
        var unwindAt = kinds.IndexOf("$unwind");

        compiled.Unwind!.ParentsFirst.Should().BeTrue(because);
        (compiled.Unwind.Prefiltered.Count > 0).Should().Be(conditioned, because);
        kinds.Take(unwindAt).Should().Equal(["$match", "$match", "$sort", "$limit"], $"{because}: the organisation, what a parent must hold, the parents' order, the page's reach");
        compiled.PageStages[3]["$limit"].ToInt64().Should().Be(Reach, because);
        string.Join(" ", compiled.PageStages.Take(unwindAt).Concat(compiled.CountStages!.TakeWhile(stage => !stage.Contains("$unwind"))).Select(stage => stage.ToJson()))
            .Should().NotContain("$expr", $"{because}: a plain condition");

        // ---- the rows ----
        var rows = await EngineDirect.RunAsync(database, Collection, compiled);

        rows.Should().HaveCount(Reach, because);
        rows.Select(row => WireEncoder.Encode(row, bound).ToJsonString())
            .Should().Equal((await EngineDirect.RunAsync(database, Collection, written)).Select(row => WireEncoder.Encode(row, bound).ToJsonString()), $"{because}: the rows of the stages as written");

        // ---- without an index: every row is read once, the first of the order kept while reading ----
        var scanned = await ExplainAsync(database, compiled.PageStages);

        scanned.Stages.Should().Contain("COLLSCAN").And.Contain("SORT", $"{because}: {scanned}");
        scanned.SortLimit.Should().Be(Reach, $"{because}: the sort keeps what the page can reach, not every parent: {scanned}");
        scanned.Docs.Should().Be(Shipments, $"{because}: {scanned}");
        scanned.Parents.Should().Be(Reach, $"{because}: the unwind is handed a page's worth of parents: {scanned}");

        // ---- the count: the parents with a passing row, and no other, reach its unwind; as many as its bound at most ----
        var everyRow = await RowsAsync(database, written);
        var passing = everyRow.Select(row => row["_id"]).Distinct().Count();

        compiled.CountParentsAt.Should().Be(2, because);
        compiled.CountStages![1].ToJson().Should().Be(compiled.PageStages[1].ToJson(), $"{because}: the count asks what the cut of the parents asks");
        (await EngineDirect.RunAsync(database, Collection, compiled, count: true))[0]["n"].ToInt64().Should().Be(Math.Min(everyRow.Count, 1_001), $"{because}: the count's first step");

        // Without a bound, every parent it would unwind is seen: exactly those with a row that passes.
        (await ExplainAsync(database, [.. compiled.CountStages!.Where(stage => !stage.Contains("$limit"))])).Parents
            .Should().Be(passing, $"{because}: the count unwinds the parents with a passing row, and no other");

        // Bounded by a step of ten rows: eleven parents are handed over, and with an index nothing after them is read.
        foreach (var rung in new[] { 10, 3_000 })
        {
            List<BsonDocument> step = [.. compiled.CountStages!.Take(compiled.CountStages!.Count - 2), new BsonDocument("$limit", rung + 1), new BsonDocument("$count", "n")];

            step[compiled.CountParentsAt!.Value] = new BsonDocument("$limit", rung + 1);

            var answered = await (await database.GetCollection<BsonDocument>(Collection).AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(step))).ToListAsync();

            answered[0]["n"].ToInt64().Should().Be(Math.Min(everyRow.Count, rung + 1), $"{because}: a step bounded by {rung} rows answers what it answers without the bound on the parents");
            (await ExplainAsync(database, step)).Parents.Should().Be(Math.Min(passing, rung + 1), $"{because}: a step bounded by {rung}");
        }

        // ---- the count alone, as the engine climbs to it: a first step of 1 000 rows, then the rest ----
        var engine = Direct.Engine(await MongoFixture.ClientAsync(), database.DatabaseNamespace.DatabaseName);
        var alone = await engine.ExecuteAsync(EngineDirect.Request(Corpus.Shipment, stages.TrimEnd().TrimEnd(']') + """, { "page": { "limit": 0, "count": { "budgetMs": 20000 } } } ]"""), Direct.Context());
        var count = alone.Should().BeOfType<QueryOutcome.Success>().Subject.Result.PageInfo.Count!;

        count.State.Should().Be(CountInfo.Exact, because);
        count.Value.Should().Be(everyRow.Count, $"{because}: the count alone is the number of rows of the stages as written");

        // ---- with an index on the order: read in order until the page's reach is met ----
        var indexed = await IndexedAsync(irregular);
        var ordered = await ExplainAsync(indexed, compiled.PageStages);
        var reached = await ReachedAsync(indexed, written);

        ordered.Stages.Should().Contain("IXSCAN").And.NotContain("COLLSCAN").And.NotContain("SORT", $"{because}: the index orders the parents: {ordered}");
        ordered.Parents.Should().Be(Reach, $"{because}: {ordered}");
        ordered.Docs.Should().Be(reached, $"{because}: every parent of the order up to the last one the page reaches is read, and none after it: {ordered}");
        ordered.Docs.Should().BeLessThan(Shipments / 4, $"{because}: {ordered}");
    }

    private static readonly Lazy<Task<IMongoDatabase>>[] Indexed =
    [
        new(() => WithIndexAsync(irregular: false), LazyThreadSafetyMode.ExecutionAndPublication),
        new(() => WithIndexAsync(irregular: true), LazyThreadSafetyMode.ExecutionAndPublication),
    ];

    private static Task<IMongoDatabase> IndexedAsync(bool irregular) => Indexed[irregular ? 1 : 0].Value;

    /// <summary>The same shipments in a database of their own with an index on the organisation, the number and the key: the order of the page.</summary>
    private static async Task<IMongoDatabase> WithIndexAsync(bool irregular)
    {
        var database = await SeedAsync(irregular);

        await database.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["ShipmentNumber"] = 1, ["_id"] = 1 }));

        return database;
    }

    /// <summary>
    /// How many parents of the order stand up to the last one the page reaches: counted off the rows
    /// of the stages as written, by the position in the parents' order of the parent of row
    /// <see cref="Reach"/> among the parents that yield a passing row.
    /// </summary>
    private static async Task<long> ReachedAsync(IMongoDatabase database, CompiledQuery written)
    {
        var rows = await RowsAsync(database, written);
        var parents = rows.Select(row => row["_id"]).Distinct().Take(Reach).ToList();
        var last = rows.First(row => row["_id"] == parents[^1]);
        var number = last.GetValue("ShipmentNumber", BsonNull.Value);

        return await database.GetCollection<BsonDocument>(Collection).CountDocumentsAsync(new BsonDocument
        {
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["$or"] = new BsonArray
            {
                new BsonDocument("ShipmentNumber", new BsonDocument("$lt", number)),
                new BsonDocument { ["ShipmentNumber"] = number, ["_id"] = new BsonDocument("$lte", last["_id"]) },
            },
        });
    }

    /// <summary>Every row of the request as written, without its page, in the order of the page.</summary>
    private static async Task<List<BsonDocument>> RowsAsync(IMongoDatabase database, CompiledQuery written) =>
        await (await database.GetCollection<BsonDocument>(Collection).AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(written.PageStages.Where(stage => !stage.Contains("$limit")).ToList()))).ToListAsync();

    private sealed record Plan(IReadOnlyList<string> Stages, long Docs, long Parents, long? SortLimit)
    {
        public override string ToString() => $"{string.Join(" ", Stages)} docs={Docs} parents={Parents} sortLimit={SortLimit}";
    }

    private static IEnumerable<BsonValue> Find(BsonValue node, string name) => node switch
    {
        BsonDocument document => document.Elements.SelectMany(element => element.Name == name ? [element.Value, .. Find(element.Value, name)] : Find(element.Value, name)),
        BsonArray array => array.SelectMany(item => Find(item, name)),
        _ => [],
    };

    /// <summary>How the server ran the stages: the plan of what it reads, what it examined, and how many rows it handed to the unwind.</summary>
    private static async Task<Plan> ExplainAsync(IMongoDatabase database, IReadOnlyList<BsonDocument> stages)
    {
        var aggregate = new BsonDocument { ["aggregate"] = Collection, ["pipeline"] = new BsonArray(stages), ["cursor"] = new BsonDocument() };
        var explained = await database.RunCommandAsync<BsonDocument>(new BsonDocument { ["explain"] = aggregate, ["verbosity"] = "executionStats" });
        var cursor = explained["stages"].AsBsonArray[0]["$cursor"].AsBsonDocument;
        var stats = cursor["executionStats"].AsBsonDocument;
        var plan = cursor["queryPlanner"]["winningPlan"];

        return new Plan(
            Find(plan, "stage").Select(stage => stage.AsString).Distinct().ToList(),
            stats["totalDocsExamined"].ToInt64(),
            stats["nReturned"].ToInt64(),
            Find(plan, "limitAmount").Select(limit => (long?)limit.ToInt64()).FirstOrDefault());
    }
}
