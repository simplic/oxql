using System.Diagnostics;
using System.Text.Json;
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
public sealed partial class MongoQueryEngine : IQueryEngine, IEngineFeatures
{
    private readonly IEntityModelProvider models;
    private readonly TimedAggregateRunner runner;
    private readonly CursorCodec cursors;
    private readonly OxQLOptions options;
    private readonly KeyedFetch fetch;
    private readonly bool remoteClient;
    private readonly IRemoteQueryClient? remote;
    private readonly ExplainForwardCache explainCache;
    private readonly OwnerFetchCache ownerCache;
    private readonly IIndexSource? indexes;
    private readonly ILogger<MongoQueryEngine> logger;
    private readonly bool includeErrorDetails;

    /// <summary>The engine over <paramref name="models"/> and <paramref name="runner"/>; a remote client lets it resolve targets of other services.</summary>
    public MongoQueryEngine(
        IEntityModelProvider models,
        IAggregateRunner runner,
        CursorCodec cursors,
        OxQLOptions options,
        IRemoteQueryClient? remote = null,
        ILogger<MongoQueryEngine>? logger = null,
        bool includeErrorDetails = false,
        IIndexSource? indexes = null,
        OwnerFetchCache? cache = null,
        ExplainForwardCache? explainCache = null)
    {
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.runner = runner as TimedAggregateRunner ?? new TimedAggregateRunner(runner ?? throw new ArgumentNullException(nameof(runner)));
        this.cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        // The keyed fetch always exists: this host answers its own targets (SelfOwner); only a
        // remote target needs the remote client.
        ownerCache = cache ?? new OwnerFetchCache(this.options);
        fetch = new KeyedFetch(remote, this, ownerCache, this.options);
        remoteClient = remote is not null;
        this.remote = remote;
        this.explainCache = explainCache ?? new ExplainForwardCache();
        this.indexes = indexes;
        this.logger = logger ?? NullLogger<MongoQueryEngine>.Instance;
        this.includeErrorDetails = includeErrorDetails;
    }

    /// <summary>What this engine keeps of its owners' explain answers: read by a host that reports or bounds what it holds.</summary>
    public ExplainForwardCache ExplainCache => explainCache;

    /// <summary>What this engine keeps of its owners' keyed fetches and of what they said a target lacks.</summary>
    public OwnerFetchCache OwnerCache => ownerCache;

    /// <inheritdoc/>
    public bool RemoteResolve => remoteClient;

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();

        // Where the time goes is always recorded; a host that started the clock earlier hands its collector in.
        var timing = context.Timing ?? new QueryTiming();

        // The breakdown is a contract 2 answer's member, asked for by the request or by its header, and a
        // host can keep it to itself. Under contract 1 nothing asks: the member and the header are ignored.
        timing.Detail = context.Contract != 1 && options.Execution.TimingDetail && (timing.Detail || request.AsksTiming);

        context = Reaching(context) with { Timing = timing };

        // The request's own ceiling (contract 2), under what its batch or its host leaves it.
        if (context.Contract != 1 && request.TimeCeilingMs is { } asked)
            context = context with { MaxTimeMs = context.MaxTimeMs is { } left and > 0 ? Math.Min(left, asked) : asked };

        var binds = QueryTiming.Now;
        var binding = await new Binder(models.Model, cursors).BindAsync(request, context, cancellationToken);

        timing.Bind(binds);

        if (binding is BindOutcome.Failed failed)
            return QueryOutcome.Of(failed.Refusal);

        var bound = ((BindOutcome.Bound)binding).Pipeline;
        var compiling = QueryTiming.Now;
        var compiled = MongoCompiler.Compile(bound, CompileOptionsFor(context));

        timing.Compile(compiling);

        if ((compiled.KeyedResolves.Any(resolve => resolve.IsRemote) || compiled.SemiJoins.Count > 0) && !remoteClient)
            return QueryOutcome.Of(Refusal.NotExecutable(Codes.ResolveUnavailable, "This host has no remote query client; a remote resolve cannot run."));

        var diagnostics = new List<Diagnostic>(bound.Diagnostics);

        // A filter condition that ran before a group which gives its name to another thing: explain says
        // it as a note, and the run that answers such rows says it too (rows, a count and values alike).
        diagnostics.AddRange(Notes.FilterBindsEarlierOf(bound));
        var runOptions = new AggregateRunOptions(compiled.MaxTimeMs, compiled.AllowDiskUse, compiled.Collation) { Comment = context.CorrelationId };
        var resolveCalls = 0;
        var cacheHits = 0;

        // The semi-joins fill their slots before the page runs; without the ids the filter cannot be evaluated.
        if (compiled.SemiJoins.Count > 0)
        {
            var asking = QueryTiming.Now;
            var (refused, semiJoinCalls) = await fetch.ByConditionCountedAsync(compiled, context, Remaining(compiled, timer), cancellationToken).ConfigureAwait(false);

            timing.Resolved(asking);
            resolveCalls += semiJoinCalls;
            timing.ResolveCalls = resolveCalls;

            if (refused is not null)
            {
                Log(compiled, timer, 0, false, false, context, refused, resolveCalls, 0);
                return QueryOutcome.Of(refused);
            }
        }

