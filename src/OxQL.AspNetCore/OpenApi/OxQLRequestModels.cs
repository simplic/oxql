using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OxQL.AspNetCore.OpenApi;

// The request bodies as they are written on the wire under contract 2, for an API description.
// Nothing is bound into these models: the engine reads a body with converters of its own, and
// what a member means, which values it takes and what is refused is the OxQL documentation's to
// say (OxQLDocumentation.QuerySyntax). A model here names the members and their JSON kinds.

/// <summary>A query: the entity it reads and the stages it reads it through.</summary>
public sealed class OxQLQueryBody
{
    /// <summary>The id of the entity, as the service's schema publishes it (`logistics.shipment`).</summary>
    [Required]
    [JsonPropertyName("entityType")]
    public string EntityType { get; init; } = "";

    /// <summary>The stages, executed as written. Each element is an object with exactly one stage.</summary>
    [Required]
    [JsonPropertyName("pipeline")]
    public IReadOnlyList<OxQLStage> Pipeline { get; init; } = [];

    /// <summary>Named values a condition refers to with `{ "$var": "name" }`.</summary>
    [JsonPropertyName("variables")]
    public IDictionary<string, object?>? Variables { get; init; }

    /// <summary>Conditions the engine places where the pipeline has the paths they read; a row must meet all of them.</summary>
    [JsonPropertyName("filter")]
    public IReadOnlyList<OxQLFilterEntry>? Filter { get; init; }

    /// <summary>Refuses the request (422) where it would otherwise answer with less data than was asked for.</summary>
    [JsonPropertyName("strict")]
    public bool? Strict { get; init; }

    /// <summary>What a join does with a reference whose target is missing, unless the join says so itself: `null`, `report` or `refuse`.</summary>
    [JsonPropertyName("onMissing")]
    public string? OnMissing { get; init; }

    /// <summary>Asks for the `timing` member of the answer.</summary>
    [JsonPropertyName("timing")]
    public bool? Timing { get; init; }

    /// <summary>A ceiling on this request's time in milliseconds, under the host's own.</summary>
    [JsonPropertyName("maxTimeMs")]
    public int? MaxTimeMs { get; init; }
}

/// <summary>One entry of a request's `filter`.</summary>
public sealed class OxQLFilterEntry
{
    /// <summary>A name of the caller's choice; errors and diagnostics of the entry carry it.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>A condition, as a `match` stage takes it.</summary>
    [Required]
    [JsonPropertyName("where")]
    public IDictionary<string, object?> Where { get; init; } = new Dictionary<string, object?>();
}

/// <summary>One stage of a pipeline: exactly one of the members is written.</summary>
public sealed class OxQLStage
{
    /// <summary>A condition on the rows: paths with operators, nested with `and`, `or`, `not` and `any`.</summary>
    [JsonPropertyName("match")]
    public IDictionary<string, object?>? Match { get; init; }

    /// <summary>A join to the records that reference the row.</summary>
    [JsonPropertyName("lookup")]
    public OxQLLookupStage? Lookup { get; init; }

    /// <summary>A join to the record a reference of the row names.</summary>
    [JsonPropertyName("resolve")]
    public OxQLResolveStage? Resolve { get; init; }

    /// <summary>One row per element of a collection.</summary>
    [JsonPropertyName("unwind")]
    public OxQLUnwindStage? Unwind { get; init; }

    /// <summary>One row per group of rows, with aggregates.</summary>
    [JsonPropertyName("group")]
    public OxQLGroupStage? Group { get; init; }

    /// <summary>The members a row keeps (`1`) or leaves out (`0`), by path; a nested object is a dotted path.</summary>
    [JsonPropertyName("project")]
    public IDictionary<string, object?>? Project { get; init; }

    /// <summary>The order of the rows: objects of one path each, with `asc` or `desc`.</summary>
    [JsonPropertyName("sort")]
    public IReadOnlyList<IDictionary<string, object?>>? Sort { get; init; }

    /// <summary>The page to answer. Once, last.</summary>
    [JsonPropertyName("page")]
    public OxQLPageStage? Page { get; init; }

    /// <summary>The distinct values of one path, in place of a page. Once, last.</summary>
    [JsonPropertyName("values")]
    public OxQLValuesStage? Values { get; init; }
}

/// <summary>The `page` stage.</summary>
public sealed class OxQLPageStage
{
    /// <summary>The most rows of the page; the host's default when absent. `0` with `count` asks for the count alone.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>The `nextCursor` of the page before, to continue after it.</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }

