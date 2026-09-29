using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// A reference with cases under a join alias: the member its cases test (the sibling <c>type</c>)
/// must be in the join's select. When it is not, the reference is still declared, so the error is
/// the join's <c>UNKNOWN_PATH</c> "not in the select of", naming the member to add, and not
/// <c>RESOLVE_NOT_DECLARED</c> (which says the path references nothing).
/// </summary>
public class ReferenceCaseSelectTests
{
    private const string Customer = "rc.customer";

    private static EntityModel Model => ResolveModel.Model;

    private static string Pipeline(string select) => $$"""
        [
          { "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoice", "first": true, "select": {{select}} } },
          { "resolve": { "path": "invoice.localSource.id", "as": "source", "target": "rc.customer" } }
        ]
        """;

    [Fact]
    public async Task A_case_member_the_join_did_not_fetch_is_a_path_not_in_its_select()
    {
        var error = await BindHost.ErrorAsync(Model, Customer, Pipeline("""["id", "localSource.id"]"""), Codes.UnknownPath);

        error.Message.Should().Be("The reference on 'invoice.localSource.id' picks its target by 'invoice.localSource.type', which is not in the select of 'invoice', which fetched 'id', 'localSource.id'; add 'localSource.type' to that select.");
        error.Path.Should().Be("invoice.localSource.type");
        error.Stage.Should().Be(1);
    }

    [Fact]
    public async Task It_is_not_reported_as_an_undeclared_reference()
    {
        var refused = await BindHost.RefusedAsync(Model, Customer, Pipeline("""["id", "localSource.id"]"""));

        refused.Errors!.Select(error => error.Code).Should().NotContain(Codes.ResolveNotDeclared);
    }

    [Theory]
    [InlineData("""["id", "localSource.id", "localSource.type"]""")]
    [InlineData("""["id", "localSource"]""")]
    public async Task With_the_case_member_selected_the_resolve_binds(string select)
    {
        var bound = await BindHost.BoundAsync(Model, Customer, Pipeline(select));

        bound.Stages.OfType<BoundStage.Resolve>().Should().ContainSingle();
    }
}
