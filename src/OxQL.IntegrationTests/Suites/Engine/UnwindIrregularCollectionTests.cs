using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Transport;
using StoredShipment = OxQL.IntegrationTests.Fleet.Models.Transport.Shipment;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// An unwind over a collection that is not stored the way its type says: an array that holds a null
/// beside its elements or nothing but nulls, an empty array, a null, a member that is not stored, a
/// single object where the array should be (a member that was once one object), and a scalar. The
/// database's <c>$unwind</c> answers a row for every element of an array, a null one included, one row
/// for a value that is no array, and none for an empty array, a null or a missing member. Whatever the
/// engine does before the unwind to read fewer rows (the parents cut first, the parents ordered by the
/// position their rows start at, the element conditions asked of the collection) answers exactly those
/// rows: the same rows in the same order as the request compiled in the order written, the same count,
/// and the same rows in a walk by cursor of any page size.
/// </summary>
[Trait("Category", "Integration")]
public class UnwindIrregularCollectionTests
{
    private const string Items = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""";

    private const string Columns = """{ "project": { "shipmentNumber": 1, "referenceNumber": 1, "position": 1, "item.text": 1, "item.orderNumber": 1 } }""";

    /// <summary>The requests: the stages before the page.</summary>
    public static readonly TheoryData<string, string> Requests = new()
    {
        // ---- the parents cut first: ordered by the entity's members, then the key ----
        { "default-order", $$"""[ {{Items}}, {{Columns}} ]""" },
        { "sort-parent", $$"""[ {{Items}}, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "sort-parent-desc", $$"""[ {{Items}}, { "sort": [ { "referenceNumber": "desc" } ] }, {{Columns}} ]""" },
        { "sort-parent-key-desc", $$"""[ {{Items}}, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" }, { "id": "desc" } ] } ]""" },
        { "match-parent", $$"""[ {{Items}}, { "match": { "referenceNumber": { "neq": "ref-1" } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },
        { "without-index", """[ { "unwind": { "path": "items", "as": "item" } }, { "project": { "shipmentNumber": 1, "item.text": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },

        // ---- the parents ordered by the position their rows start at ----
        { "sort-parent-then-position", $$"""[ {{Items}}, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""" },
        { "sort-parent-then-position-desc", $$"""[ {{Items}}, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "desc" } ] } ]""" },
        { "sort-parent-desc-then-position", $$"""[ {{Items}}, { "sort": [ { "referenceNumber": "desc" }, { "position": "asc" } ] }, {{Columns}} ]""" },
        { "sort-position", $$"""[ {{Items}}, {{Columns}}, { "sort": [ { "position": "asc" } ] } ]""" },
        { "sort-position-desc", $$"""[ {{Items}}, {{Columns}}, { "sort": [ { "position": "desc" } ] } ]""" },
        { "sort-parent-position-key-desc", $$"""[ {{Items}}, { "sort": [ { "referenceNumber": "asc" }, { "position": "asc" }, { "id": "desc" } ] }, {{Columns}} ]""" },
        { "sort-parent-position-desc-key-desc", $$"""[ {{Items}}, { "sort": [ { "referenceNumber": "asc" }, { "position": "desc" }, { "id": "desc" } ] }, {{Columns}} ]""" },

        // ---- conditions on the element, asked of the collection before the unwind ----
        { "match-element", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 2 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-text", $$"""[ {{Items}}, { "match": { "item.text": { "startsWith": "Ware" } } }, {{Columns}} ]""" },
        { "match-element-negative", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "neq": 1 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-null", $$"""[ {{Items}}, { "match": { "item.text": { "eq": null } } }, {{Columns}}, { "sort": [ { "referenceNumber": "asc" } ] } ]""" },
        { "match-element-absent", $$"""[ {{Items}}, { "match": { "item.text": { "exists": false } } }, {{Columns}} ]""" },
        { "match-element-not", $$"""[ {{Items}}, { "match": { "not": { "item.orderNumber": { "gte": 2 } } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },
        { "match-element-nin", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "nin": [1, 2] } } }, {{Columns}} ]""" },
        { "match-element-or", $$"""[ {{Items}}, { "match": { "or": [ { "item.orderNumber": { "eq": 1 } }, { "item.text": { "eq": null } } ] } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-and-position", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 1 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""" },
        { "match-parent-and-element", $$"""[ {{Items}}, { "match": { "referenceNumber": { "neq": "ref-1" }, "item.orderNumber": { "lte": 2 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },

        // ---- a row kept for a collection that holds nothing ----
        { "preserve-null", $$"""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
    };

    /// <summary>How each kind of collection is stored, by the remainder of the shipment's number.</summary>
    private static readonly string[] Kinds = ["two", "elementThenNull", "onlyNull", "nullThenElement", "scalar", "oneObject", "empty", "null", "missing", "three", "nullBetween"];

    private const int Shipments = 77;

    private static readonly Lazy<Task<IMongoDatabase>> Irregular = new(SeedAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static async Task<IMongoDatabase> SeedAsync()
    {
        var database = (await MongoFixture.CreateDatabaseAsync("unwind_irregular")).Database;
        var shipments = new List<BsonDocument>();

        for (var n = 1; n <= Shipments; n++)
        {
            var scrambled = n * 37 % Shipments;

            BsonDocument Item(int position) => new ShipmentItem
            {
                Id = Ids.Of(Spaces.ShipmentItem, Org.A, 70_000 + n * 10 + position),
                Text = (n + position) % 5 == 0 ? null : $"Ware {(char)('a' + (n * 7 + position * 3) % 9)}",
                OrderNumber = (n + position) % 4,
            }.ToBsonDocument();

            var document = new StoredShipment
            {
                Id = Ids.Of(Spaces.Shipment, Org.A, 7_000 + n),
                OrganizationId = EngineDirect.Organisation,
                // Ties on every sort field, and nulls: the parents of one number hold every kind of collection.
                ShipmentNumber = n % 13 == 0 ? null : $"S-{scrambled % 6:D2}",
                ReferenceNumber = n % 9 == 0 ? null : $"ref-{scrambled % 4}",
            }.ToBsonDocument();

            switch (Kinds[n % Kinds.Length])
            {
                case "two": document["Items"] = new BsonArray { Item(0), Item(1) }; break;
                case "three": document["Items"] = new BsonArray { Item(0), Item(1), Item(2) }; break;
                case "elementThenNull": document["Items"] = new BsonArray { Item(0), BsonNull.Value }; break;
                case "onlyNull": document["Items"] = new BsonArray { BsonNull.Value }; break;
                case "nullThenElement": document["Items"] = new BsonArray { BsonNull.Value, Item(1) }; break;
                case "nullBetween": document["Items"] = new BsonArray { Item(0), BsonNull.Value, Item(2), BsonNull.Value }; break;
                case "scalar": document["Items"] = 7; break;
                case "oneObject": document["Items"] = Item(0); break;
                case "empty": document["Items"] = new BsonArray(); break;
                case "null": document["Items"] = BsonNull.Value; break;
                case "missing": document.Remove("Items"); break;
            }

            shipments.Add(document);
        }

        await database.GetCollection<BsonDocument>(LabService.Transport.Model.Entities[Corpus.Shipment].Collection).InsertManyAsync(shipments);

        return database;
    }

    /// <summary>The rows the database's own unwind answers over the seed: one per element, one for a value that is no array.</summary>
    private static int UnwoundRows(bool preserve) => Enumerable.Range(1, Shipments).Sum(n => Kinds[n % Kinds.Length] switch
    {
        "two" or "elementThenNull" or "nullThenElement" => 2,
        "three" => 3,
        "nullBetween" => 4,
        "onlyNull" or "scalar" or "oneObject" => 1,
        _ => preserve ? 1 : 0,
    });

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task The_rows_the_count_and_the_walk_are_those_of_the_request_as_written(string id, string stages)
    {
        var direct = new EngineDirect(LabService.Transport.Model);
        var database = await Irregular.Value;

        var written = await ShapeDifferentialTests.WalkAsync(direct, database, Corpus.Shipment, stages, limit: 20_000, asWritten: true);
        var placed = await ShapeDifferentialTests.WalkAsync(direct, database, Corpus.Shipment, stages, limit: 20_000, asWritten: false);

        written.Rows.Should().NotBeEmpty(id);
        placed.Rows.Should().Equal(written.Rows, $"{id}: the same rows in the same order as the stages in the order written");

        // Without a condition after the unwind the rows are the database's own count of them.
        if (!stages.Contains("\"match\"", StringComparison.Ordinal))
            written.Rows.Should().HaveCount(UnwoundRows(preserve: stages.Contains("preserveNull", StringComparison.Ordinal)), id);

        // The count the engine sends beside a page counts the rows the page walks, placed and as written.
        foreach (var asWritten in new[] { false, true })
            (await CountAsync(direct, database, stages, asWritten)).Should().Be(written.Rows.Count, $"{id}: the count {(asWritten ? "as written" : "as placed")}");

        // A cursor continues where its page ended, at every row and across every parent's last row.
        foreach (var limit in new[] { 1, 2, 5 })
        {
            var walked = await ShapeDifferentialTests.WalkAsync(direct, database, Corpus.Shipment, stages, limit, asWritten: false);

            walked.Rows.Should().Equal(written.Rows, $"{id} in pages of {limit}: no row twice, none left out, the order kept");
            walked.Pages.Should().Be(Math.Max(1, (written.Rows.Count + limit - 1) / limit), $"{id}: a page is full until the rows end");
        }
    }

    /// <summary>The count of the request's rows as the engine's count stages answer it.</summary>
    private static async Task<long> CountAsync(EngineDirect direct, IMongoDatabase database, string stages, bool asWritten)
    {
        var options = direct.Options(configure => configure.Limits.MaxPageSize = 50_000);
        var pipeline = stages.TrimEnd().TrimEnd(']') + """, { "page": { "limit": 1, "count": { "upTo": 100000 } } } ]""";
        var bound = await direct.BindAsync(Corpus.Shipment, pipeline, direct.Context(options));
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(30_000, true, 100_000) { KeepDiscriminators = true, AsWritten = asWritten });
        var counted = await EngineDirect.RunAsync(database, bound.Entity.Collection, compiled, count: true);

        return counted.Count == 0 ? 0 : counted[0]["n"].ToInt64();
    }
}
