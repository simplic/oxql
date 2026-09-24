using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Group.GroupKit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// Area N, second part (N35–N46): <c>dateTrunc</c> as a group key. The expected buckets are never
/// read off the engine: <see cref="Truncation"/> derives them independently (wall clock in the
/// zone, truncated, back to the instant) and every case compares key for key and count for count.
/// The shipment corpus carries the rows this needs: both passes of the DST transitions, the UTC
/// and the Berlin day disagreeing, the day boundary itself, and ISO week 53 across a year.
/// <para>Ported from the legacy <c>group-datetrunc</c> battery; the engine half only.</para>
/// </summary>
[Trait("Category", "Integration")]
public class GroupDateTruncTests
{
    private const string Berlin = "Europe/Berlin";

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    /// <summary>Every organisation A shipment's <c>loadStart</c>.</summary>
    private static IReadOnlyList<DateTime> LoadStarts()
    {
        var starts = Corpus.Rows(Corpus.Shipment).Select(row => Corpus.Date(row, "loadStart")).ToList();
        starts.Should().OnlyContain(start => start != null, "every shipment carries a loadStart, so no null bucket is expected");

        return starts.Select(start => start!.Value).ToList();
    }

    private static string DateTrunc(string unit, string? timezone, string? weekStart)
    {
        var node = new JsonObject { ["path"] = "loadStart", ["unit"] = unit };

        if (timezone is not null)
            node["timezone"] = timezone;

        if (weekStart is not null)
            node["weekStart"] = weekStart;

        return node.ToJsonString();
    }

