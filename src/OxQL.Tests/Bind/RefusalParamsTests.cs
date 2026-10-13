using System.Net;
using System.Text.Json;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Tests.AspNetCore;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// What a refusal carries for a client that renders it: <c>params</c> on the codes whose message
/// alone names the values (an operand of another kind, a value off a closed list, a limit, a member
/// a request does not have, a cursor that does not verify), and messages that say the mistake that
/// was made. Every member is added to what the code carried before; a request under contract 1
/// reads what it always read.
/// </summary>
public class RefusalParamsTests
{
    private const string Order = "probe.order";

    private static string Json(QueryValidationError error) => JsonSerializer.Serialize(error.Params);

    private static Task<QueryValidationError> Refused(string pipeline, string code, RequestContext? context = null, string? variables = null) =>
        BindHost.ErrorAsync(BindHost.Probe, Order, pipeline, code, context, variables);

    private static string Match(string condition) => $$"""[{ "match": {{condition}} }]""";

    // ---- INVALID_OPERAND ----------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "when": { "gte": "2026-06-01" } }""", "when", "gte", "dateTime", "string")]
    [InlineData("""{ "flag": { "eq": 1 } }""", "flag", "eq", "bool", "number")]
    [InlineData("""{ "count": { "eq": 1.5 } }""", "count", "eq", "int", "number")]
    [InlineData("""{ "count": { "eq": "many" } }""", "count", "eq", "int", "string")]
    [InlineData("""{ "count": { "gt": null } }""", "count", "gt", "int", "null")]
    [InlineData("""{ "count": { "eq": [1, 2] } }""", "count", "eq", "int", "array")]
    [InlineData("""{ "count": { "eq": { "value": 1 } } }""", "count", "eq", "int", "object")]
    [InlineData("""{ "number": { "exists": "yes" } }""", "number", "exists", "boolean", "string")]
    [InlineData("""{ "number": { "regex": 5 } }""", "number", "regex", "string", "number")]
    [InlineData("""{ "number": { "contains": true } }""", "number", "contains", "string", "boolean")]
    [InlineData("""{ "day": { "eq": "2026-06-01T00:00:00Z" } }""", "day", "eq", "date", "string")]
    [InlineData("""{ "span": { "lt": "01:00:00" } }""", "span", "lt", "timeSpan", "string")]
    public async Task An_operand_of_another_kind_names_the_path_the_operator_and_both_kinds(string condition, string path, string op, string expected, string actual)
    {
        var error = await Refused(Match(condition), Codes.InvalidOperand);

        error.Path.Should().Be(path);
        error.Stage.Should().Be(0);
        Json(error).Should().Be($$"""{"path":"{{path}}","operator":"{{op}}","expected":"{{expected}}","actual":"{{actual}}"}""");
    }

    [Fact]
    public async Task An_operand_in_a_set_is_named_under_the_sets_path_and_operator()
    {
        var error = await Refused(Match("""{ "count": { "in": [1, "two"] } }"""), Codes.InvalidOperand);

        Json(error).Should().Be("""{"path":"count","operator":"in","expected":"int","actual":"string"}""");
        error.Message.Should().Contain("count[1]");
    }

    [Theory]
    [InlineData("""{ "number": { "gt": true } }""", "gt", "operator", null)]
    [InlineData("""{ "flag": { "contains": "x" } }""", "contains", "operator", "bool")]
    [InlineData("""{ "initial": { "startsWith": "A" } }""", "startsWith", "operator", "string")]
    public async Task An_operator_that_does_not_apply_to_the_member_says_so_with_the_members_kind(string condition, string op, string reason, string? kind)
    {
        var refusal = await BindHost.RefusedAsync(BindHost.Probe, Order, Match(condition));
        var error = refusal.Errors!.First(candidate => candidate.Code == Codes.InvalidOperand);

        error.Params!["operator"].Should().Be(op);

        if (kind is not null)
        {
            error.Params["reason"].Should().Be(reason);
            error.Params["kind"].Should().Be(kind);
        }
    }

    [Theory]
    [InlineData("""{ "when": { "gte": "2026-02-30T00:00:00Z" } }""", "names no day of the calendar")]
    [InlineData("""{ "when": { "gte": "2026-06-01T25:00:00Z" } }""", "names no day of the calendar or no time of day")]
    [InlineData("""{ "day": { "eq": "2026-02-30" } }""", "is written as a date and names none")]
    public async Task A_date_that_is_written_as_one_and_does_not_exist_is_said_as_that(string condition, string says)
    {
        var error = await Refused(Match(condition), Codes.InvalidOperand);

        error.Message.Should().Contain(says);
        error.Message.Should().NotContain("with 'Z' or an offset");
    }

