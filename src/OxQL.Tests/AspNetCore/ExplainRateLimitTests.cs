using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OxQL.AspNetCore.Batch;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.TestCases;
using OxQL.Tests.Bind;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The rate and concurrency limits of explain: per organisation
/// and user a token bucket and a bound on what is in flight, per host a bound on what is in flight,
/// per calling service on the internal route; an explain over one is 429 with <c>Retry-After</c>
/// before any work, and none of it touches the query route.
/// </summary>
public class ExplainRateLimitTests
{
    private static ExplainOptions Limits(int perMinute = 20, int burst = 5, int perUser = 2, int perHost = 8, int perCaller = 4, int ownersPerMinute = 20, int ownerBurst = 5) => new()
    {
        RatePerMinute = perMinute,
        RateBurst = burst,
        OwnerRatePerMinute = ownersPerMinute,
        OwnerRateBurst = ownerBurst,
        MaxConcurrentPerUser = perUser,
        MaxConcurrentPerHost = perHost,
        MaxConcurrentPerCaller = perCaller,
    };

    [Fact]
    public void A_user_has_a_burst_of_tokens_refilled_evenly_and_another_user_a_bucket_of_its_own()
    {
        var time = new ManualTime();
        using var limiter = new ExplainRateLimiter(Limits(perUser: 100, perHost: 100), time);

        for (var explain = 0; explain < 5; explain++)
            limiter.Acquire("org|alice").Acquired.Should().BeTrue($"explain {explain + 1} is within the burst of 5");

        var refused = limiter.Acquire("org|alice");

        refused.Acquired.Should().BeFalse("the burst is spent");
        refused.Limit.Should().Be("rate");
        refused.Max.Should().Be(20);
        refused.RetryAfterSeconds.Should().Be(3, "20 a minute is a token every 3 seconds");
        limiter.Acquire("org|bob").Acquired.Should().BeTrue("the bucket is the user's, not the host's");
        limiter.Acquire("other|alice").Acquired.Should().BeTrue("and the organisation's");

        time.Advance(TimeSpan.FromSeconds(3));
        limiter.Acquire("org|alice").Acquired.Should().BeTrue("one token came back");
        limiter.Acquire("org|alice").Acquired.Should().BeFalse();

        time.Advance(TimeSpan.FromMinutes(5));

        Enumerable.Range(0, 6).Select(_ => limiter.Acquire("org|alice").Acquired).Should().Equal([true, true, true, true, true, false], "the bucket never holds more than the burst");
    }

    [Fact]
    public void A_bucket_nobody_asks_is_idle_once_it_is_full_again_so_its_partition_is_dropped()
    {
        var time = new ManualTime();
        using var bucket = new ExplainRateLimiter.ManualTokenBucket(burst: 2, TimeSpan.FromSeconds(3), time);

        time.Advance(TimeSpan.FromSeconds(30));
        bucket.IdleDuration.Should().Be(TimeSpan.FromSeconds(30), "a bucket never asked is full");

        bucket.AttemptAcquire().IsAcquired.Should().BeTrue();
        bucket.AttemptAcquire().IsAcquired.Should().BeTrue();
        bucket.IdleDuration.Should().BeNull("an empty bucket is state worth keeping");

        time.Advance(TimeSpan.FromSeconds(5));
        bucket.IdleDuration.Should().BeNull("one token of two is back");

        time.Advance(TimeSpan.FromSeconds(1));
        bucket.IdleDuration.Should().Be(TimeSpan.FromSeconds(6), "full again: a new bucket would answer the same");
        bucket.AttemptAcquire().IsAcquired.Should().BeTrue();
        bucket.AttemptAcquire().IsAcquired.Should().BeTrue();
        bucket.AttemptAcquire().IsAcquired.Should().BeFalse("reading the idle time took and gave nothing");
    }

    [Fact]
    public void A_user_has_two_explains_in_flight_and_a_host_eight_and_a_refused_one_costs_no_token()
    {
        using var limiter = new ExplainRateLimiter(Limits(burst: 3), new ManualTime());

        var first = limiter.Acquire("org|alice");
        var second = limiter.Acquire("org|alice");
        var third = limiter.Acquire("org|alice");

        third.Acquired.Should().BeFalse();
        third.Limit.Should().Be("concurrentPerUser");
        third.Max.Should().Be(2);
        third.RetryAfterSeconds.Should().Be(1);

        first.Dispose();
        var fourth = limiter.Acquire("org|alice");

        fourth.Acquired.Should().BeTrue("a finished explain frees its place, and the refused one took no token: this is the third of three");
        second.Dispose();
        fourth.Dispose();
        limiter.Acquire("org|alice").Limit.Should().Be("rate", "the burst of three is spent now");

        using var host = new ExplainRateLimiter(Limits(perUser: 2, perHost: 3), new ManualTime());
        var held = Enumerable.Range(0, 3).Select(user => host.Acquire($"org|user{user}")).ToList();

        held.Should().OnlyContain(lease => lease.Acquired);
        host.Acquire("org|user9").Limit.Should().Be("concurrentPerHost");
        held[0].Dispose();
        host.Acquire("org|user9").Acquired.Should().BeTrue();
    }

