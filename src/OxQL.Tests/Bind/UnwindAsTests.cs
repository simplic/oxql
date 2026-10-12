using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind.Fixtures.Resolve;
using OxQL.Tests.Model.Fixtures.Variants;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The contract 2 unwind: it names its element under <c>as</c>, and the collection leaves
/// the row, so a path names one thing along the whole pipeline: the collection before the unwind,
/// the element's alias after it. A later path under the collection is an unknown path that says
/// where the element went. There is nothing to choose: <c>keepPath</c> is not a member. Contract 1
/// unwinds in place or beside the alias, as it always has.
/// </summary>
public class UnwindAsTests
{
    private const string Kennel = VariantModel.Kennel;
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static Task<BoundPipeline> Bound(string pipeline, RequestContext? context = null) =>
        BindHost.BoundAsync(VariantModel.Model, Kennel, pipeline, context);

    private static Task<QueryValidationError> Error(string pipeline, string code, RequestContext? context = null) =>
        BindHost.ErrorAsync(VariantModel.Model, Kennel, pipeline, code, context);

    private static JsonNode UnwindOf(BoundPipeline bound) => JsonNode.Parse(bound.Canonical)!["stages"]![0]!["unwind"]!;

    [Fact]
    public async Task The_collection_leaves_the_shape_and_the_element_is_under_the_alias()
    {
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } }, { "match": { "node.label": { "eq": "a" } } } ]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().KeepPath.Should().BeFalse();
        bound.FinalShape.Unset.Should().Equal(new Dictionary<string, string> { ["nodes"] = "node" });
        bound.FinalShape.IsVisible("nodes").Should().BeFalse();
        bound.FinalShape.IsVisible("nodes.label").Should().BeFalse();
        bound.FinalShape.IsVisible("node.label").Should().BeTrue();
    }

    [Fact]
    public async Task A_later_path_under_the_collection_says_where_the_element_went()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes", "as": "node" } }, { "match": { "nodes.label": { "eq": "a" } } } ]""", Codes.UnknownPath);

        error.Message.Should().Be("'nodes.label' left the row when 'nodes' was unwound as 'node'; read the element under 'node'.");
        error.Stage.Should().Be(1);
        JsonSerializer.Serialize(error.Params).Should().Be("""{"reason":"unwound","collection":"nodes","alias":"node"}""");

        // Before the unwind the path means some element of the collection, as it always has.
        await Bound("""[ { "match": { "nodes.label": { "eq": "a" } } }, { "unwind": { "path": "nodes", "as": "node" } } ]""");
    }

    [Fact]
    public async Task An_unwind_without_as_is_refused_and_names_the_member()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes" } } ]""", Codes.InvalidAlias);

        error.Message.Should().Be("An unwind names its element under 'as': after it, 'nodes' is not in the row any more and each row holds one element under that name.");
        error.Path.Should().Be("nodes");
        error.Stage.Should().Be(0);
        JsonSerializer.Serialize(error.Params).Should().Be("""{"stage":"unwind","member":"as","reason":"required"}""");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public async Task KeepPath_is_not_a_member_of_unwind(string value)
    {
        var error = await Error($$"""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": {{value}} } } ]""", Codes.UnknownStageMember);

        error.Message.Should().Be("'keepPath' is not a member of unwind; unwind carries path, as, preserveNull, includeIndex, flatten. An unwind always takes its collection out of the row; the element is under 'as'.");
        JsonSerializer.Serialize(error.Params).Should().Be("""{"stage":"unwind","member":"keepPath","reason":"removed"}""");

        (await Error("""[ { "unwind": { "path": "nodes", "keep": false } } ]""", Codes.UnknownStageMember))
            .Message.Should().Be("'keep' is not a member of unwind; unwind carries path, as, preserveNull, includeIndex, flatten.");
    }

    [Fact]
    public async Task Contract_1_unwinds_in_place_or_beside_the_alias_and_never_had_keepPath()
    {
        var legacy = BindHost.Context(contract: 1);
        var inPlace = await BindHost.BoundAsync(BindHost.Probe, "probe.order", """[ { "unwind": { "path": "items" } }, { "match": { "items.quantity": { "gt": 1 } } } ]""", legacy);

        inPlace.Stages.OfType<BoundStage.Unwind>().Single().Should().Match<BoundStage.Unwind>(unwind => unwind.KeepPath && unwind.As == null);
        inPlace.FinalShape.Unset.Should().BeEmpty();

        var error = await Error("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": false } } ]""", Codes.LegacyStageUnsupported, legacy);

        error.Message.Should().Be("'keepPath' is not a member of unwind; unwind carries path, as, preserveNull, includeIndex." + Binder.Contract1Hint);
    }

    [Fact]
    public async Task The_canonical_form_says_nothing_of_the_collection_because_there_is_nothing_to_choose()
    {
        UnwindOf(await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } } ]""")).ToJsonString()
            .Should().Be("""{"path":"Nodes","as":"node","preserveNull":false,"includeIndex":null}""");
    }

    [Fact]
    public async Task It_compiles_to_an_unset_of_the_collection_after_the_alias_is_set()
    {
        var compiled = MongoCompiler.Compile(await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } } ]"""), Options);
        var stages = compiled.PageStages.Select(stage => stage.ToString()).ToList();

        var set = stages.FindIndex(stage => stage.Contains("\"$set\" : { \"node\" : \"$Nodes\" }"));
        var unset = stages.FindIndex(stage => stage.Contains("\"$unset\" : \"Nodes\""));

        set.Should().BeGreaterThan(0);
        unset.Should().Be(set + 1);
    }

    [Fact]
    public async Task A_projection_after_it_keeps_the_collection_out()
    {
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } }, { "project": { "node": 1 } } ]""");

        bound.FinalShape.IsVisible("nodes").Should().BeFalse();
        bound.FinalShape.IsVisible("node").Should().BeTrue();
    }

    [Fact]
    public async Task A_lookups_alias_leaves_the_row_too_and_its_row_is_under_the_unwinds_alias()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, "probe.customer",
            """[ { "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "unwind": { "path": "orders", "as": "order" } }, { "match": { "order.number": { "eq": "x" } } } ]""");

        bound.FinalShape.Carries("orders").Should().BeFalse();
        bound.FinalShape.Carries("order").Should().BeTrue();

        var error = await BindHost.ErrorAsync(BindHost.Probe, "probe.customer",
            """[ { "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "unwind": { "path": "orders", "as": "order" } }, { "match": { "orders.number": { "eq": "x" } } } ]""", Codes.UnknownPath);

        error.Message.Should().Be("'orders.number' left the row when 'orders' was unwound as 'order'; read the element under 'order'.");
    }

    [Fact]
    public async Task It_is_refused_on_a_collection_an_earlier_join_reads_its_keys_from()
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, ResolveModel.Invoice,
            """[{ "resolve": { "path": "lines.customerId", "as": "c", "elements": "first" } }, { "unwind": { "path": "lines", "as": "line" } }]""", Codes.UnwindOrder);

        error.Message.Should().Be("The unwind takes 'lines' out of the row, but the join 'c' of an earlier stage reads its keys from it; join after the unwind, through 'line'.");
        error.Stage.Should().Be(1);
        JsonSerializer.Serialize(error.Params).Should().Be("""{"reason":"joinReadsCollection","join":"c","alias":"line"}""");

        // The same join after the unwind reads the element's own key.
        await BindHost.BoundAsync(ResolveModel.Model, ResolveModel.Invoice,
            """[{ "unwind": { "path": "lines", "as": "line" } }, { "resolve": { "path": "line.customerId", "as": "c" } }]""");
    }

    [Fact]
    public async Task It_binds_when_the_earlier_join_reads_outside_the_collection()
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, ResolveModel.Invoice,
            """[{ "resolve": { "path": "customerId", "as": "c" } }, { "unwind": { "path": "lines", "as": "line" } }]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().KeepPath.Should().BeFalse();
    }
}
