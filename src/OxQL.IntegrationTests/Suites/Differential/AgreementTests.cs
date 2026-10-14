using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Differential.EdgeCondition;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// The answers that have to agree with one another, over the generated queries of the other suites
/// of this folder: explain with the run, a request alone with the same request in a batch, contract
/// 1 answered by the rewrite with contract 2, a list of values with the group and the selections it
/// stands for, a count in every state with the rows there are, and the rows of one organisation
/// with nothing of another's, whatever runs beside them.
/// </summary>
[Trait("Category", "Integration")]
public class AgreementTests
{
    private const string Entity = EdgeCorpus.Entity;

    /// <summary>Every generated query of the walks, by its stages.</summary>
    private static IEnumerable<string> Generated() =>
        new[] { PagingDifferentialTests.RootSorts(), PagingDifferentialTests.UnwoundSorts(), PagingDifferentialTests.GroupSorts(), PagingDifferentialTests.JoinedSorts() }
            .SelectMany(data => data.Select(row => (string)row[1]));

    /// <summary>Requests the binder refuses, one of each kind of mistake.</summary>
    private static readonly string[] Refused =
    [
        """[{ "match": { "nope": 1 } }]""", """[{ "match": { "count": { "eq": "many" } } }]""", """[{ "match": { "count": { "like": 1 } } }]""", """[{ "match": { "state": { "eq": "Nope" } } }]""",
        """[{ "match": { "name": { "gt": "a", "options": { "caseSensitive": true } } } }]""", """[{ "match": { "and": [] } }]""", """[{ "match": { "tags": { "any": { "x": 1 } } } }]""",
        """[{ "sort": [{ "items.code": "asc" }] }]""", """[{ "sort": [{ "name": "up" }] }]""", """[{ "unwind": { "path": "name", "as": "n" } }]""", """[{ "unwind": { "path": "items" } }]""",
        """[{ "group": { "by": [{ "path": "items.code", "as": "c" }], "fields": {} } }]""", """[{ "group": { "by": [], "fields": { "x": { "median": "count" } } } }]""",
        """[{ "group": { "by": [{ "dateTrunc": { "path": "moment", "unit": "eon" }, "as": "d" }], "fields": {} } }]""", """[{ "group": { "by": [], "fields": { "x": { "sum": { "add": ["name", 1] } } } } }]""",
        """[{ "project": { "name": 1, "note": 0 } }]""", """[{ "project": { "scratch": 1 } }]""", """[{ "resolve": { "path": "name", "as": "r" } }]""", """[{ "resolve": { "path": "refCode", "as": "name" } }]""",
        """[{ "lookup": { "from": "conformance.child", "path": "name", "as": "kids" } }]""", """[{ "lookup": { "from": "conformance.nope", "path": "parentId", "as": "kids" } }]""",
        """[{ "page": { "limit": 501 } }]""", """[{ "page": { "limit": 0 } }]""", """[{ "page": { "limit": 5 } }, { "match": { "count": 1 } }]""", """[{ "page": { "limit": 5, "offset": 2 } }]""",
        """[{ "values": { "path": "items" } }]""", """[{ "values": { "path": "count", "search": "1" } }]""", """[{ "limitTo": 5 }]""", """[{ "match": { "count": 1 }, "sort": [] }]""",
        """[{ "match": { "count": { "eq": { "$var": "nope" } } } }]""", """[{ "resolve": { "path": "employeeId", "as": "e" } }, { "sort": [{ "e.matchCode": "asc" }] }]""",
    ];

    // ── explain and the run ───────────────────────────────────────────────────────────────

