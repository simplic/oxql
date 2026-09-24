using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What only a server can show about the collation: the fold and the prefix range match the
/// case and accent variants, an exact comparison inside a collated aggregate does not, a group
/// folds, a keyset walk over case variants neither repeats nor drops a row, and a join on a
/// string key stays exact. The stages are the compiler's own, run on the run's server in a
/// database each test owns. Moved from the unit project, where they ran only when a server was
/// named by hand.
/// </summary>
[Trait("Category", "Integration")]
public class CaseInsensitiveServerTests
{
    private const string Order = "ci.order";
    private const string Collection = "orders";

    private static readonly EngineDirect Orders = new(ClrModelBuilder.Build([new EntityDeclaration(Order, Order, typeof(OrderModel), Collection, null, false)]));

    private static readonly EngineDirect Joins = new(CaseInsensitiveJoinModel.Model);

    /// <summary>A string-keyed entity: all a collation case needs.</summary>
    public sealed class OrderModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Number { get; set; } = "";

        public string? Note { get; set; }
    }

    private static BsonDocument Row(int id, string number) => new()
    {
        ["_id"] = new BsonBinaryData(Guid.Parse($"00000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard),
        ["Number"] = number,
        ["OrganizationId"] = EngineDirect.OrganisationValue,
    };

    private static async Task<List<BsonDocument>> Run(IMongoDatabase database, string pipeline, bool count = false) =>
        await EngineDirect.RunAsync(database, Collection, await Orders.CompileAsync(Order, pipeline), count);

    [Fact]
    public async Task A_fold_matches_case_and_accent_variants_and_an_exact_comparison_does_not()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("ci_fold");
        var database = owned.Database;

        await database.GetCollection<BsonDocument>(Collection).InsertManyAsync([Row(1, "abc"), Row(2, "ABC"), Row(3, "äbc"), Row(4, "abcd"), Row(5, "abd")]);

        (await Run(database, """[{ "match": { "number": { "eq": "ABC" } } }]""")).Should().HaveCount(3);
        (await Run(database, """[{ "match": { "number": { "startsWith": "ab" } } }]""")).Should().HaveCount(5, "ä folds to a at primary strength");
        (await Run(database, """[{ "match": { "number": { "startsWith": "abc" } } }]""")).Should().HaveCount(4);
        (await Run(database, """[{ "match": { "number": { "neq": "abc" } } }]""")).Should().HaveCount(2);
        (await Run(database, """[{ "match": { "number": { "eq": "ABC", "options": { "caseSensitive": true } } } }]""")).Should().ContainSingle().Which["Number"].AsString.Should().Be("ABC");

        var exactInsideCollated = await Orders.CompileAsync(Order, """[{ "match": { "number": { "eq": "ABC", "options": { "caseSensitive": true } }, "note": { "neq": "zzz" } } }]""");

        exactInsideCollated.Collation.Should().NotBeNull();
        (await EngineDirect.RunAsync(database, Collection, exactInsideCollated)).Should().ContainSingle().Which["Number"].AsString.Should().Be("ABC");

        (await Run(database, """[{ "match": { "number": { "eq": "abc" } } }, { "page": { "includeTotalCount": true } }]""", count: true)).Single()["n"].ToInt64().Should().Be(3);
    }

    [Fact]
    public async Task A_group_folds_its_string_key()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("ci_group");

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync([Row(1, "abc"), Row(2, "ABC"), Row(3, "äbc"), Row(4, "abd")]);

        var groups = await Run(owned.Database, """[{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "c": { "count": true } } } }]""");

        groups.Should().HaveCount(2, "abc, ABC and äbc are one group; abd is the other");
        groups.Select(group => group["c"].ToInt64()).Order().Should().Equal(1, 3);
    }

    [Fact]
    public async Task A_keyset_walk_over_case_variants_neither_repeats_nor_drops_a_row()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("ci_walk");
        var keys = new[] { "b", "A", "ä", "B", "a", "Ä", "c" };

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(keys.Select((key, index) => Row(index + 1, key)));

        var seen = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var pipeline = cursor is null
                ? """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]"""
                : $$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""";
            var compiled = await Orders.CompileAsync(Order, pipeline);
            var rows = await EngineDirect.RunAsync(owned.Database, Collection, compiled);
            var hasNext = rows.Count > compiled.Limit;
            var taken = hasNext ? rows.Take(compiled.Limit).ToList() : rows;

            seen.AddRange(taken.Select(row => row["Number"].AsString + "#" + row["_id"]));

            if (!hasNext)
                break;

            cursor = Orders.Cursors.Encode(MongoQueryEngine.NextCursor(compiled, taken[^1]));
        }

        seen.Should().HaveCount(keys.Length);
        seen.Distinct().Should().HaveCount(keys.Length, "no row repeats");
        string.Concat(seen.Select(entry => char.ToLowerInvariant(entry[0]) is 'ä' ? 'a' : char.ToLowerInvariant(entry[0]))).Should().Be("aaaabbc", "the order folds case and accents");
    }

    [Fact]
    public async Task A_join_on_a_string_key_stays_exact_inside_a_collated_aggregate()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("ci_join");
        var folders = owned.Database.GetCollection<BsonDocument>(CaseInsensitiveJoinModel.FolderCollection);
        var documents = owned.Database.GetCollection<BsonDocument>(CaseInsensitiveJoinModel.DocumentCollection);

        static BsonDocument Folder(int id, string code) => new() { ["_id"] = new BsonBinaryData(Guid.Parse($"10000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard), ["OrganizationId"] = EngineDirect.OrganisationValue, ["Code"] = code, ["Name"] = "x" };
        static BsonDocument Document(int id, string code) => new() { ["_id"] = new BsonBinaryData(Guid.Parse($"20000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard), ["OrganizationId"] = EngineDirect.OrganisationValue, ["FolderCode"] = code, ["Title"] = "t" };

        await folders.InsertManyAsync([Folder(1, "abc"), Folder(2, "ABC")]);
        await documents.InsertManyAsync([Document(1, "abc"), Document(2, "ABC"), Document(3, "äbc")]);

        var resolved = await Joins.CompileAsync(CaseInsensitiveJoinModel.Document, """[{ "resolve": { "path": "folderCode", "as": "folder", "select": ["code"] } }, { "match": { "title": { "eq": "T" } } }, { "sort": [{ "id": "asc" }] }]""");

        resolved.Collation.Should().NotBeNull();

        var rows = await EngineDirect.RunAsync(owned.Database, CaseInsensitiveJoinModel.DocumentCollection, resolved);

        rows.Should().HaveCount(3, "the title folds");
        rows[0]["folder"]["Code"].AsString.Should().Be("abc");
        rows[1]["folder"]["Code"].AsString.Should().Be("ABC");
        rows[2].Contains("folder").Should().BeFalse("no folder is spelled äbc");

        var looked = await EngineDirect.RunAsync(owned.Database, CaseInsensitiveJoinModel.FolderCollection, await Joins.CompileAsync(CaseInsensitiveJoinModel.Folder, """[{ "lookup": { "from": "ci.document", "path": "folderCode", "as": "docs", "select": ["folderCode"] } }, { "match": { "name": { "eq": "X" } } }, { "sort": [{ "id": "asc" }] }]"""));

        looked.Should().HaveCount(2);
        looked[0]["docs"].AsBsonArray.Select(document => document["FolderCode"].AsString).Should().Equal("abc");
        looked[1]["docs"].AsBsonArray.Select(document => document["FolderCode"].AsString).Should().Equal("ABC");
    }
}