        // A request that ends in values answers a list of values, not rows: one aggregate, no page.
        if (bound.Values is { } values)
            return await ValuesAsync(values, compiled, runOptions, diagnostics, timer, timing, context, resolveCalls, cancellationToken).ConfigureAwait(false);

        // A count-only request reads no page: the count is refined step by step within its budget.
        if (bound.Page is { CountOnly: true, Count: { } ladder } && compiled.CountStages is { } counted)
        {
            CountInfo known;

            try
            {
                known = await CountLadderAsync(bound, counted, ladder, runOptions, context, cancellationToken).ConfigureAwait(false);
            }
            catch (CountRefused refused)
            {
                // What the database refuses of the request itself (a pattern it cannot compile, a value over
                // its limits) is the refusal the same request with a page gets, not a count nobody knows.
                Log(compiled, timer, 0, false, false, context, refused.Refusal, resolveCalls, 0);

                return QueryOutcome.Of(refused.Refusal);
            }

            Log(compiled, timer, 0, false, known is { State: CountInfo.AtLeast, StoppedBy: CountInfo.ByCap }, context, null, resolveCalls, 0);

            return QueryOutcome.Of(Timed(new QueryResult
            {
                Items = [],
                PageInfo = new PageInfo { HasNextPage = false, Count = known },
                Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
            }, timing));
        }

        IReadOnlyList<BsonDocument> rows;
        IReadOnlyList<BsonDocument>? countRows = null;
        var timedOut = false;
        var strict = context.Contract == 2 && request.IsStrict;

        // The page, the count and a strict request's truncation probes run together and end
        // together: when one fails the others are cancelled rather than left to hold a connection
        // until their own time budget runs out. Sent at the same moment under the same maxTimeMS,
        // they also share one deadline: the request's time limit is a ceiling for all of them, not
        // a budget each.
        using var aggregates = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<BsonDocument>>? pageTask = null;
        Task<IReadOnlyList<BsonDocument>>? countTask = null;

        // The count step of a contract 2 page (DG2 M3) runs beside the page under its own time and
        // its own cancellation: it never fails the rows, and however this request ends it is stopped.
        var step = compiled.CountStages is null ? null : compiled.Count;
        using var counting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopCounting = new CancelOnExit(counting);
        Stopwatch? countTimer = null;

        // Beside a page of groups the count is not sent with the page: counting the groups computes
        // every group, which is what the page itself does, and a page of groups that is not full
        // (the usual one) is its own exact count. The count is sent once the page has come back
        // full. A count beside a page of rows stops at its step's bound and leaves with the page.
        var countAfterPage = step is not null && bound.FinalShape.Grouped;
        var probeTasks = new List<Task<IReadOnlyList<BsonDocument>>>();

        try
        {
            // The page asks for its rows in one reply: the limit and the row that tells whether a next
            // page exists, and room for one more. A batch of exactly what the pipeline's own $limit lets
            // through leaves the cursor open (the server has not seen its end yet), and the driver then
            // sends a getMore that returns nothing; one above it, the first reply closes the cursor.
            pageTask = runner.AggregateAsync(QueryTiming.Page, timing, bound.Entity, compiled.PageStages, runOptions with { BatchSize = compiled.Limit + 2 }, aggregates.Token);
            if (step is not null && !countAfterPage)
            {
                countTimer = Stopwatch.StartNew();
                countTask = runner.AggregateAsync(QueryTiming.Count, timing, bound.Entity, compiled.CountStages!, runOptions with { MaxTimeMs = Math.Clamp(step.BudgetMs, 1, runOptions.MaxTimeMs) }, counting.Token);
            }
            else if (step is null && compiled.CountStages is not null)
            {
                countTask = runner.AggregateAsync(QueryTiming.Count, timing, bound.Entity, compiled.CountStages, runOptions, aggregates.Token);
            }

            // Whatever ends this request before the count is read (the caller giving up while the
            // page runs, say), the count's own failure is read, never left unobserved.
            Observe(countTask);

            // A probe reads the rows up to a truncation, not the page, so it does not wait for the
            // page: only whether its answer is still needed depends on what the page reports.
            if (strict)
                foreach (var probe in compiled.TruncationProbes)
                {
                    probeTasks.Add(runner.AggregateAsync(QueryTiming.Probe, timing, bound.Entity, probe.Stages, runOptions, aggregates.Token));
                    Observe(probeTasks[^1]);
                }

            rows = await pageTask.ConfigureAwait(false);

            // The count of includeTotalCount (contract 1, an internal call) is part of the answer and
            // is waited for; a page's count step is read once the rows are ready.
            if (countTask is not null && step is null)
                countRows = await countTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Aborted(exception);
        }

