using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Hosts;
using OxQL.IntegrationTests.Suites.Refusals;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Diagnostics;

/// <summary>
/// Area W: one case per diagnostic code. A diagnostic says "the rows are real, and something
/// about them is not what you asked for", so every case asserts both halves: the exact rows,
/// computed from the corpus before the request, and the exact diagnostic beside them. Rows with
/// no diagnostic, or a diagnostic with no rows, fail either way.
/// <para>
/// The legacy rig provoked two of the codes live (<c>SORT_ON_ADDON</c>, <c>REGEX_UNANCHORED</c>).
/// Here every one of the eight is provoked: a retired id through a host built with one, the count
/// cap through a variant, the three resolve diagnostics through a private fleet's chaos owner, and
/// the text-decimal exclusion on the corpus vehicles.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class DiagnosticsTests
{
    private static readonly string[] Members = ["code", "message", "params", "path", "stage"];

    /// <summary>W10: a diagnostic carries a code and a message, and nothing outside the five members the contract declares.</summary>
    private static JsonObject Only(WireAnswer answer, string code)
    {
        answer.ShouldBeOk();
        answer.DiagnosticCodes.Should().Equal([code], answer.ToString());

        var diagnostic = answer.Diagnostics[0];
        diagnostic["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        diagnostic.Select(member => member.Key).Should().BeSubsetOf(Members, diagnostic.ToJsonString());

        return diagnostic;
    }

    [Fact]
    public async Task W6_a_sort_on_a_defined_addon_key_is_answered_with_every_row_and_says_its_order_is_best_effort()
    {
        // The rows as a set: the diagnostic exists because mixed representations order by BSON
        // type, which is not a promise.
        var expected = Corpus.AllIds(Corpus.Employee);

        var answer = await (await Lab.ClientAsync(LabService.Staff)).SendAsync(Corpus.Employee, """[{ "sort": [{ "addon.shiftModel": "asc" }] }, { "page": { "limit": 500 } }]""");

        answer.ShouldHaveIdsInAnyOrder(expected);
        var diagnostic = Only(answer, "SORT_ON_ADDON");
        diagnostic["message"]!.GetValue<string>().Should().Be("The sort on 'addon.shiftModel' orders an addon key; mixed representations group by BSON type.");
        diagnostic["stage"]!.GetValue<int>().Should().Be(0);
        diagnostic["path"]!.GetValue<string>().Should().Be("addon.shiftModel");
    }

    [Fact]
    public async Task W7_an_unanchored_pattern_returns_its_rows_and_warns_and_an_anchored_one_returns_the_same_rows_without_the_member()
    {
        var unanchored = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Text(row, "matchCode") is { } code && Regex.IsMatch(code, "UP"));
        var anchored = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Text(row, "matchCode") is { } code && Regex.IsMatch(code, "^DUP"));
        unanchored.Should().HaveCount(3, "the corpus holds the three DUP rows");
        anchored.Should().Equal(unanchored);

        var staff = await Lab.ClientAsync(LabService.Staff);
        var warned = await staff.SendAsync(Corpus.Employee, """[{ "match": { "matchCode": { "regex": "UP" } } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");
        var quiet = await staff.SendAsync(Corpus.Employee, """[{ "match": { "matchCode": { "regex": "^DUP" } } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");

        warned.ShouldHaveIds(unanchored);
        var diagnostic = Only(warned, "REGEX_UNANCHORED");
        diagnostic["path"]!.GetValue<string>().Should().Be("matchCode");
        diagnostic["stage"]!.GetValue<int>().Should().Be(0);

        // W9: no diagnostic means no member, not an empty array.
        quiet.ShouldHaveIds(anchored);
        ((JsonObject)quiet.Body!).ContainsKey("diagnostics").Should().BeFalse(quiet.ToString());
    }

    [Fact]
    public async Task W8_a_diagnostic_never_replaces_rows_the_count_is_the_whole_set_and_the_diagnostic_is_one()
    {
        var expected = Corpus.AllIds(Corpus.Employee);

        var answer = await (await Lab.ClientAsync(LabService.Staff)).SendAsync(Corpus.Employee, """[{ "sort": [{ "addon.shiftModel": "asc" }] }, { "page": { "limit": 500, "includeTotalCount": true } }]""");

        answer.ShouldHaveIdsInAnyOrder(expected).ShouldHaveTotal(expected.Count);
        answer.Diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task W1_a_retired_entity_id_answers_the_current_entity_and_names_it()
    {
        // The retired id comes from the model build, as a host's schema options declare it; no
        // lab service declares one, so this host is built with it.
        var declarations = EntityScanner.Scan([typeof(LabService).Assembly], [])
            .Where(declaration => declaration.Id.StartsWith("staff.", StringComparison.Ordinal))
            .ToList();
        var model = ClrModelBuilder.Build(declarations, new Dictionary<string, IReadOnlyList<string>> { [Corpus.Employee] = ["staff.person"] });
        var expected = Corpus.AllIds(Corpus.Employee);

        await using var host = await CustomHost.StartAsync(LabService.Staff, await CustomHost.SharedDatabaseAsync(LabService.Staff), new CustomHost.Wiring { Model = model });

        var retired = await host.SendAsync("staff.person", """[{ "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");
        var current = await host.SendAsync(Corpus.Employee, """[{ "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");

        retired.ShouldHaveIds(expected);
        var diagnostic = Only(retired, "ENTITY_ID_RETIRED");
        diagnostic["params"]!["currentId"]!.GetValue<string>().Should().Be(Corpus.Employee);

        current.ShouldHaveIds(expected).ShouldHaveNoDiagnostics();
    }

    [Fact]
    public async Task W2_a_count_above_the_cap_is_the_cap_flagged_as_capped_with_the_rows_untouched()
    {
        const int cap = 1000;
        var all = Corpus.AllIds(Corpus.Template);
        all.Count.Should().BeGreaterThan(cap);

        var capped = (await CorpusFleet.SharedAsync()).Variant(LabService.Transport, "b5-countcap-1000", new Dictionary<string, string?> { ["OxQL:Limits:CountCap"] = $"{cap}" });

        var answer = await capped.SendAsync(Corpus.Template, """[{ "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 5, "includeTotalCount": true } }]""");

        answer.ShouldHaveIds(Corpus.PageOf(all, 5)).ShouldHaveTotal(cap, capped: true);
        var diagnostic = Only(answer, "TOTAL_COUNT_CAPPED");
        diagnostic["params"]!["cap"]!.GetValue<int>().Should().Be(cap);

        // Under the cap the count is exact and says nothing.
        var under = Corpus.IdsWhere(Corpus.Template, row => Corpus.Text(row, "templateName") == "T-DUP");
        var small = await capped.SendAsync(Corpus.Template, """[{ "match": { "templateName": { "eq": "T-DUP" } } }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 50, "includeTotalCount": true } }]""");
        small.ShouldHaveIds(under).ShouldHaveTotal(under.Count, capped: false).ShouldHaveNoDiagnostics();
    }

    /// <summary>The conformance rows in id order, each with the widget code its explicit remote reference names.</summary>
    private static IReadOnlyList<(Guid Id, string? Widget)> WidgetRows() =>
        Corpus.Sorted(Corpus.Conformance, [("id", false)]).Select(row => (row.Id, Corpus.Text(row, "widgetCodeExplicit"))).ToList();

    private const string WidgetPipeline = """
        [ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } },
          { "project": { "id": 1, "w": 1 } },
          { "sort": [{ "id": "asc" }] },
          { "page": { "limit": 10 } } ]
        """;

    [Fact]
    public async Task W3_an_owner_that_stalls_past_the_resolve_budget_leaves_the_alias_null_on_every_row_and_says_so()
    {
        var rows = WidgetRows();
        rows.Should().NotBeEmpty().And.OnlyContain(row => row.Widget != null, "every row names a widget, so a null is the timeout's doing");

        await using var fleet = await CorpusFleet.CreateAsync("b5-resolve-timeout", seed: [LabService.Conformance]);
        fleet.Owner.Set(new ChaosSettings { Mode = ChaosMode.Hang });
        var client = fleet.Variant(LabService.Conformance, "resolve-200", new Dictionary<string, string?> { ["OxQL:Execution:ResolveTimeoutMs"] = "200" });

        var answer = await client.SendAsync(Corpus.Conformance, WidgetPipeline);

        answer.ShouldHaveIds(rows.Select(row => row.Id));
        answer.Values("w").Should().AllSatisfy(value => value.Should().BeNull());
        var diagnostic = Only(answer, "RESOLVE_TIMEOUT");
        diagnostic["params"]!["service"]!.GetValue<string>().Should().Be("owner");
        diagnostic["params"]!["aliases"]!.AsArray().Select(alias => alias!.GetValue<string>()).Should().Equal(["w"]);
    }

    [Fact]
    public async Task W4_an_owner_that_answers_with_an_error_status_leaves_the_alias_null_on_every_row_and_says_so()
    {
        var rows = WidgetRows();

        await using var fleet = await CorpusFleet.CreateAsync("b5-resolve-unreachable", seed: [LabService.Conformance]);
        fleet.Owner.Set(new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.InternalServerError });

        var answer = await fleet.Client(LabService.Conformance).SendAsync(Corpus.Conformance, WidgetPipeline);

        answer.ShouldHaveIds(rows.Select(row => row.Id));
        answer.Values("w").Should().AllSatisfy(value => value.Should().BeNull());
        var diagnostic = Only(answer, "RESOLVE_UNREACHABLE");
        diagnostic["params"]!["service"]!.GetValue<string>().Should().Be("owner");
        diagnostic["message"]!.GetValue<string>().Should().Contain("500");
    }

    [Fact]
    public async Task W5_a_page_needing_more_distinct_keys_than_the_resolve_cap_resolves_the_first_keys_and_says_how_many()
    {
        var rows = WidgetRows();
        var keys = rows.Select(row => row.Widget).Distinct().ToList();
        keys.Should().HaveCountGreaterThan(1, "the page must need more keys than the cap of one");

        await using var fleet = await CorpusFleet.CreateAsync("b5-resolve-partial", seed: [LabService.Conformance]);
        var client = fleet.Variant(LabService.Conformance, "resolve-keys-1", new Dictionary<string, string?> { ["OxQL:Limits:MaxResolveKeys"] = "1" });

        var answer = await client.SendAsync(Corpus.Conformance, WidgetPipeline);

        answer.ShouldHaveIds(rows.Select(row => row.Id));
        var resolved = answer.Values("w.code").Select(value => value?.GetValue<string>()).ToList();
        resolved.Should().Equal(rows.Select(row => row.Widget == keys[0] ? row.Widget : null), "the first key in page order is resolved, the rest are not");
        var diagnostic = Only(answer, "RESOLVE_PARTIAL");
        diagnostic["params"]!["alias"]!.GetValue<string>().Should().Be("w");
        diagnostic["params"]!["keys"]!.GetValue<int>().Should().Be(keys.Count);
        diagnostic["params"]!["max"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task W13_an_ordered_comparison_on_a_decimal_member_covers_the_numeric_rows_and_names_the_text_rows_it_left_out()
    {
        var expected = Corpus.IdsWhere(Corpus.Vehicle, row => Corpus.Number(row, "mileage") is { } mileage && Order.CompareDecimal(mileage, "0") > 0);
        var text = Corpus.VehiclesWithStringMileage();
        expected.Should().NotBeEmpty();
        text.Should().NotBeEmpty("the corpus keeps mileages written as text");

        var answer = await (await Lab.ClientAsync(LabService.Fleet)).SendAsync(Corpus.Vehicle, """[{ "match": { "mileage": { "gt": 0 } } }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");

        answer.ShouldHaveIds(expected);
        var diagnostic = Only(answer, "DECIMAL_TEXT_EXCLUDED");
        diagnostic["path"]!.GetValue<string>().Should().Be("mileage");
        diagnostic["stage"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void W12_the_engine_declares_exactly_the_diagnostics_that_are_provoked_live()
    {
        // UNWIND_DEPTH_TRUNCATED is provoked in RowsVariantsTests, LOOKUP_TRUNCATED in JoinsLookupMembersTests, RESOLVE_MISSING,
        // RESOLVE_AMBIGUOUS and RESOLVE_TRUNCATED in JoinsOutcomesTests, every other one in this suite.
        var provoked = new[]
        {
            "SORT_ON_ADDON", "REGEX_UNANCHORED", "ENTITY_ID_RETIRED", "TOTAL_COUNT_CAPPED",
            "RESOLVE_TIMEOUT", "RESOLVE_UNREACHABLE", "RESOLVE_PARTIAL", "DECIMAL_TEXT_EXCLUDED",
            "UNWIND_DEPTH_TRUNCATED", "LOOKUP_TRUNCATED", "RESOLVE_MISSING", "RESOLVE_AMBIGUOUS", "RESOLVE_TRUNCATED",
        };
        var catalogue = typeof(OxQL.Core.Binding.Codes).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        // Every other code of the catalogue is an error, each provoked in the refusal suites
        // (RefusalsCatalogueTests checks that half), so these are the diagnostics.
        provoked.Should().BeSubsetOf(catalogue);
        RefusalsCatalogueTests.DiagnosticCodes.Should().BeEquivalentTo(provoked);
        catalogue.Should().HaveCount(RefusalsServerTests.Table.Select(row => row.Code).Distinct().Count() + 20 + provoked.Length,
            "52 codes of the V table, 20 provoked outside it (8 in the catalogue suite, EXPLAIN_LIMIT among them, UNKNOWN_REQUEST_MEMBER in RefusalsRequestMembersTests, PAGE_INCOMPLETE in JoinsOutcomesTests, RESOLVE_NOT_FILTERABLE in V35, UNKNOWN_VARIANT and FLATTEN_NOT_RECURSIVE in RowsVariantsTests, LOOKUP_ON_NOT_ENTITY in JoinsLookupMembersTests, NOT_CONTINUABLE and MAX_CONTINUED_STAGES_EXCEEDED in JoinsContinuationTests, UNION_CARDINALITY_MISMATCH in JoinsUnionJoinTests, RESOLVE_ON_COLLECTION, RESOLVE_TARGET_NOT_DECLARED and RESOLVE_PARENT_NOT_ITEM in JoinsResolveMembersTests) and the 13 diagnostics");
    }
}
