using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
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

    /// <summary>
    /// An addon source over a database that takes <see cref="Read"/> to answer: it says the wait
    /// as a command it sent, or as one another request had sent (<see cref="Joined"/>), and with
    /// <see cref="Cached"/> it answers from memory and says nothing.
    /// </summary>
    private sealed class DatabaseAddons : IReportingAddonDefinitionSource
    {
        public const string Collection = "model_definition.addon_definition.probe";

        public TimeSpan Read { get; set; } = TimeSpan.FromMilliseconds(60);

        public bool Joined { get; set; }

        public bool Cached { get; set; }

        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
            ForEntityAsync(entity, organisation, new AddonReadReport(), cancellationToken);

        public async ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, AddonReadReport report, CancellationToken cancellationToken)
        {
            if (Cached)
                return [];

            var started = Stopwatch.GetTimestamp();

            await Task.Delay(Read, cancellationToken);
            report.Database(Collection, started, docs: 4, roundTrips: Joined ? 0 : 1);

            return [];
        }
    }

    /// <summary>An addon source that takes as long and says nothing of where the time went.</summary>
    private sealed class SilentAddons : IAddonDefinitionSource
    {
        public async ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken)
        {
            await Task.Delay(60, cancellationToken);

            return [];
        }
    }

    private static async Task<QueryResult> RunAsync(IAggregateRunner runner, string pipeline, bool timing = true, IAddonDefinitionSource? addons = null, int contract = 2, Action<OxQLOptions>? configure = null, bool header = false)
    {
        var options = BindHost.Options(configure);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options);
        var context = BindHost.Context(options, addons: addons, contract: contract) with { Timing = new QueryTiming { Detail = header } };
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline) with { Timing = timing ? QueryRequest.TimingAsked : null }, context);

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
    public async Task Under_contract_1_neither_the_member_nor_the_header_asks_for_the_breakdown()
    {
        // Contract 1 never had the member: it is ignored as every member it does not know, and the answer carries none.
        var member = await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "page": { "limit": 5 } }]""", contract: 1);
        var header = await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "page": { "limit": 5 } }]""", timing: false, contract: 1, header: true);

        member.Timing.Should().BeNull();
        header.Timing.Should().BeNull();
        member.Measured!.Detail.Should().BeFalse("nothing downstream is asked for a breakdown either");
        System.Text.Json.JsonSerializer.Serialize(member, BindHost.Json).Should().NotContain("timing");
    }

    [Fact]
    public async Task A_host_that_keeps_the_breakdown_to_itself_answers_none_whoever_asks_and_still_measures()
    {
        static void Off(OxQLOptions options) => options.Execution.TimingDetail = false;

        var member = await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "page": { "limit": 5 } }]""", configure: Off);
        var header = await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "page": { "limit": 5 } }]""", timing: false, configure: Off, header: true);

        member.Timing.Should().BeNull("the member is accepted and answers nothing");
        header.Timing.Should().BeNull();
        member.Measured!.Detailed.Should().BeFalse("no owner is asked for its breakdown");
        member.Measured!.ServerTiming().Should().Contain("total;dur=").And.Contain("db;dur=", "the header's durations are sent either way");

        new OxQLOptions().Execution.TimingDetail.Should().BeTrue("the breakdown is on unless a host switches it off");
        (await RunAsync(new SlowRunner { PageRows = Rows(3) }, """[{ "page": { "limit": 5 } }]""", timing: false, header: true)).Timing.Should().NotBeNull("the header asks where the host allows it");
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

        Number(timing["db"]!["ms"]).Should().BeLessThan(sum, "two commands side by side are waited for once, not twice");
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
    public async Task A_read_of_addon_definitions_the_source_made_at_its_database_is_database_time_and_not_this_hosts()
    {
        var result = await RunAsync(new SlowRunner { PageRows = Rows(3), Page = TimeSpan.FromMilliseconds(5) }, """[{ "page": { "limit": 5 } }]""", addons: new DatabaseAddons());
        var timing = result.Timing!;
        var list = timing["db"]!["list"]!.AsArray();

        list.Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("addon", "page");

        var read = list[0]!.AsObject();

        read.Select(member => member.Key).Should().Equal("kind", "collection", "ms", "docs", "parallelWith");
        read["collection"]!.GetValue<string>().Should().Be(DatabaseAddons.Collection, "the collection as the source named it");
        read["docs"]!.GetValue<int>().Should().Be(4);
        read["parallelWith"].Should().BeNull();
        Number(read["ms"]).Should().BeGreaterThanOrEqualTo(50);
        list[1]!["parallelWith"].Should().BeNull("the read was over when the page left");
        timing["db"]!["commands"]!.GetValue<int>().Should().Be(2);
        timing["db"]!["roundTrips"]!.GetValue<int>().Should().Be(2);

        // The wait is the database's: this host's own time does not hold it.
        Number(timing["db"]!["ms"]).Should().BeGreaterThanOrEqualTo(Number(read["ms"]));
        Number(timing["addonMs"]).Should().BeGreaterThanOrEqualTo(Number(read["ms"]) - 0.011, "the time the source was asked holds the read");
        (Number(timing["totalMs"]) - Number(timing["db"]!["ms"])).Should().BeApproximately(Number(timing["oursMs"]), 0.011);
        Number(timing["oursMs"]).Should().BeLessThan(Number(timing["totalMs"]) - 50, "a database round trip is not this host's own time");

        var metrics = result.Measured!.ServerTiming().Split(", ").Select(metric => metric.Split(';')).ToDictionary(parts => parts[0], parts => parts.Skip(1).ToArray());

        metrics.Keys.Should().Equal("total", "db", "ours", "bind", "compile", "convert", "addon");
        metrics["db"].Should().Contain("desc=\"2 cmd / 2 rt\"");
        metrics["addon"].Should().Contain("desc=\"db\"");
        double.Parse(metrics["addon"][0]["dur=".Length..], CultureInfo.InvariantCulture).Should().BeApproximately(Number(read["ms"]), 0.011);
        (double.Parse(metrics["total"][0]["dur=".Length..], CultureInfo.InvariantCulture) - double.Parse(metrics["db"][0]["dur=".Length..], CultureInfo.InvariantCulture))
            .Should().BeApproximately(double.Parse(metrics["ours"][0]["dur=".Length..], CultureInfo.InvariantCulture), 0.011);
    }

    [Fact]
    public async Task A_read_another_request_had_sent_is_listed_as_waited_for_and_counted_as_no_command_of_this_request()
    {
        var result = await RunAsync(new SlowRunner { PageRows = Rows(3), Page = TimeSpan.FromMilliseconds(5) }, """[{ "page": { "limit": 5 } }]""", addons: new DatabaseAddons { Joined = true });
        var timing = result.Timing!;
        var read = timing["db"]!["list"]!.AsArray()[0]!.AsObject();

        read.Select(member => member.Key).Should().Equal("kind", "collection", "ms", "docs", "parallelWith", "shared");
        read["kind"]!.GetValue<string>().Should().Be("addon");
        read["shared"]!.GetValue<bool>().Should().BeTrue();
        timing["db"]!["commands"]!.GetValue<int>().Should().Be(1, "the page; the read was another request's");
        timing["db"]!["roundTrips"]!.GetValue<int>().Should().Be(1);
        Number(timing["db"]!["ms"]).Should().BeGreaterThanOrEqualTo(50, "the request waited for the database all the same");
        (Number(timing["totalMs"]) - Number(timing["db"]!["ms"])).Should().BeApproximately(Number(timing["oursMs"]), 0.011);
        result.Measured!.ServerTiming().Should().Contain("db;dur=").And.Contain("desc=\"1 cmd / 1 rt\"").And.Contain("addon;dur=");
    }

    [Fact]
    public async Task A_source_that_answers_from_memory_lists_no_command_and_one_that_says_nothing_keeps_its_time_with_this_host()
    {
        var cached = await RunAsync(new SlowRunner { PageRows = Rows(3), Page = TimeSpan.FromMilliseconds(5) }, """[{ "page": { "limit": 5 } }]""", addons: new DatabaseAddons { Cached = true });

        cached.Timing!["db"]!["list"]!.AsArray().Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("page");
        cached.Measured!.ServerTiming().Should().NotContain("addon;");

        // The engine does not guess what a source waited for: only a source that says so moves its time to the database.
        var silent = await RunAsync(new SlowRunner { PageRows = Rows(3), Page = TimeSpan.FromMilliseconds(5) }, """[{ "page": { "limit": 5 } }]""", addons: new SilentAddons());
        var timing = silent.Timing!;

        timing["db"]!["list"]!.AsArray().Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("page");
        Number(timing["addonMs"]).Should().BeGreaterThanOrEqualTo(50);
        Number(timing["oursMs"]).Should().BeGreaterThanOrEqualTo(50);
        Number(timing["bindMs"]).Should().BeLessThan(Number(timing["addonMs"]), "the time the source was asked is taken out of the binding");
        silent.Measured!.ServerTiming().Should().NotContain("addon;");
    }

    [Fact]
    public async Task The_queries_of_a_batch_that_take_turns_at_the_source_say_its_read_and_not_their_wait_for_the_turn()
    {
        var source = SerialAddonSource.Of(new DatabaseAddons());
        var first = new AddonReadReport();
        var second = new AddonReadReport();
        var started = Stopwatch.GetTimestamp();
        var one = ((IReportingAddonDefinitionSource)source).ForEntityAsync(Order, BindHost.Organisation, first, CancellationToken.None).AsTask();
        var two = ((IReportingAddonDefinitionSource)source).ForEntityAsync(Order, BindHost.Organisation, second, CancellationToken.None).AsTask();

        await Task.WhenAll(one, two);

        first.Reads.Should().ContainSingle();
        second.Reads.Should().ContainSingle();

        var later = first.Reads[0].Started > second.Reads[0].Started ? first.Reads[0] : second.Reads[0];
        var earlier = first.Reads[0].Started > second.Reads[0].Started ? second.Reads[0] : first.Reads[0];

        later.Started.Should().BeGreaterThanOrEqualTo(earlier.Ended, "the second read began when its turn came, not when it was asked");
        Stopwatch.GetElapsedTime(started, later.Started).TotalMilliseconds.Should().BeGreaterThanOrEqualTo(50);

        // A source that reports nothing is read in turns as before.
        (await SerialAddonSource.Of(new SilentAddons()).ForEntityAsync(Order, BindHost.Organisation, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void A_read_that_is_over_joins_the_union_of_the_commands_and_keeps_its_kind_in_a_nested_request()
    {
        var timing = new QueryTiming();
        var before = QueryTiming.Now;

        Thread.Sleep(30);
        timing.Read(QueryTiming.AddonRead, "definitions", before, QueryTiming.Now, docs: 2);

        var page = timing.Begin(QueryTiming.Page, "a");

        Thread.Sleep(20);
        page.End(5);

        // An owner query this host runs for itself reads its target's definitions: the read is this request's, an addon read still.
        var nested = timing.Nested();
        var inner = QueryTiming.Now;

        Thread.Sleep(20);
        nested.Read(QueryTiming.AddonRead, "definitions", inner, QueryTiming.Now, docs: 0, roundTrips: 3);
        timing.Stop();

        var json = timing.ToJson();
        var list = json["db"]!["list"]!.AsArray();

        list.Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("addon", "page", "addon");
        list[2]!["getMores"]!.GetValue<int>().Should().Be(2, "three round trips are the command and two further batches");
        json["db"]!["commands"]!.GetValue<int>().Should().Be(3);
        json["db"]!["roundTrips"]!.GetValue<int>().Should().Be(5);
        list.Select(command => command!["parallelWith"]).Should().AllSatisfy(beside => beside.Should().BeNull("each read was over before the next command left"));

        // What is asserted is how the time is attributed, never how long the machine took: a span is
        // at least a clock tick long, the union of spans that do not overlap is their sum, and what
        // is not the database's is this host's. A bound on this host's share would be a bound on the
        // scheduler, which is free to take the thread between any two statements above.
        list.Select(command => Number(command!["ms"])).Should().AllSatisfy(ms => ms.Should().BeGreaterThan(0));
        Number(json["db"]!["ms"]).Should().BeApproximately(list.Sum(command => Number(command!["ms"])), 0.5, "one after the other, the union is the sum");
        (Number(json["totalMs"]) - Number(json["db"]!["ms"])).Should().BeApproximately(Number(json["oursMs"]), 0.011);
        Number(json["oursMs"]).Should().BeGreaterThanOrEqualTo(0);
        Number(json["db"]!["ms"]).Should().BeLessThanOrEqualTo(Number(json["totalMs"]));
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
