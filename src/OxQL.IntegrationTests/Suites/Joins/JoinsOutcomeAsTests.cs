using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Hosts;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// The per-row outcome on a real server (improvement plan §3.O): <c>outcomeAs</c> names a member that
/// says on every row what became of the reference, <c>resolved</c> included. Every outcome per kind of
/// join: an inline resolve (written by the aggregate, so a condition, a group and a count read it), a
/// keyed one, an element-wise one, a remote one, a stage continued at an owner and a union join (written
/// after the page, lifted from the owner, and said by the origin for the rows no owner ran the stage
/// for). It reports and refuses nothing by itself: <c>onMissing</c> and <c>strict</c> keep their roles.
/// A private database holds the dangling and duplicated local references; the report fleet the chains.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsOutcomeAsTests
{
    private static string Id(Guid id) => id.ToString("D");

    private static IReadOnlyList<string?> Outcomes(WireAnswer answer, string member) =>
        answer.ShouldBeOk().Items.Select(item => item![member]?.GetValue<string>()).ToList();

    // ---- a private database: an inline, a keyed and an element-wise resolve ------------------------------

    private const string Customer = """{ "resolve": { "path": "customerId", "as": "customer", "select": ["name"], "outcomeAs": "customerOutcome"MORE } }""";

    private static string Orders(string stages) => $$"""[ { "sort": [ { "number": "asc" } ] }, {{stages}} ]""";

    [Fact]
    public async Task An_inline_resolve_says_its_outcome_on_every_row_and_reports_nothing_it_did_not_report()
    {
        var (owned, host) = await JoinsOutcomesTests.StartAsync("o_inline");
        await using var _ = owned;
        await using var __ = host;

        var answer = await host.SendAsync("oc.order", Orders(Customer.Replace("MORE", "")));

        Outcomes(answer, "customerOutcome").Should().Equal(["resolved", "not_found", "reference_null"], "a customer, one that is gone, and no customer at all");
        answer.Items.Select(item => item!["customer"]?["name"]?.GetValue<string>()).Should().Equal("Alice", null, null);
        answer.ShouldHaveNoDiagnostics("the member changes no diagnostic: onMissing is still null");

        // The projection decides alone: only whether the customer exists.
        var only = await host.SendAsync("oc.order", Orders(Customer.Replace("MORE", "") + """, { "project": { "number": 1, "customerOutcome": 1 } }"""));

        only.ShouldBeOk().Items.Select(item => item!.AsObject().Select(pair => pair.Key)).Should().AllBeEquivalentTo(new[] { "id", "number", "customerOutcome" });
        Outcomes(only, "customerOutcome").Should().Equal("resolved", "not_found", "reference_null");
    }

    [Fact]
    public async Task An_inline_outcome_filters_groups_and_counts_before_the_page()
    {
        var (owned, host) = await JoinsOutcomesTests.StartAsync("o_inline_reads");
        await using var _ = owned;
        await using var __ = host;

        var broken = await host.SendAsync("oc.order", """
            [ { "resolve": { "path": "customerId", "as": "customer", "outcomeAs": "customerOutcome" } },
              { "match": { "customerOutcome": { "eq": "not_found" } } },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        broken.ShouldBeOk().Items.Select(item => item!["number"]!.GetValue<string>()).Should().Equal(["O-32"], "broken lines only");
        broken.TotalCount.Should().Be(1, "the count counts what the condition keeps");

        var unresolved = await host.SendAsync("oc.order", Orders(Customer.Replace("MORE", "") + """, { "match": { "customerOutcome": { "in": ["not_found", "reference_null"] } } }"""));

        unresolved.ShouldBeOk().Items.Select(item => item!["number"]!.GetValue<string>()).Should().Equal("O-32", "O-33");

        var perOutcome = await host.SendAsync("oc.order", """
            [ { "resolve": { "path": "customerId", "as": "customer", "outcomeAs": "customerOutcome" } },
              { "group": { "by": [ { "path": "customerOutcome", "as": "outcome" } ], "fields": { "orders": { "count": true } } } },
              { "sort": [ { "outcome": "asc" } ] } ]
            """);

        perOutcome.ShouldBeOk().Items.Select(item => (item!["outcome"]!.GetValue<string>(), item["orders"]!.ToString()))
            .Should().Equal(("not_found", "1"), ("reference_null", "1"), ("resolved", "1"));
    }

    [Fact]
    public async Task A_filter_tells_an_excluded_customer_from_a_missing_one()
    {
        var (owned, host) = await JoinsOutcomesTests.StartAsync("o_inline_filter");
        await using var _ = owned;
        await using var __ = host;

        var answer = await host.SendAsync("oc.order", Orders(Customer.Replace("MORE", """, "filter": { "name": { "eq": "Bob" } }""")));

        Outcomes(answer, "customerOutcome").Should().Equal(["excluded", "not_found", "reference_null"], "Alice exists and is not Bob; the second order's customer does not exist");
        answer.Items.Should().OnlyContain(item => item!["customer"] == null);
        answer.Items.SelectMany(item => item!.AsObject().Select(pair => pair.Key)).Should().NotContain(name => name.Contains("__", StringComparison.Ordinal), "the fields the join worked with never reach the row");
    }

    [Fact]
    public async Task A_target_field_that_is_not_the_key_says_ambiguous_and_the_ambiguity_is_reported()
    {
        var (owned, host) = await JoinsOutcomesTests.StartAsync("o_inline_ambiguous");
        await using var _ = owned;
        await using var __ = host;
        const string ByName = """{ "resolve": { "path": "customerName", "as": "namesake", "select": ["name"]MORE } }""";

        var silent = await host.SendAsync("oc.order", Orders(ByName.Replace("MORE", "")));

        silent.ShouldBeOk().ShouldHaveNoDiagnostics("without the member nobody looks for a second record");

        var answer = await host.SendAsync("oc.order", Orders(ByName.Replace("MORE", """, "outcomeAs": "namesakeOutcome" """)));

        Outcomes(answer, "namesakeOutcome").Should().Equal(["resolved", "ambiguous", "not_found"], "one Alice, two Twins, no Nobody");
        answer.DiagnosticCodes.Should().Equal(["RESOLVE_AMBIGUOUS"], "a stage that names its outcome reads its outcomes, and an ambiguity that is seen is always reported");
        answer.Items[1]!["namesake"]!["name"]!.GetValue<string>().Should().Be("Twin", "the first by key is taken");
    }

    [Fact]
    public async Task A_keyed_and_an_element_wise_resolve_say_their_outcome_after_the_page()
    {
        var (owned, host) = await JoinsOutcomesTests.StartAsync("o_keyed");
        await using var _ = owned;
        await using var __ = host;

        var answer = await host.SendAsync("oc.order", Orders("""
            { "resolve": { "path": "lineId", "as": "line", "select": ["code"], "outcomeAs": "lineOutcome" } },
            { "resolve": { "path": "customerIds", "as": "customers", "elements": "all", "outcomeAs": "customersOutcome" } },
            { "resolve": { "path": "customerIds", "as": "firstCustomer", "elements": "first", "outcomeAs": "firstOutcome" } }
            """));

        Outcomes(answer, "lineOutcome").Should().Equal(["resolved", "ambiguous", "not_found"], "a line one depot holds, one two hold, one none holds");
        Outcomes(answer, "customersOutcome").Should().Equal(["resolved", "resolved", "reference_null"], "the fold of the elements: some resolved, or an empty collection");
        Outcomes(answer, "firstOutcome").Should().Equal("resolved", "resolved", "reference_null");
        answer.DiagnosticCodes.Should().BeEquivalentTo(["RESOLVE_AMBIGUOUS", "RESOLVE_TRUNCATED"], "what was reported before, and no RESOLVE_MISSING: onMissing is null");

        var filtered = await host.SendAsync("oc.order", """
            [ { "resolve": { "path": "lineId", "as": "line", "outcomeAs": "lineOutcome" } }, { "match": { "lineOutcome": { "eq": "not_found" } } } ]
            """);

        filtered.ShouldRefuse("RESOLVE_NOT_FILTERABLE", 400)["stage"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Strict_refuses_a_missing_reference_unless_the_stage_reports_it_and_then_the_member_shows_it()
    {
        var (owned, host) = await JoinsOutcomesTests.StartAsync("o_strict");
        await using var _ = owned;
        await using var __ = host;

        JsonObject Strict(string more)
        {
            var body = Json.Request("oc.order", JsonNode.Parse(Orders(Customer.Replace("MORE", more)))!);

            body["strict"] = true;

            return body;
        }

        (await host.PostAsync("OxQL/query", Strict("").ToJsonString(), Org.A.Id())).ShouldRefuse("RESOLVE_MISSING", 422);

        var reported = await host.PostAsync("OxQL/query", Strict(""", "onMissing": "report" """).ToJsonString(), Org.A.Id());

        Outcomes(reported, "customerOutcome").Should().Equal("resolved", "not_found", "reference_null");
        reported.ShouldHaveDiagnostic("RESOLVE_MISSING")["params"]!["count"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task The_member_travels_through_cursor_pages_and_an_outcome_never_orders_one()
    {
        var (owned, host) = await JoinsOutcomesTests.StartAsync("o_paging");
        await using var _ = owned;
        await using var __ = host;
        const string Stages = """{ "resolve": { "path": "customerId", "as": "customer", "outcomeAs": "customerOutcome" } }, { "project": { "number": 1, "customerOutcome": 1 } }, { "sort": [ { "number": "asc" } ] }""";

        var first = await host.SendAsync("oc.order", $$"""[ {{Stages}}, { "page": { "limit": 2 } } ]""");

        Outcomes(first, "customerOutcome").Should().Equal("resolved", "not_found");
        first.HasNextPage.Should().BeTrue();

        var second = await host.SendAsync("oc.order", $$"""[ {{Stages}}, { "page": { "limit": 2, "cursor": "{{first.NextCursor}}" } } ]""");

        Outcomes(second, "customerOutcome").Should().Equal(["reference_null"], "the cursor is the sort's and the key's; the member rides in the row");

        (await host.SendAsync("oc.order", """[ { "resolve": { "path": "customerId", "as": "customer", "outcomeAs": "customerOutcome" } }, { "sort": [ { "customerOutcome": "asc" } ] } ]"""))
            .ShouldRefuse("RESOLVE_NOT_SORTABLE", 400);
        (await host.SendAsync("oc.order", """[ { "resolve": { "path": "customerId", "as": "customer", "outcomeAs": "customerOutcome" } }, { "match": { "customerOutcome": { "eq": "gone" } } } ]"""))
            .ShouldRefuse("UNKNOWN_ENUM_MEMBER", 400)["params"]!["values"]!.AsArray().Should().HaveCount(8);
    }

    // ---- the report fleet: remote, continued and union joins -------------------------------------------

    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    /// <summary>
    /// The lines of an invoice with the ERP line (inline), its source line and the owning shipment or tour
    /// (remote), the shipment's delivering tour (continued for one target) and the vehicle (a union join),
    /// each naming its outcome.
    /// </summary>
    internal static string Chain(Guid transaction, string more = "", bool strict = false) => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{Id(transaction)}}" },{{(strict ? " \"strict\": true," : "")}}
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine", "outcomeAs": "erpLineOutcome"{{more}} } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent", "outcomeAs": "sourceLineOutcome"{{more}} } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first", "outcomeAs": "deliveringTourOutcome"{{more}} } },
            { "resolve": { "as": "vehicle", "outcomeAs": "vehicleOutcome"{{more}},
                           "byTarget": { "{{ReportSeed.Shipment}}": "deliveringTour.resource.id", "{{ReportSeed.Tour}}": "sourceParent.resource.id" } } },
            { "project": { "position": 1, "erpLineOutcome": 1, "sourceParent.id": 1, "sourceLineOutcome": 1, "deliveringTourOutcome": 1,
                           "vehicle.matchCode": 1, "vehicleOutcome": 1 } },
            { "sort": [ { "position": "asc" } ] }
          ]
        }
        """;

    [Fact]
    public async Task Every_join_of_a_chain_says_its_outcome_and_the_origin_says_it_for_the_rows_no_owner_ran_the_stage_for()
    {
        var answer = await (await LedgerClient()).QueryAsync(Chain(ReportSeed.TransactionId));
        var rows = answer.ShouldBeOk().Items.OfType<JsonObject>().ToList();
        var tours = rows.Select(row => row["sourceParent"]!["entity"]!.GetValue<string>() == ReportSeed.Tour).ToList();

        tours.Should().Equal([false, true, false, false], "two lines of the scenario shipment, the tour line, and the line of the shipment on the carrier tour");
        Outcomes(answer, "erpLineOutcome").Should().OnlyContain(outcome => outcome == "resolved");
        Outcomes(answer, "sourceLineOutcome").Should().OnlyContain(outcome => outcome == "resolved");
        Outcomes(answer, "deliveringTourOutcome").Should().Equal(["resolved", "not_applicable", "resolved", "resolved"], "the stage is the shipments': a tour line never had one");
        Outcomes(answer, "vehicleOutcome").Should().Equal(["resolved", "resolved", "resolved", "excluded"], "each branch's own; a carrier is a resource no case of the reference selects");
        rows.Select(row => row["vehicle"] is JsonObject).Should().Equal(true, true, true, false);
        answer.DiagnosticCodes.Should().NotContain("RESOLVE_MISSING");
    }

    [Fact]
    public async Task A_source_line_that_is_gone_is_not_found_and_everything_continued_from_it_is_reference_null()
    {
        var answer = await (await LedgerClient()).QueryAsync(Chain(ReportSeed.MissingSourceTransactionId));

        Outcomes(answer, "erpLineOutcome").Should().Equal("resolved");
        Outcomes(answer, "sourceLineOutcome").Should().Equal("not_found");
        Outcomes(answer, "deliveringTourOutcome").Should().Equal(["reference_null"], "an earlier hop was null: this host says so, no owner saw the row");
        Outcomes(answer, "vehicleOutcome").Should().Equal("reference_null");
        answer.DiagnosticCodes.Should().NotContain("RESOLVE_MISSING", "the member is not a report: onMissing is null");

        var ambiguous = await (await LedgerClient()).QueryAsync(Chain(ReportSeed.AmbiguousSourceTransactionId));

        Outcomes(ambiguous, "sourceLineOutcome").Should().Equal("ambiguous");
        ambiguous.DiagnosticCodes.Should().Contain("RESOLVE_AMBIGUOUS");
    }

    [Fact]
    public async Task Under_strict_a_gone_source_line_refuses_and_with_onMissing_report_the_row_shows_not_found()
    {
        var ledger = await LedgerClient();

        (await ledger.QueryAsync(Chain(ReportSeed.MissingSourceTransactionId, strict: true))).ShouldRefuse("RESOLVE_MISSING", 422);

        var reported = await ledger.QueryAsync(Chain(ReportSeed.MissingSourceTransactionId, more: """, "onMissing": "report" """, strict: true));

        Outcomes(reported, "sourceLineOutcome").Should().Equal(["not_found"], "the report prints 'missing' where the page would have been refused");
        reported.ShouldHaveDiagnostic("RESOLVE_MISSING")["params"]!["alias"]!.GetValue<string>().Should().Be("sourceLine");
    }

    [Fact]
    public async Task Explain_says_the_member_its_type_its_values_and_where_its_join_runs()
    {
        var ledger = await LedgerClient();
        var explained = await ledger.ExplainHereAsync(Chain(ReportSeed.TransactionId));
        var answer = explained.Body!;

        answer["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);

        answer["aliases"]!["erpLine"]!["outcome"]!.ToJsonString().Should().Be("""{"as":"erpLineOutcome","values":["not_found"]}""");
        answer["aliases"]!["erpLineOutcome"]!.ToJsonString().Should().Be(
            """{"stage":3,"node":"scalar","kind":"string","outcomeOf":"erpLine","heldBy":"ledger","complete":true,"lookupOn":false,"values":["resolved","reference_null","not_found"],"type":"k:string"}""");
        answer["aliases"]!["sourceLineOutcome"]!["values"]!.AsArray().Select(value => value!.GetValue<string>())
            .Should().Equal("resolved", "ambiguous", "reference_null", "excluded", "not_found", "invalid_key", "owner_unanswered");
        answer["aliases"]!["vehicle"]!["outcome"]!["as"]!.GetValue<string>().Should().Be("vehicleOutcome");
        answer["aliases"]!["vehicleOutcome"]!["outcomeOf"]!.GetValue<string>().Should().Be("vehicle");
        answer["aliases"]!["vehicleOutcome"]!["heldBy"]!.GetValue<string>().Should().Be("transport");
        answer["aliases"]!["vehicleOutcome"]!["values"]!.AsArray().Should().HaveCount(8, "a continued stage may also be not applicable");

        answer["stages"]![3]!["creates"]!.ToJsonString().Should().Be("""["erpLine","erpLineOutcome"]""");
        answer["stages"]![6]!["creates"]!.ToJsonString().Should().Be("""["vehicle","vehicleOutcome"]""");
        answer["stages"]![6]!["shape"]!["roots"]!["vehicleOutcome"]!.GetValue<string>().Should().Be("k:string");

        var columns = answer["result"]!["columns"]!.AsArray().OfType<JsonObject>().ToDictionary(column => column["path"]!.GetValue<string>());

        foreach (var member in new[] { "erpLineOutcome", "sourceLineOutcome", "deliveringTourOutcome", "vehicleOutcome" })
            columns[member].ToJsonString().Should().Contain("\"kind\":\"string\",\"nullable\":false").And.Contain("\"root\":\"\",\"present\":\"always\"");

        answer["notes"]!.AsArray().Where(note => note!["code"]!.GetValue<string>() == "MISSING_POLICY").Select(note => note!["params"]!["outcomeAs"]!.GetValue<string>())
            .Should().Equal("erpLineOutcome", "sourceLineOutcome", "deliveringTourOutcome", "vehicleOutcome");

        // The rules of the roots: an inline outcome is compared and grouped by, one written after the page is projected only.
        var inline = answer["rules"]![answer["stages"]![3]!["shape"]!["rules"]!["erpLineOutcome"]!.GetValue<string>()]!["self"]!.GetValue<string>();
        var after = answer["rules"]![answer["stages"]![6]!["shape"]!["rules"]!["vehicleOutcome"]!.GetValue<string>()]!["self"]!.GetValue<string>();

        Convert.ToInt32(inline, 16).Should().Be((1 << 0) | (1 << 1) | (1 << 6) | (1 << 7) | (1 << 16) | (1 << 18), "eq, neq, in, nin; groupable, projectable; never sortable");
        Convert.ToInt32(after, 16).Should().Be(1 << 18, "projectable");

        // A condition on the inline outcome keeps its join before the page; the run agrees.
        var filtered = Chain(ReportSeed.TransactionId).Replace("""{ "project":""", """{ "match": { "erpLineOutcome": { "eq": "resolved" } } }, { "project":""", StringComparison.Ordinal);
        var placed = await ledger.ExplainHereAsync(filtered);

        placed.Body!["valid"]!.GetValue<bool>().Should().BeTrue(placed.Text);
        placed.Body!["stages"]![3]!["placement"]!.ToJsonString().Should().Be("""{"executor":"inline","phase":"beforePage","host":"ledger"}""");
        (await ledger.QueryAsync(filtered)).ShouldBeOk().Items.Should().HaveCount(4);
    }

    public static TheoryData<string> Requests => new()
    {
        Chain(ReportSeed.TransactionId),
        Chain(ReportSeed.MissingSourceTransactionId),
        Chain(ReportSeed.TransactionId).Replace("\"vehicleOutcome\": 1 }", "\"vehicleOutcome\": 1 } }, { \"match\": { \"vehicleOutcome\": { \"eq\": \"resolved\" } }", StringComparison.Ordinal),
        Chain(ReportSeed.TransactionId).Replace("\"sort\": [ { \"position\": \"asc\" } ]", "\"sort\": [ { \"erpLineOutcome\": \"asc\" } ]", StringComparison.Ordinal),
        Chain(ReportSeed.TransactionId).Replace("\"outcomeAs\": \"vehicleOutcome\"", "\"outcomeAs\": \"vehicle\"", StringComparison.Ordinal),
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
