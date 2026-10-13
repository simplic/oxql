using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
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
/// buffer, and the buffer is left where it stood.
/// </summary>
public class HostRequestTextBufferedTests
{
    private const int Cap = 512;

    /// <summary>Half a surrogate pair, as the escape a body writes it with.</summary>
    private const string Half = "\\ud800";

    private static string Value(string text) => $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "{{text}}" } } }, { "page": { "limit": 1 } }] }""";

    private static readonly string InValue = Value("a" + Half);

    private static readonly string InName = $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "num{{Half}}ber": { "eq": "a" } } }] }""";

    private static SampleHost Host(BufferedBody? body) => new(
        options =>
        {
            options.Limits.MaxRequestBytes = Cap;
            options.Explain.MaxRequestBytes = Cap;
        },
        services =>
        {
            if (body is { } left)
                services.AddSingleton<IStartupFilter>(new BufferingStartupFilter(left));
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
