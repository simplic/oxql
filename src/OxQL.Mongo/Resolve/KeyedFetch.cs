using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Mongo.Resolve;

/// <summary>What a resolve pass produced: the objects per row and alias, the diagnostics, and the counters for the log line.</summary>
public sealed record ResolveResult
{
    /// <summary>A refusal that aborts the request: the owner refused, or a semi-join could not be answered.</summary>
    public Refusal? Refusal { get; init; }

    /// <summary>Per row (same order as the page), the resolved object per alias; null when the owner has no such key or did not answer.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>> Rows { get; init; } = [];

    /// <summary>What did not change the rows.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Every row (or element, under <c>elements: "all"</c>) whose keyed resolve did not simply
    /// resolve, with its outcome (DESIGN §3.6); an <c>ambiguous</c> one resolved to its first
    /// target. What the outcomes report or refuse is the caller's policy.
    /// </summary>
    public IReadOnlyList<KeyedRowOutcome> Outcomes { get; init; } = [];

    /// <summary>The rows whose <c>elements: "all"</c> alias holds only the first <c>MaxLookupLimit</c> of more targets.</summary>
    public IReadOnlyList<KeyedTruncation> Truncations { get; init; } = [];

    /// <summary>
    /// The select and owning-row select paths remote owners said one target of a union lacks, dropped
    /// for that target (DESIGN §3.4.1 flat select); what explain's <c>SELECT_PATH_NOT_ON_TARGET</c>
    /// note names for a remote target. A local target's are on its bound target.
    /// </summary>
    public IReadOnlyList<DroppedSelectPath> Dropped { get; init; } = [];

    /// <summary>How many calls went to remote owners.</summary>
    public int Calls { get; init; }

    /// <summary>How many keys were answered from the cache.</summary>
    public int CacheHits { get; init; }
}

/// <summary>What became of one reference value of a keyed resolve (DESIGN §3.6).</summary>
public enum KeyedOutcome
{
    /// <summary>The key selects a case and exactly one target record or element.</summary>
    Resolved,

    /// <summary>As resolved, but more than one record or element holds the key; the alias holds the first by target order, then record key.</summary>
    Ambiguous,

    /// <summary>The reference is null or absent, or <c>elements</c> found no element.</summary>
    ReferenceNull,

    /// <summary>No case matches the stored values, <c>target</c> narrowed the case away, or the target's filter left the record out (the existence probe found it).</summary>
    Excluded,

    /// <summary>The key selects a case and no target holds a record for it.</summary>
    NotFound,

    /// <summary>A case converts the key (<c>KeyAs</c>) and the stored value does not convert.</summary>
    InvalidKey,

    /// <summary>The owner did not answer for the key: timeout, unreachable, the key budget left it out, or the owner answered its chunk only in part.</summary>
    OwnerUnanswered,

    /// <summary>A continued stage with <c>forTarget</c> on a row its keyed stage resolved to another target: its alias is null, which loses nothing.</summary>
    NotApplicable,
}

/// <summary>
/// The outcome of one row of a keyed resolve: the caller's <paramref name="Stage"/> index, the
/// alias, the page row, the element index under <c>elements</c>, and the key (in the form sent to
/// the owner) where there is one.
/// </summary>
public sealed record KeyedRowOutcome(int? Stage, string Alias, int Row, int? Element, string? Key, KeyedOutcome Outcome);

/// <summary>A select path (<paramref name="Parent"/>: of the owning row) that one target of a union's keyed stage lacks, dropped for it.</summary>
public sealed record DroppedSelectPath(int? Stage, string Alias, string Target, string Path, bool Parent);

/// <summary>
/// One target's owner query of a keyed stage as explain shows it: the target (<c>entity</c> or
/// <c>entity#item</c>), whether it is remote and the service owning it, whether the query is grouped
/// per key, the query with its keys elided, the continued stages it carries and those that do not
/// apply to its rows.
/// </summary>
public sealed record ExplainedOwnerQuery(string Target, bool Remote, string Service, bool Grouped, QueryRequest Query, IReadOnlyList<ContinuedStage> Continued, IReadOnlyList<ContinuedStage> NotApplicable);

/// <summary>
/// One owner query of a keyed stage checked at its owner's internal explain (DESIGN §4.3 remote
/// check): the target (<c>entity</c> or <c>entity#item</c>), the owner's service, the query with the
/// check key, the caller index of its first continued stage, and the mapping of an owner error back
/// to the caller (null for an error at the owner query's own stages).
/// <para>
/// A target is checked for its continued stages and for the paths the caller wrote under its
/// aliases: its <c>select</c>, its <c>parentSelect</c>, and every path the last projection after the
/// stage names under them, which the check query projects at <see cref="ProjectAt"/> so the
/// owner says which it lacks. <see cref="Stage"/> is the resolve's caller index,
/// <see cref="ProjectStage"/> that projection's; <c>select</c> and
/// <c>parentSelect</c> are what the caller wrote.
/// </para>
/// </summary>
public sealed record OwnerCheck(string Target, string Service, QueryRequest Query, int FirstContinued, Func<JsonObject, QueryValidationError?> Map)
{
    /// <summary>The resolve this target belongs to.</summary>
    public BoundStage.Resolve? Resolve { get; init; }

    /// <summary>The target as bound.</summary>
    public BoundResolveTarget? Bound { get; init; }

    /// <summary>The resolve's caller index.</summary>
    public int? Stage { get; init; }

    /// <summary>The caller index of the last projection after the resolve, when it names paths under its aliases.</summary>
    public int? ProjectStage { get; init; }

    /// <summary>The check query's projection stage, where an owner reports a path the target lacks.</summary>
    public int ProjectAt { get; init; } = -1;

    /// <summary>Whether the target is an item: its members travel under <see cref="BoundKeyedBy.Element"/>.</summary>
    public bool Item { get; init; }
}

/// <summary>A row whose <c>elements: "all"</c> alias resolved <paramref name="Count"/> targets, more than it holds.</summary>
public sealed record KeyedTruncation(int? Stage, string Alias, int Row, int Count);

/// <summary>What the outcomes of a page's resolves say: the diagnostics, and those of them the request refuses on.</summary>
public sealed record OutcomeReport(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<Diagnostic> Refusing)
{
    /// <summary>A report with nothing to say.</summary>
    public static readonly OutcomeReport Empty = new([], []);
}

/// <summary>
/// The policy over per-row join outcomes (DESIGN §3.6, §3.4.3): the outcomes of a resolve, keyed
/// or inline, become at most one diagnostic per stage and kind, and the diagnostics that lose
/// data refuse the request under <c>strict</c> or <c>onMissing: "refuse"</c>.
/// <list type="bullet">
///   <item><c>RESOLVE_AMBIGUOUS</c>, always: the rows whose key more than one record holds;</item>
///   <item><c>RESOLVE_MISSING</c>, under an effective <c>onMissing</c> of <c>report</c> or
///   <c>refuse</c>: the rows whose outcome is <c>not_found</c>, <c>invalid_key</c> or
///   <c>owner_unanswered</c>; refused under <c>refuse</c>;</item>
///   <item><c>RESOLVE_TRUNCATED</c>, always: the rows whose <c>elements: "all"</c> alias holds only the first targets.</item>
/// </list>
/// The rows a diagnostic lists are capped at <c>MaxReportedRows</c>; <c>count</c> is every one.
/// </summary>
public static class OutcomePolicy
{
    /// <summary>The diagnostics a strict request refuses on (DESIGN §3.4.3); <c>RESOLVE_MISSING</c> refuses by its stage's effective <c>onMissing</c>.</summary>
    public static readonly IReadOnlySet<string> StrictCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        Codes.ResolveAmbiguous, Codes.ResolveTimeout, Codes.ResolveUnreachable, Codes.ResolvePartial,
        Codes.LookupTruncated, Codes.ResolveTruncated, Codes.UnwindDepthTruncated, Codes.PageIncomplete,
    };

    /// <summary>The wire name of an outcome, as <c>params.rows[].outcome</c> carries it.</summary>
    public static string WireName(KeyedOutcome outcome) => outcome switch
    {
        KeyedOutcome.Resolved => "resolved",
        KeyedOutcome.Ambiguous => "ambiguous",
        KeyedOutcome.ReferenceNull => "reference_null",
        KeyedOutcome.Excluded => "excluded",
        KeyedOutcome.NotFound => "not_found",
        KeyedOutcome.InvalidKey => "invalid_key",
        KeyedOutcome.OwnerUnanswered => "owner_unanswered",
        KeyedOutcome.NotApplicable => "not_applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>Whether an outcome is a missing reference: data loss that <c>onMissing</c> governs.</summary>
    public static bool IsMissing(KeyedOutcome outcome) => outcome is KeyedOutcome.NotFound or KeyedOutcome.InvalidKey or KeyedOutcome.OwnerUnanswered;

    /// <summary>
    /// The diagnostics of the outcomes and truncations of <paramref name="bound"/>'s resolves, in
    /// stage order, and those that refuse: a <c>RESOLVE_MISSING</c> of a stage whose effective
    /// <c>onMissing</c> is <c>refuse</c>, and under <paramref name="strict"/> every other one.
    /// </summary>
    public static OutcomeReport Report(BoundPipeline bound, IEnumerable<KeyedRowOutcome> outcomes, IEnumerable<KeyedTruncation> truncations, bool strict, OxQLOptions options)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(options);

        var byAlias = bound.Stages.OfType<BoundStage.Resolve>().ToDictionary(stage => stage.As, StringComparer.Ordinal);
        var cap = Math.Max(1, options.Limits.MaxReportedRows);
        var diagnostics = new List<Diagnostic>();
        var refusing = new List<Diagnostic>();
        var outcomeGroups = outcomes.GroupBy(outcome => (outcome.Stage, outcome.Alias)).ToDictionary(group => group.Key, group => group.ToList());
        var truncationGroups = truncations.GroupBy(truncation => (truncation.Stage, truncation.Alias)).ToDictionary(group => group.Key, group => group.ToList());
        var keys = outcomeGroups.Keys.Concat(truncationGroups.Keys).Distinct().OrderBy(key => key.Stage ?? int.MaxValue).ThenBy(key => key.Alias, StringComparer.Ordinal);

        foreach (var key in keys)
        {
            byAlias.TryGetValue(key.Alias, out var stage);
            var path = stage?.Reference.Wire;
            var rows = outcomeGroups.GetValueOrDefault(key) ?? [];

            var ambiguous = rows.Where(row => row.Outcome == KeyedOutcome.Ambiguous).ToList();

            if (ambiguous.Count > 0)
            {
                var diagnostic = Rows(Codes.ResolveAmbiguous, key.Stage, key.Alias, path, ambiguous, cap,
                    $"{ambiguous.Count} {Rows(ambiguous.Count)} a key that more than one record holds ('{key.Alias}'); the first by target order, then key, is taken.");

                diagnostics.Add(diagnostic);

                if (strict)
                    refusing.Add(diagnostic);
            }

            var missing = rows.Where(row => IsMissing(row.Outcome)).ToList();
            var onMissing = stage?.EffectiveOnMissing ?? ResolveOnMissing.Null;

            if (missing.Count > 0 && onMissing != ResolveOnMissing.Null)
            {
                var diagnostic = Rows(Codes.ResolveMissing, key.Stage, key.Alias, path, missing, cap,
                    missing.All(row => row.Outcome == KeyedOutcome.NotFound)
                        ? $"{missing.Count} {Rows(missing.Count)} a record that does not exist ('{key.Alias}')."
                        : $"{missing.Count} {Rows(missing.Count)} a record that does not exist, by a key that does not convert, or that its owner did not answer ('{key.Alias}').");

                diagnostics.Add(diagnostic);

                if (onMissing == ResolveOnMissing.Refuse)
                    refusing.Add(diagnostic);
            }

            // A remote lookup over its limit is the lookup's truncation, as a local lookup reports it.
            if (truncationGroups.GetValueOrDefault(key) is { Count: > 0 } cut && stage?.RemoteLookup is { } lookup)
            {
                var diagnostic = new Diagnostic
                {
                    Code = Codes.LookupTruncated,
                    Message = $"'{key.Alias}' holds the first {lookup.Limit} children of a parent that has more ({cut.Count} {(cut.Count == 1 ? "row" : "rows")} of this page affected).",
                    Stage = key.Stage,
                    Path = key.Alias,
                    Params = new Dictionary<string, object?> { ["alias"] = key.Alias, ["limit"] = lookup.Limit, ["rows"] = cut.Count },
                };

                diagnostics.Add(diagnostic);

                if (strict)
                    refusing.Add(diagnostic);
            }
            else if (truncationGroups.GetValueOrDefault(key) is { Count: > 0 } truncated)
            {
                var limit = options.Limits.MaxLookupLimit;
                var diagnostic = new Diagnostic
                {
                    Code = Codes.ResolveTruncated,
                    Message = $"'{key.Alias}' holds the first {limit} targets of a row that references more ({truncated.Count} {(truncated.Count == 1 ? "row" : "rows")} of this page affected).",
                    Stage = key.Stage,
                    Path = path,
                    Params = new Dictionary<string, object?> { ["alias"] = key.Alias, ["limit"] = limit, ["rows"] = truncated.Count },
                };

                diagnostics.Add(diagnostic);

                if (strict)
                    refusing.Add(diagnostic);
            }
        }

        return new OutcomeReport(diagnostics, refusing);
    }

    /// <summary>
    /// The diagnostics of a page that refuse under <paramref name="strict"/> by their code alone:
    /// owner failures and truncations (the outcome diagnostics refuse through <see cref="Report"/>).
    /// </summary>
    public static IEnumerable<Diagnostic> StrictLosses(IEnumerable<Diagnostic> diagnostics, bool strict) =>
        strict ? diagnostics.Where(diagnostic => StrictCodes.Contains(diagnostic.Code) && diagnostic.Code is not (Codes.ResolveAmbiguous or Codes.ResolveTruncated)) : [];

    private static string Rows(int count) => count == 1 ? "row references" : "rows reference";

    /// <summary>A diagnostic listing rows: <c>{ alias, count, truncated, rows: [{ row, element?, key, outcome }] }</c>, rows by row then element, at most <paramref name="cap"/>.</summary>
    private static Diagnostic Rows(string code, int? stage, string alias, string? path, IReadOnlyList<KeyedRowOutcome> outcomes, int cap, string message)
    {
        var listed = outcomes
            .OrderBy(outcome => outcome.Row)
            .ThenBy(outcome => outcome.Element ?? -1)
            .Take(cap)
            .Select(outcome =>
            {
                var entry = new Dictionary<string, object?> { ["row"] = outcome.Row };

                if (outcome.Element is { } element)
                    entry["element"] = element;

                entry["key"] = outcome.Key;
                entry["outcome"] = WireName(outcome.Outcome);

                return entry;
            })
            .ToList();

        return new Diagnostic
        {
            Code = code,
            Message = message,
            Stage = stage,
            Path = path,
            Params = new Dictionary<string, object?>
            {
                ["alias"] = alias,
                ["count"] = outcomes.Count,
                ["truncated"] = outcomes.Count > cap,
                ["rows"] = listed,
            },
        };
    }
}