    /// <summary>The engine's buckets for one dateTrunc, key to count, in the order it answered.</summary>
    private static async Task<IReadOnlyList<(string Key, int Count)>> EngineBuckets(string unit, string? timezone = null, string? weekStart = null)
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""
            [ { "group": { "by": [{ "dateTrunc": {{DateTrunc(unit, timezone, weekStart)}}, "as": "bucket" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "bucket": "asc" }] },
              { "page": { "limit": 100 } } ]
            """);

        answer.ShouldBeOk($"{unit} {timezone} {weekStart}");
        answer.HasNextPage.Should().BeFalse();

        return answer.Items.Select(item => (item!["bucket"]!.GetValue<string>(), (int)Number(item["total"])!.Value)).ToList();
    }

    private static int CountAt(IEnumerable<(string Key, int Count)> buckets, string key) => buckets.SingleOrDefault(bucket => bucket.Key == key).Count;

    private static string Refused(string dateTrunc) => $$"""[{ "group": { "by": [{ "dateTrunc": {{dateTrunc}}, "as": "bucket" }], "fields": { "total": { "count": true } } } }, { "page": { "limit": 5 } }]""";

    [Theory]
    [InlineData("N36", "year")]
    [InlineData("N36", "quarter")]
    [InlineData("N36", "month")]
    [InlineData("N36", "week")]
    [InlineData("N36", "day")]
    [InlineData("N36", "hour")]
    [InlineData("N35", "minute")]
    [InlineData("N35", "second")]
    public async Task N35_N36_every_unit_buckets_by_the_utc_instant_of_the_local_boundary(string id, string unit)
    {
        _ = id;
        var expected = Truncation.Buckets(LoadStarts(), unit, Berlin);

        var observed = await EngineBuckets(unit, Berlin);

        observed.Should().Equal(expected, unit);
        observed.Sum(bucket => bucket.Count).Should().Be(Corpus.Counts(Corpus.Shipment).A);
    }

    [Fact]
    public async Task N36b_the_bucket_shapes_the_corpus_was_built_to_produce()
    {
        // Spelled out, so a silent re-bucketing of the corpus fails here and not somewhere else.
        (await EngineBuckets("month", Berlin)).Should().HaveCount(7);
        (await EngineBuckets("week", Berlin)).Should().HaveCount(8);
        (await EngineBuckets("day", Berlin)).Should().HaveCount(10);
    }

    [Fact]
    public async Task N39_AC5_an_iana_timezone_is_dst_correct_23_00Z_under_cet_and_22_00Z_under_cest()
    {
        var months = await EngineBuckets("month", Berlin);
        var expectedMonths = Truncation.Buckets(LoadStarts(), "month", Berlin);

        // January and December are CET, June and October CEST: the offset shows in the key itself.
        months.Select(bucket => bucket.Key).Should().Contain(["2025-12-31T23:00:00Z", "2026-05-31T22:00:00Z", "2026-09-30T22:00:00Z", "2026-11-30T23:00:00Z"]);
        CountAt(months, "2025-12-31T23:00:00Z").Should().Be(CountAt(expectedMonths, "2025-12-31T23:00:00Z")).And.Be(1);
        CountAt(months, "2026-05-31T22:00:00Z").Should().Be(CountAt(expectedMonths, "2026-05-31T22:00:00Z"));

        // Each DST pair shares its day bucket; the spring pair is one UTC hour but two local hours apart.
        var days = await EngineBuckets("day", Berlin);
        Truncation.Truncate(Corpus.Date(Corpus.Row(Corpus.Shipment, "dst-spring-before"), "loadStart")!.Value, "day", Berlin).Should().Be("2026-03-28T23:00:00Z");
        Truncation.Truncate(Corpus.Date(Corpus.Row(Corpus.Shipment, "dst-spring-after"), "loadStart")!.Value, "day", Berlin).Should().Be("2026-03-28T23:00:00Z");
        Truncation.Truncate(Corpus.Date(Corpus.Row(Corpus.Shipment, "dst-autumn-first"), "loadStart")!.Value, "day", Berlin).Should().Be("2026-10-24T22:00:00Z");
        Truncation.Truncate(Corpus.Date(Corpus.Row(Corpus.Shipment, "dst-autumn-second"), "loadStart")!.Value, "day", Berlin).Should().Be("2026-10-24T22:00:00Z");
        CountAt(days, "2026-03-28T23:00:00Z").Should().Be(2);
        CountAt(days, "2026-10-24T22:00:00Z").Should().Be(2);

        // The hour buckets keep both pairs apart, which is what proves the day bucket is no accident:
        // the two passes through the repeated autumn hour are two buckets.
        var hours = await EngineBuckets("hour", Berlin);
        foreach (var key in new[] { "2026-03-29T00:00:00Z", "2026-03-29T01:00:00Z", "2026-10-25T00:00:00Z", "2026-10-25T01:00:00Z" })
            CountAt(hours, key).Should().Be(1, key);
    }

    [Fact]
    public async Task N38_AC26_timezone_defaults_to_utc_and_the_utc_and_berlin_day_buckets_disagree_exactly_where_the_corpus_says()
    {
        var instants = LoadStarts();
        var utc = await EngineBuckets("day");
        var berlin = await EngineBuckets("day", Berlin);

        utc.Should().Equal(Truncation.Buckets(instants, "day"));
        berlin.Should().Equal(Truncation.Buckets(instants, "day", Berlin));
        utc.Should().OnlyContain(bucket => bucket.Key.EndsWith("T00:00:00Z", StringComparison.Ordinal));
        berlin.Should().NotContain(bucket => bucket.Key.EndsWith("T00:00:00Z", StringComparison.Ordinal));
        // utc-day-before (23:30Z) is a UTC day of its own and a Berlin day shared with utc-day-after.
        utc.Count.Should().Be(berlin.Count + 1);

        // The default is still UTC: a caller that wants local buckets must ask.
        (await EngineBuckets("month")).Should().OnlyContain(bucket => bucket.Key.EndsWith("-01T00:00:00Z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task N41_AC26_week_start_defaults_to_monday_not_sunday()
    {
        var instants = LoadStarts();
        var bare = await EngineBuckets("week", Berlin);
        var monday = await EngineBuckets("week", Berlin, "monday");
        var sunday = await EngineBuckets("week", Berlin, "sunday");

        bare.Should().Equal(monday).And.Equal(Truncation.Buckets(instants, "week", Berlin, "monday"));
        sunday.Should().Equal(Truncation.Buckets(instants, "week", Berlin, "sunday"));
        bare.Select(bucket => bucket.Key).Should().NotEqual(sunday.Select(bucket => bucket.Key));
        bare.Should().OnlyContain(bucket => Truncation.WeekDayOf(DateTime.Parse(bucket.Key, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Berlin) == "monday");
        sunday.Should().OnlyContain(bucket => Truncation.WeekDayOf(DateTime.Parse(bucket.Key, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Berlin) == "sunday");

        // The year-crossing ISO week: Wed 2026-12-30 and Sat 2027-01-02 share one bucket that starts
        // in the previous calendar year; Tue 2027-01-05 opens the next.
        CountAt(bare, "2026-12-27T23:00:00Z").Should().Be(2);
        CountAt(bare, "2027-01-03T23:00:00Z").Should().Be(1);
    }

    [Theory]
    [InlineData("N42", "monday", "monday")]
    [InlineData("N42", "tuesday", "tuesday")]
    [InlineData("N42", "wednesday", "wednesday")]
    [InlineData("N42", "thursday", "thursday")]
    [InlineData("N42", "friday", "friday")]
    [InlineData("N42", "saturday", "saturday")]
    [InlineData("N42", "sunday", "sunday")]
    [InlineData("N42", "mon", "monday")]
    [InlineData("N42", "tue", "tuesday")]
    [InlineData("N42", "wed", "wednesday")]
    [InlineData("N42", "thu", "thursday")]
    [InlineData("N42", "fri", "friday")]
    [InlineData("N42", "sat", "saturday")]
    [InlineData("N42", "sun", "sunday")]
    [InlineData("N42", "MONDAY", "monday")]
    [InlineData("N42", "Sun", "sunday")]
    [InlineData("N42", "sunDAY", "sunday")]
    public async Task N42_every_week_start_spelling_binds_long_and_short_in_any_case(string id, string written, string means)
    {
        _ = id;
        var observed = await EngineBuckets("week", Berlin, written);

        observed.Should().Equal(Truncation.Buckets(LoadStarts(), "week", Berlin, means), written);
        observed.Should().OnlyContain(bucket => Truncation.WeekDayOf(DateTime.Parse(bucket.Key, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Berlin) == means);
    }

    [Theory]
    [InlineData("N37", "fortnight")]
    [InlineData("N37", "millisecond")]
    [InlineData("N37", "WEEK")]
    [InlineData("N37", "Week")]
    [InlineData("N37", "")]
    public async Task N37_an_unknown_unit_is_refused_and_the_unit_is_matched_case_sensitively(string id, string unit)
    {
        _ = id;
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, Refused(DateTrunc(unit, null, null)));

        answer.ShouldRefuse("INVALID_DATE_TRUNC_UNIT", 400, unit);
        answer.ErrorCodes[0].Should().Be("INVALID_DATE_TRUNC_UNIT");
    }

    [Fact]
    public async Task N43_an_unknown_week_start_shares_the_unit_code_and_drags_a_second_error_naming_the_alias()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""
            [ { "group": { "by": [{ "dateTrunc": {{DateTrunc("week", null, "funday")}}, "as": "bucket" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "bucket": "asc" }] },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldRefuse("INVALID_DATE_TRUNC_UNIT", 400)["message"]!.GetValue<string>().Should().Be("'funday' is not a day of the week.");
        answer.ErrorCodes.Should().Equal("INVALID_DATE_TRUNC_UNIT", "UNKNOWN_PATH");
        answer.Errors[1]["stage"]!.GetValue<int>().Should().Be(1);
    }

    [Theory]
    [InlineData("N40", "Mars/Olympus")]
    [InlineData("N40", "+02:00")]
    [InlineData("N40", "Europe/Kolen")]
    [InlineData("N40", "Berlin")]
    [InlineData("N40", "Europe//Berlin")]
    [InlineData("N40", "CET")]
    public async Task N40_a_non_iana_timezone_and_the_link_name_cet_are_refused(string id, string timezone)
    {
        _ = id;
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, Refused(DateTrunc("day", timezone, null)));

        answer.ShouldRefuse("INVALID_TIMEZONE", 400, timezone);
        answer.ErrorCodes[0].Should().Be("INVALID_TIMEZONE");
    }

    [Fact]
    public async Task N40b_a_case_variant_is_canonicalised_whitespace_is_trimmed_and_utc_etc_utc_and_the_empty_string_mean_utc()
    {
        var instants = LoadStarts();
        var canonical = await EngineBuckets("day", Berlin);
        var utcControl = await EngineBuckets("day", "UTC");
        canonical.Should().NotEqual(utcControl, "the control zone must differ from UTC, or the case proves nothing");

        foreach (var variant in new[] { "europe/berlin", "EUROPE/BERLIN", "Europe/berlin", " Europe/Berlin " })
            (await EngineBuckets("day", variant)).Should().Equal(canonical, $"'{variant}'");

        // A quarter-hour zone resolves, so the offset is read and not assumed.
        var chatham = await EngineBuckets("day", "Pacific/Chatham");
        chatham.Should().Equal(Truncation.Buckets(instants, "day", "Pacific/Chatham"));
        chatham.Should().OnlyContain(bucket => bucket.Key.EndsWith(":15:00Z", StringComparison.Ordinal) || bucket.Key.EndsWith(":45:00Z", StringComparison.Ordinal));

        var bare = await EngineBuckets("day");
        foreach (var utc in new[] { "UTC", "Etc/UTC", "" })
            (await EngineBuckets("day", utc)).Should().Equal(bare, $"'{utc}'");
    }

    [Theory]
    [InlineData("N44", "day")]
    [InlineData("N44", "month")]
    [InlineData("N44", "year")]
    [InlineData("N44", "hour")]
    public async Task N44_AC5_week_start_is_ignored_by_the_engine_for_a_non_week_unit(string id, string unit)
    {
        _ = id;
        (await EngineBuckets(unit, Berlin, "sunday")).Should().Equal(await EngineBuckets(unit, Berlin));
    }

    [Theory]
    [InlineData("N45", "shipmentNumber")]
    [InlineData("N45", "id")]
    [InlineData("N45", "isDeleted")]
    [InlineData("N45", "actualWeight.value")]
    public async Task N45_date_trunc_on_a_non_temporal_path_is_refused(string id, string path)
    {
        _ = id;
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, Refused($$"""{ "path": "{{path}}", "unit": "day" }"""));

        answer.ShouldRefuse("INVALID_AGGREGATE_ARGUMENT", 400, path)["message"]!.GetValue<string>().Should().Be($"'{path}' is not a date under no collection.");
        answer.ErrorCodes[0].Should().Be("INVALID_AGGREGATE_ARGUMENT");
    }

    [Fact]
    public async Task N46_date_trunc_on_a_date_under_a_collection_is_refused_as_group_on_collection()
    {
        // Legacy: items.weightNotes.createDateTime; the lab's date under a collection is billingLines.date.
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, Refused("""{ "path": "billingLines.date", "unit": "day" }"""));

        ShouldRefuseOnly(answer, "GROUP_ON_COLLECTION").Should().Be("'billingLines.date' is not a date under no collection.");
    }

    [Fact]
    public async Task N_typed_a_week_bucket_holds_its_own_earliest_row_and_a_year_bucket_takes_a_temporal_filter_after_the_group()
    {
        var instants = LoadStarts();
        var expected = Truncation.Buckets(instants, "week", Berlin);
        var client = await Transport();

        var weeks = await client.SendAsync(Corpus.Shipment, $$"""
            [ { "group": { "by": [{ "dateTrunc": {{DateTrunc("week", Berlin, "monday")}}, "as": "week" }], "fields": { "total": { "count": true }, "earliest": { "min": "loadStart" } } } },
              { "sort": [{ "week": "asc" }] },
              { "page": { "limit": 50, "includeTotalCount": true } } ]
            """);

        weeks.ShouldHaveTotal(expected.Count);
        weeks.Strings("week").Should().Equal(expected.Select(bucket => bucket.Key));
        Numbers(weeks, "total").Should().Equal(expected.Select(bucket => (decimal?)bucket.Count));
        foreach (var item in weeks.Items)
            Truncation.Truncate(DateTime.Parse(item!["earliest"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal), "week", Berlin).Should().Be(item["week"]!.GetValue<string>());

        const string From = "2026-12-31T23:00:00Z";
        var years = Truncation.Buckets(instants, "year", Berlin).Where(bucket => string.CompareOrdinal(bucket.Key, From) >= 0).ToList();
        years.Should().ContainSingle();

        var late = await client.SendAsync(Corpus.Shipment, $$"""
            [ { "group": { "by": [{ "dateTrunc": {{DateTrunc("year", Berlin, null)}}, "as": "year" }], "fields": { "total": { "count": true } } } },
              { "match": { "year": { "gte": "{{From}}" } } },
              { "sort": [{ "year": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        late.ShouldHaveTotal(1);
        late.Strings("year").Should().Equal(years[0].Key);
        Numbers(late, "total").Should().Equal((decimal?)years[0].Count);
    }
}
