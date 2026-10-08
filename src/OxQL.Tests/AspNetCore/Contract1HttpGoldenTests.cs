using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// What a contract 1 caller receives over HTTP is what it received from the engine before the work
/// on contract 2 answers began: the status, the set of headers with their values, and the body byte
/// for byte. <see cref="Compile.Contract1PipelineTests"/> holds the same for what is sent to the
/// database; this holds it for what is sent to the caller.
/// <para>
/// The recorded answers (<c>Fixtures/contract1/http.json</c>) were captured by this test from the
/// engine at the pushed base (commit <c>99e646f</c>), through the Sample host: a request without the
/// contract header that carries everything a newer client or a browser may add (<c>Accept-Encoding</c>,
/// <c>Origin</c>, <c>X-OxQL-Timing</c>, the top-level members <c>timing</c> and <c>maxTimeMs</c> in
/// kinds contract 2 would refuse) and a body large enough to be content-coded. They are not
/// re-recorded when contract 2 changes: a difference here is a contract 1 caller's answer changing.
/// <c>OXQL_RECORD_CONTRACT1=1</c> rewrites the file, which is right only on a commit whose contract 1
/// output is known to be the reference.
/// </para>
/// </summary>
public class Contract1HttpGoldenTests
{
    private const string RecordVariable = "OXQL_RECORD_CONTRACT1";

    private static readonly object Gate = new();

    private static bool Recording => Environment.GetEnvironmentVariable(RecordVariable) is "1" or "true";

