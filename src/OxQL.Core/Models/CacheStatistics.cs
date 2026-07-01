namespace OxQL.Core.Models;

/// <summary>
/// Cache statistics for the query plan cache.
/// </summary>
public sealed record CacheStatistics
{
    /// <summary>
    /// Total number of cache hits.
    /// </summary>
    public long Hits { get; init; }

    /// <summary>
    /// Total number of cache misses.
    /// </summary>
    public long Misses { get; init; }

    /// <summary>
    /// Current number of cached entries.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// Cache hit ratio (0.0 to 1.0).
    /// </summary>
    public double HitRatio => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}
