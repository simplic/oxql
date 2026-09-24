using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Shipment;

/// <summary>
/// The worked example every suite copies. The rule: a case states its expected answer before it
/// runs, computed from the corpus (<see cref="Corpus"/>), and asserts a row set: ids, order,
/// count. "The server answered 200" is not a case.
/// <list type="number">
/// <item>Compute the expectation from the oracle: <c>Corpus.Where</c>, <c>Corpus.SortedIds</c>,
/// <c>Corpus.PageOf</c>. Guard it: an expectation that is empty, or that is the whole entity,
/// proves nothing, so say so with an assertion.</item>
/// <item>Send the hand-written wire JSON through a <see cref="LabClient"/> of the shared fleet.</item>
/// <item>Assert with the answer's verbs: <c>ShouldHaveIds</c>, <c>ShouldHaveTotal</c>,
/// <c>ShouldRefuse("CODE")</c>. A failure prints the request and the whole answer.</item>
/// </list>
/// Two habits the corpus enforces: order by <c>id</c> (the key, never null) when the order is not
/// the subject, because the corpus carries null, empty and duplicate sort keys on purpose; and
/// never assume a nullable member is present.
/// <para>
/// Ported from the legacy <c>shipment</c> battery; the method name starts with the case id
/// (<c>SH01</c> …), which the case ledger maps back.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ShipmentTests
{
    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    [Fact]
    public async Task SH01_an_eq_on_a_nested_string_returns_exactly_the_rows_the_oracle_picks()
    {
        var expected = Corpus.IdsOf(Corpus.ShipmentsWithStatus("Planned"));
        expected.Should().NotBeEmpty("the corpus holds Planned shipments");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "eq": "Planned" } } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 500, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count, capped: false);
        answer.Strings("status.name").Should().AllBe("Planned", "each row carries the value it was filtered on");
    }

    [Fact]
    public async Task SH02_an_in_list_with_a_descending_sort_and_a_limit_returns_exactly_the_top_slice()
    {
        var wanted = new[] { "Planned", "Closed" };
        var matching = Corpus.SortedIds(Corpus.Shipment, "id", descending: true, filter: row => wanted.Contains(Corpus.Text(row, "status.name")));
        var expected = Corpus.PageOf(matching, limit: 5);
        matching.Count.Should().BeGreaterThan(5, "the slice must be a strict prefix for hasNextPage to mean anything");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "in": ["Planned", "Closed"] } } },
              { "sort": [ { "id": "desc" } ] },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldHaveIds(expected);
        answer.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public async Task SH03_a_count_answers_the_exact_number_of_matches_and_a_null_or_empty_value_matches_nothing()
    {
        // startsWith folds case and accents under the default collation; the prefix here is ASCII,
        // and a null, missing or empty shipmentNumber matches nothing.
        var expected = Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "shipmentNumber") is { } number && Order.FoldCi(number).StartsWith(Order.FoldCi("S-00"), StringComparison.Ordinal)).Count;
        expected.Should().BeGreaterThan(0);
        expected.Should().BeLessThan(Corpus.Counts(Corpus.Shipment).A, "the corpus holds rows this prefix does not match");

        var answer = await (await Transport()).SendAsync(Corpus.Shipment, """
            [ { "match": { "shipmentNumber": { "startsWith": "S-00" } } },
              { "project": { "id": 1 } },
              { "page": { "limit": 1, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveTotal(expected, capped: false);
    }

    [Fact]
    public async Task SH04_a_cursor_walk_returns_every_row_exactly_once_in_sort_order()
    {
        var expected = Corpus.AllIds(Corpus.Shipment);

        // A page size well below the entity forces the walk, not the page size, to do the work.
        var walk = await (await Transport()).WalkAsync(Corpus.Shipment, """[ { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1 } } ]""", limit: 7);

        walk.Ids.Should().Equal(expected);
        walk.Ids.Should().OnlyHaveUniqueItems();
        walk.Pages.Should().Be((expected.Count + 6) / 7);
    }

    [Fact]
    public async Task SH05_an_enum_travels_as_its_stored_number_an_unnamed_value_as_itself_and_an_absent_one_not_at_all()
    {
        var rows = Corpus.Rows(Corpus.Shipment);
        var declared = rows.Where(row => Corpus.ValueAt(row, "loadingTimeType") is BsonInt32 { Value: >= 0 and <= 2 }).ToList();
        var undeclared = rows.Where(row => Corpus.ValueAt(row, "loadingTimeType") is BsonInt32 { Value: > 2 }).ToList();
        var absent = Corpus.RowsMissing(Corpus.Shipment, "loadingTimeType");
        declared.Should().NotBeEmpty();
        undeclared.Should().ContainSingle();
        absent.Should().ContainSingle();

        var wire = (await (await Transport()).PullAsync(Corpus.Shipment, """{ "id": 1, "loadingTimeType": 1 }"""))
            .ToDictionary(row => Guid.Parse(row["id"]!.GetValue<string>()));

        foreach (var row in declared.Concat(undeclared))
            wire[row.Id]["loadingTimeType"]!.GetValue<int>().Should().Be(Corpus.ValueAt(row, "loadingTimeType")!.AsInt32, row.Key);

        foreach (var row in absent)
            Json.Has(wire[row.Id], "loadingTimeType").Should().BeFalse($"{row.Key}: the engine invents no value for a member storage does not have");
    }

    [Fact]
    public async Task SH06_the_bytes_the_client_compiles_for_the_eq_case_answer_the_same_rows()
    {
        // The legacy battery captured these bytes from the TypeScript client; that half now lives
        // with the client's specs. The engine half: exactly these bytes, as one request body.
        var expected = Corpus.IdsOf(Corpus.ShipmentsWithStatus("Planned"));

        var answer = await (await Transport()).QueryAsync(
            """{"entityType":"transport.shipment","pipeline":[{"match":{"status.name":{"eq":"Planned"}}},{"sort":[{"id":"asc"}]},{"page":{"limit":500,"includeTotalCount":true}}]}""");

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
    }

    [Fact]
    public async Task SH07_the_in_list_body_with_an_explicit_false_count_answers_the_slice_and_no_total()
    {
        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Shipment, "id", descending: true, filter: row => Corpus.Text(row, "status.name") is "Planned" or "Closed"), limit: 5);

        var answer = await (await Transport()).QueryAsync(
            """{"entityType":"transport.shipment","pipeline":[{"match":{"status.name":{"in":["Planned","Closed"]}}},{"sort":[{"id":"desc"}]},{"page":{"limit":5,"includeTotalCount":false}}]}""");

        answer.ShouldHaveIds(expected);
        answer.TotalCount.Should().BeNull("a count nobody asked for is not computed");
        answer.PageInfo.ContainsKey("totalCount").Should().BeFalse(answer.ToString());
    }
}
