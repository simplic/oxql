using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Shape;

/// <summary>
/// The row under the alias of an unwind is what the projection names there. A join that reads its
/// reference from the element keeps that reference in storage past the projection; the row does not
/// show it, as it does not show one kept among the row's own members.
/// </summary>
[Trait("Category", "Integration")]
public class UnwoundProjectionTests
{
    public static TheoryData<string, string> Joins() => new()
    {
        { "a reference of the element", """{ "resolve": { "path": "item.billingLineId", "as": "joined" } }""" },
        { "the same, with its missing references reported", """{ "resolve": { "path": "item.billingLineId", "as": "joined", "onMissing": "report" } }""" },
        { "a reference in a collection of the element", """{ "resolve": { "path": "item.references.referenceId", "as": "joined", "elements": "first", "target": "transport.shipment" } }""" },
    };

    [Theory]
    [MemberData(nameof(Joins))]
    public async Task The_element_of_an_unwind_shows_the_members_the_projection_names_and_no_reference_a_join_read(string name, string join)
    {
        var ledger = await Lab.ClientAsync(LabService.Ledger, Org.R);

        foreach (var unwind in new[] { """{ "unwind": { "path": "items", "as": "item" } }""", """{ "unwind": { "path": "items", "flatten": "items", "as": "item" } }""" })
        {
            var answer = await ledger.SendAsync(ReportSeed.Transaction, $$"""
                [ { "match": { "id": { "eq": "{{ReportSeed.TransactionId:D}}" } } }, {{unwind}}, { "match": { "item": { "is": "BillingLineTransactionItem" } } }, {{join}},
                  { "project": { "number": 1, "item.id": 1, "joined.id": 1 } } ]
                """);

            answer.ShouldBeOk(name).Items.Should().NotBeEmpty();

            foreach (var row in answer.Items.OfType<JsonObject>())
                row["item"]!.AsObject().Select(member => member.Key).Should().Equal(["id"], $"{name}: {row.ToJsonString()}");
        }
    }

    [Fact]
    public async Task An_element_no_projection_names_shows_what_it_holds()
    {
        var ledger = await Lab.ClientAsync(LabService.Ledger, Org.R);

        // An inclusion that names the collection, then the unwind: the alias is written after the projection, which says nothing of it.
        var answer = await ledger.SendAsync(ReportSeed.Transaction, $$"""
            [ { "match": { "id": { "eq": "{{ReportSeed.TransactionId:D}}" } } }, { "project": { "number": 1, "items": 1 } }, { "unwind": { "path": "items", "as": "item" } } ]
            """);

        answer.ShouldBeOk().Items.OfType<JsonObject>().Should().OnlyContain(row => row["item"]!.AsObject().ContainsKey("text") && row["item"]!.AsObject().ContainsKey("sortNumber"));
    }
}
