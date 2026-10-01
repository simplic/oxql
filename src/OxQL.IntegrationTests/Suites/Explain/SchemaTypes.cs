using System.Globalization;
using System.Text.Json.Nodes;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// A client of the default explain answer, written against the schema documents alone: what a consumer
/// that loaded <c>GET /schema</c> of each service does with the type references and the flag rules of
/// an answer. It reads nothing of the engine's model and shares no code with it, so that the tests
/// which compare it with the <c>include: "types"</c> answer prove the documented algorithm, not the
/// engine against itself.
/// <para>
/// A type reference (<c>t:&lt;entity&gt;[#item]</c>) is the entity's pool entry of its service's document,
/// walked from the entity's property list; a member's flags are a function of its descriptors and of
/// where it stands: the collections above it that are not unwound, and the arrays its path crosses.
/// </para>
/// </summary>
internal sealed class SchemaTypes
{
    /// <summary>The operators in the order of their bits.</summary>
    private static readonly string[] Operators = ["eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex", "is", "any"];

    private static readonly HashSet<string> TextOperators = ["contains", "startsWith", "endsWith", "regex"];
    private static readonly HashSet<string> OrderedOperators = ["gt", "gte", "lt", "lte"];
    private static readonly HashSet<string> OrderedKinds = ["int", "long", "double", "decimal", "date", "dateTime", "timeSpan", "string", "enum"];
    private static readonly HashSet<string> Composite = ["object", "array", "dictionary", "unknown"];

    private readonly Dictionary<string, JsonObject> documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node> roots = new(StringComparer.Ordinal);

    public SchemaTypes(IEnumerable<JsonObject> documents)
    {
        foreach (var document in documents)
            this.documents[document["service"]!.GetValue<string>()] = document;
    }

    /// <summary>The revision of a service's document.</summary>
    public string? RevisionOf(string service) => documents.TryGetValue(service, out var document) ? document["revision"]?.GetValue<string>() : null;

    /// <summary>One member of a type: its property descriptor and where it stands below the entity.</summary>
    public sealed class Node
    {
        public required string Service { get; init; }

        /// <summary>The path below the entity, dotted; <c>""</c> for the entity itself.</summary>
        public required string Path { get; init; }

        /// <summary>The property descriptor; null for the entity itself.</summary>
        public JsonObject? Property { get; init; }

        public Node? Parent { get; init; }

        /// <summary>Whether the member and every member above it is stored.</summary>
        public bool Stored { get; init; } = true;

        /// <summary>The pool entries on the way from the entity to this member; a type on it is not entered again.</summary>
        public required IReadOnlyList<string> Branch { get; init; }

        /// <summary>The pool entry whose properties are this member's members; null for a leaf.</summary>
        public string? Type { get; init; }

        public List<Node>? Children { get; set; }
    }

    // ---- the walk -------------------------------------------------------------------------------

    private JsonObject Pool(string service) => documents[service]["types"]!.AsObject();

    private static string? Pointer(JsonObject? descriptor) => descriptor?["type"]?.GetValue<string>() is { } pointer ? pointer["#/types/".Length..] : null;

    private static string KindOf(JsonObject? descriptor) => descriptor?["kind"]?.GetValue<string>() ?? "unknown";

    /// <summary>The descriptor once array traversal is accounted for.</summary>
    private static JsonObject Leaf(JsonObject descriptor)
    {
        while (KindOf(descriptor) == "array" && descriptor["of"] is JsonObject of)
            descriptor = of;

        return descriptor;
    }

    /// <summary>The entity of a type key (<c>entity</c> or <c>entity#item</c>), walked on demand.</summary>
    private Node Entity(string entity)
    {
        if (roots.TryGetValue(entity, out var known))
            return known;

        var service = documents.Values.FirstOrDefault(document => document["types"]![entity]?["entity"]?.GetValue<bool>() == true)?["service"]!.GetValue<string>()
            ?? throw new InvalidOperationException($"No schema document describes the entity '{entity}'.");

        return roots[entity] = new Node { Service = service, Path = "", Branch = [entity], Type = entity };
    }

