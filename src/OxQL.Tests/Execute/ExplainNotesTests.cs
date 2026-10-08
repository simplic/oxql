using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The engine-behaviour notes of an explain (DESIGN §4.4), each provoked where it applies and absent
/// where it does not: folding, element and absence semantics, variant-only and snapshot paths, join
/// placement, what owners bind, dropped select paths, caps and limits, paging, the missing policy and
/// the report page. <c>REMOTE_UNCHECKED</c> is E13's; <c>INDEX_ADVICE</c> is asserted with the advisory
/// in <c>AspNetCore/HostHardeningExplainTests</c>; the run's <c>SELECT_PATH_NOT_ON_TARGET</c> in
/// <c>Execute/ContinuationExecutionTests</c>.
/// </summary>
public class ExplainNotesTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static async Task<IReadOnlyList<Diagnostic>> NotesAsync(string pipeline, string entity = Invoice, bool strict = false, EntityModel? model = null)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(model ?? ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var outcome = await engine.ExplainAsync(BindHost.Request(entity, pipeline) with { Strict = strict ? true : null }, BindHost.Context());
        var result = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => $"{error.Code} {error.Message}")));

        return result.Notes;
    }

    private static Diagnostic Single(IReadOnlyList<Diagnostic> notes, string code) =>
        notes.Should().ContainSingle(note => note.Code == code, string.Join(", ", notes.Select(note => note.Code))).Subject;

    [Fact]
    public void The_codes_are_the_list_of_DESIGN_4_4_in_its_order()
    {
        Notes.All.Should().Equal(
            "TEXT_FOLDS", "PATTERN_FOLDS_CASE_ONLY", "EXACT_FORCES_EXACT", "SOME_ELEMENT", "NEQ_MATCHES_ABSENT", "ONLY_FOR_VARIANTS", "SNAPSHOT_COPY",
            "JOIN_BEFORE_PAGE", "JOIN_AFTER_PAGE", "OWNER_BINDS", "REMOTE_UNCHECKED", "SELECT_PATH_NOT_ON_TARGET", "COUNT_CAP", "LOOKUP_LIMIT",
            "OFFSET_PAGING", "MISSING_POLICY", "REPORT_PAGE", "INDEX_ADVICE", "REMOTE_LOOKUP", "EXPLAIN_LIMIT", "EXPLAIN_TRIMMED", "FILTER_BINDS_EARLIER",
            "PATTERN_FOLDS", "PROJECT_AFTER_PAGE", "SORT_BEFORE_UNWIND", "UNWIND_PREFILTER", "SEMI_JOIN_NOT_BY_KEY", "GROUP_SCANS", "VALUES_SCANS");
        Notes.All.Except([Notes.ExplainLimit]).Should().NotIntersectWith(typeof(Codes).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!),
            "a note is neither a refusal nor a diagnostic of the catalogue; only an explain limit is both a refusal (past a bound, before binding) and a note (hit mid-way)");
    }

    [Fact]
    public async Task A_folding_comparison_says_its_collation_and_a_folding_pattern_what_it_folds_and_that_no_index_bounds_it()
    {
        var notes = await NotesAsync("""[{ "match": { "and": [{ "number": { "eq": "re-1" } }, { "number": { "contains": "e-" } }] } }]""");

        var folds = Single(notes, Notes.TextFolds);
        folds.Stage.Should().Be(0);
        folds.Path.Should().Be("number");
        folds.Params!["op"].Should().Be("eq");
        folds.Params["locale"].Should().Be(BindHost.Options().Representation.Collation.Locale);
        folds.Params["strength"].Should().Be(BindHost.Options().Representation.Collation.Strength);

        // contains and endsWith fold what the collation folds, as a pattern (DG2 M10).
        var pattern = Single(notes, Notes.PatternFolds);

        pattern.Message.Should().Be("'contains' on 'number' folds case and accents (collation de, strength 1) as a pattern: no index bounds it, it reads every candidate row.");
        System.Text.Json.JsonSerializer.Serialize(pattern.Params).Should().Be("""{"locale":"de","strength":1,"op":"contains","accents":true}""");
        notes.Should().NotContain(note => note.Code == Notes.PatternFoldsCaseOnly, "that is what a contract 1 pattern does");

        (await NotesAsync("""[{ "match": { "number": { "eq": "RE-1", "options": { "caseSensitive": true } } } }]""")).Should().NotContain(note => note.Code == Notes.TextFolds);
    }

    [Fact]
    public async Task What_the_compiler_places_elsewhere_than_the_request_wrote_it_is_said_at_the_stage()
    {
        var notes = await NotesAsync(
            """[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "gt": 2 } } }, { "project": { "number": 1, "item.quantity": 1 } }, { "sort": [{ "number": "asc" }] }]""",
            "probe.order", model: BindHost.Probe);

        var late = Single(notes, Notes.ProjectAfterPage);

        late.Stage.Should().Be(2);
        late.Message.Should().Be("The projection runs after the page is taken, on the page's rows only: the rows and their order are the same wherever it is written.");

        var parents = Single(notes, Notes.SortBeforeUnwind);

        parents.Stage.Should().Be(0);
        parents.Path.Should().Be("items");
        System.Text.Json.JsonSerializer.Serialize(parents.Params).Should().Be("""{"path":"items","alias":"item"}""");

        var prefilter = Single(notes, Notes.UnwindPrefilter);

        prefilter.Stage.Should().Be(0);
        prefilter.Message.Should().Be("Only rows with an element of 'items' that passes what is asked of 'item' after the unwind are unwound.");
        System.Text.Json.JsonSerializer.Serialize(prefilter.Params).Should().Be("""{"path":"items","alias":"item","paths":["item.quantity"]}""");

        // A condition an element without members passes (not equal, not in) is asked before the unwind as well, and the parents are cut first: both are said.
        var negative = await NotesAsync(
            """[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "nin": [1, 2] } } }, { "sort": [{ "number": "asc" }] }]""",
            "probe.order", model: BindHost.Probe);

        Single(negative, Notes.SortBeforeUnwind).Stage.Should().Be(0);
        System.Text.Json.JsonSerializer.Serialize(Single(negative, Notes.UnwindPrefilter).Params).Should().Be("""{"path":"items","alias":"item","paths":["item.quantity"]}""");

        // Sorted on the element, the order is not the parents': nothing is cut before the unwind, and nothing says so.
        (await NotesAsync("""[{ "unwind": { "path": "items", "as": "item" } }, { "sort": [{ "item.quantity": "asc" }] }]""", "probe.order", model: BindHost.Probe))
            .Should().NotContain(note => note.Code == Notes.SortBeforeUnwind || note.Code == Notes.UnwindPrefilter);

        // Contract 1 runs as written and says none of it.
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var legacy = (await engine.ExplainAsync(BindHost.Request("probe.order", """[{ "project": { "number": 1 } }, { "sort": [{ "number": "asc" }] }]"""), BindHost.Context(contract: 1)))
            .Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        legacy.Notes.Should().NotContain(note => note.Code == Notes.ProjectAfterPage);
    }

    [Fact]
    public async Task What_a_group_reads_for_every_page_is_said_once_at_the_first_group()
    {
        var notes = await NotesAsync(
            """[{ "match": { "number": { "exists": true } } }, { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "group": { "by": [{ "path": "n", "as": "size" }], "fields": { "groups": { "count": true } } } }]""",
            "probe.order", model: BindHost.Probe);
        var scans = Single(notes, Notes.GroupScans);

        scans.Stage.Should().Be(1, "the first group; a group of groups reads no more rows");
        scans.Message.Should().StartWith("The groups are computed over every row that reaches the group, for every page of groups");
        System.Text.Json.JsonSerializer.Serialize(scans.Params).Should().Be("""{"keys":1}""");
        Basis.OfNote[scans.Code].Should().Be(Basis.Advisory);

        var totals = Single(await NotesAsync("""[{ "group": { "by": [], "fields": { "n": { "count": true } } } }]""", "probe.order", model: BindHost.Probe), Notes.GroupScans);

        totals.Message.Should().StartWith("The totals are computed over every row that reaches the group");
        System.Text.Json.JsonSerializer.Serialize(totals.Params).Should().Be("""{"keys":0}""");

        (await NotesAsync("""[{ "sort": [{ "number": "asc" }] }]""", "probe.order", model: BindHost.Probe)).Should().NotContain(note => note.Code == Notes.GroupScans);

        // Contract 1 says what it always said.
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var legacy = (await engine.ExplainAsync(BindHost.Request("probe.order", """[{ "group": { "by": [], "fields": { "n": { "count": true } } } }]"""), BindHost.Context(contract: 1)))
            .Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        legacy.Notes.Should().NotContain(note => note.Code == Notes.GroupScans);
    }

    [Fact]
    public async Task An_exact_sort_says_that_it_makes_the_request_exact()
    {
        var note = Single(await NotesAsync("""[{ "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }] }]"""), Notes.ExactForcesExact);

        note.Stage.Should().Be(0);
        note.Path.Should().Be("number");

        Single(await NotesAsync("""[{ "sort": [{ "number": "asc" }] }]"""), Notes.TextFolds).Params!["op"].Should().Be("sort");
    }

    [Fact]
    public async Task A_condition_through_a_collection_matches_some_element_and_neq_matches_the_absent()
    {
        var notes = await NotesAsync("""[{ "match": { "and": [{ "lines.customerId": { "eq": "00000000-0000-0000-0000-000000000001" } }, { "number": { "neq": "x" } }] } }]""");

        Single(notes, Notes.SomeElement).Path.Should().Be("lines.customerId");
        Single(notes, Notes.NeqMatchesAbsent).Params!["op"].Should().Be("neq");

        (await NotesAsync("""[{ "match": { "number": { "neq": null } } }]""")).Should().NotContain(note => note.Code == Notes.NeqMatchesAbsent, "neq null is 'present'");
    }

    [Fact]
    public async Task A_variant_only_path_and_a_path_in_an_embedded_copy_are_said()
    {
        Single(await NotesAsync("""[{ "match": { "slot.licence": { "eq": "B" } } }]"""), Notes.OnlyForVariants).Params!["variants"]
            .Should().BeAssignableTo<IEnumerable<string>>().Which.Should().Equal("RcDriverSlot");

        Single(await NotesAsync("""[{ "match": { "customer.name": { "eq": "x" } } }]""", "probe.order", model: BindHost.Probe), Notes.SnapshotCopy)
            .Params!["entity"].Should().Be("probe.customer");
    }

    [Fact]
    public async Task Every_join_says_where_it_runs()
    {
        var before = await NotesAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "match": { "c.name": { "eq": "x" } } }]""");
        Single(before, Notes.JoinBeforePage).Should().Match<Diagnostic>(note => note.Stage == 0 && note.Path == "c");

        var after = await NotesAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }]""");
        Single(after, Notes.JoinAfterPage).Params!["kind"].Should().Be("resolve");
    }

    [Fact]
    public async Task A_join_kept_before_an_unwind_says_so_and_on_which_rows_it_runs()
    {
        // The page is ordered by the parents: they are cut before the unwind, and the join runs on those.
        var parents = Single(await NotesAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "unwind": { "path": "items", "as": "item" } }, { "sort": [{ "number": "asc" }] }]""", "probe.order", model: BindHost.Probe), Notes.JoinBeforePage);

        parents.Should().Match<Diagnostic>(note => note.Stage == 0 && note.Path == "c");
        parents.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["alias"] = "c", ["kind"] = "resolve", ["unwind"] = "items", ["rows"] = "parents" });

        // Ordered by the element: where it was written.
        var written = Single(await NotesAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "unwind": { "path": "items", "as": "item" } }, { "sort": [{ "item.quantity": "asc" }] }]""", "probe.order", model: BindHost.Probe), Notes.JoinBeforePage);

        written.Params!["rows"].Should().Be("written");
        written.Message.Should().Contain("once per row there, not once per element");

        // Written after the unwind it was asked once per element: after the page, as before.
        (await NotesAsync("""[{ "unwind": { "path": "items", "as": "item" } }, { "resolve": { "path": "customerId", "as": "c" } }]""", "probe.order", model: BindHost.Probe))
            .Should().Contain(note => note.Code == Notes.JoinAfterPage).And.NotContain(note => note.Code == Notes.JoinBeforePage);
    }

    [Fact]
    public async Task What_an_owner_binds_is_said_for_a_remote_resolve_and_every_continued_stage()
    {
        var notes = await NotesAsync("""
            [{ "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "r.companyId", "as": "co" } }]
            """);

        var owners = notes.Where(note => note.Code == Notes.OwnerBinds).ToList();
        owners.Select(note => (note.Stage, note.Path)).Should().Equal((0, "contactId"), (1, "r.companyId"));
        owners.Should().OnlyContain(note => ((IEnumerable<string>)note.Params!["services"]!).SequenceEqual(new[] { "crm" }));
        owners[1].Params!["alias"].Should().Be("r");
    }

    [Theory]
    [InlineData(null, false, "null")]
    [InlineData(null, true, "refuse")]
    [InlineData("report", false, "report")]
    [InlineData("null", true, "null")]
    public async Task A_continued_resolve_says_its_missing_policy_as_its_owner_binds_it(string? onMissing, bool strict, string effective)
    {
        var notes = await NotesAsync($$"""
            [{ "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "r.companyId", "as": "co"{{(onMissing is null ? "" : $", \"onMissing\": \"{onMissing}\"")}} } }]
            """, strict: strict);

        var policies = notes.Where(note => note.Code == Notes.MissingPolicy).ToDictionary(note => note.Stage!.Value);
        policies.Keys.Should().Equal([0, 1], "the continued stage says its policy as the origin's resolve does");

        var continued = policies[1];
        continued.Path.Should().Be("r.companyId");
        continued.Params!["alias"].Should().Be("co");
        continued.Params["onMissing"].Should().Be(effective);
        continued.Params["strict"].Should().Be(strict);
        continued.Params["dataLoss"].Should().BeEquivalentTo(Notes.DataLossOutcomes, "the owner binds it; every outcome is possible");
    }

    [Fact]
    public async Task A_continued_lookup_says_no_missing_policy()
    {
        var notes = await NotesAsync("""
            [{ "resolve": { "path": "contactId", "as": "r" } },
             { "lookup": { "from": "crm.note", "path": "contactId", "on": "r", "as": "notes" } }]
            """);

        notes.Where(note => note.Code == Notes.MissingPolicy).Select(note => note.Stage).Should().Equal(0);
    }

    [Fact]
    public async Task A_flat_select_path_a_local_target_lacks_is_said_per_target()
    {
        var notes = await NotesAsync("""[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }, { "project": { "line": 1, "owner.id": 1, "owner.number": 1, "owner.name": 1 } }]""");

        var dropped = notes.Where(note => note.Code == Notes.SelectPathNotOnTarget).ToList();
        dropped.Select(note => (note.Path, (string)note.Params!["target"]!)).Should().BeEquivalentTo([("name", "rc.shipment#billingLines"), ("number", "rc.tour#billingLines")],
            "each local target lacks one owning-row path; the remote target's are only known to its owner");
        dropped.Should().OnlyContain(note => (bool)note.Params!["parent"]! && (string)note.Params["alias"]! == "line" && note.Stage == 0);
    }

    [Fact]
    public async Task The_caps_and_limits_are_said_where_they_bind()
    {
        // The count says where it stopped in its own answer (pageInfo.count); no note repeats the cap (DG2 M3).
        (await NotesAsync("""[{ "page": { "count": true } }]""")).Should().NotContain(note => note.Code == Notes.CountCap);

        Single(await NotesAsync("""[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "limit": 7 } }]""", "rc.customer"), Notes.LookupLimit)
            .Params!["limit"].Should().Be(7);

        Single(await NotesAsync("""[{ "resolve": { "path": "customerIds", "as": "customers", "elements": "all" } }]"""), Notes.LookupLimit)
            .Params!["limit"].Should().Be(BindHost.Options().Limits.MaxLookupLimit);
    }

    [Fact]
    public async Task Contract_2_pages_by_its_cursor_and_says_nothing_of_an_offset()
    {
        (await NotesAsync("""[{ "match": { "number": { "exists": true } } }, { "unwind": { "path": "lines", "as": "line" } }]"""))
            .Should().NotContain(note => note.Code == Notes.OffsetPaging, "the cursor is the only address of a contract 2 page (DG2 M4)");
    }

    [Fact]
    public async Task Each_resolve_says_its_missing_policy_and_a_strict_request_its_report_page()
    {
        var loose = Single(await NotesAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }]"""), Notes.MissingPolicy);
        loose.Params!["onMissing"].Should().Be("null");
        loose.Params["strict"].Should().Be(false);
        loose.Params["dataLoss"].Should().BeEquivalentTo(Notes.DataLossOutcomes);

        var strict = await NotesAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }]""", strict: true);
        Single(strict, Notes.MissingPolicy).Params!["onMissing"].Should().Be("refuse", "strict defaults onMissing to refuse");
        Single(strict, Notes.ReportPage).Params!["max"].Should().Be(Math.Max(BindHost.Options().Limits.MaxPageSize, BindHost.Options().Limits.MaxReportPageSize));

        (await NotesAsync("""[{ "page": { "limit": 10 } }]""")).Should().NotContain(note => note.Code == Notes.ReportPage, "a request that is not strict has no report page");
    }

    [Fact]
    public async Task The_notes_are_in_stage_order_with_request_wide_ones_last()
    {
        var notes = await NotesAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }, { "match": { "number": { "eq": "x" } } }]""");

        notes.Select(note => note.Stage ?? int.MaxValue).Should().BeInAscendingOrder();
    }
}
