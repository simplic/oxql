using System.Text.Json.Serialization;

namespace OxQL.AspNetCore.Models;

/// <summary>
/// Describes a single registered OxQL entity type, including its collection mapping
/// and the recursive property structure of the backing CLR type.
/// </summary>
public sealed record OxQLTypeDescriptor
{
    /// <summary>The OxQL entity type name used in query requests (e.g. "vehicle.vehicle").</summary>
    [JsonPropertyName("typeName")]
    public required string TypeName { get; init; }

    /// <summary>The backing MongoDB collection name.</summary>
    [JsonPropertyName("collectionName")]
    public required string CollectionName { get; init; }

    /// <summary>The database name override, or <c>null</c> when the adapter default is used.</summary>
    [JsonPropertyName("databaseName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatabaseName { get; init; }

    /// <summary>
    /// The fully-qualified CLR type name, or <c>null</c> for manually registered types.
    /// </summary>
    [JsonPropertyName("clrType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClrType { get; init; }

    /// <summary>
    /// Public properties of the CLR type. Empty when the registration was created manually
    /// without a CLR type.
    /// </summary>
    [JsonPropertyName("properties")]
    public IReadOnlyList<OxQLPropertyDescriptor> Properties { get; init; } = [];
}

/// <summary>
/// Describes a single public property, carrying both its scalar kind and any
/// nested / collection structure.
/// </summary>
public sealed record OxQLPropertyDescriptor
{
    /// <summary>The property name as declared on the CLR type.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The JSON-style kind of this property:
    /// <c>"string"</c>, <c>"number"</c>, <c>"boolean"</c>, <c>"Guid"</c>,
    /// <c>"DateTime"</c>, <c>"object"</c>, <c>"array"</c>, or <c>"dictionary"</c>.
    /// </summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Whether the property value may be <c>null</c>.</summary>
    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; }

    /// <summary>
    /// For <c>kind = "array"</c>: describes the element type.
    /// For <c>kind = "dictionary"</c>: describes the value type.
    /// Omitted for scalar kinds.
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OxQLPropertyDescriptor? Items { get; init; }

    /// <summary>
    /// For <c>kind = "dictionary"</c>: the scalar kind of the key (usually <c>"string"</c>).
    /// </summary>
    [JsonPropertyName("keyKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeyKind { get; init; }

    /// <summary>
    /// For <c>kind = "object"</c>: the nested properties of the child type.
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OxQLPropertyDescriptor>? Properties { get; init; }
}
