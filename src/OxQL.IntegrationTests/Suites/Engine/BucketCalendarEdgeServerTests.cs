using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The key of a <c>dateTrunc</c> bucket at the two ends of the calendar, on rows of the test's own:
/// rows that hold the first and the last instant a .NET service can store, rows near them, rows of
/// an ordinary year, a null and a row without the member. A bucket starts at a local boundary, so
/// the bucket of the first instant starts before the calendar in a zone east of its meridian and in
/// a week that starts on another day. What holds for every unit and zone: every key is an ISO-8601
/// instant or null, no two groups share a key, the groups count every row once, the range from a key
/// to the next answers the group's count, and a cursor walks the groups in either direction.
/// </summary>
[Trait("Category", "Integration")]
public class BucketCalendarEdgeServerTests
{
    private const string First = "0001-01-01T00:00:00Z";

    private const string Last = "9999-12-31T23:59:59.9999999Z";

    private static string Collection => LabService.Transport.Model.Entities[Corpus.Shipment].Collection;

    private static readonly EngineDirect Direct = new(LabService.Transport.Model);

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>The dated rows: three at the first instant, others in its first days, an ordinary year, and two at the last instant with one a day before it.</summary>
    private static readonly IReadOnlyList<DateTime> Instants =
    [
        DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
        DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
        DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
        Utc(1, 1, 1, 0, 20),
        Utc(1, 1, 1, 12),
        Utc(1, 1, 2, 10),
        Utc(1, 1, 9, 3),
        Utc(1, 2, 15),
        Utc(2, 6, 1),
        Utc(2026, 3, 29, 0, 30),
        Utc(2026, 3, 29, 1, 30),
        Utc(2026, 6, 1, 8),
        Utc(2026, 12, 31, 23, 30),
        Utc(9999, 12, 30, 12),
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
    ];

    /// <summary>The rows without a date: one null, one without the member.</summary>
    private const int Undated = 2;

    private sealed class Rows(TestDatabase owned, IQueryEngine engine) : IAsyncDisposable
    {
        private readonly RequestContext context = Direct.Context(Options());

        public async Task<QueryResult> AnswerAsync(string pipeline)
        {
            var outcome = await engine.ExecuteAsync(EngineDirect.Request(Corpus.Shipment, pipeline), context);

            outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "");

            return ((QueryOutcome.Success)outcome).Result;
        }

        /// <summary>The groups of a <c>dateTrunc</c> key, key to count, sorted by the key; all of them, or one page.</summary>
        public async Task<(List<(string? Key, int Count)> Groups, string? Next)> GroupsAsync(string unit, string zone, string weekStart, string direction = "asc", int limit = 500, string? cursor = null)
        {
            var page = new JsonObject { ["limit"] = limit };

            if (cursor is not null)
                page["cursor"] = cursor;

            var answer = await AnswerAsync($$"""
                [ { "group": { "by": [{ "dateTrunc": { "path": "loadStart", "unit": "{{unit}}", "timezone": "{{zone}}", "weekStart": "{{weekStart}}" }, "as": "bucket" }], "fields": { "count": { "count": true } } } },
                  { "sort": [{ "bucket": "{{direction}}" }] }, { "page": {{page.ToJsonString()}} } ]
                """);

            return (answer.Items.Select(group => (group!["bucket"]?.GetValue<string>(), group["count"]!.GetValue<int>())).ToList(), answer.PageInfo.NextCursor);
        }

        /// <summary>The number of rows a condition on the member answers.</summary>
        public async Task<int> CountAsync(string condition)
        {
            var total = await AnswerAsync($$"""[ { "match": { "loadStart": {{condition}} } }, { "group": { "by": [], "fields": { "count": { "count": true } } } } ]""");

            return total.Items.Count == 0 ? 0 : total.Items.Single()!["count"]!.GetValue<int>();
        }

        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    private static OxQLOptions Options() => Direct.Options(options => options.Limits.MaxPageSize = 500);

