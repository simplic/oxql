using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Differential.EdgeCondition;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// Every operator on every kind of member against what the documentation says a row meets,
/// computed from the stored rows (<see cref="EdgeOracle"/>): members that are null, not stored,
/// under a collection that is absent, null, empty or filled, under two collections; operands that
/// are null, an empty set, a value no row holds, the same value in another case or with an accent,
/// text of pattern characters. And the same condition asked in the ways that have to agree: negated
/// twice, split by De Morgan, in two <c>match</c> stages or one, as the request's <c>filter</c>,
/// counted, grouped and listed by its values.
/// </summary>
[Trait("Category", "Integration")]
public class MatchOracleTests
{
    private const string Entity = EdgeCorpus.Entity;

    private static JsonNode? N(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        _ => throw new ArgumentException(value.GetType().Name),
    };

    private static JsonArray Set(params object?[] values) => new(values.Select(N).ToArray());

    private static string Marker(int n) => Ids.Of(Spaces.ConformanceEntity, Org.A, n).ToString("D");

    /// <summary>The operands a member is compared with: values the rows hold, the same in another spelling, and one no row holds.</summary>
    private static object[] Operands(string member) => member switch
    {
        "name" => ["alpha", "ALPHA", "Älpha", "müller", "Mueller", "strasse", "Straße", "", "a.b*c", "a+b(c)", "Zulu [x]", "x\\y", "50% off", "under_score", "nobody"],
        "note" => ["note-1", "NOTE-1", "note-2", "", "zeta", "nobody"],
        "address.city" => ["köln", "Koln", "berlin", ""],
        "address.zip" or "optionalAddress.zip" => ["51", "Z1", "z3", "nobody"],
        "optionalAddress.city" => ["city 0", "City 2", "nobody"],
        "refCode" => ["REF-A", "ref-a", "REF-MISSING", "nobody"],
        "tags" => ["alpha", "ALPHA", "beta", "älpha", "gamma", "nobody"],
        "items.code" => ["A-1", "a-1", "Ä-1", "C.3", "D*4", "nobody"],
        "items.parts.sku" => ["SKU-1", "sku-2", "SKU-9"],
        "count" or "optionalCount" or "address.floor" or "optionalAddress.floor" or "items.quantity" => [0, 1, 2, 3, "3", 7],
        "magnitude" or "optionalMagnitude" => [9007199254740993L, 9007199254740992L, "9007199254740991", -9007199254740993L, 0, 42, long.MaxValue, long.MinValue, 5],
        "amount" or "optionalAmount" or "items.parts.weight" => [0, 1234.5m, "1234.50", 99.99m, -5, 0.1m, 79228162514264337593543950335m, 3],
        "ratio" or "optionalRatio" => [0, 0.25, -1.5, 1e300, -1e300, 0.1, 2.5, 3],
        "active" or "optionalActive" or "isDeleted" => [true, false, "true"],
        "marker" or "optionalMarker" => [Marker(0), Marker(1), Marker(3), Marker(5), Ids.Dangling(9).ToString("D")],
        "childId" => [EdgeCorpus.ChildIdOf(Org.A, 1).ToString("D"), EdgeCorpus.ChildIdOf(Org.A, 4).ToString("D"), Ids.Dangling(9).ToString("D")],
        "id" => [EdgeCorpus.IdOf(Org.A, 1).ToString("D"), EdgeCorpus.IdOf(Org.A, 90).ToString("D"), EdgeCorpus.IdOf(Org.B, 1).ToString("D"), Ids.Dangling(9).ToString("D")],
        "items.id" => [Ids.Of(Spaces.ConformanceItem, Org.A, 21).ToString("D"), Ids.Dangling(9).ToString("D")],
        "moment" or "optionalMoment" => ["0001-01-01T00:00:00Z", "9999-12-31T23:59:59.999Z", "2026-03-29T00:30:00Z", "2026-03-29T01:30:00+01:00", "2026-06-15T14:00:00+02:00", "1970-01-01T00:00:00Z", "1969-12-31T23:59:59.999Z", "2026-10-25T00:30:00.000Z", "2000-01-01T00:00:00Z"],
        "day" or "optionalDay" => ["0001-01-01", "9999-12-31", "2026-03-29", "2026-06-15", "2026-01-05", "1970-01-01", "2000-01-01"],
        "duration" or "optionalDuration" => ["PT0S", "PT1H30M", "PT10H", "PT1S", "PT23H59M59S", "PT5M"],
        "grade" or "optionalGrade" => ["A", "a", "B", "Z", "x"],
        "state" or "optionalState" => ["Draft", "Active", "Huge", 0, 1, 9007199254740993L],
        _ => throw new ArgumentException(member),
    };

