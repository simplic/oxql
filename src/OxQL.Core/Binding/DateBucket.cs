namespace OxQL.Core.Binding;

/// <summary>
/// The bucket a <c>dateTrunc</c> puts an instant in, as the half-open range of instants
/// <c>[From, Until)</c>: the computation the database's <c>$dateTrunc</c> makes for a group key, made
/// here so that a condition on a bucket is a range on the stored date, which an index bounds, instead
/// of an expression evaluated for every row.
/// <para>
/// The units from <c>day</c> up are calendar units of the zone: the wall clock of the instant, cut to
/// the unit, read back as an instant. A wall-clock time the zone skips is the instant the clock next
/// shows (the gap moves it forward). One the zone shows twice starts the bucket at the first of the
/// two, although the database names the bucket by the second: the key of such a bucket is an instant
/// inside it, an hour after its start, and its bucket is still this one. The units below
/// <c>day</c> are not: the database counts them in whole units from midnight of 2000-01-01 in the
/// zone, in elapsed time. In a zone whose offset is a whole number of the unit that is the wall clock's
/// boundary; where an offset changed by less than an hour it is not, and this follows the database.
/// </para>
/// <para>
/// The zone's rules are the runtime's (<see cref="TimeZoneInfo"/>), the database's are its own copy of
/// the IANA database. They are the same rules where the runtime reads that database (Linux, macOS); on
/// Windows the runtime reads the system's rules, which agree for the years a zone's current rules
/// cover and may differ before them.
/// </para>
/// </summary>
public static class DateBucket
{
    /// <summary>The wall-clock time the database counts the units below a day from, in the zone.</summary>
    private static readonly DateTime Reference = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// The bucket of <paramref name="utc"/> for <paramref name="unit"/> (year, quarter, month, week,
    /// day, hour, minute, second) in <paramref name="zone"/>, a week starting on
    /// <paramref name="weekStart"/>. Null when the bucket does not lie within the years a date holds.
    /// </summary>
    public static (DateTime From, DateTime Until)? Of(DateTime utc, string unit, TimeZoneInfo zone, DayOfWeek weekStart = DayOfWeek.Monday)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(zone);

        try
        {
            // A stored date holds milliseconds; what lies below them is not part of the instant.
            utc = new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

            long? elapsed = unit switch
            {
                "second" => 1_000,
                "minute" => 60_000,
                "hour" => 3_600_000,
                _ => null,
            };

            if (elapsed is { } milliseconds)
            {
                var reference = Instant(Reference, zone);
                var since = (utc - reference).Ticks / TimeSpan.TicksPerMillisecond;
                var bins = Math.DivRem(since, milliseconds, out var into);

                if (into < 0)
                    bins--;

                var from = reference.AddMilliseconds(bins * milliseconds);

                return (from, from.AddMilliseconds(milliseconds));
            }

            var wall = ReferenceEquals(zone, TimeZoneInfo.Utc) ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            DateTime start, next;

            switch (unit)
            {
                case "year":
                    start = new DateTime(wall.Year, 1, 1);
                    next = start.AddYears(1);
                    break;

                case "quarter":
                    start = new DateTime(wall.Year, ((wall.Month - 1) / 3 * 3) + 1, 1);
                    next = start.AddMonths(3);
                    break;

                case "month":
                    start = new DateTime(wall.Year, wall.Month, 1);
                    next = start.AddMonths(1);
                    break;

                case "week":
                    start = wall.Date.AddDays(-(((int)wall.DayOfWeek - (int)weekStart + 7) % 7));
                    next = start.AddDays(7);
                    break;

                case "day":
                    start = wall.Date;
                    next = start.AddDays(1);
                    break;

                default:
                    return null;
            }

            return (Instant(start, zone), Instant(next, zone));
        }
        catch (ArgumentException)
        {
            // The first or the last year a date holds: the bucket's bounds are not both dates.
            return null;
        }
    }

    /// <summary>
    /// The calendar days whose midnight in <paramref name="zone"/> lies in <paramref name="bucket"/>,
    /// as the half-open range <c>[From, Until)</c> of days. A member that holds a date and no time is
    /// truncated from that midnight, so these are the days the bucket holds; a bucket below a day
    /// holds the one day whose midnight it starts at, or none.
    /// </summary>
    public static (DateOnly From, DateOnly Until) Days((DateTime From, DateTime Until) bucket, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return (FirstDayFrom(bucket.From, zone), FirstDayFrom(bucket.Until, zone));
    }

    /// <summary>
    /// The instant of midnight of <paramref name="day"/> in <paramref name="zone"/>, as the database
    /// reads a date without a time before it truncates it: the second of the two where the zone shows
    /// that midnight twice.
    /// </summary>
    public static DateTime Midnight(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return Instant(day.ToDateTime(TimeOnly.MinValue), zone, second: true);
    }

    /// <summary>The first day whose midnight in the zone is at or after <paramref name="instant"/>.</summary>
    private static DateOnly FirstDayFrom(DateTime instant, TimeZoneInfo zone)
    {
        var day = DateOnly.FromDateTime(ReferenceEquals(zone, TimeZoneInfo.Utc) ? instant : TimeZoneInfo.ConvertTimeFromUtc(instant, zone));

        while (Midnight(day, zone) < instant)
            day = day.AddDays(1);

        while (day > DateOnly.MinValue && Midnight(day.AddDays(-1), zone) >= instant)
            day = day.AddDays(-1);

        return day;
    }

    /// <summary>
    /// The instant at which the wall clock of <paramref name="zone"/> reads <paramref name="wall"/>:
    /// moved forward by the gap when the zone skips that time; when it shows it twice the first, or
    /// with <paramref name="second"/> the second.
    /// </summary>
    private static DateTime Instant(DateTime wall, TimeZoneInfo zone, bool second = false)
    {
        var naive = DateTime.SpecifyKind(wall, DateTimeKind.Utc);

        if (ReferenceEquals(zone, TimeZoneInfo.Utc))
            return naive;

        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);

        // Read with the offset from before the gap, a skipped time is the instant the gap ends at plus what it skipped of it.
        if (zone.IsInvalidTime(wall))
            return naive - zone.GetUtcOffset(naive.AddDays(-1));

        if (zone.IsAmbiguousTime(wall))
            return naive - (second ? zone.GetAmbiguousTimeOffsets(wall).Min() : zone.GetAmbiguousTimeOffsets(wall).Max());

        return naive - zone.GetUtcOffset(wall);
    }
}
