using System.Collections;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// Reads and edits a stored document by wire path, with the element names taken from the
/// driver's own class maps rather than from the engine: the wire name of a member is its
/// camelCase CLR name, its storage name is whatever the driver writes (<c>_id</c> for an id at
/// any depth, <c>QRCode</c> where an attribute says so). A path through a collection fans out
/// over its elements; a segment under a dictionary is a key, and a value the driver wrapped in
/// <c>{ _t, _v }</c> (a decimal or a nested dictionary in an addon bag) is stepped through
/// transparently unless the path names <c>_t</c> or <c>_v</c> itself.
/// <para>
/// Independent of the engine on purpose: the oracle must not share a naming bug with the thing
/// it checks.
/// </para>
/// </summary>
public static class StoragePath
{
    private static readonly JsonNamingPolicy Wire = JsonNamingPolicy.CamelCase;

    /// <summary>
    /// The value at <paramref name="wirePath"/>: <c>null</c> when the member is missing,
    /// <see cref="BsonNull"/> when it is stored as null. A path that crosses a collection answers a
    /// <see cref="BsonArray"/> of every element's value, flattened, with missing elements left out;
    /// so does a path whose last member is itself an array.
    /// </summary>
    public static BsonValue? Read(BsonDocument document, Type root, string wirePath)
    {
        var steps = Resolve(root, wirePath);
        var fanned = false;
        IEnumerable<BsonValue> current = [document];

        foreach (var step in steps)
        {
            var next = new List<BsonValue>();

            foreach (var node in current)
            {
                if (node is BsonArray array)
                {
                    fanned = true;

                    foreach (var element in array)
                        Add(next, Step(element, step));

                    continue;
                }

                Add(next, Step(node, step));
            }

            current = next;
        }

        var values = current.ToList();

        if (values.Count == 1 && values[0] is BsonArray leafArray && !fanned)
            return new BsonArray(leafArray);

        if (fanned)
            return new BsonArray(values.SelectMany(value => value is BsonArray inner ? inner : [value]));

        return values.Count == 0 ? null : values[0];

        static void Add(List<BsonValue> into, BsonValue? value)
        {
            if (value is not null)
                into.Add(value);
        }
    }

    /// <summary>Whether <paramref name="wirePath"/> crosses a collection before its last segment.</summary>
    public static bool Fans(Type root, string wirePath) =>
        Resolve(root, wirePath).SkipLast(1).Any(step => step.Fans);

    /// <summary>Removes the member at <paramref name="wirePath"/>, in every element of any collection on the way.</summary>
    public static void Unset(BsonDocument document, Type root, string wirePath) =>
        Edit(document, Resolve(root, wirePath), (parent, name) => parent.Remove(name));

    /// <summary>Writes <paramref name="value"/> at <paramref name="wirePath"/>, in every element of any collection on the way.</summary>
    public static void Set(BsonDocument document, Type root, string wirePath, BsonValue value) =>
        Edit(document, Resolve(root, wirePath), (parent, name) => parent[name] = value);

    /// <summary>The storage element names of a wire path, for a failure message or a raw filter.</summary>
    public static string StorageNameOf(Type root, string wirePath) =>
        string.Join('.', Resolve(root, wirePath).Select(step => step.Element));

    private static void Edit(BsonDocument document, IReadOnlyList<PathStep> steps, Action<BsonDocument, string> edit)
    {
        IEnumerable<BsonDocument> parents = [document];

        foreach (var step in steps.SkipLast(1))
        {
            parents = parents
                .Select(parent => parent.TryGetValue(step.Element, out var value) ? value : null)
                .SelectMany(value => value switch
                {
                    BsonArray array => array.OfType<BsonDocument>(),
                    BsonDocument wrapped when step.Dynamic && wrapped.Contains("_v") && wrapped["_v"] is BsonDocument inner => [inner],
                    BsonDocument inner => [inner],
                    _ => Enumerable.Empty<BsonDocument>(),
                })
                .ToList();
        }

        foreach (var parent in parents)
            edit(parent, steps[^1].Element);
    }

    private static BsonValue? Step(BsonValue node, PathStep step)
    {
        if (node is not BsonDocument document)
            return null;

        // A dynamic value the driver wrapped: step through the wrapper unless the path names it.
        if (step.Dynamic && step.Element is not ("_t" or "_v") && document.Contains("_t") && document.TryGetValue("_v", out var wrapped))
            return Step(wrapped, step);

        if (!document.TryGetValue(step.Element, out var value))
            return null;

        // Reading a wrapped decimal or dictionary answers the value, not the wrapper.
        if (step.Dynamic && value is BsonDocument inner && inner.ElementCount == 2 && inner.Contains("_t") && inner.TryGetValue("_v", out var unwrapped))
            return unwrapped;

        return value;
    }

    private sealed record PathStep(string Element, bool Fans, bool Dynamic);

    private static readonly Dictionary<(Type, string), IReadOnlyList<PathStep>> Cache = [];

    private static IReadOnlyList<PathStep> Resolve(Type root, string wirePath)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((root, wirePath), out var cached))
                return cached;
        }

        var steps = new List<PathStep>();
        Type? current = root;

        foreach (var segment in wirePath.Split('.'))
        {
            if (current is null || current == typeof(object))
            {
                // Inside an addon bag or an untyped value: segments are stored keys, and a
                // wrapper is stepped through whether or not the path spells its `_v`.
                if (segment == "_v")
                    continue;

                steps.Add(new PathStep(segment, false, true));
                current = null;
                continue;
            }

            if (DictionaryValue(current) is { } dictionaryValue)
            {
                steps.Add(new PathStep(segment, false, dictionaryValue == typeof(object)));
                current = Element(dictionaryValue, out _);
                continue;
            }

            var map = BsonClassMap.LookupClassMap(current);
            var member = map.AllMemberMaps.FirstOrDefault(candidate => Wire.ConvertName(candidate.MemberName) == segment)
                ?? throw new ArgumentException($"'{wirePath}': {current.Name} has no member with the wire name '{segment}'.", nameof(wirePath));

            var type = Element(member.MemberType, out var fans);
            steps.Add(new PathStep(member.ElementName, fans, false));
            current = type;
        }

        lock (Cache)
            Cache[(root, wirePath)] = steps;

        return steps;
    }

    /// <summary>The type a member's values have: the element type of a collection, the type itself otherwise.</summary>
    private static Type Element(Type type, out bool fans)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        fans = false;

        if (type == typeof(string) || type == typeof(byte[]) || DictionaryValue(type) is not null)
            return type;

        if (type.IsArray)
        {
            fans = true;
            return type.GetElementType()!;
        }

        var enumerable = type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerable is not null && typeof(IEnumerable).IsAssignableFrom(type))
        {
            fans = true;
            return enumerable.GetGenericArguments()[0];
        }

        return type;
    }

    private static Type? DictionaryValue(Type type)
    {
        var dictionary = type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        return dictionary?.GetGenericArguments()[0] == typeof(string) ? dictionary.GetGenericArguments()[1] : null;
    }
}
