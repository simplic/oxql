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
