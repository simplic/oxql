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
/// Joins and unwound rows against the stored rows: a record of another service joined for the keys
/// of a page in chunks of every size around the number of keys there are, with keys that repeat,
/// that name no record and that are not there; the children and the record a row names on this
/// host; a condition on the element of an unwound collection; and a request cut short by its own
/// time, after which the same requests are answered as before.
/// </summary>
[Trait("Category", "Integration")]
public class JoinOracleTests
{
    private const string Entity = EdgeCorpus.Entity;

    private static string? Text(BsonDocument? row, string member) => row is not null && row.TryGetValue(member, out var value) && value is BsonString text ? text.Value : null;

    private static BsonDocument? Employee(BsonDocument row) =>
        row.TryGetValue("EmployeeId", out var id) && id is BsonBinaryData
            ? EdgeCorpus.Employees.FirstOrDefault(employee => employee["_id"] == id && employee["OrganizationId"] == row["OrganizationId"])
            : null;

    /// <summary>The distinct keys of the page are five (and one that names nobody): chunks of one to seven keys split them at, below and above every boundary.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(500)]
    public async Task A_record_of_another_service_is_joined_the_same_in_chunks_of_any_size(int chunk)
    {
        var fleet = await EdgeCorpus.FleetAsync();
        var client = fleet.Variant(LabService.Conformance, $"chunk-{chunk}", new Dictionary<string, string?> { ["OxQL:Limits:ResolveKeyChunk"] = chunk.ToString(System.Globalization.CultureInfo.InvariantCulture), ["OxQL:Cache:ResolveTtlSeconds"] = "1" });
        var expected = EdgeCorpus.Stored().OrderBy(EdgeCorpus.WireId, StringComparer.Ordinal).Select(row => (Id: EdgeCorpus.WireId(row), Code: Text(Employee(row), "MatchCode"), Found: Employee(row) is not null)).ToList();

        expected.Count(row => row.Found).Should().BeGreaterThan(40);
        expected.Count(row => !row.Found).Should().BeGreaterThan(20);

        foreach (var limit in new[] { 200, 7 })
        {
            var pages = await Walks.PagesAsync(client, new Walks.Query(Entity, """[{ "resolve": { "path": "employeeId", "as": "e", "onMissing": "report" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "e.id": 1, "e.matchCode": 1 } }]"""), limit);
            var rows = pages.SelectMany(page => page.Items).Select(row => (Id: row!["id"]!.GetValue<string>(), Code: row["e"]?["matchCode"]?.GetValue<string>(), Found: row["e"] is JsonObject)).ToList();

            rows.Should().Equal(expected, $"in chunks of {chunk} keys and pages of {limit}");

            // What resolves to nothing is reported with its key, each row once: the keys that name no record, never the ones that are not there.
            var missing = pages.SelectMany(page => page.Diagnostics.Where(diagnostic => diagnostic["code"]!.GetValue<string>() == "RESOLVE_MISSING")).Sum(diagnostic => diagnostic["params"]!["count"]!.GetValue<int>());

            missing.Should().Be(EdgeCorpus.Stored().Count(row => row.TryGetValue("EmployeeId", out var id) && id is BsonBinaryData && Employee(row) is null));
        }
    }

