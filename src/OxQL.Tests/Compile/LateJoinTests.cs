using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// Where a join is emitted: after the page when only the rows' display reads its alias, where
/// it was written when a later stage filters, orders, unwinds, groups or resolves through it,
/// and never in the count pipeline unless the count needs it.
/// </summary>
public class LateJoinTests
{
    private const string Order = "probe.order";
    private const string Customer = "probe.customer";
    private static readonly BsonBinaryData Org = new(BindHost.Organisation, GuidRepresentation.Standard);
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string OrdersLookup = """{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }""";
    private const string CustomerResolve = """{ "resolve": { "path": "customerId", "as": "cust" } }""";

    private static string OrgJson => $"{{ OrganizationId: {Org.ToJson()} }}";

    private static string OrdersLookupJson => $$"""{ $lookup: { from: "orders", localField: "_id", foreignField: "CustomerId", pipeline: [ { $match: {{OrgJson}} }, { $sort: { _id: 1 } }, { $limit: 101 }, { $project: { _id: 1, Number: 1 } } ], as: "orders" } }""";

    /// <summary>The lookup's cut to its limit and its truncation flag: it fetches one child more than it keeps.</summary>
    private const string OrdersCutJson = """{ $set: { __oxLk0: { $gt: [ { $size: "$orders" }, 100 ] }, orders: { $slice: [ "$orders", 100 ] } } }""";

    private static string CustomerLookupJson => $$"""{ $lookup: { from: "customers", localField: "CustomerId", foreignField: "_id", pipeline: [ { $match: {{OrgJson}} }, { $limit: 1 }, { $project: { _id: 1, Name: 1 } } ], as: "cust__arr" } }""";

