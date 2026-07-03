using OxQL.Core.Cursor;
using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using OxQL.Mongo.Builders;
using MongoDB.Bson;
using MongoDB.Driver;

namespace OxQL.Mongo;

/// <summary>
/// MongoDB implementation of IQueryAdapter that translates query plans to aggregation pipelines.
/// </summary>
public sealed class MongoQueryAdapter : IQueryAdapter<BsonDocument>
{
    private readonly ICursorSerializer _cursorSerializer;
    private readonly Func<string, IMongoCollection<BsonDocument>> _collectionResolver;

    public MongoQueryAdapter(
        Func<string, IMongoCollection<BsonDocument>> collectionResolver,
        ICursorSerializer cursorSerializer)
    {
        _collectionResolver = collectionResolver ?? throw new ArgumentNullException(nameof(collectionResolver));
        _cursorSerializer = cursorSerializer ?? throw new ArgumentNullException(nameof(cursorSerializer));
    }

    public async Task<QueryResponse<BsonDocument>> ExecuteAsync(
        QueryPlan plan,
        QueryVariables? variables,
        CancellationToken cancellationToken = default)
    {
        var pipelineBuilder = new MongoPipelineBuilder(variables);

        // Decode cursor if present
        CursorPayload? cursorPayload = null;
        var pageStage = plan.Page;
        if (!string.IsNullOrEmpty(pageStage.Cursor))
        {
            cursorPayload = _cursorSerializer.Deserialize(pageStage.Cursor, plan.Sort);
        }

        // Build main pipeline
        var pipeline = pipelineBuilder.Build(plan, cursorPayload);

        // Resolve the collection for this entity type
        var collection = _collectionResolver(plan.EntityType);

        // Execute aggregation
        var pipelineDef = pipeline.Select(doc => (PipelineStageDefinition<BsonDocument, BsonDocument>)doc).ToList();
        var aggPipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(pipelineDef);

        var results = await collection
            .Aggregate(aggPipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken);

        // Determine if there's a next page (we fetched limit+1)
        var hasNextPage = results.Count > pageStage.Limit;
        if (hasNextPage)
        {
            results = results.Take(pageStage.Limit).ToList();
        }

        // Build next cursor
        string? nextCursor = null;
        if (hasNextPage && results.Count > 0)
        {
            var lastDoc = results[^1];
            var cursorData = CursorSerializer.CreateFromDocument(
                plan.Sort,
                path => GetFieldValue(lastDoc, path));
            nextCursor = _cursorSerializer.Serialize(cursorData);
        }

        // Get total count if requested
        long? totalCount = null;
        if (pageStage.IncludeTotalCount)
        {
            var countPipeline = pipelineBuilder.BuildCountPipeline(plan);
            var countPipelineDef = countPipeline.Select(doc => (PipelineStageDefinition<BsonDocument, BsonDocument>)doc).ToList();
            var countAgg = PipelineDefinition<BsonDocument, BsonDocument>.Create(countPipelineDef);

            var countResult = await collection
                .Aggregate(countAgg, cancellationToken: cancellationToken)
                .FirstOrDefaultAsync(cancellationToken);

            if (countResult is not null && countResult.Contains("totalCount"))
            {
                totalCount = countResult["totalCount"].ToInt64();
            }
            else
            {
                totalCount = 0;
            }
        }

        return new QueryResponse<BsonDocument>
        {
            Items = results,
            PageInfo = new PageInfo
            {
                HasNextPage = hasNextPage,
                NextCursor = nextCursor,
                TotalCount = totalCount
            }
        };
    }

    private static object? GetFieldValue(BsonDocument doc, string path)
    {
        var mongoPath = path == "id" ? "_id" : path;
        var segments = mongoPath.Split('.');
        BsonValue current = doc;

        foreach (var segment in segments)
        {
            if (current is BsonDocument d && d.Contains(segment))
            {
                current = d[segment];
            }
            else
            {
                return null;
            }
        }

        return current switch
        {
            BsonString s => s.Value,
            BsonInt32 i => i.Value,
            BsonInt64 l => l.Value,
            BsonDouble d => d.Value,
            BsonDateTime dt => dt.ToUniversalTime(),
            BsonBoolean b => b.Value,
            BsonNull => null,
            BsonObjectId oid => oid.Value.ToString(),
            _ => current.ToString()
        };
    }
}
