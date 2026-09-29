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
/// The test gaps of the engine review (RE-26): explain sends no owner batch for a keyed-local, a
/// keyed-remote or a remotely checked stage; one cache never answers one organisation from another's
/// rows; keys the budget cut are not cached while cache hits cost nothing; a key both targets of a
/// union hold is ambiguous.
/// </summary>
public class ReviewTestGapTests
{
    private const string Invoice = ResolveModel.Invoice;
    private static readonly Guid ContactA = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    private static readonly Guid ContactB = Guid.Parse("c0000000-0000-0000-0000-00000000000b");

    /// <summary>Answers each aggregate with the rows of its entity and records every call.</summary>
    private sealed class EntityRunner : IAggregateRunner
    {
        public Dictionary<string, List<BsonDocument>> Rows { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add(entity.Id);

            return Task.FromResult<IReadOnlyList<BsonDocument>>(Rows.TryGetValue(entity.Id, out var rows) ? rows.Select(row => row.DeepClone().AsBsonDocument).ToList() : []);
        }
    }

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonDocument ContactRow(Guid contact, Guid? organisation = null) => new()
    {
        ["_id"] = Id(Guid.NewGuid()),
        ["OrganizationId"] = Id(organisation ?? BindHost.Organisation),
        ["ContactId"] = Id(contact),
    };

    private static List<string> KeysOf(QueryRequest query) =>
        query.Pipeline[0].Match!.Condition!.Value!.Value.EnumerateArray().Select(key => key.GetString()!).ToList();

    [Theory]
    [InlineData("""[{ "resolve": { "path": "customerIds", "as": "c", "elements": "first" } }]""")]
    [InlineData("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""")]
    [InlineData("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "resolve": { "path": "r.companyId", "as": "co" } }]""")]
    public async Task Explain_sends_no_owner_batch_and_runs_no_aggregate(string pipeline)
    {
        var runner = new EntityRunner();
        var client = new FakeRemoteClient();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, BindHost.Options(), client);

        var outcome = await engine.ExplainAsync(BindHost.Request(Invoice, pipeline), BindHost.Context());

        outcome.Should().BeOfType<ExplainOutcome.Success>();
        client.Calls.Should().BeEmpty("explain never executes: no owner batch, keyed-local or remote");
        runner.Calls.Should().BeEmpty("nor an aggregate of this host");
    }

    [Fact]
    public async Task One_cache_never_answers_one_organisation_from_anothers_rows()
    {
        var options = BindHost.Options();
        var cache = new OwnerFetchCache(options);
        var runner = new EntityRunner();
        var client = new FakeRemoteClient { Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactA.ToString(), ("name", "Alice"))) };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: cache);
        var other = Guid.Parse("b0b0b0b0-0000-0000-0000-000000000001");
        const string Pipeline = """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""";

        runner.Rows[Invoice] = [ContactRow(ContactA)];
        await engine.ExecuteAsync(BindHost.Request(Invoice, Pipeline), BindHost.Context());

        runner.Rows[Invoice] = [ContactRow(ContactA, other)];
        await engine.ExecuteAsync(BindHost.Request(Invoice, Pipeline), BindHost.Context(organisation: other));

        client.Calls.Should().HaveCount(2, "the second organisation's key is asked of the owner, not read from the first's answer");

        runner.Rows[Invoice] = [ContactRow(ContactA)];
        await engine.ExecuteAsync(BindHost.Request(Invoice, Pipeline), BindHost.Context());

        client.Calls.Should().HaveCount(2, "the first organisation's own answer is still cached");
    }

    [Fact]
    public async Task Keys_the_budget_cut_are_not_cached_and_cache_hits_cost_no_budget()
    {
        var options = BindHost.Options(configure => configure.Limits.MaxResolveKeys = 1);
        var runner = new EntityRunner();
        var client = new FakeRemoteClient
        {
            Script = (_, query, _) => new FakeRemoteClient.Answer.Rows(KeysOf(query).Select(key => FakeRemoteClient.Row("id", key, ("name", key))).ToArray()),
        };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));
        const string Pipeline = """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""";

        runner.Rows[Invoice] = [ContactRow(ContactA), ContactRow(ContactB)];

        var first = ((QueryOutcome.Success)await engine.ExecuteAsync(BindHost.Request(Invoice, Pipeline), BindHost.Context())).Result;

        first.Diagnostics!.Should().Contain(diagnostic => diagnostic.Code == Codes.ResolvePartial);

        // The first key is cached and free now, so the second takes the one key of the budget.
        var second = ((QueryOutcome.Success)await engine.ExecuteAsync(BindHost.Request(Invoice, Pipeline), BindHost.Context())).Result;

        (second.Diagnostics ?? []).Should().NotContain(diagnostic => diagnostic.Code == Codes.ResolvePartial);
        second.Items.Should().OnlyContain(item => item!["r"] != null);
        client.Calls.Should().HaveCount(2);
        KeysOf(client.Calls[1].Request.Queries.Single()).Should().NotIntersectWith(KeysOf(client.Calls[0].Request.Queries.Single()),
            "the key the budget cut was not cached as missing: it is asked now, and the cached one is not asked again");
    }

    [Fact]
    public async Task A_key_both_targets_of_a_union_hold_is_ambiguous()
    {
        var runner = new EntityRunner();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var line = Guid.Parse("b0000000-0000-0000-0000-000000000001");

        BsonDocument Owner(string display) => new()
        {
            ["_id"] = Id(Guid.NewGuid()), ["OrganizationId"] = Id(BindHost.Organisation), [display] = "x",
            ["oxEl"] = new BsonDocument { ["_id"] = Id(line), ["Code"] = "L", ["Amount"] = new BsonDecimal128(1m) },
        };

        runner.Rows[Invoice] =
        [
            new BsonDocument
            {
                ["_id"] = Id(Guid.NewGuid()), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "RE",
                ["LocalSource"] = new BsonDocument { ["Type"] = "logistics", ["_id"] = Id(line) },
            },
        ];
        runner.Rows["rc.shipment"] = [Owner("Number")];
        runner.Rows["rc.tour"] = [Owner("Name")];

        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "localSource.id", "as": "line" } }]"""), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>().Which.Result.Diagnostics!.Should().Contain(diagnostic => diagnostic.Code == Codes.ResolveAmbiguous);
    }
}
