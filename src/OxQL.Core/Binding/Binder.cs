using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MongoDB.Bson;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Model.Build;

namespace OxQL.Core.Binding;

/// <summary>
/// Binds a request against the model: the entity, the engine-owned scope, then every stage in
/// order with the shape folded through them, every path checked at its stage, every operand
/// encoded for storage. Collects every error; never throws for a caller error.
/// </summary>
public sealed partial class Binder
{
    /// <summary>
    /// The deepest nesting of <c>and</c>, <c>or</c>, <c>not</c> and <c>any</c> a condition may have
    /// (<c>MAX_CONDITIONS_EXCEEDED</c> beyond it). Every level is one or two more nested documents in
    /// the compiled pipeline, whose serializer stops at 100; the bound keeps a margin for the stages
    /// a filter is nested in (a lookup or resolve sub-pipeline).
    /// </summary>
    public const int MaxConditionDepth = 32;

    /// <summary>
    /// The deepest nesting of arithmetic an aggregate's argument may have
    /// (<c>INVALID_AGGREGATE_ARGUMENT</c> beyond it). Every level is two more nested values in the
    /// compiled pipeline, as a level of a condition is.
    /// </summary>
    public const int MaxExpressionDepth = 32;

    private static readonly IReadOnlySet<string> Units = new HashSet<string>(StringComparer.Ordinal) { "year", "quarter", "month", "week", "day", "hour", "minute", "second" };
    private static readonly IReadOnlySet<string> WeekDays = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
    private static readonly IReadOnlySet<string> Aggregates = new HashSet<string>(StringComparer.Ordinal) { "sum", "avg", "min", "max", "first", "last", "push", "count", "countDistinct" };
    private static readonly IReadOnlySet<string> NumericAggregates = new HashSet<string>(StringComparer.Ordinal) { "sum", "avg" };

    /// <summary>
    /// The sentence that ends every refusal a contract 1 request receives for a contract 2
    /// construct: a request without the contract header is read as contract 1
    /// while compatibility is on, which is the likeliest mistake when a query is pasted into a
    /// report data source. It begins with a space, so it is appended to a finished sentence.
    /// </summary>
    public const string Contract1Hint = " This request was read as contract 1 because it carries no 'X-OxQL-Contract: 2' header.";

    /// <summary>
    /// The top-level members a request carries. An internal call may also carry <c>keyedBy</c>
    /// (<see cref="InternalRequestMembers"/>); the public route never names it.
    /// </summary>
    public const string RequestMembers = "entityType, variables, filter, pipeline, strict, onMissing, timing, maxTimeMs";

    /// <summary>What an owner says of a request on the internal route that carries a member it does not know: another build of the engine wrote it.</summary>
    public const string OtherBuildHint = "The request came over the internal route, where the calling service's engine writes it: that engine and this one are different builds. The engine and the base package are deployed together on every service that calls or is called.";

    /// <summary>The top-level members a request carries on an internal call (<see cref="RequestContext.Internal"/>).</summary>
    public const string InternalRequestMembers = RequestMembers + ", keyedBy";

    private readonly EntityModel model;
    private readonly CursorCodec cursors;

