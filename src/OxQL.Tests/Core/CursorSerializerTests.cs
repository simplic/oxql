using OxQL.Core.Cursor;
using OxQL.Core.Models;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.Core;

public class CursorSerializerTests
{
    private readonly CursorSerializer _serializer = new();

    private static readonly IReadOnlyList<SortField> DefaultSort =
    [
        new SortField { Path = "createdAt", Direction = "desc" },
        new SortField { Path = "id", Direction = "asc" }
    ];

    [Fact]
    public void Serialize_ThenDeserialize_RoundTrips()
    {
        var payload = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = "2024-01-15T10:00:00Z" },
                new CursorField { Path = "id", Direction = "asc", Value = "abc123" }
            ]
        };

        var cursor = _serializer.Serialize(payload);
        var result = _serializer.Deserialize(cursor, DefaultSort);

        result.Fields.Should().HaveCount(2);
        result.Fields[0].Path.Should().Be("createdAt");
        result.Fields[0].Direction.Should().Be("desc");
        result.Fields[1].Path.Should().Be("id");
        result.Fields[1].Direction.Should().Be("asc");
    }

    [Fact]
    public void Serialize_ProducesBase64UrlString()
    {
        var payload = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "id", Direction = "asc", Value = "test" }
            ]
        };

        var cursor = _serializer.Serialize(payload);

        cursor.Should().NotContain("+");
        cursor.Should().NotContain("/");
        cursor.Should().NotContain("=");
    }

    [Fact]
    public void Deserialize_InvalidBase64_Throws()
    {
        var sort = new List<SortField> { new() { Path = "id", Direction = "asc" } };

        var act = () => _serializer.Deserialize("not!!!valid===base64", sort);

        act.Should().Throw<QueryValidationException>()
            .Which.Errors[0].Code.Should().Be("INVALID_CURSOR");
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        var sort = new List<SortField> { new() { Path = "id", Direction = "asc" } };
        // Encode invalid JSON
        var invalid = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not json"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var act = () => _serializer.Deserialize(invalid, sort);

        act.Should().Throw<QueryValidationException>()
            .Which.Errors[0].Code.Should().Be("INVALID_CURSOR");
    }

    [Fact]
    public void Deserialize_FieldCountMismatch_Throws()
    {
        var payload = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = "2024-01-15" },
                new CursorField { Path = "id", Direction = "asc", Value = "abc" }
            ]
        };

        var cursor = _serializer.Serialize(payload);
        var wrongSort = new List<SortField> { new() { Path = "id", Direction = "asc" } };

        var act = () => _serializer.Deserialize(cursor, wrongSort);

        act.Should().Throw<QueryValidationException>()
            .Which.Errors[0].Code.Should().Be("CURSOR_SORT_MISMATCH");
    }

    [Fact]
    public void Deserialize_PathMismatch_Throws()
    {
        var payload = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = "2024-01-15" },
                new CursorField { Path = "id", Direction = "asc", Value = "abc" }
            ]
        };

        var cursor = _serializer.Serialize(payload);
        var wrongSort = new List<SortField>
        {
            new() { Path = "updatedAt", Direction = "desc" },
            new() { Path = "id", Direction = "asc" }
        };

        var act = () => _serializer.Deserialize(cursor, wrongSort);

        act.Should().Throw<QueryValidationException>()
            .Which.Errors[0].Code.Should().Be("CURSOR_SORT_MISMATCH");
    }

    [Fact]
    public void Deserialize_DirectionMismatch_Throws()
    {
        var payload = new CursorPayload
        {
            Fields =
            [
                new CursorField { Path = "createdAt", Direction = "desc", Value = "2024-01-15" },
                new CursorField { Path = "id", Direction = "asc", Value = "abc" }
            ]
        };

        var cursor = _serializer.Serialize(payload);
        var wrongSort = new List<SortField>
        {
            new() { Path = "createdAt", Direction = "asc" },  // wrong direction
            new() { Path = "id", Direction = "asc" }
        };

        var act = () => _serializer.Deserialize(cursor, wrongSort);

        act.Should().Throw<QueryValidationException>()
            .Which.Errors[0].Code.Should().Be("CURSOR_SORT_MISMATCH");
    }

    [Fact]
    public void CreateFromDocument_CreatesCorrectPayload()
    {
        var sortFields = new List<SortField>
        {
            new() { Path = "createdAt", Direction = "desc" },
            new() { Path = "id", Direction = "asc" }
        };

        var payload = CursorSerializer.CreateFromDocument(sortFields, path => path switch
        {
            "createdAt" => "2024-01-15T10:00:00Z",
            "id" => "doc123",
            _ => null
        });

        payload.Fields.Should().HaveCount(2);
        payload.Fields[0].Path.Should().Be("createdAt");
        payload.Fields[0].Value.Should().Be("2024-01-15T10:00:00Z");
        payload.Fields[1].Path.Should().Be("id");
        payload.Fields[1].Value.Should().Be("doc123");
    }
}
