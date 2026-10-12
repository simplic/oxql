using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using OxQL.AspNetCore.Compat;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Tests.Bind;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.Tests.Cursor;

/// <summary>
/// The cursor-stability rule: a cursor is bound to its query by a fingerprint over the
/// canonical render of the bound pipeline, so a request must render and fingerprint byte-identically
/// under every later engine, and every cursor minted must still decode. The goldens under
/// <c>Fixtures/golden/</c> pin both.
/// <para>
/// The contract 1 golden was captured on the 2.0 engine and is never regenerated: contract 1 keeps
/// its render, its fingerprint and its cursors. The contract 2 goldens were re-recorded once, at the
/// wire freeze of the unpublished contract 2: its page lost <c>offset</c> and
/// <c>includeTotalCount</c>, and its cursor is fingerprinted over what decides the rows and
/// carries the rows served, so no cursor minted before it verifies, by intent. From here a
/// change that alters one is a cursor break again. <c>OXQL_RECORD_GOLDEN=1</c> rewrites the contract 2
/// goldens in the source tree; the diff is reviewed and explained.
/// </para>
/// </summary>
public class GoldenRenderTests(ITestOutputHelper output)
{
    private const string CursorFile = "cursors-2.0.json";

    /// <summary>The golden of a remote resolve filter holding a <c>$var</c>, rendered substituted.</summary>
    private const string RemoteFilterVariable = "remote-filter-var";

    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden");

