using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OxQL.Core;
using OxQL.Core.Models;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Core;

/// <summary>The collation is configured under <c>Representation:Collation</c>, defaults to German at primary strength, and is brought into the range the database accepts.</summary>
public class CaseInsensitiveOptionsTests
{
    [Fact]
    public void The_default_collation_folds_case_and_accents_in_German()
    {
        var options = new OxQLOptions();

        options.Representation.Collation.Locale.Should().Be("de");
        options.Representation.Collation.Strength.Should().Be(1);
        options.Normalise().Should().BeEmpty();
    }

    [Fact]
    public void The_section_binds_the_collation()
    {
        var section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OxQL:Representation:Collation:Locale"] = "en",
            ["OxQL:Representation:Collation:Strength"] = "2",
            ["OxQL:Cursor:SigningKey"] = BindHost.SigningKey,
        }).Build().GetSection("OxQL");

        using var provider = new ServiceCollection().AddOxQLCore(section).BuildServiceProvider();
        var collation = provider.GetRequiredService<OxQLOptions>().Representation.Collation;

        collation.Locale.Should().Be("en");
        collation.Strength.Should().Be(2);
        provider.GetServices<OxQLOptionsAdjustment>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(6, 5)]
    public void A_strength_outside_the_databases_range_is_clamped_and_named(int configured, int expected)
    {
        var options = new OxQLOptions { Representation = { Collation = { Strength = configured } } };

        var adjustments = options.Normalise();

        options.Representation.Collation.Strength.Should().Be(expected);
        adjustments.Should().ContainSingle().Which.Should().Contain("Collation:Strength").And.Contain(configured.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_locale_falls_back_to_the_default_and_is_named(string configured)
    {
        var options = new OxQLOptions { Representation = { Collation = { Locale = configured } } };

        var adjustments = options.Normalise();

        options.Representation.Collation.Locale.Should().Be("de");
        adjustments.Should().ContainSingle().Which.Should().Contain("Collation:Locale");
    }
}