    private static BsonDocument Row(BsonValue? loadStart)
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
        };

        if (loadStart is not null)
            row["LoadStart"] = loadStart;

        return row;
    }

    private static async Task<Rows> SeedAsync(IEnumerable<BsonValue?> values)
    {
        var owned = await MongoFixture.CreateDatabaseAsync("bucket_edge");

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(values.Select(Row));

        return new Rows(owned, Direct.Engine(await MongoFixture.ClientAsync(), owned.Name, Options()));
    }

    private static Task<Rows> SeedAsync() =>
        SeedAsync(Instants.Select(instant => (BsonValue?)new BsonDateTime(instant)).Concat([BsonNull.Value, null]));

    private static bool IsInstant(string? key) =>
        key is not null && DateTime.TryParseExact(key, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);

    /// <summary>UTC, east and west of it, and an offset of half an hour: there the hours of the zone start before the calendar does.</summary>
    public static TheoryData<string> Zones => new() { "UTC", "Europe/Berlin", "America/New_York", "Asia/Kolkata", "Pacific/Auckland" };

    private static readonly (string Unit, string WeekStart)[] Units =
    [
        ("year", "monday"), ("quarter", "monday"), ("month", "monday"), ("week", "monday"), ("week", "sunday"),
        ("day", "monday"), ("hour", "monday"), ("minute", "monday"), ("second", "monday"),
    ];

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Every_bucket_key_is_a_date_and_the_range_from_a_key_to_the_next_answers_its_group(string zone)
    {
        await using var rows = await SeedAsync();
        var differences = new List<string>();

        foreach (var (unit, weekStart) in Units)
        {
            var (groups, _) = await rows.GroupsAsync(unit, zone, weekStart);
            var name = $"{unit} {weekStart}";

            groups.Sum(group => group.Count).Should().Be(Instants.Count + Undated, $"{name}: every row is in one group");
            groups.Select(group => group.Key).Should().OnlyHaveUniqueItems(name);

            // Ascending, the rows without a date come first, under null.
            groups[0].Should().Be(((string?)null, Undated), name);

            var dated = groups.Skip(1).ToList();

            foreach (var (key, _) in dated)
                if (!IsInstant(key))
                    differences.Add($"{name}: the key '{key}' is no ISO-8601 instant");

            dated[0].Key.Should().Be(First, $"{name}: the bucket of the first instant starts at it or before it, and is keyed with it");
            dated[0].Count.Should().BeGreaterThanOrEqualTo(3, name);

            for (var index = 0; index < dated.Count; index++)
            {
                var (key, count) = dated[index];
                var condition = index + 1 < dated.Count
                    ? $$"""{ "gte": "{{key}}", "lt": "{{dated[index + 1].Key}}" }"""
                    : $$"""{ "gte": "{{key}}" }""";
                var answered = await rows.CountAsync(condition);

                if (answered != count)
                    differences.Add($"{name} key {key}: the group counts {count}, the range {condition} answers {answered}");
            }

            (await rows.CountAsync("""{ "eq": null }""")).Should().Be(Undated, name);
        }

        differences.Should().BeEmpty($"in {zone}");
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task A_cursor_walks_the_groups_in_either_direction_through_both_ends_of_the_calendar(string zone)
    {
        await using var rows = await SeedAsync();

        foreach (var (unit, weekStart) in new[] { ("month", "monday"), ("week", "sunday"), ("day", "monday"), ("hour", "monday") })
        {
            foreach (var direction in new[] { "asc", "desc" })
            {
                var (whole, _) = await rows.GroupsAsync(unit, zone, weekStart, direction);
                var walked = new List<(string? Key, int Count)>();
                string? cursor = null;

                do
                {
                    var (page, next) = await rows.GroupsAsync(unit, zone, weekStart, direction, limit: 2, cursor);

                    walked.AddRange(page);
                    cursor = next;
                }
                while (cursor is not null && walked.Count <= whole.Count);

                walked.Should().Equal(whole, $"{unit} {weekStart} {direction} in {zone}");
            }

            var (ascending, _) = await rows.GroupsAsync(unit, zone, weekStart);
            var (descending, _) = await rows.GroupsAsync(unit, zone, weekStart, "desc");

            descending.Should().Equal(ascending.AsEnumerable().Reverse(), $"{unit} {weekStart} in {zone}: the two directions are one order");
        }
    }

    [Fact]
    public async Task The_bucket_of_the_first_instant_is_keyed_with_it_where_its_local_boundary_lies_before_the_calendar()
    {
        await using var rows = await SeedAsync();

        // East of the meridian the first of January of year 1 begins in the year before.
        foreach (var zone in new[] { "Europe/Berlin", "Asia/Kolkata", "Pacific/Auckland" })
            foreach (var unit in new[] { "year", "quarter", "month", "day" })
                (await rows.GroupsAsync(unit, zone, "monday")).Groups[1].Key.Should().Be(First, $"{unit} in {zone}");

        // West of it the first instant is an evening of the year before, and its month began earlier still.
        foreach (var unit in new[] { "year", "quarter", "month", "week", "day" })
            (await rows.GroupsAsync(unit, "America/New_York", "monday")).Groups[1].Key.Should().Be(First, $"{unit} in America/New_York");

        // The first of January of year 1 is a Monday: a week that starts on a Sunday began the day before.
        var sundays = (await rows.GroupsAsync("week", "UTC", "sunday")).Groups;
        var mondays = (await rows.GroupsAsync("week", "UTC", "monday")).Groups;

        sundays[1].Should().Be((First, 6), "the six rows of the first six days");
        mondays[1].Should().Be((First, 6), "the same six rows: the week from Monday the first");
        sundays[2].Key.Should().Be("0001-01-07T00:00:00Z");
        mondays[2].Key.Should().Be("0001-01-08T00:00:00Z");

        // Without a zone a day, a month and a year begin within the calendar: the key is the boundary itself.
        (await rows.GroupsAsync("day", "UTC", "monday")).Groups[1].Should().Be((First, 5));
        (await rows.GroupsAsync("month", "UTC", "monday")).Groups[1].Should().Be((First, 7));
    }

    [Fact]
    public async Task The_bucket_of_the_last_instant_is_a_date_in_every_zone()
    {
        await using var rows = await SeedAsync();

        foreach (var zone in new[] { "UTC", "Europe/Berlin", "America/New_York", "Asia/Kolkata", "Pacific/Auckland" })
        {
            foreach (var (unit, weekStart) in Units)
            {
                var (groups, _) = await rows.GroupsAsync(unit, zone, weekStart, "desc", limit: 1);
                var (key, count) = groups.Single();

                IsInstant(key).Should().BeTrue($"{unit} {weekStart} in {zone}: '{key}'");
                count.Should().BeGreaterThanOrEqualTo(2, $"{unit} {weekStart} in {zone}: both rows at the last instant");
                (await rows.CountAsync($$"""{ "gte": "{{key}}" }""")).Should().Be(count, $"{unit} {weekStart} in {zone}");
            }
        }

        (await rows.GroupsAsync("second", "UTC", "monday", "desc", limit: 1)).Groups.Single().Should().Be(("9999-12-31T23:59:59Z", 2));
    }

    [Fact]
    public async Task The_first_and_the_last_instant_read_back_as_dates_in_a_row_a_list_of_values_and_an_aggregate()
    {
        await using var rows = await SeedAsync();

        var ascending = await rows.AnswerAsync("""[ { "match": { "loadStart": { "neq": null } } }, { "sort": [{ "loadStart": "asc" }] }, { "project": { "loadStart": 1 } }, { "page": { "limit": 1 } } ]""");
        var descending = await rows.AnswerAsync("""[ { "sort": [{ "loadStart": "desc" }] }, { "project": { "loadStart": 1 } }, { "page": { "limit": 1 } } ]""");

        ascending.Items.Single()!["loadStart"]!.GetValue<string>().Should().Be(First);
        descending.Items.Single()!["loadStart"]!.GetValue<string>().Should().Be(Last);

        var values = await rows.AnswerAsync("""[ { "values": { "path": "loadStart", "counts": true, "limit": 100 } } ]""");
        var listed = values.Items.ToDictionary(entry => entry!["value"]?.GetValue<string>() ?? "", entry => entry!["count"]!.GetValue<int>());

        listed[First].Should().Be(3);
        listed[Last].Should().Be(2);
        listed.Keys.Where(key => key.Length > 0).Should().OnlyContain(key => IsInstant(key));

        var ends = await rows.AnswerAsync("""[ { "group": { "by": [], "fields": { "from": { "min": "loadStart" }, "to": { "max": "loadStart" } } } } ]""");

        ends.Items.Single()!["from"]!.GetValue<string>().Should().Be(First);
        ends.Items.Single()!["to"]!.GetValue<string>().Should().Be(Last);

        (await rows.CountAsync($$"""{ "eq": "{{First}}" }""")).Should().Be(3);
        (await rows.CountAsync($$"""{ "eq": "{{Last}}" }""")).Should().Be(2);
    }

    [Fact]
    public async Task Dates_another_writer_stored_outside_the_calendar_are_one_group_at_each_end_keyed_with_a_date()
    {
        var first = new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)).MillisecondsSinceEpoch;
        var last = new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)).MillisecondsSinceEpoch;
        const long Day = 86_400_000;

        // Two rows in two different months before the calendar, one at its start, one in an ordinary
        // year, one at its end and two in two different months after it.
        await using var rows = await SeedAsync(new long[] { first - (400 * Day), first - (40 * Day), first, new BsonDateTime(Utc(2026, 6, 1)).MillisecondsSinceEpoch, last, last + (40 * Day), last + (400 * Day) }
            .Select(milliseconds => (BsonValue?)new BsonDateTime(milliseconds)));

        foreach (var zone in new[] { "UTC", "Europe/Berlin", "America/New_York" })
        {
            var (groups, _) = await rows.GroupsAsync("month", zone, "monday");

            groups.Select(group => group.Key).Should().OnlyHaveUniqueItems(zone).And.OnlyContain(key => IsInstant(key), zone);
            groups.Sum(group => group.Count).Should().Be(7, zone);
            groups[0].Should().Be((First, 3), $"{zone}: the rows before the calendar and the one at its start");
            groups[^1].Key.Should().Be(Last, zone);
            groups[^1].Count.Should().Be(2, $"{zone}: the rows past the calendar");

            var walked = new List<(string? Key, int Count)>();
            string? cursor = null;

            do
            {
                var (page, next) = await rows.GroupsAsync("month", zone, "monday", limit: 1, cursor: cursor);

                walked.AddRange(page);
                cursor = next;
            }
            while (cursor is not null && walked.Count <= groups.Count);

            walked.Should().Equal(groups, zone);
        }
    }
}
