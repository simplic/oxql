namespace OxQL.Core.Models;

/// <summary>
/// Represents the response from a query execution.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed record QueryResponse<T>
{
    /// <summary>
    /// The result items for the current page.
    /// </summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>
    /// Pagination information including cursor for next page.
    /// </summary>
    public required PageInfo PageInfo { get; init; }
}

/// <summary>
/// Contains pagination metadata.
/// </summary>
public sealed record PageInfo
{
    /// <summary>
    /// Whether there are more results after the current page.
    /// </summary>
    public required bool HasNextPage { get; init; }

    /// <summary>
    /// Opaque cursor for fetching the next page. Null if no next page.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>
    /// Total count of matching documents. Null if not requested.
    /// </summary>
    public long? TotalCount { get; init; }
}
