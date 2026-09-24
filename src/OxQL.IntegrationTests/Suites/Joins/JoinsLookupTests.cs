using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// Area L: <c>lookup</c>, the backward join. The parent is <c>fleet.vehicle</c> and the child
/// <c>fleet.equipment</c> (<c>vehicle.id</c> references the vehicle); <c>fleet.department</c> is a
/// second parent whose children are vehicles (<c>department.id</c>), and
/// <c>conformance.entity</c> a third whose children declare <c>parentId</c>. Every expected child
/// set is computed from the corpus before the request.
/// <para>
/// Ported from the legacy <c>joins-l-lookup</c> battery. Its 22 red cases were red because the
/// vendored client modules carried no references; the lab model declares them, so the engine
/// half of each runs here. The typed-client halves (offline refusals, captured bytes) live with
/// the client's specs.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class JoinsLookupTests
{
    private const int MaxLookupLimit = 100;

    private static Task<LabClient> FleetClient() => Lab.ClientAsync(LabService.Fleet);

    private static IReadOnlyList<CorpusRow> Equipment(Org org = Org.A) => Corpus.Rows(Corpus.Equipment, org);

    private static IReadOnlyList<CorpusRow> Vehicles(Org org = Org.A) => Corpus.Rows(Corpus.Vehicle, org);

    /// <summary>The children of a vehicle: the equipment whose <c>vehicle.id</c> names it, key-ordered.</summary>
    private static IReadOnlyList<CorpusRow> ChildrenOf(Guid vehicle, Org org = Org.A) =>
        Equipment(org).Where(row => Corpus.GuidAt(row, "vehicle.id") == vehicle).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).ToList();

    /// <summary>The vehicles of a department, key-ordered.</summary>
    private static IReadOnlyList<CorpusRow> VehiclesOf(Guid department, Org org = Org.A) =>
        Vehicles(org).Where(row => Corpus.GuidAt(row, "department.id") == department).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).ToList();

    /// <summary>A vehicle with exactly two pieces of equipment, the parent most cases use.</summary>
    private static CorpusRow TwoChildParent()
    {
        var parent = Vehicles().FirstOrDefault(vehicle => ChildrenOf(vehicle.Id).Count == 2);
        parent.Should().NotBeNull("the corpus needs a vehicle with two pieces of equipment");
        return parent!;
    }

    private static IReadOnlyList<Guid> IdsIn(JsonNode? array) =>
        (array as JsonArray ?? throw new InvalidOperationException($"not an array: {array?.ToJsonString()}"))
        .Select(item => Guid.Parse(item!["id"]!.GetValue<string>())).ToList();

    // ── rows ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task L01_L02_L09_the_lookup_returns_exactly_the_children_key_ordered_with_the_default_select_and_an_empty_array_where_there_are_none()
    {
        string[] parents = ["VEH-001", "VEH-002", "VEH-003"];
        var expected = Vehicles().Where(vehicle => parents.Contains(Corpus.Text(vehicle, "matchCode"))).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds))
            .Select(vehicle => (vehicle.Id, Children: IdsOf(ChildrenOf(vehicle.Id)))).ToList();
        // Computed, not observed: VEH-001 has two, VEH-002 one, VEH-003 none, and the duplicate
        // matchCode row also has none.
        expected.Select(row => row.Children.Count).Should().Equal(2, 1, 0, 0);

        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, """
            [ { "match": { "matchCode": { "in": ["VEH-001", "VEH-002", "VEH-003"] } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "equipment" } },
              { "project": { "id": 1, "matchCode": 1, "equipment": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(expected.Select(row => row.Id));

        for (var index = 0; index < expected.Count; index++)
        {
            var children = answer.Items[index]!["equipment"];
            children.Should().BeOfType<JsonArray>($"an absent child set is [] and never null: {answer}");
            IdsIn(children).Should().Equal(expected[index].Children, "L2: the array is ordered by the child's key");
        }

        // L9: no select means the child's key plus its display member, and nothing else.
        answer.Items[0]!["equipment"]![0]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "name"]);
    }

    [Fact]
    public async Task L08_select_keeps_the_named_child_paths_and_the_child_key_whether_or_not_it_was_asked_for()
    {
        var parent = TwoChildParent();
        var expected = ChildrenOf(parent.Id);

        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"] } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 2 } } ]
            """);

        answer.ShouldHaveIds([parent.Id]);
        var children = answer.Items[0]!["eq"]!.AsArray();
        IdsIn(children).Should().Equal(IdsOf(expected));
        children.Select(child => child!["name"]!.GetValue<string>()).Should().Equal(expected.Select(child => Corpus.Text(child, "name")));
        children.Should().AllSatisfy(child => child!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "name"], "the key is kept although select did not name it, and it is the only extra member"));
    }

    [Fact]
    public async Task L10_select_accepts_a_bare_string_as_well_as_an_array_and_answers_identically()
    {
        var parent = TwoChildParent();
        var client = await FleetClient();

        async Task<string> ChildrenFor(string select) => (await client.SendAsync(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": {{select}} } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 2 } } ]
            """)).ShouldBeOk().Items[0]!["eq"]!.ToJsonString();

        var bare = await ChildrenFor("\"name\"");
        var array = await ChildrenFor("[\"name\"]");

        bare.Should().Be(array);
        IdsIn(JsonNode.Parse(bare)).Should().Equal(IdsOf(ChildrenOf(parent.Id)));
    }

    [Fact]
    public async Task L11_a_filter_on_the_child_narrows_the_array_to_the_matching_children_and_empties_it_where_none_match()
    {
        var parent = TwoChildParent();
        var all = ChildrenOf(parent.Id);
        var wanted = Corpus.Text(all[0], "name")!;
        var matching = IdsOf(all.Where(child => Corpus.Text(child, "name") == wanted));
        matching.Should().NotBeEmpty();
        var client = await FleetClient();

        async Task<JsonNode?> Children(string name) => (await client.SendAsync(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"], "filter": { "name": { "eq": "{{name}}" } } } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 2 } } ]
            """)).ShouldHaveIds([parent.Id]).Items[0]!["eq"];

        IdsIn(await Children(wanted)).Should().Equal(matching);
        IdsIn(await Children("no equipment is called this")).Should().BeEmpty("the parent stays and its array empties");

        // The filter binds against the child's shape: a path only the parent has is refused.
        (await client.SendAsync(Corpus.Vehicle, """
            [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "filter": { "mileage": { "eq": 1 } } } },
              { "page": { "limit": 1 } } ]
            """)).ShouldRefuse("UNKNOWN_PATH", 400);
    }

    [Fact]
    public async Task L12_L13_limit_caps_the_children_per_parent_100_is_accepted_and_101_and_0_are_refused()
    {
        var parent = TwoChildParent();
        var client = await FleetClient();

        var capped = await client.SendAsync(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"], "limit": 1 } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 2 } } ]
            """);

        // Key-ordered, so the child that survives the cap is the lowest-keyed one.
        IdsIn(capped.ShouldHaveIds([parent.Id]).Items[0]!["eq"]).Should().Equal(ChildrenOf(parent.Id)[0].Id);

        (await client.SendAsync(Corpus.Vehicle, $$"""
            [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "limit": {{MaxLookupLimit}} } }, { "page": { "limit": 1 } } ]
            """)).ShouldBeOk();

        foreach (var limit in new[] { MaxLookupLimit + 1, 0 })
        {
            var refused = await client.SendAsync(Corpus.Vehicle, $$"""
                [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "limit": {{limit}} } }, { "page": { "limit": 1 } } ]
                """);

            refused.ShouldRefuse("LOOKUP_LIMIT_EXCEEDED", 400, $"limit {limit}");
            refused.ErrorCodes.Should().Equal(["LOOKUP_LIMIT_EXCEEDED"], $"limit {limit}");
            refused.Text.Should().Contain(MaxLookupLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task L14_with_no_limit_the_lookup_applies_maxLookupLimit_as_the_default()
    {
        // The corpus' largest child set is 20, so the default is only observable over a parent
        // with more than 100 children: this fleet adds 101 pieces of equipment to one vehicle.
        var parent = Corpus.Row(Corpus.Vehicle, "dec-decimal128");
        const int Added = MaxLookupLimit + 1;

        await using var fleet = await CorpusFleet.CreateAsync("b4_lookup_default", seed: [LabService.Fleet], extra: async lab =>
        {
            var database = await lab.DatabaseAsync(LabService.Fleet);
            var collection = database.GetCollection<Equipment>(Corpus.Entity(Corpus.Equipment).Collection);

            await collection.InsertManyAsync(Enumerable.Range(1, Added).Select(n => new Equipment
            {
                Id = Ids.Of(Spaces.Equipment, Org.A, 1000 + n),
                OrganizationId = Org.A.Id(),
                Name = "Bulk " + n.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                Number = "EQ-B" + n.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                Vehicle = new VehicleSubset { Id = parent.Id, RegistrationPlate = "B-LA 100", MatchCode = "VEH-001" },
            }));
        });

        var all = IdsOf(ChildrenOf(parent.Id)).Concat(Enumerable.Range(1, Added).Select(n => Ids.Of(Spaces.Equipment, Org.A, 1000 + n))).Order().ToList();
        all.Count.Should().BeGreaterThan(MaxLookupLimit);
        var client = fleet.Client(LabService.Fleet);

        async Task<IReadOnlyList<Guid>> Children(string limit) => IdsIn((await client.SendAsync(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"]{{limit}} } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 1 } } ]
            """)).ShouldHaveIds([parent.Id]).Items[0]!["eq"]);

        (await Children("")).Should().Equal(all.Take(MaxLookupLimit), "no limit is maxLookupLimit, key-ordered, and never 'all'");
        (await Children($", \"limit\": {MaxLookupLimit}")).Should().Equal(all.Take(MaxLookupLimit));
    }

    [Fact]
    public async Task L28_a_second_parent_whose_children_are_vehicles_one_department_has_all_of_them_and_two_have_exactly_empty()
    {
        var departments = Corpus.Rows(Corpus.Department);
        var expected = departments.Select(department => IdsOf(VehiclesOf(department.Id))).ToList();
        expected.Select(ids => ids.Count).Should().Equal(Vehicles().Count, 0, 0);

        var answer = await (await FleetClient()).SendAsync(Corpus.Department, """
            [ { "lookup": { "from": "fleet.vehicle", "path": "department.id", "as": "vehicles", "select": ["matchCode"], "limit": 100 } },
              { "project": { "id": 1, "name": 1, "vehicles": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(departments));

        for (var index = 0; index < expected.Count; index++)
            IdsIn(answer.Items[index]!["vehicles"]).Should().Equal(expected[index], $"department {departments[index].Key}");
    }

    [Fact]
    public async Task L15_the_child_sub_pipeline_carries_the_childs_own_organisation_scope()
    {
        var answer = await (await FleetClient()).SendAsync(Corpus.Department, """
            [ { "lookup": { "from": "fleet.vehicle", "path": "department.id", "as": "vehicles", "select": ["matchCode", "organizationId"], "limit": 100 } },
              { "project": { "id": 1, "vehicles": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        var joined = answer.ShouldBeOk().Items.SelectMany(row => row!["vehicles"]!.AsArray()).ToList();

        joined.Select(child => child!["organizationId"]!.GetValue<string>()).Distinct().Should().Equal(Org.A.Id().Wire());
        joined.Select(child => Guid.Parse(child!["id"]!.GetValue<string>())).Should().BeEquivalentTo(Corpus.AllIds(Corpus.Vehicle), "every organisation A vehicle exactly once, none of B's");
    }

    [Fact]
    public async Task L17_a_filter_on_the_alias_selects_the_parents_and_the_count_agrees()
    {
        var wanted = Corpus.Text(Corpus.Row(Corpus.Equipment, "eq-veh1"), "name")!;
        var expected = IdsOf(Vehicles().Where(vehicle => ChildrenOf(vehicle.Id).Any(child => Corpus.Text(child, "name") == wanted)));
        expected.Should().NotBeEmpty();

        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, $$"""
            [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"] } },
              { "match": { "eq.name": { "eq": "{{wanted}}" } } },
              { "project": { "id": 1, "eq": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
        answer.Items.Should().AllSatisfy(row => row!["eq"]!.AsArray().Should().NotBeEmpty("a row that matched the filter has a child"));
    }

    [Fact]
    public async Task L17_J1_an_inclusion_projection_that_leaves_out_the_lookup_alias_leaves_it_out_of_the_row()
    {
        var wanted = Corpus.Text(Corpus.Row(Corpus.Equipment, "eq-veh1"), "name")!;
        var expected = IdsOf(Vehicles().Where(vehicle => ChildrenOf(vehicle.Id).Any(child => Corpus.Text(child, "name") == wanted)));
        expected.Should().NotBeEmpty();

        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, $$"""
            [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"] } },
              { "match": { "eq.name": { "eq": "{{wanted}}" } } },
              { "project": { "id": 1, "matchCode": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        // Every one of these rows provably has a child, so an [] under the alias is a lie; the
        // projection did not name the alias, so the row carries only what it named.
        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
        answer.Items.Should().AllSatisfy(row => row!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "matchCode"], answer.ToString()));
    }

    [Fact]
    public async Task L17_the_engine_refuses_a_sort_on_a_lookup_alias_with_NOT_SORTABLE_naming_the_collection()
    {
        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, """
            [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq" } }, { "sort": [ { "eq.name": "asc" } ] }, { "page": { "limit": 1 } } ]
            """);

        answer.ShouldRefuse("NOT_SORTABLE", 400)["message"]!.GetValue<string>().Should().Contain("lies in a collection");
        answer.ErrorCodes.Should().Equal("NOT_SORTABLE");
    }

    [Theory]
    [InlineData("L03", Corpus.Vehicle, """[ { "lookup": { "from": "fleet.equipment", "path": "createUserId", "as": "eq" } }, { "page": { "limit": 1 } } ]""", "LOOKUP_NOT_DECLARED")]
    [InlineData("L04", Corpus.Department, """[ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq" } }, { "page": { "limit": 1 } } ]""", "LOOKUP_NOT_DECLARED")]
    [InlineData("L06", Corpus.Vehicle, """[ { "lookup": { "from": "fleet.nope", "path": "vehicle.id", "as": "eq" } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_ENTITY")]
    [InlineData("L07", Corpus.Vehicle, """[ { "lookup": { "from": "fleet.Equipment", "path": "vehicle.id", "as": "eq" } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_ENTITY")]
    [InlineData("L07", Corpus.Vehicle, """[ { "lookup": { "from": "", "path": "vehicle.id", "as": "eq" } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_ENTITY")]
    [InlineData("L07", Corpus.Vehicle, """[ { "lookup": { "from": "equipment", "path": "vehicle.id", "as": "eq" } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_ENTITY")]
    [InlineData("L16", Corpus.Vehicle, """[ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "matchCode" } }, { "page": { "limit": 1 } } ]""", "ALIAS_COLLISION")]
    [InlineData("L18", Corpus.Vehicle, """[ { "group": { "by": [ { "path": "matchCode", "as": "mc" } ], "fields": { "n": { "count": true } } } }, { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq" } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_PATH")]
    [InlineData("L20", Corpus.Vehicle, """[ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "nope": 1 } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_STAGE_MEMBER")]
    [InlineData("L25", Corpus.Vehicle, """[ { "lookup": { "from": "transport.shipment_template", "path": "createUserId", "as": "templates" } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_ENTITY")]
    public async Task L03_L04_L06_L07_L16_L18_L20_L25_the_engine_refuses_the_lookup_shapes_it_cannot_serve(string caseId, string entity, string pipeline, string code)
    {
        // L7: `equipment` is the collection's name, never an entity id. L25: the child lives on
        // another service; there is no remote backward lookup, so the host does not know it.
        var answer = await (await FleetClient()).SendAsync(entity, pipeline);

        answer.ShouldRefuse(code, 400, caseId);
        answer.ErrorCodes.Should().Equal([code], caseId);

        if (caseId == "L18")
            answer.Errors[0]["message"]!.GetValue<string>().Should().Contain("no key to join on");
    }

    [Fact]
    public async Task L21_six_lookups_are_refused_MAX_LOOKUP_STAGES_EXCEEDED_and_five_run()
    {
        static string Lookups(int count) => string.Join(", ", Enumerable.Range(0, count).Select(index => $$"""{ "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq{{index}}" } }"""));
        var client = await FleetClient();

        var six = await client.SendAsync(Corpus.Vehicle, $$"""[ {{Lookups(6)}}, { "page": { "limit": 1 } } ]""");
        six.ShouldRefuse("MAX_LOOKUP_STAGES_EXCEEDED", 400);
        six.ErrorCodes.Should().Equal("MAX_LOOKUP_STAGES_EXCEEDED");

        var parent = Corpus.Row(Corpus.Vehicle, "dec-decimal128");
        var five = await client.SendAsync(Corpus.Vehicle, $$"""[ { "match": { "id": { "eq": "{{parent.WireId}}" } } }, {{Lookups(5)}}, { "project": { "id": 1, "eq0": 1, "eq4": 1 } }, { "page": { "limit": 1 } } ]""");
        five.ShouldHaveIds([parent.Id]);
        IdsIn(five.Items[0]!["eq4"]).Should().Equal(IdsOf(ChildrenOf(parent.Id)));
    }

    /// <summary>The whole engine under the fleet model with <c>equipment</c> retired onto <c>fleet.equipment</c>, over the shared fleet database (read only): the lab model declares no retired ids.</summary>
    private static async Task<(EngineDirect Direct, IQueryEngine Engine)> RetiredEquipmentEngineAsync()
    {
        var findings = new List<BuildFinding>();
        var declarations = EntityScanner.Scan([typeof(LabService).Assembly], findings).Where(declaration => declaration.Id.StartsWith("fleet.", StringComparison.Ordinal)).ToList();
        var model = ClrModelBuilder.Build(declarations, new Dictionary<string, IReadOnlyList<string>> { [Corpus.Equipment] = ["equipment"] });
        var direct = new EngineDirect(model);
        var database = await (await CorpusFleet.SharedAsync()).Fleet.DatabaseAsync(LabService.Fleet);

        return (direct, direct.Engine(await MongoFixture.ClientAsync(), database.DatabaseNamespace.DatabaseName));
    }

    private static async Task<QueryResult> LookupThroughRetiredIdAsync(CorpusRow parent)
    {
        var (direct, engine) = await RetiredEquipmentEngineAsync();
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "equipment", "path": "vehicle.id", "as": "eq", "select": ["name"] } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 1 } } ]
            """), direct.Context());

        return outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;
    }

    [Fact]
    public async Task L07_a_retired_entity_id_resolves_at_the_root_with_a_notice_and_inside_a_lookup_from()
    {
        var (direct, engine) = await RetiredEquipmentEngineAsync();

        var root = await engine.ExecuteAsync(EngineDirect.Request("equipment", """[ { "project": { "id": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]"""), direct.Context());
        var rootResult = root.Should().BeOfType<QueryOutcome.Success>().Subject.Result;
        rootResult.Items.Select(item => Guid.Parse(item!["id"]!.GetValue<string>())).Should().Equal(Corpus.AllIds(Corpus.Equipment));
        rootResult.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Equal("ENTITY_ID_RETIRED");

        var parent = Corpus.Row(Corpus.Vehicle, "dec-decimal128");
        var joined = await LookupThroughRetiredIdAsync(parent);

        IdsIn(joined.Items[0]!["eq"]).Should().Equal(IdsOf(ChildrenOf(parent.Id)), "the retired id resolves inside lookup.from as it does at the root");
    }

    [Fact]
    public async Task L07_J4_a_retired_entity_id_inside_lookup_from_carries_the_same_notice_as_at_the_root()
    {
        var joined = await LookupThroughRetiredIdAsync(Corpus.Row(Corpus.Vehicle, "dec-decimal128"));

        (joined.Diagnostics ?? []).Select(diagnostic => diagnostic.Code).Should().Equal(["ENTITY_ID_RETIRED"], "a retired id is announced wherever the caller wrote it");
    }

    [Fact]
    public async Task L19_every_lookup_child_entity_stores_a_root_organisation_so_the_unscoped_child_is_unreachable()
    {
        // L19 needs a child with no stored root organizationId; every lab entity has one, which
        // is asserted here so the case is not silently assumed.
        var answer = await (await FleetClient()).SendAsync(Corpus.Equipment, """[ { "project": { "organizationId": 1 } }, { "page": { "limit": 1 } } ]""");

        answer.ShouldBeOk().Strings("organizationId").Should().Equal(Org.A.Id().Wire());
    }

    [Fact]
    public async Task L22_L23_the_count_ignores_a_lookup_nothing_reads_and_counts_through_one_a_later_stage_reads()
    {
        var client = await FleetClient();

        var unread = await client.SendAsync(Corpus.Vehicle, """
            [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq" } }, { "project": { "id": 1 } }, { "page": { "limit": 3, "includeTotalCount": true } } ]
            """);
        unread.ShouldHaveTotal(Vehicles().Count);

        var wanted = Corpus.Text(Equipment()[0], "name")!;
        var expected = Vehicles().Count(vehicle => ChildrenOf(vehicle.Id).Any(child => Corpus.Text(child, "name") == wanted));
        expected.Should().BeInRange(1, Vehicles().Count - 1);

        var read = await client.SendAsync(Corpus.Vehicle, $$"""
            [ { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"] } },
              { "match": { "eq.name": { "eq": "{{wanted}}" } } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 3, "includeTotalCount": true } } ]
            """);
        read.ShouldHaveTotal(expected);
    }

    [Fact]
    public async Task L24_a_fan_out_produces_the_parent_id_once_per_child_once_the_alias_is_unwound()
    {
        var parent = TwoChildParent();

        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"] } },
              { "unwind": { "path": "eq" } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds([parent.Id, parent.Id]);
        answer.Items.Select(row => Guid.Parse(row!["eq"]!["id"]!.GetValue<string>())).Should().Equal(IdsOf(ChildrenOf(parent.Id)));
    }

    [Fact]
    public async Task L27_the_child_rows_have_the_same_wire_encoding_as_a_root_row_of_the_same_entity()
    {
        var parent = TwoChildParent();
        var client = await FleetClient();

        var joined = await client.SendAsync(Corpus.Vehicle, $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name", "number", "createDateTime", "organizationId"] } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 1 } } ]
            """);
        var child = joined.ShouldHaveIds([parent.Id]).Items[0]!["eq"]![0]!.AsObject();

        var asRoot = await client.SendAsync(Corpus.Equipment, $$"""
            [ { "match": { "id": { "eq": "{{child["id"]!.GetValue<string>()}}" } } },
              { "project": { "id": 1, "name": 1, "number": 1, "createDateTime": 1, "organizationId": 1 } },
              { "page": { "limit": 1 } } ]
            """);
        var root = asRoot.ShouldBeOk().Items[0]!.AsObject();

        foreach (var member in new[] { "id", "name", "number", "createDateTime", "organizationId" })
            child[member]!.ToJsonString().Should().Be(root[member]!.ToJsonString(), $"{member}: a lookup alias encodes as the root does");

        // And the encoding itself, so the case still says something if both halves change together.
        child["organizationId"]!.GetValue<string>().Should().Be(Org.A.Id().Wire());
        child["createDateTime"]!.GetValue<string>().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
        child["number"]!.GetValue<string>().Should().Be(Corpus.Text(ChildrenOf(parent.Id)[0], "number"));
    }

    [Fact]
    public async Task L29_a_variable_inside_a_lookup_filter_is_bound_and_filters_the_children()
    {
        var parent = TwoChildParent();
        var wanted = Corpus.Text(ChildrenOf(parent.Id)[0], "name")!;
        var expected = IdsOf(ChildrenOf(parent.Id).Where(child => Corpus.Text(child, "name") == wanted));
        var client = await FleetClient();
        var pipeline = $$"""
            [ { "match": { "id": { "eq": "{{parent.WireId}}" } } },
              { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq", "select": ["name"], "filter": { "name": { "eq": { "$var": "wanted" } } } } },
              { "project": { "id": 1, "eq": 1 } },
              { "page": { "limit": 2 } } ]
            """;

        var bound = await client.SendAsync(Corpus.Vehicle, pipeline, new JsonObject { ["wanted"] = wanted });
        IdsIn(bound.ShouldHaveIds([parent.Id]).Items[0]!["eq"]).Should().Equal(expected);

        var unbound = await client.SendAsync(Corpus.Vehicle, pipeline);
        unbound.ShouldRefuse("UNBOUND_VARIABLE", 400);
    }

    [Fact]
    public async Task L01_the_same_join_on_the_conformance_entity_whose_child_declares_parentId()
    {
        var parents = Corpus.Rows(Corpus.Conformance);
        var children = Corpus.Rows(Corpus.ConformanceChild);
        var expected = parents.Select(parent => IdsOf(children.Where(child => Corpus.GuidAt(child, "parentId") == parent.Id).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)))).ToList();
        expected.Select(ids => ids.Count).Should().Equal(2, 1, 0);

        var answer = await (await Lab.ClientAsync(LabService.Conformance)).SendAsync(Corpus.Conformance, """
            [ { "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids" } },
              { "project": { "id": 1, "name": 1, "kids": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(parents));

        for (var index = 0; index < expected.Count; index++)
            IdsIn(answer.Items[index]!["kids"]).Should().Equal(expected[index], parents[index].Key);

        // The child's display member is name, so the default select is {id, name}; and B's
        // child, whose parent id names an A row, is hidden by the child's own scope.
        answer.Items[0]!["kids"]![0]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "name"]);
    }

    private static IReadOnlyList<Guid> IdsOf(IEnumerable<CorpusRow> rows) => rows.Select(row => row.Id).ToList();
}
