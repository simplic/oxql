using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The defect register (<c>oxql-lab/OXQL_DEFECTS.md</c>), one named case per row that is an
/// engine fault: what v1 did silently, v2 refuses with a code or does right. F8 is a
/// base-package configuration item; T1, T3 and T4 are the wire spelling, the removed
/// <c>/types</c> endpoint and the published limits, covered by the model and the options.
/// </summary>
public class RegisterCasesTests
{
    private const string Order = "probe.order";
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static Task<BoundPipeline> Bound(string pipeline, string entity = Order) => BindHost.BoundAsync(BindHost.Probe, entity, pipeline);

    private static Task<OxQL.Core.Models.QueryValidationError> Error(string pipeline, string code, string entity = Order) => BindHost.ErrorAsync(BindHost.Probe, entity, pipeline, code);

    private static async Task<BsonDocument> Filter(string pipeline)
    {
        var compiled = MongoCompiler.Compile(await Bound(pipeline), Options);

        return compiled.PageStages[1]["$match"].AsBsonDocument;
    }

    [Fact]
    public async Task F1_an_unknown_path_is_refused_not_matched_against_nothing()
    {
        (await Error("""[{ "match": { "zzzNotAFieldAnywhere": { "eq": 1 } } }]""", Codes.UnknownPath)).Path.Should().Be("zzzNotAFieldAnywhere");
        await Error("""[{ "sort": [{ "zzzNotAFieldAnywhere": "asc" }] }]""", Codes.UnknownPath);
        await Error("""[{ "project": { "zzzNotAFieldAnywhere": 1 } }]""", Codes.UnknownPath);
    }

    [Fact]
    public async Task F2_published_names_resolve_and_storage_names_come_from_the_registry()
    {
        (await Filter("""[{ "match": { "number": { "eq": "x" } } }]""")).ShouldBeBson(BsonDocument.Parse("{ Number: 'x' }"));
        (await Filter("""[{ "match": { "id": { "eq": "195fb742-82b3-405e-b77b-42838eb0aaa9" } } }]""")).Contains("_id").Should().BeTrue();
        (await Filter("""[{ "match": { "items.id": { "eq": "195fb742-82b3-405e-b77b-42838eb0aaa9" } } }]""")).Contains("Items._id").Should().BeTrue();
        (await Filter("""[{ "match": { "qrCode": { "eq": "q" } } }]""")).ShouldBeBson(BsonDocument.Parse("{ QRCode: 'q' }"));
        await Error("""[{ "match": { "Number": { "eq": "x" } } }]""", Codes.UnknownPath);
        await Error("""[{ "match": { "QrCode": { "eq": "x" } } }]""", Codes.UnknownPath);
    }

    [Fact]
    public async Task F3_guid_sets_are_typed()
    {
        var filter = await Filter("""[{ "match": { "id": { "in": ["195fb742-82b3-405e-b77b-42838eb0aaa9"] }, "customerId": { "nin": ["195fb742-82b3-405e-b77b-42838eb0aaa9"] } } }]""");

        filter["$and"][0]["_id"]["$in"][0].Should().BeOfType<BsonBinaryData>();
        filter["$and"][1]["CustomerId"]["$nin"][0].Should().BeOfType<BsonBinaryData>();
    }

    [Fact]
    public async Task F4_operands_are_encoded_from_the_kind_and_the_response_form_is_the_request_form()
    {
        var filter = await Filter("""[{ "match": { "when": { "gte": "2026-09-05T00:00:00Z" }, "amount": { "eq": "1003.5" }, "big": { "gte": "1002" }, "flag": { "eq": "false" }, "day": { "eq": "2026-09-03" } } }]""");

        filter["$and"][0]["When"]["$gte"].Should().BeOfType<BsonDateTime>();
        filter["$and"][1]["$or"][0]["Amount"].Should().BeOfType<BsonDecimal128>();
        filter["$and"][2]["Big"]["$gte"].Should().BeOfType<BsonInt64>();
        filter["$and"][3]["Flag"].Should().Be(BsonBoolean.False);
        filter["$and"][4]["Day"].Should().BeOfType<BsonDateTime>();

        // The type-hint wrappers are retired: an object operand is refused, never guessed.
        await Error("""[{ "match": { "when": { "gte": { "$date": "2026-09-05T00:00:00Z" } } } }]""", Codes.InvalidOperand);
        await Error("""[{ "match": { "id": { "eq": { "$uuid": "195fb742-82b3-405e-b77b-42838eb0aaa9" } } } }]""", Codes.InvalidOperand);
    }

