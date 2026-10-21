using System.Text.Json;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The params a binding refusal carries so that a client says it in its own words from the code
/// alone: which name collides and what holds it, why a stage cannot continue under an alias, the
/// targets and collections of a join, why a member cannot be filtered or sorted, the value that is
/// off a closed list with the list, the variable that is not bound, and the limit a request is over.
/// Every member is added to what the code carried before; a contract 1 request reads what it always read.
/// </summary>
public class MessageParamsTests
{
    private const string Order = "probe.order";

    private static string Json(QueryValidationError error) => JsonSerializer.Serialize(error.Params);

    private static Task<QueryValidationError> Probe(string pipeline, string code, RequestContext? context = null, string? variables = null) =>
        BindHost.ErrorAsync(BindHost.Probe, Order, pipeline, code, context, variables);

    private static Task<QueryValidationError> Invoice(string pipeline, string code) =>
        BindHost.ErrorAsync(ResolveModel.Model, ResolveModel.Invoice, pipeline, code);

    // ---- ALIAS_COLLISION, INVALID_ALIAS --------------------------------------------------------

    [Fact]
    public async Task A_colliding_alias_names_itself_its_member_and_what_holds_the_name()
    {
        (Json(await Probe("""[{ "unwind": { "path": "items", "as": "number" } }]""", Codes.AliasCollision)))
            .Should().Be("""{"alias":"number","member":"as","takenBy":{"root":true}}""", "a member of the entity holds the name");

        (Json(await Probe("""[{ "unwind": { "path": "items", "as": "it" } }, { "unwind": { "path": "it.stops", "as": "it" } }]""", Codes.AliasCollision)))
            .Should().Be("""{"alias":"it","member":"as","takenBy":{"stage":0}}""", "the stage that added the name holds it");

        (Json(await Probe("""[{ "unwind": { "path": "items", "as": "it", "includeIndex": "it" } }]""", Codes.AliasCollision)))
            .Should().Be("""{"alias":"it","member":"includeIndex","takenBy":{"stage":0,"member":"as"}}""");

        (Json(await Probe("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "st": { "count": true } } } }]""", Codes.AliasCollision)))
            .Should().Be("""{"alias":"st","member":"groupAs","takenBy":{"stage":0,"member":"groupAs"}}""");

        (Json(await Invoice("""[{ "resolve": { "path": "billingLineId", "as": "r", "parentAs": "r" } }]""", Codes.AliasCollision)))
            .Should().Be("""{"alias":"r","member":"parentAs","takenBy":{"stage":0,"member":"as"}}""");
    }

    [Theory]
    [InlineData("""[{ "unwind": { "path": "items", "as": "bad name" } }]""", "as", "notIdentifier")]
    [InlineData("""[{ "unwind": { "path": "items", "as": "it", "includeIndex": "_id" } }]""", "includeIndex", "keyName")]
    [InlineData("""[{ "group": { "by": [{ "path": "state", "as": "__k" }], "fields": {} } }]""", "groupAs", "reserved")]
    public async Task An_alias_that_cannot_be_used_names_its_member_and_why(string pipeline, string member, string reason)
    {
        var error = await Probe(pipeline, Codes.InvalidAlias);

        error.Params.Should().Contain("member", member).And.Contain("reason", reason);
    }

    [Fact]
    public async Task A_contract_1_request_reads_the_collision_without_params()
    {
        var error = await Probe("""[{ "unwind": { "path": "items", "as": "number" } }]""", Codes.AliasCollision, BindHost.Context(contract: 1));

        error.Params.Should().BeNull();
    }

    // ---- NOT_CONTINUABLE -----------------------------------------------------------------------

    [Fact]
    public async Task A_stage_that_cannot_continue_under_an_alias_names_why_its_kind_and_the_alias()
    {
        (Json(await Invoice("""[{ "resolve": { "path": "customerIds", "as": "cs", "elements": "all" } }, { "resolve": { "path": "cs.id", "as": "c" } }]""", Codes.NotContinuable)))
            .Should().Be("""{"reason":"elementsAll","kind":"resolve","alias":"cs"}""");

        (Json(await Invoice("""[{ "resolve": { "path": "contactId", "as": "r" } }, { "group": { "by": [{ "path": "number", "as": "number" }], "fields": { "top": { "max": "r.name" } } } }]""", Codes.NotContinuable)))
            .Should().Be("""{"reason":"joinsOnly","kind":"group","alias":"r"}""");
    }

    // ---- joins and collections -----------------------------------------------------------------

