using System.Reflection;
using System.Text.Json.Serialization;
using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using OxQL.Core.Registration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace OxQL.Mongo;

/// <summary>
/// Configuration options specific to the MongoDB adapter.
/// </summary>
public sealed class MongoOxQLOptions
{
    /// <summary>
    /// The MongoDB connection string.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The default MongoDB database name (used when no database override is present on a type).
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// The default collection name, used when the query's <c>entityType</c> is not found
    /// in the type registry.
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// Assemblies to scan for <see cref="OxQL.Core.Attributes.OxQLTypeAttribute"/>.
    /// Call <see cref="ScanAssemblies"/> to add assemblies fluently.
    /// </summary>
    public List<Assembly> AssembliesToScan { get; } = [];

    /// <summary>
    /// Adds assemblies to scan for <see cref="OxQL.Core.Attributes.OxQLTypeAttribute"/>
    /// decorated types.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddOxQLMongo(options =>
    /// {
    ///     options.ConnectionString = "...";
    ///     options.DatabaseName     = "mydb";
    ///     options.ScanAssemblies(typeof(Vehicle).Assembly);
    /// });
    /// </code>
    /// </example>
    public MongoOxQLOptions ScanAssemblies(params Assembly[] assemblies)
    {
        AssembliesToScan.AddRange(assemblies);
        return this;
    }
}

/// <summary>
/// Extension methods for registering OxQL.Mongo services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds OxQL.Mongo services to the dependency injection container.
    /// <para>
    /// If any assemblies are configured via <see cref="MongoOxQLOptions.ScanAssemblies"/>,
    /// they are scanned for <see cref="OxQL.Core.Attributes.OxQLTypeAttribute"/> decorated
    /// classes and registered in the <see cref="OxQLTypeRegistry"/>. The adapter will then
    /// route each query to the collection specified by the matching attribute.
    /// </para>
    /// </summary>
    public static IServiceCollection AddOxQLMongo(
        this IServiceCollection services,
        Action<MongoOxQLOptions> configure)
    {
        var mongoOptions = new MongoOxQLOptions();
        configure(mongoOptions);

        // Register the STJ converter so ASP.NET Core's JSON option wiring in
        // AddOxQLAspNetCore / AddOxQLEndpoint can pick it up automatically.
        services.AddSingleton<JsonConverter>(new BsonDocumentJsonConverter());

        if (string.IsNullOrEmpty(mongoOptions.ConnectionString))
            return services;

        var client   = new MongoClient(mongoOptions.ConnectionString);
        var database = client.GetDatabase(mongoOptions.DatabaseName);

        // Default collection — used when entityType has no registered mapping
        var defaultCollection = database.GetCollection<BsonDocument>(
            mongoOptions.CollectionName ?? "documents");

        services.AddSingleton(defaultCollection);

        // Populate the type registry from scanned assemblies
        services.AddSingleton<MongoCollectionResolver>(sp =>
        {
            var registry = sp.GetRequiredService<OxQLTypeRegistry>();

            if (mongoOptions.AssembliesToScan.Count > 0)
                registry.ScanAssemblies([.. mongoOptions.AssembliesToScan]);

            // Build a per-entityType collection resolver.
            // Caches resolved IMongoCollection instances so the database round-trip
            // is only performed once per entityType per application lifetime.
            var collectionCache = new Dictionary<string, IMongoCollection<BsonDocument>>(
                StringComparer.OrdinalIgnoreCase);

            IMongoCollection<BsonDocument> Resolve(string entityType)
            {
                if (collectionCache.TryGetValue(entityType, out var cached))
                    return cached;

                IMongoCollection<BsonDocument> col;

                if (registry.TryGet(entityType, out var reg))
                {
                    // Use the database override if specified, otherwise the default database
                    var db = reg.DatabaseName is not null
                        ? client.GetDatabase(reg.DatabaseName)
                        : database;

                    col = db.GetCollection<BsonDocument>(reg.CollectionName);
                }
                else
                {
                    col = defaultCollection;
                }

                collectionCache[entityType] = col;
                return col;
            }

            return new MongoCollectionResolver(Resolve);
        });

        services.AddSingleton<IQueryAdapter<BsonDocument>, MongoQueryAdapter>(sp =>
        {
            var resolver         = sp.GetRequiredService<MongoCollectionResolver>();
            var cursorSerializer = sp.GetRequiredService<OxQL.Core.Interfaces.ICursorSerializer>();
            return new MongoQueryAdapter(defaultCollection, cursorSerializer, resolver.Resolve);
        });

        services.AddSingleton<IQueryExecutor<BsonDocument>, MongoQueryExecutor>(sp =>
        {
            var validator  = sp.GetRequiredService<OxQL.Core.Interfaces.IQueryValidator>();
            var normalizer = sp.GetRequiredService<OxQL.Core.Interfaces.IQueryRequestNormalizer>();
            var planner    = sp.GetRequiredService<OxQL.Core.Interfaces.IQueryPlanner>();
            var cache      = sp.GetRequiredService<OxQL.Core.Interfaces.IQueryPlanCache>();
            var adapter    = sp.GetRequiredService<IQueryAdapter<BsonDocument>>();
            return new MongoQueryExecutor(validator, normalizer, planner, cache, adapter);
        });

        return services;
    }
}

/// <summary>
/// Wraps the per-entityType collection resolver function so it can be registered in DI
/// without conflicting with other <c>Func&lt;&gt;</c> registrations.
/// </summary>
internal sealed class MongoCollectionResolver(Func<string, IMongoCollection<BsonDocument>> resolve)
{
    public Func<string, IMongoCollection<BsonDocument>> Resolve { get; } = resolve;
}

