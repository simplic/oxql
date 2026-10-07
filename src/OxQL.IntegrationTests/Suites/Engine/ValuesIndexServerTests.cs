using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What a <c>values</c> request costs the database, read off the server's own execution statistics
/// in a database of the test's own (the indexes here exist in no other): the stage reads at most
/// <c>scan</c> rows whatever the request matches, and an index that holds the organisation and the
/// path answers them without reading a document.
/// <list type="bullet">
/// <item>Without an index on the path the rows are read as documents: as many as the bound lets
/// through, never all that match.</item>
/// <item>With an index on the organisation and the path, built without a collation, no document is
/// read: the keys examined are the rows the stage reads.</item>
/// <item>An index built with a collation holds the sort keys of its strings, not the strings, and the
/// database does not answer from its keys whatever the path holds: the documents are read again.</item>
/// <item>A condition on a member the index does not hold, and a path under a collection, read the
/// documents too; the bound still holds.</item>
/// </list>
/// The values answered are the same whichever way the rows were read.
/// </summary>
[Trait("Category", "Integration")]
public class ValuesIndexServerTests
{
    private const int Rows = 6_000;
    private const int Others = 1_500;
    private const int Scan = 1_000;

    private static string Collection => LabService.Transport.Model.Entities[Corpus.Shipment].Collection;

    private static readonly EngineDirect Direct = new(LabService.Transport.Model);

    private static readonly string[] Statuses = ["Open", "Planned", "Closed", "open", "Geöffnet", "geoffnet"];

    private sealed class Seeded(TestDatabase owned, IMongoCollection<BsonDocument> rows) : IAsyncDisposable
    {
        public IMongoDatabase Database => owned.Database;

        public string Name => owned.Name;

        public IMongoCollection<BsonDocument> Rows => rows;

        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    /// <summary>6 000 shipments of the organisation and 1 500 of another, a handful of statuses, a day per 40 rows, one or two items each.</summary>
    private static async Task<Seeded> SeedAsync()
    {
        var owned = await MongoFixture.CreateDatabaseAsync("values_index");
        var rows = owned.Database.GetCollection<BsonDocument>(Collection);
        var other = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        var day = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        await rows.InsertManyAsync(Enumerable.Range(0, Rows + Others).Select(n => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["OrganizationId"] = n % 5 == 4 ? other : EngineDirect.OrganisationValue,
            ["IsDeleted"] = n % 7 == 0,
            ["ShipmentNumber"] = "S-" + n,
            ["Status"] = new BsonDocument("Name", Statuses[n % Statuses.Length]),
            ["LoadStart"] = day.AddDays(n / 40),
            ["Notes"] = new string('x', 600),
            ["Items"] = new BsonArray(Enumerable.Range(0, 1 + (n % 2)).Select(item => new BsonDocument("Text", "T" + ((n + item) % 9)))),
        }));

        return new Seeded(owned, rows);
    }

    private sealed record Plan(IReadOnlyList<string> Stages, long Keys, long Docs)
    {
        public override string ToString() => $"{string.Join(" ", Stages)} keys={Keys} docs={Docs}";
    }

    /// <summary>Every value under a name, at any depth of an explain answer.</summary>
    private static IEnumerable<BsonValue> Find(BsonValue node, string name) => node switch
    {
        BsonDocument document => document.Elements.SelectMany(element => element.Name == name ? [element.Value, .. Find(element.Value, name)] : Find(element.Value, name)),
        BsonArray array => array.SelectMany(item => Find(item, name)),
        _ => [],
    };

    /// <summary>How the server ran the stages a values request compiles to: the plan's stages and what it examined.</summary>
    private static async Task<Plan> ExplainAsync(Seeded seeded, string pipeline, string? filter = null)
    {
        var request = EngineDirect.Request(Corpus.Shipment, pipeline) with { Filter = filter is null ? null : System.Text.Json.JsonDocument.Parse(filter).RootElement };
        var outcome = await new OxQL.Core.Binding.Binder(Direct.Model, Direct.Cursors).BindAsync(request, Direct.Context(), CancellationToken.None);
        var compiled = MongoCompiler.Compile(((OxQL.Core.Binding.BindOutcome.Bound)outcome).Pipeline, new CompileOptions(10_000, null, 100_000));
        var aggregate = new BsonDocument { ["aggregate"] = Collection, ["pipeline"] = new BsonArray(compiled.PageStages), ["cursor"] = new BsonDocument() };

        if (compiled.Collation is { } collation)
            aggregate["collation"] = collation;

        var explained = await seeded.Database.RunCommandAsync<BsonDocument>(new BsonDocument { ["explain"] = aggregate, ["verbosity"] = "executionStats" });
        var stats = Find(explained, "executionStats").OfType<BsonDocument>().First();

        return new Plan(
            Find(explained, "winningPlan").SelectMany(plan => Find(plan, "stage")).Select(stage => stage.AsString).Distinct().ToList(),
            stats["totalKeysExamined"].ToInt64(),
            stats["totalDocsExamined"].ToInt64());
    }

    private static async Task<Dictionary<string, int>> ValuesAsync(Seeded seeded, string pipeline)
    {
        var engine = Direct.Engine(await MongoFixture.ClientAsync(), seeded.Name);
        var items = await Direct.ExecuteAsync(engine, Corpus.Shipment, pipeline);

        return items.ToDictionary(item => Order.FoldCi(item!["value"]?.ToString() ?? "\0"), item => item!["count"]!.GetValue<int>());
    }

