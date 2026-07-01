namespace OxQL.Core.Models;

/// <summary>
/// Represents a single validation error.
/// </summary>
public sealed record QueryValidationError
{
    /// <summary>
    /// Error code for programmatic handling.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// The path or location where the error occurred.
    /// </summary>
    public string? Path { get; init; }
}

/// <summary>
/// Represents the result of query validation.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>
    /// Whether the validation passed.
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// The list of validation errors.
    /// </summary>
    public required IReadOnlyList<QueryValidationError> Errors { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static ValidationResult Success() => new() { Errors = [] };

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static ValidationResult Failure(params QueryValidationError[] errors) =>
        new() { Errors = errors };

    /// <summary>
    /// Creates a failed validation result from a list of errors.
    /// </summary>
    public static ValidationResult Failure(IReadOnlyList<QueryValidationError> errors) =>
        new() { Errors = errors };
}

/// <summary>
/// Exception thrown for unexpected internal query processing errors.
/// </summary>
public sealed class QueryValidationException : Exception
{
    public IReadOnlyList<QueryValidationError> Errors { get; }

    public QueryValidationException(IReadOnlyList<QueryValidationError> errors)
        : base($"Query validation failed with {errors.Count} error(s): {errors[0].Message}")
    {
        Errors = errors;
    }

    public QueryValidationException(string message) : base(message)
    {
        Errors = [new QueryValidationError { Code = "INTERNAL_ERROR", Message = message }];
    }
}