    [Fact]
    public async Task A_date_time_without_an_offset_is_still_asked_for_one()
    {
        (await Refused(Match("""{ "when": { "gte": "2026-06-01T00:00:00" } }"""), Codes.InvalidOperand))
            .Message.Should().Be("'when' expects an ISO-8601 date-time with 'Z' or an offset.");
    }

    // ---- closed lists -------------------------------------------------------------------------

    [Theory]
    [InlineData("\"draft\"")]
    [InlineData("99")]
    public async Task A_value_that_is_no_member_of_the_enum_carries_the_members(string operand)
    {
        var error = await Refused(Match($$"""{ "state": { "eq": {{operand}} } }"""), Codes.UnknownEnumMember);
        var names = Enum.GetNames<Model.Fixtures.OrderState>();

        error.Params!["path"].Should().Be("state");
        error.Params["operator"].Should().Be("eq");
        error.Params["values"].Should().BeEquivalentTo(names, "enum names are matched as they are written, so the list says how they are");
        JsonSerializer.Serialize(error.Params["value"]).Should().Be(operand);
    }

    [Fact]
    public async Task A_name_that_is_no_variant_carries_the_variants()
    {
        var kennel = Model.Fixtures.Variants.VariantModel.Kennel;
        var model = Model.Fixtures.Variants.VariantModel.Model;
        var error = await BindHost.ErrorAsync(model, kennel, Match("""{ "pet": { "is": "Parrot" } }"""), Codes.UnknownVariant);

        error.Params!["path"].Should().Be("pet");
        error.Params["variant"].Should().Be("Parrot");
        ((IEnumerable<string>)error.Params["variants"]!).Should().Contain(["Cat", "Hound", "Beagle"]).And.OnlyHaveUniqueItems();
        error.Message.Should().ContainAll((IEnumerable<string>)error.Params["variants"]!);

        var kind = await BindHost.ErrorAsync(model, kennel, Match("""{ "pet": { "is": 5 } }"""), Codes.InvalidOperand);

        Json(kind).Should().Be("""{"path":"pet","operator":"is","expected":"string","actual":"number"}""");
    }

    // ---- limits -------------------------------------------------------------------------------

    public static TheoryData<string, string, string, int, int> Limits => new()
    {
        // pipeline, code, limit, max, count
        { "[" + string.Join(",", Enumerable.Repeat("""{ "match": { "count": 1 } }""", 21)) + "]", Codes.MaxPipelineStagesExceeded, "maxPipelineStages", 20, 21 },
        { "[" + string.Join(",", Enumerable.Range(0, 6).Select(index => $$"""{ "unwind": { "path": "items", "as": "i{{index}}" } }""")) + "]", Codes.MaxUnwindStagesExceeded, "maxUnwindStages", 5, 6 },
        { """[{ "project": { """ + string.Join(",", Enumerable.Range(0, 501).Select(index => $"\"p{index}\": 1")) + " } }]", Codes.MaxProjectionFieldsExceeded, "maxProjectionFields", 500, 501 },
        { """[{ "match": { "and": [""" + string.Join(",", Enumerable.Repeat("""{ "count": 1 }""", 201)) + "] } }]", Codes.MaxConditionsExceeded, "maxConditions", 200, 201 },
        { """[{ "match": { "number": { "regex": "^""" + new string('a', 200) + "\" } } }]", Codes.RegexTooLong, "regexMaxLength", 200, 201 },
        { """[{ "page": { "limit": 501 } }]""", Codes.PageSizeExceeded, "maxPageSize", 500, 501 },
        { """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "limit": 101 } }]""", Codes.LookupLimitExceeded, "maxLookupLimit", 100, 101 },
    };

    [Theory]
    [MemberData(nameof(Limits))]
    public async Task A_request_over_a_limit_names_the_limit_as_health_does_with_its_value_and_what_was_asked(string pipeline, string code, string limit, int max, int count)
    {
        var entity = code == Codes.LookupLimitExceeded ? "probe.customer" : Order;
        var refusal = await BindHost.RefusedAsync(BindHost.Probe, entity, pipeline);
        var error = refusal.Errors!.FirstOrDefault(candidate => candidate.Code == code);

        error.Should().NotBeNull(BindHost.Describe(refusal));
        JsonSerializer.Serialize(error!.Params).Should().Be($$"""{"limit":"{{limit}}","max":{{max}},"count":{{count}}}""");
    }

