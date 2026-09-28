using FluentAssertions;
using OxQL.AspNetCore.Resolve;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Tests.Execute;
using OxQL.Tests.Model.Fixtures.References;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>
/// Reference cases and their declarations (DESIGN §3.3): <c>[OxQLReference]</c> with <c>Item</c>
/// and <c>KeyAs</c>, the repeatable <c>[OxQLReferenceWhen]</c>, host-side declarations on a
/// pooled type's wire member, and what the build refuses.
/// </summary>
public class ReferenceCaseTests
{
    private static readonly EntityModel Model = ReferenceModel.Build();

    private static EntityDef Invoice => Model.Entities[ReferenceModel.Invoice];

    private static EntityDef Broken => Model.Entities[ReferenceModel.Broken];

    private static EntityDef Plan => Model.Entities[ReferenceModel.Plan];

    private static string Render(PathDef path) => string.Join(" ; ", path.References);

    private static IEnumerable<BuildFinding> FindingsAt(string target) =>
        Model.Findings.Where(finding => finding.Target == target);

    // ---- [OxQLReference] forms ------------------------------------------------------------

    [Fact]
    public void A_plain_reference_is_simple_and_is_both_the_reference_and_its_only_case()
    {
        var path = Invoice.Path("lines.shipmentId")!;

        path.Reference.Should().NotBeNull();
        path.Reference!.IsSimple.Should().BeTrue();
        path.References.Should().ContainSingle().Which.Should().BeSameAs(path.Reference);
        path.Reference.Targets.Should().Equal(new ReferenceTarget("refs.shipment", "id", null, false, true));
        path.Reference.When.Should().BeNull();
        path.Reference.KeyAs.Should().Be(KeyAs.None);
        path.Reference.DeclaredBy.Should().Be(ReferenceSource.Attribute);
    }

    [Fact]
    public void An_item_target_names_the_element_by_its_key_and_is_not_simple()
    {
        var path = Invoice.Path("lines.shipmentBillingLineId")!;

        path.Reference.Should().BeNull("an item reference is never published as an entity join");
        path.References.Should().ContainSingle().Which.Targets.Should().Equal(new ReferenceTarget("refs.shipment", "id", "billingLines", false, true));
        path.References[0].IsSimple.Should().BeFalse();
    }

    [Fact]
    public void An_item_target_without_a_field_matches_the_element_key()
    {
        Invoice.Path("lines.defaultedItemId")!.References.Should().ContainSingle()
            .Which.Targets.Should().Equal(new ReferenceTarget("refs.shipment", "id", "billingLines", false, true));
    }

    [Fact]
    public void An_item_target_on_another_field_is_not_keyed()
    {
        Invoice.Path("lines.itemCode")!.References.Should().ContainSingle()
            .Which.Targets.Should().Equal(new ReferenceTarget("refs.shipment", "code", "billingLines", false, false));
    }

    [Fact]
    public void KeyAs_guid_converts_a_string_member_towards_a_remote_or_a_local_guid_key()
    {
        var remote = Invoice.Path("lines.remoteShipmentId")!;

        remote.Reference.Should().BeNull("a converted reference is not simple");
        remote.References.Should().ContainSingle().Which.Should().Match<ReferenceDef>(reference =>
            reference.KeyAs == KeyAs.Guid && reference.IsRemote && reference.TargetEntity == "transport.shipment" && reference.Targets[0].FieldIsKey);

        Invoice.Path("lines.localShipmentKey")!.References.Should().ContainSingle().Which.Should().Match<ReferenceDef>(reference =>
            reference.KeyAs == KeyAs.Guid && !reference.IsRemote && reference.TargetField == "id");
    }

