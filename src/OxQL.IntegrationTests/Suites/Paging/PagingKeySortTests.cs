using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Paging;

/// <summary>
/// Paging over a sort that names the entity key. The key ends every total order; a sort that names
/// it gives it a place and a direction, and the page's order, the cursor and the rows after the
/// cursor all follow that one direction. Every case walks sixty templates in pages of seven: a
/// ten-row block sharing <c>templateName</c>, a twenty-row null and missing bracket and two
/// thirty-row halves on <c>timeMode</c>, so page boundaries fall inside a tie on the keys before
/// the entity key and inside the null bracket. The walk is compared with the corpus order and with
/// the one unpaged answer.
/// </summary>
[Trait("Category", "Integration")]
public class PagingKeySortTests
{
    private const int Limit = 7;

    private static readonly (int From, int To)[] Blocks = [(996, 1015), (1996, 2015), (2996, 3015)];

    private static readonly int Rows = Blocks.Sum(block => block.To - block.From + 1);

    private static readonly IReadOnlyDictionary<string, string?> LegacyOff = new Dictionary<string, string?> { ["OxQL:Compat:Legacy:Enabled"] = "false" };

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static bool InSet(CorpusRow row) => Blocks.Any(block => row.N >= block.From && row.N <= block.To);

    /// <summary>The match that keeps the sixty rows, on a member under <paramref name="number"/> (the wire name, or the storage name of contract 1).</summary>
    private static string Match(string number = "shipmentNumber") =>
        $$"""{ "match": { "or": [{{string.Join(", ", Blocks.Select(block => $$"""{ "{{number}}": { "gte": "SN-{{block.From:D6}}", "lte": "SN-{{block.To:D6}}" } }"""))}}] } }""";

    private static string Sort(IEnumerable<(string Path, bool Descending)> keys) =>
        $$"""{ "sort": [{{string.Join(", ", keys.Select(key => $$"""{ "{{key.Path}}": "{{(key.Descending ? "desc" : "asc")}}" }"""))}}] }""";

    /// <summary>A sort written as <c>templateName:asc,id:desc</c>.</summary>
    private static List<(string Path, bool Descending)> Keys(string sort) =>
        sort.Split(',').Select(key => (key.Split(':')[0], key.Split(':')[1] == "desc")).ToList();

    /// <summary>Every direction of the key against every direction of the members around it, the key last, first, between two members and alone.</summary>
    public static TheoryData<string, bool> KeySorts()
    {
        var data = new TheoryData<string, bool>();

        foreach (var count in new[] { false, true })
        {
            foreach (var key in new[] { "asc", "desc" })
            {
                data.Add($"id:{key}", count);

                foreach (var member in new[] { "asc", "desc" })
                {
                    data.Add($"templateName:{member},id:{key}", count);
                    data.Add($"id:{key},templateName:{member}", count);
                    data.Add($"timeMode:{member},id:{key},templateName:{member}", count);
                    data.Add($"timeMode:{member},templateName:{member},id:{key}", count);
                }
            }
        }

        return data;
    }

    /// <summary>Walks a query to its end, with <c>count: true</c> on every page when asked; every answer in order.</summary>
    private static async Task<List<WireAnswer>> WalkAsync(LabClient client, string entity, string stages, bool count, int limit = Limit)
    {
        var pages = new List<WireAnswer>();
        string? cursor = null;

        do
        {
            var page = new JsonObject { ["limit"] = limit };

            if (cursor is not null)
                page["cursor"] = cursor;

            if (count)
                page["count"] = true;

            var pipeline = (JsonArray)JsonNode.Parse(stages)!;
            pipeline.Add(new JsonObject { ["page"] = page });

            var answer = await client.SendAsync(entity, pipeline);
            answer.ShouldBeOk($"page {pages.Count + 1}");
            pages.Add(answer);
            cursor = answer.HasNextPage ? answer.NextCursor : null;
            pages.Count.Should().BeLessThan(200, "the walk did not end");
        }
        while (cursor is not null);

        return pages;
    }

    private static List<string> RowsOf(IEnumerable<WireAnswer> pages) =>
        pages.SelectMany(page => page.Items).Select(item => item!.ToJsonString()).ToList();

