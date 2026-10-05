using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The differential behind every stage the compiler places elsewhere than the request wrote it: the
/// same request compiled in the order written (<see cref="CompileOptions.AsWritten"/>, what contract 1
/// runs and what contract 2 ran before) and as the engine now places its stages, both run on the
/// server over the corpus, answer the same rows in the same order. And a walk by cursor in small
/// pages answers the rows of one page that holds them all, so a cursor continues exactly where its
/// page ended whatever the shape (rows, unwound rows, groups).
/// </summary>
[Trait("Category", "Integration")]
public class ShapeDifferentialTests
{
    private const string Columns = """ "shipmentNumber": 1, "loadStart": 1, "status.name": 1, "customer.address.companyName": 1 """;

    /// <summary>The reference requests: entity, then the stages before the page.</summary>
    public static readonly TheoryData<string, string, string> Requests = new()
    {
        // ---- rows: a projection before and after the sort, with filters around it ----
        { "rows-project-sort", Corpus.Shipment, $$"""[ { "project": { {{Columns}} } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "rows-sort-project", Corpus.Shipment, $$"""[ { "sort": [ { "loadStart": "desc" } ] }, { "project": { {{Columns}} } } ]""" },
        { "rows-project-match-sort", Corpus.Shipment, $$"""[ { "project": { {{Columns}} } }, { "match": { "status.name": { "neq": "Closed" } } }, { "sort": [ { "customer.address.companyName": "asc" }, { "shipmentNumber": "desc" } ] } ]""" },
        { "rows-exclusion", Corpus.Shipment, """[ { "project": { "items": 0, "billingLines": 0, "id": 0 } }, { "sort": [ { "referenceNumber": "asc" } ] } ]""" },
        { "rows-project-drops-sort-key", Corpus.Shipment, """[ { "sort": [ { "loadStart": "asc" } ] }, { "project": { "shipmentNumber": 1 } } ]""" },
        { "rows-two-projections", Corpus.Shipment, $$"""[ { "project": { {{Columns}}, "referenceNumber": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] }, { "project": { "referenceNumber": 0 } } ]""" },
        { "rows-default-order", Corpus.Shipment, $$"""[ { "project": { {{Columns}} } } ]""" },
        { "rows-volume-nulls-and-ties", Corpus.Template, """[ { "project": { "templateName": 1, "shipmentNumber": 1 } }, { "sort": [ { "templateName": "asc" } ] } ]""" },
        { "rows-volume-desc", Corpus.Template, """[ { "match": { "isDeleted": { "neq": true } } }, { "project": { "templateName": 1 } }, { "sort": [ { "templateName": "desc" } ] } ]""" },
        { "rows-inline-join-display", Corpus.Equipment, """[ { "resolve": { "path": "vehicle.id", "as": "veh" } }, { "project": { "id": 1, "name": 1, "veh.matchCode": 1 } }, { "sort": [ { "name": "asc" } ] } ]""" },
        { "rows-inline-join-sorted", Corpus.Equipment, """[ { "resolve": { "path": "vehicle.id", "as": "veh" } }, { "project": { "id": 1, "name": 1, "veh.matchCode": 1 } }, { "sort": [ { "veh.matchCode": "asc" } ] } ]""" },
    };

    /// <summary>The request as the engine places its stages and in the order written answers the same rows in the same order.</summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public async Task A_placed_stage_answers_the_rows_of_the_request_as_written(string id, string entity, string stages)
    {
        var (direct, database) = await HostAsync(entity);
        var limit = entity == Corpus.Template ? 499 : 7;
        var placed = await WalkAsync(direct, database, entity, stages, limit, asWritten: false);
        var written = await WalkAsync(direct, database, entity, stages, limit, asWritten: true);

        placed.Rows.Should().NotBeEmpty(id);
        placed.Rows.Should().Equal(written.Rows, $"{id}: the same rows in the same order");
        placed.Pages.Should().Be(written.Pages, id);
    }

    /// <summary>A walk in small pages answers the rows of the one page that holds them all.</summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public async Task A_walk_by_cursor_answers_the_rows_of_one_page_that_holds_them_all(string id, string entity, string stages)
    {
        var (direct, database) = await HostAsync(entity);
        var whole = await WalkAsync(direct, database, entity, stages, limit: 20_000, asWritten: false);
        var walked = await WalkAsync(direct, database, entity, stages, limit: entity == Corpus.Template ? 997 : 3, asWritten: false);

        whole.Pages.Should().Be(1, id);
        walked.Pages.Should().BeGreaterThan(1, id);
        walked.Rows.Should().Equal(whole.Rows, $"{id}: no row twice, none left out, the order kept");
    }

    private static async Task<(EngineDirect Direct, IMongoDatabase Database)> HostAsync(string entity)
    {
        var service = Corpus.Entity(entity).Service;

        return (new EngineDirect(service.Model), await (await CorpusFleet.SharedAsync()).Fleet.DatabaseAsync(service));
    }

    /// <summary>
    /// Every row of the request, walked by cursor: bound, compiled (in the order written or as the
    /// engine places the stages), run on the server, and encoded as the wire writes a row.
    /// </summary>
    internal static async Task<(IReadOnlyList<string> Rows, int Pages)> WalkAsync(EngineDirect direct, IMongoDatabase database, string entity, string stages, int limit, bool asWritten)
    {
        var options = direct.Options(configure => configure.Limits.MaxPageSize = 50_000);
        var context = direct.Context(options);
        var rows = new List<string>();
        string? cursor = null;
        long served = 0;

        for (var pages = 1; ; pages++)
        {
            pages.Should().BeLessThan(5_000, "the walk ends");

            var pipeline = (JsonArray)JsonNode.Parse(stages)!;
            var page = new JsonObject { ["limit"] = limit };

            if (cursor is not null)
                page["cursor"] = cursor;

            pipeline.Add(new JsonObject { ["page"] = page });

            var bound = await direct.BindAsync(entity, pipeline.ToJsonString(), context);
            var compiled = MongoCompiler.Compile(bound, new CompileOptions(30_000, true, 100_000) { KeepDiscriminators = true, AsWritten = asWritten });
            var answered = await EngineDirect.RunAsync(database, bound.Entity.Collection, compiled);
            var taken = answered.Take(limit).ToList();

            // The next cursor is minted off the row as the server answered it, before anything is encoded.
            var next = answered.Count > limit ? direct.Cursors.Encode(MongoQueryEngine.NextCursor(compiled, taken[^1], served += taken.Count)) : null;

            rows.AddRange(taken.Select(row => WireEncoder.Encode(row, bound).ToJsonString()));

            if (next is null)
                return (rows, pages);

            next.Should().NotBe(cursor, "a cursor is never repeated");
            cursor = next;
        }
    }
}
