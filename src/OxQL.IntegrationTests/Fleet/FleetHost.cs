using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OxQL.AspNetCore;
using OxQL.Core;
using OxQL.Core.Engine;
using OxQL.Mongo;
using OxQL.Model.Addon;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// One lab service running in process: a <see cref="WebApplication"/> on a test server that
/// registers the engine the way a real service does (options from the <c>OxQL</c> section,
/// the MongoDB engine over the service's own database, the controller, the scope provider,
/// the addon source and the remote client), with test implementations of the four seams.
/// </summary>
public sealed class FleetHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private FleetHost(LabService service, string variant, IMongoDatabase database, WebApplication app, LogCapture logs)
    {
        Service = service;
        Variant = variant;
        Database = database;
        Logs = logs;
        this.app = app;
        Server = app.GetTestServer();
    }

    public LabService Service { get; }

    /// <summary>The variant key; empty for the service's standard host.</summary>
    public string Variant { get; }

    /// <summary>The service's database, shared by the standard host and its variants.</summary>
    public IMongoDatabase Database { get; }

    public TestServer Server { get; }

    public LogCapture Logs { get; }

    public IServiceProvider Services => app.Services;

    /// <summary>
    /// Starts a host. <paramref name="configuration"/> is layered over the host's defaults, so a
    /// variant names only the keys it changes (<c>OxQL:Limits:MaxPageSize</c> and the like).
    /// </summary>
    internal static async Task<FleetHost> StartAsync(
        LabFleet fleet,
        LabService service,
        string variant,
        IMongoClient client,
        string databaseName,
        IReadOnlyDictionary<string, string?>? configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Development is one of the strict environments: a declared remote reference into a
            // service the host does not know stops it, as it stops a real service.
            EnvironmentName = "Development",
            ApplicationName = typeof(FleetHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        // A variant's keys replace the defaults of the same name.
        var settings = new Dictionary<string, string?>(Defaults(fleet), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in fleet.Configuration)
            settings[key] = value;

        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key] = value;

        builder.Configuration.AddInMemoryCollection(settings);

        var logs = new LogCapture();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        var services = builder.Services;
        var database = client.GetDatabase(databaseName);

        // Registered before the core, so the engine's empty default does not win.
        services.AddScoped<IAddonDefinitionSource>(_ => new TestAddonSource(database));
        services.AddOxQLCore(builder.Configuration.GetSection("OxQL"));

        // The base package hands the engine the model its schema build made; here it is the
        // service's own entities, built once per run and shared by the service's variants.
        services.AddSingleton<IEntityModelProvider>(new LazyEntityModelProvider(() => service.Model));
        services.AddSingleton(client);

        // A fleet that slows its aggregates down registers the runner itself; the engine's own is the default.
        if (fleet.AggregateDelay > TimeSpan.Zero)
            services.AddSingleton<IAggregateRunner>(new DelayedRunner(new MongoAggregateRunner(client, databaseName), fleet, service.Key));

        services.AddOxQLMongo(options =>
        {
            options.DatabaseName = databaseName;
            options.IncludeErrorDetails = true;
        });

        services.AddOxQLAspNetCore(options => options.ContinuousIntegration = false);
        services.AddOxQLScope<TestScopeProvider>();
        services.AddSingleton(fleet);
        services.AddSingleton<IRemoteQueryClient, InMemoryRemoteClient>();

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        return new FleetHost(service, variant, database, app, logs);
    }

    private static IEnumerable<KeyValuePair<string, string?>> Defaults(LabFleet fleet) =>
    [
        new("OxQL:Cursor:SigningKey", fleet.SigningKey),
        // The suites explain in bursts as one user; the limits of explain have tests of their own.
        new("OxQL:Explain:RatePerMinute", "600000"),
        new("OxQL:Explain:RateBurst", "100000"),
        new("OxQL:Explain:MaxConcurrentPerUser", "1000"),
        new("OxQL:Explain:MaxConcurrentPerHost", "1000"),
    ];

    /// <summary>A client for one organisation (organisation A when null) under the given contract; an anonymous client sends no identity.</summary>
    public HttpClient Client(Guid? organisation = default, int contract = 2, bool anonymous = false)
    {
        var client = Server.CreateClient();

        client.DefaultRequestHeaders.Add(LabIdentity.ContractHeader, contract.ToString());

        if (!anonymous)
        {
            client.DefaultRequestHeaders.Add(LabIdentity.OrganisationHeader, (organisation ?? LabIdentity.OrganisationA).ToString());
            client.DefaultRequestHeaders.Add(LabIdentity.UserHeader, LabIdentity.User.ToString());
        }

        return client;
    }

    /// <summary>Posts a query body (the whole request JSON) to <c>/OxQL/query</c> as <paramref name="organisation"/>.</summary>
    public async Task<FleetResponse> QueryAsync(string body, Guid? organisation = default)
    {
        using var client = Client(organisation);
        using var content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var response = await client.PostAsync("OxQL/query", content);
        var text = await response.Content.ReadAsStringAsync();

        return new FleetResponse(response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), text);
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    public override string ToString() => Variant.Length == 0 ? Service.Key : $"{Service.Key}#{Variant}";
}

/// <summary>
/// The engine's runner with every aggregate taking at least <see cref="LabFleet.AggregateDelay"/>, and
/// recorded with its start and end (<see cref="LabFleet.Aggregates"/>). A local server answers in
/// well under a millisecond, which hides what a hosted one makes plain: aggregates sent one after
/// another add up, aggregates sent together do not.
/// </summary>
internal sealed class DelayedRunner(IAggregateRunner inner, LabFleet fleet, string service) : IAggregateRunner
{
    public async Task<IReadOnlyList<MongoDB.Bson.BsonDocument>> AggregateAsync(OxQL.Model.EntityDef entity, IReadOnlyList<MongoDB.Bson.BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            await Task.Delay(fleet.AggregateDelay, cancellationToken);

            return await inner.AggregateAsync(entity, stages, options, cancellationToken);
        }
        finally
        {
            fleet.Aggregates.Enqueue((service, entity.Id, started, System.Diagnostics.Stopwatch.GetTimestamp()));
        }
    }
}

/// <summary>A host's answer: the status, the parsed body and the raw text for failure messages.</summary>
public sealed record FleetResponse(HttpStatusCode Status, JsonNode? Body, string Text)
{
    /// <summary>The <c>items</c> of a success body.</summary>
    public JsonArray Items => Body?["items"]?.AsArray() ?? throw new InvalidOperationException($"The answer carries no items ({(int)Status}): {Text}");

    public override string ToString() => $"{(int)Status} {Text}";
}

/// <summary>Keeps every log line a host writes, with its category.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
