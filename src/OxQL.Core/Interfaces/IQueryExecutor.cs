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
}