    [Fact]
    public void A_calling_service_has_four_internal_explains_in_flight()
    {
        using var limiter = new ExplainRateLimiter(Limits(), new ManualTime());
        var held = Enumerable.Range(0, 4).Select(_ => limiter.AcquireCaller("erp")).ToList();

        held.Should().OnlyContain(lease => lease.Acquired);

        var refused = limiter.AcquireCaller("erp");

        refused.Acquired.Should().BeFalse();
        refused.Limit.Should().Be("concurrentPerCaller");
        refused.Max.Should().Be(4);
        refused.Refusal().Status.Should().Be(429);
        refused.Refusal().Errors!.Single().Code.Should().Be(Codes.ExplainLimit);
        limiter.AcquireCaller("crm").Acquired.Should().BeTrue("the bound is per calling service");
        held[0].Dispose();
        limiter.AcquireCaller("erp").Acquired.Should().BeTrue();
        Enumerable.Range(0, 30).Select(_ => { using var lease = limiter.AcquireCaller("hr"); return lease.Acquired; }).Should().OnlyContain(acquired => acquired, "the internal route has no rate of its own: its callers are bounded by their own explains");
    }

    /// <summary>A model provider that counts how often the model is read: the binder's first act.</summary>
    private sealed class CountingModels(EntityModel model) : IEntityModelProvider
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public EntityModel Model
        {
            get
            {
                Interlocked.Increment(ref reads);
                return model;
            }
        }
    }

    // ---- what costs is charged when it costs ----------------------------------------------------------

    [Fact]
    public void By_default_a_hundred_explains_a_minute_are_admitted_and_only_those_that_call_owners_meet_the_tight_bucket()
    {
        var defaults = new ExplainOptions();

        (defaults.RatePerMinute, defaults.RateBurst, defaults.OwnerRatePerMinute, defaults.OwnerRateBurst).Should().Be((120, 30, 20, 5));

        var time = new ManualTime();
        using var limiter = new ExplainRateLimiter(new ExplainOptions { MaxConcurrentPerUser = 100, MaxConcurrentPerHost = 100 }, time);

        // An editor that explains on every settled edit: a hundred explains spread over one minute, none calling an owner.
        for (var explain = 0; explain < 100; explain++)
        {
            using var lease = limiter.Acquire("org|alice");

            lease.Acquired.Should().BeTrue($"explain {explain + 1} of a hundred in a minute is admitted");
            time.Advance(TimeSpan.FromSeconds(0.6));
        }
    }

    [Fact]
    public void The_owner_bucket_is_charged_apart_refills_evenly_is_the_users_own_and_says_when_a_check_is_admitted_again()
    {
        var time = new ManualTime();
        using var limiter = new ExplainRateLimiter(Limits(perUser: 100, perHost: 100), time);

        for (var explain = 0; explain < 5; explain++)
            limiter.ChargeOwner("org|alice").Should().Be(new ExplainOwnerAdmission(true, 20, 0), $"owner-calling explain {explain + 1} is within the burst of 5");

        limiter.ChargeOwner("org|alice").Should().Be(new ExplainOwnerAdmission(false, 20, 3), "the sixth in a burst is not admitted: one token comes back every three seconds");
        limiter.Acquire("org|alice").Acquired.Should().BeTrue("the explain itself is still admitted: only its owner calls are held back");
        limiter.ChargeOwner("org|bob").Admitted.Should().BeTrue("the bucket is the user's");
        limiter.ChargeOwner("other|alice").Admitted.Should().BeTrue("and the organisation's");

        time.Advance(TimeSpan.FromSeconds(3));
        limiter.ChargeOwner("org|alice").Admitted.Should().BeTrue("one token came back");
        limiter.ChargeOwner("org|alice").Admitted.Should().BeFalse();
    }

    [Fact]
    public async Task At_the_host_an_explain_that_may_not_call_owners_now_is_answered_200_from_what_is_kept_and_never_429()
    {
        var owner = new FakeRemoteClient { Explains = (_, _) => new JsonObject { ["valid"] = true, ["errors"] = new JsonArray() } };
        using var host = new SampleHost(options => { options.Explain.OwnerRateBurst = 1; options.Explain.OwnerRatePerMinute = 1; }, services => services.AddSingleton<IRemoteQueryClient>(owner));
        var client = host.Client();

        static string Query(string member) =>
            $$"""{ "entityType": "probe.order", "pipeline": [ { "resolve": { "path": "contactNumber", "as": "contact" } }, { "project": { "number": 1, "contact.{{member}}": 1 } } ] }""";

        var first = await client.PostAsync("/OxQL/explain", SampleHost.Json(Query("name")));
        var checkedAnswer = await SampleHost.Body(first);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        checkedAnswer!["notes"]!.AsArray().Should().NotContain(note => note!["code"]!.GetValue<string>() == Codes.ExplainLimit, "its owner was asked");
        owner.ExplainCalls.Should().ContainSingle("the first owner-calling explain is within the burst of one");

        // The owner bucket is empty; this explain would have to call the owner again.
        var second = await client.PostAsync("/OxQL/explain", SampleHost.Json(Query("email")));
        var held = await SampleHost.Body(second);

        second.StatusCode.Should().Be(HttpStatusCode.OK, "an explain over the owner rate degrades, it is not refused");
        held!["valid"]!.GetValue<bool>().Should().BeTrue();
        held["cache"]!["complete"]!.GetValue<bool>().Should().BeFalse();
        held["executable"]!.GetValue<string>().Should().Be("unknown");
        held["notes"]!.AsArray().Single(note => note!["code"]!.GetValue<string>() == Codes.ExplainLimit)!["params"]!.ToJsonString()
            .Should().Be("""{"limit":"ownerRate","max":1,"service":"crm","target":"crm.contact","retryAfter":60}""");
        owner.ExplainCalls.Should().ContainSingle("no owner was called for it");

        // An explain that asks no owner is not held back, and the kept answer of the first is still served.
        (await SampleHost.Body(await client.PostAsync("/OxQL/explain", SampleHost.Json("""{ "entityType": "probe.order", "pipeline": [] }"""))))!["executable"]!.GetValue<string>().Should().Be("yes");
        (await SampleHost.Body(await client.PostAsync("/OxQL/explain", SampleHost.Json(Query("name")))))!["notes"]!.AsArray()
            .Should().NotContain(note => note!["code"]!.GetValue<string>() == Codes.ExplainLimit, "what the first check kept is answered without a call");
        owner.ExplainCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task At_the_host_an_explain_over_the_rate_is_429_with_Retry_After_before_any_work_and_the_query_route_is_not_limited()
    {
        var models = new CountingModels(BindHost.Probe);
        using var host = new SampleHost(options => { options.Explain.RateBurst = 2; options.Explain.RatePerMinute = 1; }, services =>
        {
            services.RemoveAll<IEntityModelProvider>();
            services.AddSingleton<IEntityModelProvider>(models);
        });
        var client = host.Client();
        const string query = """{ "entityType": "probe.order", "pipeline": [] }""";

        (await client.PostAsync("/OxQL/explain", SampleHost.Json(query))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/OxQL/explain", SampleHost.Json(query))).StatusCode.Should().Be(HttpStatusCode.OK);

        var before = models.Reads;
        var limited = await client.PostAsync("/OxQL/explain", SampleHost.Json("this is not even JSON"));
        var body = await SampleHost.Body(limited);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the limiter answers before the body is read");
        limited.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));
        body!["type"]!.GetValue<string>().Should().Be("rate_limited");
        body["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.ExplainLimit);
        body["errors"]![0]!["params"]!.ToJsonString().Should().Be("""{"limit":"rate","max":1,"retryAfter":60}""");
        models.Reads.Should().Be(before, "nothing was bound");

        // Another user of the organisation, and another organisation, have buckets of their own.
        (await host.Client(organisation: Guid.NewGuid()).PostAsync("/OxQL/explain", SampleHost.Json(query))).StatusCode.Should().Be(HttpStatusCode.OK);

        // Run traffic is not explain traffic.
        for (var run = 0; run < 5; run++)
            (await client.PostAsync("/OxQL/query", SampleHost.Json(query))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_host_whose_explain_is_switched_off_is_not_limited_and_stays_a_404()
    {
        using var host = new SampleHost(options => { options.Explain.Enabled = false; options.Explain.RateBurst = 1; });
        var client = host.Client();

        for (var explain = 0; explain < 3; explain++)
            (await client.PostAsync("/OxQL/explain", SampleHost.Json("""{ "entityType": "probe.order", "pipeline": [] }"""))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
