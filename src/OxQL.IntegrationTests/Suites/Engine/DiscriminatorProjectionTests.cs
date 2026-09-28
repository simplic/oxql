using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// A projection or a join's select of a member below a polymorphic object keeps the object's
/// discriminator in a contract 2 query (RE-16), so the row is encoded by the variant it is stored as;
/// the discriminator itself is not a member and never reaches the wire row.
/// </summary>
[Trait("Category", "Integration")]
public class DiscriminatorProjectionTests
{
    private static readonly EngineDirect Transport = new(LabService.Transport.Model);

    private static BsonDocument ProjectionOf(CompiledQuery compiled) =>
        compiled.PageStages.Single(stage => stage.Contains("$project"))["$project"].AsBsonDocument;

    [Fact]
    public async Task A_member_below_a_polymorphic_resource_keeps_the_resources_discriminator()
    {
        const string Pipeline = """[{ "project": { "attachedResources.resource.matchCode": 1 } }]""";

        var kept = ProjectionOf(await Transport.CompileAsync("transport.tour", Pipeline, new CompileOptions(10_000, null, 100_000) { KeepDiscriminators = true }));
        var plain = ProjectionOf(await Transport.CompileAsync("transport.tour", Pipeline));

        kept.Names.Should().Contain("AttachedResources.Resource.MatchCode").And.Contain("AttachedResources.Resource._t");
        plain.Names.Should().NotContain("AttachedResources.Resource._t", "a contract 1 row is the document as stored");
    }

    [Fact]
    public async Task The_polymorphic_object_projected_whole_needs_nothing_more()
    {
        var kept = ProjectionOf(await Transport.CompileAsync("transport.tour", """[{ "project": { "attachedResources.resource": 1 } }]""", new CompileOptions(10_000, null, 100_000) { KeepDiscriminators = true }));

        kept.Names.Should().NotContain(name => name.EndsWith("._t", StringComparison.Ordinal));
    }
}
