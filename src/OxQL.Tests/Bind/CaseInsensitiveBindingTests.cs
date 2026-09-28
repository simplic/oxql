using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// Under contract 2 every string comparison, sort and group key folds case and accents unless
/// the caller opts out with <c>caseSensitive</c>; <c>ignoreCase</c> is its alias with the
/// opposite sense. Contract 1 keeps its opt-in fold. What folds marks the request as collated.
/// </summary>
public class CaseInsensitiveBindingTests
{
    private const string Order = "probe.order";

    private static EntityModel Model => BindHost.Probe;

    private static RequestContext Contract1 => BindHost.Context(contract: 1);

    private static async Task<BoundPipeline> Bound(string pipeline, RequestContext? context = null) =>
        await BindHost.BoundAsync(Model, Order, pipeline, context);

    private static async Task<QueryValidationError> Error(string pipeline, string code, RequestContext? context = null) =>
        await BindHost.ErrorAsync(Model, Order, pipeline, code, context);

    private static BoundCondition.Leaf Leaf(BoundPipeline bound, int stage = 0) =>
        (BoundCondition.Leaf)((BoundStage.Match)bound.Stages[stage]).Condition;

    private static BoundStage.Sort SortOf(BoundPipeline bound) => bound.Stages.OfType<BoundStage.Sort>().First();

    // ---- conditions -------------------------------------------------------------------------

    [Theory]
    [InlineData("eq", "\"x\"")]
    [InlineData("neq", "\"x\"")]
    [InlineData("in", "[\"x\"]")]
    [InlineData("nin", "[\"x\"]")]
    [InlineData("gt", "\"x\"")]
    [InlineData("gte", "\"x\"")]
    [InlineData("lt", "\"x\"")]
    [InlineData("lte", "\"x\"")]
    [InlineData("contains", "\"x\"")]
    [InlineData("startsWith", "\"x\"")]
    [InlineData("endsWith", "\"x\"")]
    public async Task A_string_comparison_folds_by_default_and_collates_the_request(string op, string operand)
    {
        var bound = await Bound($$"""[{ "match": { "number": { "{{op}}": {{operand}} } } }]""");

        Leaf(bound).IgnoreCase.Should().BeTrue(op);
        bound.Collated.Should().BeTrue(op);
    }

    [Theory]
    [InlineData("eq", "\"x\"")]
    [InlineData("neq", "\"x\"")]
    [InlineData("in", "[\"x\"]")]
    [InlineData("nin", "[\"x\"]")]
    [InlineData("contains", "\"x\"")]
    [InlineData("startsWith", "\"x\"")]
    [InlineData("endsWith", "\"x\"")]
    public async Task CaseSensitive_compares_exactly_and_on_its_own_leaves_the_request_uncollated(string op, string operand)
    {
        var bound = await Bound($$"""[{ "match": { "number": { "{{op}}": {{operand}}, "options": { "caseSensitive": true } } } }]""");

        Leaf(bound).IgnoreCase.Should().BeFalse(op);
        bound.Collated.Should().BeFalse(op);

        var restated = await Bound($$"""[{ "match": { "number": { "{{op}}": {{operand}}, "options": { "caseSensitive": false } } } }]""");

        Leaf(restated).IgnoreCase.Should().BeTrue("caseSensitive: false restates the default");
    }

    [Fact]
    public async Task IgnoreCase_is_the_alias_with_the_opposite_sense()
    {
        Leaf(await Bound("""[{ "match": { "number": { "eq": "x", "options": { "ignoreCase": true } } } }]""")).IgnoreCase.Should().BeTrue();
        Leaf(await Bound("""[{ "match": { "number": { "eq": "x", "options": { "ignoreCase": false } } } }]""")).IgnoreCase.Should().BeFalse();
        Leaf(await Bound("""[{ "match": { "number": { "eq": "x", "options": { "ignoreCase": false, "caseSensitive": true } } } }]""")).IgnoreCase.Should().BeFalse("the two agree");

        var contradiction = await Error("""[{ "match": { "number": { "eq": "x", "options": { "ignoreCase": true, "caseSensitive": true } } } }]""", Codes.OptionNotApplicable);

        contradiction.Message.Should().Contain("contradict");
        contradiction.Path.Should().Be("number");
    }

