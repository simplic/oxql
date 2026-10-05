using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// What explain says for an editor and for a save rule (DG2 M7): the basis of every error, diagnostic
/// and note with the verdict derived from it, what a row is and what identifies it at every boundary,
/// the entities a row holds, the total order of the page, and what may be done with a column.
/// </summary>
public class ExplainFactsTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static async Task<ExplainResult> ExplainAsync(string pipeline, string entity = Invoice, EntityModel? model = null, RequestContext? context = null, FakeRemoteClient? client = null, string envelope = "")
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(model ?? ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), client);
        var body = $$"""{ "query": { "entityType": "{{entity}}", "pipeline": {{pipeline}} }{{envelope}} }""";
        var outcome = await engine.ExplainAsync(ExplainAnswer.Envelope(body), context ?? BindHost.Context());

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

    // ---- basis -----------------------------------------------------------------------------------------

    [Fact]
    public void Every_code_and_every_note_has_exactly_one_basis_so_none_can_be_added_without_it()
    {
        var codes = typeof(Codes).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).ToList();

        Basis.OfCode.Keys.Should().BeEquivalentTo(codes, "every error and diagnostic code of the catalogue says what would have to change");
        Basis.OfNote.Keys.Should().BeEquivalentTo(Notes.All, "every explain note says what would have to change");
        Basis.OfCode.Values.Concat(Basis.OfNote.Values).Should().OnlyContain(basis => Basis.All.Contains(basis));

        // The table of DG2-ENG-2, by basis: a code that moves is a decision, written here.
        string Of(string basis) => string.Join(" ", Basis.OfCode.Where(pair => pair.Value == basis).Select(pair => pair.Key).Order(StringComparer.Ordinal));

        Of(Basis.Request).Should().Be("EMPTY_LOGICAL_GROUP FILTER_NOT_PLACEABLE INVALID_ALIAS INVALID_DATE_TRUNC_UNIT INVALID_FILTER INVALID_PAGE_LIMIT INVALID_PATH INVALID_REGEX "
            + "INVALID_SORT_DIRECTION INVALID_TIMEZONE INVALID_VARIABLE LEGACY_STAGE_UNSUPPORTED MIXED_PROJECTION MULTIPLE_PAGE_STAGES OPERAND_NOT_ARRAY STAGE_AFTER_PAGE "
            + "UNBOUND_VARIABLE UNKNOWN_AGG_FUNCTION UNKNOWN_OPERATOR UNKNOWN_REQUEST_MEMBER UNKNOWN_STAGE UNKNOWN_STAGE_MEMBER");
        Of(Basis.Model).Should().Be("ALIAS_COLLISION ANY_NOT_APPLICABLE DECIMAL_TEXT_NOT_ORDERABLE FLATTEN_NOT_RECURSIVE GROUP_ON_COLLECTION INVALID_AGGREGATE_ARGUMENT INVALID_OPERAND "
            + "LOOKUP_NOT_DECLARED LOOKUP_ON_NOT_ENTITY NOT_A_COLLECTION NOT_CONTINUABLE NOT_FILTERABLE NOT_SORTABLE NOT_STORED OPTION_NOT_APPLICABLE RESOLVE_NOT_DECLARED "
            + "RESOLVE_NOT_FILTERABLE RESOLVE_NOT_SORTABLE RESOLVE_ON_COLLECTION RESOLVE_PARENT_NOT_ITEM RESOLVE_REFUSED RESOLVE_TARGET_NOT_DECLARED UNION_CARDINALITY_MISMATCH "
            + "UNKNOWN_ENTITY UNKNOWN_ENUM_MEMBER UNKNOWN_PATH UNKNOWN_VARIANT UNWIND_ORDER");
        Of(Basis.Limits).Should().Be("BATCH_TOO_LARGE EXPLAIN_LIMIT LOOKUP_LIMIT_EXCEEDED MAX_CONDITIONS_EXCEEDED MAX_CONTINUED_STAGES_EXCEEDED MAX_GROUP_FIELDS_EXCEEDED "
            + "MAX_LOOKUP_STAGES_EXCEEDED MAX_OFFSET_EXCEEDED MAX_PIPELINE_STAGES_EXCEEDED MAX_PROJECTION_FIELDS_EXCEEDED MAX_RESOLVE_STAGES_EXCEEDED MAX_UNWIND_STAGES_EXCEEDED "
            + "MAX_VARIABLES_EXCEEDED PAGE_SIZE_EXCEEDED REGEX_TOO_LONG REQUEST_TOO_LARGE");
        Of(Basis.Owner).Should().Be("RESOLVE_UNAVAILABLE");
        Of(Basis.Data).Should().Be("ACCESS_DENIED CURSOR_INVALID INTERNAL_ERROR LOOKUP_TRUNCATED PAGE_INCOMPLETE QUERY_TIMEOUT QUERY_TOO_EXPENSIVE RESOLVE_AMBIGUOUS RESOLVE_MISSING "
            + "RESOLVE_PARTIAL RESOLVE_TIMEOUT RESOLVE_TRUNCATED RESOLVE_UNREACHABLE SEMI_JOIN_TOO_LARGE TOTAL_COUNT_CAPPED UNWIND_DEPTH_TRUNCATED");
        Of(Basis.Advisory).Should().Be("DECIMAL_TEXT_EXCLUDED ENTITY_ID_RETIRED REGEX_UNANCHORED SORT_ON_ADDON");
        Of(Basis.Organisation).Should().BeEmpty("a code is the organisation's only by its path: one under an addon bag");
        Basis.OfNote.Where(pair => pair.Value == Basis.Owner).Select(pair => pair.Key).Should().BeEquivalentTo([Notes.RemoteUnchecked, Notes.ExplainLimit, Notes.ExplainTrimmed]);
        Basis.OfNote.Where(pair => pair.Value != Basis.Owner).Should().OnlyContain(pair => pair.Value == Basis.Advisory);
    }

    [Fact]
    public async Task A_request_that_binds_and_is_checked_is_executable_and_every_note_carries_its_basis()
    {
        var answer = await ExplainAsync("""[{ "match": { "number": { "eq": "x" } } }, { "resolve": { "path": "customerId", "as": "c" } }]""");

        answer.Valid.Should().BeTrue();
        answer.Executable.Should().Be("yes");
        answer.Notes.Should().NotBeEmpty().And.OnlyContain(note => note.Basis == Basis.Advisory);
    }

    [Theory]
    [InlineData("""[{ "match": { "number": { "like": "x" } } }]""", Codes.UnknownOperator, "request")]
    [InlineData("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }]""", Codes.UnknownStageMember, "request")]
    [InlineData("""[{ "match": { "nope": { "eq": 1 } } }]""", Codes.UnknownPath, "model")]
    [InlineData("""[{ "sort": [{ "lines": "asc" }] }]""", Codes.NotSortable, "model")]
    [InlineData("""[{ "page": { "limit": 100000 } }]""", Codes.PageSizeExceeded, "limits")]
    public async Task An_error_of_the_request_the_model_or_a_limit_makes_the_request_never_executable(string pipeline, string code, string basis)
    {
        var answer = await ExplainAsync(pipeline);

        answer.Valid.Should().BeFalse();
        answer.Errors.Should().ContainSingle().Which.Should().Match<QueryValidationError>(error => error.Code == code && error.Basis == basis);
        answer.Executable.Should().Be("never");
    }

    [Theory]
    [InlineData(null, "undefined")]
    [InlineData(true, "retired")]
    public async Task A_condition_on_an_addon_key_without_a_live_definition_is_the_organisations_to_change_and_leaves_the_verdict_unknown(bool? retired, string state)
    {
        var context = BindHost.Context(addons: new Definitions(retired is null ? [] : [new AddonDefinition { Id = Guid.NewGuid(), Entity = "probe.order", Path = "weight", Kind = AddonKind.Decimal, Retired = true }]));
        var answer = await ExplainAsync("""[{ "match": { "addon.weight": { "gte": 1000 } } }]""", "probe.order", BindHost.Probe, context);

        answer.Valid.Should().BeFalse();
        var error = answer.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.NotFilterable);
        error.Basis.Should().Be("organisation", "a definition is data of the organisation: no deployment and no change of the request is needed");
        ((IReadOnlyDictionary<string, object?>)error.Params!["addon"]!).Should().BeEquivalentTo(new Dictionary<string, object?> { ["key"] = "weight", ["state"] = state });
        answer.Executable.Should().Be("unknown", "it could run for an organisation that defines the key");
    }

    [Fact]
    public async Task A_condition_on_a_defined_addon_key_is_executable_and_one_against_its_kind_is_still_the_organisations()
    {
        var context = BindHost.Context(addons: new Definitions([new AddonDefinition { Id = Guid.NewGuid(), Entity = "probe.order", Path = "weight", Kind = AddonKind.Decimal }]));

        (await ExplainAsync("""[{ "match": { "addon.weight": { "gte": 1000 } } }]""", "probe.order", BindHost.Probe, context)).Executable.Should().Be("yes");

        var wrong = await ExplainAsync("""[{ "match": { "addon.weight": { "gte": "heavy" } } }]""", "probe.order", BindHost.Probe, context);
        var error = wrong.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.InvalidOperand);
        error.Basis.Should().Be("organisation");
        ((IReadOnlyDictionary<string, object?>)error.Params!["addon"]!)["state"].Should().Be("defined");
        wrong.Executable.Should().Be("unknown");
    }

    [Fact]
    public async Task An_owner_that_is_not_asked_leaves_the_verdict_unknown_and_the_note_says_whose_it_is()
    {
        var answer = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r" } }, { "project": { "number": 1, "r.name": 1 } }]""",
            client: OwnerFleet.Client(), envelope: """, "remote": "cached" """);

        answer.Valid.Should().BeTrue();
        answer.Cache.Complete.Should().BeFalse();
        answer.Executable.Should().Be("unknown");
        answer.Notes.Should().Contain(note => note.Code == Notes.RemoteUnchecked && note.Basis == Basis.Owner);
    }

    // ---- identity --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_row_of_records_is_an_entity_with_its_key_and_a_join_does_not_change_that()
    {
        var answer = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "sort": [{ "number": "desc" }] }]""");

        Json(answer.Result!.Identity).Should().Be("""{"kind":"entity","entity":"rc.invoice","key":["id"]}""");
        Json(answer.Entry!.Shape.Identity).Should().Be(Json(answer.Result.Identity));
        answer.Stages.Should().OnlyContain(stage => stage.Shape!.Identity == null, "no stage changes what a row is: it is said once, at the entry");
    }

    [Fact]
    public async Task An_unwound_row_is_an_element_of_its_record_named_by_its_key_or_its_index_and_a_parent_kept_without_elements_is_said()
    {
        var answer = await ExplainAsync("""
            [{ "match": { "number": { "neq": "x" } } },
             { "unwind": { "path": "lines", "as": "line", "includeIndex": "at", "preserveNull": true } },
             { "unwind": { "path": "line.parts", "as": "part" } }]
            """);

        answer.Stage(0).Shape!.Identity.Should().BeNull();
        Json(answer.Stage(1).Shape!.Identity).Should().Be(
            """{"kind":"element","entity":"rc.invoice","key":["id"],"element":{"collection":"lines","alias":"line","key":"line.id","index":"at","item":{"entity":"rc.invoice","path":"lines"},"orParent":true}}""");

        var last = answer.Result!.Identity!;

        Json(last["element"]).Should().Be("""{"collection":"line.parts","alias":"part","item":{"entity":"rc.invoice","path":"lines.parts"},"orParent":false}""",
            "a part has no key member and the unwind names no index: only its position identifies it, which the row does not carry");
        last["elements"]!.AsArray().Select(element => element!["alias"]!.GetValue<string>()).Should().Equal("line", "part");
        Json(answer.Stage(2).Shape!.Identity).Should().Be(Json(last));
    }

    [Fact]
    public async Task An_unwound_lookup_alias_makes_the_row_one_child_record_with_the_record_it_was_looked_up_from_as_its_parent()
    {
        var answer = await ExplainAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices" } },
             { "unwind": { "path": "invoices", "as": "inv", "preserveNull": true } }]
            """, "rc.customer");

        Json(answer.Result!.Identity).Should().Be(
            """{"kind":"entity","entity":"rc.invoice","key":["inv.id"],"alias":"inv","orParent":true,"parent":{"entity":"rc.customer","key":["id"]}}""");
    }

    [Fact]
    public async Task A_grouped_row_is_a_bucket_of_its_keys_and_a_group_over_no_key_is_a_single_row()
    {
        var bucket = await ExplainAsync("""
            [{ "unwind": { "path": "lines", "as": "line" } },
             { "group": { "by": [{ "path": "number", "as": "n" }, { "path": "customerCode", "as": "code" }], "fields": { "count": { "count": true } } } },
             { "sort": [{ "count": "desc" }] }]
            """);

        Json(bucket.Result!.Identity).Should().Be("""{"kind":"bucket","bucket":{"keys":["n","code"]}}""");
        Json(bucket.Result.Subjects).Should().Be("[]", "a bucket holds no single record");
        Json(bucket.Result.Order).Should().Be(
            """[{"path":"count","direction":"desc"},{"path":"n","direction":"asc","tieBreak":true},{"path":"code","direction":"asc","tieBreak":true}]""",
            "the group keys the sort does not name complete the order");

        var single = await ExplainAsync("""[{ "group": { "by": [], "fields": { "count": { "count": true } } } }]""");

        Json(single.Result!.Identity).Should().Be("""{"kind":"single"}""");
        Json(single.Result.Order).Should().Be("[]");
    }

    // ---- order -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_order_lists_the_callers_fields_then_what_makes_it_total_marked_as_tie_breaks()
    {
        (await ExplainAsync("[]")).Result!.Order!.ToJsonString().Should().Be("""[{"path":"id","direction":"asc","tieBreak":true}]""");
        (await ExplainAsync("""[{ "sort": [{ "number": "desc" }] }]""")).Result!.Order!.ToJsonString().Should().Be(
            """[{"path":"number","direction":"desc"},{"path":"id","direction":"asc","tieBreak":true}]""");
        (await ExplainAsync("""[{ "sort": [{ "id": "desc" }] }]""")).Result!.Order!.ToJsonString().Should().Be("""[{"path":"id","direction":"desc"}]""", "the key the caller sorts on keeps its place");

        var named = await ExplainAsync("""[{ "unwind": { "path": "lines", "as": "line", "includeIndex": "at" } }, { "sort": [{ "number": "asc" }] }]""");
        var unnamed = await ExplainAsync("""[{ "unwind": { "path": "lines", "as": "line" } }]""");

        named.Result!.Order!.ToJsonString().Should().Be(
            """[{"path":"number","direction":"asc"},{"path":"id","direction":"asc","tieBreak":true},{"path":"at","direction":"asc","tieBreak":true}]""");
        unnamed.Result!.Order!.ToJsonString().Should().Be(
            """[{"path":"id","direction":"asc","tieBreak":true},{"positionOf":"lines","direction":"asc","tieBreak":true}]""",
            "the default order of unwound rows is the key and each element's position; a position no member names is said by its collection");
    }

    [Fact]
    public async Task The_order_explain_states_is_the_sort_the_compiler_emits()
    {
        foreach (var pipeline in new[]
        {
            "[]",
            """[{ "sort": [{ "number": "desc" }] }]""",
            """[{ "unwind": { "path": "lines", "as": "line", "includeIndex": "at" } }, { "sort": [{ "number": "asc" }] }]""",
            """[{ "unwind": { "path": "lines", "as": "line" } }, { "unwind": { "path": "line.parts", "as": "part", "includeIndex": "p" } }]""",
            """[{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "count": { "count": true } } } }, { "sort": [{ "count": "desc" }] }]""",
        })
        {
            var answer = await ExplainAsync(pipeline, envelope: """, "include": ["shape", "notes", "plan"] """);
            var emitted = answer.Plan!.Stages.Last(stage => stage!.AsObject().ContainsKey("$sort"))!["$sort"]!.AsObject().Select(pair => (pair.Key, pair.Value!.GetValue<int>())).ToList();
            var stated = answer.Result!.Order!.AsArray().Select(entry => entry!.AsObject()).ToList();

            stated.Should().HaveCount(emitted.Count, $"{pipeline}: every sort key the server orders by is stated, tie-breakers included");
            stated.Select(entry => entry["direction"]!.GetValue<string>() == "asc" ? 1 : -1).Should().Equal(emitted.Select(key => key.Item2), pipeline);
            stated.Where(entry => entry.ContainsKey("positionOf")).Should().HaveCount(emitted.Count(key => key.Key.StartsWith("__ox", StringComparison.Ordinal)), $"{pipeline}: a reserved index is a position no member names");
        }
    }

    // ---- subjects --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_subjects_are_the_rows_own_record_and_every_alias_that_holds_records_of_an_entity()
    {
        var answer = await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "ship" } },
             { "resolve": { "path": "customerIds", "as": "many", "elements": "all" } },
             { "resolve": { "path": "billing.referenceId", "as": "b" } },
             { "lookup": { "from": "rc.invoice", "path": "customerCode", "on": "c", "as": "same" } }]
            """, client: OwnerFleet.Client());

        answer.Valid.Should().BeTrue(string.Join("; ", answer.Errors.Select(error => error.Message)));
        answer.Result!.Subjects!.Select(subject => subject!.ToJsonString()).Should().Equal(
            """{"root":"","entity":"rc.invoice","key":"id","nullable":false,"many":false}""",
            """{"root":"c","entity":"rc.customer","key":"c.id","nullable":true,"many":false}""",
            """{"root":"r","entity":"crm.contact","key":"r.id","nullable":true,"many":false,"remote":true}""",
            """{"root":"ship","entity":"rc.shipment","key":"ship.id","nullable":true,"many":false}""",
            """{"root":"many","entity":"rc.customer","key":"many.id","nullable":true,"many":true}""",
            """{"root":"b","entities":["rc.shipment","rc.tour"],"key":"b.id","nullable":true,"many":false}""",
            """{"root":"same","entity":"rc.invoice","key":"same.id","nullable":false,"many":true}""");
    }

    // ---- can / why ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_column_says_what_may_be_done_with_it_and_why_not_once_per_distinct_answer()
    {
        var answer = await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "billingLineId", "as": "bl" } },
             { "project": { "number": 1, "lines": 1, "lines.customerId": 1, "c.name": 1, "r.name": 1, "bl.amount": 1 } }]
            """, client: OwnerFleet.Client());
        var result = answer.Result!;

        JsonObject Of(string path) => result.Capabilities![result.Columns.Single(column => column.Path == path).Can!]!.AsObject();

        Json(Of("number")).Should().Be(
            """{"can":{"filter":["eq","neq","gt","gte","lt","lte","in","nin","contains","startsWith","endsWith","exists","regex"],"sort":true,"group":true,"aggregate":["count","countDistinct","min","max","first","last","push"],"values":true}}""");
        Of("c.name")["can"]!["sort"]!.GetValue<bool>().Should().BeTrue("an inline join's member orders the page");
        Json(Of("r.name")).Should().Be(
            """{"can":{"filter":["eq","neq","gt","gte","lt","lte","in","nin","contains","startsWith","endsWith","exists","regex"],"sort":false,"group":false,"aggregate":[],"values":false},"why":{"filter":"semiJoin","sort":"owner","group":"owner","values":"owner","aggregate":"owner"}}""",
            "another service's member: a condition is a semi-join at its owner, and nothing else reads it before the page");
        Json(Of("bl.amount")).Should().Be(
            """{"can":{"filter":[],"sort":false,"group":false,"aggregate":[],"values":false},"why":{"filter":"joinedAfterPage","sort":"joinedAfterPage","group":"joinedAfterPage","values":"joinedAfterPage","aggregate":"joinedAfterPage"}}""");
        Of("lines")["why"]!["sort"]!.GetValue<string>().Should().Be("notScalar");

        // A member below a collection is no column of its own; its answer is the same function of the row.
        var below = ExplainFacts.Can((await BindHost.BoundAsync(ResolveModel.Model, Invoice, "[]")).FinalShape, "lines.customerId");

        below.Why!["sort"]!.GetValue<string>().Should().Be("underCollection");
        below.Can["filter"]!.AsArray().Should().NotBeEmpty("a condition through a collection matches where some element does");
        below.Can["aggregate"]!.ToJsonString().Should().Be("""["push"]""", "under a collection only push takes the path");
        below.Why!["aggregate"]!.GetValue<string>().Should().Be("underCollection");
        await BindHost.BoundAsync(ResolveModel.Model, Invoice, """[{ "group": { "by": [], "fields": { "all": { "push": "lines.customerId" } } } }]""");
        await BindHost.ErrorAsync(ResolveModel.Model, Invoice, """[{ "group": { "by": [], "fields": { "one": { "first": "lines.customerId" } } } }]""", Codes.GroupOnCollection);

        result.Columns.Should().OnlyContain(column => column.Can != null && result.Capabilities!.ContainsKey(column.Can));
        result.Capabilities!.Select(pair => pair.Value!.ToJsonString()).Should().OnlyHaveUniqueItems("equal answers are written once");
        result.Capabilities.SelectMany(pair => pair.Value!["why"]?.AsObject().Select(reason => reason.Value!.GetValue<string>()) ?? []).Should().OnlyContain(reason => ExplainFacts.Reasons.Contains(reason));
    }

    [Fact]
    public async Task What_a_column_can_is_what_its_flags_say_so_a_member_that_is_no_column_is_answered_by_its_flags_alike()
    {
        const string Pipeline = """
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "resolve": { "path": "contactId", "as": "r" } },
             { "unwind": { "path": "lines", "as": "line" } }]
            """;

        var answer = await ExplainAsync(Pipeline);
        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, Pipeline);
        var result = answer.Result!;

        result.Columns.Should().NotBeEmpty();

        foreach (var column in result.Columns)
        {
            var flags = ExplainTypes.FlagsAt(bound.FinalShape, column.Path);
            var can = result.Capabilities![column.Can!]!["can"]!.AsObject();

            can["filter"]!.ToJsonString().Should().Be(flags.ToJson()["operators"]!.ToJsonString(), $"{column.Path}: filter is the flag set's operators");
            can["sort"]!.GetValue<bool>().Should().Be(flags.Sortable, column.Path);
            can["group"]!.GetValue<bool>().Should().Be(flags.Groupable, column.Path);
            can["values"]!.GetValue<bool>().Should().Be(flags.Groupable, column.Path);
        }

        // A member no column shows (the row shows the customer's key and name) is answered from its flags the same way.
        var hidden = ExplainFacts.Can(bound.FinalShape, "c.code");

        hidden.Can.ToJsonString().Should().Be(ExplainFacts.CanOf(ExplainTypes.FlagsAt(bound.FinalShape, "c.code"), aggregable: true, Kind.String).ToJsonString());
        hidden.Why.Should().BeNull();
    }

    private sealed class Definitions(IReadOnlyList<AddonDefinition> definitions) : IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) => ValueTask.FromResult(definitions);
    }
}
