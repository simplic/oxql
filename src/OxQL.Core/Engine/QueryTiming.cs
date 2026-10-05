using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OxQL.Core.Engine;

/// <summary>
/// Where the time of one request went: the time it waited for the database (every command, with
/// what it was and what it returned), the time it waited for other services' owners, and the rest,
/// which is this host's own (binding, compiling, converting rows, the driver's work).
/// <para>
/// It is always recorded: a timestamp pair per phase and per command. Every answer says the sum in
/// its <c>Server-Timing</c> header (<see cref="ServerTiming"/>); a request that asks
/// (<c>"timing": true</c>, or the header <see cref="Header"/>) gets the breakdown as the
/// <c>timing</c> member beside <c>pageInfo</c> (<see cref="ToJson"/>).
/// </para>
/// <para>
/// <c>db.ms</c> is the time the request spent with at least one command in flight: commands sent
/// together count once (the union of their spans), so <c>totalMs − db.ms = oursMs</c> always holds.
/// A command's time is what the driver saw: the server's execution, the network and the reply's
/// read; the server alone is found in its profiler by the command's <c>comment</c>, the request's
/// correlation id.
/// </para>
/// </summary>
public sealed class QueryTiming
{
    /// <summary>The request header that asks for the <c>timing</c> member, as <c>"timing": true</c> in the body does: any value but <c>0</c> and <c>false</c>.</summary>
    public const string Header = "X-OxQL-Timing";

    /// <summary>The response header every query answer carries.</summary>
    public const string ServerTimingHeader = "Server-Timing";

    /// <summary>The <c>HttpContext.Items</c> key a host stores the request's start under (<see cref="Stopwatch.GetTimestamp"/>), before the body is read.</summary>
    public const string StartItem = "OxQL.Timing.Start";

    /// <summary>The page's rows.</summary>
    public const string Page = "page";

    /// <summary>The count beside a page, or the count of <c>includeTotalCount</c>.</summary>
    public const string Count = "count";

    /// <summary>One step of the count alone (<c>limit: 0</c>).</summary>
    public const string CountRung = "countRung";

    /// <summary>A strict request's truncation probe.</summary>
    public const string Probe = "probe";

    /// <summary>A command this host ran as the owner of a keyed join's target.</summary>
    public const string Resolve = "resolve";

    private readonly object gate = new();
    private readonly List<Command> commands = [];
    private readonly List<Owner> owners = [];
    private readonly List<(long Started, long Ended)> remote = [];
    private readonly QueryTiming? parent;
    private readonly long started;
    private long parse;
    private long bind;
    private long addon;
    private long compile;
    private long convert;
    private long countWait;
    private long resolve;
    private long excluded;
    private long ended;
    private double rtt = -1;

    /// <summary>A request that started now, or at <paramref name="startedAt"/> (a <see cref="Stopwatch"/> timestamp the host took before it read the body).</summary>
    public QueryTiming(long? startedAt = null)
    {
        var now = Stopwatch.GetTimestamp();

        started = startedAt is { } at && at <= now ? at : now;
        parse = now - started;
    }

    private QueryTiming(QueryTiming parent)
    {
        this.parent = parent;
        started = parent.started;
    }

    /// <summary>Whether the answer carries the breakdown (<c>timing</c>): the request or its header asked.</summary>
    public bool Detail { get; set; }

    /// <summary>Whether the request this collector belongs to asked for the breakdown, also when this is the collector of a request nested in it.</summary>
    public bool Detailed => Root.Detail;

    /// <summary>The rows the answer carries.</summary>
    public int Rows { get; set; }

    /// <summary>The keyed joins' owner calls and cache hits, as the keyed fetch counted them.</summary>
    public int ResolveCalls { get; set; }

    /// <inheritdoc cref="ResolveCalls"/>
    public int ResolveCacheHits { get; set; }

    /// <summary>Now, as a phase's start.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>
    /// The collector of a request this host runs for itself inside this one (the owner query of a
    /// local join target): its commands are this request's, of kind <see cref="Resolve"/>; its
    /// phases are part of this request's resolve time and are not counted again.
    /// </summary>
    public QueryTiming Nested() => new(parent ?? this) { Detail = false };

    private QueryTiming Root => parent ?? this;

    /// <summary>The time since <paramref name="since"/> was spent binding (the addon definitions' read is taken out by <see cref="Addon"/>).</summary>
    public void Bind(long since) => Add(ref bind, since);

    /// <summary>The time since <paramref name="since"/> was spent reading addon definitions.</summary>
    public void Addon(long since) => Add(ref addon, since);

    /// <summary>The time since <paramref name="since"/> was spent compiling.</summary>
    public void Compile(long since) => Add(ref compile, since);

