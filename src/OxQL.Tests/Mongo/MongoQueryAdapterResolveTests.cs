using FluentAssertions;
using OxQL.Core.Cursor;
using OxQL.Core.Models;
using OxQL.Tests.Fakes;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace OxQL.Tests.Mongo;

public class MongoQueryAdapterResolveTests
{
    [Fact]
    public async Task ExecuteAsync_ResolveStage_CachesResponsesPerQuery()
    {
        var resolver = new FakeExternalResolver("crm.customer")
            .WithData("cust-1", new Dictionary<string, object?>
            {
                ["id"] = "cust-1",
                ["name"] = "Contoso"
            });

        var adapter = new global::OxQL.Mongo.MongoQueryAdapter(
            _ => throw new NotSupportedException(),
            new CursorSerializer(),
            [resolver],
            (_, _, _) => Task.FromResult(new List<BsonDocument>
            {
                new() { ["_id"] = "1", ["attributes"] = new BsonDocument("customerId", "cust-1") },
                new() { ["_id"] = "2", ["attributes"] = new BsonDocument("customerId", "cust-1") }
            }));

        var response = await adapter.ExecuteAsync(
            CreatePlan(
                new PipelineStage
                {
                    Resolve = new ResolveStage
                    {
                        Source = "crm.customer",
                        LocalPath = "attributes.customerId",
                        As = "crmCustomer"
                    }
                }),
            null);

        response.Items.Should().HaveCount(2);
        response.Items[0]["crmCustomer"].AsBsonDocument["name"].AsString.Should().Be("Contoso");
        response.Items[1]["crmCustomer"].AsBsonDocument["name"].AsString.Should().Be("Contoso");
        resolver.CallCount.Should().Be(1);
        resolver.RequestedKeyBatches.Should().ContainSingle();
        resolver.RequestedKeyBatches[0].Should().Equal("cust-1");
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedResolveStage_ReusesCachedKeys()
    {
        var resolver = new FakeExternalResolver("crm.customer")
            .WithData("cust-1", new Dictionary<string, object?> { ["id"] = "cust-1" });

        var adapter = new global::OxQL.Mongo.MongoQueryAdapter(
            _ => throw new NotSupportedException(),
            new CursorSerializer(),
            [resolver],
            (_, _, _) => Task.FromResult(new List<BsonDocument>
            {
                new() { ["_id"] = "1", ["attributes"] = new BsonDocument("customerId", "cust-1") }
            }));

        var response = await adapter.ExecuteAsync(
            CreatePlan(
                new PipelineStage
                {
                    Resolve = new ResolveStage
                    {
                        Source = "crm.customer",
                        LocalPath = "attributes.customerId",
                        As = "crmCustomer"
                    }
                },
                new PipelineStage
                {
                    Resolve = new ResolveStage
                    {
                        Source = "crm.customer",
                        LocalPath = "attributes.customerId",
                        As = "crmCustomerAgain"
                    }
                }),
            null);

        response.Items[0].Contains("crmCustomer").Should().BeTrue();
        response.Items[0].Contains("crmCustomerAgain").Should().BeTrue();
        resolver.CallCount.Should().Be(1);
        resolver.RequestedKeyBatches.Should().ContainSingle();
        resolver.RequestedKeyBatches[0].Should().Equal("cust-1");
    }

    private static QueryPlan CreatePlan(params PipelineStage[] stages) => new()
    {
        EntityType = "invoice",
        Pipeline = [.. stages, new PipelineStage { Page = new PageStage { Limit = 50 } }],
        Sort = [new SortField { Path = "id", Direction = "asc" }],
        Page = new PageStage { Limit = 50 },
        CacheKey = "test"
    };
}
