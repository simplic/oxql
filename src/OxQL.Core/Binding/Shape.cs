using MongoDB.Bson;
using MongoDB.Bson.Serialization.Options;
using OxQL.Model;
using OxQL.Model.Addon;

namespace OxQL.Core.Binding;

/// <summary>What a path is used for; the rules differ per use.</summary>
public enum PathUsage
{
    Match,
    Sort,
    Project,
    Unwind,
    GroupKey,
    Aggregate,
    Select,
}

/// <summary>The outcome of resolving one path: the path, or a code and message.</summary>
public sealed record PathResolution(ResolvedPath? Path, string? Code, string? Message)
{
    public static PathResolution Ok(ResolvedPath path) => new(path, null, null);

    public static PathResolution Fail(string code, string message) => new(null, code, message);

    /// <summary>A failure with its reason in machine-readable form (<see cref="Params"/>).</summary>
    public static PathResolution Fail(string code, string message, string reason, params (string Name, object? Value)[] facts)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = reason };

        foreach (var (name, value) in facts)
            parameters[name] = value;

        return new(null, code, message) { Params = parameters };
    }

    /// <summary>
    /// Why the path did not resolve, for a caller that acts on it rather than showing the message:
    /// <c>reason</c> (<see cref="PathReasons"/>) and the facts of it (<c>alias</c>, <c>entity</c>,
    /// <c>targets</c>, <c>collection</c>). Null where the code says it all.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Params { get; init; }

    public bool Succeeded => Path is not null;
}

/// <summary>The <c>params.reason</c> of a path that does not resolve.</summary>
public static class PathReasons
{
    /// <summary>The path is no member of the entity, element or group output it is read on (<c>entity</c>).</summary>
    public const string NotAMember = "notAMember";

    /// <summary>A projection before the stage removed the path.</summary>
    public const string Projected = "projected";

    /// <summary>An unwind with <c>keepPath: false</c> took the collection out of the row (<c>collection</c>, <c>alias</c>).</summary>
    public const string Unwound = "unwound";

    /// <summary>The path lies under an alias joined after the page, which cannot be used this way (<c>alias</c>).</summary>
    public const string AfterPage = "afterPage";

    /// <summary>No target of the keyed alias has the path (<c>alias</c>, <c>targets</c>).</summary>
    public const string NoTarget = "noTarget";

    /// <summary>The root is a scalar or a group output and has no members (<c>alias</c>).</summary>
    public const string NoMembers = "noMembers";

    /// <summary>The member is in the wire view only.</summary>
    public const string NotStored = "notStored";
}

/// <summary>
/// The shape of a row at one point of the pipeline: the roots a path may start from, which
/// arrays are unwound, whether a group replaced the row, and what a projection kept. Immutable;
/// every stage that changes the shape returns a new one.
/// </summary>
public sealed class Shape
{
    /// <summary>The name of the implicit root.</summary>
    public const string ImplicitRoot = "";

    /// <summary>
    /// The code of a path under a poisoned alias (DESIGN §3.8). It is not a code of the catalogue:
    /// the binder drops every error carrying it, since the stage that failed to create the alias
    /// already reported why.
    /// </summary>
    internal const string PoisonedCode = "$poisoned";

    /// <summary>
    /// The most segments a path may have. A segment below an addon bag and the key of a
    /// dictionary are the caller's text and become part of a field name in storage, so the
    /// whole path is held to what the database accepts as one, with room to spare: no model
    /// path and no addon definition comes near it.
    /// </summary>
    private const int MaxSegments = 64;

    /// <summary>The longest segment a path may have, in characters; see <see cref="MaxSegments"/>.</summary>
    private const int MaxSegmentLength = 256;

    private static readonly IReadOnlySet<string> NoRoots = new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> NoUnset = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AddonDefinition>> NoAddons =
        new Dictionary<string, IReadOnlyList<AddonDefinition>>(StringComparer.Ordinal);

