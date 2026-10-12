using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// The top-level <c>filter</c> on a real server: a list of <c>{ id?, where }</c> the engine
/// places, each condition at the earliest point of the pipeline where the paths it reads are in the
/// row. The same conditions written as <c>match</c> stages at those points answer the same rows;
/// explain says where each entry ran, and what it emits is what runs.
/// </summary>
[Trait("Category", "Integration")]
public class FilterPlacementTests
{
    private const string Page = """{ "page": { "limit": 500, "count": true } }""";

    private static JsonObject Request(string entity, string filter, string pipeline) =>
        JsonNode.Parse($$"""{ "entityType": "{{entity}}", "filter": {{filter}}, "pipeline": {{pipeline}} }""")!.AsObject();

    /// <summary>A query written both ways: the conditions as filter entries, and as match stages where the engine places them.</summary>
    public static TheoryData<string, string, string, string, string, string> Cases() => new()
    {
        {
            "an entity member, before a sort", LabService.Transport.Key, Corpus.Shipment,
            """[{ "id": "planned", "where": { "status.name": { "eq": "Planned" } } }]""",
            $$"""[{ "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "status.name": 1 } }, {{Page}}]""",
            $$"""[{ "match": { "status.name": { "eq": "Planned" } } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "status.name": 1 } }, {{Page}}]"""
        },
        {
            "an element's member after its unwind, the entity's before it", LabService.Transport.Key, Corpus.Shipment,
            """[{ "id": "col:item", "where": { "item.status.name": { "eq": "Open" }, "item.quantity.value": { "gt": 0 } } }, { "id": "preset", "where": { "status.name": { "neq": "Closed" } } }]""",
            $$"""[{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "sort": [{ "id": "asc" }, { "position": "asc" }] }, { "project": { "id": 1, "position": 1, "item.orderNumber": 1 } }, {{Page}}]""",
            $$"""[{ "match": { "status.name": { "neq": "Closed" } } }, { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "match": { "item.status.name": { "eq": "Open" }, "item.quantity.value": { "gt": 0 } } }, { "sort": [{ "id": "asc" }, { "position": "asc" }] }, { "project": { "id": 1, "position": 1, "item.orderNumber": 1 } }, {{Page}}]"""
        },
        {
            "the collection's path means some element, before the unwind", LabService.Transport.Key, Corpus.Shipment,
            """[{ "where": { "items.status.name": { "eq": "Open" } } }, { "where": { "items": { "any": { "quantity.value": { "gt": 0 }, "status.name": { "eq": "Open" } } } } }]""",
            $$"""[{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "sort": [{ "id": "asc" }, { "position": "asc" }] }, { "project": { "id": 1, "position": 1 } }, {{Page}}]""",
            $$"""[{ "match": { "items.status.name": { "eq": "Open" } } }, { "match": { "items": { "any": { "quantity.value": { "gt": 0 }, "status.name": { "eq": "Open" } } } } }, { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }, { "sort": [{ "id": "asc" }, { "position": "asc" }] }, { "project": { "id": 1, "position": 1 } }, {{Page}}]"""
        },
        {
            "around a group: the rows that feed it, and the groups it yields", LabService.Transport.Key, Corpus.Shipment,
            """[{ "id": "rows", "where": { "isDeleted": { "neq": true } } }, { "id": "having", "where": { "total": { "gt": 1 } } }]""",
            $$"""[{ "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "status": "asc" }] }, {{Page}}]""",
            $$"""[{ "match": { "isDeleted": { "neq": true } } }, { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "total": { "count": true } } } }, { "match": { "total": { "gt": 1 } } }, { "sort": [{ "status": "asc" }] }, {{Page}}]"""
        },
        {
            "a lookup's alias after the lookup", LabService.Fleet.Key, Corpus.Vehicle,
            """[{ "id": "has", "where": { "eq.name": { "exists": true } } }]""",
            $$"""[{ "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }, {{Page}}]""",
            $$"""[{ "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq" } }, { "match": { "eq.name": { "exists": true } } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }, {{Page}}]"""
        },
        {
            "another service's alias after its resolve, as a semi-join", LabService.Transport.Key, Corpus.Shipment,
            """[{ "id": "remote", "where": { "dep.name": { "eq": "Workshop" } } }]""",
            $$"""[{ "resolve": { "path": "department.id", "as": "dep" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }, {{Page}}]""",
            $$"""[{ "resolve": { "path": "department.id", "as": "dep" } }, { "match": { "dep.name": { "eq": "Workshop" } } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }, {{Page}}]"""
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_filter_answers_the_rows_its_conditions_answer_as_match_stages_where_the_engine_places_them(string what, string service, string entity, string filter, string pipeline, string asMatches)
    {
        var client = await Lab.ClientAsync(LabService.Of(service));

        var placed = await client.QueryAsync(Request(entity, filter, pipeline));
        var written = await client.SendAsync(entity, asMatches);

        written.ShouldBeOk(what);
        placed.ShouldBeOk(what);
        placed.Items.Select(row => row!.ToJsonString()).Should().Equal(written.Items.Select(row => row!.ToJsonString()), what);
        placed.TotalCount.Should().Be(written.TotalCount, what);
        placed.Items.Should().NotBeEmpty($"{what}: a case that matches nothing proves nothing");

        // A cursor is bound to which rows there are, however the conditions were written.
        if (written.NextCursor is { } cursor)
            (await client.QueryAsync(Request(entity, filter, pipeline.Replace(Page, $$"""{ "page": { "limit": 500, "cursor": "{{cursor}}" } }""", StringComparison.Ordinal)))).ShouldBeOk(what);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Explain_says_where_each_entry_runs_and_emits_what_the_match_stages_emit(string what, string service, string entity, string filter, string pipeline, string asMatches)
    {
        var client = await Lab.ClientAsync(LabService.Of(service));

        var placed = await client.ExplainHereAsync(new JsonObject { ["query"] = Request(entity, filter, pipeline), ["include"] = new JsonArray("plan", "notes") });
        var written = await client.ExplainHereAsync(new JsonObject { ["query"] = Json.Request(entity, asMatches), ["include"] = new JsonArray("plan", "notes") });

        placed.Body!["valid"]!.GetValue<bool>().Should().BeTrue($"{what}: {placed}");
        written.Body!["valid"]!.GetValue<bool>().Should().BeTrue($"{what}: {written}");

        // Explain ≡ execution: what the filter emits is what its conditions emit as match stages at the points explain names.
        placed.Body["plan"]!["stages"]!.ToJsonString().Should().Be(written.Body["plan"]!["stages"]!.ToJsonString(), what);
        placed.Body["plan"]!["count"]!.ToJsonString().Should().Be(written.Body["plan"]!["count"]!.ToJsonString(), what);

        var entries = placed.Body["filter"]!.AsArray();
        var requested = JsonNode.Parse(filter)!.AsArray();

        entries.Should().HaveCount(requested.Count, what);
        written.Body.AsObject().ContainsKey("filter").Should().BeFalse("a request without a filter answers none");

        for (var index = 0; index < entries.Count; index++)
        {
            entries[index]!["id"]?.GetValue<string>().Should().Be(requested[index]!["id"]?.GetValue<string>(), what);
            entries[index]!["status"]!.GetValue<string>().Should().Be("ok", what);
            entries[index]!["placed"]!.AsArray().Should().NotBeEmpty(what);
        }

        // Each placement names a boundary of the caller's pipeline: the match stage written there in the other form.
        var stages = JsonNode.Parse(asMatches)!.AsArray();
        var callers = JsonNode.Parse(pipeline)!.AsArray();

        foreach (var placement in entries.SelectMany(entry => entry!["placed"]!.AsArray()))
        {
            var boundary = placement!["before"] is not null ? 0 : placement["after"]!.GetValue<int>() + 1;

            boundary.Should().BeInRange(0, callers.Count - 1, what);

            // The stages written before that boundary in the match form, then a match that reads the placement's paths.
            var at = boundary == 0 ? 0 : IndexOfCallerStage(stages, callers, boundary - 1) + 1;
            var matches = stages.Skip(at).TakeWhile(stage => stage!["match"] is not null).Select(stage => stage!.ToJsonString()).ToList();

            foreach (var path in placement["paths"]!.AsArray())
                matches.Should().Contain(match => match.Contains($"\"{path!.GetValue<string>()}\"", StringComparison.Ordinal), $"{what}: '{path}' is matched right at the boundary explain names");
        }
    }

    /// <summary>Where caller stage <paramref name="index"/> of the filter form stands in the match form.</summary>
    private static int IndexOfCallerStage(JsonArray matchForm, JsonArray callers, int index)
    {
        var wanted = callers[index]!.ToJsonString();
        var seen = callers.Take(index).Count(stage => stage!.ToJsonString() == wanted);

        for (var at = 0; at < matchForm.Count; at++)
            if (matchForm[at]!.ToJsonString() == wanted && seen-- == 0)
                return at;

        throw new InvalidOperationException($"The match form does not carry caller stage {index}.");
    }

    [Fact]
    public async Task Explain_names_the_boundary_the_paths_and_having_for_each_entry()
    {
        var (_, _, entity, filter, pipeline, _) = ("", "", Corpus.Shipment,
            """[{ "id": "rows", "where": { "isDeleted": { "neq": true }, "item.quantity.value": { "gt": 0 } } }, { "where": { "total": { "gt": 1 } } }, { "id": "empty", "where": {} }]""",
            """[{ "unwind": { "path": "items", "as": "item" } }, { "group": { "by": [{ "path": "item.status.name", "as": "status" }], "fields": { "total": { "count": true } } } }]""", "");

        var answer = await (await Lab.ClientAsync(LabService.Transport)).ExplainHereAsync(new JsonObject { ["query"] = Request(entity, filter, pipeline) });

        answer.Body!["valid"]!.GetValue<bool>().Should().BeTrue(answer.ToString());
        answer.Body["filter"]!.ToJsonString().Should().Be(
            """[{"id":"rows","status":"ok","placed":[{"before":0,"paths":["isDeleted"]},{"after":0,"paths":["item.quantity.value"]}]},{"status":"ok","placed":[{"after":1,"paths":["total"],"having":true}]},{"id":"empty","status":"ok","placed":[]}]""");
        answer.Body["stages"]!.AsArray().Should().HaveCount(2, "the filter adds no stage to the caller's pipeline");
    }

    [Fact]
    public async Task A_condition_that_cannot_be_placed_is_FILTER_NOT_PLACEABLE_and_names_its_entry_and_the_births_of_its_paths()
    {
        var client = await Lab.ClientAsync(LabService.Transport);
        var request = Request(Corpus.Shipment,
            """[{ "id": "ok", "where": { "isDeleted": { "neq": true } } }, { "id": "split", "where": { "or": [{ "items.orderNumber": { "eq": 1 } }, { "item.orderNumber": { "eq": 2 } }] } }]""",
            """[{ "unwind": { "path": "items", "as": "item" } }, { "page": { "limit": 5 } }]""");

        var refused = await client.QueryAsync(request);
        var error = refused.ShouldRefuse("FILTER_NOT_PLACEABLE", 400);

        refused.ErrorCodes.Should().Equal("FILTER_NOT_PLACEABLE");
        error["filter"]!.GetValue<string>().Should().Be("split");
        error.ContainsKey("stage").Should().BeFalse("an error of a filter entry belongs to no stage");
        error["params"]!.ToJsonString().Should().Be("""{"paths":["items.orderNumber","item.orderNumber"],"births":[{"path":"items.orderNumber","before":0},{"path":"item.orderNumber","after":0}]}""");

        // Explain answers the same error, and says which entry it is.
        var explained = await client.ExplainHereAsync(new JsonObject { ["query"] = request });

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse();
        explained.Body["errors"]!.AsArray().Select(each => each!["code"]!.GetValue<string>()).Should().Equal("FILTER_NOT_PLACEABLE");
        explained.Body["filter"]!.AsArray().Select(entry => $"{entry!["id"]}:{entry["status"]}").Should().Equal("ok:ok", "split:error");
    }

    [Fact]
    public async Task A_malformed_filter_is_INVALID_FILTER_and_an_error_in_a_condition_names_its_entry_by_id_or_index()
    {
        var client = await Lab.ClientAsync(LabService.Transport);

        var malformed = await client.QueryAsync(Request(Corpus.Shipment, """[{ "id": "a", "where": {} }, { "id": "a", "where": {} }, { "when": {} }]""", "[]"));

        malformed.ShouldRefuse("INVALID_FILTER", 400);
        malformed.Body!["errors"]!.AsArray().Select(error => $"{error!["code"]}:{error["filter"]}:{error["params"]!["reason"]}").Should().Equal(
            "INVALID_FILTER:1:duplicateId", "INVALID_FILTER:2:unknownMember", "INVALID_FILTER:2:whereRequired");
        (await client.QueryAsync(Request(Corpus.Shipment, """{ "isDeleted": { "eq": false } }""", "[]"))).ShouldRefuse("INVALID_FILTER", 400)["params"]!.ToJsonString().Should().Be("""{"reason":"notAnArray"}""");

        var unknown = await client.QueryAsync(Request(Corpus.Shipment, """[{ "where": { "isDeleted": { "eq": false } } }, { "where": { "nope": { "eq": 1 } } }, { "id": "col:x", "where": { "status.name": { "gt": { "x": 1 } } } }]""", "[]"));

        unknown.StatusCode.Should().Be(400);
        unknown.Body!["errors"]!.AsArray().Select(error => $"{error!["code"]}:{error["filter"]}:{error["stage"]}:{error["path"]}").Should().BeEquivalentTo(
            "UNKNOWN_PATH:1::nope", "INVALID_OPERAND:col:x::status.name");

        // Contract 1 never had the member: it is ignored, whatever it holds.
        var legacy = await client.Contract(1).QueryAsync(Request(Corpus.Shipment, """{ "nonsense": true }""", """[{ "page": { "limit": 1 } }]"""));

        legacy.ShouldBeOk();
    }

    [Fact]
    public async Task A_filter_over_its_bounds_is_refused_once_before_anything_is_placed()
    {
        var client = await Lab.ClientAsync(LabService.Transport);
        var limit = (await client.HealthAsync()).Body!["limits"]!["maxFilterEntries"]!.GetValue<int>();

        static string Entries(int count, string where = """{ "isDeleted": { "neq": true } }""") =>
            "[" + string.Join(",", Enumerable.Range(0, count).Select(index => $$"""{ "id": "e{{index}}", "where": {{where}} }""")) + "]";

        limit.Should().Be(64);
        (await client.QueryAsync(Request(Corpus.Shipment, Entries(limit), """[{ "page": { "limit": 1 } }]"""))).ShouldBeOk();

        // One more entry: one error, whatever the entries hold (these name a path that does not exist).
        var over = await client.QueryAsync(Request(Corpus.Shipment, Entries(limit + 1, """{ "nope": { "eq": 1 } }"""), """[{ "page": { "limit": 1 } }]"""));

        over.ShouldRefuse("MAX_FILTER_ENTRIES_EXCEEDED", 400)["params"]!.ToJsonString().Should().Be($$"""{"limit":"maxFilterEntries","max":{{limit}},"count":{{limit + 1}}}""");
        over.ErrorCodes.Should().Equal(["MAX_FILTER_ENTRIES_EXCEEDED"], over.ToString());

        // More conditions than a request may carry, in one entry: one error, none of them bound.
        var conditions = "{ \"and\": [" + string.Join(",", Enumerable.Repeat("""{ "nope": { "eq": 1 } }""", 2_000)) + "] }";
        var many = await client.QueryAsync(Request(Corpus.Shipment, $$"""[{ "id": "many", "where": {{conditions}} }]""", """[{ "page": { "limit": 1 } }]"""));

        many.ShouldRefuse("MAX_CONDITIONS_EXCEEDED", 400)["filter"]!.GetValue<string>().Should().Be("many");
        many.ErrorCodes.Should().Equal(["MAX_CONDITIONS_EXCEEDED"], many.ToString());

        // Explain says the same of the request, with the basis of a limit, and says it can never run.
        var explained = await client.ExplainAsync(Request(Corpus.Shipment, Entries(limit + 1), "[]"));
        var error = explained.Body!["errors"]!.AsArray().Should().ContainSingle().Subject!;

        explained.Body["valid"]!.GetValue<bool>().Should().BeFalse();
        error["code"]!.GetValue<string>().Should().Be("MAX_FILTER_ENTRIES_EXCEEDED");
        error["basis"]!.GetValue<string>().Should().Be("limits");
    }

    [Fact]
    public async Task A_run_says_in_its_diagnostics_that_a_filter_condition_ran_before_the_group_that_gives_its_name_to_an_aggregate()
    {
        var client = await Lab.ClientAsync(LabService.Transport);
        const string Level = """{ "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "loadStart": { "max": "loadStart" }, "n": { "count": true } } } }""";
        const string Column = """[{ "id": "col:loadStart", "where": { "loadStart": { "gte": "2026-01-01T00:00:00Z" } } }]""";

        var run = await client.QueryAsync(Request(Corpus.Shipment, Column, $$"""[{{Level}}, { "sort": [{ "status": "asc" }] }, { "page": { "limit": 50 } }]"""));
        var said = run.ShouldBeOk().Diagnostics.Should().ContainSingle(each => each["code"]!.GetValue<string>() == "FILTER_BINDS_EARLIER").Subject;

        said["stage"]!.GetValue<int>().Should().Be(0);
        said["path"]!.GetValue<string>().Should().Be("loadStart");
        said["filter"]!.GetValue<string>().Should().Be("col:loadStart");
        said["params"]!.ToJsonString().Should().Be("""{"path":"loadStart","alias":"loadStart"}""");

        // What it says is what ran: the rows loaded from that day on feed the groups, as a match before the group does.
        var written = await client.QueryAsync(Request(Corpus.Shipment, "[]", $$"""[{ "match": { "loadStart": { "gte": "2026-01-01T00:00:00Z" } } }, {{Level}}, { "sort": [{ "status": "asc" }] }, { "page": { "limit": 50 } }]"""));

        run.Items.Select(row => row!.ToJsonString()).Should().Equal(written.ShouldBeOk().Items.Select(row => row!.ToJsonString()));
        written.DiagnosticCodes.Should().NotContain("FILTER_BINDS_EARLIER");

        // Explain's note of the same request carries the same text.
        var explained = await client.ExplainAsync(Request(Corpus.Shipment, Column, $$"""[{{Level}}]"""));

        explained.Body!["notes"]!.AsArray().Should().ContainSingle(note => note!["code"]!.GetValue<string>() == "FILTER_BINDS_EARLIER")
            .Which!["message"]!.GetValue<string>().Should().Be(said["message"]!.GetValue<string>());
    }
}
