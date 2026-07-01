using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a lookup/join stage for including related documents.
/// </summary>
public sealed record LookupStage
{
    /// <summary>
    /// The source collection to join from.
    /// </summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>
    /// The local field path to match on.
    /// </summary>
    [JsonPropertyName("localPath")]
    public required string LocalPath { get; init; }

    /// <summary>
    /// The foreign field path to match on.
    /// </summary>
    [JsonPropertyName("foreignPath")]
    public required string ForeignPath { get; init; }

    /// <summary>
    /// The alias for the joined result.
    /// </summary>
    [JsonPropertyName("as")]
    public required string As { get; init; }
}

/// <summary>
/// Represents a resolve stage for external source lookups.
/// </summary>
public sealed record ResolveStage
{
    /// <summary>
    /// The external source identifier (e.g., "crm.customer").
    /// </summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>
    /// The local field path containing the foreign key.
    /// </summary>
    [JsonPropertyName("localPath")]
    public required string LocalPath { get; init; }

    /// <summary>
    /// The alias for the resolved result.
    /// </summary>
    [JsonPropertyName("as")]
    public required string As { get; init; }
}
