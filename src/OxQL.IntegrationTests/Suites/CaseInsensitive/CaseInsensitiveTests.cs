using System.Globalization;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Filters;
using Xunit;

namespace OxQL.IntegrationTests.Suites.CaseInsensitive;

/// <summary>
/// Contract 2 is case-insensitive by default: every string comparison, sort and group key runs
/// under the owner collation (<c>de</c>, strength 1) unless it opts out with
/// <c>caseSensitive: true</c>; contract 1 keeps the exact comparison and its regex fold. Ported
/// from the legacy <c>final/case-insensitive</c> battery (CI-00 … CI-13). Equality is computed with
/// <see cref="Order.FoldCi"/>, order with <see cref="Order.CompareCollated"/>, and what a regex with
/// the <c>i</c> flag folds with <see cref="Order.FoldCaseOnly"/>; none of it is read off the engine.
/// </summary>
[Trait("Category", "Integration")]
public class CaseInsensitiveTests
{
    private static Task<LabClient> Staff(int? contract = 2) => Lab.ClientAsync(LabService.Staff, contract: contract);

    private static Guid Employee(string key) => Corpus.IdOf(Corpus.Employee, key);

    private static IReadOnlyList<Guid> Codes(Func<string, bool> test) => Corpus.IdsWhere(Corpus.Employee, row => Corpus.Text(row, "matchCode") is { } code && test(code));

    private static IReadOnlyList<Guid> Folded(string text) => Codes(code => Order.EqualsCi(code, text));

    private static IReadOnlyList<Guid> Exact(string text) => Codes(code => code == text);

    private static IReadOnlyList<Guid> All => Corpus.AllIds(Corpus.Employee);

    private static IReadOnlyList<Guid> Except(IReadOnlyList<Guid> excluded) => FilterKit.AllBut(Corpus.Employee, excluded);

    private static Task<IReadOnlyList<Guid>> Ids(LabClient client, string condition) => client.MatchIdsAsync(Corpus.Employee, condition);

    private static async Task<IReadOnlyList<Guid>> SortedLive(LabClient client, string entry)
    {
        var answer = await client.SendAsync(Corpus.Employee, $$"""[ { "sort": [ {{entry}} ] }, { "project": { "id": 1 } }, { "page": { "limit": 500 } } ]""");

        answer.ShouldBeOk();

        return answer.Ids();
    }

    private static Task<WireAnswer> Leaf(LabClient client, string condition) =>
        client.SendAsync(Corpus.Employee, $$"""[ { "match": {{condition}} }, { "page": { "limit": 1 } } ]""");

    [Fact]
    public void CI00_the_fold_and_the_collated_order_of_the_oracle_agree_on_every_corpus_pair()
    {
        var codes = Corpus.Rows(Corpus.Employee).Select(row => Corpus.Text(row, "matchCode")).OfType<string>().ToList();
        codes.Should().HaveCount(Corpus.Counts(Corpus.Employee).A - 2, "every row but the null and the missing one holds a string");

        var disagreements = (from a in codes from b in codes where (Order.CompareCollated(a, b) == 0) != Order.EqualsCi(a, b) select $"{a} / {b}").ToList();

        disagreements.Should().BeEmpty();
        Folded("müller").Should().HaveCount(4);
        Folded("istanbul").Should().HaveCount(2);
    }

