using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Contract;

/// <summary>
/// Area A: the contract header, the two bindings it selects, and the request and response
/// envelope. A request with <c>X-OxQL-Contract: 2</c> is bound one way; a request without the
/// header is bound as contract 1 by a compat-enabled host (the default), which resolves storage
/// spellings and answers storage-named rows. The cases prove both bindings against each other on
/// the same bytes. Ported from the legacy <c>contract-a</c> battery; its client-only cases (URL
/// building, the header on every route, reading refusals, aborts) went to the client's specs.
/// </summary>
[Trait("Category", "Integration")]
public class ContractATests
{
    private const string Page1 = """{ "page": { "limit": 1 } }""";

    private static Task<LabClient> Transport(int? contract = 2) => Lab.ClientAsync(LabService.Transport, contract: contract);

    private static Guid FirstShipment => Corpus.AllIds(Corpus.Shipment)[0];

    /// <summary>The <c>_id</c> of every row of a contract 1 page.</summary>
    private static IReadOnlyList<Guid> LegacyIds(WireAnswer answer) =>
        answer.Items.Select(item => Guid.Parse(item!["_id"]!.GetValue<string>())).ToList();

    // ── contract 2 against contract 1, on the same bytes ────────────────────────────────────

    [Fact]
    public async Task A03_under_contract_2_a_storage_spelling_is_refused_never_silently_resolved()
    {
        var underscore = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[ { "match": { "_id": { "eq": "{{FirstShipment}}" } } }, {{Page1}} ]""");

        underscore.ShouldRefuse("UNKNOWN_PATH", 400)["path"]!.GetValue<string>().Should().Be("_id");
        underscore.ErrorCodes.Should().Equal("UNKNOWN_PATH");

        // QRCode is the storage name of fleet.vehicle's qrCode, which the derivation does not give.
        var storageName = await (await Lab.ClientAsync(LabService.Fleet)).SendAsync(Corpus.Vehicle, $$"""[ { "match": { "QRCode": { "exists": true } } }, {{Page1}} ]""");

        storageName.ShouldRefuse("UNKNOWN_PATH", 400);
        storageName.ErrorCodes.Should().Equal("UNKNOWN_PATH");
    }

    [Fact]
    public async Task A04_with_no_contract_header_the_request_is_contract_1_so_id_resolves_and_rows_come_back_storage_named()
    {
        var one = await (await Transport(contract: null)).SendAsync(Corpus.Shipment, $$"""[ { "match": { "_id": { "eq": "{{FirstShipment}}" } } }, {{Page1}} ]""");

        one.ShouldBeOk("a missing header is contract 1 while Compat:Enabled");
        LegacyIds(one).Should().Equal([FirstShipment]);
        var row = one.Items[0]!.AsObject();
        row.ContainsKey("id").Should().BeFalse("the key comes back as _id, not id");
        row.Select(member => member.Key).Where(key => key != "_id").Should().OnlyContain(key => char.IsUpper(key[0]), "contract 1 rows are storage-named: " + row.ToJsonString());

        // The same row under contract 2 answers the wire spelling: the two bindings differ observably.
        var two = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[ { "match": { "id": { "eq": "{{FirstShipment}}" } } }, {{Page1}} ]""");

