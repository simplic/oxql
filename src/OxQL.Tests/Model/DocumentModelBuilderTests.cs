using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>The document builder over the four vendored service documents and the fixture document.</summary>
public class DocumentModelBuilderTests
{
    private static EntityModel Build(string name, DocumentModelOptions? options = null) =>
        DocumentModelBuilder.Build(ProbeModel.ReadFixture(name), options);

    [Fact]
    public void The_four_vendored_documents_describe_the_eight_queryable_entities()
    {
        Build("erp.json").Entities.Keys.Should().Equal("erp.transaction");
        Build("hr.json").Entities.Keys.Should().Equal("hr.employee");
        Build("logistics.json").Entities.Keys.Should().Equal("logistics.shipment", "logistics.shipment_template");
        Build("vehicle.json").Entities.Keys.Should().Equal("vehicle.department", "vehicle.equipment", "vehicle.status", "vehicle.vehicle");
    }

    [Fact]
    public void Every_vendored_document_builds_clean()
    {
        foreach (var name in ProbeModel.VendoredDocuments)
        {
            var model = Build(name);

            model.Findings.Should().BeEmpty(name);
            model.Entities.Values.Should().OnlyContain(entity => entity.Key != null && entity.Key.Wire == "id", name);
            model.Entities.Values.Should().OnlyContain(entity => entity.Paths.Count > 0, name);
        }
    }

    [Fact]
    public void Storage_names_follow_the_documents_rule()
    {
        var logistics = Build("logistics.json");
        var shipment = logistics.Entities["logistics.shipment"];

        shipment.Path("id")!.Storage.Should().Be("_id");
        shipment.Path("items.id")!.Storage.Should().Be("Items._id");
        shipment.Path("items.shippingUnit.sscc")!.Storage.Should().Be("Items.ShippingUnit.SSCC", "storageName is published where the derivation is wrong");
        shipment.Path("loadAddress.city")!.Storage.Should().Be("LoadAddress.City");
        shipment.Path("tours.isMirrored")!.Stored.Should().BeTrue("the document cannot know the member is [BsonIgnore]; the registry can");

        var vehicle = Build("vehicle.json").Entities["vehicle.vehicle"];

        vehicle.Path("qrCode")!.Storage.Should().Be("QRCode");
        vehicle.Path("registrationCertificate.egTypeApprovalNumber")!.Storage.Should().Be("RegistrationCertificate.EGTypeApprovalNumber");
        vehicle.Path("registrationCertificate.zlbiIatId")!.Storage.Should().Be("RegistrationCertificate.ZLBIIatId");
    }

    [Fact]
    public void Representations_are_the_measured_fleet_defaults()
    {
        var template = Build("logistics.json").Entities["logistics.shipment_template"];

        template.Path("loadStart.relativeTime")!.Kind.Should().Be(Kind.TimeSpan);
        template.Path("loadStart.relativeTime")!.Shape.Representation.Should().Be(Representation.Of(BsonType.String));
        template.Path("id")!.Shape.Representation.Should().Be(new Representation(BsonType.Binary, GuidRepresentation.Standard));
        template.Path("createDateTime")!.Shape.Representation.Should().Be(Representation.Of(BsonType.DateTime));

        var transaction = Build("erp.json").Entities["erp.transaction"];
        var decimalPath = transaction.Paths.First(path => path.Kind == Kind.Decimal);

        decimalPath.Shape.Representation.Should().Be(Representation.Of(BsonType.Decimal128));

        var enumPath = transaction.Paths.First(path => path.Kind == Kind.Enum);

        enumPath.Shape.Representation.Should().Be(Representation.Of(BsonType.Int32));
        enumPath.Shape.Type!.IsEnum.Should().BeTrue();
        enumPath.Shape.Type.EnumValues.Should().NotBeEmpty();
    }