    public IReadOnlyList<Node> ChildrenOf(Node node)
    {
        if (node.Children is not null)
            return node.Children;

        var children = new List<Node>();

        if (node.Type is { } type)
            foreach (var property in Pool(node.Service)[type]!["properties"]!.AsArray().OfType<JsonObject>())
            {
                var name = property["name"]!.GetValue<string>();
                var leaf = Leaf(property);
                var target = KindOf(leaf) == "object" ? Pointer(leaf) : null;
                // A type reached again on the same branch is a member without members.
                var entered = target is not null && !node.Branch.Contains(target, StringComparer.Ordinal) && Pool(node.Service)[target]?["properties"] is JsonArray;

                children.Add(new Node
                {
                    Service = node.Service,
                    Path = node.Path.Length == 0 ? name : node.Path + "." + name,
                    Property = property,
                    Parent = node,
                    Stored = node.Stored && property["stored"]?.GetValue<bool>() != false,
                    Branch = entered ? [.. node.Branch, target!] : node.Branch,
                    Type = entered ? target : null,
                });
            }

        return node.Children = children;
    }

    /// <summary>The member at a path below a node, or null.</summary>
    public Node? Find(Node from, string relative)
    {
        var node = from;

        foreach (var segment in relative.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = ChildrenOf(node).FirstOrDefault(child => child.Path[(child.Path.LastIndexOf('.') + 1)..] == segment);

            if (node is null)
                return null;
        }

        return node;
    }

    /// <summary>The node a type key's members lie below: the entity, or the collection of an item type.</summary>
    public Node RootOf(string typeKey)
    {
        var target = typeKey["t:".Length..];
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var entity = Entity(hash < 0 ? target : target[..hash]);

        return hash < 0 ? entity : Find(entity, target[(hash + 1)..]) ?? throw new InvalidOperationException($"'{typeKey}' names no collection of its entity.");
    }

    private static string Relative(Node root, Node member) => root.Path.Length == 0 ? member.Path : member.Path[(root.Path.Length + 1)..];

    /// <summary>Whether a member is a collection in storage: an array, or a dictionary stored as an array of entries.</summary>
    private static bool IsCollection(Node node) =>
        KindOf(node.Property) == "array" || (KindOf(node.Property) == "dictionary" && node.Property!["storedAs"]?.GetValue<string>() is "arrayOfDocuments" or "arrayOfArrays");

    /// <summary>The element of a collection: an array's <c>of</c>, a dictionary's <c>value</c>.</summary>
    private static JsonObject? Element(JsonObject property) => KindOf(property) switch
    {
        "array" => property["of"] as JsonObject,
        "dictionary" => property["value"] as JsonObject,
        _ => property,
    };

    // ---- flags ----------------------------------------------------------------------------------

    /// <summary>Where a root's members stand at one point of a pipeline: the rule of an answer, read.</summary>
    /// <param name="Unwound">The collections below the root that are unwound here, relative to it.</param>
    /// <param name="Under"><c>collection</c>, <c>afterPage</c>, <c>owner</c>, or null.</param>
    /// <param name="Filter">With <c>owner</c>: whether a condition may compare the members' values.</param>
    /// <param name="Many">With <c>afterPage</c>: whether the alias holds an array of rows.</param>
    public sealed record Standing(IReadOnlyList<string> Unwound, string? Under = null, bool Filter = false, bool Many = false)
    {
        public static readonly Standing Own = new([]);
    }

