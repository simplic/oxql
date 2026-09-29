using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The binder's small defects of the engine review (RE-23): a union's different filter errors are both
/// reported, <c>"flatten": null</c> is the member left out, and a contract 1 resolve through a
/// collection is told why <c>elements</c> is not there.
/// </summary>
public class BinderSmallDefectsTests
{
    private const string Invoice = ResolveModel.Invoice;

    [Fact]
    public async Task A_union_filter_failing_differently_on_two_targets_reports_both()
    {
        var refusal = await BindHost.RefusedAsync(ResolveModel.Model, Invoice,
            """[{ "resolve": { "path": "localSource.id", "as": "s", "filter": { "amount": { "eq": "x" } } } }]""");

        refusal.Errors!.Select(error => error.Code).Should().Contain([Codes.UnknownPath]).And.HaveCountGreaterThan(1,
            "the billing lines refuse the operand, the customer has no amount: two mistakes, not one");
    }

    [Fact]
    public async Task Flatten_null_is_the_member_left_out()
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, """[{ "unwind": { "path": "lines", "flatten": null } }]""");

        bound.Stages.OfType<BoundStage.Unwind>().Single().Flatten.Should().BeNull();
    }

    [Fact]
    public async Task A_contract_1_resolve_through_a_collection_carries_the_contract_1_hint()
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, """[{ "resolve": { "path": "customerIds", "as": "c" } }]""", Codes.ResolveOnCollection, BindHost.Context(contract: 1));

        error.Message.Should().EndWith(Binder.Contract1Hint);
    }
}
