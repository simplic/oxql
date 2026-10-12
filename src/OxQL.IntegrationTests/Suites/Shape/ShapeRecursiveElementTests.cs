using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Shape;

/// <summary>
/// The members of an element whose type holds its own kind (a transaction item's items, an
/// operation item's assigned items): after such a collection is unwound, and through it without an
/// unwind, a member below it is a path like any other. The model's path index stops where a type
/// meets itself on its branch; the paths below are described from where the index describes the
/// type. Over the acceptance invoice of organisation R: item 3 is a group holding a billing line (4)
/// and a group (5), which holds a billing line (6).
/// </summary>
[Trait("Category", "Integration")]
public class ShapeRecursiveElementTests
{
    private static async Task<WireAnswer> Send(string stages) =>
        await (await Lab.ClientAsync(LabService.Ledger, Org.R)).SendAsync(ReportSeed.Transaction,
            $$"""[ { "match": { "id": { "eq": "{{ReportSeed.TransactionId.Wire()}}" } } }, {{stages}} ]""");

    private static string? Text(JsonNode? row, string path) => Json.At(row, path) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [Fact]
    public async Task R01_the_members_of_an_unwound_element_of_its_parents_own_kind_are_paths()
    {
        var answer = await Send("""
            { "unwind": { "path": "items", "as": "item" } },
            { "unwind": { "path": "item.items", "as": "sub" } },
            { "project": { "item.sortNumber": 1, "sub.text": 1, "sub.sortNumber": 1, "sub.id": 1 } },
            { "sort": [ { "sub.sortNumber": "asc" } ] }
            """);

        answer.ShouldBeOk();
        answer.Items.Select(row => Text(row, "sub.text")).Should().Equal("Billing line 4", "Extras");
        answer.Items.Select(row => row!["sub"]!["sortNumber"]!.GetValue<int>()).Should().Equal(4, 5);
        answer.Items.Should().OnlyContain(row => row!["item"]!["sortNumber"]!.GetValue<int>() == 3);
        answer.Items.Should().OnlyContain(row => Guid.Parse(Text(row, "sub.id")!) != Guid.Empty);
    }

    [Fact]
    public async Task R02_a_condition_on_a_member_of_such_an_element_filters_the_rows()
    {
        var answer = await Send("""
            { "unwind": { "path": "items", "as": "item" } },
            { "unwind": { "path": "item.items", "as": "sub" } },
            { "match": { "sub.text": { "eq": "Extras" } } },
            { "project": { "sub.text": 1, "sub.deserializationType": 1 } }
            """);

        answer.ShouldBeOk();
        answer.Items.Should().ContainSingle();
        Text(answer.Items[0], "sub.deserializationType").Should().Be("group");
    }

    [Fact]
    public async Task R03_the_element_unwinds_again_and_its_members_are_paths_at_every_level()
    {
        var answer = await Send("""
            { "unwind": { "path": "items", "as": "item" } },
            { "unwind": { "path": "item.items", "as": "sub" } },
            { "unwind": { "path": "sub.items", "as": "deep" } },
            { "project": { "sub.text": 1, "deep.text": 1, "deep.totalPrice": 1, "deep.references.dataType": 1 } }
            """);

        answer.ShouldBeOk();
        answer.Items.Should().ContainSingle();
        Text(answer.Items[0], "sub.text").Should().Be("Extras");
        Text(answer.Items[0], "deep.text").Should().Be("Billing line 6");
        answer.Items[0]!["deep"]!["totalPrice"]!.ToJsonString().Should().Be("120", "a decimal is a number in canonical digits under contract 2");
        answer.Items[0]!["deep"]!["references"]!.AsArray().Select(reference => Text(reference, "dataType")).Should().Equal("shipment");
    }

    [Fact]
    public async Task R04_a_member_below_the_collection_is_a_path_without_an_unwind_too()
    {
        var answer = await Send("""
            { "match": { "items.items.text": { "eq": "Extras" } } },
            { "project": { "number": 1, "items.items.text": 1 } }
            """);

        answer.ShouldBeOk();
        answer.Items.Should().ContainSingle();

        var nested = answer.Items[0]!["items"]!.AsArray().SelectMany(item => (item?["items"] as JsonArray ?? []).Select(sub => Text(sub, "text"))).ToList();

        nested.Should().Equal("Billing line 4", "Extras");
    }

    [Fact]
    public async Task R05_a_name_the_type_does_not_have_is_still_not_a_path()
    {
        var answer = await Send("""
            { "unwind": { "path": "items", "as": "item" } },
            { "unwind": { "path": "item.items", "as": "sub" } },
            { "project": { "sub.nope": 1 } }
            """);

        answer.ShouldRefuse("UNKNOWN_PATH", 400)["path"]!.GetValue<string>().Should().Be("sub.nope");
    }

    [Fact]
    public async Task R06_explain_lists_the_members_of_such_an_element_as_columns()
    {
        var ledger = await Lab.ClientAsync(LabService.Ledger, Org.R);
        var answer = await ledger.ExplainAsync(Json.Request(ReportSeed.Transaction, """
            [ { "unwind": { "path": "items", "as": "item" } },
              { "unwind": { "path": "item.assignedTransactionItems", "as": "assigned" } },
              { "project": { "assigned.text": 1, "assigned.sortNumber": 1 } } ]
            """));

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Body!["valid"]!.GetValue<bool>().Should().BeTrue(answer.ToString());
    }
}
