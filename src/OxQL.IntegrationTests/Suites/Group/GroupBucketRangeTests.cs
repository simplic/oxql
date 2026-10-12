using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Filters;
using Xunit;
using static OxQL.IntegrationTests.Suites.Group.GroupKit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// The rows of a <c>dateTrunc</c> group on the fleet. The engine has no condition for a bucket:
/// a client asks for a group's rows with a half-open range on the member, <c>gte</c> the bucket's
/// start and <c>lt</c> its end. These hold what such a range answers. For <c>day</c> and coarser the
/// bucket is a calendar range in the zone, so the range a client computes from the calendar
/// (<see cref="CalendarRange"/>) answers exactly the rows the group counted, in UTC and across both
/// changes of the clock in Berlin, on a date-time and on a date without a time. Below a day a bucket
/// is elapsed time, not a clock hour: its rows are those from the key to the key plus one unit, and
/// the hour a clock shows twice is two groups. The rows without a date are the group keyed
/// <c>null</c>, asked with <c>eq null</c>. Every expected row is the corpus's, bucketed by the
/// suites' own oracle (<see cref="Truncation"/>), never read off the engine.
/// </summary>
[Trait("Category", "Integration")]
public class GroupBucketRangeTests
{
    private const string Berlin = "Europe/Berlin";

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static Task<LabClient> Conformance() => Lab.ClientAsync(LabService.Conformance);