    /// <summary>
    /// The flags of a member of the type rooted at <paramref name="root"/>, as a flag id. Its own flags
    /// are those at <see cref="Standing.Own"/>; a rule changes where it stands.
    /// </summary>
    public string Flags(Node root, Node member, Standing standing)
    {
        // The chain from below the root down to the member.
        var chain = new List<Node>();

        for (var node = member; node is not null && !ReferenceEquals(node, root); node = node.Parent)
            chain.Insert(0, node);

        bool Unwound(Node node) => ReferenceEquals(node, root) || (chain.Contains(node) && standing.Unwound.Contains(Relative(root, node), StringComparer.Ordinal));

        // The collections above the member that are not unwound: those above an item type's collection count too.
        var above = 0;

        for (var node = member.Parent; node is { Property: not null }; node = node.Parent)
            if (IsCollection(node) && !Unwound(node))
                above++;

        if (standing.Under == "collection")
            above++;

        // The arrays the path crosses below the root, itself included; an unwound one is its element.
        var crossed = chain.Count(node => KindOf(Unwound(node) ? Element(node.Property!) : node.Property) == "array") + (standing.Under == "collection" || standing.Many ? 1 : 0);
        var shape = IsCollection(member) && Unwound(member) ? Element(member.Property!) ?? member.Property! : member.Property!;
        var kind = KindOf(shape);
        var leaf = Leaf(shape);
        var leafKind = KindOf(leaf);
        var references = member.Property!["references"] is JsonObject || member.Property["referenceCases"] is JsonArray { Count: > 0 };
        var follow = !references ? 0 : crossed switch { 0 => 1, 1 => 2, _ => 3 };

        if (!member.Stored)
            return Under(Pack(0, false, false, false, false, above, follow), standing);

        var filterable = !Composite.Contains(leafKind) && leaf["storedAs"]?.GetValue<string>() != "document";
        var codePoint = leafKind == "string" && leaf["storedAs"]?.GetValue<string>() == "codePoint";
        var operators = 0;

        for (var bit = 0; bit < Operators.Length; bit++)
        {
            var op = Operators[bit];
            var admitted = op switch
            {
                "is" => above == 0 && leafKind == "object" && Pointer(leaf) is { } type && Pool(member.Service)[type]?["variants"] is JsonArray { Count: > 0 },
                "any" => above == 0 && kind == "array" && KindOf(shape["of"] as JsonObject) == "object",
                "exists" => true,
                _ when !filterable => false,
                _ when TextOperators.Contains(op) => leafKind == "string" && !codePoint,
                _ when OrderedOperators.Contains(op) => OrderedKinds.Contains(leafKind),
                _ => true,
            };

            if (admitted)
                operators |= 1 << bit;
        }

        var sortable = filterable && above == 0 && kind is not ("array" or "dictionary");
        var groupable = above == 0 && !Composite.Contains(kind);
        var unwindable = above == 0 && (kind == "array" || (kind == "dictionary" && shape["storedAs"]?.GetValue<string>() is "arrayOfDocuments" or "arrayOfArrays"));

        return Under(Pack(operators, sortable, groupable, unwindable, filterable && leafKind == "string", above, follow), standing);
    }

    private const int IsBit = 1 << 13;
    private const int AnyBit = 1 << 14;

    /// <summary>What an alias the page does not hold leaves of a member's flags.</summary>
    private static string Under(int packed, Standing standing)
    {
        var operators = packed & 0x7FFF;
        var folds = (packed & (1 << 19)) != 0;
        var follow = (packed >> 20) & 3;
        var above = packed >> 22;

        switch (standing.Under)
        {
            // Joined after the page from local targets: read and followed, nothing else.
            case "afterPage":
                return Id(Pack(0, false, false, false, false, 0, follow));

            // An owner's rows: a condition is a semi-join on the owner where the alias can be narrowed.
            case "owner":
                operators = standing.Filter ? operators & ~(IsBit | AnyBit) : 0;

                return Id(Pack(operators, false, false, false, operators != 0 && folds, above, follow));

            default:
                return Id(packed);
        }
    }

    private static int Pack(int operators, bool sortable, bool groupable, bool unwindable, bool folds, int above, int follow) =>
        operators | (sortable ? 1 << 15 : 0) | (groupable ? 1 << 16 : 0) | (unwindable ? 1 << 17 : 0) | (1 << 18) | (folds ? 1 << 19 : 0) | (follow << 20) | (Math.Min(above, 255) << 22);

    private static string Id(int packed) => packed.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>A flag id as the flag set an answer with <c>include: "types"</c> writes for it.</summary>
    public static JsonObject FlagSet(string id)
    {
        var packed = int.Parse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var set = new JsonObject
        {
            ["operators"] = new JsonArray(Operators.Where((_, bit) => (packed & (1 << bit)) != 0).Select(op => (JsonNode)op).ToArray()),
            ["sortable"] = (packed & (1 << 15)) != 0,
            ["groupable"] = (packed & (1 << 16)) != 0,
            ["unwindable"] = (packed & (1 << 17)) != 0,
            ["projectable"] = (packed & (1 << 18)) != 0,
            ["folds"] = (packed & (1 << 19)) != 0,
            ["underCollection"] = packed >> 22,
        };

        if ((string?[])[null, "one", "elements", "none"] is var names && names[(packed >> 20) & 3] is { } follow)
            set["follow"] = follow;

        return set;
    }

