using MongoDB.Bson;
using MongoDB.Bson.Serialization.Options;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo.Resolve;

namespace OxQL.Mongo;

/// <summary>A slot in the page filter that a semi-join fills with the owner's ids before execution.</summary>
public sealed record SemiJoinSlot(BoundCondition.Leaf Leaf, BsonArray Ids);

/// <summary>
/// The field a flattening unwind writes into every row it produces: true when the collection the
/// row came from had items nested deeper than <paramref name="Depth"/> levels, which are not in
/// the rows. <paramref name="Stage"/> and <paramref name="Path"/> are the caller's, for the diagnostic.
/// </summary>
public sealed record FlattenProbe(string Field, int Stage, string Path, int Depth);

/// <summary>
/// The field a lookup writes into every row it joins: true when some parent had more children
/// than <paramref name="Limit"/>, of which only the first <paramref name="Limit"/> are under the
/// alias. <paramref name="Stage"/> and <paramref name="Alias"/> are the caller's, for the diagnostic.
/// </summary>
public sealed record LookupFlag(string Field, int Stage, string Alias, int Limit);

/// <summary>
/// An inline resolve whose outcomes the executor reads off the page rows (DESIGN §3.6): a row whose
/// reference at <paramref name="ReferenceStorage"/> holds a value and whose alias is null is
/// <c>not_found</c>, or <c>excluded</c> when <paramref name="ExistsFlag"/> says the unfiltered
/// target has the record (a filtered resolve); a row whose <paramref name="AmbiguityFlag"/> is set
/// joined a key two records hold (a target onto a member that is not its key) and is
/// <c>ambiguous</c>. The compiler keeps the reference and the flags in storage against projections.
/// </summary>
public sealed record InlineProbe(int Stage, BoundStage.Resolve Resolve, string ReferenceStorage, string? AmbiguityFlag = null, string? ExistsFlag = null);

/// <summary>
/// A strict request's check that no candidate row lost children to a truncation before a later
/// match filtered it (DESIGN §3.4.3): <paramref name="Stages"/> run the pipeline up to the stage that
/// sets the flag and keep one flagged row; any row means the page's answer may depend on what was
/// cut. <paramref name="Code"/> is <c>LOOKUP_TRUNCATED</c> or <c>UNWIND_DEPTH_TRUNCATED</c>, and
/// <paramref name="Stage"/>, <paramref name="Path"/> and <paramref name="Bound"/> (the limit or the
/// depth) are the caller's, for the refusal.
/// </summary>
public sealed record TruncationProbe(string Code, int Stage, string Path, int Bound, IReadOnlyList<BsonDocument> Stages);

/// <summary>What the compiler emits: the page and count pipelines, and what the executor still has to do.</summary>
public sealed record CompiledQuery
{
    public required BoundPipeline Bound { get; init; }

    public required IReadOnlyList<BsonDocument> PageStages { get; init; }

    public IReadOnlyList<BsonDocument>? CountStages { get; init; }

    public required int Limit { get; init; }

    public required int Offset { get; init; }

    public required PagingMode PagingMode { get; init; }

    public required bool IncludeTotalCount { get; init; }

    /// <summary>The sort fields whose values the next cursor carries.</summary>
    public required IReadOnlyList<BoundSortField> SortFields { get; init; }

    /// <summary>
    /// The resolves the keyed fetch runs after the page (DESIGN §3.5.2), in stage order: every
    /// resolve on the keyed executor, remote or local, whose alias or owning row the row shows.
    /// </summary>
    public required IReadOnlyList<BoundStage.Resolve> KeyedResolves { get; init; }

    public required IReadOnlyList<SemiJoinSlot> SemiJoins { get; init; }

    public required int MaxTimeMs { get; init; }

    public bool? AllowDiskUse { get; init; }

    /// <summary>The cap the count stops at: the request's own when it gave one, otherwise the host's.</summary>
    public required int CountCap { get; init; }

    /// <summary>
    /// The projection excluded the key and the compiler kept it anyway so the sort and the
    /// cursor have something to read. Contract 2 rows follow the shape and never show it;
    /// a contract 1 row is rendered from the document, so the executor drops it there.
    /// </summary>
    public bool KeyKeptAgainstProjection { get; init; }

    /// <summary>
    /// Storage paths of the paging sort the projection dropped and the compiler kept, for the
    /// same reason and with the same consequence as <see cref="KeyKeptAgainstProjection"/>.
    /// </summary>
    public IReadOnlyList<string> SortKeptAgainstProjection { get; init; } = [];

    /// <summary>
    /// The collation both aggregates run under, <c>{ locale, strength }</c>; null when nothing
    /// in the pipeline folds case, in which case the aggregates run as they always have.
    /// </summary>
    public BsonDocument? Collation { get; init; }

    /// <summary>The depth probes of the flattening unwinds, in stage order; the executor turns a set probe into <c>UNWIND_DEPTH_TRUNCATED</c>.</summary>
    public IReadOnlyList<FlattenProbe> FlattenProbes { get; init; } = [];

    /// <summary>The truncation flags of the lookups that return an array, in stage order; the executor turns a set flag into <c>LOOKUP_TRUNCATED</c>.</summary>
    public IReadOnlyList<LookupFlag> LookupFlags { get; init; } = [];

    /// <summary>The inline resolves whose effective <c>onMissing</c> is not <c>null</c> and whose alias the row shows; the executor reads their missing references off the page.</summary>
    public IReadOnlyList<InlineProbe> InlineProbes { get; init; } = [];

    /// <summary>The truncation checks of a strict request whose truncated alias a later match reads.</summary>
    public IReadOnlyList<TruncationProbe> TruncationProbes { get; init; } = [];
}

/// <summary>What the compiler needs from the host beside the bound pipeline; a null collation is the default one.</summary>
public sealed record CompileOptions(int MaxTimeMs, bool? AllowDiskUse, int CountCap, CollationOptions? Collation = null)
{
    /// <summary>
    /// Whether a projection or a join's select that keeps a member below a polymorphic object also
    /// keeps that object's discriminator, so the row is encoded by the variant it is stored as (a
    /// contract 2 row; a contract 1 row is the document as stored and would show it).
    /// </summary>
    public bool KeepDiscriminators { get; init; }
}

/// <summary>
/// Emits the aggregation pipeline for a bound pipeline: typed <c>$match</c>, string
/// comparisons under the collation or as patterns, <c>$elemMatch</c>, joins as indexed
/// <c>$lookup</c> with the scope inside and after the page when only the rows' display reads
/// them, null-aware keyset cursors merged into the leading scope match, offset paging after a
/// group, and the count pipeline with its <c>$limit</c> after any <c>$group</c>.
/// </summary>
public static class MongoCompiler
{
    private const string KeyStorage = Aliases.KeyStorage;

    /// <summary>The prefix of an unwind index the compiler adds for paging and removes again; no caller alias starts with it.</summary>
    private const string ReservedIndex = Aliases.ReservedPrefix + "oxIx";

    /// <summary>The prefix of a flattening unwind's depth probe; the row keeps it through projections and groups, and the wire row never shows it.</summary>
    private const string ReservedFlatten = Aliases.ReservedPrefix + "oxFlat";

    /// <summary>The prefix of a lookup's truncation flag; the row keeps it through projections and groups, and the executor removes it.</summary>
    private const string ReservedLookupFlag = Aliases.ReservedPrefix + "oxLk";

    /// <summary>The prefix of an inline resolve's ambiguity flag (a key two records hold); the row keeps it through projections, and the executor removes it.</summary>
    private const string ReservedAmbiguityFlag = Aliases.ReservedPrefix + "oxAmb";

    /// <summary>The prefix of a filtered inline resolve's existence flag (the unfiltered target has the record); the row keeps it through projections, and the executor removes it.</summary>
    private const string ReservedExistsFlag = Aliases.ReservedPrefix + "oxHas";

    /// <summary>The variable a join on an alias binds the parent's key to, so a parent the row does not hold joins no children.</summary>
    private const string ParentVariable = "oxParent";

    /// <summary>The field the <c>keyedBy</c> window numbers each key's rows in; the rows never keep it.</summary>
    private const string ReservedRank = Aliases.ReservedPrefix + "oxRank";

    /// <summary>The position of an item target's element in its collection, which orders two elements of one row; the rows never keep it.</summary>
    private const string ReservedElementIndex = Aliases.ReservedPrefix + "oxElIx";

    /// <summary>The variable the <c>keyedBy</c> prologue's filter reads each element under.</summary>
    private const string ElementVariable = "oxItem";

    /// <summary>The variable a join binds its local key to when the sub-pipeline has to compare it byte for byte.</summary>
    private const string KeyVariable = "oxKey";

    /// <summary>
    /// The character with the highest primary weight in the root collation; a string is
    /// below the prefix followed by it exactly when it starts with the prefix under the
    /// collation.
    /// </summary>
    private const char AfterEveryCharacter = '￿';

    /// <summary>How a string comparison is emitted.</summary>
    private enum Casing
    {
        /// <summary>A plain operator: the exact value, or the value under the aggregate's collation.</summary>
        Value,

        /// <summary>A plain operator under the collation, where a prefix is a range.</summary>
        Collated,

        /// <summary>An anchored pattern with the <c>i</c> flag: the fold inside an aggregate without a collation.</summary>
        Fold,

        /// <summary>An anchored pattern without a flag: the exact value inside a collated aggregate, which a plain operator would fold.</summary>
        Exact,
    }

