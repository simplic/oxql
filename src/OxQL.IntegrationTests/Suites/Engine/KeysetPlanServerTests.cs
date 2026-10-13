using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Cursor;
using OxQL.IntegrationTests.Fleet;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What the predicate of a cursor costs on a server whose collection has an index on the sort: a
/// page that continues across the stored types reads the keys of its page, as one that stays inside
/// a type does, in either direction and for a number and a text alike.
/// </summary>
[Trait("Category", "Integration")]
public class KeysetPlanServerTests
{
    private const int Rows = 20_000;

    private const int Page = 51;

    private static readonly BsonBinaryData Organisation = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), GuidRepresentation.Standard);

    private static readonly Lazy<Task<IMongoDatabase>> Seeded = new(async () =>
    {
        var owned = await MongoFixture.CreateDatabaseAsync("keyset_plan");
        var database = (await MongoFixture.ClientAsync()).GetDatabase(owned.Name);
        var rows = database.GetCollection<BsonDocument>("rows");

        await rows.InsertManyAsync(Enumerable.Range(0, Rows).Select(n => new BsonDocument
        {
            ["_id"] = n,
            ["OrganizationId"] = Organisation,
            ["Count"] = n % 500,
            ["Name"] = "name " + (n % 700).ToString("D5"),
        }));

        // The test's own indexes, in a database of its own: what a service has on a list it sorts.
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["Count"] = 1, ["_id"] = 1 }));
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["Name"] = 1, ["_id"] = 1 }));

        return database;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static TheoryData<string, bool, bool> Legs()
    {
        var data = new TheoryData<string, bool, bool>();

        foreach (var field in new[] { "Count", "Name" })
            foreach (var ascending in new[] { true, false })
                foreach (var types in new[] { false, true })
                    data.Add(field, ascending, types);

        return data;
    }

    [Theory]
    [MemberData(nameof(Legs))]
    public async Task A_page_after_a_position_reads_the_keys_of_the_page(string field, bool ascending, bool severalTypes)
    {
        var database = await Seeded.Value;
        BsonValue position = field == "Count" ? 250 : "name 00350";

        var legs = new List<KeysetField> { new(field, ascending, position) { SeveralTypes = severalTypes }, new("_id", true, Rows / 2) };
        var conjuncts = new BsonArray { new BsonDocument("OrganizationId", Organisation) };

        if (KeysetPredicate.LeadingRange(legs, everyType: true) is { } range)
            conjuncts.Add(range);

        conjuncts.Add(KeysetPredicate.After(legs, inclusive: false, everyType: true));

        var pipeline = new BsonArray
        {
            new BsonDocument("$match", new BsonDocument("$and", conjuncts)),
            new BsonDocument("$sort", new BsonDocument { [field] = ascending ? 1 : -1, ["_id"] = ascending ? 1 : -1 }),
            new BsonDocument("$limit", Page),
        };

        var explained = await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["explain"] = new BsonDocument { ["aggregate"] = "rows", ["pipeline"] = pipeline, ["cursor"] = new BsonDocument() },
            ["verbosity"] = "executionStats",
        });

        var stats = Find(explained, "executionStats")!.AsBsonDocument;

        stats["nReturned"].ToInt64().Should().Be(Page);
        stats["totalKeysExamined"].ToInt64().Should().BeLessThan(Page * 3, "the index bounds the predicate at the position, and the keys read are those of the page and of the rows that share its first value");
        stats["totalDocsExamined"].ToInt64().Should().BeLessThan(Page * 3);
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
