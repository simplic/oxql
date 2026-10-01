using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Report;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// T2, the golden explain answers (improvement plan §3.T): the reference set explained over the fleet
/// exactly as the studio calls explain, normalised (<see cref="ExplainGolden.Normalise"/>) and compared
/// with the answer recorded in <c>Suites/Explain/Golden/&lt;id&gt;.json</c>. The reference set is the
/// report scenarios A1–A5 (A2b included) and fleet queries shaped like EXAMPLE-CASE. The files are the
/// engine–studio contract: the studio's parser and replay cases read copies of them (synced later).
/// <para>
/// To re-record after an intended change of the answer, run the suite with <c>OXQL_RECORD_GOLDEN=1</c>;
/// each case then writes its file instead of comparing, and the diff of the files is the change to
/// review and explain.
/// <code>
/// OXQL_RECORD_GOLDEN=1 dotnet test src/OxQL.IntegrationTests --filter "FullyQualifiedName~Suites.Explain.ExplainGoldenTests"
/// </code>
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ExplainGoldenTests
{
    public static TheoryData<string> Ids => new(ExplainGolden.Cases.Keys);

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task The_explain_answer_equals_its_golden_answer(string id)
    {
        var request = ExplainGolden.Cases[id]();
        var client = await ReportScenarios.ClientAsync(request);
        var answer = await client.ExplainHereAsync(request);
        var recorded = ExplainGolden.Record(request, answer);
        var path = ExplainGolden.PathOf(id);

        if (ExplainGolden.Recording)
        {
            await File.WriteAllTextAsync(path, recorded);
            return;
        }

        File.Exists(path).Should().BeTrue($"'{id}' has a golden answer; record it with {ExplainGolden.RecordVariable}=1");
        // A checkout that turns LF into CRLF changes no answer.
        var golden = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal);

        recorded.Should().Be(golden, $"the explain answer of '{id}' is its golden answer; an intended change is re-recorded with {ExplainGolden.RecordVariable}=1 and explained");
    }

    [Fact]
    public void Every_golden_file_belongs_to_a_case()
    {
        var files = Directory.EnumerateFiles(ExplainGolden.Directory(), "*.json").Select(Path.GetFileNameWithoutExtension).ToList();

        files.Should().BeEquivalentTo(ExplainGolden.Cases.Keys, "a golden answer without a case is never checked");
    }
}

/// <summary>The golden explain answers: the cases, the normalisation and the file form.</summary>
internal static class ExplainGolden
{
    public const string RecordVariable = "OXQL_RECORD_GOLDEN";

    /// <summary>Whether this run records the answers instead of comparing them.</summary>
    public static bool Recording => Environment.GetEnvironmentVariable(RecordVariable) is "1" or "true";

    /// <summary>
    /// The members that change between runs of one engine or with nothing the answer is about: etags,
    /// times, revisions, and what an owner cost (<c>calls</c>, <c>cached</c>), which depends on whether
    /// the forward cache already held its answer; the engine's version goes with them.
    /// </summary>
    private static readonly HashSet<string> Volatile = new(StringComparer.Ordinal) { "etag", "ms", "revision", "calls", "cached" };

    /// <summary>The reference set, by id; each builds its request (contract 2, organisation R).</summary>
    public static readonly IReadOnlyDictionary<string, Func<JsonObject>> Cases = new SortedDictionary<string, Func<JsonObject>>(StringComparer.Ordinal)
    {
        ["A1"] = () => ReportScenarios.Request("A1"),
        ["A2"] = () => ReportScenarios.Request("A2"),
        ["A2b"] = () => ReportScenarios.Request("A2b"),
        ["A3"] = () => ReportScenarios.Request("A3"),
        ["A4"] = () => ReportScenarios.Request("A4"),
        ["A5"] = () => ReportScenarios.Request("A5"),
        ["EX1-source-chain"] = () => JsonNode.Parse(ExampleChain)!.AsObject(),
        ["EX2-continued-refused"] = () => JsonNode.Parse(ContinuedRefused)!.AsObject(),
    }.AsReadOnly();

