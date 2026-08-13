using OxQL.Core.Caching;
using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using OxQL.Core.Normalization;
using OxQL.Core.Planning;
using OxQL.Core.Validation;
using OxQL.Mongo;
using FluentAssertions;
using MongoDB.Bson;
using Xunit;

namespace OxQL.Tests.Mongo;

/// <summary>
/// Covers the interplay of plan cache and paging: the cache key describes the query
/// shape and deliberately excludes the cursor, so every page of a result set shares one
/// cached plan. The cursor must therefore be applied per request instead of travelling
/// inside the cached plan.
/// </summary>
public class MongoQueryExecutorTests
{
    private readonly OxQLOptions _options = new();

    /// <summary>Captures the plans it is handed instead of querying MongoDB.</summary>
    private sealed class RecordingAdapter : IQueryAdapter<BsonDocument>
    {
        public List<QueryPlan> Plans { get; } = [];

        public Task<QueryResponse<BsonDocument>> ExecuteAsync(
            QueryPlan plan,
            QueryVariables? variables,
            CancellationToken cancellationToken = default)
        {
            Plans.Add(plan);

            return Task.FromResult(new QueryResponse<BsonDocument>
            {
                Items = [],
                PageInfo = new PageInfo { HasNextPage = false }
            });
        }

        public IReadOnlyList<object> Explain(QueryPlan plan, QueryVariables? variables) => [];
    }

    private (MongoQueryExecutor Executor, RecordingAdapter Adapter) CreateExecutor()
    {
        var normalizer = new QueryRequestNormalizer(_options);
        var adapter = new RecordingAdapter();

        var executor = new MongoQueryExecutor(
            new QueryValidator(_options),
            normalizer,
            new QueryPlanner(normalizer),
            new QueryPlanCache(_options),
            adapter);

        return (executor, adapter);
    }

    /// <summary>Two requests built this way share a cache key — only the cursor differs.</summary>
    private static QueryRequest CreateRequest(string? cursor) => new()
    {
        EntityType = "logistics.shipment",
        Pipeline =
        [
            new PipelineStage { Sort = [new SortField { Path = "LoadStart", Direction = "asc" }] },
            new PipelineStage { Page = new PageStage { Limit = 500, Cursor = cursor } }
        ]
    };

    [Fact]
    public async Task ExecuteAsync_PageAfterCachedPlan_UsesItsOwnCursor()
    {
        var (executor, adapter) = CreateExecutor();

        // The first page primes the cache with a plan that has no cursor.
        await executor.ExecuteAsync(CreateRequest(cursor: null));
        // The second page has the same shape and therefore hits that cached plan.
        await executor.ExecuteAsync(CreateRequest(cursor: "cursor-page-2"));

        adapter.Plans.Should().HaveCount(2);
        adapter.Plans[0].Page.Cursor.Should().BeNull();
        adapter.Plans[1].Page.Cursor.Should().Be("cursor-page-2");
    }

    [Fact]
    public async Task ExecuteAsync_FirstPageAfterCachedPlanWithCursor_DoesNotInheritIt()
    {
        var (executor, adapter) = CreateExecutor();

        // Reverse order: the cache is primed by a request that carries a cursor. A later
        // first page must not silently resume from it and skip rows.
        await executor.ExecuteAsync(CreateRequest(cursor: "cursor-page-2"));
        await executor.ExecuteAsync(CreateRequest(cursor: null));

        adapter.Plans.Should().HaveCount(2);
        adapter.Plans[1].Page.Cursor.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_PageAfterCachedPlan_KeepsTheCursorOutOfThePipelineStage()
    {
        var (executor, adapter) = CreateExecutor();

        await executor.ExecuteAsync(CreateRequest(cursor: null));
        await executor.ExecuteAsync(CreateRequest(cursor: "cursor-page-2"));

        var pageStage = adapter.Plans[1].Pipeline.Single(stage => stage.Page is not null).Page;
        pageStage!.Cursor.Should().Be("cursor-page-2");
    }

    [Fact]
    public async Task ExecuteAsync_PagesOfOneResultSet_ShareTheCachedPlanShape()
    {
        var (executor, adapter) = CreateExecutor();

        await executor.ExecuteAsync(CreateRequest(cursor: null));
        await executor.ExecuteAsync(CreateRequest(cursor: "cursor-page-2"));

        // Same cache key and sort: the cursor must not fragment the plan cache.
        adapter.Plans[1].CacheKey.Should().Be(adapter.Plans[0].CacheKey);
        adapter.Plans[1].Sort.Should().BeEquivalentTo(adapter.Plans[0].Sort);
    }
}
