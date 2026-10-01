# OxQL

A typed query engine over MongoDB for Simplic OxS services. A client posts a JSON pipeline
naming an entity; the engine binds every path and operand against the host's entity model,
applies the caller's organisation scope at every entry into an entity, compiles one
aggregate, executes it under guard rails, and answers rows in the same wire encoding the
service's REST responses use. This is **contract 2**; contract 1 requests are still served
through a compatibility mode for one release (see *Compatibility mode*). Version 2.0 is a
breaking package upgrade for hosts; see *Upgrading from 1.x*.

Contract 2 has what reports need: polymorphic members and the `is`
operator, flattened item trees, typed, item and key-converting references, `lookup` with `sort`,
`first` and `on`, the outcome of every join with `onMissing` and `strict` and on the row under a
name (`outcomeAs`), chains across services by remote continuation, one alias across the targets of
a union (`byTarget`), and `POST /oxql/explain` as the one "all information about this query"
endpoint, on by default and never executing. What a host and a caller coming from contract 1 need
to know is in *Upgrading from contract 1*.

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
| `IEntityModelProvider` (`OxQL.Core.Engine`) | the host, optionally | The model the engine binds against. Default: `LazyEntityModelProvider` over the scanned assemblies, built on the first request. `ClrModelBuilder` must run after every serializer and class-map registration, never during `ConfigureServices`: the driver freezes a type's serializer on first lookup. |
| `IRemoteQueryClient` (`OxQL.Core.Engine`) | the host, when entities reference entities of other services | `BatchAsync(serviceKey, BatchRequest, budget, ct)` sends a batch to the owner's internal batch route, `IsConfigured` says whether the host knows the service, `IsReachableAsync` feeds `/health`. `ExplainBatchAsync(serviceKey, ExplainBatchRequest, budget, ct)` sends what one round of an explain asks one owner (its checks, with the time and owner calls left) as one call to the owner's internal explain, for explain's remote check and its catalog entries of the owner's entities; its default asks check by check through `ExplainAsync(serviceKey, ExplainRequest, budget, ct)`, whose default answers `null` (the parts are noted `REMOTE_UNCHECKED`). A client that also implements `IRemoteOwnerInfo` reports what each owner's shallow health said (engine version, `maxBatchQueries`), from which the engine splits batches. Without a client every remote resolve is refused with 422 `RESOLVE_UNAVAILABLE`. The service key is the target entity's namespace (`vehicle` of `vehicle.vehicle`). |
| `IOxQLQueryService` (`OxQL.AspNetCore`) | the engine; called by the host's internal routes | `BatchAsync(batch, internalCall: true, ct)` and `ExplainBatchAsync(batch, ct)` run a batch or the checks of one explain round as an owner call: only they (and `ExplainAsync(request, internalCall: true, ct)`, one check) admit the keyed fetch's `keyedBy`. A host that serves other services' remote resolves routes its internal batch and explain routes through them; implementers and fakes of the interface add the overloads they are called with. |
| `IAddonDefinitionSource` (`OxQL.Model.Addon`) | the host, when entities are extendable | An organisation's addon definitions per entity, read per request. Default: `EmptyAddonDefinitionSource` (every addon key is `unknown`: projectable, never filterable). |

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
| `GET /oxql/health` | Anonymous. `{ "status", "service": "oxql", "engine": { "version", "contract": 2 }, "capabilities": [ … ], "limits": { … }, "remote": [ { "service", "configured", "reachable" } ] }`. `engine.contract` 2 is the marker of this package: every contract 2 construct and the explain answer. Capabilities: `batch`, `group.page`, `page.offset`, `any`, `unwind.keepPath`, and with a remote client `resolve.remote`, `semiJoin`, `resolve.chain`, `lookup.remote`; `explain` and `compat.v1` when enabled. `status` is `degraded` when a referenced service is not configured or did not answer when last measured. The answer never waits for another service: `reachable` is the last measurement, refreshed in the background at most once per `Cache:HealthProbeTtlSeconds`, and `null` until the first one has finished. `?shallow=true` leaves `remote` out and starts no measurement; it is the form one host asks of another. |
| `POST /oxql/explain` | On by default; 404 while `Explain:Enabled` is false. Takes a query or the envelope `{ query, include?, shape?, remote?, catalog? }` and never executes it. Answers 200 with `valid`, every error, each stage with its placement and the shape of the row after it, the aliases, the types of the row's roots by reference to the services' schema documents (entity, service, revision) with a rule per root and stage that says where the members stand (`include: ["types"]` writes the member rows and their flags out for a caller without schema documents), the result columns, the owners asked, engine-behaviour notes, and on request the plan (`include: ["plan"]`: the canonical bound form, the emitted page and count stages, the collation, the owner queries) and the index advisory (`include: ["indexes"]`, which reads only `listIndexes`). Rate-limited per user (429 with `Retry-After`) and bounded in what one explain may ask of other services. See [`oxql-operations.md`](src/docs/oxql-operations.md#post-oxqlexplain). |

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
contract 2, header or not. A missing header is the likeliest mistake when a query is pasted into
a report data source, so every refusal a contract 1 request receives for a contract 2 construct
(`strict`, `is`, a contract 2 stage member, a continued stage, `caseSensitive`, the object form of a sort
entry, the number form of `includeTotalCount`) ends its message with "This request was read as
contract 1 because it carries no 'X-OxQL-Contract: 2' header."; the codes are unchanged.

## Configuration (`OxQL` section)

Every `Limits` value is published on `GET /oxql/health`; the Simplic base package also puts the
fifteen a caller checks a request against before sending it into the schema document's `limits`.
Where each limit is enforced, and how it reaches calls between services, is in
[`src/docs/oxql-operations.md`](src/docs/oxql-operations.md#limits).

| key | default | notes |
|---|---|---|
| `Compat:Enabled` | `true` | contract 1 for requests without the header |
| `Explain:Enabled` | `true` | `POST /oxql/explain` answers |
| `Explain:RemoteTimeoutMs` / `TimeoutMs` | 1 500 / 2 000 | explain's wait for owners in all / the wall time of one explain |
| `Explain:DefaultShapeDepth` / `MaxTypeMembers` / `MaxAnswerBytes` | 2 / 300 / 262 144 | with `include: ["types"]`, levels of member rows listed / member rows per type; the size an answer is trimmed to |
| `Explain:MaxOwnerServices` / `MaxOwnerCalls` | 4 / 8 | owner services one explain asks / owner calls it causes in all |
| `Explain:RatePerMinute` / `RateBurst` / `MaxConcurrentPerUser` / `MaxConcurrentPerHost` / `MaxConcurrentPerCaller` | 20 / 5 / 2 / 8 / 4 | the rate and concurrency of explain, per user, per host and per calling service; more is 429 |
| `Limits:MaxPageSize` / `DefaultPageSize` | 500 / 100 | the page a request may ask for / gets without a limit (`DefaultPageSize` is clamped to `MaxPageSize`) |
| `Limits:MaxPipelineStages` | 20 | caller stages; the engine's scope stage does not count |
| `Limits:MaxLookupStages` / `MaxUnwindStages` / `MaxResolveStages` | 5 / 5 / 8 | resolve stages bound on this host; continued stages count at their owner |
| `Limits:MaxContinuedStages` | 8 | stages continued under one keyed or remote alias; clamped to `MaxPipelineStages` |
| `Limits:MaxFlattenDepth` | 5 | levels an `unwind` with `flatten` descends; clamped to 1–12 |
| `Limits:MaxReportPageSize` / `MaxReportedRows` | 5 000 / 50 | the page of a `strict` request without cursor or offset / rows one outcome diagnostic lists |
| `Limits:MaxGroupFields` / `MaxProjectionFields` | 20 / 500 | |
| `Limits:MaxConditions` / `MaxVariables` | 200 / 64 | leaf conditions, lookup and resolve filters included |
| `Limits:MaxOffset` | 5 000 | the largest `offset`; beyond it a cursor |
| `Limits:CountCap` | 100 000 | above it `totalCount` is the cap and `totalCountCapped` true |
| `Limits:MaxSemiJoinIds` | 5 000 | a larger semi-join is refused, never truncated; never above `MaxOffset`, which clamps it (the ids are read from the owner by offset) |
| `Limits:ResolveKeyChunk` / `MaxResolveKeys` | 500 / 10 000 | keys per owner query (never above `MaxPageSize`, which clamps it) / keys asked of owners per request, over every keyed stage |
| `Limits:MaxRequestBytes` / `MaxBatchQueries` / `RegexMaxLength` | 262 144 / 10 / 200 | |
| `Limits:MaxLookupLimit` | 100 | rows one lookup returns per parent |
| `Execution:MaxTimeMs` | 10 000 | `maxTimeMS` on every aggregate, clamped to 60 000; a timeout is 504 `QUERY_TIMEOUT` |
| `Execution:ResolveTimeoutMs` | 2 000 | budget of one plain remote resolve call, clamped to the effective `MaxTimeMs` |
| `Execution:ChainTimeoutMs` | 6 000 | budget of one owner call carrying continued stages or a typed, item, converted or element-wise resolve (the time left less 50 ms, at most this), clamped to the effective `MaxTimeMs` |
| `Execution:AllowDiskUse` | `true` | a sort or group over the server's memory limit spills to disk and finishes slowly; when `false`, it is 422 `QUERY_TOO_EXPENSIVE`; unset in code (`null`) leaves the server's default |
| `Execution:SlowQueryMs` | 1 000 | a request whose whole time (binding, aggregates, count, remote resolves) exceeds it is logged at warning level with the entity, the stage kinds, whether a regex, an unbounded sort, a count or a remote resolve was involved, the duration and the row count — never an operand; `0` turns the line off |
| `Representation:GuidTolerant` | `false` | also match legacy subtype 3 and string guids |
| `Representation:DecimalMode` | `tolerant` | match Decimal128 and string decimals; `typed` after a migration |
| `Representation:Collation:Locale` / `Strength` | `de` / `1` | the collation a contract 2 string comparison, sort and group key folds under; strength 1 folds case and accents, 2 case only, 3 and above tell both apart (clamped to 1–5; an empty locale falls back to `de`) |
| `Cache:ResolveTtlSeconds` / `OwnerFetchCacheMaxEntries` | 60 / 50 000 | resolved rows of the keyed fetch and, apart, semi-join ids (formerly `ResolveCacheMaxEntries`, still bound for one release; the new key wins when both are set) |
| `Cache:NegativeResolveTtlSeconds` | 10 | how long a key an owner answered as not found is cached; a `strict` request reads past it; `0` caches none |
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
default; `caseSensitive` or `ignoreCase` travel to it as the caller wrote them.

A `resolve` or `lookup` on an alias another service returned is **continued**: it rides in the
query sent to that owner, which binds it with its own model and answers enriched rows, and so on
through a third service. The internal call gains no header: a forwarded query always carries fewer
join stages than the one that produced it, the time rides on the batch's `maxTimeMs`, `strict` is in
the body. The only new wire member is `keyedBy`, which groups an owner's answer per key (two
records at most, the second meaning `ambiguous`) and is accepted only on the internal route. Every
owner an origin reaches runs this package: an owner needs the internal routes
(`internal/oxql/batch`, `internal/oxql/explain`), which only this package has. A service still on
the version 1 package is not listed in `InternalHosts` and cannot be an owner.

An owner refusal becomes the caller's 422 `RESOLVE_REFUSED` with the owner's errors mapped to the
caller's stages; a timeout or an unreachable owner yields `null` under the alias and a
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
`include: ["indexes"]`: markers from `errors`, `valid: false` shown as an answer, steps, notes,
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

## Upgrading from contract 1

The 2.0 release package is the first with contract 2; `engine.contract: 2` on health marks it.
What follows are its release notes for a host and a caller coming from the version 1 package and
contract 1; the changes to the package surface are in *Upgrading from 1.x*.

A host other services call as an
owner needs the base package whose internal batch and explain routes use the internal-call
overloads (see *Hosts serving other services* below): without them it answers `keyedBy` with
`UNKNOWN_REQUEST_MEMBER`, which its callers see as `RESOLVE_REFUSED`. What a host, a caller and an
operator notice:

- **Capabilities.** A `lookup` whose `from` is another service's entity (or its item
  collection, `service.entity#item`) runs through the keyed fetch at that owner (capability
  `lookup.remote`); `unwind.keepPath: false` takes the unwound collection out of the row (capability
  `unwind.keepPath`). A caller gates on the capability before sending either.

