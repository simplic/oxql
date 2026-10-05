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
    public async Task CC01_an_upTo_of_10_over_6000_rows_answers_at_least_10_stopped_by_the_cap()
    {
        var total = Corpus.Counts(Corpus.Template).A;
        total.Should().Be(Corpus.TemplateVolume).And.BeGreaterThan(10);

        var transport = await Transport();
        var capped = await transport.SendAsync(Corpus.Template, """[ { "project": { "id": 1 } }, { "page": { "limit": 2, "count": { "upTo": 10 } } } ]""");

        capped.ShouldCount("atLeast", 10, "cap").ShouldHaveNoDiagnostics();
        capped.Items.Should().HaveCount(2);

        // true counts one step beside the page; the count alone runs to the end within its budget.
        (await transport.SendAsync(Corpus.Template, """[ { "project": { "id": 1 } }, { "page": { "limit": 1, "count": true } } ]""")).ShouldCount("atLeast", 1_000, "budget");
        (await transport.CountOnlyAsync(Corpus.Template, """[ { "project": { "id": 1 } } ]""")).ShouldCount("exact", total);
    }

    [Fact]
    public async Task CC02_an_upTo_at_or_above_the_match_count_is_an_exact_count()
    {
        var count = Corpus.Counts(Corpus.Employee).A;
        count.Should().BeLessThan(100);

        var staff = await Staff();

        var above = await staff.SendAsync(Corpus.Employee, """[ { "project": { "id": 1 } }, { "page": { "limit": 2, "count": { "upTo": 100 } } } ]""");
        above.ShouldCount("exact", count).ShouldHaveNoDiagnostics();

        var at = await staff.SendAsync(Corpus.Employee, $$"""[ { "project": { "id": 1 } }, { "page": { "limit": 2, "count": { "upTo": {{count}} } } } ]""");
        at.ShouldCount("exact", count);
    }

    [Fact]
    public async Task CC03_a_bound_that_is_not_a_positive_integer_is_INVALID_PAGE_LIMIT_and_includeTotalCount_is_contract_1s()
    {
        var transport = await Transport();

        foreach (var member in new[] { "upTo", "budgetMs" })
            foreach (var value in new[] { "0", "-5", "1.5", "\"x\"" })
                (await transport.SendAsync(Corpus.Template, $$"""[ { "page": { "limit": 2, "count": { "{{member}}": {{value}} } } } ]""")).ShouldRefuseExactly("INVALID_PAGE_LIMIT")
                    .Should().Contain("is a positive integer", $"{member} {value}");

        (await transport.SendAsync(Corpus.Template, """[ { "page": { "limit": 2, "count": 10 } } ]""")).ShouldRefuseExactly("INVALID_PAGE_LIMIT");
        (await transport.SendAsync(Corpus.Template, """[ { "page": { "limit": 2, "count": { "max": 10 } } } ]""")).ShouldRefuseExactly("UNKNOWN_STAGE_MEMBER");

        // DG2 M3: contract 2 asks with 'count'; both forms of includeTotalCount are refused by name.
        foreach (var form in new[] { "true", "false", "10" })
        {
            var refused = await transport.SendAsync(Corpus.Template, $$"""[ { "page": { "limit": 2, "includeTotalCount": {{form}} } } ]""");

            refused.ShouldRefuseExactly("UNKNOWN_STAGE_MEMBER");
            refused.Body!["errors"]![0]!["params"]!.ToJsonString().Should().Be("""{"stage":"page","member":"includeTotalCount","reason":"removed","use":"count"}""");
        }

        var v1 = await Transport(contract: 1);

        (await v1.SendAsync(Corpus.Template, """[ { "page": { "limit": 2, "includeTotalCount": 10 } } ]""")).ShouldRefuseExactly("LEGACY_STAGE_UNSUPPORTED")
            .Should().Be("'includeTotalCount' is true or false under contract 1; a count cap needs contract 2 (X-OxQL-Contract: 2). This request was read as contract 1 because it carries no 'X-OxQL-Contract: 2' header.");
        (await v1.SendAsync(Corpus.Template, """[ { "page": { "limit": 2, "count": true } } ]""")).ShouldRefuseExactly("LEGACY_STAGE_UNSUPPORTED");

        // Contract 1's boolean is untouched, and its answer carries no 'count'.
        var legacy = (await v1.SendAsync(Corpus.Template, """[ { "page": { "limit": 1, "includeTotalCount": true } } ]""")).ShouldBeOk();

        legacy.PageInfo["totalCount"]!.GetValue<long>().Should().Be(Corpus.TemplateVolume);
        legacy.PageInfo.ContainsKey("count").Should().BeFalse();
    }

    [Fact]
    public async Task CC04_the_count_runs_under_the_collation_a_folded_match_bounded_below_its_match_count()
    {
        var matching = Corpus.Where(Corpus.Employee, row => Corpus.Text(row, "matchCode") is { } code && Order.EqualsCi(code, "muller")).Count;
        matching.Should().Be(4);

        var staff = await Staff();

        (await staff.SendAsync(Corpus.Employee, $$"""[ { "match": { "matchCode": { "eq": "MULLER" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "count": { "upTo": {{matching - 1}} } } } ]"""))
            .ShouldCount("atLeast", matching - 1, "cap");
        (await staff.SendAsync(Corpus.Employee, $$"""[ { "match": { "matchCode": { "eq": "MULLER" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "count": { "upTo": {{matching}} } } } ]"""))
            .ShouldCount("exact", matching);
    }

    /// <summary>What every page knows without a count step: the rows served, and one more while a page follows; the last page is exact.</summary>
    [Fact]
    public async Task CC05_a_walk_without_a_count_knows_the_rows_served_plus_one_and_ends_exact()
    {
        var total = Corpus.Counts(Corpus.Employee).A;
        var staff = await Staff();
        string? cursor = null;
        long served = 0;

        // The page size changes mid walk: the cursor carries the rows served, not a page number.
        foreach (var limit in new[] { 2, 5, 3, 500 })
        {
            var page = await staff.SendAsync(Corpus.Employee, $$"""[ { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1 } }, { "page": { "limit": {{limit}}{{(cursor is null ? "" : $", \"cursor\": \"{cursor}\"")}} } } ]""");

            served += page.Items.Count;

            if (page.HasNextPage)
                page.ShouldCount("atLeast", served + 1, "page");
            else
                page.ShouldCount("exact", served);

            cursor = page.NextCursor;
        }

        served.Should().Be(total);

        // A first page that holds every row is exact although a count was asked, and no step was waited for.
        var whole = await staff.SendAsync(Corpus.Employee, """[ { "page": { "limit": 500, "count": true } } ]""");

        whole.ShouldCount("exact", total);
        whole.Count!.ContainsKey("ms").Should().BeFalse();
    }
}
