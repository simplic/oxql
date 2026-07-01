using OxQL.Core.Models;

namespace OxQL.Core.Interfaces;

/// <summary>
/// Validates a query request against configured rules and limits.
/// </summary>
public interface IQueryValidator
{
    /// <summary>
    /// Validates the query request and returns validation results.
    /// Does not throw for validation failures; returns structured errors.
    /// </summary>
    /// <param name="request">The query request to validate.</param>
    /// <returns>Validation result containing any errors found.</returns>
    ValidationResult Validate(QueryRequest request);
}
