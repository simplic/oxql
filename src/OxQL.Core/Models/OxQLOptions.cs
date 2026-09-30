namespace OxQL.Core.Models;

/// <summary>
/// The engine's configuration, bound from the host's <c>OxQL</c> section. Every limit has a
/// default and is published on <c>GET /oxql/health</c>; the schema's <c>limits</c> carries the
/// ones a caller checks a request against before sending it. Every execution value has a
/// ceiling the engine clamps to. The flat limit members mirror <see cref="Limits"/> for the
/// callers that read them before the section was nested.
/// </summary>
public sealed class OxQLOptions
{
    /// <summary>Contract 1 compatibility: a request without the contract header is contract 1 while on.</summary>
    public CompatOptions Compat { get; set; } = new();

    /// <summary>The explain endpoint.</summary>
    public ExplainOptions Explain { get; set; } = new();

    /// <summary>Every cap a request is checked against.</summary>
    public LimitOptions Limits { get; set; } = new();

    /// <summary>Time and memory bounds on the aggregate.</summary>
    public ExecutionOptions Execution { get; set; } = new();

    /// <summary>How operands are encoded for members whose storage is not uniform.</summary>
    public RepresentationOptions Representation { get; set; } = new();

    /// <summary>In-process caches.</summary>
    public CacheOptions Cache { get; set; } = new();

    /// <summary>Cursor signing.</summary>
    public CursorOptions Cursor { get; set; } = new();

    /// <inheritdoc cref="LimitOptions.MaxPageSize"/>
    public int MaxPageSize { get => Limits.MaxPageSize; set => Limits.MaxPageSize = value; }

    /// <inheritdoc cref="LimitOptions.DefaultPageSize"/>
    public int DefaultPageSize { get => Limits.DefaultPageSize; set => Limits.DefaultPageSize = value; }

    /// <inheritdoc cref="LimitOptions.MaxPipelineStages"/>
    public int MaxPipelineStages { get => Limits.MaxPipelineStages; set => Limits.MaxPipelineStages = value; }

    /// <inheritdoc cref="LimitOptions.MaxLookupStages"/>
    public int MaxLookupStages { get => Limits.MaxLookupStages; set => Limits.MaxLookupStages = value; }

    /// <inheritdoc cref="LimitOptions.MaxUnwindStages"/>
    public int MaxUnwindStages { get => Limits.MaxUnwindStages; set => Limits.MaxUnwindStages = value; }

    /// <inheritdoc cref="LimitOptions.MaxGroupFields"/>
    public int MaxGroupFields { get => Limits.MaxGroupFields; set => Limits.MaxGroupFields = value; }

    /// <inheritdoc cref="LimitOptions.MaxProjectionFields"/>
    public int MaxProjectionFields { get => Limits.MaxProjectionFields; set => Limits.MaxProjectionFields = value; }

    /// <inheritdoc cref="LimitOptions.RegexMaxLength"/>
    public int RegexMaxLength { get => Limits.RegexMaxLength; set => Limits.RegexMaxLength = value; }

