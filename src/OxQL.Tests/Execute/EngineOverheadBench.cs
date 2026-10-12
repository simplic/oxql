using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.Tests.Execute;

/// <summary>
/// The engine's own share of a request with the database taken out: a runner that answers at once
/// with a page of rows, so what is measured is binding, compiling, the cursor, converting the rows
/// and writing the answer. It runs only when asked (<c>OXQL_PERF_ENGINE=1</c>; without it the test
/// is reported as skipped) and prints microseconds per request and per phase. Run it in a Release
/// build: <c>dotnet test src/OxQL.Tests -c Release --filter FullyQualifiedName~EngineOverheadBench</c>
/// with a console logger at detailed verbosity.
/// </summary>
public class EngineOverheadBench(ITestOutputHelper output)
{
    private const string Order = "probe.order";

    private const string Columns = """ "number": 1, "state": 1, "flag": 1, "when": 1, "amount": 1, "count": 1, "ratio": 1, "qrCode": 1, "big": 1, "customerId": 1, "shipTo.city": 1, "shipTo.street": 1, "note": 1, "contactNumber": 1 """;

    private const string Variable = "OXQL_PERF_ENGINE";

    /// <summary>A fact that runs only when <see cref="Variable"/> is set.</summary>
    private sealed class BenchFactAttribute : FactAttribute
    {
        public BenchFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(Variable) is not ("1" or "true"))
                Skip = $"Opt-in: set {Variable}=1 to measure the engine's own share of a request.";
        }
    }

    [BenchFact]
    public async Task Measure_the_engines_own_share()
    {
        var rows = Enumerable.Range(1, 101).Select(index => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(new Guid(index, 0, 0, new byte[8]), GuidRepresentation.Standard),
            ["Number"] = "RE-" + index.ToString("D6", CultureInfo.InvariantCulture),
            ["State"] = index % 3,
            ["Flag"] = index % 2 == 0,
            ["When"] = new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(index)),
            ["Amount"] = new BsonDecimal128(12.5m * index),
            ["Count"] = index,
            ["Ratio"] = index / 7.0,
            ["QRCode"] = "qr-" + index,
            ["Big"] = (long)index * 1_000_000_007,
            ["CustomerId"] = new BsonBinaryData(new Guid(index, 1, 0, new byte[8]), GuidRepresentation.Standard),
            ["ShipTo"] = new BsonDocument { ["City"] = "Stadt " + (index % 40), ["Street"] = "Industriestraße " + index },
            ["Note"] = "Lieferung nur werktags zwischen 7 und 16 Uhr, Rampe " + (index % 9),
            ["ContactNumber"] = "c-" + (index % 30),
        }).ToList();
        var runner = new FakeAggregateRunner { PageRows = rows };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());
        var request = BindHost.Request(Order, $$"""[ { "match": { "flag": { "neq": null } } }, { "project": { {{Columns}} } }, { "sort": [ { "number": "asc" } ] }, { "page": { "limit": 100 } } ]""");
        var context = BindHost.Context();

        async Task<(double Total, double Bind, double Compile, double Convert, double Write, int Bytes)> RunAsync(int runs)
        {
            double bind = 0, compile = 0, convert = 0, write = 0;
            var bytes = 0;
            var started = Stopwatch.GetTimestamp();

            for (var run = 0; run < runs; run++)
            {
                runner.Calls.Clear();

                var result = ((QueryOutcome.Success)await engine.ExecuteAsync(request, context)).Result;
                var writing = Stopwatch.GetTimestamp();

                bytes = JsonSerializer.SerializeToUtf8Bytes(result, BindHost.Json).Length;
                write += Stopwatch.GetElapsedTime(writing).TotalMicroseconds;

                var header = result.Measured!.ServerTiming().Split(", ").Select(metric => metric.Split(';')).ToDictionary(parts => parts[0], parts => double.Parse(parts[1][4..], CultureInfo.InvariantCulture) * 1000);

                bind += header["bind"];
                compile += header["compile"];
                convert += header["convert"];
            }

            return (Stopwatch.GetElapsedTime(started).TotalMicroseconds / runs, bind / runs, compile / runs, convert / runs, write / runs, bytes);
        }

        await RunAsync(2_000);

        var measured = await RunAsync(10_000);

        measured.Bytes.Should().BeGreaterThan(10_000);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{QueryTiming.Build}: {measured.Total:F0} µs per request of 100 rows × 14 columns ({measured.Bytes} B JSON): bind {measured.Bind:F0}, compile {measured.Compile:F0}, convert {measured.Convert:F0}, write {measured.Write:F0}, the rest {measured.Total - measured.Bind - measured.Compile - measured.Convert - measured.Write:F0}"));
    }
}
