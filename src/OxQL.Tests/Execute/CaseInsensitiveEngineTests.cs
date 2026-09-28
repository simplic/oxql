using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The collation reaches both aggregates, the explain and the advisory, and a semi-join
/// forwards the caller's choice; a request over other kinds runs exactly as before.
/// </summary>
public class CaseInsensitiveEngineTests
{
    private const string Order = "probe.order";
    private static readonly Guid Vehicle1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly BsonDocument DefaultCollation = BsonDocument.Parse("{ locale: 'de', strength: 1 }");

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, FakeRemoteClient Client) Host(Action<OxQLOptions>? configure = null)
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient { Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", Vehicle1.ToString())) };
        var options = BindHost.Options(configure);

        return (new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options)), runner, client);
    }

    private static async Task<QueryResult> Success(MongoQueryEngine engine, string pipeline)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    [Fact]
    public async Task The_page_and_the_count_run_under_the_collation_when_a_string_folds()
    {
        var (engine, runner, _) = Host();

        await Success(engine, """[{ "match": { "number": { "eq": "x" } } }, { "page": { "includeTotalCount": true } }]""");

        runner.Calls.Should().HaveCount(2);
        runner.Calls.Should().OnlyContain(call => call.Options == new AggregateRunOptions(10_000, true, DefaultCollation));
    }

    [Fact]
    public async Task A_request_over_other_kinds_runs_with_the_options_it_always_had()
    {
        var (engine, runner, _) = Host();

        await Success(engine, """[{ "match": { "count": { "eq": 1 } } }, { "sort": [{ "when": "desc" }] }, { "page": { "includeTotalCount": true } }]""");

        runner.Calls.Should().HaveCount(2);
        runner.Calls.Should().OnlyContain(call => call.Options == new AggregateRunOptions(10_000, true));
    }

    [Fact]
    public async Task The_configured_collation_reaches_the_runner()
    {
        var (engine, runner, _) = Host(options => options.Representation.Collation = new CollationOptions { Locale = "en", Strength = 2 });

        await Success(engine, """[{ "sort": [{ "number": "asc" }] }]""");

        runner.Calls.Single().Options.Collation!.ShouldBeBson(BsonDocument.Parse("{ locale: 'en', strength: 2 }"));
    }

    [Fact]
    public async Task Explain_shows_the_collation_and_the_advisory_names_it()
    {
        var (engine, _, _) = Host();
        var folded = await engine.ExplainAsync(BindHost.Request(Order, """[{ "match": { "number": { "eq": "x" } } }]"""), BindHost.Context());

        folded.Should().BeOfType<ExplainOutcome.Success>().Which.Result.Collation!.ToJsonString().Should().Be("""{"locale":"de","strength":1}""");

        var plain = await engine.ExplainAsync(BindHost.Request(Order, """[{ "match": { "count": { "eq": 1 } } }]"""), BindHost.Context());

        plain.Should().BeOfType<ExplainOutcome.Success>().Which.Result.Collation.Should().BeNull();

        var stages = new[] { new BsonDocument("$match", new BsonDocument("Number", "x")) };
        var advised = IndexAdvisor.Advise(stages, [], null, DefaultCollation);

        advised.Should().ContainSingle(line => line["field"]!.GetValue<string>() == "collation")
            .Which["note"]!.GetValue<string>().Should().Contain("de/1").And.Contain("same collation");
        IndexAdvisor.Advise(stages, [], null).Should().NotContain(line => line["field"]!.GetValue<string>() == "collation");
    }

    [Fact]
    public async Task A_semi_join_forwards_the_callers_choice_and_leaves_the_default_to_the_owner()
    {
        var (engine, _, client) = Host();
        const string Resolve = """{ "resolve": { "path": "vehicleId", "as": "veh" } }""";

        await Success(engine, $$"""[{{Resolve}}, { "match": { "veh.matchCode": { "eq": "V-1" } } }]""");
        await Success(engine, $$"""[{{Resolve}}, { "match": { "veh.matchCode": { "eq": "V-1", "options": { "caseSensitive": true } } } }]""");
        await Success(engine, $$"""[{{Resolve}}, { "match": { "veh.matchCode": { "eq": "V-1", "options": { "ignoreCase": true } } } }]""");

        var sent = client.Calls.Select(call => call.Request.Queries.Single().Pipeline[0].Match!.Condition!.Options).ToList();

        sent.Should().HaveCount(3);
        sent[0].Should().BeNull("nothing was written, so the owner applies its own default");
        sent[1].Should().BeEquivalentTo(new FilterConditionOptions { CaseSensitive = true });
        sent[2].Should().BeEquivalentTo(new FilterConditionOptions { IgnoreCase = true });

        // The alias asks the owner the same question, which the semi-join cache already holds.
        await Success(engine, $$"""[{{Resolve}}, { "match": { "veh.matchCode": { "eq": "V-1", "options": { "ignoreCase": false } } } }]""");

        client.Calls.Should().HaveCount(3, "ignoreCase: false is caseSensitive: true, and that owner query is cached");
    }
}
