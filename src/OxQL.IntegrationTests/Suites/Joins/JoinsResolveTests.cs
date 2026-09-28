using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// Area M on the shared fleet: <c>resolve</c>, local and remote, and the semi-join, read only.
/// <list type="bullet">
/// <item>local: <c>fleet.equipment#vehicle.id</c> onto <c>fleet.vehicle</c>,
/// <c>fleet.vehicle#department.id</c> onto <c>fleet.department</c>, and on the conformance
/// service <c>refCode</c> onto <c>conformance.ref</c> (keyed on <c>code</c>) and <c>childId</c>
/// onto <c>conformance.child</c>;</item>
/// <item>remote: <c>transport.shipment_template#createUserId</c> onto <c>fleet.vehicle</c>,
/// <c>transport.shipment#department.id</c> onto <c>fleet.department</c>, and
/// <c>conformance.entity#employeeId</c> onto <c>staff.employee</c> and
/// <c>#widgetCodeExplicit</c> onto the owner's <c>owner.widget</c>.</item>
/// </list>
/// Ported from the legacy <c>joins-m-resolve</c> battery; its 26 red cases were red because the
/// vendored client modules carried no references. The owner-call cases are in
/// <see cref="JoinsRemoteOwnerTests"/>, on a private fleet whose owner can be counted.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsResolveTests
{
    private static Task<LabClient> FleetClient() => Lab.ClientAsync(LabService.Fleet);

    private static Task<LabClient> TransportClient() => Lab.ClientAsync(LabService.Transport);

    private static Task<LabClient> ConformanceClient() => Lab.ClientAsync(LabService.Conformance);

    private static readonly Comparer<CorpusRow> ById = Comparer<CorpusRow>.Create(Corpus.CompareIds);

    /// <summary>The vehicle a row's reference names in its own organisation, or null.</summary>
    private static CorpusRow? Target(string entity, CorpusRow row, string path, Org org = Org.A) =>
        Corpus.GuidAt(row, path) is { } key ? Corpus.Rows(entity, org).FirstOrDefault(target => target.Id == key) : null;

    private static IReadOnlyList<CorpusRow> EquipmentById() => Corpus.Rows(Corpus.Equipment).Order(ById).ToList();

    /// <summary>The first 24 templates: ordinals 1–20 name a vehicle (four real ones, every fifth dangling), 21–24 the lab user.</summary>
    private static IReadOnlyList<CorpusRow> TemplateHead() =>
        Corpus.Where(Corpus.Template, row => Corpus.Text(row, "templateName") is { } name && Order.CompareCollated(name, "T-000024") <= 0).Order(ById).ToList();

    private static Guid? AliasId(JsonNode? row, string alias, string key = "id") =>
        row?[alias] is JsonObject target ? Guid.Parse(target[key]!.GetValue<string>()) : null;

    private static IReadOnlyList<Guid> IdsOf(IEnumerable<CorpusRow> rows) => rows.Select(row => row.Id).ToList();

    // ── local resolve ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task M01_M10_M20_M21_a_local_resolve_answers_one_target_per_row_null_where_the_id_dangles_and_null_where_the_member_is_absent()
    {
        var rows = EquipmentById();
        var expected = rows.Select(row => (row.Id, Key: Corpus.GuidAt(row, "vehicle.id"), Target: Target(Corpus.Vehicle, row, "vehicle.id"))).ToList();
        expected.Count(row => row.Target is not null).Should().Be(3, "the corpus has three resolvable rows");
        expected.Count(row => row.Key is not null && row.Target is null).Should().Be(1, "M21 needs a dangling id");
        expected.Count(row => row.Key is null).Should().Be(1, "M20 needs a row whose reference member is absent");

        var answer = await (await FleetClient()).SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh" } },
              { "project": { "id": 1, "name": 1, "veh": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(expected.Select(row => row.Id));

        for (var index = 0; index < expected.Count; index++)
        {
            var (_, key, target) = expected[index];
            var veh = answer.Items[index]!["veh"];

            if (target is null)
            {
                veh.Should().BeNull($"{rows[index].Key}: {(key is null ? "the member is absent" : "the id dangles")}");
                continue;
            }

            AliasId(answer.Items[index], "veh").Should().Be(target.Id, rows[index].Key);
            veh!["matchCode"]!.GetValue<string>().Should().Be(Corpus.Text(target, "matchCode"));
            // M10: no select means the target's key plus its display member.
            veh.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "matchCode"]);
        }
    }

    [Fact]
    public async Task M09_select_keeps_the_named_target_paths_and_the_target_key()
    {
        var rows = EquipmentById();

        var answer = await (await FleetClient()).SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh", "select": ["location"] } },
              { "project": { "id": 1, "veh": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(rows));

        for (var index = 0; index < rows.Count; index++)
        {
            var target = Target(Corpus.Vehicle, rows[index], "vehicle.id");
            var veh = answer.Items[index]!["veh"];

            if (target is null)
            {
                veh.Should().BeNull(rows[index].Key);
                continue;
            }

            veh!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "location"], "the key is kept and nothing else is added");
            veh["location"]!.GetValue<string>().Should().Be(Corpus.Text(target, "location"));
        }
    }

    [Fact]
    public async Task M11_a_filter_on_a_local_target_nulls_the_alias_for_a_non_match_and_keeps_the_row()
    {
        var rows = EquipmentById();
        const string Wanted = "VEH-001";
        var expected = rows.Select(row => Target(Corpus.Vehicle, row, "vehicle.id") is { } target && Corpus.Text(target, "matchCode") == Wanted ? target.Id : (Guid?)null).ToList();
        expected.Should().Contain(id => id != null).And.Contain(id => id == null);

        var answer = await (await FleetClient()).SendAsync(Corpus.Equipment, $$"""
            [ { "resolve": { "path": "vehicle.id", "as": "veh", "filter": { "matchCode": { "eq": "{{Wanted}}" } } } },
              { "project": { "id": 1, "veh": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(rows), "every row survives; only the alias changes");
        answer.Items.Select(row => AliasId(row, "veh")).Should().Equal(expected);
    }

    [Fact]
    public async Task M01_M09_M11_a_local_resolve_filter_and_the_alias_bind_against_the_target_shape()
    {
        var client = await FleetClient();

        // A path only the target has binds; a path only the source has does not.
        (await client.SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh", "filter": { "mileage": { "eq": 1 } } } }, { "page": { "limit": 1 } } ]
            """)).ShouldBeOk();

        (await client.SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh", "filter": { "number": { "eq": "EQ-001" } } } }, { "page": { "limit": 1 } } ]
            """)).ShouldRefuse("UNKNOWN_PATH", 400);

        // The alias is the target's shape, not an open bag: a member it lacks is refused.
        (await client.SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh" } }, { "match": { "veh.anything": { "eq": "x" } } }, { "page": { "limit": 1 } } ]
            """)).ShouldRefuse("UNKNOWN_PATH", 400);
    }

    [Fact]
    public async Task M12_a_local_resolve_alias_orders_the_page_and_a_filter_on_it_narrows_the_page_and_the_count()
    {
        var rows = EquipmentById();
        var labelled = rows.Select(row => (Row: row, MatchCode: Target(Corpus.Vehicle, row, "vehicle.id") is { } target ? Corpus.Text(target, "matchCode") : null)).ToList();

        // Descending on the target's display member, then the id: a missing alias is the lowest
        // BSON bracket, so it comes last on desc.
        var expectedOrder = labelled.Order(Comparer<(CorpusRow Row, string? MatchCode)>.Create((a, b) =>
        {
            var primary = (a.MatchCode, b.MatchCode) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                var (left, right) => -Order.CompareCollated(left, right),
            };

            return primary != 0 ? primary : Corpus.CompareIds(a.Row, b.Row);
        })).Select(entry => entry.Row.Id).ToList();

        var client = await FleetClient();

        var sorted = await client.SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh" } },
              { "sort": [ { "veh.matchCode": "desc" } ] },
              { "project": { "id": 1, "veh": 1 } },
              { "page": { "limit": 10 } } ]
            """);
        sorted.ShouldHaveIds(expectedOrder);

        const string Wanted = "VEH-001";
        var expectedRows = labelled.Where(entry => entry.MatchCode == Wanted).Select(entry => entry.Row.Id).ToList();
        expectedRows.Should().HaveCountGreaterThan(0).And.HaveCountLessThan(rows.Count);

        var filtered = await client.SendAsync(Corpus.Equipment, $$"""
            [ { "resolve": { "path": "vehicle.id", "as": "veh" } },
              { "match": { "veh.matchCode": { "eq": "{{Wanted}}" } } },
              { "project": { "id": 1, "veh": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);
        filtered.ShouldHaveIds(expectedRows).ShouldHaveTotal(expectedRows.Count, because: "the alias condition reached the count too");
    }

    [Fact]
    public async Task M13_M15_a_local_resolve_leaves_the_paging_mode_alone_and_a_root_shape_still_pages_by_cursor()
    {
        var all = IdsOf(EquipmentById());
        var walk = await (await FleetClient()).WalkAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh" } }, { "project": { "id": 1, "veh": 1 } }, { "sort": [ { "id": "asc" } ] } ]
            """, limit: 2);

        walk.Ids.Should().Equal(all);
        walk.Pages.Should().Be(3);
        walk.Cursors.Should().OnlyContain(cursor => cursor.Contains('.'), "a signed keyset cursor, not an offset");
    }

    [Fact]
    public async Task M12_a_second_local_target_on_the_same_service_resolves_beside_the_first()
    {
        var vehicles = Corpus.Rows(Corpus.Vehicle).Order(ById).ToList();
        var expected = vehicles.Select(vehicle => Target(Corpus.Department, vehicle, "department.id")).ToList();
        expected.Should().OnlyContain(target => target != null, "every corpus vehicle names a department");

        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, """
            [ { "resolve": { "path": "department.id", "as": "dep", "select": ["name"] } },
              { "project": { "id": 1, "dep": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 100 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(vehicles));
        answer.Items.Select(row => AliasId(row, "dep")).Should().Equal(expected.Select(target => (Guid?)target!.Id));
        answer.Strings("dep.name").Should().Equal(expected.Select(target => Corpus.Text(target!, "name")));
    }

    [Theory]
    [InlineData("M02", """[ { "resolve": { "path": "createUserId", "as": "x" } }, { "page": { "limit": 1 } } ]""", "RESOLVE_NOT_DECLARED", null)]
    [InlineData("M08", """[ { "resolve": { "path": "vehicle.id", "as": "name" } }, { "page": { "limit": 1 } } ]""", "ALIAS_COLLISION", null)]
    [InlineData("M49", """[ { "resolve": { "path": "vehicle.id", "as": "veh", "limit": 2 } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_STAGE_MEMBER", "path, as, select, filter")]
    [InlineData("M07", """[ { "group": { "by": [ { "path": "name", "as": "n" } ], "fields": { "c": { "count": true } } } }, { "resolve": { "path": "vehicle.id", "as": "veh" } }, { "page": { "limit": 1 } } ]""", "UNKNOWN_PATH", "not an output of the group stage")]
    [InlineData("M47", """[ { "resolve": { "path": "vehicle.id", "as": "a" } }, { "resolve": { "path": "vehicle.id", "as": "b" } }, { "resolve": { "path": "vehicle.id", "as": "c" } }, { "resolve": { "path": "vehicle.id", "as": "d" } }, { "resolve": { "path": "vehicle.id", "as": "e" } }, { "resolve": { "path": "vehicle.id", "as": "f" } }, { "resolve": { "path": "vehicle.id", "as": "g" } }, { "resolve": { "path": "vehicle.id", "as": "h" } }, { "resolve": { "path": "vehicle.id", "as": "i" } }, { "page": { "limit": 1 } } ]""", "MAX_RESOLVE_STAGES_EXCEEDED", "more than 8 resolve stages")]
    public async Task M02_M07_M08_M47_M49_the_engine_refuses_the_local_resolve_shapes_it_cannot_serve(string caseId, string pipeline, string code, string? contains)
    {
        var answer = await (await FleetClient()).SendAsync(Corpus.Equipment, pipeline);

        answer.ShouldRefuse(code, 400, caseId);
        answer.ErrorCodes.Should().Equal([code], caseId);

        if (contains is not null)
            answer.Text.Should().Contain(contains, caseId);
    }

    [Fact]
    public async Task M47_two_resolve_stages_run()
    {
        var rows = EquipmentById();

        var answer = await (await FleetClient()).SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "a" } }, { "resolve": { "path": "vehicle.id", "as": "b" } },
              { "project": { "id": 1, "a": 1, "b": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(rows));
        answer.Items.Select(row => AliasId(row, "a")).Should().Equal(answer.Items.Select(row => AliasId(row, "b")));
    }

    [Fact]
    public async Task M48_every_resolve_target_stores_a_root_organisation_so_the_unscoped_target_is_unreachable()
    {
        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, """[ { "project": { "organizationId": 1 } }, { "page": { "limit": 1 } } ]""");

        answer.ShouldBeOk().Strings("organizationId").Should().Equal(Org.A.Id().Wire());
    }

    [Fact]
    public async Task J1_an_inclusion_projection_that_leaves_out_a_local_resolve_alias_leaves_it_out_of_the_row()
    {
        var resolvable = EquipmentById().Where(row => Target(Corpus.Vehicle, row, "vehicle.id") is not null).ToList();
        resolvable.Should().HaveCount(3);

        var answer = await (await FleetClient()).SendAsync(Corpus.Equipment, $$"""
            [ { "match": { "id": { "in": {{Json.Ids(IdsOf(resolvable))}} } } },
              { "resolve": { "path": "vehicle.id", "as": "veh", "select": ["matchCode"] } },
              { "project": { "id": 1, "name": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        // The targets exist, so a null under the alias would say "dangling" of a row that is not;
        // the projection did not name the alias, so the row carries only what it named.
        answer.ShouldHaveIds(IdsOf(resolvable));
        answer.Items.Should().AllSatisfy(row => row!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "name"], answer.ToString()));
    }

    [Fact]
    public async Task J1_projecting_the_alias_or_its_leaf_keeps_it_and_a_projection_before_the_resolve_does_not_touch_it()
    {
        var resolvable = EquipmentById().Where(row => Target(Corpus.Vehicle, row, "vehicle.id") is not null).ToList();
        var expected = resolvable.Select(row => Corpus.Text(Target(Corpus.Vehicle, row, "vehicle.id")!, "matchCode")).ToList();
        var client = await FleetClient();
        var match = $$"""{ "match": { "id": { "in": {{Json.Ids(IdsOf(resolvable))}} } } }""";

        foreach (var (why, pipeline) in new[]
        {
            ("the alias projected", $$"""[ {{match}}, { "resolve": { "path": "vehicle.id", "as": "veh", "select": ["matchCode"] } }, { "project": { "id": 1, "veh": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]"""),
            ("the alias's leaf projected", $$"""[ {{match}}, { "resolve": { "path": "vehicle.id", "as": "veh", "select": ["matchCode"] } }, { "project": { "id": 1, "veh.matchCode": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]"""),
            ("a projection before the resolve", $$"""[ {{match}}, { "project": { "id": 1, "vehicle.id": 1 } }, { "resolve": { "path": "vehicle.id", "as": "veh", "select": ["matchCode"] } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]"""),
        })
        {
            var answer = await client.SendAsync(Corpus.Equipment, pipeline);

            answer.ShouldHaveIds(IdsOf(resolvable), why);
            answer.Strings("veh.matchCode").Should().Equal(expected, why);
        }
    }

    [Fact]
    public async Task M01_M09_M10_a_local_resolve_onto_a_target_keyed_on_code_joins_on_that_key_and_defaults_to_it()
    {
        var rows = Corpus.Rows(Corpus.Conformance);
        var refs = Corpus.Rows(Corpus.ConformanceRef);
        var expected = rows.Select(row => refs.FirstOrDefault(target => Corpus.Text(target, "code") == Corpus.Text(row, "refCode"))).ToList();
        expected.Select(target => target is null ? null : Corpus.Text(target, "code")).Should().Equal("REF-A", "REF-B", null);

        var answer = await (await ConformanceClient()).SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "refCode", "as": "r" } },
              { "project": { "id": 1, "name": 1, "r": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(rows));
        answer.Strings("r.code").Should().Equal(expected.Select(target => target is null ? null : Corpus.Text(target, "code")));
        answer.Strings("r.name").Should().Equal(expected.Select(target => target is null ? null : Corpus.Text(target, "name")));
        answer.Items[0]!["r"]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["code", "name"], "M10 for a key that is not id");
    }

    [Fact]
    public async Task M20_M21_on_one_entity_a_hit_a_dangling_key_and_a_null_key_each_answer_what_the_contract_reserves()
    {
        var rows = Corpus.Rows(Corpus.Conformance);
        var expectedChild = rows.Select(row => Target(Corpus.ConformanceChild, row, "childId")).ToList();
        expectedChild.Select(target => target is null ? null : Corpus.Text(target, "name")).Should().Equal("Child one", "Child three", null);

        var expectedEmployee = rows.Select(row => Target(Corpus.Employee, row, "employeeId")).ToList();
        expectedEmployee.Select(target => target is not null).Should().Equal(true, false, false);
        rows.Select(row => Corpus.GuidAt(row, "employeeId") is null).Should().Equal([false, false, true], "c-bravo's employeeId dangles and c-charlie's is null");

        var client = await ConformanceClient();

        var child = await client.SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "childId", "as": "c" } }, { "project": { "id": 1, "c": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]
            """);
        child.ShouldHaveIds(IdsOf(rows)).Strings("c.name").Should().Equal(expectedChild.Select(target => target is null ? null : Corpus.Text(target, "name")));

        // employeeId is REMOTE: the owning staff host answers the hit; the dangling key and the
        // null key are both null under the alias, and neither is a diagnostic.
        var remote = await client.SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "employeeId", "as": "e", "select": ["matchCode"] } }, { "project": { "id": 1, "e": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]
            """);
        remote.ShouldHaveIds(IdsOf(rows)).ShouldHaveNoDiagnostics();
        remote.Items.Select(row => AliasId(row, "e")).Should().Equal(expectedEmployee.Select(target => target?.Id));
        remote.Strings("e.matchCode").Should().Equal(expectedEmployee.Select(target => target is null ? null : Corpus.Text(target, "matchCode")));
    }

    // ── remote resolve ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task M16_M19_M21_M35_a_cross_service_resolve_answers_the_owners_rows_and_null_where_the_key_dangles()
    {
        var templates = TemplateHead();
        templates.Should().HaveCount(24);
        var expected = templates.Select(template => Target(Corpus.Vehicle, template, "createUserId")).ToList();
        expected.Count(target => target is not null).Should().Be(16, "ordinals 1–20 name four real vehicles, every fifth dangling");
        expected.Where(target => target is not null).Select(target => target!.Id).Distinct().Should().HaveCount(4, "M19: four distinct keys answer sixteen rows");

        var answer = await (await TransportClient()).SendAsync(Corpus.Template, """
            [ { "match": { "templateName": { "lte": "T-000024" } } },
              { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "project": { "id": 1, "templateName": 1, "veh": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 50 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(templates)).ShouldHaveNoDiagnostics();
        // M35: the guid key travels as the wire string and comes back as the target's id.
        answer.Items.Select(row => AliasId(row, "veh")).Should().Equal(expected.Select(target => target?.Id));
        answer.Strings("veh.matchCode").Should().Equal(expected.Select(target => target is null ? null : Corpus.Text(target, "matchCode")));
    }

    [Fact]
    public async Task M16_M20_M21_a_remote_department_resolve_hits_misses_and_skips_a_null_key_without_a_diagnostic()
    {
        var shipments = Corpus.Rows(Corpus.Shipment).Order(ById).ToList();
        var expected = shipments.Select(row => Target(Corpus.Department, row, "department.id")).ToList();
        shipments.Where(row => Corpus.GuidAt(row, "department.id") is null).Should().ContainSingle("M20: enum-absent carries no department");
        shipments.Where(row => Corpus.GuidAt(row, "department.id") is not null && Target(Corpus.Department, row, "department.id") is null).Should().ContainSingle("M21: enum-booking's department dangles");
        expected.Where(target => target is not null).Select(target => target!.Id).Distinct().Should().HaveCount(3, "every department of A is named");

        var answer = await (await TransportClient()).SendAsync(Corpus.Shipment, """
            [ { "resolve": { "path": "department.id", "as": "dep", "select": ["name"] } },
              { "project": { "id": 1, "dep": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 100 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(shipments)).ShouldHaveNoDiagnostics("a dangling or missing remote key is not a diagnostic");
        answer.Items.Select(row => AliasId(row, "dep")).Should().Equal(expected.Select(target => target?.Id));
        answer.Strings("dep.name").Should().Equal(expected.Select(target => target is null ? null : Corpus.Text(target, "name")));
    }

    [Fact]
    public async Task M16_a_remote_resolve_is_scoped_at_the_owner_to_the_callers_organisation()
    {
        var shipments = Corpus.Rows(Corpus.Shipment, Org.B).Order(ById).ToList();
        var expected = shipments.Select(row => Target(Corpus.Department, row, "department.id", Org.B)?.Id).ToList();
        expected.Should().OnlyContain(id => id != null);

        var answer = await (await TransportClient()).As(Org.B).SendAsync(Corpus.Shipment, """
            [ { "resolve": { "path": "department.id", "as": "dep" } }, { "project": { "id": 1, "dep": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(IdsOf(shipments));
        answer.Items.Select(row => AliasId(row, "dep")).Should().Equal(expected);
    }

    [Fact]
    public async Task M17_the_engine_refuses_a_sort_under_a_remote_alias_with_RESOLVE_NOT_SORTABLE()
    {
        var answer = await (await TransportClient()).SendAsync(Corpus.Template, """
            [ { "resolve": { "path": "createUserId", "as": "veh" } }, { "sort": [ { "veh.matchCode": "asc" } ] }, { "page": { "limit": 1 } } ]
            """);

        answer.ShouldRefuse("RESOLVE_NOT_SORTABLE", 400);
        answer.ErrorCodes.Should().Equal("RESOLVE_NOT_SORTABLE");
        answer.Text.Should().Contain("the owner's rows cannot order this host's page");
    }

    [Fact]
    public async Task M37_M39_a_semi_join_asks_the_owner_for_the_condition_and_its_ids_reach_the_page_and_the_count()
    {
        const string Wanted = "VEH-002";
        var expected = IdsOf(TemplateHead().Where(template => Target(Corpus.Vehicle, template, "createUserId") is { } target && Corpus.Text(target, "matchCode") == Wanted));
        expected.Should().HaveCountGreaterThan(1, "several templates point at one vehicle");

        var answer = await (await TransportClient()).SendAsync(Corpus.Template, $$"""
            [ { "match": { "templateName": { "lte": "T-000024" } } },
              { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "match": { "veh.matchCode": { "eq": "{{Wanted}}" } } },
              { "project": { "id": 1, "templateName": 1, "veh": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 50, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count, because: "M39: the owner's ids reached the count too");
        answer.Strings("veh.matchCode").Should().AllBe(Wanted);
    }

    [Fact]
    public async Task M37_a_semi_join_over_a_target_value_two_vehicles_share_keeps_the_rows_of_both()
    {
        // VEH-001 is the matchCode of two vehicles (the baseline and its duplicate); only the
        // baseline is named by templates, and the semi-join must not lose it to the duplicate.
        const string Wanted = "VEH-001";
        Corpus.Where(Corpus.Vehicle, row => Corpus.Text(row, "matchCode") == Wanted).Should().HaveCount(2);
        var expected = IdsOf(TemplateHead().Where(template => Target(Corpus.Vehicle, template, "createUserId") is { } target && Corpus.Text(target, "matchCode") == Wanted));
        expected.Should().NotBeEmpty();

        var answer = await (await TransportClient()).SendAsync(Corpus.Template, $$"""
            [ { "match": { "templateName": { "lte": "T-000024" } } },
              { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "match": { "veh.matchCode": { "eq": "{{Wanted}}" } } },
              { "project": { "id": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 50, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
    }

    [Fact]
    public async Task M38_the_case_rule_travels_to_the_owner_ignoreCase_and_the_default_fold_and_caseSensitive_does_not()
    {
        // Contract 2 folds case by default, so the legacy "without ignoreCase nothing matches"
        // is now written as the opt-out: caseSensitive.
        const string Exact = "VEH-002";
        var lowered = Exact.ToLowerInvariant();
        var expected = IdsOf(TemplateHead().Where(template => Target(Corpus.Vehicle, template, "createUserId") is { } target && Corpus.Text(target, "matchCode") == Exact));
        expected.Should().NotBeEmpty();
        var client = await TransportClient();

        Task<WireAnswer> Ask(string options) => client.SendAsync(Corpus.Template, $$"""
            [ { "match": { "templateName": { "lte": "T-000024" } } },
              { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "match": { "veh.matchCode": { "eq": "{{lowered}}"{{options}} } } },
              { "project": { "id": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 50, "includeTotalCount": true } } ]
            """);

        (await Ask(""", "options": { "ignoreCase": true }""")).ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
        (await Ask("")).ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
        (await Ask(""", "options": { "caseSensitive": true }""")).ShouldHaveIds([]).ShouldHaveTotal(0);
    }

    [Fact]
    public async Task M36_a_projection_that_drops_the_reference_member_still_resolves_and_the_member_stays_absent()
    {
        var first = TemplateHead().First(template => Target(Corpus.Vehicle, template, "createUserId") is not null);

        var answer = await (await TransportClient()).SendAsync(Corpus.Template, $$"""
            [ { "match": { "id": { "eq": "{{first.WireId}}" } } },
              { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "project": { "id": 1, "veh": 1 } },
              { "page": { "limit": 2 } } ]
            """);

        answer.ShouldHaveIds([first.Id]);
        AliasId(answer.Items[0], "veh").Should().Be(Target(Corpus.Vehicle, first, "createUserId")!.Id);
        answer.Items[0]!.AsObject().ContainsKey("createUserId").Should().BeFalse("the reference member was not projected");
    }

    [Fact]
    public async Task M18_J2_a_join_select_is_bound_locally_as_a_400_and_travels_to_a_remote_owner_whose_refusal_is_a_422()
    {
        var local = await (await FleetClient()).SendAsync(Corpus.Equipment, """
            [ { "resolve": { "path": "vehicle.id", "as": "veh", "select": ["nope"] } }, { "page": { "limit": 2 } } ]
            """);
        local.ShouldRefuse("UNKNOWN_PATH", 400);
        local.ErrorCodes.Should().Equal("UNKNOWN_PATH");

        var transport = await TransportClient();

        var remote = await transport.SendAsync(Corpus.Template, """
            [ { "match": { "templateName": { "eq": "T-000001" } } }, { "resolve": { "path": "createUserId", "as": "veh", "select": ["nope.nothing"] } }, { "page": { "limit": 2 } } ]
            """);
        remote.ShouldRefuse("RESOLVE_REFUSED", 422, "the owner refused, and its refusal became ours");
        remote.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "UNKNOWN_PATH");
        remote.Text.Should().Contain("is not a path of fleet.vehicle");

        var remoteFilter = await transport.SendAsync(Corpus.Template, """
            [ { "match": { "templateName": { "eq": "T-000001" } } }, { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"], "filter": { "nope": { "eq": 1 } } } }, { "page": { "limit": 2 } } ]
            """);
        remoteFilter.ShouldRefuse("RESOLVE_REFUSED", 422);
        remoteFilter.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "UNKNOWN_PATH");
    }

    [Fact]
    public async Task M50_a_match_on_the_remote_alias_root_is_refused_at_bind_time()
    {
        // The alias is the owner's row, not one of its paths; the engine used to send the
        // owner a match on 'veh' and answer its 422 UNKNOWN_PATH refusal.
        var answer = await (await TransportClient()).SendAsync(Corpus.Template, """
            [ { "resolve": { "path": "createUserId", "as": "veh" } }, { "match": { "veh": { "eq": "x" } } }, { "page": { "limit": 1 } } ]
            """);

        var error = answer.ShouldRefuse("RESOLVE_NOT_FILTERABLE", 400);
        answer.ErrorCodes.Should().Equal("RESOLVE_NOT_FILTERABLE");
        error["path"]!.GetValue<string>().Should().Be("veh");
    }

    [Fact]
    public async Task M06_a_resolve_whose_path_sits_under_a_remote_alias_continues_the_chain_at_the_owner()
    {
        // The owner of the vehicle runs the department's resolve for the vehicles it was sent
        // (DESIGN §3.5.3): a local resolve there, lifted here beside 'veh'.
        var answer = await (await TransportClient()).SendAsync(Corpus.Template, """
            [ { "resolve": { "path": "createUserId", "as": "veh" } }, { "resolve": { "path": "veh.department.id", "as": "dep" } }, { "page": { "limit": 1 } } ]
            """);

        var row = answer.ShouldBeOk().ShouldHaveNoDiagnostics().Items.Should().ContainSingle().Subject!;
        row["dep"]!["name"]!.GetValue<string>().Should().Be("Fleet");
        row["veh"]!.AsObject().ContainsKey("dep").Should().BeFalse("the continued alias is the row's, not the vehicle's");
    }

    [Fact]
    public async Task M34_M51_two_remote_resolves_onto_two_services_and_a_remote_resolve_beside_a_local_one()
    {
        // The legacy template's second target was absent from its entity; the conformance entity
        // carries two remote references onto two services (staff and the owner) and a local one.
        var rows = Corpus.Rows(Corpus.Conformance);
        var employees = rows.Select(row => Target(Corpus.Employee, row, "employeeId")?.Id).ToList();
        var widgets = rows.Select(row => Corpus.Text(row, "widgetCodeExplicit")).ToList();
        var refs = rows.Select(row => Corpus.Text(row, "refCode") is { } code && Corpus.Rows(Corpus.ConformanceRef).Any(target => Corpus.Text(target, "code") == code) ? code : null).ToList();
        employees.Should().Contain(id => id != null);

        var client = await ConformanceClient();

        var twoRemote = await client.SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "employeeId", "as": "e", "select": ["matchCode"] } },
              { "resolve": { "path": "widgetCodeExplicit", "as": "w", "select": ["name"] } },
              { "project": { "id": 1, "e": 1, "w": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 5 } } ]
            """);
        twoRemote.ShouldHaveIds(IdsOf(rows)).ShouldHaveNoDiagnostics();
        twoRemote.Items.Select(row => AliasId(row, "e")).Should().Equal(employees);
        twoRemote.Strings("w.code").Should().Equal(widgets);
        twoRemote.Strings("w.name").Should().Equal(widgets.Select(code => ChaosOwner.Widgets.First(widget => widget.Code == code).Name));

        var mixed = await client.SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "refCode", "as": "r" } },
              { "resolve": { "path": "employeeId", "as": "e", "select": ["matchCode"] } },
              { "project": { "id": 1, "r": 1, "e": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 5 } } ]
            """);
        mixed.ShouldHaveIds(IdsOf(rows));
        mixed.Strings("r.code").Should().Equal(refs);
        mixed.Items.Select(row => AliasId(row, "e")).Should().Equal(employees);
    }

    [Fact]
    public async Task F_ENT_001_a_remote_reference_that_names_no_target_field_is_refused_and_its_declared_twin_joins_on_code()
    {
        var rows = Corpus.Rows(Corpus.Conformance);
        var client = await ConformanceClient();

        var fieldless = await client.SendAsync(Corpus.Conformance, """[ { "resolve": { "path": "widgetCode", "as": "w", "select": ["name"] } }, { "page": { "limit": 5 } } ]""");
        fieldless.ShouldRefuse("RESOLVE_NOT_DECLARED", 400);
        fieldless.ErrorCodes.Should().Equal("RESOLVE_NOT_DECLARED");

        var expected = rows.Select(row => ChaosOwner.Widgets.First(widget => widget.Code == Corpus.Text(row, "widgetCodeExplicit")).Name).ToList();
        var wrong = rows.Select(row => ChaosOwner.Widgets.First(widget => widget.Id == Corpus.Text(row, "widgetCode")).Name).ToList();
        expected.Should().Equal("Widget One", "Widget Two", "Widget Three");
        wrong.Should().NotEqual(expected, "joining on the decoy id names a different widget for every row");

        var control = await client.SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "widgetCodeExplicit", "as": "w", "select": ["name"] } }, { "project": { "id": 1, "name": 1, "w": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]
            """);
        control.ShouldHaveIds(IdsOf(rows)).Strings("w.name").Should().Equal(expected);
    }

    [Fact]
    public async Task M31_a_host_with_no_remote_query_client_refuses_a_remote_resolve_and_a_semi_join_RESOLVE_UNAVAILABLE()
    {
        var direct = new EngineDirect(LabService.Conformance.Model);
        var database = await (await CorpusFleet.SharedAsync()).Fleet.DatabaseAsync(LabService.Conformance);
        var engine = direct.Engine(await MongoFixture.ClientAsync(), database.DatabaseNamespace.DatabaseName);

        foreach (var pipeline in new[]
        {
            """[ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } }, { "page": { "limit": 5 } } ]""",
            """[ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } }, { "match": { "w.name": { "eq": "Widget One" } } }, { "page": { "limit": 5 } } ]""",
        })
        {
            var outcome = await engine.ExecuteAsync(EngineDirect.Request(Corpus.Conformance, pipeline), direct.Context());
            var refused = outcome.Should().BeOfType<OxQL.Core.Engine.QueryOutcome.Refused>(pipeline).Subject.Refusal;

            refused.Status.Should().Be(422, EngineDirect.Describe(refused));
            (refused.Errors ?? []).Select(error => error.Code).Should().Contain("RESOLVE_UNAVAILABLE", EngineDirect.Describe(refused));
        }
    }

    [Fact]
    public async Task M14_a_reference_onto_the_target_key_can_match_at_most_one_target_row()
    {
        // M14 asks which target wins when several match; every lab reference is onto the target's
        // key, unique by construction, so the question is unreachable. The uniqueness is asserted
        // on the engine's own answer rather than assumed.
        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, """[ { "project": { "id": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 100 } } ]""");

        answer.ShouldHaveIds(Corpus.AllIds(Corpus.Vehicle)).Ids().Should().OnlyHaveUniqueItems();
    }
}
