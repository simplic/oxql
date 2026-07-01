using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Caches compiled query plans by their normalized shape.
/// </summary>
public interface IQueryPlanCache
{
    /// <summary>
    /// Attempts to retrieve a cached query plan by key.
    /// </summary>
    /// <param name="cacheKey">The cache key.</param>
    /// <param name="plan">The cached plan if found.</param>
    /// <returns>True if the plan was found in cache.</returns>
    bool TryGet(string cacheKey, out QueryPlan? plan);

    /// <summary>
    /// Stores a query plan in the cache.
    /// </summary>
    /// <param name="cacheKey">The cache key.</param>
    /// <param name="plan">The plan to cache.</param>
    void Set(string cacheKey, QueryPlan plan);

    /// <summary>
    /// Gets current cache statistics.
    /// </summary>
    CacheStatistics GetStatistics();

    /// <summary>
    /// Clears all cached plans.
    /// </summary>
    void Clear();
}
