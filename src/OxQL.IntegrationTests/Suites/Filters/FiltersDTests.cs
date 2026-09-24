using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using Xunit;
using static OxQL.IntegrationTests.Suites.Filters.FilterKit;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// Area D: the structure of <c>match</c> and its boolean combinators. Ported from the legacy
/// <c>filters-d</c> battery, engine half only: the cases that pinned what the TypeScript builder
/// compiles (D1, D14, D17 and the byte captures of D15, D16, D26) live with the client's specs.
/// Every expectation is computed from the corpus before the request is sent.
/// </summary>
[Trait("Category", "Integration")]
public class FiltersDTests
{
    private static IReadOnlyList<Guid> Employees(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Employee, predicate);

    /// <summary>The employees whose matchCode equals <paramref name="value"/> under the default collation.</summary>
    private static IReadOnlyList<Guid> MatchCode(string value) => Employees(row => TextIs(row, "matchCode", code => Order.EqualsCi(code, value)));

    private static bool LoadStartIn(CorpusRow row, DateTime? from, DateTime? to) =>
        Corpus.Date(row, "loadStart") is { } at && (from is null || at >= from) && (to is null || at < to);

    private static readonly DateTime WindowFrom = DateTime.Parse("2026-06-01T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static readonly DateTime WindowTo = DateTime.Parse("2026-11-01T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Fact]
    public async Task D02_two_operators_under_one_path_match_the_intersection_not_the_union()
    {
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => LoadStartIn(row, WindowFrom, WindowTo));
        expected.Should().NotBeEmpty();
        expected.Count.Should().BeLessThan(Corpus.Counts(Corpus.Shipment).A);

        var transport = await TransportClient();

        (await transport.Ids(Corpus.Shipment, """{ "loadStart": { "gte": "2026-06-01T00:00:00Z", "lt": "2026-11-01T00:00:00Z" } }""")).Should().Equal(expected);