    [Theory]
    [MemberData(nameof(KeySorts))]
    public async Task K01_a_walk_over_a_sort_naming_the_key_serves_every_row_once_in_the_unpaged_order(string sort, bool count)
    {
        var keys = Keys(sort);
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Template, keys, filter: InSet));
        expected.Should().HaveCount(Rows);
        var stages = $$"""[{{Match()}}, {{Sort(keys)}}, { "project": { "id": 1, "templateName": 1 } }]""";
        var client = await Transport();

        var unpaged = await client.SendAsync(Corpus.Template, $$"""[{{Match()}}, {{Sort(keys)}}, { "project": { "id": 1, "templateName": 1 } }, { "page": { "limit": 500 } }]""");
        unpaged.ShouldHaveIds(expected, "the order of one page");

        var pages = await WalkAsync(client, Corpus.Template, stages, count);
        var ids = pages.SelectMany(page => page.Ids()).ToList();

        ids.Should().OnlyHaveUniqueItems("no row is served twice");
        ids.Should().Equal(expected, "the pages are the unpaged order, split");
        pages.Should().HaveCount((Rows + Limit - 1) / Limit);

        if (count)
        {
            pages[^1].ShouldCount("exact", Rows);
            pages.Should().OnlyContain(page => page.TotalCount <= Rows, "a count never exceeds the rows there are");
        }
    }

    [Theory]
    [InlineData("templateName")]
    [InlineData("timeMode")]
    public async Task K02_the_descending_walk_is_the_ascending_walk_reversed(string member)
    {
        var client = await Transport();

        async Task<List<Guid>> Walk(string direction) =>
            (await WalkAsync(client, Corpus.Template, $$"""[{{Match()}}, {{Sort(Keys($"{member}:{direction},id:{direction}"))}}, { "project": { "id": 1 } }]""", count: false))
            .SelectMany(page => page.Ids()).ToList();

        var ascending = await Walk("asc");
        var descending = await Walk("desc");

        ascending.Should().HaveCount(Rows);
        descending.Should().Equal(ascending.AsEnumerable().Reverse());
    }

    [Fact]
    public async Task K03_a_page_boundary_inside_the_null_bracket_and_inside_a_tie_of_the_earlier_key_continues_in_the_keys_direction()
    {
        var keys = Keys("templateName:asc,id:desc");
        var ordered = Corpus.Sorted(Corpus.Template, keys, filter: InSet);

        // Ascending, the twenty rows without a name lead and the ten sharing one follow the ten named before them.
        ordered.Take(20).Should().OnlyContain(row => !Corpus.Present(row, "templateName"));
        ordered.Skip(Limit - 1).Take(2).Should().OnlyContain(row => !Corpus.Present(row, "templateName"), "the first boundary lies inside the null bracket");
        var block = ordered.Select((row, index) => (row, index)).Where(entry => Corpus.Text(entry.row, "templateName") == "T-DUP").Select(entry => entry.index).ToList();
        block.Should().HaveCount(10);
        var (from, to) = (block[0], block[^1]);
        Enumerable.Range(1, Rows / Limit).Select(page => page * Limit).Should().Contain(boundary => boundary > from && boundary <= to, "a boundary lies inside the tie");

        var pages = await WalkAsync(await Transport(), Corpus.Template, $$"""[{{Match()}}, {{Sort(keys)}}, { "project": { "id": 1, "templateName": 1 } }]""", count: true);

        pages.SelectMany(page => page.Ids()).Should().Equal(Corpus.IdsOf(ordered));
        pages[0].Ids().Select(id => id.ToString("D")).Should().BeInDescendingOrder(StringComparer.Ordinal, "the rows of the bracket tie on the name and the key runs downwards");
        pages[^1].ShouldCount("exact", Rows);
    }

    /// <summary>A sort may name the key twice. The key orders once, at its first place and in the direction written last.</summary>
    [Theory]
    [InlineData("id:asc,id:desc", "id:desc")]
    [InlineData("id:desc,id:asc", "id:asc")]
    [InlineData("templateName:asc,id:asc,id:desc", "templateName:asc,id:desc")]
    [InlineData("templateName:desc,id:desc,timeMode:asc,id:asc", "templateName:desc,id:asc")]
    public async Task K04_a_sort_naming_the_key_twice_pages_in_the_order_its_one_page_has(string sort, string effective)
    {
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Template, Keys(effective), filter: InSet));
        var client = await Transport();

        var unpaged = await client.SendAsync(Corpus.Template, $$"""[{{Match()}}, {{Sort(Keys(sort))}}, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""");
        unpaged.ShouldHaveIds(expected);

        var pages = await WalkAsync(client, Corpus.Template, $$"""[{{Match()}}, {{Sort(Keys(sort))}}, { "project": { "id": 1 } }]""", count: true);

        pages.SelectMany(page => page.Ids()).Should().Equal(expected);
        pages[^1].ShouldCount("exact", Rows);
    }

    /// <summary>A member twice in a sort is one leg of the order, whatever the member: the direction written last.</summary>
    [Fact]
    public async Task K04b_a_sort_naming_another_member_twice_pages_in_the_order_its_one_page_has()
    {
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Template, Keys("templateName:desc,id:desc"), filter: InSet));
        var client = await Transport();
        var sort = Sort(Keys("templateName:asc,templateName:desc,id:desc"));

        (await client.SendAsync(Corpus.Template, $$"""[{{Match()}}, {{sort}}, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""")).ShouldHaveIds(expected);

        var pages = await WalkAsync(client, Corpus.Template, $$"""[{{Match()}}, {{sort}}, { "project": { "id": 1 } }]""", count: true);

        pages.SelectMany(page => page.Ids()).Should().Equal(expected);
        pages[^1].ShouldCount("exact", Rows);
    }

    // ── the key of another entity under an alias ──────────────────────────────────────────

    /// <summary>The key of a joined row is a member like any other: it orders the rows that hold it, and the entity's own key still ends the order, ascending unless the sort names it.</summary>
    [Theory]
    [InlineData("veh.id:asc")]
    [InlineData("veh.id:desc")]
    [InlineData("veh.id:desc,id:desc")]
    [InlineData("veh.id:asc,id:desc")]
    [InlineData("id:desc,veh.id:asc")]
    public async Task K05_a_sort_on_the_key_of_a_joined_row_is_not_the_entitys_key(string sort)
    {
        var keys = Keys(sort);
        var client = await Lab.ClientAsync(LabService.Fleet);
        var stages = $$"""[{ "resolve": { "path": "vehicle.id", "as": "veh" } }, {{Sort(keys)}}, { "project": { "id": 1, "veh.id": 1 } }]""";

        BsonValue? Joined(CorpusRow row) =>
            Corpus.GuidAt(row, "vehicle.id") is { } key && Corpus.Rows(Corpus.Vehicle).Any(vehicle => vehicle.Id == key) ? new BsonString(key.ToString("D")) : null;

        var expected = Corpus.IdsOf(Corpus.Rows(Corpus.Equipment).Order(Comparer<CorpusRow>.Create((a, b) =>
        {
            foreach (var (path, descending) in keys)
            {
                var order = path == "id" ? Corpus.CompareIds(a, b) : Order.Compare(Joined(a), Joined(b), StringOrder.Binary, descending);

                if (order != 0)
                    return descending ? -order : order;
            }

            return Corpus.CompareIds(a, b);
        })));
        expected.Count.Should().BeGreaterThan(4);

        var unpaged = await client.SendAsync(Corpus.Equipment, $$"""[{ "resolve": { "path": "vehicle.id", "as": "veh" } }, {{Sort(keys)}}, { "project": { "id": 1, "veh.id": 1 } }, { "page": { "limit": 500 } }]""");
        unpaged.ShouldHaveIds(expected);

        var pages = await WalkAsync(client, Corpus.Equipment, stages, count: true, limit: 2);

        pages.SelectMany(page => page.Ids()).Should().Equal(expected);
        pages[^1].ShouldCount("exact", expected.Count);
    }

    // ── unwound rows and groups ───────────────────────────────────────────────────────────

    /// <summary>
    /// Unwound rows share their parent's key, so the position of the element follows it: the walk is
    /// the unpaged answer, split, wherever the key and the key of an element stand in the sort.
    /// </summary>
    [Theory]
    [InlineData("id:desc")]
    [InlineData("id:asc")]
    [InlineData("loadingTimeType:asc,id:desc")]
    [InlineData("loadingTimeType:desc,id:desc")]
    [InlineData("loadingTimeType:desc,id:asc")]
    [InlineData("id:desc,loadingTimeType:asc")]
    [InlineData("item.id:desc")]
    [InlineData("item.id:asc,id:desc")]
    [InlineData("loadingTimeType:asc,id:desc,item.id:desc")]
    public async Task K06_a_walk_over_unwound_rows_sorted_on_the_key_serves_every_row_once_in_the_unpaged_order(string sort)
    {
        var keys = Keys(sort);
        var client = await Transport();
        const string Unwind = """{ "unwind": { "path": "items", "as": "item" } }""";
        const string Project = """{ "project": { "id": 1, "loadingTimeType": 1, "item.id": 1 } }""";
        var expected = Corpus.Rows(Corpus.Shipment).Sum(row => Corpus.Elements(row, "items").Count);
        expected.Should().BeGreaterThan(10);

        var unpaged = await client.SendAsync(Corpus.Shipment, $$"""[{{Unwind}}, {{Sort(keys)}}, {{Project}}, { "page": { "limit": 500 } }]""");
        unpaged.ShouldBeOk();
        unpaged.HasNextPage.Should().BeFalse();
        var rows = RowsOf([unpaged]);
        rows.Should().HaveCount(expected).And.OnlyHaveUniqueItems();

        // The parents arrive in the order of the sort's own members, the key in its direction.
        if (keys.All(key => !key.Path.StartsWith("item.", StringComparison.Ordinal)))
        {
            var parents = Corpus.IdsOf(Corpus.Sorted(Corpus.Shipment, keys, filter: row => Corpus.Elements(row, "items").Count > 0));
            var ids = unpaged.Ids();

            ids.Where((id, index) => index == 0 || ids[index - 1] != id).Should().Equal(parents);
        }

        foreach (var limit in new[] { 2, 3, 5 })
        {
            var pages = await WalkAsync(client, Corpus.Shipment, $$"""[{{Unwind}}, {{Sort(keys)}}, {{Project}}]""", count: true, limit);

            RowsOf(pages).Should().Equal(rows, $"in pages of {limit}");
            pages[^1].ShouldCount("exact", expected, because: $"in pages of {limit}");
        }
    }

    /// <summary>A group keyed by the entity's key has no key of its own: its keys end the order in the direction the sort gives each.</summary>
    [Theory]
    [InlineData("key:desc")]
    [InlineData("type:asc,key:desc")]
    [InlineData("type:desc,key:desc")]
    [InlineData("key:desc,type:asc")]
    public async Task K07_a_walk_over_groups_keyed_by_the_key_serves_every_group_once_in_the_unpaged_order(string sort)
    {
        var keys = Keys(sort);
        var client = await Transport();
        const string Group = """{ "group": { "by": [{ "path": "loadingTimeType", "as": "type" }, { "path": "id", "as": "key" }], "fields": { "total": { "count": true } } } }""";
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Shipment, keys.Select(key => (key.Path == "key" ? "id" : "loadingTimeType", key.Descending)).ToList()));

        var unpaged = await client.SendAsync(Corpus.Shipment, $$"""[{{Group}}, {{Sort(keys)}}, { "page": { "limit": 500 } }]""");
        unpaged.Ids("key").Should().Equal(expected);

        var pages = await WalkAsync(client, Corpus.Shipment, $$"""[{{Group}}, {{Sort(keys)}}]""", count: true, limit: 3);

        pages.SelectMany(page => page.Ids("key")).Should().Equal(expected);
        pages[^1].ShouldCount("exact", expected.Count);
    }

    // ── contract 1 on the rewrite ─────────────────────────────────────────────────────────

    /// <summary>Contract 1 answered by the rewrite pages over the same order: storage names, byte order, the key as <c>_id</c>.</summary>
    [Theory]
    [InlineData("_id:asc")]
    [InlineData("_id:desc")]
    [InlineData("TemplateName:asc,_id:asc")]
    [InlineData("TemplateName:asc,_id:desc")]
    [InlineData("TemplateName:desc,_id:asc")]
    [InlineData("TemplateName:desc,_id:desc")]
    [InlineData("_id:desc,TemplateName:asc")]
    [InlineData("_id:asc,TemplateName:desc")]
    [InlineData("TimeMode:asc,_id:desc,TemplateName:asc")]
    [InlineData("TimeMode:desc,TemplateName:desc,_id:desc")]
    [InlineData("TemplateName:asc")]
    [InlineData("TemplateName:desc")]
    public async Task K08_contract_1_on_the_rewrite_walks_a_sort_naming_the_key_in_the_unpaged_order(string sort)
    {
        var keys = Keys(sort);
        var oracle = keys.Select(key => (key.Path == "_id" ? "id" : char.ToLowerInvariant(key.Path[0]) + key.Path[1..], key.Descending)).ToList();
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Template, oracle, filter: InSet, strings: StringOrder.Binary));
        var client = (await CorpusFleet.SharedAsync()).Variant(LabService.Transport, "legacy-off", LegacyOff, contract: null);

        var ids = new List<Guid>();
        string? cursor = null;
        var requests = 0;
        WireAnswer answer;

        do
        {
            var pipeline = (JsonArray)JsonNode.Parse($$"""[{{Match("ShipmentNumber")}}, {{Sort(keys)}}]""")!;
            pipeline.Add(new JsonObject { ["page"] = new JsonObject { ["limit"] = Limit, ["cursor"] = cursor, ["includeTotalCount"] = true } });

            answer = await client.SendAsync(Corpus.Template, pipeline);
            answer.ShouldBeOk($"page {requests + 1}");
            answer.TotalCount.Should().Be(Rows);
            ids.AddRange(answer.Ids("_id"));
            cursor = answer.HasNextPage ? answer.NextCursor : null;
            (++requests).Should().BeLessThan(200, "the walk did not end");
        }
        while (cursor is not null);

        ids.Should().Equal(expected);
    }

    // ── cursors ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A cursor names a row by the value of every sort member and, last, by the key ascending,
    /// whether the sort names the key or not: the layout of every 2.1 release. A cursor issued by
    /// any of them continues after its row, in the order of its sort.
    /// </summary>
    [Theory]
    [InlineData("templateName:asc")]
    [InlineData("templateName:desc")]
    [InlineData("templateName:asc,id:desc")]
    [InlineData("templateName:desc,id:desc")]
    [InlineData("timeMode:desc,id:desc,templateName:asc")]
    [InlineData("id:desc,templateName:asc")]
    [InlineData("id:desc")]
    public async Task K09_a_cursor_in_the_layout_of_every_2_1_release_continues_in_the_order_of_its_sort(string sort)
    {
        var keys = Keys(sort);
        var ordered = Corpus.Sorted(Corpus.Template, keys, filter: InSet);
        var expected = Corpus.IdsOf(ordered);
        var client = await Transport();
        var stages = $$"""{{Match()}}, {{Sort(keys)}}, { "project": { "id": 1 } }""";

        var first = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": {{Limit}} } }]""");
        first.ShouldHaveIds(expected.Take(Limit));

        // What the engine issues is that layout, leg for leg.
        var issued = Cursors.Payload(first.NextCursor!);
        var legs = ((JsonArray)issued["v"]!).Select(leg => (Path: leg!["p"]!.GetValue<string>(), Direction: leg["d"]!.GetValue<string>())).ToList();
        legs.Should().Equal([.. keys.Select(key => (key.Path, key.Descending ? "desc" : "asc")), ("_id", "asc")]);

        // The same position written by hand, from the corpus: the last row of the page, in that layout.
        var codec = new CursorCodec(client.Fleet.Fleet.SigningKey);
        var requests = 0;
        var walked = new List<Guid>(expected.Take(Limit));

        while (walked.Count < Rows)
        {
            var last = ordered[walked.Count - 1];
            var fields = keys.Select(key => new CursorValue(key.Path, !key.Descending, Corpus.ValueAt(last, key.Path) ?? BsonNull.Value)).ToList();

            fields.Add(new CursorValue("_id", true, Corpus.ValueAt(last, "id")!));

            var cursor = codec.Encode(new CursorPayload(issued["f"]!.GetValue<string>(), PagingMode.Keyset, fields, 0) { Served = walked.Count });
            var page = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": {{Limit}}, "cursor": "{{cursor}}", "count": true } }]""");

            page.ShouldHaveIds(expected.Skip(walked.Count).Take(Limit), $"after {walked.Count} rows");
            walked.AddRange(page.Ids());
            (++requests).Should().BeLessThan(50);

            if (!page.HasNextPage)
                page.ShouldCount("exact", Rows);
        }

        walked.Should().Equal(expected);
    }
}