    /// <summary>Parts of text a member's rows hold, for the three text operators.</summary>
    private static string[] Fragments(string member) => member switch
    {
        "name" => ["l", "ph", "ÜLL", "a.", "*", "(c)", "[x", "\\", "%", "_", "ZULU", "Stra", "sse", "e", "nobody"],
        "note" => ["note", "NÖTE", "-1", "1", "z", "nobody"],
        "address.city" => ["k", "LN", "ö", "ber"],
        "tags" => ["al", "TA", "ä", "mm"],
        "items.code" => ["a", "-1", ".", "*4", "Ä"],
        "items.parts.sku" => ["sku", "-1", "U-"],
        _ => ["a", "1", "Z"],
    };

    private static string[] Patterns(string member) => member switch
    {
        "name" => ["^A", "a$", "^.{5}$", "\\*", "ll", "^[a-z]+$", "^$", "\\[x\\]", "^Mü"],
        "items.code" => ["^[A-C]", "\\.3$", "1"],
        _ => ["^[A-Za-z]", "1$", "^$"],
    };

    private static IEnumerable<EdgeCondition> Conditions(string member)
    {
        var kind = EdgeOracle.Members[member];
        var operands = Operands(member);
        var ordered = kind is EdgeKind.Text or EdgeKind.Number or EdgeKind.Moment or EdgeKind.Day or EdgeKind.Span or EdgeKind.Letter or EdgeKind.State;
        var char2 = kind == EdgeKind.Letter;

        // What every member takes.
        yield return new Leaf(member, "eq", null);
        yield return new Leaf(member, "neq", null);
        yield return new Leaf(member, "exists", N(true));
        yield return new Leaf(member, "exists", N(false));
        yield return new Leaf(member, "in", Set());
        yield return new Leaf(member, "nin", Set());
        yield return new Leaf(member, "in", Set([null]));
        yield return new Leaf(member, "nin", Set([null]));
        yield return new Leaf(member, "in", Set(operands[0], null));
        yield return new Leaf(member, "nin", Set(operands[0], null));
        yield return new Leaf(member, "in", Set(operands[0], operands[^1], operands[operands.Length / 2]));
        yield return new Leaf(member, "nin", Set(operands[0], operands[^1], operands[operands.Length / 2]));

        foreach (var operand in operands)
        {
            yield return new Leaf(member, "eq", N(operand));
            yield return new Leaf(member, "neq", N(operand));

            // The strings of a flag are an operand of eq; nothing orders a flag or a guid.
            if (ordered)
                foreach (var op in new[] { "gt", "gte", "lt", "lte" })
                    if (kind != EdgeKind.Text || operand is string { Length: > 0 } text && text.All(char.IsAsciiLetterOrDigit))
                        yield return new Leaf(member, op, N(operand));

            if (kind is EdgeKind.Text || char2)
            {
                yield return new Leaf(member, "eq", N(operand), CaseSensitive: true);
                yield return new Leaf(member, "neq", N(operand), CaseSensitive: true);
                yield return new Leaf(member, "in", Set(operand, operands[0]), CaseSensitive: true);
                yield return new Leaf(member, "nin", Set(operand), CaseSensitive: false);
            }
        }

        if (kind != EdgeKind.Text)
            yield break;

        foreach (var fragment in Fragments(member))
            foreach (var op in new[] { "contains", "startsWith", "endsWith" })
            {
                yield return new Leaf(member, op, N(fragment));
                yield return new Leaf(member, op, N(fragment), CaseSensitive: true);
            }

        foreach (var pattern in Patterns(member))
            yield return new Leaf(member, "regex", N(pattern));
    }

    public static TheoryData<string> Members() => [.. EdgeOracle.Members.Keys];

    /// <summary>What the engine answers for a condition, as keys in key order: one page with a count beside it.</summary>
    private static async Task<(List<string>? Keys, string Said)> AnsweredAsync(LabClient client, JsonObject match, string? filter = null)
    {
        var body = new Walks.Query(Entity, $$"""[{ "match": {{match.ToJsonString()}} }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }]""", filter).Body(new JsonObject { ["limit"] = 500, ["count"] = true });
        var answer = await client.QueryAsync(body);

        if (!answer.IsPage)
            return (null, Walks.Short(answer.Text));

        var keys = answer.Ids().Select(id => id.ToString("D")).ToList();

        return answer.HasNextPage || answer.CountState != "exact" || answer.TotalCount != keys.Count
            ? (null, $"{keys.Count} rows, hasNextPage {answer.HasNextPage}, count {answer.Count?.ToJsonString()}")
            : (keys, "");
    }

