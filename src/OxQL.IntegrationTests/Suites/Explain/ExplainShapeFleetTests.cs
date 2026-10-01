using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Report;
using OxQL.Model;
using OxQL.Mongo;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// The explain shape on the fleet (improvement plan §3.E), with the fleet's engines explaining for one
/// another in process as the internal explain route would. Every prefix of every report scenario — what
/// a studio explains while a user builds A1–A5 stage by stage — binds, is complete, and gives every root
/// of every stage a type, the owners' included; and the answer of the reference query stays within its
/// budgets (T4). Nothing is read from a database: explain never executes.
/// </summary>
[Trait("Category", "Integration")]
[Collection(TimedCollection.Name)]
public class ExplainShapeFleetTests(ITestOutputHelper output)
{
    /// <summary>T4: the size budget of the reference query's answer, raw and gzipped, and its warm server time.</summary>
    private const int MaxRawBytes = 60_000;
    private const int MaxGzipBytes = 10_000;
    private const double MaxWarmMs = 10;

    public static TheoryData<string, int> Prefixes()
    {
        var prefixes = new TheoryData<string, int>();

        foreach (var id in ReportScenarios.Ids)
            for (var stages = 0; stages <= ReportScenarios.Request(id)["pipeline"]!.AsArray().Count; stages++)
                prefixes.Add(id, stages);

        return prefixes;
    }

    internal static ExplainRequest Prefix(string id, int stages)
    {
        var request = ReportScenarios.Request(id);
        var pipeline = request["pipeline"]!.AsArray();

        while (pipeline.Count > stages)
            pipeline.RemoveAt(pipeline.Count - 1);

        return JsonSerializer.Deserialize<ExplainRequest>(request.ToJsonString(), OxQLJson.Wire)!;
    }

    /// <summary>The request asking for the member rows too: what a consumer without schema documents sends.</summary>
    internal static ExplainRequest WithTypes(ExplainRequest request) =>
        request with { Include = [ExplainRequest.IncludeShape, ExplainRequest.IncludeNotes, ExplainRequest.IncludeTypes], IsEnvelope = true };

    private static string Errors(ExplainResult answer) => string.Join("; ", answer.Errors.Select(error => $"{error.Code}@{error.Stage} {error.Path}: {error.Message}"));

    [Theory]
    [MemberData(nameof(Prefixes))]
    public async Task X21_every_prefix_of_every_scenario_binds_is_complete_and_types_every_root_of_every_stage(string id, int stages)
    {
        var answer = await new InProcessFleet().ExplainAsync(WithTypes(Prefix(id, stages)));

        answer.Valid.Should().BeTrue(Errors(answer));
        answer.Cache.Complete.Should().BeTrue(string.Join("; ", answer.Notes.Where(note => note.Code is Notes.RemoteUnchecked or Notes.ExplainLimit or Notes.ExplainTrimmed).Select(note => note.Message)));
        answer.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked || note.Code == Notes.ExplainLimit, "every owner answers in process, within the limits of one explain");
        answer.Stages.Should().HaveCount(stages);
        answer.Entry!.Shape.Roots.Select(pair => pair.Key).Should().Equal("");

        foreach (var stage in answer.Stages)
        {
            foreach (var (root, type) in stage.Shape!.Roots)
            {
                type.Should().NotBeNull($"{id} stage {stage.Index}: '{root}' has a type");

                var pointer = type!.GetValue<string>();

                if (!pointer.StartsWith("k:", StringComparison.Ordinal))
                    answer.Types.ContainsKey(pointer).Should().BeTrue($"{id} stage {stage.Index}: '{root}' points to '{pointer}'");
            }

            foreach (var (_, overrides) in stage.Shape.Flags!)
                answer.FlagSets!.ContainsKey(overrides!.GetValue<string>()).Should().BeTrue();

            foreach (var (_, rule) in stage.Shape.Rules)
                answer.Rules.ContainsKey(rule!.GetValue<string>()).Should().BeTrue();

            foreach (var alias in stage.Creates)
                answer.Aliases[alias]!["complete"]!.GetValue<bool>().Should().BeTrue($"{id}: '{alias}' is complete");
        }

