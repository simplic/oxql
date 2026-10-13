using MongoDB.Bson;

namespace OxQL.Core.Cursor;

/// <summary>One sort field as the keyset predicate sees it: storage path, direction, the last row's value.</summary>
public sealed record KeysetField(string Storage, bool Ascending, BsonValue Value)
{
    /// <summary>
    /// Whether the rows may hold values of more than one stored type here: a decimal still stored as
    /// text beside the migrated rows, a value of an addon bag, a member written as another type than
    /// its model declares. Such a leg is compared across the
    /// types (<see cref="KeysetPredicate.After(IReadOnlyList{KeysetField}, bool, bool)"/>); any other
    /// within the type its member is stored as.
    /// </summary>
    public bool SeveralTypes { get; init; }

    /// <summary>
    /// Whether every row holds a value here: an entity's key. With <c>everyType</c> such a leg has no
    /// alternative for the rows that hold nothing, so every alternative of the predicate is a range
    /// of the index of the sort.
    /// </summary>
    public bool NeverNull { get; init; }
}

/// <summary>
/// The null-aware keyset predicate, in forms an index on the sort fields serves: Mongo's range
/// operators never match null or missing, so each leg is built from the last value's bracket.
/// Assembled as an <c>$or</c> over the legs of every prefix, the key last.
/// <para>
/// A range operator holds for no row against a NaN and stays inside the type of its operand, while
/// a sort orders every value: NaN before every number, and by type first (null, numbers, text,
/// objects, binary, object ids, booleans, dates …). With <c>everyType</c> a leg whose position is a
/// number therefore also admits the rows on the far side of a NaN, and a
/// leg of several stored types (<see cref="KeysetField.SeveralTypes"/>) the rows whose value is of
/// a type that orders beyond the position's: the rows after a position are the rows the sort puts
/// after it.
/// </para>
/// </summary>
public static class KeysetPredicate
{
    /// <summary>
    /// Builds the predicate for "rows after the last one" over <paramref name="fields"/> in
    /// order, with the key (<paramref name="keyStorage"/>, always ascending, never null) as the
    /// final tie-breaker.
    /// </summary>
    public static BsonDocument Build(IReadOnlyList<KeysetField> fields, string keyStorage, BsonValue keyValue) =>
        After([.. fields, new KeysetField(keyStorage, true, keyValue)]);

    /// <summary>
    /// The predicate for "rows after the last one" over the legs of a total order, the last leg its
    /// final tie-breaker: an entity's key after the sort fields, the position of each unwind after the
    /// key (a row a <c>preserveNull</c> unwind kept holds a null there), or the last key of a group.
    /// Every leg is null-aware. With <paramref name="inclusive"/> the last row itself passes too
    /// ("from the last one on"): the bound a parent passes while rows of it are still to come.
    /// </summary>
    public static BsonDocument After(IReadOnlyList<KeysetField> legs, bool inclusive = false) => After(legs, inclusive, everyType: false);

    /// <summary>
    /// <see cref="After(IReadOnlyList{KeysetField}, bool)"/>; with <paramref name="everyType"/> each leg
    /// follows the sort's order across a NaN, and a leg of several stored types
    /// (<see cref="KeysetField.SeveralTypes"/>) across them.
    /// </summary>
    public static BsonDocument After(IReadOnlyList<KeysetField> legs, bool inclusive, bool everyType)
    {
        ArgumentNullException.ThrowIfNull(legs);

        var alternatives = new BsonArray();

        // The most specific leg first: the last tie-break, then each field from the last to the first.
        for (var index = legs.Count - 1; index >= 0; index--)
        {
            var leg = new BsonDocument();

            for (var previous = 0; previous < index; previous++)
                leg[legs[previous].Storage] = IsNull(legs[previous].Value) ? BsonNull.Value : legs[previous].Value;

            var field = legs[index];
            var last = index == legs.Count - 1;
            var types = everyType && field.SeveralTypes;

            if (field.Ascending)
            {
                // After a null in ascending order everything present and non-null follows; from a null on, everything does.
                if (IsNull(field.Value))
                {
                    if (!(last && inclusive))
                        leg[field.Storage] = new BsonDocument("$ne", BsonNull.Value);

                    alternatives.Add(leg);
                    continue;
                }

                foreach (var beyond in Beyond(field.Value, ascending: true, last && inclusive, everyType, types))
                    alternatives.Add(new BsonDocument(leg) { [field.Storage] = beyond });

                continue;
            }

            // Descending: nulls come last, so after a non-null value both the smaller values
            // and the null bracket follow; after a null nothing further on this field.
            if (IsNull(field.Value))
            {
                if (last && inclusive)
                    alternatives.Add(new BsonDocument(leg) { [field.Storage] = BsonNull.Value });

                continue;
            }

            foreach (var beyond in Beyond(field.Value, ascending: false, last && inclusive, everyType, types))
                alternatives.Add(new BsonDocument(leg) { [field.Storage] = beyond });

            if (!(everyType && field.NeverNull))
                alternatives.Add(new BsonDocument(leg) { [field.Storage] = BsonNull.Value });
        }

        return alternatives.Count == 1 ? alternatives[0].AsBsonDocument : new BsonDocument("$or", alternatives);
    }