    [Fact]
    public async Task The_option_does_not_apply_to_a_member_without_text()
    {
        (await Error("""[{ "match": { "count": { "eq": 1, "options": { "caseSensitive": true } } } }]""", Codes.OptionNotApplicable)).Path.Should().Be("count");
        await Error("""[{ "match": { "count": { "eq": 1, "options": { "caseSensitive": false } } } }]""", Codes.OptionNotApplicable);
        await Error("""[{ "match": { "count": { "eq": 1, "options": { "ignoreCase": true } } } }]""", Codes.OptionNotApplicable);
        await Error("""[{ "match": { "when": { "gt": "2026-01-01T00:00:00Z", "options": { "caseSensitive": true } } } }]""", Codes.OptionNotApplicable);

        // ignoreCase: false asks a number for the exact comparison it already makes; it changes nothing.
        var number = await Bound("""[{ "match": { "count": { "eq": 1, "options": { "ignoreCase": false } } } }]""");

        Leaf(number).IgnoreCase.Should().BeFalse();
        number.Collated.Should().BeFalse();

        var guid = await Bound("""[{ "match": { "id": { "eq": "195fb742-82b3-405e-b77b-42838eb0aaa9" } } }]""");

        Leaf(guid).IgnoreCase.Should().BeFalse("a guid has no case to fold");
        guid.Collated.Should().BeFalse();
    }

    [Fact]
    public async Task An_ordered_comparison_and_a_pattern_cannot_opt_out()
    {
        var ordered = await Error("""[{ "match": { "number": { "gt": "x", "options": { "caseSensitive": true } } } }]""", Codes.OptionNotApplicable);

        ordered.Message.Should().Contain("cannot opt out");
        await Error("""[{ "match": { "number": { "gt": "x", "options": { "ignoreCase": false } } } }]""", Codes.OptionNotApplicable);
        await Error("""[{ "match": { "number": { "gt": "x", "options": { "ignoreCase": true } } } }]""", Codes.OptionNotApplicable);
        await Error("""[{ "match": { "number": { "regex": "^x", "options": { "caseSensitive": true } } } }]""", Codes.OptionNotApplicable);
        await Error("""[{ "match": { "number": { "exists": true, "options": { "caseSensitive": false } } } }]""", Codes.OptionNotApplicable);
    }

    [Fact]
    public async Task A_null_and_an_empty_set_have_nothing_to_fold()
    {
        foreach (var condition in new[] { """{ "eq": null }""", """{ "neq": null }""", """{ "in": [] }""", """{ "in": [null] }""" })
        {
            var bound = await Bound($$"""[{ "match": { "number": {{condition}} } }]""");

            Leaf(bound).IgnoreCase.Should().BeFalse(condition);
            bound.Collated.Should().BeFalse(condition);
        }
    }

    [Fact]
    public async Task A_char_folds_by_code_point_by_default_and_never_collates()
    {
        var folded = await Bound("""[{ "match": { "initial": { "eq": "a" } } }]""");

        Leaf(folded).Op.Should().Be("in");
        Leaf(folded).Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().Equal(new BsonInt32('a'), new BsonInt32('A'));
        Leaf(folded).IgnoreCase.Should().BeFalse("the fold replaces the pattern");
        folded.Collated.Should().BeFalse("a code point is not text the collation reaches");

        var exact = await Bound("""[{ "match": { "initial": { "eq": "a", "options": { "caseSensitive": true } } } }]""");

        Leaf(exact).Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonInt32('a'));

        var ordered = await Bound("""[{ "match": { "initial": { "gt": "a" } } }]""");

