using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.AspNetCore.Legacy;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Legacy;

/// <summary>
/// The bounds of the legacy pipeline: when it answers and when it does not (every contract 1
/// request by default; the one switch, the contract header, compatibility off, <c>Always</c> off),
/// that every collection it touches is read for the caller's organisation only, whatever the order
/// of the request's stages, and what it answers where a request fails. Over
/// <see cref="LegacyFixture"/>'s documents; what the rows are is <see cref="LegacyGoldenTests"/>'.
/// </summary>
[Trait("Category", "Integration")]
public class LegacyPipelineTests
{
    private static readonly IReadOnlyDictionary<string, string?> Off = new Dictionary<string, string?> { ["OxQL:Compat:Legacy:Enabled"] = "false" };

    /// <summary>The rewrite first, as in the published 2.0 engine: the legacy pipeline answers only what the rewrite cannot express.</summary>
    private static readonly IReadOnlyDictionary<string, string?> RewriteFirst = LegacyGoldenTests.RewriteFirst;

    private const string Rewrite = "rewrite-first";

    private static string Id(int n) => LegacyFixture.Id(n).ToString("D");

    private static string Organisation => LegacyFixture.Organisation.ToString("D");

    private static string Foreign => LegacyFixture.Foreign.ToString("D");

    private static Task<LabClient> Ledger(string variant = "legacy", IReadOnlyDictionary<string, string?>? configuration = null) => LegacyFixture.ClientAsync(LabService.Ledger, variant, configuration);

    private static Task<LabClient> Transport(string variant = "legacy", IReadOnlyDictionary<string, string?>? configuration = null) => LegacyFixture.ClientAsync(LabService.Transport, variant, configuration);

    private static async Task<bool> LoggedAsync(LabClient client, string correlation) =>
        (await client.HostAsync()).Logs.Entries.Any(line => line.Category == LegacyRoute.LogCategory && line.Message.Contains(correlation, StringComparison.Ordinal));

    private const string SubTours = """
        { "lookup": { "from": "tour", "localPath": "GlobalTourId", "foreignPath": "GlobalTourId", "as": "SubTours", "filter": { "IsMirroredTour": { "eq": true } } } }
        """;

    // ── when it answers ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task P01_the_one_switch_off_refuses_what_the_rewrite_cannot_express_as_before()
    {
        var transport = await Transport("legacy-off", Off);
        var lookup = await transport.PostAsync("OxQL/query", LegacyGoldenTests.RequestText("report-tour-plan"));
        var error = lookup.ShouldRefuse("LEGACY_STAGE_UNSUPPORTED", 400);

        error["stage"]!.GetValue<int>().Should().Be(1);
        error["message"]!.GetValue<string>().Should().Contain("Compat:Legacy:Enabled").And.Contain("a reference the service's model declares");

        var ledger = await Ledger("legacy-off", Off);

        (await ledger.PostAsync("OxQL/query", LegacyGoldenTests.RequestText("unmodelled-only"))).ShouldRefuse("UNKNOWN_PATH", 400);
    }

    [Fact]
    public async Task P02_a_request_under_the_contract_2_header_never_reaches_it()
    {
        var transport = (await Transport()).Contract(2);
        var correlation = $"legacy-p02-{Guid.NewGuid():N}";
        var answer = await transport.WithHeader(LabIdentity.CorrelationHeader, correlation).PostAsync("OxQL/query", LegacyGoldenTests.RequestText("report-tour-plan"));

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().NotContain("LEGACY_STAGE_UNSUPPORTED");
        (await LoggedAsync(transport, correlation)).Should().BeFalse("the legacy pipeline is contract 1's alone");
    }

    [Fact]
    public async Task P03_a_host_without_compatibility_has_no_contract_1_request_and_no_legacy_pipeline()
    {
        var transport = await Transport("compat-off", new Dictionary<string, string?> { ["OxQL:Compat:Enabled"] = "false", ["OxQL:Compat:Legacy:Always"] = "true" });
        var correlation = $"legacy-p03-{Guid.NewGuid():N}";
        var answer = await transport.WithHeader(LabIdentity.CorrelationHeader, correlation).PostAsync("OxQL/query", LegacyGoldenTests.RequestText("report-tour-plan"));

        answer.StatusCode.Should().Be(400, answer.ToString());
        (await LoggedAsync(transport, correlation)).Should().BeFalse();
    }