    private Shape(
        EntityDef entity,
        IReadOnlyDictionary<string, ShapeNode> roots,
        IReadOnlySet<string> unwound,
        bool grouped,
        IReadOnlySet<string>? included,
        IReadOnlySet<string>? excluded,
        IReadOnlyDictionary<string, IReadOnlyList<AddonDefinition>> addons,
        IReadOnlySet<string>? dropped = null,
        IReadOnlyDictionary<string, string>? unset = null)
    {
        Entity = entity;
        Roots = roots;
        Unwound = unwound;
        Grouped = grouped;
        Included = included;
        Excluded = excluded;
        Addons = addons;
        Dropped = dropped ?? NoRoots;
        Unset = unset ?? NoUnset;
    }

    /// <summary>The entity the pipeline entered.</summary>
    public EntityDef Entity { get; }

    /// <summary>The roots by name; <see cref="ImplicitRoot"/> is the entity itself until a group replaces it.</summary>
    public IReadOnlyDictionary<string, ShapeNode> Roots { get; }

    /// <summary>The unwound collections as <c>root|absoluteWire</c>.</summary>
    public IReadOnlySet<string> Unwound { get; }

    /// <summary>True after a group: the roots are the outputs.</summary>
    public bool Grouped { get; }

    /// <summary>The wire paths an inclusion projection kept, or null.</summary>
    public IReadOnlySet<string>? Included { get; }

    /// <summary>The wire paths an exclusion projection removed, or null.</summary>
    public IReadOnlySet<string>? Excluded { get; }

    /// <summary>Addon definitions per entity id, for every entity the pipeline entered.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AddonDefinition>> Addons { get; }

    /// <summary>
    /// The named roots a projection after them did not keep. They stay roots, so their name
    /// stays taken and a path under them stays "removed by the projection", but the row no
    /// longer carries them. A root a stage adds after a projection is not in here: the
    /// projection could not name it, and the row carries it.
    /// </summary>
    public IReadOnlySet<string> Dropped { get; }

    /// <summary>
    /// The collections an unwind with <c>keepPath: false</c> took out of the row, by wire path, each
    /// with the alias its element went to. A path at or under one is not in the row any more.
    /// </summary>
    public IReadOnlyDictionary<string, string> Unset { get; }

    /// <summary>Whether the row carries a named root: it is a root and no projection after it dropped it.</summary>
    public bool Carries(string name) => Roots.ContainsKey(name) && !Dropped.Contains(name);

    /// <summary>True while every row is still one entity row with its key: keyset paging applies.</summary>
    public bool IsRootShape => !Grouped && Unwound.Count == 0;

    /// <summary>The shape at the entry into an entity.</summary>
    public static Shape ForEntity(EntityDef entity, IReadOnlyDictionary<string, IReadOnlyList<AddonDefinition>>? addons = null) =>
        new(
            entity,
            new Dictionary<string, ShapeNode>(StringComparer.Ordinal) { [ImplicitRoot] = new ShapeNode.Entity(entity, "") },
            new HashSet<string>(StringComparer.Ordinal),
            grouped: false,
            included: null,
            excluded: null,
            addons ?? NoAddons);

    /// <summary>The shape inside one element of a collection of objects, paths relative to the element: the <c>any</c> form.</summary>
    public Shape ForElement(ResolvedPath collection)
    {
        var root = collection.Root;
        var entity = root switch
        {
            ShapeNode.Entity e => e.Def,
            ShapeNode.Element e => e.Def,
            ShapeNode.Array a => a.Target,
            _ => Entity,
        };

        return new Shape(
            entity,
            new Dictionary<string, ShapeNode>(StringComparer.Ordinal) { [ImplicitRoot] = new ShapeNode.Element(entity, collection.Path!, "") { Join = JoinOf(root) } },
            new HashSet<string>(StringComparer.Ordinal),
            grouped: false,
            included: null,
            excluded: null,
            Addons);
    }

    /// <summary>
    /// Whether a root name is taken: by a root, or by a member of the implicit root entity under
    /// its wire name or its storage name. An alias is written into the row in storage, where a
    /// member lives under its storage name, so either spelling would shadow the member.
    /// </summary>
    public bool IsTaken(string alias) =>
        Roots.ContainsKey(alias)
        || (Roots.TryGetValue(ImplicitRoot, out var root) && root is ShapeNode.Entity entity
            && (entity.Def.Root.Member(alias) is not null
                || entity.Def.Root.Members.Any(member => string.Equals(member.StorageName, alias, StringComparison.Ordinal))));

