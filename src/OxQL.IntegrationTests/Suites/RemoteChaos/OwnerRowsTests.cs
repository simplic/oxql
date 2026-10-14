using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Conformance;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Perf;
using Xunit;

namespace OxQL.IntegrationTests.Suites.RemoteChaos;

/// <summary>
/// An owner whose answer holds other rows than the ones asked for: rows of keys nobody asked,
/// every row twice, rows left out, the rows in another order. Whatever it sends, a row of the page
/// is joined with the record of its own key or with none: never with another key's record, and the
/// same on every page size. Generated rows against the owner's own table.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OwnerRowsTests : OwnerRowsFleet
{
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

/// <summary>
/// The same owner under load, where what is held is also how long an answer takes: it runs with the
/// tests that measure time, one after another and after every other test.
/// </summary>
[Trait("Category", "Integration")]
[Collection(TimedCollection.Name)]
public sealed class OwnerRowsUnderLoadTests : OwnerRowsFleet
{
    /// <summary>
    /// An owner that goes away and comes back while requests keep arriving. The host remembers an
    /// owner that did not answer and does not call it for a while; whatever the owner does and
    /// whatever the host remembers at the moment a request arrives, the request is answered within
    /// its budget, a row is joined with its own record or with none, a row left without its record
    /// says why, and once the owner is back and the time is over the owner is asked again and nothing
    /// is remembered. It is a soak test and runs only when <c>OXQL_LOAD</c> is set.
    /// </summary>
    [OptInFact(OptInFactAttribute.Load)]
    public async Task While_an_owner_goes_and_comes_back_under_load_every_answer_is_in_time_and_right_and_the_owner_is_asked_again_in_the_end()
    {
        const int ResolveTimeoutMs = 400;

        var client = Fleet.Variant(LabService.Conformance, "memory", new Dictionary<string, string?>
        {
            ["OxQL:Execution:ResolveTimeoutMs"] = ResolveTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OxQL:Cache:OwnerUnavailableTtlSeconds"] = "1",
        });

        var ok = new ChaosSettings { Mode = ChaosMode.Ok, Many = Widgets };

        Fleet.Owner.Reset();
        Fleet.Owner.Set(ok);

        var sent = 0;

        // A filter of the request's own, so every request asks the owner and none reads another's answer.
        async Task<WireAnswer> AskAsync() => await client.QueryAsync(new JsonObject
        {
            ["entityType"] = Corpus.Conformance,
            ["pipeline"] = JsonNode.Parse($$"""
                [ { "resolve": { "path": "widgetCodeExplicit", "as": "w", "filter": { "name": { "neq": "memory {{Interlocked.Increment(ref sent)}}" } } } },
                  { "sort": [ { "count": "asc" } ] },
                  { "project": { "count": 1, "w.code": 1 } },
                  { "page": { "limit": 100 } } ]
                """),
        }.ToJsonString());

        (await AskAsync()).ShouldBeOk().ShouldHaveNoDiagnostics();

        using var over = new CancellationTokenSource();
        var answers = new System.Collections.Concurrent.ConcurrentQueue<WireAnswer>();

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!over.IsCancellationRequested)
                answers.Enqueue(await AskAsync());
        })).ToList();

        // Gone, back, silent, back, answering with an error, back: each for longer than a request takes and shorter or longer than the host remembers.
        foreach (var (mode, forMs) in new[]
        {
            (new ChaosSettings { Mode = ChaosMode.Closed }, 700), (ok, 300), (new ChaosSettings { Mode = ChaosMode.Hang }, 900), (ok, 1_300),
            (new ChaosSettings { Mode = ChaosMode.Status }, 500), (ok, 200), (new ChaosSettings { Mode = ChaosMode.Closed }, 300), (ok, 600),
        })
        {
            Fleet.Owner.Set(mode);
            await Task.Delay(forMs);
        }

        over.Cancel();
        await Task.WhenAll(workers);

        answers.Should().HaveCountGreaterThan(20);

        var joined = 0;
        var remembered = 0;

        foreach (var answer in answers)
        {
            answer.ShouldBeOk();
            answer.Duration.Should().BeLessThan(TimeSpan.FromMilliseconds(ResolveTimeoutMs + 2_500), $"a request waits for an owner no longer than its budget: {answer}");
            answer.Items.Select(row => row!["count"]!.GetValue<int>()).Should().Equal(Enumerable.Range(1, Rows));

            var missing = 0;

            foreach (var row in answer.Items.OfType<JsonObject>())
            {
                var code = CodeOf(row["count"]!.GetValue<int>());
                var known = code is not null && int.Parse(code[2..]) <= Widgets;

                if (row["w"] is JsonObject widget)
                    widget["code"]!.GetValue<string>().Should().Be(code, answer.ToString());
                else if (known)
                    missing++;
            }

            if (missing > 0)
            {
                answer.DiagnosticCodes.Should().Contain(code => code == "RESOLVE_UNREACHABLE" || code == "RESOLVE_TIMEOUT", $"a row left without its record says why: {answer}");
                missing.Should().Be(Enumerable.Range(1, Rows).Count(n => CodeOf(n) is { } code && int.Parse(code[2..]) <= Widgets), "an owner that did not answer answered for no row");

                if (answer.Diagnostics.Any(diagnostic => diagnostic["params"]?["remembered"]?.GetValue<bool>() == true))
                    remembered++;
            }
            else
            {
                answer.ShouldHaveNoDiagnostics();
                joined++;
            }
        }

        joined.Should().BeGreaterThan(0, "the owner answered in between");
        remembered.Should().BeGreaterThan(0, "the host answered from what it remembered in between");

        // The owner is back: once the host's memory of it is over, a request asks it again and nothing is remembered.
        await Task.Delay(1_200);

        var mark = Fleet.Owner.Mark();
        var last = await AskAsync();

        last.ShouldBeOk().ShouldHaveNoDiagnostics();
        Fleet.Owner.BatchesSince(mark).Should().NotBeEmpty("the owner is asked again");
        (await client.HostAsync()).Services.GetRequiredService<OxQL.Core.Engine.OwnerAvailability>().OutageOf(LabFleet.ExternalOwner).Should().BeNull();
    }
}

/// <summary>The fleet both classes run against: rows that name a widget of the owner, one it does not have, or none.</summary>
public abstract class OwnerRowsFleet : IAsyncLifetime
{
    protected const int Rows = 64;

    protected const int Widgets = 40;

    private CorpusFleet? fleet;

    protected CorpusFleet Fleet => fleet ?? throw new InvalidOperationException("The fixture has not started.");

    protected static Guid IdOf(int n) => Ids.Of(Spaces.ConformanceEntity, Org.A, 7_000 + (n * 41 % 211));

    /// <summary>The widget a row names: one of the owner's, one the owner does not have, or none.</summary>
    protected static string? CodeOf(int n) => (n % 8) switch
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
}
