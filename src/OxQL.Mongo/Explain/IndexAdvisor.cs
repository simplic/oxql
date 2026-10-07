using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Mongo.Explain;

/// <summary>
/// The opt-in index advisory of an explain (<c>include: ["indexes"]</c>), static: for the request as
/// bound and compiled, the indexes that would serve it, each a complete definition (collection, key
/// document in order, collation), what of the request it serves by the paths and filter entries the
/// caller wrote, and whether an index the collection has serves it already.
/// <para>
/// The key order is equality, sort, range: the organisation member the engine always filters by, then
/// the members the request compares for equality (<c>eq</c>, <c>in</c>), then the sort keys in the
/// request's directions with the key that completes the order, then the members it bounds by a range.
/// A request that ends in <c>values</c> or runs a <c>group</c> has no sort to serve: its index bounds
/// the rows by equality and range and then holds what the stage reads, so the database answers from
/// the index's keys alone where everything read is a member of the entity's own row.
/// </para>
/// <para>
/// Nothing runs on the server and nothing is created: the index lists are read through
/// <see cref="IIndexSource"/> before this runs, and where none was read every index says
/// <c>existing: "unknown"</c> rather than that it is missing.
/// </para>
/// </summary>
public static class IndexAdvisor
{
    private const string KeyStorage = "_id";

    /// <summary>The roles of a key of an advised index, in the order the keys stand.</summary>
    public static class Roles
    {
        /// <summary>The organisation member the engine always filters by.</summary>
        public const string Scope = "scope";
        /// <summary>A member the request compares for equality.</summary>
        public const string Equality = "equality";
        /// <summary>The member a join matches on.</summary>
        public const string Join = "join";
        /// <summary>A sort key, in the request's direction.</summary>
        public const string Sort = "sort";
        /// <summary>A member the request bounds by a range.</summary>
        public const string Range = "range";
        /// <summary>A member a group key reads.</summary>
        public const string Group = "group";
        /// <summary>The member whose values are listed.</summary>
        public const string Values = "values";
        /// <summary>A member held so that no document is read for it; it bounds nothing.</summary>
        public const string Covered = "covered";
    }

    /// <summary>What an advised index is for: the request's rows, its group, its list of values, or a join.</summary>
    public static class Purposes
    {
        /// <summary>The rows of a page.</summary>
        public const string Rows = "rows";
        /// <summary>The rows a group reads.</summary>
        public const string Group = "group";
        /// <summary>A list of values.</summary>
        public const string Values = "values";
        /// <summary>A join the aggregate runs.</summary>
        public const string Join = "join";
    }

    /// <summary>Whether an index of the collection serves what an advised one would.</summary>
    public static class Existing
    {
        /// <summary>An index of the collection serves all of it.</summary>
        public const string Served = "served";
        /// <summary>An index of the collection serves part of it.</summary>
        public const string Partial = "partial";
        /// <summary>No index of the collection leads to it.</summary>
        public const string Missing = "missing";
        /// <summary>This host read no index list of the collection.</summary>
        public const string Unknown = "unknown";
    }

    private enum Bound
    {
        None,
        Equality,
        Range,
    }

    /// <summary>One condition of the request on one member, and how an index key could bound it.</summary>
    private sealed record Condition(string Path, string? Field, string Op, Bound Bound, string? Reason, object? Filter, int? Stage, bool Text, bool Multikey, bool Single, string? Via)
    {
        /// <summary>Whether an index key can hold the member at all: it is a stored member of the entity's own row.</summary>
        public bool Keyable => Field is not null;
    }

    private sealed record Key(string Field, int Direction, string Role, string? Path, bool Text);

    private sealed class Advised
    {
        public required string Collection { get; init; }

        public required string Entity { get; init; }

        public required string Purpose { get; init; }

        public List<Key> Keys { get; } = [];

        public BsonDocument? Collation { get; set; }

        public bool Multikey { get; set; }

        /// <summary>The index holds every member the request reads up to its group or its values: no document is read.</summary>
        public bool Covers { get; set; }

        public List<JsonObject> Serves { get; } = [];

        /// <summary>The conditions already listed among <see cref="Serves"/>.</summary>
        public HashSet<object> ListedConditions { get; } = new(ReferenceEqualityComparer.Instance);

        public string? Note { get; set; }

        /// <summary>The index lists of the collection, or null where none was read.</summary>
        public IReadOnlyList<BsonDocument>? Listed { get; set; }

        public string Id
        {
            get
            {
                var text = Collection + "|" + string.Join(",", Keys.Select(key => key.Field + ":" + key.Direction))
                    + "|" + (Collation is null ? "" : Collation.GetValue("locale", "") + "/" + Collation.GetValue("strength", ""));

                return "ix:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
            }
        }
    }

    private sealed record IndexDef(string Name, IReadOnlyList<(string Field, int Direction)> Keys, (string Locale, int Strength)? Collation);

    /// <summary>
    /// The advisory of a bound and compiled request. <paramref name="indexes"/> are the index list of
    /// the entity's collection and <paramref name="joined"/> those of the collections its joins read,
    /// by collection name; null where this host read none (no index source, or the read failed).
    /// </summary>
    public static JsonObject Advise(BoundPipeline bound, CompiledQuery compiled, IReadOnlyList<BsonDocument>? indexes, IReadOnlyDictionary<string, IReadOnlyList<BsonDocument>>? joined = null)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(compiled);

