using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using OxQL.AspNetCore.Health;
using OxQL.Core.Attributes;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Hosts;
using OxQL.Model.Attributes;
using Xunit;

namespace OxQL.IntegrationTests.Suites.NonQuery;

/// <summary>
/// Area Y, the engine half: <c>OxQL/health</c>, <c>OxQL/explain</c> and the routes that answer
/// nothing. The legacy battery also covered <c>/schema</c>, <c>/schema/addons</c>, their ETags
/// and the internal batch route; those are OxS controllers (the schema build and the
/// <c>i-api-key</c> scheme) and stay with the base package's tests.
/// </summary>
[Trait("Category", "Integration")]
public class NonQueryTests
{
    private static readonly string[] Universal = ["batch", "group.page", "page.offset", "any"];

    private static IEnumerable<string> Strings(JsonNode? node) => (node as JsonArray ?? []).Select(item => item!.GetValue<string>());

    /// <summary>
    /// The services a lab service's model reaches remotely, read off the model classes: every
    /// <c>[OxQLReference]</c> and <c>[OxQLReferenceWhen]</c> target at any depth of any of the
    /// service's entities (their registered variants included) that lives in another service and
    /// names the target field (a fieldless remote reference is a build finding, not a
    /// reference), and every target of the service's host-side declarations.
    /// </summary>
    private static IReadOnlyList<string> RemoteServicesOf(LabService service)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<Type>();
        var variants = BsonClassMap.GetRegisteredClassMaps().Select(map => map.ClassType).ToList();

        void Add(string target, string? field)
        {
            if (field is not null && target.Split('#')[0].Split('.')[0] is var key && key != service.Key)
                found.Add(key);
        }

        void Walk(Type type)
        {
            if (!seen.Add(type) || type.Namespace?.StartsWith("OxQL.IntegrationTests", StringComparison.Ordinal) != true)
                return;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetCustomAttribute<OxQLReferenceAttribute>() is { } reference)
                    Add(reference.Entity, reference.Field);

                foreach (var when in property.GetCustomAttributes<OxQLReferenceWhenAttribute>())
                    foreach (var target in when.Targets)
                        Add(target, when.Field);

