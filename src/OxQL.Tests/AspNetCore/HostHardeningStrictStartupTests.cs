using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Resolve;
using OxQL.Core.Engine;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// Whether a remote reference without a configured service stops the host is decided by data
/// the host carries — its environment name and its continuous-integration option — and never by
/// a variable the machine happens to define while the host runs.
/// </summary>
public class HostHardeningStrictStartupTests
{
    private static SampleHost HostWith(string environment, bool continuousIntegration) =>
        new(configure: services => services.AddSingleton<IRemoteQueryClient>(new FakeRemoteClient { Configured = ["vehicle"] }))
        {
            Environment = environment,
            ContinuousIntegration = continuousIntegration,
        };

    [Fact]
    public void A_production_host_under_continuous_integration_refuses_to_start_on_a_finding()
    {
        using var host = HostWith("Production", continuousIntegration: true);

        var act = () => host.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*InternalHosts*crm*");
    }

    [Fact]
    public async Task A_production_host_outside_continuous_integration_logs_the_finding_and_serves()
    {
        using var host = HostWith("Production", continuousIntegration: false);

        var response = await host.CreateClient().GetAsync("/OxQL/health?shallow=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Logs.Entries.Should().Contain(entry => entry.Category == "OxQL.Startup" && entry.Message.Contains("'crm'"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Local")]
    public void A_strict_environment_refuses_to_start_whatever_the_option_says(string environment)
    {
        using var host = HostWith(environment, continuousIntegration: false);

        var act = () => host.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*InternalHosts*crm*");
    }

    [Theory]
    [InlineData("true", null, true)]
    [InlineData("1", null, true)]
    [InlineData(null, "True", true)]
    [InlineData("false", "True", true)]
    [InlineData(null, null, false)]
    [InlineData("", " ", false)]
    [InlineData("0", "false", false)]
    [InlineData("FALSE", "0", false)]
    public void Continuous_integration_is_read_from_the_variables_of_both_kinds_of_build_server(string? ci, string? tfBuild, bool expected)
    {
        RemoteReferenceCheck.ReadContinuousIntegration(ci, tfBuild).Should().Be(expected);
        RemoteReferenceCheck.IsStrict("Production", RemoteReferenceCheck.ReadContinuousIntegration(ci, tfBuild)).Should().Be(expected);
    }

    [Fact]
    public void Strictness_is_the_environment_or_continuous_integration()
    {
        RemoteReferenceCheck.IsStrict("Production", continuousIntegration: false).Should().BeFalse();
        RemoteReferenceCheck.IsStrict("Production", continuousIntegration: true).Should().BeTrue();
        RemoteReferenceCheck.IsStrict("development", continuousIntegration: false).Should().BeTrue();
        RemoteReferenceCheck.IsStrict(null, continuousIntegration: false).Should().BeFalse();
    }
}