    public Shape WithRoot(string alias, ShapeNode node)
    {
        var roots = new Dictionary<string, ShapeNode>(Roots, StringComparer.Ordinal) { [alias] = node };

        return new Shape(Entity, roots, Unwound, Grouped, Included, Excluded, Addons, Dropped, Unset);
    }

    /// <summary>
    /// The shape after unwinding <paramref name="path"/>, optionally under an alias and with an index
    /// root. With <paramref name="keepPath"/> false (only with an alias, on a member collection) the
    /// collection leaves the row: its element is under the alias only.
    /// </summary>
    public Shape WithUnwound(ResolvedPath path, string rootName, string? alias, string? indexAlias, bool keepPath = true)
    {
        var roots = new Dictionary<string, ShapeNode>(Roots, StringComparer.Ordinal);
        var unwound = new HashSet<string>(Unwound, StringComparer.Ordinal);

        if (path.Root is ShapeNode.Array array && path.Path is null)
        {
            // Unwinding a lookup alias: the alias becomes one target row, and so does the name
            // the unwind writes it under.
            roots[rootName] = new ShapeNode.Entity(array.Target, array.StoragePrefix) { Select = array.Select, Join = array.Join };

            if (alias is not null)
                roots[alias] = new ShapeNode.Entity(array.Target, alias) { Select = array.Select, Join = array.Join };
        }
        else
        {
            unwound.Add(UnwoundKey(rootName, path.Path!.Wire));
        }

        if (alias is not null && path.Path is not null)
            roots[alias] = new ShapeNode.Element(path.Entity!, path.Path, alias) { Join = JoinOf(path.Root) };

        if (indexAlias is not null)
            roots[indexAlias] = new ShapeNode.Scalar(Kind.Int, indexAlias);

        var unset = Unset;

        if (!keepPath && alias is not null && path.Path is not null)
            unset = new Dictionary<string, string>(Unset, StringComparer.Ordinal) { [path.Wire] = alias };

        return new Shape(Entity, roots, unwound, Grouped, Included, Excluded, Addons, Dropped, unset);
    }

    /// <summary>The shape after a group: only the outputs, each rooted at its alias.</summary>
    public Shape WithGroup(IEnumerable<ShapeNode.GroupOutput> outputs)
    {
        var roots = new Dictionary<string, ShapeNode>(StringComparer.Ordinal);

        foreach (var output in outputs)
            roots[output.StoragePrefix] = output;

        return new Shape(Entity, roots, new HashSet<string>(StringComparer.Ordinal), grouped: true, included: null, excluded: null, Addons);
    }

    public Shape WithProjection(bool inclusion, IEnumerable<string> paths, bool includeId)
    {
        var set = new HashSet<string>(paths, StringComparer.Ordinal);

        if (inclusion && includeId && !Grouped)
            set.Add(Model.Build.WireNames.IdWire);

        var projected = inclusion
            ? new Shape(Entity, Roots, Unwound, Grouped, set, null, Addons, unset: Unset)
            : new Shape(Entity, Roots, Unwound, Grouped, null, set, Addons, unset: Unset);

        // A named root the projection does not keep leaves the row, as a member does.
        var dropped = new HashSet<string>(Dropped, StringComparer.Ordinal);

        foreach (var name in Roots.Keys)
            if (name != ImplicitRoot && !projected.IsVisible(name))
                dropped.Add(name);

        return new Shape(Entity, Roots, Unwound, Grouped, projected.Included, projected.Excluded, Addons, dropped, Unset);
    }

    /// <summary>
    /// This shape without what its projections removed: every member and root visible again. Explain
    /// reads a member's flags on it, and the visibility on the shape itself.
    /// </summary>
    public Shape Unprojected() =>
        Included is null && Excluded is null && Dropped.Count == 0 ? this : new Shape(Entity, Roots, Unwound, Grouped, null, null, Addons, null, Unset);