        // The caller gave up: what an aggregate ended in under the abort (a connection it reset,
        // say) is no fault to log or to answer; the others are stopped and the cancellation travels.
        OperationCanceledException Aborted(Exception exception)
        {
            Abandon();

            return new OperationCanceledException("The caller gave up on the request while its aggregates ran.", exception, cancellationToken);
        }

        void Abandon()
        {
            aggregates.Cancel();
            Observe(pageTask);
            Observe(countTask);
            probeTasks.ForEach(Observe);
        }

        // An aggregate that failed: the others are stopped and the driver's error is the refusal.
        QueryOutcome Failed(Exception exception)
        {
            Abandon();

            var refusal = MapDriverError(exception);

            timedOut = refusal.Status == 504;
            Log(compiled, timer, 0, timedOut, false, context, refusal, resolveCalls, 0);

            return QueryOutcome.Of(refusal);
        }

        var hasNextPage = rows.Count > compiled.Limit;
        var page = hasNextPage ? rows.Take(compiled.Limit).ToList() : rows;

        // A page without a next one is its own count: a step still running is stopped now, not when
        // the request ends. The driver gives up the connection and the server ends the command when
        // it next looks (about half a second at most), so only a long count is cut short by it.
        if (!hasNextPage && step is not null)
        {
            counting.Cancel();
            timing.Discard(QueryTiming.Count);
        }

        // The groups are counted only now that the page has come back full, under the step's own time.
        if (hasNextPage && countAfterPage && page.Count > 0)
        {
            countTimer = Stopwatch.StartNew();
            countTask = runner.AggregateAsync(QueryTiming.Count, timing, bound.Entity, compiled.CountStages!, runOptions with { MaxTimeMs = Math.Clamp(step!.BudgetMs, 1, runOptions.MaxTimeMs) }, counting.Token);
            Observe(countTask);
        }
        var losses = new List<Diagnostic>();

        // A flattening unwind marks the rows whose collection nests deeper than it descends.
        diagnostics.AddRange(MongoCompiler.DepthTruncations(compiled, page));

        // A lookup marks the rows with a parent over its limit; the marks leave the rows here.
        diagnostics.AddRange(MongoCompiler.LookupTruncations(compiled, page));

        // An inline resolve that reports its missing rows reads them off the page (DESIGN §3.6).
        var inline = OutcomePolicy.Report(bound, MongoCompiler.InlineOutcomes(compiled, page), [], strict, options);

        diagnostics.AddRange(inline.Diagnostics);
        losses.AddRange(OutcomePolicy.StrictLosses(diagnostics, strict));
        losses.AddRange(inline.Refusing);

