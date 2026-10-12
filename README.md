# OxQL

A typed query engine over MongoDB for Simplic OxS services. A client posts a JSON pipeline
naming an entity; the engine binds every path and operand against the host's entity model,
applies the caller's organisation scope at every entry into an entity, compiles one
aggregate, executes it under guard rails, and answers rows in the same wire encoding the
service's REST responses use. This is **contract 2**. A request without the contract header is
contract 1 and keeps the answers it got, for as long as compatibility is on (see *Contract header
and compatibility mode*). What a host and a caller meet when they take the package is in
*Upgrading*.

Contract 2 has what reports need: polymorphic members and the `is`
operator, flattened item trees, typed, item and key-converting references, `lookup` with `sort`,
`first` and `on`, the outcome of every join with `onMissing` and `strict` and on the row under a
name (`outcomeAs`), chains across services by remote continuation, one alias across the targets of
a union (`byTarget`), and `POST /oxql/explain` as the one "all information about this query"
endpoint, on by default and never executing.

The full request syntax, the operand rules per kind, and the closed lists of error and
diagnostic codes are in [`src/docs/oxql-query-syntax.md`](src/docs/oxql-query-syntax.md).
What a request means (null and absent values, storage representations, case and accent
folding, default order and cursors, projections, group typing) is in
[`src/docs/oxql-semantics.md`](src/docs/oxql-semantics.md); the HTTP status of every code,
every limit with its cross-service reach, the health and explain answers and a smoke checklist
are in [`src/docs/oxql-operations.md`](src/docs/oxql-operations.md).

## Packages

| package | contents |
|---|---|
| `OxQL.Model` | The entity model: every queryable entity with its wire view (what the schema publishes), its storage view (what the driver stores, read from the MongoDB serializer registry), declared references and the pooled type structure. Built by `ClrModelBuilder` from `[OxQLType]` assemblies or by `DocumentModelBuilder` from an Ox Schema document. Also the addon definition contract (`AddonDefinition`, `IAddonDefinitionSource`). |
| `OxQL.Core` | The request models, the binder (paths, operands, conditions, the shape fold through the pipeline), the signed cursor codec, the options (`OxQLOptions`), the engine and host contracts (`IQueryEngine`, `IEntityModelProvider`, `IRemoteQueryClient`). |
| `OxQL.Mongo` | The MongoDB engine: compiler, executor, wire encoder, the keyed fetch (remote owners and this host in process, with the owner-fetch cache) that runs every join outside the aggregate and continues chains at their owners, explain with its static index advisory. |
| `OxQL.AspNetCore` | The controller (`POST /oxql/query`, `POST /oxql/batch`, `GET /oxql/health`, `POST /oxql/explain`), the query service with its internal-call overloads for a host's internal routes, contract detection and the contract 1 compatibility binder, the request-size cap, the scope-provider contract (`IOxQLScopeProvider`) and the startup checks. |
| `OxQL.Studio` | A slim developer console for one service, served at `/oxql` under the host's path base: one scratch query, Run, Explain, the health panel. The full studio is the Angular OxQL Studio. |

Dependency order: `OxQL.Model` ← `OxQL.Core` ← `OxQL.Mongo` ← `OxQL.AspNetCore`; `OxQL.Studio`
stands beside them and reads the engine options by name only.

## Registration

```csharp
// Every option of the engine, bound from the host's "OxQL" section (see the table below).
builder.Services.AddOxQLCore(builder.Configuration.GetSection("OxQL"));

// The MongoDB engine. The model is built from the scanned assemblies on the first request
// unless the host registers an IEntityModelProvider of its own (the Simplic base package does,
// from its schema build, so the engine binds against exactly the model /schema publishes).
builder.Services.AddOxQLMongo(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("MongoDB");
    options.DatabaseName = "my_service";
    options.IncludeErrorDetails = builder.Environment.IsDevelopment();
    options.ScanAssemblies(typeof(MyEntity).Assembly);
});

// The controller. RequireAuthorization decorates every action but the health probe.
builder.Services.AddOxQLAspNetCore(options => options.RequireAuthorization = true);

// The organisation scope: mandatory. The engine refuses to start without a provider and
// answers 403 ACCESS_DENIED to any request whose organisation is null.
builder.Services.AddOxQLScope<MyScopeProvider>();          // an IOxQLScopeProvider
// or: builder.Services.AddOxQLScope(httpContext => ...);   // from a delegate

// Optional: the developer console. EnableExplain defaults to true, like OxQL:Explain:Enabled.
builder.Services.AddOxQLStudio(options => options.StudioAppUrl = "/admin/dev/oxql-studio");

var app = builder.Build();
app.MapControllers();
app.MapOxQLStudio();
```

### Host contracts