    /// <summary>
    /// Brings the limits into the range the engine can work with: every limit is at least 1
    /// (<c>MaxOffset</c> at least 0, which turns offset paging off), and the limits whose
    /// relationship the documentation states are clamped to it. Returns one sentence per
    /// adjustment, which the engine logs when it is built: silence about a configuration that
    /// breaks a feature is worse than a line at startup.
    /// <para>
    /// At the shipped defaults nothing is adjusted. A limit below 1 refuses every request it
    /// applies to, or reaches the database as a <c>$limit</c> it rejects. <c>MaxSemiJoinIds</c>
    /// above <c>MaxOffset</c> puts the tail of every large id set out of reach: the resolver
    /// walks the owner's pages by offset, the owner refuses above its own <c>MaxOffset</c>, and
    /// the caller sees the whole query refused with <c>MAX_OFFSET_EXCEEDED</c> rather than the
    /// <c>SEMI_JOIN_TOO_LARGE</c> the contract promises.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Normalise()
    {
        var adjustments = new List<string>();

        Limits.MaxPageSize = AtLeast(1, Limits.MaxPageSize, nameof(LimitOptions.MaxPageSize), adjustments);
        Limits.DefaultPageSize = AtLeast(1, Limits.DefaultPageSize, nameof(LimitOptions.DefaultPageSize), adjustments);
        Limits.MaxPipelineStages = AtLeast(1, Limits.MaxPipelineStages, nameof(LimitOptions.MaxPipelineStages), adjustments);
        Limits.MaxLookupStages = AtLeast(1, Limits.MaxLookupStages, nameof(LimitOptions.MaxLookupStages), adjustments);
        Limits.MaxUnwindStages = AtLeast(1, Limits.MaxUnwindStages, nameof(LimitOptions.MaxUnwindStages), adjustments);
        Limits.MaxResolveStages = AtLeast(1, Limits.MaxResolveStages, nameof(LimitOptions.MaxResolveStages), adjustments);
        Limits.MaxGroupFields = AtLeast(1, Limits.MaxGroupFields, nameof(LimitOptions.MaxGroupFields), adjustments);
        Limits.MaxProjectionFields = AtLeast(1, Limits.MaxProjectionFields, nameof(LimitOptions.MaxProjectionFields), adjustments);
        Limits.MaxConditions = AtLeast(1, Limits.MaxConditions, nameof(LimitOptions.MaxConditions), adjustments);
        Limits.MaxVariables = AtLeast(1, Limits.MaxVariables, nameof(LimitOptions.MaxVariables), adjustments);
        Limits.MaxOffset = AtLeast(0, Limits.MaxOffset, nameof(LimitOptions.MaxOffset), adjustments);
        Limits.CountCap = AtLeast(1, Limits.CountCap, nameof(LimitOptions.CountCap), adjustments);
        Limits.MaxSemiJoinIds = AtLeast(1, Limits.MaxSemiJoinIds, nameof(LimitOptions.MaxSemiJoinIds), adjustments);
        Limits.ResolveKeyChunk = AtLeast(1, Limits.ResolveKeyChunk, nameof(LimitOptions.ResolveKeyChunk), adjustments);
        Limits.MaxResolveKeys = AtLeast(1, Limits.MaxResolveKeys, nameof(LimitOptions.MaxResolveKeys), adjustments);
        Limits.MaxRequestBytes = AtLeast(1, Limits.MaxRequestBytes, nameof(LimitOptions.MaxRequestBytes), adjustments);
        Limits.MaxBatchQueries = AtLeast(1, Limits.MaxBatchQueries, nameof(LimitOptions.MaxBatchQueries), adjustments);
        Limits.RegexMaxLength = AtLeast(1, Limits.RegexMaxLength, nameof(LimitOptions.RegexMaxLength), adjustments);
        Limits.MaxLookupLimit = AtLeast(1, Limits.MaxLookupLimit, nameof(LimitOptions.MaxLookupLimit), adjustments);
        Limits.MaxFlattenDepth = AtLeast(1, Limits.MaxFlattenDepth, nameof(LimitOptions.MaxFlattenDepth), adjustments);
        Limits.MaxContinuedStages = AtLeast(1, Limits.MaxContinuedStages, nameof(LimitOptions.MaxContinuedStages), adjustments);
        Limits.MaxReportPageSize = AtLeast(1, Limits.MaxReportPageSize, nameof(LimitOptions.MaxReportPageSize), adjustments);
        Limits.MaxReportedRows = AtLeast(1, Limits.MaxReportedRows, nameof(LimitOptions.MaxReportedRows), adjustments);
        Execution.ChainTimeoutMs = AtLeast(1, Execution.ChainTimeoutMs, nameof(ExecutionOptions.ChainTimeoutMs), adjustments, "Execution");
        Cache.NegativeResolveTtlSeconds = AtLeast(0, Cache.NegativeResolveTtlSeconds, nameof(CacheOptions.NegativeResolveTtlSeconds), adjustments, "Cache");
        Explain.RemoteTimeoutMs = AtLeast(1, Explain.RemoteTimeoutMs, nameof(ExplainOptions.RemoteTimeoutMs), adjustments, "Explain");
        Explain.MaxDescribeChildren = AtLeast(1, Explain.MaxDescribeChildren, nameof(ExplainOptions.MaxDescribeChildren), adjustments, "Explain");
        Explain.MaxDescribeRequests = AtLeast(1, Explain.MaxDescribeRequests, nameof(ExplainOptions.MaxDescribeRequests), adjustments, "Explain");

        if (Limits.MaxFlattenDepth > LimitOptions.MaxFlattenDepthCeiling)
        {
            adjustments.Add($"OxQL:Limits:MaxFlattenDepth was {Limits.MaxFlattenDepth}, above {LimitOptions.MaxFlattenDepthCeiling}; it is clamped, because every level is one more nested expression in each flattening stage.");
            Limits.MaxFlattenDepth = LimitOptions.MaxFlattenDepthCeiling;
        }

        if (Limits.MaxSemiJoinIds > Limits.MaxOffset)
        {
            adjustments.Add($"OxQL:Limits:MaxSemiJoinIds was {Limits.MaxSemiJoinIds}, above MaxOffset ({Limits.MaxOffset}); it is clamped to MaxOffset, because a semi-join reads the owner's ids by offset and cannot reach past it. Raise MaxOffset to raise it.");
            Limits.MaxSemiJoinIds = Limits.MaxOffset;
        }

        if (Limits.ResolveKeyChunk > Limits.MaxPageSize)
        {
            adjustments.Add($"OxQL:Limits:ResolveKeyChunk was {Limits.ResolveKeyChunk}, above MaxPageSize ({Limits.MaxPageSize}); it is clamped, because a resolve chunk is asked for as one page of the owner.");
            Limits.ResolveKeyChunk = Limits.MaxPageSize;
        }

        if (Limits.MaxContinuedStages > Limits.MaxPipelineStages)
        {
            adjustments.Add($"OxQL:Limits:MaxContinuedStages was {Limits.MaxContinuedStages}, above MaxPipelineStages ({Limits.MaxPipelineStages}); it is clamped, because no pipeline carries more stages to continue.");
            Limits.MaxContinuedStages = Limits.MaxPipelineStages;
        }

        if (Limits.DefaultPageSize > Limits.MaxPageSize)
        {
            adjustments.Add($"OxQL:Limits:DefaultPageSize was {Limits.DefaultPageSize}, above MaxPageSize ({Limits.MaxPageSize}); it is clamped, because a request that names no limit would otherwise be refused for the host's own default.");
            Limits.DefaultPageSize = Limits.MaxPageSize;
        }

        var collation = Representation.Collation;

        if (string.IsNullOrWhiteSpace(collation.Locale))
        {
            adjustments.Add($"OxQL:Representation:Collation:Locale was empty; it is set to '{CollationOptions.DefaultLocale}', because the database refuses a collation without a locale.");
            collation.Locale = CollationOptions.DefaultLocale;
        }

        if (collation.Strength is < CollationOptions.MinStrength or > CollationOptions.MaxStrength)
        {
            var clamped = Math.Clamp(collation.Strength, CollationOptions.MinStrength, CollationOptions.MaxStrength);

            adjustments.Add($"OxQL:Representation:Collation:Strength was {collation.Strength}; it is clamped to {clamped}, because the database accepts {CollationOptions.MinStrength} to {CollationOptions.MaxStrength}.");
            collation.Strength = clamped;
        }

        return adjustments;
    }

