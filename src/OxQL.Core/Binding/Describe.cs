using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;

namespace OxQL.Core.Binding;

/// <summary>
/// Where describe sends what this host cannot answer (DESIGN §4.2): the entities of other services,
/// answered by their owner's internal explain. The engine's implementation forwards within the
/// explain's remote budget and caches the answers; a describe that no owner answered is answered
/// with an error and a <c>REMOTE_UNCHECKED</c> note, never a refusal.
/// </summary>
public interface IDescribeOwners
{
    /// <summary>Whether the service behind <paramref name="service"/> can be asked at all (an <c>InternalHosts</c> entry).</summary>
    bool Knows(string service);

    /// <summary>
    /// The owner's answer to one describe entry (an <c>entity</c> describe of its own entity), or null
    /// when it did not answer; <c>Reason</c> then says why (<c>skipped</c>,
    /// <c>unsupported</c>, <c>unreachable</c>, <c>timeout</c>).
    /// </summary>
    Task<(JsonObject? Answer, string? Reason)> DescribeAsync(string service, JsonObject entry, CancellationToken cancellationToken);
}

/// <summary>
/// Explain's describe (DESIGN §4.2, §4.5): the children of a prefix, or exact paths, in the shape
/// before a pipeline index or in an entity's entry shape, each with the facts of its member and the
/// flags <see cref="Shape.Resolve"/> answers for every usage at that point of the pipeline. Paths
/// under a remote alias, and the entities of other services, are described by their owner and the
/// flags are this host's again. Nothing is read from the database but the addon definitions of an
/// entity the pipeline did not enter.
/// </summary>
public sealed class Describe
{
    /// <summary>The usages a describe entry may name, in DESIGN §4.2 order.</summary>
    public static readonly IReadOnlyList<string> Usages = ["match", "sort", "project", "unwind", "groupKey", "aggregate", "select", "resolve", "lookupOn"];

    /// <summary>The members a describe entry may carry.</summary>
    public static readonly IReadOnlyList<string> Members = ["id", "at", "entity", "prefix", "paths", "usage", "depth", "referencing"];

    /// <summary>The order <c>operators</c> lists the operators in: the operator table's, then <c>any</c>.</summary>
    public static readonly IReadOnlyList<string> OperatorOrder =
        ["eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex", "is", "any"];

    /// <summary>The <c>caseFolding</c> of a member a contract 2 string comparison folds by default.</summary>
    public const string Folds = "folds";

    /// <summary>The <c>caseFolding</c> of every other member.</summary>
    public const string NoFolding = "none";

    private const int MaxDepth = 3;

    private readonly EntityModel model;
    private readonly RequestContext context;
    private readonly QueryRequest query;
    private readonly BindTrace? trace;
    private readonly IDescribeOwners? owners;
    private readonly List<Diagnostic> notes;
    private readonly CancellationToken cancellationToken;
    private readonly int maxChildren;
    private readonly Dictionary<string, IReadOnlyList<AddonDefinition>> loadedAddons = new(StringComparer.Ordinal);

    private Describe(EntityModel model, RequestContext context, QueryRequest query, BindTrace? trace, IDescribeOwners? owners, List<Diagnostic> notes, CancellationToken cancellationToken)
    {
        this.model = model;
        this.context = context;
        this.query = query;
        this.trace = trace;
        this.owners = owners;
        this.notes = notes;
        this.cancellationToken = cancellationToken;
        maxChildren = Math.Max(1, context.Options.Explain.MaxDescribeChildren);
    }

    /// <summary>
    /// One answer per describe entry of <paramref name="request"/>, in order: the entries past
    /// <c>Explain.MaxDescribeRequests</c> are answered with <c>REQUEST_TOO_LARGE</c>, a malformed entry
    /// with the code of what is wrong with it. Owners that did not answer leave a note in <paramref name="notes"/>.
    /// </summary>
    public static async Task<IReadOnlyList<JsonNode>> AnswerAsync(
        ExplainRequest request, BindTrace? trace, EntityModel model, RequestContext context, IDescribeOwners? owners, List<Diagnostic> notes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(notes);

        var describe = new Describe(model, context, request.Query, trace, owners, notes, cancellationToken);
        var answers = new List<JsonNode>(request.Describe.Count);
        var cap = Math.Max(1, context.Options.Explain.MaxDescribeRequests);

        for (var position = 0; position < request.Describe.Count; position++)
        {
            var entry = request.Describe[position];

            answers.Add(position < cap
                ? await describe.AnswerAsync(entry).ConfigureAwait(false)
                : Failed(Entry.Echo(entry), Codes.RequestTooLarge, $"An explain answers at most {cap} describe requests; this is number {position + 1}."));
        }

        return answers;
    }

    // ---- one entry ---------------------------------------------------------------------------

