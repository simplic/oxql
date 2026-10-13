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
internal sealed record SemiJoinSlot(BoundCondition.Leaf Leaf, BsonArray Ids);

/// <summary>
/// The field a flattening unwind writes into every row it produces: true when the collection the
/// row came from had items nested deeper than <paramref name="Depth"/> levels, which are not in
/// the rows. <paramref name="Stage"/> and <paramref name="Path"/> are the caller's, for the diagnostic.
/// </summary>
internal sealed record FlattenProbe(string Field, int Stage, string Path, int Depth);

/// <summary>
/// The field a lookup writes into every row it joins: true when some parent had more children
/// than <paramref name="Limit"/>, of which only the first <paramref name="Limit"/> are under the
/// alias. <paramref name="Stage"/> and <paramref name="Alias"/> are the caller's, for the diagnostic.
/// </summary>
internal sealed record LookupFlag(string Field, int Stage, string Alias, int Limit);

/// <summary>
/// An inline resolve whose outcomes the executor reads off the page rows: a row whose
/// reference at <paramref name="ReferenceStorage"/> holds a value and whose alias is null is
/// <c>not_found</c>, or <c>excluded</c> when <paramref name="ExistsFlag"/> says the unfiltered
/// target has the record (a filtered resolve); a row whose <paramref name="AmbiguityFlag"/> is set
/// joined a key two records hold (a target onto a member that is not its key) and is
/// <c>ambiguous</c>. The compiler keeps the reference and the flags in storage against projections.
/// </summary>
internal sealed record InlineProbe(int Stage, BoundStage.Resolve Resolve, string ReferenceStorage, string? AmbiguityFlag = null, string? ExistsFlag = null);

/// <summary>
/// A strict request's check that no candidate row lost children to a truncation before a later
/// match filtered it: <paramref name="Stages"/> run the pipeline up to the stage that
/// sets the flag and keep one flagged row; any row means the page's answer may depend on what was
/// cut. <paramref name="Code"/> is <c>LOOKUP_TRUNCATED</c> or <c>UNWIND_DEPTH_TRUNCATED</c>, and
/// <paramref name="Stage"/>, <paramref name="Path"/> and <paramref name="Bound"/> (the limit or the
/// depth) are the caller's, for the refusal.
/// </summary>
internal sealed record TruncationProbe(string Code, int Stage, string Path, int Bound, IReadOnlyList<BsonDocument> Stages);

/// <summary>What the compiler emits: the page and count pipelines, and what the executor still has to do.</summary>
internal sealed record CompiledQuery
{
    public required BoundPipeline Bound { get; init; }

    public required IReadOnlyList<BsonDocument> PageStages { get; init; }

    public IReadOnlyList<BsonDocument>? CountStages { get; init; }

    /// <summary>
    /// The position in <see cref="CountStages"/> of the bound on the parents of an unwind, where the
    /// count has one: a <c>$limit</c> of the count's own bound plus one, before the unwind. Every
    /// parent that reaches it yields a row that is counted (the filter before it keeps exactly those),
    /// so as many parents as the count is bounded by rows hold at least that many rows, and the
    /// answer of the step is the same with it. The database then stops reading at that many parents,
    /// also where it would otherwise fill a buffer of narrowed rows before the first is unwound.
    /// The first step of a count alone keeps it; a later one, sized to read as much as the time
    /// allows, leaves it out (<c>MongoQueryEngine.CountLadderAsync</c>). Null where the count's stages
    /// bound the rows alone.
    /// </summary>
    public int? CountParentsAt { get; init; }

    /// <summary>
    /// Whether the filter before <see cref="CountParentsAt"/> asks only that a parent yields a row
    /// (no condition of the request is asked there): it is there for the bound alone, since the
    /// unwind drops such a parent itself, and goes where the bound goes.
    /// </summary>
    public bool CountParentsFilterIsTheBounds { get; init; }

    public required int Limit { get; init; }

    public required int Offset { get; init; }

    public required PagingMode PagingMode { get; init; }

    public required bool IncludeTotalCount { get; init; }

    /// <summary>The sort fields whose values the next cursor carries.</summary>
    public required IReadOnlyList<BoundSortField> SortFields { get; init; }

    /// <summary>
    /// The resolves the keyed fetch runs after the page, in stage order: every
    /// resolve on the keyed executor, remote or local, whose alias or owning row the row shows.
    /// </summary>
    public required IReadOnlyList<BoundStage.Resolve> KeyedResolves { get; init; }

    public required IReadOnlyList<SemiJoinSlot> SemiJoins { get; init; }

    public required int MaxTimeMs { get; init; }

    public bool? AllowDiskUse { get; init; }

    /// <summary>The cap the count stops at: the request's own when it gave one, otherwise the host's.</summary>
    public required int CountCap { get; init; }

    /// <summary>The count step a contract 2 page asked for; null for none, and for the count of <c>includeTotalCount</c>.</summary>
    public BoundCount? Count { get; init; }

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

    /// <summary>
    /// The positions (in <see cref="BoundPipeline.Stages"/>) of the projections emitted after the page
    /// is taken instead of where they were written; explain says so at each.
    /// </summary>
    public IReadOnlyList<int> LateProjections { get; init; } = [];

    /// <summary>
    /// The positions of the unwinds a keyset page of unwound rows is ordered by after the sort and
    /// the key, by the field each is written to (the caller's <c>includeIndex</c>, or a reserved
    /// name): the next cursor reads them off the page's last row. Empty for every other page.
    /// </summary>
    public IReadOnlyList<string> PagingIndexes { get; init; } = [];

    /// <summary>How the one unwind of the request was placed; null where the stages run as written.</summary>
    public UnwindPlacement? Unwind { get; init; }
}

/// <summary>
/// How the compiler placed the one unwind of a contract 2 request (the stage at
/// <paramref name="Position"/> of <see cref="BoundPipeline.Stages"/>), each only where the rows and
/// their order stay what the request as written answers:
/// <list type="bullet">
/// <item><paramref name="ParentsFirst"/>: the rows are ordered and cut to what the page can reach
/// before the unwind, so the unwind and the page's own sort see a page's worth of parents instead of
/// every candidate row. It holds when the order of the page is the parents' order (every sort field
/// before the key is a member of the entity) and every row kept yields a row of the page (each has
/// an element, and what filters after the unwind is applied before it as well). It also holds when
/// the element's position follows those members before the key (<see cref="IndexAscending"/>).</item>
/// <item><paramref name="Prefiltered"/>: the paths under the alias whose conditions, written after
/// the unwind, are also asked of the collection before it ("some element passes them all"), so a
/// row without such an element is not unwound. Empty when there is none.</item>
/// <item><paramref name="RenameLate"/>: the element is written under its alias, and the collection
/// taken out of the row, after the page is taken; until then the stages read the element where the
/// unwind left it.</item>
/// </list>
/// </summary>
internal sealed record UnwindPlacement(int Position, bool ParentsFirst, IReadOnlyList<string> Prefiltered, bool RenameLate)
{
    /// <summary>
    /// Set where the parents are cut first although the sort names the unwind's position
    /// (<c>includeIndex</c>) before the key: the direction of that position. The rows of parents that
    /// tie on the members before it then interleave by position, so the parents are ordered by those
    /// members, then by the first position each still has a row at, then by the key; the page's first
    /// rows lie in the first parents of that order (<see cref="MongoCompiler.PlaceUnwind(BoundPipeline, bool)"/>). Null where
    /// the key stands before the position, and the order within a parent needs no such care.
    /// </summary>
    public bool? IndexAscending { get; init; }
}

/// <summary>Where a join that runs is emitted (<see cref="MongoCompiler.PhaseOf(BoundPipeline, int, string, bool)"/>).</summary>
internal enum JoinPhase
{
    /// <summary>Where it was written, for every row that reaches it.</summary>
    BeforePage,

    /// <summary>On the parents ordered and cut before their unwind: before the page, for a page's worth of rows.</summary>
    Parents,

    /// <summary>After the page is taken, for the page's rows.</summary>
    AfterPage,
}

/// <summary>What the compiler needs from the host beside the bound pipeline; a null collation is the default one.</summary>
internal sealed record CompileOptions(int MaxTimeMs, bool? AllowDiskUse, int CountCap, CollationOptions? Collation = null)
{
    /// <summary>
    /// Whether a projection or a join's select that keeps a member below a polymorphic object also
    /// keeps that object's discriminator, so the row is encoded by the variant it is stored as (a
    /// contract 2 row; a contract 1 row is the document as stored and would show it).
    /// </summary>
    public bool KeepDiscriminators { get; init; }

    /// <summary>
    /// Whether the stages are emitted in the order the caller wrote them (contract 1, whose pipelines
    /// stay what they were). Otherwise the compiler places a stage where it costs the database least
    /// while the rows stay the same: a projection after the page is taken
    /// (<see cref="MongoCompiler.ProjectsAfterPage"/>), the rename of an unwound element after it too,
    /// the order and the cut of the parents before their unwind, the rows without a passing element
    /// dropped before it (<see cref="UnwindPlacement"/>), and a cursor's position also as one range on
    /// the leading sort field.
    /// </summary>
    public bool AsWritten { get; init; }
}

