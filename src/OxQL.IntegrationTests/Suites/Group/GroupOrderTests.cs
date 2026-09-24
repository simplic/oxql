using System.Diagnostics;
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
/// Area N, third part (N47–N57, AC25, AC30): what may follow a <c>group</c> and how grouped output
/// pages, re-run against the v1 measurements they overturn: a match after a group answering no
/// rows, a sort outside the group output silently ignored, grouped paging stopping after one
/// page, arrays as group keys. Plus paging over a sort after a group whose values tie across
/// groups, which the group keys complete.
/// <para>Ported from the legacy <c>group-order</c> battery; the engine half only.</para>
/// </summary>
[Trait("Category", "Integration")]
public class GroupOrderTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private const string ByStatus = """{ "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "total": { "count": true } } } }""";

    private static string BerlinDays => """{ "group": { "by": [{ "dateTrunc": { "path": "loadStart", "unit": "day", "timezone": "Europe/Berlin" }, "as": "day" }], "fields": { "total": { "count": true } } } }""";

    [Fact]
    public async Task N47_AC27_a_match_after_a_group_filters_the_groups_with_no_base_match_before_it()
    {
        var client = await Transport();
        var atLeastTen = ShipmentStatuses().Where(entry => entry.Count >= 10).ToList();
        atLeastTen.Should().NotBeEmpty().And.HaveCountLessThan(ShipmentStatuses().Count);

        var havingCount = await client.SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "match": { "total": { "gte": 10 } } }, { "sort": [{ "status": "asc" }] }, { "page": { "limit": 10, "includeTotalCount": true } }]""");
        havingCount.ShouldHaveTotal(atLeastTen.Count);
        havingCount.Items.Select(item => $"{item!["status"]}:{Number(item["total"])}").Should().Equal(atLeastTen.Select(entry => $"{entry.Status}:{entry.Count}"));

        var havingKey = await client.SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "match": { "status": { "in": ["Open", "Planned"] } } }, { "sort": [{ "status": "asc" }] }, { "page": { "limit": 10, "includeTotalCount": true } }]""");
        havingKey.ShouldHaveTotal(2);
        havingKey.Strings("status").Should().Equal("Open", "Planned");

        var bare = await client.SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "match": { "total": { "gte": 10 } } }, { "page": { "limit": 10 } }]""");
        bare.ShouldBeOk();
        bare.Items.Should().HaveCount(atLeastTen.Count);
    }

    [Fact]
    public async Task N48_a_sort_on_a_group_output_orders_the_groups_in_either_direction()
    {
        var statuses = ShipmentStatuses();
        statuses.Select(entry => entry.Count).Should().OnlyHaveUniqueItems("the counts must not tie, or the order is the tie-breaker's");
        var client = await Transport();

        var byCount = await client.SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "sort": [{ "total": "desc" }] }, { "page": { "limit": 10 } }]""");
        byCount.ShouldBeOk();
        byCount.Strings("status").Should().Equal(statuses.OrderByDescending(entry => entry.Count).Select(entry => entry.Status), "the server orders on the number, not on the string it renders");

        var byKey = await client.SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "sort": [{ "status": "desc" }] }, { "page": { "limit": 10 } }]""");
        byKey.Strings("status").Should().Equal(statuses.Select(entry => entry.Status).Reverse());

        var earliest = statuses.Select(entry => (entry.Status, Min: Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "status.name") == entry.Status).Min(row => Corpus.Date(row, "loadStart")!.Value))).ToList();
        var byMin = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "earliest": { "min": "loadStart" } } } },
              { "sort": [{ "earliest": "asc" }] },
              { "page": { "limit": 10 } } ]
            """);
        // Two statuses share their earliest instant, so the order between them is not asserted
        // here (the tie-breaker section below covers it); the values are, in order, and each
        // belongs to its status.
        byMin.ShouldBeOk();
        byMin.Strings("earliest").Should().Equal(earliest.Select(entry => entry.Min).Order().Select(Iso), "a min alias sorts on the source kind");
        byMin.Items.Select(item => $"{item!["status"]}:{item["earliest"]}").Should().BeEquivalentTo(earliest.Select(entry => $"{entry.Status}:{Iso(entry.Min)}"));
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("N49", """{ "match": { "shipmentNumber": { "eq": "S-0001" } } }""")]
    [InlineData("N49", """{ "sort": [{ "shipmentNumber": "asc" }] }""")]
    [InlineData("N49", """{ "project": { "shipmentNumber": 1 } }""")]
    public async Task N49_AC28_a_pre_group_path_is_refused_after_a_group_in_a_match_a_sort_and_a_project_alike(string id, string stage)
    {
        _ = id;
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, {{stage}}, { "page": { "limit": 10 } }]""");

        var error = answer.ShouldRefuse("UNKNOWN_PATH", 400, stage);
        answer.ErrorCodes.Should().Equal("UNKNOWN_PATH");
        error["message"]!.GetValue<string>().Should().Be("'shipmentNumber' is not an output of the group stage.");
        error["stage"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task N49b_after_a_group_there_is_no_id_either()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "match": { "id": { "eq": "{{Corpus.AllIds(Corpus.Shipment)[0]}}" } } }, { "page": { "limit": 10 } }]""");

        ShouldRefuseOnly(answer, "UNKNOWN_PATH");
    }

    [Fact]
    public async Task N50_a_numeric_alias_takes_a_numeric_filter_a_temporal_alias_a_date_and_a_string_alias_a_string_operator()
    {
        var client = await Transport();
        var statuses = ShipmentStatuses();

        // The client's between is half-open: gte and lt.
        var numeric = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "total": { "count": true }, "weight": { "sum": "actualWeight.value" } } } },
              { "match": { "total": { "gte": 6, "lt": 25 } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);
        numeric.ShouldBeOk();
        numeric.Strings("status").Should().Equal(statuses.Where(entry => entry.Count >= 6 && entry.Count < 25).Select(entry => entry.Status));

        var from = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var latest = statuses.Select(entry => (entry.Status, Max: Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "status.name") == entry.Status).Max(row => Corpus.Date(row, "loadStart")!.Value)))
            .Where(entry => entry.Max >= from).ToList();
        latest.Should().ContainSingle();
        var temporal = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "latest": { "max": "loadStart" } } } },
              { "match": { "latest": { "gte": "2027-01-01T00:00:00Z" } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);
        temporal.ShouldHaveTotal(1);
        temporal.Strings("status").Should().Equal(latest[0].Status);
        temporal.Strings("latest").Should().Equal(latest[0].Max.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

        // A string alias takes a string operator, folded under the default collation.
        var strings = await client.SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "match": { "status": { "startsWith": "op" } } }, { "page": { "limit": 10 } }]""");
        strings.Strings("status").Should().Equal(statuses.Where(entry => Order.FoldCi(entry.Status).StartsWith("op", StringComparison.Ordinal)).Select(entry => entry.Status));
    }

    [Fact]
    public async Task N51_a_group_after_a_group_binds_over_the_first_groups_outputs()
    {
        // Per status: how many distinct loadingTimeType keys (the absent row keys null) and how many documents.
        var expected = ShipmentStatuses().Select(entry =>
        {
            var rows = Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "status.name") == entry.Status);
            var buckets = rows.Select(row => Corpus.ValueAt(row, "loadingTimeType") is BsonInt32 value ? value.Value : (int?)null).Distinct().Count();
            return $"{entry.Status}:{buckets}:{entry.Count}";
        }).ToList();
        var client = await Transport();
        const string First = """{ "group": { "by": [{ "path": "status.name", "as": "status" }, { "path": "loadingTimeType", "as": "timeType" }], "fields": { "total": { "count": true } } } }""";

        var answer = await client.SendAsync(Corpus.Shipment, $$"""
            [ {{First}},
              { "group": { "by": [{ "path": "status", "as": "status" }], "fields": { "buckets": { "count": true }, "documents": { "sum": "total" } } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Items.Select(item => $"{item!["status"]}:{Number(item["buckets"])}:{Number(item["documents"])}").Should().Equal(expected);

        // A path of the first group that the second did not carry forward is gone.
        var dropped = await client.SendAsync(Corpus.Shipment, $$"""
            [ {{First}},
              { "group": { "by": [{ "path": "status", "as": "status" }], "fields": { "buckets": { "count": true } } } },
              { "match": { "timeType": { "eq": 1 } } },
              { "page": { "limit": 10 } } ]
            """);
        ShouldRefuseOnly(dropped, "UNKNOWN_PATH");
    }

    [Fact]
    public async Task N52_N54_AC29_a_grouped_total_count_is_the_number_of_groups_and_the_page_limit_does_not_cap_it()
    {
        var groups = ShipmentStatuses().Count;
        var client = await Transport();

        foreach (var limit in new[] { 1, 2, groups, 100 })
        {
            var answer = await client.SendAsync(Corpus.Shipment, $$"""[{{ByStatus}}, { "sort": [{ "status": "asc" }] }, { "page": { "limit": {{limit}}, "includeTotalCount": true } }]""");

            answer.ShouldHaveTotal(groups, because: $"limit {limit}");
            answer.Items.Should().HaveCount(Math.Min(limit, groups));
            answer.HasNextPage.Should().Be(limit < groups);
        }

        var many = await client.SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "id", "as": "key" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "key": "asc" }] }, { "page": { "limit": 4, "includeTotalCount": true } }]""");
        many.ShouldHaveTotal(Corpus.Counts(Corpus.Shipment).A);
        many.Items.Should().HaveCount(4);

        var modes = Corpus.Rows(Corpus.Template).Select(row => Corpus.ValueAt(row, "timeMode")!.AsInt32).Distinct().Count();
        var volume = await (await Transport()).SendAsync(Corpus.Template, """[{ "group": { "by": [{ "path": "timeMode", "as": "mode" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "mode": "asc" }] }, { "page": { "limit": 1, "includeTotalCount": true } }]""");
        volume.ShouldHaveTotal(modes);
        volume.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task N53_AC29_grouped_output_pages_by_offset_behind_the_cursor_with_no_group_repeated_and_none_dropped()
    {
        var expected = Truncation.Buckets(Corpus.Rows(Corpus.Shipment).Select(row => Corpus.Date(row, "loadStart")!.Value), "day", "Europe/Berlin").Select(bucket => $"{bucket.Key}:{bucket.Count}").ToList();
        expected.Should().HaveCountGreaterThan(6);
        var client = await Transport();
        var stages = $$"""[{{BerlinDays}}, { "sort": [{ "day": "asc" }] }]""";

        var walk = await client.WalkAsync(Corpus.Shipment, stages, limit: 3);

        walk.Pages.Should().Be((expected.Count + 2) / 3);
        walk.Items.Select(item => $"{item["day"]}:{Number(item["total"])}").Should().Equal(expected);

        // The cursor carries an offset: the same slices come back from a bare offset.
        for (var offset = 0; offset < expected.Count; offset += 3)
        {
            var sliced = await client.SendAsync(Corpus.Shipment, $$"""[{{BerlinDays}}, { "sort": [{ "day": "asc" }] }, { "page": { "limit": 3, "offset": {{offset}} } }]""");
            sliced.ShouldBeOk();
            sliced.Items.Select(item => $"{item!["day"]}:{Number(item["total"])}").Should().Equal(expected.Skip(offset).Take(3), $"offset {offset}");
        }
    }

    [Fact]
    public async Task N53b_a_cursor_walk_over_a_grouped_query_reaches_every_group_once()
    {
        var expected = Corpus.SortedIds(Corpus.Shipment, "id");

        var walk = await (await Transport()).WalkAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "id", "as": "key" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "key": "asc" }] }]""", limit: 7);

        walk.Items.Select(item => Guid.Parse(item["key"]!.GetValue<string>())).Should().Equal(expected);
        walk.Items.Should().OnlyContain(item => Number(item["total"]) == 1);
    }

    [Fact]
    public async Task N53c_an_unsorted_grouped_query_pages_deterministically_with_the_group_keys_as_the_order()
    {
        var expected = ShipmentStatuses().Select(entry => entry.Status).ToList();

        var walk = await (await Transport()).WalkAsync(Corpus.Shipment, $$"""[{{ByStatus}}]""", limit: 1);

        walk.Pages.Should().Be(expected.Count);
        walk.Items.Select(item => item["status"]!.GetValue<string>()).Should().Equal(expected);
    }

    [Fact]
    public async Task N55_an_object_valued_alias_is_an_opaque_leaf_its_members_are_not_addressable()
    {
        var client = await Transport();
        var closed = Corpus.ShipmentsWithStatus("Closed");
        closed.Select(row => Corpus.ValueAt(row, "status.orderNr")!.AsInt32).Distinct().Should().ContainSingle();

        var value = await client.SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "statusRow": { "first": "status" } } } }, { "sort": [{ "status": "asc" }] }, { "page": { "limit": 5 } }]""");
        value.ShouldBeOk();
        value.Items[0]!["statusRow"]!["name"]!.GetValue<string>().Should().Be("Closed");
        value.Items[0]!["statusRow"]!["orderNr"]!.GetValue<int>().Should().Be(Corpus.ValueAt(closed[0], "status.orderNr")!.AsInt32);

        var refused = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "statusRow": { "first": "status" } } } },
              { "match": { "statusRow.orderNr": { "gte": 20 } } },
              { "sort": [{ "statusRow.orderNr": "desc" }] },
              { "page": { "limit": 5 } } ]
            """);
        refused.ShouldRefuse("UNKNOWN_PATH", 400)["message"]!.GetValue<string>().Should().Be("'statusRow' is a group output; 'statusRow.orderNr' has no members.");
        refused.ErrorCodes.Should().Equal("UNKNOWN_PATH", "UNKNOWN_PATH");
    }

    [Fact]
    public async Task N56_AC8_every_page_of_a_grouped_query_recomputes_the_groups_and_a_late_page_stays_cheap()
    {
        // A measurement as much as a case: 6 000 documents, one group per name, the null and the
        // missing names one bucket together.
        var groups = Corpus.Rows(Corpus.Template).Select(row => Corpus.Text(row, "templateName") is { } name ? Order.FoldCi(name) : null).Distinct().Count();
        var client = await Transport();
        const string Stages = """[{ "group": { "by": [{ "path": "templateName", "as": "name" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "name": "asc" }] }]""";
        string? cursor = null;

        for (var page = 1; page <= 3; page++)
        {
            var watch = Stopwatch.StartNew();
            var pageStage = new JsonObject { ["limit"] = 10, ["includeTotalCount"] = page == 1 };

            if (cursor is not null)
                pageStage["cursor"] = cursor;

            var pipeline = (JsonArray)JsonNode.Parse(Stages)!;
            pipeline.Add(new JsonObject { ["page"] = pageStage });
            var answer = await client.SendAsync(Corpus.Template, pipeline);

            answer.ShouldBeOk($"page {page}");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

            if (page == 1)
            {
                answer.ShouldHaveTotal(groups);
                answer.Items[0]!["name"].Should().BeNull();
            }

            answer.HasNextPage.Should().BeTrue();
            cursor = answer.NextCursor;
        }
    }

    [Fact]
    public async Task N57_an_enum_group_key_travels_as_its_stored_number_and_takes_a_member_name_filter()
    {
        var declared = new[] { 0, 1, 2 };
        var expected = declared.Select(value => (Value: value, Count: Corpus.Where(Corpus.Shipment, row => Corpus.ValueAt(row, "loadingTimeType") is BsonInt32 stored && stored.Value == value).Count)).ToList();
        var client = await Transport();

        var answer = await client.SendAsync(Corpus.Shipment, """
            [ { "match": { "loadingTimeType": { "in": ["None", "Fixed", "FixedWithBooking"] } } },
              { "group": { "by": [{ "path": "loadingTimeType", "as": "timeType" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "timeType": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Values("timeType").Select(value => value!.GetValue<int>()).Should().Equal(expected.Select(entry => entry.Value));
        CountTexts(answer, "total").Should().Equal(expected.Select(entry => entry.Count.ToString(CultureInfo.InvariantCulture)));

        var filtered = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "loadingTimeType", "as": "timeType" }], "fields": { "total": { "count": true } } } },
              { "match": { "timeType": { "eq": "Fixed" } } },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);
        filtered.ShouldHaveTotal(1);
        filtered.Values("timeType").Select(value => value!.GetValue<int>()).Should().Equal(1);
        Numbers(filtered, "total").Should().Equal((decimal?)expected.Single(entry => entry.Value == 1).Count);
    }

    /// <summary>Every item of every organisation A shipment, with its status name, text, quantity and order number.</summary>
    private static IReadOnlyList<(string Status, string Text, double Quantity, int OrderNumber)> Items() =>
        Corpus.Rows(Corpus.Shipment)
            .SelectMany(row => Corpus.Elements(row, "items").OfType<BsonDocument>())
            .Select(item => (item["Status"]["Name"].AsString, item["Text"].AsString, item["Quantity"]["Value"].ToDouble(), item["OrderNumber"].AsInt32))
            .ToList();

    [Fact]
    public async Task AC25_all_nine_aggregates_work_after_an_unwind_on_the_alias_root()
    {
        var expected = Items().GroupBy(item => item.Status).OrderBy(group => group.Key, Comparer<string>.Create(Order.CompareCollated)).ToList();
        expected.Should().HaveCountGreaterThan(1);

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "unwind": { "path": "items", "as": "item" } },
              { "group": { "by": [{ "path": "item.status.name", "as": "status" }], "fields": {
                  "total": { "count": true }, "distinctText": { "countDistinct": "item.text" },
                  "quantity": { "sum": "item.quantity.value" }, "mean": { "avg": "item.quantity.value" },
                  "lowest": { "min": "item.orderNumber" }, "highest": { "max": "item.orderNumber" },
                  "firstText": { "first": "item.text" }, "lastText": { "last": "item.text" }, "everyText": { "push": "item.text" } } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Strings("status").Should().Equal(expected.Select(group => group.Key));

        for (var index = 0; index < expected.Count; index++)
        {
            var row = answer.Items[index]!;
            var group = expected[index].ToList();
            Number(row["total"]).Should().Be(group.Count);
            Number(row["distinctText"]).Should().Be(group.Select(item => item.Text).Distinct().Count());
            row["quantity"]!.GetValue<double>().Should().Be(group.Sum(item => item.Quantity));
            row["mean"]!.GetValue<double>().Should().BeApproximately(group.Average(item => item.Quantity), 1e-9);
            row["lowest"]!.GetValue<int>().Should().Be(group.Min(item => item.OrderNumber));
            row["highest"]!.GetValue<int>().Should().Be(group.Max(item => item.OrderNumber));
            group.Select(item => item.Text).Should().Contain(row["firstText"]!.GetValue<string>()).And.Contain(row["lastText"]!.GetValue<string>());
            ((JsonArray)row["everyText"]!).Select(text => text!.GetValue<string>()).Should().BeEquivalentTo(group.Select(item => item.Text));
        }
    }

    [Fact]
    public async Task AC25b_after_an_unwind_the_collection_path_names_the_element_and_push_is_flat()
    {
        var expected = Items().GroupBy(item => item.Status).OrderBy(group => group.Key, Comparer<string>.Create(Order.CompareCollated)).ToList();

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "unwind": { "path": "items" } },
              { "group": { "by": [{ "path": "items.status.name", "as": "status" }], "fields": { "total": { "count": true }, "quantity": { "sum": "items.quantity.value" }, "everyText": { "push": "items.text" } } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Items.Select(item => $"{item!["status"]}:{Number(item["total"])}:{Number(item["quantity"])}").Should()
            .Equal(expected.Select(group => $"{group.Key}:{group.Count()}:{(decimal)group.Sum(item => item.Quantity)}"));
        answer.Values("everyText").Should().OnlyContain(texts => ((JsonArray)texts!).All(text => text is JsonValue));
    }

    [Theory]
    [InlineData("AC30", "items")]
    [InlineData("AC30", "items.text")]
    [InlineData("AC30", "tags.name")]
    [InlineData("AC30", "billingLines.references.dataType")]
    public async Task AC30_G8_grouping_by_a_collection_path_is_refused_rather_than_keyed_on_arrays(string id, string path)
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "group": { "by": [{ "path": "{{path}}", "as": "key" }], "fields": { "total": { "count": true } } } }, { "page": { "limit": 5 } }]""");

        ShouldRefuseOnly(answer, "GROUP_ON_COLLECTION", $"{id} {path}");
    }

    // ── a sort after a group, over values that tie across groups ──────────────────────────

    [Theory]
    [InlineData("D03", 100)]
    [InlineData("D03", 500)]
    public async Task D03_a_walk_over_a_sort_on_a_tied_group_output_reaches_every_group_exactly_once(string id, int limit)
    {
        _ = id;
        // One group per template name: almost every group counts 1, so a sort on the count ties
        // across thousands of groups, and T-DUP (10) and the null bucket (20) lead.
        var groups = Corpus.Rows(Corpus.Template).GroupBy(row => Corpus.Text(row, "templateName") is { } name ? Order.FoldCi(name) : null).ToList();
        groups.Count(group => group.Count() == 1).Should().BeGreaterThan(limit * 2, "the tie must span several pages");

        var walk = await (await Transport()).WalkAsync(Corpus.Template, """[{ "group": { "by": [{ "path": "templateName", "as": "name" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "total": "desc" }] }]""", limit: limit, maxPages: 1000);

        var names = walk.Items.Select(item => item["name"]?.GetValue<string>()).ToList();
        names.Should().OnlyHaveUniqueItems("a group must not repeat across pages");
        names.Should().HaveCount(groups.Count, "a group must not be skipped across pages");
        Numbers(walk.Last, "total").Should().NotBeEmpty();
        walk.Items.Select(item => Number(item["total"])!.Value).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task D03b_the_same_grouped_page_over_tied_values_answers_the_same_groups_every_time()
    {
        var client = await Transport();
        const string Query = """[{ "group": { "by": [{ "path": "templateName", "as": "name" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "total": "desc" }] }, { "page": { "limit": 50, "offset": 1000 } }]""";

        var first = await client.SendAsync(Corpus.Template, Query);
        var second = await client.SendAsync(Corpus.Template, Query);

        first.ShouldBeOk();
        second.Strings("name").Should().Equal(first.Strings("name"));
    }
}
