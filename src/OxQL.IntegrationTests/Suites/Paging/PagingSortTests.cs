using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Paging;

/// <summary>
/// Area I: <c>sort</c> and ordering. Ordering is never asserted as a set: every case states the
/// exact id sequence, computed by <see cref="Corpus.SortedIds"/> before the query runs (BSON type
/// order, the null/missing bracket, the default collation, the <c>id: asc</c> tie-breaker). The
/// corpus carries three <c>DUP</c> match codes, a null, a missing and an empty one on the same
/// member, and a ten-row duplicate, null and missing block inside the 6 000 templates.
/// <para>Ported from the legacy <c>paging-sort</c> battery; the engine half only.</para>
/// </summary>
[Trait("Category", "Integration")]
public class PagingSortTests
{
    private static Task<LabClient> Staff() => Lab.ClientAsync(LabService.Staff);

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static Task<LabClient> Ledger() => Lab.ClientAsync(LabService.Ledger);

    private static IReadOnlyList<Guid> DupBlock()
    {
        var (from, to) = Corpus.TemplateDupRange;
        return Corpus.IdsOf(Corpus.Rows(Corpus.Template).Where(row => row.N >= from && row.N <= to));
    }

    // ── I1 · I2 · I26 · I12 · I27 · I28 — direction, nulls and the tie-breaker ─────────────

    [Fact]
    public async Task I01_a_sort_defaults_to_ascending_and_answers_the_exact_collated_corpus_order()
    {
        var expected = Corpus.SortedIds(Corpus.Employee, "matchCode");
        expected.Should().HaveCount(Corpus.Counts(Corpus.Employee).A);

        var answer = await (await Staff()).SendAsync(Corpus.Employee, """[{ "sort": [{ "matchCode": "asc" }] }, { "page": { "limit": 500 } }]""");

        answer.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task I02_a_descending_sort_answers_the_exact_reversed_order_with_nulls_last()
    {
        var expected = Corpus.SortedIds(Corpus.Employee, "matchCode", descending: true);

        var answer = await (await Staff()).SendAsync(Corpus.Employee, """[{ "sort": [{ "matchCode": "desc" }] }, { "page": { "limit": 500 } }]""");

        answer.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task I26_null_and_missing_are_one_bracket_first_ascending_and_last_descending()
    {
        var nullRow = Corpus.IdOf(Corpus.Employee, "matchcode-null");
        var missingRow = Corpus.IdOf(Corpus.Employee, "matchcode-missing");
        var emptyRow = Corpus.IdOf(Corpus.Employee, "matchcode-empty");
        var ascending = Corpus.SortedIds(Corpus.Employee, "matchCode");
        var descending = Corpus.SortedIds(Corpus.Employee, "matchCode", descending: true);

        // The corpus states the answer: null and missing share a rank and are separated by id only;
        // the empty string is a string, so it follows them ascending.
        ascending.Take(3).Should().Equal(nullRow, missingRow, emptyRow);
        descending.TakeLast(2).Should().Equal(nullRow, missingRow);

        var client = await Staff();
        (await client.SendAsync(Corpus.Employee, """[{ "sort": [{ "matchCode": "asc" }] }, { "page": { "limit": 500 } }]""")).ShouldHaveIds(ascending);
        (await client.SendAsync(Corpus.Employee, """[{ "sort": [{ "matchCode": "desc" }] }, { "page": { "limit": 500 } }]""")).ShouldHaveIds(descending);
    }

    [Fact]
    public async Task I12_I27_three_rows_sharing_a_sort_value_come_back_in_id_order_and_a_second_run_gives_the_same_sequence()
    {
        var duplicates = Corpus.IdsOf(Corpus.Employee, "dup-a", "dup-b", "dup-c");
        Corpus.SortedIds(Corpus.Employee, "matchCode", filter: row => Corpus.Text(row, "matchCode") == "DUP").Should().Equal(duplicates);

        var client = await Staff();
        const string query = """[{ "match": { "matchCode": { "eq": "DUP" } } }, { "sort": [{ "matchCode": "asc" }] }, { "page": { "limit": 10 } }]""";

        var once = await client.SendAsync(Corpus.Employee, query);
        var twice = await client.SendAsync(Corpus.Employee, query);

        once.ShouldHaveIds(duplicates);
        twice.ShouldHaveIds(once.Ids(), "the sort is total: a second run cannot reorder a tie");
    }

    [Fact]
    public async Task I12b_a_descending_sort_on_a_tie_still_breaks_by_id_ascending()
    {
        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "templateName", descending: true), limit: 10);
        expected.Should().Equal(DupBlock(), "T-DUP is the largest templateName, so descending puts the whole block first, in ascending id order");

        var answer = await (await Transport()).SendAsync(Corpus.Template, """[{ "sort": [{ "templateName": "desc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 10 } }]""");

        answer.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task I28_a_time_span_held_as_a_string_orders_beside_the_rows_that_have_none()
    {
        var sorted = Corpus.SortedIds(Corpus.Template, "loadStart.relativeTime");
        var expected = Corpus.PageOf(sorted, limit: 20);
        Corpus.Where(Corpus.Template, row => Corpus.ValueAt(row, "loadStart.relativeTime") is BsonString).Should().NotBeEmpty("the relative rows hold the span as its string");
        Corpus.Where(Corpus.Template, row => !Corpus.Present(row, "loadStart.relativeTime")).Should().NotBeEmpty("the absolute rows hold none");

        var answer = await (await Transport()).SendAsync(Corpus.Template, """[{ "sort": [{ "loadStart.relativeTime": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 20 } }]""");

        answer.ShouldHaveIds(expected);
    }

    // ── I3 · I4 — several paths and the wire shape ────────────────────────────────────────

    [Fact]
    public async Task I03_two_sort_paths_order_on_the_written_paths_in_the_written_order()
    {
        // Inside the ten-row T-DUP block shipmentNumber is unique, and descending puts the highest
        // ordinal first, which the plain id tie-breaker would not.
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Template, [("templateName", false), ("shipmentNumber", true)], filter: row => Corpus.Text(row, "templateName") == "T-DUP"));
        expected.Should().Equal(DupBlock().Reverse());