| contract | who implements it | what it does |
|---|---|---|
| `IOxQLScopeProvider` (`OxQL.AspNetCore.Scope`) | the host, always | `OrganisationAsync(HttpContext?)` is the caller's organisation, applied as `organizationId eq <value>` at every entry into an entity; `UserId` and `CorrelationId` feed the log line and the remote forwarding. |
| `IEntityModelProvider` (`OxQL.Core.Engine`) | the host, optionally | The model the engine binds against. Default: `LazyEntityModelProvider` over the scanned assemblies, built on the first request. `ClrModelBuilder` must run after every serializer and class-map registration, never during `ConfigureServices`: the driver freezes a type's serializer on first lookup. A provider may say what its build left (`Degraded`: `true` after a failed build, `false` for the model the host meant to build, also one without entities); health publishes it as `model.degraded` and takes a model without entities for degraded while the provider does not say. |
| `IRemoteQueryClient` (`OxQL.Core.Engine`) | the host, when entities reference entities of other services | `BatchAsync(serviceKey, BatchRequest, budget, ct)` sends a batch to the owner's internal batch route, `IsConfigured` says whether the host knows the service, `IsReachableAsync` feeds `/health`. `ExplainBatchAsync(serviceKey, ExplainBatchRequest, budget, ct)` sends what one round of an explain asks one owner (its checks, with the time and owner calls left) as one call to the owner's internal explain, for explain's remote check and its catalog entries of the owner's entities; its default asks check by check through `ExplainAsync(serviceKey, ExplainRequest, budget, ct)`, whose default answers `null` (the parts are noted `REMOTE_UNCHECKED`). A client that also implements `IRemoteOwnerInfo` reports what each owner's shallow health said (engine version, `maxBatchQueries`), from which the engine splits batches. Without a client every remote resolve is refused with 422 `RESOLVE_UNAVAILABLE`. The service key is the target entity's namespace (`vehicle` of `vehicle.vehicle`). |
| `IOxQLQueryService` (`OxQL.AspNetCore`) | the engine; called by the host's internal routes | `BatchAsync(batch, internalCall: true, ct)` and `ExplainBatchAsync(batch, ct)` run a batch or the checks of one explain round as an owner call: only they (and `ExplainAsync(request, internalCall: true, ct)`, one check) admit the keyed fetch's `keyedBy`. A host that serves other services' remote resolves routes its internal batch and explain routes through them; implementers and fakes of the interface add the overloads they are called with. |
| `IAddonDefinitionSource` (`OxQL.Model.Addon`) | the host, when entities are extendable | An organisation's addon definitions per entity, read per request. Default: `EmptyAddonDefinitionSource` (every addon key is `unknown`: projectable, never filterable). A source that reads a database implements `IReportingAddonDefinitionSource` and says each wait to the call's `AddonReadReport`, so a request's timing counts it as database time (`db.list[]`, `kind: "addon"`) and not as the host's own. |

## The entity model

One immutable model per host, built once from every class carrying `[OxQLType("<service>.<entity>", "<collection>", Extendable = ...)]`:

