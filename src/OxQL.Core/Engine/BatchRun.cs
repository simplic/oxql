using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using OxQL.Model.Addon;

namespace OxQL.Core.Engine;

/// <summary>
/// Runs the queries of one batch side by side, at most <c>degree</c> at once, and answers in the
/// order they were asked. What a batch waits for at its database is then about
/// <c>ceil(queries / degree)</c> round trips instead of one per query. A degree of 1 runs them one
/// after another, exactly as a loop would.
/// </summary>
public static class BatchRun
{
    /// <summary>
    /// The result of <paramref name="run"/> for every item, in the items' order. A run that throws
    /// stops the ones not yet started and its exception travels once those in flight have ended; the
    /// caller's cancellation travels as itself. <paramref name="run"/> is handed the token to honour.
    /// </summary>
    public static async Task<IReadOnlyList<TResult>> RunAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int degree,
        Func<TItem, CancellationToken, Task<TResult>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(run);

        var results = new TResult[items.Count];

        if (degree <= 1 || items.Count <= 1)
        {
            for (var index = 0; index < items.Count; index++)
                results[index] = await run(items[index], cancellationToken).ConfigureAwait(false);

            return results;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var gate = new SemaphoreSlim(degree, degree);

        async Task OneAsync(int index)
        {
            await gate.WaitAsync(stop.Token).ConfigureAwait(false);

            try
            {
                // A place that came free in the moment the batch was stopped is not a start.
                stop.Token.ThrowIfCancellationRequested();

                results[index] = await run(items[index], stop.Token).ConfigureAwait(false);
            }
            catch
            {
                // Nothing further starts once one has failed.
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }

        var tasks = Enumerable.Range(0, items.Count).Select(OneAsync).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The failure itself, not the cancellations it caused beside it.
            if (tasks.FirstOrDefault(task => task.IsFaulted)?.Exception?.InnerException is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();

            throw;
        }

        return results;
    }
}

/// <summary>
/// What the queries of one batch share while they run side by side: the owner queries they have sent
/// on. Run one after another, a query found in the owner cache what the one before it had fetched;
/// side by side both would ask. So a query that is about to send an owner the very query another one
/// of its batch already sent waits for that answer instead of asking again. The entries are the
/// engine's own; a host never reads them.
/// </summary>
public sealed class BatchFlights
{
    private readonly ConcurrentDictionary<string, object> flights = new(StringComparer.Ordinal);

    /// <summary>The entry under <paramref name="key"/>: <paramref name="mine"/> when there was none (the caller sends), else the one already there (the caller waits for it).</summary>
    public T Join<T>(string key, T mine)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(mine);

        return (T)flights.GetOrAdd(key, mine);
    }

    /// <summary>Takes <paramref name="mine"/> out again: it was not answered, so the next one asks for itself.</summary>
    public void Leave(string key, object mine) => flights.TryRemove(new KeyValuePair<string, object>(key, mine));
}

/// <summary>
/// A host's addon definitions read one at a time. The source is the host's and lives per request
/// (a repository, a unit of work), so the queries of a batch that bind side by side never call it
/// concurrently: they take turns, as they did when the batch ran one query after another.
/// </summary>
public sealed class SerialAddonSource : IAddonDefinitionSource
{
    private readonly IAddonDefinitionSource source;
    private readonly SemaphoreSlim turn = new(1, 1);

    private SerialAddonSource(IAddonDefinitionSource source) => this.source = source;

    /// <summary><paramref name="source"/> read one at a time; a source that already is, or that reads nothing, is returned as it is.</summary>
    public static IAddonDefinitionSource Of(IAddonDefinitionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source is SerialAddonSource or EmptyAddonDefinitionSource ? source : new SerialAddonSource(source);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await source.ForEntityAsync(entity, organisation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            turn.Release();
        }
    }
}
