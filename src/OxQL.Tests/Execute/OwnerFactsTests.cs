using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// What the engine learns of an owner and says about it: the owner's facts read before the first
/// batch, so its batch cap and engine version gate that batch without anyone calling the health
/// route first (RS-4); the API version the host routes an owner to in explain's
/// <c>owner.route.apiVersion</c> (RS-1); the host's schema revision in explain's
/// <c>schemaRevision</c> (RE-11).
/// </summary>
public class OwnerFactsTests
{
    private const string Invoice = ResolveModel.Invoice;
    private static readonly Guid ContactId = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");

    private sealed class RevisionedModels(EntityModel model, string revision) : IEntityModelProvider
    {
        public EntityModel Model { get; } = model;

        public string? SchemaRevision { get; } = revision;
    }

    private static BsonDocument ContactRow(Guid invoice, Guid contact) => new()
    {
        ["_id"] = new BsonBinaryData(invoice, GuidRepresentation.Standard),
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
        ["ContactId"] = new BsonBinaryData(contact, GuidRepresentation.Standard),
    };

    [Fact]
    public async Task The_first_request_reads_the_owners_facts_before_it_sizes_the_batch()
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient { Probe = _ => new RemoteOwnerInfo("9.9.9", 2, MaxBatchQueries: 1) };
        var options = BindHost.Options(configure => configure.Limits.ResolveKeyChunk = 1);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        runner.PageRows = [ContactRow(Guid.NewGuid(), Guid.NewGuid()), ContactRow(Guid.NewGuid(), Guid.NewGuid())];
        client.Script = (_, query, _) => new FakeRemoteClient.Answer.Rows();

        await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""), BindHost.Context());

        client.Probed.Should().Equal("crm");
        client.Calls.Select(call => call.Request.Queries.Count).Should().Equal([1, 1], "the owner's cap of one was known before the first batch");
    }

    [Fact]
    public async Task A_slow_owner_health_read_takes_only_a_slice_of_the_phase_and_the_batch_still_goes_out_under_this_hosts_caps()
    {
        var runner = new FakeAggregateRunner { PageRows = [ContactRow(Guid.NewGuid(), ContactId)] };
        var client = new FakeRemoteClient
        {
            ProbeDelay = TimeSpan.FromSeconds(5),
            Probe = _ => new RemoteOwnerInfo("9.9.9", 2, null),
            Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice"))),
        };
        var options = BindHost.Options(configure => configure.Execution.MaxTimeMs = 2_000);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""") with { Strict = true }, BindHost.Context());

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1_500), "the facts read is cut at a slice of the 2 s phase, not the owner's health time");
        outcome.Should().BeOfType<QueryOutcome.Success>("the batch goes out under this host's caps and the owner answers in time")
            .Which.Result.Items[0]!["r"]!["name"]!.GetValue<string>().Should().Be("Alice");
        client.Calls.Should().ContainSingle();
        client.Calls[0].Budget.Should().BeGreaterThan(TimeSpan.FromMilliseconds(1_000), "the owner call keeps nearly all of the phase");
    }

    [Fact]
    public async Task Explain_names_the_api_version_the_client_routes_the_owner_to_and_the_hosts_schema_revision()
    {
        var client = new FakeRemoteClient { ApiVersions = { ["crm"] = "v2" } };
        var engine = new MongoQueryEngine(new RevisionedModels(ResolveModel.Model, "sha256:abc"), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), client);

        var explained = await engine.ExplainAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""), BindHost.Context());

        var result = explained.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        var owner = result.Owner(0)!;

        owner["service"]!.GetValue<string>().Should().Be("crm");
        owner["route"]!.ToJsonString().Should().Be("""{"apiName":"crm-api","apiVersion":"v2"}""");
        result.Target("r", "crm.contact")["owner"]!.GetValue<int>().Should().Be(result.Stage(0).Placement!.Owner);
        result.Revision.Schema.Should().Contain("rc", "sha256:abc");
    }
}
