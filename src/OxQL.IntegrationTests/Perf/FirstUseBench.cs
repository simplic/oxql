using System.Globalization;
using System.Runtime;
using System.Text;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Perf;

/// <summary>
/// What the first use of a code path costs a request: a grid's pair (the count alone and the block,
/// sent together) with a filter the process has not run before, then the same pair again. The
/// first pair pays the runtime's compilation of every method it is the first to call, as this
/// host's own time (<c>ours</c>); the later pairs do not. Each line says the request's
/// <c>Server-Timing</c> sums, its phases, and how many methods the process compiled while the
/// request ran and for how long (process-wide: two requests sent together see the same work).
/// It runs only when asked: <c>OXQL_PERF_FIRSTUSE=&lt;file&gt;</c> writes the lines to that file;
/// without it the test does nothing and passes. Run it alone and in a Release build, so nothing
/// has warmed the process:
/// <c>dotnet test -c Release --filter FullyQualifiedName~FirstUseBench</c>.
/// </summary>
[Trait("Category", "Perf")]
public class FirstUseBench(ITestOutputHelper output)
{
    private const string Columns = """ "shipmentNumber": 1, "referenceNumber": 1, "loadStart": 1, "loadEnd": 1, "deliveryStart": 1, "status.name": 1, "customer.address.companyName": 1, "customer.address.city": 1 """;

    private const string Live = """{ "match": { "isDeleted": { "neq": true } } }, { "match": { "isDeleted": { "neq": true } } }""";

    [Fact]
    public async Task Measure_the_first_use_of_each_filter()
    {
        if (Environment.GetEnvironmentVariable("OXQL_PERF_FIRSTUSE") is not { Length: > 0 } file)
            return;

        await using var fleet = await CorpusFleet.CreateAsync("firstuse", [LabService.Transport, LabService.Fleet], extra: lab => PerfVolume.SeedAsync(lab, 25_000));
        var client = fleet.Client(LabService.Transport, Org.C).WithHeader("X-OxQL-Timing", "1");
        var text = new StringBuilder().AppendLine("| request | total ms | db ms | ours ms | parse | bind | addon | compile | convert | methods compiled | compile ms (process) |").AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");

        async Task One(string name, string pipeline)
        {
            var compiling = JitInfo.GetCompilationTime();
            var methods = JitInfo.GetCompiledMethodCount();
            var answer = await client.SendAsync(Corpus.Shipment, pipeline);

            answer.ShouldBeOk(name);

            var timing = answer.Body!["timing"]!;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"| {name} | {timing["totalMs"]} | {timing["db"]!["ms"]} | {timing["oursMs"]} | {timing["parseMs"]} | {timing["bindMs"]} | {timing["addonMs"]} | {timing["compileMs"]} | {timing["convertMs"]} | {JitInfo.GetCompiledMethodCount() - methods} | {(JitInfo.GetCompilationTime() - compiling).TotalMilliseconds:F1} |");

            lock (text)
                text.AppendLine(line);
        }

        // The count alone and the block, sent together as a grid sends them.
        Task Pair(string name, string filter) => Task.WhenAll(
            One(name + ", count", $$"""[ {{filter}}, { "page": { "limit": 0, "count": { "budgetMs": 800 } } } ]"""),
            One(name + ", block", $$"""[ {{filter}}, { "sort": [ { "shipmentNumber": "asc" } ] }, { "project": { {{Columns}} } }, { "page": { "limit": 200 } } ]"""));

        async Task Repeated(string name, string filter)
        {
            await Pair(name + " (first)", filter);
            await Pair(name + " (second)", filter);
            await Pair(name + " (third)", filter);
        }

        for (var run = 1; run <= 3; run++)
            await One("a page and nothing else (" + run + ")", """[ { "page": { "limit": 5 } } ]""");

        await Repeated("the grid's fixed filter", Live);
        await Repeated("contains", Live + """, { "match": { "customer.address.companyName": { "contains": "muller" } } }""");
        await Repeated("a date range", Live + """, { "match": { "loadStart": { "gte": "2026-01-01T00:00:00Z" } } }""");
        await Repeated("in", Live + """, { "match": { "status.name": { "in": ["Open", "Closed"] } } }""");

        output.WriteLine(text.ToString());
        await File.WriteAllTextAsync(file, text.ToString());
    }
}