- **Joins load what the query reads.** `select` on a `lookup` or `resolve` is a hint, never a bound:
  it says what the alias shows when the row keeps it whole (with the key; without it, key and
  display). Every member of the target can be read under the alias, and the join loads its key, what
  later stages read and what the row shows; what is only read is not in the row. A projection that
  names paths under an alias decides alone what the alias shows, the key included. There is no
  `parentSelect` (`UNKNOWN_STAGE_MEMBER`): project the paths under `parentAs`. Explain answers what
  each stage reads (`stages[].reads`) and what each join loads and shows (`aliases.X.loads`,
  `shows`, `hint`). Contract 1 is unchanged: a join fetches its `select`.
- **Explain.** `OxQL:Explain:Enabled` defaults to `true`; set it to `false` to keep the route off.
  Explain never executes a query, a query that does not bind is answered 200 with `valid: false` and every error
  instead of a 400 refusal, and the answer is the bind trace: `stages`, `aliases`, `types`,
  `rules`, `result`, `owners`, `catalog`. It names its types by reference to the schema documents
  (`GET /schema`) and writes no member; `include: ["types"]` adds the member rows and `flagSets`.
  The plan (`bound`, `stages`, `count`, `collation`) is
  under `plan` and needs `include: ["plan"]`; the index advisory needs `include: ["indexes"]`. The
  body may be an envelope with `include`, `shape`, `remote` and `catalog`. Explain is rate-limited
  per user and bounded in what it asks of other services. It answers its validator as `ETag` (304
  for `If-None-Match`), writes the body in the content coding the caller accepts, and with
  `remote: "cached"` answers from the owner answers already kept without asking an owner.
