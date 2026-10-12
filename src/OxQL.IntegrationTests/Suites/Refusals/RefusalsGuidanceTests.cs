using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Refusals;

/// <summary>
/// A refusal that says what to do instead: a join over a member that declares no reference names
/// the members that do, and where a missing one is declared; a path that ends in the stored type
/// discriminator says that it is no member and what tells the variants apart.
/// </summary>
[Trait("Category", "Integration")]
public class RefusalsGuidanceTests
{
    private static async Task<WireAnswer> Send(string stages, int? contract = 2) =>
        await (await Lab.ClientAsync(LabService.Ledger, Org.R, contract)).SendAsync(ReportSeed.Transaction, $"[ {stages} ]");

    private static IReadOnlyList<(string Path, IReadOnlyList<string> Targets)> JoinableOf(JsonObject error) =>
        error["params"]!["joinable"]!.AsArray().Select(member => (member!["path"]!.GetValue<string>(), (IReadOnlyList<string>)member["targets"]!.AsArray().Select(target => target!.GetValue<string>()).ToList())).ToList();

    [Fact]
    public async Task G01_a_resolve_over_a_member_without_a_reference_names_the_members_that_declare_one()
    {
        var error = (await Send("""{ "resolve": { "path": "number", "as": "x" } }""")).ShouldRefuse("RESOLVE_NOT_DECLARED", 400);
        var message = error["message"]!.GetValue<string>();

        message.Should().StartWith("'number' declares no reference; it is a string. The members of 'ledger.transaction' that declare a reference are: ");
        message.Should().Contain("createUserId (staff.employee)").And.Contain("items.billingLineId (ledger.billing_line)");
        message.Should().EndWith("A join follows a reference the service's model declares on the member; one that is missing is declared there.");

        error["params"]!["entity"]!.GetValue<string>().Should().Be("ledger.transaction");

        var joinable = JoinableOf(error);

        joinable.Should().Contain(member => member.Path == "createUserId" && member.Targets.SequenceEqual(new[] { "staff.employee" }));
        joinable.Should().Contain(member => member.Path == "items.references.referenceId" && member.Targets.Contains("transport.shipment") && member.Targets.Contains("transport.tour"));
        error["params"]!["joinableCount"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(joinable.Count);
    }

    [Fact]
    public async Task G02_a_lookup_over_a_member_without_the_reference_names_the_members_that_point_at_the_parent()
    {
        var error = (await Send("""{ "lookup": { "from": "ledger.billing_line", "path": "createUserId", "as": "lines" } }""")).ShouldRefuse("LOOKUP_NOT_DECLARED", 400);
        var message = error["message"]!.GetValue<string>();

        message.Should().StartWith("'ledger.billing_line#createUserId' does not declare a reference to 'ledger.transaction'. The members of 'ledger.billing_line' that declare a reference to 'ledger.transaction' are: assignedTransactionId.");
        message.Should().EndWith("one that is missing is declared there.");
        JoinableOf(error).Select(member => member.Path).Should().Equal("assignedTransactionId");

        // And where no member points at the parent at all, it says so.
        var none = (await (await Lab.ClientAsync(LabService.Ledger, Org.R)).SendAsync(ReportSeed.BillingLine,
            """[ { "lookup": { "from": "ledger.billing_line", "path": "text", "as": "same" } } ]""")).ShouldRefuse("LOOKUP_NOT_DECLARED", 400);

        none["message"]!.GetValue<string>().Should().Contain("No member of 'ledger.billing_line' declares a reference to 'ledger.billing_line'.");
        JoinableOf(none).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "project": { "items._t": 1 } }""", "items._t")]
    [InlineData("""{ "project": { "_t": 1 } }""", "_t")]
    [InlineData("""{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item._t": { "eq": "TextTransactionItem" } } }""", "item._t")]
    public async Task G03_the_stored_type_discriminator_is_said_to_be_no_member_and_what_tells_the_variants_apart(string stages, string path)
    {
        var error = (await Send(stages)).ShouldRefuse("UNKNOWN_PATH", 400);

        error["path"]!.GetValue<string>().Should().Be(path);
        error["params"]!["reason"]!.GetValue<string>().Should().Be("notAMember");
        error["params"]!["discriminator"]!.GetValue<string>().Should().Be("_t");
        error["message"]!.GetValue<string>().Should().Contain("'_t' is the stored type discriminator, which is not a member under contract 2").And.Contain("'is'");
    }

    [Fact]
    public async Task G04_a_name_that_is_no_discriminator_keeps_the_plain_refusal()
    {
        var error = (await Send("""{ "project": { "items.nope": 1 } }""")).ShouldRefuse("UNKNOWN_PATH", 400);

        error["message"]!.GetValue<string>().Should().Be("'items.nope' is not a path of ledger.transaction.");
        error["params"]!.AsObject().ContainsKey("discriminator").Should().BeFalse();
    }
}
