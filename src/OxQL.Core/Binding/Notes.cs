using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Core.Binding;

/// <summary>
/// The engine-behaviour notes of an explain (DESIGN §4.4): what the engine does with a request that
/// binds, said once where it applies, never an error and never a change to the rows. A note has the
/// form of a diagnostic (<c>{ code, message, stage, path, params }</c>); the studio renders
/// <c>oxqlStudio.note.&lt;CODE&gt;</c> when it has the key, else the message. The codes are not in
/// <see cref="Codes"/>: a note is no refusal and no diagnostic of a run, with one exception, a
/// select path an owner said its target lacks (<see cref="SelectPathNotOnTarget"/>), which only a
/// run can learn and which the run's diagnostics carry.
/// </summary>
public static class Notes
{
    /// <summary>A string comparison, sort or group key folds case and accents under the host's collation (params <c>locale</c>, <c>strength</c>, <c>op</c>).</summary>
    public const string TextFolds = "TEXT_FOLDS";

    /// <summary>A folding comparison that runs as a pattern folds case only, not accents (params <c>op</c>).</summary>
    public const string PatternFoldsCaseOnly = "PATTERN_FOLDS_CASE_ONLY";

    /// <summary>An exact sort makes every string comparison, sort and group key of the request exact as well (params <c>op</c>).</summary>
    public const string ExactForcesExact = "EXACT_FORCES_EXACT";

    /// <summary>A condition through a collection that is not unwound matches when some element matches (params <c>op</c>).</summary>
    public const string SomeElement = "SOME_ELEMENT";

    /// <summary><c>neq</c>/<c>nin</c> also match rows where the member is absent or null (params <c>op</c>).</summary>
    public const string NeqMatchesAbsent = "NEQ_MATCHES_ABSENT";

    /// <summary>The path exists only on some variants of a polymorphic value (params <c>variants</c>).</summary>
    public const string OnlyForVariants = "ONLY_FOR_VARIANTS";

    /// <summary>The path lies in an embedded copy of another entity, not its current record (params <c>entity</c>).</summary>
    public const string SnapshotCopy = "SNAPSHOT_COPY";

    /// <summary>A join runs in the aggregate before the page is taken (params <c>alias</c>, <c>kind</c>).</summary>
    public const string JoinBeforePage = "JOIN_BEFORE_PAGE";

    /// <summary>A join runs after the page is taken, for the page's rows only (params <c>alias</c>, <c>kind</c>).</summary>
    public const string JoinAfterPage = "JOIN_AFTER_PAGE";

    /// <summary>What lies under a remote or continued alias is bound by the owner, not here (params <c>alias</c>, <c>services</c>).</summary>
    public const string OwnerBinds = "OWNER_BINDS";

    /// <summary>A continued part could not be checked at its owner (E13).</summary>
    public const string RemoteUnchecked = "REMOTE_UNCHECKED";

    /// <summary>A flat select path one target of a union lacks is dropped for that target (params <c>alias</c>: the resolve's <c>as</c>, <c>target</c>, <c>parent</c>: a <c>parentSelect</c> path).</summary>
    public const string SelectPathNotOnTarget = "SELECT_PATH_NOT_ON_TARGET";

    /// <summary>The total count stops at a cap (params <c>cap</c>).</summary>
    public const string CountCap = "COUNT_CAP";

    /// <summary>A lookup, or a resolve collecting every element, holds at most a number of rows per parent (params <c>alias</c>, <c>limit</c>).</summary>
    public const string LookupLimit = "LOOKUP_LIMIT";

    /// <summary>The rows page by offset, which may repeat or skip rows between pages and ends at a maximum offset (params <c>maxOffset</c>).</summary>
    public const string OffsetPaging = "OFFSET_PAGING";

    /// <summary>What a resolve does with data loss (params <c>alias</c>, <c>onMissing</c>, <c>strict</c>, <c>dataLoss</c>).</summary>
    public const string MissingPolicy = "MISSING_POLICY";

    /// <summary>A strict request that neither continues nor jumps reads every row in one page or refuses (params <c>limit</c>, <c>max</c>).</summary>
    public const string ReportPage = "REPORT_PAGE";

    /// <summary>One line of the opt-in index advisory (params <c>field</c>, <c>used</c>, <c>index</c>).</summary>
    public const string IndexAdvice = "INDEX_ADVICE";