    // ---- the type table of include: "types" -------------------------------------------------------

    /// <summary>
    /// The entry an answer with <c>include: "types"</c> holds for a type key: the member rows level by
    /// level to <paramref name="depth"/>, cut at <paramref name="cap"/>, each with its own flags.
    /// </summary>
    public JsonObject Table(string typeKey, int depth, int cap)
    {
        var target = typeKey["t:".Length..];
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var root = RootOf(typeKey);
        var entry = new JsonObject { ["entity"] = hash < 0 ? target : target[..hash] };

        if (hash >= 0)
            entry["item"] = target[(hash + 1)..];

        entry["service"] = root.Service;

        if (RevisionOf(root.Service) is { } revision)
            entry["schemaRevision"] = revision;

        var holder = hash < 0 ? root.Type : Pointer(Leaf(root.Property!));

        if (holder is not null && VariantNames(root.Service, holder) is { } variants)
            entry["variants"] = variants;

        var onlyFor = new List<string>();
        var rows = new JsonArray();
        var queue = new List<(Node At, int Level)> { (root, 1) };
        var cut = new List<string>();
        var truncated = false;

        for (var position = 0; position < queue.Count && !truncated; position++)
        {
            var (at, level) = queue[position];

            foreach (var member in ChildrenOf(at))
            {
                if (rows.Count >= cap)
                {
                    truncated = true;
                    break;
                }

                rows.Add(Row(root, member, onlyFor));

                if (ChildrenOf(member).Count > 0 && level < depth)
                    queue.Add((member, level + 1));
                else if (ChildrenOf(member).Count > 0)
                    cut.Add(Relative(root, member));
            }

            if (truncated)
                cut.AddRange(queue.Skip(position).Where(pending => !ReferenceEquals(pending.At, root)).Select(pending => Relative(root, pending.At)));
        }

        foreach (var row in rows.OfType<JsonArray>())
        {
            if (cut.Contains(row[0]!.GetValue<string>(), StringComparer.Ordinal))
            {
                if (row[4] is not JsonObject more)
                    row[4] = more = [];

                more["children"] = true;
            }

            while (row.Count > 4 && row[^1] is null)
                row.RemoveAt(row.Count - 1);
        }

        if (onlyFor.Count > 0)
            entry["onlyFor"] = new JsonArray(onlyFor.Select(set => (JsonNode)new JsonArray(set.Split('\n').Select(name => (JsonNode)name).ToArray())).ToArray());

        entry["members"] = rows;

        if (truncated)
            entry["truncated"] = true;

        return entry;
    }

    /// <summary>
    /// The entry an answer with <c>include: "types"</c> holds for a union of <paramref name="of"/>: each
    /// member of any target once, in the order the targets list them, with the targets that have it
    /// (left out when all do) and <c>unknown</c> where their kinds disagree.
    /// </summary>
    public JsonObject Union(IReadOnlyList<string> of, int depth, int cap)
    {
        var rows = new List<(string Path, string? Kind, List<int> Have)>();
        var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = false;

        for (var index = 0; index < of.Count; index++)
        {
            var table = Table(of[index], depth, cap);

            truncated |= table["truncated"]?.GetValue<bool>() == true;

            foreach (var row in table["members"]!.AsArray().Select(row => row!.AsArray()))
            {
                var path = row[0]!.GetValue<string>();
                var kind = row[1]!.GetValue<string>();

                if (byPath.TryGetValue(path, out var at))
                {
                    rows[at].Have.Add(index);

                    if (rows[at].Kind != kind)
                        rows[at] = (path, null, rows[at].Have);
                }
                else
                {
                    byPath[path] = rows.Count;
                    rows.Add((path, kind, [index]));
                }
            }
        }

        var entry = new JsonObject
        {
            ["of"] = new JsonArray(of.Select(target => (JsonNode)target).ToArray()),
            ["members"] = new JsonArray(rows.Select(row =>
            {
                var member = new JsonArray(row.Path);

                if (row.Have.Count < of.Count || row.Kind is null)
                    member.Add(new JsonArray(row.Have.Select(index => (JsonNode)index).ToArray()));

                if (row.Kind is null)
                    member.Add("unknown");

                return (JsonNode)member;
            }).ToArray()),
        };

        if (truncated)
            entry["truncated"] = true;

        return entry;
    }

