using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.Core.Engine;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The host with a remote query client installed: the capabilities, the health diagnostic per
/// referenced service, and the startup finding for a declared reference into a service the
/// host does not know.
/// </summary>
public class RemoteHostTests
{
    private static SampleHost HostWith(FakeRemoteClient client, string environment = "Development") =>
        new(configure: services => services.AddSingleton<IRemoteQueryClient>(client)) { Environment = environment };

    [Fact]
    public async Task Health_publishes_the_remote_capabilities_and_every_referenced_service()
    {
        var client = new FakeRemoteClient();
        client.Reachable.Add("crm");

        using var host = HostWith(client);

        var response = await host.CreateClient().GetAsync("/OxQL/health");
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!["capabilities"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Contain(["resolve.remote", "semiJoin"]);

        var remote = body["remote"]!.AsArray().Select(node => node!.AsObject()).ToList();

        remote.Select(entry => entry["service"]!.GetValue<string>()).Should().Equal("crm", "vehicle");
        remote[0]["configured"]!.GetValue<bool>().Should().BeTrue();
        remote[0]["reachable"]!.GetValue<bool>().Should().BeTrue();
        remote[1]["configured"]!.GetValue<bool>().Should().BeTrue();
        remote[1]["reachable"]!.GetValue<bool>().Should().BeFalse("the probe did not answer");
        body["status"]!.GetValue<string>().Should().Be("degraded", "an unreachable target is a health diagnostic");
    }

    [Fact]
    public void A_reference_into_an_unconfigured_service_stops_a_strict_host()
    {
        var client = new FakeRemoteClient { Configured = ["vehicle"] };

        using var host = HostWith(client, environment: "Development");

        var act = () => host.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*InternalHosts*crm*");
    }

    [Fact]
    public async Task A_reference_into_an_unconfigured_service_is_logged_and_served_on_every_other_host()
    {
        var client = new FakeRemoteClient { Configured = ["vehicle"] };
        client.Reachable.Add("vehicle");

        using var host = HostWith(client, environment: "Production");

        var response = await host.CreateClient().GetAsync("/OxQL/health");
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Logs.Entries.Should().Contain(entry => entry.Category == "OxQL.Startup" && entry.Message.Contains("'crm'"));

        var crm = body!["remote"]!.AsArray().Select(node => node!.AsObject()).Single(entry => entry["service"]!.GetValue<string>() == "crm");

        crm["configured"]!.GetValue<bool>().Should().BeFalse();
        crm["reachable"].Should().BeNull("an unconfigured service is not probed");
        body["status"]!.GetValue<string>().Should().Be("degraded");
    }

    [Fact]
    public void The_check_names_every_unconfigured_reference()
    {
        var client = new FakeRemoteClient { Configured = [] };
        var findings = OxQL.AspNetCore.Resolve.RemoteReferenceCheck.Unconfigured(Bind.BindHost.Probe, client);

        findings.Select(finding => $"{finding.Entity}#{finding.Path}->{finding.Service}").Should().Equal("probe.order#contactNumber->crm", "probe.order#vehicleId->vehicle");
        OxQL.AspNetCore.Resolve.RemoteReferenceCheck.IsStrict("Local", null).Should().BeTrue();
        OxQL.AspNetCore.Resolve.RemoteReferenceCheck.IsStrict("Staging", "true").Should().BeTrue();
        OxQL.AspNetCore.Resolve.RemoteReferenceCheck.IsStrict("Staging", "0").Should().BeFalse();
    }
}