        var answer = await (await Transport()).SendAsync(Corpus.Template, """
            [ { "match": { "templateName": { "eq": "T-DUP" } } },
              { "sort": [{ "templateName": "asc" }, { "shipmentNumber": "desc" }] },
              { "page": { "limit": 20 } } ]
            """);

        answer.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task I04_a_sort_entry_carrying_two_members_is_refused_naming_the_member_it_would_have_dropped()
    {
        var client = await Transport();

        var answer = await client.SendAsync(Corpus.Template, """[{ "sort": [{ "templateName": "asc", "shipmentNumber": "desc" }] }, { "page": { "limit": 2 } }]""");

        var error = answer.ShouldRefuse("UNKNOWN_STAGE_MEMBER", 400);
        answer.ErrorCodes.Should().Equal("UNKNOWN_STAGE_MEMBER");
        error["message"]!.GetValue<string>().Should().Be("A sort entry names one path; this one also names 'shipmentNumber'. Write one object per key: [{\"templateName\": \"asc\"}, …].");
        error["path"]!.GetValue<string>().Should().Be("templateName");

        // The shape it points the caller at binds, and orders by both members.
        var split = await client.SendAsync(Corpus.Template, """[{ "sort": [{ "templateName": "asc" }, { "shipmentNumber": "desc" }] }, { "page": { "limit": 2 } }]""");

        split.ShouldHaveIds(Corpus.PageOf(Corpus.IdsOf(Corpus.Sorted(Corpus.Template, [("templateName", false), ("shipmentNumber", true)])), limit: 2));
        Legs(split.NextCursor!).Should().Equal("templateName", "shipmentNumber", "_id");
    }

    // ── I5 · I6 · I7 — direction grammar ──────────────────────────────────────────────────

    [Theory]
    [InlineData("I05", "ASC")]
    [InlineData("I05", "Desc")]
    [InlineData("I05", "up")]
    public async Task I05_a_direction_that_is_not_asc_or_desc_is_refused_case_sensitively_under_contract_2(string id, string direction)
    {
        _ = id;
        var answer = await (await Staff()).SendAsync(Corpus.Employee, $$"""[{ "sort": [{ "matchCode": "{{direction}}" }] }, { "page": { "limit": 2 } }]""");

        answer.ShouldRefuse("INVALID_SORT_DIRECTION", 400);
        answer.ErrorCodes.Should().Equal("INVALID_SORT_DIRECTION");
    }

