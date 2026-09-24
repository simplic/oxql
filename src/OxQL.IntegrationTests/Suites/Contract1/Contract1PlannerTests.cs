using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Contract1;

/// <summary>
/// The contract-1 battery: a contract-1 planner client's traffic replayed row for
/// row. The planner builds its bodies by hand, posts them with no <c>X-OxQL-Contract</c> header
/// and reads storage-spelled documents back; its match builders are reproduced here verbatim,
/// with the lab's member names (the lab shipment is the planner's shipment shape; the lab
/// template is thinner and carries no addresses and no department). Every expected id list is
/// computed from the corpus.
/// </summary>
[Trait("Category", "Integration")]
public class Contract1PlannerTests
{
    private static async Task<LabClient> V1() => (await Lab.ClientAsync(LabService.Transport)).Contract(null);

    /// <summary>The planner's filter set.</summary>
    private sealed record Filters(DateTime? From = null, DateTime? To = null, string? LoadZip = null, string? DeliveryZip = null, IReadOnlyList<string>? Departments = null, string? TemplateName = null);

    private static readonly Filters Wide = new(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static string Department => Corpus.GuidAt(Corpus.Rows(Corpus.Shipment)[0], "department.id")!.Value.ToString("D");

    // ── the planner's match builders, verbatim ──────────────────────────────────────────────

    private static void AddAddressAndDepartmentConditions(JsonArray and, Filters filters)
    {
        if (filters.LoadZip?.Trim() is { Length: > 0 } load)
            and.Add(JsonNode.Parse($$"""{ "or": [{ "LoadAddress.Zipcode": { "contains": "{{load}}", "options": { "ignoreCase": true } } }, { "LoadAddress.City": { "contains": "{{load}}", "options": { "ignoreCase": true } } }] }"""));

        if (filters.DeliveryZip?.Trim() is { Length: > 0 } delivery)
            and.Add(JsonNode.Parse($$"""{ "or": [{ "DeliveryAddress.Zipcode": { "contains": "{{delivery}}", "options": { "ignoreCase": true } } }, { "DeliveryAddress.City": { "contains": "{{delivery}}", "options": { "ignoreCase": true } } }] }"""));

        if (filters.Departments is { Count: > 0 } departments)
            and.Add(new JsonObject { ["or"] = new JsonArray(departments.Select(id => (JsonNode?)JsonNode.Parse($$"""{ "Department._id": { "eq": "{{id}}" } }""")).ToArray()) });
    }

    private static JsonObject ShipmentMatch(Filters filters)
    {
        var and = new JsonArray(
            JsonNode.Parse("""{ "IsDeleted": { "eq": false } }"""),
            JsonNode.Parse("""{ "IsTemplate": { "eq": false } }"""),
            JsonNode.Parse("""{ "or": [{ "Status.Resolver": { "eq": "open" } }, { "Status.Resolver": { "eq": "partially_planned" } }] }"""),
            JsonNode.Parse($$"""{ "LoadStart": { "gte": { "$date": "{{filters.From!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}}" } } }"""),
            JsonNode.Parse($$"""{ "LoadStart": { "lte": { "$date": "{{filters.To!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}}" } } }"""));

        AddAddressAndDepartmentConditions(and, filters);

        return new JsonObject { ["and"] = and };
    }

    private static JsonObject TemplateMatch(Filters filters)
    {
        var and = new JsonArray(JsonNode.Parse("""{ "IsDeleted": { "eq": false } }"""));

        AddAddressAndDepartmentConditions(and, filters);

        if (filters.TemplateName?.Trim() is { Length: > 0 } name)
            and.Add(JsonNode.Parse($$"""{ "TemplateName": { "contains": "{{name}}", "options": { "ignoreCase": true } } }"""));

        return new JsonObject { ["and"] = and };
    }

    private static string Pipeline(JsonObject match, string sort, int limit = 500, string? cursor = null) =>
        new JsonArray(
            new JsonObject { ["match"] = match },
            JsonNode.Parse($$"""{ "sort": [{ "{{sort}}": "asc" }] }"""),
            new JsonObject { ["page"] = new JsonObject { ["limit"] = limit, ["cursor"] = cursor, ["includeTotalCount"] = false } }).ToJsonString();

    // ── the oracle ──────────────────────────────────────────────────────────────────────────

    /// <summary>A contract-1 ignoreCase <c>contains</c>: a case-insensitive pattern, no accent folding.</summary>
    private static bool ContainsCi(CorpusRow row, string path, string needle) =>
        Corpus.Text(row, path) is { } text && text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool AddressAndDepartment(CorpusRow row, Filters filters) =>
        (filters.LoadZip is null || ContainsCi(row, "loadAddress.zipcode", filters.LoadZip) || ContainsCi(row, "loadAddress.city", filters.LoadZip))
        && (filters.DeliveryZip is null || ContainsCi(row, "deliveryAddress.zipcode", filters.DeliveryZip) || ContainsCi(row, "deliveryAddress.city", filters.DeliveryZip))
        && (filters.Departments is not { Count: > 0 } || filters.Departments.Contains(Corpus.GuidAt(row, "department.id")?.ToString("D") ?? ""));

    private static Func<CorpusRow, bool> ShipmentPredicate(Filters filters) => row =>
        Corpus.ValueAt(row, "isDeleted") is BsonBoolean { Value: false }
        && Corpus.ValueAt(row, "isTemplate") is BsonBoolean { Value: false }
        && Corpus.Text(row, "status.resolver") is "open" or "partially_planned"
        && Corpus.Date(row, "loadStart") is { } load && load >= filters.From && load <= filters.To
        && AddressAndDepartment(row, filters);

    private static Func<CorpusRow, bool> TemplatePredicate(Filters filters) => row =>
        Corpus.ValueAt(row, "isDeleted") is BsonBoolean { Value: false }
        && (filters.TemplateName?.Trim() is not { Length: > 0 } name || ContainsCi(row, "templateName", name));

    private static IReadOnlyList<Guid> Ids(WireAnswer answer) => answer.Items.Select(item => Guid.Parse(item!["_id"]!.GetValue<string>())).ToList();

    // ── step 2 · row identity ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task W3_01_the_pascal_case_route_the_generated_client_posts_to_and_the_lower_case_route_are_one_endpoint()
    {
        var client = await V1();
        var body = Json.Request(Corpus.Shipment, Pipeline(ShipmentMatch(Wide), "LoadStart")).ToJsonString();

        var pascal = await client.PostAsync("OxQL/query", body);
        var lower = await client.PostAsync("oxql/query", body);

        pascal.ShouldBeOk();
        Ids(pascal).Should().Equal(Corpus.SortedIds(Corpus.Shipment, "loadStart", filter: ShipmentPredicate(Wide)));
        lower.Text.Should().Be(pascal.Text);
    }

    [Fact]
    public async Task W3_02_every_planner_shipment_filter_combination_answers_the_oracle_ids_in_order()
    {
        var window = new Filters(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc));
        var combinations = new (string What, Filters Filters)[]
        {
            ("no optional filter, whole window", Wide),
            ("date window only", window),
            ("department only", Wide with { Departments = [Department] }),
            ("a department that exists nowhere", Wide with { Departments = ["00000000-0000-0000-0000-0000000000ff"] }),
            ("loadZip by postcode", Wide with { LoadZip = "50667" }),
            ("loadZip by city, lower-cased (ignoreCase)", Wide with { LoadZip = "koeln" }),
            ("deliveryZip by city, upper-cased (ignoreCase)", Wide with { DeliveryZip = "HAMBURG" }),
            ("all of them together", window with { LoadZip = "koeln", DeliveryZip = "hamburg", Departments = [Department] }),
        };
        var client = await V1();

        foreach (var (what, filters) in combinations)
        {
            var expected = Corpus.SortedIds(Corpus.Shipment, "loadStart", filter: ShipmentPredicate(filters));

            var answer = await client.SendAsync(Corpus.Shipment, Pipeline(ShipmentMatch(filters), "LoadStart"));

            answer.ShouldBeOk(what);
            Ids(answer).Should().Equal(expected, what);
        }

        Corpus.SortedIds(Corpus.Shipment, "loadStart", filter: ShipmentPredicate(Wide)).Should().NotBeEmpty();
        Corpus.SortedIds(Corpus.Shipment, "loadStart", filter: ShipmentPredicate(combinations[^1].Filters)).Should().NotBeEmpty("the combined filter must select something");
    }

