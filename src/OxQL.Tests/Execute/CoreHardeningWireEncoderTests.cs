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
/// A stored value its kind cannot express is rendered verbatim: the row it is on is still a
/// row, and the page it is on is still a page.
/// </summary>
public class CoreHardeningWireEncoderTests
{
    private const string Order = "probe.order";

    /// <summary>The first millisecond of the year 10000, which no <see cref="DateTime"/> reaches.</summary>
    private const long BeyondTheCalendar = 253_402_300_800_000;

    private static readonly BsonDecimal128 NotANumber = new(Decimal128.QNaN);
    private static readonly BsonDecimal128 Infinite = new(Decimal128.PositiveInfinity);
    private static readonly BsonDecimal128 Enormous = new(Decimal128.Parse("1E+6000"));

    public static TheoryData<BsonValue, Kind, string?> OutOfRange => new()
    {
        { NotANumber, Kind.Decimal, "NaN" },
        { Infinite, Kind.Decimal, "Infinity" },
        { Enormous, Kind.Decimal, "1E+6000" },
        { NotANumber, Kind.Double, "NaN" },
        { Enormous, Kind.Double, "1E+6000" },
        { NotANumber, Kind.Int, "NaN" },
        { Infinite, Kind.Long, "Infinity" },
        { new BsonDouble(double.NaN), Kind.Decimal, null },
        { new BsonDouble(1e300), Kind.Decimal, "1E+300" },
        { new BsonDouble(double.NaN), Kind.TimeSpan, null },
        { new BsonDouble(1e300), Kind.TimeSpan, "1E+300" },
        { new BsonDateTime(BeyondTheCalendar), Kind.Date, "253402300800000" },
        { new BsonDateTime(BeyondTheCalendar), Kind.DateTime, "253402300800000" },
        { new BsonDateTime(-BeyondTheCalendar), Kind.DateTime, "-253402300800000" },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_value_outside_its_kinds_range_is_rendered_verbatim(BsonValue stored, Kind kind, string? expected)
    {
        var encoded = WireEncoder.EncodeScalar(stored, kind, null);

        (encoded?.ToString()).Should().Be(expected);
    }

    [Theory]
    [InlineData(100_000d)]
    [InlineData(0.5d)]
    [InlineData(double.NaN)]
    public void An_offset_no_DateTimeOffset_carries_leaves_the_document_verbatim(double minutes)
    {
        var stored = new BsonDocument { ["DateTime"] = new BsonDateTime(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)), ["Ticks"] = 0L, ["Offset"] = minutes };

        var encoded = WireEncoder.EncodeScalar(stored, Kind.DateTime, null);

        encoded.Should().NotBeNull();
        encoded!["DateTime"]!.ToString().Should().Be("2024-01-02T03:04:05Z");
    }

