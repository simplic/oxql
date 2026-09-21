using FluentAssertions;
using OxQL.AspNetCore;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// What a caller wrote reaches a log line on one line and at a bounded length: the entity type
/// and the first legacy path of the compat line, and the first error message of a refusal, which
/// quotes the caller's entity or path.
/// </summary>
public class HostHardeningLogTextTests
{
    // JSON escapes: the line breaks arrive in the parsed request, not in the body text.
    private static readonly string Forged = "nope\\r\\n2026-01-01 00:00:00 [ERR] forged line\\u2028" + new string('x', 20_000);

    private static void AssertOneBoundedLine(string message)
    {
        message.Should().NotContainAny("\r", "\n", "\u2028", "\u0085");
        message.Length.Should().BeLessThan(1_000, "one refused request must not write the body cap into the log");
        message.Should().Contain("forged line", "the text is kept, only its line breaks are not");
    }

    [Fact]
    public async Task The_compat_line_keeps_a_callers_entity_and_path_on_one_bounded_line()
    {
        using var host = new SampleHost();

        await host.Client(contract: null).PostAsync("/OxQL/query", SampleHost.Json($$"""{ "entityType": "{{Forged}}", "pipeline": [] }"""));
        await host.Client(contract: null).PostAsync("/OxQL/query", SampleHost.Json($$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "Number": { "eq": { "$uuid": "x" } } } }, { "match": { "{{Forged}}": { "eq": { "$uuid": "x" } } } }] }"""));

        var lines = host.Logs.Of(OxQLQueryService.CompatLogCategory).ToList();

        lines.Should().HaveCount(2);
        AssertOneBoundedLine(lines[0]);
        lines[1].Should().NotContainAny("\r", "\n");
    }

    [Fact]
    public async Task The_refusal_line_keeps_a_quoted_entity_on_one_bounded_line()
    {
        using var host = new SampleHost();

        await host.Client().PostAsync("/OxQL/query", SampleHost.Json($$"""{ "entityType": "{{Forged}}", "pipeline": [] }"""));

        AssertOneBoundedLine(host.Logs.Of("OxQL.AspNetCore.Controllers.OxQLController").Single(line => line.Contains("refusal")));
    }

    [Fact]
    public async Task A_first_legacy_path_with_a_line_break_stays_on_one_line()
    {
        using var host = new SampleHost();

        await host.Client(contract: null).PostAsync("/OxQL/query", SampleHost.Json($$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "{{Forged}}": { "eq": { "$uuid": "195fb742-82b3-405e-b77b-42838eb0aaa9" } } } }] }"""));

        AssertOneBoundedLine(host.Logs.Of(OxQLQueryService.CompatLogCategory).Single());
    }
}
