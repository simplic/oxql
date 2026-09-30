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
public sealed class Binder
{
    /// <summary>
    /// The deepest nesting of <c>and</c>, <c>or</c>, <c>not</c> and <c>any</c> a condition may have
    /// (<c>MAX_CONDITIONS_EXCEEDED</c> beyond it). Every level is one or two more nested documents in
    /// the compiled pipeline, whose serializer stops at 100; the bound keeps a margin for the stages
    /// a filter is nested in (a lookup or resolve sub-pipeline). No request the 2.0 host read
    /// (JSON depth 32) nests deeper.
    /// </summary>
    public const int MaxConditionDepth = 32;

    private static readonly IReadOnlySet<string> Units = new HashSet<string>(StringComparer.Ordinal) { "year", "quarter", "month", "week", "day", "hour", "minute", "second" };
    private static readonly IReadOnlySet<string> WeekDays = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
    private static readonly IReadOnlySet<string> Aggregates = new HashSet<string>(StringComparer.Ordinal) { "sum", "avg", "min", "max", "first", "last", "push", "count", "countDistinct" };
    private static readonly IReadOnlySet<string> NumericAggregates = new HashSet<string>(StringComparer.Ordinal) { "sum", "avg" };

    /// <summary>
    /// The sentence that ends every refusal a contract 1 request receives for a contract 2
    /// construct (DESIGN §3.12): a request without the contract header is read as contract 1
    /// while compatibility is on, which is the likeliest mistake when a query is pasted into a
    /// report data source. It begins with a space, so it is appended to a finished sentence.
    /// </summary>
    public const string Contract1Hint = " This request was read as contract 1 because it carries no 'X-OxQL-Contract: 2' header.";

