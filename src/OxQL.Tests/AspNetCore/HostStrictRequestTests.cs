using System.Net;
using FluentAssertions;
using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The host's side of OxQL 2.1 request members (DESIGN §3.0, §3.12) and the published limits
/// (§3.7): an unknown top-level member is refused, <c>strict</c> under contract 1 is refused
/// with the hint that the request was read as contract 1, and health publishes the 2.1 limits
/// with the values the host runs under.
/// </summary>
public class HostStrictRequestTests
{
    private const string Query = "/OxQL/query";

    [Fact]
    public async Task An_unknown_top_level_member_under_contract_2_is_a_400_UNKNOWN_REQUEST_MEMBER()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.order", "strictMode": true, "pipeline": [] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().StartWith("'strictMode' is not a member of a request");
    }

    [Fact]
    public async Task Strict_without_the_contract_header_is_refused_with_the_contract_1_hint()
    {
        using var host = new SampleHost();

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.order", "strict": true, "pipeline": [] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.LegacyStageUnsupported);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().EndWith("This request was read as contract 1 because it carries no 'X-OxQL-Contract: 2' header.");
    }

    [Fact]
    public async Task A_join_stage_without_the_contract_header_is_refused_by_the_compat_binder_with_the_hint()
    {
        using var host = new SampleHost();

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(
            """{ "entityType": "probe.customer", "pipeline": [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "first": true } }] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.LegacyStageUnsupported, "the code is unchanged");
        body["errors"]![0]!["message"]!.GetValue<string>().Should().EndWith(Binder.Contract1Hint);
    }

    [Fact]
    public async Task A_strict_request_under_contract_2_reaches_the_engine()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.order", "strict": true, "pipeline": [{ "page": { "limit": 2000 } }] }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_publishes_the_2_1_limits_as_the_host_configured_them()
    {
        using var host = new SampleHost(options =>
        {
            options.Limits.MaxReportPageSize = 2_000;
            options.Limits.MaxContinuedStages = 3;
            options.Limits.MaxFlattenDepth = 4;
            options.Limits.MaxReportedRows = 9;
            options.Execution.MaxTimeMs = 4_000;
            options.Execution.ChainTimeoutMs = 6_000;
            options.Cache.NegativeResolveTtlSeconds = 0;
        });

        var body = await SampleHost.Body(await host.CreateClient().GetAsync("/OxQL/health"));
        var limits = body!["limits"]!.AsObject();

        limits["maxReportPageSize"]!.GetValue<int>().Should().Be(2_000);
        limits["maxContinuedStages"]!.GetValue<int>().Should().Be(3);
        limits["maxFlattenDepth"]!.GetValue<int>().Should().Be(4);
        limits["maxReportedRows"]!.GetValue<int>().Should().Be(9);
        limits["chainTimeoutMs"]!.GetValue<int>().Should().Be(4_000, "the chain budget is published as it applies, under the request ceiling");
        limits["negativeResolveTtlSeconds"]!.GetValue<int>().Should().Be(0);
    }
}
