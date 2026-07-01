using System.Text.Json.Serialization;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Models;

/// <summary>
/// Structured error response returned by the OxQL endpoint.
/// </summary>
public sealed record OxQLErrorResponse
{
    /// <summary>
    /// A short machine-readable error type.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// A human-readable summary of the problem.
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// The HTTP status code.
    /// </summary>
    [JsonPropertyName("status")]
    public required int Status { get; init; }

    /// <summary>
    /// Detailed validation errors, if any.
    /// </summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<OxQLFieldError>? Errors { get; init; }
}

/// <summary>
/// A single field-level error within a validation error response.
/// </summary>
public sealed record OxQLFieldError
{
    /// <summary>
    /// The error code.
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// The field path the error relates to, if any.
    /// </summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    internal static OxQLFieldError FromValidationError(QueryValidationError error) => new()
    {
        Code = error.Code,
        Message = error.Message,
        Path = error.Path
    };
}
