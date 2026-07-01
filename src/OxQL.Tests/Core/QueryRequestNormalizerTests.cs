using OxQL.Core.Models;
using OxQL.Core.Normalization;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.Core;

public class QueryRequestNormalizerTests
{
    private readonly OxQLOptions _options = new()
    {
        MaxPageSize = 500,
        DefaultPageSize = 50
    };

    private QueryRequestNormalizer CreateNormalizer() => new(_options);

    [Fact]
    public void Normalize_AddsTieBreakerSort_WhenIdNotPresent()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Sort = [new SortField { Path = "createdAt", Direction = "desc" }]
                }
            ]
        };

        var normalized = CreateNormalizer().Normalize(request);

        var sortStage = normalized.Pipeline.First(s => s.Sort is not null);
        sortStage.Sort.Should().HaveCount(2);
        sortStage.Sort![1].Path.Should().Be("id");
        sortStage.Sort[1].Direction.Should().Be("asc");
    }

    [Fact]
    public void Normalize_DoesNotAddTieBreaker_WhenIdAlreadyPresent()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Sort =
                    [
                        new SortField { Path = "createdAt", Direction = "desc" },
                        new SortField { Path = "id", Direction = "asc" }
                    ]
                }
            ]
        };

        var normalized = CreateNormalizer().Normalize(request);

        var sortStage = normalized.Pipeline.First(s => s.Sort is not null);
        sortStage.Sort.Should().HaveCount(2);
    }

    [Fact]
    public void Normalize_AddsDefaultSort_WhenNoSortPresent()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "eq" }
                    }
                }
            ]
        };

        var normalized = CreateNormalizer().Normalize(request);

        normalized.Pipeline.Should().Contain(s => s.Sort != null);
        var sortStage = normalized.Pipeline.First(s => s.Sort is not null);
        sortStage.Sort![0].Path.Should().Be("id");
        sortStage.Sort[0].Direction.Should().Be("asc");
    }

    [Fact]
    public void Normalize_AddsDefaultPage_WhenNoPagePresent()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Sort = [new SortField { Path = "createdAt", Direction = "desc" }]
                }
            ]
        };

        var normalized = CreateNormalizer().Normalize(request);

        normalized.Pipeline.Should().Contain(s => s.Page != null);
        var pageStage = normalized.Pipeline.First(s => s.Page is not null);
        pageStage.Page!.Limit.Should().Be(50);
    }

    [Fact]
    public void Normalize_ClampsPageSize_WhenExceedsMax()
    {
        var request = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Page = new PageStage { Limit = 1000 }
                }
            ]
        };

        var normalized = CreateNormalizer().Normalize(request);

        var pageStage = normalized.Pipeline.First(s => s.Page is not null);
        pageStage.Page!.Limit.Should().Be(500);
    }

    [Fact]
    public void GenerateCacheKey_SameQuery_ProducesSameKey()
    {
        var normalizer = CreateNormalizer();

        var request1 = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "gte" }
                    }
                },
                new PipelineStage
                {
                    Sort = [new SortField { Path = "createdAt", Direction = "desc" }]
                }
            ]
        };

        var request2 = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "gte" }
                    }
                },
                new PipelineStage
                {
                    Sort = [new SortField { Path = "createdAt", Direction = "desc" }]
                }
            ]
        };

        var key1 = normalizer.GenerateCacheKey(request1);
        var key2 = normalizer.GenerateCacheKey(request2);

        key1.Should().Be(key2);
    }

    [Fact]
    public void GenerateCacheKey_DifferentPipeline_ProducesDifferentKey()
    {
        var normalizer = CreateNormalizer();

        var request1 = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "gte" }
                    }
                }
            ]
        };

        var request2 = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "lte" }
                    }
                }
            ]
        };

        var key1 = normalizer.GenerateCacheKey(request1);
        var key2 = normalizer.GenerateCacheKey(request2);

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void GenerateCacheKey_DifferentVariableValues_ProducesSameKey()
    {
        var normalizer = CreateNormalizer();

        // Two queries with same shape but different variable values
        // (cache key is based on shape not values for literal values)
        var request1 = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "gte" }
                    }
                },
                new PipelineStage { Sort = [new SortField { Path = "id", Direction = "asc" }] },
                new PipelineStage { Page = new PageStage { Limit = 50 } }
            ]
        };

        var request2 = new QueryRequest
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition { Path = "amount", Op = "gte" }
                    }
                },
                new PipelineStage { Sort = [new SortField { Path = "id", Direction = "asc" }] },
                new PipelineStage { Page = new PageStage { Limit = 50 } }
            ]
        };

        var key1 = normalizer.GenerateCacheKey(request1);
        var key2 = normalizer.GenerateCacheKey(request2);

        key1.Should().Be(key2);
    }
}
