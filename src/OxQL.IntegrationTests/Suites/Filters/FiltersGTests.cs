using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Filters.FilterKit;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// Area G: null, missing and empty. The corpus carries the triple on <c>staff.employee#matchCode</c>
/// (<c>matchcode-null</c>, <c>matchcode-missing</c>, <c>matchcode-empty</c>) and repeats it on
/// <c>transport.shipment#shipmentNumber</c> and <c>ledger.transaction#reference</c>; every case
/// names which of the three it expects. Ported from the legacy <c>filters-g</c> battery, engine
/// half only (what <c>blank()</c> and an empty set compile to is a client spec).
/// </summary>
[Trait("Category", "Integration")]
public class FiltersGTests
{
    private static IReadOnlyList<Guid> Employees(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Employee, predicate);

    private static Guid Employee(string key) => Corpus.IdOf(Corpus.Employee, key);

    private static Guid NullRow => Employee("matchcode-null");

    private static Guid MissingRow => Employee("matchcode-missing");

    private static Guid EmptyRow => Employee("matchcode-empty");

    /// <summary>The rows whose member is null or missing: what <c>eq null</c> matches.</summary>
    private static IReadOnlyList<Guid> Unset(string entity, string path) => Corpus.IdsWhere(entity, row => Corpus.ValueAt(row, path) is null or BsonNull);

