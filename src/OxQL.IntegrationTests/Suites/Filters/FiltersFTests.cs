using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Filters.FilterKit;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// Area F: operand encoding and value semantics, where a caller's value meets storage. Every case
/// asserts what comes back, not that the request was accepted: a spelling the binder tolerates
/// but storage never matches is the failure this area exists to find. Ported from the legacy
/// <c>filters-f</c> battery, engine half only: what the TypeScript builder writes for a
/// <c>Date</c> or an enum (F31, F37, F38, F46, F51, F54, F62) is a client spec.
/// </summary>
[Trait("Category", "Integration")]
public class FiltersFTests
{
    private static IReadOnlyList<Guid> Employees(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Employee, predicate);

    private static IReadOnlyList<Guid> Vehicles(Func<CorpusRow, bool> predicate) => Corpus.IdsWhere(Corpus.Vehicle, predicate);

    private static Guid Employee(string key) => Corpus.IdOf(Corpus.Employee, key);

    private static Guid Vehicle(string key) => Corpus.IdOf(Corpus.Vehicle, key);

    private static Guid Shipment(string key) => Corpus.IdOf(Corpus.Shipment, key);

    private static IReadOnlyList<Guid> Capacity(long value) => Vehicles(row => Corpus.ValueAt(row, "fuelTankCapacity") is { IsNumeric: true } n && n.ToInt64() == value);

    private static IReadOnlyList<Guid> Deleted(bool value) => Employees(row => Corpus.ValueAt(row, "isDeleted") is BsonBoolean flag && flag.Value == value);

    private static IReadOnlyList<Guid> AddonRich() => Employees(row => Corpus.ValueAt(row, "addon.tourCount") is BsonInt64 { Value: 9007199254740993L });

    private static int RelativeTemplates() => Corpus.Where(Corpus.Template, row => Corpus.ValueAt(row, "loadStart.relativeTime") is BsonString { Value: "01:30:00" }).Count;

    // ── integers, doubles and decimals ──────────────────────────────────────────────────────

    [Fact]
    public async Task F01_the_value_the_service_returned_finds_the_row_it_came_from_the_string_stored_decimal_included()
    {
        var source = Corpus.Row(Corpus.Vehicle, "dec-string");
        Corpus.ValueAt(source, "mileage").Should().Be(new BsonString("125000.00"), "the row stores its decimal as text");
        Corpus.IdsWhere(Corpus.Vehicle, row => Corpus.Text(row, "matchCode") == "VEH-002").Should().Equal(source.Id);

        var fleet = await FleetClient();
        var answer = await fleet.SendAsync(Corpus.Vehicle, """
            [ { "match": { "matchCode": { "eq": "VEH-002" } } },
              { "project": { "id": 1, "mileage": 1, "matchCode": 1, "createDateTime": 1 } },
              { "page": { "limit": 1 } } ]
            """);

        answer.ShouldHaveIds([source.Id]);
        var row = answer.Items[0]!.AsObject();

        (await fleet.Ids(Corpus.Vehicle, new JsonObject { ["matchCode"] = new JsonObject { ["eq"] = row["matchCode"]!.DeepClone() } })).Should().Contain(source.Id);
        (await fleet.Ids(Corpus.Vehicle, new JsonObject { ["createDateTime"] = new JsonObject { ["eq"] = row["createDateTime"]!.DeepClone() } })).Should().Contain(source.Id);

        // The wire renders every decimal in its one canonical spelling, whichever form stored it,
        // and that spelling is an operand that finds the row again.
        row["mileage"]!.GetValue<string>().Should().Be("125000");
        (await fleet.Ids(Corpus.Vehicle, new JsonObject { ["mileage"] = new JsonObject { ["eq"] = row["mileage"]!.DeepClone() } })).Should().Contain(source.Id);
    }

