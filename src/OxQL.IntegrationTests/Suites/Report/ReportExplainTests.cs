using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The report scenarios through explain on the fleet (DESIGN §2, §4): each finished request binds
/// (<c>valid: true</c>) with every step <c>ok</c>, the executor, phase and owner DESIGN names for
/// each join, and every continued part checked at its owner's internal explain (the fleet's remote
/// client answers it in process), so no <c>REMOTE_UNCHECKED</c> note is left; every envelope of the
/// studio's describe plan is answered the same way through the fleet's hosts; and a continued stage
/// the owner cannot bind is <c>valid: false</c> with the owner's error mapped to the caller's stage.
/// Explain runs through <see cref="ReportExplain"/> (the route's service, see there why not the route).
/// </summary>
[Trait("Category", "Integration")]
public class ReportExplainTests
{
    public static TheoryData<string> Scenarios() => [.. ReportScenarios.Ids];

    public static TheoryData<string> PlanSteps()
    {
        var steps = new TheoryData<string>();

        foreach (var id in ReportScenarios.Ids)
            foreach (var step in ReportScenarios.Steps(ReportScenarios.Plan(), id))
                steps.Add(step["id"]!.GetValue<string>());

        return steps;
    }

    private static async Task<JsonObject> ExplainAsync(JsonObject body)
    {
        var query = body["query"] as JsonObject ?? body;
        var answer = await ReportExplain.ExplainAsync(await ReportScenarios.ClientAsync(query), body);

        answer.StatusCode.Should().Be(200, answer.ToString());

        return answer.Body!.AsObject();
    }

    private static async Task<JsonObject> ValidAsync(string id)
    {
        var answer = await ExplainAsync(ReportScenarios.Request(id));

        answer["valid"]!.GetValue<bool>().Should().BeTrue(answer["errors"]!.ToJsonString());

        return answer;
    }

    private static JsonObject Step(JsonObject answer, int index) =>
        answer["steps"]!.AsArray().Select(step => step!.AsObject()).Single(step => step["index"]!.GetValue<int>() == index);

    private static string? Text(JsonNode? node, string path) => Json.At(node, path)?.GetValue<string>();

    private static IReadOnlyList<string> CodesOf(JsonObject answer, string member) =>
        answer[member]!.AsArray().Select(entry => entry!["code"]!.GetValue<string>()).ToList();

