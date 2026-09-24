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

    private const string OrdersLookup = """{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number"] } }""";
    private const string CustomerResolve = """{ "resolve": { "path": "customerId", "as": "cust", "select": ["name"] } }""";

    private static string OrgJson => $"{{ OrganizationId: {Org.ToJson()} }}";

    private static string OrdersLookupJson => $$"""{ $lookup: { from: "orders", localField: "_id", foreignField: "CustomerId", pipeline: [ { $match: {{OrgJson}} }, { $sort: { _id: 1 } }, { $limit: 100 }, { $project: { _id: 1, Number: 1 } } ], as: "orders" } }""";

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
        var compiled = await Compile($$"""[{{OrdersLookup}}, { "sort": [{ "name": "asc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }]""", Customer);

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            "{ $sort: { Name: 1, _id: 1 } }",
            "{ $limit: 6 }",
            OrdersLookupJson);
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            "{ $limit: 100001 }",
            "{ $count: 'n' }");
    }

    [Fact]
    public async Task A_local_resolve_nothing_but_the_display_reads_runs_after_the_page()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }]""");

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            "{ $sort: { Number: 1, _id: 1 } }",
            "{ $limit: 6 }",
            CustomerLookupJson,
            """{ $set: { cust: { $arrayElemAt: [ "$cust__arr", 0 ] } } }""",
            """{ $unset: "cust__arr" }""");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            "{ $limit: 100001 }",
            "{ $count: 'n' }");
    }

    [Fact]
    public async Task A_display_only_join_runs_after_an_unwind_of_another_path_and_its_offset_page()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "unwind": { "path": "items" } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "offset": 10 } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$unwind", "$sort", "$unset", "$skip", "$limit", "$lookup", "$set", "$unset");
    }

    [Fact]
    public async Task A_display_only_join_runs_after_the_page_when_a_projection_passes_its_alias_whole()
    {
        var included = await Compile($$"""[{{OrdersLookup}}, { "project": { "id": 1, "name": 1, "orders": 1 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(included.PageStages).Should().Equal("$match", "$project", "$sort", "$limit", "$lookup");
        included.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $project: { Name: 1, orders: 1, _id: 1 } }"), "the projection is emitted as written; the join adds the alias to what it kept");

        var excludedElsewhere = await Compile($$"""[{{OrdersLookup}}, { "project": { "matchCode": 0 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(excludedElsewhere.PageStages).Should().Equal("$match", "$project", "$sort", "$limit", "$lookup");
    }

    [Fact]
    public async Task A_join_written_before_another_join_that_a_match_reads_runs_after_it()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "resolve": { "path": "customerId", "as": "buyer", "select": ["name"] } }, { "match": { "buyer.name": { "eq": "x" } } }, { "page": { "limit": 5 } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$match", "$sort", "$limit", "$lookup", "$set", "$unset");
        compiled.PageStages[1]["$lookup"]["as"].AsString.Should().Be("buyer__arr");
        compiled.PageStages[7]["$lookup"]["as"].AsString.Should().Be("cust__arr");
    }

    [Fact]
    public async Task A_projection_between_a_moved_resolve_and_the_page_keeps_the_resolve_key()
    {
        var included = await Compile($$"""[{{CustomerResolve}}, { "project": { "id": 1, "cust": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(included.PageStages).Should().Equal("$match", "$project", "$sort", "$limit", "$lookup", "$set", "$unset");
        included.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $project: { cust: 1, _id: 1, CustomerId: 1 } }"), "the join after the page reads the key off the row");

        var excluded = await Compile($$"""[{{CustomerResolve}}, { "project": { "customerId": 0, "number": 0 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(excluded.PageStages).Should().Equal("$match", "$project", "$sort", "$limit", "$lookup", "$set", "$unset");
        excluded.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $project: { Number: 0 } }"), "an exclusion of the key is not emitted while a later join reads it");
    }

    // ---- not moved -------------------------------------------------------------------------

    [Fact]
    public async Task A_lookup_a_later_match_reads_stays_before_the_match_and_in_the_count()
    {
        var compiled = await Compile($$"""[{{OrdersLookup}}, { "match": { "orders.number": { "eq": "x" } } }, { "page": { "limit": 5, "includeTotalCount": true } }]""", Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$match", "$sort", "$limit");
        Kinds(compiled.CountStages!).Should().Equal("$match", "$lookup", "$match", "$limit", "$count");
    }

    [Fact]
    public async Task A_keyset_cursor_on_a_resolve_alias_is_evaluated_after_the_join_and_before_the_sort()
    {
        const string pipeline = """[{ "resolve": { "path": "customerId", "as": "cust", "select": ["name"] } }, { "sort": [{ "cust.name": "asc" }] }, { "page": { "limit": 2 } }]""";
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
            $$"""{ $match: { $or: [ { "cust.Name": "abc", _id: { $gt: {{id.ToJson()}} } }, { "cust.Name": { $gt: "abc" } } ] } }""",
            """{ $sort: { "cust.Name": 1, _id: 1 } }""",
            "{ $limit: 3 }");
    }

    [Fact]
    public async Task A_resolve_a_later_sort_reads_stays_before_the_sort()
    {
        var compiled = await Compile($$"""[{{CustomerResolve}}, { "sort": [{ "cust.name": "asc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$sort", "$limit");
        Kinds(compiled.CountStages!).Should().Equal(["$match", "$limit", "$count"], "the sort is not in the count, so the count does not need the join");

        // A path under a lookup alias is an array element and cannot order rows, so the
        // sorted-on case is a resolve's alone.
        (await BindHost.ErrorAsync(BindHost.Probe, Customer, $$"""[{{OrdersLookup}}, { "sort": [{ "orders.number": "asc" }] }]""", Codes.NotSortable)).Path.Should().Be("orders.number");
    }

    [Fact]
    public async Task A_lookup_a_later_unwind_reads_stays_before_the_unwind()
    {
        var compiled = await Compile($$"""[{{OrdersLookup}}, { "unwind": { "path": "orders" } }, { "page": { "limit": 5, "includeTotalCount": true } }]""", Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$unwind", "$sort", "$limit");
        Kinds(compiled.CountStages!).Should().Equal("$match", "$lookup", "$unwind", "$limit", "$count");
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
        var compiled = await Compile($$"""[{{OrdersLookup}}, { "resolve": { "path": "orders.customerId", "as": "again", "select": ["name"] } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$sort", "$limit", "$lookup", "$set", "$unset");
        compiled.PageStages[1]["$lookup"]["as"].AsString.Should().Be("orders", "the lookup that is read stays before the page");
        compiled.PageStages[4]["$lookup"]["as"].AsString.Should().Be("again__arr", "the resolve nothing reads runs after it");
    }

    [Fact]
    public async Task A_join_stays_before_a_projection_that_narrows_or_drops_its_alias()
    {
        var narrowed = await Compile($$"""[{{OrdersLookup}}, { "project": { "id": 1, "orders.number": 1 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(narrowed.PageStages).Should().Equal("$match", "$lookup", "$project", "$sort", "$limit");

        var read = await Compile($$"""[{{OrdersLookup}}, { "match": { "orders.number": { "eq": "n-1" } } }, { "project": { "id": 1, "name": 1 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(read.PageStages).Should().Equal(["$match", "$lookup", "$match", "$project", "$sort", "$limit"], "a match reads the alias before the projection drops it");
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
            var compiled = await Compile($$"""[{{join}}, {{projection}}, { "page": { "limit": 5, "includeTotalCount": true } }]""", entity);

            Kinds(compiled.PageStages).Should().Equal(["$match", "$project", "$sort", "$limit"], $"{join} then {projection}: the row does not carry the alias");
            Kinds(compiled.CountStages!).Should().Equal("$match", "$project", "$limit", "$count");
        }
    }

    [Fact]
    public async Task A_remote_resolve_emits_no_stage_either_way()
    {
        var compiled = await Compile("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(compiled.PageStages).Should().Equal("$match", "$sort", "$limit");
        compiled.RemoteResolves.Should().ContainSingle();
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

        Kinds(withJoin.PageStages).Should().Equal("$match", "$sort", "$limit", "$lookup");
        withJoin.PageStages[0]["$match"].AsBsonDocument.Contains("$and").Should().BeTrue("the cursor predicate is merged into the leading match");
        withJoin.PageStages[0]["$match"]["$and"].AsBsonArray[1].AsBsonDocument.ToJson().Should().Contain("Name", "the predicate reads the sort leg off the row, which no join touches");
    }
}
