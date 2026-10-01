using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// <c>POST /oxql/explain</c> (DESIGN §4.1, §4.2): on by default, never executing, a plain query or
/// the envelope, a binding failure answered 200 with <c>valid: false</c>, the index advisory opt-in
/// and static.
/// </summary>
public class HostHardeningExplainTests
{
    private const string Lookup = """{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }""";

    private const string WithLookup = $$"""{ "entityType": "probe.customer", "pipeline": [{{Lookup}}, { "page": { "limit": 5 } }] }""";

    private static async Task<(HttpStatusCode Status, JsonObject? Body)> ExplainAsync(SampleHost host, string body)
    {
        var response = await host.Client().PostAsync("/OxQL/explain", SampleHost.Json(body));

        return (response.StatusCode, (await SampleHost.Body(response)) as JsonObject);
    }

    private static List<string> Strings(JsonNode? array) => array!.AsArray().Select(node => node!.GetValue<string>()).ToList();

    [Fact]
    public async Task Explain_is_on_by_default_and_neither_runs_the_pipeline_nor_reads_the_indexes_unasked()
    {
        using var host = new SampleHost();

        var (status, body) = await ExplainAsync(host, WithLookup);

        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["valid"]!.GetValue<bool>().Should().BeTrue();
        body["contract"]!.GetValue<int>().Should().Be(2);
        Strings(body["engine"]!["capabilities"]).Should().Contain("explain").And.NotContain("resolve.chain", "the Sample host has no remote query client")
            .And.NotContain("oxql.2.1", "the contract is the marker, not a capability per feature");
        body["engine"]!["contract"]!.GetValue<int>().Should().Be(2, "contract 2 marks an engine with the whole language and this answer");
        body["engine"]!["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["etag"]!.GetValue<string>().Should().StartWith("W/\"x3:");
        body["cache"]!.ToJsonString().Should().Be("""{"maxAge":30,"dependsOn":[],"complete":true}""");
        body["errors"]!.AsArray().Should().BeEmpty();
        body["notes"]!.AsArray().Select(note => note!["code"]!.GetValue<string>()).Should().Equal([Notes.LookupLimit, Notes.JoinAfterPage],
            "the lookup's limit, and the lookup joins after the page since nothing later reads it");
        body["stages"]![0]!["placement"]!["executor"]!.GetValue<string>().Should().Be("inline");
        body["stages"]![0]!["creates"]!.ToJsonString().Should().Be("""["orders"]""");
        body["stages"]![0]!["shape"]!["roots"]!.ToJsonString().Should().Be("""{"":"t:probe.customer","orders":"t:probe.order"}""");
        body["aliases"]!["orders"]!["node"]!.GetValue<string>().Should().Be("array");
        body["types"]!.AsObject().Select(pair => pair.Key).Should().Equal("t:probe.customer", "t:probe.order");
        body["result"]!["columns"]!.AsArray().Should().Contain(column => column!["path"]!.GetValue<string>() == "orders" && column["root"]!.GetValue<string>() == "orders" && column["present"]!.GetValue<string>() == "always");
        body["catalog"]!.AsArray().Should().BeEmpty();
        body["owners"]!.AsArray().Should().BeEmpty();
        body.ContainsKey("plan").Should().BeFalse("the plan is opt-in");
        body.ContainsKey("advisory").Should().BeFalse("the advisory is opt-in");

        host.Runner.Calls.Should().BeEmpty("explain never executes: no page, no count");
        host.Indexes.IndexCalls.Should().Be(0, "without include the index lists are not read");
    }

    [Fact]
    public async Task A_host_that_switches_explain_off_answers_404()
    {
        using var host = new SampleHost(options => options.Explain.Enabled = false);

        (await ExplainAsync(host, WithLookup)).Status.Should().Be(HttpStatusCode.NotFound);
        host.Indexes.IndexCalls.Should().Be(0);
    }

    [Fact]
    public async Task With_include_indexes_the_advisory_reads_the_index_lists_of_the_entity_and_of_every_joined_collection_only()
    {
        using var host = new SampleHost();
        host.Indexes.ByCollection["orders"] =
        [
            new BsonDocument { ["name"] = "_id_", ["key"] = new BsonDocument("_id", 1) },
            new BsonDocument { ["name"] = "org_customer", ["key"] = new BsonDocument { ["OrganizationId"] = 1, ["CustomerId"] = 1 } },
        ];

        var (status, body) = await ExplainAsync(host, $$"""{ "query": {{WithLookup}}, "include": ["indexes"] }""");

        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        host.Indexes.Asked.Should().Equal(["probe.customer", "probe.order"]);
        host.Runner.Calls.Should().BeEmpty();

        var join = body!["advisory"]!.AsArray().Select(node => node!.AsObject()).Single(entry => entry["field"]!.GetValue<string>() == "lookup:orders");

        join["used"]!.GetValue<bool>().Should().BeTrue(join.ToJsonString());
        join["index"]!.GetValue<string>().Should().Be("org_customer");

        var advice = body["notes"]!.AsArray().Select(node => node!.AsObject()).Where(note => note["code"]!.GetValue<string>() == Notes.IndexAdvice).ToList();
        advice.Should().HaveCount(body["advisory"]!.AsArray().Count, "each line of the advisory is also a note");
        advice.Single(note => note["params"]!["field"]!.GetValue<string>() == "lookup:orders")["params"]!["index"]!.GetValue<string>().Should().Be("org_customer");
    }

