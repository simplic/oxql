using System.Text.RegularExpressions;

namespace OxQL.Core.Binding;

/// <summary>
/// The one rule for a name a caller chooses — the <c>as</c> of a lookup, a resolve or an unwind,
/// an unwind's <c>includeIndex</c>, a group key and an aggregate. An alias becomes a field name
/// of the row in storage, so it is a plain identifier and never a name storage or the compiler
/// owns.
/// </summary>
public static class Aliases
{
    /// <summary>The storage name of every row's key; an alias that took it would replace what paging orders and identifies rows by.</summary>
    public const string KeyStorage = "_id";

    /// <summary>The prefix of every field the compiler adds to a row for its own use and removes again.</summary>
    public const string ReservedPrefix = "__";

    /// <summary>The suffix of the temporary field a local resolve joins into, next to its alias.</summary>
    public const string ReservedSuffix = "__arr";

    // \z rather than $: $ also matches before a trailing newline.
    private static readonly Regex Identifier = new(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Why <paramref name="alias"/> cannot be used, or null when it can.</summary>
    public static string? Problem(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || !Identifier.IsMatch(alias))
            return $"'{alias}' is not a plain identifier.";

        if (alias == KeyStorage)
            return $"'{alias}' is the name rows are keyed by in storage; choose another.";

        if (alias.StartsWith(ReservedPrefix, StringComparison.Ordinal) || alias.EndsWith(ReservedSuffix, StringComparison.Ordinal))
            return $"'{alias}' is reserved: a name starting with '{ReservedPrefix}' or ending in '{ReservedSuffix}' belongs to the engine.";

        return null;
    }
}
