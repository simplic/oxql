using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Paging;

/// <summary>
/// Area U: every published limit and cap. The shape of every case is the point: at the limit the
/// query is accepted and answers real rows, one over it is refused with the named code, and the
/// refusal names the number the service allows. A limit probed only from above proves nothing:
/// an engine that refused everything would pass.
/// <para>
/// Ported from the legacy <c>paging-limits</c> battery (engine half). The legacy battery reached
/// only the limits the default configuration makes reachable; the resolve and semi-join limits
/// are reached here through variants that lower them, and the count cap through the 100 001-row
/// bulk organisation.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class PagingLimitsTests
{
    /// <summary>The defaults every host runs with, as <c>OxQLOptions</c> documents them; twenty-five names since 2.1.</summary>
    private static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>
    {
        ["maxPageSize"] = 500,
        ["defaultPageSize"] = 100,
        ["maxPipelineStages"] = 20,
        ["maxLookupStages"] = 5,
        ["maxUnwindStages"] = 5,
        ["maxResolveStages"] = 8,
        ["maxGroupFields"] = 20,
        ["maxProjectionFields"] = 500,
        ["maxConditions"] = 200,
        ["maxVariables"] = 64,
        ["maxOffset"] = 5_000,
        ["countCap"] = 100_000,
        ["maxSemiJoinIds"] = 5_000,
        ["resolveKeyChunk"] = 500,
        ["maxResolveKeys"] = 10_000,
        ["maxRequestBytes"] = 262_144,
        ["maxBatchQueries"] = 10,
        ["regexMaxLength"] = 200,
        ["maxLookupLimit"] = 100,
        ["maxFlattenDepth"] = 5,
        ["maxContinuedStages"] = 8,
        ["maxReportPageSize"] = 5_000,
        ["maxReportedRows"] = 50,
        ["chainTimeoutMs"] = 6_000,
        ["negativeResolveTtlSeconds"] = 10,
    };

    private static int Limit(string name) => Defaults[name];

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static Task<LabClient> Fleet() => Lab.ClientAsync(LabService.Fleet);

    /// <summary>Asserts a refusal with exactly <paramref name="code"/>, whose message names <paramref name="limit"/>.</summary>
    private static void ShouldRefuseAt(WireAnswer answer, string code, int limit, int status = 400)
    {
        answer.ShouldRefuse(code, status)["message"]!.GetValue<string>().Should().Contain(Text(limit), answer.ToString());
        answer.ErrorCodes.Should().Equal([code], answer.ToString());
    }

    private static string Repeat(int count, Func<int, string> stage) => string.Join(", ", Enumerable.Range(0, count).Select(stage));

    // ── U25 — where the numbers come from ─────────────────────────────────────────────────

    [Fact]
    public async Task U25_every_host_publishes_all_twenty_five_limits_on_health_at_their_defaults()
    {
        // The legacy half that compared them with the generated TypeScript modules is the client's.
        foreach (var service in LabService.All)
        {
            var health = await (await Lab.ClientAsync(service)).HealthAsync(shallow: true);
            var limits = health.Body!["limits"]!.AsObject();

            limits.Select(pair => pair.Key).Should().BeEquivalentTo(Defaults.Keys, service.Key);

            foreach (var (name, value) in Defaults)
                limits[name]!.GetValue<int>().Should().Be(value, $"{service.Key}.{name}");
        }
    }

    [Fact]
    public async Task U25b_a_host_publishes_the_limits_it_was_configured_with_not_the_defaults()
    {
        var variant = (await CorpusFleet.SharedAsync()).Variant(LabService.Transport, "b3-limits-published", new Dictionary<string, string?>
        {
            ["OxQL:Limits:MaxPageSize"] = "50",
            ["OxQL:Limits:MaxResolveKeys"] = "7",
        });

        var limits = (await variant.HealthAsync(shallow: true)).Body!["limits"]!.AsObject();

        limits["maxPageSize"]!.GetValue<int>().Should().Be(50);
        limits["maxResolveKeys"]!.GetValue<int>().Should().Be(7);
        limits["maxOffset"]!.GetValue<int>().Should().Be(Limit("maxOffset"));
    }

    // ── U1 · U2 · U11 — page size and offset ──────────────────────────────────────────────

    [Fact]
    public async Task U01_max_page_size_500_is_served_and_501_is_refused_with_the_number()
    {
        var client = await Transport();

        (await client.SendAsync(Corpus.Template, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": {{Limit("maxPageSize")}} } }]"""))
            .ShouldHaveIds(Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "id"), limit: Limit("maxPageSize")));
        ShouldRefuseAt(await client.SendAsync(Corpus.Template, $$"""[{ "page": { "limit": {{Limit("maxPageSize") + 1}} } }]"""), "PAGE_SIZE_EXCEEDED", Limit("maxPageSize"));
    }

    [Fact]
    public async Task U02_default_page_size_a_request_with_no_limit_is_served_exactly_100_rows()
    {
        var answer = await (await Transport()).SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": {} }]""");

        answer.ShouldHaveIds(Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "id"), limit: Limit("defaultPageSize")));
    }

    [Fact]
    public async Task U11_max_offset_5000_is_served_and_5001_is_refused_with_the_number()
    {
        var client = await Transport();

        (await client.SendAsync(Corpus.Template, $$"""[{ "project": { "id": 1 } }, { "page": { "limit": 1, "offset": {{Limit("maxOffset")}} } }]"""))
            .ShouldHaveIds(Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "id"), limit: 1, offset: Limit("maxOffset")));
        ShouldRefuseAt(await client.SendAsync(Corpus.Template, $$"""[{ "page": { "limit": 1, "offset": {{Limit("maxOffset") + 1}} } }]"""), "MAX_OFFSET_EXCEEDED", Limit("maxOffset"));
    }

    // ── U3 · U4 · U5 · U6 · U7 · U8 · U15 — stage counts ───────────────────────────────────

    [Fact]
    public async Task U03_max_pipeline_stages_20_stages_run_and_21_are_refused()
    {
        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "id", filter: row => Corpus.ValueAt(row, "isDeleted")!.AsBoolean == false), limit: 1);
        var client = await Transport();
        static string Filler(int _) => """{ "match": { "isDeleted": { "eq": false } } }""";

        (await client.SendAsync(Corpus.Template, $$"""[{{Repeat(Limit("maxPipelineStages") - 1, Filler)}}, { "page": { "limit": 1 } }]""")).ShouldHaveIds(expected);
        ShouldRefuseAt(await client.SendAsync(Corpus.Template, $$"""[{{Repeat(Limit("maxPipelineStages"), Filler)}}, { "page": { "limit": 1 } }]"""), "MAX_PIPELINE_STAGES_EXCEEDED", Limit("maxPipelineStages"));
    }

    [Fact]
    public async Task U05_max_unwind_stages_five_unwinds_run_and_six_are_refused()
    {
        string[] paths = ["items", "items.weightNotes", "documents", "tags", "billingLines", "billingLines.costCenters"];
        var client = await Transport();
        string Unwinds(int count) => Repeat(count, index => $$"""{ "unwind": { "path": "{{paths[index]}}" } }""");

        (await client.SendAsync(Corpus.Shipment, $$"""[{{Unwinds(Limit("maxUnwindStages"))}}, { "page": { "limit": 1 } }]""")).ShouldBeOk();
        ShouldRefuseAt(await client.SendAsync(Corpus.Shipment, $$"""[{{Unwinds(Limit("maxUnwindStages") + 1)}}, { "page": { "limit": 1 } }]"""), "MAX_UNWIND_STAGES_EXCEEDED", Limit("maxUnwindStages"));
    }

    [Fact]
    public async Task U07_max_group_fields_twenty_keys_and_aggregates_run_and_twenty_one_are_refused()
    {
        var client = await Transport();
        string Group(int fields) => $$"""{ "group": { "by": [{ "path": "templateName", "as": "k" }], "fields": { {{string.Join(", ", Enumerable.Range(0, fields - 1).Select(index => $$"""
            "f{{index}}": { "count": true }
            """))}} } } }""";

        var at = await client.SendAsync(Corpus.Template, $$"""[{{Group(Limit("maxGroupFields"))}}, { "sort": [{ "k": "asc" }] }, { "page": { "limit": 1 } }]""");
        at.ShouldBeOk();
        at.Items.Should().ContainSingle();
        at.Items[0]!.AsObject().Count.Should().Be(Limit("maxGroupFields"));

        ShouldRefuseAt(await client.SendAsync(Corpus.Template, $$"""[{{Group(Limit("maxGroupFields") + 1)}}, { "page": { "limit": 1 } }]"""), "MAX_GROUP_FIELDS_EXCEEDED", Limit("maxGroupFields"));
    }

    /// <summary>Real, projectable, distinct scalar paths of <c>transport.shipment</c>.</summary>
    private static readonly string[] ShipmentPaths =
    [
        "id", "shipmentNumber", "referenceNumber", "loadStart", "loadEnd", "deliveryStart", "deliveryEnd",
        "orderDate", "isTemplate", "isDeleted", "status.name", "status.number",
    ];

    [Fact]
    public async Task U08_max_projection_fields_the_limit_projects_and_one_more_is_refused()
    {
        // The lab shipment has fewer than 500 distinct projectable paths, so the limit is lowered
        // on a variant: the check is the same number compared the same way.
        const int Max = 10;
        var variant = (await CorpusFleet.SharedAsync()).Variant(LabService.Transport, "b3-max-projection-10", new Dictionary<string, string?> { ["OxQL:Limits:MaxProjectionFields"] = Text(Max) });
        string Projection(int count) => $$"""{ "project": { {{string.Join(", ", ShipmentPaths.Take(count).Select(path => $"\"{path}\": 1"))}} } }""";

        var at = await variant.SendAsync(Corpus.Shipment, $$"""[{{Projection(Max)}}, { "page": { "limit": 1 } }]""");
        at.ShouldHaveIds(Corpus.PageOf(Corpus.AllIds(Corpus.Shipment), limit: 1));

        ShouldRefuseAt(await variant.SendAsync(Corpus.Shipment, $$"""[{{Projection(Max + 1)}}, { "page": { "limit": 1 } }]"""), "MAX_PROJECTION_FIELDS_EXCEEDED", Max);
    }

    [Fact]
    public async Task U08b_max_projection_fields_at_the_default_501_fields_are_refused_with_the_number()
    {
        // 501 distinct fields: the count is refused before any of them is resolved.
        var fields = string.Join(", ", ShipmentPaths.Concat(Enumerable.Range(0, Limit("maxProjectionFields") + 1 - ShipmentPaths.Length).Select(index => $"status.name{index}")).Select(path => $"\"{path}\": 1"));

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, $$"""[{ "project": { {{fields}} } }, { "page": { "limit": 1 } }]""");

        answer.ShouldRefuse("MAX_PROJECTION_FIELDS_EXCEEDED", 400)["message"]!.GetValue<string>().Should().Contain(Text(Limit("maxProjectionFields")));
    }

    [Fact]
    public async Task U06_max_resolve_stages_eight_resolves_run_and_nine_are_refused()
    {
        var client = await Transport();
        static string Resolves(int count) => Repeat(count, index => $$"""{ "resolve": { "path": "createUserId", "as": "r{{index}}" } }""");
        var first = Corpus.Rows(Corpus.Template)[0];
        var vehicle = Corpus.Rows(Corpus.Vehicle).Single(row => row.Id == Corpus.GuidAt(first, "createUserId"));

        var at = await client.SendAsync(Corpus.Template, $$"""[{{Resolves(Limit("maxResolveStages"))}}, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 2 } }]""");
        at.ShouldHaveIds(Corpus.PageOf(Corpus.AllIds(Corpus.Template), limit: 2));
        at.Strings("r0.id")[0].Should().Be(vehicle.WireId);
        at.Strings("r1.id")[0].Should().Be(vehicle.WireId);

        ShouldRefuseAt(await client.SendAsync(Corpus.Template, $$"""[{{Resolves(Limit("maxResolveStages") + 1)}}, { "page": { "limit": 2 } }]"""), "MAX_RESOLVE_STAGES_EXCEEDED", Limit("maxResolveStages"));
    }

    [Fact]
    public async Task U04_max_lookup_stages_five_lookups_run_and_six_are_refused()
    {
        // The legacy battery had only the client's leg; the engine refuses the sixth itself.
        var client = await Fleet();
        static string Lookups(int count) => Repeat(count, index => $$"""{ "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "j{{index}}" } }""");
        var vehicle = Corpus.Rows(Corpus.Vehicle)[0];
        var children = Corpus.IdsWhere(Corpus.Equipment, row => Corpus.GuidAt(row, "vehicle.id") == vehicle.Id);
        children.Should().HaveCountGreaterThan(1);

        var at = await client.SendAsync(Corpus.Vehicle, $$"""[{{Lookups(Limit("maxLookupStages"))}}, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 1 } }]""");
        at.ShouldHaveIds([vehicle.Id]);

        for (var index = 0; index < Limit("maxLookupStages"); index++)
            ((JsonArray)at.Items[0]![$"j{index}"]!).Select(child => Guid.Parse(child!["id"]!.GetValue<string>())).Should().Equal(children, $"j{index}");

        ShouldRefuseAt(await client.SendAsync(Corpus.Vehicle, $$"""[{{Lookups(Limit("maxLookupStages") + 1)}}, { "page": { "limit": 1 } }]"""), "MAX_LOOKUP_STAGES_EXCEEDED", Limit("maxLookupStages"));
    }

    [Fact]
    public async Task U15_max_lookup_limit_100_children_per_parent_run_and_101_or_0_are_refused()
    {
        var client = await Fleet();
        string Lookup(int limit) => $$"""[{ "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "j", "limit": {{limit}} } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 1 } }]""";

        (await client.SendAsync(Corpus.Vehicle, Lookup(Limit("maxLookupLimit")))).ShouldHaveIds(Corpus.PageOf(Corpus.AllIds(Corpus.Vehicle), limit: 1));
        ShouldRefuseAt(await client.SendAsync(Corpus.Vehicle, Lookup(Limit("maxLookupLimit") + 1)), "LOOKUP_LIMIT_EXCEEDED", Limit("maxLookupLimit"));

        var zero = await client.SendAsync(Corpus.Vehicle, Lookup(0));
        zero.ShouldRefuse("LOOKUP_LIMIT_EXCEEDED", 400);
    }

    // ── U9 · U10 · U14 — the limits no client carries ─────────────────────────────────────

    private static string Conditions(int count) =>
        $$"""{ "or": [ {{Repeat(count, index => $$"""{ "templateName": { "eq": "x{{index}}" } }""")}} ] }""";

    [Fact]
    public async Task U09_max_conditions_two_hundred_leaves_run_and_two_hundred_and_one_are_refused()
    {
        var client = await Transport();

        (await client.SendAsync(Corpus.Template, $$"""[{ "match": {{Conditions(Limit("maxConditions"))}} }, { "page": { "limit": 1 } }]""")).ShouldHaveIds([]);
        ShouldRefuseAt(await client.SendAsync(Corpus.Template, $$"""[{ "match": {{Conditions(Limit("maxConditions") + 1)}} }, { "page": { "limit": 1 } }]"""), "MAX_CONDITIONS_EXCEEDED", Limit("maxConditions"));
    }

    [Fact]
    public async Task U10_max_variables_sixty_four_bind_and_sixty_five_are_refused()
    {
        var client = await Transport();
        static string Match(int count) => $$"""[{ "match": { "or": [ {{Repeat(count, index => $$"""{ "templateName": { "eq": { "$var": "v{{index}}" } } }""")}} ] } }, { "page": { "limit": 1 } }]""";
        static Dictionary<string, string> Variables(int count) => Enumerable.Range(0, count).ToDictionary(index => $"v{index}", _ => "T-DUP");

        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "id", filter: row => Corpus.Text(row, "templateName") == "T-DUP"), limit: 1);

        (await client.SendAsync(Corpus.Template, Match(Limit("maxVariables")), Variables(Limit("maxVariables")))).ShouldHaveIds(expected);
        ShouldRefuseAt(await client.SendAsync(Corpus.Template, Match(Limit("maxVariables") + 1), Variables(Limit("maxVariables") + 1)), "MAX_VARIABLES_EXCEEDED", Limit("maxVariables"));
    }

    [Fact]
    public async Task U14_max_request_bytes_a_body_under_the_limit_runs_and_one_over_is_refused_413_before_binding()
    {
        var client = await Transport();

        static string Body(int bytes)
        {
            var names = new JsonArray();
            var length = 0;

            while (length < bytes)
            {
                names.Add(new string('x', 60));
                length += 63;
            }

            return new JsonObject
            {
                ["entityType"] = Corpus.Template,
                ["pipeline"] = new JsonArray(
                    new JsonObject { ["match"] = new JsonObject { ["templateName"] = new JsonObject { ["in"] = names } } },
                    new JsonObject { ["page"] = new JsonObject { ["limit"] = 1 } }),
            }.ToJsonString();
        }

        var under = Body(240_000);
        System.Text.Encoding.UTF8.GetByteCount(under).Should().BeLessThan(Limit("maxRequestBytes"));
        (await client.PostAsync("OxQL/query", under)).ShouldHaveIds([]);

        var over = Body(270_000);
        System.Text.Encoding.UTF8.GetByteCount(over).Should().BeGreaterThan(Limit("maxRequestBytes"));
        ShouldRefuseAt(await client.PostAsync("OxQL/query", over), "REQUEST_TOO_LARGE", Limit("maxRequestBytes"), status: 413);
    }

    // ── U12 · U13 — regex and batch ───────────────────────────────────────────────────────

    [Fact]
    public async Task U12_regex_max_length_a_200_character_pattern_runs_and_201_is_refused()
    {
        var client = await Transport();
        static string Pattern(int length) => new('a', length);

        (await client.SendAsync(Corpus.Template, $$"""[{ "match": { "templateName": { "regex": "{{Pattern(Limit("regexMaxLength"))}}" } } }, { "page": { "limit": 1 } }]""")).ShouldHaveIds([]);
        ShouldRefuseAt(await client.SendAsync(Corpus.Template, $$"""[{ "match": { "templateName": { "regex": "{{Pattern(Limit("regexMaxLength") + 1)}}" } } }, { "page": { "limit": 1 } }]"""), "REGEX_TOO_LONG", Limit("regexMaxLength"));
    }

    [Fact]
    public async Task U13_max_batch_queries_ten_queries_run_in_one_round_trip_and_eleven_are_refused()
    {
        // The legacy battery's eleven were refused by the client; the engine refuses them too.
        var client = await Transport();
        var dup = Corpus.Where(Corpus.Template, row => Corpus.Text(row, "templateName") == "T-DUP").Count;
        static object[] Entries(int count) => Enumerable.Range(0, count)
            .Select(_ => (object)"""{ "entityType": "transport.shipment_template", "pipeline": [{ "match": { "templateName": { "eq": "T-DUP" } } }, { "page": { "limit": 1, "includeTotalCount": true } }] }""")
            .ToArray();

        var at = await client.BatchAsync(Entries(Limit("maxBatchQueries")));
        at.StatusCode.Should().Be(200, at.ToString());
        at.Results.Should().HaveCount(Limit("maxBatchQueries"));

        foreach (var entry in at.Results)
            entry.ShouldHaveTotal(dup);

        ShouldRefuseAt(await client.BatchAsync(Entries(Limit("maxBatchQueries") + 1)), "BATCH_TOO_LARGE", Limit("maxBatchQueries"));
    }

    // ── U17 — the count cap, against the bulk organisation ────────────────────────────────

    [Fact]
    public async Task U17_count_cap_exactly_100000_matches_count_exactly_and_100001_answer_the_cap_with_the_diagnostic()
    {
        var shared = await CorpusFleet.SharedAsync();
        await shared.SeedBulkAsync();
        var client = shared.Client(LabService.Transport, Org.C);

        var at = await client.SendAsync(Corpus.Template, $$"""[{ "match": { "templateName": { "lt": "{{BulkRows.NameOf(BulkRows.Count)}}" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");
        at.ShouldHaveTotal(Limit("countCap"), capped: false).ShouldHaveNoDiagnostics();

        var over = await client.SendAsync(Corpus.Template, """[{ "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");
        over.ShouldHaveTotal(Limit("countCap"), capped: true);
        over.DiagnosticCodes.Should().Equal("TOTAL_COUNT_CAPPED");
    }

    // ── the resolve limits, reached through variants ──────────────────────────────────────

    /// <summary>The ids of the vehicles whose matchCode is one of <paramref name="codes"/>.</summary>
    private static IReadOnlyList<Guid> VehiclesCoded(params string[] codes) =>
        Corpus.IdsWhere(Corpus.Vehicle, row => codes.Contains(Corpus.Text(row, "matchCode")));

    [Fact]
    public async Task U16_max_semi_join_ids_a_match_on_a_remote_alias_within_the_limit_runs_and_one_above_it_is_refused_422()
    {
        const int Max = 2;
        var shared = await CorpusFleet.SharedAsync();
        var variant = shared.Variant(LabService.Transport, "b3-semi-join-2", new Dictionary<string, string?> { ["OxQL:Limits:MaxSemiJoinIds"] = Text(Max) });

        var owners = VehiclesCoded("VEH-002", "VEH-003");
        owners.Should().HaveCount(Max);
        var expected = Corpus.IdsWhere(Corpus.Template, row => Corpus.GuidAt(row, "createUserId") is { } user && owners.Contains(user));
        expected.Should().NotBeEmpty();

        var at = await variant.SendAsync(Corpus.Template, """[{ "resolve": { "path": "createUserId", "as": "creator" } }, { "match": { "creator.matchCode": { "in": ["VEH-002", "VEH-003"] } } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 100 } }]""");
        at.ShouldHaveIds(expected);

        var tooMany = VehiclesCoded("VEH-002", "VEH-003", "VEH-004");
        tooMany.Should().HaveCount(Max + 1);
        var over = await variant.SendAsync(Corpus.Template, """[{ "resolve": { "path": "createUserId", "as": "creator" } }, { "match": { "creator.matchCode": { "in": ["VEH-002", "VEH-003", "VEH-004"] } } }, { "page": { "limit": 100 } }]""");
        over.ShouldRefuse("SEMI_JOIN_TOO_LARGE", 422);
        over.ErrorCodes.Should().Equal("SEMI_JOIN_TOO_LARGE");
    }

    [Fact]
    public async Task U18_max_resolve_keys_a_page_needing_more_keys_resolves_the_first_ones_and_says_so_with_resolve_partial()
    {
        // At the default (2 000, above MaxPageSize) the cap cannot be reached; two variants with a
        // cap of two, one per case, so neither answers from the other's resolve cache.
        const int Max = 2;
        var shared = await CorpusFleet.SharedAsync();
        var config = new Dictionary<string, string?> { ["OxQL:Limits:MaxResolveKeys"] = Text(Max) };
        var templates = Corpus.Rows(Corpus.Template).Take(5).ToList();
        var keys = templates.Select(row => Corpus.GuidAt(row, "createUserId")).Distinct().ToList();
        keys.Should().HaveCount(5, "the first five templates name five different users");
        string? CodeOf(Guid? key) => Corpus.Rows(Corpus.Vehicle).FirstOrDefault(row => row.Id == key) is { } hit ? Corpus.Text(hit, "matchCode") : null;

        var at = await shared.Variant(LabService.Transport, "b3-resolve-keys-2-at", config).SendAsync(Corpus.Template, """[{ "resolve": { "path": "createUserId", "as": "creator" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "creator": 1 } }, { "page": { "limit": 2 } }]""");
        at.ShouldHaveIds(Corpus.IdsOf(templates.Take(Max))).ShouldHaveNoDiagnostics();
        at.Strings("creator.matchCode").Should().Equal(templates.Take(Max).Select(row => CodeOf(Corpus.GuidAt(row, "createUserId"))));

        var over = await shared.Variant(LabService.Transport, "b3-resolve-keys-2-over", config).SendAsync(Corpus.Template, """[{ "resolve": { "path": "createUserId", "as": "creator" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "creator": 1 } }, { "page": { "limit": 5 } }]""");
        over.ShouldHaveIds(Corpus.IdsOf(templates));
        var partial = over.ShouldHaveDiagnostic("RESOLVE_PARTIAL");
        partial["params"]!["keys"]!.GetValue<int>().Should().Be(keys.Count);
        partial["params"]!["max"]!.GetValue<int>().Should().Be(Max);
        // The first keys in row order are resolved; the rest are not fetched.
        over.Strings("creator.matchCode").Should().Equal(templates.Select((row, index) => index < Max ? CodeOf(Corpus.GuidAt(row, "createUserId")) : null));
    }

    [Fact]
    public async Task U19_resolve_key_chunk_one_owner_query_carries_at_most_the_chunk_of_keys()
    {
        // Observable only at an owner that records what it is asked: the chaos owner of a private
        // fleet, behind the conformance widget reference. Three rows name three widgets.
        await using var fleet = await CorpusFleet.CreateAsync("b3-chunk", seed: [LabService.Conformance]);
        var codes = Corpus.Rows(Corpus.Conformance).Select(row => Corpus.Text(row, "widgetCodeExplicit")).Where(code => code is not null).Distinct().ToList();
        codes.Should().HaveCount(3);
        const string Query = """[{ "resolve": { "path": "widgetCodeExplicit", "as": "widget" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "widget": 1 } }, { "page": { "limit": 10 } }]""";

        var mark = fleet.Owner.Mark();
        var whole = await fleet.Client(LabService.Conformance).SendAsync(Corpus.Conformance, Query);
        whole.ShouldHaveIds(Corpus.AllIds(Corpus.Conformance));
        var wholeQueries = fleet.Owner.BatchesSince(mark).SelectMany(batch => Enumerable.Range(0, batch.Queries.Count).Select(index => batch.Condition(index))).ToList();
        wholeQueries.Should().ContainSingle("at the default chunk of 500 the three keys travel in one owner query");
        ((JsonArray)wholeQueries[0]!.Value.Operand!).Select(key => key!.GetValue<string>()).Should().BeEquivalentTo(codes);

        mark = fleet.Owner.Mark();
        var chunked = await fleet.Variant(LabService.Conformance, "chunk-1", new Dictionary<string, string?> { ["OxQL:Limits:ResolveKeyChunk"] = "1" }).SendAsync(Corpus.Conformance, Query);
        chunked.ShouldHaveIds(Corpus.AllIds(Corpus.Conformance));
        chunked.Strings("widget.code").Should().Equal(whole.Strings("widget.code"), "chunking changes how the keys travel, not what they resolve to");
        var chunkedQueries = fleet.Owner.BatchesSince(mark).SelectMany(batch => Enumerable.Range(0, batch.Queries.Count).Select(index => batch.Condition(index))).ToList();
        chunkedQueries.Should().HaveCount(codes.Count);
        chunkedQueries.Should().OnlyContain(condition => condition!.Value.Operator == "in" && ((JsonArray)condition.Value.Operand!).Count == 1);
        chunkedQueries.SelectMany(condition => ((JsonArray)condition!.Value.Operand!).Select(key => key!.GetValue<string>())).Should().BeEquivalentTo(codes);
    }
}
