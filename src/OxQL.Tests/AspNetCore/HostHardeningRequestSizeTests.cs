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

    /// <summary>Reads every body into a buffer ahead of MVC and rewinds it, as a host's own middleware may.</summary>
    private sealed class BufferingStartupFilter(List<long?> declared) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                declared.Add(context.Request.ContentLength);
                context.Request.EnableBuffering();
                await context.Request.Body.CopyToAsync(Stream.Null);
                context.Request.Body.Position = 0;
                await pipeline(context);
            });
            next(app);
        };
    }

    private static SampleHost Host(bool buffered, List<long?>? declared = null) => new(
        options => options.Limits.MaxRequestBytes = Max,
        services =>
        {
            if (buffered)
                services.AddSingleton<IStartupFilter>(new BufferingStartupFilter(declared ?? []));
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
    public async Task A_host_that_cannot_cap_an_undeclared_length_says_so_once()
    {
        // The test server has no body size limit to set, and nothing buffers the body.
        using var host = Host(buffered: false);

        for (var call = 0; call < 3; call++)
            (await host.Client().SendAsync(Chunked("/OxQL/query", Small))).StatusCode.Should().Be(HttpStatusCode.OK);

        host.Logs.Entries.Where(entry => entry.Category == typeof(RequestSizeFilter).FullName && entry.Level == LogLevel.Warning)
            .Should().ContainSingle().Which.Message.Should().Contain("undeclared length").And.Contain(Max.ToString());
    }

    [Fact]
    public async Task A_declared_length_is_never_warned_about()
    {
        using var host = Host(buffered: false);

        (await host.Client().PostAsync("/OxQL/query", SampleHost.Json(Small))).StatusCode.Should().Be(HttpStatusCode.OK);

        host.Logs.Of(typeof(RequestSizeFilter).FullName!).Should().BeEmpty();
    }
}
