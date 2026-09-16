using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.AspNetCore.Compat;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// Contract 1 compatibility (design §12): storage and CLR spellings resolved relative to the
/// folded shape, alias roots included; the v1 type hints unwrapped; v1 joins refused.
/// </summary>
public class CompatBinderTests
{
    private const string Order = "probe.order";

    private static readonly CompatBinder Compat = new(BindHost.Probe);

    private static CompatRewrite Rewrite(string pipeline, string? variables = null) =>
        Compat.Rewrite(BindHost.Request(Order, pipeline, variables));

    private static FilterCondition Condition(CompatRewrite rewrite, int stage = 0) =>
        rewrite.Request.Pipeline[stage].Match!.Condition!;

    [Theory]
    [InlineData("Number", "number")]
    [InlineData("_id", "id")]
    [InlineData("Id", "id")]
    [InlineData("OrganizationId", "organizationId")]
    [InlineData("ShipTo.City", "shipTo.city")]
    [InlineData("Items.Quantity", "items.quantity")]
    [InlineData("Items._id", "items.id")]
    [InlineData("Items.Id", "items.id")]
    [InlineData("Items.Price.Net", "items.price.net")]
    [InlineData("QRCode", "qrCode")]
    [InlineData("x", "renamed")]
    [InlineData("Renamed", "renamed")]
    [InlineData("Prices.EUR.Net", "prices.EUR.net")]
    [InlineData("number", "number")]
    [InlineData("shipTo.city", "shipTo.city")]
    [InlineData("NoSuchMember.Deep", "NoSuchMember.Deep")]
    [InlineData("ShipTo.Nope", "shipTo.Nope")]
    public void Storage_and_clr_spellings_resolve_to_the_wire_spelling(string written, string wire)
    {
        var rewrite = Rewrite($$"""[{ "match": { "{{written}}": { "eq": "a" } } }]""");

        Condition(rewrite).Path.Should().Be(wire);

        if (written != wire)
        {
            rewrite.LegacyPaths.Should().Be(1);
            rewrite.FirstLegacyPath.Should().Be(written);
        }
        else
        {
            rewrite.LegacyPaths.Should().Be(0);
            rewrite.FirstLegacyPath.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_rewritten_storage_path_binds_to_its_storage()
    {
        var rewrite = Rewrite("""[{ "match": { "x": { "eq": "a" }, "Items.Quantity": { "gt": 1 } } }, { "sort": [{ "ShipTo.City": "DESC" }] }]""");
        var bound = await BindHost.BindAsync(BindHost.Probe, rewrite.Request, BindHost.Context(contract: 1));

        bound.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(bound));

        var pipeline = ((BindOutcome.Bound)bound).Pipeline;
        var leaves = ((BoundCondition.And)((BoundStage.Match)pipeline.Stages[0]).Condition).Conditions.Cast<BoundCondition.Leaf>().ToList();

        leaves[0].Path.Storage.Should().Be("x");
        leaves[1].Path.Storage.Should().Be("Items.Quantity");
        pipeline.Sort!.Fields[0].Path.Storage.Should().Be("ShipTo.City");
        pipeline.Sort.Fields[0].Ascending.Should().BeFalse("the direction is read case-insensitively in compat mode");
    }

    [Fact]
    public void Paths_resolve_relative_to_the_folded_shape_including_alias_roots()
    {
        var rewrite = Rewrite("""
            [
              { "unwind": { "path": "Items", "as": "it", "includeIndex": "i" } },
              { "match": { "it.Quantity": { "gt": 1 }, "Items.Price.Net": { "gt": "1" }, "i": { "eq": 0 } } },
              { "sort": [{ "it.Price.Net": "asc" }] },
              { "project": { "Number": 1, "it": { "Quantity": 1 } } }
            ]
            """);

        rewrite.Refusal.Should().BeNull();
        rewrite.Request.Pipeline[0].Unwind!.Path.Should().Be("items");

        var conditions = Condition(rewrite, 1).And!;

        conditions[0].Path.Should().Be("it.quantity");
        conditions[1].Path.Should().Be("items.price.net");
        conditions[2].Path.Should().Be("i");
        rewrite.Request.Pipeline[2].Sort![0].Path.Should().Be("it.price.net");
        rewrite.Request.Pipeline[3].Project!.Fields.Keys.Should().Equal("number", "it.quantity");
        rewrite.FirstLegacyPath.Should().Be("Items");
    }

    [Fact]
    public void Group_outputs_are_verbatim_and_their_inputs_are_translated()
    {
        var rewrite = Rewrite("""
            [
              { "group": { "by": [{ "path": "State", "as": "st" }, { "dateTrunc": { "path": "When", "unit": "day" }, "as": "day" }], "fields": { "n": { "sum": { "path": "Count" } }, "total": { "sum": { "add": [{ "path": "Amount" }, { "literal": 1 }] } } } } },
              { "sort": [{ "st": "asc" }] },
              { "match": { "n": { "gt": 1 } } }
            ]
            """);

        var group = rewrite.Request.Pipeline[0].Group!;

        group.By[0].Path.Should().Be("state");
        group.By[1].DateTrunc!.Path.Should().Be("when");
        group.Fields["n"].Argument!.Path.Should().Be("count");
        group.Fields["total"].Argument!.Operands![0].Path.Should().Be("amount");
        rewrite.Request.Pipeline[1].Sort![0].Path.Should().Be("st");
        Condition(rewrite, 2).Path.Should().Be("n");
    }

    [Fact]
    public void Any_conditions_are_translated_relative_to_the_element()
    {
        var rewrite = Rewrite("""[{ "match": { "Items": { "any": { "Quantity": { "gt": 1 }, "Price.Currency": "EUR" } } } }]""");
        var condition = Condition(rewrite);

        condition.Path.Should().Be("items");
        condition.Any!.And![0].Path.Should().Be("quantity");
        condition.Any.And[1].Path.Should().Be("price.currency");
    }

    [Theory]
    [InlineData("""{ "$date": "2024-01-15T10:30:00" }""", "when", "\"2024-01-15T10:30:00Z\"")]
    [InlineData("""{ "$date": "2024-01-15T10:30:00+02:00" }""", "when", "\"2024-01-15T08:30:00Z\"")]
    [InlineData("""{ "$uuid": "195fb742-82b3-405e-b77b-42838eb0aaa9" }""", "customerId", "\"195fb742-82b3-405e-b77b-42838eb0aaa9\"")]
    [InlineData("""{ "$uuid3": "195fb742-82b3-405e-b77b-42838eb0aaa9" }""", "customerId", "\"195fb742-82b3-405e-b77b-42838eb0aaa9\"")]
    [InlineData("""{ "$long": "9007199254740993" }""", "big", "\"9007199254740993\"")]
    [InlineData("""{ "$long": 42 }""", "big", "\"42\"")]
    [InlineData("""{ "$decimal": "19.99" }""", "amount", "\"19.99\"")]
    [InlineData("""{ "$null": true }""", "note", "null")]
    public async Task Type_hints_unwrap_into_the_wire_encoding_and_bind(string hinted, string path, string expected)
    {
        var rewrite = Rewrite($$"""[{ "match": { "{{path}}": { "eq": {{hinted}} } } }]""");
        var condition = Condition(rewrite);

        condition.Value!.Value.GetRawText().Should().Be(expected);
        rewrite.TypeHints.Should().Be(1);
        rewrite.FirstLegacyPath.Should().Be(path);

        var bound = await BindHost.BindAsync(BindHost.Probe, rewrite.Request, BindHost.Context(contract: 1));

        bound.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(bound));
    }

