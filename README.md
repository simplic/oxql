# OxQL

A typed query engine over MongoDB for Simplic OxS services. A client posts a JSON pipeline
naming an entity; the engine binds every path and operand against the host's entity model,
applies the caller's organisation scope at every entry into an entity, compiles one
aggregate, executes it under guard rails, and answers rows in the same wire encoding the
service's REST responses use. This is **contract 2**; contract 1 requests are still served
through a compatibility mode for one release (see *Compatibility mode*). Version 2.0 is a
breaking package upgrade for hosts; see *Upgrading from 1.x*.

The full request syntax, the operand rules per kind, and the closed lists of error and
diagnostic codes are in [`src/docs/oxql-query-syntax.md`](src/docs/oxql-query-syntax.md).

## Packages

| package | contents |
|---|---|
| `OxQL.Model` | The entity model: every queryable entity with its wire view (what the schema publishes), its storage view (what the driver stores, read from the MongoDB serializer registry), declared references and the pooled type structure. Built by `ClrModelBuilder` from `[OxQLType]` assemblies or by `DocumentModelBuilder` from an Ox Schema document. Also the addon definition contract (`AddonDefinition`, `IAddonDefinitionSource`). |
| `OxQL.Core` | The request models, the binder (paths, operands, conditions, the shape fold through the pipeline), the signed cursor codec, the options (`OxQLOptions`), the engine and host contracts (`IQueryEngine`, `IEntityModelProvider`, `IRemoteQueryClient`). |
| `OxQL.Mongo` | The MongoDB engine: compiler, executor, wire encoder, remote resolve fan-out with its cache, the explain index advisory. |
| `OxQL.AspNetCore` | The controller (`POST /oxql/query`, `POST /oxql/batch`, `GET /oxql/health`, `POST /oxql/explain`), contract detection and the contract 1 compatibility binder, the request-size cap, the scope-provider contract (`IOxQLScopeProvider`) and the startup checks. |
| `OxQL.Studio` | A browser query builder served at `/oxql`, reading the host's `/schema` and `/schema/addons` for shape and the query controller for execution. |

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

// Optional: the browser query builder.
builder.Services.AddOxQLStudio(options => options.EnableExplain = true);

var app = builder.Build();
app.MapControllers();
app.MapOxQLStudio();
```

### Host contracts

| contract | who implements it | what it does |
|---|---|---|
| `IOxQLScopeProvider` (`OxQL.AspNetCore.Scope`) | the host, always | `OrganisationAsync(HttpContext?)` is the caller's organisation, applied as `organizationId eq <value>` at every entry into an entity; `UserId` and `CorrelationId` feed the log line and the remote forwarding. |
| `IEntityModelProvider` (`OxQL.Core.Engine`) | the host, optionally | The model the engine binds against. Default: `LazyEntityModelProvider` over the scanned assemblies, built on the first request. `ClrModelBuilder` must run after every serializer and class-map registration, never during `ConfigureServices`: the driver freezes a type's serializer on first lookup. |
| `IRemoteQueryClient` (`OxQL.Core.Engine`) | the host, when entities reference entities of other services | `BatchAsync(serviceKey, BatchRequest, budget, ct)` sends a batch to the owner of a remote entity, `IsConfigured` says whether the host knows the service, `IsReachableAsync` feeds `/health`. Without a client every remote resolve is refused with 422 `RESOLVE_UNAVAILABLE`. The service key is the target entity's namespace (`vehicle` of `vehicle.vehicle`). |
| `IAddonDefinitionSource` (`OxQL.Model.Addon`) | the host, when entities are extendable | An organisation's addon definitions per entity, read per request. Default: `EmptyAddonDefinitionSource` (every addon key is `unknown`: projectable, never filterable). |

## The entity model

One immutable model per host, built once from every class carrying `[OxQLType("<service>.<entity>", "<collection>", Extendable = ...)]`:

- **Entity ids** are matched exactly and case-sensitively (`UNKNOWN_ENTITY` otherwise). A retired id declared by the host (`ClrModelBuilder.Build(assemblies, retiredIds)`) is answered as the current entity with an `ENTITY_ID_RETIRED` diagnostic carrying `params.currentId`.
- **Two views.** The wire view is every public readable property, camelCase, `id` at every depth. The storage view comes from the MongoDB driver's serializer registry, so `[BsonElement]`, `[BsonId]`, `[BsonIgnore]`, `[BsonRepresentation]` and the `Id → _id` convention are observed, not inferred. A member the driver does not store is refused with `NOT_STORED`; a member whose serializer is not a document serializer (a GeoJSON point, an interface) is `unknown`: projectable, not filterable or sortable.
- **Kinds:** `string int long double decimal bool guid date dateTime timeSpan enum binary object array dictionary unknown`.
- **References** are declarations only, never name inference: `[OxQLReference("<entity id>", field?)]` (`OxQL.Model.Attributes`) on the id member, or the base package's `[ReferenceId("<idProperty>")]` on the navigation property. A target on another host is a remote reference; the model marks it and the host must know the owner (`IRemoteQueryClient.IsConfigured`), otherwise the host logs an error and refuses to start in `Development`, `Local` and under continuous integration (the `CI` or `TF_BUILD` variable, read once at registration into `OxQLEndpointOptions.ContinuousIntegration`).
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
    { "resolve": { "path": "vehicleId", "as": "vehicle", "select": ["id", "matchCode"] } },
    { "project": { "id": 1, "number": 1, "vehicle": 1 } },
    { "sort": [{ "number": "asc" }] },
    { "page": { "limit": 50, "includeTotalCount": true } }
  ]
}
```

