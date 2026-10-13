using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model.Build;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What a page costs on a server whose collection has the index of its sort: the request is bound
/// and compiled by the engine (the organisation's scope, the collation of a request that folds, the
/// sort completed by the key, the predicate of the cursor), and the server says how many keys and
/// documents it read for the compiled page. A first page and a page from the middle of the walk
/// read about the keys of one page, for a sort over one, two and three members in every mix of
/// directions and over a number, a text and a date.
/// </summary>
[Trait("Category", "Integration")]
public class KeysetPlanServerTests(ITestOutputHelper output)
{
    private const string Entity = "kp.row";
    private const string Collection = "tmp_kp_rows";

    private const int Rows = 20_000;

    private const int Page = 51;

    /// <summary>
    /// The most keys a page may read: its rows and the one that says another page follows, once more
    /// for the rows that share the position's value on a leading member, and a constant for the
    /// bounds the server seeks between.
    /// </summary>
    private const int Bound = 2 * Page + 12;

    public sealed class RowModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public int Count { get; set; }

        public int State { get; set; }

        public string Name { get; set; } = "";

        public DateTime When { get; set; }
    }

    private static readonly EngineDirect Direct = new(ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(RowModel), Collection, null, false)]));

    private static readonly Lazy<Task<(IMongoDatabase Database, IQueryEngine Engine)>> Seeded = new(async () =>
    {
        var owned = await MongoFixture.CreateDatabaseAsync("keyset_plan");
        var client = await MongoFixture.ClientAsync();
        var database = client.GetDatabase(owned.Name);
        var day = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 40 rows share a count, about 29 a name and about 67 a day: every sort has ties, and a page of 51 crosses several values.
        await database.GetCollection<BsonDocument>(Collection).InsertManyAsync(Enumerable.Range(0, Rows).Select(n => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.Parse($"00000000-0000-0000-0000-{n * 7919 % Rows:D12}"), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["Count"] = n % 500,
            ["State"] = n % 4,
            ["Name"] = "name " + (n % 700).ToString("D5"),
            ["When"] = day.AddDays(n % 300),
        }));

        return (database, Direct.Engine(client, owned.Name, Direct.Options(options => options.Limits.MaxPageSize = Rows)));
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static TheoryData<string, string> Sorts() => new()
    {
        { "count asc", """[{ "count": "asc" }]""" },
        { "count desc", """[{ "count": "desc" }]""" },
        { "name asc", """[{ "name": "asc" }]""" },
        { "name desc", """[{ "name": "desc" }]""" },
        { "when asc", """[{ "when": "asc" }]""" },
        { "when desc", """[{ "when": "desc" }]""" },
        { "count asc, name asc", """[{ "count": "asc" }, { "name": "asc" }]""" },
        { "count asc, name desc", """[{ "count": "asc" }, { "name": "desc" }]""" },
        { "count desc, name asc", """[{ "count": "desc" }, { "name": "asc" }]""" },
        { "count desc, name desc", """[{ "count": "desc" }, { "name": "desc" }]""" },
        { "when asc, count asc", """[{ "when": "asc" }, { "count": "asc" }]""" },
        { "when desc, count asc", """[{ "when": "desc" }, { "count": "asc" }]""" },
        { "count asc, name asc, when asc", """[{ "count": "asc" }, { "name": "asc" }, { "when": "asc" }]""" },
        { "count desc, name asc, when desc", """[{ "count": "desc" }, { "name": "asc" }, { "when": "desc" }]""" },
        { "count asc, id desc", """[{ "count": "asc" }, { "id": "desc" }]""" },
        { "state asc", """[{ "state": "asc" }]""" },
        { "state desc", """[{ "state": "desc" }]""" },
        { "state asc, name asc", """[{ "state": "asc" }, { "name": "asc" }]""" },
        { "state asc, name desc", """[{ "state": "asc" }, { "name": "desc" }]""" },
        { "state desc, name asc", """[{ "state": "desc" }, { "name": "asc" }]""" },
        { "state desc, name desc", """[{ "state": "desc" }, { "name": "desc" }]""" },
        { "state asc, count asc, name asc", """[{ "state": "asc" }, { "count": "asc" }, { "name": "asc" }]""" },
        { "state desc, count desc, when desc", """[{ "state": "desc" }, { "count": "desc" }, { "when": "desc" }]""" },
    };

    [Theory]
    [MemberData(nameof(Sorts))]
    public async Task A_first_page_and_a_page_from_the_middle_read_about_the_keys_of_one_page(string what, string sort)
    {
        foreach (var filter in new[] { "", """{ "match": { "count": { "gte": 0 } } }, """ })
        {
            await Measure(what + (filter.Length > 0 ? " +match" : ""), filter, sort);
        }
    }

    private async Task Measure(string what, string filter, string sort)
    {
        var (database, engine) = await Seeded.Value;

        // The index of the sort: the scope, then the sort as the engine sends it (completed by the key), under the request's collation.
        var first = await Direct.CompileAsync(Entity, $$"""[{{filter}}{ "sort": {{sort}} }, { "page": { "limit": {{Page}} } }]""");
        var order = first.PageStages.Single(stage => stage.Contains("$sort"))["$sort"].AsBsonDocument;

        await database.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("OrganizationId", 1).AddRange(order),
            new CreateIndexOptions { Collation = first.Collation is { } collation ? Collation.FromBsonDocument(collation) : null }));

        var (firstKeys, firstDocs) = await ReadAsync(database, first);

        // A position in the middle of the walk: the cursor of a page that ends there.
        var half = await engine.ExecuteAsync(EngineDirect.Request(Entity, $$"""[{{filter}}{ "sort": {{sort}} }, { "page": { "limit": {{Rows / 2 - 7}} } }]"""), Direct.Context(Direct.Options(options => options.Limits.MaxPageSize = Rows)));
        var cursor = half.Should().BeOfType<QueryOutcome.Success>().Subject.Result.PageInfo.NextCursor;

        cursor.Should().NotBeNull();

        var middle = await Direct.CompileAsync(Entity, $$"""[{{filter}}{ "sort": {{sort}} }, { "page": { "limit": {{Page}}, "cursor": "{{cursor}}" } }]""");
        var (middleKeys, middleDocs) = await ReadAsync(database, middle);

        output.WriteLine($"{what}: first page {firstKeys} keys, {firstDocs} documents; middle page {middleKeys} keys, {middleDocs} documents");

        firstKeys.Should().BeLessThanOrEqualTo(Bound, "a first page reads its rows off the index");
        firstDocs.Should().BeLessThanOrEqualTo(Bound);
        middleKeys.Should().BeLessThanOrEqualTo(Bound, "the index bounds the predicate at the position");
        middleDocs.Should().BeLessThanOrEqualTo(Bound);
    }

    /// <summary>The keys and the documents the server reads for a compiled page that returns a full page.</summary>
    private static async Task<(long Keys, long Documents)> ReadAsync(IMongoDatabase database, OxQL.Mongo.CompiledQuery compiled)
    {
        var command = new BsonDocument
        {
            ["explain"] = new BsonDocument { ["aggregate"] = Collection, ["pipeline"] = new BsonArray(compiled.PageStages), ["cursor"] = new BsonDocument() },
            ["verbosity"] = "executionStats",
        };

        if (compiled.Collation is { } collation)
            command["explain"]["collation"] = collation;

        var explained = await database.RunCommandAsync<BsonDocument>(command);
        var stats = Find(explained, "executionStats")!.AsBsonDocument;

        stats["nReturned"].ToInt64().Should().BeInRange(Page, Page + 1, "the page is full, with the row that says another page follows");

        return (stats["totalKeysExamined"].ToInt64(), stats["totalDocsExamined"].ToInt64());
    }

    private static BsonValue? Find(BsonValue node, string name)
    {
        if (node is BsonDocument document)
        {
            if (document.Contains(name))
                return document[name];

            foreach (var element in document)
                if (Find(element.Value, name) is { } found)
                    return found;
        }
        else if (node is BsonArray array)
        {
            foreach (var item in array)
                if (Find(item, name) is { } found)
                    return found;
        }

        return null;
    }
}