        // A truncated row a later match filtered out is not on the page, but the answer may depend
        // on what was cut: a strict request checks the rows up to the truncation (DESIGN §3.4.3).
        if (strict)
        {
            try
            {
                losses.AddRange(await HiddenTruncationsAsync(compiled, probeTasks, losses).ConfigureAwait(false));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed(exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Aborted(exception);
            }
            finally
            {
                // A probe whose stage the page already reported was not waited for; it stops here.
                aggregates.Cancel();
            }
        }

        // A strict request that neither continues nor jumps reads every matching row in its one
        // page, or refuses (DESIGN §3.4.3).
        if (strict && hasNextPage && IsReportPage(request))
        {
            var max = Math.Max(options.Limits.MaxPageSize, options.Limits.MaxReportPageSize);

            losses.Add(new Diagnostic
            {
                Code = Codes.PageIncomplete,
                Message = $"The query matches more rows than one page holds ({compiled.Limit}); narrow the root or raise the page limit up to {max}.",
                Params = new Dictionary<string, object?> { ["limit"] = compiled.Limit, ["max"] = max },
            });
        }

        // What the page already loses refuses before any owner is asked.
        if (losses.Count > 0)
            return Refuse(compiled, timer, context, losses, resolveCalls, cacheHits);

        // Keyed resolves run over the trimmed page, remote targets at their owners and local ones
        // through this host's SelfOwner; an owner that does not answer yields null rows and a
        // diagnostic, never a failed page.
        IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>>? resolved = null;

        if (compiled.KeyedResolves.Count > 0)
        {
            var joining = QueryTiming.Now;
            var resolution = await fetch.ByKeysAsync(compiled, page, context, Remaining(compiled, timer), strict, cancellationToken).ConfigureAwait(false);

            timing.Resolved(joining);
            resolveCalls += resolution.Calls;
            cacheHits += resolution.CacheHits;
            timing.ResolveCalls = resolveCalls;
            timing.ResolveCacheHits = cacheHits;

            if (resolution.Refusal is not null)
            {
                Log(compiled, timer, 0, false, false, context, resolution.Refusal, resolveCalls, cacheHits);
                return QueryOutcome.Of(resolution.Refusal);
            }

            resolved = resolution.Rows;
            diagnostics.AddRange(resolution.Diagnostics);

            // A path under a union's alias that an owner said its target lacks was dropped for that target; only
            // the run learns it, so the run says it (DESIGN §3.4.1; explain notes the local ones).
            diagnostics.AddRange(resolution.Dropped.Select(drop => drop.Branch
                ? Notes.BranchPathDropped(drop.Stage, drop.Alias, drop.Target, drop.Path)
                : Notes.SelectPathDropped(drop.Stage, drop.Alias, drop.Target, drop.Path, drop.Parent)));

            var report = OutcomePolicy.Report(bound, resolution.Outcomes, resolution.Truncations, strict, options);

            diagnostics.AddRange(report.Diagnostics);
            losses.AddRange(OutcomePolicy.StrictLosses(resolution.Diagnostics, strict));
            losses.AddRange(report.Refusing);

            if (losses.Count > 0)
                return Refuse(compiled, timer, context, losses, resolveCalls, cacheHits);
        }

        long? totalCount = null;
        bool? capped = null;

        // What a contract 2 answer knows of the count (DG2 M3): for free off the page, and by the
        // count step when one was asked. The cursor carries the rows served so far.
        var served = context.Contract == 2 ? (bound.Page.Cursor?.Served ?? 0) + page.Count : 0;
        var countInfo = context.Contract == 2
            ? await CountAsync(step, countTask, countTimer, hasNextPage, served, context, cancellationToken).ConfigureAwait(false)
            : null;

        if (compiled.IncludeTotalCount)
        {
            var n = countRows is { Count: > 0 } && countRows[0].TryGetValue("n", out var count) ? count.ToInt64() : 0;

            capped = n > compiled.CountCap;
            totalCount = capped.Value ? compiled.CountCap : n;

            if (capped.Value)
                diagnostics.Add(new Diagnostic { Code = Codes.TotalCountCapped, Message = $"More than {compiled.CountCap} rows match; the count is the cap.", Params = new Dictionary<string, object?> { ["cap"] = compiled.CountCap } });
        }

        var nextCursor = hasNextPage && page.Count > 0 ? cursors.Encode(NextCursor(compiled, page[^1], served)) : null;
        var items = new List<JsonNode?>(page.Count);
        var unfit = new List<string>();
        var converting = QueryTiming.Now;

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

        timing.Convert(converting);
        timing.Rows = items.Count;

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
            PageInfo = new PageInfo { HasNextPage = hasNextPage, NextCursor = nextCursor, TotalCount = totalCount, TotalCountCapped = capped, Count = countInfo },
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
        };

        Log(compiled, timer, page.Count, timedOut, capped == true || countInfo is { State: CountInfo.AtLeast, StoppedBy: CountInfo.ByCap }, context, null, resolveCalls, cacheHits);

