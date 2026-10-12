using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using OxQL.Core.Models;

namespace OxQL.Core.Binding;

/// <summary>
/// The pattern of a text that folds accents the way the request's collation does:
/// <c>contains</c> and <c>endsWith</c> run as patterns, and a pattern knows no collation, so each
/// letter of the operand becomes the class of the characters that share its primary weight
/// (<c>u</c> → <c>[uUùÙúÚûÛüÜ…]</c>), and a letter the collation gives the weight of two or three
/// stands for them and they for it: <c>ß</c> and <c>ss</c>, <c>æ</c> and <c>ae</c>, <c>œ</c> and
/// <c>oe</c>, <c>ĳ</c> and <c>ij</c>, the ligatures (<c>ﬁ</c>, <c>ﬀ</c>, <c>ﬃ</c>, <c>ﬆ</c>, <c>ǆ</c>,
/// <c>ǉ</c>, <c>ǌ</c>). So <c>muller</c> finds <c>Müller</c> and <c>strasse</c> finds <c>Straße</c>,
/// as <c>eq</c> and <c>startsWith</c> do under the collation.
/// <para>
/// The rule is the collation's own, read from the platform's collation data (<see cref="CompareInfo"/>)
/// once per locale: a character of the Latin blocks (Latin-1 Supplement, Latin Extended-A and -B,
/// Latin Extended Additional, the Latin ligatures) belongs to the class of the ASCII letter it
/// compares equal to at primary strength, or stands for the two or three ASCII letters it compares
/// equal to. Nothing is listed by hand, and a test asks the server's collation the same questions,
/// so the pattern finds a spelling exactly when <c>eq</c> calls it equal. A host that runs without
/// that data (invariant globalization) falls back to the Unicode decomposition of each letter with
/// the three pairs no decomposition gives (<c>ß</c>, <c>æ</c>, <c>œ</c>); the letters with a stroke
/// (<c>ø</c>, <c>đ</c>, <c>ł</c>) then stay themselves. Accents fold at collation strength 1 only:
/// from strength 2 on the collation itself tells them apart, and the pattern does too.
/// </para>
/// <para>
/// A sequence is paired from the left: in <c>sss</c> the first two letters stand for <c>ß</c>, the
/// third for itself. A single letter of the operand does not match inside a character that stands
/// for two (<c>s</c> alone does not find the <c>ß</c> of <c>Straße</c>; <c>ss</c> and <c>ß</c> do).
/// </para>
/// </summary>
public static class AccentFold
{
    /// <summary>The blocks a class or an expansion is read from: Latin-1 Supplement to Latin Extended-B, Latin Extended Additional, the Latin ligatures.</summary>
    private static readonly (char First, char Last)[] Blocks = [('À', 'ɏ'), ('Ḁ', 'ỿ'), ('ﬀ', 'ﬆ')];

    /// <summary>What a locale folds: per ASCII letter the characters of its primary weight, and per sequence of two or three ASCII letters the characters that stand for it.</summary>
    private sealed record Table(IReadOnlyDictionary<char, string> Classes, IReadOnlyDictionary<string, string> Sequences, IReadOnlyDictionary<char, string> Expansions)
    {
        /// <summary>Each class as a pattern writes it: its letter, then its characters with every run of three or more neighbours as a range, which keeps a long operand's pattern under the server's limit.</summary>
        public IReadOnlyDictionary<char, string> Written { get; } = Classes.ToDictionary(pair => pair.Key, pair => "[" + pair.Key + Ranges(pair.Value) + "]");
    }

    /// <summary>The characters (in code point order) with every run of three or more consecutive code points written as <c>first-last</c>.</summary>
    private static string Ranges(string characters)
    {
        var written = new StringBuilder(characters.Length);

        for (var start = 0; start < characters.Length;)
        {
            var end = start;

            while (end + 1 < characters.Length && characters[end + 1] == characters[end] + 1)
                end++;

            if (end - start >= 2)
                written.Append(characters[start]).Append('-').Append(characters[end]);
            else
                written.Append(characters, start, end - start + 1);

            start = end + 1;
        }

        return written.ToString();
    }

    private static readonly ConcurrentDictionary<string, Table> Tables = new(StringComparer.Ordinal);

    /// <summary>Whether the collation folds accents: its strength is primary.</summary>
    public static bool Applies(CollationOptions collation) => collation.Strength <= 1;

    /// <summary>
    /// The pattern that matches <paramref name="literal"/> with accents folded under
    /// <paramref name="collation"/>: every character escaped, a letter as the class of its
    /// equivalents, a sequence that one character stands for as either spelling. Case is folded by
    /// the pattern's <c>i</c> flag for ASCII; the classes hold both cases of every other letter, so
    /// they do not depend on it. At a strength that tells accents apart the pattern is the escaped literal.
    /// </summary>
    public static string Pattern(string literal, CollationOptions collation)
    {
        ArgumentNullException.ThrowIfNull(literal);
        ArgumentNullException.ThrowIfNull(collation);

        if (!Applies(collation))
            return RegexGuard.Escape(literal);

        var table = Tables.GetOrAdd(collation.Locale ?? "", Build);
        var pattern = new StringBuilder(literal.Length * 4);
        var index = 0;

        while (index < literal.Length)
        {
            var character = literal[index];

            // A character that stands for two or three letters: those letters, or any character that stands for them.
            if (table.Expansions.TryGetValue(character, out var letters))
            {
                Sequence(pattern, letters, table);
                index++;
                continue;
            }

            // The letters one character stands for, the longest sequence first.
            var taken = 0;

            for (var length = Math.Min(3, literal.Length - index); length >= 2 && taken == 0; length--)
            {
                var written = Folded(literal.AsSpan(index, length), table);

                if (written is not null && table.Sequences.ContainsKey(written))
                {
                    Sequence(pattern, written, table);
                    taken = length;
                }
            }

            if (taken > 0)
            {
                index += taken;
                continue;
            }

            Letter(pattern, character, table);
            index++;
        }

        return pattern.ToString();
    }

