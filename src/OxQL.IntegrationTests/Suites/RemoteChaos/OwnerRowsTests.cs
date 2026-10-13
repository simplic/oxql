using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Conformance;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.RemoteChaos;

/// <summary>
/// An owner whose answer holds other rows than the ones asked for: rows of keys nobody asked,
/// every row twice, rows left out, the rows in another order. Whatever it sends, a row of the page
/// is joined with the record of its own key or with none: never with another key's record, and the
/// same on every page size. Generated rows against the owner's own table.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OwnerRowsTests : IAsyncLifetime
{
    private const int Rows = 64;

    private const int Widgets = 40;

    private CorpusFleet? fleet;

    private CorpusFleet Fleet => fleet ?? throw new InvalidOperationException("The fixture has not started.");

    private static Guid IdOf(int n) => Ids.Of(Spaces.ConformanceEntity, Org.A, 7_000 + (n * 41 % 211));

    /// <summary>The widget a row names: one of the owner's, one the owner does not have, or none.</summary>
    private static string? CodeOf(int n) => (n % 8) switch
    {
        6 => null,
        7 => $"W-{9_000 + n:D4}",
        _ => $"W-{1 + (n * 7 % Widgets):D4}",
    };

    public async Task InitializeAsync() =>
        fleet = await CorpusFleet.CreateAsync("owner_rows", seed: [], extra: async created =>
        {
            var database = await created.DatabaseAsync(LabService.Conformance);

            await database.GetCollection<BsonDocument>(LabService.Conformance.Model.Entities[Corpus.Conformance].Collection).InsertManyAsync(Enumerable.Range(1, Rows).Select(n =>
                new ConformanceEntity { Id = IdOf(n), OrganizationId = Org.A.Id(), Name = $"row {n:D3}", Count = n, WidgetCodeExplicit = CodeOf(n) }.ToBsonDocument()));
        });

    public async Task DisposeAsync()
    {
        if (fleet is not null)
            await fleet.DisposeAsync();
    }

    public static TheoryData<ChaosRows, string, string> Cases()
    {
        var data = new TheoryData<ChaosRows, string, string>();

        foreach (var rows in Enum.GetValues<ChaosRows>())
            foreach (var (form, more) in new[] { ("plain", ""), ("with its outcome", """, "outcomeAs": "said" """), ("reported", """, "onMissing": "report" """) })
                data.Add(rows, form, more);

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_row_is_joined_with_the_record_of_its_own_key_or_with_none(ChaosRows rows, string form, string more)
    {
        Fleet.Owner.Reset();
        Fleet.Owner.Set(new ChaosSettings { Mode = ChaosMode.Ok, Many = Widgets, Rows = rows });

        var client = Fleet.Client(LabService.Conformance);
        var expected = Enumerable.Range(1, Rows).OrderBy(n => n).ToList();
        List<string>? first = null;

        foreach (var limit in new[] { 100, 9 })
        {
            // A filter of the case's own: no answer of another case, or of the other page size, is read from the cache.
            var stages = $$"""
                [ { "resolve": { "path": "widgetCodeExplicit", "as": "w", "filter": { "name": { "neq": "{{rows}} {{form}} {{limit}}" } }{{more}} } },
                  { "sort": [ { "count": "asc" } ] },
                  { "project": { "count": 1, "widgetCodeExplicit": 1, "w.code": 1, "w.name": 1{{(more.Contains("outcomeAs") ? """, "said": 1""" : "")}} } } ]
                """;

            var answer = new List<JsonObject>();
            string? cursor = null;

            do
            {
                var page = new JsonObject { ["limit"] = limit };

                if (cursor is not null)
                    page["cursor"] = cursor;

                var pipeline = (JsonArray)JsonNode.Parse(stages)!;

                pipeline.Add(new JsonObject { ["page"] = page });

                var got = await client.QueryAsync(new JsonObject { ["entityType"] = Corpus.Conformance, ["pipeline"] = pipeline }.ToJsonString());

                got.ShouldBeOk($"{rows}, {form}, pages of {limit}");
                answer.AddRange(got.Items.OfType<JsonObject>());
                cursor = got.HasNextPage ? got.NextCursor : null;
            }
            while (cursor is not null);

            answer.Select(row => row["count"]!.GetValue<int>()).Should().Equal(expected, $"{rows}, {form}: every row of the page is answered");

            foreach (var row in answer)
            {
                var n = row["count"]!.GetValue<int>();
                var code = CodeOf(n);
                var known = code is not null && int.Parse(code[2..]) <= Widgets;
                var what = $"{rows}, {form}, pages of {limit}: {row.ToJsonString()}";

                if (row["w"] is JsonObject widget)
                {
                    widget["code"]!.GetValue<string>().Should().Be(code, what);
                    widget["name"]!.GetValue<string>().Should().Be($"Widget {int.Parse(code![2..]):D4}", what);
                }
                else
                {
                    // An owner that left a row out has said nothing of its key; any other owner answered every key it has.
                    if (rows != ChaosRows.Fewer && rows != ChaosRows.Twice)
                        known.Should().BeFalse(what);
                }

                if (row["said"] is JsonValue said)
                {
                    var outcome = said.GetValue<string>();

                    if (code is null)
                        outcome.Should().Be("reference_null", what);
                    else if (!known)
                        outcome.Should().Be("not_found", what);
                    else if (row["w"] is JsonObject)
                        outcome.Should().BeOneOf(["resolved", "ambiguous"], what);
                    else
                        // An owner that leaves the row out of the answer with the join's filter and sends it in the one without
                        // has said the filter kept the record away.
                        outcome.Should().BeOneOf(["not_found", "excluded", "ambiguous", "owner_unanswered"], what);
                }
            }

            // What a row is joined with does not depend on the page it is on.
            var joined = answer.Select(row => $"{row["count"]}:{row["w"]?["code"]}").ToList();

            if (rows != ChaosRows.Fewer)
            {
                first ??= joined;
                joined.Should().Equal(first, $"{rows}, {form}: pages of {limit} join what the first walk joined");
            }
        }
    }
}
