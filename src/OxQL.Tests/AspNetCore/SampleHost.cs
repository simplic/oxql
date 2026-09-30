using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Tests.Bind;
using OxQL.Tests.Execute;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The Sample host under the test server: its real registration (controller, scope provider
/// from the <c>OrganizationId</c> header, options section), with the database replaced by the
/// fixture runner, the model by the probe graph, and the log captured.
/// </summary>
internal sealed class SampleHost : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? configure;

    public SampleHost(Action<OxQLOptions>? options = null, Action<IServiceCollection>? configure = null)
    {
        Options = BindHost.Options(options);
        this.configure = configure;
    }

    public FakeAggregateRunner Runner { get; } = new();

    public FakeIndexSource Indexes { get; } = new();

    public LogCapture Logs { get; } = new();

    public OxQLOptions Options { get; }

    public EntityModel Model { get; init; } = BindHost.Probe;

    /// <summary>The host environment; Development by default, which is one of the strict ones.</summary>
    public string Environment { get; init; } = "Development";

    /// <summary>Whether the host counts as running under continuous integration; pinned, so the machine's own variables decide nothing.</summary>
    public bool ContinuousIntegration { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<OxQLOptions>();
            services.AddSingleton(Options);
            services.RemoveAll<IEntityModelProvider>();
            services.AddSingleton<IEntityModelProvider>(new StaticEntityModelProvider(Model));
            services.RemoveAll<IAggregateRunner>();
            services.AddSingleton<IAggregateRunner>(Runner);
            services.RemoveAll<IIndexSource>();
            services.AddSingleton<IIndexSource>(Indexes);
            services.PostConfigure<OxQL.AspNetCore.OxQLEndpointOptions>(endpoint => endpoint.ContinuousIntegration = ContinuousIntegration);
            configure?.Invoke(services);
        });
    }

    /// <summary>Test hook: a pause between building the host and starting it, to widen the start race on purpose.</summary>
    internal TimeSpan StartDelay { get; init; }

    /// <summary>
    /// Starts the host and, when the Sample refuses to start, surfaces the refusal itself. The
    /// Sample runs its entry point on a thread of its own: <c>app.Run()</c> fails, disposes the
    /// host and ends, while this thread starts the deferred host, which first resolves
    /// <see cref="IHostApplicationLifetime"/> from that host. When the entry point wins the race
    /// the start throws <see cref="ObjectDisposedException"/> for the service provider instead of
    /// the startup error. The web host logs the startup error before it rethrows it, so the
    /// captured log holds it whichever thread wins.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = builder.Build();

        if (StartDelay > TimeSpan.Zero)
            Thread.Sleep(StartDelay);

        try
        {
            host.Start();
        }
        catch (ObjectDisposedException) when (Logs.StartupError is { } startupError)
        {
            ExceptionDispatchInfo.Throw(startupError);
        }

        return host;
    }

    /// <summary>A client under one contract (null: no header) for one organisation.</summary>
    public HttpClient Client(int? contract = 2, Guid? organisation = null)
    {
        var client = CreateClient();

        if (contract is { } declared)
            client.DefaultRequestHeaders.Add("X-OxQL-Contract", declared.ToString());

        client.DefaultRequestHeaders.Add("OrganizationId", (organisation ?? BindHost.Organisation).ToString());

        return client;
    }

    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    public static async Task<JsonNode?> Body(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());

    public static BsonDocument Row(Guid id, string number, int count = 1) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["Number"] = number,
        ["Count"] = count,
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
    };
}

/// <summary>Answers listIndexes from fixtures, per collection when one is given; records every entity asked for.</summary>
internal sealed class FakeIndexSource : IIndexSource
{
    public List<BsonDocument> IndexDocuments { get; set; } =
    [
        new BsonDocument { ["name"] = "_id_", ["key"] = new BsonDocument("_id", 1) },
    ];

    /// <summary>The index lists of particular collections; any other collection answers <see cref="IndexDocuments"/>.</summary>
    public Dictionary<string, List<BsonDocument>> ByCollection { get; } = new(StringComparer.Ordinal);

    public int IndexCalls => Asked.Count;

    /// <summary>The entities whose index list was read, in order.</summary>
    public List<string> Asked { get; } = [];

    public Task<IReadOnlyList<BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken)
    {
        Asked.Add(entity.Id);
        return Task.FromResult<IReadOnlyList<BsonDocument>>(ByCollection.TryGetValue(entity.Collection, out var listed) ? listed : IndexDocuments);
    }
}

/// <summary>Keeps every log line the host writes, with its category.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public IEnumerable<string> Of(string category) => Entries.Where(entry => entry.Category == category).Select(entry => entry.Message);

    /// <summary>The exception the web host logged when building the application failed (its startup error), if any.</summary>
    public Exception? StartupError { get; private set; }

    public void Dispose()
    {
    }

    private sealed class Logger(string category, LogCapture capture) : ILogger
    {
        private const string HostingDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null && category == HostingDiagnostics && logLevel == LogLevel.Critical)
                capture.StartupError ??= exception;

            capture.Entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
