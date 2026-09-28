using System.Net;
using System.Reflection;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Hosts;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Refusals;

/// <summary>
/// The closed list against the engine's own catalogue, and the refusals the legacy rig could not
/// provoke. The old lab listed six codes as unreachable (a text decimal member, a host without a
/// remote client, a semi-join above the cap, a query over the server's memory, an escaped fault,
/// and <c>RESOLVE_NOT_FILTERABLE</c>) and two as provoked elsewhere (<c>BATCH_TOO_LARGE</c>,
/// <c>QUERY_TIMEOUT</c>). Here a private fleet, a variant, a host wired without a seam or a model
/// the lab does not have reaches all of them; <c>RESOLVE_NOT_FILTERABLE</c> (a condition on a
/// remote resolve alias itself) is provoked in <see cref="RefusalsServerTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public class RefusalsCatalogueTests
{
    /// <summary>The diagnostic codes of the catalogue: a diagnostic travels with rows, it never refuses.</summary>
    public static readonly IReadOnlySet<string> DiagnosticCodes = new HashSet<string>
    {
        "ENTITY_ID_RETIRED", "TOTAL_COUNT_CAPPED", "RESOLVE_TIMEOUT", "RESOLVE_UNREACHABLE",
        "RESOLVE_PARTIAL", "SORT_ON_ADDON", "REGEX_UNANCHORED", "DECIMAL_TEXT_EXCLUDED",
        "UNWIND_DEPTH_TRUNCATED", "LOOKUP_TRUNCATED",
    };

    /// <summary>Every error code and the case that provokes it outside the V table.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProvokedHere = new Dictionary<string, string>
    {
        ["BATCH_TOO_LARGE"] = nameof(V49_eleven_queries_in_one_batch_are_refused_whole_and_ten_are_answered),
        ["QUERY_TIMEOUT"] = nameof(U21_an_aggregate_over_its_time_budget_is_a_504_timeout_refusal),
        ["DECIMAL_TEXT_NOT_ORDERABLE"] = nameof(V53_an_ordered_comparison_on_a_decimal_member_stored_as_text_is_refused_and_equality_is_answered),
        ["RESOLVE_UNAVAILABLE"] = nameof(V55_a_remote_resolve_on_a_host_without_a_remote_query_client_is_refused),
        ["SEMI_JOIN_TOO_LARGE"] = nameof(V56_a_semi_join_selecting_more_owner_rows_than_the_cap_is_refused_and_one_under_it_is_answered),
        ["QUERY_TOO_EXPENSIVE"] = nameof(V57_a_push_that_builds_a_value_above_the_document_limit_is_refused_as_too_expensive),
        ["INTERNAL_ERROR"] = nameof(V58_an_exception_escaping_a_seam_is_a_coded_500_that_names_the_correlation_id),
        ["RESOLVE_NOT_FILTERABLE"] = nameof(RefusalsServerTests.V35_a_condition_on_the_remote_alias_itself_is_refused_with_RESOLVE_NOT_FILTERABLE_before_the_owner_is_called),
        ["UNKNOWN_VARIANT"] = nameof(Rows.RowsVariantsTests.An_unknown_variant_and_a_flatten_over_a_member_that_does_not_nest_the_items_are_refused),
        ["FLATTEN_NOT_RECURSIVE"] = nameof(Rows.RowsVariantsTests.An_unknown_variant_and_a_flatten_over_a_member_that_does_not_nest_the_items_are_refused),
        ["LOOKUP_ON_NOT_ENTITY"] = nameof(Joins.JoinsLookupMembersTests.On_a_lookup_array_is_LOOKUP_ON_NOT_ENTITY),
        ["NOT_CONTINUABLE"] = nameof(Joins.JoinsLookupMembersTests.On_a_remote_alias_is_NOT_CONTINUABLE),
    };

    /// <summary>Codes no request raises today, with the defect that says why.</summary>
    private static readonly IReadOnlyDictionary<string, string> Pending = new Dictionary<string, string>();

    private static IReadOnlyList<string> Catalogue() =>
        typeof(Codes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Va_every_error_code_of_the_engine_catalogue_is_provoked_by_a_case_or_named_by_a_pending_defect()
    {
        var catalogue = Catalogue();
        var errors = catalogue.Where(code => !DiagnosticCodes.Contains(code)).ToList();

        catalogue.Should().OnlyHaveUniqueItems();
        DiagnosticCodes.Should().BeSubsetOf(catalogue, "the diagnostic list names codes the engine declares");

        var table = RefusalsServerTests.Table.Select(row => row.Code).ToHashSet();
        var covered = table.Concat(ProvokedHere.Keys).Concat(Pending.Keys).ToHashSet();

        errors.Except(covered).Should().BeEmpty("an error code with no provocation and no pending defect is a hole in the suite");
        covered.Except(errors).Should().BeEmpty("a case claims a code the engine does not declare");
        table.Intersect(ProvokedHere.Keys).Should().BeEmpty();
        Pending.Keys.Intersect(table.Concat(ProvokedHere.Keys)).Should().BeEmpty("a pending code is one nothing provokes");
    }

    [Fact]
    public void V60_the_catalogue_holds_its_counted_error_codes_and_diagnostics()
    {
        var catalogue = Catalogue();

        // 2.0: 60 errors and 8 diagnostics; 2.1 adds UNKNOWN_VARIANT, FLATTEN_NOT_RECURSIVE and UNWIND_DEPTH_TRUNCATED,
        // then LOOKUP_ON_NOT_ENTITY, NOT_CONTINUABLE and LOOKUP_TRUNCATED.
        catalogue.Should().HaveCount(74);
        catalogue.Count(code => !DiagnosticCodes.Contains(code)).Should().Be(64);
        catalogue.Count(DiagnosticCodes.Contains).Should().Be(10);
    }

    [Fact]
    public async Task V49_eleven_queries_in_one_batch_are_refused_whole_and_ten_are_answered()
    {
        var staff = await Lab.ClientAsync(LabService.Staff);
        var one = """{ "entityType": "staff.employee", "pipeline": [{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 1 } }] }""";
        var first = Corpus.PageOf(Corpus.AllIds(Corpus.Employee), 1);

        var over = await staff.BatchAsync(Enumerable.Repeat<object>(one, 11));
        var at = await staff.BatchAsync(Enumerable.Repeat<object>(one, 10));

        over.StatusCode.Should().Be(400, over.ToString());
        over.Type.Should().Be("validation_error");
        over.ErrorCodes.Should().Equal(["BATCH_TOO_LARGE"]);

        at.StatusCode.Should().Be(200, at.ToString());
        at.Results.Should().HaveCount(10).And.AllSatisfy(entry => entry.ShouldHaveIds(first));
    }

    [Fact]
    public async Task U21_an_aggregate_over_its_time_budget_is_a_504_timeout_refusal()
    {
        // One millisecond of budget over the 100 001 bulk rows, sorted on a member with no index:
        // no server finishes that in time. The legacy rig could not lower the budget at start-up.
        var shared = await CorpusFleet.SharedAsync();
        await shared.SeedBulkAsync();
        var client = shared.Variant(LabService.Transport, "b5-maxtime-1", new Dictionary<string, string?> { ["OxQL:Execution:MaxTimeMs"] = "1" }, Org.C);

        var answer = await client.SendAsync(Corpus.Template, """[{ "sort": [{ "templateName": "desc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }]""");

        answer.StatusCode.Should().Be(504, answer.ToString());
        answer.Type.Should().Be("timeout");
        answer.ErrorCodes.Should().Equal(["QUERY_TIMEOUT"]);
    }

    /// <summary>A priced entity whose price is declared stored as text: the member a migration has not reached.</summary>
    public sealed class Priced
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [BsonRepresentation(BsonType.String)]
        public decimal Price { get; set; }
    }

    [Fact]
    public async Task V53_an_ordered_comparison_on_a_decimal_member_stored_as_text_is_refused_and_equality_is_answered()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("b5_text_decimal");
        var rows = new[] { ("12.50", 1), ("12.5", 2), ("7", 3) }
            .Select(entry => new BsonDocument
            {
                ["_id"] = new BsonBinaryData(Ids.Of(Spaces.Dangling, Org.A, entry.Item2), GuidRepresentation.Standard),
                ["OrganizationId"] = EngineDirect.OrganisationValue,
                ["Price"] = entry.Item1,
            }).ToList();
        await owned.Database.GetCollection<BsonDocument>("priced").InsertManyAsync(rows);

        // Equality reaches every scale the driver could have written the value with.
        var expected = new[] { Ids.Of(Spaces.Dangling, Org.A, 1), Ids.Of(Spaces.Dangling, Org.A, 2) };
        var model = ClrModelBuilder.Build([new EntityDeclaration("b5.priced", "b5.priced", typeof(Priced), "priced", null, false)]);
        await using var host = await CustomHost.StartAsync(LabService.Staff, owned.Database, new CustomHost.Wiring { Model = model });

        foreach (var op in new[] { "gt", "gte", "lt", "lte" })
        {
            var refused = await host.SendAsync("b5.priced", $$"""[{ "match": { "price": { "{{op}}": 10 } } }, { "page": { "limit": 5 } }]""");

            refused.StatusCode.Should().Be(400, refused.ToString());
            refused.Type.Should().Be("validation_error");
            refused.ErrorCodes.Should().Equal(["DECIMAL_TEXT_NOT_ORDERABLE"]);
            refused.Errors[0]["path"]!.GetValue<string>().Should().Be("price");
        }

        var equal = await host.SendAsync("b5.priced", """[{ "match": { "price": { "eq": 12.5 } } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 5 } }]""");

        equal.ShouldHaveIds(expected).ShouldHaveNoDiagnostics();
    }

    [Fact]
    public async Task V55_a_remote_resolve_on_a_host_without_a_remote_query_client_is_refused()
    {
        await using var host = await CustomHost.StartAsync(LabService.Transport, await CustomHost.SharedDatabaseAsync(LabService.Transport));

        foreach (var pipeline in new[]
        {
            """[{ "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } }, { "page": { "limit": 5 } }]""",
            """[{ "resolve": { "path": "createUserId", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "VEH-002" } } }, { "page": { "limit": 5 } }]""",
        })
        {
            var answer = await host.SendAsync(Corpus.Template, pipeline);

            answer.StatusCode.Should().Be(422, answer.ToString());
            answer.Type.Should().Be("not_executable");
            answer.ErrorCodes.Should().Equal(["RESOLVE_UNAVAILABLE"]);
        }

        // The same host answers a query without a remote stage.
        var local = await host.SendAsync(Corpus.Template, """[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 3 } }]""");
        local.ShouldHaveIds(Corpus.PageOf(Corpus.AllIds(Corpus.Template), 3));
    }

    [Fact]
    public async Task V55_an_owner_that_does_not_answer_a_semi_join_refuses_the_query_rather_than_evaluating_something_else()
    {
        await using var fleet = await CorpusFleet.CreateAsync("b5-semi-unanswered", seed: [LabService.Conformance]);
        fleet.Owner.Set(new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.InternalServerError });

        var answer = await fleet.Client(LabService.Conformance).SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } },
              { "match": { "w.name": { "eq": "Widget One" } } },
              { "page": { "limit": 5 } } ]
            """);

        answer.StatusCode.Should().Be(422, answer.ToString());
        answer.Type.Should().Be("not_executable");
        answer.ErrorCodes.Should().Equal(["RESOLVE_UNAVAILABLE"]);
        answer.Errors[0]["message"]!.GetValue<string>().Should().Contain("500");
    }

    [Fact]
    public async Task V56_a_semi_join_selecting_more_owner_rows_than_the_cap_is_refused_and_one_under_it_is_answered()
    {
        var veh = Corpus.Where(Corpus.Vehicle, row => Corpus.Text(row, "matchCode") is { } code && code.StartsWith("VEH", StringComparison.Ordinal));
        veh.Count.Should().BeGreaterThan(2, "the condition must select more vehicles than the cap");
        var two = Corpus.IdsWhere(Corpus.Vehicle, row => Corpus.Text(row, "matchCode") == "VEH-002").ToHashSet();
        two.Should().HaveCountLessThanOrEqualTo(2);
        var expected = Corpus.SortedIds(Corpus.Template, "id", filter: row => Corpus.GuidAt(row, "createUserId") is { } user && two.Contains(user));

        var capped = (await CorpusFleet.SharedAsync()).Variant(LabService.Transport, "b5-semijoin-2", new Dictionary<string, string?> { ["OxQL:Limits:MaxSemiJoinIds"] = "2" });

        var over = await capped.SendAsync(Corpus.Template, """
            [ { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "match": { "veh.matchCode": { "startsWith": "VEH" } } },
              { "page": { "limit": 5 } } ]
            """);

        over.StatusCode.Should().Be(422, over.ToString());
        over.Type.Should().Be("not_executable");
        over.ErrorCodes.Should().Equal(["SEMI_JOIN_TOO_LARGE"]);
        over.Errors[0]["stage"]!.GetValue<int>().Should().Be(1);

        var under = await capped.SendAsync(Corpus.Template, """
            [ { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "match": { "veh.matchCode": { "eq": "VEH-002" } } },
              { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 50 } } ]
            """);

        under.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task V57_a_push_that_builds_a_value_above_the_document_limit_is_refused_as_too_expensive()
    {
        // Twenty rows of one mebibyte each: a push of them all is one twenty-mebibyte value, above
        // the server's sixteen. A group by a key that splits them fits, which is the advice the
        // refusal gives.
        const int rows = 20;
        var big = new string('x', 1024 * 1024);

        await using var fleet = await CorpusFleet.CreateAsync("b5-too-expensive", seed: [], extra: async lab =>
        {
            var database = await lab.DatabaseAsync(LabService.Staff);
            await database.GetCollection<BsonDocument>("employee").InsertManyAsync(Enumerable.Range(1, rows).Select(n => new BsonDocument
            {
                ["_id"] = new BsonBinaryData(Ids.Of(Spaces.Employee, Org.A, n), GuidRepresentation.Standard),
                ["OrganizationId"] = new BsonBinaryData(Org.A.Id(), GuidRepresentation.Standard),
                ["MatchCode"] = big + n,
            }));
        });
        var staff = fleet.Client(LabService.Staff);

        var answer = await staff.SendAsync(Corpus.Employee, """[{ "group": { "by": [], "fields": { "all": { "push": "matchCode" } } } }]""");

        answer.StatusCode.Should().Be(422, answer.Text.Length > 500 ? answer.Text[..500] : answer.Text);
        answer.Type.Should().Be("not_executable");
        answer.ErrorCodes.Should().Equal(["QUERY_TOO_EXPENSIVE"]);

        var split = await staff.SendAsync(Corpus.Employee, """[{ "group": { "by": [{ "path": "id", "as": "k" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 50 } }]""");
        split.ShouldBeOk();
        split.Items.Should().HaveCount(rows);
    }

    [Fact]
    public async Task V58_an_exception_escaping_a_seam_is_a_coded_500_that_names_the_correlation_id()
    {
        var database = await CustomHost.SharedDatabaseAsync(LabService.Staff);

        await using var scopeDown = await CustomHost.StartAsync(LabService.Staff, database, new CustomHost.Wiring { Scope = new ThrowingScopeProvider() });
        await using var addonsDown = await CustomHost.StartAsync(LabService.Staff, database, new CustomHost.Wiring { Addons = new ThrowingAddonSource() });

        foreach (var host in new[] { scopeDown, addonsDown })
        {
            var answer = await host.SendAsync(Corpus.Employee, """[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 1 } }]""");

            answer.StatusCode.Should().Be(500, answer.ToString());
            answer.Type.Should().Be("internal_error");
            answer.ErrorCodes.Should().Equal(["INTERNAL_ERROR"]);
            answer.Errors[0]["message"]!.GetValue<string>().Should().Contain("b5-correlation", "the message points at the log line");
            answer.Text.Should().NotContain("down").And.NotContain("did not answer", "the fault's own text stays in the log");
        }
    }

    /// <summary>An entity with no organisation member, and one that joins it both ways.</summary>
    public sealed class Orgless
    {
        public Guid Id { get; set; }

        [OxQLReference("b5.scoped")]
        public Guid ScopedId { get; set; }

        public string? Name { get; set; }
    }

    public sealed class Scoped
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference("b5.orgless")]
        public Guid? OrglessId { get; set; }
    }

    [Fact]
    public async Task V52_an_entity_without_an_organisation_member_is_refused_403_at_the_root_and_400_as_a_join_target()
    {
        // The legacy rig could not reach the 400 half: every fleet entity publishes a root
        // organizationId. A model of two entities, one without it, reaches both halves.
        await using var owned = await MongoFixture.CreateDatabaseAsync("b5_orgless");
        var model = ClrModelBuilder.Build(
        [
            new EntityDeclaration("b5.orgless", "b5.orgless", typeof(Orgless), "orgless", null, false),
            new EntityDeclaration("b5.scoped", "b5.scoped", typeof(Scoped), "scoped", null, false),
        ]);
        await using var host = await CustomHost.StartAsync(LabService.Staff, owned.Database, new CustomHost.Wiring { Model = model });

        var root = await host.SendAsync("b5.orgless", """[{ "page": { "limit": 1 } }]""");
        root.StatusCode.Should().Be(403, root.ToString());
        root.Type.Should().Be("access_denied");
        root.ErrorCodes.Should().Equal(["ACCESS_DENIED"]);

        foreach (var pipeline in new[]
        {
            """[{ "lookup": { "path": "scopedId", "from": "b5.orgless", "as": "children" } }, { "page": { "limit": 1 } }]""",
            """[{ "resolve": { "path": "orglessId", "as": "target" } }, { "page": { "limit": 1 } }]""",
        })
        {
            var join = await host.SendAsync("b5.scoped", pipeline);

            join.StatusCode.Should().Be(400, join.ToString());
            join.Type.Should().Be("validation_error");
            join.ErrorCodes.Should().Equal(["ACCESS_DENIED"]);
        }
    }
}
