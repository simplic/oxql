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
public sealed class MongoQueryEngine : IQueryEngine, IEngineFeatures
{
    private readonly IEntityModelProvider models;
    private readonly IAggregateRunner runner;
    private readonly CursorCodec cursors;
    private readonly OxQLOptions options;
    private readonly KeyedFetch fetch;
    private readonly bool remoteClient;
    private readonly IRemoteQueryClient? remote;
    private readonly ExplainForwardCache explainCache;
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
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        // The keyed fetch always exists: this host answers its own targets (SelfOwner); only a
        // remote target needs the remote client.
        fetch = new KeyedFetch(remote, this, cache ?? new OwnerFetchCache(this.options), this.options);
        remoteClient = remote is not null;
        this.remote = remote;
        this.explainCache = explainCache ?? new ExplainForwardCache();
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
        var strict = context.Contract == 2 && request.IsStrict;
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
            losses.AddRange(await HiddenTruncationsAsync(bound, compiled, runOptions, losses, cancellationToken).ConfigureAwait(false));

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
            var resolution = await fetch.ByKeysAsync(compiled, page, context, Remaining(compiled, timer), strict, cancellationToken).ConfigureAwait(false);

            resolveCalls += resolution.Calls;
            cacheHits += resolution.CacheHits;

            if (resolution.Refusal is not null)
            {
                Log(compiled, timer, 0, false, false, context, resolution.Refusal, resolveCalls, cacheHits);
                return QueryOutcome.Of(resolution.Refusal);
            }

            resolved = resolution.Rows;
            diagnostics.AddRange(resolution.Diagnostics);

            // A flat select path an owner said its target lacks was dropped for that target; only
            // the run learns it, so the run says it (DESIGN §3.4.1; explain notes the local ones).
            diagnostics.AddRange(resolution.Dropped.Select(drop => Notes.SelectPathDropped(drop.Stage, drop.Alias, drop.Target, drop.Path, drop.Parent)));

            var report = OutcomePolicy.Report(bound, resolution.Outcomes, resolution.Truncations, strict, options);

            diagnostics.AddRange(report.Diagnostics);
            losses.AddRange(OutcomePolicy.StrictLosses(resolution.Diagnostics, strict));
            losses.AddRange(report.Refusing);

