using OxQL.Core.Interfaces;
using OxQL.Core.Models;

namespace OxQL.AspNetCore;

/// <summary>
/// Generic adapter that wraps an <see cref="IQueryExecutor{T}"/> and exposes
/// it through the non-generic <see cref="IOxQLQueryService"/> interface.
/// </summary>
/// <typeparam name="T">The document type used by the underlying executor.</typeparam>
public sealed class OxQLQueryService<T> : IOxQLQueryService
{
    private readonly IQueryExecutor<T> _executor;

    public OxQLQueryService(IQueryExecutor<T> executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<OxQLQueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _executor.ExecuteAsync(request, cancellationToken);

        return new OxQLQueryResult
        {
            Items = response.Items.Cast<object>().ToList(),
            PageInfo = response.PageInfo
        };
    }
}
