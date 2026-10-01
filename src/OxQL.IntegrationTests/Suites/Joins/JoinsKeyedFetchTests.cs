using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// The keyed fetch <i>by keys</i> on a real server, over the report fleet (DESIGN
/// §3.5.2, §3.3.3, §3.3.4): a resolve through the invoice's items with <c>elements</c>, answered by
/// the ledger itself (its SelfOwner); the ERP line's typed, converted references answered by the
/// transport owner; and the grouped owner answer (<c>keyedBy</c>) on the owners' own engines as an
/// internal call — a non-unique member with two rows for the shared user id, an item collection's
/// elements with their owning row — which the public route refuses.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsKeyedFetchTests
{
    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    private static string Id(Guid id) => id.ToString("D");

    [Fact]
    public async Task Elements_all_resolves_every_top_level_billing_line_of_the_invoice_through_the_ledgers_own_engine()
    {
        var answer = await (await LedgerClient()).SendAsync(ReportSeed.Transaction, $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.TransactionId)}}" } } },
              { "resolve": { "path": "items.billingLineId", "as": "lines", "elements": "all", "select": ["text"] } },
              { "project": { "id": 1, "lines": 1 } } ]
            """);

        answer.ShouldBeOk();

        var lines = answer.Items.Single()!["lines"]!.AsArray();

        lines.Select(line => line!["id"]!.GetValue<string>()).Should().Equal(Id(ReportSeed.ErpLineIds[0]), Id(ReportSeed.ErpLineIds[5]));
        lines.Select(line => line!["text"]!.GetValue<string>()).Should().Equal("Freight Hamburg - Bremen", "Freight Hamburg - Emden");
    }

    [Fact]
    public async Task Elements_first_takes_the_first_billing_line_item_whose_key_resolves()
    {
        var answer = await (await LedgerClient()).SendAsync(ReportSeed.Transaction, $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.MissingSourceTransactionId)}}" } } },
              { "resolve": { "path": "items.billingLineId", "as": "line", "elements": "first" } } ]
            """);

        answer.ShouldBeOk();
        answer.Items.Single()!["line"]!["id"]!.GetValue<string>().Should().Be(Id(ReportSeed.ErpLineIds[3]));
    }

    [Fact]
    public async Task A_typed_converted_reference_resolves_per_row_by_its_data_type_at_the_transport_owner()
    {
        var answer = await (await LedgerClient()).SendAsync(ReportSeed.BillingLine, $$"""
            [ { "match": { "id": { "in": ["{{Id(ReportSeed.ErpLineIds[0])}}", "{{Id(ReportSeed.ErpLineIds[2])}}", "{{Id(ReportSeed.ErpLineIds[5])}}"] } } },
              { "resolve": { "path": "references.referenceId", "as": "source", "elements": "first" } } ]
            """);

        answer.ShouldBeOk();

        var byText = answer.Items.ToDictionary(item => item!["text"]!.GetValue<string>(), item => item!["source"]);

        byText["Freight Hamburg - Bremen"]!["id"]!.GetValue<string>().Should().Be(Id(ReportSeed.ShipmentId), "the shipment case goes to the shipment");
        byText["Tour flat rate"]!["id"]!.GetValue<string>().Should().Be(Id(ReportSeed.TractorTourId), "the upper-case tour id converts, and the tariff before it selects no case");
        byText["Freight Hamburg - Emden"].Should().BeNull("a line whose references hold only a tariff references nothing");
    }

    [Fact]
    public async Task KeyedBy_on_the_public_route_is_UNKNOWN_REQUEST_MEMBER()
    {
        var refused = await (await Lab.ClientAsync(LabService.Staff, Org.R)).QueryAsync(new
        {
            entityType = ReportSeed.Employee,
            keyedBy = new { path = "userId", keys = new[] { Id(ReportSeed.ClerkUserId) } },
            pipeline = Array.Empty<object>(),
        });

        refused.ShouldRefuse("UNKNOWN_REQUEST_MEMBER", 400);
    }

    // ---- the owner's grouped answer, as an internal call ------------------------------------

    private static async Task<QueryResult> InternalAsync(LabService service, string entity, object keyedBy, string pipeline)
    {
        var host = await (await Lab.ClientAsync(service, Org.R)).HostAsync();
        var engine = host.Services.GetRequiredService<IQueryEngine>();
        var context = new RequestContext { Organisation = Org.R.Id(), Options = host.Services.GetRequiredService<OxQLOptions>(), Internal = true };
        var request = JsonSerializer.Deserialize<QueryRequest>(new JsonObject
        {
            ["entityType"] = entity,
            ["keyedBy"] = JsonSerializer.SerializeToNode(keyedBy),
            ["pipeline"] = JsonNode.Parse(pipeline),
        }.ToJsonString(), OxQLJson.Wire)!;

        var outcome = await engine.ExecuteAsync(request, context);

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? JsonSerializer.Serialize(refused.Refusal, OxQLJson.Wire) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    [Fact]
    public async Task A_non_unique_member_answers_at_most_two_rows_per_key_first_by_record_key()
    {
        var keys = new[] { Id(ReportSeed.DuplicateUserId), Id(ReportSeed.ClerkUserId), Id(ReportSeed.UnknownUserId) };
        var result = await InternalAsync(LabService.Staff, ReportSeed.Employee, new { path = "userId", keys, perKey = 2 },
            """[ { "project": { "id": 1, "userId": 1 } }, { "page": { "limit": 6 } } ]""");

        var byUser = result.Items.GroupBy(item => item!["userId"]!.GetValue<string>()).ToDictionary(group => group.Key, group => group.Select(item => item!["id"]!.GetValue<string>()).ToList());

        byUser[Id(ReportSeed.DuplicateUserId)].Should().BeEquivalentTo(ReportSeed.DuplicateUserEmployeeIds.Select(Id), "two employees share the user id: the caller reads it as ambiguous");
        byUser[Id(ReportSeed.ClerkUserId)].Should().Equal(Id(ReportSeed.ClerkEmployeeId));
        byUser.Should().NotContainKey(Id(ReportSeed.UnknownUserId));

        var one = await InternalAsync(LabService.Staff, ReportSeed.Employee, new { path = "userId", keys, perKey = 1 },
            """[ { "project": { "id": 1, "userId": 1 } }, { "page": { "limit": 3 } } ]""");

        one.Items.Should().HaveCount(2, "one row per key that has any");
        one.Items.Select(item => item!["id"]!.GetValue<string>()).Should().Contain(ReportSeed.DuplicateUserEmployeeIds.Select(Id).Min());
    }

    [Fact]
    public async Task An_item_member_answers_the_matching_element_under_oxEl_beside_its_owning_row()
    {
        var keys = new[] { Id(ReportSeed.ShipmentLineIds[0]), Id(ReportSeed.DuplicateLineId), Id(ReportSeed.DeletedSourceLineId) };
        var result = await InternalAsync(LabService.Transport, ReportSeed.Shipment, new { path = "billingLines.id", keys, perKey = 2 },
            """[ { "project": { "oxEl.id": 1, "oxEl.text": 1, "$default": 1 } }, { "page": { "limit": 6 } } ]""");

        var byLine = result.Items.ToDictionary(item => item!["oxEl"]!["id"]!.GetValue<string>(), item => item!["id"]!.GetValue<string>());

        byLine.Should().HaveCount(2, "the deleted source line is in no shipment");
        byLine[Id(ReportSeed.ShipmentLineIds[0])].Should().Be(Id(ReportSeed.ShipmentId));
        byLine[Id(ReportSeed.DuplicateLineId)].Should().Be(Id(ReportSeed.ShipmentWithDuplicateLineId));
        result.Items.Single(item => item!["oxEl"]!["id"]!.GetValue<string>() == Id(ReportSeed.ShipmentLineIds[0]))!["oxEl"]!["text"]!.GetValue<string>()
            .Should().Be("Freight Hamburg - Bremen");
    }
}