```jsonc
{
  "items": [ { "id": "…", "number": "S-1", "vehicle": { "id": "…", "matchCode": "V-1" } } ],
  "pageInfo": { "hasNextPage": true, "nextCursor": "…", "totalCount": 1234, "totalCountCapped": false }
}
```

Stages run as written, in the order `match lookup resolve unwind group project sort page`, and
every path is checked against the shape the previous stages produced. A refusal is a
`validation_error`, `access_denied`, `not_executable`, `timeout` or `internal_error` envelope
with a closed list of codes; every binding error is reported at once. Details, every stage
and every code: [`src/docs/oxql-query-syntax.md`](src/docs/oxql-query-syntax.md).

## Endpoints

| route | what |
|---|---|
| `POST /oxql/query` | One request; 200 with rows, or the refusal envelope with its status (400, 403, 413, 422, 504, 500). |
| `POST /oxql/batch` | `{ "queries": [ <request>, … ], "maxTimeMs"? }`; always 200 with `{ "results": [ … ] }` in order, each entry a full success body or a refusal envelope. More than `Limits:MaxBatchQueries` queries is `BATCH_TOO_LARGE`. Queries run sequentially on one host. |
| `GET /oxql/health` | Anonymous. `{ "status", "service": "oxql", "engine": { "version", "contract": 2 }, "capabilities": [ … ], "limits": { … }, "remote": [ { "service", "configured", "reachable" } ] }`. Capabilities: `batch`, `group.page`, `page.offset`, `any`, and with a remote client `resolve.remote`, `semiJoin`; `explain` and `compat.v1` when enabled. `status` is `degraded` when a referenced service is not configured or did not answer when last measured. The answer never waits for another service: `reachable` is the last measurement, refreshed in the background at most once per `Cache:HealthProbeTtlSeconds`, and `null` until the first one has finished. `?shallow=true` leaves `remote` out and starts no measurement; it is the form one host asks of another. |
| `POST /oxql/explain` | 404 unless `Explain:Enabled`. `{ "bound", "stages", "count"?, "collation"?, "advisory"?, "diagnostics"? }`: the bound pipeline in canonical form, the emitted page and count stages, the collation both run under when a string folds, and an index advisory read from `listIndexes` (cached per collection). No rows are returned and the count never runs; for a pipeline with a `lookup` the advisory reads the server's own explain, which executes the page pipeline once under `Execution:MaxTimeMs`. |

Every request body is capped at `Limits:MaxRequestBytes` (413 `REQUEST_TOO_LARGE`).

## Contract header and compatibility mode

