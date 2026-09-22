using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The declared scalar kinds through a group and a case-insensitive comparison: an aggregate's
/// output carries the kind's own wire encoding, a pushed array encodes its elements the way
/// a row encodes the member, a calendar date truncates on its calendar day in every zone, and
/// a char folds its case by code point rather than by a pattern it could never match.
/// </summary>
public class CoreHardeningKindsTests
{
    private const string Order = "probe.order";

    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static async Task<BoundPipeline> Bound(string pipeline) => await BindHost.BoundAsync(BindHost.Probe, Order, pipeline);

    private static async Task<CompiledQuery> Compile(string pipeline) => MongoCompiler.Compile(await Bound(pipeline), Options);

    private static async Task<BsonDocument> GroupStage(string pipeline) => (await Compile(pipeline)).PageStages[1];

    private static Aggregate Field(BoundPipeline bound, string alias) =>
        bound.Stages.OfType<BoundStage.Group>().Single().Fields.Single(field => field.As == alias);

    private static BoundCondition.Leaf Leaf(BoundPipeline bound) => (BoundCondition.Leaf)((BoundStage.Match)bound.Stages[0]).Condition;

    /// <summary>Executes a grouped pipeline over the row the database would hand back for it and returns the wire row.</summary>
    private static async Task<JsonNode> Row(string pipeline, BsonDocument row)
    {
        var runner = new FakeAggregateRunner { PageRows = [row] };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());

        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), BindHost.Context());

        return outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result.Items.Should().ContainSingle().Subject!;
    }

    private static string Group(string fields) => $$"""[{ "group": { "by": [], "fields": {{fields}} } }]""";

    // ---- avg over a long --------------------------------------------------------------------

    /// <summary>
    /// A double cannot hold a 64-bit mean: above 2^53 its granularity is coarser than one, so
    /// the mean of exact integers comes back off by a fraction. The mean is taken in decimal,
    /// which the wire spells as a string the way it spells a long.
    /// </summary>
    [Fact]
    public async Task An_average_over_a_long_is_taken_in_decimal_and_travels_as_a_string()
    {
        var pipeline = Group("""{ "mean": { "avg": "big" } }""");

        Field(await Bound(pipeline), "mean").OutputKind.Should().Be(Kind.Decimal);
        (await GroupStage(pipeline)).ShouldBeBson(BsonDocument.Parse("""{ $group: { _id: {}, mean: { $avg: { $toDecimal: "$Big" } } } }"""));

        var whole = await Row(pipeline, new BsonDocument("mean", new BsonDecimal128(Decimal128.Parse("3002399751580345"))));
        var half = await Row(pipeline, new BsonDocument("mean", new BsonDecimal128(Decimal128.Parse("4503599627370517.5"))));

        whole["mean"]!.GetValueKind().Should().Be(JsonValueKind.String);
        whole["mean"]!.ToString().Should().Be("3002399751580345");
        half["mean"]!.ToString().Should().Be("4503599627370517.5");
    }

    [Fact]
    public async Task An_average_over_a_long_arithmetic_is_taken_in_decimal_too() =>
        (await GroupStage(Group("""{ "mean": { "avg": { "add": ["big", 1] } } }""")))
            .ShouldBeBson(BsonDocument.Parse("""{ $group: { _id: {}, mean: { $avg: { $toDecimal: { $add: ["$Big", { $literal: NumberLong(1) }] } } } } }"""));

    [Fact]
    public async Task The_other_numeric_aggregates_keep_their_kind_and_their_pipeline()
    {
        var pipeline = Group("""{ "mean": { "avg": "count" }, "total": { "sum": "big" }, "exact": { "avg": "amount" }, "top": { "max": "big" } }""");
        var bound = await Bound(pipeline);

        Field(bound, "mean").OutputKind.Should().Be(Kind.Double, "an int mean fits a double");
        Field(bound, "total").OutputKind.Should().Be(Kind.Long);
        Field(bound, "exact").OutputKind.Should().Be(Kind.Decimal);
        Field(bound, "top").OutputKind.Should().Be(Kind.Long);
        (await GroupStage(pipeline)).ShouldBeBson(BsonDocument.Parse("""{ $group: { _id: {}, mean: { $avg: "$Count" }, total: { $sum: "$Big" }, exact: { $avg: "$Amount" }, top: { $max: "$Big" } } }"""));
    }

    // ---- push -------------------------------------------------------------------------------

    /// <summary>
    /// A pushed array carries the member's values, so each element is encoded the way the
    /// row encodes the member: a long is a string whatever its size, a date is its calendar
    /// day, a char is its character. A null element stays null.
    /// </summary>
    [Fact]
    public async Task A_pushed_long_is_a_string_in_every_element()
    {
        var row = await Row(Group("""{ "all": { "push": "big" } }"""), new BsonDocument("all", new BsonArray { 9007199254740993L, 42L, 0L, BsonNull.Value }));

        row["all"]!.AsArray().Select(item => item?.ToJsonString()).Should().Equal("\"9007199254740993\"", "\"42\"", "\"0\"", null);
    }

    [Fact]
    public async Task A_pushed_date_is_a_calendar_day_in_every_element()
    {
        var row = await Row(Group("""{ "all": { "push": "day" } }"""), new BsonDocument("all", new BsonArray
        {
            new BsonDateTime(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc)),
            new BsonDateTime(new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc)),
        }));

        row["all"]!.AsArray().Select(item => item!.ToString()).Should().Equal("2026-03-05", "2026-04-06");
    }

    [Fact]
    public async Task A_pushed_char_is_a_character_in_every_element()
    {
        var row = await Row(Group("""{ "all": { "push": "initial" } }"""), new BsonDocument("all", new BsonArray { 65, 66, 67 }));

        row["all"]!.AsArray().Select(item => item!.ToString()).Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task A_pushed_arithmetic_over_longs_is_encoded_as_a_long()
    {
        var row = await Row(Group("""{ "all": { "push": { "add": ["big", 1] } } }"""), new BsonDocument("all", new BsonArray { 9007199254740994L, 43L }));

        row["all"]!.AsArray().Select(item => item!.ToJsonString()).Should().Equal("\"9007199254740994\"", "\"43\"");
    }

    [Fact]
    public async Task A_pushed_object_encodes_its_members()
    {
        var row = await Row(Group("""{ "all": { "push": "shipTo" } }"""), new BsonDocument("all", new BsonArray { new BsonDocument("City", "Bremen"), BsonNull.Value }));

        row["all"]!.AsArray().Select(item => item?.ToJsonString()).Should().Equal("""{"city":"Bremen"}""", null);
    }

    [Fact]
    public async Task A_push_alias_stays_a_collection_that_no_filter_or_sort_takes()
    {
        var group = """{ "group": { "by": [], "fields": { "all": { "push": "big" } } } }""";

        await BindHost.ErrorAsync(BindHost.Probe, Order, $$"""[{{group}}, { "match": { "all": { "eq": "1" } } }]""", Codes.NotFilterable);
        await BindHost.ErrorAsync(BindHost.Probe, Order, $$"""[{{group}}, { "sort": [{ "all": "asc" }] }]""", Codes.NotSortable);
    }
}
