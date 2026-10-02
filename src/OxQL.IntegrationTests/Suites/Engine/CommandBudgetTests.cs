using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// What a request costs the database, counted: the commands the engine sends (aggregate, getMore,
/// listIndexes) for the requests a grid and a report send, cold and warm, against a ceiling. A
/// change that adds a round trip to one of them fails here instead of showing up as latency against
/// a hosted database.
/// <para>
/// Each case runs on a fleet of its own, so its caches are cold and no other test's commands reach
/// its databases; the commands are read off the run's client (<see cref="MongoFixture.Commands"/>).
/// The test addon source reads its definitions with an uncached <c>find</c> on every bind, which a
/// production host caches, so those reads are not the engine's and are not counted.
/// </para>
/// <para>
/// <c>OXQL_COMMAND_REPORT=&lt;file&gt;</c> appends what each case saw (the commands and the most of
/// them one database answered one after another) to that file.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public partial class CommandBudgetTests
{
    private const string ReportVariable = "OXQL_COMMAND_REPORT";

    private static readonly object ReportGate = new();

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>Sends the request twice on a fresh fleet and holds both runs against their ceilings; no run may cost a getMore.</summary>
    private static async Task MeasureAsync(string label, Func<CorpusFleet, Task<WireAnswer>> send, int cold, int warm, Func<LabFleet, Task>? extra = null)
    {
        await using var fleet = await CorpusFleet.CreateAsync("commands_" + label, extra: extra);
        using var capture = await MongoFixture.Commands.WatchAsync(fleet.Fleet);

        var first = await send(fleet);
        var coldRun = capture.Drain();
        var second = await send(fleet);
        var warmRun = capture.Drain();

        Report(label, "cold", coldRun);
        Report(label, "warm", warmRun);

        first.StatusCode.Should().Be(200, first.Text);
        second.StatusCode.Should().Be(200, second.Text);
        coldRun.Count("getMore").Should().Be(0, $"no page or keyed fetch pays a second round trip for its rows; cold: {coldRun}");
        warmRun.Count("getMore").Should().Be(0, $"no page or keyed fetch pays a second round trip for its rows; warm: {warmRun}");
        coldRun.Engine.Count.Should().BeLessThanOrEqualTo(cold, $"cold: {coldRun}");
        warmRun.Engine.Count.Should().BeLessThanOrEqualTo(warm, $"warm: {warmRun}");
    }

    private static void Report(string label, string run, CommandBatch batch)
    {
        if (Environment.GetEnvironmentVariable(ReportVariable) is not { Length: > 0 } file)
            return;

        var perDatabase = batch.Engine
            .GroupBy(command => command.Database)
            .Select(group => $"{Service(group.Key)}={group.Count()}/depth {batch.DepthByDatabase[group.Key]}")
            .OrderBy(text => text, StringComparer.Ordinal);
        var line = $"{label} | {run} | engine={batch.Engine.Count} | aggregate={batch.Count("aggregate")} getMore={batch.Count("getMore")} listIndexes={batch.Count("listIndexes")} | depth={batch.Depth} | {string.Join(", ", perDatabase)}";

        lock (ReportGate)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    /// <summary>The service a fleet database belongs to: its name ends in the service key and a sequence number.</summary>
    private static string Service(string database) =>
        LabService.All.Select(service => service.Key).FirstOrDefault(key => database.Contains($"_{key}_", StringComparison.Ordinal)) ?? database;

    /// <summary>
    /// A page above the driver's default first batch (101 documents) arrives in the one reply of its
    /// aggregate: the cursor's batch is sized to the page, so no <c>getMore</c> follows. The count is
    /// the second command.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(250)]
    [InlineData(500)]
    public Task S8_a_page_above_the_default_first_batch_costs_no_getMore(int limit) => MeasureAsync(
        "S8_" + limit,
        async fleet =>
        {
            var answer = await fleet.Client(LabService.Transport, Org.C).SendAsync(Corpus.Template,
                $$"""[ { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1 } }, { "page": { "limit": {{limit}}, "includeTotalCount": true } } ]""");

            answer.Items.Should().HaveCount(limit);

            return answer;
        },
        cold: 2,
        warm: 2,
        extra: SeedTemplatesAsync);

    /// <summary>600 thin shipment templates in organisation C: more than the largest page asks for.</summary>
    private static async Task SeedTemplatesAsync(LabFleet fleet)
    {
        var collection = (await fleet.DatabaseAsync(LabService.Transport)).GetCollection<BsonDocument>(Corpus.Entity(Corpus.Template).Collection);
        var at = new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var organisation = new BsonBinaryData(Org.C.Id(), GuidRepresentation.Standard);

        await collection.InsertManyAsync(Enumerable.Range(1, 600).Select(n => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(BulkRows.IdOf(n), GuidRepresentation.Standard),
            ["OrganizationId"] = organisation,
            ["TemplateName"] = BulkRows.NameOf(n),
            ["ShipmentNumber"] = $"ZB-{n:D6}",
            ["IsShipmentConversionDisabled"] = false,
            ["Items"] = new BsonArray(),
            ["Documents"] = new BsonArray(),
            ["Tags"] = new BsonArray(),
            ["BillingLines"] = new BsonArray(),
            ["CreateDateTime"] = at,
            ["UpdateDateTime"] = at,
            ["CreateUserName"] = "bulk",
            ["UpdateUserName"] = "bulk",
            ["IsDeleted"] = false,
        }), new InsertManyOptions { IsOrdered = false });
    }
}
