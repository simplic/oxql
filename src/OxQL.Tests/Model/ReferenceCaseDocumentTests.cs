using FluentAssertions;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>
/// The document reader over <c>referenceCases</c> (DESIGN §5.4): the member order the schema
/// publishes (case <c>when</c>, <c>keyAs</c>, <c>targets</c>; target <c>entity</c>, <c>item</c>,
/// <c>field</c>) reads into the same cases a CLR build declares.
/// </summary>
public class ReferenceCaseDocumentTests
{
    private const string Document = """
        {
          "schemaVersion": "1.1",
          "types": {
            "doc.invoice": { "entity": true, "properties": [
              { "name": "id", "kind": "guid", "nullable": false },
              { "name": "source", "kind": "object", "type": "#/types/t_source", "nullable": true },
              { "name": "billing", "kind": "object", "type": "#/types/t_billing", "nullable": true },
              { "name": "resource", "kind": "object", "type": "#/types/t_resource", "nullable": true },
              { "name": "shipmentId", "kind": "guid", "nullable": false,
                "references": { "entity": "doc.shipment", "field": "id", "joinable": true, "inferred": false } },
              { "name": "both", "kind": "guid", "nullable": false,
                "references": { "entity": "doc.shipment", "field": "id", "joinable": true, "inferred": false },
                "referenceCases": [ { "targets": [ { "entity": "doc.shipment", "item": "billingLines", "field": "id" } ] } ] },
              { "name": "unreadable", "kind": "string", "nullable": true,
                "referenceCases": [ { "keyAs": "int", "targets": [ { "entity": "doc.shipment", "field": "id" } ] },
                                    { "when": { "path": "x", "equals": ["a"], "variant": ["B"] }, "targets": [ { "entity": "doc.shipment", "field": "id" } ] },
                                    { "targets": [] } ] }
            ] },
            "doc.shipment": { "entity": true, "properties": [
              { "name": "id", "kind": "guid", "nullable": false },
              { "name": "billingLines", "kind": "array", "of": { "kind": "object", "type": "#/types/t_billingLine" }, "nullable": false }
            ] },
            "t_billingLine": { "properties": [ { "name": "id", "kind": "guid", "nullable": false } ] },
            "t_source": { "properties": [
              { "name": "type", "kind": "string", "nullable": false },
              { "name": "id", "kind": "guid", "nullable": false,
                "referenceCases": [ { "when": { "path": "type", "equals": ["logistics"] },
                                      "targets": [ { "entity": "doc.shipment", "item": "billingLines", "field": "id" },
                                                   { "entity": "transport.tour", "item": "billingLines", "field": "id" } ] } ] }
            ] },
            "t_billing": { "properties": [
              { "name": "dataType", "kind": "string", "nullable": false },
              { "name": "referenceId", "kind": "string", "nullable": false,
                "referenceCases": [ { "when": { "path": "dataType", "equals": ["shipment"] }, "keyAs": "guid", "targets": [ { "entity": "transport.shipment", "field": "id" } ] } ] }
            ] },
            "t_resource": { "properties": [
              { "name": "id", "kind": "guid", "nullable": false,
                "referenceCases": [ { "when": { "variant": ["DriverResource"] }, "targets": [ { "entity": "staff.employee", "field": "id" } ] } ] }
            ] }
          }
        }
        """;

    private static readonly EntityModel Model = DocumentModelBuilder.Build(Document);

    private static EntityDef Invoice => Model.Entities["doc.invoice"];

    private static string Render(string path) => string.Join(" ; ", Invoice.Path(path)!.References);

    [Fact]
    public void Typed_item_and_converted_cases_read_back_as_the_design_publishes_them()
    {
        Render("source.id").Should().Be("when type=logistics -> doc.shipment#billingLines.id, transport.tour#billingLines.id (remote)");
        Render("billing.referenceId").Should().Be("when dataType=shipment keyAs guid -> transport.shipment.id (remote)");
        Render("resource.id").Should().Be("when $variant=DriverResource -> staff.employee.id (remote)");

        Invoice.Path("source.id")!.References[0].Targets[0].FieldIsKey.Should().BeTrue("the element's id is its key");
        Invoice.Path("source.id")!.Reference.Should().BeNull();
        Invoice.Path("source.id")!.References.Should().OnlyContain(reference => reference.DeclaredBy == ReferenceSource.Document);
    }

    [Fact]
    public void A_simple_reference_still_reads_from_references()
    {
        Invoice.Path("shipmentId")!.Reference.Should().NotBeNull();
        Invoice.Path("shipmentId")!.Reference!.IsSimple.Should().BeTrue();
    }

    [Fact]
    public void Reference_cases_win_over_a_references_member_beside_them()
    {
        Render("both").Should().Be("-> doc.shipment#billingLines.id");
        Invoice.Path("both")!.Reference.Should().BeNull();
    }

    [Fact]
    public void An_unreadable_case_is_left_out_with_a_finding()
    {
        Invoice.Path("unreadable")!.References.Should().BeEmpty();
        Model.Findings.Where(finding => finding.Target == "doc.invoice#unreadable")
            .Should().HaveCount(3).And.OnlyContain(finding => finding.Code == BuildCodes.ReferenceDeclarationUnresolved);
        Model.Findings.Where(finding => finding.Target != "doc.invoice#unreadable").Should().BeEmpty();
    }
}