    public static string UnwoundKey(string root, string wire) => root + "|" + wire;

    /// <summary>The alias of the join whose rows a node holds: a join's own alias, an unwound copy of it, an element of one of its collections; null for the entity row.</summary>
    public static string? JoinOf(ShapeNode node) => node switch
    {
        ShapeNode.Entity entity => entity.Join,
        ShapeNode.Array array => array.Join,
        ShapeNode.Element element => element.Join,
        _ => null,
    };

    // ---- resolution ---------------------------------------------------------------------------

    /// <summary>Resolves a wire path at this shape for one usage.</summary>
    public PathResolution Resolve(string wire, PathUsage usage)
    {
        if (string.IsNullOrEmpty(wire))
            return PathResolution.Fail(Codes.InvalidPath, "A path must not be empty.");

        var segments = wire.Split('.');

        if (segments.Length > MaxSegments)
            return PathResolution.Fail(Codes.InvalidPath, $"The path has {segments.Length} segments; a path has at most {MaxSegments}.");

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                return PathResolution.Fail(Codes.InvalidPath, $"'{wire}' has an empty segment.");

            if (segment[0] == '$')
                return PathResolution.Fail(Codes.InvalidPath, $"'{wire}' has a segment starting with '$'.");

            if (segment.Length > MaxSegmentLength)
                return PathResolution.Fail(Codes.InvalidPath, $"The path has a segment of {segment.Length} characters; a segment has at most {MaxSegmentLength}.");

            if (segment.Any(char.IsControl))
                return PathResolution.Fail(Codes.InvalidPath, "The path has a control character in it.");
        }

        string rootName;
        ShapeNode node;
        ArraySegment<string> rest;

        if (Roots.TryGetValue(segments[0], out var named) && segments[0] != ImplicitRoot)
        {
            rootName = segments[0];
            node = named;
            rest = new ArraySegment<string>(segments, 1, segments.Length - 1);
        }
        else if (Roots.TryGetValue(ImplicitRoot, out var implicitRoot))
        {
            rootName = ImplicitRoot;
            node = implicitRoot;
            rest = new ArraySegment<string>(segments, 0, segments.Length);
        }
        else
        {
            return PathResolution.Fail(Codes.UnknownPath, Grouped
                ? $"'{wire}' is not an output of the group stage."
                : $"'{wire}' is not a path of {Entity.Id}.", PathReasons.NotAMember, ("entity", Entity.Id));
        }

        if (UnsetBy(wire) is { } unwound)
            return PathResolution.Fail(Codes.UnknownPath,
                $"'{wire}' left the row when '{unwound.Collection}' was unwound as '{unwound.Alias}'; read the element under '{unwound.Alias}', or set 'keepPath' to true on that unwind to keep '{unwound.Collection}'.",
                PathReasons.Unwound, ("collection", unwound.Collection), ("alias", unwound.Alias));

        if (!IsVisible(wire))
            return PathResolution.Fail(Codes.UnknownPath, $"'{wire}' was removed by the projection.", PathReasons.Projected);

