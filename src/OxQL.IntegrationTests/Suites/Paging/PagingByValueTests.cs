using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Transport;
using OxQL.IntegrationTests.Harness;
using Xunit;
using StoredShipment = OxQL.IntegrationTests.Fleet.Models.Transport.Shipment;

namespace OxQL.IntegrationTests.Suites.Paging;

/// <summary>
/// Unwound rows and groups page by value (DG2 M5, S5): the next page starts after the last row's
/// sort values, its key and its position, so what is written or deleted before that position
/// between two pages moves nothing. A page addressed by counting rows would repeat a row for
/// every row inserted before it and skip one for every row deleted.
/// </summary>
[Trait("Category", "Integration")]
public class PagingByValueTests
{
    private static StoredShipment Shipment(int n, string number, int items) => new()
    {
        Id = Ids.Of(Spaces.Shipment, Org.C, n),
        OrganizationId = Org.C.Id(),
        ShipmentNumber = number,
        ReferenceNumber = "ref-" + (n % 4),
        Items = [.. Enumerable.Range(0, items).Select(position => new ShipmentItem { Id = Ids.Of(Spaces.ShipmentItem, Org.C, n * 10 + position), Text = $"{number}/{position}", OrderNumber = position })],
    };

    private static async Task<(CorpusFleet Fleet, LabClient Client, IMongoCollection<BsonDocument> Shipments)> SeedAsync(string purpose)
    {
        var fleet = await CorpusFleet.CreateAsync(purpose, [LabService.Transport]);
        var shipments = (await fleet.Fleet.DatabaseAsync(LabService.Transport)).GetCollection<BsonDocument>(Corpus.Entity(Corpus.Shipment).Collection);

        // Twelve shipments S-10 … S-120 with three items each: thirty-six unwound rows.
        await shipments.InsertManyAsync(Enumerable.Range(1, 12).Select(n => Shipment(n, $"S-{n * 10:D3}", 3).ToBsonDocument()));

        return (fleet, fleet.Client(LabService.Transport, Org.C), shipments);
    }

    private static List<string> Texts(WireAnswer answer) => answer.Items.Select(item => item!["item"]!["text"]!.GetValue<string>()).ToList();

    [Theory]
    [InlineData("""{ "sort": [ { "shipmentNumber": "asc" } ] }""")]
    [InlineData("""{ "sort": [ { "item.text": "asc" } ] }""")]
    public async Task Rows_written_and_deleted_before_the_position_between_two_pages_move_no_unwound_row(string sort)
    {
        var (fleet, client, shipments) = await SeedAsync("paging_by_value_unwind");

        await using var owned = fleet;

        var stages = $$"""{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "project": { "shipmentNumber": 1, "position": 1, "item.text": 1 } }, {{sort}}""";
        var whole = Texts(await client.SendAsync(Corpus.Shipment, $$"""[ {{stages}}, { "page": { "limit": 100 } } ]"""));

        whole.Should().HaveCount(36);

        // The first page ends inside the fourth shipment (ten rows of three per shipment).
        var first = await client.SendAsync(Corpus.Shipment, $$"""[ {{stages}}, { "page": { "limit": 10 } } ]""");

        Texts(first).Should().Equal(whole.Take(10));

        // Before the position: a new shipment with three items is written, and the first one is deleted.
        await shipments.InsertOneAsync(Shipment(50, "S-015", 3).ToBsonDocument());
        await shipments.DeleteOneAsync(new BsonDocument("_id", new BsonBinaryData(Ids.Of(Spaces.Shipment, Org.C, 1), GuidRepresentation.Standard)));

        var rest = new List<string>();
        var cursor = first.NextCursor;

        while (cursor is not null)
        {
            var next = await client.SendAsync(Corpus.Shipment, $$"""[ {{stages}}, { "page": { "limit": 10, "cursor": "{{cursor}}" } } ]""");

            next.ShouldBeOk();
            rest.AddRange(Texts(next));
            cursor = next.NextCursor;
        }

        rest.Should().Equal(whole.Skip(10), "the rows after the position are the rows that were after it: none again, none left out");
    }

