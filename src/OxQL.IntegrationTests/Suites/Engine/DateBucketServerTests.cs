using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fleet;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The bucket the engine computes for a <c>dateTrunc</c> condition against the database's own
/// <c>$dateTrunc</c>, which is what a group key runs (DG2): for every unit, in zones with and without
/// daylight saving, a half-hour and a quarter-hour offset, a half-hour saving and a saving that
/// starts at midnight, across both changes of several years, the year's ends and every week start.
/// The database is asked, not assumed: the first and the last millisecond of the engine's range have
/// the key the database gives the instant, the millisecond before the range and its end have another.
/// That is the bucket itself, whatever its key is: where a zone shows a midnight twice the database's
/// key is the second of the two, and the bucket still starts at the first. A member that holds a date
/// without a time is truncated from the midnight of its day in the zone; the days the engine's range
/// holds are the days the database gives that key.
/// </summary>
[Trait("Category", "Integration")]
public class DateBucketServerTests
{
    private static readonly string[] Units = ["year", "quarter", "month", "week", "day", "hour", "minute", "second"];

    /// <summary>The zones: no saving, the usual hour of saving on both hemispheres, offsets of 30 and 45 minutes, a saving of 30 minutes, and savings that begin or end at midnight.</summary>
    public static TheoryData<string> Zones => new()
    {
        "UTC", "Europe/Berlin", "America/New_York", "Asia/Kolkata", "Pacific/Chatham", "Australia/Lord_Howe", "America/Havana", "America/Santiago", "Australia/Sydney",
    };

