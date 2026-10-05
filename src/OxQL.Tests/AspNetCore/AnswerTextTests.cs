using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.AspNetCore.Models;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The text of an answer on the wire, byte for byte: UTF-8, escaped only where JSON requires it. A
/// row's <c>"Köln"</c> is those five characters and not <c>"Köln"</c>, in the answers of
/// <c>query</c>, <c>batch</c> and <c>explain</c> alike, coded or not. A parsed body cannot tell the
/// two apart, so the bytes are held here.
/// </summary>
public class AnswerTextTests
{
    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Text the default encoder would escape, and text every encoder has to: an umlaut, an eszett, a euro sign, what HTML reads, a quote, a backslash and a line break.</summary>
    private const string Text = "Köln Straße € <b> 'x' & 1+1 \"q\" \\ \n";

    /// <summary>The same text as a JSON writer that escapes only what JSON requires spells it.</summary>
    private static readonly string Spelled = JsonSerializer.Serialize(Text, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Query(string number) =>
        $$"""{ "entityType": "probe.order", "pipeline": [ { "match": { "number": { "eq": {{JsonSerializer.Serialize(number)}} } } }, { "project": { "number": 1 } }, { "page": { "limit": 50 } } ] }""";

    private static async Task<HttpResponseMessage> PostAsync(SampleHost host, string route, string body, string? acceptEncoding = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = SampleHost.Json(body) };

        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

        return await host.Client().SendAsync(request);
    }

    private static async Task<byte[]> PlainAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();

        if (response.Content.Headers.ContentEncoding.Count == 0)
            return bytes;

        using var packed = new MemoryStream(bytes);
        using Stream coder = response.Content.Headers.ContentEncoding.Single() == "br" ? new BrotliStream(packed, CompressionMode.Decompress) : new GZipStream(packed, CompressionMode.Decompress);
        using var read = new MemoryStream();

        await coder.CopyToAsync(read);

        return read.ToArray();
    }

    [Theory]
    [InlineData("/OxQL/query", null)]
    [InlineData("/OxQL/query", "br")]
    [InlineData("/OxQL/batch", null)]
    [InlineData("/OxQL/batch", "gzip")]
    public async Task A_row_reaches_the_caller_as_the_text_it_holds_and_not_as_escapes(string route, string? acceptEncoding)
    {
        using var host = new SampleHost();

        // Enough rows for the answer to be worth coding.
        host.Runner.PageRows = [.. Enumerable.Range(0, 40).Select(_ => SampleHost.Row(Id1, Text))];

        var response = await PostAsync(host, route, route.EndsWith("batch", StringComparison.Ordinal) ? $$"""{ "queries": [ {{Query("x")}} ] }""" : Query("x"), acceptEncoding);
        var body = Encoding.UTF8.GetString(await PlainAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentEncoding.Should().Equal(acceptEncoding is null ? [] : [acceptEncoding]);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        body.Should().Contain($"\"number\":{Spelled}", "the text is written as UTF-8; only the quote, the backslash and the line break are escaped");
        body.Should().NotContain("\\u00", "no character of the text is written as an escape").And.NotContain("\\u20AC");
        Encoding.UTF8.GetBytes(body).Should().ContainInOrder(0x4B, 0xC3, 0xB6, 0x6C, 0x6E);

        var parsed = JsonNode.Parse(body)!;
        var rows = route.EndsWith("batch", StringComparison.Ordinal) ? parsed["results"]![0]!["items"]! : parsed["items"]!;

        rows[0]!["number"]!.GetValue<string>().Should().Be(Text, "the escaping changes the bytes, never the value");
    }

    [Fact]
    public async Task An_explain_answer_spells_text_the_same_way()
    {
        using var host = new SampleHost();

        // A path that does not bind is said back in the error: its text is the caller's.
        var response = await PostAsync(host, "/OxQL/explain", """{ "entityType": "probe.order", "pipeline": [ { "match": { "straße.größe": { "eq": "Köln" } } } ] }""");
        var body = Encoding.UTF8.GetString(await PlainAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        JsonNode.Parse(body)!["valid"]!.GetValue<bool>().Should().BeFalse();
        body.Should().Contain("\"path\":\"straße.größe\"").And.Contain("'straße", "a message quotes with an apostrophe, written as one");
        body.Should().NotContain("\\u00").And.NotContain("\\u0027");
    }

    [Fact]
    public void An_answer_is_written_with_the_hosts_options_and_escapes_only_what_JSON_requires_where_they_name_no_encoder()
    {
        var named = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default };

        WireCompression.Unescaped(named).Should().BeSameAs(named, "a host that names an encoder keeps it");

        var unescaped = WireCompression.Unescaped(OxQLJson.Wire);

        unescaped.Should().NotBeSameAs(OxQLJson.Wire).And.BeSameAs(WireCompression.Unescaped(OxQLJson.Wire), "the copy is made once per options");
        unescaped.Encoder.Should().BeSameAs(JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        unescaped.PropertyNamingPolicy.Should().BeSameAs(OxQLJson.Wire.PropertyNamingPolicy);
        unescaped.MaxDepth.Should().Be(OxQLJson.MaxDepth);
        OxQLJson.Wire.Encoder.Should().BeNull("what the engine hashes and measures is spelled as before");

        // Only what JSON requires is escaped: the quote, the backslash and the control characters.
        JsonSerializer.Serialize("\"\\\u0001\u001F\n", unescaped).Should().Be("\"\\\"\\\\\\u0001\\u001F\\n\"");
        JsonSerializer.Serialize("ö<>&'+€", unescaped).Should().Be("\"ö<>&'+€\"");
    }
}
