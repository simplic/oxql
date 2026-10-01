using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures;
using Xunit;

namespace OxQL.Tests.Model.Fixtures.Relations
{
    /// <summary>The holder of every form a relation name is derived from: each member one rule.</summary>
    public class RelTransaction
    {
        public Guid Id { get; set; }

        // Rule 3: the member's own name without Id, Ids, Number or Key.
        [OxQLReference("rel.tour")]
        public Guid TourId { get; set; }

        [OxQLReference("rel.vehicle")]
        public List<Guid> VehicleIds { get; set; } = [];

        [OxQLReference("rel.contact", "number")]
        public string? ContactNumber { get; set; }

        [OxQLReference("rel.tour")]
        public Guid? AccountKey { get; set; }

        // Rule 4: without Reference or Ref, else as it is.
        [OxQLReference("rel.tour")]
        public Guid? OwnerRef { get; set; }

        [OxQLReference("rel.tour")]
        public Guid? Reference { get; set; }

        // Rule 1: the navigation property names the relation of its id member.
        public Guid? DepotGuid { get; set; }

        [ReferenceId(nameof(DepotGuid))]
        public RelTour? HomeDepot { get; set; }

        // Rule 2: a key member of an embedded object, or of the objects of a collection, is named at the slot.
        public RelSource? SourceBillingLineReference { get; set; }

        public RelSource? TargetRef { get; set; }

        public List<RelResource> Resources { get; set; } = [];

        public List<RelItem> Items { get; set; } = [];

        // No reference, no name.
        public string Number { get; set; } = "";

        public RelPlain? Plain { get; set; }
    }

    /// <summary>One pooled type in two slots: the name is the slot's, not the type's.</summary>
    public class RelSource
    {
        public string Type { get; set; } = "";

        [OxQLReference("rel.tour")]
        public Guid Id { get; set; }
    }

    public class RelResource
    {
        [OxQLReference("rel.vehicle")]
        public Guid Id { get; set; }
    }

    public class RelItem
    {
        [OxQLReference("rel.tour")]
        public Guid? BillingLineId { get; set; }

        public List<RelItemReference> References { get; set; } = [];

        // Both key members carry a reference: the slot names the first (id), the other keeps its own name.
        public RelTwoKeys? Pair { get; set; }
    }

    /// <summary>A key member that is not <c>id</c>: <c>referenceId</c> is the other one a slot names.</summary>
    public class RelItemReference
    {
        [OxQLReference("rel.tour")]
        public Guid? ReferenceId { get; set; }
    }

    public class RelTwoKeys
    {
        [OxQLReference("rel.tour")]
        public Guid Id { get; set; }

        [OxQLReference("rel.vehicle")]
        public Guid? ReferenceId { get; set; }
    }

    public class RelPlain
    {
        public Guid Id { get; set; }
    }

    /// <summary>Names two members derive alike, which then are nobody's.</summary>
    public class RelCollisions
    {
        public Guid Id { get; set; }

        // Both derive `driver`: each takes its own wire name.
        [OxQLReference("rel.tour")]
        public Guid? DriverId { get; set; }

        [OxQLReference("rel.tour")]
        public Guid? DriverRef { get; set; }

        // A leaf and a slot derive `source`.
        [OxQLReference("rel.tour")]
        public Guid? SourceId { get; set; }

        public RelSource? SourceReference { get; set; }

        // `partnerId` and `partnerKey` derive `partner`; `partnerIdKey` derives `partnerId`, the name the first fell back to.
        [OxQLReference("rel.tour")]
        public Guid? PartnerId { get; set; }

        [OxQLReference("rel.tour")]
        public Guid? PartnerKey { get; set; }

        [OxQLReference("rel.tour")]
        public Guid? PartnerIdKey { get; set; }

        // A stored member of the derived name is no collision: names are a namespace of their own.
        [OxQLReference("rel.tour")]
        public Guid? UpdateUserId { get; set; }

        public string? UpdateUser { get; set; }

        // A root key member with a reference is named by itself.
        [OxQLReference("rel.vehicle")]
        public Guid? ReferenceId { get; set; }
    }