    private static string FixturePath([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.Combine(Path.GetDirectoryName(file)!, "..", "Fixtures", "contract1", "http.json");

    private const string Page = """{ "match": { "Number": { "neq": "none" } } }, { "sort": [{ "Number": "asc" }] }, { "page": { "limit": 30, "includeTotalCount": true } }""";

    /// <summary>The requests: a name, the route, the body.</summary>
    public static readonly TheoryData<string, string, string> Requests = new()
    {
        { "query", "/OxQL/query", $$"""{ "entityType": "probe.order", "pipeline": [{{Page}}] }""" },
        // Members a contract 1 request never had: ignored whatever they hold, as every unknown member was.
        { "query-newer-members", "/OxQL/query", $$"""{ "entityType": "probe.order", "timing": true, "maxTimeMs": 1, "filter": [{ "where": { "number": { "eq": "none" } } }], "pipeline": [{{Page}}] }""" },
        { "query-newer-members-of-another-kind", "/OxQL/query", $$"""{ "entityType": "probe.order", "timing": "yes", "maxTimeMs": { "ms": "soon" }, "filter": 5, "pipeline": [{{Page}}] }""" },
        { "query-refused", "/OxQL/query", """{ "entityType": "probe.order", "timing": 1, "pipeline": [{ "match": { "nope": { "eq": 1 } } }] }""" },
        // A member contract 2 added to a stage is one contract 1 does not have: named among the unknown ones where it was written.
        { "query-refused-newer-stage-members", "/OxQL/query", """{ "entityType": "probe.order", "pipeline": [{ "page": { "count": true, "skip": 5, "upTo": 1 } }] }""" },
        { "query-refused-newer-stage-member-null", "/OxQL/query", """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 5, "count": null } }] }""" },
        { "batch", "/OxQL/batch", $$"""{ "maxTimeMs": 5000, "queries": [{ "entityType": "probe.order", "timing": true, "pipeline": [{{Page}}] }, { "entityType": "probe.order", "timing": [1], "maxTimeMs": "x", "pipeline": [{ "match": { "nope": { "eq": 1 } } }] }, { "entityType": "probe.order", "pipeline": [{ "page": { "limit": 2 } }] }] }""" },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task A_contract_1_caller_receives_the_answer_it_always_received(string name, string route, string body)
    {
        var sent = await AnswerAsync(route, body);

        if (Recording)
        {
            lock (Gate)
            {
                var path = FixturePath();
                var file = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : [];

                file[name] = sent.DeepClone();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, new JsonObject(file.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }

        var recorded = JsonNode.Parse(File.ReadAllText(FixturePath()))!.AsObject();

        recorded.ContainsKey(name).Should().BeTrue($"'{name}' is recorded; a new case is recorded on the reference commit with {RecordVariable}=1");

        var was = recorded[name]!.AsObject();

        sent["status"]!.GetValue<int>().Should().Be(was["status"]!.GetValue<int>(), $"the status of '{name}' under contract 1 is what it was");
        sent["headers"]!.ToJsonString().Should().Be(was["headers"]!.ToJsonString(), $"the headers of '{name}' under contract 1 are what they were: none added, none removed, none changed");
        sent["bytes"]!.GetValue<string>().Should().Be(was["bytes"]!.GetValue<string>(), $"the body of '{name}' under contract 1 is what it was, byte for byte; it read: {sent["body"]}");
    }

    /// <summary>The limits the published engine listed on health (master <c>f37fefe</c>, package 2.0.126.924), by name.</summary>
    private static readonly string[] PublishedLimits =
    [
        "maxPageSize", "defaultPageSize", "maxPipelineStages", "maxLookupStages", "maxUnwindStages", "maxResolveStages", "maxGroupFields",
        "maxProjectionFields", "maxConditions", "maxVariables", "maxOffset", "countCap", "maxSemiJoinIds", "resolveKeyChunk", "maxResolveKeys",
        "maxRequestBytes", "maxBatchQueries", "regexMaxLength", "maxLookupLimit",
    ];

    /// <summary>
    /// Health is read by name, so it may grow and never shrinks for a caller that knew it: a request
    /// without the contract header reads every member, every limit and every capability the published
    /// engine listed (and <c>unwind.keepPath</c>, which the pushed base added), in the order it listed
    /// them, before whatever the host lists now. A contract 2 caller reads the short list.
    /// </summary>
    [Fact]
    public async Task Health_lists_for_a_contract_1_caller_everything_the_engine_listed_before()
    {
        using var host = new SampleHost();

        var legacy = (await SampleHost.Body(await host.Client(contract: null).GetAsync("/OxQL/health")))!.AsObject();
        var current = (await SampleHost.Body(await host.Client(contract: 2).GetAsync("/OxQL/health")))!.AsObject();

        legacy.Select(member => member.Key).Should().Equal(["status", "service", "engine", "capabilities", "limits"], "and remote, on a host with a remote client");
        legacy["engine"]!.AsObject().Select(member => member.Key).Should().Equal("version", "contract");
        legacy["limits"]!.AsObject().Select(member => member.Key).Should().Contain(PublishedLimits);

        var listed = legacy["capabilities"]!.AsArray().Select(name => name!.GetValue<string>()).ToList();
        var now = current["capabilities"]!.AsArray().Select(name => name!.GetValue<string>()).ToList();

        listed.Take(5).Should().Equal("batch", "group.page", "page.offset", "any", "unwind.keepPath");
        listed.Skip(5).Should().Equal(now, "what depends on the host follows, as before");
        now.Should().NotContain(["batch", "group.page", "page.offset", "any", "unwind.keepPath"], "a contract 2 caller gates on the contract number");
        now.Should().Contain("compat.v1");

        // A host that answers no contract 1 has no contract 1 caller: the header's absence means nothing there.
        using var strict = new SampleHost(options => options.Compat.Enabled = false);

        (await SampleHost.Body(await strict.Client(contract: null).GetAsync("/OxQL/health")))!["capabilities"]!.AsArray().Select(name => name!.GetValue<string>())
            .Should().NotContain(["batch", "page.offset", "compat.v1"]);
    }

    /// <summary>
    /// The answer to a request without the contract header, sent as a newer client behind a browser
    /// would send it: the status, every header of the response and of its content by name, and the
    /// body, as its bytes (base64) and as text for a reader of the file.
    /// </summary>
    private static async Task<JsonObject> AnswerAsync(string route, string body)
    {
        using var host = new SampleHost();

        host.Runner.Count = 40;
        // More than a kilobyte of rows (a body a content coding would apply to), with text the JSON encoders differ on.
        host.Runner.PageRows = Enumerable.Range(1, 31).Select(index => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(new Guid(index, 0, 0, new byte[8]), GuidRepresentation.Standard),
            ["Number"] = $"n{index:00} Müller & Söhne <b>'x'</b> + 1",
            ["Count"] = index,
            ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
        }).ToList();

        using var client = host.Client(contract: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = SampleHost.Json(body) };

        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip, deflate");
        request.Headers.TryAddWithoutValidation("Origin", "https://grid.example");
        request.Headers.TryAddWithoutValidation("X-OxQL-Timing", "1");

        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var headers = new JsonObject();

        foreach (var header in response.Headers.Concat(response.Content.Headers).OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
            headers[header.Key] = string.Join(", ", header.Value);

        return new JsonObject
        {
            ["status"] = (int)response.StatusCode,
            ["headers"] = headers,
            ["bytes"] = Convert.ToBase64String(bytes),
            ["body"] = Encoding.UTF8.GetString(bytes),
        };
    }
}
