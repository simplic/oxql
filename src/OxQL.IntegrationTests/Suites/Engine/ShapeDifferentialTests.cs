using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Transport;
using StoredShipment = OxQL.IntegrationTests.Fleet.Models.Transport.Shipment;
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

        // ---- unwound rows: the parents first, the element where the unwind left it, the prefilter ----
        { "unwind-default-order", Corpus.Shipment, $$"""[ {{Items}} ]""" },
        { "unwind-default-order-projected", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}} ]""" },
        { "unwind-sort-parent", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-sort-parent-desc", Corpus.Shipment, $$"""[ {{Items}}, { "sort": [ { "loadStart": "desc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-sort-two-parents", Corpus.Shipment, $$"""[ {{Items}}, { "sort": [ { "status.name": "desc" }, { "referenceNumber": "asc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-sort-written-before", Corpus.Shipment, $$"""[ { "sort": [ { "shipmentNumber": "desc" } ] }, {{Items}}, {{ItemColumns}} ]""" },
        { "unwind-sort-element", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "item.text": "asc" } ] } ]""" },
        { "unwind-sort-element-desc", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "item.orderNumber": "desc" } ] } ]""" },
        { "unwind-sort-parent-then-element", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "status.name": "asc" }, { "item.orderNumber": "desc" } ] } ]""" },
        { "unwind-sort-key-then-position-desc", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "id": "desc" }, { "position": "desc" } ] } ]""" },
        { "unwind-sort-position", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "position": "desc" } ] } ]""" },
        { "unwind-without-index", Corpus.Shipment, """[ { "unwind": { "path": "items", "as": "item" } }, { "project": { "shipmentNumber": 1, "item.text": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-match-element", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 2 } } }, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-match-element-negative", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "item.orderNumber": { "neq": 1 }, "item.text": { "neq": null } } }, {{ItemColumns}} ]""" },
        { "unwind-match-element-or", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "or": [ { "item.orderNumber": { "eq": 1 } }, { "item.shippingUnit": { "neq": null } } ] } }, {{ItemColumns}}, { "sort": [ { "loadStart": "asc" } ] } ]""" },
        { "unwind-match-parent-and-element", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "status.name": { "neq": "Closed" }, "item.orderNumber": { "lte": 2 } } }, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },
        { "unwind-match-two-stages", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 1 } } }, { "match": { "item.orderNumber": { "lte": 2 } } }, {{ItemColumns}} ]""" },
        { "unwind-match-mixed-or", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "or": [ { "item.orderNumber": { "eq": 1 } }, { "status.name": { "eq": "Open" } } ] } }, {{ItemColumns}} ]""" },
        { "unwind-match-index", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "position": { "gte": 1 } } }, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-match-alias-exists", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "item": { "exists": true } } }, {{ItemColumns}} ]""" },
        { "unwind-match-element-any", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "item.weightNotes": { "any": { "number": { "neq": null } } } } }, {{ItemColumns}} ]""" },
        { "unwind-filter-entry", Corpus.Shipment, $$"""[ { "match": { "isDeleted": { "neq": true } } }, {{Items}}, { "match": { "item.text": { "contains": "a" } } }, { "sort": [ { "referenceNumber": "asc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-preserve-null", Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }, {{ItemColumns}} ]""" },
        { "unwind-preserve-null-sorted", Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },
        { "unwind-preserve-null-match", Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "item", "preserveNull": true } }, { "match": { "item.orderNumber": { "neq": 1 } } }, { "project": { "shipmentNumber": 1, "item.orderNumber": 1 } } ]""" },
        { "unwind-preserve-null-sort-element", Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "item", "preserveNull": true } }, { "project": { "shipmentNumber": 1, "item.text": 1 } }, { "sort": [ { "item.text": "desc" } ] } ]""" },
        { "unwind-scalars", Corpus.Shipment, """[ { "unwind": { "path": "status.roles", "as": "role" } }, { "project": { "shipmentNumber": 1, "role": 1 } }, { "sort": [ { "role": "asc" } ] } ]""" },
        { "unwind-two", Corpus.Shipment, """[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "unwind": { "path": "item.weightNotes", "as": "note", "includeIndex": "at" } }, { "project": { "shipmentNumber": 1, "position": 1, "at": 1, "note.number": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-flatten", Corpus.Transaction, """[ { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } }, { "project": { "number": 1, "position": 1, "item.text": 1 } }, { "sort": [ { "number": "asc" } ] } ]""" },
        { "unwind-flatten-match", Corpus.Transaction, """[ { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } }, { "match": { "item.text": { "neq": null } } }, { "project": { "number": 1, "position": 1, "item.text": 1 } } ]""" },
        { "unwind-lookup-alias", Corpus.Shipment, """[ { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts" } }, { "unwind": { "path": "attempts", "as": "attempt", "includeIndex": "n" } }, { "match": { "attempt.text": { "neq": null } } }, { "project": { "shipmentNumber": 1, "n": 1, "attempt.text": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-then-late-join", Corpus.Shipment, $$"""[ {{Items}}, { "resolve": { "path": "department.id", "as": "dep", "onMissing": "null" } }, { "project": { "shipmentNumber": 1, "position": 1, "item.text": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        // ---- the position after the parents' members and before the key: parents that tie interleave by it ----
        { "unwind-sort-parent-then-position", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""" },
        { "unwind-sort-parent-then-position-desc", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "status.name": "asc" }, { "position": "desc" } ] } ]""" },
        { "unwind-sort-parent-desc-then-position", Corpus.Shipment, $$"""[ {{Items}}, { "sort": [ { "loadStart": "desc" }, { "position": "asc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-sort-two-parents-then-position", Corpus.Shipment, $$"""[ {{Items}}, { "sort": [ { "status.name": "desc" }, { "referenceNumber": "asc" }, { "position": "asc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-sort-parent-position-key-desc", Corpus.Shipment, $$"""[ {{Items}}, { "sort": [ { "referenceNumber": "desc" }, { "position": "asc" }, { "id": "desc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-sort-parent-position-desc-key-desc", Corpus.Shipment, $$"""[ {{Items}}, { "sort": [ { "referenceNumber": "asc" }, { "position": "desc" }, { "id": "desc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-sort-position-asc", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "position": "asc" } ] } ]""" },
        { "unwind-sort-position-match-parent", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "status.name": { "neq": "Planned" } } }, { "sort": [ { "referenceNumber": "asc" }, { "position": "asc" } ] }, {{ItemColumns}} ]""" },
        { "unwind-sort-position-match-element", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 1 } } }, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""" },
        { "unwind-sort-position-preserve-null", Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""" },
        { "unwind-sort-position-then-element", Corpus.Shipment, $$"""[ {{Items}}, {{ItemColumns}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" }, { "item.text": "desc" } ] } ]""" },
        { "unwind-scalars-sort-parent-then-position", Corpus.Shipment, """[ { "unwind": { "path": "status.roles", "as": "role", "includeIndex": "at" } }, { "sort": [ { "referenceNumber": "asc" }, { "at": "desc" } ] }, { "project": { "shipmentNumber": 1, "role": 1, "at": 1 } } ]""" },
        { "unwind-lookup-before-sort-parent-then-position", Corpus.Shipment, $$"""[ {{Latest}}, {{Items}}, {{ItemAndLatest}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""" },

        // ---- a join written before the unwind: on the parents cut first, or where it was written; never once per element ----
        { "unwind-lookup-before-sort-parent", Corpus.Shipment, $$"""[ {{Latest}}, {{Items}}, {{ItemAndLatest}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-lookup-before-default-order", Corpus.Shipment, $$"""[ {{Latest}}, {{Items}}, {{ItemAndLatest}} ]""" },
        { "unwind-lookup-before-sort-element", Corpus.Shipment, $$"""[ {{Latest}}, {{Items}}, {{ItemAndLatest}}, { "sort": [ { "item.text": "asc" } ] } ]""" },
        { "unwind-lookup-before-sort-position", Corpus.Shipment, $$"""[ { "match": { "referenceNumber": { "eq": "ref-3" } } }, {{Latest}}, {{Items}}, {{ItemAndLatest}}, { "sort": [ { "position": "asc" } ] } ]""" },
        { "unwind-lookup-before-match-element", Corpus.Shipment, $$"""[ {{Latest}}, {{Items}}, { "match": { "item.orderNumber": { "gte": 1 } } }, { "sort": [ { "loadStart": "desc" } ] }, {{ItemAndLatest}} ]""" },
        { "unwind-lookup-many-before-whole-alias", Corpus.Shipment, $$"""[ { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts" } }, {{Items}}, { "project": { "shipmentNumber": 1, "position": 1, "item.text": 1, "attempts": 1 } }, { "sort": [ { "shipmentNumber": "desc" } ] } ]""" },
        { "unwind-two-joins-before", Corpus.Shipment, $$"""[ {{Latest}}, { "resolve": { "path": "department.id", "as": "dep", "onMissing": "null" } }, {{Items}}, { "sort": [ { "referenceNumber": "asc" } ] }, { "project": { "shipmentNumber": 1, "position": 1, "item.text": 1, "latest.text": 1, "dep.name": 1 } } ]""" },
        { "unwind-lookup-after-sort-parent", Corpus.Shipment, $$"""[ {{Items}}, {{Latest}}, {{ItemAndLatest}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-lookup-before-unprojected", Corpus.Shipment, $$"""[ {{Latest}}, {{Items}}, { "sort": [ { "shipmentNumber": "asc" } ] } ]""" },
        { "unwind-then-group", Corpus.Shipment, $$"""[ {{Items}}, { "match": { "item.orderNumber": { "gte": 1 } } }, { "group": { "by": [ { "path": "item.orderNumber", "as": "order" } ], "fields": { "n": { "count": true } } } } ]""" },

        // ---- groups: the keys end the order, the cursor continues after the last group ----
        { "group-one-key", Corpus.Shipment, """[ { "group": { "by": [ { "path": "shipmentNumber", "as": "number" } ], "fields": { "n": { "count": true } } } } ]""" },
        { "group-two-keys", Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "status" }, { "path": "loadingTimeType", "as": "type" } ], "fields": { "n": { "count": true }, "first": { "min": "loadStart" } } } } ]""" },
        { "group-sorted-by-aggregate", Corpus.Shipment, """[ { "group": { "by": [ { "path": "referenceNumber", "as": "reference" } ], "fields": { "n": { "count": true } } } }, { "sort": [ { "n": "desc" } ] } ]""" },
        { "group-sorted-by-key-desc", Corpus.Shipment, """[ { "group": { "by": [ { "path": "shipmentNumber", "as": "number" } ], "fields": { "last": { "max": "loadStart" } } } }, { "sort": [ { "number": "desc" } ] } ]""" },
        { "group-date-bucket", Corpus.Shipment, """[ { "group": { "by": [ { "dateTrunc": { "path": "loadStart", "unit": "day", "timezone": "Europe/Berlin" }, "as": "day" } ], "fields": { "n": { "count": true } } } } ]""" },
        { "group-volume-nulls", Corpus.Template, """[ { "group": { "by": [ { "path": "templateName", "as": "name" } ], "fields": { "n": { "count": true } } } } ]""" },
        { "group-volume-by-count", Corpus.Template, """[ { "group": { "by": [ { "path": "templateName", "as": "name" } ], "fields": { "n": { "count": true } } } }, { "sort": [ { "n": "desc" }, { "name": "desc" } ] } ]""" },
        { "group-projected", Corpus.Shipment, """[ { "group": { "by": [ { "path": "shipmentNumber", "as": "number" } ], "fields": { "n": { "count": true } } } }, { "sort": [ { "n": "asc" } ] }, { "project": { "n": 1 } } ]""" },
    };

    private const string Items = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""";

    /// <summary>The latest delivery attempt of a shipment: a join only the display reads, of a member of the parent.</summary>
    private const string Latest = """{ "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "latest", "sort": [ { "dateTime": "desc" } ], "first": true } }""";

    private const string ItemAndLatest = """{ "project": { "shipmentNumber": 1, "position": 1, "item.text": 1, "latest.text": 1, "latest.dateTime": 1 } }""";

    private const string ItemColumns = """{ "project": { "shipmentNumber": 1, "loadStart": 1, "status.name": 1, "position": 1, "item.text": 1, "item.orderNumber": 1 } }""";

    /// <summary>The request as the engine places its stages and in the order written answers the same rows in the same order.</summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public async Task A_placed_stage_answers_the_rows_of_the_request_as_written(string id, string entity, string stages)
    {
        var (direct, database) = await HostAsync(entity);
        var limit = entity == Corpus.Template ? 499 : 7;
        var placed = await WalkAsync(direct, database, entity, stages, limit, asWritten: false);
        var written = await WalkAsync(direct, database, entity, stages, limit, asWritten: true);

        placed.Rows.Should().NotBeEmpty($"{id} reads rows");
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

        whole.Pages.Should().Be(1, id);

        // A page of one row ends at every row once: every position a cursor can stand at is continued from.
        foreach (var limit in entity == Corpus.Template ? new[] { 997 } : RowByRow.Contains(id) ? [1, 7] : [7])
        {
            var walked = await WalkAsync(direct, database, entity, stages, limit, asWritten: false);

            walked.Rows.Should().Equal(whole.Rows, $"{id} in pages of {limit}: no row twice, none left out, the order kept");
            walked.Pages.Should().Be(Math.Max(1, (whole.Rows.Count + limit - 1) / limit), $"{id}: a page is full until the rows end");
        }
    }

    /// <summary>The requests also walked one row a page.</summary>
    private static readonly HashSet<string> RowByRow =
    [
        "unwind-default-order", "unwind-sort-parent-desc", "unwind-sort-two-parents", "unwind-sort-element", "unwind-sort-key-then-position-desc",
        "unwind-match-element", "unwind-match-parent-and-element", "unwind-preserve-null-sorted", "unwind-two", "unwind-lookup-alias",
        "unwind-lookup-before-sort-parent", "unwind-lookup-before-sort-position", "unwind-lookup-before-match-element",
        "unwind-sort-parent-then-position", "unwind-sort-parent-then-position-desc", "unwind-sort-parent-desc-then-position", "unwind-sort-two-parents-then-position",
        "unwind-sort-parent-position-key-desc", "unwind-sort-parent-position-desc-key-desc", "unwind-sort-position-asc", "unwind-sort-position", "unwind-sort-position-match-parent",
        "unwind-scalars-sort-parent-then-position", "unwind-lookup-before-sort-parent-then-position",
        "group-one-key", "group-sorted-by-aggregate",
    ];

    /// <summary>
    /// The shipments the unwound and grouped requests read: a database of their own with rows made
    /// for the edges of an order (ties and nulls on every sort field, collations' ties), of an
    /// unwind (no items, a null collection, a missing one, items without a text, nested notes) and
    /// of a join (shipments with and without delivery attempts).
    /// </summary>
    private static readonly Lazy<Task<IMongoDatabase>> Edges = new(SeedEdgesAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private const int EdgeShipments = 230;

    private static async Task<IMongoDatabase> SeedEdgesAsync()
    {
        var database = (await MongoFixture.CreateDatabaseAsync("shape_differential")).Database;
        var organisation = EngineDirect.Organisation;
        var at = new DateTime(2026, 3, 1, 6, 0, 0, DateTimeKind.Utc);
        string[] statuses = ["Open", "open", "Planned", "Geschlossen", "geschloßen"];
        var shipments = new List<BsonDocument>();
        var attempts = new List<BsonDocument>();

        for (var n = 1; n <= EdgeShipments; n++)
        {
            var scrambled = n * 37 % EdgeShipments;
            var document = new StoredShipment
            {
                Id = Ids.Of(Spaces.Shipment, Org.A, 5_000 + n),
                OrganizationId = organisation,
                IsDeleted = n % 19 == 0,
                ShipmentNumber = n % 11 == 0 ? null : $"S-{scrambled % 60:D3}",
                ReferenceNumber = n % 7 == 0 ? null : $"ref-{scrambled % 9}",
                LoadStart = n % 13 == 0 ? null : at.AddHours(scrambled % 40),
                Status = n % 17 == 0 ? null : new ShipmentStatus { Name = statuses[scrambled % statuses.Length], Roles = [.. new[] { "plan", "load", "bill" }.Take(scrambled % 4)] },
                Items = [.. Enumerable.Range(0, n % 5).Select(position => new ShipmentItem
                {
                    Id = Ids.Of(Spaces.ShipmentItem, Org.A, 50_000 + n * 10 + position),
                    Text = (n + position) % 6 == 0 ? null : $"Ware {(char)('a' + (n * 7 + position * 3) % 9)}",
                    OrderNumber = (n + position) % 4,
                    ShippingUnit = (n + position) % 3 == 0 ? null : "Palette",
                    WeightNotes = [.. Enumerable.Range(0, (n + position) % 3).Select(note => new WeightNote { Id = Ids.Of(Spaces.WeightNote, Org.A, 500_000 + n * 100 + position * 10 + note), Number = note == 1 ? null : $"W-{n}-{position}-{note}" })],
                })],
            }.ToBsonDocument();

            // A collection stored as null, and one that is not stored at all.
            if (n % 23 == 0)
                document["Items"] = BsonNull.Value;
            else if (n % 29 == 0)
                document.Remove("Items");

            shipments.Add(document);

            for (var attempt = 0; attempt < (n % 4 == 0 ? 1 + n % 3 : 0); attempt++)
                attempts.Add(new DeliveryAttempt
                {
                    Id = Ids.Of(Spaces.DeliveryAttempt, Org.A, 50_000 + n * 10 + attempt),
                    OrganizationId = organisation,
                    ShipmentId = Ids.Of(Spaces.Shipment, Org.A, 5_000 + n),
                    Text = attempt == 1 ? null : $"Versuch {attempt}",
                    DateTime = at.AddDays(attempt),
                }.ToBsonDocument());
        }

        var model = LabService.Transport.Model;

        await database.GetCollection<BsonDocument>(model.Entities[Corpus.Shipment].Collection).InsertManyAsync(shipments);
        await database.GetCollection<BsonDocument>(model.Entities["transport.delivery_attempt"].Collection).InsertManyAsync(attempts);

        return database;
    }

    internal static async Task<(EngineDirect Direct, IMongoDatabase Database)> HostAsync(string entity)
    {
        var service = Corpus.Entity(entity).Service;

        return (new EngineDirect(service.Model), entity == Corpus.Shipment ? await Edges.Value : await (await CorpusFleet.SharedAsync()).Fleet.DatabaseAsync(service));
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
