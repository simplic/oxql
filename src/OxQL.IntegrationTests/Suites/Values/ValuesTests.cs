using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Values.ValuesKit;

namespace OxQL.IntegrationTests.Suites.Values;

/// <summary>
/// The <c>values</c> stage on the fleet's rows: the distinct values of one path
/// among the rows that reach it, answered in the row envelope in place of a page. What a list holds
/// is counted on the corpus, and it equals the group a caller could write by hand: the same values,
/// the same counts, the same order. On top of that group the stage unwinds the collections above its
/// path for itself, bounds the rows it reads and says when the bound was reached.
/// </summary>
[Trait("Category", "Integration")]
public class ValuesTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static IReadOnlyList<CorpusRow> Shipments => Corpus.Rows(Corpus.Shipment);

    private static IReadOnlyList<CorpusRow> Employees => Corpus.Rows(Corpus.Employee);

    // ---- the list is the group one would write by hand ----------------------------------------

    public static TheoryData<string, string> Paths => new()
    {
        { Corpus.Shipment, "status.name" },
        { Corpus.Shipment, "loadingTimeType" },
        { Corpus.Shipment, "loadStart" },
        { Corpus.Shipment, "isDeleted" },
        { Corpus.Shipment, "department.id" },
        { Corpus.Shipment, "shipmentNumber" },
        { Corpus.Employee, "matchCode" },
        { Corpus.Employee, "address.city" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task The_values_of_a_member_are_the_groups_of_the_hand_written_form_with_and_without_counts(string entity, string path)
    {
        var counted = await Ask(entity, $$""" "path": "{{path}}", "counts": true, "limit": 500 """);
        var plain = await Ask(entity, $$""" "path": "{{path}}", "limit": 500 """);

        Texts(counted).Should().Be(Texts(await ByHand(entity, path)), "most rows first, then by value");
        Texts(plain).Should().Be(Texts(await ByHand(entity, path, counts: false)), "by value, null first");

        counted.Items.Should().NotBeEmpty();
        Listed(counted).Sum(entry => entry.Count!.Value).Should().Be(Corpus.Rows(entity).Count, "every row holds one value, null included");
        Listed(plain).Should().OnlyContain(entry => entry.Count == null);
        Listed(plain).Select(entry => entry.Value).Should().BeEquivalentTo(Listed(counted).Select(entry => entry.Value));

        foreach (var answer in new[] { counted, plain })
        {
            answer.HasNextPage.Should().BeFalse();
            answer.NextCursor.Should().BeNull();
            answer.ShouldCount("exact", answer.Items.Count, because: "the envelope counts what it lists");
            answer.ShouldHaveNoDiagnostics();
            answer.PageInfo.Select(member => member.Key).Should().Equal("hasNextPage", "count");
        }
    }

    [Fact]
    public async Task The_statuses_of_the_shipments_are_what_the_corpus_holds()
    {
        var expected = Shipments.GroupBy(row => Corpus.Text(row, "status.name")).Select(group => (Value: group.Key, Count: group.Count()))
            .OrderByDescending(group => group.Count).ThenBy(group => group.Value, Comparer<string?>.Create((a, b) => a is null ? b is null ? 0 : -1 : b is null ? 1 : Order.CompareCollated(a, b))).ToList();
        var answer = await Ask(Corpus.Shipment, """ "path": "status.name", "counts": true """);

        Listed(answer).Should().Equal(expected.Select(group => (group.Value is null ? "null" : $"\"{group.Value}\"", (int?)group.Count)));
        expected.Should().HaveCountGreaterThan(2);
    }

    [Fact]
    public async Task An_enum_travels_as_its_number_and_a_stored_number_the_enum_does_not_declare_is_a_value()
    {
        var expected = Shipments.GroupBy(row => Corpus.ValueAt(row, "loadingTimeType") is { IsBsonNull: false } stored ? stored.ToInt32() : (int?)null).ToDictionary(group => group.Key?.ToString() ?? "null", group => group.Count());
        var listed = Listed(await Ask(Corpus.Shipment, """ "path": "loadingTimeType", "counts": true """));

        listed.ToDictionary(entry => entry.Value, entry => entry.Count!.Value).Should().BeEquivalentTo(expected);
        listed.Select(entry => entry.Value).Should().Contain("0").And.Contain("99", "the corpus holds a number no member names");
    }

    // ---- strings fold, null is a value --------------------------------------------------------

    [Fact]
    public async Task Spellings_the_collation_calls_equal_are_one_value_and_null_and_absent_are_one_null()
    {
        var expected = Employees.GroupBy(row => Corpus.Text(row, "matchCode") is { } text ? Order.FoldCi(text) : null).ToDictionary(group => group.Key ?? "\0", group => group.Count());
        var listed = Listed(await Ask(Corpus.Employee, """ "path": "matchCode", "counts": true, "limit": 500 """));

        listed.Should().HaveCount(expected.Count);

        foreach (var (value, count) in listed)
        {
            var key = value == "null" ? "\0" : Order.FoldCi(JsonNode.Parse(value)!.GetValue<string>());

            expected.Should().ContainKey(key, $"{value} is a spelling of a value the corpus holds");
            count.Should().Be(expected[key], value);
        }

        // müller, MÜLLER, Muller and Müller are one value, reported under one of its spellings.
        var muller = listed.Single(entry => entry.Value != "null" && Order.FoldCi(JsonNode.Parse(entry.Value)!.GetValue<string>()) == "muller");

        muller.Count.Should().Be(4);
        new[] { "\"müller\"", "\"MÜLLER\"", "\"Muller\"", "\"Müller\"" }.Should().Contain(muller.Value);

        // A null and a member that is not there are the one blank entry; the empty string is a value of its own.
        Corpus.RowsNull(Corpus.Employee, "matchCode").Should().NotBeEmpty();
        Corpus.RowsMissing(Corpus.Employee, "matchCode").Should().NotBeEmpty();
        listed.Single(entry => entry.Value == "null").Count.Should().Be(Employees.Count(row => Corpus.Text(row, "matchCode") is null));
        listed.Should().Contain(entry => entry.Value == "\"\"");
    }

    [Theory]
    [InlineData("mul")]
    [InlineData("MÜL")]
    [InlineData("Müller")]
    [InlineData("d")]
    [InlineData("arr-")]
    [InlineData("zz-none")]
    public async Task Search_keeps_the_values_that_start_with_the_text_folding_case_and_accents(string text)
    {
        var folded = Order.FoldCi(text);
        var expected = Employees.Select(row => Corpus.Text(row, "matchCode")).Where(value => value is not null && Order.FoldCi(value).StartsWith(folded, StringComparison.Ordinal))
            .GroupBy(value => Order.FoldCi(value!)).ToDictionary(group => group.Key, group => group.Count());
        var listed = Listed(await Ask(Corpus.Employee, $$""" "path": "matchCode", "search": "{{text}}", "counts": true """));

        listed.ToDictionary(entry => Order.FoldCi(JsonNode.Parse(entry.Value)!.GetValue<string>()), entry => entry.Count!.Value).Should().BeEquivalentTo(expected, text);

        // It is the prefix a condition of the request would test: the same rows, grouped.
        var matched = await ByHand(Corpus.Employee, "matchCode", before: $$"""{ "match": { "matchCode": { "startsWith": "{{text}}" } } }""");

        listed.Select(entry => (Order.FoldCi(JsonNode.Parse(entry.Value)!.GetValue<string>()), entry.Count))
            .Should().BeEquivalentTo(Listed(matched).Select(entry => (Order.FoldCi(JsonNode.Parse(entry.Value)!.GetValue<string>()), entry.Count)));
    }

    // ---- the two bounds -----------------------------------------------------------------------

    [Fact]
    public async Task One_value_more_than_the_limit_says_more_exist_and_nothing_continues_the_list()
    {
        var all = Listed(await Ask(Corpus.Employee, """ "path": "matchCode", "limit": 500 """));
        var first = await Ask(Corpus.Employee, """ "path": "matchCode", "limit": 3 """);

        Listed(first).Should().Equal(all.Take(3));
        first.HasNextPage.Should().BeTrue();
        first.NextCursor.Should().BeNull("a list of values has no cursor: the caller narrows with search");
        first.ShouldCount("atLeast", 4, "page");

        var whole = await Ask(Corpus.Employee, $$""" "path": "matchCode", "limit": {{all.Count}} """);

        whole.HasNextPage.Should().BeFalse();
        whole.ShouldCount("exact", all.Count);
    }

    [Fact]
    public async Task A_scan_that_reaches_its_bound_is_said_and_the_list_is_of_the_rows_read()
    {
        var rows = Shipments.Count;
        var capped = await Ask(Corpus.Shipment, """ "path": "status.name", "counts": true, "scan": 5 """);
        var diagnostic = capped.ShouldHaveDiagnostic("VALUES_SCAN_CAPPED");

        diagnostic.ToJsonString(Plain).Should().Be("""{"code":"VALUES_SCAN_CAPPED","message":"The values are those among the first 5 rows that reach the stage; more rows may match, and a count is the count within them. Narrow the request's filter to see the values of fewer rows.","stage":0,"path":"status.name","params":{"scan":5}}""");
        Listed(capped).Sum(entry => entry.Count!.Value).Should().Be(5, "a count is a count within the rows read");

        // A bound the rows do not reach says nothing; one they reach exactly is said, since no row was read beyond it.
        (await Ask(Corpus.Shipment, $$""" "path": "status.name", "scan": {{rows + 1}} """)).ShouldHaveNoDiagnostics();
        (await Ask(Corpus.Shipment, $$""" "path": "status.name", "scan": {{rows}} """)).ShouldHaveDiagnostic("VALUES_SCAN_CAPPED")["params"]!["scan"]!.GetValue<int>().Should().Be(rows);

        // The bound is on the rows that reach the stage: a filter that leaves fewer is not capped.
        var open = Shipments.Count(row => Corpus.Text(row, "status.name") == "Closed");

        open.Should().BeGreaterThan(1).And.BeLessThan(rows);
        (await Ask(Corpus.Shipment, $$""" "path": "shipmentNumber", "scan": {{open + 1}} """, filter: """[{ "where": { "status.name": { "eq": "Closed" } } }]""")).ShouldHaveNoDiagnostics();
        (await Ask(Corpus.Shipment, $$""" "path": "shipmentNumber", "scan": {{open}} """)).ShouldHaveDiagnostic("VALUES_SCAN_CAPPED");

        // A search that keeps nothing still knows how many rows were read.
        var none = await Ask(Corpus.Shipment, """ "path": "status.name", "search": "zz-none", "scan": 5 """);

        none.Items.Should().BeEmpty();
        none.ShouldHaveDiagnostic("VALUES_SCAN_CAPPED");
    }

    // ---- collections --------------------------------------------------------------------------

    /// <summary>
    /// The values at a storage path below the elements of a stored array, as a condition on the path
    /// reads them: a collection that is not there holds one null, one that is there and empty holds
    /// no value at all.
    /// </summary>
    private static IEnumerable<BsonValue> Under(BsonDocument stored, string collection, params string[] member)
    {
        var elements = stored.TryGetValue(collection, out var array) && array is BsonArray items ? items.ToList() : [BsonNull.Value];

        foreach (var element in elements)
        {
            BsonValue at = element;

            foreach (var name in member)
                at = at is BsonDocument document && document.TryGetValue(name, out var next) ? next : BsonNull.Value;

            yield return at;
        }
    }

    [Fact]
    public async Task Under_a_collection_that_is_not_unwound_the_values_are_those_its_elements_hold()
    {
        var expected = Shipments.SelectMany(row => Under(row.Stored, "Items", "Status", "Name")).GroupBy(value => value.IsBsonNull ? null : value.AsString)
            .ToDictionary(group => group.Key ?? "\0", group => group.Count());
        var answer = await Ask(Corpus.Shipment, """ "path": "items.status.name", "counts": true """);

        Listed(answer).ToDictionary(entry => entry.Value == "null" ? "\0" : JsonNode.Parse(entry.Value)!.GetValue<string>(), entry => entry.Count!.Value).Should().BeEquivalentTo(expected);
        expected.Should().ContainKey("\0", "an item without a status name is one null");
        Listed(answer).Sum(entry => entry.Count!.Value).Should().BeGreaterThan(Shipments.Count(row => row.Stored.GetValue("Items", BsonNull.Value) is BsonArray { Count: > 0 }), "a count counts elements");

        // Without counts the list holds the same values, by value.
        Listed(await Ask(Corpus.Shipment, """ "path": "items.status.name" """)).Select(entry => entry.Value)
            .Should().Equal(Listed(answer).Select(entry => entry.Value).OrderBy(value => value == "null" ? "" : value, StringComparer.Ordinal));

        // And every value of the list, null among them, sent back as a condition finds the rows that hold it in some element.
        foreach (var (value, _) in Listed(answer))
        {
            var holding = Shipments.Where(row => Under(row.Stored, "Items", "Status", "Name").Any(held => value == "null" ? held.IsBsonNull : !held.IsBsonNull && held.AsString == JsonNode.Parse(value)!.GetValue<string>())).Select(row => row.Id).ToList();
            var selected = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[ { "match": { "items.status.name": { "in": [{{value}}], "options": { "caseSensitive": true } } } }, { "page": { "limit": 500 } } ]""");

            selected.ShouldHaveIds(holding.Order(), value);
        }

        // The bound is on the rows, however many elements each holds.
        (await Ask(Corpus.Shipment, $$""" "path": "items.status.name", "scan": {{Shipments.Count + 1}} """)).ShouldHaveNoDiagnostics();
        (await Ask(Corpus.Shipment, $$""" "path": "items.status.name", "scan": {{Shipments.Count}} """)).ShouldHaveDiagnostic("VALUES_SCAN_CAPPED");
    }

    [Fact]
    public async Task Under_two_collections_both_are_unwound_for_the_values()
    {
        var rows = Corpus.Rows(Corpus.Conformance);

        // A collection that is not there is one null at its level; one that is there and empty holds no value.
        var expected = rows.SelectMany(row => Under(row.Stored, "Items", "Parts").SelectMany(parts => parts is BsonArray held ? held.Select(part => (BsonValue)part["Sku"]) : [BsonNull.Value]))
            .GroupBy(sku => sku.IsBsonNull ? "null" : $"\"{sku.AsString}\"").ToDictionary(group => group.Key, group => group.Count());
        var answer = await Ask(Corpus.Conformance, """ "path": "items.parts.sku", "counts": true, "limit": 500 """);

        Listed(answer).ToDictionary(entry => entry.Value, entry => entry.Count!.Value).Should().BeEquivalentTo(expected);
        expected.Keys.Should().Contain(key => key != "null", "the corpus holds parts");
        Listed(answer).Sum(entry => entry.Count!.Value).Should().BeGreaterThan(rows.Count(row => row.Stored.TryGetValue("Items", out var items) && items is BsonArray { Count: > 0 }) / 2);

        // Null is in the list exactly when a comparison with null selects a row.
        var nulls = await (await Lab.ClientForAsync(Corpus.Conformance)).SendAsync(Corpus.Conformance, """[ { "match": { "items.parts.sku": { "in": [null] } } }, { "page": { "limit": 500 } } ]""");

        nulls.ShouldBeOk();
        expected.ContainsKey("null").Should().Be(nulls.Items.Count > 0);

        // The rows read are rows, not elements of elements.
        (await Ask(Corpus.Conformance, $$""" "path": "items.parts.sku", "scan": {{rows.Count + 1}} """)).ShouldHaveNoDiagnostics();
        (await Ask(Corpus.Conformance, $$""" "path": "items.parts.sku", "scan": {{rows.Count}} """)).ShouldHaveDiagnostic("VALUES_SCAN_CAPPED");

        // The same on the shipments, whose items hold no weight note: a collection of notes that is there
        // and empty holds no value, so the list is what the stored rows say and nothing more.
        var notes = Shipments.SelectMany(row => Under(row.Stored, "Items", "WeightNotes").SelectMany(held => held is BsonArray written ? written.Select(note => note is BsonDocument document && document.TryGetValue("Number", out var number) ? number : BsonNull.Value) : [BsonNull.Value]))
            .Select(number => number.IsBsonNull ? "null" : $"\"{number.AsString}\"").Distinct().Order(StringComparer.Ordinal).ToList();

        Listed(await Ask(Corpus.Shipment, """ "path": "items.weightNotes.number" """)).Select(entry => entry.Value).Order(StringComparer.Ordinal).Should().Equal(notes);
    }

    [Fact]
    public async Task After_an_unwind_the_alias_member_is_a_scalar_of_the_row()
    {
        const string Unwind = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""";
        var expected = Shipments.SelectMany(row => row.Stored.TryGetValue("Items", out var held) && held is BsonArray items ? items.ToList() : [])
            .Select(item => item is BsonDocument element && element.TryGetValue("Status", out var status) && status is BsonDocument named && named.TryGetValue("Name", out var name) && !name.IsBsonNull ? name.AsString : null)
            .GroupBy(name => name).ToDictionary(group => group.Key ?? "\0", group => group.Count());
        var answer = await Ask(Corpus.Shipment, """ "path": "item.status.name", "counts": true """, before: Unwind);

        Listed(answer).ToDictionary(entry => entry.Value == "null" ? "\0" : JsonNode.Parse(entry.Value)!.GetValue<string>(), entry => entry.Count!.Value).Should().BeEquivalentTo(expected, "one row per item: a shipment without items is no row");
        Texts(answer).Should().Be(Texts(await ByHand(Corpus.Shipment, "item.status.name", before: Unwind)));

        // The projection and the sort a grid's stored stages carry change nothing of the list.
        var stored = await Ask(Corpus.Shipment, """ "path": "item.status.name", "counts": true """,
            before: Unwind + """, { "project": { "id": 1, "position": 1, "item.status.name": 1, "shipmentNumber": 1 } }, { "sort": [{ "shipmentNumber": "desc" }] }""");

        Texts(stored).Should().Be(Texts(answer));
    }

    [Fact]
    public async Task After_a_group_the_values_of_a_key_or_an_aggregate_are_values_of_the_groups()
    {
        const string Group = """{ "group": { "by": [{ "path": "status.name", "as": "statusName" }], "fields": { "n": { "count": true } } } }""";
        var statuses = Shipments.GroupBy(row => Corpus.Text(row, "status.name")).ToList();

        Listed(await Ask(Corpus.Shipment, """ "path": "statusName" """, before: Group)).Select(entry => entry.Value)
            .Should().BeEquivalentTo(statuses.Select(group => group.Key is null ? "null" : $"\"{group.Key}\""));
        Listed(await Ask(Corpus.Shipment, """ "path": "n", "counts": true """, before: Group)).ToDictionary(entry => entry.Value, entry => entry.Count!.Value)
            .Should().BeEquivalentTo(statuses.GroupBy(group => group.Count()).ToDictionary(sizes => sizes.Key.ToString(), sizes => sizes.Count()));
    }

    // ---- refusals, the envelope, the time -----------------------------------------------------

    [Fact]
    public async Task What_it_refuses_is_refused_before_anything_runs()
    {
        var remote = (await Ask(Corpus.Shipment, """ "path": "dep.name" """, before: """{ "resolve": { "path": "department.id", "as": "dep" } }""")).ShouldRefuse("NOT_CONTINUABLE", 400);

        remote["stage"]!.GetValue<int>().Should().Be(1);
        remote["path"]!.GetValue<string>().Should().Be("dep.name");

        (await Ask(Corpus.Shipment, """ "path": "status.name", "limit": 501 """)).ShouldRefuse("VALUES_LIMIT_EXCEEDED", 400).ToJsonString(Plain).Should().Be(
            """{"code":"VALUES_LIMIT_EXCEEDED","message":"The values limit 501 exceeds the maximum of 500; narrow the values with 'search' instead.","stage":0,"params":{"limit":"maxValuesLimit","max":500,"value":501,"count":501}}""");
        (await Ask(Corpus.Shipment, """ "path": "status.name", "scan": 50001 """)).ShouldRefuse("VALUES_LIMIT_EXCEEDED", 400)["params"]!["limit"]!.GetValue<string>().Should().Be("valuesScanRows");
        (await Ask(Corpus.Shipment, """ "path": "status" """)).ShouldRefuse("NOT_FILTERABLE", 400);
        (await Ask(Corpus.Shipment, """ "path": "items" """)).ShouldRefuse("GROUP_ON_COLLECTION", 400);
        (await Ask(Corpus.Shipment, """ "path": "loadStart", "search": "2026" """)).ShouldRefuse("OPTION_NOT_APPLICABLE", 400);

        var after = await (await Transport()).SendAsync(Corpus.Shipment, """[ { "values": { "path": "status.name" } }, { "page": { "limit": 5 } } ]""");

        after.ShouldRefuse("STAGE_AFTER_PAGE", 400)["stage"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Contract_1_answers_the_unknown_stage_it_always_answered()
    {
        var legacy = await Lab.ClientAsync(LabService.Transport, contract: 1);
        var answer = await legacy.SendAsync(Corpus.Shipment, """[ { "values": { "path": "status.name" } } ]""");
        var other = await legacy.SendAsync(Corpus.Shipment, """[ { "facet": { "path": "status.name" } } ]""");

        answer.ShouldRefuse("UNKNOWN_STAGE", 400);
        answer.Text.Should().Be(other.Text.Replace("facet", "values", StringComparison.Ordinal), "contract 1 never had the stage");
    }

    [Fact]
    public async Task It_is_one_command_and_says_so_alone_and_in_a_batch()
    {
        var client = await Transport();
        var request = Request(Corpus.Shipment, """ "path": "status.name", "counts": true """, extra: """ "timing": true, "maxTimeMs": 5000, """);

        // The first asks the addon definitions; the second finds them kept.
        await client.QueryAsync(request);

        var answer = (await client.QueryAsync(request)).ShouldBeOk();
        var list = answer.Body!["timing"]!["db"]!["list"]!.AsArray().Where(command => command!["kind"]!.GetValue<string>() != "addon").ToList();

        list.Should().ContainSingle().Which!["kind"]!.GetValue<string>().Should().Be("values");
        list[0]!["collection"]!.GetValue<string>().Should().Be(Corpus.Shipment);
        list[0]!["docs"]!.GetValue<int>().Should().Be(1);
        answer.Body["timing"]!["rows"]!.GetValue<int>().Should().Be(answer.Items.Count);
        answer.Headers["Server-Timing"].Should().Contain("db;dur=");
        answer.Body.AsObject().Select(member => member.Key).Should().Equal("items", "pageInfo", "timing");

        var batch = await client.BatchAsync([Request(Corpus.Shipment, """ "path": "status.name", "counts": true """), JsonNode.Parse($$"""{ "entityType": "{{Corpus.Shipment}}", "pipeline": [ { "page": { "limit": 1 } } ] }""")!]);

        batch.Results[0].Items.ToJsonString().Should().Be(answer.Items.ToJsonString());
        batch.Results[1].Items.Should().HaveCount(1);
    }
}
