using OxQL.Core.Models;

namespace OxQL.Core.Binding;

/// <summary>
/// A stage continued at the owner of a keyed or remote alias (DESIGN §3.4.1 step 3, §3.5.3): a
/// <c>resolve</c> whose path lies under the alias, or a <c>lookup</c> whose <c>on</c> names it. The
/// origin cannot run it — it has neither the owner's rows nor its model — so the stage rides in the
/// owner query the keyed fetch already sends for <paramref name="Anchor"/>, between the key match
/// and the projection, and the owner binds and runs it as its own. <paramref name="Stage"/> is the
/// stage as the caller wrote it, every variable substituted (DESIGN §3.5.5), its paths still the
/// origin's: <see cref="Continuation.For"/> rewrites them per target. <paramref name="OriginIndex"/>
/// is the caller's stage index; <paramref name="ForTarget"/> the one target of the anchor it applies
/// to, inherited from an alias it continues under (several: <see cref="Targets"/>); <paramref name="Aliases"/> the aliases it adds,
/// which the owner projects and the keyed fetch lifts to the origin row.
/// </summary>
public sealed record ContinuedStage(string Anchor, PipelineStage Stage, int OriginIndex, string? ForTarget, IReadOnlyList<string> Aliases) : BoundStage
{
    /// <summary>The stage kind: <c>resolve</c> or <c>lookup</c>.</summary>
    public string Kind => Stage.Resolve is not null ? "resolve" : "lookup";

    /// <summary>
    /// What roots the stage in the origin row: a resolve's path, a lookup's <c>on</c>; for a union join
    /// the path of its first branch (<see cref="RootFor"/> is the one of a target's branch).
    /// </summary>
    public string Root => Stage.Resolve?.Path ?? Stage.Resolve?.ByTarget?.FirstOrDefault()?.Path ?? Stage.Lookup?.On ?? "";

    /// <summary>
    /// The targets of the anchor the stage applies to when they are several and not all of them: the
    /// targets a union join has a branch for, and under its alias the same for every stage continued
    /// there. Null when <see cref="ForTarget"/> says it (one target) or the stage applies to every target.
    /// </summary>
    public IReadOnlyList<string>? Targets { get; init; }

    /// <summary>
    /// The branches of a union join (<c>byTarget</c>) this host splits: per target of the anchor the
    /// branch its owner runs as an ordinary resolve under the one alias. Null for every other stage,
    /// and for a union join under an alias a continued stage added, whose targets only the owner knows:
    /// that one travels as written and the owner splits it.
    /// </summary>
    public IReadOnlyList<ResolveBranch>? Branches { get; init; }

    /// <summary>Whether the owner of <paramref name="target"/> runs the stage.</summary>
    public bool AppliesTo(string target) =>
        (ForTarget is null || ForTarget == target) && (Targets is null || Targets.Contains(target, StringComparer.Ordinal));

    /// <summary>What roots the stage in the origin row for the rows of <paramref name="target"/>: the path of its branch, else <see cref="Root"/>.</summary>
    public string RootFor(string? target) =>
        Branches?.FirstOrDefault(branch => branch.Target == target)?.Path ?? Root;
}

/// <summary>
/// The continued stages of one keyed stage as one target's owner runs them: the rewritten stages in
/// pipeline order, the origin stage of each (same order), and every alias they add.
/// </summary>
public sealed record OwnerContinuation(IReadOnlyList<PipelineStage> Stages, IReadOnlyList<ContinuedStage> Origins, IReadOnlyList<string> Aliases)
{
    /// <summary>No continued stage.</summary>
    public static readonly OwnerContinuation None = new([], [], []);

    /// <summary>True when no stage continues for this target: the owner query is the plain one.</summary>
    public bool IsEmpty => Stages.Count == 0;
}

/// <summary>
/// The splitter of remote continuation (DESIGN §3.5.3): it collects a keyed stage's continued stages,
/// keeps those that apply to one target (<c>forTarget</c>), and rewrites their roots onto the owner's
/// row — <c>R.p</c> becomes <c>oxEl.p</c> for an item target and <c>p</c> for an entity target,
/// <c>parentAs.p</c> becomes <c>p</c>, and an <c>on</c> naming the entity row itself is dropped (the
/// owner's implicit root). A forwarded query so holds the key match and only the stages continued
/// under R, never R's own resolve or anything before it: it carries strictly fewer join stages than
/// the query it came from, and a chain of them ends by construction (DESIGN §3.5.4).
/// </summary>
public static class Continuation
{
    /// <summary>The continued stages of <paramref name="anchor"/> in pipeline order.</summary>
    public static IReadOnlyList<ContinuedStage> Of(BoundPipeline bound, BoundStage.Resolve anchor)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(anchor);