    [Theory]
    [InlineData("unconvertedShipmentId", "declare KeyAs = Guid")]
    [InlineData("convertedToText", "the target field is string")]
    [InlineData("convertedGuid", "the member is guid")]
    public void A_key_kind_mismatch_drops_the_reference(string member, string because)
    {
        Invoice.Path($"lines.{member}")!.References.Should().BeEmpty();
        FindingsAt($"t_refInvoiceLine#{member}").Should().ContainSingle()
            .Which.Should().Match<BuildFinding>(finding => finding.Code == BuildCodes.ReferenceKeyKindMismatch && finding.Message.Contains(because, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("noteId", "notes")]
    [InlineData("missingItemId", "missing")]
    [InlineData("missingItemFieldId", "billingLines")]
    public void An_item_that_is_not_a_keyed_collection_or_lacks_the_field_is_reference_item_unknown(string member, string item)
    {
        Invoice.Path($"lines.{member}")!.References.Should().BeEmpty();
        FindingsAt($"t_refInvoiceLine#{member}").Should().ContainSingle()
            .Which.Should().Match<BuildFinding>(finding => finding.Code == BuildCodes.ReferenceItemUnknown && finding.Detail == $"refs.shipment#{item}");
    }

    // ---- [OxQLReferenceWhen] ----------------------------------------------------------------

    [Fact]
    public void Same_path_attributes_form_one_reference_with_a_case_each_in_declaration_order()
    {
        var path = Invoice.Path("source.id")!;

        path.Reference.Should().BeNull();
        Render(path).Should().Be(
            "when type=logistics -> refs.shipment#billingLines.id, refs.tour#billingLines.id ; " +
            "when type=remote -> transport.shipment#billingLines.id (remote)");
        path.References[0].When.Should().BeOfType<ReferenceCondition.PathEquals>()
            .Which.Should().Match<ReferenceCondition.PathEquals>(condition => condition.Path == "type" && condition.Values.SequenceEqual(new[] { "logistics" }));
        path.References.Should().OnlyContain(reference => reference.DeclaredBy == ReferenceSource.Attribute);
    }

    [Fact]
    public void A_typed_reference_can_convert_its_key_per_case()
    {
        var path = Invoice.Path("billing.referenceId")!;

        Render(path).Should().Be(
            "when dataType=shipment keyAs guid -> transport.shipment.id (remote) ; " +
            "when dataType=tour keyAs guid -> transport.tour.id (remote)");
    }

    [Fact]
    public void A_remote_case_target_needs_a_field()
    {
        Broken.Path("remoteWithoutField")!.References.Should().BeEmpty();
        FindingsAt("refs.broken#remoteWithoutField").Should().ContainSingle()
            .Which.Code.Should().Be(BuildCodes.ReferenceTargetFieldUnknown);
    }

    [Fact]
    public void An_unknown_local_case_target_drops_that_case_only()
    {
        Render(Broken.Path("unknownCaseTarget")!).Should().Be("when kind=b -> refs.tour.id");
        FindingsAt("refs.broken#unknownCaseTarget").Should().ContainSingle()
            .Which.Should().Match<BuildFinding>(finding => finding.Code == BuildCodes.ReferenceCaseTargetUnknown && finding.Detail == "refs.nothing");
    }

    [Theory]
    [InlineData("both", "more than one way")]
    [InlineData("differentPaths", "different paths")]
    [InlineData("sameValueTwice", "Two cases of the reference apply for 'a'")]
    [InlineData("numberSibling", "not a stored string or enum member")]
    [InlineData("missingSibling", "not a stored string or enum member")]
    public void A_declaration_the_build_cannot_read_is_unresolved_and_emits_nothing(string member, string message)
    {
        Broken.Path(member)!.References.Should().BeEmpty();
        Broken.Path(member)!.Reference.Should().BeNull();
        FindingsAt($"refs.broken#{member}").Should().ContainSingle()
            .Which.Should().Match<BuildFinding>(finding => finding.Code == BuildCodes.ReferenceDeclarationUnresolved && finding.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_variant_condition_applies_to_the_declaring_type_and_only_to_the_variants_it_names()
    {
        Render(Plan.Path("resource.ownerId")!).Should().Be("when $variant=RefDriverResource -> staff.employee.id (remote)");

        Model.TypePool["t_refDriverResource"].Member("ownerId")!.References.Should().ContainSingle()
            .Which.When.Should().BeOfType<ReferenceCondition.Variant>().Which.Names.Should().Equal("RefDriverResource");
        Model.TypePool["t_refVehicleResource"].Member("ownerId")!.References.Should().BeEmpty("no case applies to a vehicle resource");
    }

    // ---- host-side declarations -------------------------------------------------------------

    [Fact]
    public void A_host_declaration_on_an_inherited_id_applies_to_the_pooled_type_and_its_variants_only()
    {
        Render(Plan.Path("resource.id")!).Should().Be(
            "when $variant=RefDriverResource -> staff.employee.id (remote) ; when $variant=RefVehicleResource -> refs.vehicle.id");
        Plan.Path("resource.id")!.References.Should().OnlyContain(reference => reference.DeclaredBy == ReferenceSource.Declaration);
        Render(Plan.Path("pool.id")!).Should().Be(Render(Plan.Path("resource.id")!), "the pooled type is the same wherever it is embedded");

        string.Join(" ; ", Model.TypePool["t_refDriverResource"].Member("id")!.References).Should().Be("when $variant=RefDriverResource -> staff.employee.id (remote)");
        string.Join(" ; ", Model.TypePool["t_refVehicleResource"].Member("id")!.References).Should().Be("when $variant=RefVehicleResource -> refs.vehicle.id");

        Plan.Path("depot.id")!.References.Should().BeEmpty("the depot inherits the same CLR member but is not the declared type or one of its variants");
    }

    [Fact]
    public void An_unconditional_host_declaration_is_a_simple_reference()
    {
        var reference = Plan.Path("depotVehicleId")!.Reference;

        reference.Should().NotBeNull();
        reference!.IsSimple.Should().BeTrue();
        reference.TargetEntity.Should().Be("refs.vehicle");
        reference.DeclaredBy.Should().Be(ReferenceSource.Declaration);
    }

    [Fact]
    public void A_host_declaration_beside_an_attribute_drops_both()
    {
        Plan.Path("attributed")!.References.Should().BeEmpty();
        FindingsAt("refs.plan#attributed").Should().ContainSingle().Which.Code.Should().Be(BuildCodes.ReferenceDeclarationUnresolved);
    }

    [Fact]
    public void A_host_declaration_no_member_takes_is_reported()
    {
        Model.Findings.Where(finding => finding.Code == BuildCodes.ReferenceDeclarationUnresolved && finding.Detail is not null
                && (finding.Detail.EndsWith(nameof(RefDepot), StringComparison.Ordinal) || finding.Detail.EndsWith(nameof(RefUnused), StringComparison.Ordinal)))
            .Select(finding => finding.Message)
            .Should().SatisfyRespectively(
                depot => depot.Should().Contain("which the type does not have"),
                unused => unused.Should().Contain("a type the model does not describe"));
    }

    [Fact]
    public void A_variant_condition_naming_no_variant_is_unresolved()
    {
        var declarations = new ReferenceDeclarations();

        declarations.For<RefResource>("id").When(ReferenceDeclarations.Variant, "RefBicycle", "refs.vehicle");

        var model = ReferenceModel.Build(declarations);

        model.Entities[ReferenceModel.Plan].Path("resource.id")!.References.Should().BeEmpty();
        model.Findings.Should().Contain(finding => finding.Code == BuildCodes.ReferenceDeclarationUnresolved
            && finding.Target == "t_refResource#id"
            && finding.Message.Contains("'RefBicycle' is not a variant of 'RefResource'; its variants are RefDriverResource, RefResource, RefVehicleResource", StringComparison.Ordinal));
    }

    [Fact]
    public void The_declaration_builder_refuses_what_no_build_could_read()
    {
        var declarations = new ReferenceDeclarations();

        ((Action)(() => declarations.For<RefPlan>(" "))).Should().Throw<ArgumentException>();
        ((Action)(() => declarations.For<RefPlan>("x").When("kind", "a", Array.Empty<string>()))).Should().Throw<ArgumentException>();
        ((Action)(() => declarations.For<RefPlan>("x").To("refs.shipment#billingLines", item: "billingLines"))).Should().Throw<ArgumentException>();

        declarations.For<RefPlan>("x").To("refs.shipment", item: "billingLines", keyAs: OxQLKeyAs.Guid);
        declarations.All.Should().ContainSingle().Which.Should().Match<ReferenceDeclaration>(declaration =>
            declaration.Targets.Single() == "refs.shipment#billingLines" && declaration.Path == null && declaration.KeyAs == OxQLKeyAs.Guid);
    }

    [Fact]
    public void The_attributes_refuse_an_empty_case()
    {
        ((Action)(() => _ = new OxQLReferenceWhenAttribute("type", "a"))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new OxQLReferenceWhenAttribute(" ", "a", "refs.tour"))).Should().Throw<ArgumentException>();
    }

    // ---- fingerprint, remote references, the startup check ----------------------------------

    [Fact]
    public void Reference_cases_enter_the_fingerprint_and_a_model_without_declarations_differs()
    {
        ReferenceModel.Build().Fingerprint.Should().Be(Model.Fingerprint, "equal models have equal fingerprints");
        ReferenceModel.Build(new ReferenceDeclarations()).Fingerprint.Should().NotBe(Model.Fingerprint);
    }

    [Fact]
    public void Remote_references_list_every_remote_target_of_every_case_once_per_path()
    {
        RemoteReferences.Of(Model).Select(reference => $"{reference.Entity}#{reference.Path}->{reference.TargetEntity}")
            .Should().Equal(
                "refs.invoice#lines.remoteShipmentId->transport.shipment",
                "refs.invoice#source.id->transport.shipment",
                "refs.invoice#billing.referenceId->transport.shipment",
                "refs.invoice#billing.referenceId->transport.tour",
                "refs.plan#resource.ownerId->staff.employee",
                "refs.plan#resource.id->staff.employee",
                "refs.plan#pool.ownerId->staff.employee",
                "refs.plan#pool.id->staff.employee");
    }

    [Fact]
    public void The_startup_check_covers_every_case_target()
    {
        var findings = RemoteReferenceCheck.Unconfigured(Model, new FakeRemoteClient { Configured = ["transport"] });

        findings.Select(finding => $"{finding.Path}->{finding.Service}").Should().Equal(
            "resource.ownerId->staff", "resource.id->staff", "pool.ownerId->staff", "pool.id->staff");

        RemoteReferenceCheck.Unconfigured(Model, new FakeRemoteClient { Configured = ["staff"] })
            .Select(finding => finding.TargetEntity).Should().Equal("transport.shipment", "transport.shipment", "transport.shipment", "transport.tour");
    }
}
