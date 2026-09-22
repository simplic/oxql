using System.Text;

namespace OxQL.AspNetCore;

/// <summary>
/// What a caller wrote, made fit for a log line. An entity name, a path or a correlation header
/// is the caller's text: on a text sink a line break in it starts a line the service never
/// wrote, and nothing bounds its length below the body cap. Every such value passes through
/// here on its way to a log call.
/// </summary>
internal static class LogText
{
    /// <summary>How much of a value a log line keeps.</summary>
    public const int MaxLength = 200;

    /// <summary>The value with every control and line-separator character replaced by a space, cut at <see cref="MaxLength"/>.</summary>
    public static string? Of(string? value)
    {
        if (value is null)
            return null;

        var length = Math.Min(value.Length, MaxLength);
        var clean = value.Length <= MaxLength;

        for (var index = 0; index < length && clean; index++)
            clean = !Breaks(value[index]);

        if (clean)
            return value;

        var text = new StringBuilder(length + 1);

        for (var index = 0; index < length; index++)
            text.Append(Breaks(value[index]) ? ' ' : value[index]);

        if (value.Length > MaxLength)
            text.Append('\u2026');

        return text.ToString();
    }

    private static bool Breaks(char character) => char.IsControl(character) || character is '\u2028' or '\u2029';
}
