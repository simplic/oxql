using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The bucket test <c>{ path: { "dateTrunc": { unit, timezone?, weekStart?, eq } } }</c> (DG2): a
/// condition on the bucket a group key of the same unit, zone and week start puts a date in. It binds
/// to one half-open range on the stored date, which an index bounds; nothing of it is an expression.
/// <c>eq</c> is any instant of the bucket (the group's key is one), a variable that holds one, on a
/// date without a time also a day, or null for the rows without a date. That the range is the
/// database's own bucket is held against a server in <c>DateBucketServerTests</c>.
/// </summary>
public class DateTruncConditionTests
{
    private const string Order = "probe.order";
    private const string Berlin = "Europe/Berlin";

    private static string Match(string path, string operand) => $$"""[{ "match": { "{{path}}": { "dateTrunc": {{operand}} } } }]""";

    private static async Task<BoundCondition.Leaf> Leaf(string path, string operand, string? variables = null)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, Match(path, operand), variablesJson: variables);

        return bound.Stages.OfType<BoundStage.Match>().Single().Condition.Should().BeOfType<BoundCondition.Leaf>().Subject;
    }

    private static async Task<(DateTime From, DateTime Until)> Range(string path, string operand, string? variables = null)
    {
        var leaf = await Leaf(path, operand, variables);
        var range = leaf.Operand.Should().BeOfType<BoundOperand.Range>().Subject;

        leaf.Op.Should().Be("dateTrunc");
        leaf.IsSemiJoin.Should().BeFalse();

        return (range.From.ToUniversalTime(), range.Until.ToUniversalTime());
    }

    private static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    // ---- the range ---------------------------------------------------------------------------

    [Fact]
    public async Task A_bucket_is_one_range_on_the_stored_date_from_its_start_up_to_the_next_buckets_start()
    {
        var leaf = await Leaf("when", $$"""{ "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-02-28T23:00:00Z" }""");
        var filter = MongoCompiler.Filter(leaf);

        // March 2026 in Berlin: from midnight of the first under winter time to midnight of 1 April under summer time.
        filter.Equals(new BsonDocument(leaf.Path.Storage!, new BsonDocument
        {
            ["$gte"] = new BsonDateTime(Utc("2026-02-28T23:00:00Z")),
            ["$lt"] = new BsonDateTime(Utc("2026-03-31T22:00:00Z")),
        })).Should().BeTrue(filter.ToJson());
        filter.ToJson().Should().NotContain("$expr").And.NotContain("$dateTrunc");
    }

    [Theory]
    [InlineData("2026-02-28T23:00:00Z")]
    [InlineData("2026-03-15T12:34:56.789Z")]
    [InlineData("2026-03-29T01:30:00Z")]
    [InlineData("2026-03-31T21:59:59.999Z")]
    [InlineData("2026-03-31T23:59:59+02:00")]
    public async Task Any_instant_of_the_bucket_names_it_so_the_groups_key_does(string instant)
    {
        (await Range("when", $$"""{ "unit": "month", "timezone": "{{Berlin}}", "eq": "{{instant}}" }"""))
            .Should().Be((Utc("2026-02-28T23:00:00Z"), Utc("2026-03-31T22:00:00Z")));
    }

    [Theory]
    [InlineData("year", "2026-01-01T00:00:00Z", "2027-01-01T00:00:00Z")]
    [InlineData("quarter", "2026-07-01T00:00:00Z", "2026-10-01T00:00:00Z")]
    [InlineData("month", "2026-08-01T00:00:00Z", "2026-09-01T00:00:00Z")]
    [InlineData("week", "2026-08-10T00:00:00Z", "2026-08-17T00:00:00Z")]
    [InlineData("day", "2026-08-13T00:00:00Z", "2026-08-14T00:00:00Z")]
    [InlineData("hour", "2026-08-13T14:00:00Z", "2026-08-13T15:00:00Z")]
    [InlineData("minute", "2026-08-13T14:25:00Z", "2026-08-13T14:26:00Z")]
    [InlineData("second", "2026-08-13T14:25:36Z", "2026-08-13T14:25:37Z")]
    public async Task Every_unit_has_its_bucket_and_the_zone_defaults_to_utc_as_a_group_keys_does(string unit, string from, string until)
    {
        // Thursday 13 August 2026, 14:25:36.5 UTC.
        (await Range("when", $$"""{ "unit": "{{unit}}", "eq": "2026-08-13T14:25:36.500Z" }""")).Should().Be((Utc(from), Utc(until)));
        (await Range("when", $$"""{ "unit": "{{unit}}", "timezone": "UTC", "eq": "2026-08-13T14:25:36.500Z" }""")).Should().Be((Utc(from), Utc(until)));
        (await Range("when", $$"""{ "unit": "{{unit}}", "timezone": null, "weekStart": null, "eq": "2026-08-13T14:25:36.500Z" }""")).Should().Be((Utc(from), Utc(until)));
    }

    [Theory]
    [InlineData(null, "2026-08-10T00:00:00Z")]
    [InlineData("monday", "2026-08-10T00:00:00Z")]
    [InlineData("Sun", "2026-08-09T00:00:00Z")]
    [InlineData("thursday", "2026-08-13T00:00:00Z")]
    [InlineData("FRIDAY", "2026-08-07T00:00:00Z")]
    public async Task A_week_starts_on_monday_unless_it_says_otherwise_in_any_spelling_a_group_key_takes(string? weekStart, string from)
    {
        var written = weekStart is null ? "" : $"\"weekStart\": \"{weekStart}\", ";
        var range = await Range("when", $$"""{ "unit": "week", {{written}}"eq": "2026-08-13T14:25:36Z" }""");

        range.From.Should().Be(Utc(from));
        range.Until.Should().Be(Utc(from).AddDays(7));
    }

    [Fact]
    public async Task The_day_of_a_change_of_the_clock_is_23_or_25_hours_long_and_a_week_start_is_ignored_on_another_unit()
    {
        var spring = await Range("when", $$"""{ "unit": "day", "timezone": "{{Berlin}}", "weekStart": "sunday", "eq": "2026-03-29T10:00:00Z" }""");
        var autumn = await Range("when", $$"""{ "unit": "day", "timezone": "{{Berlin}}", "eq": "2026-10-25T10:00:00Z" }""");

        spring.Should().Be((Utc("2026-03-28T23:00:00Z"), Utc("2026-03-29T22:00:00Z")));
        (spring.Until - spring.From).TotalHours.Should().Be(23);
        autumn.Should().Be((Utc("2026-10-24T22:00:00Z"), Utc("2026-10-25T23:00:00Z")));
        (autumn.Until - autumn.From).TotalHours.Should().Be(25);

        // The hour the clock shows twice is two buckets, as it is two groups.
        (await Range("when", $$"""{ "unit": "hour", "timezone": "{{Berlin}}", "eq": "2026-10-25T00:30:00Z" }""")).Should().Be((Utc("2026-10-25T00:00:00Z"), Utc("2026-10-25T01:00:00Z")));
        (await Range("when", $$"""{ "unit": "hour", "timezone": "{{Berlin}}", "eq": "2026-10-25T01:30:00Z" }""")).Should().Be((Utc("2026-10-25T01:00:00Z"), Utc("2026-10-25T02:00:00Z")));
    }

    // ---- a date without a time ----------------------------------------------------------------

    [Fact]
    public async Task On_a_date_without_a_time_the_bucket_is_a_range_of_days_stored_as_their_midnight_in_utc()
    {
        // The key a group gives March 2026 in Berlin is the instant of its first midnight there; the days are the calendar's.
        var march = (Utc("2026-03-01T00:00:00Z"), Utc("2026-04-01T00:00:00Z"));

        (await Range("day", $$"""{ "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-02-28T23:00:00Z" }""")).Should().Be(march);
        (await Range("day", $$"""{ "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-03-17" }""")).Should().Be(march, "a day names the bucket it lies in");
        (await Range("day", """{ "unit": "month", "timezone": "America/New_York", "eq": "2026-03-01T05:00:00Z" }""")).Should().Be(march);
        (await Range("day", """{ "unit": "month", "eq": "2026-03-01T00:00:00Z" }""")).Should().Be(march);
        (await Range("day", $$"""{ "unit": "day", "timezone": "{{Berlin}}", "eq": "2026-03-04T23:00:00Z" }""")).Should().Be((Utc("2026-03-05T00:00:00Z"), Utc("2026-03-06T00:00:00Z")));
        (await Range("day", $$"""{ "unit": "week", "timezone": "{{Berlin}}", "eq": "2026-03-05" }""")).Should().Be((Utc("2026-03-02T00:00:00Z"), Utc("2026-03-09T00:00:00Z")));
    }

    [Fact]
    public async Task Below_a_day_a_date_without_a_time_is_in_the_bucket_its_midnight_is_in_and_in_no_other()
    {
        // A group by the hour of a date gives every day the hour of its midnight in the zone.
        (await Range("day", $$"""{ "unit": "hour", "timezone": "{{Berlin}}", "eq": "2026-03-04T23:00:00Z" }""")).Should().Be((Utc("2026-03-05T00:00:00Z"), Utc("2026-03-06T00:00:00Z")));

        // Noon is nobody's midnight: the range holds no day, so the condition matches nothing.
        var noon = await Range("day", $$"""{ "unit": "hour", "timezone": "{{Berlin}}", "eq": "2026-03-05T11:00:00Z" }""");

        noon.From.Should().Be(noon.Until);
    }

    // ---- the operand --------------------------------------------------------------------------

    [Fact]
    public async Task The_operand_may_be_a_variable_and_null_is_the_group_of_the_rows_without_a_date()
    {
        (await Range("when", """{ "unit": "day", "eq": { "$var": "at" } }""", """{ "at": "2026-08-13T14:25:36Z" }"""))
            .Should().Be((Utc("2026-08-13T00:00:00Z"), Utc("2026-08-14T00:00:00Z")));

        foreach (var (operand, variables) in new[] { ("""{ "unit": "day", "eq": null }""", null), ("""{ "unit": "day", "eq": { "$var": "at" } }""", """{ "at": null }""") })
        {
            var leaf = await Leaf("when", operand, variables);

            leaf.Operand.Should().BeOfType<BoundOperand.Null>();
            MongoCompiler.Filter(leaf).Equals(new BsonDocument(leaf.Path.Storage!, BsonNull.Value)).Should().BeTrue("null and absent are one group");
        }

        (await BindHost.ErrorAsync(BindHost.Probe, Order, Match("when", """{ "unit": "day", "eq": { "$var": "at" } }"""), Codes.UnboundVariable)).Path.Should().Be("when");
        (await BindHost.ErrorAsync(BindHost.Probe, Order, Match("when", """{ "unit": "day", "eq": { "$var": "at" } }"""), Codes.InvalidVariable, variablesJson: """{ "at": ["2026-08-13T14:25:36Z"] }"""))
            .Message.Should().Contain("holds an array");
    }

    public static TheoryData<string, string, string, string> Refusals => new()
    {
        { "when", "\"2026-08-13T14:25:36Z\"", Codes.InvalidOperand, "notAnObject" },
        { "when", """{ "unit": "day" }""", Codes.InvalidOperand, "eqRequired" },
        { "when", """{ "unit": "day", "eq": "2026-08-13T14:25:36Z", "binSize": 2 }""", Codes.InvalidOperand, "unknownMember" },
        { "when", """{ "unit": "day", "path": "when", "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidOperand, "unknownMember" },
        { "when", """{ "unit": "day", "timezone": 2, "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidOperand, "unknownMember" },
        { "when", """{ "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidDateTruncUnit, "unit" },
        { "when", """{ "unit": "fortnight", "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidDateTruncUnit, "unit" },
        { "when", """{ "unit": "Week", "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidDateTruncUnit, "unit" },
        { "when", """{ "unit": "week", "weekStart": "funday", "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidDateTruncUnit, "weekStart" },
        { "when", """{ "unit": "day", "timezone": "Mars/Olympus", "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidTimezone, "timezone" },
        { "when", """{ "unit": "day", "timezone": "+02:00", "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidTimezone, "timezone" },
        { "when", """{ "unit": "day", "eq": "2026-08-13" }""", Codes.InvalidOperand, "eq" },
        { "when", """{ "unit": "day", "eq": "2026-08-13T14:25:36" }""", Codes.InvalidOperand, "eq" },
        { "when", """{ "unit": "day", "eq": 20260813 }""", Codes.InvalidOperand, "eq" },
        { "when", """{ "unit": "day", "eq": ["2026-08-13T14:25:36Z"] }""", Codes.InvalidOperand, "eq" },
        { "day", """{ "unit": "day", "eq": "13.08.2026" }""", Codes.InvalidOperand, "eq" },
        { "when", """{ "unit": "year", "eq": "9999-06-01T00:00:00Z" }""", Codes.InvalidOperand, "range" },
        { "tags", """{ "unit": "day", "eq": "2026-08-13T14:25:36Z" }""", Codes.InvalidOperand, "" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task What_the_operand_cannot_be_is_refused_with_the_reason_in_params(string path, string operand, string code, string reason)
    {
        var refusal = await BindHost.RefusedAsync(BindHost.Probe, Order, Match(path, operand));
        var error = refusal.Errors!.Should().ContainSingle(BindHost.Describe(refusal)).Subject;

        refusal.Status.Should().Be(400);
        error.Code.Should().Be(code, error.Message);
        error.Stage.Should().Be(0);
        error.Path.Should().Be(path);

        if (reason.Length > 0)
        {
            error.Params.Should().NotBeNull(error.Message);
            error.Params!["op"].Should().Be("dateTrunc");
            error.Params["reason"].Should().Be(reason, error.Message);
        }
    }

    [Theory]
    [InlineData("number", "a string")]
    [InlineData("count", "an int")]
    [InlineData("id", "a guid")]
    [InlineData("flag", "a bool")]
    [InlineData("span", "a timeSpan")]
    public async Task It_applies_to_a_date_and_a_date_time_and_to_no_other_kind(string path, string kind)
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, Match(path, """{ "unit": "day", "eq": "2026-08-13T14:25:36Z" }"""), Codes.InvalidOperand);

        error.Message.Should().Be($"'dateTrunc' does not apply to {kind}.");
        Basis.OfCode[error.Code].Should().Be(Basis.Model);
    }

    [Fact]
    public async Task Through_a_collection_that_is_not_unwound_it_is_refused_and_inside_any_and_after_the_unwind_it_binds()
    {
        const string Operand = """{ "unit": "day", "eq": "2026-08-13T14:25:36Z" }""";
        var model = BindHost.Vendored("logistics.json");

        var error = await BindHost.ErrorAsync(model, "logistics.shipment", $$"""[{ "match": { "billingLines.date": { "dateTrunc": {{Operand}} } } }]""", Codes.InvalidOperand);

        error.Params!["reason"].Should().Be("underCollection");
        error.Message.Should().Contain("'any'").And.Contain("unwind");

        var any = await BindHost.BoundAsync(model, "logistics.shipment", $$"""[{ "match": { "billingLines": { "any": { "date": { "dateTrunc": {{Operand}} } } } } }]""");
        var compiled = MongoCompiler.Filter(any.Stages.OfType<BoundStage.Match>().Single().Condition);

        compiled.ToJson().Should().Contain("$elemMatch").And.Contain("$gte").And.Contain("$lt");

        var unwound = await BindHost.BoundAsync(model, "logistics.shipment", $$"""[{ "unwind": { "path": "billingLines", "as": "line" } }, { "match": { "line.date": { "dateTrunc": {{Operand}} } } }]""");

        unwound.Stages.OfType<BoundStage.Match>().Single().Condition.Should().BeOfType<BoundCondition.Leaf>().Which.Operand.Should().BeOfType<BoundOperand.Range>();
    }

    // ---- where it may stand -------------------------------------------------------------------

    [Fact]
    public async Task It_nests_under_or_and_not_like_every_comparison()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """
            [{ "match": { "or": [ { "when": { "dateTrunc": { "unit": "month", "eq": "2026-03-10T00:00:00Z" } } },
                                  { "not": { "day": { "dateTrunc": { "unit": "year", "eq": "2026-03-10" } } } } ] } }]
            """);
        var filter = MongoCompiler.Filter(bound.Stages.OfType<BoundStage.Match>().Single().Condition);

        filter["$or"].AsBsonArray.Should().HaveCount(2);
        filter["$or"][1]["$nor"][0].AsBsonDocument.GetElement(0).Value.AsBsonDocument.Names.Should().Equal("$gte", "$lt");
    }

    [Fact]
    public async Task In_the_requests_filter_it_runs_where_its_path_is_in_the_row_before_a_group_on_the_member_and_after_it_on_the_key()
    {
        var request = BindHost.Parse($$"""
            { "entityType": "{{Order}}",
              "filter": [ { "id": "group:0", "where": { "when": { "dateTrunc": { "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-02-28T23:00:00Z" } } } },
                          { "id": "having", "where": { "bucket": { "dateTrunc": { "unit": "year", "timezone": "{{Berlin}}", "eq": "2026-02-28T23:00:00Z" } } } } ],
              "pipeline": [ { "group": { "by": [ { "dateTrunc": { "path": "when", "unit": "day", "timezone": "{{Berlin}}" }, "as": "bucket" } ], "fields": { "n": { "count": true } } } } ] }
            """);
        var outcome = await BindHost.BindAsync(BindHost.Probe, request);
        var bound = outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome)).Subject.Pipeline;

        bound.Stages.Select(stage => bound.FilterOf(stage) is { } label ? "filter:" + label : stage.GetType().Name.ToLowerInvariant())
            .Should().Equal("filter:group:0", "group", "filter:having", "sort", "page");
        bound.Filters[0].Placed.Should().ContainSingle().Which.Should().BeEquivalentTo(new FilterPlacement(0, ["when"], false));
        bound.Filters[1].Placed.Should().ContainSingle().Which.Should().BeEquivalentTo(new FilterPlacement(1, ["bucket"], true));
    }

    [Fact]
    public async Task Under_another_services_alias_the_owner_binds_it_and_is_sent_the_operand_with_its_variables_bound()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """
            [{ "resolve": { "path": "contactNumber", "as": "contact" } },
             { "match": { "contact.since": { "dateTrunc": { "unit": "month", "timezone": "Europe/Berlin", "eq": { "$var": "at" } } } } }]
            """, variablesJson: """{ "at": "2026-03-10T00:00:00Z" }""");
        var leaf = bound.Stages.OfType<BoundStage.Match>().Single().Condition.Should().BeOfType<BoundCondition.Leaf>().Subject;

        leaf.IsSemiJoin.Should().BeTrue();
        leaf.Op.Should().Be("dateTrunc");
        leaf.Operand.Should().BeOfType<BoundOperand.Raw>().Which.Value.GetRawText().Should().Be("""{"unit":"month","timezone":"Europe/Berlin","eq":"2026-03-10T00:00:00Z"}""");
        bound.HasSemiJoin.Should().BeTrue();

        var unbound = await BindHost.ErrorAsync(BindHost.Probe, Order, """
            [{ "resolve": { "path": "contactNumber", "as": "contact" } },
             { "match": { "contact.since": { "dateTrunc": { "unit": "month", "eq": { "$var": "at" } } } } }]
            """, Codes.UnboundVariable);

        unbound.Stage.Should().Be(1);
    }

    [Fact]
    public async Task In_a_joins_filter_it_binds_against_the_target_and_an_option_beside_it_does_not_apply()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """
            [{ "resolve": { "path": "customerId", "as": "c", "filter": { "createDateTime": { "dateTrunc": { "unit": "year", "eq": "2026-06-01T00:00:00Z" } } } } }]
            """);
        var filter = bound.Stages.OfType<BoundStage.Resolve>().Single().Filter.Should().BeOfType<BoundCondition.Leaf>().Subject;

        filter.Operand.Should().BeOfType<BoundOperand.Range>().Which.From.ToUniversalTime().Should().Be(Utc("2026-01-01T00:00:00Z"));

        var error = await BindHost.ErrorAsync(BindHost.Probe, Order,
            """[{ "match": { "when": { "dateTrunc": { "unit": "day", "eq": "2026-08-13T14:25:36Z" }, "options": { "caseSensitive": true } } } }]""", Codes.OptionNotApplicable);

        error.Path.Should().Be("when");
    }

    // ---- what it is bound to ------------------------------------------------------------------

    [Fact]
    public async Task A_cursor_is_bound_to_the_bucket_not_to_how_it_was_named()
    {
        async Task<string> Fingerprint(string operand) => (await BindHost.BoundAsync(BindHost.Probe, Order, Match("when", operand))).Fingerprint;

        var key = await Fingerprint($$"""{ "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-02-28T23:00:00Z" }""");

        (await Fingerprint($$"""{ "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-03-20T09:00:00+01:00" }""")).Should().Be(key, "the same bucket");
        (await Fingerprint($$"""{ "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-04-20T09:00:00Z" }""")).Should().NotBe(key, "another bucket");
        (await Fingerprint("""{ "unit": "month", "eq": "2026-03-20T09:00:00Z" }""")).Should().NotBe(key, "the month of another zone");
    }

    [Fact]
    public async Task Contract_1_does_not_have_the_operator_and_answers_what_it_always_answered()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, Match("when", """{ "unit": "day", "eq": "2026-08-13T14:25:36Z" }"""), Codes.UnknownOperator, BindHost.Context(contract: 1));

        error.Message.Should().Be("'dateTrunc' is not an operator.");
        error.Params.Should().BeNull();
    }

    // ---- explain ------------------------------------------------------------------------------

    [Theory]
    [InlineData("when", true)]
    [InlineData("day", true)]
    [InlineData("createDateTime", true)]
    [InlineData("number", false)]
    [InlineData("count", false)]
    [InlineData("span", false)]
    public async Task Explain_lists_it_last_among_what_a_date_can_be_filtered_by_and_for_no_other_kind(string path, bool admitted)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, "[]");
        var operators = ExplainFacts.Can(bound.FinalShape, path).Can["filter"]!.AsArray().Select(op => op!.GetValue<string>()).ToList();

        operators.Contains("dateTrunc").Should().Be(admitted, string.Join(",", operators));

        if (admitted)
            operators.Should().Equal("eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "exists", "dateTrunc");

        // It has no flag of its own: the flag set of a date is what it was.
        ExplainTypes.FlagsAt(bound.FinalShape, path).ToJson()["operators"]!.AsArray().Select(op => op!.GetValue<string>()).Should().NotContain("dateTrunc");
    }

    [Fact]
    public async Task Explain_does_not_list_it_for_a_date_under_a_collection_that_is_not_unwound_and_lists_it_for_the_groups_key()
    {
        var shipment = await BindHost.BoundAsync(BindHost.Vendored("logistics.json"), "logistics.shipment", "[]");

        ExplainFacts.CanOf(ExplainTypes.FlagsAt(shipment.FinalShape, "billingLines.date"), aggregable: true, Kind.DateTime)["filter"]!.AsArray().Select(op => op!.GetValue<string>())
            .Should().NotContain("dateTrunc").And.Contain("gte");

        var grouped = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "group": { "by": [ { "dateTrunc": { "path": "when", "unit": "month" }, "as": "month" } ], "fields": { "n": { "count": true } } } }]""");

        ExplainFacts.Can(grouped.FinalShape, "month").Can["filter"]!.AsArray().Select(op => op!.GetValue<string>()).Should().Contain("dateTrunc");
        ExplainFacts.Can(grouped.FinalShape, "n").Can["filter"]!.AsArray().Select(op => op!.GetValue<string>()).Should().NotContain("dateTrunc");
    }
}
