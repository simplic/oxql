using System.Text;
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

    /// <summary>
    /// The longest pattern the server compiles, in UTF-8 bytes. A longer one is rejected at
    /// execution, whatever <c>RegexMaxLength</c> allows and whether the caller wrote the pattern
    /// or the compiler built it from a text operand.
    /// </summary>
    internal const int MaxPatternBytes = 32_764;

    /// <summary>The two anchors the compiler may put around an escaped literal.</summary>
    private const int AnchorBytes = 2;

    /// <summary>Checks a pattern; null when it passes, else the code and message.</summary>
    public static (string Code, string Message)? Check(string pattern, int maxLength)
    {
        if (pattern.Length > maxLength)
            return (Codes.RegexTooLong, $"The pattern is {pattern.Length} characters; the limit is {maxLength}.");

        if (Encoding.UTF8.GetByteCount(pattern) > MaxPatternBytes)
            return (Codes.RegexTooLong, $"The pattern is longer than the {MaxPatternBytes} bytes the database compiles.");

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

    /// <summary>
    /// Whether a literal still fits the server's pattern limit once it is escaped and anchored:
    /// <c>contains</c>, <c>startsWith</c>, <c>endsWith</c>, a contract 1 <c>ignoreCase</c>
    /// comparison and a <c>caseSensitive</c> comparison inside a collated aggregate compile
    /// their text operand to a pattern.
    /// </summary>
    public static bool LiteralFits(string literal) =>
        Encoding.UTF8.GetByteCount(Escape(literal)) + AnchorBytes <= MaxPatternBytes;

    /// <summary>Escapes a literal for use inside a pattern.</summary>
    public static string Escape(string literal) => Regex.Escape(literal);
}
