using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>The page stage: once, last. A cursor continues a page; an offset jumps, under the host cap.</summary>
public sealed record PageStage
{
    /// <summary>The page size; the host's default when absent.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>The opaque cursor of the page to continue from.</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }

    /// <summary>The number of rows to skip, under <c>maxOffset</c>.</summary>
    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    /// <summary>Whether to count the matching rows, up to the count cap.</summary>
    [JsonPropertyName("includeTotalCount")]
    public bool IncludeTotalCount { get; init; }
}
