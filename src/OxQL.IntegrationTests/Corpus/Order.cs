using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>How strings compare: under the default collation contract 2 applies, or byte for byte.</summary>
public enum StringOrder
{
    /// <summary>
    /// The engine's default under contract 2: the owner's collation, <c>{ locale: "de", strength: 1 }</c>,
    /// which ignores case and accents and orders by ICU primary weights.
    /// </summary>
    Collated,

    /// <summary>Code-point order, which is UTF-8 byte order: what an exact (<c>caseSensitive</c>) comparison sees.</summary>
    Binary,
}

/// <summary>
/// The order and the equality the database applies, written out by hand so no expectation is ever
/// read off the engine: the canonical BSON type order, numbers compared exactly across Int32,
/// Int64, Double and Decimal128, strings by collation or by code point, and the array rule of a
/// sort (the smallest element ascending, the largest descending).
/// </summary>
public static class Order
{
    private static readonly CompareInfo German = CultureInfo.GetCultureInfo("de-DE").CompareInfo;

    private const CompareOptions Primary = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    /// <summary>
    /// The equality half of the default collation: canonical decomposition, every combining mark
    /// dropped, then lower-cased. At primary strength <c>müller</c>, <c>MÜLLER</c>, <c>Müller</c> and
    /// <c>Muller</c> are one value, and so are <c>istanbul</c> and <c>İSTANBUL</c> (İ is I plus a
    /// combining dot, which carries no primary weight). CJK, emoji, digits and punctuation are left
    /// as they are.
    /// </summary>
    public static string FoldCi(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var kept = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);

            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                kept.Append(character);
        }

        return kept.ToString().ToLowerInvariant();
    }

    /// <summary>Case folding without the accent step: what a case-insensitive regular expression folds.</summary>
    public static string FoldCaseOnly(string text) => text.ToLowerInvariant();

    /// <summary>Whether two strings are equal under the default collation.</summary>
    public static bool EqualsCi(string a, string b) => FoldCi(a) == FoldCi(b);

    /// <summary>The order half of the default collation: ICU primary strength for German.</summary>
    public static int CompareCollated(string a, string b) => Math.Sign(German.Compare(a, b, Primary));

    /// <summary>Code-point order, which is UTF-8 byte order; <see cref="string.CompareOrdinal(string, string)"/> is UTF-16 order and differs above the BMP.</summary>
    public static int CompareUtf8(string a, string b)
    {
        var left = a.EnumerateRunes().GetEnumerator();
        var right = b.EnumerateRunes().GetEnumerator();

        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();

            if (!hasLeft || !hasRight)
                return hasLeft == hasRight ? 0 : hasLeft ? 1 : -1;

            var difference = left.Current.Value - right.Current.Value;

            if (difference != 0)
                return Math.Sign(difference);
        }
    }

    /// <summary>Compares two strings the way <paramref name="order"/> says.</summary>
    public static int CompareStrings(string a, string b, StringOrder order) =>
        order == StringOrder.Collated ? CompareCollated(a, b) : CompareUtf8(a, b);

    private static readonly Regex DecimalLiteral = new(@"^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$", RegexOptions.Compiled);

    /// <summary>Compares two decimal literals exactly, however many digits they carry.</summary>
    public static int CompareDecimal(string a, string b)
    {
        var left = Parse(a);
        var right = Parse(b);

        if (left.Sign != right.Sign)
            return left.Sign < right.Sign ? -1 : 1;

        if (left.Sign == 0)
            return 0;

        var leftScale = left.Digits.Length + left.Exponent;
        var rightScale = right.Digits.Length + right.Exponent;
        int magnitude;

        if (leftScale != rightScale)
        {
            magnitude = leftScale < rightScale ? -1 : 1;
        }
        else
        {
            var width = Math.Max(left.Digits.Length, right.Digits.Length);
            magnitude = Math.Sign(string.CompareOrdinal(left.Digits.PadRight(width, '0'), right.Digits.PadRight(width, '0')));
        }

        return magnitude * left.Sign;

        static (int Sign, string Digits, int Exponent) Parse(string text)
        {
            var match = DecimalLiteral.Match(text.Trim());

            if (!match.Success)
                throw new FormatException($"'{text}' is not a decimal literal.");

            var digits = (match.Groups[2].Value + match.Groups[3].Value).TrimStart('0');
            var exponent = -match.Groups[3].Value.Length + (match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0);

            return digits.Length == 0 ? (0, "", 0) : (match.Groups[1].Value == "-" ? -1 : 1, digits.TrimEnd('0'), exponent + (digits.Length - digits.TrimEnd('0').Length));
        }
    }

    /// <summary>The canonical text of a numeric BSON value, exact for every numeric type.</summary>
    public static string NumberText(BsonValue value) => value switch
    {
        BsonInt32 i => i.Value.ToString(CultureInfo.InvariantCulture),
        BsonInt64 l => l.Value.ToString(CultureInfo.InvariantCulture),
        BsonDouble d => d.Value.ToString("R", CultureInfo.InvariantCulture),
        BsonDecimal128 m => m.Value.ToString(),
        _ => throw new ArgumentException($"{value.BsonType} is not a number.", nameof(value)),
    };

    /// <summary>
    /// The canonical BSON rank of a value, the first key of every comparison: missing and null,
    /// numbers, strings, documents, arrays, binary, object ids, booleans, dates. An empty array
    /// sorts below null and missing, as the database's sort does.
    /// </summary>
    public static int Rank(BsonValue? value) => value switch
    {
        BsonArray { Count: 0 } => 0,
        null or BsonNull or BsonUndefined => 2,
        BsonMinKey => 1,
        BsonInt32 or BsonInt64 or BsonDouble or BsonDecimal128 => 3,
        BsonString or BsonSymbol => 4,
        BsonDocument => 5,
        BsonArray => 6,
        BsonBinaryData => 7,
        BsonObjectId => 8,
        BsonBoolean => 9,
        BsonDateTime => 10,
        BsonTimestamp => 11,
        BsonRegularExpression => 12,
        BsonMaxKey => 13,
        _ => 14,
    };

    /// <summary>
    /// Two stored values in the database's order. A non-empty array stands in by its smallest
    /// element ascending and its largest descending, which is how a sort key over an array member
    /// is chosen; <paramref name="descending"/> says which.
    /// </summary>
    public static int Compare(BsonValue? a, BsonValue? b, StringOrder strings = StringOrder.Collated, bool descending = false)
    {
        a = SortKey(a, strings, descending);
        b = SortKey(b, strings, descending);

        var rankA = Rank(a);
        var rankB = Rank(b);

        if (rankA != rankB)
            return rankA < rankB ? -1 : 1;

        return (a, b) switch
        {
            (null or BsonNull or BsonUndefined, _) or (_, null or BsonNull or BsonUndefined) => 0,
            _ when rankA == 3 => CompareDecimal(NumberText(a!), NumberText(b!)),
            (BsonString sa, BsonString sb) => CompareStrings(sa.Value, sb.Value, strings),
            (BsonBinaryData ba, BsonBinaryData bb) => CompareBinary(ba, bb),
            (BsonBoolean xa, BsonBoolean xb) => xa.Value.CompareTo(xb.Value),
            (BsonDateTime da, BsonDateTime db) => da.MillisecondsSinceEpoch.CompareTo(db.MillisecondsSinceEpoch),
            (BsonDocument oa, BsonDocument ob) => CompareDocuments(oa, ob, strings),
            (BsonObjectId ia, BsonObjectId ib) => ia.Value.CompareTo(ib.Value),
            _ => 0,
        };
    }

    /// <summary>Whether two stored values are equal under <paramref name="strings"/>: what an <c>eq</c> matches.</summary>
    public static bool Same(BsonValue? a, BsonValue? b, StringOrder strings = StringOrder.Collated) =>
        a is BsonString sa && b is BsonString sb
            ? strings == StringOrder.Collated ? EqualsCi(sa.Value, sb.Value) : sa.Value == sb.Value
            : Rank(a) == Rank(b) && Compare(a, b, strings) == 0;

    private static BsonValue? SortKey(BsonValue? value, StringOrder strings, bool descending)
    {
        if (value is not BsonArray { Count: > 0 } array)
            return value;

        var ordered = array.OrderBy(element => element, Comparer<BsonValue>.Create((x, y) => Compare(x, y, strings))).ToList();

        return descending ? ordered[^1] : ordered[0];
    }

    private static int CompareBinary(BsonBinaryData a, BsonBinaryData b)
    {
        if (a.Bytes.Length != b.Bytes.Length)
            return a.Bytes.Length < b.Bytes.Length ? -1 : 1;

        if (a.SubType != b.SubType)
            return ((int)a.SubType).CompareTo((int)b.SubType);

        return Math.Sign(a.Bytes.AsSpan().SequenceCompareTo(b.Bytes));
    }

    private static int CompareDocuments(BsonDocument a, BsonDocument b, StringOrder strings)
    {
        for (var index = 0; index < Math.Min(a.ElementCount, b.ElementCount); index++)
        {
            var left = a.GetElement(index);
            var right = b.GetElement(index);
            var byRank = Rank(left.Value).CompareTo(Rank(right.Value));

            if (byRank != 0)
                return byRank;

            var byName = CompareUtf8(left.Name, right.Name);

            if (byName != 0)
                return byName;

            var byValue = Compare(left.Value, right.Value, strings);

            if (byValue != 0)
                return byValue;
        }

        return a.ElementCount.CompareTo(b.ElementCount);
    }
}
