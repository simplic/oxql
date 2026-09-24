using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Filters.FilterKit;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// Areas E.a and E.b: which operator applies to which kind, and the operator × kind grid. Ported
/// from the legacy <c>filters-e</c> battery, engine half only (E18, the client's operator list, is
/// a client spec). The lab model gives the cases a member of every kind: where the legacy fleet had
/// none (<c>long</c>, <c>date</c>, <c>binary</c>, <c>unknown</c>, an unstored member) the case uses
/// <c>conformance.entity</c> beside the addon-bag route it used before.
/// </summary>
[Trait("Category", "Integration")]
public class FiltersETests
{
    private static IReadOnlyList<Guid> Employees(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Employee, predicate);

    private static IReadOnlyList<Guid> Shipments(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Shipment, predicate);

    private static IReadOnlyList<Guid> Vehicles(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Vehicle, predicate);

    private static IReadOnlyList<Guid> Transactions(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Transaction, predicate);

    private static Guid Employee(string key) => Corpus.IdOf(Corpus.Employee, key);

    private static Guid Shipment(string key) => Corpus.IdOf(Corpus.Shipment, key);

    private static Guid Vehicle(string key) => Corpus.IdOf(Corpus.Vehicle, key);

    private static IReadOnlyList<Guid> MatchCodeFolded(string value) => Employees(row => TextIs(row, "matchCode", code => Order.EqualsCi(code, value)));

    private static bool IntIs(CorpusRow row, string path, Func<long, bool> test) => Corpus.ValueAt(row, path) is { IsNumeric: true } value && test(value.ToInt64());

    private static bool BoolIs(CorpusRow row, string path, bool expected) => Corpus.ValueAt(row, path) is BsonBoolean flag && flag.Value == expected;

    private static IReadOnlyList<double> Doubles(CorpusRow row, string path) => Corpus.Elements(row, path).Where(value => value.IsNumeric).Select(value => value.ToDouble()).ToList();

    private static int? EnumValue(CorpusRow row, string path) => Corpus.ValueAt(row, path) is BsonInt32 value ? value.Value : null;

    private static IReadOnlyList<string> Strings(CorpusRow row, string path) => Corpus.Texts(row, path);

