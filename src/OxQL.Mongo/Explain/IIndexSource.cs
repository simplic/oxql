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

/// <summary>
/// Reads index lists on the host's client; <c>listIndexes</c> is cached 60 seconds per collection.
/// The read carries a time limit of its own (<see cref="DefaultMaxTime"/> unless the host names one),
/// sent to the server as the command's <c>maxTimeMS</c>: a caller that stops waiting does not leave
/// the command running.
/// </summary>
public sealed class MongoIndexSource : IIndexSource
{
    /// <summary>How long a collection's index list is kept.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>How long one <c>listIndexes</c> may take when the host names no limit: the default of <c>Explain:TimeoutMs</c>.</summary>
    public static readonly TimeSpan DefaultMaxTime = TimeSpan.FromSeconds(2);

    private readonly IMongoClient client;
    private readonly string? defaultDatabase;
    private readonly TimeSpan ttl;
    private readonly TimeSpan maxTime;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<BsonDocument> Indexes)> cache = new(StringComparer.Ordinal);

    /// <summary>The source over <paramref name="client"/>; <paramref name="maxTime"/> bounds one <c>listIndexes</c> on the server.</summary>
    public MongoIndexSource(IMongoClient client, string? defaultDatabase, TimeSpan? ttl = null, TimeSpan? maxTime = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.defaultDatabase = defaultDatabase;
        this.ttl = ttl ?? CacheTtl;
        this.maxTime = maxTime is { } limit && limit > TimeSpan.Zero ? limit : DefaultMaxTime;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken)
    {
        var database = DatabaseOf(entity);
        var key = database.DatabaseNamespace.DatabaseName + "." + entity.Collection;

        if (cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Indexes;

        var indexes = await ListAsync(database, entity.Collection, cancellationToken).ConfigureAwait(false);

        cache[key] = (DateTimeOffset.UtcNow + ttl, indexes);

        return indexes;
    }

    /// <summary>
    /// One <c>listIndexes</c> under <c>maxTimeMS</c>. The driver's own index listing takes no time
    /// limit, so the command is sent as it is; a collection that does not exist yet has no indexes,
    /// as the driver's listing answers it.
    /// </summary>
    private async Task<IReadOnlyList<BsonDocument>> ListAsync(IMongoDatabase database, string collection, CancellationToken cancellationToken)
    {
        var command = new BsonDocument
        {
            ["listIndexes"] = collection,
            // A collection holds at most 64 indexes, so one reply carries the list.
            ["cursor"] = new BsonDocument("batchSize", MaxIndexes),
            ["maxTimeMS"] = Math.Max(1L, (long)Math.Ceiling(maxTime.TotalMilliseconds)),
        };

        try
        {
            var reply = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken).ConfigureAwait(false);

            return [.. reply["cursor"]["firstBatch"].AsBsonArray.Select(index => index.AsBsonDocument)];
        }
        catch (MongoCommandException exception) when (exception.Code == NamespaceNotFound)
        {
            return [];
        }
    }

    /// <summary>More than a collection can hold (64), so the first reply is the whole list.</summary>
    private const int MaxIndexes = 1_000;

    /// <summary>The server's code for a collection that does not exist.</summary>
    private const int NamespaceNotFound = 26;

    private IMongoDatabase DatabaseOf(EntityDef entity)
    {
        var name = entity.Database ?? defaultDatabase
            ?? throw new InvalidOperationException($"No database is configured for '{entity.Id}' and the host names no default.");

        return client.GetDatabase(name);
    }
}
