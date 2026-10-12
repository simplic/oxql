using System.Globalization;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// What a client computes to ask for the rows of a <c>dateTrunc</c> group: the half-open range
/// <c>[start, end)</c> of the bucket's calendar bounds in the zone, sent as <c>gte</c> and <c>lt</c>
/// on the member. The engine has no condition for a bucket; this is the client's half, written the
/// way a client writes it (the calendar day, week, month, quarter or year the key lies in, and the
/// first instant the zone's clock shows each bound) and sharing no code with the engine or with
/// <see cref="Truncation"/>.
/// <para>
/// The start of a day is the first instant from which the zone's clock shows that day or a later
/// one: the instant after the gap where a zone skips its midnight, the first of the two where it
/// shows a midnight twice.
/// </para>
/// </summary>
internal static class CalendarRange
{
    private static TimeZoneInfo Zone(string timeZone) => timeZone == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone);

    private static DateTime Wall(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>An instant as an operand is written.</summary>
    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>An instant an answer spelled.</summary>
    public static DateTime Instant(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>The first instant from which the clock of <paramref name="timeZone"/> shows <paramref name="day"/> or a later day, to the second.</summary>
    public static DateTime StartOfDay(DateOnly day, string timeZone)
    {
        var zone = Zone(timeZone);
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var naive = DateTime.SpecifyKind(midnight, DateTimeKind.Utc);

        // No zone is further than 14 hours from UTC, and no change of a clock moves it by more than a few hours.
        var (before, from) = (naive.AddHours(-30), naive.AddHours(30));

        while ((from - before).TotalSeconds > 1)
        {
            var middle = before.AddSeconds(Math.Floor((from - before).TotalSeconds / 2));

            if (Wall(middle, zone) >= midnight)
                from = middle;
            else
                before = middle;
        }

        return from;
    }

    /// <summary>The calendar days <c>[first, next)</c> of the bucket of <paramref name="unit"/> (day, week, month, quarter, year) that the day <paramref name="day"/> lies in.</summary>
    public static (DateOnly First, DateOnly Next) Days(DateOnly day, string unit, string weekStart = "monday")
    {
        switch (unit)
        {
            case "day":
                return (day, day.AddDays(1));
            case "week":
                var first = day.AddDays(-(((int)day.DayOfWeek - Array.IndexOf(Truncation.WeekDays, weekStart) + 7) % 7));

                return (first, first.AddDays(7));
            case "month":
                var month = new DateOnly(day.Year, day.Month, 1);

                return (month, month.AddMonths(1));
            case "quarter":
                var quarter = new DateOnly(day.Year, ((day.Month - 1) / 3 * 3) + 1, 1);

                return (quarter, quarter.AddMonths(3));
            case "year":
                return (new DateOnly(day.Year, 1, 1), new DateOnly(day.Year + 1, 1, 1));
            default:
                throw new ArgumentException($"'{unit}' is not a calendar unit; a bucket below a day is no calendar range.", nameof(unit));
        }
    }

    /// <summary>The day the clock of <paramref name="timeZone"/> shows at an instant.</summary>
    public static DateOnly DayOf(DateTime utc, string timeZone) => DateOnly.FromDateTime(Wall(utc, Zone(timeZone)));

    /// <summary>
    /// The range of instants <c>[start, end)</c> of the bucket a group's key names: the calendar bounds
    /// of the bucket the key lies in, each at the start of its day in the zone.
    /// </summary>
    public static (DateTime Start, DateTime End) Of(DateTime key, string unit, string timeZone = "UTC", string weekStart = "monday")
    {
        var (first, next) = Days(DayOf(key, timeZone), unit, weekStart);

        return (StartOfDay(first, timeZone), StartOfDay(next, timeZone));
    }

    /// <summary>The condition a client sends for the range: <c>{ "gte": start, "lt": end }</c>.</summary>
    public static string Condition((DateTime Start, DateTime End) range) => $$"""{ "gte": "{{Iso(range.Start)}}", "lt": "{{Iso(range.End)}}" }""";
}
