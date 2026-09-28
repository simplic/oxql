using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Tests.Model.Fixtures.Variants;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The <c>is</c> operator and <c>unwind.flatten</c> as the binder reads them: what they bind to,
/// every refusal with its code and message, contract 1, and the canonical render (an unwind
/// without flatten renders as under 2.0).
/// </summary>
public class VariantBindingTests
{
    private const string Kennel = VariantModel.Kennel;

    private static Task<BoundPipeline> Bound(string pipeline, RequestContext? context = null, string? variables = null) =>
        BindHost.BoundAsync(VariantModel.Model, Kennel, pipeline, context, variables);

    private static Task<QueryValidationError> Error(string pipeline, string code, RequestContext? context = null) =>
        BindHost.ErrorAsync(VariantModel.Model, Kennel, pipeline, code, context);

    private static BoundCondition.Leaf LeafOf(BoundPipeline bound) =>
        bound.Stages.OfType<BoundStage.Match>().Single().Condition.Should().BeOfType<BoundCondition.Leaf>().Subject;

    private static IEnumerable<string?> Values(BoundCondition.Leaf leaf) =>
        leaf.Operand.Should().BeOfType<BoundOperand.Set>().Subject.Values.Select(value => value.IsBsonNull ? null : value.AsString);

    // ---- is ----------------------------------------------------------------------------------

    [Fact]
    public async Task Is_admits_the_named_variant_and_every_registered_descendant_by_their_discriminators()
    {
        var leaf = LeafOf(await Bound("""[ { "match": { "pet": { "is": "Hound" } } } ]"""));

        leaf.Op.Should().Be("is");
        leaf.Path.Storage.Should().Be("Pet");
        Values(leaf).Should().Equal("beagle", "Hound");
    }

    [Fact]
    public async Task Is_takes_an_array_of_names_and_keeps_the_types_variant_order()
    {
        var leaf = LeafOf(await Bound("""[ { "match": { "pet": { "is": ["Cat", "Beagle"] } } } ]"""));

        Values(leaf).Should().Equal("beagle", "Cat");
    }

    [Fact]
    public async Task Is_takes_a_variable_holding_a_name()
    {
        var leaf = LeafOf(await Bound("""[ { "match": { "pet": { "is": { "$var": "kind" } } } } ]""", variables: """{ "kind": "Cat" }"""));

        Values(leaf).Should().Equal("Cat");
    }

    [Fact]
    public async Task A_concrete_base_is_its_own_name_and_adds_the_value_stored_without_a_discriminator()
    {
        Values(LeafOf(await Bound("""[ { "match": { "car": { "is": "Vehicle" } } } ]"""))).Should().Equal("Truck", null);
        Values(LeafOf(await Bound("""[ { "match": { "car": { "is": "Truck" } } } ]"""))).Should().Equal("Truck");
    }

    [Fact]
    public async Task An_abstract_base_is_not_a_name_is_accepts()
    {
        var error = await Error("""[ { "match": { "pet": { "is": "Pet" } } } ]""", Codes.UnknownVariant);

        error.Message.Should().Be("'Pet' is not a variant of 'pet'; its variants are Beagle, Cat, Hound.");
        error.Path.Should().Be("pet");
        error.Stage.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_name_lists_the_concrete_base_first()
    {
        var error = await Error("""[ { "match": { "car": { "is": ["Truck", "Bus"] } } } ]""", Codes.UnknownVariant);

        error.Message.Should().Be("'Bus' is not a variant of 'car'; its variants are Vehicle, Truck.");
    }

    [Theory]
    [InlineData("name", "'is' applies to a member that holds one of several variants; 'name' is a string.")]
    [InlineData("nodes", "'is' applies to a member that holds one of several variants; 'nodes' is an object of one type only.")]
    public async Task Is_on_a_member_without_variants_is_an_invalid_operand(string path, string message)
    {
        var error = await Error($$"""[ { "match": { "{{path}}": { "is": "Cat" } } } ]""", Codes.InvalidOperand);

        error.Message.Should().Be(message);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("[]")]
    [InlineData("[\"Cat\", 5]")]
    [InlineData("null")]
    public async Task Is_takes_a_name_or_a_non_empty_array_of_names(string operand)
    {
        var error = await Error($$"""[ { "match": { "pet": { "is": {{operand}} } } } ]""", Codes.InvalidOperand);

        error.Message.Should().Be("'is' takes a variant name or a non-empty array of variant names.");
    }

    [Fact]
    public async Task Is_under_a_collection_that_is_not_unwound_points_to_any_or_unwind()
    {
        var error = await Error("""[ { "match": { "stalls.occupant": { "is": "Cat" } } } ]""", Codes.InvalidOperand);

        error.Message.Should().Be("'stalls.occupant' lies under a collection that is not unwound; test its elements with 'any' on the collection, or unwind it first.");
    }

    [Fact]
    public async Task Is_applies_inside_any_on_an_unwound_element_and_on_a_collection_of_objects()
    {
        var any = await Bound("""[ { "match": { "stalls": { "any": { "occupant": { "is": "Cat" } } } } } ]""");
        var inner = any.Stages.OfType<BoundStage.Match>().Single().Condition.Should().BeOfType<BoundCondition.Any>().Subject.Inner;
        inner.Should().BeOfType<BoundCondition.Leaf>().Which.Op.Should().Be("is");

        var unwound = await Bound("""[ { "unwind": { "path": "stalls", "as": "stall" } }, { "match": { "stall.occupant": { "is": "Cat" } } } ]""");
        LeafOf(unwound).Path.Storage.Should().Be("stall.Occupant");

        var collection = await Bound("""[ { "match": { "pets": { "is": "Cat" } } } ]""");
        LeafOf(collection).Path.Storage.Should().Be("Pets");
    }

    [Fact]
    public async Task Is_takes_no_options()
    {
        var error = await Error("""[ { "match": { "pet": { "is": "Cat", "options": { "caseSensitive": true } } } } ]""", Codes.OptionNotApplicable);

        error.Message.Should().Be("'is' takes no options.");
    }

    [Fact]
    public async Task Is_is_not_an_operator_under_contract_1()
    {
        var error = await Error("""[ { "match": { "pet": { "is": "Cat" } } } ]""", Codes.UnknownOperator, BindHost.Context(contract: 1));

        error.Message.Should().Be("'is' is not an operator under contract 1; the variant test is a contract 2 operator." + Binder.Contract1Hint);
    }

    [Fact]
    public async Task An_is_condition_renders_its_discriminator_element_and_values()
    {
        var bound = await Bound("""[ { "match": { "car": { "is": "Vehicle" } } } ]""");
        var match = JsonNode.Parse(bound.Canonical)!["stages"]![0]!["match"]!;

        match.ToJsonString().Should().Be("""{"path":"Car","op":"is","element":"_t","operand":{"set":["Truck",null]}}""");
    }

    // ---- flatten -----------------------------------------------------------------------------

    [Fact]
    public async Task Flatten_binds_the_nested_member_and_the_hosts_depth()
    {
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node", "includeIndex": "position" } } ]""");
        var unwind = bound.Stages.OfType<BoundStage.Unwind>().Single();

        unwind.Flatten.Should().Be(new BoundFlatten("children", "Children", 5, 0));
        unwind.As.Should().Be("node");
        bound.PagingMode.Should().Be(PagingMode.Offset);
    }

