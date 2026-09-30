# OxQL operations

For service developers, API callers and operators: how an answer is shaped when it is not rows,
which HTTP status each code travels under, every limit with its key and its reach across
services, the health and explain endpoints, how joins reach other services (the keyed fetch and
remote continuation), the caches, and a smoke checklist for a deployed service. The
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
| `not_executable` | 422 | well-formed, but this host cannot execute it now: a remote owner refused, is missing or runs an engine too old for the request, a semi-join is too large, the server ran out of memory; or a `strict` request lost data (its `errors` are the diagnostics that lost it) |
| `timeout` | 504 | an aggregate exceeded its time ceiling |
| `internal_error` | 500 | an engine fault; the message names the correlation id of the log line, and carries the detail only when the host sets `IncludeErrorDetails` |

The engine turns every exception on the query path into an `INTERNAL_ERROR` envelope, so a 500
from the query routes without an envelope did not come from the engine.

**Framework responses** come from the host's ASP.NET Core pipeline before the engine sees the
request. They carry no refusal envelope; most are `application/problem+json`:

| status | cause |
|---|---|
| 400 ProblemDetails | the body cannot be read into the request model: not JSON; `entityType` or `pipeline` missing or `null`; a pipeline element that is a number, string or array; a condition that is not an object; `and`/`or` that is not an array; `not` that is not an object; an aggregate that is not an object; a `group.by` or `sort` of the wrong JSON type; a non-integral number where an integer is read; an explain envelope with a member other than `query`, `describe`, `remote`, `include`, a `remote` other than `check`/`skip`, or an `include` other than `indexes` |
| 401, 403 | authentication or the authorization policy failed, when the host registered `RequireAuthorization` (`/oxql/health` is always anonymous) |
| 404 | `POST /oxql/explain` while `Explain:Enabled` is false (it is on by default since 2.1) |
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
| `INVALID_OPERAND` | 400 | the wrong JSON kind for the member; an operator the kind does not take (`gt` on a `bool`); `null` with an ordered or text operator; an array without `in`/`nin`; a text operator on a single-character member; a text too long for the server's pattern limit; `exists` without a boolean; `is` on a member without variants or under a collection that is not unwound, or with an operand that is not a name or a non-empty array of names |
| `UNKNOWN_ENUM_MEMBER` | 400 | an enum name or number the enum does not declare; a value outside an addon key's closed list |
| `OPERAND_NOT_ARRAY` | 400 | `in` or `nin` without an array |
| `DECIMAL_TEXT_NOT_ORDERABLE` | 400 | `gt gte lt lte` on a decimal member whose declared storage is text |
| `UNBOUND_VARIABLE` | 400 | `{ "$var": … }` names a variable the request does not bind |
| `INVALID_VARIABLE` | 400 | a variable holds an object, or an array where an element is expected |
| `UNKNOWN_OPERATOR` | 400 | an operator outside the list (they are case-sensitive); `is` under contract 1 |
| `UNKNOWN_VARIANT` | 400 | an `is` names a type that is neither a variant of the member's type nor its concrete base; the message lists the variants |
| `UNKNOWN_REQUEST_MEMBER` | 400 | a contract 2 request carries a top-level member other than `entityType`, `variables`, `pipeline`, `strict`; `keyedBy` on a public route |
| `EMPTY_LOGICAL_GROUP` | 400 | an empty `and`, `or` or `not`; a condition that names neither a path nor a group |
| `OPTION_NOT_APPLICABLE` | 400 | an unknown option; `caseSensitive` on an ordered operator, `regex`, a non-string member or under contract 1; `caseSensitive` and `ignoreCase` disagreeing; an exact sort in a request that folds elsewhere; options on `is`. Stage options where they do not apply: `limit` with lookup `first`, `elements` on a single value, `parentSelect` without `parentAs`, `forTarget` on a stage that is not continued under a union alias, or naming a target the alias lacks |
| `INVALID_REGEX` | 400 | a backreference, a quantifier over a quantified group, a pattern .NET cannot parse; or, from execution, a pattern the server's PCRE2 rejects |
| `REGEX_TOO_LONG` | 400 | a pattern longer than `RegexMaxLength` characters or 32 764 UTF-8 bytes |
| `ANY_NOT_APPLICABLE` | 400 | `any` on a collection of scalars, an unwound collection or one under another collection |
| `UNKNOWN_STAGE` | 400 | a stage object with no key, several keys or an unknown key; a `null` stage; a `null` query in a batch |
| `UNKNOWN_STAGE_MEMBER` | 400 | an unknown member of a stage or of a sort entry's object form; a stage whose value is `null`; a sort entry with more than one key or none; a 2.1 stage member of the wrong JSON kind (`elements` not `first`/`all`, `onMissing` not `null`/`report`/`refuse`); a malformed describe entry |
| `FLATTEN_NOT_RECURSIVE` | 400 | an unwind's `flatten` names a member that is not a collection of the same items as the unwound collection, or the collection holds no objects |
| `STAGE_AFTER_PAGE` | 400 | a stage after `page` |
| `MULTIPLE_PAGE_STAGES` | 400 | two `page` stages |
| `MIXED_PROJECTION` | 400 | inclusion and exclusion in one projection (other than `"id": 0`); an empty projection |
| `GROUP_ON_COLLECTION` | 400 | a group key or aggregate argument under a collection that is not unwound |
| `UNKNOWN_AGG_FUNCTION` | 400 | an aggregate outside `count countDistinct sum avg min max first last push` |
| `INVALID_AGGREGATE_ARGUMENT` | 400 | `sum`/`avg` over a non-numeric argument; a missing argument; an argument object naming no path, variable, literal or operator; `dateTrunc` over a non-date |
| `INVALID_DATE_TRUNC_UNIT` | 400 | a unit outside `year quarter month week day hour minute second`; a `weekStart` that is not a day |
| `INVALID_TIMEZONE` | 400 | a `timezone` that is not an IANA (or Windows) zone id |
| `INVALID_SORT_DIRECTION` | 400 | a direction other than `asc`/`desc`; the object form without `direction`; the object form under contract 1 |
| `LOOKUP_NOT_DECLARED` | 400 | the child's `path` declares no reference to the current entity (or to the entity of the `on` alias), or the referenced key is not stored |
| `LOOKUP_ON_NOT_ENTITY` | 400 | a lookup's `on` names a lookup array, an unwound element, a scalar or a group output |
| `NOT_CONTINUABLE` | 400 | on an alias that comes from an owner after the page, a stage other than a `resolve` or `lookup` (an `unwind`, a `group` key); a continued stage under an `elements: "all"` alias; a projection keeping a continued alias but dropping the alias it continues under; any continued stage under contract 1 |
| `RESOLVE_NOT_DECLARED` | 400 | the `resolve` path declares no reference (the message names its kind), the target key or items are not stored, or a case tests a sibling that is not stored |
| `RESOLVE_ON_COLLECTION` | 400 | a resolve path under a collection that is not unwound, without `elements` |
| `RESOLVE_TARGET_NOT_DECLARED` | 400 | a resolve's `target` is not a target of any case of the reference; the message lists them |
| `RESOLVE_PARENT_NOT_ITEM` | 400 | a resolve's `parentAs` on a reference whose (narrowed) targets include an entity rather than an item |
| `RESOLVE_NOT_FILTERABLE` | 400 | a condition on a remote resolve alias itself (`{"veh":{"eq":null}}`, `{"veh":{"exists":true}}`); the owner is not called. A condition on a member of a plain remote alias (`veh.matchCode`) is a semi-join; under a keyed, typed, item, converted, element-wise or continued alias any condition is refused, since the alias is joined after the page |
| `RESOLVE_NOT_SORTABLE` | 400 | a sort on a path under a remote or keyed alias |
| `MAX_PIPELINE_STAGES_EXCEEDED` … `MAX_VARIABLES_EXCEEDED`, `MAX_CONTINUED_STAGES_EXCEEDED` | 400 | the matching limit (see *Limits*) |
| `INVALID_PAGE_LIMIT` | 400 | `limit` below 1, a negative `offset`, a cursor together with an offset, a count cap that is not a positive integer |
| `PAGE_SIZE_EXCEEDED` | 400 | `limit` above `MaxPageSize` |
| `LOOKUP_LIMIT_EXCEEDED` | 400 | a lookup `limit` below 1 or above `MaxLookupLimit` |
| `MAX_OFFSET_EXCEEDED` | 400 | `offset` above `MaxOffset` |
| `BATCH_TOO_LARGE` | 400 | a batch of more than `MaxBatchQueries` queries; the whole batch is refused |
| `REQUEST_TOO_LARGE` | 413 | a body over `MaxRequestBytes`, refused before it is read |
| `CURSOR_INVALID` | 400 | a cursor from another pipeline, organisation or signing key, or altered |
| `ACCESS_DENIED` | 403, or 400 | 403: no organisation in the request, or the entity has no root `organizationId`. 400 (inside a `validation_error`): a lookup child or resolve target without one. Test the code, not the status |
| `RESOLVE_UNAVAILABLE` | 422 | a remote resolve or semi-join on a host without a remote query client (explain answers it as `valid: false`); a semi-join whose owner did not answer or answered with an HTTP error |
| `RESOLVE_REFUSED` | 422 | the owner refused its part (its own codes follow in `errors`, for a continued stage mapped to the caller's `stage` and `path` with `params.owner`), or answered a row without the key it was asked for |
| `OWNER_NOT_CAPABLE` | 422 | a continued stage or a grouped owner query for an owner whose health reports an engine older than 2.1; `params` `service`, `version`, `needs` (`"2.1"`). Nothing was sent |
| `PAGE_INCOMPLETE` | 422 | a `strict` request without `cursor` or `offset` matches more rows than its page holds; `params` `limit`, `max` |
| `SEMI_JOIN_TOO_LARGE` | 422 | a condition under a remote alias selects more than `MaxSemiJoinIds` rows of the owner |
| `QUERY_TOO_EXPENSIVE` | 422 | a sort or group over the server's memory limit with `AllowDiskUse` off; a `push` or `countDistinct` value over the server's size limits |
| `QUERY_TIMEOUT` | 504 | an aggregate exceeded `Execution:MaxTimeMs` or the batch's `maxTimeMs` |
| `INTERNAL_ERROR` | 500 | any other fault |
| `LEGACY_STAGE_UNSUPPORTED` | 400 | contract 1 only: a v1 `lookup` or `resolve`, an unknown stage member (the 2.1 stage members included), `strict`, the number form of `includeTotalCount` |

Diagnostics travel in `diagnostics` of a successful answer. Those marked *loss* refuse a `strict`
request instead (422 `not_executable`, the diagnostic as the error); `RESOLVE_MISSING` refuses by its
stage's effective `onMissing`:

| code | `params` | when |
|---|---|---|
| `ENTITY_ID_RETIRED` | `currentId` | the request named a retired entity id |
| `TOTAL_COUNT_CAPPED` | `cap` | more rows match than the count cap in force; `totalCount` is the cap |
| `RESOLVE_TIMEOUT` (loss) | `service` (`null`: this host), `aliases` | an owner did not answer within its budget; the aliases are `null` on this page |
| `RESOLVE_UNREACHABLE` (loss) | `service`, `aliases` | an owner was not reached, or answered with an HTTP error (the message names the status) |
| `RESOLVE_PARTIAL` (loss) | `alias`, `keys`, `max`; or `alias`, `keys`, `unanswered`, `service` | the request needed more owner keys than `MaxResolveKeys` and the rest were not asked for; or an owner answered a chunk with `hasNextPage`, and its open keys are `owner_unanswered` and not cached |
| `RESOLVE_MISSING` (loss by `onMissing`) | `alias`, `count`, `truncated`, `rows` `[{ row, element?, key, outcome }]` | under `onMissing` `report` or `refuse`: references that resolved to `not_found`, `invalid_key` or `owner_unanswered`; one per stage; `rows` capped at `MaxReportedRows` |
| `RESOLVE_AMBIGUOUS` (loss) | as `RESOLVE_MISSING` | always: keys more than one record holds; the alias holds the first by target order, then key |
| `RESOLVE_TRUNCATED` (loss) | `alias`, `limit`, `rows` (a number) | an `elements: "all"` alias of some row held more than `MaxLookupLimit` targets |
| `LOOKUP_TRUNCATED` (loss) | `alias`, `limit`, `rows` (a number) | a lookup had more children for some parent than its `limit` |
| `UNWIND_DEPTH_TRUNCATED` (loss) | `path`, `depth`, `rows` | a `flatten` met items nested deeper than `MaxFlattenDepth` |
| `SORT_ON_ADDON` | — | a sort on an addon key, whose values may be stored in several types |
| `REGEX_UNANCHORED` | — | a `regex` that does not start with `^` or `\A` scans every value |
| `DECIMAL_TEXT_EXCLUDED` | — | an ordered comparison on a decimal covered the Decimal128 rows only; rows stored as text are outside it |

### Batch

`POST /oxql/batch` answers 200 with `{ "results": [ … ] }`, one entry per query in order, unless
the batch itself is refused: 400 `BATCH_TOO_LARGE`, 413 `REQUEST_TOO_LARGE`, or a framework
response (a body without a `queries` array is a 400 ProblemDetails). An entry is either a success body (it has `items`)
or a refusal envelope (it has `type`); an entry carries no status of its own, so a caller derives
it from `type` with the table above. Queries run one after another on the host; `maxTimeMs` bounds
the whole batch: each query runs under what is left of it (never above the host's own ceiling), so
the batch ends within it. A member the batch does not have (a batch-level `strict`, say) is a 400
`UNKNOWN_REQUEST_MEMBER` under contract 2.

## Limits

Every limit is bound from the host's `OxQL` section and brought into range when the engine is
registered: a value below 1 is raised to 1 (`MaxOffset` and `Cache:NegativeResolveTtlSeconds` to 0;
a `MaxOffset` of 0 turns offset jumps off), `DefaultPageSize` and `ResolveKeyChunk` are clamped to
`MaxPageSize`, `MaxSemiJoinIds` to `MaxOffset`, `MaxContinuedStages` to `MaxPipelineStages`,
`MaxFlattenDepth` to 12, and each adjustment is logged as a warning when the engine is built. All
twenty-three `Limits` values are published under `limits` on `GET /oxql/health`, with the effective
`chainTimeoutMs`, `negativeResolveTtlSeconds` and the three explain bounds (28 entries). The fifteen
a caller checks a request against before sending it are also published in the schema document's
`limits` by the Simplic base package (column */schema*).

| key (`OxQL:`) | default | enforced by | code | /schema | cross-service reach |
|---|---|---|---|---|---|
| `Limits:MaxPageSize` | 500 | binder, `page.limit` | `PAGE_SIZE_EXCEEDED` | yes | A resolve chunk and a semi-join page are asked of the owner as one page: the owner's `MaxPageSize` must be at least this host's `ResolveKeyChunk` and `min(MaxPageSize, MaxSemiJoinIds)`, or the owner refuses and the caller answers 422 `RESOLVE_REFUSED` |
| `Limits:DefaultPageSize` | 100 | binder, a page without `limit` or no page stage | — | yes | — |
| `Limits:MaxPipelineStages` | 20 | binder; the scope stage does not count | `MAX_PIPELINE_STAGES_EXCEEDED` | yes | an owner query holds the key match (or `keyedBy`), the filter, the continued stages, the projection and the page, and is checked against the owner's value |
| `Limits:MaxLookupStages` | 5 | binder | `MAX_LOOKUP_STAGES_EXCEEDED` | yes | — |
| `Limits:MaxUnwindStages` | 5 | binder | `MAX_UNWIND_STAGES_EXCEEDED` | yes | — |
| `Limits:MaxResolveStages` | 8 (2 before 2.1) | binder: resolve stages bound on this host, local and remote | `MAX_RESOLVE_STAGES_EXCEEDED` | yes | continued stages are counted by the owner that binds them, not here |
| `Limits:MaxContinuedStages` | 8, never above `MaxPipelineStages` | binder: stages continued under one keyed or remote alias | `MAX_CONTINUED_STAGES_EXCEEDED` | yes | the owner checks the forwarded stages against its own limits again |
| `Limits:MaxGroupFields` | 20 | binder, keys and aggregates together | `MAX_GROUP_FIELDS_EXCEEDED` | yes | — |
| `Limits:MaxProjectionFields` | 500 | binder | `MAX_PROJECTION_FIELDS_EXCEEDED` | yes | a remote `select` becomes the owner's projection and is checked against the owner's value |
| `Limits:MaxConditions` | 200 | binder; leaf conditions, `any` members, lookup and local resolve filters, and semi-join conditions; a condition also nests at most 32 levels of `and`, `or`, `not` and `any` (fixed, `Binder.MaxConditionDepth`) | `MAX_CONDITIONS_EXCEEDED` | no | a remote resolve's `filter` is bound, and counted, by the owner |
| `Limits:MaxVariables` | 64 | binder | `MAX_VARIABLES_EXCEEDED` | no | — |
| `Limits:MaxOffset` | 5 000 | binder, `page.offset`; not an offset cursor after `group` or `unwind` | `MAX_OFFSET_EXCEEDED` | yes | A semi-join reads the owner's matching ids page by page with `offset` up to this host's `MaxSemiJoinIds`; the owner's `MaxOffset` must be at least that, or the owner refuses (422 `RESOLVE_REFUSED`) |
| `Limits:CountCap` | 100 000 | compiler: the count stops at the cap | diagnostic `TOTAL_COUNT_CAPPED` | no | a semi-join asks the owner for a count with its first page; a count the owner capped only makes the walk go page by page |
| `Limits:MaxSemiJoinIds` | 5 000, never above `MaxOffset` | remote resolver | 422 `SEMI_JOIN_TOO_LARGE` | no | refused after the first owner page when the owner's count already exceeds it |
| `Limits:ResolveKeyChunk` | 500, never above `MaxPageSize` | keyed fetch: keys per owner query; a grouped query (`keyedBy`, two rows per key) carries at most `MaxPageSize / 2` keys | — | no | the owner's `MaxPageSize` and `MaxRequestBytes` bound it |
| `Limits:MaxResolveKeys` | 10 000 (2 000 before 2.1) | keyed fetch: keys asked of owners per request, over every keyed stage and target in stage order; keys the cache answers are free | diagnostic `RESOLVE_PARTIAL`; refused under `strict` | no | each owner applies its own to what it forwards further |
| `Limits:MaxRequestBytes` | 262 144 | request-size filter, before the body is read | 413 `REQUEST_TOO_LARGE` | no | guards the owner's internal batch route too where the host puts the same filter on it (the Simplic base package does); an owner answering 413 is an unreachable owner to the caller |
| `Limits:MaxBatchQueries` | 10 | query service | `BATCH_TOO_LARGE` | yes | The keyed fetch splits an owner's batch at the owner's `maxBatchQueries` as its shallow health last reported it, and at this host's own value until that is known (before the first health measurement, or with a remote client that does not report owners). An owner configured lower that has not been measured refuses the whole batch with HTTP 400: resolved aliases are `null` with `RESOLVE_UNREACHABLE`, a semi-join is 422 `RESOLVE_UNAVAILABLE`. Keep the value equal across services |
| `Limits:RegexMaxLength` | 200 | binder | `REGEX_TOO_LONG` | yes | a `regex` under a remote alias is checked by the owner |
| `Limits:MaxLookupLimit` | 100 | binder: the largest lookup `limit` and the default when none is given; the most targets an `elements: "all"` alias holds | `LOOKUP_LIMIT_EXCEEDED`; diagnostics `LOOKUP_TRUNCATED`, `RESOLVE_TRUNCATED` | yes | — |
| `Limits:MaxFlattenDepth` | 5, clamped to 1–12 | compiler: the levels an `unwind` with `flatten` descends | diagnostic `UNWIND_DEPTH_TRUNCATED` | yes | — |
| `Limits:MaxReportPageSize` | 5 000 | binder: the largest `page.limit` of a `strict` request without `cursor` or `offset` (at least `MaxPageSize`); beyond `MaxPageSize` the page limit times the rows its lookups return per row (their limits, one under `first`) stays within `MaxPageSize` × `MaxLookupLimit` × `MaxLookupStages` (250 000) | `PAGE_SIZE_EXCEEDED`; 422 `PAGE_INCOMPLETE` when more rows match | yes | — |
| `Limits:MaxReportedRows` | 50 | the rows one `RESOLVE_MISSING` or `RESOLVE_AMBIGUOUS` lists | — (`truncated: true`) | no | owner-reported rows are mapped back and capped here |
| `Execution:MaxTimeMs` | 10 000, clamped to 1–60 000 | `maxTimeMS` of the page and the count aggregate; a batch's `maxTimeMs` only lowers it | 504 `QUERY_TIMEOUT` | no | the time left of this budget, capped by `ResolveTimeoutMs` or `ChainTimeoutMs`, is sent to the owner as the batch's `maxTimeMs`, which the owner applies as its own ceiling |
| `Execution:ResolveTimeoutMs` | 2 000, never above `MaxTimeMs` | keyed fetch: one owner call of a plain remote resolve | `RESOLVE_TIMEOUT` (resolve), 422 `RESOLVE_UNAVAILABLE` (semi-join) | no | the whole semi-join phase is also bounded by the request's remaining budget |
| `Execution:ChainTimeoutMs` | 6 000, never above `MaxTimeMs` | keyed fetch: one owner call carrying continued stages or a typed, item, converted or element-wise resolve; the time left less 50 ms, at most this | `RESOLVE_TIMEOUT` | no | the owner budgets its own owner calls from what is left of it, so time bounds the whole chain |
| `Execution:AllowDiskUse` | `true` | aggregate option | 422 `QUERY_TOO_EXPENSIVE` when `false` | no | — |
| `Execution:SlowQueryMs` | 1 000 (`0` off) | warning log line for a slower request | — | no | — |
| `Cache:ResolveTtlSeconds` / `OwnerFetchCacheMaxEntries` | 60 / 50 000 | the owner-fetch cache: resolved rows (remote and in-process targets) and semi-join ids, per organisation; the budget holds per mode (rows, ids) | — | no | an owner's change is seen by callers within the TTL; the former key `ResolveCacheMaxEntries` still binds for one release |
| `Cache:NegativeResolveTtlSeconds` | 10 (`0` caches none) | the owner-fetch cache: keys an owner answered as not found; a `strict` request reads past them | — | no | — |
| `Cache:AddonDefinitionTtlSeconds` | 30 | the host's addon definition cache | — | no | — |
| `Cache:HealthProbeTtlSeconds` | 10 | reachability measurements behind `/oxql/health` | — | no | the same measurement reads each owner's engine version and `maxBatchQueries` |
| `Explain:Enabled` | `true` (was `false`) | `POST /oxql/explain` answers; 404 otherwise | — | no | — |
| `Explain:RemoteTimeoutMs` | 1 500 | one explain's wait for owners' internal explain in all (remote check and remote describes) | note `REMOTE_UNCHECKED` | no | — |
| `Explain:MaxDescribeChildren` / `MaxDescribeRequests` | 500 / 10 | children per describe answer (then `truncated`) / describe entries per explain (then an error) | `REQUEST_TOO_LARGE` per entry | no | — |

Fixed bounds, not configurable:

| bound | value | code |
|---|---|---|
| path segments / segment length | 64 / 256 characters | `INVALID_PATH` |
| pattern size the server compiles | 32 764 UTF-8 bytes, for a caller's `regex` and for a text the engine turns into a pattern | `REGEX_TOO_LONG`, `INVALID_OPERAND` |
| model depth / paths per entity | 32 segments / 20 000 paths; deeper members are not in the model | model build finding |
| `Execution:MaxTimeMs` ceiling | 60 000 ms | — |
| health reachability probe | 2 s per service | the service counts as not reachable |
| listed indexes for the explain advisory | cached 60 s per collection | — |
| owner answers to explain's remote check and remote describes | cached 30 s, at most 1 000 entries, failures not kept | — |
| describe `depth` | 1 to 3 | `INVALID_OPERAND` |
| records per key a grouped owner query returns | 2 (the second means `ambiguous`) | — |
| select-path retry rounds for a remote union target | 2 per request | — |
| JSON nesting of request and answer bodies | 256 (MVC's default is 32; explain answers of flattening and chain queries nest deeper) | — |

## `GET /oxql/health`

Anonymous, always 200, and never waits for another service:

```jsonc
{
  "status": "healthy",                    // "degraded" when a referenced service is not configured or was last measured unreachable
  "service": "oxql",
  "engine": { "version": "2.1.0.0", "contract": 2 },
  "capabilities": ["batch", "group.page", "page.offset", "any", "oxql.2.1", "unwind.keepPath",
                   "resolve.remote", "semiJoin", "resolve.chain", "lookup.remote", "explain", "compat.v1"],
  "limits": {
    "maxPageSize": 500, "defaultPageSize": 100, "maxPipelineStages": 20, "maxLookupStages": 5,
    "maxUnwindStages": 5, "maxResolveStages": 8, "maxGroupFields": 20, "maxProjectionFields": 500,
    "maxConditions": 200, "maxVariables": 64, "maxOffset": 5000, "countCap": 100000,
    "maxSemiJoinIds": 5000, "resolveKeyChunk": 500, "maxResolveKeys": 10000, "maxRequestBytes": 262144,
    "maxBatchQueries": 10, "regexMaxLength": 200, "maxLookupLimit": 100,
    "maxFlattenDepth": 5, "maxContinuedStages": 8, "maxReportPageSize": 5000, "maxReportedRows": 50,
    "chainTimeoutMs": 6000, "negativeResolveTtlSeconds": 10,
    "explainRemoteTimeoutMs": 1500, "maxDescribeChildren": 500, "maxDescribeRequests": 10
  },
  "remote": [ { "service": "vehicle", "configured": true, "reachable": true } ]
}
```

- `engine.version` is the version of the `OxQL.Core` assembly the host runs, which is the package
  version. Another host reads it from this
  endpoint to decide whether it may send 2.1 vocabulary (see *Keyed fetch and remote continuation*).
- `capabilities`: `batch`, `group.page`, `page.offset`, `any`, `oxql.2.1` and `unwind.keepPath` always
  (`unwind.keepPath`: an unwind may take its collection out of the row; a caller gates on it before
  sending `keepPath`, which an engine without it refuses as an unknown member);
  `resolve.remote`, `semiJoin`, `resolve.chain` and `lookup.remote` (a `lookup` whose `from` is another
  service's entity) when the host installed a remote query client;
  `explain` when `Explain:Enabled` (the default); `compat.v1` while `Compat:Enabled`. `oxql.2.1`
  covers every language feature of 2.1 and the explain answer described below; a caller gates on it
  before sending `strict`, `is`, `flatten` or a 2.1 stage member, or before reading an explain answer
  as 2.1.
- `limits` are the values in force after registration adjusted them, not the configured ones;
  `chainTimeoutMs` is the effective value (clamped to `MaxTimeMs`).
- `remote` lists every service the model references remotely, from a root member, a member of
  an embedded object or a member of a collection element alike, and every target of a typed
  reference, and is absent when the host has no remote query client or the request says
  `?shallow=true`. `reachable` is the last
  measurement, taken at most once per `Cache:HealthProbeTtlSeconds` in the background, and
  `null` before the first one has finished or for a service that is not configured; a `null`
  does not degrade `status`.
- `?shallow=true` starts no measurement. It is the form one host uses to ask another, so a probe
  never sets off the probed service's own probes. The same answer tells the asking host the owner's
  `engine.version` and `limits.maxBatchQueries`.

## `POST /oxql/explain`

Everything about a query without running it: whether it binds and every error, the shape after
each stage, where each join runs and what is sent to other services, the result columns, describe
of child paths, engine-behaviour notes, and, for a query that binds, the compiled form. On by
default since 2.1 (`Explain:Enabled`); 404 while it is off. It is the rule source of the Angular
OxQL Studio: the builder, completion, hover and markers ask explain and nothing else. There is no
separate validate endpoint. The request (a plain query or the envelope with `describe`, `remote`
and `include`) is in [`oxql-query-syntax.md`](oxql-query-syntax.md#explain-request).

```jsonc
{
  "valid": true, "contract": 2,
  "engine": { "version": "2.1.0.0", "capabilities": ["batch", "…", "oxql.2.1", "explain"] },
  "errors": [],                                     // every binding error when valid is false
  "diagnostics": [],                                // what binding diagnosed (a retired id, an unanchored regex, …)
  "notes": [ { "code": "JOIN_AFTER_PAGE", "stage": 6, "path": "sourceLine", "message": "…",
               "params": { "alias": "sourceLine", "kind": "resolve" } } ],
  "steps": [
    { "index": 3, "kind": "unwind", "status": "ok", "executor": null, "phase": null, "owner": null,
      "creates": [ { "alias": "item", "node": "element", "entity": "ledger.transaction", "source": "items" },
                   { "alias": "position", "node": "scalar", "kind": "int" } ],
      "shapeAfter": { "paging": "offset", "grouped": false, "unwound": ["items"], "projection": null } },
    { "index": 6, "kind": "resolve", "status": "ok", "executor": "keyed-remote", "phase": "afterPage",
      "owner": { "service": "transport", "route": { "apiName": "transport-api", "apiVersion": null },
                 "query": { "entityType": "transport.shipment", "pipeline": [ "…keys elided: \"…\"" ] },
                 "targets": [ { "target": "transport.shipment#billingLines", "service": "transport", "remote": true,
                                "grouped": true, "route": { … }, "query": { … }, "continued": [7], "notApplicable": [] },
                              { "target": "transport.tour#billingLines", "…": "…", "continued": [], "notApplicable": [7] } ] },
      "reference": { "cases": [ { "when": { "path": "type", "equals": ["logistics"] },
                                  "targets": [ { "entity": "transport.shipment", "item": "billingLines", "field": "id", "remote": true },
                                               { "entity": "transport.tour", "item": "billingLines", "field": "id", "remote": true } ] } ],
                     "keyAs": null, "elements": null },
      "creates": [ { "alias": "sourceLine", "node": "remote", "entities": ["transport.shipment#billingLines", "transport.tour#billingLines"] },
                   { "alias": "sourceParent", "node": "remote", "entities": ["transport.shipment", "transport.tour"] } ],
      "continued": [ { "index": 7, "forTarget": "transport.shipment" } ],
      "shapeAfter": { "paging": "offset", "grouped": false, "unwound": ["items"], "projection": null } }
  ],
  "result": { "paging": "offset", "columns": [ { "path": "number", "kind": "string", "nullable": false, "stage": null, "root": "" } ] },
  "describe": [ { "id": "d1", "at": 6, "prefix": "erpLine", "usage": "match",
                  "root": { "node": "entity", "entity": "ledger.billing_line" }, "forwarded": false, "truncated": false,
                  "children": [ … ] } ],
  "bound": { … },          // the canonical bound form the cursor fingerprint is taken over; absent when not valid
  "stages": [ … ],         // the page aggregate, in the order the server runs it; absent when not valid
  "count": [ … ],          // the count aggregate, when a count was requested
  "collation": { "locale": "de", "strength": 1 },   // when something folds
  "advisory": [ … ]        // only with include: ["indexes"]
}
```

- **`valid`.** A query that does not bind is answered 200 with `valid: false` and every error, so a
  caller still gets the steps, the shapes and describe for the part that binds; `bound` and `stages`
  are then absent, joins carry no executor, phase or owner, `result.columns` is empty, and the notes
  are describe's only. A query that binds but that this host cannot run (a remote resolve without a
  remote query client: `RESOLVE_UNAVAILABLE`) or whose continued stages an owner refuses is
  `valid: false` too. Refusals remain only for what stops explain before binding: 400 for a malformed
  body, 403 without an organisation, 413 for a body over the limit, 500 for an engine fault.
  `contract` is the contract the request was read as; a contract 1 request is rewritten by the
  compatibility binder and answered the same way.
- **`steps`**, one per caller stage: `status` `ok`, `error`, or `skipped` (it failed only because it
  reads an alias an earlier stage failed to create); for a join `executor` (`inline`,
  `keyed-local`, `keyed-remote`, `continued`) and `phase` (`beforePage`, `afterPage`, `owner`); for a
  keyed or continued stage `owner` with every owner query as it is forwarded, keys and the page limit
  elided as `"…"` (`route.apiVersion` is the version the remote client routes the service to,
  `IRemoteOwnerInfo.ApiVersionOf`, or `null` when it does not say); `reference` for a
  resolve, as bound; `creates`, the aliases added (`node` `entity`, `element`, `array`, `remote`,
  `keyed`, `scalar`, `group`; a resolve continued under another alias without a `target` lists the
  targets its owners bound the reference to, on its `forTarget` entity or on each target of the
  alias, and none when no owner answered the check); `continued` on a keyed stage; `shapeAfter` (`paging` is `cursor`
  while every row is one entity row, `offset` after an unwind or group).
- **`result.columns`**: the final shape's visible members and roots, each with `path`, `kind`,
  `nullable`, the `stage` that created it (`null`: the entry shape) and its `root`. Members of a
  remote alias are `unknown`: their shape is the owner's.
- **`notes`** (`{ code, message, stage, path, params }`, in stage order, request-wide last) say what
  the engine will do that a reader of the query might not expect: `TEXT_FOLDS`,
  `PATTERN_FOLDS_CASE_ONLY`, `EXACT_FORCES_EXACT`, `SOME_ELEMENT`, `NEQ_MATCHES_ABSENT`,
  `ONLY_FOR_VARIANTS`, `SNAPSHOT_COPY`, `JOIN_BEFORE_PAGE`, `JOIN_AFTER_PAGE`, `OWNER_BINDS`,
  `REMOTE_UNCHECKED`, `SELECT_PATH_NOT_ON_TARGET`, `COUNT_CAP`, `LOOKUP_LIMIT`, `OFFSET_PAGING`,
  `MISSING_POLICY` (the effective `onMissing`, `strict`, and which outcomes lose data),
  `REPORT_PAGE`, `INDEX_ADVICE` (only with `include: ["indexes"]`; an index list that cannot be read
  is an `INDEX_ADVICE` note saying so). Codes are stable; messages may change. A remote union
  target's `SELECT_PATH_NOT_ON_TARGET` comes from its owner's internal explain (the remote check),
  which then checks that target again without the paths it lacks, as a run asks again, so the
  stages continued at it are checked as the run binds them; a run reports the paths an owner dropped as diagnostics, from the cache as well.
  `MISSING_POLICY` names only the outcomes the stage can have: an inline resolve has no
  `owner_unanswered` or `invalid_key`, and `ambiguous` only onto a target field that is not the key.
- **`describe`**: one answer per describe entry, in order, echoing `id`, `at` or `entity`, `prefix`
  or `paths` and `usage`, with the `root` it was taken under, whether an owner answered it
  (`forwarded`), `truncated`, and the `children`. A child carries its `name`, `path`, `displayName`
  (`null` when derivable from the name), `description`, `kind`, `leafKind`, `nullable`, `stored`,
  `collection`, `underCollection`, the flags `filterable sortable projectable unwindable groupable`
  (the binder's own answer for that usage at that point of the pipeline), `operators` (those the
  binder admits there, plus `is` and `any` where they apply), `caseFolding` (`folds` or `none`),
  `enum` (values with descriptions), `variants`, `flatten` (the collection's own recursion first,
  and `flattenMembers` listing every candidate when there are several), `onlyFor`, `snapshotOf`, `reference`
  (`simple`, `keyAs`, `cases`, `followable`), `referencedBy` (with `referencing: true`), `addon`,
  `deprecated` (`since`, `replacedBy`, `note`), `constraints`, `hasChildren`, `notes`, and an `error`
  when the entry could not be answered.
- **`remote`.** With `check` (the default) the stages continued at another service are checked by
  that owner's internal explain; an owner error becomes this request's error at the caller's stage
  (`params.owner` as in a refusal) and the step's status `error`. The describes of another service's
  entities are answered the same way. All owner calls of one explain share
  `Explain:RemoteTimeoutMs` (1 500 ms); a part no owner answered (skipped by `remote: "skip"`, a
  client that cannot explain, unreachable, timed out, not configured) leaves a `REMOTE_UNCHECKED`
  note with `params.reason`, never an error. Owner answers are cached 30 s by organisation, service
  and forwarded body.

**It never executes the query.** Explain binds and compiles; it sends the database no `aggregate`
and no `explain` command. Without `include` it reads nothing from the database but, on a cache miss,
the organisation's addon definitions. With `include: ["indexes"]` it reads the
index lists of the collections involved (`listIndexes`, cached 60 s per collection) and matches
them statically against the leading `$match` (scope, cursor predicate and first caller condition,
which the server coalesces), the sort and each `$lookup`'s join field; each advisory line is also an
`INDEX_ADVICE` note. The advisory is absent when the host provides no index source. The page
aggregate fetches `limit + 1` rows; the extra one decides `hasNextPage`. A join that runs after the
page appears after the `$limit`.

**What it reveals.** To any authenticated caller of the organisation: storage paths, collection
names, the emitted Mongo stages, the collation, the forwarded owner queries, and with the opt-in
the index names and keys. None of it is more sensitive than what `GET /schema` serves anonymously
(storage names, keys, references, routes), and it concerns the caller's own organisation only: the
scope stage shows the caller's organisation id. No row data, no other organisation's data, no
configuration secret appears. Explain has no rate limit; it costs one bind and one compile.

**Changes from 2.0**, all taken up by the `OxQL.Studio` console in the same release:

- on by default (`Explain:Enabled` was `false`);
- a query that does not bind is 200 `valid: false` with `errors`, no longer a 400 refusal;
- the index advisory needs `include: ["indexes"]` and is static: 2.0 listed indexes on every call
  and ran the server's `executionStats` explain for a pipeline with a `$lookup`, which executed the
  page pipeline once;
- the body may be an envelope, whose malformed forms are 400 ProblemDetails;
- the answer gains `valid`, `contract`, `engine`, `errors`, `notes`, `steps`, `result` and
  `describe`; `bound`, `stages`, `count`, `collation` and `diagnostics` keep their names and
  meaning, and `bound` renders a continued stage as `{ "continued": { anchor, kind, forTarget,
  aliases, stage } }`.

A host that serves continued stages for other services also answers the internal twin, `POST
internal/oxql/explain` in the Simplic base package: the same body and answer, admitted by the
internal key under the forwarded identity. An origin calls it for the remote check and remote
describes through `IRemoteQueryClient.ExplainAsync`. The stages continued under a keyed stage of
the host itself are checked the same way in process, as its own owner binds them when the query
runs; what continues from there to another service (every target of a union without `forTarget`)
is that owner's remote check, so explain refuses what the run would refuse.

## Keyed fetch and remote continuation

Every join that is not the in-aggregate `$lookup` (a local lookup, or an inline local resolve) runs
through one mechanism, the **keyed fetch**, in two modes:

- **By keys**, after the page is fixed: per keyed stage the engine collects each row's keys (only
  those whose case is selected, converted per `KeyAs`), asks each target's owner for them, and
  assigns the answers per row by case and target order, lifting the aliases of continued stages
  back to the row. A remote target's owner is the other service, called through
  `IRemoteQueryClient.BatchAsync`; a local target's owner is this host, called in process with the
  same context (`SelfOwner`), so a local typed, item, converted or element-wise resolve takes the same
  path.
- **By condition**, before the page: 2.0's semi-join. A condition on a member of a plain remote alias
  asks the owner for the matching target keys page by page, refuses above `MaxSemiJoinIds`, and
  substitutes them as an `$in`. It exists for plain remote resolves only.

Both modes share the owner query builder, the batching, the time budgets, the error mapping and the
cache. The owner queries are ordinary OxQL queries, one per target and key chunk, in batches per
owning service, owners in parallel:

| target | owner query |
|---|---|
| an entity by its own key | `match <field> in [keys]`, the filter, the continued stages, the projection, a page of the chunk's size |
| an item, or a non-key field | the internal member `keyedBy: { path, keys, perKey: 2 }`: the owner matches the keys (for an item, unwinds only the elements holding a key as `oxEl`, keeping their position), ranks the records per key by record key (and position), keeps two, then the filter, the continued stages and the projection; a second record means `ambiguous`. String keys compare exactly even inside a collated aggregate. At most `MaxPageSize / 2` keys per query, and no more than the owner's own page holds. A plain 2.0 resolve onto a non-key field keeps the plain key match while nothing reads its outcomes; under `strict` or an `onMissing` other than `null` it is grouped, or, at an owner known to run 2.0, asked the plain match with two rows per key |
| the existence probe | the same without the filter and projecting only the key, for keys the filtered answer lacked, when the stage has a `filter` and an `onMissing` other than `null` |

- `keyedBy` is accepted only on the internal route and in process (`IOxQLQueryService.BatchAsync(batch,
  internalCall: true, …)`, which marks the request context internal); on the public routes it is
  `UNKNOWN_REQUEST_MEMBER`. It is the only new wire vocabulary; no header is added. A plain remote
  resolve onto an entity's key sends the plain key match, so an owner still on 2.0 keeps answering
  it.
- **Owner facts.** The keyed fetch splits each owner's batch at the owner's `maxBatchQueries`, sizes
  its key chunks by the owner's `maxPageSize`, and refuses 2.1 vocabulary (continued stages,
  `keyedBy`) to an owner whose engine is older (`OWNER_NOT_CAPABLE`), all read from the owner's
  shallow health by a remote client that implements `IRemoteOwnerInfo`. Before the first batch of a
  request the fetch asks the client for each owner's facts (`OwnerOfAsync`), which may read the
  owner's shallow health right then, within a tenth of the phase (at most 250 ms); explain reads them
  the same way, within its owner budget, and answers `OWNER_NOT_CAPABLE` where the run would. A
  client that does not know them in time leaves the host's own caps (an owner configured with a
  smaller page then refuses `PAGE_SIZE_EXCEEDED` inside `RESOLVE_REFUSED`; the next request, with the
  facts read, succeeds). An owner still on 2.0 whose facts are unknown ignores `keyedBy` and answers
  rows not grouped per key: the fetch refuses them (`RESOLVE_REFUSED`, a row without its key) or
  reports the keys it did not get as `owner_unanswered`, never as values. An owner on 2.1 packages
  behind a base package whose internal route does not use the internal-call overloads refuses
  `keyedBy` as `UNKNOWN_REQUEST_MEMBER`, which the caller sees as `RESOLVE_REFUSED`: an owner other
  services call needs the matching base package.
- **Remote lookups** rank each key's children at the owner in the lookup's sort, then by record key;
  elements of one owning row that tie there come back in no guaranteed order (sort by an element
  member to fix it).
- **Owner faults.** An owner error coded `INTERNAL_ERROR` (and the title of an `internal_error`
  envelope) reaches the caller with a fixed text, whatever the owner's `IncludeErrorDetails`; the
  detail stays in the owner's log.
- **Union select paths.** A select path an owner says one target of a union lacks is dropped for
  that target and the query asked again, for every chunk that carried it; what was learned is kept
  in the cache per organisation, service and plan, so later requests do not send it and still
  report it.
- **An owner answer with `hasNextPage`** means the owner cut rows: the chunk's open keys are
  `owner_unanswered` (`RESOLVE_PARTIAL`), never `not_found`, and are not cached.
- **Time.** See `Execution:ResolveTimeoutMs` and `ChainTimeoutMs` under *Limits*. Split batches of
  one owner share the request's deadline, each under what is left. The batch's `maxTimeMs` is the
  call's budget less a tenth (at most 250 ms), so the owner stops and answers before the caller
  stops waiting; the owner applies it to the whole batch. A `RESOLVE_TIMEOUT` names the time the
  failing call had.
- **Strictness** travels in the query body: an owner query carrying continued stages carries
  `strict: true` when the origin is strict. Variables are substituted at the origin.
- **Security** is that of a remote resolve: the internal route is admitted by the internal key and
  scoped by the forwarded identity; continued stages read owner entities under the caller's
  organisation, which that user can already query directly. The owner-fetch cache below is keyed by
  organisation, not by user: it assumes an owner answers every user of an organisation alike, as the
  fleet's owners do (they scope by organisation only). An owner that filters rows per user must not
  be reached through a caching host.

**The owner-fetch cache** holds, per mode, the owner's answers for `Cache:ResolveTtlSeconds` (60 s),
at most `Cache:OwnerFetchCacheMaxEntries` rows or ids (an answer weighs the rows it holds). A by-keys entry is keyed by target entity,
item, target field, organisation, key and a hash of the substituted owner query without its keys
(select, filter, parent select, continued stages, case), so requests that differ in any of these, or
in their variables, never share an entry. A key the owner answered as not found is kept for
`Cache:NegativeResolveTtlSeconds` (10 s); a strict request reads past such entries. An answer that
carried owner diagnostics for continued stages, and keys of a cut answer, are not cached. The
semi-join's ids are keyed by organisation and the first-page owner query.

**Contract header hint.** A request without `X-OxQL-Contract: 2` is contract 1 while
`Compat:Enabled` is on, which is the likeliest mistake when a query is pasted into a report data
source. Every refusal such a request receives for a construct only contract 2 has ends its message
with "This request was read as contract 1 because it carries no 'X-OxQL-Contract: 2' header." The
codes are unchanged.

## Staging smoke checklist

Run after an engine or base package upgrade, against one deployed service, with a token of a
test organisation. `{api}` is the service's base URL (for example
`https://staging.example.com/myservice-api/v1`), `{entity}` one of its entities, `{member}` a
string member of it and `{ref}` a member declaring a reference into another service. Every
query request sends `X-OxQL-Contract: 2`.

