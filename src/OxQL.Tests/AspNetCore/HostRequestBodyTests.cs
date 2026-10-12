using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Binding;
using OxQL.AspNetCore.Legacy;
using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// A body the host cannot bind, through the real MVC pipeline. Under contract 2 the answer is the
/// refusal envelope: 400 <c>validation_error</c>, a code, and <c>params</c> that say where in the
/// body, what stands there and what the place takes, without a type name of the runtime; a batch
/// answers an entry that does not read in its place and runs the others. Without the contract
/// header the host answers as it always has, and the request is still counted as a contract 1 one.
/// </summary>
public class HostRequestBodyTests
{
    private const string Query = "/OxQL/query";
    private const string Batch = "/OxQL/batch";
    private const string Explain = "/OxQL/explain";

    private const string Good = """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }] }""";

    private static string Stage(string stage) => $$"""{ "entityType": "probe.order", "pipeline": [{{stage}}] }""";

    private static async Task<(HttpResponseMessage Response, string Text, JsonNode? Body)> PostAsync(SampleHost host, string route, string body, int? contract = 2, string media = "application/json")
    {
        var response = await host.Client(contract).PostAsync(route, new StringContent(body, Encoding.UTF8, media));
        var text = await response.Content.ReadAsStringAsync();

        return (response, text, text.Length > 0 && text.TrimStart()[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    /// <summary>The one error of a 400 refusal envelope, after the checks every such answer passes.</summary>
    private static JsonNode Refused(HttpResponseMessage response, string text, JsonNode? body)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json", text);
        body!["type"]!.GetValue<string>().Should().Be("validation_error", text);
        body["errors"].Should().BeOfType<JsonArray>(text);
        NamesNoRuntimeType(text);

        return body["errors"]!.AsArray().Should().ContainSingle(text).Subject!;
    }

    private static void NamesNoRuntimeType(string text)
    {
        foreach (var leaked in new[] { "System.", "OxQL.Core", "OxQL.AspNetCore", "Nullable", "IReadOnlyList", "JsonException", "LineNumber", "BytePositionInLine", "Models.", "Stage'", "Exception" })
            text.Should().NotContain(leaked);
    }

    public static TheoryData<string, string, string, string?, string?, int?> Kinds => new()
    {
        // body, code, path, expected, actual, stage
        { "[]", Codes.InvalidRequestBody, "$", "object", "array", null },
        { "5", Codes.InvalidRequestBody, "$", "object", "number", null },
        { "\"query\"", Codes.InvalidRequestBody, "$", "object", "string", null },
        { """{ "entityType": 5, "pipeline": [] }""", Codes.InvalidRequestBody, "$.entityType", "string", "number", null },
        { """{ "entityType": null, "pipeline": [] }""", Codes.InvalidRequestBody, "$.entityType", "string", "null", null },
        { """{ "entityType": "probe.order", "pipeline": null }""", Codes.InvalidRequestBody, "$.pipeline", "array", "null", null },
        { """{ "entityType": "probe.order", "pipeline": {} }""", Codes.InvalidRequestBody, "$.pipeline", "array", "object", null },
        { """{ "entityType": "probe.order", "pipeline": [], "strict": "true" }""", Codes.InvalidRequestBody, "$.strict", "boolean", "string", null },
        { """{ "entityType": "probe.order", "pipeline": [], "variables": [] }""", Codes.InvalidRequestBody, "$.variables", "object", "array", null },
        { Stage("\"page\""), Codes.InvalidRequestBody, "$.pipeline[0]", "object", "string", 0 },
        { Stage("7"), Codes.InvalidRequestBody, "$.pipeline[0]", "object", "number", 0 },
        { Stage("""{ "match": [{ "number": "a" }] }"""), Codes.InvalidRequestBody, "$.pipeline[0].match", "object", "array", 0 },
        { Stage("""{ "match": { "and": { "number": "a" } } }"""), Codes.InvalidRequestBody, "$.pipeline[0].match.and", "array", "object", 0 },
        { Stage("""{ "match": { "or": [{ "number": "a" }, 5] } }"""), Codes.InvalidRequestBody, "$.pipeline[0].match.or[1]", "object", "number", 0 },
        { Stage("""{ "match": { "not": [] } }"""), Codes.InvalidRequestBody, "$.pipeline[0].match.not", "object", "array", 0 },
        { Stage("""{ "match": { "lines": { "any": { "and": 5 } } } }"""), Codes.InvalidRequestBody, "$.pipeline[0].match.lines.any.and", "array", "number", 0 },
        { Stage("""{ "match": { "customer.name": { "any": { "or": "x" } } } }"""), Codes.InvalidRequestBody, "$.pipeline[0].match['customer.name'].any.or", "array", "string", 0 },
        { Stage("""{ "sort": { "number": "asc" } }"""), Codes.InvalidRequestBody, "$.pipeline[0].sort", "array", "object", 0 },
        { Stage("""{ "page": { "limit": 1 } }, { "sort": "number" }"""), Codes.InvalidRequestBody, "$.pipeline[1].sort", "array", "string", 1 },
        { Stage("""{ "group": { "by": { "path": "number", "as": "n" }, "fields": {} } }"""), Codes.InvalidRequestBody, "$.pipeline[0].group.by", "array", "object", 0 },
        { Stage("""{ "group": { "by": [], "fields": [] } }"""), Codes.InvalidRequestBody, "$.pipeline[0].group.fields", "object", "array", 0 },
        { Stage("""{ "group": { "by": [], "fields": { "n": 5 } } }"""), Codes.InvalidRequestBody, "$.pipeline[0].group.fields.n", "object", "number", 0 },
        { Stage("""{ "group": { "by": [{ "path": 5, "as": "n" }], "fields": {} } }"""), Codes.InvalidRequestBody, "$.pipeline[0].group.by[0].path", "string", "number", 0 },
        { Stage("""{ "group": { "by": [{ "dateTrunc": "month", "as": "m" }], "fields": {} } }"""), Codes.InvalidRequestBody, "$.pipeline[0].group.by[0].dateTrunc", "object", "string", 0 },
        { Stage("""{ "group": { "by": [], "fields": { "n": { "sum": { "path": 5 } } } } }"""), Codes.InvalidRequestBody, "$.pipeline[0].group.fields.n.sum.path", "string", "number", 0 },
        { Stage("""{ "project": [ "number" ] }"""), Codes.InvalidRequestBody, "$.pipeline[0].project", "object", "array", 0 },
        { Stage("""{ "project": { "number": 1.5 } }"""), Codes.InvalidRequestBody, "$.pipeline[0].project.number", "integer", "number", 0 },
        { Stage("""{ "project": { "customer": { "name": 3000000000 } } }"""), Codes.InvalidRequestBody, "$.pipeline[0].project.customer.name", "integer", "number", 0 },
        { Stage("""{ "unwind": { "path": 5 } }"""), Codes.InvalidRequestBody, "$.pipeline[0].unwind.path", "string", "number", 0 },
        { Stage("""{ "lookup": "orders" }"""), Codes.InvalidRequestBody, "$.pipeline[0].lookup", "object", "string", 0 },
        { Stage("""{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "filter": [] } }"""), Codes.InvalidRequestBody, "$.pipeline[0].lookup.filter", "object", "array", 0 },
        { Stage("""{ "resolve": { "path": "customerId", "as": "customer", "filter": { "and": 1 } } }"""), Codes.InvalidRequestBody, "$.pipeline[0].resolve.filter.and", "array", "number", 0 },
        { Stage("""{ "page": { "limit": 1.5 } }"""), Codes.InvalidPageLimit, "$.pipeline[0].page.limit", "integer", "number", 0 },
        { Stage("""{ "page": { "limit": 3000000000 } }"""), Codes.InvalidPageLimit, "$.pipeline[0].page.limit", "integer", "number", 0 },
        { Stage("""{ "page": { "limit": 3e9 } }"""), Codes.InvalidPageLimit, "$.pipeline[0].page.limit", "integer", "number", 0 },
        { Stage("""{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "limit": 1.5 } }"""), Codes.LookupLimitExceeded, "$.pipeline[0].lookup.limit", "integer", "number", 0 },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_value_of_another_JSON_kind_than_its_place_takes_is_refused_with_the_place_and_both_kinds(string body, string code, string path, string? expected, string? actual, int? stage)
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Query, body);
        var error = Refused(response, text, answer);

        error["code"]!.GetValue<string>().Should().Be(code, text);
        error["params"]!["reason"]!.GetValue<string>().Should().Be("kind", text);
        error["params"]!["path"]!.GetValue<string>().Should().Be(path, text);
        error["params"]!["expected"]?.GetValue<string>().Should().Be(expected, text);
        error["params"]!["actual"]?.GetValue<string>().Should().Be(actual, text);
        error["stage"]?.GetValue<int>().Should().Be(stage, text);
        error["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("""{ "pipeline": [] }""", "$.entityType", null)]
    [InlineData("""{ "entityType": "probe.order" }""", "$.pipeline", null)]
    [InlineData("""{ "entityType": "probe.order", "pipeline": [{ "group": { "by": [{ "dateTrunc": { "path": "createdAt" }, "as": "m" }], "fields": {} } }] }""", "$.pipeline[0].group.by[0].dateTrunc.unit", 0)]
    public async Task A_required_member_that_is_not_written_is_refused_as_missing_with_its_place(string body, string path, int? stage)
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Query, body);
        var error = Refused(response, text, answer);

        error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
        error["params"]!["reason"]!.GetValue<string>().Should().Be("missing");
        error["params"]!["path"]!.GetValue<string>().Should().Be(path);
        error["stage"]?.GetValue<int>().Should().Be(stage);
    }

    [Fact]
    public async Task Both_required_members_missing_are_two_errors_of_one_refusal()
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Query, "{}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        answer!["errors"]!.AsArray().Select(error => error!["params"]!["path"]!.GetValue<string>()).Should().Equal("$.entityType", "$.pipeline");
        NamesNoRuntimeType(text);
    }

    [Theory]
    [InlineData("""{ "entityType": "probe.order", "pipeline": [], }""", 1, 48)]
    [InlineData("{ \"entityType\": \"probe.order\",\n  \"pipeline\": [\n    { \"page\": { \"limit\": } }\n  ] }", 3, 26)]
    [InlineData("{ \"entityType\": ", 1, 17)]
    [InlineData("not json", 1, 2)]
    public async Task A_body_that_is_no_JSON_is_refused_with_the_line_and_the_position_from_1(string body, int line, int position)
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Query, body);
        var error = Refused(response, text, answer);

        error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
        error["params"]!["reason"]!.GetValue<string>().Should().Be("json");
        error["params"]!["line"]!.GetValue<int>().Should().Be(line, text);
        error["params"]!["position"]!.GetValue<int>().Should().Be(position, text);
        error["params"]!.AsObject().ContainsKey("path").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public async Task An_empty_body_is_refused_as_empty(string body)
    {
        using var host = new SampleHost();

        foreach (var route in new[] { Query, Batch, Explain })
        {
            var (response, text, answer) = await PostAsync(host, route, body);
            var error = Refused(response, text, answer);

            error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody, route);
            error["params"]!["reason"]!.GetValue<string>().Should().Be("empty", route);
        }
    }

    [Fact]
    public async Task The_literal_null_is_no_request_on_any_route()
    {
        using var host = new SampleHost();

        foreach (var route in new[] { Query, Batch, Explain })
        {
            var (response, text, answer) = await PostAsync(host, route, "null");
            var error = Refused(response, text, answer);

            error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody, route + text);
            error["params"]!["path"]!.GetValue<string>().Should().Be("$", route + text);
        }
    }

    [Fact]
    public async Task A_body_nested_deeper_than_the_host_reads_says_the_depth()
    {
        using var host = new SampleHost();
        var condition = """{ "number": { "eq": "x" } }""";

        for (var level = 0; level < 200; level++)
            condition = $$"""{ "and": [{{condition}}] }""";

        var (response, text, answer) = await PostAsync(host, Query, Stage($$"""{ "match": {{condition}} }"""));
        var error = Refused(response, text, answer);

        error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
        error["params"]!["reason"]!.GetValue<string>().Should().Be("depth");
        error["params"]!["max"]!.GetValue<int>().Should().Be(OxQL.Core.Models.OxQLJson.MaxDepth);
    }

    // ---- batch --------------------------------------------------------------------------------

    [Fact]
    public async Task A_batch_answers_an_entry_that_does_not_read_in_its_place_and_runs_the_others()
    {
        using var host = new SampleHost();
        var bad = Stage("""{ "sort": { "number": "asc" } }""");
        var fraction = Stage("""{ "page": { "limit": 1.5 } }""");

        var (response, text, answer) = await PostAsync(host, Batch, $$"""{ "queries": [{{Good}}, {{bad}}, null, {{Good}}, {{fraction}}, { "pipeline": [] }] }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        NamesNoRuntimeType(text);

        var results = answer!["results"]!.AsArray();

        results.Should().HaveCount(6);
        results[0]!["items"].Should().NotBeNull(text);
        results[3]!["items"].Should().NotBeNull(text);

        var sort = results[1]!["errors"]!.AsArray().Should().ContainSingle().Subject!;

        results[1]!["type"]!.GetValue<string>().Should().Be("validation_error");
        sort["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
        sort["stage"]!.GetValue<int>().Should().Be(0);
        sort["params"]!["path"]!.GetValue<string>().Should().Be("$.pipeline[0].sort", "a path in an entry starts at the entry");
        sort["params"]!["entry"]!.GetValue<int>().Should().Be(1);
        sort["params"]!["expected"]!.GetValue<string>().Should().Be("array");
        sort["params"]!["actual"]!.GetValue<string>().Should().Be("object");

        var none = results[2]!["errors"]!.AsArray().Should().ContainSingle().Subject!;

        none["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
        none["params"]!["reason"]!.GetValue<string>().Should().Be("kind");
        none["params"]!["actual"]!.GetValue<string>().Should().Be("null");
        none["params"]!["entry"]!.GetValue<int>().Should().Be(2);

        results[4]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.InvalidPageLimit);
        results[4]!["errors"]![0]!["params"]!["entry"]!.GetValue<int>().Should().Be(4);
        results[5]!["errors"]![0]!["params"]!["reason"]!.GetValue<string>().Should().Be("missing");
        results[5]!["errors"]![0]!["params"]!["path"]!.GetValue<string>().Should().Be("$.entityType");

        host.Runner.Calls.Should().HaveCount(2, "the two entries that read ran, the others ran nothing");
    }

    [Theory]
    [InlineData("""{ "queries": {} }""", "kind", "$.queries")]
    [InlineData("""{ "queries": null }""", "kind", "$.queries")]
    [InlineData("{}", "missing", "$.queries")]
    [InlineData("""{ "maxTimeMs": 100 }""", "missing", "$.queries")]
    [InlineData("""{ "queries": [], "maxTimeMs": "soon" }""", "kind", "$.maxTimeMs")]
    [InlineData("""{ "queries": [], "maxTimeMs": 1.5 }""", "kind", "$.maxTimeMs")]
    [InlineData("[]", "kind", "$")]
    public async Task A_batch_that_does_not_read_itself_is_refused_as_a_whole(string body, string reason, string path)
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Batch, body);
        var error = Refused(response, text, answer);

        error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody, text);
        error["params"]!["reason"]!.GetValue<string>().Should().Be(reason, text);
        error["params"]!["path"]!.GetValue<string>().Should().Be(path, text);
    }

