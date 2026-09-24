using FluentAssertions;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.NonQuery;

/// <summary>
/// The engine half of the legacy internal-auth battery. The route one service calls on
/// another is an OxS controller: its <c>i-api-key</c> scheme, the key comparison, the blank-key
/// posture and its absence from swagger are base-package code and stay with its tests. What it
/// hands the engine is a batch and an organisation read from the forwarded
/// <c>OrganizationId</c> header, which is exactly what the lab hosts' batch route receives: the
/// organisation a batch runs under, what happens without one, and the body cap that runs
/// before anything else.
/// </summary>
[Trait("Category", "Integration")]
public class InternalBatchTests
{
    private const string Body = """{ "queries": [{ "entityType": "staff.employee", "pipeline": [{ "project": { "id": 1, "organizationId": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }] }] }""";

    private static async Task<LabClient> Staff() => await Lab.ClientAsync(LabService.Staff);

    [Fact]
    public async Task IA5_a_batch_with_no_organisation_answers_no_rows_and_a_coded_refusal_per_entry()
    {
        var answer = await (await Staff()).Anonymous().BatchBodyAsync(Body);

        answer.StatusCode.Should().Be(200, answer.ToString());
        var entry = answer.Results.Should().ContainSingle().Subject;
        entry.IsPage.Should().BeFalse("an unscoped batch must never return rows");
        entry.Type.Should().Be("access_denied");
        entry.ErrorCodes.Should().Equal(["ACCESS_DENIED"]);
    }

    [Fact]
    public async Task IA6_IA7_a_batch_runs_under_the_forwarded_organisation_and_answers_its_rows_and_no_others()
    {
        var (a, b) = Corpus.Counts(Corpus.Employee);
        a.Should().NotBe(b, "the two organisations must hold different counts for the comparison to bite");

        foreach (var org in new[] { Org.A, Org.B })
        {
            var answer = await (await Staff()).As(org).BatchBodyAsync(Body);

            answer.StatusCode.Should().Be(200);
            var entry = answer.Results.Should().ContainSingle().Subject;
            entry.ShouldHaveIds(Corpus.AllIds(Corpus.Employee, org));
            entry.Strings("organizationId").Distinct().Should().Equal([org.Id().ToString("D")]);
        }
    }

    [Fact]
    public async Task IA8_IA9_an_organisation_value_that_is_not_one_organisation_answers_no_rows_and_never_a_500()
    {
        var values = new Dictionary<string, string>
        {
            ["not a guid"] = "not-a-guid",
            ["empty"] = "",
            ["a guid-like string one digit short"] = "11111111-1111-1111-1111-11111111111",
            ["two organisations joined by a proxy"] = $"{Org.A.Id()}, {Org.B.Id()}",
        };

        foreach (var (what, value) in values)
        {
            var answer = await (await Staff()).Anonymous().WithHeader(LabIdentity.OrganisationHeader, value).BatchBodyAsync(Body);

            answer.StatusCode.Should().Be(200, $"{what}: {answer}");
            var entry = answer.Results.Should().ContainSingle().Subject;
            entry.IsPage.Should().BeFalse($"{what} returned rows");
            entry.ErrorCodes.Should().Equal(["ACCESS_DENIED"], what);
        }
    }

    [Fact]
    public async Task IA12_a_batch_body_over_the_cap_is_refused_413_before_the_organisation_is_even_read()
    {
        var huge = $$"""{ "queries": [{ "entityType": "staff.employee", "pipeline": [{ "match": { "id": { "in": {{Json.Ids(Enumerable.Range(0, 12_000).Select(n => Ids.Dangling(n)))}} } } }, { "page": { "limit": 1 } }] }] }""";
        huge.Length.Should().BeGreaterThan(new LimitOptions().MaxRequestBytes);

        var withOrganisation = await (await Staff()).BatchBodyAsync(huge);
        var without = await (await Staff()).Anonymous().BatchBodyAsync(huge);

        foreach (var answer in new[] { withOrganisation, without })
        {
            answer.StatusCode.Should().Be(413);
            answer.Type.Should().Be("validation_error");
            answer.ErrorCodes.Should().Equal(["REQUEST_TOO_LARGE"]);
        }
    }

    [Fact]
    public async Task IA13_a_get_on_the_batch_route_is_405()
    {
        (await (await Staff()).GetAsync("OxQL/batch")).StatusCode.Should().Be(405);
    }
}
