using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Conformance;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.KindsConformance;

/// <summary>
/// The kinds battery: <c>long</c>, <c>date</c>, <c>binary</c>, <c>char</c> and a
/// long-backed <c>enum</c>, declared on <c>conformance.entity</c> with a nullable twin each, run
/// through one case list. Every expected id list, order, bucket and aggregate is computed from the
/// stored corpus (exact integers for the long and the enum, calendar days for the date, BSON
/// binary order, code points for the char) before the request.
/// <para>
/// The legacy case id is "&lt;kind&gt; &lt;n&gt;" (<c>long 1a</c>); a theory's first argument
/// carries it. The client halves (decoding through <c>run()</c>, <c>validate()</c> agreement, the
/// K-6 client refusal) went to the Angular specs.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class KindsConformanceTests
{
    // ── the five kinds ──────────────────────────────────────────────────────────────────────

    /// <summary>One kind: its members, how a stored value compares, travels on the wire and is written as an operand.</summary>
    public sealed record Kind(
        string Name,
        string Member,
        string Twin,
        bool Ordered,
        Func<BsonValue, BsonValue, int> Compare,
        Func<BsonValue, string> Wire,
        Func<BsonValue, string> Operand,
        Func<BsonValue, string> LegToken);

    private static string Quote(string text) => JsonValue.Create(text).ToJsonString();

    private static string Day(BsonValue value) => value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int Bytes(BsonValue a, BsonValue b)
    {
        var left = a.AsByteArray;
        var right = b.AsByteArray;

        if (left.Length != right.Length)
            return left.Length.CompareTo(right.Length);

        for (var index = 0; index < left.Length; index++)
            if (left[index] != right[index])
                return left[index].CompareTo(right[index]);

        return 0;
    }

    private const long MaxSafe = (1L << 53) - 1;

    public static readonly IReadOnlyDictionary<string, Kind> Kinds = new Dictionary<string, Kind>
    {
        ["long"] = new("long", "magnitude", "optionalMagnitude", true,
            (a, b) => a.ToInt64().CompareTo(b.ToInt64()),
            value => Quote(value.ToInt64().ToString(CultureInfo.InvariantCulture)),
            value => Quote(value.ToInt64().ToString(CultureInfo.InvariantCulture)),
            value => value.ToInt64().ToString(CultureInfo.InvariantCulture)),
        ["date"] = new("date", "day", "optionalDay", true,
            (a, b) => a.ToUniversalTime().CompareTo(b.ToUniversalTime()),
            value => Quote(Day(value)),
            value => Quote(Day(value)),
            value => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
        ["binary"] = new("binary", "payload", "optionalPayload", false,
            Bytes,
            value => Quote(Convert.ToBase64String(value.AsByteArray)),
            value => Quote(Convert.ToBase64String(value.AsByteArray)),
            value => Convert.ToBase64String(value.AsByteArray)),
        ["char"] = new("char", "grade", "optionalGrade", true,
            (a, b) => a.AsInt32.CompareTo(b.AsInt32),
            value => Quote(((char)value.AsInt32).ToString()),
            value => Quote(((char)value.AsInt32).ToString()),
            value => value.AsInt32.ToString(CultureInfo.InvariantCulture)),
        ["enum"] = new("enum", "state", "optionalState", true,
            (a, b) => a.ToInt64().CompareTo(b.ToInt64()),
            value => Math.Abs(value.ToInt64()) <= MaxSafe ? value.ToInt64().ToString(CultureInfo.InvariantCulture) : Quote(value.ToInt64().ToString(CultureInfo.InvariantCulture)),
            value => Quote(Enum.GetName(typeof(ConformanceState), value.ToInt64())!),
            value => value.ToInt64().ToString(CultureInfo.InvariantCulture)),
    };

    public static TheoryData<string, string> Case(string number, Func<Kind, bool>? applies = null)
    {
        var data = new TheoryData<string, string>();

        foreach (var kind in Kinds.Values.Where(kind => applies?.Invoke(kind) ?? true))
            data.Add($"{kind.Name} {number}", kind.Name);

        return data;
    }

    public static TheoryData<string, string> All(string number) => Case(number);

    public static TheoryData<string, string> Ordered(string number) => Case(number, kind => kind.Ordered);

    public static TheoryData<string, string> NotLong(string number) => Case(number, kind => kind.Name != "long");

    /// <summary>The kinds that are neither numeric nor temporal: binary, char, enum.</summary>
    public static TheoryData<string, string> Neither(string number) => Case(number, kind => kind.Name is not ("long" or "date"));

    /// <summary>The push case: <c>long 7f</c>, and <c>7d</c> for the other kinds.</summary>
    public static TheoryData<string, string> Push()
    {
        var data = new TheoryData<string, string>();

        foreach (var kind in Kinds.Values)
            data.Add($"{kind.Name} {(kind.Name == "long" ? "7f" : "7d")}", kind.Name);

        return data;
    }

    // ── the corpus, read as stored ──────────────────────────────────────────────────────────

    private static IReadOnlyList<CorpusRow> Mine => Corpus.Sorted(Corpus.Conformance, [("id", false)]);

    private static CorpusRow Foreign => Corpus.Rows(Corpus.Conformance, Org.B).Single();

    private static string NameOf(CorpusRow row) => Corpus.Text(row, "name")!;

    /// <summary>The stored value of the member (or the twin), or null when it is stored as null.</summary>
    private static BsonValue? At(CorpusRow row, string path) => Corpus.ValueAt(row, path) is { IsBsonNull: false } value ? value : null;

    private static bool Same(Kind kind, BsonValue? stored, BsonValue? operand) =>
        stored is null || operand is null ? stored is null && operand is null : kind.Compare(stored, operand) == 0;

    private static IReadOnlyList<string> Names(Func<CorpusRow, bool> keep) => Mine.Where(keep).Select(NameOf).ToList();

    /// <summary>The rows in the order a sort on <paramref name="path"/> promises: nulls first ascending, then the id tie-breaker.</summary>
    private static IReadOnlyList<CorpusRow> Sorted(Kind kind, string path, bool descending) =>
        Mine.Order(Comparer<CorpusRow>.Create((left, right) =>
        {
            var a = At(left, path);
            var b = At(right, path);
            var order = a is null || b is null ? (a is null ? (b is null ? 0 : -1) : 1) : kind.Compare(a, b);

            return order != 0 ? (descending ? -order : order) : Corpus.CompareIds(left, right);
        })).ToList();

    // ── the wire ────────────────────────────────────────────────────────────────────────────

    private static async Task<WireAnswer> Send(string pipeline, string? variables = null, Org org = Org.A) =>
        await (await Lab.ClientAsync(LabService.Conformance, org)).SendAsync(Corpus.Conformance, pipeline, variables);

    private static string Matching(string condition) =>
        $$"""[{ "match": {{condition}} }, { "project": { "id": 1, "name": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""";

    private static async Task<IReadOnlyList<string>> NamesFor(string condition, string? variables = null, Org org = Org.A)
    {
        var answer = await Send(Matching(condition), variables, org);

        answer.ShouldBeOk(condition);

        return answer.Strings("name").Select(value => value!).ToList();
    }

    private static async Task ShouldFind(string condition, IReadOnlyList<string> expected, string? variables = null)
    {
        var names = await NamesFor(condition, variables);

        names.Should().NotContain(NameOf(Foreign), $"{condition}: a row of another organisation came back");
        names.Should().Equal(expected, condition);
    }

    private static async Task ShouldRefuse(string condition, string code, string? variables = null)
    {
        var answer = await Send(Matching(condition), variables);

        answer.StatusCode.Should().Be(400, $"{condition}: a refusal is a coded 400, never a 500: {answer}");
        answer.ErrorCodes.Should().Equal([code], condition);
    }

    // ── K0 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task K0_the_conformance_host_holds_exactly_the_corpus_rows_of_organisation_A()
    {
        var answer = await Send("""[{ "project": { "id": 1, "name": 1, "organizationId": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10, "includeTotalCount": true } }]""");

        answer.ShouldHaveIds(Mine.Select(row => row.Id)).ShouldHaveTotal(Mine.Count);
        answer.Strings("name").Should().Equal(Mine.Select(NameOf));
        answer.Strings("organizationId").Should().AllBe(Org.A.Id().ToString("D"));
        Mine.Should().HaveCount(3);
        Mine.Should().Contain(row => At(row, "optionalMagnitude") == null, "the sparse row holds every twin as null");
    }

    // ── 1 · encoding ────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(All), "1a")]
    public async Task K1a_every_row_carries_the_value_in_its_wire_form_in_the_body_text_and_the_null_twin_as_null_not_absent(string id, string name)
    {
        var kind = Kinds[name];
        var answer = await Send($$"""[{ "project": { "id": 1, "name": 1, "{{kind.Member}}": 1, "{{kind.Twin}}": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""");

        answer.ShouldHaveIds(Mine.Select(row => row.Id), id);

        foreach (var (row, item) in Mine.Zip(answer.Items.Select(item => item!.AsObject())))
            foreach (var path in new[] { kind.Member, kind.Twin })
            {
                var stored = At(row, path);

                item.ContainsKey(path).Should().BeTrue($"{id}: {NameOf(row)}.{path} is present [G17]");
                // The member's own text: a bare 9007199254740993 must not pass for "9007199254740993".
                (item[path]?.ToJsonString() ?? "null").Should().Be(stored is null ? "null" : kind.Wire(stored), $"{id}: {NameOf(row)}.{path}");
            }
    }

    // ── 2 · round trip ──────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(All), "2")]
    public async Task K2_a_value_read_from_a_row_and_written_back_unchanged_finds_exactly_its_own_row(string id, string name)
    {
        var kind = Kinds[name];
        var read = await Send($$"""[{ "project": { "id": 1, "name": 1, "{{kind.Member}}": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 10 } }]""");
        read.ShouldHaveIds(Mine.Select(row => row.Id), id);

        foreach (var item in read.Items)
        {
            var asRead = item![kind.Member]!.ToJsonString();
            var own = item["name"]!.GetValue<string>();
            var expected = Names(row => Same(kind, At(row, kind.Member), At(Mine.Single(candidate => NameOf(candidate) == own), kind.Member)));

            expected.Should().Equal([own], "the corpus values are distinct per row");
            await ShouldFind($$"""{ "{{kind.Member}}": { "eq": {{asRead}} } }""", expected);
            await ShouldFind($$"""{ "{{kind.Member}}": { "in": [{{asRead}}] } }""", expected);
        }
    }

    // ── 3 · operators ───────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(All), "3a")]
    public async Task K3a_eq_neq_in_nin_and_exists_on_the_member_and_on_the_nullable_twin(string id, string name)
    {
        var kind = Kinds[name];
        var a = At(Mine[0], kind.Member)!;
        var b = At(Mine[1], kind.Member)!;

        foreach (var path in new[] { kind.Member, kind.Twin })
        {
            await ShouldFind($$"""{ "{{path}}": { "eq": {{kind.Operand(a)}} } }""", Names(row => Same(kind, At(row, path), a)));
            await ShouldFind($$"""{ "{{path}}": { "neq": {{kind.Operand(a)}} } }""", Names(row => !Same(kind, At(row, path), a)));
            await ShouldFind($$"""{ "{{path}}": { "in": [{{kind.Operand(a)}}, {{kind.Operand(b)}}] } }""", Names(row => Same(kind, At(row, path), a) || Same(kind, At(row, path), b)));
            await ShouldFind($$"""{ "{{path}}": { "nin": [{{kind.Operand(a)}}] } }""", Names(row => !Same(kind, At(row, path), a)));
            // Every member is written, as null when it has no value, so it always exists.
            await ShouldFind($$"""{ "{{path}}": { "exists": true } }""", Names(row => Corpus.ValueAt(row, path) is not null));
            await ShouldFind($$"""{ "{{path}}": { "exists": false } }""", Names(row => Corpus.ValueAt(row, path) is null));
        }

        id.Should().StartWith(name);
    }

    [Theory]
    [MemberData(nameof(Ordered), "3b")]
    public async Task K3b_gt_gte_lt_lte_and_the_between_form_with_every_stored_value_as_the_bound(string id, string name)
    {
        var kind = Kinds[name];

        foreach (var path in new[] { kind.Member, kind.Twin })
        {
            foreach (var bound in Mine.Select(row => At(row, kind.Member)!))
                foreach (var (op, holds) in new (string, Func<int, bool>)[] { ("gt", c => c > 0), ("gte", c => c >= 0), ("lt", c => c < 0), ("lte", c => c <= 0) })
                    await ShouldFind($$"""{ "{{path}}": { "{{op}}": {{kind.Operand(bound)}} } }""", Names(row => At(row, path) is { } stored && holds(kind.Compare(stored, bound))));

            // The client's between is [low, high): gte and lt under one and.
            var byValue = Sorted(kind, kind.Member, descending: false).Select(row => At(row, kind.Member)!).ToList();
            var (low, high) = (byValue[0], byValue[^1]);
            await ShouldFind($$"""{ "and": [{ "{{path}}": { "gte": {{kind.Operand(low)}} } }, { "{{path}}": { "lt": {{kind.Operand(high)}} } }] }""",
                Names(row => At(row, path) is { } stored && kind.Compare(stored, low) >= 0 && kind.Compare(stored, high) < 0));
        }

        id.Should().StartWith(name);
    }

    [Fact]
    public async Task K3b_binary_gt_gte_lt_lte_on_a_binary_are_refused_each_naming_the_operator()
    {
        foreach (var op in new[] { "gt", "gte", "lt", "lte" })
        {
            var answer = await Send(Matching($$"""{ "payload": { "{{op}}": "AQIDBA==" } }"""));

            answer.StatusCode.Should().Be(400, answer.ToString());
            answer.ErrorCodes.Should().Equal(["INVALID_OPERAND"]);
            answer.Errors[0]["message"]!.GetValue<string>().Should().Contain($"'{op}'");
        }
    }

    [Theory]
    [MemberData(nameof(All), "3c")]
    public async Task K3c_contains_startsWith_endsWith_and_regex_are_a_coded_refusal_naming_the_operator(string id, string name)
    {
        var kind = Kinds[name];

        foreach (var op in new[] { "contains", "startsWith", "endsWith", "regex" })
        {
            var answer = await Send(Matching($$"""{ "{{kind.Member}}": { "{{op}}": "{{(name == "char" ? "A" : "2")}}" } }"""));

            answer.StatusCode.Should().Be(400, $"{id} {op} must be a 400 envelope, never a 500: {answer}");
            answer.Type.Should().Be("validation_error");
            answer.ErrorCodes.Should().Equal(["INVALID_OPERAND"], op);
            answer.Errors[0]["message"]!.GetValue<string>().Should().Contain($"'{op}'");
        }
    }

    [Theory]
    [MemberData(nameof(All), "3d")]
    public async Task K3d_ignoreCase_is_refused_where_the_kind_has_no_case_and_on_a_char_folds_or_is_refused_never_silently_ignored(string id, string name)
    {
        var kind = Kinds[name];

        if (name != "char")
        {
            await ShouldRefuse($$"""{ "{{kind.Member}}": { "eq": {{kind.Operand(At(Mine[0], kind.Member)!)}}, "options": { "ignoreCase": true } } }""", "OPTION_NOT_APPLICABLE");
            return;
        }

        // K-4: eq 'a' with ignoreCase either finds Alpha ('A') or is refused; 200 with no rows is the defect.
        var folded = Names(row => char.ToUpperInvariant((char)At(row, kind.Member)!.AsInt32) == 'A');
        folded.Should().ContainSingle();
        var answer = await Send(Matching("""{ "grade": { "eq": "a", "options": { "ignoreCase": true } } }"""));

        if (answer.StatusCode == 200)
            answer.Strings("name").Should().Equal(folded, id);
        else
            answer.ErrorCodes.Should().Equal(["OPTION_NOT_APPLICABLE"], id);
    }

    // ── 4 · null semantics ──────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(All), "4")]
    public async Task K4_null_on_the_twin_eq_null_neq_null_exists_in_and_nin_with_null_and_gt_null_refused(string id, string name)
    {
        var kind = Kinds[name];
        var bravo = At(Mine[1], kind.Member)!;
        var twin = kind.Twin;
        var nulls = Names(row => At(row, twin) is null);
        nulls.Should().ContainSingle($"{id}: the sparse row");

        await ShouldFind($$"""{ "{{twin}}": { "eq": null } }""", nulls);
        await ShouldFind($$"""{ "{{twin}}": { "neq": null } }""", Names(row => At(row, twin) is not null));
        await ShouldFind($$"""{ "{{twin}}": { "exists": false } }""", []);
        await ShouldFind($$"""{ "{{twin}}": { "exists": true } }""", Names(_ => true));
        await ShouldFind($$"""{ "{{twin}}": { "in": [{{kind.Operand(bravo)}}, null] } }""", Names(row => At(row, twin) is null || Same(kind, At(row, twin), bravo)));
        await ShouldFind($$"""{ "{{twin}}": { "nin": [{{kind.Operand(bravo)}}, null] } }""", Names(row => At(row, twin) is not null && !Same(kind, At(row, twin), bravo)));
        await ShouldFind($$"""{ "{{twin}}": { "neq": {{kind.Operand(bravo)}} } }""", Names(row => !Same(kind, At(row, twin), bravo)));
        await ShouldFind($$"""{ "{{kind.Member}}": { "eq": null } }""", []);
        await ShouldRefuse($$"""{ "{{twin}}": { "gt": null } }""", "INVALID_OPERAND");
    }

    // ── 5 · operand validation ──────────────────────────────────────────────────────────────

    /// <summary>One operand, written as JSON text, and what it must get: the rows holding <see cref="Value"/>, or <see cref="Code"/>.</summary>
    public sealed record Operand(string Label, string Json, BsonValue? Value = null, string? Code = null, bool FoldCase = false);

    private static Operand Holds(string label, string json, BsonValue value, bool foldCase = false) => new(label, json, value, FoldCase: foldCase);

    private static Operand Refused(string label, string json, string code) => new(label, json, Code: code);

    private static BsonValue DayValue(string day) => new BsonDateTime(DateTime.SpecifyKind(DateTime.Parse(day, CultureInfo.InvariantCulture), DateTimeKind.Utc));

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<Operand>> Operands = new Dictionary<string, IReadOnlyList<Operand>>
    {
        ["long"] =
        [
            Holds("[F3] a JSON number below 2^53", "42", 42L),
            Holds("[F3] a string of digits", "\"42\"", 42L),
            Holds("[F10] a string above 2^53", "\"9007199254740993\"", 9007199254740993L),
            Holds("a bare JSON number above 2^53, exact digits in the body", "9007199254740993", 9007199254740993L),
            Holds("the same number as a JavaScript client sends it after rounding: …992, which no row holds", "9007199254740992", 9007199254740992L),
            Holds("one above, 2^53 + 2, must not collapse onto the stored value", "\"9007199254740994\"", 9007199254740994L),
            Holds("one below, 2^53, must not collapse onto the stored value", "\"9007199254740992\"", 9007199254740992L),
            Refused("\"12.5\"", "\"12.5\"", "INVALID_OPERAND"),
            Refused("12.5", "12.5", "INVALID_OPERAND"),
            Refused("the empty string", "\"\"", "INVALID_OPERAND"),
            Refused("a leading blank", "\" 42\"", "INVALID_OPERAND"),
            Refused("hexadecimal", "\"0x2A\"", "INVALID_OPERAND"),
            Refused("an exponent", "\"4.2e1\"", "INVALID_OPERAND"),
            Refused("above Int64", "\"9223372036854775808\"", "INVALID_OPERAND"),
            Refused("true", "true", "INVALID_OPERAND"),
            Holds("pins: \"+42\" is read as 42", "\"+42\"", 42L),
            Holds("pins: \"042\" is read as 42", "\"042\"", 42L),
        ],
        ["date"] =
        [
            Holds("[F29] YYYY-MM-DD", "\"2026-03-05\"", DayValue("2026-03-05")),
            Refused("[F30] an instant", "\"2026-03-05T00:00:00Z\"", "INVALID_OPERAND"),
            Refused("no such day", "\"2026-02-30\"", "INVALID_OPERAND"),
            Refused("unpadded", "\"2026-3-5\"", "INVALID_OPERAND"),
            Refused("a zone suffix", "\"2026-03-05Z\"", "INVALID_OPERAND"),
            Refused("slashes", "\"2026/03/05\"", "INVALID_OPERAND"),
            Refused("a number", "20260305", "INVALID_OPERAND"),
            Refused("the empty string", "\"\"", "INVALID_OPERAND"),
            Holds("a leap day that exists binds", "\"2024-02-29\"", DayValue("2024-02-29")),
            Holds("0001-01-01 binds", "\"0001-01-01\"", DayValue("0001-01-01")),
            Holds("9999-12-31 binds", "\"9999-12-31\"", DayValue("9999-12-31")),
        ],
        ["binary"] =
        [
            Holds("[F56][AB11] base64", "\"CQk=\"", new BsonBinaryData([9, 9])),
            Holds("the empty string is the empty byte array", "\"\"", new BsonBinaryData([])),
            Refused("[F56] not base64", "\"not base64!\"", "INVALID_OPERAND"),
            Refused("unpadded", "\"CQk\"", "INVALID_OPERAND"),
            Refused("the url-safe alphabet", "\"AQID-_==\"", "INVALID_OPERAND"),
            Refused("a number", "9", "INVALID_OPERAND"),
            Holds("pins: whitespace inside base64 is tolerated", "\"AQID BA==\"", new BsonBinaryData([1, 2, 3, 4])),
        ],
        ["char"] =
        [
            Holds("[F57] one character", "\"B\"", (int)'B'),
            Refused("the code point as a number is not the wire form", "66", "INVALID_OPERAND"),
            Holds("contract 2 folds the case of a char by default", "\"b\"", (int)'B', foldCase: true),
            Holds("pins: two characters bind and match nothing", "\"AB\"", -1),
            Holds("pins: the empty string binds and matches nothing", "\"\"", -1),
            Holds("a BMP character outside the corpus", "\"é\"", (int)'é'),
            Holds("an astral character (two UTF-16 units)", "\"😀\"", -1),
        ],
        ["enum"] =
        [
            Holds("[F43] a member name", "\"Huge\"", (long)ConformanceState.Huge),
            Holds("[F44] a declared number", "1", 1L),
            Holds("[F44] a declared number as digits", "\"1\"", 1L),
            Holds("[F49] 9007199254740993 as a string", "\"9007199254740993\"", (long)ConformanceState.Huge),
            Holds("[F49] 9007199254740993 as a bare number, exact digits in the body", "9007199254740993", (long)ConformanceState.Huge),
            Refused("…992, the JavaScript-rounded number, is no member", "9007199254740992", "UNKNOWN_ENUM_MEMBER"),
#pragma warning disable CS0618 // the retired member is the subject
            Holds("[F52] a retired member binds", "\"Legacy\"", (long)ConformanceState.Legacy),
#pragma warning restore CS0618
            Refused("[F45] an unknown name", "\"Nope\"", "UNKNOWN_ENUM_MEMBER"),
            Refused("[F47] an undeclared number", "7", "UNKNOWN_ENUM_MEMBER"),
            Refused("a name in the wrong case", "\"active\"", "UNKNOWN_ENUM_MEMBER"),
            Refused("1.5", "1.5", "INVALID_OPERAND"),
            Refused("true", "true", "INVALID_OPERAND"),
        ],
    };

    [Theory]
    [MemberData(nameof(All), "5")]
    public async Task K5_operand_validation_each_operand_answers_the_computed_rows_or_its_named_refusal(string id, string name)
    {
        var kind = Kinds[name];
        var problems = new List<string>();

        foreach (var operand in Operands[name])
        {
            var answer = await Send(Matching($$"""{ "{{kind.Member}}": { "eq": {{operand.Json}} } }"""));

            if (answer.StatusCode >= 500)
                problems.Add($"{operand.Label}: HTTP {answer.StatusCode}, a refusal must be a coded envelope");

            if (operand.Code is { } code)
            {
                if (answer.StatusCode != 400 || !answer.ErrorCodes.SequenceEqual([code]))
                    problems.Add($"{operand.Label}: expected {code}, got {answer}");

                continue;
            }

            var expected = Names(row => At(row, kind.Member) is { } stored && (operand.FoldCase
                ? char.ToUpperInvariant((char)stored.AsInt32) == char.ToUpperInvariant((char)operand.Value!.AsInt32)
                : kind.Compare(stored, operand.Value!) == 0));

            if (!answer.IsPage || !answer.Strings("name").SequenceEqual(expected))
                problems.Add($"{operand.Label}: expected [{string.Join(", ", expected)}], got {answer}");
        }

        problems.Should().BeEmpty(id);
    }

    // ── 6 · sort and the keyset cursor ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(All), "6a")]
    public async Task K6a_a_sort_on_the_twin_walked_one_row_a_page_is_disjoint_exhaustive_and_in_order_both_ways(string id, string name)
    {
        var kind = Kinds[name];

        foreach (var descending in new[] { false, true })
        {
            var expected = Sorted(kind, kind.Twin, descending).Select(row => row.Id).ToList();
            var direction = descending ? "desc" : "asc";

            var walk = await (await Lab.ClientAsync(LabService.Conformance)).WalkAsync(Corpus.Conformance, $$"""[{ "sort": [{ "{{kind.Twin}}": "{{direction}}" }] }, { "project": { "id": 1, "{{kind.Twin}}": 1 } }]""", limit: 1);

            walk.Ids.Should().Equal(expected, $"{id} {direction}: the null row {(descending ? "last" : "first")}");
        }
    }

    [Theory]
    [MemberData(nameof(All), "6b")]
    public async Task K6b_the_cursor_leg_carries_the_sort_value_at_full_precision(string id, string name)
    {
        var kind = Kinds[name];
        var first = Sorted(kind, kind.Member, descending: true)[0];

        var answer = await Send($$"""[{ "sort": [{ "{{kind.Member}}": "desc" }] }, { "project": { "id": 1, "{{kind.Member}}": 1 } }, { "page": { "limit": 1 } }]""");

        answer.ShouldHaveIds([first.Id], id);
        answer.HasNextPage.Should().BeTrue();
        var head = Cursors.Payload(answer.NextCursor!);
        head["m"]!.GetValue<string>().Should().Be("k", "a keyset cursor");
        head["v"]!.AsArray().Select(leg => leg!["p"]!.GetValue<string>()).Should().Equal([kind.Member, "_id"]);
        var leg = head["v"]![0]!["b"]!.GetValue<string>();
        string[] tokens = name == "date" ? [kind.LegToken(At(first, kind.Member)!), Day(At(first, kind.Member)!) + "T00:00:00"] : [kind.LegToken(At(first, kind.Member)!)];
        tokens.Should().Contain(token => leg.Contains(token, StringComparison.Ordinal), $"{id}: the leg {leg} carries the value digit for digit");
    }

    [Theory]
    [MemberData(nameof(All), "6c")]
    public async Task K6c_the_same_walk_with_a_projection_that_drops_the_sort_key_ends_and_returns_every_row_once(string id, string name)
    {
        var kind = Kinds[name];
        var expected = Sorted(kind, kind.Twin, descending: false).Select(row => row.Id).ToList();

        var walk = await (await Lab.ClientAsync(LabService.Conformance)).WalkAsync(Corpus.Conformance, $$"""[{ "sort": [{ "{{kind.Twin}}": "asc" }] }, { "project": { "id": 1 } }]""", limit: 1, maxPages: 20);

        walk.Ids.Should().Equal(expected, id);
    }

    // ── 7 · group ───────────────────────────────────────────────────────────────────────────

    private static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject item => "{" + string.Join(",", item.OrderBy(member => member.Key, StringComparer.Ordinal).Select(member => $"\"{member.Key}\":" + Canonical(member.Value))) + "}",
        JsonArray list => "[" + string.Join(",", list.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };

    private static string WireOrNull(Kind kind, BsonValue? value) => value is null ? "null" : kind.Wire(value);

    [Theory]
    [MemberData(nameof(All), "7a")]
    public async Task K7a_a_group_by_the_twin_has_one_bucket_per_value_keyed_in_the_rows_wire_form_and_a_null_bucket(string id, string name)
    {
        var kind = Kinds[name];
        var expected = Mine.GroupBy(row => WireOrNull(kind, At(row, kind.Twin)))
            .Select(group => $"{{\"k\":{group.Key},\"n\":\"{group.Count()}\"}}").Order(StringComparer.Ordinal).ToList();
        expected.Should().Contain(bucket => bucket.StartsWith("{\"k\":null", StringComparison.Ordinal));

        var answer = await Send($$"""[{ "group": { "by": [{ "path": "{{kind.Twin}}", "as": "k" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 10 } }]""");

        answer.ShouldBeOk(id);
        answer.Items.Select(Canonical).Order(StringComparer.Ordinal).Should().Equal(expected, id);
    }

    [Theory]
    [MemberData(nameof(All), "7b")]
    public async Task K7b_min_max_first_last_and_countDistinct_answer_the_value_in_the_rows_wire_form_and_ignore_nulls(string id, string name)
    {
        var kind = Kinds[name];
        var byValue = Sorted(kind, kind.Member, descending: false);
        var twins = Mine.Select(row => At(row, kind.Twin)).OfType<BsonValue>().Order(Comparer<BsonValue>.Create((a, b) => kind.Compare(a, b))).ToList();
        var expected = "{" + string.Join(",", new (string Key, string Value)[]
        {
            ("distinct", $"\"{Mine.Select(row => At(row, kind.Member)!).Distinct().Count()}\""),
            ("head", kind.Wire(At(Mine[0], kind.Member)!)),
            ("hi", kind.Wire(At(byValue[^1], kind.Member)!)),
            ("lo", kind.Wire(At(byValue[0], kind.Member)!)),
            ("tail", kind.Wire(At(Mine[^1], kind.Member)!)),
            ("twinHi", kind.Wire(twins[^1])),
            ("twinLo", kind.Wire(twins[0])),
        }.Select(entry => $"\"{entry.Key}\":{entry.Value}")) + "}";

        var answer = await Send($$"""
            [ { "sort": [{ "id": "asc" }] },
              { "group": { "by": [], "fields": { "lo": { "min": "{{kind.Member}}" }, "hi": { "max": "{{kind.Member}}" }, "head": { "first": "{{kind.Member}}" }, "tail": { "last": "{{kind.Member}}" },
                "distinct": { "countDistinct": "{{kind.Member}}" }, "twinLo": { "min": "{{kind.Twin}}" }, "twinHi": { "max": "{{kind.Twin}}" } } } },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldBeOk(id);
        answer.Items.Select(Canonical).Should().Equal([expected], id);
    }

    [Fact]
    public async Task K_long_7c_7d_7e_sum_and_avg_over_a_long_are_exact_and_travel_as_strings()
    {
        var values = Mine.Select(row => At(row, "magnitude")!.ToInt64()).ToList();
        var twins = Mine.Select(row => At(row, "optionalMagnitude")).OfType<BsonValue>().Select(value => value.ToInt64()).ToList();
        var total = values.Aggregate(0m, (sum, value) => sum + value);
        var twinTotal = twins.Aggregate(0m, (sum, value) => sum + value);
        (total % values.Count).Should().Be(0, "the corpus makes the average integral, so there is one right answer");
        var expected = new JsonObject
        {
            ["total"] = total.ToString(CultureInfo.InvariantCulture),
            ["twinTotal"] = twinTotal.ToString(CultureInfo.InvariantCulture),
            ["mean"] = (total / values.Count).ToString(CultureInfo.InvariantCulture),
            ["twinMean"] = (twinTotal / twins.Count).ToString(CultureInfo.InvariantCulture),
        };
        expected["twinMean"]!.GetValue<string>().Should().EndWith(".5", "(2^53 + 1 + 42) / 2 is not representable as a double");

        var answer = await Send("""[{ "group": { "by": [], "fields": { "total": { "sum": "magnitude" }, "twinTotal": { "sum": "optionalMagnitude" }, "mean": { "avg": "magnitude" }, "twinMean": { "avg": "optionalMagnitude" } } } }, { "page": { "limit": 10 } }]""");

        answer.ShouldBeOk();
        Canonical(answer.Items.Single()).Should().Be(Canonical(expected));
    }

    [Theory]
    [MemberData(nameof(NotLong), "7c")]
    public async Task K7c_sum_and_avg_over_a_kind_that_is_not_numeric_are_refused(string id, string name)
    {
        var kind = Kinds[name];

        foreach (var function in new[] { "sum", "avg" })
        {
            var answer = await Send($$"""[{ "group": { "by": [], "fields": { "x": { "{{function}}": "{{kind.Member}}" } } } }, { "page": { "limit": 10 } }]""");

            answer.StatusCode.Should().Be(400, $"{id} {function}: {answer}");
            answer.ErrorCodes.Should().Equal(["INVALID_AGGREGATE_ARGUMENT"]);
        }
    }

    [Theory]
    [MemberData(nameof(Push))]
    public async Task K7d_push_yields_the_values_in_the_same_wire_form_a_row_carries_them_in(string id, string name)
    {
        var kind = Kinds[name];
        var expected = "[" + string.Join(",", Mine.Select(row => kind.Wire(At(row, kind.Member)!))) + "]";

        var answer = await Send($$"""[{ "sort": [{ "id": "asc" }] }, { "group": { "by": [], "fields": { "all": { "push": "{{kind.Member}}" } } } }, { "page": { "limit": 10 } }]""");

        answer.ShouldBeOk(id);
        answer.Items.Single()!["all"]!.ToJsonString().Should().Be(expected, id);
    }

    /// <summary>The bucket key of a calendar day truncated in a zone: the UTC instant of that day's (or its month's) local midnight.</summary>
    private static string LocalMidnight(DateTime day, string unit, string zone) =>
        Temporal.Iso(Temporal.ToUtc(new DateTime(day.Year, day.Month, unit == "month" ? 1 : day.Day, 0, 0, 0, DateTimeKind.Unspecified), zone));

    [Fact]
    public async Task K_date_7e_7f_7g_dateTrunc_admits_a_date_and_every_day_belongs_to_its_own_calendar_day_in_every_zone()
    {
        var days = Mine.Select(row => At(row, "day")!.ToUniversalTime()).ToList();

        foreach (var (unit, zone) in new[] { ("month", Temporal.Berlin), ("day", Temporal.Berlin), ("day", "America/New_York") })
        {
            var expected = days.Select(day => (Day: day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Bucket: LocalMidnight(day, unit, zone))).ToList();

            var answer = await Send($$"""
                [ { "group": { "by": [{ "dateTrunc": { "path": "day", "unit": "{{unit}}", "timezone": "{{zone}}" }, "as": "bucket" }], "fields": { "n": { "count": true }, "day": { "min": "day" } } } },
                  { "page": { "limit": 10 } } ]
                """);

            answer.ShouldBeOk($"{unit} {zone}");
            answer.Items.Select(item => (item!["day"]!.GetValue<string>(), item["bucket"]!.GetValue<string>())).Should().BeEquivalentTo(expected, $"{unit} in {zone}");
        }

        LocalMidnight(new DateTime(2026, 3, 5), "day", "America/New_York").Should().Be("2026-03-05T05:00:00Z", "K-3: 5 March in New York, not 4 March");
    }

    [Theory]
    [MemberData(nameof(Neither), "7e")]
    public async Task K7e_dateTrunc_over_a_kind_that_is_not_a_date_is_refused(string id, string name)
    {
        var kind = Kinds[name];
        var answer = await Send($$"""[{ "group": { "by": [{ "dateTrunc": { "path": "{{kind.Member}}", "unit": "month" }, "as": "bucket" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 10 } }]""");

        answer.StatusCode.Should().Be(400, $"{id}: {answer}");
        answer.ErrorCodes.Should().Equal(["INVALID_AGGREGATE_ARGUMENT"]);
    }

    [Fact]
    public async Task K_long_7g_dateTrunc_over_a_long_is_refused()
    {
        var answer = await Send("""[{ "group": { "by": [{ "dateTrunc": { "path": "magnitude", "unit": "month" }, "as": "bucket" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 10 } }]""");

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().Equal(["INVALID_AGGREGATE_ARGUMENT"]);
    }

    // ── 8 · variables ───────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(All), "8a")]
    public async Task K8a_a_variable_binds_as_the_operand_of_eq_and_as_the_whole_array_of_in_and_a_malformed_one_is_refused(string id, string name)
    {
        var kind = Kinds[name];
        var alpha = At(Mine[0], kind.Member)!;
        var bravo = At(Mine[1], kind.Member)!;

        await ShouldFind($$"""{ "{{kind.Member}}": { "eq": { "$var": "x" } } }""", Names(row => Same(kind, At(row, kind.Member), bravo)), $$"""{ "x": {{kind.Operand(bravo)}} }""");
        await ShouldFind($$"""{ "{{kind.Twin}}": { "in": { "$var": "xs" } } }""", Names(row => Same(kind, At(row, kind.Twin), alpha) || Same(kind, At(row, kind.Twin), bravo)),
            $$"""{ "xs": [{{kind.Operand(alpha)}}, {{kind.Operand(bravo)}}] }""");

        var bad = await Send(Matching($$"""{ "{{kind.Member}}": { "gt": { "$var": "x" } } }"""), """{ "x": { "nested": true } }""");
        bad.StatusCode.Should().Be(400, $"{id}: {bad}");
    }

    [Theory]
    [MemberData(nameof(All), "8b")]
    public async Task K8b_a_variable_as_one_element_of_an_in_array_beside_a_literal_binds_each_element(string id, string name)
    {
        var kind = Kinds[name];
        var alpha = At(Mine[0], kind.Member)!;
        var charlie = At(Mine[2], kind.Member)!;

        await ShouldFind($$"""{ "{{kind.Member}}": { "in": [{ "$var": "x" }, {{kind.Operand(charlie)}}] } }""",
            Names(row => Same(kind, At(row, kind.Member), alpha) || Same(kind, At(row, kind.Member), charlie)), $$"""{ "x": {{kind.Operand(alpha)}} }""");
        id.Should().StartWith(name);
    }

    // ── 9 · organisation ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(All), "9")]
    public async Task K9_another_organisations_value_finds_only_our_rows_holding_it_and_under_its_organisation_only_its_row(string id, string name)
    {
        var kind = Kinds[name];
        var foreign = At(Foreign, kind.Member)!;
        var alpha = At(Mine[0], kind.Member)!;
        var ours = Names(row => Same(kind, At(row, kind.Member), foreign));

        await ShouldFind($$"""{ "{{kind.Member}}": { "eq": {{kind.Operand(foreign)}} } }""", ours);
        await ShouldFind($$"""{ "{{kind.Member}}": { "in": [{{kind.Operand(foreign)}}, {{kind.Operand(alpha)}}] } }""",
            Names(row => Same(kind, At(row, kind.Member), foreign) || Same(kind, At(row, kind.Member), alpha)));

        (await NamesFor($$"""{ "{{kind.Member}}": { "eq": {{kind.Operand(foreign)}} } }""", org: Org.B)).Should().Equal([NameOf(Foreign)], $"{id}: the value exists, it is scoped away");
        (await NamesFor("""{ "id": { "neq": null } }""", org: Org.B)).Should().Equal([NameOf(Foreign)]);
    }
}
