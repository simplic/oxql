using System.Text.Json.Nodes;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.RemoteChaos;

/// <summary>
/// A private fleet whose owner of <c>owner.widget</c> a test class may drive and count: the
/// conformance and staff corpus, the chaos owner, and the requests the conformance host sent it.
/// One per test class (<see cref="IClassFixture{TFixture}"/>): a class runs its cases one after
/// another, so a case sets the owner, reads its log after <see cref="ChaosOwner.Mark"/>, and the
/// next case starts from <see cref="ChaosOwner.Reset"/>.
/// <para>
/// Cache discipline: a host caches every resolved key and every semi-join id list, so a case
/// gives its resolve a filter of its own (<see cref="Resolve"/>) and its semi-join an operand of
/// its own (<see cref="SemiJoin"/>). Without that one case's answer is served to the next and the
/// owner mode under test is never asked.
/// </para>
/// </summary>
public sealed class OwnerFleet : IAsyncLifetime
{
    private CorpusFleet? fleet;

    public CorpusFleet Fleet => fleet ?? throw new InvalidOperationException("The fixture has not started.");

    public ChaosOwner Owner => Fleet.Owner;

    public async Task InitializeAsync() =>
        fleet = await CorpusFleet.CreateAsync("b4_owner", seed: [LabService.Conformance, LabService.Staff]);

    public async Task DisposeAsync()
    {
        if (fleet is not null)
            await fleet.DisposeAsync();
    }

    /// <summary>The conformance host of this fleet, or one of its variants.</summary>
    public LabClient Conformance(Org org = Org.A, string? variant = null, IReadOnlyDictionary<string, string?>? configuration = null) =>
        variant is null
            ? Fleet.Client(LabService.Conformance, org)
            : Fleet.Variant(LabService.Conformance, variant, configuration ?? new Dictionary<string, string?>(), org);

    /// <summary>The resolve stage of a case: widgetCodeExplicit onto the owner, with a filter that matches every widget and is the case's own cache key.</summary>
    public static string ResolveStage(string caseId, string select = """["name"]""") =>
        $$"""{ "resolve": { "path": "widgetCodeExplicit", "as": "w", "select": {{select}}, "filter": { "name": { "neq": "{{caseId}}" } } } }""";

    /// <summary>A resolve page of organisation A's three rows, id-ordered, each with its <c>w</c> alias.</summary>
    public static string Resolve(string caseId, string select = """["name"]""") =>
        $$"""[ {{ResolveStage(caseId, select)}}, { "project": { "id": 1, "widgetCodeExplicit": 1, "w": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]""";

    /// <summary>
    /// A semi-join: the owner decides which rows exist. The operand carries the case id so the
    /// semi-join cache key is the case's own; <c>Widget One</c> is the member that matches, so the
    /// honest answer is always exactly the <c>W-1</c> row.
    /// </summary>
    public static string SemiJoin(string caseId, string page = """{ "limit": 10, "includeTotalCount": true }""") =>
        $$"""
        [ {{ResolveStage(caseId)}},
          { "match": { "w.name": { "in": ["Widget One", "{{caseId}}"] } } },
          { "project": { "id": 1, "widgetCodeExplicit": 1, "w": 1 } },
          { "sort": [ { "id": "asc" } ] },
          { "page": {{page}} } ]
        """;

    /// <summary>The organisation's conformance rows in id order.</summary>
    public static IReadOnlyList<CorpusRow> Rows(Org org = Org.A) => Corpus.Rows(Corpus.Conformance, org).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).ToList();

    /// <summary>The widget name each row's <c>widgetCodeExplicit</c> names, from the owner's own table.</summary>
    public static IReadOnlyList<string?> WidgetNames(Org org = Org.A) =>
        Rows(org).Select(row => ChaosOwner.Widgets.FirstOrDefault(widget => widget.Code == Corpus.Text(row, "widgetCodeExplicit"))?.Name).ToList();

    /// <summary>The <c>limits</c> object of a health answer.</summary>
    public static JsonObject Limits(WireAnswer health) => health.Body?["limits"] as JsonObject ?? throw new InvalidOperationException($"no limits: {health}");

    /// <summary>The <c>capabilities</c> of a health answer.</summary>
    public static IReadOnlyList<string> Capabilities(WireAnswer health) =>
        (health.Body?["capabilities"] as JsonArray ?? []).Select(capability => capability!.GetValue<string>()).ToList();
}
