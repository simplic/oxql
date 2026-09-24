using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Harness;

/// <summary>
/// A lab fleet with the corpus in it and a chaos owner mounted for <c>owner.widget</c>: what a
/// suite queries. <see cref="SharedAsync"/> is the run's fleet, seeded once with every service
/// and read-only by convention; its owner is frozen in <see cref="ChaosMode.Ok"/>. A suite that
/// writes, needs data of its own, or drives the owner's chaos modes creates a private fleet with
/// <see cref="CreateAsync"/> and disposes it.
/// </summary>
public sealed class CorpusFleet : IAsyncDisposable
{
    private static readonly Lazy<Task<CorpusFleet>> Shared = new(StartSharedAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lazy<Task<TimeSpan>> bulk;
    private readonly bool owned;

    private CorpusFleet(LabFleet fleet, ChaosOwner owner, IReadOnlyList<LabService> seeded, TimeSpan seedTime, bool owned)
    {
        Fleet = fleet;
        Owner = owner;
        Seeded = seeded;
        SeedTime = seedTime;
        this.owned = owned;
        bulk = new Lazy<Task<TimeSpan>>(() => BulkRows.SeedAsync(fleet), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The fleet underneath: hosts, variants, databases.</summary>
    public LabFleet Fleet { get; }

    /// <summary>The owner of <c>owner.widget</c> in this fleet. The shared fleet's is frozen.</summary>
    public ChaosOwner Owner { get; }

    /// <summary>The services whose corpus rows this fleet holds.</summary>
    public IReadOnlyList<LabService> Seeded { get; }

    /// <summary>How long writing the corpus took.</summary>
    public TimeSpan SeedTime { get; }

    /// <summary>The run's fleet: every service seeded once, hosts shared by every suite. Read-only by convention.</summary>
    public static Task<CorpusFleet> SharedAsync() => Shared.Value;

    /// <summary>
    /// A private fleet under its own databases, seeded with the corpus of <paramref name="seed"/>
    /// (every service when null, none when empty) and with a chaos owner of its own. Dispose it:
    /// its databases are dropped. <paramref name="extra"/> runs after the corpus is written, for the
    /// rows a suite adds for itself.
    /// </summary>
    public static async Task<CorpusFleet> CreateAsync(string purpose, IReadOnlyList<LabService>? seed = null, Func<LabFleet, Task>? extra = null)
    {
        var fleet = LabFleet.Create(purpose);
        var owner = new ChaosOwner();

        try
        {
            fleet.Mount(LabFleet.ExternalOwner, owner);

            var services = seed ?? LabService.All;
            var took = await CorpusSeeder.SeedAsync(fleet, services);

            if (extra is not null)
                await extra(fleet);

            return new CorpusFleet(fleet, owner, services, took, owned: true);
        }
        catch
        {
            await fleet.DisposeAsync();
            throw;
        }
    }

    private static async Task<CorpusFleet> StartSharedAsync()
    {
        var fleet = LabFleet.Shared;
        var owner = new ChaosOwner().Freeze();

        fleet.Mount(LabFleet.ExternalOwner, owner);

        var took = await CorpusSeeder.SeedAsync(fleet, LabService.All);

        return new CorpusFleet(fleet, owner, LabService.All, took, owned: false);
    }

    /// <summary>
    /// Writes the 100 001 bulk rows of organisation C into this fleet's transport database, once;
    /// later calls wait for the first. Only the tests that need volume above the count cap call it.
    /// </summary>
    public Task<TimeSpan> SeedBulkAsync() => bulk.Value;

    /// <summary>A client for one service, as organisation A under contract 2 unless told otherwise.</summary>
    public LabClient Client(LabService service, Org org = Org.A, int? contract = 2) =>
        new(this, service, () => Fleet.HostAsync(service), new LabClient.Identity(org.Id(), LabIdentity.User, contract, NoHeaders));

    /// <summary>
    /// A client for a variant of a service: the same model and database with
    /// <paramref name="configuration"/> layered over the defaults, created on first use and cached
    /// by <paramref name="key"/> (a key names one configuration for the fleet's lifetime).
    /// </summary>
    public LabClient Variant(LabService service, string key, IReadOnlyDictionary<string, string?> configuration, Org org = Org.A, int? contract = 2) =>
        new(this, service, () => Fleet.VariantAsync(service, key, configuration), new LabClient.Identity(org.Id(), LabIdentity.User, contract, NoHeaders));

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>The variant with <c>OxQL:Explain:Enabled</c>, which the explain route needs.</summary>
    internal Task<FleetHost> ExplainHostAsync(LabService service) =>
        Fleet.VariantAsync(service, "explain", new Dictionary<string, string?> { ["OxQL:Explain:Enabled"] = "true" });

    public async ValueTask DisposeAsync()
    {
        if (owned)
            await Fleet.DisposeAsync();
    }
}
