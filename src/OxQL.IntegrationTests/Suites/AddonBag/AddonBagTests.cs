using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.AddonBag;

/// <summary>
/// Area T: the addon bag, as the engine binds it from an organisation's definitions. The legacy
/// battery drove it through the typed client and the <c>/schema/addons</c> fold; the endpoint and
/// the fold are OxS and client code, so this suite sends what the fold compiled to and asserts
/// what the engine answers. <c>conformance.entity</c> carries the five purpose-built definitions
/// (a closed value list, an object definition, a wrapped decimal, a key with a space);
/// <c>transport.shipment</c> in organisation A keeps the retired <c>weight</c> definition stored
/// before the live one (the SH5 trigger).
/// </summary>
[Trait("Category", "Integration")]
public class AddonBagTests
{
    private static Task<LabClient> Conformance() => Lab.ClientAsync(LabService.Conformance);

    private static IReadOnlyList<CorpusRow> Rows => Corpus.Rows(Corpus.Conformance);

    private static string Page(string match, string extra = "") =>
        $$"""[{ "match": {{match}} }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, {{(extra.Length > 0 ? extra + ", " : "")}}{ "page": { "limit": 50, "count": true } }]""";

    /// <summary>The ids of the conformance rows whose bag value at <paramref name="key"/> satisfies <paramref name="predicate"/>, in id order.</summary>
    private static IReadOnlyList<Guid> Where(string key, Func<BsonValue?, bool> predicate, string entity = Corpus.Conformance) =>
        Corpus.SortedIds(entity, "id", filter: row => predicate(Corpus.ValueAt(row, "addon." + key)));

    [Fact]
    public async Task T0_the_retired_definition_stored_first_no_longer_shadows_the_live_one_the_key_filters_on_every_bag()
    {
        foreach (var (entity, service) in new[] { (Corpus.Shipment, LabService.Transport), (Corpus.Employee, LabService.Staff), (Corpus.Vehicle, LabService.Fleet) })
        {
            var expected = Corpus.SortedIds(entity, "id", filter: row => Corpus.Number(row, "addon.weight") is { } weight && Order.CompareDecimal(weight, "1000") >= 0);
            expected.Should().ContainSingle($"{entity}: the rich bag's weight");

            var answer = await (await Lab.ClientAsync(service)).SendAsync(entity, Page("""{ "addon.weight": { "gte": 1000 } }"""));

            // A decimal key is compared by its value converted to a decimal, text included: there is nothing left out to announce.
            answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count).ShouldHaveNoDiagnostics();
        }