    [Fact]
    public void The_addon_bag_is_the_addon_root_on_extendable_entities()
    {
        var shipment = Build("logistics.json").Entities["logistics.shipment"];

        shipment.Extendable.Should().BeTrue();
        shipment.Path("addon")!.IsAddonRoot.Should().BeTrue();
        shipment.Path("addon.*")!.Kind.Should().Be(Kind.Unknown);
        shipment.Path("addon.*")!.Filterable.Should().BeFalse();
        shipment.Path("items.addon")!.IsAddonRoot.Should().BeFalse("only the root bag is the entity's bag");

        var department = Build("vehicle.json").Entities["vehicle.department"];

        department.Extendable.Should().BeFalse();
        department.Paths.Should().NotContain(path => path.IsAddonRoot);
    }

    [Fact]
    public void A_typed_dictionary_types_its_values()
    {
        var transaction = Build("erp.json").Entities["erp.transaction"];
        var dictionary = transaction.Paths.First(path => path.Kind == Kind.Dictionary && path.Shape.Value!.Kind == Kind.Bool);

        transaction.Path(dictionary.Wire + ".*")!.Kind.Should().Be(Kind.Bool);
        transaction.Path(dictionary.Wire + ".*")!.Filterable.Should().BeTrue();
    }

    [Fact]
    public void Enum_entries_carry_their_values_in_declaration_order()
    {
        var logistics = Build("logistics.json");
        var type = logistics.TypePool["t_billingLineType"];

        type.IsEnum.Should().BeTrue();
        type.EnumValues.Select(value => (value.Name, value.Value, value.Active)).Should().Equal(("Customer", 0L, true), ("Carrier", 1L, true), ("Supplier", 2L, true));
    }