    private static int AtLeast(int minimum, int value, string name, List<string> adjustments, string section = "Limits")
    {
        if (value >= minimum)
            return value;

        adjustments.Add($"OxQL:{section}:{name} was {value}; it is raised to {minimum}, the least the engine can work with.");

        return minimum;
    }
}

/// <summary>Contract 1 compatibility.</summary>
public sealed class CompatOptions
{
    /// <summary>Whether a request without the contract header is treated as contract 1. On for the compat release.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>The explain endpoint.</summary>
public sealed class ExplainOptions
{
    /// <summary>
    /// Whether <c>POST /oxql/explain</c> answers; 404 otherwise. On by default (DESIGN §4.1): explain
    /// never executes the query and reads the index list only when a request asks for it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long, in milliseconds, one explain waits for the owners' internal explain in all (DESIGN
    /// §4.3): the remote check of continued parts and the describes of remote entities. An owner
    /// that does not answer in time leaves a <c>REMOTE_UNCHECKED</c> note, never an error.
    /// </summary>
    public int RemoteTimeoutMs { get; set; } = 1_500;

    /// <summary>The most children one describe answer lists; past it the answer is <c>truncated</c> (DESIGN §4.2).</summary>
    public int MaxDescribeChildren { get; set; } = 500;

    /// <summary>The most describe requests one explain answers; the ones past it are answered with an error (DESIGN §4.2).</summary>
    public int MaxDescribeRequests { get; set; } = 10;
}

/// <summary>Every cap a request is checked against. All are published on <c>/oxql/health</c>; the schema publishes the ones a caller can act on in advance.</summary>
public sealed class LimitOptions
{
    /// <summary>The largest page a request may ask for.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>The page size a request that names none gets, with or without a page stage.</summary>
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>The longest pipeline, counting every caller stage; the engine's scope stage does not count.</summary>
    public int MaxPipelineStages { get; set; } = 20;

