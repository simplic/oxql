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
                // The keys only: an aggregate is not unique per group and adds nothing to the
                // order. A string key folded into its group, so its order folds the same way.
                foreach (var key in stages.OfType<BoundStage.Group>().LastOrDefault()?.Keys ?? [])
                    if (shape.Resolve(key.As, PathUsage.Sort) is { Succeeded: true } resolved)
                        fields.Add(new BoundSortField(resolved.Path!, true, contract2 && key.OutputKind == Kind.String));
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
                Collated = collated,
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
                    errors.Add(Error(Codes.OptionNotApplicable, "'caseSensitive' is not an option.", index, condition.Path));
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

            if (string.IsNullOrWhiteSpace(lookup.From) || !binder.model.TryResolve(lookup.From, out var child, out var retired))
            {
                errors.Add(Error(Codes.UnknownEntity, $"'{lookup.From}' is not an entity of this host.", index, null));
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

            var firstSegment = unwind.Path.Split('.')[0];
            var rootName = shape.Roots.ContainsKey(firstSegment) && firstSegment != Shape.ImplicitRoot
                ? firstSegment
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
            shape = shape.WithGroup(keys.Select(key => new ShapeNode.GroupOutput(key.OutputKind, key.OutputShape, key.As))
                .Concat(fields.Select(field => field.Function == "push"
                    ? new ShapeNode.GroupOutput(field.OutputKind, null, field.As, field.ArgumentKind, field.OutputShape)
                    : new ShapeNode.GroupOutput(field.OutputKind, field.OutputShape, field.As))));
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

            iana = zone.Id;
            return true;
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
                    errors.Add(Error(Codes.InvalidSortDirection, "A sort entry is a path and a direction string, asc or desc; the object form is a contract 2 form.", index, field.Path));
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

            // The number form of includeTotalCount is the request's own count cap, under the
            // host's; contract 1 has the boolean form only.
            int? countCap = null;

            if (stage.TotalCountCap is { } cap)
            {
                if (context.Contract == 1)
                    errors.Add(Error(Codes.LegacyStageUnsupported, "'includeTotalCount' is true or false under contract 1; a count cap needs contract 2 (X-OxQL-Contract: 2).", index, null));
                else if (cap < 1)
                    errors.Add(Error(Codes.InvalidPageLimit, $"The count cap {cap} is not a positive integer; includeTotalCount is true, false or a positive integer.", index, null));
                else
                    countCap = Math.Min(cap, options.Limits.CountCap);
            }

            page = new BoundStage.Page(Math.Max(1, limit), Math.Max(0, offset), null, stage.IncludeTotalCount, countCap);
            pageIndex = index;
            stages.Add(page);
        }

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
