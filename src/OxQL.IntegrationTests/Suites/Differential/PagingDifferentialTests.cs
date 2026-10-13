using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// A walk by cursor against the one page that holds every row, generated: every sort member of
/// every kind in both directions, alone, in pairs and around the entity's key, on the entity's
/// rows, on unwound rows, on groups and on joined rows, in pages of one row, of a few, of all but
/// one, of all and of one more. What holds for each: the pages are the one page, split (no row
/// twice, none left out, the order kept), every page is full until the rows end, a count beside a
/// page is the number of rows, and so is the count alone.
/// </summary>
[Trait("Category", "Integration")]
public class PagingDifferentialTests
{
    private const string Entity = EdgeCorpus.Entity;

    /// <summary>Set to <c>wide</c> to walk every case in pages of one and of two rows as well.</summary>
    internal static readonly bool Wide = Environment.GetEnvironmentVariable("OXQL_DIFFERENTIAL") == "wide";

    /// <summary>The members a sort of the entity's rows names: one or two of every kind, nullable and not, nested.</summary>
    internal static readonly string[] Members =
    [
        "name", "note", "address.city", "optionalAddress.zip", "count", "optionalCount", "magnitude", "optionalMagnitude", "amount", "optionalAmount",
        "ratio", "optionalRatio", "active", "optionalActive", "optionalMarker", "moment", "optionalMoment", "day", "optionalDay", "optionalDuration",
        "grade", "optionalGrade", "state", "optionalState", "address.floor", "optionalAddress.floor",
    ];

    private static string Sort(params (string Path, string Direction)[] keys) =>
        $$"""{ "sort": [{{string.Join(", ", keys.Select(key => $$"""{ "{{key.Path}}": "{{key.Direction}}" }"""))}}] }""";

    private static string Project(IEnumerable<string> paths) =>
        $$"""{ "project": { {{string.Join(", ", paths.Prepend("id").Distinct().Select(path => $"\"{path}\": 1"))}} } }""";

    private static readonly string[] Directions = ["asc", "desc"];

    /// <summary>What narrows the rows before the sort, by turns: nothing, a member, a nullable member, a collection.</summary>
    private static readonly string?[] Matches =
    [
        null,
        """{ "match": { "isDeleted": { "neq": true } } }""",
        """{ "match": { "optionalCount": { "gte": 1 } } }""",
        """{ "match": { "items.quantity": { "gt": 1 } } }""",
        """{ "match": { "or": [{ "note": { "eq": null } }, { "count": { "in": [1, 2] } }] } }""",
    ];

    private static string Stages(params string?[] stages) => "[" + string.Join(", ", stages.Where(stage => stage is not null)) + "]";

    public static TheoryData<string, string> RootSorts()
    {
        var data = new TheoryData<string, string>();
        var turn = 0;

        // Every member alone, both ways.
        foreach (var member in Members)
            foreach (var direction in Directions)
                data.Add($"{member}:{direction}", Stages(Matches[turn++ % Matches.Length], Sort((member, direction)), Project([member])));

        // Pairs: each member with the one four places on, in the four combinations of direction.
        for (var index = 0; index < Members.Length; index++)
        {
            var (first, second) = (Members[index], Members[(index + 4) % Members.Length]);

            foreach (var a in Directions)
                foreach (var b in Directions)
                    data.Add($"{first}:{a},{second}:{b}", Stages(Matches[turn++ % Matches.Length], Sort((first, a), (second, b)), Project([first, second])));
        }

        // The key before, between and after the members, both ways; and three members.
        for (var index = 0; index < Members.Length; index += 3)
        {
            var (first, second) = (Members[index], Members[(index + 7) % Members.Length]);

            foreach (var key in Directions)
            {
                data.Add($"id:{key},{first}:desc", Stages(Sort(("id", key), (first, "desc")), Project([first])));
                data.Add($"{first}:asc,id:{key},{second}:desc", Stages(Matches[turn++ % Matches.Length], Sort((first, "asc"), ("id", key), (second, "desc")), Project([first, second])));
                data.Add($"{first}:desc,{second}:asc,id:{key}", Stages(Sort((first, "desc"), (second, "asc"), ("id", key)), Project([first, second])));
            }
        }

        // The sort written before the projection that drops its members, and without any projection.
        data.Add("dropped-sort-members", Stages(Sort(("optionalMoment", "desc"), ("note", "asc")), """{ "project": { "name": 1 } }"""));
        data.Add("dropped-key", Stages(Sort(("optionalCount", "desc")), """{ "project": { "id": 0, "items": 0, "tags": 0 } }"""));
        data.Add("no-projection", Stages(Sort(("optionalAmount", "asc"), ("optionalRatio", "desc"))));
        data.Add("no-sort", Stages(Matches[3]));
        data.Add("exact-order", Stages("""{ "sort": [{ "name": { "direction": "asc", "caseSensitive": true } }, { "note": { "direction": "desc", "caseSensitive": true } }] }""", Project(["name", "note"])));

        return data;
    }

