using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using Xunit;

namespace OxQL.Tests.Cursor;

public class CursorCodecTests
{
    private static readonly CursorCodec Codec = new("secret");

    [Fact]
    public void A_cursor_round_trips_with_its_bson_types()
    {
        var id = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
        var payload = new CursorPayload("sha256:abc", PagingMode.Keyset,
            [new CursorValue("when", false, new BsonDateTime(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))), new CursorValue("amount", true, new BsonDecimal128(new Decimal128(1.5m))), new CursorValue("note", true, BsonNull.Value), new CursorValue("_id", true, id)], 0);

        var cursor = Codec.Encode(payload);
        var decoded = Codec.Decode(cursor, "sha256:abc");

        cursor.Should().MatchRegex("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", "opaque base64url with a signature");
        decoded.Should().NotBeNull();
        decoded!.Mode.Should().Be(PagingMode.Keyset);
        decoded.Fields.Select(field => (field.Wire, field.Ascending)).Should().Equal(("when", false), ("amount", true), ("note", true), ("_id", true));
        decoded.Fields[0].Value.Should().BeOfType<BsonDateTime>();
        decoded.Fields[1].Value.Should().Be(new BsonDecimal128(new Decimal128(1.5m)));
        decoded.Fields[2].Value.Should().Be(BsonNull.Value);
        decoded.Fields[3].Value.Should().Be(id);
    }

    [Fact]
    public void An_offset_cursor_carries_the_offset()
    {
        var decoded = Codec.Decode(Codec.Encode(new CursorPayload("f", PagingMode.Offset, [], 300)), "f");

        decoded!.Mode.Should().Be(PagingMode.Offset);
        decoded.Offset.Should().Be(300);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData("a.")]
    [InlineData("!!!.!!!")]
    public void A_malformed_cursor_is_rejected(string cursor) => Codec.Decode(cursor, "f").Should().BeNull();

    [Fact]
    public void A_tampered_or_foreign_cursor_is_rejected()
    {
        var cursor = Codec.Encode(new CursorPayload("f", PagingMode.Offset, [], 300));
        var dot = cursor.IndexOf('.');
        var tampered = (cursor[0] == 'A' ? 'B' : 'A') + cursor[1..];

        Codec.Decode(tampered, "f").Should().BeNull("the signature no longer matches");
        Codec.Decode(cursor[..dot] + ".AAAA", "f").Should().BeNull();
        Codec.Decode(cursor, "other-query").Should().BeNull("the fingerprint is bound to the query");
        new CursorCodec("another-secret").Decode(cursor, "f").Should().BeNull("another host's key does not verify it");
    }

    [Fact]
    public void The_engine_refuses_to_run_without_a_signing_key()
    {
        ((Action)(() => new CursorCodec(null))).Should().Throw<InvalidOperationException>();
        ((Action)(() => new CursorCodec(" "))).Should().Throw<InvalidOperationException>();
    }
}

/// <summary>The exact null-aware legs the Stage 0 spike measured index-backed.</summary>
public class KeysetPredicateTests
{
    private static readonly BsonValue Id = new BsonInt32(120);

    private static BsonDocument Build(params KeysetField[] fields) => KeysetPredicate.Build(fields, "_id", Id);

    [Fact]
    public void Ascending_after_a_value()
    {
        Build(new KeysetField("n", true, new BsonInt32(5)))
            .ShouldBeBson(BsonDocument.Parse("{ $or: [ { n: 5, _id: { $gt: 120 } }, { n: { $gt: 5 } } ] }"));
    }

    [Fact]
    public void Ascending_after_a_null_continues_the_null_bracket_then_everything_present()
    {
        Build(new KeysetField("n", true, BsonNull.Value))
            .ShouldBeBson(BsonDocument.Parse("{ $or: [ { n: null, _id: { $gt: 120 } }, { n: { $ne: null } } ] }"));
    }

    [Fact]
    public void Descending_after_a_value_includes_the_null_bracket_that_follows()
    {
        Build(new KeysetField("n", false, new BsonInt32(5)))
            .ShouldBeBson(BsonDocument.Parse("{ $or: [ { n: 5, _id: { $gt: 120 } }, { n: { $lt: 5 } }, { n: null } ] }"));
    }

    [Fact]
    public void Descending_after_a_null_is_only_the_tie_breaker()
    {
        Build(new KeysetField("n", false, BsonNull.Value))
            .ShouldBeBson(BsonDocument.Parse("{ n: null, _id: { $gt: 120 } }"));
    }

    [Fact]
    public void Two_fields_compose_prefix_equalities_per_leg()
    {
        Build(new KeysetField("a", false, new BsonString("x")), new KeysetField("b", true, BsonNull.Value))
            .ShouldBeBson(BsonDocument.Parse("""
                { $or: [
                    { a: "x", b: null, _id: { $gt: 120 } },
                    { a: "x", b: { $ne: null } },
                    { a: { $lt: "x" } },
                    { a: null } ] }
                """));
    }

