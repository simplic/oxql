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
        var compiled = await Compile("""[{ "project": { "number": 1 } }, { "match": { "number": { "eq": "x" } } }, { "page": { "limit": 20, "count": true } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            "{ $match: { Number: 'x' } }",
            "{ $sort: { _id: 1 } }",
            "{ $limit: 21 }",
            "{ $project: { Number: 1 } }");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            "{ $match: { Number: 'x' } }",
            "{ $limit: 1001 }",
            "{ $count: 'n' }");
    }

    // ---- a projection runs after the page is taken (PERF 1) -------------------------------------

    [Fact]
    public async Task A_projection_runs_after_the_page_is_taken_wherever_the_caller_wrote_it()
    {
        const string Sorted = """{ "sort": [{ "number": "desc" }] }""";
        const string Projected = """{ "project": { "number": 1, "shipTo.city": 1 } }""";
        string[] expected =
        [
            $"{{ $match: {OrgJson} }}",
            "{ $sort: { Number: -1, _id: 1 } }",
            "{ $limit: 6 }",
            "{ $project: { Number: 1, 'ShipTo.City': 1 } }",
        ];

        var before = await Compile($$"""[{{Projected}}, {{Sorted}}, { "page": { "limit": 5 } }]""");
        var after = await Compile($$"""[{{Sorted}}, {{Projected}}, { "page": { "limit": 5 } }]""");

        ShouldBe(before.PageStages, expected);
        ShouldBe(after.PageStages, expected);
        before.LateProjections.Should().Equal([0], "explain says it at the stage");
        after.LateProjections.Should().Equal(1);

        // The paging sort's path survives a projection that drops it, so the page orders by the same values either way.
        var dropped = await Compile($$"""[{{Sorted}}, { "project": { "shipTo.city": 1 } }, { "page": { "limit": 5 } }]""");

        dropped.PageStages[^1].ShouldBeBson(BsonDocument.Parse("{ $project: { 'ShipTo.City': 1, Number: 1 } }"));

        // Several projections keep their order behind the page.
        var two = await Compile($$"""[{ "project": { "number": 1, "state": 1, "flag": 1 } }, {{Sorted}}, { "project": { "flag": 0 } }, { "page": { "limit": 5 } }]""");

        ShouldBe(two.PageStages.Skip(3).ToList(), "{ $project: { Number: 1, State: 1, Flag: 1 } }", "{ $project: { Flag: 0 } }");
    }

    [Fact]
    public async Task A_projection_stays_where_it_was_written_before_a_stage_that_reshapes_the_row_and_under_contract_1()
    {
        static string[] Kinds(CompiledQuery compiled) => compiled.PageStages.Select(stage => stage.GetElement(0).Name).ToArray();

        // An unwind after the projection unwinds what the projection kept.
        (await Compile("""[{ "project": { "number": 1, "items": 1 } }, { "unwind": { "path": "items", "as": "item" } }, { "page": { "limit": 5 } }]""")).PageStages[1]
            .Contains("$project").Should().BeTrue("a projection before an unwind is the unwind's input");

        // A group after it groups what it kept.
        Kinds(await Compile("""[{ "project": { "state": 1 } }, { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } }]"""))
            .Should().StartWith(["$match", "$project", "$group"]);

        // A join before the page that follows it adds its alias to what it kept.
        Kinds(await Compile("""[{ "project": { "flag": 0 } }, { "resolve": { "path": "customerId", "as": "cust" } }, { "match": { "cust.name": { "eq": "x" } } }, { "page": { "limit": 5 } }]"""))
            .Should().StartWith(["$match", "$project", "$lookup"]);

        // A page of thousands orders whole documents in the server's sort memory: its projection stays first.
        var report = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "project": { "number": 1 } }, { "page": { "limit": 1001 } }]""", BindHost.Context(BindHost.Options(options => options.Limits.MaxPageSize = 5_000)));

        Kinds(MongoCompiler.Compile(report, Options)).Should().Equal("$match", "$project", "$sort", "$limit");

        // Contract 1 pipelines are what they were: the stages in the order written.
        var legacy = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "project": { "number": 1 } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5 } }]""", BindHost.Context(contract: 1));
        var written = MongoCompiler.Compile(legacy, Options with { AsWritten = true });

        Kinds(written).Should().Equal("$match", "$project", "$sort", "$limit");
        written.LateProjections.Should().BeEmpty();
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
                { Amount: { $regularExpression: { pattern: "^12\\.50*\\z", options: "" } } } ] } }
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
                { Amount: { $regularExpression: { pattern: "^10(?:\\.0+)?\\z", options: "" } } } ] } }
            """));
    }

    [Fact]
    public async Task A_string_comparison_folds_under_the_collation_and_only_a_pattern_keeps_the_flag()
    {
        var compiled = await Compile("""
            [{ "match": { "or": [
                { "number": { "eq": "a.b", "options": { "caseSensitive": false } } },
                { "number": { "neq": "a", "options": { "caseSensitive": false } } },
                { "number": { "in": ["a", "b"], "options": { "caseSensitive": false } } },
                { "number": { "contains": "a", "options": { "caseSensitive": false } } },
                { "number": { "startsWith": "a", "options": { "caseSensitive": false } } },
                { "number": { "endsWith": "a", "options": { "caseSensitive": false } } },
                { "number": { "startsWith": "a" } },
                { "number": { "regex": "^a" } } ] } }]
            """);

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $or: [
                { Number: 'a.b' },
                { Number: { $ne: 'a' } },
                { Number: { $in: [ 'a', 'b' ] } },
                { Number: /[aÀ-Åà-åĀ-ąǍǎǞ-ǡǺǻȀ-ȃȦȧḀḁẠ-ặ]/i },
                { Number: { $gte: 'a', $lt: 'a￿' } },
                { Number: /[aÀ-Åà-åĀ-ąǍǎǞ-ǡǺǻȀ-ȃȦȧḀḁẠ-ặ]\z/i },
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
        var compiled = await Compile("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "filter": { "flag": { "eq": true } }, "limit": 5 } }, { "match": { "orders.number": { "eq": "x" } } }, { "page": { "count": true } }]""", "probe.customer");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""
            { $lookup: { from: "orders", localField: "_id", foreignField: "CustomerId",
                pipeline: [ { $match: {{OrgJson}} }, { $match: { Flag: true } }, { $sort: { _id: 1 } }, { $limit: 6 }, { $project: { _id: 1, Number: 1 } } ],
                as: "orders" } }
            """));
        compiled.PageStages[2].ShouldBeBson(BsonDocument.Parse("""{ $set: { __oxLk0: { $gt: [ { $size: "$orders" }, 5 ] }, orders: { $slice: [ "$orders", 5 ] } } }"""), "one child over the limit is fetched to flag the cut");
        compiled.PageStages[3].ShouldBeBson(BsonDocument.Parse("""{ $match: { "orders.Number": "x" } }"""));
        compiled.CountStages!.Should().HaveCount(6, "the lookup is read by the later match, so the count keeps it");

        var unread = await Compile("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "page": { "count": true } }]""", "probe.customer");

        unread.CountStages!.Should().HaveCount(3, "a lookup nothing reads is not in the count");
        unread.PageStages[^2]["$lookup"]["pipeline"].AsBsonArray[3].ShouldBeBson(BsonDocument.Parse("{ $project: { _id: 1, Number: 1 } }"), "the default select is the child's key and display member");
    }

    [Fact]
    public async Task Local_resolve_is_an_indexed_lookup_plus_the_first_element()
    {
        var compiled = await Compile("""[{ "resolve": { "path": "customerId", "as": "cust", "filter": { "matchCode": { "eq": "m" } } } }, { "sort": [{ "cust.name": "asc" }] }]""");

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

        compiled.KeyedResolves.Should().ContainSingle();
        compiled.SemiJoins.Should().ContainSingle();
        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $match: { ContactNumber: { $in: [] } } }"));

        compiled.SemiJoins[0].Ids.Add("c1");
        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $match: { ContactNumber: { $in: ['c1'] } } }"), "the slot is the live array in the filter");
    }

    [Fact]
    public async Task An_unwind_sorted_on_its_element_renames_after_the_page_and_pages_by_value()
    {
        const string Pipeline = """[{ "unwind": { "path": "items", "as": "it", "includeIndex": "i", "preserveNull": true } }, { "sort": [{ "it.quantity": "desc" }] }, { "page": { "limit": 10 } }]""";
        var compiled = await Compile(Pipeline);

        // The sort reads the element where the unwind left it; the rename waits for the page's rows.
        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: true, includeArrayIndex: "i" } }""",
            """{ $sort: { "Items.Quantity": -1, _id: 1, i: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { it: "$Items" } }""",
            """{ $unset: "Items" }""");
        compiled.Bound.PagingMode.Should().Be(PagingMode.Keyset);
        compiled.PagingIndexes.Should().Equal("i");
        compiled.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: false, [], RenameLate: true), "the order is the elements', not the parents'");

        // The cursor carries the sort value, the key and the position; the next page starts after them.
        var id = new BsonBinaryData(Guid.Parse("195fb742-82b3-405e-b77b-42838eb0aaa9"), GuidRepresentation.Standard);
        var last = new BsonDocument { ["_id"] = id, ["it"] = new BsonDocument("Quantity", 7), ["i"] = 2 };
        var payload = MongoQueryEngine.NextCursor(compiled, last, served: 10);

        payload.Fields.Should().BeEquivalentTo([new CursorValue("it.quantity", false, new BsonInt32(7)), new CursorValue("_id", true, id), new CursorValue("i", true, new BsonInt32(2))]);

        var next = await Compile(Pipeline.Replace("""{ "limit": 10 }""", $$"""{ "limit": 10, "cursor": "{{BindHost.Cursors.Encode(payload)}}" }"""));

        ShouldBe(next.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: true, includeArrayIndex: "i" } }""",
            $$"""
            { $match: { $and: [ { "Items.Quantity": { $not: { $gt: 7 } } }, { $or: [
                { "Items.Quantity": 7, _id: {{id.ToJson()}}, i: { $gt: 2 } },
                { "Items.Quantity": 7, _id: { $gt: {{id.ToJson()}} } },
                { "Items.Quantity": { $lt: 7 } },
                { "Items.Quantity": { $numberDouble: "NaN" } },
                { "Items.Quantity": null } ] } ] } }
            """,
            """{ $sort: { "Items.Quantity": -1, _id: 1, i: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { it: "$Items" } }""",
            """{ $unset: "Items" }""");
    }

    [Fact]
    public async Task Group_reshapes_to_the_aliases_and_counts_groups_after_the_group()
    {
        // G2: every leg of a composite _id is wrapped in $ifNull. A scalar $group _id
        // normalises a missing member to null; a subdocument _id omits the field instead, so
        // the same data bucketed null and missing together under one key and apart under two,
        // and the row lost the member altogether.
        var compiled = await Compile("""[{ "group": { "by": [{ "path": "state", "as": "st" }, { "dateTrunc": { "path": "when", "unit": "week", "timezone": "Europe/Berlin" }, "as": "wk" }], "fields": { "n": { "count": true }, "total": { "sum": "amount" }, "kinds": { "countDistinct": "number" }, "x": { "max": { "add": ["ratio", 1] } } } } }, { "sort": [{ "n": "desc" }] }, { "page": { "limit": 5, "count": true } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            $$"""{ $group: { _id: { st: { $ifNull: [ "$State", null ] }, wk: { $ifNull: [ {{BsonAssertions.InCalendar("""{ $dateTrunc: { date: "$When", unit: "week", timezone: "Europe/Berlin", startOfWeek: "monday" } }""")}}, null ] } }, n: { $sum: 1 }, total: { $sum: "$Amount" }, kinds: { $addToSet: "$Number" }, x: { $max: { $add: [ "$Ratio", { $literal: NumberLong(1) } ] } } } }""",
            """{ $addFields: { kinds: { $size: "$kinds" } } }""",
            """{ $project: { st: "$_id.st", wk: "$_id.wk", n: 1, total: 1, kinds: 1, x: 1, _id: 0 } }""",
            "{ $sort: { n: -1, st: 1, wk: 1 } }",
            "{ $limit: 6 }");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            $$"""{ $group: { _id: { st: { $ifNull: [ "$State", null ] }, wk: { $ifNull: [ {{BsonAssertions.InCalendar("""{ $dateTrunc: { date: "$When", unit: "week", timezone: "Europe/Berlin", startOfWeek: "monday" } }""")}}, null ] } }, n: { $sum: 1 }, total: { $sum: "$Amount" }, kinds: { $addToSet: "$Number" }, x: { $max: { $add: [ "$Ratio", { $literal: NumberLong(1) } ] } } } }""",
            """{ $addFields: { kinds: { $size: "$kinds" } } }""",
            """{ $project: { st: "$_id.st", wk: "$_id.wk", n: 1, total: 1, kinds: 1, x: 1, _id: 0 } }""",
            "{ $limit: 1001 }",
            "{ $count: 'n' }");

        var single = await Compile("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "month" }, "as": "m" }], "fields": {} } }]""");

        single.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""{ $group: { _id: {{BsonAssertions.InCalendar("""{ $dateTrunc: { date: "$When", unit: "month", timezone: "UTC" } }""")}} } }"""));
        single.PageStages[2].ShouldBeBson(BsonDocument.Parse("""{ $project: { m: "$_id", _id: 0 } }"""));
    }

    [Fact]
    public async Task Projection_is_inclusion_with_the_key_by_default_or_exclusion()
    {
        (await Compile("""[{ "project": { "number": 1, "shipTo.city": 1, "qrCode": 1 } }]""")).PageStages[^1]
            .ShouldBeBson(BsonDocument.Parse("""{ $project: { Number: 1, "ShipTo.City": 1, QRCode: 1 } }"""));
        (await Compile("""[{ "project": { "number": 1, "id": 0 } }]""")).PageStages[^1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1 } }"), "the key the cursor and the tie-breaker read survives a projection that drops it");
        (await Compile("""[{ "project": { "addon": 0, "items": 0 } }]""")).PageStages[^1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Addon: 0, Items: 0 } }"));
        (await Compile("""[{ "project": { "addon": 0, "id": 0 } }]""")).PageStages[^1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Addon: 0 } }"), "an exclusion that names the key keeps it for the same reason");

        (await Compile("""[{ "project": { "number": 1, "id": 0 } }]""")).KeyKeptAgainstProjection
            .Should().BeTrue("a contract 1 row drops the key again in the executor");
    }

    [Fact]
    public async Task A_projection_keeps_the_reference_member_a_remote_resolve_reads()
    {
        (await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "number": 1, "contact": 1 } }]""")).PageStages[^1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1, ContactNumber: 1 } }"), "the resolver keys the page rows on the member after the aggregate");
        (await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "number": 1, "contactNumber": 1, "contact.name": 1 } }]""")).PageStages[^1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1, ContactNumber: 1 } }"), "a kept member is not added twice");
        (await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "contactNumber": 0, "addon": 0 } }]""")).PageStages[^1]
            .ShouldBeBson(BsonDocument.Parse("{ $project: { Addon: 0 } }"), "an exclusion of the member is not emitted");

        var nothingLeft = await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "contactNumber": 0 } }]""");

        nothingLeft.PageStages.Should().NotContain(stage => stage.Contains("$project"), "an empty $project is not a valid stage");
    }

    [Fact]
    public async Task A_remote_resolve_whose_alias_the_projection_drops_is_not_run_and_keeps_nothing()
    {
        var compiled = await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "number": 1 } }]""");

        compiled.KeyedResolves.Should().BeEmpty("the row does not carry the alias, so there is nothing to ask the owner for");
        compiled.PageStages[^1].ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1 } }"), "no reference member is kept for a resolve that does not run");
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
            $$"""{ $match: { $and: [ {{OrgJson}}, { Number: { $gte: "abc" } }, { $or: [ { Number: "abc", _id: { $gt: {{id.ToJson()}} } }, { Number: { $gt: "abc" } } ] } ] } }""",
            "{ $sort: { Number: 1, _id: 1 } }",
            "{ $limit: 3 }");

        // Descending, the rows after the position are the smaller values and the nulls: everything not above it.
        var descending = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": "desc" }] }, { "page": { "limit": 2 } }]""");
        var back = BindHost.Cursors.Encode(new CursorPayload(descending.Fingerprint, PagingMode.Keyset, [new CursorValue("number", false, new BsonString("abc")), new CursorValue("_id", true, id)], 0));

        (await Compile($$"""[{ "sort": [{ "number": "desc" }] }, { "page": { "limit": 2, "cursor": "{{back}}" } }]""")).PageStages[0].ShouldBeBson(BsonDocument.Parse($$"""
            { $match: { $and: [ {{OrgJson}}, { Number: { $not: { $gt: "abc" } } },
                { $or: [ { Number: "abc", _id: { $gt: {{id.ToJson()}} } }, { Number: { $lt: "abc" } }, { Number: null } ] } ] } }
            """));

        // After a null the leading field bounds nothing, and the key alone is its own range.
        var afterNull = BindHost.Cursors.Encode(new CursorPayload(first.Fingerprint, PagingMode.Keyset, [new CursorValue("number", true, BsonNull.Value), new CursorValue("_id", true, id)], 0));

        (await Compile($$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{afterNull}}" } }]""")).PageStages[0].ShouldBeBson(BsonDocument.Parse($$"""
            { $match: { $and: [ {{OrgJson}}, { $or: [ { Number: null, _id: { $gt: {{id.ToJson()}} } }, { Number: { $ne: null } } ] } ] } }
            """));

        // Contract 1 keeps the predicate it always sent.
        var legacy = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]""", BindHost.Context(contract: 1));
        var old = BindHost.Cursors.Encode(new CursorPayload(legacy.Fingerprint, PagingMode.Keyset, [new CursorValue(legacy.Sort!.Fields[0].Path.Wire, true, new BsonString("abc")), new CursorValue("_id", true, id)], 0));
        var again = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{old}}" } }]""", BindHost.Context(contract: 1));

        MongoCompiler.Compile(again, Options with { AsWritten = true }).PageStages[0].ShouldBeBson(BsonDocument.Parse($$"""
            { $match: { $and: [ {{OrgJson}}, { $or: [ { Number: "abc", _id: { $gt: {{id.ToJson()}} } }, { Number: { $gt: "abc" } } ] } ] } }
            """));
    }

    /// <summary>
    /// The key is one leg of the order: in the direction the sort gives it, at the place the sort
    /// gives it, and ascending at the end where the sort does not name it. The page's sort and the
    /// rows after a cursor's position read that one direction.
    /// </summary>
    [Fact]
    public async Task A_sort_naming_the_key_orders_and_continues_by_the_key_in_the_sorts_direction()
    {
        var id = new BsonBinaryData(Guid.Parse("195fb742-82b3-405e-b77b-42838eb0aaa9"), GuidRepresentation.Standard);

        async Task<CompiledQuery> Next(string sort, Func<CompiledQuery, CursorPayload> position)
        {
            var first = await Compile($$"""[{ "sort": {{sort}} }, { "page": { "limit": 2 } }]""");

            return await Compile($$"""[{ "sort": {{sort}} }, { "page": { "limit": 2, "cursor": "{{BindHost.Cursors.Encode(position(first))}}" } }]""");
        }

        CursorPayload After(CompiledQuery first) => MongoQueryEngine.NextCursor(first, new BsonDocument { ["_id"] = id, ["Number"] = "abc" });

        // A cursor names the row by every sort member and, last, by the key ascending, whatever the sort names.
        var last = await Compile("""[{ "sort": [{ "number": "desc" }, { "id": "desc" }] }, { "page": { "limit": 2 } }]""");

        After(last).Fields.Should().Equal(new CursorValue("number", false, new BsonString("abc")), new CursorValue("id", false, id), new CursorValue("_id", true, id));

        ShouldBe((await Next("""[{ "number": "desc" }, { "id": "desc" }]""", After)).PageStages,
            $$"""
            { $match: { $and: [ {{OrgJson}}, { Number: { $not: { $gt: "abc" } } },
                { $or: [ { Number: "abc", _id: { $lt: {{id.ToJson()}} } }, { Number: "abc", _id: null }, { Number: { $lt: "abc" } }, { Number: null } ] } ] } }
            """,
            "{ $sort: { Number: -1, _id: -1 } }",
            "{ $limit: 3 }");

        ShouldBe((await Next("""[{ "number": "asc" }, { "id": "asc" }]""", After)).PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { Number: { $gte: "abc" } }, { $or: [ { Number: "abc", _id: { $gt: {{id.ToJson()}} } }, { Number: { $gt: "abc" } } ] } ] } }""",
            "{ $sort: { Number: 1, _id: 1 } }",
            "{ $limit: 3 }");

        // The key first: what follows it orders nothing further, and still stands in the position.
        ShouldBe((await Next("""[{ "id": "desc" }, { "number": "asc" }]""", After)).PageStages,
            $$"""
            { $match: { $and: [ {{OrgJson}}, { _id: { $not: { $gt: {{id.ToJson()}} } } },
                { $or: [ { _id: {{id.ToJson()}}, Number: { $gt: "abc" } }, { _id: { $lt: {{id.ToJson()}} } }, { _id: null } ] } ] } }
            """,
            "{ $sort: { _id: -1, Number: 1 } }",
            "{ $limit: 3 }");

        // The key alone.
        ShouldBe((await Next("""[{ "id": "desc" }]""", After)).PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { $or: [ { _id: { $lt: {{id.ToJson()}} } }, { _id: null } ] } ] } }""",
            "{ $sort: { _id: -1 } }",
            "{ $limit: 3 }");

        ShouldBe((await Next("""[{ "id": "asc" }]""", After)).PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { _id: { $gt: {{id.ToJson()}} } } ] } }""",
            "{ $sort: { _id: 1 } }",
            "{ $limit: 3 }");

        // Named twice, a member orders once: at its first place, in the direction written last.
        ShouldBe((await Next("""[{ "number": "asc" }, { "id": "asc" }, { "number": "desc" }, { "id": "desc" }]""", After)).PageStages,
            $$"""
            { $match: { $and: [ {{OrgJson}}, { Number: { $not: { $gt: "abc" } } },
                { $or: [ { Number: "abc", _id: { $lt: {{id.ToJson()}} } }, { Number: "abc", _id: null }, { Number: { $lt: "abc" } }, { Number: null } ] } ] } }
            """,
            "{ $sort: { Number: -1, _id: -1 } }",
            "{ $limit: 3 }");
    }

    /// <summary>Contract 1 on the rewrite, which emits no leading range: the key is the same one leg.</summary>
    [Fact]
    public async Task A_contract_1_sort_naming_the_key_orders_and_continues_by_the_key_in_the_sorts_direction()
    {
        var id = new BsonBinaryData(Guid.Parse("195fb742-82b3-405e-b77b-42838eb0aaa9"), GuidRepresentation.Standard);
        var context = BindHost.Context(contract: 1);

        async Task<CompiledQuery> Next(string sort)
        {
            var first = MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{ "sort": {{sort}} }, { "page": { "limit": 2 } }]""", context), Options with { AsWritten = true });
            var cursor = BindHost.Cursors.Encode(MongoQueryEngine.NextCursor(first, new BsonDocument { ["_id"] = id, ["Number"] = "abc" }));

            return MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{ "sort": {{sort}} }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""", context), Options with { AsWritten = true });
        }

        ShouldBe((await Next("""[{ "id": "desc" }]""")).PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { $or: [ { _id: { $lt: {{id.ToJson()}} } }, { _id: null } ] } ] } }""",
            "{ $sort: { _id: -1 } }",
            "{ $limit: 3 }");

        ShouldBe((await Next("""[{ "number": "asc" }, { "id": "desc" }]""")).PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { $or: [ { Number: "abc", _id: { $lt: {{id.ToJson()}} } }, { Number: "abc", _id: null }, { Number: { $gt: "abc" } } ] } ] } }""",
            "{ $sort: { Number: 1, _id: -1 } }",
            "{ $limit: 3 }");
    }

    [Fact]
    public async Task An_unwound_page_orders_and_cuts_the_parents_first_and_is_ordered_by_the_key_and_one_index_per_unwind()
    {
        // No sort of the caller's: the binder supplies the key and the compiler completes it with the
        // position of the unwind. The order is the parents', so they are ordered and cut before the unwind.
        const string Pipeline = """[{ "unwind": { "path": "items", "as": "item" } }, { "page": { "limit": 10 } }]""";
        var compiled = await Compile(Pipeline);

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            ItemsYieldARow,
            """{ $sort: { _id: 1 } }""",
            "{ $limit: 11 }",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "__oxIx0" } }""",
            """{ $sort: { _id: 1, __oxIx0: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { item: "$Items" } }""",
            """{ $unset: "Items" }""");

        compiled.Bound.PagingMode.Should().Be(PagingMode.Keyset);
        compiled.Bound.Sort!.Fields.Should().ContainSingle()
            .Which.Path.Wire.Should().Be("id", "the default sort is bound, so it reaches the canonical form and the fingerprint");
        compiled.PagingIndexes.Should().Equal("__oxIx0");
        compiled.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: true, [], RenameLate: true));

        // The next page: the parents from the cursor's on (it may hold more rows), one more of them, and after the unwind the rows after the position.
        var id = new BsonBinaryData(Guid.Parse("195fb742-82b3-405e-b77b-42838eb0aaa9"), GuidRepresentation.Standard);
        var payload = MongoQueryEngine.NextCursor(compiled, new BsonDocument { ["_id"] = id, ["__oxIx0"] = 3 }, served: 10);
        var next = await Compile(Pipeline.Replace("""{ "limit": 10 }""", $$"""{ "limit": 10, "cursor": "{{BindHost.Cursors.Encode(payload)}}" }"""));

        ShouldBe(next.PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { _id: { $gte: {{id.ToJson()}} } } ] } }""",
            ItemsYieldARow,
            """{ $sort: { _id: 1 } }""",
            "{ $limit: 12 }",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "__oxIx0" } }""",
            $$"""{ $match: { $and: [ { _id: { $gte: {{id.ToJson()}} } }, { $or: [ { _id: {{id.ToJson()}}, __oxIx0: { $gt: 3 } }, { _id: { $gt: {{id.ToJson()}} } } ] } ] } }""",
            """{ $sort: { _id: 1, __oxIx0: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { item: "$Items" } }""",
            """{ $unset: "Items" }""");

        // An internal owner's offset still skips, behind the parents its offset reaches.
        var skipped = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "page": { "limit": 10, "offset": 20 } }]""", context: BindHost.Context() with { Internal = true });

        skipped.PageStages.Select(stage => stage.ToJson()).Should().ContainInOrder(
            BsonDocument.Parse("{ $limit: 31 }").ToJson(), BsonDocument.Parse("{ $skip: 20 }").ToJson(), BsonDocument.Parse("{ $limit: 11 }").ToJson());
    }

    /// <summary>How many rows the unwind of <c>Items</c> answers for a row: an array's size, none for a null or a missing member, one for any other value.</summary>
    private const string ItemRows = """{ $cond: [ { $isArray: "$Items" }, { $size: "$Items" }, { $cond: [ { $in: [ { $type: "$Items" }, ["missing", "null", "undefined"] ] }, 0, 1 ] } ] }""";

    /// <summary>The types of a value that is no array and yields a row: every one but array, null and undefined.</summary>
    private const string ValueTypes = "[1, 2, 3, 5, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 19, -1, 127]";

    /// <summary>The types of an element none of whose members exists: every one but object and array.</summary>
    private const string MemberlessTypes = "[1, 2, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, -1, 127]";

    /// <summary>
    /// What a parent cut before the unwind is asked: that its unwind answers a row, as a plain condition. "An array that
    /// holds anything, or a value that is no array", written as neither failing so that the first is asked first; no
    /// equality with null or an empty array, which an array meets by an element, and no expression.
    /// </summary>
    private const string ItemsYieldARow = $$"""{ $match: { $nor: [ { $and: [ { Items: { $not: { $elemMatch: { $exists: true } } } }, { Items: { $not: { $type: {{ValueTypes}} } } } ] } ] } }""";

    /// <summary>
    /// What rules out that the member is one value with a row of its own: an array (only an array is at least the empty
    /// one), then a null or a member not stored. After the conditions that fail, an undefined (<see cref="ItemsUndefined"/>).
    /// </summary>
    private const string ItemsNoValue = """{ Items: { $gte: [] } }, { Items: { $in: [null] } }""";

    private const string ItemsUndefined = """{ Items: { $type: 6 } }""";

    /// <summary>The array holds an element none of whose members exists: a null, an undefined or a scalar.</summary>
    private const string ItemsHoldMemberless = $$"""{ Items: { $elemMatch: { $type: {{MemberlessTypes}} } } }""";

    /// <summary>
    /// What a filter that only spares the unwind lets pass beside an object that passes: what holds no array of elements
    /// (the unwind drops it, or the conditions after it judge the one value), and, where an absent member may pass, a
    /// member that is, or holds an element, of a kind without members.
    /// </summary>
    private const string ItemsNoArrayOfElements = """{ Items: { $not: { $elemMatch: { $exists: true } } } }""";

    private const string ItemsOfAMemberlessKind = $$"""{ Items: { $type: {{MemberlessTypes}} } }""";

    /// <summary>The first and the last position a parent's rows stand at: 0 … n − 1 of an array, −1 for the one row of a value that is no array.</summary>
    private const string ItemsLo = """{ $cond: [ { $isArray: "$Items" }, 0, -1 ] }""";

    private const string ItemsHi = $$"""{ $subtract: [ { $add: [ {{ItemsLo}}, {{ItemRows}} ] }, 1 ] }""";

    [Fact]
    public async Task The_position_after_the_parents_members_does_not_keep_the_parents_from_being_cut_first()
    {
        // `number, position`: within a parent the position is the order its elements have anyway. Each parent is
        // numbered by the first position it has a row at (0 for an array, before it for a value that is no array),
        // and the members, that position and the key order the parents: on a first page too, since the one row of
        // a value that is no array precedes every row at position 0 of the parents it ties with.
        const string Pipeline = """[{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "sort": [{ "number": "asc" }, { "at": "asc" }] }, { "page": { "limit": 10 } }]""";
        var compiled = await Compile(Pipeline);

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            ItemsYieldARow,
            $$"""{ $set: { __oxFirst: {{ItemsLo}} } }""",
            """{ $sort: { Number: 1, __oxFirst: 1, _id: 1 } }""",
            "{ $limit: 11 }",
            """{ $unset: "__oxFirst" }""",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "at" } }""",
            """{ $sort: { Number: 1, at: 1, _id: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { item: "$Items" } }""",
            """{ $unset: "Items" }""");
        compiled.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: true, [], RenameLate: true) { IndexAscending = true });

        // After a position (number, at, key): the parents from that number on; of those that tie on the number, the
        // ones with a row left, each numbered by the first position it has one at (the position's own when its key
        // comes later, else the next); ordered by the number, that position and the key.
        var id = new BsonBinaryData(Guid.Parse("195fb742-82b3-405e-b77b-42838eb0aaa9"), GuidRepresentation.Standard);
        var payload = MongoQueryEngine.NextCursor(compiled, new BsonDocument { ["_id"] = id, ["Number"] = "$n", ["at"] = 3 }, served: 10);
        var next = await Compile(Pipeline.Replace("""{ "limit": 10 }""", $$"""{ "limit": 10, "cursor": "{{BindHost.Cursors.Encode(payload)}}" }"""));

        ShouldBe(next.PageStages,
            $$"""{ $match: { $and: [ {{OrgJson}}, { Number: { $gte: "$n" } } ] } }""",
            ItemsYieldARow,
            $$"""{ $set: { __oxFirst: { $cond: [ { $eq: [ { $ifNull: [ "$Number", null ] }, { $literal: "$n" } ] }, { $max: [ {{ItemsLo}}, { $cond: [ { $gt: [ "$_id", { $literal: {{id.ToJson()}} } ] }, NumberLong(3), NumberLong(4) ] } ] }, {{ItemsLo}} ] } } }""",
            $$"""{ $match: { $expr: { $lte: [ "$__oxFirst", {{ItemsHi}} ] } } }""",
            """{ $sort: { Number: 1, __oxFirst: 1, _id: 1 } }""",
            "{ $limit: 12 }",
            """{ $unset: "__oxFirst" }""",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "at" } }""",
            $$"""{ $match: { $and: [ { Number: { $gte: "$n" } }, { $or: [ { Number: "$n", at: 3, _id: { $gt: {{id.ToJson()}} } }, { Number: "$n", at: { $gt: 3 } }, { Number: { $gt: "$n" } } ] } ] } }""",
            """{ $sort: { Number: 1, at: 1, _id: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { item: "$Items" } }""",
            """{ $unset: "Items" }""");

        // Descending the first row of a parent is its last element: the parents are ordered by the last position they hold a row at.
        var descending = await Compile("""[{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "sort": [{ "number": "asc" }, { "at": "desc" }, { "id": "desc" }] }, { "page": { "limit": 10 } }]""");

        ShouldBe(descending.PageStages.Skip(2).Take(4).ToList(),
            $$"""{ $set: { __oxFirst: {{ItemsHi}} } }""",
            """{ $sort: { Number: 1, __oxFirst: -1, _id: -1 } }""",
            "{ $limit: 11 }",
            """{ $unset: "__oxFirst" }""");
        descending.Unwind!.IndexAscending.Should().BeFalse();

        // The position alone: every parent ties, so the first position and the key order them and nothing bounds a later page but the position.
        var alone = await Compile("""[{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "sort": [{ "at": "asc" }] }, { "page": { "limit": 10 } }]""");

        ShouldBe(alone.PageStages.Take(5).ToList(), $"{{ $match: {OrgJson} }}", ItemsYieldARow, $$"""{ $set: { __oxFirst: {{ItemsLo}} } }""", """{ $sort: { __oxFirst: 1, _id: 1 } }""", "{ $limit: 11 }");

        // A position that is none (the row of a value that is no array) lies before every position of an array.
        var none = MongoQueryEngine.NextCursor(compiled, new BsonDocument { ["_id"] = id, ["Number"] = "$n", ["at"] = BsonNull.Value }, served: 10);
        var afterNone = await Compile(Pipeline.Replace("""{ "limit": 10 }""", $$"""{ "limit": 10, "cursor": "{{BindHost.Cursors.Encode(none)}}" }"""));

        afterNone.PageStages[2].ShouldBeBson(BsonDocument.Parse(
            $$"""{ $set: { __oxFirst: { $cond: [ { $eq: [ { $ifNull: [ "$Number", null ] }, { $literal: "$n" } ] }, { $max: [ {{ItemsLo}}, { $cond: [ { $gt: [ "$_id", { $literal: {{id.ToJson()}} } ] }, -1, 0 ] } ] }, {{ItemsLo}} ] } } }"""));
    }

    [Theory]
    [InlineData("""{ "unwind": { "path": "items", "as": "item", "includeIndex": "at", "preserveNull": true } }, { "sort": [{ "number": "asc" }, { "at": "asc" }] }""", "a row kept without an element stands at no position")]
    [InlineData("""{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "match": { "item.quantity": { "gt": 2 } } }, { "sort": [{ "number": "asc" }, { "at": "asc" }] }""", "the rows of a parent no longer stand at every position from the first")]
    [InlineData("""{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "sort": [{ "number": "asc" }, { "at": "asc" }, { "item.quantity": "asc" }] }""", "what follows the position is not the key")]
    [InlineData("""{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "sort": [{ "item.quantity": "asc" }, { "at": "asc" }] }""", "the order is the elements'")]
    public async Task The_parents_are_not_cut_first_by_position_where_the_rows_could_differ(string stages, string because)
    {
        var compiled = await Compile($$"""[{{stages}}, { "page": { "limit": 10 } }]""");

        (compiled.Unwind?.ParentsFirst ?? false).Should().BeFalse(because);
        compiled.Unwind?.IndexAscending.Should().BeNull(because);
    }

    [Fact]
    public async Task What_is_asked_of_the_element_after_an_unwind_is_asked_of_the_collection_before_it()
    {
        var compiled = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "gt": 2 }, "number": { "neq": "x" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10, "count": true } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $match: { $and: [ { Number: { $ne: "x" } }, { "Items.Quantity": { $gt: 2 } } ] } }""",
            """{ $sort: { Number: 1, _id: 1 } }""",
            "{ $limit: 11 }",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "__oxIx0" } }""",
            """{ $match: { $and: [ { "Items.Quantity": { $gt: 2 } }, { Number: { $ne: "x" } } ] } }""",
            """{ $sort: { Number: 1, _id: 1, __oxIx0: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { item: "$Items" } }""",
            """{ $unset: "Items" }""");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            """{ $match: { $and: [ { Number: { $ne: "x" } }, { "Items.Quantity": { $gt: 2 } } ] } }""",
            "{ $limit: 1001 }",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "__oxIx0" } }""",
            """{ $match: { $and: [ { "Items.Quantity": { $gt: 2 } }, { Number: { $ne: "x" } } ] } }""",
            "{ $limit: 1001 }",
            "{ $count: 'n' }");

        // The count asks what the cut of the parents asks: every parent that passes yields a row it counts, so as many
        // parents as it is bounded by rows hold at least that many rows, and the database stops reading there.
        compiled.CountParentsAt.Should().Be(2);
        compiled.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: true, ["item.quantity"], RenameLate: true));

        // A condition on the position cannot be asked of the collection: the element one still is, the parents are not cut.
        var byPosition = await Compile("""[{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "match": { "item.quantity": { "gt": 2 }, "at": { "gte": 1 } } }, { "page": { "limit": 10 } }]""");

        byPosition.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: false, ["item.quantity"], RenameLate: true));
        byPosition.PageStages[1].ShouldBeBson(BsonDocument.Parse("""{ $match: { "Items.Quantity": { $gt: 2 } } }"""));
        byPosition.PageStages[2].Contains("$unwind").Should().BeTrue();

        // A kept row without an element has no element to pass anything: nothing is asked before a preserving unwind.
        var preserved = await Compile("""[{ "unwind": { "path": "items", "as": "item", "preserveNull": true } }, { "match": { "item.quantity": { "neq": 2 } } }, { "page": { "limit": 10 } }]""");

        preserved.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: false, [], RenameLate: true));
        preserved.PageStages[1].Contains("$unwind").Should().BeTrue();

        // One comparison of one member that no absent member passes is asked through the collection's path, as above. Several
        // conditions, a range on one member included, have to hold of one element (read through the path two elements could
        // meet one each): some object in the array passes them all ($elemMatch), or the member is one value that passes them
        // itself, written as "nothing rules that out": it is no array, no null and not missing, no condition fails, it is
        // not undefined. The database asks $elemMatch first and stops there for a row an element of which passes.
        var two = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "gt": 2, "lt": 9 } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10, "count": true } }]""");
        var twoAsked = $$"""{ $match: { $or: [ { Items: { $elemMatch: { $and: [ { Quantity: { $gt: 2 } }, { Quantity: { $lt: 9 } } ] } } }, { $nor: [ {{ItemsNoValue}}, { $nor: [ { "Items.Quantity": { $gt: 2 } } ] }, { $nor: [ { "Items.Quantity": { $lt: 9 } } ] }, {{ItemsUndefined}} ] } ] } }""";

        two.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: true, ["item.quantity"], RenameLate: true));
        two.PageStages[1].ShouldBeBson(BsonDocument.Parse(twoAsked));
        two.CountStages![1].ShouldBeBson(BsonDocument.Parse(twoAsked));
        two.CountStages![2].ShouldBeBson(BsonDocument.Parse("{ $limit: 1001 }"));

        // What an element none of whose members exists passes (a null or a scalar in the array) is asked of three kinds of
        // row: an object in the array that passes, an element without members in the array, and the one value in the array's
        // place that passes. The parents are cut first again, and the count unwinds the rows that pass alone.
        var negative = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "neq": 2 } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10, "count": true } }]""");
        var negated = $$"""{ $or: [ { Items: { $elemMatch: { Quantity: { $ne: 2 } } } }, {{ItemsHoldMemberless}}, { $nor: [ {{ItemsNoValue}}, { $nor: [ { "Items.Quantity": { $ne: 2 } } ] }, {{ItemsUndefined}} ] } ] }""";

        ShouldBe(negative.PageStages,
            $"{{ $match: {OrgJson} }}",
            $$"""{ $match: {{negated}} }""",
            """{ $sort: { Number: 1, _id: 1 } }""",
            "{ $limit: 11 }",
            """{ $unwind: { path: "$Items", preserveNullAndEmptyArrays: false, includeArrayIndex: "__oxIx0" } }""",
            """{ $match: { "Items.Quantity": { $ne: 2 } } }""",
            """{ $sort: { Number: 1, _id: 1, __oxIx0: 1 } }""",
            "{ $limit: 11 }",
            """{ $set: { item: "$Items" } }""",
            """{ $unset: "Items" }""");
        negative.CountStages![1].ShouldBeBson(BsonDocument.Parse($$"""{ $match: {{negated}} }"""));
        negative.CountParentsAt.Should().Be(2);

        // A step of the count with another bound bounds the parents by it as well.
        var bounded = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "neq": 2 } } }, { "page": { "limit": 0, "count": { "upTo": 500 } } }]""");

        bounded.CountStages![bounded.CountParentsAt!.Value].ShouldBeBson(BsonDocument.Parse("{ $limit: 501 }"));
        bounded.CountStages![^2].ShouldBeBson(BsonDocument.Parse("{ $limit: 501 }"));
        negative.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: true, ["item.quantity"], RenameLate: true));

        foreach (var absentPasses in new[] { """{ "item.quantity": { "nin": [1, 2] } }""", """{ "item.price.currency": { "eq": null } }""", """{ "item.price.currency": { "exists": false } }""", """{ "not": { "item.quantity": { "gt": 2 } } }""" })
        {
            var asked = await Compile($$"""[{ "unwind": { "path": "items", "as": "item" } }, { "match": {{absentPasses}} }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10, "count": true } }]""");

            asked.Unwind!.ParentsFirst.Should().BeTrue(absentPasses);
            asked.Unwind.Prefiltered.Should().ContainSingle(absentPasses);
            asked.PageStages[1]["$match"]["$or"].AsBsonArray.Should().HaveCount(3, absentPasses);
            asked.PageStages[1]["$match"]["$or"][1].ShouldBeBson(BsonDocument.Parse(ItemsHoldMemberless), absentPasses);
            asked.PageStages[1].ToJson().Should().NotContain("__oxAbsent", absentPasses).And.NotContain("$expr");
            asked.CountStages![1].ShouldBeBson(asked.PageStages[1], absentPasses);
        }

        // A parent's own condition beside it: asked first, with the organisation's.
        var withParent = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "neq": 2 }, "number": { "gt": "a" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10 } }]""");

        withParent.PageStages[1]["$match"]["$and"].AsBsonArray.Should().HaveCount(2);
        withParent.PageStages[1]["$match"]["$and"][1].ShouldBeBson(BsonDocument.Parse(negated));

        // Beside a condition no absent element passes the conjunction fails for it: no element without members is asked for.
        var mixed = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "gt": 2, "neq": 5 } } }, { "page": { "limit": 10 } }]""");

        mixed.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: true, ["item.quantity"], RenameLate: true));
        mixed.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""{ $match: { $or: [ { Items: { $elemMatch: { $and: [ { Quantity: { $gt: 2 } }, { Quantity: { $ne: 5 } } ] } } }, { $nor: [ {{ItemsNoValue}}, { $nor: [ { "Items.Quantity": { $gt: 2 } } ] }, { $nor: [ { "Items.Quantity": { $ne: 5 } } ] }, {{ItemsUndefined}} ] } ] } }"""));

        // Where the conditions do not say what an absent member passes (a set that holds null), the database is asked: the
        // conditions are read at a member no row holds, of a row whose array holds an element without members ("neither is
        // there no such element, nor does a condition fail").
        var unsaid = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "or": [{ "item.quantity": { "gt": 7 } }, { "item.quantity": { "in": [1, null] } }] } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10 } }]""");

        unsaid.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: true, ["item.quantity"], RenameLate: true));
        unsaid.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""
            { $match: { $or: [
                { Items: { $elemMatch: { $or: [ { Quantity: { $gt: 7 } }, { Quantity: { $in: [1, null] } } ] } } },
                { $nor: [ { $nor: [ {{ItemsHoldMemberless}} ] }, { $nor: [ { $or: [ { "__oxAbsent.Quantity": { $gt: 7 } }, { "__oxAbsent.Quantity": { $in: [1, null] } } ] } ] } ] },
                { $nor: [ {{ItemsNoValue}}, { $nor: [ { $or: [ { "Items.Quantity": { $gt: 7 } }, { "Items.Quantity": { $in: [1, null] } } ] } ] }, {{ItemsUndefined}} ] }
            ] } }
            """));

        // A page whose order is not the parents' cuts none: its filter only spares the unwind, and asks less of the rows that
        // fail. It drops an array of objects none of which passes; what holds no array of elements it leaves to the unwind
        // (which drops it, or answers the one value for the conditions after it), and where an absent member may pass, what is
        // or holds something without members. Its count still asks exactly, and bounds the parents.
        var byElement = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "or": [{ "item.quantity": { "gt": 7 } }, { "item.quantity": { "in": [1, null] } }] } }, { "sort": [{ "item.quantity": "asc" }] }, { "page": { "limit": 10, "count": true } }]""");

        byElement.Unwind.Should().BeEquivalentTo(new UnwindPlacement(0, ParentsFirst: false, ["item.quantity"], RenameLate: true));
        byElement.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""{ $match: { $or: [ { Items: { $elemMatch: { $or: [ { Quantity: { $gt: 7 } }, { Quantity: { $in: [1, null] } } ] } } }, {{ItemsNoArrayOfElements}}, {{ItemsOfAMemberlessKind}} ] } }"""));
        byElement.CountStages![1].ShouldBeBson(unsaid.PageStages[1]);
        byElement.CountParentsAt.Should().Be(2);

        // Where a condition after the unwind cannot be asked before it (one on the position), no parent is known to yield a
        // row: the count bounds its rows alone, and its filter spares the unwind as the page's does.
        var unbounded = await Compile("""[{ "unwind": { "path": "items", "as": "item", "includeIndex": "at" } }, { "match": { "item.quantity": { "neq": 2 }, "at": { "gte": 1 } } }, { "page": { "limit": 10, "count": true } }]""");

        unbounded.CountParentsAt.Should().BeNull();
        unbounded.CountStages![1].ShouldBeBson(BsonDocument.Parse($$"""{ $match: { $or: [ { Items: { $elemMatch: { Quantity: { $ne: 2 } } } }, {{ItemsNoArrayOfElements}}, {{ItemsOfAMemberlessKind}} ] } }"""));
        unbounded.CountStages![2].Contains("$unwind").Should().BeTrue();
        unbounded.PageStages[1].ShouldBeBson(unbounded.CountStages![1]);

        // A text that folds under the collation is one plain comparison, asked through the path. A prefix under the collation is
        // a range, two comparisons of the one member, which two elements of an array could meet one each: asked of one element.
        var folded = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.price.currency": { "eq": "eur" } } }, { "page": { "limit": 10 } }]""");
        var prefix = await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.price.currency": { "startsWith": "e" } } }, { "page": { "limit": 10 } }]""");

        folded.PageStages[1].ShouldBeBson(BsonDocument.Parse("""{ $match: { "Items.Price.Currency": "eur" } }"""));
        prefix.PageStages[1]["$match"]["$or"][0]["Items"]["$elemMatch"]["Price.Currency"].AsBsonDocument.Names.Should().Equal("$gte", "$lt");
        prefix.PageStages[1]["$match"]["$or"].AsBsonArray.Should().HaveCount(2, "no absent member passes a prefix: an object in the array, or the one value in its place");

        // Two unwinds, a group after the unwind, and contract 1 run as written.
        (await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "unwind": { "path": "item.stops", "as": "stop" } }, { "page": { "limit": 10 } }]""")).Unwind.Should().BeNull();
        (await Compile("""[{ "unwind": { "path": "items", "as": "item" } }, { "group": { "by": [{ "path": "item.quantity", "as": "q" }], "fields": { "n": { "count": true } } } }]""")).Unwind.Should().BeNull();
    }

    [Fact]
    public async Task A_grouped_page_is_ordered_by_its_keys()
    {
        var compiled = await Compile("""[{ "group": { "by": [{ "path": "state", "as": "st" }, { "path": "number", "as": "no" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } }]""");

        compiled.PageStages[^2].ShouldBeBson(BsonDocument.Parse("""{ $sort: { st: 1, no: 1 } }"""), "the keys are unique per group; an aggregate adds nothing to the order");
        compiled.PageStages.Should().NotContain(stage => stage.ToJson().Contains("__oxIx"), "a group replaces the row, so no unwind index survives it");
    }

    [Fact]
    public async Task A_page_of_groups_ordered_by_a_floating_point_aggregate_leaves_the_group_it_continues_from_out_by_its_keys()
    {
        // A sum of doubles is computed anew for every page: the last group's may come out a digit off the
        // value its cursor carries, and then lie after its own position. Its keys say it was served.
        const string Pipeline = """[{ "group": { "by": [{ "path": "state", "as": "st" }, { "path": "number", "as": "no" }], "fields": { "total": { "sum": "ratio" } } } }, { "sort": [{ "total": "desc" }] }, { "page": { "limit": 2 } }]""";
        var first = await Compile(Pipeline);
        var cursor = BindHost.Cursors.Encode(MongoQueryEngine.NextCursor(first, new BsonDocument { ["total"] = 0.30000000000000004, ["st"] = 1, ["no"] = BsonNull.Value }));
        var next = await Compile(Pipeline.Replace("""{ "limit": 2 }""", $$"""{ "limit": 2, "cursor": "{{cursor}}" }"""));

        first.Bound.PagingMode.Should().Be(PagingMode.Keyset);
        next.PageStages[^3].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $and: [
                { total: { $not: { $gt: 0.30000000000000004 } } },
                { $or: [ { total: 0.30000000000000004, st: 1, no: { $ne: null } }, { total: 0.30000000000000004, st: { $gt: 1 } }, { total: { $lt: 0.30000000000000004 } }, { total: { $numberDouble: "NaN" } }, { total: null } ] },
                { $nor: [ { st: 1, no: null } ] } ] } }
            """), "the range, the position by value (below every number stands the NaN, which no range reaches), and the served group by its keys");
        next.PageStages[^2].ShouldBeBson(BsonDocument.Parse("""{ $sort: { total: -1, st: 1, no: 1 } }"""));

        // A count is exact however often it is computed: its page continues by value alone, as before.
        const string Counted = """[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "sort": [{ "n": "desc" }] }, { "page": { "limit": 2 } }]""";
        var counted = await Compile(Counted);
        var after = BindHost.Cursors.Encode(MongoQueryEngine.NextCursor(counted, new BsonDocument { ["n"] = 3, ["st"] = 1 }));

        (await Compile(Counted.Replace("""{ "limit": 2 }""", $$"""{ "limit": 2, "cursor": "{{after}}" }"""))).PageStages[^3].ToJson().Should().NotContain("$nor");
    }

    [Fact]
    public async Task A_keyset_cursor_survives_a_projection_that_drops_the_key()
    {
        // Without the key the sort reads a member that is not there and the cursor is built from
        // null, which matches nothing: page two came back empty.
        var compiled = await Compile("""[{ "project": { "number": 1, "id": 0 } }, { "page": { "limit": 2 } }]""");

        compiled.Bound.PagingMode.Should().Be(PagingMode.Keyset);
        compiled.KeyKeptAgainstProjection.Should().BeTrue();
        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $sort: { _id: 1 } }"));
        compiled.PageStages[^1].ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 1 } }"));

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
                { $or: [ { "Addon.Preis": NumberDecimal("1.5") }, { "Addon.Preis": { $regularExpression: { pattern: "^1\\.50*\\z", options: "" } } } ] },
                { $or: [ { "Addon.Preis._v": NumberDecimal("1.5") }, { "Addon.Preis._v": { $regularExpression: { pattern: "^1\\.50*\\z", options: "" } } } ] } ] } }
            """));
    }

    private sealed class OneDefinition(string path, OxQL.Model.Addon.AddonKind kind) : OxQL.Model.Addon.IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<OxQL.Model.Addon.AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OxQL.Model.Addon.AddonDefinition>>([new OxQL.Model.Addon.AddonDefinition { Id = Guid.NewGuid(), Entity = entity, Path = path, Kind = kind }]);
    }
}
