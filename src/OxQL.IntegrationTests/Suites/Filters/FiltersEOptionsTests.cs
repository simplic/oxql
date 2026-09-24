using System.Text.RegularExpressions;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Filters.FilterKit;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// Areas E.c and E.d: the case option and the regex guards. Contract 2 folds every string
/// comparison under the owner collation (<c>de</c>, strength 1) unless it opts out with
/// <c>caseSensitive: true</c>; <c>ignoreCase</c> is the deprecated alias with the opposite sense,
/// so <c>ignoreCase: true</c> restates the default. <c>contains</c> and <c>endsWith</c> stay a
/// regex with the <c>i</c> flag, which folds case and not accents. Ported from the legacy
/// <c>filters-e-options</c> battery (re-pinned for the fold in E6), engine half only: E56, E68,
/// the client halves of E44, E62 and E76, and the builder checks E63/E64-client are client specs.
/// The expectations come from <see cref="Order.FoldCi"/> and <see cref="Order.FoldCaseOnly"/> over
/// the corpus.
/// </summary>
[Trait("Category", "Integration")]
public class FiltersEOptionsTests
{
    private static IReadOnlyList<Guid> Employees(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Employee, predicate);

    private static IReadOnlyList<Guid> Codes(Func<string, bool> test) => Employees(row => TextIs(row, "matchCode", test));

    private static Guid Employee(string key) => Corpus.IdOf(Corpus.Employee, key);

    private static IReadOnlyList<Guid> Folded(string text) => Codes(code => Order.EqualsCi(code, text));

    private static IReadOnlyList<Guid> FoldedIn(params string[] texts) => Codes(code => texts.Any(text => Order.EqualsCi(code, text)));

    private static IReadOnlyList<Guid> Exact(string text) => Codes(code => code == text);

    private static readonly string[] MullerFolded = ["case-lower", "case-upper", "accent-mark", "accent-plain"];

    private static readonly string[] MullerCased = ["case-lower", "case-upper", "accent-mark"];

    // ── E.c · the case option ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task E55_ignoreCase_binds_on_exactly_eq_neq_in_nin_contains_startsWith_and_endsWith_over_a_string()
    {
        var staff = await StaffClient();

        foreach (var condition in new[]
        {
            """{ "eq": "DUP", "options": { "ignoreCase": true } }""",
            """{ "neq": "DUP", "options": { "ignoreCase": true } }""",
            """{ "in": ["DUP"], "options": { "ignoreCase": true } }""",
            """{ "nin": ["DUP"], "options": { "ignoreCase": true } }""",
            """{ "contains": "UP", "options": { "ignoreCase": true } }""",
            """{ "startsWith": "DU", "options": { "ignoreCase": true } }""",
            """{ "endsWith": "UP", "options": { "ignoreCase": true } }""",
        })
            (await staff.MatchRawAsync(Corpus.Employee, $$"""{ "matchCode": {{condition}} }""")).ShouldBeOk(condition);

        foreach (var condition in new[] { "\"gt\": \"A\"", "\"gte\": \"A\"", "\"lt\": \"A\"", "\"lte\": \"A\"", "\"exists\": true", "\"regex\": \"^DUP$\"" })
            (await staff.MatchRawAsync(Corpus.Employee, $$"""{ "matchCode": { {{condition}}, "options": { "ignoreCase": true } } }""")).ShouldRefuseExactly("OPTION_NOT_APPLICABLE");
    }

