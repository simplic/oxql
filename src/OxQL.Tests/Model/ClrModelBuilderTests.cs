using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>The registry facts the Stage 0 spike measured, each pinned on the fixture graph.</summary>
public class ClrModelBuilderTests
{
    private static EntityModel Model => ProbeModel.Clr;
    private static EntityDef Order => Model.Entities["probe.order"];

    private static PathDef Path(string wire) =>
        Order.Path(wire) ?? throw new Xunit.Sdk.XunitException($"probe.order has no path '{wire}'");

    // ---- kinds and representations ------------------------------------------------------

    [Theory]
    [InlineData("number", "string", "String")]
    [InlineData("count", "int", "Int32")]
    [InlineData("big", "long", "Int64")]
    [InlineData("ratio", "double", "Double")]
    [InlineData("amount", "decimal", "Decimal128")]
    [InlineData("flag", "bool", "Boolean")]
    [InlineData("day", "date", "DateTime")]
    [InlineData("when", "dateTime", "DateTime")]
    [InlineData("stamp", "dateTime", "Document")]
    [InlineData("span", "timeSpan", "String")]
    [InlineData("tod", "unknown", "Int64")]
    [InlineData("blob", "binary", "Binary")]
    [InlineData("initial", "string", "Int32")]
    [InlineData("link", "string", "none")]
    [InlineData("anything", "unknown", "none")]
    [InlineData("state", "enum", "Int32")]
    [InlineData("wide", "enum", "Int64")]
    [InlineData("small", "int", "Int32")]
    [InlineData("unsigned", "long", "Int32")]
    [InlineData("single", "double", "Double")]
    [InlineData("id", "guid", "Binary/Standard")]
    public void Kinds_follow_the_schema_table_and_representations_come_from_the_registry(string wire, string kind, string representation)
    {
        var path = Path(wire);

        Kinds.NameOf(path.Kind).Should().Be(kind);
        path.Shape.Representation.ToString().Should().Be(representation);
    }

    [Fact]
    public void Unknown_members_are_projectable_only()
    {
        foreach (var wire in new[] { "tod", "anything", "location", "addon.*" })
        {
            var path = Path(wire);

            path.Kind.Should().Be(Kind.Unknown, wire);
            path.Stored.Should().BeTrue(wire);
            path.Filterable.Should().BeFalse(wire);
            path.Sortable.Should().BeFalse(wire);
        }
    }

    // ---- storage names --------------------------------------------------------------------

    [Theory]
    [InlineData("id", "_id")]
    [InlineData("items.id", "Items._id")]
    [InlineData("figure.id", "Figure._id")]
    [InlineData("customer.id", "Customer._id")]
    public void Id_is_stored_as_underscore_id_at_every_depth(string wire, string storage) =>
        Path(wire).Storage.Should().Be(storage);

    [Fact]
    public void BsonElement_renames_the_storage_name()
    {
        Path("renamed").Storage.Should().Be("x");
        Path("renamed").Member.StorageName.Should().Be("x");
    }

    [Fact]
    public void Acronym_members_get_the_storage_name_and_label_for_free()
    {
        var path = Path("qrCode");

        path.Storage.Should().Be("QRCode");
        path.Member.DisplayName.Should().Be("QR Code");
        Path("number").Member.DisplayName.Should().BeNull("the derived label is right");
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("computed")]
    [InlineData("figure.figureKind")]
    public void Unstored_members_stay_in_the_wire_view_and_are_refused_for_queries(string wire)
    {
        var path = Path(wire);

        path.Member.Stored.Should().BeFalse();
        path.Storage.Should().BeNull();
        path.Filterable.Should().BeFalse();
        path.Sortable.Should().BeFalse();
        path.Kind.Should().Be(Kind.String, "the wire view still knows the kind");
    }

