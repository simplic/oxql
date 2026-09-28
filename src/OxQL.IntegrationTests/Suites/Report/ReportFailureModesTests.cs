using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The failure modes of DESIGN §8's fleet list over the report seeds of organisation R, each
/// outside strict and under strict, where the report suites and <c>Suites.Joins</c> do not already
/// hold them: a key two records hold in one chunk of a grouped owner answer beside keys that resolve
/// (no false <c>not_found</c>); the clerk two employees share, through the plain owner query of a
/// 2.0 remote resolve onto a non-key member (its ambiguity is seen only when the owner page happens
/// to hold both rows) and through the grouped query the same resolve sends once it reads its
/// outcomes (PRE-2b: the same answer cold and warm); a target the filter excludes, told apart
/// from a missing one by the existence probe; a flatten cut at its depth outside strict; the key
/// budget over an in-process chain; the 5 000-row report page and <c>PAGE_INCOMPLETE</c>;
/// <c>MAX_CONTINUED_STAGES_EXCEEDED</c> under strict. Every case gives its resolve a select of its
/// own, so no other case's cache entry answers it.
/// </summary>
[Trait("Category", "Integration")]
public class ReportFailureModesTests
{
    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    private static string Id(Guid id) => id.ToString("D");

    private static JsonObject Request(string entity, string pipeline, bool strict = false)
    {
        var body = Json.Request(entity, JsonNode.Parse(pipeline)!);

        if (strict)
            body["strict"] = true;

        return body;
    }

    private static IReadOnlyList<(int Row, string? Key, string? Outcome)> RowsOf(JsonObject diagnostic) =>
        diagnostic["params"]!["rows"]!.AsArray().Select(row => (row!["row"]!.GetValue<int>(), row["key"]?.GetValue<string>(), row["outcome"]?.GetValue<string>())).ToList();

    // ---- ambiguity and duplicate keys -------------------------------------------------------

    [Fact]
    public async Task R01_a_source_line_two_owners_hold_beside_lines_that_resolve_is_the_one_ambiguous_row_never_a_false_not_found_and_refused_under_strict()
    {
        var client = await LedgerClient();
        var pipeline = $$"""
            [ { "match": { "id": { "in": ["{{Id(ReportSeed.ErpLineIds[0])}}", "{{Id(ReportSeed.ErpLineIds[1])}}", "{{Id(ReportSeed.ErpLineIds[2])}}", "{{Id(ReportSeed.ErpLineIds[4])}}"] } } },
              { "sort": [ { "text": "asc" } ] },
              { "resolve": { "path": "sourceBillingLineReference.id", "as": "sourceLine", "select": ["id", "type"], "onMissing": "report" } },
              { "project": { "text": 1, "sourceLine": 1 } } ]
            """;

        var answer = await client.QueryAsync(Request(ReportSeed.BillingLine, pipeline));

        answer.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_AMBIGUOUS"], "one key two records hold; the others in the same chunk resolve, none missing");
        answer.Strings("text").Should().Equal("Duplicate source", "Freight Hamburg - Bremen", "Tour flat rate", "Waiting time");
        answer.Strings("sourceLine.id").Should().Equal(Id(ReportSeed.DuplicateLineId), Id(ReportSeed.ShipmentLineIds[0]), Id(ReportSeed.TourLineId), Id(ReportSeed.ShipmentLineIds[1]));
        RowsOf(answer.Diagnostics[0]).Should().Equal((0, Id(ReportSeed.DuplicateLineId), "ambiguous"));

        var refused = await client.QueryAsync(Request(ReportSeed.BillingLine, pipeline, strict: true));

        refused.ShouldRefuse(Codes.ResolveAmbiguous, 422);
        refused.ErrorCodes.Should().Equal([Codes.ResolveAmbiguous]);
    }

