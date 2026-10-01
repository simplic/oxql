using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo.Explain;
using OxQL.Mongo.Resolve;

namespace OxQL.Mongo;

/// <summary>
/// Explain: the bind trace, normalised (improvement plan §3.E). One answer says everything a builder
/// shows beyond the schema documents it already holds: each stage with its placement and the shape
/// after it, every alias, the types of the roots by reference with the rules that say where their
/// members stand, the result columns, the owners asked. Nothing is derived a second way: the shapes are the
/// binder's (<see cref="BindTrace"/>), the owner facts are the plan a run builds
/// (<see cref="KeyedFetch.Explain(BoundPipeline, BoundStage.Resolve, bool, IRemoteQueryClient?)"/>,
/// <see cref="KeyedFetch.Checks(BoundPipeline, BoundStage.Resolve, bool, IRemoteQueryClient?, bool)"/>),
/// and what lies at an owner is what that owner answered.
/// </summary>
public sealed partial class MongoQueryEngine
{
    /// <inheritdoc/>
    public async Task<ExplainOutcome> ExplainAsync(ExplainRequest request, RequestContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // An explain past its bounds is refused before the model is read (ExplainLimits).
        if (ExplainLimits.Check(request, context.Options.Explain) is { } limited)
            return new ExplainOutcome.Refused(limited);

        var wall = Stopwatch.StartNew();

        context = Reaching(context);

        var binding = await new Binder(models.Model, cursors).BindAsync(request.Query, context, explain: true, cancellationToken);

        // What stops explain before binding stays a refusal: a request without an organisation.
        if (binding is BindOutcome.Failed { Refusal.Status: not 400 } refused)
            return new ExplainOutcome.Refused(refused.Refusal);

        var depth = Math.Clamp(request.ShapeDepth ?? context.Options.Explain.DefaultShapeDepth, 1, Math.Max(1, context.Options.Explain.MaxShapeDepth));
        var types = new ExplainTypes(models.Model, context, binding.Trace, depth, request.IncludesDocs, cancellationToken, request.IncludesTypes, models.SchemaRevision);

        // The entity the pipeline entered leads the types, before what the owners answer.
        if (request.IncludesShape && binding.Trace is { } entered)
            await types.LocalAsync(entered.Entry.Entity, null).ConfigureAwait(false);

        // The owners' answers share one budget of time and calls (DESIGN §4.3, plan §3.E protection).
        var owners = new RemoteExplain(remote, explainCache, context, request, (owned, token) => ExplainOwnedAsync(owned, context, token), types, wall.Elapsed);
        var draft = new Draft(request, context, binding, types, owners, depth);

        if (binding is BindOutcome.Failed failed)
            return new ExplainOutcome.Success(await AnswerAsync(draft, valid: false, failed.Refusal.Errors ?? [], cancellationToken).ConfigureAwait(false));

        var bound = ((BindOutcome.Bound)binding).Pipeline;
        var compiled = MongoCompiler.Compile(bound, CompileOptionsFor(context));
        var strict = context.Contract == 2 && request.Query.IsStrict;

        draft.Bound = bound;
        draft.Strict = strict;

        // Explain plans the owner queries by the owners' facts a run plans by, so it reads them first
        // as a run does, within the explain budget (RL-3).
        await KeyedFetch.ReadOwnerFactsAsync(remote, compiled.KeyedResolves, owners.Remaining, cancellationToken).ConfigureAwait(false);

        draft.Indexes = Notes.CallerIndexes(bound, request.Query);
        draft.Notes.AddRange(Notes.Of(bound, request.Query, draft.Indexes, strict, context.Contract, options));
        Place(draft);

        // A request this host cannot run is not valid, though it binds: the same refusal the query
        // path gives, as an error.
        if ((compiled.KeyedResolves.Any(resolve => resolve.IsRemote) || compiled.SemiJoins.Count > 0) && !remoteClient)
            return new ExplainOutcome.Success(await AnswerAsync(draft, valid: false,
                [new QueryValidationError { Code = Codes.ResolveUnavailable, Message = "This host has no remote query client; a remote resolve cannot run." }], cancellationToken).ConfigureAwait(false));

        // An owner known to run an engine before 2.1 is refused by a run before anything is sent; explain
        // says so at the same stage (RL-2).
        if (KeyedFetch.IncapableOwner(bound, compiled.KeyedResolves, strict, remote) is { } incapable)
        {
            if (incapable.Stage is { } at)
                draft.Failed.Add(at);

            return new ExplainOutcome.Success(await AnswerAsync(draft, valid: false, [incapable], cancellationToken).ConfigureAwait(false));
        }

        // The remote check (DESIGN §4.3): the owner queries of every keyed stage are bound by their
        // owners' internal explain; an owner error there is this request's, at the caller's stage.
        var (checkErrors, checkNotes) = await owners.CheckAsync(bound, strict, cancellationToken).ConfigureAwait(false);

        draft.Notes.AddRange(checkNotes);

        if (checkErrors.Count > 0)
        {
            foreach (var error in checkErrors)
                if (error.Stage is { } at)
                    draft.Failed.Add(at);

            return new ExplainOutcome.Success(await AnswerAsync(draft, valid: false, checkErrors, cancellationToken).ConfigureAwait(false));
        }

        // The advisory reads index lists; a list that cannot be read is a note, never a failed explain.
        try
        {
            draft.Advisory = request.IncludesIndexes ? await AdviseAsync(bound, compiled, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            draft.Notes.Add(new Diagnostic
            {
                Code = Notes.IndexAdvice,
                Message = "The index lists could not be read, so no index advice is given.",
                Params = new Dictionary<string, object?> { ["reason"] = exception.GetType().Name },
            });
        }

        foreach (var line in draft.Advisory ?? [])
            draft.Notes.Add(Notes.Index(
                line["field"]?.GetValue<string>() ?? "",
                line["used"] is JsonValue used && used.TryGetValue<bool>(out var flag) ? flag : null,
                line["index"]?.GetValue<string>(),
                line["note"]?.GetValue<string>()));

        if (request.IncludesPlan)
            draft.Plan = new ExplainPlan
            {
                Bound = JsonNode.Parse(bound.Canonical, documentOptions: Deep)!,
                Stages = compiled.PageStages.Select(Relaxed).ToList(),
                Count = compiled.CountStages?.Select(Relaxed).ToList(),
                Collation = compiled.Collation is null ? null : Relaxed(compiled.Collation),
            };

        return new ExplainOutcome.Success(await AnswerAsync(draft, valid: true, [], cancellationToken).ConfigureAwait(false));
    }

    /// <summary>What one explain answer is assembled from.</summary>
    private sealed class Draft(ExplainRequest request, RequestContext context, BindOutcome binding, ExplainTypes types, RemoteExplain owners, int depth)
    {
        public ExplainRequest Request { get; } = request;

        public RequestContext Context { get; } = context;

        public BindOutcome Binding { get; } = binding;

        public ExplainTypes Types { get; } = types;

        public RemoteExplain Owners { get; } = owners;

        public int Depth { get; } = depth;

        public BoundPipeline? Bound { get; set; }

        public bool Strict { get; set; }

        public IReadOnlyList<int?> Indexes { get; set; } = [];

        public List<Diagnostic> Notes { get; } = [];

        /// <summary>The caller stages an error of the remote check or of an owner's capability lies at.</summary>
        public HashSet<int> Failed { get; } = [];

        /// <summary>Per caller stage that is a join that runs: its executor, phase, the service that runs it and the owner it is sent to.</summary>
        public Dictionary<int, (string Executor, string Phase, string Host, string? Owner, bool Remote)> Placements { get; } = [];

        /// <summary>Per keyed stage, by its alias, the owner queries a run would send.</summary>
        public Dictionary<string, IReadOnlyList<ExplainedOwnerQuery>> Explained { get; } = new(StringComparer.Ordinal);

        /// <summary>Per alias whose rows an owner holds, the type its owners' answers point it to (null: none answered).</summary>
        public Dictionary<string, string?> OwnerTypes { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<JsonNode>? Advisory { get; set; }

        public ExplainPlan? Plan { get; set; }

        /// <summary>The service this host is: the namespace of the entity the pipeline entered.</summary>
        public string? Host => Binding.Trace?.Entry.Entity.Namespace;
    }

    /// <summary>
    /// An owner query of a local keyed stage explained at this host itself, under the context its SelfOwner
    /// runs one with (internal, so it may carry <c>keyedBy</c> and the outer explain's budget, and contract 2):
    /// the answer in the internal explain route's wire form, or null when explain refuses it outright. The
    /// owner query carries strictly fewer join stages than the query it came from, so this ends by construction.
    /// </summary>
    private async Task<JsonObject?> ExplainOwnedAsync(ExplainRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var outcome = await ExplainAsync(request, context with { Internal = true, Contract = 2 }, cancellationToken).ConfigureAwait(false);

        return outcome is ExplainOutcome.Success success ? JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire) as JsonObject : null;
    }

    // ---- placement ------------------------------------------------------------------------------

    /// <summary>
    /// Where each join runs (DESIGN §4.3): its executor and phase, the service that runs it, the owner
    /// queries of a keyed stage; and the join-placement note of each join run on this host.
    /// </summary>
    private void Place(Draft draft)
    {
        var bound = draft.Bound!;
        var host = bound.Entity.Namespace;

        for (var position = 0; position < bound.Stages.Count; position++)
        {
            if (draft.Indexes[position] is not { } index)
                continue;

            switch (bound.Stages[position])
            {
                // A join nothing reads and the row does not show is not run: it has no placement.
                case BoundStage.Lookup dropped when !MongoCompiler.JoinRuns(bound, position, dropped.As):
                    break;

                case BoundStage.Resolve droppedResolve when !MongoCompiler.JoinRuns(bound, position, droppedResolve.As)
                    && !(droppedResolve.ParentAs is { } droppedParent && MongoCompiler.JoinRuns(bound, position, droppedParent)):
                    break;

                case BoundStage.Lookup lookup:
                {
                    var after = MongoCompiler.JoinsAfterPage(bound.Stages, position, lookup.As);

                    draft.Placements[index] = ("inline", after ? "afterPage" : "beforePage", host, null, false);
                    draft.Notes.Add(Notes.Join(index, lookup.As, "lookup", after));
                    break;
                }

                case BoundStage.Resolve resolve when resolve.IsRemote || resolve.Executor == ResolveExecutor.Keyed:
                {
                    var explained = KeyedFetch.Explain(bound, resolve, draft.Strict, remote);
                    var first = explained.FirstOrDefault(owner => owner.Remote) ?? explained[0];

                    draft.Explained[resolve.As] = explained;
                    draft.Placements[index] = (resolve.IsRemote ? "keyed-remote" : "keyed-local", "afterPage", first.Service, first.Service, first.Remote);
                    draft.Notes.Add(Notes.Join(index, resolve.As, resolve.Kind, afterPage: true));

                    if (resolve.RemoteLookup is { } remoteLookup)
                        draft.Notes.Add(Notes.RemoteLookupBounds(index, resolve.As, remoteLookup, KeyedFetch.KeysPerQuery(options, remote, remoteLookup.Service, remoteLookup.PerKey)));
                    break;
                }

                case BoundStage.Resolve resolve:
                {
                    var after = MongoCompiler.JoinsAfterPage(bound.Stages, position, resolve.As);

                    draft.Placements[index] = ("inline", after ? "afterPage" : "beforePage", host, null, false);
                    draft.Notes.Add(Notes.Join(index, resolve.As, "resolve", after));
                    break;
                }

                case ContinuedStage continued:
                {
                    var carrying = Carrying(draft, continued);
                    var first = carrying.FirstOrDefault(owner => owner.Remote) ?? carrying.FirstOrDefault();

                    draft.Placements[index] = ("continued", "owner", first?.Service ?? host, first?.Service, first?.Remote ?? false);
                    break;
                }
            }
        }
    }

    /// <summary>The owner queries of a continued stage's keyed stage that carry it.</summary>
    private static List<ExplainedOwnerQuery> Carrying(Draft draft, ContinuedStage continued) =>
        draft.Explained.TryGetValue(continued.Anchor, out var explained)
            ? explained.Where(owner => owner.Continued.Any(stage => ReferenceEquals(stage, continued))).ToList()
            : [];

    // ---- the answer -----------------------------------------------------------------------------

    /// <summary>The answer of <paramref name="draft"/>: the members every explain answer carries, valid or not.</summary>
    private async Task<ExplainResult> AnswerAsync(Draft draft, bool valid, IReadOnlyList<QueryValidationError> errors, CancellationToken cancellationToken)
    {
        var request = draft.Request;
        var context = draft.Context;
        var trace = draft.Binding.Trace;
        var shapes = request.IncludesShape && trace is not null;
        var owners = Owners(draft);
        var creates = new Dictionary<int, List<string>>();

        var aliases = trace is null ? [] : await AliasesAsync(draft, owners, creates, cancellationToken).ConfigureAwait(false);
        var stages = new List<ExplainStage>();
        ExplainEntry? entry = null;

        if (trace is not null)
        {
            string? OwnerType(string alias) => draft.OwnerTypes.GetValueOrDefault(alias);

            if (shapes)
                entry = new ExplainEntry { Shape = await draft.Types.ShapeAsync(trace.Stages.Count > 0 ? trace.Stages[0].Before : trace.Final, OwnerType).ConfigureAwait(false) };

            // The ledger, by stage: what the binder recorded, and for a stage continued at an owner what
            // that owner bound it with.
            var reads = trace.Reads.GroupBy(read => read.Stage).ToDictionary(group => group.Key, group => group.ToList());

            foreach (var stage in trace.Stages)
                stages.Add(new ExplainStage
                {
                    Index = stage.Index,
                    Kind = stage.Kind,
                    Status = stage.Status == StageStatus.Error || draft.Failed.Contains(stage.Index) ? "error" : stage.Status == StageStatus.Skipped ? "skipped" : "ok",
                    Placement = draft.Placements.TryGetValue(stage.Index, out var placed)
                        ? new ExplainPlacement { Executor = placed.Executor, Phase = placed.Phase, Host = placed.Host, Owner = placed.Owner is { } service ? owners.IndexOf(service, placed.Remote) : null }
                        : null,
                    Reads = ReadsOf(draft, stage.Index, reads.GetValueOrDefault(stage.Index) ?? []),
                    Creates = creates.TryGetValue(stage.Index, out var created) ? created : [],
                    Shape = shapes ? await draft.Types.ShapeAsync(stage.After, OwnerType).ConfigureAwait(false) : null,
                });
        }

        var catalog = await draft.Types.CatalogAsync(request, draft.Owners).ConfigureAwait(false);

        owners.Read(draft.Owners);

        var notes = draft.Notes.Concat(draft.Owners.LimitNotes)
            .Where(note => request.IncludesNotes || IsAboutTheAnswer(note) || (note.Code == Notes.IndexAdvice && request.IncludesIndexes))
            .ToList();
        // Complete while every owner asked answered, no limit left a part out, and every alias has its type.
        var complete = draft.Owners.Complete && aliases.All(alias => alias.Value?["complete"]?.GetValue<bool>() != false);
        // This host's document, and each owner's as its answer named it (its own owners' among them).
        var schema = new SortedDictionary<string, string?>(StringComparer.Ordinal);

        if ((draft.Host ?? models.Model.Entities.Values.FirstOrDefault()?.Namespace) is { } host)
            schema[host] = models.SchemaRevision;

        foreach (var (service, named) in draft.Owners.Revisions)
            if (!schema.TryGetValue(service, out var known) || known is null)
                schema[service] = named;

        var revision = new ExplainRevision { Schema = schema, Addons = draft.Types.AddonRevision() };
        var engine = new ExplainEngine
        {
            Version = EngineCapabilities.Version,
            Capabilities = EngineCapabilities.Of(remoteClient, options.Compat.Enabled, options.Explain.Enabled),
        };
        ExplainShapeResult? result = null;

        if (trace is not null)
            result = new ExplainShapeResult
            {
                Paging = trace.Final.IsRootShape ? "cursor" : "offset",
                Columns = draft.Bound is { } bound ? Columns(bound, trace) : [],
            };

        var answer = new ExplainResult
        {
            Valid = valid,
            Contract = context.Contract,
            Engine = engine,
            Revision = revision,
            Cache = new ExplainCache
            {
                MaxAge = (int)ExplainForwardCache.Ttl.TotalSeconds,
                DependsOn = owners.Entries.Where(owner => owner["remote"]!.GetValue<bool>()).Select(owner => owner["service"]!.GetValue<string>())
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                Complete = complete,
            },
            Errors = errors,
            Diagnostics = draft.Binding.Diagnostics,
            Notes = Ordered(notes),
            Entry = entry,
            Stages = stages,
            Aliases = aliases,
            Types = draft.Types.Types,
            Rules = draft.Types.Rules,
            FlagSets = draft.Types.Tables ? draft.Types.FlagSets : null,
            Result = result,
            Owners = owners.Entries.Select(owner => (JsonNode)owner).ToList(),
            Catalog = catalog,
            Plan = valid ? draft.Plan : null,
            Advisory = valid ? draft.Advisory : null,
        };

        // An answer far below the cap by what it holds is not measured: a member row is some 60 bytes; a
        // catalog's rows, the descriptions and a plan are the caller's to ask for and are measured.
        if (answer.Plan is not null || catalog.Count > 0 || request.IncludesDocs || draft.Types.MemberCount * 200L + 100_000 > context.Options.Explain.MaxAnswerBytes)
            answer = Trimmed(answer, notes, context.Options.Explain.MaxAnswerBytes);

        return answer with { Etag = EtagOf(request, answer) };
    }

    /// <summary>
    /// What one stage reads (<c>{ path, use, alias? }</c>, each once, in the order bound): the binder's
    /// ledger; for a stage continued at an owner, the reads the owner answered, which include what only
    /// its model knows (the member that picks a reference's case).
    /// </summary>
    private static IReadOnlyList<JsonNode> ReadsOf(Draft draft, int stage, IReadOnlyList<PathRead> recorded)
    {
        if (draft.Owners.ReadsOf(stage) is { Count: > 0 } answered)
            return answered.Select(read => read.DeepClone()).ToList();

        var reads = new List<JsonNode>();

        foreach (var read in recorded.DistinctBy(read => (read.Path, read.Use)))
        {
            var entry = new JsonObject { ["path"] = read.Path, ["use"] = read.UseName };

            if (read.Alias is not null)
                entry["alias"] = read.Alias;

            reads.Add(entry);
        }

        return reads;
    }

    /// <summary>What a join loads and shows under an alias, as the binder inferred it: <c>loads</c>, <c>shows</c> and <c>hint</c>, each a list of paths or null.</summary>
    private static void Loads(JsonObject entry, JoinLoad? load)
    {
        if (load is null)
            return;

        entry["loads"] = load.Loads is null ? null : new JsonArray(load.Loads.Select(path => (JsonNode)path).ToArray());
        entry["shows"] = load.Shows is null ? null : new JsonArray(load.Shows.Select(path => (JsonNode)path).ToArray());
        entry["hint"] = load.Hint is null ? null : new JsonArray(load.Hint.Select(path => (JsonNode)path).ToArray());
    }

    /// <summary>The notes that say what the answer itself lacks; they are answered also without <c>include: "notes"</c>.</summary>
    private static bool IsAboutTheAnswer(Diagnostic note) => note.Code is Notes.RemoteUnchecked or Notes.ExplainLimit or Notes.ExplainTrimmed;

    /// <summary>The notes in stage order, request-wide ones (no stage) last; within a stage as they were found.</summary>
    private static IReadOnlyList<Diagnostic> Ordered(List<Diagnostic> notes) =>
        notes.OrderBy(note => note.Stage ?? int.MaxValue).ToList();

    /// <summary>
    /// The answer within <paramref name="max"/> bytes (<c>Explain.MaxAnswerBytes</c>): a larger one loses
    /// the types below their first level, then the plan, and says so (<c>cache.complete</c> false, an
    /// <c>EXPLAIN_TRIMMED</c> note). A caller reads what was cut with a <c>catalog</c> entry.
    /// </summary>
    private static ExplainResult Trimmed(ExplainResult answer, List<Diagnostic> notes, int max)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(answer, OxQLJson.Wire).Length;

        if (bytes <= max)
            return answer;

        var dropped = new List<string>();
        var types = new JsonObject();
        var cut = false;

        // Only an answer that writes the member rows out (include: "types") has levels to lose.
        foreach (var (key, type) in answer.Types)
        {
            var copy = (JsonObject)type!.DeepClone();

            if (copy["members"] is JsonArray rows && rows.OfType<JsonArray>().Any(row => row[0]!.GetValue<string>().Contains('.', StringComparison.Ordinal)))
            {
                copy["members"] = new JsonArray(rows.OfType<JsonArray>().Where(row => !row[0]!.GetValue<string>().Contains('.', StringComparison.Ordinal)).Select(row => row.DeepClone()).ToArray());
                copy["truncated"] = true;
                cut = true;
            }

            types[key] = copy;
        }

        if (cut)
        {
            dropped.Add("types");
            answer = answer with { Types = types };
        }

        if (answer.Plan is not null && (!cut || JsonSerializer.SerializeToUtf8Bytes(answer, OxQLJson.Wire).Length > max))
        {
            dropped.Add("plan");
            answer = answer with { Plan = null };
        }

        // Nothing an explain can leave out: the answer stands as it is.
        if (dropped.Count == 0)
            return answer;

        var left = string.Join(" and ", dropped.Select(part => part == "types" ? "the types keep their first level only" : "the plan is left out"));

        notes.Add(new Diagnostic
        {
            Code = Notes.ExplainTrimmed,
            Message = $"The answer was {bytes} bytes, more than the {max} an explain answers; {left}.{(cut ? " A catalog entry reads the members of one entity." : "")}",
            Params = new Dictionary<string, object?> { ["dropped"] = dropped, ["bytes"] = bytes, ["max"] = max },
        });

        return answer with { Notes = Ordered(notes), Cache = answer.Cache with { Complete = false } };
    }

