using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// What a request that ends in <c>values</c> sends the database (DG2 set filters): one aggregate that
/// reads at most <c>scan</c> of the rows that reach the stage, groups them by the path, and answers
/// one document with the number of rows read and the list of values. The bound on the rows stands
/// before everything the stage adds, so no value costs more than that many rows; the search runs on
/// the groups; the order is the stage's own; nothing is paged.
/// </summary>
public class ValuesCompileTests
{
    private const string Order = "probe.order";
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static async Task<CompiledQuery> Compile(string pipeline, string entity = Order, bool resolve = false) =>
        MongoCompiler.Compile(await BindHost.BoundAsync(resolve ? ResolveModel.Model : BindHost.Probe, entity, pipeline), Options);

    private static List<string> Names(CompiledQuery compiled) => compiled.PageStages.Select(stage => stage.GetElement(0).Name).ToList();

    /// <summary>The stages after the scope: what the values stage itself emits when nothing stands before it.</summary>
    private static string Emitted(CompiledQuery compiled) => new BsonArray(compiled.PageStages.Skip(1)).ToJson();

    private static string Json(string stages) => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(stages).ToJson();

    [Fact]
    public async Task The_values_of_a_member_are_one_group_behind_the_bound_on_the_rows_and_one_document()
    {
        var compiled = await Compile("""[{ "values": { "path": "state", "limit": 20, "scan": 300 } }]""");

        Emitted(compiled).Should().Be(Json("""
            [ { $limit: 300 },
              { $group: { _id: "$State", c: { $sum: 1 } } },
              { $facet: { t: [ { $group: { _id: null, r: { $sum: "$c" } } } ],
                          v: [ { $sort: { _id: 1 } }, { $limit: 21 } ] } } ]
            """));
        compiled.PageStages[0].Contains("$match").Should().BeTrue("the organisation's scope leads, as in every request");
        compiled.CountStages.Should().BeNull("nothing is counted beside it");
        compiled.Collation.Should().BeNull("an enum folds nothing");
        compiled.SortFields.Should().BeEmpty();
    }

    [Fact]
    public async Task With_counts_the_values_lead_by_the_number_of_rows_then_by_value()
    {
        var compiled = await Compile("""[{ "values": { "path": "number", "counts": true } }]""");

        Emitted(compiled).Should().Be(Json("""
            [ { $limit: 50000 },
              { $group: { _id: "$Number", c: { $sum: 1 } } },
              { $facet: { t: [ { $group: { _id: null, r: { $sum: "$c" } } } ],
                          v: [ { $sort: { c: -1, _id: 1 } }, { $limit: 101 } ] } } ]
            """));
        compiled.Collation!.ToJson().Should().Be(BsonDocument.Parse("{ locale: 'de', strength: 1 }").ToJson(), "a string folds into its group and orders under the collation");
    }

    [Fact]
    public async Task The_search_runs_on_the_values_as_a_range_under_the_collation_and_never_on_the_rows()
    {
        var compiled = await Compile("""[{ "values": { "path": "number", "search": "Mü", "limit": 5 } }]""");
        var facet = compiled.PageStages[^1]["$facet"].AsBsonDocument;

        facet["v"].AsBsonArray[0].ToJson().Should().Be(BsonDocument.Parse("{ $match: { _id: { $gte: 'Mü', $lt: 'Mü￿' } } }").ToJson(),
            "a prefix under the collation is the range from the text to the text followed by the highest character: case and accents fold as they do in startsWith");
        Names(compiled).Should().Equal("$match", "$limit", "$group", "$facet");
        facet["t"].ToJson().Should().NotContain("$match", "the rows read are counted whatever the search keeps");
    }

    [Fact]
    public async Task A_collection_above_the_path_is_unwound_keeping_the_row_without_elements_and_one_element_counts_the_row()
    {
        var one = await Compile("""[{ "values": { "path": "items.quantity", "counts": true, "limit": 3, "scan": 10 } }]""");

        Emitted(one).Should().Be(Json("""
            [ { $limit: 10 },
              { $unwind: { path: "$Items", includeArrayIndex: "__oxVal0", preserveNullAndEmptyArrays: true } },
              { $group: { _id: "$Items.Quantity", c: { $sum: 1 }, r: { $sum: { $cond: [ { $in: [ "$__oxVal0", [ 0, null ] ] }, 1, 0 ] } } } },
              { $facet: { t: [ { $group: { _id: null, r: { $sum: "$r" } } } ],
                          v: [ { $sort: { c: -1, _id: 1 } }, { $limit: 4 } ] } } ]
            """));

        var two = await Compile("""[{ "values": { "path": "items.stops.city" } }]""");

        Names(two).Should().Equal("$match", "$limit", "$unwind", "$unwind", "$group", "$facet");
        two.PageStages[2]["$unwind"]["path"].AsString.Should().Be("$Items");
        two.PageStages[3]["$unwind"]["path"].AsString.Should().Be("$Items.Stops");
        two.PageStages[4]["$group"]["r"].ToJson().Should().Be(BsonDocument.Parse("""
            { x: { $sum: { $cond: [ { $and: [ { $in: [ "$__oxVal0", [ 0, null ] ] }, { $in: [ "$__oxVal1", [ 0, null ] ] } ] }, 1, 0 ] } } }
            """)["x"].ToJson(), "the first element of the first element of a row, or the row that has none, counts the row once");
    }

