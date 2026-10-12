using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The report scenarios through <c>POST /oxql/query</c> on the fleet, as the
/// studio sends them, over organisation R's report seeds. The mixed invoice has four billing-line
/// items in document order, two of them inside groups: position 1 (the scenario shipment's
/// freight line), 3 (the tractor tour's line; its references hold a tariff and the tour), 5 (the
/// shipment's waiting-time line, two groups deep) and 6 (a line whose references hold only a
/// tariff; its source line is on the second shipment). The strict refusals, <c>onMissing</c>
/// outside strict and the contract-1 hint run A1 over the failure invoices. A2b's rows under strict
/// are <c>Suites.Joins.JoinsContinuationTests</c>' and are not repeated here.
/// </summary>
[Trait("Category", "Integration")]
public class ReportQueryTests
{
    private static readonly int[] LinePositions = [1, 3, 5, 6];

    public static TheoryData<string> Scenarios() => [.. ReportScenarios.Ids];

    private static async Task<WireAnswer> QueryAsync(JsonObject request, int? contract = 2) =>
        await (await ReportScenarios.ClientAsync(request, contract)).QueryAsync(request);

    private static async Task<List<JsonObject>> RowsAsync(string id)
    {
        var answer = await QueryAsync(ReportScenarios.Request(id));

        answer.ShouldBeOk();
        answer.HasNextPage.Should().BeFalse();

        return answer.Items.OfType<JsonObject>().ToList();
    }