    private static string Differs(IReadOnlyList<string> expected, IReadOnlyList<string> answered)
    {
        string Tail(string id) => id[^4..];

        return $"expected {expected.Count} rows, answered {answered.Count}; not answered: [{string.Join(",", expected.Except(answered).Select(Tail).Take(8))}], not expected: [{string.Join(",", answered.Except(expected).Select(Tail).Take(8))}]";
    }

    [Theory]
    [MemberData(nameof(Members))]
    public async Task O_every_operator_on_a_member_answers_the_rows_the_documentation_says(string member)
    {
        var client = await EdgeCorpus.ClientAsync();
        var differences = new List<string>();
        var asked = 0;
        var telling = 0;

        foreach (var condition in Conditions(member))
        {
            asked++;

            var expected = EdgeOracle.Matching(condition);

            // A condition that some rows meet and some do not tells a right answer from "all" and from "none".
            if (expected.Count is > 0 and < EdgeCorpus.Count)
                telling++;

            var (answered, said) = await AnsweredAsync(client, condition.Json());

            if (answered is null)
                differences.Add($"{condition}: {said}");
            else if (!answered.SequenceEqual(expected))
                differences.Add($"{condition}: {Differs(expected, answered)}");
        }

        telling.Should().BeGreaterThan(asked / 3, $"the conditions on '{member}' part the rows");
        differences.Should().BeEmpty($"{asked} conditions on '{member}'");
    }

    // ── conditions put together ───────────────────────────────────────────────────────────

    /// <summary>Conditions of every kind of member and of every way a row can hold nothing, to combine.</summary>
    internal static readonly EdgeCondition[] Pool =
    [
        new Leaf("name", "eq", N("alpha")),
        new Leaf("name", "startsWith", N("m")),
        new Leaf("note", "eq", null),
        new Leaf("note", "neq", N("note-1")),
        new Leaf("note", "contains", N("ote"), CaseSensitive: true),
        new Leaf("optionalCount", "gte", N(2)),
        new Leaf("optionalCount", "in", Set(0, null)),
        new Leaf("optionalMagnitude", "gt", N(9007199254740992L)),
        new Leaf("optionalAmount", "lte", N(99.99m)),
        new Leaf("optionalRatio", "neq", N(0.25)),
        new Leaf("optionalActive", "eq", N(true)),
        new Leaf("optionalActive", "exists", N(false)),
        new Leaf("optionalMoment", "gte", N("2026-03-29T00:30:00Z")),
        new Leaf("optionalDay", "lt", N("2026-01-05")),
        new Leaf("optionalDuration", "gte", N("PT1H30M")),
        new Leaf("optionalGrade", "eq", N("a")),
        new Leaf("optionalState", "in", Set("Active", "Huge")),
        new Leaf("optionalAddress.zip", "eq", null),
        new Leaf("optionalAddress.city", "exists", N(true)),
        new Leaf("tags", "eq", N("alpha")),
        new Leaf("tags", "nin", Set("beta", "gamma")),
        new Leaf("tags", "exists", N(false)),
        new Leaf("items.code", "eq", N("a-1")),
        new Leaf("items.code", "eq", null),
        new Leaf("items.code", "neq", N("B-2"), CaseSensitive: true),
        new Leaf("items.quantity", "gt", N(1)),
        new Leaf("items.id", "exists", N(false)),
        new Leaf("items.parts.sku", "eq", N("SKU-1")),
        new Leaf("items.parts.weight", "in", Set(0, null)),
        new Any("items", new And([new Leaf("code", "eq", N("A-1")), new Leaf("quantity", "gte", N(1))])),
        new Any("items", new Or([new Leaf("code", "eq", null), new Leaf("parts.sku", "eq", N("SKU-2"))])),
        new Any("items", new Not(new Leaf("quantity", "in", Set(0, 1)))),
        new Not(new Any("items", new Leaf("id", "exists", N(true)))),
        new Any("items", new And([new Leaf("quantity", "gt", N(0)), new Leaf("quantity", "lt", N(3)), new Leaf("code", "startsWith", N("a"))])),
    ];

    [Fact]
    public async Task P_every_condition_of_the_pool_and_its_negations_answer_what_the_documentation_says()
    {
        var client = await EdgeCorpus.ClientAsync();
        var differences = new List<string>();
        var all = EdgeCorpus.Stored().Select(EdgeCorpus.WireId).Order(StringComparer.Ordinal).ToList();

        foreach (var condition in Pool)
        {
            var expected = EdgeOracle.Matching(condition);

            foreach (var asked in new EdgeCondition[] { condition, new Not(new Not(condition)), new And([condition, condition]), new Or([condition, new Leaf("id", "in", Set())]) })
            {
                var (answered, said) = await AnsweredAsync(client, asked.Json());

                if (answered is null || !answered.SequenceEqual(expected))
                    differences.Add($"{asked}: {(answered is null ? said : Differs(expected, answered))}");
            }

            // A condition and its negation part the rows: every row meets exactly one.
            var (negated, why) = await AnsweredAsync(client, new Not(condition).Json());

            if (negated is null || !negated.SequenceEqual(all.Except(expected)))
                differences.Add($"not {condition}: {(negated is null ? why : Differs([.. all.Except(expected)], negated))}");
        }

        differences.Should().BeEmpty();
    }

