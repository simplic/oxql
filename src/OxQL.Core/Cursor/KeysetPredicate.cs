using MongoDB.Bson;

namespace OxQL.Core.Cursor;

/// <summary>One sort field as the keyset predicate sees it: storage path, direction, the last row's value.</summary>
public sealed record KeysetField(string Storage, bool Ascending, BsonValue Value);

/// <summary>
/// The null-aware keyset predicate, in forms an index on the sort fields serves: Mongo's range
/// operators never match null or missing, so each leg is built from the last value's bracket.
/// Assembled as an <c>$or</c> over the legs of every prefix, the key last.
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
    public static BsonDocument After(IReadOnlyList<KeysetField> legs, bool inclusive = false)
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

            if (field.Ascending)
            {
                // After a null in ascending order everything present and non-null follows; from a null on, everything does.
                if (IsNull(field.Value))
                {
                    if (!(last && inclusive))
                        leg[field.Storage] = new BsonDocument("$ne", BsonNull.Value);
                }
                else
                {
                    leg[field.Storage] = new BsonDocument(last && inclusive ? "$gte" : "$gt", field.Value);
                }

                alternatives.Add(leg);
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

            alternatives.Add(new BsonDocument(leg) { [field.Storage] = new BsonDocument(last && inclusive ? "$lte" : "$lt", field.Value) });
            alternatives.Add(new BsonDocument(leg) { [field.Storage] = BsonNull.Value });
        }

        return alternatives.Count == 1 ? alternatives[0].AsBsonDocument : new BsonDocument("$or", alternatives);
    }

    /// <summary>
    /// The range every row after the last one lies in on the leading leg, or null where it is no
    /// single bound: ascending from a value, the values from it on (<c>$gte</c>); descending from a
    /// value, everything not above it (the smaller values and the nulls that follow them). It is
    /// implied by <see cref="After"/> and stands beside it: the database drops the rows before the
    /// position on one comparison, and an index on the leading leg is bounded by one range.
    /// </summary>
    public static BsonDocument? LeadingRange(IReadOnlyList<KeysetField> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);

        if (legs.Count < 2 || IsNull(legs[0].Value))
            return null;

        return legs[0].Ascending
            ? new BsonDocument(legs[0].Storage, new BsonDocument("$gte", legs[0].Value))
            : new BsonDocument(legs[0].Storage, new BsonDocument("$not", new BsonDocument("$gt", legs[0].Value)));
    }

    private static bool IsNull(BsonValue value) => value is null || value.IsBsonNull || value.IsBsonUndefined;
}
