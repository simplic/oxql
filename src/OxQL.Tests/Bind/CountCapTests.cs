using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The number form of <c>page.includeTotalCount</c>: the request's own count cap, under the
/// host's, contract 2 only; the boolean form is unchanged.
/// </summary>
public class CountCapTests
{
    private const string Order = "probe.order";
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static string Page(string includeTotalCount) => $$"""[{ "page": { "limit": 5, "includeTotalCount": {{includeTotalCount}} } }]""";

    private static PageStage Parse(string includeTotalCount) =>
        BindHost.Request(Order, Page(includeTotalCount)).Pipeline![0].Page!;

    // ---- the wire form ---------------------------------------------------------------------

    [Fact]
    public void A_number_reads_as_a_count_with_a_cap_and_a_boolean_as_before()
    {
        var capped = Parse("500");

        capped.IncludeTotalCount.Should().BeTrue();
        capped.TotalCountCap.Should().Be(500);

        var counted = Parse("true");

        counted.IncludeTotalCount.Should().BeTrue();
        counted.TotalCountCap.Should().BeNull();

        Parse("false").IncludeTotalCount.Should().BeFalse();
        Parse("\"500\"").IncludeTotalCount.Should().BeFalse("a string is not a count request, as before");
    }

    [Fact]
    public void A_number_beyond_an_int_clamps_and_a_fraction_reads_as_no_cap_at_all()
    {
        Parse("10000000000").TotalCountCap.Should().Be(int.MaxValue);
        Parse("1.5").TotalCountCap.Should().Be(0);
        Parse("-3").TotalCountCap.Should().Be(-3);
    }

    [Fact]
    public void The_cap_survives_a_round_trip_through_the_converter()
    {
        var stage = new PageStage { Limit = 5, IncludeTotalCount = true, TotalCountCap = 250 };

        JsonSerializer.Serialize(stage, BindHost.Json).Should().Be("""{"limit":5,"includeTotalCount":250}""");
        JsonSerializer.Serialize(stage with { TotalCountCap = null }, BindHost.Json).Should().Be("""{"limit":5,"includeTotalCount":true}""");
    }

    // ---- binding ---------------------------------------------------------------------------

    [Fact]
    public async Task A_cap_binds_under_the_hosts_cap_and_the_boolean_form_binds_as_before()
    {
        var capped = await BindHost.BoundAsync(BindHost.Probe, Order, Page("500"));

        capped.Page.IncludeTotalCount.Should().BeTrue();
        capped.Page.CountCap.Should().Be(500);

        var clamped = await BindHost.BoundAsync(BindHost.Probe, Order, Page("10000000000"));

        clamped.Page.CountCap.Should().Be(100_000, "the host's cap bounds the request's");

        var hostCap = await BindHost.BoundAsync(BindHost.Probe, Order, Page("500"), BindHost.Context(BindHost.Options(options => options.Limits.CountCap = 200)));

        hostCap.Page.CountCap.Should().Be(200);

        var counted = await BindHost.BoundAsync(BindHost.Probe, Order, Page("true"));

        counted.Page.IncludeTotalCount.Should().BeTrue();
        counted.Page.CountCap.Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public async Task A_cap_that_is_not_a_positive_integer_is_refused(string cap)
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, Page(cap), Codes.InvalidPageLimit);

        error.Stage.Should().Be(0);
        error.Message.Should().Contain("positive integer");
    }

    [Fact]
    public async Task Contract_1_keeps_the_boolean_form_only()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, Page("500"), Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));

        error.Stage.Should().Be(0);
        error.Message.Should().Contain("contract 2");

        var counted = await BindHost.BoundAsync(BindHost.Probe, Order, Page("true"), BindHost.Context(contract: 1));

        counted.Page.IncludeTotalCount.Should().BeTrue();
        counted.Page.CountCap.Should().BeNull();
    }

    [Fact]
    public async Task The_canonical_form_carries_the_cap_and_the_fingerprint_does_not()
    {
        var capped = await BindHost.BoundAsync(BindHost.Probe, Order, Page("500"));
        var counted = await BindHost.BoundAsync(BindHost.Probe, Order, Page("true"));

        capped.Canonical.Should().Contain("\"includeTotalCount\":500");
        counted.Canonical.Should().Contain("\"includeTotalCount\":true");
        capped.Fingerprint.Should().Be(counted.Fingerprint, "the page is not part of the fingerprint, so a cursor continues a walk whatever the count asks");
    }

    // ---- the count pipeline and the answer -------------------------------------------------

    [Fact]
    public async Task The_count_pipeline_stops_at_the_requests_cap()
    {
        var capped = MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Order, Page("500")), Options);

        capped.CountCap.Should().Be(500);
        capped.CountStages![^2].ShouldBeBson(BsonDocument.Parse("{ $limit: 501 }"));

        var counted = MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Order, Page("true")), Options);

        counted.CountCap.Should().Be(100_000);
        counted.CountStages![^2].ShouldBeBson(BsonDocument.Parse("{ $limit: 100001 }"));
    }

    [Fact]
    public async Task The_answer_is_capped_at_the_requests_cap()
    {
        var runner = new FakeAggregateRunner { Count = 501 };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());

        var capped = await Success(engine, Page("500"));

        capped.PageInfo.TotalCount.Should().Be(500);
        capped.PageInfo.TotalCountCapped.Should().BeTrue();
        capped.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.TotalCountCapped).Which.Params!["cap"].Should().Be(500);

        runner.Count = 300;

        var under = await Success(engine, Page("500"));

        under.PageInfo.TotalCount.Should().Be(300);
        under.PageInfo.TotalCountCapped.Should().BeFalse();
        under.Diagnostics.Should().BeNull();
    }

    [Fact]
    public async Task Explain_shows_the_cap_in_the_bound_form_and_in_the_count_pipeline()
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options());
        var outcome = await engine.ExplainAsync(BindHost.Request(Order, Page("500")), BindHost.Context());
        var explain = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        explain.Bound["page"]!["includeTotalCount"]!.GetValue<int>().Should().Be(500);
        explain.Count![^2]!["$limit"]!.GetValue<int>().Should().Be(501);
    }

    private static async Task<QueryResult> Success(MongoQueryEngine engine, string pipeline)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }
}
