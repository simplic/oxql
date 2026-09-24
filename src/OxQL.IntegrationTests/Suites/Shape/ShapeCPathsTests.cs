using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Shape;

/// <summary>
/// Area C: paths, their grammar, and the shape fold. The engine folds the plan stage by stage to
/// decide what each later stage may name; these cases pin what binds, what is refused and with
/// which code, stage and path. Ported from the legacy <c>shape-c-paths</c> battery, engine half
/// only: the client's own fold (<c>oxqlMeta</c>, the path grammar before send) went to the
/// client's specs.
/// </summary>
[Trait("Category", "Integration")]
public class ShapeCPathsTests
{
    private const string FullPage = """{ "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } }""";

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static Guid ItemsThree => Corpus.IdOf(Corpus.Shipment, "items-three");

    private static Guid AddonRich => Corpus.IdOf(Corpus.Shipment, "addon-rich");

    private static Task<WireAnswer> Match(object condition, string entity = Corpus.Shipment, LabService? service = null) =>
        Send(entity, $$"""[ { "match": {{Json.Text(condition)}} }, { "project": { "id": 1 } }, {{FullPage}} ]""", service);

    private static async Task<WireAnswer> Send(string entity, string pipeline, LabService? service = null) =>
        await (await Lab.ClientAsync(service ?? Corpus.Entity(entity).Service)).SendAsync(entity, pipeline);

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
    public async Task C01_a_dot_separated_camelCase_path_resolves_at_every_depth_id_included()
    {
        // department.name is Dispatch wherever a department is embedded; one row holds none.
        var byDepartment = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Text(row, "department.name") == "Dispatch");
        byDepartment.Should().NotBeEmpty().And.HaveCountLessThan(Corpus.Counts(Corpus.Shipment).A);
        (await Match(new JsonObject { ["department.name"] = new JsonObject { ["eq"] = "Dispatch" } })).ShouldHaveIds(byDepartment);