    /// <summary>One character: the class of its letter, or itself escaped.</summary>
    private static void Letter(StringBuilder pattern, char character, Table table)
    {
        if (BaseOf(character, table.Classes) is { } folded && table.Written.TryGetValue(folded, out var written))
            pattern.Append(written);
        else
            pattern.Append(RegexGuard.Escape(character.ToString()));
    }

    /// <summary>A sequence of ASCII letters one character stands for: the letters, each as its class, or one of those characters.</summary>
    private static void Sequence(StringBuilder pattern, string letters, Table table)
    {
        pattern.Append("(?:");

        foreach (var letter in letters)
            Letter(pattern, letter, table);

        // Three letters may also be spelled with a character for two of them (ffi: ﬀ and i, f and ﬁ).
        if (letters.Length == 3)
        {
            if (table.Sequences.TryGetValue(letters[..2], out var head))
            {
                pattern.Append("|[").Append(head).Append(']');
                Letter(pattern, letters[2], table);
            }

            if (table.Sequences.TryGetValue(letters[1..], out var tail))
            {
                pattern.Append('|');
                Letter(pattern, letters[0], table);
                pattern.Append('[').Append(tail).Append(']');
            }
        }

        if (table.Sequences.TryGetValue(letters, out var single))
            pattern.Append("|[").Append(single).Append(']');

        pattern.Append(')');
    }

    /// <summary>The ASCII letters (lower case) some characters fold to, or null when one of them is no letter with a class.</summary>
    private static string? Folded(ReadOnlySpan<char> characters, Table table)
    {
        Span<char> letters = stackalloc char[characters.Length];

        for (var index = 0; index < characters.Length; index++)
        {
            var lower = char.ToLowerInvariant(characters[index]);

            if (lower is >= 'a' and <= 'z')
                letters[index] = lower;
            else if (BaseOf(characters[index], table.Classes) is { } folded)
                letters[index] = folded;
            else
                return null;
        }

        return letters.ToString();
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
    /// The table of a locale: per ASCII letter (lower case) the letters of the blocks that share its
    /// primary weight, both cases, in code point order (a letter without any has no entry), and the
    /// characters that share the weight of two or three ASCII letters, by those letters.
    /// </summary>
    private static Table Build(string locale)
    {
        var compare = Collation(locale);
        var classes = new SortedDictionary<char, StringBuilder>();
        var sequences = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);
        var expansions = new Dictionary<char, string>();

        foreach (var (first, last) in Blocks)
            for (var character = first; character <= last; character++)
            {
                if (!char.IsLetter(character))
                    continue;

                if ((compare is not null ? ByCollation(character, compare) : ByDecomposition(character)) is { } letter)
                {
                    if (!classes.TryGetValue(letter, out var members))
                        classes[letter] = members = new StringBuilder();

                    members.Append(character);
                    continue;
                }

                if ((compare is not null ? ExpansionByCollation(character, compare) : ExpansionByDecomposition(character)) is not { } letters)
                    continue;

                if (!sequences.TryGetValue(letters, out var standing))
                    sequences[letters] = standing = new StringBuilder();

                standing.Append(character);
                expansions[character] = letters;
            }

        return new Table(
            classes.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
            sequences.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal),
            expansions);
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

    private const CompareOptions Primary = CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase;

    private static char? ByCollation(char character, CompareInfo compare)
    {
        var text = character.ToString();

        for (var letter = 'a'; letter <= 'z'; letter++)
            if (compare.Compare(text, letter.ToString(), Primary) == 0)
                return letter;

        return null;
    }

    /// <summary>The two or three ASCII letters a character compares equal to at primary strength, or null.</summary>
    private static string? ExpansionByCollation(char character, CompareInfo compare)
    {
        var text = character.ToString();
        Span<char> pair = stackalloc char[2];

        for (var first = 'a'; first <= 'z'; first++)
        {
            // Only a sequence that starts where the character sorts can equal it.
            pair[0] = first;
            pair[1] = 'a';

            if (compare.Compare(text, first.ToString(), Primary) < 0)
                break;

            for (var second = 'a'; second <= 'z'; second++)
            {
                pair[1] = second;

                var letters = pair.ToString();

                if (compare.Compare(text, letters, Primary) == 0)
                    return letters;

                for (var third = 'a'; third <= 'z' && character >= 'ﬀ'; third++)
                    if (compare.Compare(text, letters + third, Primary) == 0)
                        return letters + third;
            }
        }

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

    /// <summary>Without collation data: the pairs no decomposition gives, else the letters of the compatibility decomposition (the ligatures).</summary>
    private static string? ExpansionByDecomposition(char character)
    {
        switch (character)
        {
            case 'ß' or 'ẞ': return "ss";
            case 'æ' or 'Æ': return "ae";
            case 'œ' or 'Œ': return "oe";
        }

        var letters = new string(character.ToString().Normalize(NormalizationForm.FormKD)
            .Where(part => CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());

        return letters.Length is 2 or 3 && letters.All(letter => letter is >= 'a' and <= 'z') ? letters : null;
    }
}