    public class RelTour
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = "";
    }

    public class RelVehicle
    {
        public Guid Id { get; set; }
    }

    public class RelContact
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = "";
    }

    internal static class RelationModel
    {
        public const string Transaction = "rel.transaction";
        public const string Collisions = "rel.collisions";

        public static readonly Lazy<EntityModel> Model = new(() => ClrModelBuilder.Build(
        [
            new EntityDeclaration(Transaction, Transaction, typeof(RelTransaction), "transactions", null, false),
            new EntityDeclaration(Collisions, Collisions, typeof(RelCollisions), "collisions", null, false),
            new EntityDeclaration("rel.tour", "rel.tour", typeof(RelTour), "tours", null, false),
            new EntityDeclaration("rel.vehicle", "rel.vehicle", typeof(RelVehicle), "vehicles", null, false),
            new EntityDeclaration("rel.contact", "rel.contact", typeof(RelContact), "contacts", null, false),
        ], retiredIds: null, new ReferenceDeclarations()));
    }
}

namespace OxQL.Tests.Model
{
    using OxQL.Tests.Model.Fixtures.Relations;

    /// <summary>
    /// The relation names the engine derives (improvement plan §3.N): one name per reference, by the
    /// model alone, published as the schema document's <c>relation</c>. The cases of the first theory
    /// are the studio's own (<c>libs/util/oxql-schema/src/lib/relation-names.spec.ts</c>): the engine's
    /// rule answers every one of them alike.
    /// </summary>
    public class RelationNamesTests
    {
        private static EntityDef Entity(string id) => RelationModel.Model.Value.Entities[id];

        private static string? NameAt(string entity, string path)
        {
            var holder = Entity(entity);

            return RelationNames.At(holder, holder.PathIndex[path]);
        }

        [Theory]
        // rule 1
        [InlineData("startAddressId", "startAddress", "startAddress")]
        [InlineData("startAddressId", "StartAddress", "startAddress")]
        // rule 3
        [InlineData("tourId", null, "tour")]
        [InlineData("vehicleIds", null, "vehicles")]
        [InlineData("billingLineId", null, "billingLine")]
        [InlineData("contactNumber", null, "contact")]
        [InlineData("accountKey", null, "account")]
        [InlineData("referenceId", null, "reference")]
        // rule 4
        [InlineData("ownerRef", null, "owner")]
        [InlineData("ownerReference", null, "owner")]
        [InlineData("reference", null, "reference")]
        // a suffix is stripped only when something is left
        [InlineData("id", null, "id")]
        [InlineData("ids", null, "ids")]
        [InlineData("key", null, "key")]
        [InlineData("number", null, "number")]
        [InlineData("ref", null, "ref")]
        // one suffix, the first that ends the name; the case of the rest is kept
        [InlineData("tourIdNumber", null, "tourId")]
        [InlineData("MainTourId", null, "mainTour")]
        [InlineData("paid", null, "paid")]
        public void A_reference_member_is_named_by_its_navigation_property_else_by_its_own_name_without_its_suffix(string wireName, string? navigation, string expected) =>
            RelationNames.OfMember(wireName, navigation).Should().Be(expected);

        [Theory]
        [InlineData("sourceBillingLineReference", "sourceBillingLine")]
        [InlineData("targetRef", "target")]
        [InlineData("resources", "resources")]
        [InlineData("references", "references")]
        [InlineData("reference", "reference")]
        [InlineData("Resource", "resource")]
        public void A_slot_is_named_by_its_own_name_without_Reference_or_Ref(string slot, string expected) =>
            RelationNames.OfSlot(slot).Should().Be(expected);

        [Theory]
        // the studio's cases, by path
        [InlineData("sourceBillingLineReference.id", "sourceBillingLine")]
        [InlineData("resources.id", "resources")]
        [InlineData("items.references.referenceId", "references")]
        [InlineData("tourId", "tour")]
        [InlineData("vehicleIds", "vehicles")]
        [InlineData("items.billingLineId", "billingLine")]
        [InlineData("contactNumber", "contact")]
        [InlineData("accountKey", "account")]
        [InlineData("ownerRef", "owner")]
        [InlineData("reference", "reference")]
        // one pooled type in another slot
        [InlineData("targetRef.id", "target")]
        // the navigation property
        [InlineData("depotGuid", "homeDepot")]
        // the slot names its first key member; the other one is named by itself
        [InlineData("items.pair.id", "pair")]
        [InlineData("items.pair.referenceId", "reference")]
        public void The_model_names_the_relation_at_every_reference_path(string path, string expected) =>
            NameAt(RelationModel.Transaction, path).Should().Be(expected);

