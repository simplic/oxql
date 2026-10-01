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
/// </para>
/// <para>
/// Beside the answers lies what a client needs to rebuild their types itself
/// (<c>Golden/mirror</c>, recorded and compared the same way): the fleet's schema documents
/// (<c>schema/&lt;service&gt;.json</c>), the answer with the types written out of a few cases
/// (<c>types/&lt;id&gt;.json</c>; documents + default answer == this), and one answer of hosts that
/// publish their documents' revisions, the revisions replaced by fixed ones (<c>revision/&lt;id&gt;.json</c>).
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

        answer.Body?["valid"]?.GetValue<bool>().Should().Be(id != "EX2-continued-refused", answer.Text);

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

        var mirrored = Directory.EnumerateFiles(ExplainGolden.MirrorPath(), "*.json", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(ExplainGolden.MirrorPath(), file).Replace(Path.DirectorySeparatorChar, '/')).ToList();

        mirrored.Should().BeEquivalentTo(
            [
                .. LabService.All.Select(service => $"schema/{service.Key}.json"),
                .. ExplainGolden.Tabled.Select(id => $"types/{id}.json"),
                $"revision/{ExplainGolden.Revisions}.json",
            ],
            "a mirror file without a case is never checked");
    }

    public static TheoryData<string> Services => new(LabService.All.Select(service => service.Key));

    /// <summary>The documents a client reads the types of the golden answers from: what each service of the fleet publishes.</summary>
    [Theory]
    [MemberData(nameof(Services))]
    public async Task The_schema_document_of_a_service_equals_its_recorded_copy(string key)
    {
        var document = FleetSchemaDocument.Of(LabService.All.Single(service => service.Key == key));

        await ExplainGolden.CompareAsync(ExplainGolden.MirrorPath("schema", key), ExplainGolden.Pretty(document) + "\n", $"the schema document of '{key}'");
    }

    public static TheoryData<string> TabledIds => new(ExplainGolden.Tabled);

    /// <summary>
    /// The same request with <c>include: "types"</c>: the answer a client rebuilds from the schema
    /// documents and the golden answer (<see cref="ExplainByReferenceTests"/> proves it here).
    /// </summary>
    [Theory]
    [MemberData(nameof(TabledIds))]
    public async Task The_answer_with_the_types_written_out_equals_its_recorded_copy(string id)
    {
        var query = ExplainGolden.Cases[id]();
        var request = new JsonObject { ["query"] = query.DeepClone(), ["include"] = new JsonArray("shape", "notes", "types") };
        var client = await ReportScenarios.ClientAsync(query);
        var answer = await client.ExplainHereAsync(request);

        answer.Body?["flagSets"].Should().NotBeNull(answer.Text);

        await ExplainGolden.CompareAsync(ExplainGolden.MirrorPath("types", id), ExplainGolden.Record(request, answer), $"the answer of '{id}' with the types written out");
    }

    /// <summary>
    /// The golden hosts publish no revision, so the golden answers carry none. This one is answered by
    /// hosts that do (in process, as a host of the base package), and keeps <c>revision</c> and each
    /// type's <c>schemaRevision</c> with every service's revision replaced by a fixed one
    /// (<see cref="ExplainGolden.FixedRevision"/>): the shape a client checks its documents against.
    /// </summary>
    [Fact]
    public async Task The_answer_of_hosts_that_publish_revisions_equals_its_recorded_copy()
    {
        var request = ExplainGolden.RevisionsRequest();
        var fleet = new ExplainShapeFleetTests.InProcessFleet();
        var answer = JsonSerializer.SerializeToNode(await fleet.ExplainAsync(JsonSerializer.Deserialize<OxQL.Core.Engine.ExplainRequest>(request.ToJsonString(), OxQL.Core.Models.OxQLJson.Wire)!), OxQL.Core.Models.OxQLJson.Wire)!.AsObject();
        var real = answer["revision"]!["schema"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>());

        real.Keys.Should().BeEquivalentTo(["fleet", "ledger", "transport"], "this host, its owner, and the owner that one asked");

        foreach (var (service, revision) in real)
            revision.Should().Be(FleetSchemaDocument.Of(LabService.All.Single(each => each.Key == service))["revision"]!.GetValue<string>(), $"the answer names the revision of {service}'s document");

        var record = new JsonObject { ["request"] = request, ["status"] = 200, ["answer"] = ExplainGolden.WithFixedRevisions(answer, real) };

        await ExplainGolden.CompareAsync(ExplainGolden.MirrorPath("revision", ExplainGolden.Revisions), ExplainGolden.Pretty(record) + "\n", "the answer of hosts that publish revisions");
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
        // The same three without any select: what a studio writes once the joins infer their loads (§3.S).
        ["A4-no-select"] = () => ReportScenarios.WithoutSelects(ReportScenarios.Request("A4")),
        ["A5-no-select"] = () => ReportScenarios.WithoutSelects(ReportScenarios.Request("A5")),
        ["EX1-no-select"] = () => ReportScenarios.WithoutSelects(JsonNode.Parse(ExampleChain)!.AsObject()),
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
                           "select": ["id", "totalPrice"], "parentAs": "sourceParent" } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{ReportSeed.Shipment}}", "elements": "first",
                           "select": ["id", "number", "resource.id"] } },
            { "resolve": { "path": "deliveringTour.resource.id", "as": "shipmentVehicle", "target": "fleet.vehicle", "onMissing": "report",
                           "select": ["matchCode", "registrationPlate.registrationIdentifier"] } },
            { "resolve": { "path": "sourceParent.resource.id", "as": "tourVehicle", "forTarget": "transport.tour", "target": "fleet.vehicle", "onMissing": "report",
                           "select": ["matchCode", "registrationPlate.registrationIdentifier"] } },
            { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "on": "sourceParent", "forTarget": "{{ReportSeed.Shipment}}",
                          "as": "lastAttempt", "first": true, "sort": [ { "dateTime": "desc" } ], "select": ["dateTime", "status"] } },
            { "project": { "position": 1, "item.text": 1, "erpLine": 1, "sourceLine": 1,
                           "sourceParent.id": 1, "sourceParent.shipmentNumber": 1, "sourceParent.number": 1, "deliveringTour": 1,
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
                           "parentAs": "sourceParent", "select": ["id"] } },
            { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "elements": "first", "select": ["id", "number"] } },
            { "project": { "position": 1, "sourceLine": 1, "sourceParent.id": 1, "sourceParent.shipmentNumber": 1, "sourceParent.number": 1, "deliveringTour": 1 } }
          ]
        }
        """;

    /// <summary>The cases whose answer with the types written out is recorded: a union without continued stages, the largest, and the example chain.</summary>
    public static readonly IReadOnlyList<string> Tabled = ["A1", "A5", "EX1-source-chain"];

    /// <summary>The id of the answer recorded with revisions.</summary>
    public const string Revisions = "EX1-revisions";

    /// <summary>The revision a recorded answer names for <paramref name="service"/> in place of its document's.</summary>
    public static string FixedRevision(string service) => "sha256:fixed-" + service;

    /// <summary>
    /// The example chain up to the vehicle of the delivering tour (three services, one of them reached
    /// through an owner), asked for the shapes only: small, and every revision an answer can name.
    /// </summary>
    public static JsonObject RevisionsRequest()
    {
        var query = JsonNode.Parse(ExampleChain)!.AsObject();
        var pipeline = query["pipeline"]!.AsArray();

        while (pipeline.Count > 7)
            pipeline.RemoveAt(pipeline.Count - 1);

        return new JsonObject { ["query"] = query, ["include"] = new JsonArray("shape") };
    }

    /// <summary>
    /// The answer normalised but for its revisions, each service's replaced by its fixed one: in
    /// <c>revision.schema</c> and in every type's <c>schemaRevision</c>.
    /// </summary>
    public static JsonNode WithFixedRevisions(JsonObject answer, IReadOnlyDictionary<string, string> real)
    {
        var revision = answer["revision"]!.DeepClone().AsObject();

        foreach (var service in real.Keys)
            revision["schema"]![service] = FixedRevision(service);

        var stable = Normalise(answer.DeepClone()).AsObject();
        var ordered = new JsonObject();

        // The revision where the answer has it: after the engine.
        foreach (var (name, value) in stable.ToList())
        {
            stable.Remove(name);
            ordered[name] = value;

            if (name == "engine")
                ordered["revision"] = revision;
        }

        foreach (var (_, type) in ordered["types"]!.AsObject())
            if (type is JsonObject named && named["schemaRevision"] is JsonValue written)
            {
                var service = named["service"]!.GetValue<string>();

                written.GetValue<string>().Should().Be(real[service], "a type names its service's revision");
                named["schemaRevision"] = FixedRevision(service);
            }

        return ordered;
    }

    /// <summary>The directory of what a client rebuilds the types from, or a file of it.</summary>
    public static string MirrorPath(string? kind = null, string? id = null) =>
        kind is null ? System.IO.Path.Combine(Directory(), "mirror") : System.IO.Path.Combine(Directory(), "mirror", kind, $"{id}.json");

    /// <summary>Writes <paramref name="recorded"/> when recording, else asserts it is the file's content.</summary>
    public static async Task CompareAsync(string path, string recorded, string what)
    {
        if (Recording)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, recorded);
            return;
        }

        File.Exists(path).Should().BeTrue($"{what} is recorded; record it with {RecordVariable}=1");

        // A checkout that turns LF into CRLF changes nothing.
        var golden = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal);

        recorded.Should().Be(golden, $"{what} is its recorded copy; an intended change is re-recorded with {RecordVariable}=1 and explained");
    }

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
    private static readonly HashSet<string> RowLists = new(StringComparer.Ordinal) { "members", "columns", "outcomes", "onlyFor", "errors", "diagnostics", "notes", "reads" };

    /// <summary>
    /// The file form: indented with two spaces, an array of plain values on one line, and each member
    /// row of a type (and each column, outcome, error, diagnostic, note and read) on a line of its own.
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
