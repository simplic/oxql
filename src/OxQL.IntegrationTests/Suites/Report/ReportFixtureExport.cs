using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The fixture export the studio's acceptance specs replay (PLAN §2, the fixture contract): every
/// report scenario's request sent to the fleet as the studio sends it, and the answers written as
/// the wire JSON the hosts answered. Opt-in: it runs only when <c>OXQL_EXPORT_FIXTURES</c> names
/// the fixtures directory (<c>.api/deep/oxql-studio/fixtures</c>), and reads the describe plan and
/// the scenario requests from there, so a re-export follows the studio's current plan.
/// <code>
/// OXQL_EXPORT_FIXTURES=&lt;abs&gt;/oxql-studio/fixtures dotnet test src/OxQL.IntegrationTests --filter "FullyQualifiedName~Suites.Report.ReportFixtureExportTests"
/// </code>
/// </summary>
[Trait("Category", "Integration")]
public class ReportFixtureExportTests(ITestOutputHelper output)
{
    [ExportFact]
    public async Task Export_the_report_scenario_fixtures()
    {
        var export = await ReportFixtureExport.WriteAsync(Environment.GetEnvironmentVariable(ReportFixtureExport.Variable)!);

        foreach (var file in export.Files)
            output.WriteLine($"wrote {file}");

        foreach (var flag in export.Flagged)
            output.WriteLine($"flagged {flag}");

        export.Files.Should().NotBeEmpty();
    }
}

/// <summary>A fact that runs only when <see cref="ReportFixtureExport.Variable"/> is set.</summary>
internal sealed class ExportFactAttribute : FactAttribute
{
    public ExportFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReportFixtureExport.Variable)))
            Skip = $"Opt-in: set {ReportFixtureExport.Variable} to the fixtures directory to export the studio's report fixtures.";
    }
}

/// <summary>
/// Writes the fixture files. Per scenario (<see cref="ReportScenarios.Ids"/>):
/// <c>scenarios/&lt;id&gt;.explain.json</c> (the request explained, and every describe-plan step's
/// envelope explained) and <c>scenarios/&lt;id&gt;.query.json</c>; beside them
/// <c>explain-invalid.json</c> (<c>valid: false</c>), <c>strict-missing.json</c>,
/// <c>strict-ambiguous.json</c>, <c>contract1-hint.json</c>, <c>schemas/&lt;service&gt;.json</c> (each
/// fleet service's schema document, <see cref="FleetSchemaDocument"/>), when the studio recorded
/// them <c>describe-envelopes.answers.json</c> (<see cref="WriteEnvelopeAnswersAsync"/>), and
/// <c>export.json</c> (the files and the flagged steps). The request files are the input and are
/// not rewritten. Each answer is <c>{ status, answer }</c> with the host's body; the files are
/// indented with two spaces (the envelope answers compact), LF, and hold nothing that changes
/// between runs of the same engine.
/// </summary>
internal static class ReportFixtureExport
{
    public const string Variable = "OXQL_EXPORT_FIXTURES";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record Result(IReadOnlyList<string> Files, IReadOnlyList<string> Flagged);

    /// <summary>
    /// A2b with a select path the delivery attempt lacks (<c>statuz</c>) in the lookup continued at
    /// transport: the origin checks the continued stage at the owner's internal explain, and the
    /// answer is <c>valid: false</c> with the owner's <c>UNKNOWN_PATH</c> mapped to the caller's stage
    /// (<c>params.owner</c>), the shapes of the part that binds, and no <c>bound</c>/<c>stages</c>.
    /// </summary>
    public static JsonObject InvalidRequest(JsonObject a2b)
    {
        var request = a2b.DeepClone().AsObject();
        request["pipeline"]![ReportScenarios.IndexOf(request, "lastAttempt")]!["lookup"]!["select"] = new JsonArray("dateTime", "statuz");
        return request;
    }

