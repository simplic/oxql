using OxQL.Model;

namespace OxQL.Tests.Model;

/// <summary>
/// The facts of one path the two builders must agree on. Representation is not among them:
/// the document builder knows only the per-kind defaults, so it is asserted by the registry
/// tests instead.
/// </summary>
internal sealed record PathSnapshot(
    string Wire,
    string? Storage,
    string Kind,
    string LeafKind,
    string? PoolId,
    bool Nullable,
    int Depth,
    int CollectionAncestors,
    bool Filterable,
    bool Sortable,
    bool AddonRoot,
    string? Reference)
{
    public static PathSnapshot Of(PathDef path) => new(
        path.Wire,
        path.Storage,
        Kinds.NameOf(path.Kind),
        Kinds.NameOf(path.LeafKind),
        path.Shape.Leaf.Type?.PoolId,
        path.Member.Nullable,
        path.Depth,
        path.CollectionAncestors,
        path.Filterable,
        path.Sortable,
        path.IsAddonRoot,
        path.Reference is { } reference ? $"{reference.TargetEntity}#{reference.TargetField}{(reference.IsRemote ? " (remote)" : "")}" : null);

    public static IReadOnlyList<PathSnapshot> Of(EntityDef entity) => entity.Paths.Select(Of).ToList();

    /// <summary>The paths of <paramref name="left"/> that <paramref name="right"/> lacks or describes differently, and vice versa.</summary>
    public static (IReadOnlyList<PathSnapshot> OnlyLeft, IReadOnlyList<PathSnapshot> OnlyRight) Diff(EntityDef left, EntityDef right)
    {
        var leftSet = Of(left).ToHashSet();
        var rightSet = Of(right).ToHashSet();

        return (Of(left).Where(path => !rightSet.Contains(path)).ToList(), Of(right).Where(path => !leftSet.Contains(path)).ToList());
    }
}
