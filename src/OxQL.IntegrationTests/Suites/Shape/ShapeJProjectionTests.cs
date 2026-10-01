using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Shape;

/// <summary>
/// Area J: <c>project</c> in its inclusion and exclusion forms: what the engine emits, and what
/// later stages may still name. Ported from the legacy <c>shape-j-projection</c> battery, engine
/// half only: the client's projection builder (<c>spread</c>, <c>key()</c>, de-duplication, the
/// dropped empty stage) went to the client's specs. The last cases pin an inclusion projection
/// that leaves out a local join alias: the alias leaves the row.
/// </summary>
[Trait("Category", "Integration")]
public class ShapeJProjectionTests
{
    private static Guid ItemsThree => Corpus.IdOf(Corpus.Shipment, "items-three");

    private static async Task<WireAnswer> Send(string pipeline, string entity = Corpus.Shipment) =>
        await (await Lab.ClientForAsync(entity)).SendAsync(entity, pipeline);

    private static Task<WireAnswer> One(string projection, Guid? id = null) =>
        Send($$"""[ { "match": { "id": { "eq": "{{id ?? ItemsThree}}" } } }, { "project": {{projection}} }, { "page": { "limit": 5 } } ]""");

    /// <summary>The members the rows of a page carry, as one ordered set.</summary>
    private static IReadOnlyList<string> Members(WireAnswer answer) =>
        answer.Items.SelectMany(row => row!.AsObject().Select(member => member.Key)).Distinct().Order(StringComparer.Ordinal).ToList();

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
    public async Task J01_an_inclusion_projection_narrows_the_row_to_the_named_paths()
    {
        var answer = await One("""{ "shipmentNumber": 1, "status.name": 1 }""");

        answer.ShouldHaveIds([ItemsThree]);
        Members(answer).Should().Equal("id", "shipmentNumber", "status");
        answer.Items[0]!["status"]!.AsObject().Select(member => member.Key).Should().Equal("name");
        answer.Strings("shipmentNumber").Should().Equal(Corpus.Text(Corpus.Row(Corpus.Shipment, "items-three"), "shipmentNumber"));
    }

    [Fact]
    public async Task J02_id_is_kept_by_an_inclusion_unless_explicitly_excluded()
    {
        var answer = await One("""{ "shipmentNumber": 1 }""");

        answer.ShouldHaveIds([ItemsThree]);
        Members(answer).Should().Equal("id", "shipmentNumber");
    }

    [Fact]
    public async Task J03_excluding_only_the_key_from_an_inclusion_is_legal()
    {
        var answer = await One("""{ "id": 0, "shipmentNumber": 1 }""");

        answer.ShouldBeOk();
        Members(answer).Should().Equal("shipmentNumber");
    }

    [Fact]
    public async Task J04_an_exclusion_projection_keeps_everything_but_the_named_paths()
    {
        var stored = Corpus.Row(Corpus.Shipment, "items-three").Stored.Names.Count();

        var answer = await One("""{ "shipmentNumber": 0, "referenceNumber": 0 }""");

        answer.ShouldHaveIds([ItemsThree]);
        var members = Members(answer);
        members.Should().NotContain(["shipmentNumber", "referenceNumber"]).And.Contain(["id", "status"]);
        members.Count.Should().Be(stored - 2, "every stored member but the two excluded comes back");
    }

    [Theory]
    [InlineData("""{ "shipmentNumber": 1, "referenceNumber": 0 }""")]
    [InlineData("""{ "referenceNumber": 0, "shipmentNumber": 1 }""")]
    public async Task J05_mixing_1_and_0_on_paths_other_than_id_is_refused(string projection)
    {
        Refused(await Send($$"""[ { "project": {{projection}} }, { "page": { "limit": 5 } } ]"""), "MIXED_PROJECTION", stage: 0);
    }