    // ---- a member's flags at a stage, from the default answer ---------------------------------------

    private static bool Under(string path, string ancestor) =>
        path.Length > ancestor.Length && path[ancestor.Length] == '.' && path.StartsWith(ancestor, StringComparison.Ordinal);

    private static bool Names(JsonNode? paths, string path) =>
        paths!.AsArray().Select(kept => kept!.GetValue<string>()).Any(kept => path == kept || Under(path, kept) || Under(kept, path));

    /// <summary>
    /// The flags of the member <paramref name="path"/> of <paramref name="root"/> at one shape of a default
    /// answer, as a flag id; null when the row does not carry the member there, or no type of the root has it.
    /// The path <c>""</c> is the root itself.
    /// </summary>
    public string? FlagsAt(JsonObject answer, JsonObject shape, string root, string path)
    {
        var rule = shape["rules"]![root] is JsonValue pointer ? answer["rules"]![pointer.GetValue<string>()]!.AsObject() : null;

        if (path.Length == 0)
            return rule?["self"]?.GetValue<string>();

        var wire = root.Length == 0 ? path : root + "." + path;

        // What a projection or an unwind took out of the row, and what a join's alias does not show.
        if (shape["removed"] is JsonArray removed && removed.Select(gone => gone!.GetValue<string>()).Any(gone => wire == gone || Under(wire, gone)))
            return null;

        if (shape["projection"] is JsonArray projection && !Names(projection, wire))
            return null;

        if (rule?["shows"] is JsonArray shows && !Names(shows, path))
            return null;

        var type = shape["roots"]![root]?.GetValue<string>();

        if (type is null || type.StartsWith("k:", StringComparison.Ordinal))
            return null;

        // A union's member is the first target's that has it.
        var targets = type.StartsWith("u:", StringComparison.Ordinal) ? answer["types"]![type]!["of"]!.AsArray().Select(target => target!.GetValue<string>()).ToList() : [type];
        var standing = new Standing(
            rule?["unwound"]?.AsArray().Select(collection => collection!.GetValue<string>()).ToList() ?? [],
            rule?["under"]?.GetValue<string>(),
            rule?["filter"]?.GetValue<bool>() == true,
            rule?["many"]?.GetValue<bool>() == true);

        foreach (var target in targets)
        {
            var at = RootOf(target);

            if (Find(at, path) is { } member)
                return Flags(at, member, standing);
        }

        return null;
    }

    /// <summary>The names <c>is</c> accepts on a polymorphic pool entry: its base where that is concrete, then its variants; null on an entry without variants.</summary>
    private JsonArray? VariantNames(string service, string type)
    {
        var entry = Pool(service)[type]!.AsObject();

        if (entry["variants"] is not JsonArray { Count: > 0 } variants)
            return null;

        var names = new JsonArray();

        if (entry["baseVariant"]?.GetValue<string>() is { } concrete)
            names.Add(concrete);

        foreach (var variant in variants)
            names.Add(variant!["name"]!.GetValue<string>());

        return names;
    }

