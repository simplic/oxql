using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Perf;

/// <summary>
/// The performance bench: the requests a grid sends, over a volume without an index
/// (<see cref="PerfVolume"/>), each measured for its wall time, the commands it costs the database
/// and the time those commands took as the driver saw them. It runs only when asked:
/// <list type="bullet">
/// <item><c>OXQL_PERF=&lt;file&gt;</c> runs it and appends one Markdown table row per request to that file, and to
/// <c>&lt;file&gt;.coding.md</c> what the content coding of each answer costs and saves, and to
/// <c>&lt;file&gt;.timing.md</c> the engine's own breakdown of one run of each request;</item>
/// <item><c>OXQL_PERF_LABEL</c> names the run in the table (a commit, "before", "after");</item>
/// <item><c>OXQL_PERF_ROWS</c> is the number of shipments (25 000);</item>
/// <item><c>OXQL_PERF_RUNS</c> is the number of measured runs per request (15, after 3 warm ones);</item>
/// <item><c>OXQL_PERF_ONLY</c> keeps the requests whose name contains it.</item>
/// </list>
/// Without <c>OXQL_PERF</c> the test does nothing and passes. Run it in a Release build for the
/// engine's own share: <c>dotnet test -c Release --filter Category=Perf</c>.
/// <para>
/// The wall time is the in-process host's (no network); the database time is the union of the
/// command spans the run's client reports, so commands in flight together count once.
/// </para>
/// </summary>
[Trait("Category", "Perf")]
public class PerfBench(ITestOutputHelper output)
{
    internal const string Variable = "OXQL_PERF";

    private const string Columns = """ "shipmentNumber": 1, "referenceNumber": 1, "loadStart": 1, "loadEnd": 1, "deliveryStart": 1, "status.name": 1, "customer.address.companyName": 1, "customer.address.city": 1, "loadAddress.companyName": 1, "loadAddress.city": 1, "deliveryAddress.companyName": 1, "deliveryAddress.city": 1, "actualWeight.value": 1, "incoterm": 1 """;

    private const string Live = """{ "match": { "isDeleted": { "neq": true } } }""";

    /// <summary>One measured request: what it is called and how it is sent.</summary>
    private sealed record Case(string Name, Func<LabClient, Task<WireAnswer>> Send);

