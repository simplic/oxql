using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Hosts;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Organisation;

/// <summary>
/// Area X: organisation scope and isolation, the one area where a defect is a security defect.
/// Organisation B clones A's rows on the members a case filters by, so a scope that stops firing
/// shows up as extra rows, never as an empty answer. The organisation is the request's
/// <c>OrganizationId</c> (the legacy rig minted a second token; the engine sees the same thing:
/// the scope provider's answer). Every id set is the corpus's, stated before the request.
/// </summary>
[Trait("Category", "Integration")]
public class OrganisationTests
{
    private static readonly string[] Swept =
    [
        Corpus.Employee, Corpus.Vehicle, Corpus.Equipment, Corpus.Department, Corpus.Status,
        Corpus.Shipment, Corpus.Template, Corpus.Transaction, Corpus.Conformance, Corpus.ConformanceRef, Corpus.ConformanceChild,
    ];

    private static async Task<LabClient> Client(string entity, Org org = Org.A) => await Lab.ClientForAsync(entity, org);

    private static string Tag(Guid id) => id.ToString("D").Split('-')[1];

    [Fact]
    public async Task X13_every_entity_answers_exactly_its_own_organisations_rows_under_either_organisation()
    {
        // The sweep: every entity of the corpus, both organisations, every row walked. A foreign
        // row here is an S1.
        foreach (var entity in Swept)
        {
            var key = Corpus.Entity(entity).KeyPath;

            foreach (var org in new[] { Org.A, Org.B })
            {
                var expected = Corpus.Rows(entity, org).Select(row => row.WireId).Order(StringComparer.Ordinal).ToList();
                expected.Should().NotBeEmpty($"{entity} holds rows in organisation {org}, or the sweep proves nothing");

                var walk = await (await Client(entity, org)).WalkAsync(entity, $$"""[{ "project": { "{{key}}": 1 } }, { "sort": [{ "{{key}}": "asc" }] }]""", 500);
                var actual = walk.Items.Select(item => item[key]!.GetValue<string>()).ToList();

                actual.Should().BeEquivalentTo(expected, $"{entity} under {org}");
            }
        }
    }

