using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// Sends the engine's remote batches to the owning fleet host, as the base package's client sends
/// them to the owner's internal batch route: the owner's own query service is called through its
/// internal overload (<see cref="IOxQLQueryService.BatchAsync(BatchRequest, bool, CancellationToken)"/>
/// with <c>internalCall: true</c>), which alone admits the keyed fetch's <c>keyedBy</c>, under a
/// request scope of the owner carrying the caller's organisation, user and correlation forwarded
/// from the scope provider the parent query was scoped with. The batch travels as wire JSON both
/// ways; the owner's internal explain is called the same way (<see cref="ExplainAsync"/>). A
/// service served by a mounted handler (<see cref="LabFleet.Mount"/>: a fake or misbehaving owner)
/// is posted to over HTTP as before. The budget cancels the call. Calls stay inside the host's own
/// fleet; a service the fleet knows but has no server for (<see cref="LabFleet.Configured"/>) is
/// unreachable.
/// <para>
/// What each owner's shallow health said when reachability was last measured is kept
/// (<see cref="IRemoteOwnerInfo"/>): the keyed fetch splits batches at the owner's cap and refuses
/// 2.1 vocabulary to an owner on an older engine.
/// </para>
/// </summary>
public sealed class InMemoryRemoteClient(LabFleet fleet, IHttpContextAccessor httpContextAccessor) : IRemoteQueryClient, IRemoteOwnerInfo
{
    /// <summary>The bound of a batch whose caller names no positive budget, as in the base package.</summary>
    private static readonly TimeSpan FallbackBudget = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, RemoteOwnerInfo> owners = new(StringComparer.Ordinal);

    public bool IsConfigured(string serviceKey) => LabFleet.Configured.Contains(serviceKey);

    public RemoteOwnerInfo? OwnerOf(string serviceKey) => owners.TryGetValue(serviceKey, out var info) ? info : null;

