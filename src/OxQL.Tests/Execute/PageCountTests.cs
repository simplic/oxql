using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The contract 2 count (DG2 M3): <c>page.count</c> asks, <c>pageInfo.count</c> answers what the
/// engine knows: <c>exact</c>, <c>atLeast</c> with why counting stopped, or <c>unknown</c> with why.
/// Every page knows something without counting; the count step beside a page is one bounded step
/// that never fails or holds back the rows; a count-only request (<c>limit: 0</c>) climbs a ladder
/// within its time budget.
/// </summary>
public class PageCountTests
{
    private const string Order = "probe.order";

    /// <summary>Answers the page with fixture rows and each count by a script over the bound it was asked with (<c>$limit - 1</c>).</summary>
    private sealed class ScriptedRunner : IAggregateRunner
    {
        public List<BsonDocument> PageRows { get; set; } = [];

        /// <summary>How many rows match; a count answers this, stopped at its limit.</summary>
        public long Matching { get; set; }

        /// <summary>What a count step does instead of answering, by its bound; null answers.</summary>
        public Func<long, Task>? OnCount { get; set; }

        public List<(string Kind, long Bound, int MaxTimeMs)> Calls { get; } = [];

        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            if (stages.Count == 0 || !stages[^1].Contains("$count"))
            {
                lock (Calls) Calls.Add(("page", 0, options.MaxTimeMs));

                return PageRows;
            }

            var bound = stages[^2]["$limit"].ToInt64() - 1;

            lock (Calls) Calls.Add(("count", bound, options.MaxTimeMs));

            if (OnCount is not null)
                await OnCount(bound).WaitAsync(cancellationToken);