    /// <summary>Whether this run records the contract 2 goldens instead of comparing them.</summary>
    private static bool Recording => Environment.GetEnvironmentVariable("OXQL_RECORD_GOLDEN") is "1" or "true";

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The golden directory in the source tree, which a recording run writes to.</summary>
    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(file)!)!, "Fixtures", "golden");

    private static void Record(string name, JsonObject golden)
    {
        var text = golden.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";

        File.WriteAllText(Path.Combine(SourceDirectory(), name + ".json"), text);
        File.WriteAllText(Path.Combine(Directory, name + ".json"), text);
    }

    private static readonly CompatBinder Compat = new(BindHost.Probe);

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();

        foreach (var name in CaseNames())
            data.Add(name);

        return data;
    }

    public static TheoryData<string> CursorCases()
    {
        var data = new TheoryData<string>();

        foreach (var entry in CursorEntries())
            data.Add(entry["case"]!.GetValue<string>());

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_render_is_byte_identical(string name)
    {
        var golden = Load(name);
        var bound = await BoundAsync(golden, golden["pipeline"]!.AsArray());

        // Only a contract 2 golden is ever recorded; the contract 1 golden stays what 2.0 rendered.
        if (Recording && (golden["contract"]?.GetValue<int>() ?? 2) == 2)
        {
            golden["fingerprint"] = bound.Fingerprint;
            golden["canonical"] = bound.Canonical;
            Record(name, golden);
        }

        bound.Canonical.Should().Be(golden["canonical"]!.GetValue<string>(), $"the canonical render of '{name}' is the recorded fingerprint input");
        bound.Fingerprint.Should().Be(golden["fingerprint"]!.GetValue<string>(), $"a cursor over '{name}' must keep verifying");
    }

    [Theory]
    [MemberData(nameof(CursorCases))]
    public async Task A_recorded_cursor_decodes_and_pages_its_query(string name)
    {
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(Directory, CursorFile)))!.AsObject();
        var entry = file["cursors"]!.AsArray().Select(candidate => candidate!.AsObject()).Single(candidate => candidate["case"]!.GetValue<string>() == name);
        var golden = Load(name);

        // A contract 2 cursor is minted anew over the case's own fingerprint, with the values the
        // entry names and five rows served; the contract 1 cursor is the one 2.0 minted.
        if (Recording && (golden["contract"]?.GetValue<int>() ?? 2) == 2)
        {
            var fingerprint = (await BoundAsync(golden, golden["pipeline"]!.AsArray())).Fingerprint;

            entry["cursor"] = BindHost.Cursors.Encode(new CursorPayload(
                fingerprint,
                entry["mode"]!.GetValue<string>() == "keyset" ? PagingMode.Keyset : PagingMode.Offset,
                entry["fields"]!.AsArray().Select(field => new CursorValue(field!["path"]!.GetValue<string>(), field["direction"]!.GetValue<string>() == "asc", BsonDocument.Parse("{ v: " + field["value"]!.GetValue<string>() + " }")["v"])).ToList(),
                entry["offset"]!.GetValue<int>()) { Served = 5 });
            entry["served"] = 5;
            Record(Path.GetFileNameWithoutExtension(CursorFile), file);
        }

        var cursor = entry["cursor"]!.GetValue<string>();

        var payload = BindHost.Cursors.Decode(cursor, golden["fingerprint"]!.GetValue<string>());

        payload.Should().NotBeNull("a recorded cursor must still verify against the golden fingerprint");
        payload!.Served.Should().Be(entry["served"]?.GetValue<long>() ?? 0, "a contract 2 cursor carries the rows served; a contract 1 cursor none");
        payload!.Mode.Should().Be(entry["mode"]!.GetValue<string>() == "keyset" ? PagingMode.Keyset : PagingMode.Offset);
        payload.Offset.Should().Be(entry["offset"]!.GetValue<int>());
        payload.Fields.Select(field => (field.Wire, field.Ascending, field.Value.ToJson(Canonical))).Should().Equal(
            entry["fields"]!.AsArray().Select(field => (
                field!["path"]!.GetValue<string>(),
                field["direction"]!.GetValue<string>() == "asc",
                field["value"]!.GetValue<string>())));

        // The same cursor, sent back on the case's own query, binds: the fingerprint the
        // engine computes today is the one the cursor was minted under.
        var pipeline = new JsonArray(golden["pipeline"]!.AsArray()
            .Where(stage => stage!["page"] is null)
            .Select(stage => stage!.DeepClone())
            .Append(new JsonObject { ["page"] = new JsonObject { ["cursor"] = cursor } })
            .ToArray());
        var bound = await BoundAsync(golden, pipeline);

        bound.Page.Cursor.Should().NotBeNull();
    }

    [Fact]
    public async Task The_corpus_covers_every_stage_kind_and_leaves_out_remote_filters_with_variables()
    {
        var names = CaseNames();
        var stages = new List<BoundStage>();
        var semiJoin = false;
        var defaultedLookupLimit = false;

        foreach (var name in names)
        {
            var golden = Load(name);
            var bound = await BoundAsync(golden, golden["pipeline"]!.AsArray());

            stages.AddRange(bound.Stages);
            semiJoin |= bound.HasSemiJoin;
            defaultedLookupLimit |= golden["pipeline"]!.AsArray().Any(stage => stage!["lookup"] is JsonObject lookup && lookup["limit"] is null);

            // The one documented exception: a remote filter with a $var renders
            // substituted, which its own golden pins; every other golden is a 2.0 render.
            if (name == RemoteFilterVariable)
                continue;

            foreach (var resolve in golden["pipeline"]!.AsArray().Select(stage => stage!["resolve"]).OfType<JsonObject>())
                resolve["filter"]?.ToJsonString().Should().NotContain("$var", "only remote-filter-var.json renders a remote filter with a variable");
        }

        output.WriteLine($"golden corpus: {names.Count} renders, {CursorEntries().Count} cursors");

        stages.Select(stage => stage.GetType()).Distinct().Should().Contain(
        [
            typeof(BoundStage.Match), typeof(BoundStage.Lookup), typeof(BoundStage.Resolve),
            typeof(BoundStage.Unwind), typeof(BoundStage.Group), typeof(BoundStage.Project), typeof(BoundStage.Sort), typeof(BoundStage.Page),
        ]);
        stages.OfType<BoundStage.Resolve>().Should().Contain(resolve => resolve.IsRemote).And.Contain(resolve => !resolve.IsRemote);
        semiJoin.Should().BeTrue("a semi-join leaf is in the corpus");
        names.Should().Contain(RemoteFilterVariable, "the substituted render of a remote filter variable is pinned");
        Load(RemoteFilterVariable)["canonical"]!.GetValue<string>().Should().Be(Load("resolve-remote")["canonical"]!.GetValue<string>(), "a substituted variable renders as the value written literally");
        defaultedLookupLimit.Should().BeTrue("a lookup whose limit is the bound default is in the corpus");
    }

    private static List<string> CaseNames() =>
        System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.StartsWith("cursors-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList()!;

    private static List<JsonObject> CursorEntries() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Directory, CursorFile)))!["cursors"]!.AsArray().Select(entry => entry!.AsObject()).ToList();

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Directory, name + ".json")))!.AsObject();

    /// <summary>Binds a golden's request as the host would: a contract 1 request through the compat rewrite first.</summary>
    private static async Task<BoundPipeline> BoundAsync(JsonObject golden, JsonArray pipeline)
    {
        var contract = golden["contract"]?.GetValue<int>() ?? 2;
        var request = new JsonObject
        {
            ["entityType"] = golden["entity"]!.GetValue<string>(),
            ["pipeline"] = pipeline.DeepClone(),
        };

        if (golden["variables"] is { } variables)
            request["variables"] = variables.DeepClone();

        var parsed = BindHost.Parse(request.ToJsonString());

        if (contract == 1)
            parsed = Compat.Rewrite(parsed).Request;

        var outcome = await BindHost.BindAsync(BindHost.Probe, parsed, BindHost.Context(contract: contract));

        outcome.Should().BeOfType<BindOutcome.Bound>($"the golden request should bind; got: {BindHost.Describe(outcome)}");

        return ((BindOutcome.Bound)outcome).Pipeline;
    }
}
