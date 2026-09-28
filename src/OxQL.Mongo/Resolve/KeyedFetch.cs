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
}

/// <summary>
/// The outcome of one row of a keyed resolve: the caller's <paramref name="Stage"/> index, the
/// alias, the page row, the element index under <c>elements</c>, and the key (in the form sent to
/// the owner) where there is one.
/// </summary>
public sealed record KeyedRowOutcome(int? Stage, string Alias, int Row, int? Element, string? Key, KeyedOutcome Outcome);

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

            if (truncationGroups.GetValueOrDefault(key) is { Count: > 0 } truncated)
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
    public async Task<Refusal?> ByConditionAsync(CompiledQuery compiled, RequestContext context, TimeSpan remaining, CancellationToken cancellationToken)
    {
        var slots = compiled.SemiJoins;

        if (slots.Count == 0)
            return null;

        var organisation = context.Organisation!.Value;
        var cap = options.Limits.MaxSemiJoinIds;
        var pageSize = Math.Max(1, Math.Min(options.Limits.MaxPageSize, cap));
        var lastOffset = (cap / pageSize) * pageSize;
        var deadline = DateTime.UtcNow + (remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));

        // An answer already in hand costs the owner nothing, and a grid asks the same question
        // again on every block of the same filter.
        var pending = new List<SemiJoinSlot>();

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
        // asking for the rest is worth a round trip at all.
        foreach (var group in pending.GroupBy(slot => plans[slot].Service, StringComparer.Ordinal))
        {
            var members = group.ToList();
            var queries = members.Select(slot => OwnerQueryBuilder.ByCondition(slot, pageSize, offset: 0, count: true)).ToList();
            var outcome = await CallInBatchesAsync(client!, group.Key, queries, Budget(deadline), cancellationToken).ConfigureAwait(false);

            if (outcome.Failure is not null)
                return Unanswered(group.Key, outcome);

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

            var calls = wave.GroupBy(entry => plans[entry.Slot].Service, StringComparer.Ordinal).Select(async group =>
            {
                var entries = group.ToList();
                var queries = entries.Select(entry => OwnerQueryBuilder.ByCondition(entry.Slot, pageSize, entry.Offset, count: false)).ToList();

                return (Entries: entries, Service: group.Key, Outcome: await CallInBatchesAsync(client!, group.Key, queries, Budget(deadline), cancellationToken).ConfigureAwait(false));
            }).ToList();

            await Task.WhenAll(calls).ConfigureAwait(false);

            foreach (var call in calls.Select(task => task.Result))
            {
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
        var stages = compiled.KeyedResolves.Select(stage => Plan(stage, StageIndexOf(compiled.Bound, stage), rows, organisation, strict, budget, diagnostics)).ToList();
        var targets = stages.SelectMany(stage => stage.Targets).ToList();
        var cacheHits = targets.Sum(target => target.CacheHits);
        var deadline = DateTime.UtcNow + (remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
        var calls = 0;

        // Round one: the filtered owner queries of every chunk.
        var first = await SendAsync(targets.SelectMany(target => target.Chunks.Select(chunk => new Sent(target, chunk, target.Query(chunk)))).ToList(), context, Budget(remaining), cancellationToken).ConfigureAwait(false);

        foreach (var call in first)
        {
            calls += call.Service == SelfService ? 0 : 1;

            if (Failed(call, diagnostics))
                continue;

            for (var index = 0; index < call.Sent.Count; index++)
            {
                var (target, keys, _) = call.Sent[index];
                var result = index < call.Outcome.Results.Count ? call.Outcome.Results[index] : null;
                var stageIndex = StageIndexOf(compiled.Bound, target.Stage);

                if (result is null || !Succeeded(result))
                    return new ResolveResult { Refusal = Refused(result, target.Entity, stageIndex), Calls = calls, CacheHits = cacheHits };

                foreach (var item in result["items"]!.AsArray())
                {
                    if (item is not JsonObject row || target.KeyOf(row) is not { } key)
                        return new ResolveResult { Refusal = WithoutKey(target.Entity, target.KeyWire, stageIndex), Calls = calls, CacheHits = cacheHits };

                    target.Hit(key, row);
                }

                if (HasNextPage(result))
                    Partial(call.Service, target, keys.Where(key => !target.Hits.ContainsKey(key)).ToList(), keys, diagnostics);
            }
        }

        // Round two: the existence probe of the keys a filtered query did not return.
        var probes = targets.Where(target => target.Probed).SelectMany(target => target.ProbeChunks(ChunkOf(target)).Select(chunk => new Sent(target, chunk, target.Probe(chunk)))).ToList();

        foreach (var call in probes.Count == 0 ? [] : await SendAsync(probes, context, Budget(deadline), cancellationToken).ConfigureAwait(false))
        {
            calls += call.Service == SelfService ? 0 : 1;

            if (Failed(call, diagnostics))
                continue;

            for (var index = 0; index < call.Sent.Count; index++)
            {
                var (target, keys, _) = call.Sent[index];
                var result = index < call.Outcome.Results.Count ? call.Outcome.Results[index] : null;
                var stageIndex = StageIndexOf(compiled.Bound, target.Stage);

                if (result is null || !Succeeded(result))
                    return new ResolveResult { Refusal = Refused(result, target.Entity, stageIndex), Calls = calls, CacheHits = cacheHits };

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

        // What the owners answered in full is kept for the next page.
        foreach (var target in targets)
            foreach (var key in target.Chunks.SelectMany(chunk => chunk))
                if (!target.Unanswered.Contains(key) && !target.Uncached.Contains(key))
                    cache.Set(target.CacheKey(key), target.Cached(key));

        var perRow = rows.Select(_ => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)).ToList();
        var outcomes = new List<KeyedRowOutcome>();
        var truncations = new List<KeyedTruncation>();

        foreach (var stage in stages)
            Assign(stage, perRow, outcomes, truncations);

        return new ResolveResult { Rows = perRow, Diagnostics = diagnostics, Outcomes = outcomes, Truncations = truncations, Calls = calls, CacheHits = cacheHits };
    }

    /// <summary>One owner query of a round: the target it asks for, the keys it carries, and the query.</summary>
    private sealed record Sent(TargetPlan Target, IReadOnlyList<string> Keys, QueryRequest Query);

    /// <summary>The queries one owner was sent in a round, and what came back.</summary>
    private sealed record OwnerCall(string Service, IReadOnlyList<Sent> Sent, CallOutcome Outcome);

    /// <summary>Sends a round's queries, batched per owner, owners in parallel; a local target's to this host's own <see cref="SelfOwner"/>.</summary>
    private async Task<IReadOnlyList<OwnerCall>> SendAsync(IReadOnlyList<Sent> sends, RequestContext context, TimeSpan budget, CancellationToken cancellationToken)
    {
        var tasks = sends.GroupBy(sent => sent.Target.Service, StringComparer.Ordinal).Select(async group =>
        {
            var sent = group.ToList();
            var owner = group.Key == SelfService ? new SelfOwner(self!, context) : client!;

            return new OwnerCall(group.Key, sent, await CallInBatchesAsync(owner, group.Key, sent.Select(entry => entry.Query).ToList(), budget, cancellationToken).ConfigureAwait(false));
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
                ? $"{owner} did not answer within {options.Execution.EffectiveResolveTimeoutMs} ms; '{string.Join("', '", aliases)}' is null on this page."
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

    /// <summary>The rows a grouped owner query returns per key at most: two tell a resolved key from an ambiguous one.</summary>
    public const int PerKey = 2;

    /// <summary>One keyed stage over the page: its targets, and per row the keys its cases select.</summary>
    private sealed class StagePlan(BoundStage.Resolve stage, int? index)
    {
        public BoundStage.Resolve Stage { get; } = stage;

        /// <summary>The caller's stage index, for the outcomes.</summary>
        public int? Index { get; } = index;

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
    /// found although the filter left them out, and the keys the owner did not answer.
    /// </summary>
    private sealed class TargetPlan
    {
        private readonly Guid organisation;
        private readonly bool strict;
        private readonly string planHash;
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);

        public TargetPlan(BoundStage.Resolve stage, BoundResolveTarget target, Guid organisation, bool strict)
        {
            Stage = stage;
            Target = target;
            this.organisation = organisation;
            this.strict = strict;

            // An entity keyed by its own key has one row per key, which a plain key match serves;
            // every other target is grouped per key. A plain 2.0 resolve keeps the plain query it
            // always sent (DESIGN §3.5.7, §3.5.8): its owner may still be on 2.0, or reached over
            // a route that does not take keyedBy.
            Grouped = stage.NeedsKeyedFetch && (target.Declared.Item is not null || !target.Declared.FieldIsKey);
            Service = target.IsRemote ? ServiceKeyOf(target.Declared.Entity) : SelfService;

            // The probe tells a key the filter left out from a missing one, which only a stage
            // that reports or refuses missing references needs (DESIGN §3.5.2 step 3).
            Probed = target.RemoteFilter is { ValueKind: JsonValueKind.Object } && stage.EffectiveOnMissing != ResolveOnMissing.Null;

            // The plan: the owner query as sent, without its keys (DESIGN §3.5.6).
            planHash = OwnerFetchCache.PlanHashOf(Query([]), Probed);
        }

        public BoundStage.Resolve Stage { get; }

        public BoundResolveTarget Target { get; }

        public bool Grouped { get; }

        /// <summary>Whether the keys the filtered query does not return are probed without the filter.</summary>
        public bool Probed { get; }

        /// <summary>The service the target's owner query goes to; <see cref="SelfService"/> for a local target.</summary>
        public string Service { get; }

        public string Entity => Target.Declared.Entity;

        /// <summary>The member of an answer row the rows are keyed by, as the owner writes it.</summary>
        public string KeyWire => Target.Declared.Item is null ? Target.Declared.Field : BoundKeyedBy.Element + "." + Target.Declared.Field;

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

        public QueryRequest Query(IReadOnlyList<string> keys) => OwnerQueryBuilder.ByKeys(Stage, Target, keys, Grouped ? PerKey : null);

        /// <summary>The existence probe of some keys: the query without the filter, one row per key is enough.</summary>
        public QueryRequest Probe(IReadOnlyList<string> keys) => OwnerQueryBuilder.ByKeys(Stage, Target, keys, Grouped ? 1 : null, probe: true);

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

        /// <summary>What the cache keeps for a key: its rows (the first for a plain query, up to <see cref="PerKey"/> for a grouped one), or whether it was excluded.</summary>
        public OwnerAnswer Cached(string key)
        {
            Hits.TryGetValue(key, out var rows);

            return new OwnerAnswer((rows ?? []).Take(Grouped ? PerKey : 1).ToList(), rows is not { Count: > 0 } && Excluded.Contains(key));
        }

        public void Hit(string key, JsonObject row)
        {
            if (!Hits.TryGetValue(key, out var rows))
                Hits[key] = rows = [];

            if (rows.Count < PerKey)
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

        /// <summary>An answer row as the alias holds it: the element of an item target, the row of an entity target.</summary>
        public JsonNode? AliasOf(JsonObject row) =>
            (Target.Declared.Item is null ? row : row[BoundKeyedBy.Element])?.DeepClone();

        /// <summary>The owning row of an item target's answer: its entity and the parent members the owner projected.</summary>
        public JsonObject ParentOf(JsonObject row)
        {
            var parent = new JsonObject { ["entity"] = Entity };

            foreach (var (name, value) in row)
                if (name != BoundKeyedBy.Element && name != "entity")
                    parent[name] = value?.DeepClone();

            return parent;
        }
    }

    /// <summary>
    /// The keys one request may ask owners for (DESIGN §3.5.2 step 2): <c>MaxResolveKeys</c> over
    /// every keyed stage and target, in stage order; keys the cache answers cost nothing.
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

    /// <summary>The keys one owner query of a target carries: the key chunk, and for a grouped query no more than the owner's page holds at <see cref="PerKey"/> rows per key.</summary>
    private int ChunkOf(TargetPlan target)
    {
        var chunk = Math.Max(1, options.Limits.ResolveKeyChunk);

        return target.Grouped ? Math.Max(1, Math.Min(chunk, options.Limits.MaxPageSize / PerKey)) : chunk;
    }

    /// <summary>
    /// Plans one keyed stage: per row the slots its reference yields with their case, key or
    /// provisional outcome; per target the keys to ask for, less what the cache holds and beyond
    /// what the request's key budget leaves (<c>RESOLVE_PARTIAL</c>), in chunks the owner answers in
    /// one page each.
    /// </summary>
    private StagePlan Plan(BoundStage.Resolve stage, int? index, IReadOnlyList<BsonDocument> rows, Guid organisation, bool strict, KeyBudget budget, List<Diagnostic> diagnostics)
    {
        var plan = new StagePlan(stage, index);
        var cases = CasesOf(stage);

        foreach (var bound in cases)
            foreach (var target in bound.Targets)
            {
                var shared = plan.Targets.FirstOrDefault(other => other.Entity == target.Declared.Entity && other.Target.Declared.Field == target.Declared.Field && other.Target.Declared.Item == target.Declared.Item);

                if (shared is null)
                    plan.Targets.Add(shared = new TargetPlan(stage, target, organisation, strict));

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

        foreach (var target in plan.Targets)
        {
            var misses = target.Misses;
            var granted = budget.Take(misses.Count);

            if (granted < misses.Count)
            {
                diagnostics.Add(new Diagnostic
                {
                    Code = Codes.ResolvePartial,
                    Message = $"'{stage.As}' needs {misses.Count} keys of '{target.Entity}'; the request asks owners for at most {budget.Max} keys, so only the first {granted} are resolved on this page.",
                    Params = new Dictionary<string, object?> { ["alias"] = stage.As, ["keys"] = misses.Count, ["max"] = budget.Max },
                });

                target.Unanswered.UnionWith(misses.Skip(granted));
                misses = misses.Take(granted).ToList();
            }

            var chunk = ChunkOf(target);

            for (var start = 0; start < misses.Count; start += chunk)
                target.Chunks.Add(misses.Skip(start).Take(chunk).ToList());
        }

        return plan;
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
    private void Assign(StagePlan plan, List<Dictionary<string, JsonNode?>> perRow, List<KeyedRowOutcome> outcomes, List<KeyedTruncation> truncations)
    {
        var stage = plan.Stage;

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

                continue;
            }

            var (chosen, at) = stage.Elements == ResolveElements.First ? First(slots) : (slots.FirstOrDefault() ?? Unresolved.Null, 0);

            values[stage.As] = chosen.Hit is { } hit ? hit.Target.AliasOf(hit.Row) : null;

            if (stage.ParentAs is not null)
                values[stage.ParentAs] = chosen.Hit is { } parentHit ? parentHit.Target.ParentOf(parentHit.Row) : null;

            if (chosen.Outcome != KeyedOutcome.Resolved)
                outcomes.Add(new KeyedRowOutcome(plan.Index, stage.As, rowIndex, stage.Elements is null ? null : at, chosen.Key, chosen.Outcome));
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
    private static (Unresolved Slot, int Index) First(IReadOnlyList<Unresolved> slots)
    {
        for (var index = 0; index < slots.Count; index++)
            if (slots[index].Hit is not null)
                return (slots[index], index);

        foreach (var outcome in new[] { KeyedOutcome.OwnerUnanswered, KeyedOutcome.NotFound, KeyedOutcome.InvalidKey, KeyedOutcome.Excluded })
            for (var index = 0; index < slots.Count; index++)
                if (slots[index].Outcome == outcome)
                    return (slots[index], index);

        return (Unresolved.Null, 0);
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

    /// <summary>The results of a call, or why there are none; <see cref="Status"/> is set when the owner was reached and answered with an HTTP error.</summary>
    private sealed record CallOutcome(IReadOnlyList<JsonNode?> Results, Failure? Failure, int? Status = null);

    /// <summary>What is left of a phase for one call, under the per-call ceiling.</summary>
    private TimeSpan Budget(DateTime deadline) => Budget(deadline - DateTime.UtcNow);

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
    /// step 4), each within the budget, results concatenated in order.
    /// </summary>
    private async Task<CallOutcome> CallInBatchesAsync(IRemoteQueryClient owner, string service, IReadOnlyList<QueryRequest> queries, TimeSpan budget, CancellationToken cancellationToken)
    {
        var size = BatchCapOf(owner, service);
        var results = new List<JsonNode?>(queries.Count);

        for (var start = 0; start < queries.Count; start += size)
        {
            var request = new BatchRequest { Queries = queries.Skip(start).Take(size).ToList(), MaxTimeMs = (int)budget.TotalMilliseconds };
            var outcome = await CallAsync(owner, service, request, budget, cancellationToken).ConfigureAwait(false);

            if (outcome.Failure is not null)
                return outcome;

            results.AddRange(outcome.Results);
        }

        return new CallOutcome(results, null);
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

    /// <summary>The owner's refusal as this host's: 422 <c>RESOLVE_REFUSED</c> wrapping the owner's errors at the resolve stage.</summary>
    private static Refusal Refused(JsonNode? result, string targetEntity, int? stage)
    {
        var inner = new List<QueryValidationError>();

        if (result?["errors"] is JsonArray errors)
            foreach (var error in errors.OfType<JsonObject>())
                inner.Add(new QueryValidationError
                {
                    Code = error["code"]?.ToString() ?? Codes.InternalError,
                    Message = error["message"]?.ToString() ?? "",
                    Path = error["path"]?.ToString(),
                });

        var head = new QueryValidationError
        {
            Code = Codes.ResolveRefused,
            Message = result is null
                ? $"The owner of '{targetEntity}' answered without a result for this query."
                : $"The owner of '{targetEntity}' refused the query: {result["title"]?.ToString() ?? result["type"]?.ToString() ?? "no reason given"}.",
            Stage = stage,
        };

        return Refusal.NotExecutable(Codes.ResolveRefused, head.Message, stage, [head, .. inner]);
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

    /// <summary>The caller's index of a bound stage: the bound stages are the caller's in order, the page appended when absent.</summary>
    private static int? StageIndexOf(BoundPipeline bound, BoundStage stage)
    {
        var index = bound.Stages.ToList().IndexOf(stage);

        return index < 0 ? null : index;
    }

    /// <summary>The caller's index of the match stage carrying a semi-join leaf.</summary>
    private static int? StageIndexOf(BoundPipeline bound, SemiJoinSlot slot)
    {
        for (var index = 0; index < bound.Stages.Count; index++)
            if (bound.Stages[index] is BoundStage.Match match && Contains(match.Condition, slot.Leaf))
                return index;

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
