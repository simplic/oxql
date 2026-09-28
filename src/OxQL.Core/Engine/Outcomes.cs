using System.Text.Json;
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

    /// <summary>
    /// A request that would lose data under <c>strict</c> or <c>onMissing: "refuse"</c> (DESIGN
    /// §3.4.3, §3.6): 422, carrying every would-be diagnostic as an error with its code, message,
    /// stage, path and params.
    /// </summary>
    public static Refusal DataLoss(IReadOnlyList<Diagnostic> losses)
    {
        ArgumentNullException.ThrowIfNull(losses);

        return new Refusal
        {
            Type = "not_executable",
            Title = "The query would lose data, which strict or onMissing: refuse does not allow.",
            Status = 422,
            Errors = losses.Select(loss => new QueryValidationError
            {
                Code = loss.Code,
                Message = loss.Message,
                Stage = loss.Stage,
                Path = loss.Path,
                Params = loss.Params,
            }).ToList(),
        };
    }

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

/// <summary>
/// The outcome of an explain: the answer, or a refusal. A request that does not bind is an answer
/// (<see cref="ExplainResult.Valid"/> false, DESIGN §4.1); a refusal is left only for what stops
/// explain before binding: no organisation (403), an engine fault (500).
/// </summary>
public abstract record ExplainOutcome
{
    /// <summary>The answer.</summary>
    public sealed record Success(ExplainResult Result) : ExplainOutcome;

    /// <summary>A refusal.</summary>
    public sealed record Refused(Refusal Refusal) : ExplainOutcome;
}

/// <summary>
/// The body of <c>POST /oxql/explain</c> (DESIGN §4.2): a plain query, or the envelope
/// <c>{ query, describe?, remote?, include? }</c>. A body with <c>entityType</c> at the top is a
/// plain query: no describe, remote <c>check</c>, no include. A plain <see cref="QueryRequest"/>
/// converts to one, so a caller that explains a query as before still compiles.
/// </summary>
[JsonConverter(typeof(ExplainRequestConverter))]
public sealed record ExplainRequest
{
    /// <summary>The value of <see cref="Include"/> that asks for the index advisory.</summary>
    public const string IncludeIndexes = "indexes";

    /// <summary>The value of <see cref="Remote"/> that checks continued parts at their owners (the default).</summary>
    public const string RemoteCheck = "check";

    /// <summary>The value of <see cref="Remote"/> that leaves continued parts unchecked.</summary>
    public const string RemoteSkip = "skip";

    /// <summary>The query explained; its <c>page.cursor</c> is ignored.</summary>
    public required QueryRequest Query { get; init; }

    /// <summary>The describe requests as the caller wrote them, in order; empty for a plain query (answered by describe, DESIGN §4.2).</summary>
    public IReadOnlyList<JsonObject> Describe { get; init; } = [];

    /// <summary><see cref="RemoteCheck"/> (default) or <see cref="RemoteSkip"/>.</summary>
    public string Remote { get; init; } = RemoteCheck;

    /// <summary>The opt-in extra reads; only <see cref="IncludeIndexes"/> exists.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>Whether the body was the envelope rather than a plain query.</summary>
    public bool IsEnvelope { get; init; }

    /// <summary>Whether the index advisory was asked for.</summary>
    public bool IncludesIndexes => Include.Contains(IncludeIndexes, StringComparer.Ordinal);

    /// <summary>A plain query: no describe, remote check, no include.</summary>
    public static implicit operator ExplainRequest(QueryRequest query) => new() { Query = query ?? throw new ArgumentNullException(nameof(query)) };
}

/// <summary>
/// Reads the explain body. A malformed body (neither a query nor the envelope, an unknown envelope
/// member, an <c>include</c> or <c>remote</c> value the engine does not know) is a
/// <see cref="JsonException"/>, which the host answers 400 like any malformed body. Written back,
/// a request is its envelope form.
/// </summary>
public sealed class ExplainRequestConverter : JsonConverter<ExplainRequest>
{
    private static readonly string[] EnvelopeMembers = ["query", "describe", "remote", "include"];

