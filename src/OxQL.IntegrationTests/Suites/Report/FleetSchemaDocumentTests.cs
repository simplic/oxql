using FluentAssertions;
using OxQL.IntegrationTests.Fleet;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The fleet's schema documents the fixture export writes for the studio (<see cref="FleetSchemaDocument"/>):
/// each reads back through the engine's own document builder into a model with the same entities and
/// paths, carries its references, cases and variants, and has a revision that does not change between
/// two writes of the same model.
/// </summary>
[Trait("Category", "Integration")]
public class FleetSchemaDocumentTests
{
    public static TheoryData<string> Services()
    {
        var services = new TheoryData<string>();

        foreach (var service in LabService.All)
            services.Add(service.Key);

        return services;
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void A_fleet_schema_document_reads_back_into_the_same_entities_and_paths(string key)
    {
        var service = LabService.All.Single(candidate => candidate.Key == key);
        var document = FleetSchemaDocument.Of(service);

        document["schemaVersion"]!.GetValue<string>().Should().Be("1.1");
        document["revision"]!.GetValue<string>().Should().StartWith("sha256:").And.Be(FleetSchemaDocument.Of(service)["revision"]!.GetValue<string>());

        var model = DocumentModelBuilder.Build(document.ToJsonString());

        model.Entities.Keys.Should().BeEquivalentTo(service.Model.Entities.Keys);

        foreach (var (id, entity) in service.Model.Entities)
            model.Entities[id].Paths.Select(path => path.Wire).Should().BeEquivalentTo(entity.Paths.Select(path => path.Wire), id);
    }

    [Fact]
    public void The_ledger_document_carries_the_typed_source_line_cases_and_the_item_variants()
    {
        var types = FleetSchemaDocument.Of(LabService.Ledger)["types"]!.AsObject();
        var transaction = types["ledger.transaction"]!.AsObject();

        transaction["entity"]!.GetValue<bool>().Should().BeTrue();
        transaction["key"]!.AsArray().Single()!.GetValue<string>().Should().Be("id");

        types.Select(pair => pair.Value!.AsObject())
            .SelectMany(type => type["properties"]?.AsArray().OfType<System.Text.Json.Nodes.JsonObject>() ?? [])
            .Should().Contain(property => property.ContainsKey("referenceCases"), "the typed billing-line source reference is published as cases");
        types.Select(pair => pair.Value!.AsObject()).Should().Contain(type => type.ContainsKey("variants"), "the transaction items are polymorphic");
    }
}
