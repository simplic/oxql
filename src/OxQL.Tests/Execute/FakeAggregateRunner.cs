using MongoDB.Bson;
using OxQL.Model;
using OxQL.Mongo;

namespace OxQL.Tests.Execute;

/// <summary>Answers the page aggregate with fixture rows and the count aggregate with a count; records every call.</summary>
internal sealed class FakeAggregateRunner : IAggregateRunner
{
    public List<BsonDocument> PageRows { get; set; } = [];

    public long? Count { get; set; }

    public Exception? Fail { get; set; }

    public List<(EntityDef Entity, IReadOnlyList<BsonDocument> Stages, AggregateRunOptions Options)> Calls { get; } = [];

    public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
    {
        Calls.Add((entity, stages, options));

        if (Fail is not null)
            throw Fail;

        var isCount = stages.Count > 0 && stages[^1].Contains("$count");

        return Task.FromResult<IReadOnlyList<BsonDocument>>(isCount
            ? Count is { } count ? [new BsonDocument("n", count)] : []
            : PageRows);
    }
}
