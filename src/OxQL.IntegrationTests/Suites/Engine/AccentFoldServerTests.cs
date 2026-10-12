using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fleet;
using OxQL.Mongo;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// The pattern of <c>contains</c> and <c>endsWith</c> against the server's own collation:
/// for every pair of spellings, the pattern built from one, run by the server as the engine sends
/// it, finds the other exactly when the server says the two are equal under the collation (<c>eq</c>). The server's answer is measured, not
/// assumed: the letters that expand to two (<c>ß</c>, <c>æ</c>, <c>œ</c>, <c>þ</c>, <c>ĳ</c>, the
/// ligatures) are in the list beside the accents.
/// </summary>
[Trait("Category", "Integration")]
public class AccentFoldServerTests(ITestOutputHelper output)
{
    /// <summary>Groups of spellings a reader might take for one word; whether the server does is what is measured.</summary>
    private static readonly string[][] Families =
    [
        ["strasse", "straße", "STRASSE", "straẞe", "strase"],
        ["aether", "æther", "Æther", "ather", "äther"],
        ["oeuvre", "œuvre", "Œuvre", "ouvre", "öuvre"],
        ["thorn", "þorn", "Þorn", "torn"],
        ["ijs", "ĳs", "Ĳs", "ys"],
        ["muller", "müller", "MÜLLER", "mueller"],
        ["o", "ø", "Ø", "ö", "oe"],
        ["d", "đ", "Đ", "ð", "Ð"],
        ["l", "ł", "Ł"],
        ["h", "ħ", "Ħ"],
        ["t", "ŧ", "Ŧ"],
        ["fi", "ﬁ", "ff", "ﬀ", "ffi", "ﬃ", "st", "ﬆ"],
        ["dz", "ǆ", "ǅ", "lj", "ǉ", "nj", "ǌ"],
        ["n", "ŉ", "ñ", "ŋ"],
        ["i", "ı", "İ", "I"],
        ["s", "ſ", "ş", "ș", "š"],
        ["a", "ǎ", "ȁ", "ạ", "ả", "ά", "а"],
        ["e", "ẽ", "ệ", "ȩ", "ə"],
        ["c", "ç", "ć", "ĉ", "č", "ƈ"],
        ["k", "ĸ", "ķ"],
    ];

    [Fact]
    public async Task The_pattern_of_contains_finds_a_spelling_exactly_when_the_server_calls_it_equal_under_the_collation()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("accent_fold");
        var collection = owned.Database.GetCollection<BsonDocument>("words");
        var collation = new CollationOptions();
        var words = Families.SelectMany(family => family).Distinct(StringComparer.Ordinal).ToList();

        // One document per spelling: 's' is compared under the request's collation, '_id' names it exactly.
        await collection.InsertManyAsync(words.Select(word => new BsonDocument { ["_id"] = word, ["s"] = word }));

        var disagreements = new List<string>();

        foreach (var family in Families)
            foreach (var stored in family)
                foreach (var operand in family)
                {
                    var options = new FindOptions<BsonDocument> { Collation = new Collation(collation.Locale, strength: (CollationStrength)collation.Strength) };
                    using var cursor = await collection.FindAsync(new BsonDocument("s", operand), options);
                    var equal = (await cursor.ToListAsync()).Any(document => string.Equals(document["_id"].AsString, stored, StringComparison.Ordinal));
                    // The pattern as the engine sends it: a regular expression with the i option, which knows no collation.
                    using var matched = await collection.FindAsync(new BsonDocument("s", new BsonRegularExpression("^(?:" + AccentFold.Pattern(operand, collation) + ")$", "i")));
                    var found = (await matched.ToListAsync()).Any(document => string.Equals(document["_id"].AsString, stored, StringComparison.Ordinal));

                    if (equal != found)
                        disagreements.Add($"'{operand}' vs stored '{stored}': eq {(equal ? "equal" : "different")}, contains {(found ? "finds" : "misses")}");
                }

        foreach (var line in disagreements)
            output.WriteLine(line);

        disagreements.Should().BeEmpty("contains agrees with eq under the collation for every spelling of these families");
    }
}
