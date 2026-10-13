using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// A duration stored as text is compared and ordered by character (the semantics guide says where
/// that agrees with the order of durations). What holds whatever the values are: the comparison
/// has one answer, alone and beside a condition that folds case, which puts the request under a
/// collation.
/// </summary>
[Trait("Category", "Integration")]
public class DurationTextServerTests
{
    private const string Entity = "dt.task";
    private const string Collection = "tmp_dt_tasks";

    public sealed class TaskModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = "";

        public TimeSpan? Took { get; set; }
    }

    private static readonly EngineDirect Direct = new(ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(TaskModel), Collection, null, false)]));

    /// <summary>Durations as the driver writes them, below and from a day on, negative, and text of other spellings a member may hold.</summary>
    private static readonly string[] Stored =
    [
        "00:00:00", "00:30:00", "01:30:00", "10:00:00", "23:59:59", "1.00:00:00", "1.02:00:00", "10.00:00:00", "-00:05:00", "-1.00:00:00", "00:00:00.5000000",
        "PT12H", "P1DT4H", "P1D", "PT0S", "pt1h", "P10D", "-PT5M",
    ];

    private static async Task<(TestDatabase Owned, IQueryEngine Engine)> SeedAsync()
    {
        var owned = await MongoFixture.CreateDatabaseAsync("duration_text");

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(Stored.Select((text, index) => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["Name"] = "task " + index,
            ["Took"] = text,
        }).Append(new BsonDocument { ["_id"] = new BsonBinaryData(Guid.Parse("00000000-0000-0000-0000-000000000099"), GuidRepresentation.Standard), ["OrganizationId"] = EngineDirect.OrganisationValue, ["Name"] = "none" }));

        return (owned, Direct.Engine(await MongoFixture.ClientAsync(), owned.Name));
    }

    private static async Task<List<string?>> TookAsync(IQueryEngine engine, string stages)
    {
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, $$"""[{{stages}}, { "page": { "limit": 100 } }]"""), Direct.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result.Items.Select(row => row!["name"]!.GetValue<string>()).Cast<string?>().ToList();
    }

    private const string Folding = """ "name": { "neq": "NOBODY" } """;

    [Theory]
    [InlineData("gt")]
    [InlineData("gte")]
    [InlineData("lt")]
    [InlineData("lte")]
    public async Task An_ordered_comparison_has_one_answer_alone_and_beside_a_condition_that_folds(string op)
    {
        var (owned, engine) = await SeedAsync();

        await using (owned)
        {
            foreach (var operand in new[] { "PT0S", "PT30M", "PT1H30M", "PT10H", "P1D", "P1DT2H", "P10D", "-PT5M", "-P1D", "PT0.5S" })
            {
                var alone = await TookAsync(engine, $$"""{ "match": { "took": { "{{op}}": "{{operand}}" } } }, { "sort": [{ "id": "asc" }] }""");
                var beside = await TookAsync(engine, $$"""{ "match": { "took": { "{{op}}": "{{operand}}" }, {{Folding}} } }, { "sort": [{ "id": "asc" }] }""");

                beside.Should().Equal(alone, $"{op} {operand}");
            }
        }
    }
}