    /// <summary>Whether to count the matching rows: `true`, `false`, or an object `{ "upTo": n, "budgetMs": ms }` that bounds the count.</summary>
    [JsonPropertyName("count")]
    public object? Count { get; init; }
}

/// <summary>The `lookup` stage.</summary>
public sealed class OxQLLookupStage
{
    /// <summary>The id of the entity whose records reference the row.</summary>
    [Required]
    [JsonPropertyName("from")]
    public string From { get; init; } = "";

    /// <summary>The member of that entity that holds the reference.</summary>
    [Required]
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>The name the joined records stand under in the row.</summary>
    [Required]
    [JsonPropertyName("as")]
    public string As { get; init; } = "";

    /// <summary>The alias of an earlier join whose rows are the parents, instead of the row itself.</summary>
    [JsonPropertyName("on")]
    public string? On { get; init; }

    /// <summary>A condition the joined records must meet.</summary>
    [JsonPropertyName("filter")]
    public IDictionary<string, object?>? Filter { get; init; }

    /// <summary>The order of the joined records, as a `sort` stage takes it.</summary>
    [JsonPropertyName("sort")]
    public IReadOnlyList<IDictionary<string, object?>>? Sort { get; init; }

    /// <summary>The most records joined per row.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>Joins the first record as an object instead of an array.</summary>
    [JsonPropertyName("first")]
    public bool? First { get; init; }

    /// <summary>The target of a reference with several targets that this stage continues under.</summary>
    [JsonPropertyName("forTarget")]
    public string? ForTarget { get; init; }

    /// <summary>The name the parent of a joined item stands under.</summary>
    [JsonPropertyName("parentAs")]
    public string? ParentAs { get; init; }
}

/// <summary>The `resolve` stage.</summary>
public sealed class OxQLResolveStage
{
    /// <summary>The member of the row that holds the reference.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The name the joined record stands under in the row.</summary>
    [Required]
    [JsonPropertyName("as")]
    public string As { get; init; } = "";

    /// <summary>A condition the target must meet.</summary>
    [JsonPropertyName("filter")]
    public IDictionary<string, object?>? Filter { get; init; }

    /// <summary>Through a collection: `first` joins one target, `all` one per element.</summary>
    [JsonPropertyName("elements")]
    public string? Elements { get; init; }

    /// <summary>The target to join, of a reference that declares several.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>The name the parent of a joined item stands under.</summary>
    [JsonPropertyName("parentAs")]
    public string? ParentAs { get; init; }

    /// <summary>What to do with a reference whose target is missing: `null`, `report` or `refuse`.</summary>
    [JsonPropertyName("onMissing")]
    public string? OnMissing { get; init; }

    /// <summary>The target of a reference with several targets that this stage continues under.</summary>
    [JsonPropertyName("forTarget")]
    public string? ForTarget { get; init; }

    /// <summary>One path per target, for a join that continues under a reference with several targets.</summary>
    [JsonPropertyName("byTarget")]
    public IDictionary<string, object?>? ByTarget { get; init; }

    /// <summary>The name of a member that says how the join ended for the row.</summary>
    [JsonPropertyName("outcomeAs")]
    public string? OutcomeAs { get; init; }
}

/// <summary>The `unwind` stage.</summary>
public sealed class OxQLUnwindStage
{
    /// <summary>The collection whose elements become rows.</summary>
    [Required]
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>The name the element stands under in the row.</summary>
    [Required]
    [JsonPropertyName("as")]
    public string As { get; init; } = "";

    /// <summary>Keeps a row whose collection is empty or absent, once, without an element.</summary>
    [JsonPropertyName("preserveNull")]
    public bool? PreserveNull { get; init; }

    /// <summary>The name of a member that holds the element's position in the collection.</summary>
    [JsonPropertyName("includeIndex")]
    public string? IncludeIndex { get; init; }

    /// <summary>A member of the element that nests elements of the same kind: they become rows too.</summary>
    [JsonPropertyName("flatten")]
    public string? Flatten { get; init; }
}

/// <summary>The `group` stage.</summary>
public sealed class OxQLGroupStage
{
    /// <summary>The keys of a group; empty groups all rows into one.</summary>
    [JsonPropertyName("by")]
    public IReadOnlyList<OxQLGroupKey>? By { get; init; }

    /// <summary>The aggregates by the name they are answered under: each an object of one function, such as `{ "count": true }` or `{ "sum": "amount" }`.</summary>
    [JsonPropertyName("fields")]
    public IDictionary<string, IDictionary<string, object?>>? Fields { get; init; }
}

