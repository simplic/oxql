using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.TestCases;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The late-join cases (<see cref="LateJoinCases"/>, shared with the unit tests, which run them
/// over an evaluator of the emitted stages) on a real server: the whole engine over a database
/// the case owns. Moved from the unit project, where it ran only when a server was named by hand.
/// </summary>
[Trait("Category", "Integration")]
public class LateJoinServerTests
{
    private static readonly EngineDirect Engine = new(LateJoinCases.Model);

    [Theory]
    [MemberData(nameof(LateJoinCases.Cases), MemberType = typeof(LateJoinCases))]
    public async Task A_join_after_the_page_still_reads_its_key_on_a_server(string because, string entity, string pipeline, string expected)
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("late_join");

        foreach (var (name, documents) in LateJoinCases.Collections)
            await owned.Database.GetCollection<BsonDocument>(name).InsertManyAsync(documents.Select(document => document.DeepClone().AsBsonDocument));

        var client = await MongoFixture.ClientAsync();
        var rows = await Engine.ExecuteAsync(Engine.Engine(client, owned.Name), entity, pipeline, Engine.Context(organisation: LateJoinCases.Organisation));

        rows.ToJsonString().Should().Be(JsonNode.Parse(expected)!.ToJsonString(), because);
    }
}
