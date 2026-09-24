using System.Globalization;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// Calendar arithmetic for <c>dateTrunc</c> expectations, written independently of the engine:
/// local civil parts in a zone, the instant a local time names, bucket boundaries and ISO weeks.
/// A bucket key is spelled as the engine spells it, an ISO instant without milliseconds
/// (<c>2026-12-27T23:00:00Z</c>).
/// </summary>
public static class Temporal
{
    /// <summary>The zone the corpus's temporal rows are designed around.</summary>
    public const string Berlin = "Europe/Berlin";

    private static readonly string[] WeekDays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>The local civil time of a UTC instant in a zone (IANA id).</summary>
    public static DateTime Local(DateTime utc, string timeZone = "UTC") =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(timeZone));

    /// <summary>The UTC instant a local civil time names in a zone; the earlier one where a local time occurs twice.</summary>
    public static DateTime ToUtc(DateTime local, string timeZone = "UTC")
    {
        var zone = Zone(timeZone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // A local time the spring transition skips: the database moves it forward by the gap.
            local = local.AddHours(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);

            return DateTime.SpecifyKind(local - offsets.Max(), DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(local - zone.GetUtcOffset(local), DateTimeKind.Utc);
    }

    /// <summary>
    /// The UTC instant of the local bucket boundary a <c>dateTrunc</c> returns, as the ISO string a
    /// group key carries. <paramref name="weekStart"/> defaults to Monday.
    /// <para>
    /// Sub-day units subtract the wall-clock remainder from the instant instead of rebuilding the
    /// boundary from the wall clock: the two passes through the repeated autumn hour read the same
    /// wall clock, and rebuilding it named one instant for both, merging buckets the database
    /// keeps apart. The subtraction stays on the side of the transition the instant is on.
    /// </para>
    /// </summary>
    public static string DateTruncUtc(DateTime utc, string unit, string timeZone = "UTC", string weekStart = "monday")
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = Local(utc, timeZone);
        var intoSecond = TimeSpan.FromTicks(local.Ticks % TimeSpan.TicksPerSecond);

        TimeSpan? remainder = unit switch
        {
            "hour" => new TimeSpan(0, local.Minute, local.Second) + intoSecond,
            "minute" => TimeSpan.FromSeconds(local.Second) + intoSecond,
            "second" => intoSecond,
            _ => null,
        };

        if (remainder is { } sinceBoundary)
            return Iso(utc - sinceBoundary);

        DateTime start = unit switch
        {
            "year" => new DateTime(local.Year, 1, 1),
            "quarter" => new DateTime(local.Year, ((local.Month - 1) / 3 * 3) + 1, 1),
            "month" => new DateTime(local.Year, local.Month, 1),
            "week" => WeekStart(local, weekStart),
            "day" => local.Date,
            _ => throw new ArgumentException($"'{unit}' is not a dateTrunc unit.", nameof(unit)),
        };

        return Iso(ToUtc(start, timeZone));
    }

    /// <summary>The ISO week and ISO week-year of an instant in a zone.</summary>
    public static (int Year, int Week) IsoWeek(DateTime utc, string timeZone = "UTC")
    {
        var local = Local(utc, timeZone);

        return (ISOWeek.GetYear(local), ISOWeek.GetWeekOfYear(local));
    }

    /// <summary>An instant as the engine spells a bucket key: ISO, UTC, no milliseconds.</summary>
    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime WeekStart(DateTime local, string weekStart)
    {
        var target = Array.IndexOf(WeekDays, weekStart.ToLowerInvariant());

        if (target < 0)
            throw new ArgumentException($"'{weekStart}' is not a day name.", nameof(weekStart));

        var back = ((int)local.DayOfWeek - target + 7) % 7;

        return local.Date.AddDays(-back);
    }

    private static TimeZoneInfo Zone(string timeZone) =>
        timeZone is "UTC" or "Etc/UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone);
}