        Leaf(ordered).Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonInt32('a'), "a char orders by its code point");
        ordered.Collated.Should().BeFalse();
    }

    [Fact]
    public async Task A_filter_inside_a_join_folds_like_any_other_condition()
    {
        (await Bound("""[{ "resolve": { "path": "customerId", "as": "cust", "filter": { "matchCode": { "eq": "m" } } } }]""")).Collated.Should().BeTrue();
        (await Bound("""[{ "resolve": { "path": "customerId", "as": "cust", "filter": { "matchCode": { "eq": "m", "options": { "caseSensitive": true } } } } }]""")).Collated.Should().BeFalse();
        (await Bound("""[{ "match": { "items": { "any": { "notes": { "eq": "n" } } } } }]""")).Collated.Should().BeTrue("a condition inside any folds too");
    }

    // ---- sort -------------------------------------------------------------------------------

    [Fact]
    public async Task A_sort_on_a_string_folds_by_default_and_the_object_form_opts_out()
    {
        var folded = await Bound("""[{ "sort": [{ "number": "asc" }] }]""");

        SortOf(folded).Fields[0].IgnoreCase.Should().BeTrue();
        folded.Collated.Should().BeTrue();

        var exact = await Bound("""[{ "sort": [{ "number": { "direction": "desc", "caseSensitive": true } }] }]""");

        SortOf(exact).Fields[0].IgnoreCase.Should().BeFalse();
        SortOf(exact).Fields[0].Ascending.Should().BeFalse();
        exact.Collated.Should().BeFalse();

        SortOf(await Bound("""[{ "sort": [{ "number": { "direction": "asc", "caseSensitive": false } }] }]""")).Fields[0].IgnoreCase.Should().BeTrue();

        var number = await Bound("""[{ "sort": [{ "count": "asc" }] }]""");

        SortOf(number).Fields[0].IgnoreCase.Should().BeFalse("a number has no case");
        number.Collated.Should().BeFalse();
        SortOf(await Bound("""[{ "sort": [{ "initial": "asc" }] }]""")).Fields[0].IgnoreCase.Should().BeFalse("a code point orders by value");
    }

    [Fact]
    public async Task The_sort_object_form_is_checked_like_a_stage()
    {
        (await Error("""[{ "sort": [{ "count": { "direction": "asc", "caseSensitive": true } }] }]""", Codes.OptionNotApplicable)).Path.Should().Be("count");
        await Error("""[{ "sort": [{ "number": { "direction": "asc", "nulls": "first" } }] }]""", Codes.UnknownStageMember);
        await Error("""[{ "sort": [{ "number": { "caseSensitive": true } }] }]""", Codes.InvalidSortDirection);
        await Error("""[{ "sort": [{ "number": { "direction": "up" } }] }]""", Codes.InvalidSortDirection);
    }

    [Fact]
    public async Task An_exact_sort_is_refused_inside_a_collated_request()
    {
        var refused = await Error("""[{ "match": { "note": { "eq": "x" } } }, { "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }] }]""", Codes.OptionNotApplicable);

        refused.Stage.Should().Be(1);
        refused.Path.Should().Be("number");

        await Error("""[{ "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }, { "note": "asc" }] }]""", Codes.OptionNotApplicable);

        var exact = await Bound("""[{ "match": { "note": { "eq": "x", "options": { "caseSensitive": true } } } }, { "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }] }]""");

        exact.Collated.Should().BeFalse("every string comparison in it is exact");
    }

    // ---- group ------------------------------------------------------------------------------

    [Fact]
    public async Task A_group_folds_a_string_key_and_a_distinct_or_extreme_over_a_string()
    {
        var byString = await Bound("""[{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "c": { "count": true } } } }]""");

        byString.Collated.Should().BeTrue();
        SortOf(byString).Fields[0].IgnoreCase.Should().BeTrue("the default order over the key folds like the key");

        (await Bound("""[{ "group": { "by": [{ "path": "count", "as": "c" }], "fields": { "n": { "count": true } } } }]""")).Collated.Should().BeFalse();
        (await Bound("""[{ "group": { "by": [{ "path": "count", "as": "c" }], "fields": { "d": { "countDistinct": "number" } } } }]""")).Collated.Should().BeTrue();
        (await Bound("""[{ "group": { "by": [{ "path": "count", "as": "c" }], "fields": { "m": { "max": "number" } } } }]""")).Collated.Should().BeTrue();
        (await Bound("""[{ "group": { "by": [{ "path": "count", "as": "c" }], "fields": { "f": { "first": "number" } } } }]""")).Collated.Should().BeFalse("first compares nothing");
    }

    // ---- remote -----------------------------------------------------------------------------

    [Fact]
    public async Task A_remote_comparison_carries_the_callers_choice_and_nothing_else()
    {
        const string Resolve = """{ "resolve": { "path": "contactNumber", "as": "contact" } }""";

        Leaf(await Bound($$"""[{{Resolve}}, { "match": { "contact.name": { "eq": "x" } } }]"""), 1).IgnoreCase.Should().BeNull("the owner applies its own default");
        Leaf(await Bound($$"""[{{Resolve}}, { "match": { "contact.name": { "eq": "x", "options": { "caseSensitive": true } } } }]"""), 1).IgnoreCase.Should().BeFalse();
        Leaf(await Bound($$"""[{{Resolve}}, { "match": { "contact.name": { "eq": "x", "options": { "ignoreCase": false } } } }]"""), 1).IgnoreCase.Should().BeFalse();
        Leaf(await Bound($$"""[{{Resolve}}, { "match": { "contact.name": { "eq": "x", "options": { "ignoreCase": true } } } }]"""), 1).IgnoreCase.Should().BeTrue();
    }

    // ---- patterns ---------------------------------------------------------------------------

    [Fact]
    public async Task An_operand_is_held_to_the_pattern_limit_only_where_it_becomes_a_pattern()
    {
        var tooLong = new string('a', 40_000);

        await Bound($$"""[{ "match": { "number": { "eq": "{{tooLong}}", "options": { "ignoreCase": true } } } }]""");
        await Bound($$"""[{ "match": { "number": { "eq": "{{tooLong}}", "options": { "caseSensitive": true } } } }]""");

        var refused = await Error($$"""[{ "match": { "number": { "eq": "{{tooLong}}", "options": { "caseSensitive": true } }, "note": { "eq": "x" } } }]""", Codes.InvalidOperand);

        refused.Path.Should().Be("number", "inside a collated request the exact comparison is a pattern");
        await Error($$"""[{ "match": { "number": { "contains": "{{tooLong}}" } } }]""", Codes.InvalidOperand);
    }

    // ---- canonical --------------------------------------------------------------------------

    [Fact]
    public async Task The_canonical_form_says_whether_each_comparison_and_sort_is_exact()
    {
        var folded = await Bound("""[{ "match": { "number": { "eq": "x" } } }, { "sort": [{ "number": "asc" }] }]""");
        var exact = await Bound("""[{ "match": { "number": { "eq": "x", "options": { "caseSensitive": true } } } }, { "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }] }]""");

        folded.Canonical.Should().Contain("\"caseSensitive\":false").And.NotContain("\"caseSensitive\":true").And.NotContain("ignoreCase");
        exact.Canonical.Should().Contain("\"caseSensitive\":true").And.NotContain("\"caseSensitive\":false");
        exact.Fingerprint.Should().NotBe(folded.Fingerprint, "a cursor minted under one fold does not page the other");
    }

    // ---- contract 1 -------------------------------------------------------------------------

    [Fact]
    public async Task Contract_1_keeps_its_opt_in_fold_and_knows_no_caseSensitive()
    {
        var plain = await Bound("""[{ "match": { "number": { "eq": "x" } } }, { "sort": [{ "number": "asc" }] }]""", Contract1);

        Leaf(plain).IgnoreCase.Should().BeFalse();
        SortOf(plain).Fields[0].IgnoreCase.Should().BeFalse();
        plain.Collated.Should().BeFalse();

        var folded = await Bound("""[{ "match": { "number": { "eq": "x", "options": { "ignoreCase": true } } } }]""", Contract1);

        Leaf(folded).IgnoreCase.Should().BeTrue();
        folded.Collated.Should().BeFalse("contract 1 folds with a pattern, never with the collation");
        Leaf(await Bound("""[{ "match": { "number": { "eq": "x", "options": { "ignoreCase": false } } } }]""", Contract1)).IgnoreCase.Should().BeFalse();

        (await Error("""[{ "match": { "number": { "eq": "x", "options": { "caseSensitive": true } } } }]""", Codes.OptionNotApplicable, Contract1)).Message.Should().Be("'caseSensitive' is not an option under contract 1; it is a contract 2 option." + Binder.Contract1Hint);
        await Error("""[{ "match": { "number": { "gt": "x", "options": { "ignoreCase": true } } } }]""", Codes.OptionNotApplicable, Contract1);
        await Error("""[{ "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }] }]""", Codes.InvalidSortDirection, Contract1);
        (await Bound("""[{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "c": { "count": true } } } }]""", Contract1)).Collated.Should().BeFalse();
    }
}