    /// <summary>
    /// The range every row after the last one lies in on the leading leg, or null where it is no
    /// single bound: ascending from a value, the values from it on (<c>$gte</c>); descending from a
    /// value, everything not above it (the smaller values and the nulls that follow them). It is
    /// implied by <see cref="After(IReadOnlyList{KeysetField}, bool)"/> and stands beside it: the database drops the rows before the
    /// position on one comparison, and an index on the leading leg is bounded by one range.
    /// </summary>
    public static BsonDocument? LeadingRange(IReadOnlyList<KeysetField> legs) => LeadingRange(legs, everyType: false);

    /// <summary>
    /// <see cref="LeadingRange(IReadOnlyList{KeysetField})"/>; with <paramref name="everyType"/> the
    /// range from a NaN on is every value there is, and the range of a leg of several stored types
    /// also holds the rows whose value is of a type that orders after the position's.
    /// </summary>
    public static BsonDocument? LeadingRange(IReadOnlyList<KeysetField> legs, bool everyType)
    {
        ArgumentNullException.ThrowIfNull(legs);

        if (legs.Count < 2 || IsNull(legs[0].Value))
            return null;

        var leading = legs[0];

        // "Not above the value" holds for every other type and for a NaN as it stands: the range of a descending leg needs nothing more.
        if (!leading.Ascending)
            return new BsonDocument(leading.Storage, new BsonDocument("$not", new BsonDocument("$gt", leading.Value)));

        // From a NaN on, ascending, is every value there is: NaN orders before every number.
        if (everyType && IsNaN(leading.Value))
            return new BsonDocument(leading.Storage, new BsonDocument("$ne", BsonNull.Value));

        var from = new BsonDocument(leading.Storage, new BsonDocument("$gte", leading.Value));

        return everyType && leading.SeveralTypes && TypesBeyond(leading.Value, ascending: true) is { Count: > 0 } later
            ? new BsonDocument("$or", new BsonArray { from, new BsonDocument(leading.Storage, new BsonDocument("$type", later)) })
            : from;
    }

    /// <summary>
    /// What a leg's member is asked so that its value lies beyond <paramref name="value"/> in the
    /// direction of the sort (with <paramref name="inclusive"/>: at it or beyond): the range inside
    /// the value's own type; with <paramref name="nan"/> the far side of a NaN where the value is a
    /// number; with <paramref name="types"/> the types that order beyond
    /// the value's. The null bracket that ends a descending order is the caller's.
    /// </summary>
    private static IEnumerable<BsonValue> Beyond(BsonValue value, bool ascending, bool inclusive, bool nan, bool types)
    {
        var range = new BsonDocument((ascending, inclusive) switch { (true, false) => "$gt", (true, true) => "$gte", (false, false) => "$lt", _ => "$lte" }, value);

        if (!nan || !value.IsNumeric)
        {
            yield return range;
        }
        else if (IsNaN(value))
        {
            // No range holds against a NaN. Ascending, every number that is one follows it; descending, nothing of its type does.
            if (ascending)
                yield return inclusive ? new BsonDocument("$type", new BsonArray(Numbers)) : new BsonDocument("$gte", double.NegativeInfinity);
            else if (inclusive)
                yield return value;
        }
        else
        {
            yield return range;

            // Below every number stands the NaN, which no range reaches.
            if (!ascending)
                yield return new BsonDouble(double.NaN);
        }

        if (types && TypesBeyond(value, ascending) is { Count: > 0 } beyond)
            yield return new BsonDocument("$type", beyond);
    }

    private static readonly int[] Numbers = [1, 16, 18, 19];

    /// <summary>
    /// The stored types in the order a sort puts them in, each bracket with its type numbers: numbers
    /// of every width, text and symbols, objects, binary, object ids, booleans, dates,
    /// timestamps, patterns, then the types no member is written as. The null bracket stands before
    /// them all and is asked in its own words. The array is not among them: a sort orders an array
    /// by an element of it and not as a type, and asking for the type is the one question of these an
    /// index answers with every key it has.
    /// </summary>
    private static readonly int[][] Brackets = [Numbers, [2, 14], [3], [5], [7], [8], [9], [17], [11], [12], [13], [15], [127]];

    /// <summary>The type numbers of the brackets that order beyond a value's own, in the direction of the sort; empty where there is none or the value's type is in no bracket.</summary>
    private static BsonArray TypesBeyond(BsonValue value, bool ascending)
    {
        var type = (int)value.BsonType;
        var own = Array.FindIndex(Brackets, bracket => Array.IndexOf(bracket, type) >= 0);

        if (own < 0)
            return [];

        return new BsonArray((ascending ? Brackets.Skip(own + 1) : Brackets.Take(own)).SelectMany(bracket => bracket));
    }

    private static bool IsNaN(BsonValue value) => value switch
    {
        BsonDouble number => double.IsNaN(number.Value),
        BsonDecimal128 number => Decimal128.IsNaN(number.Value),
        _ => false,
    };

    private static bool IsNull(BsonValue value) => value is null || value.IsBsonNull || value.IsBsonUndefined;
}
