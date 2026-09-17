# OxQL

A typed query engine over MongoDB for Simplic OxS services. A client posts a JSON pipeline
naming an entity; the engine binds every path and operand against the host's entity model,
applies the caller's organisation scope at every entry into an entity, compiles one
aggregate, executes it under guard rails, and answers rows in the same wire encoding the
service's REST responses use. This is **contract 2**; contract 1 requests are still served
through a compatibility mode for one release (see *Compatibility mode*).

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
- **References** are declarations only, never name inference: `[OxQLReference("<entity id>", field?)]` (`OxQL.Model.Attributes`) on the id member, or the base package's `[ReferenceId("<idProperty>")]` on the navigation property. A target on another host is a remote reference; the model marks it and the host must know the owner (`IRemoteQueryClient.IsConfigured`), otherwise the host logs an error and refuses to start in `Development`, `Local` and under CI.
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
| `GET /oxql/health` | Anonymous. `{ "status", "service": "oxql", "engine": { "version", "contract": 2 }, "capabilities": [ … ], "remote": [ { "service", "configured", "reachable" } ] }`. Capabilities: `batch`, `group.page`, `page.offset`, `any`, and with a remote client `resolve.remote`, `semiJoin`; `explain` and `compat.v1` when enabled. `status` is `degraded` when a referenced service is not configured or does not answer. |
| `POST /oxql/explain` | 404 unless `Explain:Enabled`. `{ "bound", "stages", "count"?, "advisory"?, "diagnostics"? }`: the bound pipeline in canonical form, the emitted page and count stages, and an index advisory read from `listIndexes` (cached per collection). |

Every request body is capped at `Limits:MaxRequestBytes` (413 `REQUEST_TOO_LARGE`).

## Contract header and compatibility mode

Clients send `X-OxQL-Contract: 2`. While `Compat:Enabled` is true (the default for the
compatibility release), a request **without** the header is contract 1: storage-spelled
paths are resolved against the folded shape, the v1 type-hint operands (`$date`, `$uuid`,
`$decimal`, …) are accepted, and rows come back in the v1 encoding (`_id`, storage names).
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
| `Limits:MaxPageSize` / `DefaultPageSize` | 500 / 100 | the page a request may ask for / gets without a limit |
| `Limits:MaxPipelineStages` | 20 | caller stages; the engine's scope stage does not count |
| `Limits:MaxLookupStages` / `MaxUnwindStages` / `MaxResolveStages` | 5 / 5 / 2 | |
| `Limits:MaxGroupFields` / `MaxProjectionFields` | 20 / 500 | |
| `Limits:MaxConditions` / `MaxVariables` | 200 / 64 | leaf conditions, lookup and resolve filters included |
| `Limits:MaxOffset` | 5 000 | the largest `offset`; beyond it a cursor |
| `Limits:CountCap` | 100 000 | above it `totalCount` is the cap and `totalCountCapped` true |
| `Limits:MaxSemiJoinIds` | 10 000 | a larger semi-join is refused, never truncated |
| `Limits:ResolveKeyChunk` / `MaxResolveKeys` | 500 / 2 000 | keys per remote call / per request |
| `Limits:MaxRequestBytes` / `MaxBatchQueries` / `RegexMaxLength` | 262 144 / 10 / 200 | |
| `Limits:MaxLookupLimit` | 100 | rows one lookup returns per parent |
| `Execution:MaxTimeMs` | 10 000 | `maxTimeMS` on every aggregate, clamped to 60 000; a timeout is 504 `QUERY_TIMEOUT` |
| `Execution:ResolveTimeoutMs` | 2 000 | budget of one remote call, clamped to `MaxTimeMs` |
| `Execution:AllowDiskUse` | server default | when `false`, a spill is 422 `QUERY_TOO_EXPENSIVE` |
| `Representation:GuidTolerant` | `false` | also match legacy subtype 3 and string guids |
| `Representation:DecimalMode` | `tolerant` | match Decimal128 and string decimals; `typed` after a migration |
| `Cache:ResolveTtlSeconds` / `ResolveCacheMaxEntries` | 60 / 50 000 | resolved remote rows |
| `Cache:AddonDefinitionTtlSeconds` | 30 | a host's addon definition cache |
| `Cursor:SigningKey` | — | required; cursors are HMAC-signed with a key derived from it |

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

## Remote resolve

`resolve` follows a declared reference. A local target compiles to an indexed `$lookup`; a
remote target is fetched after the page is fixed, per owning service, in one call to the
owner's internal batch route through `IRemoteQueryClient` (keys chunked by
`Limits:ResolveKeyChunk`, results cached per entity, organisation, key, `select` and `filter`
for `Cache:ResolveTtlSeconds`). A `match` on a path under a remote alias is a semi-join: the
owner is asked for the matching ids first. An owner refusal becomes the caller's 422
`RESOLVE_REFUSED` with the owner's errors; a timeout or an unreachable owner yields `null`
under the alias and a `RESOLVE_TIMEOUT` / `RESOLVE_UNREACHABLE` diagnostic; a semi-join whose
owner does not answer is 422 `RESOLVE_UNAVAILABLE`.

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

## Requirements

.NET 10, MongoDB.Driver 3.9, MongoDB 6.0 or later (measured on 8.0).
