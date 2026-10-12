using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// A condition nests at most <see cref="Binder.MaxConditionDepth"/> levels of and, or, not and any.
/// At the bound the compiled pipeline still serializes inside the aggregate command, also nested in
/// a lookup's sub-pipeline; beyond it the binder refuses the request once, so it never reaches the
/// driver's nesting limit as a fault.
/// </summary>
public class ConditionDepthTests
{
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    /// <summary>A condition of <paramref name="levels"/> levels: groups alternating between and, not, or and not, around one leaf.</summary>
    private static string Nested(int levels)
    {
        var condition = """{ "number": { "eq": "x" } }""";

        for (var level = 1; level < levels; level++)
            condition = (level % 4) switch
            {
                0 => $$"""{ "and": [{{condition}}] }""",
                2 => $$"""{ "or": [{{condition}}, { "number": { "eq": "y" } }] }""",
                _ => $$"""{ "not": {{condition}} }""",
            };

        return condition;
    }

    /// <summary>Serializes the page stages as the driver sends them, inside the aggregate command.</summary>
    private static void SerializesAsSent(CompiledQuery compiled) =>
        new BsonDocument { ["aggregate"] = "c", ["pipeline"] = new BsonArray(compiled.PageStages), ["cursor"] = new BsonDocument() }.ToBson().Should().NotBeEmpty();

    [Fact]
    public async Task A_match_at_the_bound_compiles_and_serializes()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, "probe.order", $$"""[{ "match": {{Nested(Binder.MaxConditionDepth)}} }]""");

        SerializesAsSent(MongoCompiler.Compile(bound, Options));
    }

    [Fact]
    public async Task A_lookup_filter_at_the_bound_compiles_and_serializes()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, "probe.customer", $$"""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "filter": {{Nested(Binder.MaxConditionDepth)}} } }]""");

        SerializesAsSent(MongoCompiler.Compile(bound, Options));
    }

    [Fact]
    public async Task One_level_more_is_refused_once_with_the_conditions_code()
    {
        var refused = await BindHost.RefusedAsync(BindHost.Probe, "probe.order", $$"""[{ "match": {{Nested(Binder.MaxConditionDepth + 1)}} }, { "match": {{Nested(Binder.MaxConditionDepth + 2)}} }]""");

        refused.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.MaxConditionsExceeded);
        refused.Errors![0].Message.Should().Be($"A condition nests more than {Binder.MaxConditionDepth} levels of and, or, not and any; flatten the groups.");
        refused.Errors[0].Stage.Should().Be(0);
    }

    /// <summary>An aggregate argument of <paramref name="levels"/> levels of arithmetic around one path.</summary>
    private static string Arithmetic(int levels)
    {
        var argument = "\"count\"";

        for (var level = 0; level < levels; level++)
            argument = $$"""{ "add": [{{argument}}, 1] }""";

        return $$"""[{ "group": { "by": [], "fields": { "x": { "sum": {{argument}} } } } }]""";
    }

    /// <summary>A request read as a host reads it, as deep as the wire goes.</summary>
    private static async Task<BindOutcome> BindAsWiredAsync(string pipeline, string? variables = null)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<OxQL.Core.Models.QueryRequest>(
            $$"""{ "entityType": "probe.order", {{(variables is null ? "" : $"\"variables\": {variables},")}} "pipeline": {{pipeline}} }""", OxQL.Core.Models.OxQLJson.Wire)!;

        return await BindHost.BindAsync(BindHost.Probe, request);
    }

    [Fact]
    public async Task An_aggregate_argument_at_the_bound_compiles_and_serializes()
    {
        var bound = (await BindAsWiredAsync(Arithmetic(Binder.MaxExpressionDepth))).Should().BeOfType<BindOutcome.Bound>().Subject.Pipeline;

        SerializesAsSent(MongoCompiler.Compile(bound, Options));
    }

    [Fact]
    public async Task An_aggregate_argument_one_level_deeper_is_refused_once()
    {
        var refused = (await BindAsWiredAsync(Arithmetic(Binder.MaxExpressionDepth + 30))).Should().BeOfType<BindOutcome.Failed>().Subject.Refusal;

        refused.Status.Should().Be(400);
        refused.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.InvalidAggregateArgument);
        (await BindAsWiredAsync(Arithmetic(Binder.MaxExpressionDepth + 1))).Should().BeOfType<BindOutcome.Failed>().Which.Refusal.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task A_resolve_filter_at_the_bound_that_holds_a_variable_binds()
    {
        var filter = """{ "number": { "in": [{ "$var": "n" }, "y"] } }""";

        for (var level = 1; level < Binder.MaxConditionDepth; level++)
            filter = $$"""{ "and": [{{filter}}] }""";

        (await BindAsWiredAsync($$"""[{ "resolve": { "path": "customerId", "as": "c", "filter": {{filter}} } }]""", """{ "n": "x" }""")).Should().BeOfType<BindOutcome.Bound>();
    }
}
