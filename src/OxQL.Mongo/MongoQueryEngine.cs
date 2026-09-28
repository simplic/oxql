using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo.Compat;
using OxQL.Mongo.Explain;
using OxQL.Mongo.Resolve;

namespace OxQL.Mongo;

/// <summary>
/// The MongoDB engine: bind, compile, run the page and the count concurrently under
/// <c>maxTimeMS</c>, resolve remote targets, build the next cursor, encode the rows.
/// </summary>
public sealed class MongoQueryEngine : IQueryEngine, IEngineFeatures
{
    private readonly IEntityModelProvider models;
    private readonly IAggregateRunner runner;
    private readonly CursorCodec cursors;
    private readonly OxQLOptions options;
    private readonly KeyedFetch fetch;
    private readonly bool remoteClient;
    private readonly IIndexSource? indexes;
    private readonly ILogger<MongoQueryEngine> logger;
    private readonly bool includeErrorDetails;

    public MongoQueryEngine(
        IEntityModelProvider models,
        IAggregateRunner runner,
        CursorCodec cursors,
        OxQLOptions options,
        IRemoteQueryClient? remote = null,
        ILogger<MongoQueryEngine>? logger = null,
        bool includeErrorDetails = false,
        IIndexSource? indexes = null,
        OwnerFetchCache? cache = null)
    {
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        // The keyed fetch always exists: this host answers its own targets (SelfOwner); only a
        // remote target needs the remote client.
        fetch = new KeyedFetch(remote, this, cache ?? new OwnerFetchCache(this.options), this.options);
        remoteClient = remote is not null;
        this.indexes = indexes;
        this.logger = logger ?? NullLogger<MongoQueryEngine>.Instance;
        this.includeErrorDetails = includeErrorDetails;
    }

    /// <inheritdoc/>
    public bool RemoteResolve => remoteClient;

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var binding = await new Binder(models.Model, cursors).BindAsync(request, context, cancellationToken);

        if (binding is BindOutcome.Failed failed)
            return QueryOutcome.Of(failed.Refusal);

        var bound = ((BindOutcome.Bound)binding).Pipeline;
        var compiled = MongoCompiler.Compile(bound, CompileOptionsFor(context));

        if ((compiled.KeyedResolves.Any(resolve => resolve.IsRemote) || compiled.SemiJoins.Count > 0) && !remoteClient)
            return QueryOutcome.Of(Refusal.NotExecutable(Codes.ResolveUnavailable, "This host has no remote query client; a remote resolve cannot run."));

        var diagnostics = new List<Diagnostic>(bound.Diagnostics);
        var runOptions = new AggregateRunOptions(compiled.MaxTimeMs, compiled.AllowDiskUse, compiled.Collation);
        var resolveCalls = 0;
        var cacheHits = 0;

        // The semi-joins fill their slots before the page runs; without the ids the filter cannot be evaluated.
        if (compiled.SemiJoins.Count > 0)
        {
            var refused = await fetch.ByConditionAsync(compiled, context, Remaining(compiled, timer), cancellationToken).ConfigureAwait(false);

            resolveCalls += compiled.SemiJoins.Select(slot => KeyedFetch.ServiceKeyOf(((ShapeNode.Remote)slot.Leaf.Path.Root).TargetEntity)).Distinct().Count();

            if (refused is not null)
            {
                Log(compiled, timer, 0, false, false, context, refused, resolveCalls, 0);
                return QueryOutcome.Of(refused);
            }
        }

        IReadOnlyList<BsonDocument> rows;
        IReadOnlyList<BsonDocument>? countRows = null;
        var timedOut = false;

        // The page and the count run together and end together: when one fails the other is
        // cancelled rather than left to hold a connection until its own time budget runs out.
        using var aggregates = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<BsonDocument>>? pageTask = null;
        Task<IReadOnlyList<BsonDocument>>? countTask = null;

        try
        {
            pageTask = runner.AggregateAsync(bound.Entity, compiled.PageStages, runOptions, aggregates.Token);
            countTask = compiled.CountStages is not null ? runner.AggregateAsync(bound.Entity, compiled.CountStages, runOptions, aggregates.Token) : null;

            rows = await pageTask.ConfigureAwait(false);

            if (countTask is not null)
                countRows = await countTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            aggregates.Cancel();
            Observe(pageTask);
            Observe(countTask);

            var refusal = MapDriverError(exception);

            timedOut = refusal.Status == 504;
            Log(compiled, timer, 0, timedOut, false, context, refusal, resolveCalls, 0);

            return QueryOutcome.Of(refusal);
        }

