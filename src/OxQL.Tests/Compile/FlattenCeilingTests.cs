using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Model.Fixtures.Variants;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// Every flatten level is one more nested expression; at the ceiling <c>MaxFlattenDepth</c> is
/// clamped to, the flattening pipeline still serializes inside the aggregate command.
/// </summary>
public class FlattenCeilingTests
{
    [Fact]
    public async Task A_flatten_at_the_ceiling_compiles_and_serializes()
    {
        var options = BindHost.Options(options => options.Limits.MaxFlattenDepth = LimitOptions.MaxFlattenDepthCeiling);
        var bound = await BindHost.BoundAsync(VariantModel.Model, VariantModel.Kennel, """[ { "unwind": { "path": "nodes", "flatten": "children", "as": "node" } } ]""", BindHost.Context(options));
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(10_000, null, 100_000));

        new BsonDocument { ["aggregate"] = "c", ["pipeline"] = new BsonArray(compiled.PageStages), ["cursor"] = new BsonDocument() }.ToBson().Should().NotBeEmpty();
    }

    [Fact]
    public void A_configured_depth_above_the_ceiling_is_clamped_to_it()
    {
        var options = new OxQLOptions();
        options.Limits.MaxFlattenDepth = 50;

        options.Normalise();

        options.Limits.MaxFlattenDepth.Should().Be(LimitOptions.MaxFlattenDepthCeiling);
    }
}