    [Fact]
    public async Task CI01_with_no_option_eq_neq_in_nin_and_startsWith_fold_case_and_accents()
    {
        var staff = await Staff();

        (await Ids(staff, """{ "matchCode": { "eq": "MULLER" } }""")).Should().Equal(Folded("MULLER"));
        (await Ids(staff, """{ "matchCode": { "eq": "İstanbul" } }""")).Should().Equal(Folded("istanbul"));
        (await Ids(staff, """{ "matchCode": { "neq": "muller" } }""")).Should().Equal(Except(Folded("muller")));

        var inSet = Codes(code => Order.EqualsCi(code, "dup") || Order.EqualsCi(code, "muller"));
        inSet.Should().HaveCount(7);
        (await Ids(staff, """{ "matchCode": { "in": ["dup", "muller"] } }""")).Should().Equal(inSet);
        (await Ids(staff, """{ "matchCode": { "nin": ["dup", "muller"] } }""")).Should().Equal(Except(inSet));

        // startsWith is the collated prefix range: it folds accents as well.
        (await Ids(staff, """{ "matchCode": { "startsWith": "MU" } }""")).Should().Equal(Codes(code => Order.FoldCi(code).StartsWith("mu", StringComparison.Ordinal)));
        (await Ids(staff, """{ "matchCode": { "startsWith": "arr-" } }""")).Should().Equal(Codes(code => Order.FoldCi(code).StartsWith("arr-", StringComparison.Ordinal)));

        // contains and endsWith are a regex with i: case only, so ÜLL misses Muller.
        (await Ids(staff, """{ "matchCode": { "contains": "ÜLL" } }""")).Should().Equal(Codes(code => Order.FoldCaseOnly(code).Contains("üll", StringComparison.Ordinal)));
        (await Ids(staff, """{ "matchCode": { "endsWith": "ER" } }""")).Should().Equal(Codes(code => Order.FoldCaseOnly(code).EndsWith("er", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CI02_a_string_sort_orders_under_the_collation_ties_broken_by_id_and_the_object_form_is_the_same_sort()
    {
        var ascending = Corpus.SortedIds(Corpus.Employee, "matchCode");
        var descending = Corpus.SortedIds(Corpus.Employee, "matchCode", descending: true);
        ascending.Should().NotEqual(Corpus.SortedIds(Corpus.Employee, "matchCode", strings: StringOrder.Binary), "the collated order is not code-point order");

        var staff = await Staff();

        (await SortedLive(staff, """{ "matchCode": "asc" }""")).Should().Equal(ascending);
        (await SortedLive(staff, """{ "matchCode": "desc" }""")).Should().Equal(descending);
        (await SortedLive(staff, """{ "matchCode": { "direction": "asc" } }""")).Should().Equal(ascending);
        (await SortedLive(staff, """{ "matchCode": { "direction": "desc" } }""")).Should().Equal(descending);
    }

    [Fact]
    public async Task CI03_a_group_on_a_string_key_folds_into_one_bucket_per_collation_class_counted_exactly()
    {
        const string Null = "\u0000null";
        var expected = Corpus.Rows(Corpus.Employee)
            .GroupBy(row => Corpus.Text(row, "matchCode") is { } code ? Order.FoldCi(code) : Null)
            .ToDictionary(bucket => bucket.Key, bucket => (long)bucket.Count());
        expected[Order.FoldCi("müller")].Should().Be(4);
        expected[Order.FoldCi("İSTANBUL")].Should().Be(2);

        var answer = await (await Staff()).SendAsync(Corpus.Employee, """
            [ { "group": { "by": [ { "path": "matchCode", "as": "code" } ], "fields": { "n": { "count": {} } } } }, { "page": { "limit": 100 } } ]
            """);

        answer.ShouldBeOk();
        var answered = answer.Items.Select(item => (
                Key: item!["code"] is { } code && code.GetValueKind() == System.Text.Json.JsonValueKind.String ? Order.FoldCi(code.GetValue<string>()) : Null,
                Count: long.Parse(item["n"]!.ToJsonString().Trim('"'), CultureInfo.InvariantCulture)))
            .ToList();

        answered.Should().HaveCount(expected.Count, answer.ToString());
        answered.ToDictionary(bucket => bucket.Key, bucket => bucket.Count).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task CI04_caseSensitive_true_restores_the_exact_comparison_and_ignoreCase_false_is_the_same_thing()
    {
        var staff = await Staff();

        foreach (var options in new[] { """{ "caseSensitive": true }""", """{ "ignoreCase": false }""", """{ "caseSensitive": true, "ignoreCase": false }""" })
        {
            (await Ids(staff, $$"""{ "matchCode": { "eq": "müller", "options": {{options}} } }""")).Should().Equal(Exact("müller"), options);
            (await Ids(staff, $$"""{ "matchCode": { "eq": "MULLER", "options": {{options}} } }""")).Should().BeEmpty(options);
            (await Ids(staff, $$"""{ "matchCode": { "in": ["MÜLLER", "dup"], "options": {{options}} } }""")).Should().Equal(Exact("MÜLLER"), options);
            (await Ids(staff, $$"""{ "matchCode": { "startsWith": "Mü", "options": {{options}} } }""")).Should().Equal(Codes(code => code.StartsWith("Mü", StringComparison.Ordinal)), options);
            (await Ids(staff, $$"""{ "matchCode": { "nin": ["DUP"], "options": {{options}} } }""")).Should().Equal(Except(Exact("DUP")), options);
        }

        (await Ids(staff, """{ "matchCode": { "eq": "MULLER", "options": { "ignoreCase": true } } }""")).Should().Equal(Folded("MULLER"), "ignoreCase: true is the default written out");

        // An exact leaf and a folding leaf in one request each keep their own sense.
        (await Ids(staff, """{ "and": [ { "matchCode": { "startsWith": "m" } }, { "matchCode": { "neq": "müller", "options": { "caseSensitive": true } } } ] }"""))
            .Should().Equal(Codes(code => Order.FoldCi(code).StartsWith('m') && code != "müller"));
    }

    [Fact]
    public async Task CI05_an_exact_sort_alone_runs_in_byte_order_and_beside_anything_that_folds_it_is_refused()
    {
        var staff = await Staff();

        (await SortedLive(staff, """{ "matchCode": { "direction": "asc", "caseSensitive": true } }""")).Should().Equal(Corpus.SortedIds(Corpus.Employee, "matchCode", strings: StringOrder.Binary));

        var withFold = await staff.SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "eq": "dup" } } }, { "sort": [ { "matchCode": { "direction": "asc", "caseSensitive": true } } ] }, { "page": { "limit": 5 } } ]
            """);

        withFold.StatusCode.Should().Be(400);
        withFold.ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Contain("The sort on 'matchCode' asks for the exact order");

        var allExact = await staff.SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "eq": "DUP", "options": { "caseSensitive": true } } } },
              { "sort": [ { "matchCode": { "direction": "asc", "caseSensitive": true } } ] }, { "project": { "id": 1 } }, { "page": { "limit": 5 } } ]
            """);

        allExact.ShouldHaveIds(Exact("DUP"));
    }

