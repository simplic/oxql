using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// A variable in a remote resolve's filter (DESIGN §3.5.5): the origin binds it and sends the value,
/// since an owner never receives <c>variables</c>. Sent with its wrapper the filter would travel unbound and
/// the owner refused it with <c>UNBOUND_VARIABLE</c>. Two values inside one cache lifetime are two
/// owner plans (§3.5.6) and never answer for each other.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsRemoteVariablesTests
{
    private const string ByVariable = """
        [ { "match": { "templateName": { "lte": "T-000024" } } },
          { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"], "filter": { "matchCode": { "eq": { "$var": "code" } } } } },
          { "project": { "id": 1, "veh": 1 } },
          { "sort": [ { "id": "asc" } ] },
          { "page": { "limit": 50 } } ]
        """;

    private static readonly Comparer<CorpusRow> ById = Comparer<CorpusRow>.Create(Corpus.CompareIds);

    [Fact]
    public async Task A_variable_in_a_remote_filter_is_bound_at_the_origin_and_two_values_do_not_share_the_cache()
    {
        var templates = Corpus.Where(Corpus.Template, row => Corpus.Text(row, "templateName") is { } name && Order.CompareCollated(name, "T-000024") <= 0).Order(ById).ToList();
        var targets = templates.Select(row => Corpus.GuidAt(row, "createUserId") is { } key ? Corpus.Rows(Corpus.Vehicle).FirstOrDefault(vehicle => vehicle.Id == key) : null).ToList();
        var codes = targets.Where(target => target is not null).Select(target => Corpus.Text(target!, "matchCode")!).Distinct().Take(2).ToList();
        codes.Should().HaveCount(2, "the head of the templates names more than one vehicle");

        var client = await Lab.ClientAsync(LabService.Transport);

        foreach (var code in codes)
        {
            var answer = await client.SendAsync(Corpus.Template, ByVariable, new { code });

            answer.ShouldHaveIds(templates.Select(row => row.Id)).ShouldHaveNoDiagnostics();
            answer.Strings("veh.matchCode").Should().Equal(
                targets.Select(target => target is not null && Corpus.Text(target, "matchCode") == code ? code : null),
                $"only the vehicles whose matchCode is {code} pass the filter the owner was sent");
        }
    }

    [Fact]
    public async Task An_unbound_variable_in_a_remote_filter_is_refused_at_the_origin_before_any_owner_is_asked()
    {
        var answer = await (await Lab.ClientAsync(LabService.Transport)).SendAsync(Corpus.Template, ByVariable);

        answer.ShouldRefuse("UNBOUND_VARIABLE", 400);
    }
}
