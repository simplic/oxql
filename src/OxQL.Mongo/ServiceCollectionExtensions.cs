using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OxQL.Core;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model.Build;
using OxQL.Mongo.Compat;
using OxQL.Mongo.Explain;
using OxQL.Mongo.Resolve;

namespace OxQL.Mongo;

/// <summary>Configuration of the MongoDB engine.</summary>
public sealed class MongoOxQLOptions
{
    /// <summary>The connection string; when empty the host registers its own <see cref="IMongoClient"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The database an entity without an override is stored in.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Assemblies to scan for entities when the host does not hand over a model. The model is
    /// then built on the first request, after every registration.
    /// </summary>
    public List<Assembly> AssembliesToScan { get; } = [];

    /// <summary>Whether refusals of class <c>internal_error</c> carry the exception message.</summary>
    public bool IncludeErrorDetails { get; set; }

    /// <summary>Adds assemblies to scan for entities.</summary>
    public MongoOxQLOptions ScanAssemblies(params Assembly[] assemblies)
    {
        AssembliesToScan.AddRange(assemblies);
        return this;
    }
}

/// <summary>Registers the MongoDB engine.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the engine over MongoDB. Needs <c>AddOxQLCore</c>. The host may register
    /// <see cref="IEntityModelProvider"/> (the base package does, from its schema build),
    /// <see cref="IMongoClient"/>, <see cref="IIndexSource"/> (the explain advisory) and
    /// <see cref="IRemoteQueryClient"/> itself; each has a default here except the remote
    /// client, without which remote resolves are refused.
    /// </summary>
    public static IServiceCollection AddOxQLMongo(this IServiceCollection services, Action<MongoOxQLOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var mongoOptions = new MongoOxQLOptions();
        configure(mongoOptions);

        services.AddSingleton(mongoOptions);
        services.AddSingleton<JsonConverter>(new BsonDocumentJsonConverter());

        if (!string.IsNullOrEmpty(mongoOptions.ConnectionString))
            services.TryAddSingleton<IMongoClient>(_ => new MongoClient(mongoOptions.ConnectionString));

        services.TryAddSingleton<IEntityModelProvider>(_ => new LazyEntityModelProvider(() => ClrModelBuilder.Build(mongoOptions.AssembliesToScan)));
        services.TryAddSingleton<IAggregateRunner>(provider => new MongoAggregateRunner(provider.GetRequiredService<IMongoClient>(), mongoOptions.DatabaseName));
        services.TryAddSingleton<IIndexSource>(provider => new MongoIndexSource(provider.GetRequiredService<IMongoClient>(), mongoOptions.DatabaseName));
        services.TryAddSingleton(provider => new ResolveCache(provider.GetRequiredService<OxQLOptions>()));

        services.AddSingleton<IQueryEngine>(provider =>
        {
            var logger = provider.GetService<ILogger<MongoQueryEngine>>();

            // The engine is built once, so a limit the registration had to adjust is said once.
            foreach (var adjustment in provider.GetServices<OxQLOptionsAdjustment>())
                logger?.LogWarning("OxQL configuration adjusted: {Adjustment}", adjustment.Message);

            return new MongoQueryEngine(
                provider.GetRequiredService<IEntityModelProvider>(),
                provider.GetRequiredService<IAggregateRunner>(),
                provider.GetRequiredService<CursorCodec>(),
                provider.GetRequiredService<OxQLOptions>(),
                provider.GetService<IRemoteQueryClient>(),
                logger,
                mongoOptions.IncludeErrorDetails,
                provider.GetService<IIndexSource>(),
                provider.GetRequiredService<ResolveCache>());
        });

        return services;
    }
}
