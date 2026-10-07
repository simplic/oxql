using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The index explain advises is the index the database uses, read off the server's own execution
/// statistics in a database of the test's own (the indexes here exist in no other; the engine never
/// creates one). For each kind of request: explain names an index and says what the request does
/// without it; the server does exactly that; the index is created from the definition explain gave,
/// as it gave it; explain then says an index serves the request, and the server no longer does what
/// it did.
/// <list type="bullet">
/// <item>Rows: without the index an in-memory sort over the organisation's rows; with it no sort
/// stage and no more documents read than the page.</item>
/// <item>A text that folds: the same keys without the collation still sort in memory.</item>
/// <item>A list of values and a group: with the index no document is read.</item>
/// <item>A join onto a member that is not the key: without the index the joined collection is
/// scanned once per row.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class IndexAdviceServerTests
{
    private const int Rows = 6_000;
    private const int Others = 1_500;

    private static string Collection => LabService.Transport.Model.Entities[Corpus.Shipment].Collection;

    private static readonly EngineDirect Transport = new(LabService.Transport.Model);

    private static readonly EngineDirect Fleet = new(LabService.Fleet.Model);

    private static readonly string[] Statuses = ["Open", "Planned", "Closed", "open", "Geöffnet", "geoffnet"];

    private sealed class Seeded(TestDatabase owned) : IAsyncDisposable
    {
        public IMongoDatabase Database => owned.Database;

        public string Name => owned.Name;

        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    /// <summary>6 000 shipments of the organisation and 1 500 of another: a handful of statuses, a day per 40 rows, every seventh deleted.</summary>
    private static async Task<Seeded> ShipmentsAsync()
    {
        var owned = await MongoFixture.CreateDatabaseAsync("index_advice");
        var other = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        var day = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(Enumerable.Range(0, Rows + Others).Select(n => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["OrganizationId"] = n % 5 == 4 ? other : EngineDirect.OrganisationValue,
            ["IsDeleted"] = n % 7 == 0,
            ["ShipmentNumber"] = "S-" + n,
            ["Status"] = new BsonDocument("Name", Statuses[n % Statuses.Length]),
            ["LoadStart"] = day.AddDays(n / 40),
            ["Notes"] = new string('x', 600),
        }));

        return new Seeded(owned);
    }

    private sealed record Plan(IReadOnlyList<string> Stages, long Keys, long Docs, BsonDocument Raw)
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

    private static QueryRequest Request(string entity, string pipeline, string filter) =>
        EngineDirect.Request(entity, pipeline) with { Filter = JsonDocument.Parse(filter).RootElement };

    /// <summary>How the server ran the stages the request compiles to: the plan's stages and what it examined.</summary>
    private static async Task<Plan> ServerAsync(Seeded seeded, EngineDirect direct, string entity, string pipeline, string filter = "[]")
    {
        var outcome = await new Binder(direct.Model, direct.Cursors).BindAsync(Request(entity, pipeline, filter), direct.Context(), CancellationToken.None);
        var compiled = MongoCompiler.Compile(((BindOutcome.Bound)outcome).Pipeline, new CompileOptions(10_000, null, 100_000));
        var aggregate = new BsonDocument { ["aggregate"] = direct.Model.Entities[entity].Collection, ["pipeline"] = new BsonArray(compiled.PageStages), ["cursor"] = new BsonDocument() };

        if (compiled.Collation is { } collation)
            aggregate["collation"] = collation;

        var explained = await seeded.Database.RunCommandAsync<BsonDocument>(new BsonDocument { ["explain"] = aggregate, ["verbosity"] = "executionStats" });
        var stats = Find(explained, "executionStats").OfType<BsonDocument>().First();

        return new Plan(
            Find(explained, "winningPlan").SelectMany(plan => Find(plan, "stage")).Select(stage => stage.AsString).Distinct().ToList(),
            stats["totalKeysExamined"].ToInt64(),
            stats["totalDocsExamined"].ToInt64(),
            explained);
    }

    /// <summary>The advisory of the whole engine over the test's database, reading its index lists anew each time.</summary>
    private static async Task<JsonObject> AdvisoryAsync(Seeded seeded, EngineDirect direct, string entity, string pipeline, string filter = "[]", bool source = true)
    {
        var client = await MongoFixture.ClientAsync();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(direct.Model), new MongoAggregateRunner(client, seeded.Name), direct.Cursors, direct.Options(),
            indexes: source ? new MongoIndexSource(client, seeded.Name, ttl: TimeSpan.Zero) : null);
        var outcome = await engine.ExplainAsync(new ExplainRequest { Query = Request(entity, pipeline, filter), Include = [ExplainRequest.IncludeIndexes], IsEnvelope = true }, direct.Context());
        var answer = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        answer.Valid.Should().BeTrue(string.Join("; ", answer.Errors.Select(error => error.Code + " " + error.Message)));
        answer.Notes.Where(note => note.Code == Notes.IndexAdvice).Should().HaveCount(answer.Advisory!["indexes"]!.AsArray().Count).And.OnlyContain(note => note.Basis == Basis.Advisory);

        return answer.Advisory!;
    }

    private static JsonObject Of(JsonObject advisory, string purpose) =>
        advisory["indexes"]!.AsArray().Select(index => index!.AsObject()).Single(index => index["for"]!.GetValue<string>() == purpose);

    private static List<string> Texts(JsonNode? list) => list?.AsArray().Select(each => each!.GetValue<string>()).ToList() ?? [];

    /// <summary>Creates the index exactly as explain defined it: its key document, and its collation where it names one.</summary>
    private static async Task CreateAsync(Seeded seeded, JsonObject advised, bool collation = true)
    {
        var options = new CreateIndexOptions();

        if (collation && advised["collation"] is JsonObject declared)
            options.Collation = Collation.FromBsonDocument(BsonDocument.Parse(declared.ToJsonString()));

        await seeded.Database.GetCollection<BsonDocument>(advised["collection"]!.GetValue<string>())
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(BsonDocument.Parse(advised["key"]!.ToJsonString()), options));
    }

    private const string Hide = """[{ "id": "hide", "where": { "isDeleted": { "eq": false } } }, { "id": "col:loadStart", "where": { "loadStart": { "gte": "2026-02-01T00:00:00Z" } } }]""";

    [Fact]
    public async Task The_rows_index_explain_advises_takes_the_sort_out_of_memory_and_the_page_reads_a_pages_documents()
    {
        await using var seeded = await ShipmentsAsync();
        const string pipeline = """[{ "sort": [{ "loadStart": "desc" }] }, { "page": { "limit": 10 } }]""";

        // A host that reads no index lists advises all the same, and calls nothing missing.
        var unknown = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide, source: false), "rows");

        unknown["existing"]!.GetValue<string>().Should().Be("unknown");
        unknown["used"].Should().BeNull();

        var before = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide), "rows");

        before["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"IsDeleted":1,"LoadStart":-1,"_id":1}""");
        before["existing"]!.GetValue<string>().Should().Be("missing");
        Texts(before["without"]).Should().Equal("sortsInMemory", "scansCollection");

        var scanned = await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide);

        scanned.Stages.Should().Contain("COLLSCAN").And.Contain("SORT", $"what explain said the request does: {scanned}");
        scanned.Docs.Should().Be(Rows + Others);

        // With the organisation's own index, as a service has it: the organisation's rows, still ordered in memory.
        await seeded.Database.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("OrganizationId", 1)));

        var scoped = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide), "rows");

        scoped["existing"]!.GetValue<string>().Should().Be("partial");
        Texts(scoped["lacks"]).Should().Equal("equality", "sort");
        Texts(scoped["without"]).Should().Equal("sortsInMemory", "scansOrganisation");

        var organisation = await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide);

        organisation.Stages.Should().Contain("SORT").And.NotContain("COLLSCAN");
        organisation.Docs.Should().Be(Rows, $"every row of the organisation is read: {organisation}");

        await CreateAsync(seeded, before);

        var after = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide), "rows");

        after["existing"]!.GetValue<string>().Should().Be("served");
        after["used"]!.GetValue<bool>().Should().BeTrue();
        after["id"]!.GetValue<string>().Should().Be(before["id"]!.GetValue<string>());
        after.ContainsKey("without").Should().BeFalse();

        var served = await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide);

        served.Stages.Should().Contain("IXSCAN").And.NotContain("SORT", $"the index orders the rows: {served}").And.NotContain("COLLSCAN");
        served.Docs.Should().BeLessThanOrEqualTo(11, "the page and the row that tells a next one exists");
        served.Keys.Should().BeLessThanOrEqualTo(12);
    }

    [Fact]
    public async Task A_text_that_folds_is_ordered_by_the_index_only_when_it_is_built_with_the_collation_explain_names()
    {
        await using var seeded = await ShipmentsAsync();
        const string pipeline = """[{ "sort": [{ "status.name": "asc" }] }, { "page": { "limit": 10 } }]""";
        const string filter = """[{ "id": "hide", "where": { "isDeleted": { "eq": false } } }]""";
        var advised = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, filter), "rows");

        advised["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"IsDeleted":1,"Status.Name":1,"_id":1}""");
        advised["collation"]!.ToJsonString().Should().Be("""{"locale":"de","strength":1}""");

        // The same keys without the collation: explain says the index lacks it, and the server sorts in memory.
        await CreateAsync(seeded, advised, collation: false);

        var plain = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, filter), "rows");

        plain["existing"]!.GetValue<string>().Should().Be("partial");
        Texts(plain["lacks"]).Should().Equal("sort", "collation");
        Texts(plain["without"]).Should().Equal("sortsInMemory");
        (await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, filter)).Stages.Should().Contain("SORT");

        await seeded.Database.GetCollection<BsonDocument>(Collection).Indexes.DropAllAsync();
        await CreateAsync(seeded, advised);

        Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, filter), "rows")["existing"]!.GetValue<string>().Should().Be("served");

        var served = await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, filter);

        served.Stages.Should().Contain("IXSCAN").And.NotContain("SORT", $"{served}");
        served.Docs.Should().BeLessThanOrEqualTo(11);
    }

    [Fact]
    public async Task The_index_explain_advises_for_a_list_of_values_answers_it_without_a_document()
    {
        await using var seeded = await ShipmentsAsync();
        const string pipeline = """[{ "values": { "path": "status.name", "counts": true } }]""";
        const string filter = """[{ "id": "hide", "where": { "isDeleted": { "neq": true } } }, { "id": "col:loadStart", "where": { "loadStart": { "gte": "2026-02-01T00:00:00Z" } } }]""";

        await seeded.Database.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("OrganizationId", 1)));

        var advised = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, filter), "values");

        advised["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"LoadStart":1,"Status.Name":1,"IsDeleted":1}""");
        advised.ContainsKey("collation").Should().BeFalse();
        advised["covers"]!.GetValue<bool>().Should().BeTrue();
        advised["existing"]!.GetValue<string>().Should().Be("partial");
        Texts(advised["without"]).Should().Equal("scansOrganisation", "readsDocuments");

        var before = await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, filter);

        before.Docs.Should().Be(Rows, $"every row of the organisation is read as a document: {before}");

        await CreateAsync(seeded, advised);

        Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, filter), "values")["existing"]!.GetValue<string>().Should().Be("served");

        var after = await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, filter);

        after.Docs.Should().Be(0, $"the index holds everything the request reads: {after}");
        after.Keys.Should().BeLessThan(Rows, "and the range bounds the keys read");
        after.Stages.Should().Contain("IXSCAN").And.NotContain("FETCH");
    }

    [Fact]
    public async Task The_index_explain_advises_for_a_group_bounds_its_rows_and_answers_it_without_a_document()
    {
        await using var seeded = await ShipmentsAsync();
        const string pipeline = """[{ "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "n": { "count": true }, "last": { "max": "loadStart" } } } }, { "sort": [{ "n": "desc" }] }]""";

        await seeded.Database.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("OrganizationId", 1)));

        var advised = Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide), "group");

        advised["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"IsDeleted":1,"LoadStart":1,"Status.Name":1}""");
        advised.ContainsKey("collation").Should().BeFalse();
        advised["covers"]!.GetValue<bool>().Should().BeTrue();
        Texts(advised["without"]).Should().Equal("scansOrganisation", "readsDocuments");
        (await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide)).Docs.Should().Be(Rows);

        await CreateAsync(seeded, advised);

        Of(await AdvisoryAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide), "group")["existing"]!.GetValue<string>().Should().Be("served");

        var after = await ServerAsync(seeded, Transport, Corpus.Shipment, pipeline, Hide);

        after.Docs.Should().Be(0, $"the group is computed from the index's keys: {after}");
        after.Keys.Should().BeLessThan(Rows);
        after.Stages.Should().Contain("IXSCAN").And.NotContain("FETCH");
    }

    [Fact]
    public async Task The_index_explain_advises_for_a_join_ends_the_scan_of_the_joined_collection_per_row()
    {
        var owned = await MongoFixture.CreateDatabaseAsync("index_advice_join");

        await using var seeded = new Seeded(owned);

        var vehicles = LabService.Fleet.Model.Entities[Corpus.Vehicle].Collection;
        var equipment = LabService.Fleet.Model.Entities[Corpus.Equipment].Collection;
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();

        await owned.Database.GetCollection<BsonDocument>(vehicles).InsertManyAsync(ids.Select((id, n) => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["MatchCode"] = "V-" + n,
        }));

        const string pipeline = """[{ "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "equipment" } }, { "match": { "equipment.name": { "exists": true } } }]""";
        var advised = Of(await AdvisoryAsync(seeded, Fleet, Corpus.Vehicle, pipeline), "join");
        var field = advised["keys"]!.AsArray().Single(key => key!["role"]!.GetValue<string>() == "join")!["field"]!.GetValue<string>();

        await owned.Database.GetCollection<BsonDocument>(equipment).InsertManyAsync(Enumerable.Range(0, 2_000).Select(n =>
        {
            var document = new BsonDocument
            {
                ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
                ["OrganizationId"] = EngineDirect.OrganisationValue,
                ["Name"] = "E-" + n,
            };

            // The member joined on, wherever the model stores it.
            var parts = field.Split('.');
            var holder = document;

            foreach (var part in parts[..^1])
                holder = (BsonDocument)(holder[part] = new BsonDocument());

            holder[parts[^1]] = new BsonBinaryData(ids[n % ids.Count], GuidRepresentation.Standard);

            return document;
        }));

        advised["collection"]!.GetValue<string>().Should().Be(equipment);
        advised["key"]!.AsObject().Select(pair => pair.Key).Should().Equal("OrganizationId", field);
        advised["existing"]!.GetValue<string>().Should().Be("missing");
        Texts(advised["without"]).Should().Equal("scansPerRow");

        var before = await ServerAsync(seeded, Fleet, Corpus.Vehicle, pipeline);
        var lookupBefore = before.Raw["stages"].AsBsonArray.Select(stage => stage.AsBsonDocument).Single(stage => stage.Contains("$lookup"));

        // Every row of the joined collection is read for every row that joins (here along the key's index, which the join's own order picks).
        lookupBefore["totalDocsExamined"].ToInt64().Should().Be(2_000 * ids.Count, $"the joined collection is read once per row: {lookupBefore}");

        await CreateAsync(seeded, advised);

        Of(await AdvisoryAsync(seeded, Fleet, Corpus.Vehicle, pipeline), "join")["existing"]!.GetValue<string>().Should().Be("served");

        var after = await ServerAsync(seeded, Fleet, Corpus.Vehicle, pipeline);
        var lookupAfter = after.Raw["stages"].AsBsonArray.Select(stage => stage.AsBsonDocument).Single(stage => stage.Contains("$lookup"));

        lookupAfter["collectionScans"].ToInt64().Should().Be(0, $"{lookupAfter}");
        lookupAfter["indexesUsed"].AsBsonArray.Select(name => name.AsString).Should().NotContain("_id_").And.ContainSingle();
        lookupAfter["totalDocsExamined"].ToInt64().Should().Be(2_000, "each joined row is read once, for the row it belongs to");
    }
}