    // ---- across a NaN and across stored types: what a contract 2 page sends ----

    private static BsonDocument Typed(bool inclusive, params KeysetField[] legs) => KeysetPredicate.After(legs, inclusive, everyType: true);

    private static readonly KeysetField Key = new("_id", true, Id);

    [Fact]
    public void An_ascending_number_and_text_of_a_member_of_one_type_are_asked_as_before()
    {
        Typed(false, new KeysetField("n", true, new BsonInt32(5)), Key).ShouldBeBson(Build(new KeysetField("n", true, new BsonInt32(5))));
        Typed(false, new KeysetField("n", false, new BsonString("x")), Key).ShouldBeBson(Build(new KeysetField("n", false, new BsonString("x"))));
        Typed(false, new KeysetField("n", true, new BsonDouble(1.5)), Key).ShouldBeBson(Build(new KeysetField("n", true, new BsonDouble(1.5))));
    }

    [Fact]
    public void Descending_after_a_number_the_NaN_follows_the_smaller_numbers()
    {
        Typed(false, new KeysetField("n", false, new BsonDouble(1.5)), Key)
            .ShouldBeBson(BsonDocument.Parse("""{ $or: [ { n: 1.5, _id: { $gt: 120 } }, { n: { $lt: 1.5 } }, { n: { $numberDouble: "NaN" } }, { n: null } ] }"""));
    }

    [Fact]
    public void After_a_NaN_every_number_follows_ascending_and_none_descending()
    {
        Typed(false, new KeysetField("n", true, new BsonDouble(double.NaN)), Key)
            .ShouldBeBson(BsonDocument.Parse("""{ $or: [ { n: { $numberDouble: "NaN" }, _id: { $gt: 120 } }, { n: { $gte: { $numberDouble: "-Infinity" } } } ] }"""));

        Typed(false, new KeysetField("n", false, new BsonDecimal128(Decimal128.Parse("NaN"))), Key)
            .ShouldBeBson(BsonDocument.Parse("""{ $or: [ { n: { $numberDecimal: "NaN" }, _id: { $gt: 120 } }, { n: null } ] }"""));
    }

    [Fact]
    public void A_leg_of_several_stored_types_admits_the_types_that_order_beyond_the_positions()
    {
        // Ascending after a number: the larger numbers, then text and every type after it.
        Typed(false, new KeysetField("n", true, new BsonDecimal128(5m)) { SeveralTypes = true }, Key)
            .ShouldBeBson(BsonDocument.Parse("""{ $or: [ { n: { $numberDecimal: "5" }, _id: { $gt: 120 } }, { n: { $gt: { $numberDecimal: "5" } } }, { n: { $type: [2, 14, 3, 5, 7, 8, 9, 17, 11, 12, 13, 15, 127] } } ] }"""));

        // Descending after text: the smaller text, then every number (a NaN is one), then the nulls.
        Typed(false, new KeysetField("n", false, new BsonString("7")) { SeveralTypes = true }, Key)
            .ShouldBeBson(BsonDocument.Parse("""{ $or: [ { n: "7", _id: { $gt: 120 } }, { n: { $lt: "7" } }, { n: { $type: [1, 16, 18, 19] } }, { n: null } ] }"""));
    }

    [Fact]
    public void The_leading_range_of_such_a_leg_holds_the_later_types_and_from_a_NaN_on_everything()
    {
        KeysetPredicate.LeadingRange([new KeysetField("n", true, new BsonDecimal128(5m)) { SeveralTypes = true }, Key], everyType: true)!
            .ShouldBeBson(BsonDocument.Parse("""{ $or: [ { n: { $gte: { $numberDecimal: "5" } } }, { n: { $type: [2, 14, 3, 5, 7, 8, 9, 17, 11, 12, 13, 15, 127] } } ] }"""));
        KeysetPredicate.LeadingRange([new KeysetField("n", true, new BsonDouble(double.NaN)), Key], everyType: true)!
            .ShouldBeBson(BsonDocument.Parse("""{ n: { $ne: null } }"""));
        KeysetPredicate.LeadingRange([new KeysetField("n", true, new BsonDouble(1.5)), Key], everyType: true)!
            .ShouldBeBson(BsonDocument.Parse("""{ n: { $gte: 1.5 } }"""));
        KeysetPredicate.LeadingRange([new KeysetField("n", true, new BsonDecimal128(5m)) { SeveralTypes = true }, Key])!
            .ShouldBeBson(BsonDocument.Parse("""{ n: { $gte: { $numberDecimal: "5" } } }"""), "without the flag the range is the one it was");
    }

    [Fact]
    public void No_sort_fields_is_the_key_alone()
    {
        Build().ShouldBeBson(BsonDocument.Parse("{ _id: { $gt: 120 } }"));
    }
}