    /// <summary>The time since <paramref name="since"/> was spent converting rows to their wire form.</summary>
    public void Convert(long since) => Add(ref convert, since);

    /// <summary>The time since <paramref name="since"/> the finished rows waited for the count.</summary>
    public void CountWait(long since) => Add(ref countWait, since);

    /// <summary>The time since <paramref name="since"/> was spent in the keyed joins: owner calls, their cache, assigning the rows.</summary>
    public void Resolved(long since) => Add(ref resolve, since);

    /// <summary>Time the measuring itself took (sizing the rows a command returned): taken out of the total.</summary>
    public void Exclude(long since) => Interlocked.Add(ref Root.excluded, Stopwatch.GetTimestamp() - since);

    private void Add(ref long phase, long since)
    {
        // The phases of a nested request are inside the resolve phase of the request it runs for.
        if (parent is null)
            Interlocked.Add(ref phase, Stopwatch.GetTimestamp() - since);
    }

    /// <summary>A command leaves for the database: <paramref name="kind"/> is one of the kinds above, <paramref name="collection"/> the entity it reads.</summary>
    public Command Begin(string kind, string collection)
    {
        var root = Root;
        var command = new Command(parent is null ? kind : Resolve, collection, Stopwatch.GetTimestamp());

        lock (root.gate)
            root.commands.Add(command);

        return command;
    }

    /// <summary>Marks the commands of <paramref name="kind"/> whose answer the request did not use (a count the page made needless).</summary>
    public void Discard(string kind)
    {
        var root = Root;

        lock (root.gate)
            foreach (var command in root.commands)
                if (command.Kind == kind)
                    command.Discarded = true;
    }

    /// <summary>The round-trip time the driver last measured to the server the commands went to, in milliseconds.</summary>
    public void RoundTrip(double milliseconds)
    {
        if (milliseconds >= 0)
            Root.rtt = milliseconds;
    }

    /// <summary>
    /// One owner's part of a keyed join: the target it answered for, the keys it was asked, the keys
    /// the cache answered instead, the time the calls that carried them took, and, from another
    /// service that was asked for it, that owner's own timing of each query.
    /// </summary>
    public void Called(string service, string entity, bool isRemote, int keys, int cacheHits, double milliseconds, IReadOnlyList<JsonObject> timings)
    {
        var root = Root;

        lock (root.gate)
            root.owners.Add(new Owner(service, entity, isRemote, keys, cacheHits, milliseconds, timings));
    }

    /// <summary>The span of a call to another service's owner: time this request waited for it.</summary>
    public void Remote(long since)
    {
        var root = Root;
        var now = Stopwatch.GetTimestamp();

        lock (root.gate)
            root.remote.Add((since, now));
    }

    /// <summary>The request is answered: the total stops here. Later calls change nothing.</summary>
    public void Stop() => Interlocked.CompareExchange(ref ended, Stopwatch.GetTimestamp(), 0);

    private long End => Interlocked.Read(ref ended) is var stopped and not 0 ? stopped : Stopwatch.GetTimestamp();

    private static double Ms(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 2);

    /// <summary>The time covered by <paramref name="spans"/>, each cut to the request's own span: overlapping spans count once.</summary>
    private static long Union(IEnumerable<(long Started, long Ended)> spans, long from, long to)
    {
        long covered = 0;
        var until = from;

        foreach (var (begin, end) in spans.OrderBy(span => span.Started))
        {
            var cut = Math.Min(end, to);
            var at = Math.Max(begin, until);

            if (cut > at)
                covered += cut - at;

            until = Math.Max(until, cut);
        }

        return covered;
    }

    /// <summary>What the numbers are at this moment.</summary>
    private Snapshot Take()
    {
        var to = End;

        lock (gate)
        {
            var list = commands.Select(command => (Command: command, Ended: command.Ended is var done and not 0 ? done : to)).ToList();
            var database = Union(list.Select(entry => (entry.Command.Started, entry.Ended)), started, to);
            var waited = Union(remote, started, to);
            var total = Math.Max(0, to - started - Interlocked.Read(ref excluded));

            return new Snapshot(total, Math.Min(database, total), waited, list, [.. owners]);
        }
    }

    private sealed record Snapshot(long Total, long Database, long Remote, List<(Command Command, long Ended)> Commands, List<Owner> Owners)
    {
        public int RoundTrips => Commands.Sum(entry => 1 + Math.Max(0, entry.Command.GetMores ?? 0));
    }

