using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Model;

namespace OxQL.Mongo;

/// <summary>The bounds one aggregate runs under, and the collation it carries when the pipeline folds a string.</summary>
public sealed record AggregateRunOptions(int MaxTimeMs, bool? AllowDiskUse, BsonDocument? Collation = null);

/// <summary>Runs one aggregate against an entity's collection. The executor's only contact with the database, so tests replace it with fixture rows.</summary>
public interface IAggregateRunner
{
    /// <summary>Runs the stages and returns every row.</summary>
    Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken);
}

/// <summary>Runs aggregates on the host's client, resolving each entity's collection from the model.</summary>
public sealed class MongoAggregateRunner : IAggregateRunner
{
    private readonly IMongoClient client;
    private readonly string? defaultDatabase;

    public MongoAggregateRunner(IMongoClient client, string? defaultDatabase)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.defaultDatabase = defaultDatabase;
    }

    /// <summary>The collection an entity is stored in.</summary>
    public IMongoCollection<BsonDocument> CollectionOf(EntityDef entity)
    {
        var databaseName = entity.Database ?? defaultDatabase
            ?? throw new InvalidOperationException($"No database is configured for '{entity.Id}' and the host names no default.");

        return client.GetDatabase(databaseName).GetCollection<BsonDocument>(entity.Collection);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
    {
        var collection = CollectionOf(entity);
        var definition = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages.Select(stage => (PipelineStageDefinition<BsonDocument, BsonDocument>)stage));
        var aggregateOptions = new AggregateOptions { MaxTime = TimeSpan.FromMilliseconds(options.MaxTimeMs) };

        if (options.AllowDiskUse is { } allowDiskUse)
            aggregateOptions.AllowDiskUse = allowDiskUse;

        if (options.Collation is { } collation)
            aggregateOptions.Collation = Collation.FromBsonDocument(collation);

        using var cursor = await collection.AggregateAsync(definition, aggregateOptions, cancellationToken).ConfigureAwait(false);

        return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
