using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Addon;

namespace OxQL.Core.Binding;

/// <summary>
/// Where explain sends a <c>catalog</c> entry this host cannot answer: an entity of another service,
/// answered by its owner's internal explain. A lookup no owner answered is answered with an error,
/// never a refusal.
/// </summary>
public interface IExplainOwners
{
    /// <summary>Whether the service behind <paramref name="service"/> can be asked at all (an <c>InternalHosts</c> entry).</summary>
    bool Knows(string service);

    /// <summary>
    /// Per entry, the owner's whole explain answer to one catalog entry of its own entity (its
    /// <c>catalog[0]</c> with the types and flag sets it points to), or null when it did not answer;
    /// <c>Reason</c> then says why (<c>unsupported</c>, <c>unreachable</c>, <c>timeout</c>, <c>limit</c>,
    /// <c>cached</c>). The entries of one owner travel in one call; an entity of a service this host
    /// does not know is asked of the owner that reaches it.
    /// </summary>
    Task<IReadOnlyList<(JsonObject? Answer, string? Reason)>> CatalogAsync(IReadOnlyList<(string Service, JsonObject Entry, int Depth)> entries, CancellationToken cancellationToken);
}

/// <summary>
/// What may be done with one member at one point of the pipeline, as <see cref="Shape.Resolve"/>
/// answers it for every usage: the operators a condition admits (one source for filterable and
/// operators: a member is filterable exactly when it admits one), whether it sorts, groups, unwinds and
/// projects, whether a string comparison folds, the collections above it that are not unwound, and how
/// a resolve may follow its reference. Equal flags are one set of the answer, under an id that is the
/// flags themselves (<see cref="Id"/>), so every engine names a set the same way.
/// </summary>
/// <param name="Operators">The admitted operators, one bit each in <see cref="ExplainTypes.OperatorOrder"/>.</param>
/// <param name="Sortable">Whether a sort can use the member here.</param>
/// <param name="Groupable">Whether a group key can name it here.</param>
/// <param name="Unwindable">Whether an unwind can take it here.</param>
/// <param name="Projectable">Whether a projection can name it here.</param>
/// <param name="Folds">Whether a string comparison on it folds case and accents by default.</param>
/// <param name="UnderCollection">The collections above it that are not unwound: a condition through one matches when some element does.</param>
/// <param name="Follow">0: no reference; 1: followed as one value; 2: element-wise (<c>elements</c>); 3: not followable here.</param>
public readonly record struct MemberFlags(int Operators, bool Sortable, bool Groupable, bool Unwindable, bool Projectable, bool Folds, int UnderCollection, int Follow)
{
    private static readonly string?[] FollowNames = [null, "one", "elements", "none"];

    /// <summary>The id of the set: the flags packed into one number, in lower-case hexadecimal.</summary>
    public string Id =>
        (Operators
            | (Sortable ? 1 << 15 : 0) | (Groupable ? 1 << 16 : 0) | (Unwindable ? 1 << 17 : 0) | (Projectable ? 1 << 18 : 0) | (Folds ? 1 << 19 : 0)
            | (Follow << 20) | (Math.Min(UnderCollection, 255) << 22)).ToString("x", CultureInfo.InvariantCulture);

    /// <summary>The flags an id names, or null when it is not one.</summary>
    public static MemberFlags? FromId(string? id)
    {
        if (id is null || !int.TryParse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var packed) || packed < 0)
            return null;

        return new MemberFlags(packed & 0x7FFF, (packed & (1 << 15)) != 0, (packed & (1 << 16)) != 0, (packed & (1 << 17)) != 0, (packed & (1 << 18)) != 0, (packed & (1 << 19)) != 0,
            packed >> 22, (packed >> 20) & 3);
    }

    /// <summary>The set as the answer writes it.</summary>
    public JsonObject ToJson()
    {
        var operators = new JsonArray();

        for (var bit = 0; bit < ExplainTypes.OperatorOrder.Count; bit++)
            if ((Operators & (1 << bit)) != 0)
                operators.Add(ExplainTypes.OperatorOrder[bit]);

        var flags = new JsonObject
        {
            ["operators"] = operators,
            ["sortable"] = Sortable,
            ["groupable"] = Groupable,
            ["unwindable"] = Unwindable,
            ["projectable"] = Projectable,
            ["folds"] = Folds,
            ["underCollection"] = UnderCollection,
        };

        if (FollowNames[Follow] is { } follow)
            flags["follow"] = follow;

        return flags;
    }
}

/// <summary>
/// The shape side of an explain answer: the <c>types</c> the roots point to, the shape of the row
/// after each stage with the <c>rules</c> that say where each root's members stand there, and the
/// <c>catalog</c> lookups of entities outside the query.
/// <para>
/// By default a type is a reference: the entity (or the element of an item collection on it), the
/// service that owns it and the revision of that service's schema document, which describes every
/// member. What the query changes is said per root and stage as a rule (<see cref="Rule"/>): a reader
/// of the document derives a member's flags from its descriptor and the rule. With
/// <c>include: "types"</c> (<c>tables</c>) the answer also writes the members out: the member rows of
/// each type with their own flags, the <c>flagSets</c>, and per root and stage the override set of
/// the members that differ.
/// </para>
/// <para>
/// Everything a member can do at a point of the pipeline is what <see cref="Shape.Resolve"/> answers
/// for it there, for every usage: the resolution code a run binds with is the flag engine, nothing is
/// derived a second way. A type row carries the flags of the member in its type's own entry shape; a
/// stage shape carries, per root, only where a member differs from that (an unwound collection, a
/// lookup's array, a join taken after the page) or is not in the row (a projection, a join's select).
/// Types of another service are never built here: they are taken from the owner's answer
/// (<see cref="Import"/>), and their flags under an alias are this host's again.
/// </para>
/// <para>
/// A type of this host is built once per model and kept (its members do not change while the model
/// lives), unless its entity carries addon definitions, which are the asking organisation's. Nothing is
/// read from the database but the addon definitions of an entity the pipeline did not enter.
/// </para>
/// </summary>
public sealed class ExplainTypes
{
    /// <summary>The order <c>operators</c> lists the operators in: the operator table's, then <c>any</c>.</summary>
    public static readonly IReadOnlyList<string> OperatorOrder =
        ["eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex", "is", "any"];

    /// <summary>The members a catalog entry may carry.</summary>
    public static readonly IReadOnlyList<string> CatalogMembers = ["id", "entity", "prefix", "depth", "referencing"];

    /// <summary>
    /// The position of each fact in a type's member row: <c>[path, kind, nullable, flags, more, onlyFor, reference, addon, description]</c>.
    /// What every member has comes first, then the facts by how often a member has them; a row ends at its
    /// last fact, so most rows hold four.
    /// </summary>
    public static class Row
    {
        /// <summary>The member's path below the type, dotted.</summary>
        public const int Path = 0;

        /// <summary>The kind.</summary>
        public const int Kind = 1;

        /// <summary>1 when a client can read null out of the member, else 0.</summary>
        public const int Nullable = 2;

        /// <summary>The id of the member's own flags in <c>flagSets</c>.</summary>
        public const int Flags = 3;

        /// <summary>
        /// The rarer facts: <c>leafKind</c>, <c>displayName</c>, <c>stored</c> (false), <c>collection</c> (a dictionary stored as one),
        /// <c>enum</c>, <c>variants</c>, <c>flatten</c>, <c>snapshotOf</c>, <c>deprecated</c>, <c>constraints</c>, and <c>children</c>
        /// (the member has members the rows do not list: a catalog entry with its path as <c>prefix</c> reads them).
        /// </summary>
        public const int More = 4;

        /// <summary>The index of the variants that carry the member in the type's <c>onlyFor</c> list (the names themselves in a catalog answer's rows); null on a member every value has.</summary>
        public const int OnlyFor = 5;

        /// <summary>The reference the member declares.</summary>
        public const int Reference = 6;

