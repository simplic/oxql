using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// Where a request's time went (<see cref="QueryTiming"/>): the database's share is the time with a
/// command in flight, counted once however many ran together, so the total less the database is
/// this host's own; every command is listed with what it was for; and the breakdown is in the
/// answer only when the request asks for it.
/// </summary>
public class QueryTimingTests
{
    private const string Order = "probe.order";

    /// <summary>Answers every aggregate after a delay by its kind, so which ran beside which shows.</summary>
    private sealed class SlowRunner : IAggregateRunner
    {
        public List<BsonDocument> PageRows { get; set; } = [];

        public TimeSpan Page { get; set; } = TimeSpan.FromMilliseconds(40);

        public TimeSpan Count { get; set; } = TimeSpan.FromMilliseconds(40);

        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            var counts = stages.Count > 0 && stages[^1].Contains("$count");

            await Task.Delay(counts ? Count : Page, cancellationToken);

            return counts ? [new BsonDocument("n", 40)] : PageRows;
        }
    }

    private static List<BsonDocument> Rows(int count) =>
        Enumerable.Range(1, count).Select(index => new BsonDocument { ["_id"] = new BsonBinaryData(new Guid(index, 0, 0, new byte[8]), GuidRepresentation.Standard), ["Number"] = "n" + index, ["st"] = index }).ToList();

    private static async Task<QueryResult> RunAsync(IAggregateRunner runner, string pipeline, bool timing = true)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline) with { Timing = timing ? true : null }, BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static double Number(JsonNode? node) => node!.GetValue<double>();

    [Fact]
    public async Task The_breakdown_is_in_the_answer_only_when_the_request_asks_and_the_collector_always()
    {
        var silent = await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "page": { "limit": 5 } }]""", timing: false);

        silent.Timing.Should().BeNull("an answer carries the breakdown only on request");
        silent.Measured.Should().NotBeNull("the host writes its Server-Timing header from it on every answer");
        System.Text.Json.JsonSerializer.Serialize(silent, BindHost.Json).Should().NotContain("timing").And.NotContain("measured");

        var asked = await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "page": { "limit": 5 } }]""");
        var timing = asked.Timing!;

        timing.Select(member => member.Key).Should().Equal("totalMs", "oursMs", "parseMs", "bindMs", "addonMs", "compileMs", "db", "resolve", "countWaitMs", "convertMs", "rows", "build");
        timing["db"]!.AsObject().Select(member => member.Key).Should().Equal("ms", "commands", "roundTrips", "list");
        timing["resolve"]!.ToJsonString().Should().Be("""{"ms":0,"calls":0,"cacheHits":0,"remoteMs":0,"owners":[]}""");
        timing["rows"]!.GetValue<int>().Should().Be(3);
        timing["build"]!.GetValue<string>().Should().BeOneOf("Debug", "Release");

        var command = timing["db"]!["list"]!.AsArray().Should().ContainSingle().Subject!.AsObject();

        command.Select(member => member.Key).Should().Equal("kind", "collection", "ms", "docs", "bytes", "parallelWith");
        command["kind"]!.GetValue<string>().Should().Be("page");
        command["collection"]!.GetValue<string>().Should().Be(Order);
        command["docs"]!.GetValue<int>().Should().Be(3);
        command["bytes"]!.GetValue<long>().Should().Be(Rows(3).Sum(row => (long)row.ToBson().Length));
        command["parallelWith"].Should().BeNull();
        Number(command["ms"]).Should().BeGreaterThanOrEqualTo(30);

        // The total less the database is this host's own, exactly: neither is rounded away from the other.
        (Number(timing["totalMs"]) - Number(timing["db"]!["ms"])).Should().BeApproximately(Number(timing["oursMs"]), 0.011);
        Number(timing["oursMs"]).Should().BeGreaterThanOrEqualTo(0);
        Number(timing["db"]!["ms"]).Should().BeGreaterThanOrEqualTo(30).And.BeLessThanOrEqualTo(Number(timing["totalMs"]));
    }

    [Fact]
    public async Task Commands_in_flight_together_count_once_and_say_which_they_ran_beside()
    {
        var result = await RunAsync(new SlowRunner { PageRows = Rows(6), Page = TimeSpan.FromMilliseconds(120), Count = TimeSpan.FromMilliseconds(120) }, """[{ "page": { "limit": 5, "count": true } }]""");
        var timing = result.Timing!;
        var list = timing["db"]!["list"]!.AsArray();

        list.Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("page", "count");
        list[0]!["parallelWith"].Should().BeNull();
        list[1]!["parallelWith"]!.GetValue<int>().Should().Be(0, "the count left while the page was in flight");
        timing["db"]!["commands"]!.GetValue<int>().Should().Be(2);
        timing["db"]!["roundTrips"]!.GetValue<int>().Should().Be(2);

        var sum = list.Sum(command => Number(command!["ms"]));

        Number(timing["db"]!["ms"]).Should().BeLessThan(sum * 0.75, "two commands side by side are waited for once, not twice");
        (Number(timing["totalMs"]) - Number(timing["db"]!["ms"])).Should().BeApproximately(Number(timing["oursMs"]), 0.011);
    }

    [Fact]
    public async Task A_count_the_page_made_needless_is_listed_as_discarded_and_a_page_of_groups_sends_none()
    {
        // The page ends the list: the count beside it was sent, its answer is not used.
        var needless = await RunAsync(new SlowRunner { PageRows = Rows(3), Count = TimeSpan.FromSeconds(30) }, """[{ "page": { "limit": 5, "count": true } }]""");
        var count = needless.Timing!["db"]!["list"]!.AsArray().Single(command => command!["kind"]!.GetValue<string>() == "count")!;

        count["discarded"]!.GetValue<bool>().Should().BeTrue();
        Number(count["ms"]).Should().BeLessThan(5_000, "it was stopped when the page came back, not waited for");
        needless.PageInfo.Count!.State.Should().Be(CountInfo.Exact);

        // A page of groups that is not full is its own count: nothing but the page is sent.
        var grouped = await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5, "count": true } }]""");

        grouped.Timing!["db"]!["list"]!.AsArray().Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("page");

        // The count alone: its steps, one after the other.
        var alone = await RunAsync(new SlowRunner { Count = TimeSpan.FromMilliseconds(5) }, """[{ "page": { "limit": 0, "count": true } }]""");
        var steps = alone.Timing!["db"]!["list"]!.AsArray();

        steps.Select(command => command!["kind"]!.GetValue<string>()).Should().OnlyContain(kind => kind == "countRung");
        steps.Should().OnlyContain(command => command!["parallelWith"] == null);
    }

    [Fact]
    public void The_database_time_is_the_union_of_the_command_spans_and_the_header_says_the_sum()
    {
        var timing = new QueryTiming();
        var first = timing.Begin(QueryTiming.Page, "a");

        Thread.Sleep(30);

        var second = timing.Begin(QueryTiming.Count, "a");

        Thread.Sleep(30);
        first.End(101, getMores: 1);
        Thread.Sleep(30);
        second.End(1, getMores: 0);
        Thread.Sleep(30);

        // A request this host runs for itself inside this one: its command is this request's, as a resolve.
        var nested = timing.Nested();
        var owner = nested.Begin(QueryTiming.Page, "b");

        nested.Bind(QueryTiming.Now);
        Thread.Sleep(20);
        owner.End(2);
        timing.RoundTrip(0.25);
        timing.Rows = 100;
        timing.Stop();

        var json = timing.ToJson();
        var list = json["db"]!["list"]!.AsArray();

        list.Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("page", "count", "resolve");
        list[1]!["parallelWith"]!.GetValue<int>().Should().Be(0);
        list[2]!["parallelWith"].Should().BeNull("the owner query left after both had ended");
        list[0]!["getMores"]!.GetValue<int>().Should().Be(1);
        json["db"]!["roundTrips"]!.GetValue<int>().Should().Be(4, "three commands and one getMore");
        json["db"]!["rttMs"]!.GetValue<double>().Should().Be(0.25);
        json["db"]!["networkEstimateMs"]!.GetValue<double>().Should().Be(1.0);

        var database = Number(json["db"]!["ms"]);

        database.Should().BeGreaterThanOrEqualTo(100, "30 + 30 + 30 with one or both in flight, then 20").And.BeLessThan(Number(list[0]!["ms"]) + Number(list[1]!["ms"]) + Number(list[2]!["ms"]));
        (Number(json["totalMs"]) - database).Should().BeApproximately(Number(json["oursMs"]), 0.011);
        Number(json["bindMs"]).Should().Be(0, "the phases of the nested request are inside this request's resolve time");

        // The header: the same sums, durations in milliseconds, the commands and round trips described.
        var header = timing.ServerTiming();
        var metrics = header.Split(", ").Select(metric => metric.Split(';')).ToDictionary(parts => parts[0], parts => parts.Skip(1).ToArray());

        metrics.Keys.Should().Equal("total", "db", "ours", "bind", "compile", "convert", "net");
        metrics["db"].Should().Contain("desc=\"3 cmd / 4 rt\"");
        double.Parse(metrics["total"][0]["dur=".Length..], CultureInfo.InvariantCulture).Should().BeApproximately(Number(json["totalMs"]), 0.011);
        double.Parse(metrics["db"][0]["dur=".Length..], CultureInfo.InvariantCulture).Should().BeApproximately(database, 0.011);
        double.Parse(metrics["ours"][0]["dur=".Length..], CultureInfo.InvariantCulture).Should().BeApproximately(Number(json["oursMs"]), 0.011);
        metrics["net"].Should().Equal("dur=1", "desc=\"est\"");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_header_asks_for_the_breakdown_with_any_value_but_0_and_false(string? value, bool asks) =>
        QueryTiming.Asked(value).Should().Be(asks);

    [Fact]
    public void A_clock_the_host_started_earlier_counts_the_time_before_the_engine_took_the_request_as_parse()
    {
        var earlier = Stopwatch.GetTimestamp();

        Thread.Sleep(25);

        var timing = new QueryTiming(earlier);

        timing.Stop();

        var json = timing.ToJson();

        Number(json["parseMs"]).Should().BeGreaterThanOrEqualTo(20);
        Number(json["totalMs"]).Should().BeGreaterThanOrEqualTo(Number(json["parseMs"]));
    }
}