    /// <summary>
    /// The semi-join of a condition on the other service's record selects the rows whose record meets
    /// it, whatever else the request asks. The records spell one code two ways (<c>EMP-EVEN</c>,
    /// <c>Emp-Even</c>): a comparison folds them into one, and <c>caseSensitive</c> tells them apart.
    /// </summary>
    [Theory]
    [InlineData("""{ "e.matchCode": { "startsWith": "emp" } }""", "emp-", false)]
    [InlineData("""{ "e.matchCode": { "eq": "emp-even" } }""", "EMP-EVEN", false)]
    [InlineData("""{ "e.matchCode": { "eq": "EMP-EVEN", "options": { "caseSensitive": true } }, "count": { "gte": 0 } }""", "EMP-EVEN", true)]
    [InlineData("""{ "e.matchCode": { "eq": "Emp-Even", "options": { "caseSensitive": true } } }""", "Emp-Even", true)]
    [InlineData("""{ "e.matchCode": { "startsWith": "Emp", "options": { "caseSensitive": true } } }""", "Emp-", true)]
    public async Task A_condition_on_another_services_record_selects_the_rows_whose_record_meets_it(string condition, string code, bool exact)
    {
        var client = await EdgeCorpus.ClientAsync();
        var comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var expected = EdgeCorpus.Stored().Where(row => Text(Employee(row), "MatchCode") is { } held && (code.EndsWith('-') ? held.StartsWith(code, comparison) : held.Equals(code, comparison)))
            .Select(EdgeCorpus.WireId).Order(StringComparer.Ordinal).ToList();
        var folded = EdgeCorpus.Stored().Count(row => Text(Employee(row), "MatchCode") is { } held && (code.EndsWith('-') ? held.StartsWith(code, StringComparison.OrdinalIgnoreCase) : held.Equals(code, StringComparison.OrdinalIgnoreCase)));

        if (exact)
            expected.Count.Should().BeLessThan(folded, "the exact comparison selects fewer rows than the one that folds");

        expected.Should().NotBeEmpty();

        var query = new Walks.Query(Entity, $$"""[{ "resolve": { "path": "employeeId", "as": "e" } }, { "match": {{condition}} }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1 } }]""");
        var whole = await Walks.WholeAsync(client, query);

        whole.Select(row => JsonNode.Parse(row)!["id"]!.GetValue<string>()).Should().Equal(expected);
        (await Walks.DifferencesAsync(client, query, whole, [1, 3, whole.Count])).Should().BeEmpty();
        (await Walks.CountAsync(client, query)).ShouldCount("exact", expected.Count);
    }

    /// <summary>The children of a row and the record it names on this host, against the stored rows: their order, their cut at the limit, the one taken first, the filter.</summary>
    [Fact]
    public async Task The_children_and_the_record_of_a_row_are_the_stored_ones()
    {
        var client = await EdgeCorpus.ClientAsync();
        var answer = await client.SendAsync(Entity, """
            [ { "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids", "sort": [{ "sequence": "desc" }], "limit": 3, "filter": { "isDeleted": { "neq": true } } } },
              { "lookup": { "from": "conformance.child", "path": "parentId", "as": "first", "sort": [{ "name": "asc" }, { "sequence": "desc" }], "first": true } },
              { "resolve": { "path": "childId", "as": "named", "outcomeAs": "found" } },
              { "resolve": { "path": "refCode", "as": "r", "filter": { "isDeleted": { "neq": true } }, "outcomeAs": "ref" } },
              { "sort": [{ "id": "asc" }] },
              { "project": { "id": 1, "kids.id": 1, "first.id": 1, "named.id": 1, "found": 1, "r.code": 1, "ref": 1 } },
              { "page": { "limit": 200 } } ]
            """);

        answer.ShouldBeOk();
        answer.Items.Should().HaveCount(EdgeCorpus.Count);

        static string Wire(BsonValue id) => id.AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ToString("D");

        var organisation = new BsonBinaryData(Org.A.Id(), GuidRepresentation.Standard);
        var children = EdgeCorpus.Children.Where(child => child["OrganizationId"] == organisation).ToList();
        var refs = EdgeCorpus.Refs.Where(reference => reference["OrganizationId"] == organisation).ToDictionary(reference => reference["_id"].AsString);
        var stored = EdgeCorpus.Stored().OrderBy(EdgeCorpus.WireId, StringComparer.Ordinal).ToList();
        var differences = new List<string>();

        for (var index = 0; index < stored.Count; index++)
        {
            var row = stored[index];
            var answered = answer.Items[index]!;
            var own = children.Where(child => child["ParentId"] == row["_id"]).ToList();

            // The children that are not deleted, by sequence descending and then by key, the first three.
            var kids = own.Where(child => !child["IsDeleted"].AsBoolean).OrderByDescending(child => child["Sequence"].AsInt32).ThenBy(child => Wire(child["_id"]), StringComparer.Ordinal).Take(3).Select(child => Wire(child["_id"])).ToList();
            var first = own.OrderBy(child => child["Name"], Comparer<BsonValue>.Create((left, right) => Order.Compare(left, right))).ThenByDescending(child => child["Sequence"].AsInt32).ThenBy(child => Wire(child["_id"]), StringComparer.Ordinal).Select(child => Wire(child["_id"])).FirstOrDefault();
            var named = row.TryGetValue("ChildId", out var childId) && childId is BsonBinaryData ? children.FirstOrDefault(child => child["_id"] == childId) : null;
            var found = !row.TryGetValue("ChildId", out var held) || held.IsBsonNull ? "reference_null" : named is null ? "not_found" : "resolved";
            var code = Text(row, "RefCode");
            var reference = code is not null && refs.TryGetValue(code, out var target) ? target : null;
            var refOutcome = code is null ? "reference_null" : reference is null ? "not_found" : reference["IsDeleted"].AsBoolean ? "excluded" : "resolved";

            var expected = $"{string.Join(",", kids)}|{first}|{(named is null ? null : Wire(named["_id"]))}|{found}|{(refOutcome == "resolved" ? code : null)}|{refOutcome}";
            var actual = $"{string.Join(",", (answered["kids"] as JsonArray ?? []).Select(kid => kid!["id"]!.GetValue<string>()))}|{answered["first"]?["id"]?.GetValue<string>()}|{answered["named"]?["id"]?.GetValue<string>()}|{answered["found"]?.GetValue<string>()}|{answered["r"]?["code"]?.GetValue<string>()}|{answered["ref"]?.GetValue<string>()}";

            if (expected != actual)
                differences.Add($"row {EdgeCorpus.WireId(row)}: expected {expected}, answered {actual}");
        }

        differences.Should().BeEmpty();
    }