    [Fact]
    public async Task What_stands_before_it_runs_as_written_and_the_bound_on_the_rows_follows_it()
    {
        var compiled = await Compile("""
            [ { "match": { "flag": { "eq": true } } },
              { "unwind": { "path": "items", "as": "item" } },
              { "match": { "item.quantity": { "gt": 1 } } },
              { "project": { "number": 1, "item": 1 } },
              { "values": { "path": "item.quantity" } } ]
            """);
        var names = Names(compiled);

        names.Skip(names.IndexOf("$unwind")).Should().Equal("$unwind", "$set", "$unset", "$match", "$project", "$limit", "$group", "$facet");
        compiled.LateProjections.Should().BeEmpty("a projection before values stays where it is: there is no page to cut first");
        compiled.Unwind.Should().NotBeNull("the element conditions are still asked of the collection before its unwind");
        compiled.Unwind!.ParentsFirst.Should().BeFalse("no parent is cut for a page that does not exist");
        compiled.Unwind.RenameLate.Should().BeFalse();
        compiled.PageStages[^2]["$group"]["_id"].AsString.Should().Be("$item.Quantity");
    }

    [Fact]
    public async Task A_sort_only_conditions_follow_is_not_run_and_one_a_group_reads_is()
    {
        var trailing = await Compile("""[{ "sort": [{ "when": "desc" }] }, { "match": { "flag": { "eq": true } } }, { "values": { "path": "number" } }]""");

        Names(trailing).Should().Equal(["$match", "$match", "$limit", "$group", "$facet"],
            "a sort before the bound on the rows would order every row first; the values are the same in any order");

        var read = await Compile("""
            [ { "sort": [{ "when": "desc" }] },
              { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "latest": { "first": "number" } } } },
              { "values": { "path": "latest" } } ]
            """);

        Names(read).Take(3).Should().Equal(["$match", "$sort", "$group"], "the group's 'first' reads the order");
        Names(read).TakeLast(3).Should().Equal("$limit", "$group", "$facet");
    }

    [Fact]
    public async Task A_join_runs_before_it_only_when_the_values_read_it()
    {
        var read = await Compile("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "values": { "path": "c.name" } }]""", ResolveModel.Invoice, resolve: true);

        Names(read).Should().ContainInOrder("$lookup", "$limit", "$group", "$facet");
        read.PageStages[^2]["$group"]["_id"].AsString.Should().Be("$c.Name");

        var unread = await Compile("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "values": { "path": "number" } }]""", ResolveModel.Invoice, resolve: true);

        Names(unread).Should().NotContain("$lookup", "a join nothing reads is in no row the stage answers");
        unread.KeyedResolves.Should().BeEmpty();

        // A join after the page has no page to run after: one nothing reads is not fetched either.
        var keyed = await Compile("""[{ "resolve": { "path": "contactId", "as": "r" } }, { "values": { "path": "number" } }]""", ResolveModel.Invoice, resolve: true);

        keyed.KeyedResolves.Should().BeEmpty();
        Names(keyed).Should().Equal("$match", "$limit", "$group", "$facet");
    }

    [Fact]
    public async Task After_a_group_the_values_of_a_key_are_a_group_over_the_groups()
    {
        var compiled = await Compile("""
            [ { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } },
              { "values": { "path": "n", "counts": true } } ]
            """);

        Names(compiled).Should().Equal("$match", "$group", "$project", "$limit", "$group", "$facet");
        compiled.PageStages[^2]["$group"]["_id"].AsString.Should().Be("$n");
    }

    [Fact]
    public async Task The_canonical_form_says_what_was_bound_and_carries_no_page()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "values": { "path": "items.stops.city", "search": "be", "counts": true, "limit": 9, "scan": 77 } }]""");

        bound.Canonical.Should().Be("""{"entity":"probe.order","scope":{"path":"OrganizationId","organisation":"a8d899a4-2029-4806-a4b7-a414eab21801"},"stages":[{"values":{"path":"Items.Stops.City","unwind":["Items","Items.Stops"],"search":{"path":"_id","op":"startsWith","operand":"be","caseSensitive":false,"semiJoin":false,"addonDecimal":false},"limit":9,"counts":true,"scan":77}}],"paging":"offset"}""");
    }
}
