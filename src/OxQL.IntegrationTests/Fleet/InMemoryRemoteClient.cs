using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// Sends the engine's remote batches to the owning fleet host over its in-memory test server:
/// the owner's real <c>POST /OxQL/batch</c> route, the wire JSON both ways, and the caller's
/// organisation, user and correlation forwarded from the scope provider the parent query was
/// scoped with, as the base package's client does. The budget cancels the call. Calls stay
/// inside the host's own fleet; a service the fleet knows but has no server for
/// (<see cref="LabFleet.Configured"/>) is unreachable.
/// </summary>
public sealed class InMemoryRemoteClient(LabFleet fleet, IHttpContextAccessor httpContextAccessor) : IRemoteQueryClient
{
    /// <summary>The bound of a batch whose caller names no positive budget, as in the base package.</summary>
    private static readonly TimeSpan FallbackBudget = TimeSpan.FromSeconds(10);

    public bool IsConfigured(string serviceKey) => LabFleet.Configured.Contains(serviceKey);

    public async Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var owner = await fleet.OwnerClientAsync(serviceKey)
            ?? throw new HttpRequestException($"'{serviceKey}' is configured on this host but no owner is serving it.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "OxQL/batch")
        {
            Content = JsonContent.Create(request, options: OxQLJson.Wire),
        };

        await ForwardAsync(message, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget > TimeSpan.Zero ? budget : FallbackBudget);

        using var response = await owner.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The owner of '{serviceKey}' answered {(int)response.StatusCode} to the batch.", null, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<BatchResponse>(OxQLJson.Wire, timeout.Token)
            ?? throw new HttpRequestException($"The owner of '{serviceKey}' answered the batch with an empty body.");
    }

    public async Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken)
    {
        if (await fleet.OwnerClientAsync(serviceKey) is not { } owner)
            return false;

        try
        {
            using var response = await owner.GetAsync("OxQL/health?shallow=true", cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task ForwardAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        message.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, EngineCapabilities.Contract.ToString());

        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.RequestServices.GetService<IOxQLScopeProvider>() is not { } scope)
            return;

        if (await scope.OrganisationAsync(httpContext, cancellationToken) is { } organisation)
            message.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, organisation.ToString());

        if (scope.UserId(httpContext) is { Length: > 0 } user)
            message.Headers.TryAddWithoutValidation(LabIdentity.UserHeader, user);

        if (scope.CorrelationId(httpContext) is { Length: > 0 } correlation)
            message.Headers.TryAddWithoutValidation(LabIdentity.CorrelationHeader, correlation);
    }
}
