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
/// engine cancels the step, the driver gives up the command, and the server ends it.
/// <para>
/// Nothing but the cancellation can end the count here. It is made slow on purpose (each row it
/// reads sleeps), so that reading its rows takes longer than its time limit, and its time limit is
/// the longest the engine allows, a minute. The page is held back until the server reports the
/// count as running, so the count is in flight at the database when the page comes back, whatever
/// the machine's load. What is then held: the count's own task ends cancelled (it would end in a
/// timeout had the engine waited for it, and complete had it been left alone), and the server stops
/// running it long before its minute is over.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class CountCancellationServerTests
{
    private static readonly EngineDirect Direct = new(LateJoinCases.Model);

    /// <summary>What each row costs the count: short, since the server ends a cancelled count only once the row it reads is done.</summary>
    private const int SleepMs = 300;

    /// <summary>The children the count reads: two hundred and thirty, so reading them all takes 69 seconds, longer than the count may run.</summary>
    private const int Children = 230;

    /// <summary>The page's limit: more than the children, so the page ends the list and is its own count.</summary>
    private const int PageLimit = 250;

    /// <summary>The count's time limit: the longest the engine allows a request, so the test's bounds are far below it.</summary>
    private const int BudgetMs = 60_000;

    /// <summary>How long the test waits for something that has to happen; a count nothing ended would still be running then.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The host's runner with a count that sleeps on every row it reads. It says when the count was
    /// sent and how its task ended, and sends the page only once the server runs the count.
    /// </summary>
    private sealed class SlowCount(IAggregateRunner inner, Func<Task<int>> running) : IAggregateRunner
    {
        /// <summary>How the count's task ended: <c>cancelled</c>, <c>completed</c>, or the type of what it threw.</summary>
        public TaskCompletionSource<string> CountEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many commands the server ran under the request's comment when the page was sent.</summary>
        public int RunningWhenThePageWasSent { get; private set; }

        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            if (stages.Count == 0 || !stages[^1].Contains("$count"))
            {
                // The page waits until the count is running at the database: the engine sends both at once,
                // and a page that came back first would cancel a count that never reached the server.
                var waited = System.Diagnostics.Stopwatch.StartNew();

                while ((RunningWhenThePageWasSent = await running()) == 0)
                {
                    if (waited.Elapsed > Patience)
                        throw new InvalidOperationException("The server never reported the count as running; the page was not sent.");

                    await Task.Delay(25, CancellationToken.None);
                }

                return await inner.AggregateAsync(entity, stages, options, cancellationToken);
            }

            var slow = new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$function", new BsonDocument
            {
                ["body"] = $"function() {{ sleep({SleepMs}); return true; }}",
                ["args"] = new BsonArray(),
                ["lang"] = "js",
            })));

            try
            {
                var rows = await inner.AggregateAsync(entity, [stages[0], slow, .. stages.Skip(1)], options, cancellationToken);

                CountEnded.TrySetResult("completed");

                return rows;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CountEnded.TrySetResult("cancelled");

                throw;
            }
            catch (Exception exception)
            {
                CountEnded.TrySetResult(exception.GetType().Name);

                throw;
            }
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
        var comment = "count-cancellation-" + Guid.NewGuid().ToString("N");
        var runner = new SlowCount(new MongoAggregateRunner(client, owned.Name), () => RunningAsync(client, comment));

        // The count may run a minute, by the host and by the request: nothing in this test waits that long, so no time limit ends it.
        var options = Direct.Options(configured =>
        {
            configured.Execution.MaxTimeMs = BudgetMs;
            configured.Count.MaxBudgetMs = BudgetMs;
        });
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(Direct.Model), runner, Direct.Cursors, options, includeErrorDetails: true);
        var context = Direct.Context(options, organisation: LateJoinCases.Organisation) with { CorrelationId = comment };

        (Children * SleepMs).Should().BeGreaterThan(BudgetMs, "left alone the count reads rows until its time limit ends it");

        // Fewer children than the page takes: the page ends the list, so it is its own count.
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(LateJoinCases.Child, $$"""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": {{PageLimit}}, "count": { "budgetMs": {{BudgetMs}} } } } ]"""), context);
        var result = outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;

        result.Items.Should().HaveCount(Children);
        result.PageInfo.Count!.State.Should().Be("exact");
        result.PageInfo.Count.Value.Should().Be(Children);
        runner.RunningWhenThePageWasSent.Should().Be(1, "the count ran at the database before the page was sent, so it ran beside the page");

        // The engine stopped the count: its task ended in the cancellation. Had the engine waited for it, it would have
        // ended in the database's timeout a minute on; left to itself it would not have ended at all by now.
        var ended = await Task.WhenAny(runner.CountEnded.Task, Task.Delay(Patience));

        ended.Should().BeSameAs(runner.CountEnded.Task, "the count the page made needless is given up with the answer, not left running");
        (await runner.CountEnded.Task).Should().Be("cancelled");

        // And the server ends the command when it next looks at its client. A count the cancellation had not reached
        // would run on to its time limit, which lies beyond this wait.
        var gone = System.Diagnostics.Stopwatch.StartNew();

        while (await RunningAsync(client, comment) > 0)
        {
            gone.Elapsed.Should().BeLessThan(Patience, $"the count is ended at the database, not left to its time limit of {BudgetMs / 1000} s");
            await Task.Delay(50);
        }
    }
}