        // Each half alone matches strictly more, so the two really were and-ed.
        var gteOnly = Corpus.IdsWhere(Corpus.Shipment, row => LoadStartIn(row, WindowFrom, null));
        var ltOnly = Corpus.IdsWhere(Corpus.Shipment, row => LoadStartIn(row, null, WindowTo));
        gteOnly.Count.Should().BeGreaterThan(expected.Count);
        ltOnly.Count.Should().BeGreaterThan(expected.Count);
        (await transport.Ids(Corpus.Shipment, """{ "loadStart": { "gte": "2026-06-01T00:00:00Z" } }""")).Should().Equal(gteOnly);
        (await transport.Ids(Corpus.Shipment, """{ "loadStart": { "lt": "2026-11-01T00:00:00Z" } }""")).Should().Equal(ltOnly);
    }

    [Fact]
    public async Task D03_two_paths_in_one_condition_object_match_the_intersection()
    {
        var expected = Employees(row => TextIs(row, "matchCode", code => Order.EqualsCi(code, "DUP")) && Corpus.ValueAt(row, "severelyDisabled") is BsonBoolean { Value: false });
        expected.Should().Equal(MatchCode("DUP")).And.HaveCount(3);

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "DUP" }, "severelyDisabled": { "eq": false } }""")).Should().Equal(expected);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "DUP" }, "severelyDisabled": { "eq": true } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task D04_the_two_spellings_of_one_path_object_are_the_same_query()
    {
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => LoadStartIn(row, WindowFrom, WindowTo));
        var transport = await TransportClient();

        (await transport.Ids(Corpus.Shipment, """{ "loadStart": { "gte": "2026-06-01T00:00:00Z", "lt": "2026-11-01T00:00:00Z" } }""")).Should().Equal(expected);
        (await transport.Ids(Corpus.Shipment, """{ "loadStart": { "lt": "2026-11-01T00:00:00Z", "gte": "2026-06-01T00:00:00Z" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task D05_a_bare_literal_under_a_path_is_an_implicit_eq_and_matches_exactly_what_eq_matches()
    {
        var expected = MatchCode("DUP");
        expected.Should().HaveCount(3);

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": "DUP" }""")).Should().Equal(expected);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "DUP" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task D06_and_takes_an_array_of_condition_objects_and_nests()
    {
        var expected = Employees(row => TextIs(row, "matchCode", code => Order.EqualsCi(code, "DUP") || Order.EqualsCi(code, "ALPHA-01")));
        expected.Should().HaveCount(4);

        var answered = await (await StaffClient()).Ids(Corpus.Employee, """
            { "and": [ { "or": [ { "matchCode": { "eq": "DUP" } }, { "matchCode": { "eq": "ALPHA-01" } } ] },
                       { "and": [ { "matchCode": { "neq": "ZZZ" } }, { "matchCode": { "exists": true } } ] } ] }
            """);

        answered.Should().Equal(expected);
    }

    [Fact]
    public async Task D07_or_takes_an_array_of_condition_objects_and_nests()
    {
        var expected = Employees(row => TextIs(row, "matchCode", code => Order.EqualsCi(code, "DUP") || Order.EqualsCi(code, "東京") || code == ""));
        expected.Should().HaveCount(5);

        var answered = await (await StaffClient()).Ids(Corpus.Employee, """
            { "or": [ { "or": [ { "matchCode": { "eq": "DUP" } }, { "matchCode": { "eq": "東京" } } ] }, { "matchCode": { "eq": "" } } ] }
            """);

        answered.Should().Equal(expected);
    }

    [Fact]
    public async Task D08_not_eq_is_the_full_complement_and_keeps_the_null_row_and_the_absent_row()
    {
        var expected = AllBut(Corpus.Employee, MatchCode("DUP"));
        expected.Should().HaveCount(Corpus.Counts(Corpus.Employee).A - 3);

        var answered = await (await StaffClient()).Ids(Corpus.Employee, """{ "not": { "matchCode": { "eq": "DUP" } } }""");

        answered.Should().Equal(expected);
        answered.Should().Contain(Corpus.IdOf(Corpus.Employee, "matchcode-null"), "not is a nor: the null row is in the complement");
        answered.Should().Contain(Corpus.IdOf(Corpus.Employee, "matchcode-missing"), "not is a nor: the missing row is in the complement");
    }

    [Theory]
    [InlineData("D09", """{ "and": [] }""")]
    [InlineData("D10", """{ "or": [] }""")]
    [InlineData("D11", """{ "and": [{}] }""")]
    public async Task D09_D10_D11_an_empty_logical_group_is_refused_with_EMPTY_LOGICAL_GROUP(string id, string condition)
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, condition);

        answer.ShouldRefuse("EMPTY_LOGICAL_GROUP", 400, id);
    }

    [Fact]
    public async Task D12_an_empty_not_is_refused_and_the_message_names_the_group_it_refused()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "not": {} }""");

        answer.ShouldRefuse("EMPTY_LOGICAL_GROUP", 400);
        answer.Messages()[0].Should().Be("'not' has no condition.");
    }

    [Fact]
    public async Task D13_an_empty_match_matches_every_row()
    {
        var expected = Corpus.AllIds(Corpus.Employee);

        (await (await StaffClient()).Ids(Corpus.Employee, "{}")).Should().Equal(expected).And.HaveCount(Corpus.Counts(Corpus.Employee).A);
    }

    [Fact]
    public async Task D15_the_folded_away_and_the_client_sends_as_an_empty_match_answers_every_row_with_its_count()
    {
        // The client folds an and whose operands were all dropped to {match:{}}; the engine half is
        // that body answering every row and the exact total.
        var expected = Corpus.AllIds(Corpus.Employee);

        var answer = await (await StaffClient()).SendAsync(Corpus.Employee, """
            [ { "match": {} }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
    }

    [Fact]
    public async Task D16_the_folded_away_or_the_client_sends_as_an_empty_id_set_matches_no_row_and_counts_zero()
    {
        var answer = await (await StaffClient()).SendAsync(Corpus.Employee, """
            [ { "match": { "id": { "in": [] } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds([]).ShouldHaveTotal(0);
    }

    [Fact]
    public async Task D18_and_given_a_non_array_is_a_ProblemDetails_400_not_an_OxQL_envelope()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "and": { "matchCode": { "eq": "DUP" } } }""");

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().BeEmpty(answer.ToString());
        answer.IsProblemDetails.Should().BeTrue(answer.ToString());
    }

    [Fact]
    public async Task D19_not_given_a_non_object_is_a_ProblemDetails_400()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "not": 5 }""");

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().BeEmpty(answer.ToString());
        answer.IsProblemDetails.Should().BeTrue(answer.ToString());
    }

    [Fact]
    public async Task D20_a_condition_that_is_not_a_JSON_object_is_a_ProblemDetails_400()
    {
        var answer = await (await StaffClient()).SendAsync(Corpus.Employee, """[ { "match": 5 }, { "page": { "limit": 1 } } ]""");

        answer.StatusCode.Should().Be(400, answer.ToString());
        answer.ErrorCodes.Should().BeEmpty(answer.ToString());
        answer.IsProblemDetails.Should().BeTrue(answer.ToString());
    }

    [Fact]
    public async Task D21_a_group_whose_members_all_fail_to_bind_is_refused_naming_every_one_of_them()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "or": [ { "nope": { "eq": 1 } }, { "alsoNope": { "eq": 2 } } ] }""");

        answer.ShouldRefuseExactly("UNKNOWN_PATH", "UNKNOWN_PATH");
        answer.StatusCode.Should().Be(400);
        answer.Errors.Select(error => error["path"]?.GetValue<string>()).Should().Equal("nope", "alsoNope");
    }

    private static string Leaves(int n, string extra = "") =>
        $$"""{ "and": [ {{string.Join(", ", Enumerable.Range(0, n).Select(i => $$"""{ "matchCode": { "neq": "no-such-value-{{i}}" } }"""))}}{{extra}} ] }""";

    [Fact]
    public async Task D22_200_leaves_bind_and_201_are_refused_with_MAX_CONDITIONS_EXCEEDED()
    {
        // None of the 200 leaves excludes anything, but neq keeps the missing row too: the whole entity.
        var expected = Corpus.AllIds(Corpus.Employee);
        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, Leaves(200))).Should().Equal(expected);

        var over = await staff.MatchRawAsync(Corpus.Employee, Leaves(201));

        over.ShouldRefuse("MAX_CONDITIONS_EXCEEDED", 400);
        over.Messages()[0].Should().Contain("201");
    }

    [Fact]
    public async Task D23_an_any_condition_costs_its_inner_leaves_and_nothing_for_itself()
    {
        const string any = """, { "emailAddresses": { "any": { "and": [ { "email": { "eq": "split-a@lab.invalid" } }, { "type": { "eq": "private" } } ] } } }""";
        var staff = await StaffClient();

        // 198 outer + 2 inner = 200: binds.
        (await staff.MatchRawAsync(Corpus.Employee, Leaves(198, any))).ShouldBeOk("198 + any(2) is 200 conditions");

        // 199 outer + 2 inner = 201: refused, and the message says 201, so the any itself added nothing.
        var over = await staff.MatchRawAsync(Corpus.Employee, Leaves(199, any));

        over.ShouldRefuse("MAX_CONDITIONS_EXCEEDED");
        over.Messages()[0].Should().Contain("201");
    }

    [Fact]
    public async Task D24_two_match_stages_compose_to_their_intersection()
    {
        var expected = MatchCode("DUP");
        var present = Employees(row => !Corpus.Missing(row, "matchCode"));
        present.Count.Should().BeGreaterThan(expected.Count, "the second stage must really narrow the first");

        var staff = await StaffClient();
        var answer = await staff.SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "exists": true } } },
              { "match": { "matchCode": { "eq": "DUP" } } },
              { "project": { "id": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 500, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "exists": true } }""")).Should().Equal(present);
    }

    [Fact]
    public async Task D25_a_match_before_and_a_match_after_a_group_each_bind_against_the_shape_at_their_position()
    {
        // Before the group: the DUP rows and the empty-string row. After it the shape is { matchCode, n },
        // and only buckets of two or more survive.
        var buckets = Corpus.Rows(Corpus.Employee)
            .Where(row => TextIs(row, "matchCode", code => Order.EqualsCi(code, "DUP") || code == ""))
            .GroupBy(row => Order.FoldCi(Corpus.Text(row, "matchCode")!))
            .Where(bucket => bucket.Count() >= 2)
            .ToList();
        buckets.Should().ContainSingle();
        var dup = buckets[0].Count();

        var answer = await (await StaffClient()).SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "in": ["DUP", ""] } } },
              { "group": { "by": [ { "path": "matchCode", "as": "matchCode" } ], "fields": { "n": { "count": true } } } },
              { "match": { "n": { "gte": 2 } } },
              { "sort": [ { "matchCode": "asc" } ] },
              { "page": { "limit": 50, "includeTotalCount": true } } ]
            """);

        answer.ShouldBeOk();
        answer.Strings("matchCode").Should().Equal("DUP");
        answer.Values("n").Select(n => n!.ToJsonString().Trim('"')).Should().Equal(dup.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task D26_the_201_leaves_the_client_compiles_without_complaint_are_refused_by_the_server()
    {
        // The client has no condition-count guard (its half is in the client specs); the same
        // pipeline shape it builds, sent whole with its sort and page, is what the server refuses.
        var answer = await (await StaffClient()).SendAsync(Corpus.Employee, $$"""[ { "match": {{Leaves(201)}} }, { "page": { "limit": 1 } } ]""");

        answer.ShouldRefuse("MAX_CONDITIONS_EXCEEDED", 400);
    }

    [Fact]
    public async Task Dagree_an_and_or_not_tree_answers_the_rows_the_corpus_picks()
    {
        var expected = Corpus.IdsWhere(Corpus.Vehicle, row =>
            TextIs(row, "matchCode", code => code is "VEH-001" or "VEH-004") && Corpus.ValueAt(row, "isDeleted") is not BsonBoolean { Value: true });
        expected.Should().HaveCount(3, "VEH-001 is duplicated in the corpus");

        var answer = await (await FleetClient()).SendAsync(Corpus.Vehicle, """
            [ { "match": { "and": [ { "or": [ { "matchCode": { "eq": "VEH-001" } }, { "matchCode": { "eq": "VEH-004" } } ] },
                                    { "not": { "isDeleted": { "eq": true } } } ] } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 500, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
    }

    [Fact]
    public async Task Dvolume_an_eq_over_the_6000_row_volume_entity_counts_exactly_what_the_seed_predicts()
    {
        var expected = Corpus.Where(Corpus.Template, row => Corpus.Text(row, "templateName") == "T-DUP").Count;
        expected.Should().Be(Corpus.TemplateDupRange.To - Corpus.TemplateDupRange.From + 1);

        (await (await TransportClient()).MatchCountAsync(Corpus.Template, """{ "templateName": { "eq": "T-DUP" } }""")).Should().Be(expected);
    }
}
