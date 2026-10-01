using System.Text.Json.Nodes;
using OxQL.Core.Models;

namespace OxQL.Core.Engine;

/// <summary>
/// Sends a batch to the owner of a remote entity: one call per service per page, over the
/// host's internal client with the caller's user, organisation and correlation forwarded and
/// the remaining time budget as the owner's ceiling. The service key is the target entity's
/// namespace (<c>vehicle</c> of <c>vehicle.vehicle</c>), which is the host's
/// <c>InternalHosts</c> key. The base package implements it; the engine's resolver consumes
/// it, the host's startup check and health read it.
/// </summary>
public interface IRemoteQueryClient
{
    /// <summary>Executes a batch on the service behind <paramref name="serviceKey"/> within <paramref name="budget"/>.</summary>
    Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the host knows where <paramref name="serviceKey"/> lives (an <c>InternalHosts</c>
    /// entry). A declared reference into a service without one is a startup finding of the
    /// declaring host, never a silent runtime null.
    /// </summary>
    bool IsConfigured(string serviceKey);

    /// <summary>Whether the configured service answers right now; a health diagnostic, never a refusal.</summary>
    Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken);

    /// <summary>
    /// Explains <paramref name="request"/> at the service behind <paramref name="serviceKey"/>, over
    /// its internal explain route (<c>POST internal/oxql/explain</c>, DESIGN §4.1: internal key,
    /// forwarded identity, the same body as <c>POST /oxql/explain</c>) within <paramref name="budget"/>,
    /// and answers the owner's explain answer as written (<c>{ valid, errors, stages, aliases, types, … }</c>). The
    /// origin forwards the owner queries a run would send (the remote check, which also answers the
    /// types of the owner's targets) and the catalog entries of the owner's entities with it; the
    /// request carries what is left of the origin's explain (<see cref="ExplainRequest.Budget"/>). A
    /// client that cannot explain at owners answers null, the default, and the origin notes the parts
    /// <c>REMOTE_UNCHECKED</c>; a failed call (unreachable, timed out, refused, 429 from the owner's
    /// limiter) throws, and the origin notes them the same way.
    /// </summary>
    Task<JsonObject?> ExplainAsync(string serviceKey, ExplainRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
        Task.FromResult<JsonObject?>(null);

    /// <summary>
    /// Explains every request of <paramref name="batch"/> at the service behind <paramref name="serviceKey"/>
    /// in one call (<c>POST internal/oxql/explain</c>, body <c>{ checks, budget }</c>): what one round of an
    /// origin's explain asks that owner. The answers come back in the order of the checks; an entry is null
    /// where the owner did not answer that check (it refused it, or the batch's budget ran out before it).
    /// Null, for the whole call, is a client that cannot explain at owners; a failed call throws, as
    /// <see cref="ExplainAsync"/> does, and none of its checks is answered.
    /// <para>
    /// The default asks check by check through <see cref="ExplainAsync"/>, each with what the checks before it
    /// left of the batch's budget, so a client written for one check at a time keeps working; a client that
    /// reaches a real owner sends the batch as one request.
    /// </para>
    /// </summary>
    async Task<IReadOnlyList<JsonObject?>?> ExplainBatchAsync(string serviceKey, ExplainBatchRequest batch, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var answers = new List<JsonObject?>(batch.Checks.Count);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var calls = batch.Budget?.Calls ?? int.MaxValue;

        foreach (var check in batch.Checks)
        {
            var left = budget - clock.Elapsed;

            if (left <= TimeSpan.Zero)
                throw new OperationCanceledException($"The budget of the explain batch for '{serviceKey}' ran out.");

            var sent = batch.Budget is null ? check : check with { Budget = new ExplainBudget((int)Math.Min(int.MaxValue, Math.Min(batch.Budget.Ms, left.TotalMilliseconds)), calls) };
            var answer = await ExplainAsync(serviceKey, sent, left, cancellationToken).ConfigureAwait(false);

            if (answer is null)
                return null;

            calls = Math.Max(0, calls - ExplainBatchRequest.CallsOf(answer));
            answers.Add(answer);
        }

        return answers;
    }
}