    [Fact]
    public void Retired_ids_come_from_the_aliases_and_legacy_model_ids_are_not_accepted()
    {
        var vehicle = Build("vehicle.json");

        vehicle.RetiredIds.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["department"] = "vehicle.department",
            ["equipment"] = "vehicle.equipment",
        });
        vehicle.TryResolve("department", out var entity, out var retired).Should().BeTrue();
        retired.Should().BeTrue();
        entity.Id.Should().Be("vehicle.department");
        vehicle.TryResolve("$DepartmentResponse", out _, out _).Should().BeFalse();
        vehicle.TryResolve("Vehicle.Vehicle", out _, out _).Should().BeFalse();

        Build("erp.json").RetiredIds.Should().BeEquivalentTo(new Dictionary<string, string> { ["transaction"] = "erp.transaction" });
        Build("logistics.json").RetiredIds.Should().BeEmpty();
    }

    [Fact]
    public void An_inferred_reference_is_dropped_unless_asked_for()
    {
        var shipmentId = Build("logistics.json").TypePool["t_shipmentDocument"].Member("shipmentId")!;

        shipmentId.Reference.Should().BeNull("the design keeps declared references only");

        var included = Build("logistics.json", new DocumentModelOptions { IncludeInferredReferences = true })
            .TypePool["t_shipmentDocument"].Member("shipmentId")!.Reference;

        included.Should().NotBeNull();
        included!.TargetEntity.Should().Be("logistics.shipment");
        included.TargetField.Should().Be("id");
        included.DeclaredBy.Should().Be(ReferenceSource.Document);
        included.IsRemote.Should().BeFalse();
    }

    [Fact]
    public void A_declared_reference_in_the_document_binds_like_an_attribute()
    {
        var order = Build("probe.json").Entities["probe.order"];

        order.Path("customerId")!.Reference.Should().BeEquivalentTo(new { TargetEntity = "probe.customer", TargetField = "id", IsRemote = false, DeclaredBy = ReferenceSource.Document });
        order.Path("contactNumber")!.Reference.Should().BeEquivalentTo(new { TargetEntity = "crm.contact", TargetField = "number", IsRemote = true });
        order.Path("missingId")!.Reference.Should().BeNull("it is marked inferred");
    }

    [Fact]
    public void An_embedded_entity_is_a_snapshot()
    {
        var vehicle = Build("vehicle.json");

        vehicle.Entities["vehicle.vehicle"].Path("status")!.Shape.SnapshotOf.Should().Be("vehicle.status");
        vehicle.Entities["vehicle.vehicle"].Path("status")!.Shape.Type.Should().BeSameAs(vehicle.Entities["vehicle.status"].Root);
        vehicle.Entities["vehicle.vehicle"].Path("status.name")!.Storage.Should().Be("Status.Name");
    }

    [Fact]
    public void Display_and_key_follow_the_schema_rule()
    {
        var vehicle = Build("vehicle.json");

        vehicle.Entities["vehicle.vehicle"].Display!.Wire.Should().Be("matchCode");
        vehicle.Entities["vehicle.department"].Display!.Wire.Should().Be("name");
        Build("erp.json").Entities["erp.transaction"].Display!.Wire.Should().Be("number");
        Build("logistics.json").Entities["logistics.shipment"].Display.Should().BeNull();
        Build("hr.json").Entities["hr.employee"].Key!.Storage.Should().Be("_id");
    }

    [Fact]
    public void Collections_come_from_the_options_or_default_to_the_entity_id()
    {
        Build("vehicle.json").Entities["vehicle.vehicle"].Collection.Should().Be("vehicle.vehicle");

        var options = new DocumentModelOptions { Storage = new Dictionary<string, EntityStorage> { ["vehicle.vehicle"] = new("vehicle", "fleet") } };
        var entity = Build("vehicle.json", options).Entities["vehicle.vehicle"];

        entity.Collection.Should().Be("vehicle");
        entity.Database.Should().Be("fleet");
        entity.ClrType.Should().BeNull();
    }

    [Fact]
    public void Member_storage_overrides_express_what_only_the_registry_knows()
    {
        var order = ProbeModel.Document().Entities["probe.order"];

        order.Path("hidden")!.Stored.Should().BeFalse();
        order.Path("figure.figureKind")!.Storage.Should().BeNull();
        order.Path("renamed")!.Storage.Should().Be("x");
        order.Path("priceList.*.net")!.Storage.Should().Be("PriceList.v.Net");
        order.Path("prices.*.net")!.Storage.Should().Be("Prices.*.Net");
    }

    [Fact]
    public void An_unsupported_format_is_refused()
    {
        var act = () => DocumentModelBuilder.Build("""{ "schemaVersion": "2.0", "types": {} }""");

        act.Should().Throw<InvalidDataException>().WithMessage("*2.0*");
        ((Action)(() => DocumentModelBuilder.Build("""{ "types": {} }"""))).Should().Throw<InvalidDataException>();
        ((Action)(() => DocumentModelBuilder.Build("""{ "schemaVersion": "1.0" }"""))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_dangling_pointer_makes_the_member_unknown_with_a_finding()
    {
        var model = DocumentModelBuilder.Build("""
            { "schemaVersion": "1.0", "types": { "probe.thing": { "entity": true, "properties": [
                { "name": "id", "kind": "guid", "nullable": false },
                { "name": "part", "kind": "object", "type": "#/types/t_missing", "nullable": true } ] } } }
            """);

        model.Entities["probe.thing"].Path("part")!.Kind.Should().Be(Kind.Unknown);
        model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.DanglingTypePointer && finding.Target == "probe.thing#part");
    }

    [Theory]
    [InlineData("erp.json")]
    [InlineData("hr.json")]
    [InlineData("logistics.json")]
    [InlineData("vehicle.json")]
    [InlineData("probe.json")]
    public void The_model_round_trips_the_documents_wire_view(string name)
    {
        using var document = ProbeModel.ParseFixture(name);
        var model = DocumentModelBuilder.Build(document.RootElement);

        var projected = TypesProjection.Project(model);
        var expected = TypesProjection.Comparable(document.RootElement);

        JsonNode.DeepEquals(projected, expected).Should().BeTrue(
            "the model must carry everything the document publishes;\nexpected:\n{0}\nprojected:\n{1}",
            TypesProjection.Pretty(expected), TypesProjection.Pretty(projected));
    }
}
