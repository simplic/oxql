using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Health;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Chain;

/// <summary>
/// A private fleet whose <c>owner.widget</c> is a <see cref="ScriptedOwner"/>, with the conformance
/// and staff corpus: organisation A's three conformance rows name W-1, W-2 and W-3 in id order. One
/// per test class; a case resets the owner, gives its resolve a filter of its own
/// (<see cref="Resolve"/>) so no other case's cache entry answers it, and reads the owner's batch
/// log after <see cref="ScriptedOwner.Batches"/>' count.
/// </summary>
public sealed class ScriptedOwnerFleet : IAsyncLifetime
{
    private LabFleet? fleet;

    public ScriptedOwner Owner { get; } = new();

    public LabFleet Fleet => fleet ?? throw new InvalidOperationException("The fixture has not started.");

    public async Task InitializeAsync()
    {
        fleet = LabFleet.Create("e14b_scripted");
        fleet.Mount(LabFleet.ExternalOwner, Owner);
        await CorpusSeeder.SeedAsync(fleet, [LabService.Conformance, LabService.Staff]);
    }

    public async Task DisposeAsync()
    {
        if (fleet is not null)
            await fleet.DisposeAsync();
    }

    /// <summary>The conformance host, or a variant of it under <paramref name="configuration"/>.</summary>
    public Task<FleetHost> HostAsync(string? variant = null, IReadOnlyDictionary<string, string?>? configuration = null) =>
        variant is null ? Fleet.HostAsync(LabService.Conformance) : Fleet.VariantAsync(LabService.Conformance, variant, configuration ?? new Dictionary<string, string?>());

    /// <summary>Has the host read the owner's shallow health now (engine version, batch cap), as its health route does.</summary>
    public static async Task LearnOwnerAsync(FleetHost host)
    {
        using var client = host.Client();
        using var _ = await client.GetAsync("OxQL/health");
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;
    }

    /// <summary>
    /// The widget resolve of a case, with its own cache key (a filter every widget passes), then the
    /// rows in id order; <paramref name="extra"/> is written into the resolve (<c>, "onMissing": …</c>),
    /// <paramref name="after"/> as stages after it.
    /// </summary>
    public static JsonObject Resolve(string caseId, bool strict = false, string extra = "", string after = "")
    {
        var body = Json.Request(Corpus.Conformance, JsonNode.Parse($$"""
            [ { "resolve": { "path": "widgetCodeExplicit", "as": "w", "select": ["name"], "filter": { "name": { "neq": "{{caseId}}" } }{{extra}} } },
              {{after}}
              { "project": { "id": 1, "widgetCodeExplicit": 1, "w": 1 } },
              { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]
            """)!);

        if (strict)
            body["strict"] = true;

        return body;
    }

    /// <summary><c>POST OxQL/query</c> on <paramref name="host"/> as organisation A.</summary>
    public static async Task<WireAnswer> QueryAsync(FleetHost host, JsonObject body)
    {
        using var client = host.Client(Org.A.Id());
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        var watch = Stopwatch.StartNew();
        using var response = await client.PostAsync("OxQL/query", content);
        var text = await response.Content.ReadAsStringAsync();

        return new WireAnswer(response.StatusCode, Json.TryParse(text), text, new Dictionary<string, string>(), watch.Elapsed, $"POST OxQL/query {body.ToJsonString()}");
    }
}
