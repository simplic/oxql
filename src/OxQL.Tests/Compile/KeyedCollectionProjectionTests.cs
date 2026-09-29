using FluentAssertions;
using MongoDB.Bson;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// A keyed resolve over a collection keeps the whole collection through every projection, since the
/// keyed fetch reads its elements off the page rows. A projection that names a member inside that
/// collection too (the owning row's <c>tours.tourId</c> beside a continued resolve of it) merges into
/// the collection: Mongo refuses a projection naming a path and one inside it ("path collision").
/// </summary>
public class KeyedCollectionProjectionTests
{
    private static async Task<BsonDocument> ProjectionAsync(string pipeline)
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, ResolveModel.Invoice, pipeline);
        var stages = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)).PageStages;

        return stages.Single(stage => stage.GetElement(0).Name == "$project")["$project"].AsBsonDocument;
    }

    [Theory]
    [InlineData("""[{ "resolve": { "path": "lines.customerId", "as": "c", "elements": "first" } }, { "project": { "lines.customerId": 1, "c": 1 } }]""")]
    public async Task A_projected_member_inside_a_collection_a_keyed_resolve_keeps_merges_into_the_collection(string pipeline)
    {
        var projection = await ProjectionAsync(pipeline);

        projection.Names.Should().Contain("Lines");
        projection.Names.Should().NotContain(name => name.StartsWith("Lines.", StringComparison.Ordinal), "the collection holds them; naming both is a path collision");
    }

    [Fact]
    public async Task A_projected_member_beside_no_keyed_collection_stays_as_written()
    {
        var projection = await ProjectionAsync("""[{ "project": { "lines.customerId": 1, "number": 1 } }]""");

        projection.Names.Should().Contain("Lines.CustomerId");
        projection.Names.Should().NotContain("Lines");
    }
}