        var resolution = node switch
        {
            ShapeNode.Entity entity => ResolveInEntity(entity.Def, rootName, node, rest, entity.StoragePrefix, sourcePath: null, ancestorsBase: 0, wire, usage),
            ShapeNode.Element element => ResolveElement(element, rootName, rest, wire, usage),
            ShapeNode.Array array => ResolveInEntity(array.Target, rootName, node, rest, array.StoragePrefix, sourcePath: null, ancestorsBase: 1, wire, usage),
            ShapeNode.Remote remote => ResolveRemote(remote, rest, wire, usage),
            ShapeNode.Keyed keyed => ResolveKeyed(keyed, rootName, rest, wire, usage),
            ShapeNode.Poisoned => PathResolution.Fail(PoisonedCode, $"'{wire}' lies under '{rootName}', whose stage failed."),
            ShapeNode.Scalar scalar => rest.Count == 0
                ? PathResolution.Ok(new ResolvedPath
                {
                    Wire = wire, Storage = scalar.StoragePrefix, Kind = scalar.Kind, CollectionAncestors = 0,
                    Filterable = true, Sortable = true, Root = node,
                })
                : PathResolution.Fail(Codes.UnknownPath, $"'{rootName}' is a scalar; '{wire}' has no members.", PathReasons.NoMembers, ("alias", rootName)),
            ShapeNode.GroupOutput output => rest.Count == 0
                ? PathResolution.Ok(new ResolvedPath
                {
                    Wire = wire, Storage = output.StoragePrefix, Kind = output.Kind, Shape = output.Shape, CollectionAncestors = 0,
                    Filterable = Kinds.IsScalar(output.Shape?.LeafKind ?? output.Kind),
                    Sortable = Kinds.IsScalar(output.Kind) && output.Kind != Kind.Array, Root = node,
                })
                : PathResolution.Fail(Codes.UnknownPath, $"'{rootName}' is a group output; '{wire}' has no members.", PathReasons.NoMembers, ("alias", rootName)),
            _ => PathResolution.Fail(Codes.UnknownPath, $"'{wire}' cannot be resolved."),
        };

