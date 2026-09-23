using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// What a folded and an exact string comparison compile to, the collation the aggregate
/// carries, and the pipelines that must not change: everything over other kinds, and every
/// join on a key that is not a string.
/// </summary>
public class CaseInsensitiveCompileTests
{
    private const string Order = "probe.order";
    private static readonly BsonBinaryData Org = new(BindHost.Organisation, GuidRepresentation.Standard);
    private static readonly CompileOptions Options = new(10_000, null, 100_000);
    private static readonly BsonDocument DefaultCollation = BsonDocument.Parse("{ locale: 'de', strength: 1 }");

    private static async Task<CompiledQuery> Compile(string pipeline, string entity = Order, RequestContext? context = null, CompileOptions? options = null)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, entity, pipeline, context);

        return MongoCompiler.Compile(bound, options ?? Options);
    }

    private static async Task<CompiledQuery> CompileJoin(string entity, string pipeline)
    {
        var bound = await BindHost.BoundAsync(CaseInsensitiveJoinModel.Model, entity, pipeline);

        return MongoCompiler.Compile(bound, Options);
    }

    private static string OrgJson => $"{{ OrganizationId: {Org.ToJson()} }}";

    private static void ShouldBe(IReadOnlyList<BsonDocument> actual, params string[] expected) =>
        actual.Select(stage => stage.ToJson()).Should().Equal(expected.Select(stage => BsonDocument.Parse(stage).ToJson()));

    [Fact]
    public async Task A_folded_comparison_is_a_plain_operator_a_prefix_is_a_range_and_the_aggregate_carries_the_collation()
    {
        var compiled = await Compile("""
            [{ "match": { "or": [
                { "number": { "eq": "a.b" } },
                { "number": { "neq": "a" } },
                { "number": { "in": ["a", "b"] } },
                { "number": { "nin": ["a"] } },
                { "number": { "gt": "a" } },
                { "number": { "startsWith": "a.b" } },
                { "number": { "contains": "a" } },
                { "number": { "endsWith": "a" } } ] } }, { "page": { "includeTotalCount": true } }]
            """);

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $or: [
                { Number: 'a.b' },
                { Number: { $ne: 'a' } },
                { Number: { $in: ['a', 'b'] } },
                { Number: { $nin: ['a'] } },
                { Number: { $gt: 'a' } },
                { Number: { $gte: 'a.b', $lt: 'a.b￿' } },
                { Number: /a/i },
                { Number: /a$/i } ] } }
            """));
        compiled.CountStages![1].ShouldBeBson(compiled.PageStages[1], "the count reads the same filter");
        compiled.Collation.Should().NotBeNull();
        compiled.Collation!.ShouldBeBson(DefaultCollation);
    }

    [Fact]
    public async Task An_exact_comparison_inside_a_collated_aggregate_is_an_anchored_pattern_without_a_flag()
    {
        var compiled = await Compile("""
            [{ "match": { "or": [
                { "number": { "eq": "a.b", "options": { "caseSensitive": true } } },
                { "number": { "neq": "a", "options": { "caseSensitive": true } } },
                { "number": { "in": ["a", "b"], "options": { "caseSensitive": true } } },
                { "number": { "nin": ["a", null], "options": { "caseSensitive": true } } },
                { "number": { "startsWith": "a", "options": { "caseSensitive": true } } },
                { "number": { "contains": "a", "options": { "caseSensitive": true } } },
                { "number": { "endsWith": "a", "options": { "caseSensitive": true } } } ],
              "note": { "eq": "x" } } }]
            """);

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("""
            { $match: { $and: [ { $or: [
                { Number: /^a\.b$/ },
                { Number: { $not: /^a$/ } },
                { Number: { $in: [ /^a$/, /^b$/ ] } },
                { Number: { $nin: [ /^a$/, null ] } },
                { Number: /^a/ },
                { Number: /a/ },
                { Number: /a$/ } ] },
                { Note: 'x' } ] } }
            """));
        compiled.Collation.Should().NotBeNull("the note folds");
    }

    [Fact]
    public async Task An_exact_comparison_on_its_own_is_the_plain_form_without_a_collation()
    {
        var compiled = await Compile("""[{ "match": { "number": { "eq": "a.b", "options": { "caseSensitive": true } }, "note": { "startsWith": "a", "options": { "ignoreCase": false } } } }]""");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $match: { $and: [ { Number: 'a.b' }, { Note: /^a/ } ] } }"));
        compiled.Collation.Should().BeNull();
    }

    [Fact]
    public async Task A_pipeline_over_other_kinds_is_unchanged_and_carries_no_collation()
    {
        var compiled = await Compile("""
            [{ "match": { "count": { "gte": 1 }, "when": { "lt": "2026-01-01T00:00:00Z" }, "customerId": { "eq": "195fb742-82b3-405e-b77b-42838eb0aaa9" }, "flag": { "eq": true }, "state": { "in": ["Open"] } } },
             { "sort": [{ "count": "desc" }] },
             { "page": { "limit": 20, "includeTotalCount": true } }]
            """);

        ShouldBe(compiled.PageStages,
            $"{{ $match: {OrgJson} }}",
            """{ $match: { $and: [ { Count: { $gte: NumberLong(1) } }, { When: { $lt: ISODate("2026-01-01T00:00:00Z") } }, { CustomerId: BinData(4, "GV+3QoKzQF63e0KDjrCqqQ==") }, { Flag: true }, { State: { $in: [0] } } ] } }""",
            "{ $sort: { Count: -1, _id: 1 } }",
            "{ $limit: 21 }");
        ShouldBe(compiled.CountStages!,
            $"{{ $match: {OrgJson} }}",
            """{ $match: { $and: [ { Count: { $gte: NumberLong(1) } }, { When: { $lt: ISODate("2026-01-01T00:00:00Z") } }, { CustomerId: BinData(4, "GV+3QoKzQF63e0KDjrCqqQ==") }, { Flag: true }, { State: { $in: [0] } } ] } }""",
            "{ $limit: 100001 }",
            "{ $count: 'n' }");
        compiled.Collation.Should().BeNull();
    }

    [Fact]
    public async Task A_contract_1_fold_is_the_pattern_it_always_was_without_a_collation()
    {
        var compiled = await Compile("""[{ "match": { "number": { "eq": "a", "options": { "ignoreCase": true } }, "note": { "startsWith": "b", "options": { "ignoreCase": true } } } }, { "sort": [{ "number": "asc" }] }]""", context: BindHost.Context(contract: 1));

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $match: { $and: [ { Number: /^a$/i }, { Note: /^b/i } ] } }"));
        compiled.Collation.Should().BeNull();
    }

    [Fact]
    public async Task The_configured_locale_and_strength_are_what_the_aggregate_carries()
    {
        var compiled = await Compile("""[{ "match": { "number": { "eq": "a" } } }]""", options: new CompileOptions(10_000, null, 100_000, new CollationOptions { Locale = "en", Strength = 2 }));

        compiled.Collation!.ShouldBeBson(BsonDocument.Parse("{ locale: 'en', strength: 2 }"));
    }

    [Fact]
    public async Task A_char_folds_by_code_point_without_a_collation()
    {
        var compiled = await Compile("""[{ "match": { "initial": { "eq": "a" } } }]""");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $match: { Initial: { $in: [97, 65] } } }"));
        compiled.Collation.Should().BeNull();
    }

    [Fact]
    public async Task A_folded_sort_emits_the_same_sort_and_the_same_cursor_legs()
    {
        var first = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]""");
        var compiled = MongoCompiler.Compile(first, Options);

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $sort: { Number: 1, _id: 1 } }"));
        compiled.Collation.Should().NotBeNull();

        var id = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        var cursor = BindHost.Cursors.Encode(new CursorPayload(first.Fingerprint, PagingMode.Keyset, [new CursorValue("number", true, new BsonString("abc")), new CursorValue("_id", true, id)], 0));
        var next = await Compile($$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""");

        // The equality leg compares under the collation, so a neighbour that differs only in
        // case falls through to the key leg rather than being skipped or repeated.
        next.PageStages[0].ShouldBeBson(BsonDocument.Parse($$"""
            { $match: { $and: [ {{OrgJson}}, { $or: [ { Number: 'abc', _id: { $gt: {{id.ToJson()}} } }, { Number: { $gt: 'abc' } } ] } ] } }
            """));
        next.Collation!.ShouldBeBson(DefaultCollation);

        var exact = await Compile("""[{ "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }] }]""");

        exact.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $sort: { Number: 1, _id: 1 } }"));
        exact.Collation.Should().BeNull();
    }

    [Fact]
    public async Task A_join_on_a_guid_key_is_unchanged_inside_a_collated_aggregate()
    {
        var compiled = await Compile("""[{ "resolve": { "path": "customerId", "as": "cust", "select": ["name"], "filter": { "matchCode": { "eq": "m" } } } }]""");

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""
            { $lookup: { from: "customers", localField: "CustomerId", foreignField: "_id", pipeline: [ { $match: {{OrgJson}} }, { $match: { MatchCode: "m" } }, { $limit: 1 }, { $project: { _id: 1, Name: 1 } } ], as: "cust__arr" } }
            """));
        compiled.Collation.Should().NotBeNull("the filter folds");

        var lookup = await Compile("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "filter": { "number": { "eq": "x" } } } }]""", "probe.customer");

        lookup.PageStages[1]["$lookup"].AsBsonDocument.Names.Should().Equal("from", "localField", "foreignField", "pipeline", "as");
        lookup.PageStages[1]["$lookup"]["pipeline"].AsBsonArray[1].ShouldBeBson(BsonDocument.Parse("{ $match: { Number: 'x' } }"));
    }

    [Fact]
    public async Task A_join_on_a_string_key_compares_the_key_byte_for_byte_inside_a_collated_aggregate()
    {
        var resolve = await CompileJoin(CaseInsensitiveJoinModel.Document, """[{ "resolve": { "path": "folderCode", "as": "folder" } }, { "match": { "title": { "eq": "t" } } }]""");

        resolve.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""
            { $lookup: { from: "tmp_ci_folders", localField: "FolderCode", foreignField: "Code", let: { oxKey: "$FolderCode" }, pipeline: [
                { $match: {{OrgJson}} },
                { $match: { $expr: { $cond: {
                    if: { $and: [ { $eq: [ { $type: "$$oxKey" }, "string" ] }, { $eq: [ { $type: "$Code" }, "string" ] } ] },
                    then: { $and: [ { $eq: [ { $indexOfBytes: [ "$Code", "$$oxKey" ] }, 0 ] }, { $eq: [ { $strLenBytes: "$Code" }, { $strLenBytes: "$$oxKey" } ] } ] },
                    else: true } } } },
                { $limit: 1 },
                { $project: { _id: 1, Name: 1 } } ], as: "folder__arr" } }
            """));
        resolve.Collation.Should().NotBeNull();

        var lookup = await CompileJoin(CaseInsensitiveJoinModel.Folder, """[{ "lookup": { "from": "ci.document", "path": "folderCode", "as": "docs" } }, { "match": { "name": { "eq": "x" } } }]""");
        var join = lookup.PageStages[1]["$lookup"].AsBsonDocument;

        join.Names.Should().Equal("from", "localField", "foreignField", "let", "pipeline", "as");
        join["let"].AsBsonDocument.ShouldBeBson(BsonDocument.Parse("{ oxKey: '$Code' }"));
        join["pipeline"].AsBsonArray[1]["$match"]["$expr"]["$cond"]["then"].AsBsonDocument.ShouldBeBson(BsonDocument.Parse("""
            { $and: [ { $eq: [ { $indexOfBytes: [ "$FolderCode", "$$oxKey" ] }, 0 ] }, { $eq: [ { $strLenBytes: "$FolderCode" }, { $strLenBytes: "$$oxKey" } ] } ] }
            """));
    }

    [Fact]
    public async Task A_join_on_a_string_key_is_unchanged_when_nothing_folds()
    {
        var resolve = await CompileJoin(CaseInsensitiveJoinModel.Document, """[{ "resolve": { "path": "folderCode", "as": "folder" } }]""");

        resolve.PageStages[1].ShouldBeBson(BsonDocument.Parse($$"""
            { $lookup: { from: "tmp_ci_folders", localField: "FolderCode", foreignField: "Code", pipeline: [ { $match: {{OrgJson}} }, { $limit: 1 }, { $project: { _id: 1, Name: 1 } } ], as: "folder__arr" } }
            """));
        resolve.Collation.Should().BeNull();
    }
}