    public static CompiledQuery Compile(BoundPipeline bound, CompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(options);

        var page = bound.Page;
        var stages = new List<BsonDocument>();
        var keyed = new List<BoundStage.Resolve>();
        var semiJoins = new List<SemiJoinSlot>();
        var sortFields = bound.Sort?.Fields ?? [];
        var collated = bound.Collated;

        // Rows of an unwound shape share their parent's key, so the key alone does not order
        // them: the index of every unwind completes it. A group replaces the row, and its keys
        // are the order, so this is the unwound case only.
        // `bound.Sort` is the sort the indexes complete; without one there is nothing to
        // complete, and an index injected here would reach the rows with nothing to remove it.
        var unwoundOffsetPaging = bound.PagingMode == PagingMode.Offset && !bound.FinalShape.Grouped && bound.Sort is not null;
        var reservedIndexes = new List<string>();
        var probes = new List<FlattenProbe>();
        var flags = new List<LookupFlag>();

        // The flags of the lookups that join before the page: only they are in the rows a
        // projection or a group reshapes. A late lookup writes its flag after both.
        var rowFlags = new List<string>();
        IReadOnlyList<BoundSortField>? pagingSort = null;

        // The key orders and identifies a page. A projection that drops it leaves the sort and
        // the cursor reading a member that is not there, so it is kept in storage and dropped
        // again on the way out.
        var keepKey = bound.PagingMode == PagingMode.Keyset || unwoundOffsetPaging;
        var keyKeptAgainstProjection = false;

        // The same rule as the key, for the sort. NextCursor mints each leg by reading the
        // value off the document it hands back, so the sort paths survive every projection in
        // storage: a leg read from a member that is not there is the value null, and a null
        // leg re-admits every non-null row, which pages the first page for ever.
        var sortStorages = keepKey
            ? sortFields.Select(field => field.Path.Storage).Where(storage => storage is not null).Select(storage => storage!).Distinct(StringComparer.Ordinal).ToList()
            : [];
        var sortKeptAgainstProjection = new List<string>();

        // The leading scope, with the keyset predicate merged in on a root shape.
        var scope = ScopeFilter(bound.Scope);
        var keyset = page.Cursor is { Mode: PagingMode.Keyset } cursor ? KeysetFrom(cursor, sortFields) : null;

        // A sort path under a local resolve alias has no value before the join writes it, so
        // the keyset condition on it is evaluated right before the paging sort instead, where
        // every sort path holds the value the cursor was minted from.
        var joinedKeyset = keyset is not null && bound.Sort is not null && sortFields.Any(field => field.Path.Root.StoragePrefix.Length > 0)
            ? keyset
            : null;

        if (joinedKeyset is not null)
            keyset = null;

        stages.Add(new BsonDocument("$match", keyset is null ? scope : new BsonDocument("$and", new BsonArray { scope, keyset })));

        var countStages = page.IncludeTotalCount ? new List<BsonDocument> { new("$match", scope) } : null;

        // An internal owner query grouped per key: the keys matched first, where an index serves
        // them, and the window after the caller's leading matches (the resolve's filter), so a
        // key's rows are numbered among those that pass it.
        var window = bound.KeyedBy is { } keyedBy ? KeyedByWindow(keyedBy, collated, WindowSort(bound)) : null;

        if (bound.KeyedBy is { } prologueOf)
        {
            var prologue = KeyedByPrologue(prologueOf, collated).ToList();

            stages.AddRange(prologue);
            countStages?.AddRange(prologue);
        }
        var sortEmitted = false;

        // A join only the rows' display reads runs after the page is taken: the sort stays
        // next to the limit, which lets the server keep the top rows instead of ordering
        // every candidate, and the join is paid per page row rather than per candidate row.
        var lateJoins = new List<BsonDocument>();

        // The local key each of those joins matches on. It is read only after the page, so it
        // survives every projection in between in storage, as a remote resolve's reference
        // does; the wire row follows the shape and leaves it out when it was not asked for.
        var lateJoinKeys = new List<string>();

        // The reference of an inline resolve whose missing rows are reported (DESIGN §3.6) is read
        // off the page rows as well, so it survives every projection in storage the same way.
        var inlineProbes = new List<InlineProbe>();
        var probeKeys = new List<string>();

        // A strict request refuses a truncation that a later match would hide by filtering the
        // truncated row out: the rows up to the flag are checked apart from the page.
        var truncationProbes = new List<TruncationProbe>();

        for (var index = 0; index < bound.Stages.Count; index++)
        {
            var stage = bound.Stages[index];
            var emitted = new List<BsonDocument>();

            if (window is not null && stage is not BoundStage.Match)
            {
                stages.AddRange(window);
                countStages?.AddRange(window);
                window = null;
            }

            // A join whose alias no later stage reads and the row does not show adds nothing
            // anyone sees, and a projection would only drop it again: it is not run at all.
            if (JoinAlias(stage) is { } joined && !JoinUsed(bound, index, joined))
                continue;

            switch (stage)
            {
                case BoundStage.Match match:
                    emitted.Add(new BsonDocument("$match", Filter(match.Condition, semiJoins, collated)));
                    break;

                // JoinsAfterPage decides the phase of a join, which explain reports (JOIN_BEFORE_PAGE /
                // JOIN_AFTER_PAGE); a join a later lookup's 'on' reads stays before the page unless
                // that lookup joins after the page as well (see JoinsAfterPage).
                case BoundStage.Lookup lookup when JoinsAfterPage(bound.Stages, index, lookup.As):
                    lateJoins.AddRange(Lookup(lookup, semiJoins, collated, Flag(lookup, flags), options.KeepDiscriminators));
                    lateJoinKeys.Add(lookup.ParentKeyStorage);
                    break;

                case BoundStage.Lookup lookup:
                    var flag = Flag(lookup, flags);

                    if (flag is not null)
                        rowFlags.Add(flag.Field);

                    emitted.AddRange(Lookup(lookup, semiJoins, collated, flag, options.KeepDiscriminators));

                    if (flag is not null && bound.Strict && FilteredLater(bound.Stages, index, lookup.As))
                        truncationProbes.Add(new TruncationProbe(Codes.LookupTruncated, lookup.Stage, lookup.As, lookup.Limit, Probing(stages, emitted, flag.Field)));
                    break;

                // The keyed fetch's rows only ever reach the wire row; a projection that dropped
                // the alias and the owning row leaves nothing to fetch. A match under a remote
                // alias is a semi-join, which fetches its ids apart from this.
                case BoundStage.Resolve keyedResolve when IsKeyed(keyedResolve) && !Shown(bound.FinalShape, keyedResolve.As) && !(keyedResolve.ParentAs is { } parentAs && Shown(bound.FinalShape, parentAs)):
                    break;

                case BoundStage.Resolve keyedResolve when IsKeyed(keyedResolve):
                    keyed.Add(keyedResolve);
                    break;

                case BoundStage.Resolve resolve when JoinsAfterPage(bound.Stages, index, resolve.As):
                    lateJoins.AddRange(LocalResolve(resolve, semiJoins, collated, Probe(bound, index, resolve, inlineProbes, probeKeys), options.KeepDiscriminators));
                    lateJoinKeys.Add(resolve.Reference.Storage!);
                    break;

                case BoundStage.Resolve resolve:
                    emitted.AddRange(LocalResolve(resolve, semiJoins, collated, Probe(bound, index, resolve, inlineProbes, probeKeys), options.KeepDiscriminators));
                    break;

                case BoundStage.Unwind unwind:
                    // The caller's index when it asked for one, otherwise a reserved one the
                    // paging sort uses and the pipeline drops again before it skips.
                    var indexField = unwind.IncludeIndex;

                    if (unwoundOffsetPaging && indexField is null)
                        reservedIndexes.Add(indexField = ReservedIndex + reservedIndexes.Count);
                    else if (unwoundOffsetPaging)
                        reservedIndexes.Add(indexField!);

                    if (unwind.Flatten is { } flatten)
                    {
                        var probe = new FlattenProbe(ReservedFlatten + probes.Count, flatten.Stage, unwind.Path.Wire, flatten.Depth);

                        probes.Add(probe);
                        emitted.AddRange(Flatten(unwind.Path.Storage!, flatten, probe.Field));
                    }

                    emitted.Add(new BsonDocument("$unwind", new BsonDocument
                    {
                        ["path"] = "$" + unwind.Path.Storage,
                        ["preserveNullAndEmptyArrays"] = unwind.PreserveNull,
                    }.AddIf(indexField is not null, "includeArrayIndex", indexField)));

                    if (unwind.As is not null)
                        emitted.Add(new BsonDocument("$set", new BsonDocument(unwind.As, "$" + unwind.Path.Storage)));

                    if (unwind.Flatten is { } cut && bound.Strict && FilteredLater(bound.Stages, index, unwind.As ?? unwind.Path.Wire))
                        truncationProbes.Add(new TruncationProbe(Codes.UnwindDepthTruncated, cut.Stage, unwind.Path.Wire, cut.Depth, Probing(stages, emitted, probes[^1].Field)));
                    break;

                case BoundStage.Group group:
                    emitted.AddRange(Group(group, [.. probes.Select(probe => probe.Field), .. rowFlags]));
                    break;

                case BoundStage.Project project:
                    if (Project(project, keyed, [.. lateJoinKeys, .. probeKeys.Where(key => !lateJoinKeys.Contains(key, StringComparer.Ordinal))], keepKey,
                        [.. reservedIndexes, .. probes.Select(probe => probe.Field), .. rowFlags, .. InlineFlags(inlineProbes), .. options.KeepDiscriminators && project.Inclusion ? DiscriminatorsOf(project.Paths) : []],
                        sortStorages, sortKeptAgainstProjection, ref keyKeptAgainstProjection) is { } projection)
                        emitted.Add(projection);
                    break;

                // The last sort is the one the page is taken in, so on an unwound shape it is
                // emitted after the loop with the tie-breakers that make it total. Any earlier
                // sort is the caller's own and stays where it was written.
                case BoundStage.Sort sort when unwoundOffsetPaging && ReferenceEquals(sort, bound.Sort):
                    pagingSort = sort.Fields;
                    break;

                case BoundStage.Sort sort:
                    if (joinedKeyset is not null && ReferenceEquals(sort, bound.Sort))
                        emitted.Add(new BsonDocument("$match", joinedKeyset));

                    emitted.Add(Sort(sort.Fields, bound.PagingMode == PagingMode.Keyset));
                    sortEmitted = true;
                    break;

                case BoundStage.Page:
                    break;
            }

            stages.AddRange(emitted);

            // The count pipeline carries everything up to the sort and the page; a join only
            // when a later match, unwind, group or resolve reads its alias. What only the
            // rows' display reads does not change how many rows there are.
            if (countStages is not null && stage is not (BoundStage.Sort or BoundStage.Page) && (JoinAlias(stage) is not { } alias || CountReads(bound.Stages.Skip(index + 1), alias)))
                countStages.AddRange(emitted);
        }

        if (window is not null)
        {
            stages.AddRange(window);
            countStages?.AddRange(window);
        }

        if (pagingSort is not null)
        {
            stages.Add(Sort(pagingSort, tieBreak: true, reservedIndexes));

            // What ordered the page and was never asked for has no business in a row. An offset
            // cursor carries only its offset, so the key is free to go here; a keyset cursor
            // reads it off the last row and keeps it (stripped from a contract 1 row instead).
            var drop = reservedIndexes.Where(name => name.StartsWith(ReservedIndex, StringComparison.Ordinal)).ToList();

            // A join after the page that matches on the key still needs it; the wire row leaves
            // it out by the shape.
            if (keyKeptAgainstProjection && !lateJoinKeys.Contains(KeyStorage, StringComparer.Ordinal))
            {
                drop.Add(KeyStorage);
                keyKeptAgainstProjection = false;
            }

            if (drop.Count > 0)
                stages.Add(new BsonDocument("$unset", new BsonArray(drop)));
        }
        else if (!sortEmitted && bound.PagingMode == PagingMode.Keyset)
        {
            stages.Add(new BsonDocument("$sort", new BsonDocument(KeyStorage, 1)));
        }

        var offset = page.Cursor is { Mode: PagingMode.Offset } offsetCursor ? offsetCursor.Offset : page.Offset;

        if (offset > 0)
            stages.Add(new BsonDocument("$skip", offset));

        stages.Add(new BsonDocument("$limit", page.Limit + 1));
        stages.AddRange(lateJoins);

        // The request's own count cap, when it gave one, is already under the host's.
        var countCap = page.CountCap ?? options.CountCap;

        if (countStages is not null)
        {
            countStages.Add(new BsonDocument("$limit", countCap + 1));
            countStages.Add(new BsonDocument("$count", "n"));
        }

        return new CompiledQuery
        {
            Bound = bound,
            PageStages = stages,
            CountStages = countStages,
            Limit = page.Limit,
            Offset = offset,
            PagingMode = bound.PagingMode,
            IncludeTotalCount = page.IncludeTotalCount,
            SortFields = sortFields,
            KeyedResolves = keyed,
            SemiJoins = semiJoins,
            MaxTimeMs = options.MaxTimeMs,
            AllowDiskUse = options.AllowDiskUse,
            CountCap = countCap,
            KeyKeptAgainstProjection = keyKeptAgainstProjection,
            SortKeptAgainstProjection = sortKeptAgainstProjection,
            Collation = collated ? CollationDocument(options.Collation ?? new CollationOptions()) : null,
            FlattenProbes = probes,
            LookupFlags = flags,
            InlineProbes = inlineProbes,
            TruncationProbes = truncationProbes,
        };
    }

