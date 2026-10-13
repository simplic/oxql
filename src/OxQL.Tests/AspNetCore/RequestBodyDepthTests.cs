using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.AspNetCore.Binding;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// A body nested deeper than the host reads is refused for its depth from one pass over its bytes:
/// no document is built of it, so what the refusal costs is in proportion to the body's length,
/// however deep the body nests and wherever in a request the nesting stands.
/// </summary>
public class RequestBodyDepthTests
{
    private const int Limit = OxQLJson.MaxDepth;

    private const int Deep = 131_000;

    private static string Arrays(int depth) => new string('[', depth) + new string(']', depth);

    private static string Objects(int depth) => string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);

    private static string Query(string operand) => $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "in": {{operand}} } } }] }""";

    private static string Batch(string entry) => $$"""{ "queries": [{{entry}}] }""";

    public static TheoryData<string, string> Bodies => new()
    {
        { "arrays", Arrays(Deep) },
        { "objects", Objects(Deep) },
        { "arrays as an operand", Query(Arrays(Deep)) },
        { "objects as an operand", Query(Objects(Deep)) },
        { "arrays as an entry of a batch", Batch(Arrays(Deep)) },
        { "objects as an entry of a batch", Batch(Objects(Deep)) },
        { "arrays as an operand in an entry of a batch", Batch(Query(Arrays(Deep))) },
        { "objects as an operand in an entry of a batch", Batch(Query(Objects(Deep))) },
    };

    /// <summary>What <paramref name="act"/> allocates, on a thread of its own: a thread that has built a document before keeps the memory of it and builds the next from that.</summary>
    private static long Allocated(Action act)
    {
        long allocated = 0;

        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();

            act();

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        thread.Start();
        thread.Join();

        return allocated;
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void A_body_nested_far_deeper_than_the_host_reads_is_refused_for_its_depth_without_a_document_of_it(string what, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        JsonDocument? document = null;
        QueryValidationError? error = null;

        // Once on a small body ahead, so nothing of a first call is counted.
        RequestBodyReader.Parse(Encoding.UTF8.GetBytes(Arrays(Limit + 1)), Limit, out _).Should().NotBeNull();

        var allocated = Allocated(() => error = RequestBodyReader.Parse(body, Limit, out document));

        document.Should().BeNull(what);
        error!.Code.Should().Be(Codes.InvalidRequestBody, what);
        JsonSerializer.Serialize(error.Params).Should().Be($$"""{"reason":"depth","max":{{Limit}}}""", what);

        // The pass keeps one bit per level it stands in; a document keeps several bytes per token.
        allocated.Should().BeLessThan(body.Length / 4, what);
    }

    [Fact]
    public void A_body_at_the_depth_the_host_reads_is_a_document_and_one_level_deeper_is_the_depth()
    {
        RequestBodyReader.Parse(Encoding.UTF8.GetBytes(Arrays(Limit)), Limit, out var at).Should().BeNull();
        at.Should().NotBeNull();
        at!.Dispose();

        RequestBodyReader.Parse(Encoding.UTF8.GetBytes(Objects(Limit)), Limit, out var objects).Should().BeNull();
        objects!.Dispose();

        // A value of no depth of its own lies at the depth of what holds it.
        RequestBodyReader.Parse(Encoding.UTF8.GetBytes(new string('[', Limit) + "1, \"a\"" + new string(']', Limit)), Limit, out var values).Should().BeNull();
        values!.Dispose();

        foreach (var deeper in new[] { Arrays(Limit + 1), Objects(Limit + 1) })
        {
            var error = RequestBodyReader.Parse(Encoding.UTF8.GetBytes(deeper), Limit, out var none);

            none.Should().BeNull();
            JsonSerializer.Serialize(error!.Params).Should().Be($$"""{"reason":"depth","max":{{Limit}}}""");
        }
    }

    [Fact]
    public void A_deep_body_that_is_no_JSON_is_said_as_no_JSON_where_it_stops()
    {
        // Not closed, and closed with the wrong bracket at its deepest place.
        var open = RequestBodyReader.Parse(Encoding.UTF8.GetBytes(new string('[', Deep)), Limit, out _);

        JsonSerializer.Serialize(open!.Params).Should().Be($$"""{"reason":"json","line":1,"position":{{Deep + 1}}}""");

        var wrong = RequestBodyReader.Parse(Encoding.UTF8.GetBytes(new string('[', Deep) + "}" + new string(']', Deep - 1)), Limit, out _);

        JsonSerializer.Serialize(wrong!.Params).Should().Be($$"""{"reason":"json","line":1,"position":{{Deep + 1}}}""");
    }

    [Theory]
    [InlineData("/OxQL/query")]
    [InlineData("/OxQL/explain")]
    [InlineData("/OxQL/batch")]
    public async Task On_a_host_such_a_body_is_the_depth_on_every_route(string route)
    {
        using var host = new SampleHost(options =>
        {
            options.Limits.MaxRequestBytes = 4 * 1024 * 1024;
            options.Explain.MaxRequestBytes = 4 * 1024 * 1024;
        });

        foreach (var nested in new[] { Arrays(Deep), Objects(Deep) })
        {
            var body = route.EndsWith("batch", StringComparison.Ordinal) ? Batch(Query(nested)) : Query(nested);
            var response = await host.Client(contract: 2).PostAsync(route, SampleHost.Json(body));
            var text = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
            JsonNode.Parse(text)!["errors"]!.AsArray().Should().ContainSingle().Which!["params"]!.ToJsonString().Should().Be($$"""{"reason":"depth","max":{{Limit}}}""");
        }

        host.Runner.Calls.Should().BeEmpty();
    }
}
