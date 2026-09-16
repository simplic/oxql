using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>One reason a request was refused: a code from the closed list, a message, and where it happened.</summary>
public sealed record QueryValidationError
{
    /// <summary>The code, from <see cref="Binding.Codes"/>; the contract.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>A human-readable message; may change between versions.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>The zero-based index of the caller's stage the error belongs to, or null when it is not about one stage.</summary>
    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Stage { get; init; }

    /// <summary>The wire path the error is about, when any.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}

/// <summary>Thrown by callers that want a refusal as an exception; the engine itself never throws for a caller error.</summary>
public sealed class QueryValidationException : Exception
{
    /// <summary>The errors.</summary>
    public IReadOnlyList<QueryValidationError> Errors { get; }

    /// <summary>Wraps a list of errors.</summary>
    public QueryValidationException(IReadOnlyList<QueryValidationError> errors)
        : base($"Query validation failed with {errors.Count} error(s): {(errors.Count > 0 ? errors[0].Message : "")}")
    {
        Errors = errors;
    }

    /// <summary>Wraps one message as an internal error.</summary>
    public QueryValidationException(string message) : base(message)
    {
        Errors = [new QueryValidationError { Code = Binding.Codes.InternalError, Message = message }];
    }
}
