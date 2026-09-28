using System.Text.Json;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind.Fixtures.Resolve;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// A join fetches only its select (PRE-1, RE-9): a path under its alias beyond the select has no
/// value, so matching, sorting, grouping or projecting it is refused <c>UNKNOWN_PATH</c> instead of
/// binding and never matching; a lookup <c>on</c> an alias that did not fetch the member it joins on
/// is refused the same way; describe offers what the select fetched.
/// </summary>
public class JoinSelectShapeTests
{
    private const string Invoice = ResolveModel.Invoice;

    [Theory]
    [InlineData("""{ "match": { "c.code": { "eq": "x" } } }""")]
    [InlineData("""{ "sort": [{ "c.code": "asc" }] }""")]
    [InlineData("""{ "project": { "c.code": 1 } }""")]
    [InlineData("""{ "group": { "by": [{ "path": "c.code", "as": "code" }], "fields": { "n": { "count": true } } } }""")]
    public async Task A_path_under_an_inline_resolve_beyond_its_select_is_UNKNOWN_PATH(string stage)
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, $$"""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }, {{stage}}]""", Codes.UnknownPath);

        error.Stage.Should().Be(1);
        error.Message.Should().Contain("not in the select of 'c'").And.Contain("'name'");
    }

    [Fact]
    public async Task A_selected_path_or_the_key_binds()
    {
        await BindHost.BoundAsync(ResolveModel.Model, Invoice, """
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "match": { "c.name": { "eq": "x" } } },
             { "sort": [{ "c.id": "asc" }] }]
            """);
    }

    [Fact]
    public async Task A_first_lookups_row_sorts_only_on_what_it_fetched()
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, "rc.customer", """
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "latest", "first": true, "select": ["number"] } },
             { "sort": [{ "latest.customerCode": "asc" }] }]
            """, Codes.UnknownPath);

        error.Message.Should().Contain("not in the select of 'latest'");
    }

    [Fact]
    public async Task A_lookup_on_an_alias_that_did_not_fetch_the_member_it_joins_on_is_refused_and_binds_once_selected()
    {
        // rc.invoice#customerCode references rc.customer by its code, which is not the key.
        var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, """
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "lookup": { "from": "rc.invoice", "path": "customerCode", "on": "c", "as": "sameCode" } }]
            """, Codes.UnknownPath);

        error.Stage.Should().Be(1);
        error.Message.Should().Contain("add 'code' to that select");

        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, """
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name", "code"] } },
             { "lookup": { "from": "rc.invoice", "path": "customerCode", "on": "c", "as": "sameCode" } }]
            """);

        bound.Stages.OfType<BoundStage.Lookup>().Single().ParentKeyStorage.Should().Be("c.Code");
    }

    [Fact]
    public async Task Describe_offers_under_a_join_alias_only_what_its_select_fetched()
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options());
        var request = JsonSerializer.Deserialize<ExplainRequest>($$"""
            { "query": { "entityType": "{{Invoice}}", "pipeline": [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }] },
              "describe": [{ "id": "c", "at": 1, "prefix": "c", "usage": "match" }] }
            """, OxQLJson.Wire)!;

        var result = (await engine.ExplainAsync(request, BindHost.Context())).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        var children = result.Describe.Single()!["children"]!.AsArray().Select(child => child!["path"]!.GetValue<string>()).ToList();

        children.Should().BeEquivalentTo(["c.id", "c.name"]);
    }
}