    private static async Task<CompiledQuery> Compile(string pipeline, string entity = Order)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, entity, pipeline);

        return MongoCompiler.Compile(bound, Options);
    }

    private static void ShouldBe(IReadOnlyList<BsonDocument> actual, params string[] expected)
    {
        var expectedDocuments = expected.Select(BsonDocument.Parse).ToList();

        actual.Select(stage => stage.ToJson()).Should().Equal(expectedDocuments.Select(stage => stage.ToJson()));
    }

    private static IEnumerable<string> Kinds(IReadOnlyList<BsonDocument> stages) => stages.Select(stage => stage.GetElement(0).Name);

    // ---- moved -----------------------------------------------------------------------------

    [Fact]
    public async Task A_lookup_nothing_but_the_display_reads_runs_after_the_page()
    {
        var compiled = await Compile($$"""[{{OrdersLookup}}, { "sort": [{ "name": "asc" }] }, { "page": { "limit": 5, "count": true } }]""", Customer);

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            "{ $sort: { Name: 1, _id: 1 } }",
            "{ $limit: 6 }",
            OrdersLookupJson,
            OrdersCutJson);
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            "{ $limit: 1001 }",
            "{ $count: 'n' }");
    }

    [Fact]
    public async Task A_local_resolve_nothing_but_the_display_reads_runs_after_the_page()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "count": true } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            "{ $sort: { Number: 1, _id: 1 } }",
            "{ $limit: 6 }",
            CustomerLookupJson,
            """{ $set: { cust: { $arrayElemAt: [ "$cust__arr", 0 ] } } }""",
            """{ $unset: "cust__arr" }""");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            "{ $limit: 1001 }",
            "{ $count: 'n' }");
    }

    // ---- before an unwind: once per parent, never once per element ---------------------------

    [Fact]
    public async Task A_display_only_join_written_before_an_unwind_runs_on_the_parents_cut_before_it()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{{CustomerResolve}}, { "unwind": { "path": "items", "as": "item" } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5 } }]""");
        var compiled = MongoCompiler.Compile(bound, Options);

        // The parents with an item in the page's order, cut to the page's reach; the join, once per parent
        // kept; the unwind; the page; the rename. After the page the join would run once per element.
        Kinds(compiled.PageStages).Should().Equal("$match", "$match", "$sort", "$limit", "$lookup", "$set", "$unset", "$unwind", "$sort", "$limit", "$set", "$unset");
        compiled.PageStages[3].ShouldBeBson(BsonDocument.Parse("{ $limit: NumberLong(6) }"), "a page's worth of parents");
        MongoCompiler.PhaseOf(bound, 0, bound.Stages.OfType<BoundStage.Resolve>().Single()).Should().Be(JoinPhase.Parents);
        MongoCompiler.KeptBefore(bound, 0)!.Path.Wire.Should().Be("items");

        // As written (what the differential compares against) nothing is cut first: the join stays where it stands.
        var written = MongoCompiler.Compile(bound, Options with { AsWritten = true });

        Kinds(written.PageStages).Should().StartWith(["$match", "$lookup", "$set", "$unset", "$unwind"]);
    }

    [Fact]
    public async Task A_display_only_join_written_before_an_unwind_stays_where_it_was_written_when_the_parents_are_not_cut_first()
    {
        // Ordered by the element: every parent is unwound, so the join runs once per parent where it stands.
        var byElement = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{{CustomerResolve}}, { "unwind": { "path": "items", "as": "item" } }, { "sort": [{ "item.quantity": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(MongoCompiler.Compile(byElement, Options).PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$unwind", "$sort", "$limit", "$set", "$unset");
        MongoCompiler.PhaseOf(byElement, 0, byElement.Stages.OfType<BoundStage.Resolve>().Single()).Should().Be(JoinPhase.BeforePage);
        MongoCompiler.KeptBefore(byElement, 0)!.Path.Wire.Should().Be("items");

        // The same for a lookup whose projection names paths under its alias, and its count never runs the join.
        var narrowed = MongoCompiler.Compile(await BindHost.BoundAsync(OxQL.TestCases.LateJoinCases.Model, OxQL.TestCases.LateJoinCases.Parent, """
            [{ "match": { "name": { "eq": "x" } } }, { "lookup": { "from": "lj.child", "path": "parentId", "as": "children" } }, { "unwind": { "path": "tags", "as": "tag", "includeIndex": "at" } },
             { "project": { "name": 1, "tag": 1, "at": 1, "children.title": 1 } }, { "sort": [{ "tag": "desc" }] }, { "page": { "limit": 5, "count": true } }]
            """), Options);

        Kinds(narrowed.PageStages).Should().Equal("$match", "$match", "$lookup", "$set", "$unwind", "$sort", "$limit", "$set", "$unset", "$project");
        Kinds(narrowed.CountStages!).Should().Equal(["$match", "$match", "$match", "$limit", "$unwind", "$limit", "$count"], "only the display reads the alias; the parents that yield a row are bounded as the rows are");
    }

    [Fact]
    public async Task A_display_only_join_written_after_an_unwind_runs_after_the_page()
    {
        var compiled = await Compile($$"""[{ "unwind": { "path": "items", "as": "item" } }, {{CustomerResolve}}, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5 } }]""");

        // It was written to run once per element: the page holds no more rows than it would have read there.
        Kinds(compiled.PageStages).Should().Equal("$match", "$match", "$sort", "$limit", "$unwind", "$sort", "$limit", "$set", "$unset", "$lookup", "$set", "$unset");
    }

    [Fact]
    public async Task Under_contract_1_a_display_only_join_before_an_unwind_runs_after_the_page_as_it_did()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order,
            $$"""[{{CustomerResolve}}, { "unwind": { "path": "items", "as": "item" } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5 } }]""", BindHost.Context(contract: 1));
        var compiled = MongoCompiler.Compile(bound, Options with { AsWritten = true });

        Kinds(compiled.PageStages).Should().EndWith(["$limit", "$lookup", "$set", "$unset"]);
        MongoCompiler.PhaseOf(bound, 0, bound.Stages.OfType<BoundStage.Resolve>().Single(), asWritten: true).Should().Be(JoinPhase.AfterPage);
    }

    [Fact]
    public async Task A_display_only_join_runs_after_the_page_when_a_projection_passes_its_alias_whole()
    {
        var included = await Compile($$"""[{{OrdersLookup}}, { "project": { "id": 1, "name": 1, "orders": 1 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(included.PageStages).Should().Equal("$match", "$sort", "$limit", "$project", "$lookup", "$set");
        included.PageStages[3].ShouldBeBson(BsonDocument.Parse("{ $project: { Name: 1, orders: 1, _id: 1 } }"), "the projection cuts the page's rows; the join adds the alias to what it kept");

        var excludedElsewhere = await Compile($$"""[{{OrdersLookup}}, { "project": { "matchCode": 0 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(excludedElsewhere.PageStages).Should().Equal("$match", "$sort", "$limit", "$project", "$lookup", "$set");
    }

    [Fact]
    public async Task A_join_written_before_another_join_that_a_match_reads_runs_after_it()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "resolve": { "path": "customerId", "as": "buyer" } }, { "match": { "buyer.name": { "eq": "x" } } }, { "page": { "limit": 5 } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$match", "$sort", "$limit", "$lookup", "$set", "$unset");
        compiled.PageStages[1]["$lookup"]["as"].AsString.Should().Be("buyer__arr");
        compiled.PageStages[7]["$lookup"]["as"].AsString.Should().Be("cust__arr");
    }

    [Fact]
    public async Task A_projection_between_a_moved_resolve_and_the_page_keeps_the_resolve_key()
    {
        var included = await Compile($$"""[{{CustomerResolve}}, { "project": { "id": 1, "cust": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(included.PageStages).Should().Equal("$match", "$sort", "$limit", "$project", "$lookup", "$set", "$unset");
        included.PageStages[3].ShouldBeBson(BsonDocument.Parse("{ $project: { cust: 1, _id: 1, CustomerId: 1 } }"), "the join after the page reads the key off the row");

        var excluded = await Compile($$"""[{{CustomerResolve}}, { "project": { "customerId": 0, "number": 0 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(excluded.PageStages).Should().Equal("$match", "$sort", "$limit", "$project", "$lookup", "$set", "$unset");
        excluded.PageStages[3].ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 0 } }"), "an exclusion of the key is not emitted while a later join reads it");
    }

    // ---- not moved -------------------------------------------------------------------------

    [Fact]
    public async Task A_lookup_a_later_match_reads_stays_before_the_match_and_in_the_count()
    {
        var compiled = await Compile($$"""[{{OrdersLookup}}, { "match": { "orders.number": { "eq": "x" } } }, { "page": { "limit": 5, "count": true } }]""", Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$match", "$sort", "$limit");
        Kinds(compiled.CountStages!).Should().Equal("$match", "$lookup", "$set", "$match", "$limit", "$count");
    }

    [Fact]
    public async Task A_keyset_cursor_on_a_resolve_alias_is_evaluated_after_the_join_and_before_the_sort()
    {
        const string pipeline = """[{ "resolve": { "path": "customerId", "as": "cust" } }, { "sort": [{ "cust.name": "asc" }] }, { "page": { "limit": 2 } }]""";
        var first = await BindHost.BoundAsync(BindHost.Probe, Order, pipeline);
        var id = new BsonBinaryData(Id1, GuidRepresentation.Standard);
        var cursor = BindHost.Cursors.Encode(new CursorPayload(first.Fingerprint, PagingMode.Keyset, [new CursorValue("cust.name", true, new BsonString("abc")), new CursorValue("_id", true, id)], 0));
        var compiled = await Compile(pipeline.Replace("""{ "limit": 2 }""", $$"""{ "limit": 2, "cursor": "{{cursor}}" }"""));

        // Before the join the alias holds nothing, and a condition on it there drops every row
        // (ascending) or none (descending); after the join it reads what the cursor was minted from.
        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            CustomerLookupJson,
            """{ $set: { cust: { $arrayElemAt: ["$cust__arr", 0] } } }""",
            """{ $unset: "cust__arr" }""",
            $$"""{ $match: { $and: [ { "cust.Name": { $gte: "abc" } }, { $or: [ { "cust.Name": "abc", _id: { $gt: {{id.ToJson()}} } }, { "cust.Name": { $gt: "abc" } } ] } ] } }""",
            """{ $sort: { "cust.Name": 1, _id: 1 } }""",
            "{ $limit: 3 }");
    }

    [Fact]
    public async Task A_resolve_alias_sorted_on_and_dropped_by_the_projection_stays_in_storage_and_out_of_the_row()
    {
        const string pipeline = """[{ "resolve": { "path": "customerId", "as": "cust" } }, { "sort": [{ "cust.name": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 2 } }]""";
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, pipeline);
        var compiled = MongoCompiler.Compile(bound, Options);

        compiled.PageStages.Should().Contain(stage => stage.Contains("$project") && stage["$project"].AsBsonDocument.Contains("cust.Name"), "the cursor reads the sort path off the last row");

        var row = WireEncoder.Encode(new BsonDocument { ["_id"] = new BsonBinaryData(Id1, GuidRepresentation.Standard), ["cust"] = new BsonDocument("Name", "Alice") }, bound);

        row.Select(pair => pair.Key).Should().Equal(["id"], "the projection is the caller's; the sort path it kept is paging's");
    }

    [Fact]
    public async Task A_resolve_a_later_sort_reads_stays_before_the_sort()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "sort": [{ "cust.name": "asc" }] }, { "page": { "limit": 5, "count": true } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$sort", "$limit");
        Kinds(compiled.CountStages!).Should().Equal(["$match", "$limit", "$count"], "the sort is not in the count, so the count does not need the join");

        // A path under a lookup alias is an array element and cannot order rows, so the
        // sorted-on case is a resolve's alone.
        (await BindHost.ErrorAsync(BindHost.Probe, Customer, $$"""[{{OrdersLookup}}, { "sort": [{ "orders.number": "asc" }] }]""", Codes.NotSortable)).Path.Should().Be("orders.number");
    }

    [Fact]
    public async Task An_unwind_of_a_lookup_alias_under_its_own_as_addresses_the_child_under_that_name()
    {
        var pipeline = $$"""[{{OrdersLookup}}, { "unwind": { "path": "orders", "as": "order" } }, { "match": { "order.number": { "eq": "n-1" } } }, { "project": { "id": 1, "order.number": 1 } }, { "page": { "limit": 5 } }]""";
        var bound = await BindHost.BoundAsync(BindHost.Probe, Customer, pipeline);
        var compiled = MongoCompiler.Compile(bound, Options);

        // The parents with a child that passes the match, in key order, cut to the page's reach; the unwind; the
        // match on the child where the unwind left it; the page; then the child under the name the unwind writes.
        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$match", "$sort", "$limit", "$unwind", "$match", "$sort", "$limit", "$set", "$unset", "$project");
        compiled.PageStages[3].ShouldBeBson(BsonDocument.Parse("""{ $match: { "orders.Number": "n-1" } }"""), "a parent no child of which passes is not unwound");
        compiled.PageStages[7]["$match"].AsBsonDocument.Names.Should().Equal(["orders.Number"], "the child's member where the unwind left the child");
        compiled.PageStages[10].ShouldBeBson(BsonDocument.Parse("""{ $set: { order: "$orders" } }"""));

        var written = MongoCompiler.Compile(bound, Options with { AsWritten = true });

        Kinds(written.PageStages).Should().Equal("$match", "$lookup", "$set", "$unwind", "$set", "$unset", "$match", "$project", "$sort", "$limit");
        written.PageStages[6]["$match"].AsBsonDocument.Names.Should().Equal(["order.Number"], "as written, the match reads the child under the name the unwind wrote");

        var row = WireEncoder.Encode(new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Id1, GuidRepresentation.Standard),
            ["orders"] = new BsonDocument("Number", "n-1"),
            ["order"] = new BsonDocument("Number", "n-1"),
        }, bound);

        row.Select(pair => pair.Key).Should().Equal(["id", "order"], "the projection kept the element under its as and dropped the lookup alias");
        row["order"]!["number"]!.GetValue<string>().Should().Be("n-1");
    }

    [Fact]
    public async Task A_lookup_a_later_unwind_reads_stays_before_the_unwind()
    {
        var compiled = await Compile($$"""[{{OrdersLookup}}, { "unwind": { "path": "orders", "as": "order" } }, { "page": { "limit": 5, "count": true } }]""", Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$match", "$sort", "$limit", "$unwind", "$sort", "$limit", "$set", "$unset");
        Kinds(compiled.CountStages!).Should().Equal(["$match", "$lookup", "$set", "$match", "$limit", "$unwind", "$limit", "$count"], "the count unwinds and counts: it orders and renames nothing, and bounds the parents that yield a row as it bounds the rows");
    }

    [Fact]
    public async Task A_join_before_a_group_stays_where_it_was_written()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "group": { "by": [{ "path": "cust.name", "as": "customer" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$group", "$project", "$sort", "$limit");
    }

    [Fact]
    public async Task A_join_a_group_does_not_read_is_not_run()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } }]""");

        Kinds(compiled.PageStages).Should().Equal(["$match", "$group", "$project", "$sort", "$limit"], "the group replaces the row, and nothing in it reads the alias");
    }

    [Fact]
    public async Task A_join_a_later_resolve_reads_stays_before_the_resolve()
    {
        // A resolve through the lookup's array needs 'elements' (RESOLVE_ON_COLLECTION); through
        // its first child it reads one row, as the plain form of this case means.
        var compiled = await Compile("""
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "first": true } },
             { "resolve": { "path": "orders.customerId", "as": "again" } }, { "page": { "limit": 5 } }]
            """, Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$sort", "$limit", "$lookup", "$set", "$unset");
        compiled.PageStages[1]["$lookup"]["as"].AsString.Should().Be("orders", "the lookup that is read stays before the page");
        compiled.PageStages[5]["$lookup"]["as"].AsString.Should().Be("again__arr", "the resolve nothing reads runs after it");
    }

    [Fact]
    public async Task A_join_runs_after_a_projection_that_names_paths_under_its_alias_and_stays_before_one_that_cuts_it_or_a_stage_that_reads_it()
    {
        // A contract 2 join has no select: the projection names what the alias shows, the join loads
        // those paths with its key, and it still runs for the page's rows only.
        var narrowed = await Compile($$"""[{{OrdersLookup}}, { "project": { "id": 1, "orders.number": 1 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(narrowed.PageStages).Should().Equal("$match", "$sort", "$limit", "$project", "$lookup", "$set");
        narrowed.PageStages[^2]["$lookup"]["pipeline"].AsBsonArray[^1].ShouldBeBson(BsonDocument.Parse("{ $project: { _id: 1, Number: 1 } }"), "the late join loads what the projection names, and its key");

        // An exclusion under the alias cuts what the join produced: the join stays before it.
        var cut = await Compile($$"""[{{OrdersLookup}}, { "project": { "orders.number": 0 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(cut.PageStages).Should().StartWith(["$match", "$lookup"]);

        // Contract 1 keeps its rule: its join fetches the select as written, and a projection that narrows the alias cuts it.
        var legacy = MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number"] } }, { "project": { "id": 1, "orders.number": 1 } }, { "page": { "limit": 5 } }]""",
            BindHost.Context(contract: 1)), Options);

        Kinds(legacy.PageStages).Should().StartWith(["$match", "$lookup"]);

        var read = await Compile($$"""[{{OrdersLookup}}, { "match": { "orders.number": { "eq": "n-1" } } }, { "project": { "id": 1, "name": 1 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(read.PageStages).Should().Equal(["$match", "$lookup", "$set", "$match", "$sort", "$limit", "$project"], "a match reads the alias before the projection drops it");
    }

    [Fact]
    public async Task A_join_whose_alias_a_projection_drops_and_nothing_reads_is_not_run()
    {
        foreach (var (entity, join, projection) in new[]
        {
            (Customer, OrdersLookup, """{ "project": { "orders": 0 } }"""),
            (Customer, OrdersLookup, """{ "project": { "id": 1, "name": 1 } }"""),
            (Order, CustomerResolve, """{ "project": { "id": 1, "number": 1 } }"""),
        })
        {
            var compiled = await Compile($$"""[{{join}}, {{projection}}, { "page": { "limit": 5, "count": true } }]""", entity);

            Kinds(compiled.PageStages).Should().Equal(["$match", "$sort", "$limit", "$project"], $"{join} then {projection}: the row does not carry the alias");
            Kinds(compiled.CountStages!).Should().Equal(["$match", "$limit", "$count"], "a projection that runs after the page is not in the count: it changes no row");
        }
    }

    [Fact]
    public async Task A_remote_resolve_emits_no_stage_either_way()
    {
        var compiled = await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$sort", "$limit");
        compiled.KeyedResolves.Should().ContainSingle();
    }

    // ---- the rows and the cursor -----------------------------------------------------------

    private static BsonDocument CustomerRow(Guid id, string name) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["Name"] = name,
        ["OrganizationId"] = Org,
        ["orders"] = new BsonArray
        {
            new BsonDocument { ["_id"] = new BsonBinaryData(Id2, GuidRepresentation.Standard), ["Number"] = "O-1" },
        },
    };

    private static async Task<QueryResult> Run(FakeAggregateRunner runner, string pipeline)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());
        var outcome = await engine.ExecuteAsync(BindHost.Request(Customer, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    [Fact]
    public async Task The_rows_of_a_moved_join_are_encoded_exactly_like_those_of_one_that_stayed()
    {
        var moved = await Run(new FakeAggregateRunner { PageRows = [CustomerRow(Id1, "A")] }, $$"""[{{OrdersLookup}}, { "sort": [{ "name": "asc" }] }, { "page": { "limit": 5 } }]""");
        var stayed = await Run(new FakeAggregateRunner { PageRows = [CustomerRow(Id1, "A")] }, $$"""[{{OrdersLookup}}, { "match": { "orders.number": { "exists": true } } }, { "sort": [{ "name": "asc" }] }, { "page": { "limit": 5 } }]""");

        moved.Items.Should().ContainSingle();
        moved.Items[0]!.ToJsonString().Should().Be(stayed.Items[0]!.ToJsonString());
        moved.Items[0]!["orders"]![0]!["number"]!.GetValue<string>().Should().Be("O-1");
    }

    [Fact]
    public async Task The_keyset_predicate_of_the_next_page_does_not_change_when_the_join_moves()
    {
        var runner = new FakeAggregateRunner { PageRows = [CustomerRow(Id1, "A"), CustomerRow(Id2, "B")] };
        var first = await Run(runner, $$"""[{{OrdersLookup}}, { "sort": [{ "name": "asc" }] }, { "page": { "limit": 1 } }]""");

        first.PageInfo.NextCursor.Should().NotBeNull();

        var withJoin = await Compile($$"""[{{OrdersLookup}}, { "sort": [{ "name": "asc" }] }, { "page": { "limit": 1, "cursor": "{{first.PageInfo.NextCursor}}" } }]""", Customer);

        Kinds(withJoin.PageStages).Should().Equal("$match", "$sort", "$limit", "$lookup", "$set");
        withJoin.PageStages[0]["$match"].AsBsonDocument.Contains("$and").Should().BeTrue("the cursor predicate is merged into the leading match");
        withJoin.PageStages[0]["$match"]["$and"].AsBsonArray[1].AsBsonDocument.ToJson().Should().Contain("Name", "the predicate reads the sort leg off the row, which no join touches");
    }
}
