using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Converts a validated QueryRequest into a provider-neutral QueryPlan.
/// </summary>
public interface IQueryPlanner
{
    /// <summary>
    /// Creates a query plan from a validated and normalized request.
    /// </summary>
    /// <param name="request">The normalized query request.</param>
    /// <returns>A compiled query plan ready for adapter execution.</returns>
    QueryPlan CreatePlan(QueryRequest request);
}
