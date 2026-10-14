// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Mongo.Legacy;
using OxQL.Tests.Compile;
using Xunit;

namespace OxQL.Tests.Cursor;

/// <summary>
/// The position of a contract 1 cursor of the legacy pipeline, held as the engine before
/// contract 2 wrote it: per sort field one range in the field's direction, the fields before it
/// equal. The position is a comparison with the last value of the page, made among the rows that
/// hold a value in the sort member (see <c>LEGACY.md</c>, <i>Behaviour kept</i>); a walk across
/// the rows that hold null there is a contract 2 walk.
/// </summary>
public class LegacyCursorTests
{
    private static readonly string Key = "2f1d6c1e-6f0a-4a57-9c50-0d7f1a2b3c4d";

    [Fact]
    public void The_position_is_one_range_per_field_in_its_direction()
    {
        LegacyCursor.Filter([new LegacyCursorField("Number", "asc", "N-1")])
            .ShouldBeBson(BsonDocument.Parse("""{ Number: { $gt: "N-1" } }"""));

        LegacyCursor.Filter([new LegacyCursorField("Number", "desc", "N-1"), new LegacyCursorField("Code", "asc", "c")])
            .ShouldBeBson(BsonDocument.Parse("""{ $or: [ { Number: { $lt: "N-1" } }, { Number: "N-1", Code: { $gt: "c" } } ] }"""));
    }

    [Fact]
    public void A_null_at_the_position_is_compared_as_a_value()
    {
        // Ascending after a null: a range above null, which no value is in.
        LegacyCursor.Filter([new LegacyCursorField("Note", "asc", null), new LegacyCursorField("Number", "asc", Key)])
            .ShouldBeBson(BsonDocument.Parse($$"""{ $or: [ { Note: { $gt: null } }, { Note: null, Number: { $gt: "{{Key}}" } } ] }"""));

        // Descending after a value: the smaller values; the nulls that end the order are in no range.
        LegacyCursor.Filter([new LegacyCursorField("Note", "desc", "b"), new LegacyCursorField("Number", "asc", Key)])
            .ShouldBeBson(BsonDocument.Parse($$"""{ $or: [ { Note: { $lt: "b" } }, { Note: "b", Number: { $gt: "{{Key}}" } } ] }"""));
    }
}