        return bound.Stages.OfType<ContinuedStage>().Where(stage => stage.Anchor == anchor.As).ToList();
    }

    /// <summary>
    /// The stages <paramref name="continued"/> as the owner of <paramref name="targetEntity"/> runs them
    /// for <paramref name="anchor"/>: those without <c>forTarget</c> or with this target's, rewritten.
    /// </summary>
    public static OwnerContinuation For(BoundStage.Resolve anchor, string targetEntity, bool itemTarget, IReadOnlyList<ContinuedStage> continued)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(continued);

        var stages = new List<PipelineStage>();
        var origins = new List<ContinuedStage>();
        var aliases = new List<string>();

        foreach (var stage in continued)
        {
            if (!stage.AppliesTo(targetEntity))
                continue;

            stages.Add(Rewrite(BranchOf(stage, targetEntity), anchor, itemTarget));
            origins.Add(stage);
            aliases.AddRange(stage.Aliases);
        }

        return stages.Count == 0 ? OwnerContinuation.None : new OwnerContinuation(stages, origins, aliases);
    }

    /// <summary>
    /// A continued stage as one target's owner is sent it, before the rewrite of its root. A union join
    /// this host splits is the branch of that target: an ordinary resolve with the branch's path and
    /// <c>elements</c> under the stage's alias, exactly what a <c>forTarget</c> stage sends. Every other
    /// stage is the stage itself.
    /// </summary>
    public static PipelineStage BranchOf(ContinuedStage stage, string targetEntity)
    {
        ArgumentNullException.ThrowIfNull(stage);

        if (stage.Branches?.FirstOrDefault(each => each.Target == targetEntity) is not { } branch || stage.Stage.Resolve is not { } resolve)
            return stage.Stage;

        return stage.Stage with { Resolve = resolve with { ByTarget = null, Path = branch.Path, Elements = branch.Elements } };
    }

    /// <summary>
    /// One stage with its root on the owner's row. A stage directly under the anchor's alias or its
    /// owning row loses <c>forTarget</c>, which picked this target here and names nothing the owner
    /// continues; a stage under an alias another continued stage added keeps it, since it names a
    /// target of that alias, whose join the owner binds and continues itself.
    /// </summary>
    public static PipelineStage Rewrite(PipelineStage stage, BoundStage.Resolve anchor, bool itemTarget)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(anchor);

        // A union join under an alias a continued stage added travels as written: the owner splits it.
        if (stage.Resolve is { ByTarget: { } branches } union)
            return stage with
            {
                Resolve = union with { ByTarget = branches.Select(branch => branch with { Path = ToOwner(branch.Path, anchor, itemTarget) }).ToList() },
            };

        if (stage.Resolve is { } resolve)
            return stage with
            {
                Resolve = resolve with
                {
                    Path = ToOwner(resolve.Path, anchor, itemTarget),
                    ForTarget = UnderAnchor(resolve.Path, anchor) ? null : resolve.ForTarget,
                },
            };

        if (stage.Lookup is { } lookup)
        {
            var on = ToOwner(lookup.On, anchor, itemTarget);

            return stage with
            {
                Lookup = lookup with
                {
                    On = string.IsNullOrEmpty(on) ? null : on,
                    ForTarget = UnderAnchor(lookup.On, anchor) ? null : lookup.ForTarget,
                },
            };
        }

        return stage;
    }

    /// <summary>Whether a stage's root lies directly under the anchor's alias or its owning row, not under an alias a continued stage added.</summary>
    private static bool UnderAnchor(string? root, BoundStage.Resolve anchor) =>
        root is not null && (Strip(root, anchor.As) is not null || (anchor.ParentAs is { } parentAs && Strip(root, parentAs) is not null));

    /// <summary>
    /// A path of the origin row as the owner's row holds it: under the anchor's alias onto the
    /// matched element (item target) or the row itself (entity target), under its <c>parentAs</c> onto
    /// the row; an alias a continued stage added keeps its name. The row itself is the empty path.
    /// </summary>
    public static string? ToOwner(string? path, BoundStage.Resolve anchor, bool itemTarget)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if (path is null)
            return null;

        if (Strip(path, anchor.As) is { } underAlias)
            return itemTarget ? Join(BoundKeyedBy.Element, underAlias) : underAlias;

        if (anchor.ParentAs is { } parentAs && Strip(path, parentAs) is { } underParent)
            return underParent;

        return path;
    }

    /// <summary>
    /// An owner's error path as the caller wrote it: the rewritten root of <paramref name="origin"/>
    /// back to the origin's, a path under the element back under the anchor's alias; anything else
    /// (a path of the continued stage's own target) as it is.
    /// </summary>
    public static string? ToOrigin(string? ownerPath, ContinuedStage origin, BoundStage.Resolve anchor, bool itemTarget, string? targetEntity = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(anchor);

        // A union join's root is the path of the branch the owner was sent.
        var root = origin.RootFor(targetEntity);

        if (ownerPath is null)
            return root;

        if (ownerPath == ToOwner(root, anchor, itemTarget))
            return root;

        if (itemTarget && Strip(ownerPath, BoundKeyedBy.Element) is { } underElement)
            return Join(anchor.As, underElement);

        return ownerPath;
    }

    /// <summary>
    /// A path of the owner's row as the origin row has it, the inverse of <see cref="ToOwner"/>: under
    /// an alias a continued stage added it keeps its name; a member of the matched element lies under
    /// the anchor's alias, a member of an item target's row under its <c>parentAs</c>, a member of an
    /// entity target's row under the anchor's alias. Explain reads an owner's reads back with it.
    /// </summary>
    public static string FromOwner(string ownerPath, BoundStage.Resolve anchor, bool itemTarget, IReadOnlyCollection<string> continuedAliases)
    {
        ArgumentNullException.ThrowIfNull(ownerPath);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(continuedAliases);

        if (continuedAliases.Contains(ownerPath.Split('.')[0]))
            return ownerPath;

        if (!itemTarget)
            return Join(anchor.As, ownerPath);

        if (Strip(ownerPath, BoundKeyedBy.Element) is { } underElement)
            return Join(anchor.As, underElement);

        return anchor.ParentAs is { } parentAs ? Join(parentAs, ownerPath) : ownerPath;
    }

    /// <summary>The rest of <paramref name="path"/> below <paramref name="root"/> (empty for the root itself), or null when it is not under it.</summary>
    private static string? Strip(string path, string root) =>
        path == root ? "" : path.StartsWith(root + ".", StringComparison.Ordinal) ? path[(root.Length + 1)..] : null;

    private static string Join(string root, string rest) => rest.Length == 0 ? root : root + "." + rest;
}
