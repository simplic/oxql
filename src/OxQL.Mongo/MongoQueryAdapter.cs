using System.Globalization;
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
    private readonly IReadOnlyDictionary<string, IExternalResolver> _externalResolvers;
    private readonly Func<QueryPlan, QueryVariables?, CancellationToken, Task<List<BsonDocument>>> _executor;

    public MongoQueryAdapter(
        Func<string, IMongoCollection<BsonDocument>> collectionResolver,
        ICursorSerializer cursorSerializer,
        IEnumerable<IExternalResolver>? externalResolvers = null,
        Func<QueryPlan, QueryVariables?, CancellationToken, Task<List<BsonDocument>>>? executor = null)
    {
        _collectionResolver = collectionResolver ?? throw new ArgumentNullException(nameof(collectionResolver));
        _cursorSerializer = cursorSerializer ?? throw new ArgumentNullException(nameof(cursorSerializer));
        _externalResolvers = (externalResolvers ?? [])
            .ToDictionary(resolver => resolver.Source, StringComparer.OrdinalIgnoreCase);
        _executor = executor ?? ExecuteAggregationAsync;
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

        var results = await _executor(plan, variables, cancellationToken);

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
            var collection = _collectionResolver(plan.EntityType);

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

        results = await ApplyResolveStagesAsync(results, plan.Pipeline, cancellationToken);

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

    private async Task<List<BsonDocument>> ExecuteAggregationAsync(
        QueryPlan plan,
        QueryVariables? variables,
        CancellationToken cancellationToken)
    {
        var pipelineBuilder = new MongoPipelineBuilder(variables);

        CursorPayload? cursorPayload = null;
        var pageStage = plan.Page;
        if (!string.IsNullOrEmpty(pageStage.Cursor))
        {
            cursorPayload = _cursorSerializer.Deserialize(pageStage.Cursor, plan.Sort);
        }

        var pipeline = pipelineBuilder.Build(plan, cursorPayload);
        var collection = _collectionResolver(plan.EntityType);
        var pipelineDef = pipeline.Select(doc => (PipelineStageDefinition<BsonDocument, BsonDocument>)doc).ToList();
        var aggPipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(pipelineDef);

        return await collection
            .Aggregate(aggPipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<BsonDocument>> ApplyResolveStagesAsync(
        List<BsonDocument> documents,
        IReadOnlyList<PipelineStage> pipeline,
        CancellationToken cancellationToken)
    {
        var resolveStages = pipeline
            .Where(stage => stage.Resolve is not null)
            .Select(stage => stage.Resolve!)
            .ToList();

        if (documents.Count == 0 || resolveStages.Count == 0)
            return documents;

        var cache = new Dictionary<string, Dictionary<string, ExternalResolutionCacheEntry>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var resolveStage in resolveStages)
        {
            await ApplyResolveStageAsync(documents, resolveStage, cache, cancellationToken);
        }

        return documents;
    }

    private async Task ApplyResolveStageAsync(
        List<BsonDocument> documents,
        ResolveStage resolveStage,
        Dictionary<string, Dictionary<string, ExternalResolutionCacheEntry>> cache,
        CancellationToken cancellationToken)
    {
        if (!_externalResolvers.TryGetValue(resolveStage.Source, out var resolver))
        {
            throw new QueryValidationException(
                $"No external resolver is registered for source '{resolveStage.Source}'.");
        }

        if (!cache.TryGetValue(resolveStage.Source, out var sourceCache))
        {
            sourceCache = new Dictionary<string, ExternalResolutionCacheEntry>(StringComparer.Ordinal);
            cache[resolveStage.Source] = sourceCache;
        }

        var keys = documents
            .Select(document => GetResolverKey(document, resolveStage.LocalPath))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList()!;

        var missingKeys = keys
            .Where(key => !sourceCache.ContainsKey(key))
            .ToList();

        if (missingKeys.Count > 0)
        {
            var resolvedValues = await resolver.ResolveAsync(missingKeys, cancellationToken);

            foreach (var key in missingKeys)
            {
                sourceCache[key] = resolvedValues.TryGetValue(key, out var value)
                    ? new ExternalResolutionCacheEntry(true, value)
                    : new ExternalResolutionCacheEntry(false, null);
            }
        }

        foreach (var document in documents)
        {
            var key = GetResolverKey(document, resolveStage.LocalPath);
            var value = key is not null && sourceCache.TryGetValue(key, out var entry) && entry.Found
                ? entry.Value
                : null;

            SetFieldValue(document, resolveStage.As, value);
        }
    }

    private static string? GetResolverKey(BsonDocument doc, string path)
    {
        var value = GetFieldValue(doc, path);
        return value switch
        {
            null => null,
            string s when string.IsNullOrWhiteSpace(s) => null,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static void SetFieldValue(BsonDocument doc, string path, object? value)
    {
        var segments = path.Split('.');
        var current = doc;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (!current.TryGetValue(segment, out var existing) || existing is not BsonDocument nested)
            {
                nested = new BsonDocument();
                current[segment] = nested;
            }

            current = nested;
        }

        current[segments[^1]] = ToBsonValue(value);
    }

    private static BsonValue ToBsonValue(object? value)
    {
        if (value is null)
            return BsonNull.Value;

        if (value is BsonValue bsonValue)
            return bsonValue;

        if (BsonTypeMapper.TryMapToBsonValue(value, out var mapped))
            return mapped;

        return value.ToBsonDocument();
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

    private readonly record struct ExternalResolutionCacheEntry(bool Found, object? Value);
}