    /// <summary>The (executor, phase, owner service) of a step.</summary>
    private static (string? Executor, string? Phase, string? Owner) Placement(JsonObject answer, int index)
    {
        var step = Step(answer, index);
        return (Text(step, "executor"), Text(step, "phase"), Text(step, "owner.service"));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task E01_every_scenario_binds_with_every_step_ok_and_every_owner_part_checked(string id)
    {
        var request = ReportScenarios.Request(id);
        var answer = await ValidAsync(id);

        answer["errors"]!.AsArray().Should().BeEmpty();
        answer["steps"]!.AsArray().Select(step => Text(step, "status")).Should().OnlyContain(status => status == "ok");
        answer["steps"]!.AsArray().Should().HaveCount(request["pipeline"]!.AsArray().Count);
        CodesOf(answer, "notes").Should().NotContain(Notes.RemoteUnchecked, "every owner explains in process");
        answer["result"]!["columns"]!.AsArray().Should().NotBeEmpty();
        answer.Select(member => member.Key).Should().Contain(["bound", "stages"]);
        answer.ContainsKey("advisory").Should().BeFalse("the index advisory is opt-in");

        if (request["strict"]?.GetValue<bool>() == true)
            CodesOf(answer, "notes").Should().Contain(Notes.ReportPage, "a strict request is a report");
    }

    [Fact]
    public async Task E02_A1_joins_the_erp_line_inline_before_the_page_and_the_source_line_at_transport_after_it_through_both_logistics_targets()
    {
        var answer = await ValidAsync("A1");

        Placement(answer, 3).Should().Be(("inline", "beforePage", null));
        Placement(answer, 4).Should().Be(("keyed-remote", "afterPage", "transport"));

        var sourceLine = Step(answer, 4);
        var @case = sourceLine["reference"]!["cases"]!.AsArray().Single()!;

        Text(@case, "when.path").Should().Be("type");
        @case["when"]!["equals"]!.AsArray().Select(value => value!.GetValue<string>()).Should().Equal("logistics");
        @case["targets"]!.AsArray().Select(target => $"{Text(target, "entity")}#{Text(target, "item")}")
            .Should().Equal("transport.shipment#billingLines", "transport.tour#billingLines");
        sourceLine["owner"]!["targets"]!.AsArray().Select(target => Text(target, "target"))
            .Should().Equal("transport.shipment#billingLines", "transport.tour#billingLines");
        sourceLine["creates"]!.AsArray().Select(created => Text(created, "alias")).Should().Equal("sourceLine", "sourceParent");

        answer["notes"]!.AsArray().Should().Contain(note => Text(note, "code") == Notes.MissingPolicy && note!["stage"]!.GetValue<int>() == 4
            && note["params"]!["onMissing"]!.GetValue<string>() == "refuse" && note["params"]!["strict"]!.GetValue<bool>(), "strict refuses a missing source line");
        answer["notes"]!.AsArray().Should().Contain(note => Text(note, "code") == Notes.ReportPage && note!["params"]!["limit"]!.GetValue<int>() == 5000);
        answer["result"]!["columns"]!.AsArray().Select(column => Text(column, "path"))
            .Should().Contain(["position", "sourceLine.id", "sourceParent.entity", "sourceParent.id", "sourceParent.shipmentNumber", "sourceParent.number"]);
    }

    [Fact]
    public async Task E03_A2a_runs_the_latest_attempt_lookup_inline_at_transport()
    {
        var answer = await ValidAsync("A2");

        Placement(answer, 1).Should().Be(("inline", "afterPage", null));
        Step(answer, 1)["creates"]!.AsArray().Single()!["entity"]!.GetValue<string>().Should().Be("transport.delivery_attempt");
        answer["result"]!["columns"]!.AsArray().Select(column => Text(column, "path"))
            .Should().Equal("id", "shipmentNumber", "lastAttempt.id", "lastAttempt.dateTime", "lastAttempt.status", "lastAttempt.text");
    }

    [Fact]
    public async Task E04_A2b_continues_the_lookup_at_transport_for_shipment_targets_and_marks_it_not_applicable_for_tour_targets()
    {
        var answer = await ValidAsync("A2b");
        var anchor = Step(answer, 4);

        anchor["continued"]!.AsArray().Select(entry => (entry!["index"]!.GetValue<int>(), Text(entry, "forTarget"))).Should().Equal((5, "transport.shipment"));
        Placement(answer, 5).Should().Be(("continued", "owner", "transport"));

        var targets = anchor["owner"]!["targets"]!.AsArray().ToDictionary(target => Text(target, "target")!, target => target!.AsObject());

        targets["transport.shipment#billingLines"]["continued"]!.AsArray().Select(index => index!.GetValue<int>()).Should().Equal(5);
        targets["transport.shipment#billingLines"]["notApplicable"]!.AsArray().Should().BeEmpty();
        targets["transport.tour#billingLines"]["continued"]!.AsArray().Should().BeEmpty();
        targets["transport.tour#billingLines"]["notApplicable"]!.AsArray().Select(index => index!.GetValue<int>()).Should().Equal(5);
    }

    [Fact]
    public async Task E05_A3_resolves_the_recipient_contact_at_directory()
    {
        var answer = await ValidAsync("A3");

        Placement(answer, 1).Should().Be(("keyed-remote", "afterPage", "directory"));
        Step(answer, 1)["reference"]!["cases"]!.AsArray().Single()!["targets"]!.AsArray().Single()!["entity"]!.GetValue<string>().Should().Be("directory.contact");
        answer["result"]!["columns"]!.AsArray().Select(column => Text(column, "path"))
            .Should().Contain(["recipientContact.primaryEmailAddress.email", "recipientContact.primaryPhoneNumber.number", "recipientContact.address.companyName"]);
    }

    [Fact]
    public async Task E06_A5_binds_six_resolves_at_ledger_and_continues_the_latest_attempt_and_the_tour_chain_at_transport()
    {
        var answer = await ValidAsync("A5");
        var request = ReportScenarios.Request("A5");
        int At(string alias) => ReportScenarios.IndexOf(request, alias);

        Placement(answer, At("recipientContact")).Should().Be(("keyed-remote", "afterPage", "directory"));
        Placement(answer, At("clerk")).Should().Be(("keyed-remote", "afterPage", "staff"));
        Placement(answer, At("erpLine")).Should().Be(("inline", "beforePage", null));
        Placement(answer, At("sourceLine")).Should().Be(("keyed-remote", "afterPage", "transport"));
        Placement(answer, At("lineShipment")).Should().Be(("keyed-remote", "afterPage", "transport"));
        Placement(answer, At("lineTour")).Should().Be(("keyed-remote", "afterPage", "transport"));

        foreach (var alias in new[] { "lastAttempt", "deliveringTour", "tourVehicle", "driver" })
            Placement(answer, At(alias)).Should().Be(("continued", "owner", "transport"), alias);

        Step(answer, At("sourceLine"))["continued"]!.AsArray().Select(entry => (entry!["index"]!.GetValue<int>(), Text(entry, "forTarget")))
            .Should().Equal((At("lastAttempt"), "transport.shipment"));
        Step(answer, At("lineShipment"))["continued"]!.AsArray().Select(entry => entry!["index"]!.GetValue<int>())
            .Should().Equal(At("deliveringTour"), At("tourVehicle"), At("driver"));

        var owner = Step(answer, At("lineShipment"))["owner"]!["query"]!.AsObject();

        owner["entityType"]!.GetValue<string>().Should().Be("transport.shipment");
        owner["pipeline"]!.ToJsonString().Should().Contain("deliveringTour").And.Contain("tourVehicle").And.Contain("driver");
        Json.At(owner["pipeline"]![0], "match.id.in")!.AsArray().Select(key => key!.GetValue<string>()).Should().Equal(["…"], "keys are elided");
        owner["pipeline"]!.ToJsonString().Should().NotContain("lineTour", "the forwarded query holds only the stages continued at the owner");
    }

    [Theory]
    [MemberData(nameof(PlanSteps))]
    public async Task E07_every_envelope_of_the_describe_plan_is_answered_through_the_fleets_hosts_with_every_owner_part_checked(string id)
    {
        var step = ReportScenarios.Steps(ReportScenarios.Plan(), id.Split('.')[0]).Single(step => step["id"]!.GetValue<string>() == id);
        var envelope = step["envelope"]!.AsObject();
        var answer = await ExplainAsync(envelope);

        answer["valid"]!.GetValue<bool>().Should().BeTrue(answer["errors"]!.ToJsonString());
        answer["describe"]!.AsArray().Should().HaveCount(envelope["describe"]!.AsArray().Count);
        CodesOf(answer, "notes").Should().NotContain(Notes.RemoteUnchecked, "every owner explains in process");

        foreach (var described in answer["describe"]!.AsArray().Select(entry => entry!.AsObject()))
        {
            // The plan's one focus ahead of its stage (A5.14 describes lineTour before the stage
            // creating it is in the envelope; the studio's plan fixes it in F18) answers so.
            if (id == "A5.14" && Text(described, "id") == "focus" && described["error"] is JsonObject error)
            {
                Text(error, "code").Should().Be(Codes.UnknownPath);
                continue;
            }

            described["error"].Should().BeNull(described.ToJsonString());
            described["children"]!.AsArray().Should().NotBeEmpty(described.ToJsonString());
        }
    }

    [Fact]
    public async Task E08_a_continued_stage_the_owner_cannot_bind_is_valid_false_with_the_owners_error_at_the_callers_stage()
    {
        var request = ReportFixtureExport.InvalidRequest(ReportScenarios.Request("A2b"));
        var lookup = ReportScenarios.IndexOf(request, "lastAttempt");

        var answer = await ExplainAsync(request);

        answer["valid"]!.GetValue<bool>().Should().BeFalse();
        var error = answer["errors"]!.AsArray().Single()!.AsObject();

        Text(error, "code").Should().Be(Codes.UnknownPath);
        error["stage"]!.GetValue<int>().Should().Be(lookup);
        Text(error, "params.owner.service").Should().Be("transport");
        Step(answer, lookup)["status"]!.GetValue<string>().Should().Be("error");
        answer.Select(member => member.Key).Should().NotContain(["bound", "stages"]);
    }
}
