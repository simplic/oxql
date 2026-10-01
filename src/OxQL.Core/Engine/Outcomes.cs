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

    /// <summary>
    /// More explains than a caller may send or have in flight: 429, refused before the body is read.
    /// <paramref name="limit"/> is <c>rate</c>, <c>concurrentPerUser</c>, <c>concurrentPerHost</c> or
    /// <c>concurrentPerCaller</c>; the host answers <paramref name="retryAfterSeconds"/> as <c>Retry-After</c>.
    /// </summary>
    public static Refusal TooManyExplains(string limit, int max, int retryAfterSeconds) => new()
    {
        Type = "rate_limited",
        Title = "Too many explains.",
        Status = 429,
        Errors = [new QueryValidationError
        {
            Code = Binding.Codes.ExplainLimit,
            Message = limit == "rate"
                ? $"More explains than {max} a minute; retry in {retryAfterSeconds} s."
                : $"More explains in flight than {max} ({limit}); retry in {retryAfterSeconds} s.",
            Params = new Dictionary<string, object?> { ["limit"] = limit, ["max"] = max, ["retryAfter"] = retryAfterSeconds },
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
/// explain before binding: no organisation (403), a body past the explain bounds (400), an engine fault (500).
/// </summary>
public abstract record ExplainOutcome
{
    /// <summary>The answer.</summary>
    public sealed record Success(ExplainResult Result) : ExplainOutcome;

    /// <summary>A refusal.</summary>
    public sealed record Refused(Refusal Refusal) : ExplainOutcome;
}

/// <summary>
/// What an internal explain may still spend of the explain it belongs to (no amplification): the
/// time left of the origin's wall time and the owner calls left of its total. An owner asks its own
/// owners only within both and never starts rounds of its own beyond them. Only an internal call
/// carries it; the public route refuses the member.
/// </summary>
/// <param name="Ms">The milliseconds left of the origin's explain.</param>
/// <param name="Calls">The owner calls (one per service and round) left of the origin's explain.</param>
public sealed record ExplainBudget(int Ms, int Calls);

/// <summary>
/// The body of <c>POST /oxql/explain</c>: a plain query, or the envelope
/// <c>{ query, include?, shape?: { depth }, remote?, catalog? }</c>. A body with <c>entityType</c> at
/// the top is a plain query, which is the envelope with its defaults: include <c>shape</c> and
/// <c>notes</c>, remote <c>check</c>, no catalog. The types are answered by reference: a caller that
/// holds no schema document asks for the member rows with <c>include: "types"</c>, to which alone the
/// shape depth (<see cref="Models.ExplainOptions.DefaultShapeDepth"/>) applies. A plain
/// <see cref="QueryRequest"/> converts to one.
/// </summary>
[JsonConverter(typeof(ExplainRequestConverter))]
public sealed record ExplainRequest
{
    /// <summary>The <see cref="Include"/> value for the per-stage shapes, the type references and the flag rules (default).</summary>
    public const string IncludeShape = "shape";

    /// <summary>
    /// The <see cref="Include"/> value for the member rows of every type, the flag sets and the per-root
    /// overrides: what a caller without the services' schema documents needs. It implies <see cref="IncludeShape"/>.
    /// </summary>
    public const string IncludeTypes = "types";

    /// <summary>The <see cref="Include"/> value for the engine-behaviour notes (default).</summary>
    public const string IncludeNotes = "notes";

    /// <summary>The <see cref="Include"/> value for the descriptions of types, members and enum values, which the type table leaves out otherwise.</summary>
    public const string IncludeDocs = "docs";

    /// <summary>The <see cref="Include"/> value for the plan: the bound form, the emitted stages and every owner query.</summary>
    public const string IncludePlan = "plan";

    /// <summary>The <see cref="Include"/> value that asks for the index advisory.</summary>
    public const string IncludeIndexes = "indexes";

    /// <summary>The value of <see cref="Remote"/> that checks continued parts at their owners (the default).</summary>
    public const string RemoteCheck = "check";

    /// <summary>
    /// The value of <see cref="Remote"/> that answers from owner answers already kept and asks no owner.
    /// Accepted; until the cached tier is built it is answered as <see cref="RemoteCheck"/>.
    /// </summary>
    public const string RemoteCached = "cached";

    /// <summary>The query explained; its <c>page.cursor</c> is ignored.</summary>
    public required QueryRequest Query { get; init; }

    /// <summary><see cref="RemoteCheck"/> (default) or <see cref="RemoteCached"/>.</summary>
    public string Remote { get; init; } = RemoteCheck;

    /// <summary>The members asked for: <see cref="DefaultIncludes"/> unless the envelope names its own.</summary>
    public IReadOnlyList<string> Include { get; init; } = DefaultIncludes;

    /// <summary>What an explain answers when the body names no <c>include</c>.</summary>
    public static readonly IReadOnlyList<string> DefaultIncludes = [IncludeShape, IncludeNotes];

    /// <summary>The <c>include</c> values the engine knows.</summary>
    public static readonly IReadOnlyList<string> KnownIncludes = [IncludeShape, IncludeNotes, IncludeTypes, IncludeDocs, IncludePlan, IncludeIndexes];

    /// <summary>The <c>remote</c> values the engine knows.</summary>
    public static readonly IReadOnlyList<string> KnownRemotes = [RemoteCheck, RemoteCached];

    /// <summary>
    /// The <c>catalog</c> entries as the caller wrote them (<c>{ id, entity, prefix?, depth?, referencing? }</c>):
    /// the one lookup of an entity outside the query, at most <c>Explain.MaxCatalogEntries</c>.
    /// </summary>
    public IReadOnlyList<JsonObject> Catalog { get; init; } = [];

    /// <summary>The <c>shape.depth</c> the caller asked for (at most <c>Explain.MaxShapeDepth</c>), or null for the default: the levels of member rows <c>include: "types"</c> answers.</summary>
    public int? ShapeDepth { get; init; }

    /// <summary>
    /// The <c>include</c> and <c>remote</c> values the engine does not know, in the order written;
    /// such a request is refused with <c>EXPLAIN_LIMIT</c> before anything is bound (<see cref="ExplainLimits"/>).
    /// </summary>
    public IReadOnlyList<ExplainUnknownValue> UnknownValues { get; init; } = [];

    /// <summary>Whether the body was the envelope rather than a plain query.</summary>
    public bool IsEnvelope { get; init; }

    /// <summary>What an internal explain may still spend of its origin's explain; null on a public one.</summary>
    public ExplainBudget? Budget { get; init; }

    /// <summary>Whether the per-stage shapes, the type references and the flag rules were asked for.</summary>
    public bool IncludesShape => Include.Contains(IncludeShape, StringComparer.Ordinal) || IncludesTypes;

    /// <summary>Whether the member rows, the flag sets and the overrides were asked for.</summary>
    public bool IncludesTypes => Include.Contains(IncludeTypes, StringComparer.Ordinal);

    /// <summary>Whether the notes were asked for.</summary>
    public bool IncludesNotes => Include.Contains(IncludeNotes, StringComparer.Ordinal);

    /// <summary>Whether the descriptions were asked for.</summary>
    public bool IncludesDocs => Include.Contains(IncludeDocs, StringComparer.Ordinal);

    /// <summary>Whether the plan was asked for.</summary>
    public bool IncludesPlan => Include.Contains(IncludePlan, StringComparer.Ordinal);

    /// <summary>Whether the index advisory was asked for.</summary>
    public bool IncludesIndexes => Include.Contains(IncludeIndexes, StringComparer.Ordinal);

    /// <summary>A plain query: the envelope's defaults.</summary>
    public static implicit operator ExplainRequest(QueryRequest query) => new() { Query = query ?? throw new ArgumentNullException(nameof(query)) };
}

/// <summary>
/// Reads the explain body. A malformed body (neither a query nor the envelope, an unknown envelope
/// member, a member of the wrong kind) is a <see cref="JsonException"/>, which the host answers 400
/// like any malformed body. An <c>include</c> or <c>remote</c> value the engine does not know is
/// kept in <see cref="ExplainRequest.UnknownValues"/> and refused as <c>EXPLAIN_LIMIT</c>
/// (<see cref="ExplainLimits"/>). Written back, a request is its envelope form.
/// </summary>
public sealed class ExplainRequestConverter : JsonConverter<ExplainRequest>
{
    private static readonly string[] EnvelopeMembers = ["query", "remote", "include", "catalog", "shape", "budget"];

    /// <inheritdoc/>
    public override ExplainRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var body = document.RootElement;

        if (body.ValueKind != JsonValueKind.Object)
            throw new JsonException("An explain body is a query or an envelope { query, include?, shape?, remote?, catalog? }.");

        if (body.TryGetProperty("entityType", out _))
            return new ExplainRequest { Query = QueryOf(body, options) };

        foreach (var member in body.EnumerateObject())
            if (!EnvelopeMembers.Contains(member.Name, StringComparer.Ordinal))
                throw new JsonException($"'{member.Name}' is not a member of an explain envelope; it carries query, include, shape, remote and catalog.");

        if (!body.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.Object)
            throw new JsonException("An explain envelope carries the query under 'query'.");

        var unknown = new List<ExplainUnknownValue>();

        return new ExplainRequest
        {
            Query = QueryOf(query, options),
            Remote = RemoteOf(body, unknown),
            Include = IncludeOf(body, unknown),
            Catalog = ObjectsOf(body, "catalog", "catalog entries"),
            ShapeDepth = ShapeDepthOf(body),
            Budget = BudgetOf(body),
            UnknownValues = unknown,
            IsEnvelope = true,
        };
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ExplainRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("query");
        JsonSerializer.Serialize(writer, value.Query, options);
        writer.WriteString("remote", value.Remote);

        if (!value.Include.SequenceEqual(ExplainRequest.DefaultIncludes, StringComparer.Ordinal))
        {
            writer.WritePropertyName("include");
            JsonSerializer.Serialize(writer, value.Include, options);
        }

        if (value.Catalog.Count > 0)
        {
            writer.WritePropertyName("catalog");
            JsonSerializer.Serialize(writer, value.Catalog, options);
        }

        if (value.ShapeDepth is { } depth)
        {
            writer.WriteStartObject("shape");
            writer.WriteNumber("depth", depth);
            writer.WriteEndObject();
        }

        if (value.Budget is { } budget)
        {
            writer.WriteStartObject("budget");
            writer.WriteNumber("ms", budget.Ms);
            writer.WriteNumber("calls", budget.Calls);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static QueryRequest QueryOf(JsonElement element, JsonSerializerOptions options) =>
        element.Deserialize<QueryRequest>(options) ?? throw new JsonException("The explained query is null.");

    private static List<JsonObject> ObjectsOf(JsonElement body, string name, string what)
    {
        var objects = new List<JsonObject>();

        if (!body.TryGetProperty(name, out var entries) || entries.ValueKind == JsonValueKind.Null)
            return objects;

        if (entries.ValueKind != JsonValueKind.Array)
            throw new JsonException($"'{name}' is an array of {what}.");

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new JsonException($"An entry of '{name}' is an object.");

            objects.Add(JsonNode.Parse(entry.GetRawText())!.AsObject());
        }

        return objects;
    }

    private static string RemoteOf(JsonElement body, List<ExplainUnknownValue> unknown)
    {
        if (!body.TryGetProperty("remote", out var remote) || remote.ValueKind == JsonValueKind.Null)
            return ExplainRequest.RemoteCheck;

        if (remote.ValueKind != JsonValueKind.String)
            throw new JsonException("'remote' is a string.");

        var value = remote.GetString()!;

        if (ExplainRequest.KnownRemotes.Contains(value, StringComparer.Ordinal))
            return value;

        unknown.Add(new ExplainUnknownValue("remote", value));

        return ExplainRequest.RemoteCheck;
    }

    private static IReadOnlyList<string> IncludeOf(JsonElement body, List<ExplainUnknownValue> unknown)
    {
        if (!body.TryGetProperty("include", out var entries) || entries.ValueKind == JsonValueKind.Null)
            return ExplainRequest.DefaultIncludes;

        if (entries.ValueKind != JsonValueKind.Array)
            throw new JsonException("'include' is an array of strings.");

        var include = new List<string>();

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                throw new JsonException("An entry of 'include' is a string.");

            var value = entry.GetString()!;

            if (!ExplainRequest.KnownIncludes.Contains(value, StringComparer.Ordinal))
                unknown.Add(new ExplainUnknownValue("include", value));
            else if (!include.Contains(value, StringComparer.Ordinal))
                include.Add(value);
        }

        return include;
    }

    private static int? ShapeDepthOf(JsonElement body)
    {
        if (!body.TryGetProperty("shape", out var shape) || shape.ValueKind == JsonValueKind.Null)
            return null;

        if (shape.ValueKind != JsonValueKind.Object)
            throw new JsonException("'shape' is an object { depth }.");

        int? depth = null;

        foreach (var member in shape.EnumerateObject())
        {
            if (member.Name != "depth")
                throw new JsonException($"'{member.Name}' is not a member of 'shape'; it carries depth.");

            if (member.Value.ValueKind != JsonValueKind.Number || !member.Value.TryGetInt32(out var value) || value < 1)
                throw new JsonException("'shape.depth' is a whole number of at least 1.");

            depth = value;
        }

        return depth;
    }

    private static ExplainBudget? BudgetOf(JsonElement body)
    {
        if (!body.TryGetProperty("budget", out var budget) || budget.ValueKind == JsonValueKind.Null)
            return null;

        if (budget.ValueKind != JsonValueKind.Object
            || !budget.TryGetProperty("ms", out var ms) || ms.ValueKind != JsonValueKind.Number || !ms.TryGetInt32(out var milliseconds)
            || !budget.TryGetProperty("calls", out var calls) || calls.ValueKind != JsonValueKind.Number || !calls.TryGetInt32(out var left))
            throw new JsonException("'budget' is an object { ms, calls } of whole numbers.");

        return new ExplainBudget(Math.Max(0, milliseconds), Math.Max(0, left));
    }
}

/// <summary>
/// Everything about a query without running it: whether it binds, every error, each stage with its
/// placement and the shape after it, the aliases, one shared table of types, the result columns, the
/// owners asked, and, on request, the plan. Explain is the bind trace, normalised: one answer says
/// everything a builder shows. It never executes the query; the index advisory (<see cref="Advisory"/>)
/// reads only the index list, and only when the request asks for it.
/// </summary>
public sealed record ExplainResult
{
    /// <summary>Whether the query binds and this host can run it. False: <see cref="Errors"/> says why.</summary>
    [JsonPropertyName("valid")]
    public required bool Valid { get; init; }

    /// <summary>The contract the request was read as: 1 without the contract header while compatibility is on, 2 otherwise.</summary>
    [JsonPropertyName("contract")]
    public required int Contract { get; init; }

    /// <summary>The engine version, the contract it speaks and the capabilities of this host, as health publishes them.</summary>
    [JsonPropertyName("engine")]
    public required ExplainEngine Engine { get; init; }

    /// <summary>A weak validator of the answer: it changes when the request, a revision or the capabilities change.</summary>
    [JsonPropertyName("etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Etag { get; init; }

    /// <summary>The revisions the answer was bound against.</summary>
    [JsonPropertyName("revision")]
    public ExplainRevision Revision { get; init; } = new();

    /// <summary>How long the answer may be kept, the owners it depends on, and whether it is complete.</summary>
    [JsonPropertyName("cache")]
    public ExplainCache Cache { get; init; } = new();

    /// <summary>Every binding error; empty when <see cref="Valid"/>.</summary>
    [JsonPropertyName("errors")]
    public required IReadOnlyList<QueryValidationError> Errors { get; init; }

    /// <summary>The diagnostics binding produced, also for a request that does not bind; always present.</summary>
    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>Engine-behaviour notes (DESIGN §4.4); always present, empty without <c>include: "notes"</c> except the notes about the answer itself.</summary>
    [JsonPropertyName("notes")]
    public required IReadOnlyList<Diagnostic> Notes { get; init; }

    /// <summary>The shape before the first stage; absent when the entity itself did not bind or the shape was not asked for.</summary>
    [JsonPropertyName("entry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExplainEntry? Entry { get; init; }

    /// <summary>One entry per caller stage, in order, also for a request that does not bind.</summary>
    [JsonPropertyName("stages")]
    public required IReadOnlyList<ExplainStage> Stages { get; init; }

    /// <summary>Every alias a stage creates, by name: the stage, the node, the targets, its type and who holds it.</summary>
    [JsonPropertyName("aliases")]
    public JsonObject Aliases { get; init; } = [];

    /// <summary>
    /// The types the roots point to: <c>t:&lt;entity&gt;[#item]</c> names an entity (or the element of an item
    /// collection on it) of a service's schema document at a revision, <c>u:&lt;alias&gt;</c> the union of several.
    /// With <c>include: "types"</c> each also carries its member rows.
    /// </summary>
    [JsonPropertyName("types")]
    public JsonObject Types { get; init; } = [];

    /// <summary>
    /// The flag rules the stage shapes point to (<c>r:n</c>): where the members of one root stand at one
    /// point of the pipeline, from which a reader of the schema documents derives each member's flags.
    /// </summary>
    [JsonPropertyName("rules")]
    public JsonObject Rules { get; init; } = [];

    /// <summary>The flag sets the type rows (by the id of the flags) and the stage shapes (<c>o:n</c>) point to; only with <c>include: "types"</c>.</summary>
    [JsonPropertyName("flagSets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? FlagSets { get; init; }

    /// <summary>The final shape: paging and the visible columns. Absent when the entity itself did not bind.</summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExplainShapeResult? Result { get; init; }

    /// <summary>The owners the query reaches, each once; <c>stages[].placement.owner</c> and <c>aliases.*.targets[].owner</c> point here by index.</summary>
    [JsonPropertyName("owners")]
    public IReadOnlyList<JsonNode> Owners { get; init; } = [];

    /// <summary>One answer per <c>catalog</c> entry of the request, in request order.</summary>
    [JsonPropertyName("catalog")]
    public IReadOnlyList<JsonNode> Catalog { get; init; } = [];

    /// <summary>The bound form and the emitted stages; only with <c>include: ["plan"]</c> on a valid request.</summary>
    [JsonPropertyName("plan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExplainPlan? Plan { get; init; }

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
        Stages = [],
    };
}

/// <summary>The engine block of an explain answer.</summary>
public sealed record ExplainEngine
{
    /// <summary>The engine version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The contract the engine speaks (<see cref="EngineCapabilities.Contract"/>): 2 marks this package.</summary>
    [JsonPropertyName("contract")]
    public int Contract { get; init; } = EngineCapabilities.Contract;

    /// <summary>The capabilities, as <see cref="EngineCapabilities.Of"/> names them.</summary>
    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>The revisions an explain answer was bound against.</summary>
public sealed record ExplainRevision
{
    /// <summary>
    /// The revision of the schema document of each service the answer names a type of or asked, this
    /// host's included: a reader whose document of a service has another revision loads it again. Null
    /// for a service that publishes none.
    /// </summary>
    [JsonPropertyName("schema")]
    public IReadOnlyDictionary<string, string?> Schema { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>A hash of the asking organisation's addon definitions the answer read; null when it read none.</summary>
    [JsonPropertyName("addons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Addons { get; init; }
}

/// <summary>How an explain answer may be kept.</summary>
public sealed record ExplainCache
{
    /// <summary>The seconds the answer may be kept while its etag is not checked.</summary>
    [JsonPropertyName("maxAge")]
    public int MaxAge { get; init; } = 30;

    /// <summary>The owner services the answer depends on, transitive ones included, ordinally.</summary>
    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>
    /// False when something the answer would say is missing: an owner did not answer, a limit was hit
    /// (<c>EXPLAIN_LIMIT</c>) or the answer was trimmed (<c>EXPLAIN_TRIMMED</c>). Such an answer is not kept as complete.
    /// </summary>
    [JsonPropertyName("complete")]
    public bool Complete { get; init; } = true;
}

/// <summary>The shape before the first stage.</summary>
public sealed record ExplainEntry
{
    /// <summary>The entry shape.</summary>
    [JsonPropertyName("shape")]
    public required ExplainShape Shape { get; init; }
}

/// <summary>One caller stage as explain bound it.</summary>
public sealed record ExplainStage
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

    /// <summary>Where a join stage runs; absent for a stage that is no join, a join nothing reads or shows, and a request that does not bind.</summary>
    [JsonPropertyName("placement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExplainPlacement? Placement { get; init; }

    /// <summary>
    /// The paths the stage reads off the row, <c>{ path, use, alias? }</c> each, from the binder's read
    /// ledger (improvement plan §3.S): <c>use</c> one of <c>match, sort, project, unwind, groupKey,
    /// aggregate, resolveKey, caseCondition, lookupOn</c>; <c>alias</c> the join whose rows hold the
    /// path, which loads it. A stage continued at an owner carries the reads its owner answered.
    /// </summary>
    [JsonPropertyName("reads")]
    public IReadOnlyList<JsonNode> Reads { get; init; } = [];

    /// <summary>The aliases the stage adds to the shape, by name (<see cref="ExplainResult.Aliases"/>); empty for a stage that adds none.</summary>
    [JsonPropertyName("creates")]
    public required IReadOnlyList<string> Creates { get; init; }

    /// <summary>The shape after the stage; null when the shape was not asked for.</summary>
    [JsonPropertyName("shape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExplainShape? Shape { get; init; }
}

/// <summary>Where a join stage runs.</summary>
public sealed record ExplainPlacement
{
    /// <summary><c>inline</c>, <c>keyed-local</c>, <c>keyed-remote</c> or <c>continued</c>.</summary>
    [JsonPropertyName("executor")]
    public required string Executor { get; init; }

    /// <summary><c>beforePage</c>, <c>afterPage</c> or <c>owner</c>.</summary>
    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>The service whose engine runs the stage: this host's, or the owner's of a keyed-remote or continued stage.</summary>
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Host { get; init; }

    /// <summary>For a keyed or continued stage: the index of its owner in <see cref="ExplainResult.Owners"/> (the first remote target's, else the first); absent elsewhere.</summary>
    [JsonPropertyName("owner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Owner { get; init; }
}

/// <summary>What the rows look like at one point of the pipeline.</summary>
public sealed record ExplainShape
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

    /// <summary>The paths an inclusion projection kept, in ordinal order; absent while no inclusion projection ran.</summary>
    [JsonPropertyName("projection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Projection { get; init; }

    /// <summary>
    /// The roots the row carries, by name (<c>""</c> the entity itself), each pointing to its type:
    /// <c>t:&lt;entity&gt;[#item]</c>, <c>u:&lt;alias&gt;</c>, <c>k:&lt;kind&gt;</c> for a scalar, or null for an
    /// alias whose owner did not answer.
    /// </summary>
    [JsonPropertyName("roots")]
    public required JsonObject Roots { get; init; }

    /// <summary>
    /// The paths that left the row and are not named by <see cref="Projection"/>: what an exclusion
    /// projection removed and the collections an unwind with <c>keepPath: false</c> took out, as wire
    /// paths in ordinal order. A path at or under one is not in the row. Absent when there is none.
    /// </summary>
    [JsonPropertyName("removed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Removed { get; init; }

    /// <summary>
    /// Per root whose members stand differently here than in their type, or that is no row of a type,
    /// the rule (<c>r:n</c>) in <see cref="ExplainResult.Rules"/>; the members of a root without an entry
    /// have their own flags.
    /// </summary>
    [JsonPropertyName("rules")]
    public required JsonObject Rules { get; init; }

    /// <summary>
    /// Only with <c>include: "types"</c>: per root whose members differ here from their type's own flags,
    /// the override set (<c>o:n</c>) in <see cref="ExplainResult.FlagSets"/>; a root without an entry has its type's flags.
    /// </summary>
    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Flags { get; init; }
}

/// <summary>The final shape of the query.</summary>
public sealed record ExplainShapeResult
{
    /// <summary>As <see cref="ExplainShape.Paging"/>, for the final shape.</summary>
    [JsonPropertyName("paging")]
    public required string Paging { get; init; }

    /// <summary>The final shape's visible members and roots. The outcomes a join may have are its alias's (<c>aliases.*.outcome</c>).</summary>
    [JsonPropertyName("columns")]
    public required IReadOnlyList<ExplainColumn> Columns { get; init; }
}

/// <summary>One visible member or root of the final shape.</summary>
public sealed record ExplainColumn
{
    /// <summary>The key is on every row; its value may be null.</summary>
    public const string Always = "always";

    /// <summary>The key is absent on a row whose join found nothing (the alias is null there).</summary>
    public const string IfJoined = "ifJoined";

    /// <summary>The key is on the rows of the variants that have the member only.</summary>
    public const string IfVariant = "ifVariant";

    /// <summary>The key is absent on a row whose stored record does not hold the member, or whose parent object is null.</summary>
    public const string IfStored = "ifStored";

    /// <summary>The wire path.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The kind.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Whether the value may be null.</summary>
    [JsonPropertyName("nullable")]
    public required bool Nullable { get; init; }

    /// <summary>The stage that created the member; absent for a member of the entry shape.</summary>
    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Stage { get; init; }

    /// <summary>The root the member lies under, <c>""</c> for the entity itself.</summary>
    [JsonPropertyName("root")]
    public required string Root { get; init; }

    /// <summary>When a row carries the key: <see cref="Always"/>, <see cref="IfJoined"/>, <see cref="IfVariant"/> or <see cref="IfStored"/>.</summary>
    [JsonPropertyName("present")]
    public string Present { get; init; } = Always;
}

/// <summary>The plan of a valid request (<c>include: ["plan"]</c>).</summary>
public sealed record ExplainPlan
{
    /// <summary>The bound pipeline in canonical form (storage names).</summary>
    [JsonPropertyName("bound")]
    public required JsonNode Bound { get; init; }

    /// <summary>The page pipeline's stages.</summary>
    [JsonPropertyName("stages")]
    public required IReadOnlyList<JsonNode> Stages { get; init; }

    /// <summary>The count pipeline's stages, when a count was requested.</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonNode>? Count { get; init; }

    /// <summary>The collation both pipelines run under, when a string comparison, sort or group key folds case.</summary>
    [JsonPropertyName("collation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Collation { get; init; }
}