        return QueryOutcome.Of(Timed(result, timing));
    }

    /// <summary>
    /// The answer of a request that ends in <c>values</c>: the distinct values of the stage's path
    /// among the rows that reach it, in the row envelope. One aggregate answers one document: how many
    /// rows the stage read, and the values the search keeps, in order, one more than the limit. Each
    /// value is written as a row of the same member is (<c>value</c>, and <c>count</c> when asked for);
    /// one more than the limit says the list goes on (<c>hasNextPage</c>, without a cursor: the caller
    /// narrows with <c>search</c>). A stage that read as many rows as its bound lets it says so
    /// (<c>VALUES_SCAN_CAPPED</c>): the list is then of those rows, not of all that match.
    /// </summary>
    private async Task<QueryOutcome> ValuesAsync(BoundStage.Values values, CompiledQuery compiled, AggregateRunOptions runOptions, List<Diagnostic> diagnostics,
        Stopwatch timer, QueryTiming timing, RequestContext context, int resolveCalls, CancellationToken cancellationToken)
    {
        var bound = compiled.Bound;
        IReadOnlyList<BsonDocument> answered;

        try
        {
            answered = await runner.AggregateAsync(QueryTiming.Values, timing, bound.Entity, compiled.PageStages, runOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var refusal = MapDriverError(exception);

            Log(compiled, timer, 0, refusal.Status == 504, false, context, refusal, resolveCalls, 0);

            return QueryOutcome.Of(refusal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The caller gave up: what the aggregate ended in under the abort is no fault to log or to answer.
            throw new OperationCanceledException("The caller gave up on the request while its aggregate ran.", exception, cancellationToken);
        }

        var answer = answered.Count > 0 ? answered[0] : [];
        var list = answer.TryGetValue(MongoCompiler.ValuesList, out var listed) && listed is BsonArray entries ? entries : [];
        var read = answer.TryGetValue(MongoCompiler.ValuesTotal, out var totals) && totals is BsonArray { Count: > 0 } total && total[0] is BsonDocument counted
            && counted.TryGetValue(MongoCompiler.ValuesRows, out var rows) ? rows.ToInt64() : 0;
        var hasMore = list.Count > values.Limit;
        var items = new List<JsonNode?>(Math.Min(list.Count, values.Limit));
        var unfit = new List<string>();
        var converting = QueryTiming.Now;

        foreach (var entry in list.Take(values.Limit).OfType<BsonDocument>())
            items.Add(WireEncoder.Encode(new BsonDocument
            {
                [BoundStage.Values.ValueAlias] = entry.GetValue("_id", BsonNull.Value),
                [BoundStage.Values.CountAlias] = entry.GetValue(MongoCompiler.ValuesCount, 0),
            }, bound, null, unfit));

        timing.Convert(converting);
        timing.Rows = items.Count;

        if (unfit.Count > 0)
            logger.LogWarning(
                "OxQL {Entity} returned {Count} stored values verbatim because they do not fit their kind; paths={Paths} org={OrganisationId} correlation={CorrelationId}",
                bound.Entity.Id,
                unfit.Count,
                values.Path.Wire,
                context.Organisation,
                context.CorrelationId);

        // As many rows read as the bound lets through: there may be more, and the list says nothing of them.
        if (read >= values.Scan)
            diagnostics.Add(new Diagnostic
            {
                Code = Codes.ValuesScanCapped,
                Message = $"The values are those among the first {values.Scan} rows that reach the stage; more rows may match{(values.Counts ? ", and a count is the count within them" : "")}. Narrow the request's filter to see the values of fewer rows.",
                Stage = bound.CallerIndexOf(values),
                Path = values.Path.Wire,
                Params = new Dictionary<string, object?> { ["scan"] = values.Scan },
            });

        var result = new QueryResult
        {
            Items = items,
            PageInfo = new PageInfo
            {
                HasNextPage = hasMore,
                // The envelope's count is of what it lists: the values, all of them or at least one more.
                Count = context.Contract == 2
                    ? hasMore ? new CountInfo { State = CountInfo.AtLeast, Value = items.Count + 1, StoppedBy = CountInfo.ByPage } : new CountInfo { State = CountInfo.Exact, Value = items.Count }
                    : null,
            },
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
        };

        Log(compiled, timer, items.Count, false, false, context, null, resolveCalls, 0);

        return QueryOutcome.Of(Timed(result, timing));
    }

    /// <summary>
    /// The answer with where its time went: the collector for the host's <c>Server-Timing</c> header,
    /// and the breakdown as the <c>timing</c> member when the request asked for it. A request this
    /// host runs for itself inside another (a local join target's owner query) is part of that
    /// request's timing and carries none of its own.
    /// </summary>
    private static QueryResult Timed(QueryResult result, QueryTiming timing) => result with
    {
        Measured = timing,
        Timing = timing.Detail ? timing.ToJson() : null,
    };

    /// <summary>A further step of a count is sent only when one this many times the last is predicted to fit the time left.</summary>
    private const int LadderFactor = 10;

    /// <summary>The share of the time left a further step is sized to fill; the rest is the margin of the prediction.</summary>
    private const double LadderFill = 0.5;

    /// <summary>
    /// The count of a count-only request (DG2 M3): a first step of <c>Count:StepRows</c> rows, then as
    /// far as the time allows. A step counts up to its bound (<c>$limit bound + 1</c>, <c>$count</c>);
    /// one that finds no more rows than its bound is exact and ends the count. The first step is cheap
    /// where matches are dense and proves a lower bound whatever follows. From what it took, the next
    /// step is sized to what the time left is predicted to reach (a scan is linear in what it reads),
    /// which for a count the database answers quickly is the ceiling at once: two commands, not one
    /// per power of ten. A step is sent only when ten times the last one is predicted to fit; otherwise
    /// the last bound is the answer, stopped by the budget. Each step's own time limit is what is left;
    /// one that runs out leaves the bound before it, stopped by time. Nothing counted yet is unknown.
    /// </summary>
    private async Task<CountInfo> CountLadderAsync(BoundPipeline bound, IReadOnlyList<BsonDocument> counted, BoundCount ladder, AggregateRunOptions runOptions, RequestContext context, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var budget = Math.Min(ladder.BudgetMs, runOptions.MaxTimeMs);
        var stages = counted.Take(counted.Count - 2).ToList();
        long proven = 0;
        long rung = Math.Min(ladder.Rows, ladder.Ceiling);

        while (true)
        {
            var left = budget - (long)timer.Elapsed.TotalMilliseconds;
            var started = timer.Elapsed;

            try
            {
                if (left < 1)
                    throw new TimeoutException();

                var rows = await runner.AggregateAsync(QueryTiming.CountRung, context.Timing, bound.Entity, [.. stages, new BsonDocument("$limit", rung + 1), new BsonDocument("$count", "n")], runOptions with { MaxTimeMs = (int)left }, cancellationToken).ConfigureAwait(false);
                var found = rows is { Count: > 0 } && rows[0].TryGetValue("n", out var n) ? n.ToInt64() : 0;
                var ms = (long)timer.Elapsed.TotalMilliseconds;

                if (found <= rung)
                    return new CountInfo { State = CountInfo.Exact, Value = found, Ms = ms };

                proven = rung;

                if (rung >= ladder.Ceiling)
                    return new CountInfo { State = CountInfo.AtLeast, Value = proven, StoppedBy = CountInfo.ByCap, Ms = ms };

                // How many times the step just taken fits what is left of the budget.
                var took = Math.Max((timer.Elapsed - started).TotalMilliseconds, 0.01);
                var fits = (budget - timer.Elapsed.TotalMilliseconds) / took;

                if (fits < LadderFactor)
                    return new CountInfo { State = CountInfo.AtLeast, Value = proven, StoppedBy = CountInfo.ByBudget, Ms = ms };

                var reach = rung * Math.Max(LadderFactor, fits * LadderFill);

                rung = reach >= ladder.Ceiling ? ladder.Ceiling : (long)reach;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var timeout = exception is TimeoutException or OperationCanceledException or MongoExecutionTimeoutException or MongoCommandException { Code: 50 };
                var ms = (long)timer.Elapsed.TotalMilliseconds;

                // The first step failing for what the request asks is the request's refusal, as with a page; a
                // fault of the database or the network leaves the count unknown, and a later step leaves its bound.
                if (!timeout && proven == 0 && RequestError(exception) is { } refusal)
                    throw new CountRefused(refusal);

                if (!timeout)
                    logger.LogWarning(exception, "OxQL count step failed. org={OrganisationId} correlation={CorrelationId}", context.Organisation, context.CorrelationId);

                // A bound an earlier step proved stands, whatever ended the step after it.
                return proven > 0
                    ? new CountInfo { State = CountInfo.AtLeast, Value = proven, StoppedBy = CountInfo.ByTime, Ms = ms }
                    : new CountInfo { State = CountInfo.Unknown, Reason = timeout ? CountInfo.Timeout : CountInfo.Failed, Ms = ms };
            }
        }
    }

    /// <summary>The refusal a count-only request ends in when the database refuses what the request asks.</summary>
    private sealed class CountRefused(Refusal refusal) : Exception
    {
        public Refusal Refusal { get; } = refusal;
    }

    /// <summary>Stops what a request still runs beside its page when the request ends, however it ends.</summary>
    private sealed class CancelOnExit(CancellationTokenSource source) : IDisposable
    {
        public void Dispose() => source.Cancel();
    }

    /// <summary>
    /// What a contract 2 answer knows of how many rows match (DG2 M3).
    /// <list type="bullet">
    /// <item>A page without a next one ends the list: the rows served are the exact count, and a count
    /// step still running is not waited for.</item>
    /// <item>Without a count step, a page with a next one proves the rows served and one more.</item>
    /// <item>The count step is waited for until its budget ends, counted from when it was sent. Up to
    /// its bound it is exact; above it the bound is a lower bound, stopped by the cap when the request
    /// could not have counted further and by the step otherwise (the count alone goes further). A step
    /// that times out or fails leaves the count unknown and the rows as they are.</item>
    /// </list>
    /// A bound below what the pages already served gives way to the rows served and one more.
    /// </summary>
    private async Task<CountInfo> CountAsync(BoundCount? step, Task<IReadOnlyList<BsonDocument>>? countTask, Stopwatch? countTimer, bool hasNextPage, long served, RequestContext context, CancellationToken cancellationToken)
    {
        if (!hasNextPage)
            return new CountInfo { State = CountInfo.Exact, Value = served };

        var walked = new CountInfo { State = CountInfo.AtLeast, Value = served + 1, StoppedBy = CountInfo.ByPage };

        if (step is null || countTask is null || countTimer is null)
            return walked;

        var waiting = QueryTiming.Now;

        try
        {
            var left = TimeSpan.FromMilliseconds(step.BudgetMs) - countTimer.Elapsed;
            var rows = countTask.IsCompleted
                ? await countTask.ConfigureAwait(false)
                : await countTask.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            var counted = rows is { Count: > 0 } && rows[0].TryGetValue("n", out var n) ? n.ToInt64() : 0;
            var ms = (long)countTimer.Elapsed.TotalMilliseconds;

            if (counted <= step.Rows)
                return counted > served ? new CountInfo { State = CountInfo.Exact, Value = counted, Ms = ms } : walked with { Ms = ms };

            return step.Rows > served
                ? new CountInfo { State = CountInfo.AtLeast, Value = step.Rows, StoppedBy = step.Rows >= step.Ceiling ? CountInfo.ByCap : CountInfo.ByStep, Ms = ms }
                : walked with { Ms = ms };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var timeout = exception is TimeoutException or OperationCanceledException or MongoExecutionTimeoutException or MongoCommandException { Code: 50 };

            if (!timeout)
                logger.LogWarning(exception, "OxQL count step failed; the rows are answered without a count. org={OrganisationId} correlation={CorrelationId}", context.Organisation, context.CorrelationId);

            return new CountInfo { State = CountInfo.Unknown, Reason = timeout ? CountInfo.Timeout : CountInfo.Failed, Ms = (long)countTimer.Elapsed.TotalMilliseconds };
        }
        finally
        {
            context.Timing?.CountWait(waiting);
        }
    }

    /// <summary>
    /// The truncations a strict request's page does not show because a later match filtered the
    /// truncated rows out: one diagnostic per probe that finds a flagged candidate row, unless the
    /// page already reported that stage. The probes were sent with the page
    /// (<paramref name="probes"/>, in the order of <see cref="CompiledQuery.TruncationProbes"/>); one
    /// whose stage is already reported is not waited for, and what becomes of it is not this
    /// request's any more.
    /// </summary>
    private static async Task<IReadOnlyList<Diagnostic>> HiddenTruncationsAsync(CompiledQuery compiled, IReadOnlyList<Task<IReadOnlyList<BsonDocument>>> probes, IReadOnlyList<Diagnostic> reported)
    {
        var hidden = new List<Diagnostic>();

        for (var index = 0; index < compiled.TruncationProbes.Count; index++)
        {
            var probe = compiled.TruncationProbes[index];

            if (reported.Any(diagnostic => diagnostic.Code == probe.Code && diagnostic.Stage == probe.Stage))
            {
                Observe(probes[index]);
                continue;
            }

            var rows = await probes[index].ConfigureAwait(false);

            if (rows.Count == 0)
                continue;

            hidden.Add(probe.Code == Codes.LookupTruncated
                ? new Diagnostic
                {
                    Code = Codes.LookupTruncated,
                    Message = $"'{probe.Path}' holds the first {probe.Bound} children of a parent that has more, and a later match reads it, so a row it filtered out may have matched on a child that was cut.",
                    Stage = probe.Stage,
                    Path = probe.Path,
                    Params = new Dictionary<string, object?> { ["alias"] = probe.Path, ["limit"] = probe.Bound, ["rows"] = 0, ["filtered"] = true },
                }
                : new Diagnostic
                {
                    Code = Codes.UnwindDepthTruncated,
                    Message = $"'{probe.Path}' nests items deeper than {probe.Bound} levels, and a later match reads the items, so a row it filtered out may have matched on an item below the depth.",
                    Stage = probe.Stage,
                    Path = probe.Path,
                    Params = new Dictionary<string, object?> { ["path"] = probe.Path, ["depth"] = probe.Bound, ["rows"] = 0, ["filtered"] = true },
                });
        }

        return hidden;
    }

    /// <summary>The 422 of a request that would lose data (DESIGN §3.4.3, §3.6), logged like any refusal.</summary>
    private QueryOutcome Refuse(CompiledQuery compiled, Stopwatch timer, RequestContext context, IReadOnlyList<Diagnostic> losses, int resolveCalls, int cacheHits)
    {
        var refusal = Refusal.DataLoss(losses);

        Log(compiled, timer, 0, false, false, context, refusal, resolveCalls, cacheHits);

        return QueryOutcome.Of(refusal);
    }

    /// <summary>Whether the request's page neither continues (<c>cursor</c>) nor jumps (<c>offset</c>): the report page of a strict request.</summary>
    private static bool IsReportPage(QueryRequest request) =>
        request.Pipeline.Select(stage => stage.Page).FirstOrDefault(page => page is not null) is not { } page
        || (page.Cursor is null && page.Offset is null);

    /// <summary>
    /// Reads the failure of an aggregate nobody awaits any more, so it ends here instead of
    /// surfacing later as an unobserved task exception. The refusal does not wait for it.
    /// </summary>
    private static void Observe(Task? abandoned) =>
        abandoned?.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// The context with the owners this host can reach (<see cref="RequestContext.RemoteService"/>), which a
    /// lookup of another service's entity binds by (DESIGN §3.4.4); a host without a remote client reaches none.
    /// </summary>
    private RequestContext Reaching(RequestContext context) =>
        remote is null || context.RemoteService is not null ? context : context with { RemoteService = remote.IsConfigured };

    /// <summary>What is left of the request's time budget for the owners.</summary>
    private static TimeSpan Remaining(CompiledQuery compiled, Stopwatch timer) =>
        TimeSpan.FromMilliseconds(compiled.MaxTimeMs) - timer.Elapsed;

    private CompileOptions CompileOptionsFor(RequestContext context)
    {
        var maxTime = options.Execution.EffectiveMaxTimeMs;

        if (context.MaxTimeMs is { } requested && requested > 0)
            maxTime = Math.Min(maxTime, requested);

        return new CompileOptions(maxTime, options.Execution.AllowDiskUse, options.Limits.CountCap, options.Representation.Collation) { KeepDiscriminators = context.Contract != 1, AsWritten = context.Contract == 1 };
    }

    /// <summary>The cursor for the page after <paramref name="last"/>.</summary>
    /// <remarks><paramref name="served"/> is how many rows the pages up to this one served; a contract 2 cursor carries it (DG2 M3).</remarks>
    public static CursorPayload NextCursor(CompiledQuery compiled, BsonDocument last, long served = 0)
    {
        if (compiled.PagingMode == PagingMode.Offset)
            return new CursorPayload(compiled.Bound.Fingerprint, PagingMode.Offset, [], compiled.Offset + compiled.Limit) { Served = served };

        var fields = new List<CursorValue>();

        foreach (var field in compiled.SortFields)
            fields.Add(new CursorValue(field.Path.Wire, field.Ascending, ValueAt(last, field.Path.Storage!)));

        // A group's keys end its order; its rows have no key of their own.
        if (!compiled.Bound.FinalShape.Grouped)
            fields.Add(new CursorValue("_id", true, ValueAt(last, "_id")));

        // Unwound rows of one parent differ by the position of each unwind.
        foreach (var index in compiled.PagingIndexes)
            fields.Add(new CursorValue(index, true, ValueAt(last, index)));

        return new CursorPayload(compiled.Bound.Fingerprint, PagingMode.Keyset, fields, 0) { Served = served };
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

            case var _ when RequestError(exception) is { } refusal:
                return refusal;

            default:
                logger.LogError(exception, "OxQL engine fault");
                return Refusal.Internal(includeErrorDetails ? exception.Message : null);
        }
    }

    /// <summary>
    /// The refusal of a driver error that is the request's doing and not a fault or a timeout: what
    /// it asks costs more than the database allows, or the database cannot compile a pattern of it.
    /// Null for anything else.
    /// </summary>
    private static Refusal? RequestError(Exception exception) => exception switch
    {
        // 5414201: a keyed window ($setWindowFields) over its memory limit, which a remote
        // lookup ranking many children per key without an index can reach.
        MongoCommandException { Code: 292 or 16819 or 16820 or 16945 or 5414201 } =>
            Refusal.NotExecutable(Codes.QueryTooExpensive, "The query needs more memory than the server allows without spilling to disk; add an index for the sort or narrow the match."),

        // An accumulator over its memory cap, which spilling to disk does not lift, and a
        // row that outgrew the document size limit: both are the query's cost, not a fault.
        MongoCommandException { Code: 146 or 10334 } =>
            Refusal.NotExecutable(Codes.QueryTooExpensive, "The query builds a value larger than the server allows, typically a push or countDistinct over too many rows; narrow the match or group by more keys."),

        // The binder validates a pattern with the .NET parser and the server compiles it
        // with PCRE2; a construct only the first accepts is rejected here.
        MongoCommandException command when IsPatternRejection(command) =>
            Refusal.Validation([new QueryValidationError { Code = Codes.InvalidRegex, Message = "The database could not compile a regular expression of this query; it accepts PCRE2 syntax." }]),

        // The binder bounds how deep a condition nests; a pipeline the driver still cannot
        // serialize this deep is the query's cost, not a fault of the host.
        BsonSerializationException =>
            Refusal.NotExecutable(Codes.QueryTooExpensive, "The query nests deeper than the database accepts; flatten its conditions or joins."),

        _ => null,
    };

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
        var elapsed = timer.Elapsed.TotalMilliseconds;
        var threshold = options.Execution.EffectiveSlowQueryMs;
        var slow = threshold > 0 && elapsed > threshold;

        // Nobody reads the line: nothing is built for it.
        if (!slow && !logger.IsEnabled(LogLevel.Information))
            return;

        var stages = string.Join(",", bound.Stages.Select(stage => stage.GetType().Name.ToLowerInvariant()));
        var outcome = refusal?.Type ?? "ok";

        var spent = context.Timing?.Summary() ?? default;

        logger.LogInformation(
            "OxQL {Entity} stages={Stages} rows={Rows} elapsedMs={ElapsedMs} dbMs={DbMs} dbCommands={DbCommands} oursMs={OursMs} convertMs={ConvertMs} timedOut={TimedOut} countCapped={CountCapped} resolveCalls={ResolveCalls} resolveCacheHits={ResolveCacheHits} compat={Compat} user={UserId} org={OrganisationId} correlation={CorrelationId} outcome={Outcome}",
            bound.Entity.Id,
            stages,
            rows,
            elapsed,
            spent.DbMs,
            spent.Commands,
            Math.Max(0, Math.Round(elapsed - spent.DbMs, 2)),
            spent.ConvertMs,
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
        if (slow)
            logger.LogWarning(
                "OxQL slow query {Entity} stages={Stages} elapsedMs={ElapsedMs} thresholdMs={ThresholdMs} rows={Rows} regex={Regex} unboundedSort={UnboundedSort} count={Count} remote={Remote} outcome={Outcome} org={OrganisationId} correlation={CorrelationId}",
                bound.Entity.Id,
                stages,
                elapsed,
                threshold,
                rows,
                HasRegex(compiled.PageStages),
                HasUnboundedSort(compiled.PageStages),
                compiled.IncludeTotalCount || compiled.Count is not null,
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