    [Fact]
    public async Task More_variables_than_the_host_binds_carry_the_limit()
    {
        var variables = "{" + string.Join(",", Enumerable.Range(0, 65).Select(index => $"\"v{index}\": 1")) + "}";
        var error = await Refused("[]", Codes.MaxVariablesExceeded, variables: variables);

        Json(error).Should().Be("""{"limit":"maxVariables","max":64,"count":65}""");
    }

    [Fact]
    public async Task The_limits_carry_no_params_under_contract_1_where_the_answer_is_what_it_was()
    {
        var context = BindHost.Context(contract: 1);
        var stages = "[" + string.Join(",", Enumerable.Repeat("""{ "match": { "count": 1 } }""", 21)) + "]";

        (await Refused(stages, Codes.MaxPipelineStagesExceeded, context)).Params.Should().BeNull();
        (await Refused("""[{ "page": { "limit": 501 } }]""", Codes.PageSizeExceeded, context)).Params.Should().BeNull();
        (await Refused(Match("""{ "count": { "eq": "many" } }"""), Codes.InvalidOperand, context)).Params.Should().BeNull();
        (await Refused(Match("""{ "state": { "eq": "draft" } }"""), Codes.UnknownEnumMember, context)).Params.Should().BeNull();
    }

    // ---- a member a request does not have ---------------------------------------------------------

    [Fact]
    public async Task A_member_a_request_does_not_have_is_named_with_why()
    {
        var unknown = await BindHost.BindAsync(BindHost.Probe, BindHost.Parse($$"""{ "entityType": "{{Order}}", "page": { "limit": 1 }, "sort": [], "pipeline": [] }"""));
        var first = ((BindOutcome.Failed)unknown).Refusal.Errors!.Single(error => error.Code == Codes.UnknownRequestMember);

        Json(first).Should().Be("""{"reason":"unknown","members":["page","sort"],"member":"page"}""");

        var keyed = await BindHost.BindAsync(BindHost.Probe, BindHost.Parse($$"""{ "entityType": "{{Order}}", "keyedBy": { "path": "id", "keys": [] }, "pipeline": [] }"""));
        var second = ((BindOutcome.Failed)keyed).Refusal.Errors!.Single(error => error.Code == Codes.UnknownRequestMember);

        Json(second).Should().Be("""{"reason":"internal","members":["keyedBy"],"member":"keyedBy"}""");
    }

    // ---- options, any, dateTrunc: the mistake that was made ----------------------------------------

    [Theory]
    [InlineData("""{ "number": { "eq": "a", "options": { "caseSensitive": "true" } } }""", "'caseSensitive' is true or false.", "caseSensitive", "kind")]
    [InlineData("""{ "number": { "eq": "a", "options": { "foo": 1 } } }""", "'foo' is not an option; a condition's option is caseSensitive.", "foo", "unknown")]
    [InlineData("""{ "number": { "eq": "a", "options": true } }""", "'options' is an object of options: { \"caseSensitive\": true }.", "options", "kind")]
    public async Task An_option_written_wrongly_says_which_mistake_it_is(string condition, string message, string option, string reason)
    {
        var error = await Refused(Match(condition), Codes.OptionNotApplicable);

        error.Message.Should().Be(message);
        Json(error).Should().Be($$"""{"option":"{{option}}","reason":"{{reason}}"}""");
    }

    [Fact]
    public async Task An_any_without_a_condition_is_named_any()
    {
        var error = await Refused(Match("""{ "items": { "any": {} } }"""), Codes.EmptyLogicalGroup);

        error.Message.Should().StartWith("'any' has no conditions");
        error.Path.Should().Be("items");

        // The group a caller did write is still named as written.
        (await Refused(Match("""{ "items": { "any": { "and": [] } } }"""), Codes.EmptyLogicalGroup)).Message.Should().Be("'and' has no conditions.");
    }

    [Fact]
    public async Task A_dateTrunc_over_a_member_that_is_no_date_says_what_the_member_is()
    {
        var text = await Refused("""[{ "group": { "by": [{ "dateTrunc": { "path": "number", "unit": "month" }, "as": "m" }], "fields": {} } }]""", Codes.InvalidAggregateArgument);

        text.Message.Should().Be("'number' is a string; dateTrunc takes a date or a dateTime.");
    }

    // ---- cursors ------------------------------------------------------------------------------