        var hasNextPage = rows.Count > compiled.Limit;
        var page = hasNextPage ? rows.Take(compiled.Limit).ToList() : rows;

        // A flattening unwind marks the rows whose collection nests deeper than it descends.
        diagnostics.AddRange(MongoCompiler.DepthTruncations(compiled, page));

        // A lookup marks the rows with a parent over its limit; the marks leave the rows here.
        diagnostics.AddRange(MongoCompiler.LookupTruncations(compiled, page));

        // Keyed resolves run over the trimmed page, remote targets at their owners and local ones
        // through this host's SelfOwner; an owner that does not answer yields null rows and a
        // diagnostic, never a failed page.
        IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>>? resolved = null;

        if (compiled.KeyedResolves.Count > 0)
        {
            var resolution = await fetch.ByKeysAsync(compiled, page, context, Remaining(compiled, timer), cancellationToken).ConfigureAwait(false);

            resolveCalls += resolution.Calls;
            cacheHits += resolution.CacheHits;

            if (resolution.Refusal is not null)
            {
                Log(compiled, timer, 0, false, false, context, resolution.Refusal, resolveCalls, cacheHits);
                return QueryOutcome.Of(resolution.Refusal);
            }

            resolved = resolution.Rows;
            diagnostics.AddRange(resolution.Diagnostics);
        }

        long? totalCount = null;
        bool? capped = null;

        if (compiled.IncludeTotalCount)
        {
            var n = countRows is { Count: > 0 } && countRows[0].TryGetValue("n", out var count) ? count.ToInt64() : 0;

            capped = n > compiled.CountCap;
            totalCount = capped.Value ? compiled.CountCap : n;

            if (capped.Value)
                diagnostics.Add(new Diagnostic { Code = Codes.TotalCountCapped, Message = $"More than {compiled.CountCap} rows match; the count is the cap.", Params = new Dictionary<string, object?> { ["cap"] = compiled.CountCap } });
        }

        var nextCursor = hasNextPage && page.Count > 0 ? cursors.Encode(NextCursor(compiled, page[^1])) : null;
        var items = new List<JsonNode?>(page.Count);
        var unfit = new List<string>();

        // Contract 1 rows come back as the driver returned them, through the v1 converter; a key
        // the compiler kept against the caller's projection is theirs to lose again, and the
        // cursor above has already read it.
        for (var index = 0; index < page.Count; index++)
        {
            if (context.Contract == 1)
            {
                if (compiled.KeyKeptAgainstProjection)
                    page[index].Remove("_id");

                foreach (var storage in compiled.SortKeptAgainstProjection)
                    RemoveAt(page[index], storage);

                items.Add(CompatRows.Encode(page[index]));
            }
            else
            {
                items.Add(WireEncoder.Encode(page[index], bound, resolved?[index], unfit));
            }
        }

        // Stored values outside their kind's range travel verbatim; said once per request, by
        // path and never by value.
        if (unfit.Count > 0)
            logger.LogWarning(
                "OxQL {Entity} returned {Count} stored values verbatim because they do not fit their kind; paths={Paths} org={OrganisationId} correlation={CorrelationId}",
                bound.Entity.Id,
                unfit.Count,
                string.Join(",", unfit.Distinct(StringComparer.Ordinal).Take(10)),
                context.Organisation,
                context.CorrelationId);

        var result = new QueryResult
        {
            Items = items,
            PageInfo = new PageInfo { HasNextPage = hasNextPage, NextCursor = nextCursor, TotalCount = totalCount, TotalCountCapped = capped },
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
        };

        Log(compiled, timer, page.Count, timedOut, capped == true, context, null, resolveCalls, cacheHits);

