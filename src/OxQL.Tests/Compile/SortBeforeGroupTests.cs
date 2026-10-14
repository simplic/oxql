using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// A sort a group follows. Where the group has an aggregate whose value is a matter of the order
/// its rows come in (<c>first</c>, <c>last</c>, <c>push</c>), the sort is completed by the entity's
/// key and the positions of the elements unwound before it, so rows that tie reach the group in one
/// order. Where it has none, the sort is sent as it was written: the index that serves it serves
/// it, and the database may still fold the sort into the group.
/// </summary>
public sealed class SortBeforeGroupTests
{
    private const string Order = "probe.order";
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    /// <summary>The sorts of the compiled page: the one the request wrote first, then the order of the groups.</summary>
    private static async Task<List<BsonDocument>> Sorts(string pipeline, RequestContext? context = null) =>
        MongoCompiler.Compile(await BindHost.BoundAsync(BindHost.Probe, Order, pipeline, context), Options).PageStages
            .Where(stage => stage.Contains("$sort")).Select(stage => stage["$sort"].AsBsonDocument).ToList();

    private static string Group(string fields) => $$"""{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { {{fields}} } } }""";

    [Theory]
    [InlineData(""" "v": { "first": "number" } """)]
    [InlineData(""" "v": { "last": "number" } """)]
    [InlineData(""" "v": { "push": "number" } """)]
    [InlineData(""" "n": { "count": true }, "v": { "first": "number" } """)]
    public async Task A_group_whose_aggregate_reads_the_order_has_the_sort_completed_by_the_key(string fields)
    {
        var sorts = await Sorts($$"""[{ "sort": [{ "count": "desc" }] }, {{Group(fields)}}]""");

        sorts[0].ToJson().Should().Be(BsonDocument.Parse("{ Count: -1, _id: 1 }").ToJson());
    }

    [Theory]
    [InlineData(""" "n": { "count": true } """)]
    [InlineData(""" "v": { "sum": "count" } """)]
    [InlineData(""" "v": { "avg": "count" } """)]
    [InlineData(""" "v": { "min": "number" }, "w": { "max": "number" } """)]
    [InlineData(""" "v": { "countDistinct": "number" } """)]
    public async Task A_group_whose_aggregates_do_not_read_the_order_leaves_the_sort_as_written(string fields)
    {
        var sorts = await Sorts($$"""[{ "sort": [{ "count": "desc" }] }, {{Group(fields)}}]""");

        sorts[0].ToJson().Should().Be(BsonDocument.Parse("{ Count: -1 }").ToJson());
    }

    [Fact]
    public async Task A_contract_1_request_is_compiled_as_written()
    {
        var compiled = MongoCompiler.Compile(
            await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{ "sort": [{ "count": "desc" }] }, {{Group(""" "v": { "first": "number" } """)}}]""", BindHost.Context(contract: 1)),
            Options with { AsWritten = true });

        compiled.PageStages.First(stage => stage.Contains("$sort"))["$sort"].ToJson().Should().Be(BsonDocument.Parse("{ Count: -1 }").ToJson());
    }
}
