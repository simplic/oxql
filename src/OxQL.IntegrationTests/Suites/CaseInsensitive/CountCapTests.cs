using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Filters;
using Xunit;

namespace OxQL.IntegrationTests.Suites.CaseInsensitive;

/// <summary>
/// The count cap: <c>includeTotalCount</c> as a positive integer counts up to that many and says
/// when it stopped. Ported from the legacy <c>final/case-insensitive</c> battery (CC-01 … CC-04).
/// </summary>
[Trait("Category", "Integration")]
public class CountCapTests
{
    private static Task<LabClient> Transport(int? contract = 2) => Lab.ClientAsync(LabService.Transport, contract: contract);

    private static Task<LabClient> Staff() => Lab.ClientAsync(LabService.Staff);

    [Fact]
    public async Task CC01_a_cap_of_10_over_6000_rows_answers_10_capped_with_TOTAL_COUNT_CAPPED_naming_the_cap()
    {
        var total = Corpus.Counts(Corpus.Template).A;
        total.Should().Be(Corpus.TemplateVolume).And.BeGreaterThan(10);

        var transport = await Transport();
        var capped = await transport.SendAsync(Corpus.Template, """[ { "project": { "id": 1 } }, { "page": { "limit": 2, "includeTotalCount": 10 } } ]""");

        capped.ShouldHaveTotal(10, capped: true);
        capped.Items.Should().HaveCount(2);
        capped.DiagnosticCodes.Should().Equal("TOTAL_COUNT_CAPPED");
        capped.Diagnostics[0]["params"]!["cap"]!.GetValue<int>().Should().Be(10, capped.ToString());

        // true still counts everything: the host's own cap is far above the entity.
        (await transport.SendAsync(Corpus.Template, """[ { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } } ]""")).ShouldHaveTotal(total, capped: false);
    }

    [Fact]
    public async Task CC02_a_cap_at_or_above_the_match_count_is_an_exact_count_not_capped()
    {
        var count = Corpus.Counts(Corpus.Employee).A;
        count.Should().BeLessThan(100);

        var staff = await Staff();

        var above = await staff.SendAsync(Corpus.Employee, """[ { "project": { "id": 1 } }, { "page": { "limit": 2, "includeTotalCount": 100 } } ]""");
        above.ShouldHaveTotal(count, capped: false).ShouldHaveNoDiagnostics();

        var at = await staff.SendAsync(Corpus.Employee, $$"""[ { "project": { "id": 1 } }, { "page": { "limit": 2, "includeTotalCount": {{count}} } } ]""");
        at.ShouldHaveTotal(count, capped: false);
    }

    [Fact]
    public async Task CC03_a_zero_or_negative_cap_is_INVALID_PAGE_LIMIT_and_the_number_form_under_contract_1_is_LEGACY_STAGE_UNSUPPORTED()
    {
        var transport = await Transport();

        foreach (var cap in new[] { 0, -5 })
            (await transport.SendAsync(Corpus.Template, $$"""[ { "page": { "limit": 2, "includeTotalCount": {{cap}} } } ]""")).ShouldRefuseExactly("INVALID_PAGE_LIMIT")
                .Should().Contain("is not a positive integer", $"cap {cap}");

        var v1 = await Transport(contract: 1);

        (await v1.SendAsync(Corpus.Template, """[ { "page": { "limit": 2, "includeTotalCount": 10 } } ]""")).ShouldRefuseExactly("LEGACY_STAGE_UNSUPPORTED")
            .Should().Be("'includeTotalCount' is true or false under contract 1; a count cap needs contract 2 (X-OxQL-Contract: 2).");

        // Contract 1's boolean is untouched.
        (await v1.SendAsync(Corpus.Template, """[ { "page": { "limit": 1, "includeTotalCount": true } } ]""")).ShouldBeOk().TotalCount.Should().Be(Corpus.TemplateVolume);
    }

    [Fact]
    public async Task CC04_the_cap_counts_under_the_collation_a_folded_match_capped_below_its_match_count()
    {
        var matching = Corpus.Where(Corpus.Employee, row => Corpus.Text(row, "matchCode") is { } code && Order.EqualsCi(code, "muller")).Count;
        matching.Should().Be(4);

        var staff = await Staff();

        (await staff.SendAsync(Corpus.Employee, $$"""[ { "match": { "matchCode": { "eq": "MULLER" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": {{matching - 1}} } } ]"""))
            .ShouldHaveTotal(matching - 1, capped: true);
        (await staff.SendAsync(Corpus.Employee, $$"""[ { "match": { "matchCode": { "eq": "MULLER" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": {{matching}} } } ]"""))
            .ShouldHaveTotal(matching, capped: false);
    }
}
