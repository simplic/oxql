using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Model;

namespace OxQL.Mongo.Explain;

/// <summary>
/// What the opt-in explain advisory reads from the server (DESIGN §4.1): a collection's index list,
/// cached per collection. Nothing else: explain never runs the pipeline, not even under the
/// server's own <c>explain</c> command. Tests replace it with fixtures; a host without one gets no
/// advisory.
/// </summary>
public interface IIndexSource
{
    /// <summary>The <c>listIndexes</c> documents of the entity's collection.</summary>
    Task<IReadOnlyList<BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken);
}

/// <summary>Reads index lists on the host's client; <c>listIndexes</c> is cached 60 seconds per collection.</summary>
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

    private IMongoDatabase DatabaseOf(EntityDef entity)
    {
        var name = entity.Database ?? defaultDatabase
            ?? throw new InvalidOperationException($"No database is configured for '{entity.Id}' and the host names no default.");

        return client.GetDatabase(name);
    }
}