    [Fact]
    public void Verbatim_renders_everything_the_database_can_hold()
    {
        WireEncoder.Verbatim(NotANumber)!.ToString().Should().Be("NaN");
        WireEncoder.Verbatim(new BsonDateTime(BeyondTheCalendar))!.ToString().Should().Be("253402300800000");
        WireEncoder.Verbatim(new BsonDecimal128(new Decimal128(12.5m)))!.ToString().Should().Be("12.5");
        WireEncoder.Verbatim(new BsonDateTime(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)))!.ToString().Should().Be("2024-01-02T00:00:00Z");
    }

    [Fact]
    public async Task One_unexpressible_value_does_not_cost_the_page_and_is_logged_once()
    {
        var log = new Recorder();
        var runner = new FakeAggregateRunner
        {
            PageRows =
            [
                Row("a", NotANumber, new BsonDateTime(BeyondTheCalendar)),
                Row("b", new BsonDecimal128(new Decimal128(12.5m)), new BsonDateTime(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc))),
                Row("c", Infinite, new BsonDateTime(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc))),
            ],
        };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options(), logger: log);

        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, "[]"), BindHost.Context());

        var items = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result.Items;

        items.Should().HaveCount(3);
        items[0]!["amount"]!.ToString().Should().Be("NaN");
        items[0]!["when"]!.ToString().Should().Be("253402300800000");
        items[1]!["amount"]!.ToString().Should().Be("12.5");
        items[1]!["when"]!.ToString().Should().Be("2024-01-02T00:00:00Z");
        items[2]!["amount"]!.ToString().Should().Be("Infinity");

        var warning = log.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().ContainSingle().Subject.Message;

        warning.Should().Contain("3 stored values").And.Contain("amount").And.Contain("when");
        warning.Should().NotContain("NaN", "the log names the path, never the value");
    }

    [Fact]
    public async Task A_page_of_values_in_range_logs_no_warning()
    {
        var log = new Recorder();
        var runner = new FakeAggregateRunner { PageRows = [Row("a", new BsonDecimal128(new Decimal128(1m)), new BsonDateTime(DateTime.UnixEpoch))] };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options(), logger: log);

        await engine.ExecuteAsync(BindHost.Request(Order, "[]"), BindHost.Context());

        log.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
    }

    private static BsonDocument Row(string number, BsonValue amount, BsonValue when) => new()
    {
        ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
        ["Number"] = number,
        ["Amount"] = amount,
        ["When"] = when,
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
    };

    private sealed class Recorder : ILogger<MongoQueryEngine>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // ---- a defined date addon key ----------------------------------------------------------

    private sealed class DateDefinitions : OxQL.Model.Addon.IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<OxQL.Model.Addon.AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OxQL.Model.Addon.AddonDefinition>>(entity == Order
                ? [
                    new() { Id = Guid.NewGuid(), Entity = Order, Path = "due", Kind = OxQL.Model.Addon.AddonKind.Date },
                    new() { Id = Guid.NewGuid(), Entity = Order, Path = "trip.start", Kind = OxQL.Model.Addon.AddonKind.Date },
                    new() { Id = Guid.NewGuid(), Entity = Order, Path = "stamp", Kind = OxQL.Model.Addon.AddonKind.DateTime },
                    new() { Id = Guid.NewGuid(), Entity = Order, Path = "old", Kind = OxQL.Model.Addon.AddonKind.Date, Retired = true },
                  ]
                : []);
    }

    [Fact]
    public async Task A_defined_date_addon_key_is_written_as_a_date_and_every_other_bag_value_as_the_bag_holds_it()
    {
        var midnight = new BsonDateTime(new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc));
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "project": { "id": 1, "addon": 1 } }]""", BindHost.Context(addons: new DateDefinitions()));
        var row = WireEncoder.Encode(new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.Parse("11111111-1111-1111-1111-111111111111"), GuidRepresentation.Standard),
            ["Addon"] = new BsonDocument
            {
                ["due"] = midnight,
                ["trip"] = new BsonDocument("start", midnight),
                ["stamp"] = midnight,
                ["old"] = midnight,
                ["plain"] = midnight,
                ["text"] = "2026-06-16",
            },
        }, bound);

        var bag = row["addon"]!;

        bag["due"]!.GetValue<string>().Should().Be("2026-06-16", "a defined date travels as YYYY-MM-DD, the form its operand takes");
        bag["trip"]!["start"]!.GetValue<string>().Should().Be("2026-06-16", "a definition path may point inside a subobject");
        bag["stamp"]!.GetValue<string>().Should().Be("2026-06-16T00:00:00Z", "a dateTime key is an instant");
        bag["old"]!.GetValue<string>().Should().Be("2026-06-16T00:00:00Z", "a retired key is unknown and stays what the bag holds");
        bag["plain"]!.GetValue<string>().Should().Be("2026-06-16T00:00:00Z", "an undefined key has no kind to encode by");
        bag["text"]!.GetValue<string>().Should().Be("2026-06-16");
    }
}
