using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// A fact that runs only against a live server: <c>OXQL_TEST_MONGO</c> holds the connection
/// string, <c>OXQL_TEST_MONGO_DB</c> the database (default <c>oxql_lab_logistics</c>). Every
/// test works in collections prefixed <c>tmp_ci_</c> and drops them.
/// </summary>
internal sealed class LiveFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "OXQL_TEST_MONGO";

    public const string DatabaseVariable = "OXQL_TEST_MONGO_DB";

    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            Skip = $"Set {ConnectionVariable} to a connection string to run against a live server.";
    }
}

/// <summary>
/// What only a server can show about the collation: the fold and the prefix range match the
/// case and accent variants, an exact comparison inside a collated aggregate does not, a group
/// folds, a keyset walk over case variants neither repeats nor drops a row, and a join on a
/// string key stays exact.
/// </summary>
public class CaseInsensitiveLiveTests
{
    private const string Order = "probe.order";
    private static readonly CompileOptions Options = new(10_000, null, 100_000);
    private static readonly BsonBinaryData Org = new(BindHost.Organisation, GuidRepresentation.Standard);

    private static IMongoDatabase Database()
    {
        var client = new MongoClient(Environment.GetEnvironmentVariable(LiveFactAttribute.ConnectionVariable));

        return client.GetDatabase(Environment.GetEnvironmentVariable(LiveFactAttribute.DatabaseVariable) ?? "oxql_lab_logistics");
    }

    private static string TemporaryName() => "tmp_ci_" + Guid.NewGuid().ToString("N");

    private static BsonDocument Row(int id, string number) => new()
    {
        ["_id"] = new BsonBinaryData(Guid.Parse($"00000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard),
        ["Number"] = number,
        ["OrganizationId"] = Org,
    };

    /// <summary>Runs the compiled page (or count) stages as the engine would: under the collation the compiler chose.</summary>
    private static async Task<List<BsonDocument>> Run(IMongoDatabase database, string collection, CompiledQuery compiled, bool count = false)
    {
        var options = new AggregateOptions();

        if (compiled.Collation is { } collation)
            options.Collation = Collation.FromBsonDocument(collation);

        var stages = count ? compiled.CountStages! : compiled.PageStages;

        return await (await database.GetCollection<BsonDocument>(collection).AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages), options)).ToListAsync();
    }

    private static async Task<CompiledQuery> Compile(string pipeline, EntityModelHolder? model = null) =>
        MongoCompiler.Compile(await BindHost.BoundAsync(model?.Model ?? BindHost.Probe, model?.Entity ?? Order, pipeline), Options);

    private sealed record EntityModelHolder(OxQL.Model.EntityModel Model, string Entity);

    [LiveFact]
    public async Task A_fold_matches_case_and_accent_variants_and_an_exact_comparison_does_not()
    {
        var database = Database();
        var name = TemporaryName();

        try
        {
            await database.GetCollection<BsonDocument>(name).InsertManyAsync([Row(1, "abc"), Row(2, "ABC"), Row(3, "äbc"), Row(4, "abcd"), Row(5, "abd")]);

            (await Run(database, name, await Compile("""[{ "match": { "number": { "eq": "ABC" } } }]"""))).Should().HaveCount(3);
            (await Run(database, name, await Compile("""[{ "match": { "number": { "startsWith": "ab" } } }]"""))).Should().HaveCount(5, "ä folds to a at primary strength");
            (await Run(database, name, await Compile("""[{ "match": { "number": { "startsWith": "abc" } } }]"""))).Should().HaveCount(4);
            (await Run(database, name, await Compile("""[{ "match": { "number": { "neq": "abc" } } }]"""))).Should().HaveCount(2);
            (await Run(database, name, await Compile("""[{ "match": { "number": { "eq": "ABC", "options": { "caseSensitive": true } } } }]"""))).Should().ContainSingle().Which["Number"].AsString.Should().Be("ABC");

            var exactInsideCollated = await Compile("""[{ "match": { "number": { "eq": "ABC", "options": { "caseSensitive": true } }, "note": { "neq": "zzz" } } }]""");

            exactInsideCollated.Collation.Should().NotBeNull();
            (await Run(database, name, exactInsideCollated)).Should().ContainSingle().Which["Number"].AsString.Should().Be("ABC");

            var counted = await Compile("""[{ "match": { "number": { "eq": "abc" } } }, { "page": { "includeTotalCount": true } }]""");

            (await Run(database, name, counted, count: true)).Single()["n"].ToInt64().Should().Be(3);
        }
        finally
        {
            await database.DropCollectionAsync(name);
        }
    }

