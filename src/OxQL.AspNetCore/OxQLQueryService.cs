using Microsoft.AspNetCore.Http;
using OxQL.AspNetCore.Filtering;
using OxQL.Core.Filtering;
using OxQL.Core.Interfaces;
using OxQL.Core.Models;

namespace OxQL.AspNetCore;

/// <summary>
/// Generic adapter that wraps an <see cref="IQueryExecutor{T}"/> and exposes
/// it through the non-generic <see cref="IOxQLQueryService"/> interface.
/// <para>
/// Before execution it collects forced conditions from any registered
/// <see cref="IOxQLQueryFilterProvider"/> (for example a multi-tenant
/// <c>OrganizationId</c> constraint) and merges them into the request via
/// <see cref="QueryFilterInjector"/> so every query is filtered at the root level.
/// </para>
/// </summary>
/// <typeparam name="T">The document type used by the underlying executor.</typeparam>
public sealed class OxQLQueryService<T> : IOxQLQueryService
{
    private readonly IQueryExecutor<T> _executor;
    private readonly IReadOnlyList<IOxQLQueryFilterProvider> _filterProviders;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public OxQLQueryService(
        IQueryExecutor<T> executor,
        IEnumerable<IOxQLQueryFilterProvider>? filterProviders = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _filterProviders = filterProviders?.ToList() ?? [];
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<OxQLQueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        request = await ApplyInjectedFiltersAsync(request, cancellationToken);

        var response = await _executor.ExecuteAsync(request, cancellationToken);

        return new OxQLQueryResult
        {
            Items = response.Items.Cast<object>().ToList(),
            PageInfo = response.PageInfo
        };
    }

    /// <summary>
    /// Collects filters from every registered provider and merges them into a single
    /// root-level <c>AND</c> using <see cref="QueryFilterInjector"/>. Returns the original
    /// request unchanged when no provider contributes a filter.
    /// </summary>
    private async Task<QueryRequest> ApplyInjectedFiltersAsync(
        QueryRequest request,
        CancellationToken cancellationToken)
    {
        if (_filterProviders.Count == 0)
            return request;

        var context = new OxQLFilterInjectionContext(_httpContextAccessor?.HttpContext, request);

        List<InjectedFilter>? filters = null;
        foreach (var provider in _filterProviders)
        {
            var contributed = await provider.GetFiltersAsync(context, cancellationToken);
            if (contributed is null || contributed.Count == 0)
                continue;

            filters ??= [];
            filters.AddRange(contributed);
        }

        return filters is null ? request : QueryFilterInjector.Inject(request, filters);
    }
}
