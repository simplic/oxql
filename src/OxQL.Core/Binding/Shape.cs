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

    public bool Succeeded => Path is not null;
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

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AddonDefinition>> NoAddons =
        new Dictionary<string, IReadOnlyList<AddonDefinition>>(StringComparer.Ordinal);

    private Shape(
        EntityDef entity,
        IReadOnlyDictionary<string, ShapeNode> roots,
        IReadOnlySet<string> unwound,
        bool grouped,
        IReadOnlySet<string>? included,
        IReadOnlySet<string>? excluded,
        IReadOnlyDictionary<string, IReadOnlyList<AddonDefinition>> addons)
    {
        Entity = entity;
        Roots = roots;
        Unwound = unwound;
        Grouped = grouped;
        Included = included;
        Excluded = excluded;
        Addons = addons;
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
            new Dictionary<string, ShapeNode>(StringComparer.Ordinal) { [ImplicitRoot] = new ShapeNode.Element(entity, collection.Path!, "") },
            new HashSet<string>(StringComparer.Ordinal),
            grouped: false,
            included: null,
            excluded: null,
            Addons);
    }

    /// <summary>Whether a root name is taken: by a root, or by a member of the implicit root entity.</summary>
    public bool IsTaken(string alias) =>
        Roots.ContainsKey(alias)
        || (Roots.TryGetValue(ImplicitRoot, out var root) && root is ShapeNode.Entity entity && entity.Def.Root.Member(alias) is not null);

    public Shape WithRoot(string alias, ShapeNode node)
    {
        var roots = new Dictionary<string, ShapeNode>(Roots, StringComparer.Ordinal) { [alias] = node };

        return new Shape(Entity, roots, Unwound, Grouped, Included, Excluded, Addons);
    }

    public Shape WithAddons(IReadOnlyDictionary<string, IReadOnlyList<AddonDefinition>> addons) =>
        new(Entity, Roots, Unwound, Grouped, Included, Excluded, addons);

    /// <summary>The shape after unwinding <paramref name="path"/>, optionally under an alias and with an index root.</summary>
    public Shape WithUnwound(ResolvedPath path, string rootName, string? alias, string? indexAlias)
    {
        var roots = new Dictionary<string, ShapeNode>(Roots, StringComparer.Ordinal);
        var unwound = new HashSet<string>(Unwound, StringComparer.Ordinal);

        if (path.Root is ShapeNode.Array array && path.Path is null)
        {
            // Unwinding a lookup alias: the alias becomes one target row.
            roots[rootName] = new ShapeNode.Entity(array.Target, array.StoragePrefix);
        }
        else
        {
            unwound.Add(UnwoundKey(rootName, path.Path!.Wire));
        }

        if (alias is not null && path.Path is not null)
            roots[alias] = new ShapeNode.Element(path.Entity!, path.Path, alias);

        if (indexAlias is not null)
            roots[indexAlias] = new ShapeNode.Scalar(Kind.Int, indexAlias);

        return new Shape(Entity, roots, unwound, Grouped, Included, Excluded, Addons);
    }

    /// <summary>The shape after a group: only the outputs.</summary>
    public Shape WithGroup(IEnumerable<(string Alias, Kind Kind, ShapeDef? Shape)> outputs)
    {
        var roots = new Dictionary<string, ShapeNode>(StringComparer.Ordinal);

        foreach (var (alias, kind, shape) in outputs)
            roots[alias] = new ShapeNode.GroupOutput(kind, shape, alias);

        return new Shape(Entity, roots, new HashSet<string>(StringComparer.Ordinal), grouped: true, included: null, excluded: null, Addons);
    }

    public Shape WithProjection(bool inclusion, IEnumerable<string> paths, bool includeId)
    {
        var set = new HashSet<string>(paths, StringComparer.Ordinal);

        if (inclusion && includeId && !Grouped)
            set.Add(Model.Build.WireNames.IdWire);

        return inclusion
            ? new Shape(Entity, Roots, Unwound, Grouped, set, null, Addons)
            : new Shape(Entity, Roots, Unwound, Grouped, null, set, Addons);
    }

    public static string UnwoundKey(string root, string wire) => root + "|" + wire;

    // ---- resolution ---------------------------------------------------------------------------

    /// <summary>Resolves a wire path at this shape for one usage.</summary>
    public PathResolution Resolve(string wire, PathUsage usage)
    {
        if (string.IsNullOrEmpty(wire))
            return PathResolution.Fail(Codes.InvalidPath, "A path must not be empty.");

        var segments = wire.Split('.');

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                return PathResolution.Fail(Codes.InvalidPath, $"'{wire}' has an empty segment.");

            if (segment[0] == '$')
                return PathResolution.Fail(Codes.InvalidPath, $"'{wire}' has a segment starting with '$'.");
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
                : $"'{wire}' is not a path of {Entity.Id}.");
        }

        if (!IsVisible(wire))
            return PathResolution.Fail(Codes.UnknownPath, $"'{wire}' was removed by the projection.");

        var resolution = node switch
        {
            ShapeNode.Entity entity => ResolveInEntity(entity.Def, rootName, node, rest, entity.StoragePrefix, sourcePath: null, ancestorsBase: 0, wire, usage),
            ShapeNode.Element element => ResolveElement(element, rootName, rest, wire, usage),
            ShapeNode.Array array => ResolveInEntity(array.Target, rootName, node, rest, array.StoragePrefix, sourcePath: null, ancestorsBase: 1, wire, usage),
            ShapeNode.Remote remote => ResolveRemote(remote, rest, wire, usage),
            ShapeNode.Scalar scalar => rest.Count == 0
                ? PathResolution.Ok(new ResolvedPath
                {
                    Wire = wire, Storage = scalar.StoragePrefix, Kind = scalar.Kind, CollectionAncestors = 0,
                    Filterable = true, Sortable = true, Root = node,
                })
                : PathResolution.Fail(Codes.UnknownPath, $"'{rootName}' is a scalar; '{wire}' has no members."),
            ShapeNode.GroupOutput output => rest.Count == 0
                ? PathResolution.Ok(new ResolvedPath
                {
                    Wire = wire, Storage = output.StoragePrefix, Kind = output.Kind, Shape = output.Shape, CollectionAncestors = 0,
                    Filterable = Kinds.IsScalar(output.Shape?.LeafKind ?? output.Kind),
                    Sortable = Kinds.IsScalar(output.Kind) && output.Kind != Kind.Array, Root = node,
                })
                : PathResolution.Fail(Codes.UnknownPath, $"'{rootName}' is a group output; '{wire}' has no members."),
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
            return PathResolution.Fail(Codes.ResolveNotSortable, $"'{wire}' is under a remote resolve; the owner's rows cannot order this host's page.");

        if (usage is PathUsage.Unwind or PathUsage.GroupKey or PathUsage.Aggregate)
            return PathResolution.Fail(Codes.UnknownPath, $"'{wire}' is under a remote resolve and cannot be used here.");

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
                return PathResolution.Fail(Codes.UnknownPath, $"'{wire}' is not a path of {entity.Id}.");
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
                : PathResolution.Fail(Codes.NotStored, $"'{wire}' is not stored; it is in the wire view only.");

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
        var definition = Addons.TryGetValue(entity.Id, out var definitions)
            ? definitions.FirstOrDefault(candidate => string.Equals(candidate.Path, definitionPath, StringComparison.Ordinal))
            : null;

        if (definition is null || definition.Retired || definition.Kind == AddonKind.Object)
            return PathResolution.Ok(new ResolvedPath
            {
                Wire = wire, Storage = storage, Kind = Kind.Unknown, Path = bag, Entity = entity, CollectionAncestors = ancestors,
                Filterable = false, Sortable = false, Root = node, IsAddon = true,
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
            IsAddon = true,
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

    /// <summary>Whether a wire path survives the projection at this shape.</summary>
    public bool IsVisible(string wire)
    {
        if (Included is not null)
        {
            foreach (var kept in Included)
                if (wire == kept || wire.StartsWith(kept + ".", StringComparison.Ordinal) || kept.StartsWith(wire + ".", StringComparison.Ordinal))
                    return true;

            return false;
        }

        if (Excluded is not null)
            foreach (var removed in Excluded)
                if (wire == removed || wire.StartsWith(removed + ".", StringComparison.Ordinal))
                    return false;

        return true;
    }
}