    [Fact]
    public async Task I06_contract_1_accepts_the_direction_case_insensitively_on_a_storage_path()
    {
        // Under contract 1 the path grammar is the storage name and there is no collation, so the
        // expected order is byte order; a contract 1 row carries storage names and _id.
        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Employee, "matchCode", strings: StringOrder.Binary), limit: 2);

        var answer = await (await Staff()).Contract(1).SendAsync(Corpus.Employee, """[{ "sort": [{ "MatchCode": "ASC" }] }, { "page": { "limit": 2 } }]""");

        answer.ShouldBeOk();
        answer.Ids("_id").Should().Equal(expected, answer.ToString());
    }

    [Fact]
    public async Task I07_a_sort_entry_with_no_members_is_a_coded_refusal_envelope_not_problem_details()
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, """[{ "sort": [{}] }, { "page": { "limit": 2 } }]""");

        var error = answer.ShouldRefuse("UNKNOWN_STAGE_MEMBER", 400);
        answer.Type.Should().Be("validation_error");
        answer.IsProblemDetails.Should().BeFalse();
        answer.ErrorCodes.Should().Equal("UNKNOWN_STAGE_MEMBER");
        error["message"]!.GetValue<string>().Should().Be("A sort entry is an object of one path and a direction: {\"path\": \"asc\"}.");
    }

    // ── I8 · I9 · I11 · I22 · I24 · I25 — what cannot be sorted on ────────────────────────

    [Fact]
    public async Task I08_a_sort_crossing_a_collection_is_refused_as_not_sortable()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """[{ "sort": [{ "items.text": "asc" }] }, { "page": { "limit": 2 } }]""");

        answer.ShouldRefuse("NOT_SORTABLE", 400)["path"]!.GetValue<string>().Should().Be("items.text");
        answer.ErrorCodes.Should().Equal("NOT_SORTABLE");
    }

    [Fact]
    public async Task I09_a_sort_on_an_object_and_on_a_dictionary_is_refused_as_not_sortable()
    {
        var onObject = await (await Transport()).SendAsync(Corpus.Shipment, """[{ "sort": [{ "loadAddress": "asc" }] }, { "page": { "limit": 2 } }]""");
        onObject.ShouldRefuse("NOT_SORTABLE", 400);
        onObject.ErrorCodes.Should().Equal("NOT_SORTABLE");

        var onDictionary = await (await Staff()).SendAsync(Corpus.Employee, """[{ "sort": [{ "addon": "asc" }] }, { "page": { "limit": 2 } }]""");
        onDictionary.ShouldRefuse("NOT_SORTABLE", 400);
        onDictionary.ErrorCodes.Should().Equal("NOT_SORTABLE");
    }

    [Fact]
    public async Task I11_a_sort_on_a_member_the_wire_view_has_and_storage_does_not_is_refused_as_not_stored()
    {
        // Legacy: tours.isMirrored on the shipment. The lab model's unstored members are on the
        // conformance entity: one [BsonIgnore], one get-only. Shape.Resolve refuses an unstored
        // path for every usage but project, before the sortability test is reached.
        var client = await Lab.ClientAsync(LabService.Conformance);

        foreach (var path in new[] { "scratch", "computed" })
        {
            var answer = await client.SendAsync(Corpus.Conformance, $$"""[{ "sort": [{ "{{path}}": "asc" }] }, { "page": { "limit": 2 } }]""");

            answer.ShouldRefuse("NOT_STORED", 400, path);
            answer.ErrorCodes.Should().Equal(["NOT_STORED"], path);
        }
    }

    [Fact]
    public async Task I22_a_sort_on_a_remote_resolve_alias_is_refused_on_both_entities_that_declare_one()
    {
        var client = await Transport();

        var template = await client.SendAsync(Corpus.Template, """[{ "resolve": { "path": "createUserId", "as": "creator" } }, { "sort": [{ "creator.matchCode": "asc" }] }, { "page": { "limit": 2 } }]""");
        template.ShouldRefuse("RESOLVE_NOT_SORTABLE", 400)["path"]!.GetValue<string>().Should().Be("creator.matchCode");
        template.ErrorCodes.Should().Equal("RESOLVE_NOT_SORTABLE");

        var shipment = await client.SendAsync(Corpus.Shipment, """[{ "resolve": { "path": "department.id", "as": "dep" } }, { "sort": [{ "dep.name": "asc" }] }, { "page": { "limit": 2 } }]""");
        shipment.ShouldRefuse("RESOLVE_NOT_SORTABLE", 400);
        shipment.ErrorCodes.Should().Equal("RESOLVE_NOT_SORTABLE");

        // The control: the same resolve without the sort answers the owner's rows, so the refusal
        // is about the sort and not about the stage.
        var firstTwo = Corpus.PageOf(Corpus.Rows(Corpus.Template), limit: 2);
        var expectedCodes = firstTwo.Select(row => Corpus.Text(Corpus.Rows(Corpus.Vehicle).Single(vehicle => vehicle.Id == Corpus.GuidAt(row, "createUserId")), "matchCode")).ToList();
        expectedCodes.Should().OnlyContain(code => code != null);

        var resolved = await client.SendAsync(Corpus.Template, """[{ "resolve": { "path": "createUserId", "as": "creator" } }, { "project": { "id": 1, "creator": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 2 } }]""");

        resolved.ShouldHaveIds(Corpus.IdsOf(firstTwo));
        resolved.Strings("creator.matchCode").Should().Equal(expectedCodes);
    }

    [Fact]
    public async Task I24_a_sort_on_a_defined_addon_key_orders_the_rows_and_carries_the_sort_on_addon_diagnostic()
    {
        // Exactly one row carries addon.weight; the others have no such key, so the order is the
        // keyless rows by id and that row last.
        var expected = Corpus.SortedIds(Corpus.Employee, "addon.weight");
        expected[^1].Should().Be(Corpus.IdOf(Corpus.Employee, "addon-rich"));
        Corpus.Where(Corpus.Employee, row => Corpus.Present(row, "addon.weight")).Should().ContainSingle();

        var answer = await (await Staff()).SendAsync(Corpus.Employee, """[{ "sort": [{ "addon.weight": "asc" }] }, { "page": { "limit": 26 } }]""");

        answer.ShouldHaveIds(expected);
        answer.DiagnosticCodes.Should().Equal("SORT_ON_ADDON");
    }

    [Fact]
    public async Task I25_a_sort_on_an_undefined_addon_key_is_refused()
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, """[{ "sort": [{ "addon.nope": "asc" }] }, { "page": { "limit": 3 } }]""");

        answer.ShouldRefuse("NOT_SORTABLE", 400)["message"]!.GetValue<string>().Should().Contain("unknown to the model");
        answer.ErrorCodes.Should().Equal("NOT_SORTABLE");
    }

    // ── I13 · I16 · I17 · I19 · I20 — ordering after a group ──────────────────────────────

    /// <summary>The distinct stored convertState values, the absent row keying null, in key order.</summary>
    private static List<int?> ConvertStateKeys() =>
        Corpus.Rows(Corpus.Transaction)
            .Select(row => Corpus.ValueAt(row, "convertState") is BsonInt32 value ? value.Value : (int?)null)
            .Distinct()
            .OrderBy(value => value.HasValue)
            .ThenBy(value => value)
            .ToList();

    private static List<int?> IntKeys(WireAnswer answer, string alias) =>
        answer.Values(alias).Select(value => value is JsonValue json ? json.GetValue<int>() : (int?)null).ToList();

    [Fact]
    public async Task I16_a_grouped_shape_with_no_caller_sort_is_ordered_by_the_group_keys()
    {
        var expected = ConvertStateKeys();
        expected.Should().StartWith(new int?[] { null }).And.HaveCountGreaterThan(2);

        var answer = await (await Ledger()).SendAsync(Corpus.Transaction, """[{ "group": { "by": [{ "path": "convertState", "as": "state" }], "fields": { "total": { "count": true } } } }, { "page": { "limit": 20 } }]""");

        answer.ShouldBeOk();
        IntKeys(answer, "state").Should().Equal(expected, answer.ToString());
    }

    [Fact]
    public async Task I13_a_sort_after_a_group_orders_on_the_group_keys_and_the_rows_carry_no_id()
    {
        var expected = ConvertStateKeys().AsEnumerable().Reverse().ToList();

        var answer = await (await Ledger()).SendAsync(Corpus.Transaction, """
            [ { "group": { "by": [{ "path": "convertState", "as": "state" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "state": "desc" }] },
              { "page": { "limit": 20 } } ]
            """);

        answer.ShouldBeOk();
        IntKeys(answer, "state").Should().Equal(expected, answer.ToString());
        answer.Items.OfType<JsonObject>().Should().OnlyContain(item => !item.ContainsKey("id"), "a grouped row is the group, not a document");
    }

    [Fact]
    public async Task I17_a_group_with_no_keys_yields_exactly_one_row()
    {
        var answer = await (await Ledger()).SendAsync(Corpus.Transaction, """[{ "group": { "by": [], "fields": { "total": { "count": true } } } }, { "page": { "limit": 5 } }]""");

        answer.ShouldBeOk();
        answer.Items.Should().ContainSingle();
        long.Parse(answer.Items[0]!["total"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(Corpus.Counts(Corpus.Transaction).A);
        answer.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task I19_a_sort_after_a_group_naming_a_path_that_is_no_group_output_is_refused()
    {
        var answer = await (await Ledger()).SendAsync(Corpus.Transaction, """
            [ { "group": { "by": [{ "path": "convertState", "as": "state" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "number": "asc" }] },
              { "page": { "limit": 3 } } ]
            """);

        answer.ShouldRefuse("UNKNOWN_PATH", 400)["path"]!.GetValue<string>().Should().Be("number");
        answer.ErrorCodes.Should().Equal("UNKNOWN_PATH");
    }

    [Fact]
    public async Task I20_a_sort_on_a_push_group_output_is_refused()
    {
        var answer = await (await Ledger()).SendAsync(Corpus.Transaction, """
            [ { "group": { "by": [{ "path": "convertState", "as": "state" }], "fields": { "numbers": { "push": "number" } } } },
              { "sort": [{ "numbers": "asc" }] },
              { "page": { "limit": 3 } } ]
            """);

        answer.ShouldRefuse("NOT_SORTABLE", 400);
        answer.ErrorCodes.Should().Equal("NOT_SORTABLE");
    }

    // ── I14 · I15 · I18 — the default order, unwind, several sort stages ──────────────────

    [Fact]
    public async Task I15_a_root_shape_with_no_sort_comes_back_ordered_by_id_ascending()
    {
        var expected = Corpus.SortedIds(Corpus.Employee, "id");
        expected.Should().Equal(Corpus.Rows(Corpus.Employee).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => row.Id));

        var answer = await (await Staff()).SendAsync(Corpus.Employee, """[{ "page": { "limit": 500 } }]""");

        answer.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task I14_an_unwind_with_no_caller_sort_keeps_the_parent_order_and_one_element_run_per_parent()
    {
        // The engine's order is the key, then the unwind index: every parent's elements arrive
        // together and the parents arrive in id order. An unwind without preserve drops the empty
        // and the missing collections.
        var parents = Corpus.Rows(Corpus.Shipment).Where(row => Corpus.Elements(row, "items").Count > 0).ToList();
        parents.Should().NotBeEmpty();
        var expectedParents = Corpus.IdsOf(parents.Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)));
        var expectedRows = parents.Sum(row => Corpus.Elements(row, "items").Count);

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """[{ "unwind": { "path": "items" } }, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""");

        answer.ShouldBeOk();
        var ids = answer.Ids();
        ids.Should().HaveCount(expectedRows);
        ids.Where((id, index) => index == 0 || ids[index - 1] != id).Should().Equal(expectedParents, "runs in parent-id order, never interleaved");
    }

    [Fact]
    public async Task I18_several_sort_stages_are_allowed_and_the_last_one_decides_the_order_and_the_cursor()
    {
        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "shipmentNumber", descending: true), limit: 3);

        var answer = await (await Transport()).SendAsync(Corpus.Template, """[{ "sort": [{ "templateName": "asc" }] }, { "sort": [{ "shipmentNumber": "desc" }] }, { "page": { "limit": 3 } }]""");

        answer.ShouldHaveIds(expected);
        Legs(answer.NextCursor!).Should().Equal("shipmentNumber", "_id");
    }

    /// <summary>The sort paths a keyset cursor carries, in order.</summary>
    internal static IReadOnlyList<string> Legs(string cursor) =>
        ((JsonArray)Cursors.Payload(cursor)["v"]!).Select(leg => leg!["p"]!.GetValue<string>()).ToList();
}