    [Fact]
    public void BsonRepresentation_is_read_per_member_not_per_type()
    {
        Path("amount").Shape.Representation.Should().Be(Representation.Of(BsonType.Decimal128));
        Path("moneyText").Shape.Representation.Should().Be(Representation.Of(BsonType.String));
        Path("id").Shape.Representation.Should().Be(new Representation(BsonType.Binary, GuidRepresentation.Standard));
        Path("guidText").Shape.Representation.Should().Be(new Representation(BsonType.String, GuidRepresentation.Unspecified));
        Path("state").Shape.Representation.Should().Be(Representation.Of(BsonType.Int32));
        Path("stateName").Shape.Representation.Should().Be(Representation.Of(BsonType.String));
    }

    [Fact]
    public void Nullable_is_unwrapped_for_kind_and_representation()
    {
        var maybeState = Path("maybeState");

        maybeState.Kind.Should().Be(Kind.Enum);
        maybeState.Shape.Representation.Should().Be(Representation.Of(BsonType.Int32));
        maybeState.Member.Nullable.Should().BeTrue();
        Path("state").Member.Nullable.Should().BeFalse();
        Path("maybeCount").Member.Nullable.Should().BeTrue();
        Path("note").Member.Nullable.Should().BeTrue();
        Path("number").Member.Nullable.Should().BeFalse();
        Path("shipTo").Member.Nullable.Should().BeTrue();
        Path("items").Member.Nullable.Should().BeFalse();
    }

    // ---- structure --------------------------------------------------------------------------

    [Fact]
    public void Arrays_are_traversed_implicitly_and_count_as_collection_ancestors()
    {
        var quantity = Path("items.quantity");

        quantity.Storage.Should().Be("Items.Quantity");
        quantity.Depth.Should().Be(1);
        quantity.CollectionAncestors.Should().Be(1);
        quantity.Filterable.Should().BeTrue();
        quantity.Sortable.Should().BeFalse("a path through a collection is not sortable");

        Path("items.stops.city").CollectionAncestors.Should().Be(2);
        Path("items.price.net").CollectionAncestors.Should().Be(1);
        Path("items.price.net").Depth.Should().Be(2);

        var items = Path("items");

        items.Kind.Should().Be(Kind.Array);
        items.LeafKind.Should().Be(Kind.Object);
        items.Filterable.Should().BeFalse();
    }

    [Fact]
    public void Arrays_of_scalars_are_filterable_leaves_but_never_sortable()
    {
        var tags = Path("tags");

        tags.Kind.Should().Be(Kind.Array);
        tags.LeafKind.Should().Be(Kind.String);
        tags.Shape.Leaf.Representation.Should().Be(Representation.Of(BsonType.String));
        tags.Filterable.Should().BeTrue();
        tags.Sortable.Should().BeFalse();

        Path("relatedIds").Shape.Leaf.Representation.Should().Be(new Representation(BsonType.Binary, GuidRepresentation.Standard));

        var matrix = Path("matrix");

        matrix.LeafKind.Should().Be(Kind.Int);
        matrix.Shape.Of!.Kind.Should().Be(Kind.Array);
        matrix.Shape.Of.Of!.Kind.Should().Be(Kind.Int);
    }

    [Fact]
    public void Dictionaries_contribute_a_key_segment_and_take_their_representation_from_the_registry()
    {
        var prices = Path("prices");

        prices.Kind.Should().Be(Kind.Dictionary);
        prices.Shape.DictionaryRepresentation.Should().Be(DictionaryRepresentation.Document);
        prices.Filterable.Should().BeFalse();

        var net = Path("prices.*.net");

        net.Storage.Should().Be("Prices.*.Net");
        net.Depth.Should().Be(2);
        net.CollectionAncestors.Should().Be(0);
        net.Sortable.Should().BeTrue();
        net.Member.WireName.Should().Be("net");

        Path("byNumber").Shape.DictionaryRepresentation.Should().Be(DictionaryRepresentation.Document);
        Path("byNumber.*").Kind.Should().Be(Kind.String);
        Path("byNumber.*").Storage.Should().Be("ByNumber.*");
    }

    [Fact]
    public void An_array_of_documents_dictionary_is_stored_under_v_and_is_a_collection()
    {
        Path("priceList").Shape.DictionaryRepresentation.Should().Be(DictionaryRepresentation.ArrayOfDocuments);

        var net = Path("priceList.*.net");

        net.Storage.Should().Be("PriceList.v.Net");
        net.CollectionAncestors.Should().Be(1);
        net.Filterable.Should().BeTrue();
        net.Sortable.Should().BeFalse();
    }

