using System.Globalization;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// An independent <c>dateTrunc</c> oracle, written after the legacy battery's JavaScript
/// derivation and sharing no code with the engine or with the corpus's own calendar helper: the
/// wall clock in the zone, truncated, converted back to the UTC instant of that local boundary.
/// <para>
/// Sub-day units subtract the wall-clock remainder from the instant rather than rebuilding the
/// boundary from the wall clock. That keeps a truncation inside the repeated autumn hour on the
/// side of the transition it started on: rebuilding 02:00 on 2026-10-25 from the wall clock
/// resolves both passes to one instant and merges two rows the database keeps apart.
/// </para>
/// </summary>
internal static class Truncation
{
    public static readonly string[] Units = ["year", "quarter", "month", "week", "day", "hour", "minute", "second"];

    public static readonly string[] WeekDays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private static TimeZoneInfo Zone(string timeZone) => timeZone == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone);

    /// <summary>The wall clock in <paramref name="timeZone"/> at a UTC instant.</summary>
    public static DateTime WallOf(DateTime utc, string timeZone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(timeZone));

    /// <summary>The zone's offset at a UTC instant.</summary>
    private static TimeSpan OffsetAt(DateTime utc, string timeZone) => Zone(timeZone).GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    /// <summary>The UTC instant at which the wall clock reads <paramref name="wall"/>: two refinements of a naive guess, as the JavaScript oracle did.</summary>
    public static DateTime InstantOf(DateTime wall, string timeZone)
    {
        var naive = DateTime.SpecifyKind(wall, DateTimeKind.Utc);
        var guess = naive - OffsetAt(naive, timeZone);

        return naive - OffsetAt(guess, timeZone);
    }

    /// <summary>The day of the week at a UTC instant in a zone.</summary>
    public static string WeekDayOf(DateTime utc, string timeZone) => WeekDays[(int)WallOf(utc, timeZone).DayOfWeek];

    /// <summary>An instant as the engine spells a bucket key.</summary>
    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The bucket key of <paramref name="utc"/> for <paramref name="unit"/> in <paramref name="timeZone"/>.</summary>
    public static string Truncate(DateTime utc, string unit, string timeZone = "UTC", string weekStart = "monday")
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var wall = WallOf(utc, timeZone);
        var whole = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));

        switch (unit)
        {
            case "second":
                return Iso(whole);
            case "minute":
                return Iso(whole.AddSeconds(-wall.Second));
            case "hour":
                return Iso(whole.AddSeconds(-((wall.Minute * 60) + wall.Second)));
        }

        var floor = unit switch
        {
            "year" => new DateTime(wall.Year, 1, 1),
            "quarter" => new DateTime(wall.Year, ((wall.Month - 1) / 3 * 3) + 1, 1),
            "month" => new DateTime(wall.Year, wall.Month, 1),
            "week" or "day" => wall.Date,
            _ => throw new ArgumentException($"'{unit}' is not a unit.", nameof(unit)),
        };
        var at = InstantOf(floor, timeZone);

        if (unit == "week")
        {
            var start = Array.IndexOf(WeekDays, weekStart.ToLowerInvariant());
            var back = ((int)WallOf(at, timeZone).DayOfWeek - start + 7) % 7;

            if (back != 0)
                at = InstantOf(WallOf(at.AddDays(-back), timeZone).Date, timeZone);
        }

        return Iso(at);
    }

    /// <summary>Bucket key to row count for a list of instants, in ascending key order.</summary>
    public static IReadOnlyList<(string Key, int Count)> Buckets(IEnumerable<DateTime> instants, string unit, string timeZone = "UTC", string weekStart = "monday") =>
        instants.GroupBy(instant => Truncate(instant, unit, timeZone, weekStart))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count()))
            .ToList();
}