Clients send `X-OxQL-Contract: 2`. While `Compat:Enabled` is true (the default for the
compatibility release), a request **without** the header is contract 1: the entity id is
matched case-insensitively, storage-spelled paths are resolved against the folded shape, the
v1 type-hint operands (`$date`, `$uuid`, `$decimal`, …) are accepted, and rows come back in
the v1 encoding (`_id`, storage names).
The v1 `lookup` and `resolve` stages have no v2 equivalent and are refused with
`LEGACY_STAGE_UNSUPPORTED`. Every contract 1 request is logged under the `OxQL.Compat`
category with the first legacy path, the user, the organisation and the correlation id, so a
host can see who still needs migrating. With `Compat:Enabled` false every request is
contract 2, header or not.

## Configuration (`OxQL` section)

Every `Limits` value is what a host publishes to its clients (the Simplic base package puts
them into the schema document's `limits`).

| key | default | notes |
|---|---|---|
| `Compat:Enabled` | `true` | contract 1 for requests without the header |
| `Explain:Enabled` | `false` | `POST /oxql/explain` answers |
| `Limits:MaxPageSize` / `DefaultPageSize` | 500 / 100 | the page a request may ask for / gets without a limit (`DefaultPageSize` is clamped to `MaxPageSize`) |
| `Limits:MaxPipelineStages` | 20 | caller stages; the engine's scope stage does not count |
| `Limits:MaxLookupStages` / `MaxUnwindStages` / `MaxResolveStages` | 5 / 5 / 2 | |
| `Limits:MaxGroupFields` / `MaxProjectionFields` | 20 / 500 | |
| `Limits:MaxConditions` / `MaxVariables` | 200 / 64 | leaf conditions, lookup and resolve filters included |
| `Limits:MaxOffset` | 5 000 | the largest `offset`; beyond it a cursor |
| `Limits:CountCap` | 100 000 | above it `totalCount` is the cap and `totalCountCapped` true |
| `Limits:MaxSemiJoinIds` | 5 000 | a larger semi-join is refused, never truncated; never above `MaxOffset`, which clamps it (the ids are read from the owner by offset) |
| `Limits:ResolveKeyChunk` / `MaxResolveKeys` | 500 / 2 000 | keys per remote call (never above `MaxPageSize`, which clamps it) / per request |
| `Limits:MaxRequestBytes` / `MaxBatchQueries` / `RegexMaxLength` | 262 144 / 10 / 200 | |
| `Limits:MaxLookupLimit` | 100 | rows one lookup returns per parent |
| `Execution:MaxTimeMs` | 10 000 | `maxTimeMS` on every aggregate, clamped to 60 000; a timeout is 504 `QUERY_TIMEOUT` |
| `Execution:ResolveTimeoutMs` | 2 000 | budget of one remote call, clamped to the effective `MaxTimeMs` |
| `Execution:AllowDiskUse` | `true` | a sort or group over the server's memory limit spills to disk and finishes slowly; when `false`, it is 422 `QUERY_TOO_EXPENSIVE`; unset in code (`null`) leaves the server's default |
| `Execution:SlowQueryMs` | 1 000 | a request whose whole time (binding, aggregates, count, remote resolves) exceeds it is logged at warning level with the entity, the stage kinds, whether a regex, an unbounded sort, a count or a remote resolve was involved, the duration and the row count — never an operand; `0` turns the line off |
| `Representation:GuidTolerant` | `false` | also match legacy subtype 3 and string guids |
| `Representation:DecimalMode` | `tolerant` | match Decimal128 and string decimals; `typed` after a migration |
| `Representation:Collation:Locale` / `Strength` | `de` / `1` | the collation a contract 2 string comparison, sort and group key folds under; strength 1 folds case and accents, 2 case only, 3 and above tell both apart (clamped to 1–5; an empty locale falls back to `de`) |
| `Cache:ResolveTtlSeconds` / `ResolveCacheMaxEntries` | 60 / 50 000 | resolved remote rows |
| `Cache:AddonDefinitionTtlSeconds` | 30 | a host's addon definition cache |
| `Cache:HealthProbeTtlSeconds` | 10 | how long `/oxql/health` reuses the last reachability measurement |
| `Cursor:SigningKey` | — | required; the host does not start without it; cursors are HMAC-signed with a key derived from it |

The flat 1.x key names `MaxPageSize`, `DefaultPageSize`, `MaxPipelineStages`, `MaxLookupStages`,
`MaxUnwindStages`, `MaxGroupFields`, `MaxProjectionFields` and `RegexMaxLength` are still bound,
directly under `OxQL`, onto the same `Limits` values. A limit set above the one it depends on is
clamped at registration and the adjustment is registered as an `OxQLOptionsAdjustment` for the
host to log.

The Mongo connection (`ConnectionString`, `DatabaseName`, `IncludeErrorDetails`,
`ScanAssemblies`) is configured on `AddOxQLMongo`, not in the section.

## Guard rails

Every aggregate runs with `maxTimeMS`; every cap above has a code; no JavaScript ever reaches
Mongo; a `regex` operand passes a static check (no nested quantifiers, no backreferences) and
the length cap, and an unanchored pattern is diagnosed as `REGEX_UNANCHORED`. The
organisation scope is one typed equality on one indexed field on every query and inside every
`lookup` and local `resolve` sub-pipeline; a remote resolve is executed by the owning service
under its own scope. Driver errors map to 504 `QUERY_TIMEOUT` (code 50), 422
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

## Remote resolve

`resolve` follows a declared reference. A local target compiles to an indexed `$lookup`; a
remote target is fetched after the page is fixed, per owning service, in one call to the
owner's internal batch route through `IRemoteQueryClient` (keys chunked by
`Limits:ResolveKeyChunk`, results cached per entity, organisation, key, `select` and `filter`
for `Cache:ResolveTtlSeconds`). A `match` on a path under a remote alias is a semi-join: the
owner is asked for the matching ids first, and binds the condition under its own default;
`caseSensitive` or `ignoreCase` travel to it as the caller wrote them. An owner refusal becomes the caller's 422
`RESOLVE_REFUSED` with the owner's errors; a timeout or an unreachable owner yields `null`
under the alias and a `RESOLVE_TIMEOUT` / `RESOLVE_UNREACHABLE` diagnostic (the message says
whether the owner answered with an HTTP error or was not reached at all); a semi-join whose
owner does not answer is 422 `RESOLVE_UNAVAILABLE`. The ids a semi-join fetched are cached per
organisation and owner query for `Cache:ResolveTtlSeconds`.

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

