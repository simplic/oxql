using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Mongo;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Perf;

/// <summary>
/// What a block of unwound rows costs the database, plan against plan: the stages this build compiles
/// for the requests a grid sends over an unwound collection, beside the stages another build compiled
/// for the same requests, run alternately on one connection over one volume without an index. It
/// runs only when asked, and does two things:
/// <list type="bullet">
/// <item><c>OXQL_UNWIND_PLANS=&lt;dir&gt;</c> with <c>OXQL_UNWIND_LABEL=&lt;name&gt;</c> writes the stages this
/// build compiles to <c>&lt;dir&gt;/&lt;name&gt;.json</c> and reads no database. Run it in a work tree of each
/// build to compare (the file compiles against any build that has the unwind placement).</item>
/// <item><c>OXQL_UNWIND_BENCH=&lt;file&gt;</c> seeds the volume, runs this build's stages and those of every
/// file in <c>OXQL_UNWIND_PLANS</c>, and appends the tables to that file: per request and plan the
/// median and the fastest of the measured runs, whether the plans answered the same rows, and for
/// the count alone what the count's ladder would answer under the default budget.</item>
/// <item><c>OXQL_UNWIND_ROWS</c> is the number of shipments (25 000), <c>OXQL_UNWIND_RUNS</c> the
/// measured runs per plan (25, after 3 warm ones), <c>OXQL_UNWIND_IRREGULAR=1</c> stores one shipment
/// in five hundred with a collection that is not what its type says.</item>
/// </list>
/// The volume is shaped like the lab's shipments: about 7 KB each, some fifty members before the
/// collection, three in four with one to three lines, most of them of one unit, the rest with an
/// empty array, a null or no member.
/// </summary>
[Trait("Category", "Perf")]
public class UnwindCutBench(ITestOutputHelper output)
{
    private const string Unwind = """{ "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }""";

    private const string ByParent = """{ "sort": [ { "shipmentNumber": "asc" }, { "id": "asc" }, { "position": "asc" } ] }""";

    private const string Live = """{ "id": "hideDeleted", "where": { "isDeleted": { "neq": true } } }""";

    private static string Where(string condition) => $$"""[ {{Live}}, { "id": "col:unit", "where": {{condition}} } ]""";

    /// <summary>The requests: a name, the stages before the page, and the request's filter.</summary>
    private static readonly (string Name, string Stages, string Filter)[] Requests =
    [
        ("a: no element condition", $"[ {Unwind}, {ByParent} ]", $"[ {Live} ]"),
        ("b: unit neq the frequent one", $"[ {Unwind}, {ByParent} ]", Where("""{ "item.shippingUnit": { "neq": "Palette" } }""")),
        ("b: unit nin [the frequent one]", $"[ {Unwind}, {ByParent} ]", Where("""{ "item.shippingUnit": { "nin": ["Palette"] } }""")),
        ("b: unit neq a rare one", $"[ {Unwind}, {ByParent} ]", Where("""{ "item.shippingUnit": { "neq": "Fass" } }""")),
        ("c: unit eq the frequent one", $"[ {Unwind}, {ByParent} ]", Where("""{ "item.shippingUnit": { "eq": "Palette" } }""")),
        ("c: unit eq a rare one", $"[ {Unwind}, {ByParent} ]", Where("""{ "item.shippingUnit": { "eq": "Fass" } }""")),
        ("c: two conditions on the element", $"[ {Unwind}, {ByParent} ]", Where("""{ "item.shippingUnit": { "eq": "Palette" }, "item.orderNumber": { "gte": 1 } }""")),
        ("p: ordered by number, position", $$"""[ {{Unwind}}, { "sort": [ { "shipmentNumber": "asc" }, { "position": "asc" } ] } ]""", $"[ {Live} ]"),
    ];

    private const int Limit = 200;

    private static readonly EngineDirect Direct = new(LabService.Transport.Model);

    private static string Collection => LabService.Transport.Model.Entities[Corpus.Shipment].Collection;

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private sealed record Compiled(List<BsonDocument> Page, List<BsonDocument> Count, string Placement, bool CountFilterIsTheBounds)
    {
        public BsonDocument Plan => new() { ["page"] = new BsonArray(Page), ["count"] = new BsonArray(Count), ["placement"] = Placement, ["countFilterIsTheBounds"] = CountFilterIsTheBounds };
    }

