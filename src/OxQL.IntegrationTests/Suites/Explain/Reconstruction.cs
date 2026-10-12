using System.Text.Json.Nodes;
using FluentAssertions;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// The proof that the default explain answer and the schema documents say everything the answer with
/// the types written out (<c>include: "types"</c>) says: the same answer but for the rows, the flag sets
/// and the overrides; each type's rows rebuilt from the documents; and for every root at the entry and
/// after every stage, the flags of the root and of every member row of its type derived from the
/// document and the root's rule (<see cref="SchemaTypes"/>).
/// </summary>
internal static class Reconstruction
{
    /// <summary>What changes between two explains of one request: etags, times, and what an owner cost.</summary>
    private static readonly HashSet<string> Volatile = new(StringComparer.Ordinal) { "etag", "ms", "calls", "cached" };

    /// <summary>
    /// Asserts it for one request. <paramref name="depth"/> is the depth the rows of
    /// <paramref name="tabled"/> were asked to; <paramref name="published"/> whether the hosts publish
    /// the revisions of their documents. Returns the number of member flags compared.
    /// </summary>
    public static int Assert(SchemaTypes schema, JsonObject byReference, JsonObject tabled, int depth, bool published)
    {
        byReference.ContainsKey("flagSets").Should().BeFalse("the flags are the documents' and the rules'");

        // The same answer, less what the documents hold.
        var slim = Stable(tabled.DeepClone()).AsObject();

        slim.Remove("flagSets");

        foreach (var shape in Shapes(slim))
            shape.Shape.Remove("flags");

        foreach (var (_, type) in slim["types"]!.AsObject())
            foreach (var member in type!.AsObject().Select(pair => pair.Key).Where(name => name is not ("entity" or "item" or "service" or "schemaRevision" or "of")).ToList())
                type.AsObject().Remove(member);

        Stable(byReference.DeepClone()).ToJsonString().Should().Be(slim.ToJsonString(), "the default answer is the answer with the types written out, less the rows, the flag sets and the overrides");

        // The rows of each type.
        foreach (var (key, type) in tabled["types"]!.AsObject())
        {
            if (key.StartsWith("u:", StringComparison.Ordinal))
            {
                Same(type, schema.Union(type!["of"]!.AsArray().Select(target => target!.GetValue<string>()).ToList(), depth, 300), key);
                continue;
            }

            var rebuilt = schema.Table(key, depth, 300);

            if (!published)
                rebuilt.Remove("schemaRevision");

            Same(type, rebuilt, key);
        }

        // The flags of each root and of each member row of its type, at the entry and after every stage.
        var compared = 0;

        foreach (var (name, shape) in Shapes(tabled))
        {
            var mine = Shapes(byReference).Single(each => each.Name == name).Shape;

            foreach (var (root, pointer) in shape["roots"]!.AsObject())
            {
                schema.FlagsAt(byReference, mine, root, "").Should().Be(Written(tabled, shape, root, "", null), $"{name}: the root '{root}' itself");

                if (pointer is null || pointer.GetValue<string>().StartsWith("k:", StringComparison.Ordinal))
                    continue;

                var type = tabled["types"]![pointer.GetValue<string>()]!.AsObject();
                var of = type["of"]?.AsArray().Select(target => target!.GetValue<string>()).ToList();

                foreach (var row in type["members"]!.AsArray().Select(row => row!.AsArray()))
                {
                    var path = row[0]!.GetValue<string>();
                    // A union's member has the own flags of the first target that has it.
                    var holder = of is null ? type : tabled["types"]![of[row.Count > 1 ? row[1]![0]!.GetValue<int>() : 0]]!.AsObject();
                    var own = holder["members"]!.AsArray().Select(each => each!.AsArray()).Single(each => each[0]!.GetValue<string>() == path)[3]!.GetValue<string>();

                    schema.FlagsAt(byReference, mine, root, path).Should().Be(Written(tabled, shape, root, path, own), $"{name}: '{path}' under '{root}'");
                    compared++;
                }
            }
        }

        return compared;
    }

    /// <summary>Asserts two type entries equal, naming the first member row that differs.</summary>
    public static void Same(JsonNode? answered, JsonObject rebuilt, string what)
    {
        answered.Should().NotBeNull(what);

        var theirs = answered!["members"]!.AsArray();
        var ours = rebuilt["members"]!.AsArray();

        for (var index = 0; index < Math.Min(theirs.Count, ours.Count); index++)
            if (!JsonNode.DeepEquals(theirs[index], ours[index]))
                ours[index]!.ToJsonString().Should().Be(theirs[index]!.ToJsonString(), $"{what}: row {index}");

        ours.Count.Should().Be(theirs.Count, what);
        JsonNode.DeepEquals(answered, rebuilt).Should().BeTrue($"{what}: {rebuilt.ToJsonString()[..Math.Min(400, rebuilt.ToJsonString().Length)]} is {answered.ToJsonString()[..Math.Min(400, answered.ToJsonString().Length)]}");
    }

    /// <summary>The entry shape and the shape after each stage, by name.</summary>
    public static List<(string Name, JsonObject Shape)> Shapes(JsonObject answer)
    {
        var shapes = new List<(string, JsonObject)>();

        if (answer["entry"]?["shape"] is JsonObject entry)
            shapes.Add(("entry", entry));

        foreach (var stage in answer["stages"]!.AsArray())
            if (stage!["shape"] is JsonObject shape)
                shapes.Add(("stage " + stage["index"], shape));

        return shapes;
    }

    /// <summary>
    /// The flags the answer with the types written out says a member has at a shape, as the flag set's id
    /// (<paramref name="own"/>: the id in its row; null for the root itself), or null when it is not in the row:
    /// the member's own entry of the root's override set, else the set's entry for its own flags, else for every member, else its own.
    /// </summary>
    private static string? Written(JsonObject tabled, JsonObject shape, string root, string path, string? own)
    {
        var overrides = shape["flags"]![root] is JsonValue pointer ? tabled["flagSets"]![pointer.GetValue<string>()]!.AsObject() : null;

        if (overrides is not null && overrides.TryGetPropertyValue(path, out var here))
            return here?.GetValue<string>();

        if (own is not null && overrides?["~"] is JsonObject byOwn && byOwn.TryGetPropertyValue(own, out var mapped))
            return mapped?.GetValue<string>();

        if (overrides is not null && overrides.TryGetPropertyValue("*", out var every))
            return every?.GetValue<string>();

        return own;
    }

    private static JsonNode Stable(JsonNode answer)
    {
        Strip(answer);

        return answer;
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (name, value) in members.ToList())
                {
                    if (Volatile.Contains(name))
                        members.Remove(name);
                    else
                        Strip(value);
                }
                break;

            case JsonArray items:
                foreach (var item in items)
                    Strip(item);
                break;
        }
    }
}
