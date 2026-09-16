using Microsoft.AspNetCore.Http;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model.Addon;

namespace OxQL.AspNetCore;

/// <summary>
/// The host's face of the engine: builds the request context from the scope provider and the
/// contract header, runs the engine, and hands the outcome to the controller.
/// </summary>
public sealed class OxQLQueryService : IOxQLQueryService
{
    /// <summary>The contract header.</summary>
    public const string ContractHeader = "X-OxQL-Contract";

    private readonly IQueryEngine engine;
    private readonly IOxQLScopeProvider scope;
    private readonly OxQLOptions options;
    private readonly IAddonDefinitionSource addons;
    private readonly IHttpContextAccessor? httpContextAccessor;

    public OxQLQueryService(
        IQueryEngine engine,
        IOxQLScopeProvider scope,
        OxQLOptions options,
        IAddonDefinitionSource? addons = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.addons = addons ?? EmptyAddonDefinitionSource.Instance;
        this.httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        await engine.ExecuteAsync(request, await ContextAsync(null, cancellationToken), cancellationToken);

    /// <inheritdoc/>
    public async Task<ExplainOutcome> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        await engine.ExplainAsync(request, await ContextAsync(null, cancellationToken), cancellationToken);

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken = default) =>
        await engine.ExecuteAsync(request, await ContextAsync(maxTimeMs, cancellationToken), cancellationToken);

    /// <summary>The context of the current request.</summary>
    public async ValueTask<RequestContext> ContextAsync(int? maxTimeMs, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor?.HttpContext;

        return new RequestContext
        {
            Organisation = await scope.OrganisationAsync(httpContext, cancellationToken),
            Contract = ContractOf(httpContext),
            AddonSource = addons,
            Options = options,
            UserId = scope.UserId(httpContext),
            CorrelationId = scope.CorrelationId(httpContext),
            MaxTimeMs = maxTimeMs,
        };
    }

    /// <summary>The contract a request was written for: the header, or the compat default without one.</summary>
    public int ContractOf(HttpContext? httpContext)
    {
        if (httpContext is not null && httpContext.Request.Headers.TryGetValue(ContractHeader, out var values) && int.TryParse(values.FirstOrDefault(), out var contract))
            return contract == 1 ? 1 : 2;

        return options.Compat.Enabled ? 1 : 2;
    }
}
