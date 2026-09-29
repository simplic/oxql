using System.Text.Json;
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

    /// <summary>
    /// Whether a condition that loses data refuses the request instead of travelling as a
    /// diagnostic: a missing or ambiguous reference, a truncated lookup, resolve or flatten, an
    /// owner that did not answer, a page that holds fewer rows than match. A strict request
    /// without <c>cursor</c> or <c>offset</c> may ask for a page up to <c>MaxReportPageSize</c>.
    /// Contract 2 only; it never changes rows, so it is not part of the cursor fingerprint.
    /// </summary>
    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; init; }

    /// <summary>
    /// The per-key owner answer a keyed fetch asks for (DESIGN §3.5.2 step 3): the rows whose
    /// <c>path</c> holds one of <c>keys</c>, at most <c>perKey</c> per key. Accepted only on an
    /// internal call (<see cref="Binding.RequestContext.Internal"/>: the internal batch route and
    /// this host's own <c>SelfOwner</c>); on the public route it is <c>UNKNOWN_REQUEST_MEMBER</c>, so
    /// it never becomes public syntax.
    /// </summary>
    [JsonPropertyName("keyedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeyedByMember? KeyedBy { get; init; }

    /// <summary>Whether the request is strict: <see cref="Strict"/> written as true.</summary>
    [JsonIgnore]
    public bool IsStrict => Strict == true;

    /// <summary>
    /// The top-level members the caller wrote that a request does not have. The binder refuses
    /// them under contract 2 (<c>UNKNOWN_REQUEST_MEMBER</c>): a member an older engine drops,
    /// such as <c>strict</c>, would otherwise run the request without what it asked for.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>
/// The internal request member <c>keyedBy</c>: the owner's rows grouped per key. <see cref="Path"/>
/// is the matched member on the entity, through one item collection for an item target
/// (<c>billingLines.id</c>), where each row then carries the matched element under <c>oxEl</c>.
/// </summary>
public sealed record KeyedByMember
{
    /// <summary>The member the keys are matched on, as a wire path of the entity.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The keys, as wire values of that member.</summary>
    [JsonPropertyName("keys")]
    public JsonElement? Keys { get; init; }

    /// <summary>At most this many rows per key, first by record key; 2 by default, which tells one row from several.</summary>
    [JsonPropertyName("perKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerKey { get; init; }

    /// <summary>
    /// The entity of the asking host whose key the keys are: <see cref="Path"/> must declare a
    /// reference to it, as a local lookup's path must (<c>LOOKUP_NOT_DECLARED</c>). Sent by a remote
    /// lookup (DESIGN §3.4.4); a resolve's owner query carries none.
    /// </summary>
    [JsonPropertyName("references")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? References { get; init; }

    /// <summary>
    /// <c>"entity"</c>: a path through one collection answers whole rows, one per key a row holds in
    /// some element, the key under <c>oxKey</c>, instead of one row per matching element under
    /// <c>oxEl</c> (the default). Sent by a remote lookup whose child is the entity itself.
    /// </summary>
    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rows { get; init; }

    /// <summary>Members of <c>keyedBy</c> this engine does not know; refused, so a newer caller's member is never silently dropped.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
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
}