    /// <summary>How many lookup stages one pipeline may carry.</summary>
    public int MaxLookupStages { get; set; } = 5;

    /// <summary>How many unwind stages one pipeline may carry.</summary>
    public int MaxUnwindStages { get; set; } = 5;

    /// <summary>How many resolve stages one pipeline may carry on this host; stages continued at an owner count there.</summary>
    public int MaxResolveStages { get; set; } = 8;

    /// <summary>How many keys and aggregates one group stage may carry.</summary>
    public int MaxGroupFields { get; set; } = 20;

    /// <summary>How many fields one projection may name.</summary>
    public int MaxProjectionFields { get; set; } = 500;

    /// <summary>How many leaf conditions one request may carry, lookup and resolve filters included.</summary>
    public int MaxConditions { get; set; } = 200;

    /// <summary>How many variables one request may bind.</summary>
    public int MaxVariables { get; set; } = 64;

    /// <summary>The largest <c>offset</c> a page may skip to; beyond it a cursor is needed.</summary>
    public int MaxOffset { get; set; } = 5_000;

    /// <summary>The count above which <c>totalCount</c> is reported as the cap with <c>totalCountCapped</c>.</summary>
    public int CountCap { get; set; } = 100_000;

    /// <summary>
    /// The most ids a semi-join may substitute; a larger set is refused, never truncated.
    /// <para>
    /// Kept at or below the owner's <see cref="MaxOffset"/> so every page of ids is reachable by
    /// offset and the whole set arrives in one batch. Raising it past that puts the tail of the
    /// set out of reach, and the substituted <c>$in</c> grows with it: the list travels in every
    /// page request and costs the database one index seek per id.
    /// </para>
    /// </summary>
    public int MaxSemiJoinIds { get; set; } = 5_000;

    /// <summary>How many keys one remote resolve call carries.</summary>
    public int ResolveKeyChunk { get; set; } = 500;

    /// <summary>
    /// The most keys one request asks owners for, over every keyed stage of the request; keys
    /// beyond it are not fetched, their rows are <c>owner_unanswered</c>, and the page carries a
    /// <c>RESOLVE_PARTIAL</c> diagnostic. A key counts once per stage, whichever targets of a union it
    /// is asked of, and a key the cache answers costs nothing.
    /// <para>
    /// The budget is per request, not per stage: several keyed stages over a report page of
    /// <see cref="MaxReportPageSize"/> rows, or <c>elements: "all"</c> with several keys per row,
    /// reach it at the defaults.
    /// </para>
    /// </summary>
    public int MaxResolveKeys { get; set; } = 10_000;

    /// <summary>The largest request body.</summary>
    public int MaxRequestBytes { get; set; } = 262_144;

