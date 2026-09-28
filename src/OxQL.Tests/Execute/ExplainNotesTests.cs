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
            "OFFSET_PAGING", "MISSING_POLICY", "REPORT_PAGE", "INDEX_ADVICE");
        Notes.All.Should().NotIntersectWith(typeof(Codes).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!),
            "a note is neither a refusal nor a diagnostic of the catalogue");
    }

    [Fact]
    public async Task A_folding_comparison_says_its_collation_and_a_folding_pattern_that_it_folds_case_only()
    {
        var notes = await NotesAsync("""[{ "match": { "and": [{ "number": { "eq": "re-1" } }, { "number": { "contains": "e-" } }] } }]""");

        var folds = Single(notes, Notes.TextFolds);
        folds.Stage.Should().Be(0);
        folds.Path.Should().Be("number");
        folds.Params!["op"].Should().Be("eq");
        folds.Params["locale"].Should().Be(BindHost.Options().Representation.Collation.Locale);
        folds.Params["strength"].Should().Be(BindHost.Options().Representation.Collation.Strength);
        Single(notes, Notes.PatternFoldsCaseOnly).Params!["op"].Should().Be("contains");

        (await NotesAsync("""[{ "match": { "number": { "eq": "RE-1", "options": { "caseSensitive": true } } } }]""")).Should().NotContain(note => note.Code == Notes.TextFolds);
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
        var before = await NotesAsync("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }, { "match": { "c.name": { "eq": "x" } } }]""");
        Single(before, Notes.JoinBeforePage).Should().Match<Diagnostic>(note => note.Stage == 0 && note.Path == "c");

        var after = await NotesAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }]""");
        Single(after, Notes.JoinAfterPage).Params!["kind"].Should().Be("resolve");
    }

    [Fact]
    public async Task What_an_owner_binds_is_said_for_a_remote_resolve_and_every_continued_stage()
    {
        var notes = await NotesAsync("""
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
             { "resolve": { "path": "r.companyId", "as": "co" } }]
            """);

        var owners = notes.Where(note => note.Code == Notes.OwnerBinds).ToList();
        owners.Select(note => (note.Stage, note.Path)).Should().Equal((0, "contactId"), (1, "r.companyId"));
        owners.Should().OnlyContain(note => ((IEnumerable<string>)note.Params!["services"]!).SequenceEqual(new[] { "crm" }));
        owners[1].Params!["alias"].Should().Be("r");
    }

    [Fact]
    public async Task A_flat_select_path_a_local_target_lacks_is_said_per_target()
    {
        var notes = await NotesAsync("""[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner", "parentSelect": ["id", "number", "name"] } }]""");

        var dropped = notes.Where(note => note.Code == Notes.SelectPathNotOnTarget).ToList();
        dropped.Select(note => (note.Path, (string)note.Params!["target"]!)).Should().BeEquivalentTo([("name", "rc.shipment#billingLines"), ("number", "rc.tour#billingLines")],
            "each local target lacks one owning-row path; the remote target's are only known to its owner");
        dropped.Should().OnlyContain(note => (bool)note.Params!["parent"]! && (string)note.Params["alias"]! == "line" && note.Stage == 0);
    }

    [Fact]
    public async Task The_caps_and_limits_are_said_where_they_bind()
    {
        Single(await NotesAsync("""[{ "page": { "includeTotalCount": true } }]"""), Notes.CountCap).Should()
            .Match<Diagnostic>(note => note.Stage == 0 && (int)note.Params!["cap"]! == BindHost.Options().Limits.CountCap);

        Single(await NotesAsync("""[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "limit": 7 } }]""", "rc.customer"), Notes.LookupLimit)
            .Params!["limit"].Should().Be(7);

        Single(await NotesAsync("""[{ "resolve": { "path": "customerIds", "as": "customers", "elements": "all" } }]"""), Notes.LookupLimit)
            .Params!["limit"].Should().Be(BindHost.Options().Limits.MaxLookupLimit);
    }

    [Fact]
    public async Task Offset_paging_is_said_at_the_stage_that_causes_it()
    {
        Single(await NotesAsync("""[{ "match": { "number": { "exists": true } } }, { "unwind": { "path": "lines", "as": "line" } }]"""), Notes.OffsetPaging)
            .Should().Match<Diagnostic>(note => note.Stage == 1 && (int)note.Params!["maxOffset"]! == BindHost.Options().Limits.MaxOffset);

        (await NotesAsync("[]")).Should().NotContain(note => note.Code == Notes.OffsetPaging);
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

        (await NotesAsync("""[{ "page": { "limit": 10, "offset": 0 } }]""", strict: true)).Should().NotContain(note => note.Code == Notes.ReportPage, "a page that jumps is no report page");
    }

    [Fact]
    public async Task The_notes_are_in_stage_order_with_request_wide_ones_last()
    {
        var notes = await NotesAsync("""[{ "resolve": { "path": "billingLineId", "as": "r" } }, { "match": { "number": { "eq": "x" } } }]""");

        notes.Select(note => note.Stage ?? int.MaxValue).Should().BeInAscendingOrder();
    }
}
