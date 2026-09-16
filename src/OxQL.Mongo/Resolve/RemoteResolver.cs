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
/// The remote half of resolve (design §11): after the page is fixed, one batch per owning
/// service carrying one query per resolve stage and key chunk, cached per key for a TTL;
/// and, before the page runs, the semi-join that asks an owner for the ids a condition on
/// its rows selects, refusing above the cap rather than truncating.
/// </summary>
public sealed class RemoteResolver
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly IRemoteQueryClient client;
    private readonly ResolveCache cache;
    private readonly OxQLOptions options;

    public RemoteResolver(IRemoteQueryClient client, ResolveCache cache, OxQLOptions options)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The service key a target entity is owned by: its namespace, the host's <c>InternalHosts</c> key.</summary>
    public static string ServiceKeyOf(string targetEntity) => targetEntity.Split('.')[0];

    // ---- semi-join (before the page) --------------------------------------------------------

    /// <summary>
    /// Fills every semi-join slot with the ids the owner selects for the leaf's condition: one
    /// query per leaf, batched per service. More than <c>MaxSemiJoinIds</c> is refused, an
    /// owner refusal is the caller's refusal, and an owner that does not answer refuses the
    /// request too: without the ids the filter cannot be evaluated, and the engine never
    /// executes something else instead.
    /// </summary>
    public async Task<Refusal?> SemiJoinAsync(CompiledQuery compiled, TimeSpan remaining, CancellationToken cancellationToken)
    {
        var slots = compiled.SemiJoins;

        if (slots.Count == 0)
            return null;

        var cap = options.Limits.MaxSemiJoinIds;
        var byService = slots.GroupBy(slot => ServiceKeyOf(((ShapeNode.Remote)slot.Leaf.Path.Root).TargetEntity), StringComparer.Ordinal).ToList();
        var budget = Budget(remaining);

        var calls = byService.Select(async group =>
        {
            var queries = group.Select(slot => SemiJoinQuery(slot, cap)).ToList();
            var request = new BatchRequest { Queries = queries, MaxTimeMs = (int)budget.TotalMilliseconds };

            return (Service: group.Key, Slots: group.ToList(), Outcome: await CallAsync(group.Key, request, budget, cancellationToken).ConfigureAwait(false));
        }).ToList();

        await Task.WhenAll(calls).ConfigureAwait(false);

        foreach (var call in calls.Select(task => task.Result))
        {
            if (call.Outcome.Failure is { } failure)
                return Refusal.NotExecutable(Codes.ResolveUnavailable, $"The owner of '{call.Service}' did not answer the semi-join ({failure}); the condition cannot be evaluated.");

            for (var index = 0; index < call.Slots.Count; index++)
            {
                var slot = call.Slots[index];
                var remote = (ShapeNode.Remote)slot.Leaf.Path.Root;
                var stageIndex = StageIndexOf(compiled.Bound, slot);
                var result = index < call.Outcome.Results.Count ? call.Outcome.Results[index] : null;

                if (result is null || !Succeeded(result))
                    return Refused(result, remote.TargetEntity, stageIndex);

                var items = result["items"]!.AsArray();

                if (items.Count > cap)
                    return Refusal.NotExecutable(Codes.SemiJoinTooLarge, $"The condition on '{slot.Leaf.Path.Wire}' selects more than {cap} rows of '{remote.TargetEntity}'; narrow it.", stageIndex);

                var reference = remote.Reference;
                var field = reference.Path?.Reference?.TargetField ?? "id";

                foreach (var item in items)
                {
                    var value = item?[field];

                    if (value is not null)
                        slot.Ids.Add(OwnerValueToBson(value, reference));
                }
            }
        }

        return null;
    }

    private static QueryRequest SemiJoinQuery(SemiJoinSlot slot, int cap)
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
            Options = slot.Leaf.IgnoreCase ? new FilterConditionOptions { IgnoreCase = true } : null,
        };

        return new QueryRequest
        {
            EntityType = remote.TargetEntity,
            Pipeline =
            [
                new PipelineStage { Match = new MatchStage { Condition = condition }, Keys = ["match"] },
                new PipelineStage { Project = new ProjectStage { Fields = new Dictionary<string, int>(StringComparer.Ordinal) { [field] = 1 } }, Keys = ["project"] },
                new PipelineStage { Page = new PageStage { Limit = Math.Min(cap + 1, int.MaxValue) }, Keys = ["page"] },
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

            var request = new BatchRequest { Queries = queries, MaxTimeMs = (int)budget.TotalMilliseconds };

            return (Service: group.Key, Owners: owners, Outcome: await CallAsync(group.Key, request, budget, cancellationToken).ConfigureAwait(false));
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
                    var key = item?[field]?.ToString();

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

        public string CacheKey(string key) => ResolveCache.KeyOf(Stage.TargetEntity, organisation, key, selectHash, filterHash);
    }

    private StagePlan Plan(BoundStage.Resolve stage, IReadOnlyList<BsonDocument> rows, Guid organisation, List<Diagnostic> diagnostics)
    {
        var selectHash = ResolveCache.HashOf(stage.RemoteSelect is null ? null : string.Join(",", stage.RemoteSelect));
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
            pipeline.Add(new PipelineStage { Match = JsonSerializer.Deserialize<MatchStage>(filter.GetRawText(), Wire)!, Keys = ["match"] });

        if (stage.RemoteSelect is { Count: > 0 } select)
        {
            var fields = select.ToDictionary(path => path, _ => 1, StringComparer.Ordinal);

            fields[stage.TargetField] = 1;
            pipeline.Add(new PipelineStage { Project = new ProjectStage { Fields = fields }, Keys = ["project"] });
        }

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
    private static BsonValue OwnerValueToBson(JsonNode value, ResolvedPath reference)
    {
        var text = value is JsonValue scalar ? scalar.ToString() : value.ToJsonString();
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

    private sealed record CallOutcome(IReadOnlyList<JsonNode?> Results, Failure? Failure);

    private TimeSpan Budget(TimeSpan remaining)
    {
        var ceiling = TimeSpan.FromMilliseconds(options.Execution.EffectiveResolveTimeoutMs);

        if (remaining <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(1);

        return remaining < ceiling ? remaining : ceiling;
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
