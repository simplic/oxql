using System.Text.Json;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The request member <c>strict</c> and the capture of unknown request members (DESIGN §3.0,
/// §3.4.3), the report page, and the contract 1 hint on every refusal of a contract 2 construct
/// (§3.12).
/// </summary>
public class StrictRequestBindTests
{
    private const string Order = "probe.order";
    private const string Customer = "probe.customer";

    private static QueryRequest Request(string entity, string pipeline, string members = "") =>
        BindHost.Parse($$"""{ "entityType": "{{entity}}", {{members}} "pipeline": {{pipeline}} }""");

    private static async Task<BindOutcome> BindAsync(string members, string pipeline = "[]", RequestContext? context = null, string entity = Order) =>
        await BindHost.BindAsync(BindHost.Probe, Request(entity, pipeline, members), context);

    private static async Task<BoundPipeline> BoundAsync(string members, string pipeline = "[]", RequestContext? context = null)
    {
        var outcome = await BindAsync(members, pipeline, context);

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));

        return ((BindOutcome.Bound)outcome).Pipeline;
    }

    private static async Task<IReadOnlyList<QueryValidationError>> ErrorsAsync(string members, string pipeline = "[]", RequestContext? context = null, string entity = Order)
    {
        var outcome = await BindAsync(members, pipeline, context, entity);

        outcome.Should().BeOfType<BindOutcome.Failed>("the request should be refused");

        return ((BindOutcome.Failed)outcome).Refusal.Errors!;
    }

    private static RequestContext Contract1 => BindHost.Context(contract: 1);

    // ---- the members -------------------------------------------------------------------------

    [Fact]
    public void Strict_and_every_unknown_top_level_member_are_captured_when_the_request_is_read()
    {
        var strict = Request(Order, "[]", """ "strict": true, "strictness": 1, "limit": 5, """);
        var loose = Request(Order, "[]", """ "strict": false, """);
        var plain = Request(Order, "[]");

        strict.Strict.Should().BeTrue();
        strict.IsStrict.Should().BeTrue();
        strict.Unknown!.Keys.Should().BeEquivalentTo(["strictness", "limit"]);
        loose.Strict.Should().BeFalse();
        loose.IsStrict.Should().BeFalse();
        loose.Unknown.Should().BeNull();
        plain.Strict.Should().BeNull();
        plain.Unknown.Should().BeNull();
    }

    [Fact]
    public void A_request_without_strict_is_written_as_before_so_an_owner_on_2_0_reads_it_unchanged()
    {
        var plain = JsonSerializer.Serialize(Request(Order, "[]"), OxQLJson.Wire);
        var strict = JsonSerializer.Serialize(Request(Order, "[]", """ "strict": true, """), OxQLJson.Wire);

        plain.Should().NotContain("strict");
        strict.Should().Contain("\"strict\":true");
    }

    [Fact]
    public async Task An_unknown_request_member_is_refused_under_contract_2_with_the_members_a_request_carries()
    {
        var errors = await ErrorsAsync(""" "strictness": true, "limit": 5, """);

        var error = errors.Should().ContainSingle().Which;

        error.Code.Should().Be(Codes.UnknownRequestMember);
        error.Stage.Should().BeNull();
        error.Message.Should().Be("'strictness, limit' is not a member of a request; a request carries entityType, variables, pipeline, strict.");
    }

    [Fact]
    public async Task An_unknown_request_member_is_reported_beside_the_stage_errors()
    {
        var errors = await ErrorsAsync(""" "limit": 5, """, """[{ "page": { "skip": 5 } }]""");

        errors.Select(error => error.Code).Should().BeEquivalentTo([Codes.UnknownRequestMember, Codes.UnknownStageMember]);
    }

    [Fact]
    public async Task Contract_1_ignores_unknown_request_members_as_it_always_has()
    {
        var bound = await BoundAsync(""" "limit": 5, """, "[]", Contract1);

        bound.Page.Limit.Should().Be(new LimitOptions().DefaultPageSize);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Strict_under_contract_1_is_a_legacy_member_with_the_contract_hint(string value)
    {
        var errors = await ErrorsAsync($""" "strict": {value}, """, "[]", Contract1);

        var error = errors.Should().ContainSingle().Which;

        error.Code.Should().Be(Codes.LegacyStageUnsupported);
        error.Message.Should().Be("'strict' is a contract 2 member of a request; contract 1 has no strict mode." + Binder.Contract1Hint);
    }

    [Fact]
    public async Task Strict_never_changes_the_canonical_render_or_the_cursor_fingerprint()
    {
        const string pipeline = """[{ "match": { "number": { "eq": "x" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10 } }]""";

        var plain = await BoundAsync("", pipeline);
        var strict = await BoundAsync(""" "strict": true, """, pipeline);
        var loose = await BoundAsync(""" "strict": false, """, pipeline);

        strict.Canonical.Should().Be(plain.Canonical);
        strict.Fingerprint.Should().Be(plain.Fingerprint);
        loose.Fingerprint.Should().Be(plain.Fingerprint);
    }

    // ---- onMissing under strict ----------------------------------------------------------------

    [Theory]
    [InlineData("""{ "path": "customerId", "as": "c" }""", "", ResolveExecutor.Inline, ResolveOnMissing.Null)]
    [InlineData("""{ "path": "customerId", "as": "c" }""", "\"strict\": true,", ResolveExecutor.Inline, ResolveOnMissing.Refuse)]
    [InlineData("""{ "path": "customerId", "as": "c" }""", "\"strict\": false,", ResolveExecutor.Inline, ResolveOnMissing.Null)]
    [InlineData("""{ "path": "customerId", "as": "c", "onMissing": "null" }""", "\"strict\": true,", ResolveExecutor.Inline, ResolveOnMissing.Null)]
    [InlineData("""{ "path": "customerId", "as": "c", "onMissing": "report" }""", "\"strict\": true,", ResolveExecutor.Inline, ResolveOnMissing.Report)]
    [InlineData("""{ "path": "customerId", "as": "c", "filter": { "name": { "eq": "x" } } }""", "\"strict\": true,", ResolveExecutor.Inline, ResolveOnMissing.Refuse)]
    public async Task A_strict_request_refuses_a_missing_reference_unless_the_stage_says_otherwise(string stage, string members, ResolveExecutor executor, ResolveOnMissing effective)
    {
        var request = BindHost.Parse($$"""{ "entityType": "{{ResolveModel.Invoice}}", {{members}} "pipeline": [{ "resolve": {{stage}} }] }""");
        var outcome = await BindHost.BindAsync(ResolveModel.Model, request);

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));
        var resolve = ((BindOutcome.Bound)outcome).Pipeline.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.EffectiveOnMissing.Should().Be(effective);
        resolve.Executor.Should().Be(executor, "strict changes what refuses, never the executor: the aggregate tells an excluded record from a missing one itself");
    }

    // ---- the report page ---------------------------------------------------------------------

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(5_000)]
    public async Task A_strict_request_without_cursor_or_offset_pages_up_to_the_report_page_size(int limit)
    {
        var bound = await BoundAsync(""" "strict": true, """, $$"""[{ "page": { "limit": {{limit}} } }]""");

        bound.Page.Limit.Should().Be(limit);
    }

    [Fact]
    public async Task A_strict_page_above_the_report_page_size_is_refused_naming_it()
    {
        var errors = await ErrorsAsync(""" "strict": true, """, """[{ "page": { "limit": 5001 } }]""");

        var error = errors.Should().ContainSingle().Which;

        error.Code.Should().Be(Codes.PageSizeExceeded);
        error.Message.Should().Be("The page limit 5001 exceeds the report page maximum of 5000.");
    }

    [Fact]
    public async Task A_strict_page_that_jumps_by_an_offset_stays_under_the_page_size()
    {
        var errors = await ErrorsAsync(""" "strict": true, """, """[{ "page": { "limit": 501, "offset": 10 } }]""");

        errors.Should().ContainSingle().Which.Code.Should().Be(Codes.PageSizeExceeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"strict\": false,")]
    public async Task A_page_that_is_not_strict_stays_under_the_page_size_and_is_told_where_a_larger_one_is(string members)
    {
        var errors = await ErrorsAsync(members, """[{ "page": { "limit": 501 } }]""");

        var error = errors.Should().ContainSingle().Which;

        error.Code.Should().Be(Codes.PageSizeExceeded);
        error.Message.Should().Be("The page limit 501 exceeds the maximum of 500. A strict request without cursor or offset may ask for up to 5000.");
    }

    [Fact]
    public async Task A_report_page_size_below_the_page_size_gives_strict_requests_the_page_size()
    {
        var context = BindHost.Context(BindHost.Options(options => options.Limits.MaxReportPageSize = 100));

        (await BoundAsync(""" "strict": true, """, """[{ "page": { "limit": 500 } }]""", context)).Page.Limit.Should().Be(500);

        var errors = await ErrorsAsync("", """[{ "page": { "limit": 501 } }]""", context);

        errors.Should().ContainSingle().Which.Message.Should().Be("The page limit 501 exceeds the maximum of 500.", "no larger page exists to point at");
    }

    [Theory]
    [InlineData(5_000, 50, true)]
    [InlineData(5_000, 51, false)]
    [InlineData(500, 100, true)]
    public async Task A_report_page_joins_no_more_rows_than_an_ordinary_page_can(int limit, int lookupLimit, bool binds)
    {
        var pipeline = $$"""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "limit": {{lookupLimit}} } }, { "page": { "limit": {{limit}} } }]""";
        if (binds)
        {
            (await BindAsync(""" "strict": true, """, pipeline, entity: Customer)).Should().BeOfType<BindOutcome.Bound>();
            return;
        }

        var errors = await ErrorsAsync(""" "strict": true, """, pipeline, entity: Customer);

        var error = errors.Should().ContainSingle().Which;
        error.Code.Should().Be(Codes.PageSizeExceeded);
        error.Message.Should().Be($"The report page of {limit} rows with lookups of up to {lookupLimit} child rows per row could join {limit * lookupLimit} rows; the limit is 250000. Lower the page limit or the lookups' limits.");
    }

    [Fact]
    public async Task A_report_page_counts_a_first_lookup_as_one_row()
    {
        var pipeline = """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "latest", "first": true, "sort": [{ "number": "desc" }] } }, { "page": { "limit": 5000 } }]""";

        (await BindAsync(""" "strict": true, """, pipeline, entity: Customer)).Should().BeOfType<BindOutcome.Bound>();
    }

    // ---- the contract 1 hint -----------------------------------------------------------------

    public static TheoryData<string, string, string, string> Contract2Constructs => new()
    {
        { Customer, """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "number": "asc" }] } }]""", Codes.LegacyStageUnsupported, "lookup sort" },
        { Customer, """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "first": "yes" } }]""", Codes.LegacyStageUnsupported, "malformed lookup first" },
        { Order, """[{ "resolve": { "path": "customerId", "as": "c", "elements": "first" } }]""", Codes.LegacyStageUnsupported, "resolve elements" },
        { Order, """[{ "page": { "includeTotalCount": 500 } }]""", Codes.LegacyStageUnsupported, "count cap" },
        { Order, """[{ "match": { "number": { "eq": "x", "options": { "caseSensitive": true } } } }]""", Codes.OptionNotApplicable, "caseSensitive" },
        { Order, """[{ "sort": [{ "number": { "direction": "asc" } }] }]""", Codes.InvalidSortDirection, "sort object form" },
    };

    [Theory]
    [MemberData(nameof(Contract2Constructs))]
    public async Task Every_refusal_of_a_contract_2_construct_under_contract_1_ends_with_the_contract_hint(string entity, string pipeline, string code, string construct)
    {
        var errors = await ErrorsAsync("", pipeline, Contract1, entity);

        var error = errors.Should().ContainSingle(construct).Which;

        error.Code.Should().Be(code, "the codes are unchanged");
        error.Message.Should().EndWith(Binder.Contract1Hint, construct);
        error.Message.Should().Contain("'X-OxQL-Contract: 2'");
    }

    [Fact]
    public async Task The_same_constructs_under_contract_2_carry_no_hint()
    {
        var bound = await BoundAsync("", """[{ "sort": [{ "number": { "direction": "asc" } }] }, { "page": { "includeTotalCount": 500 } }]""");

        bound.Page.CountCap.Should().Be(500);

        var errors = await ErrorsAsync("", """[{ "resolve": { "path": "customerId", "as": "c", "source": "x" } }]""");

        errors.Should().ContainSingle().Which.Message.Should().NotContain("contract 1");
    }

    [Theory]
    [InlineData("""[{ "page": { "skip": 5 } }]""", "a member no contract has")]
    [InlineData("""[{ "match": { "number": { "like": "x" } } }]""", "an operator no contract has")]
    public async Task A_contract_1_refusal_of_something_no_contract_has_carries_no_hint(string pipeline, string why)
    {
        var errors = await ErrorsAsync("", pipeline, Contract1);

        errors.Should().ContainSingle(why).Which.Message.Should().NotContain(Binder.Contract1Hint.Trim(), why);
    }
}
