using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Refusals;

/// <summary>
/// The request members of contract 2 on a real server (DESIGN §3.0, §3.4.3, §3.12): an unknown top-level
/// member is refused under contract 2 and ignored under contract 1; <c>strict</c> is a contract 2
/// member whose contract 1 refusal says the request was read as contract 1; a strict request
/// without cursor or offset pages up to <c>maxReportPageSize</c>.
/// </summary>
[Trait("Category", "Integration")]
public class RefusalsRequestMembersTests
{
    private const string Hint = "This request was read as contract 1 because it carries no 'X-OxQL-Contract: 2' header.";

    private static string Body(string members, string pipeline) =>
        $$"""{ "entityType": "{{Corpus.Employee}}", {{members}} "pipeline": {{pipeline}} }""";

    [Fact]
    public async Task An_unknown_top_level_member_under_contract_2_is_UNKNOWN_REQUEST_MEMBER()
    {
        var staff = await Lab.ClientAsync(LabService.Staff);

        var refused = await staff.QueryAsync(Body("""  "strictly": true, """, """[{ "page": { "limit": 1 } }]"""));

        refused.ShouldRefuse("UNKNOWN_REQUEST_MEMBER", 400)["message"]!.GetValue<string>()
            .Should().Be("'strictly' is not a member of a request; a request carries entityType, variables, pipeline, strict.");
        refused.ErrorCodes.Should().Equal(["UNKNOWN_REQUEST_MEMBER"]);
    }

    [Fact]
    public async Task Contract_1_ignores_an_unknown_member_and_refuses_strict_with_the_contract_hint()
    {
        var v1 = await Lab.ClientAsync(LabService.Staff, contract: null);

        (await v1.QueryAsync(Body("""  "strictly": true, """, """[{ "page": { "limit": 1 } }]"""))).StatusCode.Should().Be(200, "contract 1 never refused a member it did not know");

        var strict = await v1.QueryAsync(Body("""  "strict": true, """, """[{ "page": { "limit": 1 } }]"""));

        strict.ShouldRefuse("LEGACY_STAGE_UNSUPPORTED", 400)["message"]!.GetValue<string>().Should().EndWith(Hint);
        strict.ErrorCodes.Should().Equal(["LEGACY_STAGE_UNSUPPORTED"]);
    }

    [Fact]
    public async Task A_v2_lookup_without_the_contract_header_is_refused_with_the_contract_hint()
    {
        var v1 = await Lab.ClientAsync(LabService.Staff, contract: null);

        var refused = await v1.QueryAsync(Body("", """[{ "lookup": { "path": "emailAddresses", "from": "staff.employee", "as": "a", "first": true } }, { "page": { "limit": 1 } }]"""));

        refused.ShouldRefuse("LEGACY_STAGE_UNSUPPORTED", 400)["message"]!.GetValue<string>().Should().EndWith(Hint);
    }

    [Fact]
    public async Task A_strict_request_without_cursor_or_offset_pages_up_to_the_report_page_size()
    {
        var staff = await Lab.ClientAsync(LabService.Staff);
        var limits = (await staff.HealthAsync()).Body!["limits"]!;
        var maxPage = limits["maxPageSize"]!.GetValue<int>();
        var report = limits["maxReportPageSize"]!.GetValue<int>();

        report.Should().BeGreaterThan(maxPage);

        (await staff.QueryAsync(Body("""  "strict": true, """, $$"""[{ "page": { "limit": {{report}} } }]"""))).StatusCode.Should().Be(200);

        (await staff.QueryAsync(Body("""  "strict": true, """, $$"""[{ "page": { "limit": {{report + 1}} } }]""")))
            .ShouldRefuse("PAGE_SIZE_EXCEEDED", 400)["message"]!.GetValue<string>().Should().Contain("report page maximum");

        (await staff.QueryAsync(Body("""  "strict": true, """, $$"""[{ "page": { "limit": {{maxPage + 1}}, "offset": 1 } }]""")))
            .ShouldRefuse("PAGE_SIZE_EXCEEDED", 400);

        (await staff.QueryAsync(Body("", $$"""[{ "page": { "limit": {{maxPage + 1}} } }]""")))
            .ShouldRefuse("PAGE_SIZE_EXCEEDED", 400)["message"]!.GetValue<string>().Should().Contain("A strict request without cursor or offset");
    }
}
