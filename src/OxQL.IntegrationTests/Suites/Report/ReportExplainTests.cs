using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The report scenarios through explain on the fleet (DESIGN §2, §4): each finished request binds
/// (<c>valid: true</c>) with every stage <c>ok</c>, the executor, phase and owner DESIGN names for
/// each join, and every part at an owner checked at its internal explain (the fleet's remote client
/// answers it in process), so the answer is complete and every alias has its type; every prefix of
/// every scenario, which is what the studio explains while the query is built, is answered the same
/// way through the fleet's hosts; and a continued stage the owner cannot bind is <c>valid: false</c>
/// with the owner's error mapped to the caller's stage.
/// Explain runs through <see cref="ReportExplain"/> (the route, as the studio calls it).
/// </summary>
[Trait("Category", "Integration")]
public class ReportExplainTests
{
    public static TheoryData<string> Scenarios() => [.. ReportScenarios.Ids];

    public static TheoryData<string, int> Prefixes()
    {
        var prefixes = new TheoryData<string, int>();

        foreach (var id in ReportScenarios.Ids)
            for (var stages = 0; stages < ReportScenarios.Request(id)["pipeline"]!.AsArray().Count; stages++)
                prefixes.Add(id, stages);

        return prefixes;
    }

    private static async Task<JsonObject> ExplainAsync(JsonObject body)
    {
        var query = body["query"] as JsonObject ?? body;
        var answer = await ReportExplain.ExplainAsync(await ReportScenarios.ClientAsync(query), body);

        answer.StatusCode.Should().Be(200, answer.ToString());

        return answer.Body!.AsObject();
    }

    /// <summary>The finished request explained with its plan, as the explain view of a studio asks it.</summary>
    private static async Task<JsonObject> ValidAsync(string id)
    {
        var answer = await ExplainAsync(new JsonObject { ["query"] = ReportScenarios.Request(id), ["include"] = new JsonArray("shape", "notes", "plan") });

        answer["valid"]!.GetValue<bool>().Should().BeTrue(answer["errors"]!.ToJsonString());

        return answer;
    }

    private static JsonObject Stage(JsonObject answer, int index) =>
        answer["stages"]!.AsArray().Select(stage => stage!.AsObject()).Single(stage => stage["index"]!.GetValue<int>() == index);

    private static JsonObject Alias(JsonObject answer, string alias) => answer["aliases"]![alias]!.AsObject();

    private static string? Text(JsonNode? node, string path) => Json.At(node, path)?.GetValue<string>();

    private static IReadOnlyList<string> CodesOf(JsonObject answer, string member) =>
        answer[member]!.AsArray().Select(entry => entry!["code"]!.GetValue<string>()).ToList();

    /// <summary>The (executor, phase, owner service) of a stage.</summary>
    private static (string? Executor, string? Phase, string? Owner) Placement(JsonObject answer, int index)
    {
        var placement = Stage(answer, index)["placement"];

        return (Text(placement, "executor"), Text(placement, "phase"),
            placement?["owner"] is JsonValue owner ? Text(answer["owners"]![owner.GetValue<int>()], "service") : null);
    }

    /// <summary>The owner queries a keyed stage sends, by target.</summary>
    private static Dictionary<string, JsonObject> Queries(JsonObject answer, int stage) =>
        answer["owners"]!.AsArray().SelectMany(owner => owner!["queries"]?.AsArray().Select(query => query!.AsObject()) ?? [])
            .Where(query => query["stage"]!.GetValue<int>() == stage)
            .ToDictionary(query => Text(query, "target")!, query => query["query"]!.AsObject());

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task E01_every_scenario_binds_with_every_stage_ok_every_owner_part_checked_and_every_alias_typed(string id)
    {
        var request = ReportScenarios.Request(id);
        var answer = await ValidAsync(id);

        answer["errors"]!.AsArray().Should().BeEmpty();
        answer["stages"]!.AsArray().Select(stage => Text(stage, "status")).Should().OnlyContain(status => status == "ok");
        answer["stages"]!.AsArray().Should().HaveCount(request["pipeline"]!.AsArray().Count);
        CodesOf(answer, "notes").Should().NotContain([Notes.RemoteUnchecked, Notes.ExplainLimit, Notes.ExplainTrimmed], "every owner explains in process, within the limits of one explain");
        answer["cache"]!["complete"]!.GetValue<bool>().Should().BeTrue();
        answer["result"]!["columns"]!.AsArray().Should().NotBeEmpty();
        answer["plan"]!.AsObject().Select(member => member.Key).Should().Contain(["bound", "stages"]);
        answer.ContainsKey("advisory").Should().BeFalse("the index advisory is opt-in");

        foreach (var (alias, described) in answer["aliases"]!.AsObject())
        {
            described!["complete"]!.GetValue<bool>().Should().BeTrue(alias);
            described["type"].Should().NotBeNull($"'{alias}' has a type");
        }

        if (request["strict"]?.GetValue<bool>() == true)
            CodesOf(answer, "notes").Should().Contain(Notes.ReportPage, "a strict request is a report");
    }

