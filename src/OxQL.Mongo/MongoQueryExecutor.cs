using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace OxQL.Mongo;

/// <summary>
/// High-level MongoDB query executor that orchestrates validation, normalization, planning, caching, and execution.
/// </summary>
public sealed class MongoQueryExecutor : IQueryExecutor<BsonDocument>
{
    private readonly IQueryValidator _validator;
    private readonly IQueryRequestNormalizer _normalizer;
    private readonly IQueryPlanner _planner;
    private readonly IQueryPlanCache _cache;
    private readonly IQueryAdapter<BsonDocument> _adapter;

    public MongoQueryExecutor(
        IMongoCollection<BsonDocument> collection,
        OxQLOptions options,
        ICursorSerializer? cursorSerializer = null,
        IQueryPlanCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);

        var cursor = cursorSerializer ?? new Core.Cursor.CursorSerializer();
        _validator = new Core.Validation.QueryValidator(options);
        _normalizer = new Core.Normalization.QueryRequestNormalizer(options);
        _planner = new Core.Planning.QueryPlanner(_normalizer);
        _cache = cache ?? new Core.Caching.QueryPlanCache(options);
        _adapter = new MongoQueryAdapter(_ => collection, cursor);
    }

    public MongoQueryExecutor(
        IQueryValidator validator,
        IQueryRequestNormalizer normalizer,
        IQueryPlanner planner,
        IQueryPlanCache cache,
        IQueryAdapter<BsonDocument> adapter)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public async Task<QueryResponse<BsonDocument>> ExecuteAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        // Validate
        var validation = _validator.Validate(request);
        if (!validation.IsValid)
        {
            throw new QueryValidationException(validation.Errors);
        }

        // Normalize
        var normalized = _normalizer.Normalize(request);

        // Check cache. The key describes the query *shape* only — the page cursor is
        // deliberately not part of it (see GenerateCacheKey), because every page of a
        // result set shares one plan. A cached plan must therefore never carry a cursor,
        // and the cursor of the current request is applied to it after the lookup.
        var cacheKey = _normalizer.GenerateCacheKey(normalized);
        if (!_cache.TryGet(cacheKey, out var plan))
        {
            plan = _planner.CreatePlan(request);
            _cache.Set(cacheKey, WithCursor(plan!, null));
        }

        // Execute
        var planForRequest = WithCursor(plan!, GetCursor(normalized));
        return await _adapter.ExecuteAsync(planForRequest, request.Variables, cancellationToken);
    }

    public Task<IReadOnlyList<object>> ExplainAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = _validator.Validate(request);
        if (!validation.IsValid)
            throw new QueryValidationException(validation.Errors);

        var normalized = _normalizer.Normalize(request);
        var plan = _planner.CreatePlan(normalized);
        var stages = _adapter.Explain(plan, request.Variables);
        return Task.FromResult(stages);
    }

    /// <summary>
    /// Gets the cursor of the request's page stage, or null when it is the first page.
    /// </summary>
    private static string? GetCursor(QueryRequest request)
        => request.Pipeline.FirstOrDefault(stage => stage.Page is not null)?.Page?.Cursor;

    /// <summary>
    /// Returns the plan with <paramref name="cursor"/> applied to its page stage. The page
    /// stage inside the pipeline is kept in sync as well so no stale cursor can be read
    /// from there either, even though the Mongo pipeline builder only uses its limit.
    /// </summary>
    private static QueryPlan WithCursor(QueryPlan plan, string? cursor)
    {
        if (plan.Page.Cursor == cursor) return plan;

        return plan with
        {
            Page = plan.Page with { Cursor = cursor },
            Pipeline = plan.Pipeline
                .Select(stage => stage.Page is null ? stage : stage with { Page = stage.Page with { Cursor = cursor } })
                .ToList()
        };
    }
}