        Corpus.Entity(Corpus.Shipment).AddonDefinitions[Org.A][0].Should().Match<AddonDef>(def => def.Path == "weight" && def.Retired, "the trigger must stay in the corpus");
    }

    [Fact]
    public async Task T4_a_defined_scalar_key_filters_and_sorts_and_the_sort_says_it_is_best_effort()
    {
        var wanted = Corpus.Text(Corpus.Row(Corpus.Conformance, "c-alpha"), "addon.contractNumber")!;
        var expected = Where("contractNumber", value => value is BsonString text && text.Value == wanted);
        expected.Should().ContainSingle();

        var client = await Conformance();
        var filtered = await client.SendAsync(Corpus.Conformance, Page($$"""{ "addon.contractNumber": { "eq": "{{wanted}}" } }"""));
        filtered.ShouldHaveIds(expected).ShouldHaveTotal(1).ShouldHaveNoDiagnostics();

        var order = Corpus.SortedIds(Corpus.Conformance, "addon.contractNumber", descending: true);
        var sorted = await client.SendAsync(Corpus.Conformance, """[{ "sort": [{ "addon.contractNumber": "desc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 10 } }]""");

        sorted.ShouldHaveIds(order);
        sorted.ShouldHaveDiagnostic("SORT_ON_ADDON")["path"]!.GetValue<string>().Should().Be("addon.contractNumber");
    }

    [Fact]
    public async Task T9_T13_a_key_with_a_space_binds_and_a_bool_key_matches_the_boolean_and_its_string_spelling()
    {
        const string key = "Ablieferbelege vorhanden";
        var yes = Where(key, value => value is BsonBoolean { Value: true });
        var no = Where(key, value => value is BsonBoolean { Value: false });
        yes.Should().ContainSingle();
        no.Should().ContainSingle();

        var client = await Conformance();

        foreach (var (operand, expected) in new[] { ("true", yes), ("false", no), ("\"true\"", yes), ("\"false\"", no) })
        {
            var answer = await client.SendAsync(Corpus.Conformance, Page($$"""{ "addon.{{key}}": { "eq": {{operand}} } }"""));

            answer.ShouldHaveIds(expected, $"operand {operand}").ShouldHaveTotal(expected.Count);
        }
    }

    [Fact]
    public async Task T16_a_wrapped_decimal_matches_at_the_key_the_wrapper_leaf_is_not_a_path_and_the_value_travels_back_as_a_decimal_number()
    {
        var wrapped = Rows.Where(row => row.Stored["Addon"] is BsonDocument bag && bag.TryGetValue("zuschlag", out var value)
            && value is BsonDocument { } wrapper && wrapper["_t"] == Addons.DecimalDiscriminator).ToList();
        wrapped.Should().HaveCount(2, "the conformance bag carries a wrapped decimal on the two full rows");
        var values = wrapped.Select(row => Order.NumberText(row.Stored["Addon"]["zuschlag"]["_v"])).Distinct().ToList();
        values.Should().ContainSingle("both rows carry the same surcharge, so an eq returns both");
        var value = values[0];
        var expected = Corpus.IdsOf(wrapped);

        var client = await Conformance();

        foreach (var operand in new[] { value, $"\"{value}\"" })
            (await client.SendAsync(Corpus.Conformance, Page($$"""{ "addon.zuschlag": { "eq": {{operand}} } }"""))).ShouldHaveIds(expected, operand);

        var lower = (decimal.Parse(value, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture);
        (await client.SendAsync(Corpus.Conformance, Page($$"""{ "addon.zuschlag": { "gte": {{lower}} } }"""))).ShouldHaveIds(expected);

        var leaf = await client.SendAsync(Corpus.Conformance, $$"""[{ "match": { "addon.zuschlag._v": { "eq": {{value}} } } }, { "page": { "limit": 1 } }]""");
        leaf.ShouldRefuse("NOT_FILTERABLE", 400);
        leaf.ErrorCodes.Should().Equal(["NOT_FILTERABLE"]);

        var projected = await client.SendAsync(Corpus.Conformance, """[{ "project": { "id": 1, "addon.zuschlag": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""");
        projected.ShouldBeOk();
        var first = projected.Items[0]!["addon"]!["zuschlag"]!;
        first.GetValueKind().Should().Be(JsonValueKind.Number, "the driver's { _t, _v } wrapper never reaches the wire: a decimal travels as a number in canonical digits, like every decimal");
        first.ToJsonString().Should().Be(value);

        // An aggregate over the defined decimal key reads the wrapped value: it sums what the rows show.
        var summed = await client.SendAsync(Corpus.Conformance, """[{ "group": { "by": [], "fields": { "total": { "sum": "addon.zuschlag" }, "top": { "max": "addon.zuschlag" } } } }]""");
        var total = wrapped.Sum(row => decimal.Parse(Order.NumberText(row.Stored["Addon"]["zuschlag"]["_v"]), CultureInfo.InvariantCulture));

        summed.ShouldBeOk();
        summed.Items[0]!["total"]!.GetValueKind().Should().Be(JsonValueKind.Number, "an aggregate over a decimal is a number, as the member is");
        decimal.Parse(summed.Items[0]!["total"]!.ToJsonString(), CultureInfo.InvariantCulture).Should().Be(total, "a sum over wrapped decimals is the sum of the values, exact");
        summed.Items[0]!["top"]!.ToJsonString().Should().Be(value);
    }

    [Fact]
    public async Task T17_T19_a_closed_value_list_admits_its_values_and_refuses_one_off_it_naming_the_list_for_a_literal_an_element_and_a_variable()
    {
        var definition = Corpus.Entity(Corpus.Conformance).AddonDefinitions[Org.A].Single(def => def.Path == "status");
        var allowed = definition.Values!.Select(entry => entry.Value).ToList();
        allowed.Should().Equal(["open", "closed", "void"]);
        var client = await Conformance();

        foreach (var value in allowed)
        {
            var expected = Where("status", stored => stored is BsonString text && text.Value == value);
            (await client.SendAsync(Corpus.Conformance, Page($$"""{ "addon.status": { "eq": "{{value}}" } }"""))).ShouldHaveIds(expected, value).ShouldHaveTotal(expected.Count);
        }

        Where("status", stored => stored is BsonString { Value: "void" }).Should().BeEmpty("a list value no row carries is still admitted");

        var literal = await client.SendAsync(Corpus.Conformance, """[{ "match": { "addon.status": { "eq": "draft" } } }, { "page": { "limit": 1 } }]""");
        literal.ShouldRefuse("UNKNOWN_ENUM_MEMBER", 400)["message"]!.GetValue<string>().Should().Contain(string.Join(", ", allowed));
        literal.ErrorCodes.Should().Equal(["UNKNOWN_ENUM_MEMBER"]);

        var element = await client.SendAsync(Corpus.Conformance, """[{ "match": { "addon.status": { "in": ["open", "nope"] } } }, { "page": { "limit": 1 } }]""");
        element.ErrorCodes.Should().Equal(["UNKNOWN_ENUM_MEMBER"]);
        element.Text.Should().Contain("addon.status[1]");

        var closed = Where("status", stored => stored is BsonString { Value: "closed" });
        var variable = """[{ "match": { "addon.status": { "eq": { "$var": "wanted" } } } }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""";
        (await client.SendAsync(Corpus.Conformance, variable, """{ "wanted": "closed" }""")).ShouldHaveIds(closed);
        (await client.SendAsync(Corpus.Conformance, variable, """{ "wanted": "nope" }""")).ShouldRefuse("UNKNOWN_ENUM_MEMBER", 400);
    }

    [Fact]
    public async Task T18_the_closed_list_gates_eq_neq_in_nin_only_a_range_a_substring_and_exists_answer_rows()
    {
        var client = await Conformance();
        var probes = new (string Name, string Condition, Func<BsonValue?, bool> Holds)[]
        {
            ("gt", """{ "addon.status": { "gt": "m" } }""", value => value is BsonString text && Order.CompareCollated(text.Value, "m") > 0),
            ("contains", """{ "addon.status": { "contains": "pe" } }""", value => value is BsonString text && Order.FoldCi(text.Value).Contains("pe", StringComparison.Ordinal)),
            ("exists", """{ "addon.status": { "exists": true } }""", value => value is not null),
        };

        foreach (var (name, condition, holds) in probes)
        {
            var expected = Where("status", holds);
            expected.Should().NotBeEmpty(name).And.HaveCountLessThan(Rows.Count, name);

            (await client.SendAsync(Corpus.Conformance, Page(condition))).ShouldHaveIds(expected, name).ShouldHaveTotal(expected.Count);
        }
    }

    [Fact]
    public async Task T5_T6_T7_an_object_definition_an_undefined_key_and_a_retired_one_are_refused_alike_and_projectable()
    {
        var client = await Conformance();
        var transport = await Lab.ClientAsync(LabService.Transport);
        var messages = new List<string>();

        foreach (var (who, entity, path) in new[] { (client, Corpus.Conformance, "addon.vincario"), (client, Corpus.Conformance, "addon.looseKey"), (transport, Corpus.Shipment, "addon.retiredKey") })
        {
            var answer = await who.SendAsync(entity, $$"""[{ "match": { "{{path}}": { "eq": "x" } } }, { "page": { "limit": 1 } }]""");

            answer.ShouldRefuse("NOT_FILTERABLE", 400);
            answer.ErrorCodes.Should().Equal(["NOT_FILTERABLE"]);
            messages.Add(answer.Errors[0]["message"]!.GetValue<string>().Replace(path, "<path>", StringComparison.Ordinal));
        }

        messages.Distinct().Should().ContainSingle("J3: three causes, one message");
        Corpus.Entity(Corpus.Shipment).AddonDefinitions[Org.A].Should().Contain(def => def.Path == "retiredKey" && def.Retired, "the key is retired, not absent");

        var projected = await client.SendAsync(Corpus.Conformance, """[{ "project": { "id": 1, "addon.vincario": 1, "addon.looseKey": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""");
        projected.ShouldHaveIds(Corpus.AllIds(Corpus.Conformance));
        projected.Values("addon.looseKey").Select(value => value?.GetValue<string>())
            .Should().Equal(Rows.Select(row => Corpus.Text(row, "addon.looseKey")));
        Canonical(projected.Items[0]!["addon"]!["vincario"]).Should().Be(Canonical(Wire(Corpus.Row(Corpus.Conformance, "c-alpha").Stored["Addon"]["vincario"])), "an object-valued key is projected whole, as the value the wrapper holds");
    }

    [Fact]
    public async Task T6_exists_reaches_an_undefined_key_although_a_filter_cannot()
    {
        var expected = Where("looseKey", value => value is not null);
        expected.Should().HaveCount(2);

        (await (await Conformance()).SendAsync(Corpus.Conformance, Page("""{ "addon.looseKey": { "exists": true } }"""))).ShouldHaveIds(expected).ShouldHaveTotal(2);
    }

    [Fact]
    public async Task T11_a_dollar_segment_and_an_empty_segment_under_the_bag_are_refused_once_as_invalid_paths()
    {
        var client = await Conformance();

        foreach (var path in new[] { "addon.$bad", "addon..x" })
        {
            var answer = await client.SendAsync(Corpus.Conformance, $$"""[{ "match": { "{{path}}": { "eq": 1 } } }, { "page": { "limit": 1 } }]""");

            answer.StatusCode.Should().Be(400, answer.ToString());
            answer.ErrorCodes.Should().Equal(["INVALID_PATH"]);
            answer.Errors[0]["message"]!.GetValue<string>().Should().Contain(path.Contains('$') ? "segment starting with '$'" : "empty segment");
        }
    }

    [Fact]
    public async Task T22_T23_a_sort_on_a_defined_key_is_best_effort_and_one_on_an_undefined_or_object_key_is_refused()
    {
        var client = await Conformance();
        var order = Corpus.SortedIds(Corpus.Conformance, "addon.status");

        var allowed = await client.SendAsync(Corpus.Conformance, """[{ "sort": [{ "addon.status": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 10 } }]""");
        allowed.ShouldHaveIds(order);
        allowed.DiagnosticCodes.Should().Equal(["SORT_ON_ADDON"]);

        foreach (var path in new[] { "addon.looseKey", "addon.vincario" })
        {
            var refused = await client.SendAsync(Corpus.Conformance, $$"""[{ "sort": [{ "{{path}}": "asc" }] }, { "page": { "limit": 1 } }]""");
            refused.ShouldRefuse("NOT_SORTABLE", 400);
            refused.ErrorCodes.Should().Equal(["NOT_SORTABLE"]);
        }
    }

    [Fact]
    public async Task T24_the_bag_travels_verbatim_keys_as_stored_wrappers_unwrapped_a_null_kept_and_an_empty_bag_an_empty_object()
    {
        var answer = await (await Conformance()).SendAsync(Corpus.Conformance, """[{ "project": { "id": 1, "addon": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""");

        answer.ShouldHaveIds(Corpus.AllIds(Corpus.Conformance));
        answer.Values("addon").Select(Canonical).Should().Equal(Rows.Select(row => Canonical(Wire(row.Stored["Addon"]))));
        answer.Items[0]!["addon"]!.AsObject().Select(member => member.Key).Should().Contain("Ablieferbelege vorhanden", "no camel-casing below the bag");
        Canonical(answer.Items[2]!["addon"]).Should().Be("{}", "the row with no bag entries");

        var rich = Corpus.Row(Corpus.Shipment, "addon-rich");
        var shipment = await (await Lab.ClientAsync(LabService.Transport)).SendAsync(Corpus.Shipment, """[{ "match": { "addon.nullKey": { "exists": true } } }, { "project": { "id": 1, "addon": 1 } }, { "page": { "limit": 5 } }]""");
        shipment.ShouldHaveIds([rich.Id]);
        Json.Has(shipment.Items[0], "addon.nullKey").Should().BeTrue();
        shipment.Items[0]!["addon"]!["nullKey"].Should().BeNull("a null inside a bag stays null");
    }

    [Fact]
    public async Task T34_the_bag_exists_only_at_the_root_a_nested_addon_is_a_plain_dictionary()
    {
        var transport = await Lab.ClientAsync(LabService.Transport);

        var nested = await transport.SendAsync(Corpus.Shipment, """[{ "match": { "items.addon.shiftModel": { "eq": "late" } } }, { "page": { "limit": 1 } }]""");
        nested.ShouldRefuse("NOT_FILTERABLE", 400);
        nested.ErrorCodes.Should().Equal(["NOT_FILTERABLE"]);

        var root = Corpus.SortedIds(Corpus.Shipment, "id", filter: row => Corpus.Text(row, "addon.shiftModel") == "late");
        root.Should().NotBeEmpty();
        (await transport.SendAsync(Corpus.Shipment, Page("""{ "addon.shiftModel": { "eq": "late" } }"""))).ShouldHaveIds(root);

        var withText = Corpus.Sorted(Corpus.Shipment, [("id", false)], filter: row => Corpus.Texts(row, "items.text").Count > 0).Take(2).ToList();
        var projected = await transport.SendAsync(Corpus.Shipment, """[{ "match": { "items.text": { "exists": true } } }, { "project": { "id": 1, "items.addon": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 2 } }]""");
        projected.ShouldHaveIds(Corpus.IdsOf(withText));
        projected.Values("items").Select(items => items!.AsArray().Select(Canonical).ToList())
            .Should().BeEquivalentTo(withText.Select(row => Corpus.Elements(row, "items").Select(_ => "{}").ToList()), options => options.WithStrictOrdering(), "storage holds no nested bag, so each element projects empty");
    }

    [Fact]
    public async Task T12_T15_T21_T30_the_tolerant_pair_a_guid_as_string_a_double_an_int_and_a_long_above_two_to_the_53()
    {
        var rich = Corpus.Row(Corpus.Shipment, "addon-rich");
        var transport = await Lab.ClientAsync(LabService.Transport);

        async Task Finds(string condition, IReadOnlyList<Guid> expected) =>
            (await transport.SendAsync(Corpus.Shipment, Page(condition))).ShouldHaveIds(expected, condition).ShouldHaveTotal(expected.Count);

        // T30 and T12: a double key and an int key match the stored number and its string spelling.
        foreach (var path in new[] { "addon.ratio", "addon.vincario._v.axles" })
        {
            var value = Order.NumberText(Corpus.ValueAt(rich, path)!);
            var expected = Corpus.SortedIds(Corpus.Shipment, "id", filter: row => Corpus.Number(row, path) is { } number && Order.CompareDecimal(number, value) == 0);
            expected.Should().Equal([rich.Id]);

            await Finds($$"""{ "{{path}}": { "eq": {{value}} } }""", expected);
            await Finds($$"""{ "{{path}}": { "eq": "{{value}}" } }""", expected);
        }

        // T15: a GUID-looking value is a string in a bag; the canonical spelling matches.
        var guid = Corpus.Text(rich, "addon.legacyRef")!;
        Guid.TryParse(guid, out _).Should().BeTrue();
        await Finds($$"""{ "addon.legacyRef": { "eq": "{{guid}}" } }""", [rich.Id]);

        // T21: no closed list on that key, so a value no bag holds is admitted and finds nothing.
        await Finds("""{ "addon.legacyRef": { "eq": "not-a-value-any-row-holds" } }""", []);

        // T12, the long: the digit string and the exact number both match; the number a JavaScript
        // caller would send after its own double rounding (…992) matches nothing (transport T1).
        var tour = Corpus.ValueAt(rich, "addon.tourCount")!.AsInt64;
        tour.Should().Be(9007199254740993L);
        await Finds($$"""{ "addon.tourCount": { "eq": "{{tour}}" } }""", [rich.Id]);
        await Finds($$"""{ "addon.tourCount": { "eq": {{tour}} } }""", [rich.Id]);
        await Finds($$"""{ "addon.tourCount": { "eq": {{(long)(double)tour}} } }""", []);
    }

    [Fact]
    public async Task T27_a_lookup_childs_and_a_resolve_targets_own_definitions_are_read_the_same_way()
    {
        const string key = "shiftModel";
        Corpus.Entity(Corpus.Vehicle).AddonDefinitions[Org.A].Should().Contain(def => def.Path == key && !def.Retired);
        var carrying = Corpus.Rows(Corpus.Vehicle).Where(row => Corpus.Text(row, "addon." + key) is not null).ToList();
        var value = Corpus.Text(carrying[0], "addon." + key)!;
        var matching = carrying.Where(row => Corpus.Text(row, "addon." + key) == value).Select(row => row.Id).ToHashSet();
        matching.Should().NotBeEmpty().And.HaveCountLessThan(Corpus.Counts(Corpus.Vehicle).A);

        var fleet = await Lab.ClientAsync(LabService.Fleet);

        var departments = Corpus.Sorted(Corpus.Department, [("id", false)]);
        var expectedChildren = departments.Select(department => Corpus.Rows(Corpus.Vehicle)
            .Where(vehicle => Corpus.GuidAt(vehicle, "department.id") == department.Id && matching.Contains(vehicle.Id)).Select(vehicle => vehicle.Id).Order().ToList()).ToList();
        expectedChildren.SelectMany(children => children).Should().NotBeEmpty();

        var lookup = await fleet.SendAsync(Corpus.Department, $$"""
            [ { "lookup": { "from": "fleet.vehicle", "path": "department.id", "as": "v", "limit": 100, "filter": { "addon.{{key}}": { "eq": "{{value}}" } } } },
              { "project": { "id": 1, "v.id": 1, "v.matchCode": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
            """);

        lookup.ShouldHaveIds(departments.Select(department => department.Id));
        lookup.Values("v").Select(children => children!.AsArray().Select(child => Guid.Parse(child!["id"]!.GetValue<string>())).Order().ToList())
            .Should().BeEquivalentTo(expectedChildren, options => options.WithStrictOrdering());

        var equipment = Corpus.Sorted(Corpus.Equipment, [("id", false)]);
        var expectedTargets = equipment.Select(row => Corpus.GuidAt(row, "vehicle.id") is { } id && matching.Contains(id) ? id : (Guid?)null).ToList();
        expectedTargets.Should().Contain(id => id != null).And.Contain(id => id == null);

        var resolve = await fleet.SendAsync(Corpus.Equipment, $$"""
            [ { "resolve": { "path": "vehicle.id", "as": "veh", "filter": { "addon.{{key}}": { "eq": "{{value}}" } } } },
              { "project": { "id": 1, "veh.id": 1, "veh.matchCode": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
            """);

        resolve.ShouldHaveIds(equipment.Select(row => row.Id));
        resolve.Values("veh.id").Select(id => id is null ? (Guid?)null : Guid.Parse(id.GetValue<string>())).Should().Equal(expectedTargets);
    }

    [Fact]
    public async Task T25_the_engine_reads_the_definitions_on_every_request_a_definition_written_or_retired_takes_effect_on_the_next()
    {
        // The engine keeps no definition cache of its own: it asks the host's source on every
        // request (a host's source may cache; the lab's does not). The legacy case could only
        // observe the host's 30 s TTL.
        await using var fleet = await CorpusFleet.CreateAsync("case-t25", seed: [LabService.Staff]);
        var staff = fleet.Client(LabService.Staff);
        var definitions = (await fleet.Fleet.DatabaseAsync(LabService.Staff)).GetCollection<BsonDocument>(TestAddonSource.Collection);
        var expected = Corpus.SortedIds(Corpus.Employee, "id", filter: row => Corpus.Text(row, "addon.undefinedKey") == "opaque");
        expected.Should().NotBeEmpty();
        var pipeline = Page("""{ "addon.undefinedKey": { "eq": "opaque" } }""");
        var id = Ids.Of(Spaces.AddonDefinition, Org.A, 950);

        (await staff.SendAsync(Corpus.Employee, pipeline)).ShouldRefuse("NOT_FILTERABLE", 400);

        await definitions.InsertOneAsync(TestAddonSource.Document(id, Org.A.Id(), Corpus.Employee, "undefinedKey", "string", "Now defined"));
        (await staff.SendAsync(Corpus.Employee, pipeline)).ShouldHaveIds(expected);

        await definitions.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(id, GuidRepresentation.Standard)), Builders<BsonDocument>.Update.Set("Retired", true));
        (await staff.SendAsync(Corpus.Employee, pipeline)).ShouldRefuse("NOT_FILTERABLE", 400);
    }

    [Fact]
    public async Task T33_a_list_under_the_bag_is_projected_verbatim_and_is_not_filterable_as_an_undefined_key()
    {
        // No corpus bag holds a list, so the case seeds one of its own.
        await using var fleet = await CorpusFleet.CreateAsync("case-t33", seed: [], extra: async lab =>
        {
            var database = await lab.DatabaseAsync(LabService.Staff);
            await database.GetCollection<BsonDocument>("employee").InsertOneAsync(new BsonDocument
            {
                ["_id"] = new BsonBinaryData(Ids.Of(Spaces.Employee, Org.A, 900), GuidRepresentation.Standard),
                ["OrganizationId"] = new BsonBinaryData(Org.A.Id(), GuidRepresentation.Standard),
                ["Addon"] = new BsonDocument { ["codes"] = new BsonArray { "a", "b" } },
            });
        });
        var staff = fleet.Client(LabService.Staff);

        var projected = await staff.SendAsync(Corpus.Employee, """[{ "project": { "id": 1, "addon": 1 } }, { "page": { "limit": 5 } }]""");
        projected.ShouldHaveIds([Ids.Of(Spaces.Employee, Org.A, 900)]);
        Canonical(projected.Items[0]!["addon"]).Should().Be("""{"codes":["a","b"]}""");

        (await staff.SendAsync(Corpus.Employee, """[{ "match": { "addon.codes": { "eq": "a" } } }, { "page": { "limit": 5 } }]""")).ShouldRefuse("NOT_FILTERABLE", 400);
    }

    // ── a defined key is compared by its value ─────────────────────────────────────────────

    private const string DecimalType = Addons.DecimalDiscriminator;
    private const string DictionaryType = Addons.DictionaryDiscriminator;

    private static readonly Guid Reference = Guid.Parse("7b7e2a4e-0000-4000-8000-000000000042");

    /// <summary>
    /// The keys the stored-forms bags are defined with; <c>o.x</c> lies inside a dictionary the
    /// serializer may have wrapped, which the definition does not name.
    /// </summary>
    private static readonly (string Path, string Kind)[] StoredFormKeys =
    [
        ("weight", "double"), ("amount", "decimal"), ("o.x", "int"), ("day", "date"), ("flag", "bool"), ("code", "string"), ("cnt", "long"), ("ref", "guid"),
    ];

    /// <summary>
    /// One key per kind in every form a writer leaves in a bag: r1 and r2 as the service's own
    /// serializer writes them (a decimal and a dictionary wrapped in <c>{ _t, _v }</c>), r3 to r5 as
    /// another client writes them (plain, other numeric types, everything as text), r6 an empty bag,
    /// and r7 values no kind converts (text that is no number, a GUID under a text key).
    /// </summary>
    private static List<BsonDocument> StoredForms()
    {
        static BsonDocument Wrapped(string type, BsonValue value) => new() { ["_t"] = type, ["_v"] = value };
        static BsonDateTime At(string iso) => new(DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
        static BsonDecimal128 Money(string digits) => new(Decimal128.Parse(digits));

        return
        [
            Row(1, new BsonDocument { ["weight"] = Wrapped(DecimalType, Money("3.5")), ["amount"] = Wrapped(DecimalType, Money("3.5")), ["o"] = Wrapped(DictionaryType, new BsonDocument("x", new BsonInt64(5))), ["day"] = At("2026-10-25T23:00:00Z"), ["flag"] = true, ["code"] = "A-1", ["cnt"] = new BsonInt64(9007199254740993), ["ref"] = new BsonBinaryData(Reference, GuidRepresentation.Standard) }),
            Row(2, new BsonDocument { ["weight"] = new BsonInt64(3), ["amount"] = new BsonInt64(3), ["o"] = Wrapped(DictionaryType, new BsonDocument("x", new BsonInt64(7))), ["day"] = At("2026-10-26T23:00:00Z"), ["flag"] = false, ["code"] = "b-2", ["cnt"] = new BsonInt64(3), ["ref"] = Reference.ToString() }),
            Row(3, new BsonDocument { ["weight"] = 3.5, ["amount"] = 3.5, ["o"] = new BsonDocument("x", 5), ["day"] = At("2026-10-26T00:00:00Z"), ["flag"] = "true", ["code"] = 12, ["cnt"] = 3 }),
            Row(4, new BsonDocument { ["weight"] = "3.5", ["amount"] = "3.5", ["o"] = new BsonDocument("x", "5"), ["day"] = "2026-10-26", ["flag"] = "false", ["code"] = "a-1", ["cnt"] = "9007199254740993" }),
            Row(5, new BsonDocument { ["weight"] = 2.25, ["amount"] = Money("2.25"), ["o"] = new BsonDocument("x", 5.0), ["day"] = At("2026-10-26T13:00:00Z"), ["flag"] = 1, ["code"] = BsonNull.Value, ["cnt"] = 2.0 }),
            Row(6, []),
            Row(7, new BsonDocument { ["weight"] = "n/a", ["amount"] = "", ["flag"] = 0, ["code"] = new BsonBinaryData(Reference, GuidRepresentation.Standard), ["cnt"] = "3.0" }),
        ];

        static BsonDocument Row(int n, BsonDocument bag) => new()
        {
            ["_id"] = new BsonBinaryData(StoredForm(n), GuidRepresentation.Standard),
            ["OrganizationId"] = new BsonBinaryData(Org.A.Id(), GuidRepresentation.Standard),
            ["MatchCode"] = "r" + n,
            ["Addon"] = bag,
        };
    }

    private static Guid StoredForm(int n) => Ids.Of(Spaces.Employee, Org.A, 910 + n);

    private static IReadOnlyList<Guid> Forms(params int[] rows) => rows.Select(StoredForm).ToList();

    /// <summary>A fleet whose <c>staff.employee</c> holds the stored-forms rows alone, with their definitions.</summary>
    private static Task<CorpusFleet> StoredFormsFleet(string purpose) =>
        CorpusFleet.CreateAsync(purpose, seed: [], extra: async lab =>
        {
            var database = await lab.DatabaseAsync(LabService.Staff);
            var n = 0;

            await database.GetCollection<BsonDocument>(TestAddonSource.Collection).InsertManyAsync(StoredFormKeys.Select(key =>
                TestAddonSource.Document(Ids.Of(Spaces.AddonDefinition, Org.A, 960 + ++n), Org.A.Id(), Corpus.Employee, key.Path, key.Kind, key.Path)));
            await database.GetCollection<BsonDocument>("employee").InsertManyAsync(StoredForms());
        });

    private static string Sorted(string match, string extra = "") =>
        $$"""[{ "match": {{match}} }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, {{(extra.Length > 0 ? extra + ", " : "")}}{ "page": { "limit": 50 } }]""";

    [Fact]
    public async Task T35_a_defined_key_is_compared_by_its_value_read_wrapped_or_plain_and_converted_to_its_kind_whatever_form_its_writer_stored()
    {
        await using var fleet = await StoredFormsFleet("case-t35");
        var staff = fleet.Client(LabService.Staff);

        var cases = new (string Condition, IReadOnlyList<Guid> Expected)[]
        {
            // A number in any numeric type, as text, or wrapped as a decimal.
            ("""{ "addon.weight": { "eq": 3.5 } }""", Forms(1, 3, 4)),
            ("""{ "addon.weight": { "gt": 3 } }""", Forms(1, 3, 4)),
            ("""{ "addon.weight": { "lt": 3 } }""", Forms(5)),
            ("""{ "addon.amount": { "eq": "3.5" } }""", Forms(1, 3, 4)),
            ("""{ "addon.amount": { "neq": 3.5 } }""", Forms(2, 5, 6, 7)),
            ("""{ "addon.amount": { "gte": 3 } }""", Forms(1, 2, 3, 4)),
            ("""{ "addon.amount": { "lt": 3 } }""", Forms(5)),
            ("""{ "addon.amount": { "in": [3, 3.5] } }""", Forms(1, 2, 3, 4)),
            ("""{ "addon.amount": { "nin": [3.5] } }""", Forms(2, 5, 6, 7)),
            // Below a dictionary the serializer wrapped or not.
            ("""{ "addon.o.x": { "eq": 5 } }""", Forms(1, 3, 4, 5)),
            // A long beyond 2^53 exactly, as a number or as its digits; "3.0" holds no integer.
            ("""{ "addon.cnt": { "eq": 9007199254740993 } }""", Forms(1, 4)),
            ("""{ "addon.cnt": { "eq": "3" } }""", Forms(2, 3)),
            // The database's conversion to a boolean: a number unless zero, and any text, is true.
            ("""{ "addon.flag": { "eq": true } }""", Forms(1, 3, 4, 5)),
            ("""{ "addon.flag": { "eq": false } }""", Forms(2, 7)),
            // A number under a text key is its text; a GUID there is no text at all.
            ("""{ "addon.code": { "eq": "12" } }""", Forms(3)),
            ("""{ "addon.code": { "contains": "1" } }""", Forms(1, 3, 4)),
            // A GUID as binary or as its text.
            ($$"""{ "addon.ref": { "eq": "{{Reference}}" } }""", Forms(1, 2)),
            ($$"""{ "addon.ref": { "neq": "{{Reference}}" } }""", Forms(3, 4, 5, 6, 7)),
        };

        foreach (var (condition, expected) in cases)
        {
            var answer = await staff.SendAsync(Corpus.Employee, Sorted(condition));

            answer.ShouldHaveIds(expected, condition).ShouldHaveNoDiagnostics(condition);
        }
    }

    [Fact]
    public async Task T36_a_value_that_does_not_convert_counts_as_empty_and_whether_the_key_is_there_is_asked_of_it_as_stored()
    {
        await using var fleet = await StoredFormsFleet("case-t36");
        var staff = fleet.Client(LabService.Staff);

        async Task Finds(string condition, IReadOnlyList<Guid> expected) =>
            (await staff.SendAsync(Corpus.Employee, Sorted(condition))).ShouldHaveIds(expected, condition);

        // "n/a" is no number: empty, like the missing key of the empty bag.
        await Finds("""{ "addon.weight": { "eq": null } }""", Forms(6, 7));
        await Finds("""{ "addon.weight": { "neq": null } }""", Forms(1, 2, 3, 4, 5));
        await Finds("""{ "addon.weight": { "lt": 100 } }""", Forms(1, 2, 3, 4, 5));
        await Finds("""{ "addon.weight": { "exists": true } }""", Forms(1, 2, 3, 4, 5, 7));
        await Finds("""{ "addon.weight": { "exists": false } }""", Forms(6));

        // A text key holding a null, nothing, or a GUID.
        await Finds("""{ "addon.code": { "eq": null } }""", Forms(5, 6, 7));
        await Finds("""{ "addon.code": { "nin": ["A-1", "b-2"] } }""", Forms(3, 5, 6, 7));

        // Below a dictionary: there whether it was wrapped or not.
        await Finds("""{ "addon.o.x": { "exists": true } }""", Forms(1, 2, 3, 4, 5));
    }

    [Fact]
    public async Task T37_a_text_key_folds_case_and_accents_under_the_collation_unless_the_comparison_opts_out_and_a_prefix_is_the_collated_range()
    {
        await using var fleet = await StoredFormsFleet("case-t37");
        var staff = fleet.Client(LabService.Staff);

        async Task Finds(string condition, IReadOnlyList<Guid> expected) =>
            (await staff.SendAsync(Corpus.Employee, Sorted(condition))).ShouldHaveIds(expected, condition);

        await Finds("""{ "addon.code": { "eq": "a-1" } }""", Forms(1, 4));
        await Finds("""{ "addon.code": { "in": ["A-1", "B-2"] } }""", Forms(1, 2, 4));
        await Finds("""{ "addon.code": { "startsWith": "a" } }""", Forms(1, 4));
        await Finds("""{ "addon.code": { "endsWith": "-1" } }""", Forms(1, 4));
        await Finds("""{ "addon.code": { "gt": "a-1" } }""", Forms(2));

        // An exact comparison beside one that folds, which puts the collation on the whole aggregate.
        await Finds("""{ "addon.code": { "eq": "a-1", "options": { "caseSensitive": true } }, "matchCode": { "startsWith": "R" } }""", Forms(4));
        await Finds("""{ "addon.code": { "in": ["A-1", "b-2"], "options": { "caseSensitive": true } }, "matchCode": { "startsWith": "R" } }""", Forms(1, 2));
        await Finds("""{ "addon.code": { "eq": "a-1", "options": { "caseSensitive": true } } }""", Forms(4));
    }

    [Fact]
    public async Task T38_a_sort_and_an_aggregate_read_the_value_a_page_continues_after_it_and_a_date_key_is_an_instant()
    {
        await using var fleet = await StoredFormsFleet("case-t38");
        var staff = fleet.Client(LabService.Staff);

        // Empty first ascending, the rows that tie by key.
        var cases = new (string Path, string Direction, IReadOnlyList<Guid> Expected)[]
        {
            ("addon.weight", "asc", Forms(6, 7, 5, 2, 1, 3, 4)),
            ("addon.amount", "asc", Forms(6, 7, 5, 2, 1, 3, 4)),
            ("addon.cnt", "asc", Forms(6, 7, 5, 2, 3, 1, 4)),
            ("addon.day", "asc", Forms(6, 7, 1, 3, 4, 5, 2)),
            ("addon.amount", "desc", Forms(1, 3, 4, 2, 5, 6, 7)),
        };

        foreach (var (path, direction, expected) in cases)
        {
            var whole = await staff.SendAsync(Corpus.Employee, $$"""[{ "sort": [{ "{{path}}": "{{direction}}" }] }, { "project": { "id": 1 } }, { "page": { "limit": 50 } }]""");

            whole.ShouldHaveIds(expected, $"{path} {direction}").DiagnosticCodes.Should().Equal(["SORT_ON_ADDON"]);

            // Two rows a page: every cursor continues after the value its last row was ordered by.
            var walked = new List<Guid>();
            string? cursor = null;

            do
            {
                var page = await staff.SendAsync(Corpus.Employee, $$"""[{ "sort": [{ "{{path}}": "{{direction}}" }] }, { "project": { "id": 1 } }, { "page": { "limit": 2{{(cursor is null ? "" : $", \"cursor\": \"{cursor}\"")}} } }]""");

                page.ShouldBeOk();
                walked.AddRange(page.Ids());
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            walked.Should().Equal(expected, $"{path} {direction} by pages of two");
        }

        // The sum is of the converted values: 3.5 + 3 + 3.5 + 3.5 + 2.25.
        var summed = await staff.SendAsync(Corpus.Employee, """[{ "group": { "by": [], "fields": { "total": { "sum": "addon.amount" }, "top": { "max": "addon.weight" } } } }]""");

        summed.ShouldBeOk();
        decimal.Parse(summed.Items[0]!["total"]!.ToJsonString(), CultureInfo.InvariantCulture).Should().Be(15.75m);
        summed.Items[0]!["top"]!.GetValue<double>().Should().Be(3.5);

        // A date key holds the instant its writer stored; a day of the caller's calendar is the instants it spans.
        var day = await staff.SendAsync(Corpus.Employee, Sorted("""{ "addon.day": { "gte": "2026-10-26T00:00:00+01:00", "lt": "2026-10-27T00:00:00+01:00" } }"""));
        day.ShouldHaveIds(Forms(1, 3, 4, 5));
        (await staff.SendAsync(Corpus.Employee, Sorted("""{ "addon.day": { "eq": "2026-10-26" } }"""))).ShouldRefuse("INVALID_OPERAND", 400);

        var projected = await staff.SendAsync(Corpus.Employee, """[{ "project": { "id": 1, "addon.day": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""");
        projected.Values("addon.day").Take(4).Select(value => value?.GetValue<string>())
            .Should().Equal(["2026-10-25T23:00:00Z", "2026-10-26T23:00:00Z", "2026-10-26T00:00:00Z", "2026-10-26"], "a row shows what is stored");
    }

    [Fact]
    public async Task T39_a_looked_up_childs_key_filtered_after_the_lookup_is_matched_as_stored_in_the_forms_its_kind_is_written_in()
    {
        var rich = Corpus.Row(Corpus.Vehicle, "addon-rich");
        var tour = Corpus.ValueAt(rich, "addon.tourCount")!.AsInt64;
        var departments = Corpus.Sorted(Corpus.Department, [("id", false)]);
        var expected = departments.Where(department => Corpus.GuidAt(rich, "department.id") == department.Id).Select(department => department.Id).ToList();
        expected.Should().ContainSingle("the rich vehicle belongs to one department");

        var fleet = await Lab.ClientAsync(LabService.Fleet);

        // Of one element of the alias no expression can be asked: the number and its digits are both matched.
        foreach (var operand in new[] { tour.ToString(CultureInfo.InvariantCulture), $"\"{tour}\"" })
        {
            var answer = await fleet.SendAsync(Corpus.Department, $$"""
                [ { "lookup": { "from": "fleet.vehicle", "path": "department.id", "as": "v", "limit": 100 } },
                  { "match": { "v.addon.tourCount": { "eq": {{operand}} } } },
                  { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 50 } } ]
                """);

            answer.ShouldHaveIds(expected, operand);
        }

        // The rich bag's weight is a decimal the serializer wrapped: matched under the wrapper too.
        var weight = Order.NumberText(rich.Stored["Addon"]["weight"]["_v"]);
        var wrapped = await fleet.SendAsync(Corpus.Department, $$"""
            [ { "lookup": { "from": "fleet.vehicle", "path": "department.id", "as": "v", "limit": 100 } },
              { "match": { "v.addon.weight": { "eq": {{weight}} } } },
              { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 50 } } ]
            """);

        wrapped.ShouldHaveIds(expected, "a wrapped decimal of one element");
    }

    [Fact]
    public async Task T40_a_page_by_a_guid_key_continues_across_its_binary_and_text_values_and_a_pattern_finds_no_empty_value()
    {
        await using var fleet = await StoredFormsFleet("case-t40");
        var staff = fleet.Client(LabService.Staff);

        // A GUID is not converted: the sort orders the empty values, then the text, then the binary.
        var expected = Forms(3, 4, 5, 6, 7, 2, 1);
        (await staff.SendAsync(Corpus.Employee, """[{ "sort": [{ "addon.ref": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 50 } }]""")).ShouldHaveIds(expected);

        var walked = new List<Guid>();
        string? cursor = null;

        do
        {
            var page = await staff.SendAsync(Corpus.Employee, $$"""[{ "sort": [{ "addon.ref": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 2{{(cursor is null ? "" : $", \"cursor\": \"{cursor}\"")}} } }]""");

            page.ShouldBeOk();
            walked.AddRange(page.Ids());
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        walked.Should().Equal(expected, "every row once, by pages of two");

        // A null, a missing key and a GUID under a text key hold no text: no pattern matches them,
        // the empty one included, and a negated one keeps them, as for a missing member.
        async Task Finds(string condition, IReadOnlyList<Guid> rows) =>
            (await staff.SendAsync(Corpus.Employee, Sorted(condition))).ShouldHaveIds(rows, condition);

        const string Folding = """, "matchCode": { "startsWith": "R" }""";

        await Finds("""{ "addon.code": { "regex": "^(?!A)" } }""", Forms(2, 3, 4));
        await Finds("""{ "addon.code": { "regex": "^$" } }""", []);
        await Finds($$"""{ "addon.code": { "eq": "", "options": { "caseSensitive": true } }{{Folding}} }""", []);
        await Finds($$"""{ "addon.code": { "in": ["", "b-2"], "options": { "caseSensitive": true } }{{Folding}} }""", Forms(2));
        await Finds($$"""{ "addon.code": { "neq": "", "options": { "caseSensitive": true } }{{Folding}} }""", Forms(1, 2, 3, 4, 5, 6, 7));
        await Finds($$"""{ "addon.code": { "nin": ["", "b-2"], "options": { "caseSensitive": true } }{{Folding}} }""", Forms(1, 3, 4, 5, 6, 7));
    }

    [Fact]
    public async Task T41_a_group_by_a_defined_key_buckets_its_value_whatever_form_holds_it_also_behind_a_projection()
    {
        await using var fleet = await StoredFormsFleet("case-t41");
        var staff = fleet.Client(LabService.Staff);

        // Wrapped 3.5, the double 3.5 and the text "3.5" are one bucket; "n/a" and nothing are the empty one.
        var weights = await staff.SendAsync(Corpus.Employee, """[{ "group": { "by": [{ "path": "addon.weight", "as": "w" }], "fields": { "n": { "count": true } } } }, { "sort": [{ "w": "asc" }] }]""");

        weights.ShouldBeOk();
        weights.Items.Select(item => (item!["w"]?.ToJsonString() ?? "null", item["n"]!.GetValue<int>())).Should().Equal([("null", 2), ("2.25", 1), ("3", 1), ("3.5", 3)]);

        // A projection the group keeps ahead of it keeps the key wherever it is held.
        var nested = await staff.SendAsync(Corpus.Employee, """[{ "project": { "id": 1, "addon.o.x": 1 } }, { "group": { "by": [{ "path": "addon.o.x", "as": "x" }], "fields": { "n": { "count": true } } } }, { "sort": [{ "x": "asc" }] }]""");

        nested.ShouldBeOk();
        nested.Items.Select(item => (item!["x"]?.ToJsonString() ?? "null", item["n"]!.GetValue<int>())).Should().Equal([("null", 2), ("5", 4), ("7", 1)]);
    }

    [Fact]
    public async Task T42_a_projection_ahead_of_a_large_page_keeps_a_wrapped_key_for_the_sort_and_the_filter_after_it_and_the_row_shows_its_value()
    {
        await using var fleet = await StoredFormsFleet("case-t42");
        var staff = fleet.Variant(LabService.Staff, "large-pages", new Dictionary<string, string?> { ["OxQL:Limits:MaxPageSize"] = "2000" });

        // Above a thousand rows a page orders whole rows no more: the projection runs where it is written.
        var sorted = await staff.SendAsync(Corpus.Employee, """[{ "project": { "id": 1, "addon.o.x": 1 } }, { "sort": [{ "addon.o.x": "asc" }] }, { "page": { "limit": 1500 } }]""");

        sorted.ShouldHaveIds(Forms(6, 7, 1, 3, 4, 5, 2));
        sorted.Items[2]!["addon"]!["o"]!["x"]!.GetValue<long>().Should().Be(5, "the wrapped dictionary reads back as the value it holds");

        var filtered = await staff.SendAsync(Corpus.Employee, """[{ "project": { "id": 1, "addon.o.x": 1 } }, { "match": { "addon.o.x": { "eq": 7 } } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 1500 } }]""");

        filtered.ShouldHaveIds(Forms(2));
    }

    // ── the bag's wire form, computed from storage ─────────────────────────────────────────

    /// <summary>A stored bag value as the wire writes it: a long and a decimal as numbers with every digit (a decimal in canonical digits), documents and arrays recursively.</summary>
    private static JsonNode? Wire(BsonValue value) => value switch
    {
        BsonNull => null,
        BsonString text => JsonValue.Create(text.Value),
        BsonBoolean flag => JsonValue.Create(flag.Value),
        BsonInt32 number => JsonValue.Create(number.Value),
        BsonDouble number => JsonValue.Create(number.Value),
        BsonInt64 number => JsonValue.Create(number.Value),
        // Parsed from its digits, so the node writes them back as they are: no trailing fractional zero, no exponent.
        BsonDecimal128 number => JsonNode.Parse(Order.NumberText(number) is var digits && digits.Contains('.', StringComparison.Ordinal) ? digits.TrimEnd('0').TrimEnd('.') : digits),
        // The driver's wrapper of an object-typed value is never shown: the value it holds is.
        BsonDocument { ElementCount: 2 } wrapper when wrapper.Contains("_t") && wrapper.Contains("_v") => Wire(wrapper["_v"]),
        BsonDocument document => new JsonObject(document.Select(element => KeyValuePair.Create(element.Name, Wire(element.Value)))),
        BsonArray array => new JsonArray(array.Select(Wire).ToArray()),
        _ => throw new NotSupportedException($"No wire form for {value.BsonType} in this suite."),
    };

    /// <summary>A node as text with object members sorted, so two renderings compare regardless of member order.</summary>
    private static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject item => "{" + string.Join(",", item.OrderBy(member => member.Key, StringComparer.Ordinal).Select(member => JsonValue.Create(member.Key).ToJsonString() + ":" + Canonical(member.Value))) + "}",
        JsonArray list => "[" + string.Join(",", list.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };
}