    // ---- explain ------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "include": "shape" }""", "INVALID_REQUEST_BODY", "$.include")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "include": ["shape", 5] }""", "INVALID_REQUEST_BODY", "$.include[1]")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "remote": 1 }""", "INVALID_REQUEST_BODY", "$.remote")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "catalog": {} }""", "INVALID_REQUEST_BODY", "$.catalog")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "shape": { "depth": 1.5 } }""", "INVALID_REQUEST_BODY", "$.shape.depth")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "shape": { "depth": 0 } }""", "INVALID_REQUEST_BODY", "$.shape.depth")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [{ "sort": {} }] } }""", "INVALID_REQUEST_BODY", "$.query.pipeline[0].sort")]
    [InlineData("""{ "query": "probe.order" }""", "INVALID_REQUEST_BODY", "$.query")]
    [InlineData("""{ "include": ["shape"] }""", "INVALID_REQUEST_BODY", "$.query")]
    [InlineData("""{ "entityType": "probe.order", "pipeline": [{ "sort": {} }] }""", "INVALID_REQUEST_BODY", "$.pipeline[0].sort")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "verbose": true }""", "UNKNOWN_REQUEST_MEMBER", "$.verbose")]
    [InlineData("""{ "query": { "entityType": "probe.order", "pipeline": [] }, "shape": { "width": 1 } }""", "UNKNOWN_REQUEST_MEMBER", "$.shape.width")]
    public async Task An_explain_body_that_does_not_read_is_refused_with_the_place(string body, string code, string path)
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Explain, body);
        var error = Refused(response, text, answer);

        error["code"]!.GetValue<string>().Should().Be(code, text);
        error["params"]!["path"]!.GetValue<string>().Should().Be(path, text);
    }

    [Fact]
    public async Task An_unknown_member_of_an_explain_envelope_names_the_member()
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Explain, """{ "query": { "entityType": "probe.order", "pipeline": [] }, "verbose": true }""");
        var error = Refused(response, text, answer);

        error["params"]!["member"]!.GetValue<string>().Should().Be("verbose");
        error["params"]!["reason"]!.GetValue<string>().Should().Be("unknown");
    }

    // ---- what the filter leaves alone -------------------------------------------------------------

    [Fact]
    public async Task A_request_that_binds_is_answered_as_before_and_its_body_is_read_once_by_the_host()
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Query, Good);

        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        answer!["items"].Should().NotBeNull();

        var (batch, batchText, batchAnswer) = await PostAsync(host, Batch, $$"""{ "queries": [{{Good}}, {{Good}}] }""");

        batch.StatusCode.Should().Be(HttpStatusCode.OK, batchText);
        batchAnswer!["results"]!.AsArray().Should().HaveCount(2).And.OnlyContain(result => result!["items"] != null);
    }

    [Fact]
    public async Task A_body_of_another_media_type_stays_the_frameworks_415()
    {
        using var host = new SampleHost();

        var (response, text, _) = await PostAsync(host, Query, Good, media: "text/plain");

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType, text);
    }

    [Fact]
    public async Task A_header_that_names_no_contract_is_said_before_the_body()
    {
        using var host = new SampleHost();

        var (response, text, answer) = await PostAsync(host, Query, "not json", contract: 3);
        var error = Refused(response, text, answer);

        error["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
        error["params"]!["reason"]!.GetValue<string>().Should().Be("contract");
    }

    [Fact]
    public async Task A_body_in_another_charset_is_read_in_that_charset()
    {
        using var host = new SampleHost();
        var client = host.Client();
        var content = new ByteArrayContent(Encoding.Unicode.GetBytes(Stage("""{ "sort": { "number": "asc" } }""")));

        content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-16");

        var response = await client.PostAsync(Query, content);
        var text = await response.Content.ReadAsStringAsync();
        var error = Refused(response, text, JsonNode.Parse(text));

        error["params"]!["path"]!.GetValue<string>().Should().Be("$.pipeline[0].sort");
    }

    // ---- contract 1: the host's answer, as it always was ------------------------------------------

    public static TheoryData<string, string, string[]> Unbound => new()
    {
        // route, body, the keys of the host's errors object
        { Query, """{ "pipeline": [] }""", ["$", "request"] },
        { Query, """{ "entityType": "probe.order" }""", ["$", "request"] },
        { Query, """{ "entityType": "probe.order", "pipeline": {} }""", ["$.pipeline", "request"] },
        { Query, """{ "entityType": "probe.order", "pipeline": null }""", ["Pipeline"] },
        { Query, Stage("""{ "sort": { "number": "asc" } }"""), ["$", "request"] },
        { Query, Stage("""{ "page": { "limit": 1.5 } }"""), ["$", "request"] },
        { Query, "not json", ["$", "request"] },
        { Query, "", ["", "request"] },
        { Batch, """{ "queries": {} }""", ["$.queries", "batch"] },
        { Batch, "{}", ["$", "batch"] },
    };

    [Theory]
    [MemberData(nameof(Unbound))]
    public async Task Without_the_contract_header_a_body_that_does_not_bind_is_the_hosts_answer(string route, string body, string[] keys)
    {
        using var host = new SampleHost();

        foreach (var contract in new int?[] { null, 1 })
        {
            var (response, text, answer) = await PostAsync(host, route, body, contract);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json", text);
            answer!.AsObject().Select(member => member.Key).Should().Equal(["type", "title", "status", "errors", "traceId"], text);
            answer["title"]!.GetValue<string>().Should().Be("One or more validation errors occurred.");
            answer["status"]!.GetValue<int>().Should().Be(400);
            answer["errors"].Should().BeOfType<JsonObject>("the host's errors are an object by place, not the envelope's list");
            answer["errors"]!.AsObject().Select(member => member.Key).Should().BeEquivalentTo(keys, text);
            text.Should().NotContain("INVALID_REQUEST_BODY").And.NotContain("validation_error");
        }
    }

    [Fact]
    public async Task Without_the_contract_header_the_hosts_answer_to_a_body_that_is_no_JSON_is_word_for_word_the_frameworks()
    {
        using var host = new SampleHost();

        var (_, _, answer) = await PostAsync(host, Query, "not json", contract: null);

        answer!["type"]!.GetValue<string>().Should().Be("https://tools.ietf.org/html/rfc9110#section-15.5.1");
        answer["errors"]!["request"]!.AsArray().Select(message => message!.GetValue<string>()).Should().Equal("The request field is required.");
        answer["errors"]!["$"]!.AsArray().Select(message => message!.GetValue<string>()).Should().Equal("'not json' is an invalid JSON literal. Expected the literal 'null'. Path: $ | LineNumber: 0 | BytePositionInLine: 1.");
    }

    [Fact]
    public async Task A_contract_1_request_that_does_not_bind_is_counted_as_a_contract_1_request()
    {
        using var host = new SampleHost();

        await PostAsync(host, Query, """{ "entityType": "probe.order", "pipeline": {} }""", contract: null);
        await PostAsync(host, Batch, """{ "queries": {} }""", contract: null);

        var lines = host.Logs.Of(LegacyRoute.LogCategory).ToList();

        lines.Should().HaveCount(2);
        lines[0].Should().Contain("reason=unbound").And.Contain("route=query").And.Contain("org=" + Bind.BindHost.Organisation);
        lines[1].Should().Contain("reason=unbound").And.Contain("route=batch");

        // A contract 2 request that does not bind is none.
        await PostAsync(host, Query, """{ "entityType": "probe.order", "pipeline": {} }""");

        host.Logs.Of(LegacyRoute.LogCategory).Should().HaveCount(2);
    }

    // ---- a host with its own answer to an invalid model state --------------------------------------

    /// <summary>A host's filter at the default order that answers every invalid model state itself, as a base package does.</summary>
    private sealed class HostValidationFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (!context.ModelState.IsValid)
                context.Result = new ObjectResult(new { answeredBy = "host" }) { StatusCode = 400 };
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }

    private static SampleHost HostWithItsOwnValidation() => new(configure: services =>
    {
        services.Configure<ApiBehaviorOptions>(behaviour => behaviour.SuppressModelStateInvalidFilter = true);
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<HostValidationFilter>());
    });

    [Fact]
    public async Task In_a_host_that_answers_an_invalid_model_state_itself_contract_2_still_gets_the_envelope_and_contract_1_the_hosts_answer()
    {
        using var host = HostWithItsOwnValidation();
        var body = Stage("""{ "sort": { "number": "asc" } }""");

        var (response, text, answer) = await PostAsync(host, Query, body);

        Refused(response, text, answer)["params"]!["path"]!.GetValue<string>().Should().Be("$.pipeline[0].sort");

        var (legacy, legacyText, legacyAnswer) = await PostAsync(host, Query, body, contract: null);

        legacy.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        legacyAnswer!["answeredBy"]!.GetValue<string>().Should().Be("host", legacyText);

        // A batch with one entry that does not read runs past the host's filter: the model state is settled before it.
        var (batch, batchText, batchAnswer) = await PostAsync(host, Batch, $$"""{ "queries": [{{Good}}, {{body}}] }""");

        batch.StatusCode.Should().Be(HttpStatusCode.OK, batchText);
        batchAnswer!["results"]![0]!["items"].Should().NotBeNull();
        batchAnswer["results"]![1]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
    }

    [Fact]
    public void The_filter_stands_between_the_frameworks_415_and_its_automatic_400()
    {
        new OxQLRequestBodyAttribute().Order.Should().Be(-2500).And.BeGreaterThan(-3000).And.BeLessThan(-2000);
        OxQLRequestBodyAttribute.PathOf("Pipeline[0].Group.By[1].DateTrunc.Unit", "request").Should().Be("$.pipeline[0].group.by[1].dateTrunc.unit");
        OxQLRequestBodyAttribute.PathOf("request", "request").Should().Be("$");
        OxQLRequestBodyAttribute.PathOf("", "request").Should().Be("$");
        OxQLRequestBodyAttribute.PathOf("$.pipeline[0]", "request").Should().Be("$.pipeline[0]");
    }
}
