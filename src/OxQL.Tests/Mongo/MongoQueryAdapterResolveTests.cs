using FluentAssertions;
using OxQL.Core.Cursor;
using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using OxQL.Tests.Fakes;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace OxQL.Tests.Mongo;

public class MongoQueryAdapterResolveTests
{
    private sealed class SubqueryResolver : IExternalResolver
    {
        private readonly List<QueryRequest> _queries = [];

        public string Source { get; }
        public int QueryCallCount => _queries.Count;
        public IReadOnlyList<QueryRequest> Queries => _queries;

        public SubqueryResolver(string source)
        {
            Source = source;
        }

        public Task<IReadOnlyDictionary<string, object?>> ResolveAsync(
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }

        public Task<object?> ResolveOneAsync(
            ExternalResolveRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Query is null)
                return Task.FromResult<object?>(null);

            _queries.Add(request.Query);
            var id = request.Query.Variables?.GetValue("contactId")?.ToString();
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult<object?>(null);

            return Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = $"Contact {id}"
            });
        }
    }

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

    [Fact]
    public async Task ExecuteAsync_ResolveSubquery_UsesParametersAndCachesPerRequest()
    {
        var resolver = new SubqueryResolver("contact-api/v1");

        var adapter = new global::OxQL.Mongo.MongoQueryAdapter(
            _ => throw new NotSupportedException(),
            new CursorSerializer(),
            [resolver],
            (_, _, _) => Task.FromResult(new List<BsonDocument>
            {
                new() { ["_id"] = "inv-1", ["attributes"] = new BsonDocument("contactId", "contact-42") },
                new() { ["_id"] = "inv-2", ["attributes"] = new BsonDocument("contactId", "contact-42") }
            }));

        var response = await adapter.ExecuteAsync(
            CreatePlan(
                new PipelineStage
                {
                    Resolve = new ResolveStage
                    {
                        Source = "contact-api/v1",
                        LocalPath = "attributes.contactId",
                        Parameters = new Dictionary<string, string>
                        {
                            ["contactId"] = "attributes.contactId"
                        },
                        Subquery = new QueryRequest
                        {
                            EntityType = "contact",
                            Pipeline =
                            [
                                new PipelineStage
                                {
                                    Match = new MatchStage
                                    {
                                        Condition = new FilterCondition
                                        {
                                            Path = "id",
                                            Op = "eq",
                                            Value = System.Text.Json.JsonDocument.Parse("{\"$var\":\"contactId\"}").RootElement
                                        }
                                    }
                                },
                                new PipelineStage { Page = new PageStage { Limit = 1 } }
                            ]
                        },
                        As = "contact"
                    }
                }),
            null);

        response.Items.Should().HaveCount(2);
        response.Items[0]["contact"].AsBsonDocument["id"].AsString.Should().Be("contact-42");
        response.Items[1]["contact"].AsBsonDocument["id"].AsString.Should().Be("contact-42");

        resolver.QueryCallCount.Should().Be(1);
        resolver.Queries.Should().ContainSingle();
        resolver.Queries[0].Variables.Should().NotBeNull();
        resolver.Queries[0].Variables!.GetValue("contactId")!.ToString().Should().Be("contact-42");
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
