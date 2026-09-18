using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OxQL.Core.Models;

namespace OxQL.Core.Engine;

/// <summary>Something that did not change the rows: an explanation the caller may read, never a refusal.</summary>
public sealed record Diagnostic
{
    /// <summary>The code, from <see cref="Binding.Codes"/>.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>A human-readable message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>The caller's stage the diagnostic belongs to, or null.</summary>
    [JsonPropertyName("stage")]
    public int? Stage { get; init; }

    /// <summary>The wire path the diagnostic is about, or null.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Machine-readable details, e.g. <c>currentId</c> on <c>ENTITY_ID_RETIRED</c>.</summary>
    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Params { get; init; }
}

/// <summary>Paging metadata.</summary>
public sealed record PageInfo
{
    /// <summary>Whether a page follows this one.</summary>
    [JsonPropertyName("hasNextPage")]
    public required bool HasNextPage { get; init; }

    /// <summary>The opaque cursor of the next page, or null.</summary>
    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }

    /// <summary>The count, exact under the cap and the cap above it; only when requested.</summary>
    [JsonPropertyName("totalCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalCount { get; init; }

    /// <summary>Whether <see cref="TotalCount"/> is the cap rather than the exact count; only when a count was requested.</summary>
    [JsonPropertyName("totalCountCapped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TotalCountCapped { get; init; }
}

/// <summary>A successful response: rows in the wire encoding, paging, and diagnostics when any.</summary>
public sealed record QueryResult
{
    /// <summary>The rows, wire-encoded at every depth.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<JsonNode?> Items { get; init; }

    /// <summary>The paging metadata.</summary>
    [JsonPropertyName("pageInfo")]
    public required PageInfo PageInfo { get; init; }

    /// <summary>What did not change the rows; absent when empty.</summary>
    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }
}

/// <summary>Why a request was aborted: the reason class, an HTTP status by that class, and the details.</summary>
public sealed record Refusal
{
    /// <summary>The reason class: <c>validation_error</c>, <c>access_denied</c>, <c>not_executable</c>, <c>timeout</c>, <c>internal_error</c>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>A human-readable summary.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>The HTTP status the host answers with; not part of the body.</summary>
    [JsonIgnore]
    public int Status { get; init; }

    /// <summary>The details, when any.</summary>
    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QueryValidationError>? Errors { get; init; }

    /// <summary>A caller error: 400.</summary>
    public static Refusal Validation(IReadOnlyList<QueryValidationError> errors) => new()
    {
        Type = "validation_error",
        Title = "The request could not be bound.",
        Status = 400,
        Errors = errors,
    };

    /// <summary>A request without an organisation, or an entity without the scope member: 403.</summary>
    public static Refusal AccessDenied(string message) => new()
    {
        Type = "access_denied",
        Title = "The request is not scoped to an organisation.",
        Status = 403,
        Errors = [new QueryValidationError { Code = Binding.Codes.AccessDenied, Message = message }],
    };

    /// <summary>A well-formed request this host cannot execute: 422.</summary>
    public static Refusal NotExecutable(string code, string message, int? stage = null, IReadOnlyList<QueryValidationError>? inner = null) => new()
    {
        Type = "not_executable",
        Title = "The request is well-formed but cannot be executed on this host.",
        Status = 422,
        Errors = inner ?? [new QueryValidationError { Code = code, Message = message, Stage = stage }],
    };

    /// <summary>The aggregate exceeded its time budget: 504.</summary>
    public static Refusal Timeout(string message) => new()
    {
        Type = "timeout",
        Title = "The query exceeded its time budget.",
        Status = 504,
        Errors = [new QueryValidationError { Code = Binding.Codes.QueryTimeout, Message = message }],
    };

    /// <summary>
    /// An engine fault: 500 carrying a coded refusal envelope, with the detail only when the
    /// host allows it. The code travels either way: a 500 with no envelope is indistinguishable
    /// from an unreachable service, so a caller shown one goes debugging the wrong layer.
    /// </summary>
    public static Refusal Internal(string? detail) => new()
    {
        Type = "internal_error",
        Title = "An unexpected error occurred while executing the query.",
        Status = 500,
        Errors = [new QueryValidationError
        {
            Code = Binding.Codes.InternalError,
            Message = detail ?? "The engine could not answer this request; the detail is in the service log under the correlation id.",
        }],
    };

    /// <summary>The body too large: 413.</summary>
    public static Refusal RequestTooLarge(int bytes, int max) => new()
    {
        Type = "validation_error",
        Title = "The request body is too large.",
        Status = 413,
        Errors = [new QueryValidationError { Code = Binding.Codes.RequestTooLarge, Message = $"The request body is {bytes} bytes; the limit is {max}." }],
    };
}

/// <summary>The outcome of a request: rows, or a refusal.</summary>
public abstract record QueryOutcome
{
    /// <summary>Rows.</summary>
    public sealed record Success(QueryResult Result) : QueryOutcome;

    /// <summary>A refusal.</summary>
    public sealed record Refused(Refusal Refusal) : QueryOutcome;

    /// <summary>Wraps a result.</summary>
    public static QueryOutcome Of(QueryResult result) => new Success(result);

    /// <summary>Wraps a refusal.</summary>
    public static QueryOutcome Of(Refusal refusal) => new Refused(refusal);
}

/// <summary>The outcome of an explain: what would run, or a refusal.</summary>
public abstract record ExplainOutcome
{
    /// <summary>What would run.</summary>
    public sealed record Success(ExplainResult Result) : ExplainOutcome;

    /// <summary>A refusal.</summary>
    public sealed record Refused(Refusal Refusal) : ExplainOutcome;
}

/// <summary>The bound pipeline, the emitted stages and the count stages, as JSON.</summary>
public sealed record ExplainResult
{
    /// <summary>The bound pipeline in canonical form.</summary>
    [JsonPropertyName("bound")]
    public required JsonNode Bound { get; init; }

    /// <summary>The page pipeline's stages.</summary>
    [JsonPropertyName("stages")]
    public required IReadOnlyList<JsonNode> Stages { get; init; }

    /// <summary>The count pipeline's stages, when a count was requested.</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonNode>? Count { get; init; }

    /// <summary>The index advisory, when the host provides one.</summary>
    [JsonPropertyName("advisory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonNode>? Advisory { get; init; }

    /// <summary>Diagnostics the binding produced.</summary>
    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }
}
