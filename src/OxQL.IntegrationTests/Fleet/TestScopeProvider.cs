using Microsoft.AspNetCore.Http;
using OxQL.AspNetCore.Scope;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// The organisation, user and correlation of a request, read from its headers: what a real
/// service reads from the bearer token on a public call and from the forwarded headers on an
/// internal one. A request without a valid organisation header has none, and the engine
/// answers it with 403.
/// </summary>
public sealed class TestScopeProvider : IOxQLScopeProvider
{
    public ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Guid.TryParse(Header(httpContext, LabIdentity.OrganisationHeader), out var organisation) ? organisation : (Guid?)null);

    public string? UserId(HttpContext? httpContext) => Header(httpContext, LabIdentity.UserHeader);

    public string? CorrelationId(HttpContext? httpContext) =>
        Header(httpContext, LabIdentity.CorrelationHeader) ?? httpContext?.TraceIdentifier;

    private static string? Header(HttpContext? httpContext, string name) =>
        httpContext?.Request.Headers[name].FirstOrDefault() is { Length: > 0 } value ? value : null;
}
