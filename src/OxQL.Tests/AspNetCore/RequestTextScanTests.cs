using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.AspNetCore.Binding;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The look for a name or a text that is no Unicode text reads a body once and to its end, to the
/// depth the host reads a body to, and keeps nothing per value: the read is in proportion to the
/// body's length, whatever the body nests, and a path is built for the places it names only. A
/// body nested deeper is none of its business; the refusal of the depth answers it.
/// </summary>
public class RequestTextScanTests
{
    private const string Half = "\\ud800";

    private const int Limit = OxQLJson.MaxDepth;

    /// <summary>A request with <paramref name="text"/> as an operand of a set nested in arrays, so that the body nests <paramref name="depth"/> levels.</summary>
    private static string Nested(int depth, string text)
    {
        // The request, its pipeline, the stage, the match, the condition and the set are six levels.
        var arrays = depth - 6;

        return $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "in": [{{new string('[', arrays)}}"{{text}}"{{new string(']', arrays)}}] } } }] }""";
    }

    private static string PathOf(int depth) => "$.pipeline[0].match.number.in[0]" + string.Concat(Enumerable.Repeat("[0]", depth - 6));

    private static long Allocated(Action act)
    {
        // Once ahead, so nothing of the first call (statics, the JIT) is counted.
        act();

        var before = GC.GetAllocatedBytesForCurrentThread();

        act();

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void A_text_at_the_depth_the_host_reads_is_found_with_its_place()
    {
        var found = RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes(Nested(Limit, Half)), Limit);

        found.Should().Equal((PathOf(Limit), false));
    }

    [Fact]
    public void A_body_nested_deeper_than_the_host_reads_is_not_looked_through()
    {
        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes(Nested(Limit + 1, Half)), Limit).Should().BeNull();
    }

    [Fact]
    public void A_body_of_nothing_but_nesting_costs_no_more_than_its_length()
    {
        // As long as a request may be by default, all of it arrays around one text.
        var body = Encoding.UTF8.GetBytes(new string('[', 131_072) + "\"" + Half + "\"" + new string(']', 131_072));
        List<(string Path, bool Name)>? found = [];

        var allocated = Allocated(() => found = RequestBodyReader.BrokenTexts(body, Limit));

        found.Should().BeNull("the body nests deeper than the host reads");
        allocated.Should().BeLessThan(64 * 1024, "the look stops at the depth the host reads to");
    }

    [Fact]
    public void A_wide_body_at_the_depth_the_host_reads_costs_no_more_than_its_length()
    {
        // 20 000 texts written with an escape, each nested as deep as the host reads, and one that is no text at the end.
        var arrays = Limit - 1;
        var one = new string('[', arrays - 1) + "\"\\u0041b\"" + new string(']', arrays - 1);
        var text = new StringBuilder("[");

        for (var index = 0; index < 500; index++)
            text.Append(one).Append(',');

        text.Append('[').Append(string.Join(",", Enumerable.Repeat("\"\\u0041b\"", 20_000))).Append(",\"").Append(Half).Append("\"]]");

        var body = Encoding.UTF8.GetBytes(text.ToString());
        List<(string Path, bool Name)>? found = null;

        var allocated = Allocated(() => found = RequestBodyReader.BrokenTexts(body, Limit));

        found.Should().Equal(("$[500][20000]", false));
        allocated.Should().BeLessThan(body.Length, "a path is built for the place that is found, not for every value");
    }

    [Fact]
    public void Every_place_is_named_as_the_body_has_it_up_to_twenty()
    {
        var body = $$"""{ "a{{Half}}": 1, "b": { "it's": ["x", "{{Half}}"], "c.d": "\udc00", "e{{Half}}": { "f": "{{Half}}" } }, "g": "\ud83d\ude00", "h": "{{Half}}" }""";

        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes(body), Limit).Should().Equal(
            ("$", true),
            ("$.b", true),
            ("$.b['c.d']", false),
            ("$.b['it\\'s'][1]", false),
            ("$.h", false));

        var many = "[" + string.Join(",", Enumerable.Repeat("\"" + Half + "\"", 50)) + "]";

        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes(many), Limit).Should().HaveCount(20);
    }

    [Fact]
    public void The_places_past_the_ones_it_names_are_kept_as_the_entries_of_a_batch_they_lie_in()
    {
        var text = "\"" + Half + "\"";
        var twenty = string.Join(",", Enumerable.Repeat(text, 20));

        // Entry 0 holds the twenty that are named and one more; entries 2 and 3 hold places past them, entry 3 two of them.
        var batch = $$"""{ "queries": [[{{twenty}}, {{text}}], "plain", { "a": {{text}} }, { "b{{Half}}": 1, "c": [{{text}}] }, {{text}}] }""";

        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes(batch), Limit, out var unnamed).Should().HaveCount(20);
        unnamed.Elsewhere.Should().BeFalse();
        unnamed.Entries.Should().Equal(0, 2, 3, 4);

        // A place of the batch itself, a name or a text, lies elsewhere; so does one under another member, and one in a body that is no batch.
        foreach (var rest in new[] { $$""" "x{{Half}}": 1 """, $$""" "x": {{text}} """, $$""" "other": [{{text}}] """, $$""" "queries": {{text}} """ })
        {
            RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes($$"""{ "queries": [[{{twenty}}]], {{rest}} }"""), Limit, out unnamed).Should().HaveCount(20, rest);
            unnamed.Elsewhere.Should().BeTrue(rest);
        }

        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes($"[{twenty},{text}]"), Limit, out unnamed).Should().HaveCount(20);
        unnamed.Elsewhere.Should().BeTrue();

        // The member is told by its name, however it is written.
        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes($$"""{ "queries": [[{{twenty}}], {{text}}] }"""), Limit, out unnamed).Should().HaveCount(20);
        unnamed.Should().BeEquivalentTo(new { Elsewhere = false, Entries = new[] { 1 } });
    }

    [Fact]
    public void A_body_that_is_no_JSON_after_the_places_it_names_is_no_JSON()
    {
        var twenty = string.Join(",", Enumerable.Repeat("\"" + Half + "\"", 21));

        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes($"[{twenty}] garbage"), Limit).Should().BeNull();
        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes($"[{twenty}"), Limit).Should().BeNull();
    }

    [Fact]
    public void A_byte_order_mark_ahead_of_the_body_is_passed_over()
    {
        byte[] marked = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($$"""{ "a": { "b{{Half}}": 1 }, "c": "{{Half}}" }""")];

        RequestBodyReader.BrokenTexts(marked, Limit).Should().Equal(("$.a", true), ("$.c", false));
    }

    [Fact]
    public void Bytes_that_are_no_UTF_8_and_a_body_that_is_no_JSON_are_told_apart()
    {
        var bytes = Encoding.UTF8.GetBytes("""{ "a": "x?y" }""");

        bytes[Array.IndexOf(bytes, (byte)'?')] = 0xFF;

        RequestBodyReader.BrokenTexts(bytes, Limit).Should().Equal(("$.a", false));
        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes("""{ "a": """), Limit).Should().BeNull();
        RequestBodyReader.BrokenTexts(Encoding.UTF8.GetBytes("""{ "a": "b" }"""), Limit).Should().BeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(null)]
    public async Task On_a_host_a_text_at_the_depth_it_reads_is_the_text_and_one_level_deeper_is_the_depth(int? contract)
    {
        using var host = new SampleHost();

        var at = await host.Client(contract).PostAsync("/OxQL/query", SampleHost.Json(Nested(Limit, Half)));
        var atText = await at.Content.ReadAsStringAsync();

        at.StatusCode.Should().Be(HttpStatusCode.BadRequest, atText);

        var deeper = await host.Client(contract).PostAsync("/OxQL/query", SampleHost.Json(Nested(Limit + 1, Half)));
        var deeperText = await deeper.Content.ReadAsStringAsync();

        deeper.StatusCode.Should().Be(HttpStatusCode.BadRequest, deeperText);

        if (contract == 2)
        {
            JsonNode.Parse(atText)!["errors"]![0]!["params"]!.ToJsonString().Should().Be($$"""{"reason":"encoding","path":"{{PathOf(Limit)}}","in":"value"}""");
            JsonNode.Parse(deeperText)!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.InvalidRequestBody);
            JsonNode.Parse(deeperText)!["errors"]![0]!["params"]!.ToJsonString().Should().Be($$"""{"reason":"depth","max":{{Limit}}}""");
        }
        else
        {
            at.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            deeper.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        }

        host.Runner.Calls.Should().BeEmpty();
    }
}