    [Fact]
    public async Task W3_03_the_planner_template_filters_answer_the_oracle_ids_in_order_with_the_corpus_nulls_in_them()
    {
        var combinations = new (string What, Filters Filters)[]
        {
            ("no optional filter", new Filters()),
            ("templateName contains, lower-cased (ignoreCase)", new Filters(TemplateName: "t-0001")),
            ("templateName contains the duplicated value", new Filters(TemplateName: "T-DUP")),
            ("templateName that matches nothing", new Filters(TemplateName: "ZZ-NOPE")),
        };
        var client = await V1();

        foreach (var (what, filters) in combinations)
        {
            var expected = Corpus.SortedIds(Corpus.Template, "templateName", filter: TemplatePredicate(filters), strings: StringOrder.Binary);

            var answer = await client.SendAsync(Corpus.Template, Pipeline(TemplateMatch(filters), "TemplateName"));

            answer.ShouldBeOk(what);
            Ids(answer).Should().Equal(Corpus.PageOf(expected, 500), what);
        }

        // Adapted: the lab template has no department, so the planner's department condition on it
        // is a coded contract-1 refusal rather than the legacy host's empty page.
        var department = await client.SendAsync(Corpus.Template, Pipeline(TemplateMatch(new Filters(Departments: [Department])), "TemplateName"));
        department.ShouldRefuse("UNKNOWN_PATH", 400);
    }

