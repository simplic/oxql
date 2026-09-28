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
        var client = new FakeRemoteClient { Probe = _ => new RemoteOwnerInfo("2.1.0.0", 2, MaxBatchQueries: 1) };
        var options = BindHost.Options(configure => configure.Limits.ResolveKeyChunk = 1);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        runner.PageRows = [ContactRow(Guid.NewGuid(), Guid.NewGuid()), ContactRow(Guid.NewGuid(), Guid.NewGuid())];
        client.Script = (_, query, _) => new FakeRemoteClient.Answer.Rows();

        await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""), BindHost.Context());

        client.Probed.Should().Equal("crm");
        client.Calls.Select(call => call.Request.Queries.Count).Should().Equal([1, 1], "the owner's cap of one was known before the first batch");
    }

    [Fact]
    public async Task An_owner_first_known_to_run_2_0_refuses_a_chain_before_any_batch_is_sent()
    {
        var runner = new FakeAggregateRunner { PageRows = [ContactRow(Guid.NewGuid(), ContactId)] };
        var client = new FakeRemoteClient { Probe = _ => new RemoteOwnerInfo("2.0.126.924", 2, null) };
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, """
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }]
            """), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors!.Single().Code.Should().Be(Codes.OwnerNotCapable);
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Explain_names_the_api_version_the_client_routes_the_owner_to_and_the_hosts_schema_revision()
    {
        var client = new FakeRemoteClient { ApiVersions = { ["crm"] = "v2" } };
        var engine = new MongoQueryEngine(new RevisionedModels(ResolveModel.Model, "sha256:abc"), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), client);

        var explained = await engine.ExplainAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""), BindHost.Context());

        var result = explained.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        var owner = result.Steps.Single(step => step.Index == 0).Owner!;

        owner["route"]!.ToJsonString().Should().Be("""{"apiName":"crm-api","apiVersion":"v2"}""");
        owner["targets"]![0]!["route"]!["apiVersion"]!.GetValue<string>().Should().Be("v2");
        result.SchemaRevision.Should().Be("sha256:abc");
    }
}