    [Fact]
    public async Task A_date_hint_becomes_a_typed_instant()
    {
        var rewrite = Rewrite("""[{ "match": { "When": { "gte": { "$date": "2024-01-15T10:30:00" } } } }]""");
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, JsonSerializer.Serialize(rewrite.Request.Pipeline, BindHost.Json), BindHost.Context(contract: 1));
        var leaf = (BoundCondition.Leaf)((BoundStage.Match)bound.Stages[0]).Condition;

        leaf.Op.Should().Be("gte");
        ((BoundOperand.Single)leaf.Operand).Value.Should().Be(new BsonDateTime(new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void A_regex_hint_becomes_the_regex_operator()
    {
        var rewrite = Rewrite("""[{ "match": { "Number": { "eq": { "$regex": "^ab" } } } }]""");
        var condition = Condition(rewrite);

        condition.Op.Should().Be("regex");
        condition.Value!.Value.GetString().Should().Be("^ab");
    }

    [Fact]
    public void Hints_inside_a_set_and_inside_variables_are_unwrapped()
    {
        var rewrite = Rewrite(
            """[{ "match": { "CustomerId": { "in": [{ "$uuid": "195fb742-82b3-405e-b77b-42838eb0aaa9" }, "295fb742-82b3-405e-b77b-42838eb0aaa9"] }, "When": { "gte": { "$var": "since" } } } }]""",
            """{ "since": { "$date": "2024-01-01" } }""");

        var set = Condition(rewrite).And![0];

        set.Value!.Value.EnumerateArray().Select(item => item.GetString()).Should().Equal("195fb742-82b3-405e-b77b-42838eb0aaa9", "295fb742-82b3-405e-b77b-42838eb0aaa9");
        ((JsonElement)rewrite.Request.Variables!.Values["since"]!).GetString().Should().Be("2024-01-01T00:00:00Z");
        rewrite.TypeHints.Should().Be(2);
    }

    [Fact]
    public void Operators_and_directions_are_read_case_insensitively()
    {
        var rewrite = Rewrite("""[{ "match": { "Number": { "startswith": "a", "IN": ["b"] } } }, { "sort": [{ "Number": "Desc" }] }]""");
        var conditions = Condition(rewrite).And!;

        conditions[0].Op.Should().Be("startsWith");
        conditions[1].Op.Should().Be("in");
        rewrite.Request.Pipeline[1].Sort![0].Direction.Should().Be("desc");
    }

    [Fact]
    public void A_variable_wrapper_and_a_plain_object_are_left_alone()
    {
        var rewrite = Rewrite("""[{ "match": { "Number": { "eq": { "$var": "n" } }, "Note": { "eq": { "a": 1, "b": 2 } } } }]""");
        var conditions = Condition(rewrite).And!;

        conditions[0].Value!.Value.GetProperty("$var").GetString().Should().Be("n");
        conditions[1].Value!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        rewrite.TypeHints.Should().Be(0);
    }

    [Theory]
    [InlineData("""[{ "lookup": { "from": "probe.customer", "localPath": "CustomerId", "foreignPath": "id", "as": "customer" } }]""", "lookup")]
    [InlineData("""[{ "match": {} }, { "resolve": { "source": "crm.contact", "localPath": "ContactNumber", "as": "contact" } }]""", "resolve")]
    [InlineData("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }]""", "lookup")]
    public void A_join_stage_under_contract_1_is_refused_as_a_legacy_stage(string pipeline, string kind)
    {
        var rewrite = Rewrite(pipeline);

        rewrite.Refusal.Should().NotBeNull();
        rewrite.Refusal!.Status.Should().Be(400);
        rewrite.Refusal.Errors![0].Code.Should().Be(Codes.LegacyStageUnsupported);
        rewrite.Refusal.Errors[0].Stage.Should().Be(pipeline.Contains("\"match\"") ? 1 : 0);
        rewrite.Refusal.Errors[0].Message.Should().Contain(kind);
    }

    [Fact]
    public void An_unknown_entity_leaves_every_path_as_written()
    {
        var rewrite = Compat.Rewrite(BindHost.Request("probe.nope", """[{ "match": { "Number": { "eq": "a" } } }]"""));

        Condition(rewrite).Path.Should().Be("Number");
        rewrite.LegacyPaths.Should().Be(0);
    }
}
