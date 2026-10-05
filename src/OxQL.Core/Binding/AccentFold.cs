using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using OxQL.Core.Models;

namespace OxQL.Core.Binding;

/// <summary>
/// The pattern of a text that folds accents the way the request's collation does (DG2 M10):
/// <c>contains</c> and <c>endsWith</c> run as patterns, and a pattern knows no collation, so each
/// letter of the operand becomes the class of the characters that share its primary weight
/// (<c>u</c> → <c>[uUùÙúÚûÛüÜ…]</c>) and <c>ss</c> and <c>ß</c> stand for each other. So
/// <c>muller</c> finds <c>Müller</c>, as <c>eq</c> and <c>startsWith</c> do under the collation.
/// <para>
/// The classes are built once per collation from the platform's collation data
/// (<see cref="CompareInfo"/>) over the Latin-1 Supplement and Latin Extended-A letters. A host that
/// runs without that data (invariant globalization) falls back to the Unicode decomposition of each
/// letter, which gives the same classes for these blocks except the letters with a stroke
/// (<c>ø</c>, <c>đ</c>, <c>ł</c>), which then stay themselves. Accents fold at collation strength 1
/// only: from strength 2 on the collation itself tells them apart, and the pattern does too.
/// </para>
/// </summary>
public static class AccentFold
{
    /// <summary>The first and the last character a class may hold: the Latin-1 Supplement and Latin Extended-A blocks.</summary>
    private const char First = 'À', Last = 'ſ';

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<char, string>> Tables = new(StringComparer.Ordinal);

    /// <summary>Whether the collation folds accents: its strength is primary.</summary>
    public static bool Applies(CollationOptions collation) => collation.Strength <= 1;

    /// <summary>
    /// The pattern that matches <paramref name="literal"/> with accents folded under
    /// <paramref name="collation"/>: every character escaped, a letter as the class of its
    /// equivalents. Case is folded by the pattern's <c>i</c> flag for ASCII; the classes hold both
    /// cases of every other letter, so they do not depend on it. At a strength that tells accents
    /// apart the pattern is the escaped literal.
    /// </summary>
    public static string Pattern(string literal, CollationOptions collation)
    {
        ArgumentNullException.ThrowIfNull(literal);
        ArgumentNullException.ThrowIfNull(collation);

        if (!Applies(collation))
            return RegexGuard.Escape(literal);

        var classes = Tables.GetOrAdd(collation.Locale ?? "", Build);
        var pattern = new StringBuilder(literal.Length * 4);

        for (var index = 0; index < literal.Length; index++)
        {
            var character = literal[index];

            // ss and ß are one primary weight in either spelling.
            if (character is 's' or 'S' && index + 1 < literal.Length && literal[index + 1] is 's' or 'S')
            {
                pattern.Append("(?:ss|ß)");
                index++;
                continue;
            }

            if (character == 'ß')
            {
                pattern.Append("(?:ss|ß)");
                continue;
            }

            var letter = BaseOf(character, classes);

            if (letter is { } folded && classes.TryGetValue(folded, out var equivalents))
                pattern.Append('[').Append(folded).Append(equivalents).Append(']');
            else
                pattern.Append(RegexGuard.Escape(character.ToString()));
        }

        return pattern.ToString();
    }

    /// <summary>The ASCII letter (lower case) a character folds to, when it is one that has equivalents; null for any other character.</summary>
    private static char? BaseOf(char character, IReadOnlyDictionary<char, string> classes)
    {
        var lower = char.ToLowerInvariant(character);

        if (lower is >= 'a' and <= 'z')
            return classes.ContainsKey(lower) ? lower : null;

        foreach (var (letter, equivalents) in classes)
            if (equivalents.Contains(character, StringComparison.Ordinal))
                return letter;

        return null;
    }

    /// <summary>
    /// The classes of a locale: per ASCII letter (lower case) the letters of the two blocks that
    /// share its primary weight, both cases, in code point order. A letter without any has no entry.
    /// </summary>
    private static IReadOnlyDictionary<char, string> Build(string locale)
    {
        var compare = Collation(locale);
        var classes = new SortedDictionary<char, StringBuilder>();

        for (var character = First; character <= Last; character++)
        {
            if (!char.IsLetter(character) || character == 'ß')
                continue;

            if ((compare is not null ? ByCollation(character, compare) : ByDecomposition(character)) is not { } letter)
                continue;

            if (!classes.TryGetValue(letter, out var members))
                classes[letter] = members = new StringBuilder();

            members.Append(character);
        }

        return classes.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
    }

    /// <summary>The collation data of a locale, or null when the host has none (invariant globalization): then <c>a</c> and <c>ä</c> do not compare equal under any option.</summary>
    private static CompareInfo? Collation(string locale)
    {
        try
        {
            var compare = CultureInfo.GetCultureInfo(locale.Length == 0 ? "de" : locale).CompareInfo;

            return compare.Compare("a", "ä", CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase) == 0 ? compare : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static char? ByCollation(char character, CompareInfo compare)
    {
        var text = character.ToString();

        for (var letter = 'a'; letter <= 'z'; letter++)
            if (compare.Compare(text, letter.ToString(), CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase) == 0)
                return letter;

        return null;
    }

    private static char? ByDecomposition(char character)
    {
        var decomposed = character.ToString().Normalize(NormalizationForm.FormD);
        var letter = char.ToLowerInvariant(decomposed[0]);

        return decomposed.Length > 1 && letter is >= 'a' and <= 'z' && decomposed.Skip(1).All(mark => CharUnicodeInfo.GetUnicodeCategory(mark) == UnicodeCategory.NonSpacingMark)
            ? letter
            : null;
    }
}