        /// <summary>The addon definition the member binds through.</summary>
        public const int Addon = 7;

        /// <summary>The description; only with <c>include: "docs"</c>.</summary>
        public const int Description = 8;
    }

    /// <summary>The key of an override set that says what every member not named has (<c>null</c>: not in the row).</summary>
    public const string Every = "*";

    /// <summary>The key of an override set that maps a member's own flags to the flags it has here.</summary>
    public const string ByOwn = "~";

    /// <summary>The key of an override set that holds the flags of the root itself.</summary>
    public const string Self = "";

    /// <summary>A type of this host as it was built: its entry, and its members with their own flags.</summary>
    private sealed record Template(JsonObject Entry, IReadOnlyList<(string Path, MemberFlags Flags)> Members);

    private static readonly ConditionalWeakTable<EntityModel, ConcurrentDictionary<string, Template>> Built = [];

    private readonly EntityModel model;
    private readonly RequestContext context;
    private readonly BindTrace? trace;
    private readonly int depth;
    private readonly int memberCap;
    private readonly bool docs;
    private readonly bool tables;
    private readonly string? revision;
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<string, IReadOnlyList<AddonDefinition>> loadedAddons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<(string Path, MemberFlags? Flags)>> members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> overrideIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> ruleIds = new(StringComparer.Ordinal);
    private readonly List<(string Root, ShapeNode Node, object? Included, object? Excluded, object Unset, string Unwound, string? Type, string? Id)> rootFlags = [];