        // Every pointer of the table resolves within the answer.
        foreach (var (key, type) in answer.Types)
        {
            foreach (var target in type!["of"]?.AsArray() ?? [])
                answer.Types.ContainsKey(target!.GetValue<string>()).Should().BeTrue($"{key} is a union of types the table holds");

            if (key.StartsWith("t:", StringComparison.Ordinal))
                foreach (var row in type["members"]!.AsArray().Select(row => row!.AsArray()))
                    answer.FlagSets!.ContainsKey(row[ExplainTypes.Row.Flags]!.GetValue<string>()).Should().BeTrue($"{key}.{row[0]} points to a flag set of the answer");
        }

        answer.Owners.Sum(owner => owner["calls"]!.GetValue<int>()).Should().BeLessThanOrEqualTo(8, "one explain causes at most eight owner calls, transitive ones included");
    }

    [Fact]
    public async Task X22_a_remote_union_alias_is_one_union_of_its_owners_types_and_its_members_are_this_hosts_to_project_only()
    {
        var answer = await new InProcessFleet().ExplainAsync(WithTypes(Prefix("A1", 5)));

        answer.Valid.Should().BeTrue(Errors(answer));
        answer.Aliases["sourceParent"]!["node"]!.GetValue<string>().Should().Be("remote");
        answer.Aliases["sourceParent"]!["entities"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Contain(["transport.shipment", "transport.tour"]);
        answer.Aliases["sourceParent"]!["type"]!.GetValue<string>().Should().Be("u:sourceParent");
        answer.Aliases["sourceParent"]!["parentOf"]!.GetValue<string>().Should().Be("sourceLine");

        var union = answer.Types["u:sourceParent"]!;
        var of = union["of"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();

        of.Should().Contain(["t:transport.shipment", "t:transport.tour"]);

        var shipmentNumber = union["members"]!.AsArray().Select(row => row!.AsArray()).Single(row => row[0]!.GetValue<string>() == "shipmentNumber");

        shipmentNumber[1]!.AsArray().Select(index => of[index!.GetValue<int>()]).Should().Equal(["t:transport.shipment"], "only the shipment has a shipment number");

        // Under the alias nothing sorts or unwinds: the owners' rows are joined after the page here.
        foreach (var row in union["members"]!.AsArray())
        {
            var path = row![0]!.GetValue<string>();

            FlagsAt(answer, 4, "sourceParent", path).Should().Match<JsonObject>(flags => !flags["sortable"]!.GetValue<bool>() && !flags["unwindable"]!.GetValue<bool>(), path);
        }
    }

    [Fact]
    public async Task D01_the_flatten_a_ledgers_items_advertise_is_items_with_every_candidate_listed()
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>("""{ "query": { "entityType": "ledger.transaction", "pipeline": [] } }""", OxQLJson.Wire)!;

        var answer = await new InProcessFleet().ExplainAsync(WithTypes(request));
        var items = answer.Types["t:ledger.transaction"]!["members"]!.AsArray().Select(row => row!.AsArray()).Single(row => row[0]!.GetValue<string>() == "items");
        var more = items[ExplainTypes.Row.More]!;

        more["flatten"]!.GetValue<string>().Should().Be("items", "A1 flattens the groups' own items");
        more["flattenMembers"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal("items", "assignedTransactionItems");
        answer.FlagSets![items[ExplainTypes.Row.Flags]!.GetValue<string>()]!["unwindable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task D02_a_collection_below_a_remote_alias_keeps_its_depth_and_is_followed_element_wise()
    {
        var answer = await new InProcessFleet().ExplainAsync(WithTypes(Prefix("A4", ReportScenarios.IndexOf(ReportScenarios.Request("A4"), "lineShipment") + 1)));

        answer.Valid.Should().BeTrue(Errors(answer));

        var stage = answer.Stages[^1];
        var type = stage.Shape!.Roots["lineShipment"]!.GetValue<string>();

        type.Should().Be("t:transport.shipment");
        answer.Types[type]!["members"]!.AsArray().Select(row => row![0]!.GetValue<string>()).Should().Contain("tours.tourId");

        var here = FlagsAt(answer, stage.Index, "lineShipment", "tours.tourId")!;

        here["underCollection"]!.GetValue<int>().Should().Be(1, "the collection below the alias is the owner's");
        here["follow"]!.GetValue<string>().Should().Be("elements", "a resolve continued there takes elements");
    }

    [Theory]
    [InlineData("")]
    [InlineData(""", "select": ["id"]""")]
    [InlineData(""", "select": ["id", "sourceBillingLineReference.id"]""")]
    [InlineData(""", "select": ["id", "sourceBillingLineReference"]""")]
    public async Task D03_a_reference_with_cases_under_a_join_loads_its_case_member_whatever_the_join_selects_and_says_so(string select)
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>($$"""
            { "query": { "entityType": "ledger.transaction", "pipeline": [
                { "unwind": { "path": "items", "as": "item" } },
                { "resolve": { "path": "item.billingLineId", "as": "erpLine"{{select}} } },
                { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "shipment", "target": "transport.shipment" } } ] } }
            """, OxQLJson.Wire)!;

        var answer = await new InProcessFleet().ExplainAsync(request);

        answer.Valid.Should().BeTrue(Errors(answer));

        // The read no caller sees in the query text: the member that picks the reference's case.
        answer.Stages[2].Reads.Select(read => read.ToJsonString()).Should().Equal(
            """{"path":"erpLine.sourceBillingLineReference.id","use":"resolveKey","alias":"erpLine"}""",
            """{"path":"erpLine.sourceBillingLineReference.type","use":"caseCondition","alias":"erpLine"}""");

        var loads = answer.Aliases["erpLine"]!["loads"]!.AsArray().Select(path => path!.GetValue<string>()).ToList();

        (loads.Contains("sourceBillingLineReference") || (loads.Contains("sourceBillingLineReference.id") && loads.Contains("sourceBillingLineReference.type")))
            .Should().BeTrue("the join loads what the stage after it reads: " + string.Join(", ", loads));
    }

    [Fact]
    public async Task D04_after_an_unwind_with_keep_path_false_the_row_has_the_alias_and_not_the_collection()
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>("""
            { "query": { "entityType": "ledger.transaction", "pipeline": [ { "unwind": { "path": "items", "as": "item", "keepPath": false } } ] } }
            """, OxQLJson.Wire)!;

        var answer = await new InProcessFleet().ExplainAsync(WithTypes(request));

        answer.Valid.Should().BeTrue(Errors(answer));
        answer.Stages[0].Shape!.Roots.ContainsKey("item").Should().BeTrue();

        FlagsAt(answer, 0, "", "items").Should().BeNull("the collection left the row with the unwind");
        FlagsAt(answer, 0, "", "items.id").Should().BeNull("and so did what lies under it");
        FlagsAt(answer, 0, "", "number").Should().NotBeNull();
        FlagsAt(answer, 0, "item", "id").Should().NotBeNull("the element is under the alias");
        answer.Entry!.Shape.Flags!.ContainsKey("").Should().BeFalse("at the entry the collection is in the row");
    }

    [Fact]
    public async Task A_catalog_entry_is_answered_by_this_host_for_its_own_entity_and_by_the_owner_for_another_services()
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>("""
            { "query": { "entityType": "transport.shipment", "pipeline": [] }, "catalog": [{ "id": "tour", "entity": "transport.tour", "referencing": true }, { "id": "vehicle", "entity": "fleet.vehicle" }] }
            """, OxQLJson.Wire)!;

        var answer = await new InProcessFleet().ExplainAsync(request);

        answer.Catalog[0]["type"]!.GetValue<string>().Should().Be("t:transport.tour");
        answer.Catalog[0]["referencedBy"]!.AsObject().Should().NotBeNull();
        answer.Catalog[1]["forwarded"]!.GetValue<bool>().Should().BeTrue("transport reaches fleet itself");
        answer.Catalog[1]["type"]!.GetValue<string>().Should().Be("t:fleet.vehicle");
    }

    // ---- T4: the budgets of the reference query ---------------------------------------------------------

    [Theory]
    [InlineData("EX1-source-chain", 23_200, 3_500)]
    [InlineData("A4", 27_100, 3_900)]
    [InlineData("A5", 44_900, 5_700)]
    public async Task T4_the_reference_answer_stays_within_its_time_budget_and_its_size_budget(string id, int raw, int gzip)
    {
        var fleet = new InProcessFleet();
        var request = ReportScenarios.Ids.Contains(id) ? Prefix(id, int.MaxValue) : JsonSerializer.Deserialize<ExplainRequest>(ExplainGolden.Cases[id]().ToJsonString(), OxQLJson.Wire)!;

        // Cold: the owners are asked. Warm: their answers are kept, which is what the budget is about.
        var cold = Stopwatch.StartNew();
        var first = await fleet.ExplainAsync(request);
        cold.Stop();

        first.Valid.Should().BeTrue(Errors(first));
        first.Cache.Complete.Should().BeTrue();

        for (var warmup = 0; warmup < 20; warmup++)
            await fleet.ExplainAsync(request);

        var runs = new List<double>();

        for (var run = 0; run < 25; run++)
        {
            var timer = Stopwatch.StartNew();

            await fleet.ExplainAsync(request);
            runs.Add(timer.Elapsed.TotalMilliseconds);
        }

        var sizes = Sizes(first);

        if (Environment.GetEnvironmentVariable("OXQL_DUMP_EXPLAIN") is { Length: > 0 } directory)
            File.WriteAllBytes(Path.Combine(directory, $"{id}.explain.json"), JsonSerializer.SerializeToUtf8Bytes(first, OxQLJson.Wire));

        var median = runs.Order().ElementAt(runs.Count / 2);
        var parts = JsonSerializer.SerializeToNode(first, OxQLJson.Wire)!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value?.ToJsonString().Length ?? 0);
        // What a consumer without schema documents asks for: the member rows written out, two levels and one.
        var tabled = await fleet.ExplainAsync(WithTypes(request));
        var deep = Sizes(tabled);
        var shallow = Sizes(await fleet.ExplainAsync(WithTypes(request) with { ShapeDepth = 1 }));

        output.WriteLine($"{id}: {request.Query.Pipeline.Count} stages: raw {sizes.Raw} B, gzip {sizes.Gzip} B, warm median {median:F2} ms (min {runs.Min():F2}), cold {cold.Elapsed.TotalMilliseconds:F1} ms; with the types written out: depth 2 raw {deep.Raw} B, gzip {deep.Gzip} B; depth 1 raw {shallow.Raw} B, gzip {shallow.Gzip} B");
        output.WriteLine("  " + string.Join(", ", parts.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}")));
        output.WriteLine($"  types {first.Types.Count}, rules {first.Rules.Count}, member rows written out {tabled.Types.Sum(type => type.Value!["members"]!.AsArray().Count)}, owner calls (cold) {first.Owners.Sum(owner => owner["calls"]!.GetValue<int>())}");

        median.Should().BeLessThanOrEqualTo(MaxWarmMs, "T4: warm, the server answers within 10 ms");

        // The answer names its types and says where their members stand; the members are the schema
        // documents'. Measured: the reference chain 22.5 KB raw and 3.4 KB gzipped (79.8 and 10.4 with the
        // member rows in it), A4 26.3 and 3.7 (72.8 and 9.3), A5 with 16 stages and 14 aliases 43.6 and 5.5
        // (107.8 and 12.8). The budget is 60 KB raw and 10 KB gzipped; the ceilings here are what was
        // measured plus three percent, so that a member that creeps back in fails.
        sizes.Raw.Should().BeLessThanOrEqualTo(Math.Min(raw, MaxRawBytes), "T4: the answer stays within its raw size");
        sizes.Gzip.Should().BeLessThanOrEqualTo(Math.Min(gzip, MaxGzipBytes), "T4: and within its gzipped size");
        first.Types.Select(type => type.Value!["members"]).Should().OnlyContain(members => members == null, "the default answer writes no member row");
        first.FlagSets.Should().BeNull();
    }

    private static (int Raw, int Gzip) Sizes(ExplainResult answer)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(answer, OxQLJson.Wire);
        using var packed = new MemoryStream();

        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(raw);

        return (raw.Length, (int)packed.Length);
    }

    /// <summary>
    /// The flags of a member of a root after a stage, as a consumer reads them: the member's own entry
    /// of the stage's override set, else the set's entry for the member's own flags (<c>~</c>), else what
    /// every member has (<c>*</c>), else the type's own; null when the member is not in the row there.
    /// </summary>
    internal static JsonObject? FlagsAt(ExplainResult answer, int stage, string root, string path)
    {
        var shape = answer.Stages.Single(each => each.Index == stage).Shape!;
        var overrides = shape.Flags![root] is JsonValue pointer ? answer.FlagSets![pointer.GetValue<string>()]!.AsObject() : null;

        JsonObject? Read(JsonNode? id) => id is null ? null : answer.FlagSets![id.GetValue<string>()]!.AsObject();

        if (overrides is not null && overrides.TryGetPropertyValue(path, out var here))
            return Read(here);

        var type = shape.Roots[root]!.GetValue<string>();

        if (type.StartsWith("u:", StringComparison.Ordinal))
        {
            var union = answer.Types[type]!;
            var row = union["members"]!.AsArray().Select(each => each!.AsArray()).Single(each => each[0]!.GetValue<string>() == path);

            type = union["of"]![row.Count > 1 ? row[1]![0]!.GetValue<int>() : 0]!.GetValue<string>();
        }

        var own = answer.Types[type]!["members"]!.AsArray().Select(each => each!.AsArray()).Single(each => each[0]!.GetValue<string>() == path)[ExplainTypes.Row.Flags]!.GetValue<string>();

        if (overrides?[ExplainTypes.ByOwn] is JsonObject byOwn && byOwn.TryGetPropertyValue(own, out var mapped))
            return Read(mapped);

        if (overrides is not null && overrides.TryGetPropertyValue(ExplainTypes.Every, out var every))
            return Read(every);

        return answer.FlagSets![own]!.AsObject();
    }

    /// <summary>The fleet's engines side by side, each the owner of its service's entities, explaining for one another in process as the internal explain route would.</summary>
    internal sealed class InProcessFleet : IRemoteQueryClient
    {
        private readonly Dictionary<string, MongoQueryEngine> engines = new(StringComparer.Ordinal);
        private readonly EngineDirect direct = new(LabService.Ledger.Model);

        public InProcessFleet()
        {
            foreach (var service in LabService.All)
                engines[service.Key] = new MongoQueryEngine(new Published(service), new NoRunner(), direct.Cursors, direct.Options(), this);
        }

        /// <summary>A service's model with the revision of the schema document it would publish, as a host of the base package provides it.</summary>
        private sealed class Published(LabService service) : IEntityModelProvider
        {
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Revisions = new(StringComparer.Ordinal);

            public EntityModel Model => service.Model;

            public string? SchemaRevision => Revisions.GetOrAdd(service.Key, _ => FleetSchemaDocument.Of(service)["revision"]!.GetValue<string>());
        }

        /// <summary>The owner calls made, by service.</summary>
        public List<string> Calls { get; } = [];

        public async Task<ExplainResult> ExplainAsync(ExplainRequest request)
        {
            var service = request.Query.EntityType.Split('.')[0];
            var outcome = await engines[service].ExplainAsync(request, direct.Context());

            return outcome.Should().BeOfType<ExplainOutcome.Success>(outcome is ExplainOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;
        }

        public bool IsConfigured(string serviceKey) => engines.ContainsKey(serviceKey);

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(IsConfigured(serviceKey));

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Explain never runs a batch.");

        /// <summary>The owner's internal explain: its engine under the forwarded organisation, as an internal call, answered as wire JSON.</summary>
        public async Task<JsonObject?> ExplainAsync(string serviceKey, ExplainRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            Calls.Add(serviceKey);

            var sent = JsonSerializer.Deserialize<ExplainRequest>(JsonSerializer.Serialize(request, OxQLJson.Wire), OxQLJson.Wire)!;
            var outcome = await engines[serviceKey].ExplainAsync(sent, direct.Context() with { Internal = true }, cancellationToken);

            return outcome is ExplainOutcome.Success success ? JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire)!.AsObject() : null;
        }
    }

    private sealed class NoRunner : IAggregateRunner
    {
        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Explain never runs an aggregate.");
    }
}