    [Fact]
    public async Task J06_an_empty_project_stage_is_refused()
    {
        Refused(await Send("""[ { "project": {} }, { "page": { "limit": 5 } } ]"""), "MIXED_PROJECTION", stage: 0);
    }

    [Fact]
    public async Task J07_an_inclusion_then_an_exclusion_are_two_stages_and_each_is_one_flag()
    {
        // What the client compiles for project().omit(): two stages, not one mixed stage.
        var answer = await Send($$"""[ { "match": { "id": { "eq": "{{ItemsThree}}" } } }, { "project": { "shipmentNumber": 1, "referenceNumber": 1 } }, { "project": { "referenceNumber": 0 } }, { "page": { "limit": 5 } } ]""");

        answer.ShouldHaveIds([ItemsThree]);
        Members(answer).Should().Equal("id", "shipmentNumber");
    }

    [Fact]
    public async Task J08_a_projection_is_applied_where_it_stands_and_later_stages_address_only_what_it_kept()
    {
        var reference = Corpus.Text(Corpus.Row(Corpus.Shipment, "items-three"), "referenceNumber")!;
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Text(row, "referenceNumber") is { } text && Order.EqualsCi(text, reference));
        expected.Should().Contain(ItemsThree);

        var before = await Send($$"""[ { "match": { "referenceNumber": { "eq": "{{reference}}" } } }, { "project": { "shipmentNumber": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } } ]""");
        before.ShouldHaveIds(expected, "the same path is fine before the projection");

