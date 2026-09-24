using System.Net;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Mongo.Explain;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The server explain behind the index advisory executes the page pipeline, so it runs under
/// the ceiling the query itself would run under.
/// </summary>
public class HostHardeningExplainTests
{
    private const string WithLookup = """{ "entityType": "probe.customer", "pipeline": [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "page": { "limit": 5 } }] }""";

    [Fact]
    public async Task The_server_explain_of_a_lookup_runs_under_the_effective_time_ceiling()
    {
        using var host = new SampleHost(options =>
        {
            options.Explain.Enabled = true;
            options.Execution.MaxTimeMs = 1_234;
        });

        var response = await host.Client().PostAsync("/OxQL/explain", SampleHost.Json(WithLookup));

        response.StatusCode.Should().Be(HttpStatusCode.OK, (await SampleHost.Body(response))?.ToJsonString());
        host.Indexes.ExplainCalls.Should().Be(1);
        host.Indexes.ExplainMaxTimeMs.Should().Be(1_234);
        host.Runner.Calls.Should().BeEmpty("explain returns no rows and runs no count");
    }

    [Fact]
    public void The_explain_command_carries_the_ceiling_on_the_command_the_server_runs()
    {
        var entity = BindHost.Probe.Entities["probe.customer"];
        var stages = new[] { new BsonDocument("$limit", 6) };

        var command = MongoIndexSource.ExplainCommand(entity, stages, 1_234);

        command["maxTimeMS"].AsInt32.Should().Be(1_234);
        command["verbosity"].AsString.Should().Be("executionStats");
        command["explain"]["aggregate"].AsString.Should().Be(entity.Collection);
        command["explain"]["pipeline"].AsBsonArray.Should().Equal(stages);
        command.GetElement(0).Name.Should().Be("explain", "the first element names the command");

        MongoIndexSource.ExplainCommand(entity, stages, 0)["maxTimeMS"].AsInt32.Should().Be(1, "zero would mean no limit");
    }
}
