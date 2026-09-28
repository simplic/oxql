using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Model.Fixtures.Variants;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// A polymorphic value is encoded by the variant its discriminator names: a member the variants
/// store under different names (unstored on the merged type) reaches the row, a hierarchical
/// discriminator is read by its last value, and a value without one is read by the type itself.
/// </summary>
public class VariantEncoderTests
{
    private static async Task<JsonObject> Encode(BsonDocument row, string pipeline = """[ { "project": { "pet": 1, "pets": 1, "car": 1 } } ]""")
    {
        var bound = await BindHost.BoundAsync(VariantModel.Model, VariantModel.Kennel, pipeline);

        return WireEncoder.Encode(row, bound);
    }

    [Fact]
    public async Task A_member_the_variants_store_under_different_names_is_read_by_the_variant()
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["Pet"] = new BsonDocument { ["_t"] = new BsonArray { "Pet", "Cat" }, ["Name"] = "Tom", ["Indoor"] = true, ["ct"] = "C-1" },
            ["Pets"] = new BsonArray
            {
                new BsonDocument { ["_t"] = new BsonArray { "Pet", "Hound", "beagle" }, ["Name"] = "Rex", ["Barks"] = true, ["ht"] = "H-1", ["Weeks"] = 9 },
                new BsonDocument { ["_t"] = new BsonArray { "Pet", "Cat" }, ["Name"] = "Kit", ["Indoor"] = false, ["ct"] = "C-2" },
            },
        };

        var encoded = await Encode(row);

        ShouldEqual(encoded["pet"], """{"name":"Tom","indoor":true,"tag":"C-1"}""");
        ShouldEqual(encoded["pets"]![0], """{"name":"Rex","barks":true,"tag":"H-1","weeks":9}""");
        ShouldEqual(encoded["pets"]![1], """{"name":"Kit","indoor":false,"tag":"C-2"}""");
    }

    /// <summary>The same members and values; the order is the merged type's, which the model decides.</summary>
    private static void ShouldEqual(JsonNode? actual, string expected) =>
        JsonNode.DeepEquals(actual, JsonNode.Parse(expected)).Should().BeTrue($"{actual?.ToJsonString()} should equal {expected}");

    [Fact]
    public void The_merged_type_alone_leaves_the_member_out()
    {
        // The model: the variants disagree on where tag is stored, so the merged member is unstored.
        var pet = VariantModel.Model.Entities[VariantModel.Kennel].Root.Member("pet")!.Type!;

        pet.Member("tag")!.Stored.Should().BeFalse();
    }

    [Fact]
    public async Task A_value_without_a_discriminator_or_with_an_unknown_one_is_read_by_the_type()
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["Car"] = new BsonDocument { ["Plate"] = "AB-1" },
            ["Pet"] = new BsonDocument { ["_t"] = "Parrot", ["Name"] = "Polly", ["ct"] = "P-1" },
        };

        var encoded = await Encode(row);

        encoded["car"]!.ToJsonString().Should().Be("""{"plate":"AB-1"}""");
        encoded["pet"]!.ToJsonString().Should().Be("""{"name":"Polly"}""");
    }

    [Fact]
    public async Task A_scalar_discriminator_names_the_variant()
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["Car"] = new BsonDocument { ["_t"] = "Truck", ["Plate"] = "TR-1", ["Axles"] = 3 },
        };

        (await Encode(row))["car"]!.ToJsonString().Should().Be("""{"plate":"TR-1","axles":3}""");
    }

    [Fact]
    public async Task A_flattened_element_is_encoded_as_the_element_without_its_nested_collection()
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["Blocks"] = new BsonDocument { ["_t"] = "Paragraph", ["Title"] = "P", ["Text"] = "body" },
            ["block"] = new BsonDocument { ["_t"] = "Paragraph", ["Title"] = "P", ["Text"] = "body" },
            ["__oxFlat0"] = true,
        };

        var encoded = await Encode(row, """[ { "unwind": { "path": "blocks", "flatten": "blocks", "as": "block" } }, { "project": { "block": 1 } } ]""");

        encoded["block"]!.ToJsonString().Should().Be("""{"title":"P","text":"body"}""");
        encoded.Select(pair => pair.Key).Should().NotContain(key => key.StartsWith("__", StringComparison.Ordinal), "the probe never reaches the wire row");
    }
}
