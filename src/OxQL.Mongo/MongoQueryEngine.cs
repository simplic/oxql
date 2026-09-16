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
    private readonly IRemoteQueryClient? remote;
    private readonly ILogger<MongoQueryEngine> logger;
    private readonly bool includeErrorDetails;

    public MongoQueryEngine(
        IEntityModelProvider models,
        IAggregateRunner runner,
        CursorCodec cursors,
        OxQLOptions options,
        IRemoteQueryClient? remote = null,
        ILogger<MongoQueryEngine>? logger = null,
        bool includeErrorDetails = false)
    {
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.remote = remote;
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
            Log(bound, timer, 0, timedOut, false, context, refusal);

            return QueryOutcome.Of(refusal);
        }

        var hasNextPage = rows.Count > compiled.Limit;
        var page = hasNextPage ? rows.Take(compiled.Limit).ToList() : rows;

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

        foreach (var row in page)
            items.Add(WireEncoder.Encode(row, bound));

        var result = new QueryResult
        {
            Items = items,
            PageInfo = new PageInfo { HasNextPage = hasNextPage, NextCursor = nextCursor, TotalCount = totalCount, TotalCountCapped = capped },
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
        };

        Log(bound, timer, page.Count, timedOut, capped == true, context, null);

        return QueryOutcome.Of(result);
    }

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
            Stages = compiled.PageStages.Select(stage => JsonNode.Parse(stage.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }))!).ToList(),
            Count = compiled.CountStages?.Select(stage => JsonNode.Parse(stage.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }))!).ToList(),
            Diagnostics = bound.Diagnostics.Count > 0 ? bound.Diagnostics : null,
        });
    }

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

    private static BsonValue ValueAt(BsonDocument document, string storage)
    {
        BsonValue current = document;

        foreach (var segment in storage.Split('.'))
        {
            if (current is BsonDocument inner && inner.TryGetValue(segment, out var next))
                current = next;
            else
                return BsonNull.Value;
        }

        return current;
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

            default:
                logger.LogError(exception, "OxQL engine fault");
                return Refusal.Internal(includeErrorDetails ? exception.Message : null);
        }
    }

    private void Log(BoundPipeline bound, Stopwatch timer, int rows, bool timedOut, bool countCapped, RequestContext context, Refusal? refusal)
    {
        logger.LogInformation(
            "OxQL {Entity} stages={Stages} rows={Rows} elapsedMs={ElapsedMs} timedOut={TimedOut} countCapped={CountCapped} resolveCalls={ResolveCalls} compat={Compat} user={UserId} org={OrganisationId} correlation={CorrelationId} outcome={Outcome}",
            bound.Entity.Id,
            string.Join(",", bound.Stages.Select(stage => stage.GetType().Name.ToLowerInvariant())),
            rows,
            timer.Elapsed.TotalMilliseconds,
            timedOut,
            countCapped,
            0,
            context.Contract == 1,
            context.UserId,
            context.Organisation,
            context.CorrelationId,
            refusal?.Type ?? "ok");
    }
}