## Studio

`AddOxQLStudio` plus `app.MapOxQLStudio()` serves a Monaco-based query builder at
`OxQLStudioOptions.RoutePath` (default `/oxql`). It reads the entity list and shape from
`SchemaBasePath` (default `/schema`, plus `/schema/addons` for the organisation's addon
definitions, slotted into the document as members of the `addon` bag), executes against
`ApiBasePath` (default `/OxQL`) with the contract header, and uses the batch endpoint.
`EnableExplain` shows the Explain button; it should match `OxQL:Explain:Enabled`, and the host
logs a warning at startup when the two disagree.

## Sample host and tests

`src/OxQL.Sample` is a minimal host (`dotnet run --project src/OxQL.Sample`, path base
`/vehicle-api/v2`, organisation from an `OrganizationId` header for demonstration, the Studio
at `/oxql`). It has no `/schema` endpoint, so the Studio's explorer is empty there.

```bash
dotnet test src/oxql.slnx
```

The tests cover the model builders (CLR against document equivalence over the vendored
schema documents in `src/OxQL.Tests/Fixtures/schemas`), generated binding cases over every
entity, path, kind and operator, golden compiled pipelines, the executor and remote resolver
against fakes, and the host surface over `WebApplicationFactory`.

## Upgrading from 1.x

Version 2.0 is a new engine behind the same routes. The wire contract is kept for one release
through the compatibility mode above; the package surface is not. A host that only bumps the
package versions does not compile, and one that compiles does not start until the two new
requirements below are met.

### Packages

`OxQL.Model` is new and holds the entity model; `OxQL.Core` depends on it, so it arrives with
`OxQL.Core` as a transitive reference. The other packages keep their names. `[OxQLType]` now
lives in `OxQL.Model`; its namespace `OxQL.Core.Attributes` is unchanged and `OxQL.Core` forwards
the type, so entity assemblies compile without a change.

### Removed registration entry points