    /// <summary>
    /// EXAMPLE-CASE steps 1–7 on the fleet: the invoice by variable, one row per line, billing lines
    /// only, the ERP line and its source line with the owning shipment or tour, reported when missing;
    /// for shipment lines the delivering tour and its vehicle (a third service, continued), for tour
    /// lines the tour's vehicle, and the latest delivery attempt of a shipment.
    /// </summary>
    private static string ExampleChain => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{ReportSeed.TransactionId:D}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine", "onMissing": "report",
                           "select": ["id", "text", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "onMissing": "report",
                           "select": ["id", "totalPrice"], "parentAs": "sourceParent", "parentSelect": ["id", "shipmentNumber", "number"] } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first",
                           "select": ["id", "number", "resource.id"] } },
            { "resolve": { "path": "deliveringTour.resource.id", "as": "shipmentVehicle", "target": "fleet.vehicle", "onMissing": "report",
                           "select": ["matchCode", "registrationPlate.registrationIdentifier"] } },
            { "resolve": { "path": "sourceParent.resource.id", "as": "tourVehicle", "forTarget": "transport.tour", "target": "fleet.vehicle", "onMissing": "report",
                           "select": ["matchCode", "registrationPlate.registrationIdentifier"] } },
            { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "sourceParent", "forTarget": "{{ReportSeed.Shipment}}",
                          "as": "lastAttempt", "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["dateTime", "status"] } },
            { "project": { "position": 1, "item.text": 1, "erpLine": 1, "sourceLine": 1, "sourceParent": 1, "deliveringTour": 1,
                           "shipmentVehicle": 1, "tourVehicle": 1, "lastAttempt": 1 } },
            { "sort": [ { "position": "asc" } ] }
          ]
        }
        """;

    /// <summary>A stage continued under a union alias without forTarget that one target cannot bind (R3 F2): valid false, the stage's own error only.</summary>
    private static string ContinuedRefused => $$"""
        {
          "entityType": "ledger.transaction",
          "variables": { "transactionId": "{{ReportSeed.TransactionId:D}}" },
          "pipeline": [
            { "match": { "id": { "eq": { "$var": "transactionId" } } } },
            { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
            { "match": { "item": { "is": "BillingLineTransactionItem" } } },
            { "resolve": { "path": "item.billingLineId", "as": "erpLine", "select": ["id", "sourceBillingLineReference.type", "sourceBillingLineReference.id"] } },
            { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine",
                           "parentAs": "sourceParent", "select": ["id"], "parentSelect": ["id", "shipmentNumber", "number"] } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first", "select": ["id", "number"] } },
            { "project": { "position": 1, "sourceLine": 1, "sourceParent": 1, "deliveringTour": 1 } }
          ]
        }
        """;

    /// <summary>The directory the golden files live in, beside this file.</summary>
    public static string Directory([CallerFilePath] string source = "") => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "Golden");

    public static string PathOf(string id) => System.IO.Path.Combine(Directory(), $"{id}.json");

    /// <summary>A case's file: the request, the status and the normalised answer, indented, LF, ending in a newline.</summary>
    public static string Record(JsonObject request, WireAnswer answer)
    {
        var record = new JsonObject
        {
            ["request"] = request.DeepClone(),
            ["status"] = answer.StatusCode,
            ["answer"] = answer.Body is null ? null : Normalise(answer.Body.DeepClone()),
        };

        return Pretty(record) + "\n";
    }

    private static readonly JsonSerializerOptions OneLine = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The members whose entries are written one per line: a diff of two recordings then shows the rows that changed, not their brackets.</summary>
    private static readonly HashSet<string> RowLists = new(StringComparer.Ordinal) { "members", "columns", "outcomes", "onlyFor", "errors", "diagnostics", "notes" };

    /// <summary>
    /// The file form: indented with two spaces, an array of plain values on one line, and each member
    /// row of a type (and each column, outcome, error, diagnostic and note) on a line of its own.
    /// </summary>
    public static string Pretty(JsonNode node)
    {
        var text = new System.Text.StringBuilder();

        Write(text, node, 0, null);

        return text.ToString();
    }

    private static void Write(System.Text.StringBuilder text, JsonNode? node, int depth, string? name)
    {
        switch (node)
        {
            case JsonObject members when members.Count > 0:
                text.Append("{\n");

                var position = 0;

                foreach (var (key, value) in members)
                {
                    text.Append(' ', (depth + 1) * 2).Append(JsonSerializer.Serialize(key, OneLine)).Append(": ");
                    Write(text, value, depth + 1, key);
                    text.Append(++position < members.Count ? ",\n" : "\n");
                }

                text.Append(' ', depth * 2).Append('}');
                break;

            case JsonArray items when items.Count > 0 && items.Any(item => item is JsonObject or JsonArray):
                text.Append("[\n");

                for (var index = 0; index < items.Count; index++)
                {
                    text.Append(' ', (depth + 1) * 2);

                    if (name is not null && RowLists.Contains(name))
                        text.Append(items[index]?.ToJsonString(OneLine) ?? "null");
                    else
                        Write(text, items[index], depth + 1, null);

                    text.Append(index + 1 < items.Count ? ",\n" : "\n");
                }

                text.Append(' ', depth * 2).Append(']');
                break;

            default:
                text.Append(node?.ToJsonString(OneLine) ?? "null");
                break;
        }
    }

    /// <summary>
    /// The answer without what changes between runs of one engine, or with nothing the answer is
    /// about: every <c>etag</c>, <c>ms</c>, <c>revision</c>, <c>calls</c> and <c>cached</c> member, any
    /// member ending in <c>Ms</c> that holds a number (a time), and the engine's version.
    /// </summary>
    public static JsonNode Normalise(JsonNode answer)
    {
        Strip(answer);

        return answer;
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (name, value) in members.ToList())
                {
                    if (Volatile.Contains(name) || (name.EndsWith("Ms", StringComparison.Ordinal) && value is JsonValue))
                        members.Remove(name);
                    else
                        Strip(value);

                    // The engine's version, this host's and every owner's: the answers do not change with it.
                    if (name == "engine" && value is JsonObject engine)
                        engine.Remove("version");
                }
                break;

            case JsonArray items:
                foreach (var item in items)
                    Strip(item);
                break;
        }
    }
}
