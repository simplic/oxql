using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Addon;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>Every rule of design §2 to §6 and §14, one named case each, on the CLR fixture graph.</summary>
public class StageRulesTests
{
    private static EntityModel Model => BindHost.Probe;
    private const string Order = "probe.order";

    private static async Task<BoundPipeline> Bound(string pipeline, RequestContext? context = null, string? variables = null) =>
        await BindHost.BoundAsync(Model, Order, pipeline, context, variables);

    private static async Task<OxQL.Core.Models.QueryValidationError> Error(string pipeline, string code, RequestContext? context = null, string? variables = null) =>
        await BindHost.ErrorAsync(Model, Order, pipeline, code, context, variables);

    private static BoundCondition.Leaf Leaf(BoundPipeline bound, int stage = 0) =>
        (BoundCondition.Leaf)((BoundStage.Match)bound.Stages[stage]).Condition;

    // ---- entity and scope (§2, §10) ------------------------------------------------------

    [Fact]
    public async Task Entity_ids_match_exactly_and_a_retired_id_answers_with_a_diagnostic()
    {
        var refusal = await BindHost.RefusedAsync(Model, "Probe.Order", "[]");

        refusal.Status.Should().Be(400);
        refusal.Errors![0].Code.Should().Be(Codes.UnknownEntity);

        var bound = await BindHost.BoundAsync(Model, "orders", "[]");

        bound.Entity.Id.Should().Be(Order);
        bound.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.EntityIdRetired && (string)diagnostic.Params!["currentId"]! == Order);
    }

    [Fact]
    public async Task A_request_without_an_organisation_is_refused_before_binding()
    {
        var refusal = await BindHost.RefusedAsync(Model, Order, "[]", BindHost.Context(organisation: Guid.Empty));

        refusal.Status.Should().Be(403);
        refusal.Type.Should().Be("access_denied");
        refusal.Errors![0].Code.Should().Be(Codes.AccessDenied);
    }

    [Fact]
    public async Task An_entity_without_the_organisation_member_is_refused()
    {
        var refusal = await BindHost.RefusedAsync(Model, "probe.keyless", "[]");

        refusal.Status.Should().Be(403);
    }

    [Fact]
    public async Task The_scope_is_one_typed_equality_on_the_organisation_member()
    {
        var bound = await Bound("[]");

        bound.Scope.OrganisationStorage.Should().Be("OrganizationId");
        bound.Scope.Representation.Should().Be(GuidRepresentation.Standard);
        bound.Stages.Should().NotContain(stage => stage is BoundStage.Scope, "the scope is not among the caller's stages");
    }

    // ---- paths (§3) --------------------------------------------------------------------------

    [Theory]
    [InlineData("", Codes.InvalidPath)]
    [InlineData("a..b", Codes.InvalidPath)]
    [InlineData("$where", Codes.InvalidPath)]
    [InlineData("shipTo.$x", Codes.InvalidPath)]
    [InlineData("Number", Codes.UnknownPath)]
    [InlineData("shipTo.nothing", Codes.UnknownPath)]
    [InlineData("hidden", Codes.NotStored)]
    [InlineData("computed", Codes.NotStored)]
    [InlineData("figure.figureKind", Codes.NotStored)]
    [InlineData("anything", Codes.NotFilterable)]
    [InlineData("location", Codes.NotFilterable)]
    [InlineData("tod", Codes.NotFilterable)]
    [InlineData("shipTo", Codes.NotFilterable)]
    [InlineData("items", Codes.NotFilterable)]
    [InlineData("stamp", Codes.NotFilterable)]
    public async Task A_path_is_validated_against_the_shape_at_its_stage(string path, string code)
    {
        var error = await Error($$"""[{ "match": {} }, { "match": { "{{path}}": { "eq": "x" } } }]""", code);

        error.Stage.Should().Be(1, "the error names the stage");
        error.Path.Should().Be(path);
    }

    [Theory]
    [InlineData("qrCode", "QRCode")]
    [InlineData("renamed", "x")]
    [InlineData("id", "_id")]
    [InlineData("items.id", "Items._id")]
    [InlineData("shipTo.city", "ShipTo.City")]
    [InlineData("customer.name", "Customer.Name")]
    [InlineData("prices.eur.net", "Prices.eur.Net")]
    [InlineData("priceList.eur.net", "PriceList.v.Net")]
    public async Task Storage_paths_come_from_the_model_never_from_the_wire_spelling(string wire, string storage)
    {
        var bound = await Bound($$"""[{ "match": { "{{wire}}": { "eq": "{{(wire.EndsWith("id") || wire == "id" ? "195fb742-82b3-405e-b77b-42838eb0aaa9" : "1")}}" } } }]""");

        Leaf(bound).Path.Storage.Should().Be(storage);
    }

    [Fact]
    public async Task Storage_spelling_is_not_accepted_as_a_wire_path()
    {
        (await Error("""[{ "match": { "QRCode": { "eq": "x" } } }]""", Codes.UnknownPath)).Path.Should().Be("QRCode");
        (await Error("""[{ "match": { "_id": { "eq": "x" } } }]""", Codes.UnknownPath)).Path.Should().Be("_id");
        (await Error("""[{ "match": { "Items.Quantity": { "eq": 1 } } }]""", Codes.UnknownPath)).Path.Should().Be("Items.Quantity");
    }

    [Fact]
    public async Task A_path_through_a_collection_means_some_element_in_match_and_is_refused_in_sort()
    {
        var bound = await Bound("""[{ "match": { "items.quantity": { "gt": 1 } } }]""");

        Leaf(bound).Path.CollectionAncestors.Should().Be(1);
        Leaf(bound).Path.Storage.Should().Be("Items.Quantity");

        (await Error("""[{ "sort": [{ "items.quantity": "asc" }] }]""", Codes.NotSortable)).Path.Should().Be("items.quantity");
        (await Error("""[{ "sort": [{ "tags": "asc" }] }]""", Codes.NotSortable)).Path.Should().Be("tags");
        (await Error("""[{ "sort": [{ "shipTo": "asc" }] }]""", Codes.NotSortable)).Path.Should().Be("shipTo");
        (await Error("""[{ "sort": [{ "number": "up" }] }]""", Codes.InvalidSortDirection)).Path.Should().Be("number");
    }

    [Fact]
    public async Task Dictionary_keys_are_verbatim_and_typed_by_the_value()
    {
        var bound = await Bound("""[{ "match": { "byNumber.7": { "eq": "seven" }, "prices.eur.currency": { "eq": "EUR" } } }]""");
        var and = (BoundCondition.And)((BoundStage.Match)bound.Stages[0]).Condition;

        ((BoundCondition.Leaf)and.Conditions[0]).Path.Storage.Should().Be("ByNumber.7");
        ((BoundCondition.Leaf)and.Conditions[0]).Path.Kind.Should().Be(Kind.String);
        ((BoundCondition.Leaf)and.Conditions[1]).Path.Storage.Should().Be("Prices.eur.Currency");
    }

    // ---- operands (§4) -----------------------------------------------------------------------

    [Theory]
    [InlineData("number", "\"abc\"", "\"abc\"")]
    [InlineData("count", "5", "NumberLong(5)")]
    [InlineData("count", "\"5\"", "NumberLong(5)")]
    [InlineData("big", "\"9007199254740993\"", "NumberLong(\"9007199254740993\")")]
    [InlineData("ratio", "1.5", "1.5")]
    [InlineData("flag", "\"false\"", "false")]
    [InlineData("flag", "true", "true")]
    [InlineData("when", "\"2026-09-05T00:00:00Z\"", "ISODate(\"2026-09-05T00:00:00Z\")")]
    [InlineData("when", "\"2026-09-05T02:00:00+02:00\"", "ISODate(\"2026-09-05T00:00:00Z\")")]
    [InlineData("day", "\"2024-01-02\"", "ISODate(\"2024-01-02T00:00:00Z\")")]
    [InlineData("span", "\"PT1H30M\"", "\"01:30:00\"")]
    [InlineData("state", "\"Open\"", "0")]
    [InlineData("state", "1", "1")]
    [InlineData("wide", "\"Huge\"", "NumberLong(5000000000)")]
    [InlineData("stateName", "\"Shipped\"", "\"Shipped\"")]
    [InlineData("stateName", "1", "\"Shipped\"")]
    [InlineData("guidText", "\"195fb742-82b3-405e-b77b-42838eb0aaa9\"", "\"195fb742-82b3-405e-b77b-42838eb0aaa9\"")]
    [InlineData("blob", "\"AQID\"", "BinData(0, \"AQID\")")]
    public async Task An_operand_is_encoded_from_the_kind_and_the_storage_representation(string path, string operand, string expected)
    {
        var bound = await Bound($$"""[{ "match": { "{{path}}": { "eq": {{operand}} } } }]""");
        var single = Leaf(bound).Operand.Should().BeOfType<BoundOperand.Single>().Subject;

        single.Value.Should().Be(BsonSerializerExtensionsForTests.Parse(expected));
    }

    [Fact]
    public async Task A_guid_is_one_typed_comparison_in_the_members_representation()
    {
        var bound = await Bound("""[{ "match": { "id": { "eq": "195FB74282B3405EB77B42838EB0AAA9" } } }]""");
        var value = Leaf(bound).Operand.Should().BeOfType<BoundOperand.Single>().Subject.Value.Should().BeOfType<BsonBinaryData>().Subject;

        value.SubType.Should().Be(BsonBinarySubType.UuidStandard);
        value.ToGuid(GuidRepresentation.Standard).Should().Be(Guid.Parse("195fb742-82b3-405e-b77b-42838eb0aaa9"));

        var set = await Bound("""[{ "match": { "id": { "in": ["195fb742-82b3-405e-b77b-42838eb0aaa9"] } } }]""");

        Leaf(set).Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().ContainSingle().Which.Should().BeOfType<BsonBinaryData>();

        var tolerant = await Bound("""[{ "match": { "id": { "eq": "195fb742-82b3-405e-b77b-42838eb0aaa9" } } }]""",
            BindHost.Context(BindHost.Options(options => options.Representation.GuidTolerant = true)));

        Leaf(tolerant).Operand.Should().BeOfType<BoundOperand.Tolerant>().Which.Alternatives.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_decimal_matches_both_storage_forms_until_the_host_switches_to_typed()
    {
        var bound = await Bound("""[{ "match": { "amount": { "eq": "12.50" } } }]""");
        var tolerant = Leaf(bound).Operand.Should().BeOfType<BoundOperand.Tolerant>().Subject;

        // F2: the text alternative is a pattern over every scale the driver could have
        // written the value with. It used to be the single spelling G29 produces — "12.5" —
        // which never matches a row the driver wrote as "12.50", and 14 of the 15 rows that
        // hold 125 000 were unreachable by the value the service itself returned for them.
        tolerant.Alternatives.Should().HaveCount(2);
        tolerant.Alternatives[0].Should().Be(new BsonDecimal128(new Decimal128(12.50m)));
        tolerant.Alternatives[1].Should().Be(new BsonRegularExpression("^12\\.50*$"));

        var typed = await Bound("""[{ "match": { "amount": { "eq": 12.5 } } }]""",
            BindHost.Context(BindHost.Options(options => options.Representation.DecimalMode = "typed")));

        Leaf(typed).Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonDecimal128(new Decimal128(12.5m)));
    }

    [Theory]
    [InlineData("when", "\"2026-09-05\"")]
    [InlineData("when", "\"2026-09-05T00:00:00\"")]
    [InlineData("day", "\"2026-09-05T00:00:00Z\"")]
    [InlineData("count", "\"five\"")]
    [InlineData("count", "1.5")]
    [InlineData("flag", "\"yes\"")]
    [InlineData("number", "5")]
    [InlineData("span", "\"01:30:00\"")]
    [InlineData("blob", "\"***\"")]
    [InlineData("id", "\"nope\"")]
    [InlineData("count", "[1]")]
    [InlineData("count", "{ \"a\": 1 }")]
    public async Task A_wrong_json_kind_is_refused_naming_the_expected_kind(string path, string operand)
    {
        var error = await Error($$"""[{ "match": { "{{path}}": { "eq": {{operand}} } } }]""", Codes.InvalidOperand);

        error.Message.Should().Contain("expects");
    }

    [Fact]
    public async Task Null_is_legal_for_equality_on_any_member_and_nothing_else()
    {
        var eq = await Bound("""[{ "match": { "note": { "eq": null } } }]""");
        var neq = await Bound("""[{ "match": { "count": { "neq": null } } }]""");

        Leaf(eq).Operand.Should().BeOfType<BoundOperand.Null>();
        Leaf(neq).Operand.Should().BeOfType<BoundOperand.Null>();

        await Error("""[{ "match": { "count": { "gt": null } } }]""", Codes.InvalidOperand);
        await Error("""[{ "match": { "number": { "contains": null } } }]""", Codes.InvalidOperand);
    }

    [Fact]
    public async Task Sets_take_arrays_and_exists_takes_a_boolean()
    {
        await Error("""[{ "match": { "number": { "in": "abc" } } }]""", Codes.OperandNotArray);
        await Error("""[{ "match": { "number": { "exists": "yes" } } }]""", Codes.InvalidOperand);
        await Error("""[{ "match": { "number": { "startsWith": 5 } } }]""", Codes.InvalidOperand);

        var empty = await Bound("""[{ "match": { "number": { "in": [] } } }]""");

        Leaf(empty).Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().BeEmpty();

        var mixed = await Bound("""[{ "match": { "state": { "in": ["Open", 1, null] } } }]""");

        Leaf(mixed).Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().Equal(new BsonInt32(0), new BsonInt32(1), BsonNull.Value);
    }

    [Fact]
    public async Task Enum_operands_are_names_or_numbers_and_an_unknown_name_is_refused()
    {
        var error = await Error("""[{ "match": { "state": { "nin": ["NOT_OPEN"] } } }]""", Codes.UnknownEnumMember);

        error.Path.Should().Be("state");

        var names = await Bound("""[{ "match": { "state": { "in": ["Open", "Shipped"] } } }]""");

        Leaf(names).Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().Equal(new BsonInt32(0), new BsonInt32(1));
        (await Error("""[{ "match": { "state": { "contains": "Op" } } }]""", Codes.InvalidOperand)).Message.Should().Contain("does not apply");
    }

    [Fact]
    public async Task Variables_are_bound_in_the_same_encoding_and_an_unbound_one_is_refused()
    {
        var bound = await Bound("""[{ "match": { "when": { "gte": { "$var": "from" } }, "state": { "in": { "$var": "states" } } } }]""",
            variables: """{ "from": "2026-01-01T00:00:00Z", "states": ["Open", 1] }""");
        var and = (BoundCondition.And)((BoundStage.Match)bound.Stages[0]).Condition;

        ((BoundCondition.Leaf)and.Conditions[0]).Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().BeOfType<BsonDateTime>();
        ((BoundCondition.Leaf)and.Conditions[1]).Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().HaveCount(2);

        (await Error("""[{ "match": { "number": { "eq": { "$var": "v" } } } }]""", Codes.UnboundVariable)).Path.Should().Be("number");
        await Error("""[{ "match": { "number": { "eq": { "$var": "v" } } } }]""", Codes.UnboundVariable, variables: """{ "other": 1 }""");
        await Error("""[{ "match": { "number": { "eq": { "$var": "v" } } } }]""", Codes.InvalidVariable, variables: """{ "v": { "nested": 1 } }""");
        await Error("""[{ "match": { "number": { "eq": { "$var": "v" } } } }]""", Codes.InvalidOperand, variables: """{ "v": 5 }""");
        await Error("""[{ "match": { "number": { "exists": { "$var": "v" } } } }]""", Codes.UnboundVariable);
        await Error("""[{ "match": { "number": { "eq": { "$var": "v", "x": 1 } } } }]""", Codes.InvalidOperand, variables: """{ "v": "a" }""");
    }

    [Fact]
    public async Task Too_many_variables_is_a_refusal()
    {
        var context = BindHost.Context(BindHost.Options(options => options.Limits.MaxVariables = 1));

        await Error("""[]""", Codes.MaxVariablesExceeded, context, """{ "a": 1, "b": 2 }""");
    }

    // ---- conditions (§5) ---------------------------------------------------------------------

    [Fact]
    public async Task Several_operators_on_one_path_and_several_paths_are_an_and()
    {
        var bound = await Bound("""[{ "match": { "when": { "gte": "2026-01-01T00:00:00Z", "lte": "2026-12-31T00:00:00Z" }, "flag": { "eq": true } } }]""");
        var and = ((BoundStage.Match)bound.Stages[0]).Condition.Should().BeOfType<BoundCondition.And>().Subject;

        and.Conditions.Should().HaveCount(3);
        and.Conditions.Select(condition => ((BoundCondition.Leaf)condition).Op).Should().Equal("gte", "lte", "eq");
    }

    [Fact]
    public async Task Logical_groups_nest_and_an_empty_one_is_refused()
    {
        var bound = await Bound("""[{ "match": { "or": [ { "flag": { "eq": true } }, { "not": { "and": [ { "count": { "gt": 1 } }, { "count": { "lt": 5 } } ] } } ] } }]""");

        ((BoundStage.Match)bound.Stages[0]).Condition.Should().BeOfType<BoundCondition.Or>().Which.Conditions[1].Should().BeOfType<BoundCondition.Not>();

        await Error("""[{ "match": { "and": [] } }]""", Codes.EmptyLogicalGroup);
        await Error("""[{ "match": { "or": [ {} ] } }]""", Codes.EmptyLogicalGroup);
        await Error("""[{ "match": { "not": {} } }]""", Codes.EmptyLogicalGroup);
    }

    [Fact]
    public async Task Operators_are_case_sensitive_and_closed()
    {
        (await Error("""[{ "match": { "number": { "elemMatch": "x" } } }]""", Codes.UnknownOperator)).Message.Should().Contain("elemMatch");
        await Error("""[{ "match": { "number": { "EQ": "x" } } }]""", Codes.UnknownOperator);
        await Error("""[{ "match": { "number": { "startswith": "x" } } }]""", Codes.UnknownOperator);
    }

    [Fact]
    public async Task IgnoreCase_applies_to_the_listed_operators_on_strings_only()
    {
        foreach (var op in new[] { "eq", "neq", "contains", "startsWith", "endsWith" })
        {
            var bound = await Bound($$"""[{ "match": { "number": { "{{op}}": "x", "options": { "ignoreCase": true } } } }]""");

            Leaf(bound).IgnoreCase.Should().BeTrue(op);
        }

        var set = await Bound("""[{ "match": { "number": { "in": ["x"], "options": { "ignoreCase": true } } } }]""");

        Leaf(set).IgnoreCase.Should().BeTrue();

        (await Error("""[{ "match": { "count": { "eq": 1, "options": { "ignoreCase": true } } } }]""", Codes.OptionNotApplicable)).Path.Should().Be("count");
        await Error("""[{ "match": { "number": { "regex": "x", "options": { "ignoreCase": true } } } }]""", Codes.OptionNotApplicable);
        await Error("""[{ "match": { "number": { "gt": "x", "options": { "ignoreCase": true } } } }]""", Codes.OptionNotApplicable);
        (await Error("""[{ "match": { "number": { "eq": "x", "options": { "collation": "de" } } } }]""", Codes.OptionNotApplicable)).Message.Should().Contain("collation");
    }

    [Fact]
    public async Task Regex_is_guarded_statically_and_an_unanchored_pattern_is_diagnosed()
    {
        await Error("""[{ "match": { "number": { "regex": "(a+)+$" } } }]""", Codes.InvalidRegex);
        await Error("""[{ "match": { "number": { "regex": "(a)\\1" } } }]""", Codes.InvalidRegex);
        await Error("""[{ "match": { "number": { "regex": "(" } } }]""", Codes.InvalidRegex);
        await Error($$"""[{ "match": { "number": { "regex": "{{new string('a', 201)}}" } } }]""", Codes.RegexTooLong);

        var anchored = await Bound("""[{ "match": { "number": { "regex": "^abc" } } }]""");

        anchored.Diagnostics.Should().BeEmpty();

        var unanchored = await Bound("""[{ "match": { "number": { "regex": "abc" } } }]""");

        unanchored.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.RegexUnanchored);
    }

    [Fact]
    public async Task Any_correlates_within_one_element_of_a_collection_of_objects()
    {
        var bound = await Bound("""[{ "match": { "items": { "any": { "quantity": { "gt": 1 }, "price.net": { "lt": "10" } } } } }]""");
        var any = ((BoundStage.Match)bound.Stages[0]).Condition.Should().BeOfType<BoundCondition.Any>().Subject;

        any.Path.Storage.Should().Be("Items");

        var inner = any.Inner.Should().BeOfType<BoundCondition.And>().Subject;

        ((BoundCondition.Leaf)inner.Conditions[0]).Path.Storage.Should().Be("Quantity", "inner paths are relative to the element");
        ((BoundCondition.Leaf)inner.Conditions[1]).Path.Storage.Should().Be("Price.Net");

        (await Error("""[{ "match": { "tags": { "any": { "x": { "eq": 1 } } } } }]""", Codes.AnyNotApplicable)).Message.Should().Contain("scalars");
        (await Error("""[{ "match": { "items.stops": { "any": { "city": { "eq": "x" } } } } }]""", Codes.AnyNotApplicable)).Message.Should().Contain("outer");
        (await Error("""[{ "match": { "shipTo": { "any": { "city": { "eq": "x" } } } } }]""", Codes.AnyNotApplicable)).Message.Should().Contain("not a collection");
        await Error("""[{ "unwind": { "path": "items" } }, { "match": { "items": { "any": { "quantity": { "gt": 1 } } } } }]""", Codes.AnyNotApplicable);
        await Error("""[{ "match": { "items": { "any": { "nothing": { "gt": 1 } } } } }]""", Codes.UnknownPath);
    }

    // ---- stages (§6) -------------------------------------------------------------------------

    [Fact]
    public async Task A_stage_carries_exactly_one_known_member()
    {
        await Error("""[{ "foo": {} }]""", Codes.UnknownStage);
        await Error("""[{ "match": {}, "sort": [] }]""", Codes.UnknownStage);
        await Error("""[{}]""", Codes.UnknownStage);
    }

    [Fact]
    public async Task Unwind_needs_a_collection_at_the_current_shape_and_the_outer_one_first()
    {
        var bound = await Bound("""[{ "unwind": { "path": "items", "as": "it", "includeIndex": "i" } }, { "unwind": { "path": "items.stops" } }, { "match": { "items.quantity": { "gt": 1 }, "it.quantity": { "gt": 2 }, "items.stops.city": { "eq": "x" }, "i": { "gte": 1 }, "it": { "exists": true } } }, { "sort": [{ "items.quantity": "asc" }, { "it.stops.city": "desc" }] }]""");
        var and = (BoundCondition.And)((BoundStage.Match)bound.Stages[2]).Condition;

        var leaves = and.Conditions.Cast<BoundCondition.Leaf>().ToList();

        leaves[0].Path.Storage.Should().Be("Items.Quantity");
        leaves[0].Path.CollectionAncestors.Should().Be(0, "the array is unwound");
        leaves[1].Path.Storage.Should().Be("it.Quantity");
        leaves[2].Path.Storage.Should().Be("Items.Stops.City");
        leaves[2].Path.CollectionAncestors.Should().Be(0);
        leaves[3].Path.Storage.Should().Be("i");
        leaves[3].Path.Kind.Should().Be(Kind.Int);
        leaves[4].Path.Kind.Should().Be(Kind.Object);
        bound.PagingMode.Should().Be(PagingMode.Offset);

        (await Error("""[{ "unwind": { "path": "items.stops" } }]""", Codes.UnwindOrder)).Path.Should().Be("items.stops");
        await Error("""[{ "unwind": { "path": "shipTo" } }]""", Codes.NotACollection);
        await Error("""[{ "unwind": { "path": "items" } }, { "unwind": { "path": "items" } }]""", Codes.NotACollection);
        await Error("""[{ "unwind": { "path": "items", "as": "number" } }]""", Codes.AliasCollision);
        await Error("""[{ "unwind": { "path": "items", "as": "it" } }, { "unwind": { "path": "items.stops", "as": "it" } }]""", Codes.AliasCollision);
        await Error("""[{ "unwind": { "path": "items", "as": "bad name" } }]""", Codes.InvalidAlias);
        await Error("""[{ "unwind": { "path": "items", "as": "it", "includeIndex": "it" } }]""", Codes.AliasCollision);
    }

    [Fact]
    public async Task Group_replaces_the_shape_with_its_outputs()
    {
        var bound = await Bound("""[{ "group": { "by": [{ "path": "state", "as": "st" }, { "dateTrunc": { "path": "when", "unit": "week", "timezone": "Europe/Berlin" }, "as": "wk" }], "fields": { "n": { "count": true }, "total": { "sum": "amount" }, "avgQ": { "avg": { "path": "count" } }, "kinds": { "countDistinct": "state" }, "big": { "max": { "multiply": ["ratio", 2] } } } } }, { "match": { "st": { "eq": "Open" }, "n": { "gt": 1 } } }, { "sort": [{ "wk": "desc" }, { "total": "asc" }] }, { "project": { "st": 1, "n": 1 } }]""");
        var group = (BoundStage.Group)bound.Stages[0];

        group.Keys.Should().HaveCount(2);
        group.Keys[1].Trunc!.WeekStart.Should().Be("monday");
        group.Keys[1].Trunc.Timezone.Should().Be("Europe/Berlin");
        group.Fields.Select(field => field.OutputKind).Should().Equal(Kind.Long, Kind.Decimal, Kind.Double, Kind.Long, Kind.Double);
        bound.FinalShape.Grouped.Should().BeTrue();
        bound.PagingMode.Should().Be(PagingMode.Offset);

        var and = (BoundCondition.And)((BoundStage.Match)bound.Stages[1]).Condition;

        ((BoundCondition.Leaf)and.Conditions[0]).Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonInt32(0), "the key keeps the enum's type");
        ((BoundCondition.Leaf)and.Conditions[0]).Path.Storage.Should().Be("st");

        await Error("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": {} } }, { "match": { "number": { "eq": "x" } } }]""", Codes.UnknownPath);
        await Error("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": {} } }, { "sort": [{ "number": "asc" }] }]""", Codes.UnknownPath);
        await Error("""[{ "group": { "by": [{ "path": "items.quantity", "as": "q" }], "fields": {} } }]""", Codes.GroupOnCollection);
        await Error("""[{ "group": { "by": [{ "path": "shipTo", "as": "q" }], "fields": {} } }]""", Codes.NotFilterable);
        await Error("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "st": { "count": true } } } }]""", Codes.AliasCollision);
        await Error("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "x": { "median": "amount" } } } }]""", Codes.UnknownAggFunction);
        await Error("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "x": { "sum": "number" } } } }]""", Codes.InvalidAggregateArgument);
        await Error("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "x": { "sum": "items.quantity" } } } }]""", Codes.GroupOnCollection);
        await Error("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "fortnight" }, "as": "d" }], "fields": {} } }]""", Codes.InvalidDateTruncUnit);
        await Error("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "day", "timezone": "Mars/Olympus" }, "as": "d" }], "fields": {} } }]""", Codes.InvalidTimezone);
        await Error("""[{ "group": { "by": [{ "dateTrunc": { "path": "number", "unit": "day" }, "as": "d" }], "fields": {} } }]""", Codes.InvalidAggregateArgument);
        await Error("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "a": { "count": true }, "b": { "count": true } } } }]""", Codes.MaxGroupFieldsExceeded,
            BindHost.Context(BindHost.Options(options => options.Limits.MaxGroupFields = 2)));
    }

    [Fact]
    public async Task Project_is_inclusion_or_exclusion_and_shapes_what_follows()
    {
        var inclusion = await Bound("""[{ "project": { "number": 1, "shipTo.city": 1 } }, { "sort": [{ "number": "asc" }] }, { "match": { "shipTo.city": { "eq": "x" } } }]""");

        ((BoundStage.Project)inclusion.Stages[0]).Inclusion.Should().BeTrue();
        ((BoundStage.Project)inclusion.Stages[0]).IncludeId.Should().BeTrue();

        var withoutId = await Bound("""[{ "project": { "number": 1, "id": 0 } }]""");

        ((BoundStage.Project)withoutId.Stages[0]).IncludeId.Should().BeFalse();

        var exclusion = await Bound("""[{ "project": { "addon": 0, "items": 0 } }, { "match": { "number": { "eq": "x" } } }]""");

        ((BoundStage.Project)exclusion.Stages[0]).Inclusion.Should().BeFalse();

        await Error("""[{ "project": { "number": 1, "flag": 0 } }]""", Codes.MixedProjection);
        await Error("""[{ "project": { "number": 1 } }, { "match": { "count": { "gt": 1 } } }]""", Codes.UnknownPath);
        await Error("""[{ "project": { "number": 0 } }, { "sort": [{ "number": "asc" }] }]""", Codes.UnknownPath);
        await Error("""[{ "project": { "nothing": 1 } }]""", Codes.UnknownPath);
        await Error("""[{ "project": {} }]""", Codes.MixedProjection);
        await Error("""[{ "project": { "a": 1, "b": 1 } }]""", Codes.MaxProjectionFieldsExceeded, BindHost.Context(BindHost.Options(options => options.Limits.MaxProjectionFields = 1)));
    }

    [Fact]
    public async Task Page_is_once_and_last_with_a_default_size()
    {
        var bound = await Bound("""[{ "page": { "limit": 25, "offset": 50, "includeTotalCount": true } }]""");

        bound.Page.Limit.Should().Be(25);
        bound.Page.Offset.Should().Be(50);
        bound.Page.IncludeTotalCount.Should().BeTrue();

        (await Bound("""[{ "page": {} }]""")).Page.Limit.Should().Be(100);
        (await Bound("[]")).Page.Limit.Should().Be(100);

        await Error("""[{ "page": { "limit": 0 } }]""", Codes.InvalidPageLimit);
        await Error("""[{ "page": { "limit": 501 } }]""", Codes.PageSizeExceeded);
        await Error("""[{ "page": { "offset": 5001 } }]""", Codes.MaxOffsetExceeded);
        await Error("""[{ "page": { "offset": -1 } }]""", Codes.InvalidPageLimit);
        await Error("""[{ "page": {} }, { "page": {} }]""", Codes.MultiplePageStages);
        (await Error("""[{ "page": {} }, { "match": {} }]""", Codes.StageAfterPage)).Stage.Should().Be(1);
        await Error("""[{ "page": { "cursor": "garbage" } }]""", Codes.CursorInvalid);
        await Error("""[{ "page": { "cursor": "a.b", "offset": 1 } }]""", Codes.InvalidPageLimit);
    }

    [Fact]
    public async Task A_cursor_is_bound_to_the_query_it_was_issued_for()
    {
        var first = await Bound("""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2 } }]""");
        var payload = new CursorPayload(first.Fingerprint, PagingMode.Keyset, [new CursorValue("number", true, new BsonString("abc")), new CursorValue("_id", true, new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard))], 0);
        var cursor = BindHost.Cursors.Encode(payload);

        var second = await Bound($$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""");

        second.Page.Cursor.Should().NotBeNull();
        second.Page.Cursor!.Fields.Should().HaveCount(2);
        second.Fingerprint.Should().Be(first.Fingerprint, "the page does not enter the fingerprint");

        await Error($$"""[{ "sort": [{ "number": "desc" }] }, { "page": { "limit": 2, "cursor": "{{cursor}}" } }]""", Codes.CursorInvalid);
        await Error($$"""[{ "match": { "flag": { "eq": true } } }, { "sort": [{ "number": "asc" }] }, { "page": { "cursor": "{{cursor}}" } }]""", Codes.CursorInvalid);
        await Error($$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "cursor": "{{cursor[..^2]}}" } }]""", Codes.CursorInvalid);
        await Error($$"""[{ "sort": [{ "number": "asc" }] }, { "page": { "cursor": "{{cursor}}" } }]""", Codes.CursorInvalid,
            BindHost.Context(organisation: Guid.NewGuid()));
    }

    [Fact]
    public async Task Limits_are_named_when_exceeded()
    {
        var context = BindHost.Context(BindHost.Options(options =>
        {
            options.Limits.MaxPipelineStages = 2;
            options.Limits.MaxConditions = 2;
            options.Limits.MaxUnwindStages = 1;
        }));

        await Error("""[{ "match": {} }, { "match": {} }, { "match": {} }]""", Codes.MaxPipelineStagesExceeded, context);
        await Error("""[{ "match": { "count": { "gt": 1, "lt": 5 }, "flag": { "eq": true } } }]""", Codes.MaxConditionsExceeded, context);
        await Error("""[{ "unwind": { "path": "items" } }, { "unwind": { "path": "items.stops" } }]""", Codes.MaxUnwindStagesExceeded, context);
    }

    // ---- joins (§6, §11) ---------------------------------------------------------------------

    [Fact]
    public async Task Lookup_follows_a_declared_backward_reference()
    {
        var bound = await BindHost.BoundAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["number", "amount"], "filter": { "flag": { "eq": true } }, "limit": 5 } }, { "match": { "orders.number": { "eq": "x" } } }]""");
        var lookup = (BoundStage.Lookup)bound.Stages[0];

        lookup.From.Id.Should().Be(Order);
        lookup.ParentKeyStorage.Should().Be("_id");
        lookup.ChildKeyStorage.Should().Be("CustomerId");
        lookup.Select.Select(path => path.Storage).Should().Equal(["Amount", "_id", "Number"], "the load set: the key, the hint the whole alias shows, what the match reads, in ordinal order of the wire paths");
        lookup.Limit.Should().Be(5);
        lookup.ChildScope.Entity.Id.Should().Be(Order);

        var leaf = Leaf(bound, 1);

        leaf.Path.Storage.Should().Be("orders.Number");
        leaf.Path.CollectionAncestors.Should().Be(1);
        bound.PagingMode.Should().Be(PagingMode.Keyset, "a lookup does not change the row's identity");

        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "path": "number", "as": "orders" } }]""", Codes.LookupNotDeclared);
        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.nothing", "path": "customerId", "as": "orders" } }]""", Codes.UnknownEntity);
        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "name" } }]""", Codes.AliasCollision);
        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "sort": [{ "orders.number": "asc" }] }]""", Codes.NotSortable);
        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "limit": 1000 } }]""", Codes.LookupLimitExceeded);
        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "localPath": "customerId", "foreignPath": "id", "as": "orders" } }]""", Codes.UnknownStageMember);
        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "localPath": "customerId", "foreignPath": "id", "as": "orders" } }]""", Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));
        await BindHost.ErrorAsync(Model, "probe.customer", """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "lookup": { "from": "probe.order", "path": "customerId", "as": "more" } }]""", Codes.MaxLookupStagesExceeded,
            BindHost.Context(BindHost.Options(options => options.Limits.MaxLookupStages = 1)));
    }

    [Fact]
    public async Task Resolve_follows_a_declared_forward_reference_locally_or_remotely()
    {
        var local = await Bound("""[{ "resolve": { "path": "customerId", "as": "cust", "select": ["name"] } }, { "match": { "cust.name": { "eq": "x" } } }, { "sort": [{ "cust.name": "asc" }] }]""");
        var resolve = (BoundStage.Resolve)local.Stages[0];

        resolve.IsRemote.Should().BeFalse();
        resolve.Target!.Id.Should().Be("probe.customer");
        resolve.TargetFieldStorage.Should().Be("_id");
        resolve.Reference.Storage.Should().Be("CustomerId");
        Leaf(local, 1).Path.Storage.Should().Be("cust.Name");
        Leaf(local, 1).IsSemiJoin.Should().BeFalse();
        local.PagingMode.Should().Be(PagingMode.Keyset);

        var viaReferenceId = await Bound("""[{ "resolve": { "path": "supplierId", "as": "sup" } }]""");

        ((BoundStage.Resolve)viaReferenceId.Stages[0]).Target!.Id.Should().Be("probe.supplier");

        var remote = await Bound("""[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"], "filter": { "active": { "eq": true } } } }, { "match": { "contact.name": { "eq": "x" } } }]""");
        var remoteStage = (BoundStage.Resolve)remote.Stages[0];

        remoteStage.IsRemote.Should().BeTrue();
        remoteStage.TargetEntity.Should().Be("crm.contact");
        remoteStage.TargetField.Should().Be("number");
        remoteStage.RemoteSelect.Should().Equal("name");
        remoteStage.RemoteFilter.Should().NotBeNull();
        Leaf(remote, 1).IsSemiJoin.Should().BeTrue();
        Leaf(remote, 1).Operand.Should().BeOfType<BoundOperand.Raw>();
        remote.HasSemiJoin.Should().BeTrue();

        await Error("""[{ "resolve": { "path": "number", "as": "r" } }]""", Codes.ResolveNotDeclared);
        await Error("""[{ "resolve": { "path": "missingId", "as": "r" } }]""", Codes.ResolveNotDeclared);
        await Error("""[{ "resolve": { "path": "customerId", "as": "customer" } }]""", Codes.AliasCollision);
        await Error("""[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "sort": [{ "contact.name": "asc" }] }]""", Codes.ResolveNotSortable);
        await Error("""[{ "resolve": { "source": "crm.customer", "localPath": "customerId", "as": "x" } }]""", Codes.UnknownStageMember);
        // MaxResolveStages is 8 per host.
        static string Resolves(int count) => "[" + string.Join(", ", Enumerable.Range(1, count).Select(n => $$"""{ "resolve": { "path": "customerId", "as": "a{{n}}" } }""")) + "]";

        (await Bound(Resolves(8))).Stages.OfType<BoundStage.Resolve>().Should().HaveCount(8);
        (await Error(Resolves(9), Codes.MaxResolveStagesExceeded)).Message.Should().Contain("more than 8 resolve stages");
    }

    // ---- addon paths (§14) -------------------------------------------------------------------

    private sealed class Definitions : IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AddonDefinition>>(entity == Order
                ? [
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "SoloplanNr", Kind = AddonKind.Long },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Preis", Kind = AddonKind.Decimal },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Ablieferbelege vorhanden", Kind = AddonKind.Bool },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "UpdateDateTime", Kind = AddonKind.DateTime },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Kunde", Kind = AddonKind.Object },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Kunde.name", Kind = AddonKind.String },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "vincario._v.data", Kind = AddonKind.String },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Old", Kind = AddonKind.String, Retired = true },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Status", Kind = AddonKind.String, Values = [new AddonValue("open", "Open"), new AddonValue("done", "Done")] },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Prio", Kind = AddonKind.Int, Values = [new AddonValue("1", "Low"), new AddonValue("2", "High")] },
                  ]
                : []);
    }

    private static RequestContext WithDefinitions() => BindHost.Context(addons: new Definitions());

    [Fact]
    public async Task A_defined_addon_key_binds_typed_and_matches_tolerantly()
    {
        var bound = await Bound("""[{ "match": { "addon.SoloplanNr": { "eq": 5 }, "addon.Preis": { "gt": "1.5" }, "addon.Ablieferbelege vorhanden": { "eq": true }, "addon.UpdateDateTime": { "gte": "2026-01-01T00:00:00Z" }, "addon.Kunde.name": { "eq": "x" }, "addon.vincario._v.data": { "eq": "y" } } }]""", WithDefinitions());
        var leaves = ((BoundCondition.And)((BoundStage.Match)bound.Stages[0]).Condition).Conditions.Cast<BoundCondition.Leaf>().ToList();

        leaves[0].Path.Storage.Should().Be("Addon.SoloplanNr");
        leaves[0].Path.Kind.Should().Be(Kind.Long);
        leaves[0].Operand.Should().BeOfType<BoundOperand.Tolerant>().Which.Alternatives.Should().Equal(new BsonInt64(5), new BsonString("5"));
        leaves[1].Path.Kind.Should().Be(Kind.Decimal);
        leaves[1].Operand.Should().BeOfType<BoundOperand.Single>()
            .Which.Value.Should().Be(new BsonDecimal128(new Decimal128(1.5m)), "an ordered comparison gets no text bracket: text orders by characters, not by value");
        leaves[2].Path.Storage.Should().Be("Addon.Ablieferbelege vorhanden");
        leaves[2].Operand.Should().BeOfType<BoundOperand.Tolerant>().Which.Alternatives.Should().Equal(BsonBoolean.True, new BsonString("true"));
        leaves[3].Operand.Should().BeOfType<BoundOperand.Tolerant>().Which.Alternatives[1].Should().Be(new BsonString("2026-01-01T00:00:00Z"));
        leaves[4].Path.Storage.Should().Be("Addon.Kunde.name");
        leaves[4].Operand.Should().BeOfType<BoundOperand.Single>();
        leaves[5].Path.Storage.Should().Be("Addon.vincario._v.data");
    }

    [Fact]
    public async Task An_undefined_retired_or_object_addon_key_is_projectable_only()
    {
        await Error("""[{ "match": { "addon.Undefined": { "eq": 1 } } }]""", Codes.NotFilterable, WithDefinitions());
        await Error("""[{ "match": { "addon.Old": { "eq": "x" } } }]""", Codes.NotFilterable, WithDefinitions());
        await Error("""[{ "match": { "addon.Kunde": { "eq": "x" } } }]""", Codes.NotFilterable, WithDefinitions());
        await Error("""[{ "match": { "addon.SoloplanNr": { "eq": 1 } } }]""", Codes.NotFilterable, "without definitions every key is unknown" is null ? null : BindHost.Context());
        await Error("""[{ "match": { "addon.$bad": { "eq": 1 } } }]""", Codes.InvalidPath, WithDefinitions());

        var projected = await Bound("""[{ "project": { "addon.Undefined": 1, "addon.Kunde": 1 } }]""", WithDefinitions());

        ((BoundStage.Project)projected.Stages[0]).Paths.Select(path => path.Storage).Should().Equal("Addon.Undefined", "Addon.Kunde");
    }

    [Fact]
    public async Task A_closed_value_list_admits_its_values_and_keeps_the_tolerant_form()
    {
        var bound = await Bound("""[{ "match": { "addon.Status": { "eq": "open" }, "addon.Prio": { "in": [2, "1"] } } }]""", WithDefinitions());
        var leaves = ((BoundCondition.And)((BoundStage.Match)bound.Stages[0]).Condition).Conditions.Cast<BoundCondition.Leaf>().ToList();

        leaves[0].Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonString("open"));

        // An int key compares on the digits, written as a number or a string, and still matches both storage forms.
        leaves[1].Operand.Should().BeOfType<BoundOperand.Set>().Which.Values.Should().Equal(new BsonInt64(2), new BsonString("2"), new BsonInt64(1), new BsonString("1"));

        var negated = await Bound("""[{ "match": { "addon.Status": { "nin": ["done"] }, "addon.Prio": { "neq": 1 } } }]""", WithDefinitions());

        ((BoundCondition.And)((BoundStage.Match)negated.Stages[0]).Condition).Conditions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_value_outside_a_closed_list_is_refused_as_an_unknown_member()
    {
        var error = await Error("""[{ "match": { "addon.Status": { "eq": "closed" } } }]""", Codes.UnknownEnumMember, WithDefinitions());

        error.Path.Should().Be("addon.Status");
        error.Message.Should().Contain("'closed'").And.Contain("open, done");

        await Error("""[{ "match": { "addon.Status": { "in": ["open", "closed"] } } }]""", Codes.UnknownEnumMember, WithDefinitions());
        await Error("""[{ "match": { "addon.Status": { "neq": "closed" } } }]""", Codes.UnknownEnumMember, WithDefinitions());
        await Error("""[{ "match": { "addon.Prio": { "eq": 3 } } }]""", Codes.UnknownEnumMember, WithDefinitions());
        await Error("""[{ "match": { "addon.Prio": { "nin": ["3"] } } }]""", Codes.UnknownEnumMember, WithDefinitions());
    }

    [Fact]
    public async Task A_variable_bound_to_a_closed_list_key_is_checked_like_a_literal()
    {
        var bound = await Bound("""[{ "match": { "addon.Status": { "eq": { "$var": "s" } } } }]""", WithDefinitions(), variables: """{ "s": "done" }""");

        Leaf(bound).Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonString("done"));

        await Error("""[{ "match": { "addon.Status": { "eq": { "$var": "s" } } } }]""", Codes.UnknownEnumMember, WithDefinitions(), variables: """{ "s": "closed" }""");
    }

    [Fact]
    public async Task A_key_without_a_value_list_accepts_any_value_of_its_kind()
    {
        var bound = await Bound("""[{ "match": { "addon.Kunde.name": { "eq": "anything" }, "addon.SoloplanNr": { "in": [7, 8] } } }]""", WithDefinitions());

        ((BoundCondition.And)((BoundStage.Match)bound.Stages[0]).Condition).Conditions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_sort_on_an_addon_key_is_allowed_and_diagnosed()
    {
        var bound = await Bound("""[{ "sort": [{ "addon.SoloplanNr": "asc" }] }]""", WithDefinitions());

        bound.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.SortOnAddon);
        await Error("""[{ "sort": [{ "addon.Undefined": "asc" }] }]""", Codes.NotSortable, WithDefinitions());
    }
}

internal static class BsonSerializerExtensionsForTests
{
    public static BsonValue Parse(string shell) => BsonDocument.Parse("{ v: " + shell + " }")["v"];
}