    [Fact]
    public async Task W3_04_the_planners_contract_1_cursor_walk_is_disjoint_exhaustive_and_equals_the_oracle()
    {
        var client = await V1();

        async Task<(List<Guid> Ids, int Pages)> Walk(Filters filters, int limit)
        {
            var ids = new List<Guid>();
            var seen = new HashSet<string>();
            string? cursor = null;
            var pages = 0;

            do
            {
                var answer = await client.SendAsync(Corpus.Template, Pipeline(TemplateMatch(filters), "TemplateName", limit, cursor));
                answer.ShouldBeOk($"page {pages + 1}");
                ids.AddRange(Ids(answer));

                if (cursor is not null)
                    seen.Add(cursor);

                cursor = answer.HasNextPage ? answer.NextCursor : null;
                (cursor is null || !seen.Contains(cursor)).Should().BeTrue($"the service repeated a cursor at page {pages + 1}");
                pages++;
            }
            while (cursor is not null && pages < 200);

            cursor.Should().BeNull("the walk hit the planner's safety cap instead of the last page");

            return (ids, pages);
        }

        var expected = Corpus.SortedIds(Corpus.Template, "templateName", filter: TemplatePredicate(new Filters()), strings: StringOrder.Binary);
        var full = await Walk(new Filters(), 500);
        full.Pages.Should().Be((expected.Count + 499) / 500);
        full.Ids.Should().OnlyHaveUniqueItems().And.Equal(expected);

        var narrow = new Filters(TemplateName: "t-0001");
        (await Walk(narrow, 100)).Ids.Should().Equal(Corpus.SortedIds(Corpus.Template, "templateName", filter: TemplatePredicate(narrow), strings: StringOrder.Binary));
    }

