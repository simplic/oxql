using OxQL.Core.Interfaces;
using OxQL.Core.Models;

namespace OxQL.Core.Planning;

/// <summary>
/// Creates provider-neutral query plans from normalized requests.
/// </summary>
public sealed class QueryPlanner : IQueryPlanner
{
    private readonly IQueryRequestNormalizer _normalizer;

    public QueryPlanner(IQueryRequestNormalizer normalizer)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
    }

    public QueryPlan CreatePlan(QueryRequest request)
    {
        var normalized = _normalizer.Normalize(request);
        var cacheKey = _normalizer.GenerateCacheKey(normalized);

        var sortFields = normalized.Pipeline
            .Where(s => s.Sort is not null)
            .SelectMany(s => s.Sort!)
            .ToList();

        var page = normalized.Pipeline
            .Select(s => s.Page)
            .FirstOrDefault(p => p is not null)
            ?? new PageStage { Limit = 50 };

        return new QueryPlan
        {
            EntityType = normalized.EntityType,
            Pipeline = normalized.Pipeline,
            Sort = sortFields,
            Page = page,
            CacheKey = cacheKey
        };
    }
}
