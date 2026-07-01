using System.Text.Json;
using OxQL.Core.Models;
using OxQL.Mongo.Builders;
using FluentAssertions;
using MongoDB.Bson;
using Xunit;

namespace OxQL.Tests.Mongo;

public class MongoFilterBuilderTests
{
    private MongoFilterBuilder CreateBuilder(QueryVariables? variables = null) => new(variables);

    [Fact]
    public void Build_EqFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "attributes.status",
                Op = "eq",
                Value = JsonDocument.Parse("\"approved\"").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'attributes.status': { '$eq': 'approved' } }").ToString());
    }

    [Fact]
    public void Build_GteFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "attributes.amount",
                Op = "gte",
                Value = JsonDocument.Parse("1000").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'attributes.amount': { '$gte': 1000 } }").ToString());
    }

    [Fact]
    public void Build_InFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "attributes.status",
                Op = "in",
                Value = JsonDocument.Parse("[\"approved\", \"paid\"]").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'attributes.status': { '$in': ['approved', 'paid'] } }").ToString());
    }

    [Fact]
    public void Build_AndConditions_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            And =
            [
                new FilterCondition
                {
                    Path = "attributes.amount",
                    Op = "gte",
                    Value = JsonDocument.Parse("1000").RootElement
                },
                new FilterCondition
                {
                    Path = "attributes.status",
                    Op = "eq",
                    Value = JsonDocument.Parse("\"approved\"").RootElement
                }
            ]
        };

        var result = CreateBuilder().Build(match);

        result.Contains("$and").Should().BeTrue();
        var andArray = result["$and"].AsBsonArray;
        andArray.Should().HaveCount(2);
    }

    [Fact]
    public void Build_OrConditions_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Or =
            [
                new FilterCondition
                {
                    Path = "attributes.status",
                    Op = "eq",
                    Value = JsonDocument.Parse("\"approved\"").RootElement
                },
                new FilterCondition
                {
                    Path = "attributes.status",
                    Op = "eq",
                    Value = JsonDocument.Parse("\"paid\"").RootElement
                }
            ]
        };

        var result = CreateBuilder().Build(match);

        result.Contains("$or").Should().BeTrue();
        var orArray = result["$or"].AsBsonArray;
        orArray.Should().HaveCount(2);
    }

    [Fact]
    public void Build_NotCondition_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Not = new FilterCondition
            {
                Path = "attributes.deleted",
                Op = "eq",
                Value = JsonDocument.Parse("true").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.Contains("$nor").Should().BeTrue();
    }

    [Fact]
    public void Build_VariableReference_ResolvesCorrectly()
    {
        var variables = new QueryVariables
        {
            Values = new Dictionary<string, object?>
            {
                ["minAmount"] = JsonDocument.Parse("1000").RootElement
            }
        };

        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "attributes.amount",
                Op = "gte",
                Value = JsonDocument.Parse("{ \"$var\": \"minAmount\" }").RootElement
            }
        };

        var result = CreateBuilder(variables).Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'attributes.amount': { '$gte': 1000 } }").ToString());
    }

    [Fact]
    public void Build_ContainsFilter_TranslatesToRegex()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "attributes.name",
                Op = "contains",
                Value = JsonDocument.Parse("\"test\"").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.Contains("attributes.name").Should().BeTrue();
        result["attributes.name"].AsBsonDocument.Contains("$regex").Should().BeTrue();
    }

    [Fact]
    public void Build_StartsWithFilter_TranslatesToAnchoredRegex()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "attributes.name",
                Op = "startsWith",
                Value = JsonDocument.Parse("\"pre\"").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result["attributes.name"].AsBsonDocument["$regex"].AsBsonRegularExpression.Pattern
            .Should().StartWith("^");
    }

    [Fact]
    public void Build_ExistsFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "attributes.optional",
                Op = "exists",
                Value = JsonDocument.Parse("true").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'attributes.optional': { '$exists': true } }").ToString());
    }

    [Fact]
    public void Build_IdPath_TranslatesToUnderscoreId()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "id",
                Op = "eq",
                Value = JsonDocument.Parse("\"abc123\"").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.Contains("_id").Should().BeTrue();
    }

    [Fact]
    public void Build_NeqFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "status",
                Op = "neq",
                Value = JsonDocument.Parse("\"deleted\"").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'status': { '$ne': 'deleted' } }").ToString());
    }

    [Fact]
    public void Build_LtFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "count",
                Op = "lt",
                Value = JsonDocument.Parse("10").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'count': { '$lt': 10 } }").ToString());
    }

    [Fact]
    public void Build_LteFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "count",
                Op = "lte",
                Value = JsonDocument.Parse("10").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'count': { '$lte': 10 } }").ToString());
    }

    [Fact]
    public void Build_NinFilter_TranslatesCorrectly()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "status",
                Op = "nin",
                Value = JsonDocument.Parse("[\"deleted\", \"archived\"]").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'status': { '$nin': ['deleted', 'archived'] } }").ToString());
    }

    // ── Type-hint tests ────────────────────────────────────────────────────

    [Fact]
    public void Build_TypeHint_Uuid_ProducesBinarySubtype4()
    {
        var guid = Guid.Parse("7feec12f-870f-4087-a676-27e411d570a8");
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "OrganizationId",
                Op = "eq",
                Value = JsonDocument.Parse("""{ "$uuid": "7feec12f-870f-4087-a676-27e411d570a8" }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        // Direct $eq — no $or expansion because a typed hint bypasses auto-detection
        result.Contains("OrganizationId").Should().BeTrue();
        var inner = result["OrganizationId"].AsBsonDocument;
        inner.Contains("$eq").Should().BeTrue();
        var bsonVal = inner["$eq"].AsBsonBinaryData;
        bsonVal.SubType.Should().Be(BsonBinarySubType.UuidStandard);
        bsonVal.ToGuid().Should().Be(guid);
    }

    [Fact]
    public void Build_TypeHint_Uuid3_ProducesBinarySubtype3()
    {
        var guid = Guid.Parse("7feec12f-870f-4087-a676-27e411d570a8");
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "LegacyId",
                Op = "eq",
                Value = JsonDocument.Parse("""{ "$uuid3": "7feec12f-870f-4087-a676-27e411d570a8" }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        var inner = result["LegacyId"].AsBsonDocument;
        var bsonVal = inner["$eq"].AsBsonBinaryData;
        bsonVal.SubType.Should().Be(BsonBinarySubType.UuidLegacy);
        bsonVal.ToGuid(GuidRepresentation.CSharpLegacy).Should().Be(guid);
    }

    [Fact]
    public void Build_TypeHint_Date_ProducesBsonDateTime()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "CreatedAt",
                Op = "gte",
                Value = JsonDocument.Parse("""{ "$date": "2024-01-15T10:30:00Z" }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.Contains("CreatedAt").Should().BeTrue();
        var inner = result["CreatedAt"].AsBsonDocument;
        inner.Contains("$gte").Should().BeTrue();
        var dt = inner["$gte"].AsBsonDateTime.ToUniversalTime();
        dt.Year.Should().Be(2024);
        dt.Month.Should().Be(1);
        dt.Day.Should().Be(15);
        dt.Hour.Should().Be(10);
    }

    [Fact]
    public void Build_TypeHint_Oid_ProducesBsonObjectId()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "id",
                Op = "eq",
                Value = JsonDocument.Parse("""{ "$oid": "507f1f77bcf86cd799439011" }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        // "id" translates to "_id"
        result.Contains("_id").Should().BeTrue();
        var inner = result["_id"].AsBsonDocument;
        inner["$eq"].BsonType.Should().Be(BsonType.ObjectId);
        inner["$eq"].AsObjectId.ToString().Should().Be("507f1f77bcf86cd799439011");
    }

    [Fact]
    public void Build_TypeHint_Long_ProducesBsonInt64()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "counter",
                Op = "gt",
                Value = JsonDocument.Parse("""{ "$long": "9007199254740993" }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        result.Contains("counter").Should().BeTrue();
        var inner = result["counter"].AsBsonDocument;
        inner["$gt"].BsonType.Should().Be(BsonType.Int64);
        inner["$gt"].AsInt64.Should().Be(9007199254740993L);
    }

    [Fact]
    public void Build_TypeHint_Decimal_ProducesBsonDecimal128()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "price",
                Op = "eq",
                Value = JsonDocument.Parse("""{ "$decimal": "19.99" }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        var inner = result["price"].AsBsonDocument;
        inner["$eq"].BsonType.Should().Be(BsonType.Decimal128);
        inner["$eq"].AsDecimal.Should().Be(19.99m);
    }

    [Fact]
    public void Build_TypeHint_Null_ProducesBsonNull()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "deletedAt",
                Op = "eq",
                Value = JsonDocument.Parse("""{ "$null": true }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        var inner = result["deletedAt"].AsBsonDocument;
        inner["$eq"].BsonType.Should().Be(BsonType.Null);
    }

    [Fact]
    public void Build_TypeHint_Regex_ProducesBsonRegex()
    {
        var match = new MatchStage
        {
            Condition = new FilterCondition
            {
                Path = "code",
                Op = "eq",
                Value = JsonDocument.Parse("""{ "$regex": "^ABC" }""").RootElement
            }
        };

        var result = CreateBuilder().Build(match);

        var inner = result["code"].AsBsonDocument;
        inner["$eq"].BsonType.Should().Be(BsonType.RegularExpression);
        inner["$eq"].AsBsonRegularExpression.Pattern.Should().Be("^ABC");
    }
}
