using System.Globalization;
using System.Text.Json;
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
/// Aggregate × kind: what the engine puts on the wire for every aggregate function over every
/// kind the lab carries, the <c>by</c> aliases, a push under a collection, a composite key over a
/// missing member and aggregates over nothing but nulls. The legacy battery asserted the types the
/// TypeScript client decodes these into; that half is the client's. The engine half is the wire
/// value and its JSON kind, each computed from the corpus first.
/// <para>Ported from the legacy <c>group-types</c> battery.</para>
/// </summary>
[Trait("Category", "Integration")]
public class GroupTypesTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static Task<LabClient> Fleet() => Lab.ClientAsync(LabService.Fleet);

    /// <summary>The distinct stored values a <c>countDistinct</c> sees: a missing member contributes nothing, null is a value.</summary>
    private static int Distinct(IEnumerable<CorpusRow> rows, string path) =>
        rows.Select(row => Corpus.ValueAt(row, path)).Where(value => value is not null).Select(value => value!.ToJson()).Distinct().Count();

    /// <summary>A decimal literal in the engine's canonical spelling: no trailing fractional zeros.</summary>
    private static string Canonical(string literal) =>
        literal.Contains('.', StringComparison.Ordinal) ? literal.TrimEnd('0').TrimEnd('.') : literal;

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [Fact]
    public async Task GT1_count_and_count_distinct_count_exactly_whatever_the_counted_kind_and_count_travels_as_a_string()
    {
        var statuses = ShipmentStatuses();

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": {
                  "total": { "count": true },
                  "distinctString": { "countDistinct": "shipmentNumber" }, "distinctGuid": { "countDistinct": "id" },
                  "distinctDate": { "countDistinct": "loadStart" }, "distinctEnum": { "countDistinct": "loadingTimeType" },
                  "distinctBool": { "countDistinct": "isDeleted" }, "distinctDouble": { "countDistinct": "actualWeight.value" } } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(statuses.Count);
        answer.Strings("status").Should().Equal(statuses.Select(entry => entry.Status));
        CountTexts(answer, "total").Should().Equal(statuses.Select(entry => entry.Count.ToString(CultureInfo.InvariantCulture)), "a count is a long, and a long travels as a string");

        foreach (var (alias, path) in new[] { ("distinctString", "shipmentNumber"), ("distinctGuid", "id"), ("distinctDate", "loadStart"), ("distinctEnum", "loadingTimeType"), ("distinctBool", "isDeleted"), ("distinctDouble", "actualWeight.value") })
            Numbers(answer, alias).Should().Equal(statuses.Select(entry => (decimal?)Distinct(Corpus.ShipmentsWithStatus(entry.Status), path)), alias);
    }

    [Fact]
    public async Task GT2_sum_and_avg_over_a_double_or_an_int_are_numbers_and_over_a_decimal_they_are_exact_strings()
    {
        var closed = Corpus.ShipmentsWithStatus("Closed").Select(row => Corpus.Decimal(row, "actualWeight.value")).OfType<decimal>().ToList();
        closed.Should().NotBeEmpty();

        var shipments = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "sumDouble": { "sum": "actualWeight.value" }, "avgDouble": { "avg": "actualWeight.value" } } } },
              { "sort": [{ "status": "asc" }] },
              { "page": { "limit": 10 } } ]
            """);
        shipments.ShouldBeOk();
        shipments.Items[0]!["status"]!.GetValue<string>().Should().Be("Closed");
        Kinds(shipments, "sumDouble").Should().OnlyContain(kind => kind == JsonValueKind.Number);
        Kinds(shipments, "avgDouble").Should().OnlyContain(kind => kind == JsonValueKind.Number);
        Number(shipments.Items[0]!["sumDouble"]).Should().Be(closed.Sum());
        Number(shipments.Items[0]!["avgDouble"]).Should().Be(closed.Average());

        // The live vehicles: the numerically stored mileages (a string-stored one is skipped), and the tank capacities, Int32 max among them.
        var live = Corpus.Where(Corpus.Vehicle, row => !Corpus.ValueAt(row, "isDeleted")!.AsBoolean);
        var mileages = live.Select(row => Corpus.Number(row, "mileage")).OfType<string>().ToList();
        var tanks = live.Select(row => Corpus.ValueAt(row, "fuelTankCapacity")).OfType<BsonInt32>().Select(value => (long)value.Value).ToList();

        var vehicles = await (await Fleet()).SendAsync(Corpus.Vehicle, """
            [ { "group": { "by": [{ "path": "isDeleted", "as": "deleted" }], "fields": { "sumDecimal": { "sum": "mileage" }, "avgDecimal": { "avg": "mileage" }, "sumInt": { "sum": "fuelTankCapacity" }, "avgInt": { "avg": "fuelTankCapacity" } } } },
              { "sort": [{ "deleted": "asc" }] },
              { "page": { "limit": 10 } } ]
            """);
        vehicles.ShouldBeOk();
        var row = vehicles.Items[0]!;
        row["deleted"]!.GetValue<bool>().Should().BeFalse();

        row["sumDecimal"]!.GetValueKind().Should().Be(JsonValueKind.String, "a decimal is a string everywhere in a row");
        row["avgDecimal"]!.GetValueKind().Should().Be(JsonValueKind.String);
        Number(row["sumDecimal"]).Should().Be(decimal.Parse(ExactSum(mileages), CultureInfo.InvariantCulture));
        row["sumDecimal"]!.GetValue<string>().Should().NotBe(((double)decimal.Parse(ExactSum(mileages), CultureInfo.InvariantCulture)).ToString("R", CultureInfo.InvariantCulture), "no double sits between the stored decimals and the wire");

        row["sumInt"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        row["avgInt"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        Number(row["sumInt"]).Should().Be(tanks.Sum());
        ((double)Number(row["avgInt"])!.Value).Should().BeApproximately(tanks.Average(), 1e-6);
    }

    [Fact]
    public async Task GT3_min_max_first_and_last_carry_the_source_kind_faithfully_on_every_scalar_kind_enums_included()
    {
        var planned = Corpus.ShipmentsWithStatus("Planned");
        planned.Should().HaveCountGreaterThan(2);
        BsonValue Extreme(string path, bool max) => planned.Select(row => Corpus.ValueAt(row, path) ?? BsonNull.Value)
            .Order(Comparer<BsonValue>.Create((a, b) => Order.Compare(a, b))).ToList() is var ordered && max ? ordered[^1] : ordered[0];

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "eq": "Planned" } } },
              { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": {
                  "minString": { "min": "shipmentNumber" }, "maxString": { "max": "shipmentNumber" },
                  "minDate": { "min": "loadStart" }, "maxDate": { "max": "loadStart" },
                  "minGuid": { "min": "id" }, "maxGuid": { "max": "id" },
                  "minBool": { "min": "isDeleted" }, "maxBool": { "max": "isDeleted" },
                  "minDouble": { "min": "actualWeight.value" }, "maxDouble": { "max": "actualWeight.value" },
                  "minEnum": { "min": "loadingTimeType" }, "maxEnum": { "max": "loadingTimeType" },
                  "firstString": { "first": "shipmentNumber" }, "lastString": { "last": "shipmentNumber" }, "pushString": { "push": "shipmentNumber" } } } },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldBeOk();
        var row = answer.Items.Should().ContainSingle().Subject!;
        var numbers = planned.Select(item => Corpus.Text(item, "shipmentNumber")!).ToList();

        row["minString"]!.GetValue<string>().Should().Be(Extreme("shipmentNumber", false).AsString);
        row["maxString"]!.GetValue<string>().Should().Be(Extreme("shipmentNumber", true).AsString);
        row["minDate"]!.GetValue<string>().Should().Be(Iso(Extreme("loadStart", false).ToUniversalTime()));
        row["maxDate"]!.GetValue<string>().Should().Be(Iso(Extreme("loadStart", true).ToUniversalTime()));
        row["minGuid"]!.GetValue<string>().Should().Be(Corpus.IdsOf(planned).Min()!.Wire());
        row["maxGuid"]!.GetValue<string>().Should().Be(Corpus.IdsOf(planned).Max()!.Wire());
        row["minBool"]!.GetValue<bool>().Should().Be(Extreme("isDeleted", false).AsBoolean);
        row["maxBool"]!.GetValue<bool>().Should().Be(Extreme("isDeleted", true).AsBoolean);
        row["minDouble"]!.GetValue<double>().Should().Be(Extreme("actualWeight.value", false).AsDouble);
        row["maxDouble"]!.GetValue<double>().Should().Be(Extreme("actualWeight.value", true).AsDouble);

        // An enum travels as its stored number, the undeclared 99 included; the absent row is skipped.
        var enums = planned.Select(item => Corpus.ValueAt(item, "loadingTimeType")).OfType<BsonInt32>().Select(value => value.Value).ToList();
        enums.Should().Contain(99);
        row["minEnum"]!.GetValue<int>().Should().Be(enums.Min());
        row["maxEnum"]!.GetValue<int>().Should().Be(enums.Max());

        numbers.Should().Contain(row["firstString"]!.GetValue<string>()).And.Contain(row["lastString"]!.GetValue<string>());
        ((JsonArray)row["pushString"]!).Select(value => value!.GetValue<string>()).Should().BeEquivalentTo(numbers);
    }

    [Fact]
    public async Task GT4_min_max_first_last_and_push_over_a_decimal_and_a_time_span_carry_strings()
    {
        var live = Corpus.Where(Corpus.Vehicle, row => !Corpus.ValueAt(row, "isDeleted")!.AsBoolean);
        // A string-stored decimal sorts above every number, so max is a string-stored row's value.
        var ordered = live.Select(row => Corpus.ValueAt(row, "mileage")).Where(value => value is { IsBsonNull: false }).Order(Comparer<BsonValue?>.Create((a, b) => Order.Compare(a, b))).ToList();
        ordered[^1].Should().BeOfType<BsonString>();
        string Text(BsonValue? value) => Canonical(value is BsonString text ? text.Value : Order.NumberText(value!));

        var vehicles = await (await Fleet()).SendAsync(Corpus.Vehicle, """
            [ { "match": { "isDeleted": { "eq": false } } },
              { "group": { "by": [{ "path": "isDeleted", "as": "deleted" }], "fields": {
                  "minDecimal": { "min": "mileage" }, "maxDecimal": { "max": "mileage" },
                  "firstDecimal": { "first": "mileage" }, "lastDecimal": { "last": "mileage" }, "pushDecimal": { "push": "mileage" } } } },
              { "page": { "limit": 5 } } ]
            """);

        vehicles.ShouldBeOk();
        var row = vehicles.Items[0]!;
        row["minDecimal"]!.GetValue<string>().Should().Be(Text(ordered[0]));
        row["maxDecimal"]!.GetValue<string>().Should().Be(Text(ordered[^1]));
        row["firstDecimal"]!.GetValueKind().Should().Be(JsonValueKind.String);
        row["lastDecimal"]!.GetValueKind().Should().Be(JsonValueKind.String);

        // A pushed element travels in the member's wire form: the Decimal128 125000.00 and the
        // string-stored 125000.00 render as the same decimal text, and the storage spelling is gone.
        var pushed = ((JsonArray)row["pushDecimal"]!).Select(value => value!.GetValue<string>()).ToList();
        pushed.Should().BeEquivalentTo(live.Select(item => Corpus.ValueAt(item, "mileage")).Where(value => value is { IsBsonNull: false }).Select(Text));
        var at125000 = live.Count(item => Corpus.ValueAt(item, "mileage") is { IsBsonNull: false } value && Order.CompareDecimal(value is BsonString text ? text.Value : Order.NumberText(value), "125000") == 0);
        at125000.Should().BeGreaterThan(1, "the string-stored 125000.00 row is among them");
        pushed.Count(value => value == "125000").Should().Be(at125000);
        pushed.Should().NotContain("125000.00");

        // The time span: every relative template holds the same one, spelled as an ISO duration.
        var relative = Corpus.Where(Corpus.Template, row => Corpus.ValueAt(row, "timeMode")!.AsInt32 == 1);
        var spans = relative.Select(row => Corpus.Text(row, "loadStart.relativeTime")!).Distinct().ToList();
        spans.Should().ContainSingle();
        var iso = System.Xml.XmlConvert.ToString(TimeSpan.Parse(spans[0], CultureInfo.InvariantCulture));

        var templates = await (await Transport()).SendAsync(Corpus.Template, """
            [ { "match": { "timeMode": { "eq": "RelativeFromStart" } } },
              { "group": { "by": [{ "path": "timeMode", "as": "mode" }], "fields": { "minSpan": { "min": "loadStart.relativeTime" }, "maxSpan": { "max": "loadStart.relativeTime" }, "distinctSpan": { "countDistinct": "loadStart.relativeTime" } } } },
              { "page": { "limit": 5 } } ]
            """);

        templates.ShouldBeOk();
        var template = templates.Items.Should().ContainSingle().Subject!;
        template["minSpan"]!.GetValue<string>().Should().Be(iso);
        template["maxSpan"]!.GetValue<string>().Should().Be(iso);
        Number(template["distinctSpan"]).Should().Be(1);
        template["mode"]!.GetValue<int>().Should().Be(1, "an enum key travels as its stored number");
    }

    [Fact]
    public async Task GT5_a_by_alias_carries_its_source_kind_on_every_kind_an_enum_as_its_stored_number()
    {
        var planned = Corpus.Sorted(Corpus.Shipment, [("id", false)], filter: row => Corpus.Text(row, "status.name") == "Planned");
        var client = await Transport();

        var shipments = await client.SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "eq": "Planned" } } },
              { "group": { "by": [{ "path": "id", "as": "guidKey" }, { "path": "loadStart", "as": "dateKey" }, { "path": "isDeleted", "as": "boolKey" }, { "path": "actualWeight.value", "as": "doubleKey" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "guidKey": "asc" }] },
              { "page": { "limit": 10 } } ]
            """);

        shipments.ShouldBeOk();
        shipments.Items.Should().HaveCount(planned.Count);
        var first = shipments.Items[0]!;
        first["guidKey"]!.GetValue<string>().Should().Be(planned[0].WireId);
        first["dateKey"]!.GetValue<string>().Should().Be(Iso(Corpus.Date(planned[0], "loadStart")!.Value));
        first["boolKey"]!.GetValue<bool>().Should().Be(Corpus.ValueAt(planned[0], "isDeleted")!.AsBoolean);
        first["doubleKey"]!.GetValue<double>().Should().Be(Corpus.ValueAt(planned[0], "actualWeight.value")!.AsDouble);

        // Every stored value in its own bucket, the absent row keying null first.
        var enums = Corpus.Rows(Corpus.Shipment)
            .GroupBy(row => Corpus.ValueAt(row, "loadingTimeType") is BsonInt32 value ? value.Value : (int?)null)
            .OrderBy(group => group.Key.HasValue).ThenBy(group => group.Key)
            .ToList();
        var byEnum = await client.SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "loadingTimeType", "as": "timeType" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "timeType": "asc" }] }, { "page": { "limit": 10 } }]""");
        byEnum.ShouldBeOk();
        byEnum.Values("timeType").Select(value => value is null ? (int?)null : value.GetValue<int>()).Should().Equal(enums.Select(group => group.Key));
        Numbers(byEnum, "total").Should().Equal(enums.Select(group => (decimal?)group.Count()));

        var numericMileages = Corpus.Rows(Corpus.Vehicle).Select(row => Corpus.Number(row, "mileage")).OfType<string>().Order(Comparer<string>.Create(Order.CompareDecimal)).ToList();
        var byDecimal = await (await Fleet()).SendAsync(Corpus.Vehicle, """[{ "group": { "by": [{ "path": "mileage", "as": "decimalKey" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "decimalKey": "asc" }] }, { "page": { "limit": 10 } }]""");
        byDecimal.ShouldBeOk();
        byDecimal.Items[0]!["decimalKey"]!.GetValue<string>().Should().Be(Canonical(numericMileages[0]));
    }

    [Fact]
    public async Task GT6_a_push_over_a_path_under_a_collection_answers_one_array_per_document_skipping_a_missing_collection()
    {
        var closed = Corpus.ShipmentsWithStatus("Closed");
        var kept = closed.Where(row => !Corpus.Missing(row, "items")).ToList();
        kept.Should().HaveCountLessThan(closed.Count, "one Closed shipment has no items member at all, and a push skips it");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "eq": "Closed" } } },
              { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "texts": { "push": "items.text" } } } },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldBeOk();
        var texts = (JsonArray)answer.Items.Should().ContainSingle().Subject!["texts"]!;
        texts.Should().HaveCount(kept.Count).And.OnlyContain(element => element is JsonArray);
        texts.Select(element => ((JsonArray)element!).Count).Should().BeEquivalentTo(kept.Select(row => Corpus.Elements(row, "items").Count));
    }

    [Fact]
    public async Task GT7_a_composite_by_normalises_a_missing_member_to_null_and_buckets_it_with_an_explicit_null_as_a_single_by_does()
    {
        var nullBucket = Corpus.RowsNull(Corpus.Shipment, "shipmentNumber").Count + Corpus.RowsMissing(Corpus.Shipment, "shipmentNumber").Count;
        nullBucket.Should().Be(2);
        var client = await Transport();

        var single = await client.SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "shipmentNumber", "as": "number" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 3 } }]""");
        single.ShouldBeOk();
        single.Items[0]!["number"].Should().BeNull();
        Number(single.Items[0]!["total"]).Should().Be(nullBucket);

        var composite = await client.SendAsync(Corpus.Shipment, """[{ "group": { "by": [{ "path": "isDeleted", "as": "deleted" }, { "path": "shipmentNumber", "as": "number" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 3 } }]""");
        composite.ShouldBeOk();
        var first = composite.Items[0]!.AsObject();
        first.ContainsKey("number").Should().BeTrue("the member is always present");
        first["number"].Should().BeNull();
        Number(first["total"]).Should().Be(nullBucket);
        first["deleted"]!.GetValue<bool>().Should().BeFalse();
        composite.Strings("number").Should().Equal(single.Strings("number"));
        Numbers(composite, "total").Should().Equal(Numbers(single, "total"));
    }

    [Fact]
    public async Task GT8_over_a_group_with_nothing_but_nulls_avg_is_null_sum_is_0_count_distinct_is_1_and_min_max_are_null()
    {
        var live = Corpus.Where(Corpus.Employee, row => !Corpus.ValueAt(row, "isDeleted")!.AsBoolean);
        live.Should().OnlyContain(row => Corpus.Null(row, "disabilityLevel"), "the member is stored null on every row: a null, not a missing member");

        var answer = await (await Lab.ClientAsync(LabService.Staff)).SendAsync(Corpus.Employee, """
            [ { "group": { "by": [{ "path": "isDeleted", "as": "deleted" }], "fields": {
                  "avgNull": { "avg": "disabilityLevel" }, "sumNull": { "sum": "disabilityLevel" }, "distinctNull": { "countDistinct": "disabilityLevel" },
                  "minNull": { "min": "disabilityLevel" }, "maxNull": { "max": "disabilityLevel" } } } },
              { "sort": [{ "deleted": "asc" }] },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldBeOk();
        var row = answer.Items[0]!.AsObject();
        row["deleted"]!.GetValue<bool>().Should().BeFalse();
        row["avgNull"].Should().BeNull();
        Number(row["sumNull"]).Should().Be(0);
        Number(row["distinctNull"]).Should().Be(1);
        row["minNull"].Should().BeNull();
        row["maxNull"].Should().BeNull();
    }
}
