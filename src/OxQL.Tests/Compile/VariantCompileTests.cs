using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Model.Fixtures.Variants;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// What <c>is</c> and <c>unwind.flatten</c> compile to: the discriminator <c>$in</c> (with the
/// concrete base's missing discriminator), the fixed-depth descent for every depth from 1 to 5
/// (pre-order, nested collection removed, the depth probe), the probe kept through projections and
/// groups, and the diagnostic the executor reads off it.
/// </summary>
public class VariantCompileTests
{
    private const string Kennel = VariantModel.Kennel;
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static async Task<CompiledQuery> Compile(string pipeline, int depth = 5)
    {
        var context = BindHost.Context(BindHost.Options(options => options.Limits.MaxFlattenDepth = depth));
        var bound = await BindHost.BoundAsync(VariantModel.Model, Kennel, pipeline, context);

        return MongoCompiler.Compile(bound, Options);
    }

    /// <summary>The filter of the caller's match: the stage after the leading scope match.</summary>
    private static BsonDocument MatchOf(CompiledQuery compiled) => compiled.PageStages[1]["$match"].AsBsonDocument;

    // ---- is ----------------------------------------------------------------------------------

    [Fact]
    public async Task Is_compiles_to_the_discriminator_element_in_the_admitted_values()
    {
        var compiled = await Compile("""[ { "match": { "pet": { "is": "Hound" } } } ]""");

        MatchOf(compiled).ShouldBeBson(BsonDocument.Parse("""{ "Pet._t": { "$in": ["beagle", "Hound"] } }"""));
    }

    [Fact]
    public async Task Is_on_a_collection_without_the_base_is_some_element_by_the_dotted_path()
    {
        var compiled = await Compile("""[ { "match": { "pets": { "is": "Cat" } } } ]""");

        MatchOf(compiled).ShouldBeBson(BsonDocument.Parse("""{ "Pets._t": { "$in": ["Cat"] } }"""));
    }

    [Fact]
    public async Task A_concrete_base_on_an_object_adds_an_object_without_a_discriminator()
    {
        var compiled = await Compile("""[ { "match": { "car": { "is": "Vehicle" } } } ]""");

        MatchOf(compiled).ShouldBeBson(BsonDocument.Parse("""
            { "$or": [ { "Car._t": { "$in": ["Truck"] } }, { "Car": { "$type": "object" }, "Car._t": { "$exists": false } } ] }
            """));
    }

    [Fact]
    public async Task A_concrete_base_on_a_collection_is_one_elements_test()
    {
        var compiled = await Compile("""[ { "match": { "fleet": { "is": "Vehicle" } } } ]""");

        MatchOf(compiled).ShouldBeBson(BsonDocument.Parse("""
            { "Fleet": { "$elemMatch": { "$or": [ { "_t": { "$in": ["Truck"] } }, { "_t": { "$exists": false } } ] } } }
            """));
    }

    [Fact]
    public async Task Is_inside_any_and_on_an_unwound_element_is_relative_to_the_element()
    {
        var any = await Compile("""[ { "match": { "stalls": { "any": { "occupant": { "is": "Cat" } } } } } ]""");
        var unwound = await Compile("""[ { "unwind": { "path": "stalls", "as": "stall" } }, { "match": { "stall.occupant": { "is": "Cat" } } } ]""");

        MatchOf(any).ShouldBeBson(BsonDocument.Parse("""{ "Stalls": { "$elemMatch": { "Occupant._t": { "$in": ["Cat"] } } } }"""));
        unwound.PageStages.Should().Contain(stage => stage.ToJson() == BsonDocument.Parse("""{ "$match": { "stall.Occupant._t": { "$in": ["Cat"] } } }""").ToJson());
    }

