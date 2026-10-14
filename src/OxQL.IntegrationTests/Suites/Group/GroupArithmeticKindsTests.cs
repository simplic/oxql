using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// What the arithmetic of an aggregate's argument takes and what it answers, by the kind and the
/// storage of each operand and by the contract of the request: a duration stored as a number is a
/// number, one stored as text is none; a mean keeps the kind it has over whole numbers; a contract 1
/// request is bound without the refusal by kind, and the rows decide.
/// </summary>
[Trait("Category", "Integration")]
public class GroupArithmeticKindsTests
{
    private const string Entity = "ak.job";
    private const string Collection = "tmp_ak_jobs";

    public sealed class JobModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = "";

        public bool Active { get; set; }

        public long Units { get; set; }

        public long Parts { get; set; }

        /// <summary>Stored as the driver writes a duration by default: text.</summary>
        public TimeSpan Text { get; set; }

        /// <summary>Stored as ticks.</summary>
        [BsonTimeSpanOptions(BsonType.Int64)]
        public TimeSpan Ticks { get; set; }

        /// <summary>Stored as milliseconds.</summary>
        [BsonTimeSpanOptions(BsonType.Double, TimeSpanUnits.Milliseconds)]
        public TimeSpan Millis { get; set; }
    }

    private static readonly EngineDirect Direct = new(ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(JobModel), Collection, null, false)]));

    /// <summary>Minutes of the three rows: 90, 0.5 and 1440.</summary>
    private static readonly TimeSpan[] Spans = [TimeSpan.FromMinutes(90), TimeSpan.FromSeconds(30), TimeSpan.FromDays(1)];

    private static readonly (long Units, long Parts)[] Counts = [(1, 3), (2, 3), (10, 7)];

    private static async Task<(TestDatabase Owned, IQueryEngine Engine)> SeedAsync()
    {
        var owned = await MongoFixture.CreateDatabaseAsync("arithmetic_kinds");

        await owned.Database.GetCollection<BsonDocument>(Collection).InsertManyAsync(Spans.Select((span, index) => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["Name"] = "job " + index,
            ["Active"] = index % 2 == 0,
            ["Units"] = Counts[index].Units,
            ["Parts"] = Counts[index].Parts,
            ["Text"] = span.ToString("c"),
            ["Ticks"] = span.Ticks,
            ["Millis"] = span.TotalMilliseconds,
        }));

        return (owned, Direct.Engine(await MongoFixture.ClientAsync(), owned.Name));
    }

    private static string Totals(string fields) => $$"""[{ "group": { "by": [], "fields": { {{fields}} } } }]""";

    private static async Task<QueryOutcome> RunAsync(IQueryEngine engine, string fields, int contract = 2) =>
        await engine.ExecuteAsync(EngineDirect.Request(Entity, Totals(fields)), Direct.Context(contract: contract));

    private static JsonObject Row(QueryOutcome outcome) =>
        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result.Items.Single()!.AsObject();

    private static async Task<Aggregate> FieldAsync(string field, int contract = 2)
    {
        var bound = await Direct.BindAsync(Entity, Totals($$""" "r": {{field}} """), Direct.Context(contract: contract));

        return bound.Stages.OfType<BoundStage.Group>().Single().Fields.Single();
    }

    [Fact]
    public async Task A_duration_stored_as_a_number_is_an_operand_and_one_stored_as_text_is_none()
    {
        var (owned, engine) = await SeedAsync();

        await using (owned)
        {
            var totals = Row(await RunAsync(engine, """
                "minutes": { "sum": { "divide": ["millis", 60000] } },
                "ticksAsMinutes": { "sum": { "divide": ["ticks", 600000000] } },
                "longest": { "max": { "divide": ["millis", 60000] } },
                "ticks": { "sum": { "add": ["ticks", 0] } },
                "doubled": { "max": { "multiply": ["ticks", 2] } }
                """));

            totals["minutes"]!.GetValue<double>().Should().Be(90 + 0.5 + 1440);
            totals["ticksAsMinutes"]!.GetValue<double>().Should().Be(90 + 0.5 + 1440);
            totals["longest"]!.GetValue<double>().Should().Be(1440);
            totals["ticks"]!.GetValue<long>().Should().Be(Spans.Sum(span => span.Ticks));
            totals["doubled"]!.GetValue<long>().Should().Be(TimeSpan.FromDays(2).Ticks);

            // The kinds: a duration in ticks counts as the whole number it is stored as, one in milliseconds as the double.
            (await FieldAsync("""{ "sum": { "add": ["ticks", 1] } }""")).OutputKind.Should().Be(Kind.Long);
            (await FieldAsync("""{ "sum": { "add": ["millis", 1] } }""")).OutputKind.Should().Be(Kind.Double);
            (await FieldAsync("""{ "sum": { "divide": ["ticks", 600000000] } }""")).OutputKind.Should().Be(Kind.Double);

            foreach (var argument in new[] { """{ "divide": ["text", 60000] }""", """{ "add": ["text", 1] }""", """{ "multiply": [2, "text"] }""" })
            {
                var refused = (await RunAsync(engine, $$""" "r": { "max": {{argument}} } """)).Should().BeOfType<QueryOutcome.Refused>(argument).Subject.Refusal;

                refused.Status.Should().Be(400, argument);
                refused.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.InvalidAggregateArgument);
                ((IEnumerable<string>)refused.Errors![0].Params!["kinds"]!).Should().Contain("timeSpan");
            }
        }
    }

    [Fact]
    public async Task A_mean_over_a_quotient_of_whole_numbers_is_a_decimal_and_a_sum_or_an_extreme_over_one_a_double()
    {
        var (owned, engine) = await SeedAsync();

        await using (owned)
        {
            foreach (var argument in new[] { """{ "divide": ["units", "parts"] }""", """{ "add": [{ "divide": ["units", "parts"] }, 0] }""" })
            {
                var mean = await FieldAsync($$"""{ "avg": {{argument}} }""");

                mean.ArgumentKind.Should().Be(Kind.Long, argument);
                mean.OutputKind.Should().Be(Kind.Decimal, argument);

                foreach (var function in new[] { "sum", "min", "max" })
                    (await FieldAsync($$"""{ "{{function}}": {{argument}} }""")).OutputKind.Should().Be(Kind.Double, function + " " + argument);
            }

            (await FieldAsync("""{ "avg": { "divide": ["units", 2.5] } }""")).OutputKind.Should().Be(Kind.Double);

            var totals = Row(await RunAsync(engine, """
                "mean": { "avg": { "divide": ["units", "parts"] } },
                "sum": { "sum": { "divide": ["units", "parts"] } },
                "least": { "min": { "divide": ["units", "parts"] } }
                """));

            // The mean of 1/3, 2/3 and 10/7 as the database's decimal has it: every digit a decimal128 holds of the mean of the three doubles.
            var mean128 = Decimal128.Parse(totals["mean"]!.ToJsonString());
            var expected = (1d / 3 + 2d / 3 + 10d / 7) / 3;

            ((double)Decimal128.ToDecimal(mean128)).Should().BeApproximately(expected, 1e-14);
            totals["mean"]!.ToJsonString().Should().Be("0.80952380952381", "the mean is taken in decimal, each quotient with the 15 digits a decimal takes of a double");
            totals["sum"]!.GetValue<double>().Should().Be(1d / 3 + 2d / 3 + 10d / 7);
            totals["least"]!.GetValue<double>().Should().Be(1d / 3);
        }
    }

    [Fact]
    public async Task A_contract_1_request_is_bound_without_the_refusal_by_kind_and_the_rows_decide()
    {
        var (owned, engine) = await SeedAsync();

        await using (owned)
        {
            // Bound as a contract 1 request is: arithmetic is a long unless an operand is a double or a decimal.
            (await FieldAsync("""{ "sum": { "divide": ["units", "parts"] } }""", contract: 1)).OutputKind.Should().Be(Kind.Long);
            (await FieldAsync("""{ "max": { "add": ["name", 1] } }""", contract: 1)).OutputKind.Should().Be(Kind.Long);
            (await FieldAsync("""{ "max": { "add": ["text", 1] } }""", contract: 1)).OutputKind.Should().Be(Kind.Long);
            (await FieldAsync("""{ "max": { "subtract": ["units", "parts", 1] } }""", contract: 1)).OutputKind.Should().Be(Kind.Long);

            // A row that holds text where the arithmetic reads a number is the coded refusal of the run, under either contract.
            foreach (var argument in new[] { """{ "add": ["name", 1] }""", """{ "multiply": ["text", 2] }""" })
            {
                var refused = (await RunAsync(engine, $$""" "r": { "max": {{argument}} } """, contract: 1)).Should().BeOfType<QueryOutcome.Refused>(argument).Subject.Refusal;

                refused.Status.Should().Be(422, argument);
                refused.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.InvalidAggregateArgument);
            }

            // A quotient by zero is null under either contract.
            Row(await RunAsync(engine, """ "r": { "max": { "divide": ["units", 0] } } """, contract: 1))["r"].Should().BeNull();

            // Arithmetic that is over numbers answers as it does under contract 2.
            Row(await RunAsync(engine, """ "r": { "sum": { "multiply": ["units", "parts"] } } """, contract: 1))["r"]!.GetValue<long>().Should().Be(1 * 3 + 2 * 3 + 10 * 7);
        }
    }
}
