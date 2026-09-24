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

    /// <summary>How many calls went to remote owners.</summary>
    public int Calls { get; init; }

    /// <summary>How many keys were answered from the cache.</summary>
    public int CacheHits { get; init; }
}

/// <summary>
/// The remote half of resolve: after the page is fixed, one batch per owning
/// service carrying one query per resolve stage and key chunk, cached per key for a TTL;
/// and, before the page runs, the semi-join that asks an owner for the ids a condition on
/// its rows selects, refusing above the cap rather than truncating.
/// </summary>
public sealed class RemoteResolver
{
    private readonly IRemoteQueryClient client;
    private readonly ResolveCache cache;
    private readonly SemiJoinCache semiJoinIds;
    private readonly OxQLOptions options;

    public RemoteResolver(IRemoteQueryClient client, ResolveCache cache, OxQLOptions options, SemiJoinCache? semiJoinIds = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.semiJoinIds = semiJoinIds ?? new SemiJoinCache(this.options);
    }

    /// <summary>The service key a target entity is owned by: its namespace, the host's <c>InternalHosts</c> key.</summary>
    public static string ServiceKeyOf(string targetEntity) => targetEntity.Split('.')[0];

    // ---- semi-join (before the page) --------------------------------------------------------

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
    public async Task<Refusal?> SemiJoinAsync(CompiledQuery compiled, RequestContext context, TimeSpan remaining, CancellationToken cancellationToken)
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
            if (semiJoinIds.TryGet(CacheKeyOf(slot, organisation, pageSize), out var cached))
                Fill(slot, cached);
            else
                pending.Add(slot);
        }

        if (pending.Count == 0)
            return null;

        var plans = pending.ToDictionary(slot => slot, slot => new SlotPlan(ServiceKeyOf(TargetOf(slot))));

        // Round one: the first page of every pending slot, with the count that decides whether
        // asking for the rest is worth a round trip at all.
        foreach (var group in pending.GroupBy(slot => plans[slot].Service, StringComparer.Ordinal))
        {
            var members = group.ToList();
            var queries = members.Select(slot => SemiJoinQuery(slot, pageSize, offset: 0, count: true)).ToList();
            var outcome = await CallInBatchesAsync(group.Key, queries, DeadlineBudget(deadline), cancellationToken).ConfigureAwait(false);

            if (outcome.Failure is not null)
                return Unanswered(group.Key, outcome);

            for (var index = 0; index < members.Count; index++)
            {
                var slot = members[index];
                var plan = plans[slot];
                var result = index < outcome.Results.Count ? outcome.Results[index] : null;

                if (result is null || !Succeeded(result))
                    return Refused(result, TargetOf(slot), StageIndexOf(compiled.Bound, slot));

                var total = CountOf(result);

                // A capped count says "at least this many", so either form refuses once what it
                // reports is already past the cap; below that a capped count is indeterminate.
                if (total is { Value: var reported } && reported > cap)
                    return TooLarge(slot, cap, StageIndexOf(compiled.Bound, slot));

                var full = Take(plan, result, slot, pageSize);

                if (full is null)
                    return WithoutKey(TargetOf(slot), TargetFieldOf(slot), StageIndexOf(compiled.Bound, slot));

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
                var queries = entries.Select(entry => SemiJoinQuery(entry.Slot, pageSize, entry.Offset, count: false)).ToList();

                return (Entries: entries, Service: group.Key, Outcome: await CallInBatchesAsync(group.Key, queries, DeadlineBudget(deadline), cancellationToken).ConfigureAwait(false));
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
                        return Refused(result, TargetOf(slot), StageIndexOf(compiled.Bound, slot));

                    if (plan.Ids.Count > cap)
                        return TooLarge(slot, cap, StageIndexOf(compiled.Bound, slot));

                    var full = Take(plan, result, slot, pageSize);

                    if (full is null)
                        return WithoutKey(TargetOf(slot), TargetFieldOf(slot), StageIndexOf(compiled.Bound, slot));

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
            semiJoinIds.Set(CacheKeyOf(slot, organisation, pageSize), plan.Ids);
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

    /// <summary>Adds one owner page to a plan; true when the page was full, so another may follow.</summary>
    /// <summary>Adds the owner's page of ids to the plan; true when the page was full, null when a row lacks the target field.</summary>
    private static bool? Take(SlotPlan plan, JsonNode result, SemiJoinSlot slot, int pageSize)
    {
        var page = IdsOf(result, slot);

        if (page is null)
            return null;

        plan.Ids.AddRange(page);

        return page.Count >= pageSize;
    }

    private static string TargetFieldOf(SemiJoinSlot slot) =>
        ((ShapeNode.Remote)slot.Leaf.Path.Root).Reference.Path?.Reference?.TargetField ?? "id";

    /// <summary>
    /// The cache key of a slot: the organisation and the first-page query the owner is sent, which
    /// between them determine every wire value the owner answers with.
    /// </summary>
    private static string CacheKeyOf(SemiJoinSlot slot, Guid organisation, int pageSize) =>
        SemiJoinCache.KeyOf(organisation, SemiJoinQuery(slot, pageSize, offset: 0, count: true));

    /// <summary>Fills a slot with the owner's wire values, encoded as this slot's reference member is stored.</summary>
    private static void Fill(SemiJoinSlot slot, IReadOnlyList<string> values)
    {
        var reference = ((ShapeNode.Remote)slot.Leaf.Path.Root).Reference;

        foreach (var value in values)
            slot.Ids.Add(OwnerValueToBson(value, reference));
    }

    /// <summary>The target entity a semi-join leaf reaches through.</summary>
    private static string TargetOf(SemiJoinSlot slot) => ((ShapeNode.Remote)slot.Leaf.Path.Root).TargetEntity;

    /// <summary>What is left of the phase for one call, under the per-call ceiling.</summary>
    private TimeSpan DeadlineBudget(DateTime deadline)
    {
        var left = deadline - DateTime.UtcNow;
        var ceiling = TimeSpan.FromMilliseconds(options.Execution.EffectiveResolveTimeoutMs);

        return left <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : left < ceiling ? left : ceiling;
    }

    private static Refusal Unanswered(string service, CallOutcome outcome) =>
        Refusal.NotExecutable(Codes.ResolveUnavailable, outcome.Status is { } status
            ? $"The owner of '{service}' answered the semi-join with HTTP {status}; the condition cannot be evaluated."
            : $"The owner of '{service}' did not answer the semi-join ({outcome.Failure}); the condition cannot be evaluated.");

    private static Refusal TooLarge(SemiJoinSlot slot, int cap, int? stage) =>
        Refusal.NotExecutable(Codes.SemiJoinTooLarge, $"The condition on '{slot.Leaf.Path.Wire}' selects more than {cap} rows of '{TargetOf(slot)}'; narrow it.", stage);

    /// <summary>The count an owner reported for a page, when one was asked for.</summary>
    private static (long Value, bool Capped)? CountOf(JsonNode result)
    {
        if (result["pageInfo"] is not JsonObject info || info["totalCount"] is not { } total)
            return null;

        return (total.GetValue<long>(), info["totalCountCapped"]?.GetValue<bool>() == true);
    }

    /// <summary>The target field's wire values of one owner page.</summary>
    /// <summary>The owner's ids in wire form, or null when a row does not carry the target field the host projected.</summary>
    private static List<string>? IdsOf(JsonNode result, SemiJoinSlot slot)
    {
        var field = TargetFieldOf(slot);
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

    private static QueryRequest SemiJoinQuery(SemiJoinSlot slot, int pageSize, int offset, bool count)
    {
        var remote = (ShapeNode.Remote)slot.Leaf.Path.Root;
        var alias = remote.StoragePrefix;
        var relative = slot.Leaf.Path.Wire.StartsWith(alias + ".", StringComparison.Ordinal) ? slot.Leaf.Path.Wire[(alias.Length + 1)..] : slot.Leaf.Path.Wire;
        var field = remote.Reference.Path?.Reference?.TargetField ?? "id";
        var operand = slot.Leaf.Operand is BoundOperand.Raw raw ? raw.Value : JsonSerializer.SerializeToElement((object?)null);

        var condition = new FilterCondition
        {
            Path = relative,
            Op = slot.Leaf.Op,
            Value = operand,
            // The owner binds the comparison under its own default; only what the caller wrote travels.
            Options = slot.Leaf.IgnoreCase switch
            {
                true => new FilterConditionOptions { IgnoreCase = true },
                false => new FilterConditionOptions { CaseSensitive = true },
                null => null,
            },
        };

        return new QueryRequest
        {
            EntityType = remote.TargetEntity,
            Pipeline =
            [
                new PipelineStage { Match = new MatchStage { Condition = condition }, Keys = ["match"] },
                new PipelineStage { Project = new ProjectStage { Fields = new Dictionary<string, int>(StringComparer.Ordinal) { [field] = 1 } }, Keys = ["project"] },
                new PipelineStage { Page = new PageStage { Limit = pageSize, Offset = offset == 0 ? null : offset, IncludeTotalCount = count }, Keys = ["page"] },
            ],
        };
    }

    // ---- resolve (after the page) -----------------------------------------------------------

    /// <summary>Resolves every remote stage over the trimmed page rows.</summary>
    public async Task<ResolveResult> ResolveAsync(CompiledQuery compiled, IReadOnlyList<BsonDocument> rows, RequestContext context, TimeSpan remaining, CancellationToken cancellationToken)
    {
        var stages = compiled.RemoteResolves;
        var perRow = rows.Select(_ => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)).ToList();
        var diagnostics = new List<Diagnostic>();
        var plans = new List<StagePlan>();
        var cacheHits = 0;

        foreach (var stage in stages)
        {
            var plan = Plan(stage, rows, context.Organisation!.Value, diagnostics);

            cacheHits += plan.CacheHits;
            plans.Add(plan);
        }

        var calls = 0;
        var budget = Budget(remaining);

        var byService = plans.Where(plan => plan.Chunks.Count > 0).GroupBy(plan => ServiceKeyOf(plan.Stage.TargetEntity), StringComparer.Ordinal).ToList();
        var tasks = byService.Select(async group =>
        {
            var queries = new List<QueryRequest>();
            var owners = new List<(StagePlan Plan, IReadOnlyList<string> Keys)>();

            foreach (var plan in group)
                foreach (var chunk in plan.Chunks)
                {
                    queries.Add(ResolveQuery(plan.Stage, chunk));
                    owners.Add((plan, chunk));
                }

            return (Service: group.Key, Owners: owners, Outcome: await CallInBatchesAsync(group.Key, queries, budget, cancellationToken).ConfigureAwait(false));
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var call in tasks.Select(task => task.Result))
        {
            calls++;

            if (call.Outcome.Failure is { } failure)
            {
                var stagesHit = call.Owners.Select(owner => owner.Plan.Stage.As).Distinct().ToList();

                diagnostics.Add(new Diagnostic
                {
                    Code = failure == Failure.Timeout ? Codes.ResolveTimeout : Codes.ResolveUnreachable,
                    Message = failure == Failure.Timeout
                        ? $"The owner of '{call.Service}' did not answer within {options.Execution.EffectiveResolveTimeoutMs} ms; '{string.Join("', '", stagesHit)}' is null on this page."
                        : call.Outcome.Status is { } status
                            ? $"The owner of '{call.Service}' answered with HTTP {status}; '{string.Join("', '", stagesHit)}' is null on this page."
                            : $"The owner of '{call.Service}' could not be reached; '{string.Join("', '", stagesHit)}' is null on this page.",
                    Params = new Dictionary<string, object?> { ["service"] = call.Service, ["aliases"] = stagesHit },
                });
                continue;
            }

            for (var index = 0; index < call.Owners.Count; index++)
            {
                var (plan, keys) = call.Owners[index];
                var result = index < call.Outcome.Results.Count ? call.Outcome.Results[index] : null;

                if (result is null || !Succeeded(result))
                    return new ResolveResult { Refusal = Refused(result, plan.Stage.TargetEntity, StageIndexOf(compiled.Bound, plan.Stage)), Calls = calls, CacheHits = cacheHits };

                var field = plan.Stage.TargetField;
                var byKey = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

                foreach (var item in result["items"]!.AsArray())
                {
                    if (item is not JsonObject row || !row.ContainsKey(field))
                        return new ResolveResult { Refusal = WithoutKey(plan.Stage.TargetEntity, field, StageIndexOf(compiled.Bound, plan.Stage)), Calls = calls, CacheHits = cacheHits };

                    var key = row[field]?.ToString();

                    if (key is not null)
                        byKey[key] = item;
                }

                foreach (var key in keys)
                {
                    byKey.TryGetValue(key, out var row);
                    plan.Resolved[key] = row;
                    cache.Set(plan.CacheKey(key), row);
                }
            }
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            foreach (var plan in plans)
            {
                var key = plan.KeyOfRow[rowIndex];

                perRow[rowIndex][plan.Stage.As] = key is not null && plan.Resolved.TryGetValue(key, out var row) ? row?.DeepClone() : null;
            }

        return new ResolveResult { Rows = perRow, Diagnostics = diagnostics, Calls = calls, CacheHits = cacheHits };
    }

    /// <summary>One resolve stage over the page: the key per row, the cache answers, and the chunks still to fetch.</summary>
    private sealed class StagePlan(BoundStage.Resolve stage, string selectHash, string filterHash, Guid organisation)
    {
        public BoundStage.Resolve Stage { get; } = stage;

        public List<string?> KeyOfRow { get; } = [];

        public Dictionary<string, JsonNode?> Resolved { get; } = new(StringComparer.Ordinal);

        public List<IReadOnlyList<string>> Chunks { get; } = [];

        public int CacheHits { get; set; }

        public string CacheKey(string key) => ResolveCache.KeyOf(Stage.TargetEntity, Stage.TargetField, organisation, key, selectHash, filterHash);
    }

    private StagePlan Plan(BoundStage.Resolve stage, IReadOnlyList<BsonDocument> rows, Guid organisation, List<Diagnostic> diagnostics)
    {
        // The select is hashed as a JSON array: a joined string would give two different lists
        // whose paths contain the separator the same hash.
        var selectHash = ResolveCache.HashOf(stage.RemoteSelect is null ? null : JsonSerializer.Serialize(stage.RemoteSelect));
        var filterHash = ResolveCache.HashOf(stage.RemoteFilter?.GetRawText());
        var plan = new StagePlan(stage, selectHash, filterHash, organisation);
        var misses = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var key = KeyOfRow(row, stage.Reference);

            plan.KeyOfRow.Add(key);

            if (key is null || !seen.Add(key))
                continue;

            if (cache.TryGet(plan.CacheKey(key), out var cached))
            {
                plan.Resolved[key] = cached;
                plan.CacheHits++;
            }
            else
            {
                misses.Add(key);
            }
        }

        var max = options.Limits.MaxResolveKeys;

        if (misses.Count > max)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = Codes.ResolvePartial,
                Message = $"'{stage.As}' needs {misses.Count} keys of '{stage.TargetEntity}'; only the first {max} are resolved on this page.",
                Params = new Dictionary<string, object?> { ["alias"] = stage.As, ["keys"] = misses.Count, ["max"] = max },
            });
            misses = misses.Take(max).ToList();
        }

        var chunk = Math.Max(1, options.Limits.ResolveKeyChunk);

        for (var start = 0; start < misses.Count; start += chunk)
            plan.Chunks.Add(misses.Skip(start).Take(chunk).ToList());

        return plan;
    }

    private static QueryRequest ResolveQuery(BoundStage.Resolve stage, IReadOnlyList<string> keys)
    {
        var pipeline = new List<PipelineStage>
        {
            new()
            {
                Match = new MatchStage { Condition = new FilterCondition { Path = stage.TargetField, Op = "in", Value = JsonSerializer.SerializeToElement(keys) } },
                Keys = ["match"],
            },
        };

        if (stage.RemoteFilter is { } filter && filter.ValueKind == JsonValueKind.Object)
            pipeline.Add(new PipelineStage { Match = JsonSerializer.Deserialize<MatchStage>(filter.GetRawText(), OxQLJson.Wire)!, Keys = ["match"] });

        // A projection always travels. With a select it is the caller's; without one it is
        // the reserved $default key, which the owner expands to its own entity's key and
        // display members — the pair the local half of this stage keeps. Without a projection
        // the owner answers with whole documents, organizationId and every other member
        // included, to a caller that wanted a label.
        var projection = stage.RemoteSelect is { Count: > 0 } select
            ? select.ToDictionary(path => path, _ => 1, StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal) { ["$default"] = 1 };

        projection[stage.TargetField] = 1;
        pipeline.Add(new PipelineStage { Project = new ProjectStage { Fields = projection }, Keys = ["project"] });

        pipeline.Add(new PipelineStage { Page = new PageStage { Limit = keys.Count }, Keys = ["page"] });

        return new QueryRequest { EntityType = stage.TargetEntity, Pipeline = pipeline };
    }

    // ---- values ---------------------------------------------------------------------------

    /// <summary>The reference member's value of a row in the wire encoding the owner binds, or null.</summary>
    private static string? KeyOfRow(BsonDocument row, ResolvedPath reference)
    {
        var value = ValueAt(row, reference.Storage!);

        if (value is null || value.IsBsonNull || value.IsBsonUndefined)
            return null;

        var encoded = WireEncoder.EncodeScalar(value, reference.LeafKind, reference.Leaf);

        return encoded switch
        {
            null => null,
            JsonValue scalar => scalar.ToString(),
            _ => encoded.ToJsonString(),
        };
    }

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

    private TimeSpan Budget(TimeSpan remaining)
    {
        var ceiling = TimeSpan.FromMilliseconds(options.Execution.EffectiveResolveTimeoutMs);

        if (remaining <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(1);

        return remaining < ceiling ? remaining : ceiling;
    }

    /// <summary>
    /// The queries for one owner, in batches no larger than the batch cap (the owner's is
    /// assumed equal to this host's), each within the budget, results concatenated in order.
    /// </summary>
    private async Task<CallOutcome> CallInBatchesAsync(string service, IReadOnlyList<QueryRequest> queries, TimeSpan budget, CancellationToken cancellationToken)
    {
        var size = Math.Max(1, options.Limits.MaxBatchQueries);
        var results = new List<JsonNode?>(queries.Count);

        for (var start = 0; start < queries.Count; start += size)
        {
            var request = new BatchRequest { Queries = queries.Skip(start).Take(size).ToList(), MaxTimeMs = (int)budget.TotalMilliseconds };
            var outcome = await CallAsync(service, request, budget, cancellationToken).ConfigureAwait(false);

            if (outcome.Failure is not null)
                return outcome;

            results.AddRange(outcome.Results);
        }

        return new CallOutcome(results, null);
    }

    /// <summary>One call to one owner within the budget; a timeout or a transport fault is an outcome, never an exception.</summary>
    private async Task<CallOutcome> CallAsync(string service, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(budget);

        try
        {
            var response = await client.BatchAsync(service, request, budget, timeout.Token).ConfigureAwait(false);

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