    /// <summary>Whether a match after the stage at <paramref name="index"/> reads the alias: it may filter out a row the stage truncated.</summary>
    /// <remarks>An unwind of the alias under another name (<c>as</c>) carries the cut rows on under that name too (D-ENG-1).</remarks>
    private static bool FilteredLater(IReadOnlyList<BoundStage> stages, int index, string alias)
    {
        var names = new List<string> { alias };

        foreach (var stage in stages.Skip(index + 1).TakeWhile(stage => stage is not BoundStage.Group))
        {
            if (stage is BoundStage.Match && names.Any(name => Reads(stage, name)))
                return true;

            if (stage is BoundStage.Unwind { As: { } renamed } unwind && names.Any(name => RootIs(unwind.Path, name)) && !names.Contains(renamed, StringComparer.Ordinal))
                names.Add(renamed);
        }

        return false;
    }

    /// <summary>The stages so far, the stage's own, then one row carrying the truncation flag.</summary>
    private static List<BsonDocument> Probing(IEnumerable<BsonDocument> before, IEnumerable<BsonDocument> emitted, string flag) =>
    [
        .. before.Select(stage => stage.DeepClone().AsBsonDocument),
        .. emitted.Select(stage => stage.DeepClone().AsBsonDocument),
        new("$match", new BsonDocument(flag, true)),
        new("$limit", 1),
        new("$project", new BsonDocument(KeyStorage, 1)),
    ];

    /// <summary>
    /// Registers an inline resolve's outcome probe when the row shows its alias and something reads
    /// the outcomes: an effective <c>onMissing</c> other than <c>null</c> (missing rows, and with a
    /// filter the existence flag that tells an excluded record from a missing one), or a strict
    /// request (a non-key target's ambiguity). A target onto a member that is not its key is joined
    /// with two records, so a key two records hold is <c>ambiguous</c> whatever the page and cache
    /// hold. Null when there is nothing to probe.
    /// </summary>
    private static InlineProbe? Probe(BoundPipeline bound, int index, BoundStage.Resolve resolve, List<InlineProbe> probes, List<string> keys)
    {
        var reports = resolve.EffectiveOnMissing != ResolveOnMissing.Null;

        if (!(reports || bound.Strict) || resolve.Reference.Storage is not { } storage || !Shown(bound.FinalShape, resolve.As))
            return null;

        var ambiguity = TargetIsKey(resolve) ? null : ReservedAmbiguityFlag + probes.Count;
        var exists = reports && resolve.Filter is not null ? ReservedExistsFlag + probes.Count : null;

        if (!reports && ambiguity is null)
            return null;

        var probe = new InlineProbe(bound.CallerIndexOf(index) ?? index, resolve, storage, ambiguity, exists);

        probes.Add(probe);

        if (!keys.Contains(storage, StringComparer.Ordinal))
            keys.Add(storage);

        return probe;
    }

    /// <summary>Whether an inline resolve's target field is its entity's key, which no two records share.</summary>
    private static bool TargetIsKey(BoundStage.Resolve resolve) =>
        resolve.Cases is [{ Targets: [var target] }, ..] ? target.Declared.FieldIsKey : resolve.TargetFieldStorage == KeyStorage;

    /// <summary>The flags the inline probes write into the rows.</summary>
    private static IEnumerable<string> InlineFlags(IEnumerable<InlineProbe> probes) =>
        probes.SelectMany(probe => new[] { probe.AmbiguityFlag, probe.ExistsFlag }).OfType<string>();

    /// <summary>
    /// The outcomes of the page's inline resolves (DESIGN §3.6): per probe, every row whose
    /// reference holds a value and whose alias is null or absent is <c>not_found</c>, or
    /// <c>excluded</c> when the unfiltered target has the record; one whose ambiguity flag is set
    /// is <c>ambiguous</c>; each keyed by the reference's wire value. A null reference is
    /// <c>reference_null</c>, which is not reported. The flags are removed from the rows, which
    /// never show them.
    /// </summary>
    public static IReadOnlyList<KeyedRowOutcome> InlineOutcomes(CompiledQuery compiled, IReadOnlyList<BsonDocument> page)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(page);

        var outcomes = new List<KeyedRowOutcome>();

        foreach (var probe in compiled.InlineProbes)
        {
            var reference = probe.Resolve.Reference;

            for (var row = 0; row < page.Count; row++)
            {
                var ambiguous = Take(page[row], probe.AmbiguityFlag);
                var exists = Take(page[row], probe.ExistsFlag);
                var value = KeyedFetch.ValueAt(page[row], probe.ReferenceStorage);

                if (value is null || value.IsBsonNull || value.IsBsonUndefined)
                    continue;

                var joined = KeyedFetch.ValueAt(page[row], probe.Resolve.As) is { IsBsonNull: false, IsBsonUndefined: false };

                if (joined && !ambiguous)
                    continue;

                var key = WireEncoder.EncodeScalar(value, reference.LeafKind, reference.Leaf) switch
                {
                    null => null,
                    System.Text.Json.Nodes.JsonValue scalar => scalar.ToString(),
                    var other => other.ToJsonString(),
                };

                outcomes.Add(new KeyedRowOutcome(probe.Stage, probe.Resolve.As, row, null, key,
                    joined ? KeyedOutcome.Ambiguous : exists ? KeyedOutcome.Excluded : KeyedOutcome.NotFound));
            }
        }

        return outcomes;

