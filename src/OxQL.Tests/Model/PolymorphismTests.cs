using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures;
using OxQL.Tests.Model.Fixtures.Polymorphism;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>
/// Polymorphic members, interface-typed members and polymorphic entities (DESIGN §3.2): the
/// variants come from the registered class maps, their members are merged into the base with
/// <c>onlyFor</c>, and a model without polymorphism is described exactly as before.
/// </summary>
public class PolymorphismTests
{
    private static readonly EntityModel Model = PolymorphismModel.Build();

    private static EntityDef Ledger => Model.Entities[PolymorphismModel.Ledger];

    private static TypeDef LineType => Model.TypePool["t_line"];

    [Fact]
    public void The_variants_are_the_registered_concrete_subclasses_ordinally()
    {
        LineType.Variants.Select(variant => variant.Name).Should().Equal("BillingLine", "GroupLine");
        LineType.Variants.Select(variant => variant.Discriminator).Should().Equal("BillingLine", "GroupLine");
        LineType.Variants.Select(variant => variant.Type.PoolId).Should().Equal("t_billingLine", "t_groupLine");
        LineType.DiscriminatorElement.Should().Be("_t");
        LineType.DiscriminatorForm.Should().Be(DiscriminatorForm.Scalar);
    }

    [Fact]
    public void A_type_without_registered_subclasses_has_no_variants()
    {
        Model.TypePool["t_billingLine"].Variants.Should().BeEmpty();
        Model.TypePool["t_billingLine"].DiscriminatorElement.Should().BeNull();
        Model.TypePool["t_billingLine"].DiscriminatorForm.Should().BeNull();
        Ledger.Root.Variants.Should().BeEmpty();
    }

    [Fact]
    public void Variant_members_are_appended_after_the_own_members_in_variant_then_declaration_order()
    {
        LineType.Members.Select(member => member.WireName).Should().Equal("id", "amount", "billingLineId", "note", "children");
        LineType.Members.Select(member => member.OnlyFor is null ? "-" : string.Join("+", member.OnlyFor))
            .Should().Equal("-", "-", "BillingLine", "BillingLine+GroupLine", "GroupLine");
    }

    [Fact]
    public void Merged_members_are_nullable_and_the_own_members_unchanged()
    {
        LineType.Member("id")!.Nullable.Should().BeFalse();
        LineType.Member("amount")!.Nullable.Should().BeFalse();
        LineType.Member("billingLineId")!.Nullable.Should().BeTrue();
        LineType.Member("note")!.Nullable.Should().BeTrue();
        Model.TypePool["t_billingLine"].Member("note")!.Nullable.Should().BeFalse("the variant's own member keeps its nullability");
    }

    [Fact]
    public void Merged_members_are_reachable_filterable_paths_under_the_base()
    {
        var billingLineId = Ledger.Path("lines.billingLineId")!;

        billingLineId.Storage.Should().Be("Lines.BillingLineId");
        billingLineId.Kind.Should().Be(Kind.Guid);
        billingLineId.Filterable.Should().BeTrue();
        billingLineId.Member.OnlyFor.Should().Equal("BillingLine");

        Ledger.Path("head.note")!.Sortable.Should().BeTrue();
        Ledger.Path("lines.children")!.Shape.Leaf.Type.Should().BeSameAs(LineType, "a variant nesting its base reaches the merged base");
        Ledger.Path("lines.secret").Should().BeNull("an unregistered subclass is not a variant");
    }

    [Fact]
    public void A_merged_member_carries_the_variant_member_s_reference()
    {
        Ledger.Path("lines.billingLineId")!.Reference!.TargetEntity.Should().Be("poly.billing_line");
        Model.Findings.Should().NotContain(finding => finding.Code == BuildCodes.ReferenceTargetUnknown);
    }

    [Fact]
    public void An_unregistered_concrete_subclass_is_a_finding_on_its_base()
    {
        var finding = Model.Findings.Should().ContainSingle(candidate => candidate.Code == BuildCodes.PolymorphicSubtypeUnregistered && candidate.Target == "t_line").Subject;

        finding.Detail.Should().Be(typeof(UnregisteredLine).FullName);
    }

