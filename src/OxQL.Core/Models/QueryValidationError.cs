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

    /// <summary>
    /// The entry of the top-level <c>filter</c> the error belongs to: its <c>id</c>, or its index in
    /// the list when it has none. An error of a filter entry carries no <c>stage</c>.
    /// </summary>
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Filter { get; init; }

    /// <summary>The wire path the error is about, when any.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>
    /// Machine-readable details, when the error is a diagnostic refused under <c>strict</c> or
    /// <c>onMissing: "refuse"</c>: the diagnostic's own <c>params</c>.
    /// </summary>
    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Params { get; init; }

    /// <summary>
    /// Only in an explain answer: what would have to change for the request to run
    /// (<c>request</c>, <c>model</c>, <c>limits</c>, <c>organisation</c>, <c>owner</c>, <c>data</c>,
    /// <c>advisory</c>; see <c>Basis</c>). Absent on a run's answer.
    /// </summary>
    [JsonPropertyName("basis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Basis { get; init; }
}