    /// <summary>A weak validator over what the answer depends on: the request, the revisions, the capabilities and whether it is complete.</summary>
    private static string EtagOf(ExplainRequest request, ExplainResult answer)
    {
        var text = new StringBuilder();

        text.Append(JsonSerializer.Serialize(request with { Budget = null }, OxQLJson.Wire)).Append('\n')
            .Append(answer.Contract).Append('\n')
            .Append(answer.Engine.Version).Append('\n')
            .AppendJoin(',', answer.Engine.Capabilities).Append('\n')
            .AppendJoin(',', answer.Revision.Schema.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)).Append('\n')
            .Append(answer.Revision.Addons).Append('\n')
            .Append(answer.Cache.Complete);

        return "W/\"x3:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32].ToLowerInvariant() + "\"";
    }

    // ---- owners ---------------------------------------------------------------------------------

    /// <summary>The <c>owners</c> list of one answer: each owner service once, this host among them when it answers keyed stages of its own.</summary>
    private sealed class OwnerList(Func<string, JsonObject> route)
    {
        private readonly List<(string Service, bool Remote, string? Via)> keys = [];

        public List<JsonObject> Entries { get; } = [];

        /// <summary>The index of the owner <paramref name="service"/>, added when it is new.</summary>
        public int IndexOf(string service, bool remote, string? via = null)
        {
            var at = keys.FindIndex(key => key.Service == service && key.Remote == remote && key.Via == via);

            if (at >= 0)
                return at;

            keys.Add((service, remote, via));

            var entry = new JsonObject { ["service"] = service, ["remote"] = remote };

            if (via is not null)
                entry["via"] = via;

            entry["route"] = route(service);
            // This host answers its own keyed stages in process; an owner is not asked until a check asks it.
            entry["answered"] = remote ? (bool?)null : true;
            entry["calls"] = 0;
            Entries.Add(entry);

            return Entries.Count - 1;
        }

        /// <summary>What the calls of this explain said of each owner: whether it answered, what it cost and what it runs.</summary>
        public void Read(RemoteExplain calls)
        {
            foreach (var use in calls.Owners)
            {
                var entry = Entries[IndexOf(use.Service, remote: true, use.Via)];

                entry["answered"] = use.Answered;

                if (use.Reason is not null && use.Answered != true)
                    entry["reason"] = use.Reason;

                entry["calls"] = use.Calls;
                entry["cached"] = use.Answered == true && use.Cached;
                entry["ms"] = use.Ms;

                if (use.Engine is not null)
                    entry["engine"] = use.Engine.DeepClone();
            }
        }
    }

    /// <summary>The owners the bound request reaches, in pipeline order; with <c>include: "plan"</c> each with the queries it is sent, keys elided.</summary>
    private OwnerList Owners(Draft draft)
    {
        var owners = new OwnerList(Route);

        if (draft.Bound is not { } bound)
            return owners;

        foreach (var resolve in bound.Stages.OfType<BoundStage.Resolve>())
        {
            if (!draft.Explained.TryGetValue(resolve.As, out var explained))
                continue;

            foreach (var query in explained)
            {
                var entry = owners.Entries[owners.IndexOf(query.Service, query.Remote)];

                if (!draft.Request.IncludesPlan)
                    continue;

                if (entry["queries"] is not JsonArray queries)
                    entry["queries"] = queries = [];

                queries.Add(new JsonObject
                {
                    ["stage"] = bound.CallerIndexOf(resolve),
                    ["alias"] = resolve.As,
                    ["target"] = query.Target,
                    ["grouped"] = query.Grouped,
                    ["query"] = QueryOf(query),
                    ["continued"] = new JsonArray(query.Continued.Select(stage => (JsonNode)stage.OriginIndex).ToArray()),
                    ["notApplicable"] = new JsonArray(query.NotApplicable.Select(stage => (JsonNode)stage.OriginIndex).ToArray()),
                });
            }
        }

        return owners;
    }

    /// <summary>
    /// An owner's route. The engine knows an owner's service, which names its API (<c>&lt;service&gt;-api</c>);
    /// the version the host routes to is the remote client's (<see cref="IRemoteOwnerInfo.ApiVersionOf"/>), null when it does not say.
    /// </summary>
    private JsonObject Route(string service)
    {
        var route = new JsonObject { ["apiName"] = service + "-api" };

        if ((remote as IRemoteOwnerInfo)?.ApiVersionOf(service) is { } version)
            route["apiVersion"] = version;

        return route;
    }

    /// <summary>An owner query in wire form, the page's size elided with its keys: it is the number of keys times the rows per key.</summary>
    private static JsonNode QueryOf(ExplainedOwnerQuery owner)
    {
        var query = JsonSerializer.SerializeToNode(owner.Query, OxQLJson.Wire)!;

        foreach (var stage in query["pipeline"]?.AsArray() ?? [])
            if (stage?["page"] is JsonObject page && page.ContainsKey("limit"))
                page["limit"] = KeyedFetch.ElidedKey;

        return query;
    }

    // ---- aliases --------------------------------------------------------------------------------

    /// <summary>
    /// Every alias a stage creates, by name: the stage and the node from the bind trace; for a join of a
    /// request that binds, the reference it follows, its targets with the owner each is asked at and the
    /// stages continued there, and what a continued stage continues from; its type, and whether its
    /// owners said everything about it (<c>complete</c>).
    /// </summary>
    private async Task<JsonObject> AliasesAsync(Draft draft, OwnerList owners, Dictionary<int, List<string>> creates, CancellationToken cancellationToken)
    {
        var trace = draft.Binding.Trace!;
        var host = trace.Entry.Entity.Namespace;
        var aliases = new JsonObject();
        var nodes = new Dictionary<string, (ShapeNode Node, int Stage)>(StringComparer.Ordinal);
        var shapes = draft.Request.IncludesShape;

        foreach (var stage in trace.Stages)
            foreach (var (alias, node) in Created(stage.Before, stage.After))
            {
                if (!creates.TryGetValue(stage.Index, out var list))
                    creates[stage.Index] = list = [];

                list.Add(alias);

                // An alias a later stage holds differently (an unwound lookup alias) stays the alias it was
                // created as; the later form is said beside it.
                if (aliases[alias] is JsonObject known)
                {
                    if (known["becomes"] is not JsonArray becomes)
                        known["becomes"] = becomes = [];

                    becomes.Add(new JsonObject { ["stage"] = stage.Index, ["node"] = NodeOf(node) });
                    nodes[alias] = (node, stage.Index);
                    continue;
                }

                nodes[alias] = (node, stage.Index);

                var entry = new JsonObject { ["stage"] = stage.Index, ["node"] = NodeOf(node) };

                switch (node)
                {
                    case ShapeNode.Entity entity:
                        entry["entities"] = new JsonArray(entity.Def.Id);
                        break;

                    case ShapeNode.Element element:
                        entry["entities"] = new JsonArray(element.Def.Id);
                        entry["source"] = element.Source.Wire;
                        break;

                    case ShapeNode.Array array:
                        entry["entities"] = new JsonArray(array.Target.Id);
                        break;

                    case ShapeNode.Keyed keyed:
                        entry["entities"] = new JsonArray(keyed.Targets.Select(target => (JsonNode)(target.Item is null ? target.Entity.Id : target.Entity.Id + "#" + target.Item.Wire)).ToArray());

                        if (keyed.Many)
                            entry["many"] = true;
                        break;

                    // The alias of a resolve continued without a target holds what the reference it follows
                    // reaches, which only its owners' models declare: no entity until the check hears it from them.
                    case ShapeNode.Remote { TargetOpen: true }:
                        entry["entities"] = new JsonArray();
                        break;

                    case ShapeNode.Remote remote:
                        entry["entities"] = new JsonArray(remote.TargetEntity);
                        break;

                    case ShapeNode.Scalar scalar:
                        entry["kind"] = Kinds.NameOf(scalar.Kind);
                        break;

                    case ShapeNode.GroupOutput output:
                        entry["kind"] = Kinds.NameOf(output.Kind);
                        break;
                }

                entry["heldBy"] = host;
                entry["complete"] = true;
                entry["lookupOn"] = ExplainTypes.LookupParent(stage.After, alias);

                if (trace.Stages.FirstOrDefault(later => later.Index > stage.Index && (!later.After.Roots.ContainsKey(alias) || later.After.Dropped.Contains(alias))) is { } dropping)
                    entry["droppedAt"] = dropping.Index;

                aliases[alias] = entry;
            }

        if (draft.Bound is { } bound)
            Joins(draft, bound, owners, aliases, host);

        // The types: this host's own for what it holds; for what an owner holds, what the owners answered.
        foreach (var (alias, (node, stage)) in nodes)
        {
            if (aliases[alias] is not JsonObject entry)
                continue;

            if (node is ShapeNode.Remote remote)
            {
                var pointers = await OwnerTypesAsync(draft, alias, remote, stage, entry, cancellationToken).ConfigureAwait(false);

                draft.OwnerTypes[alias] = pointers;

                if (shapes)
                    entry["type"] = pointers;

                continue;
            }

            if (shapes)
                entry["type"] = await draft.Types.RootTypeAsync(alias, node, _ => null).ConfigureAwait(false);
        }

        // Each target's own type, once the table holds what the owners answered.
        if (shapes)
            foreach (var (_, entry) in aliases)
                foreach (var target in entry?["targets"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var named = target["target"]!.GetValue<string>();
                    var hash = named.IndexOf('#', StringComparison.Ordinal);
                    var key = ExplainTypes.TypeKey(named);

                    if (target["remote"]?.GetValue<bool>() != true && models.Model.TryResolve(hash < 0 ? named : named[..hash], out var local, out _))
                        key = await draft.Types.LocalAsync(local, hash < 0 ? null : named[(hash + 1)..]).ConfigureAwait(false);

                    target["type"] = key is not null && draft.Types.Types.ContainsKey(key) ? key : null;
                }

        return aliases;
    }

    private static string NodeOf(ShapeNode node) => node switch
    {
        ShapeNode.Entity => "entity",
        ShapeNode.Element => "element",
        ShapeNode.Array => "array",
        ShapeNode.Remote => "remote",
        ShapeNode.Keyed => "keyed",
        ShapeNode.Scalar => "scalar",
        ShapeNode.GroupOutput => "group",
        _ => "unknown",
    };

    /// <summary>The roots <paramref name="after"/> has that <paramref name="before"/> did not, or holds differently; poisoned ones are not created.</summary>
    private static List<(string Alias, ShapeNode Node)> Created(Shape before, Shape after)
    {
        var created = new List<(string, ShapeNode)>();

        foreach (var (alias, node) in after.Roots)
        {
            if (alias == Shape.ImplicitRoot || node is ShapeNode.Poisoned)
                continue;

            if (before.Roots.TryGetValue(alias, out var earlier) && Equals(earlier, node))
                continue;

            created.Add((alias, node));
        }

        return created;
    }

    /// <summary>What the bound joins say of their aliases (DESIGN §4.3): the reference, the targets, the owners, the continuation.</summary>
    private void Joins(Draft draft, BoundPipeline bound, OwnerList owners, JsonObject aliases, string host)
    {
        var outcomes = draft.Notes.Where(note => note.Code == Notes.MissingPolicy && note.Params?["alias"] is string)
            .GroupBy(note => (string)note.Params!["alias"]!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var stage in bound.Stages)
        {
            switch (stage)
            {
                case BoundStage.Lookup lookup when aliases[lookup.As] is JsonObject entry:
                    entry["targets"] = new JsonArray(new JsonObject { ["target"] = lookup.From.Id, ["service"] = host, ["remote"] = false });
                    Loads(entry, bound.Loads.GetValueOrDefault(lookup.As));
                    break;

                case BoundStage.Resolve resolve when aliases[resolve.As] is JsonObject entry:
                {
                    var targets = TargetsOf(resolve).Select(target => target.Declared).ToList();

                    // A lookup follows no reference of this host: the owner checks its path.
                    if (resolve.RemoteLookup is null)
                        entry["reference"] = ReferenceOf(resolve);

                    if (resolve.RemoteLookup is { First: false })
                        entry["many"] = true;

                    if (outcomes.TryGetValue(resolve.As, out var policy))
                        entry["outcome"] = OutcomeOf(policy);

                    Loads(entry, bound.Loads.GetValueOrDefault(resolve.As));

                    if (resolve.ParentAs is { } owning && aliases[owning] is JsonObject owningEntry)
                        Loads(owningEntry, bound.Loads.GetValueOrDefault(owning));

                    if (!draft.Explained.TryGetValue(resolve.As, out var explained))
                    {
                        entry["targets"] = new JsonArray(targets.DistinctBy(target => target.ToString())
                            .Select(target => (JsonNode)new JsonObject { ["target"] = target.ToString(), ["service"] = host, ["remote"] = false }).ToArray());
                        break;
                    }

                    var first = explained.FirstOrDefault(owner => owner.Remote) ?? explained[0];

                    // A remote alias's shape names one owner entity, but a union's rows come from each target.
                    entry["entities"] = new JsonArray(targets.Select(target => target.ToString()).Distinct(StringComparer.Ordinal).Select(target => (JsonNode)target).ToArray());
                    entry["heldBy"] = first.Service;
                    entry["targets"] = new JsonArray(explained.Select(owner => (JsonNode)new JsonObject
                    {
                        ["target"] = owner.Target,
                        ["service"] = owner.Service,
                        ["remote"] = owner.Remote,
                        ["grouped"] = owner.Grouped,
                        ["owner"] = owners.IndexOf(owner.Service, owner.Remote),
                        ["continued"] = new JsonArray(owner.Continued.Select(continued => (JsonNode)continued.OriginIndex).ToArray()),
                        ["notApplicable"] = new JsonArray(owner.NotApplicable.Select(continued => (JsonNode)continued.OriginIndex).ToArray()),
                    }).ToArray());

                    if (resolve.ParentAs is { } parentAs && aliases[parentAs] is JsonObject parent)
                    {
                        parent["parentOf"] = resolve.As;
                        parent["entities"] = new JsonArray(targets.Select(target => target.Entity).Distinct(StringComparer.Ordinal).Select(target => (JsonNode)target).ToArray());
                        parent["heldBy"] = first.Service;
                        parent["targets"] = new JsonArray(explained.DistinctBy(owner => owner.Target.Split('#')[0]).Select(owner => (JsonNode)new JsonObject
                        {
                            ["target"] = owner.Target.Split('#')[0],
                            ["service"] = owner.Service,
                            ["remote"] = owner.Remote,
                            ["owner"] = owners.IndexOf(owner.Service, owner.Remote),
                        }).ToArray());
                    }

                    break;
                }

                case ContinuedStage continued:
                {
                    var carrying = Carrying(draft, continued);
                    var first = carrying.FirstOrDefault(owner => owner.Remote) ?? carrying.FirstOrDefault();
                    var root = continued.Root.Split('.')[0];
                    var reached = draft.Owners.Reached;

                    foreach (var alias in continued.Aliases)
                    {
                        if (aliases[alias] is not JsonObject entry)
                            continue;

                        entry["heldBy"] = first?.Service ?? host;
                        entry["continuedFrom"] = new JsonObject { ["alias"] = root.Length == 0 ? continued.Anchor : root };

                        if (continued.ForTarget is { } forTarget)
                            entry["continuedFrom"]!["target"] = forTarget;

                        if (continued.Stage.Resolve is { } written)
                        {
                            if (written.ParentAs == alias && written.As is { } child)
                                entry["parentOf"] = child;

                            if (written.Elements == "all" && written.As == alias)
                                entry["many"] = true;
                        }

                        if (reached.TryGetValue(alias, out var entities))
                            entry["entities"] = new JsonArray(entities.Select(entity => (JsonNode)entity).ToArray());

                        // What the owner's join loads for the run's query is the owner's to infer and to say.
                        if (draft.Owners.LoadsOf(alias) is { } inferred)
                            foreach (var (name, value) in inferred)
                                entry[name] = value?.DeepClone();

                        if (outcomes.TryGetValue(alias, out var policy))
                            entry["outcome"] = OutcomeOf(policy);
                    }

                    break;
                }
            }
        }
    }

    /// <summary>The outcome block of a join: the data-loss outcomes its rows may have. No row member carries the outcome until a stage names one.</summary>
    private static JsonObject OutcomeOf(Diagnostic policy) => new()
    {
        ["values"] = new JsonArray((policy.Params?["dataLoss"] as IEnumerable<string> ?? []).Select(value => (JsonNode)value).ToArray()),
    };

    /// <summary>
    /// The type of an alias whose rows an owner holds, from the owners' answers (plan §3.E, K14): the
    /// targets of a remote resolve as the check of the run's owner query described them; what the
    /// owners bound a continued alias to; for a request that does not bind (no run plan to check), the
    /// owner's own description of the entity. Several targets are one union. An owner that did not
    /// answer leaves the alias without it and not <c>complete</c>; nothing else stands in.
    /// </summary>
    private async Task<string?> OwnerTypesAsync(Draft draft, string alias, ShapeNode.Remote node, int stage, JsonObject entry, CancellationToken cancellationToken)
    {
        if (!draft.Request.IncludesShape)
            return null;

        var types = draft.Types;
        var pointers = new List<string>();
        var complete = true;
        var bound = draft.Bound;
        var continued = bound?.Stages.OfType<ContinuedStage>().FirstOrDefault(each => each.Aliases.Contains(alias, StringComparer.Ordinal));

        if (continued is not null)
        {
            pointers.AddRange(draft.Owners.TypesOf(alias));
            complete = pointers.Count > 0 && Carrying(draft, continued).All(owner => !draft.Owners.Unanswered(continued.Anchor, owner.Target));
        }
        else
        {
            var targets = bound?.Stages.OfType<BoundStage.Resolve>().FirstOrDefault(resolve => resolve.As == alias || resolve.ParentAs == alias) is { } resolve
                ? TargetsOf(resolve).Select(target => (target.Declared.Entity, Item: resolve.As == alias ? target.Declared.Item : null)).Distinct().ToList()
                : UnboundTargets(draft.Request.Query, draft.Binding.Trace!, stage, alias, node);

            // What a continued stage reaches is its owners' to say, and a request that does not bind asks none.
            complete = targets.Count > 0;

            foreach (var (entityId, item) in targets)
            {
                var key = ExplainTypes.TypeKey(entityId, item);

                if (models.Model.TryResolve(entityId, out var local, out _))
                    key = await types.LocalAsync(local, item).ConfigureAwait(false);
                else if (bound is null && !types.Types.ContainsKey(key))
                    // No run plan to check: the owner describes its entity itself.
                    if (await draft.Owners.CatalogAsync(entityId.Split('.')[0], new JsonObject { ["id"] = "forwarded", ["entity"] = item is null ? entityId : entityId + "#" + item }, draft.Depth, cancellationToken)
                        .ConfigureAwait(false) is { Answer: { } answer })
                        types.Import(answer);

                if (key is not null && types.Types.ContainsKey(key))
                    pointers.Add(key);
                else
                    complete = false;
            }
        }

        entry["complete"] = complete;

        // A union an owner answered names its targets; several answers are one union of all of them.
        var flat = pointers
            .SelectMany(pointer => pointer.StartsWith("u:", StringComparison.Ordinal) && pointers.Count > 1 && types.Types[pointer]?["of"] is JsonArray of
                ? of.Select(each => each!.GetValue<string>())
                : [pointer])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return flat.Count switch
        {
            0 => null,
            1 => flat[0],
            _ => types.Union(alias, flat),
        };
    }

    /// <summary>
    /// The targets of a remote alias of a request that does not bind, as the stage that created it
    /// wrote them: the cases of the reference a resolve follows (narrowed by its <c>target</c>; the
    /// owning entities for its <c>parentAs</c>), or the child of a lookup.
    /// </summary>
    private static List<(string Entity, string? Item)> UnboundTargets(QueryRequest query, BindTrace trace, int stage, string alias, ShapeNode.Remote node)
    {
        if (node.TargetOpen)
            return [];

        if (stage < 0 || stage >= query.Pipeline.Count)
            return [(node.TargetEntity, null)];

        var written = query.Pipeline[stage];
        var root = (written.Resolve?.Path ?? written.Lookup?.On)?.Split('.')[0];

        // A stage continued under another alias names its target itself; the reference it follows is its owner's.
        if (root is not null && trace.ShapeAt(stage).Roots.GetValueOrDefault(root) is ShapeNode.Remote or ShapeNode.Keyed)
            return [(node.TargetEntity, null)];

        if (written.Lookup is { From: { } from } lookup)
        {
            var child = from.Trim();
            var hash = child.IndexOf('#', StringComparison.Ordinal);

            return hash < 0 ? [(child.ToLowerInvariant(), null)]
                : lookup.As == alias ? [(child[..hash].ToLowerInvariant(), child[(hash + 1)..])]
                : [(child[..hash].ToLowerInvariant(), null)];
        }

        if (written.Resolve is not { } resolve || node.Reference.Path?.References is not { Count: > 0 } cases)
            return [(node.TargetEntity, null)];

        var targets = cases.SelectMany(declared => declared.Targets).Select(target => (target.Entity, target.Item));

        if (resolve.Target is { } wanted)
            targets = targets.Where(target => target.Entity == wanted);

        if (resolve.As != alias)
            targets = targets.Select(target => (target.Entity, (string?)null));

        return targets.Distinct().ToList();
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

        // A member that would be null is left out: no condition, no conversion, no item, no elements.
        var reference = new JsonObject
        {
            ["path"] = resolve.Reference.Wire,
            ["cases"] = new JsonArray(cases.Select(selected =>
            {
                var written = new JsonObject();

                switch (selected.When)
                {
                    case ReferenceCondition.PathEquals equals:
                        written["when"] = new JsonObject { ["path"] = equals.Path, ["equals"] = new JsonArray(equals.Values.Select(value => (JsonNode)value).ToArray()) };
                        break;

                    case ReferenceCondition.Variant variant:
                        written["when"] = new JsonObject { ["variant"] = new JsonArray(variant.Names.Select(name => (JsonNode)name).ToArray()) };
                        break;
                }

                if (KeyAsName(selected.KeyAs) is { } converted)
                    written["keyAs"] = converted;

                written["targets"] = new JsonArray(selected.Targets.Select(target =>
                {
                    var named = new JsonObject { ["entity"] = target.Entity };

                    if (target.Item is not null)
                        named["item"] = target.Item;

                    named["field"] = target.Field;
                    named["remote"] = target.IsRemote;

                    return (JsonNode)named;
                }).ToArray());

                return (JsonNode)written;
            }).ToArray()),
        };

        if (cases.Select(selected => selected.KeyAs).Distinct().Count() == 1 && KeyAsName(cases[0].KeyAs) is { } keyAs)
            reference["keyAs"] = keyAs;

        if (resolve.Elements is ResolveElements.First or ResolveElements.All)
            reference["elements"] = resolve.Elements == ResolveElements.First ? "first" : "all";

        return reference;
    }

    private static string? KeyAsName(KeyAs keyAs) => keyAs == KeyAs.Guid ? "guid" : null;

    // ---- the result columns ---------------------------------------------------------------------

    /// <summary>
    /// The final shape's visible members and roots (DESIGN §4.3 <c>result.columns</c>), in row order,
    /// grouped the way the row nests them: a member of the entity under root <c>""</c>, an object
    /// member one level down under its own name, and every alias under its name — a join's select, an
    /// element's members, a lookup's array — so the studio groups a join's columns under its hop.
    /// Scalars a stage adds (an unwind index, a group output) lie under <c>""</c>. Each column says
    /// when a row carries its key (<c>present</c>), as the row encoder writes it: a stored member the
    /// record does not hold has no key, a join that found nothing leaves its alias null.
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
                    Members(entity.Def.Root, "", Shape.ImplicitRoot, nullable: false, null, shape, columns, expand: true, joined: false);
                    break;

                // A join's alias shows its output set (the final shape carries it), an unwound copy of one alike.
                case ShapeNode.Entity entity:
                    if (entity.Select is { } shown)
                        Joined(name, shown.Select(path => (path, KindAt(shape, name, path))).ToList(), stage, shape, columns);
                    else
                        Members(entity.Def.Root, name + ".", name, nullable: true, stage, shape, columns, expand: false, joined: true);
                    break;

                case ShapeNode.Element element:
                    var elementShape = element.Source.Shape.Of ?? element.Source.Shape;

                    if (elementShape is { Kind: Kind.Object, Type: { } elementType })
                        Members(elementType, name + ".", name, nullable: false, stage, shape, columns, expand: false, joined: false);
                    else
                        columns.Add(Column(name, elementShape.Kind, nullable: true, stage, Shape.ImplicitRoot, ExplainColumn.IfStored));
                    break;

                case ShapeNode.Array:
                    columns.Add(Column(name, Kind.Array, nullable: false, stage, name, ExplainColumn.Always));
                    break;

                case ShapeNode.Keyed { Many: true }:
                    columns.Add(Column(name, Kind.Array, nullable: true, stage, name, ExplainColumn.Always));
                    break;

                // The children of a remote lookup, or their owning rows, as an array per row (DESIGN §3.4.4).
                case ShapeNode.Remote when bound.Stages.OfType<BoundStage.Resolve>().Any(resolve => resolve.RemoteLookup is { First: false } && (resolve.As == name || resolve.ParentAs == name)):
                    columns.Add(Column(name, Kind.Array, nullable: true, stage, name, ExplainColumn.Always));
                    break;

                // The owner's rows: the paths the projection names under the alias and nothing else (an
                // owning row always with the entity it names); kept whole, what its owner is asked for.
                case ShapeNode.Remote or ShapeNode.Keyed:
                    var kept = RootOutput.Of(shape, name);
                    var owning = bound.Stages.OfType<BoundStage.Resolve>().Any(resolve => resolve.ParentAs == name);
                    var select = kept.Whole
                        ? SelectOf(bound, name) ?? []
                        : [.. owning && !kept.Projected.Contains("entity", StringComparer.Ordinal) ? [("entity", Kind.String)] : Array.Empty<(string, Kind)>(),
                            .. kept.Projected.Select(path => (path, owning && path == "entity" ? Kind.String : KindAt(shape, name, path)))];

                    if (select.Count > 0)
                        Joined(name, select, stage, shape, columns);
                    else
                        columns.Add(Column(name, Kind.Object, nullable: true, stage, name, ExplainColumn.Always));
                    break;

                case ShapeNode.Scalar scalar:
                    columns.Add(Column(name, scalar.Kind, nullable: false, stage, Shape.ImplicitRoot, ExplainColumn.Always));
                    break;

                case ShapeNode.GroupOutput output:
                    columns.Add(Column(name, output.Kind, nullable: true, stage, Shape.ImplicitRoot, ExplainColumn.Always));
                    break;
            }
        }

        return columns;
    }

    /// <summary>The kind of a path under an alias at the final shape; unknown under an owner's rows, whose kinds are in the owner's type.</summary>
    private static Kind KindAt(Shape shape, string alias, string path) =>
        shape.Resolve(alias + "." + path, PathUsage.Project) is { Succeeded: true, Path: { IsRemote: false } resolved } ? resolved.Kind : Kind.Unknown;

    private static ExplainColumn Column(string path, Kind kind, bool nullable, int? stage, string root, string present) =>
        new() { Path = path, Kind = Kinds.NameOf(kind), Nullable = nullable, Stage = stage, Root = root, Present = present };

    /// <summary>The columns of a join alias: the paths it shows, each nullable and absent where the join found nothing, less what an exclusion removed.</summary>
    private static void Joined(string alias, IReadOnlyList<(string Path, Kind Kind)> select, int? stage, Shape shape, List<ExplainColumn> columns)
    {
        foreach (var (path, kind) in select)
            if (!shape.IsRemoved(alias + "." + path) && !columns.Any(column => column.Path == alias + "." + path))
                columns.Add(Column(alias + "." + path, kind, nullable: true, stage, alias, ExplainColumn.IfJoined));
    }

    /// <summary>
    /// The stored members of a type as columns, the type's own first, then those only a variant has
    /// (nullable: other variants lack them). On the entity row (<paramref name="expand"/>) an object
    /// member, or an unwound collection of objects, is a root of its own with its members one level down.
    /// Under a join's alias (<paramref name="joined"/>) every member is absent where the join found nothing.
    /// </summary>
    private static void Members(TypeDef type, string prefix, string root, bool nullable, int? stage, Shape shape, List<ExplainColumn> columns, bool expand, bool joined, bool variantOnly = false)
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
            var memberVariant = variantOnly || variant || member.OnlyFor is not null;
            var memberNullable = nullable || memberVariant || member.Nullable;
            var present = joined ? ExplainColumn.IfJoined
                : memberVariant ? ExplainColumn.IfVariant
                : memberNullable ? ExplainColumn.IfStored
                : ExplainColumn.Always;

            if (expand && memberShape is { Kind: Kind.Object, Type: { } inner })
                Members(inner, wire + ".", wire, memberNullable, stage, shape, columns, expand: false, joined, memberVariant);
            else
                columns.Add(Column(wire, memberShape.Kind, memberNullable, stage, root, present));
        }
    }

    /// <summary>
    /// What a join alias the row keeps whole carries, relative to the alias: a keyed resolve's paths (the
    /// union of its targets': the select hint with the key, else key and display), the owning row's of a
    /// <c>parentAs</c> (with the <c>entity</c> it names), a remote resolve's hint, or a continued
    /// stage's hint; null when the alias has none this host knows (the owner's own key and display).
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

    // ---- the index advisory and the plan --------------------------------------------------------

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
}
