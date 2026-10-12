using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The <c>null</c> of a <c>values</c> list over a path through a collection against the condition a
/// client builds from it, on rows of the test's own: a collection that is not there, one that is
/// <c>null</c>, one that is empty, and collections whose elements hold the member in all, some or
/// none of them. What holds: a value of the list, sent back as <c>in</c>, selects exactly the rows
/// the list counted elements of, and <c>null</c> is a value like the others. A row whose
/// collection is there and empty holds no value: no entry counts it and no comparison selects it;
/// it is asked for with <c>exists: false</c> on a member of the element.
/// </summary>
[Trait("Category", "Integration")]
public class ValuesNullServerTests
{
    private static string Collection => LabService.Transport.Model.Entities[Corpus.Shipment].Collection;

    private static readonly EngineDirect Direct = new(LabService.Transport.Model);

    private static BsonDocument Item(BsonValue? name, bool status = true)
    {
        var item = new BsonDocument("_id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard));

        if (!status)
            return item;

        var held = new BsonDocument();

        if (name is not null)
            held["Name"] = name;

        item["Status"] = held;

        return item;
    }

    /// <summary>The rows by a number that says what their collection is.</summary>
    private static readonly IReadOnlyList<(string Number, BsonValue? Items)> Seed =
    [
        ("absent", null),
        ("absent-too", null),
        ("null", BsonNull.Value),
        ("empty", new BsonArray()),
        ("empty-too", new BsonArray()),
        ("empty-three", new BsonArray()),
        ("all-null", new BsonArray { Item(BsonNull.Value), Item(BsonNull.Value) }),
        ("all-missing", new BsonArray { Item(null), Item(null, status: false) }),
        ("some-null", new BsonArray { Item("open"), Item(BsonNull.Value), Item("done") }),
        ("some-missing", new BsonArray { Item("open"), Item(null, status: false) }),
        ("none-null", new BsonArray { Item("open"), Item("open"), Item("done") }),
        ("one", new BsonArray { Item("closed") }),
    ];

    private sealed class Rows(TestDatabase owned, IQueryEngine engine) : IAsyncDisposable
    {
        private readonly RequestContext context = Direct.Context();

        public async Task<QueryResult> AnswerAsync(string pipeline)
        {
            var outcome = await engine.ExecuteAsync(EngineDirect.Request(Corpus.Shipment, pipeline), context);

            outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "");

            return ((QueryOutcome.Success)outcome).Result;
        }

        /// <summary>The list of a path: value (null as null) to its count.</summary>
        public async Task<List<(string? Value, int Count)>> ListAsync(string path) =>
            (await AnswerAsync($$"""[ { "values": { "path": "{{path}}", "counts": true } } ]""")).Items
                .Select(entry => (entry!["value"]?.GetValue<string>(), entry["count"]!.GetValue<int>())).ToList();

        /// <summary>The numbers of the rows a condition selects, in order.</summary>
        public async Task<List<string>> SelectedAsync(string condition) =>
            (await AnswerAsync($$"""[ { "match": {{condition}} }, { "sort": [{ "shipmentNumber": "asc" }] }, { "project": { "shipmentNumber": 1 } }, { "page": { "limit": 100 } } ]""")).Items
                .Select(row => row!["shipmentNumber"]!.GetValue<string>()).ToList();

        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    private static async Task<Rows> SeedAsync()
    {
        var owned = await MongoFixture.CreateDatabaseAsync("values_null");

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(Seed.Select(row =>
        {
            var stored = new BsonDocument
            {
                ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
                ["OrganizationId"] = EngineDirect.OrganisationValue,
                ["ShipmentNumber"] = row.Number,
            };

            if (row.Items is not null)
                stored["Items"] = row.Items;

            return stored;
        }));

        return new Rows(owned, Direct.Engine(await MongoFixture.ClientAsync(), owned.Name));
    }

    private static string[] Sorted(params string[] numbers) => [.. numbers.Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_null_of_a_list_under_a_collection_counts_what_a_comparison_with_null_selects()
    {
        await using var rows = await SeedAsync();

        var list = await rows.ListAsync("items.status.name");

        // open: some-null, some-missing and twice in none-null; done: some-null, none-null; closed: one.
        // null: the collection that is not there (absent, absent-too, null) and every element without a name:
        // two in all-null, two in all-missing, one in some-null, one in some-missing. No entry for the three empty ones.
        list.Should().BeEquivalentTo(new[] { ((string?)null, 9), ("open", 4), ("done", 2), ("closed", 1) });

        var expected = Sorted("absent", "absent-too", "null", "all-null", "all-missing", "some-null", "some-missing");

        (await rows.SelectedAsync("""{ "items.status.name": { "in": [null] } }""")).Should().Equal(expected);
        (await rows.SelectedAsync("""{ "items.status.name": { "eq": null } }""")).Should().Equal(expected);
        (await rows.SelectedAsync("""{ "items.status.name": null }""")).Should().Equal(expected);

        // A row whose collection is empty is selected by no value of the list, null included.
        var listed = string.Join(", ", list.Select(entry => entry.Value is null ? "null" : $"\"{entry.Value}\""));
        var byAnyValue = await rows.SelectedAsync($$"""{ "items.status.name": { "in": [{{listed}}] } }""");

        byAnyValue.Should().Equal(Sorted([.. Seed.Select(row => row.Number).Where(number => !number.StartsWith("empty", StringComparison.Ordinal))]));
    }

    [Fact]
    public async Task Every_value_of_the_list_sent_back_selects_the_rows_it_counted_elements_of()
    {
        await using var rows = await SeedAsync();

        (await rows.SelectedAsync("""{ "items.status.name": { "in": ["open"] } }""")).Should().Equal(Sorted("some-null", "some-missing", "none-null"));
        (await rows.SelectedAsync("""{ "items.status.name": { "in": ["done"] } }""")).Should().Equal(Sorted("some-null", "none-null"));
        (await rows.SelectedAsync("""{ "items.status.name": { "in": ["closed"] } }""")).Should().Equal("one");
        (await rows.SelectedAsync("""{ "items.status.name": { "in": ["closed", null] } }""")).Should().HaveCount(8);

        // Without counts the list holds the same values.
        (await rows.AnswerAsync("""[ { "values": { "path": "items.status.name" } } ]""")).Items
            .Select(entry => entry!["value"]?.GetValue<string>()).Should().Equal(null, "closed", "done", "open");
    }

    [Fact]
    public async Task The_rows_without_any_element_are_asked_for_with_exists_false_on_a_member_of_the_element()
    {
        await using var rows = await SeedAsync();

        var without = Sorted("absent", "absent-too", "null", "empty", "empty-too", "empty-three");

        (await rows.SelectedAsync("""{ "items.id": { "exists": false } }""")).Should().Equal(without);
        (await rows.SelectedAsync("""{ "not": { "items": { "any": { "id": { "exists": true } } } } }""")).Should().Equal(without);

        // Together with the values of the list they are every row, each once.
        var withSome = await rows.SelectedAsync("""{ "items.id": { "exists": true } }""");

        withSome.Concat(without).Order(StringComparer.Ordinal).Should().Equal(Sorted([.. Seed.Select(row => row.Number)]));
    }

    [Fact]
    public async Task A_list_of_rows_that_all_hold_an_empty_collection_has_no_value_and_still_reads_its_rows()
    {
        await using var rows = await SeedAsync();

        var answer = await rows.AnswerAsync("""[ { "match": { "shipmentNumber": { "startsWith": "empty" } } }, { "values": { "path": "items.status.name", "counts": true, "scan": 3 } } ]""");

        answer.Items.Should().BeEmpty("an empty collection holds no value");
        answer.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Contain(Codes.ValuesScanCapped, "the three rows were read: the bound on the rows counts them");

        (await rows.AnswerAsync("""[ { "match": { "shipmentNumber": { "startsWith": "empty" } } }, { "values": { "path": "items.status.name", "scan": 4 } } ]""")).Diagnostics.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Under_two_collections_an_empty_inner_collection_holds_no_value_and_an_absent_one_is_a_null()
    {
        var owned = await MongoFixture.CreateDatabaseAsync("values_null_nested");

        await using var rows = new Rows(owned, Direct.Engine(await MongoFixture.ClientAsync(), owned.Name));

        BsonDocument Row(string number, BsonValue? items) => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["ShipmentNumber"] = number,
        }.Merge(items is null ? [] : new BsonDocument("Items", items));

        static BsonDocument Noted(BsonValue? notes)
        {
            var item = new BsonDocument("_id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard));

            if (notes is not null)
                item["WeightNotes"] = notes;

            return item;
        }

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(
        [
            Row("no-items", null),
            Row("empty-items", new BsonArray()),
            Row("item-without-notes", new BsonArray { Noted(null) }),
            Row("item-with-empty-notes", new BsonArray { Noted(new BsonArray()) }),
            Row("note-without-number", new BsonArray { Noted(new BsonArray { new BsonDocument("Weight", 1) }) }),
            Row("note-with-number", new BsonArray { Noted(new BsonArray { new BsonDocument("Number", "W-1") }), Noted(new BsonArray()) }),
        ]);

        (await rows.ListAsync("items.weightNotes.number")).Should().BeEquivalentTo(new[] { ((string?)null, 3), ("W-1", 1) });
        (await rows.SelectedAsync("""{ "items.weightNotes.number": { "in": [null] } }""")).Should().Equal(Sorted("no-items", "item-without-notes", "note-without-number"));
        (await rows.SelectedAsync("""{ "items.weightNotes.number": { "in": ["W-1"] } }""")).Should().Equal("note-with-number");
    }

    [Fact]
    public async Task A_scalar_path_lists_null_for_the_rows_without_the_member_as_before()
    {
        await using var rows = await SeedAsync();

        var owner = await rows.ListAsync("carrier.accountNumber");

        owner.Should().Equal(((string?)null, Seed.Count));
        (await rows.SelectedAsync("""{ "carrier.accountNumber": { "in": [null] } }""")).Should().HaveCount(Seed.Count);
        (await rows.ListAsync("shipmentNumber")).Should().HaveCount(Seed.Count).And.OnlyContain(entry => entry.Count == 1);
    }
}
