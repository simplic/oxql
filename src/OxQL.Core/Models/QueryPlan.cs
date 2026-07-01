namespace OxQL.Core.Models;

/// <summary>
/// A provider-neutral representation of a compiled query plan.
/// </summary>
public sealed record QueryPlan
{
    /// <summary>
    /// The entity type being queried.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// The normalized, ordered pipeline stages.
    /// </summary>
    public required IReadOnlyList<PipelineStage> Pipeline { get; init; }

    /// <summary>
    /// The normalized sort definition (with tie-breaker).
    /// </summary>
    public required IReadOnlyList<SortField> Sort { get; init; }

    /// <summary>
    /// The page configuration.
    /// </summary>
    public required PageStage Page { get; init; }

    /// <summary>
    /// Cache key for this query plan shape.
    /// </summary>
    public required string CacheKey { get; init; }
}
