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
    private readonly RemoteResolver? remote;
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
        ResolveCache? cache = null)
    {
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.remote = remote is null ? null : new RemoteResolver(remote, cache ?? new ResolveCache(this.options), this.options);
        this.indexes = indexes;
        this.logger = logger ?? NullLogger<MongoQueryEngine>.Instance;
        this.includeErrorDetails = includeErrorDetails;
    }

    /// <inheritdoc/>
    public bool RemoteResolve => remote is not null;

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var binding = await new Binder(models.Model, cursors).BindAsync(request, context, cancellationToken);

        if (binding is BindOutcome.Failed failed)
            return QueryOutcome.Of(failed.Refusal);

        var bound = ((BindOutcome.Bound)binding).Pipeline;
        var compiled = MongoCompiler.Compile(bound, CompileOptionsFor(context));

        if ((compiled.RemoteResolves.Count > 0 || compiled.SemiJoins.Count > 0) && remote is null)
            return QueryOutcome.Of(Refusal.NotExecutable(Codes.ResolveUnavailable, "This host has no remote query client; a remote resolve cannot run."));

        var diagnostics = new List<Diagnostic>(bound.Diagnostics);
        var runOptions = new AggregateRunOptions(compiled.MaxTimeMs, compiled.AllowDiskUse);
        var resolveCalls = 0;
        var cacheHits = 0;

        // The semi-joins fill their slots before the page runs; without the ids the filter cannot be evaluated.
        if (compiled.SemiJoins.Count > 0)
        {
            var refused = await remote!.SemiJoinAsync(compiled, context, Remaining(compiled, timer), cancellationToken).ConfigureAwait(false);

            resolveCalls += compiled.SemiJoins.Select(slot => RemoteResolver.ServiceKeyOf(((ShapeNode.Remote)slot.Leaf.Path.Root).TargetEntity)).Distinct().Count();

            if (refused is not null)
            {
                Log(bound, timer, 0, false, false, context, refused, resolveCalls, 0);
                return QueryOutcome.Of(refused);
            }
        }

        IReadOnlyList<BsonDocument> rows;
        IReadOnlyList<BsonDocument>? countRows = null;
        var timedOut = false;

        try
        {
            var pageTask = runner.AggregateAsync(bound.Entity, compiled.PageStages, runOptions, cancellationToken);
            var countTask = compiled.CountStages is not null ? runner.AggregateAsync(bound.Entity, compiled.CountStages, runOptions, cancellationToken) : null;

            rows = await pageTask.ConfigureAwait(false);

            if (countTask is not null)
                countRows = await countTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var refusal = MapDriverError(exception);

            timedOut = refusal.Status == 504;
            Log(bound, timer, 0, timedOut, false, context, refusal, resolveCalls, 0);

            return QueryOutcome.Of(refusal);
        }

        var hasNextPage = rows.Count > compiled.Limit;
        var page = hasNextPage ? rows.Take(compiled.Limit).ToList() : rows;

        // Remote resolves run over the trimmed page; an owner that does not answer yields null rows and a diagnostic, never a failed page.
        IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>>? resolved = null;

        if (compiled.RemoteResolves.Count > 0)
        {
            var resolution = await remote!.ResolveAsync(compiled, page, context, Remaining(compiled, timer), cancellationToken).ConfigureAwait(false);

            resolveCalls += resolution.Calls;
            cacheHits += resolution.CacheHits;

            if (resolution.Refusal is not null)
            {
                Log(bound, timer, 0, false, false, context, resolution.Refusal, resolveCalls, cacheHits);
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

        // Contract 1 rows come back as the driver returned them, through the v1 converter; a key
        // the compiler kept against the caller's projection is theirs to lose again, and the
        // cursor above has already read it.
        for (var index = 0; index < page.Count; index++)
        {
            if (context.Contract == 1)
            {
                if (compiled.KeyKeptAgainstProjection)
                    page[index].Remove("_id");

                items.Add(CompatRows.Encode(page[index]));
            }
            else
            {
                items.Add(WireEncoder.Encode(page[index], bound, resolved?[index]));
            }
        }

        var result = new QueryResult
        {
            Items = items,
            PageInfo = new PageInfo { HasNextPage = hasNextPage, NextCursor = nextCursor, TotalCount = totalCount, TotalCountCapped = capped },
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
        };

        Log(bound, timer, page.Count, timedOut, capped == true, context, null, resolveCalls, cacheHits);

        return QueryOutcome.Of(result);
    }

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
        var explain = hasLookup ? await indexes.ExplainAsync(entity, compiled.PageStages, cancellationToken).ConfigureAwait(false) : null;

        return IndexAdvisor.Advise(compiled.PageStages, listed, explain);
    }

    private static JsonNode Relaxed(BsonDocument stage) =>
        JsonNode.Parse(stage.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }))!;

    private CompileOptions CompileOptionsFor(RequestContext context)
    {
        var maxTime = options.Execution.EffectiveMaxTimeMs;

        if (context.MaxTimeMs is { } requested && requested > 0)
            maxTime = Math.Min(maxTime, requested);

        return new CompileOptions(maxTime, options.Execution.AllowDiskUse, options.Limits.CountCap);
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

    private static BsonValue ValueAt(BsonDocument document, string storage) =>
        RemoteResolver.ValueAt(document, storage) ?? BsonNull.Value;

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

            default:
                logger.LogError(exception, "OxQL engine fault");
                return Refusal.Internal(includeErrorDetails ? exception.Message : null);
        }
    }

    private void Log(BoundPipeline bound, Stopwatch timer, int rows, bool timedOut, bool countCapped, RequestContext context, Refusal? refusal, int resolveCalls, int resolveCacheHits)
    {
        logger.LogInformation(
            "OxQL {Entity} stages={Stages} rows={Rows} elapsedMs={ElapsedMs} timedOut={TimedOut} countCapped={CountCapped} resolveCalls={ResolveCalls} resolveCacheHits={ResolveCacheHits} compat={Compat} user={UserId} org={OrganisationId} correlation={CorrelationId} outcome={Outcome}",
            bound.Entity.Id,
            string.Join(",", bound.Stages.Select(stage => stage.GetType().Name.ToLowerInvariant())),
            rows,
            timer.Elapsed.TotalMilliseconds,
            timedOut,
            countCapped,
            resolveCalls,
            resolveCacheHits,
            context.Contract == 1,
            context.UserId,
            context.Organisation,
            context.CorrelationId,
            refusal?.Type ?? "ok");
    }
}