- **Limits.** `MaxResolveStages` is 8; `MaxResolveKeys` is 10 000, counted per request over
  every keyed stage. Contract 2 also has `MaxContinuedStages`, `MaxFlattenDepth`, `MaxReportPageSize`,
  `MaxReportedRows`, `Execution:ChainTimeoutMs`, `Cache:NegativeResolveTtlSeconds` and the sixteen
  `Explain` limits. Health publishes 41 limit entries, the schema's `limits` fifteen.
- **Requests.** A contract 2 request with a top-level member other than `entityType`, `variables`,
  `pipeline` and `strict` is refused with `UNKNOWN_REQUEST_MEMBER`. A resolve path
  under a collection that is not unwound is refused with `RESOLVE_ON_COLLECTION` unless it says
  `elements` (under two or more such collections, `UNWIND_ORDER`).
  A condition nests at most 32 levels of `and`, `or`, `not` and `any` (`MAX_CONDITIONS_EXCEEDED`).
  A strict report page beyond `MaxPageSize` joins at most
  `MaxPageSize` × `MaxLookupLimit` × `MaxLookupStages` lookup rows (`PAGE_SIZE_EXCEEDED`).
  `Limits:MaxFlattenDepth` is clamped to 12. A remote resolve's `filter` may hold a `$var`:
  the variable is substituted before the owner is called.
