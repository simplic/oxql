using System.Diagnostics;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Model;

namespace OxQL.Mongo;

/// <summary>
/// What a runner knows of the aggregate it is running that its caller cannot see from the rows:
/// when the command's own reply arrived, how many further round trips fetched the rest, and the
/// round-trip time the driver last measured to the server. A runner that knows fills in
/// <see cref="Current"/>; one that does not (a test's) leaves it, and the timing says less.
/// </summary>
internal sealed class AggregateRunStats
{
    private static readonly AsyncLocal<AggregateRunStats?> Running = new();

    /// <summary>The aggregate being run on this flow, or null when nobody measures it.</summary>
    public static AggregateRunStats? Current => Running.Value;

    /// <summary>When the command's own reply arrived (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    public long? FirstBatch { get; set; }

    /// <summary>How many <c>getMore</c> round trips followed the command.</summary>
    public int? GetMores { get; set; }

    /// <summary>The server's round-trip time as the driver last measured it, in milliseconds.</summary>
    public double? RoundTripMs { get; set; }

    internal static AggregateRunStats Begin() => Running.Value = new AggregateRunStats();
}

/// <summary>
/// The engine's runner with every aggregate measured: what it was for, the collection, from when it
/// left to when its last row was read, the rows it returned, and what the runner underneath says of
/// it (<see cref="AggregateRunStats"/>). It decorates whatever runner the host registered, so the
/// database's share of a request is known for every command the engine sends, whoever runs it.
/// </summary>
internal sealed class TimedAggregateRunner(IAggregateRunner inner) : IAggregateRunner
{
    private readonly IAggregateRunner inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The runner underneath.</summary>
    public IAggregateRunner Inner => inner;

    /// <summary>The deadline every aggregate has on this host's side beside its time limit; null runs them without one.</summary>
    public AggregateDeadline? Deadline { get; init; }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken) =>
        Deadline is { } deadline
            ? deadline.RunAsync(inner, entity, stages, options, cancellationToken)
            : inner.AggregateAsync(entity, stages, options, cancellationToken);

    /// <summary>Runs the aggregate and records it in <paramref name="timing"/> as a command of <paramref name="kind"/>; without a collector it runs unmeasured.</summary>
    public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(string kind, QueryTiming? timing, EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (timing is null)
            return await AggregateAsync(entity, stages, options, cancellationToken).ConfigureAwait(false);

        var command = timing.Begin(kind, entity.Id);
        var stats = AggregateRunStats.Begin();
        IReadOnlyList<BsonDocument> rows;

        try
        {
            rows = await AggregateAsync(entity, stages, options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            command.Fail();
            throw;
        }

        if (stats.FirstBatch is { } first)
            command.FirstBatchAt(first);

        command.End(rows.Count, stats.GetMores);

        if (stats.RoundTripMs is { } roundTrip)
            timing.RoundTrip(roundTrip);

        // The size of the reply is not known to the driver's caller: it is measured only for a request
        // that asked for the breakdown, and the measuring is taken out of the request's time.
        if (timing.Detailed)
        {
            var sizing = QueryTiming.Now;
            long bytes = 0;

            foreach (var row in rows)
                bytes += row.ToBson().Length;

            command.Sized(bytes);
            timing.Exclude(sizing);
        }

        return rows;
    }
}
