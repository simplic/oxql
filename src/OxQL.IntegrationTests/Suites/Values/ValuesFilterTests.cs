using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Values.ValuesKit;

namespace OxQL.IntegrationTests.Suites.Values;

/// <summary>
/// A list of values and the request's <c>filter</c> (DG2 set filters). Every entry of the filter runs
/// before the <c>values</c> stage, so a list holds the values of the rows the entries leave. The list
/// of a column's own set filter is asked with every entry but that column's: the engine has no member
/// that excludes an entry, the caller leaves it out, and the list then still offers what the column's
/// own choice would hide.
/// </summary>
[Trait("Category", "Integration")]
public class ValuesFilterTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static IReadOnlyList<CorpusRow> Shipments => Corpus.Rows(Corpus.Shipment);

    [Fact]
    public async Task The_list_follows_every_entry_of_the_filter_and_a_set_filter_leaves_its_own_entry_out()
    {
        const string NotDeleted = """{ "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } }""";
        const string Planned = """{ "id": "col:loadStart", "where": { "loadStart": { "gte": "2026-06-01T00:00:00Z" } } }""";
        const string Own = """{ "id": "col:status", "where": { "status.name": { "in": ["Open"] } } }""";

        bool Others(CorpusRow row) => Corpus.ValueAt(row, "isDeleted") is not BsonBoolean { Value: true } && Corpus.Date(row, "loadStart") is { } start && start >= new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var expected = Shipments.Where(Others).GroupBy(row => Corpus.Text(row, "status.name")).ToDictionary(group => group.Key is null ? "null" : $"\"{group.Key}\"", group => group.Count());

        expected.Should().HaveCountGreaterThan(1, "the other filters leave more than the one status the column's own filter picks");

        // The list of the column's set filter: every other entry, not the column's own.
        var list = await Ask(Corpus.Shipment, """ "path": "status.name", "counts": true """, filter: $"[{NotDeleted}, {Planned}]");

        Listed(list).ToDictionary(entry => entry.Value, entry => entry.Count!.Value).Should().BeEquivalentTo(expected);

        // With its own entry the list would hold only what is already chosen: nothing to choose from.
        var narrowed = await Ask(Corpus.Shipment, """ "path": "status.name", "counts": true """, filter: $"[{NotDeleted}, {Planned}, {Own}]");

        Listed(narrowed).Should().Equal(("\"Open\"", (int?)expected["\"Open\""]));
    }

    [Fact]
    public async Task A_condition_on_another_services_member_narrows_the_list_as_it_narrows_the_rows()
    {
        const string Resolve = """{ "resolve": { "path": "department.id", "as": "dep" } }""";
        const string Filter = """[{ "id": "col:dep", "where": { "dep.name": { "eq": "Workshop" } } }]""";
        var rows = await (await Transport()).QueryAsync(JsonNode.Parse($$"""{ "entityType": "{{Corpus.Shipment}}", "filter": {{Filter}}, "pipeline": [ {{Resolve}}, { "project": { "id": 1, "status.name": 1 } }, { "page": { "limit": 500 } } ] }""")!);
        var expected = rows.ShouldBeOk().Items.GroupBy(item => item!["status"]?["name"]?.ToJsonString() ?? "null").ToDictionary(group => group.Key, group => group.Count());

        expected.Should().NotBeEmpty();
        Listed(await Ask(Corpus.Shipment, """ "path": "status.name", "counts": true """, before: Resolve, filter: Filter)).ToDictionary(entry => entry.Value, entry => entry.Count!.Value).Should().BeEquivalentTo(expected);
    }
}
