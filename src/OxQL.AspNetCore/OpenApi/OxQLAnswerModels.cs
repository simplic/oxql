using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OxQL.AspNetCore.OpenApi;

// The answers as they are written on the wire, for an API description. The engine writes its
// answers from its own types; these models name the members a caller reads and their JSON kinds.

/// <summary>The answer to a query under contract 2: a page of rows and what is known of their count.</summary>
public sealed class OxQLQueryAnswer
{
    /// <summary>The rows, each an object in the wire encoding of the entity (or `{ value, count }` for a `values` stage).</summary>
    [Required]
    [JsonPropertyName("items")]
    public IReadOnlyList<IDictionary<string, object?>> Items { get; init; } = [];

    /// <summary>Whether a page follows, the cursor to it, and the count.</summary>
    [Required]
    [JsonPropertyName("pageInfo")]
    public OxQLPageInfo PageInfo { get; init; } = new();

    /// <summary>What the caller should know about the rows that did not refuse the request; absent when there is nothing.</summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<OxQLDiagnostic>? Diagnostics { get; init; }

    /// <summary>Where the request's time went; only when the request asked for it.</summary>
    [JsonPropertyName("timing")]
    public IDictionary<string, object?>? Timing { get; init; }
}

/// <summary>The paging metadata of a contract 2 answer.</summary>
public sealed class OxQLPageInfo
{
    /// <summary>Whether a page follows this one.</summary>
    [Required]
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; init; }

    /// <summary>The cursor of the next page, to send as `page.cursor`; absent on the last page.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    /// <summary>What the answer knows of how many rows match.</summary>
    [Required]
    [JsonPropertyName("count")]
    public OxQLCount Count { get; init; } = new();
}

/// <summary>What an answer knows of how many rows match.</summary>
public sealed class OxQLCount
{
    /// <summary>`exact`: `value` is the count. `atLeast`: `value` is a proven lower bound. `unknown`: the count that was asked for did not finish.</summary>
    [Required]
    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    /// <summary>The count, or the lower bound; absent when unknown.</summary>
    [JsonPropertyName("value")]
    public long? Value { get; init; }

    /// <summary>With `atLeast`, why counting stopped: `page`, `step`, `cap`, `budget` or `time`.</summary>
    [JsonPropertyName("stoppedBy")]
    public string? StoppedBy { get; init; }

    /// <summary>With `unknown`, why: `timeout` or `failed`.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The time the count took in milliseconds, when one was waited for.</summary>
    [JsonPropertyName("ms")]
    public long? Ms { get; init; }
}

/// <summary>Something a caller should know about an answer that did not refuse the request.</summary>
public sealed class OxQLDiagnostic
{
    /// <summary>The code; codes are stable, messages are not.</summary>
    [Required]
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    /// <summary>A message for a person.</summary>
    [Required]
    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    /// <summary>The index of the stage of the pipeline it belongs to.</summary>
    [JsonPropertyName("stage")]
    public int? Stage { get; init; }

    /// <summary>The path it is about.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The entry of the request's `filter` it belongs to: its `id`, or its index.</summary>
    [JsonPropertyName("filter")]
    public object? Filter { get; init; }

    /// <summary>Values to render a message from, by code.</summary>
    [JsonPropertyName("params")]
    public IDictionary<string, object?>? Params { get; init; }

    /// <summary>In an explain answer: what would have to change for it not to be said.</summary>
    [JsonPropertyName("basis")]
    public string? Basis { get; init; }
}

/// <summary>The answer to a batch: one entry per query, in order.</summary>
public sealed class OxQLBatchAnswer
{
    /// <summary>Per query its answer (an object with `items`) or its refusal (an object with `type` and `errors`).</summary>
    [Required]
    [JsonPropertyName("results")]
    public IReadOnlyList<IDictionary<string, object?>> Results { get; init; } = [];
}

/// <summary>A refusal: why a request was not answered.</summary>
public sealed class OxQLError
{
    /// <summary>The class of the refusal: `validation_error`, `access_denied`, `not_executable`, `timeout`, `rate_limited` or `internal_error`.</summary>
    [Required]
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>A summary for a person.</summary>
    [Required]
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    /// <summary>The reasons, each with a code.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<OxQLErrorDetail>? Errors { get; init; }

    /// <summary>The HTTP status repeated in the body; only in an answer to a contract 1 request (legacy).</summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }
}

/// <summary>One reason of a refusal.</summary>
public sealed class OxQLErrorDetail
{
    /// <summary>The code; codes are stable, messages are not.</summary>
    [Required]
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    /// <summary>A message for a person.</summary>
    [Required]
    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    /// <summary>The index of the stage of the pipeline the error belongs to.</summary>
    [JsonPropertyName("stage")]
    public int? Stage { get; init; }

