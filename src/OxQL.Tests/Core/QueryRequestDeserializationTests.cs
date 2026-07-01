using System.Text.Json;
using OxQL.Core.Models;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.Core;

public class QueryRequestDeserializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Deserialize_SimpleMatchStage_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "pipeline": [
            {
              "match": {
                "and": [
                  { "attributes.amount": { "gte": 1000 } },
                  { "attributes.status": { "eq": "approved" } }
                ]
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.EntityType.Should().Be("invoice");
        request.Pipeline.Should().HaveCount(1);
        request.Pipeline[0].Match.Should().NotBeNull();
        request.Pipeline[0].Match!.And.Should().HaveCount(2);
        request.Pipeline[0].Match.And![0].Path.Should().Be("attributes.amount");
        request.Pipeline[0].Match.And[0].Op.Should().Be("gte");
        request.Pipeline[0].Match.And[1].Path.Should().Be("attributes.status");
        request.Pipeline[0].Match.And[1].Op.Should().Be("eq");
    }

    [Fact]
    public void Deserialize_VariableReference_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "variables": {
            "minAmount": 1000,
            "statuses": ["approved", "paid"]
          },
          "pipeline": [
            {
              "match": {
                "and": [
                  { "attributes.amount": { "gte": { "$var": "minAmount" } } }
                ]
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.Variables.Should().NotBeNull();
        request.Variables!.HasVariable("minAmount").Should().BeTrue();
    }

    [Fact]
    public void Deserialize_LookupStage_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "pipeline": [
            {
              "lookup": {
                "from": "customers",
                "localPath": "attributes.customerId",
                "foreignPath": "id",
                "as": "customer"
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.Pipeline[0].Lookup.Should().NotBeNull();
        request.Pipeline[0].Lookup!.From.Should().Be("customers");
        request.Pipeline[0].Lookup.LocalPath.Should().Be("attributes.customerId");
        request.Pipeline[0].Lookup.ForeignPath.Should().Be("id");
        request.Pipeline[0].Lookup.As.Should().Be("customer");
    }

    [Fact]
    public void Deserialize_UnwindStage_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "pipeline": [
            {
              "unwind": {
                "path": "attributes.items",
                "as": "item",
                "preserveNull": false,
                "includeIndex": "itemIndex"
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.Pipeline[0].Unwind.Should().NotBeNull();
        request.Pipeline[0].Unwind!.Path.Should().Be("attributes.items");
        request.Pipeline[0].Unwind.As.Should().Be("item");
        request.Pipeline[0].Unwind.PreserveNull.Should().BeFalse();
        request.Pipeline[0].Unwind.IncludeIndex.Should().Be("itemIndex");
    }

    [Fact]
    public void Deserialize_GroupStage_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "pipeline": [
            {
              "group": {
                "by": [
                  { "path": "attributes.customerId", "as": "customerId" }
                ],
                "fields": {
                  "totalAmount": { "sum": { "path": "attributes.amount" } },
                  "count": { "count": true }
                }
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.Pipeline[0].Group.Should().NotBeNull();
        request.Pipeline[0].Group!.By.Should().HaveCount(1);
        request.Pipeline[0].Group.By[0].Path.Should().Be("attributes.customerId");
        request.Pipeline[0].Group.Fields.Should().ContainKey("totalAmount");
        request.Pipeline[0].Group.Fields.Should().ContainKey("count");
        request.Pipeline[0].Group.Fields["count"].IsCount.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_SortStage_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "pipeline": [
            {
              "sort": [
                { "createdAt": "desc" },
                { "id": "asc" }
              ]
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.Pipeline[0].Sort.Should().HaveCount(2);
        request.Pipeline[0].Sort![0].Path.Should().Be("createdAt");
        request.Pipeline[0].Sort[0].Direction.Should().Be("desc");
        request.Pipeline[0].Sort[1].Path.Should().Be("id");
    }

    [Fact]
    public void Deserialize_PageStage_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "pipeline": [
            {
              "page": {
                "limit": 25,
                "cursor": null,
                "includeTotalCount": true
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.Pipeline[0].Page.Should().NotBeNull();
        request.Pipeline[0].Page!.Limit.Should().Be(25);
        request.Pipeline[0].Page.Cursor.Should().BeNull();
        request.Pipeline[0].Page.IncludeTotalCount.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_ProjectStage_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "pipeline": [
            {
              "project": {
                "id": 1,
                "entityType": 1,
                "attributes.amount": 1
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.Pipeline[0].Project.Should().NotBeNull();
        request.Pipeline[0].Project!.Include.Should().Contain("attributes.amount");
    }

    [Fact]
    public void Deserialize_FullPipeline_Succeeds()
    {
        var json = """
        {
          "entityType": "invoice",
          "variables": {
            "minAmount": 1000,
            "statuses": ["approved", "paid"]
          },
          "pipeline": [
            {
              "match": {
                "and": [
                  { "attributes.amount": { "gte": { "$var": "minAmount" } } },
                  { "attributes.status": { "in": { "$var": "statuses" } } }
                ]
              }
            },
            {
              "lookup": {
                "from": "customers",
                "localPath": "attributes.customerId",
                "foreignPath": "id",
                "as": "customer"
              }
            },
            {
              "sort": [
                { "createdAt": "desc" }
              ]
            },
            {
              "page": {
                "limit": 50,
                "cursor": null,
                "includeTotalCount": false
              }
            }
          ]
        }
        """;

        var request = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

        request.Should().NotBeNull();
        request!.EntityType.Should().Be("invoice");
        request.Pipeline.Should().HaveCount(4);
        request.Pipeline[0].Match.Should().NotBeNull();
        request.Pipeline[1].Lookup.Should().NotBeNull();
        request.Pipeline[2].Sort.Should().NotBeNull();
        request.Pipeline[3].Page.Should().NotBeNull();
    }
}
