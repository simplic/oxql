using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OxQL.AspNetCore.Batch;
using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The body cap for a body that does not declare its length: checked on the buffered stream
/// when a middleware ahead of MVC has read it, and named in the log when the host leaves no
/// way to cap it.
/// </summary>
public class HostHardeningRequestSizeTests
{
    private const int Max = 512;

    private static readonly string Small = """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }] }""";

    private static readonly string Large = $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "{{new string('x', Max * 2)}}" } } }] }""";

    /// <summary>A request sent chunked: it never tells the server how long its body is.</summary>
    private static HttpRequestMessage Chunked(string route, string json)
    {
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };

        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };

        request.Headers.TransferEncodingChunked = true;

        return request;
    }

    private static SampleHost Host(bool buffered, List<long?>? declared = null) => new(
        options => options.Limits.MaxRequestBytes = Max,
        services =>
        {
            if (buffered)
                services.AddSingleton<IStartupFilter>(new BufferingStartupFilter(BufferedBody.Rewound, declared));
        });

    [Theory]
    [InlineData("/OxQL/query")]
    [InlineData("/OxQL/batch")]
    public async Task A_buffered_body_of_undeclared_length_over_the_cap_is_refused_before_it_is_bound(string route)
    {
        var declared = new List<long?>();

        using var host = Host(buffered: true, declared);

        var body = route.EndsWith("batch", StringComparison.Ordinal) ? $$"""{ "queries": [{{Large}}] }""" : Large;
        var response = await host.Client().SendAsync(Chunked(route, body));
        var answer = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, answer?.ToJsonString());
        answer!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.RequestTooLarge);
        host.Runner.Calls.Should().BeEmpty();
        declared.Should().Equal([null], "the request reached the host without a declared length");
    }

    [Fact]
    public async Task A_buffered_body_of_undeclared_length_under_the_cap_is_served()
    {
        using var host = Host(buffered: true);

        var response = await host.Client().SendAsync(Chunked("/OxQL/query", Small));

        response.StatusCode.Should().Be(HttpStatusCode.OK, (await SampleHost.Body(response))?.ToJsonString());
        host.Logs.Of(typeof(RequestSizeFilter).FullName!).Should().BeEmpty("the cap held, so there is nothing to warn about");
    }

    [Fact]
    public async Task A_body_of_undeclared_length_is_held_to_the_cap_where_the_engine_reads_it_whole()
    {
        // The test server has no body size limit to set, and nothing buffers the body. The engine's filter reads a body up
        // to the length a request or an explain may have before it is bound, so its length is known and the cap holds.
        using var host = Host(buffered: false);

        for (var call = 0; call < 3; call++)
            (await host.Client().SendAsync(Chunked("/OxQL/query", Small))).StatusCode.Should().Be(HttpStatusCode.OK);

        host.Logs.Of(typeof(RequestSizeFilter).FullName!).Should().BeEmpty("the cap holds, so there is nothing to warn about");

        var over = await host.Client().SendAsync(Chunked("/OxQL/query", Large));

        over.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await SampleHost.Body(over))!["errors"]![0]!["code"]!.GetValue<string>().Should().Be("REQUEST_TOO_LARGE");
    }

    [Fact]
    public async Task A_declared_length_is_never_warned_about()
    {
        using var host = Host(buffered: false);

        (await host.Client().PostAsync("/OxQL/query", SampleHost.Json(Small))).StatusCode.Should().Be(HttpStatusCode.OK);

        host.Logs.Of(typeof(RequestSizeFilter).FullName!).Should().BeEmpty();
    }
}