    [Fact]
    public void Variants_disagreeing_on_a_kind_make_the_merged_member_unknown_with_a_finding()
    {
        var code = Model.TypePool["t_tagged"].Member("code")!;

        code.Kind.Should().Be(Kind.Unknown);
        code.Stored.Should().BeTrue();
        code.StorageName.Should().Be("Code");
        code.OnlyFor.Should().Equal("TagNumber", "TagText");
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.PolymorphicMemberConflict && finding.Target == "t_tagged#code")
            .Which.Detail.Should().Be("TagNumber, TagText");
    }

    [Fact]
    public void Variants_disagreeing_on_a_storage_name_make_the_merged_member_unknown_and_unstored()
    {
        var tag = Model.TypePool["t_tagged"].Member("tag")!;

        tag.Kind.Should().Be(Kind.Unknown);
        tag.Stored.Should().BeFalse();
        Model.Entities[PolymorphismModel.TagHolder].Path("tagged.tag")!.Filterable.Should().BeFalse();
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.PolymorphicMemberConflict && finding.Target == "t_tagged#tag");
    }

    [Fact]
    public void The_discriminator_element_is_the_one_the_driver_s_convention_names() =>
        Model.TypePool["t_tagged"].DiscriminatorElement.Should().Be("kind");

    [Fact]
    public void An_interface_with_registered_implementations_is_an_object_of_their_members()
    {
        var assignment = Model.Entities[PolymorphismModel.Assignment];
        var resource = assignment.Path("resource")!;

        resource.Kind.Should().Be(Kind.Object);
        resource.Shape.Type!.PoolId.Should().Be("t_iResource");
        resource.Shape.Type.Members.Select(member => $"{member.WireName}:{string.Join("+", member.OnlyFor!)}")
            .Should().Equal("name:DriverResource+TruckResource", "employeeId:DriverResource", "plate:TruckResource");
        resource.Shape.Type.Members.Should().OnlyContain(member => member.Nullable);
        assignment.Path("resource.name")!.Storage.Should().Be("Resource.Name", "the interface's own, unstored property gives way to what the variants store");
        assignment.Path("pool.plate")!.Filterable.Should().BeTrue();
        Model.Findings.Should().NotContain(finding => finding.Code == BuildCodes.MemberSerializerOpaque && finding.Target.StartsWith("poly.assignment#resource", StringComparison.Ordinal));
        Model.Findings.Should().NotContain(finding => finding.Code == BuildCodes.MemberSerializerOpaque && finding.Target == "poly.assignment#pool");
    }

    [Fact]
    public void An_interface_without_registered_implementations_is_described_as_before_with_a_finding()
    {
        var assignment = Model.Entities[PolymorphismModel.Assignment];

        // The driver's interface serializer describes the interface's own properties, none stored.
        assignment.Path("loose")!.Kind.Should().Be(Kind.Object);
        assignment.Path("loose.name")!.Stored.Should().BeFalse();
        assignment.Path("loose.name")!.Member.OnlyFor.Should().BeNull();
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.PolymorphicSubtypeUnregistered && finding.Target == "t_iLoose")
            .Which.Detail.Should().Be(typeof(LooseImplementation).FullName);
    }

    [Fact]
    public void A_root_class_makes_the_discriminator_hierarchical_and_a_custom_discriminator_is_kept()
    {
        var animal = Model.TypePool["t_animal"];

        animal.DiscriminatorForm.Should().Be(DiscriminatorForm.Hierarchical);
        animal.Variants.Select(variant => $"{variant.Name}={variant.Discriminator}").Should().Equal("Dog=Dog", "Puppy=pup");
        animal.Members.Select(member => member.WireName).Should().Equal("name", "barks", "weeks");
        animal.Member("weeks")!.OnlyFor.Should().Equal("Puppy");
        animal.Member("barks")!.OnlyFor.Should().Equal("Dog", "Puppy");

        var dog = Model.TypePool["t_dog"];

        dog.Variants.Select(variant => variant.Name).Should().Equal("Puppy");
        dog.Member("weeks")!.OnlyFor.Should().Equal("Puppy");
    }

    [Fact]
    public void A_known_type_named_by_the_attribute_is_a_variant_without_a_registration()
    {
        var shape = Model.TypePool["t_knownShape"];

        shape.Variants.Select(variant => variant.Name).Should().Equal("KnownCircle");
        Model.Entities[PolymorphismModel.Zoo].Path("shape.radius")!.Member.OnlyFor.Should().Equal("KnownCircle");
    }

    [Fact]
    public void A_declared_class_with_registered_subclass_maps_stays_the_root_and_is_merged()
    {
        PolymorphismRegistrations.Ensure();

        var findings = new List<BuildFinding>();
        var declarations = EntityScanner.Scan([EmittedEntities.Assembly], findings);

        declarations.Single(declaration => declaration.Id == "poly.resource").ClrType.Should().Be(EmittedEntities.Resource);

        var model = ClrModelBuilder.Build([EmittedEntities.Assembly]);
        var root = model.Entities["poly.resource"].Root;

        root.Members.Select(member => $"{member.WireName}:{(member.OnlyFor is null ? "-" : string.Join("+", member.OnlyFor))}")
            .Should().Equal("id:-", "name:-", "employeeId:DriverResource", "vehicleId:VehicleResource", "name2:VehicleResource");
        root.Variants.Select(variant => variant.Name).Should().Equal("DriverResource", "VehicleResource");
        model.Entities["poly.resource"].Key!.Wire.Should().Be("id");
        findings.Should().NotContain(finding => finding.Target == "poly.resource");
    }

    [Fact]
    public void Without_registered_subclass_maps_the_most_derived_subclass_stands_in_with_a_finding()
    {
        PolymorphismRegistrations.Ensure();

        var findings = new List<BuildFinding>();
        var declaration = EntityScanner.Scan([EmittedEntities.Assembly], findings).Single(candidate => candidate.Id == "poly.fallback");

        declaration.ClrType.Should().Be(EmittedEntities.FallbackDerived);
        findings.Should().ContainSingle(finding => finding.Code == BuildCodes.PolymorphicSubtypeUnregistered && finding.Target == "poly.fallback")
            .Which.Detail.Should().Be("Emitted.FallbackDerived");
    }

    [Fact]
    public void A_class_map_the_build_registered_itself_never_turns_the_fallback_into_a_root()
    {
        PolymorphismRegistrations.Ensure();

        // The first build looks the stand-in up, which registers its class map; the second
        // build must not read that as the host's registration.
        var first = ClrModelBuilder.Build([EmittedEntities.Assembly]);
        var second = ClrModelBuilder.Build([EmittedEntities.Assembly]);

        second.Entities["poly.fallback"].ClrType.Should().Be(EmittedEntities.FallbackDerived);
        second.Fingerprint.Should().Be(first.Fingerprint);
        PolymorphismModel.Build().Fingerprint.Should().Be(Model.Fingerprint);
    }

    [Fact]
    public void The_fingerprint_covers_only_for_and_the_variants()
    {
        var projected = ProjectedDocument(Model);
        var withoutOnlyFor = JsonNode.Parse(projected.ToJsonString())!;
        var withoutVariants = JsonNode.Parse(projected.ToJsonString())!;

        withoutOnlyFor["types"]!["t_line"]!["properties"]![2]!.AsObject().Remove("onlyFor");
        withoutVariants["types"]!["t_line"]!.AsObject().Remove("variants");

        var full = DocumentModelBuilder.Build(projected.ToJsonString()).Fingerprint;

        DocumentModelBuilder.Build(withoutOnlyFor.ToJsonString()).Fingerprint.Should().NotBe(full);
        DocumentModelBuilder.Build(withoutVariants.ToJsonString()).Fingerprint.Should().NotBe(full);
    }

    [Fact]
    public void A_model_without_polymorphism_keeps_its_2_0_fingerprint()
    {
        // Captured on the 2.0 builders before variants existed.
        ProbeModel.Clr.Fingerprint.Should().Be("sha256:fd31b7db13019a70b5861cf2867e4a669762593f8108ead5b73225837c594349");
        ProbeModel.Document().Fingerprint.Should().Be("sha256:aebf661e162cac0eee9c8a87e774fdf7235e75cc07a42c251fc0e136ac02ffa0");
    }

    [Theory]
    [InlineData("erp.json", "sha256:351267fac4b67c529fb3219b55c304a5bd6908e314baccf21c3867293a253667")]
    [InlineData("hr.json", "sha256:37cecb82e42ce4bae7a4c3e9611c938bde6b3832fb9ee556a9ccf5ef2068c055")]
    [InlineData("logistics.json", "sha256:803113e79931aab9717f65a707735ff57b1a7b1369351e907b07ec458cc8aa8e")]
    [InlineData("vehicle.json", "sha256:57ab5db8ff49d120eab645f02ab14bb7d317a4889bde15118a5d85637cf24af5")]
    public void A_vendored_1_0_document_builds_the_model_it_built_before(string name, string fingerprint)
    {
        var model = DocumentModelBuilder.Build(ProbeModel.ReadFixture(name));

        model.Fingerprint.Should().Be(fingerprint);
        model.TypePool.Values.Should().OnlyContain(type => type.Variants.Count == 0 && type.DiscriminatorElement == null);
        model.Entities.Values.SelectMany(entity => entity.Paths).Should().OnlyContain(path => path.Member.OnlyFor == null);
    }

    [Fact]
    public void A_1_1_document_reads_back_the_variants_and_only_for_the_clr_build_published()
    {
        var document = DocumentModelBuilder.Build(ProjectedDocument(Model).ToJsonString(), new DocumentModelOptions
        {
            Storage = Model.Entities.ToDictionary(pair => pair.Key, pair => new EntityStorage(pair.Value.Collection)),
            MemberStorage = new Dictionary<string, string?> { ["t_tagText#tag"] = "x", ["t_tagNumber#tag"] = "y", ["t_tagged#tag"] = null, ["t_iLoose#name"] = null },
        });

        foreach (var id in new[] { PolymorphismModel.Ledger, PolymorphismModel.Assignment, PolymorphismModel.Zoo })
        {
            var (onlyClr, onlyDocument) = PathSnapshot.Diff(Model.Entities[id], document.Entities[id]);

            onlyClr.Should().BeEmpty(id);
            onlyDocument.Should().BeEmpty(id);
            document.Entities[id].Paths.Select(path => path.Member.OnlyFor is null ? "" : string.Join("+", path.Member.OnlyFor))
                .Should().Equal(Model.Entities[id].Paths.Select(path => path.Member.OnlyFor is null ? "" : string.Join("+", path.Member.OnlyFor)), id);
        }

        document.TypePool["t_line"].Variants.Select(variant => $"{variant.Name}:{variant.Type.PoolId}").Should().Equal("BillingLine:t_billingLine", "GroupLine:t_groupLine");
        document.TypePool["t_animal"].DiscriminatorForm.Should().Be(DiscriminatorForm.Hierarchical);
        document.TypePool["t_tagged"].DiscriminatorElement.Should().Be("kind");
        document.Findings.Should().BeEmpty();
    }

    /// <summary>The 1.1 document the schema would publish for a model: the 1.0 projection plus <c>onlyFor</c>, <c>discriminator</c> and <c>variants</c>.</summary>
    private static JsonObject ProjectedDocument(EntityModel model)
    {
        var types = TypesProjection.Project(model);

        foreach (var (id, type) in model.TypePool)
        {
            if (type.IsEnum)
                continue;

            var entry = types[id]!.AsObject();
            var properties = entry["properties"]!.AsArray();

            for (var index = 0; index < type.Members.Count; index++)
                if (type.Members[index].OnlyFor is { } onlyFor)
                    properties[index]!["onlyFor"] = new JsonArray([.. onlyFor.Select(name => (JsonNode)name)]);

            if (type.Variants.Count == 0)
                continue;

            entry["discriminator"] = new JsonObject
            {
                ["element"] = type.DiscriminatorElement,
                ["form"] = type.DiscriminatorForm == DiscriminatorForm.Hierarchical ? "hierarchical" : "scalar",
            };
            entry["variants"] = new JsonArray([.. type.Variants.Select(variant => (JsonNode)new JsonObject
            {
                ["name"] = variant.Name,
                ["type"] = "#/types/" + variant.Type.PoolId,
            })]);
        }

        return new JsonObject { ["schemaVersion"] = "1.1", ["types"] = types };
    }
}