    /// <summary>
    /// The top-level members a request carries. An internal call may also carry <c>keyedBy</c>
    /// (<see cref="InternalRequestMembers"/>); the public route never names it.
    /// </summary>
    public const string RequestMembers = "entityType, variables, pipeline, strict";

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
    /// Binds one request. With <paramref name="explain"/> the page's cursor is not decoded (explain
    /// ignores it, DESIGN §4.2), so a request binds the same with or without one.
    /// </summary>
    public async ValueTask<BindOutcome> BindAsync(QueryRequest request, RequestContext context, bool explain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

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
            return new BindOutcome.Failed(Refusal.Validation(errors)) { Trace = trace };

        try
        {
            return new BindOutcome.Bound(session.Result(scope, decodeCursor: !explain)) { Trace = trace };
        }
        catch (CursorException)
        {
            return new BindOutcome.Failed(Refusal.Validation([Error(Codes.CursorInvalid, "The cursor is not valid for this query: it was issued for another query, was altered, or is malformed.", null, null)])) { Trace = trace };
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

    /// <summary>One binding run: the stage loop and everything it accumulates.</summary>
    private sealed class Session(Binder binder, QueryRequest request, RequestContext context, EntityDef entity, List<QueryValidationError> errors, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        private readonly OxQLOptions options = context.Options;
        private const string DefaultSelectKey = "$default";

        private readonly OperandCoercer coercer = new(context.Options, request.Variables, diagnostics);
        private readonly List<BoundStage> stages = [];

        /// <summary>The caller's stage index of each bound stage, by position; null for a stage the binder supplied.</summary>
        private readonly List<int?> callerIndexes = [];
        private readonly Dictionary<string, IReadOnlyList<AddonDefinition>> addons = new(StringComparer.Ordinal);
        private Shape shape = null!;
        private BoundStage.Sort? sort;
        private BoundStage.Page? page;
        private int pageIndex = -1;
        private int lookups, unwinds, resolves, conditions, conditionDepth;
        private bool conditionDepthReported;
        private bool hasSemiJoin;

        /// <summary>The shape the pipeline entered with, and each caller stage's kind and shapes around it (DESIGN §4.6).</summary>
        private Shape entry = null!;
        private readonly List<(int Index, string? Kind, Shape Before, Shape After)> traced = [];

        /// <summary>The stages that failed only under a poisoned alias: their errors were dropped, their status is <c>skipped</c>.</summary>
        private readonly HashSet<int> poisonedStages = [];

        /// <summary>
        /// The aliases a stage continues under (DESIGN §3.5.3): every keyed or remote resolve's alias and
        /// owning row, and every alias a continued stage added, each with the keyed stage whose owner
        /// query carries the continuation.
        /// </summary>
        private readonly Dictionary<string, ContinuationAnchor> anchors = new(StringComparer.Ordinal);

        /// <summary>How many stages continue under each keyed stage, by its alias.</summary>
        private readonly Dictionary<string, int> continuedPerAnchor = new(StringComparer.Ordinal);

        /// <summary>
        /// An alias a stage may continue under: the keyed <paramref name="Stage"/> whose owner runs the
        /// continuation, the one target the alias exists on (<c>forTarget</c> of the stage that added
        /// it), whether it holds every resolved target of a row (<c>elements: "all"</c>), and whether a
        /// continued stage added it (<paramref name="Nested"/>): the owner then binds that stage's own
        /// join, so its targets are the owner's to know and a <c>forTarget</c> under it is the owner's to check.
        /// </summary>
        private sealed record ContinuationAnchor(BoundStage.Resolve Stage, string? ForTarget, bool Many, bool Nested = false);

        /// <summary>Contract 2, where a string comparison, sort or group key folds case unless it opts out.</summary>
        private readonly bool contract2 = context.Contract != 1;

        /// <summary>Whether something folds under the collation, which the whole aggregate then carries.</summary>
        private bool collated;

        /// <summary>The sort entries that opted out of the fold, by stage: they cannot run inside a collated aggregate.</summary>
        private readonly List<(int Stage, string Path)> exactSorts = [];

        /// <summary>The comparisons that opted out of the fold with a text too long for a pattern: refused once the aggregate turns out collated.</summary>
        private readonly List<(int Stage, string Path, string Op)> exactTexts = [];

        public async Task RunAsync()
        {
            await LoadAddonsAsync(entity);
            shape = Shape.ForEntity(entity, addons);
            entry = shape;

            BindRequestMembers();
            BindKeyedBy();

            if (request.Variables is not null && request.Variables.Values.Count > options.Limits.MaxVariables)
                errors.Add(Error(Codes.MaxVariablesExceeded, $"The request binds {request.Variables.Values.Count} variables; the limit is {options.Limits.MaxVariables}.", null, null));

            var pipeline = request.Pipeline ?? [];

            if (pipeline.Count > options.Limits.MaxPipelineStages)
                errors.Add(Error(Codes.MaxPipelineStagesExceeded, $"The pipeline has {pipeline.Count} stages; the limit is {options.Limits.MaxPipelineStages}.", null, null));

            for (var index = 0; index < pipeline.Count; index++)
            {
                var stage = pipeline[index];

                // A null element is a caller error, not a fault, and is refused with a code.
                var before = shape;

                if (stage is null)
                {
                    errors.Add(Error(Codes.UnknownStage, "A stage is an object carrying exactly one stage member; this one is null.", index, null));
                    traced.Add((index, null, before, shape));
                    continue;
                }

                // So is a known stage key whose value is null: the key is recorded, the member
                // is not, and no stage binds from nothing.
                if (stage.Kind is { } kind && !HasMember(stage, kind))
                {
                    errors.Add(Error(Codes.UnknownStageMember, $"'{kind}' is null; a {kind} stage carries {(kind == "sort" ? "an array of sort entries" : "an object")}.", index, null));
                    traced.Add((index, kind, before, shape));
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
                    default:
                        errors.Add(Error(Codes.UnknownStage, stage.Keys.Count == 0
                            ? "A stage object must carry exactly one stage member."
                            : $"'{string.Join(", ", stage.Keys)}' is not a stage; a stage object carries exactly one of match, lookup, resolve, unwind, group, project, sort, page.", index, null));
                        break;
                }

                // A stage that failed leaves its aliases poisoned: a later path under one fails
                // without an error of its own, so one mistake is reported once (DESIGN §3.8).
                if (errors.Count > errorsBefore)
                    PoisonAliases(stage);

                // A caller stage may bind to no stage (an empty match) or to one; either way every
                // bound stage keeps the caller's index, which diagnostics and explain name.
                while (callerIndexes.Count < stages.Count)
                    callerIndexes.Add(index);

                traced.Add((index, stage.Kind, before, shape));
            }

            // What failed only because it lay under a poisoned alias was reported where the alias
            // failed; explain shows such a stage as skipped.
            foreach (var poisoned in errors.Where(error => error.Code == Shape.PoisonedCode && error.Stage is not null))
                poisonedStages.Add(poisoned.Stage!.Value);

            errors.RemoveAll(error => error.Code == Shape.PoisonedCode);

            if (errors.Count == 0)
                CheckContinuedAnchorsKept();

            if (conditions > options.Limits.MaxConditions)
                errors.Add(Error(Codes.MaxConditionsExceeded, $"The request has {conditions} conditions; the limit is {options.Limits.MaxConditions}.", null, null));

            // One aggregate runs under one collation, and a sort has no form the collation
            // does not reach: an exact sort needs every string comparison and key in the
            // request to be exact as well.
            if (collated)
            {
                foreach (var (stage, path) in exactSorts)
                    errors.Add(Error(Codes.OptionNotApplicable, $"The sort on '{path}' asks for the exact order, but another comparison, sort or group key of the request folds case; a request orders exactly only when every string comparison in it is caseSensitive.", stage, path));

                // Inside a collated aggregate an exact comparison is a pattern, with the pattern's limit.
                foreach (var (stage, path, op) in exactTexts)
                    errors.Add(Error(Codes.InvalidOperand, TooLongToMatch(op, path), stage, path));
            }

            if (errors.Count == 0)
                DefaultSort();

            if (page is null)
            {
                page = new BoundStage.Page(options.Limits.DefaultPageSize, 0, null, false);
                stages.Add(page);
                callerIndexes.Add(null);
            }
        }

        /// <summary>
        /// The top-level members (DESIGN §3.0, §3.12). Under contract 2 a member a request does
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
                errors.Add(Error(Codes.UnknownRequestMember,
                    $"'{string.Join(", ", unknown.Keys)}' is not a member of a request; a request carries {(context.Internal ? InternalRequestMembers : RequestMembers)}.", null, null));
        }

        /// <summary>
        /// The internal <c>keyedBy</c> (DESIGN §3.5.2 step 3): the owner's rows grouped per key, at most
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
                errors.Add(Error(Codes.UnknownRequestMember,
                    $"'keyedBy' is not a member of a request; a request carries {RequestMembers}.", null, null));
                return;
            }

            // The roots the prologue adds, poisoned until it binds: a keyedBy that fails is one error,
            // not one more per path of the projection under them (DESIGN §3.8).
            foreach (var root in new[] { BoundKeyedBy.Element, BoundKeyedBy.Key })
                if (!shape.IsTaken(root))
                    shape = shape.WithRoot(root, new ShapeNode.Poisoned(root));

            if (keyedBy.Unknown is { Count: > 0 } unknownMembers)
            {
                errors.Add(Error(Codes.UnknownRequestMember,
                    $"'{string.Join(", ", unknownMembers.Keys.Select(name => "keyedBy." + name))}' is not a member of keyedBy; it carries path, keys, perKey, references and rows.", null, null));
                return;
            }

            if (keyedBy.Rows is not (null or "entity"))
            {
                errors.Add(Error(Codes.InvalidOperand, "'keyedBy.rows' is \"entity\" when given.", null, keyedBy.Path));
                return;
            }

            var wire = keyedBy.Path ?? "";
            var resolution = shape.Resolve(wire, PathUsage.Match);

            if (!resolution.Succeeded)
            {
                errors.Add(Error(resolution.Code!, resolution.Message!, null, keyedBy.Path));
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
            // limit plus that one (DESIGN §3.4.4).
            if (perKey < 1 || perKey > options.Limits.MaxLookupLimit + 1)
            {
                errors.Add(Error(Codes.LookupLimitExceeded,
                    $"'keyedBy.perKey' is {perKey}; this host answers between 1 and {options.Limits.MaxLookupLimit + 1} rows per key (a lookup's limit of at most {options.Limits.MaxLookupLimit}, plus one).", null, keyedBy.Path));
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

        /// <summary>The contract 1 hint when <paramref name="contract2Construct"/> is what a contract 1 request was refused for; empty otherwise.</summary>
        private string Hint(bool contract2Construct) => !contract2 && contract2Construct ? Contract1Hint : "";

        /// <summary>The aliases a failed stage would have created, each poisoned when its name is still free.</summary>
        private void PoisonAliases(PipelineStage stage)
        {
            IEnumerable<string?> aliases = stage.Kind switch
            {
                "lookup" => [stage.Lookup!.As, stage.Lookup.ParentAs],
                "resolve" => [stage.Resolve!.As, stage.Resolve.ParentAs],
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
            if (sort is not null || shape.IsRootShape)
                return;

            var fields = new List<BoundSortField>();

            if (shape.Grouped)
            {
                // The keys only: an aggregate is not unique per group and adds nothing to the order.
                fields.AddRange(GroupKeyOrder());
            }
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

            return new BindTrace(entry ?? shape, steps, shape);
        }

        public BoundPipeline Result(BoundStage.Scope scope, bool decodeCursor = true)
        {
            var mode = shape.IsRootShape ? PagingMode.Keyset : PagingMode.Offset;
            var fingerprint = BoundCanonical.Fingerprint(scope, stages, mode, keyedByBound);

            // The cursor is verified against the finished fingerprint; explain ignores it.
            if (decodeCursor && pageIndex >= 0 && request.Pipeline![pageIndex].Page!.Cursor is { } cursor)
            {
                var payload = binder.cursors.Decode(cursor, fingerprint);

                if (payload is null)
                    throw new CursorException();

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
                FinalShape = shape,
                Sort = sort,
                Page = page!,
                PagingMode = mode,
                Diagnostics = diagnostics,
                Fingerprint = fingerprint,
                Canonical = BoundCanonical.Render(scope, stages, page, mode, keyedByBound).ToJsonString(),
                HasSemiJoin = hasSemiJoin,
                KeyedBy = keyedByBound,
                Collated = collated,
                Strict = contract2 && request.IsStrict,
            };
        }

        private async Task LoadAddonsAsync(EntityDef target)
        {
            if (!target.Extendable || addons.ContainsKey(target.Id))
                return;

            addons[target.Id] = await context.AddonSource.ForEntityAsync(target.Id, context.Organisation!.Value, cancellationToken);
        }

        // ---- match ---------------------------------------------------------------------------

        private void BindMatch(MatchStage match, int index)
        {
            if (match.Condition is null)
                return;

            var bound = BindCondition(match.Condition, shape, index);

            if (bound is not null)
                stages.Add(new BoundStage.Match(bound));
        }

        /// <summary>
        /// Binds one condition, refusing a tree nested deeper than <see cref="MaxConditionDepth"/>
        /// levels of <c>and</c>, <c>or</c>, <c>not</c> and <c>any</c> once per request: the
        /// compiled filter would pass the driver's nesting limit and fail as a fault.
        /// </summary>
        private BoundCondition? BindCondition(FilterCondition condition, Shape at, int index)
        {
            if (conditionDepth >= MaxConditionDepth)
            {
                if (!conditionDepthReported)
                    errors.Add(Error(Codes.MaxConditionsExceeded, $"A condition nests more than {MaxConditionDepth} levels of and, or, not and any; flatten the groups.", index, null));

                conditionDepthReported = true;
                return null;
            }

            conditionDepth++;

            try
            {
                return BindConditionAt(condition, at, index);
            }
            finally
            {
                conditionDepth--;
            }
        }

        private BoundCondition? BindConditionAt(FilterCondition condition, Shape at, int index)
        {
            if (condition.And is not null)
                return BindGroup(condition.And, at, index, "and", list => new BoundCondition.And(list));

            if (condition.Or is not null)
                return BindGroup(condition.Or, at, index, "or", list => new BoundCondition.Or(list));

            if (condition.Not is not null)
            {
                if (condition.IsEmptyGroup)
                {
                    errors.Add(Error(Codes.EmptyLogicalGroup, "'not' has no condition.", index, null));
                    return null;
                }

                var inner = BindCondition(condition.Not, at, index);

                return inner is null ? null : new BoundCondition.Not(inner);
            }

            if (condition.Path is null)
            {
                errors.Add(Error(Codes.EmptyLogicalGroup, "A condition names a path or a logical group.", index, null));
                return null;
            }

            if (condition.IsAny)
                return BindAny(condition, at, index);

            conditions++;

            var resolution = at.Resolve(condition.Path, PathUsage.Match);

            if (!resolution.Succeeded)
            {
                errors.Add(Error(resolution.Code!, resolution.Message!, index, condition.Path));
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

            // The alias of a remote resolve is the owner's row, not a path of the owner: only a
            // member of it travels as a semi-join, so a condition on the alias itself stops here
            // instead of reaching the owner as a match on a name it does not have.
            if (path.IsRemote && !path.Filterable)
            {
                errors.Add(Error(Codes.ResolveNotFilterable,
                    $"'{condition.Path}' is the alias of a remote resolve; filter on one of its members (a semi-join on the owner), not on the alias itself.", index, condition.Path));
                return null;
            }

            // A variant test is on an object, which no other comparison can filter; under a remote
            // alias the owner binds it like any other condition.
            if (op == "is" && !path.IsRemote)
                return BindIs(condition, path, index);

            if (!path.IsRemote && !path.Filterable && !(op == "exists" && path.Storage is not null))
            {
                errors.Add(Error(Codes.NotFilterable, path.Kind == Kind.Unknown
                    ? $"'{condition.Path}' is unknown to the model; it can be projected, not filtered."
                    : $"'{condition.Path}' is {Kinds.WithArticle(path.Kind)}; a filter needs a scalar.", index, condition.Path));
                return null;
            }

            if (!path.IsRemote && !OperandCoercer.Applies(op, path.LeafKind))
            {
                errors.Add(Error(Codes.InvalidOperand, $"'{op}' does not apply to {Kinds.WithArticle(path.LeafKind)}.", index, condition.Path));
                return null;
            }

            // A char is a string on the wire and a code point in storage, so the four text
            // operators pass the kind gate above and have nothing to match against.
            if (!path.IsRemote && OperandCoercer.NeedsText(op) && OperandCoercer.IsCharRepresented(path))
            {
                errors.Add(Error(Codes.InvalidOperand, $"'{condition.Path}' holds a single character stored as its code point; '{op}' needs text. Compare it with eq, neq, in or nin.", index, condition.Path));
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

            if (isPattern && !TextFits(operand))
            {
                errors.Add(Error(Codes.InvalidOperand, TooLongToMatch(op, condition.Path), index, condition.Path));
                return null;
            }

            if (caseSensitive is true && path.LeafKind == Kind.String && !TextFits(operand))
                exactTexts.Add((index, condition.Path, op));

            if (op == "regex" && operand is BoundOperand.Single { Value: BsonString pattern } && !RegexGuard.IsAnchored(pattern.Value))
                diagnostics.Add(new Diagnostic { Code = Codes.RegexUnanchored, Message = $"The pattern on '{condition.Path}' is not anchored; it scans every value of the member.", Stage = index, Path = condition.Path });

            return new BoundCondition.Leaf(path, op, operand, ignoreCase, IsSemiJoin: false);
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

                errors.Add(Error(Codes.InvalidOperand, $"'is' applies to a member that holds one of several variants; '{condition.Path}' is {what}.", index, condition.Path));
                return null;
            }

            if (path.CollectionAncestors > 0)
            {
                errors.Add(Error(Codes.InvalidOperand, $"'{condition.Path}' lies under a collection that is not unwound; test its elements with 'any' on the collection, or unwind it first.", index, condition.Path));
                return null;
            }

            if (condition.Options is not null)
            {
                errors.Add(Error(Codes.OptionNotApplicable, "'is' takes no options.", index, condition.Path));
                return null;
            }

            var operand = coercer.CoerceVariants(condition.Value, path, type, index, errors);

            return operand is null ? null : new BoundCondition.Leaf(path, "is", operand, IgnoreCase: false, IsSemiJoin: false);
        }

        /// <summary>
        /// The case option of a condition, as the caller's choice: true to compare exactly,
        /// false to fold, null when nothing was written. <c>caseSensitive</c> is the option;
        /// <c>ignoreCase</c> is its alias with the opposite sense, kept for one release. Under
        /// contract 1 only <c>ignoreCase: true</c> means anything, as it always has.
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

            if (conditionOptions.Unknown is { Count: > 0 } unknown)
                errors.Add(Error(Codes.OptionNotApplicable, $"'{string.Join(", ", unknown)}' is not an option.", index, condition.Path));

            var applies = path.IsRemote || OperandCoercer.IgnoreCaseApplies(op, path.LeafKind);
            var why = path.LeafKind == Kind.String && !path.IsRemote && OperandCoercer.FoldsByDefault(op, path)
                ? $"'{op}' on '{condition.Path}' orders under the collation of the whole request and cannot opt out of it"
                : $"'{condition.Path}' is a {Kinds.NameOf(path.LeafKind)} under '{op}'";
            bool? caseSensitive = null;

            if (conditionOptions.CaseSensitive is { } sensitive)
            {
                if (!contract2)
                    errors.Add(Error(Codes.OptionNotApplicable, "'caseSensitive' is not an option under contract 1; it is a contract 2 option." + Contract1Hint, index, condition.Path));
                else if (!applies)
                    errors.Add(Error(Codes.OptionNotApplicable, $"'caseSensitive' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; {why}.", index, condition.Path));
                else
                    caseSensitive = sensitive;
            }

            if (conditionOptions.IgnoreCase is not { } ignore)
                return caseSensitive;

            // `ignoreCase: false` restates the contract 1 default, and on a member without text
            // asks for the exact comparison it already makes; neither changes anything and
            // neither is refused.
            if (!ignore && (!contract2 || (path.LeafKind != Kind.String && !path.IsRemote)))
                return caseSensitive;

            if (!applies)
            {
                errors.Add(Error(Codes.OptionNotApplicable, $"'ignoreCase' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; {why}.", index, condition.Path));
                return caseSensitive;
            }

            if (caseSensitive is { } declared && declared == ignore)
            {
                errors.Add(Error(Codes.OptionNotApplicable, $"'caseSensitive: {(declared ? "true" : "false")}' and 'ignoreCase: {(ignore ? "true" : "false")}' contradict each other; ignoreCase is the alias of caseSensitive with the opposite sense.", index, condition.Path));
                return caseSensitive;
            }

            return caseSensitive ?? !ignore;
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

        private BoundCondition? BindGroup(IReadOnlyList<FilterCondition> group, Shape at, int index, string name, Func<IReadOnlyList<BoundCondition>, BoundCondition> make)
        {
            if (group.Count == 0)
            {
                errors.Add(Error(Codes.EmptyLogicalGroup, $"'{name}' has no conditions.", index, null));
                return null;
            }

            var bound = new List<BoundCondition>(group.Count);

            foreach (var member in group)
            {
                var inner = BindCondition(member, at, index);

                if (inner is not null)
                    bound.Add(inner);
            }

            return bound.Count == group.Count ? make(bound) : null;
        }

        private BoundCondition? BindAny(FilterCondition condition, Shape at, int index)
        {
            var resolution = at.Resolve(condition.Path!, PathUsage.Match);

            if (!resolution.Succeeded)
            {
                errors.Add(Error(resolution.Code!, resolution.Message!, index, condition.Path));
                return null;
            }

            var path = resolution.Path!;

            if (path.IsRemote || path.Path is null || path.Kind != Kind.Array || path.Shape?.Of?.Kind != Kind.Object || path.CollectionAncestors > 0)
            {
                errors.Add(Error(Codes.AnyNotApplicable, path.Kind != Kind.Array
                    ? $"'{condition.Path}' is not a collection here; 'any' applies to a collection of objects that is not unwound."
                    : path.CollectionAncestors > 0
                        ? $"'{condition.Path}' lies under another collection; unwind the outer one first."
                        : $"'{condition.Path}' is a collection of scalars; the plain form already matches some element.", index, condition.Path));
                return null;
            }

            var inner = BindCondition(condition.Any!, at.ForElement(path), index);

            return inner is null ? null : new BoundCondition.Any(path, inner);
        }

        // ---- lookup --------------------------------------------------------------------------

        private async Task BindLookupAsync(LookupStage lookup, int index)
        {
            // sort, first, on and forTarget are contract 2 members; under contract 1 they are
            // members the stage does not have, as is any of them written with the wrong kind.
            var contract2Members = new (string Name, bool Written)[]
            {
                ("sort", lookup.Sort is not null), ("first", lookup.First is not null), ("on", lookup.On is not null), ("forTarget", lookup.ForTarget is not null),
                ("parentAs", lookup.ParentAs is not null), ("parentSelect", lookup.ParentSelect is not null),
            };
            IReadOnlyList<string> unknown = contract2
                ? lookup.Unknown
                : [.. lookup.Unknown, .. contract2Members.Where(member => member.Written).Select(member => member.Name), .. lookup.Malformed];

            if (unknown.Count > 0)
            {
                errors.Add(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                    $"'{string.Join(", ", unknown)}' is not a member of lookup; a lookup carries {(contract2 ? "from, path, as, select, filter, limit, sort, first, on, forTarget, parentAs, parentSelect" : "from, path, as, select, filter, limit")}."
                    + Hint(contract2Members.Any(member => member.Written) || lookup.Malformed.Count > 0), index, null));
                return;
            }

            if (lookup.Malformed.Count > 0)
            {
                foreach (var name in lookup.Malformed)
                    errors.Add(Error(Codes.UnknownStageMember, name switch
                    {
                        "first" => "A lookup's 'first' is true or false.",
                        "sort" => "A lookup's 'sort' is an array of sort entries: [{\"path\": \"asc\"}, …].",
                        "parentSelect" => "A lookup's 'parentSelect' is an array of paths.",
                        _ => $"A lookup's '{name}' is a string.",
                    }, index, null));
                return;
            }

            // On a keyed or remote alias the lookup runs at that alias's owner (DESIGN §3.5.3);
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
                errors.Add(Error(Codes.MaxLookupStagesExceeded, $"The pipeline has more than {options.Limits.MaxLookupStages} lookup stages.", index, null));

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
                errors.Add(Error(Codes.OptionNotApplicable,
                    $"'forTarget' applies to a stage continued under a remote alias with several targets; {(lookup.On is null ? "this lookup joins on the entity itself" : $"'{lookup.On}' is a row of this host")}.", index, null));
                return;
            }

            if (string.IsNullOrWhiteSpace(lookup.From) || !binder.model.TryResolve(lookup.From, out var child, out var retired))
            {
                // Another service's entity: the lookup runs at its owner through the keyed fetch
                // (DESIGN §3.4.4), when this host can reach that owner.
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
            if (lookup.ParentAs is not null || lookup.ParentSelect is not null)
            {
                errors.Add(Error(Codes.OptionNotApplicable,
                    $"'{(lookup.ParentAs is not null ? "parentAs" : "parentSelect")}' applies to a lookup of another service's item collection ('entity#item'); '{child.Id}' is an entity of this host.", index, null));
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

            var childShape = Shape.ForEntity(child, addons);
            var reference = childShape.Resolve(lookup.Path ?? "", PathUsage.Match);

            if (!reference.Succeeded)
            {
                errors.Add(Error(reference.Code!, reference.Message!, index, lookup.Path));
                return;
            }

            var childPath = reference.Path!;

            if (childPath.Reference() is not { } declared || declared.TargetEntity != parent.Id)
            {
                errors.Add(Error(Codes.LookupNotDeclared, $"'{child.Id}#{lookup.Path}' does not declare a reference to '{parent.Id}'.", index, lookup.Path));
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

            // Nor did a join that fetched the parent without the member the lookup joins on.
            if (lookup.On is not null && shape.NotSelected(lookup.On + "." + parentKey.Wire) is not null)
            {
                errors.Add(Error(Codes.UnknownPath,
                    $"'{lookup.On}.{parentKey.Wire}' is not in the select of '{lookup.On}'; the lookup joins on it, so add '{parentKey.Wire}' to that select.", index, lookup.On));
                return;
            }

            var childScope = ScopeOf(child, context.Organisation!.Value);

            if (childScope is null)
            {
                errors.Add(Error(Codes.AccessDenied, $"'{child.Id}' has no organisation member; it cannot be joined.", index, null));
                return;
            }

            var select = BindSelect(lookup.Select, child, childShape, index);
            var filter = lookup.Filter?.Condition is null ? null : BindCondition(lookup.Filter.Condition, childShape, index);
            var sortFields = lookup.Sort is { Count: > 0 } entries ? BindSortEntries(entries, childShape, index) : [];
            var first = lookup.First is true;
            int limit;

            if (first)
            {
                // One child is what first takes; a limit would say something else.
                if (lookup.Limit is not null)
                    errors.Add(Error(Codes.OptionNotApplicable, "'limit' does not apply to a lookup with 'first', which takes the first child or null.", index, null));

                limit = 1;
            }
            else
            {
                limit = lookup.Limit ?? options.Limits.MaxLookupLimit;

                if (limit < 1 || limit > options.Limits.MaxLookupLimit)
                    errors.Add(Error(Codes.LookupLimitExceeded, $"A lookup returns at most {options.Limits.MaxLookupLimit} children per parent; '{limit}' is outside that.", index, null));
            }

            var parentKeyStorage = parentPrefix.Length == 0 ? parentKey.Storage! : parentPrefix + "." + parentKey.Storage;

            stages.Add(new BoundStage.Lookup(child, childPath, alias, select, filter, limit, childScope, parentKeyStorage, childPath.Storage!,
                sortFields, first, lookup.On, index));
            var fetched = select.Select(path => path.Wire).ToList();

            shape = shape.WithRoot(alias, first ? new ShapeNode.Entity(child, alias) { Select = fetched } : new ShapeNode.Array(child, alias) { Select = fetched });
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

        // ---- remote lookup (DESIGN §3.4.4) ------------------------------------------------------

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
        /// A lookup whose <c>from</c> is another service's entity (DESIGN §3.4.4). It runs as the keyed
        /// fetch runs a remote resolve, and is bound as one: the key of every page row is the parent's
        /// key, the target member is the child's <c>path</c>, and the owner answers grouped per key
        /// (<c>keyedBy</c>, at most one row more than the limit per key, ranked by the lookup's
        /// <c>sort</c>). What lies on the child — its path, select, filter, sort, owning-row select — is
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
                    errors.Add(Error(Codes.OptionNotApplicable,
                        $"'parentAs' names the row that owns an element; '{from}' is an entity, which is its own row. Look up '{entityId}#<collection>' to join elements.", index, null));
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
            else if (lookup.ParentSelect is not null)
            {
                errors.Add(Error(Codes.OptionNotApplicable, "'parentSelect' applies with 'parentAs', which names the owning row it selects from.", index, null));
                return;
            }

            var childPath = lookup.Path?.Trim() ?? "";

            if (childPath.Length == 0 || childPath.Split('.').Any(segment => segment.Length == 0))
            {
                errors.Add(Error(Codes.InvalidPath, $"'{lookup.Path}' is not a path; a lookup names the child's member that references '{parent.Id}'.", index, lookup.Path));
                return;
            }

            // The keys are the parent's keys: the owner's reference names the parent entity, whose
            // key it holds (a remote reference targets the key, DESIGN §3.3.2).
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

            if (lookup.On is not null && shape.NotSelected(keyWire) is not null)
            {
                errors.Add(Error(Codes.UnknownPath,
                    $"'{keyWire}' is not in the select of '{lookup.On}'; the lookup joins on it, so add '{key.Wire}' to that select.", index, lookup.On));
                return;
            }

            if (shape.Resolve(keyWire, PathUsage.Match) is not { Succeeded: true, Path: { Storage: not null } parentKey })
            {
                errors.Add(Error(Codes.UnknownPath, $"'{keyWire}' is not in the row here; the lookup joins on it.", index, lookup.On));
                return;
            }

            var first = lookup.First is true;
            int limit;

            if (first)
            {
                if (lookup.Limit is not null)
                    errors.Add(Error(Codes.OptionNotApplicable, "'limit' does not apply to a lookup with 'first', which takes the first child or null.", index, null));

                limit = 1;
            }
            else
            {
                limit = lookup.Limit ?? options.Limits.MaxLookupLimit;

                if (limit < 1 || limit > options.Limits.MaxLookupLimit)
                    errors.Add(Error(Codes.LookupLimitExceeded, $"A lookup returns at most {options.Limits.MaxLookupLimit} children per parent; '{limit}' is outside that.", index, null));
            }

            foreach (var entry in lookup.Sort ?? [])
                if (string.IsNullOrWhiteSpace(entry.Path))
                    errors.Add(Error(Codes.InvalidPath, "A sort entry of a lookup names a path of the child.", index, null));

            // What the wire form cannot carry to the owner is refused here, not dropped on the way;
            // every variable is bound here, since the owner never receives them (DESIGN §3.5.5).
            if (RefuseUnknownOptions(lookup.Filter?.Condition, index))
                return;

            var substitution = new List<QueryValidationError>();
            var filter = lookup.RawFilter is { } rawFilter ? coercer.SubstituteVariables(rawFilter, index, lookup.Path, substitution) : (JsonElement?)null;

            errors.AddRange(substitution);

            if (++lookups > options.Limits.MaxLookupStages)
                errors.Add(Error(Codes.MaxLookupStagesExceeded, $"The pipeline has more than {options.Limits.MaxLookupStages} lookup stages.", index, null));

            if (errors.Count > 0 && errors.Any(error => error.Stage == index))
                return;

            var spec = new BoundRemoteLookup(from, entityId, item, childPath, parent.Id, lookup.On, lookup.Sort ?? [], first, limit, Rows: item is null,
                lookup.Select, lookup.ParentSelect, filter);
            var declared = new ReferenceTarget(entityId, childPath, item, IsRemote: true, FieldIsKey: false);
            var target = new BoundResolveTarget(declared, null, null, null, null, lookup.Select, null, filter, null, null, lookup.ParentSelect, []);
            var reference = new ReferenceDef { Targets = [declared], DeclaredBy = ReferenceSource.Declaration };
            var stage = new BoundStage.Resolve(parentKey, alias, entityId, childPath, IsRemote: true,
                null, null, null, null, null, lookup.Select, filter,
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
                var resolution = targetShape.Resolve(wire, PathUsage.Select);

                if (!resolution.Succeeded)
                {
                    errors.Add(Error(resolution.Code!, resolution.Message!, index, wire));
                    continue;
                }

                paths.Add(resolution.Path!);
            }

            if (target.Key is not null && paths.All(path => path.Wire != target.Key.Wire))
                paths.Insert(0, targetShape.Resolve(target.Key.Wire, PathUsage.Select).Path!);

            return paths;
        }

        // ---- resolve -------------------------------------------------------------------------

        /// <summary>
        /// A resolve (DESIGN §3.4.1): the members, the collection guard, the declared cases with
        /// <c>target</c> and <c>parentAs</c>, the executor, and the shape of the alias. A resolve
        /// under a keyed or remote alias continues the chain at that alias's owner
        /// (<see cref="BindContinued"/>); a keyed or remote resolve's own alias and owning row
        /// become aliases later stages continue under.
        /// </summary>
        private async Task BindResolveAsync(ResolveStage resolve, int index)
        {
            // Step 1: the members. The 2.1 members are contract 2; under contract 1 they are
            // members the stage does not have, as is any of them written with the wrong kind.
            var contract2Members = new (string Name, bool Written)[]
            {
                ("elements", resolve.Elements is not null), ("target", resolve.Target is not null), ("parentAs", resolve.ParentAs is not null),
                ("parentSelect", resolve.ParentSelect is not null), ("onMissing", resolve.OnMissing is not null), ("forTarget", resolve.ForTarget is not null),
            };
            IReadOnlyList<string> unknown = contract2
                ? resolve.Unknown
                : [.. resolve.Unknown, .. contract2Members.Where(member => member.Written).Select(member => member.Name), .. resolve.Malformed];

            if (unknown.Count > 0)
            {
                errors.Add(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                    $"'{string.Join(", ", unknown)}' is not a member of resolve; a resolve carries {(contract2 ? "path, as, select, filter, elements, target, parentAs, parentSelect, onMissing, forTarget" : "path, as, select, filter")}."
                    + Hint(contract2Members.Any(member => member.Written) || resolve.Malformed.Count > 0), index, null));
                return;
            }

            if (resolve.Malformed.Count > 0)
            {
                foreach (var name in resolve.Malformed)
                    errors.Add(Error(Codes.UnknownStageMember, name switch
                    {
                        "elements" => "A resolve's 'elements' is \"first\" or \"all\".",
                        "onMissing" => "A resolve's 'onMissing' is \"null\", \"report\" or \"refuse\".",
                        "parentSelect" => "A resolve's 'parentSelect' is an array of paths.",
                        _ => $"A resolve's '{name}' is a string.",
                    }, index, null));
                return;
            }

            // Step 3: under a keyed or remote alias the stage is a continuation of it, run at its
            // owner (DESIGN §3.5.3); it is not a resolve of this host and does not count towards
            // MaxResolveStages.
            var path = resolve.Path ?? "";
            var head = path.Split('.')[0];

            if (anchors.TryGetValue(head, out var anchor))
            {
                BindContinued(new PipelineStage { Resolve = resolve, Keys = ["resolve"] }, index, path, anchor, resolve.ForTarget, [resolve.As, resolve.ParentAs]);
                return;
            }

            if (++resolves > options.Limits.MaxResolveStages)
                errors.Add(Error(Codes.MaxResolveStagesExceeded, $"The pipeline has more than {options.Limits.MaxResolveStages} resolve stages.", index, null));

            // forTarget picks one target of an alias a stage continues under; a resolve this host
            // runs continues nothing.
            if (resolve.ForTarget is not null)
            {
                errors.Add(Error(Codes.OptionNotApplicable,
                    "'forTarget' applies to a stage continued under a remote alias with several targets; this resolve runs on this host.", index, null));
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
            else if (resolve.ParentSelect is not null)
            {
                errors.Add(Error(Codes.OptionNotApplicable, "'parentSelect' applies with 'parentAs', which names the owning row it selects from.", index, null));
                return;
            }

            var resolution = shape.Resolve(path, PathUsage.Match);

            if (!resolution.Succeeded)
            {
                errors.Add(Error(resolution.Code!, resolution.Message!, index, resolve.Path));
                return;
            }

            var reference = resolution.Path!;

            // Step 4: the declared cases.
            if (reference.Path?.References is not { Count: > 0 } declaredCases)
            {
                errors.Add(Error(Codes.ResolveNotDeclared, $"'{resolve.Path}' declares no reference; it is {Kinds.WithArticle(reference.Kind)}.", index, resolve.Path));
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
                errors.Add(Error(Codes.OptionNotApplicable,
                    $"'elements' applies to a path through a collection that is not unwound; '{resolve.Path}' holds one value per row.", index, resolve.Path));
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
            // A strict request refuses a missing reference unless the stage says otherwise (DESIGN §3.4.3).
            var effectiveOnMissing = onMissing ?? (contract2 && request.IsStrict ? ResolveOnMissing.Refuse : ResolveOnMissing.Null);

            // The cases with their targets bound; every target is bound, whichever executor runs it.
            var cases = new List<BoundResolveCase>();
            var errorsBefore = errors.Count;

            filterBoundOnce = false;
            reportedFilterError = false;

            // The filter as every owner is sent it, local SelfOwner included: variables bound here,
            // since an owner never receives them (DESIGN §3.5.5). A local target's own binding of the
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
            // aggregate detects itself, so neither changes the executor or what binds (DESIGN §3.0).
            var first = cases[0].Targets[0];
            var inline = cases is [{ Declared: { When: null, KeyAs: KeyAs.None }, Targets: [{ IsRemote: false, Declared.Item: null }] }]
                && elements is null;
            var anyRemote = cases.Any(bound => bound.Targets.Any(target => target.IsRemote));

            var stage = new BoundStage.Resolve(reference, alias, first.Declared.Entity, first.Declared.Field, anyRemote,
                first.Entity, first.FieldStorage, first.Select, first.Filter, first.Scope,
                first.IsRemote ? resolve.Select : null, first.IsRemote ? sentFilter : null,
                inline ? ResolveExecutor.Inline : ResolveExecutor.Keyed, cases, elements, collectionStorage, narrowedTo, parentAs, onMissing, effectiveOnMissing, index);

            stages.Add(stage);

            // Step 6: the shape. An inline alias is a row of the aggregate; a keyed local one is
            // joined after the page and checked here; a remote one is the owner's.
            if (inline)
                shape = shape.WithRoot(alias, new ShapeNode.Entity(first.Entity!, alias) { Select = first.Select?.Select(path => path.Wire).ToList() });
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
        /// A resolve or lookup under a keyed or remote alias (DESIGN §3.4.1 step 3, §3.5.3): bound
        /// as a <see cref="ContinuedStage"/> of the keyed stage whose owner query carries it, which
        /// the owner binds with its own model. Checked here: contract 2, the alias still in the row,
        /// not under an <c>elements: "all"</c> alias (the per-element association would be lost),
        /// <c>forTarget</c> naming a target of the keyed stage's alias (under an alias a continued
        /// stage added, it names a target of that alias, which only the owner knows: it travels there
        /// and the owner checks it; the target the stage goes to here is the one the alias inherited),
        /// at most <c>MaxContinuedStages</c> per keyed stage, the new aliases free at the origin — so no continued alias collides with an origin alias — and every
        /// variable bound, since the owner never receives <c>variables</c> (DESIGN §3.5.5).
        /// </summary>
        private void BindContinued(PipelineStage raw, int index, string root, ContinuationAnchor anchor, string? forTarget, IReadOnlyList<string?> aliases)
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
            var effective = anchor.ForTarget;

            // Under an alias a continued stage added, forTarget names a target of that alias, whose join
            // the owner binds with its own model (DESIGN §3.5.3): the stage carries it to the owner, which
            // checks it and applies it to its own keyed stage, and an owner's refusal maps back here.
            // This host sends the stage wherever the alias it continues under goes.
            if (forTarget is not null && !anchor.Nested)
            {
                if (!targets.Contains(forTarget, StringComparer.Ordinal))
                {
                    errors.Add(Error(Codes.OptionNotApplicable,
                        $"'forTarget' names '{forTarget}', which is not a target of '{anchor.Stage.As}'; its targets are {string.Join(", ", targets.Select(target => $"'{target}'"))}.", index, root));
                    return;
                }

                effective = forTarget;
            }

            var count = continuedPerAnchor.GetValueOrDefault(anchor.Stage.As) + 1;

            if (count > options.Limits.MaxContinuedStages)
            {
                errors.Add(Error(Codes.MaxContinuedStagesExceeded,
                    $"More than {options.Limits.MaxContinuedStages} stages continue under '{anchor.Stage.As}'.", index, root));
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
            stages.Add(new ContinuedStage(anchor.Stage.As, stage, index, effective, added));

            // The added aliases are the owner's rows under the origin row: projected, never
            // filtered or sorted here, and further stages under them continue at the same owner.
            var many = raw.Resolve?.Elements == "all";

            foreach (var alias in added)
            {
                shape = shape.WithRoot(alias, new ShapeNode.Remote(raw.Lookup?.From ?? raw.Resolve?.Target ?? anchor.Stage.TargetEntity, anchor.Stage.Reference, alias, SemiJoinable: false));
                anchors[alias] = new ContinuationAnchor(anchor.Stage, effective, many, Nested: true);
            }
        }

        /// <summary>
        /// Refuses every option of a condition the engine does not know, or wrote with a value that is
        /// not a boolean, at <paramref name="index"/> (DESIGN §3.5.3): the wire form carries only the
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
                    errors.Add(Error(Codes.OptionNotApplicable, $"'{string.Join(", ", unknown)}' is not an option; an option is ignoreCase or caseSensitive, written true or false.", index, node.Path));
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
            var effective = forTarget ?? anchor.ForTarget;
            var targets = (anchor.Stage.Cases ?? []).SelectMany(bound => bound.Targets).Where(target => effective is null || target.Declared.Entity == effective).ToList();

            return targets.Count > 0 && targets.All(target => target.Declared.Item is not null);
        }

        /// <summary>The target entities of a keyed stage's selected cases, in declaration order.</summary>
        private static List<string> TargetsOf(BoundStage.Resolve stage) =>
            (stage.Cases ?? []).SelectMany(bound => bound.Targets).Select(target => target.Declared.Entity).DefaultIfEmpty(stage.TargetEntity).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>
        /// Whether a path lies under an alias whose rows come from an owner after the page, where
        /// only a resolve or a lookup continues (DESIGN §3.5.3): an unwind or a group there is
        /// <c>NOT_CONTINUABLE</c>. True when it was refused.
        /// </summary>
        private bool RefusedUnderAnchor(string? path, string kind, int index)
        {
            var head = path?.Split('.')[0];

            if (head is null || !anchors.ContainsKey(head))
                return false;

            errors.Add(Error(Codes.NotContinuable,
                $"'{kind}' cannot run on '{head}', which comes from its owner after the page: only resolve and lookup continue a chain there. Aggregate chain data in the report.", index, path));
            return true;
        }

        /// <summary>
        /// A continued alias the final row shows needs its keyed stage fetched, and the fetch runs
        /// only for a keyed stage whose alias or owning row the row shows: a projection that keeps a
        /// continued alias but drops both is refused rather than answered with an alias that is
        /// always null.
        /// </summary>
        private void CheckContinuedAnchorsKept()
        {
            foreach (var continued in stages.OfType<ContinuedStage>())
            {
                // A group after it replaced the row, and the continued alias with it.
                if (!anchors.TryGetValue(continued.Anchor, out var anchored))
                    continue;

                var anchor = anchored.Stage;

                if (shape.Carries(anchor.As) || (anchor.ParentAs is { } parentAs && shape.Carries(parentAs)))
                    continue;

                if (continued.Aliases.FirstOrDefault(shape.Carries) is { } shown)
                    errors.Add(Error(Codes.NotContinuable,
                        $"'{shown}' continues at the owner of '{anchor.As}', which the projection drops; keep '{anchor.As}'{(anchor.ParentAs is null ? "" : $" or '{anchor.ParentAs}'")} in the projection as well.", continued.OriginIndex, shown));
            }
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
                    var sibling = shape.Resolve(siblingWire, PathUsage.Project);

                    // The member is declared but the join above it did not fetch it: the fix is the
                    // join's select, as for any other path under the alias, not the declaration.
                    if (!sibling.Succeeded && shape.NotSelected(siblingWire) is { } join)
                    {
                        var relative = siblingWire[(join.Alias.Length + 1)..];

                        errors.Add(Error(Codes.UnknownPath,
                            $"The reference on '{reference.Wire}' picks its target by '{siblingWire}', which is not in the select of '{join.Alias}', which fetched {string.Join(", ", join.Select.Select(path => $"'{path}'"))}; add '{relative}' to that select.",
                            index, siblingWire));
                        return null;
                    }

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
                    var holding = holder.Length == 0 ? null : shape.Resolve(holder, PathUsage.Project);
                    var type = holding is { Succeeded: true } ? OperandCoercer.VariantHolder(holding.Path!) : holder.Length == 0 ? reference.Entity?.Root : null;

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

                default:
                    return null;
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

                return new BoundResolveTarget(remote, null, null, null, null, resolve.Select, null, sentFilter, null, null,
                    withParent ? resolve.ParentSelect : null, []);
            }

            await LoadAddonsAsync(target);

            var entityShape = Shape.ForEntity(target, addons);
            var at = entityShape;
            string? itemStorage = null;

            if (declared.Item is not null)
            {
                var collection = entityShape.Resolve(declared.Item, PathUsage.Unwind);

                if (!collection.Succeeded || collection.Path!.Storage is null)
                {
                    errors.Add(Error(Codes.ResolveNotDeclared, $"The reference targets the items '{declared.Item}' of '{target.Id}', which are not stored.", index, resolve.Path));
                    return null;
                }

                itemStorage = collection.Path.Storage;
                at = entityShape.ForElement(collection.Path!);
            }

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
                if (at.Resolve(wire, PathUsage.Select) is { Succeeded: true } kept)
                    select.Add(kept.Path!);
                else
                    dropped.Add(wire);
            }

            var keyWire = declared.Item is null ? target.Key?.Wire : declared.Field;

            if (keyWire is not null && select.All(kept => kept.Wire != keyWire) && at.Resolve(keyWire, PathUsage.Select) is { Succeeded: true } key)
                select.Insert(0, key.Path!);

            var filter = resolve.Filter?.Condition is null ? null : BindTargetFilter(resolve.Filter.Condition, at, index);
            // The owning row's select is flat as well: a path this target's entity lacks is dropped
            // for it (refused below only when no target has it).
            IReadOnlyList<ResolvedPath>? parentSelect = null;
            List<string>? parentSent = null;
            var parentDropped = new List<string>();

            if (withParent && declared.Item is not null)
            {
                if (resolve.ParentSelect is { Count: > 0 } parentWanted)
                {
                    var parentPaths = new List<ResolvedPath>();

                    parentSent = [];

                    foreach (var wire in parentWanted)
                    {
                        if (entityShape.Resolve(wire, PathUsage.Select) is { Succeeded: true } kept)
                        {
                            parentPaths.Add(kept.Path!);
                            parentSent.Add(wire);
                        }
                        else
                        {
                            parentDropped.Add(wire);
                        }
                    }

                    if (target.Key is not null && parentPaths.All(kept => kept.Wire != target.Key.Wire))
                        parentPaths.Insert(0, entityShape.Resolve(target.Key.Wire, PathUsage.Select).Path!);

                    parentSelect = parentPaths;
                }
                else
                {
                    parentSelect = BindSelect(null, target, entityShape, index);
                }
            }

            // The filter and the owning row's select also travel: the keyed fetch asks this host's
            // own SelfOwner with an ordinary owner query, which binds them again — the owning row's
            // select without the paths this target lacks.
            return new BoundResolveTarget(declared, target, field.Path.Storage, itemStorage, select, null, filter, sentFilter, scope, parentSelect,
                parentSent, dropped, parentDropped);
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
                var once = BindCondition(condition, at, index);

                filterBoundOnce = true;
                reportedFilterError = errors.Count > before;

                return once;
            }

            var (conditionsBefore, diagnosticsBefore, exactBefore) = (conditions, diagnostics.Count, exactTexts.Count);
            var errorsBefore = errors.Count;
            var bound = BindCondition(condition, at, index);

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

            foreach (var wire in targets[0].DroppedParentSelect ?? [])
                if (targets.All(target => target.DroppedParentSelect?.Contains(wire, StringComparer.Ordinal) == true))
                    errors.Add(Error(Codes.UnknownPath, $"'{wire}' is not a path of any row that owns a target of '{resolve.Path}' ({string.Join(", ", targets.Select(target => target.Declared.Entity).Distinct(StringComparer.Ordinal))}).", index, wire));

            foreach (var wire in targets[0].DroppedSelect)
            {
                if (!targets.All(target => target.DroppedSelect.Contains(wire, StringComparer.Ordinal)))
                    continue;

                var first = targets[0];
                var at = Shape.ForEntity(first.Entity!, addons);

                if (first.Declared.Item is not null && at.Resolve(first.Declared.Item, PathUsage.Unwind) is { Succeeded: true } collection)
                    at = at.ForElement(collection.Path!);

                var failure = at.Resolve(wire, PathUsage.Select);

                errors.Add(Error(failure.Code ?? Codes.UnknownPath, failure.Message ?? $"'{wire}' is not a path of '{first.Declared}'.", index, wire));
            }
        }

        // ---- unwind --------------------------------------------------------------------------

        private void BindUnwind(UnwindStage unwind, int index)
        {
            // flatten and keepPath are contract 2 members; under contract 1 they are ones the stage does not have.
            IReadOnlyList<string> unknown = contract2
                ? unwind.Unknown
                : [.. unwind.Unknown, .. unwind.Flatten is not null ? ["flatten"] : Array.Empty<string>(), .. unwind.KeepPathWritten ? ["keepPath"] : Array.Empty<string>()];

            if (!CheckStageMembers(unknown, "unwind", contract2 ? "path, as, preserveNull, includeIndex, flatten, keepPath" : "path, as, preserveNull, includeIndex", index, Hint(unwind.Flatten is not null || unwind.KeepPathWritten)))
                return;

            if (unwind.Path is null)
            {
                errors.Add(Error(Codes.UnknownPath, "An unwind names the collection to unwind under 'path'.", index, null));
                return;
            }

            if (unwind.KeepPathInvalid)
            {
                errors.Add(Error(Codes.InvalidOperand, "'keepPath' is true or false.", index, unwind.Path));
                return;
            }

            if (++unwinds > options.Limits.MaxUnwindStages)
                errors.Add(Error(Codes.MaxUnwindStagesExceeded, $"The pipeline has more than {options.Limits.MaxUnwindStages} unwind stages.", index, null));

            if (shape.Grouped)
            {
                errors.Add(Error(Codes.NotACollection, "The shape after a group has no collections.", index, unwind.Path));
                return;
            }

            if (RefusedUnderAnchor(unwind.Path, "unwind", index))
                return;

            var resolution = shape.Resolve(unwind.Path, PathUsage.Unwind);

            if (!resolution.Succeeded)
            {
                errors.Add(Error(resolution.Code!, resolution.Message!, index, unwind.Path));
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

            if (!unwind.KeepPath && alias is null)
            {
                errors.Add(Error(Codes.OptionNotApplicable, $"'keepPath' false takes '{unwind.Path}' out of the row once its element is under 'as'; without 'as' the element replaces '{unwind.Path}' in place, so there is nothing to drop.", index, unwind.Path));
                return;
            }

            if (!unwind.KeepPath && path.Path is null)
            {
                errors.Add(Error(Codes.OptionNotApplicable, $"'keepPath' applies to a collection member; '{unwind.Path}' is a join alias, which an unwind without 'as' already replaces by its element.", index, unwind.Path));
                return;
            }

            // A join bound earlier may read its keys off the page rows after the page is taken
            // (a keyed resolve, a join after the page); the collection keepPath false removes
            // before the page would take those keys with it and null every such alias.
            if (!unwind.KeepPath && JoinReadingUnder(path.Storage) is { } reader)
            {
                errors.Add(Error(Codes.OptionNotApplicable, $"'keepPath' false would take '{unwind.Path}' out of the row, but the join '{reader}' of an earlier stage reads its keys from it; keep the collection (keepPath true), or join after the unwind through '{alias}'.", index, unwind.Path));
                return;
            }

            BoundFlatten? flatten = null;

            if (unwind.Flatten is not null && (flatten = BindFlatten(unwind, path, index)) is null)
                return;

            stages.Add(new BoundStage.Unwind(path, alias, unwind.PreserveNull, indexAlias, flatten, unwind.KeepPath));
            shape = shape.WithUnwound(path, rootName, alias, indexAlias, unwind.KeepPath);
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

            var keys = new List<GroupKey>();
            var fields = new List<Aggregate>();
            var aliases = new HashSet<string>(StringComparer.Ordinal);

            if (group.By.Count + group.Fields.Count > options.Limits.MaxGroupFields)
                errors.Add(Error(Codes.MaxGroupFieldsExceeded, $"The group has {group.By.Count + group.Fields.Count} fields; the limit is {options.Limits.MaxGroupFields}.", index, null));

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

                var resolution = shape.Resolve(by.Path, PathUsage.GroupKey);

                if (!resolution.Succeeded)
                {
                    errors.Add(Error(resolution.Code!, resolution.Message!, index, by.Path));
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

                if (function == "count")
                {
                    fields.Add(new Aggregate(alias, function, null, Kind.Unknown, Kind.Long, null));
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
                    "countDistinct" => (Kind.Long, null),
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

            shape = shape.WithGroup(keys.Select(key => new ShapeNode.GroupOutput(key.OutputKind, key.OutputShape, key.As))
                .Concat(fields.Select(field => field.Function == "push"
                    ? new ShapeNode.GroupOutput(field.OutputKind, null, field.As, field.ArgumentKind, field.OutputShape)
                    : new ShapeNode.GroupOutput(field.OutputKind, field.OutputShape, field.As))));
            sort = null;
        }

        private DateTrunc? BindDateTrunc(DateTruncExpression trunc, int index)
        {
            if (RefusedUnderAnchor(trunc.Path, "group", index))
                return null;

            var resolution = shape.Resolve(trunc.Path, PathUsage.GroupKey);

            if (!resolution.Succeeded)
            {
                errors.Add(Error(resolution.Code!, resolution.Message!, index, trunc.Path));
                return null;
            }

            var path = resolution.Path!;

            if (path.CollectionAncestors > 0 || path.Kind is not (Kind.DateTime or Kind.Date))
            {
                errors.Add(Error(path.CollectionAncestors > 0 ? Codes.GroupOnCollection : Codes.InvalidAggregateArgument,
                    $"'{trunc.Path}' is not a date under no collection.", index, trunc.Path));
                return null;
            }

            if (!Units.Contains(trunc.Unit ?? ""))
            {
                errors.Add(Error(Codes.InvalidDateTruncUnit, $"'{trunc.Unit}' is not a unit; one of year, quarter, month, week, day, hour, minute, second.", index, trunc.Path));
                return null;
            }

            var timezone = string.IsNullOrWhiteSpace(trunc.Timezone) ? "UTC" : trunc.Timezone.Trim();

            if (timezone != "UTC")
            {
                // Mongo's $dateTrunc takes an IANA id, spelled exactly. The runtime's lookup is
                // looser: it ignores case on some platforms and also finds a zone by its Windows
                // id. What goes on the wire is therefore the id of the zone that was found when
                // that is an IANA id, the IANA id the runtime maps a Windows id to otherwise,
                // and nothing when there is no such mapping.
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone) || !TryIanaId(zone, out var iana))
                {
                    errors.Add(Error(Codes.InvalidTimezone, $"'{timezone}' is not an IANA timezone.", index, trunc.Path));
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

        private BoundExpression? BindExpression(QueryExpression expression, int index, string function, out Kind kind, out ShapeDef? shapeDef)
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
                // A group over a keyed or remote alias's data is the chain's, which only a report aggregates (DESIGN §3.5.3).
                if (RefusedUnderAnchor(expression.Path, "group", index))
                    return null;

                var resolution = shape.Resolve(expression.Path!, PathUsage.Aggregate);

                if (!resolution.Succeeded)
                {
                    errors.Add(Error(resolution.Code!, resolution.Message!, index, expression.Path));
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
                return new BoundExpression.Path(path);
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
                var operands = new List<BoundExpression>();
                var numeric = Kind.Long;

                foreach (var operand in expression.Operands ?? [])
                {
                    var bound = BindExpression(operand, index, function, out var operandKind, out _);

                    if (bound is null)
                        return null;

                    if (operandKind is Kind.Double or Kind.Decimal)
                        numeric = operandKind == Kind.Decimal || numeric == Kind.Decimal ? Kind.Decimal : Kind.Double;

                    operands.Add(bound);
                }

                if (operands.Count == 0)
                {
                    errors.Add(Error(Codes.InvalidAggregateArgument, $"'{expression.Operator}' needs operands.", index, null));
                    return null;
                }

                kind = expression.Operator == "coalesce" ? Kind.Unknown : numeric;
                return new BoundExpression.Arithmetic(expression.Operator!, operands);
            }

            var constant = OperandCoercer.Literal(expression.Literal ?? OperandCoercer.JsonNull);

            kind = KindOf(constant);
            return new BoundExpression.Literal(constant);
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
            var project = ExpandDefault(rawProject, index);

            if (project is null)
                return;

            if (project.Fields.Count == 0)
            {
                errors.Add(Error(Codes.MixedProjection, "A projection names at least one path.", index, null));
                return;
            }

            if (project.Fields.Count > options.Limits.MaxProjectionFields)
                errors.Add(Error(Codes.MaxProjectionFieldsExceeded, $"The projection names {project.Fields.Count} paths; the limit is {options.Limits.MaxProjectionFields}.", index, null));

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
                var resolution = shape.Resolve(wire, PathUsage.Project);

                if (!resolution.Succeeded)
                {
                    errors.Add(Error(resolution.Code!, resolution.Message!, index, wire));
                    continue;
                }

                paths.Add(resolution.Path!);
            }

            if (inclusion && idIncluded && !shape.Grouped)
            {
                var id = shape.Resolve(WireNames.IdWire, PathUsage.Project);

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

            return new ProjectStage { Fields = fields };
        }

        // ---- sort ----------------------------------------------------------------------------

        private void BindSort(IReadOnlyList<Models.SortField> fields, int index)
        {
            var bound = BindSortEntries(fields, shape, index);

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
        private List<BoundSortField> BindSortEntries(IReadOnlyList<Models.SortField> fields, Shape at, int index)
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

                var resolution = at.Resolve(field.Path, PathUsage.Sort);

                if (!resolution.Succeeded)
                {
                    errors.Add(Error(resolution.Code!, resolution.Message!, index, field.Path));
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
                    errors.Add(Error(Codes.OptionNotApplicable, $"'caseSensitive' applies to a sort on a string member; '{field.Path}' is {Kinds.WithArticle(path.LeafKind)}.", index, field.Path));
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

            errors.Add(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                $"'{string.Join(", ", unknown)}' is not a member of {stage}; {stage} carries {members}.{hint}", index, null));

            return false;
        }

        // ---- page ----------------------------------------------------------------------------

        private void BindPage(PageStage stage, int index)
        {
            if (!CheckStageMembers(stage.Unknown, "page", "limit, offset, cursor, includeTotalCount", index))
                return;

            if (page is not null)
            {
                errors.Add(Error(Codes.MultiplePageStages, "A pipeline has at most one page stage.", index, null));
                return;
            }

            var limit = stage.Limit ?? options.Limits.DefaultPageSize;

            // A report page (DESIGN §3.4.3): a strict request that neither continues nor jumps
            // reads its rows in one page, up to MaxReportPageSize, and refuses rather than cut them.
            var report = contract2 && request.IsStrict && stage.Cursor is null && stage.Offset is null;
            var maximum = report ? Math.Max(options.Limits.MaxPageSize, options.Limits.MaxReportPageSize) : options.Limits.MaxPageSize;

            if (limit < 1)
                errors.Add(Error(Codes.InvalidPageLimit, $"The page limit {limit} is not positive.", index, null));
            else if (limit > maximum)
                errors.Add(Error(Codes.PageSizeExceeded, report
                    ? $"The page limit {limit} exceeds the report page maximum of {maximum}."
                    : $"The page limit {limit} exceeds the maximum of {maximum}." + ReportPageNote(), index, null));

            // A report page multiplies what its lookups fetch: each brings up to its limit of
            // child rows for every row of the page. Beyond the ordinary page the joined rows are
            // held to what an ordinary page can reach at most, so the larger page never carries
            // more than the 2.0 host could.
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
                    errors.Add(Error(Codes.PageSizeExceeded, $"The report page of {limit} rows with lookups of up to {perRow} child rows per row could join {joined} rows; the limit is {budget}. Lower the page limit or the lookups' limits.", index, null));
            }

            var offset = stage.Offset ?? 0;

            if (offset < 0)
                errors.Add(Error(Codes.InvalidPageLimit, $"The offset {offset} is negative.", index, null));
            else if (offset > options.Limits.MaxOffset)
                errors.Add(Error(Codes.MaxOffsetExceeded, $"The offset {offset} exceeds the maximum of {options.Limits.MaxOffset}; use a cursor beyond it.", index, null));

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

            page = new BoundStage.Page(Math.Max(1, limit), Math.Max(0, offset), null, stage.IncludeTotalCount, countCap);
            pageIndex = index;
            stages.Add(page);
        }

        /// <summary>Where a larger page is available: a strict contract 2 request without cursor or offset reads up to <c>MaxReportPageSize</c>.</summary>
        private string ReportPageNote() =>
            contract2 && options.Limits.MaxReportPageSize > options.Limits.MaxPageSize
                ? $" A strict request without cursor or offset may ask for up to {options.Limits.MaxReportPageSize}."
                : "";

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>Whether a path holds text the collation folds: a string member that is not a single character stored as its code point.</summary>
        private static bool FoldsAsText(ResolvedPath path) => path.LeafKind == Kind.String && !OperandCoercer.IsCharRepresented(path);

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
    internal sealed class CursorException : Exception;
}

internal static class ResolvedPathExtensions
{
    public static ReferenceDef? Reference(this ResolvedPath path) => path.Path?.Reference;
}