    /// <summary>
    /// A lookup of another service's entity runs at its owner after the page, grouped per key and
    /// ranked there, within bounds the owner enforces (DESIGN §3.4.4; params <c>alias</c>,
    /// <c>service</c>, <c>entity</c>, <c>perKey</c>, <c>keysPerQuery</c>, <c>sorted</c>).
    /// </summary>
    public const string RemoteLookup = "REMOTE_LOOKUP";

    /// <summary>Every note code, in the order DESIGN §4.4 lists them; the studio's key list equals it.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TextFolds, PatternFoldsCaseOnly, ExactForcesExact, SomeElement, NeqMatchesAbsent, OnlyForVariants, SnapshotCopy,
        JoinBeforePage, JoinAfterPage, OwnerBinds, RemoteUnchecked, SelectPathNotOnTarget, CountCap, LookupLimit,
        OffsetPaging, MissingPolicy, ReportPage, IndexAdvice, RemoteLookup,
    ];

    /// <summary>The outcomes that lose data (DESIGN §3.6), in the table's order.</summary>
    public static readonly IReadOnlyList<string> DataLossOutcomes = ["ambiguous", "not_found", "invalid_key", "owner_unanswered"];

    /// <summary>
    /// The caller's stage index of each bound stage, by position: as the binder recorded them, or for a
    /// pipeline built without it the caller's stages in order, except a default sort the binder
    /// inserted (null, and every later stage one back) and a default page it appended (null).
    /// </summary>
    public static IReadOnlyList<int?> CallerIndexes(BoundPipeline bound, QueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(request);

        // The binder records each bound stage's caller index, which also holds when a caller stage
        // bound to no stage (an empty match); a pipeline built elsewhere is read by position.
        if (bound.CallerIndexes is { } recorded && recorded.Count == bound.Stages.Count)
            return recorded;

        var written = request.Pipeline.Count;
        var defaultSort = bound.Sort is not null && !request.Pipeline.Any(stage => stage.Sort is not null) ? bound.Sort : null;
        var indexes = new List<int?>(bound.Stages.Count);
        var shift = 0;

        foreach (var stage in bound.Stages)
        {
            if (defaultSort is not null && ReferenceEquals(stage, defaultSort))
            {
                indexes.Add(null);
                shift = 1;
                continue;
            }

            var index = indexes.Count - shift;

            indexes.Add(index < written ? index : null);
        }

        return indexes;
    }

    /// <summary>
    /// The notes a bound request earns from its bound form alone: folding, element and absence
    /// semantics of its conditions, variant-only and snapshot paths, what owners bind, dropped select
    /// paths, caps and limits, paging, the missing policy of each resolve and the report page. The
    /// join-placement notes (<see cref="Join"/>) and the index advice come from the compile and are
    /// added by the engine.
    /// </summary>
    public static List<Diagnostic> Of(BoundPipeline bound, QueryRequest request, IReadOnlyList<int?> indexes, bool strict, int contract, OxQLOptions options)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(options);

        var collector = new Collector(bound, contract, options);

        for (var position = 0; position < bound.Stages.Count; position++)
        {
            var stage = indexes[position];

            switch (bound.Stages[position])
            {
                case BoundStage.Match match:
                    collector.Condition(match.Condition, stage);
                    break;

                case BoundStage.Lookup lookup:
                    if (lookup.Filter is not null)
                        collector.Condition(lookup.Filter, stage);

                    foreach (var field in lookup.ChildSort ?? [])
                        collector.Sort(field, stage);

                    if (!lookup.First)
                        collector.Add(LookupLimit, stage, null, $"'{lookup.As}' holds at most {lookup.Limit} children per row; a row with more keeps the first {lookup.Limit} and is reported (LOOKUP_TRUNCATED).",
                            new() { ["alias"] = lookup.As, ["limit"] = lookup.Limit });
                    break;

                // A lookup of another service's entity: its owner binds the child's members, and the
                // limit, the placement and the owner's bounds are what the caller should know.
                case BoundStage.Resolve { RemoteLookup: { } remoteLookup } resolve:
                    collector.Add(OwnerBinds, stage, resolve.As,
                        $"'{resolve.As}' comes from another service; its owner binds the path, the select, the filter, the sort and every path under the alias.",
                        new() { ["alias"] = resolve.As, ["services"] = new List<string> { remoteLookup.Service }, ["forTarget"] = null });

                    if (!remoteLookup.First)
                        collector.Add(LookupLimit, stage, null, $"'{resolve.As}' holds at most {remoteLookup.Limit} children per row; a row with more keeps the first {remoteLookup.Limit} and is reported (LOOKUP_TRUNCATED).",
                            new() { ["alias"] = resolve.As, ["limit"] = remoteLookup.Limit });
                    break;

                case BoundStage.Resolve resolve:
                    collector.Resolve(resolve, stage, strict);
                    break;

                case ContinuedStage continued:
                    collector.Add(OwnerBinds, stage, continued.Root,
                        $"This {continued.Kind} continues under '{continued.Anchor}' at its owner, which binds and runs it{(continued.ForTarget is { } only ? $" for rows resolved to '{only}' only" : "")}.",
                        new() { ["alias"] = continued.Anchor, ["services"] = collector.ServicesOf(continued.Anchor), ["forTarget"] = continued.ForTarget });
                    break;

                case BoundStage.Unwind unwind:
                    collector.Path(unwind.Path, stage);
                    break;

                case BoundStage.Group group:
                    foreach (var key in group.Keys)
                    {
                        if (key.Path is { } path)
                            collector.Path(path, stage);

                        if (contract == 2 && bound.Collated && key.OutputKind == Kind.String && key.Path is { } folded && !OperandCoercer.IsCharRepresented(folded))
                            collector.Folds(stage, folded.Wire, "groupKey");
                    }
                    break;

                case BoundStage.Project project:
                    foreach (var path in project.Paths)
                        collector.Path(path, stage);
                    break;

                case BoundStage.Sort sort:
                    foreach (var field in sort.Fields)
                        collector.Sort(field, stage);
                    break;
            }
        }

        if (bound.Page.IncludeTotalCount)
        {
            var cap = bound.Page.CountCap ?? options.Limits.CountCap;

            collector.Add(CountCap, PageIndex(request), null, $"The total count stops at {cap}; above it the count is the cap (TOTAL_COUNT_CAPPED).", new() { ["cap"] = cap });
        }

        if (bound.PagingMode == PagingMode.Offset)
        {
            var reason = bound.Stages.Select((stage, position) => (stage, position)).FirstOrDefault(pair => pair.stage is BoundStage.Unwind or BoundStage.Group);

            collector.Add(OffsetPaging, reason.stage is null ? null : indexes[reason.position], null,
                $"After an unwind or a group the rows page by offset, up to offset {options.Limits.MaxOffset}; rows written between two pages may repeat or be skipped.",
                new() { ["maxOffset"] = options.Limits.MaxOffset });
        }

        if (strict && IsReportPage(request))
        {
            var max = Math.Max(options.Limits.MaxPageSize, options.Limits.MaxReportPageSize);

            collector.Add(ReportPage, PageIndex(request), null,
                $"A strict request without cursor or offset reads every matching row in its one page of {bound.Page.Limit}, or refuses (PAGE_INCOMPLETE); the page may be raised up to {max}.",
                new() { ["limit"] = bound.Page.Limit, ["max"] = max });
        }

        return collector.Notes;
    }

    /// <summary>Where a join runs (DESIGN §4.3 <c>phase</c>): in the aggregate before the page, or after it.</summary>
    public static Diagnostic Join(int? stage, string alias, string kind, bool afterPage) => new()
    {
        Code = afterPage ? JoinAfterPage : JoinBeforePage,
        Message = afterPage
            ? $"'{alias}' is joined after the page is taken, for the page's rows only."
            : $"'{alias}' is joined in the aggregate before the page is taken, for every candidate row: a later stage reads it.",
        Stage = stage,
        Path = alias,
        Params = new Dictionary<string, object?> { ["alias"] = alias, ["kind"] = kind },
    };

    /// <summary>
    /// How a lookup of another service's entity runs (DESIGN §3.4.4): one owner query per
    /// <paramref name="keysPerQuery"/> parent keys of the page, at most <c>perKey</c> rows per key,
    /// ranked at the owner by the lookup's sort and the child key over every child of those keys that
    /// passes the filter. No index serves that per-key ranking, so what bounds it is said: the keys per
    /// query, the rows per key, the owner's page, its time ceiling and its sort memory
    /// (<c>QUERY_TOO_EXPENSIVE</c>), each enforced by the owner itself.
    /// </summary>
    public static Diagnostic RemoteLookupBounds(int? stage, string alias, BoundRemoteLookup lookup, int keysPerQuery) => new()
    {
        Code = RemoteLookup,
        Message = $"'{alias}' is looked up at '{lookup.Service}' after the page is taken: one owner query per {keysPerQuery} keys, at most {lookup.PerKey} "
            + $"{(lookup.PerKey == 1 ? "row" : "rows")} per key, ranked there by {(lookup.Sort.Count > 0 ? "the lookup's sort, then the child key" : "the child key")} over every child of those keys"
            + $"{(lookup.Filter is null ? "" : " that passes the filter")}. No index serves that ranking; the owner bounds it by its page, its time ceiling and its sort memory (QUERY_TOO_EXPENSIVE), and refuses what exceeds its own limits.",
        Stage = stage,
        Path = alias,
        Params = new Dictionary<string, object?>
        {
            ["alias"] = alias,
            ["service"] = lookup.Service,
            ["entity"] = lookup.From,
            ["perKey"] = lookup.PerKey,
            ["keysPerQuery"] = keysPerQuery,
            ["sorted"] = lookup.Sort.Count > 0,
        },
    };

    /// <summary>
    /// A flat select path one target of a union lacks, dropped for that target: at binding for a local
    /// target; for a remote one by its owner's internal explain (the remote check), and by a run,
    /// which keeps what it learned in the cache and reports it from there too.
    /// </summary>
    public static Diagnostic SelectPathDropped(int? stage, string alias, string target, string path, bool parent) => new()
    {
        Code = SelectPathNotOnTarget,
        Message = parent
            ? $"'{path}' is not a member of the row owning '{target}'; the owning row of '{alias}' carries it only for the other targets."
            : $"'{path}' is not a member of '{target}'; '{alias}' carries it only for the other targets.",
        Stage = stage,
        Path = path,
        Params = new Dictionary<string, object?> { ["alias"] = alias, ["target"] = target, ["parent"] = parent },
    };

    /// <summary>One line of the opt-in index advisory as a note.</summary>
    public static Diagnostic Index(string field, bool? used, string? index, string? note) => new()
    {
        Code = IndexAdvice,
        Message = note ?? (used switch
        {
            true => $"'{field}' is served by the index '{index}'.",
            false => $"No index serves '{field}'.",
            _ => $"Whether an index serves '{field}' is not known.",
        }),
        Path = field,
        Params = new Dictionary<string, object?> { ["field"] = field, ["used"] = used, ["index"] = index },
    };

    /// <summary>The effective <c>onMissing</c> as the wire spells it.</summary>
    public static string WireName(ResolveOnMissing onMissing) => onMissing switch
    {
        ResolveOnMissing.Report => "report",
        ResolveOnMissing.Refuse => "refuse",
        _ => "null",
    };

    private static int? PageIndex(QueryRequest request)
    {
        var index = request.Pipeline.ToList().FindIndex(stage => stage.Page is not null);

        return index < 0 ? null : index;
    }

    private static bool IsReportPage(QueryRequest request) =>
        request.Pipeline.Select(stage => stage.Page).FirstOrDefault(page => page is not null) is not { } page
        || (page.Cursor is null && page.Offset is null);

    /// <summary>Collects the notes once per (code, stage, path).</summary>
    private sealed class Collector(BoundPipeline bound, int contract, OxQLOptions options)
    {
        private readonly HashSet<(string, int?, string?)> seen = [];

        public List<Diagnostic> Notes { get; } = [];

        public void Add(string code, int? stage, string? path, string message, Dictionary<string, object?> parameters)
        {
            if (seen.Add((code, stage, path)))
                Notes.Add(new Diagnostic { Code = code, Message = message, Stage = stage, Path = path, Params = parameters });
        }

        /// <summary>The services owning the targets of a keyed or remote alias of this pipeline, in target order.</summary>
        public List<string> ServicesOf(string alias) =>
            bound.Stages.OfType<BoundStage.Resolve>().Where(resolve => resolve.As == alias || resolve.ParentAs == alias)
                .SelectMany(TargetsOf)
                .Select(target => target.Declared.Entity.Split('.')[0])
                .Distinct(StringComparer.Ordinal)
                .ToList();

        public void Resolve(BoundStage.Resolve resolve, int? stage, bool strict)
        {
            var targets = TargetsOf(resolve).ToList();

            foreach (var target in targets.Where(target => !target.IsRemote))
            {
                if (target.Filter is not null)
                    Condition(target.Filter, stage);

                foreach (var path in target.DroppedSelect)
                    Notes.Add(SelectPathDropped(stage, resolve.As, target.Declared.ToString(), path, parent: false));

                foreach (var path in target.DroppedParentSelect ?? [])
                    Notes.Add(SelectPathDropped(stage, resolve.As, target.Declared.ToString(), path, parent: true));
            }

            if (resolve.IsRemote)
                Add(OwnerBinds, stage, resolve.Reference.Wire,
                    $"'{resolve.As}' comes from another service; its owner binds the select, the filter and every path under the alias.",
                    new() { ["alias"] = resolve.As, ["services"] = targets.Where(target => target.IsRemote).Select(target => target.Declared.Entity.Split('.')[0]).Distinct(StringComparer.Ordinal).ToList(), ["forTarget"] = null });

            if (resolve.Elements == ResolveElements.All)
                Add(LookupLimit, stage, resolve.Reference.Wire, $"'{resolve.As}' collects at most {options.Limits.MaxLookupLimit} targets per row; a row with more keeps the first and is reported (RESOLVE_TRUNCATED).",
                    new() { ["alias"] = resolve.As, ["limit"] = options.Limits.MaxLookupLimit });

            var onMissing = WireName(resolve.EffectiveOnMissing);

            // An inline resolve joins one local entity in the aggregate: no owner, no conversion, so
            // no owner_unanswered or invalid_key; a second record exists only for a target field that
            // is not the key, and is seen only when the outcomes are read (RE-25).
            var inline = !resolve.IsRemote && resolve.Executor == ResolveExecutor.Inline;
            var nonKey = (resolve.Cases ?? []).SelectMany(bound => bound.Targets).Any(target => target.Declared.Item is not null || !target.Declared.FieldIsKey);
            var readsOutcomes = strict || resolve.EffectiveOnMissing != ResolveOnMissing.Null;
            var ambiguity = inline ? nonKey && readsOutcomes : true;
            var dataLoss = inline
                ? DataLossOutcomes.Where(outcome => outcome == "not_found" || (outcome == "ambiguous" && ambiguity)).ToList()
                : DataLossOutcomes.ToList();
            var what = resolve.EffectiveOnMissing switch
            {
                ResolveOnMissing.Refuse => "refuses the request",
                ResolveOnMissing.Report => "leaves the alias null and is reported (RESOLVE_MISSING)",
                _ when inline => "leaves the alias null",
                _ => "leaves the alias null; only an owner that fails is reported",
            };

            Add(MissingPolicy, stage, resolve.Reference.Wire,
                $"A reference of '{resolve.As}' that resolves to nothing {what} (onMissing {onMissing}{(strict ? ", strict" : "")}); {string.Join(", ", dataLoss)} lose data{(strict ? " and refuse under strict" : "")}."
                + (ambiguity ? " A key more than one record holds is always reported (RESOLVE_AMBIGUOUS)." : ""),
                new() { ["alias"] = resolve.As, ["onMissing"] = onMissing, ["strict"] = strict, ["dataLoss"] = dataLoss });
        }

        public void Condition(BoundCondition condition, int? stage)
        {
            switch (condition)
            {
                case BoundCondition.And and:
                    foreach (var inner in and.Conditions)
                        Condition(inner, stage);
                    break;

                case BoundCondition.Or or:
                    foreach (var inner in or.Conditions)
                        Condition(inner, stage);
                    break;

                case BoundCondition.Not not:
                    Condition(not.Condition, stage);
                    break;

                case BoundCondition.Any any:
                    Path(any.Path, stage);
                    Condition(any.Inner, stage);
                    break;

                case BoundCondition.Leaf { IsSemiJoin: true } semiJoin:
                    Add(OwnerBinds, stage, semiJoin.Path.Wire,
                        $"The condition on '{semiJoin.Path.Wire}' is asked of the owner before the page is taken; the owner binds it.",
                        new() { ["alias"] = semiJoin.Path.Root.StoragePrefix, ["services"] = ServicesOf(semiJoin.Path.Root.StoragePrefix), ["forTarget"] = null });
                    break;

                case BoundCondition.Leaf { Path.IsRemote: true }:
                    break;

                case BoundCondition.Leaf leaf:
                    Leaf(leaf, stage);
                    break;
            }
        }

        private void Leaf(BoundCondition.Leaf leaf, int? stage)
        {
            var wire = leaf.Path.Wire;

            Path(leaf.Path, stage);

            if (leaf.IgnoreCase == true && contract == 2)
            {
                if (bound.Collated && leaf.Op is not ("contains" or "endsWith"))
                    Folds(stage, wire, leaf.Op);
                else
                    Add(PatternFoldsCaseOnly, stage, wire, $"'{leaf.Op}' on '{wire}' runs as a pattern: it folds case but not accents.", new() { ["op"] = leaf.Op });
            }

            if (leaf.Op != "is" && (leaf.Path.CollectionAncestors > 0 || leaf.Path.Kind == Kind.Array))
                Add(SomeElement, stage, wire, $"'{wire}' lies in a collection that is not unwound: the row matches when some element matches.", new() { ["op"] = leaf.Op });

            if (leaf.Op is "neq" or "nin" && leaf.Operand is not BoundOperand.Null)
                Add(NeqMatchesAbsent, stage, wire, $"'{leaf.Op}' on '{wire}' also matches rows where the member is absent or null.", new() { ["op"] = leaf.Op });
        }

        public void Sort(BoundSortField field, int? stage)
        {
            Path(field.Path, stage);

            if (contract != 2 || field.Path.LeafKind != Kind.String || OperandCoercer.IsCharRepresented(field.Path))
                return;

            if (field.IgnoreCase)
                Folds(stage, field.Path.Wire, "sort");
            else
                Add(ExactForcesExact, stage, field.Path.Wire,
                    $"The sort on '{field.Path.Wire}' is exact: every other string comparison, sort and group key of the request must be caseSensitive too.",
                    new() { ["op"] = "sort" });
        }

        public void Folds(int? stage, string wire, string op)
        {
            var collation = options.Representation.Collation;

            Add(TextFolds, stage, wire, $"'{op}' on '{wire}' folds case and accents (collation {collation.Locale}, strength {collation.Strength}).",
                new() { ["locale"] = collation.Locale, ["strength"] = collation.Strength, ["op"] = op });
        }

        /// <summary>The variant-only and snapshot notes of a path of this host's model.</summary>
        public void Path(ResolvedPath path, int? stage)
        {
            if (path.Entity is not { } entity || path.Path is not { } model)
                return;

            var segments = model.Wire.Split('.');
            IReadOnlyList<string>? variants = null;
            string? snapshot = null;

            for (var length = 1; length <= segments.Length; length++)
            {
                if (entity.Path(string.Join('.', segments, 0, length)) is not { } prefix)
                    continue;

                variants ??= prefix.Member.OnlyFor;
                snapshot ??= prefix.Shape.SnapshotOf ?? prefix.Shape.Leaf.SnapshotOf;
            }

            if (variants is { Count: > 0 })
                Add(OnlyForVariants, stage, path.Wire, $"'{path.Wire}' exists only on the variants {string.Join(", ", variants)}; other rows have no value.",
                    new() { ["variants"] = variants });

            if (snapshot is not null)
                Add(SnapshotCopy, stage, path.Wire, $"'{path.Wire}' lies in a copy of '{snapshot}' taken when the record was written, not the current '{snapshot}'.",
                    new() { ["entity"] = snapshot });
        }

        private static IEnumerable<BoundResolveTarget> TargetsOf(BoundStage.Resolve resolve) =>
            resolve.Cases is { Count: > 0 } cases
                ? cases.SelectMany(selected => selected.Targets)
                : [new BoundResolveTarget(new ReferenceTarget(resolve.TargetEntity, resolve.TargetField, null, resolve.IsRemote, resolve.TargetField == "id"),
                    resolve.Target, resolve.TargetFieldStorage, null, resolve.Select, resolve.RemoteSelect, resolve.Filter, resolve.RemoteFilter, resolve.TargetScope, null, null, [])];
    }
}
