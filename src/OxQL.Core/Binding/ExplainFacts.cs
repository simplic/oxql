using System.Text.Json.Nodes;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Build;

namespace OxQL.Core.Binding;

/// <summary>
/// What an explain answer says of the rows beside their shape (DG2 M7): what one row is and what
/// identifies it (<see cref="Identity"/>), the entities a row holds (<see cref="Subjects"/>), the total
/// order the page is taken in (<see cref="Order"/>), and what may be done with one path at one
/// boundary (<see cref="Can"/>). Nothing here binds a second way: the identity folds the binder's own
/// trace, the order is the bound sort as the compiler completes it, and a capability is what
/// <see cref="Shape.Resolve"/> answers for the path there, the resolution a run binds with.
/// </summary>
public static class ExplainFacts
{
    /// <summary>The aggregate functions in the order <c>can.aggregate</c> lists them.</summary>
    public static readonly IReadOnlyList<string> AggregateOrder = ["count", "countDistinct", "sum", "avg", "min", "max", "first", "last", "push"];

    /// <summary>Every value a <c>why</c> member may hold.</summary>
    public static readonly IReadOnlyList<string> Reasons =
        ["underCollection", "notScalar", "notStored", "unknownKind", "joinedAfterPage", "owner", "semiJoin", "ownerKind", "addonUndefined", "notInRow", "notApplicable"];

    // ---- identity -------------------------------------------------------------------------------

    /// <summary>
    /// What one row is after the first <paramref name="after"/> caller stages (0: at the entry), and
    /// what identifies it: <c>kind</c> <c>entity</c> (one record of <c>entity</c>, identified by
    /// <c>key</c>), <c>element</c> (one element of a collection of that record: <c>element</c>, and
    /// <c>elements</c> when several collections are unwound), <c>bucket</c> (one group:
    /// <c>bucket.keys</c>) or <c>single</c> (a group over no key). A lookup's alias that is unwound
    /// makes the row one child record, with the record it was looked up from under <c>parent</c>.
    /// The stages that did not bind are passed over, as the shapes are.
    /// </summary>
    public static JsonObject Identity(QueryRequest query, BindTrace trace, int after)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(trace);

        var entity = trace.Entry.Entity;
        var alias = "";
        JsonObject? parent = null;
        var orParent = false;
        var elements = new List<JsonObject>();
        IReadOnlyList<string>? bucket = null;

        foreach (var stage in trace.Stages)
        {
            if (stage.Index >= after)
                break;

            if (stage.Status != StageStatus.Ok || stage.Index >= query.Pipeline.Count)
                continue;

            var written = query.Pipeline[stage.Index];

            if (written.Group is { } group)
            {
                bucket = group.By.Select(key => key.As).ToList();
                continue;
            }

            // A values stage answers one row per value: a bucket whose one key is the value.
            if (written.Values is not null)
            {
                bucket = [BoundStage.Values.ValueAlias];
                continue;
            }

            if (written.Unwind is not { Path: { } path } unwind || bucket is not null)
                continue;

            // not a read: what the unwound path was where the unwind stood
            var unwound = stage.Before.Resolve(path, PathUsage.Unwind).Path;

            if (unwound is null)
                continue;

            // A lookup's array: each row is one child record from here on.
            if (unwound.Root is ShapeNode.Array children && unwound.Path is null)
            {
                parent = Record(entity, alias, parent, orParent: false);
                entity = children.Target;
                alias = unwind.As ?? path;
                orParent = unwind.PreserveNull;
                elements.Clear();
                continue;
            }

            var element = new JsonObject { ["collection"] = path };

            if (unwind.As is { } named)
            {
                element["alias"] = named;

                // not a read: whether the element has a key member of its own
                if (stage.After.Resolve(named + "." + WireNames.IdWire, PathUsage.Project).Path is { } key && Kinds.IsScalar(key.Kind))
                    element["key"] = named + "." + WireNames.IdWire;
            }

            if (unwind.IncludeIndex is { } index)
                element["index"] = index;

            if (unwound.Entity is { } holder && unwound.Path is { } member)
                element["item"] = new JsonObject { ["entity"] = holder.Id, ["path"] = member.Wire };

            element["orParent"] = unwind.PreserveNull;

            if (unwind.Flatten is not null)
                element["flatten"] = true;

            elements.Add(element);
        }

