using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
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

/// <summary>An order that declares a reference to an entity its owner does not have.</summary>
public class UtOrder
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = "";

    [OxQLReference("crm.person", "id")]
    public Guid PersonId { get; set; }

    [OxQLReference("crm.contact", "id")]
    public Guid ContactId { get; set; }
}

/// <summary>
/// An owner that refuses the owner query itself (the target is no entity of its model, say) refuses
/// nothing the caller wrote at a stage of its own: the refusal lies at the resolve. Explain says it
/// there as an error, with the owner's error beside its <c>RESOLVE_REFUSED</c> head, and a run is
/// refused with the same two errors, so the two agree.
/// </summary>
public class OwnerRefusedTargetTests
{
    private const string Order = "ut.order";
    private const string Unknown = """[{ "resolve": { "path": "personId", "as": "person" } }, { "page": { "limit": 5 } }]""";
    private const string Known = """[{ "resolve": { "path": "contactId", "as": "contact" } }, { "page": { "limit": 5 } }]""";

    private static readonly Lazy<EntityModel> model = new(() => ClrModelBuilder.Build(
        new EntityDeclaration[] { new(Order, Order, typeof(UtOrder), "orders", null, false) }, retiredIds: null, new ReferenceDeclarations()));

    /// <summary>The owner <c>crm</c> as a real engine over its own model: it answers explains and batches as an owner does.</summary>
    private sealed class CrmOwner : IRemoteQueryClient, IRemoteOwnerInfo
    {
        private readonly MongoQueryEngine engine = OwnerFleet.Engine(OwnerFleet.Crm);

        public int Explains { get; private set; }

        public RemoteOwnerInfo? OwnerOf(string serviceKey) => new("2.1.0", EngineCapabilities.Contract, 10);

        public bool IsConfigured(string serviceKey) => serviceKey == "crm";

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            var results = new List<JsonNode?>();

            foreach (var query in request.Queries)
            {
                var outcome = await engine.ExecuteAsync(query, BindHost.Context() with { Internal = true }, cancellationToken);

                results.Add(outcome switch
                {
                    QueryOutcome.Success success => JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire),
                    QueryOutcome.Refused refused => JsonSerializer.SerializeToNode(refused.Refusal, OxQLJson.Wire),
                    _ => null,
                });
            }

            return new BatchResponse { Results = results };
        }

        public Task<JsonObject?> ExplainAsync(string serviceKey, ExplainRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            Explains++;

            return Task.FromResult(OwnerFleet.Answer(engine, request));
        }
    }

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, CrmOwner Owner) Host()
    {
        var runner = new FakeAggregateRunner();
        var owner = new CrmOwner();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(model.Value), runner, BindHost.Cursors, options, owner, cache: new OwnerFetchCache(options));

        return (engine, runner, owner);
    }

    private static async Task<ExplainResult> ExplainAsync(MongoQueryEngine engine, string pipeline, string envelope = "")
    {
        var body = $$"""{ "query": { "entityType": "{{Order}}", "pipeline": {{pipeline}} }{{envelope}} }""";
        var outcome = await engine.ExplainAsync(ExplainAnswer.Envelope(body), BindHost.Context());

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    [Fact]
    public async Task Explain_says_a_target_the_owner_does_not_have_as_an_error_at_the_resolve()
    {
        var (engine, _, owner) = Host();

        var result = await ExplainAsync(engine, Unknown);

        owner.Explains.Should().Be(1);
        result.Valid.Should().BeFalse("the owner refuses the query a run sends it");
        result.Executable.Should().Be(Basis.Never, "the model declares a target its owner does not have");
        result.Errors.Select(error => error.Code).Should().Equal(Codes.ResolveRefused, Codes.UnknownEntity);
        result.Errors.Should().OnlyContain(error => error.Stage == 0);
        result.Stage(0).Status.Should().Be("error");

        var head = result.Errors[0];

        head.Message.Should().Contain("crm.person").And.Contain("refused the query");
        head.Basis.Should().Be(Basis.Model);

        var owned = result.Errors[1];

        owned.Message.Should().Contain("'crm.person' is not an entity of this host");
        owned.Basis.Should().Be(Basis.Model);
        JsonSerializer.SerializeToNode(owned.Params, OxQLJson.Wire)!["owner"]!.ToJsonString()
            .Should().Be("""{"service":"crm","entity":"crm.person","target":"crm.person","stage":null,"path":null}""");
    }

    [Fact]
    public async Task A_run_is_refused_with_the_same_two_errors()
    {
        var (engine, runner, _) = Host();

        runner.PageRows =
        [
            new BsonDocument
            {
                ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
                ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
                ["Number"] = "O-1",
                ["PersonId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            },
        ];

        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, Unknown), BindHost.Context());
        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;
        var explained = await ExplainAsync(engine, Unknown);

        refusal.Status.Should().Be(422);
        refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveRefused, Codes.UnknownEntity);
        refusal.Errors![0].Stage.Should().Be(0);
        refusal.Errors[0].Message.Should().Be(explained.Errors[0].Message, "explain says what the run says");
        refusal.Errors[1].Message.Should().Be(explained.Errors[1].Message);
        refusal.Errors[1].Stage.Should().Be(0, "the owner's word on the target lies at the resolve that names it");
        JsonSerializer.SerializeToNode(refusal.Errors[1].Params, OxQLJson.Wire)!["owner"]!.ToJsonString()
            .Should().Be(JsonSerializer.SerializeToNode(explained.Errors[1].Params, OxQLJson.Wire)!["owner"]!.ToJsonString());
    }

    [Fact]
    public async Task A_target_the_owner_has_is_valid_as_before()
    {
        var (engine, _, _) = Host();

        var result = await ExplainAsync(engine, Known);

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Executable.Should().Be(Basis.Yes);
    }

    [Fact]
    public async Task An_owner_error_at_a_stage_this_host_added_to_the_owner_query_stays_this_hosts_own()
    {
        // The check key has the form of a guid; an owner whose field is of another kind refuses the key
        // match, which is this host's making and says nothing of the page's real keys.
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient
        {
            Explains = (_, _) => new JsonObject
            {
                ["valid"] = false,
                ["errors"] = new JsonArray(new JsonObject { ["code"] = "INVALID_OPERAND", ["message"] = "the key", ["stage"] = 0, ["path"] = "id" }),
            },
        };
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(model.Value), runner, BindHost.Cursors, options, client);

        var result = await ExplainAsync(engine, Known);

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
}
