using Microsoft.AspNetCore.Http;
using OxQL.AspNetCore.Models;
using OxQL.Core.Registration;

namespace OxQL.AspNetCore.TypeEnrichment;

/// <summary>
/// Contextual information passed to <see cref="IOxQLTypeEnricher"/> implementations
/// so they can decide which extra <see cref="OxQLPropertyDescriptor"/>s to append to a
/// type descriptor.
/// </summary>
public sealed class OxQLTypeEnrichmentContext
{
    public OxQLTypeEnrichmentContext(HttpContext? httpContext, OxQLTypeRegistration registration)
    {
        HttpContext   = httpContext;
        Registration  = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    /// <summary>
    /// The current HTTP context, or <c>null</c> when invoked outside of a request.
    /// Enrichers should tolerate a <c>null</c> context.
    /// </summary>
    public HttpContext? HttpContext { get; }

    /// <summary>
    /// Convenience accessor for the authenticated user, or <c>null</c> when unauthenticated
    /// or invoked outside of an HTTP request.
    /// </summary>
    public System.Security.Claims.ClaimsPrincipal? User => HttpContext?.User;

    /// <summary>
    /// The registration of the entity type currently being described.
    /// </summary>
    public OxQLTypeRegistration Registration { get; }
}

/// <summary>
/// A host-implemented enricher that appends extra <see cref="OxQLPropertyDescriptor"/>s to
/// a type descriptor when the <c>/types</c> endpoint is called.
/// <para>
/// Only invoked for entity types whose <see cref="OxQLTypeRegistration.Extendable"/> flag is
/// <c>true</c>. All enrichers registered in DI are called and their results are merged.
/// </para>
/// <para>
/// Return an empty sequence to contribute nothing for the current request.
/// </para>
/// </summary>
public interface IOxQLTypeEnricher
{
    /// <summary>
    /// Returns additional properties to append to the type descriptor for the given
    /// <paramref name="context"/>.
    /// </summary>
    ValueTask<IReadOnlyList<OxQLPropertyDescriptor>> GetPropertiesAsync(
        OxQLTypeEnrichmentContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Delegate-based <see cref="IOxQLTypeEnricher"/> that adapts a simple lambda into an
/// enricher, enabling inline registration such as
/// <c>AddOxQLTypeEnricher((ctx, ct) =&gt; ValueTask.FromResult(...))</c>.
/// </summary>
public sealed class DelegateOxQLTypeEnricher : IOxQLTypeEnricher
{
    private readonly Func<OxQLTypeEnrichmentContext, CancellationToken, ValueTask<IReadOnlyList<OxQLPropertyDescriptor>>> _factory;

    public DelegateOxQLTypeEnricher(
        Func<OxQLTypeEnrichmentContext, CancellationToken, ValueTask<IReadOnlyList<OxQLPropertyDescriptor>>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public ValueTask<IReadOnlyList<OxQLPropertyDescriptor>> GetPropertiesAsync(
        OxQLTypeEnrichmentContext context,
        CancellationToken cancellationToken = default) =>
        _factory(context, cancellationToken);
}
