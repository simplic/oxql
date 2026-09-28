using FluentAssertions;
using MongoDB.Bson.Serialization;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures.VariantMerge;
using Xunit;

namespace OxQL.Tests.Model.Fixtures.VariantMerge
{
    /// <summary>A holder of a polymorphic member whose variants declare the same member with different references.</summary>
    public class RvHolder
    {
        public Guid Id { get; set; }
        public RvResource? Resource { get; set; }
        public RvTagged? Tagged { get; set; }
        public RvThing? Thing { get; set; }
    }

    public abstract class RvResource
    {
        public string? Name { get; set; }
    }

    public class RvDriver : RvResource
    {
        [OxQLReference("rv.employee")]
        public Guid RefId { get; set; }
    }

    public class RvTruck : RvResource
    {
        [OxQLReference("rv.vehicle")]
        public Guid RefId { get; set; }
    }

    /// <summary>The same member without a reference: its rows resolve by no case.</summary>
    public class RvPlain : RvResource
    {
        public Guid RefId { get; set; }
    }

    public abstract class RvTagged
    {
        public string Type { get; set; } = "";
    }

    /// <summary>A case on a sibling path, which a variant condition cannot be combined with.</summary>
    public class RvTaggedA : RvTagged
    {
        [OxQLReferenceWhen("type", "e", "rv.employee")]
        public Guid RefId { get; set; }
    }

    public class RvTaggedB : RvTagged
    {
        [OxQLReference("rv.vehicle")]
        public Guid RefId { get; set; }
    }

    /// <summary>Two variants whose simple names collide.</summary>
    public abstract class RvThing
    {
        public string? Label { get; set; }
    }

    public static class RvOne
    {
        public class Item : RvThing
        {
            public int One { get; set; }
        }
    }

    public static class RvTwo
    {
        public class Item : RvThing
        {
            public int Two { get; set; }
        }
    }

    /// <summary>A variant beside the colliding pair, described as usual.</summary>
    public class RvOther : RvThing
    {
        public int Three { get; set; }
    }

    public class RvEmployee
    {
        public Guid Id { get; set; }
    }

    public class RvVehicle
    {
        public Guid Id { get; set; }
    }

    internal static class VariantMergeModel
    {
        public const string Holder = "rv.holder";

        /// <summary>Registers the variants' class maps; <see cref="ModelTestSetup"/> calls it when the test assembly loads.</summary>
        internal static void Register()
        {
            foreach (var type in new[] { typeof(RvDriver), typeof(RvTruck), typeof(RvPlain), typeof(RvTaggedA), typeof(RvTaggedB), typeof(RvOne.Item), typeof(RvTwo.Item), typeof(RvOther) })
                if (!BsonClassMap.IsClassMapRegistered(type))
                    BsonClassMap.RegisterClassMap(new BsonClassMap(type).Also(map => map.AutoMap()));
        }

        private static readonly Lazy<EntityModel> model = new(() =>
        {
            return ClrModelBuilder.Build(
            [
                new EntityDeclaration(Holder, Holder, typeof(RvHolder), "holders", null, false),
                new EntityDeclaration("rv.employee", "rv.employee", typeof(RvEmployee), "employees", null, false),
                new EntityDeclaration("rv.vehicle", "rv.vehicle", typeof(RvVehicle), "vehicles", null, false),
            ]);
        });

        public static EntityModel Model => model.Value;

        private static T Also<T>(this T value, Action<T> action)
        {
            action(value);
            return value;
        }
    }
}

namespace OxQL.Tests.Model
{
    /// <summary>
    /// A member some variants of a polymorphic type declare with different references (RE-1), and
    /// variants whose names collide (RE-10): the merged member resolves each row by its own variant's
    /// declaration, never by the first variant's for every row.
    /// </summary>
    public class VariantReferenceMergeTests
    {
        private static EntityModel Model => VariantMergeModel.Model;

        private static PathDef Path(string path) => Model.Entities[VariantMergeModel.Holder].Path(path)!;

        [Fact]
        public void Each_carriers_reference_is_conditioned_on_its_own_variant()
        {
            var cases = Path("resource.refId").References!;

            cases.Should().HaveCount(2, "the driver's and the truck's; the plain variant declares none");
            cases.Select(declared => ((ReferenceCondition.Variant)declared.When!).Names.Single()).Should().Equal("RvDriver", "RvTruck");
            cases.Select(declared => declared.Targets.Single().Entity).Should().Equal("rv.employee", "rv.vehicle");
            Path("resource.refId").Reference.Should().BeNull("a conditioned reference is not a simple one");
            Model.Findings.Should().NotContain(finding => finding.Target == "t_rvResource#refId");
        }

        [Fact]
        public void Carriers_disagreeing_with_a_sibling_path_case_emit_no_reference_and_a_finding()
        {
            Path("tagged.refId").References.Should().BeNullOrEmpty();
            Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.ReferenceDeclarationUnresolved && finding.Target.EndsWith("#refId") && finding.Message.Contains("RvTaggedA"));
        }

        [Fact]
        public void Variants_sharing_a_name_are_a_finding_and_neither_is_a_variant()
        {
            var thing = Path("thing").Shape.Type!;

            thing.Variants.Select(variant => variant.Name).Should().Equal("RvOther");
            Path("thing.one").Should().BeNull();
            Path("thing.two").Should().BeNull();
            Path("thing.three").Should().NotBeNull();
            Model.Findings.Should().ContainSingle(finding => finding.Code == BuildCodes.PolymorphicVariantNameConflict).Which.Detail.Should().Be("Item");
        }
    }
}
