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
/// An unwind over a collection that is not stored the way its type says: an array that holds a null,
/// an undefined or a scalar beside its elements, or nothing but such, an empty array, a null, a member
/// that is not stored, an undefined, a single object where the array should be (a member that was once one object,
/// with members or without), and a scalar. The database's <c>$unwind</c> answers a row for every
/// element of an array, whatever it is, one row for a value that is no array, and none for an empty
/// array, a null or a missing member. Whatever the engine does before the unwind to read fewer rows
/// (the parents cut first, the parents ordered by the position their rows start at, the element
/// conditions asked of the collection: of an object in the array, of an element without members, of
/// a value that is no array) answers exactly those rows: the same rows in the same order as the
/// request compiled in the order written, the same count, and the same rows in a walk by cursor of
/// any page size.
/// <para>
/// Not held here, and not exact where a condition on the element is asked: an element that is itself
/// an array. After the unwind a condition reads through it (it holds when an object in that inner
/// array meets it); before the unwind no plain condition does. Without a condition on the element
/// such an array's rows are exact as well (<see cref="An_element_that_is_itself_an_array_is_a_row_of_its_own"/>).
/// </para>
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

        // ---- one comparison of one member: asked through the collection's path ----
        { "match-element-in", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "in": [1, 3] } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-exists", $$"""[ {{Items}}, { "match": { "item.text": { "exists": true } } }, {{Columns}}, { "sort": [ { "referenceNumber": "desc" } ] } ]""" },
        { "match-element-exact", $$"""[ {{Items}}, { "match": { "item.text": { "eq": "Ware a", "options": { "caseSensitive": true } } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-contains", $$"""[ {{Items}}, { "match": { "item.text": { "contains": "re b" } } }, {{Columns}} ]""" },

        // ---- several conditions no absent element passes: some object passes them all, or the one object does ----
        { "match-element-two", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 1 }, "item.text": { "startsWith": "Ware" } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-range", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 1, "lte": 2 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },
        { "match-element-two-stages", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 1 } } }, { "match": { "item.orderNumber": { "lte": 2 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" }, { "id": "desc" } ] } ]""" },
        { "match-element-negative-and-positive", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "neq": 1 }, "item.text": { "startsWith": "Ware" } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-or-positive", $$"""[ {{Items}}, { "match": { "or": [ { "item.orderNumber": { "eq": 1 } }, { "item.text": { "eq": "Ware a" } } ] } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },

        // ---- conditions an absent element passes: an object that passes, an element without members, or the one value ----
        { "match-element-negative-text", $$"""[ {{Items}}, { "match": { "item.text": { "neq": "Ware a" } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-negative-exact", $$"""[ {{Items}}, { "match": { "item.text": { "neq": "Ware a", "options": { "caseSensitive": true } } } }, {{Columns}}, { "sort": [ { "referenceNumber": "asc" } ] } ]""" },
        { "match-element-negative-key-desc", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "neq": 2 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" }, { "id": "desc" } ] } ]""" },
        { "match-element-negative-default-order", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "nin": [0, 3] } } }, {{Columns}} ]""" },
        { "match-element-negative-and-position", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "neq": 1 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""" },
        { "match-element-two-negative", $$"""[ {{Items}}, { "match": { "item.orderNumber": { "neq": 1 }, "item.text": { "neq": "Ware a" } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-or-negative", $$"""[ {{Items}}, { "match": { "or": [ { "item.orderNumber": { "eq": 1 } }, { "item.text": { "neq": "Ware a" } } ] } }, {{Columns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },
        { "match-element-not-and", $$"""[ {{Items}}, { "match": { "not": { "and": [ { "item.orderNumber": { "gte": 2 } }, { "item.text": { "startsWith": "Ware" } } ] } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-not-not", $$"""[ {{Items}}, { "match": { "not": { "not": { "item.orderNumber": { "gte": 2 } } } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-parent-and-element-negative", $$"""[ {{Items}}, { "match": { "referenceNumber": { "neq": "ref-1" }, "item.orderNumber": { "neq": 2 } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },

        // ---- conditions that do not say what an absent element passes: the database is asked ----
        { "match-element-in-null", $$"""[ {{Items}}, { "match": { "item.text": { "in": [null, "Ware a"] } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-nin-null", $$"""[ {{Items}}, { "match": { "item.text": { "nin": [null, "Ware a"] } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-not-null", $$"""[ {{Items}}, { "match": { "item.text": { "neq": null } } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "match-element-null-or-positive", $$"""[ {{Items}}, { "match": { "or": [ { "item.text": { "in": [null, "Ware b"] } }, { "item.orderNumber": { "eq": 3 } } ] } }, {{Columns}}, { "sort": [ { "referenceNumber": "desc" } ] } ]""" },

        // ---- a row kept for a collection that holds nothing ----
        { "preserve-null", $$"""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }, {{Columns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
    };

    /// <summary>How each kind of collection is stored, by the remainder of the shipment's number.</summary>
    private static readonly string[] Kinds =
    [
        "two", "elementThenNull", "onlyNull", "nullThenElement", "scalar", "oneObject", "empty", "null", "missing", "three", "nullBetween",
        "elementThenScalar", "onlyScalars", "objectWithoutMembers", "undefinedThenElement", "text", "elementWithoutMembers", "undefined", "twoNulls",
    ];

    private const int Shipments = 209;

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
                case "elementThenScalar": document["Items"] = new BsonArray { Item(0), 7 }; break;
                case "onlyScalars": document["Items"] = new BsonArray { "Ware a", 3 }; break;
                case "objectWithoutMembers": document["Items"] = new BsonDocument(); break;
                case "undefinedThenElement": document["Items"] = new BsonArray { BsonUndefined.Value, Item(1) }; break;
                case "text": document["Items"] = "Ware a"; break;
                case "undefined": document["Items"] = BsonUndefined.Value; break;
                case "twoNulls": document["Items"] = new BsonArray { BsonNull.Value, BsonNull.Value }; break;
                case "elementWithoutMembers": document["Items"] = new BsonArray { new BsonDocument(), Item(1) }; break;
            }

            shipments.Add(document);
        }

        await database.GetCollection<BsonDocument>(LabService.Transport.Model.Entities[Corpus.Shipment].Collection).InsertManyAsync(shipments);

        return database;
    }

    /// <summary>The rows the database's own unwind answers over the seed: one per element, one for a value that is no array.</summary>
    private static int UnwoundRows(bool preserve) => Enumerable.Range(1, Shipments).Sum(n => Kinds[n % Kinds.Length] switch
    {
        "two" or "elementThenNull" or "nullThenElement" or "elementThenScalar" or "onlyScalars" or "undefinedThenElement" or "elementWithoutMembers" or "twoNulls" => 2,
        "three" => 3,
        "nullBetween" => 4,
        "onlyNull" or "scalar" or "oneObject" or "objectWithoutMembers" or "text" => 1,
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

    /// <summary>
    /// Where the parents are cut before the unwind, and where the element conditions are asked of the
    /// collection: every request whose order is the parents' is cut first, a condition an absent
    /// element passes included, and none of it is an expression.
    /// </summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public async Task The_parents_are_cut_first_wherever_the_order_is_theirs_and_nothing_asked_before_the_unwind_is_an_expression(string id, string stages)
    {
        var direct = new EngineDirect(LabService.Transport.Model);
        var options = direct.Options(configure => configure.Limits.MaxPageSize = 50_000);
        var bound = await direct.BindAsync(Corpus.Shipment, stages.TrimEnd().TrimEnd(']') + """, { "page": { "limit": 5, "count": {} } } ]""", direct.Context(options));
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(30_000, true, 100_000) { KeepDiscriminators = true });
        var conditioned = id.StartsWith("match-element", StringComparison.Ordinal) || id.Contains("and-element", StringComparison.Ordinal);
        var byPosition = id.Contains("position", StringComparison.Ordinal);

        // By position with a condition on the element the rows of a parent no longer stand without a gap.
        compiled.Unwind!.ParentsFirst.Should().Be(!(conditioned && byPosition), id);
        (compiled.Unwind.Prefiltered.Count > 0).Should().Be(conditioned, $"{id}: what is asked of the element is asked of the collection");

        var unwindAt = compiled.PageStages.TakeWhile(stage => !stage.Contains("$unwind")).Count();
        var before = string.Join(" ", compiled.PageStages.Take(unwindAt).Select(stage => stage.ToJson()));

        before.Should().NotContain("$expr", $"{id}: a plain condition, which an index can bound and the database evaluates on the stored row");

        if (compiled.Unwind.ParentsFirst)
            compiled.PageStages.Take(unwindAt).Should().Contain(stage => stage.Contains("$limit"), $"{id}: the parents are ordered and cut before the unwind")
                .And.Contain(stage => stage.Contains("$sort"), id);

        if (conditioned)
            string.Join(" ", compiled.CountStages!.TakeWhile(stage => !stage.Contains("$unwind")).Select(stage => stage.ToJson()))
                .Should().Contain("Items", $"{id}: the count unwinds the rows that hold a passing element alone").And.NotContain("$expr");
    }

    /// <summary>
    /// An element that is itself an array is a row of its own, and so is an array that holds nothing
    /// but arrays: without a condition on the element the cut of the parents keeps it.
    /// </summary>
    [Theory]
    [InlineData("""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "project": { "shipmentNumber": 1, "position": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""")]
    [InlineData("""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "project": { "shipmentNumber": 1, "position": 1 } }, { "sort": [ { "shipmentNumber": "desc" }, { "position": "desc" } ] } ]""")]
    [InlineData("""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "match": { "shipmentNumber": { "neq": "N-1" } } }, { "project": { "shipmentNumber": 1, "position": 1 } } ]""")]
    public async Task An_element_that_is_itself_an_array_is_a_row_of_its_own(string stages)
    {
        var direct = new EngineDirect(LabService.Transport.Model);
        var database = (await MongoFixture.CreateDatabaseAsync("unwind_nested")).Database;
        var item = new ShipmentItem { Id = Ids.Of(Spaces.ShipmentItem, Org.A, 90_001), Text = "Ware a", OrderNumber = 1 }.ToBsonDocument();
        BsonValue[] collections = [new BsonArray { new BsonArray { item } }, new BsonArray { new BsonArray() }, new BsonArray { item, new BsonArray { item, item } }, new BsonArray(), new BsonArray { item }];

        await database.GetCollection<BsonDocument>(LabService.Transport.Model.Entities[Corpus.Shipment].Collection).InsertManyAsync(collections.Select((collection, n) =>
        {
            var document = new StoredShipment { Id = Ids.Of(Spaces.Shipment, Org.A, 9_000 + n), OrganizationId = EngineDirect.Organisation, ShipmentNumber = $"N-{n % 3}" }.ToBsonDocument();

            document["Items"] = collection;

            return document;
        }));

        var written = await ShapeDifferentialTests.WalkAsync(direct, database, Corpus.Shipment, stages, limit: 1_000, asWritten: true);

        written.Rows.Count.Should().BeGreaterThan(2);

        foreach (var limit in new[] { 1_000, 1, 2 })
            (await ShapeDifferentialTests.WalkAsync(direct, database, Corpus.Shipment, stages, limit, asWritten: false)).Rows.Should().Equal(written.Rows, $"in pages of {limit}");

        (await CountAsync(direct, database, stages, asWritten: false)).Should().Be(written.Rows.Count);
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
