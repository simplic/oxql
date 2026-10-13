using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// Bodies that are valid JSON and odd: every value of a request that uses every stage replaced, one
/// at a time, by values of every JSON kind, by numbers at and beyond the limits of a number, by
/// names the engine keeps for itself, by paths that are none. Whatever the body, the answer is a
/// page or a refusal in the envelope with a code: never a fault, on <c>query</c>, <c>explain</c> and
/// as an entry of a <c>batch</c>, whose other entries are answered as they are alone.
/// </summary>
[Trait("Category", "Integration")]
public class OddBodyTests
{
    /// <summary>Requests that between them use every stage, every member of each, and every form of a condition.</summary>
    private static readonly string[] Bases =
    [
        """
        { "entityType": "conformance.entity", "variables": { "v": 2, "t": "alpha" }, "strict": false, "onMissing": "report", "timing": false, "maxTimeMs": 5000,
          "filter": [ { "id": "f1", "where": { "count": { "gte": { "$var": "v" } } } }, { "where": { "or": [ { "name": { "startsWith": { "$var": "t" }, "options": { "caseSensitive": true } } }, { "not": { "note": { "eq": null } } } ] } } ],
          "pipeline": [
            { "match": { "and": [ { "optionalCount": { "in": [1, 2, null] } }, { "items": { "any": { "code": { "contains": "a" }, "quantity": { "gt": 0, "lte": 3 } } } } ], "tags": { "nin": ["x"] }, "optionalMoment": { "lt": "2027-01-01T00:00:00Z" }, "optionalDay": { "exists": true }, "name": { "regex": "^[A-Za-z]" } } },
            { "resolve": { "path": "refCode", "as": "r", "filter": { "isDeleted": { "neq": true } }, "onMissing": "null", "outcomeAs": "found" } },
            { "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids", "filter": { "sequence": { "gte": 0 } }, "limit": 3, "sort": [ { "sequence": "desc" } ] } },
            { "unwind": { "path": "items", "as": "item", "includeIndex": "position", "preserveNull": true } },
            { "sort": [ { "note": "asc" }, { "optionalCount": { "direction": "desc" } }, { "id": "desc" } ] },
            { "project": { "id": 1, "name": 1, "item.code": 1, "position": 1, "r.name": 1, "kids.name": 1, "found": 1 } },
            { "page": { "limit": 5, "count": { "upTo": 100, "budgetMs": 500 } } } ] }
        """,
        """
        { "entityType": "conformance.entity",
          "pipeline": [
            { "unwind": { "path": "items", "as": "item" } },
            { "group": { "by": [ { "path": "item.code", "as": "code" }, { "dateTrunc": { "path": "optionalMoment", "unit": "week", "timezone": "Europe/Berlin", "weekStart": "sunday" }, "as": "week" } ],
                         "fields": { "n": { "count": true }, "total": { "sum": { "multiply": [ { "path": "item.quantity" }, 2, { "coalesce": [ "optionalCount", { "literal": 1 } ] } ] } }, "mean": { "avg": { "divide": [ "count", "optionalCount" ] } }, "all": { "push": "name" }, "kinds": { "countDistinct": "note" } } } },
            { "match": { "n": { "gte": 1 } } },
            { "sort": [ { "total": "desc" } ] },
            { "page": { "limit": 3, "count": true } } ] }
        """,
        """
        { "entityType": "conformance.entity", "strict": true,
          "pipeline": [ { "resolve": { "path": "employeeId", "as": "e" } }, { "match": { "e.matchCode": { "startsWith": "emp" } } }, { "project": { "name": 1, "e.matchCode": 1 } }, { "values": { "path": "note", "search": "n", "limit": 5, "counts": true, "scan": 100 } } ] }
        """,
    ];

    /// <summary>What a value is replaced by: every JSON kind, and the edges of each.</summary>
    private static readonly string[] Odd =
    [
        "null", "true", "false", "0", "-0", "1", "-1", "2.5", "1e400", "-1e400", "1e-400", "9223372036854775808", "-9223372036854775809", "123456789012345678901234567890", "0.1234567890123456789012345678901234567890",
        "\"\"", "\" \"", "\"a.b\"", "\"a..b\"", "\".a\"", "\"a.\"", "\"$a\"", "\"a.$b\"", "\"_id\"", "\"_t\"", "\"_v\"", "\"__ox\"", "\"x__arr\"", "\"id\"", "\"addon.x y\"", "\"items\"", "\"items.parts\"", "\"r\"", "\"é\\u0301ü\\ud83d\\ude00\"", "\"\\u0001\"",
        "\"2026-02-30\"", "\"9999-12-31T23:59:59.9999999Z\"", "\"asc\"", "\"NaN\"", "\"Infinity\"", "\"" + new string('x', 300) + "\"",
        "[]", "[null]", "[[]]", "[1, \"a\", null, {}]", "{}", "{ \"$var\": \"v\" }", "{ \"$var\": \"nope\" }", "{ \"$var\": 1 }", "{ \"a\": { \"b\": { \"c\": 1 } } }", "{ \"eq\": 1, \"gt\": 1, \"options\": { \"caseSensitive\": 1 } }", "{ \"path\": \"name\" }", "{ \"literal\": { \"$x\": 1 } }",
    ];