    private static async Task<Compiled> CompileAsync(string stages, string filter)
    {
        var pipeline = stages.TrimEnd().TrimEnd(']') + $$""", { "page": { "limit": {{Limit}}, "count": {} } } ]""";
        var request = EngineDirect.Request(Corpus.Shipment, pipeline) with { Filter = JsonDocument.Parse(filter).RootElement };
        var outcome = await new Binder(Direct.Model, Direct.Cursors).BindAsync(request, Direct.Context(), CancellationToken.None);
        var bound = outcome.Should().BeOfType<BindOutcome.Bound>(outcome is BindOutcome.Failed failed ? EngineDirect.Describe(failed.Refusal) : "").Subject.Pipeline;
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(30_000, true, 100_000) { KeepDiscriminators = true });

        // Read by name: a build before the count bounded its parents has no such member.
        var filterIsTheBounds = compiled.GetType().GetProperty("CountParentsFilterIsTheBounds")?.GetValue(compiled) is true;

        return new Compiled([.. compiled.PageStages], [.. compiled.CountStages!], compiled.Unwind is { } placed ? $"parentsFirst={placed.ParentsFirst}, prefiltered={placed.Prefiltered.Count}" : "as written", filterIsTheBounds);
    }

    [Fact]
    public async Task Write_the_stages_this_build_compiles()
    {
        if (Environment.GetEnvironmentVariable("OXQL_UNWIND_PLANS") is not { Length: > 0 } directory || Environment.GetEnvironmentVariable("OXQL_UNWIND_LABEL") is not { Length: > 0 } label)
            return;

        var plans = new BsonDocument();

        foreach (var (name, stages, filter) in Requests)
        {
            var compiled = await CompileAsync(stages, filter);

            plans[name] = compiled.Plan;
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), plans.ToJson(Canonical));
    }

    [Fact]
    public async Task Measure_the_plans_of_an_unwound_block()
    {
        if (Environment.GetEnvironmentVariable("OXQL_UNWIND_BENCH") is not { Length: > 0 } file)
            return;

        var parents = Number("OXQL_UNWIND_ROWS", 25_000);
        var runs = Number("OXQL_UNWIND_RUNS", 25);
        var irregular = Environment.GetEnvironmentVariable("OXQL_UNWIND_IRREGULAR") == "1";
        var database = (await MongoFixture.CreateDatabaseAsync("unwind_bench")).Database;
        var collection = database.GetCollection<BsonDocument>(Collection);

        await SeedAsync(collection, parents, irregular);

        // The plans: the other builds' in the order of their names, this build's last.
        var builds = new List<(string Label, BsonDocument Plans)>();

        if (Environment.GetEnvironmentVariable("OXQL_UNWIND_PLANS") is { Length: > 0 } directory && Directory.Exists(directory))
            foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
                builds.Add((Path.GetFileNameWithoutExtension(path), BsonDocument.Parse(await File.ReadAllTextAsync(path))));

        var own = new BsonDocument();

        foreach (var (name, stages, filter) in Requests)
        {
            var compiled = await CompileAsync(stages, filter);

            own[name] = compiled.Plan;
        }

        builds.Add((Environment.GetEnvironmentVariable("OXQL_UNWIND_LABEL") ?? "this build", own));

        var version = (await database.RunCommandAsync<BsonDocument>("{ buildInfo: 1 }"))["version"].AsString;
        var lines = await collection.Aggregate().Unwind("Items").Count().FirstOrDefaultAsync();
        var text = new StringBuilder();

        text.AppendLine(CultureInfo.InvariantCulture, $"## {DateTime.Now:yyyy-MM-dd HH:mm} — MongoDB {version}, {parents} shipments, {lines?.Count ?? 0} rows after the unwind, no index, {(irregular ? "one shipment in 500 not stored as its type says" : "every collection stored as its type says")}, page {Limit}, {runs} runs after 3 warm ones, plans run alternately")
            .AppendLine()
            .AppendLine("| request | plan | placement | median ms | fastest ms | rows | same rows as the first plan |")
            .AppendLine("|---|---|---|---|---|---|---|");

        foreach (var (name, _, _) in Requests)
        {
            var plans = builds.Where(build => build.Plans.Contains(name)).Select(build => (build.Label, Plan: build.Plans[name].AsBsonDocument)).ToList();
            var times = plans.Select(_ => new List<double>()).ToList();
            var answers = new List<string>[plans.Count];

            for (var run = 0; run < 3 + runs; run++)
            {
                for (var index = 0; index < plans.Count; index++)
                {
                    var stages = plans[index].Plan["page"].AsBsonArray.Select(stage => stage.AsBsonDocument).ToList();
                    var watch = Stopwatch.StartNew();
                    var rows = await (await collection.AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages), new AggregateOptions { AllowDiskUse = true })).ToListAsync();

                    watch.Stop();
                    answers[index] = rows.Select(row => $"{row["_id"]}/{row.GetValue("position", BsonNull.Value)}").ToList();

                    if (run >= 3)
                        times[index].Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            for (var index = 0; index < plans.Count; index++)
            {
                var ordered = times[index].Order().ToList();

                text.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {plans[index].Label} | {plans[index].Plan["placement"].AsString} | {ordered[ordered.Count / 2]:F1} | {ordered[0]:F1} | {answers[index].Count} | {(answers[index].SequenceEqual(answers[0]) ? "yes" : "NO")} |");
            }
        }

        // The count alone, as the engine's ladder asks it: a first step of 1 000 rows, then as far as the default budget is predicted to reach.
        text.AppendLine()
            .AppendLine("| count alone | plan | answer under the default budget (500 ms) | median total ms | median first step ms | commands |")
            .AppendLine("|---|---|---|---|---|---|");

        foreach (var (name, _, _) in Requests.Where(request => request.Name[0] != 'p'))
        {
            var plans = builds.Where(build => build.Plans.Contains(name)).Select(build => (build.Label, Plan: build.Plans[name].AsBsonDocument)).ToList();
            var totals = plans.Select(_ => new List<double>()).ToList();
            var firsts = plans.Select(_ => new List<double>()).ToList();
            var said = new Dictionary<int, List<string>>();

            for (var run = 0; run < 2 + 11; run++)
            {
                for (var index = 0; index < plans.Count; index++)
                {
                    var (answer, total, first, commands) = await LadderAsync(collection, plans[index].Plan["count"].AsBsonArray.Select(stage => stage.AsBsonDocument).ToList(), plans[index].Plan.GetValue("countFilterIsTheBounds", false).AsBoolean);

                    if (run < 2)
                        continue;

                    totals[index].Add(total);
                    firsts[index].Add(first);

                    if (!said.TryGetValue(index, out var answersOf))
                        said[index] = answersOf = [];

                    answersOf.Add($"{answer} in {commands}");
                }
            }

            for (var index = 0; index < plans.Count; index++)
            {
                var answersOf = said[index].GroupBy(answer => answer).OrderByDescending(group => group.Count()).Select(group => $"{group.Key} ({group.Count()}×)");

                text.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {plans[index].Label} | {string.Join(", ", answersOf)} | {Median(totals[index]):F1} | {Median(firsts[index]):F1} | |");
            }
        }

        text.AppendLine();
        await File.AppendAllTextAsync(file, text.ToString());
        output.WriteLine(text.ToString());
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    /// <summary>
    /// The engine's count ladder over count stages (<c>MongoQueryEngine.CountLadderAsync</c>): a step
    /// counts up to its bound; one that finds no more is exact; a further step is sent only when ten
    /// times the last is predicted to fit what is left of the budget.
    /// </summary>
    private static async Task<(string Answer, double TotalMs, double FirstMs, int Commands)> LadderAsync(IMongoCollection<BsonDocument> collection, List<BsonDocument> counted, bool filterIsTheBounds)
    {
        const double Budget = 500;
        const long Ceiling = 100_000;
        var stages = counted.Take(counted.Count - 2).ToList();
        var timer = Stopwatch.StartNew();
        long rung = 1_000;
        double first = 0;

        for (var commands = 1; ; commands++)
        {
            var started = timer.Elapsed;

            // The first step keeps a bound on the parents before the unwind, as the engine's ladder does; a later one
            // leaves it out, and with it a filter that is there for the bound alone.
            var step = stages.ToList();
            var parentsAt = step.TakeWhile(stage => !stage.Contains("$unwind")).ToList().FindIndex(stage => stage.Contains("$limit"));

            if (parentsAt >= 0 && commands == 1)
                step[parentsAt] = new BsonDocument("$limit", rung + 1);
            else if (parentsAt >= 0)
                step.RemoveRange(filterIsTheBounds ? parentsAt - 1 : parentsAt, filterIsTheBounds ? 2 : 1);

            var rows = await (await collection.AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create([.. step, new BsonDocument("$limit", rung + 1), new BsonDocument("$count", "n")]), new AggregateOptions { AllowDiskUse = true })).ToListAsync();
            var found = rows.Count > 0 ? rows[0]["n"].ToInt64() : 0;
            var took = Math.Max((timer.Elapsed - started).TotalMilliseconds, 0.01);

            if (commands == 1)
                first = took;

            if (found <= rung)
                return ($"exact {found}", timer.Elapsed.TotalMilliseconds, first, commands);

            if (rung >= Ceiling)
                return ($"atLeast {rung}, cap", timer.Elapsed.TotalMilliseconds, first, commands);

            var fits = (Budget - timer.Elapsed.TotalMilliseconds) / took;

            if (fits < 10)
                return ($"atLeast {rung}, budget", timer.Elapsed.TotalMilliseconds, first, commands);

            var reach = rung * Math.Max(10, fits * 0.5);

            rung = reach >= Ceiling ? Ceiling : (long)reach;
        }
    }

    private static async Task SeedAsync(IMongoCollection<BsonDocument> collection, int parents, bool irregular)
    {
        var filler = string.Concat(Enumerable.Repeat("Lieferung nur werktags zwischen 7 und 16 Uhr, Rampe 3, Avis 30 Minuten vorher. ", 56));
        var day = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var from = 0; from < parents; from += 2_000)
        {
            await collection.InsertManyAsync(Enumerable.Range(from, Math.Min(2_000, parents - from)).Select(n =>
            {
                var scrambled = (int)((long)n * 7_919 % parents);
                var document = new BsonDocument
                {
                    ["_id"] = new BsonBinaryData(new Guid(n, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10), GuidRepresentation.Standard),
                    ["OrganizationId"] = EngineDirect.OrganisationValue,
                    ["IsDeleted"] = n % 50 == 0,
                    ["ShipmentNumber"] = $"S-{scrambled:D7}",
                };

                // What a shipment holds before its lines: addresses, dates, numbers and texts.
                for (var member = 0; member < 48; member++)
                    document[$"Member{member:D2}"] = (member % 6) switch
                    {
                        0 => new BsonDocument { ["CompanyName"] = "Müller Logistik " + (n % 23), ["Street"] = "Industriestraße", ["HouseNumber"] = (n % 180).ToString(CultureInfo.InvariantCulture), ["Zipcode"] = "12345", ["City"] = "Stadt " + (n % 400), ["Country"] = "Deutschland", ["Latitude"] = 47.5, ["Longitude"] = 6.5 },
                        1 => day.AddMinutes((long)scrambled * member),
                        2 => (BsonValue)(n * member),
                        3 => 100.5 * member,
                        _ => $"Wert {member} von {n}",
                    };

                document["Notes"] = filler;

                var lines = n % 4 == 0 ? 0 : 1 + n % 3;

                if (n % 8 == 0)
                    document["Items"] = BsonNull.Value;
                else if (n % 16 == 4)
                    document["Items"] = new BsonArray();
                else if (n % 8 != 4)
                    document["Items"] = new BsonArray(Enumerable.Range(0, lines).Select(position => Line(n, position)));

                if (irregular && n % 500 == 7)
                    document["Items"] = (n / 500 % 5) switch
                    {
                        0 => new BsonArray { Line(n, 0), BsonNull.Value },
                        1 => new BsonArray { BsonNull.Value },
                        2 => Line(n, 0),
                        3 => 7,
                        _ => new BsonArray { "Palette", Line(n, 1) },
                    };

                return document;
            }), new InsertManyOptions { IsOrdered = false });
        }
    }

    /// <summary>A line: five in six on a pallet, the others in a crate, one in ninety in a barrel.</summary>
    private static BsonDocument Line(int n, int position) => new()
    {
        ["_id"] = new BsonBinaryData(new Guid(n, 9, (short)position, 3, 4, 5, 6, 7, 8, 9, 10), GuidRepresentation.Standard),
        ["Text"] = $"Position {position} von {n}",
        ["OrderNumber"] = position,
        ["Reference"] = $"IT-{(n * 31 + position * 7) % 10_000:D5}",
        ["ShippingUnit"] = (n * 7 + position) % 90 == 0 ? "Fass" : (n * 7 + position) % 6 == 0 ? "Kiste" : "Palette",
        ["Quantity"] = new BsonDocument("Value", 1 + (n + position) % 33),
        ["Weight"] = new BsonDocument("Value", 20.5 * (1 + (n + position) % 40)),
    };

    private static int Number(string variable, int otherwise) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : otherwise;
}
