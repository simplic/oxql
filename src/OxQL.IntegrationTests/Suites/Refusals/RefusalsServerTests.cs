using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Refusals;

/// <summary>
/// Area V.a: one provocation per server refusal code. The rule of the legacy battery: this input
/// provokes exactly this code, at exactly this HTTP status, under exactly this refusal type, and
/// every row states all three before it runs. The pipeline is hand-written wire JSON, so the
/// engine is the only thing under test.
/// <para>
/// The cases run on the lab model; <c>transport.shipment_template</c>'s <c>createUserId</c> is the
/// remote reference onto <c>fleet.vehicle</c>. The two
/// <c>NOT_STORED</c> rows move to <c>conformance.entity</c>, which carries the unstored members
/// the lab model has. The codes the legacy rig could not reach are provoked in
/// <see cref="RefusalsCatalogueTests"/>, which also checks the whole list against the engine's
/// own catalogue.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class RefusalsServerTests
{
    /// <summary>One provocation and the exact answer it must get.</summary>
    public sealed record Case(
        string Id,
        string Code,
        int Status,
        string Type,
        string Why,
        LabService Service,
        string Entity,
        string Pipeline,
        string? Variables = null,
        bool ContractOne = false,
        bool NoContractHeader = false,
        bool Anonymous = false,
        bool Contains = false);

    private const string Page1 = """{ "page": { "limit": 1 } }""";

    private static string Repeat(int n, Func<int, string> make, string separator = ", ") => string.Join(separator, Enumerable.Range(0, n).Select(make));

    private static Case Staff(string id, string code, string why, string pipeline, string? variables = null) =>
        new(id, code, 400, "validation_error", why, LabService.Staff, Corpus.Employee, pipeline, variables);

    private static Case Shipment(string id, string code, string why, string pipeline) =>
        new(id, code, 400, "validation_error", why, LabService.Transport, Corpus.Shipment, pipeline);

    private static Case Template(string id, string code, string why, string pipeline) =>
        new(id, code, 400, "validation_error", why, LabService.Transport, Corpus.Template, pipeline);

    /// <summary>The 76 provocations of the legacy table, in its order.</summary>
    public static readonly IReadOnlyList<Case> Table =
    [
        // entity
        Staff("V1", "UNKNOWN_ENTITY", "an entityType no host declares", $"[{Page1}]") with { Entity = "nope.nope" },
        Staff("V1.case", "UNKNOWN_ENTITY", "a case variant of a declared entity id", $"[{Page1}]") with { Entity = "Staff.Employee" },
        Staff("V1.lookup-from", "UNKNOWN_ENTITY", "a lookup.from naming no entity", $$"""[{ "lookup": { "path": "x", "from": "nope.nope", "as": "z" } }, {{Page1}}]"""),

        // path
        Staff("V2", "INVALID_PATH", "a path starting with $", $$"""[{ "match": { "$bad": { "eq": 1 } } }, {{Page1}}]"""),
        Staff("V2.dotdot", "INVALID_PATH", "a path with an empty segment (a..b)", $$"""[{ "match": { "a..b": { "eq": 1 } } }, {{Page1}}]"""),
        Staff("V3", "UNKNOWN_PATH", "a member the entity does not publish", $$"""[{ "match": { "nosuchpath": { "eq": 1 } } }, {{Page1}}]"""),
        Staff("V3.depth", "UNKNOWN_PATH", "a 33-segment path, one over the path depth", $$"""[{ "match": { "{{Repeat(33, i => "s" + i, ".")}}": { "eq": 1 } } }, {{Page1}}]"""),
        new("V4", "NOT_STORED", 400, "validation_error", "a filter on a [BsonIgnore] member the driver never writes (legacy: tours.isMirrored)",
            LabService.Conformance, Corpus.Conformance, $$"""[{ "match": { "scratch": { "eq": "x" } } }, {{Page1}}]"""),
        new("V4.abstract", "NOT_STORED", 400, "validation_error", "a filter on a get-only computed member (legacy: the abstract tours.resource.type)",
            LabService.Conformance, Corpus.Conformance, $$"""[{ "match": { "computed": { "eq": "x" } } }, {{Page1}}]"""),
        Shipment("V5", "NOT_FILTERABLE", "a filter on an object member", $$"""[{ "match": { "loadAddress": { "eq": "x" } } }, {{Page1}}]"""),
        Staff("V5.addon-undefined", "NOT_FILTERABLE", "a filter on an addon key with no definition", $$"""[{ "match": { "addon.undefinedKey": { "eq": "opaque" } } }, {{Page1}}]"""),
        Shipment("V6", "NOT_SORTABLE", "a sort on a path under a collection, unwound by nothing", $$"""[{ "sort": [{ "items.quantity.value": "asc" }] }, {{Page1}}]"""),
        Staff("V6.addon-undefined", "NOT_SORTABLE", "a sort on an addon key with no definition", $$"""[{ "sort": [{ "addon.undefinedKey": "asc" }] }, {{Page1}}]"""),
        Shipment("V7", "NOT_A_COLLECTION", "unwind of a scalar", $$"""[{ "unwind": { "path": "shipmentNumber" } }, {{Page1}}]"""),
        Shipment("V8", "UNWIND_ORDER", "the inner collection unwound before its outer one", $$"""[{ "unwind": { "path": "billingLines.references" } }, {{Page1}}]"""),
        Shipment("V9", "ALIAS_COLLISION", "includeIndex equal to an existing member", $$"""[{ "unwind": { "path": "items", "includeIndex": "items" } }, {{Page1}}]"""),
        Shipment("V10", "INVALID_ALIAS", "an alias that is not a plain identifier", $$"""[{ "unwind": { "path": "items", "includeIndex": "my-alias" } }, {{Page1}}]"""),
        Template("V10.no-as", "INVALID_ALIAS", "a resolve with no as at all", $$"""[{ "resolve": { "path": "createUserId" } }, {{Page1}}]"""),

        // operand
        Staff("V11", "INVALID_OPERAND", "gt null", $$"""[{ "match": { "matchCode": { "gt": null } } }, {{Page1}}]"""),
        Staff("V11.exists", "INVALID_OPERAND", "exists given a string instead of a boolean", $$"""[{ "match": { "matchCode": { "exists": "yes" } } }, {{Page1}}]"""),
        Staff("V11.object", "INVALID_OPERAND", "an operand object that is not a $var wrapper", $$"""[{ "match": { "matchCode": { "eq": { "a": 1 } } } }, {{Page1}}]"""),
        Staff("V11.array", "INVALID_OPERAND", "an array under a scalar operator", $$"""[{ "match": { "matchCode": { "eq": ["a"] } } }, {{Page1}}]"""),
        Staff("V11.kind", "INVALID_OPERAND", "a string operand on a numeric path", $$"""[{ "match": { "children": { "eq": "not-a-number" } } }, {{Page1}}]"""),
        Shipment("V12", "UNKNOWN_ENUM_MEMBER", "an enum member name the model does not declare", $$"""[{ "match": { "loadingTimeType": { "eq": "Nope" } } }, {{Page1}}]"""),
        Staff("V12.addon-values", "UNKNOWN_ENUM_MEMBER", "an addon value off a closed values list (shiftModel is early|late|night)", $$"""[{ "match": { "addon.shiftModel": { "eq": "graveyard" } } }, {{Page1}}]"""),
        Staff("V13", "OPERAND_NOT_ARRAY", "in given a scalar", $$"""[{ "match": { "matchCode": { "in": "a" } } }, {{Page1}}]"""),
        Staff("V14", "UNBOUND_VARIABLE", "a $var no variables entry binds", $$"""[{ "match": { "matchCode": { "eq": { "$var": "nope" } } } }, {{Page1}}]""", """{ "other": "x" }"""),
        Staff("V15", "INVALID_VARIABLE", "a variable bound to an object", $$"""[{ "match": { "matchCode": { "eq": { "$var": "v" } } } }, {{Page1}}]""", """{ "v": { "a": 1 } }"""),

        // condition
        Staff("V16", "UNKNOWN_OPERATOR", "an operator the server has no spelling for ('like')", $$"""[{ "match": { "matchCode": { "like": "a" } } }, {{Page1}}]"""),
        Staff("V16.between", "UNKNOWN_OPERATOR", "'between' is client sugar and never reaches the wire", $$"""[{ "match": { "children": { "between": [1, 3] } } }, {{Page1}}]"""),
        Staff("V17", "EMPTY_LOGICAL_GROUP", "and: []", $$"""[{ "match": { "and": [] } }, {{Page1}}]"""),
        Staff("V18", "OPTION_NOT_APPLICABLE", "ignoreCase on gt", $$"""[{ "match": { "matchCode": { "gt": "a", "options": { "ignoreCase": true } } } }, {{Page1}}]"""),
        Staff("V18.unknown-option", "OPTION_NOT_APPLICABLE", "an option name the server does not know", $$"""[{ "match": { "matchCode": { "eq": "a", "options": { "nope": true } } } }, {{Page1}}]"""),
        Staff("V18.options-scalar", "OPTION_NOT_APPLICABLE", "options: 5, not an object at all", $$"""[{ "match": { "matchCode": { "eq": "a", "options": 5 } } }, {{Page1}}]"""),
        Staff("V19", "INVALID_REGEX", "a pattern .NET cannot compile", $$"""[{ "match": { "matchCode": { "regex": "a|*" } } }, {{Page1}}]"""),
        Staff("V20", "REGEX_TOO_LONG", "a 201-character pattern, one over regexMaxLength", $$"""[{ "match": { "matchCode": { "regex": "{{new string('a', 201)}}" } } }, {{Page1}}]"""),
        Staff("V21", "ANY_NOT_APPLICABLE", "any on a scalar path", $$"""[{ "match": { "matchCode": { "any": { "eq": "x" } } } }, {{Page1}}]"""),

        // stage
        Staff("V22", "UNKNOWN_STAGE", "an empty stage object", $"[{{}}, {Page1}]"),
        Staff("V22.filter", "UNKNOWN_STAGE", "a stage named 'filter'", $$"""[{ "filter": { "a": 1 } }, {{Page1}}]"""),
        Staff("V23", "UNKNOWN_STAGE_MEMBER", "a contract-2 lookup carrying the v1 localPath/foreignPath", $$"""[{ "lookup": { "path": "x", "from": "staff.employee", "as": "z", "localPath": "a", "foreignPath": "b" } }, {{Page1}}]"""),
        Template("V23.resolve-source", "UNKNOWN_STAGE_MEMBER", "a resolve carrying the v1 'source'", $$"""[{ "resolve": { "path": "createUserId", "as": "veh", "source": "vehicle" } }, {{Page1}}]"""),
        Staff("V24", "STAGE_AFTER_PAGE", "a sort after the page stage", $$"""[{{Page1}}, { "sort": [{ "id": "asc" }] }]"""),
        Staff("V25", "MULTIPLE_PAGE_STAGES", "two page stages", $$"""[{{Page1}}, { "page": { "limit": 2 } }]"""),
        Staff("V26", "MIXED_PROJECTION", "one included and one excluded path in the same projection", $$"""[{ "project": { "matchCode": 1, "children": 0 } }, {{Page1}}]"""),
        Staff("V26.empty", "MIXED_PROJECTION", "an empty projection", $$"""[{ "project": {} }, {{Page1}}]"""),
        Shipment("V27", "GROUP_ON_COLLECTION", "a group key under a collection", """[{ "group": { "by": [{ "path": "items.quantity.value", "as": "k" }], "fields": { "n": { "count": true } } } }]"""),
        Shipment("V28", "UNKNOWN_AGG_FUNCTION", "an aggregate function the engine has no spelling for", """[{ "group": { "by": [], "fields": { "x": { "median": { "path": "actualWeight.value" } } } } }]"""),
        Staff("V29", "INVALID_AGGREGATE_ARGUMENT", "sum of a string member", """[{ "group": { "by": [], "fields": { "x": { "sum": { "path": "matchCode" } } } } }]"""),
        Staff("V29.datetrunc-kind", "INVALID_AGGREGATE_ARGUMENT", "dateTrunc over a non-temporal path", """[{ "group": { "by": [{ "dateTrunc": { "path": "matchCode", "unit": "day" }, "as": "k" }], "fields": { "n": { "count": true } } } }]"""),
        Staff("V30", "INVALID_DATE_TRUNC_UNIT", "a unit the engine does not declare", """[{ "group": { "by": [{ "dateTrunc": { "path": "birthday", "unit": "fortnight" }, "as": "k" }], "fields": { "n": { "count": true } } } }]"""),
        Staff("V30.weekstart", "INVALID_DATE_TRUNC_UNIT", "weekStart naming no day: the same code as a bad unit", """[{ "group": { "by": [{ "dateTrunc": { "path": "birthday", "unit": "week", "weekStart": "someday" }, "as": "k" }], "fields": { "n": { "count": true } } } }]"""),
        Staff("V31", "INVALID_TIMEZONE", "a timezone that is not IANA", """[{ "group": { "by": [{ "dateTrunc": { "path": "birthday", "unit": "day", "timezone": "Mars/Phobos" }, "as": "k" }], "fields": { "n": { "count": true } } } }]"""),
        Staff("V32", "INVALID_SORT_DIRECTION", "an upper-case direction under contract 2", $$"""[{ "sort": [{ "id": "ASC" }] }, {{Page1}}]"""),
        Staff("V33", "LOOKUP_NOT_DECLARED", "a lookup along a child member that declares no reference back", $$"""[{ "lookup": { "path": "emailAddresses", "from": "staff.employee", "as": "x" } }, {{Page1}}]"""),
        Shipment("V34", "RESOLVE_NOT_DECLARED", "a resolve on a member with no declared reference", $$"""[{ "resolve": { "path": "createUserId", "as": "u" } }, {{Page1}}]"""),
        Template("V36", "RESOLVE_NOT_SORTABLE", "a sort on a remote resolve alias", $$"""[{ "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } }, { "sort": [{ "veh.matchCode": "asc" }] }, {{Page1}}]"""),

        // limits
        Staff("V37", "MAX_PIPELINE_STAGES_EXCEEDED", "21 stages, one over maxPipelineStages", $$"""[{{Repeat(20, _ => """{ "match": { "id": { "neq": null } } }""")}}, {{Page1}}]"""),
        Staff("V38", "MAX_LOOKUP_STAGES_EXCEEDED", "6 lookups, one over maxLookupStages", $$"""[{{Repeat(6, i => $$"""{ "lookup": { "path": "emailAddresses", "from": "staff.employee", "as": "a{{i}}" } }""")}}, {{Page1}}]""") with { Contains = true },
        Shipment("V39", "MAX_UNWIND_STAGES_EXCEEDED", "6 unwinds, one over maxUnwindStages", $$"""[{{Repeat(6, i => $$"""{ "unwind": { "path": "items", "as": "u{{i}}" } }""")}}, {{Page1}}]""") with { Contains = true },
        Template("V40", "MAX_RESOLVE_STAGES_EXCEEDED", "9 resolves, one over maxResolveStages (8 since 2.1)", $$"""[{{Repeat(9, i => $$"""{ "resolve": { "path": "createUserId", "as": "r{{i}}" } }""")}}, {{Page1}}]""") with { Contains = true },
        Staff("V41", "MAX_GROUP_FIELDS_EXCEEDED", "21 group fields, one over maxGroupFields", $$"""[{ "group": { "by": [], "fields": { {{Repeat(21, i => $$"""
            "f{{i}}": { "count": true }
            """)}} } } }]"""),
        Staff("V42", "MAX_PROJECTION_FIELDS_EXCEEDED", "501 projection paths, one over maxProjectionFields", $$"""[{ "project": { {{Repeat(501, i => $"\"p{i}\": 1")}} } }, {{Page1}}]""") with { Contains = true },
        Staff("V43", "MAX_CONDITIONS_EXCEEDED", "201 leaf conditions, one over maxConditions", $$"""[{ "match": { "and": [{{Repeat(201, i => $$"""{ "matchCode": { "neq": "x{{i}}" } }""")}}] } }, {{Page1}}]"""),
        Staff("V44", "MAX_VARIABLES_EXCEEDED", "65 variables, one over maxVariables", $$"""[{ "match": { "matchCode": { "eq": { "$var": "v0" } } } }, {{Page1}}]""", $"{{ {Repeat(65, i => $"\"v{i}\": \"x\"")} }}"),
        Staff("V45", "INVALID_PAGE_LIMIT", "limit: 0", """[{ "page": { "limit": 0 } }]"""),
        Staff("V45.offset", "INVALID_PAGE_LIMIT", "offset: -1", """[{ "page": { "limit": 1, "offset": -1 } }]"""),
        Staff("V45.cursor-offset", "INVALID_PAGE_LIMIT", "a cursor and an offset in the same page stage", """[{ "page": { "limit": 1, "offset": 1, "cursor": "anything" } }]"""),
        Staff("V46", "PAGE_SIZE_EXCEEDED", "limit: 501, one over maxPageSize", """[{ "page": { "limit": 501 } }]"""),
        new("V47", "LOOKUP_LIMIT_EXCEEDED", 400, "validation_error", "lookup.limit 101, one over maxLookupLimit, on a declared backward reference",
            LabService.Fleet, Corpus.Vehicle, $$"""[{ "lookup": { "path": "vehicle.id", "from": "fleet.equipment", "as": "eq", "limit": 101 } }, {{Page1}}]"""),
        Template("V48", "MAX_OFFSET_EXCEEDED", "offset 5001, one over maxOffset", """[{ "page": { "limit": 1, "offset": 5001 } }]"""),
        new("V50", "REQUEST_TOO_LARGE", 413, "validation_error", "a body above maxRequestBytes (262 144)", LabService.Staff, Corpus.Employee,
            $$"""[{ "match": { "or": [{{Repeat(4000, i => $$"""{ "matchCode": { "eq": "{{new string('x', 60)}}{{i}}" } }""")}}] } }, {{Page1}}]"""),

        // cursor, access, execution, compat
        Staff("V51", "CURSOR_INVALID", "a cursor that is not one", """[{ "page": { "limit": 1, "cursor": "tampered-nonsense" } }]"""),
        new("V52", "ACCESS_DENIED", 403, "access_denied", "a request that carries no organisation at all (legacy: a token without the OId claim)",
            LabService.Staff, Corpus.Employee, $"[{Page1}]", Anonymous: true),
        new("V54", "RESOLVE_REFUSED", 422, "not_executable", "a remote resolve whose filter the owner refuses", LabService.Transport, Corpus.Template,
            $$"""[{ "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"], "filter": { "nosuchpath": { "eq": 1 } } } }, {{Page1}}]""", Contains: true),
        Staff("V59", "LEGACY_STAGE_UNSUPPORTED", "a lookup stage under contract 1, where the compat binder has no equivalent",
            $$"""[{ "lookup": { "path": "emailAddresses", "from": "staff.employee", "as": "x" } }, {{Page1}}]""") with { ContractOne = true },
        Staff("V59.no-header", "LEGACY_STAGE_UNSUPPORTED", "the same stage with no contract header at all: the compat leg treats it as contract 1",
            $$"""[{ "lookup": { "path": "emailAddresses", "from": "staff.employee", "as": "x" } }, {{Page1}}]""") with { NoContractHeader = true },
    ];

    public static TheoryData<string, string> Ids()
    {
        var data = new TheoryData<string, string>();

        foreach (var row in Table)
            data.Add(row.Id, row.Code);

        return data;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Va_each_server_refusal_code_answers_its_status_type_and_code_list(string id, string code)
    {
        var row = Table.Single(candidate => candidate.Id == id);
        row.Code.Should().Be(code);

        var client = await Lab.ClientAsync(row.Service);

        if (row.Anonymous)
            client = client.Anonymous();

        if (row.ContractOne)
            client = client.Contract(1);

        if (row.NoContractHeader)
            client = client.Contract(null);

        var answer = await client.QueryAsync(Json.Request(row.Entity, row.Pipeline, row.Variables));

        answer.StatusCode.Should().Be(row.Status, $"{row.Id} ({row.Why}): {answer}");
        answer.Type.Should().Be(row.Type, answer.ToString());

        if (row.Contains)
            answer.ErrorCodes.Should().Contain(row.Code, answer.ToString());
        else
            answer.ErrorCodes.Should().Equal([row.Code], answer.ToString());
    }

    [Fact]
    public void Va_the_table_carries_the_legacy_76_provocations_with_unique_ids()
    {
        Table.Should().HaveCount(76);
        Table.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        Table.Select(row => row.Code).Distinct().Should().HaveCount(52, "the legacy table provoked 52 distinct codes; two more lived in the batch battery and six were out of the old rig's reach");
    }

    [Fact]
    public async Task V35_a_filter_on_a_member_of_a_remote_resolve_alias_is_admitted_as_a_semi_join_and_answers_the_right_rows()
    {
        // The shape RESOLVE_NOT_FILTERABLE was declared for, on a member of the alias: the owner
        // is asked for the vehicles whose matchCode is VEH-002, and the templates that point at
        // them come back.
        var vehicles = Corpus.IdsWhere(Corpus.Vehicle, row => Corpus.Text(row, "matchCode") == "VEH-002").ToHashSet();
        var expected = Corpus.SortedIds(Corpus.Template, "id", filter: row => Corpus.GuidAt(row, "createUserId") is { } user && vehicles.Contains(user));
        vehicles.Should().NotBeEmpty();
        expected.Should().NotBeEmpty().And.HaveCountLessThan(20);

        var answer = await (await Lab.ClientAsync(LabService.Transport)).SendAsync(Corpus.Template, """
            [ { "resolve": { "path": "createUserId", "as": "veh", "select": ["matchCode"] } },
              { "match": { "veh.matchCode": { "eq": "VEH-002" } } },
              { "project": { "id": 1, "templateName": 1, "veh.matchCode": 1 } },
              { "sort": [{ "id": "asc" }] },
              { "page": { "limit": 50 } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveNoDiagnostics();
        answer.Strings("veh.matchCode").Should().AllBe("VEH-002");
    }

    [Fact]
    public async Task V35_a_condition_on_the_remote_alias_itself_is_refused_with_RESOLVE_NOT_FILTERABLE_before_the_owner_is_called()
    {
        // The alias is the resolved object, not a member of the owner: the owner has no path of
        // that name, so no semi-join can express the condition. The binder refuses it with
        // RESOLVE_NOT_FILTERABLE, with the path and the stage, and nothing is sent to the owner
        // (it used to send the owner a match on the alias and answer its 422 refusal).
        await using var fleet = await CorpusFleet.CreateAsync("b5-d4", seed: [LabService.Conformance]);
        var client = fleet.Client(LabService.Conformance);
        var mark = fleet.Owner.Mark();

        foreach (var condition in new[] { """{ "w": { "eq": null } }""", """{ "w": { "exists": true } }""", """{ "w": { "neq": null } }""" })
        {
            var answer = await client.SendAsync(Corpus.Conformance, $$"""
                [ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } },
                  { "match": {{condition}} },
                  { "page": { "limit": 5 } } ]
                """);

            var error = answer.ShouldRefuse("RESOLVE_NOT_FILTERABLE", 400);
            answer.Type.Should().Be("validation_error");
            answer.ErrorCodes.Should().Equal(["RESOLVE_NOT_FILTERABLE"]);
            error["path"]!.GetValue<string>().Should().Be("w");
            error["stage"]!.GetValue<int>().Should().Be(1);
        }

        fleet.Owner.BatchesSince(mark).Should().BeEmpty("a condition no owner can answer is refused before any call");
    }
}