    /// <summary>A condition on the element of an unwound collection, alone and beside one on the parent: the rows are the elements that meet it, each with its position.</summary>
    [Fact]
    public async Task A_condition_on_the_element_of_an_unwound_collection_keeps_the_elements_that_meet_it()
    {
        var client = await EdgeCorpus.ClientAsync();
        var differences = new List<string>();
        EdgeCondition[] onElement =
        [
            new Leaf("items.code", "eq", "a-1"), new Leaf("items.code", "eq", null), new Leaf("items.code", "neq", "A-1"), new Leaf("items.code", "in", new JsonArray("B-2", null)), new Leaf("items.code", "nin", new JsonArray("B-2", null)),
            new Leaf("items.code", "startsWith", "c"), new Leaf("items.code", "exists", false), new Leaf("items.code", "contains", ".", CaseSensitive: true), new Leaf("items.quantity", "gte", 2), new Leaf("items.quantity", "neq", 0),
            new Leaf("items.parts.sku", "eq", "SKU-1"), new Leaf("items.parts.sku", "eq", null), new Leaf("items.parts.weight", "gt", 0), new Leaf("items.parts.sku", "exists", false),
            new Not(new Leaf("items.code", "startsWith", "a")), new Or([new Leaf("items.quantity", "lt", 1), new Leaf("optionalCount", "eq", 3)]), new And([new Leaf("items.quantity", "gt", 0), new Leaf("note", "neq", "note-1")]),
            new Or([new Leaf("items.code", "eq", null), new Not(new Leaf("items.parts.sku", "in", new JsonArray("SKU-0", "SKU-2")))]),
        ];

        foreach (var preserve in new[] { false, true })
        {
            foreach (var condition in onElement)
            {
                // Each element as its own row, as the unwind writes it: the element where the collection stood.
                var expected = new List<string>();

                foreach (var row in EdgeCorpus.Stored().OrderBy(EdgeCorpus.WireId, StringComparer.Ordinal))
                {
                    var elements = row.TryGetValue("Items", out var items) && items is BsonArray array ? array.ToList() : [];

                    if (elements.Count == 0 && preserve)
                    {
                        var kept = (BsonDocument)row.DeepClone();

                        kept.Remove("Items");

                        if (condition.Holds(kept))
                            expected.Add($"{EdgeCorpus.WireId(row)}@");
                    }

                    for (var position = 0; position < elements.Count; position++)
                    {
                        var unwound = (BsonDocument)row.DeepClone();

                        unwound["Items"] = elements[position];

                        if (condition.Holds(unwound))
                            expected.Add($"{EdgeCorpus.WireId(row)}@{position}");
                    }
                }

                var match = condition.ToString().Replace("\"items.", "\"item.", StringComparison.Ordinal);
                var query = new Walks.Query(Entity, $$"""[{ "unwind": { "path": "items", "as": "item", "includeIndex": "position"{{(preserve ? """, "preserveNull": true""" : "")}} } }, { "match": {{match}} }, { "project": { "id": 1, "position": 1 } }]""");
                var answer = await client.QueryAsync(query.Body(new JsonObject { ["limit"] = 500, ["count"] = true }));

                if (!answer.IsPage)
                {
                    differences.Add($"{match} (preserveNull {preserve}): {answer.StatusCode} {Walks.Short(answer.Text, 200)}");
                    continue;
                }

                var rows = answer.Items.Select(row => $"{row!["id"]!.GetValue<string>()}@{row["position"]?.ToJsonString()}").ToList();

                if (!rows.SequenceEqual(expected))
                    differences.Add($"{match} (preserveNull {preserve}): expected {expected.Count} rows, answered {rows.Count}; not answered [{string.Join(" ", expected.Except(rows).Take(5))}], not expected [{string.Join(" ", rows.Except(expected).Take(5))}]");
                else if (answer.CountState != "exact" || answer.TotalCount != expected.Count)
                    differences.Add($"{match} (preserveNull {preserve}): counts {answer.Count?.ToJsonString()}, the rows are {expected.Count}");
            }
        }

        differences.Should().BeEmpty();
    }

