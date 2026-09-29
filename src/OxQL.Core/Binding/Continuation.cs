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
/// to, inherited from an alias it continues under; <paramref name="Aliases"/> the aliases it adds,
/// which the owner projects and the keyed fetch lifts to the origin row.
/// </summary>
public sealed record ContinuedStage(string Anchor, PipelineStage Stage, int OriginIndex, string? ForTarget, IReadOnlyList<string> Aliases) : BoundStage
{
    /// <summary>The stage kind: <c>resolve</c> or <c>lookup</c>.</summary>
    public string Kind => Stage.Resolve is not null ? "resolve" : "lookup";

    /// <summary>What roots the stage in the origin row: a resolve's path, a lookup's <c>on</c>.</summary>
    public string Root => Stage.Resolve?.Path ?? Stage.Lookup?.On ?? "";
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
            if (stage.ForTarget is { } only && only != targetEntity)
                continue;

            stages.Add(Rewrite(stage.Stage, anchor, itemTarget));
            origins.Add(stage);
            aliases.AddRange(stage.Aliases);
        }

        return stages.Count == 0 ? OwnerContinuation.None : new OwnerContinuation(stages, origins, aliases);
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
    public static string? ToOrigin(string? ownerPath, ContinuedStage origin, BoundStage.Resolve anchor, bool itemTarget)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(anchor);

        if (ownerPath is null)
            return origin.Root;

        if (ownerPath == ToOwner(origin.Root, anchor, itemTarget))
            return origin.Root;

        if (itemTarget && Strip(ownerPath, BoundKeyedBy.Element) is { } underElement)
            return Join(anchor.As, underElement);

        return ownerPath;
    }

    /// <summary>The rest of <paramref name="path"/> below <paramref name="root"/> (empty for the root itself), or null when it is not under it.</summary>
    private static string? Strip(string path, string root) =>
        path == root ? "" : path.StartsWith(root + ".", StringComparison.Ordinal) ? path[(root.Length + 1)..] : null;

    private static string Join(string root, string rest) => rest.Length == 0 ? root : root + "." + rest;
}
