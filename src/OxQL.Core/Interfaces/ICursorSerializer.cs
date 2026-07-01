using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Serializes and deserializes opaque cursors for pagination.
/// </summary>
public interface ICursorSerializer
{
    /// <summary>
    /// Serializes a cursor payload into an opaque string.
    /// </summary>
    /// <param name="payload">The cursor data to serialize.</param>
    /// <returns>An opaque base64url-encoded cursor string.</returns>
    string Serialize(CursorPayload payload);

    /// <summary>
    /// Deserializes an opaque cursor string back into a cursor payload.
    /// </summary>
    /// <param name="cursor">The opaque cursor string.</param>
    /// <param name="expectedSort">The expected sort definition to validate against.</param>
    /// <returns>The deserialized cursor payload.</returns>
    /// <exception cref="Models.QueryValidationException">If cursor is invalid or tampered.</exception>
    CursorPayload Deserialize(string cursor, IReadOnlyList<SortField> expectedSort);
}