    private static string? Text(JsonNode? node, string path) => Json.At(node, path) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>The request without <c>strict</c>, and so with an ordinary page (500) instead of the report page.</summary>
    private static JsonObject WithoutStrict(JsonObject request)
    {
        var copy = request.DeepClone().AsObject();
        copy.Remove("strict");

        foreach (var stage in copy["pipeline"]!.AsArray().OfType<JsonObject>().Where(stage => stage.ContainsKey("page")))
            stage["page"]!["limit"] = 500;

        return copy;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Q01_every_scenario_answers_one_page_under_its_own_strictness(string id)
    {
        var rows = await RowsAsync(id);

        rows.Should().HaveCount(id == "A2" || id == "A3" ? 1 : LinePositions.Length);

        if (rows.Count > 1)
        {
            rows.Select(row => row["position"]!.GetValue<int>()).Should().Equal(LinePositions, "one row per billing-line item, in document order, groups flattened");
            rows.Should().OnlyContain(row => Text(row, "number") == "RE-2026-0001", "the header repeats on every line row");
        }
    }

    [Fact]
    public async Task Q02_A1_the_source_line_and_its_owning_row_come_from_both_logistics_owners()
    {
        var answer = await QueryAsync(ReportScenarios.Request("A1"));
        answer.ShouldBeOk();
        var rows = answer.Items.OfType<JsonObject>().ToList();

        rows.Select(row => Text(row, "erpLine.id")).Should().Equal(Id(ReportSeed.ErpLineIds[0]), Id(ReportSeed.ErpLineIds[2]), Id(ReportSeed.ErpLineIds[1]), Id(ReportSeed.ErpLineIds[5]));
        rows.Select(row => Text(row, "sourceLine.id")).Should().Equal(Id(ReportSeed.ShipmentLineIds[0]), Id(ReportSeed.TourLineId), Id(ReportSeed.ShipmentLineIds[1]), Id(ReportSeed.ShipmentLineIds[2]));
        rows.Select(row => (Text(row, "sourceParent.entity"), Text(row, "sourceParent.id"))).Should().Equal(
            (ReportSeed.Shipment, Id(ReportSeed.ShipmentId)),
            (ReportSeed.Tour, Id(ReportSeed.TractorTourId)),
            (ReportSeed.Shipment, Id(ReportSeed.ShipmentId)),
            (ReportSeed.Shipment, Id(ReportSeed.ShipmentWithoutAttemptsId)));

        var shipment = rows[0]["sourceParent"]!.AsObject();
        var tour = rows[1]["sourceParent"]!.AsObject();

        shipment.Select(member => member.Key).Should().BeEquivalentTo(["entity", "id", "shipmentNumber", "referenceNumber"], "the paths projected under the owning row, less what a shipment lacks");
        (Text(shipment, "shipmentNumber"), Text(shipment, "referenceNumber")).Should().Be(("SN-2026-0001", "KD-4711"));
        tour.Select(member => member.Key).Should().BeEquivalentTo(["entity", "id", "number"], "the paths projected under the owning row, less what a tour lacks");
        Text(tour, "number").Should().Be("TR-2026-0101");

        rows[0]["sourceLine"]!.AsObject().Select(member => member.Key).Should().BeEquivalentTo(["id", "type", "status", "singlePrice", "totalPrice", "quantity"]);
        Text(rows[0], "sourceLine.quantity.quantityUnit.name").Should().Be("h");
        Text(rows[1], "item.text").Should().Be("Billing line 4", "the item inside the group");

        var dropped = answer.Diagnostics.Where(diagnostic => Text(diagnostic, "code") == Notes.SelectPathNotOnTarget)
            .Select(diagnostic => (Text(diagnostic, "path"), Text(diagnostic, "params.target"))).ToList();

        dropped.Should().BeEquivalentTo([
            ("number", "transport.shipment#billingLines"),
            ("shipmentNumber", "transport.tour#billingLines"),
            ("referenceNumber", "transport.tour#billingLines")]);
    }

    [Fact]
    public async Task Q03_A1_under_strict_a_deleted_source_line_is_refused_with_RESOLVE_MISSING_not_found()
    {
        var request = ReportScenarios.WithVariable(ReportScenarios.Request("A1"), "transactionId", ReportSeed.MissingSourceTransactionId);

        var error = (await QueryAsync(request)).ShouldRefuse(Codes.ResolveMissing, 422);

        error["stage"]!.GetValue<int>().Should().Be(ReportScenarios.IndexOf(request, "sourceLine"));
        Text(error, "path").Should().Be("erpLine.sourceBillingLineReference.id");
        Text(error, "params.alias").Should().Be("sourceLine");
        error["params"]!["rows"]!.AsArray().Select(row => (row!["row"]!.GetValue<int>(), Text(row, "key"), Text(row, "outcome")))
            .Should().Equal((0, Id(ReportSeed.DeletedSourceLineId), "not_found"));
    }

    [Fact]
    public async Task Q04_A1_under_strict_a_source_line_held_by_a_shipment_and_a_tour_is_refused_with_RESOLVE_AMBIGUOUS()
    {
        var request = ReportScenarios.WithVariable(ReportScenarios.Request("A1"), "transactionId", ReportSeed.AmbiguousSourceTransactionId);

        var error = (await QueryAsync(request)).ShouldRefuse(Codes.ResolveAmbiguous, 422);

        Text(error, "params.alias").Should().Be("sourceLine");
        error["params"]!["rows"]!.AsArray().Select(row => (Text(row, "key"), Text(row, "outcome"))).Should().Equal((Id(ReportSeed.DuplicateLineId), "ambiguous"));
    }

    [Fact]
    public async Task Q05_A1_outside_strict_a_missing_source_line_reads_null_and_onMissing_report_lists_it_beside_the_rows()
    {
        var request = WithoutStrict(ReportScenarios.WithVariable(ReportScenarios.Request("A1"), "transactionId", ReportSeed.MissingSourceTransactionId));

        var silent = await QueryAsync(request);
        silent.ShouldBeOk();
        silent.Items.OfType<JsonObject>().Should().ContainSingle().Which["sourceLine"].Should().BeNull();
        silent.DiagnosticCodes.Should().NotContain(Codes.ResolveMissing, "onMissing null reports only owner failures");

        var index = ReportScenarios.IndexOf(request, "sourceLine");
        request["pipeline"]![index]!["resolve"]!["onMissing"] = "report";

        var reported = await QueryAsync(request);
        reported.ShouldBeOk();
        reported.Items.OfType<JsonObject>().Should().ContainSingle().Which["sourceLine"].Should().BeNull();
        reported.ShouldHaveDiagnostic(Codes.ResolveMissing)["params"]!["rows"]!.AsArray().Single()!["outcome"]!.GetValue<string>().Should().Be("not_found");
    }

    [Fact]
    public async Task Q06_A1_sent_without_the_contract_header_is_read_as_contract_1_and_the_refusal_names_the_header()
    {
        var answer = await QueryAsync(ReportScenarios.Request("A1"), contract: null);

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.Errors.Should().NotBeEmpty();
        answer.Errors.Should().OnlyContain(error => Text(error, "message")!.EndsWith(Binder.Contract1Hint, StringComparison.Ordinal), "every contract-2 construct points at the header");
    }

    [Fact]
    public async Task Q07_A2a_the_latest_attempt_is_the_newest_by_date_and_a_shipment_without_attempts_has_none()
    {
        var row = (await RowsAsync("A2")).Single();

        Text(row, "shipmentNumber").Should().Be("SN-2026-0001");
        row["lastAttempt"]!.AsObject().Select(member => member.Key).Should().BeEquivalentTo(["id", "dateTime", "status", "text"]);
        (Text(row, "lastAttempt.dateTime"), Text(row, "lastAttempt.status.displayName"), Text(row, "lastAttempt.text"))
            .Should().Be(("2026-04-08T14:30:00Z", "delivered", "Delivered to gate 3"), "the latest of three attempts, stored second");

        var none = await QueryAsync(ReportScenarios.WithVariable(ReportScenarios.Request("A2"), "shipmentId", ReportSeed.ShipmentWithoutAttemptsId));

        none.ShouldBeOk();
        none.Items.OfType<JsonObject>().Single()["lastAttempt"].Should().BeNull("first over no children is null, not data loss");
        none.ShouldHaveNoDiagnostics();
    }

    [Fact]
    public async Task Q08_A3_the_recipients_email_comes_from_the_directory_contact()
    {
        var row = (await RowsAsync("A3")).Single();

        Text(row, "recipientContact.id").Should().Be(Id(ReportSeed.RecipientContactId));
        (Text(row, "recipientContact.primaryEmailAddress.email"), Text(row, "recipientContact.primaryPhoneNumber.number"), Text(row, "recipientContact.address.companyName"))
            .Should().Be(("rechnung@nordhafen.example", "+49 40 555 0100", "Nordhafen Logistik GmbH"));
        Text(row, "invoiceRecipient.address.id").Should().Be(Id(ReportSeed.RecipientContactId), "the snapshot the reference was read from stays");
    }

    [Fact]
    public async Task Q09_A4_each_line_reaches_its_shipment_tour_vehicle_and_driver_or_its_tour_and_a_line_with_no_such_reference_is_excluded()
    {
        var rows = await RowsAsync("A4");
        var byPosition = rows.ToDictionary(row => row["position"]!.GetValue<int>());
        var shipmentLines = new[] { byPosition[1], byPosition[5] };

        foreach (var row in shipmentLines)
        {
            (Text(row, "lineShipment.shipmentNumber"), Text(row, "lineShipment.deliveryNoteNumber"), Text(row, "lineShipment.effectiveDeliveryEnd"))
                .Should().Be(("SN-2026-0001", "LS-2026-0001", "2026-04-08T14:30:00Z"), "R3 + R6");
            row["lineShipment"]!["items"]!.AsArray().Single()!["weightNotes"]!.AsArray().Select(note => Text(note, "number"))
                .Should().Equal("WN-2026-0001", "WN-2026-0002");
            Text(row, "deliveringTour.number").Should().Be("TR-2026-0101", "R5: the shipment's tour");
            Text(row, "tourVehicle.registrationPlate.registrationIdentifier").Should().Be("HH-NH 1204", "R5: the tour's tractor at fleet");
            (Text(row, "driver.address.firstName"), Text(row, "driver.address.lastName")).Should().Be(("Jonas", "Weber"), "R5: the attached driver at staff");
            row["lineTour"].Should().BeNull("R9: a shipment reference is not a tour");
        }

        var tourLine = byPosition[3];
        tourLine["lineShipment"].Should().BeNull("R3: tariff and tour references only — excluded");
        Text(tourLine, "lineTour.number").Should().Be("TR-2026-0101", "R9: the tour through the string self-reference");
        tourLine["lineTour"]!["actions"]!.AsArray().Should().HaveCount(7, "R9: the interface-typed actions, one of each variant");
        (tourLine["deliveringTour"], tourLine["tourVehicle"], tourLine["driver"]).Should().Be((null, null, null), "nothing to chain from");

        var tariffLine = byPosition[6];
        (tariffLine["lineShipment"], tariffLine["lineTour"]).Should().Be((null, null), "a tariff reference names neither — excluded");

        rows.Should().OnlyContain(row => Text(row, "clerk.address.lastName") == "Becker" && Text(row, "termsOfPayment.formattedText")!.StartsWith("Payable", StringComparison.Ordinal),
            "R8 and R1 repeat on every line");
    }

    [Fact]
    public async Task Q10_A4_a_clerk_no_employee_is_reads_null_under_strict_because_R8_asks_onMissing_null()
    {
        var request = ReportScenarios.WithVariable(ReportScenarios.Request("A4"), "transactionId", ReportSeed.MissingClerkTransactionId);

        var answer = await QueryAsync(request);

        answer.ShouldBeOk();
        answer.Items.OfType<JsonObject>().Should().NotBeEmpty().And.OnlyContain(row => row["clerk"] == null);
    }

    [Fact]
    public async Task Q11_A5_every_hop_lands_on_its_line_and_the_latest_attempt_is_empty_on_the_tour_line()
    {
        var rows = await RowsAsync("A5");
        var byPosition = rows.ToDictionary(row => row["position"]!.GetValue<int>());

        rows.Should().OnlyContain(row => Text(row, "recipientContact.primaryEmailAddress.email") == "rechnung@nordhafen.example" && Text(row, "clerk.address.firstName") == "Anna",
            "the header's hops repeat on every line row");

        foreach (var position in new[] { 1, 5 })
        {
            var row = byPosition[position];
            Text(row, "sourceParent.entity").Should().Be(ReportSeed.Shipment);
            Text(row, "lastAttempt.dateTime").Should().Be("2026-04-08T14:30:00Z", "A2b continued at transport");
            Text(row, "lineShipment.shipmentNumber").Should().Be("SN-2026-0001");
            Text(row, "tourVehicle.matchCode").Should().Be("ZM-01");
            Text(row, "driver.primaryEmailAddress.email").Should().Be("jonas.weber@lab.example");
        }

        Text(byPosition[3], "sourceParent.entity").Should().Be(ReportSeed.Tour);
        byPosition[3]["lastAttempt"].Should().BeNull("not_applicable: the lookup is for shipment targets, and strict does not refuse it");
        Text(byPosition[3], "lineTour.number").Should().Be("TR-2026-0101");

        Text(byPosition[6], "sourceParent.id").Should().Be(Id(ReportSeed.ShipmentWithoutAttemptsId));
        byPosition[6]["lastAttempt"].Should().BeNull("the second shipment has no attempt");
        (byPosition[6]["lineShipment"], byPosition[6]["lineTour"]).Should().Be((null, null));
    }

    // ── the request's default for a missing join target ─────────────────────────────────────────

    private static JsonObject MissingSource(string? onMissing)
    {
        var request = ReportScenarios.WithVariable(ReportScenarios.Request("A1"), "transactionId", ReportSeed.MissingSourceTransactionId);

        if (onMissing is not null)
            request["onMissing"] = onMissing;

        return request;
    }

    [Fact]
    public async Task Q12_a_strict_request_that_says_onMissing_report_answers_its_rows_and_lists_the_missing_target()
    {
        // The request as the report wrote it, strict and all, dies on one deleted source line (Q03) ...
        (await QueryAsync(MissingSource(null))).ShouldRefuse(Codes.ResolveMissing, 422);

        // ... and with one word at the top every resolve reports instead: no stage is touched.
        var reported = await QueryAsync(MissingSource("report"));

        reported.ShouldBeOk();
        reported.Items.OfType<JsonObject>().Should().ContainSingle().Which["sourceLine"].Should().BeNull();

        var diagnostic = reported.ShouldHaveDiagnostic(Codes.ResolveMissing);

        diagnostic["stage"]!.GetValue<int>().Should().Be(ReportScenarios.IndexOf(MissingSource(null), "sourceLine"));
        diagnostic["params"]!["rows"]!.AsArray().Single()!["outcome"]!.GetValue<string>().Should().Be("not_found");

        var silent = await QueryAsync(MissingSource("null"));

        silent.ShouldBeOk();
        silent.Items.OfType<JsonObject>().Should().ContainSingle().Which["sourceLine"].Should().BeNull();
        silent.DiagnosticCodes.Should().NotContain(Codes.ResolveMissing);
    }

    [Fact]
    public async Task Q13_a_resolve_that_names_its_own_onMissing_keeps_it_over_the_requests()
    {
        var request = WithoutStrict(MissingSource("null"));

        request["pipeline"]![ReportScenarios.IndexOf(request, "sourceLine")]!["resolve"]!["onMissing"] = "refuse";

        (await QueryAsync(request)).ShouldRefuse(Codes.ResolveMissing, 422)["params"]!["alias"]!.GetValue<string>().Should().Be("sourceLine");

        // And outside strict the request's word alone refuses, where the default would have read null.
        var refusing = WithoutStrict(MissingSource("refuse"));

        (await QueryAsync(refusing)).ShouldRefuse(Codes.ResolveMissing, 422);
    }

    [Fact]
    public async Task Q14_onMissing_at_the_top_takes_one_of_three_words_and_contract_1_ignores_it()
    {
        foreach (var written in new JsonNode?[] { "fail", "Report", 1, true, new JsonObject() })
        {
            var request = MissingSource(null);

            request["onMissing"] = written?.DeepClone();

            var error = (await QueryAsync(request)).ShouldRefuse(Codes.InvalidOperand, 400, written?.ToJsonString() ?? "null");

            error["params"]!["member"]!.GetValue<string>().Should().Be("onMissing");
            error["params"]!["allowed"]!.AsArray().Select(word => word!.GetValue<string>()).Should().Equal("null", "report", "refuse");
        }

        var none = MissingSource(null);

        none["onMissing"] = null;
        (await QueryAsync(none)).ShouldRefuse(Codes.ResolveMissing, 422, "null is the member left out: strict's default stands");

        // Contract 1 has no such member and reads past it, whatever it holds, as it reads past every member it does not know.
        var legacy = await (await Lab.ClientAsync(OxQL.IntegrationTests.Fleet.LabService.Ledger, Org.R, contract: null))
            .QueryAsync(JsonNode.Parse("""{ "entityType": "ledger.transaction", "onMissing": { "not": "a word" }, "pipeline": [ { "page": { "limit": 1 } } ] }""")!);

        legacy.ShouldBeOk();
    }

    [Fact]
    public async Task Q15_explain_says_the_effective_onMissing_of_every_join_and_whose_word_it_is()
    {
        var request = MissingSource("report");
        var own = ReportScenarios.IndexOf(request, "erpLine");
        var inherited = ReportScenarios.IndexOf(request, "sourceLine");

        request["pipeline"]![own]!["resolve"]!["onMissing"] = "refuse";

        var answer = await (await ReportScenarios.ClientAsync(request)).ExplainAsync(request);

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Body!["valid"]!.GetValue<bool>().Should().BeTrue(answer.ToString());

        var policies = answer.Body!["notes"]!.AsArray().OfType<JsonObject>().Where(note => Text(note, "code") == Notes.MissingPolicy).ToDictionary(note => note["stage"]!.GetValue<int>());

        Text(policies[own], "params.onMissing").Should().Be("refuse");
        policies[own]["params"]!.AsObject().ContainsKey("from").Should().BeFalse("the stage's own word");
        Text(policies[inherited], "params.onMissing").Should().Be("report");
        Text(policies[inherited], "params.from").Should().Be("request");
        Text(policies[inherited], "message").Should().Contain("onMissing report, the request's default");
    }
}