    [Fact]
    public async Task P04_with_always_off_a_condition_on_an_undeclared_path_alone_is_answered_as_the_engine_before_answered_it()
    {
        var ledger = await Ledger(Rewrite, RewriteFirst);
        var correlation = $"legacy-p04-{Guid.NewGuid():N}";
        var answer = await ledger.WithHeader(LabIdentity.CorrelationHeader, correlation).SendAsync("ledger.transaction", """[ { "match": { "ReportingInformation.Headline": { "eq": "Rechnung" } } } ]""");

        // A stored member nobody modelled: the rows that hold the value, whole.
        answer.ShouldBeOk();
        answer.Items.Should().ContainSingle().Which!["Number"]!.GetValue<string>().Should().Be("RE-2026-0042");
        (await LoggedAsync(ledger, correlation)).Should().BeTrue("the legacy pipeline answered it, with the reason 'path'");

        // A name nothing stores matches no row.
        var unknown = await ledger.SendAsync("ledger.transaction", """[ { "match": { "nope": { "eq": 1 } } } ]""");

        unknown.ShouldBeOk();
        unknown.Items.Should().BeEmpty();

        // Under contract 2 the same condition is the refusal it is for every caller.
        (await ledger.Contract(2).SendAsync("ledger.transaction", """[ { "match": { "nope": { "eq": 1 } } } ]""")).ShouldRefuse("UNKNOWN_PATH", 400);

        // The same condition beside a stage that reads the member is the legacy pipeline's, whole.
        var read = await ledger.SendAsync("ledger.transaction", """[ { "match": { "ReportingInformation.Headline": { "eq": "Rechnung" } } }, { "project": { "Number": 1, "ReportingInformation": { "Headline": 1 } } } ]""");

        read.ShouldBeOk();
        read.Items.Should().ContainSingle().Which!["Number"]!.GetValue<string>().Should().Be("RE-2026-0042");
    }