    /// <summary>
    /// Two conditions in every way a request can say "both", "either" and "not both": the answers
    /// are the oracle's, and so each is the other's.
    /// </summary>
    [Fact]
    public async Task Q_two_conditions_combine_as_logic_says_however_the_request_writes_them()
    {
        var client = await EdgeCorpus.ClientAsync();
        var differences = new List<string>();
        var pairs = 0;

        for (var first = 0; first < Pool.Length; first++)
        {
            // Each condition with three others, spread over the pool.
            foreach (var step in new[] { 1, 7, 13 })
            {
                var (a, b) = (Pool[first], Pool[(first + step) % Pool.Length]);
                var both = EdgeOracle.Matching(new And([a, b]));
                var either = EdgeOracle.Matching(new Or([a, b]));
                var notBoth = EdgeOracle.Matching(new Not(new And([a, b])));
                var neither = EdgeOracle.Matching(new Not(new Or([a, b])));

                pairs++;

                async Task Ask(string how, IReadOnlyList<string> expected, EdgeCondition condition, string? filter = null, string? secondMatch = null)
                {
                    List<string>? answered;
                    string said;

                    if (secondMatch is null)
                    {
                        (answered, said) = await AnsweredAsync(client, condition.Json(), filter);
                    }
                    else
                    {
                        var answer = await client.SendAsync(Entity, $$"""[{ "match": {{condition}} }, { "match": {{secondMatch}} }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""");

                        (answered, said) = answer.IsPage ? (answer.Ids().Select(id => id.ToString("D")).ToList(), "") : (null, Walks.Short(answer.Text));
                    }

                    if (answered is null || !answered.SequenceEqual(expected))
                        differences.Add($"{how} of {a} and {b}: {(answered is null ? said : Differs(expected, answered))}");
                }

                await Ask("and", both, new And([a, b]));
                await Ask("two match stages", both, a, secondMatch: b.ToString());
                await Ask("a match and a filter entry", both, a, filter: $$"""[{ "id": "second", "where": {{b}} }]""");
                await Ask("two filter entries", both, new Leaf("id", "nin", Set()), filter: $$"""[{ "where": {{a}} }, { "id": "second", "where": {{b}} }]""");
                await Ask("or", either, new Or([a, b]));
                await Ask("not and", notBoth, new Not(new And([a, b])));
                await Ask("or of the negations", notBoth, new Or([new Not(a), new Not(b)]));
                await Ask("not or", neither, new Not(new Or([a, b])));
                await Ask("and of the negations", neither, new And([new Not(a), new Not(b)]));
                await Ask("two negated match stages", neither, new Not(a), secondMatch: new Not(b).ToString());
                await Ask("exclusive or", [.. either.Except(both)], new And([new Or([a, b]), new Not(new And([a, b]))]));
            }
        }

        pairs.Should().BeGreaterThan(90);
        differences.Should().BeEmpty();
    }

    /// <summary>The count alone, the count beside a page and the rows of a walk are one number for a condition.</summary>
    [Fact]
    public async Task S_the_count_of_a_condition_is_the_rows_a_walk_serves()
    {
        var client = await EdgeCorpus.ClientAsync();
        var differences = new List<string>();

        foreach (var condition in Pool)
        {
            var expected = EdgeOracle.Matching(condition).Count;
            var query = new Walks.Query(Entity, $$"""[{ "match": {{condition}} }, { "project": { "id": 1 } }]""");
            var counted = await Walks.CountAsync(client, query);

            if (!counted.IsPage || counted.CountState != "exact" || counted.TotalCount != expected)
                differences.Add($"{condition}: the count alone says {(counted.IsPage ? counted.Count?.ToJsonString() : Walks.Short(counted.Text))}, the rows are {expected}");

            var walked = (await Walks.PagesAsync(client, query, limit: 11, count: true)).ToList();

            if (walked.Sum(page => page.Items.Count) != expected)
                differences.Add($"{condition}: a walk serves {walked.Sum(page => page.Items.Count)} rows, the rows are {expected}");

            if (walked.Any(page => page.CountState == "exact" && page.TotalCount != expected) || walked[^1].CountState != "exact")
                differences.Add($"{condition}: a page counts {string.Join(" ", walked.Select(page => page.Count?.ToJsonString()))}, the rows are {expected}");
        }

        differences.Should().BeEmpty();
    }
}