    private static string Values(string path, int scan = Scan) => $$"""[{ "values": { "path": "{{path}}", "counts": true, "scan": {{scan}} } }]""";

    [Fact]
    public async Task Without_an_index_on_the_path_the_stage_reads_documents_and_never_more_than_its_bound_lets_through()
    {
        await using var seeded = await SeedAsync();

        // The organisation's own index, as every collection of a service has one: the rows are found by it and read as documents.
        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("OrganizationId", 1)));

        var capped = await ExplainAsync(seeded, Values("status.name"));

        capped.Docs.Should().Be(Scan, $"the bound stands before the group: {capped}");
        capped.Stages.Should().Contain("FETCH");

        // The whole bound of the host is what a request without one reads at most: here every row of the organisation.
        var whole = await ExplainAsync(seeded, """[{ "values": { "path": "status.name", "counts": true } }]""");

        whole.Docs.Should().Be(Rows, $"6 000 rows match and the host's bound is 50 000: {whole}");
        capped.Docs.Should().BeLessThan(whole.Docs / 5, "what the bound protects against is reading every row that matches");

        // The list of the rows read: the six spellings fold into four values, and the counts are those of the rows read.
        var values = await ValuesAsync(seeded, Values("status.name"));

        values.Keys.Should().BeEquivalentTo("open", "planned", "closed", "geoffnet");
        values.Values.Sum().Should().Be(Scan);
        (await ValuesAsync(seeded, """[{ "values": { "path": "status.name", "counts": true } }]""")).Values.Sum().Should().Be(Rows);
    }

    [Fact]
    public async Task An_index_that_holds_the_organisation_and_the_path_answers_it_without_reading_a_document()
    {
        await using var seeded = await SeedAsync();
        var before = await ValuesAsync(seeded, """[{ "values": { "path": "status.name", "counts": true } }]""");

        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["Status.Name"] = 1 }));

        var capped = await ExplainAsync(seeded, Values("status.name"));

        capped.Docs.Should().Be(0, $"the index holds everything the stage reads: {capped}");
        capped.Keys.Should().Be(Scan, "one key per row read, and no more than the bound");
        capped.Stages.Should().Contain("IXSCAN").And.NotContain("FETCH").And.NotContain("COLLSCAN");

        var whole = await ExplainAsync(seeded, """[{ "values": { "path": "status.name", "counts": true } }]""");

        whole.Docs.Should().Be(0);
        whole.Keys.Should().Be(Rows);

        // The values and their counts are those the documents gave.
        (await ValuesAsync(seeded, """[{ "values": { "path": "status.name", "counts": true } }]""")).Should().BeEquivalentTo(before);

        // Which rows the first thousand are is the database's to choose: by this index they are the lowest values.
        (await ValuesAsync(seeded, Values("status.name"))).Values.Sum().Should().Be(Scan);
    }

    [Fact]
    public async Task An_index_built_with_a_collation_answers_nothing_from_its_keys_whatever_the_path_holds()
    {
        await using var seeded = await SeedAsync();
        var collation = new Collation("de", strength: CollationStrength.Primary);

        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["Status.Name"] = 1 }, new CreateIndexOptions { Collation = collation }));
        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["LoadStart"] = 1 }, new CreateIndexOptions { Collation = collation }));

        var text = await ExplainAsync(seeded, Values("status.name"));

        text.Docs.Should().Be(Scan, $"a collated index holds the sort keys of a string, not the string: the documents are read for it ({text})");
        text.Stages.Should().Contain("FETCH");

        var date = await ExplainAsync(seeded, Values("loadStart"));

        date.Docs.Should().Be(Scan, $"the database does not tell a date's key from a string's in such an index: {date}");

        // The same index without a collation holds the date, and the string.
        await seeded.Rows.Indexes.DropAllAsync();
        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["LoadStart"] = 1 }));

        var plain = await ExplainAsync(seeded, Values("loadStart"));

        plain.Docs.Should().Be(0, $"{plain}");
        plain.Keys.Should().Be(Scan);
    }

    [Fact]
    public async Task What_the_index_does_not_hold_is_read_from_the_documents_under_the_same_bound()
    {
        await using var seeded = await SeedAsync();

        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["Status.Name"] = 1 }));
        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["Items.Text"] = 1 }));

        // A condition of the request's filter on a member the index lacks: the rows are read to test it.
        var filtered = await ExplainAsync(seeded, Values("status.name"), filter: """[{ "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } }]""");

        filtered.Docs.Should().BeGreaterThanOrEqualTo(Scan, $"each row is read for the condition: {filtered}").And.BeLessThan(Scan * 2, "and the reading ends once the bound is full");

        // An index that holds the condition's member as well answers it from its keys again.
        await seeded.Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["Status.Name"] = 1, ["IsDeleted"] = 1 }));

        var covered = await ExplainAsync(seeded, Values("status.name"), filter: """[{ "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } }]""");

        covered.Docs.Should().Be(0, $"{covered}");

        // A path under a collection: an index over an array's members never stands for the documents.
        var under = await ExplainAsync(seeded, Values("items.text"));

        under.Docs.Should().Be(Scan, $"{under}");

        var values = await ValuesAsync(seeded, Values("items.text", Rows + 1));

        values.Should().HaveCount(9);
        values.Values.Sum().Should().Be(Rows + (Rows / 2), "one or two items a row, each counted");
    }
}