    /// <summary>
    /// The instants asked: every 20 minutes of the three days around each change of the zone in 2025
    /// and 2026, every 7 hours and 13 minutes of the weeks around each new year, and one a day in between.
    /// </summary>
    private static List<DateTime> Instants(TimeZoneInfo zone)
    {
        var instants = new SortedSet<DateTime>();
        var from = new DateTime(2024, 12, 20, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc);

        for (var day = from; day < until; day = day.AddDays(1))
        {
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

    private static TimeZoneInfo ZoneOf(string id) => id == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);

    private static BsonDocument Trunc(BsonValue date, string unit, string zone, string weekStart)
    {
        var trunc = new BsonDocument { ["date"] = date, ["unit"] = unit, ["timezone"] = zone };

        if (unit == "week")
            trunc["startOfWeek"] = weekStart;

        return new BsonDocument("$dateTrunc", trunc);
    }

    private static async Task<List<BsonDocument>> AskAsync(IMongoDatabase database, IEnumerable<BsonDocument> documents, BsonDocument projection)
    {
        var pipeline = new[] { new BsonDocument("$documents", new BsonArray(documents)), new BsonDocument("$project", projection) };

        return await (await database.AggregateAsync(PipelineDefinition<NoPipelineInput, BsonDocument>.Create(pipeline))).ToListAsync();
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task The_bucket_of_an_instant_is_the_one_the_database_truncates_it_to_for_every_unit(string id)
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("date_bucket");
        var zone = ZoneOf(id);
        var instants = Instants(zone);
        var differences = new List<string>();

        foreach (var unit in Units)
            foreach (var (weekStart, firstDay) in unit == "week"
                ? new[] { ("monday", DayOfWeek.Monday), ("sunday", DayOfWeek.Sunday), ("saturday", DayOfWeek.Saturday), ("wednesday", DayOfWeek.Wednesday) }
                : [("monday", DayOfWeek.Monday)])
            {
                var buckets = instants.Select(instant => (Instant: instant, Bucket: DateBucket.Of(instant, unit, zone, firstDay)!.Value)).ToList();
                var answers = await AskAsync(owned.Database,
                    buckets.Select((entry, index) => new BsonDocument
                    {
                        ["n"] = index, ["at"] = entry.Instant, ["first"] = entry.Bucket.From, ["before"] = entry.Bucket.From.AddMilliseconds(-1),
                        ["last"] = entry.Bucket.Until.AddMilliseconds(-1), ["end"] = entry.Bucket.Until,
                    }),
                    new BsonDocument
                    {
                        ["n"] = 1, ["at"] = Trunc("$at", unit, id, weekStart), ["first"] = Trunc("$first", unit, id, weekStart), ["before"] = Trunc("$before", unit, id, weekStart),
                        ["last"] = Trunc("$last", unit, id, weekStart), ["end"] = Trunc("$end", unit, id, weekStart),
                    });

                answers.Should().HaveCount(buckets.Count);

                foreach (var answer in answers)
                {
                    var (instant, bucket) = buckets[answer["n"].AsInt32];
                    var key = answer["at"].ToUniversalTime();

                    instant.Should().BeOnOrAfter(bucket.From).And.BeBefore(bucket.Until);

                    if (answer["first"].ToUniversalTime() != key || answer["last"].ToUniversalTime() != key || answer["before"].ToUniversalTime() == key || answer["end"].ToUniversalTime() == key)
                        differences.Add($"{unit} {weekStart} {instant:O}: engine [{bucket.From:O}, {bucket.Until:O}), the database's key is {key:O}; it truncates the first millisecond to {answer["first"].ToUniversalTime():O}, the one before to {answer["before"].ToUniversalTime():O}, the last to {answer["last"].ToUniversalTime():O}, the end to {answer["end"].ToUniversalTime():O}");
                }
            }

        differences.Should().BeEmpty($"in {id} the engine's bucket is the database's");
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task The_days_a_bucket_holds_are_the_days_the_database_gives_its_key_on_a_date_without_a_time(string id)
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("date_bucket_days");
        var zone = ZoneOf(id);
        var days = Enumerable.Range(0, 760).Select(offset => new DateOnly(2024, 12, 20).AddDays(offset)).ToList();
        var differences = new List<string>();

        foreach (var unit in Units)
        {
            // What the compiler emits for a date without a time: its calendar parts read as a local date of the zone.
            var stored = "$d";
            BsonValue local = id == "UTC" ? stored : new BsonDocument("$dateFromParts", new BsonDocument
            {
                ["year"] = new BsonDocument("$year", stored), ["month"] = new BsonDocument("$month", stored), ["day"] = new BsonDocument("$dayOfMonth", stored), ["timezone"] = id,
            });
            var answers = await AskAsync(owned.Database,
                days.Select((day, index) => new BsonDocument { ["n"] = index, ["d"] = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) }),
                new BsonDocument { ["n"] = 1, ["key"] = Trunc(local, unit, id, "monday") });
            var keys = answers.ToDictionary(answer => days[answer["n"].AsInt32], answer => answer["key"].ToUniversalTime());

            foreach (var group in keys.GroupBy(entry => entry.Value))
            {
                var held = DateBucket.Days(DateBucket.Of(group.Key, unit, zone)!.Value, zone);
                var expected = group.Select(entry => entry.Key).Order().ToList();
                var observed = days.Where(day => day >= held.From && day < held.Until).ToList();

                // The first and the last key of the window hold days outside it; only what lies within is compared.
                if (!observed.SequenceEqual(expected))
                    differences.Add($"{unit} key {group.Key:O}: the database gives it {expected.Count} days from {expected[0]:O}, the engine's range [{held.From:O}, {held.Until:O}) holds {observed.Count} of the window");
            }

            // A day written as the operand names the bucket of its own midnight in the zone: the bucket of the key the database gives the day.
            foreach (var (day, key) in keys)
                if (DateBucket.Of(DateBucket.Midnight(day, zone), unit, zone) != DateBucket.Of(key, unit, zone))
                    differences.Add($"{unit} day {day:O}: the database's key is {key:O}, whose bucket is not the one of the day's midnight, which starts at {DateBucket.Of(DateBucket.Midnight(day, zone), unit, zone)!.Value.From:O}");
        }

        differences.Should().BeEmpty($"in {id} a date's bucket is the database's");
    }
}