    public async Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget > TimeSpan.Zero ? budget : FallbackBudget);

        var identity = await IdentityAsync(cancellationToken);

        if (LabService.All.FirstOrDefault(service => service.Key == serviceKey) is { } served)
            return await InternalAsync(served, request, identity, timeout.Token);

        var owner = await fleet.OwnerClientAsync(serviceKey)
            ?? throw new HttpRequestException($"'{serviceKey}' is configured on this host but no owner is serving it.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "OxQL/batch")
        {
            Content = JsonContent.Create(request, options: OxQLJson.Wire),
        };

        foreach (var (name, value) in identity)
            message.Headers.TryAddWithoutValidation(name, value);

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

            if (response.IsSuccessStatusCode && RemoteOwnerInfo.FromShallowHealth(await ReadAsync(response, cancellationToken)) is { } info)
                owners[serviceKey] = info;

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The owner's internal explain (<c>POST internal/oxql/explain</c>, DESIGN §4.1): the owner's
    /// query service through its internal overload
    /// (<see cref="IOxQLQueryService.ExplainAsync(ExplainRequest, bool, CancellationToken)"/> with
    /// <c>internalCall: true</c>) in a request scope of the owner carrying the forwarded identity, the
    /// envelope and the answer travelling as wire JSON. An owner served by a mounted handler has no
    /// internal explain here and answers null, which the origin notes <c>REMOTE_UNCHECKED</c>
    /// (unsupported). A refusal is the HTTP error the route would answer; the budget cancels the call.
    /// </summary>
    public async Task<JsonObject?> ExplainAsync(string serviceKey, ExplainRequest request, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (LabService.All.FirstOrDefault(service => service.Key == serviceKey) is not { } served)
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget > TimeSpan.Zero ? budget : FallbackBudget);

        var identity = await IdentityAsync(cancellationToken);
        var sent = JsonSerializer.Deserialize<ExplainRequest>(JsonSerializer.SerializeToUtf8Bytes(request, OxQLJson.Wire), OxQLJson.Wire)!;

        return await OnOwnerAsync(served, identity, timeout.Token, async (services, token) =>
            await services.GetRequiredService<IOxQLQueryService>().ExplainAsync(sent, internalCall: true, token) switch
            {
                ExplainOutcome.Success success => JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire)!.AsObject(),
                ExplainOutcome.Refused refused => throw new HttpRequestException(
                    $"The owner of '{serviceKey}' refused the explain: {refused.Refusal.Title}", null, (HttpStatusCode)refused.Refusal.Status),
                _ => throw new InvalidOperationException("An unknown explain outcome."),
            });
    }

    /// <summary>
    /// The batch run by the owner's query service through its internal overload. A batch the owner
    /// refuses whole is the HTTP error its route would answer.
    /// </summary>
    private Task<BatchResponse> InternalAsync(LabService service, BatchRequest request, IReadOnlyList<(string Name, string Value)> identity, CancellationToken cancellationToken)
    {
        var sent = JsonSerializer.Deserialize<BatchRequest>(JsonSerializer.SerializeToUtf8Bytes(request, OxQLJson.Wire), OxQLJson.Wire)!;

        return OnOwnerAsync(service, identity, cancellationToken, async (services, token) =>
            await services.GetRequiredService<IOxQLQueryService>().BatchAsync(sent, internalCall: true, token) switch
            {
                BatchOutcome.Success success => JsonSerializer.Deserialize<BatchResponse>(JsonSerializer.SerializeToUtf8Bytes(success.Response, OxQLJson.Wire), OxQLJson.Wire)!,
                BatchOutcome.Refused refused => throw new HttpRequestException(
                    $"The owner of '{service.Key}' refused the batch: {refused.Refusal.Title}", null, (HttpStatusCode)refused.Refusal.Status),
                _ => throw new InvalidOperationException("An unknown batch outcome."),
            });
    }

    /// <summary>
    /// Runs <paramref name="call"/> in a request scope of the owner host that carries the forwarded
    /// identity. It runs on a flow of its own, so the owner's request context never replaces the
    /// caller's; the token abandons it as a timed-out HTTP call would.
    /// </summary>
    private async Task<T> OnOwnerAsync<T>(LabService service, IReadOnlyList<(string Name, string Value)> identity, CancellationToken cancellationToken, Func<IServiceProvider, CancellationToken, Task<T>> call)
    {
        var host = await fleet.HostAsync(service);
        Task<T> run;

        using (ExecutionContext.SuppressFlow())
            run = Task.Run(() => InScopeAsync(host, identity, call, cancellationToken), CancellationToken.None);

        return await run.WaitAsync(cancellationToken);
    }

    private static async Task<T> InScopeAsync<T>(FleetHost host, IReadOnlyList<(string Name, string Value)> identity, Func<IServiceProvider, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        foreach (var (name, value) in identity)
            context.Request.Headers[name] = value;

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = context;

        try
        {
            return await call(scope.ServiceProvider, cancellationToken);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    /// <summary>The contract header and the caller's organisation, user and correlation, read off the parent query's scope.</summary>
    private async Task<IReadOnlyList<(string Name, string Value)>> IdentityAsync(CancellationToken cancellationToken)
    {
        var headers = new List<(string, string)> { (LabIdentity.ContractHeader, EngineCapabilities.Contract.ToString()) };
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.RequestServices.GetService<IOxQLScopeProvider>() is not { } scope)
            return headers;

        if (await scope.OrganisationAsync(httpContext, cancellationToken) is { } organisation)
            headers.Add((LabIdentity.OrganisationHeader, organisation.ToString()));

        if (scope.UserId(httpContext) is { Length: > 0 } user)
            headers.Add((LabIdentity.UserHeader, user));

        if (scope.CorrelationId(httpContext) is { Length: > 0 } correlation)
            headers.Add((LabIdentity.CorrelationHeader, correlation));

        return headers;
    }
}
