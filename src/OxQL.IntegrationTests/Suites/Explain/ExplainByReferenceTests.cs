using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Suites.Report;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// Types by reference: the default explain answer names its types and says where their members stand,
/// and a client that holds the services' schema documents rebuilds from the two everything the
/// <c>include: "types"</c> answer writes out (<see cref="SchemaTypes"/>).
/// </summary>
[Trait("Category", "Integration")]
public class ExplainByReferenceTests
{
    private static readonly Lazy<SchemaTypes> Schema = new(() => new SchemaTypes(LabService.All.Select(service => FleetSchemaDocument.Of(service))));

    public static TheoryData<string> Entities()
    {
        var entities = new TheoryData<string>();

        foreach (var service in LabService.All)
            foreach (var id in service.Model.Entities.Keys)
                entities.Add(id);

        return entities;
    }

    private static ExplainRequest Request(string json) => JsonSerializer.Deserialize<ExplainRequest>(json, OxQLJson.Wire)!;

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task R01_the_member_rows_of_every_entity_and_item_type_are_its_schema_document_read(string entity)
    {
        var items = FleetSchemaDocument.Of(LabService.Of(entity.Split('.')[0]))["types"]![entity]!["items"]!.AsArray().Select(item => item!["path"]!.GetValue<string>()).ToList();
        var catalog = string.Join(",", items.Select((item, index) => $$"""{ "id": "i{{index}}", "entity": "{{entity}}#{{item}}" }"""));

        foreach (var depth in new[] { 1, 2, 3 })
        {
            var answer = await new ExplainShapeFleetTests.InProcessFleet().ExplainAsync(Request($$"""
                { "query": { "entityType": "{{entity}}", "pipeline": [] }, "include": ["shape", "types"], "shape": { "depth": {{depth}} }, "catalog": [{{catalog}}] }
                """));

            foreach (var key in new[] { "t:" + entity }.Concat(items.Select(item => $"t:{entity}#{item}")))
                Reconstruction.Same(answer.Types[key], Schema.Value.Table(key, depth, 300), $"{key} at depth {depth}");
        }
    }

    public static TheoryData<string> Goldens => new(ExplainGolden.Cases.Keys);

    /// <summary>
    /// Every golden: the default answer and the schema documents give the answer <c>include: "types"</c>
    /// writes out, type for type, row for row, and flag for flag at every stage.
    /// </summary>
    [Theory]
    [MemberData(nameof(Goldens))]
    public async Task R02_the_schema_documents_and_the_default_answer_of_a_golden_are_its_answer_with_the_types_written_out(string id)
    {
        var request = ExplainGolden.Cases[id]();
        var client = await ReportScenarios.ClientAsync(request);
        var byReference = (await client.ExplainHereAsync(request)).Body!.AsObject();
        var tabled = (await client.ExplainHereAsync(new JsonObject { ["query"] = request.DeepClone(), ["include"] = new JsonArray("shape", "notes", "types") })).Body!.AsObject();

        Reconstruction.Assert(Schema.Value, byReference, tabled, 2, published: false).Should().BeGreaterThan(0);
    }

    /// <summary>Every prefix of every scenario, three levels deep, on engines that publish their documents' revisions.</summary>
    [Theory]
    [MemberData(nameof(ExplainShapeFleetTests.Prefixes), MemberType = typeof(ExplainShapeFleetTests))]
    public async Task R03_the_schema_documents_and_the_default_answer_of_every_prefix_are_its_answer_with_the_types_written_out(string id, int stages)
    {
        var fleet = new ExplainShapeFleetTests.InProcessFleet();
        var request = ExplainShapeFleetTests.Prefix(id, stages);
        var byReference = JsonSerializer.SerializeToNode(await fleet.ExplainAsync(request), OxQLJson.Wire)!.AsObject();
        var tabled = JsonSerializer.SerializeToNode(await fleet.ExplainAsync(ExplainShapeFleetTests.WithTypes(request) with { ShapeDepth = 3 }), OxQLJson.Wire)!.AsObject();

        Reconstruction.Assert(Schema.Value, byReference, tabled, 3, published: true).Should().BeGreaterThan(0);

        foreach (var (service, revision) in byReference["revision"]!["schema"]!.AsObject())
            revision!.GetValue<string>().Should().Be(Schema.Value.RevisionOf(service), $"the answer names the revision of {service}'s document");
    }
}
