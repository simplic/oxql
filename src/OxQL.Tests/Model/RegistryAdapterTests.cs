using FluentAssertions;
using OxQL.Core.Registration;
using OxQL.Model;
using OxQL.Tests.Model.Fixtures;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>The v1 registry is a thin adapter over the model's discovery.</summary>
public class RegistryAdapterTests
{
    [Fact]
    public void Scanning_registers_exactly_the_entities_the_model_describes()
    {
        var registry = new OxQLTypeRegistry().ScanAssemblies(typeof(OrderModel).Assembly);

        registry.Registrations.Select(registration => registration.TypeName).Should().BeEquivalentTo(
            "probe.base_only", "probe.customer", "probe.keyless", "probe.order", "probe.shared_base", "probe.supplier");
        registry.Findings.Select(finding => finding.Code).Should().Contain(BuildCodes.DuplicateEntityId).And.Contain(BuildCodes.EntityTypeShared);

        registry.TryGet("probe.order", out var order).Should().BeTrue();
        order.ClrType.Should().Be(typeof(OrderModel));
        order.CollectionName.Should().Be("orders");
        order.Extendable.Should().BeTrue();
        order.DatabaseName.Should().BeNull();

        registry.TryGet("probe.base_only", out var baseOnly).Should().BeTrue();
        baseOnly.ClrType.Should().Be(typeof(MoreDerived), "the most derived subclass rule is the model's");

        registry.GetDatabaseName("probe.supplier").Should().Be("purchasing");
        registry.IsExtendable("probe.customer").Should().BeFalse();
    }

    [Fact]
    public void The_v1_lookup_stays_case_insensitive_until_its_callers_move_to_the_model()
    {
        var registry = new OxQLTypeRegistry().ScanAssemblies(typeof(OrderModel).Assembly);

        registry.TryGet("PROBE.ORDER", out _).Should().BeTrue();
        registry.GetCollectionName("Probe.Order").Should().Be("orders");
        registry.TryGet("probe.twin", out _).Should().BeFalse("a duplicate id is dropped for every claimant");
    }

    [Fact]
    public void A_registry_over_a_model_mirrors_its_entities()
    {
        var registry = new OxQLTypeRegistry(ProbeModel.Clr);

        registry.Registrations.Select(registration => registration.TypeName).Should().BeEquivalentTo(ProbeModel.Clr.Entities.Keys);
        registry.TryGet("probe.supplier", out var supplier).Should().BeTrue();
        supplier.DatabaseName.Should().Be("purchasing");
        supplier.ClrType.Should().Be(typeof(SupplierModel));

        var fromDocument = new OxQLTypeRegistry(ProbeModel.Document());

        fromDocument.TryGet("probe.order", out var order).Should().BeTrue();
        order.ClrType.Should().BeNull();
        order.CollectionName.Should().Be("orders");
    }

    [Fact]
    public void Manual_registration_is_unchanged()
    {
        var registry = new OxQLTypeRegistry().Register("manual.thing", "things", "elsewhere");

        registry.TryGet("manual.thing", out var thing).Should().BeTrue();
        thing.ClrType.Should().BeNull();
        thing.CollectionName.Should().Be("things");
        thing.DatabaseName.Should().Be("elsewhere");
    }
}