    [Fact]
    public void The_model_describes_what_the_driver_writes_for_both_dictionary_forms()
    {
        var order = new OrderModel
        {
            Prices = { ["eur"] = new Money { Net = 1.5m } },
            PriceList = { ["eur"] = new Money { Net = 2.5m } },
        };

        var document = order.ToBsonDocument();

        document["Prices"].AsBsonDocument["eur"].AsBsonDocument["Net"].AsDecimal128.Should().Be(new Decimal128(1.5m));
        document["PriceList"].AsBsonArray[0].AsBsonDocument["k"].AsString.Should().Be("eur");
        document["PriceList"].AsBsonArray[0].AsBsonDocument["v"].AsBsonDocument["Net"].AsDecimal128.Should().Be(new Decimal128(2.5m));
        document["ByNumber"].AsBsonDocument.ElementCount.Should().Be(0);
        document["x"].AsString.Should().Be("");
        document.Contains("Hidden").Should().BeFalse();
        document.Contains("Computed").Should().BeFalse();
        document["QRCode"].AsString.Should().Be("");
    }

    [Fact]
    public void A_dictionary_with_non_string_keys_reports_the_document_form_the_driver_then_refuses_to_write()
    {
        // The registry answers Document for every dictionary whose declaration does not say
        // otherwise, and the driver only checks the keys at write time. So such a member holds
        // an empty document or nothing in storage; the model describes what the registry says.
        Path("byNumber").Shape.DictionaryRepresentation.Should().Be(DictionaryRepresentation.Document);

        var act = () => new OrderModel { ByNumber = { [7] = "seven" } }.ToBsonDocument();

        act.Should().Throw<BsonSerializationException>().WithMessage("*key values must serialize as strings*");
    }

    [Fact]
    public void The_addon_bag_is_the_addon_root_and_its_keys_are_unknown()
    {
        var addon = Path("addon");

        addon.IsAddonRoot.Should().BeTrue();
        addon.Kind.Should().Be(Kind.Dictionary);
        addon.Shape.DictionaryRepresentation.Should().Be(DictionaryRepresentation.Document);
        addon.Storage.Should().Be("Addon");

        var key = Path("addon.*");

        key.Kind.Should().Be(Kind.Unknown);
        key.Storage.Should().Be("Addon.*");
        key.IsAddonRoot.Should().BeFalse();

        Path("prices").IsAddonRoot.Should().BeFalse("only the member named addon on an extendable entity is the bag");
        Model.Entities["probe.customer"].Paths.Should().NotContain(path => path.IsAddonRoot);
    }

    [Fact]
    public void A_type_reached_again_on_its_own_branch_stops_the_walk()
    {
        Order.Path("tree.name").Should().NotBeNull();
        Order.Path("tree.next").Should().NotBeNull();
        Order.Path("tree.next.name").Should().BeNull();
        Order.Path("tree.children").Should().NotBeNull();
        Order.Path("tree.children.name").Should().BeNull();
        Model.Findings.Should().NotContain(finding => finding.Code == BuildCodes.PathDepthExceeded);
    }

    [Fact]
    public void A_polymorphic_base_exposes_its_own_members_only_and_carries_a_discriminator()
    {
        Order.Path("figure.label").Should().NotBeNull();
        Order.Path("figure.radius").Should().BeNull("subclass members are not reachable through the base");
        Model.TypePool["t_figure"].Discriminator.Should().Be("Figure");
        Model.TypePool["t_address"].Discriminator.Should().BeNull();
        Model.TypePool.Should().NotContainKey("t_circle");
    }

