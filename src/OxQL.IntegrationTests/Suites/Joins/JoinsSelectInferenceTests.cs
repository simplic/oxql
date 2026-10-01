using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Explain;
using OxQL.IntegrationTests.Suites.Report;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// Select inference on the fleet (improvement plan §3.S): a join loads what later stages read, its
/// key and its output set, and the row shows the output set alone. The report scenarios and the
/// EXAMPLE-CASE chain without any <c>select</c> answer the rows of their select-bearing versions,
/// within the run budget (T4); a member a stage only reads is loaded and not shown; the paths
/// projected under a union's alias drop per target; what a stage continued at an owner reads, its
/// owner infers.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsSelectInferenceTests
{
    public static TheoryData<string> Queries => ["A1", "A2", "A2b", "A3", "A4", "A5", "EX1-source-chain"];

    private static JsonObject Request(string id) => ExplainGolden.Cases[id]();

    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    private static string Id(Guid id) => id.ToString("D");

    private static string? Text(JsonNode? node, string path) => Json.At(node, path) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // ---- the same rows without any select ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task S01_a_query_without_any_select_answers_the_rows_of_its_select_bearing_version(string id)
    {
        var bearing = Request(id);
        var inferred = ReportScenarios.WithoutSelects(bearing);
        var client = await ReportScenarios.ClientAsync(bearing);

        inferred.ToJsonString().Should().NotContain("\"select\"", "the version under test writes no select at all");

        var expected = (await client.QueryAsync(bearing)).ShouldBeOk();
        var answer = (await client.QueryAsync(inferred)).ShouldBeOk();

        answer.Items.ToJsonString().Should().Be(expected.Items.ToJsonString(), $"{id}: the projection names what the selects showed, and the joins load what is read");
        answer.DiagnosticCodes.Should().Equal(expected.DiagnosticCodes, $"{id}: what is reported does not depend on who named the paths");
        answer.Items.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task S02_explain_and_the_run_agree_on_the_query_without_selects(string id)
    {
        var inferred = ReportScenarios.WithoutSelects(Request(id));
        var client = await ReportScenarios.ClientAsync(inferred);
        var explained = await client.ExplainHereAsync(inferred);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body["cache"]!["complete"]!.GetValue<bool>().Should().BeTrue("every owner answered what it loads and reads");
        (await client.QueryAsync(inferred)).ShouldBeOk();

        // Every join alias says what it shows: the paths the projection names under it.
        foreach (var (alias, described) in explained.Body["aliases"]!.AsObject())
        {
            if (described!["shows"] is not JsonArray shows)
                continue;

            var projected = inferred["pipeline"]!.AsArray().OfType<JsonObject>().Where(stage => stage["project"] is JsonObject)
                .SelectMany(stage => stage["project"]!.AsObject().Select(pair => pair.Key))
                .Where(path => path.StartsWith(alias + ".", StringComparison.Ordinal)).Select(path => path[(alias.Length + 1)..]).ToList();

            if (projected.Count > 0)
                shows.Select(path => path!.GetValue<string>()).Should().BeEquivalentTo(projected, $"{id}: '{alias}' shows what the projection names under it");
        }
    }

    // ---- what is only read is loaded, and not shown -------------------------------------------------------

    private static string Lines(string stages) => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(ReportSeed.TransactionId)}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            {{stages}},
            { "sort": [ { "position": "asc" } ] }
          ]
        }
        """;

    [Fact]
    public async Task S03_a_member_a_later_stage_only_reads_is_loaded_by_the_join_and_is_not_in_the_row()
    {
        var ledger = await LedgerClient();
        var whole = (await ledger.QueryAsync(Lines("""{ "resolve": { "path": "item.billingLineId", "as": "erpLine" } }"""))).ShouldBeOk();
        var read = (await ledger.QueryAsync(Lines("""
            { "resolve": { "path": "item.billingLineId", "as": "erpLine" } },
            { "match": { "erpLine.sourceBillingLineReference.type": { "exists": true } } }
            """))).ShouldBeOk();

        read.Items.Should().NotBeEmpty("the match reads a member the join loads for it: without it no row would match");
        read.Items.ToJsonString().Should().Be(whole.Items.ToJsonString(), "what the match reads is not shown: the row is the one without the match");
        read.Items.OfType<JsonObject>().Should().OnlyContain(row => !row["erpLine"]!.AsObject().ContainsKey("sourceBillingLineReference"));
    }

    [Fact]
    public async Task S04_a_projection_under_an_alias_alone_decides_what_the_alias_shows_whatever_its_hint_names()
    {
        var ledger = await LedgerClient();
        var answer = (await ledger.QueryAsync(Lines("""
            { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": ["id", "sourceBillingLineReference.id"] } },
            { "match": { "erpLine.sourceBillingLineReference.type": { "exists": true } } },
            { "project": { "position": 1, "erpLine.text": 1 } }
            """))).ShouldBeOk();

        answer.Items.Should().NotBeEmpty();
        answer.Items.OfType<JsonObject>().Should().OnlyContain(row => row["erpLine"]!.AsObject().Select(member => member.Key).SequenceEqual(new[] { "text" }),
            "the hint says what a whole alias shows; under a projection the row carries the projected path only, neither the hint nor the key nor what the match read");
    }

    // ---- a union: the paths under its alias are flat -----------------------------------------------------

    private static string SourceRows(string projection) => Lines($$"""
        { "resolve": { "path": "item.billingLineId", "as": "erpLine" } },
        { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
        { "project": { "position": 1, {{projection}} } }
        """);

    [Fact]
    public async Task S05_a_path_projected_under_a_union_alias_is_dropped_for_the_target_that_lacks_it_and_null_on_its_rows()
    {
        var ledger = await LedgerClient();
        var answer = (await ledger.QueryAsync(SourceRows("\"sourceLine.totalPrice\": 1, \"sourceParent.shipmentNumber\": 1, \"sourceParent.number\": 1"))).ShouldBeOk();
        var rows = answer.Items.OfType<JsonObject>().ToList();
        var shipments = rows.Where(row => Text(row, "sourceParent.entity") == ReportSeed.Shipment).ToList();
        var tours = rows.Where(row => Text(row, "sourceParent.entity") == ReportSeed.Tour).ToList();

        shipments.Should().NotBeEmpty();
        tours.Should().NotBeEmpty();
        shipments.Should().OnlyContain(row => row["sourceParent"]!.AsObject().Select(member => member.Key).SequenceEqual(new[] { "entity", "shipmentNumber" }),
            "a shipment has no 'number': the member is not on its rows, and the key nobody projected is not either");
        tours.Should().OnlyContain(row => row["sourceParent"]!.AsObject().Select(member => member.Key).SequenceEqual(new[] { "entity", "number" }));
        rows.Should().OnlyContain(row => row["sourceLine"]!.AsObject().Select(member => member.Key).SequenceEqual(new[] { "totalPrice" }));

        answer.Diagnostics.Where(diagnostic => Text(diagnostic, "code") == Notes.SelectPathNotOnTarget)
            .Select(diagnostic => (Text(diagnostic, "path"), Text(diagnostic, "params.target")))
            .Should().BeEquivalentTo([("number", "transport.shipment#billingLines"), ("shipmentNumber", "transport.tour#billingLines")]);
    }

    [Fact]
    public async Task S06_a_path_every_target_of_the_union_lacks_is_UNKNOWN_PATH_at_the_projection_in_explain_and_refuses_the_run()
    {
        var ledger = await LedgerClient();
        var request = SourceRows("\"sourceLine.totalPrice\": 1, \"sourceParent.nope\": 1");
        var explained = await ledger.ExplainHereAsync(request);

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Errors.Should().ContainSingle().Which.Should().Match<JsonObject>(error =>
            error["code"]!.GetValue<string>() == "UNKNOWN_PATH" && error["path"]!.GetValue<string>() == "sourceParent.nope" && error["stage"]!.GetValue<int>() == 5,
            "the path is the projection's to fix");

        var refused = await ledger.QueryAsync(request);

        refused.ShouldRefuse("RESOLVE_REFUSED", 422);
        refused.ErrorCodes.Should().Contain("UNKNOWN_PATH");
    }

    // ---- at an owner: the continued stages are bound and inferred there ----------------------------------

    private static string Chain(string projection) => Lines($$"""
        { "resolve": { "path": "item.billingLineId", "as": "erpLine" } },
        { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
        { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first" } },
        { "resolve": { "path": "deliveringTour.resource.id", "as": "shipmentVehicle", "target": "fleet.vehicle" } },
        { "project": { "position": 1, {{projection}} } }
        """);

    [Fact]
    public async Task S07_the_paths_projected_under_a_continued_alias_travel_as_paths_and_come_back_alone()
    {
        var ledger = await LedgerClient();
        var answer = (await ledger.QueryAsync(Chain("\"sourceParent.id\": 1, \"deliveringTour.number\": 1, \"shipmentVehicle.matchCode\": 1"))).ShouldBeOk();
        var continued = answer.Items.OfType<JsonObject>().Where(row => row["deliveringTour"] is JsonObject).ToList();

        continued.Should().NotBeEmpty("the scenario shipment's lines have a delivering tour");
        continued.Should().OnlyContain(row => row["deliveringTour"]!.AsObject().Select(member => member.Key).SequenceEqual(new[] { "number" }),
            "the owner joins the tour, loads the member that the vehicle's resolve reads under it, and answers what the projection names");
        continued.Where(row => row["shipmentVehicle"] is JsonObject).Should().NotBeEmpty("the vehicle resolves through a member of the tour nobody selected")
            .And.OnlyContain(row => row["shipmentVehicle"]!.AsObject().Select(member => member.Key).SequenceEqual(new[] { "matchCode" }));
    }

    [Fact]
    public async Task S08_explain_answers_the_reads_of_a_continued_stage_as_its_owner_bound_it()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(Chain("\"sourceParent.id\": 1, \"deliveringTour.number\": 1, \"shipmentVehicle.matchCode\": 1"));

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);

        var stages = explained.Body["stages"]!.AsArray();
        var tour = stages[5]!["reads"]!.AsArray().Select(read => (Text(read, "path"), Text(read, "use"), Text(read, "alias"))).ToList();
        var vehicle = stages[6]!["reads"]!.AsArray().Select(read => (Text(read, "path"), Text(read, "use"), Text(read, "alias"))).ToList();

        tour.Should().Contain(("sourceParent.tours.tourId", "resolveKey", "sourceParent"), "the owner's read of its own row, in the origin row's paths");
        vehicle.Should().Contain(("deliveringTour.resource.id", "resolveKey", "deliveringTour"));
        vehicle.Should().Contain(read => read.Item2 == "caseCondition" && read.Item3 == "deliveringTour", "the member that picks the reference's case, which only the owner's model knows");
        explained.Body["aliases"]!["deliveringTour"]!["shows"]!.AsArray().Select(path => path!.GetValue<string>()).Should().Equal("number");
        explained.Body["aliases"]!["sourceParent"]!["loads"]!.AsArray().Select(path => path!.GetValue<string>()).Should().Equal(["id", "tours.tourId"],
            "what the projection names under the owning row, and what the stage continued under it reads");
        explained.Body["aliases"]!["sourceParent"]!["hint"].Should().BeNull();
    }

    // ---- an alias a later join only continues from is loaded, and not shown --------------------------------

    private const string ErpLine = """{ "resolve": { "path": "item.billingLineId", "as": "erpLine" } }""";
    private const string SourceLine = """{ "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent", "onMissing": "report" } }""";
    private static readonly string DeliveringTour = $$"""{ "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first" } }""";
    private static readonly string TourVehicle = $$"""{ "resolve": { "path": "sourceParent.resource.id", "as": "tourVehicle", "forTarget": "{{ReportSeed.Tour}}", "target": "fleet.vehicle" } }""";
    private const string ShipmentVehicle = """{ "resolve": { "path": "deliveringTour.resource.id", "as": "shipmentVehicle", "target": "fleet.vehicle" } }""";

    /// <summary>
    /// One case per kind of join another join depends on: the stages, the paths of the last aliases
    /// alone, the paths that name the aliases they depend on, and those aliases.
    /// </summary>
    public static TheoryData<string, string, string, string, string[]> Anchors => new()
    {
        { "an inline alias a keyed resolve reads its key from", $"{ErpLine}, {SourceLine}",
            "\"sourceLine.totalPrice\": 1", "\"erpLine.id\": 1", ["erpLine"] },
        { "a keyed alias's owning row a continued resolve of a union target reads", $"{ErpLine}, {SourceLine}, {DeliveringTour}",
            "\"deliveringTour.number\": 1", "\"erpLine.id\": 1, \"sourceLine.totalPrice\": 1, \"sourceParent.id\": 1", ["erpLine", "sourceLine", "sourceParent"] },
        { "the owning row under two continued resolves, one per union target", $"{ErpLine}, {SourceLine}, {DeliveringTour}, {TourVehicle}",
            "\"deliveringTour.number\": 1, \"tourVehicle.matchCode\": 1", "\"sourceLine.totalPrice\": 1, \"sourceParent.id\": 1", ["sourceLine", "sourceParent"] },
        { "a continued alias a stage continued under it reads", $"{ErpLine}, {SourceLine}, {DeliveringTour}, {ShipmentVehicle}",
            "\"shipmentVehicle.matchCode\": 1", "\"sourceParent.id\": 1, \"deliveringTour.number\": 1", ["sourceParent", "deliveringTour"] },
        { "the owning row with only the keyed alias named", $"{ErpLine}, {SourceLine}, {DeliveringTour}",
            "\"sourceLine.totalPrice\": 1, \"deliveringTour.number\": 1", "\"sourceParent.id\": 1", ["sourceParent"] },
    };

    private static string Anchored(string stages, string projection) => Lines($$"""
        {{stages}},
        { "project": { "position": 1, {{projection}} } }
        """);

    [Theory]
    [MemberData(nameof(Anchors))]
    public async Task S10_a_projection_that_names_only_the_last_alias_answers_the_rows_with_the_aliases_it_depends_on_less_those_aliases(string kind, string stages, string leaf, string anchors, string[] dropped)
    {
        var ledger = await LedgerClient();
        var only = (await ledger.QueryAsync(Anchored(stages, leaf))).ShouldBeOk();
        var with = (await ledger.QueryAsync(Anchored(stages, leaf + ", " + anchors))).ShouldBeOk();

        var expected = with.Items.Select(row => row!.DeepClone().AsObject()).ToList();

        foreach (var row in expected)
            foreach (var alias in dropped)
                row.Remove(alias).Should().BeTrue($"{kind}: the row with the aliases projected carries '{alias}'");

        only.Items.Should().NotBeEmpty();
        only.Items.OfType<JsonObject>().Select(row => row.ToJsonString()).Should().Equal(expected.Select(row => row.ToJsonString()),
            $"{kind}: the aliases a join depends on are loaded for it and cut from the row");

        var last = leaf.Split('"')[1].Split('.')[0];

        only.Items.OfType<JsonObject>().Should().Contain(row => row[last] is JsonObject, $"{kind}: '{last}' resolves through aliases the row does not show");
    }

    [Theory]
    [MemberData(nameof(Anchors))]
    public async Task S11_explain_is_valid_for_a_projection_that_names_only_the_last_alias_and_shows_nothing_under_the_aliases_it_depends_on(string kind, string stages, string leaf, string anchors, string[] dropped)
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(Anchored(stages, leaf));

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue($"{kind}: {explained.Text}");
        anchors.Should().NotBeEmpty();

        var columns = explained.Body["result"]!["columns"]!.AsArray().Select(column => Text(column, "path")!).ToList();

        foreach (var alias in dropped)
        {
            var described = explained.Body["aliases"]![alias]!.AsObject();

            described["shows"]!.AsArray().Should().BeEmpty($"{kind}: '{alias}' is loaded for the join that reads it and is not in the row");
            described["droppedAt"].Should().NotBeNull();
            columns.Should().NotContain(path => path == alias || path.StartsWith(alias + ".", StringComparison.Ordinal));
            // The line itself is read by nothing: it is fetched with its owning row, which the continued stages read.
            if (alias != "sourceLine")
                explained.Body["stages"]!.AsArray().SelectMany(stage => stage!["reads"]?.AsArray().ToList() ?? [])
                    .Should().Contain(read => Text(read, "alias") == alias && (Text(read, "use") == "resolveKey" || Text(read, "use") == "lookupOn"), $"{kind}: a later join reads '{alias}'");
        }

        // Every join the row depends on is placed: explain says where the run fetches it.
        foreach (var stage in explained.Body["stages"]!.AsArray().OfType<JsonObject>().Where(stage => Text(stage, "kind") == "resolve"))
            stage["placement"].Should().NotBeNull($"{kind}: the resolve at stage {stage["index"]} runs");
    }

    [Fact]
    public async Task S12_explain_answers_what_an_alias_kept_alive_loads_the_reads_of_the_stages_continued_under_it()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(Anchored($"{ErpLine}, {SourceLine}, {DeliveringTour}, {TourVehicle}", "\"deliveringTour.number\": 1, \"tourVehicle.matchCode\": 1"));

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body["aliases"]!["sourceParent"]!["loads"]!.AsArray().Select(path => path!.GetValue<string>()).Should().Equal("resource.id", "tours.tourId");
        explained.Body["aliases"]!["sourceLine"]!["loads"]!.AsArray().Should().BeEmpty("nothing reads the line itself: its owner is asked for the key alone");
        explained.Body["aliases"]!["erpLine"]!["loads"]!.AsArray().Select(path => path!.GetValue<string>()).Should().Contain("sourceBillingLineReference.id");
    }

    [Fact]
    public async Task S09_parentSelect_is_refused_as_a_member_the_stage_does_not_have()
    {
        var ledger = await LedgerClient();
        var refused = await ledger.QueryAsync(Lines("""
            { "resolve": { "path": "item.billingLineId", "as": "erpLine" } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent", "parentSelect": ["id"] } }
            """));

        var error = refused.ShouldRefuse(Codes.UnknownStageMember, 400);

        Text(error, "message").Should().StartWith("'parentSelect' is not a member of resolve");
    }
}

/// <summary>T4, the run budget of select inference (improvement plan §3.T), measured without other tests running.</summary>
[Trait("Category", "Integration")]
[Collection(TimedCollection.Name)]
public class JoinsSelectInferenceBudgetTests(ITestOutputHelper output)
{
    public static TheoryData<string> Queries => JoinsSelectInferenceTests.Queries;

    private static JsonObject Request(string id) => ExplainGolden.Cases[id]();

    /// <summary>
    /// T4 (improvement plan §3.T): without selects a query answers the same row bytes, and as fast,
    /// within +10 %, as its select-bearing version. The time is the median of interleaved warm runs;
    /// a millisecond and a half of slack absorbs what a shared test machine adds to queries this short.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task T4_without_selects_the_row_bytes_and_the_latency_stay_within_a_tenth_of_the_select_bearing_version(string id)
    {
        const int Runs = 15;

        var bearing = Request(id);
        var inferred = ReportScenarios.WithoutSelects(bearing);
        var client = await ReportScenarios.ClientAsync(bearing);

        // Warm: the owners' answers of both plans are cached, as they are for every page after the first.
        for (var warmup = 0; warmup < 3; warmup++)
        {
            (await client.QueryAsync(bearing)).ShouldBeOk();
            (await client.QueryAsync(inferred)).ShouldBeOk();
        }

        var bearingMs = new List<double>();
        var inferredMs = new List<double>();
        var bearingBytes = 0;
        var inferredBytes = 0;

        for (var run = 0; run < Runs; run++)
        {
            var timer = Stopwatch.StartNew();
            var first = await client.QueryAsync(bearing);

            bearingMs.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();

            var second = await client.QueryAsync(inferred);

            inferredMs.Add(timer.Elapsed.TotalMilliseconds);
            bearingBytes = first.Items.ToJsonString().Length;
            inferredBytes = second.Items.ToJsonString().Length;
        }

        var before = bearingMs.Order().ElementAt(Runs / 2);
        var after = inferredMs.Order().ElementAt(Runs / 2);

        output.WriteLine($"{id}: rows {bearingBytes} B with selects, {inferredBytes} B without ({Percent(inferredBytes, bearingBytes)}); warm median {before:F2} ms with selects, {after:F2} ms without ({Percent(after, before)})");

        inferredBytes.Should().BeLessThanOrEqualTo((int)(bearingBytes * 1.1), "T4: the row bytes stay within +10 %");
        after.Should().BeLessThanOrEqualTo(before * 1.1 + 1.5, "T4: the latency stays within +10 %");
    }

    private static string Percent(double value, double of) => of == 0 ? "n/a" : $"{(value - of) / of * 100:+0.0;-0.0;0.0} %";
}