    /// <summary>The most queries one batch may carry.</summary>
    public int MaxBatchQueries { get; set; } = 10;

    /// <summary>The longest regex pattern a filter operand may carry, in characters.</summary>
    public int RegexMaxLength { get; set; } = 200;

    /// <summary>The most rows one lookup returns per parent.</summary>
    public int MaxLookupLimit { get; set; } = 100;

    /// <summary>
    /// The ceiling <see cref="MaxFlattenDepth"/> is clamped to. Every level nests the flattening
    /// expression deeper; at 16 the pipeline no longer serializes (the driver stops at 100 levels),
    /// so the ceiling keeps a margin below the 15 that still does.
    /// </summary>
    public const int MaxFlattenDepthCeiling = 12;

    /// <summary>
    /// How many levels an <c>unwind</c> with <c>flatten</c> descends: the collection itself is
    /// level 1. Elements nested deeper are not in the rows, and the response carries an
    /// <c>UNWIND_DEPTH_TRUNCATED</c> diagnostic when a row had any.
    /// </summary>
    public int MaxFlattenDepth { get; set; } = 5;

    /// <summary>How many stages one keyed alias may continue at its owner, counting every stage forwarded under it.</summary>
    public int MaxContinuedStages { get; set; } = 8;

    /// <summary>
    /// The largest page a strict request without <c>cursor</c> or <c>offset</c> may ask for: a
    /// report reads its rows in one page and refuses rather than cut them. A value below
    /// <see cref="MaxPageSize"/> gives strict requests no larger page, never a smaller one;
    /// every other request stays under <see cref="MaxPageSize"/>.
    /// </summary>
    public int MaxReportPageSize { get; set; } = 5_000;

    /// <summary>How many rows one diagnostic lists (a missing or ambiguous reference names the rows it met, up to this many).</summary>
    public int MaxReportedRows { get; set; } = 50;
}

/// <summary>Time and memory bounds on the aggregate.</summary>
public sealed class ExecutionOptions
{
    /// <summary>The ceiling <see cref="MaxTimeMs"/> is clamped to.</summary>
    public const int MaxTimeCeilingMs = 60_000;

    /// <summary><c>maxTimeMS</c> on every aggregate; a timeout is 504 <c>QUERY_TIMEOUT</c>.</summary>
    public int MaxTimeMs { get; set; } = 10_000;

    /// <summary>The budget of one remote resolve call.</summary>
    public int ResolveTimeoutMs { get; set; } = 2_000;

    /// <summary>The budget of one owner call that carries continued stages or typed/item targets, which the owner may pass on.</summary>
    public int ChainTimeoutMs { get; set; } = 6_000;

    /// <summary>
    /// <c>allowDiskUse</c> on every aggregate. True by default: a sort or a group that outgrows
    /// the server's in-memory limit spills to disk and finishes slowly instead of failing with
    /// <c>QUERY_TOO_EXPENSIVE</c>, whatever the server's own default is set to. False refuses
    /// such a query; null leaves the server's default.
    /// </summary>
    public bool? AllowDiskUse { get; set; } = true;

    /// <summary>
    /// The total time of a request, binding, aggregates and remote resolves included, above
    /// which it is logged at warning level with what shaped it: the entity, the stage kinds,
    /// whether a regex, an unbounded sort, a count or a remote resolve was involved, the
    /// duration and the row count, never an operand. 0 turns the line off.
    /// </summary>
    public int SlowQueryMs { get; set; } = 1_000;

    /// <summary>The effective <c>maxTimeMS</c>: the configured value under the ceiling.</summary>
    public int EffectiveMaxTimeMs => Math.Clamp(MaxTimeMs, 1, MaxTimeCeilingMs);

    /// <summary>The effective resolve budget: the configured value under <see cref="EffectiveMaxTimeMs"/>.</summary>
    public int EffectiveResolveTimeoutMs => Math.Clamp(ResolveTimeoutMs, 1, EffectiveMaxTimeMs);

