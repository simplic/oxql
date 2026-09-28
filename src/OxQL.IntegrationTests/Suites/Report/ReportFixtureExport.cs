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
/// <c>strict-ambiguous.json</c>, <c>contract1-hint.json</c> and <c>export.json</c> (the files and
/// the flagged steps). The request files are the input and are not rewritten. Each answer is
/// <c>{ status, answer }</c> with the host's body; the files are indented with two spaces, LF, and
/// hold nothing that changes between runs of the same engine.
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

        files.Add(Write(directory, "export.json", new JsonObject
        {
            ["generatedBy"] = "OxQL.IntegrationTests Suites.Report.ReportFixtureExportTests (OXQL_EXPORT_FIXTURES)",
            ["scenarios"] = new JsonArray([.. ReportScenarios.Ids.Select(id => (JsonNode?)id)]),
            ["files"] = new JsonArray([.. files.Select(file => (JsonNode?)file)]),
            ["flagged"] = new JsonArray([.. flagged.Select(flag => (JsonNode?)flag)]),
        }));

        return new Result(files, flagged);
    }

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

    private static string Write(string directory, string relative, JsonObject content)
    {
        File.WriteAllText(Path.Combine(directory, relative), content.ToJsonString(Indented) + "\n");
        return relative;
    }
}
