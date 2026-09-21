using FluentAssertions;
using OxQL.Core.Attributes;
using OxQL.Core.Binding;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.Tests.Model
{
    /// <summary>
    /// The scan binds <see cref="OxQLTypeAttribute"/> by type: one attribute, in one assembly,
    /// reachable under the assembly name entity classes were first compiled against.
    /// </summary>
    public class CoreHardeningEntityScanTests
    {
        [Fact]
        public void The_attribute_lives_with_the_scan_that_reads_it() =>
            typeof(OxQLTypeAttribute).Assembly.Should().BeSameAs(typeof(EntityScanner).Assembly);

        [Fact]
        public void An_entity_compiled_against_the_core_assembly_binds_to_the_same_attribute()
        {
            var core = typeof(Codes).Assembly;

            core.GetForwardedTypes().Should().Contain(typeof(OxQLTypeAttribute));
            Type.GetType($"OxQL.Core.Attributes.OxQLTypeAttribute, {core.GetName().Name}", throwOnError: true).Should().Be(typeof(OxQLTypeAttribute));
        }

        [Fact]
        public void An_attribute_that_only_shares_the_name_declares_no_entity_and_breaks_no_scan()
        {
            var findings = new List<BuildFinding>();
            var declarations = EntityScanner.Scan([typeof(Lookalike.Decoy).Assembly], findings);

            declarations.Should().NotContain(declaration => declaration.ClrType == typeof(Lookalike.Decoy));
            declarations.Should().Contain(declaration => declaration.Id == "probe.order", "the real declarations beside it are read as ever");
            findings.Should().NotContain(finding => finding.Code == BuildCodes.EntityScanFailed);
        }

        [Fact]
        public void A_declaration_is_read_from_the_attribute_itself()
        {
            var supplier = EntityScanner.Scan([typeof(Fixtures.SupplierModel).Assembly], []).Single(declaration => declaration.Id == "probe.supplier");

            supplier.DeclaredId.Should().Be("probe.supplier");
            supplier.Collection.Should().Be("suppliers");
            supplier.Database.Should().Be("purchasing");
            supplier.Extendable.Should().BeFalse();

            EntityScanner.Scan([typeof(Fixtures.OrderModel).Assembly], []).Single(declaration => declaration.Id == "probe.order").Extendable.Should().BeTrue();
        }
    }
}

namespace OxQL.Tests.Model.Lookalike
{
    /// <summary>Shares the attribute's simple name and none of its members.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class OxQLTypeAttribute : Attribute;

    [OxQLType]
    public class Decoy
    {
        public Guid Id { get; set; }
    }
}
