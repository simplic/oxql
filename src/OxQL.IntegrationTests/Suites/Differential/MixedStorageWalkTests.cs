using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Conformance;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// A walk by cursor over a member whose rows hold values of more than one stored type, or a value
/// that is no number: a decimal written as text before a migration beside the migrated rows, a
/// double that is NaN or infinite, a Decimal128 NaN, a member a writer stored as another type than
/// the model declares (text under a whole number, a number under a text). The database orders such rows in one order
/// (by type first, NaN before every number), and the pages of a walk are that order, split: no row
/// twice, none left out, whatever value a page ends on.
/// </summary>
[Trait("Category", "Integration")]
public class MixedStorageWalkTests
{
    private const string Entity = EdgeCorpus.Entity;

    private const int Rows = 36;

    private static readonly Lazy<Task<CorpusFleet>> Fleet = new(() => CorpusFleet.CreateAsync("mixed-storage", seed: [], extra: async fleet =>
    {
        var database = await fleet.DatabaseAsync(LabService.Conformance);

        await database.GetCollection<BsonDocument>(LabService.Conformance.Model.Entities[Entity].Collection).InsertManyAsync(Enumerable.Range(1, Rows).Select(Row));
    }), LazyThreadSafetyMode.ExecutionAndPublication);

    private static BsonDocument Row(int n)
    {
        var row = new ConformanceEntity { Id = EdgeCorpus.IdOf(Org.A, n), OrganizationId = Org.A.Id(), Name = "row " + n, Count = n % 3 }.ToBsonDocument();

        // A decimal as the migrated rows hold it, as text of two scales, as no number at all, null and not stored.
        row["OptionalAmount"] = (n % 9) switch
        {
            0 => new BsonDecimal128(12.5m),
            1 => new BsonString("12.50"),
            2 => new BsonString("7"),
            3 => new BsonDecimal128(-3m),
            4 => new BsonString("n/a"),
            5 => BsonNull.Value,
            6 => new BsonDecimal128(Decimal128.Parse("NaN")),
            7 => new BsonDecimal128(Decimal128.PositiveInfinity),
            _ => new BsonDecimal128(700m),
        };

        if (n % 9 == 5 && n % 2 == 0)
            row.Remove("OptionalAmount");

        // A double that is a number, NaN, infinite either way, null.
        row["OptionalRatio"] = (n % 7) switch
        {
            0 => new BsonDouble(double.NaN),
            1 => new BsonDouble(double.NegativeInfinity),
            2 => new BsonDouble(double.PositiveInfinity),
            3 => BsonNull.Value,
            4 => new BsonDouble(-0.0),
            5 => new BsonDouble(0.0),
            _ => new BsonDouble(1.5),
        };

        // A count four versions of a service wrote, each in its own numeric type.
        row["OptionalCount"] = (n % 6) switch
        {
            0 => new BsonInt32(2),
            1 => new BsonInt64(2),
            2 => new BsonDouble(2.5),
            3 => new BsonDecimal128(1m),
            4 => new BsonDouble(double.NaN),
            _ => BsonNull.Value,
        };

        // Members a writer stored as another type than the model declares: text and a boolean under a
        // whole number, a number and a date under a text.
        row["Count"] = (n % 5) switch
        {
            0 => new BsonString("three"),
            1 => new BsonString("12"),
            2 => BsonBoolean.True,
            _ => new BsonInt32(n % 4),
        };

        row["Note"] = (n % 8) switch
        {
            0 => new BsonInt32(7),
            1 => new BsonDouble(7.5),
            2 => new BsonDateTime(new DateTime(2026, 1, 1 + (n % 3), 0, 0, 0, DateTimeKind.Utc)),
            3 => BsonNull.Value,
            4 => new BsonInt64(-1),
            _ => new BsonString("note " + (n % 3)),
        };

        return row;
    }