        [Theory]
        [InlineData("number")]
        [InlineData("id")]
        [InlineData("items")]
        [InlineData("plain")]
        [InlineData("plain.id")]
        [InlineData("sourceBillingLineReference.type")]
        [InlineData("homeDepot")]
        public void A_path_that_carries_no_reference_has_no_relation_name(string path) =>
            NameAt(RelationModel.Transaction, path).Should().BeNull();

        [Fact]
        public void The_name_lies_on_the_member_a_reader_finds_it_at_the_slot_for_a_key_member_else_the_member_itself()
        {
            var transaction = Entity(RelationModel.Transaction).Root;

            transaction.Member("tourId")!.Relation.Should().Be(new RelationDef("tour"));
            transaction.Member("sourceBillingLineReference")!.Relation.Should().Be(new RelationDef("sourceBillingLine", "id"));
            transaction.Member("resources")!.Relation.Should().Be(new RelationDef("resources", "id"));
            transaction.Member("number")!.Relation.Should().BeNull();
            transaction.Member("plain")!.Relation.Should().BeNull("its key member carries no reference");

            // The key member itself keeps the name it has where no slot stands above it.
            var source = transaction.Member("sourceBillingLineReference")!.Type!;

            source.Member("id")!.Relation.Should().Be(new RelationDef("id"));

            var item = transaction.Member("items")!.Of!.Type!;

            item.Member("references")!.Relation.Should().Be(new RelationDef("references", "referenceId"));
            item.Member("pair")!.Relation.Should().Be(new RelationDef("pair", "id"), "id is looked for before referenceId");
        }

        [Fact]
        public void A_name_two_members_derive_is_nobodys_each_takes_its_own_wire_name()
        {
            NameAt(RelationModel.Collisions, "driverId").Should().Be("driverId");
            NameAt(RelationModel.Collisions, "driverRef").Should().Be("driverRef");

            // A leaf and a slot.
            NameAt(RelationModel.Collisions, "sourceId").Should().Be("sourceId");
            NameAt(RelationModel.Collisions, "sourceReference.id").Should().Be("sourceReference");

            // The name one fell back to is taken, so the member that derived it takes its own too.
            NameAt(RelationModel.Collisions, "partnerId").Should().Be("partnerId");
            NameAt(RelationModel.Collisions, "partnerKey").Should().Be("partnerKey");
            NameAt(RelationModel.Collisions, "partnerIdKey").Should().Be("partnerIdKey");

            // A stored member of the same name is no collision, and a root key member is named by itself.
            NameAt(RelationModel.Collisions, "updateUserId").Should().Be("updateUser");
            NameAt(RelationModel.Collisions, "referenceId").Should().Be("reference");

            var names = Entity(RelationModel.Collisions).Root.Members.Where(member => member.Relation is not null).Select(member => member.Relation!.Name).ToList();

            names.Should().OnlyHaveUniqueItems("a relation name is unique among those of its type");
        }

        [Fact]
        public void Deriving_names_reports_nothing_and_leaves_the_fingerprint_alone()
        {
            var model = RelationModel.Model.Value;

            model.Findings.Should().BeEmpty("a name is derived silently: nothing is declared, so nothing can be wrong");

            // The fingerprint describes what a query binds against; a name is a label.
            var fingerprint = model.Fingerprint;

            Entity(RelationModel.Transaction).Root.Member("tourId")!.Relation.Should().NotBeNull();
            ClrModelBuilder.Build(
            [
                new EntityDeclaration(RelationModel.Transaction, RelationModel.Transaction, typeof(RelTransaction), "transactions", null, false),
                new EntityDeclaration(RelationModel.Collisions, RelationModel.Collisions, typeof(RelCollisions), "collisions", null, false),
                new EntityDeclaration("rel.tour", "rel.tour", typeof(RelTour), "tours", null, false),
                new EntityDeclaration("rel.vehicle", "rel.vehicle", typeof(RelVehicle), "vehicles", null, false),
                new EntityDeclaration("rel.contact", "rel.contact", typeof(RelContact), "contacts", null, false),
            ], retiredIds: null, new ReferenceDeclarations()).Fingerprint.Should().Be(fingerprint);
        }

