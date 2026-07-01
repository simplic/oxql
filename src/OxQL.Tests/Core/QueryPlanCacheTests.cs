using OxQL.Core.Caching;
using OxQL.Core.Models;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.Core;

public class QueryPlanCacheTests
{
    private readonly OxQLOptions _options = new()
    {
        QueryPlanCacheTtl = TimeSpan.FromMinutes(30),
        QueryPlanCacheMaxEntries = 100
    };

    private QueryPlanCache CreateCache() => new(_options);

    private static QueryPlan CreatePlan(string key) => new()
    {
        EntityType = "invoice",
        Pipeline = [],
        Sort = [new SortField { Path = "id", Direction = "asc" }],
        Page = new PageStage { Limit = 50 },
        CacheKey = key
    };

    [Fact]
    public void TryGet_EmptyCache_ReturnsFalse()
    {
        var cache = CreateCache();

        var result = cache.TryGet("nonexistent", out var plan);

        result.Should().BeFalse();
        plan.Should().BeNull();
    }

    [Fact]
    public void Set_ThenGet_ReturnsCachedPlan()
    {
        var cache = CreateCache();
        var plan = CreatePlan("key1");

        cache.Set("key1", plan);
        var result = cache.TryGet("key1", out var cached);

        result.Should().BeTrue();
        cached.Should().Be(plan);
    }

    [Fact]
    public void GetStatistics_TracksHitsAndMisses()
    {
        var cache = CreateCache();
        var plan = CreatePlan("key1");

        cache.Set("key1", plan);
        cache.TryGet("key1", out _); // hit
        cache.TryGet("key2", out _); // miss
        cache.TryGet("key1", out _); // hit

        var stats = cache.GetStatistics();

        stats.Hits.Should().Be(2);
        stats.Misses.Should().Be(1);
        stats.Count.Should().Be(1);
        stats.HitRatio.Should().BeApproximately(2.0 / 3, 0.01);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = CreateCache();
        cache.Set("key1", CreatePlan("key1"));
        cache.Set("key2", CreatePlan("key2"));

        cache.Clear();

        cache.TryGet("key1", out _).Should().BeFalse();
        cache.TryGet("key2", out _).Should().BeFalse();
        cache.GetStatistics().Count.Should().Be(0);
    }

    [Fact]
    public void TryGet_ExpiredEntry_ReturnsFalse()
    {
        var options = new OxQLOptions
        {
            QueryPlanCacheTtl = TimeSpan.FromMilliseconds(1),
            QueryPlanCacheMaxEntries = 100
        };
        var cache = new QueryPlanCache(options);
        var plan = CreatePlan("key1");

        cache.Set("key1", plan);
        Thread.Sleep(10); // Wait for expiration

        var result = cache.TryGet("key1", out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void Set_ExceedsMaxEntries_EvictsOldEntries()
    {
        var options = new OxQLOptions
        {
            QueryPlanCacheTtl = TimeSpan.FromMinutes(30),
            QueryPlanCacheMaxEntries = 3
        };
        var cache = new QueryPlanCache(options);

        cache.Set("key1", CreatePlan("key1"));
        Thread.Sleep(5);
        cache.Set("key2", CreatePlan("key2"));
        Thread.Sleep(5);
        cache.Set("key3", CreatePlan("key3"));
        Thread.Sleep(5);

        // Access key2 and key3 to make key1 least recently accessed
        cache.TryGet("key2", out _);
        cache.TryGet("key3", out _);

        // Adding a 4th entry should evict
        cache.Set("key4", CreatePlan("key4"));

        cache.GetStatistics().Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void SameQueryShape_ReusesCachedPlan()
    {
        var cache = CreateCache();
        var plan = CreatePlan("shape1");

        cache.Set("shape1", plan);

        // Simulate second request with same shape
        cache.TryGet("shape1", out var cached).Should().BeTrue();
        cached.Should().BeSameAs(plan);
    }
}