    [Fact]
    public async Task CI06_the_two_spellings_disagreeing_is_OPTION_NOT_APPLICABLE_either_way_round()
    {
        var staff = await Staff();

        (await Leaf(staff, """{ "matchCode": { "eq": "x", "options": { "caseSensitive": true, "ignoreCase": true } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE")
            .Should().Be("'caseSensitive: true' and 'ignoreCase: true' contradict each other; ignoreCase is the alias of caseSensitive with the opposite sense.");
        (await Leaf(staff, """{ "matchCode": { "eq": "x", "options": { "caseSensitive": false, "ignoreCase": false } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE")
            .Should().Contain("contradict each other");
        (await Leaf(staff, """{ "matchCode": { "eq": "x", "options": { "caseSensitive": false, "ignoreCase": true } } }""")).ShouldBeOk("agreeing spellings are accepted");
    }

    [Fact]
    public async Task CI07_caseSensitive_on_an_ordered_operator_a_regex_exists_a_non_string_member_or_a_non_string_sort_is_OPTION_NOT_APPLICABLE()
    {
        var staff = await Staff();

        foreach (var op in new[] { "gt", "gte", "lt", "lte" })
        {
            (await Leaf(staff, $$"""{ "matchCode": { "{{op}}": "M", "options": { "caseSensitive": true } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Be(
                $"'caseSensitive' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; '{op}' on 'matchCode' orders under the collation of the whole request and cannot opt out of it.");
            (await Leaf(staff, $$"""{ "matchCode": { "{{op}}": "M", "options": { "ignoreCase": false } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE");
        }

        (await Leaf(staff, """{ "matchCode": { "regex": "^d", "options": { "caseSensitive": true } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Contain("'matchCode' is a string under 'regex'");
        (await Leaf(staff, """{ "matchCode": { "exists": true, "options": { "caseSensitive": true } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Contain("under 'exists'");
        (await Leaf(staff, """{ "isDeleted": { "eq": true, "options": { "caseSensitive": true } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE")
            .Should().Be("'caseSensitive' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; 'isDeleted' is a bool under 'eq'.");

        (await staff.SendAsync(Corpus.Employee, """[ { "sort": [ { "isDeleted": { "direction": "asc", "caseSensitive": true } } ] }, { "page": { "limit": 1 } } ]"""))
            .ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Be("'caseSensitive' applies to a sort on a string member; 'isDeleted' is a bool.");
    }

    [Theory]
    [InlineData("CI08", """{ "matchCode": { "direction": "asc", "collation": "de" } }""", "UNKNOWN_STAGE_MEMBER")]
    [InlineData("CI08", """{ "matchCode": { "caseSensitive": true } }""", "INVALID_SORT_DIRECTION")]
    [InlineData("CI08", """{ "matchCode": { "direction": "ASC" } }""", "INVALID_SORT_DIRECTION")]
    public async Task CI08_the_sort_object_form_refuses_an_unknown_member_and_a_missing_or_misspelled_direction(string id, string entry, string code)
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, $$"""[ { "sort": [ {{entry}} ] }, { "page": { "limit": 1 } } ]""");

        answer.StatusCode.Should().Be(400, id);
        answer.ShouldRefuseExactly(code);
    }

    [Fact]
    public async Task CI09_a_match_after_a_group_compares_the_reported_key_under_the_collation_too()
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, """
            [ { "group": { "by": [ { "path": "matchCode", "as": "code" } ], "fields": { "n": { "count": {} } } } },
              { "match": { "code": { "eq": "MULLER" } } },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldBeOk();
        answer.Items.Should().ContainSingle(answer.ToString());
        Order.FoldCi(answer.Strings("code")[0]!).Should().Be("muller");
        answer.Items[0]!["n"]!.ToJsonString().Trim('"').Should().Be(Folded("muller").Count.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task CI13_a_char_member_folds_case_by_code_point_by_default_and_caseSensitive_compares_the_one_code_point()
    {
        IReadOnlyList<string> NamesWithGrade(char grade) => Corpus.Where(Corpus.Conformance, row => Corpus.ValueAt(row, "grade") is BsonInt32 value && value.Value == grade)
            .Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => Corpus.Text(row, "name")!).ToList();

        var upper = NamesWithGrade('B');
        upper.Should().Equal("Bravo");
        NamesWithGrade('b').Should().BeEmpty("the corpus stores the upper-case code point only");

        var conformance = await Lab.ClientAsync(LabService.Conformance);

        async Task<IReadOnlyList<string?>> Names(string condition)
        {
            var answer = await conformance.SendAsync(Corpus.Conformance, $$"""[ { "match": {{condition}} }, { "project": { "name": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]""");
            answer.ShouldBeOk();
            return answer.Strings("name");
        }

        (await Names("""{ "grade": { "eq": "B" } }""")).Should().Equal(upper);
        (await Names("""{ "grade": { "eq": "b" } }""")).Should().Equal(upper);
        (await Names("""{ "grade": { "eq": "b", "options": { "caseSensitive": true } } }""")).Should().BeEmpty();
        (await Names("""{ "grade": { "eq": "B", "options": { "caseSensitive": true } } }""")).Should().Equal(upper);
    }

    // ── contract 1 is unchanged ─────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Guid>> V1Ids(LabClient v1, string condition)
    {
        var answer = await v1.SendAsync(Corpus.Employee, $$"""[ { "match": {{condition}} }, { "project": { "_id": 1 } }, { "sort": [ { "_id": "asc" } ] }, { "page": { "limit": 500 } } ]""");

        answer.ShouldBeOk();

        return answer.Ids("_id");
    }

    [Fact]
    public async Task CI10_contract_1_eq_is_exact_ignoreCase_is_a_regex_that_folds_case_only_and_caseSensitive_is_not_an_option()
    {
        var v1 = await Staff(contract: 1);

        (await V1Ids(v1, """{ "MatchCode": { "eq": "müller" } }""")).Should().Equal(Exact("müller"));
        (await V1Ids(v1, """{ "MatchCode": { "eq": "MULLER" } }""")).Should().BeEmpty();

        // A regex with i: the case pair and Müller fold by case, Muller (an accent is not case) does not.
        var caseOnly = Codes(code => Order.FoldCaseOnly(code) == "müller");
        caseOnly.Should().Equal(FilterKit.Keys(Corpus.Employee, "case-lower", "case-upper", "accent-mark"));
        (await V1Ids(v1, """{ "MatchCode": { "eq": "MÜLLER", "options": { "ignoreCase": true } } }""")).Should().Equal(caseOnly);

        // The Turkish pair stays apart under a regex.
        (await V1Ids(v1, """{ "MatchCode": { "eq": "istanbul", "options": { "ignoreCase": true } } }""")).Should().Equal(Codes(code => Order.FoldCaseOnly(code) == "istanbul")).And.Equal([Employee("turkish-lower")]);

        (await Leaf(v1, """{ "MatchCode": { "eq": "x", "options": { "caseSensitive": true } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Be("'caseSensitive' is not an option.");
    }

    [Fact]
    public async Task CI11_contract_1_a_string_sort_is_code_point_order_uncollated()
    {
        var answer = await (await Staff(contract: 1)).SendAsync(Corpus.Employee, """[ { "sort": [ { "MatchCode": "asc" } ] }, { "project": { "_id": 1 } }, { "page": { "limit": 500 } } ]""");

        answer.ShouldBeOk();
        answer.Ids("_id").Should().Equal(Corpus.SortedIds(Corpus.Employee, "matchCode", strings: StringOrder.Binary));
    }

    [Theory]
    [InlineData("CI12", """{ "MatchCode": { "direction": "asc", "caseSensitive": true } }""")]
    [InlineData("CI12", """{ "MatchCode": { "direction": "desc" } }""")]
    public async Task CI12_contract_1_refuses_the_sort_object_form_with_INVALID_SORT_DIRECTION(string id, string entry)
    {
        var answer = await (await Staff(contract: 1)).SendAsync(Corpus.Employee, $$"""[ { "sort": [ {{entry}} ] }, { "page": { "limit": 1 } } ]""");

        answer.StatusCode.Should().Be(400, id);
        answer.ShouldRefuseExactly("INVALID_SORT_DIRECTION");
    }
}
