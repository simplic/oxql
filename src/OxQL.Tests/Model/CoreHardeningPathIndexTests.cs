using System.Diagnostics;
using System.Text;
using FluentAssertions;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.Tests.Model;

/// <summary>
/// The path index is built at startup. Types that reference one another densely are expanded
/// once per route to them, so the index has a ceiling: a model nobody has measured costs a
/// bounded amount there, and says what it left out.
/// </summary>
public class CoreHardeningPathIndexTests(ITestOutputHelper output)
{
    /// <summary>The most paths one entity's index describes; the indexer's own constant is internal.</summary>
    private const int Ceiling = 20_000;

    /// <summary>One entity over <paramref name="types"/> types, each holding a member of every other.</summary>
    private static string DenseDocument(int types)
    {
        var json = new StringBuilder("""{ "schemaVersion": "1.0", "service": "dense", "types": { """);

        json.Append(""" "dense.root": { "entity": true, "key": ["id"], "properties": [ { "name": "id", "kind": "guid" } """);

        for (var target = 0; target < types; target++)
            json.Append($$""", { "name": "m{{target}}", "kind": "object", "type": "#/types/t_{{target}}" }""");

        json.Append(" ] }");

        for (var type = 0; type < types; type++)
        {
            json.Append($$""", "t_{{type}}": { "properties": [ { "name": "label", "kind": "string" }""");

            for (var target = 0; target < types; target++)
                if (target != type)
                    json.Append($$""", { "name": "m{{target}}", "kind": "object", "type": "#/types/t_{{target}}" }""");

            json.Append(" ] }");
        }

        return json.Append(" } }").ToString();
    }

    [Fact]
    public void A_densely_cross_referencing_model_stops_at_the_ceiling_and_says_so_once()
    {
        // Twelve types reach one another over some 1.3 billion routes; unbounded, this build does not end.
        var timer = Stopwatch.StartNew();
        var model = DocumentModelBuilder.Build(DenseDocument(12));

        timer.Stop();
        output.WriteLine($"dense build: {timer.ElapsedMilliseconds} ms");

        var entity = model.Entities["dense.root"];

        entity.Paths.Should().HaveCount(Ceiling);
        entity.Path("id").Should().NotBeNull();
        entity.Path("m0.label").Should().NotBeNull("what the walk reached before the ceiling is described as ever");

        var finding = model.Findings.Should().ContainSingle(candidate => candidate.Code == BuildCodes.PathDepthExceeded && candidate.Detail == "path-count").Subject;

        finding.Target.Should().StartWith("dense.root#");
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_model_under_the_ceiling_is_indexed_whole()
    {
        // Four types are reached over 4 + 4·3 + 4·3·2 + 4·3·2·1 routes; each contributes its label
        // and its three members, and the root its key and its four.
        var model = DocumentModelBuilder.Build(DenseDocument(4));

        model.Entities["dense.root"].Paths.Should().HaveCount(5 + 4 * (4 + 12 + 24 + 24));
        model.Findings.Should().NotContain(finding => finding.Code == BuildCodes.PathDepthExceeded);
    }

    [Fact]
    public void Every_fixture_model_is_far_below_the_ceiling()
    {
        var models = ProbeModel.VendoredDocuments.Select(name => (name, model: DocumentModelBuilder.Build(ProbeModel.ReadFixture(name))))
            .Append(("probe (clr)", ProbeModel.Clr))
            .Append(("probe.json", ProbeModel.Document()));

        foreach (var (name, model) in models)
        {
            var largest = model.Entities.Values.MaxBy(entity => entity.Paths.Count)!;

            output.WriteLine($"{name}: largest entity {largest.Id} has {largest.Paths.Count} paths");

            largest.Paths.Count.Should().BeLessThan(Ceiling / 4, $"{name} must not come near the ceiling");
            model.Findings.Should().NotContain(finding => finding.Detail == "path-count");
        }
    }
}
