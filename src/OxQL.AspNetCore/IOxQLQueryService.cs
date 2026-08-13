using OxQL.Core.Models;

namespace OxQL.AspNetCore;

/// <summary>
/// Non-generic query service interface used by the OxQL controller.
/// Abstracts away the document type so the controller remains backend-agnostic.
/// </summary>
public interface IOxQLQueryService
{
    /// <summary>
    /// Executes a query request and returns the result as an untyped response.
    /// </summary>
    /// <param name="request">The query request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A query response containing items as <see cref="object"/> and page info.</returns>
    Task<OxQLQueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the generated backend pipeline stages for <paramref name="request"/> without
    /// executing the query. Filter injection (e.g. tenant filters) is applied so the output
    /// reflects exactly what would be sent to the database.
    /// </summary>
    /// <param name="request">The query request to explain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pipeline stages serialised as a JSON-compatible object list.</returns>
    Task<IReadOnlyList<object>> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Untyped query result returned by <see cref="IOxQLQueryService"/>.
/// </summary>
public sealed record OxQLQueryResult
{
    /// <summary>
    /// The result items for the current page.
    /// </summary>
    public required IReadOnlyList<object> Items { get; init; }

    /// <summary>
    /// Pagination information.
    /// </summary>
    public required PageInfo PageInfo { get; init; }
}
