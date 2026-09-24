using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Group.GroupKit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// Area N, first part (N1–N34): the <c>group</c> stage, its <c>by</c> keys, the nine aggregate
/// functions and every refusal each of them owns. Every number is computed from the corpus
/// before the request: the shipment tally per status, the weights, the vehicle mileages.
/// <para>Ported from the legacy <c>group</c> battery; the engine half only.</para>
/// </summary>
[Trait("Category", "Integration")]
public class GroupTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private const string ByStatus = """ "by": [{ "path": "status.name", "as": "k" }] """;

    /// <summary>A pipeline grouping shipments by status with one field, sorted by status.</summary>
    private static string ByStatusWith(string fields) => $$"""[{ "group": { {{ByStatus}}, "fields": { {{fields}} } } }, { "sort": [{ "k": "asc" }] }, { "page": { "limit": 10 } }]""";

    /// <summary>The weights of the shipments that carry one, per status in key order.</summary>
    private static IReadOnlyList<(string Status, List<decimal> Weights, int Rows)> WeightsByStatus() =>
        ShipmentStatuses().Select(entry => (entry.Status,
            Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "status.name") == entry.Status).Select(row => Corpus.Decimal(row, "actualWeight.value")).OfType<decimal>().ToList(),
            entry.Count)).ToList();

    // ── N1–N3 · the stage and its keys ───────────────────────────────────────────────────

    [Fact]
    public async Task N01_a_group_replaces_the_row_with_one_member_per_alias_and_carries_no_key()
    {
        var expected = ShipmentStatuses();
        expected.Should().HaveCountGreaterThan(1);

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Strings("status").Should().Equal(expected.Select(entry => entry.Status));
        Numbers(answer, "total").Should().Equal(expected.Select(entry => (decimal?)entry.Count));
        answer.Items.OfType<JsonObject>().Should().OnlyContain(item => item.Select(pair => pair.Key).Order().SequenceEqual(new[] { "status", "total" }), "the row is the group, not a document");
    }

    [Fact]
    public async Task N02_a_by_alias_carries_its_path_value_and_an_empty_by_is_one_grand_total_bucket()
    {
        // Null and missing share the null bucket; S-DUP is held twice.
        var keys = Corpus.Rows(Corpus.Shipment).GroupBy(row => Corpus.Text(row, "shipmentNumber") is { } number ? Order.FoldCi(number) : null).ToList();
        var nullBucket = keys.Single(group => group.Key is null).Count();
        nullBucket.Should().Be(Corpus.RowsNull(Corpus.Shipment, "shipmentNumber").Count + Corpus.RowsMissing(Corpus.Shipment, "shipmentNumber").Count).And.Be(2);
        var client = await Transport();

        var keyed = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "shipmentNumber", "as": "number" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "number": "asc" }] },
              { "page": { "limit": 500, "includeTotalCount": true } } ]
            """);

        keyed.ShouldHaveTotal(keys.Count);
        keyed.Items[0]!["number"].Should().BeNull();
        Number(keyed.Items[0]!["total"]).Should().Be(nullBucket);
        Number(keyed.Items.Single(item => item!["number"]?.GetValue<string>() == "S-DUP")!["total"]).Should().Be(Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "shipmentNumber") == "S-DUP").Count);
        Numbers(keyed, "total").Sum().Should().Be(Corpus.Counts(Corpus.Shipment).A);

        var total = await client.SendAsync(Corpus.Shipment, """[{ "group": { "by": [], "fields": { "total": { "count": true } } } }, { "page": { "limit": 5, "includeTotalCount": true } }]""");
        total.ShouldHaveTotal(1);
        CountTexts(total, "total").Should().Equal(Corpus.Counts(Corpus.Shipment).A.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task N03_two_by_entries_key_on_the_composite()
    {
        var expected = Corpus.Rows(Corpus.Shipment)
            .GroupBy(row => (Status: Corpus.Text(row, "status.name")!, Deleted: Corpus.ValueAt(row, "isDeleted")!.AsBoolean))
            .OrderBy(group => group.Key.Status, Comparer<string>.Create(Order.CompareCollated)).ThenBy(group => group.Key.Deleted)
            .Select(group => $"{group.Key.Status}/{(group.Key.Deleted ? "true" : "false")}/{group.Count()}")
            .ToList();
        expected.Should().HaveCount(ShipmentStatuses().Count + 1, "the deleted shipment splits one status in two");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }, { "path": "isDeleted", "as": "deleted" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "status": "asc" }, { "deleted": "asc" }] },
              { "page": { "limit": 20, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected.Count);
        answer.Items.Select(item => $"{item!["status"]!.GetValue<string>()}/{(item["deleted"]!.GetValue<bool>() ? "true" : "false")}/{Number(item["total"])}").Should().Equal(expected);
    }

    // ── N4–N10 · what a key and an alias may be ──────────────────────────────────────────

    [Fact]
    public async Task N04_a_by_entry_naming_neither_path_nor_date_trunc_is_refused_as_unknown_path()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "as": "k" }], "fields": { "c": { "count": true } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "UNKNOWN_PATH");
    }

    [Theory]
    [InlineData("N05", "items.text")]
    [InlineData("N07", "items")]
    public async Task N05_N07_a_key_under_a_collection_or_on_the_collection_itself_is_refused(string id, string path)
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "group": { "by": [{ "path": "{{path}}", "as": "k" }], "fields": { "c": { "count": true } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "GROUP_ON_COLLECTION", id);
    }

    [Fact]
    public async Task N06_a_key_on_a_non_scalar_object_is_refused_as_not_filterable()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "status", "as": "status" }], "fields": { "c": { "count": true } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "NOT_FILTERABLE").Should().Contain("a group key needs a scalar");
    }

    [Fact]
    public async Task N08_an_alias_used_twice_across_by_and_fields_is_refused()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "k": { "count": true } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "ALIAS_COLLISION");
    }

    [Fact]
    public async Task N09_an_alias_that_is_not_a_plain_identifier_is_refused()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "status.name", "as": "a.b" }], "fields": { "c": { "count": true } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "INVALID_ALIAS");
    }

    [Fact]
    public async Task N10_keys_and_aggregates_above_max_group_fields_are_refused_and_exactly_the_limit_runs()
    {
        var client = await Transport();
        static string Fields(int count) => string.Join(", ", Enumerable.Range(0, count).Select(index => $$""" "f{{index}}": { "count": true } """));

        var at = await client.SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { {{Fields(19)}} } } }, { "sort": [{ "k": "asc" }] }, { "page": { "limit": 1 } }]""");
        at.ShouldBeOk();
        Number(at.Items[0]!["f18"]).Should().Be(ShipmentStatuses()[0].Count);

        var over = await client.SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { {{Fields(20)}} } } }, { "page": { "limit": 1 } }]""");
        ShouldRefuseOnly(over, "MAX_GROUP_FIELDS_EXCEEDED").Should().Contain("21 fields; the limit is 20");
    }

    // ── N11–N25 · the nine aggregate functions ───────────────────────────────────────────

    [Fact]
    public async Task N11_N12_count_answers_a_per_group_count_as_a_string_and_count_false_counts_too()
    {
        var expected = ShipmentStatuses().Select(entry => entry.Count.ToString(CultureInfo.InvariantCulture)).ToList();
        var client = await Transport();

        var counted = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "c": { "count": true } """));
        counted.ShouldBeOk();
        CountTexts(counted, "c").Should().Equal(expected);

        var quirk = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "c": { "count": false } """));
        quirk.ShouldBeOk();
        CountTexts(quirk, "c").Should().Equal(expected);
    }

    [Fact]
    public async Task N13_count_distinct_counts_distinct_values_counting_null_as_one_and_skipping_a_missing_member()
    {
        var live = Corpus.Where(Corpus.Shipment, row => !Corpus.ValueAt(row, "isDeleted")!.AsBoolean);
        var distinct = live.Where(row => !Corpus.Missing(row, "shipmentNumber")).Select(row => Corpus.Text(row, "shipmentNumber") ?? "\0null").Distinct().Count();
        distinct.Should().BeLessThan(live.Count);

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "isDeleted", "as": "deleted" }], "fields": { "numbers": { "countDistinct": "shipmentNumber" }, "total": { "count": true } } } },
              { "sort": [{ "deleted": "asc" }] },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldBeOk();
        answer.Items[0]!["deleted"]!.GetValue<bool>().Should().BeFalse();
        Number(answer.Items[0]!["total"]).Should().Be(live.Count);
        Number(answer.Items[0]!["numbers"]).Should().Be(distinct);
    }

    [Fact]
    public async Task N14_N16_sum_and_avg_over_a_double_skip_the_row_without_a_value()
    {
        var expected = WeightsByStatus();
        expected.Should().Contain(entry => entry.Weights.Count < entry.Rows, "one shipment has no weight and must be skipped, not counted as zero");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, ByStatusWith(""" "total": { "count": true }, "weight": { "sum": "actualWeight.value" }, "mean": { "avg": "actualWeight.value" } """));

        answer.ShouldBeOk();
        Numbers(answer, "total").Should().Equal(expected.Select(entry => (decimal?)entry.Rows));
        Numbers(answer, "weight").Should().Equal(expected.Select(entry => (decimal?)entry.Weights.Sum()));
        Numbers(answer, "mean").Should().Equal(expected.Select(entry => (decimal?)entry.Weights.Average()));
        Kinds(answer, "weight").Should().OnlyContain(kind => kind == JsonValueKind.Number, "a sum over a double is a JSON number");
    }

    [Fact]
    public async Task N17_sum_and_avg_over_a_decimal_stay_decimals_at_full_precision()
    {
        var client = await Lab.ClientAsync(LabService.Fleet);

        // The numerically stored mileages only: $sum and $avg skip the rows that hold a string.
        List<string> Mileages(bool deleted) => Corpus.Where(Corpus.Vehicle, row => Corpus.ValueAt(row, "isDeleted")!.AsBoolean == deleted).Select(row => Corpus.Number(row, "mileage")).OfType<string>().ToList();

        var deletedMileages = Mileages(true);
        deletedMileages.Should().NotBeEmpty();
        var deleted = await client.SendAsync(Corpus.Vehicle, """
            [ { "match": { "isDeleted": { "eq": true } } },
              { "group": { "by": [{ "path": "isDeleted", "as": "deleted" }], "fields": { "sum": { "sum": "mileage" }, "mean": { "avg": "mileage" } } } },
              { "page": { "limit": 5 } } ]
            """);
        deleted.ShouldBeOk();
        Kinds(deleted, "sum").Should().Equal(JsonValueKind.String);
        Number(deleted.Items[0]!["sum"]).Should().Be(decimal.Parse(ExactSum(deletedMileages), CultureInfo.InvariantCulture));
        Number(deleted.Items[0]!["mean"]).Should().Be(decimal.Parse(ExactSum(deletedMileages), CultureInfo.InvariantCulture) / deletedMileages.Count);

        // 25 fractional digits survive the average; a double would have rounded at 17 significant digits.
        var liveMileages = Mileages(false);
        var exactMean = decimal.Parse(ExactSum(liveMileages), CultureInfo.InvariantCulture) / liveMileages.Count;
        var live = await client.SendAsync(Corpus.Vehicle, """
            [ { "match": { "isDeleted": { "eq": false } } },
              { "group": { "by": [{ "path": "isDeleted", "as": "deleted" }], "fields": { "mean": { "avg": "mileage" } } } },
              { "page": { "limit": 5 } } ]
            """);
        live.ShouldBeOk();
        var mean = live.Items[0]!["mean"]!.GetValue<string>();
        Math.Abs(decimal.Parse(mean, CultureInfo.InvariantCulture) - exactMean).Should().BeLessThan(1e-20m, $"{mean} against {exactMean}");
        mean.Replace(".", "", StringComparison.Ordinal).TrimStart('-', '0').Length.Should().BeGreaterThan(17, "more significant digits than a double holds");
    }

    [Theory]
    [InlineData("N15", "sum", "shipmentNumber", "a string")]
    [InlineData("N15", "sum", "loadStart", "a dateTime")]
    [InlineData("N15", "sum", "isDeleted", "a bool")]
    [InlineData("N15", "sum", "id", "a guid")]
    [InlineData("N15", "sum", "loadingTimeType", "an enum")]
    [InlineData("N18", "avg", "shipmentNumber", "a string")]
    [InlineData("N18", "avg", "loadStart", "a dateTime")]
    [InlineData("N18", "avg", "isDeleted", "a bool")]
    public async Task N15_N18_sum_and_avg_over_a_non_numeric_argument_are_refused(string id, string function, string path, string kind)
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "s": { "{{function}}": "{{path}}" } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "INVALID_AGGREGATE_ARGUMENT", id).Should().Contain($"over {kind}");
    }

    [Fact]
    public async Task N15b_a_sum_over_a_time_span_is_refused()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Template, """[{ "group": { "by": [{ "path": "timeMode", "as": "k" }], "fields": { "s": { "sum": "loadStart.relativeTime" } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "INVALID_AGGREGATE_ARGUMENT");
    }

    [Fact]
    public async Task N19_N23_min_max_first_last_and_push_answer_what_a_reduction_over_the_same_rows_answers()
    {
        var planned = Corpus.Sorted(Corpus.Shipment, [("id", false)], filter: row => Corpus.Text(row, "status.name") == "Planned");
        var numbers = planned.Select(row => Corpus.Text(row, "shipmentNumber")!).ToList();
        numbers.Should().HaveCountGreaterThan(2).And.OnlyContain(number => number != null);
        var collated = numbers.Order(Comparer<string>.Create(Order.CompareCollated)).ToList();
        var client = await Transport();

        var answer = await client.SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "eq": "Planned" } } },
              { "sort": [{ "id": "asc" }] },
              { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": {
                  "lowest": { "min": "shipmentNumber" }, "highest": { "max": "shipmentNumber" },
                  "firstOne": { "first": "shipmentNumber" }, "lastOne": { "last": "shipmentNumber" }, "every": { "push": "shipmentNumber" } } } },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldBeOk();
        var row = answer.Items.Should().ContainSingle().Subject!;
        row["lowest"]!.GetValue<string>().Should().Be(collated[0]);
        row["highest"]!.GetValue<string>().Should().Be(collated[^1]);
        row["firstOne"]!.GetValue<string>().Should().Be(numbers[0]);
        row["lastOne"]!.GetValue<string>().Should().Be(numbers[^1]);
        ((JsonArray)row["every"]!).Select(value => value!.GetValue<string>()).Should().Equal(numbers);

        // first and last follow the order the documents reach the group in; min does not.
        var reversed = await client.SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "eq": "Planned" } } },
              { "sort": [{ "shipmentNumber": "desc" }] },
              { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "firstOne": { "first": "shipmentNumber" }, "lastOne": { "last": "shipmentNumber" }, "lowest": { "min": "shipmentNumber" } } } },
              { "page": { "limit": 5 } } ]
            """);

        reversed.ShouldBeOk();
        reversed.Strings("firstOne").Should().Equal(collated[^1]);
        reversed.Strings("lastOne").Should().Equal(collated[0]);
        reversed.Strings("lowest").Should().Equal(collated[0]);
    }

    [Theory]
    [InlineData("N24", "min", "items.text")]
    [InlineData("N24", "max", "items.text")]
    [InlineData("N24", "countDistinct", "items.text")]
    [InlineData("N24", "first", "items.text")]
    [InlineData("N24", "last", "items.text")]
    [InlineData("N24", "sum", "items.quantity.value")]
    public async Task N24_every_aggregate_but_push_is_refused_over_a_path_under_a_collection(string id, string function, string path)
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "a": { "{{function}}": "{{path}}" } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "GROUP_ON_COLLECTION", $"{id} {function}");
    }

    [Fact]
    public async Task N24b_push_is_the_one_aggregate_the_engine_allows_over_a_path_under_a_collection()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, ByStatusWith(""" "a": { "push": "items.text" } """));

        answer.ShouldBeOk();
        answer.Strings("k").Should().Equal(ShipmentStatuses().Select(entry => entry.Status));
        answer.Values("a").Should().OnlyContain(value => value is JsonArray);
    }

    [Fact]
    public async Task N25_a_push_alias_is_a_collection_neither_sortable_nor_filterable()
    {
        var client = await Transport();
        const string Group = """{ "group": { "by": [{ "path": "status.name", "as": "k" }], "fields": { "every": { "push": "shipmentNumber" } } } }""";

        ShouldRefuseOnly(await client.SendAsync(Corpus.Shipment, $$"""[{{Group}}, { "sort": [{ "every": "asc" }] }, { "page": { "limit": 3 } }]"""), "NOT_SORTABLE");
        ShouldRefuseOnly(await client.SendAsync(Corpus.Shipment, $$"""[{{Group}}, { "match": { "every": { "eq": "S-0001" } } }, { "page": { "limit": 3 } }]"""), "NOT_FILTERABLE");
    }

    // ── N26–N34 · aggregate arguments ────────────────────────────────────────────────────

    [Theory]
    [InlineData("N26", """{ "median": "actualWeight.value" }""")]
    [InlineData("N26", """{ "stdDev": "actualWeight.value" }""")]
    [InlineData("N27", "{}")]
    public async Task N26_N27_an_unknown_or_empty_aggregate_function_is_refused(string id, string aggregation)
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "a": {{aggregation}} } } }, { "page": { "limit": 3 } }]""");

        var message = ShouldRefuseOnly(answer, "UNKNOWN_AGG_FUNCTION", id);

        if (aggregation == "{}")
            message.Should().Be("'' is not an aggregate function.");
    }

    [Fact]
    public async Task N28_an_aggregate_with_a_null_argument_is_refused_for_want_of_one()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "a": { "sum": null } } } }, { "page": { "limit": 3 } }]""");

        ShouldRefuseOnly(answer, "INVALID_AGGREGATE_ARGUMENT").Should().Be("'sum' needs an argument.");
    }

    [Fact]
    public async Task N29_an_aggregate_over_an_unstored_member_is_refused_as_not_stored()
    {
        // Legacy: push over the shipment's unstored tours.isMirrored. The lab model's unstored
        // members are on the conformance entity.
        var client = await Lab.ClientAsync(LabService.Conformance);

        foreach (var path in new[] { "scratch", "computed" })
        {
            var answer = await client.SendAsync(Corpus.Conformance, $$"""[{ "group": { "by": [{ "path": "name", "as": "k" }], "fields": { "a": { "push": "{{path}}" } } } }, { "page": { "limit": 3 } }]""");

            ShouldRefuseOnly(answer, "NOT_STORED", path).Should().Contain("in the wire view only");
        }
    }

    [Fact]
    public async Task N30_an_argument_written_as_a_path_object_binds_identically_to_the_bare_string()
    {
        var client = await Transport();

        var bare = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "s": { "sum": "actualWeight.value" } """));
        var wrapped = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "s": { "sum": { "path": "actualWeight.value" } } """));

        bare.ShouldBeOk();
        Numbers(bare, "s").Should().Equal(WeightsByStatus().Select(entry => (decimal?)entry.Weights.Sum()));
        wrapped.Text.Should().Be(bare.Text);
    }

    [Fact]
    public async Task N31_N32_a_variable_and_a_literal_argument_bind_and_both_come_back_as_decimal_strings()
    {
        var client = await Transport();
        var counts = GroupKit.Counts(ShipmentStatuses());

        var literal = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "s": { "sum": { "literal": 2 } } """));
        literal.ShouldBeOk();
        CountTexts(literal, "s").Should().Equal(counts.Select(count => (count * 2).ToString(CultureInfo.InvariantCulture)));

        var variable = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "s": { "sum": { "$var": "w" } } """), new { w = 3 });
        variable.ShouldBeOk();
        CountTexts(variable, "s").Should().Equal(counts.Select(count => (count * 3).ToString(CultureInfo.InvariantCulture)));

        var unbound = await client.SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "s": { "sum": { "$var": "nope" } } } } }, { "page": { "limit": 5 } }]""");
        ShouldRefuseOnly(unbound, "UNBOUND_VARIABLE");
    }

    [Theory]
    [InlineData("N33", "add", """{ "add": [{ "path": "actualWeight.value" }, { "literal": 1 }] }""")]
    [InlineData("N33", "subtract", """{ "subtract": [{ "path": "actualWeight.value" }, { "literal": 200 }] }""")]
    [InlineData("N33", "multiply", """{ "multiply": [{ "path": "actualWeight.value" }, { "literal": 2 }] }""")]
    [InlineData("N33", "divide", """{ "divide": [{ "path": "actualWeight.value" }, { "literal": 2 }] }""")]
    [InlineData("N33", "nested", """{ "multiply": [{ "subtract": [{ "path": "actualWeight.value" }, { "literal": 200 }] }, { "divide": [{ "literal": 10 }, { "literal": 2 }] }] }""")]
    public async Task N33_add_subtract_multiply_and_divide_compile_and_nest(string id, string name, string argument)
    {
        _ = id;
        Func<decimal, decimal> apply = name switch
        {
            "add" => weight => weight + 1,
            "subtract" => weight => weight - 200,
            "multiply" => weight => weight * 2,
            "divide" => weight => weight / 2,
            _ => weight => (weight - 200) * (10m / 2),
        };
        // A row without a weight makes the whole expression null, and the sum skips it.
        var expected = WeightsByStatus().Select(entry => (decimal?)entry.Weights.Sum(apply)).ToList();

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, ByStatusWith($$""" "s": { "sum": {{argument}} } """));

        answer.ShouldBeOk();
        Numbers(answer, "s").Should().Equal(expected, name);
    }

    [Fact]
    public async Task N33c_coalesce_compiles_for_min_but_resolves_no_kind_so_sum_refuses_it_and_an_empty_operand_list_is_refused()
    {
        var client = await Transport();
        var expected = WeightsByStatus().Select(entry => (decimal?)(entry.Weights.Count < entry.Rows ? Math.Min(0, entry.Weights.Min()) : entry.Weights.Min())).ToList();

        var picked = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "s": { "min": { "coalesce": [{ "path": "actualWeight.value" }, { "literal": 0 }] } } """));
        picked.ShouldBeOk();
        Numbers(picked, "s").Should().Equal(expected, "only the group holding the row without a weight falls back to 0");

        var summed = await client.SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "s": { "sum": { "coalesce": [{ "path": "actualWeight.value" }, { "literal": 0 }] } } } } }, { "page": { "limit": 5 } }]""");
        ShouldRefuseOnly(summed, "INVALID_AGGREGATE_ARGUMENT").Should().Contain("over an unknown");

        var empty = await client.SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "s": { "sum": { "add": [] } } } } }, { "page": { "limit": 5 } }]""");
        ShouldRefuseOnly(empty, "INVALID_AGGREGATE_ARGUMENT").Should().Be("'add' needs operands.");
    }

    [Theory]
    [InlineData("N33b", "min")]
    [InlineData("N33b", "max")]
    [InlineData("N33b", "first")]
    [InlineData("N33b", "last")]
    [InlineData("N33b", "push")]
    [InlineData("N33b", "sum")]
    [InlineData("N33b", "avg")]
    [InlineData("N33b", "countDistinct")]
    public async Task N33b_an_unrecognised_argument_operator_is_refused_for_every_aggregate_naming_the_forms_that_exist(string id, string function)
    {
        _ = id;
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, ByStatusWith($$""" "s": { "{{function}}": { "power": [{ "literal": 2 }, { "literal": 3 }] } } """));

        ShouldRefuseOnly(answer, "INVALID_AGGREGATE_ARGUMENT", function).Should()
            .Be("'power' is not an argument operator; one of add, subtract, multiply, divide, coalesce is, or a path, a literal or a variable.");
    }

    [Fact]
    public async Task N33b2_a_bare_unknown_key_is_refused_naming_it_and_count_which_takes_no_argument_is_exempt()
    {
        var client = await Transport();

        var pushed = await client.SendAsync(Corpus.Shipment, $$"""[{ "group": { {{ByStatus}}, "fields": { "s": { "push": { "bogus": 1 } } } } }, { "match": { "k": { "eq": "Planned" } } }, { "page": { "limit": 5 } }]""");
        ShouldRefuseOnly(pushed, "INVALID_AGGREGATE_ARGUMENT").Should().Contain("'bogus' is not an argument operator");

        var counted = await client.SendAsync(Corpus.Shipment, ByStatusWith(""" "s": { "count": { "power": [{ "literal": 2 }, { "literal": 3 }] } } """));
        counted.ShouldBeOk();
        CountTexts(counted, "s").Should().Equal(ShipmentStatuses().Select(entry => entry.Count.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task N_var_a_declared_variable_reaches_a_post_group_filter_on_an_alias()
    {
        const int Floor = 10;
        var expected = ShipmentStatuses().Where(entry => entry.Count >= Floor).Select(entry => entry.Status).ToList();
        expected.Should().NotBeEmpty().And.HaveCountLessThan(ShipmentStatuses().Count);

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "total": { "count": true } } } },
              { "match": { "total": { "gte": { "$var": "floor" } } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """, new { floor = Floor });

        answer.ShouldHaveTotal(expected.Count);
        answer.Strings("status").Should().Equal(expected);
    }

    [Fact]
    public async Task N_scope_every_grouped_count_is_an_organisation_A_count()
    {
        var answer = await (await Lab.ClientAsync(LabService.Staff)).SendAsync(Corpus.Employee, """
            [ { "group": { "by": [{ "path": "isDeleted", "as": "deleted" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "deleted": "asc" }] },
              { "page": { "limit": 5, "includeTotalCount": true } } ]
            """);

        answer.ShouldBeOk();
        Numbers(answer, "total").Sum().Should().Be(Corpus.Counts(Corpus.Employee).A, "the organisation B clones are scoped out");
    }
}
