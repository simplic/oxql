using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// A condition on the alias of a remote resolve itself. The alias is the owner's row, not a
/// path of the owner, so no semi-join can express it: the binder refuses it with
/// <c>RESOLVE_NOT_FILTERABLE</c> at the match stage, and the owner is never called. A condition on
/// a member of the alias stays a semi-join.
/// </summary>
public class RemoteAliasFilterTests
{
    private const string Order = "probe.order";

    private const string Resolve = """{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"] } }""";

    public static TheoryData<string> OnTheAlias => new()
    {
        """{ "contact": { "eq": null } }""",
        """{ "contact": { "neq": null } }""",
        """{ "contact": { "exists": true } }""",
        """{ "contact": { "exists": false } }""",
        """{ "contact": { "in": ["x"] } }""",
        """{ "contact": "x" }""",
        """{ "or": [{ "number": { "eq": "a" } }, { "contact": { "eq": null } }] }""",
        """{ "not": { "contact": { "exists": true } } }""",
    };

    [Theory]
    [MemberData(nameof(OnTheAlias))]
    public async Task A_condition_on_the_remote_alias_itself_is_refused_at_bind_time(string match)
    {
        var refusal = await BindHost.RefusedAsync(BindHost.Probe, Order, $$"""[{{Resolve}}, { "match": {{match}} }]""");

        refusal.Status.Should().Be(400);
        refusal.Errors!.Select(error => error.Code).Should().Equal([Codes.ResolveNotFilterable], BindHost.Describe(refusal));
        refusal.Errors![0].Path.Should().Be("contact");
        refusal.Errors![0].Stage.Should().Be(1);
        refusal.Errors![0].Message.Should().Contain("'contact'").And.Contain("members");
    }

    [Fact]
    public async Task Contract_1_refuses_the_same_condition_with_the_same_code()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, $$"""[{{Resolve}}, { "match": { "contact": { "eq": null } } }]""",
            Codes.ResolveNotFilterable, BindHost.Context(contract: 1));

        error.Path.Should().Be("contact");
    }

    [Theory]
    [InlineData("""{ "contact.name": { "eq": "x" } }""")]
    [InlineData("""{ "contact.name": { "exists": true } }""")]
    [InlineData("""{ "contact.address.city": { "eq": null } }""")]
    public async Task A_condition_on_a_member_of_the_alias_stays_a_semi_join(string match)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{{Resolve}}, { "match": {{match}} }]""");

        ((BoundCondition.Leaf)((BoundStage.Match)bound.Stages[1]).Condition).IsSemiJoin.Should().BeTrue();
        bound.HasSemiJoin.Should().BeTrue();
    }

    [Fact]
    public async Task A_local_resolve_alias_keeps_its_own_filterability_rule()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order,
            """[{ "resolve": { "path": "customerId", "as": "cust" } }, { "match": { "cust": { "eq": null } } }]""", Codes.NotFilterable);

        error.Path.Should().Be("cust");
    }

    [Fact]
    public async Task The_engine_answers_the_refusal_without_calling_the_owner()
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        var outcome = await engine.ExecuteAsync(
            BindHost.Request(Order, $$"""[{{Resolve}}, { "match": { "contact": { "eq": null } } }, { "page": { "limit": 5 } }]"""), BindHost.Context());

        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Status.Should().Be(400);
        refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveNotFilterable);
        client.Calls.Should().BeEmpty("a condition no owner can answer is refused before any call");
    }
}
