using OxQL.Core.Models;
using OxQL.Mongo.Builders;
using FluentAssertions;
using MongoDB.Bson;
using System.Text.Json;
using Xunit;

namespace OxQL.Tests.Mongo;

public class MongoCursorPagingBuilderTests
{
    private readonly MongoCursorPagingBuilder _builder = new();

    [Fact]
    public void BuildCursorFilter_SingleAscField_UsesGt()
    {
        var cursor = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "id", Direction = "asc", Value = "abc123" }
            ]
        };

        var result = _builder.BuildCursorFilter(cursor);

        result.ToString().Should().Be(BsonDocument.Parse("{ '_id': { '$gt': 'abc123' } }").ToString());
    }

    [Fact]
    public void BuildCursorFilter_SingleDescField_UsesLt()
    {
        var cursor = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = "2024-01-15" }
            ]
        };

        var result = _builder.BuildCursorFilter(cursor);

        result.ToString().Should().Be(BsonDocument.Parse("{ 'createdAt': { '$lt': '2024-01-15' } }").ToString());
    }

    [Fact]
    public void BuildCursorFilter_MultipleFields_BuildsCorrectOrPredicate()
    {
        var cursor = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = "2024-01-15" },
                new CursorField { Path = "id", Direction = "asc", Value = "abc123" }
            ]
        };

        var result = _builder.BuildCursorFilter(cursor);

        result.Contains("$or").Should().BeTrue();
        var orArray = result["$or"].AsBsonArray;
        orArray.Should().HaveCount(2);

        // First condition: createdAt < lastCreatedAt
        var first = orArray[0].AsBsonDocument;
        first["createdAt"].AsBsonDocument["$lt"].AsString.Should().Be("2024-01-15");

        // Second condition: createdAt == lastCreatedAt AND id > lastId
        var second = orArray[1].AsBsonDocument;
        second["createdAt"].AsString.Should().Be("2024-01-15");
        second["_id"].AsBsonDocument["$gt"].AsString.Should().Be("abc123");
    }

    [Fact]
    public void BuildCursorFilter_ThreeFields_BuildsCorrectOrPredicate()
    {
        var cursor = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "priority", Direction = "desc", Value = 5 },
                new CursorField { Path = "createdAt", Direction = "desc", Value = "2024-01-15" },
                new CursorField { Path = "id", Direction = "asc", Value = "abc" }
            ]
        };

        var result = _builder.BuildCursorFilter(cursor);

        result.Contains("$or").Should().BeTrue();
        var orArray = result["$or"].AsBsonArray;
        orArray.Should().HaveCount(3);

        // First: priority < 5
        var first = orArray[0].AsBsonDocument;
        first["priority"].AsBsonDocument["$lt"].AsInt32.Should().Be(5);

        // Second: priority == 5 AND createdAt < "2024-01-15"
        var second = orArray[1].AsBsonDocument;
        second["priority"].AsInt32.Should().Be(5);
        second["createdAt"].AsBsonDocument["$lt"].AsString.Should().Be("2024-01-15");

        // Third: priority == 5 AND createdAt == "2024-01-15" AND id > "abc"
        var third = orArray[2].AsBsonDocument;
        third["priority"].AsInt32.Should().Be(5);
        third["createdAt"].AsString.Should().Be("2024-01-15");
        third["_id"].AsBsonDocument["$gt"].AsString.Should().Be("abc");
    }

    [Fact]
    public void BuildCursorFilter_EmptyFields_ReturnsEmptyDocument()
    {
        var cursor = new CursorPayload { Fields = [] };

        var result = _builder.BuildCursorFilter(cursor);

        result.ToString().Should().Be(new BsonDocument().ToString());
    }

    [Fact]
    public void BuildCursorFilter_IdPathTranslation_Works()
    {
        var cursor = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "id", Direction = "asc", Value = "xyz" }
            ]
        };

        var result = _builder.BuildCursorFilter(cursor);

        result.Contains("_id").Should().BeTrue();
        result.Contains("id").Should().BeFalse();
    }

    [Fact]
    public void BuildCursorFilter_DateTimeJsonElement_ProducesBsonDateTime()
    {
        // Simulate what happens after cursor round-trip through JSON:
        // DateTime values are deserialized as JsonElement with ValueKind.String.
        var isoDate = "2024-06-01T12:00:00Z";
        var jsonElement = JsonDocument.Parse($"\"{isoDate}\"").RootElement;

        var cursor = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = jsonElement }
            ]
        };

        var result = _builder.BuildCursorFilter(cursor);

        var inner = result["createdAt"].AsBsonDocument;
        inner.Contains("$lt").Should().BeTrue();
        // Must be BsonDateTime, not BsonString — otherwise the MongoDB comparison fails and
        // the cursor always points to the beginning of the collection.
        inner["$lt"].BsonType.Should().Be(BsonType.DateTime);
        inner["$lt"].AsBsonDateTime.ToUniversalTime().Should().Be(DateTime.Parse(isoDate).ToUniversalTime());
    }

    [Fact]
    public void BuildCursorFilter_MultiField_DateTimeJsonElement_ProducesBsonDateTime()
    {
        var isoDate = "2024-06-01T12:00:00Z";
        var jsonDate = JsonDocument.Parse($"\"{isoDate}\"").RootElement;
        var jsonId = JsonDocument.Parse("\"abc123\"").RootElement;

        var cursor = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = jsonDate },
                new CursorField { Path = "id", Direction = "asc", Value = jsonId }
            ]
        };

        var result = _builder.BuildCursorFilter(cursor);

        var orArray = result["$or"].AsBsonArray;

        // First condition: createdAt < lastCreatedAt (must be BsonDateTime)
        var first = orArray[0].AsBsonDocument;
        first["createdAt"].AsBsonDocument["$lt"].BsonType.Should().Be(BsonType.DateTime);

        // Second condition equality part: createdAt == lastCreatedAt (must be BsonDateTime)
        var second = orArray[1].AsBsonDocument;
        second["createdAt"].BsonType.Should().Be(BsonType.DateTime);
    }
}
