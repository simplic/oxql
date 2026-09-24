using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Model;

namespace OxQL.Mongo.Explain;

/// <summary>
/// What the explain advisory reads from the server: the collection's indexes (cached per
/// collection) and, for a pipeline with a <c>$lookup</c>, the server's own explain of the
/// page pipeline. Tests replace it with fixtures; a host without one gets no advisory.
/// </summary>
public interface IIndexSource
{
    /// <summary>The <c>listIndexes</c> documents of the entity's collection.</summary>
    Task<IReadOnlyList<BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken);

    /// <summary>
    /// The server's explain of the stages, or null when it cannot be obtained. On MongoDB 8.0
    /// the pipelined <c>$lookup</c> form the compiler emits reports <c>indexesUsed</c> only at
    /// <c>executionStats</c> verbosity (<c>queryPlanner</c> shows nothing for it), so the explain
    /// executes the page pipeline. It therefore runs under the same ceiling as the query would,
    /// <paramref name="maxTimeMs"/>, and an explain the server cuts off yields null like any
    /// other it cannot give. It is only asked for when the pipeline has a <c>$lookup</c>.
    /// </summary>
    Task<BsonDocument?> ExplainAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, int maxTimeMs, CancellationToken cancellationToken);
}

/// <summary>Reads indexes and explains on the host's client; <c>listIndexes</c> is cached 60 seconds per collection.</summary>
public sealed class MongoIndexSource : IIndexSource
{
    /// <summary>How long a collection's index list is kept.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IMongoClient client;
    private readonly string? defaultDatabase;
    private readonly TimeSpan ttl;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<BsonDocument> Indexes)> cache = new(StringComparer.Ordinal);

    public MongoIndexSource(IMongoClient client, string? defaultDatabase, TimeSpan? ttl = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.defaultDatabase = defaultDatabase;
        this.ttl = ttl ?? CacheTtl;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken)
    {
        var database = DatabaseOf(entity);
        var key = database.DatabaseNamespace.DatabaseName + "." + entity.Collection;

        if (cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Indexes;

        using var cursor = await database.GetCollection<BsonDocument>(entity.Collection).Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
        var indexes = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);

        cache[key] = (DateTimeOffset.UtcNow + ttl, indexes);

        return indexes;
    }

    /// <inheritdoc/>
    public async Task<BsonDocument?> ExplainAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, int maxTimeMs, CancellationToken cancellationToken)
    {
        try
        {
            return await DatabaseOf(entity).RunCommandAsync<BsonDocument>(ExplainCommand(entity, stages, maxTimeMs), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException)
        {
            return null;
        }
    }

    /// <summary>
    /// The explain command of the stages at <c>executionStats</c> verbosity. The time ceiling sits
    /// on the explain command itself, which is the command the server runs and bounds.
    /// </summary>
    public static BsonDocument ExplainCommand(EntityDef entity, IReadOnlyList<BsonDocument> stages, int maxTimeMs)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(stages);

        return new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["aggregate"] = entity.Collection,
                ["pipeline"] = new BsonArray(stages),
                ["cursor"] = new BsonDocument(),
            },
            ["verbosity"] = "executionStats",
            ["maxTimeMS"] = Math.Max(1, maxTimeMs),
        };
    }

    private IMongoDatabase DatabaseOf(EntityDef entity)
    {
        var name = entity.Database ?? defaultDatabase
            ?? throw new InvalidOperationException($"No database is configured for '{entity.Id}' and the host names no default.");

        return client.GetDatabase(name);
    }
}
