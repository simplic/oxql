using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Paging;

/// <summary>
/// Area P: paging, cursors, ordering boundaries and counts. Paging fails silently (a dropped row
/// looks like a shorter list, a repeated one like a duplicate record), so the central cases walk
/// the whole entity and assert the concatenation of every page equals the unpaged corpus order,
/// id for id, over a sort key with a ten-row duplicate block, a ten-row null block and a ten-row
/// missing block, so page boundaries land inside a tie and inside the null bracket.
/// <para>
/// Ported from the legacy <c>paging-cursor</c> battery (engine half), plus keyset paging with a
/// sort on a local <c>resolve</c> alias.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class PagingCursorTests
{
    private const int MaxPageSize = 500;
    private const int DefaultPageSize = 100;
    private const int MaxOffset = 5_000;
    private const int CountCap = 100_000;

    private static Task<LabClient> Staff() => Lab.ClientAsync(LabService.Staff);

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static Task<LabClient> Ledger() => Lab.ClientAsync(LabService.Ledger);

    private static IReadOnlyList<Guid> TemplateIds() => Corpus.SortedIds(Corpus.Template, "id");

    // ── P1 · P2 · P3 · P4 · P5 · P6 — the page size ────────────────────────────────────────

    [Fact]
    public async Task P01_P02_no_page_stage_and_an_empty_page_stage_both_serve_exactly_the_default_page_size()
    {
        var expected = Corpus.PageOf(TemplateIds(), limit: DefaultPageSize);
        var client = await Transport();

        var none = await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }]""");
        var empty = await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": {} }]""");

        none.ShouldHaveIds(expected);
        empty.ShouldHaveIds(expected);
        none.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public async Task P03_P04_limit_1_serves_one_row_and_limit_500_exactly_max_page_size_serves_five_hundred()
    {
        var client = await Transport();

        (await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": { "limit": 1 } }]""")).ShouldHaveIds(Corpus.PageOf(TemplateIds(), limit: 1));
        (await client.SendAsync(Corpus.Template, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": {{MaxPageSize}} } }]""")).ShouldHaveIds(Corpus.PageOf(TemplateIds(), limit: MaxPageSize));
    }

    [Fact]
    public async Task P05_P06_a_limit_above_max_page_size_and_a_non_positive_limit_are_refused()
    {
        var client = await Transport();

        var over = await client.SendAsync(Corpus.Template, $$"""[{ "page": { "limit": {{MaxPageSize + 1}} } }]""");
        over.ShouldRefuse("PAGE_SIZE_EXCEEDED", 400)["message"]!.GetValue<string>().Should().Contain(MaxPageSize.ToString(CultureInfo.InvariantCulture));
        over.ErrorCodes.Should().Equal("PAGE_SIZE_EXCEEDED");

        foreach (var limit in new[] { 0, -1 })
        {
            var answer = await client.SendAsync(Corpus.Template, $$"""[{ "page": { "limit": {{limit}} } }]""");
            answer.ShouldRefuse("INVALID_PAGE_LIMIT", 400, $"limit {limit}");
            answer.ErrorCodes.Should().Equal(["INVALID_PAGE_LIMIT"], $"limit {limit}");
        }
    }

    // ── P7 · P8 · P9 · P10 · P11 · P12 · P13 — the offset and the stage ─────────────────────

    [Fact]
    public async Task P07_offset_0_is_the_same_page_as_no_offset()
    {
        var client = await Transport();

        var zero = await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": { "limit": 3, "offset": 0 } }]""");
        var none = await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": { "limit": 3 } }]""");

        zero.ShouldHaveIds(Corpus.PageOf(TemplateIds(), limit: 3));
        none.ShouldHaveIds(zero.Ids());
    }

    [Fact]
    public async Task P08_offset_5000_exactly_max_offset_answers_the_corpus_slice_at_5000()
    {
        var expected = Corpus.PageOf(TemplateIds(), limit: 2, offset: MaxOffset);
        expected.Should().HaveCount(2);

        var answer = await (await Transport()).SendAsync(Corpus.Template, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": 2, "offset": {{MaxOffset}} } }]""");

        answer.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task P09_P10_an_offset_above_max_offset_and_a_negative_offset_are_refused()
    {
        var client = await Transport();

        var over = await client.SendAsync(Corpus.Template, $$"""[{ "page": { "limit": 2, "offset": {{MaxOffset + 1}} } }]""");
        over.ShouldRefuse("MAX_OFFSET_EXCEEDED", 400)["message"]!.GetValue<string>().Should().Contain(MaxOffset.ToString(CultureInfo.InvariantCulture));
        over.ErrorCodes.Should().Equal("MAX_OFFSET_EXCEEDED");

        var negative = await client.SendAsync(Corpus.Template, """[{ "page": { "limit": 2, "offset": -1 } }]""");
        negative.ShouldRefuse("INVALID_PAGE_LIMIT", 400);
        negative.ErrorCodes.Should().Equal("INVALID_PAGE_LIMIT");
    }

    [Fact]
    public async Task P11_a_cursor_and_an_offset_together_are_refused()
    {
        var client = await Transport();
        var first = await client.SendAsync(Corpus.Template, """[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 2 } }]""");
        first.ShouldBeOk();

        var answer = await client.SendAsync(Corpus.Template, $$"""[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 2, "offset": 1, "cursor": "{{first.NextCursor}}" } }]""");

        answer.ShouldRefuse("INVALID_PAGE_LIMIT", 400);
        answer.ErrorCodes.Should().Equal("INVALID_PAGE_LIMIT");
    }

    [Fact]
    public async Task P12_P13_two_page_stages_and_any_stage_after_the_page_stage_are_refused()
    {
        var client = await Transport();

        var twice = await client.SendAsync(Corpus.Template, """[{ "page": { "limit": 2 } }, { "page": { "limit": 2 } }]""");
        twice.ShouldRefuse("MULTIPLE_PAGE_STAGES", 400);
        twice.ErrorCodes.Should().Equal("MULTIPLE_PAGE_STAGES");

        var after = await client.SendAsync(Corpus.Template, """[{ "page": { "limit": 2 } }, { "sort": [{ "id": "asc" }] }]""");
        after.ShouldRefuse("STAGE_AFTER_PAGE", 400)["stage"]!.GetValue<int>().Should().Be(1);
        after.ErrorCodes.Should().Equal("STAGE_AFTER_PAGE");
    }

    // ── P15 · P16 · P17 · P18 — the hasNextPage boundary ───────────────────────────────────

    [Fact]
    public async Task P15_P16_P17_P18_has_next_page_at_exactly_the_remaining_rows_one_fewer_one_more_and_none()
    {
        var rows = Corpus.Counts(Corpus.Employee).A;
        var ordered = Corpus.SortedIds(Corpus.Employee, "id");
        var client = await Staff();

        var exact = await client.SendAsync(Corpus.Employee, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": {{rows}} } }]""");
        exact.ShouldHaveIds(ordered);
        exact.HasNextPage.Should().BeFalse();
        exact.NextCursor.Should().BeNull();

        var shortPage = await client.SendAsync(Corpus.Employee, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": {{rows - 1}} } }]""");
        shortPage.ShouldHaveIds(ordered.Take(rows - 1), "the page is trimmed to the limit, not to limit + 1");
        shortPage.HasNextPage.Should().BeTrue();
        shortPage.NextCursor.Should().NotBeNullOrEmpty();

        var longPage = await client.SendAsync(Corpus.Employee, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": {{rows + 1}} } }]""");
        longPage.ShouldHaveIds(ordered);
        longPage.HasNextPage.Should().BeFalse();

        var empty = await client.SendAsync(Corpus.Employee, """[{ "match": { "matchCode": { "eq": "no such match code" } } }, { "page": { "limit": 10 } }]""");
        empty.ShouldHaveIds([]);
        empty.HasNextPage.Should().BeFalse();
        empty.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task P18_the_last_page_of_a_walk_carries_no_cursor_although_every_earlier_one_did()
    {
        var rows = Corpus.Counts(Corpus.Employee).A;

        var walk = await (await Staff()).WalkAsync(Corpus.Employee, """[{ "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }]""", limit: 10);

        walk.Pages.Should().Be((rows + 9) / 10);
        walk.Cursors.Should().HaveCount(walk.Pages - 1);
        walk.Last.HasNextPage.Should().BeFalse();
        walk.Last.NextCursor.Should().BeNull();
        walk.Ids.Should().Equal(Corpus.SortedIds(Corpus.Employee, "id"));
    }

    // ── P19 — what a cursor carries ───────────────────────────────────────────────────────

    [Fact]
    public async Task P19_a_keyset_cursor_carries_the_last_rows_sort_values_and_its_id_as_canonical_extended_json()
    {
        var fourth = Corpus.Rows(Corpus.Employee).Single(row => row.Id == Corpus.SortedIds(Corpus.Employee, "matchCode")[3]);

        var answer = await (await Staff()).SendAsync(Corpus.Employee, """[{ "sort": [{ "matchCode": "asc" }] }, { "page": { "limit": 4 } }]""");

        answer.ShouldHaveIds(Corpus.PageOf(Corpus.SortedIds(Corpus.Employee, "matchCode"), limit: 4));
        var head = Cursors.Payload(answer.NextCursor!);
        head["m"]!.GetValue<string>().Should().Be("k");
        head["o"]!.GetValue<int>().Should().Be(0);
        var legs = ((JsonArray)head["v"]!).Select(leg => leg!.AsObject()).ToList();
        legs.Select(leg => leg["p"]!.GetValue<string>()).Should().Equal("matchCode", "_id");
        legs.Select(leg => leg["d"]!.GetValue<string>()).Should().Equal("asc", "asc");
        // The id leg decodes back to the last row of the page, byte for byte; the sort leg is that
        // row's own matchCode, spelled as JSON.
        Cursors.Guid(legs[1]["b"]!.GetValue<string>()).Should().Be(fourth.Id);
        JsonNode.Parse(legs[0]["b"]!.GetValue<string>())!.GetValue<string>().Should().Be(Corpus.Text(fourth, "matchCode"));
    }

    // ── P20 · P23 · P24 · P25 · P26 · P27 · P28 · P36 — the walk ───────────────────────────

    [Fact]
    public async Task P20_P23_P26_the_cursor_walk_over_a_key_with_a_null_block_and_a_duplicate_block_is_disjoint_and_exhaustive()
    {
        var expected = Corpus.SortedIds(Corpus.Template, "templateName");
        expected.Should().HaveCount(Corpus.TemplateVolume);

        // 250 divides neither the null block (at 2001) nor the duplicate block (at 1001) in a way
        // that keeps them on one page, so boundaries land inside both.
        var walk = await (await Transport()).WalkAsync(Corpus.Template, """[{ "sort": [{ "templateName": "asc" }] }, { "project": { "id": 1, "templateName": 1 } }]""", limit: 250);

        walk.Pages.Should().Be(Corpus.TemplateVolume / 250);
        walk.Ids.Should().Equal(expected);
        walk.Ids.Should().OnlyHaveUniqueItems();
        // P26: a naive $gt template would have stalled on the first null.
        Corpus.RowsNull(Corpus.Template, "templateName").Should().HaveCount(10);
        walk.Ids.Should().Contain(Corpus.IdsOf(Corpus.RowsNull(Corpus.Template, "templateName")));
    }

    [Fact]
    public async Task P20_P24_P25_the_descending_walk_over_the_same_key_is_disjoint_exhaustive_and_ends_in_the_null_bracket()
    {
        var expected = Corpus.SortedIds(Corpus.Template, "templateName", descending: true);
        var nullish = Corpus.IdsWhere(Corpus.Template, row => !Corpus.Present(row, "templateName"));
        nullish.Should().HaveCount(20);

        var walk = await (await Transport()).WalkAsync(Corpus.Template, """[{ "sort": [{ "templateName": "desc" }] }, { "project": { "id": 1, "templateName": 1 } }]""", limit: 300);

        walk.Ids.Should().Equal(expected);
        walk.Ids.Should().OnlyHaveUniqueItems();
        walk.Ids.TakeLast(20).Should().BeEquivalentTo(nullish, "descending, the null bracket follows every present value");
    }

    [Fact]
    public async Task P20_P28_a_walk_with_a_page_size_of_3_over_a_ten_row_tie_repeats_nothing_and_drops_nothing()
    {
        var (from, to) = Corpus.TemplateDupRange;
        var expected = Corpus.IdsOf(Corpus.Rows(Corpus.Template).Where(row => row.N >= from && row.N <= to));
        expected.Should().HaveCount(10);

        var walk = await (await Transport()).WalkAsync(Corpus.Template, """[{ "match": { "templateName": { "eq": "T-DUP" } } }, { "sort": [{ "templateName": "asc" }] }, { "project": { "id": 1, "templateName": 1 } }]""", limit: 3);

        walk.Pages.Should().Be(4);
        walk.Ids.Should().Equal(expected);
    }

    [Fact]
    public async Task P27_two_sort_fields_compose_and_the_walk_across_them_is_still_exact()
    {
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Template, [("timeMode", false), ("templateName", false)]));

        var walk = await (await Transport()).WalkAsync(Corpus.Template, """[{ "sort": [{ "timeMode": "asc" }, { "templateName": "asc" }] }, { "project": { "id": 1, "timeMode": 1, "templateName": 1 } }]""", limit: 333);

        walk.Ids.Should().HaveCount(Corpus.TemplateVolume).And.OnlyHaveUniqueItems();
        walk.Ids.Should().Equal(expected);
    }

    [Fact]
    public async Task P20_the_walk_over_a_key_with_no_nulls_agrees_with_the_corpus_order_row_for_row()
    {
        var expected = Corpus.SortedIds(Corpus.Template, "shipmentNumber");

        var walk = await (await Transport()).WalkAsync(Corpus.Template, """[{ "sort": [{ "shipmentNumber": "asc" }] }, { "project": { "id": 1, "shipmentNumber": 1 } }]""", limit: 500);

        walk.Pages.Should().Be(Corpus.TemplateVolume / 500);
        walk.Ids.Should().Equal(expected);
    }

    [Fact]
    public async Task P36_offset_paging_and_cursor_paging_return_the_same_rows_at_the_same_positions_up_to_max_offset()
    {
        const string stages = """[{ "sort": [{ "templateName": "asc" }] }, { "project": { "id": 1, "templateName": 1 } }]""";
        var client = await Transport();

        var cursorWalk = await client.WalkAsync(Corpus.Template, stages, limit: 500);
        // The last offset page a caller may ask for starts at the ceiling: offset paging reaches
        // 5 500 of 6 000 rows and no further, which is the reason the cursor exists.
        var offsetWalk = await client.OffsetWalkAsync(Corpus.Template, stages, limit: 500, rows: MaxOffset + 1);

        offsetWalk.Ids.Should().HaveCount(MaxOffset + 500).And.OnlyHaveUniqueItems();
        offsetWalk.Ids.Should().Equal(cursorWalk.Ids.Take(MaxOffset + 500));
        cursorWalk.Ids.Should().Equal(Corpus.SortedIds(Corpus.Template, "templateName"));
    }

    // ── P29 · P30 · P31 · P32 · P33 — cursor validity ──────────────────────────────────────

    [Fact]
    public async Task P29_a_cursor_is_refused_against_another_sort_another_pipeline_another_entity_and_another_organisation()
    {
        var client = await Transport();
        var first = await client.SendAsync(Corpus.Template, """[{ "sort": [{ "templateName": "asc" }] }, { "page": { "limit": 3 } }]""");
        first.ShouldBeOk();
        var cursor = first.NextCursor!;

        var same = await client.SendAsync(Corpus.Template, $$"""[{ "sort": [{ "templateName": "asc" }] }, { "page": { "limit": 3, "cursor": "{{cursor}}" } }]""");
        same.ShouldHaveIds(Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "templateName"), limit: 3, offset: 3), "the unchanged query still accepts its own cursor");

        var refused = new (string Why, string Entity, LabClient Client, string Pipeline)[]
        {
            ("other direction", Corpus.Template, client, $$"""[{ "sort": [{ "templateName": "desc" }] }, { "page": { "limit": 3, "cursor": "{{cursor}}" } }]"""),
            ("added match", Corpus.Template, client, $$"""[{ "match": { "isDeleted": { "eq": false } } }, { "sort": [{ "templateName": "asc" }] }, { "page": { "limit": 3, "cursor": "{{cursor}}" } }]"""),
            ("other entity", Corpus.Shipment, client, $$"""[{ "sort": [{ "shipmentNumber": "asc" }] }, { "page": { "limit": 3, "cursor": "{{cursor}}" } }]"""),
            ("added projection", Corpus.Template, client, $$"""[{ "sort": [{ "templateName": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 3, "cursor": "{{cursor}}" } }]"""),
            ("other organisation", Corpus.Template, client.As(Org.B), $$"""[{ "sort": [{ "templateName": "asc" }] }, { "page": { "limit": 3, "cursor": "{{cursor}}" } }]"""),
        };

        foreach (var (why, entity, sender, pipeline) in refused)
        {
            var answer = await sender.SendAsync(entity, pipeline);
            answer.ShouldRefuse("CURSOR_INVALID", 400, why);
            answer.ErrorCodes.Should().Equal(["CURSOR_INVALID"], why);
        }
    }

    [Fact]
    public async Task P30_P31_a_truncated_tampered_or_malformed_cursor_is_refused()
    {
        var client = await Transport();
        var first = await client.SendAsync(Corpus.Template, """[{ "sort": [{ "templateName": "asc" }] }, { "page": { "limit": 3 } }]""");
        var cursor = first.NextCursor!;
        var (payload, signature) = (cursor.Split('.')[0], cursor.Split('.')[1]);

        foreach (var bad in new[] { "", "nodot", "a.", "!!!.!!!", cursor[..^4], $"{payload}.{signature[..^2]}xy" })
        {
            var answer = await client.SendAsync(Corpus.Template, new JsonArray(
                JsonNode.Parse("""{ "sort": [{ "templateName": "asc" }] }"""),
                new JsonObject { ["page"] = new JsonObject { ["limit"] = 3, ["cursor"] = bad } }));

            answer.ShouldRefuse("CURSOR_INVALID", 400, $"cursor '{bad}'");
            answer.ErrorCodes.Should().Equal(["CURSOR_INVALID"], $"cursor '{bad}'");
        }
    }

    [Fact]
    public async Task P32_limit_offset_and_include_total_count_are_outside_the_fingerprint_so_the_page_size_may_change_mid_walk()
    {
        const string stages = """{ "sort": [{ "templateName": "asc" }] }, { "project": { "id": 1, "templateName": 1 } }""";
        var expected = Corpus.SortedIds(Corpus.Template, "templateName");
        var client = await Transport();

        var first = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3 } }]""");
        var wider = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 7, "cursor": "{{first.NextCursor}}", "includeTotalCount": true } }]""");

        first.ShouldHaveIds(expected.Take(3));
        wider.ShouldHaveIds(expected.Skip(3).Take(7)).ShouldHaveTotal(Corpus.TemplateVolume);
    }

    [Fact]
    public async Task P33_a_keyset_cursor_survives_a_projection_that_drops_the_key_from_the_row()
    {
        const string stages = """{ "sort": [{ "shipmentNumber": "asc" }] }, { "project": { "id": 0, "shipmentNumber": 1 } }""";
        var numbers = Corpus.Sorted(Corpus.Template, [("shipmentNumber", false)]).Select(row => Corpus.Text(row, "shipmentNumber")).ToList();
        var client = await Transport();

        var first = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3 } }]""");
        var second = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3, "cursor": "{{first.NextCursor}}" } }]""");

        first.ShouldBeOk();
        first.Strings("shipmentNumber").Should().Equal(numbers.Take(3));
        first.Items.OfType<JsonObject>().Should().OnlyContain(item => !item.ContainsKey("id"), "the key travels in the cursor, not in the row");
        second.ShouldBeOk();
        second.Strings("shipmentNumber").Should().Equal(numbers.Skip(3).Take(3));
    }

    [Fact]
    public async Task P33b_a_projection_that_drops_the_sort_key_still_mints_a_cursor_carrying_that_key_and_the_walk_advances()
    {
        const string stages = """{ "sort": [{ "shipmentNumber": "asc" }] }, { "project": { "id": 1 } }""";
        var ordered = Corpus.Sorted(Corpus.Template, [("shipmentNumber", false)]);
        var expected = Corpus.IdsOf(ordered.Take(9));
        var client = await Transport();

        var first = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3 } }]""");
        first.ShouldHaveIds(expected.Take(3));
        first.Items.OfType<JsonObject>().Should().OnlyContain(item => item.Count == 1 && item.ContainsKey("id"), "the projection is still the caller's");

        var leg = ((JsonArray)Cursors.Payload(first.NextCursor!)["v"]!)[0]!.AsObject();
        leg["p"]!.GetValue<string>().Should().Be("shipmentNumber");
        leg["d"]!.GetValue<string>().Should().Be("asc");
        JsonNode.Parse(leg["b"]!.GetValue<string>())!.GetValue<string>().Should().Be(Corpus.Text(ordered[2], "shipmentNumber"), "the row that ordered the page carried it, and so does the cursor");

        var second = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3, "cursor": "{{first.NextCursor}}" } }]""");
        var third = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3, "cursor": "{{second.NextCursor}}" } }]""");

        second.ShouldHaveIds(expected.Skip(3).Take(3));
        third.ShouldHaveIds(expected.Skip(6).Take(3));
        second.NextCursor.Should().NotBe(first.NextCursor);
        third.NextCursor.Should().NotBe(second.NextCursor);
    }

    [Fact]
    public async Task P33c_the_same_query_keeping_the_sort_key_in_the_projection_walks_correctly()
    {
        const string stages = """{ "sort": [{ "shipmentNumber": "asc" }] }, { "project": { "id": 1, "shipmentNumber": 1 } }""";
        var expected = Corpus.SortedIds(Corpus.Template, "shipmentNumber").Take(6).ToList();
        var client = await Transport();

        var first = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3 } }]""");
        var second = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 3, "cursor": "{{first.NextCursor}}" } }]""");

        first.Ids().Concat(second.Ids()).Should().Equal(expected);
    }

    [Fact]
    public async Task P33d_a_projection_that_drops_a_local_resolve_alias_sorted_on_keeps_it_out_of_the_rows()
    {
        // P33b's rule for a root sort key, for a sort key under a local join alias: the paging
        // key survives the projection in storage, and the wire row is the caller's projection.
        var answer = await (await Lab.ClientAsync(LabService.Fleet)).SendAsync(Corpus.Vehicle, """[{ "resolve": { "path": "department.id", "as": "dep" } }, { "sort": [{ "dep.name": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 7 } }]""");

        answer.ShouldHaveIds(Corpus.PageOf(Corpus.SortedIds(Corpus.Vehicle, "id"), limit: 7));
        answer.Items.OfType<JsonObject>().Should().OnlyContain(item => item.Count == 1 && item.ContainsKey("id"), "the projection is the caller's");
    }

    // ── P34 · P35 · P46 — paging a shape-changing pipeline ─────────────────────────────────

    /// <summary>The distinct transaction numbers: the groups of a group by <c>number</c>, in key order.</summary>
    private static List<string> TransactionNumbers() =>
        Corpus.Rows(Corpus.Transaction).Select(row => Corpus.Text(row, "number")!).Distinct().Order(Comparer<string>.Create(Order.CompareCollated)).ToList();

    private const string GroupByNumber = """{ "group": { "by": [{ "path": "number", "as": "number" }], "fields": { "total": { "count": true } } } }""";

    [Fact]
    public async Task P34_a_group_and_an_unwind_both_switch_the_query_to_offset_paging_and_the_cursor_carries_the_offset()
    {
        var grouped = await (await Ledger()).SendAsync(Corpus.Transaction, $$"""[{{GroupByNumber}}, { "page": { "limit": 2 } }]""");
        grouped.ShouldBeOk();
        var groupedHead = Cursors.Payload(grouped.NextCursor!);
        groupedHead["m"]!.GetValue<string>().Should().Be("o");
        groupedHead["o"]!.GetValue<int>().Should().Be(2);
        ((JsonArray)groupedHead["v"]!).Should().BeEmpty();

        var unwound = await (await Transport()).SendAsync(Corpus.Shipment, """[{ "unwind": { "path": "items" } }, { "page": { "limit": 5 } }]""");
        unwound.ShouldBeOk();
        var unwoundHead = Cursors.Payload(unwound.NextCursor!);
        unwoundHead["m"]!.GetValue<string>().Should().Be("o");
        unwoundHead["o"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task P46_P20_paging_after_a_group_is_one_page_per_request_and_the_walk_over_the_groups_is_disjoint_and_exhaustive()
    {
        var numbers = TransactionNumbers();
        numbers.Should().HaveCount(Corpus.Counts(Corpus.Transaction).A - 1, "two transactions share T-DUP");

        var walk = await (await Ledger()).WalkAsync(Corpus.Transaction, $$"""[{{GroupByNumber}}]""", limit: 5);

        walk.Pages.Should().Be((numbers.Count + 4) / 5);
        walk.Items.Select(item => item["number"]!.GetValue<string>()).Should().Equal(numbers);
    }

    [Fact]
    public async Task P35_P20_offset_paging_after_a_group_walks_the_same_groups_in_the_same_order()
    {
        var numbers = TransactionNumbers();

        var walk = await (await Ledger()).OffsetWalkAsync(Corpus.Transaction, $$"""[{{GroupByNumber}}]""", limit: 5, rows: numbers.Count);

        walk.Items.Select(item => item["number"]!.GetValue<string>()).Should().Equal(numbers);
    }

    [Fact]
    public async Task P34b_P08_an_offset_cursor_walks_past_max_offset_although_an_offset_of_that_size_is_refused()
    {
        // Legacy ran this over the bulk organisation; grouping the 6 000 templates by name already
        // gives more groups than the offset ceiling, under the default limits.
        const string stages = """{ "group": { "by": [{ "path": "templateName", "as": "name" }], "fields": { "total": { "count": true } } } }""";
        var groups = Corpus.Rows(Corpus.Template).Select(row => Corpus.Text(row, "templateName")).Distinct().Count();
        groups.Should().BeGreaterThan(MaxOffset + 500);
        var client = await Transport();

        var refused = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 2, "offset": {{MaxOffset + 1}} } }]""");
        refused.ShouldRefuse("MAX_OFFSET_EXCEEDED", 400)["message"]!.GetValue<string>().Should().Contain("use a cursor beyond it");

        string? cursor = null;
        var rows = 0;

        for (var pages = 0; rows <= MaxOffset; pages++)
        {
            pages.Should().BeLessThan(20, "the grouped walk did not reach the offset ceiling");
            var page = await client.SendAsync(Corpus.Template, $$"""[{{stages}}, { "page": { "limit": 500{{(cursor is null ? "" : $", \"cursor\": \"{cursor}\"")}} } }]""");
            page.ShouldBeOk();
            rows += page.Items.Count;
            page.HasNextPage.Should().BeTrue();
            cursor = page.NextCursor;
        }

        rows.Should().BeGreaterThan(MaxOffset);
        Cursors.Payload(cursor!)["o"]!.GetValue<int>().Should().Be(rows);
    }

    // ── P37 · P38 · P40 · P42 · P44 — counts ───────────────────────────────────────────────

    [Fact]
    public async Task P37_include_total_count_defaults_to_false_and_the_answer_carries_neither_member()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": { "limit": 2 } }]""");

        answer.ShouldHaveIds(Corpus.PageOf(TemplateIds(), limit: 2));
        answer.PageInfo.ContainsKey("totalCount").Should().BeFalse();
        answer.PageInfo.ContainsKey("totalCountCapped").Should().BeFalse();
    }

    [Fact]
    public async Task P38_P40_the_exact_count_below_the_cap_and_zero_for_a_filter_that_matches_nothing()
    {
        var dup = Corpus.Where(Corpus.Template, row => Corpus.Text(row, "templateName") == "T-DUP").Count;
        dup.Should().Be(10);
        var client = await Transport();

        (await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]"""))
            .ShouldHaveTotal(Corpus.TemplateVolume, capped: false);
        (await client.SendAsync(Corpus.Template, """[{ "match": { "templateName": { "eq": "T-DUP" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]"""))
            .ShouldHaveTotal(dup);

        var none = await client.SendAsync(Corpus.Template, """[{ "match": { "templateName": { "eq": "no such template" } } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");
        none.ShouldHaveTotal(0, capped: false);
        none.PageInfo["totalCountCapped"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task P38b_the_count_after_a_group_counts_groups_not_documents()
    {
        var groups = TransactionNumbers().Count;
        groups.Should().BeLessThan(Corpus.Counts(Corpus.Transaction).A);

        var answer = await (await Ledger()).SendAsync(Corpus.Transaction, $$"""[{{GroupByNumber}}, { "page": { "limit": 1, "includeTotalCount": true } }]""");

        answer.ShouldHaveTotal(groups);
    }

    [Fact]
    public async Task P44_the_count_request_the_client_sends_limit_1_with_the_count_answers_the_exact_total()
    {
        // The client's count() terminal writes { limit: 1, includeTotalCount: true } and yields no
        // rows (that half is the client's). The engine half: the total of that exact request.
        var client = await Transport();

        (await client.SendAsync(Corpus.Template, """[{ "match": { "templateName": { "eq": "T-DUP" } } }, { "page": { "limit": 1, "includeTotalCount": true } }]"""))
            .ShouldHaveTotal(10, capped: false);
        (await client.SendAsync(Corpus.Template, """[{ "page": { "limit": 1, "includeTotalCount": true } }]"""))
            .ShouldHaveTotal(Corpus.TemplateVolume, capped: false);
    }

    [Fact]
    public async Task P42_the_page_and_the_count_come_from_one_request_and_agree_on_a_quiet_corpus()
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": {{MaxPageSize}}, "includeTotalCount": true } }]""");

        answer.ShouldHaveTotal(Corpus.Counts(Corpus.Employee).A);
        answer.TotalCount.Should().Be(answer.Items.Count);
    }

    // ── P39 — the count cap, against the bulk organisation ─────────────────────────────────

    [Fact]
    public async Task P39_one_row_above_the_cap_counts_the_cap_flags_it_and_carries_the_diagnostic_and_exactly_the_cap_counts_exactly()
    {
        var shared = await CorpusFleet.SharedAsync();
        await shared.SeedBulkAsync();
        BulkRows.Count.Should().Be(CountCap + 1);
        var client = shared.Client(LabService.Transport, Org.C);

        var over = await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");
        over.ShouldHaveTotal(CountCap, capped: true);
        over.DiagnosticCodes.Should().Equal("TOTAL_COUNT_CAPPED");
        over.Diagnostics[0]["params"]!.ToJsonString().Should().Be($$"""{"cap":{{CountCap}}}""");

        // The last bulk name is the one row above the cap; everything below it is exactly the cap.
        var exact = await client.SendAsync(Corpus.Template, $$"""[{ "match": { "templateName": { "lt": "{{BulkRows.NameOf(BulkRows.Count)}}" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");
        exact.ShouldHaveTotal(CountCap, capped: false);
        exact.ShouldHaveNoDiagnostics();
    }

    // ── keyset paging with a sort on a local resolve alias ────────────────────────────────

    /// <summary>
    /// The rows of <paramref name="entity"/> in the order a sort on a local resolve alias gives:
    /// the target's member read off the joined row (missing when the reference names nothing or
    /// is null), then the id.
    /// </summary>
    private static IReadOnlyList<Guid> SortedByJoined(string entity, string reference, string target, string member, bool descending)
    {
        BsonValue? Joined(CorpusRow row) =>
            Corpus.GuidAt(row, reference) is { } key && Corpus.Rows(target).FirstOrDefault(candidate => candidate.Id == key) is { } hit
                ? Corpus.ValueAt(hit, member)
                : null;

        return Corpus.IdsOf(Corpus.Rows(entity).Order(Comparer<CorpusRow>.Create((a, b) =>
        {
            var primary = Order.Compare(Joined(a), Joined(b), StringOrder.Collated, descending);
            return primary != 0 ? (descending ? -primary : primary) : Corpus.CompareIds(a, b);
        })));
    }

    [Theory]
    [InlineData("D01", "asc")]
    [InlineData("D01", "desc")]
    public async Task D01_a_cursor_walk_sorted_on_a_local_resolve_alias_returns_every_row_once_in_the_joined_order(string id, string direction)
    {
        _ = id;
        var descending = direction == "desc";
        var expected = SortedByJoined(Corpus.Equipment, "vehicle.id", Corpus.Vehicle, "matchCode", descending);
        expected.Should().HaveCount(Corpus.Counts(Corpus.Equipment).A);
        var client = await Lab.ClientAsync(LabService.Fleet);

        // One page first: the order itself is right, so what follows is about the cursor only.
        var single = await client.SendAsync(Corpus.Equipment, $$"""[{ "resolve": { "path": "vehicle.id", "as": "veh" } }, { "sort": [{ "veh.matchCode": "{{direction}}" }] }, { "project": { "id": 1, "veh": 1 } }, { "page": { "limit": 100 } }]""");
        single.ShouldHaveIds(expected);

        var walk = await client.WalkAsync(Corpus.Equipment, $$"""[{ "resolve": { "path": "vehicle.id", "as": "veh" } }, { "sort": [{ "veh.matchCode": "{{direction}}" }] }, { "project": { "id": 1, "veh": 1 } }]""", limit: 2);

        walk.Ids.Should().Equal(expected, "a keyset walk must be the unpaged order, split");
    }

    [Fact]
    public async Task D01b_a_cursor_walk_over_vehicles_sorted_on_the_resolved_department_name_reaches_every_vehicle()
    {
        // Every vehicle names the same department, so the joined name ties across the entity and
        // the id alone orders it: the walk must be the id order, split in pages of seven.
        var expected = SortedByJoined(Corpus.Vehicle, "department.id", Corpus.Department, "name", descending: false);
        expected.Should().Equal(Corpus.SortedIds(Corpus.Vehicle, "id"));

        var walk = await (await Lab.ClientAsync(LabService.Fleet)).WalkAsync(Corpus.Vehicle, """[{ "resolve": { "path": "department.id", "as": "dep" } }, { "sort": [{ "dep.name": "asc" }] }, { "project": { "id": 1, "dep": 1 } }]""", limit: 7);

        walk.Ids.Should().Equal(expected);
    }
}
