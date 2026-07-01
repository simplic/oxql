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
}
