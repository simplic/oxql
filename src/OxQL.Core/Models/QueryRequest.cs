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
    /// The conditions the engine places (contract 2): a list of <c>{ id?, where }</c>, all of
    /// which a row must meet. Each condition runs at the earliest point of the pipeline where the
    /// paths it reads are in the row. Kept as written: the binder reads it under contract 2, and
    /// contract 1, which never had the member, ignores it as it ignores every member it does not know.
    /// </summary>
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Filter { get; init; }

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
    /// The request's default for a join target that is missing (contract 2): <c>"null"</c>,
    /// <c>"report"</c> or <c>"refuse"</c>. Every <c>resolve</c> of the request that names no
    /// <c>onMissing</c> of its own takes it, in place of the default <c>strict</c> gives it
    /// (<c>refuse</c> under strict, else <c>null</c>). Read as written: contract 1 ignores it
    /// whatever it holds, contract 2 refuses another kind or word.
    /// </summary>
    [JsonPropertyName("onMissing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? OnMissing { get; init; }

    /// <summary>The words <see cref="OnMissing"/> takes.</summary>
    public static readonly IReadOnlyList<string> MissingPolicies = ["null", "report", "refuse"];

    /// <summary>The request's default for a missing join target as one of <see cref="MissingPolicies"/>, or null when the request names none (or none that is one).</summary>
    [JsonIgnore]
    public string? MissingDefault => OnMissing is { ValueKind: JsonValueKind.String } written && written.GetString() is { } word && MissingPolicies.Contains(word) ? word : null;

    /// <summary>
    /// The per-key owner answer a keyed fetch asks for: the rows whose
    /// <c>path</c> holds one of <c>keys</c>, at most <c>perKey</c> per key. Accepted only on an
    /// internal call (<see cref="Binding.RequestContext.Internal"/>: the internal batch route and
    /// this host's own <c>SelfOwner</c>); on the public route it is <c>UNKNOWN_REQUEST_MEMBER</c>, so
    /// it never becomes public syntax.
    /// </summary>
    [JsonPropertyName("keyedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeyedByMember? KeyedBy { get; init; }

    /// <summary>
    /// Whether the answer carries where its time went (the <c>timing</c> member beside <c>pageInfo</c>):
    /// the database commands with what each returned, the owners asked, and this host's own share. The
    /// header <c>X-OxQL-Timing</c> asks the same. It changes no row and is no part of a cursor.
    /// Contract 2 only, and only <c>true</c> asks (<see cref="AsksTiming"/>). Kept as written, as
    /// <see cref="Filter"/> is: contract 1 never had the member and ignores it whatever it holds, as it
    /// ignores every member it does not know; under contract 2 the binder refuses a value that is no boolean.
    /// </summary>
    [JsonPropertyName("timing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Timing { get; init; }

    /// <summary>
    /// A ceiling on this request's time, in milliseconds, under the host's own
    /// (<c>Execution:MaxTimeMs</c>) and under what a batch leaves it (contract 2): the page, the count
    /// and the owners it asks end within it, and a request that does not is <c>QUERY_TIMEOUT</c>. A
    /// value above the host's ceiling is the host's ceiling; a value below 1 is 1
    /// (<see cref="TimeCeilingMs"/>). It changes no row and is no part of a cursor. Kept as written:
    /// contract 1 never had the member and ignores it whatever it holds; under contract 2 the binder
    /// refuses a value that is no whole number.
    /// </summary>
    [JsonPropertyName("maxTimeMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? MaxTimeMs { get; init; }

    /// <summary>The <c>timing</c> member of a request that asks for the breakdown: what a host writes on a request it sends on.</summary>
    public static JsonElement TimingAsked { get; } = JsonSerializer.SerializeToElement(true);

    /// <summary>Whether <see cref="Timing"/> asks for the breakdown: written as <c>true</c>.</summary>
    [JsonIgnore]
    public bool AsksTiming => Timing is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// The ceiling <see cref="MaxTimeMs"/> names, at least 1 and at most what an int holds; null when
    /// the member is absent, null or no whole number.
    /// </summary>
    [JsonIgnore]
    public int? TimeCeilingMs => MaxTimeMs is { ValueKind: JsonValueKind.Number } written && IsWhole(written, out var ms)
        ? (int)Math.Clamp(ms, 1d, int.MaxValue)
        : null;

    /// <summary>Whether a JSON number is whole (<c>3000</c>, <c>3e3</c>, <c>3000.0</c>); a fraction, an infinity and what no double holds are not.</summary>
    internal static bool IsWhole(JsonElement number, out double value) =>
        number.TryGetDouble(out value) && double.IsFinite(value) && Math.Floor(value) == value;

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
    /// lookup; a resolve's owner query carries none.
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
