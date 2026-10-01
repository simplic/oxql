using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// The resolve refusals of contract 2 on a real server (DESIGN §3.4.1, §3.10), over the report fleet's
/// ledger: a resolve through the transaction's items that are not unwound (the silent wrong
/// answer; it is <c>RESOLVE_ON_COLLECTION</c>), a <c>target</c> that is not one of the
/// reference's targets, and a <c>parentAs</c> on an entity target. A later stage that only reads
/// the failed alias adds no error of its own.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsResolveMembersTests
{
    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    [Fact]
    public async Task A_resolve_through_items_that_are_not_unwound_is_RESOLVE_ON_COLLECTION()
    {
        var refused = await (await LedgerClient()).SendAsync(ReportSeed.Transaction, """
            [ { "resolve": { "path": "items.billingLineId", "as": "line" } },
              { "project": { "id": 1, "line.text": 1 } } ]
            """);

        var error = refused.ShouldRefuse("RESOLVE_ON_COLLECTION", 400);

        error["path"]!.GetValue<string>().Should().Be("items.billingLineId");
        error["message"]!.GetValue<string>().Should().Contain("the collection 'items'");
        refused.ErrorCodes.Should().Equal(["RESOLVE_ON_COLLECTION"], "the projection reads only the alias that failed");
    }

    [Fact]
    public async Task A_target_that_is_not_a_target_of_the_reference_is_RESOLVE_TARGET_NOT_DECLARED()
    {
        var refused = await (await LedgerClient()).SendAsync(ReportSeed.BillingLine, """
            [ { "resolve": { "path": "sourceBillingLineReference.id", "as": "source", "target": "staff.employee" } } ]
            """);

        refused.ShouldRefuse("RESOLVE_TARGET_NOT_DECLARED", 400)["message"]!.GetValue<string>()
            .Should().Contain("'transport.shipment', 'transport.tour'");
        refused.ErrorCodes.Should().Equal(["RESOLVE_TARGET_NOT_DECLARED"]);
    }

    [Fact]
    public async Task ParentAs_on_an_entity_target_is_RESOLVE_PARENT_NOT_ITEM()
    {
        var refused = await (await LedgerClient()).SendAsync(ReportSeed.Transaction, """
            [ { "resolve": { "path": "createUserId", "as": "maker", "parentAs": "makerRow" } } ]
            """);

        refused.ShouldRefuse("RESOLVE_PARENT_NOT_ITEM", 400);
        refused.ErrorCodes.Should().Equal(["RESOLVE_PARENT_NOT_ITEM"]);
    }
}
