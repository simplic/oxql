using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Transport;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Variables;

/// <summary>
/// Area S: variables. The engine treats <c>variables</c> as an untyped bag bound by name: a
/// <c>{"$var": name}</c> operand takes the bag's value and is then checked exactly as a literal
/// in its place would be. Names match case-insensitively; an unbound reference, an object value,
/// a wrapper with extra members and a bag above <c>MaxVariables</c> are refused.
/// <para>
/// Ported from the legacy <c>variables</c> battery on <c>staff.employee</c> (and
/// <c>transport.shipment</c> for enums). Its wire and client-refusal halves (the declared set, the
/// conversion for the first use, the per-path client checks) live with the client's specs; each
/// case that had an engine half is here as that half.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class VariablesTests
{
    private const string Page = """{ "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } }""";

    private static Task<LabClient> Staff() => Lab.ClientAsync(LabService.Staff);

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static IReadOnlyList<Guid> Dup() => Corpus.IdsOf(Corpus.EmployeesWithMatchCode("DUP", StringOrder.Binary));

    private static string Match(string condition) => $$"""[ { "match": {{condition}} }, {{Page}} ]""";

    [Fact]
    public async Task S01_S02_S03_S24_the_client_bytes_with_a_declared_variable_and_an_inline_literal_select_the_rows_the_oracle_picks()
    {
        var expected = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Text(row, "matchCode") == "DUP" && Corpus.Number(row, "children") is { } children && Order.CompareDecimal(children, "2") == 0);
        expected.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(Dup().Count);

        // The bytes the client compiles for a declared variable beside a literal.
        var answer = await (await Staff()).QueryAsync(
            """{"entityType":"staff.employee","pipeline":[{"match":{"and":[{"matchCode":{"eq":"DUP"}},{"children":{"eq":{"$var":"kids"}}}]}},{"sort":[{"id":"asc"}]},{"page":{"limit":10,"includeTotalCount":true}}],"variables":{"kids":2}}""");

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);

        // S24: the variable alone selects exactly what the literal selects.
        var dup = await (await Staff()).SendAsync(Corpus.Employee, $$"""[ { "match": { "matchCode": { "eq": { "$var": "code" } } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } } ]""", new JsonObject { ["code"] = "DUP" });
        dup.ShouldHaveIds(Dup()).ShouldHaveTotal(Dup().Count);
    }

    [Theory]
    [InlineData("S07", "birthday", "gte", "\"2026-06-15T23:30:00.000Z\"")]
    [InlineData("S07", "birthday", "gte", "\"1990-01-01T00:00:00Z\"")]
    [InlineData("S08", "birthday", "lt", "\"1985-04-12T00:00:00.000Z\"")]
    [InlineData("S07", "birthday", "gte", "\"1980-01-01T00:00:00Z\"")]
    public async Task S07_S08_a_variable_on_a_temporal_path_answers_exactly_what_the_literal_answers(string caseId, string path, string op, string value)
    {
        var client = await Staff();

        var literal = await client.SendAsync(Corpus.Employee, Match($$"""{ "{{path}}": { "{{op}}": {{value}} } }"""));
        var variable = await client.SendAsync(Corpus.Employee, Match($$"""{ "{{path}}": { "{{op}}": { "$var": "v" } } }"""), JsonNode.Parse($$"""{ "v": {{value}} }"""));

        var bound = DateTime.Parse(JsonNode.Parse(value)!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var expected = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Date(row, path) is { } date && (op == "gte" ? date >= bound : date < bound));

        literal.ShouldHaveIds(expected, caseId);
        variable.ShouldHaveIds(expected, $"{caseId}: the variable disagreed with the literal at {value}");
    }

    [Fact]
    public async Task S07_S11_an_enum_member_name_through_a_variable_answers_what_the_literal_answers_in_a_typed_or_a_raw_stage()
    {
        var expected = Corpus.IdsOf(Corpus.ShipmentsWithLoadingTimeType(LoadingDateTimeType.Fixed));
        expected.Should().NotBeEmpty();
        var client = await Transport();

        (await client.SendAsync(Corpus.Shipment, Match("""{ "loadingTimeType": { "eq": "Fixed" } }"""))).ShouldHaveIds(expected);
        (await client.SendAsync(Corpus.Shipment, Match("""{ "loadingTimeType": { "eq": { "$var": "kind" } } }"""), new JsonObject { ["kind"] = "Fixed" })).ShouldHaveIds(expected);
    }

    [Fact]
    public async Task S10_a_variable_naming_no_enum_member_is_refused_as_the_literal_is()
    {
        var client = await Transport();

        var literal = await client.SendAsync(Corpus.Shipment, Match("""{ "loadingTimeType": { "eq": "Nope" } }"""));
        var variable = await client.SendAsync(Corpus.Shipment, Match("""{ "loadingTimeType": { "eq": { "$var": "kind" } } }"""), new JsonObject { ["kind"] = "Nope" });

        literal.StatusCode.Should().Be(400, literal.ToString());
        variable.StatusCode.Should().Be(400, variable.ToString());
        variable.ErrorCodes.Should().Equal(literal.ErrorCodes, "a variable is checked exactly as the literal in its place");
    }

    [Fact]
    public async Task S22_a_variable_on_an_unwind_alias_path_binds_through_the_alias()
    {
        // The legacy case used an enum under an item alias the lab model does not have; the
        // billing line type is the enum under an unwind alias here.
        var expected = Corpus.Rows(Corpus.Shipment)
            .SelectMany(row => Corpus.Elements(row, "billingLines.type").Where(type => type is BsonInt32 { Value: (int)BillingLineType.Carrier }).Select(_ => row.Id))
            .ToList();
        expected.Should().NotBeEmpty();
        var client = await Transport();
        const string Pipeline = """[ { "unwind": { "path": "billingLines", "as": "line" } }, { "match": { "line.type": { "eq": { "$var": "type" } } } }, { "project": { "id": 1 } }, { "page": { "limit": 500 } } ]""";

        (await client.SendAsync(Corpus.Shipment, Pipeline, new JsonObject { ["type"] = "Carrier" })).ShouldHaveIdsInAnyOrder(expected);
        (await client.SendAsync(Corpus.Shipment, Pipeline.Replace("""{ "$var": "type" }""", "\"Carrier\"", StringComparison.Ordinal))).ShouldHaveIdsInAnyOrder(expected);
    }

    [Fact]
    public async Task S12_an_unused_variable_is_not_an_error()
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, Match("""{ "matchCode": { "eq": "DUP" } }"""), new JsonObject { ["neverReferenced"] = "x" });

        answer.ShouldHaveIds(Dup());
    }

    [Fact]
    public async Task S13_variable_names_are_matched_case_insensitively_in_both_directions()
    {
        var client = await Staff();

        (await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "eq": { "$var": "MC" } } }"""), new JsonObject { ["mc"] = "DUP" })).ShouldHaveIds(Dup());
        (await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "eq": { "$var": "mc" } } }"""), new JsonObject { ["MC"] = "DUP" })).ShouldHaveIds(Dup());
    }

    [Fact]
    public async Task S14_S15_an_unbound_variable_is_refused_with_a_bag_and_without_one()
    {
        var client = await Staff();

        foreach (var answer in new[]
        {
            await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "eq": { "$var": "nope" } } }"""), new JsonObject { ["other"] = "x" }),
            await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "eq": { "$var": "mc" } } }""")),
        })
        {
            answer.ShouldRefuse("UNBOUND_VARIABLE", 400);
            answer.ErrorCodes.Should().Equal("UNBOUND_VARIABLE");
        }
    }

    [Fact]
    public async Task S16_a_variable_bound_to_an_object_is_INVALID_VARIABLE_and_to_an_array_under_eq_INVALID_OPERAND()
    {
        var client = await Staff();
        var pipeline = Match("""{ "matchCode": { "eq": { "$var": "v" } } }""");

        var anObject = await client.SendAsync(Corpus.Employee, pipeline, JsonNode.Parse("""{ "v": { "a": 1 } }"""));
        anObject.ShouldRefuse("INVALID_VARIABLE", 400);
        anObject.ErrorCodes.Should().Equal("INVALID_VARIABLE");

        var anArray = await client.SendAsync(Corpus.Employee, pipeline, JsonNode.Parse("""{ "v": ["a"] }"""));
        anArray.ShouldRefuse("INVALID_OPERAND", 400);
        anArray.ErrorCodes.Should().Equal("INVALID_OPERAND");
    }

    [Theory]
    [InlineData("a string on a numeric path", """{ "children": { "eq": { "$var": "v" } } }""", "\"not-a-number\"")]
    [InlineData("a boolean on a string path", """{ "matchCode": { "eq": { "$var": "v" } } }""", "true")]
    [InlineData("a number on a temporal path", """{ "birthday": { "gte": { "$var": "v" } } }""", "1")]
    [InlineData("a non-uuid on a guid path", """{ "id": { "eq": { "$var": "v" } } }""", "\"not-a-guid\"")]
    public async Task S17_a_variable_of_the_wrong_JSON_kind_for_its_path_is_INVALID_OPERAND(string why, string condition, string value)
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, Match(condition), JsonNode.Parse($$"""{ "v": {{value}} }"""));

        answer.ShouldRefuse("INVALID_OPERAND", 400, why);
        answer.ErrorCodes.Should().Equal(["INVALID_OPERAND"], why);
    }

    [Fact]
    public async Task S17_the_positive_control_a_uuid_variable_on_the_guid_path_selects_its_row()
    {
        var row = Corpus.Rows(Corpus.Employee)[4];

        var answer = await (await Staff()).SendAsync(Corpus.Employee, Match("""{ "id": { "eq": { "$var": "v" } } }"""), new JsonObject { ["v"] = row.WireId });

        answer.ShouldHaveIds([row.Id]);
    }

    [Fact]
    public async Task S18_a_var_wrapper_carrying_an_extra_member_is_INVALID_OPERAND()
    {
        var answer = await (await Staff()).SendAsync(Corpus.Employee, Match("""{ "matchCode": { "eq": { "$var": "v", "extra": 1 } } }"""), new JsonObject { ["v"] = "DUP" });

        answer.ShouldRefuse("INVALID_OPERAND", 400);
        answer.ErrorCodes.Should().Equal("INVALID_OPERAND");
    }

    [Fact]
    public async Task S19_exists_takes_a_variable_and_refuses_an_unbound_one()
    {
        var client = await Staff();

        var unbound = await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "exists": { "$var": "nope" } } }"""), new JsonObject());
        unbound.ShouldRefuse("UNBOUND_VARIABLE", 400);

        var missing = Corpus.RowsMissing(Corpus.Employee, "matchCode");
        missing.Should().ContainSingle("exactly one row has no matchCode element at all");
        var bound = await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "exists": { "$var": "v" } } }"""), new JsonObject { ["v"] = true });
        bound.ShouldHaveIds(Corpus.AllIds(Corpus.Employee).Except(Corpus.IdsOf(missing)));
    }

    [Fact]
    public async Task S20_a_variable_as_a_group_aggregate_argument_binds_and_is_refused_when_unbound()
    {
        var client = await Staff();
        const string Pipeline = """[ { "group": { "by": [], "fields": { "total": { "sum": { "$var": "each" } } } } } ]""";

        var bound = await client.SendAsync(Corpus.Employee, Pipeline, new JsonObject { ["each"] = 2 });
        var bucket = bound.ShouldBeOk().Items.Should().ContainSingle("one bucket over every row").Subject;
        decimal.Parse(bucket!["total"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(Corpus.Counts(Corpus.Employee).A * 2);

        var unbound = await client.SendAsync(Corpus.Employee, Pipeline, new JsonObject());
        unbound.ShouldRefuse("UNBOUND_VARIABLE", 400);
    }

    [Fact]
    public async Task S21_64_variables_are_accepted_and_65_are_refused_MAX_VARIABLES_EXCEEDED()
    {
        var client = await Staff();
        var pipeline = Match("""{ "matchCode": { "eq": { "$var": "v0" } } }""");
        JsonObject Bag(int count) => new(Enumerable.Range(0, count).Select(n => KeyValuePair.Create("v" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), (JsonNode?)"DUP")));

        (await client.SendAsync(Corpus.Employee, pipeline, Bag(64))).ShouldHaveIds(Dup());

        var over = await client.SendAsync(Corpus.Employee, pipeline, Bag(65));
        over.ShouldRefuse("MAX_VARIABLES_EXCEEDED", 400);
        over.ErrorCodes.Should().Equal("MAX_VARIABLES_EXCEEDED");
    }

    [Fact]
    public async Task S09_S23_a_variable_inside_an_in_array_binds_per_element_and_a_whole_in_operand_must_be_an_array()
    {
        // Contract 2: in compares under the default collation, so every spelling of müller counts.
        string[] wanted = ["DUP", "müller"];
        var expected = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Text(row, "matchCode") is { } code && wanted.Any(value => Order.FoldCi(value) == Order.FoldCi(code)));
        expected.Should().HaveCount(7);
        var client = await Staff();

        (await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "in": [ { "$var": "v" }, "müller" ] } }"""), new JsonObject { ["v"] = "DUP" })).ShouldHaveIds(expected);
        (await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "in": [ { "$var": "v" }, { "$var": "w" } ] } }"""), new JsonObject { ["v"] = "DUP", ["w"] = "müller" })).ShouldHaveIds(expected);
        (await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "in": { "$var": "v" } } }"""), JsonNode.Parse("""{ "v": ["DUP", "müller"] }"""))).ShouldHaveIds(expected);

        var unbound = await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "in": [ { "$var": "nope" }, "müller" ] } }"""), new JsonObject { ["v"] = "DUP" });
        unbound.ShouldRefuse("UNBOUND_VARIABLE", 400);

        // S9's engine half: a scalar for a whole in operand.
        var scalar = await client.SendAsync(Corpus.Employee, Match("""{ "matchCode": { "in": { "$var": "v" } } }"""), new JsonObject { ["v"] = "DUP" });
        scalar.ShouldRefuse("OPERAND_NOT_ARRAY", 400);
        scalar.ErrorCodes.Should().Equal("OPERAND_NOT_ARRAY");
    }

    [Fact]
    public async Task S_a_variable_used_at_two_paths_of_different_kinds_is_refused_in_both_orders()
    {
        var client = await Staff();

        foreach (var (condition, value) in new[]
        {
            ("""{ "and": [ { "matchCode": { "eq": { "$var": "v" } } }, { "children": { "eq": { "$var": "v" } } } ] }""", (JsonNode)"DUP"),
            ("""{ "and": [ { "children": { "eq": { "$var": "v" } } }, { "matchCode": { "eq": { "$var": "v" } } } ] }""", (JsonNode)2),
        })
        {
            var answer = await client.SendAsync(Corpus.Employee, Match(condition), new JsonObject { ["v"] = value });

            answer.ShouldRefuse("INVALID_OPERAND", 400);
            answer.ErrorCodes.Should().Equal("INVALID_OPERAND");
        }
    }
}
