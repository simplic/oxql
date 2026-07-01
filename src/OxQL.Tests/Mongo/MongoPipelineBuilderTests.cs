using OxQL.Core.Models;
using OxQL.Mongo.Builders;
using FluentAssertions;
using MongoDB.Bson;
using Xunit;

namespace OxQL.Tests.Mongo;

public class MongoPipelineBuilderTests
{
    private MongoPipelineBuilder CreateBuilder(QueryVariables? variables = null) => new(variables);

    [Fact]
    public void Build_LookupStage_TranslatesToLookup()
    {
        var plan = CreatePlan(new PipelineStage
        {
            Lookup = new LookupStage
            {
                From = "customers",
                LocalPath = "attributes.customerId",
                ForeignPath = "id",
                As = "customer"
            }
        });

        var pipeline = CreateBuilder().Build(plan);

        pipeline.Should().HaveCount(1);
        var lookup = pipeline[0];
        lookup.Contains("$lookup").Should().BeTrue();
        var lookupDoc = lookup["$lookup"].AsBsonDocument;
        lookupDoc["from"].AsString.Should().Be("customers");
        lookupDoc["localField"].AsString.Should().Be("attributes.customerId");
        lookupDoc["foreignField"].AsString.Should().Be("_id");
        lookupDoc["as"].AsString.Should().Be("customer");
    }

    [Fact]
    public void Build_UnwindStage_TranslatesToUnwind()
    {
        var plan = CreatePlan(new PipelineStage
        {
            Unwind = new UnwindStage
            {
                Path = "attributes.items",
                As = "item",
                PreserveNull = false,
                IncludeIndex = "itemIndex"
            }
        });

        var pipeline = CreateBuilder().Build(plan);

        // Unwind produces $unwind + $set for alias
        pipeline.Should().HaveCountGreaterThanOrEqualTo(1);
        var unwind = pipeline[0];
        unwind.Contains("$unwind").Should().BeTrue();
        var unwindDoc = unwind["$unwind"].AsBsonDocument;
        unwindDoc["path"].AsString.Should().Be("$attributes.items");
        unwindDoc["preserveNullAndEmptyArrays"].AsBoolean.Should().BeFalse();
        unwindDoc["includeArrayIndex"].AsString.Should().Be("itemIndex");

        // $set for alias
        pipeline[1].Contains("$set").Should().BeTrue();
        pipeline[1]["$set"].AsBsonDocument.Contains("item").Should().BeTrue();
    }

    [Fact]
    public void Build_GroupStage_TranslatesToGroup()
    {
        var plan = CreatePlan(new PipelineStage
        {
            Group = new GroupStage
            {
                By =
                [
                    new GroupByField { Path = "attributes.customerId", As = "customerId" }
                ],
                Fields = new Dictionary<string, AggregationExpression>
                {
                    ["totalAmount"] = new() { Function = "sum", Argument = new QueryExpression { Path = "attributes.amount" } },
                    ["count"] = new() { Function = "count", IsCount = true }
                }
            }
        });

        var pipeline = CreateBuilder().Build(plan);

        pipeline.Should().HaveCountGreaterThanOrEqualTo(1);
        var group = pipeline[0];
        group.Contains("$group").Should().BeTrue();
        var groupDoc = group["$group"].AsBsonDocument;
        groupDoc["_id"].AsString.Should().Be("$attributes.customerId");
        groupDoc["totalAmount"].AsBsonDocument.Contains("$sum").Should().BeTrue();
        groupDoc["count"].AsBsonDocument["$sum"].AsInt32.Should().Be(1);
    }

    [Fact]
    public void Build_GroupCountDistinct_TranslatesToAddToSet()
    {
        var plan = CreatePlan(new PipelineStage
        {
            Group = new GroupStage
            {
                By =
                [
                    new GroupByField { Path = "attributes.category", As = "category" }
                ],
                Fields = new Dictionary<string, AggregationExpression>
                {
                    ["invoiceCount"] = new() { Function = "countDistinct", Argument = new QueryExpression { Path = "id" } }
                }
            }
        });

        var pipeline = CreateBuilder().Build(plan);

        var group = pipeline[0];
        var groupDoc = group["$group"].AsBsonDocument;
        groupDoc["invoiceCount"].AsBsonDocument.Contains("$addToSet").Should().BeTrue();

        // Should have $addFields stage for $size
        pipeline.Should().Contain(s => s.Contains("$addFields"));
    }

