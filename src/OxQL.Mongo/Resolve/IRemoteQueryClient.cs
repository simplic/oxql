using OxQL.Core.Models;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// Sends a batch to the owner of a remote entity: one call per service per page, over the
/// host's internal client with the caller's user, organisation and correlation forwarded and
/// the remaining time budget as the owner's ceiling. The base package implements it; the
/// engine's resolver consumes it.
/// </summary>
public interface IRemoteQueryClient
{
    /// <summary>Executes a batch on the service behind <paramref name="serviceKey"/> within <paramref name="budget"/>.</summary>
    Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken);
}
