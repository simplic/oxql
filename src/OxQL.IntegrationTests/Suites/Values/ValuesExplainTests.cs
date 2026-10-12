using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Values;

/// <summary>
/// Explain of a request that ends in <c>values</c>, on the fleet's hosts: what the
/// stage answers and in which order, what it reads, where the request's filter runs, the aggregate a
/// run sends, and every refusal with its <c>basis</c>. What explain says is what a run does: the
/// order it names is the order of the rows a run answers.
/// </summary>
[Trait("Category", "Integration")]
public class ValuesExplainTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static JsonObject Query(string values, string before = "", string filter = "[]") =>
        JsonNode.Parse($$"""{ "entityType": "{{Corpus.Shipment}}", "filter": {{filter}}, "pipeline": [ {{before}}{{(before.Length > 0 ? "," : "")}} { "values": { {{values}} } } ] }""")!.AsObject();

    private static async Task<WireAnswer> Explain(JsonObject query, string include = """["shape", "notes", "plan"]""") =>
        await (await Transport()).ExplainHereAsync(JsonNode.Parse($$"""{ "query": {{query.ToJsonString()}}, "include": {{include}} }""")!);

    [Fact]
    public async Task It_says_what_the_stage_answers_reads_and_sends_and_a_run_answers_in_the_order_it_names()
    {
        var query = Query(""" "path": "items.status.name", "counts": true, "search": "o" """, filter: """[{ "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } }]""");
        var answer = await Explain(query);
        var body = answer.Body!;

        answer.StatusCode.Should().Be(200);
        body["valid"]!.GetValue<bool>().Should().BeTrue(answer.Text);
        body["executable"]!.GetValue<string>().Should().Be("yes");

        var stage = body["stages"]!.AsArray().Single()!;

        stage["kind"]!.GetValue<string>().Should().Be("values");
        stage["creates"]!.ToJsonString().Should().Be("""["value","count"]""");
        stage["reads"]!.ToJsonString().Should().Be("""[{"path":"items.status.name","use":"groupKey"}]""");
        stage["shape"]!["roots"]!.ToJsonString().Should().Be("""{"value":"k:string","count":"k:int"}""");
        stage["shape"]!["identity"]!.ToJsonString().Should().Be("""{"kind":"bucket","bucket":{"keys":["value"]}}""");
        body["filter"]!.ToJsonString().Should().Be("""[{"id":"removeDeleted","status":"ok","placed":[{"before":0,"paths":["isDeleted"]}]}]""");

        var result = body["result"]!;

        result["identity"]!.ToJsonString().Should().Be("""{"kind":"bucket","bucket":{"keys":["value"]}}""");
        result["order"]!.ToJsonString().Should().Be("""[{"path":"count","direction":"desc"},{"path":"value","direction":"asc","tieBreak":true}]""");
        result["columns"]!.AsArray().Select(column => column!["path"]!.GetValue<string>()).Should().Equal("value", "count");

        var notes = body["notes"]!.AsArray().Select(note => note!.AsObject()).ToList();
        var scans = notes.Single(note => note["code"]!.GetValue<string>() == "VALUES_SCANS");

        scans["basis"]!.GetValue<string>().Should().Be("advisory");
        scans["stage"]!.GetValue<int>().Should().Be(0);
        scans["params"]!.ToJsonString().Should().Be("""{"scan":50000,"limit":100,"counts":true,"unwinds":1}""");
        notes.Single(note => note["code"]!.GetValue<string>() == "TEXT_FOLDS")["params"]!["op"]!.GetValue<string>().Should().Be("values");
        notes.Should().OnlyContain(note => note.ContainsKey("basis"));

        var plan = body["plan"]!["stages"]!.AsArray().Select(emitted => emitted!.AsObject().First().Key).ToList();

        plan.Should().Equal("$match", "$match", "$limit", "$unwind", "$group", "$facet");
        body["plan"]!.AsObject().ContainsKey("count").Should().BeFalse("nothing is counted beside the values");
        body["plan"]!["bound"]!.AsObject().ContainsKey("page").Should().BeFalse();

        // The run: the rows in the order explain names, most first and then by value.
        var run = (await (await Transport()).QueryAsync(Query(""" "path": "items.status.name", "counts": true """))).ShouldBeOk();
        var counts = run.Items.Select(item => item!["count"]!.GetValue<int>()).ToList();

        counts.Should().BeInDescendingOrder();
        run.Items.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task A_refusal_carries_its_basis_and_the_request_can_never_run_as_written()
    {
        foreach (var (values, code, basis) in new[]
        {
            (""" "path": "status.name", "limit": 501 """, "VALUES_LIMIT_EXCEEDED", "limits"),
            (""" "path": "status.name", "scan": 50001 """, "VALUES_LIMIT_EXCEEDED", "limits"),
            (""" "path": "status.name", "limit": 0 """, "INVALID_PAGE_LIMIT", "request"),
            (""" "path": "status" """, "NOT_FILTERABLE", "model"),
            (""" "path": "nope" """, "UNKNOWN_PATH", "model"),
        })
        {
            var body = (await Explain(Query(values), """["notes"]""")).Body!;
            var error = body["errors"]!.AsArray().Single()!;

            body["valid"]!.GetValue<bool>().Should().BeFalse();
            body["executable"]!.GetValue<string>().Should().Be("never", values);
            error["code"]!.GetValue<string>().Should().Be(code);
            error["basis"]!.GetValue<string>().Should().Be(basis, values);
            error["stage"]!.GetValue<int>().Should().Be(0);
        }

        // Another service's member: its owner's rows come after the page, and there is no page.
        var remote = (await Explain(Query(""" "path": "dep.name" """, before: """{ "resolve": { "path": "department.id", "as": "dep" } }"""), """["notes"]""")).Body!;

        remote["executable"]!.GetValue<string>().Should().Be("never");
        remote["errors"]!.AsArray().Single()!["code"]!.GetValue<string>().Should().Be("NOT_CONTINUABLE");
        remote["errors"]!.AsArray().Single()!["basis"]!.GetValue<string>().Should().Be("model");
    }

    [Fact]
    public async Task A_column_says_whether_its_values_can_be_asked_for()
    {
        var body = (await (await Transport()).ExplainHereAsync(JsonNode.Parse($$"""
            { "query": { "entityType": "{{Corpus.Shipment}}", "pipeline": [
                { "resolve": { "path": "department.id", "as": "dep" } },
                { "project": { "id": 1, "status.name": 1, "loadingTimeType": 1, "dep.name": 1 } } ] } }
            """)!)).Body!;
        var columns = body["result"]!["columns"]!.AsArray().ToDictionary(column => column!["path"]!.GetValue<string>(), column => body["result"]!["capabilities"]![column!["can"]!.GetValue<string>()]!.AsObject());

        columns["status.name"]["can"]!["values"]!.GetValue<bool>().Should().BeTrue();
        columns["loadingTimeType"]["can"]!["values"]!.GetValue<bool>().Should().BeTrue();
        columns["dep.name"]["can"]!["values"]!.GetValue<bool>().Should().BeFalse();
        columns["dep.name"]["why"]!["values"]!.GetValue<string>().Should().Be("owner");

        // What it says is what a request does.
        (await (await Transport()).QueryAsync(Query(""" "path": "loadingTimeType" """))).ShouldBeOk();
        (await (await Transport()).QueryAsync(Query(""" "path": "dep.name" """, before: """{ "resolve": { "path": "department.id", "as": "dep" } }"""))).ShouldRefuse("NOT_CONTINUABLE", 400);
    }
}