    [Fact]
    public void Build_ProjectStage_TranslatesToProject()
    {
        var plan = CreatePlan(new PipelineStage
        {
            Project = new ProjectStage
            {
                Fields = new Dictionary<string, int> { ["id"] = 1, ["entityType"] = 1, ["attributes.amount"] = 1 }
            }
        });

        var pipeline = CreateBuilder().Build(plan);

        pipeline.Should().HaveCount(1);
        var project = pipeline[0];
        project.Contains("$project").Should().BeTrue();
        var projectDoc = project["$project"].AsBsonDocument;
        projectDoc["_id"].AsInt32.Should().Be(1);
        projectDoc["entityType"].AsInt32.Should().Be(1);
        projectDoc["attributes.amount"].AsInt32.Should().Be(1);
    }

    [Fact]
    public void Build_SortStage_TranslatesToSort()
    {
        var plan = CreatePlan(new PipelineStage
        {
            Sort =
            [
                new SortField { Path = "createdAt", Direction = "desc" },
                new SortField { Path = "id", Direction = "asc" }
            ]
        });

        var pipeline = CreateBuilder().Build(plan);

        pipeline.Should().HaveCount(1);
        var sort = pipeline[0];
        sort.Contains("$sort").Should().BeTrue();
        var sortDoc = sort["$sort"].AsBsonDocument;
        sortDoc["createdAt"].AsInt32.Should().Be(-1);
        sortDoc["_id"].AsInt32.Should().Be(1);
    }

    [Fact]
    public void Build_PageStage_TranslatesToLimit()
    {
        var plan = new QueryPlan
        {
            EntityType = "invoice",
            Pipeline = [new PipelineStage { Page = new PageStage { Limit = 25 } }],
            Sort = [new SortField { Path = "id", Direction = "asc" }],
            Page = new PageStage { Limit = 25 },
            CacheKey = "test"
        };

        var pipeline = CreateBuilder().Build(plan);

        pipeline.Should().Contain(s => s.Contains("$limit"));
        var limit = pipeline.First(s => s.Contains("$limit"));
        limit["$limit"].AsInt32.Should().Be(26); // limit + 1 for hasNextPage detection
    }

    [Fact]
    public void Build_FullPipeline_RendersExpectedStages()
    {
        var plan = new QueryPlan
        {
            EntityType = "invoice",
            Pipeline =
            [
                new PipelineStage
                {
                    Match = new MatchStage
                    {
                        Condition = new FilterCondition
                        {
                            Path = "entityType",
                            Op = "eq",
                            Value = System.Text.Json.JsonDocument.Parse("\"invoice\"").RootElement
                        }
                    }
                },
                new PipelineStage
                {
                    Sort = [new SortField { Path = "createdAt", Direction = "desc" }]
                },
                new PipelineStage
                {
                    Page = new PageStage { Limit = 10 }
                }
            ],
            Sort = [new SortField { Path = "createdAt", Direction = "desc" }],
            Page = new PageStage { Limit = 10 },
            CacheKey = "test"
        };

        var pipeline = CreateBuilder().Build(plan);

        pipeline.Should().HaveCount(3);
        pipeline[0].Contains("$match").Should().BeTrue();
        pipeline[1].Contains("$sort").Should().BeTrue();
        pipeline[2].Contains("$limit").Should().BeTrue();
    }

    private static QueryPlan CreatePlan(params PipelineStage[] stages) => new()
    {
        EntityType = "invoice",
        Pipeline = stages,
        Sort = [new SortField { Path = "id", Direction = "asc" }],
        Page = new PageStage { Limit = 50 },
        CacheKey = "test"
    };
}