    [Fact]
    public async Task R02_PRE2_the_clerk_two_employees_share_alone_on_a_page_is_RESOLVE_PARTIAL_on_the_plain_query_and_RESOLVE_AMBIGUOUS_under_strict()
    {
        // A 2.0 request reads no outcome and keeps the plain owner query, which asks with a page of
        // one row per key: the owner's second employee is a next page. A strict request reads the
        // outcomes, so its query onto the non-key userId is grouped per key and sees both (PRE-2b).
        var client = await LedgerClient();
        var pipeline = $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.AmbiguousClerkTransactionId)}}" } } },
              { "resolve": { "path": "createUserId", "as": "clerk", "select": ["address.firstName", "SELECT"] } },
              { "project": { "number": 1, "clerk": 1 } } ]
            """;

        var answer = await client.QueryAsync(Request(ReportSeed.Transaction, pipeline.Replace("SELECT", "address.city")));

        answer.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_PARTIAL"], answer.ToString());
        answer.Diagnostics[0]["params"]!.ToJsonString().Should().Be("""{"alias":"clerk","keys":1,"unanswered":0,"service":"staff"}""");
        ReportSeed.DuplicateUserEmployeeIds.Select(Id).Should().Contain(answer.Strings("clerk.id").Single(), "one of the two employees is taken");

        var refused = await client.QueryAsync(Request(ReportSeed.Transaction, pipeline.Replace("SELECT", "address.street"), strict: true));

        refused.ShouldRefuse(Codes.ResolveAmbiguous, 422);
        refused.ErrorCodes.Should().Equal([Codes.ResolveAmbiguous], "strict names the ambiguity, whatever the owner's page holds");
    }

    [Fact]
    public async Task R03_PRE2b_the_shared_clerk_under_onMissing_is_ambiguous_whatever_the_owner_page_holds_and_the_same_cold_and_warm()
    {
        var client = await LedgerClient();
        var pipeline = $$"""
            [ { "match": { "id": { "in": [IDS] } } },
              { "resolve": { "path": "createUserId", "as": "clerk", "select": ["address.lastName", "SELECT"], "onMissing": "report" } },
              { "project": { "number": 1, "clerk": 1 } }, { "sort": [ { "number": "asc" } ] } ]
            """;
        string Ids(params Guid[] ids) => string.Join(", ", ids.Select(id => $"\"{Id(id)}\""));

        // Two keys, one of them shared: grouped per key, the owner answers both employees under the
        // shared clerk, so the ambiguity is seen although a plain page of two rows would have cut it.
        var two = await client.QueryAsync(Request(ReportSeed.Transaction,
            pipeline.Replace("IDS", Ids(ReportSeed.TransactionId, ReportSeed.AmbiguousClerkTransactionId)).Replace("SELECT", "address.zipcode")));

        two.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_AMBIGUOUS"], two.ToString());
        RowsOf(two.Diagnostics[0]).Should().Equal((1, Id(ReportSeed.DuplicateUserId), "ambiguous"));
        two.Strings("clerk.address.lastName").Should().Equal("Becker", "Krause");

        // Three keys, one missing: cold, then warm from the cache, then strict: the same ambiguity.
        var three = Request(ReportSeed.Transaction,
            pipeline.Replace("IDS", Ids(ReportSeed.TransactionId, ReportSeed.AmbiguousClerkTransactionId, ReportSeed.MissingClerkTransactionId)).Replace("SELECT", "address.countryIso"));
        var cold = await client.QueryAsync(three);
        var warm = await client.QueryAsync(three);

        foreach (var answer in new[] { cold, warm })
        {
            answer.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_AMBIGUOUS", "RESOLVE_MISSING"], answer.ToString());
            RowsOf(answer.Diagnostics[0]).Should().Equal((1, Id(ReportSeed.DuplicateUserId), "ambiguous"));
            RowsOf(answer.Diagnostics[1]).Should().Equal((2, Id(ReportSeed.UnknownUserId), "not_found"));
            answer.Strings("clerk.address.lastName").Should().Equal("Becker", "Krause", null);
        }

        three["strict"] = true;
        var strict = await client.QueryAsync(three);

        strict.ShouldRefuse(Codes.ResolveAmbiguous, 422);
        strict.ErrorCodes.Should().Equal([Codes.ResolveAmbiguous], "the cached repeat keeps both employees, so strict still sees the ambiguity; the missing clerk stays a diagnostic under onMissing report");
    }

    // ---- excluded, told apart from missing by the probe ---------------------------------------

    [Fact]
    public async Task R04_a_target_the_filter_excludes_is_null_without_RESOLVE_MISSING_under_report_and_passes_strict_while_an_unfiltered_one_resolves()
    {
        var client = await LedgerClient();
        var pipeline = $$"""
            [ { "match": { "id": { "in": ["{{Id(ReportSeed.ErpLineIds[0])}}", "{{Id(ReportSeed.ErpLineIds[1])}}"] } } },
              { "sort": [ { "text": "asc" } ] },
              { "resolve": { "path": "references.referenceId", "as": "shipment", "elements": "first", "target": "transport.shipment",
                             "select": ["shipmentNumber"], "filter": { "shipmentNumber": { "OP": "SN-2026-0001" } }ONMISSING } },
              { "project": { "text": 1, "shipment": 1 } } ]
            """;

        var excluded = await client.QueryAsync(Request(ReportSeed.BillingLine, pipeline.Replace("OP", "neq").Replace("ONMISSING", """, "onMissing": "report" """)));

        excluded.ShouldBeOk().ShouldHaveNoDiagnostics("the probe finds the shipment: excluded, which is no data loss");
        excluded.Values("shipment").Should().OnlyContain(alias => alias == null).And.HaveCount(2);

        var strict = await client.QueryAsync(Request(ReportSeed.BillingLine, pipeline.Replace("OP", "neq").Replace("ONMISSING", ""), strict: true));

        strict.ShouldBeOk().ShouldHaveNoDiagnostics("strict refuses data loss, and excluded is none");
        strict.Values("shipment").Should().OnlyContain(alias => alias == null);

        var resolved = await client.QueryAsync(Request(ReportSeed.BillingLine, pipeline.Replace("OP", "eq").Replace("ONMISSING", ""), strict: true));

        resolved.ShouldBeOk().Strings("shipment.shipmentNumber").Should().Equal(["SN-2026-0001", "SN-2026-0001"], "two rows holding the same key both resolve");
    }

    // ---- truncation --------------------------------------------------------------------------

    [Fact]
    public async Task R05_a_flatten_cut_at_its_depth_keeps_the_rows_it_reached_and_says_so_outside_strict()
    {
        var answer = await (await LedgerClient()).SendAsync(ReportSeed.Transaction, $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.DeepNestingTransactionId)}}" } } },
              { "unwind": { "path": "items", "flatten": "items", "as": "item" } },
              { "match": { "item": { "is": "BillingLineTransactionItem" } } },
              { "project": { "number": 1, "item.id": 1 } } ]
            """);

        answer.ShouldBeOk().ShouldHaveDiagnostic(Codes.UnwindDepthTruncated);
        answer.Items.Should().ContainSingle("the line at the top is reached; the one seven groups deep is below the depth of five");
    }

    // ---- budgets and pages -------------------------------------------------------------------

    [Fact]
    public async Task R06_the_key_budget_over_the_in_process_chain_leaves_source_lines_owner_unanswered_and_refuses_A1_under_strict()
    {
        var shared = await CorpusFleet.SharedAsync();
        var ledger = shared.Variant(LabService.Ledger, "e14b-resolve-keys-1", new Dictionary<string, string?> { ["OxQL:Limits:MaxResolveKeys"] = "1" }, Org.R);
        var request = ReportScenarios.Request("A1");

        var refused = await ledger.QueryAsync(request);

        refused.ShouldRefuse(Codes.ResolvePartial, 422)["params"]!["max"]!.GetValue<int>().Should().Be(1);

        var plain = request.DeepClone().AsObject();
        plain.Remove("strict");
        plain["pipeline"]!.AsArray().OfType<JsonObject>().Single(stage => stage.ContainsKey("page"))["page"]!["limit"] = 500;
        plain["pipeline"]![ReportScenarios.IndexOf(plain, "sourceLine")]!["resolve"]!["onMissing"] = "report";

        var answer = await ledger.QueryAsync(plain);

        answer.ShouldBeOk().ShouldHaveDiagnostic(Codes.ResolvePartial)["params"]!["max"]!.GetValue<int>().Should().Be(1);
        var missing = answer.ShouldHaveDiagnostic(Codes.ResolveMissing);
        RowsOf(missing).Should().NotBeEmpty().And.OnlyContain(row => row.Outcome == "owner_unanswered", "keys over the budget are unanswered, never not_found");
        answer.Items.Should().HaveCount(4, "the page is whole; only aliases over the budget are null");
    }

    [Fact]
    public async Task R07_a_strict_report_page_holds_5000_rows_and_one_more_matching_row_is_PAGE_INCOMPLETE_and_a_page_above_5000_is_refused_at_bind()
    {
        var shared = await CorpusFleet.SharedAsync();
        await shared.SeedBulkAsync();
        var transport = shared.Client(LabService.Transport, Org.C);
        string Pipeline(string last, int limit) => $$"""
            [ { "match": { "templateName": { "gte": "{{BulkRows.NameOf(1)}}", "lte": "{{last}}" } } },
              { "sort": [ { "templateName": "asc" } ] },
              { "project": { "templateName": 1 } },
              { "page": { "limit": {{limit}} } } ]
            """;

        var full = await transport.QueryAsync(Request(Corpus.Template, Pipeline(BulkRows.NameOf(5_000), 5_000), strict: true));

        full.ShouldBeOk().Items.Should().HaveCount(5_000);
        full.HasNextPage.Should().BeFalse();
        full.Strings("templateName").Last().Should().Be(BulkRows.NameOf(5_000));

        var incomplete = await transport.QueryAsync(Request(Corpus.Template, Pipeline(BulkRows.NameOf(5_001), 5_000), strict: true));

        incomplete.ShouldRefuse(Codes.PageIncomplete, 422)["params"]!.ToJsonString().Should().Be("""{"limit":5000,"max":5000}""");

        var outside = await transport.QueryAsync(Request(Corpus.Template, Pipeline(BulkRows.NameOf(5_001), 500)));

        outside.ShouldBeOk().HasNextPage.Should().BeTrue("outside strict a page is a page");

        (await transport.QueryAsync(Request(Corpus.Template, Pipeline(BulkRows.NameOf(5_001), 5_001), strict: true))).ShouldRefuse(Codes.PageSizeExceeded, 400);
        (await transport.QueryAsync(Request(Corpus.Template, Pipeline(BulkRows.NameOf(5_000), 5_000)))).ShouldRefuse(Codes.PageSizeExceeded, 400);
    }

    [Fact]
    public async Task R08_more_continued_stages_than_MaxContinuedStages_is_refused_the_same_under_strict()
    {
        var shared = await CorpusFleet.SharedAsync();
        var ledger = shared.Variant(LabService.Ledger, "e11-max-continued-1", new Dictionary<string, string?> { ["OxQL:Limits:MaxContinuedStages"] = "1" }, Org.R);

        var refused = await ledger.QueryAsync(Request(ReportSeed.Transaction, """
            [ { "resolve": { "path": "invoiceRecipient.address.id", "as": "c" } },
              { "resolve": { "path": "c.address.id", "as": "c1" } },
              { "resolve": { "path": "c.address.id", "as": "c2" } },
              { "page": { "limit": 10 } } ]
            """, strict: true));

        refused.ShouldRefuse(Codes.MaxContinuedStagesExceeded, 400)["stage"]!.GetValue<int>().Should().Be(2);
        refused.ErrorCodes.Should().Equal([Codes.MaxContinuedStagesExceeded]);
    }
}
