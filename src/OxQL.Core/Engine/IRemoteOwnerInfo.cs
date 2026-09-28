using System.Text.Json.Nodes;

namespace OxQL.Core.Engine;

/// <summary>
/// What a host knows of a remote owner from the owner's shallow health
/// (<c>GET /oxql/health?shallow=true</c>): its engine version and contract, and the largest batch
/// it takes (<c>limits.maxBatchQueries</c>). Any member is null while unknown.
/// </summary>
public sealed record RemoteOwnerInfo(string? EngineVersion, int? Contract, int? MaxBatchQueries)
{
    /// <summary>The owner's facts read off its shallow-health body; null when the body is not one.</summary>
    public static RemoteOwnerInfo? FromShallowHealth(JsonNode? health)
    {
        if (health is not JsonObject body)
            return null;

        return new RemoteOwnerInfo(
            body["engine"]?["version"] is JsonValue version && version.TryGetValue<string>(out var text) ? text : null,
            body["engine"]?["contract"] is JsonValue contract && contract.TryGetValue<int>(out var number) ? number : null,
            body["limits"]?["maxBatchQueries"] is JsonValue cap && cap.TryGetValue<int>(out var size) && size > 0 ? size : null);
    }
}

/// <summary>
/// An optional capability of an <see cref="IRemoteQueryClient"/>: what it last read of each owner's
/// shallow health. The client reads that health where it measures reachability
/// (<see cref="IRemoteQueryClient.IsReachableAsync"/>, which the host's health probe drives) and
/// keeps it; the keyed fetch splits an owner's batch at the owner's own
/// <see cref="RemoteOwnerInfo.MaxBatchQueries"/> (DESIGN §3.5.2 step 4), and falls back to this
/// host's cap while nothing is known. A client without the capability is an owner of unknown facts.
/// </summary>
public interface IRemoteOwnerInfo
{
    /// <summary>What is known of the owner behind <paramref name="serviceKey"/>, or null.</summary>
    RemoteOwnerInfo? OwnerOf(string serviceKey);
}
