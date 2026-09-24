using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// D-3: a sort after a group is completed by the group keys. A grouped shape pages by offset;
/// without the keys, <c>$skip</c> over rows tied on every sort field repeated and dropped
/// groups between pages, and the same page asked twice answered different groups.
/// </summary>
public sealed class GroupSortTieBreakTests
{
    private const string Order = "probe.order";
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private const string ByNumber = """{ "group": { "by": [{ "path": "number", "as": "no" }], "fields": { "n": { "count": true } } } }""";
    private const string ByStateAndNumber = """{ "group": { "by": [{ "path": "state", "as": "st" }, { "path": "number", "as": "no" }], "fields": { "n": { "count": true } } } }""";

    private static async Task<CompiledQuery> Compile(string pipeline, RequestContext? context = null) =>
        MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Order, pipeline, context), Options);

    private static BsonDocument SortOf(CompiledQuery compiled) =>
        compiled.PageStages.Last(stage => stage.Contains("$sort"))["$sort"].AsBsonDocument;

    [Fact]
    public async Task A_sort_on_an_aggregate_is_completed_by_the_group_key()
    {
        var compiled = await Compile($$"""[{{ByNumber}}, { "sort": [{ "n": "desc" }] }, { "page": { "limit": 5, "offset": 10 } }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ n: -1, no: 1 }").ToJson(), "the key is unique per group, so no two rows tie on the whole order");
        compiled.PageStages.Select(stage => stage.GetElement(0).Name).TakeLast(3).Should().Equal("$sort", "$skip", "$limit");
    }

    [Fact]
    public async Task A_string_key_breaks_the_tie_under_the_collation_it_grouped_with()
    {
        // Under contract 2 the string key folds into its group, so the groups are unique under
        // the collation and the same collation orders them: "abc" and "ABC" are one group, not
        // two rows that tie. No byte-exact secondary is needed or possible in one aggregate.
        var compiled = await Compile($$"""[{{ByNumber}}, { "sort": [{ "n": "desc" }] }]""");

        compiled.Collation.Should().NotBeNull();
        compiled.Bound.Sort!.Fields.Should().HaveCount(2);
        compiled.Bound.Sort.Fields[1].Path.Wire.Should().Be("no");
        compiled.Bound.Sort.Fields[1].Ascending.Should().BeTrue();
        compiled.Bound.Sort.Fields[1].IgnoreCase.Should().BeTrue("the key folded into its group and its order folds the same way");
    }

    [Fact]
    public async Task Under_contract_1_the_key_breaks_the_tie_exactly()
    {
        var compiled = await Compile($$"""[{{ByNumber}}, { "sort": [{ "n": "desc" }] }]""", BindHost.Context(contract: 1));

        compiled.Collation.Should().BeNull("contract 1 groups and orders by the exact value");
        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ n: -1, no: 1 }").ToJson());
        compiled.Bound.Sort!.Fields[1].IgnoreCase.Should().BeFalse();
    }

    [Fact]
    public async Task Every_leg_of_a_composite_key_the_caller_did_not_sort_on_is_appended_in_key_order()
    {
        var compiled = await Compile($$"""[{{ByStateAndNumber}}, { "sort": [{ "n": "desc" }] }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ n: -1, st: 1, no: 1 }").ToJson());
    }

    [Fact]
    public async Task A_key_the_caller_sorts_on_keeps_its_place_and_direction()
    {
        var compiled = await Compile($$"""[{{ByStateAndNumber}}, { "sort": [{ "no": "desc" }, { "n": "asc" }] }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ no: -1, n: 1, st: 1 }").ToJson());
    }

    [Fact]
    public async Task A_sort_on_every_key_is_left_as_written()
    {
        var compiled = await Compile($$"""[{{ByStateAndNumber}}, { "sort": [{ "st": "desc" }, { "no": "desc" }] }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ st: -1, no: -1 }").ToJson());
    }

    [Fact]
    public async Task A_date_trunc_key_breaks_the_tie_by_its_instant()
    {
        var compiled = await Compile("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "day" }, "as": "d" }], "fields": { "n": { "count": true } } } }, { "sort": [{ "n": "desc" }] }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ n: -1, d: 1 }").ToJson());
        compiled.Bound.Sort!.Fields[1].IgnoreCase.Should().BeFalse();
    }

    [Fact]
    public async Task A_group_with_no_key_has_nothing_to_append()
    {
        var compiled = await Compile("""[{ "group": { "by": [], "fields": { "n": { "count": true } } } }, { "sort": [{ "n": "desc" }] }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ n: -1 }").ToJson());
    }

    [Fact]
    public async Task The_tie_break_is_bound_so_the_fingerprint_is_that_of_the_written_total_order()
    {
        // It reaches the canonical form, explain and the fingerprint like the default sort: an
        // offset cursor minted before the fix is refused instead of paging another order.
        var implicitOrder = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{{ByNumber}}, { "sort": [{ "n": "desc" }] }]""");
        var writtenOrder = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{{ByNumber}}, { "sort": [{ "n": "desc" }, { "no": "asc" }] }]""");

        implicitOrder.Fingerprint.Should().Be(writtenOrder.Fingerprint);
    }

    [Fact]
    public async Task A_sort_on_the_root_shape_still_ends_in_the_entity_key_only()
    {
        var compiled = await Compile("""[{ "sort": [{ "number": "desc" }] }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ Number: -1, _id: 1 }").ToJson());
    }

    [Fact]
    public async Task A_sort_before_a_group_is_not_completed_and_the_grouped_default_order_applies()
    {
        var compiled = await Compile($$"""[{ "sort": [{ "number": "desc" }] }, {{ByNumber}}]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ no: 1 }").ToJson(), "the group resets the sort; its keys are the default order");
    }

    [Fact]
    public async Task A_key_projected_away_before_the_sort_cannot_complete_it()
    {
        // The documented limit: the sort can only read what the shape still carries.
        var compiled = await Compile($$"""[{{ByStateAndNumber}}, { "project": { "n": 1, "no": 1 } }, { "sort": [{ "n": "desc" }] }]""");

        SortOf(compiled).ToJson().Should().Be(BsonDocument.Parse("{ n: -1, no: 1 }").ToJson());
    }
}
