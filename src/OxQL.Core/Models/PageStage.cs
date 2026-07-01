using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a paging stage with cursor-based pagination.
/// </summary>
public sealed record PageStage
{
    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 50;

    /// <summary>
    /// Opaque cursor for forward pagination. Null for first page.
    /// </summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }

    /// <summary>
    /// Whether to include the total count of matching documents.
    /// </summary>
    [JsonPropertyName("includeTotalCount")]
    public bool IncludeTotalCount { get; init; }
}
