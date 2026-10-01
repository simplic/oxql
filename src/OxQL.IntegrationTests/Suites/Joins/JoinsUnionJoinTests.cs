using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// The union join on the fleet (improvement plan §3.U): <c>resolve { as, byTarget }</c> under the
/// source line of an invoice line, which is a shipment's or a tour's. One stage and one alias where
/// two <c>forTarget</c> stages filled two; the rows equal theirs; branches that reach different
/// entities drop per branch what one does not reach; an owner's refusal names the branch; a union
/// join under an alias a continued stage added is split by that alias's owner; and explain says of
/// each what the run does (improvement plan P12). The rows are the report seeds of organisation R.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsUnionJoinTests
{
    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>The invoice's billing lines with their source line and its owning shipment or tour, then <paramref name="stages"/>.</summary>
    private static string Lines(string stages, string project) => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine" } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
            {{stages}},
            { "project": { {{project}} } },
            { "sort": [ { "position": "asc" } ] }
          ]
        }
        """;

    private static string DeliveringTour => $$"""
        { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first" } }
        """;

    /// <summary>The vehicle of a line as two stages and two aliases: the shipment's delivering tour's, the tour's own.</summary>
    private static string VehiclePair => Lines($$"""
        {{DeliveringTour}},
        { "resolve": { "path": "deliveringTour.resource.id", "as": "shipmentVehicle", "target": "fleet.vehicle", "onMissing": "report" } },
        { "resolve": { "path": "sourceParent.resource.id", "as": "tourVehicle", "forTarget": "{{ReportSeed.Tour}}", "target": "fleet.vehicle", "onMissing": "report" } }
        """,
        """ "position": 1, "sourceParent.id": 1, "shipmentVehicle.matchCode": 1, "shipmentVehicle.registrationPlate.registrationIdentifier": 1, "tourVehicle.matchCode": 1, "tourVehicle.registrationPlate.registrationIdentifier": 1 """);

    /// <summary>The same as one stage and one alias.</summary>
    private static string VehicleUnion => Vehicle(""" "position": 1, "sourceParent.id": 1, "vehicle.matchCode": 1, "vehicle.registrationPlate.registrationIdentifier": 1 """);

    private static string Vehicle(string project, string select = "") => Lines($$"""
        {{DeliveringTour}},
        { "resolve": { "as": "vehicle", "target": "fleet.vehicle", "onMissing": "report"{{select}},
                       "byTarget": { "{{ReportSeed.Shipment}}": "deliveringTour.resource.id", "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } }
        """, project);

    /// <summary>Whatever comes next after the line's owning row: the shipment's first tour, the tour's resource.</summary>
    private static string Next(string project, string select = "") => Lines($$"""
        { "resolve": { "as": "next"{{select}},
                       "byTarget": { "{{ReportSeed.Shipment}}": { "path": "sourceParent.tours.tourId", "elements": "first" }, "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } }
        """, project);

    private static List<JsonObject> Rows(WireAnswer answer) => answer.ShouldBeOk().Items.OfType<JsonObject>().ToList();

    private static bool IsShipmentRow(JsonObject row) => row["sourceParent"]?["entity"]?.GetValue<string>() == ReportSeed.Shipment;

    private static bool IsTourRow(JsonObject row) => row["sourceParent"]?["entity"]?.GetValue<string>() == ReportSeed.Tour;

    // ---- one alias for both branches --------------------------------------------------------------------

    [Fact]
    public async Task A_union_join_fills_one_alias_with_what_two_forTarget_stages_fill_two_with()
    {
        var ledger = await LedgerClient();
        var pair = Rows(await ledger.QueryAsync(VehiclePair));
        var union = Rows(await ledger.QueryAsync(VehicleUnion));

        union.Should().HaveSameCount(pair);
        union.Should().Contain(row => IsShipmentRow(row) && row["vehicle"] is JsonObject, "a shipment line's vehicle comes through its delivering tour");
        union.Should().Contain(row => IsTourRow(row) && row["vehicle"] is JsonObject, "a tour line's vehicle is the tour's own");

        for (var index = 0; index < pair.Count; index++)
        {
            var expected = pair[index]["shipmentVehicle"] ?? pair[index]["tourVehicle"];

            (union[index]["vehicle"]?.ToJsonString()).Should().Be(expected?.ToJsonString(), $"row {index}: the one alias holds what the branch of the row's target resolved");
            union[index]["position"]!.GetValue<int>().Should().Be(pair[index]["position"]!.GetValue<int>());
            union[index].ContainsKey("shipmentVehicle").Should().BeFalse();
        }
    }

    [Fact]
    public async Task Explain_describes_the_union_join_as_one_stage_one_alias_and_a_branch_per_target()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(VehicleUnion);
        var answer = explained.Body!;

        answer["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);

        var stage = answer["stages"]![6]!;
        stage["kind"]!.GetValue<string>().Should().Be("resolve");
        stage["placement"]!["executor"]!.GetValue<string>().Should().Be("continued");
        stage["creates"]!.ToJsonString().Should().Be("""["vehicle"]""");
        stage["reads"]!.AsArray().Select(read => read!["path"]!.GetValue<string>()).Should().Contain(["deliveringTour.resource.id", "sourceParent.resource.id"], "each branch's root, as its owner bound it");

        var vehicle = answer["aliases"]!["vehicle"]!;
        vehicle["node"]!.GetValue<string>().Should().Be("remote");
        vehicle["type"]!.GetValue<string>().Should().Be("t:fleet.vehicle", "both branches reach the one entity");
        vehicle["entities"]!.ToJsonString().Should().Be("""["fleet.vehicle"]""");
        vehicle["continuedFrom"]!.ToJsonString().Should().Be("""{"alias":"sourceLine"}""");
        vehicle["complete"]!.GetValue<bool>().Should().BeTrue();
        vehicle["outcome"]!["values"]!.AsArray().Should().NotBeEmpty();
        vehicle["branches"]!.ToJsonString().Should().Be(
            """[{"anchorTarget":"transport.shipment","path":"deliveringTour.resource.id","entities":["fleet.vehicle"],"heldBy":"transport","status":"ok","types":["t:fleet.vehicle"]},"""
            + """{"anchorTarget":"transport.tour","path":"sourceParent.resource.id","entities":["fleet.vehicle"],"heldBy":"transport","status":"ok","types":["t:fleet.vehicle"]}]""");

        var targets = answer["aliases"]!["sourceLine"]!["targets"]!.AsArray();
        targets.Select(target => (target!["target"]!.GetValue<string>(), target["continued"]!.ToJsonString(), target["notApplicable"]!.ToJsonString())).Should().Equal(
            ("transport.shipment#billingLines", "[5,6]", "[]"), ("transport.tour#billingLines", "[6]", "[5]"));

        answer["result"]!["columns"]!.AsArray().Where(column => column!["root"]!.GetValue<string>() == "vehicle").Select(column => column!["path"]!.GetValue<string>())
            .Should().Equal("vehicle.matchCode", "vehicle.registrationPlate.registrationIdentifier");
        answer["notes"]!.AsArray().Should().Contain(note => note!["code"]!.GetValue<string>() == "MISSING_POLICY" && note["stage"]!.GetValue<int>() == 6);
    }

    // ---- branches that reach different entities -----------------------------------------------------------

    [Fact]
    public async Task A_path_one_branch_does_not_reach_is_dropped_for_that_branch_and_the_alias_carries_it_on_the_other_rows()
    {
        var ledger = await LedgerClient();
        var request = Next(""" "position": 1, "sourceParent.id": 1, "next.number": 1, "next.matchCode": 1 """);
        var answer = await ledger.QueryAsync(request);
        var rows = Rows(answer);

        var shipments = rows.Where(row => IsShipmentRow(row) && row["next"] is JsonObject).ToList();
        var tours = rows.Where(row => IsTourRow(row) && row["next"] is JsonObject).ToList();

        shipments.Should().NotBeEmpty();
        tours.Should().NotBeEmpty();
        shipments.Should().OnlyContain(row => row["next"]!.AsObject().ContainsKey("number") && !row["next"]!.AsObject().ContainsKey("matchCode"), "a shipment line's next is its first tour, which has a number and no match code");
        tours.Should().OnlyContain(row => !row["next"]!.AsObject().ContainsKey("number"), "a tour line's next is its resource, which has no number");
        tours.Should().Contain(row => row["next"]!.AsObject().ContainsKey("matchCode"));

        // The shipment's branch is a join of transport's own: it says at binding that a tour has no match code.
        // The tour's branch is sent on to the resource's owners, which say what they lack as a page reaches them:
        // a drop for that branch, or for one target of the resource (the owner's own union).
        var dropped = answer.Diagnostics.Where(diagnostic => diagnostic["code"]!.GetValue<string>() == "SELECT_PATH_NOT_ON_TARGET").ToList();
        dropped.Should().Contain(diagnostic => diagnostic["stage"]!.GetValue<int>() == 5 && diagnostic["path"]!.GetValue<string>() == "matchCode"
            && diagnostic["params"]!["alias"]!.GetValue<string>() == "next" && diagnostic["params"]!["branch"]!.GetValue<string>() == ReportSeed.Shipment && diagnostic["params"]!["parent"]!.GetValue<bool>() == false);
        dropped.Should().Contain(diagnostic => diagnostic["stage"]!.GetValue<int>() == 5 && diagnostic["path"]!.GetValue<string>() == "number");
        dropped.Should().HaveCount(2);

        var explained = await ledger.ExplainHereAsync(request);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body!["notes"]!.AsArray().Where(note => note!["code"]!.GetValue<string>() == "SELECT_PATH_NOT_ON_TARGET" && note["params"]!["branch"] is not null)
            .Select(note => (note!["stage"]!.GetValue<int>(), note["path"]!.GetValue<string>(), note["params"]!["branch"]!.GetValue<string>()))
            .Should().BeEquivalentTo([(5, "matchCode", ReportSeed.Shipment), (5, "number", ReportSeed.Tour)], "explain says per branch what the run drops per branch");

        var next = explained.Body!["aliases"]!["next"]!;
        next["type"]!.GetValue<string>().Should().Be("u:next", "the branches reach different entities: the alias is their union, by reference");
        explained.Body!["types"]!["u:next"]!["of"]!.AsArray().Select(type => type!.GetValue<string>()).Should().Equal("t:transport.tour", "t:staff.employee", "t:fleet.vehicle");
        next["branches"]!.AsArray().Select(branch => (branch!["anchorTarget"]!.GetValue<string>(), branch["elements"]?.GetValue<string>(), branch["types"]!.ToJsonString(), branch["status"]!.GetValue<string>())).Should().Equal(
            (ReportSeed.Shipment, "first", """["t:transport.tour"]""", "ok"), (ReportSeed.Tour, null, """["t:staff.employee","t:fleet.vehicle"]""", "ok"));
    }

    [Fact]
    public async Task A_path_no_branch_reaches_is_UNKNOWN_PATH_in_explain_and_refuses_the_run()
    {
        var ledger = await LedgerClient();
        var request = Vehicle(""" "position": 1, "vehicle.nope": 1 """);

        var explained = await ledger.ExplainHereAsync(request);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body!["errors"]!.AsArray().OfType<JsonObject>().Should().ContainSingle()
            .Which.Should().Match<JsonObject>(error => error["code"]!.GetValue<string>() == "UNKNOWN_PATH" && error["path"]!.GetValue<string>() == "vehicle.nope" && error["stage"]!.GetValue<int>() == 7
                && error["params"]!["reason"]!.GetValue<string>() == "noTarget" && error["params"]!["alias"]!.GetValue<string>() == "vehicle");

        var refused = await ledger.QueryAsync(request);

        refused.ShouldRefuse("RESOLVE_REFUSED", 422)["stage"]!.GetValue<int>().Should().Be(6, "the union join's stage");
        refused.Errors.Should().Contain(error => error["code"]!.GetValue<string>() == "UNKNOWN_PATH" && error["path"]!.GetValue<string>() == "vehicle.nope");
    }

    [Fact]
    public async Task A_hint_path_one_branch_does_not_reach_is_dropped_for_that_branch_and_one_no_branch_reaches_is_the_stages_error()
    {
        var ledger = await LedgerClient();
        var request = Next(""" "position": 1, "sourceParent.id": 1, "next": 1 """, select: """, "select": ["number", "matchCode"]""");
        var answer = await ledger.QueryAsync(request);
        var rows = Rows(answer);

        rows.Where(row => IsShipmentRow(row) && row["next"] is JsonObject).Should().NotBeEmpty().And.OnlyContain(row => row["next"]!.AsObject().ContainsKey("number") && !row["next"]!.AsObject().ContainsKey("matchCode"));
        rows.Where(row => IsTourRow(row) && row["next"] is JsonObject).Should().NotBeEmpty().And.OnlyContain(row => !row["next"]!.AsObject().ContainsKey("number"));
        answer.Diagnostics.Should().Contain(diagnostic => diagnostic["code"]!.GetValue<string>() == "SELECT_PATH_NOT_ON_TARGET" && diagnostic["stage"]!.GetValue<int>() == 5
            && diagnostic["path"]!.GetValue<string>() == "matchCode" && diagnostic["params"]!["branch"]!.GetValue<string>() == ReportSeed.Shipment);

        var explained = await ledger.ExplainHereAsync(request);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body!["notes"]!.AsArray().Where(note => note!["code"]!.GetValue<string>() == "SELECT_PATH_NOT_ON_TARGET" && note["params"]!["branch"] is not null)
            .Select(note => (note!["stage"]!.GetValue<int>(), note["path"]!.GetValue<string>(), note["params"]!["branch"]!.GetValue<string>()))
            .Should().BeEquivalentTo([(5, "matchCode", ReportSeed.Shipment), (5, "number", ReportSeed.Tour)]);

        // Both branches reach a vehicle, which has no such member: the hint path is unknown at the stage that wrote it.
        var unknown = Vehicle(""" "position": 1, "vehicle": 1 """, select: """, "select": ["nope"]""");
        var invalid = await ledger.ExplainHereAsync(unknown);

        invalid.Body!["valid"]!.GetValue<bool>().Should().BeFalse(invalid.Text);
        invalid.Body!["errors"]!.AsArray().OfType<JsonObject>().Should().ContainSingle()
            .Which.Should().Match<JsonObject>(error => error["code"]!.GetValue<string>() == "UNKNOWN_PATH" && error["path"]!.GetValue<string>() == "nope" && error["stage"]!.GetValue<int>() == 6);

        var refused = await ledger.QueryAsync(unknown);

        refused.ShouldRefuse("RESOLVE_REFUSED", 422);
        refused.Errors.Should().Contain(error => error["code"]!.GetValue<string>() == "UNKNOWN_PATH" && error["path"]!.GetValue<string>() == "nope" && error["stage"]!.GetValue<int>() == 6);
    }

    // ---- refusals ---------------------------------------------------------------------------------------

    [Fact]
    public async Task One_branch_is_refused_naming_the_plain_form()
    {
        var refused = await (await LedgerClient()).QueryAsync(Lines(
            $$"""{ "resolve": { "as": "vehicle", "byTarget": { "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } }""", """ "position": 1, "vehicle": 1 """));

        var error = refused.ShouldRefuse("OPTION_NOT_APPLICABLE", 400);

        error["stage"]!.GetValue<int>().Should().Be(5);
        error["message"]!.GetValue<string>().Should().Contain("'path' and 'forTarget'");
        error["params"]!.ToJsonString().Should().Be($$"""{"option":"byTarget","reason":"singleBranch","target":"{{ReportSeed.Tour}}","form":"forTarget"}""");
    }

    [Fact]
    public async Task Branches_of_one_record_and_of_every_element_are_UNION_CARDINALITY_MISMATCH()
    {
        var refused = await (await LedgerClient()).QueryAsync(Lines($$"""
            { "resolve": { "as": "next",
                           "byTarget": { "{{ReportSeed.Shipment}}": { "path": "sourceParent.tours.tourId", "elements": "all" }, "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } }
            """, """ "position": 1, "next": 1 """));

        var error = refused.ShouldRefuse("UNION_CARDINALITY_MISMATCH", 400);

        refused.ErrorCodes.Should().Equal(["UNION_CARDINALITY_MISMATCH"]);
        error["stage"]!.GetValue<int>().Should().Be(5);
        error["params"]!["branch"]!.GetValue<string>().Should().Be(ReportSeed.Tour);
        error["params"]!["expected"]!.GetValue<string>().Should().Be("all");
    }

    [Fact]
    public async Task A_branch_its_owner_cannot_bind_is_the_requests_error_at_the_union_join_naming_the_branch()
    {
        var ledger = await LedgerClient();

        // A shipment has no resource: the shipment's branch does not bind at transport.
        var request = Lines($$"""
            { "resolve": { "as": "vehicle", "byTarget": { "{{ReportSeed.Shipment}}": "sourceParent.resource.id", "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } }
            """, """ "position": 1, "vehicle": 1 """);

        var explained = await ledger.ExplainHereAsync(request);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);

        var error = explained.Body!["errors"]!.AsArray().OfType<JsonObject>().Should().ContainSingle().Subject;
        error["code"]!.GetValue<string>().Should().Be("UNKNOWN_PATH");
        error["stage"]!.GetValue<int>().Should().Be(5);
        error["path"]!.GetValue<string>().Should().Be("sourceParent.resource.id");
        error["params"]!["owner"]!["target"]!.GetValue<string>().Should().Be("transport.shipment#billingLines");

        explained.Body!["aliases"]!["vehicle"]!["branches"]!.AsArray().Select(branch => branch!["status"]!.GetValue<string>()).Should().Equal("error", "ok");
        explained.Body!["stages"]![5]!.AsObject().ContainsKey("placement").Should().BeFalse("a stage an owner refused runs nowhere");

        var refused = await ledger.QueryAsync(request);

        refused.ShouldRefuse("RESOLVE_REFUSED", 422);
        refused.Errors.Should().Contain(each => each["code"]!.GetValue<string>() == "UNKNOWN_PATH" && each["stage"]!.GetValue<int>() == 5 && each["path"]!.GetValue<string>() == "sourceParent.resource.id");
    }

    // ---- a nested anchor --------------------------------------------------------------------------------

    /// <summary>
    /// The billing line is resolved element-wise (keyed at this host), the transport line continues under
    /// it, and the union join continues under the transport line's owning row: its targets are that
    /// alias's, which this host's own owner query binds and splits.
    /// </summary>
    private static string Nested => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "resolve": { "path": "items.billingLineId", "as": "billingLine", "elements": "first" } },
            { "resolve": { "path": "billingLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
            { "resolve": { "as": "next",
                           "byTarget": { "{{ReportSeed.Shipment}}": { "path": "sourceParent.tours.tourId", "elements": "first" }, "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } },
            { "project": { "number": 1, "sourceParent.id": 1, "next.id": 1 } }
          ]
        }
        """;

    [Fact]
    public async Task A_union_join_under_an_alias_a_continued_stage_added_is_split_by_that_aliass_owner()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(Nested);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body!["aliases"]!["next"]!["branches"]!.AsArray().Select(branch => (branch!["anchorTarget"]!.GetValue<string>(), branch["status"]!.GetValue<string>()))
            .Should().Equal([(ReportSeed.Shipment, "ok"), (ReportSeed.Tour, "ok")], "the branches as the owner that split the stage answered them");
        explained.Body!["aliases"]!["next"]!["continuedFrom"]!.ToJsonString().Should().Be("""{"alias":"sourceParent"}""");

        var row = Rows(await ledger.QueryAsync(Nested)).Should().ContainSingle().Subject;

        row["sourceParent"]!["entity"]!.GetValue<string>().Should().Be(ReportSeed.Shipment, "the first billing line of the invoice is a shipment line");
        row["next"]!["id"]!.GetValue<string>().Should().Be(Id(ReportSeed.TractorTourId), "the shipment's branch: its first tour");
    }

    // ---- a stage continued under the one alias ----------------------------------------------------------

    [Fact]
    public async Task A_stage_continued_under_the_union_joins_alias_runs_for_the_rows_of_every_branch()
    {
        var ledger = await LedgerClient();
        var request = Lines($$"""
            {{DeliveringTour}},
            { "resolve": { "as": "vehicle", "byTarget": { "{{ReportSeed.Shipment}}": "deliveringTour.resource.id", "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } },
            { "resolve": { "path": "vehicle.department.id", "as": "home", "forTarget": "fleet.vehicle", "outcomeAs": "homeOutcome" } }
            """, """ "position": 1, "sourceParent.id": 1, "vehicle.matchCode": 1, "home.name": 1, "homeOutcome": 1 """);

        var explained = await ledger.ExplainHereAsync(request);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body!["aliases"]!["home"]!["continuedFrom"]!.ToJsonString().Should().Be("""{"alias":"vehicle"}""");
        explained.Body!["aliases"]!["sourceLine"]!["targets"]!.AsArray().Select(target => target!["continued"]!.ToJsonString()).Should().Equal(["[5,6,7]", "[6,7]"], "the stage goes where the alias it continues under exists");

        var rows = Rows(await ledger.QueryAsync(request));
        var withVehicle = rows.Where(row => row["vehicle"] is JsonObject).ToList();

        withVehicle.Should().Contain(row => IsShipmentRow(row)).And.Contain(row => IsTourRow(row));
        withVehicle.Should().OnlyContain(row => row["homeOutcome"]!.GetValue<string>() == withVehicle[0]["homeOutcome"]!.GetValue<string>(),
            "a vehicle reached through either branch is continued from alike");
        rows.Where(row => row["vehicle"] is null).Should().OnlyContain(row => row["homeOutcome"]!.GetValue<string>() == "reference_null", "nothing to continue from");
    }

    // ---- explain ≡ run ----------------------------------------------------------------------------------

    public static TheoryData<string> Requests => new()
    {
        VehicleUnion,
        Next(""" "position": 1, "next.number": 1, "next.matchCode": 1 """),
        Next(""" "position": 1, "next": 1 """),
        Vehicle(""" "position": 1, "vehicle.nope": 1 """),
        Vehicle(""" "position": 1, "vehicle": 1 """, select: """, "select": ["nope"]"""),
        Vehicle(""" "position": 1, "vehicle": 1 """, select: """, "select": ["matchCode"]"""),
        Next(""" "position": 1 """),
        Nested,
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Explain_is_valid_exactly_when_the_run_is_not_refused(string request)
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(request);
        var run = await ledger.QueryAsync(request);

        explained.StatusCode.Should().Be(200, explained.Text);
        explained.Body!["valid"]!.GetValue<bool>().Should().Be(run.StatusCode == 200, $"explain: {explained.Body!["errors"]!.ToJsonString()}; run: {run}");
    }
}