    /// <summary>One member row: <c>[path, kind, nullable, flags, more, onlyFor, reference]</c>.</summary>
    private JsonArray Row(Node root, Node member, List<string> onlyFor)
    {
        var property = member.Property!;
        var kind = KindOf(property);
        var leaf = Leaf(property);
        var more = new JsonObject();

        if (KindOf(leaf) != kind)
            more["leafKind"] = KindOf(leaf);

        if (property["displayName"] is { } displayName)
            more["displayName"] = displayName.DeepClone();

        if (!member.Stored)
            more["stored"] = false;

        if (kind == "dictionary" && IsCollection(member))
            more["collection"] = true;

        if (KindOf(leaf) == "enum" && Pointer(leaf) is { } enumType && Pool(member.Service)[enumType]?["values"] is JsonArray values)
            more["enum"] = new JsonArray(values.Select(value => (JsonNode)new JsonObject { ["name"] = value!["name"]!.DeepClone(), ["value"] = value["value"]!.DeepClone() }).ToArray());

        if (KindOf(leaf) == "object" && Pointer(leaf) is { } holder && VariantNames(member.Service, holder) is { } variants)
            more["variants"] = variants;

        // The members an unwind's flatten may follow: arrays of the element's own type, the one named like the collection first.
        if (kind == "array" && property["of"] is JsonObject of && KindOf(of) == "object" && Pointer(of) is { } element && Pool(member.Service)[element]?["properties"] is JsonArray elementMembers)
        {
            var own = member.Path[(member.Path.LastIndexOf('.') + 1)..];
            var flattens = elementMembers.OfType<JsonObject>()
                .Where(nested => KindOf(nested) == "array" && nested["of"] is JsonObject inner && KindOf(inner) == "object" && Pointer(inner) is { } nestedType
                    && nested["stored"]?.GetValue<bool>() != false
                    && (nestedType == element || Pool(member.Service)[nestedType]?["variants"] is JsonArray nestedVariants && nestedVariants.Any(variant => Pointer(variant as JsonObject) == element)))
                .Select(nested => nested["name"]!.GetValue<string>())
                .OrderBy(name => name == own ? 0 : 1)
                .ToList();

            if (flattens.Count > 0)
                more["flatten"] = flattens[0];

            if (flattens.Count > 1)
                more["flattenMembers"] = new JsonArray(flattens.Select(name => (JsonNode)name).ToArray());
        }

        if ((property["snapshotOf"] ?? leaf["snapshotOf"]) is { } snapshot)
            more["snapshotOf"] = snapshot.DeepClone();

        if (property["deprecated"] is JsonObject deprecated)
            more["deprecated"] = new JsonObject { ["since"] = deprecated["since"]?.DeepClone(), ["replacedBy"] = deprecated["replacedBy"]?.DeepClone(), ["note"] = deprecated["note"]?.DeepClone() };

        if (property["constraints"] is { } constraints)
            more["constraints"] = constraints.DeepClone();

        JsonNode? variantsOf = null;

        if (property["onlyFor"] is JsonArray { Count: > 0 } only)
        {
            var set = string.Join('\n', only.Select(name => name!.GetValue<string>()));
            var at = onlyFor.IndexOf(set);

            if (at < 0)
            {
                at = onlyFor.Count;
                onlyFor.Add(set);
            }

            variantsOf = at;
        }

        return
        [
            Relative(root, member),
            kind,
            property["nullable"]?.GetValue<bool>() == true ? 1 : 0,
            Flags(root, member, Standing.Own),
            more.Count > 0 ? more : null,
            variantsOf,
            Reference(member),
        ];
    }

    /// <summary>The reference a member declares, every case: the simple one of <c>references</c>, or <c>referenceCases</c>.</summary>
    private JsonObject? Reference(Node member)
    {
        var property = member.Property!;
        var own = Pool(member.Service).Where(pair => pair.Value?["entity"]?.GetValue<bool>() == true).Select(pair => pair.Key.Split('.')[0]).ToHashSet(StringComparer.Ordinal);

        JsonObject Target(JsonNode target)
        {
            var named = new JsonObject { ["entity"] = target["entity"]!.DeepClone(), ["field"] = target["field"]!.DeepClone() };

            if (target["item"] is { } item)
                named["item"] = item.DeepClone();

            if (!own.Contains(target["entity"]!.GetValue<string>().Split('.')[0]))
                named["remote"] = true;

            return named;
        }

        if (property["referenceCases"] is JsonArray { Count: > 0 } cases)
        {
            var reference = new JsonObject();

            if (cases.All(declared => declared!["keyAs"]?.GetValue<string>() == "guid"))
                reference["keyAs"] = "guid";

            reference["cases"] = new JsonArray(cases.Select(declared =>
            {
                var written = new JsonObject();

                if (declared!["when"] is { } when)
                    written["when"] = when.DeepClone();

                if (declared["keyAs"] is { } keyAs)
                    written["keyAs"] = keyAs.DeepClone();

                written["targets"] = new JsonArray(declared["targets"]!.AsArray().Select(target => (JsonNode)Target(target!)).ToArray());

                return (JsonNode)written;
            }).ToArray());

            return reference;
        }

        return property["references"] is JsonObject simple
            ? new JsonObject { ["simple"] = true, ["cases"] = new JsonArray(new JsonObject { ["targets"] = new JsonArray(Target(simple)) }) }
            : null;
    }
}