        if (bucket is not null)
            return bucket.Count == 0
                ? new JsonObject { ["kind"] = "single" }
                : new JsonObject { ["kind"] = "bucket", ["bucket"] = new JsonObject { ["keys"] = new JsonArray(bucket.Select(key => (JsonNode)key).ToArray()) } };

        var identity = Record(entity, alias, parent, orParent);

        if (elements.Count == 0)
        {
            identity["kind"] = "entity";
            return Ordered(identity);
        }

        identity["kind"] = "element";
        identity["element"] = elements[^1].DeepClone();

        if (elements.Count > 1)
            identity["elements"] = new JsonArray(elements.Select(element => (JsonNode)element).ToArray());

        return Ordered(identity);
    }

    private static JsonObject Record(EntityDef entity, string alias, JsonObject? parent, bool orParent)
    {
        var record = new JsonObject { ["entity"] = entity.Id };
        var key = new JsonArray();

        if (entity.Key is { } keyed)
            key.Add(alias.Length == 0 ? keyed.Wire : alias + "." + keyed.Wire);

        record["key"] = key;

        if (alias.Length > 0)
            record["alias"] = alias;

        if (orParent)
            record["orParent"] = true;

        if (parent is not null)
            record["parent"] = parent;

        return record;
    }

    /// <summary><c>kind</c> first: the member a reader switches on.</summary>
    private static JsonObject Ordered(JsonObject identity)
    {
        var ordered = new JsonObject { ["kind"] = identity["kind"]!.DeepClone() };

        foreach (var (name, value) in identity)
            if (name != "kind")
                ordered[name] = value?.DeepClone();

        return ordered;
    }

    // ---- subjects -------------------------------------------------------------------------------

    /// <summary>
    /// The entities a row of the final shape holds, for what acts on an entity rather than on a row:
    /// the row's own record under root <c>""</c> and every alias that holds records of an entity
    /// (<c>root</c>, <c>entity</c> or, for a union, <c>entities</c>; <c>key</c>, the row path of its
    /// key, or null where only its owner knows the key's name; <c>nullable</c>; <c>many</c> for an
    /// alias that holds several; <c>remote</c> for another service's). An alias that holds elements of
    /// an item collection is no subject; its owning row (<c>parentAs</c>) is. A grouped row is no record
    /// itself and holds those a <c>resolve</c> after the group joined to its keys.
    /// </summary>
    public static JsonArray Subjects(BoundPipeline bound)
    {
        ArgumentNullException.ThrowIfNull(bound);

        var shape = bound.FinalShape;
        var subjects = new JsonArray();

        foreach (var (name, node) in shape.Roots)
        {
            if (name != Shape.ImplicitRoot && shape.Dropped.Contains(name))
                continue;

            switch (node)
            {
                case ShapeNode.Entity entity when name == Shape.ImplicitRoot:
                    subjects.Add(Subject(name, [entity.Def.Id], entity.Def.Key?.Wire, nullable: false, many: false, remote: false));
                    break;

                case ShapeNode.Entity entity:
                    subjects.Add(Subject(name, [entity.Def.Id], Under(name, entity.Def.Key?.Wire), nullable: true, many: false, remote: false));
                    break;

                case ShapeNode.Array array:
                    subjects.Add(Subject(name, [array.Target.Id], Under(name, array.Target.Key?.Wire), nullable: false, many: true, remote: false));
                    break;

                case ShapeNode.Keyed keyed when keyed.Targets.Where(target => target.Item is null).Select(target => target.Entity).ToList() is { Count: > 0 } targets:
                    subjects.Add(Subject(name, targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).ToList(),
                        targets.Select(target => target.Key?.Wire).Distinct(StringComparer.Ordinal).ToList() is [{ } shared] ? name + "." + shared : null,
                        nullable: true, keyed.Many, remote: false));
                    break;

                case ShapeNode.Remote remote:
                    if (RemoteSubject(bound, name, remote) is { } subject)
                        subjects.Add(subject);
                    break;
            }
        }

        return subjects;
    }

    private static string? Under(string alias, string? key) => key is null ? null : alias + "." + key;

    private static JsonObject? RemoteSubject(BoundPipeline bound, string name, ShapeNode.Remote remote)
    {
        var stage = bound.Stages.OfType<BoundStage.Resolve>().FirstOrDefault(resolve => resolve.As == name || resolve.ParentAs == name);
        var many = stage?.RemoteLookup is { First: false };

        // An alias a stage continued at an owner adds: the owner binds it; the entity is the alias's own.
        if (stage is null)
            return remote.TargetOpen || remote.TargetEntity.Contains('#', StringComparison.Ordinal)
                ? null
                : Subject(name, [remote.TargetEntity], null, nullable: true, many: false, remote: true);

        var targets = (stage.Cases ?? []).SelectMany(selected => selected.Targets).Select(target => target.Declared).ToList();

        // The owning row of an item target is a record of the item's entity; its key is the owner's to name.
        if (stage.ParentAs == name)
            return targets.Count == 0 ? null : Subject(name, targets.Select(target => target.Entity).Distinct(StringComparer.Ordinal).ToList(), null, nullable: true, many, remote: true);

        var entities = targets.Where(target => target.Item is null).ToList();

        if (entities.Count == 0)
            return null;

        return Subject(name, entities.Select(target => target.Entity).Distinct(StringComparer.Ordinal).ToList(),
            entities.All(target => target.FieldIsKey) && entities.Select(target => target.Field).Distinct(StringComparer.Ordinal).ToList() is [{ } field] ? name + "." + field : null,
            nullable: true, many, remote: entities.Any(target => target.IsRemote));
    }

    private static JsonObject Subject(string root, IReadOnlyList<string> entities, string? key, bool nullable, bool many, bool remote)
    {
        var subject = new JsonObject { ["root"] = root };

        if (entities.Count == 1)
            subject["entity"] = entities[0];
        else
            subject["entities"] = new JsonArray(entities.Select(entity => (JsonNode)entity).ToArray());

        subject["key"] = key;
        subject["nullable"] = nullable;
        subject["many"] = many;

        if (remote)
            subject["remote"] = true;

        return subject;
    }

    // ---- order ----------------------------------------------------------------------------------

    /// <summary>
    /// The total order the page is taken in, as the compiler emits it: the caller's sort fields, then
    /// what completes them, each marked <c>tieBreak</c>: the entity key on a row of records, the key
    /// and the position of every unwind on unwound rows (<c>path</c> the index member where the unwind
    /// names one with <c>includeIndex</c>, else <c>positionOf</c> the collection), the group keys the
    /// sort does not name after a group. Empty where there is nothing to order (one row).
    /// </summary>
    public static JsonArray Order(BoundPipeline bound, QueryRequest query)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(query);

        var order = new JsonArray();

        // Values are ordered by their stage: with counts by the number of rows, most first, then by value.
        if (bound.Values is { } values)
        {
            if (values.Counts)
                order.Add(Entry(BoundStage.Values.CountAlias, ascending: false, tieBreak: false));

            order.Add(Entry(BoundStage.Values.ValueAlias, ascending: true, tieBreak: values.Counts));

            return order;
        }

        var fields = bound.Sort?.Fields ?? [];
        // The fields the caller wrote lead; a sort the binder supplied or completed is a tie-breaker from there.
        var written = bound.Sort is { } sort && bound.CallerIndexOf(sort) is { } at && at < query.Pipeline.Count ? query.Pipeline[at].Sort?.Count ?? 0 : 0;

        for (var index = 0; index < fields.Count; index++)
            order.Add(Entry(fields[index].Path.Wire, fields[index].Ascending, index >= written));

        if (bound.FinalShape.Grouped)
            return order;

        var key = bound.Scope.Entity.Key;

        if (!fields.Any(field => field.Path.Storage == key?.Storage && field.Path.Root.StoragePrefix.Length == 0) && key is not null)
            order.Add(Entry(key.Wire, ascending: true, tieBreak: true));

        // Unwound rows: one position per unwind, in stage order (the compiler's reserved or named indexes).
        if (bound.PagingMode == PagingMode.Offset || bound.UnwoundRows)
            foreach (var unwind in bound.Stages.OfType<BoundStage.Unwind>())
            {
                if (unwind.IncludeIndex is { } index)
                {
                    if (!fields.Any(field => field.Path.Wire == index))
                        order.Add(Entry(index, ascending: true, tieBreak: true));
                }
                else
                {
                    order.Add(new JsonObject { ["positionOf"] = unwind.Path.Wire, ["direction"] = "asc", ["tieBreak"] = true });
                }
            }

        return order;
    }

    private static JsonObject Entry(string path, bool ascending, bool tieBreak)
    {
        var entry = new JsonObject { ["path"] = path, ["direction"] = ascending ? "asc" : "desc" };

        if (tieBreak)
            entry["tieBreak"] = true;

        return entry;
    }

    // ---- what may be done with a path ------------------------------------------------------------

    /// <summary>
    /// What may be done with <paramref name="path"/> where the row has <paramref name="shape"/>
    /// (<c>can</c>), and why not where it may not (<c>why</c>, null when nothing is refused or to be
    /// said): the operators a condition on it admits (<c>filter</c>), whether a sort and a group key
    /// may name it, the aggregate functions that take it, and whether its distinct values can be
    /// asked for (<c>values</c>: a <c>values</c> stage over it binds, which is where a group by it
    /// binds and, through collections that are not unwound, where a group would bind once they were:
    /// <see cref="CanValues"/>). It is a function of the path's flags there
    /// (<see cref="ExplainTypes.FlagsAt"/>) and of what <see cref="Shape.Resolve"/> answers for an
    /// aggregate argument, so a reader that holds the flags of a member that is no result column
    /// (its type's, with the stage's rule or override set) derives the same answer (<see cref="CanOf"/>).
    /// <para>
    /// A member of another service's rows is a member of a type this host does not hold: its shape
    /// admits every operator a condition may carry, and the owner admits those of the member's kind.
    /// With <paramref name="owner"/> (the member's own flags as its owner answered them) <c>filter</c>
    /// is exactly those (<c>why.filter: "semiJoin"</c>: allowed, run as a semi-join at the owner);
    /// without them it is still every operator, and <c>why.filter</c> is <c>ownerKind</c>: the list
    /// is not narrowed to the member's kind, which only its owner's type says.
    /// </para>
    /// </summary>
    public static (JsonObject Can, JsonObject? Why) Can(Shape shape, string path, MemberFlags? owner = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(path);

        var flags = ExplainTypes.FlagsAt(shape, path);
        // not a read: what the path is at this boundary, and whether an aggregate takes it
        var facts = shape.Resolve(path, PathUsage.Project).Path ?? shape.Resolve(path, PathUsage.Match).Path;

        // Only an owner's member is narrowed by what its owner said; this host's own are answered by its own flags.
        if (facts is not { IsRemote: true })
            owner = null;

        if (owner is { } theirs)
            flags = flags with { Operators = flags.Operators & theirs.Operators };
        var aggregate = shape.Resolve(path, PathUsage.Aggregate);
        var kind = aggregate.Path?.LeafKind ?? facts?.LeafKind ?? Kind.Unknown;
        var valued = CanValues(shape, path);
        var can = CanOf(flags, aggregate.Succeeded, kind, valued);
        var reason = ReasonOf(facts);
        var why = new JsonObject();

        if (flags.Operators == 0)
            why["filter"] = reason;
        else if (facts is { IsRemote: true })
            why["filter"] = owner is null ? "ownerKind" : "semiJoin";

        if (!flags.Sortable)
            why["sort"] = reason;

        if (!flags.Groupable)
            why["group"] = reason;

        if (!valued)
            why["values"] = reason;

        // Under a collection that is not unwound only push takes the path; the others need one value per row.
        if (!aggregate.Succeeded || flags.UnderCollection > 0)
            why["aggregate"] = reason;

        return (can, why.Count == 0 ? null : why);
    }

    /// <summary>
    /// <c>can</c> of a member from its flags at a boundary: <c>filter</c> the flag set's operators,
    /// <c>sort</c> its <c>sortable</c>, <c>group</c> its <c>groupable</c>, and
    /// <c>aggregate</c> the functions its kind takes where an aggregate may read it
    /// (<paramref name="aggregable"/>: the member is in the row before the page; every function for a
    /// number, all but <c>sum</c> and <c>avg</c> for anything else, and under a collection that is not
    /// unwound <c>push</c> alone). <c>values</c> is <paramref name="values"/> where the caller knows
    /// it (<see cref="CanValues"/>), else <c>groupable</c>. A reader that holds a member's flags and
    /// its kind derives <see cref="CanValues"/> as: <c>groupable</c>, or a member of a scalar kind
    /// (the member's own kind, so no array) that is in the row before the page and has
    /// <c>underCollection</c> above 0.
    /// </summary>
    public static JsonObject CanOf(MemberFlags flags, bool aggregable, Kind kind, bool? values = null)
    {
        var operators = new JsonArray();

        for (var bit = 0; bit < ExplainTypes.OperatorOrder.Count; bit++)
            if ((flags.Operators & (1 << bit)) != 0)
                operators.Add(ExplainTypes.OperatorOrder[bit]);

        var numeric = kind is Kind.Int or Kind.Long or Kind.Double or Kind.Decimal;
        var functions = new JsonArray();

        if (aggregable && flags.UnderCollection > 0)
            functions.Add("push");
        else if (aggregable)
            foreach (var function in AggregateOrder)
                if (numeric || function is not ("sum" or "avg"))
                    functions.Add(function);

        return new JsonObject
        {
            ["filter"] = operators,
            ["sort"] = flags.Sortable,
            ["group"] = flags.Groupable,
            ["aggregate"] = functions,
            ["values"] = values ?? flags.Groupable,
        };
    }

    /// <summary>
    /// Whether a <c>values</c> stage over <paramref name="path"/> binds where the row has
    /// <paramref name="shape"/>: the path is a scalar a group key could read there, and every
    /// collection above it that is not unwound is one the stage unwinds itself (an array of the row).
    /// It is the binder's own test, read off the same resolution.
    /// </summary>
    public static bool CanValues(Shape shape, string path)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(path);

        // not a read: whether a values stage could read the path at this boundary
        if (shape.Resolve(path, PathUsage.GroupKey) is not { Succeeded: true, Path: { } key } || key.Kind == Kind.Array || !Kinds.IsScalar(key.Kind) || key.Storage is null)
            return false;

        if (key.CollectionAncestors == 0)
            return true;

        var segments = path.Split('.');
        var arrays = 0;

        for (var length = 1; length < segments.Length; length++)
            // not a read: which prefixes of the path are collections the stage unwinds
            if (shape.Resolve(string.Join('.', segments, 0, length), PathUsage.Project) is { Succeeded: true, Path: { Kind: Kind.Array, Storage: not null } })
                arrays++;

        return arrays == key.CollectionAncestors;
    }

    /// <summary>Why a path cannot be used some way, from what it is in the row: the first of the reasons that holds.</summary>
    private static string ReasonOf(ResolvedPath? facts) => facts switch
    {
        null => "notInRow",
        { Root: ShapeNode.Remote } or { IsRemote: true } => "owner",
        { Root: ShapeNode.Keyed } or { Root: ShapeNode.Outcome { InAggregate: false } } => "joinedAfterPage",
        { Path.IsAddonRoot: true, Addon: null } => "addonUndefined",
        { CollectionAncestors: > 0 } => "underCollection",
        { Storage: null } => "notStored",
        { Kind: Kind.Unknown } => "unknownKind",
        _ when !Kinds.IsScalar(facts.Kind) => "notScalar",
        _ => "notApplicable",
    };

    // ---- the snapshot copy of a joined member ----------------------------------------------------

    /// <summary>
    /// For a column <c>&lt;alias&gt;.&lt;member&gt;</c> of a join that cannot order the page: the path of the
    /// same member in a copy of the joined record the row itself embeds (a member that declares
    /// <c>snapshotOf</c> the joined entity, at or beside the member that carries the join's reference),
    /// when that path sorts. Null when there is no such copy. The engine never sorts by it itself: it
    /// is another value (the copy, as stored), which the caller may choose.
    /// </summary>
    public static string? SortVia(BoundPipeline bound, string path)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(path);

        var dot = path.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0)
            return null;

        var alias = path[..dot];
        var member = path[(dot + 1)..];

        if (bound.Stages.OfType<BoundStage.Resolve>().FirstOrDefault(resolve => resolve.As == alias) is not { Reference: { Root: ShapeNode.Entity { StoragePrefix.Length: 0 }, Entity: { } holder } reference } stage)
            return null;

        var targets = (stage.Cases ?? []).SelectMany(selected => selected.Targets).Where(target => target.Declared.Item is null)
            .Select(target => target.Declared.Entity).Append(stage.TargetEntity).ToHashSet(StringComparer.Ordinal);
        var cut = reference.Wire.LastIndexOf('.');
        var above = cut < 0 ? "" : reference.Wire[..cut];
        var candidates = new List<string>();

        if (above.Length > 0 && holder.Path(above) is { } parent && targets.Contains(CopyOf(parent) ?? ""))
            candidates.Add(above);

        foreach (var sibling in holder.ChildrenOf(above))
            if (targets.Contains(CopyOf(sibling) ?? ""))
                candidates.Add(sibling.Wire);

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            // not a read: whether the copy has the member and can order the page
            if (bound.FinalShape.Resolve(candidate + "." + member, PathUsage.Sort) is { Succeeded: true, Path.Sortable: true })
                return candidate + "." + member;

        return null;
    }

    private static string? CopyOf(PathDef path) => path.Shape.SnapshotOf ?? path.Shape.Leaf.SnapshotOf;
}