    [Fact]
    public async Task W3_05_under_a_contract_1_sort_the_null_and_the_absent_names_come_first_as_one_bracket_in_id_order()
    {
        var nameless = Corpus.Rows(Corpus.Template).Where(row => Corpus.Text(row, "templateName") is null).ToList();
        nameless.Where(row => Corpus.Missing(row, "templateName")).Should().HaveCount(10);
        nameless.Where(row => Corpus.Null(row, "templateName")).Should().HaveCount(10);

        var answer = await (await V1()).SendAsync(Corpus.Template, """[{ "sort": [{ "TemplateName": "asc" }] }, { "project": { "TemplateName": 1 } }, { "page": { "limit": 30 } }]""");

        answer.ShouldBeOk();
        Ids(answer).Take(nameless.Count).Should().Equal(nameless.Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => row.Id));
        answer.Items.Skip(nameless.Count).Should().AllSatisfy(item => item!["TemplateName"]!.GetValue<string>().Should().NotBeNullOrEmpty());
        // A null travels as null, an absent member not at all.
        answer.Items.Take(nameless.Count).Count(item => item!.AsObject().ContainsKey("TemplateName")).Should().Be(10);
    }

    // ── step 4 · the compat difference table ────────────────────────────────────────────────

    /// <summary>A contract-1 document in contract-2 spelling: <c>_id</c> to <c>id</c>, PascalCase to camelCase, the bag's keys verbatim.</summary>
    private static JsonNode? Normalise(JsonNode? node, bool bag = false) => node switch
    {
        JsonObject item => new JsonObject(item.Select(member =>
        {
            var key = bag ? member.Key : member.Key == "_id" ? "id" : char.ToLowerInvariant(member.Key[0]) + member.Key[1..];
            return KeyValuePair.Create(key, Normalise(member.Value, bag: !bag && key == "addon"));
        })),
        JsonArray list => new JsonArray(list.Select(element => Normalise(element)).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    private static string Classify(JsonNode? c1, JsonNode? c2)
    {
        if (c1 is JsonValue a && c2 is JsonValue b)
        {
            // A defined date addon key: contract 1 writes the stored midnight-UTC instant, contract 2 the day.
            if (a.TryGetValue<string>(out var instant) && b.TryGetValue<string>(out var day)
                && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && DateTimeOffset.TryParse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stored)
                && stored.Offset == TimeSpan.Zero && stored.UtcDateTime == date.ToDateTime(TimeOnly.MinValue))
                return "date-only";

            if (a.TryGetValue<string>(out var left) && b.TryGetValue<string>(out var right)
                && DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.None, out var x)
                && DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.None, out var y) && x == y)
                return "date-fraction";

            if (a.GetValueKind() == JsonValueKind.Number && b.TryGetValue<string>(out var digits)
                && decimal.TryParse(a.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && decimal.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var text) && number == text)
                return "number-as-string";
        }

        return "UNDECLARED";
    }

    [Fact]
    public async Task W3_06_contract_1_and_contract_2_select_the_same_rows_and_differ_only_in_declared_encodings_including_the_long_by_design()
    {
        var expected = Corpus.AllIds(Corpus.Shipment);
        var transport = await Lab.ClientAsync(LabService.Transport);

        var c1 = await transport.Contract(null).SendAsync(Corpus.Shipment, """[{ "sort": [{ "_id": "asc" }] }, { "page": { "limit": 500 } }]""");
        var c2 = await transport.SendAsync(Corpus.Shipment, """[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");

        c1.ShouldBeOk();
        Ids(c1).Should().Equal(expected);
        c2.ShouldHaveIds(expected);

        var differences = new List<(string Path, string Category, string C1, string C2)>();

        void Walk(string path, JsonNode? a, JsonNode? b)
        {
            if (a is JsonObject left && b is JsonObject right)
            {
                foreach (var key in left.Select(member => member.Key).Union(right.Select(member => member.Key)))
                {
                    if (!left.ContainsKey(key) || !right.ContainsKey(key))
                        differences.Add(($"{path}.{key}", "MEMBER-SET", left.ContainsKey(key) ? "present" : "absent", right.ContainsKey(key) ? "present" : "absent"));
                    else
                        Walk(path.Length == 0 ? key : $"{path}.{key}", left[key], right[key]);
                }

                return;
            }

            if (a is JsonArray first && b is JsonArray second)
            {
                if (first.Count != second.Count)
                    differences.Add((path, "UNDECLARED", $"length {first.Count}", $"length {second.Count}"));
                else
                    for (var index = 0; index < first.Count; index++)
                        Walk(path, first[index], second[index]);

                return;
            }

            if ((a?.ToJsonString() ?? "null") != (b?.ToJsonString() ?? "null"))
                differences.Add((path, Classify(a, b), a?.ToJsonString() ?? "null", b?.ToJsonString() ?? "null"));
        }

        foreach (var (row1, row2) in c1.Items.Zip(c2.Items))
            Walk("", Normalise(row1), row2);

        differences.Where(difference => difference.Category is "UNDECLARED" or "MEMBER-SET").Should().BeEmpty("contract 1 adds, drops or changes nothing beyond the declared encodings");
        differences.Select(difference => difference.Category).Distinct().Should().BeEquivalentTo(["date-fraction", "number-as-string", "date-only"]);
        differences.Where(difference => difference.Category == "date-only").Select(difference => difference.Path).Distinct().Should().Equal(["addon.probationEnd"], "only a defined date addon key; the v1 converter writes it as the stored instant");

        // FC2, by design and documented: contract 1 renders an Int64 through the v1 converter as a
        // JSON number, so a JavaScript reader rounds 2^53 + 1 to 2^53; contract 2 writes the digits.
        var rich = Corpus.Row(Corpus.Shipment, "addon-rich");
        var tour = Corpus.ValueAt(rich, "addon.tourCount")!.AsInt64;
        tour.Should().BeGreaterThan(1L << 53);
        c1.Text.Should().Contain($"\"tourCount\":{tour}", "contract 1: a bare number, digits exact in the body");
        c2.Text.Should().Contain($"\"tourCount\":\"{tour}\"", "contract 2: a string of digits");
        ((long)double.Parse(tour.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).Should().Be(1L << 53, "what a JavaScript client reads from the contract-1 body");
        differences.Should().Contain(difference => difference.Path == "addon.tourCount" && difference.Category == "number-as-string");
    }

    // ── step 6 · the guid operand and the v1 trap ───────────────────────────────────────────

    [Fact]
    public async Task W3_07_a_bare_guid_string_on_Department_id_matches_and_the_clr_spelling_Department_Id_selects_the_same_rows()
    {
        var expected = Corpus.SortedIds(Corpus.Shipment, "loadStart", filter: row =>
            Corpus.ValueAt(row, "isDeleted") is BsonBoolean { Value: false } && Corpus.GuidAt(row, "department.id")?.ToString("D") == Department);
        expected.Should().NotBeEmpty();
        var client = await V1();

        var hit = await client.SendAsync(Corpus.Shipment, $$"""[{ "match": { "and": [{ "IsDeleted": { "eq": false } }, { "or": [{ "Department._id": { "eq": "{{Department}}" } }] }] } }, { "sort": [{ "LoadStart": "asc" }] }, { "page": { "limit": 500 } }]""");
        hit.ShouldBeOk();
        Ids(hit).Should().Equal(expected);

        var trap = await client.SendAsync(Corpus.Shipment, $$"""[{ "match": { "and": [{ "IsDeleted": { "eq": false } }, { "Department.Id": { "eq": "{{Department}}" } }] } }, { "page": { "limit": 500 } }]""");
        trap.ShouldBeOk("Department.Id must not be a refusal under compat");
        Ids(trap).Should().BeEquivalentTo(expected);
    }

    // ── step 7 · entity ids ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task W3_08_an_entity_id_the_host_does_not_declare_is_a_coded_refusal_not_an_empty_page()
    {
        var answer = await (await V1()).SendAsync("shipment_template", """[{ "page": { "limit": 1 } }]""");

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().Equal(["UNKNOWN_ENTITY"]);
    }

    [Fact]
    public async Task W3_09_a_contract_1_entity_id_resolves_case_insensitively_as_v1_resolved_it()
    {
        var answer = await (await V1()).SendAsync(Corpus.Template.ToUpperInvariant(), """[{ "sort": [{ "_id": "asc" }] }, { "page": { "limit": 3 } }]""");

        answer.ShouldBeOk("v1 answered 200 here; compat mode must not start refusing it");
        Ids(answer).Should().Equal(Corpus.PageOf(Corpus.AllIds(Corpus.Template), 3));
    }

    // ── the reviewers' findings, executed ───────────────────────────────────────────────────

    [Fact]
    public async Task W3_10_a_null_stage_member_is_a_coded_4xx_refusal_under_both_contracts()
    {
        var transport = await Lab.ClientAsync(LabService.Transport);

        foreach (var stage in new[] { "match", "sort", "project", "group", "unwind", "page" })
            foreach (var client in new[] { transport.Contract(null), transport })
            {
                var answer = await client.QueryAsync($$"""{ "entityType": "transport.shipment_template", "pipeline": [{ "{{stage}}": null }, { "page": { "limit": 1 } }] }""");

                answer.StatusCode.Should().BeInRange(400, 499, $"{stage} under {client}: {answer}");
                answer.ErrorCodes.Should().NotBeEmpty();
            }
    }

    [Fact]
    public async Task W3_11_a_null_pipeline_entry_is_a_coded_400_under_compat_as_under_contract_2()
    {
        var transport = await Lab.ClientAsync(LabService.Transport);
        var body = """{ "entityType": "transport.shipment_template", "pipeline": [null, { "page": { "limit": 1 } }] }""";

        var compat = await transport.Contract(null).QueryAsync(body);
        var v2 = await transport.QueryAsync(body);

        v2.StatusCode.Should().Be(400);
        v2.ErrorCodes.Should().Equal(["UNKNOWN_STAGE"]);
        compat.StatusCode.Should().Be(400, compat.ToString());
        compat.ErrorCodes.Should().Equal(["UNKNOWN_STAGE"]);
    }
}
