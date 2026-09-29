using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>The body of <c>POST /oxql/batch</c>: several requests, answered in order.</summary>
public sealed record BatchRequest
{
    /// <summary>The requests.</summary>
    [JsonPropertyName("queries")]
    public required IReadOnlyList<QueryRequest> Queries { get; init; }

    /// <summary>
    /// A ceiling on the whole batch, under the host's own per query: the queries run one after
    /// another, each under what is left of it, so the batch ends within it.
    /// </summary>
    [JsonPropertyName("maxTimeMs")]
    public int? MaxTimeMs { get; init; }

    /// <summary>Members of the batch the engine does not know (a batch-level <c>strict</c>, say); refused under contract 2.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; init; }
}

/// <summary>The body of a batch response: one full success body or refusal envelope per request, in order.</summary>
public sealed record BatchResponse
{
    /// <summary>The outcomes.</summary>
    [JsonPropertyName("results")]
    public required IReadOnlyList<JsonNode?> Results { get; init; }
}