    /// <summary>
    /// The types engine of one explain. With <paramref name="tables"/> the member rows are written out,
    /// <paramref name="depth"/> levels below each root and with <paramref name="docs"/> each member's
    /// description; without, a type is a reference to the schema document at <paramref name="revision"/>.
    /// </summary>
    public ExplainTypes(EntityModel model, RequestContext context, BindTrace? trace, int depth, bool docs = false, CancellationToken cancellationToken = default, bool tables = true, string? revision = null)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.trace = trace;
        this.depth = Math.Max(1, depth);
        this.docs = docs;
        this.tables = tables;
        this.revision = revision;
        memberCap = Math.Max(1, context.Options.Explain.MaxTypeMembers);
        this.cancellationToken = cancellationToken;
    }

    /// <summary>The type table: <c>t:&lt;entity&gt;[#item]</c> and <c>u:&lt;alias&gt;</c> entries, in the order they were first needed.</summary>
    public JsonObject Types { get; } = [];

    /// <summary>The flag rules (<c>r:n</c>): where the members of one root stand at one shape.</summary>
    public JsonObject Rules { get; } = [];

    /// <summary>Whether the member rows, the flag sets and the overrides are written out.</summary>
    public bool Tables => tables;

    /// <summary>The flag sets: one member's flags under their id, and under <c>o:n</c> the overrides of one root at one shape.</summary>
    public JsonObject FlagSets { get; } = [];

    /// <summary>The member rows the table holds, the unions' included: what an answer's size is estimated by.</summary>
    public int MemberCount => members.Values.Sum(list => list.Count);

    /// <summary>The key of a concrete type: the entity, or the element of an item collection on it.</summary>
    public static string TypeKey(string entity, string? item) => item is null ? "t:" + entity : "t:" + entity + "#" + item;

    /// <summary>The key of a target as a declaration spells it (<c>entity</c> or <c>entity#item</c>).</summary>
    public static string TypeKey(string target) => "t:" + target;

    /// <summary>The key of the union type of an alias with several targets.</summary>
    public static string UnionKey(string alias) => "u:" + alias;

    /// <summary>The pointer of a root that is one value of a kind, not a row.</summary>
    public static string KindKey(Kind kind) => "k:" + Kinds.NameOf(kind);

    // ---- local types ---------------------------------------------------------------------------

    /// <summary>
    /// The type of an entity of this host, or of the element of <paramref name="item"/> on it: its key,
    /// or null when <paramref name="item"/> is no collection of objects of the entity.
    /// </summary>
    public async ValueTask<string?> LocalAsync(EntityDef entity, string? item)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var key = TypeKey(entity.Id, item);

        if (Types.ContainsKey(key))
            return key;

        var shape = await EntryShapeAsync(entity).ConfigureAwait(false);

        if (!tables)
        {
            if (item is not null && ElementOf(shape, item) is null)
                return null;

            Types[key] = Reference(entity, item);

            return key;
        }

        // The asking organisation's addon definitions are members of the type: such a type is built for this answer alone.
        var shared = !shape.Addons.TryGetValue(entity.Id, out var definitions) || definitions.Count == 0;
        var cache = shared ? Built.GetValue(model, _ => new ConcurrentDictionary<string, Template>(StringComparer.Ordinal)) : null;
        var cacheKey = $"{key}|{depth}|{memberCap}|{(docs ? 1 : 0)}";

        if (cache is null || !cache.TryGetValue(cacheKey, out var template))
        {
            if (Build(entity, item, shape) is not { } built)
                return null;

            template = built;
            cache?.TryAdd(cacheKey, template);
        }

        // The kept entry is never changed: an answer takes a copy of its members beside the reference.
        var written = Reference(entity, item);

        foreach (var (name, value) in template.Entry)
            if (!written.ContainsKey(name))
                written[name] = value?.DeepClone();

        Types[key] = written;
        members[key] = template.Members.Select(member => (member.Path, (MemberFlags?)member.Flags)).ToList();

        foreach (var (_, flags) in template.Members)
            Set(flags);

        return key;
    }

    /// <summary>
    /// A type as a reference: the entity, the item collection whose element it is, the service that owns
    /// the entity and the revision of the schema document that service publishes (absent when it publishes none).
    /// </summary>
    private JsonObject Reference(EntityDef entity, string? item)
    {
        var reference = new JsonObject { ["entity"] = entity.Id };

        if (item is not null)
            reference["item"] = item;

        reference["service"] = entity.Namespace;

        if (revision is not null)
            reference["schemaRevision"] = revision;

        return reference;
    }

    /// <summary>The collection <paramref name="item"/> of the entity whose elements are objects, with their type; null when it is none.</summary>
    private static (ResolvedPath Collection, TypeDef Type)? ElementOf(Shape shape, string item)
    {
        if (item.Length == 0 || shape.Resolve(item, PathUsage.Unwind) is not { Succeeded: true, Path: { Path: not null } collection })
            return null;

        var element = collection.Path!.Shape.Kind switch
        {
            Kind.Array => collection.Path.Shape.Of,
            Kind.Dictionary => collection.Path.Shape.Value,
            _ => null,
        };

        return element is { Kind: Kind.Object, Type: { } elementType } ? (collection, elementType) : null;
    }

    private Template? Build(EntityDef entity, string? item, Shape shape)
    {
        var type = entity.Root;

        if (item is not null)
        {
            if (ElementOf(shape, item) is not { } found)
                return null;

            shape = shape.ForElement(found.Collection);
            type = found.Type;
        }

        var entry = new JsonObject { ["entity"] = entity.Id };

        if (item is not null)
            entry["item"] = item;

        if (type.Variants.Count > 0)
            entry["variants"] = new JsonArray(VariantNames(type).Select(name => (JsonNode)name).ToArray());

        if (docs && (item is null ? entity.Root.Description : type.Description) is { } description)
            entry["description"] = description;

        var onlyFor = new List<string>();
        var (rows, flags, truncated) = Rows(shape, "", depth, onlyFor);

        if (onlyFor.Count > 0)
            entry["onlyFor"] = new JsonArray(onlyFor.Select(set => (JsonNode)new JsonArray(set.Split('\n').Select(name => (JsonNode)name).ToArray())).ToArray());

        entry["members"] = rows;

        if (truncated)
            entry["truncated"] = true;

        // Kept as parsed text: a copy of it costs nothing until an answer reads it, and is written as it stands.
        return new Template(JsonNode.Parse(entry.ToJsonString())!.AsObject(), flags);
    }

    /// <summary>
    /// The member rows below <paramref name="prefix"/> to <paramref name="levels"/> levels, level by level in
    /// walk order, with each member's own flags, and whether the cap cut them. <paramref name="onlyFor"/>
    /// collects the distinct variant sets the rows point to (null: a row names its variants itself).
    /// </summary>
    private (JsonArray Rows, List<(string Path, MemberFlags Flags)> Flags, bool Truncated) Rows(Shape shape, string prefix, int levels, List<string>? onlyFor)
    {
        var rows = new JsonArray();
        var flags = new List<(string, MemberFlags)>();
        var queue = new List<(string Prefix, int Level)> { (prefix, 1) };
        // The members that have members of their own which the rows do not list: below the depth, or past the cap.
        var cut = new List<string>();
        var truncated = false;

        for (var position = 0; position < queue.Count && !truncated; position++)
        {
            var (at, level) = queue[position];

            foreach (var (_, path) in ChildPaths(shape, at))
            {
                if (rows.Count >= memberCap)
                {
                    truncated = true;
                    break;
                }

                var (row, own, children) = RowOf(shape, path, onlyFor);

                rows.Add(row);
                flags.Add((path, own));

                if (children && level < levels)
                    queue.Add((path, level + 1));
                else if (children)
                    cut.Add(path);
            }

            if (truncated)
                cut.AddRange(queue.Skip(position).Select(pending => pending.Prefix).Where(pending => pending.Length > prefix.Length));
        }

        Finish(rows, cut);

        return (rows, flags, truncated);
    }

    /// <summary>One member row (<see cref="Row"/>): the member's facts and the id of its flags in <paramref name="shape"/>; a row ends at its last fact.</summary>
    private (JsonArray Row, MemberFlags Flags, bool Children) RowOf(Shape shape, string path, List<string>? onlyFor)
    {
        var project = shape.Resolve(path, PathUsage.Project);
        var match = shape.Resolve(path, PathUsage.Match);
        var facts = project.Path ?? match.Path ?? shape.Resolve(path, PathUsage.Sort).Path;
        var member = facts is null ? null : MemberAt(facts, path);
        var kind = facts?.Kind ?? Kind.Unknown;
        var leafKind = facts?.Shape?.LeafKind ?? kind;
        var variantHolder = facts is null ? null : OperandCoercer.VariantHolder(facts);
        var children = HasChildren(shape, facts, path);
        var flags = FlagsAt(shape, path);
        var more = new JsonObject();

        if (leafKind != kind)
            more["leafKind"] = Kinds.NameOf(leafKind);

        if ((member?.DisplayName ?? facts?.Addon?.DisplayName) is { } displayName)
            more["displayName"] = displayName;

        if (!(facts?.Path is { } pathDef ? pathDef.Stored : facts is not null))
            more["stored"] = false;

        // An array is a collection by its kind; a dictionary stored as one says so.
        if (kind == Kind.Dictionary && facts?.Path is { } collectionPath && facts.Addon is null && Shape.IsCollection(collectionPath))
            more["collection"] = true;

        if (EnumOf(facts) is { } enumValues)
            more["enum"] = enumValues;

        if (variantHolder is { Variants.Count: > 0 })
            more["variants"] = new JsonArray(VariantNames(variantHolder).Select(variant => (JsonNode)variant).ToArray());

        if (FlattensOf(facts) is { Count: > 0 } flattens)
        {
            more["flatten"] = flattens[0];

            if (flattens.Count > 1)
                more["flattenMembers"] = new JsonArray(flattens.Select(flatten => (JsonNode)flatten).ToArray());
        }

        if (SnapshotOf(facts) is { } snapshot)
            more["snapshotOf"] = snapshot;

        if (member?.Deprecated is { } deprecated)
            more["deprecated"] = Deprecation(deprecated);

        if (member?.Constraints is { } constraints)
            more["constraints"] = ConstraintsOf(constraints);

        JsonNode? variants = null;

        if (member?.OnlyFor is { Count: > 0 } only)
        {
            if (onlyFor is null)
            {
                variants = new JsonArray(only.Select(variant => (JsonNode)variant).ToArray());
            }
            else
            {
                var set = string.Join('\n', only);
                var at = onlyFor.IndexOf(set);

                if (at < 0)
                {
                    at = onlyFor.Count;
                    onlyFor.Add(set);
                }

                variants = at;
            }
        }

        var row = new JsonArray
        {
            path,
            Kinds.NameOf(kind),
            (member?.Nullable ?? facts?.Addon is not null) ? 1 : 0,
            flags.Id,
            more.Count > 0 ? more : null,
            variants,
            ReferenceOf(facts),
            AddonOf(facts?.Addon),
            docs ? member?.Description ?? facts?.Addon?.Description : null,
        };

        return (row, flags, children);
    }

    /// <summary>Marks a member whose members the rows do not list (<c>more.children</c>), and ends each row at its last fact.</summary>
    private static void Finish(JsonArray rows, List<string> cut)
    {
        foreach (var row in rows.OfType<JsonArray>())
        {
            if (cut.Contains(row[Row.Path]!.GetValue<string>(), StringComparer.Ordinal))
            {
                if (row[Row.More] is not JsonObject more)
                    row[Row.More] = more = [];

                more["children"] = true;
            }

            while (row.Count > Row.Flags + 1 && row[^1] is null)
                row.RemoveAt(row.Count - 1);
        }
    }

    // ---- flags = Shape.Resolve over paths × usages ---------------------------------------------

    /// <summary>What may be done with <paramref name="path"/> at <paramref name="shape"/>: <see cref="Shape.Resolve"/> for every usage.</summary>
    public static MemberFlags FlagsAt(Shape shape, string path)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var project = shape.Resolve(path, PathUsage.Project);
        var match = shape.Resolve(path, PathUsage.Match);
        var sort = shape.Resolve(path, PathUsage.Sort);
        var unwind = shape.Resolve(path, PathUsage.Unwind);
        var group = shape.Resolve(path, PathUsage.GroupKey);
        var matched = match.Path;
        var facts = project.Path ?? matched ?? sort.Path;

        return new MemberFlags(
            Operators(match),
            sort is { Succeeded: true, Path.Sortable: true },
            group is { Succeeded: true, Path: { CollectionAncestors: 0 } key } && key.Kind != Kind.Array && Kinds.IsScalar(key.Kind),
            Unwindable(unwind),
            project.Succeeded,
            matched is { Filterable: true, Root: not ShapeNode.Outcome } && !matched.IsRemote && OperandCoercer.FoldsByDefault("eq", matched),
            project.Path?.CollectionAncestors ?? 0,
            facts?.Path?.References is { Count: > 0 } && facts.Addon is null ? Followable(Crossed(shape, path)) : 0);
    }

    private const int ExistsBit = 1 << 11;
    private const int IsBit = 1 << 13;
    private const int AnyBit = 1 << 14;

    /// <summary>
    /// A member of an owner's type under a remote alias of this host: what the origin's shape allows
    /// there. A condition is a semi-join on the owner (it compares values, never <c>is</c> or <c>any</c>),
    /// only where the alias can be narrowed beforehand; the rows are joined after the page, so nothing
    /// sorts, groups or unwinds. The collections the path crosses are the owner's, which a resolve
    /// continued there follows with <c>elements</c>.
    /// </summary>
    private static MemberFlags Rebased(MemberFlags own, (bool Filterable, bool Sortable, bool Projectable) origin)
    {
        var operators = origin.Filterable ? own.Operators & ~(IsBit | AnyBit) : 0;

        return new MemberFlags(operators, origin.Sortable, Groupable: false, Unwindable: false, origin.Projectable, operators != 0 && own.Folds, own.UnderCollection, own.Follow);
    }

    /// <summary>
    /// What the origin's shape answers for a path below a remote alias. It answers every such path alike
    /// (it knows no member of an owner's row), so one path of the alias is asked for all of them.
    /// </summary>
    private static (bool Filterable, bool Sortable, bool Projectable) Below(Shape origin, string path) =>
        (origin.Resolve(path, PathUsage.Match) is { Succeeded: true, Path.Filterable: true },
            origin.Resolve(path, PathUsage.Sort) is { Succeeded: true, Path.Sortable: true },
            origin.Resolve(path, PathUsage.Project).Succeeded);

    /// <summary>The id of a member's flags, the set written once.</summary>
    private string Set(MemberFlags flags)
    {
        var id = flags.Id;

        if (!FlagSets.ContainsKey(id))
            FlagSets[id] = flags.ToJson();

        return id;
    }

    /// <summary>The id of one root's overrides at one shape, the same map once.</summary>
    private string InternOverrides(JsonObject overrides)
    {
        var text = overrides.ToJsonString();

        if (overrideIds.TryGetValue(text, out var id))
            return id;

        id = "o:" + (overrideIds.Count + 1);
        overrideIds[text] = id;
        FlagSets[id] = overrides;

        return id;
    }

    // ---- the shape of the row at one point ------------------------------------------------------

    /// <summary>
    /// The row at <paramref name="shape"/>: each root it carries with its type, and per root the override
    /// set of the members that differ here from their type's own flags or are not in the row.
    /// <paramref name="ownerTypes"/> answers the type of a remote or continued alias (from its owners'
    /// answers), null for one no owner answered.
    /// </summary>
    public async ValueTask<ExplainShape> ShapeAsync(Shape shape, Func<string, string?> ownerTypes)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(ownerTypes);

        var roots = new JsonObject();
        var rules = new JsonObject();
        var flags = tables ? new JsonObject() : null;
        Shape? plain = null;

        foreach (var (name, node) in shape.Roots)
        {
            if (node is ShapeNode.Poisoned)
                continue;

            if (name != Shape.ImplicitRoot && (!shape.Carries(name) || !shape.IsVisible(name)))
                continue;

            var type = await RootTypeAsync(name, node, ownerTypes).ConfigureAwait(false);

            roots[name] = type;
            plain ??= shape.Unprojected();

            if (Rule(shape, plain, name, node) is { } rule)
                rules[name] = rule;

            if (flags is not null && RootFlags(shape, ref plain, name, node, type) is { } overrides)
                flags[name] = overrides;
        }

        var removed = (shape.Excluded ?? Enumerable.Empty<string>()).Concat(shape.Unset.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        return new ExplainShape
        {
            Paging = shape.IsRootShape ? "cursor" : "offset",
            Grouped = shape.Grouped,
            Unwound = shape.Unwound
                .Select(key => key.Split('|', 2))
                .Select(parts => parts[0].Length == 0 ? parts[1] : parts[0] + "." + parts[1])
                .Order(StringComparer.Ordinal)
                .ToList(),
            Projection = shape.Included?.Order(StringComparer.Ordinal).ToList(),
            Removed = removed.Count == 0 ? null : removed,
            Roots = roots,
            Rules = rules,
            Flags = flags,
        };
    }

    /// <summary>
    /// The rule of one root at one shape, or null when its members stand as in their type: what a reader
    /// of the schema document needs beside a member's descriptor to say what can be done with it here.
    /// <list type="bullet">
    /// <item><c>self</c>: the flags of the root itself, as a flag id (a named root only).</item>
    /// <item><c>under</c>: <c>collection</c> for a lookup's array (every member lies under one more
    /// collection), <c>afterPage</c> for rows joined after the page from local targets (a member is read
    /// and followed, nothing else; with <c>many</c> the alias holds an array of them), <c>owner</c> for
    /// rows another service holds (with <c>filter</c> a condition may compare a member's value, as a
    /// semi-join on the owner; nothing sorts, groups or unwinds).</item>
    /// <item><c>unwound</c>: the collections below the root that are unwound here, relative to it: such a
    /// collection is its element, and what lies below it lies under one collection fewer.</item>
    /// <item><c>unwoundAbove</c>: for the element of a nested collection, how many of the collections
    /// above its own are unwound here: its members lie under that many fewer than in their type.</item>
    /// <item><c>shows</c>: the paths the row carries under a join's alias, where that is not every member.</item>
    /// </list>
    /// What a projection kept or removed is the shape's (<c>projection</c>, <c>removed</c>), not the rule's.
    /// </summary>
    private string? Rule(Shape shape, Shape plain, string root, ShapeNode node)
    {
        var named = root != Shape.ImplicitRoot;
        var rule = new JsonObject();

        if (named)
            rule["self"] = FlagsAt(plain, root).Id;

        IEnumerable<string> unwound = [];
        IReadOnlyList<string>? shown = null;

        switch (node)
        {
            case ShapeNode.Array array:
                rule["under"] = "collection";
                shown = array.Select;
                break;

            case ShapeNode.Keyed keyed:
                rule["under"] = "afterPage";

                if (keyed.Many)
                    rule["many"] = true;
                break;

            case ShapeNode.Remote:
                rule["under"] = "owner";

                // The origin answers every path below a remote alias alike, so one stands for all.
                if (Below(plain, root + ".id").Filterable)
                    rule["filter"] = true;
                break;

            case ShapeNode.Entity entity:
                unwound = shape.Unwound.Where(key => key.StartsWith(root + "|", StringComparison.Ordinal)).Select(key => key[(root.Length + 1)..]);
                shown = entity.Select;
                break;

            // An element's members are resolved by their paths on the entity, whatever the alias is called.
            case ShapeNode.Element element:
                var below = "|" + element.Source.Wire + ".";

                unwound = shape.Unwound.Where(key => key.StartsWith(below, StringComparison.Ordinal)).Select(key => key[below.Length..]);

                if (UnwoundAbove(shape, element) is > 0 and var outer)
                    rule["unwoundAbove"] = outer;
                break;
        }

        if (unwound.Order(StringComparer.Ordinal).ToList() is { Count: > 0 } collections)
            rule["unwound"] = new JsonArray(collections.Select(path => (JsonNode)path).ToArray());

        if (shown is not null)
            rule["shows"] = new JsonArray(shown.Select(path => (JsonNode)path).ToArray());

        if (rule.Count == 0)
            return null;

        var text = rule.ToJsonString();

        if (ruleIds.TryGetValue(text, out var id))
            return id;

        id = "r:" + (ruleIds.Count + 1);
        ruleIds[text] = id;
        Rules[id] = rule;

        return id;
    }

    /// <summary>The type a root points to: a local type, the union of a keyed alias's targets, a kind, or what its owners said.</summary>
    public async ValueTask<string?> RootTypeAsync(string name, ShapeNode node, Func<string, string?> ownerTypes)
    {
        ArgumentNullException.ThrowIfNull(ownerTypes);

        switch (node)
        {
            case ShapeNode.Entity entity:
                return await LocalAsync(entity.Def, null).ConfigureAwait(false);

            case ShapeNode.Array array:
                return await LocalAsync(array.Target, null).ConfigureAwait(false);

            case ShapeNode.Element element:
                var elementShape = element.Source.Shape.Kind switch
                {
                    Kind.Array => element.Source.Shape.Of,
                    Kind.Dictionary => element.Source.Shape.Value,
                    _ => element.Source.Shape,
                };

                return elementShape is { Kind: Kind.Object, Type: not null }
                    ? await LocalAsync(element.Def, element.Source.Wire).ConfigureAwait(false) ?? KindKey(Kind.Object)
                    : KindKey(elementShape?.Kind ?? Kind.Unknown);

            case ShapeNode.Keyed keyed:
                var targets = new List<string>();

                foreach (var target in keyed.Targets)
                    if (await LocalAsync(target.Entity, target.Item?.Wire).ConfigureAwait(false) is { } local && !targets.Contains(local, StringComparer.Ordinal))
                        targets.Add(local);

                return targets.Count switch
                {
                    0 => null,
                    1 => targets[0],
                    _ => Union(name, targets),
                };

            case ShapeNode.Remote:
                return ownerTypes(name);

            case ShapeNode.Scalar scalar:
                return KindKey(scalar.Kind);

            case ShapeNode.Outcome:
                return KindKey(Kind.String);

            case ShapeNode.GroupOutput output:
                return KindKey(output.Kind);

            default:
                return null;
        }
    }

    /// <summary>
    /// For the element of a nested collection, the collections above its own that are unwound at
    /// <paramref name="shape"/>: the element's type counts them above each member (its entry shape has
    /// nothing unwound but the collection itself), the row here does not.
    /// </summary>
    private static int UnwoundAbove(Shape shape, ShapeNode.Element element)
    {
        var segments = element.Source.Wire.Split('.');
        var above = 0;

        for (var length = 1; length < segments.Length; length++)
        {
            var wire = string.Join('.', segments.Take(length));

            if (element.Def.Path(wire) is { } path && Shape.IsCollection(path) && shape.Unwound.Contains(Shape.UnwoundKey(Shape.ImplicitRoot, wire)))
                above++;
        }

        return above;
    }

    /// <summary>
    /// The union type of an alias with several targets, built from the targets' rows: each member with
    /// the targets that have it (indexes into <c>of</c>, left out when every target has it) and
    /// <c>unknown</c> where their kinds disagree. A member's facts are those of the first target that
    /// has it, as a path under a keyed alias resolves on the first target that has it.
    /// </summary>
    public string Union(string alias, IReadOnlyList<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var key = UnionKey(alias);

        if (Types[key] is JsonObject known && known["of"] is JsonArray of && of.Select(node => node!.GetValue<string>()).SequenceEqual(targets, StringComparer.Ordinal))
            return key;

        // By reference a union is its targets: a reader merges their members, each from the first target that has it.
        if (!tables)
        {
            Types[key] = new JsonObject { ["of"] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray()) };

            return key;
        }

        var rows = new List<(string Path, string? Kind, List<int> Have, MemberFlags? Flags)>();
        var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = false;

        for (var index = 0; index < targets.Count; index++)
        {
            if (Types[targets[index]] is not JsonObject type || type["members"] is not JsonArray written)
                continue;

            truncated |= type["truncated"]?.GetValue<bool>() == true;

            var own = members.GetValueOrDefault(targets[index]);

            for (var position = 0; position < written.Count; position++)
            {
                var row = written[position]!.AsArray();
                var path = row[Row.Path]!.GetValue<string>();
                var kind = row[Row.Kind]!.GetValue<string>();

                if (byPath.TryGetValue(path, out var at))
                {
                    rows[at].Have.Add(index);

                    if (rows[at].Kind != kind)
                        rows[at] = (path, null, rows[at].Have, rows[at].Flags);
                }
                else
                {
                    byPath[path] = rows.Count;
                    rows.Add((path, kind, [index], own is not null && position < own.Count ? own[position].Flags : null));
                }
            }
        }

        var entry = new JsonObject
        {
            ["of"] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray()),
            ["members"] = new JsonArray(rows.Select(row =>
            {
                var member = new JsonArray(row.Path);

                if (row.Have.Count < targets.Count || row.Kind is null)
                    member.Add(new JsonArray(row.Have.Select(index => (JsonNode)index).ToArray()));

                if (row.Kind is null)
                    member.Add(Kinds.NameOf(Kind.Unknown));

                return (JsonNode)member;
            }).ToArray()),
        };

        if (truncated)
            entry["truncated"] = true;

        Types[key] = entry;
        members[key] = rows.Select(row => (row.Path, row.Flags)).ToList();

        return key;
    }

    /// <summary>
    /// The override set of one root at one shape, or null when every member has its type's own flags:
    /// <see cref="Self"/> the root itself, a path a member that differs here, and <c>null</c> for one that is
    /// not in the row (a projection removed it, or the alias holds only part of its target). Where most members
    /// differ the same way the set says it once: under <see cref="Every"/> what every member not named has,
    /// or under <see cref="ByOwn"/> what a member has here by the flags it has of its own.
    /// </summary>
    private string? RootFlags(Shape shape, ref Shape? plain, string root, ShapeNode node, string? type)
    {
        var named = root != Shape.ImplicitRoot;
        var unwound = string.Join('\n', shape.Unwound
            .Where(key => key.StartsWith(root + "|", StringComparison.Ordinal)
                || (node is ShapeNode.Element element && key.StartsWith("|" + element.Source.Wire + ".", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal));

        foreach (var known in rootFlags)
            if (known.Root == root && ReferenceEquals(known.Node, node) && ReferenceEquals(known.Included, shape.Included) && ReferenceEquals(known.Excluded, shape.Excluded)
                && ReferenceEquals(known.Unset, shape.Unset) && known.Unwound == unwound && known.Type == type)
                return known.Id;

        plain ??= shape.Unprojected();

        var overrides = new JsonObject();

        if (named)
            overrides[Self] = Set(FlagsAt(plain, root));

        // An entity or an element nothing unwound and no unwind took a collection out of resolves as its
        // type's own shape does: its members have their own flags here, and only what is in the row is asked.
        var own = node is ShapeNode.Entity or ShapeNode.Element && unwound.Length == 0
            && !shape.Unset.Keys.Any(collection => !named || collection == root || collection.StartsWith(root + ".", StringComparison.Ordinal))
            // The element of a nested collection lies under fewer collections here than in its type once the outer ones are unwound.
            && !(node is ShapeNode.Element nested && UnwoundAbove(plain, nested) > 0);
        // On the entity row an unwind changes only what lies at or below the collection it unwound.
        var touched = !own && !named && node is ShapeNode.Entity && shape.Unset.Count == 0
            ? shape.Unwound.Where(key => key.StartsWith('|')).Select(key => key[1..]).ToList()
            : null;
        var rows = new List<(string Path, string? Own, string? Here)>();
        (bool, bool, bool)? below = null;

        // What a join's alias holds only in part (a contract 1 select, the output set of the final row).
        var shown = node switch
        {
            ShapeNode.Entity entity => entity.Select,
            ShapeNode.Array array => array.Select,
            _ => null,
        };

        foreach (var (path, flags) in members.GetValueOrDefault(type ?? "") ?? [])
        {
            var wire = named ? root + "." + path : path;
            string? here = null;

            if (shape.IsVisible(wire) && (shown is null || RootOutput.Shows(shown, path)))
                here = own || (touched is not null && !touched.Any(collection => path == collection || (path.Length > collection.Length && path[collection.Length] == '.' && path.StartsWith(collection, StringComparison.Ordinal)))) ? flags?.Id
                    : node is ShapeNode.Remote ? flags is { } theirs ? Set(Rebased(theirs, below ??= Below(plain, wire))) : null
                    : Set(FlagsAt(plain, wire));

            rows.Add((path, flags?.Id, here));
        }

        Encode(rows, overrides);

        var id = overrides.Count == 0 ? null : InternOverrides(overrides);

        rootFlags.Add((root, node, shape.Included, shape.Excluded, shape.Unset, unwound, type, id));

        return id;
    }

    /// <summary>
    /// Writes what the members have here in the fewest entries: every member that differs from its own
    /// flags; or the commonest value once under <see cref="Every"/> and every member that differs from
    /// that; or per own flags what most such members have here under <see cref="ByOwn"/> and every member
    /// that differs from that. A reader takes the member's own entry, else the <see cref="ByOwn"/> entry of
    /// its own flags, else <see cref="Every"/>, else its own flags.
    /// </summary>
    private static void Encode(List<(string Path, string? Own, string? Here)> rows, JsonObject overrides)
    {
        var differing = rows.Where(row => row.Here != row.Own).ToList();

        if (differing.Count == 0)
            return;

        var star = rows.GroupBy(row => row.Here ?? "").OrderByDescending(group => group.Count()).First().Key is { Length: > 0 } common ? common : null;
        var byStar = rows.Where(row => row.Here != star).ToList();
        var map = rows.Where(row => row.Own is not null).GroupBy(row => row.Own!)
            .ToDictionary(group => group.Key, group => group.GroupBy(row => row.Here ?? "").OrderByDescending(heres => heres.Count()).First().Key is { Length: > 0 } here ? here : null, StringComparer.Ordinal);
        var mapped = map.Where(pair => pair.Value != pair.Key).ToList();
        var byMap = rows.Where(row => row.Here != (row.Own is null ? null : map[row.Own])).ToList();

        if (differing.Count <= Math.Min(byStar.Count + 1, mapped.Count + byMap.Count))
        {
            foreach (var row in differing)
                overrides[row.Path] = row.Here;
        }
        else if (byStar.Count + 1 <= mapped.Count + byMap.Count)
        {
            overrides[Every] = star;

            foreach (var row in byStar)
                overrides[row.Path] = row.Here;
        }
        else
        {
            overrides[ByOwn] = new JsonObject(mapped.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));

            foreach (var row in byMap)
                overrides[row.Path] = row.Here;
        }
    }

    /// <summary>Whether a lookup may name <paramref name="alias"/> as its parent row (<c>on</c>) at <paramref name="shape"/>.</summary>
    public static bool LookupParent(Shape shape, string alias)
    {
        ArgumentNullException.ThrowIfNull(shape);

        return alias != Shape.ImplicitRoot && shape.Roots.TryGetValue(alias, out var node) && shape.IsVisible(alias)
            && node is ShapeNode.Entity or ShapeNode.Keyed or ShapeNode.Remote
            && shape.Resolve(alias, PathUsage.Project).Succeeded;
    }

    // ---- owners' types --------------------------------------------------------------------------

    /// <summary>
    /// Takes the types of an owner's explain answer into this table: every entry this table does not
    /// have, moved out of the answer as it stands. An owner describes its own types, so the first
    /// answer for a type wins; a flag id names the same flags at every engine, so nothing is rewritten.
    /// </summary>
    public void Import(JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        if (answer["types"] is not JsonObject types)
            return;

        // By reference an owner's type is what the owner named: the entity, its service and the revision, or a union's targets.
        if (!tables)
        {
            // The concrete types first, as with the rows written out: both answers then list the types in one order.
            foreach (var (key, value) in types.OrderBy(pair => pair.Key.StartsWith("u:", StringComparison.Ordinal) ? 1 : 0).ToList())
            {
                if (Types.ContainsKey(key) || value is not JsonObject named)
                    continue;

                var reference = new JsonObject();

                foreach (var member in (string[])["entity", "item", "service", "schemaRevision", "of"])
                    if (named[member] is { } fact)
                        reference[member] = fact.DeepClone();

                Types[key] = reference;
            }

            return;
        }

        // The concrete types first: a union reads its targets' members.
        foreach (var key in types.Select(pair => pair.Key).OrderBy(key => key.StartsWith("u:", StringComparison.Ordinal) ? 1 : 0).ToList())
        {
            if (Types.ContainsKey(key) || types[key] is not JsonObject entry || entry["members"] is not JsonArray rows)
                continue;

            types.Remove(key);

            if (key.StartsWith("u:", StringComparison.Ordinal))
            {
                var of = entry["of"]?.AsArray().Select(node => node!.GetValue<string>()).ToList() ?? [];
                var own = new Dictionary<string, Dictionary<string, MemberFlags?>>(StringComparer.Ordinal);

                members[key] = rows.OfType<JsonArray>().Select(row =>
                {
                    var path = row[0]!.GetValue<string>();
                    var first = row.Count > 1 && row[1] is JsonArray { Count: > 0 } have ? of.ElementAtOrDefault(have[0]!.GetValue<int>()) : of.FirstOrDefault();

                    if (first is null || !members.TryGetValue(first, out var targets))
                        return (path, (MemberFlags?)null);

                    if (!own.TryGetValue(first, out var flags))
                        own[first] = flags = targets.ToDictionary(member => member.Path, member => member.Flags, StringComparer.Ordinal);

                    return (path, flags.GetValueOrDefault(path));
                }).ToList();
            }
            else
            {
                members[key] = rows.OfType<JsonArray>().Select(row =>
                {
                    var flags = MemberFlags.FromId(row.Count > Row.Flags ? row[Row.Flags]?.GetValue<string>() : null);

                    if (flags is { } read)
                        Set(read);

                    return (row[Row.Path]!.GetValue<string>(), flags);
                }).ToList();
            }

            Types[key] = entry;
        }
    }

    // ---- the catalog ----------------------------------------------------------------------------

    /// <summary>
    /// One answer per <c>catalog</c> entry of <paramref name="request"/>, in order: the one lookup of an
    /// entity outside the query (a lookup's <c>from</c>, the palette, the entities referencing one).
    /// An entity of this host points to its type in the table; with a <c>prefix</c> the answer lists
    /// the members below it instead, which is how a member the table cut (<c>more.children</c>,
    /// <c>truncated</c>) is read. An entity of another service is answered by its owner.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode>> CatalogAsync(ExplainRequest request, IExplainOwners? owners)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answers = new List<JsonNode>(request.Catalog.Count);
        var forwards = new List<Forward>();

        foreach (var entry in request.Catalog)
            answers.Add(await CatalogEntryAsync(entry, owners, forwards).ConfigureAwait(false));

        // The entities of other services are asked of their owners together: one call per owner.
        if (forwards.Count > 0)
        {
            var asked = await owners!.CatalogAsync(forwards.Select(forward => (forward.Service, forward.Entry, forward.Levels)).ToList(), cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < forwards.Count; index++)
                answers[answers.IndexOf(forwards[index].Head)] = Forwarded(forwards[index], asked[index].Answer, asked[index].Reason);
        }

        return answers;
    }

    /// <summary>A catalog entry of another service's entity on its way to its owner: the entry as sent, and the head its answer fills.</summary>
    private sealed record Forward(JsonObject Head, string Service, string EntityId, JsonObject Entry, int Levels);

    private async Task<JsonObject> CatalogEntryAsync(JsonObject raw, IExplainOwners? owners, List<Forward> forwards)
    {
        var head = new JsonObject { ["id"] = raw["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) ? id : "" };

        foreach (var (name, _) in raw)
            if (!CatalogMembers.Contains(name, StringComparer.Ordinal))
                return Failed(head, Codes.UnknownStageMember, $"'{name}' is not a member of a catalog entry; it carries {string.Join(", ", CatalogMembers)}.");

        if (raw["id"] is not JsonValue named || !named.TryGetValue<string>(out _))
            return Failed(head, Codes.UnknownStageMember, "A catalog entry names its 'id', a string the answer echoes.");

        if (raw["entity"] is not JsonValue entityValue || !entityValue.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            return Failed(head, Codes.UnknownEntity, "A catalog entry names its 'entity', an entity id, or 'entity#item' for the element of an item collection.");

        var target = text.Trim();
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var entityId = hash < 0 ? target : target[..hash];
        var item = hash < 0 ? null : target[(hash + 1)..];

        head["entity"] = target;

        string? prefix = null;

        if (raw["prefix"] is { } prefixNode)
        {
            if (prefixNode is not JsonValue prefixValue || !prefixValue.TryGetValue<string>(out prefix))
                return Failed(head, Codes.InvalidPath, "A catalog entry's 'prefix' is a path of the entity, or \"\".");

            if (prefix.Length == 0)
                prefix = null;
            else
                head["prefix"] = prefix;
        }

        var levels = depth;

        if (raw["depth"] is { } depthNode && (depthNode is not JsonValue depthValue || !depthValue.TryGetValue<int>(out levels) || levels < 1 || levels > context.Options.Explain.MaxShapeDepth))
            return Failed(head, Codes.InvalidOperand, $"A catalog entry's 'depth' is 1 to {context.Options.Explain.MaxShapeDepth}.");

        var referencing = false;

        if (raw["referencing"] is { } referencingNode && (referencingNode is not JsonValue referencingValue || !referencingValue.TryGetValue<bool>(out referencing)))
            return Failed(head, Codes.InvalidOperand, "A catalog entry's 'referencing' is true or false.");

        if (!model.TryResolve(entityId, out var entity, out _))
        {
            var service = entityId.Split('.')[0];

            if (owners is null || !owners.Knows(service))
                return Failed(head, Codes.UnknownEntity, $"'{entityId}' is not an entity of this host, and this host knows no owner for '{service}'.");

            var forwarded = (JsonObject)raw.DeepClone();

            // One id for every caller: the same lookup is one cache entry at the origin.
            forwarded["id"] = "forwarded";
            forwards.Add(new Forward(head, service, entityId, forwarded, levels));

            return head;
        }

        var type = await LocalAsync(entity, item).ConfigureAwait(false);

        if (type is null)
            return Failed(head, Codes.UnknownPath, $"'{item}' is not a collection of objects on {entity.Id}; an item target names one.");

        head["type"] = type;
        head["forwarded"] = false;

        if (prefix is not null)
        {
            var shape = await EntryShapeAsync(entity).ConfigureAwait(false);

            if (item is not null)
                shape = shape.ForElement(shape.Resolve(item, PathUsage.Unwind).Path!);

            if (shape.Resolve(prefix, PathUsage.Project) is { Succeeded: false } failed)
                return Failed(head, failed.Code!, failed.Message!, prefix);

            // By reference the schema document lists the members below the prefix; the entry said whether it is a path.
            if (tables)
            {
                // The rows of a lookup stand alone: each names its variants itself.
                var (rows, flags, truncated) = Rows(shape, prefix, levels, onlyFor: null);

                foreach (var (_, each) in flags)
                    Set(each);

                head["members"] = rows;
                head["truncated"] = truncated;
            }
        }

        if (referencing)
        {
            var referencedBy = new JsonObject();

            foreach (var (path, list) in ReferencedBy(entity.Id, item).OrderBy(pair => pair.Key, StringComparer.Ordinal))
                referencedBy[path] = new JsonArray(list.Select(each => (JsonNode)each).ToArray());

            head["referencedBy"] = referencedBy;
            // Whether this host looks up another service's entities (DESIGN §3.4.4): the entities of
            // other services that reference this one, which only their schemas list, can then be
            // joined from here with a lookup.
            head["remoteLookup"] = context.Contract == 2 && context.RemoteService is not null;
        }

        return head;
    }

    /// <summary>An entity of another service: its owner's answer to the same entry, its types taken into this table, marked forwarded.</summary>
    private JsonObject Forwarded(Forward forward, JsonObject? answer, string? reason)
    {
        var (head, service, entityId, _, _) = forward;

        if (answer?["catalog"] is not JsonArray { Count: > 0 } answered || answered[0] is not JsonObject first)
        {
            var failed = Failed(head, Codes.ResolveUnreachable, $"The owner of '{entityId}' did not answer the lookup ({reason ?? "no answer"}).");

            failed["error"]!["params"] = new JsonObject { ["service"] = service, ["reason"] = reason ?? "unanswered" };

            return failed;
        }

        Import(answer!);

        var result = (JsonObject)first.DeepClone();

        result["id"] = head["id"]!.DeepClone();
        result["forwarded"] = true;

        if (!tables)
        {
            result.Remove("members");
            result.Remove("truncated");
        }

        // The rows of a lookup point to flag sets, which this answer must hold as the owner's did.
        foreach (var row in result["members"]?.AsArray().OfType<JsonArray>() ?? [])
            if (MemberFlags.FromId(row.Count > Row.Flags ? row[Row.Flags]?.GetValue<string>() : null) is { } flags)
                Set(flags);

        return result;
    }

    private static JsonObject Failed(JsonObject head, string code, string message, string? path = null)
    {
        head["type"] = null;
        head["forwarded"] = false;

        var error = new JsonObject { ["code"] = code, ["message"] = message };

        if (path is not null)
            error["path"] = path;

        head["error"] = error;

        return head;
    }

    // ---- addons ---------------------------------------------------------------------------------

    /// <summary>The entry shape of an entity, with its organisation's addon definitions when it is extendable.</summary>
    private async ValueTask<Shape> EntryShapeAsync(EntityDef entity)
    {
        if (trace?.Entry is { } entry && entry.Addons.ContainsKey(entity.Id))
            return Shape.ForEntity(entity, entry.Addons);

        if (trace?.Final is { } final && final.Addons.ContainsKey(entity.Id))
            return Shape.ForEntity(entity, final.Addons);

        if (entity.Extendable && context.Organisation is { } organisation && !loadedAddons.ContainsKey(entity.Id))
            loadedAddons[entity.Id] = await context.AddonSource.ForEntityAsync(entity.Id, organisation, cancellationToken).ConfigureAwait(false);

        return Shape.ForEntity(entity, loadedAddons);
    }

    /// <summary>
    /// A hash of every addon definition the answer read (the pipeline's entities' and the ones this
    /// table loaded), or null when it read none: the asking organisation's addons are part of what
    /// the answer says, so its etag covers them.
    /// </summary>
    public string? AddonRevision()
    {
        var definitions = (trace?.Final.Addons ?? new Dictionary<string, IReadOnlyList<AddonDefinition>>())
            .Concat(loadedAddons)
            .SelectMany(pair => pair.Value)
            .DistinctBy(definition => definition.Id)
            .OrderBy(definition => definition.Entity, StringComparer.Ordinal)
            .ThenBy(definition => definition.Path, StringComparer.Ordinal)
            .ThenBy(definition => definition.Id)
            .ToList();

        if (definitions.Count == 0)
            return null;

        var text = new StringBuilder();

        foreach (var definition in definitions)
            text.Append(definition.Id.ToString("N")).Append('|').Append(definition.Entity).Append('|').Append(definition.Path).Append('|').Append(definition.Kind).Append('|')
                .Append(definition.Retired).Append('|').Append(definition.DisplayName).Append('|').Append(definition.Description).Append('|')
                .AppendJoin(',', definition.Values?.Select(value => value.Value + "=" + value.Label) ?? []).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    // ---- the children of a prefix ---------------------------------------------------------------

    /// <summary>The names and paths one level below <paramref name="prefix"/>: the root members for <c>""</c>.</summary>
    private static IEnumerable<(string Name, string Path)> ChildPaths(Shape shape, string prefix)
    {
        if (prefix.Length == 0)
        {
            if (!shape.Roots.TryGetValue(Shape.ImplicitRoot, out var implicitRoot))
                yield break;

            var (entity, parent) = implicitRoot switch
            {
                ShapeNode.Entity row => (row.Def, ""),
                ShapeNode.Element element => (element.Def, element.Source.Wire),
                _ => ((EntityDef?)null, ""),
            };

            if (entity is null)
                yield break;

            foreach (var path in entity.ChildrenOf(parent))
            {
                var name = LastSegment(path.Wire);

                if (name != "*")
                    yield return (name, name);
            }

            yield break;
        }

        var resolution = shape.Resolve(prefix, PathUsage.Project);

        if (resolution.Path is not { } resolved)
            yield break;

        // At an addon bag or below it: the organisation's definitions one level down.
        if (AddonAt(resolved, prefix) is { } definitionPath)
        {
            foreach (var name in AddonNames(shape, resolved.Entity, definitionPath))
                yield return (name, prefix + "." + name);

            yield break;
        }

        if (resolved.Entity is not { } owner)
            yield break;

        foreach (var path in owner.ChildrenOf(resolved.Path?.Wire ?? ""))
        {
            var name = LastSegment(path.Wire);

            if (name != "*")
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

    // ---- one member's facts ---------------------------------------------------------------------

    /// <summary>The member a resolved path ends in: its path's member, unless the path ends below it (a dictionary key) or at a root.</summary>
    private static MemberDef? MemberAt(ResolvedPath facts, string path)
    {
        if (facts.Addon is not null || facts.Path is not { } pathDef || AddonAt(facts, path) is { Length: > 0 })
            return null;

        return LastSegment(pathDef.Wire) == "*" ? null : pathDef.Member;
    }

    private static bool Unwindable(PathResolution unwind)
    {
        if (unwind.Path is not { } path)
            return false;

        if (path.Root is ShapeNode.Array && path.Path is null)
            return true;

        var collection = path.Kind == Kind.Array || (path.Path is not null && path.Addon is null && Shape.IsCollection(path.Path) && path.Kind == Kind.Dictionary);

        return collection && path.Storage is not null && path.CollectionAncestors == 0;
    }

    /// <summary>
    /// The operators a condition on the path admits, as the binder admits them (DESIGN §4.5), one bit each
    /// in <see cref="OperatorOrder"/>: the operator table's, <c>is</c> on a variant holder, <c>any</c> on a
    /// collection of objects.
    /// </summary>
    private static int Operators(PathResolution match)
    {
        // A path the binder refuses in a condition admits no operator at all.
        if (!match.Succeeded || match.Path is not { } path)
            return 0;

        var admitted = 0;

        // A join's outcome compares with its own names: equality and membership, nothing of text or order.
        if (path.Root is ShapeNode.Outcome)
        {
            for (var bit = 0; bit < OperatorOrder.Count; bit++)
                if (path.Filterable && OperatorOrder[bit] is "eq" or "neq" or "in" or "nin")
                    admitted |= 1 << bit;

            return admitted;
        }

        if (path.IsRemote)
        {
            if (path.Filterable)
                for (var bit = 0; bit < OperatorOrder.Count; bit++)
                    if (OperatorOrder[bit] is not ("is" or "any"))
                        admitted |= 1 << bit;
        }
        else if (path.Filterable)
        {
            for (var bit = 0; bit < OperatorOrder.Count; bit++)
            {
                var op = OperatorOrder[bit];

                if (op is not ("is" or "any") && OperandCoercer.Applies(op, path.LeafKind) && !(OperandCoercer.NeedsText(op) && OperandCoercer.IsCharRepresented(path)))
                    admitted |= 1 << bit;
            }
        }
        else if (path.Storage is not null)
        {
            admitted |= ExistsBit;
        }

        if (!path.IsRemote && path.CollectionAncestors == 0 && OperandCoercer.VariantHolder(path) is { Variants.Count: > 0 })
            admitted |= IsBit;

        if (!path.IsRemote && path.Path is not null && path.Kind == Kind.Array && path.Shape?.Of?.Kind == Kind.Object && path.CollectionAncestors == 0)
            admitted |= AnyBit;

        return admitted;
    }

    /// <summary>The names <c>is</c> accepts on a variant holder: the concrete base first, then every variant.</summary>
    private static IEnumerable<string> VariantNames(TypeDef type)
    {
        if (OperandCoercer.ConcreteBaseName(type) is { } concrete)
            yield return concrete;

        foreach (var variant in type.Variants)
            yield return variant.Name;
    }

    private JsonArray? EnumOf(ResolvedPath? facts)
    {
        if (facts?.Addon is { Values: { Count: > 0 } values })
            return new JsonArray(values.Select(value =>
            {
                var written = new JsonObject { ["name"] = value.Value, ["value"] = value.Value };

                if (value.Label is not null)
                    written["label"] = value.Label;

                return (JsonNode)written;
            }).ToArray());

        if (facts?.Shape?.Leaf is not { Kind: Kind.Enum, Type: { IsEnum: true } type })
            return null;

        return new JsonArray(type.EnumValues.Select(value =>
        {
            var written = new JsonObject { ["name"] = value.Name, ["value"] = value.Value };

            if (docs && value.Description is not null)
                written["description"] = value.Description;

            return (JsonNode)written;
        }).ToArray());
    }

    /// <summary>
    /// The members <c>unwind.flatten</c> may follow on a collection of objects, every member nesting
    /// the same kind of element (the binder's rule): first the one named like the collection itself
    /// (a group's <c>items</c> under <c>items</c>), which is the recursion of the collection; then the
    /// others in member order.
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

    /// <summary>How a resolve may follow a reference that crosses <paramref name="crossed"/> collections: as one value (1), element-wise (2), or not at all (3).</summary>
    private static int Followable(int crossed) => crossed switch
    {
        0 => 1,
        1 => 2,
        _ => 3,
    };

    /// <summary>The reference a path declares with every case (DESIGN §4.5).</summary>
    private static JsonObject? ReferenceOf(ResolvedPath? facts)
    {
        if (facts?.Path?.References is not { Count: > 0 } cases || facts.Addon is not null)
            return null;

        var reference = new JsonObject();

        if (cases is [{ IsSimple: true }])
            reference["simple"] = true;

        if (cases.All(declared => declared.KeyAs == KeyAs.Guid))
            reference["keyAs"] = "guid";

        reference["cases"] = new JsonArray(cases.Select(declared =>
        {
            var written = new JsonObject();

            switch (declared.When)
            {
                case ReferenceCondition.PathEquals equals:
                    written["when"] = new JsonObject { ["path"] = equals.Path, ["equals"] = new JsonArray(equals.Values.Select(value => (JsonNode)value).ToArray()) };
                    break;

                case ReferenceCondition.Variant variant:
                    written["when"] = new JsonObject { ["variant"] = new JsonArray(variant.Names.Select(name => (JsonNode)name).ToArray()) };
                    break;
            }

            if (declared.KeyAs == KeyAs.Guid)
                written["keyAs"] = "guid";

            written["targets"] = new JsonArray(declared.Targets.Select(target =>
            {
                var named = new JsonObject { ["entity"] = target.Entity, ["field"] = target.Field };

                if (target.Item is not null)
                    named["item"] = target.Item;

                if (target.IsRemote)
                    named["remote"] = true;

                return (JsonNode)named;
            }).ToArray());

            return (JsonNode)written;
        }).ToArray());

        return reference;
    }

    private static JsonObject? AddonOf(AddonDefinition? definition) => definition is null ? null : new JsonObject
    {
        ["id"] = definition.Id.ToString("D"),
        ["path"] = definition.Path,
        ["kind"] = char.ToLowerInvariant(definition.Kind.ToString()[0]) + definition.Kind.ToString()[1..],
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

    /// <summary>Whether the path has members below it: an object, a collection of objects, an addon bag or object definition.</summary>
    private static bool HasChildren(Shape shape, ResolvedPath? facts, string path)
    {
        if (facts is null || facts.Addon is not null)
            return false;

        if (AddonAt(facts, path) is { } definitionPath)
            return AddonNames(shape, facts.Entity, definitionPath).Any();

        return facts.Path is { } pathDef && facts.Entity is { } entity
            && entity.ChildrenOf(pathDef.Wire).Any(child => LastSegment(child.Wire) != "*");
    }

    /// <summary>
    /// Per path of an entity (or of the element of <paramref name="item"/> on it), the same-host
    /// entities whose members reference it there (<c>referencing</c>): an entity target at its target
    /// field, an item target at its item collection (or, for the item itself, at its field).
    /// </summary>
    private Dictionary<string, List<JsonObject>> ReferencedBy(string entity, string? item)
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

                        // A local lookup joins along a simple reference to the entity itself; a typed,
                        // item or converted one is followed by a resolve from the referencing side only.
                        if (!list.Any(entry => entry["entity"]!.GetValue<string>() == id && entry["path"]!.GetValue<string>() == path.Wire))
                            list.Add(new JsonObject
                            {
                                ["entity"] = id,
                                ["path"] = path.Wire,
                                ["service"] = id.Split('.')[0],
                                ["remote"] = false,
                                ["lookup"] = item is null && path.Member.Reference is { } simple && simple.TargetEntity == entity && target.Item is null,
                            });
                    }
            }

        return found;
    }

    private static string LastSegment(string wire)
    {
        var dot = wire.LastIndexOf('.');

        return dot < 0 ? wire : wire[(dot + 1)..];
    }
}