    [Fact]
    public async Task F02_a_string_path_takes_only_a_JSON_string()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "eq": 5 } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'matchCode' expects a string.");
    }

    [Fact]
    public async Task F03_an_int_binds_from_a_JSON_number_and_from_a_string_of_digits_to_the_same_rows()
    {
        var expected = Capacity(600);
        expected.Should().NotBeEmpty().And.HaveCountLessThan(Corpus.Counts(Corpus.Vehicle).A);

        var fleet = await FleetClient();

        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": 600 } }""")).Should().Equal(expected);
        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": "600" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task F04_an_int_operand_matches_an_Int32_stored_member()
    {
        Corpus.ValueAt(Corpus.Row(Corpus.Vehicle, "int-edge"), "fuelTankCapacity").Should().BeOfType<BsonInt32>();

        (await (await FleetClient()).Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": 2147483647 } }""")).Should().Equal(Capacity(int.MaxValue)).And.Equal([Vehicle("int-edge")]);
    }

    [Fact]
    public async Task F05_a_non_integral_number_on_an_int_path_is_refused()
    {
        (await (await FleetClient()).MatchRawAsync(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": 1.5 } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task F06_a_non_numeric_string_on_an_int_path_is_refused()
    {
        var answer = await (await FleetClient()).MatchRawAsync(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": "five" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'fuelTankCapacity' expects an integer.");
    }

    [Fact]
    public async Task F07_a_leading_plus_and_leading_zeros_bind_and_surrounding_whitespace_is_refused()
    {
        var expected = Capacity(600);
        var fleet = await FleetClient();

        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": "+600" } }""")).Should().Equal(expected);
        (await fleet.Ids(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": "0600" } }""")).Should().Equal(expected);
        (await fleet.MatchRawAsync(Corpus.Vehicle, """{ "fuelTankCapacity": { "eq": " 600 " } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task F08_a_long_above_2_pow_53_binds_exactly_as_a_string_and_the_number_a_JavaScript_client_sends_matches_nothing()
    {
        AddonRich().Should().Equal(Employee("addon-rich"));

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "addon.tourCount": { "eq": "9007199254740993" } }""")).Should().Equal(AddonRich());

        // 9007199254740993 written as a JSON number reaches a JavaScript client's JSON.stringify
        // as 9007199254740992: that is the number such a client sends, and it matches nothing.
        (await staff.Ids(Corpus.Employee, """{ "addon.tourCount": { "eq": 9007199254740992 } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task F09_long_MaxValue_binds_and_one_above_it_is_refused()
    {
        var staff = await StaffClient();

        (await staff.MatchRawAsync(Corpus.Employee, """{ "addon.tourCount": { "eq": "9223372036854775807" } }""")).ShouldBeOk();
        (await staff.Ids(Corpus.Employee, """{ "addon.tourCount": { "eq": "9223372036854775807" } }""")).Should().BeEmpty();
        (await staff.MatchRawAsync(Corpus.Employee, """{ "addon.tourCount": { "eq": "9223372036854775808" } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task F10_a_long_in_the_addon_bag_comes_back_as_a_string_and_that_string_finds_its_row()
    {
        var staff = await StaffClient();
        var answer = await staff.SendAsync(Corpus.Employee, """
            [ { "match": { "matchCode": { "eq": "ADDON-RICH" } } }, { "project": { "id": 1, "addon": 1 } }, { "page": { "limit": 1 } } ]
            """);

        answer.ShouldHaveIds(AddonRich());
        var tourCount = Json.At(answer.Items[0], "addon.tourCount") as JsonValue;

        tourCount.Should().NotBeNull(answer.ToString());
        tourCount!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.String, "a long travels as a string so a JavaScript client keeps it");
        tourCount.GetValue<string>().Should().Be("9007199254740993");
        (await staff.Ids(Corpus.Employee, new JsonObject { ["addon.tourCount"] = new JsonObject { ["eq"] = tourCount.DeepClone() } })).Should().Equal(AddonRich());
    }

    [Fact]
    public async Task F11_a_double_binds_from_a_JSON_number_and_from_a_numeric_string_to_the_same_rows()
    {
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Elements(row, "items.quantity.value").Any(value => value.IsNumeric && value.ToDouble() == 7d));
        expected.Should().NotBeEmpty();

        var transport = await TransportClient();

        (await transport.Ids(Corpus.Shipment, """{ "items.quantity.value": { "eq": 7 } }""")).Should().Equal(expected);
        (await transport.Ids(Corpus.Shipment, """{ "items.quantity.value": { "eq": "7" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task F12_a_decimal_binds_from_a_JSON_number_and_from_a_string_to_the_same_rows()
    {
        var expected = Vehicles(row => DecimalEither(row, "mileage") == 99999.99m);
        expected.Should().Equal(Vehicle("dec-string-mid"));

        var fleet = await FleetClient();

        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": 99999.99 } }""")).Should().Equal(expected);
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "99999.99" } }""")).Should().Equal(expected);
    }

    [Fact]
    public async Task F13_F14_the_tolerant_string_alternative_is_a_scale_pattern_so_every_scale_the_driver_could_have_written_matches()
    {
        var fleet = await FleetClient();

        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "99999.990" } }""")).Should().Equal(Vehicle("dec-string-mid"));
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "125000.00" } }""")).Should().Contain(Vehicle("dec-string"));
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "125000" } }""")).Should().Contain(Vehicle("dec-string"));
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": "12500" } }""")).Should().NotContain(Vehicle("dec-string"), "the pattern is anchored");
    }

    [Fact]
    public async Task F16_a_decimal_range_covers_the_numerically_stored_rows_only_and_says_so_with_DECIMAL_TEXT_EXCLUDED()
    {
        var numerically = Vehicles(row => DecimalEither(row, "mileage") < 100000m);
        numerically.Should().Contain(Vehicle("dec-string-mid"));
        var stored = Vehicles(row => Corpus.ValueAt(row, "mileage") is { IsNumeric: true } && Corpus.Decimal(row, "mileage") < 100000m);
        stored.Should().Equal(numerically.Except([Vehicle("dec-string-mid")]));

        var fleet = await FleetClient();
        var raw = await fleet.MatchRawAsync(Corpus.Vehicle, """{ "mileage": { "lt": "100000" } }""");

        raw.ShouldBeOk();
        raw.DiagnosticCodes.Should().Equal("DECIMAL_TEXT_EXCLUDED");
        raw.Diagnostics[0]["path"]!.GetValue<string>().Should().Be("mileage");
        raw.Diagnostics[0]["message"]!.GetValue<string>().Should().Be(
            "'mileage' may hold decimals written as text; 'lt' covers the numerically stored rows only, because text does not order by value.");
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "lt": "100000" } }""")).Should().Equal(stored);

        // One diagnostic per ordered operator, and none for equality, which reaches both forms.
        (await fleet.MatchRawAsync(Corpus.Vehicle, """{ "mileage": { "gte": "99999.99", "lt": "125001" } }""")).DiagnosticCodes.Should().Equal("DECIMAL_TEXT_EXCLUDED", "DECIMAL_TEXT_EXCLUDED");
        (await fleet.MatchRawAsync(Corpus.Vehicle, """{ "mileage": { "eq": "125000.00" } }""")).ShouldBeOk().ShouldHaveNoDiagnostics();
    }

    [Fact]
    public async Task F17_a_negative_a_zero_and_a_25_place_decimal_all_bind_and_match_their_own_row()
    {
        var fleet = await FleetClient();

        foreach (var (operand, key) in new[] { ("-42.5", "dec-negative"), ("0", "dec-zero"), ("0.1234567890123456789012345", "dec-many-places") })
        {
            var expected = Vehicles(row => Corpus.Number(row, "mileage") is { } text && Order.CompareDecimal(text, operand) == 0);
            expected.Should().Equal([Vehicle(key)], operand);
            (await fleet.Ids(Corpus.Vehicle, $$"""{ "mileage": { "eq": "{{operand}}" } }""")).Should().Equal(expected, operand);
        }

        // The same 25-place value as a JavaScript client's JSON number (the nearest double) finds nothing.
        (await fleet.Ids(Corpus.Vehicle, """{ "mileage": { "eq": 0.12345678901234568 } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task F19_the_ledger_carries_the_same_duality_and_the_same_canonical_spelling()
    {
        var stringRow = Corpus.IdOf(Corpus.Transaction, "dec-string");
        var numerically = Corpus.IdsWhere(Corpus.Transaction, row => DecimalEither(row, "totalPrice") == 1000m);
        numerically.Should().Contain(stringRow);

        var ledger = await LedgerClient();

        (await ledger.Ids(Corpus.Transaction, """{ "totalPrice": { "eq": "1000.00" } }""")).Should().Equal(numerically);

        var projected = await ledger.SendAsync(Corpus.Transaction, """
            [ { "match": { "number": { "in": ["T-0008", "T-0009"] } } }, { "project": { "id": 1, "totalPrice": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]
            """);

        projected.ShouldHaveIds(Keys(Corpus.Transaction, "dec-decimal128", "dec-string"));
        projected.Strings("totalPrice").Should().Equal("1000", "1000");
    }

    // ── booleans, guids, temporals and durations ────────────────────────────────────────────

    [Fact]
    public async Task F20_a_bool_binds_from_true_and_false_and_from_their_strings()
    {
        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": true } }""")).Should().Equal(Deleted(true));
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": "true" } }""")).Should().Equal(Deleted(true));
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": "false" } }""")).Should().Equal(Deleted(false));
    }

    [Fact]
    public async Task F21_True_and_padded_true_also_parse_as_booleans()
    {
        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": "True" } }""")).Should().Equal(Deleted(true));
        (await staff.Ids(Corpus.Employee, """{ "isDeleted": { "eq": " true " } }""")).Should().Equal(Deleted(true));
    }

    [Fact]
    public async Task F22_yes_on_a_bool_path_is_refused()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "isDeleted": { "eq": "yes" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'isDeleted' expects a boolean.");
    }

    [Fact]
    public async Task F23_hyphenated_unhyphenated_braced_and_upper_case_GUIDs_all_bind_to_the_same_rows()
    {
        var group = Ids.Of(Spaces.EmployeeGroup, Org.A, 1);
        var expected = Employees(row => Corpus.GuidAt(row, "group.id") == group);
        expected.Should().NotBeEmpty();

        var staff = await StaffClient();

        foreach (var spelling in new[] { group.ToString("D"), group.ToString("N"), group.ToString("B"), group.ToString("D").ToUpperInvariant() })
            (await staff.Ids(Corpus.Employee, $$"""{ "group.id": { "eq": "{{spelling}}" } }""")).Should().Equal(expected, spelling);
    }

    [Fact]
    public async Task F24_a_non_GUID_string_on_a_guid_path_is_refused()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "group.id": { "eq": "not-a-guid" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'group.id' expects a GUID string.");
    }

    [Fact]
    public async Task F25_F27_one_subtype_4_comparison_is_emitted_so_a_string_spelled_guid_matches_only_where_a_string_holds_it()
    {
        var legacyRef = Employees(row => Corpus.Text(row, "addon.legacyRef") == "7b7e2a4e-0000-4000-8000-000000000042");
        legacyRef.Should().Equal(Employee("addon-rich"));

        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "addon.legacyRef": { "eq": "7b7e2a4e-0000-4000-8000-000000000042" } }""")).Should().Equal(legacyRef);
        (await staff.Ids(Corpus.Employee, """{ "group.id": { "eq": "00000000-0000-4000-8000-000000000000" } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task F28_a_guid_held_in_a_string_member_takes_string_operators()
    {
        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "addon.legacyRef": { "startsWith": "7b7e2a4e" } }""")).Should().Equal(Employee("addon-rich"));
        (await staff.Ids(Corpus.Employee, """{ "addon.legacyRef": { "contains": "-0000-" } }""")).Should().Equal(Employee("addon-rich"));
    }

    [Fact]
    public async Task F29_F30_a_date_takes_exactly_YYYY_MM_DD_and_a_date_carrying_a_time_is_refused()
    {
        var staff = await StaffClient();

        (await staff.Ids(Corpus.Employee, """{ "addon.probationEnd": { "eq": "2026-06-16" } }""")).Should().Equal(Employee("addon-rich"));

        var withTime = await staff.MatchRawAsync(Corpus.Employee, """{ "addon.probationEnd": { "eq": "2026-06-16T00:00:00Z" } }""");

        withTime.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'addon.probationEnd' expects a date as YYYY-MM-DD.");
    }

    [Fact]
    public async Task F32_F33_a_dateTime_binds_from_Z_and_from_an_offset_and_the_offset_normalises_to_the_same_instant()
    {
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Date(row, "loadStart") == new DateTime(2026, 6, 15, 23, 30, 0, DateTimeKind.Utc));
        expected.Should().Equal(Shipment("utc-day-before"));

        var transport = await TransportClient();

        foreach (var spelling in new[] { "2026-06-15T23:30:00Z", "2026-06-16T01:30:00+02:00", "2026-06-15T23:30:00.000Z" })
            (await transport.Ids(Corpus.Shipment, $$"""{ "loadStart": { "eq": "{{spelling}}" } }""")).Should().Equal(expected, spelling);
    }

    [Theory]
    [InlineData("F34", "2026-06-15T23:30:00")]
    [InlineData("F35", "2026-06-15")]
    [InlineData("F36", "2026-6-15T23:30:00Z")]
    public async Task F34_F35_F36_a_bare_local_time_a_date_only_string_and_an_unpadded_instant_are_refused(string id, string operand)
    {
        var answer = await (await TransportClient()).MatchRawAsync(Corpus.Shipment, $$"""{ "loadStart": { "eq": "{{operand}}" } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'loadStart' expects an ISO-8601 date-time with 'Z' or an offset.", id);
    }

    [Fact]
    public async Task F39_F40_a_timeSpan_takes_an_ISO_8601_duration_and_a_clock_format_or_an_instant_is_refused()
    {
        var transport = await TransportClient();

        (await transport.MatchCountAsync(Corpus.Template, """{ "loadStart.relativeTime": { "eq": "PT1H30M" } }""")).Should().Be(RelativeTemplates());

        var clock = await transport.MatchRawAsync(Corpus.Template, """{ "loadStart.relativeTime": { "eq": "01:30:00" } }""");
        clock.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'loadStart.relativeTime' expects an ISO-8601 duration.");

        // F42, engine half: a client Date written against a timeSpan path arrives as an instant, and is refused.
        (await transport.MatchRawAsync(Corpus.Template, """{ "loadStart.relativeTime": { "eq": "2026-06-15T23:30:00.000Z" } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    [Fact]
    public async Task F41_a_timeSpan_stored_in_the_driver_c_format_reads_back_as_ISO_8601_and_that_spelling_round_trips()
    {
        var source = Corpus.Row(Corpus.Template, "tpl-000001");
        Corpus.ValueAt(source, "loadStart.relativeTime").Should().Be(new BsonString("01:30:00"));

        var transport = await TransportClient();
        var answer = await transport.SendAsync(Corpus.Template, """
            [ { "match": { "templateName": { "eq": "T-000001" } } }, { "project": { "id": 1, "loadStart.relativeTime": 1 } }, { "page": { "limit": 1 } } ]
            """);

        answer.ShouldHaveIds([source.Id]);
        var relative = Json.At(answer.Items[0], "loadStart.relativeTime")!.GetValue<string>();

        relative.Should().Be("PT1H30M");
        (await transport.MatchCountAsync(Corpus.Template, new JsonObject { ["loadStart.relativeTime"] = new JsonObject { ["eq"] = relative } })).Should().Be(RelativeTemplates());
        (await transport.MatchRawAsync(Corpus.Template, """{ "loadStart.relativeTime": { "eq": "01:30:00" } }""")).ShouldRefuseExactly("INVALID_OPERAND");
    }

    // ── enums ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task F43_F44_a_member_name_and_the_member_number_bind_to_the_same_rows()
    {
        var expected = Corpus.IdsOf(Corpus.ShipmentsWithLoadingTimeType(Fleet.Models.Transport.LoadingDateTimeType.Fixed));
        expected.Should().Equal(Shipment("enum-fixed"));

        var transport = await TransportClient();

        foreach (var operand in new[] { "\"Fixed\"", "1", "\"1\"" })
            (await transport.Ids(Corpus.Shipment, $$"""{ "loadingTimeType": { "eq": {{operand}} } }""")).Should().Equal(expected, operand);
    }

    [Fact]
    public async Task F45_an_unknown_enum_name_is_refused_with_UNKNOWN_ENUM_MEMBER_on_both_services()
    {
        var answer = await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "loadingTimeType": { "eq": "Nope" } }""");

        answer.ShouldRefuseExactly("UNKNOWN_ENUM_MEMBER").Should().Be("'Nope' is not a member of the enum at 'loadingTimeType'.");
        (await (await LedgerClient()).MatchRawAsync(Corpus.Transaction, """{ "convertState": { "eq": "Nope" } }""")).ShouldRefuseExactly("UNKNOWN_ENUM_MEMBER");
    }

    [Fact]
    public async Task F47_an_unknown_enum_number_is_validated_exactly_like_an_unknown_enum_name_whatever_the_storage()
    {
        var transport = await TransportClient();
        var answer = await transport.MatchRawAsync(Corpus.Shipment, """{ "loadingTimeType": { "eq": 7 } }""");

        answer.StatusCode.Should().Be(400);
        answer.ShouldRefuseExactly("UNKNOWN_ENUM_MEMBER").Should().Be("'7' is not a member of the enum at 'loadingTimeType'.");
        (await (await LedgerClient()).MatchRawAsync(Corpus.Transaction, """{ "convertState": { "eq": 42 } }""")).ShouldRefuseExactly("UNKNOWN_ENUM_MEMBER");

        // The operand is validated, not storage: a row stores 99, which no member names. eq 99 is
        // refused, and the ordered operators still reach that row through a declared operand.
        var stored99 = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.ValueAt(row, "loadingTimeType") is BsonInt32 { Value: 99 });
        stored99.Should().Equal(Shipment("enum-unknown-value"));
        (await transport.MatchRawAsync(Corpus.Shipment, """{ "loadingTimeType": { "eq": 99 } }""")).ShouldRefuseExactly("UNKNOWN_ENUM_MEMBER");
        (await transport.Ids(Corpus.Shipment, """{ "loadingTimeType": { "gte": "FixedWithBooking" } }""")).Should().Contain(stored99);
    }

    [Fact]
    public async Task F48_an_enum_number_outside_Int32_is_a_coded_refusal_not_a_host_500()
    {
        var transport = await TransportClient();

        foreach (var operand in new[] { 2147483648L, -2147483649L })
        {
            var answer = await transport.MatchRawAsync(Corpus.Shipment, $$"""{ "loadingTimeType": { "eq": {{operand}} } }""");

            answer.StatusCode.Should().Be(400, answer.ToString());
            answer.ShouldRefuseExactly("UNKNOWN_ENUM_MEMBER").Should().Be($"'{operand}' is not a member of the enum at 'loadingTimeType'.");
        }

        (await transport.MatchRawAsync(Corpus.Shipment, """{ "loadingTimeType": { "eq": "9223372036854775808" } }""")).ShouldRefuse("UNKNOWN_ENUM_MEMBER", 400);
    }

    [Fact]
    public async Task F53_enum_values_travel_as_their_stored_numbers_at_the_root_and_inside_an_array()
    {
        // The client maps them to member names (its half is a client spec); the wire carries numbers.
        var source = Corpus.Row(Corpus.Shipment, "billing-nested");
        Corpus.Text(source, "shipmentNumber").Should().Be("S-0024");
        var types = Corpus.Elements(source, "billingLines.type").Select(value => value.AsInt32).ToList();
        types.Should().HaveCount(2);

        var answer = await (await TransportClient()).SendAsync(Corpus.Shipment, """
            [ { "match": { "shipmentNumber": { "eq": "S-0024" } } }, { "project": { "id": 1, "loadingTimeType": 1, "billingLines.type": 1 } }, { "page": { "limit": 1 } } ]
            """);

        answer.ShouldHaveIds([source.Id]);
        answer.Items[0]!["loadingTimeType"]!.GetValue<int>().Should().Be(Corpus.ValueAt(source, "loadingTimeType")!.AsInt32);
        answer.Items[0]!["billingLines"]!.AsArray().Select(line => line!["type"]!.GetValue<int>()).Should().Equal(types);
    }

    [Fact]
    public async Task F55_an_enum_value_no_member_names_arrives_as_its_own_number()
    {
        var source = Corpus.Row(Corpus.Shipment, "enum-unknown-value");
        Corpus.Text(source, "shipmentNumber").Should().Be("S-0018");

        var answer = await (await TransportClient()).SendAsync(Corpus.Shipment, """
            [ { "match": { "shipmentNumber": { "eq": "S-0018" } } }, { "project": { "id": 1, "loadingTimeType": 1 } }, { "page": { "limit": 1 } } ]
            """);

        answer.ShouldHaveIds([source.Id]);
        answer.Items[0]!["loadingTimeType"]!.GetValue<int>().Should().Be(99, "the client turns it into the string '99' of its open union");
    }

    // ── operand shapes and set elements ─────────────────────────────────────────────────────

    [Fact]
    public async Task F59_a_JSON_array_under_a_non_set_operator_is_refused_with_a_message_pointing_at_in()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "eq": ["DUP"] } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'matchCode' expects a string, not an array; use 'in' for a set.");
    }

    [Fact]
    public async Task F60_a_JSON_object_that_is_not_the_var_wrapper_is_refused()
    {
        var answer = await (await StaffClient()).MatchRawAsync(Corpus.Employee, """{ "matchCode": { "eq": { "nope": 1 } } }""");

        answer.ShouldRefuseExactly("INVALID_OPERAND").Should().Be("'matchCode' expects a string, not an object.");
    }

    [Fact]
    public async Task F61_an_element_level_error_is_labelled_path_index_in_the_message_while_the_error_path_stays_bare()
    {
        var numeric = await (await FleetClient()).MatchRawAsync(Corpus.Vehicle, """{ "fuelTankCapacity": { "in": [600, "five"] } }""");

        numeric.ShouldRefuse("INVALID_OPERAND")["path"]!.GetValue<string>().Should().Be("fuelTankCapacity");
        numeric.Messages()[0].Should().Be("'fuelTankCapacity[1]' expects an integer.");

        var enums = await (await TransportClient()).MatchRawAsync(Corpus.Shipment, """{ "loadingTimeType": { "in": ["None", "Nope"] } }""");

        enums.ShouldRefuse("UNKNOWN_ENUM_MEMBER")["path"]!.GetValue<string>().Should().Be("loadingTimeType");
        enums.Messages()[0].Should().Be("'Nope' is not a member of the enum at 'loadingTimeType[1]'.");
    }
}
