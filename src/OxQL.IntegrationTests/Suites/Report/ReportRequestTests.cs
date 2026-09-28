using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The studio's scenario requests (the request files its describe-plan spec writes) are the
/// requests DESIGN §2 writes, structurally: parsed JSON, member order not significant. A2b is A1
/// with DESIGN's lookup appended after the second resolve (and its alias projected); A4, for which
/// DESIGN gives no JSON, is A5 without A1/A2b/A3's stages and their projected aliases.
/// </summary>
[Trait("Category", "Integration")]
public class ReportRequestTests
{
    private static JsonObject Design(string json) => JsonNode.Parse(json)!.AsObject();

    private static void ShouldEqual(JsonNode actual, JsonNode expected) =>
        JsonNode.DeepEquals(actual, expected).Should().BeTrue($"the studio's request\n{actual.ToJsonString()}\nequals DESIGN §2's\n{expected.ToJsonString()}");

    [Fact]
    public void R01_A1_the_billing_line_chain_is_DESIGNs() => ShouldEqual(ReportScenarios.Request("A1"), Design(ReportScenarios.DesignA1));

    [Fact]
    public void R02_A2a_the_latest_entry_on_transport_is_DESIGNs() => ShouldEqual(ReportScenarios.Request("A2"), Design(ReportScenarios.DesignA2a));

    [Fact]
    public void R03_A2b_is_A1_with_the_latest_attempt_appended_after_the_second_resolve()
    {
        var expected = Design(ReportScenarios.DesignA1);
        var pipeline = expected["pipeline"]!.AsArray();

        pipeline.Insert(ReportScenarios.IndexOf(expected, "sourceLine") + 1, JsonNode.Parse(ReportScenarios.DesignA2bStage));
        pipeline.Single(stage => stage!.AsObject().ContainsKey("project"))!["project"]!["lastAttempt"] = 1;

        ShouldEqual(ReportScenarios.Request("A2b"), expected);
    }

    [Fact]
    public void R04_A3_the_email_of_the_contact_is_DESIGNs() => ShouldEqual(ReportScenarios.Request("A3"), Design(ReportScenarios.DesignA3));

    [Fact]
    public void R05_A4_the_single_needs_are_A5_without_the_stages_of_A1_A2b_and_A3()
    {
        string[] dropped = ["recipientContact", "erpLine", "sourceLine", "lastAttempt"];
        var expected = Design(ReportScenarios.DesignA5);
        var pipeline = expected["pipeline"]!.AsArray();

        foreach (var alias in dropped)
            pipeline.RemoveAt(ReportScenarios.IndexOf(expected, alias));

        var project = pipeline.Single(stage => stage!.AsObject().ContainsKey("project"))!["project"]!.AsObject();

        foreach (var member in dropped.Append("sourceParent"))
            project.Remove(member);

        ShouldEqual(ReportScenarios.Request("A4"), expected);
    }

    [Fact]
    public void R06_A5_the_full_invoice_is_DESIGNs() => ShouldEqual(ReportScenarios.Request("A5"), Design(ReportScenarios.DesignA5));

    [Fact]
    public void R07_the_describe_plan_walks_every_scenario_and_each_scenarios_last_step_explains_its_request()
    {
        var plan = ReportScenarios.Plan();

        plan["scenarios"]!.AsArray().Select(scenario => scenario!["id"]!.GetValue<string>()).Should().Equal(ReportScenarios.Ids);

        foreach (var id in ReportScenarios.Ids)
        {
            var request = ReportScenarios.Request(id);
            var queries = ReportScenarios.Steps(plan, id).Select(step => step["envelope"]!["query"]!).ToList();

            queries.Should().Contain(query => JsonNode.DeepEquals(query, request), $"a step of {id} explains the finished request");
        }
    }
}
