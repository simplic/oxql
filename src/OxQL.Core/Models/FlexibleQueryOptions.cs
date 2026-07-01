namespace OxQL.Core.Models;

/// <summary>
/// Configuration options for the OxQL engine.
/// </summary>
public sealed class OxQLOptions
{
    /// <summary>
    /// Maximum page size allowed. Default: 500.
    /// </summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>
    /// Default page size when not specified. Default: 50.
    /// </summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// Maximum number of pipeline stages allowed. Default: 20.
    /// </summary>
    public int MaxPipelineStages { get; set; } = 20;

    /// <summary>
    /// Maximum number of lookup stages allowed. Default: 5.
    /// </summary>
    public int MaxLookupStages { get; set; } = 5;

    /// <summary>
    /// Maximum number of unwind stages allowed. Default: 5.
    /// </summary>
    public int MaxUnwindStages { get; set; } = 5;

    /// <summary>
    /// Maximum number of group fields allowed. Default: 20.
    /// </summary>
    public int MaxGroupFields { get; set; } = 20;

    /// <summary>
    /// Maximum number of projection fields allowed. Default: 50.
    /// </summary>
    public int MaxProjectionFields { get; set; } = 50;

    /// <summary>
    /// Allowed collections/sources for lookup stages.
    /// </summary>
    public HashSet<string> AllowedLookupSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allowed sources for resolve stages.
    /// </summary>
    public HashSet<string> AllowedResolveSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allowed path prefixes for field access. Empty means all paths allowed.
    /// </summary>
    public HashSet<string> AllowedPathPrefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum regex pattern length. Default: 200.
    /// </summary>
    public int RegexMaxLength { get; set; } = 200;

    /// <summary>
    /// Query plan cache TTL. Default: 30 minutes.
    /// </summary>
    public TimeSpan QueryPlanCacheTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum number of entries in the query plan cache. Default: 1000.
    /// </summary>
    public int QueryPlanCacheMaxEntries { get; set; } = 1000;
}