    /// <summary>The effective chain budget: the configured value under <see cref="EffectiveMaxTimeMs"/>.</summary>
    public int EffectiveChainTimeoutMs => Math.Clamp(ChainTimeoutMs, 1, EffectiveMaxTimeMs);

    /// <summary>The effective slow-query threshold: the configured value, never negative; 0 is off.</summary>
    public int EffectiveSlowQueryMs => Math.Max(0, SlowQueryMs);
}

/// <summary>How operands are encoded for members whose storage is not uniform.</summary>
public sealed class RepresentationOptions
{
    /// <summary>Whether a guid operand also matches legacy subtype 3 and string storage. Off: this fleet has stored standard guids since 2022.</summary>
    public bool GuidTolerant { get; set; }

    /// <summary><c>tolerant</c> matches decimals stored as Decimal128 and as strings; <c>typed</c> Decimal128 only, after the migration.</summary>
    public string DecimalMode { get; set; } = "tolerant";

    /// <summary>Whether decimal operands are matched in both storage forms.</summary>
    public bool DecimalTolerant => !string.Equals(DecimalMode, "typed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The collation a contract 2 aggregate runs under when a string comparison, sort or group key folds case.</summary>
    public CollationOptions Collation { get; set; } = new();
}

/// <summary>
/// The collation string comparisons, sorts and group keys fold under. The aggregate carries it
/// only when the pipeline folds at least one string; a pipeline that compares no string runs
/// without one, as before. A comparison that opts out with <c>caseSensitive</c> is compiled to
/// a form the collation does not reach.
/// </summary>
public sealed class CollationOptions
{
    /// <summary>The locale used when none is configured.</summary>
    public const string DefaultLocale = "de";

    /// <summary>The least strength the database accepts: primary, which folds case and accents.</summary>
    public const int MinStrength = 1;

    /// <summary>The greatest strength the database accepts: identical.</summary>
    public const int MaxStrength = 5;

    /// <summary>The ICU locale of the collation.</summary>
    public string Locale { get; set; } = DefaultLocale;

    /// <summary>The comparison strength: 1 folds case and accents, 2 folds case only, 3 and above tell both apart.</summary>
    public int Strength { get; set; } = MinStrength;
}

/// <summary>In-process caches.</summary>
public sealed class CacheOptions
{
    /// <summary>How long a resolved remote row stays cached.</summary>
    public int ResolveTtlSeconds { get; set; } = 60;

    /// <summary>How long a key the owner answered as missing stays cached; a strict request bypasses such entries. 0 caches no missing key.</summary>
    public int NegativeResolveTtlSeconds { get; set; } = 10;

    /// <summary>The former name of <see cref="OwnerFetchCacheMaxEntries"/>; configuration under this key still binds to it,
    /// and the new key wins when both are set (the configuration binder sets properties in declaration order).</summary>
    [Obsolete("Use OwnerFetchCacheMaxEntries; this alias is kept for one release.")]
    public int ResolveCacheMaxEntries
    {
        get => OwnerFetchCacheMaxEntries;
        set => OwnerFetchCacheMaxEntries = value;
    }

    /// <summary>
    /// The owner-fetch cache's budget per mode: the most resolved remote rows it holds, and the most
    /// semi-join ids (one unit per id).
    /// </summary>
    public int OwnerFetchCacheMaxEntries { get; set; } = 50_000;

    /// <summary>How long an organisation's addon definitions stay cached on replicas that did not write them.</summary>
    public int AddonDefinitionTtlSeconds { get; set; } = 30;

    /// <summary>
    /// How long <c>/oxql/health</c> reuses the reachability it last measured. The probe calls
    /// every service the model references, so without it an anonymous caller turns one request
    /// into one call per service; with it that cost is paid once per interval however often the
    /// endpoint is asked. Freshness is worth less here than the endpoint staying cheap.
    /// </summary>
    public int HealthProbeTtlSeconds { get; set; } = 10;
}

/// <summary>Cursor signing.</summary>
public sealed class CursorOptions
{
    /// <summary>The secret cursors are signed with; the base package derives it from the host's auth token. The engine refuses to run without one.</summary>
    public string? SigningKey { get; set; }
}
