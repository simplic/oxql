using Microsoft.AspNetCore.Http;
using OxQL.Core.Filtering;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Filtering;

/// <summary>
/// Contextual information passed to <see cref="IOxQLQueryFilterProvider"/> implementations
/// so they can decide which <see cref="InjectedFilter"/>s to force onto the current query.
/// </summary>
public sealed class OxQLFilterInjectionContext
{
    public OxQLFilterInjectionContext(HttpContext? httpContext, QueryRequest request)
    {
        HttpContext = httpContext;
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    /// <summary>
    /// The current HTTP context, or <c>null</c> when the query is executed outside of a request
    /// (for example from a background job). Providers should tolerate a <c>null</c> context.
    /// </summary>
    public HttpContext? HttpContext { get; }

    /// <summary>
    /// Convenience accessor for the authenticated user, or <c>null</c> when unauthenticated
    /// or executed outside of an HTTP request.
    /// </summary>
    public System.Security.Claims.ClaimsPrincipal? User => HttpContext?.User;

    /// <summary>
    /// The query request about to be executed. Providers may inspect <see cref="QueryRequest.EntityType"/>
    /// to scope injection to particular entity types, but should not mutate the request directly.
    /// </summary>
    public QueryRequest Request { get; }
}

/// <summary>
/// A host-implemented provider that contributes forced filter conditions (for example an
/// <c>OrganizationId</c> constraint) to every OxQL query. All filters returned by all registered
/// providers are combined with the caller's query using a root-level logical <c>AND</c>.
/// <para>
/// Return an empty sequence to contribute nothing for the current request.
/// </para>
/// </summary>
public interface IOxQLQueryFilterProvider
{
    /// <summary>
    /// Produces the filters to inject for the supplied <paramref name="context"/>.
    /// </summary>
    ValueTask<IReadOnlyList<InjectedFilter>> GetFiltersAsync(
        OxQLFilterInjectionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Delegate-based <see cref="IOxQLQueryFilterProvider"/> that adapts a simple lambda into a provider,
/// enabling inline registration such as
/// <c>AddOxQLQueryFilter(ctx =&gt; [InjectedFilter.Create("OrganizationId", ctx.User?.FindFirst("org")?.Value)])</c>.
/// </summary>
public sealed class DelegateOxQLQueryFilterProvider : IOxQLQueryFilterProvider
{
    private readonly Func<OxQLFilterInjectionContext, CancellationToken, ValueTask<IReadOnlyList<InjectedFilter>>> _factory;

    public DelegateOxQLQueryFilterProvider(
        Func<OxQLFilterInjectionContext, CancellationToken, ValueTask<IReadOnlyList<InjectedFilter>>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public ValueTask<IReadOnlyList<InjectedFilter>> GetFiltersAsync(
        OxQLFilterInjectionContext context,
        CancellationToken cancellationToken = default) =>
        _factory(context, cancellationToken);
}
