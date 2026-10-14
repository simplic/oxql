using System.Net;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.RemoteChaos;

/// <summary>
/// A page some of whose records the host kept from an earlier answer while the owner does not
/// answer for the others: the rows of the kept records carry them, the others are null, and the
/// diagnostic says for how many keys the alias is null and how many were kept.
/// </summary>
[Trait("Category", "Integration")]
public class RemoteCacheMixTests
{
    private const string Widgets = """[ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } }, { "sort": [{ "count": "asc" }] }, { "project": { "name": 1, "w.name": 1 } }, { "page": { "limit": 10 } } ]""";

    [Fact]
    public async Task The_diagnostic_of_an_owner_that_does_not_answer_counts_the_keys_it_is_null_for_beside_the_kept_ones()
    {
        await using var fleet = await CorpusFleet.CreateAsync("cache-mix", seed: [LabService.Conformance]);
        var client = fleet.Client(LabService.Conformance);

        // One record is answered and kept.
        var first = await client.SendAsync(Corpus.Conformance, """[ { "match": { "count": 1 } }, { "resolve": { "path": "widgetCodeExplicit", "as": "w" } }, { "project": { "name": 1, "w.name": 1 } } ]""");

        first.ShouldBeOk().ShouldHaveNoDiagnostics();
        first.Items.Single()!["w"]!["name"]!.GetValue<string>().Should().Be("Widget One");

        fleet.Owner.Set(new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.ServiceUnavailable });

        var mixed = await client.SendAsync(Corpus.Conformance, Widgets);

        mixed.ShouldBeOk();
        mixed.Items.Should().HaveCount(3);
        mixed.Items[0]!["w"]!["name"]!.GetValue<string>().Should().Be("Widget One", "its record was kept");
        mixed.Items.Skip(1).Should().OnlyContain(row => row!["w"] == null);

        var diagnostic = mixed.ShouldHaveDiagnostic("RESOLVE_UNREACHABLE");

        diagnostic["params"]!["keys"]!.GetValue<int>().Should().Be(2);
        diagnostic["params"]!["cached"]!.GetValue<int>().Should().Be(1);
        diagnostic["message"]!.GetValue<string>().Should().Contain("'w' is null on this page for the 2 keys asked for").And.Contain("1 key kept from an earlier answer");

        // With nothing kept, the alias is null on the page, as the diagnostic says.
        await using var cold = await CorpusFleet.CreateAsync("cache-mix-cold", seed: [LabService.Conformance]);

        cold.Owner.Set(new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.ServiceUnavailable });

        var none = await cold.Client(LabService.Conformance).SendAsync(Corpus.Conformance, Widgets);
        var plain = none.ShouldHaveDiagnostic("RESOLVE_UNREACHABLE");

        none.Items.Should().OnlyContain(row => row!["w"] == null);
        plain["message"]!.GetValue<string>().Should().EndWith("'w' is null on this page.");
        plain["params"]!.AsObject().ContainsKey("keys").Should().BeFalse();
    }
}