        // Reads a flag off a row and removes it; absent is false.
        static bool Take(BsonDocument row, string? field)
        {
            if (field is null || !row.TryGetValue(field, out var set))
                return false;

            row.Remove(field);

            return set is BsonBoolean { Value: true };
        }
    }

    /// <summary>
    /// The <c>LOOKUP_TRUNCATED</c> diagnostics of a page: one per lookup when some row of the
    /// page joined a parent with more children than the lookup's limit. <c>rows</c> counts the
    /// page's rows that carry the flag. The flags are removed from the rows, which never show them.
    /// </summary>
    public static IReadOnlyList<Diagnostic> LookupTruncations(CompiledQuery compiled, IReadOnlyList<BsonDocument> page)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(page);

        var diagnostics = new List<Diagnostic>();

        foreach (var flag in compiled.LookupFlags)
        {
            var rows = 0;

            foreach (var row in page)
                if (row.TryGetValue(flag.Field, out var set))
                {
                    if (set is BsonBoolean { Value: true })
                        rows++;

                    row.Remove(flag.Field);
                }

            if (rows == 0)
                continue;

            diagnostics.Add(new Diagnostic
            {
                Code = Codes.LookupTruncated,
                Message = $"'{flag.Alias}' holds the first {flag.Limit} children of a parent that has more ({rows} {(rows == 1 ? "row" : "rows")} of this page affected).",
                Stage = flag.Stage,
                Path = flag.Alias,
                Params = new Dictionary<string, object?> { ["alias"] = flag.Alias, ["limit"] = flag.Limit, ["rows"] = rows },
            });
        }

        return diagnostics;
    }

    /// <summary>
    /// The <c>UNWIND_DEPTH_TRUNCATED</c> diagnostics of a page: one per flattening unwind when
    /// some row of the page came from a collection whose items nest deeper than the unwind
    /// descends. <c>rows</c> counts the page's rows that carry the probe.
    /// </summary>
    public static IReadOnlyList<Diagnostic> DepthTruncations(CompiledQuery compiled, IReadOnlyList<BsonDocument> page)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(page);

        var diagnostics = new List<Diagnostic>();

        foreach (var probe in compiled.FlattenProbes)
        {
            var rows = page.Count(row => row.TryGetValue(probe.Field, out var set) && set is BsonBoolean { Value: true });

            if (rows == 0)
                continue;

            diagnostics.Add(new Diagnostic
            {
                Code = Codes.UnwindDepthTruncated,
                Message = $"'{probe.Path}' nests items deeper than {probe.Depth} levels; the items below are not in the rows ({rows} {(rows == 1 ? "row" : "rows")} of this page affected).",
                Stage = probe.Stage,
                Path = probe.Path,
                Params = new Dictionary<string, object?> { ["path"] = probe.Path, ["depth"] = probe.Depth, ["rows"] = rows },
            });
        }

        return diagnostics;
    }

    /// <summary>The scope equality: one typed comparison on one indexed member.</summary>
    public static BsonDocument ScopeFilter(BoundStage.Scope scope) =>
        new(scope.OrganisationStorage, new BsonBinaryData(scope.Organisation, scope.Representation));

    /// <summary>The collation as the aggregate command carries it.</summary>
    public static BsonDocument CollationDocument(CollationOptions collation)
    {
        ArgumentNullException.ThrowIfNull(collation);

        return new BsonDocument { ["locale"] = collation.Locale, ["strength"] = collation.Strength };
    }

    private static BsonDocument KeysetFrom(CursorPayload cursor, IReadOnlyList<BoundSortField> sortFields)
    {
        var fields = new List<KeysetField>();
        BsonValue key = BsonNull.Value;

        foreach (var value in cursor.Fields)
        {
            if (value.Wire == KeyStorage)
            {
                key = value.Value;
                continue;
            }

            var sortField = sortFields.FirstOrDefault(field => field.Path.Wire == value.Wire);

            if (sortField is null)
                continue;

            fields.Add(new KeysetField(sortField.Path.Storage!, value.Ascending, value.Value));
        }

        return KeysetPredicate.Build(fields, KeyStorage, key);
    }

    // ---- filters ----------------------------------------------------------------------------

    /// <summary>Compiles a condition tree to a filter document; <paramref name="collated"/> when the aggregate carries the collation.</summary>
    public static BsonDocument Filter(BoundCondition condition, List<SemiJoinSlot>? semiJoins = null, bool collated = false) => condition switch
    {
        BoundCondition.And and => new BsonDocument("$and", new BsonArray(and.Conditions.Select(inner => Filter(inner, semiJoins, collated)))),
        BoundCondition.Or or => new BsonDocument("$or", new BsonArray(or.Conditions.Select(inner => Filter(inner, semiJoins, collated)))),
        BoundCondition.Not not => new BsonDocument("$nor", new BsonArray { Filter(not.Condition, semiJoins, collated) }),
        BoundCondition.Any any => new BsonDocument(any.Path.Storage!, new BsonDocument("$elemMatch", Filter(any.Inner, semiJoins, collated))),
        BoundCondition.Leaf leaf => Leaf(leaf, semiJoins, collated),
        _ => new BsonDocument(),
    };

    private static BsonDocument Leaf(BoundCondition.Leaf leaf, List<SemiJoinSlot>? semiJoins, bool collated)
    {
        if (leaf.IsSemiJoin)
        {
            var ids = new BsonArray();
            var reference = ((ShapeNode.Remote)leaf.Path.Root).Reference;

            semiJoins?.Add(new SemiJoinSlot(leaf, ids));

            return new BsonDocument(reference.Storage!, new BsonDocument("$in", ids));
        }

        if (leaf.Op == "is")
            return Is(leaf);

        var storage = leaf.Path.Storage!;

        // A decimal under the addon bag is written wrapped by the bag serializer: match both places.
        if (leaf.Path.Addon is not null && leaf.Path.Kind == Kind.Decimal)
            return new BsonDocument("$or", new BsonArray { LeafAt(storage, leaf, collated), LeafAt(storage + "._v", leaf, collated) });

        return LeafAt(storage, leaf, collated);
    }

    /// <summary>
    /// A variant test: the discriminator element <c>$in</c> the admitted values. A null among
    /// them is a concrete base's own value, stored without a discriminator: an object without
    /// the element. On a collection both forms are one element's (<c>$elemMatch</c>); on an
    /// object the base form also needs the member to be an object, since a null or missing
    /// member has no discriminator either.
    /// </summary>
    private static BsonDocument Is(BoundCondition.Leaf leaf)
    {
        var element = OperandCoercer.VariantHolder(leaf.Path)?.DiscriminatorElement ?? "_t";
        var storage = leaf.Path.Storage;
        var discriminator = storage is null ? element : storage + "." + element;
        var values = leaf.Operand is BoundOperand.Set set ? set.Values : [];
        var named = new BsonArray(values.Where(value => !value.IsBsonNull));
        var withBase = values.Any(value => value.IsBsonNull);

        if (!withBase)
            return new BsonDocument(discriminator, new BsonDocument("$in", named));

        if (leaf.Path.Kind == Kind.Array && storage is not null)
        {
            var either = new BsonArray();

            if (named.Count > 0)
                either.Add(new BsonDocument(element, new BsonDocument("$in", named)));

            either.Add(new BsonDocument(element, new BsonDocument("$exists", false)));

            return new BsonDocument(storage, new BsonDocument("$elemMatch", either.Count == 1 ? either[0].AsBsonDocument : new BsonDocument("$or", either)));
        }

        var bare = storage is null
            ? new BsonDocument(discriminator, new BsonDocument("$exists", false))
            : new BsonDocument { [storage] = new BsonDocument("$type", "object"), [discriminator] = new BsonDocument("$exists", false) };

        return named.Count == 0 ? bare : new BsonDocument("$or", new BsonArray { new BsonDocument(discriminator, new BsonDocument("$in", named)), bare });
    }

    /// <summary>
    /// The form a leaf's string comparison takes. A fold is a plain operator when the aggregate
    /// is collated and a pattern with the <c>i</c> flag when it is not; an exact comparison of
    /// a string is a plain operator when the aggregate is not collated and, because a plain
    /// operator would fold there, an anchored pattern when it is. A pattern is never
    /// collation-aware. Members of other kinds compare by value either way.
    /// </summary>
    private static Casing CasingOf(BoundCondition.Leaf leaf, bool collated) => leaf.IgnoreCase switch
    {
        true when collated => Casing.Collated,
        true => Casing.Fold,
        _ when collated && leaf.Path.LeafKind == Kind.String => Casing.Exact,
        _ => Casing.Value,
    };

    private static BsonDocument LeafAt(string storage, BoundCondition.Leaf leaf, bool collated)
    {
        var op = leaf.Op;
        var casing = CasingOf(leaf, collated);

        switch (leaf.Operand)
        {
            case BoundOperand.Null:
                return op == "neq"
                    ? new BsonDocument(storage, new BsonDocument("$ne", BsonNull.Value))
                    : new BsonDocument(storage, BsonNull.Value);

            case BoundOperand.Set set:
                var values = casing is Casing.Fold or Casing.Exact
                    ? new BsonArray(set.Values.Select(value => value is BsonString text ? (BsonValue)Anchored(text.Value, casing) : value))
                    : new BsonArray(set.Values);

                return new BsonDocument(storage, new BsonDocument(op == "nin" ? "$nin" : "$in", values));

            case BoundOperand.Tolerant tolerant:
                // A regex alternative — the decimal text bracket — is a pattern, and $ne
                // against a pattern compares the pattern itself rather than matching with it.
                // $nor of the positive comparisons negates whatever the alternatives are, and
                // agrees with $and of $ne on a missing member, which is the only case where
                // the two could differ for a scalar.
                if (op == "neq")
                    return new BsonDocument("$nor", new BsonArray(tolerant.Alternatives.Select(value => Compare(storage, "eq", value, casing))));

                return new BsonDocument("$or", new BsonArray(tolerant.Alternatives.Select(value => Compare(storage, op, value, casing))));

            case BoundOperand.Single single:
                return Compare(storage, op, single.Value, casing);

            default:
                return new BsonDocument(storage, BsonNull.Value);
        }
    }

    /// <summary>The whole-value pattern of a text: escaped and anchored at both ends, folding case only as a fold.</summary>
    private static BsonRegularExpression Anchored(string text, Casing casing) =>
        new("^" + RegexGuard.Escape(text) + "$", casing == Casing.Fold ? "i" : "");

    private static BsonDocument Compare(string storage, string op, BsonValue value, Casing casing)
    {
        // A fold that stays a pattern (contains, endsWith) folds case with the flag, under a
        // collation or not; an exact comparison and a caller's own pattern carry none.
        var flags = casing is Casing.Fold or Casing.Collated ? "i" : "";
        var pattern = casing is Casing.Fold or Casing.Exact && value is BsonString;

        return op switch
        {
            "eq" when pattern => new BsonDocument(storage, Anchored(Text(value), casing)),
            "eq" => new BsonDocument(storage, value),
            "neq" when pattern => new BsonDocument(storage, new BsonDocument("$not", Anchored(Text(value), casing))),
            "neq" => new BsonDocument(storage, new BsonDocument("$ne", value)),
            "gt" => new BsonDocument(storage, new BsonDocument("$gt", value)),
            "gte" => new BsonDocument(storage, new BsonDocument("$gte", value)),
            "lt" => new BsonDocument(storage, new BsonDocument("$lt", value)),
            "lte" => new BsonDocument(storage, new BsonDocument("$lte", value)),
            // The binder refuses a text operator on a member that holds no text, so `value`
            // is a string by the time it gets here; Text() renders any other type rather than
            // casting it.
            "contains" => new BsonDocument(storage, new BsonRegularExpression(RegexGuard.Escape(Text(value)), flags)),
            // Under the collation a prefix is the range from the prefix up to the prefix
            // followed by the highest character: every string that starts with it under the
            // collation, accents and case folded, and nothing else. A pattern cannot fold
            // accents, so the range is the only form that folds the way the rest of the
            // aggregate does.
            "startsWith" when casing == Casing.Collated => new BsonDocument(storage, new BsonDocument { ["$gte"] = Text(value), ["$lt"] = Text(value) + AfterEveryCharacter }),
            "startsWith" => new BsonDocument(storage, new BsonRegularExpression("^" + RegexGuard.Escape(Text(value)), flags)),
            "endsWith" => new BsonDocument(storage, new BsonRegularExpression(RegexGuard.Escape(Text(value)) + "$", flags)),
            "regex" => new BsonDocument(storage, new BsonRegularExpression(Text(value), flags)),
            "exists" => new BsonDocument(storage, new BsonDocument("$exists", value)),
            _ => new BsonDocument(storage, value),
        };
    }

    /// <summary>The text of an operand for a pattern comparison, whatever BSON type it arrived as.</summary>
    private static string Text(BsonValue value) => value switch
    {
        BsonString text => text.Value,
        BsonInt32 code => ((char)code.Value).ToString(),
        _ => value.ToString() ?? "",
    };

    // ---- flatten ----------------------------------------------------------------------------

    /// <summary>
    /// The stages that replace the collection at <paramref name="storage"/> by its items and
    /// their descendants in pre-order, each without its nested collection, before the
    /// <c>$unwind</c>: a <c>$set</c> of nested <c>$reduce</c>s of fixed depth, then a
    /// <c>$set</c> that writes the probe (an item at the last level still nests some) and
    /// removes the nested collection the last level kept for it.
    /// </summary>
    private static IEnumerable<BsonDocument> Flatten(string storage, BoundFlatten flatten, string probe)
    {
        yield return new BsonDocument("$set", new BsonDocument(storage, FlattenLevel("$" + storage, flatten, 1)));

        yield return new BsonDocument("$set", new BsonDocument
        {
            [probe] = new BsonDocument("$anyElementTrue", new BsonArray
            {
                new BsonDocument("$map", new BsonDocument
                {
                    ["input"] = "$" + storage,
                    ["as"] = "item",
                    ["in"] = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", ArrayOrEmpty("$$item." + flatten.Storage)), 0 }),
                }),
            }),
            [storage] = new BsonDocument("$map", new BsonDocument
            {
                ["input"] = "$" + storage,
                ["as"] = "item",
                ["in"] = Without("$$item", flatten.Storage),
            }),
        });
    }

    /// <summary>
    /// One level of the descent over <paramref name="input"/>: each item (bound with
    /// <c>$let</c>, since the nested <c>$reduce</c> rebinds <c>$$this</c>) followed by the next
    /// level over its nested collection. The last level keeps the nested collection for the probe.
    /// </summary>
    private static BsonDocument FlattenLevel(BsonValue input, BoundFlatten flatten, int level)
    {
        var item = "l" + level;
        var last = level >= flatten.Depth;
        var parts = new BsonArray { "$$value", new BsonArray { last ? "$$" + item : Without("$$" + item, flatten.Storage) } };

        if (!last)
            parts.Add(FlattenLevel("$$" + item + "." + flatten.Storage, flatten, level + 1));

        return new BsonDocument("$reduce", new BsonDocument
        {
            ["input"] = ArrayOrEmpty(input),
            ["initialValue"] = new BsonArray(),
            ["in"] = new BsonDocument("$let", new BsonDocument
            {
                ["vars"] = new BsonDocument(item, "$$this"),
                ["in"] = new BsonDocument("$concatArrays", parts),
            }),
        });
    }

    /// <summary>The value when it is an array, otherwise the empty array.</summary>
    private static BsonDocument ArrayOrEmpty(BsonValue value) =>
        new("$cond", new BsonArray { new BsonDocument("$isArray", value), value, new BsonArray() });

    /// <summary>An item without one of its fields.</summary>
    private static BsonDocument Without(BsonValue item, string field) =>
        new("$unsetField", new BsonDocument { ["field"] = new BsonDocument("$literal", field), ["input"] = item });

    // ---- joins ------------------------------------------------------------------------------

    /// <summary>The truncation flag of a lookup that returns an array, registered in stage order; null for a <c>first</c> lookup.</summary>
    private static LookupFlag? Flag(BoundStage.Lookup lookup, List<LookupFlag> flags)
    {
        if (lookup.First)
            return null;

        var flag = new LookupFlag(ReservedLookupFlag + flags.Count, lookup.Stage, lookup.As, lookup.Limit);

        flags.Add(flag);

        return flag;
    }

    /// <summary>
    /// A lookup: the indexed <c>$lookup</c> with the scope inside, the caller's sort completed by
    /// the child's key, and one child more than the limit, then a <c>$set</c> that flags a parent
    /// with more children and cuts the array to the limit; with <c>first</c>, one child and the
    /// <c>$set</c> that takes it out of the array (absent, so null, when there is none). A lookup
    /// on an alias joins nothing where the row holds no parent: <c>$lookup</c> reads a missing
    /// local field as null, which would join the children whose reference is null.
    /// </summary>
    private static IEnumerable<BsonDocument> Lookup(BoundStage.Lookup lookup, List<SemiJoinSlot> semiJoins, bool collated, LookupFlag? flag, bool keepDiscriminators = false)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", ScopeFilter(lookup.ChildScope)) };
        var exactKey = collated && IsStringStored(lookup.ChildReference);
        var onAlias = lookup.On is not null;

        if (onAlias)
            pipeline.Add(new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$$" + ParentVariable, BsonNull.Value }))));

        if (exactKey)
            pipeline.Add(ExactKeyMatch(lookup.ChildKeyStorage));

        if (lookup.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(lookup.Filter, semiJoins, collated)));

        pipeline.Add(Sort(lookup.ChildSort ?? [], tieBreak: true));
        pipeline.Add(new BsonDocument("$limit", lookup.First ? 1 : lookup.Limit + 1));
        pipeline.Add(new BsonDocument("$project", Select(lookup.Select, keepDiscriminators)));

        var join = Join(lookup.From.Collection, lookup.ParentKeyStorage, lookup.ChildKeyStorage, exactKey, pipeline, lookup.As);

        if (onAlias)
        {
            var let = join.TryGetValue("let", out var existing) ? existing.AsBsonDocument : new BsonDocument();

            let[ParentVariable] = "$" + lookup.ParentKeyStorage;

            // The same member order as a join without it: let before pipeline.
            join = new BsonDocument
            {
                ["from"] = join["from"],
                ["localField"] = join["localField"],
                ["foreignField"] = join["foreignField"],
                ["let"] = let,
                ["pipeline"] = join["pipeline"],
                ["as"] = join["as"],
            };
        }

        yield return new BsonDocument("$lookup", join);

        if (lookup.First)
        {
            yield return new BsonDocument("$set", new BsonDocument(lookup.As, new BsonDocument("$arrayElemAt", new BsonArray { "$" + lookup.As, 0 })));
            yield break;
        }

        yield return new BsonDocument("$set", new BsonDocument
        {
            [flag!.Field] = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + lookup.As), lookup.Limit }),
            [lookup.As] = new BsonDocument("$slice", new BsonArray { "$" + lookup.As, lookup.Limit }),
        });
    }

    /// <summary>Whether the keyed fetch runs a resolve after the page rather than the aggregate joining it.</summary>
    private static bool IsKeyed(BoundStage.Resolve resolve) => resolve.IsRemote || resolve.Executor == ResolveExecutor.Keyed;

    /// <summary>
    /// The start of an internal owner query grouped per key (<c>keyedBy</c>, DESIGN §3.5.2 step 3):
    /// the rows holding a key, and for an item target each matching element under its alias, one
    /// row per element.
    /// </summary>
    private static IEnumerable<BsonDocument> KeyedByPrologue(BoundKeyedBy keyedBy, bool collated)
    {
        var keys = new BsonArray(keyedBy.Keys);

        // Inside a collated aggregate the $in folds case like any comparison, so string keys are
        // compared again byte for byte on the one value per row, as a join's key is.
        var exact = collated && keyedBy.Keys.Count > 0 && keyedBy.Keys.All(key => key.IsString);

        yield return new BsonDocument("$match", new BsonDocument(keyedBy.Path.Storage!, new BsonDocument("$in", keys)));

        // Whole rows (a remote lookup of an entity, DESIGN §3.4.4): each row carries the key it
        // answers for. A path through a collection may hold several of the keys, one per element, so
        // the row is answered once per distinct key it holds.
        if (keyedBy.KeyAlias is { } keyAlias)
        {
            if (keyedBy.ItemStorage is null)
            {
                yield return new BsonDocument("$set", new BsonDocument(keyAlias, "$" + keyedBy.Path.Storage));
            }
            else
            {
                yield return new BsonDocument("$set", new BsonDocument(keyAlias, new BsonDocument("$filter", new BsonDocument
                {
                    ["input"] = new BsonDocument("$setUnion", new BsonArray
                    {
                        new BsonDocument("$map", new BsonDocument
                        {
                            ["input"] = new BsonDocument("$ifNull", new BsonArray { "$" + keyedBy.ItemStorage, new BsonArray() }),
                            ["as"] = ElementVariable,
                            ["in"] = "$$" + ElementVariable + "." + keyedBy.ElementFieldStorage,
                        }),
                    }),
                    ["as"] = ElementVariable,
                    ["cond"] = new BsonDocument("$in", new BsonArray { "$$" + ElementVariable, new BsonDocument("$literal", keys) }),
                })));
                yield return new BsonDocument("$unwind", "$" + keyAlias);
                yield return new BsonDocument("$match", new BsonDocument(keyAlias, new BsonDocument("$in", keys)));
            }

            if (exact)
                yield return ExactKeysMatch(keyAlias, keys);

            yield break;
        }

        if (keyedBy.ItemStorage is null)
        {
            if (exact)
                yield return ExactKeysMatch(keyedBy.Path.Storage!, keys);

            yield break;
        }

        // Only the elements holding a key are unwound, so a row does not carry its whole collection
        // into every element it has; each keeps its position, which orders two elements of one row
        // under one key (RE-15).
        yield return new BsonDocument("$set", new BsonDocument(keyedBy.ElementAlias!, new BsonDocument("$filter", new BsonDocument
        {
            ["input"] = "$" + keyedBy.ItemStorage,
            ["as"] = ElementVariable,
            ["cond"] = new BsonDocument("$in", new BsonArray { "$$" + ElementVariable + "." + keyedBy.ElementFieldStorage, new BsonDocument("$literal", keys) }),
        })));
        yield return new BsonDocument("$unwind", new BsonDocument { ["path"] = "$" + keyedBy.ElementAlias, ["includeArrayIndex"] = ReservedElementIndex });
        yield return new BsonDocument("$match", new BsonDocument(keyedBy.PartitionStorage, new BsonDocument("$in", keys)));

        if (exact)
            yield return ExactKeysMatch(keyedBy.PartitionStorage, keys);
    }

    /// <summary>
    /// The match that keeps a key lookup on strings exact inside a collated aggregate: the stored
    /// string equals one of the keys byte for byte, with the byte functions a collation does not reach.
    /// </summary>
    private static BsonDocument ExactKeysMatch(string storage, BsonArray keys)
    {
        var field = "$" + storage;
        var key = "$$" + KeyVariable;

        return new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$anyElementTrue", new BsonArray
        {
            new BsonDocument("$map", new BsonDocument
            {
                ["input"] = new BsonDocument("$literal", keys),
                ["as"] = KeyVariable,
                ["in"] = new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", field), "string" }),
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$indexOfBytes", new BsonArray { field, key }), 0 }),
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$strLenBytes", field), new BsonDocument("$strLenBytes", key) }),
                }),
            }),
        })));
    }

    /// <summary>
    /// The window of a query grouped per key: each key's rows numbered by record key, the rows
    /// past <c>perKey</c> dropped, the number removed. A key with two rows is how the caller tells
    /// an ambiguous reference from a resolved one.
    /// </summary>
    private static List<BsonDocument> KeyedByWindow(BoundKeyedBy keyedBy, bool collated = false, IReadOnlyList<BoundSortField>? sort = null)
    {
        // An element's rows carry their position, which orders two elements of one row.
        var element = keyedBy.ItemStorage is not null && keyedBy.KeyAlias is null;

        // The rows of a key in the query's own order when it sorts right after its leading matches
        // (a remote lookup's sort, DESIGN §3.4.4), completed by the record key; by record key otherwise.
        var sortBy = new BsonDocument();

        foreach (var field in sort ?? [])
            sortBy[field.Path.Storage!] = field.Ascending ? 1 : -1;

        if (!sortBy.Contains(KeyStorage))
            sortBy[KeyStorage] = 1;

        if (element)
            sortBy[ReservedElementIndex] = 1;

        return
        [
            new("$setWindowFields", new BsonDocument
            {
                // Inside a collated aggregate a partition on a string key folds keys that differ only in
                // case into one; its hash compares the bytes, so each key is ranked on its own (RE-12).
                ["partitionBy"] = collated && keyedBy.Keys.Count > 0 && keyedBy.Keys.All(key => key.IsString)
                    ? new BsonDocument("$toHashedIndexKey", "$" + keyedBy.PartitionStorage)
                    : "$" + keyedBy.PartitionStorage,
                ["sortBy"] = sortBy,

                // $documentNumber takes one sort field; rows ordered by several are numbered by a
                // running count over the ordered partition.
                ["output"] = new BsonDocument(ReservedRank, sortBy.ElementCount == 1
                    ? new BsonDocument("$documentNumber", new BsonDocument())
                    : new BsonDocument { ["$sum"] = 1, ["window"] = new BsonDocument("documents", new BsonArray { "unbounded", "current" }) }),
            }),
            new("$match", new BsonDocument(ReservedRank, new BsonDocument("$lte", keyedBy.PerKey))),
            new("$unset", element ? new BsonArray { ReservedRank, ReservedElementIndex } : ReservedRank),
        ];
    }

    /// <summary>
    /// The order a <c>keyedBy</c> window ranks each key's rows in: the query's sort when it is the
    /// first stage after the leading matches (where the window runs), so the rows it keeps per key are
    /// the first in that order; null otherwise, and the rows are ranked by record key as before.
    /// </summary>
    private static IReadOnlyList<BoundSortField>? WindowSort(BoundPipeline bound)
    {
        foreach (var stage in bound.Stages)
        {
            if (stage is BoundStage.Match)
                continue;

            return stage is BoundStage.Sort sort && ReferenceEquals(sort, bound.Sort) ? sort.Fields : null;
        }

        return null;
    }

    /// <summary>
    /// What a keyed resolve reads off the page rows, which a projection before the page keeps in
    /// storage: the reference, the collection it crosses under <c>elements</c>, and the stored
    /// values that select its cases.
    /// </summary>
    private static IEnumerable<string> KeptStorages(BoundStage.Resolve resolve)
    {
        if (resolve.CollectionStorage is { } collection)
        {
            yield return collection;
            yield break;
        }

        if (resolve.Reference.Storage is { } reference)
            yield return reference;

        foreach (var bound in resolve.Cases ?? [])
            if (bound.When is { } when)
                yield return when.Storage;
    }

    /// <summary>
    /// An inline resolve: the indexed <c>$lookup</c> with the scope inside, the target's filter, one
    /// record, then a <c>$set</c> that takes it out of the array. With an ambiguity flag the join takes
    /// two records and the flag says whether there were two; with an existence flag a second join
    /// without the filter says whether the target has the record at all (DESIGN §3.6).
    /// </summary>
    private static IEnumerable<BsonDocument> LocalResolve(BoundStage.Resolve resolve, List<SemiJoinSlot> semiJoins, bool collated, InlineProbe? probe = null, bool keepDiscriminators = false)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", ScopeFilter(resolve.TargetScope!)) };
        var exactKey = collated && IsStringStored(resolve.Reference);

        if (exactKey)
            pipeline.Add(ExactKeyMatch(resolve.TargetFieldStorage!));

        if (resolve.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(resolve.Filter, semiJoins, collated)));

        // Two records of one key: the first by record key is the one taken, as the keyed fetch takes it.
        if (probe?.AmbiguityFlag is not null)
            pipeline.Add(new BsonDocument("$sort", new BsonDocument(KeyStorage, 1)));

        pipeline.Add(new BsonDocument("$limit", probe?.AmbiguityFlag is null ? 1 : 2));
        pipeline.Add(new BsonDocument("$project", Select(resolve.Select!, keepDiscriminators)));

        // No caller alias ends in the suffix, so the temporary field shadows nothing.
        var temporary = resolve.As + Aliases.ReservedSuffix;
        var set = new BsonDocument(resolve.As, new BsonDocument("$arrayElemAt", new BsonArray { "$" + temporary, 0 }));

        yield return new BsonDocument("$lookup", Join(resolve.Target!.Collection, resolve.Reference.Storage!, resolve.TargetFieldStorage!, exactKey, pipeline, temporary));

        if (probe?.AmbiguityFlag is { } ambiguity)
            set[ambiguity] = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + temporary), 1 });

        if (probe?.ExistsFlag is not { } exists)
        {
            yield return new BsonDocument("$set", set);
            yield return new BsonDocument("$unset", temporary);
            yield break;
        }

        // The existence join: the same key match without the filter, one key read.
        var existing = resolve.As + "Has" + Aliases.ReservedSuffix;
        var probeline = new BsonArray { new BsonDocument("$match", ScopeFilter(resolve.TargetScope!)) };

        if (exactKey)
            probeline.Add(ExactKeyMatch(resolve.TargetFieldStorage!));

        probeline.Add(new BsonDocument("$limit", 1));
        probeline.Add(new BsonDocument("$project", new BsonDocument(KeyStorage, 1)));

        yield return new BsonDocument("$lookup", Join(resolve.Target!.Collection, resolve.Reference.Storage!, resolve.TargetFieldStorage!, exactKey, probeline, existing));

        set[exists] = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + existing), 0 });

        yield return new BsonDocument("$set", set);
        yield return new BsonDocument("$unset", new BsonArray { temporary, existing });
    }

    /// <summary>
    /// The <c>$lookup</c> document of a join. With <paramref name="exactKey"/> the local key is
    /// bound to a variable for the sub-pipeline's byte comparison; otherwise the document is
    /// the indexed join alone.
    /// </summary>
    private static BsonDocument Join(string from, string localField, string foreignField, bool exactKey, BsonArray pipeline, string alias)
    {
        var join = new BsonDocument
        {
            ["from"] = from,
            ["localField"] = localField,
            ["foreignField"] = foreignField,
        };

        if (exactKey)
            join["let"] = new BsonDocument(KeyVariable, "$" + localField);

        join["pipeline"] = pipeline;
        join["as"] = alias;

        return join;
    }

    /// <summary>Whether a reference member is stored as a string, which the aggregate's collation would fold on a join.</summary>
    private static bool IsStringStored(ResolvedPath reference) =>
        reference.Leaf?.Representation.BsonType == BsonType.String
        || (reference.LeafKind == Kind.String && reference.Leaf?.Representation.BsonType != BsonType.Int32);

    /// <summary>
    /// The match that keeps a join on a string key exact inside a collated aggregate. The
    /// join itself compares under the collation, so a key that differs only in case or
    /// accents would join; ids are exact, so the joined rows are compared again byte for
    /// byte, with the byte functions that a collation does not reach. A key that is not a
    /// string joins as it always has.
    /// </summary>
    private static BsonDocument ExactKeyMatch(string foreignField)
    {
        var field = "$" + foreignField;
        var key = "$$" + KeyVariable;

        return new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$cond", new BsonDocument
        {
            ["if"] = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", key), "string" }),
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", field), "string" }),
            }),
            ["then"] = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$indexOfBytes", new BsonArray { field, key }), 0 }),
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$strLenBytes", field), new BsonDocument("$strLenBytes", key) }),
            }),
            ["else"] = true,
        })));
    }

    private static BsonDocument Select(IReadOnlyList<ResolvedPath> select, bool keepDiscriminators = false)
    {
        var projection = new BsonDocument();

        foreach (var path in select)
            if (path.Storage is not null)
                projection[path.Storage] = 1;

        // A select under a polymorphic object keeps its discriminator, unless a selected path covers it.
        foreach (var storage in keepDiscriminators ? DiscriminatorsOf(select) : [])
            if (!projection.Names.Any(kept => storage == kept || storage.StartsWith(kept + ".", StringComparison.Ordinal)))
                projection[storage] = 1;

        if (projection.ElementCount == 0)
            projection[KeyStorage] = 1;

        return projection;
    }

    // ---- group ------------------------------------------------------------------------------

    private static IEnumerable<BsonDocument> Group(BoundStage.Group group, IReadOnlyList<string> truncations)
    {
        var groupDocument = new BsonDocument { ["_id"] = GroupId(group.Keys) };

        // A group of rows some truncation reached (a flattened item's depth, a lookup's limit)
        // is reached by it too.
        foreach (var field in truncations)
            groupDocument[field] = new BsonDocument("$max", "$" + field);
        var distinct = new List<string>();

        foreach (var field in group.Fields)
        {
            groupDocument[field.As] = Accumulator(field);

            if (field.Function == "countDistinct")
                distinct.Add(field.As);
        }

        yield return new BsonDocument("$group", groupDocument);

        if (distinct.Count > 0)
        {
            var sizes = new BsonDocument();

            foreach (var name in distinct)
                sizes[name] = new BsonDocument("$size", "$" + name);

            yield return new BsonDocument("$addFields", sizes);
        }

        var reshape = new BsonDocument();

        foreach (var key in group.Keys)
            reshape[key.As] = group.Keys.Count == 1 ? "$_id" : "$_id." + key.As;

        foreach (var field in group.Fields)
            reshape[field.As] = 1;

        foreach (var field in truncations)
            reshape[field] = 1;

        reshape["_id"] = 0;

        yield return new BsonDocument("$project", reshape);
    }

    private static BsonValue GroupId(IReadOnlyList<GroupKey> keys)
    {
        if (keys.Count == 1)
            return KeyExpression(keys[0]);

        var document = new BsonDocument();

        // A scalar $group _id normalises a missing member to null; a subdocument _id omits the
        // field instead, which would bucket a null and a missing value apart and leave the
        // row without the member. $ifNull makes every leg of a composite key behave the way
        // the single-key form does.
        foreach (var key in keys)
            document[key.As] = new BsonDocument("$ifNull", new BsonArray { KeyExpression(key), BsonNull.Value });

        return document;
    }

    private static BsonValue KeyExpression(GroupKey key)
    {
        if (key.Trunc is null)
            return "$" + key.Path!.Storage;

        var trunc = new BsonDocument
        {
            ["date"] = TruncatedDate(key.Trunc),
            ["unit"] = key.Trunc.Unit,
            ["timezone"] = key.Trunc.Timezone,
        };

        if (key.Trunc.Unit == "week")
            trunc["startOfWeek"] = key.Trunc.WeekStart ?? "monday";

        return new BsonDocument("$dateTrunc", trunc);
    }

    /// <summary>
    /// The instant a truncation starts from. A date has no time of day and is stored as the
    /// midnight-UTC instant of its calendar day; truncating that instant in a zone west of
    /// UTC would read it as the evening before. Its calendar parts are therefore re-read as
    /// a local date in the caller's zone, which is the stored instant itself in UTC. A
    /// dateTime is an instant already and truncates as stored.
    /// </summary>
    private static BsonValue TruncatedDate(DateTrunc trunc)
    {
        var stored = "$" + trunc.Path.Storage;

        if (trunc.Path.Kind != Kind.Date || trunc.Timezone == "UTC")
            return stored;

        return new BsonDocument("$dateFromParts", new BsonDocument
        {
            ["year"] = new BsonDocument("$year", stored),
            ["month"] = new BsonDocument("$month", stored),
            ["day"] = new BsonDocument("$dayOfMonth", stored),
            ["timezone"] = trunc.Timezone,
        });
    }

    private static BsonValue Accumulator(Aggregate field) => field.Function switch
    {
        "count" => new BsonDocument("$sum", 1),
        "countDistinct" => new BsonDocument("$addToSet", Expression(field.Argument!)),
        "sum" => new BsonDocument("$sum", Expression(field.Argument!)),
        // A double cannot hold the mean of 64-bit integers exactly; a decimal can, and the
        // binder types the output as one.
        "avg" when field.ArgumentKind == Kind.Long => new BsonDocument("$avg", new BsonDocument("$toDecimal", Expression(field.Argument!))),
        "avg" => new BsonDocument("$avg", Expression(field.Argument!)),
        "min" => new BsonDocument("$min", Expression(field.Argument!)),
        "max" => new BsonDocument("$max", Expression(field.Argument!)),
        "first" => new BsonDocument("$first", Expression(field.Argument!)),
        "last" => new BsonDocument("$last", Expression(field.Argument!)),
        "push" => new BsonDocument("$push", Expression(field.Argument!)),
        _ => BsonNull.Value,
    };

    /// <summary>Compiles a group expression.</summary>
    public static BsonValue Expression(BoundExpression expression) => expression switch
    {
        BoundExpression.Path path => "$" + path.Resolved.Storage,
        BoundExpression.Literal literal => new BsonDocument("$literal", literal.Value),
        BoundExpression.Arithmetic arithmetic => new BsonDocument(arithmetic.Operator switch
        {
            "add" => "$add",
            "subtract" => "$subtract",
            "multiply" => "$multiply",
            "divide" => "$divide",
            _ => "$ifNull",
        }, new BsonArray(arithmetic.Operands.Select(Expression))),
        _ => BsonNull.Value,
    };

    // ---- project, sort ----------------------------------------------------------------------

    /// <summary>
    /// The projection. A remote resolve reads its reference member off the page rows after the
    /// aggregate, and a join that runs after the page reads its local key there, so both
    /// survive every projection in storage; the wire output follows the shape and drops them
    /// again when they were not kept. Null when nothing is left to emit.
    /// </summary>
    private static BsonDocument? Project(
        BoundStage.Project project,
        IReadOnlyList<BoundStage.Resolve> keyedResolves,
        IReadOnlyList<string> lateJoinKeys,
        bool keepKey,
        IReadOnlyList<string> reservedIndexes,
        IReadOnlyList<string> sortStorages,
        List<string> sortKeptAgainstProjection,
        ref bool keyKeptAgainstProjection)
    {
        var projection = new BsonDocument();
        var kept = keyedResolves.SelectMany(KeptStorages).Distinct(StringComparer.Ordinal).ToList();

        foreach (var storage in lateJoinKeys)
            if (!kept.Contains(storage, StringComparer.Ordinal))
                kept.Add(storage);

        // An index the paging sort still has to read survives the projection like a reference does.
        kept.AddRange(reservedIndexes);

        // So does a path the paging sort orders by and the cursor is minted from. What the
        // caller did not ask for is recorded, so a contract 1 row loses it again.
        foreach (var storage in sortStorages)
        {
            if (storage == KeyStorage || kept.Contains(storage, StringComparer.Ordinal))
                continue;

            kept.Add(storage);

            var asked = project.Inclusion
                ? project.Paths.Any(path => path.Storage is not null && Covers(path.Storage, storage))
                : !project.Paths.Any(path => path.Storage is not null && Covers(path.Storage, storage));

            if (!asked)
                sortKeptAgainstProjection.Add(storage);
        }

        foreach (var path in project.Paths)
        {
            if (path.Storage is null)
                continue;

            if (!project.Inclusion && kept.Any(storage => Covers(path.Storage, storage)))
                continue;

            // An exclusion that names the key writes it here; an inclusion leaves it to the
            // IncludeId rule below. Both have to keep it when paging still reads it.
            if (!project.Inclusion && path.Storage == KeyStorage && keepKey)
            {
                keyKeptAgainstProjection = true;
                continue;
            }

            projection[path.Storage] = project.Inclusion ? 1 : 0;
        }

        if (project.Inclusion)
            foreach (var storage in kept)
                if (!project.Paths.Any(path => path.Storage is not null && Covers(path.Storage, storage)))
                    projection[storage] = 1;

        // Excluding the key leaves the sort and the cursor reading a member that is not there,
        // which pages an order Mongo never produced; keep it and record that the caller did not
        // ask for it, so the row loses it again.
        if (!project.IncludeId && !projection.Contains(KeyStorage) && !kept.Contains(KeyStorage, StringComparer.Ordinal))
        {
            if (keepKey)
                keyKeptAgainstProjection = true;
            else
                projection[KeyStorage] = 0;
        }

        return projection.ElementCount == 0 ? null : new BsonDocument("$project", projection);
    }

    /// <summary>
    /// The discriminators a projection of <paramref name="paths"/> has to keep (RE-16): for each path
    /// below a polymorphic object (an object, or the element of a collection of objects, whose type has
    /// variants), that object's discriminator element, in storage; the path itself is not an ancestor.
    /// </summary>
    private static IEnumerable<string> DiscriminatorsOf(IEnumerable<ResolvedPath> paths)
    {
        var kept = new List<string>();

        foreach (var path in paths)
        {
            if (path is not { Path: { } def, Entity: { } entity, Storage: { } storage })
                continue;

            var wire = def.Wire.Split('.');
            var stored = storage.Split('.');

            for (var length = 1; length < wire.Length; length++)
            {
                var holder = entity.Path(string.Join('.', wire.Take(length)));
                var type = holder?.Shape.Kind switch
                {
                    Kind.Object => holder.Shape.Type,
                    Kind.Array => holder.Shape.Of?.Type,
                    _ => null,
                };

                if (type is not { Variants.Count: > 0, DiscriminatorElement: { } element })
                    continue;

                var depth = stored.Length - (wire.Length - length);

                if (depth <= 0)
                    continue;

                var discriminator = string.Join('.', stored.Take(depth)) + "." + element;

                if (!kept.Contains(discriminator, StringComparer.Ordinal))
                    kept.Add(discriminator);
            }
        }

        return kept;
    }

    /// <summary>Whether a projected storage path is the reference path or one of its ancestors.</summary>
    private static bool Covers(string projected, string reference) =>
        reference == projected || reference.StartsWith(projected + ".", StringComparison.Ordinal);

    private static BsonDocument Sort(IReadOnlyList<BoundSortField> fields, bool tieBreak, IReadOnlyList<string>? indexes = null)
    {
        var sort = new BsonDocument();

        foreach (var field in fields)
            sort[field.Path.Storage!] = field.Ascending ? 1 : -1;

        // The key first, then one index per unwind: rows of one parent differ only by position.
        if (indexes is { Count: > 0 })
        {
            if (!sort.Contains(KeyStorage))
                sort[KeyStorage] = 1;

            foreach (var index in indexes)
                if (!sort.Contains(index))
                    sort[index] = 1;

            return new BsonDocument("$sort", sort);
        }

        if (tieBreak && !sort.Contains(KeyStorage))
            sort[KeyStorage] = 1;

        return new BsonDocument("$sort", sort);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>The alias a lookup or an inline resolve joins under; null for every other stage.</summary>
    private static string? JoinAlias(BoundStage stage) => stage switch
    {
        BoundStage.Lookup lookup => lookup.As,
        BoundStage.Resolve resolve when !IsKeyed(resolve) => resolve.As,
        _ => null,
    };

    /// <summary>
    /// Whether a local join is needed: a later stage other than a projection reads its alias,
    /// or the final row shows it. A projection that names the alias only passes it on.
    /// </summary>
    private static bool JoinUsed(BoundPipeline bound, int index, string alias) =>
        bound.Stages.Skip(index + 1).Any(stage => stage is not BoundStage.Project && Reads(stage, alias))
        || Shown(bound.FinalShape, alias);

    /// <summary>
    /// Whether the join at <paramref name="position"/> runs: a later stage other than a projection
    /// reads its alias, or the final row shows it (a keyed resolve runs only for the rows it shows).
    /// Explain reads it so a join the compiler leaves out is placed nowhere.
    /// </summary>
    public static bool JoinRuns(BoundPipeline bound, int position, string alias)
    {
        ArgumentNullException.ThrowIfNull(bound);

        return bound.Stages[position] is BoundStage.Resolve resolve && IsKeyed(resolve)
            ? Shown(bound.FinalShape, alias)
            : JoinUsed(bound, position, alias);
    }

    /// <summary>Whether the final row carries an alias: it is still a root and no projection after it dropped it.</summary>
    private static bool Shown(Shape shape, string alias) => shape.Carries(alias);

    /// <summary>
    /// Whether a join can run after the page is taken. It can when no later stage filters,
    /// orders, unwinds, groups or resolves through its alias, no group replaces the row, and
    /// every later projection passes the alias whole: the page then holds the same rows, and
    /// the join adds the same value to each of them, whether it runs before the sort or
    /// after the limit. Explain reads it for a join step's phase (DESIGN §4.3).
    /// </summary>
    public static bool JoinsAfterPage(IReadOnlyList<BoundStage> stages, int index, string alias)
    {
        for (var later = index + 1; later < stages.Count; later++)
        {
            switch (stages[later])
            {
                case BoundStage.Group:
                    return false;

                case BoundStage.Project project:
                    if (!PassesWhole(project, alias))
                        return false;
                    break;

                // A lookup on the alias reads the joined parent, which is there after the page
                // too when that lookup also joins after the page: the late joins run in stage order.
                case BoundStage.Lookup lookup when lookup.On == alias && JoinsAfterPage(stages, later, lookup.As):
                    break;

                case var stage when Reads(stage, alias):
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a projection leaves the alias as the join produces it: an inclusion that names
    /// the alias itself and nothing below it, or an exclusion that names nothing under it. An
    /// inclusion that drops the alias or keeps a member below it changes what the alias holds,
    /// and the join stays before it.
    /// </summary>
    private static bool PassesWhole(BoundStage.Project project, string alias) => project.Inclusion
        ? project.Paths.Any(path => RootIs(path, alias) && !Under(path, alias)) && !project.Paths.Any(path => Under(path, alias))
        : !project.Paths.Any(path => RootIs(path, alias));

    /// <summary>Whether a later stage the count pipeline carries reads the alias; a sort or a projection is not in the count.</summary>
    private static bool CountReads(IEnumerable<BoundStage> later, string alias) =>
        later.Any(stage => stage is not (BoundStage.Sort or BoundStage.Project) && Reads(stage, alias));

    /// <summary>Whether a stage reads a path at or under the alias.</summary>
    private static bool Reads(BoundStage stage, string alias) => stage switch
    {
        BoundStage.Match match => Paths(match.Condition).Any(path => RootIs(path, alias)),
        BoundStage.Sort sort => sort.Fields.Any(field => RootIs(field.Path, alias)),
        BoundStage.Unwind unwind => RootIs(unwind.Path, alias),
        BoundStage.Group group => group.Keys.Any(key => (key.Path is not null && RootIs(key.Path, alias)) || (key.Trunc is not null && RootIs(key.Trunc.Path, alias)))
            || group.Fields.Any(field => field.Argument is not null && Paths(field.Argument).Any(path => RootIs(path, alias))),
        BoundStage.Project project => project.Paths.Any(path => RootIs(path, alias)),
        BoundStage.Resolve resolve => RootIs(resolve.Reference, alias),
        BoundStage.Lookup lookup => lookup.On == alias,
        _ => false,
    };

    private static bool RootIs(ResolvedPath path, string alias) => path.Root.StoragePrefix == alias || path.Wire == alias || Under(path, alias);

    private static bool Under(ResolvedPath path, string alias) => path.Wire.StartsWith(alias + ".", StringComparison.Ordinal);

    private static IEnumerable<ResolvedPath> Paths(BoundCondition condition) => condition switch
    {
        BoundCondition.And and => and.Conditions.SelectMany(Paths),
        BoundCondition.Or or => or.Conditions.SelectMany(Paths),
        BoundCondition.Not not => Paths(not.Condition),
        BoundCondition.Any any => [any.Path],
        BoundCondition.Leaf leaf => [leaf.Path],
        _ => [],
    };

    private static IEnumerable<ResolvedPath> Paths(BoundExpression expression) => expression switch
    {
        BoundExpression.Path path => [path.Resolved],
        BoundExpression.Arithmetic arithmetic => arithmetic.Operands.SelectMany(Paths),
        _ => [],
    };

    private static BsonDocument AddIf(this BsonDocument document, bool condition, string name, string? value)
    {
        if (condition && value is not null)
            document[name] = value;

        return document;
    }
}