/// <summary>One key of a `group`: a path, or a date truncated to a unit.</summary>
public sealed class OxQLGroupKey
{
    /// <summary>The member whose value is the key.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>A date member truncated to a unit, in place of `path`.</summary>
    [JsonPropertyName("dateTrunc")]
    public OxQLDateTrunc? DateTrunc { get; init; }

    /// <summary>The name the key is answered under.</summary>
    [Required]
    [JsonPropertyName("as")]
    public string As { get; init; } = "";
}

/// <summary>A date truncated to a unit in a time zone.</summary>
public sealed class OxQLDateTrunc
{
    /// <summary>The `date` or `dateTime` member.</summary>
    [Required]
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>`year`, `quarter`, `month`, `week`, `day`, `hour`, `minute` or `second`.</summary>
    [Required]
    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "";

    /// <summary>An IANA time zone id; UTC when absent.</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    /// <summary>The first day of a week; Monday when absent.</summary>
    [JsonPropertyName("weekStart")]
    public string? WeekStart { get; init; }
}

/// <summary>The `values` stage.</summary>
public sealed class OxQLValuesStage
{
    /// <summary>The path whose distinct values are answered.</summary>
    [Required]
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>Keeps the values that start with this text.</summary>
    [JsonPropertyName("search")]
    public string? Search { get; init; }

    /// <summary>The most values answered.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>Answers each value with the number of rows that hold it.</summary>
    [JsonPropertyName("counts")]
    public bool? Counts { get; init; }

    /// <summary>The most rows read for the list.</summary>
    [JsonPropertyName("scan")]
    public int? Scan { get; init; }
}

/// <summary>The body of a batch: several queries, answered in order.</summary>
public sealed class OxQLBatchBody
{
    /// <summary>The queries.</summary>
    [Required]
    [JsonPropertyName("queries")]
    public IReadOnlyList<OxQLQueryBody> Queries { get; init; } = [];

    /// <summary>A ceiling on the whole batch in milliseconds.</summary>
    [JsonPropertyName("maxTimeMs")]
    public int? MaxTimeMs { get; init; }
}

/// <summary>The body of an explain in its envelope form. A plain query (a body with `entityType` at the top) is accepted as well and is this envelope with its defaults.</summary>
public sealed class OxQLExplainBody
{
    /// <summary>The query to explain.</summary>
    [Required]
    [JsonPropertyName("query")]
    public OxQLQueryBody Query { get; init; } = new();

    /// <summary>What the answer carries beyond the verdict: any of `shape`, `notes`, `types`, `docs`, `plan`, `indexes`.</summary>
    [JsonPropertyName("include")]
    public IReadOnlyList<string>? Include { get; init; }

    /// <summary>How deep the member rows of the types go.</summary>
    [JsonPropertyName("shape")]
    public OxQLExplainShape? Shape { get; init; }

    /// <summary>`check` asks the other services a join reaches; `cached` answers from what is already known.</summary>
    [JsonPropertyName("remote")]
    public string? Remote { get; init; }

    /// <summary>Entities to describe beside the query: objects of `id`, `entity` and optional `prefix`, `depth`, `referencing`.</summary>
    [JsonPropertyName("catalog")]
    public IReadOnlyList<IDictionary<string, object?>>? Catalog { get; init; }
}

/// <summary>The `shape` member of an explain envelope.</summary>
public sealed class OxQLExplainShape
{
    /// <summary>The levels of member rows, from 1.</summary>
    [JsonPropertyName("depth")]
    public int? Depth { get; init; }
}

/// <summary>
/// A contract 1 query (legacy): what a caller without the contract header writes. The entity and
/// the paths are written as they are stored, and the stages take the members of that contract.
/// New callers write <see cref="OxQLQueryBody"/> and send the header.
/// </summary>
public sealed class OxQLContract1QueryBody
{
    /// <summary>The entity, as the first contract named it.</summary>
    [Required]
    [JsonPropertyName("entityType")]
    public string EntityType { get; init; } = "";

    /// <summary>Named values a condition refers to.</summary>
    [JsonPropertyName("variables")]
    public IDictionary<string, object?>? Variables { get; init; }

    /// <summary>The stages of the first contract, each an object with one stage.</summary>
    [Required]
    [JsonPropertyName("pipeline")]
    public IReadOnlyList<IDictionary<string, object?>> Pipeline { get; init; } = [];
}