            return Matching == 0 ? [] : [new BsonDocument("n", Math.Min(Matching, bound + 1))];
        }
    }

    private static (MongoQueryEngine Engine, ScriptedRunner Runner) Host(int rows, long matching, Action<OxQLOptions>? configure = null)
    {
        var runner = new ScriptedRunner
        {
            PageRows = Enumerable.Range(0, rows).Select(index => new BsonDocument { ["_id"] = new BsonBinaryData(new Guid(index + 1, 0, 0, new byte[8]), GuidRepresentation.Standard), ["Number"] = "n" + index }).ToList(),
            Matching = matching,
        };

        return (new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options(configure)), runner);
    }

    private static async Task<QueryResult> Run(MongoQueryEngine engine, string page, RequestContext? context = null)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, $$"""[{ "page": {{page}} }]"""), context ?? BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    /// <summary>The count as the wire carries it, without the time it took.</summary>
    private static string Wire(CountInfo? count) =>
        JsonSerializer.Serialize(count is null ? null : count with { Ms = null }, BindHost.Json);

    // ---- the wire form and the binder -------------------------------------------------------

    [Fact]
    public void The_member_reads_as_true_false_or_an_object_and_writes_back_what_it_read()
    {
        static PageStage Parse(string count) => BindHost.Request(Order, $$"""[{ "page": { "limit": 5, "count": {{count}} } }]""").Pipeline[0].Page!;

        Parse("true").Count.Should().BeEquivalentTo(new PageCount { Asked = true });
        Parse("false").Count.Should().BeEquivalentTo(new PageCount());
        Parse("""{ "upTo": 50000, "budgetMs": 800 }""").Count.Should().BeEquivalentTo(new PageCount { Asked = true, UpTo = 50_000, BudgetMs = 800 });
        Parse("{}").Count.Should().BeEquivalentTo(new PageCount { Asked = true });
        Parse("5").Count!.Invalid.Should().BeTrue();
        Parse("null").Count!.Invalid.Should().BeTrue();
        Parse("""{ "upTo": 1.5, "max": 1 }""").Count.Should().BeEquivalentTo(new PageCount { Asked = true, UpTo = 0, Unknown = ["max"] });
        BindHost.Request(Order, """[{ "page": { "limit": 5 } }]""").Pipeline[0].Page!.Count.Should().BeNull();

        JsonSerializer.Serialize(Parse("true"), BindHost.Json).Should().Be("""{"limit":5,"count":true}""");
        JsonSerializer.Serialize(Parse("false"), BindHost.Json).Should().Be("""{"limit":5,"count":false}""");
        JsonSerializer.Serialize(Parse("""{ "budgetMs": 800, "upTo": 7 }"""), BindHost.Json).Should().Be("""{"limit":5,"count":{"upTo":7,"budgetMs":800}}""");
    }

    [Fact]
    public async Task The_step_is_bound_by_the_host_and_lowered_by_the_request()
    {
        static async Task<BoundCount?> Bound(string count, Action<OxQLOptions>? configure = null) =>
            (await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{ "page": { "limit": 5, "count": {{count}} } }]""", BindHost.Context(BindHost.Options(configure)))).Page.Count;

        (await Bound("true")).Should().Be(new BoundCount(Rows: 1_000, Ceiling: 100_000, BudgetMs: 500));
        (await Bound("false")).Should().BeNull();
        (await Bound("""{ "upTo": 250 }""")).Should().Be(new BoundCount(250, 250, 500));
        (await Bound("""{ "upTo": 50000, "budgetMs": 800 }""")).Should().Be(new BoundCount(1_000, 50_000, 800));
        (await Bound("""{ "upTo": 10000000000, "budgetMs": 10000000000 }""")).Should().Be(new BoundCount(1_000, 100_000, 5_000), "the host's cap and its longest budget bound the request's");
        (await Bound("true", options => { options.Limits.CountCap = 200; options.Count.StepRows = 5_000; options.Count.BudgetMs = 50; })).Should().Be(new BoundCount(200, 200, 50));

        var compiled = MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "match": { "number": { "eq": "x" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "count": true } }]"""), new CompileOptions(10_000, null, 100_000));

        compiled.CountStages!.Select(stage => stage.GetElement(0).Name).Should().Equal(["$match", "$match", "$limit", "$count"], "the count carries the filters, never the sort or the cursor");
        compiled.CountStages![^2].ShouldBeBson(BsonDocument.Parse("{ $limit: 1001 }"));
    }

    [Theory]
    [InlineData("5", Codes.InvalidPageLimit)]
    [InlineData("\"yes\"", Codes.InvalidPageLimit)]
    [InlineData("""{ "upTo": 0 }""", Codes.InvalidPageLimit)]
    [InlineData("""{ "upTo": -1 }""", Codes.InvalidPageLimit)]
    [InlineData("""{ "upTo": 1.5 }""", Codes.InvalidPageLimit)]
    [InlineData("""{ "budgetMs": 0 }""", Codes.InvalidPageLimit)]
    [InlineData("""{ "budgetMs": "1s" }""", Codes.InvalidPageLimit)]
    [InlineData("""{ "max": 5 }""", Codes.UnknownStageMember)]
    public async Task A_count_that_is_not_true_false_or_a_bounded_object_is_refused(string count, string code)
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, $$"""[{ "page": { "limit": 5, "count": {{count}} } }]""", code);

        error.Stage.Should().Be(0);
    }

    [Fact]
    public async Task IncludeTotalCount_is_not_a_member_of_a_contract_2_page_and_count_is_not_one_of_contract_1()
    {
        foreach (var form in new[] { "true", "false", "500" })
        {
            var error = await BindHost.ErrorAsync(BindHost.Probe, Order, $$"""[{ "page": { "includeTotalCount": {{form}} } }]""", Codes.UnknownStageMember);

            JsonSerializer.Serialize(error.Params).Should().Be("""{"stage":"page","member":"includeTotalCount","reason":"removed","use":"count"}""", form);
        }

        var legacy = BindHost.Context(contract: 1);

        (await BindHost.ErrorAsync(BindHost.Probe, Order, """[{ "page": { "count": true } }]""", Codes.LegacyStageUnsupported, legacy))
            .Message.Should().Be("'count' is not a member of page; page carries limit, offset, cursor, includeTotalCount.");
        (await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "page": { "includeTotalCount": true } }]""", legacy)).Page.Should().Match<BoundStage.Page>(page => page.IncludeTotalCount && page.Count == null);
    }

    [Fact]
    public async Task Limit_0_asks_for_the_count_alone_and_needs_a_count_and_no_cursor()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "page": { "limit": 0, "count": { "budgetMs": 800 } } }]""");

        bound.Page.CountOnly.Should().BeTrue();
        bound.Page.Count.Should().Be(new BoundCount(1_000, 100_000, 800));

        foreach (var page in new[] { """{ "limit": 0 }""", """{ "limit": 0, "count": false }""", """{ "limit": 0, "count": true, "cursor": "a.b" }""" })
        {
            var error = await BindHost.ErrorAsync(BindHost.Probe, Order, $$"""[{ "page": {{page}} }]""", Codes.InvalidPageLimit);

            JsonSerializer.Serialize(error.Params).Should().Be("""{"limit":0,"needs":"count"}""", page);
        }

        (await BindHost.ErrorAsync(BindHost.Probe, Order, """[{ "page": { "limit": 0, "includeTotalCount": true } }]""", Codes.InvalidPageLimit, BindHost.Context(contract: 1)))
            .Message.Should().Be("The page limit 0 is not positive.", "contract 1 has no count-only request");
    }

    // ---- what a page knows without counting -------------------------------------------------

    [Fact]
    public async Task A_page_without_a_next_one_is_the_exact_count_and_a_page_with_one_proves_one_row_more()
    {
        var (last, runner) = Host(rows: 3, matching: 3);

        Wire((await Run(last, """{ "limit": 5 }""")).PageInfo.Count).Should().Be("""{"state":"exact","value":3}""");
        runner.Calls.Select(call => call.Kind).Should().Equal(["page"], "nothing was counted");

        var (more, _) = Host(rows: 6, matching: 40);

        Wire((await Run(more, """{ "limit": 5 }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":6,"stoppedBy":"page"}""");
        Wire((await Run(more, """{ "limit": 5, "count": false }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":6,"stoppedBy":"page"}""");
    }

    [Fact]
    public async Task The_cursor_carries_the_rows_served_so_a_later_page_knows_them_whatever_its_size()
    {
        var (engine, runner) = Host(rows: 6, matching: 40);
        var first = await Run(engine, """{ "limit": 5 }""");
        var payload = BindHost.Cursors.Decode(first.PageInfo.NextCursor!, (await BindHost.BoundAsync(BindHost.Probe, Order, "[]")).Fingerprint);

        payload!.Served.Should().Be(5);

        // The second page is smaller, and a third ends the list.
        runner.PageRows = runner.PageRows.Take(3).ToList();
        var second = await Run(engine, $$"""{ "limit": 2, "cursor": "{{first.PageInfo.NextCursor}}" }""");

        Wire(second.PageInfo.Count).Should().Be("""{"state":"atLeast","value":8,"stoppedBy":"page"}""");

        var third = await Run(engine, $$"""{ "limit": 10, "cursor": "{{second.PageInfo.NextCursor}}" }""");

        Wire(third.PageInfo.Count).Should().Be("""{"state":"exact","value":10}""", "5 + 2 + the 3 rows of the last page");
        third.PageInfo.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task A_contract_1_answer_and_its_cursor_carry_nothing_of_it()
    {
        var (engine, _) = Host(rows: 6, matching: 40);
        var legacy = await Run(engine, """{ "limit": 5, "includeTotalCount": true }""", BindHost.Context(contract: 1));

        legacy.PageInfo.Count.Should().BeNull();
        legacy.PageInfo.TotalCount.Should().Be(40);
        JsonSerializer.Serialize(legacy.PageInfo, BindHost.Json).Should().NotContain("\"count\"");
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(legacy.PageInfo.NextCursor!.Split('.')[0]))).Should().NotContain("\"n\"");

        static string Pad(string text) => text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4);
    }

    // ---- the count step beside a page -------------------------------------------------------

    [Fact]
    public async Task The_step_is_exact_up_to_its_bound_and_a_lower_bound_above_it()
    {
        var (engine, runner) = Host(rows: 6, matching: 412);

        Wire((await Run(engine, """{ "limit": 5, "count": true }""")).PageInfo.Count).Should().Be("""{"state":"exact","value":412}""");
        runner.Calls.Should().BeEquivalentTo([("page", 0L, 10_000), ("count", 1_000L, 500)], "one step beside the page, under its own budget");

        runner.Matching = 1_000;
        Wire((await Run(engine, """{ "limit": 5, "count": true }""")).PageInfo.Count).Should().Be("""{"state":"exact","value":1000}""");

        runner.Matching = 1_001;
        Wire((await Run(engine, """{ "limit": 5, "count": true }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":1000,"stoppedBy":"budget"}""", "a page's count is one step; counting on would go further");
        Wire((await Run(engine, """{ "limit": 5, "count": { "upTo": 50 } }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":50,"stoppedBy":"cap"}""", "the request needs no more");

        var (capped, _) = Host(rows: 6, matching: 5_000);
        var lowCap = BindHost.Context(BindHost.Options(options => options.Limits.CountCap = 300));

        Wire((await Run(capped, """{ "limit": 5, "count": true }""", lowCap)).PageInfo.Count).Should().Be("""{"state":"atLeast","value":300,"stoppedBy":"cap"}""", "the host's cap is the ceiling");
        (await Run(capped, """{ "limit": 5, "count": true }""", lowCap)).Diagnostics.Should().BeNull("no TOTAL_COUNT_CAPPED: the count says it itself");
    }

    [Fact]
    public async Task A_step_that_took_time_says_how_long_and_one_that_was_not_needed_is_not_waited_for()
    {
        var (engine, runner) = Host(rows: 6, matching: 20);
        var counted = await Run(engine, """{ "limit": 5, "count": true }""");

        counted.PageInfo.Count!.Ms.Should().NotBeNull();

        // The page ends the list: the count is the rows served, and the step, slow as it is, holds nothing back.
        runner.PageRows = runner.PageRows.Take(4).ToList();
        runner.OnCount = _ => Task.Delay(TimeSpan.FromSeconds(30));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var last = await Run(engine, """{ "limit": 5, "count": true }""");

        Wire(last.PageInfo.Count).Should().Be("""{"state":"exact","value":4}""");
        last.PageInfo.Count!.Ms.Should().BeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_step_that_does_not_finish_in_its_budget_leaves_the_count_unknown_and_the_rows_as_they_are()
    {
        var (engine, runner) = Host(rows: 6, matching: 20);

        runner.OnCount = _ => Task.Delay(TimeSpan.FromSeconds(30));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await Run(engine, """{ "limit": 5, "count": { "budgetMs": 40 } }""");

        result.Items.Should().HaveCount(5);
        result.PageInfo.HasNextPage.Should().BeTrue();
        Wire(result.PageInfo.Count).Should().Be("""{"state":"unknown","reason":"timeout"}""");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the answer waits for the step no longer than its budget");
        runner.Calls.Single(call => call.Kind == "count").MaxTimeMs.Should().Be(40, "the budget is the step's own time limit at the database");
    }

    [Fact]
    public async Task A_step_the_database_times_out_or_fails_is_unknown_never_a_refusal()
    {
        var (engine, runner) = Host(rows: 6, matching: 20);

        runner.OnCount = _ => throw new MongoExecutionTimeoutException(new MongoDB.Driver.Core.Connections.ConnectionId(new MongoDB.Driver.Core.Servers.ServerId(new MongoDB.Driver.Core.Clusters.ClusterId(), new System.Net.DnsEndPoint("localhost", 27017))), "operation exceeded time limit");
        Wire((await Run(engine, """{ "limit": 5, "count": true }""")).PageInfo.Count).Should().Be("""{"state":"unknown","reason":"timeout"}""");

        runner.OnCount = _ => throw new InvalidOperationException("the count broke");

        var failed = await Run(engine, """{ "limit": 5, "count": true }""");

        failed.Items.Should().HaveCount(5);
        Wire(failed.PageInfo.Count).Should().Be("""{"state":"unknown","reason":"failed"}""");
    }

    [Fact]
    public async Task A_bound_below_what_the_pages_already_served_gives_way_to_the_rows_served()
    {
        var (engine, runner) = Host(rows: 6, matching: 5_000);
        var first = await Run(engine, """{ "limit": 5 }""");

        // 5 rows served, 6 known; a count bounded at 3 proves less than the walk already has.
        var second = await Run(engine, $$"""{ "limit": 5, "cursor": "{{first.PageInfo.NextCursor}}", "count": { "upTo": 3 } }""");

        Wire(second.PageInfo.Count).Should().Be("""{"state":"atLeast","value":11,"stoppedBy":"page"}""");

        // The same when the data shrank under an exact count: what was served is what is proven.
        runner.Matching = 2;
        Wire((await Run(engine, $$"""{ "limit": 5, "cursor": "{{first.PageInfo.NextCursor}}", "count": true }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":11,"stoppedBy":"page"}""");
    }

    // ---- the count alone: a ladder under a time budget ---------------------------------------

    [Fact]
    public async Task The_count_alone_reads_no_page_and_climbs_until_it_is_exact()
    {
        var (engine, runner) = Host(rows: 6, matching: 41_234);
        var result = await Run(engine, """{ "limit": 0, "count": { "budgetMs": 5000 } }""");

        result.Items.Should().BeEmpty();
        result.PageInfo.HasNextPage.Should().BeFalse();
        result.PageInfo.NextCursor.Should().BeNull();
        Wire(result.PageInfo.Count).Should().Be("""{"state":"exact","value":41234}""");
        runner.Calls.Select(call => (call.Kind, call.Bound)).Should().Equal([("count", 1_000L), ("count", 10_000L), ("count", 100_000L)], "each step counts ten times further, and no page is read");
        runner.Calls.Should().OnlyContain(call => call.MaxTimeMs <= 5_000, "a step's time limit is what is left of the budget");

        runner.Matching = 0;
        Wire((await Run(engine, """{ "limit": 0, "count": true }""")).PageInfo.Count).Should().Be("""{"state":"exact","value":0}""");
    }

    [Fact]
    public async Task The_ladder_stops_at_the_requests_upTo_and_at_the_hosts_cap()
    {
        var (engine, runner) = Host(rows: 0, matching: 500_000);

        Wire((await Run(engine, """{ "limit": 0, "count": { "budgetMs": 5000, "upTo": 2500 } }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":2500,"stoppedBy":"cap"}""");
        runner.Calls.Select(call => call.Bound).Should().Equal(1_000L, 2_500L);

        runner.Calls.Clear();
        Wire((await Run(engine, """{ "limit": 0, "count": { "budgetMs": 5000 } }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":100000,"stoppedBy":"cap"}""");
        runner.Calls.Select(call => call.Bound).Should().Equal(1_000L, 10_000L, 100_000L);
    }

    [Fact]
    public async Task The_next_step_runs_only_when_ten_times_the_last_one_fits_what_is_left_of_the_budget()
    {
        var (engine, runner) = Host(rows: 0, matching: 500_000);

        // The first step takes 60 ms: the next is predicted at 600 ms, which 300 ms cannot hold.
        runner.OnCount = _ => Task.Delay(60);

        var result = await Run(engine, """{ "limit": 0, "count": { "budgetMs": 300 } }""");

        Wire(result.PageInfo.Count).Should().Be("""{"state":"atLeast","value":1000,"stoppedBy":"budget"}""");
        runner.Calls.Select(call => call.Bound).Should().Equal(1_000L);
        result.PageInfo.Count!.Ms.Should().BeGreaterThanOrEqualTo(50);
    }

    [Fact]
    public async Task A_step_that_runs_out_of_time_leaves_the_bound_before_it_and_a_first_step_that_does_leaves_nothing()
    {
        var (engine, runner) = Host(rows: 0, matching: 500_000);

        // The first step is instant, so the second is predicted to fit, and then does not.
        runner.OnCount = bound => bound == 1_000 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(30));

        var watch = System.Diagnostics.Stopwatch.StartNew();

        // The fake does not stop by itself at maxTimeMS as the database does; the engine's own budget does not either, so the fake is told to.
        runner.OnCount = bound => bound == 1_000 ? Task.CompletedTask : Task.FromException(new TimeoutException());

        Wire((await Run(engine, """{ "limit": 0, "count": { "budgetMs": 200 } }""")).PageInfo.Count).Should().Be("""{"state":"atLeast","value":1000,"stoppedBy":"time"}""");

        runner.OnCount = _ => Task.FromException(new TimeoutException());
        Wire((await Run(engine, """{ "limit": 0, "count": { "budgetMs": 200 } }""")).PageInfo.Count).Should().Be("""{"state":"unknown","reason":"timeout"}""");

        runner.OnCount = _ => Task.FromException(new InvalidOperationException("broken"));
        Wire((await Run(engine, """{ "limit": 0, "count": { "budgetMs": 200 } }""")).PageInfo.Count).Should().Be("""{"state":"unknown","reason":"failed"}""");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
