using System.Collections.Concurrent;
using System.Security.Cryptography;
using MongoDB.Driver;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// A set of lab hosts that resolve into each other: one standard host per service, started on
/// first use, each over its own database, plus variants (the same service and database under
/// changed configuration) created on first use and cached by key. Remote calls of any host go
/// to the standard host of the owning service in the same fleet.
/// <para>
/// <see cref="Shared"/> is the run's fleet: its databases hold read-only data and its hosts
/// live until the process ends. A test that writes creates its own fleet with
/// <see cref="Create"/>, which drops its databases on disposal.
/// </para>
/// </summary>
public sealed class LabFleet : IAsyncDisposable
{
    /// <summary>
    /// The key of the one service the fleet knows without serving it: the programmable remote
    /// owner of the conformance entity's widget references. Until a handler is mounted for it
    /// (<see cref="Mount"/>) it is configured but unreachable.
    /// </summary>
    public const string ExternalOwner = "owner";

    /// <summary>Every service key a host of the fleet may reference: the lab services and the external owner.</summary>
    public static readonly IReadOnlySet<string> Configured =
        new HashSet<string>(LabService.All.Select(service => service.Key).Append(ExternalOwner), StringComparer.Ordinal);

    private static readonly Lazy<LabFleet> SharedFleet = new(() => new LabFleet("shared", dropOnDispose: false), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<string, Lazy<Task<FleetHost>>> hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> databases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HttpClient> owners = new(StringComparer.Ordinal);
    private readonly string purpose;
    private readonly bool dropOnDispose;

    private LabFleet(string purpose, bool dropOnDispose)
    {
        this.purpose = purpose;
        this.dropOnDispose = dropOnDispose;
    }

    /// <summary>The run's shared fleet.</summary>
    public static LabFleet Shared => SharedFleet.Value;

    /// <summary>The cursor signing key of the fleet's hosts; generated per fleet, never stored.</summary>
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Configuration every host of this fleet starts with, over the defaults and under a variant's own
    /// keys. Set before the first host starts: a host reads it once.
    /// </summary>
    public Dictionary<string, string?> Configuration { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long every aggregate of this fleet's hosts takes at least, as a database a network away
    /// would make it: with it, which aggregates run side by side and which one after another shows in
    /// <see cref="Aggregates"/>. Zero by default. Set before the first host starts.
    /// </summary>
    public TimeSpan AggregateDelay { get; set; }

    /// <summary>The aggregates the fleet's hosts ran while <see cref="AggregateDelay"/> is set: the service, the entity, and when each started and ended.</summary>
    public ConcurrentQueue<(string Service, string Entity, long Started, long Ended)> Aggregates { get; } = new();

    /// <summary>A private fleet for a test that writes; its databases are dropped on disposal.</summary>
    public static LabFleet Create(string purpose) => new(purpose, dropOnDispose: true);

    /// <summary>The standard host of a service.</summary>
    public Task<FleetHost> HostAsync(LabService service) => HostAsync(service, "", null);

    /// <summary>
    /// A variant of a service: the same model and database, with <paramref name="configuration"/>
    /// layered over the defaults. The first call under a key creates it; later calls return the
    /// same host whatever configuration they pass, so a key names one configuration.
    /// </summary>
    public Task<FleetHost> VariantAsync(LabService service, string key, IReadOnlyDictionary<string, string?> configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(configuration);

        return HostAsync(service, key, configuration);
    }

    /// <summary>The database of a service in this fleet.</summary>
    public async Task<IMongoDatabase> DatabaseAsync(LabService service) =>
        (await MongoFixture.ClientAsync()).GetDatabase(DatabaseName(service));

    /// <summary>
    /// Serves <paramref name="serviceKey"/> from a handler instead of a lab host: how a fake or
    /// misbehaving owner joins the fleet. Once per key and fleet.
    /// </summary>
    public void Mount(string serviceKey, HttpMessageHandler handler)
    {
        if (!Configured.Contains(serviceKey))
            throw new ArgumentException($"'{serviceKey}' is not a service the fleet's hosts know.", nameof(serviceKey));

        if (!owners.TryAdd(serviceKey, new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }))
            throw new InvalidOperationException($"'{serviceKey}' already has an owner in this fleet.");
    }

    /// <summary>The client that reaches the owner of a service: a mounted handler, or the service's standard host; null when nothing serves it.</summary>
    internal async Task<HttpClient?> OwnerClientAsync(string serviceKey)
    {
        if (owners.TryGetValue(serviceKey, out var mounted))
            return mounted;

        if (LabService.All.FirstOrDefault(service => service.Key == serviceKey) is not { } owner)
            return null;

        var host = await HostAsync(owner);

        return owners.GetOrAdd(serviceKey, _ => host.Server.CreateClient());
    }

    private Task<FleetHost> HostAsync(LabService service, string variant, IReadOnlyDictionary<string, string?>? configuration)
    {
        var key = variant.Length == 0 ? service.Key : $"{service.Key}#{variant}";
        var host = hosts.GetOrAdd(key, _ => new Lazy<Task<FleetHost>>(
            async () => await FleetHost.StartAsync(this, service, variant, await MongoFixture.ClientAsync(), DatabaseName(service), configuration),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return host.Value;
    }

    private string DatabaseName(LabService service) =>
        databases.GetOrAdd(service.Key, _ => MongoFixture.DatabaseName($"{purpose}_{service.Key}"));

    public async ValueTask DisposeAsync()
    {
        foreach (var host in hosts.Values.Where(host => host.IsValueCreated))
        {
            try
            {
                await (await host.Value).DisposeAsync();
            }
            catch (Exception) when (host.Value.IsFaulted)
            {
                // A host that never started has nothing to stop.
            }
        }

        foreach (var client in owners.Values)
            client.Dispose();

        if (dropOnDispose && databases.Count > 0)
        {
            var client = await MongoFixture.ClientAsync();

            foreach (var name in databases.Values)
                await client.DropDatabaseAsync(name);
        }
    }
}