    [Fact]
    public async Task E57_eq_with_ignoreCase_is_an_anchored_match_under_the_collation_with_metacharacters_not_special()
    {
        var expected = Folded("müller");
        expected.Should().Equal(Keys(Corpus.Employee, MullerFolded), "strength 1: case and accent both fold");

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "müller", "options": { "ignoreCase": true } } }""")).Should().Equal(expected);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "müller" } }""")).Should().Equal(expected, "the option is the default");
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "ülle", "options": { "ignoreCase": true } } }""")).Should().BeEmpty("eq is anchored");

        Codes(code => code.Contains('.', StringComparison.Ordinal)).Should().BeEmpty();
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "ALPHA.01", "options": { "ignoreCase": true } } }""")).Should().BeEmpty("a dot is no wildcard");

        // ignoreCase: false is the alias of caseSensitive: true. E-ic-agree: so is the typed opt-out.
        Exact("müller").Should().Equal(Employee("case-lower"));
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "müller", "options": { "ignoreCase": false } } }""")).Should().Equal(Exact("müller"));
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "müller", "options": { "caseSensitive": true } } }""")).Should().Equal(Exact("müller"));
    }

    [Fact]
    public async Task E58_neq_with_ignoreCase_is_the_exact_negation_and_keeps_the_null_and_the_absent_row()
    {
        var expected = AllBut(Corpus.Employee, Folded("dup"));
        Folded("dup").Should().Equal(Keys(Corpus.Employee, "dup-a", "dup-b", "dup-c"));

        var answered = await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "neq": "dup", "options": { "ignoreCase": true } } }""");

        answered.Should().Equal(expected);
        answered.Should().Contain([Employee("matchcode-null"), Employee("matchcode-missing"), Employee("matchcode-empty")]);
    }

    [Fact]
    public async Task E59_in_with_ignoreCase_matches_every_member_under_the_collation()
    {
        var expected = FoldedIn("MÜLLER", "dup");
        expected.Should().Equal(Keys(Corpus.Employee, ["dup-a", "dup-b", "dup-c", .. MullerFolded]));

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "in": ["MÜLLER", "dup"], "options": { "ignoreCase": true } } }""")).Should().Equal(expected);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "in": ["MÜLLER", "dup"] } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task E60_nin_with_ignoreCase_is_the_exact_complement_of_the_in_set()
    {
        var expected = AllBut(Corpus.Employee, FoldedIn("MÜLLER", "dup"));

        var answered = await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "nin": ["MÜLLER", "dup"], "options": { "ignoreCase": true } } }""");

        answered.Should().Equal(expected);
        answered.Should().NotContain(Employee("accent-plain"), "under the collation Muller is MÜLLER");
        answered.Should().Contain([Employee("matchcode-missing"), Employee("matchcode-null")]);
    }

    [Fact]
    public async Task E61_contains_and_endsWith_fold_case_only_and_startsWith_folds_case_and_accents()
    {
        IReadOnlyList<Guid> CaseOnly(Func<string, bool> test) => Codes(code => test(Order.FoldCaseOnly(code)));

        var contains = CaseOnly(code => code.Contains(Order.FoldCaseOnly("ÜLL"), StringComparison.Ordinal));
        contains.Should().Equal(Keys(Corpus.Employee, MullerCased), "Muller has no Ü, and a regex folds no accent");

        var prefix = Codes(code => Order.FoldCi(code).StartsWith(Order.FoldCi("mÜ"), StringComparison.Ordinal));
        prefix.Should().Equal(Keys(Corpus.Employee, MullerFolded), "the prefix range runs under the collation");

        var suffix = CaseOnly(code => code.EndsWith(Order.FoldCaseOnly("LLER"), StringComparison.Ordinal));
        suffix.Should().Equal(Keys(Corpus.Employee, MullerFolded));

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "contains": "ÜLL", "options": { "ignoreCase": true } } }""")).Should().Equal(contains);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "startsWith": "mÜ", "options": { "ignoreCase": true } } }""")).Should().Equal(prefix);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "endsWith": "LLER", "options": { "ignoreCase": true } } }""")).Should().Equal(suffix);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "contains": "ÜLL" } }""")).Should().Equal(contains);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "startsWith": "mÜ" } }""")).Should().Equal(prefix);
    }

    [Fact]
    public async Task E62_ignoreCase_on_a_non_string_path_is_refused_with_the_kind_and_the_operator_in_the_message()
    {
        // E-ic-agree and E62-client: the body the client refuses to send, sent by hand, is refused whole.
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "isDeleted": { "eq": true, "options": { "ignoreCase": true } } }""");

        answer.StatusCode.Should().Be(400);
        answer.ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Be(
            "'ignoreCase' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; 'isDeleted' is a bool under 'eq'.");
    }

    [Fact]
    public async Task E63_ignoreCase_on_gt_is_refused_because_an_ordered_comparison_cannot_opt_out_of_the_collation()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "gt": "A", "options": { "ignoreCase": true } } }""");

        answer.ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Be(
            "'ignoreCase' applies to eq, neq, in, nin, contains, startsWith and endsWith on string members; 'gt' on 'matchCode' orders under the collation of the whole request and cannot opt out of it.");
    }

    [Fact]
    public async Task E64_ignoreCase_on_regex_is_refused_by_the_binder()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "regex": "^dup$", "options": { "ignoreCase": true } } }""");

        answer.ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Contain("'matchCode' is a string under 'regex'");
    }

    [Fact]
    public async Task E65_ignoreCase_on_exists_is_refused()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "exists": true, "options": { "ignoreCase": true } } }""");

        answer.ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Contain("under 'exists'");
    }

    [Fact]
    public async Task E66_an_unknown_option_name_is_refused_with_the_name_in_the_message()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "eq": "DUP", "options": { "collation": "de" } } }""");

        answer.ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Be("'collation' is not an option.");
    }

    [Fact]
    public async Task E67_a_non_object_options_member_is_refused_the_same_way()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "eq": "DUP", "options": 5 } }""");

        answer.ShouldRefuseExactly("OPTION_NOT_APPLICABLE").Should().Be("'options' is not an option.");
    }

    [Fact]
    public async Task E70_every_string_comparison_runs_under_the_collation_and_the_Turkish_pair_and_the_accent_pair_prove_it()
    {
        var turkish = Folded("istanbul");
        turkish.Should().Equal(Keys(Corpus.Employee, "turkish-lower", "turkish-upper"));

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "istanbul", "options": { "ignoreCase": true } } }""")).Should().Equal(turkish);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "İSTANBUL" } }""")).Should().Equal(turkish);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "Muller", "options": { "ignoreCase": true } } }""")).Should().Equal(Folded("Muller"));

        // Opted out, each spelling is its own row again.
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "istanbul", "options": { "caseSensitive": true } } }""")).Should().Equal(Employee("turkish-lower"));
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "İSTANBUL", "options": { "caseSensitive": true } } }""")).Should().Equal(Employee("turkish-upper"));
    }

    [Fact]
    public async Task E44_notContains_arrives_as_not_contains_and_answers_the_complement_either_way()
    {
        // The client's half (the bytes) is a client spec; the engine half is what the two bodies answer.
        var folded = Codes(code => Order.FoldCaseOnly(code).Contains(Order.FoldCaseOnly("ÜLL"), StringComparison.Ordinal));
        var exact = Codes(code => code.Contains("ÜLL", StringComparison.Ordinal));
        exact.Should().Equal(Employee("case-upper"));
        folded.Count.Should().BeGreaterThan(exact.Count);

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "not": { "matchCode": { "contains": "ÜLL" } } }""")).Should().Equal(AllBut(Corpus.Employee, folded));
        (await staff.Ids(Corpus.Employee, """{ "not": { "matchCode": { "contains": "ÜLL", "options": { "caseSensitive": true } } } }""")).Should().Equal(AllBut(Corpus.Employee, exact));
    }

    // ── E.d · the regex guards ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("(a+)+")]
    [InlineData("^(a+)+$")]
    [InlineData(@"(\w*)*")]
    public async Task E72_a_nested_quantifier_is_refused_with_INVALID_REGEX(string pattern)
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, new { matchCode = new { regex = pattern } });

        answer.ShouldRefuse("INVALID_REGEX");
        answer.Messages()[0].Should().Be("A quantifier over a quantified group is not allowed.");
    }

    [Theory]
    [InlineData(@"(a)\1")]
    [InlineData(@"^(a)\1$")]
    [InlineData(@"(?<x>a)\k<x>")]
    public async Task E73_a_backreference_is_refused_numbered_and_named(string pattern)
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, new { matchCode = new { regex = pattern } });

        answer.ShouldRefuse("INVALID_REGEX");
        answer.Messages()[0].Should().Be("Backreferences are not allowed.");
    }

    [Fact]
    public async Task E74_an_unparsable_pattern_is_refused_with_the_parser_message()
    {
        var staff = await StaffClient();

        var bracket = await staff.MatchRawAsync(Corpus.Employee, """{ "matchCode": { "regex": "[" } }""");
        bracket.ShouldRefuse("INVALID_REGEX");
        bracket.Messages()[0].Should().Contain("Unterminated [] set");

        (await staff.MatchRawAsync(Corpus.Employee, """{ "matchCode": { "regex": "(" } }""")).ShouldRefuse("INVALID_REGEX");
    }

    [Fact]
    public async Task E75_alternation_under_a_quantifier_with_overlapping_branches_is_allowed_and_returns_rows()
    {
        var expected = Codes(code => Regex.IsMatch(code, "(a|a)*b"));
        expected.Should().Equal(Employee("turkish-lower"));

        (await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "regex": "(a|a)*b" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task E76_a_201_character_pattern_is_refused_with_REGEX_TOO_LONG()
    {
        var pattern = "^DUPx" + string.Concat(Enumerable.Repeat("|zzz", 49));
        pattern.Length.Should().Be(201);

        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, new { matchCode = new { regex = pattern } });

        answer.ShouldRefuse("REGEX_TOO_LONG");
        answer.Messages()[0].Should().Be("The pattern is 201 characters; the limit is 200.");
    }

    [Fact]
    public async Task E77_a_200_character_pattern_is_accepted_and_returns_the_rows_it_names()
    {
        var pattern = "^DUP" + string.Concat(Enumerable.Repeat("|zzz", 49));
        pattern.Length.Should().Be(200);

        var expected = Codes(code => Regex.IsMatch(code, pattern));
        expected.Should().HaveCount(3);

        (await (await StaffClient()).Ids(Corpus.Employee, new { matchCode = new { regex = pattern } })).Should().Equal(expected);
    }

    [Fact]
    public async Task E78_an_anchored_pattern_yields_no_diagnostic()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "regex": "^DUP" } }""");

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();
        answer.Body!.AsObject().ContainsKey("diagnostics").Should().BeFalse("the member is left out when there are none");
    }

    [Fact]
    public async Task E79_an_unanchored_pattern_yields_REGEX_UNANCHORED_beside_the_rows()
    {
        var staff = await StaffClient();
        var answer = await staff.MatchRawAsync(Corpus.Employee, """{ "matchCode": { "regex": "UP" } }""");

        answer.ShouldBeOk();
        answer.DiagnosticCodes.Should().Equal("REGEX_UNANCHORED");
        answer.Diagnostics[0]["path"]!.GetValue<string>().Should().Be("matchCode");

        // The rows still come back: a diagnostic is not a refusal.
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "regex": "UP" } }""")).Should().Equal(Codes(code => Regex.IsMatch(code, "UP")));
    }

    [Theory]
    [InlineData("staff.employee", "matchCode", "^DUP$")]
    [InlineData("transport.shipment", "shipmentNumber", "^S-DUP$")]
    [InlineData("fleet.vehicle", "matchCode", "^VEH-001$")]
    [InlineData("ledger.transaction", "number", "^T-DUP$")]
    public async Task E80_regex_is_available_on_every_service_not_a_per_service_switch(string entity, string path, string pattern)
    {
        var expected = Corpus.IdsWhere(entity, row => TextIs(row, path, text => Regex.IsMatch(text, pattern)));
        expected.Count.Should().BeGreaterThanOrEqualTo(2);

        var client = await Lab.ClientForAsync(entity);

        (await client.Ids(entity, new Dictionary<string, object> { [path] = new { regex = pattern } })).Should().Equal(expected);
    }
}