- **Entity ids** are matched exactly and case-sensitively (`UNKNOWN_ENTITY` otherwise). A retired id declared by the host (`ClrModelBuilder.Build(assemblies, retiredIds)`) is answered as the current entity with an `ENTITY_ID_RETIRED` diagnostic carrying `params.currentId`.
- **Two views.** The wire view is every public readable property, camelCase, `id` at every depth. The storage view comes from the MongoDB driver's serializer registry, so `[BsonElement]`, `[BsonId]`, `[BsonIgnore]`, `[BsonRepresentation]` and the `Id → _id` convention are observed, not inferred. A member the driver does not store is refused with `NOT_STORED`; a member whose serializer is not a document serializer (a GeoJSON point, an interface) is `unknown`: projectable, not filterable or sortable.
- **Kinds:** `string int long double decimal bool guid date dateTime timeSpan enum binary object array dictionary unknown`.
- **Polymorphic members.** A member typed as a base class or interface whose subtypes are registered class maps publishes the base's members plus every member of its variants, each marked `onlyFor` its variants; rows render the stored variant's members, and `is` filters by variant. A polymorphic entity keeps its declared class as the root. See [`oxql-semantics.md`](src/docs/oxql-semantics.md#polymorphic-values).
- **Descriptions** come from `[OxQLDescription]`, then `[Description]`, then the XML `<summary>` of the documentation file beside the assembly (`<inheritdoc/>` resolved; the service sets `GenerateDocumentationFile`). They are normalised: a leading "Gets or sets", "Gets" or "Represents" is dropped and the rest kept as written (the article stays: "Gets or sets the billing line" becomes "The billing line"), the first letter capitalised, `<see cref>` rendered as the simple name, paragraphs separated by a blank line, whitespace collapsed, at most 500 characters cut at a word boundary. `[Obsolete]` publishes `deprecated`, `[MaxLength]`/`[StringLength]`, `[Range]` and `[RegularExpression]` publish `constraints`. None of this enters the model fingerprint.
- **References** are declarations only, never name inference: `[OxQLReference("<entity id>", field?)]` (`OxQL.Model.Attributes`) on the id member, or the base package's `[ReferenceId("<idProperty>")]` on the navigation property, at any depth (a nested object or a collection element counts as much as a root member). `[OxQLReference]` takes `Item` (the key names an element of a keyed item collection) and `KeyAs = OxQLKeyAs.Guid` (a string member holding a guid); the repeatable `[OxQLReferenceWhen(path, equals, targets…)]` declares a typed reference whose target depends on a sibling's stored value or, with `OxQLReferenceWhenAttribute.Variant`, on the holding object's variant; and `ReferenceDeclarations` declares references the host cannot annotate, passed to `ClrModelBuilder.Build(assemblies, retiredIds, references)`. See [`oxql-semantics.md`](src/docs/oxql-semantics.md#references). A target on another host is a remote reference; the model marks it and the host must know the owner (`IRemoteQueryClient.IsConfigured`), otherwise the host logs an error and refuses to start in `Development`, `Local` and under continuous integration (the `CI` or `TF_BUILD` variable, read once at registration into `OxQLEndpointOptions.ContinuousIntegration`).
- **Addon bags.** An extendable entity carries an `addon` dictionary. A key the organisation has defined binds with the definition's kind and matches tolerantly across storage representations; an undefined, retired or `object` key is `unknown`.

## A request in one look

```jsonc
POST /oxql/query
X-OxQL-Contract: 2

{
  "entityType": "logistics.shipment",
  "variables": { "from": "2026-01-01T00:00:00Z" },
  "pipeline": [
    { "match": { "isDeleted": { "eq": false }, "createdAt": { "gte": { "$var": "from" } } } },
    { "resolve": { "path": "vehicleId", "as": "vehicle" } },
    { "project": { "id": 1, "number": 1, "vehicle.id": 1, "vehicle.matchCode": 1 } },
    { "sort": [{ "number": "asc" }] },
    { "page": { "limit": 50, "count": true } }
  ]
}
```

```jsonc
{
  "items": [ { "id": "…", "number": "S-1", "vehicle": { "id": "…", "matchCode": "V-1" } } ],
  "pageInfo": { "hasNextPage": true, "nextCursor": "…", "count": { "state": "exact", "value": 734 } }
}
```

Stages run as written, in the order `match lookup resolve unwind group project sort page`, and
every path is checked against the shape the previous stages produced. A refusal is a
`validation_error`, `access_denied`, `not_executable`, `timeout`, `rate_limited` or `internal_error` envelope
with a closed list of codes; every binding error is reported at once. Details, every stage
and every code: [`src/docs/oxql-query-syntax.md`](src/docs/oxql-query-syntax.md).

## Endpoints

| route | what |
|---|---|
| `POST /oxql/query` | One request; 200 with rows, or the refusal envelope with its status (400, 403, 413, 422, 504, 500). |
| `POST /oxql/batch` | `{ "queries": [ <request>, … ], "maxTimeMs"? }`; 200 with `{ "results": [ … ] }` in order, each entry a full success body or a refusal envelope. The batch itself is refused with 400: more than `Limits:MaxBatchQueries` queries (`BATCH_TOO_LARGE`), a member it does not have, a body without `queries`. Queries run sequentially on one host. |
| `GET /oxql/health` | Anonymous. `{ "status", "service": "oxql", "engine": { "version", "contract": 2 }, "capabilities": [ … ], "limits": { … }, "remote": [ { "service", "configured", "reachable", "contract"?, "engineVersion"?, "usable"?, "reason"? } ], "model": { "entities", "degraded", "reason"? } }`. `engine.contract` 2 is the marker of this package: every contract 2 construct and the explain answer. Capabilities, only what depends on the host: with a remote client `resolve.remote`, `semiJoin`, `resolve.chain`, `lookup.remote`; `explain` and `compat.v1` when enabled. The two lists are read per caller: a contract 1 caller reads what the legacy pipeline honours (`batch`, `compat.v1` and ten limits), and on a host that answers contract 1 by the rewrite first (`Compat:Legacy:Always=false`) `batch`, `group.page`, `page.offset`, `any` ahead of the host's own. `status` is `degraded` when a referenced service is not configured, did not answer when last measured, or answered without speaking contract 2 (`usable: false` with `reason` `notConfigured`, `unreachable` or `engine`; a contract 2 caller reads these and the owner's `contract` and `engineVersion`, a caller without the header the first three members), and when the host's entity model is what a failed build left or has no entity (`model.degraded`, contract 2). The answer never waits for another service: `reachable` is the last measurement, refreshed in the background at most once per `Cache:HealthProbeTtlSeconds`, and absent until the first one has finished. `?shallow=true` leaves `remote` out and starts no measurement; it is the form one host asks of another. |
| `POST /oxql/explain` | On by default; 404 while `Explain:Enabled` is false. Takes a query or the envelope `{ query, include?, shape?, remote?, catalog? }` and never executes it. Answers 200 with `valid`, every error, each stage with its placement and the shape of the row after it, the aliases, the types of the row's roots by reference to the services' schema documents (entity, service, revision) with a rule per root and stage that says where the members stand (`include: ["types"]` writes the member rows and their flags out for a caller without schema documents), the result columns, the owners asked, engine-behaviour notes, and on request the plan (`include: ["plan"]`: the canonical bound form, the emitted page and count stages, the collation, the owner queries) and the index advisory (`include: ["indexes"]`, which reads only `listIndexes`). Rate-limited per user (429 with `Retry-After`) and bounded in what one explain may ask of other services. See [`oxql-operations.md`](src/docs/oxql-operations.md#post-oxqlexplain). |

Every request body is capped at `Limits:MaxRequestBytes` (413 `REQUEST_TOO_LARGE`).

## Contract header and compatibility mode

Clients send `X-OxQL-Contract: 2`. While `Compat:Enabled` is true (the default) the header
decides the contract: absent or empty is contract 1, `1` and `2` are themselves, and any other
value is 400 `UNKNOWN_REQUEST_MEMBER` with `params.reason: "contract"`, on `query`, `batch` and
`explain`. With `Compat:Enabled` false the header is not read and every request is contract 2.

Two things can answer a contract 1 request:

- **The legacy pipeline (the default).** Contract 1 requests are answered by a pipeline ported
  from the 1.1 query path, so that existing callers get the answers they got: the stored
  documents under their stored names, the contract 1 envelope, cursors, count and refusals. The
  caller's organisation is matched before any stage of the request and in the filter of every
  `lookup`, and every database call carries a time limit. It is temporary:
  [`src/docs/LEGACY.md`](src/docs/LEGACY.md) says what it answers and guarantees, what a host can
  set and how it is removed. Every request it answers is logged under `OxQL.Compat.Legacy`, so a
  host can see who still calls without the header.
- **The compatibility rewrite** (`Compat:Legacy:Always=false`, the published 2.0 behaviour of
  contract 1): the request is rewritten into contract 2 spelling and run on the contract 2 engine
  (entity ids without regard to case, storage-spelled paths resolved against the model, the v1
  type-hint operands, rows in the v1 encoding); the legacy pipeline then answers only what the
  rewrite cannot express. Each request the rewrite handles is logged under `OxQL.Compat`. With
  `Compat:Legacy:Enabled=false` the rewrite answers alone and refuses what it cannot express
  (`LEGACY_STAGE_UNSUPPORTED`, `UNKNOWN_PATH`).

A missing header is the likeliest mistake when a contract 2 query is pasted into a report data
source. On the rewrite path every refusal a contract 1 request receives for a contract 2 construct
ends its message with "This request was read as contract 1 because it carries no
'X-OxQL-Contract: 2' header."; on the legacy pipeline such a request is read as contract 1
(members it does not know are ignored, paths in the model's spelling match nothing).

## Configuration (`OxQL` section)

Every `Limits` value is published on `GET /oxql/health`; the Simplic base package also puts the
fifteen a caller checks a request against before sending it into the schema document's `limits`.
Where each limit is enforced, and how it reaches calls between services, is in
[`src/docs/oxql-operations.md`](src/docs/oxql-operations.md#limits).

| key | default | notes |
|---|---|---|
| `Compat:Enabled` | `true` | contract 1 for requests without the header |
| `Compat:Legacy:Enabled` / `Always` / `OrganisationPath` / `AllowedLookupSources` / `MaxTimeMs` | `true` / `true` / `OrganizationId` / empty / 600 000 | the legacy pipeline of contract 1; see [`LEGACY.md`](src/docs/LEGACY.md#what-a-host-can-set) |
| `Explain:Enabled` | `true` | `POST /oxql/explain` answers |
| `Explain:RemoteTimeoutMs` / `TimeoutMs` | 1 500 / 2 000 | explain's wait for owners in all / the wall time of one explain |
| `Explain:DefaultShapeDepth` / `MaxTypeMembers` / `MaxAnswerBytes` | 2 / 300 / 262 144 | with `include: ["types"]`, levels of member rows listed / member rows per type; the size an answer is trimmed to |
| `Explain:MaxOwnerServices` / `MaxOwnerCalls` | 4 / 8 | owner services one explain asks / owner calls it causes in all |
| `Explain:RatePerMinute` / `RateBurst` / `MaxConcurrentPerUser` / `MaxConcurrentPerHost` / `MaxConcurrentPerCaller` | 120 / 30 / 2 / 8 / 4 | the rate and concurrency of explain, per user, per host and per calling service; more is 429 |
| `Explain:MaxRequestBytes` / `MaxStages` / `MaxCatalogEntries` / `MaxShapeDepth` / `MaxBatchChecks` | 65 536 / 30 / 10 / 3 / 64 | the largest explain body (413 before it is read) / the stages an explained query may carry / the `catalog` entries and the `shape.depth` one explain may ask for (400 `EXPLAIN_LIMIT`) / the checks one internal explain carries |
| `Explain:OwnerRatePerMinute` / `OwnerRateBurst` | 20 / 5 | the explains of one user that may call owners, charged at the first owner call; over it an explain is answered from what is kept (`cache.complete: false`, note `EXPLAIN_LIMIT` `ownerRate`), never 429 |
| `Limits:MaxPageSize` / `DefaultPageSize` | 500 / 100 | the page a request may ask for / gets without a limit (`DefaultPageSize` is clamped to `MaxPageSize`) |
| `Limits:MaxPipelineStages` | 20 | caller stages; the engine's scope stage does not count |
| `Limits:MaxLookupStages` / `MaxUnwindStages` / `MaxResolveStages` | 5 / 5 / 8 | resolve stages bound on this host; continued stages count at their owner |
| `Limits:MaxContinuedStages` | 8 | stages continued under one keyed or remote alias; clamped to `MaxPipelineStages` |
| `Limits:MaxFlattenDepth` | 5 | levels an `unwind` with `flatten` descends; clamped to 1–12 |
| `Limits:MaxReportPageSize` / `MaxReportedRows` | 5 000 / 50 | the page of a `strict` request without cursor or offset / rows one outcome diagnostic lists |
| `Limits:MaxGroupFields` / `MaxProjectionFields` | 40 / 500 | keys and aggregates of one `group` together / paths of one projection |
| `Limits:MaxValuesLimit` / `Values:DefaultLimit` / `Values:ScanRows` | 500 / 100 / 50 000 | of a `values` stage (the distinct values of a path, a set filter's list): the most values answered / those answered without a `limit` / the most rows read for them, after which the answer says `VALUES_SCAN_CAPPED` |
| `Limits:MaxConditions` / `MaxVariables` | 200 / 64 | leaf conditions, lookup and resolve filters included |
| `Limits:MaxFilterEntries` | 64 | entries the `filter` of one contract 2 request carries (`MAX_FILTER_ENTRIES_EXCEEDED`) |
| `Count:StepRows` / `BudgetMs` / `MaxBudgetMs` | 1 000 / 500 / 5 000 | the count step beside a contract 2 page: the rows it counts before it stops (above them the count is a lower bound) / the time it has / the longest a request may ask for with `page.count.budgetMs` |
| `Limits:MaxOffset` | 5 000 | the largest `offset` (contract 1 on the rewrite path and internal calls; a contract 2 page has a cursor only) |
| `Limits:CountCap` | 100 000 | above it `totalCount` is the cap and `totalCountCapped` true (contract 1 on the rewrite path and internal calls); the ceiling of a contract 2 count |
| `Limits:MaxSemiJoinIds` | 5 000 | a larger semi-join is refused, never truncated; never above `MaxOffset`, which clamps it (the ids are read from the owner by offset), and at least 1 |
| `Limits:ResolveKeyChunk` / `MaxResolveKeys` | 500 / 10 000 | keys per owner query (never above `MaxPageSize`, which clamps it) / keys asked of owners per request, over every keyed stage |
| `Limits:MaxRequestBytes` / `MaxBatchQueries` / `RegexMaxLength` | 262 144 / 10 / 200 | |
| `Limits:MaxLookupLimit` | 100 | rows one lookup returns per parent |
| `Execution:MaxTimeMs` | 10 000 | `maxTimeMS` on every aggregate, clamped to 60 000; a timeout is 504 `QUERY_TIMEOUT` |
| `Execution:ResolveTimeoutMs` | 2 000 | budget of one plain remote resolve call, clamped to the effective `MaxTimeMs` |
| `Execution:ChainTimeoutMs` | 6 000 | budget of one owner call carrying continued stages or a typed, item, converted or element-wise resolve (the time left less 50 ms, at most this), clamped to the effective `MaxTimeMs` |
| `Execution:BatchConcurrency` | 4 | queries of one internal batch that run at once (1 to 16); a public batch runs one after another |
| `Execution:AllowDiskUse` | `true` | a sort or group over the server's memory limit spills to disk and finishes slowly; when `false`, it is 422 `QUERY_TOO_EXPENSIVE`; unset in code (`null`) leaves the server's default |
| `Execution:TimingDetail` | `true` | whether a contract 2 caller may ask for the breakdown of a request's time (the `timing` member: entities read, documents and bytes per command, owners asked, build); `false` answers none, whoever asks; `Server-Timing` is sent either way |
| `Execution:SlowQueryMs` | 1 000 | a request whose whole time (binding, aggregates, count, remote resolves) exceeds it is logged at warning level with the entity, the stage kinds, whether a regex, an unbounded sort, a count or a remote resolve was involved, the duration and the row count — never an operand; `0` turns the line off |
| `Representation:GuidTolerant` | `false` | also match legacy subtype 3 and string guids |
| `Representation:DecimalMode` | `tolerant` | match Decimal128 and string decimals; `typed` after a migration |
| `Representation:Collation:Locale` / `Strength` | `de` / `1` | the collation a contract 2 string comparison, sort and group key folds under; strength 1 folds case and accents, 2 case only, 3 and above tell both apart (clamped to 1–5; an empty locale falls back to `de`) |
| `Cache:ResolveTtlSeconds` / `OwnerFetchCacheMaxEntries` | 60 / 50 000 | resolved rows of the keyed fetch and, apart, semi-join ids |
| `Cache:NegativeResolveTtlSeconds` | 10 | how long a key an owner answered as not found is cached; a `strict` request reads past it; `0` caches none |
| `Cache:AddonDefinitionTtlSeconds` | 30 | a host's addon definition cache |
| `Cache:HealthProbeTtlSeconds` | 10 | how long `/oxql/health` reuses the last reachability measurement |
| `Cache:OwnerUnavailableTtlSeconds` | 5 | how long an owner that did not answer (no connection, no answer within its call budget, HTTP 404 on the internal query route) is not called again: its aliases are `null` with `RESOLVE_UNREACHABLE` (`params.remembered`) at once instead of after the call budget; `0` remembers nothing |
| `Cursor:SigningKey` | — | required; the host does not start without it; cursors are HMAC-signed with a key derived from it |

The flat 1.x key names `MaxPageSize`, `DefaultPageSize`, `MaxPipelineStages`, `MaxLookupStages`,
`MaxUnwindStages`, `MaxGroupFields`, `MaxProjectionFields` and `RegexMaxLength` are still bound,
directly under `OxQL`, onto the same `Limits` values. A limit set above the one it depends on is
clamped at registration, and the adjustment is logged as a warning when the engine is built.

The Mongo connection (`ConnectionString`, `DatabaseName`, `IncludeErrorDetails`,
`ScanAssemblies`) is configured on `AddOxQLMongo`, not in the section.

## Guard rails

Every aggregate runs with `maxTimeMS`; every cap above has a code; no JavaScript ever reaches
Mongo; a `regex` operand passes a static check (no nested quantifiers, no backreferences) and
the length cap, and an unanchored pattern is diagnosed as `REGEX_UNANCHORED`. The
organisation scope is one typed equality on one indexed field on every query and inside every
`lookup` and local `resolve` sub-pipeline; a keyed fetch's owner query, and every stage continued
in it, is executed by the owning service (or this host in process) under its own scope for the
forwarded organisation. Driver errors map to 504 `QUERY_TIMEOUT` (code 50), 422
`QUERY_TOO_EXPENSIVE` (code 292) and 500 `INTERNAL_ERROR` (details only with
`IncludeErrorDetails`).

Under contract 2 a string comparison, sort or group key folds case and accents under the
`Representation:Collation`. The aggregate carries the collation only when something folds; a
request over other kinds is emitted exactly as before. A comparison that opts out with
`caseSensitive` inside such an aggregate is an anchored pattern, which the collation does not
reach; `startsWith` under the collation is a range, `contains` and `endsWith` stay patterns
with the `i` flag and fold case only; the engine never inspects indexes, so every form is
correct on a collection with no index but `_id`. A join on a string key compares the key byte
for byte again inside the join, so ids never fold; a guid or binary key is untouched.

## Joins across services

`resolve` follows a declared reference. A simple reference onto a local entity compiles to an
indexed `$lookup` in the aggregate. Every other resolve runs as a **keyed fetch** after the page is
fixed: the engine collects the page's keys, asks each target's owner for them in ordinary OxQL
queries (per owning service one call to the owner's internal batch route through
`IRemoteQueryClient`, keys chunked by `Limits:ResolveKeyChunk`; a local target in process), and
assigns the answers per row. Answers are cached per target, organisation, key and the substituted
owner query for `Cache:ResolveTtlSeconds`. A `match` on a member of a plain remote alias is a
semi-join: the owner is asked for the matching ids first, and binds the condition under its own
default; `caseSensitive` travels to it as the caller wrote it (a contract 1 caller's `ignoreCase: true` as `caseSensitive: false`). Explain has the
owner bind that same query (never run it), so a condition the owner refuses is refused by explain
as by a run, with the owner's code where the caller wrote the condition.

A `resolve` or `lookup` on an alias another service returned is **continued**: it rides in the
query sent to that owner, which binds it with its own model and answers enriched rows, and so on
through a third service. The internal call gains no header: a forwarded query always carries fewer
join stages than the one that produced it, the time rides on the batch's `maxTimeMs`, `strict` is in
the body. The only new wire member is `keyedBy`, which groups an owner's answer per key (two
records at most, the second meaning `ambiguous`) and is accepted only on the internal route. Every
owner an origin reaches runs this package and is listed in `InternalHosts`: an owner needs the
internal routes (`internal/oxql/batch`, `internal/oxql/explain`). Any other service's entities
stay unknown or unavailable to origins (`UNKNOWN_ENTITY`, `RESOLVE_UNAVAILABLE`).

An owner refusal becomes the caller's 422 `RESOLVE_REFUSED` with the owner's errors mapped to the
caller's stages, paths and `filter` entries, as explain says them; a timeout or an unreachable owner yields `null` under the alias and a
`RESOLVE_TIMEOUT` / `RESOLVE_UNREACHABLE` diagnostic (the message says whether the owner answered
with an HTTP error or was not reached at all); a semi-join whose owner does not answer is 422
`RESOLVE_UNAVAILABLE`. Each join's outcome (`resolved`, `ambiguous`, `reference_null`, `excluded`,
`not_applicable`, `not_found`, `invalid_key`, `owner_unanswered`) is reported by `onMissing` and
refused under `strict`; a resolve that names it (`outcomeAs`) carries it on every row. The details: [`oxql-semantics.md`](src/docs/oxql-semantics.md#chains-across-services)
and [`oxql-operations.md`](src/docs/oxql-operations.md#keyed-fetch-and-remote-continuation).

## Addon definitions

An extendable entity's `addon` bag is untyped storage; an organisation declares what its keys
hold through the host's addon definition API (the base package's `AddonDefinition` controller
and `GET /schema/addons`). The engine reads the definitions per request through
`IAddonDefinitionSource`: a defined key of kind `string int long double decimal bool date
dateTime guid` is filterable and sortable (sorting is diagnosed with `SORT_ON_ADDON`) and
matches tolerantly across the representations the bag may hold (a BSON date or an ISO string,
a number or a numeric string, a boolean or `"true"`/`"false"`, a binary or string guid, a
decimal at `key` and at `key._v`). A definition with a closed `values` list admits only its
values on `eq`, `neq`, `in` and `nin` (`UNKNOWN_ENUM_MEMBER` otherwise). A key of kind
`object`, a retired key and an undefined key are `unknown`.

## Studio console

`AddOxQLStudio` plus `app.MapOxQLStudio()` serves a slim developer console for one service: a
Monaco editor on one scratch query, **Run** (`/query`, or `/batch` for a body of
`{ "queries": [ … ] }`), **Explain** and **Indexes** (explain without and with
`include: ["indexes"]`: markers from `errors`, `valid: false` shown as an answer, stages, notes,
owners, result columns, diagnostics, the bound form and the emitted stages), the health panel, and
a credential field. It always sends `X-OxQL-Contract: 2`. The full studio (catalog, builder,
workbench) is the Angular OxQL Studio; `StudioAppUrl` links to it.

- **Route.** `OxQLStudioOptions.RoutePath` (default `/oxql`) is relative to the request's path
  base (`UsePathBase`): the shell is served at `{pathBase}{RoutePath}` and the assets at
  `{pathBase}{RoutePath}/{asset}`, both anonymous. It may equal the API's path: the asset route
  matches only names of word characters and dashes ending in `.js`, `.css` or `.svg`, and never
  `health`, `query`, `batch` or `explain`. The Simplic base package sets `RoutePath = "/oxql"`, so
  the console sits at `{pathBase}/oxql`.
- `ApiBasePath` (default `/OxQL`) is the API the console calls and `SchemaBasePath` (default
  `/schema`) the schema it reads the entity ids from, for completion only; both are prefixed with
  the path base in the page. `Title` and `MonacoCdnBase` (jsDelivr by default; self-host it where
  the CDN is not reachable) complete the options.
- **Explain.** `EnableExplain` (default `true`) shows the Explain and Indexes buttons; they are also
  hidden when `/health` does not list the `explain` capability. It should match
  `OxQL:Explain:Enabled`, and the host logs a warning when the console is mapped and the two differ.
- **Credential.** A bearer pasted in the page is kept in `sessionStorage` per service, shown with
  its decoded claims and a warning before it expires, sent only to this service's API and never
  logged. The scratch query is kept per service in `localStorage`; the tabbed studio's stored query
  is taken over once and its stored bearer deleted.

## Sample host and tests

`src/OxQL.Sample` is a minimal host (`dotnet run --project src/OxQL.Sample`, path base
`/vehicle-api/v2`, organisation from an `OrganizationId` header for demonstration, the console
at `/vehicle-api/v2/oxql`). It has no `/schema` endpoint, so the console offers no entity
completion there.

```bash
dotnet test src/oxql.slnx
```

The tests cover the model builders (CLR against document equivalence over the vendored
schema documents in `src/OxQL.Tests/Fixtures/schemas`), generated binding cases over every
entity, path, kind and operator, golden compiled pipelines, the executor and remote resolver
against fakes, and the host surface over `WebApplicationFactory`.

`src/OxQL.IntegrationTests` runs the engine against a real MongoDB, which the tests start
themselves (no Docker, no installation). It uses a simulated fleet of in-process services over
a designed fixture corpus; the report entities mirror the real services' shapes, and the report
scenarios A1–A5 run through explain and query on them. How it works, how to run parts of it, how to
add a case and how to export the Angular studio's fixtures is in
[`src/docs/oxql-conformance.md`](src/docs/oxql-conformance.md).

## Upgrading

From the published 2.0 package (2.0.126.924) or from the 1.1 packages to 2.1. Coming from 1.1, a
host also meets the changes of the package surface that 2.0 made:
[`src/docs/UPGRADING-1.x.md`](src/docs/UPGRADING-1.x.md).

In short:

1. Raise the five packages together with the base package, on every service that calls or is
   called. Nothing negotiates between two versions of the engine (see *One build in a fleet*).
2. There is nothing to configure. Callers without the contract header keep their answers: the
   legacy pipeline is on by default.
3. Contract 2 callers change what *Contract 2 callers* lists: numbers, `NOT_STORED`, the removed
   members, stricter kinds.
4. Every service's schema revision changes once.
5. To turn the legacy pipeline off: `OxQL:Compat:Legacy:Enabled=false` (the rewrite alone answers
   contract 1) or `OxQL:Compat:Enabled=false` (every request is contract 2).

### What changes with no configuration change

A host that set a key keeps its value. Defaults that differ from 2.0:

| key | 2.0 | 2.1 | |
|---|---|---|---|
| `Compat:Legacy:Enabled`, `Compat:Legacy:Always` | (new) | `true` | contract 1 is answered by the legacy pipeline, not by the rewrite |
| `Explain:Enabled`, the console's `EnableExplain` | `false` | `true` | `POST /oxql/explain` answers; it never executes a query and is rate-limited per user. Set `false` to keep the route off |
| `Execution:TimingDetail` | (new) | `true` | a contract 2 caller may ask for the `timing` breakdown |
| `Execution:BatchConcurrency` | (new) | 4 | queries of one internal batch run four at once; a public batch runs one after another |
| `Limits:MaxResolveStages` | 2 | 8 | |
| `Limits:MaxResolveKeys` | 2 000 | 10 000 | counted per request over every keyed stage |
| `Limits:MaxGroupFields` | 20 | 40 | on both contracts |
| `Cache:ResolveCacheMaxEntries` | 50 000 | no longer read | renamed `Cache:OwnerFetchCacheMaxEntries`; a host that still sets the old key is warned when the engine is built and runs at the default |

Every other new key and its default is in the table of *Configuration*. Also:

- The console's shell and assets are served anonymously at `RoutePath` under the path base; a
  host that spelled its path base into `RoutePath` sets it to `/oxql`.
- `AddOxQLAspNetCore` raises the JSON depth the host reads to `OxQLJson.MaxDepth` (256), on every
  JSON body the host reads.
- New log lines: `OxQL.Compat.Legacy` (one per legacy answer), `OxQL owner call refused` /
  `failed` (warning), `OxQL keyed fetch on this host failed` (error); `OxQL engine fault` names the
  organisation and the correlation id.

### Contract 2 callers

Contract 2 was published with 2.0 and is complete with 2.1. What a caller written against 2.0
changes:

- **A `long` and a `decimal` are JSON numbers** with every digit, as the services' REST API
  writes them: in rows, group keys and outputs, pushed arrays, `values` rows and addon values, an
  enum backed by a long included. They were strings. A reader that parses JSON numbers into
  doubles keeps 15 to 17 significant digits. Operands are unchanged; durations stay ISO 8601;
  there is no option for text.
- **Removed members**, each refused with `params.reason: "removed"` and what to use instead:
  `select` on a `lookup` or `resolve` (`project` the paths under the alias), `page.offset` (a
  `cursor`), `page.includeTotalCount` (`page.count`, answered under `pageInfo.count`),
  `unwind.keepPath`, `options.ignoreCase` (`caseSensitive`). An `unwind` names its alias (`as`).
- **A top-level member the request does not have** is `UNKNOWN_REQUEST_MEMBER` (it was ignored),
  and so is a contract header that names neither contract (`params.reason: "contract"`).
- **A projection** that names a member nothing stores is `NOT_STORED`, also under a keyed alias.
  `true` and `false` read as `1` and `0`; any other value that is no number (`null`, a string, an
  array) is `MIXED_PROJECTION`. A path an earlier projection removed is `UNKNOWN_PATH` when read
  after a second projection.
- **Stage members of the wrong JSON kind are refused** where they were read as absent: a `page`
  that is no object and a `limit` that is no number (`INVALID_PAGE_LIMIT`), a `cursor` that is no
  string (`CURSOR_INVALID`), a `group` that is no object, a group key with both `path` and
  `dateTrunc`, an `unwind.preserveNull` that is no boolean and a `lookup.limit` that is no number
  (`UNKNOWN_STAGE_MEMBER`), an aggregate with two functions or arithmetic nested deeper than 32
  (`INVALID_AGGREGATE_ARGUMENT`). `null` is still "not written".
- **Operands.** A decimal written as text takes a leading sign, a decimal point and an exponent
  only (`"12,50"`, `" 7 "` are `INVALID_OPERAND`); an operand of a single-character member is
  exactly one character.
- **The model's own type member** of a polymorphic type (`type`: `"driver"`, …) is answered from
  the stored discriminator and compared with `eq`, `neq`, `in`, `nin`.
- **Strict requests** refuse a `lookup` or `flatten` cut that a later match hides, also over the
  rows a condition on another service's alias selects (422 `LOOKUP_TRUNCATED` /
  `UNWIND_DEPTH_TRUNCATED`, `filtered: true`).
- **Rows.** A stored `NaN` or infinity under an `int`, `long` or `decimal` member is `null`. A
  condition on another service's alias over a reference stored as text selects the owner's keys
  exactly, also in a request that folds case.
- **Cursors.** A cursor of a query whose joins only fill the row is bound to the fingerprint of
  the query without them; a cursor such a query got from an earlier build is `CURSOR_INVALID`
  once.
- **Health.** A contract 2 caller gates on `engine.contract` and reads 48 limits. The capability
  names `batch`, `group.page`, `page.offset` and `any` are listed only for a contract 1 caller on
  the rewrite path.

What it gains: `onMissing` at the top of a request as the default of every `resolve`; members
below a member that holds its own kind as paths at every level; `RESOLVE_NOT_DECLARED` and
`LOOKUP_NOT_DECLARED` listing the members that do declare a reference (`params.joinable`); the
request's `filter`, `values`, `page.count`, `timing`, `maxTimeMs`, `outcomeAs`, the union join
and joins across services by remote `lookup` (see the query syntax).

### Every service's schema revision changes once

The base package publishes fifteen of the engine's limits in the schema document (`limits`), and
the document's `revision` is the hash of its bytes. At the defaults:

| published in `/schema` → `limits` | 2.0 | 2.1 |
|---|---|---|
| `maxGroupFields` | 20 | **40** |
| `maxResolveStages` | 2 | **8** |
| `maxContinuedStages`, `maxFlattenDepth`, `maxReportPageSize` | not in the document | 8, 5, 5 000 |
| the other ten | as now | as now |

So the schema revision of every service changes when it takes the package, without any change to
its model: for the document's format (1.1), these limits, and `byVariant`, `values` and
`notSortable` on the type member of a polymorphic type. A client that holds a document reloads
it, and an origin's kept explain answers of that owner are dropped with the revision they were
keyed by. The other limits 2.1 adds are published on health only.

### One build in a fleet

**The engine and the base package are deployed together, on every service that calls or is
called.** Health and explain publish the package version and `engine.contract: 2`; nothing
negotiates between two versions of the engine, and a fleet of mixed versions is not held by any
test. Known to fail between a 2.0 service and a 2.1 service: a 2.0 origin sends a folding
condition on an owner's member as `options: { ignoreCase: true }`, which a 2.1 owner refuses (the
origin answers 422 `RESOLVE_REFUSED`); a 2.1 origin sends a 2.0 owner what 2.0 does not know (a
continued stage, a typed or item reference, `timing`); and each side reads members of the other's
internal explain answer that the other does not write the same way. Under a join's alias the rows
are the owner's, so a `long` or `decimal` member of another service is a string while that
service runs 2.0 and a number once it runs 2.1.

An owner that is sent a top-level member it does not know on the internal route refuses it with
`UNKNOWN_REQUEST_MEMBER` and `params { reason: "engineBuild", members }`; the origin's
`RESOLVE_REFUSED` carries that error.

### Public API against 2.0.126.924

For code outside the packages (the Simplic base package is adapted with its pin):

- **Changed.** `IOxQLQueryService.ExplainAsync` and `IQueryEngine.ExplainAsync` take an
  `ExplainRequest` (a `QueryRequest` converts to one). The `MongoQueryEngine` constructor takes an
  `OwnerFetchCache` and an `ExplainForwardCache` (and an optional `TimeProvider`); the
  `OxQLQueryService` constructor takes the `LegacyRoute`. `ReferenceDef.Targets` is required;
  `TargetEntity`, `TargetField` and `IsRemote` derive from it. `WireNames.DisplayCandidates` is an
  `IReadOnlyList<string>`. `OxQL.Model` references `MongoDB.Bson`, no longer `MongoDB.Driver`.
- **Removed.** `RemoteResolver`; `ResolveCache` and `SemiJoinCache` (one `OwnerFetchCache` holds
  the resolved rows and, apart, the semi-join ids; a host that registered or constructed either
  registers `OwnerFetchCache`, which `AddOxQLMongo` does itself) and with them
  `CacheOptions.ResolveCacheMaxEntries` (now `OwnerFetchCacheMaxEntries`); `IndexAdvice`;
  `IIndexSource.ExplainAsync` with `MongoIndexSource.ExplainAsync` and `ExplainCommand`;
  `ExplainResult.Bound`, `Count` and `Collation` (the record holds the explain model of
  [`oxql-operations.md`](src/docs/oxql-operations.md)); `EntityModel.BuiltAt`;
  `ReferenceDef.Direction` and `ReferenceDirection`; `WireNames.PublishedStorageName`.
- **No longer public.** The compiler and the helpers behind the engine: `MongoCompiler` with
  `CompiledQuery`, `CompileOptions` and `SemiJoinSlot`, `WireEncoder`, `IndexAdvisor`,
  `CompatBinder` with `CompatRewrite`, `CompatRows`, `BsonDocumentJsonConverter`, `ResolveResult`,
  and the members `MongoQueryEngine.NextCursor`, `ClrModelBuilder.ReferenceIdAttributeName`,
  `ClrModelBuilder.PublishedProperties` and `DocumentModelBuilder.DefaultRepresentation`. The
  positional records of `OxQL.Core.Binding` stay public and are engine internals: their shape
  changes without notice.
- **Added, with defaults** so existing implementers compile: `IRemoteQueryClient.ExplainAsync` and
  `ExplainBatchAsync`; `IOxQLQueryService.BatchAsync(batch, internalCall, …)`,
  `ExplainAsync(request, internalCall, …)` and `ExplainBatchAsync` (the defaults throw
  `NotSupportedException` for an internal call); `IEntityModelProvider.SchemaRevision`;
  `IRemoteOwnerInfo`; `IReportingAddonDefinitionSource`; `OxQLQueryService.ContractRefusal`.

### Turning the legacy pipeline off

Count the lines of the log category `OxQL.Compat.Legacy` per service: each is a caller without
the header. When it is quiet, `OxQL:Compat:Legacy:Enabled=false` leaves contract 1 to the rewrite
(what it cannot express is refused), and `OxQL:Compat:Enabled=false` ends contract 1 on the host.
Removing the code is described in [`src/docs/LEGACY.md`](src/docs/LEGACY.md#removing-it) and is a
breaking change of the packages' public API.

## Requirements

.NET 10, MongoDB.Driver 3.9, MongoDB 6.0 or later (tested on 8.0).
