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

                // The rows whose text orders so against the operand's text, byte by byte: worked out here from the stored texts.
                var written = System.Xml.XmlConvert.ToTimeSpan(operand).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
                var expected = Stored.Select((text, index) => (Order: string.CompareOrdinal(text, written), Name: "task " + index))
                    .Where(row => op switch { "gt" => row.Order > 0, "gte" => row.Order >= 0, "lt" => row.Order < 0, _ => row.Order <= 0 })
                    .Select(row => row.Name)
                    .ToList();

                alone.Should().Equal(expected, $"{op} {operand}");
                beside.Should().Equal(alone, $"{op} {operand}");
            }
        }
    }

    /// <summary>The same, written out for one operand: what orders after ten hours by its characters.</summary>
    [Fact]
    public async Task What_orders_after_ten_hours_by_character_is_these_rows()
    {
        var (owned, engine) = await SeedAsync();

        await using (owned)
        {
            // After "10:00:00" come "23:59:59" and every text that starts with a letter; a day and more as the driver
            // writes it ("1.00:00:00", "10.00:00:00") and a negative duration order before it.
            (await TookAsync(engine, """{ "match": { "took": { "gt": "PT10H" } } }, { "sort": [{ "id": "asc" }] }"""))
                .Should().Equal("task 4", "task 11", "task 12", "task 13", "task 14", "task 15", "task 16");

            (await TookAsync(engine, """{ "match": { "took": { "lte": "PT0S" } } }, { "sort": [{ "id": "asc" }] }"""))
                .Should().Equal("task 0", "task 8", "task 9", "task 17");
        }
    }
}