    public static async Task<Result> WriteAsync(string directory)
    {
        var files = new List<string>();
        var flagged = new List<string>();
        var plan = ReportScenarios.Plan(File.Exists(Path.Combine(directory, "describe-plan.json")) ? directory : null);
        var requests = ReportScenarios.Ids.ToDictionary(id => id, id => ReportScenarios.Request(id, File.Exists(Path.Combine(directory, "scenarios", $"{id}.request.json")) ? directory : null));

        Directory.CreateDirectory(Path.Combine(directory, "scenarios"));

        foreach (var id in ReportScenarios.Ids)
        {
            var request = requests[id];
            var client = await ReportScenarios.ClientAsync(request);
            var explained = await ReportExplain.ExplainAsync(client, request);
            var steps = new JsonArray();

            foreach (var step in ReportScenarios.Steps(plan, id))
            {
                var envelope = step["envelope"]!.AsObject();
                var answer = await ReportExplain.ExplainAsync(await ReportScenarios.ClientAsync(envelope["query"]!.AsObject()), envelope);
                var stepId = step["id"]!.GetValue<string>();

                foreach (var entry in (answer.Body?["describe"] as JsonArray ?? []).OfType<JsonObject>().Where(entry => entry["error"] is JsonObject))
                    flagged.Add($"{stepId} describe '{entry["id"]}' answered {entry["error"]!["code"]}");

                if (answer.Body?["valid"]?.GetValue<bool>() != true)
                    flagged.Add($"{stepId} answered valid:false ({string.Join(", ", answer.ErrorCodes)})");

                steps.Add(new JsonObject { ["id"] = stepId, ["status"] = answer.StatusCode, ["answer"] = answer.Body?.DeepClone() });
            }

            files.Add(Write(directory, $"scenarios/{id}.explain.json", new JsonObject
            {
                ["scenario"] = id,
                ["request"] = $"scenarios/{id}.request.json",
                ["explain"] = Answer(explained, 200),
                ["steps"] = steps,
            }));

            files.Add(Write(directory, $"scenarios/{id}.query.json", new JsonObject
            {
                ["scenario"] = id,
                ["request"] = $"scenarios/{id}.request.json",
                ["query"] = Answer(await client.QueryAsync(request), 200),
            }));
        }

        var a1 = requests["A1"];
        var ledger = await ReportScenarios.ClientAsync(a1);
        var invalid = InvalidRequest(requests["A2b"]);
        var missing = ReportScenarios.WithVariable(a1, "transactionId", ReportSeed.MissingSourceTransactionId);
        var ambiguous = ReportScenarios.WithVariable(a1, "transactionId", ReportSeed.AmbiguousSourceTransactionId);

        files.Add(Write(directory, "explain-invalid.json", Case(
            "A2b with a select path the delivery attempt lacks (statuz) in the lookup continued at transport: explain answers valid:false with the owner's UNKNOWN_PATH mapped to the caller's stage",
            invalid, contract: 2, Answer(await ReportExplain.ExplainAsync(ledger, invalid), 200))));
        files.Add(Write(directory, "strict-missing.json", Case(
            "A1 on the invoice whose ERP line points at a deleted source line: strict refuses with RESOLVE_MISSING (not_found)",
            missing, contract: 2, Answer(await ledger.QueryAsync(missing), 422))));
        files.Add(Write(directory, "strict-ambiguous.json", Case(
            "A1 on the invoice whose source line id is held by a shipment and a tour: strict refuses with RESOLVE_AMBIGUOUS",
            ambiguous, contract: 2, Answer(await ledger.QueryAsync(ambiguous), 422))));
        files.Add(Write(directory, "contract1-hint.json", Case(
            "A1 sent without the X-OxQL-Contract header, as a report data source pasting it would: read as contract 1, refused with the header hint",
            a1, contract: null, Answer(await (await ReportScenarios.ClientAsync(a1, contract: null)).QueryAsync(a1), null))));

        Directory.CreateDirectory(Path.Combine(directory, "schemas"));

        foreach (var service in OxQL.IntegrationTests.Fleet.LabService.All)
            files.Add(Write(directory, $"schemas/{service.Key}.json", FleetSchemaDocument.Of(service)));

        if (File.Exists(Path.Combine(directory, EnvelopesFile)))
            files.Add(await WriteEnvelopeAnswersAsync(directory, flagged));

        files.Add(Write(directory, "export.json", new JsonObject
        {
            ["generatedBy"] = "OxQL.IntegrationTests Suites.Report.ReportFixtureExportTests (OXQL_EXPORT_FIXTURES)",
            ["scenarios"] = new JsonArray([.. ReportScenarios.Ids.Select(id => (JsonNode?)id)]),
            ["files"] = new JsonArray([.. files.Select(file => (JsonNode?)file)]),
            ["flagged"] = new JsonArray([.. flagged.Select(flag => (JsonNode?)flag)]),
        }));

        return new Result(files, flagged);
    }

    /// <summary>The explain envelopes the studio recorded while the scenarios were built, an array.</summary>
    public const string EnvelopesFile = "describe-envelopes.json";