| 1.x | 2.0 |
|---|---|
| `endpoints.MapOxQL()` (`EndpointRouteBuilderExtensions`) | removed; the controller is the only surface, mapped by `app.MapControllers()` |
| `AddOxQLEndpoint<T>(…)` | folded into `AddOxQLAspNetCore(…)` |
| `AddOxQLAspNetCore<T>(…)` | kept and marked obsolete; the type argument is ignored. Call `AddOxQLAspNetCore(…)` |
| `AddOxQLQueryFilter<TProvider>()` and the two delegate overloads, `IOxQLQueryFilterProvider`, `OxQLFilterInjectionContext`, `InjectedFilter`, `QueryFilterInjector` | `AddOxQLScope<TProvider>()` / `AddOxQLScope(httpContext => …)` with `IOxQLScopeProvider`; the organisation scope is applied by the engine, not injected as a filter |
| `AddOxQLTypeEnricher<TEnricher>()` and its overloads, `IOxQLTypeEnricher`, `OxQLTypeEnrichmentContext`, `OxQLTypeDescriptor`, `OxQLPropertyDescriptor` | no equivalent; the entity model and the host's `/schema` describe the types |
| `OxQLErrorResponse`, `OxQLFieldError`, `OxQLQueryResult` | `Refusal`, `QueryValidationError`, `QueryResult` / `QueryOutcome` (`OxQL.Core.Engine`) |
| `IExternalResolver`, `ICursorSerializer`, `IQueryAdapter<T>`, `IQueryExecutor<T>`, `IQueryPlanner`, `IQueryPlanCache`, `IQueryRequestNormalizer`, `IQueryValidator` and their implementations | `IQueryEngine`, `IRemoteQueryClient`, `CursorCodec` |
| `OxQLEndpointOptions.RoutePrefix`, `.IncludeErrorDetails`, `.EnableExplain` | the route is `/OxQL` (controller convention); `IncludeErrorDetails` moved to `AddOxQLMongo`; explain is `OxQL:Explain:Enabled` |
| `IOxQLQueryService.ExecuteAsync` returning `OxQLQueryResult`, `ExplainAsync` returning a list | `QueryOutcome` and `ExplainOutcome`; new `ExecuteAsync(request, maxTimeMs, ct)` and `BatchAsync` |
| `OxQLTypeRegistry`, `QueryValidationException`, `SortStage`, the `MatchStage.And` / `Or` / `Not` helpers, `Shape.WithAddons` | removed from the public surface; no replacement is needed by a host |

Beyond the entry points, three `OxQL.Core` binder types are worth naming for a host that
constructs bound pipeline parts itself:

- `Aggregate` takes the argument's kind as its fourth positional member, `Kind ArgumentKind`.
- `ShapeNode.GroupOutput` takes two optional trailing members, `Kind ElementKind` and
  `ShapeDef? ElementShape`, which describe a pushed element.
- `Shape.WithGroup` takes an `IEnumerable<ShapeNode.GroupOutput>`.

### Two new mandatory requirements

- **A scope provider.** Register one with `AddOxQLScope`. Without it the host throws
  `InvalidOperationException` at startup; this replaces the 1.x organisation filter provider.
- **A cursor signing key.** Set `OxQL:Cursor:SigningKey`. Without it `AddOxQLCore` throws at
  startup. The Simplic base package derives it from the auth token.

### Configuration

The `OxQL` section is now nested (`Limits`, `Execution`, `Compat`, `Explain`, `Representation`,
`Cache`, `Cursor`); the eight flat 1.x limit keys are still read (see *Configuration*). Five 1.x
keys are no longer read and are silently ignored by the binder: `AllowedLookupSources`,
`AllowedResolveSources`, `AllowedPathPrefixes`, `QueryPlanCacheTtl`, `QueryPlanCacheMaxEntries`.
Lookups and resolves are now allowed only along references the model declares, which is
stricter than any allow-list was, and there is no plan cache to size.

`DefaultPageSize` changed from 50 to 100: a request without `page.limit` gets twice the rows.

`Execution:AllowDiskUse` defaults to `true`: a sort or group that outgrows the server's memory
limit spills to disk and finishes slowly instead of failing with `QUERY_TOO_EXPENSIVE`, whatever
the server's own default. Set it to `false` to refuse such queries as before.

`Execution:SlowQueryMs` is new (default 1 000): a request slower than that is logged once more,
at warning level, with what shaped it. Set it to `0` to turn the line off.