    /// <summary>
    /// The rows of an unwound lookup alias share their parent's key like any unwound rows, though the
    /// shape still counts them as the entity's: they are ordered and continued by the key and the
    /// position. Continued by the key alone, a walk lost the children a page boundary cut off their
    /// parent (9 children in pages of 2 answered 6).
    /// </summary>
    [Fact]
    public async Task A_walk_over_the_unwound_children_of_a_lookup_answers_every_child()
    {
        await using var fleet = await CorpusFleet.CreateAsync("paging_by_value_lookup", [LabService.Transport]);
        var database = await fleet.Fleet.DatabaseAsync(LabService.Transport);

        await database.GetCollection<BsonDocument>(Corpus.Entity(Corpus.Shipment).Collection).InsertManyAsync(Enumerable.Range(1, 3).Select(n => Shipment(n, "S-" + n, 0).ToBsonDocument()));
        await database.GetCollection<BsonDocument>(LabService.Transport.Model.Entities["transport.delivery_attempt"].Collection).InsertManyAsync(Enumerable.Range(1, 9).Select(n =>
            new DeliveryAttempt { Id = Ids.Of(Spaces.DeliveryAttempt, Org.C, n), OrganizationId = Org.C.Id(), ShipmentId = Ids.Of(Spaces.Shipment, Org.C, 1 + (n % 3)), Text = "attempt " + n }.ToBsonDocument()));

        var client = fleet.Client(LabService.Transport, Org.C);
        const string Stages = """[ { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts" } }, { "unwind": { "path": "attempts", "as": "attempt" } }, { "project": { "shipmentNumber": 1, "attempt.text": 1 } } ]""";
        var whole = await client.WalkAsync(Corpus.Shipment, Stages, limit: 100);
        var walked = await client.WalkAsync(Corpus.Shipment, Stages, limit: 2);

        whole.Items.Should().HaveCount(9);
        walked.Items.Select(item => item.ToJsonString()).Should().Equal(whole.Items.Select(item => item.ToJsonString()), "three parents with three children each, in pages of two: no child is lost at a page boundary");
        walked.Pages.Should().Be(5);
    }

    [Fact]
    public async Task A_group_that_appears_before_the_position_between_two_pages_moves_no_group()
    {
        var (fleet, client, shipments) = await SeedAsync("paging_by_value_group");

        await using var owned = fleet;

        const string Stages = """{ "group": { "by": [ { "path": "shipmentNumber", "as": "number" } ], "fields": { "n": { "count": true } } } }""";
        var whole = (await client.SendAsync(Corpus.Shipment, $$"""[ {{Stages}}, { "page": { "limit": 100 } } ]""")).Items.Select(item => item!["number"]!.GetValue<string>()).ToList();

        whole.Should().HaveCount(12);

        var first = await client.SendAsync(Corpus.Shipment, $$"""[ {{Stages}}, { "page": { "limit": 5 } } ]""");

        // A new group that sorts before the position, and one before it that disappears.
        await shipments.InsertOneAsync(Shipment(50, "S-015", 1).ToBsonDocument());
        await shipments.DeleteOneAsync(new BsonDocument("_id", new BsonBinaryData(Ids.Of(Spaces.Shipment, Org.C, 2), GuidRepresentation.Standard)));

        var second = await client.SendAsync(Corpus.Shipment, $$"""[ {{Stages}}, { "page": { "limit": 5, "cursor": "{{first.NextCursor}}" } } ]""");

        second.Items.Select(item => item!["number"]!.GetValue<string>()).Should().Equal(whole.Skip(5).Take(5), "the next groups are the groups after the last one served");
        second.Count!["state"]!.GetValue<string>().Should().Be("atLeast");
        second.Count["value"]!.GetValue<long>().Should().Be(11, "five and five served, one more known");
    }
}