            if (losses.Count > 0)
                return Refuse(compiled, timer, context, losses, resolveCalls, cacheHits);
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
    /// The truncations a strict request's page does not show because a later match filtered the
    /// truncated rows out: one diagnostic per probe that finds a flagged candidate row, unless the
    /// page already reported that stage.
    /// </summary>
    private async Task<IReadOnlyList<Diagnostic>> HiddenTruncationsAsync(BoundPipeline bound, CompiledQuery compiled, AggregateRunOptions runOptions, IReadOnlyList<Diagnostic> reported, CancellationToken cancellationToken)
    {
        var hidden = new List<Diagnostic>();

        foreach (var probe in compiled.TruncationProbes)
        {
            if (reported.Any(diagnostic => diagnostic.Code == probe.Code && diagnostic.Stage == probe.Stage))
                continue;

            var rows = await runner.AggregateAsync(bound.Entity, probe.Stages, runOptions, cancellationToken).ConfigureAwait(false);

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

    /// <summary>What is left of the request's time budget for the owners.</summary>
    private static TimeSpan Remaining(CompiledQuery compiled, Stopwatch timer) =>
        TimeSpan.FromMilliseconds(compiled.MaxTimeMs) - timer.Elapsed;

    /// <inheritdoc/>
    public async Task<ExplainOutcome> ExplainAsync(ExplainRequest request, RequestContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var binding = await new Binder(models.Model, cursors).BindAsync(request.Query, context, explain: true, cancellationToken);
        var steps = Steps(binding.Trace);
        var result = binding.Trace is { } trace ? ResultOf(trace.Final) : null;

        // What stops explain before binding stays a refusal: a request without an organisation.
        if (binding is BindOutcome.Failed { Refusal.Status: not 400 } refused)
            return new ExplainOutcome.Refused(refused.Refusal);

        // Describe answers from the shapes of the part that binds, valid or not (DESIGN §4.2, §4.5);
        // the owners' answers and the remote check share one budget (DESIGN §4.3).
        var owners = new RemoteExplain(remote, explainCache, context, request);
        var describeNotes = new List<Diagnostic>();
        var describe = await Describe.AnswerAsync(request, binding.Trace, models.Model, context, owners, describeNotes, cancellationToken).ConfigureAwait(false);

        if (binding is BindOutcome.Failed failed)
            return new ExplainOutcome.Success(Answer(context, valid: false, failed.Refusal.Errors ?? [], [], steps, result, request) with
            {
                Notes = Ordered(describeNotes),
                Describe = describe,
            });

        var bound = ((BindOutcome.Bound)binding).Pipeline;
        var compiled = MongoCompiler.Compile(bound, CompileOptionsFor(context));
        var diagnostics = bound.Diagnostics.ToList();
        var strict = context.Contract == 2 && request.Query.IsStrict;
        var indexes = Notes.CallerIndexes(bound, request.Query);
        var notes = Notes.Of(bound, request.Query, indexes, strict, context.Contract, options);

        steps = Placed(steps, bound, indexes, strict, notes);
        result = binding.Trace is { } bindTrace ? ResultOf(bindTrace.Final) with { Columns = Columns(bound, bindTrace) } : result;
        notes.AddRange(describeNotes);

        // A request this host cannot run is not valid, though it binds: the same refusal the query
        // path gives, as an error.
        if ((compiled.KeyedResolves.Any(resolve => resolve.IsRemote) || compiled.SemiJoins.Count > 0) && !remoteClient)
            return new ExplainOutcome.Success(Answer(context, valid: false,
                [new QueryValidationError { Code = Codes.ResolveUnavailable, Message = "This host has no remote query client; a remote resolve cannot run." }],
                diagnostics, steps, result, request) with { Notes = Ordered(notes), Describe = describe });

        // The remote check (DESIGN §4.3): the stages continued at an owner are bound by the owner's
        // internal explain; an owner error there is this request's, at the caller's stage.
        var (checkErrors, checkNotes) = await owners.CheckAsync(bound, strict, cancellationToken).ConfigureAwait(false);

        notes.AddRange(checkNotes);

        if (checkErrors.Count > 0)
        {
            var failedStages = checkErrors.Where(error => error.Stage is not null).Select(error => error.Stage!.Value).ToHashSet();

            return new ExplainOutcome.Success(Answer(context, valid: false, checkErrors, diagnostics,
                steps.Select(step => failedStages.Contains(step.Index) ? step with { Status = "error" } : step).ToList(), result, request) with
            {
                Notes = Ordered(notes),
                Describe = describe,
            });
        }

        var advisory = request.IncludesIndexes ? await AdviseAsync(bound, compiled, cancellationToken).ConfigureAwait(false) : null;

        foreach (var line in advisory ?? [])
            notes.Add(Notes.Index(
                line["field"]?.GetValue<string>() ?? "",
                line["used"] is JsonValue used && used.TryGetValue<bool>(out var flag) ? flag : null,
                line["index"]?.GetValue<string>(),
                line["note"]?.GetValue<string>()));

        return new ExplainOutcome.Success(Answer(context, valid: true, [], diagnostics, steps, result, request) with
        {
            Notes = Ordered(notes),
            Describe = describe,
            Bound = JsonNode.Parse(bound.Canonical, documentOptions: Deep)!,
            Stages = compiled.PageStages.Select(Relaxed).ToList(),
            Count = compiled.CountStages?.Select(Relaxed).ToList(),
            Collation = compiled.Collation is null ? null : Relaxed(compiled.Collation),
            Advisory = advisory,
        });
    }

    /// <summary>The notes in stage order, request-wide ones (no stage) last; within a stage as they were found.</summary>
    private static IReadOnlyList<Diagnostic> Ordered(List<Diagnostic> notes) =>
        notes.OrderBy(note => note.Stage ?? int.MaxValue).ToList();

    /// <summary>
    /// The steps with where each join runs (DESIGN §4.3): its executor and phase, the reference it
    /// follows, the owner and the owner queries of a keyed or continued stage (keys elided), the stages
    /// continued under a keyed stage; and the join-placement note of each join run on this host.
    /// </summary>
    private static IReadOnlyList<ExplainStep> Placed(IReadOnlyList<ExplainStep> steps, BoundPipeline bound, IReadOnlyList<int?> indexes, bool strict, List<Diagnostic> notes)
    {
        var placed = steps.ToDictionary(step => step.Index);
        var owners = new Dictionary<string, IReadOnlyList<ExplainedOwnerQuery>>(StringComparer.Ordinal);

        for (var position = 0; position < bound.Stages.Count; position++)
        {
            if (indexes[position] is not { } index || !placed.TryGetValue(index, out var step))
                continue;

            switch (bound.Stages[position])
            {
                case BoundStage.Lookup lookup:
                {
                    var after = MongoCompiler.JoinsAfterPage(bound.Stages, position, lookup.As);

                    placed[index] = step with { Executor = "inline", Phase = after ? "afterPage" : "beforePage" };
                    notes.Add(Notes.Join(index, lookup.As, "lookup", after));
                    break;
                }

                case BoundStage.Resolve resolve when resolve.IsRemote || resolve.Executor == ResolveExecutor.Keyed:
                {
                    var explained = KeyedFetch.Explain(bound, resolve, strict);
                    var continued = Continuation.Of(bound, resolve);

                    owners[resolve.As] = explained;
                    placed[index] = step with
                    {
                        Executor = resolve.IsRemote ? "keyed-remote" : "keyed-local",
                        Phase = "afterPage",
                        Creates = Created(step.Creates, resolve),
                        Owner = OwnerOf(explained),
                        Reference = ReferenceOf(resolve),
                        Continued = continued.Count == 0 ? null : continued.Select(stage => (JsonNode)new JsonObject { ["index"] = stage.OriginIndex, ["forTarget"] = stage.ForTarget }).ToList(),
                    };
                    notes.Add(Notes.Join(index, resolve.As, "resolve", afterPage: true));
                    break;
                }

                case BoundStage.Resolve resolve:
                {
                    var after = MongoCompiler.JoinsAfterPage(bound.Stages, position, resolve.As);

                    placed[index] = step with { Executor = "inline", Phase = after ? "afterPage" : "beforePage", Reference = ReferenceOf(resolve) };
                    notes.Add(Notes.Join(index, resolve.As, "resolve", after));
                    break;
                }

                case ContinuedStage continued:
                {
                    var carrying = owners.TryGetValue(continued.Anchor, out var explained)
                        ? explained.Where(owner => owner.Continued.Any(stage => ReferenceEquals(stage, continued))).ToList()
                        : [];

                    placed[index] = step with { Executor = "continued", Phase = "owner", Owner = carrying.Count == 0 ? null : OwnerOf(carrying) };
                    break;
                }
            }
        }

        return steps.Select(step => placed[step.Index]).ToList();
    }

    /// <summary>
    /// The aliases a keyed resolve creates with every target they may hold (DESIGN §4.3): a remote
    /// alias's shape names one owner entity, but a union's rows come from each target — the alias
    /// holds <c>entity#item</c> or the entity per target, its <c>parentAs</c> the owning entities.
    /// </summary>
    private static IReadOnlyList<ExplainCreated> Created(IReadOnlyList<ExplainCreated> creates, BoundStage.Resolve resolve)
    {
        var targets = TargetsOf(resolve).Select(target => target.Declared).ToList();

        return creates.Select(created => created.Node != "remote"
            ? created
            : created.Alias == resolve.As
                ? created with { Entities = targets.Select(target => target.ToString()).Distinct(StringComparer.Ordinal).ToList() }
                : created.Alias == resolve.ParentAs
                    ? created with { Entities = targets.Select(target => target.Entity).Distinct(StringComparer.Ordinal).ToList() }
                    : created).ToList();
    }

    /// <summary>
    /// The owner block of a keyed or continued stage: the first remote target's owner (else the first
    /// target's) with its route and query, and every target's owner query, service, continued stages
    /// and the continued stages that are <c>not_applicable</c> to its rows. The engine knows an owner's
    /// service, which names its API (<c>&lt;service&gt;-api</c>), but not the version the host routes
    /// to, which stays null.
    /// </summary>
    private static JsonObject OwnerOf(IReadOnlyList<ExplainedOwnerQuery> explained)
    {
        var first = explained.FirstOrDefault(owner => owner.Remote) ?? explained[0];

        return new JsonObject
        {
            ["service"] = first.Service,
            ["route"] = Route(first.Service),
            ["query"] = QueryOf(first),
            ["targets"] = new JsonArray(explained.Select(owner => (JsonNode)new JsonObject
            {
                ["target"] = owner.Target,
                ["service"] = owner.Service,
                ["remote"] = owner.Remote,
                ["grouped"] = owner.Grouped,
                ["route"] = Route(owner.Service),
                ["query"] = QueryOf(owner),
                ["continued"] = new JsonArray(owner.Continued.Select(stage => (JsonNode)stage.OriginIndex).ToArray()),
                ["notApplicable"] = new JsonArray(owner.NotApplicable.Select(stage => (JsonNode)stage.OriginIndex).ToArray()),
            }).ToArray()),
        };
    }

    private static JsonObject Route(string service) => new() { ["apiName"] = service + "-api", ["apiVersion"] = null };

    /// <summary>An owner query in wire form, the page's size elided with its keys: it is the number of keys times the rows per key.</summary>
    private static JsonNode QueryOf(ExplainedOwnerQuery owner)
    {
        var query = JsonSerializer.SerializeToNode(owner.Query, OxQLJson.Wire)!;

        foreach (var stage in query["pipeline"]?.AsArray() ?? [])
            if (stage?["page"] is JsonObject page && page.ContainsKey("limit"))
                page["limit"] = KeyedFetch.ElidedKey;

        return query;
    }

    /// <summary>
    /// The reference a resolve follows as bound (DESIGN §4.3 <c>reference</c>): each selected case with
    /// its condition, conversion and targets after <c>target</c> narrowed them, the conversion shared by
    /// every case, and <c>elements</c>.
    /// </summary>
    private static JsonObject ReferenceOf(BoundStage.Resolve resolve)
    {
        var cases = resolve.Cases is { Count: > 0 } bound
            ? bound.Select(selected => (selected.Declared.When, selected.KeyAs, Targets: selected.Targets.Select(target => target.Declared).ToList())).ToList()
            : [(When: (ReferenceCondition?)null, KeyAs: KeyAs.None, Targets: new List<ReferenceTarget> { new(resolve.TargetEntity, resolve.TargetField, null, resolve.IsRemote, resolve.TargetField == "id") })];

        return new JsonObject
        {
            ["cases"] = new JsonArray(cases.Select(selected => (JsonNode)new JsonObject
            {
                ["when"] = selected.When switch
                {
                    ReferenceCondition.PathEquals equals => new JsonObject { ["path"] = equals.Path, ["equals"] = new JsonArray(equals.Values.Select(value => (JsonNode)value).ToArray()) },
                    ReferenceCondition.Variant variant => new JsonObject { ["variant"] = new JsonArray(variant.Names.Select(name => (JsonNode)name).ToArray()) },
                    _ => null,
                },
                ["keyAs"] = KeyAsName(selected.KeyAs),
                ["targets"] = new JsonArray(selected.Targets.Select(target => (JsonNode)new JsonObject
                {
                    ["entity"] = target.Entity,
                    ["item"] = target.Item,
                    ["field"] = target.Field,
                    ["remote"] = target.IsRemote,
                }).ToArray()),
            }).ToArray()),
            ["keyAs"] = cases.Select(selected => selected.KeyAs).Distinct().Count() == 1 ? KeyAsName(cases[0].KeyAs) : null,
            ["elements"] = resolve.Elements switch
            {
                ResolveElements.First => "first",
                ResolveElements.All => "all",
                _ => null,
            },
        };
    }

    private static string? KeyAsName(KeyAs keyAs) => keyAs == KeyAs.Guid ? "guid" : null;

    /// <summary>
    /// The final shape's visible members and roots (DESIGN §4.3 <c>result.columns</c>), in row order,
    /// grouped the way the row nests them: a member of the entity under root <c>""</c>, an object
    /// member one level down under its own name, and every alias under its name — a join's select, an
    /// element's members, a lookup's array — so the studio groups a join's columns under its hop.
    /// Scalars a stage adds (an unwind index, a group output) lie under <c>""</c>.
    /// </summary>
    private static IReadOnlyList<ExplainColumn> Columns(BoundPipeline bound, BindTrace trace)
    {
        var shape = bound.FinalShape;
        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var columns = new List<ExplainColumn>();

        foreach (var stage in trace.Stages)
            foreach (var alias in stage.After.Roots.Keys)
                if (alias != Shape.ImplicitRoot && !stage.Before.Roots.ContainsKey(alias))
                    created.TryAdd(alias, stage.Index);

        foreach (var (name, node) in shape.Roots)
        {
            if (name != Shape.ImplicitRoot && shape.Dropped.Contains(name))
                continue;

            int? stage = created.TryGetValue(name, out var index) ? index : null;

            switch (node)
            {
                case ShapeNode.Entity entity when name == Shape.ImplicitRoot:
                    Members(entity.Def.Root, "", Shape.ImplicitRoot, nullable: false, null, shape, columns, expand: true);
                    break;

                case ShapeNode.Entity entity:
                    if (SelectOf(bound, name) is { } entitySelect)
                        Joined(name, entitySelect, stage, shape, columns);
                    else
                        Members(entity.Def.Root, name + ".", name, nullable: true, stage, shape, columns, expand: false);
                    break;

                case ShapeNode.Element element:
                    var elementShape = element.Source.Shape.Of ?? element.Source.Shape;

                    if (elementShape is { Kind: Kind.Object, Type: { } elementType })
                        Members(elementType, name + ".", name, nullable: false, stage, shape, columns, expand: false);
                    else
                        columns.Add(Column(name, elementShape.Kind, nullable: true, stage, Shape.ImplicitRoot));
                    break;

                case ShapeNode.Array:
                    columns.Add(Column(name, Kind.Array, nullable: false, stage, name));
                    break;

                case ShapeNode.Keyed { Many: true }:
                    columns.Add(Column(name, Kind.Array, nullable: true, stage, name));
                    break;

                case ShapeNode.Remote or ShapeNode.Keyed:
                    var select = SelectOf(bound, name) ?? [];
                    var projected = shape.Included?.Where(path => path.StartsWith(name + ".", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList() ?? [];

                    if (select.Count == 0 && projected.Count > 0)
                        select = projected.Select(path => (path[(name.Length + 1)..], Kind.Unknown)).ToList();

                    if (select.Count > 0)
                        Joined(name, select, stage, shape, columns);
                    else
                        columns.Add(Column(name, Kind.Object, nullable: true, stage, name));
                    break;

                case ShapeNode.Scalar scalar:
                    columns.Add(Column(name, scalar.Kind, nullable: false, stage, Shape.ImplicitRoot));
                    break;

                case ShapeNode.GroupOutput output:
                    columns.Add(Column(name, output.Kind, nullable: true, stage, Shape.ImplicitRoot));
                    break;
            }
        }

        return columns;
    }

    private static ExplainColumn Column(string path, Kind kind, bool nullable, int? stage, string root) =>
        new() { Path = path, Kind = Kinds.NameOf(kind), Nullable = nullable, Stage = stage, Root = root };

    /// <summary>The columns of a join alias: its select paths, each nullable (the join may find nothing), as far as a projection keeps them.</summary>
    private static void Joined(string alias, IReadOnlyList<(string Path, Kind Kind)> select, int? stage, Shape shape, List<ExplainColumn> columns)
    {
        foreach (var (path, kind) in select)
            if (shape.IsVisible(alias + "." + path) && !columns.Any(column => column.Path == alias + "." + path))
                columns.Add(Column(alias + "." + path, kind, nullable: true, stage, alias));
    }

    /// <summary>
    /// The stored members of a type as columns, the type's own first, then those only a variant has
    /// (nullable: other variants lack them). On the entity row (<paramref name="expand"/>) an object
    /// member, or an unwound collection of objects, is a root of its own with its members one level down.
    /// </summary>
    private static void Members(TypeDef type, string prefix, string root, bool nullable, int? stage, Shape shape, List<ExplainColumn> columns, bool expand)
    {
        var members = type.Members.Select(member => (Member: member, Variant: false))
            .Concat(type.Variants.SelectMany(variant => variant.Type.Members).Where(member => type.Member(member.WireName) is null)
                .DistinctBy(member => member.WireName).Select(member => (Member: member, Variant: true)));

        foreach (var (member, variant) in members)
        {
            var wire = prefix + member.WireName;

            if (!member.Stored || !shape.IsVisible(wire))
                continue;

            var memberShape = shape.Unwound.Contains(Shape.UnwoundKey(Shape.ImplicitRoot, wire)) && member.Of is { } element ? element : member;
            var memberNullable = nullable || variant || member.Nullable || member.OnlyFor is not null;

            if (expand && memberShape is { Kind: Kind.Object, Type: { } inner })
                Members(inner, wire + ".", wire, memberNullable, stage, shape, columns, expand: false);
            else
                columns.Add(Column(wire, memberShape.Kind, memberNullable, stage, root));
        }
    }

    /// <summary>
    /// The select of a join alias as the row carries it, relative to the alias: a lookup's, an inline or
    /// keyed resolve's (the union of its targets', a flat select), the owning row's of a <c>parentAs</c>
    /// (with the <c>entity</c> it names), a remote resolve's as written, or a continued stage's as
    /// written; null when the alias has none this host knows (the owner's default).
    /// </summary>
    private static List<(string Path, Kind Kind)>? SelectOf(BoundPipeline bound, string alias)
    {
        foreach (var stage in bound.Stages)
        {
            switch (stage)
            {
                case BoundStage.Lookup lookup when lookup.As == alias:
                    return lookup.Select.Select(path => (path.Wire, path.Kind)).ToList();

                case BoundStage.Resolve resolve when resolve.As == alias:
                    return Flat(TargetsOf(resolve).Select(target => target.IsRemote
                        ? target.RemoteSelect?.Select(path => (path, Kind.Unknown))
                        : target.Select?.Select(path => (path.Wire, path.Kind))));

                case BoundStage.Resolve resolve when resolve.ParentAs == alias:
                    var parent = Flat(TargetsOf(resolve).Select(target => target.IsRemote
                        ? target.RemoteParentSelect?.Select(path => (path, Kind.Unknown))
                        : target.ParentSelect?.Select(path => (path.Wire, path.Kind))));

                    return parent is null ? null : [("entity", Kind.String), .. parent];

                case ContinuedStage continued when continued.Aliases.Contains(alias):
                    return continued.Stage.Resolve is { As: var resolved, Select: { Count: > 0 } written } && resolved == alias
                        ? written.Select(path => (path, Kind.Unknown)).ToList()
                        : null;
            }
        }

        return null;
    }

    /// <summary>The paths of every target's select in first-seen order, or null when no target has one.</summary>
    private static List<(string Path, Kind Kind)>? Flat(IEnumerable<IEnumerable<(string Path, Kind Kind)>?> selects)
    {
        var known = selects.Where(select => select is not null).ToList();

        return known.Count == 0 ? null : known.SelectMany(select => select!).DistinctBy(pair => pair.Path).ToList();
    }

    private static IEnumerable<BoundResolveTarget> TargetsOf(BoundStage.Resolve resolve) =>
        resolve.Cases is { Count: > 0 } cases
            ? cases.SelectMany(selected => selected.Targets)
            : [new BoundResolveTarget(new ReferenceTarget(resolve.TargetEntity, resolve.TargetField, null, resolve.IsRemote, resolve.TargetField == "id"),
                resolve.Target, resolve.TargetFieldStorage, null, resolve.Select, resolve.RemoteSelect, resolve.Filter, resolve.RemoteFilter, resolve.TargetScope, null, null, [])];

    /// <summary>The members every explain answer carries, valid or not.</summary>
    private ExplainResult Answer(RequestContext context, bool valid, IReadOnlyList<QueryValidationError> errors, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<ExplainStep> steps, ExplainShapeResult? result, ExplainRequest request) => new()
    {
        Valid = valid,
        Contract = context.Contract,
        Engine = new ExplainEngine
        {
            Version = EngineCapabilities.Version,
            Capabilities = EngineCapabilities.Of(remoteClient, options.Compat.Enabled, options.Explain.Enabled),
        },
        Errors = errors,
        Diagnostics = diagnostics,
        Notes = [],
        Steps = steps,
        Result = result,
        // Each caller fills in describe (DESIGN §4.2): answered once per request, valid or not.
        Describe = [],
    };

    /// <summary>One step per caller stage: kind, status, the aliases it created and the shape after it (DESIGN §4.3).</summary>
    private static IReadOnlyList<ExplainStep> Steps(BindTrace? trace) =>
        trace is null ? [] : trace.Stages.Select(stage => new ExplainStep
        {
            Index = stage.Index,
            Kind = stage.Kind,
            Status = stage.Status switch
            {
                StageStatus.Error => "error",
                StageStatus.Skipped => "skipped",
                _ => "ok",
            },
            Creates = Created(stage.Before, stage.After),
            ShapeAfter = Summary(stage.After),
        }).ToList();

    /// <summary>The roots <paramref name="after"/> has that <paramref name="before"/> did not, or holds differently; poisoned ones are not created.</summary>
    private static IReadOnlyList<ExplainCreated> Created(Shape before, Shape after)
    {
        var created = new List<ExplainCreated>();

        foreach (var (alias, node) in after.Roots)
        {
            if (alias == Shape.ImplicitRoot || node is ShapeNode.Poisoned)
                continue;

            if (before.Roots.TryGetValue(alias, out var earlier) && Equals(earlier, node))
                continue;

            created.Add(node switch
            {
                ShapeNode.Entity entity => new ExplainCreated { Alias = alias, Node = "entity", Entity = entity.Def.Id },
                ShapeNode.Element element => new ExplainCreated { Alias = alias, Node = "element", Entity = element.Def.Id, Source = element.Source.Wire },
                ShapeNode.Array array => new ExplainCreated { Alias = alias, Node = "array", Entity = array.Target.Id },
                ShapeNode.Remote remote => new ExplainCreated { Alias = alias, Node = "remote", Entities = [remote.TargetEntity] },
                ShapeNode.Keyed keyed => new ExplainCreated
                {
                    Alias = alias,
                    Node = "keyed",
                    Entities = keyed.Targets.Select(target => target.Item is null ? target.Entity.Id : target.Entity.Id + "#" + target.Item.Wire).ToList(),
                },
                ShapeNode.Scalar scalar => new ExplainCreated { Alias = alias, Node = "scalar", Kind = Kinds.NameOf(scalar.Kind) },
                ShapeNode.GroupOutput output => new ExplainCreated { Alias = alias, Node = "group", Kind = Kinds.NameOf(output.Kind) },
                _ => new ExplainCreated { Alias = alias, Node = "unknown" },
            });
        }

        return created;
    }

    /// <summary>A shape as explain reports it: paging, grouped, the unwound collections, the inclusion projection.</summary>
    private static ExplainShapeSummary Summary(Shape shape) => new()
    {
        Paging = Paging(shape),
        Grouped = shape.Grouped,
        Unwound = shape.Unwound
            .Select(key => key.Split('|', 2))
            .Select(parts => parts[0].Length == 0 ? parts[1] : parts[0] + "." + parts[1])
            .Order(StringComparer.Ordinal)
            .ToList(),
        Projection = shape.Included?.Order(StringComparer.Ordinal).ToList(),
    };

    /// <summary>The final shape's paging; its columns are filled from the bound pipeline once the request binds (<see cref="Columns"/>).</summary>
    private static ExplainShapeResult ResultOf(Shape shape) => new() { Paging = Paging(shape), Columns = [] };

    private static string Paging(Shape shape) => shape.IsRootShape ? "cursor" : "offset";

    /// <summary>
    /// The opt-in index advisory (DESIGN §4.1): the index lists of the entity's collection and of
    /// every collection a <c>$lookup</c> joins, matched statically against the leading match, the
    /// sort and each join field. It reads <c>listIndexes</c> only (cached by the index source) and
    /// never runs the pipeline.
    /// </summary>
    private async Task<IReadOnlyList<JsonNode>?> AdviseAsync(BoundPipeline bound, CompiledQuery compiled, CancellationToken cancellationToken)
    {
        if (indexes is null)
            return null;

        var listed = await indexes.IndexesAsync(bound.Entity, cancellationToken).ConfigureAwait(false);
        var joined = new Dictionary<string, IReadOnlyList<BsonDocument>>(StringComparer.Ordinal);

        foreach (var target in JoinedEntities(bound))
            if (!joined.ContainsKey(target.Collection))
                joined[target.Collection] = await indexes.IndexesAsync(target, cancellationToken).ConfigureAwait(false);

        return IndexAdvisor.Advise(compiled.PageStages, listed, joined, compiled.Collation);
    }

    /// <summary>The entities the aggregate joins with <c>$lookup</c>: every lookup's child and every inline resolve's target.</summary>
    private static IEnumerable<EntityDef> JoinedEntities(BoundPipeline bound)
    {
        foreach (var stage in bound.Stages)
        {
            switch (stage)
            {
                case BoundStage.Lookup lookup:
                    yield return lookup.From;
                    break;

                case BoundStage.Resolve { IsRemote: false, Executor: ResolveExecutor.Inline, Target: { } target }:
                    yield return target;
                    break;
            }
        }
    }

    private static JsonNode Relaxed(BsonDocument stage) =>
        JsonNode.Parse(stage.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }), documentOptions: Deep)!;

    /// <summary>Parses as deep as the wire writes (<see cref="OxQLJson.MaxDepth"/>): an emitted stage nests expressions in expressions.</summary>
    private static readonly JsonDocumentOptions Deep = new() { MaxDepth = OxQLJson.MaxDepth };

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
