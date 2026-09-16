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

    public required int CountCap { get; init; }
}

/// <summary>What the compiler needs from the host beside the bound pipeline.</summary>
public sealed record CompileOptions(int MaxTimeMs, bool? AllowDiskUse, int CountCap);

/// <summary>
/// Emits the aggregation pipeline for a bound pipeline: typed <c>$match</c>, per-field
/// case-insensitive regex, <c>$elemMatch</c>, joins as indexed <c>$lookup</c> with the scope
/// inside, null-aware keyset cursors merged into the leading scope match, offset paging after
/// a group, and the count pipeline with its <c>$limit</c> after any <c>$group</c>.
/// </summary>
public static class MongoCompiler
{
    private const string KeyStorage = "_id";

    public static CompiledQuery Compile(BoundPipeline bound, CompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(options);

        var page = bound.Page;
        var stages = new List<BsonDocument>();
        var remote = new List<BoundStage.Resolve>();
        var semiJoins = new List<SemiJoinSlot>();
        var sortFields = bound.Sort?.Fields ?? [];

        // The leading scope, with the keyset predicate merged in on a root shape.
        var scope = ScopeFilter(bound.Scope);
        var keyset = page.Cursor is { Mode: PagingMode.Keyset } cursor ? KeysetFrom(cursor, sortFields) : null;

        stages.Add(new BsonDocument("$match", keyset is null ? scope : new BsonDocument("$and", new BsonArray { scope, keyset })));

        var countStages = page.IncludeTotalCount ? new List<BsonDocument> { new("$match", scope) } : null;
        var sortEmitted = false;

        for (var index = 0; index < bound.Stages.Count; index++)
        {
            var stage = bound.Stages[index];
            var emitted = new List<BsonDocument>();

            switch (stage)
            {
                case BoundStage.Match match:
                    emitted.Add(new BsonDocument("$match", Filter(match.Condition, semiJoins)));
                    break;

                case BoundStage.Lookup lookup:
                    emitted.Add(Lookup(lookup, semiJoins));
                    break;

                case BoundStage.Resolve { IsRemote: true } remoteResolve:
                    remote.Add(remoteResolve);
                    break;

                case BoundStage.Resolve resolve:
                    emitted.AddRange(LocalResolve(resolve, semiJoins));
                    break;

                case BoundStage.Unwind unwind:
                    emitted.Add(new BsonDocument("$unwind", new BsonDocument
                    {
                        ["path"] = "$" + unwind.Path.Storage,
                        ["preserveNullAndEmptyArrays"] = unwind.PreserveNull,
                    }.AddIf(unwind.IncludeIndex is not null, "includeArrayIndex", unwind.IncludeIndex)));

                    if (unwind.As is not null)
                        emitted.Add(new BsonDocument("$set", new BsonDocument(unwind.As, "$" + unwind.Path.Storage)));
                    break;

                case BoundStage.Group group:
                    emitted.AddRange(Group(group));
                    break;

                case BoundStage.Project project:
                    emitted.Add(Project(project));
                    break;

                case BoundStage.Sort sort:
                    emitted.Add(Sort(sort.Fields, bound.PagingMode == PagingMode.Keyset));
                    sortEmitted = true;
                    break;

                case BoundStage.Page:
                    break;
            }

            stages.AddRange(emitted);

            // The count pipeline carries everything up to the sort and the page, lookups
            // only when a later stage reads their alias.
            if (countStages is not null && stage is not (BoundStage.Sort or BoundStage.Page))
            {
                if (stage is BoundStage.Lookup lookupStage && !UsesAlias(bound.Stages.Skip(index + 1), lookupStage.As))
                    continue;

                countStages.AddRange(emitted);
            }
        }

        if (!sortEmitted && bound.PagingMode == PagingMode.Keyset)
            stages.Add(new BsonDocument("$sort", new BsonDocument(KeyStorage, 1)));

        var offset = page.Cursor is { Mode: PagingMode.Offset } offsetCursor ? offsetCursor.Offset : page.Offset;

        if (offset > 0)
            stages.Add(new BsonDocument("$skip", offset));

        stages.Add(new BsonDocument("$limit", page.Limit + 1));

        if (countStages is not null)
        {
            countStages.Add(new BsonDocument("$limit", options.CountCap + 1));
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
            CountCap = options.CountCap,
        };
    }

    /// <summary>The scope equality: one typed comparison on one indexed member.</summary>
    public static BsonDocument ScopeFilter(BoundStage.Scope scope) =>
        new(scope.OrganisationStorage, new BsonBinaryData(scope.Organisation, scope.Representation));

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

