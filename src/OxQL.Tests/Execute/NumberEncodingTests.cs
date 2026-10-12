using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// Contract 2 writes a <c>long</c>, a <c>decimal</c> and an enum backed by a long as JSON numbers
/// with every digit they hold, as the services' REST API writes them. The
/// wire is exact; a reader that parses a number into an IEEE double keeps 15 to 17 significant
/// digits. What is no number stays text and is said. What is compared instead of shown is text.
/// </summary>
public class NumberEncodingTests
{
    private const string Order = "probe.order";

    private static string? Json(BsonValue stored, Kind kind) => WireEncoder.EncodeScalar(stored, kind, null)?.ToJsonString();

    [Fact]
    public void A_long_is_a_number_with_every_digit_whatever_it_is_stored_as()
    {
        Json(new BsonInt64(long.MaxValue), Kind.Long).Should().Be("9223372036854775807");
        Json(new BsonInt64(long.MinValue), Kind.Long).Should().Be("-9223372036854775808");
        Json(new BsonInt64(9007199254740993), Kind.Long).Should().Be("9007199254740993", "one above what a double holds exactly");
        Json(new BsonInt32(42), Kind.Long).Should().Be("42");
        Json(new BsonDouble(42d), Kind.Long).Should().Be("42");
        Json(new BsonDecimal128(new Decimal128(42m)), Kind.Long).Should().Be("42");
    }

    [Fact]
    public void A_decimal_is_a_number_in_its_canonical_digits_whatever_it_is_stored_as()
    {
        Json(new BsonDecimal128(new Decimal128(125000.00m)), Kind.Decimal).Should().Be("125000", "the scale a value was stored with is not kept");
        Json(new BsonDecimal128(new Decimal128(12.50m)), Kind.Decimal).Should().Be("12.5");
        Json(new BsonDecimal128(new Decimal128(0.0000001m)), Kind.Decimal).Should().Be("0.0000001", "never an exponent");
        Json(new BsonDecimal128(new Decimal128(-0.10m)), Kind.Decimal).Should().Be("-0.1");
        Json(new BsonDecimal128(new Decimal128(79228162514264337593543950335m)), Kind.Decimal).Should().Be("79228162514264337593543950335", "the largest decimal, all twenty-nine digits");
        Json(new BsonDecimal128(new Decimal128(1234567890123456789.0123456789m)), Kind.Decimal).Should().Be("1234567890123456789.0123456789", "more digits than a double holds: exact on the wire");
        Json(new BsonDecimal128(Decimal128.Parse("1624965.1234567890123456789012345")), Kind.Decimal).Should().Be("1624965.1234567890123456789012345", "more digits than the runtime's decimal holds: the database's own, all of them");
        Json(new BsonDecimal128(Decimal128.Parse("-1.2345678901234567890123456789012E-3")), Kind.Decimal).Should().Be("-0.0012345678901234567890123456789012");
        Json(new BsonDecimal128(Decimal128.Parse("1.50E+3")), Kind.Decimal).Should().Be("1500");
        WireEncoder.Verbatim(new BsonDecimal128(Decimal128.Parse("0.3333333333333333333333333333333333")))!.ToJsonString().Should().Be("0.3333333333333333333333333333333333");
        Json(new BsonString("1000.00"), Kind.Decimal).Should().Be("1000", "the text a row written before the migration holds is the number it spells");
        Json(new BsonString("1E-7"), Kind.Decimal).Should().Be("0.0000001");
        Json(new BsonInt32(7), Kind.Decimal).Should().Be("7");
        Json(new BsonInt64(9007199254740993), Kind.Decimal).Should().Be("9007199254740993");
        Json(new BsonDouble(0.5), Kind.Decimal).Should().Be("0.5");
    }

    [Fact]
    public void What_is_no_number_stays_text()
    {
        Json(new BsonString("n/a"), Kind.Decimal).Should().Be("\"n/a\"", "text that spells no decimal is what the row holds");
        Json(new BsonDecimal128(Decimal128.QNaN), Kind.Decimal).Should().Be("\"NaN\"");
        Json(new BsonDecimal128(Decimal128.NegativeInfinity), Kind.Decimal).Should().Be("\"-Infinity\"");
        WireEncoder.EncodeScalar(new BsonDecimal128(Decimal128.Parse("1E+6000")), Kind.Decimal, null)!.GetValue<string>().Should().Be("1E+6000", "a magnitude no decimal of the runtime holds");
        Json(new BsonDecimal128(Decimal128.PositiveInfinity), Kind.Long).Should().Be("\"Infinity\"");
        Json(new BsonDouble(1e300), Kind.Long).Should().Be("1E+300", "a double no long holds stays the double it is");
    }