/// <summary>
/// The one cross-record fetch through an owner (DESIGN §3.5.8), in two modes that share the
/// owner query builder (<see cref="OwnerQueryBuilder"/>), the owner client, batching at
/// <c>MaxBatchQueries</c>, budgeting, error mapping and the cache (<see cref="OwnerFetchCache"/>):
/// <list type="bullet">
///   <item><b>by keys</b> (<see cref="ByKeysAsync"/>): after the page is fixed, one batch per owning
///   service carrying one query per target and key chunk — plain for an entity keyed by its own
///   key, grouped per key (<c>keyedBy</c>) otherwise — and this host's own targets answered by its
///   <see cref="SelfOwner"/>; cached per key for a TTL;</item>
///   <item><b>by condition</b> (<see cref="ByConditionAsync"/>): before the page runs, the semi-join
///   that asks an owner for the ids a condition on its rows selects, refusing above the cap rather
///   than truncating.</item>
/// </list>
/// </summary>
public sealed class KeyedFetch
{
    private readonly IRemoteQueryClient? client;
    private readonly IQueryEngine? self;
    private readonly OwnerFetchCache cache;
    private readonly OxQLOptions options;

    /// <summary>A keyed fetch through <paramref name="client"/>, caching owner answers in <paramref name="cache"/>.</summary>
    public KeyedFetch(IRemoteQueryClient client, OwnerFetchCache cache, OxQLOptions options)
        : this(client ?? throw new ArgumentNullException(nameof(client)), null, cache, options)
    {
    }

    /// <summary>
    /// A keyed fetch whose remote targets go through <paramref name="client"/> (none when null: the
    /// engine refuses such a request before it gets here) and whose local targets this host's own
    /// <paramref name="self"/> answers through a <see cref="SelfOwner"/>, caching owner answers in
    /// <paramref name="cache"/>.
    /// </summary>
    public KeyedFetch(IRemoteQueryClient? client, IQueryEngine? self, OwnerFetchCache cache, OxQLOptions options)
    {
        this.client = client;
        this.self = self;
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The service key a target entity is owned by: its namespace, the host's <c>InternalHosts</c> key.</summary>
    public static string ServiceKeyOf(string targetEntity) => targetEntity.Split('.')[0];

    // ---- by condition: the semi-join, before the page ---------------------------------------

    /// <summary>
    /// Fills every semi-join slot with the ids the owner selects for the leaf's condition.
    /// <para>
    /// The first call asks the owner for page 0 <b>and the count</b>, so a condition selecting
    /// more than <c>MaxSemiJoinIds</c> is refused after one round trip instead of after walking
    /// every page to find out. The remaining pages are addressed by <c>offset</c> and sent
    /// together, which is why the cap is kept within what the owner's own <c>MaxOffset</c> can
    /// reach. Every call is bounded by one deadline for the whole phase rather than a fresh
    /// timeout each: without the ids the filter cannot be evaluated, and the engine never
    /// executes something else instead.
    /// </para>
    /// </summary>
    public async Task<Refusal?> ByConditionAsync(CompiledQuery compiled, RequestContext context, TimeSpan remaining, CancellationToken cancellationToken) =>
        (await ByConditionCountedAsync(compiled, context, remaining, cancellationToken).ConfigureAwait(false)).Refusal;

    /// <summary>
    /// <see cref="ByConditionAsync"/>, with how many batches went to owners for it (none for slots
    /// the cache answers, a split batch counted per part), for the log line.
    /// </summary>
    public async Task<(Refusal? Refusal, int Calls)> ByConditionCountedAsync(CompiledQuery compiled, RequestContext context, TimeSpan remaining, CancellationToken cancellationToken)
    {
        var calls = new StrongBox(0);
        var refusal = await ByConditionCoreAsync(compiled, context, remaining, calls, cancellationToken).ConfigureAwait(false);

        return (refusal, calls.Value);
    }

    /// <summary>A counter the semi-join's calls add to.</summary>
    private sealed class StrongBox(int value)
    {
        public int Value = value;
    }

    private async Task<Refusal?> ByConditionCoreAsync(CompiledQuery compiled, RequestContext context, TimeSpan remaining, StrongBox calls, CancellationToken cancellationToken)
    {
        var slots = compiled.SemiJoins;

        if (slots.Count == 0)
            return null;

        var organisation = context.Organisation!.Value;
        var cap = options.Limits.MaxSemiJoinIds;
        var deadline = DateTime.UtcNow + (remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));

        // An answer already in hand costs the owner nothing, and a grid asks the same question
        // again on every block of the same filter.
        var pending = new List<SemiJoinSlot>();

        await ReadOwnersAsync(slots.Select(slot => ServiceKeyOf(OwnerQueryBuilder.TargetOf(slot))), remaining, cancellationToken).ConfigureAwait(false);

        // One page size for the phase: the smallest page any owner asked answers, so no owner
        // refuses a page as too large (RE-13).
        var pageSize = Math.Max(1, Math.Min(slots.Select(slot => PageOf(options, client, ServiceKeyOf(OwnerQueryBuilder.TargetOf(slot)))).DefaultIfEmpty(options.Limits.MaxPageSize).Min(), cap));
        var lastOffset = (cap / pageSize) * pageSize;

        foreach (var slot in slots)
        {
            if (cache.TryGetKeys(CacheKeyOf(slot, organisation, pageSize), out var cached))
                Fill(slot, cached);
            else
                pending.Add(slot);
        }

        if (pending.Count == 0)
            return null;

        var plans = pending.ToDictionary(slot => slot, slot => new SlotPlan(ServiceKeyOf(OwnerQueryBuilder.TargetOf(slot))));

        // Round one: the first page of every pending slot, with the count that decides whether
        // asking for the rest is worth a round trip at all; owners in parallel.
        var firsts = pending.GroupBy(slot => plans[slot].Service, StringComparer.Ordinal).Select(async group =>
        {
            var members = group.ToList();
            var queries = members.Select(slot => OwnerQueryBuilder.ByCondition(slot, pageSize, offset: 0, count: true)).ToList();

            return (Service: group.Key, Members: members, Outcome: await CallInBatchesAsync(client!, group.Key, queries, deadline, chain: false, cancellationToken).ConfigureAwait(false));
        }).ToList();

        await Task.WhenAll(firsts).ConfigureAwait(false);

        foreach (var (service, members, outcome) in firsts.Select(task => task.Result))
        {
            calls.Value += outcome.Calls;

            if (outcome.Failure is not null)
                return Unanswered(service, outcome);

            for (var index = 0; index < members.Count; index++)
            {
                var slot = members[index];
                var plan = plans[slot];
                var result = index < outcome.Results.Count ? outcome.Results[index] : null;

                if (result is null || !Succeeded(result))
                    return Refused(result, OwnerQueryBuilder.TargetOf(slot), StageIndexOf(compiled.Bound, slot));

                var total = CountOf(result);

                // A capped count says "at least this many", so either form refuses once what it
                // reports is already past the cap; below that a capped count is indeterminate.
                if (total is { Value: var reported } && reported > cap)
                    return TooLarge(slot, cap, StageIndexOf(compiled.Bound, slot));

                var full = Take(plan, result, slot, pageSize);

                if (full is null)
                    return WithoutKey(OwnerQueryBuilder.TargetOf(slot), OwnerQueryBuilder.TargetFieldOf(slot), StageIndexOf(compiled.Bound, slot));

                if (full == false)
                    continue;

                if (total is { Capped: false, Value: var exact })
                {
                    // The count is exact, so every remaining page is known and goes out together.
                    for (var offset = pageSize; offset < exact && offset <= lastOffset; offset += pageSize)
                        plan.Queue.Enqueue(offset);
                }
                else
                {
                    // No usable count: ask one page at a time and stop at the first short one.
                    plan.Indeterminate = true;
                    plan.Queue.Enqueue(pageSize);
                }
            }
        }

        // Round two: the remaining pages, batched per owner, owners in parallel. A slot with an
        // exact count empties its queue in one wave; an indeterminate one takes a wave per page.
        while (plans.Values.Any(plan => plan.Queue.Count > 0))
        {
            var wave = new List<(SemiJoinSlot Slot, int Offset)>();

            foreach (var (slot, plan) in plans)
                while (plan.Queue.Count > 0)
                {
                    wave.Add((slot, plan.Queue.Dequeue()));

                    if (plan.Indeterminate)
                        break;
                }

            var waves = wave.GroupBy(entry => plans[entry.Slot].Service, StringComparer.Ordinal).Select(async group =>
            {
                var entries = group.ToList();
                var queries = entries.Select(entry => OwnerQueryBuilder.ByCondition(entry.Slot, pageSize, entry.Offset, count: false)).ToList();

                return (Entries: entries, Service: group.Key, Outcome: await CallInBatchesAsync(client!, group.Key, queries, deadline, chain: false, cancellationToken).ConfigureAwait(false));
            }).ToList();

            await Task.WhenAll(waves).ConfigureAwait(false);

            foreach (var call in waves.Select(task => task.Result))
            {
                calls.Value += call.Outcome.Calls;

                if (call.Outcome.Failure is not null)
                    return Unanswered(call.Service, call.Outcome);

                for (var index = 0; index < call.Entries.Count; index++)
                {
                    var (slot, offset) = call.Entries[index];
                    var plan = plans[slot];
                    var result = index < call.Outcome.Results.Count ? call.Outcome.Results[index] : null;

                    if (result is null || !Succeeded(result))
                        return Refused(result, OwnerQueryBuilder.TargetOf(slot), StageIndexOf(compiled.Bound, slot));

                    if (plan.Ids.Count > cap)
                        return TooLarge(slot, cap, StageIndexOf(compiled.Bound, slot));

                    var full = Take(plan, result, slot, pageSize);

                    if (full is null)
                        return WithoutKey(OwnerQueryBuilder.TargetOf(slot), OwnerQueryBuilder.TargetFieldOf(slot), StageIndexOf(compiled.Bound, slot));

                    if (full == true && plan.Indeterminate && offset + pageSize <= lastOffset)
                        plan.Queue.Enqueue(offset + pageSize);
                }
            }
        }

        foreach (var (slot, plan) in plans)
        {
            if (plan.Ids.Count > cap)
                return TooLarge(slot, cap, StageIndexOf(compiled.Bound, slot));

            Fill(slot, plan.Ids);
            cache.SetKeys(CacheKeyOf(slot, organisation, pageSize), plan.Ids);
        }

        return null;
    }

    /// <summary>What is still to fetch for one slot and what has arrived.</summary>
    private sealed class SlotPlan(string service)
    {
        public string Service { get; } = service;

        /// <summary>The owner's wire values of the target field, in the order they arrived.</summary>
        public List<string> Ids { get; } = [];

        public Queue<int> Queue { get; } = new();

        /// <summary>The owner gave no usable count, so the pages are walked one wave at a time.</summary>
        public bool Indeterminate { get; set; }
    }

    /// <summary>Adds the owner's page of ids to the plan; true when the page was full, null when a row lacks the target field.</summary>
    private static bool? Take(SlotPlan plan, JsonNode result, SemiJoinSlot slot, int pageSize)
    {
        var page = IdsOf(result, slot);

        if (page is null)
            return null;

        plan.Ids.AddRange(page);

        return page.Count >= pageSize;
    }

    /// <summary>
    /// The cache key of a slot: the organisation and the first-page query the owner is sent, which
    /// between them determine every wire value the owner answers with.
    /// </summary>
    private static string CacheKeyOf(SemiJoinSlot slot, Guid organisation, int pageSize) =>
        OwnerFetchCache.KeyOf(organisation, OwnerQueryBuilder.ByCondition(slot, pageSize, offset: 0, count: true));

    /// <summary>Fills a slot with the owner's wire values, encoded as this slot's reference member is stored.</summary>
    private static void Fill(SemiJoinSlot slot, IReadOnlyList<string> values)
    {
        var reference = ((ShapeNode.Remote)slot.Leaf.Path.Root).Reference;

        foreach (var value in values)
            slot.Ids.Add(OwnerValueToBson(value, reference));
    }

    private static Refusal Unanswered(string service, CallOutcome outcome) =>
        Refusal.NotExecutable(Codes.ResolveUnavailable, outcome.Status is { } status
            ? $"The owner of '{service}' answered the semi-join with HTTP {status}; the condition cannot be evaluated."
            : $"The owner of '{service}' did not answer the semi-join ({outcome.Failure}); the condition cannot be evaluated.");

    private static Refusal TooLarge(SemiJoinSlot slot, int cap, int? stage) =>
        Refusal.NotExecutable(Codes.SemiJoinTooLarge, $"The condition on '{slot.Leaf.Path.Wire}' selects more than {cap} rows of '{OwnerQueryBuilder.TargetOf(slot)}'; narrow it.", stage);

    /// <summary>The count an owner reported for a page, when one was asked for.</summary>
    private static (long Value, bool Capped)? CountOf(JsonNode result)
    {
        if (result["pageInfo"] is not JsonObject info || info["totalCount"] is not { } total)
            return null;

        return (total.GetValue<long>(), info["totalCountCapped"]?.GetValue<bool>() == true);
    }

