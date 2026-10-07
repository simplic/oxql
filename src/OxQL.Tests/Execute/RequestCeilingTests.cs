using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The request's own time ceiling, <c>maxTimeMs</c> (DG2): under the host's
/// (<c>Execution:MaxTimeMs</c>) and under what a batch leaves the request, it bounds the page and
/// the count step. A grid's totals request carries one, so a total that is slow ends early instead
/// of holding a connection for the host's ceiling. It decides no row and is no part of a cursor.
/// </summary>
public class RequestCeilingTests
{
    private const string Order = "probe.order";

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner) Host(Action<OxQLOptions>? configure = null)
    {
        var runner = new FakeAggregateRunner();
        var options = BindHost.Options(configure);

        return (new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, new FakeRemoteClient(), cache: new OwnerFetchCache(options)), runner);
    }

    private static QueryRequest Request(string maxTimeMs, string pipeline = """[{ "page": { "limit": 5, "count": true } }]""") =>
        BindHost.Parse($$"""{ "entityType": "{{Order}}", "maxTimeMs": {{maxTimeMs}}, "pipeline": {{pipeline}} }""");

    private static async Task<List<int>> Ceilings(QueryRequest request, RequestContext? context = null, Action<OxQLOptions>? configure = null)
    {
        var (engine, runner) = Host(configure);
        var outcome = await engine.ExecuteAsync(request, context ?? BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return runner.Calls.Select(call => call.Options.MaxTimeMs).ToList();
    }

    [Fact]
    public async Task It_lowers_the_ceiling_of_the_page_and_bounds_the_count_step()
    {
        (await Ceilings(Request("3000"))).Should().Equal([3_000, 500], "the page under the request's ceiling, the count under its budget, which is lower");
        (await Ceilings(Request("200"))).Should().Equal([200, 200], "a count step never outlasts the request");
        (await Ceilings(BindHost.Request(Order, """[{ "page": { "limit": 5, "count": true } }]"""))).Should().Equal([10_000, 500], "without one the host's ceiling holds");
    }

    [Fact]
    public async Task It_never_raises_the_hosts_ceiling_and_a_value_below_one_is_one()
    {
        (await Ceilings(Request("600000"))).Should().Equal([10_000, 500]);
        (await Ceilings(Request("5000"), configure: options => options.Execution.MaxTimeMs = 2_000)).Should().Equal([2_000, 500]);
        (await Ceilings(Request("0"))).Should().Equal([1, 1]);
        (await Ceilings(Request("-5"))).Should().Equal([1, 1]);
    }

    [Fact]
    public async Task Inside_a_batch_the_lower_of_the_requests_and_what_the_batch_leaves_it_holds()
    {
        (await Ceilings(Request("3000"), BindHost.Context() with { MaxTimeMs = 2_000 })).Should().Equal([2_000, 500]);
        (await Ceilings(Request("300"), BindHost.Context() with { MaxTimeMs = 2_000 })).Should().Equal([300, 300]);
    }

    [Fact]
    public async Task A_group_over_no_key_under_a_ceiling_is_the_totals_request_of_a_grid()
    {
        var ceilings = await Ceilings(Request("3000", """[{ "match": { "count": { "gt": 0 } } }, { "group": { "by": [], "fields": { "count": { "count": true }, "sum": { "sum": "amount" } } } }]"""));

        ceilings.Should().Equal([3_000], "one aggregate, under the request's ceiling");
    }

    [Fact]
    public async Task It_is_a_member_of_a_contract_2_request_and_decides_neither_a_row_nor_a_cursor()
    {
        var with = await BindHost.BindAsync(BindHost.Probe, Request("250", """[{ "sort": [{ "number": "asc" }] }]"""));
        var without = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": "asc" }] }]""");
        var bound = with.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(with)).Subject.Pipeline;

        bound.Fingerprint.Should().Be(without.Fingerprint, "a cursor continues under any ceiling");
        bound.Canonical.Should().Be(without.Canonical);
        Binder.RequestMembers.Should().Be("entityType, variables, filter, pipeline, strict, timing, maxTimeMs");
    }

    [Fact]
    public async Task Contract_1_never_had_the_member_and_runs_under_the_hosts_ceiling()
    {
        (await Ceilings(Request("200", """[{ "page": { "limit": 5 } }]"""), BindHost.Context(contract: 1))).Should().Equal([10_000]);
    }
}