- **Diagnostics.** A lookup over its `limit` is `LOOKUP_TRUNCATED`; a key
  more than one record holds, where the keyed fetch sees it, is `RESOLVE_AMBIGUOUS`.
- **Rows and model.** A polymorphic value renders the members of the variant it is stored as;
  an interface-typed member whose implementations are registered is an object.
  A polymorphic entity or type whose subclasses have class maps (or `[BsonKnownTypes]`) is
  modelled as its declared class with the subclasses as variants, their own members marked `onlyFor`;
  a subclass without a class map is the log-only build finding
  `polymorphic-subtype-unregistered`, so a host with class hierarchies may log such findings.
- **Cursors.** A member enters the cursor fingerprint only when a request sets it, so a cursor of a
  request that does not use it stays valid.
- **Hosts serving other services** route their internal batch and explain through the query
  service's internal-call overloads, which alone accept `keyedBy`; a remote client reports owners'
  shallow health through `IRemoteOwnerInfo` and implements `ExplainAsync`. The Simplic base package
  does all three.
- **Codes.** Contract 2 has eleven error codes of its own (`UNKNOWN_REQUEST_MEMBER`, `UNKNOWN_VARIANT`, `FLATTEN_NOT_RECURSIVE`,
  `LOOKUP_ON_NOT_ENTITY`, `NOT_CONTINUABLE`, `UNION_CARDINALITY_MISMATCH`, `RESOLVE_ON_COLLECTION`, `RESOLVE_TARGET_NOT_DECLARED`,
  `RESOLVE_PARENT_NOT_ITEM`, `MAX_CONTINUED_STAGES_EXCEEDED`,
  `PAGE_INCOMPLETE`) and five diagnostics (`RESOLVE_MISSING`, `RESOLVE_AMBIGUOUS`,
  `RESOLVE_TRUNCATED`, `LOOKUP_TRUNCATED`, `UNWIND_DEPTH_TRUNCATED`).
