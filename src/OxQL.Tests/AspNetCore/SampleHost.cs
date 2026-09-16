using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            configure?.Invoke(services);
        });
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

/// <summary>Answers listIndexes and the server explain from fixtures; records every call.</summary>
internal sealed class FakeIndexSource : IIndexSource
{
    public List<BsonDocument> IndexDocuments { get; set; } =
    [
        new BsonDocument { ["name"] = "_id_", ["key"] = new BsonDocument("_id", 1) },
    ];

    public BsonDocument? ExplainDocument { get; set; }

    public int IndexCalls { get; private set; }

    public int ExplainCalls { get; private set; }

    public Task<IReadOnlyList<BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken)
    {
        IndexCalls++;
        return Task.FromResult<IReadOnlyList<BsonDocument>>(IndexDocuments);
    }

    public Task<BsonDocument?> ExplainAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, CancellationToken cancellationToken)
    {
        ExplainCalls++;
        return Task.FromResult(ExplainDocument);
    }
}

/// <summary>Keeps every log line the host writes, with its category.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

    public IEnumerable<string> Of(string category) => Entries.Where(entry => entry.Category == category).Select(entry => entry.Message);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
