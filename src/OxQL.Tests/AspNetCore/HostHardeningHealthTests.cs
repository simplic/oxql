using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OxQL.AspNetCore.Health;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.TestCases;
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

        private readonly List<(int Count, TaskCompletionSource Reached)> waiting = [];

        /// <summary>Ends once <paramref name="count"/> probes have been sent; fails after <see cref="ManualTime.Ceiling"/>.</summary>
        public Task ProbedAsync(int count)
        {
            lock (Tokens)
            {
                if (Probes >= count)
                    return Task.CompletedTask;

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                waiting.Add((count, reached));

                return reached.Task.WaitAsync(ManualTime.Ceiling);
            }
        }

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken)
        {
            lock (Tokens)
            {
                var now = Interlocked.Increment(ref probes);

                Tokens.Add(cancellationToken);

                foreach (var (count, reached) in waiting)
                    if (now >= count)
                        reached.TrySetResult();
            }

            return IgnoresCancellation ? Answer.Task : Answer.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>A host whose clock stands still: the probe's timeout ends only when the test moves it.</summary>
    private static SampleHost StillHost(IRemoteQueryClient client) =>
        new(configure: services => services.AddSingleton<IRemoteQueryClient>(client).AddSingleton<TimeProvider>(new ManualTime()));

    private static RemoteHealthProbe Probe(ManualTime? time = null, TimeSpan? timeout = null) =>
        new(BindHost.Options(options => options.Cache.HealthProbeTtlSeconds = 10), time: time) { ProbeTimeout = timeout ?? TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task The_first_request_is_answered_before_any_service_has()
    {
        var client = new GatedClient();

        using var host = StillHost(client);

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
    public async Task The_first_contract_2_request_reads_what_the_first_measurement_found_and_all_callers_of_the_moment_share_it()
    {
        var client = new GatedClient();

        using var host = StillHost(client);

        var callers = Enumerable.Range(0, 6).Select(_ => host.Client(contract: 2).GetAsync("/OxQL/health")).ToList();

        await client.ProbedAsync(2);
        callers.Should().OnlyContain(caller => !caller.IsCompleted, "the measurement is not in yet");

        // A caller without the header is answered meanwhile, as before.
        var plain = await SampleHost.Body(await host.CreateClient().GetAsync("/OxQL/health"));

        plain!["remote"]!.AsArray().Should().OnlyContain(entry => entry!["reachable"] == null);

        client.Answer.SetResult(false);

        foreach (var caller in callers)
        {
            var body = await SampleHost.Body(await caller);

            body!["status"]!.GetValue<string>().Should().Be("degraded");
            body["remote"]!.AsArray().Should().OnlyContain(entry => entry!["reachable"]!.GetValue<bool>() == false && entry["reason"]!.GetValue<string>() == "unreachable");
        }

        client.Probes.Should().Be(2, "one measurement of the two services answers every caller");
    }

    [Fact]
    public async Task A_service_that_never_answers_is_measured_as_not_reachable_by_the_end_of_the_wait()
    {
        var time = new ManualTime();
        var client = new GatedClient { IgnoresCancellation = true };
        var probe = Probe(time, TimeSpan.FromMilliseconds(100));
        var first = probe.FirstStateAsync(BindHost.Probe, client);

        await client.ProbedAsync(2);
        first.IsCompleted.Should().BeFalse("neither service has answered and the time is not up");
        time.Advance(TimeSpan.FromMilliseconds(100));

        (await first.WaitAsync(ManualTime.Ceiling)).Should().OnlyContain(state => state.Reachable == false, "its time was up, which is a measurement");
    }

    [Fact]
    public async Task The_wait_for_the_first_measurement_ends_with_the_measurement()
    {
        // The probe's clock stands still, so its timeout cannot end the wait: only the measurement can.
        var client = new GatedClient();
        var probe = Probe(new ManualTime());
        var first = probe.FirstStateAsync(BindHost.Probe, client);

        first.IsCompleted.Should().BeFalse();
        client.Answer.SetResult(true);

        (await first.WaitAsync(ManualTime.Ceiling)).Should().OnlyContain(state => state.Reachable == true);
    }

    [Fact]
    public async Task A_wait_that_ran_out_says_pending_and_no_later_call_waits()
    {
        // The client holds the thread of the measurement, so the measurement outlasts its own timeout.
        var time = new ManualTime();
        var client = new BlockingClient();
        var probe = Probe(time, TimeSpan.FromMilliseconds(50));
        var first = probe.FirstStateAsync(BindHost.Probe, client);

        // The wait is the probe's timeout and a moment after it.
        time.Advance(TimeSpan.FromSeconds(1));

        (await first.WaitAsync(ManualTime.Ceiling)).Should().OnlyContain(state => state.Reason == RemoteServiceState.Pending);

        var again = probe.FirstStateAsync(BindHost.Probe, client);

        again.IsCompleted.Should().BeTrue("the first measurement is waited for once per process");
        (await again).Should().OnlyContain(state => state.Reason == RemoteServiceState.Pending);

        client.Release.Set();
        await probe.Refreshing;
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_let_go_of_the_wait_and_the_measurement_goes_on_for_the_others()
    {
        var client = new GatedClient();
        var probe = Probe();

        using var gone = new CancellationTokenSource();

        var leaving = probe.FirstStateAsync(BindHost.Probe, client, gone.Token);
        var staying = probe.FirstStateAsync(BindHost.Probe, client, CancellationToken.None);

        leaving.IsCompleted.Should().BeFalse();
        gone.Cancel();

        await FluentActions.Awaiting(() => leaving).Should().ThrowAsync<OperationCanceledException>();
        staying.IsCompleted.Should().BeFalse("the measurement is the others' too");

        client.Answer.SetResult(true);

        (await staying).Should().OnlyContain(state => state.Reachable == true);
    }

    /// <summary>The action a host compiled against the engine calls is the one it was: it answers at once, from what was measured.</summary>
    [Fact]
    public void The_health_action_keeps_its_signature_and_the_route_is_served_by_the_one_that_waits()
    {
        var actions = typeof(OxQL.AspNetCore.Controllers.OxQLController).GetMethods().Where(method => method.Name.StartsWith("Health", StringComparison.Ordinal)).ToList();
        var kept = actions.Single(method => method.Name == "Health");

        kept.ReturnType.Should().Be(typeof(Microsoft.AspNetCore.Mvc.IActionResult));
        kept.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(typeof(IQueryEngine), typeof(IEntityModelProvider), typeof(RemoteHealthProbe), typeof(IRemoteQueryClient), typeof(bool));
        kept.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.NonActionAttribute), false).Should().NotBeEmpty("one action answers the route");

        var routed = actions.Single(method => method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), false).Length > 0);

        routed.ReturnType.Should().Be(typeof(Task<Microsoft.AspNetCore.Mvc.IActionResult>));
        routed.GetParameters().Last().ParameterType.Should().Be(typeof(CancellationToken));
    }

    /// <summary>A client whose probe blocks the thread that calls it until the test lets go.</summary>
    private sealed class BlockingClient : IRemoteQueryClient
    {
        public ManualResetEventSlim Release { get; } = new();

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken)
        {
            Release.Wait(TimeSpan.FromSeconds(30));

            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task A_shallow_request_leaves_the_remote_state_out_and_starts_no_measurement()
    {
        var client = new GatedClient();

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client));

        var response = await host.Client(contract: 2).GetAsync("/OxQL/health?shallow=true");
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

        // The refresh runs on the thread pool, so its probes arrive after the callers return: the callers past the interval start it.
        await client.ProbedAsync(probesBefore * 2);

        client.Answer.SetResult(false);
        await probe.Refreshing;

        client.Probes.Should().Be(probesBefore * 2, "five callers past the interval share one refresh");
        probe.State(BindHost.Probe, client).Should().OnlyContain(state => state.Reachable == false);
    }

    [Fact]
    public async Task The_measurement_runs_under_its_own_timeout_and_no_callers_token()
    {
        var time = new ManualTime();
        var client = new GatedClient();
        var probe = Probe(time, TimeSpan.FromMilliseconds(50));

        probe.State(BindHost.Probe, client);
        await client.ProbedAsync(2);
        probe.Refreshing.IsCompleted.Should().BeFalse("neither service has answered and the time is not up");
        time.Advance(TimeSpan.FromMilliseconds(50));
        await probe.Refreshing.WaitAsync(ManualTime.Ceiling);

        client.Tokens.Should().OnlyContain(token => token.CanBeCanceled);
        probe.State(BindHost.Probe, client).Should().OnlyContain(state => state.Configured && state.Reachable == false, "a service that does not answer in time is not reachable");
        client.Probes.Should().Be(2, "the measurement finished, so the interval holds and nobody measures again");
    }

    [Fact]
    public async Task A_client_that_ignores_cancellation_cannot_hold_the_measurement_open()
    {
        var time = new ManualTime();
        var client = new GatedClient { IgnoresCancellation = true };
        var probe = Probe(time, TimeSpan.FromMilliseconds(50));

        probe.State(BindHost.Probe, client);

        var refreshing = probe.Refreshing;

        await client.ProbedAsync(2);
        refreshing.IsCompleted.Should().BeFalse("the probes do not answer and the time is not up");

        // The refresh ends at the probe timeout.
        time.Advance(TimeSpan.FromMilliseconds(50));
        await refreshing.WaitAsync(ManualTime.Ceiling);

        probe.State(BindHost.Probe, client).Should().OnlyContain(state => state.Reachable == false);
    }

    /// <summary>A client whose owners answer their health, and that says what each health named (null: it keeps no facts).</summary>
    private sealed class FactsClient(Func<string, RemoteOwnerInfo?> facts) : IRemoteQueryClient, IRemoteOwnerInfo
    {
        public HashSet<string> Down { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unconfigured { get; } = new(StringComparer.Ordinal);

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsConfigured(string serviceKey) => !Unconfigured.Contains(serviceKey);

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(!Down.Contains(serviceKey));

        public RemoteOwnerInfo? OwnerOf(string serviceKey) => facts(serviceKey);
    }

    /// <summary>A client that measures reachability and says nothing of what an owner runs.</summary>
    private sealed class PlainClient : IRemoteQueryClient
    {
        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static async Task<IReadOnlyList<RemoteServiceState>> MeasuredAsync(IRemoteQueryClient client)
    {
        var probe = Probe();

        probe.State(BindHost.Probe, client);
        await probe.Refreshing;

        return probe.State(BindHost.Probe, client);
    }

    [Fact]
    public async Task An_owner_that_speaks_this_engines_contract_is_usable_and_names_its_engine()
    {
        var states = await MeasuredAsync(new FactsClient(_ => new RemoteOwnerInfo("2.1.0", EngineCapabilities.Contract, 10)));

        states.Should().OnlyContain(state => state.Reachable == true && state.Usable == true && state.Reason == null
            && state.Contract == EngineCapabilities.Contract && state.EngineVersion == "2.1.0" && !state.Degrades);
    }

    [Fact]
    public async Task An_owner_whose_health_names_no_contract_answers_and_is_not_usable()
    {
        // What a health answer without an engine member reads as: facts that are known and name nothing.
        var nameless = RemoteOwnerInfo.FromShallowHealth(System.Text.Json.Nodes.JsonNode.Parse("""{"status":"healthy","service":"oxql"}"""));
        var states = await MeasuredAsync(new FactsClient(service => service == "crm" ? nameless : new RemoteOwnerInfo("2.1.0", 2, 10)));
        var crm = states.Single(state => state.Service == "crm");

        crm.Reachable.Should().BeTrue("its health answered");
        crm.Usable.Should().BeFalse("it names no contract this host can send a join to");
        crm.Reason.Should().Be(RemoteServiceState.Engine);
        crm.Contract.Should().BeNull();
        crm.Degrades.Should().BeTrue();
        states.Single(state => state.Service == "vehicle").Usable.Should().BeTrue();
    }

    [Fact]
    public async Task An_owner_on_another_contract_is_not_usable_and_says_which_it_speaks()
    {
        var states = await MeasuredAsync(new FactsClient(_ => new RemoteOwnerInfo("1.0.0", 1, 10)));

        states.Should().OnlyContain(state => state.Usable == false && state.Reason == RemoteServiceState.Engine && state.Contract == 1 && state.EngineVersion == "1.0.0");
    }

    [Fact]
    public async Task An_owner_that_does_not_answer_and_one_that_is_not_configured_say_why_they_are_not_usable()
    {
        var client = new FactsClient(_ => new RemoteOwnerInfo("2.1.0", 2, 10)) { Down = { "crm" }, Unconfigured = { "vehicle" } };
        var states = await MeasuredAsync(client);

        states.Single(state => state.Service == "crm").Should().Match<RemoteServiceState>(state =>
            state.Configured && state.Reachable == false && state.Usable == false && state.Reason == RemoteServiceState.Unreachable && state.Contract == null);
        states.Single(state => state.Service == "vehicle").Should().Match<RemoteServiceState>(state =>
            !state.Configured && state.Reachable == null && state.Usable == false && state.Reason == RemoteServiceState.NotConfigured);
    }

    [Fact]
    public async Task A_client_that_keeps_no_owner_facts_leaves_usable_unknown_and_the_host_healthy()
    {
        foreach (var client in new IRemoteQueryClient[] { new PlainClient(), new FactsClient(_ => null) })
        {
            var states = await MeasuredAsync(client);

            states.Should().OnlyContain(state => state.Reachable == true && state.Usable == null && state.Reason == null && !state.Degrades);
        }
    }

    [Fact]
    public async Task Health_under_contract_2_says_what_each_owner_can_do_and_is_degraded_by_one_that_is_not_usable()
    {
        var client = new FactsClient(service => service == "crm" ? new RemoteOwnerInfo(null, null, null) : new RemoteOwnerInfo("2.1.0", 2, 10));

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client));

        await host.Client(contract: 2).GetAsync("/OxQL/health");
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;

        var body = await SampleHost.Body(await host.Client(contract: 2).GetAsync("/OxQL/health"));
        var remote = body!["remote"]!.AsArray().Select(node => node!.AsObject()).ToList();

        // What the check of the declared references says of each service has tests of its own (RemoteReferenceValidatorTests).
        remote.ForEach(entry => entry.Remove("references").Should().BeTrue("a contract 2 caller reads the state of the declared references beside the owner's"));

        body["status"]!.GetValue<string>().Should().Be("degraded");
        remote.Single(entry => entry["service"]!.GetValue<string>() == "crm").ToJsonString()
            .Should().Be("""{"service":"crm","configured":true,"reachable":true,"usable":false,"reason":"engine"}""");
        remote.Single(entry => entry["service"]!.GetValue<string>() == "vehicle").ToJsonString()
            .Should().Be("""{"service":"vehicle","configured":true,"reachable":true,"contract":2,"engineVersion":"2.1.0","usable":true}""");
    }

    /// <summary>A model provider that says what its build left, or has no model at all.</summary>
    private sealed class SayingModels(OxQL.Model.EntityModel? model, bool? degraded) : IEntityModelProvider
    {
        public OxQL.Model.EntityModel Model => model ?? throw new InvalidOperationException("The host has not built a model.");

        public bool? Degraded => degraded;
    }

    private static readonly OxQL.Model.EntityModel NoEntities =
        OxQL.Model.Build.ClrModelBuilder.Build(Array.Empty<OxQL.Model.Build.EntityDeclaration>(), retiredIds: null, new OxQL.Model.Build.ReferenceDeclarations());

    private static async Task<System.Text.Json.Nodes.JsonObject> HealthAsync(IEntityModelProvider? models, int? contract = 2, string route = "/OxQL/health")
    {
        using var host = new SampleHost(configure: services =>
        {
            if (models is null)
                return;

            services.RemoveAll<IEntityModelProvider>();
            services.AddSingleton(models);
        });

        return (await SampleHost.Body(await host.Client(contract).GetAsync(route)))!.AsObject();
    }

    [Fact]
    public async Task Health_under_contract_2_says_how_many_entities_the_model_has()
    {
        foreach (var route in new[] { "/OxQL/health", "/OxQL/health?shallow=true" })
        {
            var body = await HealthAsync(null, route: route);

            body["status"]!.GetValue<string>().Should().Be("healthy");
            body["model"]!.ToJsonString().Should().Be($$"""{"entities":{{BindHost.Probe.Entities.Count}},"degraded":false}""", route);
        }
    }

    [Fact]
    public async Task A_model_a_failed_build_left_degrades_the_status_and_says_so()
    {
        var body = await HealthAsync(new SayingModels(NoEntities, degraded: true));

        body["status"]!.GetValue<string>().Should().Be("degraded");
        body["model"]!.ToJsonString().Should().Be("""{"entities":0,"degraded":true,"reason":"build"}""");
    }

    [Fact]
    public async Task A_model_without_entities_is_healthy_unless_its_provider_says_it_is_degraded()
    {
        var silent = await HealthAsync(new StaticEntityModelProvider(NoEntities));

        silent["status"]!.GetValue<string>().Should().Be("healthy", "a service without entities of its own is a host like any other");
        silent["model"]!.ToJsonString().Should().Be("""{"entities":0,"degraded":false}""", "the count is shown either way");

        var meant = await HealthAsync(new SayingModels(NoEntities, degraded: false));

        meant["status"]!.GetValue<string>().Should().Be("healthy");
        meant["model"]!.ToJsonString().Should().Be("""{"entities":0,"degraded":false}""");

        var said = await HealthAsync(new SayingModels(NoEntities, degraded: true));

        said["status"]!.GetValue<string>().Should().Be("degraded", "only the provider knows that its build lost the entities");
        said["model"]!.ToJsonString().Should().Be("""{"entities":0,"degraded":true,"reason":"build"}""");
    }

    [Fact]
    public async Task A_host_without_a_model_says_it_is_unavailable()
    {
        var body = await HealthAsync(new SayingModels(null, degraded: null));

        body["status"]!.GetValue<string>().Should().Be("degraded");
        body["model"]!.ToJsonString().Should().Be("""{"entities":0,"degraded":true,"reason":"unavailable"}""");
    }

    [Fact]
    public async Task A_model_whose_build_throws_does_not_fail_health()
    {
        var body = await HealthAsync(new LazyEntityModelProvider(() => throw new NotSupportedException("a serializer the build cannot read")), route: "/OxQL/health?shallow=true");

        body["status"]!.GetValue<string>().Should().Be("degraded");
        body["model"]!.ToJsonString().Should().Be("""{"entities":0,"degraded":true,"reason":"unavailable"}""");
    }

    [Fact]
    public async Task Health_without_the_contract_header_has_no_model_member_and_a_status_the_model_does_not_enter()
    {
        var healthy = await HealthAsync(null, contract: null);
        var degraded = await HealthAsync(new SayingModels(NoEntities, degraded: true), contract: null);
        var none = await HealthAsync(new SayingModels(null, degraded: null), contract: null);

        healthy.ContainsKey("model").Should().BeFalse();
        degraded.ContainsKey("model").Should().BeFalse();
        none.ContainsKey("model").Should().BeFalse();
        healthy["status"]!.GetValue<string>().Should().Be("healthy");
        degraded["status"]!.GetValue<string>().Should().Be("healthy", "the status of that form is of the referenced services alone, as it always was");
        none["status"]!.GetValue<string>().Should().Be("healthy");
    }

    [Fact]
    public async Task An_owner_that_is_not_usable_degrades_the_status_under_contract_2_and_not_without_the_header()
    {
        // Both owners answer their health; crm's names no contract this host can send a join to.
        var client = new FactsClient(service => service == "crm" ? new RemoteOwnerInfo(null, null, null) : new RemoteOwnerInfo("2.1.0", 2, 10));

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client));

        await host.CreateClient().GetAsync("/OxQL/health");
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;

        var plain = (await SampleHost.Body(await host.CreateClient().GetAsync("/OxQL/health")))!.AsObject();
        var second = (await SampleHost.Body(await host.Client(contract: 2).GetAsync("/OxQL/health")))!.AsObject();

        plain["status"]!.GetValue<string>().Should().Be("healthy", "both services are configured and answer, which is all that form's status has ever said");
        plain["remote"]!.ToJsonString().Should().Be("""[{"service":"crm","configured":true,"reachable":true},{"service":"vehicle","configured":true,"reachable":true}]""");
        plain.Select(member => member.Key).Should().Equal("status", "service", "engine", "capabilities", "limits", "remote");

        second["status"]!.GetValue<string>().Should().Be("degraded");
        second["remote"]!.AsArray().Single(entry => entry!["service"]!.GetValue<string>() == "crm")!["usable"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Health_without_the_contract_header_is_degraded_by_a_service_that_does_not_answer_or_is_not_configured()
    {
        foreach (var client in new[]
        {
            new FactsClient(_ => new RemoteOwnerInfo("2.1.0", 2, 10)) { Down = { "crm" } },
            new FactsClient(_ => new RemoteOwnerInfo("2.1.0", 2, 10)) { Unconfigured = { "vehicle" } },
        })
        {
            using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client)) { Environment = "Production" };

            await host.CreateClient().GetAsync("/OxQL/health");
            await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;

            var body = await SampleHost.Body(await host.CreateClient().GetAsync("/OxQL/health"));

            body!["status"]!.GetValue<string>().Should().Be("degraded");
        }
    }

    [Fact]
    public async Task Health_without_the_contract_header_keeps_the_three_members_of_a_remote_service()
    {
        var client = new FactsClient(_ => new RemoteOwnerInfo("2.1.0", 2, 10)) { Unconfigured = { "vehicle" } };

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client)) { Environment = "Production" };

        await host.CreateClient().GetAsync("/OxQL/health");
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;

        var body = await SampleHost.Body(await host.CreateClient().GetAsync("/OxQL/health"));

        body!["remote"]!.ToJsonString().Should().Be("""[{"service":"crm","configured":true,"reachable":true},{"service":"vehicle","configured":false}]""");
    }
}
