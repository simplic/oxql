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

    [Theory]
    [MemberData(nameof(Services))]
    public void A_fleet_schema_document_reads_back_what_is_not_stored_how_a_value_is_stored_and_a_concrete_base(string key)
    {
        var service = LabService.All.Single(candidate => candidate.Key == key);
        var model = DocumentModelBuilder.Build(FleetSchemaDocument.Of(service).ToJsonString());

        foreach (var (id, entity) in service.Model.Entities)
            foreach (var path in entity.Paths)
            {
                var read = model.Entities[id].Path(path.Wire)!;

                read.Stored.Should().Be(path.Stored, $"{id}#{path.Wire}: stored");
                OxQL.Core.Binding.Shape.IsCollection(read).Should().Be(OxQL.Core.Binding.Shape.IsCollection(path), $"{id}#{path.Wire}: a collection in storage");

                // What decides the operators a member admits: a character's code point, a scalar stored as a document.
                if (path.LeafKind == OxQL.Model.Kind.String)
                    (read.Shape.Leaf.Representation.BsonType == MongoDB.Bson.BsonType.Int32).Should().Be(path.Shape.Leaf.Representation.BsonType == MongoDB.Bson.BsonType.Int32, $"{id}#{path.Wire}: a character");

                (read.Shape.Leaf.Representation.BsonType == MongoDB.Bson.BsonType.Document).Should().Be(path.Shape.Leaf.Representation.BsonType == MongoDB.Bson.BsonType.Document, $"{id}#{path.Wire}: stored as a document");
            }

        foreach (var (id, type) in service.Model.TypePool.Where(pair => pair.Value.Variants.Count > 0))
            OxQL.Core.Binding.OperandCoercer.ConcreteBaseName(model.TypePool[id]).Should().Be(OxQL.Core.Binding.OperandCoercer.ConcreteBaseName(type), $"{id}: the name of a value stored as the type itself");
    }

    [Fact]
    public void The_conformance_document_says_what_is_not_stored_and_what_is_stored_as_a_code_point()
    {
        var properties = FleetSchemaDocument.Of(LabService.Conformance)["types"]!["conformance.entity"]!["properties"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().ToList();

        properties.Single(property => property["name"]!.GetValue<string>() == "grade")["storedAs"]!.GetValue<string>().Should().Be("codePoint");
        properties.Single(property => property["name"]!.GetValue<string>() == "computed")["stored"]!.GetValue<bool>().Should().BeFalse();
        properties.Where(property => property["name"]!.GetValue<string>() is "name" or "count").Should().OnlyContain(property => !property.ContainsKey("stored") && !property.ContainsKey("storedAs"));
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
