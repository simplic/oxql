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
}
