using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// The lookup as it is emitted under OxQL 2.1 (DESIGN §3.4.2): the caller's sort completed by the
/// child's key, one child more than the limit and a <c>$set</c> that flags the cut and slices the
/// array to the limit (<c>LOOKUP_TRUNCATED</c> instead of a silent cut), <c>first</c> as one child
/// taken out of the array, and <c>on</c> joining under the parent alias without joining the
/// children of a parent the row does not hold.
/// </summary>
public class LookupMembersCompileTests
{
    private const string Order = "probe.order";
    private const string Customer = "probe.customer";
    private static readonly BsonBinaryData Org = new(BindHost.Organisation, GuidRepresentation.Standard);
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private const string CustomerResolve = """{ "resolve": { "path": "customerId", "as": "cust" } }""";

    private static string OrgJson => $"{{ OrganizationId: {Org.ToJson()} }}";

    private static async Task<CompiledQuery> Compile(string pipeline, string entity)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, entity, pipeline);

        return MongoCompiler.Compile(bound, Options);
    }

    private static IEnumerable<string> Kinds(IReadOnlyList<BsonDocument> stages) => stages.Select(stage => stage.GetElement(0).Name);

    private static BsonDocument LookupOf(CompiledQuery compiled, string alias) =>
        compiled.PageStages.Where(stage => stage.Contains("$lookup")).Select(stage => stage["$lookup"].AsBsonDocument).Single(join => join["as"] == alias);

    [Fact]
    public async Task A_sorted_lookup_orders_the_children_by_the_caller_then_the_key_and_fetches_one_over_the_limit()
    {
        var compiled = await Compile("""
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number"], "limit": 2, "sort": [{ "when": "desc" }] } },
             { "match": { "orders.number": { "eq": "x" } } }]
            """, Customer);

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""
            { $lookup: { from: "orders", localField: "_id", foreignField: "CustomerId",
                pipeline: [ { $match: {{OrgJson}} }, { $sort: { When: -1, _id: 1 } }, { $limit: 3 }, { $project: { _id: 1, Number: 1 } } ],
                as: "orders" } }
            """));
        compiled.PageStages[2].ShouldBeBson(BsonDocument.Parse("""{ $set: { __oxLk0: { $gt: [ { $size: "$orders" }, 2 ] }, orders: { $slice: [ "$orders", 2 ] } } }"""));
        compiled.LookupFlags.Should().Equal(new LookupFlag("__oxLk0", 0, "orders", 2));
    }

    [Fact]
    public async Task A_sort_that_names_the_key_is_not_completed_twice()
    {
        var compiled = await Compile("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "id": "desc" }] } }]""", Customer);

        LookupOf(compiled, "orders")["pipeline"][1].ShouldBeBson(BsonDocument.Parse("{ $sort: { _id: -1 } }"));
    }

    [Fact]
    public async Task First_takes_one_child_out_of_the_array_and_carries_no_flag()
    {
        var compiled = await Compile("""
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "latest", "first": true, "sort": [{ "when": "desc" }], "select": ["number"] } },
             { "match": { "latest.number": { "eq": "x" } } }]
            """, Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$match", "$sort", "$limit");
        LookupOf(compiled, "latest")["pipeline"].AsBsonArray.Skip(1).Select(stage => stage.ToJson()).Should().Equal(
            BsonDocument.Parse("{ $sort: { When: -1, _id: 1 } }").ToJson(),
            BsonDocument.Parse("{ $limit: 1 }").ToJson(),
            BsonDocument.Parse("{ $project: { _id: 1, Number: 1 } }").ToJson());
        compiled.PageStages[2].ShouldBeBson(BsonDocument.Parse("""{ $set: { latest: { $arrayElemAt: [ "$latest", 0 ] } } }"""));
        compiled.PageStages[3].ShouldBeBson(BsonDocument.Parse("""{ $match: { "latest.Number": "x" } }"""), "the child is one row under the alias");
        compiled.LookupFlags.Should().BeEmpty();
    }

    [Fact]
    public async Task On_joins_under_the_alias_and_joins_nothing_where_the_row_holds_no_parent()
    {
        var compiled = await Compile($$"""
            [{{CustomerResolve}},
             { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "siblings", "select": ["number"] } },
             { "match": { "siblings.number": { "eq": "x" } } }]
            """, Order);

        LookupOf(compiled, "siblings").ShouldBeBson(BsonDocument.Parse($$"""
            { from: "orders", localField: "cust._id", foreignField: "CustomerId", let: { oxParent: "$cust._id" },
              pipeline: [ { $match: {{OrgJson}} }, { $match: { $expr: { $gt: [ "$$oxParent", null ] } } }, { $sort: { _id: 1 } }, { $limit: 101 }, { $project: { _id: 1, Number: 1 } } ],
              as: "siblings" }
            """));
        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$lookup", "$set", "$match", "$sort", "$limit");
    }

    [Fact]
    public async Task On_puts_the_parent_variable_in_let_before_the_pipeline()
    {
        var compiled = await Compile($$"""
            [{ "match": { "number": { "eq": "x" } } }, {{CustomerResolve}},
             { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "siblings" } }]
            """, Order);

        LookupOf(compiled, "siblings").Names.Should().Equal("from", "localField", "foreignField", "let", "pipeline", "as");
        LookupOf(compiled, "siblings")["let"].AsBsonDocument.Names.Should().Contain("oxParent");
    }

    [Fact]
    public async Task A_resolve_and_a_lookup_on_its_alias_both_run_after_the_page_when_only_the_display_reads_them()
    {
        var compiled = await Compile($$"""
            [{{CustomerResolve}}, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "siblings", "first": true } },
             { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }]
            """, Order);

        Kinds(compiled.PageStages).Should().Equal("$match", "$sort", "$limit", "$lookup", "$set", "$unset", "$lookup", "$set");
        compiled.PageStages[3]["$lookup"]["as"].AsString.Should().Be("cust__arr", "the late joins run in stage order, the parent first");
        compiled.PageStages[6]["$lookup"]["as"].AsString.Should().Be("siblings");
        Kinds(compiled.CountStages!).Should().Equal("$match", "$limit", "$count");
    }

    [Fact]
    public async Task A_lookup_on_an_alias_a_later_stage_reads_holds_the_parent_join_before_the_page()
    {
        var compiled = await Compile($$"""
            [{{CustomerResolve}}, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "siblings", "first": true } },
             { "match": { "siblings.number": { "eq": "x" } } }, { "page": { "limit": 5 } }]
            """, Order);

        Kinds(compiled.PageStages).Should().Equal("$match", "$lookup", "$set", "$unset", "$lookup", "$set", "$match", "$sort", "$limit");
    }

    [Fact]
    public async Task A_lookup_on_an_alias_keeps_the_parent_join_that_nothing_else_reads()
    {
        var compiled = await Compile($$"""
            [{{CustomerResolve}}, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "siblings" } },
             { "project": { "number": 1, "siblings": 1 } }, { "page": { "limit": 5 } }]
            """, Order);

        compiled.PageStages.Count(stage => stage.Contains("$lookup")).Should().Be(2, "the resolve the row no longer shows is still the lookup's parent");
    }

    [Fact]
    public async Task The_flag_survives_an_inclusion_projection_and_a_group()
    {
        var projected = await Compile("""
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "match": { "orders.number": { "eq": "x" } } },
             { "project": { "name": 1, "orders": 1 } }]
            """, Customer);

        projected.PageStages.Single(stage => stage.Contains("$project"))["$project"].AsBsonDocument.Names.Should().Contain("__oxLk0");

        var grouped = await Compile("""
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "unwind": { "path": "orders" } },
             { "group": { "by": [{ "path": "name", "as": "name" }], "fields": { "n": { "count": true } } } }]
            """, Customer);
        var group = grouped.PageStages.Single(stage => stage.Contains("$group"))["$group"].AsBsonDocument;

        group["__oxLk0"].ShouldBeBson(BsonDocument.Parse("{ $max: '$__oxLk0' }"), "a group of rows a cut reached is reached by it");
    }

    [Fact]
    public async Task A_late_lookup_is_not_kept_by_a_projection_that_runs_before_it()
    {
        var compiled = await Compile("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "project": { "name": 1, "orders": 1 } }, { "page": { "limit": 5 } }]""", Customer);

        Kinds(compiled.PageStages).Should().Equal("$match", "$project", "$sort", "$limit", "$lookup", "$set");
        compiled.PageStages[1]["$project"].AsBsonDocument.Names.Should().NotContain("__oxLk0");
        compiled.LookupFlags.Should().ContainSingle();
    }

    [Fact]
    public void LookupTruncations_counts_the_flagged_rows_and_removes_every_flag()
    {
        var compiled = new CompiledQuery
        {
            Bound = null!,
            PageStages = [],
            Limit = 3,
            Offset = 0,
            PagingMode = PagingMode.Keyset,
            IncludeTotalCount = false,
            SortFields = [],
            KeyedResolves = [],
            SemiJoins = [],
            MaxTimeMs = 1,
            CountCap = 1,
            LookupFlags = [new LookupFlag("__oxLk0", 2, "orders", 5), new LookupFlag("__oxLk1", 4, "notes", 1)],
        };
        var page = new List<BsonDocument>
        {
            new() { ["_id"] = 1, ["__oxLk0"] = true, ["__oxLk1"] = false },
            new() { ["_id"] = 2, ["__oxLk0"] = true },
            new() { ["_id"] = 3, ["__oxLk0"] = false, ["__oxLk1"] = BsonNull.Value },
        };

        var diagnostics = MongoCompiler.LookupTruncations(compiled, page);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Code.Should().Be(Codes.LookupTruncated);
        diagnostic.Stage.Should().Be(2);
        diagnostic.Path.Should().Be("orders");
        diagnostic.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["alias"] = "orders", ["limit"] = 5, ["rows"] = 2 });
        page.Should().AllSatisfy(row => row.Names.Should().Equal("_id"));
    }
}