    /// <summary>The entry of the request's `filter` the error belongs to: its `id`, or its index.</summary>
    [JsonPropertyName("filter")]
    public object? Filter { get; init; }

    /// <summary>The path of the entity the error is about.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Values to render a message from, by code.</summary>
    [JsonPropertyName("params")]
    public IDictionary<string, object?>? Params { get; init; }

    /// <summary>In an explain answer: what would have to change for the request to run.</summary>
    [JsonPropertyName("basis")]
    public string? Basis { get; init; }
}

/// <summary>The answer to an explain. The members that describe stages, types and plans are objects whose form is in the OxQL documentation.</summary>
public sealed class OxQLExplainAnswer
{
    /// <summary>Whether the query binds and this host can run it.</summary>
    [Required]
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    /// <summary>Whether the request can run: `yes`, `never` or `unknown`.</summary>
    [JsonPropertyName("executable")]
    public string? Executable { get; init; }

    /// <summary>The contract the request was read as.</summary>
    [Required]
    [JsonPropertyName("contract")]
    public int Contract { get; init; }

    /// <summary>The engine's version, contract and the capabilities of this host.</summary>
    [Required]
    [JsonPropertyName("engine")]
    public IDictionary<string, object?> Engine { get; init; } = new Dictionary<string, object?>();

    /// <summary>The validator of the answer, also sent as the `ETag` header.</summary>
    [JsonPropertyName("etag")]
    public string? Etag { get; init; }

    /// <summary>The revisions of the models the answer was bound against.</summary>
    [JsonPropertyName("revision")]
    public IDictionary<string, object?>? Revision { get; init; }

    /// <summary>How long the answer may be kept and whether it is complete.</summary>
    [JsonPropertyName("cache")]
    public IDictionary<string, object?>? Cache { get; init; }

    /// <summary>Every error of the query; empty when it is valid.</summary>
    [Required]
    [JsonPropertyName("errors")]
    public IReadOnlyList<OxQLErrorDetail> Errors { get; init; } = [];

    /// <summary>The diagnostics a run of the query would carry, as far as binding knows them.</summary>
    [Required]
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<OxQLDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Notes on how the engine reads the query.</summary>
    [Required]
    [JsonPropertyName("notes")]
    public IReadOnlyList<OxQLDiagnostic> Notes { get; init; } = [];

    /// <summary>The entity the query starts at.</summary>
    [JsonPropertyName("entry")]
    public IDictionary<string, object?>? Entry { get; init; }

    /// <summary>Per stage of the pipeline: its kind, its status, what it reads and the shape after it.</summary>
    [Required]
    [JsonPropertyName("stages")]
    public IReadOnlyList<IDictionary<string, object?>> Stages { get; init; } = [];

    /// <summary>Per entry of the request's `filter`: where it was placed.</summary>
    [JsonPropertyName("filter")]
    public IReadOnlyList<IDictionary<string, object?>>? Filter { get; init; }

    /// <summary>The aliases the joins and unwinds of the query create.</summary>
    [JsonPropertyName("aliases")]
    public IDictionary<string, object?>? Aliases { get; init; }

    /// <summary>The types the shapes refer to.</summary>
    [JsonPropertyName("types")]
    public IDictionary<string, object?>? Types { get; init; }

    /// <summary>The rules by which a member can be filtered, sorted, grouped or listed.</summary>
    [JsonPropertyName("rules")]
    public IDictionary<string, object?>? Rules { get; init; }

    /// <summary>The sets of such flags the types refer to.</summary>
    [JsonPropertyName("flagSets")]
    public IDictionary<string, object?>? FlagSets { get; init; }

    /// <summary>The columns of the rows a run answers.</summary>
    [JsonPropertyName("result")]
    public IDictionary<string, object?>? Result { get; init; }

    /// <summary>The flags of the result's columns.</summary>
    [JsonPropertyName("columnFlags")]
    public IDictionary<string, object?>? ColumnFlags { get; init; }

    /// <summary>The services asked for the parts of the query they own, and how each answered.</summary>
    [JsonPropertyName("owners")]
    public IReadOnlyList<IDictionary<string, object?>>? Owners { get; init; }

    /// <summary>The entries of the request's `catalog`, described.</summary>
    [JsonPropertyName("catalog")]
    public IReadOnlyList<IDictionary<string, object?>>? Catalog { get; init; }

    /// <summary>The bound form and the stages sent to the database; only with `include: ["plan"]`.</summary>
    [JsonPropertyName("plan")]
    public IDictionary<string, object?>? Plan { get; init; }

    /// <summary>What the indexes of the collection do for the query; only with `include: ["indexes"]`.</summary>
    [JsonPropertyName("advisory")]
    public IDictionary<string, object?>? Advisory { get; init; }
}