    private static DateTime Utc(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static bool LoadStartIn(CorpusRow row, string from, string to) => Corpus.Date(row, "loadStart") is { } at && at >= Utc(from) && at < Utc(to);

    private static readonly Guid GroupA = Ids.Of(Spaces.EmployeeGroup, Org.A, 1);

    // ── E.a · the applicability rules ───────────────────────────────────────────────────────

    [Fact]
    public async Task E01_eq_binds_on_every_scalar_kind_and_the_refusal_on_a_non_scalar_is_the_filterability_gate()
    {
        var staff = await StaffClient();
        var fleet = await FleetClient();
        var transport = await TransportClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "eq": "DUP" } }""")).Should().Equal(MatchCodeFolded("DUP"));
        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": 2147483647 } }""")).Should().Equal(Vehicles(row => IntIs(row, "fuelTankCapacity", n => n == int.MaxValue)));
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": true } }""")).Should().Equal(Employees(row => BoolIs(row, "isDeleted", true)));
        (await staff.Ids(Corpus.Employee, $$"""{ "group.id": { "eq": "{{GroupA}}" } }""")).Should().Equal(Employees(row => Corpus.GuidAt(row, "group.id") == GroupA));
        (await transport.Ids(Corpus.Shipment, """{ "loadingTimeType": { "eq": "Fixed" } }""")).Should().Equal(Shipments(row => EnumValue(row, "loadingTimeType") == 1));

        // Non-scalars are refused by NOT_FILTERABLE, never by INVALID_OPERAND: eq did apply, and the
        // filterability gate stopped it. The legacy unknown member (tariff.parameters.value) is the
        // lab's conformance.entity#opaque.
        (await staff.MatchRawAsync(Corpus.Employee, """{ "group": { "eq": null } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
        (await transport.MatchRawAsync(Corpus.Shipment, """{ "items": { "eq": null } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
        (await staff.MatchRawAsync(Corpus.Employee, """{ "addon": { "eq": null } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
        (await (await ConformanceClient()).MatchRawAsync(Corpus.Conformance, """{ "opaque": { "eq": null } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
    }

    [Fact]
    public async Task E02_gte_binds_on_the_nine_ordered_kinds_and_on_nothing_else()
    {
        var binds = new (Func<Task<LabClient>> Client, string Entity, string Path, string Operand)[]
        {
            (FleetClient, Corpus.Vehicle, "fuelTankCapacity", "600"),
            (TransportClient, Corpus.Shipment, "items.quantity.value", "5"),
            (FleetClient, Corpus.Vehicle, "mileage", "0"),
            (TransportClient, Corpus.Shipment, "loadStart", "\"2026-01-01T00:00:00Z\""),
            (TransportClient, Corpus.Template, "loadStart.relativeTime", "\"PT1H\""),
            (StaffClient, Corpus.Employee, "matchCode", "\"A\""),
            (TransportClient, Corpus.Shipment, "loadingTimeType", "\"Fixed\""),
            (StaffClient, Corpus.Employee, "addon.tourCount", "\"0\""),
            (StaffClient, Corpus.Employee, "addon.probationEnd", "\"2020-01-01\""),
            // The lab model has a declared long and date member, which the legacy fleet lacked.
            (ConformanceClient, Corpus.Conformance, "magnitude", "\"0\""),
            (ConformanceClient, Corpus.Conformance, "day", "\"2026-01-01\""),
        };

        foreach (var (client, entity, path, operand) in binds)
            (await (await client()).MatchRawAsync(entity, $$"""{ "{{path}}": { "gte": {{operand}} } }""")).ShouldBeOk($"gte on {path}");

        // guid and bool are the two scalar kinds it must not apply to.
        var staff = await StaffClient();
        (await staff.MatchRawAsync(Corpus.Employee, $$"""{ "group.id": { "gte": "{{GroupA}}" } }""")).ShouldRefuseExactly("INVALID_OPERAND");
        (await staff.MatchRawAsync(Corpus.Employee, """{ "isDeleted": { "gte": true } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task E03_gt_on_a_guid_path_is_refused_with_INVALID_OPERAND_naming_the_kind()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, $$"""{ "group.id": { "gt": "{{GroupA}}" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'gt' does not apply to a guid.");

        // E-agree: the dynamic spelling a grid sends, with a non-GUID operand, is refused the same way.
        (await (await StaffClient()).SendAsync(Corpus.Employee, """[ { "match": { "group.id": { "gt": "x" } } }, { "page": { "limit": 1 } } ]""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task E04_gte_on_a_bool_path_is_refused_with_INVALID_OPERAND()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "severelyDisabled": { "gte": true } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'gte' does not apply to a bool.");
    }

    [Fact]
    public async Task E05_lt_on_a_binary_path_is_refused_with_INVALID_OPERAND()
    {
        // Unreachable in the legacy fleet (no binary member); conformance.entity#payload is one.
        var answer = await (await ConformanceClient()).MatchRawAsync(Corpus.Conformance, """{ "payload": { "lt": "AQ==" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'lt' does not apply to a binary.");
    }

    [Fact]
    public async Task E06_lte_on_an_object_an_array_a_dictionary_and_an_unknown_is_refused_by_NOT_FILTERABLE_not_INVALID_OPERAND()
    {
        (await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "group": { "lte": 1 } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
        (await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "items": { "lte": 1 } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
        (await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "addon": { "lte": 1 } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
        (await (await ConformanceClient()).MatchRawAsync(Corpus.Conformance, """{ "opaque": { "lte": 1 } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
    }

    [Fact]
    public async Task E07_E08_contains_on_an_enum_path_is_refused_with_the_article_the_kind_takes()
    {
        var answer = await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "loadingTimeType": { "contains": "Fix" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'contains' does not apply to an enum.");
    }

    [Fact]
    public async Task E09_startsWith_on_an_int_path_is_refused_with_the_article_the_kind_takes()
    {
        var answer = await (await FleetClient()).MatchRawAsync(Corpus.Vehicle, """{ "fuelTankCapacity": { "startsWith": "6" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'startsWith' does not apply to an int.");
    }

    [Fact]
    public async Task E10_regex_on_a_dateTime_path_is_refused()
    {
        var answer = await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "loadStart": { "regex": "^2026" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'regex' does not apply to a dateTime.");
    }

    [Fact]
    public async Task E11_contains_on_an_array_of_strings_matches_any_element()
    {
        // contains is a regex with the i flag under contract 2: it folds case, not accents.
        var expected = Employees(row => Strings(row, "functions").Any(function => Order.FoldCaseOnly(function).Contains("work", StringComparison.Ordinal)));
        expected.Should().Equal(Employee("arrays-many"));
        (await (await StaffClient()).Ids(Corpus.Employee, """{ "functions": { "contains": "work" } }""")).Should().Equal(expected);

        var posted = Transactions(row => Strings(row, "states").Any(state => Order.FoldCaseOnly(state).Contains("post", StringComparison.Ordinal)));
        posted.Should().Equal(Corpus.IdOf(Corpus.Transaction, "states-many"));
        (await (await LedgerClient()).Ids(Corpus.Transaction, """{ "states": { "contains": "post" } }""")).Should().Equal(posted);
    }

    [Fact]
    public async Task E12_in_binds_on_every_scalar_kind()
    {
        var binds = new (Func<Task<LabClient>> Client, string Entity, string Path, string Members)[]
        {
            (StaffClient, Corpus.Employee, "matchCode", """["DUP", "ALPHA-01"]"""),
            (FleetClient, Corpus.Vehicle, "fuelTankCapacity", "[600, 2147483647]"),
            (TransportClient, Corpus.Shipment, "items.quantity.value", "[0, 7]"),
            (FleetClient, Corpus.Vehicle, "mileage", """[0, "125000.00"]"""),
            (StaffClient, Corpus.Employee, "isDeleted", "[true, false]"),
            (StaffClient, Corpus.Employee, "group.id", $"""["{GroupA}"]"""),
            (TransportClient, Corpus.Shipment, "loadStart", """["2026-06-15T23:30:00Z"]"""),
            (TransportClient, Corpus.Template, "loadStart.relativeTime", """["PT1H30M"]"""),
            (TransportClient, Corpus.Shipment, "loadingTimeType", """["Fixed", "None"]"""),
            (StaffClient, Corpus.Employee, "addon.tourCount", """["9007199254740993"]"""),
            (StaffClient, Corpus.Employee, "addon.probationEnd", """["2026-06-16"]"""),
        };

        foreach (var (client, entity, path, members) in binds)
            (await (await client()).MatchRawAsync(entity, $$"""{ "{{path}}": { "in": {{members}} } }""")).ShouldBeOk($"in on {path}");
    }

    [Fact]
    public async Task E13_exists_is_the_one_operator_that_passes_the_filterability_gate_on_a_stored_non_filterable_path()
    {
        var staff = await StaffClient();

        var groupPresent = Employees(row => !Corpus.Missing(row, "group"));
        groupPresent.Should().NotContain(Employee("group-missing")).And.Contain(Employee("group-null"));
        (await staff.Ids(Corpus.Employee, """{ "group": { "exists": true } }""")).Should().Equal(groupPresent);

        var itemsPresent = Shipments(row => !Corpus.Missing(row, "items"));
        itemsPresent.Should().NotContain(Shipment("items-missing"));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "items": { "exists": true } }""")).Should().Equal(itemsPresent);

        var bagPresent = Employees(row => !Corpus.Missing(row, "addon"));
        bagPresent.Should().NotContain(Employee("addon-missing"));
        (await staff.Ids(Corpus.Employee, """{ "addon": { "exists": true } }""")).Should().Equal(bagPresent);

        // An unknown member. The legacy member was never written, so nothing had it; the lab's
        // conformance.entity#opaque is written as null on every row, so every row has it.
        var opaquePresent = Corpus.IdsWhere(Corpus.Conformance, row => !Corpus.Missing(row, "opaque"));
        opaquePresent.Should().NotBeEmpty();
        (await (await ConformanceClient()).Ids(Corpus.Conformance, """{ "opaque": { "exists": true } }""")).Should().Equal(opaquePresent);
        (await (await ConformanceClient()).Ids(Corpus.Conformance, """{ "opaque": { "exists": false } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task E14_exists_on_an_unstored_member_is_still_refused_with_NOT_STORED_not_NOT_FILTERABLE()
    {
        // The legacy members (tours.isMirrored, tours.resource.type) are the lab's [BsonIgnore]
        // conformance.entity#scratch and the get-only #computed.
        var conformance = await ConformanceClient();

        (await conformance.MatchRawAsync(Corpus.Conformance, """{ "scratch": { "exists": true } }""")).ShouldRefuseExactly("NOT_STORED")
            .Should().Be("'scratch' is not stored; it is in the wire view only.");
        (await conformance.MatchRawAsync(Corpus.Conformance, """{ "computed": { "exists": false } }""")).ShouldRefuseExactly("NOT_STORED");
    }

    [Theory]
    [InlineData("eq", "1")]
    [InlineData("neq", "1")]
    [InlineData("in", "[]")]
    [InlineData("contains", "\"x\"")]
    [InlineData("gt", "1")]
    public async Task E15_any_operator_but_exists_on_a_stored_non_filterable_path_is_NOT_FILTERABLE(string op, string operand)
    {
        (await (await StaffClient()).MatchRawAsync(Corpus.Employee, $$"""{ "group": { "{{op}}": {{operand}} } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
    }

    [Theory]
    [InlineData("EQ")]
    [InlineData("like")]
    [InlineData("between")]
    [InlineData("elemMatch")]
    [InlineData("startswith")]
    public async Task E17_an_unknown_operator_key_is_refused_with_UNKNOWN_OPERATOR(string op)
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, $$"""{ "matchCode": { "{{op}}": "DUP" } }""");

        answer.ShouldRefuse("UNKNOWN_OPERATOR");
        answer.Messages()[0].Should().Be($"'{op}' is not an operator.");
    }

    // ── E.b · the operator × kind grid ──────────────────────────────────────────────────────

    [Fact]
    public async Task E19_eq_on_a_string_folds_case_and_accents_by_default_and_caseSensitive_is_exact()
    {
        var mullers = Keys(Corpus.Employee, "case-lower", "case-upper", "accent-plain", "accent-mark");
        MatchCodeFolded("müller").Should().Equal(mullers, "the case pair and the accent pair are one value at strength 1");

        var staff = await StaffClient();

        foreach (var spelling in new[] { "müller", "MÜLLER", "Muller" })
            (await staff.Ids(Corpus.Employee, $$"""{ "matchCode": { "eq": "{{spelling}}" } }""")).Should().Equal(MatchCodeFolded(spelling), spelling);

        foreach (var (spelling, key) in new[] { ("müller", "case-lower"), ("MÜLLER", "case-upper"), ("Muller", "accent-plain") })
        {
            Employees(row => Corpus.Text(row, "matchCode") == spelling).Should().Equal(Employee(key));
            (await staff.Ids(Corpus.Employee, $$"""{ "matchCode": { "eq": "{{spelling}}", "options": { "caseSensitive": true } } }""")).Should().Equal([Employee(key)], spelling);
        }
    }

    [Fact]
    public async Task E20_eq_on_an_int_matches_the_Int32_edge_value_exactly()
    {
        var edge = Vehicles(row => IntIs(row, "fuelTankCapacity", n => n == int.MaxValue));
        edge.Should().Equal(Vehicle("int-edge"));

        var fleet = await FleetClient();

        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": 2147483647 } }""")).Should().Equal(edge);
        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": 600 } }""")).Should().Equal(Vehicles(row => IntIs(row, "fuelTankCapacity", n => n == 600)));
    }

    [Fact]
    public async Task E21_eq_on_a_long_matches_above_2_pow_53_exactly()
    {
        var expected = Employees(row => Corpus.ValueAt(row, "addon.tourCount") is BsonInt64 { Value: 9007199254740993L });
        expected.Should().Equal(Employee("addon-rich"));

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "addon.tourCount": { "eq": "9007199254740993" } }""")).Should().Equal(expected);
        (await staff.Ids(Corpus.Employee, """{ "addon.tourCount": { "eq": "9007199254740992" } }""")).Should().BeEmpty("the neighbouring value must not match");

        // The lab model also declares a long member, which the legacy fleet did not.
        var magnitude = Corpus.IdsWhere(Corpus.Conformance, row => Corpus.ValueAt(row, "magnitude") is BsonInt64 { Value: 9007199254740993L });
        magnitude.Should().ContainSingle();
        (await (await ConformanceClient()).Ids(Corpus.Conformance, """{ "magnitude": { "eq": "9007199254740993" } }""")).Should().Equal(magnitude);
        (await (await ConformanceClient()).Ids(Corpus.Conformance, """{ "magnitude": { "eq": "9007199254740992" } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task E22_eq_on_a_decimal_matches_every_row_of_that_value_in_either_representation_and_at_any_stored_scale()
    {
        var numerically = Vehicles(row => DecimalEither(row, "mileage") == 125000m);
        numerically.Should().Contain(Vehicle("dec-string"));
        numerically.Count.Should().BeLessThan(Corpus.Counts(Corpus.Vehicle).A);
        Corpus.VehiclesWithStringMileage().Select(row => row.Key).Should().Equal("dec-string", "dec-string-mid");

        var fleet = await FleetClient();

        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "125000.00" } }""")).Should().Equal(numerically);
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "125000" } }""")).Should().Equal(numerically, "the operand's own scale is irrelevant");
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "99999.99" } }""")).Should().Equal(Vehicles(row => DecimalEither(row, "mileage") == 99999.99m));
    }

    [Fact]
    public async Task E23_eq_on_a_double_matches_exactly_the_rows_holding_that_value_at_any_element_of_a_collection()
    {
        var transport = await TransportClient();

        foreach (var value in new[] { 0d, 5d })
        {
            var expected = Shipments(row => Doubles(row, "items.quantity.value").Contains(value));
            expected.Should().Equal(Shipment("items-split"));
            (await transport.Ids(Corpus.Shipment, $$"""{ "items.quantity.value": { "eq": {{value.ToString(CultureInfo.InvariantCulture)}} } }""")).Should().Equal(expected);
        }

        var seven = Shipments(row => Doubles(row, "items.quantity.value").Contains(7d));
        seven.Should().NotContain([Shipment("items-none"), Shipment("items-missing"), Shipment("items-split")]);
        (await transport.Ids(Corpus.Shipment, """{ "items.quantity.value": { "eq": 7 } }""")).Should().Equal(seven);
    }

    [Fact]
    public async Task E24_eq_on_a_bool_splits_the_corpus_in_two()
    {
        var deleted = Employees(row => BoolIs(row, "isDeleted", true));
        var live = Employees(row => BoolIs(row, "isDeleted", false));
        deleted.Should().Equal(Employee("deleted"));
        (deleted.Count + live.Count).Should().Be(Corpus.Counts(Corpus.Employee).A);

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": true } }""")).Should().Equal(deleted);
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": false } }""")).Should().Equal(live);
    }

    [Fact]
    public async Task E25_eq_on_a_guid_matches_the_rows_naming_that_guid_and_no_others()
    {
        var expected = Employees(row => Corpus.GuidAt(row, "group.id") == GroupA);
        expected.Should().NotBeEmpty().And.NotContain([Employee("group-null"), Employee("group-missing")]);

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, $$"""{ "group.id": { "eq": "{{GroupA}}" } }""")).Should().Equal(expected);
        (await staff.Ids(Corpus.Employee, $$"""{ "group.id": { "eq": "{{Ids.Dangling()}}" } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task E26_eq_on_a_dateTime_matches_one_instant()
    {
        var expected = Shipments(row => Corpus.Date(row, "loadStart") == Utc("2026-06-15T23:30:00Z"));
        expected.Should().Equal(Shipment("utc-day-before"));

        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadStart": { "eq": "2026-06-15T23:30:00Z" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task E27_eq_on_a_date_takes_YYYY_MM_DD()
    {
        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "addon.probationEnd": { "eq": "2026-06-16" } }""")).Should().Equal(Employees(row => Corpus.Date(row, "addon.probationEnd") == Utc("2026-06-16T00:00:00Z")));
        (await staff.Ids(Corpus.Employee, """{ "addon.probationEnd": { "eq": "2026-06-17" } }""")).Should().BeEmpty();

        // The lab's declared date member (a DateOnly).
        var day = Corpus.IdsWhere(Corpus.Conformance, row => Corpus.Date(row, "day") == Utc("2026-04-06T00:00:00Z"));
        day.Should().ContainSingle();
        (await (await ConformanceClient()).Ids(Corpus.Conformance, """{ "day": { "eq": "2026-04-06" } }""")).Should().Equal(day);
    }

    [Fact]
    public async Task E28_eq_on_a_timeSpan_takes_an_ISO_8601_duration()
    {
        var expected = Corpus.Where(Corpus.Template, row => Corpus.ValueAt(row, "loadStart.relativeTime") is BsonString { Value: "01:30:00" }).Count;
        expected.Should().BeGreaterThan(0).And.BeLessThan(Corpus.TemplateVolume);

        var transport = await TransportClient();

        (await transport.MatchCountAsync(Corpus.Template, """{ "loadStart.relativeTime": { "eq": "PT1H30M" } }""")).Should().Be(expected);
        (await transport.MatchCountAsync(Corpus.Template, """{ "loadStart.relativeTime": { "eq": "PT2H" } }""")).Should().Be(0);
    }

    [Fact]
    public async Task E29_eq_on_an_enum_matches_by_member_name_and_by_the_member_number_identically()
    {
        var expected = Shipments(row => EnumValue(row, "loadingTimeType") == 1);
        expected.Should().Equal(Shipment("enum-fixed"));

        var transport = await TransportClient();

        (await transport.Ids(Corpus.Shipment, """{ "loadingTimeType": { "eq": "Fixed" } }""")).Should().Equal(expected);
        (await transport.Ids(Corpus.Shipment, """{ "loadingTimeType": { "eq": 1 } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task E30_eq_on_a_binary_takes_base64_and_matches_the_bytes()
    {
        // Unreachable in the legacy fleet (no binary member); conformance.entity#payload is one.
        var bytes = new byte[] { 1, 2, 3, 4 };
        var expected = Corpus.IdsWhere(Corpus.Conformance, row => Corpus.ValueAt(row, "payload") is BsonBinaryData binary && binary.Bytes.SequenceEqual(bytes));
        expected.Should().ContainSingle();

        var conformance = await ConformanceClient();

        (await conformance.Ids(Corpus.Conformance, $$"""{ "payload": { "eq": "{{Convert.ToBase64String(bytes)}}" } }""")).Should().Equal(expected);
        (await conformance.Ids(Corpus.Conformance, """{ "payload": { "eq": "AQIDBQ==" } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task E31_eq_on_an_object_root_is_NOT_FILTERABLE()
    {
        var answer = await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "loadAddress": { "eq": null } }""");

        answer.ShouldRefuseExactly("NOT_FILTERABLE").Should().Be("'loadAddress' is an object; a filter needs a scalar.");
    }

    [Fact]
    public async Task E32_eq_on_a_collection_root_of_objects_is_NOT_FILTERABLE()
    {
        (await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "items": { "eq": null } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
    }

    [Fact]
    public async Task E33_eq_on_a_collection_of_scalars_is_an_any_element_match()
    {
        var ledger = await LedgerClient();

        var review = Transactions(row => Strings(row, "states").Contains("review"));
        review.Should().Equal(Corpus.IdOf(Corpus.Transaction, "states-many"));
        (await ledger.Ids(Corpus.Transaction, """{ "states": { "eq": "review" } }""")).Should().Equal(review);

        var draft = Transactions(row => Strings(row, "states").Contains("draft"));
        draft.Should().NotContain(Corpus.IdOf(Corpus.Transaction, "states-empty"));
        (await ledger.Ids(Corpus.Transaction, """{ "states": { "eq": "draft" } }""")).Should().Equal(draft);
    }

    [Fact]
    public async Task E34_eq_on_a_dictionary_root_is_NOT_FILTERABLE()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "addon": { "eq": null } }""");

        answer.ShouldRefuseExactly("NOT_FILTERABLE").Should().Be("'addon' is a dictionary; a filter needs a scalar.");
    }

    [Fact]
    public async Task E35_eq_on_a_defined_addon_key_matches_and_matches_across_representations()
    {
        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "addon.shiftModel": { "eq": "early" } }""")).Should().Equal(Employees(row => Corpus.Text(row, "addon.shiftModel") == "early")).And.Equal([Employee("addon-rich")]);

        var late = Employees(row => Corpus.Text(row, "addon.shiftModel") == "late");
        late.Should().NotBeEmpty();
        (await staff.Ids(Corpus.Employee, """{ "addon.shiftModel": { "eq": "late" } }""")).Should().Equal(late);

        // A double key written as a number and as a string are the same query.
        var ratio = Employees(row => Corpus.ValueAt(row, "addon.ratio") is BsonDouble { Value: 0.5 });
        ratio.Should().Equal(Employee("addon-rich"));
        (await staff.Ids(Corpus.Employee, """{ "addon.ratio": { "eq": 0.5 } }""")).Should().Equal(ratio);
        (await staff.Ids(Corpus.Employee, """{ "addon.ratio": { "eq": "0.5" } }""")).Should().Equal(ratio);

        // A key with a space is addressable: a dictionary's segments are not path segments.
        (await staff.Ids(Corpus.Employee, """{ "addon.Ablieferbelege vorhanden": { "eq": "ja" } }""")).Should().Equal(Employees(row => Corpus.Text(row, "addon.Ablieferbelege vorhanden") == "ja"));
    }

    [Theory]
    [InlineData("undefinedKey")]
    [InlineData("nullKey")]
    [InlineData("plainCount")]
    [InlineData("retiredKey")]
    [InlineData("vincario")]
    public async Task E36_eq_on_an_undefined_a_retired_or_an_object_addon_key_is_NOT_FILTERABLE(string key)
    {
        (await (await StaffClient()).MatchRawAsync(Corpus.Employee, $$"""{ "addon.{{key}}": { "eq": "x" } }""")).ShouldRefuseExactly("NOT_FILTERABLE");
    }

    [Fact]
    public async Task E36_a_defined_member_under_an_object_addon_key_is_filterable()
    {
        var expected = Employees(row => Corpus.Text(row, "addon.vincario._v.make") == "MAN");
        expected.Should().Equal(Employee("addon-rich"));

        (await (await StaffClient()).Ids(Corpus.Employee, """{ "addon.vincario._v.make": { "eq": "MAN" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task E37_eq_on_an_unknown_member_is_NOT_FILTERABLE_with_the_unknown_to_the_model_wording()
    {
        var answer = await (await ConformanceClient()).MatchRawAsync(Corpus.Conformance, """{ "opaque": { "eq": "x" } }""");

        answer.ShouldRefuseExactly("NOT_FILTERABLE").Should().Contain("unknown to the model");
    }

    [Fact]
    public async Task E38_neq_is_the_complement_plus_the_rows_where_the_member_is_absent()
    {
        var staff = await StaffClient();

        var expected = AllBut(Corpus.Employee, MatchCodeFolded("DUP"));
        expected.Should().Contain([Employee("matchcode-missing"), Employee("matchcode-null")]);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "neq": "DUP" } }""")).Should().Equal(expected);

        (await (await FleetClient()).Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "neq": 600 } }""")).Should().Equal(Vehicles(row => !IntIs(row, "fuelTankCapacity", n => n == 600)));
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "neq": true } }""")).Should().Equal(Employees(row => !BoolIs(row, "isDeleted", true)));

        var notNone = Shipments(row => EnumValue(row, "loadingTimeType") != 0);
        notNone.Should().Contain([Shipment("enum-absent"), Shipment("enum-unknown-value")]);
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadingTimeType": { "neq": "None" } }""")).Should().Equal(notNone);

        // On a decimal the text-stored row of the value is excluded like the Decimal128 rows are.
        var notMileage = Vehicles(row => DecimalEither(row, "mileage") != 125000m);
        notMileage.Should().NotContain(Vehicle("dec-string"));
        (await (await FleetClient()).Ids(Corpus.Vehicle, """{ "mileage": { "neq": "125000.00" } }""")).Should().Equal(notMileage);
    }

    [Fact]
    public async Task E39_gt_gte_lt_lte_order_int_double_and_decimal_correctly()
    {
        var fleet = await FleetClient();

        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "gt": 600 } }""")).Should().Equal(Vehicles(row => IntIs(row, "fuelTankCapacity", n => n > 600)));
        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "gte": 600 } }""")).Should().Equal(Vehicles(row => IntIs(row, "fuelTankCapacity", n => n >= 600)));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "items.quantity.value": { "gt": 0 } }""")).Should().Equal(Shipments(row => Doubles(row, "items.quantity.value").Any(value => value > 0)));

        // Decimal: a range covers the numerically stored rows only; the text-stored ones do not
        // order by value (F16 asserts the diagnostic that says so).
        var numericallyBetween = Vehicles(row => DecimalEither(row, "mileage") is >= 99999.99m and < 125001m);
        numericallyBetween.Should().Contain([Vehicle("dec-string"), Vehicle("dec-string-mid")]);
        var stored = Vehicles(row => Corpus.ValueAt(row, "mileage") is { IsNumeric: true } && Corpus.Decimal(row, "mileage") is >= 99999.99m and < 125001m);
        stored.Should().Equal(numericallyBetween.Except([Vehicle("dec-string"), Vehicle("dec-string-mid")]));
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "gte": "99999.99", "lt": "125001" } }""")).Should().Equal(stored);

        // E-agree: the typed gte form the client compiles.
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "gte": "99999.99" } }""")).Should().Equal(Vehicles(row => Corpus.ValueAt(row, "mileage") is { IsNumeric: true } && Corpus.Decimal(row, "mileage") >= 99999.99m));
    }

    [Fact]
    public async Task E40_gt_gte_lt_lte_order_dateTime_timeSpan_and_a_date()
    {
        var transport = await TransportClient();

        var window = Shipments(row => LoadStartIn(row, "2026-10-01T00:00:00Z", "2027-01-01T00:00:00Z"));
        window.Should().NotBeEmpty();
        (await transport.Ids(Corpus.Shipment, """{ "loadStart": { "gte": "2026-10-01T00:00:00Z", "lt": "2027-01-01T00:00:00Z" } }""")).Should().Equal(window);

        var relative = Corpus.Where(Corpus.Template, row => Corpus.ValueAt(row, "loadStart.relativeTime") is BsonString { Value: "01:30:00" }).Count;
        relative.Should().BeGreaterThan(0);
        (await transport.MatchCountAsync(Corpus.Template, """{ "loadStart.relativeTime": { "gte": "PT1H" } }""")).Should().Be(relative);
        (await transport.MatchCountAsync(Corpus.Template, """{ "loadStart.relativeTime": { "gt": "PT1H30M" } }""")).Should().Be(0);
        (await transport.MatchCountAsync(Corpus.Template, """{ "loadStart.relativeTime": { "lt": "PT2H" } }""")).Should().Be(relative);

        var staff = await StaffClient();
        (await staff.Ids(Corpus.Employee, """{ "addon.probationEnd": { "gte": "2026-06-16" } }""")).Should().Equal(Employee("addon-rich"));
        (await staff.Ids(Corpus.Employee, """{ "addon.probationEnd": { "gt": "2026-06-16" } }""")).Should().BeEmpty();

        // The lab's declared date member orders the same way.
        var day = Corpus.IdsWhere(Corpus.Conformance, row => Corpus.Date(row, "day") >= Utc("2026-04-01T00:00:00Z"));
        day.Should().NotBeEmpty().And.HaveCountLessThan(Corpus.Counts(Corpus.Conformance).A);
        (await (await ConformanceClient()).Ids(Corpus.Conformance, """{ "day": { "gte": "2026-04-01" } }""")).Should().Equal(day);
    }

    [Fact]
    public async Task E41_ordered_string_operators_order_under_the_collation_not_by_code_point()
    {
        var atOrAbove = Employees(row => TextIs(row, "matchCode", code => Order.CompareCollated(code, "D") >= 0));
        var below = Employees(row => TextIs(row, "matchCode", code => Order.CompareCollated(code, "D") < 0));
        atOrAbove.Should().Contain(Employee("unicode-cjk"));
        atOrAbove.Should().NotContain(Employee("unicode-emoji"), "a symbol sorts before the letters under the collation");
        below.Should().Contain(Employee("unicode-emoji"));
        atOrAbove.Concat(below).Should().NotContain([Employee("matchcode-null"), Employee("matchcode-missing")]);

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "gte": "D" } }""")).Should().Equal(atOrAbove);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "lt": "D" } }""")).Should().Equal(below);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "gte": "d" } }""")).Should().Equal(atOrAbove, "d and D are one value at primary strength");
    }

    [Fact]
    public async Task E42_ordered_operators_on_an_enum_order_by_the_stored_value_undeclared_values_included()
    {
        var expected = Shipments(row => EnumValue(row, "loadingTimeType") >= 1);
        expected.Should().Equal(Keys(Corpus.Shipment, "enum-fixed", "enum-booking", "enum-unknown-value"));

        var transport = await TransportClient();

        (await transport.Ids(Corpus.Shipment, """{ "loadingTimeType": { "gte": "Fixed" } }""")).Should().Equal(expected);
        (await transport.Ids(Corpus.Shipment, """{ "loadingTimeType": { "gte": 1 } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task E43_contains_matches_substrings_and_escapes_the_operand()
    {
        // contains is a regex with the i flag: case folds, accents do not.
        IReadOnlyList<Guid> Containing(string part) => Employees(row => TextIs(row, "matchCode", code => Order.FoldCaseOnly(code).Contains(Order.FoldCaseOnly(part), StringComparison.Ordinal)));

        var staff = await StaffClient();

        Containing("UP").Should().NotBeEmpty();
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "contains": "UP" } }""")).Should().Equal(Containing("UP"));

        // No matchCode holds a dot, so an unescaped '.' would match every non-empty row.
        Containing(".").Should().BeEmpty();
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "contains": "." } }""")).Should().BeEmpty();
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "contains": "A-0" } }""")).Should().Equal(Containing("A-0"));
    }

    [Fact]
    public async Task E45_startsWith_and_endsWith_anchor_at_one_end_only()
    {
        var staff = await StaffClient();

        // startsWith is the collated prefix range; endsWith a regex with the i flag.
        var prefix = Employees(row => TextIs(row, "matchCode", code => Order.FoldCi(code).StartsWith(Order.FoldCi("ARR-"), StringComparison.Ordinal)));
        prefix.Should().NotBeEmpty();
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "startsWith": "ARR-" } }""")).Should().Equal(prefix);

        var suffix = Employees(row => TextIs(row, "matchCode", code => Order.FoldCaseOnly(code).EndsWith("many", StringComparison.Ordinal)));
        suffix.Should().NotBeEmpty();
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "endsWith": "MANY" } }""")).Should().Equal(suffix);

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "startsWith": "." } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task E46_between_is_client_sugar_and_its_half_open_and_of_gte_and_lt_excludes_the_upper_bound()
    {
        var transport = await TransportClient();

        (await transport.MatchRawAsync(Corpus.Shipment, """{ "loadStart": { "between": ["2026-10-01T00:00:00Z", "2026-10-25T01:30:00Z"] } }""")).ShouldRefuseExactly("UNKNOWN_OPERATOR");

        // What the client compiles between(from, to) to; the row sitting exactly on 'to' is out.
        var expected = Shipments(row => LoadStartIn(row, "2026-10-01T00:00:00Z", "2026-10-25T01:30:00Z"));
        expected.Should().Equal(Shipment("dst-autumn-first"));
        Corpus.Date(Corpus.Row(Corpus.Shipment, "dst-autumn-second"), "loadStart").Should().Be(Utc("2026-10-25T01:30:00Z"));

        (await transport.Ids(Corpus.Shipment, """{ "and": [ { "loadStart": { "gte": "2026-10-01T00:00:00Z" } }, { "loadStart": { "lt": "2026-10-25T01:30:00Z" } } ] }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task E47_the_local_calendar_day_range_onDay_compiles_to_holds_the_Berlin_day_not_the_UTC_one()
    {
        // The client computes the Berlin day 2026-06-16 as [2026-06-15T22:00Z, 2026-06-16T22:00Z).
        var expected = Shipments(row => LoadStartIn(row, "2026-06-15T22:00:00Z", "2026-06-16T22:00:00Z"));
        expected.Should().Equal(Keys(Corpus.Shipment, "utc-day-before", "utc-day-after"));

        var answered = await (await TransportClient()).Ids(Corpus.Shipment, """{ "and": [ { "loadStart": { "gte": "2026-06-15T22:00:00.000Z" } }, { "loadStart": { "lt": "2026-06-16T22:00:00.000Z" } } ] }""");

        answered.Should().Equal(expected);
        answered.Should().NotContain(Shipment("utc-day-local-prev"));
    }

    [Fact]
    public async Task E50_regex_matches_with_the_pattern_sent_verbatim()
    {
        var expected = Employees(row => TextIs(row, "matchCode", code => Regex.IsMatch(code, "^ARR-(ONE|MANY)$")));
        expected.Should().HaveCount(2);

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "regex": "^ARR-(ONE|MANY)$" } }""")).Should().Equal(expected);
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "regex": "^arr-one$" } }""")).Should().BeEmpty("the engine adds no flags of its own");
    }

    [Fact]
    public async Task E51_in_is_membership_on_each_scalar_kind()
    {
        var staff = await StaffClient();
        var fleet = await FleetClient();

        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "in": ["DUP", "ALPHA-01"] } }""")).Should().Equal(Employees(row => TextIs(row, "matchCode", code => Order.EqualsCi(code, "DUP") || Order.EqualsCi(code, "ALPHA-01"))));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadingTimeType": { "in": ["Fixed", "FixedWithBooking"] } }""")).Should().Equal(Shipments(row => EnumValue(row, "loadingTimeType") is 1 or 2));
        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "in": [2147483647] } }""")).Should().Equal(Vehicles(row => IntIs(row, "fuelTankCapacity", n => n == int.MaxValue)));
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "in": ["0", "-42.5"] } }""")).Should().Equal(Vehicles(row => DecimalEither(row, "mileage") is 0m or -42.5m));
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "in": [true] } }""")).Should().Equal(Employees(row => BoolIs(row, "isDeleted", true)));
    }

    [Fact]
    public async Task E52_nin_is_non_membership_plus_the_rows_where_the_member_is_absent()
    {
        var expected = Employees(row => !TextIs(row, "matchCode", code => Order.EqualsCi(code, "DUP") || Order.EqualsCi(code, "ALPHA-01")));
        expected.Should().Contain(Employee("matchcode-missing"));
        (await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "nin": ["DUP", "ALPHA-01"] } }""")).Should().Equal(expected);

        var enums = Shipments(row => EnumValue(row, "loadingTimeType") != 0);
        enums.Should().Contain(Shipment("enum-absent"));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadingTimeType": { "nin": ["None"] } }""")).Should().Equal(enums);
    }

    [Fact]
    public async Task E53_exists_true_counts_a_null_stored_member_as_present()
    {
        var staff = await StaffClient();

        var expected = Employees(row => !Corpus.Missing(row, "matchCode"));
        expected.Should().Contain([Employee("matchcode-null"), Employee("matchcode-empty")]).And.NotContain(Employee("matchcode-missing"));
        (await staff.Ids(Corpus.Employee, """{ "matchCode": { "exists": true } }""")).Should().Equal(expected);

        (await staff.Ids(Corpus.Employee, """{ "birthday": { "exists": true } }""")).Should().Equal(Employees(row => !Corpus.Missing(row, "birthday")));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadingTimeType": { "exists": true } }""")).Should().Equal(Shipments(row => !Corpus.Missing(row, "loadingTimeType")));
    }

    [Fact]
    public async Task E54_exists_false_is_exactly_the_absent_rows()
    {
        Corpus.IdsOf(Corpus.RowsMissing(Corpus.Employee, "matchCode")).Should().Equal(Employee("matchcode-missing"));

        (await (await StaffClient()).Ids(Corpus.Employee, """{ "matchCode": { "exists": false } }""")).Should().Equal(Corpus.IdsOf(Corpus.RowsMissing(Corpus.Employee, "matchCode")));
        (await (await TransportClient()).Ids(Corpus.Shipment, """{ "loadingTimeType": { "exists": false } }""")).Should().Equal(Corpus.IdsOf(Corpus.RowsMissing(Corpus.Shipment, "loadingTimeType"))).And.Equal([Shipment("enum-absent")]);
        (await (await LedgerClient()).Ids(Corpus.Transaction, """{ "convertState": { "exists": false } }""")).Should().Equal(Corpus.IdOf(Corpus.Transaction, "cs-absent"));
        (await (await StaffClient()).Ids(Corpus.Employee, """{ "addon": { "exists": false } }""")).Should().Equal(Employee("addon-missing"));
    }
}
