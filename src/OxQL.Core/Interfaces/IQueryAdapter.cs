using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Executes a QueryPlan against a specific backend store.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public interface IQueryAdapter<T>
{
    /// <summary>
    /// Executes the query plan and returns results.
    /// </summary>
    /// <param name="plan">The compiled query plan.</param>
    /// <param name="variables">Runtime variables for the query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query response.</returns>
    Task<QueryResponse<T>> ExecuteAsync(QueryPlan plan, QueryVariables? variables, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the generated pipeline stages for the plan without executing the query.
    /// Each element is the backend-specific representation of a single pipeline stage
    /// (e.g. a <c>BsonDocument</c> for MongoDB).
    /// </summary>
    /// <param name="plan">The compiled query plan.</param>
    /// <param name="variables">Runtime variables for the query.</param>
    /// <returns>The ordered list of pipeline stage objects.</returns>
    IReadOnlyList<object> Explain(QueryPlan plan, QueryVariables? variables);
}
