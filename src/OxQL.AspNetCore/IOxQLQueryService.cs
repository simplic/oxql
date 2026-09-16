using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore;

/// <summary>The query service the controller and the minimal-API mapping call.</summary>
public interface IOxQLQueryService
{
    /// <summary>Executes one request under the current request's scope.</summary>
    Task<QueryOutcome> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Executes one request under a per-request time ceiling, as a batch does.</summary>
    Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken = default);

    /// <summary>Binds and compiles one request without executing it.</summary>
    Task<ExplainOutcome> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default);
}