    private static IReadOnlyList<Guid> Sorted(IEnumerable<CorpusRow> rows) => rows.Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => row.Id).ToList();

    private static string Key(string path, string unit, string? timezone, string? weekStart)
    {
        var node = new JsonObject { ["path"] = path, ["unit"] = unit };

        if (timezone is not null)
            node["timezone"] = timezone;

        if (weekStart is not null)
            node["weekStart"] = weekStart;

        return node.ToJsonString();
    }

    private static async Task<WireAnswer> Level(LabClient client, string entity, string key, string fields = "")
    {
        var level = await client.SendAsync(entity, $$"""
            [ { "group": { "by": [{ "dateTrunc": {{key}}, "as": "bucket" }], "fields": { "count": { "count": true }{{fields}} } } },
              { "sort": [{ "bucket": "asc" }] }, { "page": { "limit": 500, "count": true } } ]
            """);

        level.ShouldBeOk(key);
        level.HasNextPage.Should().BeFalse();
        level.Items.Should().NotBeEmpty();

        return level;
    }

    // ---- day and coarser: a calendar range ------------------------------------------------------

    public static TheoryData<string, string?, string?> CalendarBuckets()
    {
        var data = new TheoryData<string, string?, string?>();

        foreach (var unit in new[] { "day", "week", "month", "quarter", "year" })
        {
            data.Add(unit, null, null);
            data.Add(unit, Berlin, null);
        }

        data.Add("week", Berlin, "sunday");
        data.Add("week", Berlin, "thursday");
        data.Add("day", "Pacific/Chatham", null);
        data.Add("month", "Asia/Kolkata", null);

        return data;
    }

    [Theory]
    [MemberData(nameof(CalendarBuckets))]
    public async Task The_calendar_range_of_a_day_or_coarser_bucket_answers_the_rows_the_group_counted_and_the_ranges_hold_every_row_once(string unit, string? timezone, string? weekStart)
    {
        var client = await Transport();
        var rows = Corpus.Rows(Corpus.Shipment);
        var zone = timezone ?? "UTC";
        var first = weekStart ?? "monday";
        var level = await Level(client, Corpus.Shipment, Key("loadStart", unit, timezone, weekStart));
        var seen = new List<Guid>();
        DateTime? previousEnd = null;

        foreach (var group in level.Items)
        {
            var key = group!["bucket"]!.GetValue<string>();
            var range = CalendarRange.Of(CalendarRange.Instant(key), unit, zone, first);

            // The corpus lies in zones that show no midnight twice: there a key is its bucket's first instant.
            CalendarRange.Iso(range.Start).Should().Be(key, $"{unit} {zone} {weekStart}");

            if (previousEnd is { } end)
                range.Start.Should().BeOnOrAfter(end, "the ranges of two groups share no instant");

            previousEnd = range.End;

            var expected = Sorted(rows.Where(row => Truncation.Truncate(Corpus.Date(row, "loadStart")!.Value, unit, zone, first) == key));
            var leaves = await client.Ids(Corpus.Shipment, $$"""{ "loadStart": {{CalendarRange.Condition(range)}} }""");

            leaves.Should().Equal(expected, $"{unit} {zone} {weekStart}: the range of {key}");
            leaves.Should().HaveCount((int)Number(group["count"])!.Value, $"the group's count is the rows of its range: {key}");
            seen.AddRange(leaves);
        }

        seen.Should().BeEquivalentTo(rows.Select(row => row.Id), "every row is in exactly one range");
    }

    [Fact]
    public async Task A_day_on_which_the_clock_changes_is_a_range_of_23_or_25_hours_and_holds_the_rows_on_both_sides_of_the_change()
    {
        var client = await Transport();
        var days = await Level(client, Corpus.Shipment, Key("loadStart", "day", Berlin, null));

        foreach (var (key, hours, held) in new[]
        {
            ("2026-03-28T23:00:00Z", 23, new[] { "dst-spring-before", "dst-spring-after" }),
            ("2026-10-24T22:00:00Z", 25, ["dst-autumn-first", "dst-autumn-second"]),
        })
        {
            var range = CalendarRange.Of(CalendarRange.Instant(key), "day", Berlin);

            (range.End - range.Start).TotalHours.Should().Be(hours, key);
            Number(days.Items.Single(group => group!["bucket"]!.GetValue<string>() == key)!["count"]).Should().Be(held.Length);
            (await client.Ids(Corpus.Shipment, $$"""{ "loadStart": {{CalendarRange.Condition(range)}} }""")).Should().Equal(FilterKit.Keys(Corpus.Shipment, held), key);

            // A day of 24 hours from the key would lose or take a row: the range is the calendar's, not the key plus a length.
            var naive = (range.Start, End: range.Start.AddHours(24));

            naive.End.Should().NotBe(range.End);
        }
    }

    [Theory]
    [InlineData("month", Berlin)]
    [InlineData("day", Berlin)]
    [InlineData("day", "America/New_York")]
    [InlineData("week", "America/New_York")]
    [InlineData("quarter", Berlin)]
    [InlineData("year", null)]
    public async Task On_a_date_without_a_time_the_range_is_the_buckets_days_written_as_days(string unit, string? timezone)
    {
        var client = await Conformance();
        var rows = Corpus.Rows(Corpus.Conformance);
        var level = await Level(client, Corpus.Conformance, Key("day", unit, timezone, null), """, "days": { "push": "day" }""");
        var seen = 0;

        foreach (var group in level.Items)
        {
            // The key is an instant, the midnight of the bucket's first day in the zone; the member holds days.
            var key = CalendarRange.Instant(group!["bucket"]!.GetValue<string>());
            var (from, until) = CalendarRange.Days(CalendarRange.DayOf(key, timezone ?? "UTC"), unit);
            var days = group["days"]!.AsArray().Select(day => day!.GetValue<string>()).ToList();
            var expected = Sorted(rows.Where(row => Corpus.ValueAt(row, "day") is BsonDateTime stored && days.Contains(stored.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));

            expected.Should().HaveCount((int)Number(group["count"])!.Value);
            days.Should().OnlyContain(day => string.CompareOrdinal(day, from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0 && string.CompareOrdinal(day, until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0);

            (await client.Ids(Corpus.Conformance, $$"""{ "day": { "gte": "{{from:yyyy-MM-dd}}", "lt": "{{until:yyyy-MM-dd}}" } }"""))
                .Should().Equal(expected, $"{unit} {timezone}: the days of {group["bucket"]}");
            seen += expected.Count;
        }

        seen.Should().Be(rows.Count);
    }

    // ---- below a day: elapsed time ---------------------------------------------------------------

    [Theory]
    [InlineData("hour", null, 3600)]
    [InlineData("hour", Berlin, 3600)]
    [InlineData("hour", "Asia/Kolkata", 3600)]
    [InlineData("minute", Berlin, 60)]
    [InlineData("second", Berlin, 1)]
    public async Task Below_a_day_a_buckets_rows_are_those_from_its_key_to_the_key_plus_one_unit_of_elapsed_time(string unit, string? timezone, int seconds)
    {
        var client = await Transport();
        var rows = Corpus.Rows(Corpus.Shipment);
        var level = await Level(client, Corpus.Shipment, Key("loadStart", unit, timezone, null));
        var seen = new List<Guid>();

        foreach (var group in level.Items)
        {
            var key = group!["bucket"]!.GetValue<string>();
            var start = CalendarRange.Instant(key);
            var expected = Sorted(rows.Where(row => Truncation.Truncate(Corpus.Date(row, "loadStart")!.Value, unit, timezone ?? "UTC") == key));
            var leaves = await client.Ids(Corpus.Shipment, $$"""{ "loadStart": {{CalendarRange.Condition((start, start.AddSeconds(seconds)))}} }""");

            leaves.Should().Equal(expected, $"{unit} {timezone}: {key}");
            leaves.Should().HaveCount((int)Number(group["count"])!.Value);
            seen.AddRange(leaves);
        }

        seen.Should().BeEquivalentTo(rows.Select(row => row.Id));
    }

    [Fact]
    public async Task The_hour_a_clock_shows_twice_is_two_groups_so_an_hour_on_the_clock_is_no_bucket()
    {
        var client = await Transport();
        var (first, second) = (Corpus.Row(Corpus.Shipment, "dst-autumn-first"), Corpus.Row(Corpus.Shipment, "dst-autumn-second"));
        var (firstAt, secondAt) = (Corpus.Date(first, "loadStart")!.Value, Corpus.Date(second, "loadStart")!.Value);

        // Both rows load at the same time on Berlin's clock, an hour of elapsed time apart.
        Truncation.WallOf(firstAt, Berlin).Should().Be(Truncation.WallOf(secondAt, Berlin));
        (secondAt - firstAt).Should().Be(TimeSpan.FromHours(1));

        var hours = await Level(client, Corpus.Shipment, Key("loadStart", "hour", Berlin, null));
        var keys = hours.Items.Select(group => group!["bucket"]!.GetValue<string>()).ToList();

        keys.Should().Contain([Truncation.Truncate(firstAt, "hour", Berlin), Truncation.Truncate(secondAt, "hour", Berlin)]);
        Truncation.Truncate(firstAt, "hour", Berlin).Should().NotBe(Truncation.Truncate(secondAt, "hour", Berlin), "one hour on the clock, two buckets");

        // Each pass is its own range of one elapsed hour; the two together are the clock's hour.
        foreach (var (row, at) in new[] { (first, firstAt), (second, secondAt) })
        {
            var start = CalendarRange.Instant(Truncation.Truncate(at, "hour", Berlin));

            (await client.Ids(Corpus.Shipment, $$"""{ "loadStart": {{CalendarRange.Condition((start, start.AddHours(1)))}} }""")).Should().Equal(row.Id);
        }
    }

    // ---- no date ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("optionalMoment")]
    [InlineData("optionalDay")]
    public async Task The_rows_without_a_date_are_the_group_keyed_null_asked_with_eq_null_and_no_range_holds_them(string member)
    {
        var client = await Conformance();
        var rows = Corpus.Rows(Corpus.Conformance);
        var without = Sorted(rows.Where(row => !Corpus.Present(row, member)));

        without.Should().NotBeEmpty("the corpus holds a row without the member");

        var level = await Level(client, Corpus.Conformance, Key(member, "year", null, null));

        level.Items.Should().ContainSingle(group => group!["bucket"] == null, "null and absent are one group, keyed null");
        Number(level.Items.Single(group => group!["bucket"] == null)!["count"]).Should().Be(without.Count);

        (await client.Ids(Corpus.Conformance, $$"""{ "{{member}}": { "eq": null } }""")).Should().Equal(without);

        var inSome = new List<Guid>();

        foreach (var group in level.Items.Where(group => group!["bucket"] is not null))
        {
            var (from, until) = CalendarRange.Days(CalendarRange.DayOf(CalendarRange.Instant(group!["bucket"]!.GetValue<string>()), "UTC"), "year");
            var condition = member == "optionalDay"
                ? $$"""{ "gte": "{{from:yyyy-MM-dd}}", "lt": "{{until:yyyy-MM-dd}}" }"""
                : CalendarRange.Condition((CalendarRange.StartOfDay(from, "UTC"), CalendarRange.StartOfDay(until, "UTC")));
            var leaves = await client.Ids(Corpus.Conformance, $$"""{ "{{member}}": {{condition}} }""");

            leaves.Should().HaveCount((int)Number(group["count"])!.Value);
            inSome.AddRange(leaves);
        }

        inSome.Should().BeEquivalentTo(Sorted(rows.Where(row => Corpus.Present(row, member))));
        inSome.Should().NotIntersectWith(without);
    }

    // ---- there is no bucket condition -------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task DateTrunc_is_no_operator_of_a_condition_under_either_contract(int contract)
    {
        var answer = await (await Transport()).Contract(contract).MatchRawAsync(Corpus.Shipment, """{ "loadStart": { "dateTrunc": { "unit": "day", "eq": "2026-03-05T00:00:00Z" } } }""");

        answer.ShouldRefuseExactly("UNKNOWN_OPERATOR").Should().Be("'dateTrunc' is not an operator.");
    }

    [Fact]
    public async Task Explain_lists_the_range_operators_for_a_date_column_and_for_a_dateTrunc_key_and_no_bucket_operator()
    {
        var client = await Transport();
        var column = await client.ExplainHereAsync(new JsonObject { ["query"] = Json.Request(Corpus.Shipment, """[{ "project": { "id": 1, "loadStart": 1 } }]""") });
        var key = await client.ExplainHereAsync(new JsonObject
        {
            ["query"] = Json.Request(Corpus.Shipment, $$"""[{ "group": { "by": [{ "dateTrunc": {{Key("loadStart", "month", Berlin, null)}}, "as": "month" }], "fields": { "count": { "count": true } } } }]"""),
        });

        static string Filter(WireAnswer answer, string path)
        {
            var columns = answer.Body!["result"]!["columns"]!.AsArray();

            return answer.Body["result"]!["capabilities"]![columns.Single(entry => entry!["path"]!.GetValue<string>() == path)!["can"]!.GetValue<string>()]!["can"]!["filter"]!.ToJsonString();
        }

        Filter(column, "loadStart").Should().Be("""["eq","neq","gt","gte","lt","lte","in","nin","exists"]""");
        Filter(key, "month").Should().Be("""["eq","neq","gt","gte","lt","lte","in","nin","exists"]""");

        var refused = await client.ExplainHereAsync(new JsonObject { ["query"] = Json.Request(Corpus.Shipment, """[{ "match": { "loadStart": { "dateTrunc": { "unit": "day", "eq": "2026-03-05T00:00:00Z" } } } }]""") });

        refused.Body!["valid"]!.GetValue<bool>().Should().BeFalse();
        refused.Body["errors"]![0]!["code"]!.GetValue<string>().Should().Be("UNKNOWN_OPERATOR");
        refused.Body["errors"]![0]!["basis"]!.GetValue<string>().Should().Be("request");
    }
}