/// <summary>
/// Emits the aggregation pipeline for a bound pipeline: typed <c>$match</c>, string
/// comparisons under the collation or as patterns, <c>$elemMatch</c>, joins as indexed
/// <c>$lookup</c> with the scope inside and after the page when only the rows' display reads
/// them, null-aware keyset cursors merged into the leading scope match, offset paging after a
/// group, and the count pipeline with its <c>$limit</c> after any <c>$group</c>.
/// </summary>
internal static class MongoCompiler
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

    /// <summary>The variable a quotient reads its divisor under.</summary>
    private const string DivisorVariable = "oxDivisor";

    /// <summary>The variable a join binds its local key to when the sub-pipeline has to compare it byte for byte.</summary>
    private const string KeyVariable = "oxKey";

    /// <summary>The prefix of the position a values stage numbers the elements of a collection it unwinds by; no row keeps it.</summary>
    private const string ReservedValuesIndex = Aliases.ReservedPrefix + "oxVal";

    /// <summary>The member a values stage notes under, per collection it unwinds, whether the collection is there and holds no element.</summary>
    private const string ReservedValuesEmpty = Aliases.ReservedPrefix + "oxValE";

    /// <summary>The member of a values answer that holds the values: each its <c>_id</c> and, under <see cref="ValuesCount"/>, the rows that hold it.</summary>
    public const string ValuesList = "v";

    /// <summary>The member of a values answer that holds the one total: under <see cref="ValuesRows"/> the rows the stage read.</summary>
    public const string ValuesTotal = "t";

    /// <summary>The member of a value that holds the number of rows (elements, under a collection) with it.</summary>
    public const string ValuesCount = "c";

    /// <summary>The member that holds the number of rows read: of a value, and of the total.</summary>
    public const string ValuesRows = "r";

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

    /// <summary>
    /// The collation of the aggregate being compiled on this thread, which a folding
    /// <c>contains</c> or <c>endsWith</c> builds its accent-folding pattern by. A filter
    /// compiled outside <see cref="Compile"/> folds by the default collation.
    /// </summary>
    [ThreadStatic]
    private static CollationOptions? folding;

    /// <summary>
    /// Whether the patterns compiled on this thread end as a contract 1 pattern always has, with
    /// <c>$</c> (see <see cref="End"/>); set for an aggregate compiled as written.
    /// </summary>
    [ThreadStatic]
    private static bool dollarEnd;

    /// <summary>
    /// The end of a pattern the engine builds from a text: the very end of the value, which is
    /// <c>\z</c>. (<c>$</c> is that place and one more, before a line feed that ends the value: a
    /// text is found where the value ends, and nowhere before it.) The start anchor needs no
    /// such care: without the multi-line option, which the engine never sets, <c>^</c> is the start
    /// of the value. A contract 1 pattern ends as it always has.
    /// </summary>
    private static string End => dollarEnd ? "$" : @"\z";

    public static CompiledQuery Compile(BoundPipeline bound, CompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(options);

        var outer = folding;
        var outerEnd = dollarEnd;

        folding = options.Collation ?? new CollationOptions();
        dollarEnd = options.AsWritten;

        try
        {
            return CompileUnder(bound, options);
        }
        finally
        {
            folding = outer;
            dollarEnd = outerEnd;
        }
    }

    private static CompiledQuery CompileUnder(BoundPipeline bound, CompileOptions options)
    {
        var page = bound.Page;
        var stages = new List<BsonDocument>();
        var keyed = new List<BoundStage.Resolve>();
        var semiJoins = new List<SemiJoinSlot>();
        var sortFields = Ordering(bound.Sort?.Fields ?? []);
        var collated = bound.Collated;

        var grouped = bound.FinalShape.Grouped;
        var keysetPaging = bound.PagingMode == PagingMode.Keyset;

        // Rows of an unwound shape share their parent's key, so the key alone does not order
        // them: the index of every unwind completes it. A group replaces the row, and its keys
        // are the order, so this is the unwound case only.
        // `bound.Sort` is the sort the indexes complete; without one there is nothing to
        // complete, and an index injected here would reach the rows with nothing to remove it.
        var unwoundPaging = !grouped && bound.Sort is not null && (bound.PagingMode == PagingMode.Offset || bound.UnwoundRows);
        var reservedIndexes = new List<string>();

        // Where the one unwind of a contract 2 request is placed (null: as written), and whether its
        // parents each yield a counted row once asked what a cut of the parents asks.
        var parentsYield = false;
        var unwinding = options.AsWritten ? null : PlaceUnwind(bound, collated, out parentsYield);
        int? countParentsAt = null;
        var countParentsFilterIsTheBounds = false;
        var probes = new List<FlattenProbe>();
        var flags = new List<LookupFlag>();

        // The flags of the lookups that join before the page: only they are in the rows a
        // projection or a group reshapes. A late lookup writes its flag after both.
        var rowFlags = new List<string>();
        IReadOnlyList<BoundSortField>? pagingSort = null;

        // The key orders and identifies a page. A projection that drops it leaves the sort and
        // the cursor reading a member that is not there, so it is kept in storage and dropped
        // again on the way out.
        var keepKey = (keysetPaging && !grouped) || unwoundPaging;
        var keyKeptAgainstProjection = false;

        // The same rule as the key, for the sort. NextCursor mints each leg by reading the
        // value off the document it hands back, so the sort paths survive every projection in
        // storage: a leg read from a member that is not there is the value null, and a null
        // leg re-admits every non-null row, which pages the first page for ever.
        var sortStorages = keepKey || keysetPaging
            ? sortFields.Select(field => field.Path.Storage).Where(storage => storage is not null).Select(storage => storage!).Distinct(StringComparer.Ordinal).ToList()
            : [];
        var sortKeptAgainstProjection = new List<string>();

        // The leading scope, with the keyset predicate merged in on a root shape.
        var scope = ScopeFilter(bound.Scope);
        var position = page.Cursor is { Mode: PagingMode.Keyset } cursor ? cursor : null;

        // The rows after the cursor's position. On unwound rows the position reads the unwinds'
        // indexes, which exist only after them: it is built with the paging sort, after the loop.
        var keyset = position is null || unwoundPaging ? null
            : KeysetConjuncts(Legs(position, sortFields, key: !grouped, [], repeatKey: options.AsWritten && !grouped), !options.AsWritten);

        // A sum or an average of floating-point values is computed anew for every page, and the order
        // its terms are added in is the server's: the last group's value may differ from the one its
        // cursor was minted from in the last digit, and then lie after the position it stood at. The
        // group the position names was served, whatever its value is now: it is left out by its keys.
        if (grouped && keyset is not null && position is not null && bound.Stages.OfType<BoundStage.Group>().LastOrDefault() is { Keys.Count: > 0 } paged
            && sortFields.Any(field => field.Path.Kind == Kind.Double && !paged.Keys.Any(key => key.As == field.Path.Storage)))
        {
            var served = new BsonDocument();

            foreach (var key in paged.Keys)
                served[key.As] = position.Fields.FirstOrDefault(value => value.Wire == sortFields.First(field => field.Path.Storage == key.As).Path.Wire)?.Value is { IsBsonNull: false, IsBsonUndefined: false } value ? value : BsonNull.Value;

            keyset.Add(new BsonDocument("$nor", new BsonArray { served }));
        }

        // A sort path under a local resolve alias has no value before the join writes it, and a
        // group's keys and aggregates none before the group, so the keyset condition on them is
        // evaluated right before the paging sort instead, where every sort path holds the value
        // the cursor was minted from.
        var joinedKeyset = keyset is not null && (grouped || (bound.Sort is not null && sortFields.Any(field => field.Path.Root.StoragePrefix.Length > 0)))
            ? keyset
            : null;

        if (joinedKeyset is not null)
            keyset = null;

        // Parents first: a parent the position lies in or before passes the leading match, where an
        // index on the sort bounds it; the rows of the parent the position lies in are cut after the unwind.
        if (position is not null && unwinding is { ParentsFirst: true, IndexAscending: null })
            keyset = KeysetConjuncts(Legs(position, ParentFields(sortFields), key: true, []), !options.AsWritten, inclusive: true);

        // The position stands before the key: every parent that ties with the position's on the members
        // before it may still have a row to come, so the bound is on those members alone.
        if (position is not null && unwinding is { ParentsFirst: true, IndexAscending: not null })
            keyset = LeadingFields(sortFields) is { Count: > 0 } leading ? KeysetConjuncts(Legs(position, leading, key: false, []), !options.AsWritten, inclusive: true) : null;

        stages.Add(new BsonDocument("$match", keyset is null ? scope : new BsonDocument("$and", new BsonArray { scope }.AddRange(keyset))));

        var countStages = page.IncludeTotalCount || page.Count is not null ? new List<BsonDocument> { new("$match", scope) } : null;

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

        // A sort a group follows orders what the group's first, last and push see. Rows that tie in it
        // have no order of their own, and the database is free to hand them over in another one for
        // another page: so such a sort is completed by the entity's key and the position of every
        // element unwound before it, and the same rows are first and last whatever page is asked.
        // (A contract 1 request is compiled as written.)
        var groupOrders = new HashSet<int>();
        var orderIndexes = new List<string>();

        if (!options.AsWritten)
            for (int at = 0, pending = -1; at < bound.Stages.Count; at++)
            {
                if (bound.Stages[at] is BoundStage.Sort)
                    pending = at;
                else if (bound.Stages[at] is BoundStage.Group)
                {
                    if (pending >= 0)
                        groupOrders.Add(pending);

                    // After a group the rows have neither the key nor a position.
                    break;
                }
            }

        // A join only the rows' display reads runs after the page is taken: the sort stays
        // next to the limit, which lets the server keep the top rows instead of ordering
        // every candidate, and the join is paid per page row rather than per candidate row.
        var lateJoins = new List<BsonDocument>();

        // A join only the display reads, written before the unwind whose parents are ordered and cut
        // first: it runs on those parents, once each, between their cut and the unwind.
        var parentJoins = new List<BsonDocument>();

        // A projection the page does not depend on runs after the page is taken as well: the server
        // then cuts the page's rows to their columns, not every candidate row before it orders them.
        var lateProjections = new List<BsonDocument>();
        var lateProjectionStages = new List<int>();

        // The rename of an unwound element that waits for the page, and the alias the stages
        // until then read where the unwind left it.
        var lateRename = new List<BsonDocument>();
        (string Alias, string Storage)? renamed = null;

        // The local key each of those joins matches on. It is read only after the page, so it
        // survives every projection in between in storage, as a remote resolve's reference
        // does; the wire row follows the shape and leaves it out when it was not asked for.
        var lateJoinKeys = new List<string>();

        // The reference of an inline resolve whose missing rows are reported is read
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
            if (JoinAlias(stage) is { } joined && !JoinUsed(bound, index, joined) && !(OutcomeOf(stage) is { } told && JoinUsed(bound, index, told)))
                continue;

            switch (stage)
            {
                case BoundStage.Match match:
                    emitted.Add(new BsonDocument("$match", Renamed(Filter(match.Condition, semiJoins, collated), renamed)));
                    break;

                // PhaseOf decides where a join runs, which explain reports (JOIN_BEFORE_PAGE /
                // JOIN_AFTER_PAGE); a join a later lookup's 'on' reads stays before the page unless
                // that lookup is only displayed as well (see PhaseOf).
                case BoundStage.Lookup lookup when JoinsAfterPage(bound, index, lookup.As, options.AsWritten):
                    lateJoins.AddRange(Lookup(lookup, semiJoins, collated, Flag(lookup, flags), options.KeepDiscriminators));
                    lateJoinKeys.Add(lookup.ParentKeyStorage);
                    break;

                // On the parents cut before the unwind: its key and its flag are read after every
                // projection written in between, so both survive them in storage.
                case BoundStage.Lookup lookup when PhaseOf(bound, index, lookup.As, options.AsWritten) == JoinPhase.Parents:
                    var parentFlag = Flag(lookup, flags);

                    if (parentFlag is not null)
                        rowFlags.Add(parentFlag.Field);

                    parentJoins.AddRange(Lookup(lookup, semiJoins, collated, parentFlag, options.KeepDiscriminators));
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
                // the alias, the owning row and every alias continued under them leaves nothing to
                // fetch. A match under a remote alias is a semi-join, which fetches its ids apart from this.
                case BoundStage.Resolve keyedResolve when IsKeyed(keyedResolve) && !KeyedRuns(bound, keyedResolve):
                    break;

                case BoundStage.Resolve keyedResolve when IsKeyed(keyedResolve):
                    keyed.Add(keyedResolve);
                    break;

                case BoundStage.Resolve resolve when JoinsAfterPage(bound, index, resolve, options.AsWritten):
                    lateJoins.AddRange(LocalResolve(resolve, semiJoins, collated, Probe(bound, index, resolve, inlineProbes, probeKeys), options.KeepDiscriminators));
                    lateJoinKeys.Add(resolve.Reference.Storage!);
                    break;

                case BoundStage.Resolve resolve when PhaseOf(bound, index, resolve, options.AsWritten) == JoinPhase.Parents:
                    parentJoins.AddRange(LocalResolve(resolve, semiJoins, collated, Probe(bound, index, resolve, inlineProbes, probeKeys), options.KeepDiscriminators));
                    lateJoinKeys.Add(resolve.Reference.Storage!);
                    break;

                case BoundStage.Resolve resolve:
                    emitted.AddRange(LocalResolve(resolve, semiJoins, collated, Probe(bound, index, resolve, inlineProbes, probeKeys), options.KeepDiscriminators));
                    break;

                case BoundStage.Unwind unwind:
                    // The caller's index when it asked for one, otherwise a reserved one the
                    // paging sort uses and the pipeline drops again before it skips.
                    var indexField = unwind.IncludeIndex;

                    if (unwoundPaging && indexField is null)
                        reservedIndexes.Add(indexField = ReservedIndex + reservedIndexes.Count);
                    else if (unwoundPaging)
                        reservedIndexes.Add(indexField!);
                    else if (groupOrders.Any(order => order > index))
                        orderIndexes.Add(indexField ??= ReservedIndex + "G" + orderIndexes.Count);

                    var placed = unwinding is not null && unwinding.Position == index ? unwinding : null;

                    // The rows no element of which passes what is asked of the element after the
                    // unwind are not unwound: the page and the count read fewer rows, the same ones.
                    if (placed is not null && UnwindPrefilter(bound, index, collated, parents: placed.ParentsFirst) is { } prefilter)
                        stages.Add(new BsonDocument("$match", prefilter));

                    // The count unwinds fewer rows for the same reason. Where every parent that passes what a
                    // cut of the parents asks yields a counted row, it asks exactly that and bounds the parents
                    // by its own bound (CountParentsAt); elsewhere it asks what spares the unwind.
                    // (It is the request's one unwind: nothing says of another that its parents yield.)
                    if (countStages is not null && parentsYield)
                    {
                        if (UnwindPrefilter(bound, index, collated, parents: true) is { } yielding)
                        {
                            countStages.Add(new BsonDocument("$match", yielding));
                            countParentsFilterIsTheBounds = UnwindFilter.Of(bound, index) is { Element.Count: 0, Parent.Count: 0 };
                        }

                        countParentsAt = countStages.Count;
                        countStages.Add(new BsonDocument("$limit", 0));
                    }
                    else if (placed is not null && countStages is not null && UnwindPrefilter(bound, index, collated, parents: false) is { } unwoundCount)
                    {
                        countStages.Add(new BsonDocument("$match", unwoundCount));
                    }

                    // The parents in the page's order, cut to what the page can reach: each yields a row,
                    // so the page's rows come from the first of them (one more than the page for the row
                    // that tells a next page exists, and one more for the parent a cursor's position lies in).
                    if (placed is { ParentsFirst: true })
                    {
                        var reach = (long)page.Limit + 1 + (position is null ? 0 : 1) + (page.Cursor is { Mode: PagingMode.Offset } skipped ? skipped.Offset : page.Offset);

                        if (placed.IndexAscending is { } indexAscending)
                        {
                            stages.AddRange(ParentsByPosition(unwind, sortFields, indexAscending, position, reach));
                        }
                        else
                        {
                            stages.Add(Sort(ParentFields(sortFields), tieBreak: true));
                            stages.Add(new BsonDocument("$limit", reach));
                        }

                        // The joins written before the unwind that only the display reads: once per parent kept.
                        stages.AddRange(parentJoins);
                        parentJoins.Clear();
                    }

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

                    // Written under its alias after the page is taken, for the page's rows only: until
                    // then the stages read the element where the unwind left it.
                    if (placed is { RenameLate: true })
                    {
                        lateRename.Add(new BsonDocument("$set", new BsonDocument(unwind.As!, "$" + unwind.Path.Storage)));
                        lateRename.Add(new BsonDocument("$unset", unwind.Path.Storage));
                        renamed = (unwind.As!, unwind.Path.Storage!);
                    }
                    else
                    {
                        if (unwind.As is not null)
                            emitted.Add(new BsonDocument("$set", new BsonDocument(unwind.As, "$" + unwind.Path.Storage)));

                        // keepPath: false — the element lives on under the alias only.
                        if (unwind is { KeepPath: false, As: not null })
                            emitted.Add(new BsonDocument("$unset", unwind.Path.Storage));
                    }

                    if (unwind.Flatten is { } cut && bound.Strict && FilteredLater(bound.Stages, index, unwind.As ?? unwind.Path.Wire))
                        truncationProbes.Add(new TruncationProbe(Codes.UnwindDepthTruncated, cut.Stage, unwind.Path.Wire, cut.Depth, Probing(stages, emitted, probes[^1].Field)));
                    break;

                case BoundStage.Group group:
                    emitted.AddRange(Group(group, [.. probes.Select(probe => probe.Field), .. rowFlags]));
                    break;

                case BoundStage.Values values:
                    emitted.AddRange(Values(values, collated));
                    break;

                // A sort that only conditions follow up to the values orders rows nobody sees in that
                // order: it would read and order every row before the stage's bound on the rows read
                // could apply, so it is not run. The values are the same whatever the order of the rows.
                case BoundStage.Sort when bound.Values is not null && bound.Stages.Skip(index + 1).All(later => later is BoundStage.Match or BoundStage.Values or BoundStage.Page):
                    break;

                case BoundStage.Project project:
                    if (Project(project, keyed, [.. lateJoinKeys, .. probeKeys.Where(key => !lateJoinKeys.Contains(key, StringComparer.Ordinal))], keepKey,
                        [.. reservedIndexes, .. orderIndexes, .. probes.Select(probe => probe.Field), .. rowFlags, .. InlineFlags(inlineProbes), .. options.KeepDiscriminators && project.Inclusion ? DiscriminatorsOf(project.Paths) : []],
                        sortStorages, sortKeptAgainstProjection, ref keyKeptAgainstProjection, wholeCollections: options.AsWritten) is { } projection)
                    {
                        if (!options.AsWritten && ProjectsAfterPage(bound, index))
                        {
                            lateProjections.Add(projection);
                            lateProjectionStages.Add(index);
                        }
                        else
                        {
                            emitted.Add(projection);
                        }
                    }
                    break;

                // The last sort is the one the page is taken in, so on an unwound shape it is
                // emitted after the loop with the tie-breakers that make it total. Any earlier
                // sort is the caller's own and stays where it was written.
                case BoundStage.Sort sort when unwoundPaging && ReferenceEquals(sort, bound.Sort):
                    pagingSort = sortFields;
                    break;

                case BoundStage.Sort sort:
                    if (joinedKeyset is not null && ReferenceEquals(sort, bound.Sort))
                        emitted.Add(new BsonDocument("$match", joinedKeyset.Count == 1 ? joinedKeyset[0] : new BsonDocument("$and", new BsonArray(joinedKeyset))));

                    // A group's keys are its order's tie-break already: the rows of a group have no key.
                    emitted.Add(new BsonDocument("$sort", Renamed((groupOrders.Contains(index)
                        ? Sort(sort.Fields, tieBreak: true, orderIndexes.Count > 0 ? orderIndexes : null)
                        : Sort(sort.Fields, keysetPaging && !grouped))["$sort"].AsBsonDocument, renamed)));
                    sortEmitted = true;
                    break;

                case BoundStage.Page:
                    break;
            }

            stages.AddRange(emitted);

            // The count pipeline carries everything up to the sort and the page; a join only
            // when a later match, unwind, group or resolve reads its alias. What only the
            // rows' display reads does not change how many rows there are.
            if (countStages is not null && stage is not (BoundStage.Sort or BoundStage.Page)
                && (JoinAlias(stage) is not { } alias || CountReads(bound.Stages.Skip(index + 1), alias) || (OutcomeOf(stage) is { } counted && CountReads(bound.Stages.Skip(index + 1), counted))))
                countStages.AddRange(emitted);
        }

        if (window is not null)
        {
            stages.AddRange(window);
            countStages?.AddRange(window);
        }

        if (pagingSort is not null)
        {
            // The rows after the cursor's position, by the sort, the key and the position of each
            // unwind: every leg holds its value here, where the page is ordered.
            if (position is not null && keysetPaging)
            {
                var after = KeysetConjuncts(Legs(position, pagingSort, key: true, reservedIndexes, renamed), !options.AsWritten);

                stages.Add(new BsonDocument("$match", after.Count == 1 ? after[0] : new BsonDocument("$and", new BsonArray(after))));
            }

            stages.Add(new BsonDocument("$sort", Renamed(Sort(pagingSort, tieBreak: true, reservedIndexes)["$sort"].AsBsonDocument, renamed)));

            // What ordered the page and was never asked for has no business in a row. An offset
            // cursor carries only its offset, so the key is free to go here; a keyset cursor
            // reads the key and the indexes off the last row and keeps them (a contract 2 row
            // follows the shape and shows neither; the key is stripped from a contract 1 row instead).
            var drop = keysetPaging ? [] : reservedIndexes.Where(name => name.StartsWith(ReservedIndex, StringComparison.Ordinal)).ToList();

            // A join after the page that matches on the key still needs it; the wire row leaves
            // it out by the shape.
            if (!keysetPaging && keyKeptAgainstProjection && !lateJoinKeys.Contains(KeyStorage, StringComparer.Ordinal))
            {
                drop.Add(KeyStorage);
                keyKeptAgainstProjection = false;
            }

            if (drop.Count > 0)
                stages.Add(new BsonDocument("$unset", new BsonArray(drop)));
        }
        else if (!sortEmitted && keysetPaging && !grouped)
        {
            stages.Add(new BsonDocument("$sort", new BsonDocument(KeyStorage, 1)));
        }

        var offset = page.Cursor is { Mode: PagingMode.Offset } offsetCursor ? offsetCursor.Offset : page.Offset;

        if (offset > 0)
            stages.Add(new BsonDocument("$skip", offset));

        // A values stage cuts its own list and answers one document; there is no page to cut.
        if (bound.Values is null)
            stages.Add(new BsonDocument("$limit", page.Limit + 1));
        stages.AddRange(lateRename);
        stages.AddRange(lateProjections);
        stages.AddRange(lateJoins);

        // The request's own count cap, when it gave one, is already under the host's.
        var countCap = page.Count?.Rows ?? page.CountCap ?? options.CountCap;

        if (countStages is not null)
        {
            if (countParentsAt is { } parentsAt)
                countStages[parentsAt] = new BsonDocument("$limit", countCap + 1);

            countStages.Add(new BsonDocument("$limit", countCap + 1));
            countStages.Add(new BsonDocument("$count", "n"));
        }

        return new CompiledQuery
        {
            Bound = bound,
            PageStages = stages,
            CountStages = countStages,
            CountParentsAt = countParentsAt,
            CountParentsFilterIsTheBounds = countParentsFilterIsTheBounds,
            Limit = page.Limit,
            Offset = offset,
            PagingMode = bound.PagingMode,
            IncludeTotalCount = page.IncludeTotalCount,
            SortFields = bound.Sort?.Fields ?? [],
            KeyedResolves = keyed,
            SemiJoins = semiJoins,
            MaxTimeMs = options.MaxTimeMs,
            AllowDiskUse = options.AllowDiskUse,
            CountCap = countCap,
            Count = page.Count,
            KeyKeptAgainstProjection = keyKeptAgainstProjection,
            SortKeptAgainstProjection = sortKeptAgainstProjection,
            Collation = collated ? CollationDocument(options.Collation ?? new CollationOptions()) : null,
            FlattenProbes = probes,
            LookupFlags = flags,
            InlineProbes = inlineProbes,
            TruncationProbes = truncationProbes,
            LateProjections = lateProjectionStages,
            PagingIndexes = keysetPaging && pagingSort is not null
                ? reservedIndexes.Where(name => !pagingSort.Any(field => field.Path.Storage == name)).Distinct(StringComparer.Ordinal).ToList()
                : [],
            Unwind = unwinding,
        };
    }

    // ---- the one unwind of a contract 2 request ----------------------------------------------------

    /// <summary>
    /// Where the one unwind of a request is placed (<see cref="UnwindPlacement"/>); null when the
    /// request has none or several, is an internal owner query, or nothing about its unwind can move.
    /// </summary>
    public static UnwindPlacement? PlaceUnwind(BoundPipeline bound, bool collated = false) => PlaceUnwind(bound, collated, out _);

    /// <summary>
    /// <see cref="PlaceUnwind(BoundPipeline, bool)"/>, and whether every parent that passes what a cut
    /// of the parents asks (<see cref="UnwindPrefilter"/>) yields a row the count counts: nothing
    /// between the unwind and the page reshapes, groups or joins the rows, and what filters after the
    /// unwind is asked before it exactly. It is what the cut of the parents needs, less what only a
    /// page needs (an order that is the parents', a page by value).
    /// </summary>
    private static UnwindPlacement? PlaceUnwind(BoundPipeline bound, bool collated, out bool parentsYield)
    {
        ArgumentNullException.ThrowIfNull(bound);

        parentsYield = false;

        if (bound.KeyedBy is not null || bound.Stages.Count(stage => stage is BoundStage.Unwind) != 1)
            return null;

        var at = bound.Stages.ToList().FindIndex(stage => stage is BoundStage.Unwind);
        var unwind = (BoundStage.Unwind)bound.Stages[at];
        var grouped = bound.FinalShape.Grouped;
        var sortFields = Ordering(bound.Sort?.Fields ?? []);

        // A sort written before the unwind over the collection itself orders by what the collection
        // holds; as written it runs after the collection has left the row. Nothing about it moves.
        var sortsCollection = sortFields.Any(field => field.Path.Storage is { } storage && Covers(unwind.Path.Storage!, storage));

        // The rename waits for the page when every stage up to the page reads the element through a
        // filter or the paging sort (both read it where the unwind left it), or reads the row after
        // the page is taken (a projection, a join after the page, a keyed join).
        var renameLate = !grouped && !sortsCollection && unwind is { As: not null, KeepPath: false };
        var reshaped = false;

        for (var later = at + 1; later < bound.Stages.Count; later++)
        {
            switch (bound.Stages[later])
            {
                case BoundStage.Match or BoundStage.Page or ContinuedStage:
                    break;

                case BoundStage.Sort:
                    break;

                case BoundStage.Project when ProjectsAfterPage(bound, later):
                    break;

                case BoundStage.Resolve keyed when IsKeyed(keyed):
                    break;

                case BoundStage.Lookup lookup when !JoinRuns(bound, later, lookup.As) || DisplayOnly(bound, later, lookup.As):
                    break;

                case BoundStage.Resolve resolve when !JoinRuns(bound, later, resolve.As) || PhaseOf(bound, later, resolve) == JoinPhase.AfterPage:
                    break;

                default:
                    renameLate = false;
                    reshaped = true;
                    break;
            }
        }

        var filter = UnwindFilter.Of(bound, at);

        // Parents first: the page's order is the parents' order (every sort field before the key is
        // a member of the entity, and not the collection), the rows are paged by value, nothing
        // between the unwind and the page reshapes or joins the rows, and every parent kept yields a
        // row of the page: what filters after the unwind is asked before it as well, exactly, of
        // whatever the member holds (SomeRowPasses).
        parentsYield = !grouped && !reshaped && filter.Exact
            && (filter.Element.Count == 0 || unwind is { PreserveNull: false, Flatten: null });

        var cuttable = parentsYield && !sortsCollection
            && bound.PagingMode == PagingMode.Keyset && bound.Sort is not null;

        // The element's position after the parents' members and before the key (`number, position`):
        // within one parent it is the order the elements have anyway, but two parents that tie on the
        // members interleave by it. The parents are still cut first where every parent's rows stand at
        // the positions 0, 1, 2 … without a gap (no row kept without an element, no flattening, no
        // condition on the element), ordered by the first position each has a row left at.
        var leading = LeadingFields(sortFields);
        var rest = sortFields.Skip(leading.Count).ToList();
        var byPosition = rest.Count > 0 && unwind.IncludeIndex is { } indexed && rest[0].Path.Storage == indexed && !IsOfEntity(rest[0].Path)
            && (rest.Count == 1 || IsKey(rest[1]))
            && unwind is { PreserveNull: false, Flatten: null } && filter.Element.Count == 0;

        var parentsFirst = cuttable && (byPosition || rest.Count == 0 || IsKey(rest[0]));

        var prefiltered = unwind is { PreserveNull: false, Flatten: null, As: not null } ? filter.ElementPaths : [];

        return parentsFirst || renameLate || prefiltered.Count > 0
            ? new UnwindPlacement(at, parentsFirst, prefiltered, renameLate) { IndexAscending = parentsFirst && byPosition ? rest[0].Ascending : null }
            : null;
    }

    /// <summary>The sort fields that are members of the entity's own row, up to the first that is not or is the key.</summary>
    private static List<BoundSortField> LeadingFields(IReadOnlyList<BoundSortField> sortFields) =>
        sortFields.TakeWhile(field => !IsKey(field) && IsOfEntity(field.Path)).ToList();

    /// <summary>The field the parents of a page ordered by position are numbered in; no row keeps it.</summary>
    private const string ReservedFirst = Aliases.ReservedPrefix + "oxFirst";

    /// <summary>
    /// How many rows the database's <c>$unwind</c> answers for a row, as an expression over the row
    /// before it: the size of an array, its null elements counted (each is a row), none for a null or
    /// a member that is not stored, and one for any other value (the unwind of a value that is no
    /// array answers the value itself). It is what the engine computes before an unwind where it must
    /// know how many rows a parent yields (<see cref="ParentsByPosition"/>); whether it yields one at
    /// all is asked as a plain condition (<see cref="YieldsRow"/>).
    /// </summary>
    private static BsonDocument UnwoundRows(string storage)
    {
        var collection = "$" + storage;
        var absent = new BsonDocument("$in", new BsonArray { new BsonDocument("$type", collection), new BsonArray { "missing", "null", "undefined" } });

        return new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$isArray", collection),
            new BsonDocument("$size", collection),
            new BsonDocument("$cond", new BsonArray { absent, 0, 1 }),
        });
    }

    /// <summary>
    /// The order and the cut of the parents of a page sorted by the entity's members, then the
    /// position of the element, then the key (<see cref="UnwindPlacement.IndexAscending"/>).
    /// <para>
    /// The rows of the parents that tie on the members interleave: all their rows at one position
    /// before any at the next, by the key within a position. A parent's rows stand at the positions
    /// <c>lo … hi</c> without a gap: 0 … n − 1 for an array of n, and the one row of a value that is no
    /// array at the position the database gives it, none (null), which orders before 0 and is counted
    /// here as −1. Its rows still to come start at one position, its first: <c>lo</c> ascending and
    /// <c>hi</c> descending on a first page; after a cursor's position <c>(members, p, key)</c> a parent
    /// that ties with it on the members starts at <c>p</c> when its key comes after the position's, else
    /// one position further, and has no row left when that lies outside <c>lo … hi</c>. Ordered by the
    /// members, that first position and the key, a parent stands before every parent whose first row
    /// to come follows its own, so the first <c>n</c> rows of the page lie in the first <c>n</c>
    /// parents: each parent before a row's own has a row to come that precedes it.
    /// </para>
    /// <para>
    /// The first position is computed for every page, the first ascending one included: whether a
    /// parent beyond the cut holds a value that is no array, whose row precedes every row at position
    /// 0 of the parents it ties with, is known only by reading it. No index orders the cut.
    /// </para>
    /// </summary>
    private static List<BsonDocument> ParentsByPosition(BoundStage.Unwind unwind, IReadOnlyList<BoundSortField> sortFields, bool ascending, CursorPayload? position, long reach)
    {
        var leading = LeadingFields(sortFields);
        var keyAscending = sortFields.FirstOrDefault(IsKey)?.Ascending ?? true;
        var order = new BsonDocument();

        foreach (var field in leading)
            order[field.Path.Storage!] = field.Ascending ? 1 : -1;

        // The positions a parent's rows stand at: lo … hi, hi below lo where it has none.
        BsonValue lo = new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", "$" + unwind.Path.Storage), 0, NoPosition });
        BsonValue hi = new BsonDocument("$subtract", new BsonArray { new BsonDocument("$add", new BsonArray { lo, UnwoundRows(unwind.Path.Storage!) }), 1 });
        BsonValue first = ascending ? lo : hi;
        var stages = new List<BsonDocument>();

        if (position is not null)
        {
            BsonValue At(string wire) => position.Fields.FirstOrDefault(value => value.Wire == wire)?.Value ?? BsonNull.Value;

            // A value of the position is data, never a field path or an operator.
            static BsonDocument Literal(BsonValue value) => new("$literal", value);

            // The position of a row of a value that is no array is none: before every position of an array.
            var at = At(unwind.IncludeIndex!) is { IsNumeric: true } number ? number.ToInt64() : NoPosition;
            var ties = leading
                .Select(field => (BsonValue)new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$" + field.Path.Storage, BsonNull.Value }), Literal(At(field.Path.Wire) is { IsBsonNull: false, IsBsonUndefined: false } value ? value : BsonNull.Value) }))
                .ToList();
            BsonValue tied = ties.Count switch { 0 => true, 1 => ties[0], _ => new BsonDocument("$and", new BsonArray(ties)) };
            var later = new BsonDocument(keyAscending ? "$gt" : "$lt", new BsonArray { "$" + KeyStorage, Literal(At(KeyStorage)) });
            BsonValue next = new BsonDocument("$cond", new BsonArray { later, at, ascending ? at + 1 : at - 1 });

            first = new BsonDocument("$cond", new BsonArray { tied, new BsonDocument(ascending ? "$max" : "$min", new BsonArray { first, next }), first });
        }

        stages.Add(new BsonDocument("$set", new BsonDocument(ReservedFirst, first)));

        // A parent the position has passed has no row left: ascending its first lies beyond its last position, descending before its first.
        if (position is not null)
            stages.Add(new BsonDocument("$match", new BsonDocument("$expr", ascending
                ? new BsonDocument("$lte", new BsonArray { "$" + ReservedFirst, hi })
                : new BsonDocument("$gte", new BsonArray { "$" + ReservedFirst, lo }))));

        order[ReservedFirst] = ascending ? 1 : -1;
        order[KeyStorage] = keyAscending ? 1 : -1;

        stages.Add(new BsonDocument("$sort", order));
        stages.Add(new BsonDocument("$limit", reach));
        stages.Add(new BsonDocument("$unset", ReservedFirst));

        return stages;
    }

    /// <summary>The position of the one row of a value that is no array, as the cut of the parents counts it: the database writes none (null), which orders before 0.</summary>
    private const int NoPosition = -1;

    /// <summary>Whether a sort field is the entity's key, from which on the order lies within one parent.</summary>
    private static bool IsKey(BoundSortField field) => field.Path.Storage == KeyStorage && field.Path.Root.StoragePrefix.Length == 0;

    /// <summary>Whether a path is a member of the entity's own row: it holds the same value before and after an unwind.</summary>
    private static bool IsOfEntity(ResolvedPath path) => path.Root is ShapeNode.Entity { StoragePrefix.Length: 0 } && path.Storage is not null;

    /// <summary>
    /// The sort fields that order the parents: those up to the key, with the key where the request
    /// sorts on it (it has a direction there); what follows the key orders the rows of one parent.
    /// </summary>
    private static List<BoundSortField> ParentFields(IReadOnlyList<BoundSortField> sortFields)
    {
        var fields = sortFields.TakeWhile(field => !IsKey(field)).ToList();

        if (fields.Count < sortFields.Count)
            fields.Add(sortFields[fields.Count]);

        return fields;
    }

    /// <summary>
    /// What the matches between an unwind and the page (or the group) ask, split by what each
    /// top-level condition reads: only the element under the alias (<see cref="Element"/>), only the
    /// entity's own members (<see cref="Parent"/>), or anything else, which makes the split inexact.
    /// </summary>
    private sealed record UnwindFilter(IReadOnlyList<BoundCondition> Element, IReadOnlyList<BoundCondition> Parent, bool Exact)
    {
        /// <summary>The paths the element conditions read, in the order they are written.</summary>
        public IReadOnlyList<string> ElementPaths => Element.SelectMany(Paths).Select(path => path.Wire).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>
        /// Whether an element none of whose members exists passes the element conditions together: a
        /// null or a scalar in the array, or a scalar where the array should be, is such an element
        /// after the unwind, where every path under its alias is absent. True and false are certain;
        /// null where the conditions do not say it by themselves (a comparison with null, a set that
        /// holds null, a raw operand), and the database is then asked (<see cref="SomeRowPasses"/>).
        /// </summary>
        public bool? AbsentPasses => PassedByAbsent(new BoundCondition.And(Element));

        private static bool? PassedByAbsent(BoundCondition condition)
        {
            switch (condition)
            {
                case BoundCondition.And and:
                    var all = and.Conditions.Select(PassedByAbsent).ToList();

                    return all.Any(passed => passed == false) ? false : all.All(passed => passed == true) ? true : null;

                case BoundCondition.Or or:
                    var some = or.Conditions.Select(PassedByAbsent).ToList();

                    return some.Any(passed => passed == true) ? true : some.All(passed => passed == false) ? false : null;

                case BoundCondition.Not not:
                    return PassedByAbsent(not.Condition) is { } inner ? !inner : null;

                // No element of a collection that is not there passes anything.
                case BoundCondition.Any:
                    return false;

                case BoundCondition.Leaf { IsSemiJoin: true } or BoundCondition.Leaf { Operand: BoundOperand.Raw }:
                    return null;

                // A variant is told by its discriminator, or by being an object without one.
                case BoundCondition.Leaf { Op: "is" }:
                    return false;

                // Compiled as "is null or not stored", and its negation.
                case BoundCondition.Leaf { Operand: BoundOperand.Null } leaf:
                    return leaf.Op != "neq";

                case BoundCondition.Leaf leaf when Values(leaf.Operand).Any(value => value.IsBsonNull || value.IsBsonUndefined):
                    return null;

                case BoundCondition.Leaf { Op: "eq" or "gt" or "gte" or "lt" or "lte" or "in" or "contains" or "startsWith" or "endsWith" or "regex" }:
                    return false;

                case BoundCondition.Leaf { Op: "neq" or "nin" }:
                    return true;

                case BoundCondition.Leaf { Op: "exists", Operand: BoundOperand.Single { Value: BsonBoolean stored } }:
                    return !stored.Value;

                default:
                    return null;
            }
        }

        private static IEnumerable<BsonValue> Values(BoundOperand operand) => operand switch
        {
            BoundOperand.Single single => [single.Value],
            BoundOperand.Set set => set.Values,
            BoundOperand.Tolerant tolerant => tolerant.Alternatives,
            _ => [],
        };

        public static UnwindFilter Of(BoundPipeline bound, int at)
        {
            var unwind = (BoundStage.Unwind)bound.Stages[at];
            var element = new List<BoundCondition>();
            var parent = new List<BoundCondition>();
            var exact = true;

            foreach (var stage in bound.Stages.Skip(at + 1).TakeWhile(stage => stage is not BoundStage.Group))
            {
                if (stage is not BoundStage.Match match)
                    continue;

                foreach (var condition in Conjuncts(match.Condition))
                {
                    var paths = Paths(condition).ToList();

                    // So is a condition that may be asked as an expression, which no element test holds: it is asked after the unwind alone.
                    if (paths.Count == 0 || SemiJoins(condition) || ComparesTextDuration(condition))
                        exact = false;
                    else if (unwind.As is { } alias && paths.All(path => Under(path, alias) && path.Storage is not null))
                        element.Add(condition);
                    else if (paths.All(path => IsOfEntity(path) && !Covers(unwind.Path.Storage!, path.Storage!)))
                        parent.Add(condition);
                    else
                        exact = false;
                }
            }

            return new UnwindFilter(element, parent, exact);
        }

        private static IEnumerable<BoundCondition> Conjuncts(BoundCondition condition) =>
            condition is BoundCondition.And and ? and.Conditions.SelectMany(Conjuncts) : [condition];

        private static bool ComparesTextDuration(BoundCondition condition) => condition switch
        {
            BoundCondition.And and => and.Conditions.Any(ComparesTextDuration),
            BoundCondition.Or or => or.Conditions.Any(ComparesTextDuration),
            BoundCondition.Not not => ComparesTextDuration(not.Condition),
            BoundCondition.Any any => ComparesTextDuration(any.Inner),
            BoundCondition.Leaf leaf => leaf is { Path.LeafKind: Kind.TimeSpan, Op: "gt" or "gte" or "lt" or "lte", Operand: BoundOperand.Single { Value: BsonString } },
            _ => false,
        };

        private static bool SemiJoins(BoundCondition condition) => condition switch
        {
            BoundCondition.And and => and.Conditions.Any(SemiJoins),
            BoundCondition.Or or => or.Conditions.Any(SemiJoins),
            BoundCondition.Not not => SemiJoins(not.Condition),
            BoundCondition.Any any => SemiJoins(any.Inner),
            BoundCondition.Leaf leaf => leaf.IsSemiJoin,
            _ => true,
        };
    }

    /// <summary>
    /// The filter asked of a row before its collection is unwound; null when there is nothing to ask.
    /// The conditions on the element, written after the unwind, are asked of the collection
    /// (<see cref="SomeRowPasses"/>): every row that yields a row after the unwind and its filters is
    /// kept, whatever the member holds.
    /// <para>
    /// With <paramref name="parents"/> (the parents are cut before the unwind) every row kept has to
    /// yield one, so exactly those are kept: the conditions on the entity's own members are asked
    /// here as well, and a row whose collection yields none is dropped where the unwind would drop it
    /// (<see cref="YieldsRow"/>). Without it (the count, and a page whose order is not the parents')
    /// the filter only spares the unwind: a row that yields none may pass where telling it apart
    /// costs more than its unwind, which drops it.
    /// </para>
    /// </summary>
    private static BsonDocument? UnwindPrefilter(BoundPipeline bound, int at, bool collated, bool parents)
    {
        var unwind = (BoundStage.Unwind)bound.Stages[at];
        var filter = UnwindFilter.Of(bound, at);
        var conditions = new BsonArray();
        var storage = unwind.Path.Storage!;

        if (parents)
            foreach (var condition in filter.Parent)
                conditions.Add(Filter(condition, null, collated));

        if (filter.Element.Count > 0 && unwind is { PreserveNull: false, Flatten: null, As: not null })
            conditions.Add(SomeRowPasses(storage, unwind.As!, filter, collated, exactly: parents));
        else if (parents && !unwind.PreserveNull)
            conditions.Add(YieldsRow(storage));

        return conditions.Count switch
        {
            0 => null,
            1 => conditions[0].AsBsonDocument,
            _ => new BsonDocument("$and", conditions),
        };
    }

    /// <summary>Every BSON type but array, null and undefined: what a value that is no array and yields a row is of.</summary>
    private static BsonArray ValueTypes => [1, 2, 3, 5, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 19, -1, 127];

    /// <summary>Every BSON type but object and array: what an element none of whose members exists is of, a null included.</summary>
    private static BsonArray MemberlessTypes => [1, 2, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, -1, 127];

    /// <summary>The member a filter is read at to ask what an element without members passes; no row holds it.</summary>
    private const string ReservedAbsent = Aliases.ReservedPrefix + "oxAbsent";

    /// <summary>The BSON type of an undefined, which yields no row; a comparison with null no longer finds it in every version of the database.</summary>
    private const int UndefinedType = 6;

    /// <summary>The member holds an array that holds anything: of every element, a null included, "it is there" holds.</summary>
    private static BsonDocument HoldsAnElement(string storage) => new(storage, new BsonDocument("$elemMatch", new BsonDocument("$exists", true)));

    /// <summary>
    /// Whether the database's <c>$unwind</c> answers a row for what the member holds, as a plain
    /// condition (no expression): an array that holds anything, a null element included, or a value
    /// that is no array, null or undefined. An empty array, a null and a member that is not stored
    /// yield none.
    /// <para>
    /// "An array that holds anything, or a value", written as "neither is it no such array nor no
    /// value". The database asks the conditions of a filter in an order of its own, by their kind, and
    /// every condition on the collection walks the stored row to the member again, so the order is
    /// what the filter costs: written this way the organisation is asked first, then "an array that
    /// holds anything", which alone answers for a row with elements; only a row without (nothing to
    /// unwind, or a value that is no array) is asked the second.
    /// </para>
    /// </summary>
    private static BsonDocument YieldsRow(string storage) => new("$nor", new BsonArray
    {
        new BsonDocument("$and", new BsonArray
        {
            new BsonDocument(storage, new BsonDocument("$not", HoldsAnElement(storage)[storage])),
            new BsonDocument(storage, new BsonDocument("$not", new BsonDocument("$type", ValueTypes))),
        }),
    });

    /// <summary>
    /// Whether some row the unwind answers for the member passes the conditions on the element,
    /// asked before the unwind: exactly the rows that yield a row after the unwind and its filters.
    /// The rows of an unwind are of three kinds, and each is asked in its own way:
    /// <list type="bullet">
    /// <item>an element that is an object: <c>$elemMatch</c> of the conditions, read relative to the element;</item>
    /// <item>an element that is no object (a null or a scalar in the array): every path under the
    /// alias is absent after the unwind, so it passes or fails the conditions whatever it is. Where it
    /// fails them (<see cref="UnwindFilter.AbsentPasses"/> is false) nothing is asked; where it passes,
    /// "the array holds such an element"; where the conditions do not say, that and the conditions
    /// read at a member no row holds, so the database answers what an absent member passes;</item>
    /// <item>a value that is no array (one object or a scalar where the array should be): the one
    /// row its unwind answers is the value, so the conditions as written, read at the member itself,
    /// of a member that is no array, and no null, undefined or missing member either, which yield
    /// no row.</item>
    /// </list>
    /// The three are the branches of one <c>$or</c>, of kinds the database asks in this order (the
    /// last is written as "none of what rules it out"): a row an object of which passes is answered by
    /// the first alone, and a row of the usual form that fails it is asked one condition per further
    /// branch. No single plain condition tells an array of objects from both an array that also holds
    /// a null and one object in the array's place, which is why two more are asked.
    /// <para>
    /// One comparison of one member that no absent member passes needs none of this: read through
    /// the collection (<c>"lines.status": "x"</c>) it holds exactly when an object in the array, or
    /// the one object in its place, passes it, and an index on that path bounds it.
    /// </para>
    /// <para>
    /// Without <paramref name="exactly"/> the filter may keep a row that yields none, and asks less
    /// of the rows that fail: an object in the array passes; or the member is no array that holds
    /// anything (nothing to unwind, or the one value, which the conditions after the unwind judge);
    /// or, where an absent member may pass, the member or an element of it is of a kind without
    /// members. What it drops is an array of objects none of which passes, which is all a filter
    /// before the unwind is for.
    /// </para>
    /// </summary>
    private static BsonDocument SomeRowPasses(string storage, string alias, UnwindFilter filter, bool collated, bool exactly)
    {
        BsonArray At(string under) => new(filter.Element.Select(condition => (BsonValue)Renamed(Filter(condition, null, collated), (alias, under))));

        var ofValue = At(storage);
        var absentPasses = filter.AbsentPasses;

        if (absentPasses == false && ofValue.Count == 1 && IsOneComparison(ofValue[0].AsBsonDocument))
            return ofValue[0].AsBsonDocument;

        var ofElement = At("");
        var branches = new BsonArray { new BsonDocument(storage, new BsonDocument("$elemMatch", ofElement.Count == 1 ? ofElement[0] : new BsonDocument("$and", ofElement))) };

        if (!exactly)
        {
            branches.Add(new BsonDocument(storage, new BsonDocument("$not", HoldsAnElement(storage)[storage])));

            if (absentPasses != false)
                branches.Add(new BsonDocument(storage, new BsonDocument("$type", MemberlessTypes)));

            return new BsonDocument("$or", branches);
        }

        var memberless = new BsonDocument(storage, new BsonDocument("$elemMatch", new BsonDocument("$type", MemberlessTypes)));

        // "The array holds an element without members, and such an element passes each condition", as
        // "none of these fails": the first is asked first, and fails for every array of objects.
        if (absentPasses == true)
            branches.Add(memberless);
        else if (absentPasses is null)
            branches.Add(new BsonDocument("$nor", new BsonArray { Fails(memberless) }.AddRange(At(ReservedAbsent).Select(condition => (BsonValue)Fails(condition.AsBsonDocument)))));

        // The value that is no array: none of what rules it out holds. An array is ruled out first,
        // an empty one included (compared with an array only an array is at least the empty one), which
        // answers for every row of the usual form; then a null and a member not stored, which yield no
        // row (the conditions do not say so themselves); then each condition that fails, one by one (a
        // conjunction in here the database would lift out and ask before everything else); then an
        // undefined, which a comparison with null no longer finds.
        var ruledOut = new BsonArray
        {
            new BsonDocument(storage, new BsonDocument("$gte", new BsonArray())),
            new BsonDocument(storage, new BsonDocument("$in", new BsonArray { BsonNull.Value })),
        };

        ruledOut.AddRange(ofValue.Select(condition => (BsonValue)Fails(condition.AsBsonDocument)));
        ruledOut.Add(new BsonDocument(storage, new BsonDocument("$type", UndefinedType)));
        branches.Add(new BsonDocument("$nor", ruledOut));

        return new BsonDocument("$or", branches);
    }

    /// <summary>A condition does not hold.</summary>
    private static BsonDocument Fails(BsonDocument condition) => new("$nor", new BsonArray { condition });

    /// <summary>
    /// Whether a compiled condition is one comparison of one member: one path with one value, one
    /// pattern or one operator. Two operators on one path (a range) are two conditions, which two
    /// different elements of an array could meet.
    /// </summary>
    private static bool IsOneComparison(BsonDocument condition)
    {
        if (condition.ElementCount != 1 || condition.GetElement(0).Name.StartsWith('$'))
            return false;

        if (condition.GetElement(0).Value is not BsonDocument asked || asked.ElementCount == 0 || !asked.Names.Any(name => name.StartsWith('$')))
            return true;

        return asked.ElementCount == 1 && asked.GetElement(0).Name is "$eq" or "$gt" or "$gte" or "$lt" or "$lte" or "$in" or "$exists";
    }

    /// <summary>
    /// A filter or sort document with every field path under <c>renamed.Alias</c> read under
    /// <c>renamed.Storage</c> instead (the element where the unwind left it; an empty storage reads it
    /// relative to the element, inside <c>$elemMatch</c>). The values are the document's own, so a
    /// semi-join slot in it is still the slot the executor fills; an expression (the exact comparison
    /// of a semi-join's keys) reads its fields under the new name too. Null leaves the document as it is.
    /// </summary>
    private static BsonDocument Renamed(BsonDocument document, (string Alias, string Storage)? renamed)
    {
        if (renamed is not { } rename)
            return document;

        var result = new BsonDocument();

        foreach (var element in document)
        {
            if (element.Name is "$and" or "$or" or "$nor" && element.Value is BsonArray conditions)
                result.Add(element.Name, new BsonArray(conditions.Select(condition => condition is BsonDocument inner ? Renamed(inner, renamed) : condition)));
            else if (element.Name == "$expr" && rename.Storage.Length > 0)
                result.Add(element.Name, RenamedIn(element.Value, rename));
            else if (element.Name.StartsWith(rename.Alias + ".", StringComparison.Ordinal))
                result.Add(rename.Storage.Length == 0 ? element.Name[(rename.Alias.Length + 1)..] : rename.Storage + element.Name[rename.Alias.Length..], element.Value);
            else if (element.Name == rename.Alias && rename.Storage.Length > 0)
                result.Add(rename.Storage, element.Value);
            else
                result.Add(element.Name, element.Value);
        }

        return result;
    }

    /// <summary>An expression with every field under <c>rename.Alias</c> read under <c>rename.Storage</c>; a literal is kept as the instance it is.</summary>
    private static BsonValue RenamedIn(BsonValue expression, (string Alias, string Storage) rename) => expression switch
    {
        BsonString { Value: var field } when field == "$" + rename.Alias => new BsonString("$" + rename.Storage),
        BsonString { Value: var field } when field.StartsWith("$" + rename.Alias + ".", StringComparison.Ordinal) => new BsonString("$" + rename.Storage + field[(rename.Alias.Length + 1)..]),
        BsonArray items => new BsonArray(items.Select(item => RenamedIn(item, rename))),
        BsonDocument document => new BsonDocument(document.Select(element => new BsonElement(element.Name, element.Name == "$literal" ? element.Value : RenamedIn(element.Value, rename)))),
        _ => expression,
    };

    /// <summary>
    /// The fields of a sort that order: a path the sort names more than once orders once, at its
    /// first place and in the direction written last, as the <c>$sort</c> document of
    /// <see cref="Sort"/> holds it. Everything that reads a direction off the sort reads it here, so
    /// the order of a page and the rows after a cursor's position are one order.
    /// </summary>
    internal static IReadOnlyList<BoundSortField> Ordering(IReadOnlyList<BoundSortField> fields)
    {
        if (fields.Select(field => field.Path.Storage).Distinct(StringComparer.Ordinal).Count() == fields.Count)
            return fields;

        var ordering = new List<BoundSortField>();

        foreach (var field in fields)
        {
            var at = ordering.FindIndex(earlier => earlier.Path.Storage == field.Path.Storage);

            if (at < 0)
                ordering.Add(field);
            else
                ordering[at] = ordering[at] with { Ascending = field.Ascending };
        }

        return ordering;
    }

    /// <summary>
    /// The legs of a page's total order with the values of the cursor's position: the sort fields,
    /// then the key unless a sort field is it or the rows have none (<paramref name="key"/>: groups),
    /// then the position of each unwind not sorted on already. It mirrors the emitted <c>$sort</c>:
    /// the key is one leg, in the direction the sort gives it and ascending where the sort does not
    /// name it. A cursor carries the key's value twice where the sort names it (under the sort's
    /// path and, last, as the key); both are the same value of the same row, and one leg reads it.
    /// </summary>
    /// <remarks>
    /// <paramref name="repeatKey"/>: a contract 1 page sends the predicate it always sent for a sort
    /// that names the key ascending, the key once more as the last leg. That leg admits only rows
    /// the legs before it admit, so the rows are the same.
    /// </remarks>
    private static List<KeysetField> Legs(CursorPayload cursor, IReadOnlyList<BoundSortField> sortFields, bool key, IReadOnlyList<string> indexes, (string Alias, string Storage)? renamed = null, bool repeatKey = false)
    {
        BsonValue At(string wire) => cursor.Fields.FirstOrDefault(value => value.Wire == wire)?.Value ?? BsonNull.Value;

        string Stored(string storage) => renamed is not { } rename ? storage
            : storage == rename.Alias ? rename.Storage
            : storage.StartsWith(rename.Alias + ".", StringComparison.Ordinal) ? rename.Storage + storage[rename.Alias.Length..]
            : storage;

        var legs = new List<KeysetField>();

        foreach (var field in sortFields)
            if (!legs.Any(leg => leg.Storage == Stored(field.Path.Storage!)))
                legs.Add(new KeysetField(Stored(field.Path.Storage!), field.Ascending, At(field.Path.Wire)) { SeveralTypes = HoldsSeveralTypes(field.Path) });

        if (key && (!legs.Any(leg => leg.Storage == KeyStorage) || (repeatKey && legs.Any(leg => leg is { Storage: KeyStorage, Ascending: true }))))
            legs.Add(new KeysetField(KeyStorage, true, At(KeyStorage)));

        foreach (var index in indexes)
            if (!legs.Any(leg => leg.Storage == index))
                legs.Add(new KeysetField(index, true, At(index)));

        return legs;
    }

    /// <summary>
    /// Whether the rows may hold values of more than one stored type under a sort path: a decimal,
    /// which the rows written before a migration hold as text, and a value of an addon bag, which is
    /// stored as it was written. A sort orders such rows by type first, and a page continues across
    /// the types.
    /// </summary>
    private static bool HoldsSeveralTypes(ResolvedPath path) => path.LeafKind == Kind.Decimal || path.Kind == Kind.Unknown || path.Addon is not null;

    /// <summary>
    /// The conditions that keep the rows after a position (with <paramref name="inclusive"/>: from it
    /// on): the null-aware predicate over every leg and, with <paramref name="ranged"/>, the one range
    /// on the leading leg it implies, first.
    /// </summary>
    private static List<BsonDocument> KeysetConjuncts(IReadOnlyList<KeysetField> legs, bool ranged, bool inclusive = false)
    {
        var conjuncts = new List<BsonDocument>();

        // A contract 2 page follows the sort across stored types; a contract 1 page sends the predicate it always sent.
        if (ranged && KeysetPredicate.LeadingRange(legs, everyType: true) is { } range)
            conjuncts.Add(range);

        conjuncts.Add(KeysetPredicate.After(legs, inclusive, everyType: ranged));

        return conjuncts;
    }

    /// <summary>
    /// The largest page a projection is moved behind: the server then orders whole documents and keeps
    /// the page's worth of them in memory, which for a report page of thousands of large documents
    /// could reach its sort memory limit where the projected rows would not.
    /// </summary>
    public const int MaxLateProjectionPage = 1_000;

    /// <summary>
    /// Whether the projection at <paramref name="index"/> can run after the page is taken, whatever
    /// place the caller wrote it in. It can when every stage after it only filters, orders or cuts
    /// the rows, or joins after the page: a filter or a sort written after a projection reads only what
    /// the projection kept, which holds the same values before it, and what the page and the engine
    /// read off the rows afterwards (the key, the paging sort's paths, a keyed join's reference, a late
    /// join's key) the projection keeps anyway. The page then holds the same rows in the same order,
    /// and the projection cuts the page's rows instead of every candidate row. A later unwind, group
    /// or join before the page reshapes the row, so a projection before one stays where it is; so does
    /// every projection of an internal owner query, and of a page above
    /// <see cref="MaxLateProjectionPage"/>. Explain reads it (<c>PROJECT_AFTER_PAGE</c>).
    /// </summary>
    public static bool ProjectsAfterPage(BoundPipeline bound, int index)
    {
        ArgumentNullException.ThrowIfNull(bound);

        if (bound.KeyedBy is not null || bound.Page.Limit > MaxLateProjectionPage || bound.Stages[index] is not BoundStage.Project)
            return false;

        for (var later = index + 1; later < bound.Stages.Count; later++)
        {
            switch (bound.Stages[later])
            {
                case BoundStage.Match or BoundStage.Sort or BoundStage.Page or BoundStage.Project or ContinuedStage:
                    break;

                case BoundStage.Resolve keyed when IsKeyed(keyed):
                    break;

                // A join that is not run, or runs after the page, does not stand between the projection and the page.
                case BoundStage.Lookup lookup when !JoinRuns(bound, later, lookup.As) || JoinsAfterPage(bound, later, lookup.As):
                    break;

                case BoundStage.Resolve resolve when !JoinRuns(bound, later, resolve.As) || JoinsAfterPage(bound, later, resolve):
                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>Whether a match after the stage at <paramref name="index"/> reads the alias: it may filter out a row the stage truncated.</summary>
    /// <remarks>An unwind of the alias under another name (<c>as</c>) carries the cut rows on under that name too.</remarks>
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

    /// <summary>
    /// The stages so far, the stage's own, then one row carrying the truncation flag. The stages are
    /// the page's own documents, not copies: a semi-join slot in them is the one the executor fills,
    /// so the probe reads the rows the page reads.
    /// </summary>
    private static List<BsonDocument> Probing(IEnumerable<BsonDocument> before, IEnumerable<BsonDocument> emitted, string flag) =>
    [
        .. before,
        .. emitted,
        new("$match", new BsonDocument(flag, true)),
        new("$limit", 1),
        new("$project", new BsonDocument(KeyStorage, 1)),
    ];

    /// <summary>
    /// Registers an inline resolve's outcome probe when the row shows its alias and something reads
    /// the outcomes: an effective <c>onMissing</c> other than <c>null</c> (missing rows, and with a
    /// filter the existence flag that tells an excluded record from a missing one), or a strict
    /// request (a non-key target's ambiguity), or the outcome under a name (<c>outcomeAs</c>), which
    /// says <c>ambiguous</c> and so has the ambiguity seen and reported. A target onto a member that is
    /// not its key is joined with two records, so a key two records hold is <c>ambiguous</c> whatever
    /// the page and cache hold. Null when there is nothing to probe.
    /// </summary>
    private static InlineProbe? Probe(BoundPipeline bound, int index, BoundStage.Resolve resolve, List<InlineProbe> probes, List<string> keys)
    {
        var reports = resolve.EffectiveOnMissing != ResolveOnMissing.Null;

        if (!(reports || bound.Strict || resolve.OutcomeAs is not null) || resolve.Reference.Storage is not { } storage || !Shown(bound.FinalShape, resolve.As))
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
    /// The outcomes of the page's inline resolves: per probe, every row whose
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

                var key = WireEncoder.KeyText(value, reference.LeafKind, reference.Leaf);

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

    // ---- filters ----------------------------------------------------------------------------

    /// <summary>Compiles a condition tree to a filter document; <paramref name="collated"/> when the aggregate carries the collation.</summary>
    public static BsonDocument Filter(BoundCondition condition, List<SemiJoinSlot>? semiJoins = null, bool collated = false) => Filter(condition, semiJoins, collated, element: false);

    /// <summary><see cref="Filter(BoundCondition, List{SemiJoinSlot}?, bool)"/>; <paramref name="element"/> inside an <c>$elemMatch</c>, which takes no expression.</summary>
    private static BsonDocument Filter(BoundCondition condition, List<SemiJoinSlot>? semiJoins, bool collated, bool element) => condition switch
    {
        BoundCondition.And and => new BsonDocument("$and", new BsonArray(and.Conditions.Select(inner => Filter(inner, semiJoins, collated, element)))),
        BoundCondition.Or or => new BsonDocument("$or", new BsonArray(or.Conditions.Select(inner => Filter(inner, semiJoins, collated, element)))),
        BoundCondition.Not not => new BsonDocument("$nor", new BsonArray { Filter(not.Condition, semiJoins, collated, element) }),
        BoundCondition.Any any => new BsonDocument(any.Path.Storage!, new BsonDocument("$elemMatch", Filter(any.Inner, semiJoins, collated, element: true))),
        BoundCondition.Leaf leaf => Leaf(leaf, semiJoins, collated, element),
        _ => new BsonDocument(),
    };

    private static BsonDocument Leaf(BoundCondition.Leaf leaf, List<SemiJoinSlot>? semiJoins, bool collated, bool element)
    {
        if (leaf.IsSemiJoin)
        {
            var ids = new BsonArray();
            var reference = ((ShapeNode.Remote)leaf.Path.Root).Reference;

            semiJoins?.Add(new SemiJoinSlot(leaf, ids));

            var among = new BsonDocument(reference.Storage!, new BsonDocument("$in", ids));

            // The collation folds the $in like any comparison, and the owner's keys are exact: a
            // reference stored as text is compared again byte for byte, over the same slot.
            return collated && !element && IsStringStored(reference)
                ? new BsonDocument("$and", new BsonArray { among, ExactKeysMatch(reference.Storage!, ids)["$match"] })
                : among;
        }

        if (leaf.Op == "is")
            return Is(leaf);

        var storage = leaf.Path.Storage!;

        // A decimal under the addon bag is written wrapped by the bag serializer: match both places.
        if (leaf.Path.Addon is not null && leaf.Path.Kind == Kind.Decimal)
            return new BsonDocument("$or", new BsonArray { LeafAt(storage, leaf, collated, element), LeafAt(storage + "._v", leaf, collated, element) });

        return LeafAt(storage, leaf, collated, element);
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

    private static BsonDocument LeafAt(string storage, BoundCondition.Leaf leaf, bool collated, bool element)
    {
        var op = leaf.Op;
        var casing = CasingOf(leaf, collated);

        // A path through a collection holds one value per element, and a condition on it is one element's to meet.
        var many = leaf.Path.CollectionAncestors > 0 || leaf.Path.Kind == Kind.Array;

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
                    return new BsonDocument("$nor", new BsonArray(tolerant.Alternatives.Select(value => Compare(storage, "eq", value, casing, many))));

                return new BsonDocument("$or", new BsonArray(tolerant.Alternatives.Select(value => Compare(storage, op, value, casing, many))));

            // A duration stored as text orders by character, and by the same characters whatever else the
            // request asks: under a collation, which would order them by its own weights, the comparison is
            // made on the bytes. (Of one element, inside 'any', and through a collection no expression can
            // be asked: there the plain operator stands.)
            case BoundOperand.Single { Value: BsonString duration } when collated && !many && !element && leaf.Path.LeafKind == Kind.TimeSpan && op is "gt" or "gte" or "lt" or "lte":
                return ByBytes(storage, op, duration.Value);

            case BoundOperand.Single single:
                return Compare(storage, op, single.Value, casing, many);

            default:
                return new BsonDocument(storage, BsonNull.Value);
        }
    }

    /// <summary>
    /// An ordered comparison of a stored text with a text by their bytes, inside an aggregate whose
    /// collation a plain operator would compare under: the texts are compared by a function the
    /// collation does not reach. Both are text without letters of two cases (a duration as the driver
    /// writes it), so that the function sets case aside changes nothing. A value that is no text
    /// meets no ordered comparison with one, as under the plain operator.
    /// </summary>
    private static BsonDocument ByBytes(string storage, string op, string text) =>
        new("$expr", new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$" + storage), "string" }),
            new BsonDocument("$" + op, new BsonArray { new BsonDocument("$strcasecmp", new BsonArray { TextOrEmpty("$" + storage), new BsonDocument("$literal", text) }), 0 }),
        }));

    /// <summary>The whole-value pattern of a text: escaped and anchored at both ends, folding case only as a fold.</summary>
    private static BsonRegularExpression Anchored(string text, Casing casing) =>
        new("^" + RegexGuard.Escape(text) + End, casing == Casing.Fold ? "i" : "");

    /// <summary>
    /// One comparison of one member. Each form asks the member one thing, so through a collection
    /// (<paramref name="many"/>) it holds when one element meets it; the one form of two bounds, a
    /// prefix under the collation, is a pattern there, since two bounds on a collection's path could
    /// be met by two elements.
    /// </summary>
    private static BsonDocument Compare(string storage, string op, BsonValue value, Casing casing, bool many)
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
            // Under the collation a pattern folds accents itself: every letter is the class of its
            // equivalents, so contains and endsWith find what eq and startsWith find. It is still a
            // pattern: no index bounds it, and it reads every candidate row.
            "contains" when casing == Casing.Collated => new BsonDocument(storage, new BsonRegularExpression(AccentFold.Pattern(Text(value), folding ?? new CollationOptions()), flags)),
            "endsWith" when casing == Casing.Collated => new BsonDocument(storage, new BsonRegularExpression(AccentFold.Pattern(Text(value), folding ?? new CollationOptions()) + End, flags)),
            "contains" => new BsonDocument(storage, new BsonRegularExpression(RegexGuard.Escape(Text(value)), flags)),
            // Under the collation a prefix is the range from the prefix up to the prefix
            // followed by the highest character: every string that starts with it under the
            // collation, accents and case folded, and nothing else, and an index on the member
            // bounds it, which no pattern does.
            // Through a collection the two bounds would each be met by some element, not by one: there
            // the prefix is the anchored pattern that folds as the collation does.
            "startsWith" when casing == Casing.Collated && many => new BsonDocument(storage, new BsonRegularExpression("^" + AccentFold.Pattern(Text(value), folding ?? new CollationOptions()), flags)),
            "startsWith" when casing == Casing.Collated => new BsonDocument(storage, new BsonDocument { ["$gte"] = Text(value), ["$lt"] = Text(value) + AfterEveryCharacter }),
            "startsWith" => new BsonDocument(storage, new BsonRegularExpression("^" + RegexGuard.Escape(Text(value)), flags)),
            "endsWith" => new BsonDocument(storage, new BsonRegularExpression(RegexGuard.Escape(Text(value)) + End, flags)),
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

        // A parent that holds no key has no children, also where children hold nothing in their
        // reference. The entity's own key is always there; a parent under an alias, or
        // one a reference names by another member than its key, may hold none.
        var keyMayBeAbsent = onAlias || lookup.ParentKeyStorage != KeyStorage;

        if (keyMayBeAbsent)
            pipeline.Add(new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$$" + ParentVariable, BsonNull.Value }))));

        if (exactKey)
            pipeline.Add(ExactKeyMatch(lookup.ChildKeyStorage));

        if (lookup.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(lookup.Filter, semiJoins, collated)));

        pipeline.Add(Sort(lookup.ChildSort ?? [], tieBreak: true));
        pipeline.Add(new BsonDocument("$limit", lookup.First ? 1 : lookup.Limit + 1));
        pipeline.Add(new BsonDocument("$project", Select(lookup.Select, keepDiscriminators, lookup.From)));

        var join = Join(lookup.From.Collection, lookup.ParentKeyStorage, lookup.ChildKeyStorage, exactKey, pipeline, lookup.As);

        if (keyMayBeAbsent)
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
    /// The start of an internal owner query grouped per key (<c>keyedBy</c>):
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

        // Whole rows (a remote lookup of an entity): each row carries the key it
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
        // under one key.
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
        // (a remote lookup's sort), completed by the record key; by record key otherwise.
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
                // case into one; its hash compares the bytes, so each key is ranked on its own,
                // and the key beside the hash keeps two keys apart whose hashes collide.
                ["partitionBy"] = collated && keyedBy.Keys.Count > 0 && keyedBy.Keys.All(key => key.IsString)
                    ? new BsonDocument { ["h"] = new BsonDocument("$toHashedIndexKey", "$" + keyedBy.PartitionStorage), ["k"] = "$" + keyedBy.PartitionStorage }
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
    /// What a keyed resolve reads off the page rows, which a projection keeps in storage: the
    /// reference and the stored values that select its cases; under <c>elements</c> those of each
    /// element of the collection it crosses, and with <paramref name="whole"/> that collection whole.
    /// </summary>
    private static IEnumerable<string> KeptStorages(BoundStage.Resolve resolve, bool whole)
    {
        if (resolve.CollectionStorage is { } collection)
        {
            // As written (contract 1) the collection is kept whole. Otherwise only what the keyed
            // fetch reads of each element: the reference, and the stored values that select its
            // cases, which are relative to the element. The elements keep their places either way.
            if (whole)
            {
                yield return collection;
                yield break;
            }

            yield return resolve.Reference.Storage ?? collection;

            foreach (var bound in resolve.Cases ?? [])
                if (bound.When is { } condition)
                    yield return collection + "." + condition.Storage;

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
    /// without the filter says whether the target has the record at all.
    /// </summary>
    private static IEnumerable<BsonDocument> LocalResolve(BoundStage.Resolve resolve, List<SemiJoinSlot> semiJoins, bool collated, InlineProbe? probe = null, bool keepDiscriminators = false)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", ScopeFilter(resolve.TargetScope!)) };
        var exactKey = collated && IsStringStored(resolve.Reference);

        // A reference that holds nothing names no record, also where a record holds nothing in the
        // member it is named by. A record always holds its key, so only a reference that names
        // its target by another member is asked.
        var held = resolve.TargetFieldStorage != KeyStorage ? HoldsKey() : null;

        if (held is not null)
            pipeline.Add(held);

        if (exactKey)
            pipeline.Add(ExactKeyMatch(resolve.TargetFieldStorage!));

        if (resolve.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(resolve.Filter, semiJoins, collated)));

        // The outcome under a name is written by the aggregate: it needs the second record where the
        // target field is not the key, and with a filter whether the unfiltered target has the record.
        var outcomeAs = resolve.OutcomeAs;
        var two = probe?.AmbiguityFlag is not null || (outcomeAs is not null && !TargetIsKey(resolve));
        var probed = probe?.ExistsFlag is not null || (outcomeAs is not null && resolve.Filter is not null);

        // Two records of one key: the first by record key is the one taken, as the keyed fetch takes it.
        if (two)
            pipeline.Add(new BsonDocument("$sort", new BsonDocument(KeyStorage, 1)));

        pipeline.Add(new BsonDocument("$limit", two ? 2 : 1));
        pipeline.Add(new BsonDocument("$project", Select(resolve.Select!, keepDiscriminators, resolve.Target)));

        // No caller alias ends in the suffix, so the temporary field shadows nothing.
        var temporary = resolve.As + Aliases.ReservedSuffix;
        var set = new BsonDocument(resolve.As, new BsonDocument("$arrayElemAt", new BsonArray { "$" + temporary, 0 }));

        yield return new BsonDocument("$lookup", Join(resolve.Target!.Collection, resolve.Reference.Storage!, resolve.TargetFieldStorage!, exactKey || held is not null, pipeline, temporary));

        if (probe?.AmbiguityFlag is { } ambiguity)
            set[ambiguity] = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + temporary), 1 });

        // The existence join: the same key match without the filter, one key read.
        var existing = resolve.As + "Has" + Aliases.ReservedSuffix;

        if (outcomeAs is not null)
            set[outcomeAs] = OutcomeExpression(resolve.Reference.Storage!, temporary, two, probed ? existing : null);

        if (!probed)
        {
            yield return new BsonDocument("$set", set);
            yield return new BsonDocument("$unset", temporary);
            yield break;
        }

        var probeline = new BsonArray { new BsonDocument("$match", ScopeFilter(resolve.TargetScope!)) };

        if (held is not null)
            probeline.Add(held);

        if (exactKey)
            probeline.Add(ExactKeyMatch(resolve.TargetFieldStorage!));

        probeline.Add(new BsonDocument("$limit", 1));
        probeline.Add(new BsonDocument("$project", new BsonDocument(KeyStorage, 1)));

        yield return new BsonDocument("$lookup", Join(resolve.Target!.Collection, resolve.Reference.Storage!, resolve.TargetFieldStorage!, exactKey || held is not null, probeline, existing));

        if (probe?.ExistsFlag is { } exists)
            set[exists] = new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + existing), 0 });

        yield return new BsonDocument("$set", set);
        yield return new BsonDocument("$unset", new BsonArray { temporary, existing });
    }

    /// <summary>
    /// The outcome of an inline resolve as the aggregate writes it on every row,
    /// in the keyed fetch's words: <c>reference_null</c> where the reference holds nothing; with a record
    /// joined, <c>ambiguous</c> where a second holds the key (<paramref name="two"/>) and else
    /// <c>resolved</c>; without one, <c>excluded</c> where the unfiltered target has the record
    /// (<paramref name="existing"/>) and else <c>not_found</c>.
    /// </summary>
    private static BsonDocument OutcomeExpression(string reference, string joined, bool two, string? existing)
    {
        var branches = new BsonArray
        {
            Branch(new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$" + reference, BsonNull.Value }), BsonNull.Value }), "reference_null"),
        };

        if (two)
            branches.Add(Branch(new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + joined), 1 }), "ambiguous"));

        branches.Add(Branch(new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + joined), 0 }), "resolved"));

        if (existing is not null)
            branches.Add(Branch(new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + existing), 0 }), "excluded"));

        return new BsonDocument("$switch", new BsonDocument { ["branches"] = branches, ["default"] = "not_found" });

        static BsonDocument Branch(BsonDocument when, string outcome) => new() { ["case"] = when, ["then"] = outcome };
    }

    /// <summary>The match of a join's sub-pipeline that lets nothing through for a row whose local key (<see cref="KeyVariable"/>) is null or not stored.</summary>
    private static BsonDocument HoldsKey() =>
        new("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$$" + KeyVariable, BsonNull.Value })));

    /// <summary>
    /// The <c>$lookup</c> document of a join. With <paramref name="bindKey"/> the local key is
    /// bound to a variable the sub-pipeline reads (its byte comparison, or whether the row holds
    /// a key at all); otherwise the document is the indexed join alone.
    /// </summary>
    private static BsonDocument Join(string from, string localField, string foreignField, bool bindKey, BsonArray pipeline, string alias)
    {
        var join = new BsonDocument
        {
            ["from"] = from,
            ["localField"] = localField,
            ["foreignField"] = foreignField,
        };

        if (bindKey)
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
    /// <para>
    /// The byte functions take text and nothing else, and the database evaluates them where their
    /// operand is known before a row is read: the key of the row a join runs for is such an operand,
    /// in the branch that is not taken too. So each reads its operand as text or as the empty text
    /// (<see cref="TextOrEmpty"/>), and a row whose key is null, not stored or of another type is
    /// compared by the plain join alone.
    /// </para>
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
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$indexOfBytes", new BsonArray { TextOrEmpty(field), TextOrEmpty(key) }), 0 }),
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$strLenBytes", TextOrEmpty(field)), new BsonDocument("$strLenBytes", TextOrEmpty(key)) }),
            }),
            ["else"] = true,
        })));
    }

    /// <summary>A value where it is text, the empty text where it is anything else: an operand a byte function takes whatever the value is.</summary>
    private static BsonDocument TextOrEmpty(string value) =>
        new("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", value), "string" }), value, "" });

    /// <summary>
    /// The projection inside a join: what the join loads of its target (the select of a contract 1
    /// join; under contract 2 its key, what later stages read under its alias and its output set).
    /// </summary>
    private static BsonDocument Select(IReadOnlyList<ResolvedPath> select, bool keepDiscriminators = false, EntityDef? target = null)
    {
        var projection = new BsonDocument();

        foreach (var path in select)
            if (path.Storage is not null)
                projection[path.Storage] = 1;

        // A target whose rows are of several variants keeps the row's own discriminator: a variant
        // test on the alias and a reference that picks its target by the row's variant read it.
        if (keepDiscriminators && target?.Root is { Variants.Count: > 0, DiscriminatorElement: { } rowDiscriminator })
            projection[rowDiscriminator] = 1;

        // A select under a polymorphic object keeps its discriminator, unless a selected path covers it.
        foreach (var storage in keepDiscriminators ? DiscriminatorsOf(select) : [])
            if (!projection.Names.Any(kept => storage == kept || storage.StartsWith(kept + ".", StringComparison.Ordinal)))
                projection[storage] = 1;

        if (projection.ElementCount == 0)
            projection[KeyStorage] = 1;

        return projection;
    }

    // ---- values -----------------------------------------------------------------------------

    /// <summary>
    /// The values stage: at most <c>scan</c> of the rows that reach it, the collections above the path
    /// unwound, one group per value with the rows that hold it, then in one document the number of rows
    /// read and the list: the values the search keeps, in order, one more than the limit.
    /// <list type="bullet">
    /// <item>The bound on the rows stands first, so nothing after it reads more than that many rows,
    /// and an index that holds the organisation and the path answers them without reading a document.</item>
    /// <item>A collection is unwound keeping the row that holds none, and with the position of each
    /// element: the first element of a row, or the row without one, counts the row, so the rows read
    /// are known however many elements they hold.</item>
    /// <item>A value is listed for what a condition on the path selects, and null is no exception: a
    /// collection that is not there is a null (a comparison with null matches the row), a collection
    /// that is there and empty holds no value at all (no comparison matches it). So the emptiness of
    /// each collection is noted before its unwind, such a row adds nothing to a value's count, and a
    /// value nothing was counted for is not listed. The row is still one of the rows read.</item>
    /// <item>The search runs on the groups, not on the rows: there are as many comparisons as there
    /// are distinct values, and the rows read are counted whatever the search keeps.</item>
    /// <item>With counts the values lead by their number of rows, then by value; without, by value.
    /// A string orders, and folds into its group, under the aggregate's collation; null orders first.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<BsonDocument> Values(BoundStage.Values values, bool collated)
    {
        yield return new BsonDocument("$limit", values.Scan);

        var positions = new List<string>();
        var empties = new List<BsonValue>();

        foreach (var collection in values.Collections)
        {
            var empty = ReservedValuesEmpty + positions.Count;

            positions.Add(ReservedValuesIndex + positions.Count);
            empties.Add("$" + empty);

            yield return new BsonDocument("$addFields", new BsonDocument(empty, new BsonDocument("$eq", new BsonArray { "$" + collection, new BsonArray() })));
            yield return new BsonDocument("$unwind", new BsonDocument
            {
                ["path"] = "$" + collection,
                ["includeArrayIndex"] = positions[^1],
                ["preserveNullAndEmptyArrays"] = true,
            });
        }

        var group = new BsonDocument
        {
            ["_id"] = "$" + values.Path.Storage,
            // What an empty collection left of its row holds no value: it counts for none.
            [ValuesCount] = new BsonDocument("$sum", empties.Count == 0
                ? 1
                : new BsonDocument("$cond", new BsonArray { empties.Count == 1 ? empties[0] : new BsonDocument("$or", new BsonArray(empties)), 0, 1 })),
        };

        // One element per row counts the row: the first of each collection, or the row that has none.
        if (positions.Count > 0)
        {
            var first = positions.Select(position => (BsonValue)new BsonDocument("$in", new BsonArray { "$" + position, new BsonArray { 0, BsonNull.Value } })).ToList();

            group[ValuesRows] = new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { first.Count == 1 ? first[0] : new BsonDocument("$and", new BsonArray(first)), 1, 0 }));
        }

        yield return new BsonDocument("$group", group);

        var list = new BsonArray();

        // The null nothing but empty collections stood for is no value of the list.
        if (empties.Count > 0)
            list.Add(new BsonDocument("$match", new BsonDocument(ValuesCount, new BsonDocument("$gt", 0))));

        if (values.Search is { } search)
            list.Add(new BsonDocument("$match", Filter(search, null, collated)));

        list.Add(new BsonDocument("$sort", values.Counts
            ? new BsonDocument { [ValuesCount] = -1, ["_id"] = 1 }
            : new BsonDocument("_id", 1)));
        list.Add(new BsonDocument("$limit", values.Limit + 1));

        yield return new BsonDocument("$facet", new BsonDocument
        {
            [ValuesTotal] = new BsonArray { new BsonDocument("$group", new BsonDocument { ["_id"] = BsonNull.Value, [ValuesRows] = new BsonDocument("$sum", "$" + (positions.Count > 0 ? ValuesRows : ValuesCount)) }) },
            [ValuesList] = list,
        });
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

        return InCalendar(new BsonDocument("$dateTrunc", trunc));
    }

    /// <summary>The first instant a date on the wire can name: midnight of 1 January of year 1, UTC.</summary>
    internal static readonly BsonDateTime FirstInstant = new(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));

    /// <summary>The last instant a date on the wire can name, in the milliseconds the database keeps: the end of year 9999, UTC.</summary>
    internal static readonly BsonDateTime LastInstant = new(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));

    /// <summary>
    /// A bucket's key, kept within the calendar a date on the wire can name. A bucket starts at a
    /// local boundary, so the bucket of a row at the very start of the calendar can start before
    /// it (the first of January of year 1 lies in the last hours of the year before in every zone
    /// east of its meridian, and in the week before for a week that starts on another day): that
    /// bucket is keyed with the first instant, and one past the end with the last. The key is
    /// clamped in the group itself, so the rows of every bucket outside the calendar are one
    /// group, and a sort and a cursor read the key the row shows. No date gives no key, as before.
    /// </summary>
    private static BsonDocument InCalendar(BsonDocument bucket) => new("$let", new BsonDocument
    {
        ["vars"] = new BsonDocument("bucket", bucket),
        ["in"] = new BsonDocument("$switch", new BsonDocument
        {
            ["branches"] = new BsonArray
            {
                // Null sorts below every date, so the lower bound asks for a date first.
                new BsonDocument
                {
                    ["case"] = new BsonDocument("$and", new BsonArray
                    {
                        new BsonDocument("$gt", new BsonArray { "$$bucket", BsonNull.Value }),
                        new BsonDocument("$lt", new BsonArray { "$$bucket", FirstInstant }),
                    }),
                    ["then"] = FirstInstant,
                },
                new BsonDocument
                {
                    ["case"] = new BsonDocument("$gt", new BsonArray { "$$bucket", LastInstant }),
                    ["then"] = LastInstant,
                },
            },
            ["default"] = "$$bucket",
        }),
    });

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
        // A decimal under the addon bag is written wrapped by the bag serializer: read the value it holds.
        BoundExpression.Path { Unwrap: true } path => new BsonDocument("$ifNull", new BsonArray { "$" + path.Resolved.Storage + "._v", "$" + path.Resolved.Storage }),
        BoundExpression.Path path => "$" + path.Resolved.Storage,
        BoundExpression.Literal literal => new BsonDocument("$literal", literal.Value),

        // A quotient by zero is no number: it is null, as a quotient by null is. The divisor is read
        // once and taken as null where it is zero, so no row and no constant makes the database
        // divide by zero, in a branch it takes or in one it only looks at.
        BoundExpression.Arithmetic { Operator: "divide", Operands: [var dividend, var divisor] } => new BsonDocument("$let", new BsonDocument
        {
            ["vars"] = new BsonDocument(DivisorVariable, Expression(divisor)),
            ["in"] = new BsonDocument("$divide", new BsonArray
            {
                Expression(dividend),
                new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$$" + DivisorVariable, 0 }), BsonNull.Value, "$$" + DivisorVariable }),
            }),
        }),
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
        ref bool keyKeptAgainstProjection,
        bool wholeCollections = true)
    {
        var projection = new BsonDocument();
        var kept = keyedResolves.SelectMany(resolve => KeptStorages(resolve, wholeCollections)).Distinct(StringComparer.Ordinal).ToList();

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

            // A member read off the discriminator: an inclusion keeps the discriminator, and an
            // exclusion leaves it where it is, since the object's other members are read by it. The
            // row follows the shape and does not show the member.
            if (!project.Inclusion && path.Derived is not null)
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
        {
            foreach (var storage in kept)
                if (!project.Paths.Any(path => path.Storage is not null && Covers(path.Storage, storage)))
                    projection[storage] = 1;

            // A kept member may hold paths the caller projected inside it — a keyed resolve over a
            // collection keeps the whole collection, and the caller asked for one member of its
            // elements (tours.tourId beside a resolve of it). Mongo refuses a projection naming a path
            // and one inside it ("path collision"), so the narrower path merges into the one holding
            // it; the wire row still follows the shape and shows only what was asked for.
            foreach (var inner in projection.Names.Where(name => projection.Names.Any(outer => name != outer && Covers(outer, name))).ToList())
                projection.Remove(inner);
        }

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
    /// The discriminators a projection of <paramref name="paths"/> has to keep: for each path
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

    /// <summary>The root an inline resolve writes its outcome under (<c>outcomeAs</c>); null for every other stage.</summary>
    private static string? OutcomeOf(BoundStage stage) => stage is BoundStage.Resolve resolve && !IsKeyed(resolve) ? resolve.OutcomeAs : null;

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
            ? KeyedRuns(bound, resolve)
            : JoinUsed(bound, position, alias) || (OutcomeOf(bound.Stages[position]) is { } told && JoinUsed(bound, position, told));
    }

    /// <summary>
    /// Whether a keyed resolve is fetched: the final row shows its alias, its owning row, or an alias
    /// a stage continued under them adds. A continued stage reads the alias it continues under, so the
    /// keyed stage runs for it although the projection names neither; the row then leaves both out.
    /// So does the outcome it names: a row that shows only whether the record exists still has it looked for.
    /// </summary>
    public static bool KeyedRuns(BoundPipeline bound, BoundStage.Resolve resolve)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(resolve);

        return Shown(bound.FinalShape, resolve.As)
            || (resolve.OutcomeAs is { } outcomeAs && Shown(bound.FinalShape, outcomeAs))
            || (resolve.ParentAs is { } parentAs && Shown(bound.FinalShape, parentAs))
            || bound.Stages.OfType<ContinuedStage>().Any(continued => continued.Anchor == resolve.As && continued.Aliases.Any(alias => Shown(bound.FinalShape, alias)));
    }

    /// <summary>Whether the final row carries an alias: it is still a root and no projection after it dropped it.</summary>
    private static bool Shown(Shape shape, string alias) => shape.Carries(alias);

    /// <summary>
    /// Where a join that runs is emitted (<see cref="JoinPhase"/>). The rule: a join only the rows'
    /// display reads is moved from where it was written only to a place where it runs for no more rows
    /// than there.
    /// <list type="bullet">
    /// <item>A later stage filters, orders, unwinds, groups or resolves through its alias, or a group
    /// replaces the row: where it was written (<see cref="JoinPhase.BeforePage"/>).</item>
    /// <item>Nothing between it and the page yields more rows than it reads: after the page
    /// (<see cref="JoinPhase.AfterPage"/>). The page then holds no more rows than the join would have
    /// read where it was written.</item>
    /// <item>An unwind stands between it and the page (contract 2): the page holds one row per
    /// element, and a join after it would run once per element of a parent it was written to read
    /// once. Where the parents are ordered and cut before that unwind (<see cref="UnwindPlacement.ParentsFirst"/>)
    /// it runs on those parents, a page's worth of them (<see cref="JoinPhase.Parents"/>); elsewhere
    /// where it was written, once per parent.</item>
    /// </list>
    /// Contract 1 keeps its rule (after the page whenever only the display reads the alias), and so do
    /// the stages as written (<paramref name="asWritten"/>), which cut no parents. Explain reads it for
    /// a join step's phase, so what it says is what runs.
    /// </summary>
    public static JoinPhase PhaseOf(BoundPipeline bound, int index, string alias, bool asWritten = false)
    {
        ArgumentNullException.ThrowIfNull(bound);

        if (!DisplayOnly(bound, index, alias))
            return JoinPhase.BeforePage;

        if (!bound.UnwoundRows)
            return JoinPhase.AfterPage;

        var unwound = -1;

        for (var later = index + 1; later < bound.Stages.Count && unwound < 0; later++)
            if (bound.Stages[later] is BoundStage.Unwind)
                unwound = later;

        if (unwound < 0)
            return JoinPhase.AfterPage;

        return !asWritten && PlaceUnwind(bound, bound.Collated) is { ParentsFirst: true } placed && placed.Position == unwound
            ? JoinPhase.Parents
            : JoinPhase.BeforePage;
    }

    /// <summary>
    /// <see cref="PhaseOf(BoundPipeline, int, string, bool)"/> for an inline resolve: its alias is only
    /// displayed, and no later stage reads the outcome it names (<c>outcomeAs</c>): a condition or a
    /// group over the outcome needs it on every candidate row. A projection only passes the outcome on
    /// or drops it.
    /// </summary>
    public static JoinPhase PhaseOf(BoundPipeline bound, int index, BoundStage.Resolve resolve, bool asWritten = false)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(resolve);

        return resolve.OutcomeAs is { } outcomeAs && bound.Stages.Skip(index + 1).Any(stage => stage is not BoundStage.Project && Reads(stage, outcomeAs))
            ? JoinPhase.BeforePage
            : PhaseOf(bound, index, resolve.As, asWritten);
    }

    /// <summary>
    /// The unwind a join only the display reads is kept before (its phase is not
    /// <see cref="JoinPhase.AfterPage"/> although nothing but the display reads its alias); null for a
    /// join a later stage reads and for one that runs after the page. Explain says it at the join.
    /// </summary>
    public static BoundStage.Unwind? KeptBefore(BoundPipeline bound, int index, bool asWritten = false)
    {
        ArgumentNullException.ThrowIfNull(bound);

        var (alias, phase) = bound.Stages[index] switch
        {
            BoundStage.Lookup lookup => (lookup.As, PhaseOf(bound, index, lookup.As, asWritten)),
            BoundStage.Resolve resolve when !IsKeyed(resolve) => (resolve.As, PhaseOf(bound, index, resolve, asWritten)),
            _ => (null, JoinPhase.AfterPage),
        };

        if (alias is null || phase == JoinPhase.AfterPage || !DisplayOnly(bound, index, alias)
            || (bound.Stages[index] is BoundStage.Resolve { OutcomeAs: { } outcomeAs } && bound.Stages.Skip(index + 1).Any(stage => stage is not BoundStage.Project && Reads(stage, outcomeAs))))
            return null;

        return bound.Stages.Skip(index + 1).OfType<BoundStage.Unwind>().FirstOrDefault();
    }

    /// <summary>Whether the join at <paramref name="index"/> runs after the page is taken (<see cref="PhaseOf(BoundPipeline, int, string, bool)"/>).</summary>
    public static bool JoinsAfterPage(BoundPipeline bound, int index, string alias, bool asWritten = false) =>
        PhaseOf(bound, index, alias, asWritten) == JoinPhase.AfterPage;

    /// <summary>Whether the inline resolve at <paramref name="index"/> runs after the page is taken (<see cref="PhaseOf(BoundPipeline, int, BoundStage.Resolve, bool)"/>).</summary>
    public static bool JoinsAfterPage(BoundPipeline bound, int index, BoundStage.Resolve resolve, bool asWritten = false) =>
        PhaseOf(bound, index, resolve, asWritten) == JoinPhase.AfterPage;

    /// <summary>
    /// Whether only the rows' display reads a join's alias: no later stage filters, orders, unwinds,
    /// groups or resolves through it, no group replaces the row, and every later projection passes the
    /// alias on. The rows and their order are then the same wherever the join runs, and it adds the
    /// same value to each of them.
    /// <para>
    /// A projection passes the alias on when it keeps it whole, and, for a join whose loads the
    /// binder inferred (contract 2), also when it names paths under it: the join then loads those
    /// paths with its key, and the row shows them alone (<see cref="BoundPipeline.Loads"/>, the
    /// output set of the final shape), so what the projection would cut is never loaded and the
    /// join need not stand before it. Without this a contract 2 join, which has no <c>select</c>,
    /// could show chosen members only by running for every candidate row.
    /// </para>
    /// </summary>
    private static bool DisplayOnly(BoundPipeline bound, int index, string alias)
    {
        var stages = bound.Stages;
        var inferred = bound.Loads.ContainsKey(alias);

        for (var later = index + 1; later < stages.Count; later++)
        {
            switch (stages[later])
            {
                // A group and a values stage replace the row: what they do not read is in no row.
                case BoundStage.Group or BoundStage.Values:
                    return false;

                case BoundStage.Project project:
                    if (!PassesWhole(project, alias) && !(inferred && NamesUnder(project, alias)))
                        return false;
                    break;

                // A lookup on the alias reads the joined parent, which is there when that lookup runs
                // too: it is only displayed itself, so it runs in the same place or a later one, and
                // the joins of one place run in stage order.
                case BoundStage.Lookup lookup when lookup.On == alias && DisplayOnly(bound, later, lookup.As):
                    break;

                case var stage when Reads(stage, alias):
                    return false;
            }
        }

        return true;
    }

    /// <summary>Whether an inclusion keeps the alias by naming it or paths under it: what a join with inferred loads is cut to on the way out.</summary>
    private static bool NamesUnder(BoundStage.Project project, string alias) =>
        project.Inclusion && project.Paths.Any(path => RootIs(path, alias));

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
        BoundStage.Values values => RootIs(values.Path, alias),
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