    /// <summary>What explain says of a request is what running it does: valid and executable where it answers rows, the same codes at the same places where it is refused.</summary>
    [Fact]
    public async Task E_explain_says_of_every_generated_request_what_the_run_does()
    {
        var client = await EdgeCorpus.ClientAsync();
        var differences = new List<string>();
        var asked = 0;

        // Every third generated query is enough to meet every shape; every refusal is asked.
        foreach (var stages in Generated().Where((_, index) => index % 3 == 0).Concat(Refused))
        {
            asked++;

            var run = await client.SendAsync(Entity, $"{stages[..^1]}{(stages.Contains("\"page\"", StringComparison.Ordinal) || stages.Contains("\"values\"", StringComparison.Ordinal) ? "" : (stages.Trim() == "[]" ? "" : ", ") + """{ "page": { "limit": 3 } }""")}]");
            var explained = await client.ExplainHereAsync($$"""{ "entityType": "{{Entity}}", "pipeline": {{stages}} }""");

            if (explained.StatusCode != 200 || explained.Body?["valid"] is null)
            {
                differences.Add($"{stages}: explain answered {explained.StatusCode} {Walks.Short(explained.Text, 200)}");
                continue;
            }

            var valid = explained.Body["valid"]!.GetValue<bool>();
            var codes = (explained.Body["errors"] as JsonArray ?? []).Select(error => $"{error!["code"]!.GetValue<string>()}@{error["stage"]?.ToJsonString() ?? "-"}").Order().ToList();
            var ran = run.Errors.Select(error => $"{error["code"]!.GetValue<string>()}@{error["stage"]?.ToJsonString() ?? "-"}").Order().ToList();

            if (valid != run.IsPage)
                differences.Add($"{stages}: explain says valid {valid}, the run answered {run.StatusCode} {Walks.Short(run.Text, 200)}");
            else if (!valid && !codes.SequenceEqual(ran))
                differences.Add($"{stages}: explain says [{string.Join(", ", codes)}], the run [{string.Join(", ", ran)}]");
            else if (valid && explained.Body["executable"]?.GetValue<string>() is { } executable && executable != "yes")
                differences.Add($"{stages}: the run answered rows and explain says executable '{executable}'");
        }

        asked.Should().BeGreaterThan(200);
        differences.Should().BeEmpty();
    }

    /// <summary>The <c>etag</c> of an explain is one for one answer and another for another.</summary>
    [Fact]
    public async Task E_two_explains_of_one_request_carry_one_etag_and_another_request_another()
    {
        var client = await EdgeCorpus.ClientAsync();
        string? Tag(WireAnswer answer) => answer.Body?["etag"]?.GetValue<string>() ?? (answer.Headers.TryGetValue("ETag", out var header) ? header : null);

        var one = await client.ExplainHereAsync($$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "count": 1 } }] }""");
        var again = await client.ExplainHereAsync($$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "count": 1 } }] }""");
        var other = await client.ExplainHereAsync($$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "count": 2 } }] }""");

        one.StatusCode.Should().Be(200);

        var tag = Tag(one);

        tag.Should().NotBeNullOrEmpty("an explain answer carries its etag");
        Tag(again).Should().Be(tag);
        Tag(other).Should().NotBeNullOrEmpty().And.NotBe(tag);

        JsonNode Without(JsonNode body)
        {
            var copy = (JsonObject)body.DeepClone();

            copy.Remove("etag");
            copy.Remove("ms");

            return copy;
        }

        Without(again.Body!).ToJsonString().Should().Be(Without(one.Body!).ToJsonString(), "the same request is the same answer");
    }

    // ── a request alone and in a batch ────────────────────────────────────────────────────

    [Fact]
    public async Task B_a_request_in_a_batch_is_answered_as_it_is_alone_whatever_stands_beside_it()
    {
        var client = await EdgeCorpus.ClientAsync();
        var requests = Generated().Where((_, index) => index % 11 == 0).Take(36).Select(stages => $$"""{ "entityType": "{{Entity}}", "pipeline": {{stages[..^1]}}{{(stages.Trim() == "[]" ? "" : ", ")}}{ "page": { "limit": 4, "count": true } }] }""")
            .Concat(Refused.Take(8).Select(stages => $$"""{ "entityType": "{{Entity}}", "pipeline": {{stages}} }"""))
            .Append("""{ "entityType": "nowhere.thing", "pipeline": [] }""")
            .Append($$"""{ "entityType": "{{Entity}}", "maxTimeMs": 1, "pipeline": [{ "group": { "by": [{ "path": "name", "as": "k" }], "fields": { "all": { "push": "note" } } } }] }""")
            .ToList();

        static string Answer(JsonNode? body)
        {
            var copy = body!.DeepClone();

            // How long a count took is no part of what it found; a request under a ceiling of a millisecond ends as the clock has it.
            (copy["pageInfo"]?["count"] as JsonObject)?.Remove("ms");

            return copy["errors"]?[0]?["code"]?.GetValue<string>() == "QUERY_TIMEOUT" || copy["items"] is not null && body!["pageInfo"]?["count"]?["state"]?.GetValue<string>() == "unknown" ? "(by the clock)" : copy.ToJsonString();
        }

        var alone = new List<string>();

        foreach (var request in requests)
            alone.Add(Answer((await client.PostAsync("OxQL/query", request)).Body));

        // In the order written and reversed, ten at a time: every request stands beside every kind of neighbour.
        foreach (var order in new[] { requests.Select((request, index) => (request, index)).ToList(), requests.Select((request, index) => (request, index)).Reverse().ToList() })
        {
            foreach (var chunk in order.Chunk(10))
            {
                var batch = await client.PostAsync("OxQL/batch", $$"""{ "queries": [{{string.Join(", ", chunk.Select(entry => entry.request))}}] }""");

                batch.StatusCode.Should().Be(200, batch.ToString());
                batch.Results.Should().HaveCount(chunk.Length);

                for (var position = 0; position < chunk.Length; position++)
                {
                    var expected = alone[chunk[position].index];
                    var answered = Answer(batch.Results[position].Body);

                    if (expected != "(by the clock)" && answered != "(by the clock)")
                        answered.Should().Be(expected, $"entry {position} of a batch is the request alone: {chunk[position].request}");
                }
            }
        }
    }

    // ── contract 1 by the rewrite and contract 2 ──────────────────────────────────────────

    /// <summary>
    /// What both contracts can say (conditions on members that are no text, a sort by such members
    /// and the key, a page by cursor): the rewrite of a contract 1 request answers the rows contract 2
    /// answers, in the same order, page by page.
    /// </summary>
    [Fact]
    public async Task C_the_rewrite_of_a_contract_1_request_answers_the_rows_of_contract_2()
    {
        var fleet = await EdgeCorpus.FleetAsync();
        var one = fleet.Variant(LabService.Conformance, "rewrite-only", new Dictionary<string, string?> { ["OxQL:Compat:Legacy:Enabled"] = "false" }, contract: null);
        var two = fleet.Client(LabService.Conformance);
        var differences = new List<string>();
        var conditions = MatchOracleTests.Pool.OfType<Leaf>().Where(leaf => EdgeOracle.Members[leaf.Path] is not (EdgeKind.Text or EdgeKind.Letter) && leaf.CaseSensitive is null).ToList();
        string[] sorts = ["""[{ "optionalCount": "asc" }]""", """[{ "optionalMoment": "desc" }, { "id": "desc" }]""", """[{ "count": "desc" }, { "optionalRatio": "asc" }]""", """[{ "id": "desc" }]"""];

        conditions.Count.Should().BeGreaterThan(10);

        for (var index = 0; index < conditions.Count; index++)
        {
            var stages = $$"""[{ "match": {{conditions[index]}} }, { "sort": {{sorts[index % sorts.Length]}} }]""";
            var expected = EdgeOracle.Matching(conditions[index]).Count;

            async Task<List<string>?> Walk(LabClient client, string key)
            {
                var ids = new List<string>();
                string? cursor = null;

                do
                {
                    var pipeline = (JsonArray)JsonNode.Parse(stages)!;

                    pipeline.Add(new JsonObject { ["page"] = new JsonObject { ["limit"] = 7, ["cursor"] = cursor } });

                    var answer = await client.SendAsync(Entity, pipeline);

                    if (!answer.IsPage)
                    {
                        differences.Add($"{stages} ({client}): {answer.StatusCode} {Walks.Short(answer.Text, 200)}");
                        return null;
                    }

                    ids.AddRange(answer.Ids(key).Select(id => id.ToString("D")));
                    cursor = answer.HasNextPage ? answer.NextCursor : null;
                }
                while (cursor is not null && ids.Count < 1_000);

                return ids;
            }

            var first = await Walk(one, "_id");
            var second = await Walk(two, "id");

            if (first is null || second is null)
                continue;

            if (second.Count != expected)
                differences.Add($"{stages}: contract 2 walks {second.Count} rows, the rows are {expected}");

            if (!first.SequenceEqual(second))
                differences.Add($"{stages}: {Walks.Describe(second, first)}");
        }

        differences.Should().BeEmpty();
    }

    // ── values, groups and selections ─────────────────────────────────────────────────────

    /// <summary>
    /// The list of a path's values is the stored rows': a value per folded spelling with the elements
    /// that hold it, null for what a comparison with null selects; and each value of the list, sent
    /// back as <c>in</c>, selects the rows that hold it.
    /// </summary>
    [Theory]
    [InlineData("note")]
    [InlineData("name")]
    [InlineData("optionalCount")]
    [InlineData("optionalActive")]
    [InlineData("optionalAddress.zip")]
    [InlineData("items.code")]
    [InlineData("items.quantity")]
    [InlineData("items.parts.sku")]
    public async Task V_the_values_of_a_path_are_the_stored_rows_and_each_selects_the_rows_that_hold_it(string path)
    {
        var client = await EdgeCorpus.ClientAsync();
        var text = EdgeOracle.Members[path] == EdgeKind.Text;
        // A number's trailing zeros are dropped only from its fractional part: 10 stays 10, 2.50 is 2.5.
        static string Number(string number) => number.Contains('.') && !number.Contains('E') ? number.TrimEnd('0').TrimEnd('.') is { Length: > 0 } trimmed ? trimmed : "0" : number;
        string Key(BsonValue value) => value.IsBsonNull ? "(null)" : value.IsNumeric ? Number(Order.NumberText(value)) : text ? EdgeOracle.Fold(value.AsString) : value.ToString()!.ToLowerInvariant();
        var expected = EdgeCorpus.Stored().SelectMany(row => EdgeOracle.Reach(row, EdgeOracle.Storage(path)).Values).GroupBy(Key).ToDictionary(group => group.Key, group => group.Count());

        var listed = await client.SendAsync(Entity, $$"""[{ "values": { "path": "{{path}}", "counts": true, "limit": 500 } }]""");

        listed.ShouldBeOk().ShouldHaveNoDiagnostics();
        listed.HasNextPage.Should().BeFalse();

        string KeyOf(JsonNode? value) => value is null ? "(null)" : value.GetValueKind() == System.Text.Json.JsonValueKind.String ? (text ? EdgeOracle.Fold(value.GetValue<string>()) : value.GetValue<string>()) : value.ToJsonString().ToLowerInvariant();

        listed.Items.ToDictionary(row => KeyOf(row!["value"]), row => row!["count"]!.GetValue<int>()).Should().Equal(expected, path);
        listed.Items.Select(row => row!["count"]!.GetValue<int>()).Should().BeInDescendingOrder("with counts the values lead by them");
        listed.ShouldCount("exact", expected.Count);

        // Without counts: the same values, by value.
        var plain = await client.SendAsync(Entity, $$"""[{ "values": { "path": "{{path}}", "limit": 500 } }]""");

        plain.Items.Select(row => KeyOf(row!["value"])).Should().BeEquivalentTo(expected.Keys);

        // Each value, sent back, selects the rows the oracle selects for it; all of them together every row that holds a value.
        foreach (var row in listed.Items)
        {
            var condition = new Leaf(path, "in", new JsonArray(row!["value"]?.DeepClone()));
            var selected = await client.MatchIdsAsync(Entity, condition.Json());

            selected.Select(id => id.ToString("D")).Should().Equal(EdgeOracle.Matching(condition), $"{path} in [{row["value"]?.ToJsonString() ?? "null"}]");
        }

        // On a path that crosses no collection, a group by it counts the same.
        if (!path.StartsWith("items", StringComparison.Ordinal))
        {
            var grouped = await client.SendAsync(Entity, $$"""[{ "group": { "by": [{ "path": "{{path}}", "as": "value" }], "fields": { "count": { "count": true } } } }, { "page": { "limit": 500 } }]""");

            grouped.Items.ToDictionary(row => KeyOf(row!["value"]), row => row!["count"]!.GetValue<int>()).Should().Equal(expected, path);
            grouped.Items.Sum(row => row!["count"]!.GetValue<int>()).Should().Be(EdgeCorpus.Count, "the groups part the rows");
        }
    }

    /// <summary>
    /// A list of values narrowed by <c>search</c> is the values that start with the text, folded as a
    /// comparison folds: against the stored rows, with the counts of the elements that hold each, for
    /// texts in another case, with an accent, with the characters a pattern has a meaning for, and
    /// one no value starts with.
    /// </summary>
    [Theory]
    [InlineData("name", new[] { "al", "AL", "Äl", "mu", "MÜ", "mue", "str", "stras", "a.", "a+b(", "zulu [", "x\\", "50%", "under_", "z", "ce", "ç", "zz-none", "alphabet" })]
    [InlineData("note", new[] { "n", "N", "no", "zz-none" })]
    [InlineData("items.code", new[] { "a", "A-", "ä", "c.", "C.3", "d*", "b-2", "-", "zz-none" })]
    [InlineData("items.parts.sku", new[] { "s", "S", "sku", "zz-none" })]
    public async Task V_the_values_a_search_keeps_are_the_stored_ones_that_start_with_it(string path, string[] searches)
    {
        var client = await EdgeCorpus.ClientAsync();
        var stored = EdgeCorpus.Stored().SelectMany(row => EdgeOracle.Reach(row, EdgeOracle.Storage(path)).Values).Where(value => value is BsonString).Select(value => EdgeOracle.Fold(value.AsString)).GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
        var kept = 0;

        stored.Should().NotBeEmpty(path);

        foreach (var search in searches)
        {
            var expected = stored.Where(value => value.Key.StartsWith(EdgeOracle.Fold(search), StringComparison.Ordinal)).ToDictionary(value => value.Key, value => value.Value);
            var listed = await client.SendAsync(Entity, $$"""[{ "values": { "path": "{{path}}", "search": {{System.Text.Json.JsonSerializer.Serialize(search)}}, "counts": true, "limit": 500 } }]""");

            listed.ShouldBeOk();
            listed.Items.ToDictionary(row => EdgeOracle.Fold(row!["value"]!.GetValue<string>()), row => row!["count"]!.GetValue<int>()).Should().Equal(expected, $"{path} search '{search}'");
            listed.ShouldCount("exact", expected.Count);

            kept += expected.Count;
        }

        kept.Should().BeGreaterThan(0, "some search keeps a value");
    }

    // ── counts in every state ─────────────────────────────────────────────────────────────

    /// <summary>
    /// On a host whose count step is ten rows and whose cap is forty: beside every page of a walk the
    /// count is exact up to the step and a lower bound above it, never more than the rows there are,
    /// and exact on the last page; the count alone is exact up to the cap and says the cap above it.
    /// </summary>
    [Fact]
    public async Task N_a_count_says_no_more_than_is_known_and_never_more_than_there_is()
    {
        var fleet = await EdgeCorpus.FleetAsync();
        var client = fleet.Variant(LabService.Conformance, "count-step-10", new Dictionary<string, string?> { ["OxQL:Count:StepRows"] = "10", ["OxQL:Limits:CountCap"] = "40" });
        var differences = new List<string>();

        (string Stages, int Rows)[] queries =
        [
            ("""[{ "match": { "count": 1 } }]""", EdgeCorpus.Stored().Count(row => row["Count"].ToInt32() == 1)),
            ("""[{ "match": { "optionalCount": { "eq": 2 } } }, { "sort": [{ "note": "asc" }] }]""", EdgeOracle.Matching(new Leaf("optionalCount", "eq", 2)).Count),
            ("""[{ "sort": [{ "optionalMoment": "desc" }] }]""", EdgeCorpus.Count),
            ("""[{ "match": { "count": { "in": [1, 2] } } }]""", EdgeCorpus.Stored().Count(row => row["Count"].ToInt32() is 1 or 2)),
            ("""[{ "unwind": { "path": "items", "as": "item" } }]""", EdgeCorpus.Stored().Sum(row => row.GetValue("Items", BsonNull.Value) is BsonArray items ? items.Count : 0)),
            ("""[{ "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "gte": 2 } } }, { "sort": [{ "note": "desc" }] }]""", EdgeCorpus.Stored().Sum(row => row.GetValue("Items", BsonNull.Value) is BsonArray items ? items.Count(item => item["Quantity"].ToInt32() >= 2) : 0)),
            ("""[{ "group": { "by": [{ "path": "optionalMoment", "as": "k" }], "fields": { "n": { "count": true } } } }]""", EdgeCorpus.Stored().Select(row => row.GetValue("OptionalMoment", BsonNull.Value)).Distinct().Count()),
            ("""[{ "match": { "count": 99 } }]""", 0),
        ];

        foreach (var (stages, rows) in queries)
        {
            var query = new Walks.Query(Entity, stages);

            foreach (var limit in new[] { 3, 9, 10, 11, 39, 40, 41 })
            {
                var pages = await Walks.PagesAsync(client, query, limit, count: true);
                var served = 0;

                pages.Sum(page => page.Items.Count).Should().Be(rows, stages);

                for (var index = 0; index < pages.Count; index++)
                {
                    served += pages[index].Items.Count;

                    var said = pages[index].Count!;
                    var state = said["state"]!.GetValue<string>();
                    var value = said["value"]?.GetValue<long>();
                    var last = index == pages.Count - 1;

                    var holds = state switch
                    {
                        "exact" => value == rows,
                        "atLeast" => !last && value <= rows && value >= Math.Min(rows, served + 1) && said["stoppedBy"]?.GetValue<string>() is "step" or "cap" or "page",
                        "unknown" => said["reason"]?.GetValue<string>() == "timeout",
                        _ => false,
                    };

                    // Up to the step the count beside a page is the number.
                    if (rows <= 10 && state == "atLeast")
                        holds = false;

                    if (!holds)
                        differences.Add($"{stages} in pages of {limit}: page {index + 1} of {pages.Count} ({served} of {rows} rows served) says {said.ToJsonString()}");
                }
            }

            var alone = await Walks.CountAsync(client, query);
            var count = alone.Count!;
            var exact = count["state"]!.GetValue<string>() == "exact" && count["value"]!.GetValue<long>() == rows;
            var capped = count["state"]!.GetValue<string>() == "atLeast" && count["value"]!.GetValue<long>() == 40 && count["stoppedBy"]!.GetValue<string>() == "cap";

            if (rows <= 40 ? !exact : !capped)
                differences.Add($"{stages}: the count alone says {count.ToJsonString()}, the rows are {rows}");

            // A request's own bound below the rows there are.
            var bounded = await client.QueryAsync(query.Body(new JsonObject { ["limit"] = 0, ["count"] = new JsonObject { ["upTo"] = 5, ["budgetMs"] = 5_000 } }));
            var five = bounded.Count!;

            if (rows <= 5 ? five["state"]!.GetValue<string>() != "exact" || five["value"]!.GetValue<long>() != rows : five["state"]!.GetValue<string>() != "atLeast" || five["value"]!.GetValue<long>() != 5 || five["stoppedBy"]!.GetValue<string>() != "cap")
                differences.Add($"{stages}: the count up to 5 says {five.ToJsonString()}, the rows are {rows}");
        }

        differences.Should().BeEmpty();
    }

    // ── one organisation beside another ───────────────────────────────────────────────────

    /// <summary>
    /// Two organisations asking the same host the same requests at once, joins into another service
    /// and its kept records included: each reads its own rows and its own records, as it reads them alone.
    /// </summary>
    [Fact]
    public async Task O_what_one_organisation_reads_holds_nothing_of_another_whatever_runs_beside_it()
    {
        var a = await EdgeCorpus.ClientAsync(Org.A);
        var b = await EdgeCorpus.ClientAsync(Org.B);
        string[] requests =
        [
            """[{ "resolve": { "path": "employeeId", "as": "e" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "e.id": 1, "e.matchCode": 1 } }, { "page": { "limit": 200 } }]""",
            """[{ "resolve": { "path": "employeeId", "as": "e" } }, { "match": { "e.matchCode": { "startsWith": "emp" } } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "e.id": 1 } }, { "page": { "limit": 200 } }]""",
            """[{ "resolve": { "path": "refCode", "as": "r" } }, { "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "r.code": 1, "kids.id": 1 } }, { "page": { "limit": 200 } }]""",
            """[{ "group": { "by": [{ "path": "refCode", "as": "code" }], "fields": { "n": { "count": true } } } }, { "resolve": { "path": "code", "as": "r" } }, { "project": { "code": 1, "n": 1, "r.code": 1 } }, { "page": { "limit": 200 } }]""",
            """[{ "values": { "path": "refCode", "counts": true } }]""",
        ];

        var alone = new Dictionary<(Org, int), string>();

        for (var index = 0; index < requests.Length; index++)
        {
            alone[(Org.A, index)] = (await a.SendAsync(Entity, requests[index])).ShouldBeOk().Body!["items"]!.ToJsonString();
            alone[(Org.B, index)] = (await b.SendAsync(Entity, requests[index])).ShouldBeOk().Body!["items"]!.ToJsonString();
        }

        // What each reads alone is its own: every id carries its organisation's tag, and the two differ.
        foreach (var ((org, index), items) in alone)
        {
            var other = org == Org.A ? Org.B : Org.A;

            items.Should().NotContain($"-{other.Tag()}-4000-", $"{org} reads no id of {other} (request {index})");

            if (index < 3)
                items.Should().Contain($"-{org.Tag()}-4000-");
        }

        // The rows of B name the codes A's records carry; B's own records carry others, so B joins none, where A does.
        alone[(Org.A, 2)].Should().Contain("\"r\":{\"code\":\"REF-A\"}");
        alone[(Org.B, 2)].Should().NotContain("\"r\":{");
        alone[(Org.B, 3)].Should().NotContain("\"r\":{");

        // Sixty requests at once, the two organisations interleaved.
        var together = await Task.WhenAll(Enumerable.Range(0, 60).Select(async turn =>
        {
            var org = turn % 2 == 0 ? Org.A : Org.B;
            var index = turn / 2 % requests.Length;
            var answer = await (org == Org.A ? a : b).SendAsync(Entity, requests[index]);

            return (org, index, Items: answer.IsPage ? answer.Body!["items"]!.ToJsonString() : answer.ToString());
        }));

        foreach (var (org, index, items) in together)
            items.Should().Be(alone[(org, index)], $"{org} beside the other reads what it reads alone (request {index})");
    }
}
