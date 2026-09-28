using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OxQL.Core;
using OxQL.Core.Models;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Core;

/// <summary>
/// The OxQL 2.1 limits (DESIGN §3.7): their defaults, the lines <see cref="OxQLOptions.Normalise"/>
/// adds for them, and their binding from the host's section.
/// </summary>
public class ReportLimitsOptionsTests
{
    [Fact]
    public void The_shipped_defaults_are_the_2_1_values_and_need_no_adjustment()
    {
        var options = new OxQLOptions();

        options.Limits.MaxResolveStages.Should().Be(8);
        options.Limits.MaxResolveKeys.Should().Be(10_000);
        options.Limits.MaxContinuedStages.Should().Be(8);
        options.Limits.MaxFlattenDepth.Should().Be(5);
        options.Limits.MaxReportPageSize.Should().Be(5_000);
        options.Limits.MaxReportedRows.Should().Be(50);
        options.Limits.MaxLookupLimit.Should().Be(100);
        options.Execution.ChainTimeoutMs.Should().Be(6_000);
        options.Execution.EffectiveChainTimeoutMs.Should().Be(6_000);
        options.Cache.NegativeResolveTtlSeconds.Should().Be(10);

        options.Normalise().Should().BeEmpty();
    }

    [Fact]
    public void The_resolve_key_cap_stays_above_every_page_so_its_diagnostic_is_dormant_at_the_defaults()
    {
        var limits = new LimitOptions();

        limits.MaxResolveKeys.Should().BeGreaterThanOrEqualTo(limits.MaxPageSize).And.BeGreaterThanOrEqualTo(limits.MaxReportPageSize);
    }

    [Fact]
    public void A_chain_timeout_below_one_millisecond_is_raised_and_named_under_its_own_section()
    {
        var options = new OxQLOptions { Execution = { ChainTimeoutMs = 0 } };

        var adjustments = options.Normalise();

        options.Execution.ChainTimeoutMs.Should().Be(1);
        adjustments.Should().ContainSingle().Which.Should().StartWith("OxQL:Execution:ChainTimeoutMs was 0");
    }

    [Fact]
    public void The_chain_timeout_is_bounded_by_the_request_ceiling()
    {
        var options = new OxQLOptions { Execution = { MaxTimeMs = 3_000, ChainTimeoutMs = 6_000 } };

        options.Execution.EffectiveChainTimeoutMs.Should().Be(3_000);
    }

    [Fact]
    public void A_negative_ttl_is_raised_to_zero_which_caches_no_missing_key()
    {
        var zero = new OxQLOptions { Cache = { NegativeResolveTtlSeconds = 0 } };
        var negative = new OxQLOptions { Cache = { NegativeResolveTtlSeconds = -3 } };

        zero.Normalise().Should().BeEmpty("0 turns the negative cache off and is a valid choice");
        negative.Normalise().Should().ContainSingle().Which.Should().StartWith("OxQL:Cache:NegativeResolveTtlSeconds was -3");
        negative.Cache.NegativeResolveTtlSeconds.Should().Be(0);
    }

    [Fact]
    public void More_continued_stages_than_a_pipeline_can_carry_are_clamped_to_the_pipeline_limit()
    {
        var options = new OxQLOptions { Limits = { MaxContinuedStages = 50 } };

        var adjustments = options.Normalise();

        options.Limits.MaxContinuedStages.Should().Be(options.Limits.MaxPipelineStages);
        adjustments.Should().ContainSingle().Which.Should().Contain("MaxContinuedStages was 50");
    }

    [Fact]
    public void A_report_page_below_the_page_size_is_left_as_configured_because_it_never_shrinks_a_page()
    {
        var options = new OxQLOptions { Limits = { MaxReportPageSize = 100 } };

        options.Normalise().Should().BeEmpty();
        options.Limits.MaxReportPageSize.Should().Be(100);
    }

    [Fact]
    public void The_registration_binds_the_2_1_limits()
    {
        var section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OxQL:Limits:MaxReportPageSize"] = "2000",
            ["OxQL:Limits:MaxReportedRows"] = "7",
            ["OxQL:Limits:MaxContinuedStages"] = "3",
            ["OxQL:Execution:ChainTimeoutMs"] = "4000",
            ["OxQL:Cache:NegativeResolveTtlSeconds"] = "0",
            ["OxQL:Cursor:SigningKey"] = BindHost.SigningKey,
        }).Build().GetSection("OxQL");

        using var provider = new ServiceCollection().AddOxQLCore(section).BuildServiceProvider();
        var options = provider.GetRequiredService<OxQLOptions>();

        options.Limits.MaxReportPageSize.Should().Be(2_000);
        options.Limits.MaxReportedRows.Should().Be(7);
        options.Limits.MaxContinuedStages.Should().Be(3);
        options.Execution.ChainTimeoutMs.Should().Be(4_000);
        options.Cache.NegativeResolveTtlSeconds.Should().Be(0);
    }
}
