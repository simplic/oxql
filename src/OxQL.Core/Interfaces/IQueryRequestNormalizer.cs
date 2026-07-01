using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Normalizes query requests to canonical form for caching and comparison.
/// </summary>
public interface IQueryRequestNormalizer
{
    /// <summary>
    /// Normalizes a query request by adding defaults, sorting properties,
    /// adding tie-breaker sorts, and enforcing page limits.
    /// </summary>
    /// <param name="request">The original query request.</param>
    /// <returns>A normalized query request.</returns>
    QueryRequest Normalize(QueryRequest request);

    /// <summary>
    /// Generates a cache key for the query shape (excluding variable values).
    /// </summary>
    /// <param name="request">The normalized query request.</param>
    /// <returns>A deterministic cache key string.</returns>
    string GenerateCacheKey(QueryRequest request);
}