        two.ShouldHaveIds([FirstShipment]);
        two.Items[0]!.AsObject().ContainsKey("_id").Should().BeFalse();
    }

    [Fact]
    public async Task A04b_a_contract_1_request_still_does_not_resolve_a_name_the_model_never_had()
    {
        // The compat binder translates _id and the CLR and storage spellings; it is not a general
        // resolver, so a name the entity never had is still an unknown path.
        var answer = await (await Transport(contract: null)).SendAsync(Corpus.Shipment, $$"""[ { "match": { "Number": { "eq": "S-0001" } } }, {{Page1}} ]""");

        answer.ShouldRefuse("UNKNOWN_PATH", 400);
        answer.ErrorCodes.Should().Equal("UNKNOWN_PATH");
    }

    [Fact]
    public async Task A06_a_non_numeric_contract_header_falls_back_to_contract_1()
    {
        var client = (await Transport(contract: null)).WithHeader(LabIdentity.ContractHeader, "banana");

        var answer = await client.SendAsync(Corpus.Shipment, $$"""[ { "match": { "_id": { "eq": "{{FirstShipment}}" } } }, {{Page1}} ]""");

        answer.ShouldBeOk();
        LegacyIds(answer).Should().Equal([FirstShipment]);
    }

    [Fact]
    public async Task A07_a_contract_header_of_3_is_read_as_contract_2_not_refused()
    {
        var three = await Transport(contract: 3);

        var legacy = await three.SendAsync(Corpus.Shipment, $$"""[ { "match": { "_id": { "eq": "{{FirstShipment}}" } } }, {{Page1}} ]""");
        legacy.ShouldRefuse("UNKNOWN_PATH", 400, "a future contract is clamped down to 2, so _id is unknown");
        legacy.ErrorCodes.Should().Equal("UNKNOWN_PATH");

        var wire = await three.SendAsync(Corpus.Shipment, $$"""[ { "match": { "id": { "eq": "{{FirstShipment}}" } } }, {{Page1}} ]""");
        wire.ShouldHaveIds([FirstShipment]);
    }

    [Fact]
    public async Task A29_under_contract_1_operators_and_sort_directions_are_read_case_insensitively()
    {
        // Contract 1 compares exactly: startsWith is a plain prefix on the stored value.
        var matching = Corpus.SortedIds(Corpus.Shipment, "id", descending: true, filter: row => Corpus.Text(row, "shipmentNumber")?.StartsWith("S-00", StringComparison.Ordinal) == true);
        matching.Count.Should().BeGreaterThan(2);

        var answer = await (await Transport(contract: null)).SendAsync(Corpus.Shipment,
            """[ { "match": { "ShipmentNumber": { "startswith": "S-00" } } }, { "sort": [ { "Id": "DESC" } ] }, { "page": { "limit": 2 } } ]""");

        answer.ShouldBeOk();
        LegacyIds(answer).Should().Equal(Corpus.PageOf(matching, limit: 2));
    }

    [Fact]
    public async Task A30_A31_a_v1_date_hint_binds_under_contract_1_and_is_refused_under_contract_2()
    {
        var since = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected = Corpus.Where(Corpus.Shipment, row => Corpus.Date(row, "loadStart") >= since).Count;
        expected.Should().BeGreaterThan(0).And.BeLessThan(Corpus.Counts(Corpus.Shipment).A, "the corpus holds rows before the bound");

        var one = await (await Transport(contract: null)).SendAsync(Corpus.Shipment,
            """[ { "match": { "LoadStart": { "gte": { "$date": "2026-06-01T00:00:00Z" } } } }, { "page": { "limit": 1, "includeTotalCount": true } } ]""");

        one.ShouldHaveTotal(expected);

        var two = await (await Transport()).SendAsync(Corpus.Shipment,
            """[ { "match": { "loadStart": { "gte": { "$date": "2026-01-01T00:00:00Z" } } } }, { "page": { "limit": 1 } } ]""");

        two.ShouldRefuse("INVALID_OPERAND", 400)["path"]!.GetValue<string>().Should().Be("loadStart");
        two.ErrorCodes.Should().Equal("INVALID_OPERAND");
    }

    [Fact]
    public async Task A28_the_contract_1_binder_translates_CLR_spellings_against_the_folded_shape()
    {
        // Contract 1 compares exactly, so the case-variant cities (köln, KÖLN) are not Koeln.
        var expected = Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "loadAddress.city") == "Koeln").Count;
        expected.Should().BeGreaterThan(0).And.BeLessThan(Corpus.Counts(Corpus.Shipment).A);

        var legacy = await Transport(contract: null);
        var nested = await legacy.SendAsync(Corpus.Shipment, """[ { "match": { "LoadAddress.City": { "eq": "Koeln" } } }, { "page": { "limit": 1, "includeTotalCount": true } } ]""");

        nested.ShouldHaveTotal(expected);

        // An unwind alias root, and the alias reused inside a projection, both storage-spelled.
        var unwound = await legacy.SendAsync(Corpus.Shipment, """[ { "unwind": { "path": "Items", "as": "It" } }, { "project": { "_id": 1, "It.OrderNumber": 1 } }, { "page": { "limit": 2 } } ]""");

        unwound.ShouldBeOk();
        var row = unwound.Items[0]!.AsObject();
        row.Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("It", "_id");
        row["It"]!.AsObject().Select(member => member.Key).Should().Equal("OrderNumber");
    }

    [Theory]
    [InlineData("v1 lookup", """{ "lookup": { "localPath": "a", "foreignPath": "b", "as": "x" } }""")]
    [InlineData("v1 resolve", """{ "resolve": { "source": "a", "as": "x" } }""")]
    [InlineData("v2-shaped lookup under contract 1", """{ "lookup": { "from": "transport.shipment", "path": "id", "as": "x" } }""")]
    public async Task A32_A33_under_contract_1_every_lookup_and_resolve_is_refused_as_a_legacy_stage(string label, string stage)
    {
        var answer = await (await Transport(contract: null)).SendAsync(Corpus.Shipment, $"[ {stage}, {Page1} ]");

        answer.ShouldRefuse("LEGACY_STAGE_UNSUPPORTED", 400, label)["message"]!.GetValue<string>().Should().Contain("X-OxQL-Contract: 2", label);
        answer.ErrorCodes.Should().Equal(["LEGACY_STAGE_UNSUPPORTED"], label);
    }

    /// <summary>
    /// Contract 1 never reaches an owner (Q14): a v2-shaped lookup of another service's entity, at a host
    /// that reaches its owner, is refused as every contract 1 lookup is, by the run and by explain alike,
    /// before anything is bound or sent.
    /// </summary>
    [Fact]
    public async Task A33b_under_contract_1_a_lookup_of_another_services_entity_is_refused_as_a_legacy_stage_not_looked_up_at_its_owner()
    {
        var ledger = await Lab.ClientAsync(LabService.Ledger, contract: null);
        var stage = """{ "lookup": { "from": "transport.shipment", "path": "customer.id", "as": "shipments" } }""";

        var answer = await ledger.SendAsync("ledger.transaction", $"[ {stage}, {Page1} ]");

        answer.ShouldRefuse("LEGACY_STAGE_UNSUPPORTED", 400)["message"]!.GetValue<string>().Should().Contain("X-OxQL-Contract: 2");
        answer.ErrorCodes.Should().Equal(["LEGACY_STAGE_UNSUPPORTED"]);

        var explained = await ledger.ExplainHereAsync($$"""{ "entityType": "ledger.transaction", "pipeline": [ {{stage}} ] }""");

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body!["errors"]!.AsArray().Select(error => error!["code"]!.GetValue<string>()).Should().Equal(["LEGACY_STAGE_UNSUPPORTED"]);
        explained.Body!["steps"]?.AsArray().Should().NotContain(step => step!["owner"] != null, "no owner is planned for contract 1");
    }

    [Fact]
    public async Task A34_under_contract_2_a_lookup_carrying_v1_members_is_refused_and_the_message_names_the_accepted_ones()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[ { "lookup": { "localPath": "a", "foreignPath": "b", "as": "x" } }, {{Page1}} ]""");

        answer.ShouldRefuse("UNKNOWN_STAGE_MEMBER", 400)["message"]!.GetValue<string>().Should().Contain("from, path, as, select, filter, limit");
        answer.ErrorCodes.Should().Equal("UNKNOWN_STAGE_MEMBER");
    }

    // ── envelope, status and error shape ────────────────────────────────────────────────────

    [Fact]
    public async Task A15_A16_the_refusal_envelope_is_exactly_type_title_errors_and_the_status_follows_the_reason_class()
    {
        var validation = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[ { "match": { "nope": { "eq": 1 } } }, {{Page1}} ]""");

        validation.StatusCode.Should().Be(400, validation.ToString());
        validation.Body!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("errors", "title", "type");
        validation.Type.Should().Be("validation_error");
        validation.Errors[0].Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("code", "message", "path", "stage");

        // access_denied is 403: a request whose context carries no organisation. The legacy rig
        // could not send one (its interceptor always attached a token); the lab can.
        var denied = await (await Transport()).As((Guid?)null).SendAsync(Corpus.Shipment, $"[ {Page1} ]");

        denied.ShouldRefuse("ACCESS_DENIED", 403);
        denied.Type.Should().Be("access_denied");
        denied.Body!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("errors", "title", "type");
    }

    [Fact]
    public async Task A23_A24_binding_reports_every_error_of_a_request_at_once()
    {
        var two = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[ { "match": { "nope": { "eq": 1 } } }, { "sort": [ { "alsoNope": "asc" } ] }, {{Page1}} ]""");

        two.StatusCode.Should().Be(400, two.ToString());
        two.ErrorCodes.Should().Equal("UNKNOWN_PATH", "UNKNOWN_PATH");
        two.Errors.Select(error => error["path"]!.GetValue<string>()).Should().Equal("nope", "alsoNope");
        two.Errors.Select(error => error["stage"]!.GetValue<int>()).Should().Equal(0, 1);

        // A limit breach does not stop binding either: the bad path is still reported beside it.
        var limitAndPath = await (await Transport()).SendAsync(Corpus.Shipment, """[ { "match": { "nope": { "eq": 1 } } }, { "page": { "limit": 100000 } } ]""");

        limitAndPath.StatusCode.Should().Be(400, limitAndPath.ToString());
        limitAndPath.ErrorCodes.Should().BeEquivalentTo(["UNKNOWN_PATH", "PAGE_SIZE_EXCEEDED"]);
    }

    [Fact]
    public async Task A25_stage_is_the_callers_pipeline_index_and_a_request_level_error_carries_none()
    {
        var transport = await Transport();

        var staged = await transport.SendAsync(Corpus.Shipment, """[ { "page": { "limit": 1 } }, { "match": { "nope": { "eq": 1 } } } ]""");
        staged.StatusCode.Should().Be(400, staged.ToString());
        staged.Errors.Should().Contain(error => error["stage"] != null && error["stage"]!.GetValue<int>() == 1, staged.ToString());

        var requestLevel = await transport.SendAsync("transport.nope", $"[ {Page1} ]");
        requestLevel.ShouldRefuse("UNKNOWN_ENTITY", 400);
        requestLevel.ErrorCodes.Should().Equal("UNKNOWN_ENTITY");
        requestLevel.Errors[0].ContainsKey("stage").Should().BeFalse("the member is omitted, not sent as an explicit null: " + requestLevel);

        var tooLarge = await transport.SendAsync(Corpus.Shipment, OversizedPipeline());
        tooLarge.ShouldRefuse("REQUEST_TOO_LARGE", 413);
        tooLarge.ErrorCodes.Should().Equal("REQUEST_TOO_LARGE");
        tooLarge.Errors[0].ContainsKey("stage").Should().BeFalse(tooLarge.ToString());
    }

    [Fact]
    public async Task A10_a_body_above_max_request_bytes_is_refused_413_before_it_is_read()
    {
        var pipeline = OversizedPipeline();
        Json.Request(Corpus.Shipment, pipeline).ToJsonString().Length.Should().BeGreaterThan(262_144, "the body must exceed the default MaxRequestBytes");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, pipeline);

        answer.ShouldRefuse("REQUEST_TOO_LARGE", 413)["message"]!.GetValue<string>().Should().Contain("262144");
        answer.ErrorCodes.Should().Equal("REQUEST_TOO_LARGE");
    }

    [Fact]
    public async Task A11_A12_A13_malformed_JSON_and_a_missing_required_member_are_ProblemDetails_not_an_OxQL_refusal()
    {
        var transport = await Transport();

        var noEntity = await transport.QueryAsync($$"""{ "pipeline": [ {{Page1}} ] }""");
        var noPipeline = await transport.QueryAsync($$"""{ "entityType": "{{Corpus.Shipment}}" }""");

        foreach (var answer in new[] { noEntity, noPipeline })
        {
            answer.StatusCode.Should().Be(400, answer.ToString());
            answer.IsProblemDetails.Should().BeTrue(answer.ToString());
            answer.Body!["errors"].Should().BeOfType<JsonObject>("errors is an object here, not the OxQL list: " + answer);
            answer.ErrorCodes.Should().BeEmpty();
        }

        var malformed = await transport.PostAsync("OxQL/query", "");
        malformed.StatusCode.Should().BeOneOf([400, 415], malformed.ToString());
        malformed.ErrorCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task A14_an_empty_pipeline_executes_as_a_default_first_page()
    {
        var expected = Corpus.AllIds(Corpus.Shipment);
        expected.Count.Should().BeLessThanOrEqualTo(100, "the default page size is 100, so the whole entity must fit one default page");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, "[]");

        answer.ShouldHaveIds(expected, "no sort means ascending by id");
        answer.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task A36_next_cursor_total_count_capped_and_diagnostics_are_omitted_when_null()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $"[ {Page1} ]");

        answer.ShouldBeOk();
        answer.PageInfo.Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("hasNextPage", "nextCursor");
        answer.Body!.AsObject().ContainsKey("diagnostics").Should().BeFalse();

        // …and the cursor is omitted, not null, on the last page.
        var last = await (await Transport()).SendAsync(Corpus.Shipment, """[ { "page": { "limit": 500 } } ]""");
        last.PageInfo.Select(member => member.Key).Should().Equal("hasNextPage");
    }

    [Fact]
    public async Task A27_the_query_route_answers_POST_only()
    {
        var transport = await Transport();

        var get = await transport.GetAsync("OxQL/query");
        get.StatusCode.Should().BeOneOf([404, 405], "no GET route serves the query endpoint: " + get);

        var emptyPost = await transport.PostAsync("OxQL/query", "", "application/json");
        emptyPost.StatusCode.Should().BeOneOf([400, 415], emptyPost.ToString());

        var postHealth = await transport.PostAsync("OxQL/health", "{}");
        postHealth.StatusCode.Should().BeOneOf([404, 405], "health answers GET only: " + postHealth);
    }

    [Fact]
    public async Task A35_the_resource_planners_own_pipelines_reach_a_v2_host_unchanged_as_contract_1()
    {
        // Storage paths, a $date operand, options.ignoreCase and no contract header at all, as the
        // planner's generated client sends them. Contract 1 folds only where ignoreCase says so
        // (a case-only pattern: "KÖLN" does not contain "oe"), and sorts exactly.
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var expected = Corpus.IdsOf(Corpus.Sorted(Corpus.Shipment, [("loadStart", false)], filter: row =>
            Corpus.ValueAt(row, "isDeleted")!.AsBoolean == false &&
            Corpus.Text(row, "loadAddress.city")?.Contains("oe", StringComparison.OrdinalIgnoreCase) == true &&
            Corpus.Date(row, "loadStart") is { } start && start >= from && start <= to));
        expected.Should().NotBeEmpty();
        expected.Count.Should().BeLessThan(Corpus.Counts(Corpus.Shipment).A);

        var legacy = await Transport(contract: null);
        var answer = await legacy.SendAsync(Corpus.Shipment, """
            [ { "match": { "and": [
                  { "IsDeleted": { "eq": false } },
                  { "LoadAddress.City": { "contains": "oe", "options": { "ignoreCase": true } } },
                  { "LoadStart": { "gte": { "$date": "2026-01-01T00:00:00Z" } } },
                  { "LoadStart": { "lte": { "$date": "2027-12-31T00:00:00Z" } } } ] } },
              { "sort": [ { "LoadStart": "asc" } ] },
              { "page": { "limit": 500, "cursor": null, "includeTotalCount": false } } ]
            """);

        answer.ShouldBeOk("the planner's shape was refused");
        LegacyIds(answer).Should().Equal(expected, "and the rows come back v1-shaped, which is what its normaliser expects");

        // The template loader's contains + ignoreCase on a storage path, the other half.
        var templates = Corpus.IdsOf(Corpus.Sorted(Corpus.Template, [("templateName", false)], filter: row =>
            Corpus.ValueAt(row, "isDeleted")!.AsBoolean == false &&
            Corpus.Text(row, "templateName")?.Contains("t-00", StringComparison.OrdinalIgnoreCase) == true, strings: StringOrder.Binary));
        templates.Count.Should().BeGreaterThan(10);

        var loader = await legacy.SendAsync(Corpus.Template, """
            [ { "match": { "and": [ { "IsDeleted": { "eq": false } }, { "TemplateName": { "contains": "t-00", "options": { "ignoreCase": true } } } ] } },
              { "sort": [ { "TemplateName": "asc" } ] },
              { "page": { "limit": 10, "cursor": null, "includeTotalCount": false } } ]
            """);

        loader.ShouldBeOk();
        LegacyIds(loader).Should().Equal(Corpus.PageOf(templates, limit: 10));
    }

    /// <summary>A pipeline whose body is well above the default <c>MaxRequestBytes</c> (262 144).</summary>
    private static JsonArray OversizedPipeline()
    {
        var or = new JsonArray(Enumerable.Range(0, 6000).Select(i => (JsonNode?)new JsonObject { ["shipmentNumber"] = new JsonObject { ["eq"] = new string('x', 40) + i } }).ToArray());

        return [new JsonObject { ["match"] = new JsonObject { ["or"] = or } }, JsonNode.Parse(Page1)];
    }
}
