using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Health;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// Health never waits for another service: it answers from the last measurement, refreshes in
/// the background under the probe's own timeout, and has a shallow form that starts nothing.
/// </summary>
public class HostHardeningHealthTests
{
    /// <summary>A client whose probe answers when the test says so, or never.</summary>
    private sealed class GatedClient : IRemoteQueryClient
    {
        private int probes;

        public TaskCompletionSource<bool> Answer { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, the probe ignores its token, as a client with a blocking call would.</summary>
        public bool IgnoresCancellation { get; init; }

        public int Probes => Volatile.Read(ref probes);

        public List<CancellationToken> Tokens { get; } = [];

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probes);

            lock (Tokens)
                Tokens.Add(cancellationToken);

            return IgnoresCancellation ? Answer.Task : Answer.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>A clock the test moves.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static RemoteHealthProbe Probe(ManualTime? time = null, TimeSpan? timeout = null) =>
        new(BindHost.Options(options => options.Cache.HealthProbeTtlSeconds = 10), time: time) { ProbeTimeout = timeout ?? TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task The_first_request_is_answered_before_any_service_has()
    {
        var client = new GatedClient();

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client));

        var response = await host.CreateClient().GetAsync("/OxQL/health");
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var remote = body!["remote"]!.AsArray().Select(node => node!.AsObject()).ToList();

        remote.Select(entry => entry["service"]!.GetValue<string>()).Should().Equal("crm", "vehicle");
        remote.Should().OnlyContain(entry => entry["configured"]!.GetValue<bool>() && entry["reachable"] == null, "nothing is measured yet and the request did not wait for it");
        body["status"]!.GetValue<string>().Should().Be("healthy", "an unknown reachability is not a finding");

        client.Answer.SetResult(true);
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;

        body = await SampleHost.Body(await host.CreateClient().GetAsync("/OxQL/health"));
        body!["remote"]!.AsArray().Should().OnlyContain(entry => entry!["reachable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_shallow_request_leaves_the_remote_state_out_and_starts_no_measurement()
    {
        var client = new GatedClient();

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client));

        var response = await host.CreateClient().GetAsync("/OxQL/health?shallow=true");
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!["status"]!.GetValue<string>().Should().Be("healthy");
        body["remote"].Should().BeNull();
        body["capabilities"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Contain("resolve.remote");

        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;
        client.Probes.Should().Be(0, "the form one host asks of another never sets off that host's own probes");
    }

    [Fact]
    public async Task A_measurement_past_its_interval_is_still_served_while_one_refresh_runs()
    {
        var time = new ManualTime();
        var client = new GatedClient();
        var probe = Probe(time);

        probe.State(BindHost.Probe, client);
        client.Answer.SetResult(true);
        await probe.Refreshing;

        time.Advance(TimeSpan.FromSeconds(11));
        client.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var probesBefore = client.Probes;

        for (var call = 0; call < 5; call++)
            probe.State(BindHost.Probe, client).Should().OnlyContain(state => state.Reachable == true, "the last measurement answers while the next one is taken");

        client.Probes.Should().Be(probesBefore * 2, "five callers past the interval share one refresh");

        client.Answer.SetResult(false);
        await probe.Refreshing;

        probe.State(BindHost.Probe, client).Should().OnlyContain(state => state.Reachable == false);
    }

    [Fact]
    public async Task The_measurement_runs_under_its_own_timeout_and_no_callers_token()
    {
        var client = new GatedClient();
        var probe = Probe(timeout: TimeSpan.FromMilliseconds(50));

        probe.State(BindHost.Probe, client);
        await probe.Refreshing;

        client.Tokens.Should().OnlyContain(token => token.CanBeCanceled);
        probe.State(BindHost.Probe, client).Should().OnlyContain(state => state.Configured && state.Reachable == false, "a service that does not answer in time is not reachable");
        client.Probes.Should().Be(2, "the measurement finished, so the interval holds and nobody measures again");
    }

    [Fact]
    public async Task A_client_that_ignores_cancellation_cannot_hold_the_measurement_open()
    {
        var client = new GatedClient { IgnoresCancellation = true };
        var probe = Probe(timeout: TimeSpan.FromMilliseconds(50));

        probe.State(BindHost.Probe, client);

        var refreshing = probe.Refreshing;
        var finished = await Task.WhenAny(refreshing, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(refreshing, "the refresh ends at the probe timeout");
        probe.State(BindHost.Probe, client).Should().OnlyContain(state => state.Reachable == false);
    }
}
