using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using System.Collections.Concurrent;

namespace OxQL.Core.Caching;

/// <summary>
/// Thread-safe query plan cache with TTL and size-based eviction.
/// </summary>
public sealed class QueryPlanCache : IQueryPlanCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly OxQLOptions _options;
    private long _hits;
    private long _misses;

    public QueryPlanCache(OxQLOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool TryGet(string cacheKey, out QueryPlan? plan)
    {
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                Interlocked.Increment(ref _hits);
                entry.LastAccessed = DateTimeOffset.UtcNow;
                plan = entry.Plan;
                return true;
            }

            // Expired - remove
            _cache.TryRemove(cacheKey, out _);
        }

        Interlocked.Increment(ref _misses);
        plan = null;
        return false;
    }

    public void Set(string cacheKey, QueryPlan plan)
    {
        EvictIfNeeded();

        var entry = new CacheEntry
        {
            Plan = plan,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_options.QueryPlanCacheTtl),
            LastAccessed = DateTimeOffset.UtcNow
        };

        _cache.AddOrUpdate(cacheKey, entry, (_, _) => entry);
    }

    public CacheStatistics GetStatistics()
    {
        return new CacheStatistics
        {
            Hits = Interlocked.Read(ref _hits),
            Misses = Interlocked.Read(ref _misses),
            Count = _cache.Count
        };
    }

    public void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    private void EvictIfNeeded()
    {
        if (_cache.Count < _options.QueryPlanCacheMaxEntries) return;

        // Remove expired entries first
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _cache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }

        // If still over limit, remove least recently accessed
        if (_cache.Count >= _options.QueryPlanCacheMaxEntries)
        {
            var toRemove = _cache
                .OrderBy(kv => kv.Value.LastAccessed)
                .Take(_cache.Count - _options.QueryPlanCacheMaxEntries + 1)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private sealed class CacheEntry
    {
        public required QueryPlan Plan { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset LastAccessed { get; set; }
    }
}
