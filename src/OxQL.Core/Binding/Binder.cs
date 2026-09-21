using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly IReadOnlySet<string> Units = new HashSet<string>(StringComparer.Ordinal) { "year", "quarter", "month", "week", "day", "hour", "minute", "second" };
    private static readonly IReadOnlySet<string> WeekDays = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
    private static readonly IReadOnlySet<string> Aggregates = new HashSet<string>(StringComparer.Ordinal) { "sum", "avg", "min", "max", "first", "last", "push", "count", "countDistinct" };
    private static readonly IReadOnlySet<string> NumericAggregates = new HashSet<string>(StringComparer.Ordinal) { "sum", "avg" };

    private readonly EntityModel model;
    private readonly CursorCodec cursors;

    public Binder(EntityModel model, CursorCodec cursors)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
    }

    /// <summary>Binds one request.</summary>
    public async ValueTask<BindOutcome> BindAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken)
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

        if (errors.Count > 0)
            return new BindOutcome.Failed(Refusal.Validation(errors));

        try
        {
            return new BindOutcome.Bound(session.Result(scope));
        }
        catch (CursorException)
        {
            return new BindOutcome.Failed(Refusal.Validation([Error(Codes.CursorInvalid, "The cursor is not valid for this query: it was issued for another query, was altered, or is malformed.", null, null)]));
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
        private readonly Dictionary<string, IReadOnlyList<AddonDefinition>> addons = new(StringComparer.Ordinal);
        private Shape shape = null!;
        private BoundStage.Sort? sort;
        private BoundStage.Page? page;
        private int pageIndex = -1;
        private int lookups, unwinds, resolves, conditions;
        private bool hasSemiJoin;

        public async Task RunAsync()
        {
            await LoadAddonsAsync(entity);
            shape = Shape.ForEntity(entity, addons);

            if (request.Variables is not null && request.Variables.Values.Count > options.Limits.MaxVariables)
                errors.Add(Error(Codes.MaxVariablesExceeded, $"The request binds {request.Variables.Values.Count} variables; the limit is {options.Limits.MaxVariables}.", null, null));

            var pipeline = request.Pipeline ?? [];

            if (pipeline.Count > options.Limits.MaxPipelineStages)
                errors.Add(Error(Codes.MaxPipelineStagesExceeded, $"The pipeline has {pipeline.Count} stages; the limit is {options.Limits.MaxPipelineStages}.", null, null));

            for (var index = 0; index < pipeline.Count; index++)
            {
                var stage = pipeline[index];

                // A null element is a caller error, not a fault, and is refused with a code.
                if (stage is null)
                {
                    errors.Add(Error(Codes.UnknownStage, "A stage is an object carrying exactly one stage member; this one is null.", index, null));
                    continue;
                }

                // So is a known stage key whose value is null: the key is recorded, the member
                // is not, and no stage binds from nothing.
                if (stage.Kind is { } kind && !HasMember(stage, kind))
                {
                    errors.Add(Error(Codes.UnknownStageMember, $"'{kind}' is null; a {kind} stage carries {(kind == "sort" ? "an array of sort entries" : "an object")}.", index, null));
                    continue;
                }

                if (page is not null && stage.Kind != "page")
                {
                    errors.Add(Error(Codes.StageAfterPage, "No stage may follow the page stage.", index, null));
                    continue;
                }

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
            }

            if (conditions > options.Limits.MaxConditions)
                errors.Add(Error(Codes.MaxConditionsExceeded, $"The request has {conditions} conditions; the limit is {options.Limits.MaxConditions}.", null, null));

            if (errors.Count == 0)
                DefaultSort();

            if (page is null)
            {
                page = new BoundStage.Page(options.Limits.DefaultPageSize, 0, null, false);
                stages.Add(page);
            }
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
                foreach (var key in stages.OfType<BoundStage.Group>().LastOrDefault()?.Keys ?? [])
                    if (shape.Resolve(key.As, PathUsage.Sort) is { Succeeded: true } resolved)
                        fields.Add(new BoundSortField(resolved.Path!, true));
            }
            else if (shape.Resolve(Model.Build.WireNames.IdWire, PathUsage.Sort) is { Succeeded: true } key)
            {
                fields.Add(new BoundSortField(key.Path!, true));
            }

            // A group over no key yields exactly one row; there is nothing to order.
            if (fields.Count == 0)
                return;

            var stage = new BoundStage.Sort(fields);

            // The page stage is last when the caller wrote one, and stays last.
            if (page is not null)
                stages.Insert(stages.Count - 1, stage);
            else
                stages.Add(stage);

            sort = stage;
        }

        public BoundPipeline Result(BoundStage.Scope scope)
        {
            var mode = shape.IsRootShape ? PagingMode.Keyset : PagingMode.Offset;
            var fingerprint = BoundCanonical.Fingerprint(scope, stages, mode);

            // The cursor is verified against the finished fingerprint.
            if (pageIndex >= 0 && request.Pipeline![pageIndex].Page!.Cursor is { } cursor)
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
                FinalShape = shape,
                Sort = sort,
                Page = page!,
                PagingMode = mode,
                Diagnostics = diagnostics,
                Fingerprint = fingerprint,
                Canonical = BoundCanonical.Render(scope, stages, page, mode).ToJsonString(),
                HasSemiJoin = hasSemiJoin,
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

        private BoundCondition? BindCondition(FilterCondition condition, Shape at, int index)
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
            // operators pass the kind gate above and have nothing to match against; they used
            // to reach the compiler and throw InvalidCastException into a bare 500.
            if (!path.IsRemote && OperandCoercer.NeedsText(op) && OperandCoercer.IsCharRepresented(path))
            {
                errors.Add(Error(Codes.InvalidOperand, $"'{condition.Path}' holds a single character stored as its code point; '{op}' needs text. Compare it with eq, neq, in or nin.", index, condition.Path));
                return null;
            }

            var ignoreCase = false;

            if (condition.Options is { } conditionOptions)
            {
                if (conditionOptions.Unknown is { Count: > 0 } unknown)
                    errors.Add(Error(Codes.OptionNotApplicable, $"'{string.Join(", ", unknown)}' is not an option.", index, condition.Path));

                if (conditionOptions.IgnoreCase)
                {
                    if (path.IsRemote || OperandCoercer.IgnoreCaseApplies(op, path.LeafKind))
                        ignoreCase = true;
                    else
                        errors.Add(Error(Codes.OptionNotApplicable, $"'ignoreCase' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; '{condition.Path}' is a {Kinds.NameOf(path.LeafKind)} under '{op}'.", index, condition.Path));
                }
            }

            var operand = coercer.Coerce(condition.Value, path, op, index, errors);

            if (operand is null)
                return null;

            // A text operand that is matched as a pattern is escaped and anchored by the
            // compiler; one the database cannot compile is a caller error, refused here.
            if (!path.IsRemote && (ignoreCase || op is "contains" or "startsWith" or "endsWith") && TextsOf(operand).Any(text => !RegexGuard.LiteralFits(text)))
            {
                errors.Add(Error(Codes.InvalidOperand, $"The operand of '{op}' on '{condition.Path}' is too long to be matched as text.", index, condition.Path));
                return null;
            }

            if (op == "regex" && operand is BoundOperand.Single { Value: BsonString pattern } && !RegexGuard.IsAnchored(pattern.Value))
                diagnostics.Add(new Diagnostic { Code = Codes.RegexUnanchored, Message = $"The pattern on '{condition.Path}' is not anchored; it scans every value of the member.", Stage = index, Path = condition.Path });

            if (path.IsRemote)
                hasSemiJoin = true;

            return new BoundCondition.Leaf(path, op, operand, ignoreCase, path.IsRemote);
        }

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
            if (lookup.Unknown.Count > 0)
            {
                errors.Add(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                    $"'{string.Join(", ", lookup.Unknown)}' is not a member of lookup; a lookup carries from, path, as, select, filter, limit.", index, null));
                return;
            }

            if (++lookups > options.Limits.MaxLookupStages)
                errors.Add(Error(Codes.MaxLookupStagesExceeded, $"The pipeline has more than {options.Limits.MaxLookupStages} lookup stages.", index, null));

            if (shape.Grouped)
            {
                errors.Add(Error(Codes.UnknownPath, "The shape after a group has no key to join on.", index, null));
                return;
            }

            if (string.IsNullOrWhiteSpace(lookup.From) || !binder.model.TryResolve(lookup.From, out var child, out _))
            {
                errors.Add(Error(Codes.UnknownEntity, $"'{lookup.From}' is not an entity of this host.", index, null));
                return;
            }

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

            if (childPath.Reference() is not { } declared || declared.TargetEntity != entity.Id)
            {
                errors.Add(Error(Codes.LookupNotDeclared, $"'{child.Id}#{lookup.Path}' does not declare a reference to '{entity.Id}'.", index, lookup.Path));
                return;
            }

            var targetField = declared.TargetField;
            var parentKey = entity.Path(targetField);

            if (parentKey is null || !parentKey.Stored)
            {
                errors.Add(Error(Codes.LookupNotDeclared, $"The reference targets '{entity.Id}#{targetField}', which is not stored.", index, lookup.Path));
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
            var limit = lookup.Limit ?? options.Limits.MaxLookupLimit;

            if (limit < 1 || limit > options.Limits.MaxLookupLimit)
                errors.Add(Error(Codes.LookupLimitExceeded, $"A lookup returns at most {options.Limits.MaxLookupLimit} children per parent; '{limit}' is outside that.", index, null));

            stages.Add(new BoundStage.Lookup(child, childPath, alias, select, filter, limit, childScope, parentKey.Storage!, childPath.Storage!));
            shape = shape.WithRoot(alias, new ShapeNode.Array(child, alias));
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

        private async Task BindResolveAsync(ResolveStage resolve, int index)
        {
            if (resolve.Unknown.Count > 0)
            {
                errors.Add(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                    $"'{string.Join(", ", resolve.Unknown)}' is not a member of resolve; a resolve carries path, as, select, filter.", index, null));
                return;
            }

            if (++resolves > options.Limits.MaxResolveStages)
                errors.Add(Error(Codes.MaxResolveStagesExceeded, $"The pipeline has more than {options.Limits.MaxResolveStages} resolve stages.", index, null));

            if (!CheckAlias(resolve.As, index, out var alias))
                return;

            var resolution = shape.Resolve(resolve.Path ?? "", PathUsage.Match);

            if (!resolution.Succeeded)
            {
                errors.Add(Error(resolution.Code!, resolution.Message!, index, resolve.Path));
                return;
            }

            var reference = resolution.Path!;

            if (reference.IsRemote || reference.Reference() is not { } declared)
            {
                errors.Add(Error(Codes.ResolveNotDeclared, $"'{resolve.Path}' declares no reference.", index, resolve.Path));
                return;
            }

            if (declared.IsRemote || !binder.model.Entities.TryGetValue(declared.TargetEntity, out var target))
            {
                stages.Add(new BoundStage.Resolve(reference, alias, declared.TargetEntity, declared.TargetField, IsRemote: true,
                    null, null, null, null, null, resolve.Select, resolve.RawFilter));
                shape = shape.WithRoot(alias, new ShapeNode.Remote(declared.TargetEntity, reference, alias));
                return;
            }

            await LoadAddonsAsync(target);

            var targetShape = Shape.ForEntity(target, addons);
            var targetField = targetShape.Resolve(declared.TargetField, PathUsage.Match);

            if (!targetField.Succeeded || targetField.Path!.Storage is null)
            {
                errors.Add(Error(Codes.ResolveNotDeclared, $"The reference targets '{target.Id}#{declared.TargetField}', which is not stored.", index, resolve.Path));
                return;
            }

            var targetScope = ScopeOf(target, context.Organisation!.Value);

            if (targetScope is null)
            {
                errors.Add(Error(Codes.AccessDenied, $"'{target.Id}' has no organisation member; it cannot be resolved.", index, null));
                return;
            }

            var select = BindSelect(resolve.Select, target, targetShape, index);
            var filter = resolve.Filter?.Condition is null ? null : BindCondition(resolve.Filter.Condition, targetShape, index);

            stages.Add(new BoundStage.Resolve(reference, alias, target.Id, declared.TargetField, IsRemote: false,
                target, targetField.Path.Storage, select, filter, targetScope, null, null));
            shape = shape.WithRoot(alias, new ShapeNode.Entity(target, alias));
        }

        // ---- unwind --------------------------------------------------------------------------

        private void BindUnwind(UnwindStage unwind, int index)
        {
            if (!CheckStageMembers(unwind.Unknown, "unwind", "path, as, preserveNull, includeIndex", index))
                return;

            if (unwind.Path is null)
            {
                errors.Add(Error(Codes.UnknownPath, "An unwind names the collection to unwind under 'path'.", index, null));
                return;
            }

            if (++unwinds > options.Limits.MaxUnwindStages)
                errors.Add(Error(Codes.MaxUnwindStagesExceeded, $"The pipeline has more than {options.Limits.MaxUnwindStages} unwind stages.", index, null));

            if (shape.Grouped)
            {
                errors.Add(Error(Codes.NotACollection, "The shape after a group has no collections.", index, unwind.Path));
                return;
            }

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

            var rootName = shape.Roots.ContainsKey(unwind.Path.Split('.')[0]) && unwind.Path.Split('.')[0] != Shape.ImplicitRoot
                ? unwind.Path.Split('.')[0]
                : Shape.ImplicitRoot;

            stages.Add(new BoundStage.Unwind(path, alias, unwind.PreserveNull, indexAlias));
            shape = shape.WithUnwound(path, rootName, alias, indexAlias);
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
                if (!Identifier.IsMatch(by.As ?? ""))
                {
                    errors.Add(Error(Codes.InvalidAlias, $"'{by.As}' is not a plain identifier.", index, by.As));
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

                keys.Add(new GroupKey(by.As, path, null, path.Kind, path.Shape));
            }

            foreach (var (alias, aggregate) in group.Fields)
            {
                if (!Identifier.IsMatch(alias))
                {
                    errors.Add(Error(Codes.InvalidAlias, $"'{alias}' is not a plain identifier.", index, alias));
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
                    fields.Add(new Aggregate(alias, function, null, Kind.Long, null));
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

                var (outputKind, outputShape) = function switch
                {
                    "countDistinct" => (Kind.Long, null),
                    "avg" => (kind == Kind.Decimal ? Kind.Decimal : Kind.Double, null),
                    "push" => (Kind.Array, argumentShape),
                    _ => (kind, argumentShape),
                };

                fields.Add(new Aggregate(alias, function, argument, outputKind, outputShape));
            }

            stages.Add(new BoundStage.Group(keys, fields));
            shape = shape.WithGroup(keys.Select(key => (key.As, key.OutputKind, key.OutputShape))
                .Concat(fields.Select(field => (field.As, field.OutputKind, field.Function == "push" ? null : field.OutputShape))));
            sort = null;
        }

        private DateTrunc? BindDateTrunc(DateTruncExpression trunc, int index)
        {
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
                // The lookup is case-insensitive and Mongo's $dateTrunc is not, so a spelling
                // that passes here and travels on verbatim used to throw at execution and
                // leave the caller a bare 500. The zone the lookup found carries the canonical
                // id, which is the one that goes on the wire; a runtime without the IANA table
                // (globalization-invariant) reports no IANA id, and there the caller's
                // spelling is the best there is.
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone))
                {
                    errors.Add(Error(Codes.InvalidTimezone, $"'{timezone}' is not an IANA timezone.", index, trunc.Path));
                    return null;
                }

                if (zone.HasIanaId)
                    timezone = zone.Id;
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

        private BoundExpression? BindExpression(QueryExpression expression, int index, string function, out Kind kind, out ShapeDef? shapeDef)
        {
            kind = Kind.Unknown;
            shapeDef = null;

            // An object naming no operator the engine knows used to compile as a literal
            // object, which Mongo evaluates to missing: {"power": …}, {"bogus": 1} and {}
            // all answered 200 with a column of nulls under min, max, first, last and push,
            // and were caught under sum and avg only because those check the argument's kind.
            // An unknown aggregate function is refused; an unknown argument operator is too.
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

            var constant = OperandCoercer.Literal(expression.Literal ?? JsonDocument.Parse("null").RootElement);

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

            var includeId = inclusion ? !idExcluded : !idExcluded;

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

                var resolution = shape.Resolve(field.Path, PathUsage.Sort);

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

                bound.Add(new BoundSortField(path, ascending.Value));
            }

            var stage = new BoundStage.Sort(bound);

            stages.Add(stage);
            sort = stage;
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
        private bool CheckStageMembers(IReadOnlyList<string> unknown, string stage, string members, int index)
        {
            if (unknown.Count == 0)
                return true;

            errors.Add(Error(context.Contract == 1 ? Codes.LegacyStageUnsupported : Codes.UnknownStageMember,
                $"'{string.Join(", ", unknown)}' is not a member of {stage}; {stage} carries {members}.", index, null));

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

            if (limit < 1)
                errors.Add(Error(Codes.InvalidPageLimit, $"The page limit {limit} is not positive.", index, null));
            else if (limit > options.Limits.MaxPageSize)
                errors.Add(Error(Codes.PageSizeExceeded, $"The page limit {limit} exceeds the maximum of {options.Limits.MaxPageSize}.", index, null));

            var offset = stage.Offset ?? 0;

            if (offset < 0)
                errors.Add(Error(Codes.InvalidPageLimit, $"The offset {offset} is negative.", index, null));
            else if (offset > options.Limits.MaxOffset)
                errors.Add(Error(Codes.MaxOffsetExceeded, $"The offset {offset} exceeds the maximum of {options.Limits.MaxOffset}; use a cursor beyond it.", index, null));

            if (stage.Cursor is not null && stage.Offset is not null)
                errors.Add(Error(Codes.InvalidPageLimit, "A page continues from a cursor or jumps by an offset, not both.", index, null));

            page = new BoundStage.Page(Math.Max(1, limit), Math.Max(0, offset), null, stage.IncludeTotalCount);
            pageIndex = index;
            stages.Add(page);
        }

        // ---- helpers -------------------------------------------------------------------------

        private bool CheckAlias(string? alias, int index, out string checkedAlias)
        {
            checkedAlias = alias ?? "";

            if (string.IsNullOrEmpty(alias) || !Identifier.IsMatch(alias))
            {
                errors.Add(Error(Codes.InvalidAlias, $"'{alias}' is not a plain identifier.", index, alias));
                return false;
            }

            if (shape.IsTaken(alias))
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
