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
/// <c>unwind.keepPath</c>: <c>false</c> takes the unwound collection out of the row once its
/// element is under <c>as</c>. The default keeps it (2.0 wire semantics), the option needs
/// <c>as</c> and a member collection, it is a contract 2 member, and a later path under the
/// dropped collection is an unknown path that says where the element went.
/// </summary>
public class UnwindKeepPathTests
{
    private const string Kennel = VariantModel.Kennel;
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static Task<BoundPipeline> Bound(string pipeline, RequestContext? context = null) =>
        BindHost.BoundAsync(VariantModel.Model, Kennel, pipeline, context);

    private static Task<QueryValidationError> Error(string pipeline, string code, RequestContext? context = null) =>
        BindHost.ErrorAsync(VariantModel.Model, Kennel, pipeline, code, context);

    private static JsonNode UnwindOf(BoundPipeline bound) => JsonNode.Parse(bound.Canonical)!["stages"]![0]!["unwind"]!;

    [Fact]
    public async Task By_default_the_collection_stays_in_the_row_beside_the_alias()
    {
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } }, { "match": { "nodes.label": { "eq": "a" } } } ]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().KeepPath.Should().BeTrue();
        bound.FinalShape.Unset.Should().BeEmpty();
    }

    [Fact]
    public async Task KeepPath_false_binds_and_takes_the_collection_out_of_the_shape()
    {
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": false } }, { "match": { "node.label": { "eq": "a" } } } ]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().KeepPath.Should().BeFalse();
        bound.FinalShape.Unset.Should().Equal(new Dictionary<string, string> { ["nodes"] = "node" });
        bound.FinalShape.IsVisible("nodes").Should().BeFalse();
        bound.FinalShape.IsVisible("nodes.label").Should().BeFalse();
        bound.FinalShape.IsVisible("node.label").Should().BeTrue();
    }

    [Fact]
    public async Task A_later_path_under_the_dropped_collection_says_where_the_element_went()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": false } }, { "match": { "nodes.label": { "eq": "a" } } } ]""", Codes.UnknownPath);

        error.Message.Should().Be("'nodes.label' left the row when 'nodes' was unwound as 'node'; read the element under 'node', or set 'keepPath' to true on that unwind to keep 'nodes'.");
        error.Stage.Should().Be(1);
    }

    [Fact]
    public async Task KeepPath_true_written_is_the_default()
    {
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": true } }, { "match": { "nodes.label": { "eq": "a" } } } ]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().KeepPath.Should().BeTrue();
        UnwindOf(bound).ToJsonString().Should().Be("""{"path":"Nodes","as":"node","preserveNull":false,"includeIndex":null}""");
    }

    [Fact]
    public async Task KeepPath_false_without_as_is_not_applicable()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes", "keepPath": false } } ]""", Codes.OptionNotApplicable);

        error.Message.Should().Be("'keepPath' false takes 'nodes' out of the row once its element is under 'as'; without 'as' the element replaces 'nodes' in place, so there is nothing to drop.");
        error.Path.Should().Be("nodes");
    }

    [Fact]
    public async Task KeepPath_is_not_a_member_of_unwind_under_contract_1()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": false } } ]""", Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));

        error.Message.Should().Be("'keepPath' is not a member of unwind; unwind carries path, as, preserveNull, includeIndex." + Binder.Contract1Hint);
    }

    [Fact]
    public async Task The_contract_2_member_list_names_keepPath()
    {
        var error = await Error("""[ { "unwind": { "path": "nodes", "keep": false } } ]""", Codes.UnknownStageMember);

        error.Message.Should().Be("'keep' is not a member of unwind; unwind carries path, as, preserveNull, includeIndex, flatten, keepPath.");
    }

    [Fact]
    public async Task KeepPath_false_renders_in_the_canonical_form_and_the_default_renders_as_under_2_0()
    {
        var dropped = UnwindOf(await Bound("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": false } } ]"""));
        var kept = UnwindOf(await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } } ]"""));

        dropped.ToJsonString().Should().Be("""{"path":"Nodes","as":"node","preserveNull":false,"includeIndex":null,"keepPath":false}""");
        kept.ToJsonString().Should().Be("""{"path":"Nodes","as":"node","preserveNull":false,"includeIndex":null}""");
    }

    [Fact]
    public async Task KeepPath_false_compiles_to_an_unset_of_the_collection_after_the_alias_is_set()
    {
        var compiled = MongoCompiler.Compile(await Bound("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": false } } ]"""), Options);
        var stages = compiled.PageStages.Select(stage => stage.ToString()).ToList();

        var set = stages.FindIndex(stage => stage.Contains("\"$set\" : { \"node\" : \"$Nodes\" }"));
        var unset = stages.FindIndex(stage => stage.Contains("\"$unset\" : \"Nodes\""));

        set.Should().BeGreaterThan(0);
        unset.Should().Be(set + 1);
    }

    [Fact]
    public async Task The_default_compiles_without_an_unset()
    {
        var compiled = MongoCompiler.Compile(await Bound("""[ { "unwind": { "path": "nodes", "as": "node" } } ]"""), Options);

        compiled.PageStages.Should().NotContain(stage => stage.Contains("$unset") && stage["$unset"] == new BsonString("Nodes"));
    }

    [Fact]
    public async Task A_projection_after_it_keeps_the_collection_out()
    {
        var bound = await Bound("""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": false } }, { "project": { "node": 1 } } ]""");

        bound.FinalShape.IsVisible("nodes").Should().BeFalse();
        bound.FinalShape.IsVisible("node").Should().BeTrue();
    }

    [Theory]
    [InlineData("\"false\"")]
    [InlineData("0")]
    [InlineData("null")]
    public async Task KeepPath_other_than_true_or_false_is_refused(string value)
    {
        var error = await Error($$"""[ { "unwind": { "path": "nodes", "as": "node", "keepPath": {{value}} } } ]""", Codes.InvalidOperand);

        error.Message.Should().Be("'keepPath' is true or false.");
    }

    [Fact]
    public async Task KeepPath_false_is_refused_on_a_collection_an_earlier_join_reads_its_keys_from()
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, ResolveModel.Invoice,
            """[{ "resolve": { "path": "lines.customerId", "as": "c", "elements": "first" } }, { "unwind": { "path": "lines", "as": "line", "keepPath": false } }]""", Codes.OptionNotApplicable);

        error.Message.Should().Contain("the join 'c' of an earlier stage reads its keys from it");
        error.Stage.Should().Be(1);
    }

    [Fact]
    public async Task KeepPath_false_binds_when_the_earlier_join_reads_outside_the_collection()
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, ResolveModel.Invoice,
            """[{ "resolve": { "path": "customerId", "as": "c" } }, { "unwind": { "path": "lines", "as": "line", "keepPath": false } }]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().KeepPath.Should().BeFalse();
    }
}
