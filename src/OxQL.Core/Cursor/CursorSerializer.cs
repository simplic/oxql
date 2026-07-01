using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using System.Text;
using System.Text.Json;

namespace OxQL.Core.Cursor;

/// <summary>
/// Serializes and deserializes cursors as base64url-encoded JSON.
/// </summary>
public sealed class CursorSerializer : ICursorSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Serialize(CursorPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Base64UrlEncode(bytes);
    }

    public CursorPayload Deserialize(string cursor, IReadOnlyList<SortField> expectedSort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentNullException.ThrowIfNull(expectedSort);

        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            throw new QueryValidationException(
                [new QueryValidationError { Code = "INVALID_CURSOR", Message = "Cursor is not valid base64url." }]);
        }

        CursorPayload? payload;
        try
        {
            var json = Encoding.UTF8.GetString(bytes);
            payload = JsonSerializer.Deserialize<CursorPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            throw new QueryValidationException(
                [new QueryValidationError { Code = "INVALID_CURSOR", Message = "Cursor contains invalid JSON." }]);
        }

        if (payload is null || payload.Fields is null || payload.Fields.Count == 0)
        {
            throw new QueryValidationException(
                [new QueryValidationError { Code = "INVALID_CURSOR", Message = "Cursor payload is empty." }]);
        }

        // Validate cursor matches expected sort
        if (payload.Fields.Count != expectedSort.Count)
        {
            throw new QueryValidationException(
                [new QueryValidationError
                {
                    Code = "CURSOR_SORT_MISMATCH",
                    Message = $"Cursor has {payload.Fields.Count} fields but sort has {expectedSort.Count} fields."
                }]);
        }

        for (var i = 0; i < expectedSort.Count; i++)
        {
            if (!string.Equals(payload.Fields[i].Path, expectedSort[i].Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new QueryValidationException(
                    [new QueryValidationError
                    {
                        Code = "CURSOR_SORT_MISMATCH",
                        Message = $"Cursor field '{payload.Fields[i].Path}' does not match expected sort field '{expectedSort[i].Path}'."
                    }]);
            }

            if (!string.Equals(payload.Fields[i].Direction, expectedSort[i].Direction, StringComparison.OrdinalIgnoreCase))
            {
                throw new QueryValidationException(
                    [new QueryValidationError
                    {
                        Code = "CURSOR_SORT_MISMATCH",
                        Message = $"Cursor direction for '{payload.Fields[i].Path}' does not match sort direction."
                    }]);
            }
        }

        return payload;
    }

    /// <summary>
    /// Creates a cursor payload from a document's sort field values.
    /// </summary>
    public static CursorPayload CreateFromDocument(
        IReadOnlyList<SortField> sortFields,
        Func<string, object?> getFieldValue)
    {
        var fields = sortFields.Select(sf => new CursorField
        {
            Path = sf.Path,
            Direction = sf.Direction,
            Value = getFieldValue(sf.Path)
        }).ToList();

        return new CursorPayload { Fields = fields };
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }
}