- **The console** is served at `RoutePath` under the path base (see *Studio console*). A host that sets
  `RoutePath` with its path base spelled in (`/{ApiName}/{ApiVersion}/oxql`)
  serves it under the doubled path; set `RoutePath` to `/oxql`.
  `OxQLStudioOptions.EnableExplain` defaults to `true`, as the engine's explain does.
- **Resolve outcomes do not depend on the page, the cache or the executor.** Under `strict` or an
  `onMissing` other than `null` a remote resolve onto a non-key field is grouped per key;
  an inline resolve onto a non-key field is flagged
  `ambiguous` in the aggregate; an inline resolve with a `filter` stays inline under `strict`.
  A strict request refuses a lookup or `flatten` cut that a later
  match hides. A join loads what the query reads under its alias (see below). The stage index in
  every diagnostic is the caller's, also after a stage that binds to nothing (an empty `match`).
- **Batches.** A batch's `maxTimeMs` bounds the whole batch, not each query; the keyed fetch sends
  it a tenth (at most 250 ms) below its own wait. A batch member other than `queries` and
  `maxTimeMs` is `UNKNOWN_REQUEST_MEMBER` under contract 2.
- **Public API.** Beyond the entry points listed in *Upgrading from 1.x*, for code outside the
  package:
  - `IOxQLQueryService.ExplainAsync` takes an `ExplainRequest` (a `QueryRequest` converts to one);
    `ExplainAsync(QueryRequest)` is a default member. `BatchAsync(batch,
    internalCall, …)` and `ExplainAsync(request, internalCall, …)` default to the public form and throw
    `NotSupportedException` for an internal call.
    `IQueryEngine.ExplainAsync` takes an `ExplainRequest`.
  - `IRemoteQueryClient` has a default `ExplainAsync` and a default `ExplainBatchAsync` (the checks of
    one explain round as one call; its default asks check by check), and `IOxQLQueryService` and
    `IQueryEngine` a default `ExplainBatchAsync` for the owner's side of it; a client implements `IRemoteOwnerInfo` (with
    `RemoteOwnerInfo`) to report owners' shallow health: `OwnerOf`, `OwnerOfAsync`,
    `ApiVersionOf`; `IEntityModelProvider` has a default `SchemaRevision`.
  - The `MongoQueryEngine` constructor takes an `OwnerFetchCache` and an `ExplainForwardCache`;
    `ReferenceDef.Targets` is required, and `TargetEntity`, `TargetField` and `IsRemote` derive from
    it; `EnumValueDef` has `Description`; the positional records
    of `OxQL.Core.Binding` (`BoundStage.*`, `ShapeNode.Remote`), `Shape.WithUnwound` and
    `BoundCanonical.Render`/`Fingerprint` are engine internals.
- **Request depth.** Hosts read request bodies to `OxQLJson.MaxDepth` (256), the depth explain's
  answers need; every host of a fleet runs the same limit, so a body one host accepts its owners
  read too. `AddOxQLAspNetCore` sets it on the host's MVC and minimal-API JSON options, so it applies
  to every JSON body the host reads, not only OxQL's.
- **Owners.** Every owner an origin reaches runs this package: an owner needs the internal
  routes (`internal/oxql/batch`, `internal/oxql/explain`), which only this package has. A service
  still on the version 1 package has no internal route, is not listed in `InternalHosts` and cannot
  be an owner; its entities stay unknown or unavailable to origins (`UNKNOWN_ENTITY`,
  `RESOLVE_UNAVAILABLE`). Listed by mistake, it is not reached: a run refuses with
  `RESOLVE_UNREACHABLE` and explain notes the part `REMOTE_UNCHECKED`. There is no probe and no
  gate.
- **Owner faults** reach the caller with a fixed text; an owner's exception detail never passes
  through another service.

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
  a join before a `group` that reads it, and a join whose alias a projection narrows stay where
  they were written. A join, local or remote, whose alias a projection drops and no later stage
  reads is not run, and the row does not carry the alias. The count pipeline never carries a
  join only the rows' display reads; explain shows the order the server runs.
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
