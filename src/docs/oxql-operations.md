# OxQL operations

For service developers, API callers and operators: how an answer is shaped when it is not rows,
which HTTP status each code travels under, every limit with its key and its reach across
services, the health and explain endpoints, and a smoke checklist for a deployed service. The
request grammar is in [`oxql-query-syntax.md`](oxql-query-syntax.md); what a request means is in
[`oxql-semantics.md`](oxql-semantics.md).

## Answers that are not rows

The query routes answer in one of two shapes when they do not answer rows.

**The refusal envelope** is the engine's own answer to a request it read: a JSON object with
`type`, `title` and `errors`, each error carrying `code`, `message` and, where they apply,
`stage` (the caller's stage index) and `path`. Every binding error of a request is in `errors`
at once. The HTTP status follows from `type` and is not repeated in the body:

| `type` | HTTP | meaning |
|---|---|---|
| `validation_error` | 400, 413 for `REQUEST_TOO_LARGE` | the caller's request is wrong; sending it again will not help |
| `access_denied` | 403 | the request carries no organisation, or the entity has no organisation member |
| `not_executable` | 422 | well-formed, but this host cannot execute it now: a remote owner refused or is missing, a semi-join is too large, the server ran out of memory |
| `timeout` | 504 | an aggregate exceeded its time ceiling |
| `internal_error` | 500 | an engine fault; the message names the correlation id of the log line, and carries the detail only when the host sets `IncludeErrorDetails` |

The engine turns every exception on the query path into an `INTERNAL_ERROR` envelope, so a 500
from the query routes without an envelope did not come from the engine.

**Framework responses** come from the host's ASP.NET Core pipeline before the engine sees the
request. They carry no refusal envelope; most are `application/problem+json`:

| status | cause |
|---|---|
| 400 ProblemDetails | the body cannot be read into the request model: not JSON; `entityType` or `pipeline` missing or `null`; a pipeline element that is a number, string or array; a condition that is not an object; `and`/`or` that is not an array; `not` that is not an object; an aggregate that is not an object; a `group.by` or `sort` of the wrong JSON type; a non-integral number where an integer is read |
| 401, 403 | authentication or the authorization policy failed, when the host registered `RequireAuthorization` (`/oxql/health` is always anonymous) |
| 404 | `POST /oxql/explain` while `Explain:Enabled` is false |
| 413 | a body of undeclared length over `Limits:MaxRequestBytes`, cut off by the server; a body with a declared length gets the `REQUEST_TOO_LARGE` envelope |
| 415 | the request is not `application/json` |

A client that maps statuses to messages treats a 400 ProblemDetails as a caller error, like a
`validation_error`, never as an unreachable service. A pipeline element that is `null` and a
`null` entry of a batch are refusals (`UNKNOWN_STAGE`), not ProblemDetails.

### Codes

Codes are the contract; messages may change. Every code below is refused inside the envelope of
the listed status.

| code | HTTP | typical cause |
|---|---|---|
| `UNKNOWN_ENTITY` | 400 | `entityType` or a lookup's `from` is not an entity id of this host (case-sensitive under contract 2) |
| `INVALID_PATH` | 400 | an empty segment, a segment starting with `$`, more than 64 segments, a segment over 256 characters, a control character |
| `UNKNOWN_PATH` | 400 | not a path of the shape at that stage: misspelled, removed by a projection, not an output of the group, a member of a group output, an alias written after an inclusion projection, `$default` after a group or unwind |
| `NOT_STORED` | 400 | the member is in the wire view only; it may be projected, nothing else |
| `NOT_FILTERABLE` | 400 | a condition or group key on an object, collection, dictionary or `unknown` member, or on an addon key that is undefined, retired or of kind `object` |
| `NOT_SORTABLE` | 400 | a sort on a path under a collection (a lookup alias included), or on a non-scalar or `unknown` member |
| `NOT_A_COLLECTION` | 400 | `unwind` of a member that is not a collection, or after a group |
| `UNWIND_ORDER` | 400 | an inner collection unwound before its outer one |
| `ALIAS_COLLISION` | 400 | an alias equal to a member's wire or storage name or an earlier alias; two group outputs of one name; an unwind's `as` equal to its `includeIndex` |
| `INVALID_ALIAS` | 400 | an alias that is not a plain identifier, is `_id`, starts with `__` or ends in `__arr` |
| `INVALID_OPERAND` | 400 | the wrong JSON kind for the member; an operator the kind does not take (`gt` on a `bool`); `null` with an ordered or text operator; an array without `in`/`nin`; a text operator on a single-character member; a text too long for the server's pattern limit; `exists` without a boolean |
| `UNKNOWN_ENUM_MEMBER` | 400 | an enum name or number the enum does not declare; a value outside an addon key's closed list |
| `OPERAND_NOT_ARRAY` | 400 | `in` or `nin` without an array |
| `DECIMAL_TEXT_NOT_ORDERABLE` | 400 | `gt gte lt lte` on a decimal member whose declared storage is text |
| `UNBOUND_VARIABLE` | 400 | `{ "$var": … }` names a variable the request does not bind |
| `INVALID_VARIABLE` | 400 | a variable holds an object, or an array where an element is expected |
| `UNKNOWN_OPERATOR` | 400 | an operator outside the list (they are case-sensitive) |
| `EMPTY_LOGICAL_GROUP` | 400 | an empty `and`, `or` or `not`; a condition that names neither a path nor a group |
| `OPTION_NOT_APPLICABLE` | 400 | an unknown option; `caseSensitive` on an ordered operator, `regex`, a non-string member or under contract 1; `caseSensitive` and `ignoreCase` disagreeing; an exact sort in a request that folds elsewhere |
| `INVALID_REGEX` | 400 | a backreference, a quantifier over a quantified group, a pattern .NET cannot parse; or, from execution, a pattern the server's PCRE2 rejects |
| `REGEX_TOO_LONG` | 400 | a pattern longer than `RegexMaxLength` characters or 32 764 UTF-8 bytes |
| `ANY_NOT_APPLICABLE` | 400 | `any` on a collection of scalars, an unwound collection or one under another collection |
| `UNKNOWN_STAGE` | 400 | a stage object with no key, several keys or an unknown key; a `null` stage; a `null` query in a batch |
| `UNKNOWN_STAGE_MEMBER` | 400 | an unknown member of a stage or of a sort entry's object form; a stage whose value is `null`; a sort entry with more than one key or none |
| `STAGE_AFTER_PAGE` | 400 | a stage after `page` |
| `MULTIPLE_PAGE_STAGES` | 400 | two `page` stages |
| `MIXED_PROJECTION` | 400 | inclusion and exclusion in one projection (other than `"id": 0`); an empty projection |
| `GROUP_ON_COLLECTION` | 400 | a group key or aggregate argument under a collection that is not unwound |
| `UNKNOWN_AGG_FUNCTION` | 400 | an aggregate outside `count countDistinct sum avg min max first last push` |
| `INVALID_AGGREGATE_ARGUMENT` | 400 | `sum`/`avg` over a non-numeric argument; a missing argument; an argument object naming no path, variable, literal or operator; `dateTrunc` over a non-date |
| `INVALID_DATE_TRUNC_UNIT` | 400 | a unit outside `year quarter month week day hour minute second`; a `weekStart` that is not a day |
| `INVALID_TIMEZONE` | 400 | a `timezone` that is not an IANA (or Windows) zone id |
| `INVALID_SORT_DIRECTION` | 400 | a direction other than `asc`/`desc`; the object form without `direction`; the object form under contract 1 |
| `LOOKUP_NOT_DECLARED` | 400 | the child's `path` declares no reference to the current entity, or the referenced key is not stored |
| `RESOLVE_NOT_DECLARED` | 400 | the `resolve` path declares no reference, or the target key is not stored |
| `RESOLVE_NOT_FILTERABLE` | 400 | a condition on a remote resolve alias itself (`{"veh":{"eq":null}}`, `{"veh":{"exists":true}}`); the owner is not called. A condition on a member of the alias (`veh.matchCode`) is a semi-join |
| `RESOLVE_NOT_SORTABLE` | 400 | a sort on a path under a remote alias |
| `MAX_PIPELINE_STAGES_EXCEEDED` … `MAX_VARIABLES_EXCEEDED` | 400 | the matching limit (see *Limits*) |
| `INVALID_PAGE_LIMIT` | 400 | `limit` below 1, a negative `offset`, a cursor together with an offset, a count cap that is not a positive integer |
| `PAGE_SIZE_EXCEEDED` | 400 | `limit` above `MaxPageSize` |
| `LOOKUP_LIMIT_EXCEEDED` | 400 | a lookup `limit` below 1 or above `MaxLookupLimit` |
| `MAX_OFFSET_EXCEEDED` | 400 | `offset` above `MaxOffset` |
| `BATCH_TOO_LARGE` | 400 | a batch of more than `MaxBatchQueries` queries; the whole batch is refused |
| `REQUEST_TOO_LARGE` | 413 | a body over `MaxRequestBytes`, refused before it is read |
| `CURSOR_INVALID` | 400 | a cursor from another pipeline, organisation or signing key, or altered |
| `ACCESS_DENIED` | 403, or 400 | 403: no organisation in the request, or the entity has no root `organizationId`. 400 (inside a `validation_error`): a lookup child or resolve target without one. Test the code, not the status |
| `RESOLVE_UNAVAILABLE` | 422 | a remote resolve or semi-join on a host without a remote query client; a semi-join whose owner did not answer or answered with an HTTP error |
| `RESOLVE_REFUSED` | 422 | the owner refused its part (its own codes follow in `errors`), or answered a row without the key it was asked for |
| `SEMI_JOIN_TOO_LARGE` | 422 | a condition under a remote alias selects more than `MaxSemiJoinIds` rows of the owner |
| `QUERY_TOO_EXPENSIVE` | 422 | a sort or group over the server's memory limit with `AllowDiskUse` off; a `push` or `countDistinct` value over the server's size limits |
| `QUERY_TIMEOUT` | 504 | an aggregate exceeded `Execution:MaxTimeMs` or the batch's `maxTimeMs` |
| `INTERNAL_ERROR` | 500 | any other fault |
| `LEGACY_STAGE_UNSUPPORTED` | 400 | contract 1 only: a v1 `lookup` or `resolve`, an unknown stage member, the number form of `includeTotalCount` |

Diagnostics never refuse; they travel in `diagnostics` of a successful answer:

| code | `params` | when |
|---|---|---|
| `ENTITY_ID_RETIRED` | `currentId` | the request named a retired entity id |
| `TOTAL_COUNT_CAPPED` | `cap` | more rows match than the count cap in force; `totalCount` is the cap |
| `RESOLVE_TIMEOUT` | `service`, `aliases` | a remote owner did not answer within the resolve budget; the aliases are `null` on this page |
| `RESOLVE_UNREACHABLE` | `service`, `aliases` | a remote owner was not reached, or answered with an HTTP error (the message names the status) |
| `RESOLVE_PARTIAL` | `alias`, `keys`, `max` | a remote resolve needed more than `MaxResolveKeys` keys; the rest are `null`. Dormant while `MaxPageSize` ≤ `MaxResolveKeys` |
| `SORT_ON_ADDON` | — | a sort on an addon key, whose values may be stored in several types |
| `REGEX_UNANCHORED` | — | a `regex` that does not start with `^` or `\A` scans every value |
| `DECIMAL_TEXT_EXCLUDED` | — | an ordered comparison on a decimal covered the Decimal128 rows only; rows stored as text are outside it |

### Batch

`POST /oxql/batch` answers 200 with `{ "results": [ … ] }`, one entry per query in order, unless
the batch itself is refused: 400 `BATCH_TOO_LARGE`, 413 `REQUEST_TOO_LARGE`, or a framework
response (a body without a `queries` array is a 400 ProblemDetails). An entry is either a success body (it has `items`)
or a refusal envelope (it has `type`); an entry carries no status of its own, so a caller derives
it from `type` with the table above. Queries run one after another on the host; a batch of
`n` queries may take up to `n` times the time ceiling, and `maxTimeMs` lowers the ceiling of
every query in it, never raises it.

## Limits

Every limit is bound from the host's `OxQL` section and brought into range when the engine is
registered: a value below 1 is raised to 1 (`MaxOffset` to 0, which turns offset jumps off),
`DefaultPageSize` and `ResolveKeyChunk` are clamped to `MaxPageSize`, `MaxSemiJoinIds` to
`MaxOffset`, and each adjustment is logged as a warning when the engine is built. All nineteen
`Limits` values are published under `limits` on `GET /oxql/health`. The twelve a caller checks a
request against before sending it are also published in the schema document's `limits` by the
Simplic base package (column */schema*).

| key (`OxQL:`) | default | enforced by | code | /schema | cross-service reach |
|---|---|---|---|---|---|
| `Limits:MaxPageSize` | 500 | binder, `page.limit` | `PAGE_SIZE_EXCEEDED` | yes | A resolve chunk and a semi-join page are asked of the owner as one page: the owner's `MaxPageSize` must be at least this host's `ResolveKeyChunk` and `min(MaxPageSize, MaxSemiJoinIds)`, or the owner refuses and the caller answers 422 `RESOLVE_REFUSED` |
| `Limits:DefaultPageSize` | 100 | binder, a page without `limit` or no page stage | — | yes | — |
| `Limits:MaxPipelineStages` | 20 | binder; the scope stage does not count | `MAX_PIPELINE_STAGES_EXCEEDED` | yes | the engine's own owner queries have at most four stages |
| `Limits:MaxLookupStages` | 5 | binder | `MAX_LOOKUP_STAGES_EXCEEDED` | yes | — |
| `Limits:MaxUnwindStages` | 5 | binder | `MAX_UNWIND_STAGES_EXCEEDED` | yes | — |
| `Limits:MaxResolveStages` | 2 | binder, local and remote | `MAX_RESOLVE_STAGES_EXCEEDED` | yes | — |
| `Limits:MaxGroupFields` | 20 | binder, keys and aggregates together | `MAX_GROUP_FIELDS_EXCEEDED` | yes | — |
| `Limits:MaxProjectionFields` | 500 | binder | `MAX_PROJECTION_FIELDS_EXCEEDED` | yes | a remote `select` becomes the owner's projection and is checked against the owner's value |
| `Limits:MaxConditions` | 200 | binder; leaf conditions, `any` members, lookup and local resolve filters, and semi-join conditions | `MAX_CONDITIONS_EXCEEDED` | no | a remote resolve's `filter` is bound, and counted, by the owner |
| `Limits:MaxVariables` | 64 | binder | `MAX_VARIABLES_EXCEEDED` | no | — |
| `Limits:MaxOffset` | 5 000 | binder, `page.offset`; not an offset cursor after `group` or `unwind` | `MAX_OFFSET_EXCEEDED` | yes | A semi-join reads the owner's matching ids page by page with `offset` up to this host's `MaxSemiJoinIds`; the owner's `MaxOffset` must be at least that, or the owner refuses (422 `RESOLVE_REFUSED`) |
| `Limits:CountCap` | 100 000 | compiler: the count stops at the cap | diagnostic `TOTAL_COUNT_CAPPED` | no | a semi-join asks the owner for a count with its first page; a count the owner capped only makes the walk go page by page |
| `Limits:MaxSemiJoinIds` | 5 000, never above `MaxOffset` | remote resolver | 422 `SEMI_JOIN_TOO_LARGE` | no | refused after the first owner page when the owner's count already exceeds it |
| `Limits:ResolveKeyChunk` | 500, never above `MaxPageSize` | remote resolver: keys per owner query | — | no | the owner's `MaxPageSize` and `MaxRequestBytes` bound it |
| `Limits:MaxResolveKeys` | 2 000 | remote resolver: keys per resolve stage and page | diagnostic `RESOLVE_PARTIAL` | no | — |
| `Limits:MaxRequestBytes` | 262 144 | request-size filter, before the body is read | 413 `REQUEST_TOO_LARGE` | no | guards the owner's internal batch route too where the host puts the same filter on it (the Simplic base package does); an owner answering 413 is an unreachable owner to the caller |
| `Limits:MaxBatchQueries` | 10 | query service | `BATCH_TOO_LARGE` | yes | A host sends its owner calls in batches of its own `MaxBatchQueries`. An owner configured lower refuses the whole batch with HTTP 400: resolved aliases are `null` with `RESOLVE_UNREACHABLE`, a semi-join is 422 `RESOLVE_UNAVAILABLE`. Keep the value equal across services |
| `Limits:RegexMaxLength` | 200 | binder | `REGEX_TOO_LONG` | yes | a `regex` under a remote alias is checked by the owner |
| `Limits:MaxLookupLimit` | 100 | binder: the largest lookup `limit` and the default when none is given | `LOOKUP_LIMIT_EXCEEDED` | yes | — |
| `Execution:MaxTimeMs` | 10 000, clamped to 1–60 000 | `maxTimeMS` of the page and the count aggregate; a batch's `maxTimeMs` only lowers it | 504 `QUERY_TIMEOUT` | no | the time left of this budget, capped by `ResolveTimeoutMs`, is sent to the owner as the batch's `maxTimeMs` |
| `Execution:ResolveTimeoutMs` | 2 000, never above `MaxTimeMs` | remote resolver: one owner call | `RESOLVE_TIMEOUT` (resolve), 422 `RESOLVE_UNAVAILABLE` (semi-join) | no | the whole semi-join phase is also bounded by the request's remaining budget |
| `Execution:AllowDiskUse` | `true` | aggregate option | 422 `QUERY_TOO_EXPENSIVE` when `false` | no | — |
| `Execution:SlowQueryMs` | 1 000 (`0` off) | warning log line for a slower request | — | no | — |
| `Cache:ResolveTtlSeconds` / `ResolveCacheMaxEntries` | 60 / 50 000 | resolved remote rows and semi-join ids, per organisation | — | no | an owner's change is seen by callers within the TTL |
| `Cache:AddonDefinitionTtlSeconds` | 30 | the host's addon definition cache | — | no | — |
| `Cache:HealthProbeTtlSeconds` | 10 | reachability measurements behind `/oxql/health` | — | no | — |

Fixed bounds, not configurable:

| bound | value | code |
|---|---|---|
| path segments / segment length | 64 / 256 characters | `INVALID_PATH` |
| pattern size the server compiles | 32 764 UTF-8 bytes, for a caller's `regex` and for a text the engine turns into a pattern | `REGEX_TOO_LONG`, `INVALID_OPERAND` |
| model depth / paths per entity | 32 segments / 20 000 paths; deeper members are not in the model | model build finding |
| `Execution:MaxTimeMs` ceiling | 60 000 ms | — |
| health reachability probe | 2 s per service | the service counts as not reachable |
| listed indexes for the explain advisory | cached 60 s per collection | — |

## `GET /oxql/health`

Anonymous, always 200, and never waits for another service:

```jsonc
{
  "status": "healthy",                    // "degraded" when a referenced service is not configured or was last measured unreachable
  "service": "oxql",
  "engine": { "version": "2.0.0.0", "contract": 2 },
  "capabilities": ["batch", "group.page", "page.offset", "any", "resolve.remote", "semiJoin", "compat.v1"],
  "limits": {
    "maxPageSize": 500, "defaultPageSize": 100, "maxPipelineStages": 20, "maxLookupStages": 5,
    "maxUnwindStages": 5, "maxResolveStages": 2, "maxGroupFields": 20, "maxProjectionFields": 500,
    "maxConditions": 200, "maxVariables": 64, "maxOffset": 5000, "countCap": 100000,
    "maxSemiJoinIds": 5000, "resolveKeyChunk": 500, "maxResolveKeys": 2000, "maxRequestBytes": 262144,
    "maxBatchQueries": 10, "regexMaxLength": 200, "maxLookupLimit": 100
  },
  "remote": [ { "service": "vehicle", "configured": true, "reachable": true } ]
}
```

- `engine.version` is the version of the `OxQL.AspNetCore` assembly the host runs.
- `capabilities`: `batch`, `group.page`, `page.offset` and `any` always; `resolve.remote` and
  `semiJoin` when the host installed a remote query client; `explain` when `Explain:Enabled`;
  `compat.v1` while `Compat:Enabled`.
- `limits` are the values in force after registration adjusted them, not the configured ones.
- `remote` lists every service the model references remotely, from a root member, a member of
  an embedded object or a member of a collection element alike, and is absent when the host has
  no remote query client or the request says `?shallow=true`. `reachable` is the last
  measurement, taken at most once per `Cache:HealthProbeTtlSeconds` in the background, and
  `null` before the first one has finished or for a service that is not configured; a `null`
  does not degrade `status`.
- `?shallow=true` starts no measurement. It is the form one host uses to ask another, so a probe
  never sets off the probed service's own probes.

## `POST /oxql/explain`

404 unless `Explain:Enabled`. Takes the body of `POST /oxql/query`, binds and compiles it,
answers refusals exactly as the query route would, and returns no rows:

```jsonc
{
  "bound": {                                  // the canonical form the cursor fingerprint is taken over
    "entity": "logistics.shipment",
    "scope": { "path": "OrganizationId", "organisation": "…" },
    "stages": [ … ],                          // storage paths and typed operands
    "paging": "keyset",                       // or "offset"
    "page": { "limit": 50, "offset": 0, "cursor": null, "includeTotalCount": true }
  },
  "stages": [ { "$match": { … } }, { "$sort": { … } }, { "$limit": 51 } ],   // the page aggregate, in the order the server runs it
  "count": [ { "$match": { … } }, { "$limit": 100001 }, { "$count": "n" } ], // when a count was requested
  "collation": { "locale": "de", "strength": 1 },                           // when something folds
  "advisory": [
    { "field": "OrganizationId", "used": true, "index": "org_number" },
    { "field": "sort:Number,_id", "used": false, "index": "org_number", "note": "the index covers the sort fields but not the _id tie-breaker; the sort runs in memory" },
    { "field": "lookup:documents", "used": true, "index": "…", "note": "…" },
    { "field": "collation", "used": null, "note": "…" }
  ],
  "diagnostics": [ … ]
}
```

- The page aggregate fetches `limit + 1` rows; the extra one decides `hasNextPage`. A join that
  runs after the page appears after the `$limit`.
- The advisory matches the leading `$match` (scope, cursor predicate and the first caller
  condition, which the server coalesces) and the sort against `listIndexes`, and reads the
  server's own explain for every `$lookup`. That explain runs the page aggregate once under the
  time ceiling; a pipeline without a lookup is never executed. The advisory is absent when the
  host provides no index source.

## Staging smoke checklist

Run after an engine or base package upgrade, against one deployed service, with a token of a
test organisation. `{api}` is the service's base URL (for example
`https://staging.example.com/myservice-api/v1`), `{entity}` one of its entities, `{member}` a
string member of it and `{ref}` a member declaring a reference into another service. Every
query request sends `X-OxQL-Contract: 2`.