        var after = await Send($$"""[ { "project": { "shipmentNumber": 1 } }, { "match": { "referenceNumber": { "eq": "{{reference}}" } } }, { "page": { "limit": 5 } } ]""");
        Refused(after, "UNKNOWN_PATH", "referenceNumber", 1);
    }

    [Fact]
    public async Task J09_a_path_dropped_by_an_inclusion_is_refused_in_a_later_match()
    {
        Refused(await Send("""[ { "project": { "shipmentNumber": 1 } }, { "match": { "referenceNumber": { "eq": "R-0001" } } }, { "page": { "limit": 5 } } ]"""), "UNKNOWN_PATH", "referenceNumber", 1);
    }

    [Fact]
    public async Task J10_a_path_removed_by_an_exclusion_is_refused_in_a_later_sort()
    {
        Refused(await Send("""[ { "project": { "referenceNumber": 0 } }, { "sort": [ { "referenceNumber": "asc" } ] }, { "page": { "limit": 5 } } ]"""), "UNKNOWN_PATH", "referenceNumber", 1);
    }

    [Fact]
    public async Task J11_an_ancestor_or_a_descendant_of_a_kept_path_is_still_addressable()
    {
        Corpus.Text(Corpus.Row(Corpus.Shipment, "items-three"), "loadAddress.city").Should().Be("Koeln");

        var ancestor = await Send($$"""[ { "match": { "id": { "eq": "{{ItemsThree}}" } } }, { "project": { "loadAddress.city": 1 } }, { "match": { "loadAddress": { "exists": true } } }, { "page": { "limit": 5 } } ]""");
        ancestor.ShouldHaveIds([ItemsThree]);

        var descendant = await Send($$"""[ { "match": { "id": { "eq": "{{ItemsThree}}" } } }, { "project": { "loadAddress": 1 } }, { "match": { "loadAddress.city": { "eq": "Koeln" } } }, { "page": { "limit": 5 } } ]""");
        descendant.ShouldHaveIds([ItemsThree]);
    }

    [Fact]
    public async Task J12_more_than_max_projection_fields_is_refused()
    {
        var many = new JsonObject(Enumerable.Range(0, 501).Select(index => new KeyValuePair<string, JsonNode?>($"x{index}", 1)));

        var answer = await Send($$"""[ { "project": {{many.ToJsonString()}} }, { "page": { "limit": 5 } } ]""");

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().Contain("MAX_PROJECTION_FIELDS_EXCEEDED");

        // 500 is the limit itself: the same stage one field shorter is refused for its unknown paths only.
        var atLimit = new JsonObject(Enumerable.Range(0, 500).Select(index => new KeyValuePair<string, JsonNode?>($"x{index}", 1)));
        (await Send($$"""[ { "project": {{atLimit.ToJsonString()}} }, { "page": { "limit": 5 } } ]""")).ErrorCodes.Should().NotContain("MAX_PROJECTION_FIELDS_EXCEEDED");
    }

    [Fact]
    public async Task J13_the_nested_projection_syntax_is_read_as_dotted_paths()
    {
        var nested = await One("""{ "loadAddress": { "city": 1 } }""");
        var flat = await One("""{ "loadAddress.city": 1 }""");

        nested.ShouldHaveIds([ItemsThree]);
        nested.Items.ToJsonString().Should().Be(flat.Items.ToJsonString());
        Members(nested).Should().Equal("id", "loadAddress");
        nested.Items[0]!["loadAddress"]!.AsObject().Select(member => member.Key).Should().Equal("city");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"yes\"")]
    [InlineData("[\"a\"]")]
    [InlineData("false")]
    public async Task J14_a_non_numeric_projection_leaf_means_include_false_included(string leaf)
    {
        var asOne = await One("""{ "shipmentNumber": 1 }""");

        var answer = await One($$"""{ "shipmentNumber": {{leaf}} }""");

        answer.ShouldBeOk(leaf);
        answer.Items.ToJsonString().Should().Be(asOne.Items.ToJsonString(), leaf);
        Members(answer).Should().Equal("id", "shipmentNumber");
    }

    [Fact]
    public async Task J15_a_non_integral_projection_value_fails_before_binding_as_ProblemDetails_not_an_OxQL_envelope()
    {
        var answer = await Send("""[ { "project": { "shipmentNumber": 1.5 } }, { "page": { "limit": 5 } } ]""");

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().BeEmpty("no coded OxQL error: the JSON converter threw");
        answer.IsProblemDetails.Should().BeTrue(answer.ToString());
        answer.Text.Should().Contain("ProjectStage");
    }

    [Fact]
    public async Task J16_an_unstored_member_is_projectable_and_NOT_STORED_is_not_raised_in_a_projection()
    {
        // The lab's unstored members are conformance.entity's scratch and computed (see C10).
        var target = Corpus.Rows(Corpus.Conformance)[0].Id;

        var answer = await Send($$"""[ { "match": { "id": { "eq": "{{target}}" } } }, { "project": { "id": 1, "scratch": 1, "computed": 1 } }, { "page": { "limit": 5 } } ]""", Corpus.Conformance);

        answer.ShouldHaveIds([target]);
        Members(answer).Should().Equal("id");

        // The same path in a match is refused, which is what unstored means.
        Refused(await Send("""[ { "match": { "scratch": { "eq": "x" } } }, { "page": { "limit": 5 } } ]""", Corpus.Conformance), "NOT_STORED", "scratch");
    }

    [Fact]
    public async Task J17_the_key_excluded_from_the_row_still_breaks_ties_so_a_cursor_walk_neither_repeats_nor_loses_a_row()
    {
        var expected = Corpus.Sorted(Corpus.Shipment, [("shipmentNumber", false)]);
        expected.Count.Should().BeGreaterThan(7);

        var client = await Lab.ClientAsync(LabService.Transport);
        var first = await client.SendAsync(Corpus.Shipment, """[ { "project": { "id": 0, "shipmentNumber": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] }, { "page": { "limit": 7, "includeTotalCount": true } } ]""");
        first.ShouldHaveTotal(expected.Count);
        first.HasNextPage.Should().BeTrue();
        Members(first).Should().Equal("shipmentNumber");

        // shipmentNumber holds nulls, a missing member, an empty string and a duplicate; the key is
        // invisible but still doing the tie-breaking.
        var walk = await client.WalkAsync(Corpus.Shipment, """[ { "project": { "id": 0, "shipmentNumber": 1 } }, { "sort": [ { "shipmentNumber": "asc" } ] } ]""", limit: 7);

        walk.Items.Should().HaveCount(expected.Count);
        walk.Items.Select(row => Json.At(row, "shipmentNumber")?.GetValue<string>()).Should().Equal(expected.Select(row => Corpus.Text(row, "shipmentNumber")));
        walk.Items.Should().OnlyContain(row => !row.ContainsKey("id"));
    }

    [Fact]
    public async Task J18_a_projection_that_drops_the_reference_member_fails_the_resolve_on_the_path_and_one_after_keeps_the_key_for_it()
    {
        // Legacy: unreachable (no reference declared). transport.shipment#department.id is a
        // remote reference onto fleet.department in the lab model, so both halves now run.
        var dropped = await Send("""[ { "project": { "id": 1 } }, { "resolve": { "path": "department.id", "as": "dep" } }, { "page": { "limit": 5 } } ]""");
        Refused(dropped, "UNKNOWN_PATH", "department.id", 1);

        // A projection after the resolve that names the alias but not the reference member: the
        // key survives in storage for the join and is left out of the row.
        var departments = Corpus.Rows(Corpus.Department).ToDictionary(row => row.Id);
        var expected = Corpus.Rows(Corpus.Shipment).Select(row => (row.Id,
            Name: Corpus.GuidAt(row, "department.id") is { } id && departments.TryGetValue(id, out var department) ? Corpus.Text(department, "name") : null)).ToList();
        expected.Should().Contain(entry => entry.Name == null).And.Contain(entry => entry.Name != null);

        var kept = await Send("""[ { "resolve": { "path": "department.id", "as": "dep" } }, { "project": { "id": 1, "dep": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } } ]""");

        kept.ShouldHaveIds(expected.Select(entry => entry.Id));
        Members(kept).Should().Equal("dep", "id");
        kept.Strings("dep.name").Should().Equal(expected.Select(entry => entry.Name));
    }

    [Fact]
    public async Task J19_an_exclusion_of_only_the_key_returns_every_other_member_and_emits_no_project_stage()
    {
        var stored = Corpus.Row(Corpus.Shipment, "items-three").Stored.Names.Count();

        var answer = await One("""{ "id": 0 }""");

        answer.ShouldBeOk();
        var members = Members(answer);
        members.Should().NotContain("id");
        members.Count.Should().Be(stored - 1);

        // Legacy: unreachable, there was no explain endpoint. The explain variant now answers it:
        // the projection is bound (an exclusion of the key), and because the key survives every
        // projection in storage for the cursor, a projection that excludes only the key leaves
        // nothing to project, so the page aggregate carries no $project; the row drops the key.
        var explained = await (await Lab.ClientAsync(LabService.Transport)).ExplainAsync(new JsonObject
        {
            ["query"] = Json.Request(Corpus.Shipment, $$"""[ { "match": { "id": { "eq": "{{ItemsThree}}" } } }, { "project": { "id": 0 } }, { "page": { "limit": 5 } } ]"""),
            ["include"] = new JsonArray("plan"),
        });
        explained.StatusCode.Should().Be(200, explained.ToString());
        explained.Body!["plan"]!["bound"]!["stages"]!.AsArray().Should().Contain(stage => stage!["project"] != null && stage["project"]!["mode"]!.GetValue<string>() == "exclude", explained.ToString());
        explained.Body!["plan"]!["stages"]!.AsArray().Should().NotContain(stage => stage!.AsObject().ContainsKey("$project"), explained.ToString());
    }

    [Fact]
    public async Task J20_dotted_paths_under_one_prefix_narrow_the_object_to_those_members()
    {
        // The engine half of the client's spread(prefix, fragment): what it expands to.
        var answer = await One("""{ "shipmentNumber": 1, "loadAddress.city": 1, "loadAddress.zipcode": 1, "loadAddress.country": 1 }""");

        answer.ShouldHaveIds([ItemsThree]);
        Members(answer).Should().Equal("id", "loadAddress", "shipmentNumber");
        answer.Items[0]!["loadAddress"]!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("city", "country", "zipcode");
    }

    [Fact]
    public async Task J23_a_projection_distributes_over_a_collection()
    {
        var items = Corpus.Elements(Corpus.Row(Corpus.Shipment, "items-three"), "items").OfType<BsonDocument>().Select(item => item["OrderNumber"].AsInt32).ToList();
        items.Should().HaveCount(3);

        var answer = await One("""{ "items.orderNumber": 1 }""");

        answer.ShouldHaveIds([ItemsThree]);
        var elements = answer.Items[0]!["items"]!.AsArray();
        elements.Should().OnlyContain(item => item!.AsObject().Select(member => member.Key).SequenceEqual(new[] { "orderNumber" }));
        elements.Select(item => item!["orderNumber"]!.GetValue<int>()).Should().Equal(items);

        // The same one level deeper, through the nested collection.
        var nested = Corpus.Row(Corpus.Shipment, "billing-nested");
        var references = Corpus.Texts(nested, "billingLines.references.referenceId");
        references.Should().Equal("O-1", "I-1", "O-2");

        var deep = await One("""{ "billingLines.references.referenceId": 1 }""", nested.Id);
        deep.Items[0]!["billingLines"]!.AsArray().SelectMany(line => line!["references"]!.AsArray().Select(reference => reference!["referenceId"]!.GetValue<string>()))
            .Should().Equal(references);
    }

    [Fact]
    public async Task J25_an_exclusion_removes_the_path_and_everything_under_it()
    {
        Refused(await Send($$"""[ { "match": { "id": { "eq": "{{ItemsThree}}" } } }, { "project": { "loadAddress": 0 } }, { "match": { "loadAddress.city": { "eq": "Koeln" } } }, { "page": { "limit": 5 } } ]"""),
            "UNKNOWN_PATH", "loadAddress.city", 2);

        var emitted = await One("""{ "loadAddress": 0 }""");
        emitted.ShouldHaveIds([ItemsThree]);
        Members(emitted).Should().NotContain("loadAddress");
    }

    // ── an inclusion projection that leaves out a local join alias ─────────────────────────

    public static TheoryData<string, string, string> LocalJoins() => new()
    {
        { "local resolve", Corpus.Equipment, """{ "resolve": { "path": "vehicle.id", "as": "veh" } }""" },
        { "local resolve on a code key", Corpus.Conformance, """{ "resolve": { "path": "refCode", "as": "ref" } }""" },
        { "lookup", Corpus.Conformance, """{ "lookup": { "from": "conformance.child", "path": "parentId", "as": "children" } }""" },
    };

    [Theory]
    [MemberData(nameof(LocalJoins))]
    public async Task D2_an_inclusion_projection_that_leaves_out_a_local_join_alias_does_not_return_it(string label, string entity, string join)
    {
        var expected = Corpus.AllIds(entity);

        var answer = await Send($$"""[ {{join}}, { "project": { "name": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } } ]""", entity);

        answer.ShouldHaveIds(expected, label);
        Members(answer).Should().Equal(["id", "name"], $"{label}: an inclusion projection keeps the key and the named paths only: {answer}");
    }

    [Theory]
    [MemberData(nameof(LocalJoins))]
    public async Task D2b_naming_the_local_join_alias_in_the_inclusion_projection_returns_the_join(string label, string entity, string join)
    {
        var alias = JsonNode.Parse(join)!.AsObject().First().Value!["as"]!.GetValue<string>();

        var answer = await Send($$"""[ {{join}}, { "project": { "name": 1, "{{alias}}": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } } ]""", entity);

        answer.ShouldHaveIds(Corpus.AllIds(entity), label);
        Members(answer).Should().Equal(new[] { "id", "name", alias }.Order(StringComparer.Ordinal), label);
    }
}