    /// <summary>A describe entry as read: the members, or the first thing wrong with it.</summary>
    private sealed record Entry(string Id, int? At, string? Entity, string? Prefix, IReadOnlyList<string>? Paths, string Usage, int Depth, bool Referencing)
    {
        /// <summary>The answer's echo of an entry that may not have read: whatever of it is readable.</summary>
        public static JsonObject Echo(JsonObject raw)
        {
            var echo = new JsonObject { ["id"] = raw["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : "" };

            foreach (var name in new[] { "at", "entity", "prefix", "paths" })
                if (raw[name] is { } value)
                    echo[name] = value.DeepClone();

            echo["usage"] = raw["usage"] is JsonValue usage && usage.TryGetValue<string>(out var usageText) ? usageText : null;

            return echo;
        }

        public JsonObject Head()
        {
            var head = new JsonObject { ["id"] = Id };

            if (At is { } at)
                head["at"] = at;

            if (Entity is not null)
                head["entity"] = Entity;

            if (Paths is not null)
                head["paths"] = new JsonArray(Paths.Select(path => (JsonNode)path).ToArray());
            else
                head["prefix"] = Prefix;

            head["usage"] = Usage;

            return head;
        }
    }

    private static (Entry? Entry, string? Code, string? Message) Read(JsonObject raw)
    {
        foreach (var (name, _) in raw)
            if (!Members.Contains(name, StringComparer.Ordinal))
                return (null, Codes.UnknownStageMember, $"'{name}' is not a member of a describe request; it carries {string.Join(", ", Members)}.");

        if (raw["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id))
            return (null, Codes.UnknownStageMember, "A describe request names its 'id', a string the answer echoes.");

        if (raw["usage"] is not JsonValue usageValue || !usageValue.TryGetValue<string>(out var usage) || !Usages.Contains(usage, StringComparer.Ordinal))
            return (null, Codes.UnknownStageMember, $"A describe request names its 'usage', one of {string.Join(", ", Usages)}.");

        int? at = null;
        string? entity = null;

        if (raw["at"] is { } atNode)
        {
            if (atNode is not JsonValue atValue || !atValue.TryGetValue<int>(out var index) || index < 0)
                return (null, Codes.InvalidOperand, "A describe request's 'at' is a pipeline index, 0 (the entry shape) to the pipeline's length (the final shape).");

            at = index;
        }

        if (raw["entity"] is { } entityNode)
        {
            if (entityNode is not JsonValue entityValue || !entityValue.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                return (null, Codes.UnknownEntity, "A describe request's 'entity' is an entity id, or 'entity#item' for the element of an item collection.");

            entity = text.Trim();
        }

        if ((at is null) == (entity is null))
            return (null, Codes.UnknownStageMember, "A describe request names either 'at' (a pipeline index) or 'entity' (an entity id), and not both.");

        string? prefix = null;
        List<string>? paths = null;

        if (raw["prefix"] is { } prefixNode)
        {
            if (prefixNode is not JsonValue prefixValue || !prefixValue.TryGetValue<string>(out var text))
                return (null, Codes.InvalidPath, "A describe request's 'prefix' is a root alias, a path, or \"\".");

            prefix = text;
        }

        if (raw["paths"] is { } pathsNode)
        {
            if (prefix is not null)
                return (null, Codes.UnknownStageMember, "A describe request names either 'prefix' or 'paths', and not both.");

            if (pathsNode is not JsonArray array || array.Count == 0)
                return (null, Codes.InvalidPath, "A describe request's 'paths' is a non-empty array of paths.");

            paths = [];

            foreach (var item in array)
            {
                if (item is not JsonValue pathValue || !pathValue.TryGetValue<string>(out var path) || path.Length == 0)
                    return (null, Codes.InvalidPath, "A describe request's 'paths' holds non-empty strings.");

                paths.Add(path);
            }
        }

        var depth = 1;

        if (raw["depth"] is { } depthNode)
        {
            if (paths is not null)
                return (null, Codes.OptionNotApplicable, "'depth' applies to a describe by 'prefix'; 'paths' names each path it describes.");

            if (depthNode is not JsonValue depthValue || !depthValue.TryGetValue<int>(out depth) || depth < 1 || depth > MaxDepth)
                return (null, Codes.InvalidOperand, $"A describe request's 'depth' is 1 to {MaxDepth}.");
        }

        var referencing = false;

        if (raw["referencing"] is { } referencingNode)
        {
            if (referencingNode is not JsonValue referencingValue || !referencingValue.TryGetValue<bool>(out referencing))
                return (null, Codes.InvalidOperand, "A describe request's 'referencing' is true or false.");

            if (referencing && entity is null)
                return (null, Codes.OptionNotApplicable, "'referencing' applies to a describe of an 'entity'.");
        }

        return (new Entry(id, at, entity, paths is null ? prefix ?? "" : null, paths, usage, depth, referencing), null, null);
    }

    private async Task<JsonNode> AnswerAsync(JsonObject raw)
    {
        var (entry, code, message) = Read(raw);

        if (entry is null)
            return Failed(Entry.Echo(raw), code!, message!);

        if (entry.At is { } at && at > query.Pipeline.Count)
            return Failed(entry.Head(), Codes.InvalidOperand, $"'at' is {at}; the pipeline has {query.Pipeline.Count} stages, so 'at' is 0 to {query.Pipeline.Count}.");

        try
        {
            return entry.Entity is not null ? await EntityAsync(entry).ConfigureAwait(false) : await AtAsync(entry).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    // ---- at a pipeline index -----------------------------------------------------------------

    private async Task<JsonObject> AtAsync(Entry entry)
    {
        if (trace is null)
            return Failed(entry.Head(), Codes.UnknownEntity, $"'{query.EntityType}' is not an entity of this host; there is no shape to describe.");

        var shape = trace.ShapeAt(entry.At!.Value);
        var collected = new Collected();

        if (entry.Paths is not null)
        {
            foreach (var path in entry.Paths)
            {
                if (collected.Full(maxChildren))
                    break;

                if (UnderAlias(shape, path) is { } alias)
                    await AliasChildrenAsync(entry, shape, alias.Name, alias.Node, alias.Below, [alias.Below], collected).ConfigureAwait(false);
                else
                    collected.Add(Child(shape, path, entry.Usage, name: LastSegment(path)));
            }

            return Answered(entry, RootOf(shape, ""), collected);
        }

        var prefix = entry.Prefix!;

        if (prefix.Length > 0 && UnderAlias(shape, prefix) is { } under)
        {
            await AliasChildrenAsync(entry, shape, under.Name, under.Node, under.Below, null, collected).ConfigureAwait(false);
            return Answered(entry, RootOfAlias(under.Name, under.Node, under.Below, collected.Targets), collected);
        }

        if (prefix.Length > 0)
        {
            var resolution = shape.Resolve(prefix, PathUsage.Project);

            if (!resolution.Succeeded)
                return Failed(entry.Head(), resolution.Code == Shape.PoisonedCode ? Codes.UnknownPath : resolution.Code!, resolution.Message!);
        }

        await LocalChildrenAsync(shape, prefix, entry.Usage, entry.Depth, collected, origin: null).ConfigureAwait(false);

        return Answered(entry, RootOf(shape, prefix), collected);
    }

    /// <summary>A path that starts at a remote or keyed alias: the alias, its node and the rest of the path below it.</summary>
    private static (string Name, ShapeNode Node, string Below)? UnderAlias(Shape shape, string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? path : path[..dot];

        if (head.Length == 0 || !shape.Roots.TryGetValue(head, out var node) || node is not (ShapeNode.Remote or ShapeNode.Keyed))
            return null;

        return (head, node, dot < 0 ? "" : path[(dot + 1)..]);
    }

    // ---- an entity's entry shape ------------------------------------------------------------

    private async Task<JsonObject> EntityAsync(Entry entry)
    {
        var id = entry.Entity!;
        var hash = id.IndexOf('#', StringComparison.Ordinal);
        var entityId = hash < 0 ? id : id[..hash];
        var item = hash < 0 ? null : id[(hash + 1)..];

        if (!model.TryResolve(entityId, out var entity, out _))
            return await ForwardedEntityAsync(entry, entityId).ConfigureAwait(false);

        var shape = await EntryShapeAsync(entity).ConfigureAwait(false);
        JsonObject root;

        if (item is not null)
        {
            if (item.Length == 0 || shape.Resolve(item, PathUsage.Unwind) is not { Succeeded: true, Path: { Path: not null, Kind: Kind.Array } collection }
                || collection.Shape?.Of?.Kind != Kind.Object)
                return Failed(entry.Head(), Codes.UnknownPath, $"'{item}' is not a collection of objects on {entity.Id}; an item target names one.");

            shape = shape.ForElement(collection);
            root = new JsonObject { ["node"] = "element", ["entity"] = entity.Id, ["source"] = item };
        }
        else
        {
            root = new JsonObject { ["node"] = "entity", ["entity"] = entity.Id };
        }

        var collected = new Collected();
        var referencedBy = entry.Referencing ? ReferencedBy(entity.Id, item) : null;

        if (entry.Paths is not null)
        {
            foreach (var path in entry.Paths)
                if (!collected.Full(maxChildren))
                    collected.Add(Child(shape, path, entry.Usage, LastSegment(path), referencedBy));

            return Answered(entry, root, collected);
        }

        if (entry.Prefix!.Length > 0 && shape.Resolve(entry.Prefix, PathUsage.Project) is { Succeeded: false } failed)
            return Failed(entry.Head(), failed.Code!, failed.Message!);

        await LocalChildrenAsync(shape, entry.Prefix, entry.Usage, entry.Depth, collected, origin: null, referencedBy).ConfigureAwait(false);

        return Answered(entry, root, collected);
    }

    /// <summary>The entry shape of an entity, with its organisation's addon definitions when it is extendable.</summary>
    private async Task<Shape> EntryShapeAsync(EntityDef entity)
    {
        if (trace?.Entry is { } entry && ReferenceEquals(entry.Entity, entity))
            return Shape.ForEntity(entity, entry.Addons);

        if (entity.Extendable && context.Organisation is { } organisation && !loadedAddons.ContainsKey(entity.Id))
            loadedAddons[entity.Id] = await context.AddonSource.ForEntityAsync(entity.Id, organisation, cancellationToken).ConfigureAwait(false);

        return Shape.ForEntity(entity, loadedAddons);
    }

    /// <summary>An entity of another service: its owner's answer to the same entry, marked forwarded.</summary>
    private async Task<JsonObject> ForwardedEntityAsync(Entry entry, string entityId)
    {
        var service = entityId.Split('.')[0];

        if (owners is null || !owners.Knows(service))
            return Failed(entry.Head(), Codes.UnknownEntity, $"'{entityId}' is not an entity of this host, and this host knows no owner for '{service}'.");

        var forwarded = entry.Head();

        if (entry.Depth > 1)
            forwarded["depth"] = entry.Depth;

        if (entry.Referencing)
            forwarded["referencing"] = true;

        var (answer, reason) = await owners.DescribeAsync(service, forwarded, cancellationToken).ConfigureAwait(false);

        if (answer is null)
        {
            notes.Add(Unchecked(null, service, entry.Entity!, reason, entry.Id));
            return Failed(entry.Head(), Codes.ResolveUnreachable, $"The owner of '{entityId}' did not answer the describe ({reason ?? "no answer"}).");
        }

        var result = (JsonObject)answer.DeepClone();

        result["id"] = entry.Id;
        result["forwarded"] = true;

        return result;
    }

    // ---- children of a local prefix --------------------------------------------------------

    /// <summary>What one answer collects: its children, whether the cap cut them, and the targets of a remote alias.</summary>
    private sealed class Collected
    {
        public List<JsonObject> Children { get; } = [];

        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public bool Truncated { get; set; }

        public bool Forwarded { get; set; }

        public List<string> Targets { get; } = [];

        public bool Full(int cap)
        {
            if (Children.Count < cap)
                return false;

            Truncated = true;
            return true;
        }

        public void Add(JsonObject child)
        {
            var path = child["path"]!.GetValue<string>();

            if (Seen.Add(path))
                Children.Add(child);
        }
    }

    /// <summary>
    /// The children of <paramref name="prefix"/> in <paramref name="shape"/>, to <paramref name="depth"/>
    /// levels, in walk order. With <paramref name="origin"/> the shape is a target's own and every child
    /// is re-described at the origin under the alias (<see cref="Rebased"/>).
    /// </summary>
    private async Task LocalChildrenAsync(Shape shape, string prefix, string usage, int depth, Collected collected,
        (Shape Shape, string Alias)? origin, IReadOnlyDictionary<string, List<JsonObject>>? referencedBy = null)
    {
        foreach (var (name, path) in ChildPaths(shape, prefix))
        {
            if (collected.Full(maxChildren))
                return;

            var child = Child(shape, path, usage, name, referencedBy);

            if (origin is { } at)
                child = Rebased(child, at.Shape, at.Alias + "." + path, usage);

            if (collected.Seen.Contains(child["path"]!.GetValue<string>()))
                continue;

            collected.Add(child);

            if (depth > 1 && child["hasChildren"]!.GetValue<bool>() && UnderAlias(shape, path) is null)
                await LocalChildrenAsync(shape, path, usage, depth - 1, collected, origin, referencedBy).ConfigureAwait(false);
        }
    }

    /// <summary>The names and paths one level below <paramref name="prefix"/>: the root members and the aliases for <c>""</c>.</summary>
    private static IEnumerable<(string Name, string Path)> ChildPaths(Shape shape, string prefix)
    {
        if (prefix.Length == 0)
        {
            if (shape.Roots.TryGetValue(Shape.ImplicitRoot, out var implicitRoot))
            {
                var (entity, parent) = implicitRoot switch
                {
                    ShapeNode.Entity row => (row.Def, ""),
                    ShapeNode.Element element => (element.Def, element.Source.Wire),
                    _ => ((EntityDef?)null, ""),
                };

                if (entity is not null)
                    foreach (var path in entity.ChildrenOf(parent))
                    {
                        var name = LastSegment(path.Wire);

                        if (name != "*" && (!shape.Roots.ContainsKey(name) || name == Shape.ImplicitRoot) && shape.IsVisible(name))
                            yield return (name, name);
                    }
            }

            foreach (var (name, node) in shape.Roots)
                if (name != Shape.ImplicitRoot && node is not ShapeNode.Poisoned && shape.Carries(name) && shape.IsVisible(name))
                    yield return (name, name);

            yield break;
        }

        var resolution = shape.Resolve(prefix, PathUsage.Project);

        if (resolution.Path is not { } resolved)
            yield break;

        // At an addon bag or below it: the organisation's definitions one level down.
        if (AddonAt(resolved, prefix) is { } definitionPath)
        {
            foreach (var name in AddonNames(shape, resolved.Entity, definitionPath))
                if (shape.IsVisible(prefix + "." + name))
                    yield return (name, prefix + "." + name);

            yield break;
        }

        var owner = resolved.Entity;

        if (owner is null)
            yield break;

        var wire = resolved.Path?.Wire ?? "";

        foreach (var path in owner.ChildrenOf(wire))
        {
            var name = LastSegment(path.Wire);

            // A join's alias offers what its select fetched, nothing it would answer as missing.
            if (name != "*" && shape.IsVisible(prefix + "." + name) && shape.NotSelected(prefix + "." + name) is null)
                yield return (name, prefix + "." + name);
        }
    }

    /// <summary>
    /// Where a resolved path lies in an addon bag: <c>""</c> at the bag itself, the definition path
    /// below it, or null when the path is not the bag nor under it.
    /// </summary>
    private static string? AddonAt(ResolvedPath resolved, string path)
    {
        if (resolved.Path is not { IsAddonRoot: true } bag)
            return null;

        var segments = path.Split('.');
        var at = Array.LastIndexOf(segments, bag.Member.WireName);

        return at < 0 ? null : string.Join('.', segments.Skip(at + 1));
    }

    /// <summary>The addon definition names one level below <paramref name="at"/> (<c>""</c>: the bag itself), ordinally.</summary>
    private static IEnumerable<string> AddonNames(Shape shape, EntityDef? entity, string at)
    {
        if (entity is null || !shape.Addons.TryGetValue(entity.Id, out var definitions))
            return [];

        var start = at.Length == 0 ? "" : at + ".";

        return definitions
            .Where(definition => definition.Path.StartsWith(start, StringComparison.Ordinal) && definition.Path.Length > start.Length)
            .Select(definition => definition.Path[start.Length..].Split('.')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    }

    // ---- remote and keyed aliases ----------------------------------------------------------

    /// <summary>
    /// The children below a remote or keyed alias: each target described in its own shape (this host's
    /// for a local target, the owner's answer for a remote one), merged by path, the first target's
    /// description winning, and every child re-described at the origin under the alias.
    /// </summary>
    private async Task AliasChildrenAsync(Entry entry, Shape origin, string alias, ShapeNode node, string rest, IReadOnlyList<string>? paths, Collected collected)
    {
        IReadOnlyList<(string Entity, string? Item)> targets = node is ShapeNode.Keyed keyed
            ? keyed.Targets.Select(target => (target.Entity.Id, target.Item?.Wire)).Distinct().ToList()
            : await RemoteTargetsAsync(alias, entry.At!.Value).ConfigureAwait(false);

        foreach (var (entityId, item) in targets)
        {
            var name = item is null ? entityId : entityId + "#" + item;

            if (!collected.Targets.Contains(name))
                collected.Targets.Add(name);
        }

        foreach (var (entityId, item) in targets)
        {
            if (collected.Full(maxChildren))
                return;

            if (model.TryResolve(entityId, out var entity, out _))
            {
                var shape = await EntryShapeAsync(entity).ConfigureAwait(false);

                if (item is not null)
                {
                    if (shape.Resolve(item, PathUsage.Unwind) is not { Succeeded: true, Path: { Path: not null } collection })
                        continue;

                    shape = shape.ForElement(collection);
                }

                if (paths is not null)
                {
                    foreach (var path in paths)
                        if (path.Length > 0 && shape.Resolve(path, PathUsage.Project).Succeeded && !collected.Full(maxChildren))
                            collected.Add(Rebased(Child(shape, path, entry.Usage, LastSegment(path)), origin, alias + "." + path, entry.Usage));
                        else if (path.Length == 0)
                            collected.Add(Child(origin, alias, entry.Usage, alias));

                    continue;
                }

                if (rest.Length > 0 && !shape.Resolve(rest, PathUsage.Project).Succeeded)
                    continue;

                await LocalChildrenAsync(shape, rest, entry.Usage, entry.Depth, collected, (origin, alias)).ConfigureAwait(false);
                continue;
            }

            var service = entityId.Split('.')[0];

            if (owners is null || !owners.Knows(service))
            {
                notes.Add(Unchecked(entry.At, service, item is null ? entityId : entityId + "#" + item, "unconfigured", entry.Id));
                continue;
            }

            var forwarded = new JsonObject { ["id"] = entry.Id, ["entity"] = item is null ? entityId : entityId + "#" + item };

            if (paths is not null)
            {
                var named = paths.Where(path => path.Length > 0).ToList();

                if (paths.Any(path => path.Length == 0))
                    collected.Add(Child(origin, alias, entry.Usage, alias));

                if (named.Count == 0)
                    continue;

                forwarded["paths"] = new JsonArray(named.Select(path => (JsonNode)path).ToArray());
            }
            else
            {
                forwarded["prefix"] = rest;
            }

            forwarded["usage"] = entry.Usage;

            if (paths is null && entry.Depth > 1)
                forwarded["depth"] = entry.Depth;

            var (answer, reason) = await owners.DescribeAsync(service, forwarded, cancellationToken).ConfigureAwait(false);

            if (answer is null)
            {
                notes.Add(Unchecked(entry.At, service, forwarded["entity"]!.GetValue<string>(), reason, entry.Id));
                continue;
            }

            collected.Forwarded = true;

            if (answer["truncated"] is JsonValue truncated && truncated.TryGetValue<bool>(out var cut) && cut)
                collected.Truncated = true;

            foreach (var child in answer["children"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (collected.Full(maxChildren))
                    return;

                var path = child["path"]?.GetValue<string>();

                if (path is null)
                    continue;

                collected.Add(Rebased((JsonObject)child.DeepClone(), origin, alias + "." + path, entry.Usage, owner: service));
            }
        }
    }

    /// <summary>
    /// The targets of a remote alias created before <paramref name="at"/>: the cases of the resolve
    /// that named it (narrowed by its <c>target</c>; its owning row's entities for <c>parentAs</c>), or
    /// the entity of the lookup that named it. A resolve continued under another remote alias has its
    /// reference at that alias's owner, which is asked for it.
    /// </summary>
    private async Task<IReadOnlyList<(string Entity, string? Item)>> RemoteTargetsAsync(string alias, int at)
    {
        for (var index = Math.Min(at, query.Pipeline.Count) - 1; index >= 0; index--)
        {
            var stage = query.Pipeline[index];

            if (stage.Lookup is { } lookup && lookup.As == alias && lookup.From is { } from)
                return [(from.Trim().ToLowerInvariant(), null)];

            if (stage.Resolve is not { } resolve || (resolve.As != alias && resolve.ParentAs != alias) || resolve.Path is null)
                continue;

            var cases = await ReferenceTargetsAsync(resolve.Path, index).ConfigureAwait(false);

            if (resolve.Target is { } wanted)
                cases = cases.Where(target => target.Entity == wanted).ToList();

            if (resolve.As != alias)
                cases = cases.Select(target => (target.Entity, (string?)null)).ToList();

            return cases.Distinct().ToList();
        }

        return trace?.ShapeAt(at).Roots.GetValueOrDefault(alias) is ShapeNode.Remote remote ? [(remote.TargetEntity, null)] : [];
    }

    /// <summary>The targets the reference at <paramref name="path"/> declares, in the shape before stage <paramref name="index"/>.</summary>
    private async Task<IReadOnlyList<(string Entity, string? Item)>> ReferenceTargetsAsync(string path, int index)
    {
        var shape = trace!.ShapeAt(index);

        if (UnderAlias(shape, path) is not { } under)
            return shape.Resolve(path, PathUsage.Match).Path?.Path?.References
                .SelectMany(declared => declared.Targets)
                .Select(target => (target.Entity, target.Item))
                .ToList() ?? [];

        // A continued resolve: the reference lies under another alias, whose targets hold it.
        var described = new Collected();
        var probe = new Entry("reference", index, null, null, [under.Below], "resolve", 1, false);

        await AliasChildrenAsync(probe, shape, under.Name, under.Node, under.Below, [under.Below], described).ConfigureAwait(false);

        return described.Children
            .SelectMany(child => child["reference"]?["cases"]?.AsArray().OfType<JsonObject>() ?? [])
            .SelectMany(declared => declared["targets"]?.AsArray().OfType<JsonObject>() ?? [])
            .Select(target => (target["entity"]!.GetValue<string>(), target["item"]?.GetValue<string>()))
            .ToList();
    }

    /// <summary>
    /// A child described in a target's shape, re-described at the origin under its alias: its path is
    /// the origin's, and what may be done with it there — filter, sort, unwind, group, project — is
    /// what the origin's shape answers, which knows the rows are joined after the page. The
    /// collections the path crosses below the alias are the target's: the origin cannot see them
    /// under a remote alias, and a resolve continued there follows them with <c>elements</c> at
    /// the owner, so <c>underCollection</c> and <c>reference.followable</c> keep what the target said.
    /// </summary>
    private static JsonObject Rebased(JsonObject child, Shape origin, string path, string usage, string? owner = null)
    {
        var ownUnder = child["underCollection"] is JsonValue under && under.TryGetValue<int>(out var depth) ? depth : 0;
        var ownCrossed = child["reference"]?["followable"] is JsonObject followable
            ? followable["one"]?.GetValue<bool>() == true ? 0 : followable["elements"] is JsonArray ? 1 : 2
            : 0;

        child["path"] = path;

        var match = origin.Resolve(path, PathUsage.Match);
        var filterable = match is { Succeeded: true, Path.Filterable: true };

        child["filterable"] = filterable;
        child["sortable"] = origin.Resolve(path, PathUsage.Sort) is { Succeeded: true, Path.Sortable: true };
        child["projectable"] = origin.Resolve(path, PathUsage.Project).Succeeded;
        child["unwindable"] = false;
        child["groupable"] = false;
        child["underCollection"] = ownUnder;

        if (!filterable)
            child["operators"] = new JsonArray();
        else if (child["operators"] is JsonArray operators)
            child["operators"] = new JsonArray(operators.Where(op => op?.GetValue<string>() is not ("any" or "is")).Select(op => op!.DeepClone()).ToArray());

        if (!filterable)
            child["caseFolding"] = NoFolding;

        var notes = child["notes"] as JsonArray ?? [];

        if (notes.All(note => note?.GetValue<string>() != Notes.OwnerBinds) && owner is not null)
            notes.Add(Notes.OwnerBinds);

        child["notes"] = notes.DeepClone();

        if (child["reference"] is JsonObject reference)
            reference["followable"] = Followable(Math.Max(Crossed(origin, path), ownCrossed));

        var asked = Asked(origin, path, usage);

        child["error"] = asked is { Succeeded: false } ? Issue(asked.Code!, asked.Message!, path) : null;

        return child;
    }

    // ---- one child ---------------------------------------------------------------------------

    /// <summary>One child descriptor (DESIGN §4.5): the member's facts and the flags of every usage at <paramref name="shape"/>.</summary>
    private static JsonObject Child(Shape shape, string path, string usage, string name, IReadOnlyDictionary<string, List<JsonObject>>? referencedBy = null)
    {
        var project = shape.Resolve(path, PathUsage.Project);
        var match = shape.Resolve(path, PathUsage.Match);
        var sort = shape.Resolve(path, PathUsage.Sort);
        var unwind = shape.Resolve(path, PathUsage.Unwind);
        var group = shape.Resolve(path, PathUsage.GroupKey);
        var facts = project.Path ?? match.Path ?? sort.Path;
        var asked = Asked(shape, path, usage);
        var member = facts is null ? null : MemberAt(facts, path);
        var kind = facts?.Kind ?? Kind.Unknown;
        var leafKind = facts?.Shape?.LeafKind ?? kind;
        var matched = match.Path;
        var filterable = matched is { Filterable: true };
        var folds = filterable && !matched!.IsRemote && OperandCoercer.FoldsByDefault("eq", matched);
        var variantHolder = facts is null ? null : OperandCoercer.VariantHolder(facts);
        var node = facts?.Root;
        var isRootAlias = facts is not null && facts.Path is null && path.IndexOf('.', StringComparison.Ordinal) < 0 && shape.Roots.ContainsKey(path);
        var enumValues = EnumOf(facts);
        var childNotes = new JsonArray();

        if (member?.OnlyFor is { Count: > 0 })
            childNotes.Add(Notes.OnlyForVariants);

        if (SnapshotOf(facts) is not null)
            childNotes.Add(Notes.SnapshotCopy);

        if (folds && usage is "match" or "sort" or "groupKey")
            childNotes.Add(Notes.TextFolds);

        if (usage == "match" && matched is { CollectionAncestors: > 0 })
            childNotes.Add(Notes.SomeElement);

        if (node is ShapeNode.Remote)
            childNotes.Add(Notes.OwnerBinds);

        var child = new JsonObject
        {
            ["name"] = name,
            ["path"] = path,
            ["displayName"] = member?.DisplayName ?? facts?.Addon?.DisplayName,
            ["description"] = member?.Description ?? facts?.Addon?.Description ?? (isRootAlias ? RootEntityOf(node)?.Root.Description : null),
            ["kind"] = Kinds.NameOf(kind),
            ["leafKind"] = Kinds.NameOf(leafKind),
            ["nullable"] = member?.Nullable ?? (node is ShapeNode.Entity or ShapeNode.Keyed or ShapeNode.Remote or ShapeNode.Array || facts?.Addon is not null),
            ["stored"] = facts?.Path is { } pathDef ? pathDef.Stored : facts is not null,
            ["collection"] = kind == Kind.Array || (facts?.Path is { } collectionPath && facts.Addon is null && Shape.IsCollection(collectionPath) && kind == Kind.Dictionary),
            ["underCollection"] = project.Path?.CollectionAncestors ?? 0,
            ["filterable"] = filterable,
            ["sortable"] = sort is { Succeeded: true, Path.Sortable: true },
            ["projectable"] = project.Succeeded,
            ["unwindable"] = Unwindable(unwind),
            ["groupable"] = group is { Succeeded: true, Path: { CollectionAncestors: 0 } key } && key.Kind != Kind.Array && Kinds.IsScalar(key.Kind),
            ["operators"] = new JsonArray(Operators(match).Select(op => (JsonNode)op).ToArray()),
            ["caseFolding"] = folds ? Folds : NoFolding,
            ["enum"] = enumValues,
            ["variants"] = variantHolder is { Variants.Count: > 0 } ? new JsonArray(VariantNames(variantHolder).Select(variant => (JsonNode)variant).ToArray()) : null,
            ["flatten"] = FlattensOf(facts).FirstOrDefault(),
            ["flattenMembers"] = FlattensOf(facts) is { Count: > 1 } flattens ? new JsonArray(flattens.Select(flatten => (JsonNode)flatten).ToArray()) : null,
            ["onlyFor"] = member?.OnlyFor is { Count: > 0 } onlyFor ? new JsonArray(onlyFor.Select(variant => (JsonNode)variant).ToArray()) : null,
            ["snapshotOf"] = SnapshotOf(facts),
            ["reference"] = ReferenceOf(facts, Crossed(shape, path)),
            ["referencedBy"] = referencedBy is not null && referencedBy.TryGetValue(path, out var referencing) && referencing.Count > 0
                ? new JsonArray(referencing.Select(entry => (JsonNode)entry.DeepClone()).ToArray())
                : null,
            ["addon"] = AddonOf(facts?.Addon),
            ["deprecated"] = member?.Deprecated is { } deprecated ? Deprecation(deprecated) : null,
            ["constraints"] = member?.Constraints is { } constraints ? ConstraintsOf(constraints) : null,
            ["hasChildren"] = HasChildren(shape, facts, path),
            ["notes"] = childNotes,
            ["error"] = asked is { Succeeded: false } ? Issue(asked.Code == Shape.PoisonedCode ? Codes.UnknownPath : asked.Code!, asked.Message!, path) : null,
        };

        return child;
    }

    /// <summary>The resolution of <paramref name="path"/> for the usage a describe entry names; <c>lookupOn</c> is checked as the lookup checks its <c>on</c>.</summary>
    private static PathResolution Asked(Shape shape, string path, string usage)
    {
        if (usage == "lookupOn")
        {
            if (path.Contains('.', StringComparison.Ordinal) || !shape.Roots.TryGetValue(path, out var node) || path == Shape.ImplicitRoot)
                return PathResolution.Fail(Codes.LookupOnNotEntity, $"'{path}' is not an alias of the row here; 'on' names the alias of the parent row.");

            if (!shape.IsVisible(path))
                return PathResolution.Fail(Codes.UnknownPath, $"'{path}' was removed by the projection.");

            return node is ShapeNode.Entity or ShapeNode.Keyed or ShapeNode.Remote
                ? shape.Resolve(path, PathUsage.Project)
                : PathResolution.Fail(Codes.LookupOnNotEntity, $"'{path}' is not an entity row; a lookup's parent is one entity row: the entity itself, a resolved alias or an unwound lookup alias.");
        }

        return shape.Resolve(path, usage switch
        {
            "match" or "resolve" => PathUsage.Match,
            "sort" => PathUsage.Sort,
            "unwind" => PathUsage.Unwind,
            "groupKey" => PathUsage.GroupKey,
            "aggregate" => PathUsage.Aggregate,
            "select" => PathUsage.Select,
            _ => PathUsage.Project,
        });
    }

    /// <summary>The member a resolved path ends in: its path's member, unless the path ends below it (a dictionary key) or at a root.</summary>
    private static MemberDef? MemberAt(ResolvedPath facts, string path)
    {
        if (facts.Addon is not null || facts.Path is not { } pathDef || AddonAt(facts, path) is { Length: > 0 })
            return null;

        return LastSegment(pathDef.Wire) == "*" ? null : pathDef.Member;
    }

    private static EntityDef? RootEntityOf(ShapeNode? node) => node switch
    {
        ShapeNode.Entity entity => entity.Def,
        ShapeNode.Array array => array.Target,
        _ => null,
    };

    private static bool Unwindable(PathResolution unwind)
    {
        if (unwind.Path is not { } path)
            return false;

        if (path.Root is ShapeNode.Array && path.Path is null)
            return true;

        var collection = path.Kind == Kind.Array || (path.Path is not null && path.Addon is null && Shape.IsCollection(path.Path) && path.Kind == Kind.Dictionary);

        return collection && path.Storage is not null && path.CollectionAncestors == 0;
    }

    /// <summary>The operators a condition on the path admits, as the binder admits them (DESIGN §4.5): the operator table's, <c>is</c> on a variant holder, <c>any</c> on a collection of objects.</summary>
    private static IReadOnlyList<string> Operators(PathResolution match)
    {
        if (match.Path is not { } path)
            return [];

        var admitted = new HashSet<string>(StringComparer.Ordinal);

        if (path.IsRemote)
        {
            if (path.Filterable)
                foreach (var op in OperandCoercer.Operators)
                    if (op != "is")
                        admitted.Add(op);
        }
        else if (path.Filterable)
        {
            foreach (var op in OperandCoercer.Operators)
                if (op != "is" && OperandCoercer.Applies(op, path.LeafKind) && !(OperandCoercer.NeedsText(op) && OperandCoercer.IsCharRepresented(path)))
                    admitted.Add(op);
        }
        else if (path.Storage is not null)
        {
            admitted.Add("exists");
        }

        if (!path.IsRemote && path.CollectionAncestors == 0 && OperandCoercer.VariantHolder(path) is { Variants.Count: > 0 })
            admitted.Add("is");

        if (!path.IsRemote && path.Path is not null && path.Kind == Kind.Array && path.Shape?.Of?.Kind == Kind.Object && path.CollectionAncestors == 0)
            admitted.Add("any");

        return OperatorOrder.Where(admitted.Contains).ToList();
    }

    /// <summary>The names <c>is</c> accepts on a variant holder: the concrete base first, then every variant.</summary>
    private static IEnumerable<string> VariantNames(TypeDef type)
    {
        if (OperandCoercer.ConcreteBaseName(type) is { } concrete)
            yield return concrete;

        foreach (var variant in type.Variants)
            yield return variant.Name;
    }

    private static JsonArray? EnumOf(ResolvedPath? facts)
    {
        if (facts?.Addon is { Values: { Count: > 0 } values })
            return new JsonArray(values.Select(value => (JsonNode)new JsonObject { ["name"] = value.Value, ["value"] = value.Value, ["description"] = value.Label }).ToArray());

        if (facts?.Shape?.Leaf is not { Kind: Kind.Enum, Type: { IsEnum: true } type })
            return null;

        return new JsonArray(type.EnumValues.Select(value => (JsonNode)new JsonObject
        {
            ["name"] = value.Name,
            ["value"] = value.Value,
            ["description"] = value.Description,
        }).ToArray());
    }

    /// <summary>
    /// The members <c>unwind.flatten</c> may follow on a collection of objects, every member nesting
    /// the same kind of element (the binder's rule): first the one named like the collection itself
    /// (a group's <c>items</c> under <c>items</c>), which is the recursion of the collection; then the
    /// others in member order. <c>flatten</c> advertises the first, <c>flattenMembers</c> all of them
    /// when there are several.
    /// </summary>
    private static List<string> FlattensOf(ResolvedPath? facts)
    {
        if (facts is not { Kind: Kind.Array } || facts.Shape?.Of is not { Kind: Kind.Object, Type: { } element })
            return [];

        var own = facts.Wire[(facts.Wire.LastIndexOf('.') + 1)..];

        return element.Members
            .Where(member => member is { Kind: Kind.Array, Of: { Kind: Kind.Object, Type: { } nested }, Stored: true, StorageName: not null }
                && (ReferenceEquals(nested, element) || nested.Variants.Any(variant => ReferenceEquals(variant.Type, element))))
            .Select(member => member.WireName)
            .OrderBy(name => name == own ? 0 : 1)
            .ToList();
    }

    private static string? SnapshotOf(ResolvedPath? facts) => facts?.Shape?.SnapshotOf ?? facts?.Shape?.Leaf.SnapshotOf;

    /// <summary>The collections a path crosses that are not unwound here, as the resolve's collection guard counts them.</summary>
    private static int Crossed(Shape shape, string wire)
    {
        var crossed = 0;
        var segments = wire.Split('.');

        for (var length = 1; length <= segments.Length; length++)
            if (shape.Resolve(string.Join('.', segments.Take(length)), PathUsage.Project) is { Succeeded: true, Path.Kind: Kind.Array })
                crossed++;

        return crossed;
    }

    private static JsonObject Followable(int crossed) => new()
    {
        ["one"] = crossed == 0,
        ["elements"] = crossed == 1 ? new JsonArray("first", "all") : null,
    };

    /// <summary>The reference a path declares with every case (DESIGN §4.5), and how a resolve may follow it here.</summary>
    private static JsonObject? ReferenceOf(ResolvedPath? facts, int crossed)
    {
        if (facts?.Path?.References is not { Count: > 0 } cases || facts.Addon is not null)
            return null;

        return new JsonObject
        {
            ["simple"] = cases is [{ IsSimple: true }],
            ["keyAs"] = cases.All(declared => declared.KeyAs == KeyAs.Guid) ? "guid" : null,
            ["cases"] = new JsonArray(cases.Select(declared => (JsonNode)new JsonObject
            {
                ["when"] = declared.When switch
                {
                    ReferenceCondition.PathEquals equals => new JsonObject
                    {
                        ["path"] = equals.Path,
                        ["equals"] = new JsonArray(equals.Values.Select(value => (JsonNode)value).ToArray()),
                    },
                    ReferenceCondition.Variant variant => new JsonObject { ["variant"] = new JsonArray(variant.Names.Select(name => (JsonNode)name).ToArray()) },
                    _ => null,
                },
                ["keyAs"] = declared.KeyAs == KeyAs.Guid ? "guid" : null,
                ["targets"] = new JsonArray(declared.Targets.Select(target => (JsonNode)new JsonObject
                {
                    ["entity"] = target.Entity,
                    ["field"] = target.Field,
                    ["item"] = target.Item,
                    ["remote"] = target.IsRemote,
                }).ToArray()),
            }).ToArray()),
            ["followable"] = Followable(crossed),
        };
    }

    private static JsonObject? AddonOf(AddonDefinition? definition) => definition is null ? null : new JsonObject
    {
        ["id"] = definition.Id.ToString("D"),
        ["path"] = definition.Path,
        ["kind"] = JsonSerializer.SerializeToNode(definition.Kind, OxQLJson.Wire),
        ["displayName"] = definition.DisplayName,
        ["description"] = definition.Description,
        ["retired"] = definition.Retired,
    };

    private static JsonObject Deprecation(DeprecationDef deprecated) => new()
    {
        ["since"] = deprecated.Since,
        ["replacedBy"] = deprecated.ReplacedBy,
        ["note"] = deprecated.Note,
    };

    private static JsonObject ConstraintsOf(ConstraintsDef constraints)
    {
        var written = new JsonObject();

        if (constraints.MaxLength is { } maxLength)
            written["maxLength"] = maxLength;

        if (constraints.Min is { } min)
            written["min"] = min;

        if (constraints.Max is { } max)
            written["max"] = max;

        if (constraints.Pattern is { } pattern)
            written["pattern"] = pattern;

        return written;
    }

    /// <summary>Whether a describe of the path has children: an object, a collection of objects, an entity, element or join alias, an addon bag or object definition.</summary>
    private static bool HasChildren(Shape shape, ResolvedPath? facts, string path)
    {
        if (facts is null)
            return false;

        if (facts.Addon is not null)
            return false;

        if (AddonAt(facts, path) is { } definitionPath)
            return AddonNames(shape, facts.Entity, definitionPath).Any();

        if (facts.Path is { } pathDef && facts.Entity is { } entity)
            return entity.ChildrenOf(pathDef.Wire).Any(child => LastSegment(child.Wire) != "*");

        return facts.Root switch
        {
            ShapeNode.Entity or ShapeNode.Element or ShapeNode.Array or ShapeNode.Keyed => true,
            ShapeNode.Remote => !path.Contains('.', StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Per path of the described entity (or of the element of <paramref name="item"/> on it), the
    /// same-host entities whose members reference it there (DESIGN §4.2 <c>referencing</c>): an entity
    /// target at its target field, an item target at its item collection (or, describing the item, at
    /// its field).
    /// </summary>
    private IReadOnlyDictionary<string, List<JsonObject>> ReferencedBy(string entity, string? item)
    {
        var found = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);

        foreach (var (id, referencing) in model.Entities)
            foreach (var path in referencing.Paths)
            {
                if (!path.Stored)
                    continue;

                foreach (var declared in path.References)
                    foreach (var target in declared.Targets)
                    {
                        if (target.IsRemote || target.Entity != entity)
                            continue;

                        var at = item is null
                            ? target.Item ?? target.Field
                            : target.Item == item ? target.Field : null;

                        if (at is null)
                            continue;

                        if (!found.TryGetValue(at, out var list))
                            found[at] = list = [];

                        if (!list.Any(entry => entry["entity"]!.GetValue<string>() == id && entry["path"]!.GetValue<string>() == path.Wire))
                            list.Add(new JsonObject { ["entity"] = id, ["path"] = path.Wire });
                    }
            }

        return found;
    }

    // ---- the answer --------------------------------------------------------------------------

    private static JsonObject RootOf(Shape shape, string prefix)
    {
        if (prefix.Length == 0)
        {
            if (shape.Grouped)
                return new JsonObject { ["node"] = "group", ["entity"] = shape.Entity.Id };

            return shape.Roots.GetValueOrDefault(Shape.ImplicitRoot) switch
            {
                ShapeNode.Element element => new JsonObject { ["node"] = "element", ["entity"] = element.Def.Id, ["source"] = element.Source.Wire },
                _ => new JsonObject { ["node"] = "entity", ["entity"] = shape.Entity.Id },
            };
        }

        var resolved = shape.Resolve(prefix, PathUsage.Project).Path;

        if (resolved is null)
            return new JsonObject { ["node"] = "unknown" };

        if (resolved.Path is null && resolved.Addon is null)
            return resolved.Root switch
            {
                ShapeNode.Entity entity => new JsonObject { ["node"] = "entity", ["entity"] = entity.Def.Id },
                ShapeNode.Element element => new JsonObject { ["node"] = "element", ["entity"] = element.Def.Id, ["source"] = element.Source.Wire },
                ShapeNode.Array array => new JsonObject { ["node"] = "array", ["entity"] = array.Target.Id },
                ShapeNode.Scalar scalar => new JsonObject { ["node"] = "scalar", ["kind"] = Kinds.NameOf(scalar.Kind) },
                ShapeNode.GroupOutput output => new JsonObject { ["node"] = "group", ["kind"] = Kinds.NameOf(output.Kind) },
                _ => new JsonObject { ["node"] = "unknown" },
            };

        if (resolved.Root is ShapeNode.Element { } rootElement && resolved.Path is { } source && source.Wire == rootElement.Source.Wire && prefix.IndexOf('.', StringComparison.Ordinal) < 0)
            return new JsonObject { ["node"] = "element", ["entity"] = rootElement.Def.Id, ["source"] = source.Wire };

        return new JsonObject
        {
            ["node"] = "member",
            ["entity"] = resolved.Entity?.Id,
            ["kind"] = Kinds.NameOf(resolved.Kind),
        };
    }

    private static JsonObject RootOfAlias(string alias, ShapeNode node, string rest, IReadOnlyList<string> targets)
    {
        var root = new JsonObject
        {
            ["node"] = node is ShapeNode.Keyed ? "keyed" : "remote",
            ["entities"] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray()),
        };

        if (rest.Length > 0)
            root["path"] = rest;

        return root;
    }

    private static JsonObject Answered(Entry entry, JsonObject root, Collected collected)
    {
        var answer = entry.Head();

        answer["root"] = root;
        answer["forwarded"] = collected.Forwarded;
        answer["truncated"] = collected.Truncated;
        answer["children"] = new JsonArray(collected.Children.Select(child => (JsonNode)child).ToArray());

        return answer;
    }

    private static JsonObject Failed(JsonObject head, string code, string message)
    {
        head["root"] = null;
        head["forwarded"] = false;
        head["truncated"] = false;
        head["children"] = new JsonArray();
        head["error"] = Issue(code, message, head["prefix"]?.GetValue<string>() is { Length: > 0 } prefix ? prefix : null);

        return head;
    }

    private static JsonObject Issue(string code, string message, string? path)
    {
        var issue = new JsonObject { ["code"] = code, ["message"] = message };

        if (path is not null)
            issue["path"] = path;

        return issue;
    }

    /// <summary>The <c>REMOTE_UNCHECKED</c> note of a describe no owner answered.</summary>
    private static Diagnostic Unchecked(int? stage, string service, string target, string? reason, string describe) => new()
    {
        Code = Notes.RemoteUnchecked,
        Message = $"The owner '{service}' did not describe '{target}' ({reason ?? "no answer"}); what lies under it is not described here.",
        Stage = stage,
        Params = new Dictionary<string, object?> { ["service"] = service, ["target"] = target, ["reason"] = reason ?? "unanswered", ["describe"] = describe },
    };

    private static string LastSegment(string wire)
    {
        var dot = wire.LastIndexOf('.');

        return dot < 0 ? wire : wire[(dot + 1)..];
    }
}