    public Binder(EntityModel model, CursorCodec cursors)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
    }

    /// <summary>Binds one request.</summary>
    public ValueTask<BindOutcome> BindAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken) =>
        BindAsync(request, context, explain: false, cancellationToken);

    /// <summary>
    /// Binds one request. With <paramref name="explain"/> the page's cursor is not decoded (explain ignores it), so a request binds the same with or without one.
    /// </summary>
    public async ValueTask<BindOutcome> BindAsync(QueryRequest request, RequestContext context, bool explain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await BindRequestAsync(request, context, explain, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (RequestText.HoldsBroken(request))
        {
            // A name or a text of the request is no Unicode text, and a read of it as one failed: the
            // caller's mistake, whether or not a host answered it from the body first.
            return new BindOutcome.Failed(Refusal.Validation([RequestText.Refused()]));
        }
    }

    private async ValueTask<BindOutcome> BindRequestAsync(QueryRequest request, RequestContext context, bool explain, CancellationToken cancellationToken)
    {

        if (context.Organisation is null || context.Organisation == Guid.Empty)
            return new BindOutcome.Failed(Refusal.AccessDenied("The request carries no organisation."));

        var errors = new List<QueryValidationError>();
        var diagnostics = new List<Diagnostic>();

        if (string.IsNullOrWhiteSpace(request.EntityType) || !model.TryResolve(request.EntityType, out var entity, out var retired))
            return new BindOutcome.Failed(Refusal.Validation([Error(Codes.UnknownEntity, $"'{request.EntityType}' is not an entity of this host.", null, null)]));

        if (retired)
            diagnostics.Add(new Diagnostic
            {
                Code = Codes.EntityIdRetired,
                Message = $"'{request.EntityType}' was retired; the entity is '{entity.Id}'.",
                Params = new Dictionary<string, object?> { ["currentId"] = entity.Id },
            });

        var scope = ScopeOf(entity, context.Organisation.Value);

        if (scope is null)
            return new BindOutcome.Failed(Refusal.AccessDenied($"'{entity.Id}' has no organisation member; it cannot be scoped."));

        var session = new Session(this, request, context, entity, errors, diagnostics, cancellationToken);

        await session.RunAsync();

        var trace = session.Trace();

        if (errors.Count > 0)
            return new BindOutcome.Failed(Refusal.Validation(errors)) { Trace = trace, Diagnostics = diagnostics.ToList() };

        try
        {
            return new BindOutcome.Bound(session.Result(scope, decodeCursor: !explain)) { Trace = trace, Diagnostics = diagnostics.ToList() };
        }
        catch (CursorException invalid)
        {
            // Under contract 2 the refusal says which of the three it is; a contract 1 caller reads what it always read.
            var error = Error(Codes.CursorInvalid, "The cursor is not valid for this query: it was issued for another query, was altered, or is malformed.", null, null);

            if (context.Contract != 1 && invalid.Reason is { } reason)
                error = error with
                {
                    Message = reason switch
                    {
                        CursorCodec.OtherQuery => "The cursor was issued for another query: a cursor continues the query it came from, with the same entity, conditions and sort.",
                        CursorCodec.Signature => "The cursor is not one this host issued: it was altered, or it comes from another host.",
                        _ => "The cursor is malformed: it is the 'nextCursor' of the page before, as it was answered.",
                    },
                    Params = new Dictionary<string, object?> { ["reason"] = reason },
                };

            return new BindOutcome.Failed(Refusal.Validation([error])) { Trace = trace, Diagnostics = diagnostics.ToList() };
        }
    }

    /// <summary>The scope stage for an entity, or null when it has no stored root guid <c>organizationId</c>.</summary>
    internal static BoundStage.Scope? ScopeOf(EntityDef entity, Guid organisation)
    {
        var path = entity.Path("organizationId");

        if (path is null || !path.Stored || path.Depth != 0 || path.Kind != Kind.Guid)
            return null;

        var representation = path.Shape.Representation.GuidRepresentation is { } declared && declared != GuidRepresentation.Unspecified
            ? declared
            : GuidRepresentation.Standard;

        return new BoundStage.Scope(entity, path.Storage!, organisation, representation);
    }

    private static QueryValidationError Error(string code, string message, int? stage, string? path) =>
        new() { Code = code, Message = message, Stage = stage, Path = path };

    /// <summary>The most members a refusal lists as joinable; the rest are counted.</summary>
    public const int MaxJoinableListed = 12;

    /// <summary>
    /// A refusal of a join over a member that declares no reference, with what the caller can do
    /// about it: the members of <paramref name="entity"/> that do declare one (to
    /// <paramref name="toward"/>, when the join names its target), and that a reference which is
    /// missing is declared in the service's model. The members travel in <c>params.joinable</c>
    /// (path and targets) for a client that offers them.
    /// </summary>
    internal static QueryValidationError Joinable(QueryValidationError error, EntityDef? entity, string? toward)
    {
        const string Model = " A join follows a reference the service's model declares on the member; one that is missing is declared there.";

        if (entity is null)
            return error with { Message = error.Message + Model };

        var joinable = entity.Paths
            .Where(path => path.References.Count > 0)
            .Select(path => (path.Wire, Targets: path.References.SelectMany(reference => reference.Targets).Select(target => target.ToString()).Distinct(StringComparer.Ordinal).ToList()))
            .Where(member => toward is null || member.Targets.Any(target => target == toward || target.StartsWith(toward + "#", StringComparison.Ordinal)))
            .ToList();
        var listed = joinable.Take(MaxJoinableListed).Select(member => toward is null ? $"{member.Wire} ({string.Join(", ", member.Targets)})" : member.Wire).ToList();
        var reference = toward is null ? "a reference" : $"a reference to '{toward}'";
        var sentence = joinable.Count == 0
            ? $" No member of '{entity.Id}' declares {reference}."
            : $" The members of '{entity.Id}' that declare {reference} are: {string.Join(", ", listed)}{(joinable.Count > listed.Count ? $" and {joinable.Count - listed.Count} more" : "")}.";

        return error with
        {
            Message = error.Message + sentence + Model,
            Params = new Dictionary<string, object?>(error.Params ?? new Dictionary<string, object?>())
            {
                ["entity"] = entity.Id,
                ["joinable"] = joinable.Take(MaxJoinableListed).Select(member => new Dictionary<string, object?> { ["path"] = member.Wire, ["targets"] = member.Targets }).ToList(),
                ["joinableCount"] = joinable.Count,
            },
        };
    }

    /// <summary>One binding run: the stage loop and everything it accumulates.</summary>
    private sealed partial class Session(Binder binder, QueryRequest request, RequestContext context, EntityDef entity, List<QueryValidationError> errors, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        private readonly OxQLOptions options = context.Options;
        private const string DefaultSelectKey = "$default";

        private readonly OperandCoercer coercer = new(context.Options, request.Variables, diagnostics, context.Contract != 1);
        private readonly List<BoundStage> stages = [];

        /// <summary>The caller's stage index of each bound stage, by position; null for a stage the binder supplied.</summary>
        private readonly List<int?> callerIndexes = [];
        private readonly Dictionary<string, IReadOnlyList<AddonDefinition>> addons = new(StringComparer.Ordinal);
        private Shape shape = null!;
        private BoundStage.Sort? sort;
        private BoundStage.Page? page;
        private int pageIndex = -1;

        /// <summary>The values stage the pipeline ends in instead of a page (contract 2), and the caller's index of it; nothing follows it.</summary>
        private BoundStage.Values? values;
        private int valuesIndex = -1;
        private int lookups, unwinds, resolves, conditions, conditionDepth;
        private bool conditionDepthReported;
        private bool hasSemiJoin;

        /// <summary>The shape the pipeline entered with, and each caller stage's kind and shapes around it.</summary>
        private Shape entry = null!;
        private readonly List<(int Index, string? Kind, Shape Before, Shape After)> traced = [];

        /// <summary>The stages that failed only under a poisoned alias: their errors were dropped, their status is <c>skipped</c>.</summary>
        private readonly HashSet<int> poisonedStages = [];

        /// <summary>
        /// The aliases a stage continues under: every keyed or remote resolve's alias and
        /// owning row, and every alias a continued stage added, each with the keyed stage whose owner
        /// query carries the continuation.
        /// </summary>
        private readonly Dictionary<string, ContinuationAnchor> anchors = new(StringComparer.Ordinal);

        /// <summary>The stages whose resolve took the request's <c>onMissing</c> because it named none of its own.</summary>
        private readonly HashSet<int> inheritedOnMissing = [];

        /// <summary>How many stages continue under each keyed stage, by its alias.</summary>
        private readonly Dictionary<string, int> continuedPerAnchor = new(StringComparer.Ordinal);

        /// <summary>
        /// An alias a stage may continue under: the keyed <paramref name="Stage"/> whose owner runs the
        /// continuation, the targets of that stage the alias exists on (<c>forTarget</c> of the stage
        /// that added it, or the targets a union join has a branch for; null for every target), whether
        /// it holds every resolved target of a row (<c>elements: "all"</c>), and whether a
        /// continued stage added it (<paramref name="Nested"/>): the owner then binds that stage's own
        /// join, so its targets are the owner's to know and a <c>forTarget</c> under it is the owner's to check.
        /// </summary>
        private sealed record ContinuationAnchor(BoundStage.Resolve Stage, IReadOnlyList<string>? Targets, bool Many, bool Nested = false)
        {
            /// <summary>The one target the alias exists on, when it is one.</summary>
            public string? ForTarget => Targets is [var only] ? only : null;
        }

        /// <summary>Contract 2, where a string comparison, sort or group key folds case unless it opts out.</summary>
        private readonly bool contract2 = context.Contract != 1;

        /// <summary>Whether something folds under the collation, which the whole aggregate then carries.</summary>
        private bool collated;

        /// <summary>The sort entries that opted out of the fold, by stage: they cannot run inside a collated aggregate.</summary>
        private readonly List<(int Stage, string Path)> exactSorts = [];

        /// <summary>The comparisons that opted out of the fold with a text too long for a pattern: refused once the aggregate turns out collated.</summary>
        private readonly List<(int Stage, string Path, string Op)> exactTexts = [];

        /// <summary>
        /// The read ledger: every path a stage reads off the row, with its use and
        /// the join it loads from. It is written at the binder's read sites only (<see cref="Read"/>,
        /// <see cref="Record"/>): a path bound against a join's own target (its hint, its filter, its sort),
        /// a probe and what explain asks of a shape never reach it.
        /// </summary>
        private readonly List<PathRead> reads = [];

        /// <summary>What each join loads and shows, by alias; filled once every stage is bound (<see cref="InferLoads"/>).</summary>
        private readonly Dictionary<string, JoinLoad> loads = new(StringComparer.Ordinal);

        /// <summary>The final shape with each join alias's output set, once the loads are inferred.</summary>
        private Shape? output;

        /// <summary>
        /// Where a condition or sort entry reads: the row (recorded, an <c>any</c>'s inner paths under
        /// <paramref name="Prefix"/>), or null at its call sites for a join's own target, whose filter and
        /// sort run inside the join before anything is loaded.
        /// </summary>
        private readonly record struct Reading(string Prefix)
        {
            /// <summary>A read of the row itself.</summary>
            public static readonly Reading Row = new("");
        }

        /// <summary>
        /// Resolves a path a stage reads, and records the read when it reads the row
        /// (<paramref name="reading"/> not null) and resolves.
        /// </summary>
        private PathResolution Read(Shape at, string wire, PathUsage usage, ReadUse use, int index, Reading? reading)
        {
            var resolution = at.Resolve(wire, usage);

            if (reading is { } row && resolution.Succeeded)
                Record(index, row.Prefix + wire, use, wire, resolution.Path!);

            return resolution;
        }

        /// <summary>Records one read of the row: the path as the row has it, and the join the resolved path loads from.</summary>
        private void Record(int index, string path, ReadUse use, string wire, ResolvedPath resolved, bool typeOnly = false)
        {
            var from = PathRead.Attribute(wire, resolved);

            reads.Add(new PathRead(index, path, use, from?.Alias, from?.Relative) { TypeOnly = typeOnly });
        }

        public async Task RunAsync()
        {
            await LoadAddonsAsync(entity);
            shape = Shape.ForEntity(entity, addons).Deriving(contract2);
            entry = shape;

            BindRequestMembers();
            BindKeyedBy();
            ReadFilter();

            if (request.Variables is not null && request.Variables.Values.Count > options.Limits.MaxVariables)
                errors.Add(Limit(Codes.MaxVariablesExceeded, $"The request binds {request.Variables.Values.Count} variables; the limit is {options.Limits.MaxVariables}.", null, null, "maxVariables", options.Limits.MaxVariables, request.Variables.Values.Count));

            // A name that starts with '$' is the engine's to bind: a request that supplies
            // one is refused, so a variable the engine binds later never changes what a request means.
            if (contract2)
                foreach (var name in (request.Variables?.Values.Keys ?? Enumerable.Empty<string>()).Where(name => name.StartsWith('$')).Order(StringComparer.Ordinal))
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.InvalidVariable,
                        Message = $"The variable name '{name}' is reserved: a name that starts with '$' is bound by the engine, never by a request.",
                        Params = new Dictionary<string, object?> { ["name"] = name, ["reason"] = "reserved" },
                    });

            var pipeline = request.Pipeline ?? [];

            if (pipeline.Count > options.Limits.MaxPipelineStages)
                errors.Add(Limit(Codes.MaxPipelineStagesExceeded, $"The pipeline has {pipeline.Count} stages; the limit is {options.Limits.MaxPipelineStages}.", null, null, "maxPipelineStages", options.Limits.MaxPipelineStages, pipeline.Count));

            for (var index = 0; index < pipeline.Count; index++)
            {
                var stage = pipeline[index];

                // The conditions of the request's filter run at the earliest point their paths are in
                // the row: each boundary between two stages is tried in order, the page's last.
                if (page is null)
                    PlaceFilter(index, last: stage?.Kind == "page" || (contract2 && stage?.Kind == "values"));

                // A null element is a caller error, not a fault, and is refused with a code.
                var before = shape;

                if (stage is null)
                {
                    errors.Add(NoStage(Error(Codes.UnknownStage, "A stage is an object carrying exactly one stage member; this one is null.", index, null), "null", []));
                    traced.Add((index, null, before, shape));
                    continue;
                }

                // So is a known stage key whose value is null: the key is recorded, the member
                // is not, and no stage binds from nothing.
                if (stage.Kind is { } kind && !HasMember(stage, kind) && (contract2 || kind != "values"))
                {
                    errors.Add(NoMember(Error(Codes.UnknownStageMember, $"'{kind}' is null; a {kind} stage carries {(kind == "sort" ? "an array of sort entries" : "an object")}.", index, null), kind, [kind], "null"));
                    traced.Add((index, kind, before, shape));
                    continue;
                }

                // A values stage stands in place of the page: nothing follows it, a page included.
                if (valuesIndex >= 0)
                {
                    errors.Add(Error(Codes.StageAfterPage, "No stage may follow the values stage: it ends the pipeline in place of a page.", index, null));
                    traced.Add((index, stage.Kind, before, shape));
                    continue;
                }

                if (page is not null && stage.Kind != "page")
                {
                    errors.Add(Error(Codes.StageAfterPage, "No stage may follow the page stage.", index, null));
                    traced.Add((index, stage.Kind, before, shape));
                    continue;
                }

                var errorsBefore = errors.Count;
                var boundBefore = stages.Count;

                switch (stage.Kind)
                {
                    case "match": BindMatch(stage.Match!, index); break;
                    case "lookup": await BindLookupAsync(stage.Lookup!, index); break;
                    case "resolve": await BindResolveAsync(stage.Resolve!, index); break;
                    case "unwind": BindUnwind(stage.Unwind!, index); break;
                    case "group": BindGroup(stage.Group!, index); break;
                    case "project": BindProject(stage.Project!, index); break;
                    case "sort": BindSort(stage.Sort!, index); break;
                    case "page": BindPage(stage.Page!, index); break;
                    // Contract 1 knows no such stage: it is the unknown stage it always was there.
                    case "values" when contract2: BindValues(stage.Values!, index); break;
                    default:
                        errors.Add(NoStage(Error(Codes.UnknownStage, stage.Keys.Count == 0
                            ? "A stage object must carry exactly one stage member."
                            : $"'{string.Join(", ", stage.Keys)}' is not a stage; a stage object carries exactly one of match, lookup, resolve, unwind, group, project, sort, page{(contract2 ? ", values" : "")}.", index, null),
                            stage.Keys.Count switch { 0 => "empty", 1 => "unknown", _ => "severalKeys" }, stage.Keys));
                        break;
                }

                // A stage that failed leaves its aliases poisoned: a later path under one fails
                // without an error of its own, so one mistake is reported once.
                if (errors.Count > errorsBefore)
                    PoisonAliases(stage);

                // A caller stage may bind to no stage (an empty match) or to one; either way every
                // bound stage keeps the caller's index, which diagnostics and explain name.
                while (callerIndexes.Count < stages.Count)
                    callerIndexes.Add(index);

                if (stage.Kind == "group" && errors.Count == errorsBefore && stages.Count > boundBefore && stages[^1] is BoundStage.Group grouped)
                    NoteFilterBindsEarlier(grouped, index);

                traced.Add((index, stage.Kind, before, shape));
            }

            // Without a page stage the end of the pipeline is the last point a condition can run at.
            if (page is null)
                PlaceFilter(pipeline.Count, last: true);

            // What failed only because it lay under a poisoned alias was reported where the alias
            // failed; explain shows such a stage as skipped.
            foreach (var poisoned in errors.Where(error => error.Code == Shape.PoisonedCode && error.Stage is not null))
                poisonedStages.Add(poisoned.Stage!.Value);

            errors.RemoveAll(error => error.Code == Shape.PoisonedCode);

            if (conditions > options.Limits.MaxConditions)
                errors.Add(Limit(Codes.MaxConditionsExceeded, $"The request has {conditions} conditions; the limit is {options.Limits.MaxConditions}.", null, null, "maxConditions", options.Limits.MaxConditions, conditions));

            // One aggregate runs under one collation, and a sort has no form the collation
            // does not reach: an exact sort needs every string comparison and key in the
            // request to be exact as well.
            if (collated)
            {
                foreach (var (stage, path) in exactSorts)
                    errors.Add(Option(Error(Codes.OptionNotApplicable, $"The sort on '{path}' asks for the exact order, but another comparison, sort or group key of the request folds case; a request orders exactly only when every string comparison in it is caseSensitive.", stage, path), "caseSensitive", "folds"));

                // Inside a collated aggregate an exact comparison is a pattern, with the pattern's limit.
                foreach (var (stage, path, op) in exactTexts)
                    errors.Add(Operand(TooLongToMatch(op, path), stage, path, op, "length"));
            }

            // Every member a stage does not have says its stage and why, whichever place of the binder
            // refused it: one that says no more is a member the stage does not know.
            if (contract2)
                for (var position = 0; position < errors.Count; position++)
                    if (errors[position] is { Code: Codes.UnknownStageMember, Stage: { } at } incomplete && at < pipeline.Count
                        && (incomplete.Params is null || !incomplete.Params.ContainsKey("stage") || !incomplete.Params.ContainsKey("reason")))
                        errors[position] = incomplete with
                        {
                            Params = new Dictionary<string, object?>(incomplete.Params ?? new Dictionary<string, object?>())
                            {
                                ["stage"] = incomplete.Params?.GetValueOrDefault("stage") ?? pipeline[at]?.Kind,
                                ["reason"] = incomplete.Params?.GetValueOrDefault("reason") ?? "unknown",
                            },
                        };

            LabelFilterFindings();

            if (errors.Count == 0)
                DefaultSort();

            if (page is null)
            {
                page = new BoundStage.Page(options.Limits.DefaultPageSize, 0, null, false);
                stages.Add(page);
                callerIndexes.Add(null);
            }

            // Contract 1 is frozen: a join fetches its select, as written or defaulted, and nothing else.
            if (contract2 && errors.Count == 0)
                InferLoads();
        }

        // ---- join loads -------------------------------------------------

        /// <summary>
        /// What each join loads, once every stage is bound: its key, what later stages read under its
        /// alias, and its output set. The output set is what the final row shows under the alias: the
        /// paths the projection names under it, or, kept whole, its key and display member (a contract 2
        /// join has no <c>select</c>). A join in the aggregate loads all three and the row is cut to the output set
        /// (the final shape carries it), so what is only read never shows. A join after the page (keyed
        /// or remote) is read by nothing but the projection, so it asks its owner for the output set
        /// alone: the paths, or nothing for the owner's own key and display.
        /// </summary>
        private void InferLoads()
        {
            var final = shape;
            var whole = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            for (var position = 0; position < stages.Count; position++)
            {
                switch (stages[position])
                {
                    case BoundStage.Lookup lookup:
                    {
                        whole[lookup.As] = lookup.Select.Select(path => path.Wire).ToList();

                        var loaded = LoadedBy(final, lookup.As, lookup.From.Key?.Wire, whole[lookup.As]);

                        stages[position] = lookup with { Select = BindLoads(loaded, Shape.ForEntity(lookup.From, addons).Deriving(contract2)) };
                        loads[lookup.As] = new JoinLoad(loaded, ShownUnder(final, lookup.As, whole[lookup.As]));
                        break;
                    }

                    case BoundStage.Resolve { IsRemote: false, Executor: ResolveExecutor.Inline, Cases: [{ Targets: [var target] } only] } resolve:
                    {
                        whole[resolve.As] = (target.Select ?? []).Select(path => path.Wire).ToList();

                        var loaded = LoadedBy(final, resolve.As, target.Entity!.Key?.Wire, whole[resolve.As]);
                        var select = BindLoads(loaded, Shape.ForEntity(target.Entity, addons).Deriving(contract2));

                        stages[position] = resolve with { Select = select, Cases = [only with { Targets = [target with { Select = select }] }] };
                        loads[resolve.As] = new JoinLoad(loaded, ShownUnder(final, resolve.As, whole[resolve.As]));
                        break;
                    }

                    case BoundStage.Resolve resolve:
                        stages[position] = InferKeyed(final, resolve);
                        break;
                }
            }

            // The row shows the output set under each alias a join in the aggregate fills, and under its
            // unwound copies; what was loaded for a stage that only reads it is cut on the way out.
            output = final;

            foreach (var (name, node) in final.Roots)
            {
                if (Shape.JoinOf(node) is not { } join || !whole.TryGetValue(join, out var kept))
                    continue;

                output = node switch
                {
                    ShapeNode.Entity entity => output.WithRoot(name, entity with { Select = ShownUnder(final, name, kept) }),
                    ShapeNode.Array array => output.WithRoot(name, array with { Select = ShownUnder(final, name, kept) }),
                    _ => output,
                };
            }
        }

        /// <summary>
        /// What a join in the aggregate loads under <paramref name="join"/>: its key, every path a stage
        /// reads under it or under an unwound copy or element of it, and the <paramref name="whole"/> set
        /// where the final row keeps the alias or a copy of it whole, or a stage reads the alias itself
        /// (an existence test on it, the first of it in a group): a whole alias is its output set, never
        /// the target's full row. Paths another one covers are left out.
        /// </summary>
        private List<string> LoadedBy(Shape final, string join, string? key, IReadOnlyList<string> whole)
        {
            var paths = new List<string>();

            if (key is not null)
                paths.Add(key);

            paths.AddRange(reads.Where(read => read.Alias == join && !read.TypeOnly && read.Relative is { Length: > 0 }).Select(read => read.Relative!));

            if (final.Roots.Any(root => root.Value is ShapeNode.Entity or ShapeNode.Array && Shape.JoinOf(root.Value) == join && RootOutput.Of(final, root.Key) is { Carried: true, Whole: true })
                || reads.Any(read => read.Alias == join && !read.TypeOnly && read.Relative is { Length: 0 }))
                paths.AddRange(whole);

            return RootOutput.Cover(paths);
        }

        /// <summary>What the final row shows under the root <paramref name="name"/>: nothing when it does not carry it, the <paramref name="whole"/> set when it keeps it whole, else the paths the projection names under it.</summary>
        private static List<string> ShownUnder(Shape final, string name, IReadOnlyList<string> whole) =>
            RootOutput.Of(final, name) switch
            {
                { Carried: false } => [],
                { Whole: true } => RootOutput.Written(whole),
                var projected => RootOutput.Cover(projected.Projected),
            };

        /// <summary>The loads as paths of the target: each read resolved there once before, under its alias.</summary>
        private static List<ResolvedPath> BindLoads(IReadOnlyList<string> loaded, Shape target)
        {
            var bound = new List<ResolvedPath>(loaded.Count);

            foreach (var wire in loaded)
                // not a read: the load set is bound against the join's own target
                if (target.Resolve(wire, PathUsage.Project) is { Succeeded: true } resolution)
                    bound.Add(resolution.Path!);

            return bound;
        }

        /// <summary>
        /// A keyed or remote resolve with what its owners are asked for (see <see cref="InferLoads"/>): per
        /// alias the paths the projection names under it; kept whole, nothing, which the
        /// owner query spells as the owner's own key and display. A local target binds the paths it has
        /// and drops the others for itself; a remote one is sent them, and its owner says what it lacks.
        /// </summary>
        private BoundStage.Resolve InferKeyed(Shape final, BoundStage.Resolve resolve)
        {
            var own = RootOutput.Of(final, resolve.As);
            var parent = resolve.ParentAs is { } parentAs ? RootOutput.Of(final, parentAs) : null;
            // An alias the row does not carry shows nothing: its owner is asked for the key alone, which is
            // all this host needs of it to lift what the stages continued under it add (an anchor).
            IReadOnlyList<string>? sent = !own.Carried ? []
                : own.Whole ? null
                : RootOutput.Cover(own.Projected);
            // The owning row's 'entity' is this host's to write, not a member of the owner's row.
            IReadOnlyList<string>? parentSent = parent is { Carried: true, Whole: false }
                ? RootOutput.Cover(parent.Projected.Where(path => path != ParentEntity && !path.StartsWith(ParentEntity + ".", StringComparison.Ordinal)))
                : parent is { Carried: false } ? [] : null;

            loads[resolve.As] = new JoinLoad(WithAnchorReads(sent, resolve.As), own.Carried ? sent : []);

            if (resolve.ParentAs is not null)
                loads[resolve.ParentAs] = new JoinLoad(WithAnchorReads(parentSent, resolve.ParentAs), parent!.Carried ? parent.Whole ? null : RootOutput.Cover(parent.Projected) : []);

            var cases = (resolve.Cases ?? []).Select(bound => bound with
            {
                Targets = bound.Targets.Select(target => InferTarget(target, sent, parentSent, resolve.ParentAs is not null)).ToList(),
            }).ToList();
            var first = cases.Count > 0 && cases[0].Targets.Count > 0 ? cases[0].Targets[0] : null;

            return resolve with
            {
                Cases = resolve.Cases is null ? null : cases,
                Select = first is null ? resolve.Select : first.Select,
                RemoteSelect = first is { IsRemote: true } ? sent : null,
                RemoteLookup = resolve.RemoteLookup is { } lookup ? lookup with { Select = sent, ParentSelect = parentSent } : null,
            };
        }

        /// <summary>
        /// What an owner loads under <paramref name="alias"/>: the paths it is asked for and those the
        /// stages continued under the alias root at. A continued stage reads the alias it continues under,
        /// whether or not the row shows it: the owner loads the path for the stage it runs, and the row is
        /// cut to what the projection names. The paths asked for keep their order, the reads follow. Null
        /// where only the owner knows what it loads.
        /// </summary>
        private IReadOnlyList<string>? WithAnchorReads(IReadOnlyList<string>? sent, string alias) => sent is null
            ? null
            : RootOutput.Written(sent.Concat(reads
                .Where(read => read.Alias == alias && read.Use is ReadUse.ResolveKey or ReadUse.LookupOn && read.Relative is { Length: > 0 })
                .Select(read => read.Relative!)
                .Order(StringComparer.Ordinal)));

        /// <summary>The member of an owning row (<c>parentAs</c>) that names its entity; the keyed fetch writes it.</summary>
        private const string ParentEntity = "entity";

        /// <summary>One target of a keyed stage with the paths it is asked for; a local target keeps its default where none are asked.</summary>
        private BoundResolveTarget InferTarget(BoundResolveTarget target, IReadOnlyList<string>? sent, IReadOnlyList<string>? parentSent, bool withParent)
        {
            if (target.IsRemote || target.Entity is not { } entity)
                return target with { RemoteSelect = sent, RemoteParentSelect = withParent ? parentSent : null };

            var entityShape = Shape.ForEntity(entity, addons).Deriving(contract2);
            var at = entityShape;

            // not a read: the item collection of the join's own target
            if (target.Declared.Item is { } item && entityShape.Resolve(item, PathUsage.Unwind) is { Succeeded: true } collection)
                at = entityShape.ForElement(collection.Path!);

            var bound = target;

            if (sent is not null)
            {
                var (select, dropped) = BindKeyedLoads(sent, at, target.Declared.Item is null ? entity.Key?.Wire : target.Declared.Field);

                bound = bound with { Select = select, DroppedSelect = dropped };
            }

            if (withParent && parentSent is not null && target.Declared.Item is not null)
            {
                var (select, dropped) = BindKeyedLoads(parentSent, entityShape, entity.Key?.Wire);

                bound = bound with
                {
                    ParentSelect = select,
                    RemoteParentSelect = parentSent.Where(path => !dropped.Contains(path, StringComparer.Ordinal)).ToList(),
                    DroppedParentSelect = dropped,
                };
            }

            return bound;
        }

        /// <summary>The paths a local target of a keyed stage has, bound, its key first, and those it lacks.</summary>
        private static (List<ResolvedPath> Select, List<string> Dropped) BindKeyedLoads(IReadOnlyList<string> sent, Shape at, string? keyWire)
        {
            var select = new List<ResolvedPath>();
            var dropped = new List<string>();

            foreach (var wire in sent)
            {
                // not a read: the paths asked of the join's own target
                if (at.Resolve(wire, PathUsage.Project) is { Succeeded: true } kept)
                    select.Add(kept.Path!);
                else
                    dropped.Add(wire);
            }

            // not a read: the key of the join's own target
            if (keyWire is not null && select.All(kept => kept.Wire != keyWire) && at.Resolve(keyWire, PathUsage.Select) is { Succeeded: true } key)
                select.Insert(0, key.Path!);

            return (select, dropped);
        }

        /// <summary>
        /// The top-level members. Under contract 2 a member a request does
        /// not have is refused, since an engine that dropped it would run the request without
        /// what it asked for. Contract 1 ignores unknown members as it always has, but refuses
        /// <c>strict</c>: dropping it would hand a report the rows strict exists to refuse.
        /// </summary>
        private void BindRequestMembers()
        {
            if (!contract2)
            {
                if (request.Strict is not null)
                    errors.Add(Error(Codes.LegacyStageUnsupported, "'strict' is a contract 2 member of a request; contract 1 has no strict mode." + Contract1Hint, null, null));

                return;
            }

            if (request.Unknown is { Count: > 0 } unknown)
                errors.Add(OtherBuild(NoRequestMember(
                    $"'{string.Join(", ", unknown.Keys)}' is not a member of a request; a request carries {(context.Internal ? InternalRequestMembers : RequestMembers)}.", unknown.Keys, "unknown"), unknown.Keys));

            // The two members a request is read with as written (contract 1 ignores them whatever they
            // hold): under contract 2 each has one kind, and another is refused with the member's name.
            if (request.Timing is { ValueKind: not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) })
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidOperand,
                    Message = "'timing' is true or false: whether the answer carries where its time went.",
                    Params = new Dictionary<string, object?> { ["member"] = "timing", ["reason"] = "notABoolean" },
                });

            // The request's default for a missing join target: one of three words.
            if (request.OnMissing is { ValueKind: not JsonValueKind.Null } && request.MissingDefault is null)
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidOperand,
                    Message = "'onMissing' is \"null\", \"report\" or \"refuse\": what every resolve of the request that names none of its own does with a reference that resolves to nothing.",
                    Params = new Dictionary<string, object?> { ["member"] = "onMissing", ["reason"] = "notAPolicy", ["allowed"] = QueryRequest.MissingPolicies },
                });

            if (request.MaxTimeMs is { ValueKind: not JsonValueKind.Null } ceiling && (ceiling.ValueKind != JsonValueKind.Number || !QueryRequest.IsWhole(ceiling, out _)))
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidOperand,
                    Message = "'maxTimeMs' is a whole number of milliseconds: the ceiling on this request's time.",
                    Params = new Dictionary<string, object?> { ["member"] = "maxTimeMs", ["reason"] = "notAnInteger" },
                });
        }

        /// <summary>
        /// A top-level member this engine does not know, on an internal call: there the request is
        /// written by the calling service's engine, never by a caller, so the two services run
        /// different builds of the engine. The refusal says so and carries
        /// <c>params { reason: "engineBuild", members }</c>, so that the origin's <c>RESOLVE_REFUSED</c>
        /// names the cause instead of a member no caller wrote. On the public route the error is as it was.
        /// </summary>
        private QueryValidationError OtherBuild(QueryValidationError error, IEnumerable<string> members) => !context.Internal ? error : error with
        {
            Message = error.Message + " " + OtherBuildHint,
            Params = new Dictionary<string, object?> { ["reason"] = "engineBuild", ["members"] = members.ToList() },
        };

        /// <summary>
        /// A member a request does not have. Under contract 2 it carries the members (<c>member</c> is
        /// the first of them) and why: <c>unknown</c> (no member of a request), or <c>internal</c> (a
        /// member only the internal route between services takes).
        /// </summary>
        private QueryValidationError NoRequestMember(string message, IEnumerable<string> members, string reason)
        {
            var error = Error(Codes.UnknownRequestMember, message, null, null);

            return contract2
                ? error with { Params = new Dictionary<string, object?> { ["reason"] = reason, ["members"] = members.ToList(), ["member"] = members.First() } }
                : error;
        }

        /// <summary>
        /// The internal <c>keyedBy</c>: the owner's rows grouped per key, at most
        /// <c>perKey</c> per key. Only an internal call carries it; anywhere else it is a member a
        /// request does not have. The path is a stored member of the entity, or of one item
        /// collection's element, which then travels under <see cref="BoundKeyedBy.Element"/> and is
        /// a root the rest of the pipeline reads (the filter, the projection).
        /// </summary>
        private void BindKeyedBy()
        {
            if (request.KeyedBy is not { } keyedBy)
                return;

            // Only an internal call of contract 2 carries it; the contract 1 hint would suggest the
            // header is what is missing, which it is not.
            if (!context.Internal || !contract2)
            {
                errors.Add(NoRequestMember($"'keyedBy' is not a member of a request; a request carries {RequestMembers}.", ["keyedBy"], "internal"));
                return;
            }

            // The roots the prologue adds, poisoned until it binds: a keyedBy that fails is one error,
            // not one more per path of the projection under them.
            foreach (var root in new[] { BoundKeyedBy.Element, BoundKeyedBy.Key })
                if (!shape.IsTaken(root))
                    shape = shape.WithRoot(root, new ShapeNode.Poisoned(root));

            if (keyedBy.Unknown is { Count: > 0 } unknownMembers)
            {
                errors.Add(OtherBuild(NoRequestMember(
                    $"'{string.Join(", ", unknownMembers.Keys.Select(name => "keyedBy." + name))}' is not a member of keyedBy; it carries path, keys, perKey, references and rows.", unknownMembers.Keys.Select(name => "keyedBy." + name), "unknown"), unknownMembers.Keys.Select(name => "keyedBy." + name)));
                return;
            }

            if (keyedBy.Rows is not (null or "entity"))
            {
                errors.Add(Error(Codes.InvalidOperand, "'keyedBy.rows' is \"entity\" when given.", null, keyedBy.Path));
                return;
            }

            var wire = keyedBy.Path ?? "";
            // not a read: the member an internal owner query is keyed by, on the entity row
            var resolution = shape.Resolve(wire, PathUsage.Match);

            if (!resolution.Succeeded)
            {
                errors.Add(Unresolved(resolution, null, keyedBy.Path));
                return;
            }

            var path = resolution.Path!;
            var crossed = CollectionsCrossed(wire);

            if (path.Storage is null || path.Kind == Kind.Array || crossed.Count > 1 || crossed.Any(collection => collection.Wire == wire))
            {
                errors.Add(Error(Codes.InvalidPath,
                    $"'keyedBy.path' is a stored member of the entity or of the element of one item collection; '{wire}' is not.", null, keyedBy.Path));
                return;
            }

            if (keyedBy.Keys is not { ValueKind: JsonValueKind.Array } keys || keys.GetArrayLength() == 0)
            {
                errors.Add(Error(Codes.InvalidOperand, "'keyedBy.keys' is a non-empty array of keys.", null, keyedBy.Path));
                return;
            }

            // Every key answers at least one row, so no more keys than the largest page holds.
            var maxKeys = Math.Max(options.Limits.MaxPageSize, options.Limits.MaxReportPageSize);

            if (keys.GetArrayLength() > maxKeys)
            {
                errors.Add(Error(Codes.InvalidOperand, $"'keyedBy.keys' holds {keys.GetArrayLength()} keys; one query takes at most {maxKeys}.", null, keyedBy.Path));
                return;
            }

            var perKey = keyedBy.PerKey ?? 2;

            // The owner's own cap on rows per key, whatever the caller's is: a remote lookup asks one
            // row more than its limit to tell a truncated parent, so the cap is this host's lookup
            // limit plus that one.
            if (perKey < 1 || perKey > options.Limits.MaxLookupLimit + 1)
            {
                errors.Add(Limit(Codes.LookupLimitExceeded,
                    $"'keyedBy.perKey' is {perKey}; this host answers between 1 and {options.Limits.MaxLookupLimit + 1} rows per key (a lookup's limit of at most {options.Limits.MaxLookupLimit}, plus one).", null, keyedBy.Path, "maxLookupLimit", options.Limits.MaxLookupLimit + 1, perKey));
                return;
            }

            // A remote lookup's path must declare a reference to the asking host's entity, as a local
            // lookup's must; the keys are that entity's keys.
            if (keyedBy.References is { } referenced
                && !(path.Path?.References ?? []).Any(declared => declared.Targets.Any(target => target.Entity == referenced && target.Item is null && target.Field == WireNames.IdWire)))
            {
                errors.Add(Error(Codes.LookupNotDeclared, $"'{entity.Id}#{wire}' does not declare a reference to '{referenced}'.", null, keyedBy.Path));
                return;
            }

            // The keys compare exactly, as the ids they are; the coercer's errors are the request's.
            var coerced = new List<QueryValidationError>();
            var operand = coercer.Coerce(keys, path, "in", 0, coerced);

            if (operand is null)
            {
                errors.AddRange(coerced.Select(error => error with { Stage = null }));
                return;
            }

            string? itemStorage = null, element = null, elementField = null, keyAlias = null;

            if (crossed is [var collection])
            {
                itemStorage = collection.Storage!;
                elementField = path.Storage[(itemStorage.Length + 1)..];

                if (keyedBy.Rows is null)
                {
                    element = BoundKeyedBy.Element;
                    shape = shape.WithRoot(element, new ShapeNode.Element(entity, collection.Path!, element));
                }
                else
                {
                    // Each whole row carries the key it answers for, which the caller keys it by.
                    keyAlias = BoundKeyedBy.Key;
                    shape = shape.WithRoot(keyAlias, new ShapeNode.Scalar(path.LeafKind, keyAlias));
                }
            }
            else if (keyedBy.Rows is not null)
            {
                // A path on the row itself holds one key per row: the row carries it all the same, so
                // the caller reads every whole-row answer the one way.
                keyAlias = BoundKeyedBy.Key;
                shape = shape.WithRoot(keyAlias, new ShapeNode.Scalar(path.LeafKind, keyAlias));
            }

            keyedByBound = new BoundKeyedBy(path, ValuesOf(operand), perKey, itemStorage, element, elementField, keyAlias, keyedBy.References);
        }

        private BoundKeyedBy? keyedByBound;

        /// <summary>
        /// The error of a path that did not resolve, with why in <c>params</c> where the shape says it
        /// (<see cref="PathReasons"/>). A contract 1 answer is frozen: it carries the code and the message as before.
        /// </summary>
        private QueryValidationError Unresolved(PathResolution resolution, int? stage, string? path) =>
            new() { Code = resolution.Code!, Message = NamedIn(resolution.Message!, path), Stage = stage, Path = path, Params = contract2 ? resolution.Params : null };

        /// <summary>
        /// What a shape says of a path that does not resolve, with the path named under its collection
        /// inside <c>any</c>: the shape of an element knows the member by its own name, and its words
        /// name nothing the caller wrote but paths.
        /// </summary>
        private string NamedIn(string message, string? path) =>
            under is null || path is null ? message : message.Replace($"'{path}'", $"'{Named(path)}'", StringComparison.Ordinal);

        /// <summary>The contract 1 hint when <paramref name="contract2Construct"/> is what a contract 1 request was refused for; empty otherwise.</summary>
        private string Hint(bool contract2Construct) => !contract2 && contract2Construct ? Contract1Hint : "";

        /// <summary>The aliases a failed stage would have created, each poisoned when its name is still free.</summary>
        private void PoisonAliases(PipelineStage stage)
        {
            IEnumerable<string?> aliases = stage.Kind switch
            {
                "lookup" => [stage.Lookup!.As, stage.Lookup.ParentAs],
                "resolve" => [stage.Resolve!.As, stage.Resolve.ParentAs, stage.Resolve.OutcomeAs],
                "unwind" => [stage.Unwind!.As, stage.Unwind.IncludeIndex],
                _ => [],
            };

            foreach (var alias in aliases)
                if (alias is not null && Aliases.Problem(alias) is null && !shape.IsTaken(alias))
                    shape = shape.WithRoot(alias, new ShapeNode.Poisoned(alias));
        }

        /// <summary>
        /// A grouped or unwound shape pages by offset, and <c>$skip</c> over output nothing
        /// ordered repeats and drops rows between pages. When the caller wrote no sort the
        /// binder supplies one that is total: the group keys, which are unique per group, or the
        /// entity key, which the compiler completes with the index of every unwind. It is bound
        /// like any other sort, so it travels into the canonical form, the fingerprint and
        /// explain, and a cursor issued before it existed is refused rather than silently
        /// paging a different order.
        /// </summary>
        private void DefaultSort()
        {
            // Values are ordered by the stage itself, and nothing pages them.
            if (values is not null || sort is not null || (shape.IsRootShape && !UnwoundRows))
                return;

            var fields = new List<BoundSortField>();

            if (shape.Grouped)
            {
                // The keys only: an aggregate is not unique per group and adds nothing to the order.
                fields.AddRange(GroupKeyOrder());
            }
            // not a read: the key completing the order of an unwound shape
            else if (shape.Resolve(WireNames.IdWire, PathUsage.Sort) is { Succeeded: true } key)
            {
                fields.Add(new BoundSortField(key.Path!, true));
            }

            // A group over no key yields exactly one row; there is nothing to order.
            if (fields.Count == 0)
                return;

            var stage = new BoundStage.Sort(fields);

            // The page stage is last when the caller wrote one, and stays last.
            if (page is not null)
            {
                stages.Insert(stages.Count - 1, stage);
                callerIndexes.Insert(callerIndexes.Count - 1, null);
            }
            else
            {
                stages.Add(stage);
                callerIndexes.Add(null);
            }

            sort = stage;
        }

        /// <summary>
        /// The keys of the last group, ascending, as far as the shape here still carries them.
        /// They are unique per group under the comparison the group ran with: a string key
        /// folded into its group under the collation, and its order folds the same way, so two
        /// groups never compare equal on all of them and an order ending in them is total.
        /// </summary>
        private List<BoundSortField> GroupKeyOrder()
        {
            var fields = new List<BoundSortField>();

            foreach (var key in stages.OfType<BoundStage.Group>().LastOrDefault()?.Keys ?? [])
                // not a read: a probe of which group keys the shape still carries
                if (shape.Resolve(key.As, PathUsage.Sort) is { Succeeded: true } resolved)
                    fields.Add(new BoundSortField(resolved.Path!, true, contract2 && key.OutputKind == Kind.String));

            return fields;
        }

        /// <summary>
        /// The stage loop as it ran. A stage carrying an error of its own is <c>error</c> (errors the
        /// binder adds after the loop, such as an exact sort in a collated request, count for their
        /// stage); one that failed only under a poisoned alias is <c>skipped</c>.
        /// </summary>
        public BindTrace Trace()
        {
            var failed = errors.Where(error => error.Stage is not null).Select(error => error.Stage!.Value).ToHashSet();
            var steps = traced
                .Select(step => new StageTrace(
                    step.Index,
                    step.Kind,
                    failed.Contains(step.Index) ? StageStatus.Error : poisonedStages.Contains(step.Index) ? StageStatus.Skipped : StageStatus.Ok,
                    step.Before,
                    step.After))
                .ToList();

            return new BindTrace(entry ?? shape, steps, shape) { Reads = reads.ToList(), Filters = BoundFilters() };
        }

        /// <summary>
        /// Whether the rows are one per element of an unwind (contract 2): of a member collection, or of
        /// a lookup's alias, whose rows the shape still counts as the entity's. They share their parent's
        /// key, so the position of each unwind completes their order, and the binder supplies the key as
        /// their sort where the request wrote none.
        /// </summary>
        private bool UnwoundRows => contract2 && !shape.Grouped && stages.Any(stage => stage is BoundStage.Unwind);

        /// <summary>
        /// Whether the rows of a shape an unwind or a group made are in a total order the engine can
        /// continue from by value (contract 2): then the cursor carries the last row's values, and the
        /// next page starts after them, whatever was written in between and however deep the page lies.
        /// Unwound rows are: the sort, the entity's key and the position of each unwind. Groups are
        /// when the sort that orders them stands after the group and ends in every one of its keys,
        /// which the binder completes it with as far as the shape still carries them. Where it is not
        /// (a key a projection dropped, a sort written before the group, an unwind of what a group
        /// collected, no sort at all), the page is still addressed by a cursor, which then counts rows.
        /// </summary>
        private bool OrderIsTotal()
        {
            if (sort is null)
                return false;

            if (!shape.Grouped)
                return true;

            var grouped = stages.FindLastIndex(stage => stage is BoundStage.Group);
            var sorted = stages.FindLastIndex(stage => ReferenceEquals(stage, sort));

            if (grouped < 0 || sorted < grouped || stages.Skip(grouped + 1).Any(stage => stage is BoundStage.Unwind))
                return false;

            return ((BoundStage.Group)stages[grouped]).Keys.All(key => sort.Fields.Any(field => field.Path.Storage == key.As));
        }

        public BoundPipeline Result(BoundStage.Scope scope, bool decodeCursor = true)
        {
            var mode = shape.IsRootShape || (contract2 && OrderIsTotal()) ? PagingMode.Keyset : PagingMode.Offset;
            // A contract 2 cursor is bound to what decides the rows and their order; contract 1
            // fingerprints the whole bound form, as it always has.
            var fingerprint = contract2
                ? BoundCanonical.RowsFingerprint(scope, stages, reads, mode, keyedByBound)
                : BoundCanonical.Fingerprint(scope, stages, mode, keyedByBound);

            // The cursor is verified against the finished fingerprint; explain ignores it.
            if (decodeCursor && pageIndex >= 0 && request.Pipeline![pageIndex].Page!.Cursor is { } cursor)
            {
                var payload = binder.cursors.Decode(cursor, fingerprint, out var reason);

                if (payload is null)
                    throw new CursorException { Reason = reason };

                page = page! with { Cursor = payload, Offset = payload.Mode == PagingMode.Offset ? payload.Offset : page.Offset };
                stages[stages.Count - 1] = page;
            }

            return new BoundPipeline
            {
                Entity = entity,
                Organisation = scope.Organisation,
                Scope = scope,
                Stages = stages,
                CallerIndexes = callerIndexes,
                FinalShape = output ?? shape,
                Reads = reads,
                Loads = loads,
                Sort = sort,
                Page = page!,
                Values = values,
                PagingMode = mode,
                Diagnostics = diagnostics,
                Fingerprint = fingerprint,
                Canonical = BoundCanonical.Render(scope, stages, page, mode, keyedByBound, contract2).ToJsonString(),
                HasSemiJoin = hasSemiJoin,
                UnwoundRows = UnwoundRows,
                Filters = BoundFilters(),
                FilterStages = filterStages.Count > 0 ? filterStages : null,
                KeyedBy = keyedByBound,
                Collated = collated,
                Strict = contract2 && request.IsStrict,
            };
        }

        // ---- filter -------------------------------------------------------------------

        /// <summary>One entry of the request's <c>filter</c>: its place in the list, its id, and what became of it.</summary>
        private sealed class FilterEntry(int index)
        {
            public int Index { get; } = index;

            public string? Id { get; set; }

            /// <summary>What errors, diagnostics and notes name the entry by: its id, or its index when it has none.</summary>
            public object Label => Id is null ? Index : Id;

            public bool Failed { get; set; }

            public List<FilterPlacement> Placed { get; } = [];

            public List<(string Path, string Alias, int Stage)> BindsEarlier { get; } = [];

            /// <summary>The entry's conditions no boundary has taken yet, in the order written: a boundary looks at these and at nothing placed before it.</summary>
            public List<FilterUnit> Unplaced { get; } = [];
        }

        /// <summary>
        /// One condition the engine places on its own: a top-level conjunct of an entry's <c>where</c>
        /// (a comparison, or an <c>or</c>, <c>not</c> or <c>any</c> group as a whole), with the paths
        /// it reads off the row and the boundary each was first in the row at.
        /// </summary>
        private sealed class FilterUnit(FilterEntry entry, FilterCondition condition, IReadOnlyList<string> paths)
        {
            public FilterEntry Entry { get; } = entry;

            public FilterCondition Condition { get; } = condition;

            public IReadOnlyList<string> Paths { get; } = paths;

            public Dictionary<string, int> Births { get; } = new(StringComparer.Ordinal);

            public bool Placed { get; set; }
        }

        private readonly List<FilterEntry> filterEntries = [];

        /// <summary>How many conditions of the filter no boundary has taken yet; at zero a boundary has nothing to do.</summary>
        private int filterUnplaced;
        private readonly Dictionary<BoundStage, object> filterStages = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The stage index the findings of a filter entry are recorded under while binding: below zero,
        /// so no caller stage has it. <see cref="LabelFilterFindings"/> turns it into the entry's label.
        /// </summary>
        private static int FilterMarker(FilterEntry entry) => -1 - entry.Index;

        private QueryValidationError FilterError(FilterEntry? entry, string reason, string message) => new()
        {
            Code = Codes.InvalidFilter,
            Message = message,
            Filter = entry?.Label,
            Params = new Dictionary<string, object?> { ["reason"] = reason },
        };

        /// <summary>
        /// Reads the request's <c>filter</c> (contract 2): a list of <c>{ id?, where }</c>. Each
        /// <c>where</c> is a condition object as a <c>match</c> stage takes it; its top-level conjuncts
        /// are placed one by one, an <c>or</c>, <c>not</c> or <c>any</c> group as a whole. Contract 1
        /// never had the member and ignores it.
        /// </summary>
        private void ReadFilter()
        {
            if (!contract2 || request.Filter is not { } raw || raw.ValueKind == JsonValueKind.Null)
                return;

            if (raw.ValueKind != JsonValueKind.Array)
            {
                errors.Add(FilterError(null, "notAnArray", "'filter' is an array of entries, each { id?, where }."));
                return;
            }

            // The bounds come first, before an entry is read and before anything is placed: a filter of
            // thousands of entries costs its parse and one error, never a pass over them per stage.
            var limits = options.Limits;

            if (raw.GetArrayLength() is var entries && entries > limits.MaxFilterEntries)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.MaxFilterEntriesExceeded,
                    Message = $"The request's filter has {entries} entries; the limit is {limits.MaxFilterEntries}.",
                    Params = new Dictionary<string, object?> { ["limit"] = "maxFilterEntries", ["max"] = limits.MaxFilterEntries, ["count"] = entries },
                });
                return;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            var leaves = 0;

            foreach (var element in raw.EnumerateArray())
            {
                var entry = new FilterEntry(index++);

                filterEntries.Add(entry);

                if (element.ValueKind != JsonValueKind.Object)
                {
                    Fail("notAnObject", "A filter entry is an object carrying id and where.");
                    continue;
                }

                JsonElement? where = null;
                var unknown = new List<string>();

                foreach (var property in element.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "id":
                            if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: > 0 and <= MaxFilterIdLength } id)
                                Fail("invalidId", $"A filter entry's 'id' is a string of 1 to {MaxFilterIdLength} characters.");
                            else if (!ids.Add(id))
                                Fail("duplicateId", $"The filter id '{id}' names more than one entry.");
                            else
                                entry.Id = id;
                            break;

                        case "where": where = property.Value; break;
                        default: unknown.Add(property.Name); break;
                    }
                }

                if (unknown.Count > 0)
                    Fail("unknownMember", $"'{string.Join(", ", unknown)}' is not a member of a filter entry; an entry carries id, where.");

                if (where is not { ValueKind: JsonValueKind.Object } condition)
                {
                    Fail("whereRequired", "A filter entry carries its condition under 'where', a condition object as a match stage takes it.");
                    continue;
                }

                FilterCondition? parsed;

                try
                {
                    parsed = FilterConditionConverter.ReadFromElement(condition, OxQLJson.Wire);
                }
                catch (JsonException malformed)
                {
                    Fail("malformedWhere", malformed.Message);
                    continue;
                }

                // An empty where matches everything and places nothing.
                if (parsed is null)
                    continue;

                // The filter's conditions alone over the request's bound: refused here, with nothing of it
                // placed or bound. (The request's whole count, its stages included, is checked at the end.)
                if ((leaves += Leaves(parsed, limits.MaxConditions + 1 - leaves)) > limits.MaxConditions)
                {
                    foreach (var read in filterEntries)
                        read.Unplaced.Clear();

                    filterUnplaced = 0;
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.MaxConditionsExceeded,
                        Message = $"The request's filter has more than {limits.MaxConditions} conditions; the limit is {limits.MaxConditions}.",
                        Stage = FilterMarker(entry),
                        Params = new Dictionary<string, object?> { ["limit"] = "maxConditions", ["max"] = limits.MaxConditions },
                    });
                    return;
                }

                foreach (var conjunct in Conjuncts(parsed))
                {
                    entry.Unplaced.Add(new FilterUnit(entry, conjunct, RowPaths(conjunct).Distinct(StringComparer.Ordinal).ToList()));
                    filterUnplaced++;
                }

                void Fail(string reason, string message)
                {
                    entry.Failed = true;
                    errors.Add(FilterError(entry, reason, message));
                }
            }
        }

        /// <summary>
        /// The leaf conditions of a condition (a comparison, those inside an <c>any</c> included), counted
        /// no further than <paramref name="upTo"/>: enough to know a bound is passed.
        /// </summary>
        private static int Leaves(FilterCondition condition, int upTo)
        {
            var count = 0;
            var open = new Stack<FilterCondition>();

            open.Push(condition);

            while (open.Count > 0 && count < upTo)
            {
                var next = open.Pop();
                var members = 0;

                foreach (var member in (next.And ?? []).Concat(next.Or ?? []))
                {
                    open.Push(member);
                    members++;
                }

                if (next.Not is { } negated && !ReferenceEquals(negated, FilterCondition.EmptyGroup))
                {
                    open.Push(negated);
                    members++;
                }

                if (next.Any is { } inner)
                {
                    open.Push(inner);
                    members++;
                }

                if (members == 0)
                    count++;
            }

            return count;
        }

        /// <summary>The longest <c>id</c> a filter entry may carry.</summary>
        private const int MaxFilterIdLength = 64;

        /// <summary>The top-level conjuncts of a condition: an <c>and</c> flattened through every <c>and</c> directly in it; anything else is one.</summary>
        private static IEnumerable<FilterCondition> Conjuncts(FilterCondition condition) =>
            condition.And is { Count: > 0 } and ? and.SelectMany(Conjuncts) : [condition];

        /// <summary>The paths a condition reads off the row: a comparison's path, an <c>any</c>'s collection (its inner paths are the element's), and those of every member of a group.</summary>
        private static IEnumerable<string> RowPaths(FilterCondition condition)
        {
            if (condition.Path is { } path)
                return [path];

            IEnumerable<FilterCondition> members = [.. condition.And ?? [], .. condition.Or ?? [], .. condition.Not is { } negated && !ReferenceEquals(negated, FilterCondition.EmptyGroup) ? [negated] : Array.Empty<FilterCondition>()];

            return members.SelectMany(RowPaths);
        }

        /// <summary>
        /// Whether a path is in the row here. A path that resolves is; so is one that is there but
        /// cannot be filtered (its error is the condition's, reported where it is placed). A path that
        /// is unknown here, left the row with an unwind, a projection or a group, or is not born yet is not.
        /// </summary>
        private bool InRow(string wire)
        {
            // not a read: a probe of whether the path is in the row at this boundary
            var resolution = shape.Resolve(wire, PathUsage.Match);

            return resolution.Succeeded || resolution.Code != Codes.UnknownPath;
        }

        /// <summary>
        /// Places every condition of the filter that can run at the boundary before caller stage
        /// <paramref name="boundary"/>: one whose paths are all in the row here and that was not placed
        /// earlier. So each runs at the earliest point its paths exist: a condition on an entity member
        /// at the entry, on an unwind's alias right after the unwind, on a join's alias after the join,
        /// on an aggregate after its group. The conditions of one entry placed at one boundary are one
        /// match. At the <paramref name="last"/> boundary what is still unplaced is refused: a path that
        /// was never in the row as an unknown path, paths that were never in the row together as
        /// <c>FILTER_NOT_PLACEABLE</c>.
        /// </summary>
        private void PlaceFilter(int boundary, bool last)
        {
            if (filterUnplaced == 0)
                return;

            // Whether a path is in the row is asked once per path at a boundary, however many conditions read it.
            var present = new Dictionary<string, bool>(StringComparer.Ordinal);

            bool Here(string path) => present.TryGetValue(path, out var known) ? known : present[path] = InRow(path);

            foreach (var entry in filterEntries)
            {
                if (entry.Unplaced.Count == 0)
                    continue;

                var conditions = new List<BoundCondition>();
                var paths = new List<string>();
                var named = new HashSet<string>(StringComparer.Ordinal);
                var left = 0;

                // One pass over what the entry still holds: a condition placed or refused here leaves the
                // list, so a later boundary costs what is still unplaced and nothing else.
                for (var at = 0; at < entry.Unplaced.Count; at++)
                {
                    var unit = entry.Unplaced[at];
                    var all = true;

                    foreach (var path in unit.Paths)
                    {
                        if (!Here(path))
                            all = false;
                        else if (!unit.Births.ContainsKey(path))
                            unit.Births[path] = boundary;
                    }

                    if (all)
                    {
                        unit.Placed = true;

                        foreach (var path in unit.Paths)
                            if (named.Add(path))
                                paths.Add(path);

                        if (BindCondition(unit.Condition, shape, FilterMarker(entry), Reading.Row) is { } bound)
                            conditions.Add(bound);
                    }
                    else if (last)
                    {
                        unit.Placed = true;
                        RefuseUnplaced(unit);
                    }
                    else
                    {
                        entry.Unplaced[left++] = unit;
                    }
                }

                filterUnplaced -= entry.Unplaced.Count - left;
                entry.Unplaced.RemoveRange(left, entry.Unplaced.Count - left);

                if (paths.Count > 0 || conditions.Count > 0)
                    entry.Placed.Add(new FilterPlacement(boundary, paths, shape.Grouped));

                if (conditions.Count == 0)
                    continue;

                var stage = new BoundStage.Match(conditions.Count == 1 ? conditions[0] : new BoundCondition.And(conditions));

                stages.Add(stage);
                callerIndexes.Add(null);
                filterStages[stage] = entry.Label;
            }
        }

        /// <summary>
        /// The refusal of a condition no boundary could take. A path that was never in the row is the
        /// error it would be in a match at the end of the pipeline. Paths that each were in the row, but
        /// never together (a member the group removed, OR-ed with an aggregate), cannot be placed.
        /// </summary>
        private void RefuseUnplaced(FilterUnit unit)
        {
            var never = unit.Paths.Where(path => !unit.Births.ContainsKey(path)).ToList();

            foreach (var path in never)
                // not a read: the refusal of a path that was never in the row
                errors.Add(Unresolved(shape.Resolve(path, PathUsage.Match), FilterMarker(unit.Entry), path));

            if (never.Count > 0)
                return;

            errors.Add(new QueryValidationError
            {
                Code = Codes.FilterNotPlaceable,
                Message = $"The condition reads {string.Join(", ", unit.Paths.Select(path => $"'{path}'"))}, which are never in the row at the same point of the pipeline; the engine places a condition where every path it reads exists. Split it into conditions that each read what exists together, or write a match stage where it should run.",
                Stage = FilterMarker(unit.Entry),
                Params = new Dictionary<string, object?>
                {
                    ["paths"] = unit.Paths.ToList(),
                    ["births"] = unit.Paths.Select(path => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = path,
                        [unit.Births[path] == 0 ? "before" : "after"] = unit.Births[path] == 0 ? 0 : unit.Births[path] - 1,
                    }).ToList(),
                },
            });
        }

        /// <summary>
        /// A group gives a name a placed condition reads to another thing: an aggregate, or a key over
        /// another path. The condition stays where its path first existed; explain says so
        /// (<c>FILTER_BINDS_EARLIER</c>). A key over the path of the same name is the same thing, and
        /// filtering the rows that feed it is filtering its groups.
        /// </summary>
        private void NoteFilterBindsEarlier(BoundStage.Group group, int index)
        {
            var reborn = group.Keys.Where(key => key.Path?.Wire != key.As).Select(key => key.As)
                .Concat(group.Fields.Select(field => field.As))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var entry in filterEntries)
                foreach (var path in entry.Placed.SelectMany(placement => placement.Paths).Distinct(StringComparer.Ordinal))
                    if (reborn.Contains(path.Split('.', 2)[0]) && !entry.BindsEarlier.Any(noted => noted.Path == path))
                        entry.BindsEarlier.Add((path, path.Split('.', 2)[0], index));
        }

        /// <summary>
        /// Names every finding of a filter entry by the entry: an error or a diagnostic recorded under
        /// an entry's marker loses the stage it never had and carries <c>filter</c>, the entry's id or
        /// its index.
        /// </summary>
        private void LabelFilterFindings()
        {
            if (filterEntries.Count == 0)
                return;

            for (var at = 0; at < errors.Count; at++)
            {
                if (errors[at].Stage is not { } marker || marker >= 0)
                    continue;

                var entry = filterEntries[-1 - marker];

                entry.Failed = true;
                errors[at] = errors[at] with { Stage = null, Filter = entry.Label };
            }

            for (var at = 0; at < diagnostics.Count; at++)
                if (diagnostics[at].Stage is < 0 and var marker)
                    diagnostics[at] = diagnostics[at] with { Stage = null, Filter = filterEntries[-1 - marker].Label };
        }

        private List<BoundFilter> BoundFilters() =>
            filterEntries.Select(entry => new BoundFilter(entry.Index, entry.Id, entry.Failed, entry.Placed.ToList(), entry.BindsEarlier.ToList())).ToList();

        private async Task LoadAddonsAsync(EntityDef target)
        {
            if (!target.Extendable || addons.ContainsKey(target.Id))
                return;

            var reading = Engine.QueryTiming.Now;

            // A source that says what it waited for at a database is asked to: that wait is database time, not this host's.
            if (context.Timing is { } timing && context.AddonSource is IReportingAddonDefinitionSource reporting)
            {
                var report = new AddonReadReport();

                addons[target.Id] = await reporting.ForEntityAsync(target.Id, context.Organisation!.Value, report, cancellationToken);
                timing.Addon(reading);

                foreach (var read in report.Reads)
                    timing.Read(Engine.QueryTiming.AddonRead, read.Collection, read.Started, read.Ended, read.Docs, read.RoundTrips);

                return;
            }

            addons[target.Id] = await context.AddonSource.ForEntityAsync(target.Id, context.Organisation!.Value, cancellationToken);
            context.Timing?.Addon(reading);
        }

        // ---- match ---------------------------------------------------------------------------

        private void BindMatch(MatchStage match, int index)
        {
            if (match.Condition is null)
                return;

            var bound = BindCondition(match.Condition, shape, index, Reading.Row);

            if (bound is not null)
                stages.Add(new BoundStage.Match(bound));
        }

        /// <summary>
        /// Binds one condition, refusing a tree nested deeper than <see cref="MaxConditionDepth"/>
        /// levels of <c>and</c>, <c>or</c>, <c>not</c> and <c>any</c> once per request: the
        /// compiled filter would pass the driver's nesting limit and fail as a fault.
        /// </summary>
        private BoundCondition? BindCondition(FilterCondition condition, Shape at, int index, Reading? reading)
        {
            if (conditionDepth >= MaxConditionDepth)
            {
                if (!conditionDepthReported)
                    errors.Add(Limit(Codes.MaxConditionsExceeded, $"A condition nests more than {MaxConditionDepth} levels of and, or, not and any; flatten the groups.", index, null, "maxConditionDepth", MaxConditionDepth, null));

                conditionDepthReported = true;
                return null;
            }

            conditionDepth++;

            try
            {
                return BindConditionAt(condition, at, index, reading);
            }
            finally
            {
                conditionDepth--;
            }
        }

        private BoundCondition? BindConditionAt(FilterCondition condition, Shape at, int index, Reading? reading)
        {
            if (condition.And is not null)
                return BindGroup(condition.And, at, index, "and", list => new BoundCondition.And(list), reading);

            if (condition.Or is not null)
                return BindGroup(condition.Or, at, index, "or", list => new BoundCondition.Or(list), reading);

            if (condition.Not is not null)
            {
                if (condition.IsEmptyGroup)
                {
                    errors.Add(Error(Codes.EmptyLogicalGroup, "'not' has no condition.", index, null));
                    return null;
                }

                var inner = BindCondition(condition.Not, at, index, reading);

                return inner is null ? null : new BoundCondition.Not(inner);
            }

            if (condition.Path is null)
            {
                errors.Add(Error(Codes.EmptyLogicalGroup, "A condition names a path or a logical group.", index, null));
                return null;
            }

            if (condition.IsAny)
                return BindAny(condition, at, index, reading);

            conditions++;

            var resolution = Read(at, condition.Path, PathUsage.Match, ReadUse.Match, index, reading);

            if (!resolution.Succeeded)
            {
                errors.Add(Unresolved(resolution, index, condition.Path));
                return null;
            }

            var path = resolution.Path!;
            var op = condition.Op ?? "eq";

            if (!OperandCoercer.Operators.Contains(op))
            {
                errors.Add(Error(Codes.UnknownOperator, $"'{op}' is not an operator.", index, condition.Path));
                return null;
            }

            // The variant test is a contract 2 operator; contract 1 never had it.
            if (op == "is" && !contract2)
            {
                errors.Add(Error(Codes.UnknownOperator, "'is' is not an operator under contract 1; the variant test is a contract 2 operator." + Contract1Hint, index, condition.Path));
                return null;
            }

            // A join's outcome compares with its own vocabulary, exactly.
            if (path.Root is ShapeNode.Outcome)
                return BindOutcomeCondition(condition, path, op, index);

            // The alias of a remote resolve is the owner's row, not a path of the owner: only a
            // member of it travels as a semi-join, so a condition on the alias itself stops here
            // instead of reaching the owner as a match on a name it does not have.
            if (path.IsRemote && !path.Filterable)
            {
                errors.Add(Error(Codes.ResolveNotFilterable,
                    $"'{Named(condition.Path)}' is the alias of a remote resolve; filter on one of its members (a semi-join on the owner), not on the alias itself.", index, condition.Path));
                return null;
            }

            // A variant test is on an object, which no other comparison can filter; under a remote
            // alias the owner binds it like any other condition.
            if (op == "is" && !path.IsRemote)
                return BindIs(condition, path, index);

            // The model's own type member: compared by the variants that hold the value.
            if (path.Derived is { } derived && !path.IsRemote)
                return BindDerived(condition, path, derived, op, at, index, reading);

            if (!path.IsRemote && !path.Filterable && !(op == "exists" && path.Storage is not null))
            {
                errors.Add(Error(Codes.NotFilterable, path.Kind == Kind.Unknown
                    ? $"'{Named(condition.Path)}' is unknown to the model; it can be projected, not filtered."
                    : $"'{Named(condition.Path)}' is {Kinds.WithArticle(path.Kind)}; a filter needs a scalar.", index, condition.Path));
                return null;
            }

            if (!path.IsRemote && !OperandCoercer.Applies(op, path.LeafKind))
            {
                errors.Add(Operand($"'{op}' does not apply to {Kinds.WithArticle(path.LeafKind)}.", index, condition.Path, op, "operator", path.LeafKind));
                return null;
            }

            // A char is a string on the wire and a code point in storage, so the four text
            // operators pass the kind gate above and have nothing to match against.
            if (!path.IsRemote && OperandCoercer.NeedsText(op) && OperandCoercer.IsCharRepresented(path))
            {
                errors.Add(Operand($"'{Named(condition.Path)}' holds a single character stored as its code point; '{op}' needs text. Compare it with eq, neq, in or nin.", index, condition.Path, op, "operator", path.LeafKind));
                return null;
            }

            var caseSensitive = BindCaseOption(condition, path, op, index);

            var operand = coercer.Coerce(condition.Value, path, op, index, errors);

            if (operand is null)
                return null;

            // Under a remote alias the owner binds the comparison: what the caller wrote travels
            // as written, and nothing written leaves the owner its own default.
            if (path.IsRemote)
            {
                hasSemiJoin = true;

                return new BoundCondition.Leaf(path, op, operand, caseSensitive is { } remoteChoice ? !remoteChoice : null, IsSemiJoin: true);
            }

            // Under contract 2 a string comparison folds case and accents unless it opts out;
            // under contract 1 it folds when asked to, as it always has.
            var ignoreCase = contract2
                ? caseSensitive is not true && OperandCoercer.FoldsByDefault(op, path)
                : caseSensitive is false && OperandCoercer.IgnoreCaseApplies(op, path.LeafKind);

            // A char compares by code point, so a case-insensitive comparison is the set of
            // the character's case forms, compared by value, rather than a pattern the
            // stored code point could never match.
            if (ignoreCase && OperandCoercer.IsCharRepresented(path))
            {
                (op, operand) = FoldCharCase(op, operand);
                ignoreCase = false;
            }

            // A null and an empty set compare no text; there is nothing to fold.
            if (ignoreCase && !TextsOf(operand).Any())
                ignoreCase = false;

            // A contract 1 fold is a pattern; only a contract 2 fold needs the collation.
            if (ignoreCase && contract2)
                collated = true;

            // A text operand that is matched as a pattern is escaped and anchored by the
            // compiler; one the database cannot compile is a caller error, refused here. A
            // contract 1 fold is a pattern always; a comparison that opts out of the fold
            // becomes one only inside a collated aggregate, which is known once every stage
            // is bound, so it is checked then.
            var isPattern = op is "contains" or "startsWith" or "endsWith" || (ignoreCase && !contract2);

            // A folding contains or endsWith is a pattern that folds accents too: each letter
            // is a class of its equivalents, and it is that pattern the server has to compile.
            // So is a folding startsWith through a collection, where the prefix cannot be a range.
            var throughCollection = path.CollectionAncestors > 0 || path.Kind == Kind.Array;
            var foldsAccents = contract2 && ignoreCase && (op is "contains" or "endsWith" || (op == "startsWith" && throughCollection)) && AccentFold.Applies(options.Representation.Collation);

            if (isPattern && (foldsAccents ? !TextsOf(operand).All(text => RegexGuard.PatternFits(AccentFold.Pattern(text, options.Representation.Collation))) : !TextFits(operand)))
            {
                errors.Add(Operand(TooLongToMatch(op, Named(condition.Path)!), index, condition.Path, op, "length"));
                return null;
            }

            if (caseSensitive is true && path.LeafKind == Kind.String && !TextFits(operand))
                exactTexts.Add((index, condition.Path, op));

            if (op == "regex" && operand is BoundOperand.Single { Value: BsonString pattern } && !RegexGuard.IsAnchored(pattern.Value))
                diagnostics.Add(new Diagnostic { Code = Codes.RegexUnanchored, Message = $"The pattern on '{Named(condition.Path)}' is not anchored; it scans every value of the member.", Stage = index, Path = condition.Path });

            return new BoundCondition.Leaf(path, op, operand, ignoreCase, IsSemiJoin: false);
        }

        /// <summary>
        /// A condition on a join's outcome (<c>outcomeAs</c>): <c>eq</c>, <c>neq</c>, <c>in</c> or
        /// <c>nin</c> against the outcomes as the wire spells them. The member always holds one of them,
        /// so it compares exactly: nothing folds, no option applies, and a name that is no outcome is
        /// refused rather than matching nothing.
        /// </summary>
        private BoundCondition? BindOutcomeCondition(FilterCondition condition, ResolvedPath path, string op, int index)
        {
            if (op is not ("eq" or "neq" or "in" or "nin"))
            {
                errors.Add(Operand($"'{op}' does not apply to an outcome; compare '{Named(condition.Path)}' with eq, neq, in or nin.", index, condition.Path, op, "operator"));
                return null;
            }

            if (condition.Options is not null)
            {
                errors.Add(Option(Error(Codes.OptionNotApplicable, $"'{Named(condition.Path)}' is an outcome, which compares exactly; no option applies to it.", index, condition.Path), "options", "outcome"));
                return null;
            }

            var operand = coercer.Coerce(condition.Value, path, op, index, errors);

            if (operand is null)
                return null;

            var named = operand switch
            {
                BoundOperand.Single { Value: BsonString one } => [one.Value],
                BoundOperand.Set set when set.Values.All(value => value is BsonString) => set.Values.Select(value => value.AsString).ToList(),
                _ => (List<string>?)null,
            };

            if (named is null)
            {
                errors.Add(Error(Codes.InvalidOperand, $"'{Named(condition.Path)}' is an outcome; compare it with one of {string.Join(", ", Notes.Outcomes)}.", index, condition.Path));
                return null;
            }

            if (named.FirstOrDefault(name => !Notes.Outcomes.Contains(name, StringComparer.Ordinal)) is { } unknown)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnknownEnumMember,
                    Message = $"'{unknown}' is not an outcome; the outcomes are {string.Join(", ", Notes.Outcomes)}.",
                    Stage = index,
                    Path = condition.Path,
                    Params = contract2 ? new Dictionary<string, object?> { ["values"] = Notes.Outcomes } : null,
                });
                return null;
            }

            return new BoundCondition.Leaf(path, op, operand, IgnoreCase: false, IsSemiJoin: false);
        }

        /// <summary>
        /// A condition on a member whose value follows from the variant its object is stored as (the
        /// model's own type member): <c>{ "resource.type": { "eq": "driver" } }</c>. Each variant holds
        /// one value, so <c>eq</c> and <c>in</c> are the variant test over the variants that hold one of
        /// the values, and <c>neq</c> and <c>nin</c> its negation; several variants may hold one value. A
        /// value no variant holds selects no row (and every row, negated). Text folds case unless the
        /// condition says <c>caseSensitive</c>, as every string comparison of contract 2 does. Under a
        /// collection the test is of some element, and negated of none, as it is for a stored member.
        /// </summary>
        private BoundCondition? BindDerived(FilterCondition condition, ResolvedPath path, DerivedDef derived, string op, Shape at, int index, Reading? reading)
        {
            if (op is not ("eq" or "neq" or "in" or "nin"))
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidOperand,
                    Message = $"'{op}' does not apply to '{Named(condition.Path)}', whose value follows from the variant its object is stored as; compare it with eq, neq, in or nin.",
                    Stage = index,
                    Path = condition.Path,
                    Params = new Dictionary<string, object?> { ["reason"] = PathReasons.Derived, ["can"] = new List<string> { "eq", "neq", "in", "nin" } },
                });
                return null;
            }

            var caseSensitive = BindCaseOption(condition, path, op, index);
            var operand = coercer.Coerce(condition.Value, path, op, index, errors);

            if (operand is null)
                return null;

            var texts = operand switch
            {
                BoundOperand.Single { Value: BsonString one } => [one.Value],
                BoundOperand.Set set when set.Values.All(value => value is BsonString) => set.Values.Select(value => value.AsString).ToList(),
                _ => (List<string>?)null,
            };

            if (texts is null)
            {
                errors.Add(Error(Codes.InvalidOperand,
                    $"'{Named(condition.Path)}' holds one of {string.Join(", ", derived.Values.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(value => $"'{value}'"))} wherever its object is; compare it with one of them. Whether the object is there is a test of the object ('exists').",
                    index, condition.Path));
                return null;
            }

            var comparison = caseSensitive is true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var holder = derived.Holder;
            var baseName = OperandCoercer.ConcreteBaseName(holder);
            var selected = derived.Values.Where(held => texts.Any(text => string.Equals(text, held.Value, comparison))).Select(held => held.Key).ToHashSet(StringComparer.Ordinal);
            var includeBase = baseName is not null && selected.Contains(baseName);
            var values = new List<BsonValue>();

            if (includeBase && holder.DiscriminatorForm == DiscriminatorForm.Hierarchical)
                values.Add(new BsonString(holder.Discriminator ?? baseName!));

            foreach (var variant in holder.Variants)
                if (selected.Contains(variant.Name) && !values.Contains(new BsonString(variant.Discriminator)))
                    values.Add(new BsonString(variant.Discriminator));

            if (includeBase)
                values.Add(BsonNull.Value);

            // The object that holds the member: the path one segment up, or the row or the alias itself.
            var dot = condition.Path!.LastIndexOf('.');
            ResolvedPath holding;

            if (dot < 0)
            {
                holding = new ResolvedPath
                {
                    Wire = "", Storage = null, Kind = Kind.Object, Entity = path.Entity, CollectionAncestors = 0, Filterable = false, Sortable = false, Root = path.Root,
                    Derived = derived,
                };
            }
            else
            {
                // not a read: the object holding the member, whose read is recorded; the discriminator lies beside it
                var resolution = at.Resolve(condition.Path[..dot], PathUsage.Match);

                if (!resolution.Succeeded)
                {
                    errors.Add(Unresolved(resolution, index, condition.Path));
                    return null;
                }

                // The holder is named on the path, so the test reads the discriminator this type stores, whatever the place is.
                holding = resolution.Path! with { Derived = derived };
            }

            // A hierarchical discriminator lists a row's ancestors too, so a variant's discriminator also
            // finds the rows of the variants derived from it; where those hold another value they are taken
            // out again, which one object can say and a collection's elements cannot say together. A
            // concrete root class is above every variant: its own discriminator stands in every row, so
            // where its value is asked for every variant that holds another value is taken out.
            var hierarchical = holder.DiscriminatorForm == DiscriminatorForm.Hierarchical;
            var inherited = hierarchical
                ? holder.Variants.Where(variant => !selected.Contains(variant.Name) && (includeBase || holder.Variants.Any(above => selected.Contains(above.Name) && OperandCoercer.Descends(variant, above)))).ToList()
                : [];
            var throughCollection = holding.CollectionAncestors > 0 || holding.Kind == Kind.Array;

            if (throughCollection && (inherited.Count > 0 || (includeBase && holding.CollectionAncestors > 0)))
            {
                errors.Add(Error(Codes.InvalidOperand,
                    $"'{Named(condition.Path)}' lies under a collection that is not unwound, and which value an element holds cannot be told from its stored variant without looking at one element at a time; test the elements with 'any' on the collection, or unwind it first.",
                    index, condition.Path));
                return null;
            }

            BoundCondition test = new BoundCondition.Leaf(holding, "is", new BoundOperand.Set(values), IgnoreCase: false, IsSemiJoin: false);

            if (inherited.Count > 0)
                test = new BoundCondition.And([test, new BoundCondition.Not(new BoundCondition.Leaf(holding, "is", new BoundOperand.Set([.. inherited.Select(variant => (BsonValue)new BsonString(variant.Discriminator))]), IgnoreCase: false, IsSemiJoin: false))]);

            return op is "eq" or "in" ? test : new BoundCondition.Not(test);
        }

        /// <summary>
        /// A variant test: <c>{ "pet": { "is": "Dog" } }</c>. It applies to an object, or a
        /// collection of objects (some element), whose pooled type has variants, on the row, an
        /// unwound element, a join alias or inside <c>any</c>. Under a collection that is not
        /// unwound it would need the collection's own correlation, which <c>any</c> is.
        /// </summary>
        private BoundCondition? BindIs(FilterCondition condition, ResolvedPath path, int index)
        {
            var type = OperandCoercer.VariantHolder(path);

            if (type is null || type.Variants.Count == 0)
            {
                var what = type is null ? Kinds.WithArticle(path.LeafKind) : "an object of one type only";

                errors.Add(Operand($"'is' applies to a member that holds one of several variants; '{Named(condition.Path)}' is {what}.", index, condition.Path, "is", "operator", path.LeafKind));
                return null;
            }

            if (path.CollectionAncestors > 0)
            {
                errors.Add(Operand($"'{Named(condition.Path)}' lies under a collection that is not unwound; test its elements with 'any' on the collection, or unwind it first.", index, condition.Path, "is", "collection"));
                return null;
            }

            if (condition.Options is not null)
            {
                errors.Add(Option(Error(Codes.OptionNotApplicable, "'is' takes no options.", index, condition.Path), "options", "operator"));
                return null;
            }

            var operand = coercer.CoerceVariants(condition.Value, path, type, index, errors);

            return operand is null ? null : new BoundCondition.Leaf(path, "is", operand, IgnoreCase: false, IsSemiJoin: false);
        }

        /// <summary>A stage object that is no stage, with what a client renders it from (contract 2): why, and the keys it carries.</summary>
        private QueryValidationError NoStage(QueryValidationError error, string reason, IReadOnlyList<string> keys) => !contract2 ? error : error with
        {
            Params = new Dictionary<string, object?> { ["reason"] = reason, ["keys"] = keys.ToList() },
        };

        /// <summary>
        /// A member a stage does not have, or has in another form, with what a client renders it from
        /// (contract 2): the stage, the members (<c>member</c> is the first) and why.
        /// </summary>
        private QueryValidationError NoMember(QueryValidationError error, string stage, IReadOnlyList<string> members, string reason) => !contract2 || error.Code != Codes.UnknownStageMember ? error : error with
        {
            Params = new Dictionary<string, object?>(error.Params ?? new Dictionary<string, object?>())
            {
                ["stage"] = stage,
                ["member"] = members.Count > 0 ? members[0] : null,
                ["members"] = members.ToList(),
                ["reason"] = reason,
            },
        };

        /// <summary>
        /// A refusal of an option with what a client renders it from (contract 2): the option, and why
        /// it does not apply.
        /// </summary>
        private QueryValidationError Option(QueryValidationError error, string option, string reason) => !contract2 ? error : error with
        {
            Params = new Dictionary<string, object?>(error.Params ?? new Dictionary<string, object?>()) { ["option"] = option, ["reason"] = reason },
        };

        /// <summary>
        /// The case option of a condition, as the caller's choice: true to compare exactly,
        /// false to fold, null when nothing was written. <c>caseSensitive</c> is the option;
        /// <c>ignoreCase</c> is contract 1's, where only <c>ignoreCase: true</c> means anything, as it
        /// always has; contract 2 refuses it.
        /// <para>
        /// The option applies to the comparisons a string can opt out of. A member that holds
        /// no text has nothing to fold; an ordered comparison orders under the collation of the
        /// whole aggregate and cannot leave it; a pattern is the caller's own. A remote path is
        /// the owner's to check.
        /// </para>
        /// </summary>
        private bool? BindCaseOption(FilterCondition condition, ResolvedPath path, string op, int index)
        {
            if (condition.Options is not { } conditionOptions)
                return null;

            // A name the engine knows, written with a value that is no boolean, is not an unknown option; neither is an 'options' that is no object.
            foreach (var name in conditionOptions.Unknown ?? [])
            {
                var (message, reason) = name switch
                {
                    "options" => ("'options' is an object of options: { \"caseSensitive\": true }.", "kind"),
                    "caseSensitive" or "ignoreCase" => ($"'{name}' is true or false.", "kind"),
                    _ => ($"'{name}' is not an option; a condition's option is {(contract2 ? "caseSensitive" : "ignoreCase")}.", "unknown"),
                };

                errors.Add(Error(Codes.OptionNotApplicable, message, index, condition.Path) with
                {
                    Params = contract2 ? new Dictionary<string, object?> { ["option"] = name, ["reason"] = reason } : null,
                });
            }

            var applies = path.IsRemote || OperandCoercer.IgnoreCaseApplies(op, path.LeafKind);
            var why = path.LeafKind == Kind.String && !path.IsRemote && OperandCoercer.FoldsByDefault(op, path)
                ? $"'{op}' on '{Named(condition.Path)}' orders under the collation of the whole request and cannot opt out of it"
                : $"'{Named(condition.Path)}' is a {Kinds.NameOf(path.LeafKind)} under '{op}'";
            bool? caseSensitive = null;

            if (conditionOptions.CaseSensitive is { } sensitive)
            {
                if (!contract2)
                    errors.Add(Error(Codes.OptionNotApplicable, "'caseSensitive' is not an option under contract 1; it is a contract 2 option." + Contract1Hint, index, condition.Path));
                else if (!applies)
                    errors.Add(Option(Error(Codes.OptionNotApplicable, $"'caseSensitive' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; {why}.", index, condition.Path), "caseSensitive", "notApplicable"));
                else
                    caseSensitive = sensitive;
            }

            if (conditionOptions.IgnoreCase is not { } ignore)
                return caseSensitive;

            // Contract 2 has the one option: the alias with the opposite sense is contract 1's.
            if (contract2)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.OptionNotApplicable,
                    Message = "'ignoreCase' is not an option under contract 2; a string comparison folds case unless it says 'caseSensitive: true'.",
                    Stage = index,
                    Path = condition.Path,
                    Params = new Dictionary<string, object?> { ["option"] = "ignoreCase", ["reason"] = "removed", ["use"] = "caseSensitive" },
                });
                return caseSensitive;
            }

            // Contract 1 from here on, where 'caseSensitive' is no option. `ignoreCase: false` restates
            // its default: it changes nothing and is not refused.
            if (!ignore)
                return null;

            if (!applies)
            {
                errors.Add(Error(Codes.OptionNotApplicable, $"'ignoreCase' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; {why}.", index, condition.Path));
                return null;
            }

            return false;
        }

        /// <summary>
        /// The comparison widened to every case form of each character in its operand: an
        /// equality becomes a set membership when the character has another form, and a
        /// membership takes the forms into its set.
        /// </summary>
        private static (string Op, BoundOperand Operand) FoldCharCase(string op, BoundOperand operand)
        {
            IReadOnlyList<BsonValue>? values = operand switch
            {
                BoundOperand.Single single => [single.Value],
                BoundOperand.Set set => set.Values,
                _ => null,
            };

            if (values is null)
                return (op, operand);

            var forms = new List<BsonValue>();

            foreach (var value in values)
            {
                if (value is not BsonInt32 code)
                {
                    forms.Add(value);
                    continue;
                }

                var character = (char)code.Value;

                foreach (var form in new[] { character, char.ToUpperInvariant(character), char.ToLowerInvariant(character) })
                    if (!forms.Contains(new BsonInt32(form)))
                        forms.Add(new BsonInt32(form));
            }

            return forms.Count == 1 && operand is BoundOperand.Single
                ? (op, new BoundOperand.Single(forms[0]))
                : (op is "eq" or "in" ? "in" : "nin", new BoundOperand.Set(forms));
        }

        /// <summary>Whether every text of an operand still fits the server's pattern limit once escaped and anchored.</summary>
        private static bool TextFits(BoundOperand operand) => TextsOf(operand).All(RegexGuard.LiteralFits);

        private static string TooLongToMatch(string op, string path) => $"The operand of '{op}' on '{path}' is too long to be matched as text.";

        /// <summary>The text values of an operand: the ones a pattern comparison is built from.</summary>
        private static IEnumerable<string> TextsOf(BoundOperand operand) => operand switch
        {
            BoundOperand.Single { Value: BsonString text } => [text.Value],
            BoundOperand.Set set => set.Values.OfType<BsonString>().Select(text => text.Value),
            BoundOperand.Tolerant tolerant => tolerant.Alternatives.OfType<BsonString>().Select(text => text.Value),
            _ => [],
        };

        private BoundCondition? BindGroup(IReadOnlyList<FilterCondition> group, Shape at, int index, string name, Func<IReadOnlyList<BoundCondition>, BoundCondition> make, Reading? reading)
        {
            if (group.Count == 0)
            {
                errors.Add(Error(Codes.EmptyLogicalGroup, $"'{name}' has no conditions.", index, null));
                return null;
            }

            var bound = new List<BoundCondition>(group.Count);

            foreach (var member in group)
            {
                var inner = BindCondition(member, at, index, reading);

                if (inner is not null)
                    bound.Add(inner);
            }

            return bound.Count == group.Count ? make(bound) : null;
        }

        private BoundCondition? BindAny(FilterCondition condition, Shape at, int index, Reading? reading)
        {
            var resolution = Read(at, condition.Path!, PathUsage.Match, ReadUse.Match, index, reading);

            if (!resolution.Succeeded)
            {
                errors.Add(Unresolved(resolution, index, condition.Path));
                return null;
            }

            var path = resolution.Path!;

            if (path.IsRemote || path.Path is null || path.Kind != Kind.Array || path.Shape?.Of?.Kind != Kind.Object || path.CollectionAncestors > 0)
            {
                errors.Add(Error(Codes.AnyNotApplicable, path.Kind != Kind.Array
                    ? $"'{Named(condition.Path)}' is not a collection here; 'any' applies to a collection of objects that is not unwound."
                    : path.CollectionAncestors > 0
                        ? $"'{Named(condition.Path)}' lies under another collection; unwind the outer one first."
                        : $"'{Named(condition.Path)}' is a collection of scalars; the plain form already matches some element.", index, condition.Path));
                return null;
            }

            // An 'any' written with no condition in it is said as that, not as the group it is read into.
            if (ReferenceEquals(condition.Any, FilterCondition.EmptyAny))
            {
                errors.Add(Error(Codes.EmptyLogicalGroup, "'any' has no conditions; it takes the conditions one element of the collection must meet.", index, condition.Path));
                return null;
            }

            // The inner paths are relative to one element; the ledger has them under the collection.
            var errorsBefore = errors.Count;
            // What the conditions of an element say of a member, they say of it as the row has it: under its collection.
            (under, coercer.Under) = (condition.Path, condition.Path);

            BoundCondition? inner;

            try
            {
                inner = BindCondition(condition.Any!, at.ForElement(path), index, reading is { } row ? new Reading(row.Prefix + condition.Path + ".") : null);
            }
            finally
            {
                (under, coercer.Under) = (null, null);
            }

            // So has an error: it names the path as the row has it (the collection, then the element's member), in its path and its params.
            for (var position = errorsBefore; position < errors.Count; position++)
                errors[position] = UnderCollection(errors[position], condition.Path!);

            return inner is null ? null : new BoundCondition.Any(path, inner);
        }

        /// <summary>
        /// An error of a condition inside <c>any</c>, with the element's member named under its
        /// collection in its path and its params. Its words are written with that name where they are
        /// written (<see cref="Named"/>), so nothing else in them is touched: an operator, a variable or
        /// an operand spelled like the member stays what the caller wrote.
        /// </summary>
        private static QueryValidationError UnderCollection(QueryValidationError error, string collection)
        {
            if (error.Path is not { Length: > 0 } relative || error.Code == Shape.PoisonedCode)
                return error;

            var full = collection + "." + relative;
            var parameters = error.Params;

            if (parameters is not null && parameters.TryGetValue("path", out var named) && named is string text && text == relative)
                parameters = new Dictionary<string, object?>(parameters) { ["path"] = full };

            return error with { Path = full, Params = parameters };
        }

        /// <summary>The collection whose elements the conditions being bound are of (inside <c>any</c>); null elsewhere.</summary>
        private string? under;

        /// <summary>A member as a message names it: under the collection whose element it is a member of, inside <c>any</c>.</summary>
        private string? Named(string? path) => under is null || path is null ? path : under + "." + path;

        // ---- lookup --------------------------------------------------------------------------

        private async Task BindLookupAsync(LookupStage lookup, int index)
        {
            // sort, first, on and forTarget are contract 2 members; under contract 1 they are
            // members the stage does not have, as is any of them written with the wrong kind.
            var contract2Members = new (string Name, bool Written)[]
            {
                ("sort", lookup.Sort is not null), ("first", lookup.First is not null), ("on", lookup.On is not null), ("forTarget", lookup.ForTarget is not null),
                ("parentAs", lookup.ParentAs is not null),
            };
            IReadOnlyList<string> unknown = contract2
                ? lookup.Unknown
                : [.. lookup.Unknown, .. contract2Members.Where(member => member.Written).Select(member => member.Name), .. lookup.Malformed];

            if (unknown.Count > 0)
            {
                errors.Add(NoMember(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                    $"'{string.Join(", ", unknown)}' is not a member of lookup; a lookup carries {(contract2 ? LookupMembers : "from, path, as, select, filter, limit")}."
                    + Hint(contract2Members.Any(member => member.Written) || lookup.Malformed.Count > 0), index, null), "lookup", unknown, "unknown"));
                return;
            }

            if (lookup.Select is not null && RefuseSelect("lookup", LookupMembers, lookup.As, index))
                return;

            if (contract2 && lookup.LimitInvalid && RefuseInvalidMembers(["limit"], "lookup", LookupMembers, "a whole number", index))
                return;

            if (lookup.Malformed.Count > 0)
            {
                foreach (var name in lookup.Malformed)
                    errors.Add(NoMember(Error(Codes.UnknownStageMember, name switch
                    {
                        "first" => "A lookup's 'first' is true or false.",
                        "sort" => "A lookup's 'sort' is an array of sort entries: [{\"path\": \"asc\"}, …].",
                        _ => $"A lookup's '{name}' is a string.",
                    }, index, null), "lookup", [name], "kind"));
                return;
            }

            // On a keyed or remote alias the lookup runs at that alias's owner;
            // it is not a lookup of this host and does not count towards its limit.
            if (lookup.On is { } continuedOn && anchors.TryGetValue(continuedOn, out var anchor))
            {
                // An item target's alias holds an element, not an entity row: every owner would refuse
                // the lookup, so it is refused here (its owning row, parentAs, is the entity row).
                if (continuedOn == anchor.Stage.As && ItemTargetsOnly(anchor, lookup.ForTarget))
                {
                    errors.Add(Error(Codes.LookupOnNotEntity,
                        $"'{continuedOn}' is an element of an item collection; a lookup's parent is one entity row. Join on the owning row ('parentAs') instead.", index, continuedOn));
                    return;
                }

                BindContinued(new PipelineStage { Lookup = lookup, Keys = ["lookup"] }, index, continuedOn, anchor, lookup.ForTarget, [lookup.As, lookup.ParentAs]);
                return;
            }

            if (++lookups > options.Limits.MaxLookupStages)
                errors.Add(Limit(Codes.MaxLookupStagesExceeded, $"The pipeline has more than {options.Limits.MaxLookupStages} lookup stages.", index, null, "maxLookupStages", options.Limits.MaxLookupStages, lookups));

            // The parent: the implicit root, or the entity row the alias 'on' names.
            var parent = entity;
            var parentPrefix = "";

            if (lookup.On is null)
            {
                if (shape.Grouped)
                {
                    errors.Add(Error(Codes.UnknownPath, "The shape after a group has no key to join on.", index, null));
                    return;
                }
            }
            else if (!BindLookupParent(lookup.On, index, out parent, out parentPrefix))
            {
                return;
            }

            // forTarget picks one target of an alias a stage continues under; a lookup this host
            // runs continues nothing.
            if (lookup.ForTarget is not null)
            {
                errors.Add(Option(Error(Codes.OptionNotApplicable,
                    $"'forTarget' applies to a stage continued under a remote alias with several targets; {(lookup.On is null ? "this lookup joins on the entity itself" : $"'{lookup.On}' is a row of this host")}.", index, null), "forTarget", "notContinued"));
                return;
            }

            if (string.IsNullOrWhiteSpace(lookup.From) || !binder.model.TryResolve(lookup.From, out var child, out var retired))
            {
                // Another service's entity: the lookup runs at its owner through the keyed fetch,
                // when this host can reach that owner.
                if (RemoteOwnerOf(lookup.From) is { } service)
                {
                    BindRemoteLookup(lookup, index, parent, service);
                    return;
                }

                var from = lookup.From?.Trim() ?? "";
                var owner = from.Split('.')[0];

                errors.Add(Error(Codes.UnknownEntity, from.Contains('#', StringComparison.Ordinal) && IsOwnNamespace(owner)
                    ? $"'{from}' names an item collection of this host; a lookup of this host joins whole entities: look up '{from.Split('#')[0]}' and unwind the collection."
                    : contract2 && !IsOwnNamespace(owner) && owner.Length > 0 && from.Contains('.', StringComparison.Ordinal)
                        ? $"'{from}' is not an entity of this host, and this host knows no owner for '{owner}'."
                        : $"'{lookup.From}' is not an entity of this host.", index, null));
                return;
            }

            // The owning row of an element: only a lookup of another service's item collection has one.
            if (lookup.ParentAs is not null)
            {
                errors.Add(Option(Error(Codes.OptionNotApplicable,
                    $"'parentAs' applies to a lookup of another service's item collection ('entity#item'); '{child.Id}' is an entity of this host.", index, null), "parentAs", "notAnItem"));
                return;
            }

            // A retired id is announced wherever the caller wrote it, as at the root.
            if (retired)
                diagnostics.Add(new Diagnostic
                {
                    Code = Codes.EntityIdRetired,
                    Message = $"'{lookup.From}' was retired; the entity is '{child.Id}'.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["currentId"] = child.Id },
                });

            if (!CheckAlias(lookup.As, index, out var alias))
                return;

            await LoadAddonsAsync(child);

            var childShape = Shape.ForEntity(child, addons).Deriving(contract2);
            // not a read: the child's own reference member, matched inside the join
            var reference = childShape.Resolve(lookup.Path ?? "", PathUsage.Match);

            if (!reference.Succeeded)
            {
                errors.Add(Unresolved(reference, index, lookup.Path));
                return;
            }

            var childPath = reference.Path!;

            if (childPath.Reference() is not { } declared || declared.TargetEntity != parent.Id)
            {
                errors.Add(Joinable(Error(Codes.LookupNotDeclared, $"'{child.Id}#{lookup.Path}' does not declare a reference to '{parent.Id}'.", index, lookup.Path), child, parent.Id));
                return;
            }

            var targetField = declared.TargetField;
            var parentKey = parent.Path(targetField);

            if (parentKey is null || !parentKey.Stored)
            {
                errors.Add(Error(Codes.LookupNotDeclared, $"The reference targets '{parent.Id}#{targetField}', which is not stored.", index, lookup.Path));
                return;
            }

            // A projection that kept members of the parent alias but not its key left nothing to join on.
            if (lookup.On is not null && !shape.IsVisible(lookup.On + "." + parentKey.Wire))
            {
                errors.Add(Error(Codes.UnknownPath, $"'{lookup.On}.{parentKey.Wire}' was removed by the projection; the lookup joins on it.", index, lookup.On));
                return;
            }

            // The lookup reads the parent's key off the row: the join that holds the parent loads it.
            Read(shape, lookup.On is null ? parentKey.Wire : lookup.On + "." + parentKey.Wire, PathUsage.Match, ReadUse.LookupOn, index, Reading.Row);

            var childScope = ScopeOf(child, context.Organisation!.Value);

            if (childScope is null)
            {
                errors.Add(Error(Codes.AccessDenied, $"'{child.Id}' has no organisation member; it cannot be joined.", index, null));
                return;
            }

            var select = BindSelect(lookup.Select, child, childShape, index);
            var filter = lookup.Filter?.Condition is null ? null : BindCondition(lookup.Filter.Condition, childShape, index, reading: null);
            var sortFields = lookup.Sort is { Count: > 0 } entries ? BindSortEntries(entries, childShape, index, reading: null) : [];
            var first = lookup.First is true;
            int limit;

            if (first)
            {
                // One child is what first takes; a limit would say something else.
                if (lookup.Limit is not null)
                    errors.Add(Option(Error(Codes.OptionNotApplicable, "'limit' does not apply to a lookup with 'first', which takes the first child or null.", index, null), "limit", "withFirst"));

                limit = 1;
            }
            else
            {
                limit = lookup.Limit ?? options.Limits.MaxLookupLimit;

                if (limit < 1 || limit > options.Limits.MaxLookupLimit)
                    errors.Add(Limit(Codes.LookupLimitExceeded, $"A lookup returns at most {options.Limits.MaxLookupLimit} children per parent; '{limit}' is outside that.", index, null, "maxLookupLimit", options.Limits.MaxLookupLimit, limit));
            }

            var parentKeyStorage = parentPrefix.Length == 0 ? parentKey.Storage! : parentPrefix + "." + parentKey.Storage;

            stages.Add(new BoundStage.Lookup(child, childPath, alias, select, filter, limit, childScope, parentKeyStorage, childPath.Storage!,
                sortFields, first, lookup.On, index));

            // Under contract 2 every member of the child can be read under the alias, and the join loads
            // what is read (InferLoads); under contract 1 the alias holds the select and nothing else.
            var fetched = contract2 ? null : select.Select(path => path.Wire).ToList();

            shape = shape.WithRoot(alias, first
                ? new ShapeNode.Entity(child, alias) { Select = fetched, Join = alias }
                : new ShapeNode.Array(child, alias) { Select = fetched, Join = alias });
        }

        /// <summary>
        /// The parent a lookup's <c>on</c> names: an entity row of this host under an alias (a
        /// local resolve, or an unwound lookup). An array, element, scalar or group output is
        /// refused with <c>LOOKUP_ON_NOT_ENTITY</c>. A keyed or remote alias never gets here: a
        /// lookup on it continues at its owner (<see cref="BindContinued"/>).
        /// </summary>
        private bool BindLookupParent(string on, int index, out EntityDef parent, out string prefix)
        {
            parent = entity;
            prefix = "";

            if (on == Shape.ImplicitRoot || !shape.Roots.TryGetValue(on, out var node))
            {
                errors.Add(Error(Codes.UnknownPath, $"'{on}' is not an alias of the row here; 'on' names the alias of the parent row.", index, on));
                return false;
            }

            if (!shape.IsVisible(on))
            {
                errors.Add(Error(Codes.UnknownPath, $"'{on}' was removed by the projection.", index, on));
                return false;
            }

            switch (node)
            {
                case ShapeNode.Entity row:
                    parent = row.Def;
                    prefix = row.StoragePrefix;
                    return true;

                case ShapeNode.Poisoned:
                    errors.Add(Error(Shape.PoisonedCode, $"'{on}' failed to bind.", index, on));
                    return false;

                default:
                    var what = node switch
                    {
                        ShapeNode.Array => "the array of a lookup",
                        ShapeNode.Element => "an unwound element",
                        ShapeNode.Scalar => "a scalar",
                        ShapeNode.GroupOutput => "a group output",
                        _ => "not an entity row",
                    };

                    errors.Add(Error(Codes.LookupOnNotEntity,
                        $"'{on}' is {what}; a lookup's parent is one entity row: the entity itself, a resolved alias or an unwound lookup alias.", index, on));
                    return false;
            }
        }

        // ---- remote lookup ------------------------------------------------------

        /// <summary>
        /// The service that owns <paramref name="from"/> when a lookup of it is a remote lookup: a
        /// contract 2 request, an id <c>service.entity</c> or <c>service.entity#item</c> whose namespace
        /// is not one of this host's, and an owner this host can reach (<see cref="RequestContext.RemoteService"/>).
        /// Null otherwise, and the lookup is refused as naming no entity.
        /// </summary>
        private string? RemoteOwnerOf(string? from)
        {
            if (!contract2 || context.RemoteService is not { } reachable || string.IsNullOrWhiteSpace(from))
                return null;

            var entity = from.Trim().Split('#')[0];
            var service = entity.Split('.')[0];

            if (service.Length == 0 || !entity.Contains('.', StringComparison.Ordinal) || IsOwnNamespace(service))
                return null;

            return reachable(service) ? service : null;
        }

        /// <summary>Whether <paramref name="service"/> is the namespace of an entity of this host.</summary>
        private bool IsOwnNamespace(string service) =>
            binder.model.Entities.Keys.Any(id => id.StartsWith(service + ".", StringComparison.Ordinal));

        /// <summary>
        /// A lookup whose <c>from</c> is another service's entity. It runs as the keyed
        /// fetch runs a remote resolve, and is bound as one: the key of every page row is the parent's
        /// key, the target member is the child's <c>path</c>, and the owner answers grouped per key
        /// (<c>keyedBy</c>, at most one row more than the limit per key, ranked by the lookup's
        /// <c>sort</c>). What lies on the child — its path, select, filter, sort — is
        /// bound by the owner, as under a remote resolve's alias; the owner also checks that the path
        /// declares a reference to the parent entity (<c>keyedBy.references</c>). Checked here: the
        /// alias, <c>parentAs</c> only for an item child, the parent's key visible in the row, the
        /// limit under this host's cap, the filter's options and variables. Its alias is a remote
        /// alias, never filtered or sorted here; with <c>first</c> a later resolve or lookup under it
        /// continues at the same owner.
        /// </summary>
        private void BindRemoteLookup(LookupStage lookup, int index, EntityDef parent, string service)
        {
            var from = lookup.From!.Trim();
            var hash = from.IndexOf('#', StringComparison.Ordinal);
            var entityId = hash < 0 ? from : from[..hash];
            var item = hash < 0 ? null : from[(hash + 1)..];

            if (item is not null && (item.Length == 0 || item.Split('.').Any(segment => segment.Length == 0)))
            {
                errors.Add(Error(Codes.UnknownEntity, $"'{from}' names no item collection; an item child is written 'service.entity#collection'.", index, null));
                return;
            }

            if (!CheckAlias(lookup.As, index, out var alias))
                return;

            string? parentAs = null;

            if (lookup.ParentAs is not null)
            {
                if (item is null)
                {
                    errors.Add(Option(Error(Codes.OptionNotApplicable,
                        $"'parentAs' names the row that owns an element; '{from}' is an entity, which is its own row. Look up '{entityId}#<collection>' to join elements.", index, null), "parentAs", "notAnItem"));
                    return;
                }

                if (!CheckAlias(lookup.ParentAs, index, out var checkedParent))
                    return;

                if (checkedParent == alias)
                {
                    errors.Add(Error(Codes.AliasCollision, $"'parentAs' and 'as' are both '{alias}'; the owning row needs a name of its own.", index, checkedParent));
                    return;
                }

                parentAs = checkedParent;
            }

            var childPath = lookup.Path?.Trim() ?? "";

            if (childPath.Length == 0 || childPath.Split('.').Any(segment => segment.Length == 0))
            {
                errors.Add(Error(Codes.InvalidPath, $"'{lookup.Path}' is not a path; a lookup names the child's member that references '{parent.Id}'.", index, lookup.Path));
                return;
            }

            // The keys are the parent's keys: the owner's reference names the parent entity, whose
            // key it holds (a remote reference targets the key).
            if (parent.Key is not { Stored: true } key)
            {
                errors.Add(Error(Codes.LookupNotDeclared, $"'{parent.Id}' has no stored key; a lookup from another service joins on it.", index, lookup.Path));
                return;
            }

            var keyWire = lookup.On is null ? key.Wire : lookup.On + "." + key.Wire;

            if (lookup.On is not null && !shape.IsVisible(keyWire))
            {
                errors.Add(Error(Codes.UnknownPath, $"'{keyWire}' was removed by the projection; the lookup joins on it.", index, lookup.On));
                return;
            }

            if (Read(shape, keyWire, PathUsage.Match, ReadUse.LookupOn, index, Reading.Row) is not { Succeeded: true, Path: { Storage: not null } parentKey })
            {
                errors.Add(Error(Codes.UnknownPath, $"'{keyWire}' is not in the row here; the lookup joins on it.", index, lookup.On));
                return;
            }

            var first = lookup.First is true;
            int limit;

            if (first)
            {
                if (lookup.Limit is not null)
                    errors.Add(Option(Error(Codes.OptionNotApplicable, "'limit' does not apply to a lookup with 'first', which takes the first child or null.", index, null), "limit", "withFirst"));

                limit = 1;
            }
            else
            {
                limit = lookup.Limit ?? options.Limits.MaxLookupLimit;

                if (limit < 1 || limit > options.Limits.MaxLookupLimit)
                    errors.Add(Limit(Codes.LookupLimitExceeded, $"A lookup returns at most {options.Limits.MaxLookupLimit} children per parent; '{limit}' is outside that.", index, null, "maxLookupLimit", options.Limits.MaxLookupLimit, limit));
            }

            foreach (var entry in lookup.Sort ?? [])
                if (string.IsNullOrWhiteSpace(entry.Path))
                    errors.Add(Error(Codes.InvalidPath, "A sort entry of a lookup names a path of the child.", index, null));

            // What the wire form cannot carry to the owner is refused here, not dropped on the way;
            // every variable is bound here, since the owner never receives them.
            if (RefuseUnknownOptions(lookup.Filter?.Condition, index))
                return;

            var substitution = new List<QueryValidationError>();
            var filter = lookup.RawFilter is { } rawFilter ? coercer.SubstituteVariables(rawFilter, index, lookup.Path, substitution) : (JsonElement?)null;

            errors.AddRange(substitution);

            if (++lookups > options.Limits.MaxLookupStages)
                errors.Add(Limit(Codes.MaxLookupStagesExceeded, $"The pipeline has more than {options.Limits.MaxLookupStages} lookup stages.", index, null, "maxLookupStages", options.Limits.MaxLookupStages, lookups));

            if (errors.Count > 0 && errors.Any(error => error.Stage == index))
                return;

            var written = lookup.Select is { Count: > 0 } hint ? hint : null;
            var spec = new BoundRemoteLookup(from, entityId, item, childPath, parent.Id, lookup.On, lookup.Sort ?? [], first, limit, Rows: item is null,
                written, null, filter);
            var declared = new ReferenceTarget(entityId, childPath, item, IsRemote: true, FieldIsKey: false);
            var target = new BoundResolveTarget(declared, null, null, null, null, written, null, filter, null, null, null, []);
            var reference = new ReferenceDef { Targets = [declared], DeclaredBy = ReferenceSource.Declaration };
            var stage = new BoundStage.Resolve(parentKey, alias, entityId, childPath, IsRemote: true,
                null, null, null, null, null, written, filter,
                ResolveExecutor.Keyed, [new BoundResolveCase(reference, null, [target])], null, null, null, parentAs, null, ResolveOnMissing.Null, index, spec);

            stages.Add(stage);

            // The alias holds the owner's rows: a remote alias, projected, never filtered or sorted
            // here. Under 'first' it is one row, and a resolve or lookup under it continues at the
            // owner; an array of children is not continued (its per-child association would be lost).
            shape = shape.WithRoot(alias, new ShapeNode.Remote(entityId, parentKey, alias, SemiJoinable: false));
            anchors[alias] = new ContinuationAnchor(stage, null, Many: !first);

            if (parentAs is not null)
            {
                shape = shape.WithRoot(parentAs, new ShapeNode.Remote(entityId, parentKey, parentAs, SemiJoinable: false));
                anchors[parentAs] = new ContinuationAnchor(stage, null, Many: !first);
            }
        }

        private IReadOnlyList<ResolvedPath> BindSelect(IReadOnlyList<string>? select, EntityDef target, Shape targetShape, int index)
        {
            var paths = new List<ResolvedPath>();
            var wanted = select is { Count: > 0 }
                ? select
                : new[] { target.Key?.Wire, target.Display?.Wire }.Where(wire => wire is not null).Select(wire => wire!).ToList();

            foreach (var wire in wanted)
            {
                // not a read: the select hint (or the default) of the join's own target
                var resolution = targetShape.Resolve(wire, PathUsage.Select);

                if (!resolution.Succeeded)
                {
                    errors.Add(Unresolved(resolution, index, wire));
                    continue;
                }

                paths.Add(resolution.Path!);
            }

            if (target.Key is not null && paths.All(path => path.Wire != target.Key.Wire))
                // not a read: the key of the join's own target
                paths.Insert(0, targetShape.Resolve(target.Key.Wire, PathUsage.Select).Path!);

            return paths;
        }

        // ---- resolve -------------------------------------------------------------------------

        /// <summary>
        /// A resolve: the members, the collection guard, the declared cases with
        /// <c>target</c> and <c>parentAs</c>, the executor, and the shape of the alias. A resolve
        /// under a keyed or remote alias continues the chain at that alias's owner
        /// (<see cref="BindContinued"/>); a keyed or remote resolve's own alias and owning row
        /// become aliases later stages continue under.
        /// </summary>
        private async Task BindResolveAsync(ResolveStage resolve, int index)
        {
            // Step 1: the members. All but path, as, select and filter are contract 2; under contract 1 they are
            // members the stage does not have, as is any of them written with the wrong kind.
            var contract2Members = new (string Name, bool Written)[]
            {
                ("elements", resolve.Elements is not null), ("target", resolve.Target is not null), ("parentAs", resolve.ParentAs is not null),
                ("onMissing", resolve.OnMissing is not null), ("forTarget", resolve.ForTarget is not null), ("byTarget", resolve.ByTarget is not null),
                ("outcomeAs", resolve.OutcomeAs is not null),
            };
            IReadOnlyList<string> unknown = contract2
                ? resolve.Unknown
                : [.. resolve.Unknown, .. contract2Members.Where(member => member.Written).Select(member => member.Name), .. resolve.Malformed];

            if (unknown.Count > 0)
            {
                errors.Add(NoMember(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                    $"'{string.Join(", ", unknown)}' is not a member of resolve; a resolve carries {(contract2 ? ResolveMembers : "path, as, select, filter")}."
                    + Hint(contract2Members.Any(member => member.Written) || resolve.Malformed.Count > 0), index, null), "resolve", unknown, "unknown"));
                return;
            }

            if (resolve.Select is not null && RefuseSelect("resolve", ResolveMembers, resolve.As, index))
                return;

            if (resolve.Malformed.Count > 0)
            {
                foreach (var name in resolve.Malformed)
                    errors.Add(NoMember(Error(Codes.UnknownStageMember, name switch
                    {
                        "elements" => "A resolve's 'elements' is \"first\" or \"all\".",
                        "onMissing" => "A resolve's 'onMissing' is \"null\", \"report\" or \"refuse\".",
                        "byTarget" => "A resolve's 'byTarget' is an object with one member per target: its path, or { \"path\": …, \"elements\": \"first\" | \"all\" }.",
                        _ => $"A resolve's '{name}' is a string.",
                    }, index, null), "resolve", [name], "kind"));
                return;
            }

            // The request's default for a missing target: a resolve that names none takes it as
            // if it had written it, so an owner a continued resolve runs at is sent it on the stage itself.
            if (contract2 && resolve.OnMissing is null && request.MissingDefault is { } standing)
            {
                resolve = resolve with { OnMissing = standing };
                inheritedOnMissing.Add(index);
            }

            // A union join: one path per target of the alias it continues under, one alias.
            if (resolve.ByTarget is not null)
            {
                BindUnionJoin(resolve, index);
                return;
            }

            // Step 3: under a keyed or remote alias the stage is a continuation of it, run at its
            // owner; it is not a resolve of this host and does not count towards
            // MaxResolveStages.
            var path = resolve.Path ?? "";
            var head = path.Split('.')[0];

            if (anchors.TryGetValue(head, out var anchor))
            {
                BindContinued(new PipelineStage { Resolve = resolve, Keys = ["resolve"] }, index, path, anchor, resolve.ForTarget, [resolve.As, resolve.ParentAs], resolve.OutcomeAs);
                return;
            }

            if (++resolves > options.Limits.MaxResolveStages)
                errors.Add(Limit(Codes.MaxResolveStagesExceeded, $"The pipeline has more than {options.Limits.MaxResolveStages} resolve stages.", index, null, "maxResolveStages", options.Limits.MaxResolveStages, resolves));

            // forTarget picks one target of an alias a stage continues under; a resolve this host
            // runs continues nothing.
            if (resolve.ForTarget is not null)
            {
                errors.Add(Option(Error(Codes.OptionNotApplicable,
                    "'forTarget' applies to a stage continued under a remote alias with several targets; this resolve runs on this host.", index, null), "forTarget", "notContinued"));
                return;
            }

            if (!CheckAlias(resolve.As, index, out var alias))
                return;

            string? parentAs = null;

            if (resolve.ParentAs is not null)
            {
                if (!CheckAlias(resolve.ParentAs, index, out var checkedParent))
                    return;

                if (checkedParent == alias)
                {
                    errors.Add(Error(Codes.AliasCollision, $"'parentAs' and 'as' are both '{alias}'; the owning row needs a name of its own.", index, checkedParent));
                    return;
                }

                parentAs = checkedParent;
            }

            if (!CheckOutcomeAlias(resolve.OutcomeAs, [alias, parentAs], index, out var outcomeAs))
                return;

            var resolution = Read(shape, path, PathUsage.Match, ReadUse.ResolveKey, index, Reading.Row);

            if (!resolution.Succeeded)
            {
                errors.Add(Unresolved(resolution, index, resolve.Path));
                return;
            }

            var reference = resolution.Path!;

            // Step 4: the declared cases. A group's key over a member that declares a reference follows that reference.
            if (reference.References is not { } declaredCases)
            {
                errors.Add(Joinable(Error(Codes.ResolveNotDeclared, $"'{resolve.Path}' declares no reference; it is {Kinds.WithArticle(reference.Kind)}.", index, resolve.Path), reference.Entity, toward: null));
                return;
            }

            // Step 2: the collection guard. A path through a collection that is not unwound names
            // one key per element; without 'elements' the join would pick one silently.
            var elements = resolve.Elements switch { "first" => ResolveElements.First, "all" => ResolveElements.All, _ => (ResolveElements?)null };
            var crossed = CollectionsCrossed(path);
            string? collectionStorage = null;

            if (crossed.Count > 1)
            {
                errors.Add(Error(Codes.UnwindOrder,
                    $"'{resolve.Path}' crosses {crossed.Count} collections that are not unwound here ({string.Join(", ", crossed.Select(collection => $"'{collection.Wire}'"))}); unwind the outer ones first, so at most one is left for 'elements'.", index, resolve.Path));
                return;
            }

            if (crossed.Count == 1)
            {
                if (elements is null)
                {
                    errors.Add(Error(Codes.ResolveOnCollection,
                        $"'{resolve.Path}' lies under the collection '{crossed[0].Wire}', which is not unwound here; unwind it first, or set 'elements' to 'first' or 'all'." + Hint(true), index, resolve.Path));
                    return;
                }

                collectionStorage = crossed[0].Storage;
            }
            else if (elements is not null)
            {
                errors.Add(Option(Error(Codes.OptionNotApplicable,
                    $"'elements' applies to a path through a collection that is not unwound; '{resolve.Path}' holds one value per row.", index, resolve.Path), "elements", "oneValue"));
                return;
            }

            // 'target' narrows the cases to one target entity; it must be one of them.
            IReadOnlyList<ReferenceDef> selected = declaredCases;
            string? narrowedTo = null;

            if (resolve.Target is { } wanted)
            {
                var targets = declaredCases.SelectMany(declared => declared.Targets).Select(target => target.Entity).Distinct(StringComparer.Ordinal).ToList();

                if (!targets.Contains(wanted, StringComparer.Ordinal))
                {
                    errors.Add(Error(Codes.ResolveTargetNotDeclared,
                        $"'{wanted}' is not a target of '{resolve.Path}'; its targets are {string.Join(", ", targets.Select(target => $"'{target}'"))}.", index, resolve.Path));
                    return;
                }

                if (targets.Count > 1)
                {
                    selected = declaredCases
                        .Select(declared => declared with { Targets = declared.Targets.Where(target => target.Entity == wanted).ToList() })
                        .Where(declared => declared.Targets.Count > 0)
                        .ToList();
                    narrowedTo = wanted;
                }
            }

            // After a group the row holds the key and nothing beside it: a reference that picks its target
            // by another member of the row, or by the variant of the object that held it, has nothing to pick by.
            if (reference.Root is ShapeNode.GroupOutput && selected.Any(declared => declared.When is not null))
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.ResolveNotDeclared,
                    Message = $"'{resolve.Path}' is a group's key over a member whose reference picks its target by another member of the row, which the group's row does not hold; resolve it before the group.",
                    Stage = index,
                    Path = resolve.Path,
                    Params = contract2 ? new Dictionary<string, object?> { ["reason"] = "conditionalAfterGroup" } : null,
                });
                return;
            }

            // 'parentAs' is the owning row of an item target; an entity target is its own row.
            if (parentAs is not null && selected.SelectMany(declared => declared.Targets).FirstOrDefault(target => target.Item is null) is { } entityTarget)
            {
                errors.Add(Error(Codes.ResolveParentNotItem,
                    $"'parentAs' names the row that owns an item target; '{resolve.Path}' resolves to the entity '{entityTarget.Entity}' itself.", index, resolve.Path));
                return;
            }

            var onMissing = resolve.OnMissing switch
            {
                "null" => ResolveOnMissing.Null,
                "report" => ResolveOnMissing.Report,
                "refuse" => ResolveOnMissing.Refuse,
                _ => (ResolveOnMissing?)null,
            };
            // A strict request refuses a missing reference unless the stage says otherwise.
            var effectiveOnMissing = onMissing ?? (contract2 && request.IsStrict ? ResolveOnMissing.Refuse : ResolveOnMissing.Null);

            // The cases with their targets bound; every target is bound, whichever executor runs it.
            var cases = new List<BoundResolveCase>();
            var errorsBefore = errors.Count;

            filterBoundOnce = false;
            reportedFilterError = false;

            // The filter as every owner is sent it, local SelfOwner included: variables bound here,
            // since an owner never receives them. A local target's own binding of the
            // filter reports an unbound variable already; a remote-only one reports it below.
            var substitution = new List<QueryValidationError>();

            sentFilter = resolve.RawFilter is { } rawFilter ? coercer.SubstituteVariables(rawFilter, index, resolve.Path, substitution) : null;

            foreach (var declared in selected)
            {
                var when = BindCaseCondition(declared.When, reference, collectionStorage, index);
                var targets = new List<BoundResolveTarget>();

                foreach (var target in declared.Targets)
                    if (await BindResolveTargetAsync(target, resolve, parentAs is not null, index) is { } bound)
                        targets.Add(bound);

                cases.Add(new BoundResolveCase(declared, when, targets));
            }

            ReportUnboundSelect(resolve, cases, index);

            if (substitution.Count > 0 && !reportedFilterError && cases.Any(bound => bound.Targets.Any(target => target.IsRemote)))
                errors.AddRange(substitution);

            // An option the wire form cannot carry is lost on the way to a remote owner, which would
            // then compare without it; a local target's binding refuses it already.
            if (cases.Any(bound => bound.Targets.Any(target => target.IsRemote)) && !cases.Any(bound => bound.Targets.Any(target => !target.IsRemote)))
                RefuseUnknownOptions(resolve.Filter?.Condition, index);

            if (errors.Count > errorsBefore)
                return;

            // Step 5: the executor. The in-aggregate $lookup runs one unconditional case on one
            // local entity, without conversion. What strict or onMissing asks of it (a filter's
            // excluded record told from a missing one, a non-key target's second record) the
            // aggregate detects itself, so neither changes the executor or what binds.
            var first = cases[0].Targets[0];
            var inline = cases is [{ Declared: { When: null, KeyAs: KeyAs.None }, Targets: [{ IsRemote: false, Declared.Item: null }] }]
                && elements is null;
            var anyRemote = cases.Any(bound => bound.Targets.Any(target => target.IsRemote));

            var stage = new BoundStage.Resolve(reference, alias, first.Declared.Entity, first.Declared.Field, anyRemote,
                first.Entity, first.FieldStorage, first.Select, first.Filter, first.Scope,
                first.IsRemote ? resolve.Select : null, first.IsRemote ? sentFilter : null,
                inline ? ResolveExecutor.Inline : ResolveExecutor.Keyed, cases, elements, collectionStorage, narrowedTo, parentAs, onMissing, effectiveOnMissing, index)
            {
                OutcomeAs = outcomeAs,
                OnMissingInherited = inheritedOnMissing.Contains(index),
            };

            stages.Add(stage);

            // Step 6: the shape. An inline alias is a row of the aggregate; a keyed local one is
            // joined after the page and checked here; a remote one is the owner's.
            if (inline)
                shape = shape.WithRoot(alias, new ShapeNode.Entity(first.Entity!, alias) { Select = contract2 ? null : first.Select?.Select(path => path.Wire).ToList(), Join = alias });
            else if (anyRemote)
                shape = shape.WithRoot(alias, new ShapeNode.Remote(first.Declared.Entity, reference, alias, SemiJoinable: stage.IsPlain));
            else
                shape = shape.WithRoot(alias, new ShapeNode.Keyed(
                    cases.SelectMany(bound => bound.Targets).Select(target => new KeyedTarget(target.Entity!, target.Declared.Item is { } item ? target.Entity!.Path(item) : null)).Distinct().ToList(),
                    elements == ResolveElements.All, alias));

            if (parentAs is not null)
                shape = shape.WithRoot(parentAs, anyRemote
                    ? new ShapeNode.Remote(first.Declared.Entity, reference, parentAs, SemiJoinable: false)
                    : new ShapeNode.Keyed(
                        cases.SelectMany(bound => bound.Targets).Select(target => new KeyedTarget(target.Entity!, null)).Distinct().ToList(),
                        elements == ResolveElements.All, parentAs));

            // The outcome under its name follows the join: written by the aggregate for an inline
            // resolve, after the page for every other.
            if (outcomeAs is not null)
                shape = shape.WithRoot(outcomeAs, new ShapeNode.Outcome(alias, inline, outcomeAs));

            // A keyed or remote alias and its owning row: later resolves and lookups under them
            // continue at their owner.
            if (!inline)
            {
                anchors[alias] = new ContinuationAnchor(stage, null, elements == ResolveElements.All);

                if (parentAs is not null)
                    anchors[parentAs] = new ContinuationAnchor(stage, null, elements == ResolveElements.All);
            }
        }

        /// <summary>
        /// A resolve or lookup under a keyed or remote alias: bound
        /// as a <see cref="ContinuedStage"/> of the keyed stage whose owner query carries it, which
        /// the owner binds with its own model. Checked here: contract 2, the alias still in the row,
        /// not under an <c>elements: "all"</c> alias (the per-element association would be lost),
        /// <c>forTarget</c> naming a target of the keyed stage's alias (under an alias a continued
        /// stage added, it names a target of that alias, which only the owner knows: it travels there
        /// and the owner checks it; the target the stage goes to here is the one the alias inherited),
        /// at most <c>MaxContinuedStages</c> per keyed stage, the new aliases free at the origin — so no continued alias collides with an origin alias — and every
        /// variable bound, since the owner never receives <c>variables</c>.
        /// </summary>
        private void BindContinued(PipelineStage raw, int index, string root, ContinuationAnchor anchor, string? forTarget, IReadOnlyList<string?> aliases, string? outcome = null)
        {
            var kind = raw.Kind!;
            var head = root.Split('.')[0];

            if (!contract2)
            {
                errors.Add(Error(Codes.NotContinuable,
                    $"'{kind}' cannot run on '{head}', which comes from its owner after the page; a chain continues at the owner under contract 2 only." + Contract1Hint, index, root));
                return;
            }

            if (!shape.IsVisible(root))
            {
                errors.Add(Error(Codes.UnknownPath, $"'{root}' was removed by the projection.", index, root));
                return;
            }

            if (anchor.Many && anchor.Stage.RemoteLookup is not null)
            {
                errors.Add(Error(Codes.NotContinuable,
                    $"'{kind}' cannot run on '{head}', which holds every child the lookup found for a row; a continued stage would lose which child it belongs to. Look up with 'first' to continue from one child.", index, root));
                return;
            }

            if (anchor.Many)
            {
                errors.Add(Error(Codes.NotContinuable,
                    $"'{kind}' cannot run on '{head}', which holds every resolved target of a row ('elements: \"all\"'); a continued stage would lose which element it belongs to. Resolve with 'elements: \"first\"' or unwind the collection first.", index, root));
                return;
            }

            var targets = TargetsOf(anchor.Stage);
            var effective = anchor.Targets;

            // Under an alias a continued stage added, forTarget names a target of that alias, whose join
            // the owner binds with its own model: the stage carries it to the owner, which
            // checks it and applies it to its own keyed stage, and an owner's refusal maps back here.
            // This host sends the stage wherever the alias it continues under goes.
            if (forTarget is not null && !anchor.Nested)
            {
                if (!targets.Contains(forTarget, StringComparer.Ordinal))
                {
                    errors.Add(Option(Error(Codes.OptionNotApplicable,
                        $"'forTarget' names '{forTarget}', which is not a target of '{anchor.Stage.As}'; its targets are {string.Join(", ", targets.Select(target => $"'{target}'"))}.", index, root), "forTarget", "notATarget"));
                    return;
                }

                effective = [forTarget];
            }

            var count = continuedPerAnchor.GetValueOrDefault(anchor.Stage.As) + 1;

            if (count > options.Limits.MaxContinuedStages)
            {
                errors.Add(Limit(Codes.MaxContinuedStagesExceeded,
                    $"More than {options.Limits.MaxContinuedStages} stages continue under '{anchor.Stage.As}'.", index, root, "maxContinuedStages", options.Limits.MaxContinuedStages, count));
                return;
            }

            var added = new List<string>();

            foreach (var alias in aliases)
            {
                if (alias is null && added.Count > 0)
                    continue;

                if (!CheckAlias(alias, index, out var checkedAlias))
                    return;

                if (added.Contains(checkedAlias, StringComparer.Ordinal))
                {
                    errors.Add(Error(Codes.AliasCollision, $"'parentAs' and 'as' are both '{checkedAlias}'; the owning row needs a name of its own.", index, checkedAlias));
                    return;
                }

                added.Add(checkedAlias);
            }

            // The outcome under its name: the owner writes it on the rows it answers, and it is lifted with the alias.
            if (!CheckOutcomeAlias(outcome, added, index, out var outcomeAs))
                return;

            // What the wire form cannot carry to the owner is refused here, not dropped on the way.
            if (RefuseUnknownOptions(raw.Resolve?.Filter?.Condition ?? raw.Lookup?.Filter?.Condition, index))
                return;

            // The stage as the owner is sent it: every variable bound here.
            var substitution = new List<QueryValidationError>();
            var written = JsonSerializer.SerializeToElement(raw, OxQLJson.Wire);
            var sent = coercer.SubstituteVariables(written, index, root, substitution);

            if (substitution.Count > 0)
            {
                errors.AddRange(substitution);
                return;
            }

            var stage = OperandCoercer.HoldsVariable(written) ? JsonSerializer.Deserialize<PipelineStage>(sent.GetRawText(), OxQLJson.Wire)! : raw;

            continuedPerAnchor[anchor.Stage.As] = count;
            stages.Add(new ContinuedStage(anchor.Stage.As, stage, index, effective is [var only] ? only : null, outcomeAs is null ? added : [.. added, outcomeAs]) { Targets = effective is { Count: > 1 } ? effective : null, OnMissingInherited = inheritedOnMissing.Contains(index) });

            // What this host can see of the stage's reads: the path it roots at. Its owner binds it and
            // answers the rest (a case member, the parent's key), which explain takes from the owner.
            reads.Add(new PathRead(index, root, kind == "resolve" ? ReadUse.ResolveKey : ReadUse.LookupOn, head, root.Length > head.Length ? root[(head.Length + 1)..] : ""));

            // The added aliases are the owner's rows under the origin row: projected, never
            // filtered or sorted here, and further stages under them continue at the same owner.
            // Their entity is the lookup's child or the resolve's target; a resolve without a target
            // follows a reference of the owner's model, which explain asks the owner for.
            var many = raw.Resolve?.Elements == "all";
            var known = raw.Lookup?.From ?? raw.Resolve?.Target;

            foreach (var alias in added)
            {
                shape = shape.WithRoot(alias, new ShapeNode.Remote(known ?? anchor.Stage.TargetEntity, anchor.Stage.Reference, alias, SemiJoinable: false, TargetOpen: known is null));
                anchors[alias] = new ContinuationAnchor(anchor.Stage, effective, many, Nested: true);
            }

            if (outcomeAs is not null)
                shape = shape.WithRoot(outcomeAs, new ShapeNode.Outcome(added[0], InAggregate: false, outcomeAs));
        }

        /// <summary>
        /// The name a stage gives its join's outcome (<c>outcomeAs</c>): an alias like any other, free in
        /// the row and not one of the stage's own (<paramref name="own"/>). True with
        /// <paramref name="outcomeAs"/> null when the stage names none.
        /// </summary>
        private bool CheckOutcomeAlias(string? written, IEnumerable<string?> own, int index, out string? outcomeAs)
        {
            outcomeAs = null;

            if (written is null)
                return true;

            if (!CheckAlias(written, index, out var checkedAlias))
                return false;

            if (own.Contains(checkedAlias, StringComparer.Ordinal))
            {
                errors.Add(Error(Codes.AliasCollision, $"'outcomeAs' is '{checkedAlias}', which this stage already names; the outcome needs a name of its own.", index, checkedAlias));
                return false;
            }

            outcomeAs = checkedAlias;
            return true;
        }

        /// <summary>
        /// A union join: <c>resolve { as, byTarget: { target: path | { path, elements } } }</c>,
        /// one stage continued under a keyed or remote alias with several targets, one branch per target,
        /// all filling the one alias. It binds as one <see cref="ContinuedStage"/>: the owner of each
        /// target named runs its branch as an ordinary resolve (<see cref="Continuation.BranchOf"/>), and
        /// a target without a branch is not sent the stage, as with a foreign <c>forTarget</c>.
        /// <para>
        /// Checked here: no <c>path</c>, <c>elements</c>, <c>forTarget</c> or <c>parentAs</c> beside it
        /// (the first two are the branch's; the stage is every target's; a branch's owning row would be
        /// one alias of several shapes); at least two branches, each target once; every branch path under
        /// an alias of one keyed stage (its alias, its owning row, or an alias continued under them that
        /// exists on the branch's target); every target named a target of that stage; one cardinality
        /// (every branch one record, or every branch <c>elements: "all"</c>). The paths themselves are
        /// the owners' to bind.
        /// </para>
        /// <para>
        /// When the branches root at one alias a continued stage added and name targets that are not the
        /// keyed stage's, they name targets of that alias, which only its owner knows (the nested rule of
        /// <c>forTarget</c>): the stage travels as written and the owner splits it.
        /// </para>
        /// </summary>
        private void BindUnionJoin(ResolveStage resolve, int index)
        {
            var branches = resolve.ByTarget!;

            QueryValidationError NotApplicable(string message, string reason, string? path = null, Dictionary<string, object?>? more = null)
            {
                var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["option"] = "byTarget", ["reason"] = reason };

                foreach (var (name, value) in more ?? [])
                    parameters[name] = value;

                return new QueryValidationError { Code = Codes.OptionNotApplicable, Message = message, Stage = index, Path = path, Params = parameters };
            }

            foreach (var (name, given) in new[] { ("path", resolve.Path is not null), ("elements", resolve.Elements is not null), ("forTarget", resolve.ForTarget is not null), ("parentAs", resolve.ParentAs is not null) })
            {
                if (!given)
                    continue;

                errors.Add(NotApplicable(name switch
                {
                    "path" => "'byTarget' takes the place of 'path': a resolve names one path, or one path per target.",
                    "elements" => "'elements' is each branch's own under 'byTarget': write it beside the branch's path ({ \"path\": …, \"elements\": … }).",
                    "forTarget" => "'forTarget' picks one target; 'byTarget' names a path for each of several. Write one of them.",
                    _ => "'parentAs' does not apply to a union join: the owning rows of its branches have no one shape.",
                }, "with" + char.ToUpperInvariant(name[0]) + name[1..], null, new() { ["member"] = name }));
                return;
            }

            if (branches.GroupBy(branch => branch.Target, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
            {
                errors.Add(NotApplicable($"'byTarget' names '{twice.Key}' twice; a target has one branch.", "duplicateTarget", null, new() { ["target"] = twice.Key }));
                return;
            }

            // One branch is the plain form: a path for one target.
            if (branches.Count < 2)
            {
                errors.Add(NotApplicable(branches is [var single]
                    ? $"'byTarget' names one target; a resolve for one target is written with 'path' and 'forTarget': {{ \"path\": \"{single.Path}\", \"forTarget\": \"{single.Target}\", \"as\": … }}."
                    : "'byTarget' names no target; it carries one path per target, for at least two.", "singleBranch", branches is [var one] ? one.Path : null,
                    new() { ["target"] = branches is [var named] ? named.Target : null, ["form"] = "forTarget" }));
                return;
            }

            // Every branch continues under an alias of one keyed stage.
            var roots = new List<(ResolveBranch Branch, string Head, ContinuationAnchor Anchor)>();

            foreach (var branch in branches)
            {
                var path = branch.Path ?? "";
                var head = path.Split('.')[0];

                if (path.Length == 0 || path.Split('.').Any(segment => segment.Length == 0))
                {
                    errors.Add(Error(Codes.InvalidPath, $"'{branch.Path}' is not a path; the branch of '{branch.Target}' names the member carrying the reference.", index, branch.Path));
                    return;
                }

                if (!anchors.TryGetValue(head, out var under))
                {
                    // not a read: why a branch path is not one a union join continues from
                    var here = shape.Resolve(path, PathUsage.Project);

                    errors.Add(here.Succeeded || shape.Roots.ContainsKey(head)
                        ? NotApplicable($"'byTarget' applies to a stage continued under an alias with several targets; '{path}' is a path of this host's row. Write it with 'path'.", "notContinued", path, new() { ["target"] = branch.Target })
                        : Unresolved(here, index, path));
                    return;
                }

                if (!shape.IsVisible(path))
                {
                    errors.Add(Error(Codes.UnknownPath, $"'{path}' was removed by the projection.", index, path));
                    return;
                }

                if (under.Many)
                {
                    errors.Add(Error(Codes.NotContinuable, under.Stage.RemoteLookup is not null
                        ? $"'resolve' cannot run on '{head}', which holds every child the lookup found for a row; a continued stage would lose which child it belongs to. Look up with 'first' to continue from one child."
                        : $"'resolve' cannot run on '{head}', which holds every resolved target of a row ('elements: \"all\"'); a continued stage would lose which element it belongs to. Resolve with 'elements: \"first\"' or unwind the collection first.", index, path));
                    return;
                }

                roots.Add((branch, head, under));
            }

            var anchor = roots[0].Anchor;

            if (roots.FirstOrDefault(root => !ReferenceEquals(root.Anchor.Stage, anchor.Stage)) is { Branch: not null } other)
            {
                errors.Add(NotApplicable(
                    $"The branches of 'byTarget' continue under one alias with several targets; '{roots[0].Branch.Path}' lies under '{anchor.Stage.As}' and '{other.Branch.Path}' under '{other.Anchor.Stage.As}'.",
                    "anchors", other.Branch.Path, new() { ["target"] = other.Branch.Target, ["aliases"] = new List<string> { anchor.Stage.As, other.Anchor.Stage.As } }));
                return;
            }

            var targets = TargetsOf(anchor.Stage);
            var direct = branches.All(branch => targets.Contains(branch.Target, StringComparer.Ordinal));

            // Under one alias a continued stage added, the targets are that alias's, which its owner knows.
            var nested = !direct && roots.All(root => root.Head == roots[0].Head && root.Anchor.Nested);

            if (!direct && !nested)
            {
                var unknown = branches.First(branch => !targets.Contains(branch.Target, StringComparer.Ordinal));

                errors.Add(NotApplicable(
                    $"'byTarget' names '{unknown.Target}', which is not a target of '{anchor.Stage.As}'; its targets are {string.Join(", ", targets.Select(target => $"'{target}'"))}.",
                    "notATarget", unknown.Path, new() { ["target"] = unknown.Target, ["alias"] = anchor.Stage.As, ["targets"] = targets }));
                return;
            }

            // A branch roots at an alias that exists on its target's rows: one continued for another target does not.
            if (direct && roots.FirstOrDefault(root => root.Anchor.Targets is { } exists && !exists.Contains(root.Branch.Target, StringComparer.Ordinal)) is { Branch: not null } foreign)
            {
                errors.Add(NotApplicable(
                    $"'{foreign.Branch.Path}' is the branch of '{foreign.Branch.Target}', but '{foreign.Head}' belongs to the rows of {string.Join(", ", foreign.Anchor.Targets!.Select(target => $"'{target}'"))}.",
                    "otherBranch", foreign.Branch.Path, new() { ["target"] = foreign.Branch.Target, ["alias"] = foreign.Head, ["targets"] = foreign.Anchor.Targets }));
                return;
            }

            // One alias, one shape: every branch one record, or every branch an array.
            var all = branches[0].Elements == "all";

            if (branches.FirstOrDefault(branch => (branch.Elements == "all") != all) is { } mixed)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnionCardinalityMismatch,
                    Message = $"The branch of '{mixed.Target}' resolves {(all ? "one record" : "every element ('elements: \"all\"')")} while the branch of '{branches[0].Target}' resolves {(all ? "every element ('elements: \"all\"')" : "one record")}; "
                        + $"'{resolve.As}' holds one record per row or an array, not both. Change the branch of '{mixed.Target}' to {(all ? "'elements: \"all\"'" : "'elements: \"first\"' (or a path that crosses no collection)")}.",
                    Stage = index,
                    Path = mixed.Path,
                    Params = new Dictionary<string, object?>
                    {
                        ["alias"] = resolve.As,
                        ["branch"] = mixed.Target,
                        ["elements"] = mixed.Elements,
                        ["expected"] = all ? "all" : "one",
                    },
                });
                return;
            }

            // The stage counts once under its keyed stage, however many branches it has.
            var count = continuedPerAnchor.GetValueOrDefault(anchor.Stage.As) + 1;

            if (count > options.Limits.MaxContinuedStages)
            {
                errors.Add(Limit(Codes.MaxContinuedStagesExceeded,
                    $"More than {options.Limits.MaxContinuedStages} stages continue under '{anchor.Stage.As}'.", index, roots[0].Branch.Path, "maxContinuedStages", options.Limits.MaxContinuedStages, count));
                return;
            }

            if (!CheckAlias(resolve.As, index, out var alias))
                return;

            if (!CheckOutcomeAlias(resolve.OutcomeAs, [alias], index, out var outcomeAs))
                return;

            // What the wire form cannot carry to the owner is refused here, not dropped on the way.
            if (RefuseUnknownOptions(resolve.Filter?.Condition, index))
                return;

            // The stage as the owners are sent it: every variable bound here.
            var raw = new PipelineStage { Resolve = resolve, Keys = ["resolve"] };
            var substitution = new List<QueryValidationError>();
            var written = JsonSerializer.SerializeToElement(raw, OxQLJson.Wire);
            var sent = coercer.SubstituteVariables(written, index, roots[0].Branch.Path, substitution);

            if (substitution.Count > 0)
            {
                errors.AddRange(substitution);
                return;
            }

            var stage = OperandCoercer.HoldsVariable(written) ? JsonSerializer.Deserialize<PipelineStage>(sent.GetRawText(), OxQLJson.Wire)! : raw;

            // The rows the alias exists on: those of the targets it has a branch for; under a nested
            // alias, wherever that alias exists.
            var applies = direct ? branches.Select(branch => branch.Target).ToList() : anchor.Targets;

            continuedPerAnchor[anchor.Stage.As] = count;
            stages.Add(new ContinuedStage(anchor.Stage.As, stage, index, applies is [var only] ? only : null, outcomeAs is null ? [alias] : [alias, outcomeAs])
            {
                Targets = applies is { Count: > 1 } ? applies : null,
                Branches = direct ? stage.Resolve!.ByTarget : null,
                OnMissingInherited = inheritedOnMissing.Contains(index),
            });

            // What this host can see of the stage's reads: the path each branch roots at.
            foreach (var (branch, head, _) in roots)
                reads.Add(new PathRead(index, branch.Path!, ReadUse.ResolveKey, head, branch.Path!.Length > head.Length ? branch.Path[(head.Length + 1)..] : ""));

            // The alias holds the owners' rows: projected, never filtered or sorted here; what each
            // branch reaches is its owner's to say, unless the stage names the one target.
            shape = shape.WithRoot(alias, new ShapeNode.Remote(resolve.Target ?? anchor.Stage.TargetEntity, anchor.Stage.Reference, alias, SemiJoinable: false, TargetOpen: resolve.Target is null));
            anchors[alias] = new ContinuationAnchor(anchor.Stage, applies, all, Nested: true);

            if (outcomeAs is not null)
                shape = shape.WithRoot(outcomeAs, new ShapeNode.Outcome(alias, InAggregate: false, outcomeAs));
        }

        /// <summary>
        /// Refuses every option of a condition the engine does not know, or wrote with a value that is
        /// not a boolean, at <paramref name="index"/>: the wire form carries only the
        /// options it knows, so an owner would otherwise compare without what the caller asked for.
        /// True when something was refused.
        /// </summary>
        private bool RefuseUnknownOptions(FilterCondition? condition, int index)
        {
            var refused = false;

            void Walk(FilterCondition? node)
            {
                if (node is null)
                    return;

                if (node.Options?.Unknown is { Count: > 0 } unknown)
                {
                    errors.Add(Option(Error(Codes.OptionNotApplicable, $"'{string.Join(", ", unknown)}' is not an option; the option is caseSensitive, written true or false.", index, node.Path), unknown[0], "unknown"));
                    refused = true;
                }

                foreach (var inner in (node.And ?? []).Concat(node.Or ?? []))
                    Walk(inner);

                Walk(node.Not);
                Walk(node.Any);
            }

            Walk(condition);

            return refused;
        }

        /// <summary>Whether every target a stage continued under the anchor reaches (after <c>forTarget</c>) is an item target.</summary>
        private static bool ItemTargetsOnly(ContinuationAnchor anchor, string? forTarget)
        {
            IReadOnlyList<string>? effective = forTarget is null ? anchor.Targets : [forTarget];
            var targets = (anchor.Stage.Cases ?? []).SelectMany(bound => bound.Targets).Where(target => effective is null || effective.Contains(target.Declared.Entity, StringComparer.Ordinal)).ToList();

            return targets.Count > 0 && targets.All(target => target.Declared.Item is not null);
        }

        /// <summary>The target entities of a keyed stage's selected cases, in declaration order.</summary>
        private static List<string> TargetsOf(BoundStage.Resolve stage) =>
            (stage.Cases ?? []).SelectMany(bound => bound.Targets).Select(target => target.Declared.Entity).DefaultIfEmpty(stage.TargetEntity).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>
        /// Whether a path lies under an alias whose rows come from an owner after the page, where
        /// only a resolve or a lookup continues: an unwind or a group there is
        /// <c>NOT_CONTINUABLE</c>. True when it was refused.
        /// </summary>
        private bool RefusedUnderAnchor(string? path, string kind, int index)
        {
            var head = path?.Split('.')[0];

            if (head is null || !anchors.ContainsKey(head))
                return false;

            errors.Add(Error(Codes.NotContinuable,
                $"'{kind}' cannot run on '{head}', which comes from its owner after the page: only resolve and lookup continue a chain there. {(kind == "values" ? "The values of such a member are its owner's to list." : "Aggregate chain data in the report.")}", index, path));
            return true;
        }

        /// <summary>
        /// The collections a path crosses that are not unwound here, outermost first: a lookup
        /// array it starts under, an array member on the way, or the member itself when it is one.
        /// </summary>
        private List<ResolvedPath> CollectionsCrossed(string wire)
        {
            var crossed = new List<ResolvedPath>();
            var segments = wire.Split('.');

            for (var length = 1; length <= segments.Length; length++)
            {
                var prefix = string.Join('.', segments.Take(length));

                // not a read: a probe of which prefixes of a path are collections
                if (shape.Resolve(prefix, PathUsage.Project) is { Succeeded: true, Path: { Kind: Kind.Array } collection })
                    crossed.Add(collection);
            }

            return crossed;
        }

        /// <summary>
        /// What selects a case in a row: the sibling of the reference member, or the variant of
        /// the object holding it, with the stored values the declaration names. The model build
        /// checked both, so a failure here is the model's, reported like a path the model lacks.
        /// </summary>
        private BoundCaseCondition? BindCaseCondition(ReferenceCondition? condition, ResolvedPath reference, string? collectionStorage, int index)
        {
            if (condition is null)
                return null;

            var dot = reference.Wire.LastIndexOf('.');
            var holder = dot < 0 ? "" : reference.Wire[..dot];

            switch (condition)
            {
                case ReferenceCondition.PathEquals equals:
                {
                    var siblingWire = holder.Length == 0 ? equals.Path : holder + "." + equals.Path;

                    // The one read a caller cannot see in the query text: the join that holds the
                    // reference loads the member that picks its target with it.
                    var sibling = Read(shape, siblingWire, PathUsage.Project, ReadUse.CaseCondition, index, Reading.Row);

                    if (!sibling.Succeeded && !shape.IsVisible(siblingWire))
                    {
                        errors.Add(Error(Codes.UnknownPath, $"The reference on '{reference.Wire}' picks its target by '{siblingWire}': {sibling.Message}", index, siblingWire));
                        return null;
                    }

                    if (!sibling.Succeeded || sibling.Path!.Storage is null)
                    {
                        errors.Add(Error(Codes.ResolveNotDeclared, $"The reference on '{reference.Wire}' tests '{siblingWire}', which is not stored here.", index, reference.Wire));
                        return null;
                    }

                    var operand = coercer.Coerce(JsonSerializer.SerializeToElement(equals.Values), sibling.Path, "in", index, errors);

                    return operand is null ? null : new BoundCaseCondition(sibling.Path, ElementRelative(sibling.Path.Storage, collectionStorage), ValuesOf(operand), IsVariant: false);
                }

                case ReferenceCondition.Variant variant:
                {
                    // The object holding the member: a member path, or the entity row itself.
                    // not a read: resolved here, recorded below as a read of the object's variant only
                    var holding = holder.Length == 0 ? null : shape.Resolve(holder, PathUsage.Project);
                    var type = holding is { Succeeded: true } ? OperandCoercer.VariantHolder(holding.Path!) : holder.Length == 0 ? reference.Entity?.Root : null;

                    // The discriminator travels with any member a join loads below the object, and the
                    // reference is one, so this read loads nothing by itself.
                    if (holding is { Succeeded: true })
                        Record(index, holder, ReadUse.CaseCondition, holder, holding.Path!, typeOnly: true);

                    if (type?.DiscriminatorElement is not { } element)
                    {
                        errors.Add(Error(Codes.ResolveNotDeclared, $"The reference on '{reference.Wire}' tests the variant of an object whose variants are not known here.", index, reference.Wire));
                        return null;
                    }

                    var holderStorage = holding?.Path?.Storage;
                    var storage = string.IsNullOrEmpty(holderStorage) ? element : holderStorage + "." + element;
                    var probe = holding?.Path ?? reference;
                    var operand = coercer.CoerceVariants(JsonSerializer.SerializeToElement(variant.Names), probe, type, index, errors);

                    return operand is null ? null : new BoundCaseCondition(probe, ElementRelative(storage, collectionStorage), ValuesOf(operand), IsVariant: true);
                }

                // No condition is an unconditional case, so a kind of condition this binder does not know is never read as none.
                default:
                    throw new NotSupportedException($"The reference condition {condition.GetType().Name} on '{reference.Wire}' is of a kind the binder does not know.");
            }
        }

        /// <summary>
        /// A case condition's storage as the keyed fetch reads it: under <c>elements</c> relative to
        /// one element of the collection crossed, absolute in the row otherwise.
        /// </summary>
        private static string ElementRelative(string storage, string? collectionStorage) =>
            collectionStorage is not null && storage.StartsWith(collectionStorage + ".", StringComparison.Ordinal)
                ? storage[(collectionStorage.Length + 1)..]
                : storage;

        private static IReadOnlyList<BsonValue> ValuesOf(BoundOperand operand) => operand switch
        {
            BoundOperand.Set set => set.Values,
            BoundOperand.Tolerant tolerant => tolerant.Alternatives,
            BoundOperand.Single single => [single.Value],
            _ => [BsonNull.Value],
        };

        /// <summary>
        /// One target of a case. A remote target carries what the owner binds, as written. A local
        /// one is bound against its entity, or against the element of its item collection: the
        /// matched field (stored), the organisation scope, the select (paths it lacks are dropped
        /// for it), the filter, and the owning row's select when <c>parentAs</c> asks for it.
        /// Null with errors added when the target cannot be joined at all.
        /// </summary>
        private async Task<BoundResolveTarget?> BindResolveTargetAsync(ReferenceTarget declared, ResolveStage resolve, bool withParent, int index)
        {
            if (declared.IsRemote || !binder.model.Entities.TryGetValue(declared.Entity, out var target))
            {
                var remote = declared.IsRemote ? declared : declared with { IsRemote = true };

                return new BoundResolveTarget(remote, null, null, null, null, resolve.Select, null, sentFilter, null, null, null, []);
            }

            await LoadAddonsAsync(target);

            var entityShape = Shape.ForEntity(target, addons).Deriving(contract2);
            var at = entityShape;
            string? itemStorage = null;

            if (declared.Item is not null)
            {
                // not a read: the item collection of the join's own target
                var collection = entityShape.Resolve(declared.Item, PathUsage.Unwind);

                if (!collection.Succeeded || collection.Path!.Storage is null)
                {
                    errors.Add(Error(Codes.ResolveNotDeclared, $"The reference targets the items '{declared.Item}' of '{target.Id}', which are not stored.", index, resolve.Path));
                    return null;
                }

                itemStorage = collection.Path.Storage;
                at = entityShape.ForElement(collection.Path!);
            }

            // not a read: the matched member of the join's own target
            var field = at.Resolve(declared.Field, PathUsage.Match);

            if (!field.Succeeded || field.Path!.Storage is null)
            {
                errors.Add(Error(Codes.ResolveNotDeclared, $"The reference targets '{declared}#{declared.Field}', which is not stored.", index, resolve.Path));
                return null;
            }

            var scope = ScopeOf(target, context.Organisation!.Value);

            if (scope is null)
            {
                errors.Add(Error(Codes.AccessDenied, $"'{target.Id}' has no organisation member; it cannot be resolved.", index, null));
                return null;
            }

            // An entity's default select is its key and display; an item's is its matched field.
            var wanted = resolve.Select is { Count: > 0 } written
                ? written
                : declared.Item is null
                    ? new[] { target.Key?.Wire, target.Display?.Wire }.Where(wire => wire is not null).Select(wire => wire!).ToList()
                    : [declared.Field];
            var select = new List<ResolvedPath>();
            var dropped = new List<string>();

            foreach (var wire in wanted)
            {
                // not a read: the select hint (or the default) of the join's own target
                if (at.Resolve(wire, PathUsage.Select) is { Succeeded: true } kept)
                    select.Add(kept.Path!);
                else
                    dropped.Add(wire);
            }

            var keyWire = declared.Item is null ? target.Key?.Wire : declared.Field;

            // not a read: the key of the join's own target
            if (keyWire is not null && select.All(kept => kept.Wire != keyWire) && at.Resolve(keyWire, PathUsage.Select) is { Succeeded: true } key)
                select.Insert(0, key.Path!);

            var filter = resolve.Filter?.Condition is null ? null : BindTargetFilter(resolve.Filter.Condition, at, index);

            // The owning row shows its key and display until the loads are inferred: the paths the
            // projection names under 'parentAs' then replace them (InferLoads).
            var parentSelect = withParent && declared.Item is not null ? BindSelect(null, target, entityShape, index) : null;

            // The filter also travels: the keyed fetch asks this host's own SelfOwner with an ordinary
            // owner query, which binds it again.
            return new BoundResolveTarget(declared, target, field.Path.Storage, itemStorage, select, null, filter, sentFilter, scope, parentSelect,
                null, dropped, []);
        }

        /// <summary>
        /// The filter on one local target. It binds once per target; only the first binding counts
        /// towards the request's conditions and diagnostics, and only the first failure is
        /// reported, so a union whose targets share the filtered member reports a mistake once.
        /// </summary>
        private BoundCondition? BindTargetFilter(FilterCondition condition, Shape at, int index)
        {
            if (!filterBoundOnce)
            {
                var before = errors.Count;
                var once = BindCondition(condition, at, index, reading: null);

                filterBoundOnce = true;
                reportedFilterError = errors.Count > before;

                return once;
            }

            var (conditionsBefore, diagnosticsBefore, exactBefore) = (conditions, diagnostics.Count, exactTexts.Count);
            var errorsBefore = errors.Count;
            var bound = BindCondition(condition, at, index, reading: null);

            conditions = conditionsBefore;
            diagnostics.RemoveRange(diagnosticsBefore, diagnostics.Count - diagnosticsBefore);
            exactTexts.RemoveRange(exactBefore, exactTexts.Count - exactBefore);

            // The same mistake on another target of the union is the one already reported; a
            // different one is this target's own and stays.
            if (errors.Count > errorsBefore && reportedFilterError)
                for (var position = errors.Count - 1; position >= errorsBefore; position--)
                    if (errors.Take(errorsBefore).Any(reported => reported.Code == errors[position].Code && reported.Path == errors[position].Path))
                        errors.RemoveAt(position);

            reportedFilterError |= errors.Count > errorsBefore;

            return bound;
        }

        private bool filterBoundOnce, reportedFilterError;

        /// <summary>The resolve filter being bound as it is sent to owners: its variables substituted.</summary>
        private JsonElement? sentFilter;

        /// <summary>
        /// A select path no target of the resolve has is refused, with the reason its first local
        /// target gives; a path some target has is only dropped for the others. A remote target
        /// has every path as far as this host knows: its owner binds them.
        /// </summary>
        private void ReportUnboundSelect(ResolveStage resolve, IReadOnlyList<BoundResolveCase> cases, int index)
        {
            var targets = cases.SelectMany(bound => bound.Targets).ToList();

            if (targets.Count == 0 || targets.Any(target => target.IsRemote))
                return;

            foreach (var wire in targets[0].DroppedSelect)
            {
                if (!targets.All(target => target.DroppedSelect.Contains(wire, StringComparer.Ordinal)))
                    continue;

                var first = targets[0];
                var at = Shape.ForEntity(first.Entity!, addons).Deriving(contract2);

                // not a read: the item collection of the join's own target
                if (first.Declared.Item is not null && at.Resolve(first.Declared.Item, PathUsage.Unwind) is { Succeeded: true } collection)
                    at = at.ForElement(collection.Path!);

                // not a read: why no target has a hint path
                var failure = at.Resolve(wire, PathUsage.Select);

                errors.Add(Error(failure.Code ?? Codes.UnknownPath, failure.Message ?? $"'{wire}' is not a path of '{first.Declared}'.", index, wire));
            }
        }

        // ---- select ----------------------------------------------------------------------

        /// <summary>The members of a contract 2 lookup: a join has no <c>select</c> there.</summary>
        private const string LookupMembers = "from, path, as, filter, limit, sort, first, on, forTarget, parentAs";

        /// <summary>The members of a contract 2 resolve; see <see cref="LookupMembers"/>.</summary>
        private const string ResolveMembers = "path, as, filter, elements, target, parentAs, onMissing, forTarget, byTarget, outcomeAs";

        /// <summary>
        /// A contract 2 join has no <c>select</c>: what it loads is inferred from what is read under its
        /// alias, and what it shows is what the projection names under it (without one, its key and
        /// display member). The member is refused by name, with the stage that replaces it, on the public
        /// route and on an internal call alike: an owner query carries what the origin needs of an alias
        /// as its own <c>project</c> stage, and a stage continued at an owner travels as the caller
        /// wrote it, so nothing the engine sends writes one. Contract 1 keeps its <c>select</c>.
        /// </summary>
        private bool RefuseSelect(string stage, string members, string? alias, int index)
        {
            if (!contract2)
                return false;

            errors.Add(new QueryValidationError
            {
                Code = Codes.UnknownStageMember,
                Message = $"'select' is not a member of {stage}; a {stage} carries {members}. A join loads what later stages read under its alias and shows what a project stage names under it ({{ \"project\": {{ \"{alias ?? "alias"}.<path>\": 1 }} }}); without a projection it shows its key and display member.",
                Stage = index,
                Params = new Dictionary<string, object?> { ["stage"] = stage, ["member"] = "select", ["reason"] = "removed", ["use"] = "project" },
            });

            return true;
        }

        // ---- unwind --------------------------------------------------------------------------

        /// <summary>The members of a contract 2 unwind.</summary>
        private const string UnwindMembers = "path, as, preserveNull, includeIndex, flatten";

        private void BindUnwind(UnwindStage unwind, int index)
        {
            // flatten is a contract 2 member; under contract 1 it is one the stage does not have, and so
            // is keepPath, which no contract has any more.
            IReadOnlyList<string> unknown = contract2
                ? unwind.Unknown
                : [.. unwind.Unknown, .. unwind.Flatten is not null ? ["flatten"] : Array.Empty<string>(), .. unwind.KeepPathWritten ? ["keepPath"] : Array.Empty<string>()];

            if (!CheckStageMembers(unknown, "unwind", contract2 ? UnwindMembers : "path, as, preserveNull, includeIndex", index, Hint(unwind.Flatten is not null || unwind.KeepPathWritten)))
                return;

            if (contract2 && RefuseInvalidMembers(unwind.Invalid, "unwind", UnwindMembers, "true or false", index))
                return;

            // Contract 2 has one unwind: the element goes under 'as' and the collection leaves
            // the row, so a path names one thing along the whole pipeline. There is nothing to choose.
            if (contract2 && unwind.KeepPathWritten)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnknownStageMember,
                    Message = $"'keepPath' is not a member of unwind; unwind carries {UnwindMembers}. An unwind always takes its collection out of the row; the element is under 'as'.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = "unwind", ["member"] = "keepPath", ["reason"] = "removed" },
                });
                return;
            }

            if (unwind.Path is null)
            {
                errors.Add(Error(Codes.UnknownPath, "An unwind names the collection to unwind under 'path'.", index, null));
                return;
            }

            if (contract2 && unwind.As is null)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidAlias,
                    Message = $"An unwind names its element under 'as': after it, '{unwind.Path}' is not in the row any more and each row holds one element under that name.",
                    Stage = index,
                    Path = unwind.Path,
                    Params = new Dictionary<string, object?> { ["stage"] = "unwind", ["member"] = "as", ["reason"] = "required" },
                });
                return;
            }

            if (++unwinds > options.Limits.MaxUnwindStages)
                errors.Add(Limit(Codes.MaxUnwindStagesExceeded, $"The pipeline has more than {options.Limits.MaxUnwindStages} unwind stages.", index, null, "maxUnwindStages", options.Limits.MaxUnwindStages, unwinds));

            if (shape.Grouped)
            {
                errors.Add(Error(Codes.NotACollection, "The shape after a group has no collections.", index, unwind.Path));
                return;
            }

            if (RefusedUnderAnchor(unwind.Path, "unwind", index))
                return;

            var resolution = Read(shape, unwind.Path, PathUsage.Unwind, ReadUse.Unwind, index, Reading.Row);

            if (!resolution.Succeeded)
            {
                errors.Add(Unresolved(resolution, index, unwind.Path));
                return;
            }

            var path = resolution.Path!;
            var isCollection = path.Kind == Kind.Array || (path.Path is not null && Shape.IsCollection(path.Path) && path.Kind == Kind.Dictionary);

            if (!isCollection || path.Storage is null)
            {
                errors.Add(Error(Codes.NotACollection, $"'{unwind.Path}' is not a collection at this stage.", index, unwind.Path));
                return;
            }

            if (path.CollectionAncestors > 0)
            {
                errors.Add(Error(Codes.UnwindOrder, $"'{unwind.Path}' lies under another collection; unwind the outer one first.", index, unwind.Path));
                return;
            }

            string? alias = null;
            string? indexAlias = null;

            if (unwind.As is not null && !CheckAlias(unwind.As, index, out alias))
                return;

            if (unwind.IncludeIndex is not null && !CheckAlias(unwind.IncludeIndex, index, out indexAlias))
                return;

            if (alias is not null && indexAlias is not null && alias == indexAlias)
            {
                errors.Add(Error(Codes.AliasCollision, $"'{alias}' is used for both the element and the index.", index, alias));
                return;
            }

            var firstSegment = unwind.Path.Split('.')[0];
            var rootName = shape.Roots.ContainsKey(firstSegment) && firstSegment != Shape.ImplicitRoot
                ? firstSegment
                : Shape.ImplicitRoot;

            // Contract 1 unwinds in place, or beside the alias, as it always has.
            var keepPath = !contract2;

            // A join bound earlier may read its keys off the page rows after the page is taken
            // (a keyed resolve, a join after the page); the collection the unwind removes before
            // the page would take those keys with it and null every such alias.
            if (!keepPath && JoinReadingUnder(path.Storage) is { } reader)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnwindOrder,
                    Message = $"The unwind takes '{unwind.Path}' out of the row, but the join '{reader}' of an earlier stage reads its keys from it; join after the unwind, through '{alias}'.",
                    Stage = index,
                    Path = unwind.Path,
                    Params = new Dictionary<string, object?> { ["reason"] = "joinReadsCollection", ["join"] = reader, ["alias"] = alias },
                });
                return;
            }

            BoundFlatten? flatten = null;

            if (unwind.Flatten is not null && (flatten = BindFlatten(unwind, path, index)) is null)
                return;

            stages.Add(new BoundStage.Unwind(path, alias, unwind.PreserveNull, indexAlias, flatten, keepPath));
            shape = shape.WithUnwound(path, rootName, alias, indexAlias, keepPath);
        }

        /// <summary>The alias of an earlier resolve or lookup whose reference or parent key lies in or under <paramref name="storage"/>, or null.</summary>
        private string? JoinReadingUnder(string storage)
        {
            static bool Under(string? read, string storage) =>
                read is not null && (read == storage || read.StartsWith(storage + ".", StringComparison.Ordinal));

            foreach (var stage in stages)
            {
                switch (stage)
                {
                    case BoundStage.Resolve resolve when Under(resolve.Reference.Storage, storage) || Under(resolve.CollectionStorage, storage):
                        return resolve.As;

                    case BoundStage.Lookup lookup when Under(lookup.ParentKeyStorage, storage):
                        return lookup.As;
                }
            }

            return null;
        }

        /// <summary>
        /// The descent of <c>unwind.flatten</c>: a member of the element type, merged variant
        /// members included, that is an array of the element's pooled type or of a base of it.
        /// </summary>
        private BoundFlatten? BindFlatten(UnwindStage unwind, ResolvedPath path, int index)
        {
            var name = unwind.Flatten!;
            var element = path.Kind == Kind.Array && path.Shape?.Of is { Kind: Kind.Object, Type: { } type } ? type : null;

            if (element is null)
            {
                errors.Add(Error(Codes.FlattenNotRecursive, $"'{unwind.Path}' is not a collection of objects; flatten follows a member that nests the same kind of element.", index, unwind.Path));
                return null;
            }

            var member = element.Member(name);

            if (member is null)
            {
                errors.Add(Error(Codes.UnknownPath, $"'{name}' is not a member of the elements of '{unwind.Path}'.", index, unwind.Path + "." + name));
                return null;
            }

            var nested = member is { Kind: Kind.Array, Of: { Kind: Kind.Object, Type: { } of } } ? of : null;
            var recursive = nested is not null
                && (ReferenceEquals(nested, element) || nested.Variants.Any(variant => ReferenceEquals(variant.Type, element)));

            if (!recursive || !member.Stored || member.StorageName is null)
            {
                errors.Add(Error(Codes.FlattenNotRecursive, $"'{name}' is not a collection of the same items as '{unwind.Path}'; flatten follows a member that nests the same kind of element.", index, unwind.Path + "." + name));
                return null;
            }

            return new BoundFlatten(name, member.StorageName, options.Limits.MaxFlattenDepth, index);
        }

        // ---- group ---------------------------------------------------------------------------

        /// <summary>
        /// Refuses a group that reads as another one than the caller wrote: a stage that is no object, a key
        /// that names a path and a dateTrunc, an aggregate of several functions. Each would be bound with
        /// part of it dropped.
        /// </summary>
        private bool RefusedAsWritten(GroupStage group, int index)
        {
            if (RefuseInvalidMembers(group.Invalid ? ["group"] : [], "group", "by, fields", "", index))
                return true;

            var before = errors.Count;

            foreach (var by in group.By)
                if (by.Path is not null && by.DateTrunc is not null)
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.UnknownStageMember,
                        Message = $"The key '{by.As}' names a path and a dateTrunc; a group key names one of them.",
                        Stage = index,
                        Path = by.Path,
                        Params = new Dictionary<string, object?> { ["stage"] = "group", ["members"] = new[] { "path", "dateTrunc" }, ["reason"] = "exclusive" },
                    });

            foreach (var (alias, aggregate) in group.Fields)
                if (aggregate.Others.Count > 0)
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.InvalidAggregateArgument,
                        Message = $"The aggregate '{alias}' carries '{aggregate.Function}' and '{string.Join(", ", aggregate.Others)}'; an aggregate is an object of one function.",
                        Stage = index,
                        Params = new Dictionary<string, object?> { ["stage"] = "group", ["field"] = alias, ["reason"] = "exclusive" },
                    });

            return errors.Count > before;
        }

        private void BindGroup(GroupStage group, int index)
        {
            if (!CheckStageMembers(group.Unknown, "group", "by, fields", index))
                return;

            if (group.By.Any(by => by is null) || group.Fields.Any(field => field.Value is null))
            {
                errors.Add(Error(Codes.UnknownStageMember, "A group key is an object of path or dateTrunc and as, and an aggregate is an object of one function; neither is null.", index, null));
                return;
            }

            foreach (var by in group.By)
                if (!CheckStageMembers(by.Unknown, "a group key", "path, dateTrunc, as", index))
                    return;

            if (contract2 && RefusedAsWritten(group, index))
                return;

            var keys = new List<GroupKey>();
            var fields = new List<Aggregate>();
            var aliases = new HashSet<string>(StringComparer.Ordinal);

            if (group.By.Count + group.Fields.Count > options.Limits.MaxGroupFields)
                errors.Add(new QueryValidationError
                {
                    Code = Codes.MaxGroupFieldsExceeded,
                    Message = $"The group has {group.By.Count + group.Fields.Count} fields; the limit is {options.Limits.MaxGroupFields}.",
                    Stage = index,
                    Params = contract2 ? new Dictionary<string, object?> { ["limit"] = "maxGroupFields", ["max"] = options.Limits.MaxGroupFields, ["count"] = group.By.Count + group.Fields.Count } : null,
                });

            foreach (var by in group.By)
            {
                if (Aliases.Problem(by.As) is { } keyProblem)
                {
                    errors.Add(Error(Codes.InvalidAlias, keyProblem, index, by.As));
                    continue;
                }

                if (!aliases.Add(by.As))
                {
                    errors.Add(Error(Codes.AliasCollision, $"'{by.As}' is used twice in the group.", index, by.As));
                    continue;
                }

                if (by.DateTrunc is not null)
                {
                    var trunc = BindDateTrunc(by.DateTrunc, index);

                    if (trunc is not null)
                        keys.Add(new GroupKey(by.As, null, trunc, Kind.DateTime, null));

                    continue;
                }

                if (by.Path is null)
                {
                    errors.Add(Error(Codes.UnknownPath, $"The key '{by.As}' names neither a path nor a dateTrunc.", index, null));
                    continue;
                }

                if (RefusedUnderAnchor(by.Path, "group", index))
                    continue;

                var resolution = Read(shape, by.Path, PathUsage.GroupKey, ReadUse.GroupKey, index, Reading.Row);

                if (!resolution.Succeeded)
                {
                    errors.Add(Unresolved(resolution, index, by.Path));
                    continue;
                }

                var path = resolution.Path!;

                if (path.CollectionAncestors > 0 || path.Kind == Kind.Array)
                {
                    errors.Add(Error(Codes.GroupOnCollection, $"'{by.Path}' is a collection or lies under one; unwind it first.", index, by.Path));
                    continue;
                }

                if (!Kinds.IsScalar(path.Kind))
                {
                    errors.Add(Error(Codes.NotFilterable, $"'{by.Path}' is {Kinds.WithArticle(path.Kind)}; a group key needs a scalar.", index, by.Path));
                    continue;
                }

                // Under contract 2 a string key folds its groups the way a sort folds its order.
                if (contract2 && FoldsAsText(path))
                    collated = true;

                keys.Add(new GroupKey(by.As, path, null, path.Kind, path.Shape));
            }

            foreach (var (alias, aggregate) in group.Fields)
            {
                if (Aliases.Problem(alias) is { } fieldProblem)
                {
                    errors.Add(Error(Codes.InvalidAlias, fieldProblem, index, alias));
                    continue;
                }

                if (!aliases.Add(alias))
                {
                    errors.Add(Error(Codes.AliasCollision, $"'{alias}' is used twice in the group.", index, alias));
                    continue;
                }

                var function = aggregate.Function ?? "";

                if (!Aggregates.Contains(function))
                {
                    errors.Add(Error(Codes.UnknownAggFunction, $"'{function}' is not an aggregate function.", index, null));
                    continue;
                }

                // What the engine counts is a number of rows: an integer far inside what a JSON number holds
                // exactly, so under contract 2 it is an int and travels as a JSON number. Contract 1 keeps its
                // long, which its own row converter writes.
                if (function == "count")
                {
                    fields.Add(new Aggregate(alias, function, null, Kind.Unknown, contract2 ? Kind.Int : Kind.Long, null));
                    continue;
                }

                if (aggregate.Argument is null)
                {
                    errors.Add(Error(Codes.InvalidAggregateArgument, $"'{function}' needs an argument.", index, null));
                    continue;
                }

                var argument = BindExpression(aggregate.Argument, index, function, out var kind, out var argumentShape);

                if (argument is null)
                    continue;

                if (NumericAggregates.Contains(function) && kind is not (Kind.Int or Kind.Long or Kind.Double or Kind.Decimal))
                {
                    errors.Add(Error(Codes.InvalidAggregateArgument, $"'{function}' needs a numeric argument; '{alias}' is over {Kinds.WithArticle(kind)}.", index, null));
                    continue;
                }

                // A mean over 64-bit integers is taken in decimal: a double's granularity
                // above 2^53 is coarser than one, and the wire spells a decimal the way it
                // spells a long, so no digit is lost on either side.
                var (outputKind, outputShape) = function switch
                {
                    "countDistinct" => (contract2 ? Kind.Int : Kind.Long, null),
                    "avg" => (kind is Kind.Decimal or Kind.Long ? Kind.Decimal : Kind.Double, null),
                    "push" => (Kind.Array, argumentShape),
                    _ => (kind, argumentShape),
                };

                // Distinctness and extremes over a string are questions of equality and order,
                // and fold like a key and a sort do.
                if (contract2 && function is "countDistinct" or "min" or "max" && argument is BoundExpression.Path { Resolved: var over } && FoldsAsText(over))
                    collated = true;

                fields.Add(new Aggregate(alias, function, argument, kind, outputKind, outputShape));
            }

            stages.Add(new BoundStage.Group(keys, fields));

            // A group replaces the row: no alias a later stage could continue under survives it.
            anchors.Clear();

            // A key over a member that declares a reference keeps it: the key holds the member's value,
            // so a resolve after the group follows the reference from the key's alias (a key over such a key too).
            // Contract 1 is frozen: its group rows carry no reference, as they never did.
            shape = shape.WithGroup(keys.Select(key => new ShapeNode.GroupOutput(key.OutputKind, key.OutputShape, key.As)
                {
                    KeyOf = contract2 && key.Path is { Addon: null, References: not null } over ? over.Path ?? over.KeyOf : null,
                })
                .Concat(fields.Select(field => field.Function == "push"
                    ? new ShapeNode.GroupOutput(field.OutputKind, null, field.As, field.ArgumentKind, field.OutputShape)
                    : new ShapeNode.GroupOutput(field.OutputKind, field.OutputShape, field.As))));
            sort = null;
        }

        private DateTrunc? BindDateTrunc(DateTruncExpression trunc, int index)
        {
            if (RefusedUnderAnchor(trunc.Path, "group", index))
                return null;

            var resolution = Read(shape, trunc.Path, PathUsage.GroupKey, ReadUse.GroupKey, index, Reading.Row);

            if (!resolution.Succeeded)
            {
                errors.Add(Unresolved(resolution, index, trunc.Path));
                return null;
            }

            var path = resolution.Path!;

            if (path.CollectionAncestors > 0 || path.Kind is not (Kind.DateTime or Kind.Date))
            {
                errors.Add(path.CollectionAncestors > 0
                    ? Error(Codes.GroupOnCollection, $"'{trunc.Path}' lies under a collection that is not unwound; a dateTrunc key is one date per row. Unwind the collection first.", index, trunc.Path)
                    : Error(Codes.InvalidAggregateArgument, $"'{trunc.Path}' is {Kinds.WithArticle(path.Kind)}; dateTrunc takes a date or a dateTime.", index, trunc.Path));
                return null;
            }

            if (!Units.Contains(trunc.Unit ?? ""))
            {
                errors.Add(Error(Codes.InvalidDateTruncUnit, $"'{trunc.Unit}' is not a unit; one of year, quarter, month, week, day, hour, minute, second.", index, trunc.Path));
                return null;
            }

            var timezone = string.IsNullOrWhiteSpace(trunc.Timezone) ? "UTC" : trunc.Timezone.Trim();

            // A fixed offset (+02:00, +0200, +02) and the fixed zones of the Etc area are no entries of a
            // zone table: the database takes them as written, on every platform the same.
            if (timezone != "UTC" && !FixedZone().IsMatch(timezone))
            {
                // Mongo's $dateTrunc takes an IANA id, spelled exactly. The runtime's lookup is
                // looser: it ignores case on some platforms and also finds a zone by its Windows
                // id. What goes on the wire is therefore the id of the zone that was found when
                // that is an IANA id, the IANA id the runtime maps a Windows id to otherwise,
                // and nothing when there is no such mapping.
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone) || !TryIanaId(zone, out var iana))
                {
                    errors.Add(Error(Codes.InvalidTimezone, $"'{timezone}' is not a timezone: an IANA zone id (Europe/Berlin), or an offset from UTC (+02:00).", index, trunc.Path));
                    return null;
                }

                timezone = iana;
            }

            string? weekStart = null;

            if (trunc.Unit == "week")
            {
                weekStart = string.IsNullOrWhiteSpace(trunc.WeekStart) ? "monday" : trunc.WeekStart.Trim().ToLowerInvariant();

                if (!WeekDays.Contains(weekStart))
                {
                    errors.Add(Error(Codes.InvalidDateTruncUnit, $"'{trunc.WeekStart}' is not a day of the week.", index, trunc.Path));
                    return null;
                }
            }

            return new DateTrunc(path, trunc.Unit!, timezone, weekStart);
        }

        /// <summary>
        /// A zone without rules, which the database reads off its spelling: an offset from UTC up to
        /// fourteen hours, or a fixed zone of the Etc area. That area has the zones from fourteen hours
        /// east of UTC (<c>Etc/GMT-14</c>) to twelve west (<c>Etc/GMT+12</c>), and no other.
        /// </summary>
        [System.Text.RegularExpressions.GeneratedRegex(@"^(?:[+-](?:0[0-9]|1[0-4])(?::?[0-5][0-9])?|Etc/GMT(?:-(?:[0-9]|1[0-4])|\+(?:[0-9]|1[0-2]))?|Etc/UTC|GMT)$")]
        private static partial System.Text.RegularExpressions.Regex FixedZone();

        private static bool TryIanaId(TimeZoneInfo zone, [NotNullWhen(true)] out string? iana)
        {
            if (!zone.HasIanaId)
                return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out iana);

            // On Linux the runtime finds a zone by its file under the zoneinfo directory, so it
            // also accepts ids Mongo refuses ("Europe//Berlin") and names Windows cannot find
            // ("CET"). Requiring the id to be one the runtime maps to a Windows zone is the rule
            // Windows applies anyway, so an id binds or is refused the same on every platform.
            iana = zone.Id;
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out _);
        }

        private BoundExpression? BindExpression(QueryExpression expression, int index, string function, out Kind kind, out ShapeDef? shapeDef, int depth = 1)
        {
            kind = Kind.Unknown;
            shapeDef = null;

            // An object naming no operator the engine knows is not a literal: compiled as one,
            // Mongo evaluates it to missing and the column comes back null with a 200. An
            // unknown aggregate function is refused, and an unknown argument operator is too.
            if (expression.Unrecognised is { } unrecognised)
            {
                errors.Add(Error(
                    Codes.InvalidAggregateArgument,
                    unrecognised.Count == 0
                        ? $"The argument of '{function}' is an empty object; it names no path, variable, literal or operator."
                        : $"'{string.Join(", ", unrecognised)}' is not an argument operator; one of {string.Join(", ", QueryExpression.ArithmeticOperators)} is, or a path, a literal or a variable.",
                    index,
                    null));

                return null;
            }

            if (expression.IsPath)
            {
                // A group over a keyed or remote alias's data is the chain's, which only a report aggregates.
                if (RefusedUnderAnchor(expression.Path, "group", index))
                    return null;

                var resolution = Read(shape, expression.Path!, PathUsage.Aggregate, ReadUse.Aggregate, index, Reading.Row);

                if (!resolution.Succeeded)
                {
                    errors.Add(Unresolved(resolution, index, expression.Path));
                    return null;
                }

                var path = resolution.Path!;

                if (path.Storage is null || path.IsRemote)
                {
                    errors.Add(Error(Codes.InvalidAggregateArgument, $"'{expression.Path}' cannot be aggregated here.", index, expression.Path));
                    return null;
                }

                if (path.CollectionAncestors > 0 && function != "push")
                {
                    errors.Add(Error(Codes.GroupOnCollection, $"'{expression.Path}' lies under a collection; unwind it first.", index, expression.Path));
                    return null;
                }

                kind = path.Kind;
                shapeDef = path.Shape;
                return new BoundExpression.Path(path) { Unwrap = contract2 && path is { Addon: not null, Kind: Kind.Decimal } };
            }

            if (expression.IsVar)
            {
                if (!coercer.TryResolveVariable(expression.Var!, out var value))
                {
                    errors.Add(Error(Codes.UnboundVariable, $"The variable '{expression.Var}' is not bound.", index, null));
                    return null;
                }

                var literal = OperandCoercer.Literal(value);

                kind = KindOf(literal);
                return new BoundExpression.Literal(literal);
            }

            if (expression.IsArithmetic)
            {
                if (depth > MaxExpressionDepth)
                {
                    errors.Add(Error(Codes.InvalidAggregateArgument, $"The argument of '{function}' nests more than {MaxExpressionDepth} levels of arithmetic.", index, null));
                    return null;
                }

                var operands = new List<BoundExpression>();
                var kinds = new List<Kind>();
                var taken = new List<Kind>();
                var numeric = Kind.Long;

                foreach (var operand in expression.Operands ?? [])
                {
                    var bound = BindExpression(operand, index, function, out var operandKind, out _, depth + 1);

                    if (bound is null)
                        return null;

                    // A duration its member stores as a number takes part as that number (contract 2; a contract 1 request is bound by the declared kinds alone).
                    var counted = contract2 ? StoredNumber(bound, operandKind) : operandKind;

                    if (counted is Kind.Double or Kind.Decimal)
                        numeric = counted == Kind.Decimal || numeric == Kind.Decimal ? Kind.Decimal : Kind.Double;

                    operands.Add(bound);
                    kinds.Add(operandKind);
                    taken.Add(counted);
                }

                if (operands.Count == 0)
                {
                    errors.Add(Error(Codes.InvalidAggregateArgument, $"'{expression.Operator}' needs operands.", index, null));
                    return null;
                }

                if (expression.Operator == "coalesce")
                    return new BoundExpression.Arithmetic(expression.Operator!, operands);

                // Contract 1 is frozen: its arithmetic binds over any operands, a long unless one is a double or a
                // decimal. What its rows hold is said when it runs.
                if (!contract2)
                {
                    kind = numeric;
                    return new BoundExpression.Arithmetic(expression.Operator!, operands);
                }

                if (!ArithmeticApplies(expression.Operator!, taken, out var result, out var why))
                {
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.InvalidAggregateArgument,
                        Message = $"'{expression.Operator}' {why}.",
                        Stage = index,
                        Params = contract2 ? new Dictionary<string, object?> { ["operator"] = expression.Operator, ["kinds"] = kinds.Select(Kinds.NameOf).ToList() } : null,
                    });
                    return null;
                }

                // A quotient of whole numbers is no whole number: it is a double, a decimal with a decimal operand.
                // Under a mean it counts as the whole numbers it is taken from: the mean is taken in decimal, as
                // a mean over whole numbers is.
                kind = result ?? (expression.Operator == "divide" && numeric == Kind.Long && function != "avg" ? Kind.Double : numeric);
                return new BoundExpression.Arithmetic(expression.Operator!, operands);
            }

            var constant = OperandCoercer.Literal(expression.Literal ?? OperandCoercer.JsonNull);

            kind = KindOf(constant);
            return new BoundExpression.Literal(constant);
        }

        /// <summary>
        /// The kind an operand takes part in arithmetic as: a duration whose member stores it as a number
        /// (ticks as a 64-bit integer, or a double in the member's unit) is that number; every other
        /// operand is of its own kind, so a duration stored as text is no number.
        /// </summary>
        private static Kind StoredNumber(BoundExpression operand, Kind kind) =>
            kind == Kind.TimeSpan && operand is BoundExpression.Path { Resolved: { Addon: null, Leaf.Representation.BsonType: var stored } }
                ? stored switch { BsonType.Int64 => Kind.Long, BsonType.Double => Kind.Double, _ => kind }
                : kind;

        /// <summary>
        /// Whether an arithmetic operator takes operands of these kinds, and the kind of what it
        /// answers where that is no number. Arithmetic is over numbers; a member of a kind the model
        /// does not know, a null and what a <c>coalesce</c> answers are taken as they come (a row that
        /// holds something else there is refused when the request runs). Dates take part in two forms:
        /// one date in an <c>add</c> or as what a <c>subtract</c> takes from (a date moved by
        /// milliseconds), and one date less another (the milliseconds between them).
        /// </summary>
        private static bool ArithmeticApplies(string op, IReadOnlyList<Kind> kinds, out Kind? result, [NotNullWhen(false)] out string? why)
        {
            static bool Number(Kind kind) => kind is Kind.Int or Kind.Long or Kind.Double or Kind.Decimal or Kind.Enum or Kind.Unknown;
            static bool Date(Kind kind) => kind is Kind.Date or Kind.DateTime;

            result = null;
            why = null;

            if (op is "subtract" or "divide" && kinds.Count != 2)
            {
                why = $"takes two operands, not {kinds.Count}";
                return false;
            }

            if (op == "subtract" && Date(kinds[0]) && (Date(kinds[1]) || Number(kinds[1])))
            {
                result = Date(kinds[1]) ? Kind.Long : Kind.DateTime;
                return true;
            }

            if (op == "add" && kinds.Count(Date) == 1 && kinds.All(kind => Date(kind) || Number(kind)))
            {
                result = Kind.DateTime;
                return true;
            }

            if (kinds.FirstOrDefault(kind => !Number(kind), Kind.Unknown) is var other && other != Kind.Unknown)
            {
                why = op switch
                {
                    "add" when Date(other) => "takes numbers and at most one date; two dates do not add up",
                    "subtract" when Date(other) => "takes a date from a date, or a number from a date or a number, not a date from a number",
                    _ when other == Kind.TimeSpan => "takes numbers, not a duration its member stores as text",
                    _ => $"takes numbers, not {Kinds.WithArticle(other)}",
                };
                return false;
            }

            return true;
        }

        private static Kind KindOf(BsonValue value) => value.BsonType switch
        {
            BsonType.Int32 or BsonType.Int64 => Kind.Long,
            BsonType.Double => Kind.Double,
            BsonType.Decimal128 => Kind.Decimal,
            BsonType.String => Kind.String,
            BsonType.Boolean => Kind.Bool,
            BsonType.DateTime => Kind.DateTime,
            _ => Kind.Unknown,
        };

        // ---- project -------------------------------------------------------------------------

        private void BindProject(ProjectStage rawProject, int index)
        {
            if (!contract2)
            {
                rawProject = rawProject.AsContract1();
            }
            else if (rawProject.Invalid.Count > 0)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.MixedProjection,
                    Message = $"'{string.Join(", ", rawProject.Invalid)}' of project is neither 1 nor 0: a projection keeps a path with 1 or true and removes it with 0 or false.",
                    Stage = index,
                    Path = rawProject.Invalid[0],
                    Params = new Dictionary<string, object?> { ["stage"] = "project", ["paths"] = rawProject.Invalid },
                });
                return;
            }

            var project = ExpandDefault(rawProject, index);

            if (project is null)
                return;

            if (project.Fields.Count == 0)
            {
                errors.Add(Error(Codes.MixedProjection, "A projection names at least one path.", index, null));
                return;
            }

            if (project.Fields.Count > options.Limits.MaxProjectionFields)
                errors.Add(Limit(Codes.MaxProjectionFieldsExceeded, $"The projection names {project.Fields.Count} paths; the limit is {options.Limits.MaxProjectionFields}.", index, null, "maxProjectionFields", options.Limits.MaxProjectionFields, project.Fields.Count));

            var included = project.Fields.Where(pair => pair.Value != 0 && pair.Key != WireNames.IdWire).Select(pair => pair.Key).ToList();
            var excluded = project.Fields.Where(pair => pair.Value == 0 && pair.Key != WireNames.IdWire).Select(pair => pair.Key).ToList();
            var idExcluded = project.Fields.TryGetValue(WireNames.IdWire, out var idValue) && idValue == 0;
            var idIncluded = project.Fields.TryGetValue(WireNames.IdWire, out idValue) && idValue != 0;

            if (included.Count > 0 && excluded.Count > 0)
            {
                errors.Add(Error(Codes.MixedProjection, "A projection is inclusion or exclusion, never both; 'id' may be excluded from an inclusion.", index, null));
                return;
            }

            var inclusion = included.Count > 0 || idIncluded;
            var paths = new List<ResolvedPath>();

            foreach (var wire in (inclusion ? included : excluded).Concat(idExcluded && !inclusion ? [WireNames.IdWire] : Array.Empty<string>()))
            {
                // A path an exclusion names is removed, not read.
                var resolution = Read(shape, wire, PathUsage.Project, ReadUse.Project, index, inclusion ? Reading.Row : null);

                if (!resolution.Succeeded)
                {
                    // A member no row can carry (not stored, and nothing stored says its value) is refused
                    // where a projection asks for it. An exclusion asks for nothing: the row is without it anyway.
                    if (!inclusion && resolution.Code == Codes.NotStored)
                        continue;

                    errors.Add(Unresolved(resolution, index, wire));
                    continue;
                }

                paths.Add(resolution.Path!);
            }

            if (inclusion && idIncluded && !shape.Grouped)
            {
                var id = Read(shape, WireNames.IdWire, PathUsage.Project, ReadUse.Project, index, Reading.Row);

                if (id.Succeeded)
                    paths.Add(id.Path!);
            }

            var includeId = !idExcluded;

            stages.Add(new BoundStage.Project(inclusion, paths, includeId));
            shape = shape.WithProjection(inclusion, (inclusion ? included : excluded).Concat(inclusion && idIncluded ? [WireNames.IdWire] : Array.Empty<string>()).Concat(!inclusion && idExcluded ? [WireNames.IdWire] : Array.Empty<string>()), includeId);
        }

        /// <summary>
        /// Expands the reserved <c>$default</c> key of a projection to the entity's key and
        /// display members — the same pair a resolve with no <c>select</c> keeps locally.
        /// A remote resolve has no model of its target and so cannot name that pair itself;
        /// with this the owner applies it, and the remote default is the local one rather
        /// than the owner's whole row.
        /// </summary>
        private ProjectStage? ExpandDefault(ProjectStage project, int index)
        {
            if (!project.Fields.ContainsKey(DefaultSelectKey))
                return project;

            if (shape.Grouped || !shape.IsRootShape)
            {
                errors.Add(Error(Codes.UnknownPath, $"'{DefaultSelectKey}' is the entity's key and display members; the shape here is not the entity's.", index, DefaultSelectKey));
                return null;
            }

            var fields = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (name, value) in project.Fields)
                if (name != DefaultSelectKey)
                    fields[name] = value;

            foreach (var wire in new[] { entity.Key?.Wire, entity.Display?.Wire })
                if (wire is not null)
                    fields[wire] = 1;

            return project with { Fields = fields };
        }

        // ---- sort ----------------------------------------------------------------------------

        private void BindSort(IReadOnlyList<Models.SortField> fields, int index)
        {
            var bound = BindSortEntries(fields, shape, index, Reading.Row);

            // A grouped shape pages by offset, and $skip over rows that tie on every sort field
            // repeats and drops groups between pages: $group emits no stable order and a top-k
            // sort answers any members of a tie. The group keys complete the order, as the key
            // completes it on a root shape; a key the caller sorts on already keeps its place.
            if (shape.Grouped && bound.Count == fields.Count)
                foreach (var key in GroupKeyOrder())
                    if (!bound.Any(field => field.Path.Storage == key.Path.Storage))
                        bound.Add(key);

            var stage = new BoundStage.Sort(bound);

            stages.Add(stage);
            sort = stage;
        }

        /// <summary>
        /// Binds sort entries against <paramref name="at"/>: the sort stage against the row, a
        /// lookup's <c>sort</c> against the child. Each string entry counts in the collation
        /// rules of the whole request, since a lookup's sub-pipeline runs under the aggregate's
        /// collation too.
        /// </summary>
        private List<BoundSortField> BindSortEntries(IReadOnlyList<Models.SortField> fields, Shape at, int index, Reading? reading)
        {
            var bound = new List<BoundSortField>();

            foreach (var field in fields)
            {
                if (field is null)
                {
                    errors.Add(Error(Codes.UnknownStageMember, "A sort entry is an object of one path and a direction: {\"path\": \"asc\"}; this one is null.", index, null));
                    continue;
                }

                if (field.Extra.Count > 0)
                {
                    errors.Add(Error(Codes.UnknownStageMember,
                        $"A sort entry names one path; this one also names '{string.Join(", ", field.Extra)}'. Write one object per key: [{{\"{field.Path}\": \"{field.Direction}\"}}, …].", index, field.Path));
                    continue;
                }

                if (field.Path.Length == 0)
                {
                    errors.Add(Error(Codes.UnknownStageMember, "A sort entry is an object of one path and a direction: {\"path\": \"asc\"}.", index, null));
                    continue;
                }

                // The object form is contract 2; under contract 1 a direction is a string.
                if (!contract2 && field.ObjectForm)
                {
                    errors.Add(Error(Codes.InvalidSortDirection, "A sort entry is a path and a direction string, asc or desc; the object form is a contract 2 form." + Contract1Hint, index, field.Path));
                    continue;
                }

                if (field.Unknown.Count > 0)
                {
                    errors.Add(Error(Codes.UnknownStageMember, $"'{string.Join(", ", field.Unknown)}' is not a member of a sort entry; the object form carries direction and caseSensitive.", index, field.Path));
                    continue;
                }

                if (field.ObjectForm && field.Direction.Length == 0)
                {
                    errors.Add(Error(Codes.InvalidSortDirection, "The object form of a sort entry needs a direction, asc or desc.", index, field.Path));
                    continue;
                }

                var ascending = field.Direction switch
                {
                    "asc" => true,
                    "desc" => false,
                    _ => (bool?)null,
                };

                if (ascending is null)
                {
                    errors.Add(Error(Codes.InvalidSortDirection, $"'{field.Direction}' is not a direction; asc or desc.", index, field.Path));
                    continue;
                }

                var resolution = Read(at, field.Path, PathUsage.Sort, ReadUse.Sort, index, reading);

                if (!resolution.Succeeded)
                {
                    errors.Add(Unresolved(resolution, index, field.Path));
                    continue;
                }

                var path = resolution.Path!;

                if (!path.Sortable)
                {
                    errors.Add(Error(Codes.NotSortable, path.CollectionAncestors > 0 || path.Kind == Kind.Array
                        ? $"'{field.Path}' lies in a collection; a sort needs one value per row."
                        : path.Kind == Kind.Unknown
                            ? $"'{field.Path}' is unknown to the model; it cannot order rows."
                            : $"'{field.Path}' is {Kinds.WithArticle(path.Kind)}; a sort needs a scalar.", index, field.Path));
                    continue;
                }

                if (path.Addon is not null)
                    diagnostics.Add(new Diagnostic { Code = Codes.SortOnAddon, Message = $"The sort on '{field.Path}' orders an addon key; mixed representations group by BSON type.", Stage = index, Path = field.Path });

                // A string orders under the collation unless the entry opts out; anything else
                // orders by value and has nothing to opt out of.
                var text = FoldsAsText(path);

                if (field.CaseSensitive is not null && !text)
                {
                    errors.Add(Option(Error(Codes.OptionNotApplicable, $"'caseSensitive' applies to a sort on a string member; '{field.Path}' is {Kinds.WithArticle(path.LeafKind)}.", index, field.Path), "caseSensitive", "notApplicable"));
                    continue;
                }

                var ignoreCase = contract2 && text && field.CaseSensitive is not true;

                if (ignoreCase)
                    collated = true;
                else if (field.CaseSensitive is true)
                    exactSorts.Add((index, field.Path));

                bound.Add(new BoundSortField(path, ascending.Value, ignoreCase));
            }

            return bound;
        }

        /// <summary>Whether the stage carries a value under its one key; the JSON literal <c>null</c> leaves none.</summary>
        private static bool HasMember(PipelineStage stage, string kind) => kind switch
        {
            "match" => stage.Match is not null,
            "lookup" => stage.Lookup is not null,
            "resolve" => stage.Resolve is not null,
            "unwind" => stage.Unwind is not null,
            "group" => stage.Group is not null,
            "project" => stage.Project is not null,
            "sort" => stage.Sort is not null,
            "page" => stage.Page is not null,
            "values" => stage.Values is not null,
            _ => false,
        };

        /// <summary>
        /// Refuses a stage that carries a member the engine does not have. System.Text.Json
        /// skips an unmapped member by default, so every stage records the names it does not
        /// know: a page carrying <c>skip</c> instead of <c>offset</c>, or an unwind carrying
        /// <c>preserveNulls</c>, is refused rather than bound with the member dropped and the
        /// default applied.
        /// </summary>
        private bool CheckStageMembers(IReadOnlyList<string> unknown, string stage, string members, int index, string hint = "")
        {
            if (unknown.Count == 0)
                return true;

            errors.Add(NoMember(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                $"'{string.Join(", ", unknown)}' is not a member of {stage}; {stage} carries {members}.{hint}", index, null), stage == "a group key" ? "group" : stage, unknown, "unknown"));

            return false;
        }

        /// <summary>
        /// Refuses a stage that carries a member of another JSON kind than the member takes (the stage's
        /// own name for a stage that is no object): read as not written, it would be bound with the default.
        /// </summary>
        private bool RefuseInvalidMembers(IReadOnlyList<string> invalid, string stage, string members, string takes, int index)
        {
            foreach (var member in invalid)
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnknownStageMember,
                    Message = member == stage
                        ? $"A {stage} stage is an object carrying {members}."
                        : $"'{member}' of {stage} is {takes}.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = stage, ["member"] = member, ["reason"] = "kind" },
                });

            return invalid.Count > 0;
        }

        // ---- page ----------------------------------------------------------------------------

        /// <summary>The members of a page stage on the public contract 2 route.</summary>
        private const string PublicPageMembers = "limit, cursor, count";

        private void BindPage(PageStage stage, int index)
        {
            // The public contract 2 page is addressed by a cursor alone. Contract 1 keeps its
            // offset, and so does an internal call: a semi-join walks its owner's ids by offset.
            var cursorOnly = contract2 && !context.Internal;

            // count is a contract 2 member; under contract 1 it is one the stage does not have, named where it was written.
            IReadOnlyList<string> unknown = contract2 || stage.CountWrittenAt is not { } written ? stage.Unknown : [.. stage.Unknown.Take(written), "count", .. stage.Unknown.Skip(written)];

            if (!CheckStageMembers(unknown, "page", cursorOnly ? PublicPageMembers : contract2 ? "limit, offset, cursor, includeTotalCount, count" : "limit, offset, cursor, includeTotalCount", index))
                return;

            // The count is asked with 'count' and answered under 'pageInfo.count'.
            if (cursorOnly && stage.IncludeTotalCountWritten)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnknownStageMember,
                    Message = $"'includeTotalCount' is not a member of page; page carries {PublicPageMembers}. A count is asked with 'count' and answered under 'pageInfo.count'.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = "page", ["member"] = "includeTotalCount", ["reason"] = "removed", ["use"] = "count" },
                });
                return;
            }

            if (cursorOnly && stage.OffsetWritten)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnknownStageMember,
                    Message = $"'offset' is not a member of page; page carries {PublicPageMembers}. A page continues from the 'nextCursor' of the page before it.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = "page", ["member"] = "offset", ["reason"] = "removed", ["use"] = "cursor" },
                });
                return;
            }

            if (contract2 && stage.Invalid.Count > 0)
            {
                foreach (var member in stage.Invalid)
                    errors.Add(new QueryValidationError
                    {
                        Code = member == "cursor" ? Codes.CursorInvalid : Codes.InvalidPageLimit,
                        Message = member switch
                        {
                            "page" => $"A page stage is an object carrying {(cursorOnly ? PublicPageMembers : "limit, offset, cursor, includeTotalCount, count")}.",
                            "cursor" => "'cursor' of page is a string: the 'nextCursor' of the page before it.",
                            _ => $"'{member}' of page is a whole number.",
                        },
                        Stage = index,
                        Params = new Dictionary<string, object?> { ["stage"] = "page", ["member"] = member, ["reason"] = "kind" },
                    });
                return;
            }

            if (page is not null)
            {
                errors.Add(Error(Codes.MultiplePageStages, "A pipeline has at most one page stage.", index, null));
                return;
            }

            // Only an internal contract 2 call can write both ways of asking for a count. They are two
            // answers of one count aggregate (totalCount, and pageInfo.count): a page that carried both
            // would answer a total of rows nobody counted, so it is refused.
            if (contract2 && stage.IncludeTotalCount && stage.Count is { Asked: true })
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidPageLimit,
                    Message = "A page asks for its count one way: 'includeTotalCount' or 'count', not both.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = "page", ["members"] = new[] { "includeTotalCount", "count" }, ["reason"] = "exclusive" },
                });
                return;
            }

            var limit = stage.Limit ?? options.Limits.DefaultPageSize;

            // A report page: a strict request that neither continues nor jumps
            // reads its rows in one page, up to MaxReportPageSize, and refuses rather than cut them.
            var report = contract2 && request.IsStrict && stage.Cursor is null && stage.Offset is null;
            var maximum = report ? Math.Max(options.Limits.MaxPageSize, options.Limits.MaxReportPageSize) : options.Limits.MaxPageSize;

            // A count-only request: limit 0 with a count and without a cursor reads no page.
            var countOnly = contract2 && limit == 0 && stage.Count is { Asked: true } && stage.Cursor is null;

            if (limit == 0 && contract2 && !countOnly)
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidPageLimit,
                    Message = "The page limit 0 asks for the count alone: it needs 'count' and takes no cursor.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["limit"] = 0, ["needs"] = "count" },
                });
            else if (limit < 1 && !countOnly)
                errors.Add(Error(Codes.InvalidPageLimit, $"The page limit {limit} is not positive.", index, null));
            else if (limit > maximum)
                errors.Add(Limit(Codes.PageSizeExceeded, report
                    ? $"The page limit {limit} exceeds the report page maximum of {maximum}."
                    : $"The page limit {limit} exceeds the maximum of {maximum}." + ReportPageNote(), index, null, report && maximum != options.Limits.MaxPageSize ? "maxReportPageSize" : "maxPageSize", maximum, limit));

            // A report page multiplies what its lookups fetch: each brings up to its limit of
            // child rows for every row of the page. Beyond the ordinary page the joined rows are
            // held to what an ordinary page can reach at most, so the larger page never carries
            // more than a host without the cap could.
            if (report && limit > options.Limits.MaxPageSize && limit <= maximum)
            {
                var perRow = stages.Sum(bound => bound switch
                {
                    BoundStage.Lookup lookup => lookup.First ? 1 : lookup.Limit,
                    BoundStage.Resolve { RemoteLookup: { } remote } => remote.First ? 1 : remote.Limit,
                    _ => 0,
                });
                var joined = (long)limit * perRow;
                var budget = (long)options.Limits.MaxPageSize * options.Limits.MaxLookupLimit * options.Limits.MaxLookupStages;

                if (joined > budget)
                    errors.Add(Limit(Codes.PageSizeExceeded, $"The report page of {limit} rows with lookups of up to {perRow} child rows per row could join {joined} rows; the limit is {budget}. Lower the page limit or the lookups' limits.", index, null, "maxReportedRows", budget, joined));
            }

            var offset = stage.Offset ?? 0;

            if (offset < 0)
                errors.Add(Error(Codes.InvalidPageLimit, $"The offset {offset} is negative.", index, null));
            else if (offset > options.Limits.MaxOffset)
                errors.Add(Limit(Codes.MaxOffsetExceeded, $"The offset {offset} exceeds the maximum of {options.Limits.MaxOffset}; use a cursor beyond it.", index, null, "maxOffset", options.Limits.MaxOffset, offset));

            if (stage.Cursor is not null && stage.Offset is not null)
                errors.Add(Error(Codes.InvalidPageLimit, "A page continues from a cursor or jumps by an offset, not both.", index, null));

            // The number form of includeTotalCount is the request's own count cap, under the
            // host's; contract 1 has the boolean form only.
            int? countCap = null;

            if (stage.TotalCountCap is { } cap)
            {
                if (context.Contract == 1)
                    errors.Add(Error(Codes.LegacyStageUnsupported, "'includeTotalCount' is true or false under contract 1; a count cap needs contract 2 (X-OxQL-Contract: 2)." + Contract1Hint, index, null));
                else if (cap < 1)
                    errors.Add(Error(Codes.InvalidPageLimit, $"The count cap {cap} is not a positive integer; includeTotalCount is true, false or a positive integer.", index, null));
                else
                    countCap = Math.Min(cap, options.Limits.CountCap);
            }

            page = new BoundStage.Page(Math.Max(1, limit), Math.Max(0, offset), null, stage.IncludeTotalCount, countCap) { Count = contract2 ? BindCount(stage.Count, index) : null, CountOnly = countOnly };
            pageIndex = index;
            stages.Add(page);
        }

        /// <summary>
        /// The count step of a contract 2 page: one step of <c>Count:StepRows</c> rows, lowered
        /// by the request's <c>upTo</c> and by the host's <c>CountCap</c>, within the request's
        /// <c>budgetMs</c> under <c>Count:MaxBudgetMs</c>. Null when no count is asked.
        /// </summary>
        private BoundCount? BindCount(PageCount? count, int index)
        {
            if (count is null)
                return null;

            if (count.Invalid)
            {
                errors.Add(Error(Codes.InvalidPageLimit, "'count' is true, false, or an object carrying upTo and budgetMs.", index, null));
                return null;
            }

            if (count.Unknown.Count > 0)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.UnknownStageMember,
                    Message = $"'{string.Join(", ", count.Unknown)}' is not a member of page.count; it carries upTo, budgetMs.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = "page", ["member"] = "count." + count.Unknown[0] },
                });
                return null;
            }

            if (count.UpTo is { } upTo && upTo < 1)
                errors.Add(Error(Codes.InvalidPageLimit, $"'count.upTo' is {upTo}; it is a positive integer.", index, null));

            if (count.BudgetMs is { } asked && asked < 1)
                errors.Add(Error(Codes.InvalidPageLimit, $"'count.budgetMs' is {asked}; it is a positive integer.", index, null));

            if (!count.Asked)
                return null;

            var ceiling = Math.Min(Math.Max(1, count.UpTo ?? options.Limits.CountCap), options.Limits.CountCap);
            var budget = Math.Min(Math.Max(1, count.BudgetMs ?? options.Count.EffectiveBudgetMs), options.Count.EffectiveMaxBudgetMs);

            return new BoundCount(Math.Min(options.Count.EffectiveStepRows, ceiling), ceiling, budget);
        }

        /// <summary>Where a larger page is available: a strict contract 2 request without cursor or offset reads up to <c>MaxReportPageSize</c>.</summary>
        private string ReportPageNote() =>
            contract2 && options.Limits.MaxReportPageSize > options.Limits.MaxPageSize
                ? $" A strict request without {(context.Internal ? "cursor or offset" : "a cursor")} may ask for up to {options.Limits.MaxReportPageSize}."
                : "";

        // ---- values --------------------------------------------------------------------------

        /// <summary>The members of a values stage.</summary>
        private const string ValuesMembers = "path, search, limit, counts, scan";

        /// <summary>
        /// The values stage (contract 2): the distinct values of one scalar path among the rows that
        /// reach it, in place of the page. The path is read where the stage stands, as a group key is,
        /// with one difference: collections above it that are not unwound are unwound for the values
        /// alone, so a member of an item answers the values its items hold. Under an alias whose rows
        /// come from an owner after the page there is nothing to read them off (<c>NOT_CONTINUABLE</c>).
        /// A string's values fold as a group key folds, and <c>search</c> keeps the values that start
        /// with its text under the same fold. The stage replaces the row by <c>value</c> (and
        /// <c>count</c>), as a group does, and supplies the page that carries its limit.
        /// </summary>
        private void BindValues(ValuesStage stage, int index)
        {
            valuesIndex = index;

            if (!CheckStageMembers(stage.Unknown, "values", ValuesMembers, index))
                return;

            if (stage.Invalid.Count > 0)
            {
                foreach (var member in stage.Invalid)
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.InvalidPageLimit,
                        Message = member == "values"
                            ? $"A values stage is an object carrying {ValuesMembers}."
                            : $"'{member}' of values is {member switch { "path" or "search" => "a string", "counts" => "true or false", _ => "a whole number" }}.",
                        Stage = index,
                        Params = new Dictionary<string, object?> { ["stage"] = "values", ["member"] = member },
                    });
                return;
            }

            // An internal owner query answers rows per key; it has no values to answer.
            if (request.KeyedBy is not null)
            {
                errors.Add(Error(Codes.UnknownStage, "A request keyed by its caller answers rows; it carries no values stage.", index, null));
                return;
            }

            if (string.IsNullOrEmpty(stage.Path))
            {
                errors.Add(Error(Codes.UnknownPath, "A values stage names the member whose values it answers under 'path'.", index, null));
                return;
            }

            var limit = stage.Limit ?? Math.Min(options.Values.DefaultLimit, options.Limits.MaxValuesLimit);
            var scan = stage.Scan ?? options.Values.EffectiveScanRows;
            var bounded = true;

            if (limit < 1)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidPageLimit,
                    Message = $"The values limit {limit} is not positive.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = "values", ["member"] = "limit" },
                });
                bounded = false;
            }
            else if (limit > options.Limits.MaxValuesLimit)
            {
                errors.Add(ValuesLimit("maxValuesLimit", options.Limits.MaxValuesLimit, limit, index,
                    $"The values limit {limit} exceeds the maximum of {options.Limits.MaxValuesLimit}; narrow the values with 'search' instead."));
                bounded = false;
            }

            if (scan < 1)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.InvalidPageLimit,
                    Message = $"The values scan {scan} is not positive.",
                    Stage = index,
                    Params = new Dictionary<string, object?> { ["stage"] = "values", ["member"] = "scan" },
                });
                bounded = false;
            }
            else if (scan > options.Values.EffectiveScanRows)
            {
                errors.Add(ValuesLimit("valuesScanRows", options.Values.EffectiveScanRows, scan, index,
                    $"The values scan {scan} exceeds the {options.Values.EffectiveScanRows} rows this host reads for a list of values."));
                bounded = false;
            }

            var search = string.IsNullOrEmpty(stage.Search) ? null : stage.Search;

            if (search is not null && search.Length > options.Limits.RegexMaxLength)
            {
                errors.Add(ValuesLimit("regexMaxLength", options.Limits.RegexMaxLength, search.Length, index,
                    $"The values search has {search.Length} characters; the limit is {options.Limits.RegexMaxLength}."));
                bounded = false;
            }

            if (RefusedUnderAnchor(stage.Path, "values", index))
                return;

            var resolution = Read(shape, stage.Path, PathUsage.GroupKey, ReadUse.GroupKey, index, Reading.Row);

            if (!resolution.Succeeded)
            {
                errors.Add(Unresolved(resolution, index, stage.Path));
                return;
            }

            var path = resolution.Path!;

            if (path.Kind == Kind.Array)
            {
                errors.Add(Error(Codes.GroupOnCollection, $"'{stage.Path}' is a collection; values takes a scalar member, also one of the elements of a collection.", index, stage.Path));
                return;
            }

            if (!Kinds.IsScalar(path.Kind) || path.Storage is null)
            {
                errors.Add(Error(Codes.NotFilterable, $"'{stage.Path}' is {Kinds.WithArticle(path.Kind)}; values needs a scalar.", index, stage.Path));
                return;
            }

            // The collections above the path that are not unwound, outermost first: each is unwound for
            // the values alone. One the shape counts and no array stands for (a dictionary stored as an
            // array) has no element to name a member of.
            var collections = CollectionsCrossed(stage.Path).Where(collection => collection.Wire != stage.Path).ToList();

            if (collections.Count != path.CollectionAncestors || collections.Any(collection => collection.Storage is null))
            {
                errors.Add(Error(Codes.GroupOnCollection, $"'{stage.Path}' lies under a collection the values cannot be read through; unwind it first.", index, stage.Path));
                return;
            }

            var text = FoldsAsText(path);

            if (search is not null && !text)
            {
                errors.Add(new QueryValidationError
                {
                    Code = Codes.OptionNotApplicable,
                    Message = $"'search' keeps the values that start with a text; '{stage.Path}' is {Kinds.WithArticle(path.LeafKind)}, not text.",
                    Stage = index,
                    Path = stage.Path,
                    Params = new Dictionary<string, object?> { ["option"] = "search", ["reason"] = "notApplicable", ["kind"] = Kinds.NameOf(path.LeafKind) },
                });
                return;
            }

            if (!bounded)
                return;

            // A string's values fold into one another as a group's keys do, and the search folds with them.
            if (text)
                collated = true;

            var counted = stage.Counts;
            var value = new ShapeNode.GroupOutput(path.Kind, path.Shape, BoundStage.Values.ValueAlias);

            values = new BoundStage.Values(
                path,
                collections.Select(collection => collection.Storage!).ToList(),
                search is null ? null : new BoundCondition.Leaf(
                    path with { Wire = BoundStage.Values.ValueAlias, Storage = BoundStage.Values.ValueStorage, CollectionAncestors = 0, Root = value },
                    "startsWith", new BoundOperand.Single(new BsonString(search)), IgnoreCase: true, IsSemiJoin: false),
                limit,
                counted,
                scan);

            stages.Add(values);
            callerIndexes.Add(index);

            // The stage replaces the row, as a group does: no alias a later stage could continue under survives it.
            anchors.Clear();
            shape = shape.WithGroup(counted
                ? [value, new ShapeNode.GroupOutput(Kind.Int, null, BoundStage.Values.CountAlias)]
                : [value]);
            sort = null;

            // The page the engine supplies carries the limit; nothing is paged, counted or continued.
            page = new BoundStage.Page(limit, 0, null, false);
            stages.Add(page);
            callerIndexes.Add(null);
        }

        private static QueryValidationError ValuesLimit(string limit, int max, int value, int index, string message) => new()
        {
            Code = Codes.ValuesLimitExceeded,
            Message = message,
            Stage = index,
            Params = new Dictionary<string, object?> { ["limit"] = limit, ["max"] = max, ["value"] = value, ["count"] = value },
        };

        /// <summary>
        /// A request over one of the host's limits. Under contract 2 it carries what a client renders
        /// it from: <c>limit</c>, the limit's name as health publishes it, <c>max</c>, its value on
        /// this host, and <c>count</c>, what the request asked for (absent where the binder stops counting).
        /// </summary>
        private QueryValidationError Limit(string code, string message, int? stage, string? path, string limit, long max, long? count)
        {
            var error = Error(code, message, stage, path);

            if (!contract2)
                return error;

            var parameters = new Dictionary<string, object?> { ["limit"] = limit, ["max"] = max };

            if (count is { } asked)
                parameters["count"] = asked;

            return error with { Params = parameters };
        }

        /// <summary>
        /// A condition whose operand or operator does not fit its member, for a reason the member
        /// itself gives. Under contract 2 it carries the path, the operator and that reason.
        /// </summary>
        private QueryValidationError Operand(string message, int? stage, string? path, string op, string reason, Kind? kind = null)
        {
            var error = Error(Codes.InvalidOperand, message, stage, path);

            if (!contract2)
                return error;

            var parameters = new Dictionary<string, object?> { ["path"] = path, ["operator"] = op, ["reason"] = reason };

            if (kind is { } member)
                parameters["kind"] = Kinds.NameOf(member);

            return error with { Params = parameters };
        }

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>Whether a path holds text the collation folds: a string member that is not a single character stored as its code point.</summary>
        private static bool FoldsAsText(ResolvedPath path) => path.LeafKind == Kind.String && !OperandCoercer.IsCharRepresented(path) && path.Root is not ShapeNode.Outcome;

        private bool CheckAlias(string? alias, int index, out string checkedAlias)
        {
            checkedAlias = alias ?? "";

            if (Aliases.Problem(alias) is { } problem)
            {
                errors.Add(Error(Codes.InvalidAlias, problem, index, alias));
                return false;
            }

            if (shape.IsTaken(checkedAlias))
            {
                errors.Add(Error(Codes.AliasCollision, $"'{alias}' collides with a member or an earlier alias.", index, alias));
                return false;
            }

            return true;
        }
    }

    /// <summary>Raised inside the session when the cursor does not verify; turned into <c>CURSOR_INVALID</c> by the caller.</summary>
    internal sealed class CursorException : Exception
    {
        /// <summary>Why the cursor does not verify (<see cref="CursorCodec.Malformed"/>, <see cref="CursorCodec.Signature"/>, <see cref="CursorCodec.OtherQuery"/>).</summary>
        public string? Reason { get; init; }
    }
}

internal static class ResolvedPathExtensions
{
    public static ReferenceDef? Reference(this ResolvedPath path) => path.Path?.Reference;
}
