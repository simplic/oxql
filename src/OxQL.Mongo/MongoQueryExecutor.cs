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

        // Check cache
        var cacheKey = _normalizer.GenerateCacheKey(normalized);
        if (!_cache.TryGet(cacheKey, out var plan))
        {
            plan = _planner.CreatePlan(request);
            _cache.Set(cacheKey, plan!);
        }

        // Execute
        return await _adapter.ExecuteAsync(plan!, request.Variables, cancellationToken);
    }
}
