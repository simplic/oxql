using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// How many aggregates a request makes one service wait for one after another: its sequential depth
/// there. Against a hosted database each is a network round trip, so the depth, not the number of
/// commands, is what a caller waits for. The fleet's aggregates are slowed to
/// <see cref="Delay"/> each (<see cref="LabFleet.AggregateDelay"/>), which makes side by side and
/// one after another tell apart; a local server answers too fast for that.
/// <para>
/// The queries of one internal batch run side by side (<c>Execution:BatchConcurrency</c>), and so do
/// a page, its count and a strict request's truncation probes. What still runs in sequence is what
/// depends on an earlier answer: a keyed join that reads another join's alias waits for it.
/// </para>
/// <para>
/// The cases measure time, so they run in the <see cref="TimedCollection"/>, one after another.
/// <c>OXQL_COMMAND_REPORT=&lt;file&gt;</c> appends what each case measured.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(TimedCollection.Name)]
public class CommandDepthTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(60);

    /// <summary>The depth per service of one cold run of <paramref name="send"/> on a fleet of its own.</summary>
    private static async Task<IReadOnlyDictionary<string, int>> DepthsAsync(string label, Func<CorpusFleet, Task<WireAnswer>> send, int? concurrency = null)
    {
        await using var fleet = await CorpusFleet.CreateAsync("depth_" + label);

        fleet.Fleet.AggregateDelay = Delay;

        if (concurrency is { } degree)
            fleet.Fleet.Configuration["OxQL:Execution:BatchConcurrency"] = degree.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var answer = await send(fleet);
        var took = watch.Elapsed;

        answer.StatusCode.Should().Be(200, answer.Text);

        var depths = fleet.Fleet.Aggregates
            .GroupBy(run => run.Service, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => CommandBatch.DepthOf(group.Select(run => (run.Started, run.Ended))), StringComparer.Ordinal);

        if (Environment.GetEnvironmentVariable(CommandBudgetTests.ReportVariable) is { Length: > 0 } file)
            lock (CommandBudgetTests.ReportGate)
                File.AppendAllText(file, $"{label} | depth at {Delay.TotalMilliseconds} ms per aggregate | concurrency={concurrency?.ToString() ?? "default"} | aggregates={fleet.Fleet.Aggregates.Count} | {string.Join(", ", depths.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={fleet.Fleet.Aggregates.Count(run => run.Service == pair.Key)}/depth {pair.Value}"))} | took {took.TotalMilliseconds:F0} ms{Environment.NewLine}");

        return depths;
    }

    private static Task<WireAnswer> UnionAsync(CorpusFleet fleet) => fleet.Client(LabService.Ledger, Org.R).QueryAsync(CommandBudgetTests.Lines($$"""
        { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first" } },
        { "resolve": { "as": "vehicle", "target": "fleet.vehicle", "onMissing": "report", "outcomeAs": "vehicleOutcome",
                       "byTarget": { "{{ReportSeed.Shipment}}": "deliveringTour.resource.id", "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } }
        """, """ "position": 1, "sourceParent.id": 1, "vehicle.matchCode": 1, "vehicleOutcome": 1 """));

    private static Task<WireAnswer> ReportAsync(CorpusFleet fleet) => fleet.Client(LabService.Ledger, Org.R).QueryAsync(CommandBudgetTests.InvoiceReport);

    [Fact]
    public async Task D1_a_page_and_its_count_are_one_round_trip()
    {
        var depths = await DepthsAsync("D1", fleet => fleet.Client(LabService.Transport).SendAsync(Corpus.Shipment,
            """[ { "match": { "id": { "neq": null } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5, "includeTotalCount": true } } ]"""));

        depths["transport"].Should().Be(1, "the page and the count are sent together");
    }

    [Fact]
    public async Task D4_the_owner_queries_of_a_union_join_run_side_by_side_at_their_owner()
    {
        var depths = await DepthsAsync("D4", UnionAsync);

        depths["transport"].Should().BeLessThanOrEqualTo(2, "the three owner queries of one batch run together; the tour the shipment continues to waits for the shipment");
    }

    [Fact]
    public async Task D4_with_a_batch_concurrency_of_one_they_run_one_after_another_as_they_did()
    {
        var depths = await DepthsAsync("D4_sequential", UnionAsync, concurrency: 1);

        depths["transport"].Should().Be(3);
    }

    [Fact]
    public async Task D6_the_invoice_report_waits_for_fewer_aggregates_in_a_row_than_it_sends()
    {
        var depths = await DepthsAsync("D6", ReportAsync);

        depths["transport"].Should().BeLessThanOrEqualTo(3, "five aggregates in three waves: what a join reads of another join's alias waits for it");
        depths["staff"].Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task D6_with_a_batch_concurrency_of_one_the_report_waits_for_each_owner_query_in_turn()
    {
        var depths = await DepthsAsync("D6_sequential", ReportAsync, concurrency: 1);

        depths["transport"].Should().BeGreaterThan(3);
    }
}