    [Fact]
    public async Task E02_A1_joins_the_erp_line_inline_before_the_page_and_the_source_line_at_transport_after_it_through_both_logistics_targets()
    {
        var answer = await ValidAsync("A1");

        Placement(answer, 3).Should().Be(("inline", "beforePage", null));
        Placement(answer, 4).Should().Be(("keyed-remote", "afterPage", "transport"));

        var sourceLine = Alias(answer, "sourceLine");
        var @case = sourceLine["reference"]!["cases"]!.AsArray().Single()!;

        Text(@case, "when.path").Should().Be("type");
        @case["when"]!["equals"]!.AsArray().Select(value => value!.GetValue<string>()).Should().Equal("logistics");
        @case["targets"]!.AsArray().Select(target => $"{Text(target, "entity")}#{Text(target, "item")}")
            .Should().Equal("transport.shipment#billingLines", "transport.tour#billingLines");
        sourceLine["targets"]!.AsArray().Select(target => Text(target, "target"))
            .Should().Equal("transport.shipment#billingLines", "transport.tour#billingLines");
        sourceLine["type"]!.GetValue<string>().Should().Be("u:sourceLine");
        answer["types"]!["u:sourceLine"]!["of"]!.AsArray().Select(type => type!.GetValue<string>())
            .Should().Equal("t:transport.shipment#billingLines", "t:transport.tour#billingLines");
        Stage(answer, 4)["creates"]!.AsArray().Select(created => created!.GetValue<string>()).Should().Equal("sourceLine", "sourceParent");
        Alias(answer, "sourceParent")["parentOf"]!.GetValue<string>().Should().Be("sourceLine");

        answer["notes"]!.AsArray().Should().Contain(note => Text(note, "code") == Notes.MissingPolicy && note!["stage"]!.GetValue<int>() == 4
            && note["params"]!["onMissing"]!.GetValue<string>() == "refuse" && note["params"]!["strict"]!.GetValue<bool>(), "strict refuses a missing source line");
        answer["notes"]!.AsArray().Should().Contain(note => Text(note, "code") == Notes.ReportPage && note!["params"]!["limit"]!.GetValue<int>() == 5000);
        answer["result"]!["columns"]!.AsArray().Select(column => Text(column, "path"))
            .Should().Contain(["position", "sourceLine.id", "sourceParent.entity", "sourceParent.id", "sourceParent.shipmentNumber", "sourceParent.number"]);
        answer["result"]!["columns"]!.AsArray().Single(column => Text(column, "path") == "sourceLine.id")!["present"]!.GetValue<string>().Should().Be(ExplainColumn.IfJoined);
        answer["aliases"]!.AsObject().Where(alias => alias.Value!["outcome"] is not null).Select(alias => alias.Key).Should().Equal(["erpLine", "sourceLine"], "each join that may lose data says its outcomes on its alias");
    }

    [Fact]
    public async Task E03_A2a_runs_the_latest_attempt_lookup_inline_at_transport()
    {
        var answer = await ValidAsync("A2");

        Placement(answer, 1).Should().Be(("inline", "afterPage", null));
        Stage(answer, 1)["creates"]!.AsArray().Single()!.GetValue<string>().Should().Be("lastAttempt");
        Alias(answer, "lastAttempt")["entities"]!.AsArray().Single()!.GetValue<string>().Should().Be("transport.delivery_attempt");
        Alias(answer, "lastAttempt")["type"]!.GetValue<string>().Should().Be("t:transport.delivery_attempt");
        answer["owners"]!.AsArray().Should().BeEmpty();
        answer["result"]!["columns"]!.AsArray().Select(column => Text(column, "path"))
            .Should().Equal("id", "shipmentNumber", "lastAttempt.id", "lastAttempt.dateTime", "lastAttempt.status", "lastAttempt.text");
    }