        return resolution;
    }

    private PathResolution ResolveElement(ShapeNode.Element element, string rootName, ArraySegment<string> rest, string wire, PathUsage usage)
    {
        if (rest.Count == 0)
        {
            // The element itself.
            var elementShape = ElementShape(element.Source);
            var kind = elementShape?.Kind ?? Kind.Unknown;

            return PathResolution.Ok(new ResolvedPath
            {
                Wire = wire,
                Storage = element.StoragePrefix.Length == 0 ? null : element.StoragePrefix,
                Kind = kind,
                Shape = elementShape,
                Path = element.Source,
                Entity = element.Def,
                CollectionAncestors = 0,
                Filterable = elementShape is not null && Kinds.IsScalar(elementShape.LeafKind) && element.StoragePrefix.Length > 0,
                Sortable = elementShape is not null && Kinds.IsScalar(kind) && kind != Kind.Array && element.StoragePrefix.Length > 0,
                Root = element,
            });
        }

        // Members of the element: the entity's index has them under the source path.
        var absolute = new string[element.Source.Wire.Split('.').Length + rest.Count];
        var sourceSegments = element.Source.Wire.Split('.');

        Array.Copy(sourceSegments, absolute, sourceSegments.Length);
        Array.Copy(rest.Array!, rest.Offset, absolute, sourceSegments.Length, rest.Count);

        return ResolveInEntity(element.Def, ImplicitRoot, element, new ArraySegment<string>(absolute), element.StoragePrefix, element.Source, 0, wire, usage);
    }

    private static PathResolution ResolveRemote(ShapeNode.Remote remote, ArraySegment<string> rest, string wire, PathUsage usage)
    {
        if (usage == PathUsage.Sort)
            return PathResolution.Fail(Codes.ResolveNotSortable, $"'{wire}' is under a remote resolve; the owner's rows cannot order this host's page.", PathReasons.AfterPage, ("alias", remote.StoragePrefix));

        if (usage is PathUsage.Unwind or PathUsage.GroupKey or PathUsage.Aggregate)
            return PathResolution.Fail(Codes.UnknownPath, $"'{wire}' is under a remote resolve and cannot be used here.", PathReasons.AfterPage, ("alias", remote.StoragePrefix));

        // Only a plain remote resolve's keys are one $in on one local member; the alias of a
        // typed, item, converted or element-wise one cannot be narrowed by the owner beforehand.
        if (usage == PathUsage.Match && !remote.SemiJoinable)
            return PathResolution.Fail(Codes.ResolveNotFilterable,
                $"'{wire}' is joined after the page is taken, from typed, item, converted or element-wise targets; it cannot filter the rows.", PathReasons.AfterPage, ("alias", remote.StoragePrefix));

        return PathResolution.Ok(new ResolvedPath
        {
            Wire = wire,
            Storage = null,
            Kind = rest.Count == 0 ? Kind.Object : Kind.Unknown,
            CollectionAncestors = 0,
            Filterable = rest.Count > 0,
            Sortable = false,
            Root = remote,
            IsRemote = true,
        });
    }

    /// <summary>
    /// A path under a keyed alias: checked against the targets here, since they are local, and
    /// usable where the rows are already taken (a projection, the stages that continue a chain).
    /// A path some target has resolves on the first such target; one no target has is unknown.
    /// </summary>
    private static PathResolution ResolveKeyed(ShapeNode.Keyed keyed, string rootName, ArraySegment<string> rest, string wire, PathUsage usage)
    {
        if (usage == PathUsage.Sort)
            return PathResolution.Fail(Codes.ResolveNotSortable, $"'{wire}' is joined after the page is taken; it cannot order the page.", PathReasons.AfterPage, ("alias", rootName));

        if (usage == PathUsage.Match)
            return PathResolution.Fail(Codes.ResolveNotFilterable, $"'{wire}' is joined after the page is taken; it cannot filter the rows.", PathReasons.AfterPage, ("alias", rootName));

        if (usage is PathUsage.Unwind or PathUsage.GroupKey or PathUsage.Aggregate)
            return PathResolution.Fail(Codes.UnknownPath, $"'{wire}' is joined after the page is taken and cannot be used here.", PathReasons.AfterPage, ("alias", rootName));

        if (rest.Count == 0)
            return PathResolution.Ok(new ResolvedPath
            {
                Wire = wire, Storage = null, Kind = keyed.Many ? Kind.Array : Kind.Object, CollectionAncestors = 0,
                Filterable = false, Sortable = false, Root = keyed,
            });

        var relative = string.Join('.', (IEnumerable<string>)rest);

        foreach (var target in keyed.Targets)
        {
            var at = ForEntity(target.Entity);

            if (target.Item is { } item && at.Resolve(item.Wire, PathUsage.Unwind) is { Succeeded: true } collection)
                at = at.ForElement(collection.Path!);

            var resolution = at.Resolve(relative, PathUsage.Project);

            if (resolution.Succeeded)
                return PathResolution.Ok(resolution.Path! with
                {
                    Wire = wire, Storage = null, CollectionAncestors = 0, Filterable = false, Sortable = false, Root = keyed,
                });
        }

        return PathResolution.Fail(Codes.UnknownPath,
            $"'{wire}' is not a path of any target of '{rootName}' ({string.Join(", ", keyed.Targets.Select(target => target.Item is null ? target.Entity.Id : $"{target.Entity.Id}#{target.Item.Wire}"))}).",
            PathReasons.NoTarget, ("alias", rootName), ("targets", keyed.Targets.Select(target => target.Item is null ? target.Entity.Id : $"{target.Entity.Id}#{target.Item.Wire}").ToList()));
    }

    private PathResolution ResolveInEntity(
        EntityDef entity,
        string rootName,
        ShapeNode node,
        ArraySegment<string> segments,
        string storagePrefix,
        PathDef? sourcePath,
        int ancestorsBase,
        string wire,
        PathUsage usage)
    {
        if (segments.Count == 0)
        {
            if (node is ShapeNode.Array)
                return PathResolution.Ok(new ResolvedPath
                {
                    Wire = wire, Storage = storagePrefix, Kind = Kind.Array, Entity = entity, CollectionAncestors = 0,
                    Filterable = false, Sortable = false, Root = node,
                });

            return PathResolution.Ok(new ResolvedPath
            {
                Wire = wire, Storage = storagePrefix.Length == 0 ? null : storagePrefix, Kind = Kind.Object, Entity = entity,
                CollectionAncestors = 0, Filterable = false, Sortable = false, Root = node,
            });
        }

        var index = entity.PathIndex;
        var key = "";
        PathDef? current = null;
        var literals = new List<string>();
        var ancestors = ancestorsBase;
        var unwoundKeys = new List<string>();
        var unwoundSelf = false;

        for (var position = 0; position < segments.Count; position++)
        {
            var segment = segments[position];

            if (current is { IsAddonRoot: true })
            {
                // Everything below the bag is the definition path, verbatim.
                var definitionPath = string.Join('.', segments.Skip(position));

                return ResolveAddon(entity, node, current, definitionPath, wire, usage, storagePrefix, sourcePath, ancestors);
            }

            var candidate = key.Length == 0 ? segment : key + "." + segment;

            if (index.TryGetValue(candidate, out var next))
            {
                key = candidate;
            }
            else if (current is { Kind: Kind.Dictionary } && index.TryGetValue(key + ".*", out next))
            {
                key += ".*";
                literals.Add(segment);
            }
            else
            {
                return PathResolution.Fail(Codes.UnknownPath, $"'{wire}' is not a path of {entity.Id}.", PathReasons.NotAMember, ("entity", entity.Id));
            }

            var isLast = position == segments.Count - 1;
            var collection = IsCollection(next);
            var unwound = Unwound.Contains(UnwoundKey(rootName, next.Wire)) || (sourcePath is not null && next.Wire == sourcePath.Wire);

            if (collection && !isLast && !unwound)
                ancestors++;

            if (collection && isLast && unwound)
                unwoundSelf = true;

            if (unwound)
                unwoundKeys.Add(next.Wire);

            current = next;
        }

        var path = current!;
        var shape = unwoundSelf ? ElementShape(path) : path.Shape;
        var kind = shape?.Kind ?? Kind.Unknown;

        if (!path.Stored)
            return usage == PathUsage.Project
                ? PathResolution.Ok(Unstored(wire, path, entity, node, ancestors))
                : PathResolution.Fail(Codes.NotStored, $"'{wire}' is not stored; it is in the wire view only.", PathReasons.NotStored);

        var storage = RenderStorage(path.Storage!, literals);

        if (sourcePath is not null)
            storage = Rebase(storage, sourcePath.Storage!, storagePrefix);
        else if (storagePrefix.Length > 0)
            storage = storagePrefix + "." + storage;

        var leafKind = shape?.LeafKind ?? Kind.Unknown;
        var leaf = shape?.Leaf;
        var filterable = Kinds.IsScalar(leafKind) && leaf is not null && leaf.Representation.BsonType != BsonType.Document;
        var sortable = filterable && ancestors == 0 && kind != Kind.Array && kind != Kind.Dictionary;

        return PathResolution.Ok(new ResolvedPath
        {
            Wire = wire,
            Storage = storage,
            Kind = kind,
            Shape = shape,
            Path = path,
            Entity = entity,
            CollectionAncestors = ancestors,
            Filterable = filterable,
            Sortable = sortable,
            Root = node,
        });
    }

    private PathResolution ResolveAddon(EntityDef entity, ShapeNode node, PathDef bag, string definitionPath, string wire, PathUsage usage, string storagePrefix, PathDef? sourcePath, int ancestors)
    {
        if (definitionPath.Length == 0 || definitionPath.Split('.').Any(segment => segment.Length == 0 || segment[0] == '$'))
            return PathResolution.Fail(Codes.InvalidPath, $"'{wire}' is not a valid addon path.");

        var bagStorage = bag.Storage!;

        if (sourcePath is not null)
            bagStorage = Rebase(bagStorage, sourcePath.Storage!, storagePrefix);
        else if (storagePrefix.Length > 0)
            bagStorage = storagePrefix + "." + bagStorage;

        var storage = bagStorage + "." + definitionPath;
        // Retire-and-recreate is the ordinary life of an addon key, and both rows survive, in
        // no particular order. A live definition wins; a retired one is the answer only when
        // there is no live one, so a key the schema publishes as queryable is queryable here.
        var candidates = Addons.TryGetValue(entity.Id, out var definitions)
            ? definitions.Where(candidate => string.Equals(candidate.Path, definitionPath, StringComparison.Ordinal)).ToList()
            : [];
        var definition = candidates.FirstOrDefault(candidate => !candidate.Retired) ?? candidates.FirstOrDefault();

        if (definition is null || definition.Retired || definition.Kind == AddonKind.Object)
            return PathResolution.Ok(new ResolvedPath
            {
                Wire = wire, Storage = storage, Kind = Kind.Unknown, Path = bag, Entity = entity, CollectionAncestors = ancestors,
                Filterable = false, Sortable = false, Root = node,
            });

        var kind = AddonKinds.ToKind(definition.Kind);

        return PathResolution.Ok(new ResolvedPath
        {
            Wire = wire,
            Storage = storage,
            Kind = kind,
            Shape = null,
            Path = bag,
            Entity = entity,
            CollectionAncestors = ancestors,
            Filterable = true,
            Sortable = ancestors == 0,
            Root = node,
            Addon = definition,
        });
    }

    private static ResolvedPath Unstored(string wire, PathDef path, EntityDef entity, ShapeNode node, int ancestors) => new()
    {
        Wire = wire, Storage = null, Kind = path.Kind, Shape = path.Shape, Path = path, Entity = entity,
        CollectionAncestors = ancestors, Filterable = false, Sortable = false, Root = node,
    };

    /// <summary>The shape of one element of a collection path.</summary>
    private static ShapeDef? ElementShape(PathDef path) => path.Shape.Kind switch
    {
        Kind.Array => path.Shape.Of,
        Kind.Dictionary => path.Shape.Value,
        _ => path.Shape,
    };

    /// <summary>Whether a path is a collection in storage: an array, or a dictionary stored as one.</summary>
    public static bool IsCollection(PathDef path) =>
        path.Kind == Kind.Array
        || (path.Kind == Kind.Dictionary && path.Shape.DictionaryRepresentation is DictionaryRepresentation.ArrayOfDocuments or DictionaryRepresentation.ArrayOfArrays);

    /// <summary>Renders a storage path, replacing each <c>*</c> with the literal key the caller wrote.</summary>
    private static string RenderStorage(string storage, IReadOnlyList<string> literals)
    {
        if (literals.Count == 0)
            return storage;

        var segments = storage.Split('.');
        var next = 0;

        for (var index = 0; index < segments.Length && next < literals.Count; index++)
            if (segments[index] == "*")
                segments[index] = literals[next++];

        return string.Join('.', segments);
    }

    /// <summary>Rewrites a storage path under an unwound source onto the alias.</summary>
    private static string Rebase(string storage, string sourceStorage, string prefix)
    {
        if (storage == sourceStorage)
            return prefix;

        var remainder = storage.StartsWith(sourceStorage + ".", StringComparison.Ordinal)
            ? storage[(sourceStorage.Length + 1)..]
            : storage;

        return prefix.Length == 0 ? remainder : prefix + "." + remainder;
    }

    /// <summary>The unwound collection (and its alias) that took a wire path out of the row, or null.</summary>
    public (string Collection, string Alias)? UnsetBy(string wire)
    {
        foreach (var (collection, alias) in Unset)
            if (wire == collection || Under(wire, collection))
                return (collection, alias);

        return null;
    }

    /// <summary>Whether <paramref name="wire"/> lies below <paramref name="ancestor"/>: it starts with it and a dot.</summary>
    private static bool Under(string wire, string ancestor) =>
        wire.Length > ancestor.Length && wire[ancestor.Length] == '.' && wire.StartsWith(ancestor, StringComparison.Ordinal);

    /// <summary>
    /// Whether an exclusion projection or an unwind with <c>keepPath: false</c> took a wire path out of
    /// the row. Unlike <see cref="IsVisible"/> it says nothing of an inclusion projection, which cannot
    /// name a root a later stage adds: what such a root shows is its output set.
    /// </summary>
    public bool IsRemoved(string wire)
    {
        if (UnsetBy(wire) is not null)
            return true;

        foreach (var removed in Excluded ?? NoRoots)
            if (wire == removed || Under(wire, removed))
                return true;

        return false;
    }

    /// <summary>Whether a wire path survives the projection (and every unwind that dropped its collection) at this shape.</summary>
    public bool IsVisible(string wire)
    {
        if (UnsetBy(wire) is not null)
            return false;

        if (Included is not null)
        {
            foreach (var kept in Included)
                if (wire == kept || Under(wire, kept) || Under(kept, wire))
                    return true;

            return false;
        }

        if (Excluded is not null)
            foreach (var removed in Excluded)
                if (wire == removed || Under(wire, removed))
                    return false;

        return true;
    }
}
