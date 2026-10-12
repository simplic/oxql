using FluentAssertions;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>
/// A finding about a pooled type names the id the pool publishes it under, also where two types share
/// a simple name and the pool tells them apart by a tail; each of the two keeps its own findings.
/// </summary>
public class PooledTypeFindingTests
{
    public static class Billing
    {
        public sealed class Address
        {
            public string Street { get; set; } = "";

            public System.Collections.ArrayList Notes { get; set; } = [];
        }
    }

    public static class Shipping
    {
        public sealed class Address
        {
            public string City { get; set; } = "";

            public System.Collections.ArrayList Notes { get; set; } = [];
        }
    }

    public sealed class Party
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public Billing.Address? Billed { get; set; }

        public Shipping.Address? Shipped { get; set; }
    }

    [Fact]
    public void Two_types_of_one_simple_name_each_keep_their_findings_under_their_published_id()
    {
        var model = ClrModelBuilder.Build([new EntityDeclaration("pf.party", "pf.party", typeof(Party), "tmp_pf_parties", null, false)]);

        var billed = model.TypePool.Single(pair => pair.Value.ClrType == typeof(Billing.Address)).Key;
        var shipped = model.TypePool.Single(pair => pair.Value.ClrType == typeof(Shipping.Address)).Key;

        billed.Should().NotBe(shipped).And.StartWith("t_address_");
        model.Findings.Where(finding => finding.Code == BuildCodes.CollectionUntyped).Select(finding => finding.Target)
            .Should().BeEquivalentTo([$"{billed}#notes", $"{shipped}#notes"]);
        model.Findings.Should().OnlyContain(finding => !finding.ToString().Contains('\u0001'));
    }
}
