using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// What a host that buffers the body ahead of MVC answers differently from one that hands it on
/// from the wire: the buffer is read up to the length a request may have, so a body of undeclared
/// length is held to that length like one that declares it.
/// </summary>
public abstract class HostRequestBodyOverBufferTests : HostRequestBodyTests
{
    private static readonly string Padding = new('x', Cap * 2);

    [Fact]
    public override async Task A_body_of_undeclared_length_that_outgrows_what_is_kept_and_does_not_read_is_refused_without_a_place() =>
        await ShouldBeTooLargeAsync($$"""{ "entityType": 5, "pipeline": [], "variables": { "pad": "{{Padding}}" } }""");

    [Fact]
    public override async Task A_body_of_undeclared_length_that_outgrows_what_is_kept_and_reads_is_bound_as_without_the_copy() =>
        await ShouldBeTooLargeAsync($$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "{{Padding}}" } } }, { "page": { "limit": 1 } }] }""");

    private async Task ShouldBeTooLargeAsync(string body)
    {
        using var host = Capped();

        var response = await host.Client(contract: 2).SendAsync(Chunked(Query, body));
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, text);
        JsonNode.Parse(text)!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.RequestTooLarge);
        host.Runner.Calls.Should().BeEmpty();
    }
}

/// <summary>
/// Every case of <see cref="HostRequestBodyTests"/> on a host whose own middleware has read the
/// body into a buffer ahead of MVC and set it back to its start: the answers are the same.
/// </summary>
public class HostRequestBodyBufferedTests : HostRequestBodyOverBufferTests
{
    private protected override SampleHost NewHost(Action<OxQLOptions>? options = null, Action<IServiceCollection>? configure = null) => new(options, services =>
    {
        services.AddSingleton<IStartupFilter>(new BufferingStartupFilter(BufferedBody.Rewound));
        configure?.Invoke(services);
    });
}

/// <summary>
/// Every case of <see cref="HostRequestBodyTests"/> on a host whose own middleware has made the
/// body seekable and not read it: the answers are the same.
/// </summary>
public class HostRequestBodyUnreadBufferTests : HostRequestBodyOverBufferTests
{
    private protected override SampleHost NewHost(Action<OxQLOptions>? options = null, Action<IServiceCollection>? configure = null) => new(options, services =>
    {
        services.AddSingleton<IStartupFilter>(new BufferingStartupFilter(BufferedBody.Unread));
        configure?.Invoke(services);
    });
}

/// <summary>
/// A name or a text that is no Unicode text in a body the host's own middleware has buffered ahead
/// of MVC: the answer is the one a body read from the wire gets, wherever the middleware left the
/// buffer, and the buffer is left where it stood, holding the body as it was written.
/// </summary>
public class HostRequestTextBufferedTests
{
    private const int Cap = 512;

    /// <summary>Half a surrogate pair, as the escape a body writes it with.</summary>
    private const string Half = "\\ud800";

    private static string Value(string text) => $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "{{text}}" } } }, { "page": { "limit": 1 } }] }""";

    private static readonly string InValue = Value("a" + Half);

    private static readonly string InName = $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "num{{Half}}ber": { "eq": "a" } } }] }""";

    private static SampleHost Host(BufferedBody? body, int cap = Cap, List<byte[]>? after = null, Action<IServiceCollection>? configure = null) => new(
        options =>
        {
            options.Limits.MaxRequestBytes = cap;
            options.Explain.MaxRequestBytes = cap;
            options.Limits.MaxBatchQueries = 32;
        },
        services =>
        {
            if (body is { } left)
                services.AddSingleton<IStartupFilter>(new BufferingStartupFilter(left, after: after));

            configure?.Invoke(services);
        });

    private static async Task<(HttpStatusCode Status, string Text)> SendAsync(SampleHost host, string route, string body, int? contract, bool declared = true)
    {
        HttpContent content = declared ? new StringContent(body, Encoding.UTF8, "application/json") : new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));

        if (!declared)
            content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };

        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };

        if (!declared)
            request.Headers.TransferEncodingChunked = true;

        var response = await host.Client(contract).SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<int> Left => new() { (int)BufferedBody.Rewound, (int)BufferedBody.AtEnd, (int)BufferedBody.Unread };

    [Theory]
    [MemberData(nameof(Left))]
    public async Task A_buffered_body_is_answered_as_one_read_from_the_wire(int left)
    {
        using var wire = Host(null);
        using var buffered = Host((BufferedBody)left);

        foreach (var declared in new[] { true, false })
        foreach (var contract in new int?[] { 2, 1, null })
        foreach (var route in new[] { "/OxQL/query", "/OxQL/explain" })
        foreach (var body in new[] { InValue, InName })
        {
            var expected = await SendAsync(wire, route, body, contract, declared);
            var answer = await SendAsync(buffered, route, body, contract, declared);
            var because = $"{route} contract {contract?.ToString() ?? "none"} {(declared ? "declared" : "undeclared")} {body}";

            answer.Status.Should().Be(HttpStatusCode.BadRequest, because + ": " + answer.Text);
            WithoutTrace(answer.Text).Should().Be(WithoutTrace(expected.Text), because);

            if (contract == 2)
            {
                var error = JsonNode.Parse(answer.Text)!["errors"]!.AsArray().Should().ContainSingle().Subject!;

                error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
                error["params"]!["reason"]!.GetValue<string>().Should().Be("encoding");
                error["params"]!["in"]!.GetValue<string>().Should().Be(body == InName ? "name" : "value");
            }
        }

        buffered.Runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Left))]
    public async Task An_entry_of_a_buffered_batch_is_answered_in_its_place_and_the_others_run(int left)
    {
        using var wire = Host(null);
        using var buffered = Host((BufferedBody)left);

        var good = """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }] }""";
        var body = $$"""{ "queries": [{{good}}, {{InName}}, {{good}}] }""";
        var expected = await SendAsync(wire, "/OxQL/batch", body, 2);
        var answer = await SendAsync(buffered, "/OxQL/batch", body, 2);

        if ((BufferedBody)left == BufferedBody.AtEnd)
        {
            // Nothing is left for the binder to read, so no entry can run: the text is still what is said.
            answer.Status.Should().Be(HttpStatusCode.BadRequest, answer.Text);
            JsonNode.Parse(answer.Text)!["errors"]!.AsArray().Select(error => error!["params"]!.ToJsonString()).Should().Equal(
                """{"reason":"encoding","path":"$.queries[1].pipeline[0].match","in":"name"}""");

            return;
        }

        answer.Status.Should().Be(HttpStatusCode.OK, answer.Text);
        answer.Text.Should().Be(expected.Text);

        var results = JsonNode.Parse(answer.Text)!["results"]!.AsArray();

        results[0]!["items"].Should().NotBeNull();
        results[2]!["items"].Should().NotBeNull();
        results[1]!["errors"]!.AsArray().Select(error => error!["params"]!.ToJsonString()).Should().Equal(
            """{"reason":"encoding","path":"$.pipeline[0].match","in":"name","entry":1}""");
    }

    [Theory]
    [InlineData((int)BufferedBody.Rewound)]
    [InlineData((int)BufferedBody.Unread)]
    public async Task A_buffered_batch_answered_entry_by_entry_is_read_after_the_action_as_it_was_written(int left)
    {
        var after = new List<byte[]>();

        using var buffered = Host((BufferedBody)left, after: after);

        var good = """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }] }""";
        var body = $$"""{ "queries": [{{good}}, {{InName}}, {{good}}] }""";

        foreach (var declared in new[] { true, false })
        {
            after.Clear();

            var answer = await SendAsync(buffered, "/OxQL/batch", body, 2, declared);

            answer.Status.Should().Be(HttpStatusCode.OK, answer.Text);
            JsonNode.Parse(answer.Text)!["results"]![1]!["errors"].Should().NotBeNull(answer.Text);
            Encoding.UTF8.GetString(after.Should().ContainSingle().Subject).Should().Be(body);
        }
    }

    /// <summary>A body a contract 1 request does not bind for another reason: the host answers it by its own means.</summary>
    private const string Unbound = """{ "entityType": 5, "pipeline": [] }""";

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Without_the_contract_header_such_a_body_is_answered_in_the_form_of_the_hosts_own_400(int? left)
    {
        using var host = Host((BufferedBody?)left);

        foreach (var declared in new[] { true, false })
        foreach (var contract in new int?[] { 1, null })
        foreach (var route in new[] { "/OxQL/query", "/OxQL/explain", "/OxQL/batch" })
        foreach (var body in new[] { InValue, InName })
        {
            var own = await SendAsync(host, route, Unbound, contract, declared);
            var answer = await SendAsync(host, route, body, contract, declared);
            var because = $"{route} contract {contract?.ToString() ?? "none"} {(declared ? "declared" : "undeclared")} {body}: {answer.Text}";

            own.Status.Should().Be(HttpStatusCode.BadRequest, own.Text);
            answer.Status.Should().Be(HttpStatusCode.BadRequest, because);

            var said = JsonNode.Parse(answer.Text)!.AsObject();
            var hosts = JsonNode.Parse(own.Text)!.AsObject();

            // The members of the host's problem, with its type, its title and the id of the request.
            said.Select(member => member.Key).Should().Equal(hosts.Select(member => member.Key), because);
            said["type"]!.GetValue<string>().Should().Be(hosts["type"]!.GetValue<string>(), because);
            said["title"]!.GetValue<string>().Should().Be(hosts["title"]!.GetValue<string>(), because);
            said["status"]!.GetValue<int>().Should().Be(400, because);
            said["traceId"]!.GetValue<string>().Should().NotBeNullOrEmpty(because);
            said["errors"]!.AsObject().Select(member => member.Key).Should().Equal(["$"], because);
            said["errors"]!["$"]![0]!.GetValue<string>().Should().Contain("no Unicode text", because);
        }

        host.Runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Without_the_contract_header_such_a_body_is_answered_by_the_factory_the_host_configured(int? left)
    {
        // A host that builds its 400 for an invalid model state itself, as a service's base package does.
        using var host = Host((BufferedBody?)left, configure: services => services.Configure<ApiBehaviorOptions>(behaviour =>
            behaviour.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(new
            {
                type = "urn:host:problem:bad-request",
                title = "Bad Request",
                status = 400,
                instance = context.HttpContext.Request.Path.Value,
                errors = context.ModelState.Where(entry => entry.Value!.Errors.Count > 0).ToDictionary(entry => entry.Key, entry => entry.Value!.Errors.Select(error => error.ErrorMessage).ToArray()),
            })));

        foreach (var declared in new[] { true, false })
        foreach (var contract in new int?[] { 1, null })
        foreach (var body in new[] { InValue, InName })
        {
            var own = JsonNode.Parse((await SendAsync(host, "/OxQL/query", Unbound, contract, declared)).Text)!;
            var answer = await SendAsync(host, "/OxQL/query", body, contract, declared);
            var because = $"contract {contract?.ToString() ?? "none"} {(declared ? "declared" : "undeclared")} {body}: {answer.Text}";

            own["type"]!.GetValue<string>().Should().Be("urn:host:problem:bad-request");
            answer.Status.Should().Be(HttpStatusCode.BadRequest, because);

            var said = JsonNode.Parse(answer.Text)!;

            said["type"]!.GetValue<string>().Should().Be("urn:host:problem:bad-request", because);
            said["title"]!.GetValue<string>().Should().Be("Bad Request", because);
            said["instance"]!.GetValue<string>().Should().Be("/OxQL/query", because);
            said["errors"]!.AsObject().Select(member => member.Key).Should().Equal(["$"], because);
            said["errors"]!["$"]![0]!.GetValue<string>().Should().Contain("no Unicode text", because);
        }

        // A contract 2 request is answered in the refusal envelope whatever the host configured.
        var coded = await SendAsync(host, "/OxQL/query", InValue, 2);

        JsonNode.Parse(coded.Text)!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody, coded.Text);
        host.Runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Without_the_contract_header_a_factory_that_answers_nothing_leaves_the_400_of_a_host_without_one(int? left)
    {
        using var host = Host((BufferedBody?)left, configure: services => services.Configure<ApiBehaviorOptions>(behaviour =>
            behaviour.InvalidModelStateResponseFactory = _ => null!));

        foreach (var declared in new[] { true, false })
        foreach (var contract in new int?[] { 1, null })
        foreach (var route in new[] { "/OxQL/query", "/OxQL/explain", "/OxQL/batch" })
        foreach (var body in new[] { InValue, InName })
        {
            var answer = await SendAsync(host, route, body, contract, declared);
            var because = $"{route} contract {contract?.ToString() ?? "none"} {(declared ? "declared" : "undeclared")} {body}: {answer.Text}";

            answer.Status.Should().Be(HttpStatusCode.BadRequest, because);

            var said = JsonNode.Parse(answer.Text)!;

            said["status"]!.GetValue<int>().Should().Be(400, because);
            said["errors"]!.AsObject().Select(member => member.Key).Should().Equal(["$"], because);
            said["errors"]!["$"]![0]!.GetValue<string>().Should().Contain("no Unicode text", because);
        }

        host.Runner.Calls.Should().BeEmpty();
    }

    /// <summary>A byte order mark, as a client may write it ahead of a body.</summary>
    private const string Mark = "\uFEFF";

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task A_body_behind_a_byte_order_mark_is_answered_as_the_body_without_it(int? left)
    {
        using var host = Host((BufferedBody?)left);

        foreach (var declared in new[] { true, false })
        foreach (var contract in new int?[] { 2, 1, null })
        foreach (var route in new[] { "/OxQL/query", "/OxQL/explain" })
        foreach (var body in new[] { InValue, InName })
        {
            var expected = await SendAsync(host, route, body, contract, declared);
            var answer = await SendAsync(host, route, Mark + body, contract, declared);
            var because = $"{route} contract {contract?.ToString() ?? "none"} {(declared ? "declared" : "undeclared")} {body}";

            answer.Status.Should().Be(HttpStatusCode.BadRequest, because + ": " + answer.Text);
            WithoutTrace(answer.Text).Should().Be(WithoutTrace(expected.Text), because);

            if (contract == 2)
                JsonNode.Parse(answer.Text)!["errors"]![0]!["params"]!["in"]!.GetValue<string>().Should().Be(body == InName ? "name" : "value", because);
        }

        host.Runner.Calls.Should().BeEmpty();

        if ((BufferedBody?)left == BufferedBody.AtEnd)
            return;

        // An entry of a batch behind the mark is answered in its place.
        var good = """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }] }""";
        var batch = $$"""{ "queries": [{{good}}, {{InName}}] }""";
        var plain = await SendAsync(host, "/OxQL/batch", batch, 2);
        var marked = await SendAsync(host, "/OxQL/batch", Mark + batch, 2);

        marked.Status.Should().Be(HttpStatusCode.OK, marked.Text);
        marked.Text.Should().Be(plain.Text);

        // So is a body that does not bind for another reason.
        var kind = """{ "entityType": 5, "pipeline": [] }""";
        var said = await SendAsync(host, "/OxQL/query", kind, 2);
        var behind = await SendAsync(host, "/OxQL/query", Mark + kind, 2);

        behind.Status.Should().Be(HttpStatusCode.BadRequest, behind.Text);
        WithoutTrace(behind.Text).Should().Be(WithoutTrace(said.Text));
    }

    /// <summary>The wire (null) and every way a host's middleware leaves a buffer.</summary>
    public static TheoryData<int?> Hosts => new() { null, (int)BufferedBody.Rewound, (int)BufferedBody.AtEnd, (int)BufferedBody.Unread };

    /// <summary>An entry of a batch whose operand is no Unicode text.</summary>
    private static readonly string BrokenEntry = """{"entityType":"probe.order","pipeline":[{"match":{"number":{"eq":"a""" + Half + "\"}}}]}";

    private static string Entries(int count) => string.Join(",", Enumerable.Repeat(BrokenEntry, count));

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task A_batch_with_more_such_places_than_a_refusal_names_is_refused_for_what_follows_them(int? left)
    {
        using var host = Host((BufferedBody?)left, cap: 64 * 1024);

        var bodies = new (string What, string Body, string Reason)[]
        {
            ("text after the batch", $$"""{"queries":[{{Entries(21)}}] garbage""", "json"),
            ("a batch that does not end", $$"""{"queries":[{{Entries(21)}}""", "json"),
            ("a name of the batch itself", $$"""{"queries":[{{Entries(20)}}], "x{{Half}}": 1}""", "encoding"),
            ("a text of the batch itself", $$"""{"queries":[{{Entries(20)}}], "x": "{{Half}}"}""", "encoding"),
        };

        foreach (var declared in new[] { true, false })
        foreach (var (what, body, reason) in bodies)
        {
            var answer = await SendAsync(host, "/OxQL/batch", body, 2, declared);
            var because = $"{what}, {(declared ? "declared" : "undeclared")}: {answer.Text}";

            answer.Status.Should().Be(HttpStatusCode.BadRequest, because);

            var errors = JsonNode.Parse(answer.Text)!["errors"]!.AsArray();

            errors.Should().NotBeEmpty(because);
            errors.Should().OnlyContain(error => error!["code"]!.GetValue<string>() == Codes.InvalidRequestBody, because);
            errors.Should().OnlyContain(error => error!["params"]!["reason"]!.GetValue<string>() == reason, because);
        }

        host.Runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Every_entry_of_a_batch_with_more_such_places_than_a_refusal_names_is_answered_in_its_place(int? left)
    {
        if ((BufferedBody?)left == BufferedBody.AtEnd)
            return;

        using var host = Host((BufferedBody?)left, cap: 64 * 1024);

        var good = """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }] }""";

        foreach (var declared in new[] { true, false })
        {
            host.Runner.Calls.Clear();

            var answer = await SendAsync(host, "/OxQL/batch", $$"""{"queries":[{{Entries(11)}},{{good}},{{Entries(11)}}]}""", 2, declared);

            answer.Status.Should().Be(HttpStatusCode.OK, answer.Text);

            var results = JsonNode.Parse(answer.Text)!["results"]!.AsArray();

            results.Should().HaveCount(23);
            results[11]!["items"].Should().NotBeNull(answer.Text);
            host.Runner.Calls.Should().ContainSingle();

            for (var index = 0; index < 23; index++)
            {
                if (index == 11)
                    continue;

                var error = results[index]!["errors"]!.AsArray().Should().ContainSingle($"entry {index}: {answer.Text}").Subject!;

                error["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
                error["params"]!["reason"]!.GetValue<string>().Should().Be("encoding");
                error["params"]!["entry"]!.GetValue<int>().Should().Be(index);
            }

            // The places a refusal names are named; an entry past them is said without its place.
            results[0]!["errors"]![0]!["params"]!["path"]!.GetValue<string>().Should().Be("$.pipeline[0].match.number.eq");
            results[20]!["errors"]![0]!["params"]!["path"]!.GetValue<string>().Should().Be("$.pipeline[0].match.number.eq");
            results[22]!["errors"]![0]!["params"]!.AsObject().ContainsKey("path").Should().BeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(Left))]
    public async Task A_buffered_body_at_the_length_a_request_may_have_is_checked_and_one_above_it_is_too_large(int left)
    {
        using var buffered = Host((BufferedBody)left);

        var at = Value("a" + Half + new string('x', Cap - Encoding.UTF8.GetByteCount(InValue)));
        var above = Value("a" + Half + new string('x', Cap + 1 - Encoding.UTF8.GetByteCount(InValue)));

        Encoding.UTF8.GetByteCount(at).Should().Be(Cap);
        Encoding.UTF8.GetByteCount(above).Should().Be(Cap + 1);

        foreach (var declared in new[] { true, false })
        {
            var exact = await SendAsync(buffered, "/OxQL/query", at, 2, declared);

            exact.Status.Should().Be(HttpStatusCode.BadRequest, exact.Text);
            JsonNode.Parse(exact.Text)!["errors"]![0]!["params"]!["reason"]!.GetValue<string>().Should().Be("encoding");

            var over = await SendAsync(buffered, "/OxQL/query", above, 2, declared);

            over.Status.Should().Be(HttpStatusCode.RequestEntityTooLarge, $"{(declared ? "declared" : "undeclared")}: {over.Text}");
            JsonNode.Parse(over.Text)!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.RequestTooLarge);
        }

        buffered.Runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Left))]
    public async Task A_buffered_body_that_is_text_is_served_as_before(int left)
    {
        using var wire = Host(null);
        using var buffered = Host((BufferedBody)left);

        // A whole pair, written as two escapes and as the character itself, and a text without one.
        foreach (var body in new[] { Value("\\ud83d\\ude00"), Value("\U0001F600"), Value("plain") })
        foreach (var contract in new int?[] { 2, null })
        {
            var expected = await SendAsync(wire, "/OxQL/query", body, contract);
            var answer = await SendAsync(buffered, "/OxQL/query", body, contract);

            expected.Status.Should().Be(HttpStatusCode.OK, expected.Text);

            if ((BufferedBody)left == BufferedBody.AtEnd)
            {
                // The host's middleware left nothing for the binder to read: the buffer is left where it stood.
                answer.Status.Should().Be(HttpStatusCode.BadRequest, answer.Text);
                continue;
            }

            answer.Status.Should().Be(HttpStatusCode.OK, answer.Text);
            answer.Text.Should().Be(expected.Text);
        }
    }

    /// <summary>An answer without the trace id the host gives each request.</summary>
    private static string WithoutTrace(string text)
    {
        if (text.Length == 0 || JsonNode.Parse(text) is not JsonObject answer)
            return text;

        answer.Remove("traceId");

        return answer.ToJsonString();
    }
}
