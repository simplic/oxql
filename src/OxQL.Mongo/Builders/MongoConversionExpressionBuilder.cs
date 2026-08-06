using MongoDB.Bson;

namespace OxQL.Mongo.Builders;

/// <summary>
/// The direction of a GUID/UUID conversion, describing how the local and foreign lookup keys are stored.
/// </summary>
public enum GuidConversionDirection
{
    /// <summary>Local key is a string GUID, foreign key is a binary UUID (the local string is coerced to a UUID).</summary>
    StringToUuid,

    /// <summary>Local key is a binary UUID, foreign key is a string GUID (the foreign string is coerced to a UUID).</summary>
    UuidToString
}

/// <summary>
/// Builds reusable MongoDB aggregation expressions that coerce a string GUID into a binary UUID
/// (BSON subtype 4) so that values stored in different representations can be compared.
/// </summary>
/// <remarks>
/// The generated expression uses the <c>$function</c> operator (server-side JavaScript) and is
/// therefore compatible with MongoDB <b>7.0 and later</b> — unlike the native <c>$convert</c>/<c>$toUUID</c>
/// UUID support, which requires MongoDB 8.0+.
/// <para>
/// The coercion is non-throwing: a value that is not a string, or a string that is not a valid
/// GUID, is returned unchanged (so it simply will not match rather than raising an error).
/// </para>
/// <para>
/// Requirements and caveats: server-side JavaScript must be enabled on the server
/// (<c>security.javascriptEnabled</c>, enabled by default but disabled on some hosted tiers), the
/// per-document JavaScript call is slower than a native operator, and only the standard UUID
/// representation (BSON subtype 4) is produced.
/// </para>
/// </remarks>
public static class MongoConversionExpressionBuilder
{
    /// <summary>
    /// JavaScript body: returns the value unchanged unless it is a string that parses as a GUID,
    /// in which case it returns the corresponding binary UUID (subtype 4).
    /// </summary>
    internal const string UuidCoerceBody =
        "function(v){ if (typeof v !== 'string') { return v; } try { return UUID(v); } catch (e) { return v; } }";

    /// <summary>
    /// Builds a <c>$function</c> expression that coerces <paramref name="input"/> from a string GUID
    /// into a binary UUID (subtype 4), leaving non-string / unparseable values unchanged.
    /// </summary>
    /// <param name="input">
    /// The input expression to coerce (for example a field reference like <c>"$$localValue"</c> or <c>"$_id"</c>).
    /// </param>
    /// <returns>A <see cref="BsonDocument"/> representing the <c>$function</c> expression.</returns>
    public static BsonDocument CoerceToUuid(BsonValue input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new BsonDocument("$function", new BsonDocument
        {
            ["body"] = UuidCoerceBody,
            ["args"] = new BsonArray { input },
            ["lang"] = "js"
        });
    }

    /// <summary>
    /// Parses a lookup <c>convert</c> direction token (<c>"stringToUuid"</c> or <c>"uuidToString"</c>).
    /// </summary>
    /// <param name="value">The direction token (case-insensitive).</param>
    /// <returns>The parsed <see cref="GuidConversionDirection"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the token is not recognized.</exception>
    public static GuidConversionDirection ParseDirection(string value) => value?.ToLowerInvariant() switch
    {
        "stringtouuid" => GuidConversionDirection.StringToUuid,
        "uuidtostring" => GuidConversionDirection.UuidToString,
        _ => throw new InvalidOperationException($"Unknown conversion direction: {value}")
    };
}