    [Theory]
    [MemberData(nameof(RootSorts))]
    public async Task R_a_walk_over_the_entitys_rows_is_the_one_page_split(string id, string stages)
    {
        await HoldsAsync(id, new Walks.Query(Entity, stages), [7, 89, 90, 91]);
    }

    /// <summary>The cases whose rows may read alike: an element twice in one collection without its position.</summary>
    private static readonly string[] Repeating = ["tags-preserve/"];

    /// <summary>Walks a query against its one page; with <paramref name="rowByRow"/> (or the wide run) also in pages of one and two rows.</summary>
    internal static async Task HoldsAsync(string id, Walks.Query query, int[] limits, bool rowByRow = false, int? expected = null)
    {
        var client = await EdgeCorpus.ClientAsync();
        var whole = await Walks.WholeAsync(client, query);

        // Rows that say what they are (a key, a position, the keys of a group) are each there once.
        if (!Repeating.Any(id.StartsWith))
            whole.Should().OnlyHaveUniqueItems($"{id}: the one page holds no row twice");

        if (expected is { } rows)
            whole.Should().HaveCount(rows, id);

        // The sizes around the number of rows there are, whatever the case's own limits say.
        var sizes = limits.Concat(Wide || rowByRow ? [1, 2] : []).Concat(whole.Count > 1 ? [whole.Count - 1, whole.Count, whole.Count + 1] : [1]).Where(limit => limit is > 0 and <= 500);
        var differences = await Walks.DifferencesAsync(client, query, whole, sizes);
        var counted = await Walks.CountAsync(client, query);

        if (!counted.IsPage || counted.CountState != "exact" || counted.TotalCount != whole.Count)
            differences.Add($"the count alone says {(counted.IsPage ? counted.Count?.ToJsonString() : counted.ToString())}, the rows are {whole.Count}");

        differences.Should().BeEmpty($"{id}: {query.Stages}{(query.Filter is null ? "" : " filter " + query.Filter)}");
    }

    // ── unwound rows ──────────────────────────────────────────────────────────────────────

    private static readonly (string Id, string Unwind, string[] Element, string? Position)[] Unwinds =
    [
        ("items", """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""", ["item.code", "item.quantity"], "position"),
        ("items-no-index", """{ "unwind": { "path": "items", "as": "item" } }""", ["item.code", "item.quantity"], null),
        ("items-preserve", """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }""", ["item.code", "item.quantity"], "position"),
        ("tags", """{ "unwind": { "path": "tags", "as": "tag", "includeIndex": "position" } }""", ["tag"], "position"),
        ("tags-preserve", """{ "unwind": { "path": "tags", "as": "tag", "preserveNull": true } }""", ["tag"], null),
    ];

    private static readonly string?[] ElementMatches =
    [
        null,
        """{ "match": { "item.quantity": { "gte": 1 } } }""",
        """{ "match": { "item.code": { "neq": "A-1" } } }""",
        """{ "match": { "item.code": { "eq": null } } }""",
        """{ "match": { "item.code": { "in": ["a-1", null] } } }""",
        """{ "match": { "or": [{ "item.quantity": { "lt": 2 } }, { "count": { "eq": 3 } }] } }""",
        """{ "match": { "optionalCount": { "neq": 2 }, "item.quantity": { "neq": 0 } } }""",
        """{ "match": { "not": { "item.code": { "startsWith": "a" } } } }""",
        """{ "match": { "item.parts": { "any": { "sku": { "eq": "SKU-1" } } } } }""",
        """{ "match": { "item.parts.weight": { "gt": 0 } } }""",
    ];

