using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>The emitted pipeline per shape, pinned as BSON.</summary>
public class GoldenPipelineTests
{
    private const string Order = "probe.order";
    private static readonly BsonBinaryData Org = new(BindHost.Organisation, GuidRepresentation.Standard);
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static async Task<CompiledQuery> Compile(string pipeline, string entity = Order, RequestContext? context = null, string? variables = null)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, entity, pipeline, context, variables);

        return MongoCompiler.Compile(bound, Options);
    }

    private static BsonDocument Scope() => new("$match", new BsonDocument("OrganizationId", Org));

    private static void ShouldBe(IReadOnlyList<BsonDocument> actual, params string[] expected)
    {
        var expectedDocuments = expected.Select(BsonDocument.Parse).ToList();

        actual.Select(stage => stage.ToJson()).Should().Equal(expectedDocuments.Select(stage => stage.ToJson()));
    }

    private static string OrgJson => $"{{ OrganizationId: {Org.ToJson()} }}";

    [Fact]
    public async Task The_scope_leads_every_pipeline_and_the_default_page_sorts_by_the_key()
    {
        var compiled = await Compile("[]");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            "{ $sort: { _id: 1 } }",
            "{ $limit: 101 }");
        compiled.CountStages.Should().BeNull();
    }

    [Fact]
    public async Task A_match_after_a_projection_still_sees_the_scope_first()
    {
        var compiled = await Compile("""[{ "project": { "number": 1 } }, { "match": { "number": { "eq": "x" } } }, { "page": { "limit": 20, "includeTotalCount": true } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            "{ $project: { Number: 1 } }",
            "{ $match: { Number: 'x' } }",
            "{ $sort: { _id: 1 } }",
            "{ $limit: 21 }");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            "{ $project: { Number: 1 } }",
            "{ $match: { Number: 'x' } }",
            "{ $limit: 100001 }",
            "{ $count: 'n' }");
    }

    [Fact]
    public async Task Typed_operands_reach_the_filter_as_they_are_stored()
    {
        var compiled = await Compile("""[{ "match": { "id": { "eq": "195fb742-82b3-405e-b77b-42838eb0aaa9" }, "state": { "nin": ["Open"] }, "flag": { "eq": "false" }, "when": { "gte": "2026-09-05T00:00:00Z" }, "qrCode": { "eq": "q" }, "big": { "in": ["1", 2] } } }]""");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $and: [
                { _id: BinData(4, "GV+3QoKzQF63e0KDjrCqqQ==") },
                { State: { $nin: [0] } },
                { Flag: false },
                { When: { $gte: ISODate("2026-09-05T00:00:00Z") } },
                { QRCode: "q" },
                { Big: { $in: [NumberLong(1), NumberLong(2)] } } ] } }
            """));
    }

    [Fact]
    public async Task A_decimal_is_matched_in_both_storage_forms()
    {
        // F2: the text bracket is an anchored regex over every scale the driver could have
        // written the value with, not one canonical spelling. `"12.50"` used to bind the
        // string alternative `"12.5"` — G29 drops the trailing zero — so the value the
        // service itself returns for a string-stored row did not find that row.
        var compiled = await Compile("""[{ "match": { "amount": { "eq": "12.50" } } }]""");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $or: [
                { Amount: NumberDecimal("12.50") },
                { Amount: { $regularExpression: { pattern: "^12\\.50*$", options: "" } } } ] } }
            """));

        // F3: an ordered comparison gets no text bracket. Mongo compares text by characters,
        // so `$gt: "10"` admitted "9.5" and excluded "100.00"; numbers only is what $sum and
        // $min already answer over the same member, and the rows it does not reach are named
        // in a diagnostic instead of being silently mis-ordered.
        var range = await Compile("""[{ "match": { "amount": { "gt": 10 } } }]""");

        range.PageStages[1].ShouldBeBson(BsonDocument.Parse("""{ $match: { Amount: { $gt: NumberDecimal("10") } } }"""));
        range.Bound.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.DecimalTextExcluded && diagnostic.Path == "amount");

        // A regex alternative is a pattern, and $ne against a pattern compares the pattern
        // itself rather than matching with it, so the negation is $nor of the positives.
        var negated = await Compile("""[{ "match": { "amount": { "neq": 10 } } }]""");

        negated.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $nor: [
                { Amount: NumberDecimal("10") },
                { Amount: { $regularExpression: { pattern: "^10(?:\\.0+)?$", options: "" } } } ] } }
            """));
    }

    [Fact]
    public async Task A_string_comparison_folds_under_the_collation_and_only_a_pattern_keeps_the_flag()
    {
        var compiled = await Compile("""
            [{ "match": { "or": [
                { "number": { "eq": "a.b", "options": { "ignoreCase": true } } },
                { "number": { "neq": "a", "options": { "ignoreCase": true } } },
                { "number": { "in": ["a", "b"], "options": { "ignoreCase": true } } },
                { "number": { "contains": "a", "options": { "ignoreCase": true } } },
                { "number": { "startsWith": "a", "options": { "ignoreCase": true } } },
                { "number": { "endsWith": "a", "options": { "ignoreCase": true } } },
                { "number": { "startsWith": "a" } },
                { "number": { "regex": "^a" } } ] } }]
            """);

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $or: [
                { Number: 'a.b' },
                { Number: { $ne: 'a' } },
                { Number: { $in: [ 'a', 'b' ] } },
                { Number: /a/i },
                { Number: { $gte: 'a', $lt: 'a￿' } },
                { Number: /a$/i },
                { Number: { $gte: 'a', $lt: 'a￿' } },
                { Number: /^a/ } ] } }
            """));
        compiled.Collation.Should().NotBeNull();
    }

    [Fact]
    public async Task Null_forms_and_not_and_exists()
    {
        var compiled = await Compile("""[{ "match": { "note": { "eq": null }, "count": { "neq": null }, "not": { "flag": { "eq": true } }, "link": { "exists": false } } }]""");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $and: [ { Note: null }, { Count: { $ne: null } }, { $nor: [ { Flag: true } ] }, { Link: { $exists: false } } ] } }
            """));
    }

    [Fact]
    public async Task Any_compiles_to_elemMatch_with_relative_paths()
    {
        var compiled = await Compile("""[{ "match": { "items": { "any": { "quantity": { "gt": 1 }, "price.currency": { "eq": "EUR" } } } } }]""");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { Items: { $elemMatch: { $and: [ { Quantity: { $gt: NumberLong(1) } }, { "Price.Currency": "EUR" } ] } } } }
            """));
    }

    [Fact]
    public async Task Lookup_is_the_concise_form_with_the_scope_inside()
    {
        var compiled = await Compile("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number"], "filter": { "flag": { "eq": true } }, "limit": 5 } }, { "match": { "orders.number": { "eq": "x" } } }, { "page": { "includeTotalCount": true } }]""", "probe.customer");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""
            { $lookup: { from: "orders", localField: "_id", foreignField: "CustomerId",
                pipeline: [ { $match: {{OrgJson}} }, { $match: { Flag: true } }, { $sort: { _id: 1 } }, { $limit: 5 }, { $project: { _id: 1, Number: 1 } } ],
                as: "orders" } }
            """));
        compiled.PageStages[2].ShouldBeBson(BsonDocument.Parse("""{ $match: { "orders.Number": "x" } }"""));
        compiled.CountStages!.Should().HaveCount(5, "the lookup is read by the later match, so the count keeps it");

        var unread = await Compile("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "page": { "includeTotalCount": true } }]""", "probe.customer");

        unread.CountStages!.Should().HaveCount(3, "a lookup nothing reads is not in the count");
        unread.PageStages[1]["$lookup"]["pipeline"].AsBsonArray[3].ShouldBeBson(BsonDocument.Parse("{ $project: { _id: 1, Number: 1 } }"), "the default select is the child's key and display member");
    }

    [Fact]
    public async Task Local_resolve_is_an_indexed_lookup_plus_the_first_element()
    {
        var compiled = await Compile("""[{ "resolve": { "path": "customerId", "as": "cust", "select": ["name"], "filter": { "matchCode": { "eq": "m" } } } }, { "sort": [{ "cust.name": "asc" }] }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            $$"""{ $lookup: { from: "customers", localField: "CustomerId", foreignField: "_id", pipeline: [ { $match: {{OrgJson}} }, { $match: { MatchCode: "m" } }, { $limit: 1 }, { $project: { _id: 1, Name: 1 } } ], as: "cust__arr" } }""",
            """{ $set: { cust: { $arrayElemAt: [ "$cust__arr", 0 ] } } }""",
            """{ $unset: "cust__arr" }""",
            """{ $sort: { "cust.Name": 1, _id: 1 } }""",
            "{ $limit: 101 }");
    }

    [Fact]
    public async Task A_remote_resolve_emits_nothing_and_a_semi_join_leaves_a_slot()
    {
        var compiled = await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "match": { "contact.name": { "eq": "x" } } }]""");

        compiled.RemoteResolves.Should().ContainSingle();
        compiled.SemiJoins.Should().ContainSingle();
        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $match: { ContactNumber: { $in: [] } } }"));

        compiled.SemiJoins[0].Ids.Add("c1");
        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $match: { ContactNumber: { $in: ['c1'] } } }"), "the slot is the live array in the filter");
    }

    [Fact]
    public async Task Unwind_with_alias_and_index_then_offset_paging()
    {
        var compiled = await Compile("""[{ "unwind": { "path": "items", "as": "it", "includeIndex": "i", "preserveNull": true } }, { "sort": [{ "items.quantity": "desc" }] }, { "page": { "limit": 10, "offset": 20 } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: true, includeArrayIndex: "i" } }""",
            """{ $set: { it: "$Items" } }""",
            """{ $sort: { "Items.Quantity": -1, _id: 1, i: 1 } }""",
            "{ $skip: 20 }",
            "{ $limit: 11 }");
        compiled.PagingMode.Should().Be(PagingMode.Offset);
    }

    [Fact]
    public async Task Group_reshapes_to_the_aliases_and_counts_groups_after_the_group()
    {
        // G2: every leg of a composite _id is wrapped in $ifNull. A scalar $group _id
        // normalises a missing member to null; a subdocument _id omits the field instead, so
        // the same data bucketed null and missing together under one key and apart under two,
        // and the row lost the member altogether.
        var compiled = await Compile("""[{ "group": { "by": [{ "path": "state", "as": "st" }, { "dateTrunc": { "path": "when", "unit": "week", "timezone": "Europe/Berlin" }, "as": "wk" }], "fields": { "n": { "count": true }, "total": { "sum": "amount" }, "kinds": { "countDistinct": "number" }, "x": { "max": { "add": ["ratio", 1] } } } } }, { "sort": [{ "n": "desc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $group: { _id: { st: { $ifNull: [ "$State", null ] }, wk: { $ifNull: [ { $dateTrunc: { date: "$When", unit: "week", timezone: "Europe/Berlin", startOfWeek: "monday" } }, null ] } }, n: { $sum: 1 }, total: { $sum: "$Amount" }, kinds: { $addToSet: "$Number" }, x: { $max: { $add: [ "$Ratio", { $literal: NumberLong(1) } ] } } } }""",
            """{ $addFields: { kinds: { $size: "$kinds" } } }""",
            """{ $project: { st: "$_id.st", wk: "$_id.wk", n: 1, total: 1, kinds: 1, x: 1, _id: 0 } }""",
            "{ $sort: { n: -1 } }",
            "{ $limit: 6 }");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            """{ $group: { _id: { st: { $ifNull: [ "$State", null ] }, wk: { $ifNull: [ { $dateTrunc: { date: "$When", unit: "week", timezone: "Europe/Berlin", startOfWeek: "monday" } }, null ] } }, n: { $sum: 1 }, total: { $sum: "$Amount" }, kinds: { $addToSet: "$Number" }, x: { $max: { $add: [ "$Ratio", { $literal: NumberLong(1) } ] } } } }""",
            """{ $addFields: { kinds: { $size: "$kinds" } } }""",
            """{ $project: { st: "$_id.st", wk: "$_id.wk", n: 1, total: 1, kinds: 1, x: 1, _id: 0 } }""",
            "{ $limit: 100001 }",
            "{ $count: 'n' }");

        var single = await Compile("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "month" }, "as": "m" }], "fields": {} } }]""");

        single.PageStages[1].ShouldBeBson(BsonDocument.Parse("""{ $group: { _id: { $dateTrunc: { date: "$When", unit: "month", timezone: "UTC" } } } }"""));
        single.PageStages[2].ShouldBeBson(BsonDocument.Parse("""{ $project: { m: "$_id", _id: 0 } }"""));
    }

    [Fact]
    public async Task Projection_is_inclusion_with_the_key_by_default_or_exclusion()
    {
        (await Compile("""[{ "project": { "number": 1, "shipTo.city": 1, "qrCode": 1 } }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("""{ $project: { Number: 1, "ShipTo.City": 1, QRCode: 1 } }"""));
        (await Compile("""[{ "project": { "number": 1, "id": 0 } }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1 } }"), "the key the cursor and the tie-breaker read survives a projection that drops it");
        (await Compile("""[{ "project": { "addon": 0, "items": 0 } }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Addon: 0, Items: 0 } }"));
        (await Compile("""[{ "project": { "addon": 0, "id": 0 } }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Addon: 0 } }"), "an exclusion that names the key keeps it for the same reason");

        (await Compile("""[{ "project": { "number": 1, "id": 0 } }]""")).KeyKeptAgainstProjection
            .Should().BeTrue("a contract 1 row drops the key again in the executor");
    }

    [Fact]
    public async Task A_projection_keeps_the_reference_member_a_remote_resolve_reads()
    {
        (await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "number": 1 } }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1, ContactNumber: 1 } }"), "the resolver keys the page rows on the member after the aggregate");
        (await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "number": 1, "contactNumber": 1 } }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1, ContactNumber: 1 } }"), "a kept member is not added twice");
        (await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "contactNumber": 0, "addon": 0 } }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Addon: 0 } }"), "an exclusion of the member is not emitted");

        var nothingLeft = await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "contactNumber": 0 } }]""");

        nothingLeft.PageStages.Should().NotContain(stage => stage.Contains("$project"), "an empty $project is not a valid stage");
    }

    [Fact]
    public async Task Sort_adds_the_key_as_tie_breaker_on_a_root_shape()
    {
        (await Compile("""[{ "sort": [{ "number": "desc" }, { "count": "asc" }] }]""")).PageStages[1]
            .ShouldBeBson(BsonDocument.Parse("{ $sort: { Number: -1, Count: 1, _id: 1 } }"));
    }

    [Fact]
    public async Task A_keyset_cursor_merges_into_the_leading_scope_match()
    {
        var first = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]""");
        var id = new BsonBinaryData(Guid.Parse("195fb742-82b3-405e-b77b-42838eb0aaa9"), GuidRepresentation.Standard);
        var cursor = BindHost.Cursors.Encode(new CursorPayload(first.Fingerprint, PagingMode.Keyset, [new CursorValue("number", true, new BsonString("abc")), new CursorValue("_id", true, id)], 0));
        var compiled = await Compile($$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""");

        ShouldBe(compiled.PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { $or: [ { Number: "abc", _id: { $gt: {{id.ToJson()}} } }, { Number: { $gt: "abc" } } ] } ] } }""",
            "{ $sort: { Number: 1, _id: 1 } }",
            "{ $limit: 3 }");
    }

    [Fact]
    public async Task An_unwound_page_is_ordered_by_the_key_and_one_index_per_unwind()
    {
        // No sort of the caller's: $skip over $unwind output nothing ordered repeats and drops
        // rows between pages, so the binder supplies the key and the compiler completes it.
        var compiled = await Compile("""[{ "unwind": { "path": "items" } }, { "page": { "limit": 10, "offset": 20 } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "__oxIx0" } }""",
            """{ $sort: { _id: 1, __oxIx0: 1 } }""",
            """{ $unset: ["__oxIx0"] }""",
            "{ $skip: 20 }",
            "{ $limit: 11 }");

        compiled.Bound.PagingMode.Should().Be(PagingMode.Offset);
        compiled.Bound.Sort!.Fields.Should().ContainSingle()
            .Which.Path.Wire.Should().Be("id", "the default sort is bound, so it reaches the canonical form and the fingerprint");
    }

    [Fact]
    public async Task A_grouped_page_is_ordered_by_its_keys()
    {
        var compiled = await Compile("""[{ "group": { "by": [{ "path": "state", "as": "st" }, { "path": "number", "as": "no" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } }]""");

        compiled.PageStages[^2].ShouldBeBson(BsonDocument.Parse("""{ $sort: { st: 1, no: 1 } }"""), "the keys are unique per group; an aggregate adds nothing to the order");
        compiled.PageStages.Should().NotContain(stage => stage.ToJson().Contains("__oxIx"), "a group replaces the row, so no unwind index survives it");
    }

    [Fact]
    public async Task A_keyset_cursor_survives_a_projection_that_drops_the_key()
    {
        // Without the key the sort reads a member that is not there and the cursor is built from
        // null, which matches nothing: page two came back empty.
        var compiled = await Compile("""[{ "project": { "number": 1, "id": 0 } }, { "page": { "limit": 2 } }]""");

        compiled.Bound.PagingMode.Should().Be(PagingMode.Keyset);
        compiled.KeyKeptAgainstProjection.Should().BeTrue();
        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1 } }"));
        compiled.PageStages[2].ShouldBeBson(BsonDocument.Parse("{ $sort: { _id: 1 } }"));

        var id = ObjectId.GenerateNewId();
        var cursor = MongoQueryEngine.NextCursor(compiled, new BsonDocument { ["_id"] = id, ["Number"] = "n" });

        cursor.Fields.Should().ContainSingle()
            .Which.Value.Should().Be((BsonValue)id, "the cursor reads the key the projection would have dropped");
    }

    [Fact]
    public async Task An_offset_cursor_after_a_group_skips()
    {
        var first = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": {} } }, { "page": { "limit": 2 } }]""");
        var cursor = BindHost.Cursors.Encode(new CursorPayload(first.Fingerprint, PagingMode.Offset, [], 4));
        var compiled = await Compile($$"""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": {} } }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $group: { _id: "$State" } }""",
            """{ $project: { st: "$_id", _id: 0 } }""",
            """{ $sort: { st: 1 } }""",
            "{ $skip: 4 }",
            "{ $limit: 3 }");
        compiled.Offset.Should().Be(4);
    }

    [Fact]
    public async Task An_addon_decimal_is_matched_at_the_key_and_under_the_wrapper()
    {
        var definitions = new OneDefinition("Preis", OxQL.Model.Addon.AddonKind.Decimal);
        var compiled = await Compile("""[{ "match": { "addon.Preis": { "eq": "1.5" } } }]""", context: BindHost.Context(addons: definitions));

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $or: [
                { $or: [ { "Addon.Preis": NumberDecimal("1.5") }, { "Addon.Preis": { $regularExpression: { pattern: "^1\\.50*$", options: "" } } } ] },
                { $or: [ { "Addon.Preis._v": NumberDecimal("1.5") }, { "Addon.Preis._v": { $regularExpression: { pattern: "^1\\.50*$", options: "" } } } ] } ] } }
            """));
    }

    private sealed class OneDefinition(string path, OxQL.Model.Addon.AddonKind kind) : OxQL.Model.Addon.IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<OxQL.Model.Addon.AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OxQL.Model.Addon.AddonDefinition>>([new OxQL.Model.Addon.AddonDefinition { Id = Guid.NewGuid(), Entity = entity, Path = path, Kind = kind }]);
    }
}
