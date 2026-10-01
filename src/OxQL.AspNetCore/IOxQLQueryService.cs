using OxQL.AspNetCore.Batch;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore;

/// <summary>The query service the public controller and the base package's internal batch controller call.</summary>
public interface IOxQLQueryService
{
    /// <summary>Executes one request under the current request's scope.</summary>
    Task<QueryOutcome> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Executes one request under a per-request time ceiling, as a batch does.</summary>
    Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes several requests in order under one time ceiling: every entry carries its own
    /// outcome; more than <c>Limits:MaxBatchQueries</c> refuses the batch whole. The public
    /// <c>POST /oxql/batch</c> and the internal batch route both delegate here.
    /// </summary>
    Task<BatchOutcome> BatchAsync(BatchRequest batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a batch as <see cref="BatchAsync(BatchRequest, CancellationToken)"/> does; with
    /// <paramref name="internalCall"/> every request runs as an internal call
    /// (<c>RequestContext.Internal</c>), which alone may carry the keyed fetch's <c>keyedBy</c>.
    /// The base package's internal batch route calls it with <c>true</c>; the public route never
    /// does, so <c>keyedBy</c> stays <c>UNKNOWN_REQUEST_MEMBER</c> there. The route is the signal:
    /// no header carries it. The default serves the public form and refuses an internal call, so an
    /// implementation that knows only the public form keeps compiling; the package's own service implements both.
    /// </summary>
    Task<BatchOutcome> BatchAsync(BatchRequest batch, bool internalCall, CancellationToken cancellationToken = default) =>
        internalCall
            ? throw new NotSupportedException($"{GetType().Name} does not run internal calls; implement BatchAsync(BatchRequest, bool, CancellationToken).")
            : BatchAsync(batch, cancellationToken);

    /// <summary>
    /// Everything about one request without executing it (DESIGN §4): a plain query or the explain
    /// envelope. A request that does not bind is an answer with <c>valid: false</c>.
    /// </summary>
    Task<ExplainOutcome> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explains as <see cref="ExplainAsync(ExplainRequest, CancellationToken)"/> does; with
    /// <paramref name="internalCall"/> the request is explained as an internal call
    /// (<c>RequestContext.Internal</c>), so the owner queries an origin forwards for its remote check,
    /// which carry <c>keyedBy</c>, bind, and the request may carry what is left of its origin's explain (<c>budget</c>). The base package's internal explain route
    /// (<c>POST internal/oxql/explain</c>, DESIGN §4.1) calls it with <c>true</c>; the public route never does.
    /// The default serves the public form and refuses an internal call.
    /// </summary>
    Task<ExplainOutcome> ExplainAsync(ExplainRequest request, bool internalCall, CancellationToken cancellationToken = default) =>
        internalCall
            ? throw new NotSupportedException($"{GetType().Name} does not run internal calls; implement ExplainAsync(ExplainRequest, bool, CancellationToken).")
            : ExplainAsync(request, cancellationToken);

    /// <summary>
    /// Explains the checks of one internal explain (<c>POST internal/oxql/explain</c>, body
    /// <c>{ checks, budget }</c>): what one round of an origin's explain asks this owner, in one call.
    /// Every check is explained as an internal call and answered slim, in order, each within what the
    /// ones before it left of the batch's budget (time, and the owner calls this host may cause for it);
    /// an entry is null where a check was refused before binding or the budget ran out before it. More
    /// checks than <c>Explain:MaxBatchChecks</c> refuse the batch whole. The default explains check by
    /// check through <see cref="ExplainAsync(ExplainRequest, bool, CancellationToken)"/>.
    /// </summary>
    async Task<ExplainBatchOutcome> ExplainBatchAsync(ExplainBatchRequest batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var answers = new List<System.Text.Json.Nodes.JsonNode?>(batch.Checks.Count);

        foreach (var check in batch.Checks)
            answers.Add(await ExplainAsync(check with { Budget = batch.Budget, Slim = true }, internalCall: true, cancellationToken).ConfigureAwait(false) is ExplainOutcome.Success success
                ? System.Text.Json.JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire)
                : null);

        return new ExplainBatchOutcome.Success(new ExplainBatchResponse { Answers = answers });
    }

    /// <summary>
    /// The plain form of <see cref="ExplainAsync(ExplainRequest, CancellationToken)"/>: a query without an
    /// envelope, explained with the envelope's defaults.
    /// </summary>
    Task<ExplainOutcome> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        ExplainAsync((ExplainRequest)request, cancellationToken);
}