    /// <inheritdoc/>
    public override ExplainRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var body = document.RootElement;

        if (body.ValueKind != JsonValueKind.Object)
            throw new JsonException("An explain body is a query or an envelope { query, describe?, remote?, include? }.");

        if (body.TryGetProperty("entityType", out _))
            return new ExplainRequest { Query = QueryOf(body, options) };

        foreach (var member in body.EnumerateObject())
            if (!EnvelopeMembers.Contains(member.Name, StringComparer.Ordinal))
                throw new JsonException($"'{member.Name}' is not a member of an explain envelope; it carries query, describe, remote and include.");

        if (!body.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.Object)
            throw new JsonException("An explain envelope carries the query under 'query'.");

        return new ExplainRequest
        {
            Query = QueryOf(query, options),
            Describe = DescribeOf(body),
            Remote = RemoteOf(body),
            Include = IncludeOf(body),
            IsEnvelope = true,
        };
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ExplainRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("query");
        JsonSerializer.Serialize(writer, value.Query, options);

        if (value.Describe.Count > 0)
        {
            writer.WritePropertyName("describe");
            JsonSerializer.Serialize(writer, value.Describe, options);
        }

        writer.WriteString("remote", value.Remote);

        if (value.Include.Count > 0)
        {
            writer.WritePropertyName("include");
            JsonSerializer.Serialize(writer, value.Include, options);
        }

        writer.WriteEndObject();
    }

    private static QueryRequest QueryOf(JsonElement element, JsonSerializerOptions options) =>
        element.Deserialize<QueryRequest>(options) ?? throw new JsonException("The explained query is null.");

    private static List<JsonObject> DescribeOf(JsonElement body)
    {
        var describe = new List<JsonObject>();

        if (!body.TryGetProperty("describe", out var requests) || requests.ValueKind == JsonValueKind.Null)
            return describe;

        if (requests.ValueKind != JsonValueKind.Array)
            throw new JsonException("'describe' is an array of describe requests.");

        foreach (var entry in requests.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new JsonException("A describe request is an object.");

            describe.Add(JsonNode.Parse(entry.GetRawText())!.AsObject());
        }

        return describe;
    }

    private static string RemoteOf(JsonElement body)
    {
        if (!body.TryGetProperty("remote", out var remote) || remote.ValueKind == JsonValueKind.Null)
            return ExplainRequest.RemoteCheck;

        var value = remote.ValueKind == JsonValueKind.String ? remote.GetString() : null;

        return value is ExplainRequest.RemoteCheck or ExplainRequest.RemoteSkip
            ? value
            : throw new JsonException("'remote' is \"check\" or \"skip\".");
    }

    private static List<string> IncludeOf(JsonElement body)
    {
        var include = new List<string>();

        if (!body.TryGetProperty("include", out var entries) || entries.ValueKind == JsonValueKind.Null)
            return include;

        if (entries.ValueKind != JsonValueKind.Array)
            throw new JsonException("'include' is an array; it may name \"indexes\".");

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() != ExplainRequest.IncludeIndexes)
                throw new JsonException("'include' may name \"indexes\" only.");

            if (!include.Contains(ExplainRequest.IncludeIndexes))
                include.Add(ExplainRequest.IncludeIndexes);
        }

        return include;
    }
}

/// <summary>
/// Everything about a query without running it (DESIGN §4.3): whether it binds, every error, the
/// shape after each stage, the result shape, describe, notes, and, when it binds, the canonical
/// bound form and the emitted Mongo stages. Explain never executes the query; the index advisory
/// (<see cref="Advisory"/>) reads only the index list, and only when the request asks for it.
/// </summary>
public sealed record ExplainResult
{
    /// <summary>Whether the query binds and this host can run it. False: <see cref="Errors"/> says why, and <see cref="Bound"/> and <see cref="Stages"/> are absent.</summary>
    [JsonPropertyName("valid")]
    public required bool Valid { get; init; }

