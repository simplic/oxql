using FluentAssertions;
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

    [Theory]
    [InlineData("CET")]
    [InlineData("Europe//Berlin")]
    [InlineData("Mars/Olympus")]
    [InlineData("+15:00")]
    [InlineData("+2")]
    [InlineData("+02:60")]
    [InlineData("Etc/GMT+15")]
    [InlineData("02:00")]
    public async Task What_is_no_zone_is_refused_before_the_database_is_asked(string timezone)
    {
        var answer = await (await EdgeCorpus.ClientAsync()).SendAsync(EdgeCorpus.Entity, Days(timezone));

        answer.ShouldRefuse("INVALID_TIMEZONE", 400);
    }
}