    [Fact]
    public void A_cursor_that_does_not_verify_says_which_of_the_three_it_is()
    {
        var codec = BindHost.Cursors;
        var cursor = codec.Encode(new CursorPayload("f", PagingMode.Offset, [], 10));

        codec.Decode(cursor, "f", out var none).Should().NotBeNull();
        none.Should().BeNull();

        codec.Decode(cursor, "g", out var query).Should().BeNull();
        query.Should().Be(CursorCodec.OtherQuery);

        new CursorCodec("another-key-of-another-host").Decode(cursor, "f", out var signature).Should().BeNull();
        signature.Should().Be(CursorCodec.Signature);

        var dot = cursor.IndexOf('.');

        codec.Decode(cursor[..dot] + "x" + cursor[dot..], "f", out var altered).Should().BeNull();
        altered.Should().Be(CursorCodec.Signature);

        foreach (var garbage in new[] { "", "nodot", ".x", "x.", "!!.??" })
        {
            codec.Decode(garbage, "f", out var malformed).Should().BeNull();
            malformed.Should().Be(CursorCodec.Malformed, garbage);
        }
    }

    [Fact]
    public async Task CURSOR_INVALID_carries_the_reason_under_contract_2_and_reads_as_before_under_contract_1()
    {
        var foreign = BindHost.Cursors.Encode(new CursorPayload("another-query", PagingMode.Offset, [], 10));

        var query = await Refused($$"""[{ "page": { "limit": 1, "cursor": "{{foreign}}" } }]""", Codes.CursorInvalid);
        var malformed = await Refused("""[{ "page": { "limit": 1, "cursor": "garbage" } }]""", Codes.CursorInvalid);
        var forged = await Refused($$"""[{ "page": { "limit": 1, "cursor": "{{new CursorCodec("another-key").Encode(new CursorPayload("f", PagingMode.Offset, [], 10))}}" } }]""", Codes.CursorInvalid);

        Json(query).Should().Be("""{"reason":"query"}""");
        Json(malformed).Should().Be("""{"reason":"malformed"}""");
        Json(forged).Should().Be("""{"reason":"signature"}""");
        query.Message.Should().StartWith("The cursor was issued for another query");

        var legacy = await Refused("""[{ "page": { "limit": 1, "cursor": "garbage" } }]""", Codes.CursorInvalid, BindHost.Context(contract: 1));

        legacy.Params.Should().BeNull();
        legacy.Message.Should().Be("The cursor is not valid for this query: it was issued for another query, was altered, or is malformed.");
    }

    // ---- the host's own refusals ------------------------------------------------------------------

    [Fact]
    public async Task A_batch_over_its_limit_and_a_member_a_batch_does_not_have_carry_params()
    {
        using var host = new SampleHost();
        var query = """{ "entityType": "probe.order", "pipeline": [] }""";

        var over = await SampleHost.Body(await host.Client().PostAsync("/OxQL/batch", SampleHost.Json($$"""{ "queries": [{{string.Join(",", Enumerable.Repeat(query, 11))}}] }""")));

        over!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.BatchTooLarge);
        over["errors"]![0]!["params"]!.ToJsonString().Should().Be("""{"limit":"maxBatchQueries","max":10,"count":11}""");

        var member = await SampleHost.Body(await host.Client().PostAsync("/OxQL/batch", SampleHost.Json($$"""{ "queries": [{{query}}], "strict": true }""")));

        member!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
        member["errors"]![0]!["params"]!.ToJsonString().Should().Be("""{"reason":"unknown","members":["strict"],"member":"strict"}""");

        // A caller without the header reads the limit as it always did.
        var legacy = await SampleHost.Body(await host.Client(contract: null).PostAsync("/OxQL/batch", SampleHost.Json($$"""{ "queries": [{{string.Join(",", Enumerable.Repeat(query, 11))}}] }""")));

        legacy!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.BatchTooLarge);
        legacy["errors"]![0]!.AsObject().ContainsKey("params").Should().BeFalse();
    }

    [Fact]
    public async Task A_body_over_the_hosts_size_names_the_limit_under_contract_2_only()
    {
        using var host = new SampleHost(options => options.Limits.MaxRequestBytes = 200);
        var body = $$"""{ "entityType": "probe.order", "pipeline": [{ "match": { "number": "{{new string('x', 300)}}" } }] }""";

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(body));
        var answer = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        answer!["errors"]![0]!["params"]!["limit"]!.GetValue<string>().Should().Be("maxRequestBytes");
        answer["errors"]![0]!["params"]!["max"]!.GetValue<int>().Should().Be(200);
        answer["errors"]![0]!["params"]!["count"]!.GetValue<int>().Should().BeGreaterThan(300);

        var legacy = await SampleHost.Body(await host.Client(contract: null).PostAsync("/OxQL/query", SampleHost.Json(body)));

        legacy!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.RequestTooLarge);
        legacy["errors"]![0]!.AsObject().ContainsKey("params").Should().BeFalse();
    }
}
