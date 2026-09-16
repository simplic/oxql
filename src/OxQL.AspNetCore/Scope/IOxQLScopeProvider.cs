using Microsoft.AspNetCore.Http;

namespace OxQL.AspNetCore.Scope;

/// <summary>
/// Where the host tells the engine which organisation a request belongs to. The engine
/// refuses every request with 403 when the provider returns null, and the host refuses to
/// serve queries at all when no provider is registered: a service cannot run the engine
/// unscoped. The base package's provider returns the request context's organisation.
/// </summary>
public interface IOxQLScopeProvider
{
    /// <summary>The caller's organisation, or null when the request carries none.</summary>
    ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken);

    /// <summary>The caller's user id, for the log line and remote forwarding; null when unknown.</summary>
    string? UserId(HttpContext? httpContext) => null;

    /// <summary>The request's correlation id; null when unknown.</summary>
    string? CorrelationId(HttpContext? httpContext) => httpContext?.TraceIdentifier;
}

/// <summary>A provider from a delegate, for hosts that resolve the organisation from a claim or header.</summary>
public sealed class DelegateOxQLScopeProvider(Func<HttpContext?, Guid?> organisation) : IOxQLScopeProvider
{
    /// <inheritdoc/>
    public ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(organisation(httpContext));
}