        return QueryOutcome.Of(result);
    }

    /// <summary>
    /// Reads the failure of an aggregate nobody awaits any more, so it ends here instead of
    /// surfacing later as an unobserved task exception. The refusal does not wait for it.
    /// </summary>
    private static void Observe(Task? abandoned) =>
        abandoned?.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>What is left of the request's time budget for the owners.</summary>
    private static TimeSpan Remaining(CompiledQuery compiled, Stopwatch timer) =>
        TimeSpan.FromMilliseconds(compiled.MaxTimeMs) - timer.Elapsed;

    /// <inheritdoc/>
    public async Task<ExplainOutcome> ExplainAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken = default)
    {
        var binding = await new Binder(models.Model, cursors).BindAsync(request, context, cancellationToken);

        if (binding is BindOutcome.Failed failed)
            return new ExplainOutcome.Refused(failed.Refusal);

        var bound = ((BindOutcome.Bound)binding).Pipeline;
        var compiled = MongoCompiler.Compile(bound, CompileOptionsFor(context));

        return new ExplainOutcome.Success(new ExplainResult
        {
            Bound = JsonNode.Parse(bound.Canonical)!,
            Stages = compiled.PageStages.Select(Relaxed).ToList(),
            Count = compiled.CountStages?.Select(Relaxed).ToList(),
            Collation = compiled.Collation is null ? null : Relaxed(compiled.Collation),
            Advisory = await AdviseAsync(bound.Entity, compiled, cancellationToken).ConfigureAwait(false),
            Diagnostics = bound.Diagnostics.Count > 0 ? bound.Diagnostics : null,
        });
    }

    /// <summary>The index advisory: listIndexes matched against the leading match and the sort, the server's explain for every lookup.</summary>
    private async Task<IReadOnlyList<JsonNode>?> AdviseAsync(EntityDef entity, CompiledQuery compiled, CancellationToken cancellationToken)
    {
        if (indexes is null)
            return null;

        var listed = await indexes.IndexesAsync(entity, cancellationToken).ConfigureAwait(false);
        var hasLookup = compiled.PageStages.Any(stage => stage.Contains("$lookup"));
        var explain = hasLookup ? await indexes.ExplainAsync(entity, compiled.PageStages, compiled.MaxTimeMs, cancellationToken).ConfigureAwait(false) : null;

        return IndexAdvisor.Advise(compiled.PageStages, listed, explain, compiled.Collation);
    }

    private static JsonNode Relaxed(BsonDocument stage) =>
        JsonNode.Parse(stage.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }))!;

    private CompileOptions CompileOptionsFor(RequestContext context)
    {
        var maxTime = options.Execution.EffectiveMaxTimeMs;

        if (context.MaxTimeMs is { } requested && requested > 0)
            maxTime = Math.Min(maxTime, requested);

        return new CompileOptions(maxTime, options.Execution.AllowDiskUse, options.Limits.CountCap, options.Representation.Collation);
    }

    /// <summary>The cursor for the page after <paramref name="last"/>.</summary>
    public static CursorPayload NextCursor(CompiledQuery compiled, BsonDocument last)
    {
        if (compiled.PagingMode == PagingMode.Offset)
            return new CursorPayload(compiled.Bound.Fingerprint, PagingMode.Offset, [], compiled.Offset + compiled.Limit);

        var fields = new List<CursorValue>();

        foreach (var field in compiled.SortFields)
            fields.Add(new CursorValue(field.Path.Wire, field.Ascending, ValueAt(last, field.Path.Storage!)));

        fields.Add(new CursorValue("_id", true, ValueAt(last, "_id")));

        return new CursorPayload(compiled.Bound.Fingerprint, PagingMode.Keyset, fields, 0);
    }

    /// <summary>
    /// A sort leg's value off the page's last row. The compiler keeps every paging sort path
    /// in storage against a projection that would have dropped it, so a member that is not
    /// there is a member the row does not hold — which is a null, and orders with one.
    /// </summary>
    private static BsonValue ValueAt(BsonDocument document, string storage) =>
        KeyedFetch.ValueAt(document, storage) ?? BsonNull.Value;

    /// <summary>Removes a dotted storage path from a document; a contract 1 row loses what only the paging read.</summary>
    private static void RemoveAt(BsonDocument document, string storage)
    {
        var cut = storage.IndexOf('.', StringComparison.Ordinal);

        if (cut < 0)
        {
            document.Remove(storage);
            return;
        }

        if (document.TryGetValue(storage[..cut], out var inner) && inner is BsonDocument nested)
            RemoveAt(nested, storage[(cut + 1)..]);
    }

    private Refusal MapDriverError(Exception exception)
    {
        switch (exception)
        {
            case MongoExecutionTimeoutException:
                return Refusal.Timeout("The query exceeded its time budget.");

            case MongoCommandException command when command.Code == 50:
                return Refusal.Timeout("The query exceeded its time budget.");

            case MongoCommandException command when command.Code is 292 or 16819 or 16820 or 16945:
                return Refusal.NotExecutable(Codes.QueryTooExpensive, "The query needs more memory than the server allows without spilling to disk; add an index for the sort or narrow the match.");

            // An accumulator over its memory cap, which spilling to disk does not lift, and a
            // row that outgrew the document size limit: both are the query's cost, not a fault.
            case MongoCommandException command when command.Code is 146 or 10334:
                return Refusal.NotExecutable(Codes.QueryTooExpensive, "The query builds a value larger than the server allows, typically a push or countDistinct over too many rows; narrow the match or group by more keys.");

            // The binder validates a pattern with the .NET parser and the server compiles it
            // with PCRE2; a construct only the first accepts is rejected here.
            case MongoCommandException command when IsPatternRejection(command):
                return Refusal.Validation([new QueryValidationError { Code = Codes.InvalidRegex, Message = "The database could not compile a regular expression of this query; it accepts PCRE2 syntax." }]);

            default:
                logger.LogError(exception, "OxQL engine fault");
                return Refusal.Internal(includeErrorDetails ? exception.Message : null);
        }
    }

    /// <summary>
    /// Whether the server refused to compile a pattern: its dedicated code, or the generic
    /// <c>BadValue</c> when the message names a regular expression.
    /// </summary>
    private static bool IsPatternRejection(MongoCommandException command) =>
        command.Code == 51091
        || (command.Code == 2 && command.ErrorMessage is { } message && message.Contains("egular expression", StringComparison.Ordinal));

    private void Log(CompiledQuery compiled, Stopwatch timer, int rows, bool timedOut, bool countCapped, RequestContext context, Refusal? refusal, int resolveCalls, int resolveCacheHits)
    {
        var bound = compiled.Bound;
        var stages = string.Join(",", bound.Stages.Select(stage => stage.GetType().Name.ToLowerInvariant()));
        var elapsed = timer.Elapsed.TotalMilliseconds;
        var outcome = refusal?.Type ?? "ok";

        logger.LogInformation(
            "OxQL {Entity} stages={Stages} rows={Rows} elapsedMs={ElapsedMs} timedOut={TimedOut} countCapped={CountCapped} resolveCalls={ResolveCalls} resolveCacheHits={ResolveCacheHits} compat={Compat} user={UserId} org={OrganisationId} correlation={CorrelationId} outcome={Outcome}",
            bound.Entity.Id,
            stages,
            rows,
            elapsed,
            timedOut,
            countCapped,
            resolveCalls,
            resolveCacheHits,
            context.Contract == 1,
            context.UserId,
            context.Organisation,
            context.CorrelationId,
            outcome);

        // A request over the slow-query threshold is said once more, at warning level, with
        // what shaped it: the stage kinds and the flags an operator can act on, never an
        // operand. The time is the whole request's, remote resolves and the count included.
        var threshold = options.Execution.EffectiveSlowQueryMs;

        if (threshold > 0 && elapsed > threshold)
            logger.LogWarning(
                "OxQL slow query {Entity} stages={Stages} elapsedMs={ElapsedMs} thresholdMs={ThresholdMs} rows={Rows} regex={Regex} unboundedSort={UnboundedSort} count={Count} remote={Remote} outcome={Outcome} org={OrganisationId} correlation={CorrelationId}",
                bound.Entity.Id,
                stages,
                elapsed,
                threshold,
                rows,
                HasRegex(compiled.PageStages),
                HasUnboundedSort(compiled.PageStages),
                compiled.IncludeTotalCount,
                compiled.KeyedResolves.Any(resolve => resolve.IsRemote) || compiled.SemiJoins.Count > 0,
                outcome,
                context.Organisation,
                context.CorrelationId);
    }

    /// <summary>Whether any stage of the pipeline, a join's inner pipeline included, matches with a regular expression.</summary>
    private static bool HasRegex(IReadOnlyList<BsonDocument> stages) => stages.Any(HasRegex);

    private static bool HasRegex(BsonValue value) => value switch
    {
        BsonRegularExpression => true,
        BsonDocument document => document.Values.Any(HasRegex),
        BsonArray array => array.Any(HasRegex),
        _ => false,
    };

    /// <summary>
    /// Whether a sort orders every candidate row rather than the page's: one that is not
    /// followed by the page's limit, with only a skip or an unset between them. Such a sort
    /// cannot keep the top rows alone and holds the whole input in memory or on disk.
    /// </summary>
    private static bool HasUnboundedSort(IReadOnlyList<BsonDocument> stages)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            if (!stages[index].Contains("$sort"))
                continue;

            var bounded = false;

            for (var later = index + 1; later < stages.Count && !bounded; later++)
            {
                if (stages[later].Contains("$limit"))
                    bounded = true;
                else if (!stages[later].Contains("$skip") && !stages[later].Contains("$unset"))
                    break;
            }

            if (!bounded)
                return true;
        }

        return false;
    }
}