    /// <summary>Whether a header or a body member asks for the breakdown: present and neither <c>0</c> nor <c>false</c>.</summary>
    public static bool Asked(string? header) =>
        header is { Length: > 0 } && header != "0" && !string.Equals(header, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>Server-Timing</c> value: <c>total</c>, <c>db</c> (with the commands and round trips),
    /// <c>ours</c> (= total − db), <c>bind</c>, <c>compile</c>, <c>convert</c>, and where they
    /// occurred <c>resolve</c>, <c>remote</c>, <c>count</c> and the estimated network part of
    /// <c>db</c> (<c>net</c>). Durations in milliseconds.
    /// </summary>
    public string ServerTiming() => ServerTiming([this]);

    /// <summary>The <c>Server-Timing</c> value of several requests answered together (a batch): the sums, the database time as the union over all of them.</summary>
    public static string ServerTiming(IReadOnlyList<QueryTiming> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
            return "total;dur=0";

        var roots = requests.Select(request => request.Root).Distinct().ToList();
        var snapshots = roots.Select(root => root.Take()).ToList();
        var from = roots.Min(root => root.started);
        var to = roots.Max(root => root.End);
        var total = roots.Count == 1 ? snapshots[0].Total : to - from;
        var database = roots.Count == 1
            ? snapshots[0].Database
            : Math.Min(total, Union(snapshots.SelectMany(snapshot => snapshot.Commands.Select(entry => (entry.Command.Started, entry.Ended))), from, to));
        var waited = snapshots.Sum(snapshot => snapshot.Remote);
        var commands = snapshots.Sum(snapshot => snapshot.Commands.Count);
        var trips = snapshots.Sum(snapshot => snapshot.RoundTrips);
        var text = new StringBuilder();

        void Metric(string name, long ticks, string? description = null)
        {
            if (text.Length > 0)
                text.Append(", ");

            text.Append(name).Append(";dur=").Append(Ms(ticks).ToString("0.##", CultureInfo.InvariantCulture));

            if (description is not null)
                text.Append(";desc=\"").Append(description).Append('"');
        }

        Metric("total", total);
        Metric("db", database, string.Create(CultureInfo.InvariantCulture, $"{commands} cmd / {trips} rt"));
        Metric("ours", total - database);
        Metric("bind", roots.Sum(root => Math.Max(0, Interlocked.Read(ref root.bind) - Interlocked.Read(ref root.addon))));
        Metric("compile", roots.Sum(root => Interlocked.Read(ref root.compile)));
        Metric("convert", roots.Sum(root => Interlocked.Read(ref root.convert)));

        if (roots.Sum(root => Interlocked.Read(ref root.resolve)) is var resolved and > 0)
            Metric("resolve", resolved, string.Create(CultureInfo.InvariantCulture, $"{roots.Sum(root => root.ResolveCalls)} calls / {roots.Sum(root => root.ResolveCacheHits)} cached"));

        if (waited > 0)
            Metric("remote", waited);

        if (roots.Sum(root => Interlocked.Read(ref root.countWait)) is var counted and > 0)
            Metric("count", counted);

        if (roots.Select(root => root.rtt).Where(measured => measured >= 0).DefaultIfEmpty(-1).Max() is var rtt and >= 0)
            Metric("net", (long)(rtt * trips * Stopwatch.Frequency / 1000.0), "est");

        return text.ToString();
    }

    /// <summary>
    /// The <c>timing</c> member:
    /// <c>{ totalMs, oursMs, parseMs, bindMs, addonMs, compileMs, db { ms, commands, roundTrips, rttMs?, networkEstimateMs?, list[] },
    /// resolve { ms, calls, cacheHits, remoteMs, owners[] }, countWaitMs, convertMs, rows, build }</c>.
    /// </summary>
    public JsonObject ToJson()
    {
        var root = Root;
        var snapshot = root.Take();
        var list = new JsonArray();

        for (var index = 0; index < snapshot.Commands.Count; index++)
        {
            var (command, end) = snapshot.Commands[index];
            var entry = new JsonObject
            {
                ["kind"] = command.Kind,
                ["collection"] = command.Collection,
                ["ms"] = Ms(end - command.Started),
            };

            if (command.FirstBatch is { } first)
            {
                entry["firstBatchMs"] = Ms(first - command.Started);
                entry["drainMs"] = Ms(Math.Max(0, end - first));
            }

            entry["docs"] = command.Docs;

            if (command.Bytes is { } bytes)
                entry["bytes"] = bytes;

            if (command.GetMores is { } more)
                entry["getMores"] = more;

            // The first earlier command that was still in flight when this one left.
            var beside = snapshot.Commands.Take(index).ToList().FindIndex(other => other.Ended > command.Started);

            entry["parallelWith"] = beside < 0 ? null : beside;

            if (command.Discarded)
                entry["discarded"] = true;

            if (command.Ended == 0)
                entry["running"] = true;
            else if (command.Failed)
                entry["failed"] = true;

            list.Add(entry);
        }

        var database = new JsonObject
        {
            ["ms"] = Ms(snapshot.Database),
            ["commands"] = snapshot.Commands.Count,
            ["roundTrips"] = snapshot.RoundTrips,
        };

        if (root.rtt >= 0)
        {
            database["rttMs"] = Math.Round(root.rtt, 2);
            database["networkEstimateMs"] = Math.Round(root.rtt * snapshot.RoundTrips, 2);
        }

        database["list"] = list;

        var calls = new JsonArray();

        foreach (var owner in snapshot.Owners)
        {
            var entry = new JsonObject
            {
                ["service"] = owner.Service,
                ["entity"] = owner.Entity,
                ["remote"] = owner.IsRemote,
                ["ms"] = Math.Round(owner.Milliseconds, 2),
                ["keys"] = owner.Keys,
                ["cacheHits"] = owner.CacheHits,
            };

            if (owner.Timings.Count > 0)
                entry["timing"] = new JsonArray(owner.Timings.Select(timing => (JsonNode?)timing.DeepClone()).ToArray());

            calls.Add(entry);
        }

        return new JsonObject
        {
            ["totalMs"] = Ms(snapshot.Total),
            ["oursMs"] = Ms(snapshot.Total - snapshot.Database),
            ["parseMs"] = Ms(Interlocked.Read(ref root.parse)),
            ["bindMs"] = Ms(Math.Max(0, Interlocked.Read(ref root.bind) - Interlocked.Read(ref root.addon))),
            ["addonMs"] = Ms(Interlocked.Read(ref root.addon)),
            ["compileMs"] = Ms(Interlocked.Read(ref root.compile)),
            ["db"] = database,
            ["resolve"] = new JsonObject
            {
                ["ms"] = Ms(Interlocked.Read(ref root.resolve)),
                ["calls"] = root.ResolveCalls,
                ["cacheHits"] = root.ResolveCacheHits,
                ["remoteMs"] = Ms(snapshot.Remote),
                ["owners"] = calls,
            },
            ["countWaitMs"] = Ms(Interlocked.Read(ref root.countWait)),
            ["convertMs"] = Ms(Interlocked.Read(ref root.convert)),
            ["rows"] = root.Rows,
            ["build"] = Build,
        };
    }

    /// <summary>The numbers a log line carries: total, database, ours, convert in milliseconds, and the commands sent.</summary>
    public (double TotalMs, double DbMs, double OursMs, double ConvertMs, int Commands) Summary()
    {
        var snapshot = Root.Take();

        return (Ms(snapshot.Total), Ms(snapshot.Database), Ms(snapshot.Total - snapshot.Database), Ms(Interlocked.Read(ref Root.convert)), snapshot.Commands.Count);
    }

    /// <summary>How this engine was built: its own share is larger in a Debug build.</summary>
    public static string Build =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private sealed record Owner(string Service, string Entity, bool IsRemote, int Keys, int CacheHits, double Milliseconds, IReadOnlyList<JsonObject> Timings);

    /// <summary>One command sent to the database, from when it left to when its last row was read.</summary>
    public sealed class Command
    {
        internal Command(string kind, string collection, long started)
        {
            Kind = kind;
            Collection = collection;
            Started = started;
        }

        /// <summary>What the command was for: <c>page</c>, <c>count</c>, <c>countRung</c>, <c>probe</c>, <c>resolve</c>.</summary>
        public string Kind { get; }

        /// <summary>The entity whose collection it read.</summary>
        public string Collection { get; }

        internal long Started { get; }

        internal long Ended { get; private set; }

        internal long? FirstBatch { get; private set; }

        internal int Docs { get; private set; }

        internal long? Bytes { get; private set; }

        internal int? GetMores { get; private set; }

        internal bool Failed { get; private set; }

        internal bool Discarded { get; set; }

        /// <summary>The first reply arrived (the command's own; what follows is reading the rest).</summary>
        public void FirstBatchAt(long timestamp) => FirstBatch = timestamp;

        /// <summary>The command returned <paramref name="docs"/> documents, after <paramref name="getMores"/> further round trips where the runner counts them.</summary>
        public void End(int docs, int? getMores = null)
        {
            Docs = docs;
            GetMores = getMores;
            Ended = Stopwatch.GetTimestamp();
        }

        /// <summary>The command ended without an answer: it failed, timed out or was cancelled.</summary>
        public void Fail()
        {
            Failed = true;
            Ended = Stopwatch.GetTimestamp();
        }

        /// <summary>The size of what the command returned, as BSON.</summary>
        public void Sized(long bytes) => Bytes = bytes;
    }
}