    [Fact]
    public async Task A_query_that_does_not_bind_is_200_valid_false_with_every_error_and_the_shape_of_each_stage()
    {
        using var host = new SampleHost();
        var request = """
            { "entityType": "probe.order", "pipeline": [
              { "match": { "nothing": { "eq": 1 } } },
              { "unwind": { "path": "notACollection", "as": "line" } },
              { "match": { "line.amount": { "gt": 1 } } },
              { "sort": [{ "number": "asc" }] },
              { "page": { "limit": 5 } } ] }
            """;

        var (status, body) = await ExplainAsync(host, request);

        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["valid"]!.GetValue<bool>().Should().BeFalse();
        body.ContainsKey("plan").Should().BeFalse("a query that does not bind has no plan");

        var errors = body["errors"]!.AsArray().Select(node => node!.AsObject()).ToList();

        errors.Select(error => error["stage"]!.GetValue<int>()).Should().Equal([0, 1], "one error per failed stage; the stage under the failed alias adds none");
        errors[0]["code"]!.GetValue<string>().Should().Be(Codes.UnknownPath);

        errors[0]["params"]!["reason"]!.GetValue<string>().Should().Be("notAMember", "why a path did not resolve is said in a form a caller can act on");

        var stages = body["stages"]!.AsArray().Select(node => node!.AsObject()).ToList();

        stages.Select(stage => stage["kind"]!.GetValue<string>()).Should().Equal(["match", "unwind", "match", "sort", "page"]);
        stages.Select(stage => stage["status"]!.GetValue<string>()).Should().Equal(["error", "error", "skipped", "ok", "ok"]);
        stages.Should().OnlyContain(stage => !stage.ContainsKey("placement"));
        stages[4]["shape"]!["paging"]!.GetValue<string>().Should().Be("cursor");
        body["result"]!["paging"]!.GetValue<string>().Should().Be("cursor");
        host.Runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plain_query_and_its_envelope_get_the_same_answer_and_a_cursor_is_ignored()
    {
        using var host = new SampleHost();
        var plain = """{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "a" } } }, { "page": { "limit": 5, "cursor": "not-a-cursor" } }] }""";

        var (plainStatus, plainBody) = await ExplainAsync(host, plain);
        var (envelopeStatus, envelopeBody) = await ExplainAsync(host, $$"""{ "query": {{plain}}, "remote": "check", "include": ["shape", "notes"], "shape": { "depth": 2 } }""");

        plainStatus.Should().Be(HttpStatusCode.OK, plainBody?.ToJsonString());
        plainBody!["valid"]!.GetValue<bool>().Should().BeTrue("explain ignores page.cursor, so a cursor for another query is no CURSOR_INVALID");
        envelopeStatus.Should().Be(HttpStatusCode.OK);
        // The etag covers the request as written; everything else is the same answer.
        envelopeBody!.Remove("etag");
        plainBody.Remove("etag");
        envelopeBody.ToJsonString().Should().Be(plainBody.ToJsonString(), "a plain query is the envelope with its defaults");
    }

    [Theory]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "rows": true }""")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "include": ["executionStats"] }""")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "remote": "always" }""")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "describe": [] }""")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "remote": "skip" }""")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "catalog": {} }""")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "budget": { "ms": 100, "calls": 1 } }""")]
    [InlineData("""{ "catalog": [] }""")]
    [InlineData("""[ { "entityType": "probe.order", "pipeline": [] } ]""")]
    public async Task A_malformed_body_or_envelope_stays_a_400(string body)
    {
        using var host = new SampleHost();

        (await ExplainAsync(host, body)).Status.Should().Be(HttpStatusCode.BadRequest);
        host.Indexes.IndexCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_request_without_an_organisation_stays_a_403()
    {
        using var host = new SampleHost();

        // The Sample host's scope provider reads the organisation off a header; the empty id is none.
        var response = await host.Client(organisation: Guid.Empty).PostAsync("/OxQL/explain", SampleHost.Json(WithLookup));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