    /// <summary>The owner's ids in wire form, or null when a row does not carry the target field the host projected.</summary>
    private static List<string>? IdsOf(JsonNode result, SemiJoinSlot slot)
    {
        var field = OwnerQueryBuilder.TargetFieldOf(slot);
        var values = new List<string>();

        foreach (var item in result["items"]!.AsArray())
        {
            if (item is not JsonObject row || !row.ContainsKey(field))
                return null;

            if (row[field] is { } value)
                values.Add(value is JsonValue scalar ? scalar.ToString() : value.ToJsonString());
        }

        return values;
    }

    // ---- by keys: the resolve, after the page -----------------------------------------------

    /// <summary>
    /// Resolves every keyed stage over the trimmed page rows (DESIGN §3.5.2): per row the keys of
    /// the selected cases (one per row, or one per element under <c>elements</c>), converted per
    /// <c>KeyAs</c>; at most <c>MaxResolveKeys</c> keys asked of owners for the whole request, the
    /// rest <c>RESOLVE_PARTIAL</c>; one owner query per target and key chunk, a remote target's
    /// batched per service at the owner's own batch cap and a local target's run by this host's own
    /// <see cref="SelfOwner"/>; the answers assigned per row by case and target order. A plain key
    /// match serves an entity target keyed by its own key, where no key can have two rows; every
    /// other target is asked grouped per key (<c>keyedBy</c>), at most <see cref="PerKey"/> rows per
    /// key, so a key with two rows is <c>ambiguous</c> and no key cuts another key's rows from the
    /// page. An owner answer with a next page is <c>RESOLVE_PARTIAL</c> for its chunk: the chunk's
    /// keys without rows are <c>owner_unanswered</c>, never <c>not_found</c>. A target with a filter
    /// under an effective <c>onMissing</c> other than <c>null</c> asks the keys its filtered query did
    /// not return once more without the filter (the existence probe): found there is
    /// <c>excluded</c>, found nowhere <c>not_found</c>. A <paramref name="strict"/> request reads no
    /// negative cache entry (DESIGN §3.5.6).
    /// </summary>
    public async Task<ResolveResult> ByKeysAsync(CompiledQuery compiled, IReadOnlyList<BsonDocument> rows, RequestContext context, TimeSpan remaining, bool strict, CancellationToken cancellationToken)
    {
        var organisation = context.Organisation!.Value;
        var diagnostics = new List<Diagnostic>();
        var budget = new KeyBudget(options.Limits.MaxResolveKeys);

        // The deadline is taken first: reading the owners' facts is part of the phase's time.
        var deadline = DateTime.UtcNow + (remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));

        // The owners' facts gate and size what is sent them, so they are read before the plan.
        await ReadOwnersAsync(compiled.KeyedResolves.SelectMany(CasesOf).SelectMany(selected => selected.Targets)
            .Where(target => target.IsRemote).Select(target => ServiceKeyOf(target.Declared.Entity)), remaining, cancellationToken).ConfigureAwait(false);
        var stages = compiled.KeyedResolves.Select(stage => Plan(compiled.Bound, stage, Continuation.Of(compiled.Bound, stage), rows, organisation, strict, budget, diagnostics)).ToList();
        var targets = stages.SelectMany(stage => stage.Targets).ToList();
        var cacheHits = targets.Sum(target => target.CacheHits);
        var calls = 0;

        // This host answers its own targets through its engine; a fetch built without one (the
        // obsolete RemoteResolver forwarder) cannot resolve a local target.
        if (self is null && targets.FirstOrDefault(target => target.Service == SelfService && target.Chunks.Count > 0) is { } local)
            return new ResolveResult
            {
                Refusal = Refusal.NotExecutable(Codes.ResolveUnavailable,
                    $"'{local.Stage.As}' resolves a target of this host, which this keyed fetch has no engine to ask; build it with the host's engine.", StageIndexOf(compiled.Bound, local.Stage)),
                CacheHits = cacheHits,
            };

        // An owner known to run an engine before 2.1 cannot bind what a chain sends it.
        if (Incapable(compiled.Bound, targets) is { } incapable)
            return new ResolveResult { Refusal = incapable, CacheHits = cacheHits };

        // Round one: the filtered owner queries of every chunk. A remote target of a union whose
        // owner refuses only select paths it lacks is asked again without them (a flat select).
        var pending = targets.SelectMany(target => target.Chunks.Select(chunk => new Sent(target, chunk, target.Query(chunk)))).ToList();
        var fromOwners = new List<OwnerDiagnostic>();

        for (var attempt = 0; pending.Count > 0; attempt++)
        {
            var retry = new List<Sent>();
            var round = await SendAsync(pending, context, deadline, cancellationToken).ConfigureAwait(false);

            foreach (var call in round)
            {
                calls += call.Service == SelfService ? 0 : call.Outcome.Calls;

                if (Failed(call, diagnostics))
                    continue;

                for (var index = 0; index < call.Sent.Count; index++)
                {
                    var (target, keys, query) = call.Sent[index];
                    var result = index < call.Outcome.Results.Count ? call.Outcome.Results[index] : null;
                    var stageIndex = StageIndexOf(compiled.Bound, target.Stage);

                    if (result is null || !Succeeded(result))
                    {
                        if (attempt < MaxDropRounds && target.DropUnknown(result, query))
                        {
                            retry.Add(new Sent(target, keys, target.Query(keys)));
                            continue;
                        }

                        return new ResolveResult { Refusal = Refused(result, target, call.Service, stageIndex), Calls = calls, CacheHits = cacheHits };
                    }

                    var items = result["items"]!.AsArray();

                    foreach (var item in items)
                    {
                        if (item is not JsonObject row || target.KeyOf(row) is not { } key)
                            return new ResolveResult { Refusal = WithoutKey(target.Entity, target.KeyWire, stageIndex), Calls = calls, CacheHits = cacheHits };

                        target.Hit(key, row);
                    }

                    if (HasNextPage(result))
                        Partial(call.Service, target, keys.Where(key => !target.Hits.ContainsKey(key)).ToList(), keys, diagnostics);

                    // What the owner reported about the continued stages comes back to the caller;
                    // the cache keeps rows, not reports, so such an answer is not kept.
                    if (!target.Continued.IsEmpty && result["diagnostics"] is JsonArray { Count: > 0 } reported)
                    {
                        fromOwners.AddRange(reported.OfType<JsonObject>().Select(diagnostic => new OwnerDiagnostic(target, call.Service, diagnostic, items)));
                        target.Uncached.UnionWith(keys);
                    }
                }
            }

            pending = retry;
        }

        // A flat select path is refused only when no target of the stage has it.
        foreach (var stage in stages)
            if (NoTargetHas(stage) is { } refusal)
                return new ResolveResult { Refusal = refusal, Calls = calls, CacheHits = cacheHits };

        // Round two: the existence probe of the keys a filtered query did not return.
        var probes = targets.Where(target => target.Probed).SelectMany(target => target.ProbeChunks(ChunkOf(target)).Select(chunk => new Sent(target, chunk, target.Probe(chunk)))).ToList();

        foreach (var call in probes.Count == 0 ? [] : await SendAsync(probes, context, deadline, cancellationToken).ConfigureAwait(false))
        {
            calls += call.Service == SelfService ? 0 : call.Outcome.Calls;

            if (Failed(call, diagnostics))
                continue;

            for (var index = 0; index < call.Sent.Count; index++)
            {
                var (target, keys, _) = call.Sent[index];
                var result = index < call.Outcome.Results.Count ? call.Outcome.Results[index] : null;
                var stageIndex = StageIndexOf(compiled.Bound, target.Stage);

                if (result is null || !Succeeded(result))
                    return new ResolveResult { Refusal = Refused(result, target, call.Service, stageIndex), Calls = calls, CacheHits = cacheHits };

                foreach (var item in result["items"]!.AsArray())
                {
                    if (item is not JsonObject row || target.KeyOf(row) is not { } key)
                        return new ResolveResult { Refusal = WithoutKey(target.Entity, target.KeyWire, stageIndex), Calls = calls, CacheHits = cacheHits };

                    target.Excluded.Add(key);
                }

                if (HasNextPage(result))
                    Partial(call.Service, target, keys.Where(key => !target.Excluded.Contains(key)).ToList(), keys, diagnostics);
            }
        }

        // What the owners answered in full is kept for the next page, and so is what an owner said a
        // union target lacks.
        foreach (var target in targets)
        {
            foreach (var key in target.Chunks.SelectMany(chunk => chunk))
                if (!target.Unanswered.Contains(key) && !target.Uncached.Contains(key))
                    cache.Set(target.CacheKey(key), target.Cached(key));

            if (target.DroppedSelect.Count > 0 || target.DroppedParent.Count > 0)
                cache.SetDrops(target.DropsKey, target.DroppedSelect, target.DroppedParent);
        }