        var unbounded = new JsonArray();
        var advised = new List<Advised>();
        var own = Own(bound, compiled, unbounded);

        own.Listed = indexes;
        advised.Add(own);

        foreach (var join in Joins(bound, compiled))
        {
            join.Listed = joined is not null && joined.TryGetValue(join.Collection, out var listed) ? listed : null;

            // Two joins onto the same member of the same collection are served by one index.
            if (advised.FirstOrDefault(known => known.Id == join.Id) is { } same)
                same.Serves.AddRange(join.Serves.Select(serve => serve.DeepClone().AsObject()));
            else
                advised.Add(join);
        }

        var advisory = new JsonObject
        {
            ["existing"] = indexes is null ? "unknown" : "known",
            ["indexes"] = new JsonArray(advised.Select(index => (JsonNode)Written(index)).ToArray()),
        };

        if (unbounded.Count > 0)
            advisory["unbounded"] = unbounded;

        return advisory;
    }

    // ---- the request's own index ------------------------------------------------------------

    private static Advised Own(BoundPipeline bound, CompiledQuery compiled, JsonArray unbounded)
    {
        var stages = bound.Stages;
        var grouped = IndexOf(stages, stage => stage is BoundStage.Group);
        var valued = IndexOf(stages, stage => stage is BoundStage.Values);
        // The rows an index can bound are those before the first stage that replaces them.
        var end = grouped >= 0 ? grouped : valued >= 0 ? valued : stages.Count;
        var conditions = Conditions(bound, compiled, end, unbounded);
        var collated = bound.Collated;
        var index = new Advised
        {
            Collection = bound.Entity.Collection,
            Entity = bound.Entity.Id,
            Purpose = grouped >= 0 ? Purposes.Group : valued >= 0 ? Purposes.Values : Purposes.Rows,
        };

        index.Keys.Add(new Key(bound.Scope.OrganisationStorage, 1, Roles.Scope, null, false));

        if (grouped >= 0)
            GroupKeys(index, bound, grouped, conditions, collated, compiled.Collation);
        else if (valued >= 0)
            ValuesKeys(index, bound, valued, conditions, collated, compiled.Collation);
        else
            RowKeys(index, bound, compiled, conditions, collated, unbounded);

        foreach (var condition in conditions)
            if (condition.Bound == Bound.None || !index.Keys.Any(key => key.Field == condition.Field && key.Role is Roles.Equality or Roles.Range or Roles.Sort))
                unbounded.Add(Unbounded(condition));

        index.Multikey = conditions.Any(condition => condition.Multikey && index.Keys.Any(key => key.Field == condition.Field));

        return index;
    }

    /// <summary>Rows: equality, the sort with the key that completes it, range.</summary>
    private static void RowKeys(Advised index, BoundPipeline bound, CompiledQuery compiled, List<Condition> conditions, bool collated, JsonArray unbounded)
    {
        var sort = SortKeys(bound, compiled, unbounded);
        // A set on a member the request orders by is a range of that order, not a point of it.
        var equalities = Distinct(conditions, Bound.Equality).Where(condition => condition.Single || !sort.Any(key => key.Field == condition.Field)).ToList();
        var ranges = Distinct(conditions, Bound.Range).Concat(Distinct(conditions, Bound.Equality).Where(condition => !equalities.Contains(condition))).ToList();

        foreach (var condition in equalities)
            Add(index, condition, Roles.Equality);

        foreach (var key in sort)
        {
            // A member held to one value orders nothing.
            if (index.Keys.Any(known => known.Field == key.Field))
                continue;

            index.Keys.Add(key);
        }

        foreach (var field in bound.Sort?.Fields ?? [])
            if (sort.Any(key => key.Path == field.Path.Wire))
                index.Serves.Add(new JsonObject { ["use"] = "sort", ["path"] = field.Path.Wire, ["direction"] = field.Ascending ? "asc" : "desc" }.With("stage", bound.CallerIndexOf(bound.Sort!)));

        foreach (var condition in ranges)
            Add(index, condition, Roles.Range);

        if (collated && index.Keys.Any(key => key.Text))
            index.Collation = compiled.Collation;
    }

    /// <summary>
    /// The sort keys an index can order the rows by: every sort member is one of the entity's own row
    /// (and, of unwound rows, the compiler orders and cuts the parents first), completed by the key.
    /// Empty where no index orders the rows, each sort member then listed as unbounded.
    /// </summary>
    private static List<Key> SortKeys(BoundPipeline bound, CompiledQuery compiled, JsonArray unbounded)
    {
        var fields = bound.Sort?.Fields ?? [];
        var unwinds = bound.Stages.Any(stage => stage is BoundStage.Unwind);
        var leading = fields.TakeWhile(field => !IsKey(field) && IsOfEntity(field.Path)).ToList();
        var key = fields.Skip(leading.Count).FirstOrDefault() is { } next && IsKey(next) ? next : null;
        var rest = fields.Skip(leading.Count + (key is null ? 0 : 1)).ToList();
        // Entity rows: every member has to be the row's own. Unwound rows: what follows the key orders the rows of one parent.
        var served = unwinds ? compiled.Unwind is { ParentsFirst: true } : rest.Count == 0;
        string? reason = served ? null : unwinds ? "afterUnwind" : "joined";

        if (served && leading.FirstOrDefault(field => field.Path.CollectionAncestors > 0 || field.Path.Addon is not null) is { } unfit)
            (served, reason) = (false, unfit.Path.Addon is not null ? "addon" : "collection");

        if (!served)
        {
            foreach (var field in fields)
                unbounded.Add(new JsonObject { ["use"] = "sort", ["path"] = field.Path.Wire, ["reason"] = reason }.With("stage", bound.CallerIndexOf(bound.Sort!)));

            return [];
        }

        var keys = leading.Select(field => new Key(field.Path.Storage!, field.Ascending ? 1 : -1, Roles.Sort, field.Path.Wire, IsText(field.Path))).ToList();

        keys.Add(new Key(KeyStorage, key is { Ascending: false } ? -1 : 1, Roles.Sort, key?.Path.Wire, false));

        return keys.DistinctBy(each => each.Field).ToList();
    }

    /// <summary>
    /// A group: equality and range bound the rows it reads; where it reads nothing but members of the
    /// entity's own row, its keys and what its aggregates and the conditions read follow, so the group
    /// is computed from the index. A text the collation bounds needs the collation, which keeps the
    /// documents read: bounding the rows comes first.
    /// </summary>
    private static void GroupKeys(Advised index, BoundPipeline bound, int at, List<Condition> conditions, bool collated, BsonDocument? collation)
    {
        var group = (BoundStage.Group)bound.Stages[at];
        var read = new List<(ResolvedPath Path, string Role, JsonObject Serve)>();
        var stage = bound.CallerIndexOf(at);

        foreach (var key in group.Keys)
            if ((key.Trunc?.Path ?? key.Path) is { } path)
                read.Add((path, Roles.Group, new JsonObject { ["use"] = "group", ["path"] = path.Wire, ["as"] = key.As }.With("stage", stage)));

        foreach (var field in group.Fields)
            foreach (var path in Paths(field.Argument))
                read.Add((path, Roles.Covered, new JsonObject { ["use"] = "aggregate", ["path"] = path.Wire, ["as"] = field.As }.With("stage", stage)));

        Bounding(index, conditions, collated, textBounds: true);

        var covers = Coverable(bound, at, conditions) && read.All(each => IsOfEntity(each.Path) && each.Path.CollectionAncestors == 0 && each.Path.Addon is null);

        if (collated && index.Keys.Any(key => key.Text))
        {
            index.Collation = collation;
            covers = false;
        }

        if (!covers)
            return;

        foreach (var (path, role, serve) in read)
        {
            if (!index.Keys.Any(key => key.Field == path.Storage))
                index.Keys.Add(new Key(path.Storage!, 1, role, path.Wire, IsText(path)));

            index.Serves.Add(serve);
        }

        Covered(index, conditions);
        index.Covers = true;

        if (collated)
            index.Note = "Built without a collation: the group reads the stored values off the index's keys and folds them itself; from an index built with a collation the database reads every document.";
    }

    /// <summary>
    /// A list of values: equality and range bound the rows read, then the path, then what else the
    /// conditions read, without a collation, so no document is read. A text the request compares under
    /// the collation is held, not bounded: an index built with the collation would bound it and make
    /// the database read every document for the values.
    /// </summary>
    private static void ValuesKeys(Advised index, BoundPipeline bound, int at, List<Condition> conditions, bool collated, BsonDocument? collation)
    {
        var values = (BoundStage.Values)bound.Stages[at];
        var path = values.Path;
        var own = IsOfEntity(path) && values.Collections.Count == 0 && path.CollectionAncestors == 0 && path.Addon is null && Coverable(bound, at, conditions);

        if (!own)
        {
            Bounding(index, conditions, collated, textBounds: true);

            if (collated && index.Keys.Any(key => key.Text))
                index.Collation = collation;

            index.Note = values.Collections.Count > 0 || path.CollectionAncestors > 0
                ? "The path lies under a collection: an index over the members of an array never stands for the documents, so the values are read off them, at most the stage's bound of rows."
                : "The values are read off the documents: the stage reads more than the members of the entity's own row.";

            return;
        }

        Bounding(index, conditions, collated, textBounds: !collated);

        if (!index.Keys.Any(key => key.Field == path.Storage))
            index.Keys.Add(new Key(path.Storage!, 1, Roles.Values, path.Wire, IsText(path)));

        index.Serves.Add(new JsonObject { ["use"] = "values", ["path"] = path.Wire }.With("stage", bound.CallerIndexOf(at)));
        Covered(index, conditions);
        index.Covers = true;

        if (collated)
            index.Note = "Built without a collation: the values are read off the index's keys, at most the stage's bound of them and no document. An index built with a collation holds the sort keys of its texts, and the database then reads every document, whatever the path holds; a search by prefix runs on the values, after them.";
    }

    /// <summary>The keys that bound the rows: equality, then range. A text bounds only where <paramref name="textBounds"/>.</summary>
    private static void Bounding(Advised index, List<Condition> conditions, bool collated, bool textBounds)
    {
        foreach (var condition in Distinct(conditions, Bound.Equality).Where(condition => textBounds || !condition.Text))
            Add(index, condition, Roles.Equality);

        foreach (var condition in Distinct(conditions, Bound.Range).Where(condition => textBounds || !condition.Text))
            Add(index, condition, Roles.Range);
    }

    /// <summary>What the conditions read beside the bounding keys, held so that no document is read for it.</summary>
    private static void Covered(Advised index, List<Condition> conditions)
    {
        foreach (var condition in conditions.Where(condition => condition.Keyable))
            Add(index, condition, Roles.Covered);
    }

    /// <summary>
    /// Whether the rows up to the stage at <paramref name="at"/> are read off an index alone: every
    /// condition reads a member of the entity's own row that is no element of a collection, and no
    /// unwind and no join stands before it.
    /// </summary>
    private static bool Coverable(BoundPipeline bound, int at, List<Condition> conditions)
    {
        for (var position = 0; position < at; position++)
        {
            switch (bound.Stages[position])
            {
                case BoundStage.Unwind:
                    return false;

                case BoundStage.Lookup lookup when MongoCompiler.JoinRuns(bound, position, lookup.As):
                case BoundStage.Resolve resolve when MongoCompiler.JoinRuns(bound, position, resolve.As):
                    return false;
            }
        }

        return conditions.All(condition => condition is { Keyable: true, Multikey: false, Via: null });
    }

    private static void Add(Advised index, Condition condition, string role)
    {
        if (!index.Keys.Any(key => key.Field == condition.Field))
            index.Keys.Add(new Key(condition.Field!, 1, role, condition.Path, condition.Text && role != Roles.Covered));

        if (!index.ListedConditions.Add(condition))
            return;

        role = index.Keys.First(key => key.Field == condition.Field).Role;

        index.Serves.Add(new JsonObject { ["use"] = "filter", ["role"] = role, ["path"] = condition.Path, ["op"] = condition.Op }
            .With("via", condition.Via).With("filter", condition.Filter).With("stage", condition.Filter is null ? condition.Stage : null));
    }

    private static List<Condition> Distinct(List<Condition> conditions, Bound bound) =>
        conditions.Where(condition => condition.Bound == bound && condition.Keyable)
            // A member compared for equality and by a range is a point: the equality bounds it.
            .Where(condition => bound == Bound.Equality || !conditions.Any(other => other.Bound == Bound.Equality && other.Field == condition.Field))
            .ToList();

    private static JsonObject Unbounded(Condition condition) =>
        new JsonObject { ["use"] = "filter", ["path"] = condition.Path, ["op"] = condition.Op, ["reason"] = condition.Reason ?? "heldNotBounded" }
            .With("filter", condition.Filter).With("stage", condition.Filter is null ? condition.Stage : null);

    // ---- conditions -------------------------------------------------------------------------

    /// <summary>
    /// The conditions of every match before the stage at <paramref name="end"/>, each with how an index
    /// key could bound it; those after it (a condition on a group's row) are listed as unbounded.
    /// </summary>
    private static List<Condition> Conditions(BoundPipeline bound, CompiledQuery compiled, int end, JsonArray unbounded)
    {
        var conditions = new List<Condition>();
        string? collection = null;

        for (var position = 0; position < bound.Stages.Count; position++)
        {
            if (bound.Stages[position] is not BoundStage.Match match)
                continue;

            var filter = bound.FilterOf(match);
            var stage = bound.CallerIndexOf(position);

            foreach (var conjunct in Conjuncts(match.Condition))
            {
                if (position > end)
                {
                    foreach (var leaf in Leaves(conjunct))
                        unbounded.Add(Unbounded(new Condition(leaf.Path.Wire, null, leaf.Op, Bound.None, "afterGroup", filter, stage, false, false, false, null)));

                    continue;
                }

                if (conjunct is not BoundCondition.Leaf single)
                {
                    var reason = conjunct switch { BoundCondition.Or => "or", BoundCondition.Not => "not", _ => "any" };

                    // Held for a covering index where it is a member of the row; never a bound.
                    foreach (var leaf in Leaves(conjunct))
                        conditions.Add(Classify(bound, compiled, position, leaf, filter, stage) with { Bound = Bound.None, Reason = reason });

                    continue;
                }

                var condition = Classify(bound, compiled, position, single, filter, stage);

                // An index holds the elements of one collection: a second one's member is not a key of it.
                if (condition is { Multikey: true, Bound: not Bound.None })
                {
                    var top = condition.Field!.Split('.')[0];

                    if (collection is not null && collection != top)
                        condition = condition with { Bound = Bound.None, Reason = "secondCollection", Field = null };
                    else
                        collection = top;
                }

                conditions.Add(condition);
            }
        }

        return conditions;
    }

    private static Condition Classify(BoundPipeline bound, CompiledQuery compiled, int position, BoundCondition.Leaf leaf, object? filter, int? stage)
    {
        var path = leaf.Path;
        var none = new Condition(path.Wire, null, leaf.Op, Bound.None, null, filter, stage, false, false, false, null);

        if (leaf.Op is "any" or "is")
            return none with { Reason = leaf.Op == "is" ? "variant" : "any" };

        // A condition on another service's member is asked of its owner first: the keys it answers are a set on the reference.
        if (leaf.IsSemiJoin)
            return path.Root is ShapeNode.Remote { Reference: var reference } && IsOfEntity(reference) && reference.CollectionAncestors == 0
                ? none with { Field = reference.Storage, Bound = Bound.Equality, Via = reference.Wire }
                : none with { Reason = "joined" };

        string? field;
        var multikey = path.CollectionAncestors > 0;

        if (IsOfEntity(path))
        {
            field = path.Storage;

            // Contract 1 keeps an unwound collection in the row: a condition on it after its unwind reads one element.
            if (bound.Stages.Take(position).OfType<BoundStage.Unwind>().Any(unwind => unwind.Path.Storage is { } unwound && (field == unwound || field!.StartsWith(unwound + ".", StringComparison.Ordinal))))
                return none with { Reason = "afterUnwind" };
        }
        else if (path.Root is ShapeNode.Element element && path.Storage is { } under
            && bound.Stages.Take(position).OfType<BoundStage.Unwind>().FirstOrDefault(unwind => unwind.As == element.StoragePrefix) is { } unwind)
        {
            // What is asked of the element after the unwind is asked of the collection before it where the compiler placed it there.
            if (compiled.Unwind is not { } placed || !placed.Prefiltered.Contains(path.Wire, StringComparer.Ordinal) || !IsOfEntity(unwind.Path) || under.Length <= element.StoragePrefix.Length)
                return none with { Reason = "afterUnwind" };

            field = unwind.Path.Storage + under[element.StoragePrefix.Length..];
            multikey = true;
        }
        else
        {
            return none with { Reason = path.Root is ShapeNode.Element ? "afterUnwind" : "joined" };
        }

        if (field is null)
            return none with { Reason = "notStored" };

        if (path.Addon is not null)
            return none with { Reason = "addon" };

        var text = IsText(path);
        var folds = leaf.IgnoreCase == true;
        var collated = bound.Collated;
        var held = none with { Field = field, Text = text, Multikey = multikey };

        if (leaf.Operand is BoundOperand.Tolerant)
            return held with { Reason = "decimalText" };

        // A text compares by a plain operator, which an index bounds, only where the comparison and the
        // aggregate agree: folded under the collation, or exact without one. Otherwise it is a pattern.
        var plain = !text || folds == collated;

        return leaf.Op switch
        {
            "eq" or "in" when plain => held with { Bound = Bound.Equality, Single = leaf.Op == "eq" },
            "eq" or "in" => held with { Reason = folds ? "foldsWithoutCollation" : "exactUnderCollation" },
            "gt" or "gte" or "lt" or "lte" => held with { Bound = Bound.Range },
            "startsWith" when text && folds && collated => held with { Bound = Bound.Range },
            "startsWith" when text && !folds && !collated => held with { Bound = Bound.Range },
            "startsWith" => held with { Reason = folds ? "foldsWithoutCollation" : "exactUnderCollation" },
            "neq" or "nin" => held with { Reason = "negation" },
            "exists" => held with { Reason = "exists" },
            "contains" or "endsWith" or "regex" => held with { Reason = "pattern" },
            "is" => held with { Reason = "variant" },
            _ => held with { Reason = "notBounded" },
        };
    }

    private static IEnumerable<BoundCondition> Conjuncts(BoundCondition condition) =>
        condition is BoundCondition.And and ? and.Conditions.SelectMany(Conjuncts) : [condition];

    private static IEnumerable<BoundCondition.Leaf> Leaves(BoundCondition condition) => condition switch
    {
        BoundCondition.And and => and.Conditions.SelectMany(Leaves),
        BoundCondition.Or or => or.Conditions.SelectMany(Leaves),
        BoundCondition.Not not => Leaves(not.Condition),
        BoundCondition.Leaf leaf => [leaf],
        // The members of an element are read relative to it: a key of their own they are not here.
        BoundCondition.Any any => [new BoundCondition.Leaf(any.Path, "any", new BoundOperand.Null(), null, false)],
        _ => [],
    };

    private static IEnumerable<ResolvedPath> Paths(BoundExpression? expression) => expression switch
    {
        BoundExpression.Path path => [path.Resolved],
        BoundExpression.Arithmetic arithmetic => arithmetic.Operands.SelectMany(Paths),
        _ => [],
    };

    // ---- joins ------------------------------------------------------------------------------

    /// <summary>
    /// The index of each join the aggregate runs onto a member that is not the key (the key has its
    /// own): the joined collection's organisation member, the member joined on, and of a lookup the
    /// order of its children.
    /// </summary>
    private static IEnumerable<Advised> Joins(BoundPipeline bound, CompiledQuery compiled)
    {
        for (var position = 0; position < bound.Stages.Count; position++)
        {
            Advised? index = null;

            switch (bound.Stages[position])
            {
                case BoundStage.Lookup lookup when lookup.ChildKeyStorage != KeyStorage && MongoCompiler.JoinRuns(bound, position, lookup.As):
                    index = new Advised { Collection = lookup.From.Collection, Entity = lookup.From.Id, Purpose = Purposes.Join };
                    index.Keys.Add(new Key(lookup.ChildScope.OrganisationStorage, 1, Roles.Scope, null, false));
                    index.Keys.Add(new Key(lookup.ChildKeyStorage, 1, Roles.Join, lookup.ChildReference.Wire, IsText(lookup.ChildReference)));

                    if (lookup.ChildSort is { Count: > 0 } sort && sort.All(field => field.Path.Storage is not null && field.Path.CollectionAncestors == 0))
                    {
                        foreach (var field in sort)
                            if (!index.Keys.Any(key => key.Field == field.Path.Storage))
                                index.Keys.Add(new Key(field.Path.Storage!, field.Ascending ? 1 : -1, Roles.Sort, field.Path.Wire, IsText(field.Path)));

                        if (!index.Keys.Any(key => key.Field == KeyStorage))
                            index.Keys.Add(new Key(KeyStorage, 1, Roles.Sort, null, false));
                    }

                    index.Serves.Add(new JsonObject { ["use"] = "join", ["alias"] = lookup.As, ["path"] = lookup.ChildReference.Wire }.With("stage", bound.CallerIndexOf(position)));
                    break;

                case BoundStage.Resolve { IsRemote: false, Executor: ResolveExecutor.Inline, Target: { } target, TargetFieldStorage: { } field } resolve
                    when field != KeyStorage && MongoCompiler.JoinRuns(bound, position, resolve.As):
                    index = new Advised { Collection = target.Collection, Entity = target.Id, Purpose = Purposes.Join };

                    if (resolve.TargetScope is { } scope)
                        index.Keys.Add(new Key(scope.OrganisationStorage, 1, Roles.Scope, null, false));

                    index.Keys.Add(new Key(field, 1, Roles.Join, resolve.TargetField, IsText(resolve.Reference)));
                    index.Serves.Add(new JsonObject { ["use"] = "join", ["alias"] = resolve.As, ["path"] = resolve.Reference.Wire }.With("stage", bound.CallerIndexOf(position)));
                    break;
            }

            if (index is null)
                continue;

            // The join compares a text under the aggregate's collation: only an index built with it is used.
            if (bound.Collated && index.Keys.Any(key => key.Text))
                index.Collation = compiled.Collation;

            yield return index;
        }
    }

    // ---- existing indexes -------------------------------------------------------------------

    private static IndexDef? Parse(BsonDocument index)
    {
        if (!index.TryGetValue("key", out var key) || key is not BsonDocument keys)
            return null;

        // An index over part of the rows, one that skips rows without the member, and one the planner is told to ignore serve no request for certain.
        if (index.Contains("partialFilterExpression") || index.GetValue("sparse", false).ToBoolean() || index.GetValue("hidden", false).ToBoolean())
            return null;

        var list = new List<(string, int)>();

        foreach (var element in keys)
        {
            // A text, hashed, geo or wildcard key orders nothing.
            if (!element.Value.IsNumeric || element.Name.Contains("$**", StringComparison.Ordinal))
                return null;

            list.Add((element.Name, Math.Sign(element.Value.ToDouble())));
        }

        (string, int)? collation = index.TryGetValue("collation", out var declared) && declared is BsonDocument document && document.GetValue("locale", "simple").ToString() is { } locale && locale != "simple"
            ? (locale, document.GetValue("strength", 3).ToInt32())
            : null;

        return new IndexDef(index.TryGetValue("name", out var name) ? name.ToString() ?? "" : "", list, collation);
    }

    private sealed record Match(IndexDef Index, bool Served, List<string> Lacks, int Score, int Reached, bool Scoped);

    /// <summary>
    /// How far an existing index goes for an advised one: the keys it leads with among the advised
    /// equality keys, whether the sort keys follow them in the request's directions (or all reversed),
    /// whether the first range key follows where nothing is sorted, whether it holds every key of an
    /// index that is to stand for the documents, and whether its collation is the one the texts need.
    /// </summary>
    private static Match Compare(Advised advised, IndexDef existing)
    {
        var wanted = advised.Collation is { } collation ? ((string, int)?)(collation.GetValue("locale", "").ToString()!, collation.GetValue("strength", 3).ToInt32()) : null;
        var collationFits = existing.Collation == wanted;
        // A text is bounded and ordered only by an index of the request's collation; any other key by any index.
        bool Usable(Key key) => !key.Text || collationFits;

        var equality = advised.Keys.Where(key => key.Role is Roles.Scope or Roles.Equality or Roles.Join).ToList();
        var sort = advised.Keys.Where(key => key.Role == Roles.Sort).ToList();
        var range = advised.Keys.FirstOrDefault(key => key.Role == Roles.Range);
        var position = 0;
        var reached = new HashSet<string>(StringComparer.Ordinal);

        while (position < existing.Keys.Count && equality.FirstOrDefault(key => key.Field == existing.Keys[position].Field) is { } key && Usable(key) && reached.Add(key.Field))
            position++;

        // A join is served by an index that reaches the member joined on; its organisation member narrows no further.
        var allEqual = advised.Purpose == Purposes.Join
            ? equality.Where(key => key.Role == Roles.Join).All(key => reached.Contains(key.Field))
            : reached.Count == equality.Count;
        var sorted = sort.Count == 0 || (sort.All(Usable) && Follows(existing, position, sort));
        var ranged = range is null || sort.Count > 0 || (allEqual && Usable(range) && position < existing.Keys.Count && existing.Keys[position].Field == range.Field);
        // An index that stands for the documents holds the stored values: it has no collation, and every key.
        var covering = !advised.Covers || (existing.Collation is null && advised.Keys.All(key => existing.Keys.Any(held => held.Field == key.Field)));
        var lacks = new List<string>();

        if (!allEqual)
            lacks.Add("equality");

        if (!sorted)
            lacks.Add("sort");

        if (!ranged)
            lacks.Add("range");

        if (!covering)
            lacks.Add("covering");

        if (!collationFits && (advised.Keys.Any(key => key.Text) || (advised.Covers && existing.Collation is not null)))
            lacks.Add("collation");

        var score = (reached.Count * 16) + (sort.Count > 0 && sorted ? 8 : 0) + (range is not null && ranged ? 4 : 0) + (advised.Covers && covering ? 2 : 0) + (collationFits ? 1 : 0);

        var scope = advised.Keys.FirstOrDefault(key => key.Role == Roles.Scope);

        return new Match(existing, lacks.Count == 0, lacks, score, reached.Count + (sort.Count > 0 && sorted ? 1 : 0), scope is null || reached.Contains(scope.Field));
    }

    /// <summary>Whether the keys of <paramref name="index"/> from <paramref name="start"/> are the sort keys in order, in their directions or all reversed.</summary>
    private static bool Follows(IndexDef index, int start, List<Key> sort)
    {
        if (start + sort.Count > index.Keys.Count)
            return false;

        int? flip = null;

        for (var offset = 0; offset < sort.Count; offset++)
        {
            var held = index.Keys[start + offset];

            if (held.Field != sort[offset].Field)
                return false;

            var sign = held.Direction * sort[offset].Direction;

            flip ??= sign;

            if (sign != flip)
                return false;
        }

        return true;
    }

    // ---- the wire form ----------------------------------------------------------------------

    private static JsonObject Written(Advised advised)
    {
        var key = new JsonObject();

        foreach (var each in advised.Keys)
            key[each.Field] = each.Direction;

        var written = new JsonObject
        {
            ["id"] = advised.Id,
            ["for"] = advised.Purpose,
            ["collection"] = advised.Collection,
            ["entity"] = advised.Entity,
            ["key"] = key,
            ["keys"] = new JsonArray(advised.Keys.Select(each => (JsonNode)new JsonObject { ["field"] = each.Field, ["direction"] = each.Direction, ["role"] = each.Role }.With("path", each.Path)).ToArray()),
        };

        if (advised.Collation is { } collation)
            written["collation"] = new JsonObject { ["locale"] = collation.GetValue("locale", "").ToString(), ["strength"] = collation.GetValue("strength", 3).ToInt32() };

        if (advised.Multikey)
            written["multikey"] = true;

        if (advised.Covers)
            written["covers"] = true;

        var matches = advised.Listed?.Select(Parse).Where(index => index is not null).Select(index => Compare(advised, index!)).ToList();
        var served = matches?.Where(match => match.Served).OrderBy(match => match.Index.Keys.Count).FirstOrDefault();
        var best = served ?? matches?.Where(match => match.Reached > 0).OrderByDescending(match => match.Score).ThenBy(match => match.Index.Keys.Count).FirstOrDefault();

        written["existing"] = matches is null ? Existing.Unknown : served is not null ? Existing.Served : best is not null ? Existing.Partial : Existing.Missing;
        written["used"] = matches is null ? null : served is not null;

        if (best is not null)
            written["index"] = best.Index.Name;

        if (best is { Served: false })
            written["lacks"] = new JsonArray(best.Lacks.Select(lack => (JsonNode)lack).ToArray());

        if (matches is not null && served is null && Without(advised, best) is { Count: > 0 } without)
            written["without"] = new JsonArray(without.Select(each => (JsonNode)each).ToArray());

        written["serves"] = new JsonArray(advised.Serves.Select(serve => (JsonNode)serve.DeepClone()).ToArray());
        written["command"] = Command(advised, key);

        if (advised.Note is not null)
            written["note"] = advised.Note;

        return written;
    }

    /// <summary>What the request does while no index serves the advised one: only what follows from the index list.</summary>
    private static List<string> Without(Advised advised, Match? best)
    {
        var without = new List<string>();
        var lacks = best?.Lacks ?? ["equality", "sort", "range", "covering"];
        var bounded = advised.Keys.Any(key => key.Role is Roles.Equality or Roles.Range);

        if (advised.Purpose == Purposes.Join)
            return ["scansPerRow"];

        if (advised.Keys.Any(key => key.Role == Roles.Sort) && lacks.Contains("sort"))
            without.Add("sortsInMemory");

        // No index leads with the organisation: every row of the collection is a candidate.
        if (best is not { Scoped: true })
            without.Add("scansCollection");
        else if (bounded && (lacks.Contains("equality") || lacks.Contains("range")))
            without.Add("scansOrganisation");

        if (advised.Covers && lacks.Contains("covering"))
            without.Add("readsDocuments");

        return without;
    }

    /// <summary>The shell line that creates the index, to copy as it is; the engine never runs it.</summary>
    private static string Command(Advised advised, JsonObject key)
    {
        var keys = Shell(key);
        var options = advised.Collation is { } collation
            ? $", {{ collation: {{ locale: {Quoted(collation.GetValue("locale", "").ToString()!)}, strength: {collation.GetValue("strength", 3).ToInt32()} }} }}"
            : "";

        return $"db.getCollection({Quoted(advised.Collection)}).createIndex({keys}{options})";
    }

    private static string Quoted(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // ---- the notes ------------------------------------------------------------------------------

    /// <summary>
    /// One <c>INDEX_ADVICE</c> note per advised index: the sentence a reader is shown, at the stage the
    /// index is for (the group, the values, the join) and of rows at the part of the request it serves
    /// first (a filter entry, else a stage, with the path as written),
    /// and the index's <c>id</c> among the advisory's (params <c>id</c>, <c>for</c>,
    /// <c>collection</c>, <c>existing</c>, <c>used</c>, <c>index</c>).
    /// </summary>
    public static IEnumerable<Diagnostic> NotesOf(JsonObject advisory)
    {
        ArgumentNullException.ThrowIfNull(advisory);

        foreach (var index in advisory["indexes"]!.AsArray().Select(node => node!.AsObject()))
        {
            var collection = index["collection"]!.GetValue<string>();
            var existing = index["existing"]!.GetValue<string>();
            var name = index["index"]?.GetValue<string>();
            var purpose = index["for"]!.GetValue<string>();
            var serves = index["serves"]!.AsArray().Select(node => node!.AsObject()).ToList();
            // At the stage the index is for (the group, the values, the join); of rows, at what it serves first.
            var first = serves.FirstOrDefault(serve => serve["use"]!.GetValue<string>() == purpose) ?? serves.FirstOrDefault();
            var alias = first?["alias"]?.GetValue<string>();
            var what = purpose switch
            {
                Purposes.Group => "the rows the group reads",
                Purposes.Values => "the list of values",
                Purposes.Join => $"the join '{alias}'",
                _ => "the request's filter and order",
            };
            var definition = Shell(index["key"]!.AsObject())
                + (index["collation"] is JsonObject collation ? $" with the collation {collation["locale"]!.GetValue<string>()}/{collation["strength"]!.GetValue<int>()}" : "");
            var without = index["without"] is JsonArray consequences && consequences.Count > 0
                ? " Without it the request " + string.Join(", ", consequences.Select(each => Consequence(each!.GetValue<string>()))) + "."
                : "";
            var message = existing switch
            {
                Existing.Served => $"The index '{name}' of '{collection}' serves {what}.",
                Existing.Partial => $"The index '{name}' of '{collection}' serves {what} in part (it lacks: {string.Join(", ", index["lacks"]!.AsArray().Select(lack => lack!.GetValue<string>()))}); {definition} serves all of it.{without}",
                Existing.Missing => $"No index of '{collection}' serves {what}; {definition} would.{without}",
                _ => $"Whether an index of '{collection}' serves {what} is not known, this host read no index list of it; the index that serves it is {definition}.",
            };

            yield return new Diagnostic
            {
                Code = Notes.IndexAdvice,
                Message = message,
                Stage = first?["stage"]?.GetValue<int>(),
                Filter = first?["filter"] is JsonValue label ? label.TryGetValue<int>(out var position) ? position : label.GetValue<string>() : null,
                Path = first?["path"]?.GetValue<string>(),
                Params = new Dictionary<string, object?>
                {
                    ["id"] = index["id"]!.GetValue<string>(),
                    ["for"] = purpose,
                    ["collection"] = collection,
                    ["existing"] = existing,
                    ["used"] = index["used"] is JsonValue used && used.TryGetValue<bool>(out var flag) ? flag : null,
                    ["index"] = name,
                },
            };
        }
    }

    private static string Consequence(string code) => code switch
    {
        "sortsInMemory" => "orders its rows in memory",
        "scansOrganisation" => "reads the organisation's rows to find those that match",
        "scansCollection" => "reads the collection's rows, other organisations' among them",
        "scansPerRow" => "reads every row of the joined collection once for every row that joins",
        "readsDocuments" => "reads the documents where the index's keys would do",
        _ => code,
    };

    /// <summary>A key document as the shell writes it.</summary>
    private static string Shell(JsonObject key) =>
        "{ " + string.Join(", ", key.Select(pair => Quoted(pair.Key) + ": " + pair.Value!.GetValue<int>())) + " }";

    // ---- helpers ----------------------------------------------------------------------------

    private static int IndexOf(IReadOnlyList<BoundStage> stages, Func<BoundStage, bool> test)
    {
        for (var position = 0; position < stages.Count; position++)
            if (test(stages[position]))
                return position;

        return -1;
    }

    private static bool IsKey(BoundSortField field) => field.Path.Storage == KeyStorage && field.Path.Root.StoragePrefix.Length == 0;

    private static bool IsOfEntity(ResolvedPath path) => path.Root is ShapeNode.Entity { StoragePrefix.Length: 0 } && path.Storage is not null;

    /// <summary>Whether the member is stored as a text, which the aggregate's collation compares and orders.</summary>
    private static bool IsText(ResolvedPath path) =>
        path.Leaf?.Representation.BsonType == BsonType.String
        || (path.LeafKind == Kind.String && path.Leaf?.Representation.BsonType != BsonType.Int32);

    private static JsonObject With(this JsonObject node, string name, object? value)
    {
        switch (value)
        {
            case null:
                break;

            case int number:
                node[name] = number;
                break;

            default:
                node[name] = value.ToString();
                break;
        }

        return node;
    }
}
