using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Mongo;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Differential;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// The spellings a <c>dateTrunc</c> takes for its zone, on a server: what binds is a zone the
/// database computes with, and two spellings of one zone answer the same buckets; what does not
/// bind is refused with <c>INVALID_TIMEZONE</c> and never reaches the database.
/// </summary>
[Trait("Category", "Integration")]
public class GroupTimezoneTests
{
    private static string Days(string timezone) =>
        $$"""[{ "group": { "by": [{ "dateTrunc": { "path": "moment", "unit": "day", "timezone": "{{timezone}}" }, "as": "day" }], "fields": { "n": { "count": true } } } }, { "sort": [{ "day": "asc" }] }, { "page": { "limit": 500 } }]""";

    private static async Task<List<string>> BucketsAsync(string timezone)
    {
        var answer = await (await EdgeCorpus.ClientAsync()).SendAsync(EdgeCorpus.Entity, Days(timezone));

        answer.ShouldBeOk(timezone);

        return Walks.Rows(answer);
    }

    [Theory]
    [InlineData("+02:00", "Etc/GMT-2")]
    [InlineData("+0200", "+02:00")]
    [InlineData("+02", "+02:00")]
    [InlineData("-09:30", "-0930")]
    [InlineData("+14:00", "Etc/GMT-14")]
    [InlineData("-12", "Etc/GMT+12")]
    [InlineData("Etc/GMT", "UTC")]
    [InlineData("Etc/UTC", "UTC")]
    [InlineData("GMT", "UTC")]
    [InlineData("+00:00", "UTC")]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("  Europe/Berlin ", "Europe/Berlin")]
    [InlineData("Asia/Kolkata", "+05:30")]
    public async Task Two_spellings_of_one_zone_answer_the_same_buckets(string one, string other)
    {
        var buckets = await BucketsAsync(one);

        buckets.Should().NotBeEmpty();
        buckets.Should().Equal(await BucketsAsync(other));
    }

    [Fact]
    public async Task An_offset_moves_the_day()
    {
        (await BucketsAsync("+14:00")).Should().NotEqual(await BucketsAsync("-12"));
    }

    /// <summary>
    /// The host's zone table and the database's are two tables. Every zone the host knows, by the
    /// id the host lists it under and by the other platform's id for it, is answered with buckets
    /// or with the code of the zone: a zone only one of the two knows is the request's, never a fault.
    /// </summary>
    [Fact]
    public async Task Every_zone_the_host_knows_is_answered_with_buckets_or_with_the_code_of_the_zone()
    {
        var client = await EdgeCorpus.ClientAsync();
        var ids = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            ids.Add(zone.Id);

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana))
                ids.Add(iana);

            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows))
                ids.Add(windows);
        }

        // Ids of zones the table gained late, which a database with an older table does not have.
        ids.UnionWith(["America/Ciudad_Juarez", "Europe/Kyiv", "America/Nuuk", "Pacific/Kanton", "Asia/Qostanay", "Antarctica/Troll"]);

        var answered = 0;
        var refused = new List<string>();

        foreach (var id in ids)
        {
            var answer = await client.SendAsync(EdgeCorpus.Entity, Days(id));

            if (answer.StatusCode == 200)
            {
                answered++;
                continue;
            }

            answer.StatusCode.Should().BeOneOf([400, 422], $"{id}: {answer}");
            answer.ErrorCodes.Should().StartWith("INVALID_TIMEZONE", id);
            refused.Add(id);
        }

        answered.Should().BeGreaterThan(50, $"refused: {string.Join(", ", refused)}");
    }

    /// <summary>The code the engine reads a zone the database does not have by is the one the database answers with.</summary>
    [Fact]
    public async Task The_database_refuses_a_zone_it_does_not_know_with_the_code_the_engine_reads()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("zone_code");

        var rows = (await MongoFixture.ClientAsync()).GetDatabase(owned.Name).GetCollection<BsonDocument>("tmp_zone");

        await rows.InsertOneAsync(new BsonDocument("at", DateTime.UtcNow));

        var truncated = new BsonDocument("$project", new BsonDocument("day", new BsonDocument("$dateTrunc", new BsonDocument { ["date"] = "$at", ["unit"] = "day", ["timezone"] = "Mars/Olympus" })));

        var refused = await FluentActions.Awaiting(() => rows.Aggregate<BsonDocument>(new[] { truncated }).ToListAsync()).Should().ThrowAsync<MongoCommandException>();

        refused.Which.Code.Should().Be(MongoQueryEngine.UnknownZoneCode);
    }

    [Theory]
    [InlineData("CET")]
    [InlineData("Europe//Berlin")]
    [InlineData("Mars/Olympus")]
    [InlineData("+15:00")]
    [InlineData("+2")]
    [InlineData("+02:60")]
    [InlineData("Etc/GMT+13")]
    [InlineData("Etc/GMT+14")]
    [InlineData("Etc/GMT+15")]
    [InlineData("Etc/GMT-15")]
    [InlineData("02:00")]
    public async Task What_is_no_zone_is_refused_before_the_database_is_asked(string timezone)
    {
        var answer = await (await EdgeCorpus.ClientAsync()).SendAsync(EdgeCorpus.Entity, Days(timezone));

        answer.ShouldRefuse("INVALID_TIMEZONE", 400);
    }
}