        var perRow = rows.Select(_ => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)).ToList();
        var outcomes = new List<KeyedRowOutcome>();
        var truncations = new List<KeyedTruncation>();

        var liftedRows = new Dictionary<JsonObject, List<int>>(ReferenceEqualityComparer.Instance);

        foreach (var stage in stages)
            Assign(stage, perRow, outcomes, truncations, liftedRows);

        diagnostics.AddRange(MapOwnerDiagnostics(fromOwners, liftedRows));

        var dropped = targets
            .SelectMany(target => target.DroppedSelect.Select(path => (Target: target, Path: path, Parent: false))
                .Concat(target.DroppedParent.Select(path => (Target: target, Path: path, Parent: true))))
            .Select(drop => new DroppedSelectPath(StageIndexOf(compiled.Bound, drop.Target.Stage), drop.Target.Stage.As, drop.Target.TargetName, drop.Path, drop.Parent))
            .ToList();

        return new ResolveResult { Rows = perRow, Diagnostics = diagnostics, Outcomes = outcomes, Truncations = truncations, Dropped = dropped, Calls = calls, CacheHits = cacheHits };
    }

    /// <summary>One owner query of a round: the target it asks for, the keys it carries, and the query.</summary>
    private sealed record Sent(TargetPlan Target, IReadOnlyList<string> Keys, QueryRequest Query);

    /// <summary>The queries one owner was sent in a round, and what came back.</summary>
    private sealed record OwnerCall(string Service, IReadOnlyList<Sent> Sent, CallOutcome Outcome);

    /// <summary>
    /// Sends a round's queries, batched per owner, owners in parallel; a local target's to this host's
    /// own <see cref="SelfOwner"/>. An owner's batch carries a chain (continued stages, or a 2.1 target
    /// form) under the chain ceiling, any other under the per-resolve one (DESIGN §3.5.4).
    /// </summary>
    private async Task<IReadOnlyList<OwnerCall>> SendAsync(IReadOnlyList<Sent> sends, RequestContext context, DateTime deadline, CancellationToken cancellationToken)
    {
        var tasks = sends.GroupBy(sent => sent.Target.Service, StringComparer.Ordinal).Select(async group =>
        {
            var sent = group.ToList();
            var owner = group.Key == SelfService ? new SelfOwner(self!, context) : client!;

            return new OwnerCall(group.Key, sent, await CallInBatchesAsync(owner, group.Key, sent.Select(entry => entry.Query).ToList(), deadline, sent.Any(entry => entry.Target.Chain), cancellationToken).ConfigureAwait(false));
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return tasks.Select(task => task.Result).ToList();
    }

    /// <summary>
    /// An owner that did not answer a round: <c>RESOLVE_TIMEOUT</c> or <c>RESOLVE_UNREACHABLE</c>
    /// naming the aliases, and every key it was sent unanswered. True when the call failed.
    /// </summary>
    private bool Failed(OwnerCall call, List<Diagnostic> diagnostics)
    {
        if (call.Outcome.Failure is not { } failure)
            return false;

        var aliases = call.Sent.Select(entry => entry.Target.Stage.As).Distinct().ToList();
        var owner = call.Service == SelfService ? "This host" : $"The owner of '{call.Service}'";

        diagnostics.Add(new Diagnostic
        {
            Code = failure == Failure.Timeout ? Codes.ResolveTimeout : Codes.ResolveUnreachable,
            Message = failure == Failure.Timeout
                ? $"{owner} did not answer within {(int)Math.Round(call.Outcome.Budget.TotalMilliseconds)} ms; '{string.Join("', '", aliases)}' is null on this page."
                : call.Outcome.Status is { } status
                    ? $"{owner} answered with HTTP {status}; '{string.Join("', '", aliases)}' is null on this page."
                    : $"{owner} could not be reached; '{string.Join("', '", aliases)}' is null on this page.",
            Params = new Dictionary<string, object?> { ["service"] = call.Service == SelfService ? null : call.Service, ["aliases"] = aliases },
        });

        foreach (var (target, keys, _) in call.Sent)
            target.Unanswered.UnionWith(keys);

        return true;
    }

    /// <summary>
    /// An owner answer with a next page (DESIGN §3.5.2 step 3): the owner cut rows, so the chunk's
    /// <paramref name="open"/> keys are <c>owner_unanswered</c>, never <c>not_found</c>, and none of
    /// the chunk's <paramref name="keys"/> is cached; <c>RESOLVE_PARTIAL</c> names the alias.
    /// </summary>
    private static void Partial(string service, TargetPlan target, IReadOnlyList<string> open, IReadOnlyList<string> keys, List<Diagnostic> diagnostics)
    {
        target.Unanswered.UnionWith(open);
        target.Uncached.UnionWith(keys);

        var owner = service == SelfService ? "This host" : $"The owner of '{service}'";

        diagnostics.Add(new Diagnostic
        {
            Code = Codes.ResolvePartial,
            Message = $"{owner} answered '{target.Stage.As}' with more rows than one page of '{target.Entity}' holds; {open.Count} of its {keys.Count} keys are not resolved on this page.",
            Params = new Dictionary<string, object?> { ["alias"] = target.Stage.As, ["keys"] = keys.Count, ["unanswered"] = open.Count, ["service"] = service == SelfService ? null : service },
        });
    }

    /// <summary>Whether an owner answer says a next page exists: rows it did not return.</summary>
    private static bool HasNextPage(JsonNode result) =>
        result["pageInfo"]?["hasNextPage"] is JsonValue next && next.TryGetValue<bool>(out var more) && more;

    /// <summary>The owner key of this host's own targets in a call plan; no service namespace is empty.</summary>
    private const string SelfService = "";

    /// <summary>How many times a union target's query is asked again without select paths its owner lacks.</summary>
    private const int MaxDropRounds = 2;

    /// <summary>A diagnostic an owner reported with its answer for a target that carried continued stages, and the answer's rows.</summary>
    private sealed record OwnerDiagnostic(TargetPlan Target, string Service, JsonObject Reported, JsonArray Items);

    /// <summary>
    /// A flat select path no target of a union has (DESIGN §3.4.1): refused. A local target lacks
    /// what it dropped at bind time, a remote one what its owner refused; a remote target not asked
    /// on this page is taken to have every path.
    /// </summary>
    private static Refusal? NoTargetHas(StagePlan plan)
    {
        if (!plan.Targets.Any(target => target.Target.IsRemote && target.Union))
            return null;

        foreach (var parent in new[] { false, true })
        {
            IReadOnlyCollection<string> Lacks(TargetPlan target) => target.Target.IsRemote
                ? parent ? target.DroppedParent : target.DroppedSelect
                : parent ? target.Target.DroppedParentSelect ?? [] : target.Target.DroppedSelect;

            foreach (var path in plan.Targets.SelectMany(Lacks).Distinct(StringComparer.Ordinal).ToList())
            {
                if (!plan.Targets.All(target => Lacks(target).Contains(path, StringComparer.Ordinal)))
                    continue;

                var message = $"'{path}' is not a path of any {(parent ? "row that owns a target" : "target")} of '{plan.Stage.As}' ({string.Join(", ", plan.Targets.Select(target => target.TargetName).Distinct(StringComparer.Ordinal))}).";
                var head = new QueryValidationError { Code = Codes.ResolveRefused, Message = message, Stage = plan.Index };

                return Refusal.NotExecutable(Codes.ResolveRefused, message, plan.Index,
                    [head, new QueryValidationError { Code = Codes.UnknownPath, Message = message, Stage = plan.Index, Path = path }]);
            }
        }

        return null;
    }

    /// <summary>
    /// The owners' diagnostics about continued stages as this host's (DESIGN §3.5.3): each at the
    /// caller's stage and path with <c>params.owner</c> saying where the owner saw it, its rows moved
    /// from the owner's answer rows to the page rows that took them, one diagnostic per code, stage and
    /// alias however many owners and chunks reported it, its rows capped at <c>MaxReportedRows</c>. A
    /// diagnostic that names no continued stage or alias stays the owner's.
    /// </summary>
    private List<Diagnostic> MapOwnerDiagnostics(IReadOnlyList<OwnerDiagnostic> reported, Dictionary<JsonObject, List<int>> liftedRows)
    {
        var merged = new List<(string Key, Diagnostic First, Dictionary<string, object?> Params, List<Dictionary<string, object?>>? Rows, long Count, long? RowCount, bool Truncated)>();

        foreach (var (target, service, diagnostic, items) in reported)
        {
            var ownerStage = diagnostic["stage"] is JsonValue at && at.TryGetValue<int>(out var number) ? number : (int?)null;
            var ownerPath = diagnostic["path"]?.ToString();
            var written = diagnostic["params"] as JsonObject;
            var aliases = new List<string>();

            if (written?["alias"] is JsonValue alias && alias.TryGetValue<string>(out var name))
                aliases.Add(name);

            if (written?["aliases"] is JsonArray named)
                aliases.AddRange(named.OfType<JsonValue>().Select(value => value.ToString()));

            if ((target.OriginOf(ownerStage) ?? target.Continued.Origins.FirstOrDefault(origin => origin.Aliases.Any(aliases.Contains))) is not { } continued)
                continue;

            var parameters = written?.Where(pair => pair.Key is not ("rows" or "count" or "truncated"))
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone(), StringComparer.Ordinal) ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            parameters["owner"] = new Dictionary<string, object?>
            {
                ["service"] = service == SelfService ? null : service,
                ["entity"] = target.Entity,
                ["target"] = target.TargetName,
                ["stage"] = ownerStage,
                ["path"] = ownerPath,
            };

            List<Dictionary<string, object?>>? rows = null;
            long? rowCount = null;
            var count = written?["count"] is JsonValue counted && counted.TryGetValue<long>(out var total) ? total : 0;
            var truncated = written?["truncated"] is JsonValue flag && flag.TryGetValue<bool>(out var cut) && cut;

            if (written?["rows"] is JsonArray entries)
            {
                rows = [];

                foreach (var entry in entries.OfType<JsonObject>())
                {
                    if (entry["row"] is not JsonValue row || !row.TryGetValue<int>(out var ownerRow) || ownerRow < 0 || ownerRow >= items.Count
                        || items[ownerRow] is not JsonObject answer || !liftedRows.TryGetValue(answer, out var pageRows))
                        continue;

                    foreach (var pageRow in pageRows)
                    {
                        var mapped = new Dictionary<string, object?>(StringComparer.Ordinal) { ["row"] = pageRow };

                        foreach (var (key, value) in entry)
                            if (key != "row")
                                mapped[key] = value?.DeepClone();

                        rows.Add(mapped);
                    }
                }

                // What the owner counted beyond the rows it listed stays counted.
                count = rows.Count + Math.Max(0, count - entries.Count);
            }
            else if (written?["rows"] is JsonValue listed && listed.TryGetValue<long>(out var affected))
            {
                rowCount = affected;
            }

            var mergeKey = $"{diagnostic["code"]}|{continued.OriginIndex}|{string.Join(",", aliases)}";
            var index = merged.FindIndex(entry => entry.Key == mergeKey);

            if (index < 0)
            {
                merged.Add((mergeKey, new Diagnostic
                {
                    Code = diagnostic["code"]?.ToString() ?? Codes.InternalError,
                    Message = diagnostic["message"]?.ToString() ?? "",
                    Stage = continued.OriginIndex,
                    Path = ownerPath is null ? null : Continuation.ToOrigin(ownerPath, continued, target.Stage, target.Target.Declared.Item is not null),
                }, parameters, rows, count, rowCount, truncated));
                continue;
            }

            var existing = merged[index];

            existing.Rows?.AddRange(rows ?? []);
            merged[index] = existing with
            {
                Count = existing.Count + count,
                RowCount = existing.RowCount is null && rowCount is null ? null : (existing.RowCount ?? 0) + (rowCount ?? 0),
                Truncated = existing.Truncated || truncated,
            };
        }

        var cap = Math.Max(1, options.Limits.MaxReportedRows);

        return merged.Select(entry =>
        {
            var parameters = new Dictionary<string, object?>(entry.Params, StringComparer.Ordinal);

            if (entry.Rows is not null)
            {
                var rows = entry.Rows.OrderBy(row => (int)row["row"]!).ThenBy(row => row.TryGetValue("element", out var element) && element is JsonValue value && value.TryGetValue<int>(out var at) ? at : -1).ToList();

                parameters["count"] = entry.Count;
                parameters["truncated"] = entry.Truncated || rows.Count > cap;
                parameters["rows"] = rows.Take(cap).ToList();
            }
            else if (entry.RowCount is { } affected)
            {
                parameters["rows"] = affected;
            }

            return entry.First with { Params = parameters };
        }).ToList();
    }

    /// <summary>The rows a grouped owner query returns per key at most: two tell a resolved key from an ambiguous one.</summary>
    public const int PerKey = 2;

    /// <summary>The placeholder an explained owner query carries where the page's keys would be (DESIGN §4.3 "keys elided").</summary>
    public const string ElidedKey = "…";

    /// <summary>
    /// The owner queries a keyed stage would send, one per distinct target, as explain shows them
    /// (DESIGN §4.3 <c>owner</c>): built by the very plan a run builds, the page's keys elided to
    /// <see cref="ElidedKey"/>, with the continued stages that target's owner runs and those it does
    /// not (<c>not_applicable</c> for its rows). Nothing is sent and nothing is cached.
    /// </summary>
    public static IReadOnlyList<ExplainedOwnerQuery> Explain(BoundPipeline bound, BoundStage.Resolve stage, bool strict) => Explain(bound, stage, strict, null);

    /// <summary><see cref="Explain(BoundPipeline, BoundStage.Resolve, bool)"/>, with the remote client whose owner facts a run plans by (a known pre-2.1 owner is asked the plain query).</summary>
    public static IReadOnlyList<ExplainedOwnerQuery> Explain(BoundPipeline bound, BoundStage.Resolve stage, bool strict, IRemoteQueryClient? client)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(stage);

        var continued = Continuation.Of(bound, stage);

        return PlansOf(bound, stage, strict, continued, client).Select(plan => new ExplainedOwnerQuery(
            plan.TargetName,
            plan.Target.IsRemote,
            plan.Target.Declared.Entity.Split('.')[0],
            plan.Grouped,
            plan.Query([ElidedKey]),
            plan.Continued.Origins,
            continued.Where(other => !plan.Continued.Origins.Any(origin => ReferenceEquals(origin, other))).ToList())).ToList();
    }

    /// <summary>The key an owner query checked at its owner's internal explain carries in place of the page's keys: of a key's form, naming nothing.</summary>
    public const string CheckKey = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// The owner queries of a keyed stage that a remote owner continues (DESIGN §4.3 remote check):
    /// per remote target with continued stages (and per local target with continued stages, service
    /// empty: this host checks them in process), its service, the query a run sends with one
    /// <see cref="CheckKey"/> in place of the page's keys, and how an owner error maps back — one at
    /// a continued stage to the caller's stage and path with <c>params.owner</c>, as a run maps a
    /// refusal; one at the owner query's own stages to null, since those are this host's making.
    /// Nothing is sent here.
    /// </summary>
    public static IReadOnlyList<OwnerCheck> Checks(BoundPipeline bound, BoundStage.Resolve stage, bool strict) => Checks(bound, stage, strict, null);

    /// <summary><see cref="Checks(BoundPipeline, BoundStage.Resolve, bool)"/>, planned by the remote client's owner facts as a run plans.</summary>
    public static IReadOnlyList<OwnerCheck> Checks(BoundPipeline bound, BoundStage.Resolve stage, bool strict, IRemoteQueryClient? client)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(stage);

        var index = StageIndexOf(bound, stage);
        var position = PositionOf(bound, stage);
        var projected = ProjectedUnder(bound, position, stage.As) ?? [];
        var parentProjected = stage.ParentAs is { } parentAs ? ProjectedUnder(bound, position, parentAs) ?? [] : [];
        var project = position is { } at ? bound.Stages.Skip(at + 1).OfType<BoundStage.Project>().LastOrDefault() : null;
        var projectStage = project is null || (projected.Count == 0 && parentProjected.Count == 0) ? null : StageIndexOf(bound, project);

        // A local target is checked only for the stages continued under it: this host binds its select
        // and paths itself, but the continued stages are bound by its own SelfOwner when the query runs,
        // with its own model and, for a join there that reaches another service, that owner's.
        return PlansOf(bound, stage, strict, Continuation.Of(bound, stage), client)
            .Where(plan => plan.Target.IsRemote
                ? !plan.Continued.IsEmpty || Writes(plan.Target) || projected.Count > 0 || parentProjected.Count > 0 || stage.RemoteLookup is not null
                : !plan.Continued.IsEmpty)
            .Select(plan =>
            {
                var query = plan.Query([CheckKey]);
                var item = plan.Target.Declared.Item is not null;
                var projectAt = query.Pipeline.ToList().FindLastIndex(each => each.Project is not null);

                // The paths the projection names under the aliases are asked too, so the owner says
                // which of them this target lacks; a run sends only those under the select.
                if (projectAt >= 0)
                {
                    var fields = new Dictionary<string, int>(query.Pipeline[projectAt].Project!.Fields, StringComparer.Ordinal);

                    foreach (var path in projected)
                        fields.TryAdd(item ? BoundKeyedBy.Element + "." + path : path, 1);

                    foreach (var path in item ? parentProjected : [])
                        fields.TryAdd(path, 1);

                    var pipeline = query.Pipeline.ToList();
                    pipeline[projectAt] = pipeline[projectAt] with { Project = pipeline[projectAt].Project! with { Fields = fields } };
                    query = query with { Pipeline = pipeline };
                }

                return new OwnerCheck(plan.TargetName, plan.Service, query, plan.Continued.IsEmpty ? index ?? 0 : plan.Continued.Origins[0].OriginIndex, error => MapBack(error, plan, plan.Service))
                {
                    Resolve = stage,
                    Bound = plan.Target,
                    Stage = index,
                    ProjectStage = projectStage,
                    ProjectAt = projectAt,
                    Item = item,
                };
            })
            .ToList();

        static bool Writes(BoundResolveTarget target) =>
            target.RemoteSelect is { Count: > 0 } || target.RemoteParentSelect is { Count: > 0 };
    }

    /// <summary>One plan per distinct target (entity, field, item) of a keyed stage, as a run builds them.</summary>
    private static List<TargetPlan> PlansOf(BoundPipeline bound, BoundStage.Resolve stage, bool strict, IReadOnlyList<ContinuedStage> continued, IRemoteQueryClient? client = null)
    {
        var position = PositionOf(bound, stage);
        var cases = CasesOf(stage);
        var union = cases.SelectMany(selected => selected.Targets).Select(target => target.Declared.Entity).Distinct(StringComparer.Ordinal).Count() > 1;
        var projected = ProjectedUnder(bound, position, stage.As);
        var parentProjected = stage.ParentAs is { } parentAs ? ProjectedUnder(bound, position, parentAs) : null;
        var plans = new List<TargetPlan>();

        foreach (var target in cases.SelectMany(selected => selected.Targets))
            if (!plans.Any(other => other.Entity == target.Declared.Entity && other.Target.Declared.Field == target.Declared.Field && other.Target.Declared.Item == target.Declared.Item))
                plans.Add(new TargetPlan(stage, target, continued, bound.Organisation, strict, union, projected, parentProjected,
                    legacyOwner: target.IsRemote && IsBefore21(client, ServiceKeyOf(target.Declared.Entity))));

        return plans;
    }

    /// <summary>One keyed stage over the page: its targets, the stages continued under it, and per row the keys its cases select.</summary>
    private sealed class StagePlan(BoundStage.Resolve stage, int? index, IReadOnlyList<ContinuedStage> continued)
    {
        public BoundStage.Resolve Stage { get; } = stage;

        /// <summary>The caller's stage index, for the outcomes.</summary>
        public int? Index { get; } = index;

        /// <summary>Every stage continued under the stage's aliases, whichever target it applies to.</summary>
        public IReadOnlyList<ContinuedStage> Continued { get; } = continued;

        public List<TargetPlan> Targets { get; } = [];

        /// <summary>Per bound target, its plan; targets of different cases with the same entity, field and item share one.</summary>
        public Dictionary<BoundResolveTarget, TargetPlan> ByTarget { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Per row its slots: one for a single value, one per element under <c>elements</c>; empty when there is nothing to resolve.</summary>
        public List<List<Slot>> Rows { get; } = [];
    }

    /// <summary>One reference value of a row: the case it selects, its key, or the outcome it already has without a call.</summary>
    private sealed record Slot(BoundResolveCase? Case, string? Key, KeyedOutcome? Outcome);

    /// <summary>
    /// One target of a keyed stage: the keys it is asked for, what the cache and the owner answered
    /// per key (up to <see cref="PerKey"/> rows, first by record key), the keys the existence probe
    /// found although the filter left them out, and the keys the owner did not answer. The stages
    /// continued under the keyed stage for this target ride in its owner query (DESIGN §3.5.3).
    /// </summary>
    private sealed class TargetPlan
    {
        private readonly Guid organisation;
        private readonly bool strict;
        private readonly string planHash;
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> lifted;
        private readonly IReadOnlyList<string>? projected;
        private readonly IReadOnlyList<string>? parentProjected;

        public TargetPlan(BoundStage.Resolve stage, BoundResolveTarget target, IReadOnlyList<ContinuedStage> continued, Guid organisation, bool strict,
            bool union = false, IReadOnlyList<string>? projected = null, IReadOnlyList<string>? parentProjected = null, bool legacyOwner = false)
        {
            Stage = stage;
            Target = target;
            this.organisation = organisation;
            this.strict = strict;
            this.projected = projected;
            this.parentProjected = parentProjected;
            Union = union;
            Continued = Continuation.For(stage, target.Declared.Entity, target.Declared.Item is not null, continued);
            lifted = new HashSet<string>(Continued.Aliases, StringComparer.Ordinal);

            // An entity keyed by its own key has one row per key, which a plain key match serves;
            // every other target is grouped per key. A plain 2.0 resolve keeps the plain query it
            // always sent (DESIGN §3.5.7, §3.5.8): its owner may still be on 2.0, or reached over
            // a route that does not take keyedBy. A request that reads the outcomes (strict, or an
            // onMissing other than null) is 2.1, and a plain query onto a member that is not the key
            // sees a second row only when the owner's page happens to hold it, so such a target is
            // grouped too: its ambiguity then depends neither on the page nor on the cache.
            // An owner known to run an engine before 2.1 takes no keyedBy; it is asked the plain query
            // with two rows per key instead, so a second row arrives or the answer has a next page.
            var nonKey = target.Declared.Item is not null || !target.Declared.FieldIsKey;
            var readsOutcomes = strict || stage.EffectiveOnMissing != ResolveOnMissing.Null;

            Grouped = nonKey && (stage.NeedsKeyedFetch || (readsOutcomes && !legacyOwner));
            PlainRowsPerKey = !Grouped && nonKey && readsOutcomes ? PerKey : 1;
            Service = target.IsRemote ? ServiceKeyOf(target.Declared.Entity) : SelfService;

            // The probe tells a key the filter left out from a missing one, which only a stage
            // that reports or refuses missing references needs (DESIGN §3.5.2 step 3).
            Probed = target.RemoteFilter is { ValueKind: JsonValueKind.Object } && stage.EffectiveOnMissing != ResolveOnMissing.Null;

            // The plan: the owner query as sent, without its keys (DESIGN §3.5.6).
            planHash = OwnerFetchCache.PlanHashOf(Query([]), Probed);
        }

        public BoundStage.Resolve Stage { get; }

        public BoundResolveTarget Target { get; }

        /// <summary>The stages continued under the keyed stage that this target's owner runs.</summary>
        public OwnerContinuation Continued { get; }

        /// <summary>
        /// Whether the owner query carries a chain: continued stages, or a target form of 2.1 (typed,
        /// item, converted, element-wise); it runs under the chain ceiling.
        /// </summary>
        public bool Chain => !Continued.IsEmpty || Stage.NeedsKeyedFetch;

        /// <summary>Whether the owner must run 2.1 to take the query: continued stages and <c>keyedBy</c> are 2.1 vocabulary.</summary>
        public bool NeedsOwner21 => !Continued.IsEmpty || Grouped;

        /// <summary>The owner's stage index of the first continued stage: where the query as built had its projection.</summary>
        public int ContinuedAt { get; private set; }

        public bool Grouped { get; }

        /// <summary>The rows per key a plain query's page holds: two for a non-key target whose outcomes are read at an owner before 2.1, else one.</summary>
        public int PlainRowsPerKey { get; }

        /// <summary>Whether the keys the filtered query does not return are probed without the filter.</summary>
        public bool Probed { get; }

        /// <summary>The service the target's owner query goes to; <see cref="SelfService"/> for a local target.</summary>
        public string Service { get; }

        public string Entity => Target.Declared.Entity;

        /// <summary>The target as a note or an owner mapping names it: the entity, or <c>entity#item</c>.</summary>
        public string TargetName => Target.Declared.Item is { } item ? $"{Entity}#{item}" : Entity;

        /// <summary>The member of an answer row the rows are keyed by, as the owner writes it.</summary>
        public string KeyWire => Stage.RemoteLookup is { Rows: true } ? BoundKeyedBy.Key
            : Target.Declared.Item is null ? Target.Declared.Field : BoundKeyedBy.Element + "." + Target.Declared.Field;

        /// <summary>
        /// The rows a grouped owner query answers per key: <see cref="PerKey"/> for a resolve, which tells one
        /// record from several; a remote lookup's own (one under <c>first</c>, else one more than its limit).
        /// </summary>
        public int RowsPerKey => Stage.RemoteLookup?.PerKey ?? PerKey;

        /// <summary>Whether the keys are guids, compared in their normalised form.</summary>
        public bool GuidKeys { get; set; }

        public Dictionary<string, List<JsonObject>> Hits { get; } = new(StringComparer.Ordinal);

        /// <summary>The keys the filter left out and the probe found: <c>excluded</c>.</summary>
        public HashSet<string> Excluded { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unanswered { get; } = new(StringComparer.Ordinal);

        /// <summary>The keys of a chunk the owner answered only in part; their answers are not kept.</summary>
        public HashSet<string> Uncached { get; } = new(StringComparer.Ordinal);

        public List<string> Misses { get; } = [];

        public List<IReadOnlyList<string>> Chunks { get; } = [];

        public int CacheHits { get; private set; }

        public string CacheKey(string key) => OwnerFetchCache.KeyOf(Entity, Target.Declared.Item, Target.Declared.Field, organisation, key, planHash);

        /// <summary>
        /// The owner query of some keys: the key match, the target's filter, the continued stages with
        /// every alias they add projected, the projection and the page. A query with continued stages
        /// carries <c>strict</c> when the request is strict, so the owner refuses the chain's data loss
        /// as this host would (DESIGN §3.5.4); without them strict changes nothing the owner does.
        /// </summary>
        public QueryRequest Query(IReadOnlyList<string> keys)
        {
            var query = OwnerQueryBuilder.ByKeys(Stage, Sending(), keys, Grouped ? RowsPerKey : null, plainRowsPerKey: PlainRowsPerKey);

            if (Continued.IsEmpty)
                return query;

            var pipeline = query.Pipeline.ToList();
            var at = pipeline.FindLastIndex(stage => stage.Project is not null);
            var projection = new Dictionary<string, int>(pipeline[at].Project!.Fields, StringComparer.Ordinal);

            foreach (var alias in Continued.Aliases)
                projection[alias] = 1;

            pipeline[at] = pipeline[at] with { Project = pipeline[at].Project! with { Fields = projection } };
            pipeline.InsertRange(at, Continued.Stages);
            ContinuedAt = at;

            return query with { Pipeline = pipeline, Strict = strict ? true : null };
        }

        /// <summary>The continued stage an owner's stage index names, or null for a stage of the query itself.</summary>
        public ContinuedStage? OriginOf(int? ownerStage) =>
            ownerStage is { } index && index >= ContinuedAt && index - ContinuedAt < Continued.Origins.Count ? Continued.Origins[index - ContinuedAt] : null;

        /// <summary>What a continued stage added to an owner row, lifted to the origin row (DESIGN §3.5.2 step 6).</summary>
        public static JsonNode? Lifted(JsonObject row, string alias) =>
            row.TryGetPropertyValue(alias, out var value) ? value?.DeepClone() : null;

        /// <summary>The existence probe of some keys: the query without the filter, one row per key is enough.</summary>
        public QueryRequest Probe(IReadOnlyList<string> keys) => OwnerQueryBuilder.ByKeys(Stage, Sending(), keys, Grouped ? 1 : null, probe: true, plainRowsPerKey: PlainRowsPerKey);

        /// <summary>Whether the keyed stage has more than one target entity: a flat select path one of them lacks is dropped for it.</summary>
        public bool Union { get; }

        /// <summary>The select paths the owner said this remote target lacks, dropped from its query (DESIGN §3.4.1 flat select).</summary>
        public HashSet<string> DroppedSelect { get; } = new(StringComparer.Ordinal);

        /// <summary>The owning-row select paths the owner said this remote target's entity lacks.</summary>
        public HashSet<string> DroppedParent { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The target as its owner query carries it: the select and the owning row's select narrowed
        /// to what a projection after the stage keeps of the alias (DESIGN §3.5.3), less what the owner
        /// said this target lacks. A remote target sends them as written, a local one as bound.
        /// </summary>
        private BoundResolveTarget Sending()
        {
            var parent = Narrow(Target.RemoteParentSelect, parentProjected)?.Where(path => !DroppedParent.Contains(path)).ToList();

            if (Target.IsRemote)
                return Target with
                {
                    RemoteSelect = Narrow(Target.RemoteSelect, projected)?.Where(path => !DroppedSelect.Contains(path)).ToList(),
                    RemoteParentSelect = parent,
                };

            if (Target.Select is not { Count: > 0 } bound || Narrow(bound.Select(path => path.Wire).ToList(), projected) is not { } narrowed)
                return Target with { RemoteParentSelect = parent };

            return Target with { Select = bound.Where(path => narrowed.Contains(path.Wire, StringComparer.Ordinal)).ToList(), RemoteParentSelect = parent };
        }

        /// <summary>
        /// A select narrowed to the paths a projection keeps under the alias: a projected path at or
        /// under a selected one is sent, a selected one under a projected one stays. Without a select, a
        /// projection, or anything left, the select as it is.
        /// </summary>
        private static IReadOnlyList<string>? Narrow(IReadOnlyList<string>? select, IReadOnlyList<string>? kept)
        {
            if (select is not { Count: > 0 } || kept is not { Count: > 0 })
                return select;

            var narrowed = new List<string>();

            foreach (var path in select)
            {
                foreach (var wanted in kept.Where(wanted => wanted == path || wanted.StartsWith(path + ".", StringComparison.Ordinal)))
                    if (!narrowed.Contains(wanted, StringComparer.Ordinal))
                        narrowed.Add(wanted);

                if (kept.Any(wanted => path.StartsWith(wanted + ".", StringComparison.Ordinal)) && !narrowed.Contains(path, StringComparer.Ordinal))
                    narrowed.Add(path);
            }

            return narrowed.Count == 0 ? select : narrowed;
        }

        /// <summary>
        /// A remote union target's owner refused its query only because select paths are not paths of
        /// this target: they are dropped for it and the query is asked again. Anything else in the
        /// refusal is the refusal. True when the query as sent projected every refused path, so the
        /// query asked again does not: newly learned, or learned from another chunk of the same round
        /// that was sent before the drop (RE-5).
        /// </summary>
        public bool DropUnknown(JsonNode? result, QueryRequest sent)
        {
            if (!Target.IsRemote || !Union || result?["errors"] is not JsonArray { Count: > 0 } errors)
                return false;

            var projectAt = sent.Pipeline.ToList().FindLastIndex(stage => stage.Project is not null);

            if (projectAt < 0)
                return false;

            var sentFields = sent.Pipeline[projectAt].Project!.Fields;
            var element = BoundKeyedBy.Element + ".";
            var select = new List<string>();
            var parent = new List<string>();

            foreach (var error in errors)
            {
                if (error?["code"]?.ToString() != Codes.UnknownPath
                    || error["stage"] is not JsonValue at || !at.TryGetValue<int>(out var stage) || stage != projectAt
                    || error["path"]?.ToString() is not { Length: > 0 } path)
                    return false;

                if (Target.Declared.Item is null)
                    select.Add(path);
                else if (path.StartsWith(element, StringComparison.Ordinal))
                    select.Add(path[element.Length..]);
                else
                    parent.Add(path);
            }

            var sentSelect = Narrow(Target.RemoteSelect, projected) ?? [];
            var sentParent = Narrow(Target.RemoteParentSelect, parentProjected) ?? [];

            if (!select.All(path => sentSelect.Contains(path, StringComparer.Ordinal)) || !parent.All(path => sentParent.Contains(path, StringComparer.Ordinal)))
                return false;

            // The query asked again leaves every refused path out, so asking again cannot loop.
            if (!select.All(path => sentFields.ContainsKey(Target.Declared.Item is null ? path : element + path)) || !parent.All(sentFields.ContainsKey))
                return false;

            DroppedSelect.UnionWith(select);
            DroppedParent.UnionWith(parent);

            return true;
        }

        /// <summary>The cache key of the paths an owner said this target lacks, per organisation, service and plan.</summary>
        public string DropsKey => OwnerFetchCache.DropsKeyOf(organisation, Service, planHash);

        /// <summary>Takes the paths an owner said this target lacks, learned by an earlier request: they are not sent again.</summary>
        public void Learned(IEnumerable<string> select, IEnumerable<string> parent)
        {
            DroppedSelect.UnionWith(select);
            DroppedParent.UnionWith(parent);
        }

        /// <summary>The keys the filtered query was sent and answered without a row, in chunks of <paramref name="size"/>.</summary>
        public IEnumerable<IReadOnlyList<string>> ProbeChunks(int size)
        {
            var open = Chunks.SelectMany(chunk => chunk).Where(key => !Hits.ContainsKey(key) && !Unanswered.Contains(key)).ToList();

            for (var start = 0; start < open.Count; start += size)
                yield return open.Skip(start).Take(size).ToList();
        }

        /// <summary>Asks for a key once: from the cache when it holds the answer, otherwise from the owner.</summary>
        public void Want(string key, OwnerFetchCache cache)
        {
            if (!seen.Add(key))
                return;

            if (!cache.TryGet(CacheKey(key), strict, out var cached) || cached is null)
            {
                Misses.Add(key);
                return;
            }

            CacheHits++;

            if (cached.Rows.Count > 0)
                Hits[key] = cached.Rows.ToList();

            if (cached.Excluded)
                Excluded.Add(key);
        }

        /// <summary>
        /// What the cache keeps for a key: its rows, up to <see cref="PerKey"/> as they arrived, so a
        /// repeat answers the ambiguity the first answer saw; or whether it was excluded.
        /// </summary>
        public OwnerAnswer Cached(string key)
        {
            Hits.TryGetValue(key, out var rows);

            return new OwnerAnswer((rows ?? []).Take(RowsPerKey).ToList(), rows is not { Count: > 0 } && Excluded.Contains(key));
        }

        public void Hit(string key, JsonObject row)
        {
            if (!Hits.TryGetValue(key, out var rows))
                Hits[key] = rows = [];

            if (rows.Count < RowsPerKey)
                rows.Add(row);
        }

        /// <summary>The key of an answer row, or null when the row does not carry the member it was keyed by.</summary>
        public string? KeyOf(JsonObject row)
        {
            JsonNode? at = row;

            foreach (var segment in KeyWire.Split('.'))
            {
                if (at is not JsonObject inner || !inner.TryGetPropertyValue(segment, out var next))
                    return null;

                at = next;
            }

            var text = at switch
            {
                null => "",
                JsonValue scalar => scalar.ToString(),
                _ => at.ToJsonString(),
            };

            return GuidKeys && Guid.TryParse(text, out var guid) ? guid.ToString("D") : text;
        }

        /// <summary>An answer row as the alias holds it: the element of an item target, the row of an entity target without what continued stages added.</summary>
        public JsonNode? AliasOf(JsonObject row)
        {
            if (Target.Declared.Item is not null)
                return row[BoundKeyedBy.Element]?.DeepClone();

            var keyed = Stage.RemoteLookup is { Rows: true };

            if (lifted.Count == 0 && !keyed)
                return row.DeepClone();

            var alias = new JsonObject();

            // A whole row answered per key carries that key for this host alone.
            foreach (var (name, value) in row)
                if (!lifted.Contains(name) && !(keyed && name == BoundKeyedBy.Key))
                    alias[name] = value?.DeepClone();

            return alias;
        }

        /// <summary>The owning row of an item target's answer: its entity and the parent members the owner projected.</summary>
        public JsonObject ParentOf(JsonObject row)
        {
            var parent = new JsonObject { ["entity"] = Entity };

            foreach (var (name, value) in row)
                if (name != BoundKeyedBy.Element && name != "entity" && !lifted.Contains(name))
                    parent[name] = value?.DeepClone();

            return parent;
        }
    }

    /// <summary>
    /// The keys one request may ask owners for (DESIGN §3.5.2 step 2): <c>MaxResolveKeys</c> over
    /// every keyed stage, in stage order, a key counted once per stage however many targets of a
    /// union it is asked of; keys the cache answers cost nothing.
    /// </summary>
    private sealed class KeyBudget(int max)
    {
        public int Max { get; } = Math.Max(0, max);

        public int Left { get; private set; } = Math.Max(0, max);

        /// <summary>Takes up to <paramref name="wanted"/> keys from what is left; returns how many were granted.</summary>
        public int Take(int wanted)
        {
            var granted = Math.Min(wanted, Left);

            Left -= granted;

            return granted;
        }
    }

    /// <summary>
    /// The keys one owner query of a target carries: the key chunk, and no more than the owner's page
    /// holds, at <see cref="PerKey"/> rows per key for a grouped query. The page is the smaller of this
    /// host's <c>MaxPageSize</c> and the owner's own, when its shallow health said it (RE-13): an owner
    /// configured smaller would refuse the page as too large.
    /// </summary>
    private int ChunkOf(TargetPlan target) =>
        KeysPerQuery(options, client, target.Service, target.Grouped || target.PlainRowsPerKey > 1 ? Math.Max(target.RowsPerKey, target.PlainRowsPerKey) : 1);

    /// <summary>
    /// The keys one owner query of <paramref name="service"/> carries at <paramref name="rowsPerKey"/> rows per
    /// key: the key chunk, and no more than the owner's page holds (<see cref="ChunkOf"/>); what explain
    /// names in a remote lookup's <c>REMOTE_LOOKUP</c> note.
    /// </summary>
    public static int KeysPerQuery(OxQLOptions options, IRemoteQueryClient? client, string service, int rowsPerKey)
    {
        ArgumentNullException.ThrowIfNull(options);

        var chunk = Math.Max(1, options.Limits.ResolveKeyChunk);
        var page = PageOf(options, client, service);

        return rowsPerKey > 1 ? Math.Max(1, Math.Min(chunk, page / rowsPerKey)) : Math.Min(chunk, page);
    }

    /// <summary>The largest page an owner answers: this host's <c>MaxPageSize</c>, or the owner's own when it is known and smaller.</summary>
    private static int PageOf(OxQLOptions options, IRemoteQueryClient? client, string service)
    {
        var page = Math.Max(1, options.Limits.MaxPageSize);

        return service != SelfService && client is IRemoteOwnerInfo known && known.OwnerOf(service)?.MaxPageSize is { } owner and > 0 && owner < page ? owner : page;
    }

    /// <summary>
    /// Plans one keyed stage: per row the slots its reference yields with their case, key or
    /// provisional outcome; per target the keys to ask for, less what the cache holds and beyond
    /// what the request's key budget leaves (<c>RESOLVE_PARTIAL</c>), in chunks the owner answers in
    /// one page each.
    /// </summary>
    private StagePlan Plan(BoundPipeline bound, BoundStage.Resolve stage, IReadOnlyList<ContinuedStage> continued, IReadOnlyList<BsonDocument> rows, Guid organisation, bool strict, KeyBudget budget, List<Diagnostic> diagnostics)
    {
        var position = PositionOf(bound, stage);
        var plan = new StagePlan(stage, StageIndexOf(bound, stage), continued);
        var cases = CasesOf(stage);
        var union = cases.SelectMany(selected => selected.Targets).Select(target => target.Declared.Entity).Distinct(StringComparer.Ordinal).Count() > 1;
        var projected = ProjectedUnder(bound, position, stage.As);
        var parentProjected = stage.ParentAs is { } parentAs ? ProjectedUnder(bound, position, parentAs) : null;

        foreach (var selected in cases)
            foreach (var target in selected.Targets)
            {
                var shared = plan.Targets.FirstOrDefault(other => other.Entity == target.Declared.Entity && other.Target.Declared.Field == target.Declared.Field && other.Target.Declared.Item == target.Declared.Item);

                if (shared is null)
                {
                    plan.Targets.Add(shared = new TargetPlan(stage, target, continued, organisation, strict, union, projected, parentProjected,
                        legacyOwner: target.IsRemote && IsBefore21(ServiceKeyOf(target.Declared.Entity))));

                    // What an owner said this union target lacks, an earlier request learned: not
                    // asked again, and reported whether or not this page reaches the owner (RE-6).
                    if (target.IsRemote && union && cache.TryGetDrops(shared.DropsKey, out var select, out var parent))
                        shared.Learned(select, parent);
                }

                plan.ByTarget[target] = shared;
            }

        foreach (var row in rows)
        {
            var slots = SlotsOf(stage, cases, row);

            plan.Rows.Add(slots);

            foreach (var slot in slots)
            {
                if (slot is not { Case: { } selected, Key: { } key, Outcome: null })
                    continue;

                foreach (var target in selected.Targets)
                {
                    var targetPlan = plan.ByTarget[target];

                    targetPlan.GuidKeys |= selected.KeyAs == KeyAs.Guid || stage.Reference.LeafKind == Kind.Guid;
                    targetPlan.Want(key, cache);
                }
            }
        }

        // A key counts once per stage, however many targets of a union it is asked of.
        var granted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in plan.Targets)
        {
            var misses = target.Misses;
            var fresh = misses.Where(key => !granted.Contains(key)).ToList();
            var allowed = budget.Take(fresh.Count);

            granted.UnionWith(fresh.Take(allowed));

            if (allowed < fresh.Count)
            {
                var asked = misses.Where(granted.Contains).ToList();

                diagnostics.Add(new Diagnostic
                {
                    Code = Codes.ResolvePartial,
                    Message = $"'{stage.As}' needs {misses.Count} keys of '{target.Entity}'; the request asks owners for at most {budget.Max} keys, so only {asked.Count} are resolved on this page.",
                    Params = new Dictionary<string, object?> { ["alias"] = stage.As, ["keys"] = misses.Count, ["max"] = budget.Max },
                });

                target.Unanswered.UnionWith(misses.Where(key => !granted.Contains(key)));
                misses = asked;
            }

            var chunk = ChunkOf(target);

            for (var start = 0; start < misses.Count; start += chunk)
                target.Chunks.Add(misses.Skip(start).Take(chunk).ToList());
        }

        return plan;
    }

    /// <summary>
    /// The paths below <paramref name="alias"/> the last projection after the stage keeps, relative to
    /// the alias (DESIGN §3.5.3: a projected path under a keyed alias narrows its select); null when
    /// no inclusion projection follows, or it keeps the alias whole or not at all.
    /// </summary>
    private static IReadOnlyList<string>? ProjectedUnder(BoundPipeline bound, int? position, string alias)
    {
        if (position is not { } at || bound.Stages.Skip(at + 1).OfType<BoundStage.Project>().LastOrDefault() is not { Inclusion: true } project
            || project.Paths.Any(path => path.Wire == alias))
            return null;

        var under = project.Paths.Where(path => path.Wire.StartsWith(alias + ".", StringComparison.Ordinal)).Select(path => path.Wire[(alias.Length + 1)..]).ToList();

        return under.Count == 0 ? null : under;
    }

    /// <summary>The bound cases of a stage; a resolve bound without them (a 2.0 form) has its one simple case.</summary>
    private static IReadOnlyList<BoundResolveCase> CasesOf(BoundStage.Resolve stage)
    {
        if (stage.Cases is { Count: > 0 } cases)
            return cases;

        var declared = new ReferenceTarget(stage.TargetEntity, stage.TargetField, null, stage.IsRemote, stage.TargetField == "id");
        var target = new BoundResolveTarget(declared, stage.Target, stage.TargetFieldStorage, null, stage.Select, stage.RemoteSelect, stage.Filter, stage.RemoteFilter, stage.TargetScope, null, null, []);

        return [new BoundResolveCase(new ReferenceDef { Targets = [declared], DeclaredBy = ReferenceSource.Declaration }, null, [target])];
    }

    /// <summary>
    /// The slots of one row (DESIGN §3.5.2 step 1): the reference's value, or under
    /// <c>elements</c> each element's, with the first case its stored values select and its key
    /// converted per the case's <c>KeyAs</c>. A value that is null is <c>reference_null</c>, one no
    /// case selects <c>excluded</c>, one that does not convert <c>invalid_key</c>.
    /// </summary>
    private static List<Slot> SlotsOf(BoundStage.Resolve stage, IReadOnlyList<BoundResolveCase> cases, BsonDocument row)
    {
        var slots = new List<Slot>();

        if (stage.CollectionStorage is not { } collection)
        {
            slots.Add(SlotOf(stage, cases, row, row, stage.Reference.Storage!));
            return slots;
        }

        // Under elements the reference and the case conditions are read per element.
        var relative = stage.Reference.Storage == collection ? "" : stage.Reference.Storage![(collection.Length + 1)..];

        if (ValueAt(row, collection) is BsonArray elements)
            foreach (var element in elements)
                slots.Add(element is BsonDocument document
                    ? SlotOf(stage, cases, document, document, relative)
                    : relative.Length == 0 ? SlotOf(stage, cases, null, element, "") : new Slot(null, null, KeyedOutcome.ReferenceNull));

        return slots;
    }

    private static Slot SlotOf(BoundStage.Resolve stage, IReadOnlyList<BoundResolveCase> cases, BsonDocument? holder, BsonValue scope, string storage)
    {
        var value = storage.Length == 0 ? scope : scope is BsonDocument document ? ValueAt(document, storage) : null;

        if (value is null || value.IsBsonNull || value.IsBsonUndefined)
            return new Slot(null, null, KeyedOutcome.ReferenceNull);

        var selected = cases.FirstOrDefault(bound => bound.When is null || (holder is not null && Selects(bound.When, holder)));

        if (selected is null || selected.Targets.Count == 0)
            return new Slot(null, null, KeyedOutcome.Excluded);

        if (selected.KeyAs == KeyAs.Guid)
            return value is BsonString text && Guid.TryParse(text.Value, out var guid)
                ? new Slot(selected, guid.ToString("D"), null)
                : new Slot(selected, value.ToString(), KeyedOutcome.InvalidKey);

        var encoded = WireEncoder.EncodeScalar(value, stage.Reference.LeafKind, stage.Reference.Leaf);
        var key = encoded switch
        {
            null => null,
            JsonValue scalar => scalar.ToString(),
            _ => encoded.ToJsonString(),
        };

        if (key is null)
            return new Slot(null, null, KeyedOutcome.ReferenceNull);

        // A guid is keyed in one form, whatever form it is stored in, so the owner's answer finds it.
        return new Slot(selected, stage.Reference.LeafKind == Kind.Guid && Guid.TryParse(key, out var stored) ? stored.ToString("D") : key, null);
    }

    /// <summary>
    /// Whether a case's condition holds for a row or element: the stored value is one of the
    /// declared values; a hierarchical discriminator matches by any of its entries, and an absent
    /// one when the values admit a value stored without one.
    /// </summary>
    private static bool Selects(BoundCaseCondition when, BsonDocument holder)
    {
        var stored = ValueAt(holder, when.Storage);

        if (stored is null || stored.IsBsonNull || stored.IsBsonUndefined)
            return when.Values.Any(value => value.IsBsonNull);

        return stored is BsonArray entries
            ? entries.Any(entry => when.Values.Contains(entry))
            : when.Values.Contains(stored);
    }

    /// <summary>
    /// Assigns one stage's answers to the rows (DESIGN §3.5.2 step 5, §3.6): per slot the hits of
    /// its case's targets in target order, the first taken; <c>elements: first</c> takes the first
    /// slot that resolves, <c>all</c> every one up to <c>MaxLookupLimit</c>. The owning row of an
    /// item target goes under <c>parentAs</c>.
    /// </summary>
    private void Assign(StagePlan plan, List<Dictionary<string, JsonNode?>> perRow, List<KeyedRowOutcome> outcomes, List<KeyedTruncation> truncations, Dictionary<JsonObject, List<int>> liftedRows)
    {
        var stage = plan.Stage;

        if (stage.RemoteLookup is { } lookup)
        {
            AssignLookup(plan, lookup, perRow, truncations, liftedRows);
            return;
        }

        for (var rowIndex = 0; rowIndex < plan.Rows.Count; rowIndex++)
        {
            var slots = plan.Rows[rowIndex].Select(slot => Answer(plan, slot)).ToList();
            var values = perRow[rowIndex];

            if (stage.Elements == ResolveElements.All)
            {
                var resolved = slots.Where(slot => slot.Hit is not null).ToList();
                var limit = options.Limits.MaxLookupLimit;

                if (resolved.Count > limit)
                {
                    truncations.Add(new KeyedTruncation(plan.Index, stage.As, rowIndex, resolved.Count));
                    resolved = resolved.Take(limit).ToList();
                }

                values[stage.As] = new JsonArray(resolved.Select(slot => slot.Hit!.Value.Target.AliasOf(slot.Hit.Value.Row)).ToArray());

                if (stage.ParentAs is not null)
                    values[stage.ParentAs] = new JsonArray(resolved.Select(slot => (JsonNode?)slot.Hit!.Value.Target.ParentOf(slot.Hit.Value.Row)).ToArray());

                for (var element = 0; element < slots.Count; element++)
                    if (slots[element].Outcome != KeyedOutcome.Resolved)
                        outcomes.Add(new KeyedRowOutcome(plan.Index, stage.As, rowIndex, element, slots[element].Key, slots[element].Outcome));

                // An absent or empty collection has no element to name: the row itself is reference_null.
                if (slots.Count == 0)
                    outcomes.Add(new KeyedRowOutcome(plan.Index, stage.As, rowIndex, null, null, KeyedOutcome.ReferenceNull));

                continue;
            }

            var (chosen, at) = stage.Elements == ResolveElements.First ? First(slots) : (slots.FirstOrDefault() ?? Unresolved.Null, (int?)0);

            values[stage.As] = chosen.Hit is { } hit ? hit.Target.AliasOf(hit.Row) : null;

            if (stage.ParentAs is not null)
                values[stage.ParentAs] = chosen.Hit is { } parentHit ? parentHit.Target.ParentOf(parentHit.Row) : null;

            if (chosen.Outcome != KeyedOutcome.Resolved)
                outcomes.Add(new KeyedRowOutcome(plan.Index, stage.As, rowIndex, stage.Elements is null ? null : at, chosen.Key, chosen.Outcome));

            Lift(plan, chosen, rowIndex, values, outcomes, liftedRows);
        }
    }

    /// <summary>
    /// Assigns a remote lookup's answers to the rows (DESIGN §3.4.4): per row the children its key
    /// has at the owner, in the owner's order (the lookup's sort, then the child key). Under
    /// <c>first</c> the alias is the first child or null; otherwise the array of the first
    /// <c>limit</c> children, and a key that brought one more is a truncated parent
    /// (<c>LOOKUP_TRUNCATED</c>). An element child's owning rows go under <c>parentAs</c>, one per child
    /// in the same order. A row whose key the owner did not answer holds null, not an empty array:
    /// the owner's failure (<c>RESOLVE_TIMEOUT</c>, <c>RESOLVE_UNREACHABLE</c>, <c>RESOLVE_PARTIAL</c>)
    /// is already reported and refuses under <c>strict</c>. Children found or not, a lookup has no
    /// missing reference: no outcome is recorded.
    /// </summary>
    private static void AssignLookup(StagePlan plan, BoundRemoteLookup lookup, List<Dictionary<string, JsonNode?>> perRow, List<KeyedTruncation> truncations, Dictionary<JsonObject, List<int>> liftedRows)
    {
        var stage = plan.Stage;
        var target = plan.Targets[0];

        for (var rowIndex = 0; rowIndex < plan.Rows.Count; rowIndex++)
        {
            var values = perRow[rowIndex];
            var key = plan.Rows[rowIndex].FirstOrDefault()?.Key;
            var answered = key is not null && !target.Unanswered.Contains(key);
            var children = key is not null && target.Hits.TryGetValue(key, out var rows) ? rows : [];

            if (lookup.First)
            {
                var child = answered && children.Count > 0 ? children[0] : null;

                values[stage.As] = child is null ? null : target.AliasOf(child);

                if (stage.ParentAs is not null)
                    values[stage.ParentAs] = child is null ? null : target.ParentOf(child);

                Lift(plan, child is null ? Unresolved.Null : new Unresolved(key, KeyedOutcome.Resolved, (target, child)), rowIndex, values, [], liftedRows);
                continue;
            }

            if (!answered && key is not null)
            {
                values[stage.As] = null;

                if (stage.ParentAs is not null)
                    values[stage.ParentAs] = null;

                continue;
            }

            if (children.Count > lookup.Limit)
                truncations.Add(new KeyedTruncation(plan.Index, stage.As, rowIndex, children.Count));

            var kept = children.Take(lookup.Limit).ToList();

            values[stage.As] = new JsonArray(kept.Select(child => target.AliasOf(child)).ToArray());

            if (stage.ParentAs is not null)
                values[stage.ParentAs] = new JsonArray(kept.Select(child => (JsonNode?)target.ParentOf(child)).ToArray());
        }
    }

    /// <summary>
    /// The aliases the continued stages added, lifted from the owner row to the origin row (DESIGN
    /// §3.5.2 step 6). A continued stage the target's owner did not run — its <c>forTarget</c> names
    /// another target — leaves its aliases null with the outcome <c>not_applicable</c>, which loses
    /// nothing; a row the keyed stage did not resolve has nothing to continue from.
    /// </summary>
    private static void Lift(StagePlan plan, Unresolved chosen, int rowIndex, Dictionary<string, JsonNode?> values, List<KeyedRowOutcome> outcomes, Dictionary<JsonObject, List<int>> liftedRows)
    {
        foreach (var continued in plan.Continued)
        {
            if (chosen.Hit is { } hit && hit.Target.Continued.Origins.Contains(continued))
            {
                if (!liftedRows.TryGetValue(hit.Row, out var origins))
                    liftedRows[hit.Row] = origins = [];

                if (!origins.Contains(rowIndex))
                    origins.Add(rowIndex);

                foreach (var alias in continued.Aliases)
                    values[alias] = TargetPlan.Lifted(hit.Row, alias);

                continue;
            }

            foreach (var alias in continued.Aliases)
                values[alias] = null;

            if (chosen.Hit is not null)
                outcomes.Add(new KeyedRowOutcome(continued.OriginIndex, continued.Aliases[0], rowIndex, null, chosen.Key, KeyedOutcome.NotApplicable));
        }
    }

    /// <summary>A slot with its answer: the first hit by target order and record key, and the outcome.</summary>
    private sealed record Unresolved(string? Key, KeyedOutcome Outcome, (TargetPlan Target, JsonObject Row)? Hit)
    {
        public static readonly Unresolved Null = new(null, KeyedOutcome.ReferenceNull, null);
    }

    private static Unresolved Answer(StagePlan plan, Slot slot)
    {
        if (slot.Outcome is not null || slot.Case is null || slot.Key is null)
            return new Unresolved(slot.Key, slot.Outcome ?? KeyedOutcome.ReferenceNull, null);

        var hits = new List<(TargetPlan Target, JsonObject Row)>();
        var unanswered = false;
        var excluded = false;

        foreach (var target in slot.Case.Targets.Select(bound => plan.ByTarget[bound]).Distinct())
        {
            if (target.Hits.TryGetValue(slot.Key, out var rows))
                hits.AddRange(rows.Select(row => (target, row)));
            else if (target.Unanswered.Contains(slot.Key))
                unanswered = true;
            else if (target.Excluded.Contains(slot.Key))
                excluded = true;
        }

        // No row: an owner that did not answer leaves it open; the probe finding the key means the
        // filter left it out; otherwise no target holds it.
        return hits.Count switch
        {
            0 => new Unresolved(slot.Key, unanswered ? KeyedOutcome.OwnerUnanswered : excluded ? KeyedOutcome.Excluded : KeyedOutcome.NotFound, null),
            1 => new Unresolved(slot.Key, KeyedOutcome.Resolved, hits[0]),
            _ => new Unresolved(slot.Key, KeyedOutcome.Ambiguous, hits[0]),
        };
    }

    /// <summary>
    /// <c>elements: first</c> (DESIGN §3.6): the first slot that resolves, ambiguous or not; else the
    /// row folds to <c>owner_unanswered</c>, <c>not_found</c> or <c>invalid_key</c> when any slot had
    /// it, then <c>excluded</c>, then <c>reference_null</c>.
    /// </summary>
    private static (Unresolved Slot, int? Index) First(IReadOnlyList<Unresolved> slots)
    {
        for (var index = 0; index < slots.Count; index++)
            if (slots[index].Hit is not null)
                return (slots[index], index);

        foreach (var outcome in new[] { KeyedOutcome.OwnerUnanswered, KeyedOutcome.NotFound, KeyedOutcome.InvalidKey, KeyedOutcome.Excluded })
            for (var index = 0; index < slots.Count; index++)
                if (slots[index].Outcome == outcome)
                    return (slots[index], index);

        // No element at all (an absent or empty collection), or none but null ones: the first null
        // one when there is one, else no element to name.
        return slots.Count == 0 ? (Unresolved.Null, null) : (Unresolved.Null, 0);
    }

    // ---- values ---------------------------------------------------------------------------

    /// <summary>An owner's wire value of the target field as the reference member stores it.</summary>
    private static BsonValue OwnerValueToBson(string text, ResolvedPath reference)
    {
        var representation = reference.Leaf?.Representation ?? Representation.None;

        switch (reference.LeafKind)
        {
            case Kind.Guid when Guid.TryParse(text, out var guid):
                return representation.BsonType == BsonType.String
                    ? new BsonString(guid.ToString())
                    : new BsonBinaryData(guid, representation.GuidRepresentation is { } declared && declared != GuidRepresentation.Unspecified ? declared : GuidRepresentation.Standard);

            case Kind.Int or Kind.Long when long.TryParse(text, out var integer):
                return representation.BsonType == BsonType.Int32 ? new BsonInt32((int)integer) : new BsonInt64(integer);

            default:
                return new BsonString(text);
        }
    }

    /// <summary>The value at a dotted storage path, or null.</summary>
    public static BsonValue? ValueAt(BsonDocument document, string storage)
    {
        BsonValue current = document;

        foreach (var segment in storage.Split('.'))
        {
            if (current is BsonDocument inner && inner.TryGetValue(segment, out var next))
                current = next;
            else
                return null;
        }

        return current;
    }

    // ---- calls ------------------------------------------------------------------------------

    private enum Failure
    {
        Timeout,
        Unreachable,
    }

    /// <summary>
    /// The results of a call, or why there are none; <see cref="Status"/> is set when the owner was
    /// reached and answered with an HTTP error, <see cref="Budget"/> is the time the failing call had.
    /// </summary>
    private sealed record CallOutcome(IReadOnlyList<JsonNode?> Results, Failure? Failure, int? Status = null)
    {
        public TimeSpan Budget { get; init; }

        /// <summary>How many batches went out for it, a split batch counted per part.</summary>
        public int Calls { get; init; } = 1;
    }

    /// <summary>
    /// What one call carrying a chain may take (DESIGN §3.5.4): the time left less 50 ms, so the
    /// owner answers before this host's own ceiling, at most <c>Execution.ChainTimeoutMs</c>, never
    /// less than a millisecond. The owner applies it as its request ceiling and budgets its own
    /// owner calls from what is left of it, so time bounds the whole chain without a header.
    /// </summary>
    private TimeSpan ChainBudget(TimeSpan remaining)
    {
        var ceiling = TimeSpan.FromMilliseconds(options.Execution.EffectiveChainTimeoutMs);
        var left = remaining - TimeSpan.FromMilliseconds(50);

        if (left <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(1);

        return left < ceiling ? left : ceiling;
    }

    /// <summary>What one call may take: the time left, under the per-call ceiling, never less than a millisecond.</summary>
    private TimeSpan Budget(TimeSpan remaining)
    {
        var ceiling = TimeSpan.FromMilliseconds(options.Execution.EffectiveResolveTimeoutMs);

        if (remaining <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(1);

        return remaining < ceiling ? remaining : ceiling;
    }

    /// <summary>
    /// The queries for one owner, in batches no larger than the owner's batch cap (DESIGN §3.5.2
    /// step 4), results concatenated in order. The batches go out one after another, each under what
    /// is left before <paramref name="deadline"/> (the chain ceiling for a <paramref name="chain"/>,
    /// else the per-resolve one; DESIGN §3.5.4), so split batches share one budget instead of each
    /// taking the whole of it.
    /// </summary>
    private async Task<CallOutcome> CallInBatchesAsync(IRemoteQueryClient owner, string service, IReadOnlyList<QueryRequest> queries, DateTime deadline, bool chain, CancellationToken cancellationToken)
    {
        var size = BatchCapOf(owner, service);
        var results = new List<JsonNode?>(queries.Count);
        var calls = 0;

        for (var start = 0; start < queries.Count; start += size)
        {
            calls++;

            var left = deadline - DateTime.UtcNow;
            var budget = chain ? ChainBudget(left) : Budget(left);
            var request = new BatchRequest { Queries = queries.Skip(start).Take(size).ToList(), MaxTimeMs = OwnerCeilingMs(budget) };
            var outcome = await CallAsync(owner, service, request, budget, cancellationToken).ConfigureAwait(false);

            if (outcome.Failure is not null)
                return outcome with { Budget = budget, Calls = calls };

            results.AddRange(outcome.Results);
        }

        return new CallOutcome(results, null) { Calls = calls };
    }

    /// <summary>
    /// The batch ceiling an owner is sent: the call's budget less a margin (a tenth, at most 250 ms),
    /// so the owner stops and answers before this host stops waiting, never less than a millisecond.
    /// </summary>
    public static int OwnerCeilingMs(TimeSpan budget)
    {
        var milliseconds = Math.Max(1, (int)budget.TotalMilliseconds);

        return Math.Max(1, milliseconds - Math.Min(250, milliseconds / 10));
    }

    /// <summary>
    /// The largest batch an owner takes: its own <c>maxBatchQueries</c> as its client last read it
    /// off the owner's shallow health (<see cref="IRemoteOwnerInfo"/>), else this host's cap, which
    /// is also this host's own cap as its <see cref="SelfOwner"/>.
    /// </summary>
    private int BatchCapOf(IRemoteQueryClient owner, string service) =>
        service != SelfService && owner is IRemoteOwnerInfo known && known.OwnerOf(service)?.MaxBatchQueries is { } cap and > 0
            ? cap
            : Math.Max(1, options.Limits.MaxBatchQueries);

    /// <summary>One call to one owner within the budget; a timeout or a transport fault is an outcome, never an exception.</summary>
    private static async Task<CallOutcome> CallAsync(IRemoteQueryClient owner, string service, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(budget);

        try
        {
            var response = await owner.BatchAsync(service, request, budget, timeout.Token).ConfigureAwait(false);

            return new CallOutcome(response.Results, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CallOutcome([], Failure.Timeout);
        }
        catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested && exception.StatusCode is { } status)
        {
            // The owner was reached and said no: a rejected key, a body over its cap, a fault of
            // its own. The status keeps that apart from a network that is down.
            return new CallOutcome([], Failure.Unreachable, (int)status);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new CallOutcome([], Failure.Unreachable);
        }
    }

    private static bool Succeeded(JsonNode result) => result is JsonObject success && success["items"] is JsonArray;

    /// <summary>
    /// The owner's refusal as this host's: 422 <c>RESOLVE_REFUSED</c> wrapping the owner's errors at
    /// the resolve stage. An owner error at one of the continued stages is mapped back (DESIGN
    /// §3.5.3): its <c>stage</c> and <c>path</c> become the caller's, <c>params.owner</c> carries
    /// where the owner saw it (<c>service</c>, <c>entity</c>, <c>target</c>, <c>stage</c>, <c>path</c>),
    /// and the head points at the first such stage. An owner that continued further maps its own
    /// owner's errors the same way first, so the mapping composes along the chain.
    /// </summary>
    private static Refusal Refused(JsonNode? result, TargetPlan target, string service, int? stage) =>
        Refused(result, target.Entity, stage, (error, mapped) => MapBack(error, mapped, target, service));

    /// <summary>An owner error at one of <paramref name="target"/>'s continued stages as the caller's; null for one at the owner query's own stages.</summary>
    private static QueryValidationError? MapBack(JsonObject error, TargetPlan target, string service) =>
        MapBack(error, new QueryValidationError
        {
            Code = error["code"]?.ToString() ?? Codes.InternalError,
            Message = error["message"]?.ToString() ?? "",
            Path = error["path"]?.ToString(),
        }, target, service);

    private static QueryValidationError? MapBack(JsonObject error, QueryValidationError mapped, TargetPlan target, string service)
    {
        var ownerStage = error["stage"] is JsonValue index && index.TryGetValue<int>(out var number) ? number : (int?)null;

        if (target.OriginOf(ownerStage) is not { } origin)
            return target.Stage.RemoteLookup is { } lookup ? MapLookupBack(error, mapped, target, lookup, service, ownerStage) : null;

        var parameters = error["params"] is JsonObject written
            ? written.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone(), StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        parameters["owner"] = new Dictionary<string, object?>
        {
            ["service"] = service == SelfService ? null : service,
            ["entity"] = target.Entity,
            ["target"] = target.Target.Declared.Item is { } item ? $"{target.Entity}#{item}" : target.Entity,
            ["stage"] = ownerStage,
            ["path"] = mapped.Path,
        };

        return mapped with
        {
            Stage = origin.OriginIndex,
            Path = Continuation.ToOrigin(mapped.Path, origin, target.Stage, target.Target.Declared.Item is not null),
            Params = parameters,
        };
    }

    /// <summary>
    /// An owner error at a remote lookup's own owner query (DESIGN §3.4.4): its key match, filter, sort,
    /// projection or <c>keyedBy</c>, all written by the caller in the lookup — the path, the filter, the
    /// sort, the select, the owning-row select, the limit against the owner's own cap — so the error is
    /// the lookup's, at its stage, with the path as the caller wrote it (relative to the child) and
    /// <c>params.owner</c> saying where the owner saw it.
    /// </summary>
    private static QueryValidationError MapLookupBack(JsonObject error, QueryValidationError mapped, TargetPlan target, BoundRemoteLookup lookup, string service, int? ownerStage)
    {
        var parameters = error["params"] is JsonObject written
            ? written.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone(), StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        parameters["owner"] = new Dictionary<string, object?>
        {
            ["service"] = service == SelfService ? null : service,
            ["entity"] = target.Entity,
            ["target"] = target.TargetName,
            ["stage"] = ownerStage,
            ["path"] = mapped.Path,
        };

        var element = BoundKeyedBy.Element + ".";
        var keyedPath = lookup.Item is null ? lookup.Path : lookup.Item + "." + lookup.Path;
        var path = mapped.Path switch
        {
            null => null,
            var owned when owned == keyedPath || owned == BoundKeyedBy.Key => lookup.Path,
            var owned when lookup.Item is not null && owned.StartsWith(element, StringComparison.Ordinal) => owned[element.Length..],
            var owned => owned,
        };

        return mapped with { Stage = target.Stage.Stage < 0 ? null : target.Stage.Stage, Path = path, Params = parameters };
    }

    /// <summary>
    /// The owner's refusal of a query for <paramref name="targetEntity"/> as this host's 422
    /// <c>RESOLVE_REFUSED</c> at <paramref name="stage"/>; <paramref name="map"/> maps an owner error
    /// back to the caller's stage, or leaves it (null), and the head then points at the first mapped one.
    /// </summary>
    private static Refusal Refused(JsonNode? result, string targetEntity, int? stage, Func<JsonObject, QueryValidationError, QueryValidationError?>? map = null)
    {
        result = OwnerFaults.Scrubbed(result);

        var inner = new List<QueryValidationError>();
        int? mappedStage = null;

        if (result?["errors"] is JsonArray errors)
            foreach (var error in errors.OfType<JsonObject>())
            {
                var owner = new QueryValidationError
                {
                    Code = error["code"]?.ToString() ?? Codes.InternalError,
                    Message = error["message"]?.ToString() ?? "",
                    Path = error["path"]?.ToString(),
                };

                if (map?.Invoke(error, owner) is { } mapped)
                {
                    owner = mapped;
                    mappedStage ??= mapped.Stage;
                }

                inner.Add(owner);
            }

        var at = mappedStage ?? stage;
        var head = new QueryValidationError
        {
            Code = Codes.ResolveRefused,
            Message = result is null
                ? $"The owner of '{targetEntity}' answered without a result for this query."
                : $"The owner of '{targetEntity}' refused the query: {result["title"]?.ToString() ?? result["type"]?.ToString() ?? "no reason given"}.",
            Stage = at,
        };

        return Refusal.NotExecutable(Codes.ResolveRefused, head.Message, at, [head, .. inner]);
    }

    /// <summary>
    /// <c>OWNER_NOT_CAPABLE</c> (DESIGN §3.5.4): a remote target whose query needs 2.1 — continued
    /// stages or <c>keyedBy</c> — at an owner whose shallow health reports an older engine, refused
    /// before anything is sent. An owner not yet probed, or reporting a version this host cannot read,
    /// is sent the query, and an old owner refuses its unknown members inside <c>RESOLVE_REFUSED</c>.
    /// </summary>
    private Refusal? Incapable(BoundPipeline bound, IReadOnlyList<TargetPlan> targets)
    {
        if (client is not IRemoteOwnerInfo owners)
            return null;

        foreach (var target in targets)
        {
            if (target.Service == SelfService || !target.NeedsOwner21 || (target.Chunks.Count == 0 && target.Continued.IsEmpty))
                continue;

            if (owners.OwnerOf(target.Service)?.EngineVersion is not { } version || EngineVersionOf(version) is not { } parsed || parsed >= Owner21)
                continue;

            var stage = target.Continued.IsEmpty ? StageIndexOf(bound, target.Stage) : target.Continued.Origins[0].OriginIndex;
            var message = $"{target.Service} runs OxQL {version}; this stage needs 2.1.";

            return Refusal.NotExecutable(Codes.OwnerNotCapable, message, stage,
            [
                new QueryValidationError
                {
                    Code = Codes.OwnerNotCapable,
                    Message = message,
                    Stage = stage,
                    Params = new Dictionary<string, object?> { ["service"] = target.Service, ["version"] = version, ["needs"] = "2.1" },
                },
            ]);
        }

        return null;
    }

    /// <summary>
    /// Asks the client for each owner's facts before anything is sized or gated by them (RS-4): a
    /// client that has not read an owner's shallow health yet may read it now. Bounded by the time
    /// left; a client that does not answer in time leaves the facts unknown, as they were.
    /// </summary>
    private async Task ReadOwnersAsync(IEnumerable<string> services, TimeSpan remaining, CancellationToken cancellationToken)
    {
        if (client is not IRemoteOwnerInfo owners)
            return;

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A small slice of the phase (a tenth, at most 250 ms): an owner whose health is slow leaves
        // its facts unknown and this host's own caps apply, rather than the probe eating the time
        // the owner's answer needs (D-ENG-2).
        bounded.CancelAfter(OwnerFactsSlice(remaining));

        try
        {
            await Task.WhenAll(services.Distinct(StringComparer.Ordinal).Select(service => owners.OwnerOfAsync(service, bounded.Token).AsTask())).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Facts are an optimisation of what is sent; without them the owner answers or refuses.
        }
    }

    /// <summary>The time the owners' facts may take out of a phase of <paramref name="remaining"/>: a tenth, at most 250 ms, at least a millisecond.</summary>
    public static TimeSpan OwnerFactsSlice(TimeSpan remaining) =>
        TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds / 10, 1, 250));

    /// <summary>Whether the owner of <paramref name="service"/> is known, by its shallow health, to run an engine before 2.1.</summary>
    private bool IsBefore21(string service) => IsBefore21(client, service);

    private static bool IsBefore21(IRemoteQueryClient? client, string service) =>
        client is IRemoteOwnerInfo owners && owners.OwnerOf(service)?.EngineVersion is { } version && EngineVersionOf(version) is { } parsed && parsed < Owner21;

    /// <summary>The engine version remote continuation, typed and item targets and <c>keyedBy</c> need at the owner.</summary>
    private static readonly Version Owner21 = new(2, 1);

    /// <summary>The numeric part of an engine version as health reports it (<c>2.1.0.0</c>, <c>2.0.126-beta</c>), or null.</summary>
    private static Version? EngineVersionOf(string text)
    {
        var numeric = text.Split('-', '+')[0];

        return Version.TryParse(numeric.Contains('.') ? numeric : numeric + ".0", out var version) ? version : null;
    }

    /// <summary>
    /// An owner row without the member the host projected and keys by. The host asked for that member,
    /// so its absence means the answer cannot be read; it is refused rather than taken as "no such row".
    /// </summary>
    private static Refusal WithoutKey(string targetEntity, string field, int? stage)
    {
        var message = $"The owner of '{targetEntity}' answered a row without the projected member '{field}'.";

        return Refusal.NotExecutable(Codes.ResolveRefused, message, stage, [new QueryValidationError { Code = Codes.ResolveRefused, Message = message, Stage = stage }]);
    }

    /// <summary>The caller's index of a bound stage, which diagnostics and refusals name (a caller stage that bound to nothing leaves no position).</summary>
    private static int? StageIndexOf(BoundPipeline bound, BoundStage stage) => bound.CallerIndexOf(stage);

    /// <summary>The position of a bound stage among the bound stages, for reading the stages after it.</summary>
    private static int? PositionOf(BoundPipeline bound, BoundStage stage)
    {
        for (var position = 0; position < bound.Stages.Count; position++)
            if (ReferenceEquals(bound.Stages[position], stage))
                return position;

        return null;
    }

    /// <summary>The caller's index of the match stage carrying a semi-join leaf.</summary>
    private static int? StageIndexOf(BoundPipeline bound, SemiJoinSlot slot)
    {
        for (var position = 0; position < bound.Stages.Count; position++)
            if (bound.Stages[position] is BoundStage.Match match && Contains(match.Condition, slot.Leaf))
                return bound.CallerIndexOf(position);

        return null;
    }

    private static bool Contains(BoundCondition condition, BoundCondition.Leaf leaf) => condition switch
    {
        BoundCondition.And and => and.Conditions.Any(inner => Contains(inner, leaf)),
        BoundCondition.Or or => or.Conditions.Any(inner => Contains(inner, leaf)),
        BoundCondition.Not not => Contains(not.Condition, leaf),
        BoundCondition.Any any => Contains(any.Inner, leaf),
        _ => ReferenceEquals(condition, leaf),
    };
}
