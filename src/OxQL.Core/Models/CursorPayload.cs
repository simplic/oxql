namespace OxQL.Core.Models;

/// <summary>
/// Represents a cursor containing sort field values for cursor-based pagination.
/// </summary>
public sealed record CursorPayload
{
    /// <summary>
    /// The sort field values from the last document in the previous page.
    /// </summary>
    public required IReadOnlyList<CursorField> Fields { get; init; }
}

/// <summary>
/// A single field value in a cursor.
/// </summary>
public sealed record CursorField
{
    /// <summary>
    /// The field path this cursor value corresponds to.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The direction of the sort for this field.
    /// </summary>
    public required string Direction { get; init; }

    /// <summary>
    /// The value at the cursor position.
    /// </summary>
    public object? Value { get; init; }
}
