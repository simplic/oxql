using System.Net;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.AspNetCore.Health;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>A tour whose resource is a driver of one service or a vehicle of another.</summary>
public class OaTour
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = "";
    public OaResource? Resource { get; set; }
}

/// <summary>A typed reference with one remote owner per case.</summary>
public class OaResource
{
    public string Type { get; set; } = "";

    [OxQLReferenceWhen("type", "driver", "staff.employee", Field = "id")]
    [OxQLReferenceWhen("type", "vehicle", "fleet.vehicle", Field = "id")]
    public Guid Id { get; set; }
}

/// <summary>
/// What a host remembers of an owner that did not answer, and what a page asks of owners at all:
/// an owner that failed is not called again for a short time and its rows say so at once; one caller
/// tries it when the time is over; a page asks only the owners it has keys for, for their facts and
/// for rows.
/// </summary>
public class OwnerAvailabilityTests
{
    private const string Tour = "oa.tour";
    private const string Resolve = """[{ "resolve": { "path": "resource.id", "as": "res" } }, { "page": { "limit": 10 } }]""";

    private static readonly Guid Driver = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Vehicle = Guid.Parse("f0000000-0000-0000-0000-000000000001");

    private static readonly Lazy<EntityModel> model = new(() => ClrModelBuilder.Build(
        new EntityDeclaration[] { new(Tour, Tour, typeof(OaTour), "tours", null, false) }, retiredIds: null, new ReferenceDeclarations()));

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, FakeRemoteClient Client, ManualTime Time) Host(Action<OxQLOptions>? configure = null)
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options(configure);
        var time = new ManualTime();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(model.Value), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options))
        {
            Availability = new OwnerAvailability(options, time),
        };

        return (engine, runner, client, time);
    }

    private static BsonDocument Row(string type, Guid resource) => new()
    {
        ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
        ["Number"] = "T-1",
        ["Resource"] = new BsonDocument { ["Type"] = type, ["_id"] = new BsonBinaryData(resource, GuidRepresentation.Standard) },
    };

    private static async Task<QueryResult> RunAsync(MongoQueryEngine engine, string pipeline = Resolve)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Tour, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    // ---- the record ---------------------------------------------------------------------------------

    [Fact]
    public void An_owner_that_failed_is_not_entered_until_the_time_is_over_and_then_by_one_caller()
    {
        var time = new ManualTime();
        var availability = new OwnerAvailability(TimeSpan.FromSeconds(5), time);

        availability.TryEnter("staff", out _).Should().BeTrue("nothing is remembered of it");

        availability.Failed("staff", OwnerAvailability.Unreachable);
        time.Advance(TimeSpan.FromSeconds(2));

        availability.TryEnter("staff", out var outage).Should().BeFalse();
        outage.Should().Be(new OwnerOutage(OwnerAvailability.Unreachable, null, TimeSpan.FromSeconds(3)));
        availability.TryEnter("fleet", out _).Should().BeTrue("another owner is not concerned");

        time.Advance(TimeSpan.FromSeconds(3));

        availability.OutageOf("staff").Should().BeNull("a read lets nobody in and holds nobody back once the time is over");
        availability.TryEnter("staff", out _).Should().BeTrue("the time is over: one caller tries");
        availability.TryEnter("staff", out outage).Should().BeFalse("the others keep the answer until that call lands");
        outage!.Reason.Should().Be(OwnerAvailability.Unreachable);

        availability.Succeeded("staff");

        availability.TryEnter("staff", out _).Should().BeTrue("the call landed and the owner answered");
        availability.OutageOf("staff").Should().BeNull();
    }

    [Fact]
    public void A_trial_that_never_reports_holds_the_others_back_for_one_more_period_only()
    {
        var time = new ManualTime();
        var availability = new OwnerAvailability(TimeSpan.FromSeconds(5), time);

        availability.Failed("staff", OwnerAvailability.Timeout);
        time.Advance(TimeSpan.FromSeconds(5));
        availability.TryEnter("staff", out _).Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(5));

        availability.TryEnter("staff", out _).Should().BeTrue("the caller that tried was stopped before it could say how the call ended");
    }

    [Fact]
    public void A_health_measurement_that_answers_forgets_an_owner_but_not_its_missing_route()
    {
        var availability = new OwnerAvailability(TimeSpan.FromSeconds(5), new ManualTime());

        availability.Failed("staff", OwnerAvailability.Unreachable);
        availability.Failed("fleet", OwnerAvailability.Route, 404);

        availability.Succeeded("staff", keepRoute: true);
        availability.Succeeded("fleet", keepRoute: true);

        availability.OutageOf("staff").Should().BeNull();
        availability.OutageOf("fleet").Should().Be(new OwnerOutage(OwnerAvailability.Route, 404, TimeSpan.FromSeconds(5)), "an answering health says nothing of the internal query route");
    }

    [Fact]
    public void A_time_of_zero_remembers_nothing()
    {
        var availability = new OwnerAvailability(BindHost.Options(options => options.Cache.OwnerUnavailableTtlSeconds = 0));

        availability.Failed("staff", OwnerAvailability.Unreachable);

        availability.TryEnter("staff", out _).Should().BeTrue();
    }

    [Fact]
    public void The_default_time_is_five_seconds()
    {
        new OxQLOptions().Cache.OwnerUnavailableTtlSeconds.Should().Be(5);
    }

    // ---- the keyed fetch ----------------------------------------------------------------------------

    [Fact]
    public async Task The_second_request_makes_no_call_to_an_owner_that_just_failed_and_reports_the_same_outcome()
    {
        var (engine, runner, client, _) = Host();

        runner.PageRows = [Row("driver", Driver)];
        client.Unreachable.Add("staff");

        var first = await RunAsync(engine);
        var second = await RunAsync(engine);

        client.Calls.Should().ContainSingle("the owner is not called again while the host remembers that it did not answer");
        client.Probed.Should().Equal(["staff"], "nor asked for its facts");

        first.Items[0]!["res"].Should().BeNull();
        second.Items[0]!["res"].Should().BeNull();

        first.Diagnostics.Should().ContainSingle().Which.Params.Should().Contain("reason", "unreachable").And.NotContainKey("remembered");

        var diagnostic = second.Diagnostics.Should().ContainSingle().Subject;

        diagnostic.Code.Should().Be(Codes.ResolveUnreachable);
        diagnostic.Message.Should().Contain("was not asked").And.Contain("could not be reached a moment ago").And.Contain("'res' is null on this page");
        diagnostic.Params.Should().Contain("service", "staff").And.Contain("reason", "unreachable").And.Contain("remembered", true).And.Contain("retryAfterMs", 5000);
    }

    [Fact]
    public async Task A_row_of_an_owner_that_is_not_asked_is_owner_unanswered_and_a_strict_request_is_refused_as_by_a_failed_call()
    {
        var (engine, runner, client, _) = Host();

        runner.PageRows = [Row("driver", Driver)];
        client.Unreachable.Add("staff");
        await RunAsync(engine);

        var reported = await RunAsync(engine, """[{ "resolve": { "path": "resource.id", "as": "res", "outcomeAs": "resOutcome" } }, { "page": { "limit": 10 } }]""");

        reported.Items[0]!["resOutcome"]!.GetValue<string>().Should().Be("owner_unanswered");

        var strict = await engine.ExecuteAsync(BindHost.Request(Tour, Resolve) with { Strict = true }, BindHost.Context());

        strict.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors!.Select(error => error.Code).Should().Contain(Codes.ResolveUnreachable);
        client.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task When_the_time_is_over_the_owner_is_called_once_more_and_an_answer_ends_the_memory()
    {
        var (engine, runner, client, time) = Host();

        runner.PageRows = [Row("driver", Driver)];
        client.Unreachable.Add("staff");
        await RunAsync(engine);

        time.Advance(TimeSpan.FromSeconds(5));
        await RunAsync(engine);

        client.Calls.Should().HaveCount(2, "the time is over: the owner is tried");

        await RunAsync(engine);

        client.Calls.Should().HaveCount(2, "it failed again and is remembered again");

        time.Advance(TimeSpan.FromSeconds(5));
        client.Unreachable.Clear();
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Driver.ToString(), ("name", "Ada")));

        var answered = await RunAsync(engine);

        answered.Items[0]!["res"]!["name"]!.GetValue<string>().Should().Be("Ada");
        answered.Diagnostics.Should().BeNullOrEmpty();
        engine.Availability.OutageOf("staff").Should().BeNull();
    }

    [Fact]
    public async Task An_owner_without_the_internal_query_route_is_remembered_with_its_status()
    {
        var (engine, runner, _, _) = Host();
        var counting = new CountingClient(HttpStatusCode.NotFound);
        var options = BindHost.Options();
        var routed = new MongoQueryEngine(new StaticEntityModelProvider(model.Value), runner, BindHost.Cursors, options, counting, cache: new OwnerFetchCache(options))
        {
            Availability = engine.Availability,
        };

        runner.PageRows = [Row("driver", Driver)];

        await routed.ExecuteAsync(BindHost.Request(Tour, Resolve), BindHost.Context());

        var second = ((QueryOutcome.Success)await routed.ExecuteAsync(BindHost.Request(Tour, Resolve), BindHost.Context())).Result;

        counting.Calls.Should().Be(1);

        var diagnostic = second.Diagnostics.Should().ContainSingle().Subject;

        diagnostic.Message.Should().Contain("HTTP 404").And.Contain("InternalApiVersions:staff");
        diagnostic.Params.Should().Contain("reason", "route").And.Contain("status", 404).And.Contain("remembered", true);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_owner_that_answered_this_request_with_an_error_is_asked_again_by_the_next(HttpStatusCode status)
    {
        var runner = new FakeAggregateRunner { PageRows = [Row("driver", Driver)] };
        var counting = new CountingClient(status);
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(model.Value), runner, BindHost.Cursors, options, counting, cache: new OwnerFetchCache(options));

        await engine.ExecuteAsync(BindHost.Request(Tour, Resolve), BindHost.Context());
        await engine.ExecuteAsync(BindHost.Request(Tour, Resolve), BindHost.Context());

        counting.Calls.Should().Be(2, "the status says something of the request that was sent, not of whether the owner answers");
    }

    [Fact]
    public async Task An_owner_that_did_not_answer_within_a_whole_call_budget_is_remembered_and_one_cut_short_by_the_request_is_not()
    {
        var (engine, runner, client, _) = Host(options => options.Execution.ResolveTimeoutMs = 60);

        runner.PageRows = [Row("driver", Driver)];
        client.Silent.Add("staff");

        var late = await RunAsync(engine);

        late.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Codes.ResolveTimeout);
        engine.Availability.OutageOf("staff")!.Reason.Should().Be(OwnerAvailability.Timeout);

        var second = await RunAsync(engine);

        client.Calls.Should().ContainSingle();
        second.Diagnostics.Should().ContainSingle().Which.Params.Should().Contain("reason", "timeout").And.Contain("remembered", true);

        // A request with 300 ms in all leaves its owner call less than half of what one call may take.
        var (hurried, hurriedRunner, hurriedClient, _) = Host(options => options.Execution.ResolveTimeoutMs = 2_000);

        hurriedRunner.PageRows = [Row("driver", Driver)];
        hurriedClient.Silent.Add("staff");

        await hurried.ExecuteAsync(BindHost.Parse($$"""{ "entityType": "{{Tour}}", "maxTimeMs": 300, "pipeline": {{Resolve}} }"""), BindHost.Context());

        hurriedClient.Calls.Should().ContainSingle();
        hurried.Availability.OutageOf("staff").Should().BeNull("the request ran out of time, which says nothing of the owner");
    }

    [Fact]
    public async Task A_semi_join_into_an_owner_that_just_failed_is_refused_at_once()
    {
        var options = BindHost.Options();
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));
        const string semiJoin = """[{ "resolve": { "path": "vehicleId", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "V-1" } } }, { "page": { "limit": 10 } }]""";

        client.Unreachable.Add("vehicle");

        var first = await engine.ExecuteAsync(BindHost.Request("probe.order", semiJoin), BindHost.Context());
        var second = await engine.ExecuteAsync(BindHost.Request("probe.order", semiJoin), BindHost.Context());

        first.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors![0].Code.Should().Be(Codes.ResolveUnavailable);
        second.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors![0].Code.Should().Be(Codes.ResolveUnavailable);
        client.Calls.Should().ContainSingle();
    }

    // ---- what a page asks ---------------------------------------------------------------------------

    [Fact]
    public async Task A_page_without_rows_reads_no_owners_facts_and_calls_no_owner()
    {
        var (engine, runner, client, _) = Host();

        runner.PageRows = [];

        var result = await RunAsync(engine);

        result.Items.Should().BeEmpty();
        client.Probed.Should().BeEmpty("there is no key to size a query for");
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_with_one_variant_asks_the_one_owner_it_has_keys_for()
    {
        var (engine, runner, client, _) = Host();

        runner.PageRows = [Row("driver", Driver), Row("driver", Driver)];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Driver.ToString(), ("name", "Ada")));

        var result = await RunAsync(engine);

        result.Items[0]!["res"]!["name"]!.GetValue<string>().Should().Be("Ada");
        client.Probed.Should().Equal(["staff"], "the owner of the vehicles has no key on this page");
        client.Calls.Select(call => call.Service).Should().Equal("staff");
    }

    [Fact]
    public async Task A_page_with_both_variants_asks_both_owners()
    {
        var (engine, runner, client, _) = Host();

        runner.PageRows = [Row("driver", Driver), Row("vehicle", Vehicle)];

        await RunAsync(engine);

        client.Probed.Should().BeEquivalentTo(["staff", "fleet"]);
        client.Calls.Select(call => call.Service).Should().BeEquivalentTo(["staff", "fleet"]);
    }

    [Fact]
    public async Task A_page_the_cache_answers_reads_no_owners_facts()
    {
        var (engine, runner, client, _) = Host();

        runner.PageRows = [Row("driver", Driver)];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Driver.ToString(), ("name", "Ada")));
        await RunAsync(engine);

        client.Probed.Clear();
        client.Calls.Clear();

        var cached = await RunAsync(engine);

        cached.Items[0]!["res"]!["name"]!.GetValue<string>().Should().Be("Ada");
        client.Probed.Should().BeEmpty();
        client.Calls.Should().BeEmpty();
    }

    // ---- the health probe ---------------------------------------------------------------------------

    /// <summary>An owner whose health ends as the test says, per service.</summary>
    private sealed class ProbedClient(Func<string, CancellationToken, Task<bool>> health) : IRemoteQueryClient
    {
        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => health(serviceKey, cancellationToken);
    }

    private static async Task<(OwnerAvailability Availability, IReadOnlyList<RemoteServiceState> States)> ProbedAsync(Func<string, CancellationToken, Task<bool>> health, Action<OwnerAvailability>? before = null, TimeSpan? timeout = null)
    {
        var options = BindHost.Options();
        var availability = new OwnerAvailability(options);
        var probe = new RemoteHealthProbe(options) { Availability = availability, ProbeTimeout = timeout ?? TimeSpan.FromSeconds(30) };
        var client = new ProbedClient(health);

        before?.Invoke(availability);

        probe.State(model.Value, client);
        await probe.Refreshing;

        return (availability, probe.State(model.Value, client));
    }

    [Fact]
    public async Task A_health_probe_that_answers_ends_what_is_remembered_of_the_owner()
    {
        var (availability, _) = await ProbedAsync((_, _) => Task.FromResult(true), before: record => record.Failed("staff", OwnerAvailability.Unreachable));

        availability.OutageOf("staff").Should().BeNull("health saw the owner answer");
    }

    public static TheoryData<string, Exception> ConnectionFailures => new()
    {
        { "refused", new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made.", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)) },
        { "name", new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.") },
        { "connect timeout", new HttpRequestException("The connection attempt failed.", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut)) },
        { "socket", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable) },
    };

    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public async Task A_health_probe_that_could_make_no_connection_is_remembered(string cause, Exception failure)
    {
        var (availability, states) = await ProbedAsync((_, _) => Task.FromException<bool>(failure));

        states.Should().OnlyContain(state => state.Reachable == false);
        availability.OutageOf("staff")!.Reason.Should().Be(OwnerAvailability.Unreachable, cause);
        availability.OutageOf("fleet")!.Reason.Should().Be(OwnerAvailability.Unreachable, cause);
    }

    public static TheoryData<string, Exception> OtherFailures => new()
    {
        { "status", new HttpRequestException("The owner did not answer with success.", null, HttpStatusCode.ServiceUnavailable) },
        { "no health route", new HttpRequestException("The owner did not answer with success.", null, HttpStatusCode.NotFound) },
        { "unread", new System.Text.Json.JsonException("The answer is no JSON.") },
        { "response ended", new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.") },
        { "the client's own timeout", new TaskCanceledException("The request was cancelled.", new TimeoutException("The request timed out.")) },
        { "fault", new InvalidOperationException("The client is misconfigured.") },
    };

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public async Task A_health_probe_that_fails_otherwise_reports_the_owner_unreachable_and_records_nothing(string cause, Exception failure)
    {
        var (availability, states) = await ProbedAsync((_, _) => Task.FromException<bool>(failure));

        states.Should().OnlyContain(state => state.Reachable == false, "health still says what it measured");
        availability.OutageOf("staff").Should().BeNull(cause);
        availability.OutageOf("fleet").Should().BeNull(cause);
    }

    [Fact]
    public async Task A_health_probe_that_is_answered_false_records_nothing_and_leaves_what_is_remembered()
    {
        var (availability, states) = await ProbedAsync((_, _) => Task.FromResult(false), before: record => record.Failed("fleet", OwnerAvailability.Timeout));

        states.Should().OnlyContain(state => state.Reachable == false);
        availability.OutageOf("staff").Should().BeNull("an answer of false does not say why");
        availability.OutageOf("fleet")!.Reason.Should().Be(OwnerAvailability.Timeout, "nor does it end what a join's own call found");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_health_probe_that_times_out_records_nothing(bool hearsCancellation)
    {
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (availability, states) = await ProbedAsync(
            (_, token) => hearsCancellation ? never.Task.WaitAsync(token) : never.Task,
            timeout: TimeSpan.FromMilliseconds(50));

        states.Should().OnlyContain(state => state.Reachable == false, "a service that does not answer in time is not reachable on health");
        availability.OutageOf("staff").Should().BeNull("the probe's own time says nothing of the route a join calls");
        availability.OutageOf("fleet").Should().BeNull();
    }

    /// <summary>An owner that answers every batch with one HTTP status and counts the batches.</summary>
    private sealed class CountingClient(HttpStatusCode status) : IRemoteQueryClient
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);

            throw new HttpRequestException("The owner did not answer with success.", null, status);
        }

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