    [Fact]
    public async Task P05_a_host_that_sets_nothing_answers_every_contract_1_request_as_the_engine_before_contract_2()
    {
        // The host of this client sets the page sizes of the recording and nothing of the legacy pipeline: this is the default.
        var ledger = await Ledger();
        var correlation = $"legacy-p05-{Guid.NewGuid():N}";
        var answer = await LegacyGoldenTests.AnswerAsync("plain-page", ledger, correlation);
        var recorded = (await LegacyGoldenTests.RecordedAsync("plain-page"))["body"]!;

        new OxQL.Core.Models.OxQLOptions().Compat.Legacy.Always.Should().BeTrue("a caller without the contract header gets the legacy pipeline's answer");

        answer.ShouldBeOk();
        answer.Body!.ToJsonString().Should().Be(recorded.ToJsonString(), "the rows, the cursor and the count are the recorded ones, text for text: a request the rewrite could express too");
        answer.PageInfo.ContainsKey("totalCountCapped").Should().BeFalse("the count is the legacy pipeline's: every matching row");
        (await ledger.HostAsync()).Logs.Entries.Should().Contain(line => line.Category == LegacyRoute.LogCategory && line.Message.Contains(correlation, StringComparison.Ordinal) && line.Message.Contains("reason=always", StringComparison.Ordinal));

        // And nothing is refused for a path: a condition on a member nobody stored matches no row.
        var unknown = await ledger.SendAsync("ledger.transaction", """[ { "match": { "nope": { "eq": 1 } } } ]""");

        unknown.ShouldBeOk();
        unknown.Items.Should().BeEmpty();

        var stored = await ledger.SendAsync("ledger.transaction", """[ { "match": { "ReportingInformation.Headline": { "eq": "Rechnung" } } }, { "project": { "Number": 1 } } ]""");

        stored.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task P06_a_batch_answers_each_query_by_the_path_that_answers_it_alone()
    {
        var bodies = new[] { "report-invoice-pricing", "plain-page", "unknown-type" };
        var pricing = (await LegacyGoldenTests.RecordedAsync("report-invoice-pricing"))["body"]!;
        var page = (await LegacyGoldenTests.RecordedAsync("plain-page"))["body"]!;

        // The default: every entry is the legacy pipeline's, read as it was written.
        var batch = await (await Ledger()).BatchAsync(bodies.Select(name => (object)JsonNode.Parse(LegacyGoldenTests.RequestText(name))!));

        batch.StatusCode.Should().Be(200, batch.ToString());
        batch.Results[0].Items.Select(row => row!.ToJsonString()).Order(StringComparer.Ordinal)
            .Should().Equal(pricing["items"]!.AsArray().Select(row => row!.ToJsonString()).Order(StringComparer.Ordinal), "the legacy pipeline reads the entry as it was written");
        batch.Results[1].Body!.ToJsonString().Should().Be(page.ToJsonString(), "the second entry is the recorded answer too: its cursor, its count");
        batch.Results[2].ErrorCodes.Should().Equal("UNKNOWN_TYPE");
        batch.Results[2].Body!["status"]!.GetValue<int>().Should().Be(400, "a legacy refusal repeats the status in its body");

        // With Always off each entry goes the way it goes alone: the rewrite answers what it can express.
        var rewritten = await (await Ledger(Rewrite, RewriteFirst)).BatchAsync(bodies.Select(name => (object)JsonNode.Parse(LegacyGoldenTests.RequestText(name))!));

        rewritten.StatusCode.Should().Be(200, rewritten.ToString());
        rewritten.Results[0].Items.Select(row => row!.ToJsonString()).Order(StringComparer.Ordinal)
            .Should().Equal(pricing["items"]!.AsArray().Select(row => row!.ToJsonString()).Order(StringComparer.Ordinal));
        rewritten.Results[1].ShouldBeOk();
        rewritten.Results[1].PageInfo.ContainsKey("totalCountCapped").Should().BeTrue("the second entry is the rewrite's");
        rewritten.Results[2].ErrorCodes.Should().Equal("UNKNOWN_TYPE");
    }

    [Fact]
    public async Task P06b_a_batch_that_writes_its_queries_twice_is_read_from_the_ones_it_was_bound_from()
    {
        var ledger = await Ledger();
        var first = LegacyGoldenTests.RequestText("unknown-type");
        var last = LegacyGoldenTests.RequestText("plain-page");
        var batch = await ledger.BatchBodyAsync($$"""{ "queries": [ {{first}} ], "maxTimeMs": 5000, "queries": [ {{last}} ] }""");

        batch.StatusCode.Should().Be(200, batch.ToString());
        batch.Results.Should().ContainSingle().Which.ShouldBeOk("the host binds the last 'queries' member, and that entry is the one the legacy pipeline reads");
        batch.Results[0].Items.Should().ContainSingle().Which!["Number"]!.GetValue<string>().Should().Be("RE-2026-0043");
    }

    [Fact]
    public async Task P07_the_answer_says_in_the_log_that_the_legacy_pipeline_gave_it_and_why()
    {
        var always = await Transport();
        var whole = $"legacy-p07-{Guid.NewGuid():N}";

        (await always.WithHeader(LabIdentity.CorrelationHeader, whole).PostAsync("OxQL/query", LegacyGoldenTests.RequestText("op-eq"))).ShouldBeOk();
        (await always.HostAsync()).Logs.Entries.Where(line => line.Category == LegacyRoute.LogCategory).Select(line => line.Message)
            .Should().Contain(line => line.Contains(whole, StringComparison.Ordinal) && line.Contains("reason=always", StringComparison.Ordinal) && line.Contains("transport.tour", StringComparison.Ordinal));

        // With Always off the reason is what the rewrite could not express.
        var transport = await Transport(Rewrite, RewriteFirst);
        var correlation = $"legacy-p07-{Guid.NewGuid():N}";

        (await transport.WithHeader(LabIdentity.CorrelationHeader, correlation).PostAsync("OxQL/query", LegacyGoldenTests.RequestText("report-tour-plan"))).ShouldBeOk();
        (await transport.WithHeader(LabIdentity.CorrelationHeader, correlation + "-path").PostAsync("OxQL/query", LegacyGoldenTests.RequestText("page-1"))).ShouldBeOk();

        var lines = (await transport.HostAsync()).Logs.Entries.Where(line => line.Category == LegacyRoute.LogCategory).Select(line => line.Message).ToList();

        lines.Should().Contain(line => line.Contains(correlation, StringComparison.Ordinal) && line.Contains("reason=lookup", StringComparison.Ordinal) && line.Contains("transport.tour", StringComparison.Ordinal));
        lines.Should().Contain(line => line.Contains(correlation + "-path", StringComparison.Ordinal) && line.Contains("reason=path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task P08_explain_has_no_legacy_form_and_says_who_answers_the_query()
    {
        var transport = await Transport();
        var answer = await transport.ExplainHereAsync(JsonNode.Parse(LegacyGoldenTests.RequestText("report-tour-plan"))!);

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Body!["valid"]!.GetValue<bool>().Should().BeFalse();
        answer.Body!["errors"]!.AsArray().Single()!["message"]!.GetValue<string>().Should().Contain("legacy pipeline").And.Contain("an explain of it is not");
    }

    [Fact]
    public async Task P09_health_lists_for_a_caller_without_the_header_what_the_path_that_answers_it_honours()
    {
        const string SecondByOffset = """[ { "sort": [ { "Number": "asc" } ] }, { "page": { "limit": 1, "offset": 1 } } ]""";
        const string First = """[ { "sort": [ { "Number": "asc" } ] }, { "page": { "limit": 1 } } ]""";

        static List<string> Names(JsonNode? list) => list!.AsArray().Select(name => name!.GetValue<string>()).ToList();

        // A host that sets nothing: the legacy pipeline answers, the page is read by cursor only, and health says so.
        var ledger = await Ledger();
        var health = (await ledger.HealthAsync()).Body!;

        Names(health["capabilities"]).Should().Equal("batch", "compat.v1");
        health["limits"]!.AsObject().Select(member => member.Key).Should().Equal(
            "maxPageSize", "defaultPageSize", "maxPipelineStages", "maxLookupStages", "maxUnwindStages", "maxGroupFields", "maxProjectionFields", "maxRequestBytes", "maxBatchQueries", "regexMaxLength");
        health["limits"]!["maxPageSize"]!.GetValue<int>().Should().Be(500);
        health["engine"]!["contract"]!.GetValue<int>().Should().Be(2, "what the host is does not depend on the caller");
        OxQL.Core.Engine.RemoteOwnerInfo.FromShallowHealth((await ledger.HealthAsync(shallow: true)).Body)!.MaxBatchQueries.Should().NotBeNull("a host that reads this one's shallow health without the header sizes its batches by it");

        var first = await ledger.SendAsync("ledger.transaction", First);
        var offset = await ledger.SendAsync("ledger.transaction", SecondByOffset);

        offset.ShouldBeOk();
        offset.Items.Single()!["Number"]!.GetValue<string>().Should().Be(first.Items.Single()!["Number"]!.GetValue<string>(), "an offset is no member of this page: the first page is answered, and the cursor leads on");
        offset.PageInfo["nextCursor"]!.GetValue<string>().Should().Be(first.PageInfo["nextCursor"]!.GetValue<string>());

        // The same host asked under contract 2: the contract 2 engine's lists.
        var current = (await ledger.Contract(2).HealthAsync()).Body!;

        Names(current["capabilities"]).Should().Contain(["lookup.remote", "explain", "compat.v1"]).And.NotContain(["batch", "page.offset"]);
        current["limits"]!.AsObject().Count.Should().Be(49, "the contract 2 list, with what was added to it since a caller without the header first read its own");

        // The rewrite first: it pages by offset for a caller without the header, and health lists that.
        var rewrite = await Ledger(Rewrite, RewriteFirst);
        var listed = (await rewrite.HealthAsync()).Body!;

        Names(listed["capabilities"]).Should().StartWith(["batch", "group.page", "page.offset", "any"]);
        listed["limits"]!.AsObject().Count.Should().Be(48);
        listed["limits"]!.AsObject().ContainsKey("maxOffset").Should().BeTrue();
        (await rewrite.SendAsync("ledger.transaction", SecondByOffset)).Items.Single()!["Number"]!.GetValue<string>().Should().NotBe(first.Items.Single()!["Number"]!.GetValue<string>());
        (await rewrite.Contract(2).HealthAsync()).Body!["capabilities"]!.ToJsonString().Should().Be(current["capabilities"]!.ToJsonString(), "the switch changes nothing for a contract 2 caller");
    }

    // ── the organisation ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S01_a_request_without_an_organisation_is_refused()
    {
        var transport = (await Transport()).As((Guid?)null);

        (await transport.PostAsync("OxQL/query", LegacyGoldenTests.RequestText("report-tour-plan"))).ShouldRefuse("ACCESS_DENIED", 403);
    }

    [Fact]
    public async Task S02_no_row_and_no_joined_document_is_another_organisations_or_nobodys()
    {
        var invoice = await LegacyGoldenTests.AnswerAsync("report-invoice");
        var employee = await LegacyGoldenTests.AnswerAsync("report-hr");
        var tours = await LegacyGoldenTests.AnswerAsync("report-tour-plan");

        invoice.ShouldBeOk();
        invoice.Text.Should().NotContain("FOREIGN").And.NotContain("NO-ORGANISATION").And.NotContain(Foreign);
        invoice.Text.Should().Contain("S-1001").And.Contain("TR-2026-0101").And.Contain("S-1005", "a document whose organisation is stored as text is the caller's too");

        employee.ShouldBeOk();
        employee.Items.Select(row => row!["Holidays"]?["Entitlement"]?.GetValue<int>()).Should().OnlyContain(days => days == 30, "the holidays of the same employee key under another organisation and under none are not read");

        tours.ShouldBeOk();
        tours.Text.Should().NotContain("FOREIGN");
        tours.Items.Select(row => row!["Number"]!.GetValue<string>()).Should().Equal("TR-2026-0101", "TR-M-1", "TR-M-2", "TR-2026-0102");
    }

    [Fact]
    public async Task S03_the_caller_cannot_name_another_organisation()
    {
        var ledger = await Ledger();

        var named = await ledger.SendAsync("ledger.transaction", $$"""[ { "match": { "OrganizationId": { "eq": "{{Foreign}}" } } }, { "project": { "Number": 1, "ReportingInformation": 1 } } ]""");

        named.ShouldBeOk();
        named.Items.Should().BeEmpty();

        // A caller's variable is a variable like any other, whatever its name: the organisation is the request scope's.
        var variable = await ledger.QueryAsync(JsonNode.Parse($$"""
            { "entityType": "ledger.transaction", "variables": { "__oxql_OrganizationId": "{{Foreign}}" },
              "pipeline": [ { "match": { "TransactionYear": { "eq": 2026 } } }, { "project": { "Number": 1, "ReportingInformation": 1 } }, { "sort": [ { "Number": "asc" } ] } ] }
            """)!);

        variable.Items.Select(row => row!["Number"]!.GetValue<string>()).Should().Equal("RE-2026-0042", "RE-2026-0043");

        // Nor can a lookup into the queried collection itself read past the organisation.
        var self = await ledger.SendAsync("ledger.transaction", """
            [ { "match": { "Number": { "eq": "RE-2026-0042" } } },
              { "lookup": { "from": "transaction", "localPath": "TransactionYear", "foreignPath": "TransactionYear", "as": "SameYear" } },
              { "project": { "Number": 1, "SameYear": { "Number": 1 } } } ]
            """);

        self.Items.Single()!["SameYear"]!.AsArray().Select(row => row!["Number"]!.GetValue<string>()).Should().BeEquivalentTo("RE-2026-0042", "RE-2026-0043");
    }

    [Fact]
    public async Task S04_the_organisation_is_matched_first_whatever_the_order_of_the_stages()
    {
        var ledger = await Ledger();

        // A request whose first stage is a group: the caller's organisation is matched before it.
        var grouped = await ledger.SendAsync("ledger.transaction", """
            [ { "group": { "by": [ { "path": "OrganizationId", "as": "OrganizationId" }, { "path": "TransactionYear", "as": "Year" } ], "fields": { "Count": { "count": true }, "Net": { "sum": "TotalPriceNet" } } } },
              { "match": { "Year": { "eq": 2026 } } } ]
            """);

        grouped.ShouldBeOk();

        var row = grouped.Items.Should().ContainSingle().Subject!;

        row["OrganizationId"]!.GetValue<string>().Should().Be(Organisation);
        row["Count"]!.GetValue<int>().Should().Be(2, "the caller's two invoices are grouped");
        row["Net"]!.GetValue<decimal>().Should().Be(1010m);

        // A request whose page stands before its match: the page is cut from the caller's documents.
        var paged = await ledger.SendAsync("ledger.transaction", """
            [ { "sort": [ { "Number": "asc" } ] }, { "page": { "limit": 1 } }, { "match": { "TransactionYear": { "eq": 2026 } } }, { "project": { "Number": 1, "ReportingInformation": { "Headline": 1 } } } ]
            """);

        paged.Items.Should().ContainSingle().Which!["Number"]!.GetValue<string>().Should().Be("RE-2026-0042", "the first of the caller's documents");

        // An alias that takes the organisation's name after the first stage changes nothing about what was read.
        var aliased = await ledger.SendAsync("ledger.transaction", $$"""
            [ { "unwind": { "path": "States", "as": "OrganizationId", "preserveNull": true } }, { "match": { "TransactionYear": { "eq": 2026 } } }, { "project": { "Number": 1, "ReportingInformation": { "Headline": 1 } } } ]
            """);

        aliased.ShouldBeOk();
        aliased.Text.Should().NotContain("FOREIGN").And.NotContain("ORPHAN");
    }

    [Fact]
    public async Task S05_the_collections_a_lookup_may_read_can_be_named()
    {
        var configuration = new Dictionary<string, string?> { ["OxQL:Compat:Legacy:AllowedLookupSources:0"] = "Tour" };
        var transport = await Transport("legacy-sources", configuration);
        var ledger = await Ledger("legacy-sources", configuration);

        (await transport.PostAsync("OxQL/query", LegacyGoldenTests.RequestText("report-tour-plan"))).ShouldBeOk("the collection is named, without regard to case");

        var refused = (await ledger.PostAsync("OxQL/query", LegacyGoldenTests.RequestText("report-invoice"))).ShouldRefuse("DISALLOWED_LOOKUP_SOURCE", 400);

        refused["path"]!.GetValue<string>().Should().Be("context_data");
    }

    [Fact]
    public async Task S06_a_collection_whose_documents_carry_no_organisation_is_not_read_through_a_lookup_nor_as_an_entity()
    {
        var transport = await Transport();
        var fleet = await LegacyFixture.FleetAsync();
        var stored = await (await fleet.Fleet.DatabaseAsync(LabService.Transport)).GetCollection<MongoDB.Bson.BsonDocument>("settings").CountDocumentsAsync(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty);

        stored.Should().Be(2, "the collection holds documents: one without the organisation's member, one with a null in it");

        // Joined on a value every one of its documents holds: nothing comes back.
        var joined = await LegacyGoldenTests.AnswerAsync("lookup-collection-without-organisation", transport);

        joined.ShouldBeOk();
        joined.Items.Should().ContainSingle().Which!["Settings"]!.AsArray().Should().BeEmpty("a document that does not carry the caller's organisation is never read, whatever the collection");
        joined.Text.Should().NotContain("NOBODYS-SETTING").And.NotContain("NULL-ORGANISATION-SETTING");

        // With a filter of the caller's, an 'or' that would hold for every document, and under 'convert': still nothing.
        foreach (var lookup in new[]
        {
            """{ "from": "settings", "localPath": "IsDeleted", "foreignPath": "IsDeleted", "as": "Settings", "filter": { "or": [ { "Name": { "exists": true } }, { "OrganizationId": { "exists": false } }, { "OrganizationId": { "eq": null } } ] } }""",
            """{ "from": "settings", "localPath": "IsDeleted", "foreignPath": "IsDeleted", "as": "Settings", "filter": { "not": { "OrganizationId": { "exists": true } } } }""",
            """{ "from": "settings", "localPath": "Number", "foreignPath": "Name", "as": "Settings", "convert": "stringToUuid", "filter": { "Secret": { "regex": "." } } }""",
        })
        {
            var answer = await transport.SendAsync("transport.tour", $$"""[ { "match": { "Number": { "eq": "TR-2026-0101" } } }, { "lookup": {{lookup}} }, { "project": { "Number": 1, "Settings": 1 } } ]""");

            answer.ShouldBeOk();
            answer.Items.Should().ContainSingle().Which!["Settings"]!.AsArray().Should().BeEmpty(lookup);
            answer.Text.Should().NotContain("SETTING");
        }

        // And a document of an entity's own collection that carries no organisation is no row (the ledger holds one: ORPHAN-0001).
        var orphan = await (await Ledger()).SendAsync("ledger.transaction", """[ { "match": { "Number": { "eq": "ORPHAN-0001" } } } ]""");

        orphan.ShouldBeOk();
        orphan.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task S07_a_path_with_a_dollar_is_refused_in_a_lookups_filter_as_everywhere()
    {
        var transport = await Transport();
        var refused = (await transport.SendAsync("transport.tour", """
            [ { "match": { "Number": { "eq": "TR-2026-0101" } } },
              { "lookup": { "from": "tour", "localPath": "GlobalTourId", "foreignPath": "GlobalTourId", "as": "SameTour", "filter": { "Status.$Name": { "eq": "planned" } } } } ]
            """)).ShouldRefuse("INVALID_PATH_DOLLAR", 400);

        refused["path"]!.GetValue<string>().Should().Be("Status.$Name");
        refused["message"]!.GetValue<string>().Should().Be("Path 'Status.$Name' contains '$' which is not allowed.", "the refusal every path of a contract 1 request gets");
    }

    // ── what failure looks like ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task F01_a_request_the_database_refuses_is_a_500_that_says_where_the_detail_is()
    {
        var transport = await Transport();
        var answer = await transport.SendAsync("transport.tour", $$"""[ {{SubTours}}, { "project": { "Number": 0 } } ]""");
        var recorded = await LegacyGoldenTests.RecordedAsync("project-empty");

        // A projection that keeps nothing: the database refuses it, and the answer is the recorded 500.
        recorded["status"]!.GetValue<int>().Should().Be(500);

        answer.StatusCode.Should().Be(500, answer.ToString());
        answer.Type.Should().Be("internal_error");
        answer.Body!["title"]!.GetValue<string>().Should().Be(recorded["body"]!["title"]!.GetValue<string>());
        answer.Body["status"]!.GetValue<int>().Should().Be(500);
        answer.ErrorCodes.Should().Equal("INTERNAL_ERROR");
        answer.Text.Should().NotContain("$project", "what the database said stays in the service log");
    }

    [Fact]
    public async Task F02_a_row_that_holds_a_legacy_uuid_is_a_coded_500()
    {
        var ledger = await Ledger();
        var answer = await ledger.SendAsync("ledger.transaction", $$"""
            [ { "match": { "id": { "eq": "{{LegacyFixture.SecondInvoiceId:D}}" } } },
              { "unwind": { "path": "Items", "as": "Item" } }, { "unwind": { "path": "Items.References", "as": "Ref" } },
              { "lookup": { "from": "context_data", "localPath": "Ref.ReferenceId", "foreignPath": "_id", "as": "ContextDatas", "convert": "stringToUuid" } } ]
            """);

        answer.StatusCode.Should().Be(500, answer.ToString());
        answer.Type.Should().Be("internal_error");
        answer.Body!["status"]!.GetValue<int>().Should().Be(500);
        answer.ErrorCodes.Should().Equal("INTERNAL_ERROR");
        answer.Body["errors"]![0]!["message"]!.GetValue<string>().Should().Contain("the contract 1 encoding does not write");
    }

    [Fact]
    public async Task F03_an_operand_its_operator_cannot_take_is_a_500_that_says_the_request_is_at_fault()
    {
        var transport = await Transport();
        var answer = await transport.SendAsync("transport.tour", $$"""[ { "match": { "Number": { "in": "TR-M-1" } } }, {{SubTours}} ]""");

        // The recorded status of such a request (operand-in-not-an-array) is 500; nothing is sent to the database.
        (await LegacyGoldenTests.RecordedAsync("operand-in-not-an-array"))["status"]!.GetValue<int>().Should().Be(500);

        answer.StatusCode.Should().Be(500, answer.ToString());
        answer.ErrorCodes.Should().Equal("INTERNAL_ERROR");
        answer.Body!["errors"]![0]!["message"]!.GetValue<string>().Should().Contain("an operand is of a kind its operator does not take").And.NotContain("BsonString");
    }

    [Fact]
    public async Task F04_the_page_sizes_are_the_hosts_and_the_default_page_is_its_default()
    {
        var transport = await Transport("legacy-small", new Dictionary<string, string?> { ["OxQL:Limits:MaxPageSize"] = "3", ["OxQL:Limits:DefaultPageSize"] = "2" });
        var defaulted = await transport.SendAsync("transport.tour", $$"""[ {{SubTours}}, { "project": { "Number": 1 } } ]""");

        defaulted.ShouldBeOk();
        defaulted.Items.Should().HaveCount(2);
        defaulted.HasNextPage.Should().BeTrue();

        var refused = (await transport.SendAsync("transport.tour", $$"""[ {{SubTours}}, { "page": { "limit": 4 } } ]""")).ShouldRefuse("PAGE_SIZE_EXCEEDED", 400);

        refused["message"]!.GetValue<string>().Should().Be("Page limit 4 exceeds maximum of 3.");
    }
}
