using MongoDB.Bson;
using OxQL.Model;

namespace OxQL.Mongo;

/// <summary>
/// An aggregate that did not answer by its client-side deadline: its time limit and the margin
/// after it passed without the database saying anything, the limit's own expiry included. It is
/// answered as that expiry is (504 <c>QUERY_TIMEOUT</c>).
/// </summary>
internal sealed class AggregateDeadlineException(int maxTimeMs, int marginMs)
    : TimeoutException($"The aggregate did not answer within its {maxTimeMs} ms and the {marginMs} ms after them.")
{
    /// <summary>The time limit the aggregate ran under.</summary>
    public int MaxTimeMs { get; } = maxTimeMs;
}

/// <summary>
/// The deadline an aggregate has on this host's side, beside the time limit the database applies
/// to it (<c>maxTimeMS</c>). The database ends an aggregate that reaches its limit and says so, and
/// that is the answer in every ordinary case. The deadline is for the case in which nothing comes
/// back: the limit, then <see cref="MarginMs"/> for the database's own answer to arrive, and then
/// the aggregate is given up on (its cancellation is asked for) and the request is answered as one
/// that timed out, instead of waiting for as long as the connection does.
/// <para>
/// The time is wall-clock on this host from the call of the runner to its answer: the selection of
/// a server, the wait for a pooled connection and the transfer of the rows are in it, the limit of
/// the database is for its own work alone. The margin is what covers the difference.
/// </para>
/// <para>
/// The clock is <see cref="Time"/>, so a test moves it. A margin below zero switches the deadline off.
/// </para>
/// </summary>
internal sealed record AggregateDeadline(TimeProvider Time, int MarginMs)
{
    /// <summary>Runs the aggregate on <paramref name="runner"/> under the deadline.</summary>
    /// <exception cref="AggregateDeadlineException">The deadline passed before the runner answered.</exception>
    public async Task<IReadOnlyList<BsonDocument>> RunAsync(IAggregateRunner runner, EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
    {
        if (MarginMs < 0)
            return await runner.AggregateAsync(entity, stages, options, cancellationToken).ConfigureAwait(false);

        // The runner runs under a token of its own, so giving up on it does not cancel the caller's.
        var abandoned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<BsonDocument>> running;

        try
        {
            running = runner.AggregateAsync(entity, stages, options, abandoned.Token);
        }
        catch
        {
            abandoned.Dispose();
            throw;
        }

        try
        {
            // The caller's cancellation reaches the runner through its token, and what the runner ends in under it is
            // what the caller of this method sees, as without a deadline: only the deadline is waited for here.
            return await running.WaitAsync(TimeSpan.FromMilliseconds((long)options.MaxTimeMs + MarginMs), Time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The runner ended, in a timeout of its own or in the instant the deadline passed: what it ended in stands,
            // its answer as much as its fault or its cancellation.
            if (running.IsCompleted)
                return await running.ConfigureAwait(false);

            await abandoned.CancelAsync().ConfigureAwait(false);

            throw new AggregateDeadlineException(options.MaxTimeMs, MarginMs);
        }
        finally
        {
            // What an aggregate nobody waits for any more ends in is read, and its token is let go when it has ended.
            _ = running.ContinueWith(
                static (ended, source) =>
                {
                    _ = ended.Exception;
                    ((CancellationTokenSource)source!).Dispose();
                },
                abandoned,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