    /// <summary>
    /// Answers the recorded envelopes compactly (<c>describe-envelopes.answers.json</c>): the distinct
    /// queries (by canonical JSON) and the distinct describe entries, each explained once per distinct
    /// (query, entry) pair (the entries of one query sent together, at most
    /// <c>Explain.MaxDescribeRequests</c> per call), and each distinct describe answer kept once:
    /// <code>
    /// { source, counts, queries: [{ query, status, valid, errors }], entries: [entry],
    ///   answers: [answer without its id], pairs: [[query, entry, answer]] }
    /// </code>
    /// A consumer finds an envelope's query and entries by their canonical JSON (object members
    /// sorted by name, no whitespace) and reads each entry's answer through <c>pairs</c>, giving it the
    /// entry's id.
    /// </summary>
    public static async Task<string> WriteEnvelopeAnswersAsync(string directory, List<string> flagged)
    {
        var envelopes = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, EnvelopesFile)))!.AsArray();
        var queries = new List<JsonObject>();
        var queryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<JsonObject>();
        var entryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var wanted = new SortedDictionary<int, SortedSet<int>>();

        foreach (var envelope in envelopes.OfType<JsonObject>())
        {
            var query = envelope["query"]!.AsObject();
            var queryKey = CanonicalOf(query);

            if (!queryIndex.TryGetValue(queryKey, out var q))
            {
                queryIndex[queryKey] = q = queries.Count;
                queries.Add(query);
            }

            if (!wanted.TryGetValue(q, out var set))
                wanted[q] = set = [];

            foreach (var entry in (envelope["describe"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var entryKey = CanonicalOf(entry);

                if (!entryIndex.TryGetValue(entryKey, out var e))
                {
                    entryIndex[entryKey] = e = entries.Count;
                    entries.Add(entry);
                }

                set.Add(e);
            }
        }

        var answers = new List<JsonNode?>();
        var answerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairs = new JsonArray();
        var described = new JsonArray();
        var perCall = Math.Max(1, new OxQL.Core.Models.OxQLOptions().Explain.MaxDescribeRequests);

        foreach (var (q, wantedEntries) in wanted)
        {
            var query = queries[q];
            var client = await ReportScenarios.ClientAsync(query);
            int? status = null;
            bool? valid = null;
            JsonArray errors = [];
            var chunks = wantedEntries.Count == 0 ? [new List<int>()] : wantedEntries.Chunk(perCall).Select(chunk => chunk.ToList()).ToList();

            foreach (var chunk in chunks)
            {
                // Two distinct entries may share an id, so each call carries ids of its own.
                var sent = chunk.Select((e, position) => (Entry: e, Id: "e" + position)).ToList();
                var describe = new JsonArray();

                foreach (var (e, id) in sent)
                {
                    var copy = entries[e].DeepClone().AsObject();

                    copy["id"] = id;
                    describe.Add(copy);
                }

                var answer = await ReportExplain.ExplainAsync(client, new JsonObject { ["query"] = query.DeepClone(), ["describe"] = describe });

                status ??= answer.StatusCode;
                valid ??= answer.Body?["valid"]?.GetValue<bool>();

                if (errors.Count == 0 && answer.Body?["errors"] is JsonArray reported)
                    errors = (JsonArray)reported.DeepClone();

                var byId = (answer.Body?["describe"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(item => item["id"]!.GetValue<string>(), StringComparer.Ordinal);

                foreach (var (e, id) in sent)
                {
                    if (!byId.TryGetValue(id, out var one))
                    {
                        flagged.Add($"envelope query {q} entry {e} got no describe answer (HTTP {answer.StatusCode})");
                        continue;
                    }

                    var body = one.DeepClone().AsObject();

                    body.Remove("id");

                    if (body["error"] is JsonObject error)
                        flagged.Add($"envelope query {q} entry {e} answered {error["code"]}");

                    var bodyKey = CanonicalOf(body);

                    if (!answerIndex.TryGetValue(bodyKey, out var a))
                    {
                        answerIndex[bodyKey] = a = answers.Count;
                        answers.Add(body);
                    }

                    pairs.Add(new JsonArray(q, e, a));
                }
            }

            described.Add(new JsonObject { ["query"] = query.DeepClone(), ["status"] = status, ["valid"] = valid, ["errors"] = errors });
        }

        return Write(directory, "describe-envelopes.answers.json", new JsonObject
        {
            ["source"] = EnvelopesFile,
            ["counts"] = new JsonObject
            {
                ["envelopes"] = envelopes.Count,
                ["queries"] = queries.Count,
                ["entries"] = entries.Count,
                ["pairs"] = pairs.Count,
                ["answers"] = answers.Count,
            },
            ["queries"] = described,
            ["entries"] = new JsonArray([.. entries.Select(entry => (JsonNode?)entry.DeepClone())]),
            ["answers"] = new JsonArray([.. answers]),
            ["pairs"] = pairs,
        }, indented: false);
    }

    /// <summary>A node's canonical JSON: object members sorted by name at every depth, no whitespace.</summary>
    public static string CanonicalOf(JsonNode? node) => Sorted(node)?.ToJsonString() ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sorted)]),
        null => null,
        _ => node.DeepClone(),
    };

    private static JsonObject Case(string description, JsonObject request, int? contract, JsonObject answer) => new()
    {
        ["description"] = description,
        ["contract"] = contract,
        ["request"] = request.DeepClone(),
        ["status"] = answer["status"]!.DeepClone(),
        ["answer"] = answer["answer"]?.DeepClone(),
    };

    /// <summary>The status and the body; a status other than <paramref name="expected"/> (when given) fails the export.</summary>
    private static JsonObject Answer(WireAnswer answer, int? expected)
    {
        if (expected is { } status)
            answer.StatusCode.Should().Be(status, answer.ToString());
        else
            answer.StatusCode.Should().BeGreaterThanOrEqualTo(400, answer.ToString());

        return new JsonObject { ["status"] = answer.StatusCode, ["answer"] = answer.Body?.DeepClone() };
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Write(string directory, string relative, JsonObject content, bool indented = true)
    {
        File.WriteAllText(Path.Combine(directory, relative), content.ToJsonString(indented ? Indented : Compact) + "\n");
        return relative;
    }
}