    [Fact]
    public async Task G01_eq_null_matches_the_null_row_and_the_absent_row()
    {
        Unset(Corpus.Employee, "matchCode").Should().Equal(Keys(Corpus.Employee, "matchcode-null", "matchcode-missing"));
        Unset(Corpus.Shipment, "shipmentNumber").Should().Equal(Keys(Corpus.Shipment, "number-null", "number-missing"));
        Unset(Corpus.Transaction, "reference").Should().Equal(Keys(Corpus.Transaction, "ref-null", "ref-missing"));

        (await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "eq": null } }""")).Should().Equal(Unset(Corpus.Employee, "matchCode"));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "shipmentNumber": { "eq": null } }""")).Should().Equal(Unset(Corpus.Shipment, "shipmentNumber"));
        (await (await LedgerClient()).Ids(Corpus.Transaction, """{ "reference": { "eq": null } }""")).Should().Equal(Unset(Corpus.Transaction, "reference"));
    }

    [Fact]
    public async Task G02_neq_null_is_the_exact_complement_present_and_non_null()
    {
        var expected = AllBut(Corpus.Employee, [NullRow, MissingRow]);

        var answered = await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "neq": null } }""");

        answered.Should().Equal(expected);
        answered.Should().Contain(EmptyRow, "the empty string is present and non-null");
    }

    [Fact]
    public async Task G03_eq_null_is_legal_on_every_kind_not_only_the_nullable_ones()
    {
        var staff = await StaffClient();

        Unset(Corpus.Employee, "isDeleted").Should().BeEmpty("a non-nullable bool is always written");
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": null } }""")).Should().BeEmpty();

        Unset(Corpus.Employee, "group.id").Should().Equal(Keys(Corpus.Employee, "group-null", "group-missing"));
        (await staff.Ids(Corpus.Employee, """{ "group.id": { "eq": null } }""")).Should().Equal(Unset(Corpus.Employee, "group.id"));

        Unset(Corpus.Shipment, "loadingTimeType").Should().Equal(Corpus.IdOf(Corpus.Shipment, "enum-absent"));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadingTimeType": { "eq": null } }""")).Should().Equal(Unset(Corpus.Shipment, "loadingTimeType"));

        Unset(Corpus.Transaction, "balance").Should().Equal(Keys(Corpus.Transaction, "balance-null", "balance-missing"));
        (await (await LedgerClient()).Ids(Corpus.Transaction, """{ "balance": { "eq": null } }""")).Should().Equal(Unset(Corpus.Transaction, "balance"));

        Unset(Corpus.Employee, "birthday").Should().Equal(Employee("birthday-null"));
        (await staff.Ids(Corpus.Employee, """{ "birthday": { "eq": null } }""")).Should().Equal(Unset(Corpus.Employee, "birthday"));

        Unset(Corpus.Employee, "disabilityLevel").Should().Equal(Corpus.AllIds(Corpus.Employee), "no row holds a disability level");
        (await staff.Ids(Corpus.Employee, """{ "disabilityLevel": { "eq": null } }""")).Should().Equal(Unset(Corpus.Employee, "disabilityLevel"));
    }

    [Fact]
    public async Task G04_an_ordered_operator_with_null_is_refused()
    {
        var staff = await StaffClient();

        (await staff.MatchRawAsync(Corpus.Employee, """{ "matchCode": { "gt": null } }""")).ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'gt' does not take null.");

        foreach (var op in new[] { "gte", "lt", "lte" })
            (await staff.MatchRawAsync(Corpus.Employee, $$"""{ "matchCode": { "{{op}}": null } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task G05_a_string_operator_with_null_is_refused()
    {
        var staff = await StaffClient();

        (await staff.MatchRawAsync(Corpus.Employee, """{ "matchCode": { "contains": null } }""")).ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'contains' takes a string.");

        foreach (var op in new[] { "startsWith", "endsWith", "regex" })
            (await staff.MatchRawAsync(Corpus.Employee, $$"""{ "matchCode": { "{{op}}": null } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task G06_exists_false_separates_absent_from_null_stored_where_eq_null_does_not()
    {
        var staff = await StaffClient();

        var eqNull = await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": null } }""");
        var absent = await staff.Ids(Corpus.Employee, """{ "matchCode": { "exists": false } }""");
        var present = await staff.Ids(Corpus.Employee, """{ "matchCode": { "exists": true } }""");

        absent.Should().Equal(MissingRow);
        present.Should().Contain(NullRow).And.NotContain(MissingRow);
        eqNull.Should().Equal(Keys(Corpus.Employee, "matchcode-null", "matchcode-missing"), "eq null is the union of the two states");
        eqNull.Except(absent).Should().Equal(NullRow);
    }

    [Fact]
    public async Task G07_on_a_member_no_document_writes_eq_null_matches_the_whole_collection()
    {
        Corpus.RowsMissing(Corpus.Employee, "address.additional02").Should().HaveCount(Corpus.Counts(Corpus.Employee).A, "the seed never writes it");

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "address.additional02": { "eq": null } }""")).Should().Equal(Corpus.AllIds(Corpus.Employee));
        (await staff.Ids(Corpus.Employee, """{ "address.additional02": { "exists": true } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task G08_null_missing_and_the_empty_string_are_three_disjoint_sets_on_a_string_path()
    {
        var staff = await StaffClient();

        var nulls = await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": null } }""");
        var absent = await staff.Ids(Corpus.Employee, """{ "matchCode": { "exists": false } }""");
        var empty = await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "" } }""");

        empty.Should().Equal(Employees(row => Corpus.Text(row, "matchCode") == "")).And.Equal([EmptyRow]);
        absent.Should().Equal(MissingRow);
        nulls.Should().Contain(NullRow).And.NotContain(EmptyRow);
        absent.Should().NotContain([EmptyRow, NullRow]);
    }

    [Fact]
    public async Task G09_the_blank_condition_covers_all_three_states_and_its_negation_is_the_exact_complement()
    {
        // What the client compiles blank() and notBlank() to; the bytes are a client spec.
        var blank = Employees(row => Corpus.ValueAt(row, "matchCode") is null or BsonNull || Corpus.Text(row, "matchCode") == "");
        blank.Should().Equal(Keys(Corpus.Employee, "matchcode-null", "matchcode-missing", "matchcode-empty"));

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "or": [ { "matchCode": { "eq": null } }, { "matchCode": { "eq": "" } } ] }""")).Should().Equal(blank);

        var notBlank = await staff.Ids(Corpus.Employee, """{ "not": { "or": [ { "matchCode": { "eq": null } }, { "matchCode": { "eq": "" } } ] } }""");

        notBlank.Should().Equal(AllBut(Corpus.Employee, blank));
        (notBlank.Count + blank.Count).Should().Be(Corpus.Counts(Corpus.Employee).A);
    }

    [Fact]
    public async Task G10_on_a_number_or_a_date_path_the_empty_string_arm_is_refused_not_omitted()
    {
        (await (await StaffClient()).Ids(Corpus.Employee, """{ "disabilityLevel": { "eq": null } }""")).Should().Equal(Unset(Corpus.Employee, "disabilityLevel"));

        (await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "disabilityLevel": { "eq": "" } }""")).ShouldRefuseExactly("INVALID_OPERAND");
        (await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "birthday": { "eq": "" } }""")).ShouldRefuseExactly("INVALID_OPERAND");
        (await (await FleetClient()).MatchRawAsync(Corpus.Vehicle, """{ "mileage": { "eq": "" } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task G11_in_with_an_empty_set_binds_and_matches_no_row()
    {
        var staff = await StaffClient();

        (await staff.MatchRawAsync(Corpus.Employee, """{ "matchCode": { "in": [] } }""")).ShouldBeOk();
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "in": [] } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task G12_nin_with_an_empty_set_binds_and_matches_every_row_the_null_and_the_absent_one_included()
    {
        var answered = await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "nin": [] } }""");

        answered.Should().Equal(Corpus.AllIds(Corpus.Employee));
        answered.Should().Contain([NullRow, MissingRow]);
    }

    [Fact]
    public async Task G13_a_null_inside_an_in_set_matches_the_null_or_missing_rows_alongside_the_literals()
    {
        var expected = Employees(row => Corpus.ValueAt(row, "matchCode") is null or BsonNull || TextIs(row, "matchCode", code => Order.EqualsCi(code, "DUP")));
        expected.Should().HaveCount(5);

        (await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "in": [null, "DUP"] } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task G14_a_mixed_enum_set_of_a_name_a_number_and_null_matches_all_three()
    {
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.ValueAt(row, "loadingTimeType") switch
        {
            BsonInt32 value => value.Value is 0 or 1,
            null or BsonNull => true,
            _ => false,
        });
        expected.Should().Contain(Corpus.IdOf(Corpus.Shipment, "enum-absent"));
        expected.Should().NotContain([Corpus.IdOf(Corpus.Shipment, "enum-booking"), Corpus.IdOf(Corpus.Shipment, "enum-unknown-value")]);

        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadingTimeType": { "in": ["None", 1, null] } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task G15_a_non_null_neq_and_nin_also_match_the_rows_where_the_member_is_absent()
    {
        var staff = await StaffClient();

        var neqExpected = Employees(row => !TextIs(row, "matchCode", code => Order.EqualsCi(code, "ALPHA-01")));
        neqExpected.Should().Contain([NullRow, MissingRow]);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "neq": "ALPHA-01" } }""")).Should().Equal(neqExpected);

        var ninExpected = Employees(row => !TextIs(row, "matchCode", code => Order.EqualsCi(code, "ALPHA-01") || Order.EqualsCi(code, "DUP")));
        ninExpected.Should().Contain(MissingRow);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "nin": ["ALPHA-01", "DUP"] } }""")).Should().Equal(ninExpected);
    }

    [Fact]
    public async Task G16_an_empty_array_is_distinct_from_an_absent_one_for_exists_for_eq_null_and_for_unwind()
    {
        var staff = await StaffClient();

        var present = await staff.Ids(Corpus.Employee, """{ "functions": { "exists": true } }""");
        present.Should().Equal(Employees(row => !Corpus.Missing(row, "functions")));
        present.Should().Contain(Employee("arrays-empty")).And.NotContain(Employee("arrays-missing"));

        var unset = await staff.Ids(Corpus.Employee, """{ "functions": { "eq": null } }""");
        unset.Should().Equal(Employee("arrays-missing"));

        // A collection of objects is not filterable at its root at all.
        (await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "tags": { "eq": null } }""")).ShouldRefuseExactly("NOT_FILTERABLE");

        // unwind without preserveNull drops both the empty and the absent collection.
        var rows = new[] { "items-none", "items-one", "items-missing" }.Select(key => Corpus.Row(Corpus.Shipment, key)).ToList();
        var kept = rows.Where(row => Corpus.Elements(row, "items").Count > 0).Select(row => row.Id).ToList();
        kept.Should().Equal(Corpus.IdOf(Corpus.Shipment, "items-one"));

        var numbers = string.Join(", ", rows.Select(row => $"\"{Corpus.Text(row, "shipmentNumber")}\""));
        var unwound = await (await TransportClient()).SendAsync(Corpus.Shipment, $$"""
            [ { "match": { "shipmentNumber": { "in": [{{numbers}}] } } }, { "unwind": { "path": "items" } }, { "project": { "id": 1 } },
              { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        unwound.ShouldHaveIds(kept).ShouldHaveTotal(kept.Count);
    }

    [Fact]
    public async Task G17_an_explicit_null_travels_on_the_wire_as_null_never_omitted()
    {
        var source = Corpus.Row(Corpus.Employee, "plain");
        Corpus.ValueAt(source, "religion").Should().Be(BsonNull.Value);

        var answer = await (await StaffClient()).SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "eq": "ALPHA-01" } } }, { "project": { "id": 1, "religion": 1, "matchCode": 1 } }, { "page": { "limit": 1 } } ]
            """);

        answer.ShouldHaveIds([source.Id]);
        Json.Has(answer.Items[0], "religion").Should().BeTrue(answer.ToString());
        answer.Items[0]!["religion"].Should().BeNull();
    }

    [Fact]
    public async Task G18_a_member_absent_from_the_document_is_omitted_from_the_row_entirely()
    {
        var staff = await StaffClient();
        var answer = await staff.SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "in": ["ALPHA-01", "GRP-NULL", "GRP-MISSING"] } } },
              { "project": { "id": 1, "matchCode": 1, "group.displayName": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldHaveIds(Keys(Corpus.Employee, "plain", "group-null", "group-missing"));
        answer.Strings("matchCode").Should().Equal("ALPHA-01", "GRP-NULL", "GRP-MISSING");
        answer.Items[0]!["group"]!.ToJsonString().Should().Be("""{"displayName":"Drivers"}""");
        Json.Has(answer.Items[1], "group").Should().BeFalse("a null object projected into carries no member: {0}", answer);
        Json.Has(answer.Items[2], "group").Should().BeFalse("an absent object carries no member: {0}", answer);

        // An absent collection is omitted the same way.
        var collection = await staff.SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "eq": "ARR-MISSING" } } }, { "project": { "id": 1, "functions": 1 } }, { "page": { "limit": 1 } } ]
            """);

        collection.ShouldHaveIds([Employee("arrays-missing")]);
        collection.Items[0]!.AsObject().Select(member => member.Key).Should().Equal("id");
    }
}
