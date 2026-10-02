using System.Collections.Concurrent;
using FluentAssertions;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// Explain never executes the query (DESIGN §4.1): a command monitor on the engine's own client
/// watches every command sent to the fleet database. Without <c>include</c> explain sends none;
/// with <c>include: ["indexes"]</c> it sends <c>listIndexes</c> and nothing else — no
/// <c>aggregate</c>, no <c>explain</c> (the <c>executionStats</c> explain ran the page pipeline).
/// The same query executed shows the monitor sees the <c>aggregate</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ExplainNeverExecutesTests
{
    private static readonly string LookupAndCount = """
        [ { "match": { "id": { "neq": null } } },
          { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "equipment", "select": ["name"] } },
          { "sort": [ { "id": "asc" } ] },
          { "page": { "limit": 5, "includeTotalCount": true } } ]
        """;

    /// <summary>The whole engine over the shared fleet database on a client whose every command to that database is recorded.</summary>
    private static async Task<(MongoQueryEngine Engine, EngineDirect Direct, ConcurrentQueue<string> Commands)> MonitoredAsync()
    {
        var database = (await (await CorpusFleet.SharedAsync()).Fleet.DatabaseAsync(LabService.Fleet)).DatabaseNamespace.DatabaseName;
        var settings = (await MongoFixture.ClientAsync()).Settings.Clone();
        var commands = new ConcurrentQueue<string>();

        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(started =>
        {
            if (started.DatabaseNamespace?.DatabaseName == database)
                commands.Enqueue(started.CommandName);
        });

        var client = new MongoClient(settings);
        var direct = new EngineDirect(LabService.Fleet.Model);
        var engine = new MongoQueryEngine(
            new StaticEntityModelProvider(direct.Model),
            new MongoAggregateRunner(client, database),
            direct.Cursors,
            direct.Options(),
            includeErrorDetails: true,
            indexes: new MongoIndexSource(client, database));

        return (engine, direct, commands);
    }

    private static ExplainResult Answer(ExplainOutcome outcome) =>
        outcome.Should().BeOfType<ExplainOutcome.Success>(outcome is ExplainOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;

    [Fact]
    public async Task X01_explain_of_a_lookup_with_a_count_sends_no_command_and_the_same_query_executed_sends_the_aggregate()
    {
        var (engine, direct, commands) = await MonitoredAsync();
        var request = new ExplainRequest { Query = EngineDirect.Request(Corpus.Vehicle, LookupAndCount), Include = [ExplainRequest.IncludeShape, ExplainRequest.IncludeNotes, ExplainRequest.IncludePlan] };

        var answer = Answer(await engine.ExplainAsync(request, direct.Context()));

        answer.Valid.Should().BeTrue(string.Join("; ", answer.Errors.Select(error => error.Code + ": " + error.Message)));
        answer.Plan!.Stages.Should().Contain(stage => stage.AsObject().ContainsKey("$lookup"));
        answer.Types.Should().NotBeNull("the shape is answered from the model alone");
        answer.Advisory.Should().BeNull("the advisory is opt-in");
        commands.Should().BeEmpty("explain without include reads nothing from the database");

        await direct.ExecuteAsync(engine, Corpus.Vehicle, LookupAndCount);

        commands.Should().Contain("aggregate", "the monitor sees what the query path sends");
    }

    [Fact]
    public async Task X02_with_include_indexes_explain_sends_listIndexes_for_the_entity_and_the_joined_collection_and_nothing_else()
    {
        var (engine, direct, commands) = await MonitoredAsync();
        var request = new ExplainRequest { Query = EngineDirect.Request(Corpus.Vehicle, LookupAndCount), Include = [ExplainRequest.IncludeIndexes] };

        var answer = Answer(await engine.ExplainAsync(request, direct.Context()));

        answer.Valid.Should().BeTrue();
        answer.Advisory.Should().NotBeNull().And.Contain(line => line["field"]!.GetValue<string>() == "lookup:equipment");
        commands.Should().Equal(["listIndexes", "listIndexes"], "one index list for fleet.vehicle, one for the joined fleet.equipment; no aggregate, no explain");
    }

    [Fact]
    public async Task X03_a_query_that_does_not_bind_is_answered_valid_false_without_any_command()
    {
        var (engine, direct, commands) = await MonitoredAsync();
        var request = new ExplainRequest
        {
            Query = EngineDirect.Request(Corpus.Vehicle, """[ { "match": { "nothing": { "eq": 1 } } }, { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "equipment" } } ]"""),
            Include = [ExplainRequest.IncludeIndexes],
        };

        var answer = Answer(await engine.ExplainAsync(request, direct.Context()));

        answer.Valid.Should().BeFalse();
        answer.Errors.Select(error => error.Code).Should().Equal("UNKNOWN_PATH");
        answer.Stages.Select(stage => stage.Status).Should().Equal("error", "ok");
        answer.Plan.Should().BeNull();
        commands.Should().BeEmpty("a query that does not bind is not compiled, so not even its indexes are read");
    }

    [Fact]
    public async Task X04_listIndexes_carries_a_server_side_time_limit_and_a_collection_that_does_not_exist_has_no_indexes()
    {
        var database = (await (await CorpusFleet.SharedAsync()).Fleet.DatabaseAsync(LabService.Fleet)).DatabaseNamespace.DatabaseName;
        var settings = (await MongoFixture.ClientAsync()).Settings.Clone();
        var limits = new ConcurrentQueue<long?>();

        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(started =>
        {
            if (started.CommandName == "listIndexes" && started.DatabaseNamespace?.DatabaseName == database)
                limits.Enqueue(started.Command.TryGetValue("maxTimeMS", out var limit) ? limit.ToInt64() : null);
        });

        var source = new MongoIndexSource(new MongoClient(settings), database, maxTime: TimeSpan.FromMilliseconds(750));
        var model = LabService.Fleet.Model;

        model.TryResolve(Corpus.Vehicle, out var vehicle, out _).Should().BeTrue();

        var listed = await source.IndexesAsync(vehicle, CancellationToken.None);

        listed.Should().Contain(index => index["name"] == "_id_", "the list is the server's, read off the one reply");
        limits.Should().Equal([750L], "a caller that stops waiting does not leave the command running on the server");

        // A source over a database nothing was written to: the collection does not exist.
        var empty = new MongoIndexSource(new MongoClient(settings), MongoFixture.DatabaseName("no_such_collection"));

        (await empty.IndexesAsync(vehicle, CancellationToken.None)).Should().BeEmpty();
    }
}
