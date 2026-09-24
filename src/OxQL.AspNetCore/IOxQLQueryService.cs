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

    /// <summary>Binds and compiles one request without executing it.</summary>
    Task<ExplainOutcome> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default);
}
