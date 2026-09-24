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
    public static BsonDocument Build(IReadOnlyList<KeysetField> fields, string keyStorage, BsonValue keyValue)
    {
        var legs = new BsonArray();
        var all = new List<KeysetField>(fields) { new(keyStorage, true, keyValue) };

        // The most specific leg first: the key's tie-break, then each field from the last to the first.
        for (var index = all.Count - 1; index >= 0; index--)
        {
            var leg = new BsonDocument();

            for (var previous = 0; previous < index; previous++)
                leg[all[previous].Storage] = IsNull(all[previous].Value) ? BsonNull.Value : all[previous].Value;

            var field = all[index];
            var isKey = index == all.Count - 1;

            if (isKey)
            {
                leg[field.Storage] = new BsonDocument("$gt", field.Value);
                legs.Add(leg);
                continue;
            }

            if (field.Ascending)
            {
                // After a null in ascending order everything present and non-null follows.
                leg[field.Storage] = IsNull(field.Value)
                    ? new BsonDocument("$ne", BsonNull.Value)
                    : new BsonDocument("$gt", field.Value);

                legs.Add(leg);
                continue;
            }

            // Descending: nulls come last, so after a non-null value both the smaller values
            // and the null bracket follow; after a null nothing further on this field.
            if (IsNull(field.Value))
                continue;

            legs.Add(new BsonDocument(leg) { [field.Storage] = new BsonDocument("$lt", field.Value) });
            legs.Add(new BsonDocument(leg) { [field.Storage] = BsonNull.Value });
        }

        return legs.Count == 1 ? legs[0].AsBsonDocument : new BsonDocument("$or", legs);
    }

    private static bool IsNull(BsonValue value) => value is null || value.IsBsonNull || value.IsBsonUndefined;
}