    public static TheoryData<string, string> UnwoundSorts()
    {
        var data = new TheoryData<string, string>();
        string[] parents = ["name", "optionalCount", "optionalMoment", "note", "active"];
        var turn = 0;

        foreach (var (id, unwind, element, position) in Unwinds)
        {
            var columns = Project([.. element, .. position is null ? Array.Empty<string>() : [position], "optionalCount"]);
            var items = id.StartsWith("items", StringComparison.Ordinal);

            string? Narrowed() => items ? ElementMatches[turn++ % ElementMatches.Length] : null;

            data.Add($"{id}/default", Stages(unwind, columns));
            data.Add($"{id}/default-narrowed", Stages(unwind, Narrowed(), columns));

            foreach (var direction in Directions)
            {
                // By the parents, by the element, by both, and with the key and the position in every place.
                foreach (var parent in parents)
                    data.Add($"{id}/{parent}:{direction}", Stages(unwind, Narrowed(), Sort((parent, direction)), columns));

                foreach (var member in element)
                {
                    data.Add($"{id}/{member}:{direction}", Stages(unwind, Narrowed(), Sort((member, direction)), columns));
                    data.Add($"{id}/optionalCount:asc,{member}:{direction}", Stages(unwind, Sort(("optionalCount", "asc"), (member, direction)), columns));
                    data.Add($"{id}/{member}:{direction},note:desc", Stages(unwind, Narrowed(), Sort((member, direction), ("note", "desc")), columns));
                    data.Add($"{id}/{member}:{direction},id:desc", Stages(unwind, Sort((member, direction), ("id", "desc")), columns));
                }

                data.Add($"{id}/id:{direction}", Stages(unwind, Narrowed(), Sort(("id", direction)), columns));
                data.Add($"{id}/note:asc,id:{direction}", Stages(unwind, Sort(("note", "asc"), ("id", direction)), columns));

                if (position is null)
                    continue;

                data.Add($"{id}/position:{direction}", Stages(unwind, Narrowed(), Sort((position, direction)), columns));
                data.Add($"{id}/optionalCount:desc,position:{direction}", Stages(unwind, Sort(("optionalCount", "desc"), (position, direction)), columns));
                data.Add($"{id}/note:asc,position:{direction},id:desc", Stages(unwind, Sort(("note", "asc"), (position, direction), ("id", "desc")), columns));
                data.Add($"{id}/note:desc,active:asc,position:{direction}", Stages(unwind, Narrowed(), Sort(("note", "desc"), ("active", "asc"), (position, direction)), columns));
                data.Add($"{id}/position:{direction},optionalCount:asc", Stages(unwind, Sort((position, direction), ("optionalCount", "asc")), columns));
                data.Add($"{id}/id:asc,position:{direction}", Stages(unwind, Sort(("id", "asc"), (position, direction)), columns));
            }
        }

        // Two collections, one inside the other, and a lookup's rows unwound.
        const string Nested = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "unwind": { "path": "item.parts", "as": "part", "includeIndex": "at" } }""";
        const string NestedColumns = """{ "project": { "id": 1, "position": 1, "at": 1, "part.sku": 1, "item.code": 1 } }""";

        data.Add("nested/default", Stages(Nested, NestedColumns));
        data.Add("nested/part.sku:asc", Stages(Nested, Sort(("part.sku", "asc")), NestedColumns));
        data.Add("nested/part.weight:desc,item.code:asc", Stages(Nested, Sort(("part.weight", "desc"), ("item.code", "asc")), NestedColumns));
        data.Add("nested/note:desc,at:desc", Stages(Nested, Sort(("note", "desc"), ("at", "desc")), NestedColumns));
        data.Add("nested/at:asc,position:desc,id:desc", Stages(Nested, """{ "match": { "part.weight": { "gte": 0 } } }""", Sort(("at", "asc"), ("position", "desc"), ("id", "desc")), NestedColumns));
        data.Add("nested/preserve", Stages("""{ "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }, { "unwind": { "path": "item.parts", "as": "part", "includeIndex": "at", "preserveNull": true } }""", Sort(("optionalCount", "asc")), NestedColumns));

        const string Kids = """{ "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids" } }, { "unwind": { "path": "kids", "as": "kid", "includeIndex": "position" } }""";
        const string KidColumns = """{ "project": { "id": 1, "position": 1, "kid.name": 1, "kid.sequence": 1 } }""";

        data.Add("kids/default", Stages(Kids, KidColumns));
        data.Add("kids/kid.sequence:desc", Stages(Kids, Sort(("kid.sequence", "desc")), KidColumns));
        data.Add("kids/kid.name:asc,count:desc", Stages(Kids, """{ "match": { "kid.isDeleted": { "neq": true } } }""", Sort(("kid.name", "asc"), ("count", "desc")), KidColumns));
        data.Add("kids/note:asc,position:desc", Stages(Kids, Sort(("note", "asc"), ("position", "desc")), KidColumns));

        return data;
    }

    [Theory]
    [MemberData(nameof(UnwoundSorts))]
    public async Task U_a_walk_over_unwound_rows_is_the_one_page_split(string id, string stages)
    {
        await HoldsAsync(id, new Walks.Query(Entity, stages), [5, 13]);
    }

    // ── groups ────────────────────────────────────────────────────────────────────────────

    private static readonly (string Id, string Keys, string[] Aliases)[] Groupings =
    [
        ("note", """{ "path": "note", "as": "k" }""", ["k"]),
        ("optionalCount", """{ "path": "optionalCount", "as": "k" }""", ["k"]),
        ("optionalActive", """{ "path": "optionalActive", "as": "k" }""", ["k"]),
        ("optionalState", """{ "path": "optionalState", "as": "k" }""", ["k"]),
        ("optionalMarker", """{ "path": "optionalMarker", "as": "k" }""", ["k"]),
        ("optionalAmount", """{ "path": "optionalAmount", "as": "k" }""", ["k"]),
        ("optionalDay", """{ "path": "optionalDay", "as": "k" }""", ["k"]),
        ("optionalDuration", """{ "path": "optionalDuration", "as": "k" }""", ["k"]),
        ("optionalGrade", """{ "path": "optionalGrade", "as": "k" }""", ["k"]),
        ("name", """{ "path": "name", "as": "k" }""", ["k"]),
        ("note+count", """{ "path": "note", "as": "k" }, { "path": "count", "as": "c" }""", ["k", "c"]),
        ("optionalCount+optionalActive+grade", """{ "path": "optionalCount", "as": "k" }, { "path": "optionalActive", "as": "a" }, { "path": "grade", "as": "g" }""", ["k", "a", "g"]),
        ("address.city+optionalAddress.zip", """{ "path": "address.city", "as": "k" }, { "path": "optionalAddress.zip", "as": "z" }""", ["k", "z"]),
        ("day-berlin", """{ "dateTrunc": { "path": "optionalMoment", "unit": "day", "timezone": "Europe/Berlin" }, "as": "k" }""", ["k"]),
        ("week-sunday-berlin", """{ "dateTrunc": { "path": "optionalMoment", "unit": "week", "timezone": "Europe/Berlin", "weekStart": "sunday" }, "as": "k" }""", ["k"]),
        ("month-kolkata", """{ "dateTrunc": { "path": "optionalMoment", "unit": "month", "timezone": "Asia/Kolkata" }, "as": "k" }""", ["k"]),
        ("year-utc", """{ "dateTrunc": { "path": "optionalMoment", "unit": "year" }, "as": "k" }""", ["k"]),
        ("hour-newyork+count", """{ "dateTrunc": { "path": "moment", "unit": "hour", "timezone": "America/New_York" }, "as": "k" }, { "path": "count", "as": "c" }""", ["k", "c"]),
        ("quarter-of-day", """{ "dateTrunc": { "path": "optionalDay", "unit": "quarter", "timezone": "Pacific/Auckland" }, "as": "k" }""", ["k"]),
        ("id", """{ "path": "id", "as": "k" }""", ["k"]),
    ];

    private const string Fields = """ "n": { "count": true }, "total": { "sum": "count" }, "top": { "max": "optionalRatio" }, "mean": { "avg": "optionalAmount" }, "low": { "min": "note" }, "kinds": { "countDistinct": "optionalCount" } """;

    public static TheoryData<string, string> GroupSorts()
    {
        var data = new TheoryData<string, string>();
        var turn = 0;

        foreach (var (id, keys, aliases) in Groupings)
        {
            var group = $$"""{ "group": { "by": [{{keys}}], "fields": { {{Fields}} } } }""";

            data.Add($"{id}/default", Stages(Matches[turn++ % Matches.Length], group));

            foreach (var direction in Directions)
            {
                data.Add($"{id}/{aliases[0]}:{direction}", Stages(group, Sort((aliases[0], direction))));
                data.Add($"{id}/n:{direction}", Stages(Matches[turn++ % Matches.Length], group, Sort(("n", direction))));
                data.Add($"{id}/total:{direction},{aliases[^1]}:desc", Stages(group, Sort(("total", direction), (aliases[^1], "desc"))));
                data.Add($"{id}/top:{direction}", Stages(group, Sort(("top", direction))));
                data.Add($"{id}/mean:{direction},n:asc", Stages(group, Sort(("mean", direction), ("n", "asc"))));
                data.Add($"{id}/low:{direction}", Stages(group, Sort(("low", direction))));
                data.Add($"{id}/kinds:{direction},low:asc", Stages(Matches[turn++ % Matches.Length], group, Sort(("kinds", direction), ("low", "asc"))));
            }

            // A condition on the groups, and the groups of unwound rows.
            data.Add($"{id}/having", Stages(group, """{ "match": { "n": { "gte": 2 } } }""", Sort(("n", "desc"))));
        }

        const string Unwound = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""";

        data.Add("unwound/item.code", Stages(Unwound, """{ "group": { "by": [{ "path": "item.code", "as": "code" }], "fields": { "n": { "count": true }, "q": { "sum": "item.quantity" } } } }""", Sort(("q", "desc"))));
        data.Add("unwound/item.code+position", Stages(Unwound, """{ "group": { "by": [{ "path": "item.code", "as": "code" }, { "path": "position", "as": "at" }], "fields": { "n": { "count": true } } } }"""));
        data.Add("unwound/optionalCount+item.quantity", Stages(Unwound, """{ "match": { "item.quantity": { "neq": 2 } } }""", """{ "group": { "by": [{ "path": "optionalCount", "as": "k" }, { "path": "item.quantity", "as": "q" }], "fields": { "n": { "count": true } } } }""", Sort(("n", "asc"))));
        data.Add("group-of-groups", Stages("""{ "group": { "by": [{ "path": "note", "as": "k" }, { "path": "count", "as": "c" }], "fields": { "n": { "count": true } } } }""", """{ "group": { "by": [{ "path": "k", "as": "note" }], "fields": { "groups": { "count": true }, "rows": { "sum": "n" } } } }""", Sort(("rows", "desc"))));
        data.Add("group-then-resolve", Stages("""{ "group": { "by": [{ "path": "refCode", "as": "code" }], "fields": { "n": { "count": true } } } }""", """{ "resolve": { "path": "code", "as": "r" } }""", """{ "project": { "code": 1, "n": 1, "r.name": 1 } }""", Sort(("n", "desc"))));
        // A sort with ties before a group: what is first, last and pushed is the same whatever page is asked.
        const string Ordered = """{ "group": { "by": [{ "path": "optionalCount", "as": "k" }], "fields": { "first": { "first": "name" }, "last": { "last": "name" }, "all": { "push": "id" } } } }""";

        data.Add("sort-before-group/ties", Stages(Sort(("optionalMoment", "desc")), Ordered));
        data.Add("sort-before-group/one-value", Stages(Sort(("active", "asc")), """{ "match": { "count": { "gte": 0 } } }""", Ordered));
        data.Add("sort-before-group/unwound", Stages("""{ "unwind": { "path": "items", "as": "item" } }""", Sort(("note", "asc")), """{ "group": { "by": [{ "path": "count", "as": "k" }], "fields": { "first": { "first": "item.id" }, "last": { "last": "item.id" }, "all": { "push": "item.id" } } } }"""));
        data.Add("sort-before-group/projected", Stages(Sort(("optionalCount", "desc")), """{ "project": { "name": 1, "note": 1 } }""", """{ "group": { "by": [{ "path": "note", "as": "k" }], "fields": { "first": { "first": "name" }, "all": { "push": "name" } } } }"""));

        return data;
    }

    [Theory]
    [MemberData(nameof(GroupSorts))]
    public async Task G_a_walk_over_groups_is_the_one_page_split(string id, string stages)
    {
        await HoldsAsync(id, new Walks.Query(Entity, stages) { Folded = true }, [1, 3], rowByRow: true);
    }

    // ── joined rows ───────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> JoinedSorts()
    {
        var data = new TheoryData<string, string>();
        const string Reference = """{ "resolve": { "path": "refCode", "as": "r" } }""";
        const string Named = """{ "resolve": { "path": "childId", "as": "c" } }""";
        const string Remote = """{ "resolve": { "path": "employeeId", "as": "e" } }""";
        const string Kids = """{ "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids", "sort": [{ "sequence": "desc" }, { "name": "asc" }], "limit": 3 } }""";
        const string Latest = """{ "lookup": { "from": "conformance.child", "path": "parentId", "as": "latest", "sort": [{ "sequence": "desc" }], "first": true, "filter": { "isDeleted": { "neq": true } } } }""";

        foreach (var direction in Directions)
        {
            data.Add($"resolve/r.rank:{direction}", Stages(Reference, Sort(("r.rank", direction)), """{ "project": { "id": 1, "r.rank": 1, "refCode": 1 } }"""));
            data.Add($"resolve/r.name:{direction},note:asc", Stages(Reference, Sort(("r.name", direction), ("note", "asc")), """{ "project": { "id": 1, "r.name": 1, "note": 1 } }"""));
            data.Add($"resolve/note:asc,r.rank:{direction},id:desc", Stages(Reference, Sort(("note", "asc"), ("r.rank", direction), ("id", "desc")), """{ "project": { "id": 1, "r.rank": 1 } }"""));
            data.Add($"resolve/c.sequence:{direction},c.name:asc", Stages(Named, """{ "match": { "c.isDeleted": { "neq": true } } }""", Sort(("c.sequence", direction), ("c.name", "asc")), """{ "project": { "id": 1, "c.name": 1, "c.sequence": 1 } }"""));
            data.Add($"resolve/two/r.rank:{direction},c.name:desc", Stages(Reference, Named, Sort(("r.rank", direction), ("c.name", "desc")), """{ "project": { "id": 1, "r.code": 1, "c.name": 1 } }"""));
            data.Add($"resolve/display-only/optionalCount:{direction}", Stages(Reference, Named, Sort(("optionalCount", direction)), """{ "project": { "id": 1, "r.name": 1, "c.name": 1, "optionalCount": 1 } }"""));
            data.Add($"resolve/outcome/note:{direction}", Stages("""{ "resolve": { "path": "refCode", "as": "r", "outcomeAs": "found", "filter": { "isDeleted": { "neq": true } } } }""", """{ "match": { "found": { "in": ["resolved", "excluded", "not_found"] } } }""", Sort(("note", direction)), """{ "project": { "id": 1, "found": 1, "r.name": 1 } }"""));
            data.Add($"remote/note:{direction}", Stages(Remote, Sort(("note", direction)), """{ "project": { "id": 1, "e.matchCode": 1, "note": 1 } }"""));
            data.Add($"remote/semi-join/optionalCount:{direction}", Stages(Remote, """{ "match": { "e.matchCode": { "startsWith": "emp" } } }""", Sort(("optionalCount", direction)), """{ "project": { "id": 1, "e.matchCode": 1 } }"""));
            data.Add($"remote/whole/id:{direction}", Stages(Remote, Sort(("id", direction)), """{ "project": { "id": 1, "e": 1 } }"""));
            data.Add($"lookup/kids/optionalMoment:{direction}", Stages(Kids, Sort(("optionalMoment", direction)), """{ "project": { "id": 1, "kids.name": 1, "kids.sequence": 1 } }"""));
            data.Add($"lookup/latest/latest.sequence:{direction}", Stages(Latest, Sort(("latest.sequence", direction), ("count", "asc")), """{ "project": { "id": 1, "latest.name": 1, "latest.sequence": 1 } }"""));
            data.Add($"lookup/latest/match/note:{direction}", Stages(Latest, """{ "match": { "latest.sequence": { "gte": 1 } } }""", Sort(("note", direction)), """{ "project": { "id": 1, "latest.name": 1 } }"""));
            data.Add($"lookup/kids/match/count:{direction}", Stages(Kids, """{ "match": { "kids.sequence": { "eq": 2 } } }""", Sort(("count", direction)), """{ "project": { "id": 1, "kids.sequence": 1 } }"""));
            data.Add($"unwound-then-resolve/item.code:{direction}", Stages("""{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""", Reference, Sort(("item.code", direction), ("r.rank", "asc")), """{ "project": { "id": 1, "position": 1, "item.code": 1, "r.rank": 1 } }"""));
            data.Add($"resolve-then-unwound/note:{direction}", Stages(Reference, Latest, """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""", Sort(("note", direction)), """{ "project": { "id": 1, "position": 1, "r.name": 1, "latest.name": 1 } }"""));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JoinedSorts))]
    public async Task J_a_walk_over_joined_rows_is_the_one_page_split(string id, string stages)
    {
        await HoldsAsync(id, new Walks.Query(Entity, stages), [7, 31]);
    }

    // ── the two directions ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nulls come first ascending and last descending, and the key ends the order in the direction
    /// the sort gives it: so the rows by a member and the key, both descending, are the rows by both
    /// ascending, reversed. A member whose values are of several stored types orders by type first,
    /// both ways, and is reversed as well.
    /// </summary>
    [Fact]
    public async Task D_descending_is_ascending_reversed_for_every_member()
    {
        var client = await EdgeCorpus.ClientAsync();
        var differences = new List<string>();

        foreach (var member in Members)
        {
            async Task<List<string>> Keys(string direction) =>
                (await client.SendAsync(Entity, Stages(Sort((member, direction), ("id", direction)), """{ "project": { "id": 1 } }""", """{ "page": { "limit": 500 } }"""))).ShouldBeOk(member).Ids().Select(id => id.ToString("D")).ToList();

            var ascending = await Keys("asc");
            var descending = await Keys("desc");

            ascending.Should().HaveCount(EdgeCorpus.Count, member);

            if (!descending.SequenceEqual(ascending.AsEnumerable().Reverse()))
                differences.Add($"{member}: {Walks.Describe([.. ascending.AsEnumerable().Reverse()], descending)}");
        }

        differences.Should().BeEmpty();
    }

    /// <summary>A cursor continues the query it came from, and no other: another sort, another filter, another organisation.</summary>
    [Fact]
    public async Task C_a_cursor_of_one_query_is_refused_on_another()
    {
        var client = await EdgeCorpus.ClientAsync();
        string[] queries =
        [
            Stages(Sort(("note", "asc"))),
            Stages(Sort(("note", "desc"))),
            Stages(Sort(("note", "asc"), ("id", "desc"))),
            Stages(Sort(("note", "asc"), ("count", "asc"))),
            Stages(Matches[1], Sort(("note", "asc"))),
            Stages(Matches[2], Sort(("note", "asc"))),
            Stages("""{ "unwind": { "path": "items", "as": "item" } }""", Sort(("note", "asc"))),
            Stages("""{ "unwind": { "path": "items", "as": "item", "preserveNull": true } }""", Sort(("note", "asc"))),
            Stages("""{ "unwind": { "path": "tags", "as": "item" } }""", Sort(("note", "asc"))),
            Stages("""{ "group": { "by": [{ "path": "note", "as": "note" }], "fields": { "n": { "count": true } } } }""", Sort(("note", "asc"))),
            Stages("""{ "group": { "by": [{ "path": "note", "as": "note" }], "fields": { "n": { "sum": "count" } } } }""", Sort(("note", "asc"))),
            Stages("""{ "sort": [{ "note": { "direction": "asc", "caseSensitive": true } }] }"""),
        ];
        var cursors = new List<string>();

        foreach (var query in queries)
        {
            var first = await client.QueryAsync(new Walks.Query(Entity, query).Body(new() { ["limit"] = 2 }));

            first.ShouldBeOk(query);
            first.HasNextPage.Should().BeTrue(query);
            cursors.Add(first.NextCursor!);
        }

        cursors.Should().OnlyHaveUniqueItems();

        for (var asked = 0; asked < queries.Length; asked++)
        {
            for (var issued = 0; issued < queries.Length; issued++)
            {
                var answer = await client.QueryAsync(new Walks.Query(Entity, queries[asked]).Body(new() { ["limit"] = 2, ["cursor"] = cursors[issued] }));

                if (asked == issued)
                    answer.ShouldBeOk();
                else
                    answer.ShouldRefuse("CURSOR_INVALID", 400, $"the cursor of {queries[issued]} on {queries[asked]}")["params"]!["reason"]!.GetValue<string>().Should().Be("query");
            }

            (await client.As(Org.B).QueryAsync(new Walks.Query(Entity, queries[asked]).Body(new() { ["limit"] = 2, ["cursor"] = cursors[asked] })))
                .ShouldRefuse("CURSOR_INVALID", 400, "another organisation");
        }
    }
}
