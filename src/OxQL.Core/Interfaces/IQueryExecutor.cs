using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Executes a query request and returns typed results.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public interface IQueryExecutor<T>
{
    /// <summary>
    /// Executes a query request and returns paginated results.
    /// </summary>
    /// <param name="request">The query request to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query response with items and page info.</returns>
    Task<QueryResponse<T>> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the generated pipeline stages for <paramref name="request"/> without executing it.
    /// Each element is the backend-specific pipeline stage object (e.g. a <c>BsonDocument</c>).
    /// </summary>
    /// <param name="request">The query request to explain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ordered list of pipeline stage objects.</returns>
    Task<IReadOnlyList<object>> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default);
}
