using System.Diagnostics;
using System.Text.Json.Nodes;
using OxQL.Core.Engine;

namespace OxQL.Mongo.Explain;

/// <summary>
/// The owner calls of the explains that belong together (improvement plan §3.E): one explain a caller
/// sent, or the checks of one internal explain batch, which are explained side by side. They share what
/// one origin's explain may cause (the owner calls left), and what they ask their owners in a round goes
/// out together: one call per owner service, whichever of them asked, the services asked at once. So an
/// owner that is sent several checks asks each of its own owners once per round, as its origin asked it.
/// <para>
/// The explains run one at a time (they hold the turn while they compute); one that asks its owners
/// parks. Once every explain of the pool is parked or done, what the parked ones asked is sent, and
/// they go on, one at a time again. An explain alone is a pool of one: its asks go out as it makes them.
/// </para>
/// </summary>
public sealed class ExplainOwnerPool
{
    private readonly object gate = new();
    private readonly SemaphoreSlim? turn;
    private readonly List<Waiting> waiting = [];
    private int running;
    private int callsLeft;

    /// <summary>A pool of <paramref name="explains"/> explains that may cause <paramref name="calls"/> owner calls together.</summary>
    public ExplainOwnerPool(int explains, int calls)
    {
        running = Math.Max(1, explains);
        callsLeft = Math.Max(0, calls);
        turn = explains > 1 ? new SemaphoreSlim(1, 1) : null;
    }

    /// <summary>The owner calls the pool's explains may still cause.</summary>
    public int CallsLeft
    {
        get
        {
            lock (gate)
                return callsLeft;
        }
    }

    /// <summary>Takes <paramref name="calls"/> an owner said it caused in turn off what is left.</summary>
    public void Spend(int calls)
    {
        lock (gate)
            callsLeft = Math.Max(0, callsLeft - Math.Max(0, calls));
    }

    /// <summary>
    /// Runs one of the pool's explains: it computes while it holds the turn and is counted as done when
    /// it ends, however it ends. Every explain the pool was made for must be run through here, or the
    /// others would wait for it.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> explain)
    {
        ArgumentNullException.ThrowIfNull(explain);

        if (turn is not null)
            await turn.WaitAsync().ConfigureAwait(false);

        try
        {
            return await explain().ConfigureAwait(false);
        }
        finally
        {
            turn?.Release();
            Step(null);
        }
    }

    /// <summary>What one explain asks one owner in a round: the checks, and whether that owner may have to ask owners of its own for them.</summary>
    internal sealed record Group(string Service, IReadOnlyList<ExplainRequest> Checks, bool Nests);

    /// <summary>
    /// What came of one <see cref="Group"/>: the answers in the order of its checks, or why there are none
    /// (<see cref="RemoteExplain.Unsupported"/>, <see cref="RemoteExplain.Unreachable"/>,
    /// <see cref="RemoteExplain.Timeout"/>, or <see cref="NoCalls"/> / <see cref="TooManyChecks"/> when a
    /// limit kept it from being sent), the time the call took, and whether this explain is the one the call is counted for.
    /// </summary>
    internal sealed record Sent(IReadOnlyList<JsonObject?>? Answers, string? Failure, long Ms, bool Paid);

    /// <summary>The failure of a group no call was left for.</summary>
    internal const string NoCalls = "calls";

    /// <summary>The failure of a group that did not fit into the one call its owner is sent.</summary>
    internal const string TooManyChecks = "checks";

    private sealed class Waiting(IRemoteQueryClient client, IReadOnlyList<Group> groups, TimeSpan remaining, int maxChecks, CancellationToken cancellationToken)
    {
        public IRemoteQueryClient Client { get; } = client;

        public IReadOnlyList<Group> Groups { get; } = groups;

        public TimeSpan Remaining { get; } = remaining;

        public int MaxChecks { get; } = maxChecks;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public Sent?[] Results { get; } = new Sent?[groups.Count];

