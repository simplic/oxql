using MongoDB.Bson;
using MongoDB.Bson.Serialization.Options;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Mongo;

/// <summary>A slot in the page filter that a semi-join fills with the owner's ids before execution.</summary>
public sealed record SemiJoinSlot(BoundCondition.Leaf Leaf, BsonArray Ids);

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

    public required IReadOnlyList<BoundStage.Resolve> RemoteResolves { get; init; }

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
}

/// <summary>What the compiler needs from the host beside the bound pipeline; a null collation is the default one.</summary>
public sealed record CompileOptions(int MaxTimeMs, bool? AllowDiskUse, int CountCap, CollationOptions? Collation = null);

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
        var remote = new List<BoundStage.Resolve>();
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

        stages.Add(new BsonDocument("$match", keyset is null ? scope : new BsonDocument("$and", new BsonArray { scope, keyset })));

        var countStages = page.IncludeTotalCount ? new List<BsonDocument> { new("$match", scope) } : null;
        var sortEmitted = false;

        // A join only the rows' display reads runs after the page is taken: the sort stays
        // next to the limit, which lets the server keep the top rows instead of ordering
        // every candidate, and the join is paid per page row rather than per candidate row.
        var lateJoins = new List<BsonDocument>();

        // The local key each of those joins matches on. It is read only after the page, so it
        // survives every projection in between in storage, as a remote resolve's reference
        // does; the wire row follows the shape and leaves it out when it was not asked for.
        var lateJoinKeys = new List<string>();

        for (var index = 0; index < bound.Stages.Count; index++)
        {
            var stage = bound.Stages[index];
            var emitted = new List<BsonDocument>();

            switch (stage)
            {
                case BoundStage.Match match:
                    emitted.Add(new BsonDocument("$match", Filter(match.Condition, semiJoins, collated)));
                    break;

                case BoundStage.Lookup lookup when JoinsAfterPage(bound.Stages, index, lookup.As):
                    lateJoins.Add(Lookup(lookup, semiJoins, collated));
                    lateJoinKeys.Add(lookup.ParentKeyStorage);
                    break;

                case BoundStage.Lookup lookup:
                    emitted.Add(Lookup(lookup, semiJoins, collated));
                    break;

                case BoundStage.Resolve { IsRemote: true } remoteResolve:
                    remote.Add(remoteResolve);
                    break;

                case BoundStage.Resolve resolve when JoinsAfterPage(bound.Stages, index, resolve.As):
                    lateJoins.AddRange(LocalResolve(resolve, semiJoins, collated));
                    lateJoinKeys.Add(resolve.Reference.Storage!);
                    break;

                case BoundStage.Resolve resolve:
                    emitted.AddRange(LocalResolve(resolve, semiJoins, collated));
                    break;

                case BoundStage.Unwind unwind:
                    // The caller's index when it asked for one, otherwise a reserved one the
                    // paging sort uses and the pipeline drops again before it skips.
                    var indexField = unwind.IncludeIndex;

                    if (unwoundOffsetPaging && indexField is null)
                        reservedIndexes.Add(indexField = ReservedIndex + reservedIndexes.Count);
                    else if (unwoundOffsetPaging)
                        reservedIndexes.Add(indexField!);

                    emitted.Add(new BsonDocument("$unwind", new BsonDocument
                    {
                        ["path"] = "$" + unwind.Path.Storage,
                        ["preserveNullAndEmptyArrays"] = unwind.PreserveNull,
                    }.AddIf(indexField is not null, "includeArrayIndex", indexField)));

                    if (unwind.As is not null)
                        emitted.Add(new BsonDocument("$set", new BsonDocument(unwind.As, "$" + unwind.Path.Storage)));
                    break;

                case BoundStage.Group group:
                    emitted.AddRange(Group(group));
                    break;

                case BoundStage.Project project:
                    if (Project(project, remote, lateJoinKeys, keepKey, reservedIndexes, sortStorages, sortKeptAgainstProjection, ref keyKeptAgainstProjection) is { } projection)
                        emitted.Add(projection);
                    break;

                // The last sort is the one the page is taken in, so on an unwound shape it is
                // emitted after the loop with the tie-breakers that make it total. Any earlier
                // sort is the caller's own and stays where it was written.
                case BoundStage.Sort sort when unwoundOffsetPaging && ReferenceEquals(sort, bound.Sort):
                    pagingSort = sort.Fields;
                    break;

                case BoundStage.Sort sort:
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
            RemoteResolves = remote,
            SemiJoins = semiJoins,
            MaxTimeMs = options.MaxTimeMs,
            AllowDiskUse = options.AllowDiskUse,
            CountCap = countCap,
            KeyKeptAgainstProjection = keyKeptAgainstProjection,
            SortKeptAgainstProjection = sortKeptAgainstProjection,
            Collation = collated ? CollationDocument(options.Collation ?? new CollationOptions()) : null,
        };
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

        var storage = leaf.Path.Storage!;

        // A decimal under the addon bag is written wrapped by the bag serializer: match both places.
        if (leaf.Path.Addon is not null && leaf.Path.Kind == Kind.Decimal)
            return new BsonDocument("$or", new BsonArray { LeafAt(storage, leaf, collated), LeafAt(storage + "._v", leaf, collated) });

        return LeafAt(storage, leaf, collated);
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

    // ---- joins ------------------------------------------------------------------------------

    private static BsonDocument Lookup(BoundStage.Lookup lookup, List<SemiJoinSlot> semiJoins, bool collated)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", ScopeFilter(lookup.ChildScope)) };
        var exactKey = collated && IsStringStored(lookup.ChildReference);

        if (exactKey)
            pipeline.Add(ExactKeyMatch(lookup.ChildKeyStorage));

        if (lookup.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(lookup.Filter, semiJoins, collated)));

        pipeline.Add(new BsonDocument("$sort", new BsonDocument(KeyStorage, 1)));
        pipeline.Add(new BsonDocument("$limit", lookup.Limit));
        pipeline.Add(new BsonDocument("$project", Select(lookup.Select)));

        return new BsonDocument("$lookup", Join(lookup.From.Collection, lookup.ParentKeyStorage, lookup.ChildKeyStorage, exactKey, pipeline, lookup.As));
    }

    private static IEnumerable<BsonDocument> LocalResolve(BoundStage.Resolve resolve, List<SemiJoinSlot> semiJoins, bool collated)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", ScopeFilter(resolve.TargetScope!)) };
        var exactKey = collated && IsStringStored(resolve.Reference);

        if (exactKey)
            pipeline.Add(ExactKeyMatch(resolve.TargetFieldStorage!));

        if (resolve.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(resolve.Filter, semiJoins, collated)));

        pipeline.Add(new BsonDocument("$limit", 1));
        pipeline.Add(new BsonDocument("$project", Select(resolve.Select!)));

        // No caller alias ends in the suffix, so the temporary field shadows nothing.
        var temporary = resolve.As + Aliases.ReservedSuffix;

        yield return new BsonDocument("$lookup", Join(resolve.Target!.Collection, resolve.Reference.Storage!, resolve.TargetFieldStorage!, exactKey, pipeline, temporary));
        yield return new BsonDocument("$set", new BsonDocument(resolve.As, new BsonDocument("$arrayElemAt", new BsonArray { "$" + temporary, 0 })));
        yield return new BsonDocument("$unset", temporary);
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

    private static BsonDocument Select(IReadOnlyList<ResolvedPath> select)
    {
        var projection = new BsonDocument();

        foreach (var path in select)
            if (path.Storage is not null)
                projection[path.Storage] = 1;

        if (projection.ElementCount == 0)
            projection[KeyStorage] = 1;

        return projection;
    }

    // ---- group ------------------------------------------------------------------------------

    private static IEnumerable<BsonDocument> Group(BoundStage.Group group)
    {
        var groupDocument = new BsonDocument { ["_id"] = GroupId(group.Keys) };
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
        IReadOnlyList<BoundStage.Resolve> remoteResolves,
        IReadOnlyList<string> lateJoinKeys,
        bool keepKey,
        IReadOnlyList<string> reservedIndexes,
        IReadOnlyList<string> sortStorages,
        List<string> sortKeptAgainstProjection,
        ref bool keyKeptAgainstProjection)
    {
        var projection = new BsonDocument();
        var kept = remoteResolves.Select(resolve => resolve.Reference.Storage).Where(storage => storage is not null).Select(storage => storage!).ToList();

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

    /// <summary>The alias a lookup or a local resolve joins under; null for every other stage.</summary>
    private static string? JoinAlias(BoundStage stage) => stage switch
    {
        BoundStage.Lookup lookup => lookup.As,
        BoundStage.Resolve { IsRemote: false } resolve => resolve.As,
        _ => null,
    };

    /// <summary>
    /// Whether a join can run after the page is taken. It can when no later stage filters,
    /// orders, unwinds, groups or resolves through its alias, no group replaces the row, and
    /// every later projection passes the alias whole: the page then holds the same rows, and
    /// the join adds the same value to each of them, whether it runs before the sort or
    /// after the limit.
    /// </summary>
    private static bool JoinsAfterPage(IReadOnlyList<BoundStage> stages, int index, string alias)
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
