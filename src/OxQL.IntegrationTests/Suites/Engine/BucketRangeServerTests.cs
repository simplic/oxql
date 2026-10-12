using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Group;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The buckets of a <c>dateTrunc</c> group key against the range a client sends for a group's rows,
/// on rows laid around every change of the clock of 2025 and 2026, around both new years and
/// one a day in between, in a database of the test's own. The database computes the key; the range is
/// the client's (<see cref="CalendarRange"/>). What holds:
/// <list type="bullet">
/// <item>For <c>day</c>, <c>week</c>, <c>month</c>, <c>quarter</c> and <c>year</c> the calendar range
/// of the bucket in the zone answers exactly the group's count, in zones without and with daylight
/// saving, with offsets of 30 and 45 minutes, and where a zone skips a midnight or shows one twice.</item>
/// <item>The key is the bucket's first instant, except where the zone shows the bucket's midnight
/// twice: there the key is the second of the two and the bucket starts at the first, so a range
/// computed from the key's instant loses the hour between them.</item>
/// <item>Below a day a bucket is one unit of elapsed time from its key, whatever the clock shows:
/// with a change of the clock by half an hour the hours of one zone start at <c>:00</c> for a part
/// of the year and at <c>:30</c> for the rest.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class BucketRangeServerTests
{
    private static string Collection => LabService.Transport.Model.Entities[Corpus.Shipment].Collection;

    private static readonly EngineDirect Direct = new(LabService.Transport.Model);

    private static TimeZoneInfo ZoneOf(string id) => id == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);

    /// <summary>
    /// The instants of the rows: every 20 minutes of the three days around each change of the zone's
    /// clock in 2025 and 2026, every 7 hours and 13 minutes of the weeks around each new year, and,
    /// when <paramref name="daily"/>, one a day in between.
    /// </summary>
    private static List<DateTime> Instants(TimeZoneInfo zone, bool daily)
    {
        var instants = new SortedSet<DateTime>();
        var from = new DateTime(2024, 12, 20, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc);

        for (var day = from; day < until; day = day.AddDays(1))
        {
            if (daily)
                instants.Add(day.AddHours(10).AddMinutes(7).AddSeconds(31).AddMilliseconds(250));

            if (zone.GetUtcOffset(day) != zone.GetUtcOffset(day.AddDays(1)))
                for (var at = day.AddDays(-1); at < day.AddDays(2); at = at.AddMinutes(20))
                    instants.Add(at);

            if (day is { Month: 12, Day: > 24 } or { Month: 1, Day: < 8 })
                for (var at = day; at < day.AddDays(1); at = at.AddMinutes(433))
                    instants.Add(at);
        }

        return [.. instants];
    }

    private sealed class Rows(TestDatabase owned, IQueryEngine engine, IReadOnlyList<DateTime> instants) : IAsyncDisposable
    {
        private readonly RequestContext context = Direct.Context(Options());

        public IReadOnlyList<DateTime> Instants { get; } = instants;

        /// <summary>The groups of a <c>dateTrunc</c> key over the rows, key to count, in the key's order.</summary>
        public async Task<List<(DateTime Key, int Count)>> GroupsAsync(string unit, string zone, string weekStart = "monday")
        {
            var groups = await Direct.ExecuteAsync(engine, Corpus.Shipment, $$"""
                [ { "group": { "by": [{ "dateTrunc": { "path": "loadStart", "unit": "{{unit}}", "timezone": "{{zone}}", "weekStart": "{{weekStart}}" }, "as": "bucket" }], "fields": { "count": { "count": true } } } },
                  { "sort": [{ "bucket": "asc" }] }, { "page": { "limit": 5000 } } ]
                """, context);

            groups.Count.Should().BeLessThan(5000, "the level is one page");

            return groups.Select(group => (CalendarRange.Instant(group!["bucket"]!.GetValue<string>()), group["count"]!.GetValue<int>())).ToList();
        }

        /// <summary>The number of rows a range on the member answers, as the engine counts them.</summary>
        public async Task<int> CountAsync((DateTime Start, DateTime End) range)
        {
            var total = await Direct.ExecuteAsync(engine, Corpus.Shipment, $$"""
                [ { "match": { "loadStart": {{CalendarRange.Condition(range)}} } }, { "group": { "by": [], "fields": { "count": { "count": true } } } } ]
                """, context);

            return total.Count == 0 ? 0 : total.Single()!["count"]!.GetValue<int>();
        }

        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    private static OxQLOptions Options() => Direct.Options(options => options.Limits.MaxPageSize = 5000);

    private static async Task<Rows> SeedAsync(string zone, bool daily)
    {
        var owned = await MongoFixture.CreateDatabaseAsync("bucket_range");
        var instants = Instants(ZoneOf(zone), daily);

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(instants.Select(instant => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["LoadStart"] = instant,
        }));

        return new Rows(owned, Direct.Engine(await MongoFixture.ClientAsync(), owned.Name, Options()), instants);
    }

    /// <summary>No saving, the usual hour of saving on both hemispheres, offsets of 30 and 45 minutes, a saving of 30 minutes, a midnight skipped and a midnight shown twice.</summary>
    public static TheoryData<string> Zones => new()
    {
        "UTC", "Europe/Berlin", "America/New_York", "Asia/Kolkata", "Pacific/Chatham", "Australia/Lord_Howe", "Australia/Sydney", "America/Santiago", "America/Havana",
    };

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task The_calendar_range_of_a_day_or_coarser_bucket_in_the_zone_answers_exactly_the_groups_count_across_every_change_of_the_clock(string zone)
    {
        await using var rows = await SeedAsync(zone, daily: true);
        var differences = new List<string>();

        foreach (var (unit, weekStart) in new[] { ("day", "monday"), ("week", "monday"), ("week", "sunday"), ("month", "monday"), ("quarter", "monday"), ("year", "monday") })
        {
            var groups = await rows.GroupsAsync(unit, zone, weekStart);
            DateTime? previousEnd = null;

            groups.Sum(group => group.Count).Should().Be(rows.Instants.Count, $"{unit}: every row is in one group");

            foreach (var (key, count) in groups)
            {
                var range = CalendarRange.Of(key, unit, zone, weekStart);
                var answered = await rows.CountAsync(range);

                if (answered != count)
                    differences.Add($"{unit} {weekStart} key {key:O}: the group counts {count}, the range [{range.Start:O}, {range.End:O}) answers {answered}");

                if (previousEnd is { } end && range.Start < end)
                    differences.Add($"{unit} {weekStart} key {key:O}: its range starts at {range.Start:O}, before the end {end:O} of the one before");

                previousEnd = range.End;
            }
        }

        differences.Should().BeEmpty($"in {zone} a bucket of a day or more is a calendar range");
    }

    [Theory]
    [InlineData("Europe/Berlin", 23, 25)]
    [InlineData("UTC", 24, 24)]
    public async Task A_day_is_as_long_as_the_calendar_makes_it_and_its_key_is_its_first_instant(string zone, int shortest, int longest)
    {
        await using var rows = await SeedAsync(zone, daily: true);
        var days = await rows.GroupsAsync("day", zone);
        var lengths = days.Select(day => CalendarRange.Of(day.Key, "day", zone)).Select(range => (range.End - range.Start).TotalHours).ToList();

        lengths.Min().Should().Be(shortest);
        lengths.Max().Should().Be(longest);
        lengths.Count(length => length != 24).Should().Be(zone == "UTC" ? 0 : 4, "both changes of 2025 and of 2026");
        days.Should().OnlyContain(day => CalendarRange.Of(day.Key, "day", zone).Start == day.Key);
    }

    [Fact]
    public async Task Where_a_zone_shows_a_midnight_twice_the_key_is_the_second_and_a_range_from_the_key_loses_the_hour_before_it()
    {
        const string Havana = "America/Havana";

        await using var rows = await SeedAsync(Havana, daily: true);

        // Cuba's clock goes from 01:00 back to 00:00 on the first Sunday of November: 2026-11-01 has two midnights.
        var first = CalendarRange.StartOfDay(new DateOnly(2026, 11, 1), Havana);
        var next = CalendarRange.StartOfDay(new DateOnly(2026, 11, 2), Havana);

        first.Should().Be(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc));
        (next - first).TotalHours.Should().Be(25);

        foreach (var unit in new[] { "day", "month" })
        {
            var groups = await rows.GroupsAsync(unit, Havana);
            var group = groups.Single(candidate => CalendarRange.DayOf(candidate.Key, Havana) == new DateOnly(2026, 11, 1));
            var end = CalendarRange.Of(group.Key, unit, Havana).End;

            group.Key.Should().Be(first.AddHours(1), $"{unit}: the key the database gives the bucket is the second midnight");

            // The rows of the hour between the two midnights are the group's: three rows, 20 minutes apart.
            (await rows.CountAsync((first, end))).Should().Be(group.Count, $"{unit}: the calendar range from the first midnight");
            (await rows.CountAsync((group.Key, end))).Should().Be(group.Count - 3, $"{unit}: a range that starts at the key");

            // And no other group takes them: the bucket before ends at the first midnight.
            var before = groups[groups.IndexOf(group) - 1];

            CalendarRange.Of(before.Key, unit, Havana).End.Should().Be(first);
        }
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Berlin")]
    [InlineData("Asia/Kolkata")]
    [InlineData("Pacific/Chatham")]
    [InlineData("Australia/Lord_Howe")]
    public async Task An_hour_bucket_is_one_hour_of_elapsed_time_from_its_key_across_every_change_of_the_clock(string zone)
    {
        await using var rows = await SeedAsync(zone, daily: false);
        var hours = await rows.GroupsAsync("hour", zone);
        var differences = new List<string>();

        hours.Sum(hour => hour.Count).Should().Be(rows.Instants.Count);

        for (var index = 0; index < hours.Count; index++)
        {
            var (key, count) = hours[index];
            var answered = await rows.CountAsync((key, key.AddHours(1)));

            if (answered != count)
                differences.Add($"key {key:O}: the group counts {count}, the hour from the key answers {answered}");

            if (index > 0 && key < hours[index - 1].Key.AddHours(1))
                differences.Add($"key {key:O} lies within the hour of {hours[index - 1].Key:O}");
        }

        differences.Should().BeEmpty($"in {zone} an hour bucket is the hour that follows its key");
    }

    [Fact]
    public async Task Hour_buckets_follow_elapsed_time_not_the_clock_two_for_an_hour_shown_twice_and_off_the_clocks_hour_after_a_change_by_half_an_hour()
    {
        // Berlin, 2026-10-25: the clock shows 02:00 to 03:00 twice. Each pass is a bucket of its own.
        await using (var berlin = await SeedAsync("Europe/Berlin", daily: false))
        {
            var hours = await berlin.GroupsAsync("hour", "Europe/Berlin");
            var shown = hours.Where(hour => Truncation.WallOf(hour.Key, "Europe/Berlin") == new DateTime(2026, 10, 25, 2, 0, 0)).ToList();

            shown.Select(hour => hour.Key).Should().Equal(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc));
            shown.Should().OnlyContain(hour => hour.Count == 3);
            hours.Should().OnlyContain(hour => Truncation.WallOf(hour.Key, "Europe/Berlin").Minute == 0, "a zone that changes by whole hours keeps its buckets on the clock's hours");
        }

        // Lord Howe changes its clock by 30 minutes: its hours start on the clock's hour for one part of the year and at half past for the other.
        await using var lordHowe = await SeedAsync("Australia/Lord_Howe", daily: false);
        var minutes = (await lordHowe.GroupsAsync("hour", "Australia/Lord_Howe")).Select(hour => Truncation.WallOf(hour.Key, "Australia/Lord_Howe").Minute).Distinct().Order().ToList();

        minutes.Should().Equal(0, 30);
    }
}