    /// <summary>The contract the request was read as: 1 without the contract header while compatibility is on, 2 otherwise.</summary>
    [JsonPropertyName("contract")]
    public required int Contract { get; init; }

    /// <summary>The engine version and the capabilities of this host, as health publishes them.</summary>
    [JsonPropertyName("engine")]
    public required ExplainEngine Engine { get; init; }

    /// <summary>The schema revision the answer was bound against, when the host knows it.</summary>
    [JsonPropertyName("schemaRevision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaRevision { get; init; }

    /// <summary>Every binding error; empty when <see cref="Valid"/>.</summary>
    [JsonPropertyName("errors")]
    public required IReadOnlyList<QueryValidationError> Errors { get; init; }

    /// <summary>The diagnostics binding produced; always present.</summary>
    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>Engine-behaviour notes (DESIGN §4.4); always present.</summary>
    [JsonPropertyName("notes")]
    public required IReadOnlyList<Diagnostic> Notes { get; init; }

    /// <summary>One entry per caller stage, in order, also for a request that does not bind.</summary>
    [JsonPropertyName("steps")]
    public required IReadOnlyList<ExplainStep> Steps { get; init; }

    /// <summary>The final shape: paging and the visible columns. Absent when the entity itself did not bind.</summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExplainShapeResult? Result { get; init; }

    /// <summary>One answer per describe request, in request order; always present.</summary>
    [JsonPropertyName("describe")]
    public required IReadOnlyList<JsonNode> Describe { get; init; }

    /// <summary>The bound pipeline in canonical form; absent when not <see cref="Valid"/>.</summary>
    [JsonPropertyName("bound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Bound { get; init; }

    /// <summary>The page pipeline's stages; absent when not <see cref="Valid"/>.</summary>
    [JsonPropertyName("stages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonNode>? Stages { get; init; }

    /// <summary>The count pipeline's stages, when a count was requested.</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonNode>? Count { get; init; }

    /// <summary>The collation both pipelines run under, when a string comparison, sort or group key folds case.</summary>
    [JsonPropertyName("collation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Collation { get; init; }

    /// <summary>The static index advisory: only with <c>include: ["indexes"]</c> on a host with an index source.</summary>
    [JsonPropertyName("advisory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonNode>? Advisory { get; init; }

    /// <summary>The answer to a request refused before its stages were bound (a contract 1 rewrite): not valid, the errors, nothing else.</summary>
    public static ExplainResult Invalid(int contract, ExplainEngine engine, IReadOnlyList<QueryValidationError> errors) => new()
    {
        Valid = false,
        Contract = contract,
        Engine = engine,
        Errors = errors,
        Diagnostics = [],
        Notes = [],
        Steps = [],
        Describe = [],
    };
}

/// <summary>The engine block of an explain answer.</summary>
public sealed record ExplainEngine
{
    /// <summary>The engine version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The capabilities, as <see cref="EngineCapabilities.Of"/> names them.</summary>
    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>
/// One caller stage as explain bound it (DESIGN §4.3). <see cref="Executor"/>, <see cref="Phase"/>
/// and <see cref="Owner"/> are written as <c>null</c> where they do not apply.
/// </summary>
public sealed record ExplainStep
{
    /// <summary>The stage's index in the caller's pipeline.</summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    /// <summary>The stage kind (<c>match</c>, <c>lookup</c>, …), or null for a stage that names none.</summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Kind { get; init; }

    /// <summary><c>ok</c>, <c>error</c>, or <c>skipped</c>: it failed only under an alias an earlier stage failed to create (DESIGN §3.8).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>For a join stage: <c>inline</c>, <c>keyed-local</c>, <c>keyed-remote</c>, <c>continued</c>; null elsewhere.</summary>
    [JsonPropertyName("executor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Executor { get; init; }

    /// <summary>For a join stage: <c>beforePage</c>, <c>afterPage</c>, <c>owner</c>; null elsewhere.</summary>
    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Phase { get; init; }

    /// <summary>For a keyed or continued stage: the owner and the query forwarded to it; null elsewhere.</summary>
    [JsonPropertyName("owner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonNode? Owner { get; init; }

    /// <summary>For a join stage: the reference as bound.</summary>
    [JsonPropertyName("reference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Reference { get; init; }

    /// <summary>The aliases the stage adds to the shape; empty for a stage that adds none.</summary>
    [JsonPropertyName("creates")]
    public required IReadOnlyList<ExplainCreated> Creates { get; init; }

    /// <summary>For a keyed stage: the stages continued at its owner.</summary>
    [JsonPropertyName("continued")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonNode>? Continued { get; init; }

    /// <summary>The shape after the stage.</summary>
    [JsonPropertyName("shapeAfter")]
    public required ExplainShapeSummary ShapeAfter { get; init; }
}

/// <summary>
/// An alias a stage creates: <c>entity</c>, <c>element</c> (with its <see cref="Source"/>),
/// <c>array</c> (a lookup), <c>remote</c> and <c>keyed</c> (with <see cref="Entities"/>),
/// <c>scalar</c> and <c>group</c> (with <see cref="Kind"/>).
/// </summary>
public sealed record ExplainCreated
{
    /// <summary>The alias.</summary>
    [JsonPropertyName("alias")]
    public required string Alias { get; init; }

    /// <summary>The node the alias is.</summary>
    [JsonPropertyName("node")]
    public required string Node { get; init; }

    /// <summary>The entity an <c>entity</c>, <c>element</c> or <c>array</c> node holds.</summary>
    [JsonPropertyName("entity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity { get; init; }

    /// <summary>The entities (or <c>entity#item</c> targets) a <c>remote</c> or <c>keyed</c> node may hold.</summary>
    [JsonPropertyName("entities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Entities { get; init; }

    /// <summary>The collection an <c>element</c> node was unwound from, as a wire path.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>The kind a <c>scalar</c> or <c>group</c> node holds.</summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }
}

/// <summary>What the rows look like after a stage.</summary>
public sealed record ExplainShapeSummary
{
    /// <summary><c>cursor</c> while every row is one entity row with its key (keyset paging); <c>offset</c> after an unwind or a group.</summary>
    [JsonPropertyName("paging")]
    public required string Paging { get; init; }

    /// <summary>Whether a group ran.</summary>
    [JsonPropertyName("grouped")]
    public required bool Grouped { get; init; }

    /// <summary>The collections unwound so far, as wire paths, in ordinal order.</summary>
    [JsonPropertyName("unwound")]
    public required IReadOnlyList<string> Unwound { get; init; }

    /// <summary>The paths an inclusion projection kept, in ordinal order; null while no inclusion projection ran.</summary>
    [JsonPropertyName("projection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<string>? Projection { get; init; }
}

/// <summary>The final shape of the query (DESIGN §4.3 <c>result</c>).</summary>
public sealed record ExplainShapeResult
{
    /// <summary>As <see cref="ExplainShapeSummary.Paging"/>, for the final shape.</summary>
    [JsonPropertyName("paging")]
    public required string Paging { get; init; }

    /// <summary>The final shape's visible members and roots.</summary>
    [JsonPropertyName("columns")]
    public required IReadOnlyList<ExplainColumn> Columns { get; init; }
}

/// <summary>One visible member or root of the final shape.</summary>
public sealed record ExplainColumn
{
    /// <summary>The wire path.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The kind.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Whether the value may be null.</summary>
    [JsonPropertyName("nullable")]
    public required bool Nullable { get; init; }

    /// <summary>The stage that created the member, or null for a member of the entry shape.</summary>
    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Stage { get; init; }

    /// <summary>The root the member lies under, <c>""</c> for the entity itself.</summary>
    [JsonPropertyName("root")]
    public required string Root { get; init; }
}