        var byArticle = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Texts(row, "items.article.name").Contains("Gravel"));
        byArticle.Should().ContainSingle();
        (await Match(new JsonObject { ["items.article.name"] = new JsonObject { ["eq"] = "Gravel" } })).ShouldHaveIds(byArticle);

        var itemId = Corpus.Row<Fleet.Models.Transport.Shipment>(Corpus.Shipment, "items-three").Doc.Items[1].Id;
        (await Match(new JsonObject { ["items.id"] = new JsonObject { ["eq"] = itemId.ToString() } })).ShouldHaveIds([ItemsThree]);
    }

    [Theory]
    [InlineData("C02", "$where")]
    [InlineData("C02", "loadAddress.$city")]
    [InlineData("C03", "loadAddress..city")]
    [InlineData("C04", "")]
    [InlineData("C05", "loadAddress.")]
    [InlineData("C05", "loadAddress..")]
    public async Task C02_C05_a_path_with_a_dollar_an_empty_segment_or_nothing_at_all_is_an_invalid_path(string id, string path)
    {
        var answer = await Match(new JsonObject { [path] = new JsonObject { ["eq"] = 1 } });

        answer.ShouldRefuse("INVALID_PATH", 400, id);
        answer.ErrorCodes.Should().Equal("INVALID_PATH");

        if (path.Length == 0)
            answer.Errors[0]["path"]!.GetValue<string>().Should().Be("");
    }

    [Fact]
    public async Task C06_a_PascalCase_segment_is_an_unknown_path_the_server_has_no_casing_rule()
    {
        Refused(await Match("""{ "LoadAddress.City": { "eq": "Koeln" } }"""), "UNKNOWN_PATH", "LoadAddress.City");
    }

    [Fact]
    public async Task C07_an_unknown_path_in_match_is_refused_naming_the_stage_index_and_the_path()
    {
        var answer = await Send(Corpus.Shipment, """[ { "match": {} }, { "sort": [ { "id": "asc" } ] }, { "match": { "nope": { "eq": 1 } } }, { "page": { "limit": 5 } } ]""");

        Refused(answer, "UNKNOWN_PATH", "nope", 2);
    }

    [Fact]
    public async Task C08_an_unknown_path_in_sort_is_refused()
    {
        Refused(await Send(Corpus.Shipment, """[ { "sort": [ { "nope": "asc" } ] }, { "page": { "limit": 5 } } ]"""), "UNKNOWN_PATH", "nope", 0);
    }

    [Fact]
    public async Task C09_an_unknown_path_in_project_is_refused()
    {
        Refused(await Send(Corpus.Shipment, """[ { "project": { "nope": 1 } }, { "page": { "limit": 5 } } ]"""), "UNKNOWN_PATH", "nope", 0);
    }

    // The legacy fleet's unstored members were tours.isMirrored and tours.resource.type on the
    // shipment. The lab model's unstored members are conformance.entity's scratch ([BsonIgnore])
    // and computed (get-only), so C10-C12 are asserted there. There is no unstored member under a
    // collection in the lab model, so C10's "NOT_STORED wins over GROUP_ON_COLLECTION" leg is
    // asserted on a root unstored member instead.

    [Theory]
    [InlineData("scratch")]
    [InlineData("computed")]
    public async Task C10_a_member_the_driver_does_not_store_is_refused_for_match_sort_and_group(string path)
    {
        Refused(await Match(new JsonObject { [path] = new JsonObject { ["eq"] = "x" } }, Corpus.Conformance), "NOT_STORED", path, 0);
        Refused(await Send(Corpus.Conformance, $$"""[ { "sort": [ { "{{path}}": "asc" } ] }, { "page": { "limit": 5 } } ]"""), "NOT_STORED", path, 0);
        Refused(await Send(Corpus.Conformance, $$"""[ { "group": { "by": [ { "path": "{{path}}", "as": "x" } ], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } } ]"""), "NOT_STORED", path, 0);
    }

    [Fact]
    public async Task C11_an_unstored_member_is_projectable_and_simply_absent_from_the_row()
    {
        var target = Corpus.Rows(Corpus.Conformance)[0].Id;

        var answer = await Send(Corpus.Conformance, $$"""[ { "match": { "id": { "eq": "{{target}}" } } }, { "project": { "id": 1, "scratch": 1, "computed": 1 } }, { "page": { "limit": 5 } } ]""");

        answer.ShouldHaveIds([target]);
        answer.Items[0]!.AsObject().Select(member => member.Key).Should().Equal("id");
    }

    [Fact]
    public async Task C12_the_unstored_members_are_exactly_the_ones_the_model_marks_unstored_and_exists_is_refused_on_them_too()
    {
        var unstored = LabService.Conformance.Model.Entities[Corpus.Conformance].Paths.Where(path => !path.Stored).Select(path => path.Wire).Order(StringComparer.Ordinal).ToList();
        unstored.Should().Equal("computed", "scratch");

        foreach (var path in unstored)
            Refused(await Match(new JsonObject { [path] = new JsonObject { ["exists"] = true } }, Corpus.Conformance), "NOT_STORED", path);
    }

    [Fact]
    public async Task C13_an_unknown_kind_member_is_projectable_but_refused_for_filter_and_sort()
    {
        // addon.vincario is defined with kind object: an unknown-kind path.
        Refused(await Match("""{ "addon.vincario": { "eq": "x" } }"""), "NOT_FILTERABLE", "addon.vincario");
        Refused(await Send(Corpus.Shipment, """[ { "sort": [ { "addon.vincario": "asc" } ] }, { "page": { "limit": 5 } } ]"""), "NOT_SORTABLE", "addon.vincario");

        var projected = await Send(Corpus.Shipment, $$"""[ { "match": { "id": { "eq": "{{AddonRich}}" } } }, { "project": { "id": 1, "addon.vincario": 1 } }, { "page": { "limit": 5 } } ]""");
        projected.ShouldHaveIds([AddonRich]);
        projected.Items[0]!["addon"]!.AsObject().Select(member => member.Key).Should().Equal("vincario");

        // …while its declared members one level down are filterable: they have their own definitions.
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Text(row, "addon.vincario._v.make") == "MAN");
        expected.Should().Equal([AddonRich]);
        (await Match("""{ "addon.vincario._v.make": { "eq": "MAN" } }""")).ShouldHaveIds(expected);
    }

    [Fact]
    public async Task C14_a_path_deeper_than_32_segments_is_an_unknown_path()
    {
        var deep = string.Join('.', Enumerable.Range(0, 33).Select(index => $"a{index}"));

        Refused(await Match(new JsonObject { [deep] = new JsonObject { ["eq"] = 1 } }), "UNKNOWN_PATH");

        // The depth rule cannot be separated from ordinary unknown-ness: no real path is that deep.
        LabService.Transport.Model.Entities[Corpus.Shipment].Paths.Max(path => path.Wire.Split('.').Length).Should().BeLessThan(32);
    }

    [Fact]
    public async Task C15_a_dictionary_member_takes_any_key_verbatim_and_is_typed_by_the_definition()
    {
        var rich = Corpus.IdOf(Corpus.Employee, "addon-rich");
        var heavy = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Decimal(row, "addon.weight") >= 1000m);
        heavy.Should().Equal([rich]);

        (await Match("""{ "addon.weight": { "gte": 1000 } }""", Corpus.Employee)).ShouldHaveIds(heavy);

        // A long key above 2^53 takes its exact string form: only the definition's kind makes that work.
        (await Match("""{ "addon.tourCount": { "eq": "9007199254740993" } }""", Corpus.Employee)).ShouldHaveIds(heavy);

        // A key with no definition is opaque: projectable, never filterable.
        Refused(await Match("""{ "addon.undefinedKey": { "eq": "opaque" } }""", Corpus.Employee), "NOT_FILTERABLE", "addon.undefinedKey");
    }

    [Theory]
    [InlineData(Corpus.Employee)]
    [InlineData(Corpus.Vehicle)]
    [InlineData(Corpus.Shipment)]
    public async Task C15b_the_same_weight_definition_is_filterable_on_every_business_bag_the_retired_shadow_included(string entity)
    {
        // transport.shipment in organisation A stores a retired weight definition before the live
        // one (the legacy SH5 finding): the live one must win there too.
        var expected = Corpus.IdsWhere(entity, row => Corpus.Decimal(row, "addon.weight") >= 1000m);
        expected.Should().Equal([Corpus.IdOf(entity, "addon-rich")]);

        var answer = await Match("""{ "addon.weight": { "gte": 1000 } }""", entity);
        answer.ShouldHaveIds(expected);

        // The same answer on every bag, the documented diagnostic of an ordered comparison on a
        // defined decimal key included (a bag may hold the decimal as text).
        answer.DiagnosticCodes.Should().Equal("DECIMAL_TEXT_EXCLUDED");

        var present = Corpus.IdsWhere(entity, row => Corpus.Present(row, "addon.weight"));
        present.Should().Equal(expected, "exists answers the same row, which is what the same key has to mean");
        (await Match("""{ "addon.weight": { "exists": true } }""", entity)).ShouldHaveIds(present);
    }

    [Fact]
    public async Task C16_a_dictionary_key_with_characters_the_path_grammar_forbids_is_accepted_below_the_dictionary_root()
    {
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Text(row, "addon.Ablieferbelege vorhanden") == "ja");
        expected.Should().Equal([AddonRich]);

        (await Match("""{ "addon.Ablieferbelege vorhanden": { "eq": "ja" } }""")).ShouldHaveIds(expected);
    }

    [Fact]
    public async Task C17_a_star_segment_under_a_dictionary_is_an_undefined_key_not_a_wildcard()
    {
        // No definition names *, so it is an opaque key: refused, never zero rows.
        Refused(await Match("""{ "addon.*.singlePriceNet": { "eq": 1 } }"""), "NOT_FILTERABLE", "addon.*.singlePriceNet");
    }

    [Fact]
    public async Task C18_a_path_is_validated_at_the_stage_where_it_appears_and_the_error_names_that_stage()
    {
        var answer = await Send(Corpus.Shipment, """
            [ { "match": {} },
              { "project": { "shipmentNumber": 1, "status.name": 1 } },
              { "sort": [ { "shipmentNumber": "asc" } ] },
              { "match": { "referenceNumber": { "eq": "R-0001" } } },
              { "page": { "limit": 5 } } ]
            """);

        Refused(answer, "UNKNOWN_PATH", "referenceNumber", 3);
    }

    [Fact]
    public async Task C19_an_unwind_alias_is_a_new_root_addressable_by_later_stages_with_its_sources_operators()
    {
        var items = Corpus.Row<Fleet.Models.Transport.Shipment>(Corpus.Shipment, "items-three").Doc.Items
            .Where(item => item.Status?.Name == "Open").OrderByDescending(item => item.OrderNumber).ToList();
        items.Should().HaveCount(3);

        var answer = await Send(Corpus.Shipment, $$"""
            [ { "match": { "id": { "eq": "{{ItemsThree}}" } } },
              { "unwind": { "path": "items", "as": "item" } },
              { "match": { "item.status.name": { "eq": "Open" } } },
              { "sort": [ { "item.orderNumber": "desc" } ] },
              { "project": { "item.orderNumber": 1, "item.article.name": 1 } },
              { "page": { "limit": 50 } } ]
            """);

        answer.ShouldBeOk();
        answer.Values("item.orderNumber").Select(value => value!.GetValue<int>()).Should().Equal(items.Select(item => item.OrderNumber));
        answer.Strings("item.article.name").Should().Equal(items.Select(item => item.Article!.Name));
    }

    [Theory]
    [InlineData("item.x")]
    [InlineData("my-alias")]
    [InlineData("2x")]
    [InlineData("")]
    public async Task C20_an_alias_that_is_not_a_single_identifier_is_refused(string alias)
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "{{alias}}" } }, {{FullPage}} ]"""), "INVALID_ALIAS");
    }

    [Fact]
    public async Task C21_an_alias_naming_a_member_the_row_already_has_is_refused()
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "status" } }, {{FullPage}} ]"""), "ALIAS_COLLISION", stage: 0);
    }

    [Fact]
    public async Task C22_an_alias_colliding_with_a_previous_alias_is_refused_at_the_later_stage()
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "as": "thing" } }, { "unwind": { "path": "tags", "as": "thing" } }, {{FullPage}} ]"""), "ALIAS_COLLISION", stage: 1);
    }

    [Fact]
    public async Task C23_group_aliases_replace_the_shape_so_an_alias_may_reuse_a_root_name_and_collides_only_with_group_aliases()
    {
        var buckets = Corpus.Rows(Corpus.Shipment).GroupBy(row => Corpus.Text(row, "status.name")!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, Comparer<string>.Create(Order.CompareCollated)).Select(group => (group.Key, (long)group.Count())).ToList();
        buckets.Should().HaveCountGreaterThan(1);

        var answer = await Send(Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "status" } ], "fields": { "n": { "count": true } } } }, { "sort": [ { "status": "asc" } ] }, { "page": { "limit": 50 } } ]""");

        answer.ShouldBeOk("the alias reuses a root member name of the pre-group shape");
        answer.Items.Select(row => (row!["status"]!.GetValue<string>(), long.Parse(row["n"]!.GetValue<string>()))).Should().Equal(buckets);

        // …and the alias is addressable after the group, on the grouped shape.
        var afterGroup = await Send(Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "status" } ], "fields": { "n": { "count": true } } } }, { "match": { "status": { "eq": "Open" } } }, { "page": { "limit": 50 } } ]""");
        afterGroup.ShouldBeOk();
        afterGroup.Strings("status").Should().Equal("Open");

        // Two group aliases of the same name do collide.
        var collision = await Send(Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "x" }, { "path": "shipmentNumber", "as": "x" } ], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } } ]""");
        Refused(collision, "ALIAS_COLLISION");
    }

    [Fact]
    public async Task C24_a_path_under_an_unwind_index_alias_is_refused_the_index_is_a_scalar()
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "items", "includeIndex": "position" } }, { "match": { "position.x": { "eq": 1 } } }, {{FullPage}} ]"""), "UNKNOWN_PATH", "position.x", 1);
    }

    [Fact]
    public async Task Cfold1_the_key_stays_addressable_after_an_inclusion_projection_that_omits_it()
    {
        var filtered = await Send(Corpus.Shipment, $$"""[ { "project": { "shipmentNumber": 1 } }, { "match": { "id": { "eq": "{{ItemsThree}}" } } }, { "page": { "limit": 5 } } ]""");
        filtered.ShouldHaveIds([ItemsThree]);

        var sorted = await Send(Corpus.Shipment, """[ { "project": { "shipmentNumber": 1 } }, { "sort": [ { "id": "desc" } ] }, { "page": { "limit": 2 } } ]""");
        sorted.ShouldHaveIds(Corpus.AllIds(Corpus.Shipment).Reverse().Take(2));
    }

    [Theory]
    [InlineData("""{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.orderNumber": { "eq": 1 } } }""", "item.orderNumber")]
    [InlineData("""{ "unwind": { "path": "items", "as": "item" } }, { "sort": [ { "item.orderNumber": "asc" } ] }""", "item.orderNumber")]
    [InlineData("""{ "unwind": { "path": "items", "includeIndex": "at" } }, { "match": { "at": { "eq": 0 } } }""", "at")]
    public async Task Cfold2_an_inclusion_projection_before_an_unwind_makes_the_aliases_it_writes_unaddressable(string stages, string path)
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "project": { "items.orderNumber": 1 } }, {{stages}}, { "page": { "limit": 3 } } ]"""), "UNKNOWN_PATH", path, 2);
    }

    [Fact]
    public async Task Cfold2b_naming_the_alias_in_a_projection_after_the_unwind_works()
    {
        var expected = Corpus.Rows(Corpus.Shipment).SelectMany(row => Corpus.Elements(row, "items").Select((_, index) => (row.Id, index)))
            .OrderBy(entry => entry.index).ThenBy(entry => entry.Id.ToString("D"), StringComparer.Ordinal).Take(3).ToList();

        var works = await Send(Corpus.Shipment, """[ { "unwind": { "path": "items", "includeIndex": "at" } }, { "project": { "at": 1, "items.orderNumber": 1 } }, { "sort": [ { "at": "asc" } ] }, { "page": { "limit": 3 } } ]""");

        works.ShouldHaveIds(expected.Select(entry => entry.Id));
        works.Values("at").Select(value => value!.GetValue<int>()).Should().Equal(expected.Select(entry => entry.index));
    }

    [Fact]
    public async Task Cfold3_a_projection_that_keeps_a_collection_allows_its_unwind_and_one_that_drops_it_does_not()
    {
        var expected = Corpus.Rows(Corpus.Shipment).SelectMany(row => Corpus.Elements(row, "items").Select(_ => row.Id)).Take(3).ToList();

        var kept = await Send(Corpus.Shipment, """[ { "project": { "items.orderNumber": 1 } }, { "unwind": { "path": "items" } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 3 } } ]""");
        kept.ShouldHaveIds(expected);

        foreach (var stage in new[] { """{ "project": { "status.name": 1 } }""", """{ "project": { "items": 0 } }""" })
            Refused(await Send(Corpus.Shipment, $$"""[ {{stage}}, { "unwind": { "path": "items" } }, { "page": { "limit": 3 } } ]"""), "UNKNOWN_PATH", "items", 1);
    }

    [Fact]
    public async Task Cfold4_after_a_group_only_the_group_aliases_exist_and_a_projection_over_them_behaves_like_any_other()
    {
        var names = Corpus.Rows(Corpus.Shipment).Select(row => Corpus.Text(row, "status.name")!).Distinct().Order(Comparer<string>.Create(Order.CompareCollated)).ToList();

        var projected = await Send(Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "st" } ], "fields": { "n": { "count": true } } } }, { "project": { "st": 1 } }, { "sort": [ { "st": "asc" } ] }, { "page": { "limit": 20 } } ]""");

        projected.ShouldBeOk();
        projected.Items.SelectMany(row => row!.AsObject().Select(member => member.Key)).Distinct().Should().Equal("st");
        projected.Strings("st").Should().Equal(names);

        var outside = await Send(Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "st" } ], "fields": { "n": { "count": true } } } }, { "project": { "shipmentNumber": 1 } }, { "page": { "limit": 20 } } ]""");
        Refused(outside, "UNKNOWN_PATH", "shipmentNumber", 1);
    }

    [Fact]
    public async Task Cfold5_an_exclusion_of_the_key_does_drop_it()
    {
        Refused(await Send(Corpus.Shipment, """[ { "project": { "id": 0 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]"""), "UNKNOWN_PATH", "id", 1);
        Refused(await Send(Corpus.Shipment, $$"""[ { "project": { "id": 0 } }, { "match": { "id": { "eq": "{{ItemsThree}}" } } }, { "page": { "limit": 5 } } ]"""), "UNKNOWN_PATH", "id", 1);
    }

    [Fact]
    public async Task C26_an_alias_of_a_collection_resolves_to_the_schema_path_for_the_enum_on_both_legs()
    {
        var nested = Corpus.Row(Corpus.Shipment, "billing-nested");
        Corpus.Elements(nested, "billingLines.type").Select(value => value.AsInt32).Should().Equal(0, 1);

        // An enum operand through an alias is checked against the source member's enum.
        Refused(await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "billingLines", "as": "bl" } }, { "match": { "bl.type": { "eq": "Nonsense" } } }, {{FullPage}} ]"""), "UNKNOWN_ENUM_MEMBER", "bl.type", 1);

        var carriers = Corpus.Rows(Corpus.Shipment).SelectMany(row => Corpus.Elements(row, "billingLines").OfType<BsonDocument>()
            .Where(line => line["Type"].AsInt32 == 1).Select(line => (row.Id, Text: line["Text"].AsString))).ToList();
        carriers.Should().Equal([(nested.Id, "line 2")]);

        var live = await Send(Corpus.Shipment, """[ { "unwind": { "path": "billingLines", "as": "bl" } }, { "match": { "bl.type": { "eq": "Carrier" } } }, { "sort": [ { "id": "asc" } ] }, { "project": { "bl.text": 1 } }, { "page": { "limit": 50 } } ]""");
        live.ShouldHaveIds(carriers.Select(carrier => carrier.Id));
        live.Strings("bl.text").Should().Equal(carriers.Select(carrier => carrier.Text));

        // The grouped alias carries the stored number on the wire; the client names it.
        var grouped = await Send(Corpus.Shipment, """[ { "unwind": { "path": "billingLines", "as": "bl" } }, { "group": { "by": [ { "path": "bl.type", "as": "lineType" } ], "fields": { "n": { "count": true } } } }, { "sort": [ { "lineType": "asc" } ] }, { "page": { "limit": 50 } } ]""");
        grouped.ShouldBeOk();
        grouped.Values("lineType").Select(value => value!.GetValue<int>()).Should().Equal(0, 1);
    }
}