    [Fact]
    public async Task E04_A2b_continues_the_lookup_at_transport_for_shipment_targets_and_marks_it_not_applicable_for_tour_targets()
    {
        var answer = await ValidAsync("A2b");

        Placement(answer, 5).Should().Be(("continued", "owner", "transport"));
        Alias(answer, "lastAttempt")["continuedFrom"]!.ToJsonString().Should().Be("""{"alias":"sourceParent","target":"transport.shipment"}""");
        Alias(answer, "lastAttempt")["type"]!.GetValue<string>().Should().Be("t:transport.delivery_attempt", "the owner says what the continued alias holds");

        var targets = Alias(answer, "sourceLine")["targets"]!.AsArray().ToDictionary(target => Text(target, "target")!, target => target!.AsObject());

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
        Alias(answer, "recipientContact")["reference"]!["cases"]!.AsArray().Single()!["targets"]!.AsArray().Single()!["entity"]!.GetValue<string>().Should().Be("directory.contact");
        Alias(answer, "recipientContact")["type"]!.GetValue<string>().Should().Be("t:directory.contact");
        answer["cache"]!["dependsOn"]!.AsArray().Select(service => service!.GetValue<string>()).Should().Equal("directory");
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

        Alias(answer, "sourceLine")["targets"]!.AsArray().Single(target => Text(target, "target") == "transport.shipment#billingLines")!["continued"]!
            .AsArray().Select(index => index!.GetValue<int>()).Should().Equal(At("lastAttempt"));
        Alias(answer, "lineShipment")["targets"]!.AsArray().Single()!["continued"]!.AsArray().Select(index => index!.GetValue<int>())
            .Should().Equal(At("deliveringTour"), At("tourVehicle"), At("driver"));
        Alias(answer, "tourVehicle")["continuedFrom"]!["alias"]!.GetValue<string>().Should().Be("deliveringTour");
        Alias(answer, "tourVehicle")["type"]!.GetValue<string>().Should().Be("t:fleet.vehicle", "transport's answer carries what its own owner said");
        Alias(answer, "driver")["type"]!.GetValue<string>().Should().Be("t:staff.employee");

        var owner = Queries(answer, At("lineShipment")).Values.Single();

        owner["entityType"]!.GetValue<string>().Should().Be("transport.shipment");
        owner["pipeline"]!.ToJsonString().Should().Contain("deliveringTour").And.Contain("tourVehicle").And.Contain("driver");
        Json.At(owner["pipeline"]![0], "match.id.in")!.AsArray().Select(key => key!.GetValue<string>()).Should().Equal(["…"], "keys are elided");
        owner["pipeline"]!.ToJsonString().Should().NotContain("lineTour", "the forwarded query holds only the stages continued at the owner");

        // The owners the query reaches: three asked by ledger, two reached through transport.
        answer["owners"]!.AsArray().Where(each => each!["via"] is null).Select(each => Text(each, "service")).Should().BeEquivalentTo(["directory", "staff", "transport"]);
        answer["owners"]!.AsArray().Where(each => each!["via"] is not null).Select(each => $"{Text(each, "via")}>{Text(each, "service")}").Should().BeEquivalentTo(["transport>fleet", "transport>staff"]);
        answer["cache"]!["dependsOn"]!.AsArray().Select(service => service!.GetValue<string>()).Should().Equal("directory", "fleet", "staff", "transport");
    }

    [Theory]
    [MemberData(nameof(Prefixes))]
    public async Task E07_every_prefix_of_every_scenario_is_answered_through_the_fleets_hosts_complete(string id, int stages)
    {
        var request = ReportScenarios.Request(id);
        var pipeline = request["pipeline"]!.AsArray();

        while (pipeline.Count > stages)
            pipeline.RemoveAt(pipeline.Count - 1);

        var answer = await ExplainAsync(request);

        answer["valid"]!.GetValue<bool>().Should().BeTrue(answer["errors"]!.ToJsonString());
        answer["cache"]!["complete"]!.GetValue<bool>().Should().BeTrue(answer["notes"]!.ToJsonString());
        CodesOf(answer, "notes").Should().NotContain([Notes.RemoteUnchecked, Notes.ExplainLimit], "every owner explains in process");
        answer["stages"]!.AsArray().Should().HaveCount(stages);
        answer.ContainsKey("plan").Should().BeFalse("the plan is opt-in");

        foreach (var stage in answer["stages"]!.AsArray())
            foreach (var (root, type) in stage!["shape"]!["roots"]!.AsObject())
                type.Should().NotBeNull($"stage {stage["index"]}: '{root}' has a type");
    }

    [Fact]
    public async Task E08_a_continued_stage_the_owner_cannot_bind_is_valid_false_with_the_owners_error_at_the_callers_stage()
    {
        var request = ReportFixtureExport.InvalidRequest(ReportScenarios.Request("A2b"));
        var lookup = ReportScenarios.IndexOf(request, "lastAttempt");

        var answer = await ExplainAsync(new JsonObject { ["query"] = request, ["include"] = new JsonArray("shape", "notes", "plan") });

        answer["valid"]!.GetValue<bool>().Should().BeFalse();
        var error = answer["errors"]!.AsArray().Single()!.AsObject();

        Text(error, "code").Should().Be(Codes.UnknownPath);
        error["stage"]!.GetValue<int>().Should().Be(lookup);
        Text(error, "params.owner.service").Should().Be("transport");
        Stage(answer, lookup)["status"]!.GetValue<string>().Should().Be("error");
        answer.ContainsKey("plan").Should().BeFalse();
    }
}
