namespace OxQL.Core.Binding;

/// <summary>What a stage reads a path for (improvement plan §3.S): the explicit use the binder records at each read site.</summary>
public enum ReadUse
{
    /// <summary>A condition of a <c>match</c>, an <c>any</c>'s collection and its inner paths included.</summary>
    Match,

    /// <summary>A sort entry.</summary>
    Sort,

    /// <summary>A path a projection names.</summary>
    Project,

    /// <summary>The collection an <c>unwind</c> unwinds.</summary>
    Unwind,

    /// <summary>A group key, or the date a <c>dateTrunc</c> key truncates.</summary>
    GroupKey,

    /// <summary>A path in an aggregate's argument.</summary>
    Aggregate,

    /// <summary>The reference a <c>resolve</c> follows.</summary>
    ResolveKey,

    /// <summary>The sibling member, or the object whose variant, picks the case of a reference: the one read a caller cannot see in the query text.</summary>
    CaseCondition,

    /// <summary>The parent key a <c>lookup</c> joins on.</summary>
    LookupOn,
}

/// <summary>
/// One read of the ledger: the caller's stage, the path as the row has it (an <c>any</c>'s inner path
/// prefixed by its collection), what it is read for, and where it loads from. <paramref name="Alias"/>
/// is the join alias whose rows hold the path (a resolve's or lookup's <c>as</c>, a <c>parentAs</c>, an
/// alias a continued stage added), null for a path of the entity row; a path under an element or an
/// unwound copy of a join's rows names that join. <paramref name="Relative"/> is the path below the
/// alias as the join's target has it (through the unwound collection for an element), empty for the
/// alias itself, null for a path of the entity row.
/// </summary>
public sealed record PathRead(int Stage, string Path, ReadUse Use, string? Alias, string? Relative)
{
    /// <summary>
    /// The read is of the variant of the object at the path, not of its members: a join keeps an
    /// object's discriminator with any member it loads below it, so the read loads nothing by itself.
    /// </summary>
    public bool TypeOnly { get; init; }

    /// <summary>The use as the wire spells it.</summary>
    public string UseName => WireName(Use);

    /// <summary>The wire name of a use: <c>match, sort, project, unwind, groupKey, aggregate, resolveKey, caseCondition, lookupOn</c>.</summary>
    public static string WireName(ReadUse use) => use switch
    {
        ReadUse.Match => "match",
        ReadUse.Sort => "sort",
        ReadUse.Project => "project",
        ReadUse.Unwind => "unwind",
        ReadUse.GroupKey => "groupKey",
        ReadUse.Aggregate => "aggregate",
        ReadUse.ResolveKey => "resolveKey",
        ReadUse.CaseCondition => "caseCondition",
        ReadUse.LookupOn => "lookupOn",
        _ => throw new ArgumentOutOfRangeException(nameof(use), use, null),
    };

    /// <summary>
    /// Where a resolved path loads from: the join alias its root holds rows of, and the path below
    /// it in the target's model. Null for a path of the entity row (or of a scalar or group output).
    /// </summary>
    public static (string Alias, string Relative)? Attribute(string wire, ResolvedPath resolved)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(resolved);

        switch (resolved.Root)
        {
            case ShapeNode.Entity { Join: { } join } entity:
                return (join, Below(wire, entity.StoragePrefix));

            case ShapeNode.Array { Join: { } join } array:
                return (join, Below(wire, array.StoragePrefix));

            // An element of a collection of a joined row: its members lie under the collection in the target.
            case ShapeNode.Element { Join: { } join } element:
            {
                var rest = Below(wire, element.StoragePrefix);

                return (join, rest.Length == 0 ? element.Source.Wire : element.Source.Wire + "." + rest);
            }

            case ShapeNode.Remote remote:
                return (remote.StoragePrefix, Below(wire, remote.StoragePrefix));

            case ShapeNode.Keyed keyed:
                return (keyed.StoragePrefix, Below(wire, keyed.StoragePrefix));

            default:
                return null;
        }
    }

    /// <summary>The rest of <paramref name="wire"/> below the root named <paramref name="root"/>; the whole path under the implicit root.</summary>
    private static string Below(string wire, string root) =>
        root.Length == 0 ? wire
        : wire == root ? ""
        : wire.StartsWith(root + ".", StringComparison.Ordinal) ? wire[(root.Length + 1)..]
        : wire;
}

/// <summary>
/// What a join loads and shows under one alias (improvement plan §3.S): <paramref name="Loads"/> the
/// paths fetched, relative to the alias, in ordinal order (its key, what later stages read, its output
/// set); <paramref name="Shows"/> the output set, the paths the row carries under the alias (what the
/// projection names under it; kept whole, the <c>select</c> hint with the key, else key and display);
/// <paramref name="Hint"/> the <c>select</c> as written, or null. <see cref="Loads"/> and
/// <see cref="Shows"/> are null where only the owner knows them: an alias of another host's rows kept
/// whole without a hint shows its owner's key and display members (<c>$default</c>).
/// </summary>
public sealed record JoinLoad(IReadOnlyList<string>? Loads, IReadOnlyList<string>? Shows, IReadOnlyList<string>? Hint);

/// <summary>What the final row carries of one root: whether it carries it at all, whole, or only the paths a projection names under it.</summary>
public sealed record RootOutput(bool Carried, bool Whole, IReadOnlyList<string> Projected)
{
    /// <summary>
    /// The output of the root <paramref name="name"/> at the final shape <paramref name="final"/>: not
    /// carried when a projection or a group dropped it; whole without an inclusion projection, or under
    /// one that names the root itself; else the paths the projection names under it, relative to it, in
    /// ordinal order.
    /// </summary>
    public static RootOutput Of(Shape final, string name)
    {
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(name);

        if (!final.Carries(name))
            return new RootOutput(false, false, []);

        var under = final.Included?
            .Where(path => path.StartsWith(name + ".", StringComparison.Ordinal))
            .Select(path => path[(name.Length + 1)..])
            .Order(StringComparer.Ordinal)
            .ToList() ?? [];

        var whole = final.Included is null || final.Included.Contains(name) || under.Count == 0;

        return new RootOutput(true, whole, under);
    }

    /// <summary>
    /// <paramref name="paths"/> without those another of them covers (a path at or above it), in ordinal
    /// order: a projection naming a path and one inside it is a collision, and the outer one loads both.
    /// </summary>
    public static List<string> Cover(IEnumerable<string> paths) => Written(paths).Order(StringComparer.Ordinal).ToList();

    /// <summary><see cref="Cover"/> in the order written: a hint keeps the order its caller gave it, which is the order a row's columns are listed in.</summary>
    public static List<string> Written(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var distinct = paths.Where(path => path.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        return distinct
            .Where(path => !distinct.Any(other => other.Length < path.Length && path[other.Length] == '.' && path.StartsWith(other, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>Whether <paramref name="path"/> is one of <paramref name="shown"/>, lies under one, or holds one.</summary>
    public static bool Shows(IReadOnlyList<string> shown, string path)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(path);

        foreach (var kept in shown)
            if (path == kept || Under(path, kept) || Under(kept, path))
                return true;

        return false;
    }

    private static bool Under(string path, string ancestor) =>
        path.Length > ancestor.Length && path[ancestor.Length] == '.' && path.StartsWith(ancestor, StringComparison.Ordinal);
}
