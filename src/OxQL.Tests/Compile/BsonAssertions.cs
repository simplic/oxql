using MongoDB.Bson;
using Xunit;

namespace OxQL.Tests;

/// <summary>Structural equality of BSON values through their shell JSON, so a mismatch shows both forms.</summary>
internal static class BsonAssertions
{
    public static void ShouldBeBson(this BsonValue actual, BsonDocument expected, string because = "") =>
        Assert.Equal(expected.ToJson(), actual.ToJson());
}
