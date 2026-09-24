using System.Globalization;
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
        $$"""[{ "match": {{match}} }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, {{(extra.Length > 0 ? extra + ", " : "")}}{ "page": { "limit": 50, "includeTotalCount": true } }]""";

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

            answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
            // A decimal key whose bag may hold text announces what an ordered comparison cannot cover.
            answer.DiagnosticCodes.Should().Equal(["DECIMAL_TEXT_EXCLUDED"], answer.ToString());
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
    public async Task T16_a_wrapped_decimal_matches_at_the_key_the_wrapper_leaf_is_not_a_path_and_the_value_travels_back_wrapped()
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
        first["_t"]!.GetValue<string>().Should().Be(Addons.DecimalDiscriminator);
        first["_v"]!.GetValue<string>().Should().Be(value);
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
        Canonical(projected.Items[0]!["addon"]!["vincario"]).Should().Be(Canonical(Wire(Corpus.Row(Corpus.Conformance, "c-alpha").Stored["Addon"]["vincario"])), "an object-valued key is projected whole, wrapper included");
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
    public async Task T24_the_bag_travels_verbatim_keys_as_stored_wrappers_kept_a_null_kept_and_an_empty_bag_an_empty_object()
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
            [ { "lookup": { "from": "fleet.vehicle", "path": "department.id", "as": "v", "select": ["matchCode"], "limit": 100, "filter": { "addon.{{key}}": { "eq": "{{value}}" } } } },
              { "project": { "id": 1, "v": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
            """);

        lookup.ShouldHaveIds(departments.Select(department => department.Id));
        lookup.Values("v").Select(children => children!.AsArray().Select(child => Guid.Parse(child!["id"]!.GetValue<string>())).Order().ToList())
            .Should().BeEquivalentTo(expectedChildren, options => options.WithStrictOrdering());

        var equipment = Corpus.Sorted(Corpus.Equipment, [("id", false)]);
        var expectedTargets = equipment.Select(row => Corpus.GuidAt(row, "vehicle.id") is { } id && matching.Contains(id) ? id : (Guid?)null).ToList();
        expectedTargets.Should().Contain(id => id != null).And.Contain(id => id == null);

        var resolve = await fleet.SendAsync(Corpus.Equipment, $$"""
            [ { "resolve": { "path": "vehicle.id", "as": "veh", "select": ["matchCode"], "filter": { "addon.{{key}}": { "eq": "{{value}}" } } } },
              { "project": { "id": 1, "veh": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
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
        await using var fleet = await CorpusFleet.CreateAsync("b5-t25", seed: [LabService.Staff]);
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
        await using var fleet = await CorpusFleet.CreateAsync("b5-t33", seed: [], extra: async lab =>
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

    // ── the bag's wire form, computed from storage ─────────────────────────────────────────

    /// <summary>A stored bag value as the wire writes it: decimals as canonical text, documents and arrays recursively.</summary>
    private static JsonNode? Wire(BsonValue value) => value switch
    {
        BsonNull => null,
        BsonString text => JsonValue.Create(text.Value),
        BsonBoolean flag => JsonValue.Create(flag.Value),
        BsonInt32 number => JsonValue.Create(number.Value),
        BsonDouble number => JsonValue.Create(number.Value),
        BsonInt64 number => Math.Abs(number.Value) < (1L << 53) ? JsonValue.Create(number.Value) : JsonValue.Create(number.Value.ToString(CultureInfo.InvariantCulture)),
        BsonDecimal128 number => JsonValue.Create(Order.NumberText(number)),
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