        public TaskCompletionSource<IReadOnlyList<Sent>> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Asks the owners of <paramref name="groups"/>, each within <paramref name="remaining"/>: the explain
    /// parks until every explain of the pool has asked or ended, then the asks go out, one call per owner
    /// service. One result per group, in order. It throws only for the caller's own cancellation.
    /// </summary>
    internal async Task<IReadOnlyList<Sent>> AskAsync(IRemoteQueryClient client, IReadOnlyList<Group> groups, TimeSpan remaining, int maxChecks, CancellationToken cancellationToken)
    {
        var mine = new Waiting(client, groups, remaining, maxChecks, cancellationToken);

        turn?.Release();
        Step(mine);

        try
        {
            return await mine.Done.Task.ConfigureAwait(false);
        }
        finally
        {
            if (turn is not null)
                await turn.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>One explain parks (<paramref name="parked"/>) or ends (null); when none is left computing, what the parked ones asked is sent.</summary>
    private void Step(Waiting? parked)
    {
        List<Waiting>? flush = null;

        lock (gate)
        {
            if (parked is not null)
                waiting.Add(parked);

            running--;

            if (running <= 0 && waiting.Count > 0)
            {
                flush = [.. waiting];
                waiting.Clear();
                // The parked explains go on once they are answered.
                running = flush.Count;
            }
        }

        if (flush is not null)
            _ = FlushAsync(flush);
    }

    /// <summary>One call of a flush: an owner, the groups of the parked explains it carries, in the order they were asked.</summary>
    private sealed class Call(string service)
    {
        public string Service { get; } = service;

        public List<(Waiting Asker, int Index)> Parts { get; } = [];

        public int Share { get; set; }
    }

    private async Task FlushAsync(List<Waiting> flush)
    {
        try
        {
            var calls = new List<Call>();

            foreach (var asker in flush)
                for (var index = 0; index < asker.Groups.Count; index++)
                {
                    var group = asker.Groups[index];
                    var call = calls.FirstOrDefault(each => each.Service == group.Service);

                    if (call is null)
                        calls.Add(call = new Call(group.Service));

                    // What does not fit into the one call is left out: never a second call in a round.
                    if (call.Parts.Sum(part => part.Asker.Groups[part.Index].Checks.Count) + group.Checks.Count > asker.MaxChecks && call.Parts.Count > 0)
                        asker.Results[index] = new Sent(null, TooManyChecks, 0, false);
                    else
                        call.Parts.Add((asker, index));
                }

            calls.RemoveAll(call => call.Parts.Count == 0);

            // A call is taken off what is left when it is sent; what is left then is shared out among the
            // owners that may ask owners of their own, so the owners asked at once cannot together cause
            // more than is left. An owner asked only for paths of its own entities gets none.
            lock (gate)
            {
                foreach (var call in calls.ToList())
                {
                    if (callsLeft > 0)
                    {
                        callsLeft--;
                        continue;
                    }

                    foreach (var (asker, index) in call.Parts)
                        asker.Results[index] = new Sent(null, NoCalls, 0, false);

                    calls.Remove(call);
                }

                var nesting = calls.Where(call => call.Parts.Any(part => part.Asker.Groups[part.Index].Nests)).ToList();

                for (var position = 0; position < nesting.Count; position++)
                    nesting[position].Share = callsLeft / nesting.Count + (position < callsLeft % nesting.Count ? 1 : 0);
            }

            var sent = await Task.WhenAll(calls.Select(SendAsync)).ConfigureAwait(false);

            for (var position = 0; position < calls.Count; position++)
            {
                var (answers, failure, ms) = sent[position];
                var offset = 0;

                for (var part = 0; part < calls[position].Parts.Count; part++)
                {
                    var (asker, index) = calls[position].Parts[part];
                    var count = asker.Groups[index].Checks.Count;

                    // The call is counted once: for the explain that asked first.
                    asker.Results[index] = new Sent(answers?.Skip(offset).Take(count).ToList(), failure, ms, Paid: part == 0);
                    offset += count;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing here may leave an explain parked: what was not answered is not answered.
            foreach (var asker in flush)
                for (var index = 0; index < asker.Results.Length; index++)
                    asker.Results[index] ??= new Sent(null, RemoteExplain.Unreachable, 0, false);
        }
        finally
        {
            foreach (var asker in flush)
            {
                if (asker.CancellationToken.IsCancellationRequested)
                    asker.Done.TrySetCanceled(asker.CancellationToken);
                else
                    asker.Done.TrySetResult(asker.Results.Select(result => result ?? new Sent(null, RemoteExplain.Unreachable, 0, false)).ToList());
            }
        }
    }

    /// <summary>One call to one owner, within the least time any of its askers has left; it never throws.</summary>
    private static async Task<(IReadOnlyList<JsonObject?>? Answers, string? Failure, long Ms)> SendAsync(Call call)
    {
        var first = call.Parts[0].Asker;
        var remaining = call.Parts.Min(part => part.Asker.Remaining);
        var batch = new ExplainBatchRequest
        {
            Checks = call.Parts.SelectMany(part => part.Asker.Groups[part.Index].Checks).ToList(),
            Budget = new ExplainBudget((int)Math.Min(int.MaxValue, Math.Max(0, remaining.TotalMilliseconds)), call.Share),
        };
        var watch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(first.CancellationToken);
        timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);

        try
        {
            var answers = await first.Client.ExplainBatchAsync(call.Service, batch, remaining, timeout.Token).ConfigureAwait(false);

            return (answers, answers is null ? RemoteExplain.Unsupported : null, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return (null, RemoteExplain.Timeout, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, RemoteExplain.Unreachable, watch.ElapsedMilliseconds);
        }
    }
}
