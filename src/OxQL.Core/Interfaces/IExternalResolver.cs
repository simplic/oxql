namespace OxQL.Core.Interfaces;

/// <summary>
/// Resolves data from external sources (e.g., CRM, external APIs).
/// </summary>
public interface IExternalResolver
{
    /// <summary>
    /// The source identifier this resolver handles (e.g., "crm.customer").
    /// </summary>
    string Source { get; }

    /// <summary>
    /// Resolves external data for the given set of keys.
    /// </summary>
    /// <param name="keys">The foreign keys to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary mapping keys to resolved objects.</returns>
    Task<IReadOnlyDictionary<string, object?>> ResolveAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);
}