                var member = property.PropertyType;
                var element = member.IsGenericType && typeof(IEnumerable).IsAssignableFrom(member) ? member.GetGenericArguments().Last() : member;
                Walk(Nullable.GetUnderlyingType(element) ?? element);
            }

            foreach (var variant in variants.Where(variant => variant != type && type.IsAssignableFrom(variant)))
                Walk(variant);
        }

        var entities = typeof(LabService).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<OxQLTypeAttribute>() is { } declared && declared.TypeName.StartsWith(service.Key + ".", StringComparison.Ordinal));

        foreach (var entity in entities)
            Walk(entity);

        foreach (var declaration in service.References?.All ?? [])
            foreach (var target in declaration.Targets)
                Add(target, declaration.Field);

        return found.ToList();
    }

    [Fact]
    public async Task Y1_Y2_health_is_anonymous_on_every_host_and_carries_the_whole_body_under_the_literal_service_name()
    {
        foreach (var service in LabService.All)
        {
            var health = await (await Lab.ClientAsync(service)).Anonymous().Contract(null).HealthAsync();

            health.StatusCode.Should().Be(200, $"{service}: {health}");
            health.Body!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal)
                .Should().Equal(["capabilities", "engine", "limits", "remote", "service", "status"], service.Key);
            health.Body!["engine"]!["contract"]!.GetValue<int>().Should().Be(2);
            health.Body!["engine"]!["version"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            // H-2: the body cannot say who answered.
            health.Body!["service"]!.GetValue<string>().Should().Be("oxql");
        }
    }

    [Fact]
    public async Task Y3_Y4_Y5_Y6_the_capabilities_are_the_universal_four_plus_exactly_the_features_a_host_has_switched_on()
    {
        var expectedFleet = Universal.Concat(["resolve.remote", "semiJoin", "compat.v1"]).ToList();

        foreach (var service in LabService.All)
            Strings((await (await Lab.ClientAsync(service)).HealthAsync()).Body!["capabilities"]).Should().Equal(expectedFleet, service.Key);

        var shared = await CorpusFleet.SharedAsync();

        // Y5: explain is published exactly when it is on, and the route answers to match.
        var explain = await shared.Fleet.VariantAsync(LabService.Transport, "explain", new Dictionary<string, string?> { ["OxQL:Explain:Enabled"] = "true" });
        using (var client = explain.Server.CreateClient())
        {
            var body = JsonNode.Parse(await client.GetStringAsync("OxQL/health"))!;
            Strings(body["capabilities"]).Should().Equal(Universal.Concat(["resolve.remote", "semiJoin", "explain", "compat.v1"]));
        }

        // Y6: compat.v1 is published exactly when the compat binder is on.
        var noCompat = shared.Variant(LabService.Staff, "b5-compat-off", new Dictionary<string, string?> { ["OxQL:Compat:Enabled"] = "false" });
        Strings((await noCompat.HealthAsync()).Body!["capabilities"]).Should().Equal(Universal.Concat(["resolve.remote", "semiJoin"]));

        // Y4: resolve.remote and semiJoin need a remote query client.
        await using var bare = await CustomHost.StartAsync(LabService.Transport, await CustomHost.SharedDatabaseAsync(LabService.Transport));
        var bareHealth = await bare.GetAsync("OxQL/health");
        Strings(bareHealth.Body!["capabilities"]).Should().Equal(Universal.Concat(["compat.v1"]));
        bareHealth.Body!.AsObject().ContainsKey("remote").Should().BeFalse("a host with no remote client has no remote state to report");
    }

    [Fact]
    public async Task Y8_the_limits_member_publishes_all_twenty_five_limits_with_the_values_the_host_runs_under()
    {
        var defaults = new LimitOptions();
        var options = new OxQLOptions();
        var expected = new Dictionary<string, int>
        {
            ["maxPageSize"] = defaults.MaxPageSize, ["defaultPageSize"] = defaults.DefaultPageSize, ["maxPipelineStages"] = defaults.MaxPipelineStages,
            ["maxLookupStages"] = defaults.MaxLookupStages, ["maxUnwindStages"] = defaults.MaxUnwindStages, ["maxResolveStages"] = defaults.MaxResolveStages,
            ["maxGroupFields"] = defaults.MaxGroupFields, ["maxProjectionFields"] = defaults.MaxProjectionFields, ["maxConditions"] = defaults.MaxConditions,
            ["maxVariables"] = defaults.MaxVariables, ["maxOffset"] = defaults.MaxOffset, ["countCap"] = defaults.CountCap,
            ["maxSemiJoinIds"] = defaults.MaxSemiJoinIds, ["resolveKeyChunk"] = defaults.ResolveKeyChunk, ["maxResolveKeys"] = defaults.MaxResolveKeys,
            ["maxRequestBytes"] = defaults.MaxRequestBytes, ["maxBatchQueries"] = defaults.MaxBatchQueries, ["regexMaxLength"] = defaults.RegexMaxLength,
            ["maxLookupLimit"] = defaults.MaxLookupLimit,
            // 2.1 (DESIGN §3.7)
            ["maxFlattenDepth"] = defaults.MaxFlattenDepth, ["maxContinuedStages"] = defaults.MaxContinuedStages, ["maxReportPageSize"] = defaults.MaxReportPageSize,
            ["maxReportedRows"] = defaults.MaxReportedRows, ["chainTimeoutMs"] = options.Execution.EffectiveChainTimeoutMs,
            ["negativeResolveTtlSeconds"] = options.Cache.NegativeResolveTtlSeconds,
        };
        expected.Should().HaveCount(25);
        expected["maxSemiJoinIds"].Should().Be(5000, "U20: the published cap is 5 000");

        var limits = (await (await Lab.ClientAsync(LabService.Transport)).HealthAsync()).Body!["limits"]!.AsObject();
        limits.ToDictionary(member => member.Key, member => member.Value!.GetValue<int>()).Should().BeEquivalentTo(expected);

        var capped = (await CorpusFleet.SharedAsync()).Variant(LabService.Transport, "b5-countcap-1000", new Dictionary<string, string?> { ["OxQL:Limits:CountCap"] = "1000" });
        (await capped.HealthAsync()).Body!["limits"]!["countCap"]!.GetValue<int>().Should().Be(1000, "the body reports the host's configuration, not the defaults");
    }

    [Fact]
    public async Task Y9_Y10_Y12_remote_lists_each_referenced_service_configured_and_reachable_and_a_host_without_references_lists_none()
    {
        foreach (var service in LabService.All)
        {
            var expected = RemoteServicesOf(service);
            var client = await Lab.ClientAsync(service);
            var host = await client.HostAsync();

            await client.HealthAsync();
            await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;
            var health = await client.HealthAsync();

            var remote = health.Body!["remote"]!.AsArray();
            remote.Select(entry => entry!["service"]!.GetValue<string>()).Should().Equal(expected, service.Key);
            remote.Should().AllSatisfy(entry =>
            {
                entry!["configured"]!.GetValue<bool>().Should().BeTrue();
                entry["reachable"]!.GetValue<bool>().Should().BeTrue();
            });
            health.Body!["status"]!.GetValue<string>().Should().Be("healthy", "every reference reachable");
        }

        RemoteServicesOf(LabService.Transport).Should().Equal(["fleet", "staff"]);
        RemoteServicesOf(LabService.Ledger).Should().Equal(["directory", "staff", "transport"]);
        RemoteServicesOf(LabService.Conformance).Should().Equal(["owner", "staff"]);
        RemoteServicesOf(LabService.Staff).Should().BeEmpty();
    }

    [Fact]
    public async Task Y12_an_owner_whose_health_is_down_makes_the_host_degraded_and_says_which_service()
    {
        await using var fleet = await CorpusFleet.CreateAsync("b5-y12-degraded", seed: []);
        fleet.Owner.SetHealth(ChaosHealth.Down);
        var client = fleet.Client(LabService.Conformance);
        var host = await client.HostAsync();

        await client.HealthAsync();
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;
        var health = await client.HealthAsync();

        health.Body!["status"]!.GetValue<string>().Should().Be("degraded", health.ToString());
        var byService = health.Body!["remote"]!.AsArray().ToDictionary(entry => entry!["service"]!.GetValue<string>(), entry => entry!["reachable"]!.GetValue<bool>());
        byService.Should().BeEquivalentTo(new Dictionary<string, bool> { ["owner"] = false, ["staff"] = true });
    }

    [Fact]
    public async Task Y11_reachability_is_measured_once_per_interval_however_often_health_is_asked()
    {
        await using var fleet = await CorpusFleet.CreateAsync("b5-y11-cached", seed: []);
        var client = fleet.Client(LabService.Conformance).Anonymous();
        var probe = (await client.HostAsync()).Services.GetRequiredService<RemoteHealthProbe>();

        await client.HealthAsync();
        await probe.Refreshing;

        for (var call = 0; call < 10; call++)
            (await client.HealthAsync()).StatusCode.Should().Be(200);

        await probe.Refreshing;
        fleet.Owner.HealthCalls.Should().Be(1, "one measurement per HealthProbeTtlSeconds (10 s), not one per request");

        // A shallow health call, the form one host asks of another, starts no measurement.
        var shallow = await client.HealthAsync(shallow: true);
        shallow.Body!.AsObject().ContainsKey("remote").Should().BeFalse();
        fleet.Owner.HealthCalls.Should().Be(1);
    }

    [Fact]
    public async Task Y22_explain_is_404_while_disabled_and_answers_the_bound_pipeline_without_rows_when_enabled()
    {
        var transport = await Lab.ClientAsync(LabService.Transport);
        var request = """{ "entityType": "transport.shipment", "pipeline": [{ "match": { "shipmentNumber": { "eq": "S-0001" } } }, { "page": { "limit": 1 } }] }""";

        var off = await transport.ExplainHereAsync(request);
        off.StatusCode.Should().Be(404);

        var on = await transport.ExplainAsync(request);
        on.StatusCode.Should().Be(200, on.ToString());
        ((JsonObject)on.Body!).ContainsKey("items").Should().BeFalse("explain returns no rows");
    }

    [Fact]
    public async Task Y29_the_retired_types_route_does_not_exist()
    {
        (await (await Lab.ClientAsync(LabService.Transport)).GetAsync("OxQL/types")).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Y31_each_route_answers_its_own_method_and_405_for_another()
    {
        // The legacy host answered 404 for a GET on the POST routes: its routing sat in the base
        // package. The engine's own controller answers 405 for all three, which is what A27 asks.
        var transport = await Lab.ClientAsync(LabService.Transport);

        (await transport.PostAsync("OxQL/health", "{}")).StatusCode.Should().Be(405);
        (await transport.GetAsync("OxQL/query")).StatusCode.Should().Be(405);
        (await transport.GetAsync("OxQL/batch")).StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task Y31_the_request_body_cap_and_the_batch_size_cap_hold_on_the_batch_route()
    {
        var transport = await Lab.ClientAsync(LabService.Transport);
        var huge = $$"""{ "queries": [{ "entityType": "transport.shipment", "pipeline": [{ "match": { "or": [{{string.Join(", ", Enumerable.Range(0, 6000).Select(i => $$"""{ "shipmentNumber": { "eq": "{{new string('x', 40)}}{{i}}" } }"""))}}] } }] }] }""";
        huge.Length.Should().BeGreaterThan(new LimitOptions().MaxRequestBytes);

        var tooLarge = await transport.BatchBodyAsync(huge);
        tooLarge.StatusCode.Should().Be(413);
        tooLarge.ErrorCodes.Should().Equal(["REQUEST_TOO_LARGE"]);

        var tooMany = await transport.BatchAsync(Enumerable.Repeat<object>("""{ "entityType": "transport.shipment", "pipeline": [{ "page": { "limit": 1 } }] }""", 11));
        tooMany.StatusCode.Should().Be(400);
        tooMany.ErrorCodes.Should().Equal(["BATCH_TOO_LARGE"]);
    }
}
