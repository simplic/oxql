using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

    // ---- one line per service ------------------------------------------------------------------

    [Fact]
    public void The_findings_are_said_once_per_service_with_the_configuration_to_add()
    {
        RemoteReference[] findings =
        [
            new("logistics.tour", "driver.id", "hr.employee"),
            new("logistics.tour", "codriver.id", "hr.employee"),
            new("logistics.shipment", "vehicleId", "vehicle.vehicle"),
            new("logistics.tour", "resource.id", "hr.employee"),
        ];

        var lines = RemoteReferenceCheck.DescribeByService(findings);

        lines.Should().HaveCount(2, "a line per service, not per reference path");
        lines[0].Should().StartWith("service 'hr' is not configured on this host: 3 declared references need it ('logistics.tour#driver.id' -> 'hr.employee' and 2 more)")
            .And.Contain("InternalHosts:hr").And.Contain("""  "InternalHosts": { "hr": "<host:port>" }  """.Trim())
            .And.Contain("when the owner does not answer on API version v1, also InternalApiVersions:hr").And.Contain("""  "InternalApiVersions": { "hr": "<version>" }  """.Trim());
        lines[1].Should().StartWith("service 'vehicle' is not configured on this host: 1 declared reference needs it ('logistics.shipment#vehicleId' -> 'vehicle.vehicle')")
            .And.Contain("InternalHosts:vehicle").And.Contain("InternalApiVersions:vehicle");
    }

    [Fact]
    public async Task A_host_logs_one_error_per_service_and_the_reference_paths_at_debug()
    {
        using var host = new SampleHost(configure: services =>
        {
            services.AddSingleton<IRemoteQueryClient>(new FakeRemoteClient { Configured = [] });
            services.Configure<Microsoft.Extensions.Logging.LoggerFilterOptions>(filter => filter.Rules.Add(new Microsoft.Extensions.Logging.LoggerFilterRule(null, "OxQL.Startup", Microsoft.Extensions.Logging.LogLevel.Debug, null)));
        })
        {
            Model = OxQL.Tests.Bind.Fixtures.Resolve.ResolveModel.Model,
            Environment = "Production",
        };

        (await host.CreateClient().GetAsync("/OxQL/health?shallow=true")).StatusCode.Should().Be(HttpStatusCode.OK);

        var startup = host.Logs.Entries.Where(entry => entry.Category == "OxQL.Startup").ToList();
        var references = RemoteReferences.Of(host.Model);

        references.Count.Should().BeGreaterThan(2, "the model declares more reference paths than services");
        startup.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error).Select(entry => entry.Message).Should().SatisfyRespectively(
            crm => crm.Should().Contain("service 'crm'").And.Contain("InternalHosts:crm"),
            transport => transport.Should().Contain("service 'transport'").And.Contain("InternalHosts:transport"));
        startup.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Debug && entry.Message.StartsWith("OxQL remote reference without a configured service", StringComparison.Ordinal))
            .Should().Be(references.Count, "every reference path is still listed for whoever asks");
    }

    // ---- strictness the host states ----------------------------------------------------------------

    [Theory]
    [InlineData("Development")]
    [InlineData("Local")]
    public async Task A_host_whose_configuration_says_not_strict_starts_in_a_strict_environment_and_still_logs_the_finding(string environment)
    {
        using var host = new SampleHost(options => options.Startup.StrictRemoteReferences = false, services => services.AddSingleton<IRemoteQueryClient>(new FakeRemoteClient { Configured = ["vehicle"] }))
        {
            Environment = environment,
            ContinuousIntegration = true,
        };

        var response = await host.Client().GetAsync("/OxQL/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Logs.Entries.Should().ContainSingle(entry => entry.Category == "OxQL.Startup" && entry.Level == Microsoft.Extensions.Logging.LogLevel.Error)
            .Which.Message.Should().Contain("service 'crm'");
        host.Logs.Entries.Should().NotContain(entry => entry.Category == "OxQL.Startup" && entry.Level == Microsoft.Extensions.Logging.LogLevel.Critical);

        var crm = (await SampleHost.Body(response))!["remote"]!.AsArray().Single(entry => entry!["service"]!.GetValue<string>() == "crm")!;

        crm["configured"]!.GetValue<bool>().Should().BeFalse("health keeps saying what is missing");
    }

    [Fact]
    public void A_host_whose_configuration_says_strict_refuses_to_start_in_production()
    {
        using var host = new SampleHost(options => options.Startup.StrictRemoteReferences = true, services => services.AddSingleton<IRemoteQueryClient>(new FakeRemoteClient { Configured = ["vehicle"] }))
        {
            Environment = "Production",
        };

        var act = () => host.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*service 'crm'*InternalHosts:crm*OxQL:Startup:StrictRemoteReferences*");
    }

    [Fact]
    public async Task What_the_host_sets_in_code_wins_over_its_configuration()
    {
        using var host = new SampleHost(options => options.Startup.StrictRemoteReferences = true, services =>
        {
            services.AddSingleton<IRemoteQueryClient>(new FakeRemoteClient { Configured = ["vehicle"] });
            services.PostConfigure<OxQL.AspNetCore.OxQLEndpointOptions>(endpoint => endpoint.StrictRemoteReferences = false);
        })
        {
            Environment = "Development",
        };

        (await host.CreateClient().GetAsync("/OxQL/health?shallow=true")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void The_option_is_bound_from_the_configuration_section()
    {
        static bool? Bound(string? value)
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["OxQL:Cursor:SigningKey"] = "test-signing-key", ["OxQL:Startup:StrictRemoteReferences"] = value })
                .Build();
            var services = new ServiceCollection();

            OxQL.Core.ServiceCollectionExtensions.AddOxQLCore(services, configuration.GetSection("OxQL"));

            return services.BuildServiceProvider().GetRequiredService<OxQL.Core.Models.OxQLOptions>().Startup.StrictRemoteReferences;
        }

        Bound("false").Should().BeFalse();
        Bound("true").Should().BeTrue();
        Bound(null).Should().BeNull("unset leaves the rule by environment");
    }

    [Fact]
    public void An_explicit_choice_decides_and_none_leaves_the_environment()
    {
        RemoteReferenceCheck.IsStrict(endpoint: null, configured: null, "Development", continuousIntegration: false).Should().BeTrue();
        RemoteReferenceCheck.IsStrict(endpoint: null, configured: null, "Production", continuousIntegration: false).Should().BeFalse();
        RemoteReferenceCheck.IsStrict(endpoint: null, configured: false, "Development", continuousIntegration: true).Should().BeFalse();
        RemoteReferenceCheck.IsStrict(endpoint: null, configured: true, "Production", continuousIntegration: false).Should().BeTrue();
        RemoteReferenceCheck.IsStrict(endpoint: true, configured: false, "Production", continuousIntegration: false).Should().BeTrue();
        RemoteReferenceCheck.IsStrict(endpoint: false, configured: true, "Development", continuousIntegration: true).Should().BeFalse();
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
