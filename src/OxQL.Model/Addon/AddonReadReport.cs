using System.Diagnostics;

namespace OxQL.Model.Addon;

/// <summary>
/// An addon source that says what a call cost the request: the engine cannot see inside the host's
/// source, so a source that waits for a database says so, and the request's timing lists the wait
/// as database time (a command of kind <c>addon</c>) instead of counting it as this host's own.
/// A source that answers from memory reports nothing. A source that does not implement this
/// interface is not asked: whatever its call takes stays in the request's own time.
/// </summary>
public interface IReportingAddonDefinitionSource : IAddonDefinitionSource
{
    /// <summary>
    /// The definitions for one entity and one organisation, retired ones included, as
    /// <see cref="IAddonDefinitionSource.ForEntityAsync"/> answers them; every database command the
    /// call waited for is said to <paramref name="report"/> before it returns.
    /// </summary>
    ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, AddonReadReport report, CancellationToken cancellationToken);
}

/// <summary>What one call of an addon source waited for at a database. One per call; not shared between calls.</summary>
public sealed class AddonReadReport
{
    private List<AddonDatabaseRead>? reads;

    /// <summary>The database commands the call waited for, in the order they were reported; empty when it answered from memory.</summary>
    public IReadOnlyList<AddonDatabaseRead> Reads => reads ?? (IReadOnlyList<AddonDatabaseRead>)[];

    /// <summary>
    /// The call waited for a database command from <paramref name="started"/> (a
    /// <see cref="Stopwatch.GetTimestamp"/> the source took before it sent or joined the command)
    /// until now.
    /// </summary>
    /// <param name="collection">The collection read, as the database names it.</param>
    /// <param name="started">When the wait began.</param>
    /// <param name="docs">The documents the command returned.</param>
    /// <param name="roundTrips">
    /// The round trips this call caused: 1 for a command it sent, more when it read further
    /// batches, 0 when it waited for a command another request had already sent.
    /// </param>
    public void Database(string collection, long started, int docs, int roundTrips = 1)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var ended = Stopwatch.GetTimestamp();

        (reads ??= []).Add(new AddonDatabaseRead(collection, Math.Min(started, ended), ended, Math.Max(0, docs), Math.Max(0, roundTrips)));
    }
}

/// <summary>One database command an addon source's call waited for.</summary>
/// <param name="Collection">The collection read.</param>
/// <param name="Started">When the wait began (<see cref="Stopwatch.GetTimestamp"/>).</param>
/// <param name="Ended">When it ended.</param>
/// <param name="Docs">The documents the command returned.</param>
/// <param name="RoundTrips">The round trips the call caused; 0 when it waited for a command another request had sent.</param>
public readonly record struct AddonDatabaseRead(string Collection, long Started, long Ended, int Docs, int RoundTrips);