    [Fact]
    public void A_member_whose_serializer_is_not_a_document_serializer_is_unknown_with_a_finding()
    {
        var location = Path("location");

        location.Kind.Should().Be(Kind.Unknown);
        location.Shape.Type.Should().BeNull();
        Order.Path("location.lat").Should().BeNull("the schema's CLR-derived paths under it do not exist in storage");
        Model.TypePool.Should().NotContainKey("t_geoPoint");
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.MemberSerializerOpaque && finding.Target == "probe.order#location");
    }

    [Fact]
    public void An_embedded_entity_is_a_snapshot_of_it()
    {
        Path("customer").Shape.Type.Should().BeSameAs(Model.Entities["probe.customer"].Root);
        Path("customer").Shape.SnapshotOf.Should().Be("probe.customer");
        Path("customer.name").Storage.Should().Be("Customer.Name");
        Path("shipTo").Shape.SnapshotOf.Should().BeNull();
    }

    [Fact]
    public void Members_are_published_most_derived_first_in_declaration_order()
    {
        Order.Root.Members.Select(member => member.WireName).Take(3).Should().Equal("number", "count", "big");
        Order.Root.Members.Select(member => member.WireName).TakeLast(3).Should().Equal("id", "createDateTime", "organizationId");
        Model.Entities["probe.base_only"].Root.Members.Select(member => member.WireName).Should().Equal("deepest", "extra", "id", "common");
    }

    // ---- references -------------------------------------------------------------------------

    [Fact]
    public void An_attribute_reference_to_a_local_entity_defaults_to_the_target_key()
    {
        var reference = Path("customerId").Reference;

        reference.Should().NotBeNull();
        reference!.TargetEntity.Should().Be("probe.customer");
        reference.TargetField.Should().Be("id");
        reference.DeclaredBy.Should().Be(ReferenceSource.Attribute);
        reference.IsRemote.Should().BeFalse();
        reference.Direction.Should().Be(ReferenceDirection.Forward);
    }

    [Fact]
    public void An_attribute_reference_outside_the_hosts_namespaces_is_remote_and_not_validated()
    {
        var reference = Path("contactNumber").Reference;

        reference.Should().NotBeNull();
        reference!.TargetEntity.Should().Be("crm.contact");
        reference.TargetField.Should().Be("number");
        reference.IsRemote.Should().BeTrue();
    }

    /// <summary>
    /// F-ENT-001. A remote target's key cannot be read on this host, and defaulting the field
    /// to <c>"id"</c> was a guess: asked for the ids of an owner keyed on something else, the
    /// owner answered rows keyed by whatever member happens to be called <c>id</c>, and every
    /// row of the join was wrong with a 200 and no diagnostic. Proven on the lab's
    /// <c>crm.conformance</c>: <c>Alpha(W-1)</c> resolved to <c>Widget Three</c>. The local
    /// branch reads the key and gets it right, which is what made the remote default
    /// indefensible; a declaration this host cannot complete is a finding now, and the resolve
    /// is refused rather than answered wrongly.
    /// </summary>
    [Fact]
    public void A_remote_reference_without_a_declared_field_is_dropped_with_a_finding()
    {
        // Declared explicitly rather than by attribute: an [OxQLType] anywhere in this
        // assembly would join the probe graph and change the equivalence snapshots.
        var model = ClrModelBuilder.Build([new EntityDeclaration("probe.unfielded", "probe.unfielded", typeof(UnfieldedReferences), "unfielded", null, false)]);
        var entity = model.Entities["probe.unfielded"];

        entity.Path("partnerId")!.Reference.Should().BeNull("a guessed join is worse than no join");
        model.Findings.Should().ContainSingle(finding =>
            finding.Code == BuildCodes.ReferenceTargetFieldUnknown && finding.Target == "probe.unfielded#partnerId");

        // The same declaration with the field named binds, which is the one-argument fix.
        entity.Path("declaredId")!.Reference!.TargetField.Should().Be("code");
        entity.Path("declaredId")!.Reference!.IsRemote.Should().BeTrue();
    }

    /// <summary>A local target's key is read from the model, so no field is needed and none is guessed.</summary>
    [Fact]
    public void A_local_reference_reads_its_target_key_rather_than_assuming_id()
    {
        Path("customerId").Reference!.TargetField.Should().Be("id", "probe.customer keys on id");
        Model.Findings.Should().NotContain(finding => finding.Code == BuildCodes.ReferenceTargetFieldUnknown && finding.Target == "probe.order#customerId");
    }

    [Fact]
    public void An_attribute_reference_to_an_unknown_local_entity_is_dropped_with_a_finding()
    {
        Path("missingId").Reference.Should().BeNull();
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.ReferenceTargetUnknown && finding.Target == "probe.order#missingId");
    }

    [Fact]
    public void ReferenceId_on_the_navigation_property_is_read_by_name_onto_the_id_member()
    {
        var reference = Path("supplierId").Reference;

        reference.Should().NotBeNull();
        reference!.TargetEntity.Should().Be("probe.supplier");
        reference.TargetField.Should().Be("id");
        reference.DeclaredBy.Should().Be(ReferenceSource.ReferenceId);
        Path("supplier").Reference.Should().BeNull("the navigation property itself carries no reference");
    }

    [Fact]
    public void An_unresolvable_ReferenceId_declaration_is_a_finding()
    {
        Model.Findings.Where(finding => finding.Code == BuildCodes.ReferenceDeclarationUnresolved)
            .Select(finding => finding.Target)
            .Should().BeEquivalentTo("probe.order#broken", "probe.order#notAnEntity");
    }

    // ---- discovery --------------------------------------------------------------------------

    [Fact]
    public void Entities_are_the_declared_ids_normalised_minus_the_dropped_ones()
    {
        Model.Entities.Keys.Should().Equal("probe.base_only", "probe.customer", "probe.keyless", "probe.order", "probe.shared_base", "probe.supplier");
        Model.Entities["probe.order"].DeclaredId.Should().Be("probe.order");
        Model.Entities["probe.order"].Collection.Should().Be("orders");
        Model.Entities["probe.order"].Extendable.Should().BeTrue();
        Model.Entities["probe.order"].DisplayName.Should().Be("Order");
        Model.Entities["probe.order"].Namespace.Should().Be("probe");
        Model.Entities["probe.supplier"].Database.Should().Be("purchasing");
        Model.Entities["probe.customer"].Database.Should().BeNull();
    }

    [Fact]
    public void A_duplicate_id_drops_every_claimant_case_insensitively()
    {
        Model.Entities.Should().NotContainKey("probe.twin");
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.DuplicateEntityId && finding.Target == "probe.twin");
    }

    [Fact]
    public void A_declaration_on_a_base_class_resolves_to_the_most_derived_subclass()
    {
        Model.Entities["probe.base_only"].ClrType.Should().Be(typeof(MoreDerived));
        Model.Entities["probe.base_only"].DisplayName.Should().Be("More Derived");
    }

    [Fact]
    public void A_type_claimed_under_two_ids_is_described_under_the_first()
    {
        Model.Entities["probe.shared_base"].ClrType.Should().Be(typeof(SharedDerived));
        Model.Entities.Should().NotContainKey("probe.shared_derived");
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.EntityTypeShared && finding.Target == "probe.shared_derived");
    }

    [Fact]
    public void Key_is_the_member_stored_under_underscore_id_and_display_follows_the_schema_rule()
    {
        Order.Key!.Wire.Should().Be("id");
        Order.Display!.Wire.Should().Be("number");
        Model.Entities["probe.customer"].Display!.Wire.Should().Be("name");
        Model.Entities["probe.supplier"].Display!.Wire.Should().Be("matchCode");
        Model.Entities["probe.base_only"].Display.Should().BeNull();

        var keyless = Model.Entities["probe.keyless"];

        keyless.Key.Should().BeNull();
        Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.EntityKeyMissing && finding.Target == "probe.keyless");
    }

    [Fact]
    public void Retired_ids_resolve_to_the_current_entity_and_live_ids_match_exactly()
    {
        Model.RetiredIds.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["probe.orders_old"] = "probe.order",
            ["orders"] = "probe.order",
        });
        Order.RetiredIds.Should().Equal("orders", "probe.orders_old");

        Model.TryResolve("probe.order", out var entity, out var retired).Should().BeTrue();
        retired.Should().BeFalse();
        entity.Id.Should().Be("probe.order");

        Model.TryResolve("probe.orders_old", out entity, out retired).Should().BeTrue();
        retired.Should().BeTrue();
        entity.Id.Should().Be("probe.order");

        Model.TryResolve("Probe.Order", out _, out _).Should().BeFalse("entity ids are matched case-sensitively");
        Model.TryResolve("probe.nothing", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_retired_id_that_is_also_live_or_claimed_twice_is_a_finding()
    {
        var model = ClrModelBuilder.Build([typeof(OrderModel).Assembly], new Dictionary<string, IReadOnlyList<string>>
        {
            ["probe.order"] = ["probe.customer", "old"],
            ["probe.supplier"] = ["old"],
        });

        model.RetiredIds.Should().BeEmpty();
        model.Findings.Where(finding => finding.Code == BuildCodes.RetiredIdAmbiguous).Select(finding => finding.Target)
            .Should().BeEquivalentTo("probe.customer", "old");
    }

    [Fact]
    public void The_type_pool_carries_the_schemas_ids()
    {
        Model.TypePool.Keys.Should().Equal(
            "probe.base_only", "probe.customer", "probe.keyless", "probe.order", "probe.shared_base", "probe.supplier",
            "t_address", "t_figure", "t_money", "t_node", "t_orderItem", "t_orderOptions", "t_orderState", "t_wideState");

        var state = Model.TypePool["t_orderState"];

        state.IsEnum.Should().BeTrue();
        state.EnumFlags.Should().BeFalse();
        state.EnumValues.Should().Equal(new EnumValueDef("Open", 0, true), new EnumValueDef("Shipped", 1, true), new EnumValueDef("Legacy", 2, false));
        Model.TypePool["t_orderOptions"].EnumFlags.Should().BeTrue();
        Model.TypePool["t_wideState"].EnumValues[1].Value.Should().Be(5_000_000_000);
        Model.TypePool["probe.order"].Should().BeSameAs(Order.Root);
    }

    [Fact]
    public void Two_pooled_types_sharing_a_name_both_take_a_hash_tail()
    {
        var declarations = new[]
        {
            new EntityDeclaration("probe.twins", "probe.twins", typeof(TwinHost), "twins", null, false),
        };

        var model = ClrModelBuilder.Build(declarations);
        var ids = model.TypePool.Keys.Where(id => id.StartsWith("t_part", StringComparison.Ordinal)).ToList();

        ids.Should().HaveCount(2);
        ids.Should().OnlyContain(id => id.Length > "t_part_".Length && id.StartsWith("t_part_", StringComparison.Ordinal));
        ids.Should().NotContain("t_part");
    }

    [Fact]
    public void The_fingerprint_is_stable_for_the_same_input_and_moves_with_it()
    {
        var again = ClrModelBuilder.Build([typeof(OrderModel).Assembly], ProbeModel.RetiredIds);
        var different = ClrModelBuilder.Build([typeof(OrderModel).Assembly]);

        again.Fingerprint.Should().Be(Model.Fingerprint);
        different.Fingerprint.Should().NotBe(Model.Fingerprint);
        Model.Fingerprint.Should().StartWith("sha256:");
    }

    [Fact]
    public void An_empty_assembly_list_is_a_finding_not_a_throw()
    {
        var model = ClrModelBuilder.Build(Array.Empty<System.Reflection.Assembly>());

        model.Entities.Should().BeEmpty();
        model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.EntityAssembliesMissing);
    }

    public class TwinHost
    {
        public Guid Id { get; set; }
        public Part First { get; set; } = new();
        public Nested.Part Second { get; set; } = new();
    }

    public class Part
    {
        public string? A { get; set; }
    }

    public static class Nested
    {
        public class Part
        {
            public string? B { get; set; }
        }
    }

    /// <summary>
    /// Two references into another service, one naming the target field and one not. Not
    /// carrying <c>[OxQLType]</c>: it is declared to the builder by hand so it stays out of
    /// the probe graph.
    /// </summary>
    private sealed class UnfieldedReferences
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQL.Model.Attributes.OxQLReference("partner.partner")]
        public Guid PartnerId { get; set; }

        [OxQL.Model.Attributes.OxQLReference("partner.partner", "code")]
        public Guid DeclaredId { get; set; }
    }
}