/// <summary>The answer of the health route. A caller without the contract header reads `status`, `service`, `engine`, `capabilities`, `limits` and, per service, `service`, `configured` and `reachable`.</summary>
public sealed class OxQLHealthAnswer
{
    /// <summary>`healthy`, or `degraded` while a service the model joins into is not usable or the model is degraded.</summary>
    [Required]
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>Always `oxql`.</summary>
    [Required]
    [JsonPropertyName("service")]
    public string Service { get; init; } = "";

    /// <summary>The engine this host runs.</summary>
    [Required]
    [JsonPropertyName("engine")]
    public OxQLHealthEngine Engine { get; init; } = new();

    /// <summary>What depends on the host: joins into other services, explain, contract 1.</summary>
    [Required]
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>The limits a request is checked against, by name, with the values in force on this host.</summary>
    [Required]
    [JsonPropertyName("limits")]
    public IDictionary<string, int> Limits { get; init; } = new Dictionary<string, int>();

    /// <summary>The services the host's model joins into; absent with `?shallow=true` and on a host without any.</summary>
    [JsonPropertyName("remote")]
    public IReadOnlyList<OxQLHealthService>? Remote { get; init; }

    /// <summary>The state of the host's entity model; contract 2 only.</summary>
    [JsonPropertyName("model")]
    public OxQLHealthModel? Model { get; init; }
}

/// <summary>The engine a host runs.</summary>
public sealed class OxQLHealthEngine
{
    /// <summary>The version of the engine's packages.</summary>
    [Required]
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary>The contract the engine speaks: `2`.</summary>
    [Required]
    [JsonPropertyName("contract")]
    public int Contract { get; init; }
}

/// <summary>A service the host's model joins into, as last measured.</summary>
public sealed class OxQLHealthService
{
    /// <summary>The name of the service.</summary>
    [Required]
    [JsonPropertyName("service")]
    public string Service { get; init; } = "";

    /// <summary>Whether the host has an address for it.</summary>
    [Required]
    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    /// <summary>Whether it answered the last measurement; null until the first has finished.</summary>
    [JsonPropertyName("reachable")]
    public bool? Reachable { get; init; }

    /// <summary>The contract its engine speaks; contract 2 only.</summary>
    [JsonPropertyName("contract")]
    public int? Contract { get; init; }

    /// <summary>The version of its engine; contract 2 only.</summary>
    [JsonPropertyName("engineVersion")]
    public string? EngineVersion { get; init; }

    /// <summary>Whether joins into it can run; contract 2 only.</summary>
    [JsonPropertyName("usable")]
    public bool? Usable { get; init; }

    /// <summary>Why it is not usable; contract 2 only.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>What the host's check of its declared references into the service says; contract 2 only.</summary>
    [JsonPropertyName("references")]
    public IDictionary<string, object?>? References { get; init; }
}

/// <summary>The state of a host's entity model.</summary>
public sealed class OxQLHealthModel
{
    /// <summary>How many entities the model has.</summary>
    [Required]
    [JsonPropertyName("entities")]
    public int Entities { get; init; }

    /// <summary>Whether the model is what a failed build left.</summary>
    [Required]
    [JsonPropertyName("degraded")]
    public bool Degraded { get; init; }

    /// <summary>Why it is degraded: `build`, `empty` or `unavailable`.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// The answer to a contract 1 query (legacy): what a caller without the contract header reads. The
/// rows are as they are stored. New callers send the header and read <see cref="OxQLQueryAnswer"/>.
/// </summary>
public sealed class OxQLContract1QueryAnswer
{
    /// <summary>The rows, as stored.</summary>
    [Required]
    [JsonPropertyName("items")]
    public IReadOnlyList<IDictionary<string, object?>> Items { get; init; } = [];

    /// <summary>The paging metadata of the first contract.</summary>
    [Required]
    [JsonPropertyName("pageInfo")]
    public OxQLContract1PageInfo PageInfo { get; init; } = new();

    /// <summary>What the caller should know about the rows.</summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<OxQLDiagnostic>? Diagnostics { get; init; }
}

/// <summary>The paging metadata of a contract 1 answer (legacy).</summary>
public sealed class OxQLContract1PageInfo
{
    /// <summary>Whether a page follows this one.</summary>
    [Required]
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; init; }

    /// <summary>The cursor of the next page.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    /// <summary>The count; only when `includeTotalCount` asked for it.</summary>
    [JsonPropertyName("totalCount")]
    public long? TotalCount { get; init; }

    /// <summary>Whether `totalCount` is the cap rather than the count.</summary>
    [JsonPropertyName("totalCountCapped")]
    public bool? TotalCountCapped { get; init; }
}