### Behaviour changes for callers

- Requests without `X-OxQL-Contract: 2` are served as contract 1 while `Compat:Enabled` is
  true, which is the default for this release. Each one is logged under the `OxQL.Compat`
  category with the user, organisation and correlation id, so the hosts can see who still needs
  migrating before the mode is switched off in the next release.
- Under contract 2 an entity id is matched case-sensitively and has to be spelled as the
  schema publishes it (`UNKNOWN_ENTITY` otherwise); contract 1 requests keep v1's
  case-insensitive matching, retired ids included.
- `GET /OxQL/types` is removed; the host's `/schema` document describes the entities.
- The refusal envelope no longer carries a `status` member; the HTTP status is the status.
- `dateTrunc` truncates in the given `timezone` (default UTC) with weeks starting on Monday; 1.x
  passed neither to the server, which truncated in UTC with weeks starting on Sunday, so a ported
  grouping query gets different week buckets unless it names `weekStart`. A `date` member has
  no time of day, so it truncates on its calendar day in that zone: the bucket is local
  midnight of the row's own day. A `dateTime` truncates as the instant it holds.
- `avg` over a `long` is taken in decimal and travels as a decimal string, the way a `sum`
  over a long does; over an `int` or a `double` it is a JSON number. A double cannot hold the
  mean of 64-bit integers exactly.
- The elements of a pushed array arrive in the member's wire encoding: a `long` as a string,
  a `date` as `YYYY-MM-DD`, a single character as that character, an object by its members.
- Under contract 2 string comparisons, sorts and group keys are case- and accent-insensitive by
  default (`muller` matches `Müller`), under a collation the host configures
  (`Representation:Collation`, German at primary strength unless changed). `options.caseSensitive:
  true` on a condition and `{ "path": { "direction": "asc", "caseSensitive": true } }` on a sort
  entry opt out; an exact sort needs every string comparison of the request to be exact too.
  `ignoreCase` is accepted for one release as the alias with the opposite sense
  (`ignoreCase: false` is `caseSensitive: true`). An ordered comparison on a string orders
  under the collation and cannot opt out. Contract 1 folds only with `ignoreCase: true`, as
  before, and knows no `caseSensitive`.
- A member holding a single character folds the operand's case by code point, so `eq`, `neq`,
  `in` and `nin` match either case unless `caseSensitive`; the character is stored as its code
  point, which neither a pattern nor a collation reaches.
- Two `sort` stages do not compose: the later one replaces the earlier one.
- A `lookup` or a local `resolve` that no later `match`, `sort`, `unwind`, `group` or `resolve`
  reads runs after the page is taken, on the page's rows alone; the rows are the same, the
  join is paid per page row, and the sort stays next to the limit. A join a later stage reads,
  a join before a `group`, and a join whose alias a projection narrows or drops stay where they
  were written. The count pipeline never carries a join only the rows' display reads; explain
  shows the order the server runs.
- `page.includeTotalCount` also takes a positive integer: the request's own count cap, under
  the host's `CountCap`; `totalCount` and `totalCountCapped` then read against it. Contract 1
  keeps the boolean form only.
- Refusals are wider, under existing codes: `INVALID_ALIAS` for an alias `_id`, starting with
  `__` or ending in `__arr`; `ALIAS_COLLISION` for an alias equal to a member's wire or storage
  name; `INVALID_PATH` for a path with a control character, more than 64 segments or a segment
  over 256 characters; `INVALID_OPERAND` for a text operand of `contains`, `startsWith`,
  `endsWith`, a contract 1 `ignoreCase` comparison or a `caseSensitive` comparison inside a
  request that folds elsewhere, when it no longer fits the server's 32 KB pattern limit once
  escaped; `OPTION_NOT_APPLICABLE` for `caseSensitive` where it does not apply, for a
  `caseSensitive` sort inside a request that folds, and for `caseSensitive` and `ignoreCase`
  disagreeing on one condition; and `INVALID_REGEX` / `QUERY_TOO_EXPENSIVE` may now also come
  back from execution, when the server rejects a pattern or exceeds its memory limit.

## Requirements

.NET 10, MongoDB.Driver 3.9, MongoDB 6.0 or later (tested on 8.0).