    [LiveFact]
    public async Task A_group_folds_its_string_key()
    {
        var database = Database();
        var name = TemporaryName();

        try
        {
            await database.GetCollection<BsonDocument>(name).InsertManyAsync([Row(1, "abc"), Row(2, "ABC"), Row(3, "äbc"), Row(4, "abd")]);

            var groups = await Run(database, name, await Compile("""[{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "c": { "count": true } } } }]"""));

            groups.Should().HaveCount(2, "abc, ABC and äbc are one group; abd is the other");
            groups.Select(group => group["c"].ToInt64()).Order().Should().Equal(1, 3);
        }
        finally
        {
            await database.DropCollectionAsync(name);
        }
    }

    [LiveFact]
    public async Task A_keyset_walk_over_case_variants_neither_repeats_nor_drops_a_row()
    {
        var database = Database();
        var name = TemporaryName();
        var keys = new[] { "b", "A", "ä", "B", "a", "Ä", "c" };

        try
        {
            await database.GetCollection<BsonDocument>(name).InsertManyAsync(keys.Select((key, index) => Row(index + 1, key)));

            var seen = new List<string>();
            string? cursor = null;

            for (var page = 0; page < 10; page++)
            {
                var pipeline = cursor is null
                    ? """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]"""
                    : $$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""";
                var compiled = await Compile(pipeline);
                var rows = await Run(database, name, compiled);
                var hasNext = rows.Count > compiled.Limit;
                var taken = hasNext ? rows.Take(compiled.Limit).ToList() : rows;

                seen.AddRange(taken.Select(row => row["Number"].AsString + "#" + row["_id"]));

                if (!hasNext)
                    break;

                cursor = BindHost.Cursors.Encode(MongoQueryEngine.NextCursor(compiled, taken[^1]));
            }

            seen.Should().HaveCount(keys.Length);
            seen.Distinct().Should().HaveCount(keys.Length, "no row repeats");
            string.Concat(seen.Select(entry => char.ToLowerInvariant(entry[0]) is 'ä' ? 'a' : char.ToLowerInvariant(entry[0]))).Should().Be("aaaabbc", "the order folds case and accents");
        }
        finally
        {
            await database.DropCollectionAsync(name);
        }
    }

    [LiveFact]
    public async Task A_join_on_a_string_key_stays_exact_inside_a_collated_aggregate()
    {
        var database = Database();
        var folders = database.GetCollection<BsonDocument>(CaseInsensitiveJoinModel.FolderCollection);
        var documents = database.GetCollection<BsonDocument>(CaseInsensitiveJoinModel.DocumentCollection);

        static BsonDocument Folder(int id, string code) => new() { ["_id"] = new BsonBinaryData(Guid.Parse($"10000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard), ["OrganizationId"] = Org, ["Code"] = code, ["Name"] = "x" };
        static BsonDocument Document(int id, string code) => new() { ["_id"] = new BsonBinaryData(Guid.Parse($"20000000-0000-0000-0000-{id:D12}"), GuidRepresentation.Standard), ["OrganizationId"] = Org, ["FolderCode"] = code, ["Title"] = "t" };

        try
        {
            await folders.InsertManyAsync([Folder(1, "abc"), Folder(2, "ABC")]);
            await documents.InsertManyAsync([Document(1, "abc"), Document(2, "ABC"), Document(3, "äbc")]);

            var byDocument = new EntityModelHolder(CaseInsensitiveJoinModel.Model, CaseInsensitiveJoinModel.Document);
            var resolved = await Compile("""[{ "resolve": { "path": "folderCode", "as": "folder", "select": ["code"] } }, { "match": { "title": { "eq": "T" } } }, { "sort": [{ "id": "asc" }] }]""", byDocument);

            resolved.Collation.Should().NotBeNull();

            var rows = await Run(database, CaseInsensitiveJoinModel.DocumentCollection, resolved);

            rows.Should().HaveCount(3, "the title folds");
            rows[0]["folder"]["Code"].AsString.Should().Be("abc");
            rows[1]["folder"]["Code"].AsString.Should().Be("ABC");
            rows[2].Contains("folder").Should().BeFalse("no folder is spelled äbc");

            var byFolder = new EntityModelHolder(CaseInsensitiveJoinModel.Model, CaseInsensitiveJoinModel.Folder);
            var looked = await Run(database, CaseInsensitiveJoinModel.FolderCollection, await Compile("""[{ "lookup": { "from": "ci.document", "path": "folderCode", "as": "docs", "select": ["folderCode"] } }, { "match": { "name": { "eq": "X" } } }, { "sort": [{ "id": "asc" }] }]""", byFolder));

            looked.Should().HaveCount(2);
            looked[0]["docs"].AsBsonArray.Select(document => document["FolderCode"].AsString).Should().Equal("abc");
            looked[1]["docs"].AsBsonArray.Select(document => document["FolderCode"].AsString).Should().Equal("ABC");
        }
        finally
        {
            await database.DropCollectionAsync(CaseInsensitiveJoinModel.FolderCollection);
            await database.DropCollectionAsync(CaseInsensitiveJoinModel.DocumentCollection);
        }
    }
}