    // ---- flatten -----------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Flatten_descends_depth_levels_in_pre_order_removing_the_nested_collection_and_probes_the_last(int depth)
    {
        var compiled = await Compile("""[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node", "includeIndex": "position" } } ]""", depth);
        var stages = compiled.PageStages;

        // scope, the descent, the probe, the unwind, the alias.
        stages.Select(stage => stage.GetElement(0).Name).Take(5).Should().Equal("$match", "$set", "$set", "$unwind", "$set");

        var level = stages[1]["$set"]["Nodes"].AsBsonDocument;
        BsonValue input = "$Nodes";

        for (var index = 1; index <= depth; index++)
        {
            var reduce = level["$reduce"].AsBsonDocument;
            var item = "$$l" + index;

            reduce["input"].ShouldBeBson(new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", input), input, new BsonArray() }));
            reduce["initialValue"].AsBsonArray.Should().BeEmpty();

            var let = reduce["in"]["$let"].AsBsonDocument;
            let["vars"].ShouldBeBson(new BsonDocument("l" + index, "$$this"));

            // Pre-order: what came before, then the item, then its descendants.
            var parts = let["in"]["$concatArrays"].AsBsonArray;
            parts[0].Should().Be(new BsonString("$$value"));

            var self = parts[1].AsBsonArray.Single();

            if (index < depth)
            {
                self.ShouldBeBson(BsonDocument.Parse($$"""{ "$unsetField": { "field": { "$literal": "Children" }, "input": "{{item}}" } }"""));
                parts.Should().HaveCount(3);
                level = parts[2].AsBsonDocument;
                input = item + ".Children";
            }
            else
            {
                // The last level keeps the nested collection for the probe and does not descend.
                self.Should().Be(new BsonString(item));
                parts.Should().HaveCount(2);
            }
        }

        stages[2].ShouldBeBson(BsonDocument.Parse("""
            { "$set": {
                "__oxFlat0": { "$anyElementTrue": [ { "$map": { "input": "$Nodes", "as": "item",
                    "in": { "$gt": [ { "$size": { "$cond": [ { "$isArray": "$$item.Children" }, "$$item.Children", [] ] } }, 0 ] } } } ] },
                "Nodes": { "$map": { "input": "$Nodes", "as": "item", "in": { "$unsetField": { "field": { "$literal": "Children" }, "input": "$$item" } } } } } }
            """));
        stages[3].ShouldBeBson(BsonDocument.Parse("""{ "$unwind": { "path": "$Nodes", "preserveNullAndEmptyArrays": false, "includeArrayIndex": "position" } }"""));
        stages[4].ShouldBeBson(BsonDocument.Parse("""{ "$set": { "node": "$Nodes" } }"""));

        compiled.FlattenProbes.Should().Equal(new FlattenProbe("__oxFlat0", 0, "nodes", depth));
    }

    [Fact]
    public async Task The_count_pipeline_flattens_as_the_page_does()
    {
        var compiled = await Compile("""[ { "unwind": { "path": "nodes", "flatten": "children" } }, { "page": { "includeTotalCount": true } } ]""");

        compiled.CountStages!.Select(stage => stage.GetElement(0).Name).Should().Equal("$match", "$set", "$set", "$unwind", "$limit", "$count");
    }

    [Fact]
    public async Task The_probe_survives_a_projection_and_a_group()
    {
        var projected = await Compile("""[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node" } }, { "project": { "node.label": 1 } } ]""");
        var grouped = await Compile("""[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node" } }, { "group": { "by": [ { "path": "node.label", "as": "label" } ], "fields": { "n": { "count": true } } } } ]""");

        projected.PageStages.Single(stage => stage.Contains("$project"))["$project"]["__oxFlat0"].Should().Be(new BsonInt32(1));

        var group = grouped.PageStages.Single(stage => stage.Contains("$group"))["$group"].AsBsonDocument;
        group["__oxFlat0"].ShouldBeBson(new BsonDocument("$max", "$__oxFlat0"));
        grouped.PageStages.Single(stage => stage.Contains("$project"))["$project"]["__oxFlat0"].Should().Be(new BsonInt32(1));
    }

    [Fact]
    public async Task Each_flattening_unwind_has_its_own_probe()
    {
        var compiled = await Compile("""[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node" } }, { "unwind": { "path": "blocks", "flatten": "blocks", "as": "block" } } ]""");

        compiled.FlattenProbes.Select(probe => (probe.Field, probe.Stage, probe.Path)).Should().Equal(("__oxFlat0", 0, "nodes"), ("__oxFlat1", 1, "blocks"));
    }

    [Fact]
    public async Task A_page_row_with_a_set_probe_is_reported_once_per_unwind_with_path_depth_and_rows()
    {
        var compiled = await Compile("""[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node" } } ]""", depth: 2);
        BsonDocument[] page =
        [
            new() { ["__oxFlat0"] = true },
            new() { ["__oxFlat0"] = false },
            new() { ["__oxFlat0"] = true },
        ];

        var diagnostic = MongoCompiler.DepthTruncations(compiled, page).Should().ContainSingle().Subject;

        diagnostic.Code.Should().Be(Codes.UnwindDepthTruncated);
        diagnostic.Stage.Should().Be(0);
        diagnostic.Path.Should().Be("nodes");
        diagnostic.Message.Should().Be("'nodes' nests items deeper than 2 levels; the items below are not in the rows (2 rows of this page affected).");
        diagnostic.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["path"] = "nodes", ["depth"] = 2, ["rows"] = 2 });

        MongoCompiler.DepthTruncations(compiled, [new BsonDocument { ["__oxFlat0"] = false }]).Should().BeEmpty();
    }
}
