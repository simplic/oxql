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
/// an owner that failed is not called again for a short time and its rows say so at once, under the
/// code of the call that failed; every caller asks it again when the time is over; a page asks only
/// the owners it has keys for, for their facts and for rows.
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
    public void An_owner_that_failed_is_not_entered_until_the_time_is_over_and_then_by_every_caller()
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
        availability.TryEnter("staff", out _).Should().BeTrue("the time is over: the owner is asked again");
        availability.TryEnter("staff", out outage).Should().BeTrue("by everyone: who comes while the first call is under way asks too");
        outage.Should().BeNull();

        availability.Failed("staff", OwnerAvailability.Timeout);

        availability.TryEnter("staff", out outage).Should().BeFalse("a call that fails starts the time again");
        outage.Should().Be(new OwnerOutage(OwnerAvailability.Timeout, null, TimeSpan.FromSeconds(5)));

        availability.Succeeded("staff");

        availability.TryEnter("staff", out _).Should().BeTrue("the owner answered");
        availability.OutageOf("staff").Should().BeNull();
    }

    [Fact]
    public async Task Requests_that_arrive_together_after_the_time_is_over_are_all_answered_by_an_owner_that_is_back()
    {
        var options = BindHost.Options();
        var time = new ManualTime();
        var down = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new EndingClient(async token =>
        {
            if (Volatile.Read(ref down))
                throw new HttpRequestException("No connection could be made.");

            await release.Task.WaitAsync(token);

            return new BatchResponse
            {
                Results =
                [
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["items"] = new System.Text.Json.Nodes.JsonArray(FakeRemoteClient.Row("id", Driver.ToString(), ("name", "Ada"))),
                        ["pageInfo"] = new System.Text.Json.Nodes.JsonObject { ["hasNextPage"] = false },
                    },
                ],
            };
        });

        // Two hosts' worth of state would hide it: both requests share the record, and neither reads the other's rows from a cache.
        MongoQueryEngine Engine(OwnerAvailability shared) =>
            new(new StaticEntityModelProvider(model.Value), new FakeAggregateRunner { PageRows = [Row("driver", Driver)] }, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options)) { Availability = shared };

        var availability = new OwnerAvailability(options, time);
        var one = Engine(availability);
        var other = Engine(availability);

        (await RunAsync(one)).Diagnostics.Should().ContainSingle();
        (await RunAsync(other)).Diagnostics.Should().ContainSingle().Which.Params.Should().Contain("remembered", true);
        client.Calls.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(5));
        Volatile.Write(ref down, false);

        var first = RunAsync(one);

        SpinWait.SpinUntil(() => client.Calls == 2, TimeSpan.FromSeconds(30)).Should().BeTrue("the first request asks the owner again");

        // The second arrives while the first one's call is still under way.
        var second = RunAsync(other);

        SpinWait.SpinUntil(() => client.Calls == 3 || second.IsCompleted, TimeSpan.FromSeconds(30)).Should().BeTrue();
        release.SetResult();

        foreach (var result in await Task.WhenAll(first, second))
        {
            result.Diagnostics.Should().BeNullOrEmpty("the owner answers again, so nothing is said from memory");
            result.Items[0]!["res"]!["name"]!.GetValue<string>().Should().Be("Ada");
        }

        client.Calls.Should().Be(3);
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

        diagnostic.Message.Should().Contain("did not answer on its query route a moment ago").And.NotContain("InternalApiVersions");
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

        var remembered = second.Diagnostics.Should().ContainSingle().Subject;

        remembered.Code.Should().Be(Codes.ResolveTimeout);
        remembered.Params.Should().Contain("reason", "timeout").And.Contain("remembered", true);

        // A request with 300 ms in all leaves its owner call less than half of what one call may take.
        var (hurried, hurriedRunner, hurriedClient, _) = Host(options => options.Execution.ResolveTimeoutMs = 2_000);

        hurriedRunner.PageRows = [Row("driver", Driver)];
        hurriedClient.Silent.Add("staff");

        await hurried.ExecuteAsync(BindHost.Parse($$"""{ "entityType": "{{Tour}}", "maxTimeMs": 300, "pipeline": {{Resolve}} }"""), BindHost.Context());

        hurriedClient.Calls.Should().ContainSingle();
        hurried.Availability.OutageOf("staff").Should().BeNull("the request ran out of time, which says nothing of the owner");
    }

    /// <summary>An owner whose every batch ends as the test says, counted.</summary>
    private sealed class EndingClient(Func<CancellationToken, Task<BatchResponse>> end) : IRemoteQueryClient
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);

            return end(cancellationToken);
        }

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static (MongoQueryEngine Engine, EndingClient Client) Ending(Func<CancellationToken, Task<BatchResponse>> end, EntityModel? entities = null)
    {
        var options = BindHost.Options(configure => configure.Execution.ResolveTimeoutMs = 60);
        var client = new EndingClient(end);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(entities ?? model.Value), new FakeAggregateRunner { PageRows = [Row("driver", Driver)] }, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options))
        {
            Availability = new OwnerAvailability(options, new ManualTime()),
        };

        return (engine, client);
    }

    private static async Task<BatchResponse> Never(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        throw new OperationCanceledException(cancellationToken);
    }

    private static Func<CancellationToken, Task<BatchResponse>> EndOf(string cause) => cause switch
    {
        "timeout" => Never,
        "unreachable" => _ => throw new HttpRequestException("No connection could be made."),
        "route" => _ => throw new HttpRequestException("The owner did not answer with success.", null, HttpStatusCode.NotFound),
        _ => _ => throw new HttpRequestException("The owner did not answer with success.", null, HttpStatusCode.ServiceUnavailable),
    };

    [Theory]
    [InlineData("unreachable", Codes.ResolveUnreachable, "unreachable", null)]
    [InlineData("timeout", Codes.ResolveTimeout, "timeout", null)]
    [InlineData("route", Codes.ResolveUnreachable, "route", 404)]
    [InlineData("gateway", Codes.ResolveUnreachable, "status", 503)]
    public async Task The_answer_from_memory_has_the_code_the_reason_and_the_status_of_the_call_that_failed(string cause, string code, string reason, int? status)
    {
        var (engine, client) = Ending(EndOf(cause));

        var first = (await RunAsync(engine)).Diagnostics.Should().ContainSingle().Subject;
        var second = (await RunAsync(engine)).Diagnostics.Should().ContainSingle().Subject;

        client.Calls.Should().Be(1, "the second answer is given from what the host remembers");

        first.Code.Should().Be(code);
        first.Params.Should().Contain("reason", reason).And.NotContainKey("remembered");

        second.Code.Should().Be(code, "what is remembered is that call, so it is said under that call's code");
        second.Params.Should().Contain("reason", reason).And.Contain("remembered", true).And.Contain("retryAfterMs", 5000);
        second.Message.Should().Contain("was not asked");

        foreach (var diagnostic in new[] { first, second })
        {
            if (status is { } answered)
                diagnostic.Params.Should().Contain("status", answered);
            else
                diagnostic.Params.Should().NotContainKey("status");
        }
    }

    [Theory]
    [InlineData("unreachable", "unreachable", "Unreachable")]
    [InlineData("timeout", "timeout", "Timeout")]
    public async Task A_semi_join_refused_from_memory_says_what_the_call_that_failed_said(string cause, string reason, string word)
    {
        const string semiJoin = """[{ "resolve": { "path": "vehicleId", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "V-1" } } }, { "page": { "limit": 10 } }]""";
        var (engine, client) = Ending(EndOf(cause), BindHost.Probe);

        var first = (await engine.ExecuteAsync(BindHost.Request("probe.order", semiJoin), BindHost.Context())).Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal.Errors![0];
        var second = (await engine.ExecuteAsync(BindHost.Request("probe.order", semiJoin), BindHost.Context())).Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal.Errors![0];

        client.Calls.Should().Be(1);

        first.Code.Should().Be(Codes.ResolveUnavailable);
        second.Code.Should().Be(Codes.ResolveUnavailable);
        first.Message.Should().Contain($"({word})");
        second.Message.Should().Be(first.Message);
        first.Params.Should().Contain("reason", reason).And.NotContainKey("remembered");
        second.Params.Should().Contain("reason", reason).And.Contain("remembered", true);
    }

    public static TheoryData<string, Exception, bool> Faults => new()
    {
        { "no connection", new HttpRequestException("No connection could be made."), true },
        { "the connection broke", new InvalidOperationException("The answer could not be read.", new IOException("The connection was reset.")), true },
        { "a socket error", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset), true },
        { "no JSON", new System.Text.Json.JsonException("'<' is an invalid start of a value."), false },
        { "another shape", new InvalidOperationException("The answer has no results."), false },
        { "a fault of the client", new NotSupportedException("The serializer has no converter."), false },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task Only_a_fault_of_the_transport_is_remembered_and_an_answer_that_could_not_be_read_is_not(string cause, Exception fault, bool kept)
    {
        var (engine, client) = Ending(_ => throw fault);

        var first = (await RunAsync(engine)).Diagnostics.Should().ContainSingle().Subject;

        first.Code.Should().Be(Codes.ResolveUnreachable, cause);
        first.Params.Should().Contain("reason", "unreachable");

        if (kept)
            engine.Availability.OutageOf("staff")!.Reason.Should().Be(OwnerAvailability.Unreachable, cause);
        else
            engine.Availability.OutageOf("staff").Should().BeNull("the owner answered, and what it sent this time says nothing of the next: " + cause);

        var second = (await RunAsync(engine)).Diagnostics.Should().ContainSingle().Subject;

        client.Calls.Should().Be(kept ? 1 : 2, cause);
        second.Params!.ContainsKey("remembered").Should().Be(kept, cause);
    }

    [Fact]
    public async Task A_caller_that_gives_up_while_an_owner_is_asked_leaves_nothing_remembered()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (engine, client) = Ending(token =>
        {
            asked.TrySetResult();

            return Never(token);
        });
        using var caller = new CancellationTokenSource();

        var running = engine.ExecuteAsync(BindHost.Request(Tour, Resolve), BindHost.Context(), caller.Token);

        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await caller.CancelAsync();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();

        client.Calls.Should().Be(1);
        engine.Availability.OutageOf("staff").Should().BeNull("the caller's own end says nothing of the owner");
        engine.Availability.TryEnter("staff", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_caller_that_gives_up_while_an_owner_fails_leaves_nothing_remembered()
    {
        using var caller = new CancellationTokenSource();
        var (engine, _) = Ending(_ =>
        {
            // The connection the caller's abort reset: the fault comes after the caller has gone.
            caller.Cancel();

            throw new HttpRequestException("The connection was reset.");
        });

        await FluentActions.Awaiting(() => engine.ExecuteAsync(BindHost.Request(Tour, Resolve), BindHost.Context(), caller.Token)).Should().ThrowAsync<OperationCanceledException>();

        engine.Availability.OutageOf("staff").Should().BeNull();
    }

    [Fact]
    public async Task A_page_in_several_batches_after_the_time_is_over_is_whole_when_the_owner_answers_again()
    {
        var drivers = Enumerable.Range(1, 4).Select(index => Guid.Parse($"d0000000-0000-0000-0000-00000000000{index}")).ToList();
        var (engine, runner, client, time) = Host(options => options.Limits.ResolveKeyChunk = 1);

        // The owner takes one query per batch, so the page's four chunks go out as four batches.
        client.Owners["staff"] = new RemoteOwnerInfo("2.1.0", EngineCapabilities.Contract, 1);
        runner.PageRows = drivers.Select(driver => Row("driver", driver)).ToList();
        client.Unreachable.Add("staff");

        var down = await RunAsync(engine);

        down.Items.Should().OnlyContain(item => item!["res"] == null);
        client.Calls.Should().ContainSingle("the first batch failed and the others were not sent");

        time.Advance(TimeSpan.FromSeconds(5));
        client.Unreachable.Clear();
        client.Calls.Clear();
        client.Script = (_, query, _) => new FakeRemoteClient.Answer.Rows(drivers.Where(driver => System.Text.Json.JsonSerializer.Serialize(query).Contains(driver.ToString())).Select(driver => FakeRemoteClient.Row("id", driver.ToString(), ("name", "N-" + driver.ToString()[^1]))).ToArray());

        var back = await RunAsync(engine);

        back.Diagnostics.Should().BeNullOrEmpty("the batch that tries the owner lands before the next one is sent, so none of them is answered from memory");
        back.Items.Select(item => item!["res"]?["name"]?.GetValue<string>()).Should().Equal("N-1", "N-2", "N-3", "N-4");
        client.Calls.Should().HaveCount(4);
        engine.Availability.OutageOf("staff").Should().BeNull();
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
    public async Task Health_names_the_route_for_an_owner_whose_health_is_not_found_and_for_one_a_join_found_without_its_query_route()
    {
        // The owner's host answers 404 to the health of the API version it is called with.
        var (_, notFound) = await ProbedAsync((_, _) => Task.FromException<bool>(new HttpRequestException("Not found.", null, System.Net.HttpStatusCode.NotFound)));

        notFound.Should().OnlyContain(state => state.Reachable == false && state.Usable == false && state.Reason == RemoteServiceState.Route);

        // Its health answers, and a join found the query route missing: the same word, while the host remembers it.
        var (_, remembered) = await ProbedAsync((_, _) => Task.FromResult(true), before: record => record.Failed("staff", OwnerAvailability.Route, 404));

        remembered.Single(state => state.Service == "staff").Should().Match<RemoteServiceState>(state => state.Reachable == true && state.Usable == false && state.Reason == RemoteServiceState.Route && state.Degrades);
        remembered.Where(state => state.Service != "staff").Should().OnlyContain(state => state.Reason == null);
    }

    [Fact]
    public void Health_says_of_a_service_nothing_was_measured_of_that_it_is_pending()
    {
        var options = BindHost.Options();
        var gate = new TaskCompletionSource<bool>();
        var probe = new RemoteHealthProbe(options);
        var states = probe.State(model.Value, new ProbedClient((_, _) => gate.Task));

        states.Should().NotBeEmpty().And.OnlyContain(state => state.Configured && state.Reachable == null && state.Usable == null && state.Reason == RemoteServiceState.Pending && !state.Degrades);
        gate.SetResult(true);
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