    [Fact]
    public async Task X13_the_two_organisations_share_no_row_and_every_id_carries_its_own_organisation_tag()
    {
        foreach (var entity in new[] { Corpus.Employee, Corpus.Vehicle, Corpus.Equipment, Corpus.Department })
        {
            var a = await (await Client(entity, Org.A)).MatchIdsAsync(entity, """{ "id": { "neq": null } }""");
            var b = await (await Client(entity, Org.B)).MatchIdsAsync(entity, """{ "id": { "neq": null } }""");

            a.Should().Equal(Corpus.AllIds(entity, Org.A));
            b.Should().Equal(Corpus.AllIds(entity, Org.B));
            a.Should().OnlyContain(id => Tag(id) == Org.A.Tag());
            b.Should().OnlyContain(id => Tag(id) == Org.B.Tag());
            a.Intersect(b).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task X7_the_count_is_scoped_too_each_organisation_counts_its_own_rows_not_the_collection()
    {
        var (a, b) = Corpus.Counts(Corpus.Employee);
        a.Should().NotBe(b);

        var answerA = await (await Client(Corpus.Employee, Org.A)).SendAsync(Corpus.Employee, """[{ "page": { "limit": 1, "includeTotalCount": true } }]""");
        var answerB = await (await Client(Corpus.Employee, Org.B)).SendAsync(Corpus.Employee, """[{ "page": { "limit": 1, "includeTotalCount": true } }]""");

        answerA.TotalCount.Should().Be(a, answerA.ToString());
        answerB.TotalCount.Should().Be(b, answerB.ToString());
    }

    [Fact]
    public async Task X13_a_filter_cannot_reach_another_organisations_row_by_id_singly_or_in_a_list()
    {
        var foreign = Corpus.AllIds(Corpus.Employee, Org.B);
        var staff = await Client(Corpus.Employee);

        var single = await staff.SendAsync(Corpus.Employee, $$"""[{ "match": { "id": { "eq": "{{foreign[0]}}" } } }, { "page": { "limit": 5 } }]""");
        var list = await staff.SendAsync(Corpus.Employee, $$"""[{ "match": { "id": { "in": {{Json.Ids(foreign)}} } } }, { "page": { "limit": 5 } }]""");

        single.ShouldHaveIds([]);
        list.ShouldHaveIds([]);
    }

    [Fact]
    public async Task X13_a_by_id_read_of_a_foreign_row_answers_nothing_and_of_an_own_row_answers_it()
    {
        // The engine half of the client's byId: a match on one id with a page of one.
        var mine = Corpus.AllIds(Corpus.Employee, Org.A)[0];
        var foreign = Corpus.AllIds(Corpus.Employee, Org.B)[0];
        var staff = await Client(Corpus.Employee);

        (await staff.SendAsync(Corpus.Employee, $$"""[{ "match": { "id": { "eq": "{{foreign}}" } } }, { "page": { "limit": 1 } }]""")).ShouldHaveIds([]);
        (await staff.SendAsync(Corpus.Employee, $$"""[{ "match": { "id": { "eq": "{{mine}}" } } }, { "page": { "limit": 1 } }]""")).ShouldHaveIds([mine]);
    }

    [Fact]
    public async Task X3_a_callers_own_condition_on_organizationId_cannot_displace_the_scope()
    {
        var a = Org.A.Id();
        var b = Org.B.Id();
        var staff = await Client(Corpus.Employee);

        var foreignOnly = await staff.SendAsync(Corpus.Employee, $$"""[{ "match": { "organizationId": { "eq": "{{b}}" } } }, { "page": { "limit": 500 } }]""");
        var both = await staff.SendAsync(Corpus.Employee, $$"""[{ "match": { "or": [{ "organizationId": { "eq": "{{a}}" } }, { "organizationId": { "eq": "{{b}}" } }] } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");
        var negated = await staff.SendAsync(Corpus.Employee, $$"""[{ "match": { "not": { "organizationId": { "eq": "{{a}}" } } } }, { "page": { "limit": 500 } }]""");

        foreignOnly.ShouldHaveIds([]);
        both.ShouldHaveIds(Corpus.AllIds(Corpus.Employee));
        negated.ShouldHaveIds([]);
    }

    [Fact]
    public async Task X4_the_scope_stage_does_not_count_toward_the_stage_limit_twenty_caller_stages_pass_and_twenty_one_do_not()
    {
        // Exactly n caller stages: n - 2 matches, the sort, the page. The engine adds its scope stage on top.
        static string Exactly(int n) => "[" + string.Join(", ", Enumerable.Repeat("""{ "match": { "id": { "neq": null } } }""", n - 2)) + """, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 1 } }]""";
        var staff = await Client(Corpus.Employee);

        var at = await staff.SendAsync(Corpus.Employee, Exactly(20));
        var over = await staff.SendAsync(Corpus.Employee, Exactly(21));

        at.ShouldHaveIds(Corpus.PageOf(Corpus.AllIds(Corpus.Employee), 1));
        over.ShouldRefuse("MAX_PIPELINE_STAGES_EXCEEDED", 400);
        over.ErrorCodes.Should().Equal(["MAX_PIPELINE_STAGES_EXCEEDED"]);
    }

    [Fact]
    public async Task X5_the_scope_is_applied_again_inside_a_lookup_no_child_of_another_organisation_is_joined()
    {
        // The vehicle form of the legacy case, and the conformance form that bites: organisation
        // B's child names an A parent, so an unscoped lookup would join it under A.
        foreach (var org in new[] { Org.A, Org.B })
        {
            var vehicles = Corpus.Sorted(Corpus.Vehicle, [("id", false)], org);
            var equipment = Corpus.Rows(Corpus.Equipment, org);
            var expected = vehicles.Select(vehicle => equipment.Where(child => Corpus.GuidAt(child, "vehicle.id") == vehicle.Id).Select(child => child.Id).Order().ToList()).ToList();
            expected.SelectMany(children => children).Should().NotBeEmpty($"organisation {org} joins children, or the case is vacuous");

            var answer = await (await Client(Corpus.Vehicle, org)).SendAsync(Corpus.Vehicle, """
                [ { "lookup": { "path": "vehicle.id", "from": "fleet.equipment", "as": "eq", "select": ["name"] } },
                  { "project": { "id": 1, "eq": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } } ]
                """);

            answer.ShouldHaveIds(vehicles.Select(vehicle => vehicle.Id));
            answer.Values("eq").Select(children => (children as JsonArray ?? []).Select(child => Guid.Parse(child!["id"]!.GetValue<string>())).Order().ToList())
                .Should().BeEquivalentTo(expected, options => options.WithStrictOrdering(), $"{org}: {answer}");
        }

        var parents = Corpus.Sorted(Corpus.Conformance, [("id", false)]);
        var children = Corpus.Rows(Corpus.ConformanceChild).Concat(Corpus.Rows(Corpus.ConformanceChild, Org.B)).ToList();
        children.Should().Contain(child => child.Org == Org.B && parents.Any(parent => parent.Id == Corpus.GuidAt(child, "parentId")), "a B child names an A parent: the trap");
        var own = parents.Select(parent => children.Where(child => child.Org == Org.A && Corpus.GuidAt(child, "parentId") == parent.Id).Select(child => child.Id).Order().ToList()).ToList();

        var conformance = await (await Client(Corpus.Conformance)).SendAsync(Corpus.Conformance, """
            [ { "lookup": { "path": "parentId", "from": "conformance.child", "as": "children" } },
              { "project": { "id": 1, "children": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
            """);

        conformance.ShouldHaveIds(parents.Select(parent => parent.Id));
        conformance.Values("children").Select(list => (list as JsonArray ?? []).Select(child => Guid.Parse(child!["id"]!.GetValue<string>())).Order().ToList())
            .Should().BeEquivalentTo(own, options => options.WithStrictOrdering(), conformance.ToString());
    }

    [Fact]
    public async Task X6_the_scope_is_applied_again_inside_a_local_resolve_no_target_of_another_organisation_is_resolved()
    {
        foreach (var org in new[] { Org.A, Org.B })
        {
            var departments = Corpus.AllIds(Corpus.Department, org).ToHashSet();
            var vehicles = Corpus.Sorted(Corpus.Vehicle, [("id", false)], org);
            var expected = vehicles.Select(vehicle => Corpus.GuidAt(vehicle, "department.id") is { } id && departments.Contains(id) ? id : (Guid?)null).ToList();
            expected.Should().Contain(id => id != null);

            var answer = await (await Client(Corpus.Vehicle, org)).SendAsync(Corpus.Vehicle, """
                [ { "resolve": { "path": "department.id", "as": "dep", "select": ["name"] } },
                  { "project": { "id": 1, "dep.id": 1, "dep.name": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } } ]
                """);

            answer.ShouldHaveIds(vehicles.Select(vehicle => vehicle.Id));
            answer.Values("dep.id").Select(value => value is null ? (Guid?)null : Guid.Parse(value.GetValue<string>())).Should().Equal(expected, $"{org}");
        }

        // The conformance form that bites: organisation B's row names REF-A, a code only
        // organisation A holds, so the scoped resolve answers null.
        var refs = Corpus.Rows(Corpus.ConformanceRef, Org.B).Select(row => Corpus.Text(row, "code")).ToHashSet();
        var rows = Corpus.Sorted(Corpus.Conformance, [("id", false)], Org.B);
        var expectedCodes = rows.Select(row => Corpus.Text(row, "refCode") is { } code && refs.Contains(code) ? code : null).ToList();
        rows.Should().Contain(row => Corpus.Text(row, "refCode") == "REF-A");
        expectedCodes.Should().OnlyContain(code => code == null, "REF-A lives in organisation A only");

        var trap = await (await Client(Corpus.Conformance, Org.B)).SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "refCode", "as": "ref" } }, { "project": { "id": 1, "ref": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
            """);

        trap.ShouldHaveIds(rows.Select(row => row.Id));
        trap.Values("ref").Should().AllSatisfy(value => value.Should().BeNull(trap.ToString()));
    }

    /// <summary>The page of the first <paramref name="limit"/> templates by id, each with the vehicle its createUserId names in the same organisation, or null.</summary>
    private static List<(Guid Row, Guid? Vehicle)> TemplateTargets(Org org, int limit)
    {
        var vehicles = Corpus.AllIds(Corpus.Vehicle, org).ToHashSet();

        return Corpus.PageOf(Corpus.Sorted(Corpus.Template, [("id", false)], org), limit)
            .Select(row => (row.Id, Corpus.GuidAt(row, "createUserId") is { } user && vehicles.Contains(user) ? user : (Guid?)null))
            .ToList();
    }

    private static List<(Guid Row, Guid? Vehicle)> Targets(WireAnswer answer) =>
        answer.Items.Select(item => (Guid.Parse(item!["id"]!.GetValue<string>()), Json.At(item, "veh.id") is JsonValue id ? Guid.Parse(id.GetValue<string>()) : (Guid?)null)).ToList();

    private const string RemotePipeline = """
        [ { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
          { "project": { "id": 1, "createUserId": 1, "veh.id": 1, "veh.matchCode": 1 } },
          { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
        """;

    [Fact]
    public async Task X8_a_remote_resolve_runs_at_the_owner_under_the_callers_organisation()
    {
        foreach (var org in new[] { Org.A, Org.B })
        {
            var expected = TemplateTargets(org, 10);
            expected.Should().Contain(entry => entry.Vehicle != null).And.Contain(entry => entry.Vehicle == null);

            var answer = await (await Client(Corpus.Template, org)).SendAsync(Corpus.Template, RemotePipeline);

            answer.ShouldBeOk();
            Targets(answer).Should().Equal(expected, $"{org}: {answer}");
        }

        // The conformance form: B's employeeId names B's employee; A's name A's or nothing.
        foreach (var org in new[] { Org.A, Org.B })
        {
            var employees = Corpus.AllIds(Corpus.Employee, org).ToHashSet();
            var rows = Corpus.Sorted(Corpus.Conformance, [("id", false)], org);
            var expected = rows.Select(row => Corpus.GuidAt(row, "employeeId") is { } id && employees.Contains(id) ? id : (Guid?)null).ToList();

            var answer = await (await Client(Corpus.Conformance, org)).SendAsync(Corpus.Conformance, """
                [ { "resolve": { "path": "employeeId", "as": "emp", "select": ["matchCode"] } }, { "project": { "id": 1, "emp.id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } } ]
                """);

            answer.ShouldHaveIds(rows.Select(row => row.Id));
            answer.Values("emp.id").Select(value => value is null ? (Guid?)null : Guid.Parse(value.GetValue<string>())).Should().Equal(expected, $"{org}");
        }
    }

    [Fact]
    public async Task X15_the_resolve_cache_never_serves_another_organisations_row_however_the_calls_interleave()
    {
        var pipeline = """
            [ { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "project": { "id": 1, "veh.id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 4 } } ]
            """;
        var expected = new Dictionary<Org, List<(Guid, Guid?)>> { [Org.A] = TemplateTargets(Org.A, 4), [Org.B] = TemplateTargets(Org.B, 4) };
        expected[Org.A].Select(entry => entry.Item2).Should().NotEqual(expected[Org.B].Select(entry => entry.Item2), "the organisations must disagree, or a shared cache would look right");

        foreach (var org in new[] { Org.A, Org.B, Org.A, Org.B, Org.A })
        {
            var answer = await (await Client(Corpus.Template, org)).SendAsync(Corpus.Template, pipeline);

            Targets(answer).Should().Equal(expected[org], $"{org}: {answer}");
        }
    }

    [Fact]
    public async Task X16_the_semi_join_id_cache_is_keyed_by_organisation()
    {
        var pipeline = """
            [ { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "match": { "veh.matchCode": { "eq": "VEH-002" } } },
              { "project": { "id": 1, "templateName": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 50 } } ]
            """;

        IReadOnlyList<Guid> Expected(Org org)
        {
            var vehicles = Corpus.IdsWhere(Corpus.Vehicle, row => Corpus.Text(row, "matchCode") == "VEH-002", org).ToHashSet();
            return Corpus.SortedIds(Corpus.Template, "id", org: org, filter: row => Corpus.GuidAt(row, "createUserId") is { } user && vehicles.Contains(user));
        }

        var a = Expected(Org.A);
        var b = Expected(Org.B);
        a.Should().NotBeEmpty();
        b.Should().NotBeEmpty();
        a.Intersect(b).Should().BeEmpty();
        a.Select(id => Corpus.TemplateName((int)Ids.OrdinalOf(id)))
            .Should().Equal(b.Select(id => Corpus.TemplateName((int)Ids.OrdinalOf(id))), "the same names, different rows: the clone design");

        (await (await Client(Corpus.Template, Org.A)).SendAsync(Corpus.Template, pipeline)).ShouldHaveIds(a);
        (await (await Client(Corpus.Template, Org.B)).SendAsync(Corpus.Template, pipeline)).ShouldHaveIds(b);
        (await (await Client(Corpus.Template, Org.A)).SendAsync(Corpus.Template, pipeline)).ShouldHaveIds(a);
    }

    [Fact]
    public async Task X14_a_cursor_issued_under_one_organisation_is_refused_under_the_other()
    {
        var all = Corpus.AllIds(Corpus.Employee);
        var a = await Client(Corpus.Employee, Org.A);

        var page = await a.SendAsync(Corpus.Employee, """[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 1 } }]""");
        var cursor = page.ShouldHaveIds(Corpus.PageOf(all, 1)).NextCursor;
        cursor.Should().NotBeNull();

        var here = await a.SendAsync(Corpus.Employee, $$"""[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 1, "cursor": "{{cursor}}" } }]""");
        var there = await (await Client(Corpus.Employee, Org.B)).SendAsync(Corpus.Employee, $$"""[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 1, "cursor": "{{cursor}}" } }]""");

        here.ShouldHaveIds(Corpus.PageOf(all, 1, offset: 1));
        there.ShouldRefuse("CURSOR_INVALID", 400);
        there.ErrorCodes.Should().Equal(["CURSOR_INVALID"]);
    }

    [Fact]
    public async Task X9_a_request_with_no_usable_organisation_is_refused_403_and_never_compared_against_null()
    {
        var staff = await Client(Corpus.Employee);
        var cases = new Dictionary<string, LabClient>
        {
            ["no organisation"] = staff.Anonymous(),
            ["the empty value"] = staff.Anonymous().WithHeader(LabIdentity.OrganisationHeader, ""),
            ["not a guid"] = staff.Anonymous().WithHeader(LabIdentity.OrganisationHeader, "not-a-guid"),
            ["the empty guid"] = staff.As(Guid.Empty),
        };

        foreach (var (what, client) in cases)
        {
            var answer = await client.SendAsync(Corpus.Employee, """[{ "page": { "limit": 1 } }]""");

            answer.StatusCode.Should().Be(403, $"{what}: {answer}");
            answer.Type.Should().Be("access_denied");
            answer.ErrorCodes.Should().Equal(["ACCESS_DENIED"]);
        }
    }

    [Fact]
    public async Task X12_the_organisation_alone_decides_the_same_bytes_answer_two_row_sets()
    {
        var pipeline = """[{ "match": { "matchCode": { "eq": "DUP" } } }, { "project": { "id": 1, "matchCode": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""";
        var a = Corpus.IdsOf(Corpus.EmployeesWithMatchCode("DUP"));
        var b = Corpus.IdsOf(Corpus.EmployeesWithMatchCode("DUP", org: Org.B));
        a.Should().HaveCount(3);
        b.Should().ContainSingle();

        (await (await Client(Corpus.Employee, Org.A)).SendAsync(Corpus.Employee, pipeline)).ShouldHaveIds(a);
        (await (await Client(Corpus.Employee, Org.B)).SendAsync(Corpus.Employee, pipeline)).ShouldHaveIds(b);
    }

    [Fact]
    public async Task X17_addon_definitions_are_read_per_organisation_and_a_third_organisations_are_in_neither_answer()
    {
        // The engine half of the legacy /schema/addons comparison (that endpoint is OxS): a key is
        // filterable exactly where the caller's organisation defines it. Organisation A defines
        // contractNumber on conformance.entity, B defines nothing; a definition stored for a
        // third organisation reaches neither.
        var third = Guid.Parse("77777777-7777-7777-7777-777777777777");

        await using var fleet = await CorpusFleet.CreateAsync("b5-x17", seed: [LabService.Conformance], extra: async lab =>
        {
            var database = await lab.DatabaseAsync(LabService.Conformance);
            await database.GetCollection<BsonDocument>(TestAddonSource.Collection)
                .InsertOneAsync(TestAddonSource.Document(Ids.Of(Spaces.AddonDefinition, Org.C, 900), third, Corpus.Conformance, "looseKey", "string", "Third org"));
        });

        var wanted = Corpus.Row(Corpus.Conformance, "c-alpha");
        var expected = Corpus.IdsWhere(Corpus.Conformance, row => Corpus.Text(row, "addon.contractNumber") == Corpus.Text(wanted, "addon.contractNumber"));
        expected.Should().ContainSingle();

        var a = fleet.Client(LabService.Conformance, Org.A);
        var b = fleet.Client(LabService.Conformance, Org.B);
        var filter = $$"""[{ "match": { "addon.contractNumber": { "eq": "{{Corpus.Text(wanted, "addon.contractNumber")}}" } } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""";
        var loose = """[{ "match": { "addon.looseKey": { "eq": "undefined on purpose" } } }, { "page": { "limit": 10 } }]""";

        (await a.SendAsync(Corpus.Conformance, filter)).ShouldHaveIds(expected);
        (await b.SendAsync(Corpus.Conformance, filter)).ShouldRefuse("NOT_FILTERABLE", 400);
        (await a.SendAsync(Corpus.Conformance, loose)).ShouldRefuse("NOT_FILTERABLE", 400);
        (await b.SendAsync(Corpus.Conformance, loose)).ShouldRefuse("NOT_FILTERABLE", 400);
    }

    [Fact]
    public async Task X18_X19_a_host_advertises_no_policy_permission_concurrency_or_quota_surface_beyond_the_organisation_scope()
    {
        var health = await (await Lab.ClientAsync(LabService.Transport)).HealthAsync();
        var capabilities = health.Body!["capabilities"]!.AsArray().Select(item => item!.GetValue<string>()).ToList();

        capabilities.Should().NotBeEmpty();
        capabilities.Should().NotContain(name => System.Text.RegularExpressions.Regex.IsMatch(name, "policy|permission|rate|quota|concurren", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public async Task X11_a_host_with_no_scope_provider_refuses_to_start()
    {
        var database = await CustomHost.SharedDatabaseAsync(LabService.Staff);
        var start = () => CustomHost.StartAsync(LabService.Staff, database, new CustomHost.Wiring { NoScope = true });

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("IOxQLScopeProvider");
    }
}
