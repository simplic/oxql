using MongoDB.Bson;
using Xunit;

namespace OxQL.Tests;

/// <summary>Structural equality of BSON values through their shell JSON, so a mismatch shows both forms.</summary>
internal static class BsonAssertions
{
    public static void ShouldBeBson(this BsonValue actual, BsonDocument expected, string because = "") =>
        Assert.Equal(expected.ToJson(), actual.ToJson());

    /// <summary>
    /// The shell JSON of a <c>dateTrunc</c> key as the compiler writes it: the <c>$dateTrunc</c>
    /// given, kept within the calendar a date on the wire can name. Spelled out here, apart from
    /// the compiler, so a change of the clamp shows in every case that pins a bucket.
    /// </summary>
    public static string InCalendar(string dateTrunc) =>
        $$"""{ $let: { vars: { bucket: {{dateTrunc}} }, in: { $switch: { branches: [ { case: { $and: [ { $gt: [ "$$bucket", null ] }, { $lt: [ "$$bucket", ISODate("0001-01-01T00:00:00Z") ] } ] }, then: ISODate("0001-01-01T00:00:00Z") }, { case: { $gt: [ "$$bucket", ISODate("9999-12-31T23:59:59.999Z") ] }, then: ISODate("9999-12-31T23:59:59.999Z") } ], default: "$$bucket" } } } }""";
}
