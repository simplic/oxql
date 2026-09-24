using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Shape;

/// <summary>
/// Area K: <c>unwind</c>. One row per element, the element under the collection's own name, an
/// optional alias copy and index, empty and absent collections dropped unless preserved, inner
/// collections only after their outer one, and offset paging behind the cursor. Also re-runs the
/// v1-era unwind measurements (VAL U/X probes) against v2. Ported from the legacy
/// <c>shape-k-unwind</c> battery, engine half only: the client's compiled unwind stage and its
/// alias checks before send went to the client's specs.
/// </summary>
[Trait("Category", "Integration")]
public class ShapeKUnwindTests
{
    private const string FullPage = """{ "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } }""";

    private static Guid Id(string key) => Corpus.IdOf(Corpus.Shipment, key);

    /// <summary>The stored items of a shipment, by storage name; empty for none, null or missing.</summary>
    private static IReadOnlyList<BsonDocument> Items(CorpusRow row) => Corpus.Elements(row, "items").OfType<BsonDocument>().ToList();

    private static string? Status(BsonDocument item) => item["Status"] is BsonDocument status && status["Name"] is BsonString name ? name.Value : null;

    private static double Quantity(BsonDocument item) => item["Quantity"] is BsonDocument quantity ? quantity["Value"].ToDouble() : 0;