    [Fact]
    public async Task Measure_the_grid_requests()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } file)
            return;

        var rows = Number("OXQL_PERF_ROWS", 25_000);
        var runs = Number("OXQL_PERF_RUNS", 15);
        var label = Environment.GetEnvironmentVariable("OXQL_PERF_LABEL") ?? "run";
        var only = Environment.GetEnvironmentVariable("OXQL_PERF_ONLY");

        await using var fleet = await CorpusFleet.CreateAsync("perf", [LabService.Transport, LabService.Fleet], extra: lab => PerfVolume.SeedAsync(lab, rows));
        using var capture = await MongoFixture.Commands.WatchAsync(fleet.Fleet);
        var client = fleet.Client(LabService.Transport, Org.C);
        var cases = await CasesAsync(client, rows);
        var text = new StringBuilder();
        var coding = new StringBuilder();
        var samples = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"## {label}").AppendLine();

        if (!File.Exists(file + ".coding.md"))
            coding.AppendLine("| run | request | body bytes | br bytes | br ms | gzip bytes | gzip ms |").AppendLine("|---|---|---|---|---|---|---|");

        if (!File.Exists(file))
            text.AppendLine("| run | request | wall p50 ms | wall p95 ms | db p50 ms | ours p50 ms | aggregates | getMores | rows | body bytes | Server-Timing ours p50 ms | of it convert |").AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var each in cases.Where(each => only is null || each.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
        {
            for (var warm = 0; warm < 3; warm++)
                (await each.Send(client)).ShouldBeOk(each.Name);

            capture.Drain();

            var wall = new List<double>();
            var database = new List<double>();
            var ours = new List<double>();
            var engine = new List<double>();
            var convert = new List<double>();
            CommandBatch commands = new([]);
            WireAnswer answer = null!;

            for (var run = 0; run < runs; run++)
            {
                var started = Stopwatch.GetTimestamp();

                answer = await each.Send(client);

                var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                // A command the engine no longer waits for may still be running: give it a moment to end.
                await Task.Delay(5);
                commands = capture.Drain();

                var spent = Union(commands.Engine.Where(command => command.Ended != 0).Select(command => (command.Started, command.Ended)));

                wall.Add(took);
                database.Add(spent);
                ours.Add(took - Math.Min(took, spent));

                // The engine's own account of the request, where it gives one (the Server-Timing header).
                if (answer.Headers.TryGetValue("Server-Timing", out var header))
                {
                    engine.Add(Metric(header, "ours"));
                    convert.Add(Metric(header, "convert"));
                }
            }

            answer.ShouldBeOk(each.Name);

            var line = string.Create(CultureInfo.InvariantCulture,
                $"| {label} | {each.Name} | {Percentile(wall, 0.5):F1} | {Percentile(wall, 0.95):F1} | {Percentile(database, 0.5):F1} | {Percentile(ours, 0.5):F1} | {commands.Count("aggregate")} | {commands.Count("getMore")} | {answer.Items.Count} | {Encoding.UTF8.GetByteCount(answer.Text)} | {(engine.Count == 0 ? "" : Percentile(engine, 0.5).ToString("F2", CultureInfo.InvariantCulture))} | {(convert.Count == 0 ? "" : Percentile(convert, 0.5).ToString("F2", CultureInfo.InvariantCulture))} |");

            output.WriteLine(line);
            text.AppendLine(line);
            coding.AppendLine(Coding(label, each.Name, Encoding.UTF8.GetBytes(answer.Text)));

            // The engine's breakdown of one more run of the request, with its Server-Timing header.
            var detailed = await each.Send(client.WithHeader("X-OxQL-Timing", "1"));

            samples.AppendLine(CultureInfo.InvariantCulture, $"### {each.Name}").AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"`Server-Timing: {(detailed.Headers.TryGetValue("Server-Timing", out var said) ? said : "")}`").AppendLine()
                .AppendLine("```json").AppendLine(detailed.Body?["timing"]?.ToJsonString() ?? "null").AppendLine("```").AppendLine();
        }

        await File.AppendAllTextAsync(file + ".timing.md", samples.ToString());

        await File.AppendAllTextAsync(file, text.ToString());
        await File.AppendAllTextAsync(file + ".coding.md", coding.ToString());
    }

    /// <summary>What coding an answer costs this process and saves on the wire: the body at the level the host codes at, timed over many runs.</summary>
    private static string Coding(string label, string name, byte[] body)
    {
        (int Bytes, double Ms) Timed(string encoding)
        {
            var coded = OxQL.AspNetCore.Models.WireCompression.Compress(body, encoding);
            const int Runs = 200;
            var started = Stopwatch.GetTimestamp();

            for (var run = 0; run < Runs; run++)
                OxQL.AspNetCore.Models.WireCompression.Compress(body, encoding);

            return (coded.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds / Runs);
        }

        var brotli = Timed(OxQL.AspNetCore.Models.WireCompression.Brotli);
        var gzip = Timed(OxQL.AspNetCore.Models.WireCompression.Gzip);

        return string.Create(CultureInfo.InvariantCulture, $"| {label} | {name} | {body.Length} | {brotli.Bytes} | {brotli.Ms:F3} | {gzip.Bytes} | {gzip.Ms:F3} |");
    }

    /// <summary>The requests, with the cursors the deeper blocks continue from walked to once.</summary>
    private static async Task<IReadOnlyList<Case>> CasesAsync(LabClient client, int rows)
    {
        string List(string sort, string page, bool projectFirst = true) => projectFirst
            ? $$"""[ {{Live}}, { "project": { {{Columns}} } }, { "sort": [ {{sort}} ] }, { "page": {{page}} } ]"""
            : $$"""[ {{Live}}, { "sort": [ {{sort}} ] }, { "project": { {{Columns}} } }, { "page": {{page}} } ]""";

        const string ByNumber = """{ "shipmentNumber": "asc" }""";
        const string ByLoad = """{ "loadStart": "desc" }""";
        const string ByCompany = """{ "customer.address.companyName": "asc" }""";

        string Unwind(string sort, string page) => $$"""
            [ {{Live}}, { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } },
              { "project": { "shipmentNumber": 1, "loadStart": 1, "status.name": 1, "customer.address.companyName": 1, "position": 1, "item.text": 1, "item.reference": 1, "item.orderNumber": 1, "item.loadingMeters": 1, "item.shippingUnit": 1, "item.quantity.value": 1, "item.weight.value": 1 } },
              { "sort": [ {{sort}} ] }, { "page": {{page}} } ]
            """;

        string Group(string by, string page) => $$"""
            [ {{Live}}, { "group": { "by": {{by}}, "fields": { "n": { "count": true }, "weight": { "sum": "actualWeight.value" }, "first": { "min": "loadStart" } } } },
              { "page": {{page}} } ]
            """;

        const string Month = """[ { "dateTrunc": { "path": "loadStart", "unit": "month", "timezone": "Europe/Berlin" }, "as": "month" } ]""";

        string Joined(string more, string project) => $$"""
            [ {{Live}}, { "resolve": { "path": "tours.tourId", "as": "tour", "elements": "first", "onMissing": "null" } }{{more}},
              { "project": { "shipmentNumber": 1, "loadStart": 1, "status.name": 1, {{project}} } },
              { "sort": [ {{ByNumber}} ] }, { "page": { "limit": 100 } } ]
            """;

        // The cursor a block continues from: the request walked that many blocks deep, once.
        async Task<string> CursorAsync(Func<string, string> request, int blocks, int limit)
        {
            string? cursor = null;

            for (var block = 0; block < blocks; block++)
            {
                var page = cursor is null ? $$"""{ "limit": {{limit}} }""" : $$"""{ "limit": {{limit}}, "cursor": "{{cursor}}" }""";
                var answer = await client.SendAsync(Corpus.Shipment, request(page));

                answer.ShouldBeOk("the walk to a deeper block");
                cursor = answer.NextCursor ?? throw new InvalidOperationException("The volume is too small for the deeper blocks.");
            }

            return cursor!;
        }

        Case Of(string name, string pipeline) => new(name, sender => sender.SendAsync(Corpus.Shipment, pipeline));

        var half = Math.Max(1, rows / 2 / 500);
        var second = await CursorAsync(page => List(ByNumber, page), 1, 100);
        var middle = await CursorAsync(page => List(ByNumber, page), half, 500);
        var unwound = await CursorAsync(page => Unwind(ByNumber, page), 30, 100);
        var unwoundByElement = await CursorAsync(page => Unwind("""{ "item.reference": "asc" }""", page), 30, 100);
        var grouped = await CursorAsync(page => Group("""[ { "path": "customer.address.companyName", "as": "company" } ]""", page), 2, 100);

        return
        [
            Of("block, sort shipmentNumber, 14 cols (project before sort)", List(ByNumber, """{ "limit": 100 }""")),
            Of("block, sort shipmentNumber, 14 cols (project after sort)", List(ByNumber, """{ "limit": 100 }""", projectFirst: false)),
            Of("block, sort loadStart desc", List(ByLoad, """{ "limit": 100 }""")),
            Of("block, sort companyName (collated)", List(ByCompany, """{ "limit": 100 }""")),
            Of("block 2 by cursor", List(ByNumber, $$"""{ "limit": 100, "cursor": "{{second}}" }""")),
            Of("block at 50 % by cursor", List(ByNumber, $$"""{ "limit": 100, "cursor": "{{middle}}" }""")),
            Of("block with count: true", List(ByNumber, """{ "limit": 100, "count": true }""")),
            Of("count only", $$"""[ {{Live}}, { "page": { "limit": 0, "count": { "budgetMs": 800 } } } ]"""),
            Of("filtered block, contains", $$"""[ {{Live}}, { "match": { "customer.address.companyName": { "contains": "muller" } } }, { "project": { {{Columns}} } }, { "sort": [ {{ByNumber}} ] }, { "page": { "limit": 100 } } ]"""),
            Of("unwind block, sort parent key", Unwind(ByNumber, """{ "limit": 100 }""")),
            Of("unwind block 31, sort parent key", Unwind(ByNumber, $$"""{ "limit": 100, "cursor": "{{unwound}}" }""")),
            Of("unwind block, sort element key", Unwind("""{ "item.reference": "asc" }""", """{ "limit": 100 }""")),
            Of("unwind block 31, sort element key", Unwind("""{ "item.reference": "asc" }""", $$"""{ "limit": 100, "cursor": "{{unwoundByElement}}" }""")),
            Of("unwind count only", $$"""[ {{Live}}, { "unwind": { "path": "items", "as": "item" } }, { "page": { "limit": 0, "count": { "budgetMs": 800 } } } ]"""),
            Of("group by status, count: true", Group("""[ { "path": "status.name", "as": "status" } ]""", """{ "limit": 100, "count": true }""")),
            Of("group by month (Europe/Berlin), count: true", Group(Month, """{ "limit": 100, "count": true }""")),
            Of("group by company, block 3 by cursor", Group("""[ { "path": "customer.address.companyName", "as": "company" } ]""", $$"""{ "limit": 100, "cursor": "{{grouped}}" }""")),
            Of("join shipment → tour", Joined("", """ "tour.number": 1 """)),
            Of("join shipment → tour → vehicle", Joined(""", { "resolve": { "path": "tour.resource.id", "as": "vehicle", "target": "fleet.vehicle", "onMissing": "null" } }""", """ "tour.number": 1, "vehicle.matchCode": 1 """)),
        ];
    }

    /// <summary>The duration of one metric of a <c>Server-Timing</c> value; 0 when it is not there.</summary>
    private static double Metric(string header, string name) =>
        header.Split(", ").Select(metric => metric.Split(';')).FirstOrDefault(parts => parts[0] == name) is { } found
        && double.TryParse(found.FirstOrDefault(part => part.StartsWith("dur=", StringComparison.Ordinal))?[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ? duration : 0;

    private static int Number(string variable, int otherwise) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : otherwise;

    private static double Percentile(List<double> values, double at)
    {
        var sorted = values.OrderBy(value => value).ToList();

        return sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(at * sorted.Count))];
    }

    /// <summary>The time covered by <paramref name="spans"/> in milliseconds: overlapping spans count once.</summary>
    internal static double Union(IEnumerable<(long Started, long Ended)> spans)
    {
        long covered = 0;
        long until = long.MinValue;

        foreach (var (started, ended) in spans.OrderBy(span => span.Started))
        {
            var from = Math.Max(started, until);

            if (ended > from)
                covered += ended - from;

            until = Math.Max(until, ended);
        }

        return covered * 1000.0 / Stopwatch.Frequency;
    }
}