    /// <summary>A request that ends at its own time ceiling, again and again beside others, leaves nothing behind: the requests around it are answered as they are alone.</summary>
    [Fact]
    public async Task A_request_cut_short_by_its_time_leaves_the_requests_beside_and_after_it_as_they_were()
    {
        var client = await EdgeCorpus.ClientAsync();
        string[] steady =
        [
            """[{ "resolve": { "path": "employeeId", "as": "e" } }, { "sort": [{ "id": "asc" }] }, { "project": { "id": 1, "e.matchCode": 1 } }, { "page": { "limit": 200 } }]""",
            """[{ "group": { "by": [{ "path": "note", "as": "k" }], "fields": { "n": { "count": true } } } }, { "sort": [{ "n": "desc" }] }, { "page": { "limit": 50 } }]""",
            """[{ "values": { "path": "items.code", "counts": true } }]""",
        ];
        var before = new List<string>();

        foreach (var stages in steady)
            before.Add((await client.SendAsync(Entity, stages)).ShouldBeOk().Body!["items"]!.ToJsonString().ToLowerInvariant());

        var cut = $$"""{ "entityType": "{{Entity}}", "maxTimeMs": 1, "pipeline": [{ "resolve": { "path": "employeeId", "as": "e" } }, { "match": { "e.matchCode": { "contains": "e" } } }, { "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids" } }, { "unwind": { "path": "items", "as": "item" } }, { "group": { "by": [{ "path": "item.code", "as": "k" }], "fields": { "all": { "push": "name" } } } }, { "page": { "limit": 5, "count": true } }] }""";

        var answers = await Task.WhenAll(Enumerable.Range(0, 45).Select(async turn =>
        {
            if (turn % 3 == 0)
            {
                var hurried = await client.PostAsync("OxQL/query", cut);

                // By the clock it is answered or it ends at its ceiling, here or at the owner it sent the rest of its time
                // to, which then refuses the owner query under the same code; it is never a fault.
                var timedOut = hurried.ErrorCodes.SequenceEqual(["QUERY_TIMEOUT"]) || hurried.ErrorCodes.SequenceEqual(["RESOLVE_REFUSED", "QUERY_TIMEOUT"]);

                return (Index: -1, Items: hurried.IsPage || timedOut || hurried.ErrorCodes.Contains("RESOLVE_UNAVAILABLE") ? "" : hurried.ToString());
            }

            var index = turn % steady.Length;

            return (Index: index, Items: (await client.SendAsync(Entity, steady[index])).Body?["items"]?.ToJsonString().ToLowerInvariant() ?? "(no page)");
        }));

        foreach (var (index, items) in answers)
            items.Should().Be(index < 0 ? "" : before[index]);

        for (var index = 0; index < steady.Length; index++)
            (await client.SendAsync(Entity, steady[index])).ShouldBeOk().Body!["items"]!.ToJsonString().ToLowerInvariant().Should().Be(before[index]);
    }
}