    /// <summary>Each row's id once per element the counter says, in id order: what an unwound page returns.</summary>
    private static IReadOnlyList<Guid> Unwound(Func<CorpusRow, int> count, IEnumerable<CorpusRow>? rows = null) =>
        (rows ?? Corpus.Rows(Corpus.Shipment)).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).SelectMany(row => Enumerable.Repeat(row.Id, count(row))).ToList();

    private static async Task<WireAnswer> Send(string pipeline, string entity = Corpus.Shipment) =>
        await (await Lab.ClientForAsync(entity)).SendAsync(entity, pipeline);

    private static void Refused(WireAnswer answer, string code, string? path = null, int? stage = null)
    {
        var error = answer.ShouldRefuse(code, 400);
        answer.ErrorCodes.Should().Equal([code], answer.ToString());

        if (path is not null)
            error["path"]!.GetValue<string>().Should().Be(path, answer.ToString());

        if (stage is not null)
            error["stage"]!.GetValue<int>().Should().Be(stage.Value, answer.ToString());
    }

    [Fact]
    public async Task K00_the_rows_the_area_is_built_on_hold_what_their_keys_say()
    {
        Items(Corpus.Row(Corpus.Shipment, "items-none")).Should().BeEmpty();
        Items(Corpus.Row(Corpus.Shipment, "items-one")).Should().HaveCount(1);
        Items(Corpus.Row(Corpus.Shipment, "items-three")).Should().HaveCount(3);
        Items(Corpus.Row(Corpus.Shipment, "items-split")).Should().HaveCount(2);
        Corpus.Missing(Corpus.Row(Corpus.Shipment, "items-missing"), "items").Should().BeTrue();
    }

    [Fact]
    public async Task K01_an_unwind_replaces_the_document_with_one_per_element_under_the_same_member_name()
    {
        var expected = Unwound(row => Items(row).Count);
        expected.Count.Should().BeGreaterThan(Corpus.Counts(Corpus.Shipment).A);

        var answer = await Send($$"""[ { "unwind": { "path": "items" } }, { "project": { "id": 1, "items.orderNumber": 1 } }, {{FullPage}} ]""");

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);

        // …and the member is the element, not the array.
        var three = answer.Items.Where(row => Guid.Parse(row!["id"]!.GetValue<string>()) == Id("items-three")).ToList();
        three.Should().HaveCount(3);
        three.Should().OnlyContain(row => row!["items"] is JsonObject);
        three.Select(row => row!["items"]!["orderNumber"]!.GetValue<int>()).Should().Equal(Items(Corpus.Row(Corpus.Shipment, "items-three")).Select(item => item["OrderNumber"].AsInt32));
    }

    [Fact]
    public async Task K02_after_an_unwind_the_written_path_means_the_element_not_some_element()
    {
        var after = Unwound(row => Items(row).Count(item => Status(item) == "Open" && Quantity(item) > 0));
        var before = Corpus.IdsWhere(Corpus.Shipment, row => Items(row).Any(item => Status(item) == "Open") && Items(row).Any(item => Quantity(item) > 0));
        before.Should().Contain(Id("items-split"));
        after.Should().NotContain(Id("items-split"));

        const string condition = """{ "and": [ { "items.status.name": { "eq": "Open" } }, { "items.quantity.value": { "gt": 0 } } ] }""";

        (await Send($$"""[ { "unwind": { "path": "items" } }, { "match": {{condition}} }, { "project": { "id": 1 } }, {{FullPage}} ]""")).ShouldHaveIds(after);
        (await Send($$"""[ { "match": {{condition}} }, { "project": { "id": 1 } }, {{FullPage}} ]""")).ShouldHaveIds(before);
    }

    [Fact]
    public async Task K03_as_writes_a_copy_and_the_original_member_still_carries_the_element()
    {
        var answer = await Send($$"""[ { "match": { "id": { "eq": "{{Id("items-three")}}" } } }, { "unwind": { "path": "items", "as": "item" } }, { "project": { "id": 1, "items.orderNumber": 1, "item.orderNumber": 1 } }, {{FullPage}} ]""");

        answer.ShouldHaveIds(Enumerable.Repeat(Id("items-three"), 3));

        foreach (var row in answer.Items)
        {
            row!["items"].Should().BeOfType<JsonObject>("the original member keeps the element (v1 dropped it, VAL U2)");
            row["item"]!["orderNumber"]!.GetValue<int>().Should().Be(row["items"]!["orderNumber"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task K04_include_index_writes_the_zero_based_position_as_an_int_in_stored_order()
    {
        var stored = Items(Corpus.Row(Corpus.Shipment, "items-three")).Select(item => item["OrderNumber"].AsInt32).ToList();

        var answer = await Send($$"""[ { "match": { "id": { "eq": "{{Id("items-three")}}" } } }, { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "sort": [ { "position": "asc" } ] }, { "page": { "limit": 50 } } ]""");

        answer.ShouldBeOk();
        answer.Values("position").Select(value => value!.GetValue<int>()).Should().Equal(0, 1, 2);
        answer.Values("position").Should().OnlyContain(value => value!.GetValueKind() == System.Text.Json.JsonValueKind.Number);
        answer.Values("item.orderNumber").Select(value => value!.GetValue<int>()).Should().Equal(stored);
    }

    [Fact]
    public async Task K05_the_index_member_is_filterable_and_sortable()
    {
        var answer = await Send($$"""[ { "match": { "id": { "eq": "{{Id("items-three")}}" } } }, { "unwind": { "path": "items", "includeIndex": "position" } }, { "match": { "position": { "gte": 1 } } }, { "sort": [ { "position": "desc" } ] }, { "page": { "limit": 50, "includeTotalCount": true } } ]""");

        answer.ShouldHaveTotal(2);
        answer.Values("position").Select(value => value!.GetValue<int>()).Should().Equal(2, 1);
    }

    [Fact]
    public async Task K06_preserve_null_keeps_the_empty_and_the_absent_document_with_the_element_absent_and_the_index_null()
    {
        var kept = new[] { "items-none", "items-missing", "items-one" }.Select(key => Corpus.Row(Corpus.Shipment, key)).ToList();
        var expected = Unwound(row => Math.Max(Items(row).Count, 1), kept);

        var answer = await Send($$"""
            [ { "match": { "id": { "in": {{Json.Ids(kept.Select(row => row.Id))}} } } },
              { "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } },
              { "project": { "id": 1, "item.orderNumber": 1, "position": 1 } },
              {{FullPage}} ]
            """);

        answer.ShouldHaveIds(expected);

        foreach (var row in answer.Items.Select(row => row!.AsObject()))
        {
            var empty = Guid.Parse(row["id"]!.GetValue<string>()) != Id("items-one");
            row["position"]?.GetValue<int>().Should().Be(empty ? null : 0, row.ToJsonString());
            row.ContainsKey("position").Should().BeTrue("the index of a preserved row is null, not absent: " + row.ToJsonString());
            row.ContainsKey("item").Should().Be(!empty, "a preserved row carries no element at all: " + row.ToJsonString());
        }
    }

    [Fact]
    public async Task K07_without_preserve_null_the_empty_and_the_absent_document_are_dropped()
    {
        var ids = new[] { "items-none", "items-missing", "items-one" }.Select(Id);

        var answer = await Send($$"""[ { "match": { "id": { "in": {{Json.Ids(ids)}} } } }, { "unwind": { "path": "items" } }, { "project": { "id": 1 } }, {{FullPage}} ]""");

        answer.ShouldHaveIds([Id("items-one")]).ShouldHaveTotal(1);
    }

    [Fact]
    public async Task K08_an_unwind_on_a_non_collection_is_refused()
    {
        Refused(await Send($$"""[ { "unwind": { "path": "status" } }, {{FullPage}} ]"""), "NOT_A_COLLECTION", "status", 0);
    }

    [Fact]
    public async Task K09_an_inner_collection_before_its_outer_one_is_refused_where_v1_answered_an_empty_page()
    {
        Refused(await Send($$"""[ { "unwind": { "path": "billingLines.references" } }, {{FullPage}} ]"""), "UNWIND_ORDER", "billingLines.references", 0);
    }

    [Fact]
    public async Task K10_outer_then_inner_is_accepted_and_the_inner_paths_rebase_onto_the_element()
    {
        var nested = Corpus.Row(Corpus.Shipment, "billing-nested");
        var expected = Corpus.Elements(nested, "billingLines").OfType<BsonDocument>()
            .SelectMany((line, lineIndex) => line["References"].AsBsonArray.OfType<BsonDocument>().Select((reference, refIndex) => (lineIndex, refIndex, reference["ReferenceId"].AsString)))
            .ToList();
        expected.Select(entry => entry.Item3).Should().Equal("O-1", "I-1", "O-2");

        var answer = await Send($$"""
            [ { "match": { "id": { "eq": "{{nested.Id}}" } } },
              { "unwind": { "path": "billingLines", "as": "bl", "includeIndex": "bi" } },
              { "unwind": { "path": "billingLines.references", "as": "ref", "includeIndex": "ri" } },
              { "project": { "id": 1, "bi": 1, "ri": 1, "ref.referenceId": 1 } },
              { "sort": [ { "id": "asc" }, { "bi": "asc" }, { "ri": "asc" } ] },
              { "page": { "limit": 50, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Items.Select(row => (row!["bi"]!.GetValue<int>(), row["ri"]!.GetValue<int>(), row["ref"]!["referenceId"]!.GetValue<string>())).Should().Equal(expected);

        // The alias root spells the same collection.
        var viaAlias = await Send($$"""
            [ { "match": { "id": { "eq": "{{nested.Id}}" } } },
              { "unwind": { "path": "billingLines", "as": "bl" } },
              { "unwind": { "path": "bl.references", "as": "ref" } },
              { "project": { "id": 1, "ref.referenceId": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 50 } } ]
            """);

        viaAlias.ShouldBeOk();
        viaAlias.Strings("ref.referenceId").Should().BeEquivalentTo(expected.Select(entry => entry.Item3));
    }

    [Fact]
    public async Task K11_an_unwind_of_an_already_unwound_path_is_refused()
    {
        Refused(await Send($$"""[ { "unwind": { "path": "items" } }, { "unwind": { "path": "items" } }, {{FullPage}} ]"""), "NOT_A_COLLECTION", "items", 1);
    }

    [Fact]
    public async Task K12_an_unwind_after_a_group_is_refused_the_grouped_shape_has_no_collections()
    {
        Refused(await Send("""[ { "group": { "by": [ { "path": "status.name", "as": "st" } ], "fields": { "n": { "count": true } } } }, { "unwind": { "path": "st" } }, { "page": { "limit": 50 } } ]"""),
            "NOT_A_COLLECTION", "st", 1);
    }

    [Fact]
    public async Task K13_an_as_alias_colliding_with_an_existing_member_is_refused()
    {
        Refused(await Send($$"""[ { "unwind": { "path": "items", "as": "tags" } }, {{FullPage}} ]"""), "ALIAS_COLLISION", stage: 0);
    }

    [Fact]
    public async Task K14_as_equal_to_include_index_is_refused()
    {
        Refused(await Send($$"""[ { "unwind": { "path": "items", "as": "element", "includeIndex": "element" } }, {{FullPage}} ]"""), "ALIAS_COLLISION", stage: 0);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("2x")]
    [InlineData("")]
    public async Task K15_an_alias_that_is_not_an_identifier_is_refused(string alias)
    {
        Refused(await Send($$"""[ { "unwind": { "path": "items", "as": "{{alias}}" } }, {{FullPage}} ]"""), "INVALID_ALIAS", stage: 0);
    }

    [Theory]
    [InlineData("items.orderNumber", null)]
    [InlineData("item.orderNumber", "item")]
    public async Task K16_the_elements_paths_become_sortable_under_the_original_name_and_under_the_alias(string path, string? alias)
    {
        var expected = Items(Corpus.Row(Corpus.Shipment, "items-three")).Select(item => item["OrderNumber"].AsInt32).OrderDescending().ToList();
        var unwind = alias is null ? """{ "path": "items" }""" : $$"""{ "path": "items", "as": "{{alias}}" }""";

        var answer = await Send($$"""[ { "match": { "id": { "eq": "{{Id("items-three")}}" } } }, { "unwind": {{unwind}} }, { "sort": [ { "{{path}}": "desc" } ] }, { "page": { "limit": 50 } } ]""");

        answer.ShouldBeOk();
        answer.Values(path).Select(value => value!.GetValue<int>()).Should().Equal(expected);
    }

    [Fact]
    public async Task K17_a_sibling_collection_that_was_not_unwound_stays_unsortable()
    {
        Refused(await Send("""[ { "unwind": { "path": "items" } }, { "sort": [ { "tags.name": "asc" } ] }, { "page": { "limit": 5 } } ]"""), "NOT_SORTABLE", "tags.name", 1);
    }

    [Fact]
    public async Task K18_an_unwind_switches_the_query_to_offset_paging_behind_the_cursor()
    {
        var expected = Unwound(row => Items(row).Count);
        const string stages = """{ "unwind": { "path": "items" } }, { "project": { "id": 1 } }, { "sort": [ { "id": "asc" } ] }""";
        var client = await Lab.ClientAsync(LabService.Transport);

        var first = await client.SendAsync(Corpus.Shipment, $$"""[ {{stages}}, { "page": { "limit": 3, "includeTotalCount": true } } ]""");

        first.ShouldHaveIds(Corpus.PageOf(expected, limit: 3));
        first.HasNextPage.Should().BeTrue();
        var payload = Cursors.Payload(first.NextCursor!);
        payload["m"]!.GetValue<string>().Should().Be("o", "the offset mode: " + payload.ToJsonString());
        payload["o"]!.GetValue<int>().Should().Be(3);

        var second = await client.SendAsync(Corpus.Shipment, $$"""[ {{stages}}, { "page": { "limit": 3, "cursor": "{{first.NextCursor}}" } } ]""");
        second.ShouldHaveIds(Corpus.PageOf(expected, limit: 3, offset: 3));
    }

    [Fact]
    public async Task K19_the_total_count_after_an_unwind_counts_the_unwound_rows_not_the_source_documents()
    {
        var expected = Corpus.Rows(Corpus.Shipment).Sum(row => Items(row).Count);
        expected.Should().NotBe(Corpus.Counts(Corpus.Shipment).A);

        (await Send("""[ { "unwind": { "path": "items" } }, { "project": { "id": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 3, "includeTotalCount": true } } ]""")).ShouldHaveTotal(expected, capped: false);
    }

    [Fact]
    public async Task K20_a_collection_of_objects_and_a_collection_of_scalars_are_unwindable_and_a_dictionary_is_not_a_collection()
    {
        var many = Corpus.Row(Corpus.Shipment, "tags-many");
        var tags = Corpus.Texts(many, "tags.name");
        tags.Should().HaveCount(3);

        var objects = await Send($$"""[ { "match": { "id": { "eq": "{{many.Id}}" } } }, { "unwind": { "path": "tags", "as": "tag", "includeIndex": "at" } }, { "project": { "id": 1, "at": 1, "tag.name": 1 } }, { "sort": [ { "id": "asc" }, { "at": "asc" } ] }, { "page": { "limit": 50 } } ]""");
        objects.ShouldBeOk();
        objects.Strings("tag.name").Should().Equal(tags);

        // A collection of scalars: the element is the value itself.
        var states = Corpus.Row(Corpus.Transaction, "states-many");
        var values = Corpus.Texts(states, "states");
        values.Should().HaveCount(3);

        var scalars = await Send($$"""[ { "match": { "id": { "eq": "{{states.Id}}" } } }, { "unwind": { "path": "states", "includeIndex": "at" } }, { "project": { "id": 1, "states": 1, "at": 1 } }, { "sort": [ { "at": "asc" } ] }, { "page": { "limit": 50 } } ]""", Corpus.Transaction);
        scalars.ShouldBeOk();
        scalars.Strings("states").Should().Equal(values);

        // Legacy: the dictionary half was unreachable (the fleet's one ArrayOfDocuments dictionary
        // was not published). conformance.entity carries both dictionary forms. A dictionary stored
        // as a document is not a collection; one stored as an array of key/value documents is, and
        // unwinds to one row per entry with the entry under the member's name as { k, v }.
        Refused(await Send($$"""[ { "unwind": { "path": "labels" } }, {{FullPage}} ]""", Corpus.Conformance), "NOT_A_COLLECTION", "labels", 0);

        var rows = Corpus.Rows(Corpus.Conformance).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).ToList();
        var entries = rows.SelectMany(row => Corpus.Elements(row, "quantities").OfType<BsonDocument>()
            .Select(entry => (row.Id, Key: entry["k"].AsGuid, Value: entry["v"].AsInt32))).ToList();
        entries.Should().NotBeEmpty();

        var unwound = await Send("""[ { "unwind": { "path": "quantities" } }, { "project": { "id": 1, "quantities": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } } ]""", Corpus.Conformance);

        unwound.ShouldHaveIds(entries.Select(entry => entry.Id));
        unwound.Items.Select(row => (Guid.Parse(row!["quantities"]!["k"]!.GetValue<string>()), row["quantities"]!["v"]!.GetValue<int>()))
            .Should().Equal(entries.Select(entry => (entry.Key, entry.Value)));
    }

    [Fact]
    public async Task K21_more_than_max_unwind_stages_is_refused()
    {
        const string five = """{ "unwind": { "path": "items" } }, { "unwind": { "path": "items.weightNotes" } }, { "unwind": { "path": "tags" } }, { "unwind": { "path": "billingLines" } }, { "unwind": { "path": "billingLines.references" } }""";

        (await Send($$"""[ {{five}}, {{FullPage}} ]""")).ShouldBeOk("five unwind stages are within the limit");

        var six = await Send($$"""[ {{five}}, { "unwind": { "path": "documents" } }, {{FullPage}} ]""");
        six.StatusCode.Should().Be(400, six.ToString());
        six.ErrorCodes.Should().Contain("MAX_UNWIND_STAGES_EXCEEDED");
    }

    [Theory]
    [InlineData("item.quantity.value")]
    [InlineData("items.quantity.value")]
    public async Task K22_a_filter_on_the_alias_root_and_on_the_original_path_select_the_same_rows(string path)
    {
        var expected = Unwound(row => Items(row).Count(item => Quantity(item) == 5));
        expected.Should().Equal([Id("items-split")]);

        (await Send($$"""[ { "unwind": { "path": "items", "as": "item" } }, { "match": { "{{path}}": { "eq": 5 } } }, { "project": { "id": 1 } }, {{FullPage}} ]""")).ShouldHaveIds(expected);
    }

    [Fact]
    public async Task K23_a_PascalCased_alias_root_is_an_unknown_path_under_contract_2_never_an_empty_page()
    {
        Refused(await Send($$"""[ { "unwind": { "path": "items", "as": "item" } }, { "match": { "Item.quantity.value": { "eq": 5 } } }, { "project": { "id": 1 } }, {{FullPage}} ]"""),
            "UNKNOWN_PATH", "Item.quantity.value", 1);
    }

    [Fact]
    public async Task K24_the_unwind_alias_root_is_an_object_and_supports_exists()
    {
        var expected = Unwound(row => Items(row).Count);

        (await Send($$"""[ { "unwind": { "path": "items", "as": "item" } }, { "match": { "item": { "exists": true } } }, { "project": { "id": 1 } }, {{FullPage}} ]""")).ShouldHaveIds(expected);

        // exists false after a plain unwind matches nothing: every surviving row has the element.
        (await Send($$"""[ { "unwind": { "path": "items", "as": "item" } }, { "match": { "item": { "exists": false } } }, { "project": { "id": 1 } }, {{FullPage}} ]""")).ShouldHaveIds([]);
    }

    // ── the v1-era unwind measurements, re-run against v2 ───────────────────────────────────

    [Fact]
    public async Task VALX5_unwind_then_group_by_a_root_key_counts_the_elements_per_key_and_the_total_counts_groups()
    {
        var wanted = new[] { "items-one", "items-three", "items-split" }.Select(key => Corpus.Row(Corpus.Shipment, key)).ToList();
        var expected = wanted.Select(row => (Corpus.Text(row, "shipmentNumber")!, (long)Items(row).Count)).OrderBy(entry => entry.Item1, Comparer<string>.Create(Order.CompareCollated)).ToList();

        var answer = await Send($$"""
            [ { "match": { "id": { "in": {{Json.Ids(wanted.Select(row => row.Id))}} } } },
              { "unwind": { "path": "items" } },
              { "group": { "by": [ { "path": "shipmentNumber", "as": "number" } ], "fields": { "n": { "count": true } } } },
              { "sort": [ { "number": "asc" } ] },
              { "page": { "limit": 20, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Items.Select(row => (row!["number"]!.GetValue<string>(), long.Parse(row["n"]!.GetValue<string>()))).Should().Equal(expected);
    }

    [Fact]
    public async Task VALX6_match_the_key_then_unwind_returns_that_rows_elements_in_order()
    {
        var three = Corpus.Row(Corpus.Shipment, "items-three");
        var stored = Items(three).Select(item => item["OrderNumber"].AsInt32).ToList();

        var answer = await Send($$"""[ { "match": { "shipmentNumber": { "eq": "{{Corpus.Text(three, "shipmentNumber")}}" } } }, { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "sort": [ { "position": "asc" } ] }, { "page": { "limit": 20, "includeTotalCount": true } } ]""");

        answer.ShouldHaveTotal(stored.Count);
        answer.Values("position").Select(value => value!.GetValue<int>()).Should().Equal(0, 1, 2);
        answer.Values("item.orderNumber").Select(value => value!.GetValue<int>()).Should().Equal(stored);
    }

    [Fact]
    public async Task VALM1_match_unwind_match_group_match_sort_executes_in_the_written_order()
    {
        var expected = Corpus.Rows(Corpus.Shipment).Where(row => Corpus.Text(row, "status.name") == "Closed")
            .SelectMany(Items).Where(item => Status(item) == "Open")
            .GroupBy(item => item["Article"]["Name"].AsString).Select(group => (group.Key, (long)group.Count()))
            .OrderBy(entry => entry.Key, Comparer<string>.Create(Order.CompareCollated)).ToList();
        expected.Should().HaveCountGreaterThan(1);

        var answer = await Send("""
            [ { "match": { "status.name": { "eq": "Closed" } } },
              { "unwind": { "path": "items", "as": "item" } },
              { "match": { "item.status.name": { "eq": "Open" } } },
              { "group": { "by": [ { "path": "item.article.name", "as": "article" } ], "fields": { "n": { "count": true } } } },
              { "match": { "n": { "gte": 1 } } },
              { "sort": [ { "article": "asc" } ] },
              { "page": { "limit": 20, "includeTotalCount": true } } ]
            """);

        // v1 answered a post-group match with nothing unless a match {} led the pipeline (VAL G4/G5).
        answer.ShouldHaveTotal(expected.Count);
        answer.Items.Select(row => (row!["article"]!.GetValue<string>(), long.Parse(row["n"]!.GetValue<string>()))).Should().Equal(expected);
    }

    [Fact]
    public async Task VALL3_a_lookup_then_an_unwind_of_its_alias_then_a_match_on_the_element()
    {
        // Legacy: unreachable (no declared backward reference). conformance.child#parentId
        // references conformance.entity, so the v1 probe L3 now runs.
        var children = Corpus.Rows(Corpus.ConformanceChild).Where(row => Corpus.GuidAt(row, "parentId") is not null).ToList();
        var parents = Corpus.Rows(Corpus.Conformance).ToDictionary(row => row.Id);
        var expected = children.Where(child => parents.ContainsKey(Corpus.GuidAt(child, "parentId")!.Value))
            .Select(child => (Parent: Corpus.GuidAt(child, "parentId")!.Value, Child: child.Id, Name: Corpus.Text(child, "name")!))
            .OrderBy(entry => entry.Parent.ToString("D"), StringComparer.Ordinal).ThenBy(entry => entry.Child.ToString("D"), StringComparer.Ordinal).ToList();
        expected.Should().NotBeEmpty();
        var first = expected[0].Name;
        var matching = expected.Where(entry => Order.EqualsCi(entry.Name, first)).ToList();

        var answer = await Send($$"""
            [ { "lookup": { "from": "conformance.child", "path": "parentId", "as": "peers" } },
              { "unwind": { "path": "peers", "as": "peer" } },
              { "match": { "peer.name": { "eq": "{{first}}" } } },
              { "project": { "id": 1, "peer.id": 1 } },
              {{FullPage}} ]
            """, Corpus.Conformance);

        answer.ShouldHaveIds(matching.Select(entry => entry.Parent));
        answer.Ids("peer.id").Should().Equal(matching.Select(entry => entry.Child));
    }

    [Fact]
    public async Task VALL3b_a_lookup_alias_unwound_without_as_is_addressed_under_its_own_name()
    {
        var parents = Corpus.Rows(Corpus.Conformance).ToDictionary(row => row.Id);
        var expected = Corpus.Rows(Corpus.ConformanceChild)
            .Where(child => Corpus.GuidAt(child, "parentId") is { } parent && parents.ContainsKey(parent))
            .Select(child => (Parent: Corpus.GuidAt(child, "parentId")!.Value, Child: child.Id, Name: Corpus.Text(child, "name")!))
            .OrderBy(entry => entry.Parent.ToString("D"), StringComparer.Ordinal).ThenBy(entry => entry.Child.ToString("D"), StringComparer.Ordinal).ToList();
        expected.Should().NotBeEmpty();
        var first = expected[0].Name;
        var matching = expected.Where(entry => Order.EqualsCi(entry.Name, first)).ToList();

        var answer = await Send($$"""
            [ { "lookup": { "from": "conformance.child", "path": "parentId", "as": "peers" } },
              { "unwind": { "path": "peers" } },
              { "match": { "peers.name": { "eq": "{{first}}" } } },
              { "project": { "id": 1, "peers.id": 1 } },
              {{FullPage}} ]
            """, Corpus.Conformance);

        answer.ShouldHaveIds(matching.Select(entry => entry.Parent));
        answer.Ids("peers.id").Should().Equal(matching.Select(entry => entry.Child));
    }
}
