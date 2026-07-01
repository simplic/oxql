using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// Represents a complete query request to be executed against a document store.
/// </summary>
public sealed record QueryRequest
{
    /// <summary>
    /// The entity type (collection) to query.
    /// </summary>
    [JsonPropertyName("entityType")]
    public required string EntityType { get; init; }

    /// <summary>
    /// Variables that can be referenced in filter expressions using $var syntax.
    /// </summary>
    [JsonPropertyName("variables")]
    public QueryVariables? Variables { get; init; }

    /// <summary>
    /// The ordered list of pipeline stages to execute.
    /// </summary>
    [JsonPropertyName("pipeline")]
    public required IReadOnlyList<PipelineStage> Pipeline { get; init; }
}

/// <summary>
/// A dictionary of named variables for use in query expressions.
/// </summary>
public sealed record QueryVariables
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    [JsonExtensionData]
    public Dictionary<string, object?> Values
    {
        get => _values;
        init => _values = value ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a variable value by name.
    /// </summary>
    public object? GetValue(string name) =>
        _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Checks if a variable exists.
    /// </summary>
    public bool HasVariable(string name) => _values.ContainsKey(name);
}