    [Fact]
    public void An_enum_is_a_number_also_where_a_long_backs_it()
    {
        Json(new BsonInt64(9007199254740993), Kind.Enum).Should().Be("9007199254740993");
        Json(new BsonInt64(3), Kind.Enum).Should().Be("3");
        Json(new BsonInt32(3), Kind.Enum).Should().Be("3");
    }

    [Fact]
    public void A_value_the_model_does_not_describe_is_a_number_by_its_stored_type()
    {
        WireEncoder.Verbatim(new BsonInt64(9007199254740993))!.ToJsonString().Should().Be("9007199254740993");
        WireEncoder.Verbatim(new BsonInt64(-5))!.ToJsonString().Should().Be("-5");
        WireEncoder.Verbatim(new BsonDecimal128(new Decimal128(261.80m)))!.ToJsonString().Should().Be("261.8");
        WireEncoder.Verbatim(new BsonDecimal128(Decimal128.QNaN))!.ToJsonString().Should().Be("\"NaN\"");
        WireEncoder.Verbatim(new BsonDocument { ["_t"] = "System.Decimal", ["_v"] = new BsonDecimal128(new Decimal128(1.50m)) })!.ToJsonString().Should().Be("1.5");
    }

    /// <summary>
    /// A reference's key is compared as text, and the text is the same for the number this build
    /// writes and for the string an earlier build wrote: a join between two builds finds its rows.
    /// </summary>
    [Fact]
    public void A_key_is_compared_as_the_same_text_for_a_number_and_for_the_string_an_earlier_build_wrote()
    {
        WireEncoder.KeyText(new BsonInt64(9007199254740993), Kind.Long, null).Should().Be("9007199254740993");
        WireEncoder.KeyText(new BsonDecimal128(new Decimal128(12.50m)), Kind.Decimal, null).Should().Be("12.5");
        WireEncoder.KeyText(new BsonString("abc"), Kind.String, null).Should().Be("abc");
        WireEncoder.KeyText(BsonNull.Value, Kind.Long, null).Should().BeNull();

        // What an owner's answer holds, read off its JSON: the number of this build, the string of the one before.
        var answered = System.Text.Json.Nodes.JsonNode.Parse("""{ "now": 12.50, "before": "12.5", "big": 9007199254740993, "bigBefore": "9007199254740993", "id": "a-1", "count": 7 }""")!;

        WireEncoder.KeyText(answered["now"]).Should().Be(WireEncoder.KeyText(answered["before"])).And.Be("12.5");
        WireEncoder.KeyText(answered["big"]).Should().Be(WireEncoder.KeyText(answered["bigBefore"])).And.Be("9007199254740993");
        WireEncoder.KeyText(answered["id"]).Should().Be("a-1");
        WireEncoder.KeyText(answered["count"]).Should().Be("7");
    }

    [Fact]
    public async Task Text_under_a_decimal_member_that_is_no_number_does_not_cost_the_page_and_is_logged_by_path()
    {
        var log = new Recorder();
        var runner = new FakeAggregateRunner
        {
            PageRows =
            [
                Row("a", new BsonString("n/a")),
                Row("b", new BsonString("12.50")),
                Row("c", new BsonDecimal128(new Decimal128(3m))),
            ],
        };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options(), logger: log);
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, "[]"), BindHost.Context());
        var items = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result.Items;

        items.Select(item => item!["amount"]!.ToJsonString()).Should().Equal("\"n/a\"", "12.5", "3");

        var warning = log.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().ContainSingle().Subject.Message;

        warning.Should().Contain("1 stored values").And.Contain("amount");
        warning.Should().NotContain("n/a", "the log names the path, never the value");
    }

    private static BsonDocument Row(string number, BsonValue amount) => new()
    {
        ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
        ["Number"] = number,
        ["Amount"] = amount,
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
    };

    private sealed class Recorder : ILogger<MongoQueryEngine>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