    [Fact]
    public async Task F5_malformed_operands_are_client_errors()
    {
        await Error("""[{ "match": { "tags": { "in": "x" } } }]""", Codes.OperandNotArray);
        await Error("""[{ "match": { "number": { "exists": "yes" } } }]""", Codes.InvalidOperand);
        await Error("""[{ "match": { "number": { "startsWith": 5 } } }]""", Codes.InvalidOperand);
        await Error("""[{ "match": { "number": { "contains": null } } }]""", Codes.InvalidOperand);
        await Error("""[{ "match": { "number": { "exists": { "$var": "v" } } } }]""", Codes.UnboundVariable);
        await Error("""[{ "match": { "and": [] } }]""", Codes.EmptyLogicalGroup);
        await Error("""[{ "match": { "or": [] } }]""", Codes.EmptyLogicalGroup);
    }

    [Fact]
    public async Task F6_an_unbound_variable_is_refused()
    {
        await Error("""[{ "match": { "number": { "eq": { "$var": "v" } } } }]""", Codes.UnboundVariable);
        (await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "match": { "number": { "eq": { "$var": "v" } } } }]""", variablesJson: """{ "v": "x" }""")).Stages.Should().HaveCount(2);
    }

    [Fact]
    public async Task F7_exclusion_projections_are_honoured_and_mixed_ones_refused()
    {
        var compiled = MongoCompiler.Compile(await Bound("""[{ "project": { "flag": 0, "organizationId": 0 } }]"""), Options);

        compiled.PageStages[1].ShouldBeBson(BsonDocument.Parse("{ $project: { Flag: 0, OrganizationId: 0 } }"));
        await Error("""[{ "project": { "number": 1, "flag": 0 } }]""", Codes.MixedProjection);
    }

    [Fact]
    public async Task F9_the_flat_legacy_condition_shape_is_refused_and_every_operator_counts()
    {
        await Error("""[{ "match": { "path": "number", "op": "eq", "value": "x" } }]""", Codes.UnknownPath);

        var bound = await Bound("""[{ "match": { "when": { "gte": "2026-01-01T00:00:00Z", "lte": "2026-12-31T00:00:00Z" }, "number": { "eq": "x" }, "flag": { "eq": true } } }]""");

        ((BoundCondition.And)((BoundStage.Match)bound.Stages[0]).Condition).Conditions.Should().HaveCount(4, "X1: every operator and every path is a condition");
    }

    [Fact]
    public async Task F10_entity_ids_are_exact()
    {
        await Error("[]", Codes.UnknownEntity, "PROBE.ORDER");
        await Error("[]", Codes.UnknownEntity, "Probe.Order");
        (await Bound("[]")).Entity.Id.Should().Be(Order);
    }

    [Fact]
    public async Task F11_enum_operands_are_names_or_numbers_and_nin_over_a_name_is_typed()
    {
        (await Filter("""[{ "match": { "state": { "nin": ["Open"] } } }]""")).ShouldBeBson(BsonDocument.Parse("{ State: { $nin: [0] } }"));
        (await Filter("""[{ "match": { "state": { "eq": "Shipped" } } }]""")).ShouldBeBson(BsonDocument.Parse("{ State: 1 }"));
        (await Filter("""[{ "match": { "state": { "in": ["Open", "Shipped"] } } }]""")).ShouldBeBson(BsonDocument.Parse("{ State: { $in: [0, 1] } }"));
        await Error("""[{ "match": { "state": { "eq": "NOT_CONVERTED" } } }]""", Codes.UnknownEnumMember);
        await Error("""[{ "match": { "state": { "contains": "Not" } } }]""", Codes.InvalidOperand);
    }

    [Fact]
    public async Task F12_ignoreCase_restates_the_fold_every_string_operator_makes_under_the_collation()
    {
        (await Filter("""[{ "match": { "number": { "startsWith": "templatename", "options": { "ignoreCase": true } } } }]""")).ShouldBeBson(BsonDocument.Parse("{ Number: { $gte: 'templatename', $lt: 'templatename￿' } }"));
        (await Filter("""[{ "match": { "number": { "endsWith": "NAME 1", "options": { "ignoreCase": true } } } }]""")).ShouldBeBson(BsonDocument.Parse("{ Number: /NAME\\ 1$/i }"));
        (await Filter("""[{ "match": { "number": { "in": ["templatename 1"], "options": { "ignoreCase": true } } } }]""")).ShouldBeBson(BsonDocument.Parse("{ Number: { $in: [ 'templatename 1' ] } }"));
        await Error("""[{ "match": { "number": { "regex": "^templatename", "options": { "ignoreCase": true } } } }]""", Codes.OptionNotApplicable);
    }

    [Fact]
    public async Task F13_a_grouped_query_pages_by_offset_behind_the_cursor()
    {
        var bound = await Bound("""[{ "group": { "by": [{ "path": "state", "as": "type" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");
        var compiled = MongoCompiler.Compile(bound, Options);

        bound.PagingMode.Should().Be(PagingMode.Offset);
        compiled.PageStages.Should().ContainSingle(stage => stage.Contains("$sort"), "a grouped page is ordered by its keys or $skip pages an order Mongo never produced")
            .Which["$sort"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument("type", 1), "the group keys order the page; no key is appended to a grouped shape");
        MongoQueryEngine.NextCursor(compiled, new BsonDocument("type", 1)).Should().BeEquivalentTo(new CursorPayload(bound.Fingerprint, PagingMode.Offset, [], 1));
    }

    [Fact]
    public async Task F14_the_scope_is_engine_owned_and_leads_the_pipeline_whatever_the_caller_writes()
    {
        var compiled = MongoCompiler.Compile(await Bound("""[{ "project": { "id": 1, "number": 1, "flag": 1 } }, { "match": { "flag": { "eq": false } } }, { "page": { "limit": 20, "includeTotalCount": true } }]"""), Options);

        compiled.PageStages[0]["$match"].AsBsonDocument.Names.Should().Equal("OrganizationId");
        compiled.PageStages[1].Contains("$project").Should().BeTrue();
        compiled.PageStages[2].ShouldBeBson(BsonDocument.Parse("{ $match: { Flag: false } }"));
        compiled.CountStages![0].ShouldBeBson(compiled.PageStages[0]);

        // A caller cannot displace it: organizationId in a condition is an ordinary member, the scope stays.
        var displaced = MongoCompiler.Compile(await Bound("""[{ "match": { "organizationId": { "eq": "00000000-0000-0000-0000-000000000001" } } }]"""), Options);

        displaced.PageStages[0]["$match"]["OrganizationId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).Should().Be(BindHost.Organisation);
    }

    [Fact]
    public async Task T2_any_is_the_correlation_form()
    {
        (await Filter("""[{ "match": { "items": { "any": { "quantity": { "gt": 1 }, "price.currency": { "eq": "EUR" } } } } }]""")).Contains("Items").Should().BeTrue();
        await Error("""[{ "match": { "items": { "elemMatch": { "quantity": { "gt": 1 } } } } }]""", Codes.UnknownOperator);
    }

    [Fact]
    public async Task T5_guid_equality_is_one_typed_comparison()
    {
        var filter = await Filter("""[{ "match": { "id": { "eq": "195FB74282B3405EB77B42838EB0AAA9" } } }]""");

        filter.Names.Should().Equal("_id");
        filter["_id"].Should().BeOfType<BsonBinaryData>("no three-way $or");
    }

    [Fact]
    public async Task T6_a_retired_id_answers_as_the_current_entity()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, "probe.orders_old", "[]");

        bound.Entity.Id.Should().Be(Order);
        bound.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == Codes.EntityIdRetired);
    }

    [Fact]
    public async Task T7_joins_name_entities_and_follow_declared_references()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, "probe.customer", """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }]""");

        ((BoundStage.Lookup)bound.Stages[0]).From.Collection.Should().Be("orders", "the collection is the model's, never the caller's");
        await Error("""[{ "lookup": { "from": "customers", "path": "customerId", "as": "orders" } }]""", Codes.UnknownEntity, "probe.customer");
        await Error("""[{ "resolve": { "source": "crm.customer", "localPath": "customerId", "as": "c" } }]""", Codes.UnknownStageMember);
    }
}
