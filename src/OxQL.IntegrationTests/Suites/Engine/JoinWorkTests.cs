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
/// How often a join runs, counted on the server: the rows the <c>$lookup</c> of a request read, taken
/// from the execution statistics of the aggregate the engine sends. A join costs one sub-query per
/// row it reads, which no command count shows, so a placement that makes it run once per element of
/// a parent it was written to read once fails here and not as latency on a volume.
/// </summary>
[Trait("Category", "Integration")]
public class JoinWorkTests
{
    private const string Latest = """{ "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "latest", "sort": [ { "dateTime": "desc" } ], "first": true } }""";

    private const string Items = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""";

    private const string Columns = """{ "project": { "shipmentNumber": 1, "position": 1, "item.text": 1, "latest.text": 1, "latest.dateTime": 1 } }""";

    /// <summary>A shipment of the differential's database with four items and delivery attempts.</summary>
    private static readonly string OneShipment = Ids.Of(Spaces.Shipment, Org.A, 5_024).ToString("D");

    /// <summary>The rows each <c>$lookup</c> of the request's page aggregate read, in stage order, the kinds of its stages, and the rows it answers.</summary>
    private static async Task<(IReadOnlyList<long> Joined, List<string> Kinds, int Rows)> RunAsync(string stages, int limit)
    {
        var (direct, database) = await ShapeDifferentialTests.HostAsync(Corpus.Shipment);
        var bound = await direct.BindAsync(Corpus.Shipment, stages.TrimEnd().TrimEnd(']') + $$""", { "page": { "limit": {{limit}} } } ]""");
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(30_000, true, 100_000) { KeepDiscriminators = true });
        var aggregate = new BsonDocument
        {
            ["aggregate"] = bound.Entity.Collection,
            ["pipeline"] = new BsonArray(compiled.PageStages),
            ["cursor"] = new BsonDocument(),
        };

        if (compiled.Collation is { } collation)
            aggregate["collation"] = collation;

        var explained = await database.RunCommandAsync<BsonDocument>(new BsonDocument { ["explain"] = aggregate, ["verbosity"] = "executionStats" });
        var joined = explained["stages"].AsBsonArray.Select(stage => stage.AsBsonDocument).Where(stage => stage.Contains("$lookup")).Select(stage => stage["nReturned"].ToInt64()).ToList();
        var rows = await EngineDirect.RunAsync(database, bound.Entity.Collection, compiled);

        return (joined, compiled.PageStages.Select(stage => stage.GetElement(0).Name).ToList(), rows.Count);
    }

    [Fact]
    public async Task A_lookup_written_before_the_unwind_of_one_row_runs_once_and_not_once_per_element()
    {
        // The studio's example shape: one record, its latest child, its elements in order.
        var (joined, kinds, rows) = await RunAsync($$"""[ { "match": { "id": { "eq": "{{OneShipment}}" } } }, {{Latest}}, {{Items}}, {{Columns}}, { "sort": [ { "position": "asc" } ] } ]""", 100);

        rows.Should().Be(4, "the shipment has four items");
        joined.Should().Equal([1L], "one parent is read: the join runs once, not once per unwound row");
        kinds.IndexOf("$lookup").Should().BeLessThan(kinds.IndexOf("$unwind"));
    }

    [Fact]
    public async Task A_lookup_written_before_the_unwind_of_a_page_ordered_by_the_parents_runs_on_the_parents_cut_first()
    {
        var (joined, kinds, rows) = await RunAsync($$"""[ {{Latest}}, {{Items}}, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""", 7);

        rows.Should().Be(8, "a full page and the row that tells a next one exists");
        joined.Should().Equal([8L], "the parents are cut to what the page can reach before the join reads them");
        kinds.Should().ContainInConsecutiveOrder("$sort", "$limit", "$lookup");
        kinds.IndexOf("$lookup").Should().BeLessThan(kinds.IndexOf("$unwind"));
    }

    [Fact]
    public async Task A_lookup_written_before_the_unwind_of_a_page_ordered_by_the_element_runs_once_per_parent_where_it_stands()
    {
        var (joined, kinds, _) = await RunAsync($$"""[ { "match": { "referenceNumber": { "eq": "ref-3" } } }, {{Latest}}, {{Items}}, {{Columns}}, { "sort": [ { "item.text": "asc" } ] } ]""", 7);
        var (direct, database) = await ShapeDifferentialTests.HostAsync(Corpus.Shipment);
        var parents = await database.GetCollection<BsonDocument>(direct.Model.Entities[Corpus.Shipment].Collection)
            .CountDocumentsAsync(new BsonDocument { ["ReferenceNumber"] = "ref-3", ["OrganizationId"] = EngineDirect.OrganisationValue });

        joined.Should().Equal([parents], "once per parent that reaches it, whatever the number of their elements");
        kinds.IndexOf("$lookup").Should().BeLessThan(kinds.IndexOf("$unwind"));
    }

    [Fact]
    public async Task A_lookup_written_after_the_unwind_runs_for_the_rows_of_the_page()
    {
        var (joined, kinds, rows) = await RunAsync($$"""[ {{Items}}, {{Latest}}, {{Columns}}, { "sort": [ { "item.text": "asc" } ] } ]""", 7);

        rows.Should().Be(8);
        joined.Should().Equal([8L], "it was written to run per element: the page holds no more rows than it would have read there");
        kinds.IndexOf("$lookup").Should().BeGreaterThan(kinds.LastIndexOf("$limit"));
    }
}
