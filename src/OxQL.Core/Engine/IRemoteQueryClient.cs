using System.Text.Json.Nodes;
using OxQL.Core.Models;

namespace OxQL.Core.Engine;

/// <summary>
/// Sends a batch to the owner of a remote entity: one call per service per page, over the
/// host's internal client with the caller's user, organisation and correlation forwarded and
/// the remaining time budget as the owner's ceiling. The service key is the target entity's
/// namespace (<c>vehicle</c> of <c>vehicle.vehicle</c>), which is the host's
/// <c>InternalHosts</c> key. The base package implements it; the engine's resolver consumes
/// it, the host's startup check and health read it.
/// </summary>
public interface IRemoteQueryClient
{
    /// <summary>Executes a batch on the service behind <paramref name="serviceKey"/> within <paramref name="budget"/>.</summary>
    Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the host knows where <paramref name="serviceKey"/> lives (an <c>InternalHosts</c>
    /// entry). A declared reference into a service without one is a startup finding of the
    /// declaring host, never a silent runtime null.
    /// </summary>
    bool IsConfigured(string serviceKey);

    /// <summary>Whether the configured service answers right now; a health diagnostic, never a refusal.</summary>
    Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken);

    /// <summary>
    /// Explains <paramref name="request"/> at the service behind <paramref name="serviceKey"/>, over
    /// its internal explain route (<c>POST internal/oxql/explain</c>, DESIGN §4.1: internal key,
    /// forwarded identity, the same body as <c>POST /oxql/explain</c>) within <paramref name="budget"/>,
    /// and answers the owner's explain answer as written (<c>{ valid, errors, stages, aliases, types, … }</c>). The
    /// origin forwards the owner queries a run would send (the remote check, which also answers the
    /// types of the owner's targets) and the catalog entries of the owner's entities with it; the
    /// request carries what is left of the origin's explain (<see cref="ExplainRequest.Budget"/>). A
    /// client that cannot explain at owners answers null, the default, and the origin notes the parts
    /// <c>REMOTE_UNCHECKED</c>; a failed call (unreachable, timed out, refused, 429 from the owner's
    /// limiter) throws, and the origin notes them the same way.
    /// </summary>
    Task<JsonObject?> ExplainAsync(string serviceKey, ExplainRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
        Task.FromResult<JsonObject?>(null);
}
