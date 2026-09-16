using MongoDB.Bson.Serialization.Options;

namespace OxQL.Model.Build;

/// <summary>
/// Flattens an entity's type graph into its path index: every reachable wire path with its
/// storage path, collection depth and query capability. Array traversal is implicit (the
/// element's members continue the parent's path); a dictionary contributes a <c>*</c> segment.
/// </summary>
/// <remarks>
/// A type reached again on the same branch stops the walk, which is what makes cyclic graphs
/// finite; a hard depth cap guards against a graph that grows through distinct types.
/// </remarks>
internal static class PathIndexer
{
    /// <summary>The deepest path the index describes, in segments.</summary>
    public const int MaxDepth = 32;

    /// <summary>The wire segment standing for any dictionary key.</summary>
    public const string KeySegment = "*";

    public static void Index(EntityDef entity, ICollection<BuildFinding> findings)
    {
        var context = new Context(entity, findings);

        context.WalkMembers(entity.Root, wirePrefix: null, storagePrefix: "", depth: 0, ancestors: 0, [entity.Root]);

        entity.Paths = context.Paths;
        entity.PathIndex = context.Index;
    }

    private sealed class Context(EntityDef entity, ICollection<BuildFinding> findings)
    {
        public readonly List<PathDef> Paths = [];
        public readonly Dictionary<string, PathDef> Index = new(StringComparer.Ordinal);

        public void WalkMembers(TypeDef type, string? wirePrefix, string? storagePrefix, int depth, int ancestors, HashSet<TypeDef> branch)
        {
            foreach (var member in type.Members)
            {
                var wire = Join(wirePrefix, member.WireName);
                var storage = storagePrefix is null || !member.Stored || member.StorageName is null
                    ? null
                    : Join(storagePrefix, member.StorageName);

                var path = Add(wire, storage, member, member, depth, ancestors);

                path.IsAddonRoot = entity.Extendable && depth == 0 && member.Kind == Kind.Dictionary && wire == WireNames.AddonWire;

                Descend(member, wire, storage, member, depth, ancestors, branch);
            }
        }

        private void Descend(ShapeDef shape, string wire, string? storage, MemberDef member, int depth, int ancestors, HashSet<TypeDef> branch)
        {
            switch (shape.Kind)
            {
                case Kind.Object when shape.Type is { IsEnum: false } target:
                    if (branch.Contains(target))
                        return;

                    if (depth + 1 >= MaxDepth)
                    {
                        findings.Add(new BuildFinding(
                            BuildCodes.PathDepthExceeded,
                            $"{entity.Id}#{wire}",
                            $"The path is {MaxDepth} segments deep, so nothing below it is described."));

                        return;
                    }

                    WalkMembers(target, wire, storage, depth + 1, ancestors, [.. branch, target]);
                    return;

                case Kind.Array when shape.Of is not null:
                    Descend(shape.Of, wire, storage, member, depth, ancestors + 1, branch);
                    return;

                case Kind.Dictionary when shape.Value is not null:
                    var representation = shape.DictionaryRepresentation ?? DictionaryRepresentation.Document;
                    var keyWire = Join(wire, KeySegment);
                    var keyStorage = storage is null
                        ? null
                        : Join(storage, representation switch
                        {
                            DictionaryRepresentation.ArrayOfDocuments => "v",
                            DictionaryRepresentation.ArrayOfArrays => "1",
                            _ => KeySegment,
                        });
                    var keyAncestors = representation == DictionaryRepresentation.Document ? ancestors : ancestors + 1;

                    Add(keyWire, keyStorage, member, shape.Value, depth + 1, keyAncestors);
                    Descend(shape.Value, keyWire, keyStorage, member, depth + 1, keyAncestors, branch);
                    return;

                default:
                    return;
            }
        }

        private PathDef Add(string wire, string? storage, MemberDef member, ShapeDef shape, int depth, int ancestors)
        {
            var path = new PathDef(wire, storage, member, shape, depth, ancestors)
            {
                Filterable = storage is not null && Kinds.IsScalar(shape.LeafKind),
            };

            path.Sortable = path.Filterable && ancestors == 0 && shape.Kind != Kind.Array;

            Paths.Add(path);
            Index[wire] = path;

            return path;
        }

        private static string Join(string? prefix, string segment) =>
            string.IsNullOrEmpty(prefix) ? segment : prefix + "." + segment;
    }
}