    /// <summary>Compiles a condition tree to a filter document.</summary>
    public static BsonDocument Filter(BoundCondition condition, List<SemiJoinSlot>? semiJoins = null) => condition switch
    {
        BoundCondition.And and => new BsonDocument("$and", new BsonArray(and.Conditions.Select(inner => Filter(inner, semiJoins)))),
        BoundCondition.Or or => new BsonDocument("$or", new BsonArray(or.Conditions.Select(inner => Filter(inner, semiJoins)))),
        BoundCondition.Not not => new BsonDocument("$nor", new BsonArray { Filter(not.Condition, semiJoins) }),
        BoundCondition.Any any => new BsonDocument(any.Path.Storage!, new BsonDocument("$elemMatch", Filter(any.Inner, semiJoins))),
        BoundCondition.Leaf leaf => Leaf(leaf, semiJoins),
        _ => new BsonDocument(),
    };

    private static BsonDocument Leaf(BoundCondition.Leaf leaf, List<SemiJoinSlot>? semiJoins)
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
            return new BsonDocument("$or", new BsonArray { LeafAt(storage, leaf), LeafAt(storage + "._v", leaf) });

        return LeafAt(storage, leaf);
    }

    private static BsonDocument LeafAt(string storage, BoundCondition.Leaf leaf)
    {
        var op = leaf.Op;

        switch (leaf.Operand)
        {
            case BoundOperand.Null:
                return op == "neq"
                    ? new BsonDocument(storage, new BsonDocument("$ne", BsonNull.Value))
                    : new BsonDocument(storage, BsonNull.Value);

            case BoundOperand.Set set:
                var values = leaf.IgnoreCase
                    ? new BsonArray(set.Values.Select(value => value is BsonString text ? (BsonValue)new BsonRegularExpression("^" + RegexGuard.Escape(text.Value) + "$", "i") : value))
                    : new BsonArray(set.Values);

                return new BsonDocument(storage, new BsonDocument(op == "nin" ? "$nin" : "$in", values));

            case BoundOperand.Tolerant tolerant:
                if (op == "neq")
                    return new BsonDocument("$and", new BsonArray(tolerant.Alternatives.Select(value => new BsonDocument(storage, new BsonDocument("$ne", value)))));

                return new BsonDocument("$or", new BsonArray(tolerant.Alternatives.Select(value => Compare(storage, op, value, leaf.IgnoreCase))));

            case BoundOperand.Single single:
                return Compare(storage, op, single.Value, leaf.IgnoreCase);

            default:
                return new BsonDocument(storage, BsonNull.Value);
        }
    }

    private static BsonDocument Compare(string storage, string op, BsonValue value, bool ignoreCase)
    {
        var flags = ignoreCase ? "i" : "";

        return op switch
        {
            "eq" when ignoreCase && value is BsonString text => new BsonDocument(storage, new BsonRegularExpression("^" + RegexGuard.Escape(text.Value) + "$", "i")),
            "eq" => new BsonDocument(storage, value),
            "neq" when ignoreCase && value is BsonString text => new BsonDocument(storage, new BsonDocument("$not", new BsonRegularExpression("^" + RegexGuard.Escape(text.Value) + "$", "i"))),
            "neq" => new BsonDocument(storage, new BsonDocument("$ne", value)),
            "gt" => new BsonDocument(storage, new BsonDocument("$gt", value)),
            "gte" => new BsonDocument(storage, new BsonDocument("$gte", value)),
            "lt" => new BsonDocument(storage, new BsonDocument("$lt", value)),
            "lte" => new BsonDocument(storage, new BsonDocument("$lte", value)),
            "contains" => new BsonDocument(storage, new BsonRegularExpression(RegexGuard.Escape(value.AsString), flags)),
            "startsWith" => new BsonDocument(storage, new BsonRegularExpression("^" + RegexGuard.Escape(value.AsString), flags)),
            "endsWith" => new BsonDocument(storage, new BsonRegularExpression(RegexGuard.Escape(value.AsString) + "$", flags)),
            "regex" => new BsonDocument(storage, new BsonRegularExpression(value.AsString, flags)),
            "exists" => new BsonDocument(storage, new BsonDocument("$exists", value)),
            _ => new BsonDocument(storage, value),
        };
    }

    // ---- joins ------------------------------------------------------------------------------

    private static BsonDocument Lookup(BoundStage.Lookup lookup, List<SemiJoinSlot> semiJoins)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", ScopeFilter(lookup.ChildScope)) };

        if (lookup.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(lookup.Filter, semiJoins)));

        pipeline.Add(new BsonDocument("$sort", new BsonDocument(KeyStorage, 1)));
        pipeline.Add(new BsonDocument("$limit", lookup.Limit));
        pipeline.Add(new BsonDocument("$project", Select(lookup.Select)));

        return new BsonDocument("$lookup", new BsonDocument
        {
            ["from"] = lookup.From.Collection,
            ["localField"] = lookup.ParentKeyStorage,
            ["foreignField"] = lookup.ChildKeyStorage,
            ["pipeline"] = pipeline,
            ["as"] = lookup.As,
        });
    }

    private static IEnumerable<BsonDocument> LocalResolve(BoundStage.Resolve resolve, List<SemiJoinSlot> semiJoins)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", ScopeFilter(resolve.TargetScope!)) };

        if (resolve.Filter is not null)
            pipeline.Add(new BsonDocument("$match", Filter(resolve.Filter, semiJoins)));

        pipeline.Add(new BsonDocument("$limit", 1));
        pipeline.Add(new BsonDocument("$project", Select(resolve.Select!)));

        var temporary = resolve.As + "__arr";

        yield return new BsonDocument("$lookup", new BsonDocument
        {
            ["from"] = resolve.Target!.Collection,
            ["localField"] = resolve.Reference.Storage,
            ["foreignField"] = resolve.TargetFieldStorage,
            ["pipeline"] = pipeline,
            ["as"] = temporary,
        });
        yield return new BsonDocument("$set", new BsonDocument(resolve.As, new BsonDocument("$arrayElemAt", new BsonArray { "$" + temporary, 0 })));
        yield return new BsonDocument("$unset", temporary);
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

        foreach (var key in keys)
            document[key.As] = KeyExpression(key);

        return document;
    }

    private static BsonValue KeyExpression(GroupKey key)
    {
        if (key.Trunc is null)
            return "$" + key.Path!.Storage;

        var trunc = new BsonDocument
        {
            ["date"] = "$" + key.Trunc.Path.Storage,
            ["unit"] = key.Trunc.Unit,
            ["timezone"] = key.Trunc.Timezone,
        };

        if (key.Trunc.Unit == "week")
            trunc["startOfWeek"] = key.Trunc.WeekStart ?? "monday";

        return new BsonDocument("$dateTrunc", trunc);
    }

    private static BsonValue Accumulator(Aggregate field) => field.Function switch
    {
        "count" => new BsonDocument("$sum", 1),
        "countDistinct" => new BsonDocument("$addToSet", Expression(field.Argument!)),
        "sum" => new BsonDocument("$sum", Expression(field.Argument!)),
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

    private static BsonDocument Project(BoundStage.Project project)
    {
        var projection = new BsonDocument();

        foreach (var path in project.Paths)
        {
            if (path.Storage is null)
                continue;

            projection[path.Storage] = project.Inclusion ? 1 : 0;
        }

        if (!project.IncludeId && !projection.Contains(KeyStorage))
            projection[KeyStorage] = 0;

        return new BsonDocument("$project", projection);
    }

    private static BsonDocument Sort(IReadOnlyList<BoundSortField> fields, bool tieBreak)
    {
        var sort = new BsonDocument();

        foreach (var field in fields)
            sort[field.Path.Storage!] = field.Ascending ? 1 : -1;

        if (tieBreak && !sort.Contains(KeyStorage))
            sort[KeyStorage] = 1;

        return new BsonDocument("$sort", sort);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static bool UsesAlias(IEnumerable<BoundStage> later, string alias)
    {
        foreach (var stage in later)
        {
            var uses = stage switch
            {
                BoundStage.Match match => Paths(match.Condition).Any(path => RootIs(path, alias)),
                BoundStage.Unwind unwind => RootIs(unwind.Path, alias),
                BoundStage.Group group => group.Keys.Any(key => (key.Path is not null && RootIs(key.Path, alias)) || (key.Trunc is not null && RootIs(key.Trunc.Path, alias)))
                    || group.Fields.Any(field => field.Argument is not null && Paths(field.Argument).Any(path => RootIs(path, alias))),
                BoundStage.Project project => project.Paths.Any(path => RootIs(path, alias)),
                BoundStage.Resolve resolve => RootIs(resolve.Reference, alias),
                _ => false,
            };

            if (uses)
                return true;
        }

        return false;
    }

    private static bool RootIs(ResolvedPath path, string alias) => path.Root.StoragePrefix == alias || path.Wire == alias || path.Wire.StartsWith(alias + ".", StringComparison.Ordinal);

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