1. `GET {api}/oxql/health` without a token → 200, `engine.contract` 2, the expected
   `engine.version`, `status` `healthy`, every `remote` entry `configured: true` and, after a
   second call ten seconds later, `reachable: true`; `oxql.2.1` in `capabilities`, and `explain`
   unless the host switched it off.
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
   (`{ "match": { "target.id": { "eq": "<an id>" } } }`) → 200, not 422. The same resolve with
   `"onMissing": "report"` and `"strict": true` at the top → 200 when every reference on the page
   resolves, else 422 `not_executable` listing the rows.
8. An addon filter: read the organisation's definitions with `GET {api}/schema/addons`, then
   `{ "match": { "addon.<defined key>": { "exists": true } } }` → 200; an undefined key under
   `eq` → 400 `NOT_FILTERABLE`.
9. Organisation isolation: take an `id` returned in step 4 and ask for it
   (`{ "match": { "id": { "eq": "<id>" } } }`) with a token of a second organisation → 200 with no
   rows. A token without an organisation → 403 `ACCESS_DENIED`.
10. One internal batch call, as another service would make it: `POST` the host's internal batch
    route (for the Simplic base package `{api}/internal/oxql/batch`) with the cluster's internal
    api key and an organisation header, body `{ "queries": [ { "entityType": "{entity}", "pipeline": [ { "page": { "limit": 1 } } ] } ] }`
    → 200 with one result; without the key → 401. The same body with a top-level `keyedBy` sent
    to the public `{api}/oxql/batch` → the entry is refused with `UNKNOWN_REQUEST_MEMBER`.
11. `POST {api}/oxql/explain` with the request of step 4 → 200, `valid: true`, `steps` and
    `result.columns` filled, no `advisory`; with the refusal of step 6 → 200, `valid: false`,
    `UNKNOWN_PATH` at stage 0. 404 only where explain was switched off on purpose.
12. The service log: no OxQL startup error, no option-adjustment warning you did not expect, and
    slow-query warnings at a rate you expect.
