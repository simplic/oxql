using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.NonQuery;

/// <summary>
/// The answers of <c>query</c> and <c>batch</c> are written in the content coding the caller accepts,
/// as explain and the schema document are: Brotli, else gzip, for a body of at least 1 KB, with
/// <c>Vary: Accept-Encoding</c> either way. The coded body is the plain one, byte for byte.
/// </summary>
[Trait("Category", "Integration")]
public class ContentCodingTests
{
    private const string Page = """{ "entityType": "transport.shipment_template", "pipeline": [ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 100 } } ] }""";

    private const string Small = """{ "entityType": "transport.shipment_template", "pipeline": [ { "project": { "id": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 2 } } ] }""";

    private static async Task<HttpResponseMessage> PostAsync(string route, string body, string? acceptEncoding)
    {
        var host = await (await CorpusFleet.SharedAsync()).Fleet.HostAsync(LabService.Transport);
        var client = host.Client();
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };

        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("OxQL/query", "br", "br")]
    [InlineData("OxQL/query", "gzip", "gzip")]
    [InlineData("OxQL/query", "gzip, deflate, br, zstd", "br")]
    [InlineData("OxQL/query", "br;q=0, gzip", "gzip")]
    [InlineData("OxQL/query", "identity", null)]
    [InlineData("OxQL/query", null, null)]
    [InlineData("OxQL/batch", "br", "br")]
    [InlineData("OxQL/batch", "gzip", "gzip")]
    [InlineData("OxQL/batch", null, null)]
    public async Task A_page_of_rows_is_written_in_the_coding_the_caller_accepts(string route, string? acceptEncoding, string? expected)
    {
        var body = route.EndsWith("batch", StringComparison.Ordinal) ? $$"""{ "queries": [ {{Page}} ] }""" : Page;
        var plain = await (await PostAsync(route, body, null)).Content.ReadAsByteArrayAsync();
        var response = await PostAsync(route, body, acceptEncoding);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
        response.Headers.Vary.Should().Contain("Accept-Encoding", "the body depends on that header, coded or not");
        response.Content.Headers.ContentEncoding.Should().Equal(expected is null ? [] : [expected]);
        response.Content.Headers.ContentLength.Should().Be(bytes.Length);

        if (expected is null)
        {
            bytes.Should().Equal(plain);
            JsonNode.Parse(bytes)!.Should().NotBeNull();

            return;
        }

        bytes.Length.Should().BeLessThan(plain.Length / 4, "a page of rows repeats its member names in every row");

        using var packed = new MemoryStream(bytes);
        using Stream coder = expected == "br" ? new BrotliStream(packed, CompressionMode.Decompress) : new GZipStream(packed, CompressionMode.Decompress);
        using var read = new MemoryStream();

        await coder.CopyToAsync(read);
        read.ToArray().Should().Equal(plain, "the coded body is the answer, byte for byte");
    }

    [Fact]
    public async Task A_small_answer_and_a_refusal_are_not_coded()
    {
        var small = await PostAsync("OxQL/query", Small, "br");

        small.StatusCode.Should().Be(HttpStatusCode.OK);
        small.Content.Headers.ContentEncoding.Should().BeEmpty("coding a body under 1 KB saves less than a packet");
        small.Headers.Vary.Should().Contain("Accept-Encoding");
        JsonNode.Parse(await small.Content.ReadAsByteArrayAsync())!["items"]!.AsArray().Should().HaveCount(2);

        var refused = await PostAsync("OxQL/query", """{ "entityType": "transport.shipment_template", "pipeline": [ { "match": { "nope": { "eq": 1 } } } ] }""", "br");

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refused.Content.Headers.ContentEncoding.Should().BeEmpty();
    }
}
