using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents an unwind stage that deconstructs an array field.
/// </summary>
public sealed record UnwindStage
{
    /// <summary>
    /// The array field path to unwind.
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>
    /// The alias for each unwound element.
    /// </summary>
    [JsonPropertyName("as")]
    public string? As { get; init; }

    /// <summary>
    /// Whether to preserve documents where the array is null or empty.
    /// </summary>
    [JsonPropertyName("preserveNull")]
    public bool PreserveNull { get; init; }

    /// <summary>
    /// Optional field name to include the array index of the unwound element.
    /// </summary>
    [JsonPropertyName("includeIndex")]
    public string? IncludeIndex { get; init; }
}
