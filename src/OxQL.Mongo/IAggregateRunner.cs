using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Model;

namespace OxQL.Mongo;

/// <summary>The bounds one aggregate runs under, and the collation it carries when the pipeline folds a string.</summary>
public sealed record AggregateRunOptions(int MaxTimeMs, bool? AllowDiskUse, BsonDocument? Collation = null)
{
    /// <summary>
    /// How many rows the aggregate is expected to return at most: the cursor's batch is sized to it,
    /// so the rows arrive in the reply of the aggregate itself instead of a first batch of 101 and a
    /// <c>getMore</c> for the rest. Null leaves the server's default, which suits an aggregate that
    /// returns a row or two (a count, a probe). The server still ends a reply at 16 MB whatever the
    /// batch asks for, and the rest then costs a <c>getMore</c>.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    /// The command's <c>comment</c>: the request's correlation id, so an aggregate seen in the
    /// database's profiler, its slow-query log or <c>currentOp</c> is found from the request that
    /// sent it, and from the log lines of every service the request passed. It is not part of the
    /// query's shape, so it changes neither the plan nor what the plan cache keeps. Null sends none.
    /// </summary>
    public string? Comment { get; init; }
}

/// <summary>Runs one aggregate against an entity's collection. The executor's only contact with the database, so tests replace it with fixture rows.</summary>
public interface IAggregateRunner
{
    /// <summary>Runs the stages and returns every row.</summary>
    Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken);
}

/// <summary>Runs aggregates on the host's client, resolving each entity's collection from the model.</summary>
public sealed class MongoAggregateRunner : IAggregateRunner
{
    /// <summary>
    /// The largest cursor batch the runner asks for, whatever <see cref="AggregateRunOptions.BatchSize"/>
    /// names: twice the default report page. A larger result comes in several replies, as it would have.
    /// </summary>
    public const int MaxBatchSize = 10_000;

    /// <summary>The longest <see cref="AggregateRunOptions.Comment"/> sent; a longer one is cut.</summary>
    public const int MaxCommentLength = 128;

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

        if (options.Comment is { Length: > 0 } comment)
            aggregateOptions.Comment = comment.Length > MaxCommentLength ? comment[..MaxCommentLength] : comment;

        // A batch of 0 would ask for an empty first reply and a getMore for every row.
        if (options.BatchSize is { } batchSize and > 0)
            aggregateOptions.BatchSize = Math.Min(batchSize, MaxBatchSize);

        using var cursor = await collection.AggregateAsync(definition, aggregateOptions, cancellationToken).ConfigureAwait(false);

        return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