        [Fact]
        public void A_schema_document_carries_the_names_and_its_reader_takes_them_as_published()
        {
            // A document names what its publisher derived; here a name no rule would give (a navigation property's).
            var document = """
                {
                  "schemaVersion": "1.1",
                  "service": "rel",
                  "types": {
                    "rel.trip": {
                      "entity": true,
                      "properties": [
                        { "name": "id", "kind": "guid", "nullable": false },
                        { "name": "depotGuid", "kind": "guid", "nullable": true, "references": { "entity": "rel.depot", "field": "id", "joinable": true, "inferred": false }, "relation": { "name": "homeDepot" } },
                        { "name": "origin", "kind": "object", "type": "#/types/t_origin", "nullable": true, "relation": { "name": "origin", "member": "id" } },
                        { "name": "tourId", "kind": "guid", "nullable": true, "references": { "entity": "rel.depot", "field": "id", "joinable": true, "inferred": false } }
                      ]
                    },
                    "t_origin": {
                      "properties": [
                        { "name": "id", "kind": "guid", "nullable": false, "references": { "entity": "rel.depot", "field": "id", "joinable": true, "inferred": false }, "relation": { "name": "id" } }
                      ]
                    },
                    "rel.depot": {
                      "entity": true,
                      "properties": [
                        { "name": "id", "kind": "guid", "nullable": false },
                        { "name": "ownerId", "kind": "guid", "nullable": true, "references": { "entity": "rel.trip", "field": "id", "joinable": true, "inferred": false } }
                      ]
                    }
                  }
                }
                """;

            var model = DocumentModelBuilder.Build(document);
            var trip = model.Entities["rel.trip"];

            RelationNames.At(trip, trip.PathIndex["depotGuid"]).Should().Be("homeDepot", "the published name stands; the rule alone would say depotGuid");
            RelationNames.At(trip, trip.PathIndex["origin.id"]).Should().Be("origin");
            trip.Root.Member("tourId")!.Relation.Should().BeNull("a type that publishes names is taken as published, member by member");

            // A type whose document names nothing (an earlier format) is named by this engine's rule.
            var depot = model.Entities["rel.depot"];

            RelationNames.At(depot, depot.PathIndex["ownerId"]).Should().Be("owner");
        }

        [Fact]
        public void The_studios_cases_are_answered_alike_whichever_side_derives()
        {
            // relation-names.spec.ts, `relationName`: [path, navigation, expected]. The engine's two functions
            // are the studio's one: the slot rule where the path has a slot above a key member, else the member rule.
            (string Path, string? Navigation, string Expected)[] cases =
            [
                ("startAddressId", "StartAddress", "startAddress"),
                ("billingLine.sourceBillingLineReference.id", null, "sourceBillingLine"),
                ("resources.id", null, "resources"),
                ("items.references.referenceId", null, "references"),
                ("tourId", null, "tour"),
                ("vehicleIds", null, "vehicles"),
                ("items.billingLineId", null, "billingLine"),
                ("contactNumber", null, "contact"),
                ("accountKey", null, "account"),
                ("referenceId", null, "reference"),
                ("ownerRef", null, "owner"),
                ("reference", null, "reference"),
                ("id", null, "id"),
            ];

            foreach (var (path, navigation, expected) in cases)
            {
                var segments = path.Split('.');
                var leaf = segments[^1];
                var derived = navigation is null && segments.Length > 1 && RelationNames.SlotKeys.Contains(leaf)
                    ? RelationNames.OfSlot(segments[^2])
                    : RelationNames.OfMember(leaf, navigation);

                derived.Should().Be(expected, path);
            }
        }
    }
}
