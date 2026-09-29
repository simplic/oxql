using System.Net;
using FluentAssertions;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The host reads request bodies to <see cref="OxQLJson.MaxDepth"/> (RE-27): the depth MVC needs to
/// write explain's deep answers is the depth it reads, so a request nested past System.Text.Json's
/// default of 64 is read and bound (every host of the fleet runs the same limit, so a forwarded body
/// the origin accepted is read by its owner too), and one past the limit is refused as a body that
/// does not read, not as a fault.
/// </summary>
public class HostRequestDepthTests
{
    private static string Nested(int depth)
    {
        var condition = """{ "number": { "eq": "x" } }""";

        for (var level = 0; level < depth; level++)
            condition = $$"""{ "and": [{{condition}}] }""";

        return $$"""{ "entityType": "probe.order", "pipeline": [{ "match": {{condition}} }] }""";
    }

    [Fact]
    public async Task A_request_nested_past_the_default_depth_of_64_is_read_and_bound()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(Nested(40)));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_nested_past_the_hosts_depth_is_a_400_not_a_fault()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(Nested(OxQLJson.MaxDepth)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }
}
