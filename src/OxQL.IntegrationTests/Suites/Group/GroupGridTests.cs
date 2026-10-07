using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Group.GroupKit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// The requests a grid that groups sends (DG2 grouping), each on the fleet's real rows: a level of
/// groups paged by value in any order, the rows of one group by its key sent back, a second level
/// under the first, the conditions of the request's <c>filter</c> run before or after the group by
/// what they read, the number of groups, and the totals as a group over no key. The engine has no
/// tree of groups: a level is a request, and these hold that the requests of a tree add up.
/// Every expectation is counted on the corpus, none is read off the engine.
/// </summary>
[Trait("Category", "Integration")]
public class GroupGridTests
{
    private const string Berlin = "Europe/Berlin";

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static IReadOnlyList<CorpusRow> Shipments => Corpus.Rows(Corpus.Shipment);

    private static JsonObject Request(string filter, string pipeline, string extra = "") =>
        JsonNode.Parse($$"""{ "entityType": "{{Corpus.Shipment}}", {{extra}} "filter": {{filter}}, "pipeline": {{pipeline}} }""")!.AsObject();

    private static IReadOnlyList<Guid> Sorted(IEnumerable<CorpusRow> rows) => rows.Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => row.Id).ToList();

    private const string Leaves = """[ { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500, "count": true } } ]""";

    // ---- a level ------------------------------------------------------------------------------

    public static TheoryData<string, string> Orders => new()
    {
        { "by the key", """[{ "number": "asc" }]""" },
        { "by an aggregate, most first", """[{ "count": "desc" }]""" },
        { "by an aggregate of dates, then the key backwards", """[{ "latest": "asc" }, { "number": "desc" }]""" },
        { "none: the engine orders by the key", "" },
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task A_level_walked_in_blocks_answers_every_group_once_in_the_order_of_one_page_whatever_orders_it(string what, string sort)
    {
        var client = await Transport();
        var expected = Shipments.GroupBy(row => Corpus.Text(row, "shipmentNumber") is { } number ? Order.FoldCi(number) : null).ToList();
        var stages = $$"""
            [ { "group": { "by": [{ "path": "shipmentNumber", "as": "number" }], "fields": { "count": { "count": true }, "latest": { "max": "loadStart" } } } }
              {{(sort.Length == 0 ? "" : $$""", { "sort": {{sort}} }""")}} ]
            """;
        var whole = await client.SendAsync(Corpus.Shipment, JsonNode.Parse(stages)!.AsArray().Append(JsonNode.Parse("""{ "page": { "limit": 500, "count": true } }""")).Select(node => node!.DeepClone()).ToArray());

        whole.ShouldBeOk(what);
        whole.Items.Should().HaveCount(expected.Count);
        whole.ShouldCount("exact", expected.Count, because: "the count of a level counts groups");
        Numbers(whole, "count").Sum().Should().Be(Shipments.Count);

        var walk = await client.WalkAsync(Corpus.Shipment, stages, limit: 3);

        walk.Pages.Should().BeGreaterThan(3);
        walk.Items.Select(row => row.ToJsonString()).Should().Equal(whole.Items.Select(row => row!.ToJsonString()), what);
        walk.Cursors.Should().OnlyContain(cursor => Cursors.Payload(cursor)["m"]!.GetValue<string>() == "k", "a level continues by value, never by a number of rows");

        // The position is the last group's values, and ends in the key: no group before it can move the next block.
        var position = Cursors.Payload(walk.Cursors[0])["v"]!.AsArray();

        position[^1]!["p"]!.GetValue<string>().Should().Be("number");
        Cursors.Payload(walk.Cursors[0])["n"]!.GetValue<int>().Should().Be(3, "the cursor knows the groups served so far");
    }

    [Fact]
    public async Task The_number_of_groups_is_asked_with_a_block_or_alone_and_a_block_without_a_next_one_knows_it()
    {
        var client = await Transport();
        var groups = Shipments.GroupBy(row => Corpus.Text(row, "shipmentNumber") is { } number ? Order.FoldCi(number) : null).Count();
        const string Group = """{ "group": { "by": [{ "path": "shipmentNumber", "as": "number" }], "fields": { "count": { "count": true } } } }""";

        (await client.SendAsync(Corpus.Shipment, $$"""[ {{Group}}, { "page": { "limit": 2, "count": true } } ]""")).ShouldCount("exact", groups);
        (await client.SendAsync(Corpus.Shipment, $$"""[ {{Group}}, { "page": { "limit": 2 } } ]""")).ShouldCount("atLeast", 3, "page");
        (await client.SendAsync(Corpus.Shipment, $$"""[ {{Group}}, { "page": { "limit": 500 } } ]""")).ShouldCount("exact", groups);
        (await client.CountOnlyAsync(Corpus.Shipment, $"[ {Group} ]")).ShouldCount("exact", groups);

        // A condition on an aggregate is on the groups, and the count follows it.
        var held = Shipments.GroupBy(row => Corpus.Text(row, "shipmentNumber") is { } number ? Order.FoldCi(number) : null).Count(group => group.Count() > 1);

        held.Should().BeGreaterThan(0);
        (await client.CountOnlyAsync(Corpus.Shipment, $$"""[ {{Group}}, { "match": { "count": { "gt": 1 } } } ]""")).ShouldCount("exact", held);
    }

    // ---- the rows of a group ------------------------------------------------------------------

    public static TheoryData<string, string> Keys => new()
    {
        { "a string, which folds case and accents", "shipmentNumber" },
        { "a string under an object", "status.name" },
        { "an enum, answered as its number", "loadingTimeType" },
        { "a guid", "department.id" },
        { "a boolean", "isDeleted" },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task A_groups_key_sent_back_as_an_equality_on_the_member_answers_the_groups_rows_and_null_the_rows_without_a_value(string what, string path)
    {
        var client = await Transport();
        var level = await client.SendAsync(Corpus.Shipment, $$"""
            [ { "group": { "by": [{ "path": "{{path}}", "as": "key" }], "fields": { "count": { "count": true }, "ids": { "push": "id" } } } }, { "page": { "limit": 500 } } ]
            """);

        level.ShouldBeOk(what);
        level.Items.Count.Should().BeGreaterThan(1, what);

        var seen = new List<Guid>();
        var undeclared = 0;

        foreach (var group in level.Items)
        {
            // The key as the wire answered it, sent back untouched: a string, a number, a boolean, or null.
            var key = group!["key"]?.ToJsonString() ?? "null";
            var leaves = await client.QueryAsync(Request($$"""[ { "id": "group:0", "where": { "{{path}}": { "eq": {{key}} } } } ]""", Leaves));

            // A stored number the enum does not declare is a group of its own, and no condition can name it:
            // an operand off the enum is refused, here as everywhere. Its rows are the group's all the same.
            if (path == "loadingTimeType" && key is not ("0" or "1" or "2" or "null"))
            {
                leaves.ShouldRefuse("UNKNOWN_ENUM_MEMBER", 400)["filter"]!.GetValue<string>().Should().Be("group:0");
                seen.AddRange(group["ids"]!.AsArray().Select(id => Guid.Parse(id!.GetValue<string>())));
                undeclared++;
                continue;
            }

            leaves.ShouldBeOk($"{what}: {key}");
            leaves.Ids().Should().BeEquivalentTo(group["ids"]!.AsArray().Select(id => Guid.Parse(id!.GetValue<string>())), $"{what}: the rows of {key} are the rows the group collected");
            leaves.Items.Should().HaveCount((int)Number(group["count"])!.Value);
            leaves.ShouldCount("exact", leaves.Items.Count);
            seen.AddRange(leaves.Ids());
        }

        seen.Should().BeEquivalentTo(Shipments.Select(row => row.Id), $"{what}: every row is in exactly one group");
        undeclared.Should().Be(path == "loadingTimeType" ? 1 : 0, "the corpus stores one number its enum does not declare");

        // The corpus holds rows without the member for these keys; they are one group, keyed null.
        if (Shipments.Any(row => !Corpus.Present(row, path)))
            level.Items.Should().ContainSingle(group => group!["key"] == null, what);
    }

    [Fact]
    public async Task Two_levels_and_their_leaves_add_up_with_each_parents_key_as_one_more_filter_entry()
    {
        var client = await Transport();
        const string Removed = """{ "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } }""";
        var rows = Shipments.Where(row => Corpus.ValueAt(row, "isDeleted") is not BsonBoolean { Value: true }).ToList();

        // Level 0: the status. Level 1 under a status: the month it loads in. Leaves under both.
        var statuses = await client.QueryAsync(Request($"[ {Removed} ]", """
            [ { "group": { "by": [{ "path": "status.name", "as": "statusName" }], "fields": { "count": { "count": true } } } }, { "sort": [{ "statusName": "asc" }] }, { "page": { "limit": 100, "count": true } } ]
            """));

        statuses.ShouldBeOk();
        Numbers(statuses, "count").Sum().Should().Be(rows.Count);

        var leavesSeen = 0;

        foreach (var status in statuses.Items)
        {
            var name = status!["statusName"]!.ToJsonString();
            var parent = $$"""{ "id": "group:0", "where": { "status.name": { "eq": {{name}} } } }""";
            var months = await client.QueryAsync(Request($"[ {Removed}, {parent} ]", $$"""
                [ { "group": { "by": [{ "dateTrunc": { "path": "loadStart", "unit": "month", "timezone": "{{Berlin}}" }, "as": "month" }], "fields": { "count": { "count": true } } } },
                  { "sort": [{ "month": "asc" }] }, { "page": { "limit": 100, "count": true } } ]
                """));

            months.ShouldBeOk(name);
            Numbers(months, "count").Sum().Should().Be(Number(status["count"]), $"the months under {name} hold its rows");

            var inStatus = rows.Where(row => Order.EqualsCi(Corpus.Text(row, "status.name")!, status["statusName"]!.GetValue<string>())).ToList();

            months.Items.Select(item => (item!["month"]!.GetValue<string>(), (int)Number(item["count"])!.Value))
                .Should().Equal(Truncation.Buckets(inStatus.Select(row => Corpus.Date(row, "loadStart")!.Value), "month", Berlin));

            foreach (var month in months.Items)
            {
                var bucket = $$"""{ "id": "group:1", "where": { "loadStart": { "dateTrunc": { "unit": "month", "timezone": "{{Berlin}}", "eq": {{month!["month"]!.ToJsonString()}} } } } }""";
                var leaves = await client.QueryAsync(Request($"[ {Removed}, {parent}, {bucket} ]", Leaves));

                leaves.ShouldHaveIds(Sorted(inStatus.Where(row => Truncation.Truncate(Corpus.Date(row, "loadStart")!.Value, "month", Berlin) == month["month"]!.GetValue<string>())));
                leaves.Items.Should().HaveCount((int)Number(month["count"])!.Value);
                leavesSeen += leaves.Items.Count;
            }
        }

        leavesSeen.Should().Be(rows.Count, "the leaves of every month of every status are the rows");
    }

    // ---- where the request's filter runs ------------------------------------------------------

    [Fact]
    public async Task The_entries_of_the_filter_run_before_the_group_on_what_feeds_it_and_after_it_on_what_it_yields()
    {
        var client = await Transport();
        const string Pipeline = """
            [ { "group": { "by": [{ "path": "shipmentNumber", "as": "shipmentNumber" }, { "path": "status.name", "as": "statusName" }], "fields": { "count": { "count": true }, "latest": { "max": "loadStart" } } } },
              { "sort": [{ "shipmentNumber": "asc" }, { "statusName": "asc" }] }, { "page": { "limit": 500, "count": true } } ]
            """;
        const string Filter = """
            [ { "id": "preset", "where": { "isDeleted": { "neq": true } } },
              { "id": "col:number", "where": { "shipmentNumber": { "startsWith": "s" } } },
              { "id": "col:count", "where": { "count": { "gte": 1 } } },
              { "id": "col:status", "where": { "statusName": { "neq": "Closed" } } },
              { "id": "col:latest", "where": { "latest": { "dateTrunc": { "unit": "year", "timezone": "Europe/Berlin", "eq": "2026-06-01T00:00:00Z" } } } } ]
            """;
        var request = Request(Filter, Pipeline);
        var answer = await client.QueryAsync(request);
        var expected = Shipments
            .Where(row => Corpus.ValueAt(row, "isDeleted") is not BsonBoolean { Value: true })
            .Where(row => Corpus.Text(row, "shipmentNumber") is { } number && Order.FoldCi(number).StartsWith('s'))
            .GroupBy(row => (Number: Order.FoldCi(Corpus.Text(row, "shipmentNumber")!), Status: Order.FoldCi(Corpus.Text(row, "status.name")!)))
            .Where(group => !Order.EqualsCi(group.Key.Status, "Closed"))
            .Where(group => Truncation.Truncate(group.Max(row => Corpus.Date(row, "loadStart")!.Value), "year", Berlin) == Truncation.Truncate(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "year", Berlin))
            .Select(group => group.Count())
            .ToList();

        expected.Should().NotBeEmpty();
        answer.ShouldBeOk();
        Numbers(answer, "count").Order().Should().Equal(expected.Select(count => (decimal?)count).Order());
        answer.ShouldCount("exact", expected.Count);

        var explain = await client.ExplainHereAsync(new JsonObject { ["query"] = request.DeepClone(), ["include"] = new JsonArray("notes") });
        var placed = explain.Body!["filter"]!.AsArray().ToDictionary(entry => entry!["id"]!.GetValue<string>(), entry => entry!["placed"]!.ToJsonString());

        explain.Body["valid"]!.GetValue<bool>().Should().BeTrue(explain.ToString());
        placed["preset"].Should().Be("""[{"before":0,"paths":["isDeleted"]}]""", "an entity member: the rows that feed the groups");
        placed["col:number"].Should().Be("""[{"before":0,"paths":["shipmentNumber"]}]""", "a key over the member of the same name: which rows feed the buckets is which buckets there are");
        placed["col:count"].Should().Be("""[{"after":0,"paths":["count"],"having":true}]""", "an aggregate: the groups");
        placed["col:status"].Should().Be("""[{"after":0,"paths":["statusName"],"having":true}]""", "a key under a name of its own: the groups");
        placed["col:latest"].Should().Be("""[{"after":0,"paths":["latest"],"having":true}]""");
    }

    [Fact]
    public async Task A_key_named_like_another_member_of_the_entity_is_that_member_to_the_filter()
    {
        // 'status' is an object of the shipment. A key over status.name under the alias 'status' gives the name to
        // another thing; a filter entry on 'status' is still about the member, which is in the row from the entry.
        var answer = await (await Transport()).QueryAsync(Request("""[ { "id": "col:status", "where": { "status": { "eq": "Open" } } } ]""", """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "count": { "count": true } } } }, { "page": { "limit": 10 } } ]
            """));

        var error = answer.ShouldRefuse("NOT_FILTERABLE", 400);

        error["filter"]!.GetValue<string>().Should().Be("col:status");
        error["path"]!.GetValue<string>().Should().Be("status");
    }

    // ---- the totals ---------------------------------------------------------------------------

    [Fact]
    public async Task The_totals_are_a_group_over_no_key_one_row_over_what_the_filter_leaves_with_its_own_time_ceiling()
    {
        var client = await Transport();
        var month = Truncation.Buckets(Shipments.Select(row => Corpus.Date(row, "loadStart")!.Value), "month", Berlin).MaxBy(bucket => bucket.Count);
        var rows = Shipments.Where(row => Corpus.ValueAt(row, "isDeleted") is not BsonBoolean { Value: true }
            && Truncation.Truncate(Corpus.Date(row, "loadStart")!.Value, "month", Berlin) == month.Key).ToList();
        var filter = $$"""
            [ { "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } },
              { "id": "preset:month", "where": { "loadStart": { "dateTrunc": { "unit": "month", "timezone": "{{Berlin}}", "eq": "{{month.Key}}" } } } } ]
            """;
        const string Totals = """[ { "group": { "by": [], "fields": { "count": { "count": true }, "first": { "min": "loadStart" }, "last": { "max": "loadStart" }, "numbers": { "countDistinct": "shipmentNumber" } } } } ]""";

        rows.Should().NotBeEmpty();

        var totals = await client.QueryAsync(Request(filter, Totals, """ "maxTimeMs": 3000, """));

        totals.ShouldBeOk();
        totals.HasNextPage.Should().BeFalse();
        totals.NextCursor.Should().BeNull();
        totals.ShouldCount("exact", 1);

        var row = totals.Items.Should().ContainSingle().Subject!.AsObject();

        row.Select(pair => pair.Key).Should().Equal("count", "first", "last", "numbers");
        row["count"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.Number, "a count is a JSON number");
        row["count"]!.GetValue<int>().Should().Be(rows.Count);
        row["first"]!.GetValue<string>().Should().Be(Temporal.Iso(rows.Min(shipment => Corpus.Date(shipment, "loadStart")!.Value)));
        row["last"]!.GetValue<string>().Should().Be(Temporal.Iso(rows.Max(shipment => Corpus.Date(shipment, "loadStart")!.Value)));

        // The distinct values fold like a key's; a stored null is one of them, a member that is absent is none.
        row["numbers"]!.GetValue<int>().Should().Be(
            rows.Select(shipment => Corpus.Text(shipment, "shipmentNumber")).Where(number => number is not null).Select(number => Order.FoldCi(number!)).Distinct().Count()
            + (rows.Any(shipment => Corpus.Null(shipment, "shipmentNumber")) ? 1 : 0));

        // The count of the same rows, asked alone, is the total's count.
        (await client.QueryAsync(Request(filter, """[ { "page": { "limit": 0, "count": true } } ]"""))).ShouldCount("exact", rows.Count);

        // Over no row at all there is no group, so there is no row: an empty answer is a total of nothing.
        var none = await client.QueryAsync(Request("""[ { "where": { "loadStart": { "dateTrunc": { "unit": "year", "eq": "1999-06-01T00:00:00Z" } } } } ]""", Totals));

        none.ShouldBeOk();
        none.Items.Should().BeEmpty();
        none.ShouldCount("exact", 0);

        var explain = await client.ExplainHereAsync(new JsonObject { ["query"] = Request(filter, Totals, """ "maxTimeMs": 3000, """), ["include"] = new JsonArray("notes") });

        explain.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explain.ToString());
        explain.Body["result"]!["identity"]!.ToJsonString().Should().Be("""{"kind":"single"}""");
        explain.Body["result"]!["order"]!.AsArray().Should().BeEmpty();
        explain.Body["notes"]!.AsArray().Single(note => note!["code"]!.GetValue<string>() == "GROUP_SCANS")!.ToJsonString().Should().Be(
            """{"code":"GROUP_SCANS","message":"The totals are computed over every row that reaches the group: no page and no cap makes them cheaper, only the conditions before the group do.","stage":0,"params":{"keys":0},"basis":"advisory"}""");
        explain.Body["filter"]!.AsArray().Should().OnlyContain(entry => entry!["placed"]!.ToJsonString().StartsWith("""[{"before":0""", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_that_outlasts_its_own_ceiling_is_a_timeout_and_the_same_request_under_a_wider_one_answers()
    {
        var shared = await CorpusFleet.SharedAsync();

        await shared.SeedBulkAsync();

        var client = shared.Client(LabService.Transport, Org.C);
        const string Heavy = """[ { "sort": [ { "templateName": "desc" } ] }, { "page": { "limit": 500 } } ]""";

        JsonObject With(int ceiling) => JsonNode.Parse($$"""{ "entityType": "{{Corpus.Template}}", "maxTimeMs": {{ceiling.ToString(CultureInfo.InvariantCulture)}}, "pipeline": {{Heavy}} }""")!.AsObject();

        var tight = await client.QueryAsync(With(1));

        tight.ShouldRefuse("QUERY_TIMEOUT", 504);
        tight.Type.Should().Be("timeout");

        (await client.QueryAsync(With(30_000))).ShouldBeOk().Items.Should().HaveCount(500);

        // A ceiling above the host's is the host's: nothing is refused for asking.
        (await client.QueryAsync(With(int.MaxValue))).ShouldBeOk();

        // It is a number of milliseconds: anything else is a body the host cannot read, as for every typed member.
        var malformed = await client.QueryAsync(JsonNode.Parse($$"""{ "entityType": "{{Corpus.Template}}", "maxTimeMs": "soon", "pipeline": [ { "page": { "limit": 1 } } ] }""")!);

        malformed.StatusCode.Should().Be(400, malformed.ToString());

        // Contract 1 never had the member and ignores it.
        var legacy = await client.Contract(1).QueryAsync(JsonNode.Parse($$"""{ "entityType": "{{Corpus.Template}}", "maxTimeMs": 1, "pipeline": [ { "page": { "limit": 1 } } ] }""")!);

        legacy.ShouldBeOk();
    }
}
