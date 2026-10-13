using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Contract;

/// <summary>
/// A body whose member name or text is no Unicode text: half of a surrogate pair written as an
/// escape (lexically valid JSON), or bytes that are no UTF-8. Wherever it stands, the answer is the
/// caller's mistake said once: 400 <c>INVALID_REQUEST_BODY</c> with <c>reason: "encoding"</c> and
/// the place, in the refusal envelope under contract 2 (an entry of a batch in its place), the
/// host's 400 for a caller without the contract header; never a fault.
/// </summary>
[Trait("Category", "Integration")]
public class RequestTextTests
{
    /// <summary>Half a surrogate pair, as the escape a body writes it with.</summary>
    private const string Half = "\\ud800";

    private const string Entity = Corpus.Employee;

    /// <summary>The host the cases run against; a derived class puts a buffering middleware ahead of MVC.</summary>
    protected virtual Task<LabClient> Staff(int? contract = 2) => Lab.ClientAsync(LabService.Staff, contract: contract);

    public static TheoryData<string, string, string, string> Places() => new()
    {
        // what, body, path, in
        { "a path in match", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "match{{Half}}Code": { "eq": "x" } } }] }""", "$.pipeline[0].match", "name" },
        { "a path in project", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "project": { "a{{Half}}": 1 } }] }""", "$.pipeline[0].project", "name" },
        { "a top-level member", $$"""{ "entityType": "{{Entity}}", "x{{Half}}": 1, "pipeline": [] }""", "$", "name" },
        { "an operand", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "matchCode": { "eq": "a{{Half}}" } } }] }""", "$.pipeline[0].match.matchCode.eq", "value" },
        { "an operand of a set", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "matchCode": { "in": ["a", "{{Half}}"] } } }] }""", "$.pipeline[0].match.matchCode.in[1]", "value" },
        { "a filter id", $$"""{ "entityType": "{{Entity}}", "filter": [{ "id": "{{Half}}", "where": { "matchCode": "x" } }], "pipeline": [] }""", "$.filter[0].id", "value" },
        { "the entity", $$"""{ "entityType": "staff.{{Half}}", "pipeline": [] }""", "$.entityType", "value" },
        { "an alias", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "unwind": { "path": "functions", "as": "f{{Half}}" } }] }""", "$.pipeline[0].unwind.as", "value" },
        { "a variable name", $$"""{ "entityType": "{{Entity}}", "variables": { "v{{Half}}": 1 }, "pipeline": [] }""", "$.variables", "name" },
        { "a variable value", $$"""{ "entityType": "{{Entity}}", "variables": { "v": "{{Half}}" }, "pipeline": [{ "match": { "matchCode": { "eq": { "$var": "v" } } } }] }""", "$.variables.v", "value" },
        { "a search", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "values": { "path": "matchCode", "search": "{{Half}}" } }] }""", "$.pipeline[0].values.search", "value" },
        { "the second half alone", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "matchCode": { "contains": "\udc00" } } }] }""", "$.pipeline[0].match.matchCode.contains", "value" },
    };

    private static void ShouldSayEncoding(WireAnswer answer, string path, string @in, string because)
    {
        answer.StatusCode.Should().Be(400, $"{because}: {answer}");
        answer.Type.Should().Be("validation_error", because);
        answer.ErrorCodes.Should().Equal(["INVALID_REQUEST_BODY"], because);
        answer.Errors[0]["params"]!.ToJsonString().Should().Be($$"""{"reason":"encoding","path":"{{path}}","in":"{{@in}}"}""", because);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public async Task Half_a_surrogate_pair_is_the_callers_mistake_wherever_it_stands(string what, string body, string path, string @in)
    {
        var staff = await Staff();

        ShouldSayEncoding(await staff.PostAsync("OxQL/query", body), path, @in, what);
        ShouldSayEncoding(await staff.PostAsync("OxQL/explain", body), path, @in, what + " (explain)");

        // A body sent without a declared length is answered the same.
        ShouldSayEncoding(await SendAsync(staff, "OxQL/query", Encoding.UTF8.GetBytes(body), declared: false, contract: 2), path, @in, what + " (no declared length)");
    }

    [Theory]
    [MemberData(nameof(Places))]
    public async Task A_caller_without_the_contract_header_reads_the_hosts_400(string what, string body, string path, string @in)
    {
        _ = (path, @in);

        foreach (var declared in new[] { true, false })
        {
            var answer = await SendAsync(await Staff(contract: null), "OxQL/query", Encoding.UTF8.GetBytes(body), declared, contract: null);

            // What the legacy pipeline reads past (a member a contract 1 request does not have) it still reads past.
            answer.StatusCode.Should().BeOneOf([200, 400], $"{what}: {answer}");

            if (answer.StatusCode == 400 && answer.Errors.Count == 0)
                answer.IsProblemDetails.Should().BeTrue($"{what}: {answer}");
        }
    }

    [Fact]
    public async Task A_whole_pair_is_text()
    {
        var answer = await (await Staff()).PostAsync("OxQL/query", $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "matchCode": { "contains": "😀" } } }, { "page": { "limit": 1 } }] }""");

        answer.ShouldBeOk();
    }

    [Fact]
    public async Task An_entry_of_a_batch_is_answered_in_its_place_and_the_others_run()
    {
        var staff = await Staff();
        var good = $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 1 } }] }""";
        var broken = $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "a{{Half}}": 1 } }, { "match": { "matchCode": "{{Half}}" } }] }""";

        var answer = await staff.PostAsync("OxQL/batch", $$"""{ "queries": [{{good}}, {{broken}}, {{good}}, null] }""");

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Results.Should().HaveCount(4);
        answer.Results[0].ShouldBeOk();
        answer.Results[2].ShouldBeOk();
        answer.Results[1].ErrorCodes.Should().Equal("INVALID_REQUEST_BODY", "INVALID_REQUEST_BODY");
        answer.Results[1].Errors.Select(error => error["params"]!.ToJsonString()).Should().Equal(
            """{"reason":"encoding","path":"$.pipeline[0].match","in":"name","entry":1}""",
            """{"reason":"encoding","path":"$.pipeline[1].match.matchCode","in":"value","entry":1}""");
        answer.Results[3].Errors[0]["params"]!["reason"]!.GetValue<string>().Should().Be("kind", "an entry written as null is said as that");

        // A batch sent without a declared length is answered the same, entry by entry.
        var chunked = await SendAsync(staff, "OxQL/batch", Encoding.UTF8.GetBytes($$"""{ "queries": [{{good}}, {{broken}}, {{good}}] }"""), declared: false, contract: 2);

        chunked.StatusCode.Should().Be(200, chunked.ToString());
        chunked.Results[0].ShouldBeOk();
        chunked.Results[2].ShouldBeOk();
        chunked.Results[1].Errors.Select(error => error["params"]!.ToJsonString()).Should().Equal(
            """{"reason":"encoding","path":"$.pipeline[0].match","in":"name","entry":1}""",
            """{"reason":"encoding","path":"$.pipeline[1].match.matchCode","in":"value","entry":1}""");

        // A name of the batch itself is the batch's.
        var whole = await staff.PostAsync("OxQL/batch", $$"""{ "queries": [{{good}}], "m{{Half}}": 1 }""");

        ShouldSayEncoding(whole, "$", "name", "a member of the batch");
    }

    [Fact]
    public async Task Bytes_that_are_no_UTF_8_are_said_the_same()
    {
        var staff = await Staff();
        var body = Encoding.UTF8.GetBytes($$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "matchCode": { "eq": "a?b" } } }] }""");

        body[Array.IndexOf(body, (byte)'?')] = 0xFF;

        foreach (var declared in new[] { true, false })
        {
            var answer = await SendAsync(staff, "OxQL/query", body, declared, contract: 2);

            answer.StatusCode.Should().Be(400, answer.ToString());
            answer.ErrorCodes.Should().Equal("INVALID_REQUEST_BODY");
            answer.Errors[0]["params"]!["reason"]!.GetValue<string>().Should().Be("encoding", answer.ToString());
        }
    }

    /// <summary>A raw POST of bytes, with a declared length or as a stream of unknown length.</summary>
    private static async Task<WireAnswer> SendAsync(LabClient client, string route, byte[] body, bool declared, int? contract)
    {
        var host = await client.HostAsync();
        using var http = host.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, route);

        request.Content = declared ? new ByteArrayContent(body) : new StreamContent(new Unsized(body));
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        if (contract is not null)
            request.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, contract.ToString());

        request.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, LabIdentity.OrganisationA.ToString("D"));
        request.Headers.TryAddWithoutValidation(LabIdentity.UserHeader, LabIdentity.User.ToString("D"));

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? parsed = null;

        try
        {
            parsed = text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            // No JSON: the status says it.
        }

        return new WireAnswer(response.StatusCode, parsed, text, new Dictionary<string, string>(), TimeSpan.Zero, $"POST {route} ({(declared ? "declared" : "undeclared")} length)");
    }

    /// <summary>A stream that does not say how long it is.</summary>
    private sealed class Unsized(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }
}

/// <summary>
/// Every case of <see cref="RequestTextTests"/> on a host whose own middleware has read the body
/// into a buffer ahead of MVC and set it back to its start: the answers are the same.
/// </summary>
[Trait("Category", "Integration")]
public class RequestTextBufferedTests : RequestTextTests
{
    protected override async Task<LabClient> Staff(int? contract = 2) =>
        (await CorpusFleet.SharedAsync()).Variant(LabService.Staff, "buffered", new Dictionary<string, string?> { [FleetHost.BufferBodyKey] = "rewound" }, contract: contract);
}

/// <summary>
/// Every case of <see cref="RequestTextTests"/> on a host whose own middleware has made the body
/// seekable and not read it: the answers are the same.
/// </summary>
[Trait("Category", "Integration")]
public class RequestTextUnreadBufferTests : RequestTextTests
{
    protected override async Task<LabClient> Staff(int? contract = 2) =>
        (await CorpusFleet.SharedAsync()).Variant(LabService.Staff, "buffered-unread", new Dictionary<string, string?> { [FleetHost.BufferBodyKey] = "unread" }, contract: contract);
}
