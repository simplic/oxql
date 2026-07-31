using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Describes a resolve request sent to an <see cref="IExternalResolver"/>.
/// </summary>
public sealed record ExternalResolveRequest
{
    /// <summary>
    /// The keys to resolve in key-based mode.
    /// </summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>
    /// The query request to execute in subquery mode.
    /// </summary>
    public QueryRequest? Query { get; init; }
}

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

    /// <summary>
    /// Resolves one object using either key-based or query-based request context.
    /// </summary>
    /// <remarks>
    /// Default behavior maps key-based requests to <see cref="ResolveAsync(IReadOnlyList{string}, CancellationToken)"/>.
    /// Override this in resolvers that support subquery forwarding.
    /// </remarks>
    async Task<object?> ResolveOneAsync(
        ExternalResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Query is not null)
        {
            throw new NotSupportedException(
                $"Resolver '{Source}' does not support query-based resolve requests.");
        }

        if (request.Keys is null || request.Keys.Count == 0)
            return null;

        var resolved = await ResolveAsync(request.Keys, cancellationToken);
        var firstKey = request.Keys[0];
        return resolved.TryGetValue(firstKey, out var value) ? value : null;
    }
}
