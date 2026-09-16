using System.Text.RegularExpressions;

namespace OxQL.Core.Binding;

/// <summary>
/// The static check on a caller's regex: the length cap and the shapes that can blow up.
/// The server's PCRE2 defuses the classic catastrophic patterns and <c>maxTimeMS</c> does not
/// interrupt a regex, so this check and the cap are the whole guard; it is kept simple.
/// </summary>
public static class RegexGuard
{
    /// <summary>A nested quantifier: a quantified group that is itself quantified, <c>(a+)+</c>, <c>(a*)*</c>, <c>(a{2,})+</c>.</summary>
    private static readonly Regex NestedQuantifier = new(@"\((?:[^()\\]|\\.)*[+*}](?:[^()\\]|\\.)*\)[+*{?]", RegexOptions.Compiled);

    /// <summary>A backreference.</summary>
    private static readonly Regex Backreference = new(@"(?<!\\)\\[1-9]|\\k<", RegexOptions.Compiled);

    /// <summary>Checks a pattern; null when it passes, else the code and message.</summary>
    public static (string Code, string Message)? Check(string pattern, int maxLength)
    {
        if (pattern.Length > maxLength)
            return (Codes.RegexTooLong, $"The pattern is {pattern.Length} characters; the limit is {maxLength}.");

        if (Backreference.IsMatch(pattern))
            return (Codes.InvalidRegex, "Backreferences are not allowed.");

        if (NestedQuantifier.IsMatch(pattern))
            return (Codes.InvalidRegex, "A quantifier over a quantified group is not allowed.");

        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException exception)
        {
            return (Codes.InvalidRegex, $"The pattern is not a valid regular expression: {exception.Message}");
        }

        return null;
    }

    /// <summary>Whether a pattern is anchored at its start, so an index can bound it.</summary>
    public static bool IsAnchored(string pattern) => pattern.StartsWith('^') || pattern.StartsWith(@"\A", StringComparison.Ordinal);

    /// <summary>Escapes a literal for use inside a pattern.</summary>
    public static string Escape(string literal) => Regex.Escape(literal);
}