1. `GET {api}/oxql/health` without a token → 200, `engine.contract` 2, the expected
   `engine.version`, `status` `healthy`, every `remote` entry `configured: true` and, after a
   second call ten seconds later, `reachable: true`; `explain` absent from `capabilities` unless
   intended.
2. `GET {api}/schema` → 200; its `limits` equal the corresponding values on `/oxql/health`; the
   entity ids you expect are there.
3. `POST {api}/oxql/query` with `{ "entityType": "{entity}", "pipeline": [ { "page": { "limit": 1 } } ] }`
   and no token → 401 when the host requires authorization; with the token → 200.
4. A filtered, sorted, counted page:
   `[ { "match": { "{member}": { "startsWith": "a" } } }, { "sort": [ { "{member}": "asc" } ] }, { "page": { "limit": 20, "includeTotalCount": true } } ]`
   → 200, rows starting with `a` or `A` (accents folded), `totalCount` present.
5. The next page: repeat 4 with `"cursor": "<nextCursor>"` and no count, several times → no row
   repeats; with more than one replica this also proves the replicas share the signing key.
6. A refusal: `{ "match": { "noSuchMember": { "eq": 1 } } }` → 400 `validation_error` with
   `UNKNOWN_PATH` and `stage` 0.
7. A remote resolve: `[ { "resolve": { "path": "{ref}", "as": "target" } }, { "page": { "limit": 20 } } ]`
   → 200, `target` filled on rows whose reference is set, no `RESOLVE_UNREACHABLE` or
   `RESOLVE_TIMEOUT` diagnostic. A condition under the alias
   (`{ "match": { "target.id": { "eq": "<an id>" } } }`) → 200, not 422.
8. An addon filter: read the organisation's definitions with `GET {api}/schema/addons`, then
   `{ "match": { "addon.<defined key>": { "exists": true } } }` → 200; an undefined key under
   `eq` → 400 `NOT_FILTERABLE`.
9. Organisation isolation: take an `id` returned in step 4 and ask for it
   (`{ "match": { "id": { "eq": "<id>" } } }`) with a token of a second organisation → 200 with no
   rows. A token without an organisation → 403 `ACCESS_DENIED`.
10. One internal batch call, as another service would make it: `POST` the host's internal batch
    route (for the Simplic base package `{api}/internal/oxql/batch`) with the cluster's internal
    api key and an organisation header, body `{ "queries": [ { "entityType": "{entity}", "pipeline": [ { "page": { "limit": 1 } } ] } ] }`
    → 200 with one result; without the key → 401.
11. `POST {api}/oxql/explain` → 404 unless explain is meant to be on.
12. The service log: no OxQL startup error, no option-adjustment warning you did not expect, and
    slow-query warnings at a rate you expect.
