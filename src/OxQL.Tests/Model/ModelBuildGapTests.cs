using System.Xml.Linq;
using FluentAssertions;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures.BuildGaps;
using Xunit;

namespace OxQL.Tests.Model.Fixtures.BuildGaps
{
    public class BgOrder
    {
        public Guid Id { get; set; }

        [OxQLReference("bg.customer", "nosuchfield")]
        public string? CustomerCode { get; set; }

        [OxQLReference("bg.customer", "code")]
        public string? KnownCode { get; set; }
    }

    public class BgCustomer
    {
        public Guid Id { get; set; }

        public string Code { get; set; } = "";
    }

    /// <summary>
    /// <list type="bullet"><item><term>Open</term><description>still running</description></item></list>
    /// </summary>
    public class BgDocumented;
}

namespace OxQL.Tests.Model
{
    /// <summary>The model build gaps of the engine review (RE-24).</summary>
    public class ModelBuildGapTests
    {
        private static EntityModel Build() => ClrModelBuilder.Build(
        [
            new EntityDeclaration("bg.order", "bg.order", typeof(BgOrder), "orders", null, false),
            new EntityDeclaration("bg.customer", "bg.customer", typeof(BgCustomer), "customers", null, false),
        ]);

        [Fact]
        public void A_local_targets_declared_field_the_entity_does_not_have_is_a_finding_and_no_reference()
        {
            var model = Build();

            model.Entities["bg.order"].Path("customerCode")!.References.Should().BeNullOrEmpty();
            model.Entities["bg.order"].Path("knownCode")!.Reference!.TargetField.Should().Be("code");
            model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.ReferenceTargetFieldUnknown && finding.Detail == "bg.customer#nosuchfield");
        }

        [Fact]
        public void An_empty_reference_case_list_leaves_the_simple_reference_standing()
        {
            var document = """
                { "schemaVersion": "1.1", "service": "bg", "api": { "name": "bg-api", "version": "v1" }, "limits": {},
                  "types": {
                    "bg.order": { "entity": true, "key": ["id"], "queryable": true, "properties": [
                      { "name": "id", "kind": "guid" },
                      { "name": "customerId", "kind": "guid", "references": { "entity": "bg.customer", "field": "id", "joinable": true, "inferred": false }, "referenceCases": [] } ] },
                    "bg.customer": { "entity": true, "key": ["id"], "queryable": true, "properties": [ { "name": "id", "kind": "guid" } ] } } }
                """;

            DocumentModelBuilder.Build(document).Entities["bg.order"].Path("customerId")!.Reference!.TargetEntity.Should().Be("bg.customer");
        }

        [Fact]
        public void A_list_items_term_and_description_are_separated()
        {
            var docs = new XmlDocs(_ => XDocument.Parse($$"""
                <doc><members><member name="T:{{typeof(BgDocumented).FullName}}"><summary>States: <list type="bullet"><item><term>Open</term><description>still running</description></item></list></summary></member></members></doc>
                """));

            docs.Summary(typeof(BgDocumented)).Should().Contain("Open: still running");
        }
    }
}
