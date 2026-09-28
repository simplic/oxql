using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Chain;

/// <summary>
/// <c>OWNER_NOT_CAPABLE</c> under strict (DESIGN §3.5.4): a continued stage for an owner whose
/// shallow health reports OxQL 2.0 is refused before anything is sent, as it is outside strict
/// (<c>Suites.Joins.JoinsContinuationTests</c>). A fleet of its own, since what a host learned of
/// the owner's engine stays with it.
/// </summary>
[Trait("Category", "Integration")]
public class ChainOwnerCapabilityTests
{
    [Fact]
    public async Task A_strict_chain_onto_an_owner_on_OxQL_2_0_is_OWNER_NOT_CAPABLE_and_no_batch_is_sent()
    {
        await using var fleet = LabFleet.Create("e14b_owner_capable");
        var owner = new ScriptedOwner { Version = "2.0.126.924" };
        fleet.Mount(LabFleet.ExternalOwner, owner);
        await CorpusSeeder.SeedAsync(fleet, [LabService.Conformance]);

        var host = await fleet.HostAsync(LabService.Conformance);
        await ScriptedOwnerFleet.LearnOwnerAsync(host);

        var refused = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("cap", strict: true, after: """{ "resolve": { "path": "w.code", "as": "again" } },"""));

        var error = refused.ShouldRefuse("OWNER_NOT_CAPABLE", 422);
        error["stage"]!.GetValue<int>().Should().Be(1);
        error["params"]!.ToJsonString().Should().Be("""{"service":"owner","version":"2.0.126.924","needs":"2.1"}""");
        refused.ErrorCodes.Should().Equal(["OWNER_NOT_CAPABLE"]);
        owner.Batches.Should().BeEmpty();

        // The plain resolve needs nothing of 2.1 and runs on the same owner.
        var plain = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("cap-plain", strict: true));

        plain.ShouldBeOk().ShouldHaveNoDiagnostics();
        plain.Items.Select(item => (item!["w"] as JsonObject)?["name"]?.GetValue<string>()).Should().Equal("Widget One", "Widget Two", "Widget Three");
    }
}