    [Fact]
    public async Task A_join_refused_for_its_target_or_a_collection_names_them()
    {
        (Json(await Invoice("""[{ "resolve": { "path": "localSource.id", "as": "bl", "target": "rc.invoice" } }]""", Codes.ResolveTargetNotDeclared)))
            .Should().Be("""{"target":"rc.invoice","targets":["rc.shipment","rc.tour","rc.customer"]}""");

        (Json(await Invoice("""[{ "resolve": { "path": "lines.customerId", "as": "c" } }]""", Codes.ResolveOnCollection)))
            .Should().Be("""{"collection":"lines"}""");

        (Json(await Invoice("""[{ "resolve": { "path": "lines.parts.customerId", "as": "c", "elements": "first" } }]""", Codes.UnwindOrder)))
            .Should().Be("""{"reason":"crossesCollections","collections":["lines","lines.parts"]}""");

        (Json(await Probe("""[{ "unwind": { "path": "items.stops", "as": "stop" } }]""", Codes.UnwindOrder)))
            .Should().Be("""{"reason":"outerCollection","collection":"items"}""");

        (Json(await Invoice("""[{ "resolve": { "path": "customerId", "as": "c", "parentAs": "p" } }]""", Codes.ResolveParentNotItem)))
            .Should().Be("""{"entity":"rc.customer"}""");

        (Json(await Probe("""[{ "group": { "by": [{ "path": "items.quantity", "as": "q" }], "fields": {} } }]""", Codes.GroupOnCollection)))
            .Should().Be("""{"collection":"items"}""");

        (Json(await Probe("""[{ "unwind": { "path": "items", "as": "item", "flatten": "stops" } }]""", Codes.FlattenNotRecursive)))
            .Should().Be("""{"member":"stops","reason":"notRecursive"}""");
    }

    // ---- NOT_FILTERABLE, NOT_SORTABLE ----------------------------------------------------------

    [Fact]
    public async Task A_member_that_cannot_be_filtered_or_sorted_names_why_in_the_words_of_explain()
    {
        var filter = await Probe("""[{ "match": { "shipTo": { "eq": null } } }]""", Codes.NotFilterable);

        filter.Params.Should().Contain("reason", "notScalar").And.Contain("kind", "object");

        (Json(await Probe("""[{ "sort": [{ "items.quantity": "asc" }] }]""", Codes.NotSortable)))
            .Should().Be("""{"reason":"underCollection","kind":"int"}""");
    }

    // ---- closed lists --------------------------------------------------------------------------

    [Fact]
    public async Task A_value_off_a_closed_list_names_the_value_and_the_list()
    {
        var function = await Probe("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "x": { "median": "amount" } } } }]""", Codes.UnknownAggFunction);

        function.Params.Should().Contain("value", "median").And.Contain("field", "x");
        ((IEnumerable<string>)function.Params!["allowed"]!).Should().Contain(["sum", "countDistinct"]);

        var unit = await Probe("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "fortnight" }, "as": "d" }], "fields": {} } }]""", Codes.InvalidDateTruncUnit);

        unit.Params.Should().Contain("member", "unit").And.Contain("value", "fortnight");
        ((IEnumerable<string>)unit.Params!["allowed"]!).Should().Equal("year", "quarter", "month", "week", "day", "hour", "minute", "second");

        var timezone = await Probe("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "day", "timezone": "Nowhere/Never" }, "as": "d" }], "fields": {} } }]""", Codes.InvalidTimezone);

        timezone.Params.Should().Contain("member", "timezone").And.Contain("value", "Nowhere/Never");

        (Json(await Probe("""[{ "sort": [{ "number": { "direction": "up" } }] }]""", Codes.InvalidSortDirection)))
            .Should().Be("""{"value":"up","allowed":["asc","desc"]}""");

        var op = await Probe("""[{ "match": { "number": { "like": "a" } } }]""", Codes.UnknownOperator);

        op.Params.Should().Contain("value", "like");
        ((IEnumerable<string>)op.Params!["allowed"]!).Should().Contain(["eq", "in", "regex"]);
    }

    // ---- UNBOUND_VARIABLE ----------------------------------------------------------------------

    [Fact]
    public async Task A_variable_that_is_not_bound_names_itself()
    {
        (Json(await Probe("""[{ "match": { "number": { "eq": { "$var": "missing" } } } }]""", Codes.UnboundVariable)))
            .Should().Be("""{"name":"missing"}""");

        (Json(await Probe("""[{ "match": { "number": { "in": ["A", { "$var": "absent" }] } } }]""", Codes.UnboundVariable)))
            .Should().Be("""{"name":"absent"}""");
    }

    // ---- limits --------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_over_a_limit_names_the_limit_its_value_and_what_was_asked()
    {
        var context = BindHost.Context(BindHost.Options(options => options.Limits.MaxPipelineStages = 1));

        (Json(await Probe("""[{ "match": { "number": { "eq": "a" } } }, { "sort": [{ "number": "asc" }] }]""", Codes.MaxPipelineStagesExceeded, context)))
            .Should().Be("""{"limit":"maxPipelineStages","max":1,"count":2}""");

        (Json(await Probe("""[{ "page": { "limit": 1000000 } }]""", Codes.PageSizeExceeded)))
            .Should().StartWith("""{"limit":"maxPageSize","max":""").And.EndWith(""","count":1000000}""");
    }
}
