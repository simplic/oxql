using System.Net;
using FluentAssertions;
using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// <c>keyedBy</c> is internal only, on every public route (RE-26): query, batch and explain answer
/// <c>UNKNOWN_REQUEST_MEMBER</c> without the contract 1 hint, which would suggest the header is what
/// is missing (RE-22); and a member a batch does not have is refused rather than dropped.
/// </summary>
public class HostKeyedByTests
{
    private const string Keyed = """{ "entityType": "probe.customer", "keyedBy": { "path": "number", "keys": ["a"] }, "pipeline": [] }""";

    [Fact]
    public async Task Query_refuses_keyedBy_as_a_member_a_request_does_not_have()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(Keyed));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().NotContain("X-OxQL-Contract");
    }

    [Fact]
    public async Task Batch_refuses_keyedBy_in_the_query_it_carries()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/batch", SampleHost.Json($$"""{ "queries": [{{Keyed}}] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["results"]![0]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
    }

    [Fact]
    public async Task Explain_answers_keyedBy_as_invalid()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/explain", SampleHost.Json($$"""{ "query": {{Keyed}} }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["valid"]!.GetValue<bool>().Should().BeFalse();
        body["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
    }

    [Fact]
    public async Task A_batch_level_strict_is_refused_rather_than_dropped()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/batch", SampleHost.Json("""{ "strict": true, "queries": [{ "entityType": "probe.order", "pipeline": [] }] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().Contain("'strict'");
    }
}
