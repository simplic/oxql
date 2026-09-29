using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The outcome record of a row whose collection is absent or empty under <c>elements</c> (RE-18): it
/// is <c>reference_null</c> with no element to name, under <c>first</c> and under <c>all</c> alike.
/// </summary>
public class KeyedOutcomeDetailTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static async Task<ResolveResult> FetchAsync(string elements, BsonValue? customerIds)
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, $$"""[{ "resolve": { "path": "customerIds", "as": "c", "elements": "{{elements}}" } }]""");
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options);
        var fetch = new KeyedFetch(new FakeRemoteClient(), engine, new OwnerFetchCache(options), options);
        var row = new BsonDocument { ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) };

        if (customerIds is not null)
            row["CustomerIds"] = customerIds;

        return await fetch.ByKeysAsync(compiled, [row], BindHost.Context(), TimeSpan.FromSeconds(5), strict: false, CancellationToken.None);
    }

    [Theory]
    [InlineData("first", false)]
    [InlineData("first", true)]
    [InlineData("all", false)]
    [InlineData("all", true)]
    public async Task An_absent_or_empty_collection_is_reference_null_with_no_element(string elements, bool present)
    {
        var result = await FetchAsync(elements, present ? new BsonArray() : null);

        result.Outcomes.Should().ContainSingle().Which.Should().Be(new KeyedRowOutcome(0, "c", 0, null, null, KeyedOutcome.ReferenceNull));
    }
}
