using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Differential;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// The arithmetic of an aggregate's argument against the stored rows: a quotient by zero or by
/// nothing is null and is skipped as a null is, whatever rows the request meets; an operand that is
/// no number is refused when the request is bound, with the code of the argument, and explain says
/// what the run does. Dates take part in the two forms a date can: moved by a number, and one less
/// another.
/// </summary>
[Trait("Category", "Integration")]
public class GroupArithmeticTests
{
    private const string Entity = EdgeCorpus.Entity;

    private static string Totals(string fields) => $$"""[{ "group": { "by": [], "fields": { {{fields}} } } }]""";

    private static double? Number(BsonDocument row, string member) => row.TryGetValue(member, out var value) && value.IsNumeric ? value.ToDouble() : null;

    [Fact]
    public async Task A_quotient_by_zero_or_by_nothing_is_null_and_an_aggregate_skips_it()
    {
        var client = await EdgeCorpus.ClientAsync();
        var rows = EdgeCorpus.Stored();

        // count is zero in every fifth row; optionalCount is zero, null or not stored in others.
        rows.Should().Contain(row => row["Count"].ToInt32() == 0).And.Contain(row => !row.Contains("OptionalCount"));

        var answer = await client.SendAsync(Entity, Totals("""
            "byCount": { "sum": { "divide": ["address.floor", "count"] } },
            "byOptional": { "avg": { "divide": ["count", "optionalCount"] } },
            "largest": { "max": { "divide": [{ "multiply": ["count", 10] }, "address.floor"] } },
            "n": { "count": true },
            "constant": { "max": { "divide": [1, 0] } },
            "nothing": { "max": { "divide": [1, { "literal": null }] } },
            "half": { "max": { "divide": [1, 2] } }
            """));

        answer.ShouldBeOk();

        var totals = answer.Items.Single()!.AsObject();
        var byCount = rows.Where(row => row["Count"].ToInt32() != 0).Sum(row => row["Address"]["Floor"].ToDouble() / row["Count"].ToInt32());
        var byOptional = rows.Select(row => Number(row, "OptionalCount") is { } divisor && divisor != 0 ? row["Count"].ToInt32() / divisor : (double?)null).Where(quotient => quotient is not null).ToList();
        var largest = rows.Where(row => row["Address"]["Floor"].ToInt32() != 0).Max(row => row["Count"].ToInt32() * 10d / row["Address"]["Floor"].ToInt32());

        totals["n"]!.GetValue<int>().Should().Be(EdgeCorpus.Count);
        totals["byCount"]!.GetValue<double>().Should().BeApproximately(byCount, 1e-9);
        totals["byOptional"]!.GetValue<double>().Should().BeApproximately(byOptional.Average()!.Value, 1e-9);
        totals["largest"]!.GetValue<double>().Should().BeApproximately(largest, 1e-9);
        totals["constant"].Should().BeNull("a quotient by zero is null");
        totals["nothing"].Should().BeNull();
        totals["half"]!.GetValue<double>().Should().Be(0.5);

        // Per group, a group whose every divisor is zero included.
        var grouped = await client.SendAsync(Entity, """[{ "group": { "by": [{ "path": "count", "as": "count" }], "fields": { "mean": { "avg": { "divide": ["address.floor", "count"] } } } } }, { "sort": [{ "count": "asc" }] }]""");

        grouped.ShouldBeOk();
        grouped.Items.Select(row => row!["count"]!.GetValue<int>()).Should().Equal(0, 1, 2, 3, 4);
        grouped.Items[0]!["mean"].Should().BeNull("every row of the group divides by zero");
        grouped.Items.Skip(1).Should().OnlyContain(row => row!["mean"] != null);
    }

    public static TheoryData<string, string> NoNumbers() => new()
    {
        { "multiply", """["moment", 2]""" },
        { "multiply", """["active", 2]""" },
        { "add", """["name", 1]""" },
        { "add", """[1, "text"]""".Replace("\"text\"", """{ "literal": "text" }""") },
        { "add", """["moment", "optionalMoment"]""" },
        { "add", """["duration", 1]""" },
        { "add", """["marker", 1]""" },
        { "subtract", """[1, "moment"]""" },
        { "subtract", """["count"]""" },
        { "divide", """["count", 2, 3]""" },
        { "divide", """["day", 2]""" },
        { "subtract", """["count", { "literal": true }]""" },
    };

    [Theory]
    [MemberData(nameof(NoNumbers))]
    public async Task Arithmetic_over_what_is_no_number_is_refused_when_bound_and_explain_says_so(string op, string operands)
    {
        var client = await EdgeCorpus.ClientAsync();
        var pipeline = Totals($$""" "r": { "max": { "{{op}}": {{operands}} } } """);

        var run = await client.SendAsync(Entity, pipeline);
        var error = run.ShouldRefuse("INVALID_AGGREGATE_ARGUMENT", 400);

        error["stage"]!.GetValue<int>().Should().Be(0);
        error["params"]!["operator"]!.GetValue<string>().Should().Be(op);

        var explained = await client.ExplainAsync($$"""{ "entityType": "{{Entity}}", "pipeline": {{pipeline}} }""");

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body["errors"]!.AsArray().Select(each => each!["code"]!.GetValue<string>()).Should().Contain("INVALID_AGGREGATE_ARGUMENT");
    }

    [Fact]
    public async Task A_date_is_moved_by_a_number_and_one_date_less_another_is_the_milliseconds_between_them()
    {
        var client = await EdgeCorpus.ClientAsync();
        var one = EdgeCorpus.Stored().First(row => row.TryGetValue("OptionalMoment", out var moment) && moment is BsonDateTime && row["Moment"] != moment && row["Moment"].ToUniversalTime().Year is > 1 and < 9999 && moment.ToUniversalTime().Year is > 1 and < 9999);
        var answer = await client.SendAsync(Entity, $$"""
            [{ "match": { "id": "{{EdgeCorpus.WireId(one)}}" } },
             { "group": { "by": [], "fields": {
                 "between": { "max": { "subtract": ["moment", "optionalMoment"] } },
                 "later": { "max": { "add": ["moment", 60000] } },
                 "earlier": { "max": { "subtract": ["moment", 60000] } } } } }]
            """);

        answer.ShouldBeOk();

        var totals = answer.Items.Single()!.AsObject();
        var moment = one["Moment"].ToUniversalTime();

        totals["between"]!.GetValue<long>().Should().Be((long)(moment - one["OptionalMoment"].ToUniversalTime()).TotalMilliseconds);
        DateTime.Parse(totals["later"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).Should().Be(moment.AddMinutes(1));
        DateTime.Parse(totals["earlier"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).Should().Be(moment.AddMinutes(-1));
    }

    /// <summary>What the kinds do not say is the rows' to say: a member the model does not describe that holds text is refused when the request runs, with a code.</summary>
    [Fact]
    public async Task A_row_that_holds_no_number_where_the_kinds_allow_one_is_a_coded_refusal()
    {
        var client = await EdgeCorpus.ClientAsync();
        var answer = await client.SendAsync(Entity, Totals(""" "r": { "sum": { "add": [{ "coalesce": ["name", 1] }, 1] } } """));

        answer.StatusCode.Should().Be(422, answer.ToString());
        answer.Type.Should().Be("not_executable");
        answer.ErrorCodes.Should().Equal("INVALID_AGGREGATE_ARGUMENT");
    }
}
