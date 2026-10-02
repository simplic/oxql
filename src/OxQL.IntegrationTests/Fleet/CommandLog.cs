using System.Collections.Concurrent;
using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver.Core.Events;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// One command the run's client sent: where it went, what it was, the <c>comment</c> it carried, and
/// when it started and ended (<see cref="Stopwatch.GetTimestamp"/>; the end is 0 while it runs).
/// </summary>
public sealed record CommandSeen(string Database, string Name, string? Collection, string? Comment, long Started)
{
    /// <summary>When the answer or the failure arrived; 0 while the command runs.</summary>
    public long Ended { get; set; }

    /// <summary>Whether the engine sent it: an aggregate, a getMore or a listIndexes, and not the test addon source's read.</summary>
    public bool IsEngine => Name is "aggregate" or "getMore" or "listIndexes" && Collection != TestAddonSource.Collection;

    public override string ToString() => $"{Database}: {Name} {Collection}";
}

/// <summary>
/// Every command of the run's one client (<see cref="MongoFixture"/>), offered to whoever watches: a
/// <see cref="CommandCapture"/> keeps the commands sent to its own databases, so a test over a private
/// fleet counts what its requests cost whatever the tests beside it send. Nobody watching costs a
/// dictionary lookup per command.
/// </summary>
public sealed class CommandLog
{
    private readonly ConcurrentDictionary<CommandCapture, byte> captures = new();

    /// <summary>Keeps the commands sent to <paramref name="databases"/> from now until the capture is disposed.</summary>
    public CommandCapture Watch(IEnumerable<string> databases)
    {
        var capture = new CommandCapture(this, databases);

        captures.TryAdd(capture, 0);

        return capture;
    }

    /// <summary>Keeps the commands sent to the databases of every service of <paramref name="fleet"/>.</summary>
    public async Task<CommandCapture> WatchAsync(LabFleet fleet)
    {
        var names = new List<string>();

        foreach (var service in LabService.All)
            names.Add((await fleet.DatabaseAsync(service)).DatabaseNamespace.DatabaseName);

        return Watch(names);
    }

    internal void Forget(CommandCapture capture) => captures.TryRemove(capture, out _);

    internal void Started(CommandStartedEvent started)
    {
        if (captures.IsEmpty || started.DatabaseNamespace?.DatabaseName is not { } database)
            return;

        foreach (var capture in captures.Keys)
            capture.Started(database, started);
    }

    internal void Ended(int requestId)
    {
        if (captures.IsEmpty)
            return;

        foreach (var capture in captures.Keys)
            capture.Ended(requestId);
    }
}

/// <summary>The commands sent to a set of databases while it is watched; see <see cref="CommandLog.Watch"/>.</summary>
public sealed class CommandCapture : IDisposable
{
    private readonly CommandLog log;
    private readonly HashSet<string> databases;
    private readonly ConcurrentDictionary<int, CommandSeen> running = new();
    private readonly ConcurrentQueue<CommandSeen> seen = new();

    internal CommandCapture(CommandLog log, IEnumerable<string> databases)
    {
        this.log = log;
        this.databases = new HashSet<string>(databases, StringComparer.Ordinal);
    }

    /// <summary>What was sent since the last call, in the order it was sent; the capture starts over.</summary>
    public CommandBatch Drain()
    {
        var drained = new List<CommandSeen>();

        while (seen.TryDequeue(out var command))
            drained.Add(command);

        return new CommandBatch(drained);
    }

    internal void Started(string database, CommandStartedEvent started)
    {
        if (!databases.Contains(database))
            return;

        var command = started.Command;
        var collection = started.CommandName == "getMore"
            ? command.GetValue("collection", BsonNull.Value)
            : command.GetValue(started.CommandName, BsonNull.Value);
        var entry = new CommandSeen(
            database,
            started.CommandName,
            collection.IsString ? collection.AsString : null,
            command.TryGetValue("comment", out var comment) && comment.IsString ? comment.AsString : null,
            Stopwatch.GetTimestamp());

        running[started.RequestId] = entry;
        seen.Enqueue(entry);
    }

    internal void Ended(int requestId)
    {
        if (running.TryRemove(requestId, out var entry))
            entry.Ended = Stopwatch.GetTimestamp();
    }

    public void Dispose() => log.Forget(this);
}

/// <summary>The commands of one request, as a <see cref="CommandCapture"/> saw them.</summary>
public sealed record CommandBatch(IReadOnlyList<CommandSeen> All)
{
    /// <summary>What the engine sent: aggregates, getMores and listIndexes, without the test addon source's reads.</summary>
    public IReadOnlyList<CommandSeen> Engine => All.Where(command => command.IsEngine).ToList();

    /// <summary>How many of the engine's commands were <paramref name="name"/>.</summary>
    public int Count(string name) => Engine.Count(command => command.Name == name);

    /// <summary>
    /// The longest run of engine commands one database answered one after another: each started after
    /// the one before it had ended. Commands in flight together count once. What a request waits for
    /// at one owner is this many round trips, however many commands it sent.
    /// </summary>
    public int Depth => Engine.GroupBy(command => command.Database).Select(DepthOf).DefaultIfEmpty(0).Max();

    /// <summary>The <see cref="Depth"/> per database, by the database's name.</summary>
    public IReadOnlyDictionary<string, int> DepthByDatabase => Engine.GroupBy(command => command.Database).ToDictionary(group => group.Key, DepthOf);

    private static int DepthOf(IEnumerable<CommandSeen> commands) =>
        DepthOf(commands.Where(command => command.Ended != 0).Select(command => (command.Started, command.Ended)));

    /// <summary>The most of <paramref name="spans"/> that ran one after another: each started after the one before it had ended.</summary>
    public static int DepthOf(IEnumerable<(long Started, long Ended)> spans)
    {
        var depth = 0;
        var free = long.MinValue;

        // The most spans that do not overlap pairwise: take them by their end.
        foreach (var (started, ended) in spans.OrderBy(span => span.Ended))
        {
            if (started < free)
                continue;

            depth++;
            free = ended;
        }

        return depth;
    }

    public override string ToString() =>
        $"{Engine.Count} engine commands ({string.Join(", ", Engine.GroupBy(command => command.Name).Select(group => $"{group.Count()} {group.Key}"))}), depth {Depth}: {string.Join("; ", Engine)}";
}
