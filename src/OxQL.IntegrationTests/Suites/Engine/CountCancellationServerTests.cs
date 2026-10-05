using System.Diagnostics;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.TestCases;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The count beside a page is stopped when the page shows it is not needed, down to the database: the
/// engine cancels the step, the driver gives up the command, and the server ends it. The count here
/// is made slow on purpose (each row it reads sleeps), so left alone it would run for its whole time
/// limit; the test holds that the answer does not wait for it and that the server no longer runs it
/// shortly after.
/// </summary>
[Trait("Category", "Integration")]
public class CountCancellationServerTests
{
    private static readonly EngineDirect Direct = new(LateJoinCases.Model);

    /// <summary>What each row costs the count; left alone it reads every child, so it runs for their number times this.</summary>
    private const int SleepMs = 400;

    /// <summary>The children the count reads: twenty-three, so nothing stopping it would take about nine seconds.</summary>
    private const int Children = 23;

    /// <summary>The host's runner, with a count that sleeps on every row it reads.</summary>
    private sealed class SlowCount(IAggregateRunner inner) : IAggregateRunner
    {
        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            if (stages.Count == 0 || !stages[^1].Contains("$count"))
                return inner.AggregateAsync(entity, stages, options, cancellationToken);

            var slow = new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$function", new BsonDocument
            {
                ["body"] = $"function() {{ sleep({SleepMs}); return true; }}",
                ["args"] = new BsonArray(),
                ["lang"] = "js",
            })));

            return inner.AggregateAsync(entity, [stages[0], slow, .. stages.Skip(1)], options, cancellationToken);
        }
    }

    /// <summary>The commands the server runs under the request's comment.</summary>
    private static async Task<int> RunningAsync(IMongoClient client, string comment)
    {
        var answer = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { ["currentOp"] = 1, ["command.comment"] = comment });

        return answer["inprog"].AsBsonArray.Count;
    }

    [Fact]
    public async Task A_count_the_page_shows_is_not_needed_is_ended_at_the_database_and_not_left_to_its_time_limit()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("count_cancellation");

        foreach (var (name, documents) in LateJoinCases.Collections)
            await owned.Database.GetCollection<BsonDocument>(name).InsertManyAsync(documents.Select(document => document.DeepClone().AsBsonDocument));

        var children = owned.Database.GetCollection<BsonDocument>(Direct.Model.Entities[LateJoinCases.Child].Collection);
        var first = await children.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync();

        await children.InsertManyAsync(Enumerable.Range(0, Children - LateJoinCases.Collections[Direct.Model.Entities[LateJoinCases.Child].Collection].Length).Select(n =>
        {
            var copy = first.DeepClone().AsBsonDocument;

            copy["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);

            return copy;
        }));

        var client = await MongoFixture.ClientAsync();
        var runner = new SlowCount(new MongoAggregateRunner(client, owned.Name));
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(Direct.Model), runner, Direct.Cursors, Direct.Options(), includeErrorDetails: true);
        var comment = "count-cancellation-" + Guid.NewGuid().ToString("N");
        var context = Direct.Context(organisation: LateJoinCases.Organisation) with { CorrelationId = comment };
        var watch = Stopwatch.StartNew();

        // Fewer children than the page takes: the page ends the list, so it is its own count.
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(LateJoinCases.Child, """[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 50, "count": true } } ]"""), context);
        var answered = watch.Elapsed;
        var running = await RunningAsync(client, comment);
        var result = outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;

        result.Items.Should().HaveCount(Children);
        result.PageInfo.Count!.State.Should().Be("exact");
        result.PageInfo.Count.Value.Should().Be(Children);
        answered.Should().BeLessThan(TimeSpan.FromSeconds(3), "the answer does not wait for a count it does not need (left alone it takes about nine seconds)");
        running.Should().Be(1, "the count ran beside the page and was still running at the database when the page came back");

        // The server ends the command when it next looks at its client: long before the count would have finished.
        var gone = Stopwatch.StartNew();

        while (await RunningAsync(client, comment) > 0)
        {
            gone.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the count is ended at the database, not left to read the rest of its rows");
            await Task.Delay(50);
        }
    }
}