    public static TheoryData<string> Sorts() =>
    [
        """[{ "optionalAmount": "asc" }]""", """[{ "optionalAmount": "desc" }]""", """[{ "optionalAmount": "desc" }, { "id": "desc" }]""", """[{ "count": "asc" }, { "optionalAmount": "desc" }]""",
        """[{ "optionalRatio": "asc" }]""", """[{ "optionalRatio": "desc" }]""", """[{ "optionalRatio": "asc" }, { "optionalAmount": "asc" }]""", """[{ "count": "desc" }, { "optionalRatio": "desc" }, { "id": "desc" }]""",
        """[{ "optionalCount": "asc" }]""", """[{ "optionalCount": "desc" }]""", """[{ "optionalCount": "asc" }, { "optionalRatio": "desc" }]""",
        """[{ "count": "asc" }]""", """[{ "count": "desc" }]""", """[{ "note": "asc" }]""", """[{ "note": "desc" }]""",
        """[{ "note": "asc" }, { "count": "desc" }]""", """[{ "count": "asc" }, { "note": "desc" }, { "id": "desc" }]""", """[{ "active": "asc" }, { "note": "asc" }, { "count": "asc" }]""",
    ];

    [Theory]
    [MemberData(nameof(Sorts))]
    public async Task A_walk_over_a_member_of_several_stored_types_is_the_one_page_split(string sort)
    {
        var client = (await Fleet.Value).Client(LabService.Conformance);
        var query = new Walks.Query(Entity, $$"""[{ "sort": {{sort}} }, { "project": { "id": 1, "optionalAmount": 1, "optionalRatio": 1, "optionalCount": 1, "count": 1, "note": 1 } }]""");
        var whole = await Walks.WholeAsync(client, query);

        whole.Should().HaveCount(Rows).And.OnlyHaveUniqueItems();

        (await Walks.DifferencesAsync(client, query, whole, [1, 2, 5, Rows - 1, Rows, Rows + 1])).Should().BeEmpty(sort);
    }

    /// <summary>
    /// The order itself, against the stored rows put in order here: by the stored type first (nothing,
    /// numbers, text, booleans, dates), NaN before every number, the infinities at the ends, numbers
    /// of every stored type by value, and the key where two rows tie. Descending is that order
    /// turned round, the key still ascending.
    /// </summary>
    [Theory]
    [InlineData("optionalAmount", "asc")]
    [InlineData("optionalAmount", "desc")]
    [InlineData("optionalRatio", "asc")]
    [InlineData("optionalRatio", "desc")]
    [InlineData("optionalCount", "asc")]
    [InlineData("optionalCount", "desc")]
    [InlineData("count", "asc")]
    [InlineData("count", "desc")]
    [InlineData("note", "asc")]
    [InlineData("note", "desc")]
    public async Task The_pages_are_in_the_order_the_stored_values_have(string member, string direction)
    {
        var client = (await Fleet.Value).Client(LabService.Conformance);
        var storage = EdgeOracle.Storage(member);
        var stored = Enumerable.Range(1, Rows).Select(Row).ToList();

        stored.Sort((left, right) =>
        {
            var order = Order.Compare(EdgeOracle.At(left, storage), EdgeOracle.At(right, storage));

            if (direction == "desc")
                order = -order;

            return order != 0 ? order : Order.Compare(left["_id"], right["_id"]);
        });

        var expected = stored.Select(EdgeCorpus.WireId).ToList();
        var query = new Walks.Query(Entity, $$"""[{ "sort": [{ "{{member}}": "{{direction}}" }] }, { "project": { "id": 1 } }]""");

        foreach (var limit in new[] { 500, 1, 5 })
        {
            var pages = await Walks.PagesAsync(client, query, limit);

            pages.SelectMany(page => page.Items.Select(row => row!["id"]!.GetValue<string>())).Should().Equal(expected, $"{member} {direction} in pages of {limit}");
        }
    }