    [Fact]
    public async Task Flatten_follows_a_merged_member_that_nests_the_base_of_the_element()
    {
        var bound = await Bound("""[ { "unwind": { "path": "blocks", "flatten": "blocks", "as": "block" } } ]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().Flatten!.Storage.Should().Be("Blocks");
    }

    [Fact]
    public async Task Flatten_descends_as_deep_as_the_host_allows()
    {
        var context = BindHost.Context(BindHost.Options(options => options.Limits.MaxFlattenDepth = 3));
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "flatten": "children" } } ]""", context);

        bound.Stages.OfType<BoundStage.Unwind>().Single().Flatten!.Depth.Should().Be(3);
    }

    [Theory]
    [InlineData("nodes", "tags", "'tags' is not a collection of the same items as 'nodes'; flatten follows a member that nests the same kind of element.")]
    [InlineData("nodes", "label", "'label' is not a collection of the same items as 'nodes'; flatten follows a member that nests the same kind of element.")]
    [InlineData("pets", "name", "'name' is not a collection of the same items as 'pets'; flatten follows a member that nests the same kind of element.")]
    public async Task Flatten_on_a_member_that_does_not_nest_the_same_items_is_refused(string path, string flatten, string message)
    {
        var error = await Error($$"""[ { "unwind": { "path": "{{path}}", "flatten": "{{flatten}}" } } ]""", Codes.FlattenNotRecursive);

        error.Message.Should().Be(message);
        error.Path.Should().Be($"{path}.{flatten}");
    }

    [Fact]
    public async Task Flatten_on_a_member_the_element_does_not_have_is_an_unknown_path()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes", "flatten": "branches" } } ]""", Codes.UnknownPath);

        error.Message.Should().Be("'branches' is not a member of the elements of 'nodes'.");
    }

    [Fact]
    public async Task Flatten_is_not_a_member_of_unwind_under_contract_1()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes", "flatten": "children" } } ]""", Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));

        error.Message.Should().Be("'flatten' is not a member of unwind; unwind carries path, as, preserveNull, includeIndex." + Binder.Contract1Hint);
    }

    [Fact]
    public async Task A_flattening_unwind_renders_flatten_and_its_depth_and_a_plain_one_renders_as_under_2_0()
    {
        var flattened = JsonNode.Parse((await Bound("""[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node" } } ]""")).Canonical)!["stages"]![0]!["unwind"]!;
        var plain = JsonNode.Parse((await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } } ]""")).Canonical)!["stages"]![0]!["unwind"]!;

        flattened.ToJsonString().Should().Be("""{"path":"Nodes","as":"node","preserveNull":false,"includeIndex":null,"flatten":"Children","flattenDepth":5}""");
        plain.ToJsonString().Should().Be("""{"path":"Nodes","as":"node","preserveNull":false,"includeIndex":null}""");
    }

    [Fact]
    public void The_flatten_depth_is_at_least_1_and_at_most_the_ceiling()
    {
        var low = new OxQLOptions { Limits = { MaxFlattenDepth = 0 } };
        var high = new OxQLOptions { Limits = { MaxFlattenDepth = 99 } };

        low.Normalise().Should().ContainSingle(line => line.Contains("MaxFlattenDepth"));
        high.Normalise().Should().ContainSingle(line => line.Contains("MaxFlattenDepth"));

        low.Limits.MaxFlattenDepth.Should().Be(1);
        high.Limits.MaxFlattenDepth.Should().Be(LimitOptions.MaxFlattenDepthCeiling);
        new OxQLOptions().Limits.MaxFlattenDepth.Should().Be(5);
    }
}
