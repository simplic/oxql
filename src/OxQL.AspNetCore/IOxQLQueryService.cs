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
    /// no header carries it.
    /// </summary>
    Task<BatchOutcome> BatchAsync(BatchRequest batch, bool internalCall, CancellationToken cancellationToken = default);

    /// <summary>Binds and compiles one request without executing it.</summary>
    Task<ExplainOutcome> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default);
}
