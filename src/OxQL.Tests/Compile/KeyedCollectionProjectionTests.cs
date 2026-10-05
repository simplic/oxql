using FluentAssertions;
using MongoDB.Bson;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// A keyed resolve over a collection reads each element's reference (and what selects its cases)
/// off the page rows, so a projection keeps those members of the collection, and no more of it: the
/// page then carries one key per element, not the elements whole. A projection that names the whole
/// collection as well keeps it whole once: Mongo refuses a projection naming a path and one inside it
/// ("path collision"). As written (contract 1) the collection is kept whole, as it always was.
/// </summary>
public class KeyedCollectionProjectionTests
{
    private static async Task<BsonDocument> ProjectionAsync(string pipeline, bool asWritten = false)
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, ResolveModel.Invoice, pipeline);
        var stages = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000) { AsWritten = asWritten }).PageStages;

        return stages.Single(stage => stage.GetElement(0).Name == "$project")["$project"].AsBsonDocument;
    }

    [Fact]
    public async Task A_join_through_a_collection_keeps_the_key_of_each_element_and_not_the_elements()
    {
        const string Join = """{ "resolve": { "path": "lines.customerId", "as": "c", "elements": "first" } }""";

        (await ProjectionAsync($$"""[{{Join}}, { "project": { "number": 1, "c": 1 } }]""")).Names
            .Should().Equal(["Number", "Lines.CustomerId"], "the keyed fetch reads one key per element; the rest of the elements is not fetched for it");

        (await ProjectionAsync($$"""[{{Join}}, { "project": { "lines.customerId": 1, "c": 1 } }]""")).Names
            .Should().Equal(["Lines.CustomerId"], "the caller asked for the same member: it is named once");

        var whole = await ProjectionAsync($$"""[{{Join}}, { "project": { "lines": 1, "c": 1 } }]""");

        whole.Names.Should().Contain("Lines");
        whole.Names.Should().NotContain(name => name.StartsWith("Lines.", StringComparison.Ordinal), "the collection holds its members; naming both is a path collision");

        // As written, the collection is kept whole and a member the caller names inside it merges into it.
        var written = await ProjectionAsync($$"""[{{Join}}, { "project": { "lines.customerId": 1, "c": 1 } }]""", asWritten: true);

        written.Names.Should().Contain("Lines");
        written.Names.Should().NotContain(name => name.StartsWith("Lines.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_projected_member_beside_no_keyed_collection_stays_as_written()
    {
        var projection = await ProjectionAsync("""[{ "project": { "lines.customerId": 1, "number": 1 } }]""");

        projection.Names.Should().Contain("Lines.CustomerId");
        projection.Names.Should().NotContain("Lines");
    }
}