    /// <summary>Every place of a body: the path from the root to each value.</summary>
    private static IEnumerable<List<object>> Places(JsonNode? node, List<object> at)
    {
        yield return at;

        if (node is JsonObject item)
            foreach (var (name, value) in item.ToList())
                foreach (var place in Places(value, [.. at, name]))
                    yield return place;

        if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                foreach (var place in Places(array[index], [.. at, index]))
                    yield return place;
    }

    private static JsonNode Replaced(JsonNode body, List<object> place, string by)
    {
        var copy = body.DeepClone();
        JsonNode? parent = null;
        JsonNode? current = copy;

        foreach (var step in place)
        {
            parent = current;
            current = step is string name ? current![name] : current![(int)step];
        }

        var value = JsonNode.Parse(by);

        if (parent is JsonObject holder)
            holder[(string)place[^1]] = value;
        else if (parent is JsonArray list)
            list[(int)place[^1]] = value;

        return copy;
    }

    /// <summary>The bodies of one base: each value replaced by each odd value, spread so that every place meets every kind over the bases.</summary>
    private static IEnumerable<string> Bodies(int index)
    {
        var body = JsonNode.Parse(Bases[index])!;
        var places = Places(body, []).Where(place => place.Count > 0).ToList();
        var turn = index;

        foreach (var place in places)
        {
            // Each place meets a third of the odd values, another third at the next place: every value meets every kind of place.
            for (var pick = turn++ % 3; pick < Odd.Length; pick += 3)
                yield return Replaced(body, place, Odd[pick]).ToJsonString();

            // A member renamed to a name the engine keeps, and one written twice.
            if (place[^1] is string name)
            {
                var renamed = body.DeepClone();
                JsonNode? holder = renamed;

                foreach (var step in place.Take(place.Count - 1))
                    holder = step is string member ? holder![member] : holder![(int)step];

                var value = holder![name]?.DeepClone();

                ((JsonObject)holder).Remove(name);
                ((JsonObject)holder)[turn % 2 == 0 ? "_id" : "$" + name] = value;

                yield return renamed.ToJsonString();
            }
        }
    }

    private static void ShouldBeAnAnswer(WireAnswer answer, string body, List<string> faults)
    {
        var coded = answer.Body is JsonObject envelope && envelope["type"] is not null && answer.Errors.Count > 0 && answer.Errors.All(error => error["code"] is not null);

        // A time ceiling of a millisecond is a request's own to set, and its expiry a coded answer.
        if (answer.StatusCode == 500 || answer.Type == "internal_error" || (answer.StatusCode != 200 && !coded) || (answer.StatusCode == 200 && !answer.IsPage && answer.Body?["valid"] is null))
            faults.Add($"{answer.StatusCode} {Walks.Short(answer.Text, 300)}\n      body: {Walks.Short(body, 700)}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Every_odd_body_is_answered_with_a_page_or_a_coded_refusal(int index)
    {
        var client = await EdgeCorpus.ClientAsync();
        var faults = new List<string>();
        var sent = 0;

        foreach (var body in Bodies(index))
        {
            sent++;
            ShouldBeAnAnswer(await client.PostAsync("OxQL/query", body), body, faults);

            // Explain reads the same body without running it; every third body is enough to meet every place.
            if (sent % 3 == 0)
                ShouldBeAnAnswer(await client.ExplainHereAsync(body), body, faults);
        }

        sent.Should().BeGreaterThan(300);
        faults.Should().BeEmpty($"{faults.Count} of {sent} bodies were answered with a fault");
    }

    [Fact]
    public async Task An_odd_entry_of_a_batch_is_answered_in_its_place_and_its_neighbours_as_they_are_alone()
    {
        var client = await EdgeCorpus.ClientAsync();
        const string Good = """{ "entityType": "conformance.entity", "pipeline": [ { "sort": [ { "optionalCount": "desc" } ] }, { "project": { "id": 1, "optionalCount": 1 } }, { "page": { "limit": 4 } } ] }""";
        var alone = await client.PostAsync("OxQL/query", Good);
        var faults = new List<string>();
        var entries = Bodies(1).Where((_, position) => position % 9 == 0).ToList();

        alone.ShouldBeOk();
        entries.Count.Should().BeGreaterThan(40);

        foreach (var chunk in entries.Chunk(4))
        {
            var answer = await client.PostAsync("OxQL/batch", $$"""{ "queries": [ {{Good}}, {{string.Join(", ", chunk)}}, {{Good}} ] }""");

            if (answer.StatusCode != 200 || answer.Body?["results"] is not JsonArray results || results.Count != chunk.Length + 2)
            {
                faults.Add($"{answer.StatusCode} {Walks.Short(answer.Text, 300)}");
                continue;
            }

            foreach (var neighbour in new[] { results[0], results[^1] })
                if (neighbour?["items"]?.ToJsonString() != alone.Body!["items"]!.ToJsonString() || neighbour["pageInfo"]?["nextCursor"]?.ToJsonString() != alone.Body["pageInfo"]!["nextCursor"]?.ToJsonString())
                    faults.Add($"a neighbour of an odd entry answered {Walks.Short(neighbour?.ToJsonString() ?? "null", 300)}");

            for (var position = 0; position < chunk.Length; position++)
                if (results[position + 1] is not JsonObject entry || (entry["items"] is null && (entry["type"] is null || entry["type"]!.GetValue<string>() == "internal_error")))
                    faults.Add($"entry answered {Walks.Short(results[position + 1]?.ToJsonString() ?? "null", 300)}\n      body: {Walks.Short(chunk[position], 500)}");
        }

        faults.Should().BeEmpty();
    }
}