    /// <summary>The groups of such a member page the same way: a key of every stored type, the group of NaN, the group of null.</summary>
    [Theory]
    [InlineData("optionalAmount")]
    [InlineData("optionalRatio")]
    [InlineData("optionalCount")]
    [InlineData("count")]
    [InlineData("note")]
    public async Task A_walk_over_the_groups_of_such_a_member_is_the_one_page_split(string member)
    {
        var client = (await Fleet.Value).Client(LabService.Conformance);

        foreach (var sort in new[] { "", """, { "sort": [{ "k": "desc" }] }""", """, { "sort": [{ "n": "desc" }, { "k": "asc" }] }""", """, { "sort": [{ "top": "asc" }] }""" })
        {
            var query = new Walks.Query(Entity, $$"""[{ "group": { "by": [{ "path": "{{member}}", "as": "k" }], "fields": { "n": { "count": true }, "top": { "max": "optionalRatio" } } } }{{sort}}]""");
            var whole = await Walks.WholeAsync(client, query);

            // The group of NaN and the group of null are two groups, and a row writes both keys as null.
            whole.Should().NotBeEmpty();

            (await Walks.DifferencesAsync(client, query, whole, [1, 2, 3])).Should().BeEmpty($"{member}{sort}");
        }
    }

    /// <summary>
    /// The position rule of a contract 1 cursor over these rows: it continues by one comparison per
    /// sort member with the last value of the page, and a comparison is made within the stored type
    /// of that value. What holds for every walk: the one page of all rows is the order of the sort,
    /// every page of a walk is in that order, no row is answered twice, and the walk ends. Each case
    /// names, with its sort, the number of rows the positions of a walk in pages of five continue
    /// to: the rule applied to the values those pages end on. A walk whose position continues
    /// across the stored types is a contract 2 walk (<c>X-OxQL-Contract: 2</c>).
    /// </summary>
    [Theory]
    [InlineData("""[{ "count": "asc" }, { "note": "desc" }]""", 12)]
    [InlineData("""[{ "count": "asc" }]""", 14)]
    [InlineData("""[{ "count": "desc" }]""", 7)]
    [InlineData("""[{ "name": "asc" }]""", 36)]
    [InlineData("""[{ "note": "asc" }]""", 19)]
    [InlineData("""[{ "note": "desc" }]""", 10)]
    [InlineData("""[{ "optionalAmount": "asc" }]""", 8)]
    [InlineData("""[{ "optionalAmount": "desc" }]""", 16)]
    [InlineData("""[{ "optionalCount": "asc" }]""", 12)]
    [InlineData("""[{ "optionalCount": "desc" }]""", 30)]
    [InlineData("""[{ "optionalRatio": "asc" }]""", 10)]
    [InlineData("""[{ "optionalRatio": "desc" }]""", 31)]
    public async Task A_contract_1_walk_over_such_a_member_continues_by_comparison_with_the_last_value_in_the_order_of_the_sort(string sort, int continued)
    {
        var fleet = await Fleet.Value;
        var query = new Walks.Query(Entity, $$"""[{ "sort": {{sort}} }, { "project": { "id": 1 } }]""");
        var whole = (await Walks.WholeAsync(fleet.Client(LabService.Conformance), query)).Select(Key).ToList();
        var legacy = fleet.Client(LabService.Conformance, contract: 1);

        // A contract 1 row is the stored document, its key under the stored name.
        static string Key(string row) => System.Text.Json.Nodes.JsonNode.Parse(row)!.AsObject().Single().Value!.GetValue<string>();

        // One page of every row is the order contract 2 answers.
        (await Walks.WholeAsync(legacy, query)).Select(Key).Should().Equal(whole, sort);

        var rows = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = new System.Text.Json.Nodes.JsonObject { ["limit"] = 5 };

            if (cursor is not null)
                page["cursor"] = cursor;

            var answer = await legacy.QueryAsync(query.Body(page));

            answer.ShouldBeOk($"{sort}, page {pages + 1}");
            rows.AddRange(Walks.Rows(answer).Select(Key));
            cursor = answer.HasNextPage ? answer.NextCursor : null;
        }
        while (cursor is not null && ++pages < Rows);

        cursor.Should().BeNull($"{sort}: the walk ends");
        rows.Should().OnlyHaveUniqueItems(sort);
        rows.Should().BeSubsetOf(whole).And.Equal(whole.Where(rows.Contains), $"{sort}: the rows of the walk are in the order of the sort");
        rows.Should().HaveCount(continued, $"{sort}: the rows the positions of this walk continue to");
    }
}
