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
| 404 | `POST /oxql/explain` while `Explain:Enabled` is false (it is on by default) |
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
| `UNWIND_ORDER` | 400 | an inner collection unwound before its outer one; under contract 2 an unwind of a collection a join of an earlier stage reads its keys from (`params` `reason: "joinReadsCollection"`, `join`, `alias`: join after the unwind) |
| `ALIAS_COLLISION` | 400 | an alias equal to a member's wire or storage name or an earlier alias; two group outputs of one name; an unwind's `as` equal to its `includeIndex` |
| `INVALID_ALIAS` | 400 | an alias that is not a plain identifier, is `_id`, starts with `__` or ends in `__arr`; a contract 2 `unwind` without `as` (`params` `stage: "unwind"`, `member: "as"`, `reason: "required"`) |
| `INVALID_OPERAND` | 400 | the wrong JSON kind for the member; an operator the kind does not take (`gt` on a `bool`); `null` with an ordered or text operator; an array without `in`/`nin`; a text operator on a single-character member; a text too long for the server's pattern limit; `exists` without a boolean; `is` on a member without variants or under a collection that is not unwound, or with an operand that is not a name or a non-empty array of names; `dateTrunc` on a member that is no date, through a collection that is not unwound, or with an operand that is not `{ unit, timezone?, weekStart?, eq }` with an instant, a day on a `date` member, or `null` as `eq` (`params` `op: "dateTrunc"`, `reason`) |
| `UNKNOWN_ENUM_MEMBER` | 400 | an enum name or number the enum does not declare; a value outside an addon key's closed list; a name that is no outcome in a condition on a member named by `outcomeAs` (`params.values` lists the outcomes) |
| `OPERAND_NOT_ARRAY` | 400 | `in` or `nin` without an array |
| `DECIMAL_TEXT_NOT_ORDERABLE` | 400 | `gt gte lt lte` on a decimal member whose declared storage is text |
| `UNBOUND_VARIABLE` | 400 | `{ "$var": … }` names a variable the request does not bind |
| `INVALID_VARIABLE` | 400 | a variable holds an object, or an array where an element is expected; under contract 2 a name in `variables` that starts with `$`, which is reserved for the engine (`params` `name`, `reason: "reserved"`) |
| `INVALID_FILTER` | 400 | contract 2: the request's `filter` is malformed; `params.reason` is `notAnArray`, `notAnObject`, `unknownMember`, `whereRequired`, `invalidId`, `duplicateId` or `malformedWhere`; `filter` names the entry |
| `FILTER_NOT_PLACEABLE` | 400 | contract 2: a condition of the request's `filter` reads paths that are never in the row at the same point of the pipeline; `params.paths`, `params.births` (per path `{ path, before: 0 }` or `{ path, after: i }`); `filter` names the entry |
| `UNKNOWN_OPERATOR` | 400 | an operator outside the list (they are case-sensitive); `is` under contract 1 |
| `UNKNOWN_VARIANT` | 400 | an `is` names a type that is neither a variant of the member's type nor its concrete base; the message lists the variants |
| `UNKNOWN_REQUEST_MEMBER` | 400 | a contract 2 request carries a top-level member other than `entityType`, `variables`, `filter`, `pipeline`, `strict`, `timing`; `keyedBy` on a public route; an explain envelope's `budget` on the public route |
| `EMPTY_LOGICAL_GROUP` | 400 | an empty `and`, `or` or `not`; a condition that names neither a path nor a group |
| `OPTION_NOT_APPLICABLE` | 400 | an unknown option; `caseSensitive` on an ordered operator, `regex`, a non-string member or under contract 1; `ignoreCase` under contract 2 (`params` `option`, `reason: "removed"`, `use: "caseSensitive"`); under contract 1 `caseSensitive` and `ignoreCase` disagreeing; an exact sort in a request that folds elsewhere; options on `is`. Stage options where they do not apply: `limit` with lookup `first`, `elements` on a single value, `parentSelect` without `parentAs`, `forTarget` on a stage that is not continued under a union alias, or naming a target the alias lacks |
| `INVALID_REGEX` | 400 | a backreference, a quantifier over a quantified group, a pattern .NET cannot parse; or, from execution, a pattern the server's PCRE2 rejects |
| `REGEX_TOO_LONG` | 400 | a pattern longer than `RegexMaxLength` characters or 32 764 UTF-8 bytes |
| `ANY_NOT_APPLICABLE` | 400 | `any` on a collection of scalars, an unwound collection or one under another collection |
| `UNKNOWN_STAGE` | 400 | a stage object with no key, several keys or an unknown key; a `null` stage; a `null` query in a batch |
| `UNKNOWN_STAGE_MEMBER` | 400 | an unknown member of a stage or of a sort entry's object form; a stage whose value is `null`; a sort entry with more than one key or none; a contract 2 stage member of the wrong JSON kind (`elements` not `first`/`all`, `onMissing` not `null`/`report`/`refuse`); a malformed catalog entry of an explain (answered on the entry, never a refusal) |
| `FLATTEN_NOT_RECURSIVE` | 400 | an unwind's `flatten` names a member that is not a collection of the same items as the unwound collection, or the collection holds no objects |
| `STAGE_AFTER_PAGE` | 400 | a stage after `page` |
| `MULTIPLE_PAGE_STAGES` | 400 | two `page` stages |
| `MIXED_PROJECTION` | 400 | inclusion and exclusion in one projection (other than `"id": 0`); an empty projection |
| `GROUP_ON_COLLECTION` | 400 | a group key or aggregate argument under a collection that is not unwound |
| `UNKNOWN_AGG_FUNCTION` | 400 | an aggregate outside `count countDistinct sum avg min max first last push` |
| `INVALID_AGGREGATE_ARGUMENT` | 400 | `sum`/`avg` over a non-numeric argument; a missing argument; an argument object naming no path, variable, literal or operator; a group key's `dateTrunc` over a non-date |
| `INVALID_DATE_TRUNC_UNIT` | 400 | a unit outside `year quarter month week day hour minute second`; a `weekStart` that is not a day |
| `INVALID_TIMEZONE` | 400 | a `timezone` that is not an IANA (or Windows) zone id |
| `INVALID_SORT_DIRECTION` | 400 | a direction other than `asc`/`desc`; the object form without `direction`; the object form under contract 1 |
| `LOOKUP_NOT_DECLARED` | 400 | the child's `path` declares no reference to the current entity (or to the entity of the `on` alias), or the referenced key is not stored |
| `LOOKUP_ON_NOT_ENTITY` | 400 | a lookup's `on` names a lookup array, an unwound element, a scalar or a group output |
| `UNION_CARDINALITY_MISMATCH` | 400 | the branches of a union join (`byTarget`) do not yield one shape: one resolves one record per row and another every element (`elements: "all"`); `params` `alias`, `branch` (the branch to change), `elements`, `expected` (`one` or `all`) |
| `NOT_CONTINUABLE` | 400 | on an alias that comes from an owner after the page, a stage other than a `resolve` or `lookup` (an `unwind`, a `group` key); a continued stage under an `elements: "all"` alias or under a remote `lookup` without `first`; any continued stage under contract 1 |
| `RESOLVE_NOT_DECLARED` | 400 | the `resolve` path declares no reference (the message names its kind), the target key or items are not stored, or a case tests a sibling that is not stored |
| `RESOLVE_ON_COLLECTION` | 400 | a resolve path under a collection that is not unwound, without `elements` |
| `RESOLVE_TARGET_NOT_DECLARED` | 400 | a resolve's `target` is not a target of any case of the reference; the message lists them |
| `RESOLVE_PARENT_NOT_ITEM` | 400 | a resolve's `parentAs` on a reference whose (narrowed) targets include an entity rather than an item |
| `RESOLVE_NOT_FILTERABLE` | 400 | a condition on a remote resolve alias itself (`{"veh":{"eq":null}}`, `{"veh":{"exists":true}}`); the owner is not called. A condition on a member of a plain remote alias (`veh.matchCode`) is a semi-join; under a keyed, typed, item, converted, element-wise or continued alias any condition is refused, since the alias is joined after the page |
| `RESOLVE_NOT_SORTABLE` | 400 | a sort on a path under a remote or keyed alias; a sort on a join's outcome (`outcomeAs`), wherever the join runs |
| `MAX_PIPELINE_STAGES_EXCEEDED` … `MAX_VARIABLES_EXCEEDED`, `MAX_CONTINUED_STAGES_EXCEEDED` | 400 | the matching limit (see *Limits*) |
| `INVALID_PAGE_LIMIT` | 400 | `limit` below 1 (contract 2: `limit: 0` without `count` or with a cursor, `params` `limit: 0`, `needs: "count"`); a `count` that is not `true`, `false` or an object, or whose `upTo` or `budgetMs` is not a positive integer; contract 1 and internal calls: a negative `offset`, a cursor together with an offset, a count cap that is not a positive integer |
| `PAGE_SIZE_EXCEEDED` | 400 | `limit` above `MaxPageSize` |
| `LOOKUP_LIMIT_EXCEEDED` | 400 | a lookup `limit` below 1 or above `MaxLookupLimit` |
| `MAX_OFFSET_EXCEEDED` | 400 | contract 1 and internal calls: `offset` above `MaxOffset`. A contract 2 page has no `offset` (`UNKNOWN_STAGE_MEMBER`) |
| `BATCH_TOO_LARGE` | 400 | a batch of more than `MaxBatchQueries` queries; the whole batch is refused |
| `REQUEST_TOO_LARGE` | 413 | a body over `MaxRequestBytes`, refused before it is read |
| `EXPLAIN_LIMIT` | 400, or 429 | 400: an explain past its bounds (`Explain:MaxStages`, `MaxCatalogEntries`, `MaxShapeDepth`) or naming an `include` or `remote` value the engine does not know, refused before anything is bound. 429 (`rate_limited`, with `Retry-After`): more explains than a caller may send or have in flight, refused before the body is read; `params` `limit`, `max`, `retryAfter`. As an explain note: a cost limit was hit mid-way and a part was left out |
| `CURSOR_INVALID` | 400 | a cursor from another pipeline, organisation or signing key, or altered |
| `ACCESS_DENIED` | 403, or 400 | 403: no organisation in the request, or the entity has no root `organizationId`. 400 (inside a `validation_error`): a lookup child or resolve target without one. Test the code, not the status |
| `RESOLVE_UNAVAILABLE` | 422 | a remote resolve or semi-join on a host without a remote query client (explain answers it as `valid: false`); a semi-join whose owner did not answer or answered with an HTTP error |
| `RESOLVE_REFUSED` | 422 | the owner refused its part, or answered a row without the key it was asked for. Contract 2: the head is followed by the owner's errors as the caller's, exactly as explain says them: the owner's `code`, `message` and `params`, `params.owner { service, entity, target, stage, path }`, and the caller's place and path (a continued stage: its `stage` and path; a remote `resolve`'s or `lookup`'s own `filter`: the stage, the path as written in the filter; a condition on another service's member: the `match` stage or the `filter` entry (`filter`, no `stage`), the path as written, `recipient.id`). The head lies where the first of them lies. A chain's errors are said once: an owner's own `RESOLVE_REFUSED` head is not repeated. Contract 1: as before, the owner's errors of a semi-join and of a resolve's filter as the owner wrote them (its path, no stage, no params) |
| `PAGE_INCOMPLETE` | 422 | a `strict` request without a `cursor` matches more rows than its page holds; `params` `limit`, `max` |
| `SEMI_JOIN_TOO_LARGE` | 422 | a condition under a remote alias selects more than `MaxSemiJoinIds` rows of the owner |
| `QUERY_TOO_EXPENSIVE` | 422 | a sort or group over the server's memory limit with `AllowDiskUse` off; a `push` or `countDistinct` value over the server's size limits |
| `QUERY_TIMEOUT` | 504 | an aggregate exceeded `Execution:MaxTimeMs` or the batch's `maxTimeMs` |
| `INTERNAL_ERROR` | 500 | any other fault |
| `LEGACY_STAGE_UNSUPPORTED` | 400 | contract 1 only: a v1 `lookup` or `resolve`, an unknown stage member (the contract 2 stage members included), `strict`, the number form of `includeTotalCount` |

Diagnostics travel in `diagnostics` of a successful answer. Those marked *loss* refuse a `strict`
request instead (422 `not_executable`, the diagnostic as the error); `RESOLVE_MISSING` refuses by its
stage's effective `onMissing`:

| code | `params` | when |
|---|---|---|
| `ENTITY_ID_RETIRED` | `currentId` | the request named a retired entity id |
| `TOTAL_COUNT_CAPPED` | `cap` | contract 1 and internal calls: more rows match than the count cap in force; `totalCount` is the cap. A contract 2 count says it under `pageInfo.count` (`state: "atLeast"`, `stoppedBy: "cap"`) and carries no diagnostic |
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

### Where the time went

Every `query` and `batch` answer carries `Server-Timing` (`total`, `db` with `desc="<commands> cmd /
<round trips> rt"`, `ours` = `total` − `db`, `bind`, `compile`, `convert`, and `addon`, `resolve`,
`remote`, `count`, `net` where they occurred), and a request that asks (`"timing": true`, or the header
`X-OxQL-Timing`) the breakdown as the `timing` member; the members are listed in
[`oxql-query-syntax.md`](oxql-query-syntax.md#where-the-time-went). What a host should know:

- The split is always recorded (a timestamp pair per phase and per command); only the `bytes` of
  a command are measured on request, and that measuring is taken out of the request's total.
- Every command the engine sends goes through a decorator of the host's `IAggregateRunner`
  (`TimedAggregateRunner`), so a host that registers its own runner is measured too. The engine's
  `MongoAggregateRunner` also says when the command's own reply arrived, how many `getMore`s
  followed and the driver's round-trip time; another runner leaves those out.
- **A read the host makes at a database for the engine is database time.** The engine cannot see
  inside the host's `IAddonDefinitionSource`, so a source that waits for a database says so: it
  implements `IReportingAddonDefinitionSource` (`OxQL.Model.Addon`) and calls
  `report.Database(collection, started, docs, roundTrips)` for every command a call waited for,
  with the `Stopwatch` timestamp it took before the wait began (`roundTrips: 0` when the call
  waited for a command another request had sent). The request's timing then lists the wait as a
  command of kind `addon`, inside `db.ms`, and `Server-Timing` carries `addon;dur=…;desc="db"`.
  A call the source answers from memory reports nothing. A source that does not implement the
  interface is not asked and the engine guesses nothing: whatever its calls take stays in
  `addonMs` and in `oursMs`. A host should keep that read off the request path altogether: cache
  the definitions per organisation and entity, serve a value past its lifetime while one read
  replaces it in the background, and let the calls that miss together share one read.
- **The first use of a code path is this host's own time.** A request that is the first in the
  process to run a stage or an operator pays the runtime's compilation of every method it is the
  first to call: measured in a Release build on the test fleet, 16 to 23 ms of `ours` for the
  first request with a `match`, `sort` and `project`, 17 to 20 ms for the first `contains`, on
  each of two requests sent together, and under 1 ms for the first date range or `in` after
  them; the second request of the same kind pays nothing. It shows in `bindMs` and `parseMs`,
  never in `db`. The opt-in `FirstUseBench` (`OXQL_PERF_FIRSTUSE=<file>`, Release) measures it.
  The engine does not warm itself at start.
- The owner queries a host runs for its own join targets are the request's own commands
  (`kind: "resolve"`). Another service's owner is asked for its timing only when the origin's
  request asked for the breakdown; it travels back in the owner's answer (`timing` on each result of
  the internal batch) and is listed under `resolve.owners[].timing`.
- The log line of a request carries `dbMs`, `dbCommands`, `oursMs` and `convertMs` beside
  `elapsedMs`, so the split is in the logs without any client.
- **Browsers.** A page on another origin reads the header only when it is exposed. The engine's
  controller names `Server-Timing` in `Access-Control-Expose-Headers` on an answer to a request
  that carries `Origin`. A host whose CORS policy sets exposed headers itself
  (`WithExposedHeaders`) replaces that value and must name `Server-Timing` there; a gateway that
  answers CORS in front of the service must expose it as well. The browser's own resource timing
  of a cross-origin request (transfer, sizes) needs `Timing-Allow-Origin` from the host or the
  gateway; the engine does not send it.
- **Content coding.** The answers of `query` and `batch` are written in the coding the caller
  accepts (`Accept-Encoding`: Brotli, else gzip, at the fastest level) when the body is at least
  1 KB, with `Vary: Accept-Encoding` either way; a refusal is not coded. A page of 100 rows with
  14 columns (51 KB) is 7.7 KB in Brotli and costs 0.06 ms to code. The body and its coded form are
  held in pooled segments, never in one array.
- **Text.** The body of a `query`, `batch` and `explain` answer is UTF-8 and escapes only what JSON
  requires: the quote, the backslash and the control characters. `"Köln"` is those characters, not
  `"K\u00F6ln"`, and `<`, `>`, `&`, `'` and `+` are written as they are (as the framework's JSON
  formatter writes an answer of a host whose JSON options name no encoder; a host that names one
  keeps it). The answers are `application/json; charset=utf-8` with
  `X-Content-Type-Options: nosniff`, never part of an HTML page; a client that writes a value into
  one escapes it there. The schema document is the base package's and keeps its own spelling (its
  revision is the hash of its bytes).

### Batch

`POST /oxql/batch` answers 200 with `{ "results": [ … ] }`, one entry per query in order, unless
the batch itself is refused: 400 `BATCH_TOO_LARGE`, 413 `REQUEST_TOO_LARGE`, or a framework
response (a body without a `queries` array is a 400 ProblemDetails). An entry is either a success body (it has `items`)
or a refusal envelope (it has `type`); an entry carries no status of its own, so a caller derives
it from `type` with the table above. Queries of a public batch run one after another on the host;
`maxTimeMs` bounds the whole batch: it is one deadline, and each query runs under what is left of it
when it starts (never above the host's own ceiling), so the batch ends within it. The queries of an
internal batch (a caller's keyed fetch, and the ones a host sends itself for its own targets) run
side by side, `Execution:BatchConcurrency` at once, under the same one deadline and answered in the
order asked. A member the batch does not have (a batch-level `strict`, say) is a 400
`UNKNOWN_REQUEST_MEMBER` under contract 2.

## Limits

Every limit is bound from the host's `OxQL` section and brought into range when the engine is
registered: a value below 1 is raised to 1 (`MaxOffset` and `Cache:NegativeResolveTtlSeconds` to 0;
a `MaxOffset` of 0 turns offset jumps off), `DefaultPageSize` and `ResolveKeyChunk` are clamped to
`MaxPageSize`, `MaxSemiJoinIds` to `MaxOffset`, `MaxContinuedStages` to `MaxPipelineStages`,
`MaxFlattenDepth` to 12, and each adjustment is logged as a warning when the engine is built. All
twenty-three `Limits` values are published under `limits` on `GET /oxql/health`, with the effective
`chainTimeoutMs`, `negativeResolveTtlSeconds` and the seventeen explain limits (42 entries). The fifteen
a caller checks a request against before sending it are also published in the schema document's
`limits` by the Simplic base package (column */schema*).

| key (`OxQL:`) | default | enforced by | code | /schema | cross-service reach |
|---|---|---|---|---|---|
| `Limits:MaxPageSize` | 500 | binder, `page.limit` | `PAGE_SIZE_EXCEEDED` | yes | A resolve chunk and a semi-join page are asked of the owner as one page: the owner's `MaxPageSize` must be at least this host's `ResolveKeyChunk` and `min(MaxPageSize, MaxSemiJoinIds)`, or the owner refuses and the caller answers 422 `RESOLVE_REFUSED` |
| `Limits:DefaultPageSize` | 100 | binder, a page without `limit` or no page stage | — | yes | — |
| `Limits:MaxPipelineStages` | 20 | binder; the scope stage does not count | `MAX_PIPELINE_STAGES_EXCEEDED` | yes | an owner query holds the key match (or `keyedBy`), the filter, the continued stages, the projection and the page, and is checked against the owner's value |
| `Limits:MaxLookupStages` | 5 | binder | `MAX_LOOKUP_STAGES_EXCEEDED` | yes | — |
| `Limits:MaxUnwindStages` | 5 | binder | `MAX_UNWIND_STAGES_EXCEEDED` | yes | — |
| `Limits:MaxResolveStages` | 8 | binder: resolve stages bound on this host, local and remote | `MAX_RESOLVE_STAGES_EXCEEDED` | yes | continued stages are counted by the owner that binds them, not here |
| `Limits:MaxContinuedStages` | 8, never above `MaxPipelineStages` | binder: stages continued under one keyed or remote alias | `MAX_CONTINUED_STAGES_EXCEEDED` | yes | the owner checks the forwarded stages against its own limits again |
| `Limits:MaxGroupFields` | 20 | binder, keys and aggregates together | `MAX_GROUP_FIELDS_EXCEEDED` | yes | — |
| `Limits:MaxProjectionFields` | 500 | binder | `MAX_PROJECTION_FIELDS_EXCEEDED` | yes | the paths a join after the page asks its owner for (the projection's under its alias) become the owner's projection and are checked against the owner's value |
| `Limits:MaxConditions` | 200 | binder; leaf conditions, `any` members, lookup and local resolve filters, and semi-join conditions; a condition also nests at most 32 levels of `and`, `or`, `not` and `any` (fixed, `Binder.MaxConditionDepth`) | `MAX_CONDITIONS_EXCEEDED` | no | a remote resolve's `filter` is bound, and counted, by the owner |
| `Limits:MaxVariables` | 64 | binder | `MAX_VARIABLES_EXCEEDED` | no | — |
| `Limits:MaxOffset` | 5 000 | binder, `page.offset` of contract 1 and of an internal call (a contract 2 page has none); not a cursor after `group` or `unwind` | `MAX_OFFSET_EXCEEDED` | yes | A semi-join reads the owner's matching ids page by page with `offset` up to this host's `MaxSemiJoinIds`; the owner's `MaxOffset` must be at least that, or the owner refuses (422 `RESOLVE_REFUSED`) |
| `Limits:CountCap` | 100 000 | binder and compiler: the ceiling of every count. A contract 2 count never counts further (`pageInfo.count.stoppedBy: "cap"`); a contract 1 count stops at it | contract 1: diagnostic `TOTAL_COUNT_CAPPED` | no | a semi-join asks the owner for a count with its first page; a count the owner capped only makes the walk go page by page |
| `Limits:MaxSemiJoinIds` | 5 000, never above `MaxOffset` | remote resolver | 422 `SEMI_JOIN_TOO_LARGE` | no | refused after the first owner page when the owner's count already exceeds it |
| `Limits:ResolveKeyChunk` | 500, never above `MaxPageSize` | keyed fetch: keys per owner query; a grouped query (`keyedBy`, two rows per key) carries at most `MaxPageSize / 2` keys | — | no | the owner's `MaxPageSize` and `MaxRequestBytes` bound it |
| `Limits:MaxResolveKeys` | 10 000 | keyed fetch: keys asked of owners per request, over every keyed stage and target in stage order; keys the cache answers are free | diagnostic `RESOLVE_PARTIAL`; refused under `strict` | no | each owner applies its own to what it forwards further |
| `Limits:MaxRequestBytes` | 262 144 | request-size filter, before the body is read | 413 `REQUEST_TOO_LARGE` | no | guards the owner's internal batch route too where the host puts the same filter on it (the Simplic base package does); an owner answering 413 is an unreachable owner to the caller |
| `Limits:MaxBatchQueries` | 10 | query service | `BATCH_TOO_LARGE` | yes | The keyed fetch splits an owner's batch at the owner's `maxBatchQueries` as its shallow health last reported it, and at this host's own value until that is known (before the first health measurement, or with a remote client that does not report owners). An owner configured lower that has not been measured refuses the whole batch with HTTP 400: resolved aliases are `null` with `RESOLVE_UNREACHABLE`, a semi-join is 422 `RESOLVE_UNAVAILABLE`. Keep the value equal across services |
| `Limits:RegexMaxLength` | 200 | binder | `REGEX_TOO_LONG` | yes | a `regex` under a remote alias is checked by the owner |
| `Limits:MaxLookupLimit` | 100 | binder: the largest lookup `limit` and the default when none is given; the most targets an `elements: "all"` alias holds | `LOOKUP_LIMIT_EXCEEDED`; diagnostics `LOOKUP_TRUNCATED`, `RESOLVE_TRUNCATED` | yes | — |
| `Limits:MaxFlattenDepth` | 5, clamped to 1–12 | compiler: the levels an `unwind` with `flatten` descends | diagnostic `UNWIND_DEPTH_TRUNCATED` | yes | — |
| `Count:StepRows` | 1 000 | binder: the rows the count step beside a contract 2 page counts (`page.count`), and the first step of a count-only request, whose next step is sized to the time left | above it the answer is a lower bound (`stoppedBy: "step"` beside a page) | no | — |
| `Count:BudgetMs` / `Count:MaxBudgetMs` | 500 / 5 000 | the time a contract 2 count has when the request names none, and the most a request may name with `page.count.budgetMs` (a larger one is clamped): the count step's own `maxTimeMS`, the longest an answer waits for it, and the budget of a count-only ladder | `pageInfo.count` `unknown` / `timeout`, or a lower bound `stoppedBy: "budget"` / `"time"`; never a refusal | no | — |
| `Limits:MaxReportPageSize` | 5 000 | binder: the largest `page.limit` of a `strict` request without a `cursor` (at least `MaxPageSize`); beyond `MaxPageSize` the page limit times the rows its lookups return per row (their limits, one under `first`) stays within `MaxPageSize` × `MaxLookupLimit` × `MaxLookupStages` (250 000) | `PAGE_SIZE_EXCEEDED`; 422 `PAGE_INCOMPLETE` when more rows match | yes | — |
| `Limits:MaxReportedRows` | 50 | the rows one `RESOLVE_MISSING` or `RESOLVE_AMBIGUOUS` lists | — (`truncated: true`) | no | owner-reported rows are mapped back and capped here |
| `Execution:MaxTimeMs` | 10 000, clamped to 1–60 000 | `maxTimeMS` of the page and the count aggregate, and of a `strict` request's truncation checks, which are sent with them and so end within the same limit; a batch's `maxTimeMs` only lowers it | 504 `QUERY_TIMEOUT` | no | the server ends a command at its next interrupt check after the limit, so a command that finishes within milliseconds of it is still answered (a page that comes in one reply has no later check to fail at); the time left of this budget, capped by `ResolveTimeoutMs` or `ChainTimeoutMs`, is sent to the owner as the batch's `maxTimeMs`, which the owner applies as its own ceiling |
| `Execution:ResolveTimeoutMs` | 2 000, never above `MaxTimeMs` | keyed fetch: one owner call of a plain remote resolve | `RESOLVE_TIMEOUT` (resolve), 422 `RESOLVE_UNAVAILABLE` (semi-join) | no | the whole semi-join phase is also bounded by the request's remaining budget |
| `Execution:ChainTimeoutMs` | 6 000, never above `MaxTimeMs` | keyed fetch: one owner call carrying continued stages or a typed, item, converted or element-wise resolve; the time left less 50 ms, at most this | `RESOLVE_TIMEOUT` | no | the owner budgets its own owner calls from what is left of it, so time bounds the whole chain |
| `Execution:BatchConcurrency` | 4, clamped to 1–16 | how many queries of one internal batch run at once, at the owner and at a host answering its own targets; `1` runs them one after another | — | no | each query in flight holds a connection (two with a count); a public batch always runs one query after another |
| `Execution:AllowDiskUse` | `true` | aggregate option | 422 `QUERY_TOO_EXPENSIVE` when `false` | no | — |
| `Execution:SlowQueryMs` | 1 000 (`0` off) | warning log line for a slower request | — | no | every aggregate of a request carries the request's correlation id as its `comment` (cut at 128 characters), at every owner the request reaches, so a command in the database's profiler or slow-query log is found from the log line and back; the page's cursor batch is its limit plus two (at most 10 000): one above what the pipeline's own `$limit` lets through, so the first reply closes the cursor and no `getMore` follows unless the page exceeds 16 MB |
| `Cache:ResolveTtlSeconds` / `OwnerFetchCacheMaxEntries` | 60 / 50 000 | the owner-fetch cache: resolved rows (remote and in-process targets) and semi-join ids, per organisation; the budget holds per mode (rows, ids) | — | no | an owner's change is seen by callers within the TTL |
| `Cache:NegativeResolveTtlSeconds` | 10 (`0` caches none) | the owner-fetch cache: keys an owner answered as not found; a `strict` request reads past them | — | no | — |
| `Cache:AddonDefinitionTtlSeconds` | 30 | the host's addon definition cache | — | no | — |
| `Cache:HealthProbeTtlSeconds` | 10 | reachability measurements behind `/oxql/health` | — | no | the same measurement reads each owner's engine version and `maxBatchQueries` |
| `Explain:Enabled` | `true` | `POST /oxql/explain` answers; 404 otherwise | — | no | — |
| `Explain:RemoteTimeoutMs` | 1 500 | one explain's wait for owners' internal explain in all (the remote check and forwarded catalog entries) | note `REMOTE_UNCHECKED` | no | — |
| `Explain:TimeoutMs` | 2 000 | the wall time of one explain; once it is spent no further owner is asked | note `EXPLAIN_LIMIT` (`time`) | no | an internal explain carries what is left, so an owner never outlasts its origin |
| `Explain:MaxRequestBytes` | 65 536 | request-size filter on `POST /oxql/explain`, before the body is read (the lower of it and `Limits:MaxRequestBytes`) | 413 `REQUEST_TOO_LARGE` | no | explain is rare and cheap to refuse; no bound is a way to load a service |
| `Explain:MaxStages` / `MaxCatalogEntries` / `MaxShapeDepth` | 30 / 10 / 3 | the parsed explain body, before the model, the binder or an owner is touched | 400 `EXPLAIN_LIMIT` | no | an unknown `include` or `remote` value is `EXPLAIN_LIMIT` too |
| `Explain:DefaultShapeDepth` / `MaxTypeMembers` | 2 (never above `MaxShapeDepth`) / 300 | only with `include: ["types"]`: the levels of member rows listed below each root when the request names no `shape.depth` / the member rows of one type, then `truncated` | — | no | by default an answer names its types and lists no member; an owner writes its rows out at the depth the origin was asked for |
| `Explain:MaxAnswerBytes` | 262 144 | the answer: over it the member rows (where asked for) keep their first level, then the plan goes | note `EXPLAIN_TRIMMED` | no | — |
| `Explain:MaxOwnerServices` / `MaxOwnerCalls` | 4 / 8 | the distinct owner services one explain asks / the owner calls it causes in all, one per service and round, transitive ones included | note `EXPLAIN_LIMIT` (`ownerServices`, `ownerCalls`) | no | an internal explain carries the calls left; an owner starts none beyond them |
| `Explain:MaxBatchChecks` | 64 | the checks one internal explain carries (`checks`): what one round of an origin's explain asks one owner, one per remote target of a keyed stage and one per distinct condition asked of that owner | origin: note `EXPLAIN_LIMIT` (`checks`) for what exceeds it; owner: 400 `EXPLAIN_LIMIT` before anything is bound | no | — |
| `Explain:RatePerMinute` / `RateBurst` | 120 / 30 | the public explain route, per organisation and user: a token bucket that admits an explain, before the body is read | 429 `EXPLAIN_LIMIT` (`rate`) with `Retry-After` | no | separate from run traffic; an explain that binds here and asks no owner costs nothing else |
| `Explain:OwnerRatePerMinute` / `OwnerRateBurst` | 20 / 5 | the same identity: a second token bucket, charged once when an explain is about to make its first call to an owner | never a refusal: the explain asks no owner and answers from what is kept (`cache.complete: false`, note `EXPLAIN_LIMIT` with `params.limit: "ownerRate"`, `max`, `retryAfter`) | no | an answer kept by an earlier check is served without a charge |
| `Explain:MaxConcurrentPerUser` / `MaxConcurrentPerHost` | 2 / 8 | explains in flight on the public route, per user and per host | 429 `EXPLAIN_LIMIT` (`concurrentPerUser`, `concurrentPerHost`) | no | — |
| `Explain:MaxConcurrentPerCaller` | 4 | internal explains in flight per calling service; the host of the internal route enforces it (the Simplic base package does) | 429 `EXPLAIN_LIMIT` (`concurrentPerCaller`) | no | the origin notes the part `REMOTE_UNCHECKED` |

Fixed bounds, not configurable:

| bound | value | code |
|---|---|---|
| path segments / segment length | 64 / 256 characters | `INVALID_PATH` |
| pattern size the server compiles | 32 764 UTF-8 bytes, for a caller's `regex` and for a text the engine turns into a pattern | `REGEX_TOO_LONG`, `INVALID_OPERAND` |
| model depth / paths per entity | 32 segments / 20 000 paths; deeper members are not in the model | model build finding |
| `Execution:MaxTimeMs` ceiling | 60 000 ms | — |
| health reachability probe | 2 s per service | the service counts as not reachable |
| listed indexes for the explain advisory | cached 60 s per collection | — |
| owner answers to explain's remote check and forwarded catalog entries | kept 30 s per organisation, user, owner, owner schema revision and forwarded body, at most 1 000 entries and 16 MB; failures and answers that were not complete are not kept, and an answer naming another revision of a service retires what was kept for the old one | — |
| records per key a grouped owner query returns | 2 (the second means `ambiguous`) | — |
| ask-again rounds for a remote union target without the paths it lacks | 2 per request | — |
| JSON nesting of request and answer bodies | 256 (MVC's default is 32; explain answers of flattening and chain queries nest deeper) | — |

## `GET /oxql/health`

Anonymous, always 200, and never waits for another service:

```jsonc
{
  "status": "healthy",                    // "degraded" when a referenced service is not configured or was last measured unreachable
  "service": "oxql",
  "engine": { "version": "2.1.0.0", "contract": 2 },
  "capabilities": ["resolve.remote", "semiJoin", "resolve.chain", "lookup.remote", "explain", "compat.v1"],
  "limits": {
    "maxPageSize": 500, "defaultPageSize": 100, "maxPipelineStages": 20, "maxLookupStages": 5,
    "maxUnwindStages": 5, "maxResolveStages": 8, "maxGroupFields": 20, "maxProjectionFields": 500,
    "maxConditions": 200, "maxVariables": 64, "maxOffset": 5000, "countCap": 100000,
    "maxSemiJoinIds": 5000, "resolveKeyChunk": 500, "maxResolveKeys": 10000, "maxRequestBytes": 262144,
    "maxBatchQueries": 10, "regexMaxLength": 200, "maxLookupLimit": 100,
    "maxFlattenDepth": 5, "maxContinuedStages": 8, "maxReportPageSize": 5000, "maxReportedRows": 50,
    "chainTimeoutMs": 6000, "negativeResolveTtlSeconds": 10,
    "explainRemoteTimeoutMs": 1500, "explainTimeoutMs": 2000,
    "explainMaxRequestBytes": 65536, "explainMaxStages": 30, "explainMaxCatalogEntries": 10, "explainMaxShapeDepth": 3,
    "explainDefaultShapeDepth": 2, "explainMaxTypeMembers": 300, "explainMaxAnswerBytes": 262144,
    "explainMaxOwnerServices": 4, "explainMaxOwnerCalls": 8, "explainMaxBatchChecks": 64, "explainRatePerMinute": 120, "explainRateBurst": 30, "explainOwnerRatePerMinute": 20, "explainOwnerRateBurst": 5,
    "explainMaxConcurrentPerUser": 2, "explainMaxConcurrentPerHost": 8, "explainMaxConcurrentPerCaller": 4
  },
  "remote": [ { "service": "vehicle", "configured": true, "reachable": true } ]
}
```

- `engine.contract` is the contract the engine speaks. `2` is the marker of this package: an engine
  that reports it has the whole contract 2 language (`strict`, `is`, `flatten`, every stage member of
  contract 2) and the explain answer described below. A caller gates on it, never on a capability
  per feature.
- `engine.version` is the version of the `OxQL.Core` assembly the host runs, which is the package
  version.
- `capabilities` lists only what depends on the host; what every contract 2 engine has is said by
  `engine.contract` and is not listed (`batch`, `group.page`, `page.offset`, `any` and
  `unwind.keepPath` are gone from the list):
  `resolve.remote`, `semiJoin`, `resolve.chain` and `lookup.remote` (a `lookup` whose `from` is another
  service's entity) when the host installed a remote query client;
  `explain` when `Explain:Enabled` (the default); `compat.v1` while `Compat:Enabled`.
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

Everything about a query without running it, in one answer: whether it binds and every error, each
stage with where it runs and the shape of the row after it, every alias, the types the row's roots
have, the result columns, the owners asked, engine-behaviour notes, and, on request, the plan. On by
default (`Explain:Enabled`); 404 while it is off. It is the rule source of the Angular OxQL Studio:
the builder, completion, hover and markers read explain and nothing else, with one call per edit.
There is no separate validate or describe endpoint. The request (a plain query or the envelope
with `include`, `shape`, `remote` and `catalog`) is in
[`oxql-query-syntax.md`](oxql-query-syntax.md#explain-request).

Explain is the bind trace, normalised. Nothing in it is derived a second way: the shapes are the
binder's, the owner facts are the plan a run builds, and what lies at another service is what that
service's engine answered.

**It enriches the schema documents and never re-sends them.** A caller holds the schema document of
each service (`GET /schema`), which describes every member of every entity. The answer therefore
names its types by reference (entity, service, the revision of that service's document) and says
only what the query adds: the aliases, and per root and stage a rule that says where the members
stand. A member's flags are a function of its descriptor and that rule (below). A caller without
schema documents asks for the member rows with `include: ["shape", "notes", "types"]`.

```jsonc
{
  "valid": true, "contract": 2,
  "engine": { "version": "2.1.0.0", "contract": 2, "capabilities": ["resolve.remote", "…", "explain"] },
  "etag": "W/\"x3:4575645a135436d89b17f6ade5f1f30d\"",
  "revision": { "schema": { "fleet": "sha256:…", "ledger": "sha256:…", "transport": "sha256:…" }, "addons": null },
  "cache": { "maxAge": 30, "dependsOn": ["fleet", "transport"], "complete": true },
  "errors": [],                                     // every binding error when valid is false
  "diagnostics": [],                                // what binding diagnosed, also when it does not bind
  "notes": [ { "code": "JOIN_AFTER_PAGE", "stage": 4, "path": "sourceLine", "message": "…",
               "params": { "alias": "sourceLine", "kind": "resolve" } } ],
  "entry": { "shape": { "paging": "cursor", "grouped": false, "unwound": [],
                        "roots": { "": "t:ledger.transaction" }, "rules": {} } },
  "stages": [
    { "index": 1, "kind": "unwind", "status": "ok",
      "reads": [ { "path": "items", "use": "unwind" } ],
      "creates": ["item", "position"],
      "shape": { "paging": "cursor", "grouped": false, "unwound": ["items"], "removed": ["items"],
                 "roots": { "": "t:ledger.transaction", "item": "t:ledger.transaction#items", "position": "k:int" },
                 "rules": { "": "r:1", "item": "r:2", "position": "r:3" } } },
    { "index": 4, "kind": "resolve", "status": "ok",
      "placement": { "executor": "keyed-remote", "phase": "afterPage", "host": "transport", "owner": 0 },
      "reads": [ { "path": "erpLine.sourceBillingLineReference.id", "use": "resolveKey", "alias": "erpLine" },
                 { "path": "erpLine.sourceBillingLineReference.type", "use": "caseCondition", "alias": "erpLine" } ],
      "creates": ["sourceLine", "sourceParent"],
      "shape": { "…": "…", "roots": { "…": "…", "sourceLine": "u:sourceLine", "sourceParent": "u:sourceParent" },
                 "rules": { "…": "…", "sourceLine": "r:5", "sourceParent": "r:5" } } }
  ],
  "aliases": {
    "sourceLine": {
      "stage": 4, "node": "remote", "entities": ["transport.shipment#billingLines", "transport.tour#billingLines"],
      "heldBy": "transport", "complete": true, "lookupOn": true, "type": "u:sourceLine",
      "loads": ["totalPrice"], "shows": ["totalPrice"],   // the projection names sourceLine.totalPrice
      "reference": { "path": "erpLine.sourceBillingLineReference.id",
                     "cases": [ { "when": { "path": "type", "equals": ["logistics"] },
                                  "targets": [ { "entity": "transport.shipment", "item": "billingLines", "field": "id", "remote": true },
                                               { "entity": "transport.tour", "item": "billingLines", "field": "id", "remote": true } ] } ] },
      "outcome": { "values": ["ambiguous", "not_found", "invalid_key", "owner_unanswered"] },
      "targets": [ { "target": "transport.shipment#billingLines", "service": "transport", "remote": true, "grouped": true,
                     "owner": 0, "continued": [5, 6, 8], "notApplicable": [7], "type": "t:transport.shipment#billingLines" },
                   { "target": "transport.tour#billingLines", "…": "…", "continued": [7], "notApplicable": [5, 6, 8] } ] },
    "deliveringTour": { "stage": 5, "node": "remote", "entities": ["transport.tour"], "heldBy": "transport", "complete": true,
                        "continuedFrom": { "alias": "sourceParent", "target": "transport.shipment" }, "type": "t:transport.tour" }
  },
  "types": {
    "t:ledger.transaction":       { "entity": "ledger.transaction", "service": "ledger", "schemaRevision": "sha256:…" },
    "t:ledger.transaction#items": { "entity": "ledger.transaction", "item": "items", "service": "ledger", "schemaRevision": "sha256:…" },
    "t:transport.shipment":       { "entity": "transport.shipment", "service": "transport", "schemaRevision": "sha256:…" },
    "t:fleet.vehicle":            { "entity": "fleet.vehicle", "service": "fleet", "schemaRevision": "sha256:…" },   // as transport's answer named it
    "u:sourceParent":             { "of": ["t:transport.shipment", "t:transport.tour"] }
  },
  "rules": {
    "r:1": { "unwound": ["items"] },                 // the entity row once items is unwound
    "r:2": { "self": "42800" },                      // an element: its members have their own flags
    "r:3": { "self": "588ff" },                      // a scalar
    "r:5": { "self": "40000", "under": "owner" }     // an owner's rows, joined after the page
  },
  "result": { "paging": "cursor",
              "columns": [ { "path": "number", "kind": "string", "nullable": false, "root": "", "present": "always" } ] },
  "owners": [ { "service": "transport", "remote": true, "route": { "apiName": "transport-api" },
                "answered": true, "calls": 2, "cached": false, "ms": 18,
                "engine": { "version": "2.1.0.0", "contract": 2 } },
              { "service": "fleet", "remote": true, "via": "transport", "…": "…" } ],
  "catalog": [],
  "plan": { "bound": { … }, "stages": [ … ], "count": [ … ], "collation": { "locale": "de", "strength": 1 } },   // only with include: ["plan"]
  "advisory": [ … ]                                                                                             // only with include: ["indexes"]
}
```

- **`valid`.** A query that does not bind is answered 200 with `valid: false` and every error (and
  the `diagnostics` binding produced up to there), so a caller still gets the stages, the aliases and
  the shapes of the part that binds; the joins then carry no `placement`, `result.columns` is empty,
  and there is no `plan`. A query that binds but that this host cannot run (a remote resolve without
  a remote query client: `RESOLVE_UNAVAILABLE`) or whose continued stages an owner refuses is
  `valid: false` too. It bound here, so its joins keep their `placement` and `result.columns` is
  filled; only the stage an owner refused (`status: "error"`) has no `placement`. Refusals remain only for what stops explain before binding: 400 for a
  malformed body or one past an explain bound, 403 without an organisation, 413 for a body over the
  limit, 429 over the rate, 500 for an engine fault. `contract` is the contract the request was read
  as; a contract 1 request is rewritten by the compatibility binder and answered the same way.
- **`engine`.** `contract` is the contract the engine speaks. `2` is the marker of this package: an
  engine that reports it has the whole contract 2 language and this answer.
- **Absent members.** A member that would be null is left out, except where null says something:
  a root whose owner did not answer (`roots`), `loads` and `shows`, an owner never asked
  (`answered`), and the revisions.
- **`etag`, `revision`, `cache`.** `etag` is a validator of the answer itself: it changes when the
  request, who asks (organisation, user) or anything the answer says changes (see *The validator*
  below). `revision` names what the answer was bound against:
  `schema`, per service, the revision of the schema document of this host and of every owner that
  answered, the owners those asked included (`null` for a host that publishes none), and `addons`, a
  hash of the asking organisation's addon definitions it read. A caller whose document of a service
  has another revision loads it again before it reads that service's types.
  `cache.dependsOn` lists the owner services the answer depends
  on, transitive ones included; `cache.complete` is false when something the answer would say is
  missing (an owner did not answer, a limit was hit, the answer was trimmed), and such an answer is
  never kept as the complete one.
- **`stages`**, one per caller stage: `status` `ok`, `error`, or `skipped` (it failed only because it
  reads an alias an earlier stage failed to create); `placement` for a join that runs, else absent
  (a stage that is no join, a join nothing reads, every join of a request that does not bind here,
  and a stage with another `status` than `ok`):
  `executor` (`inline`, `keyed-local`, `keyed-remote`, `continued`), `phase` (`beforePage`,
  `afterPage`, `owner`), `host` (the service whose engine runs the stage) and `owner` (the index of
  its owner in `owners`); `creates`, the names of the aliases it adds; `shape`, the row after the
  stage; `reads`, what the stage reads off the row (below). The row before stage `i`
  is `stages[i-1].shape`, and `entry.shape` before the first.
- **`filter`**, one entry per entry of the request's top-level `filter`, in the request's order;
  absent when the request carries none. `id` (when the entry has one), `status` (`ok`, or `error`
  when a condition of the entry did not bind; the errors carry `filter`), and `placed`: where its
  conditions run, in pipeline order, one element per point: `{ "before": 0, "paths": [...] }` at
  the entry (before the first caller stage) or `{ "after": i, "paths": [...] }` right after caller
  stage `i`, with `"having": true` when that point lies after a group. `paths` are the paths the
  conditions placed there read, as written. The placed conditions are no caller stages: `stages`
  has one entry per stage the caller wrote, and with `include: ["plan"]` the emitted `$match`
  stands at the named point. A note or an error about a placed condition carries `filter` (the
  entry's `id`, or its index) and no `stage`.

  ```jsonc
  "filter": [
    { "id": "rows", "status": "ok", "placed": [ { "before": 0, "paths": ["isDeleted"] }, { "after": 0, "paths": ["item.quantity.value"] } ] },
    { "status": "ok", "placed": [ { "after": 1, "paths": ["total"], "having": true } ] } ]
  ```
- **`reads`**, per stage, is the binder's read ledger: `{ path, use, alias? }` each, in the order
  bound. `path` is the path as the row has it (an `any`'s inner path under its collection). `use` is
  one of `match`, `sort`, `project`, `unwind`, `groupKey`, `aggregate`, `resolveKey` (the reference a
  resolve follows), `caseCondition` (the sibling member, or the object whose variant, that picks a
  reference's case: the read a caller cannot see in the query) and `lookupOn` (the parent key a
  lookup joins on). `alias` is the join whose rows hold the path and which therefore loads it (a
  path under an unwound copy or an element of a lookup names the lookup); it is absent for a path
  of the entity row. A join's own `filter` and `sort` are bound against its target and
  are no reads of the row, and neither is a path an exclusion names. A stage continued at an owner
  carries the reads its owner answered, in this host's paths, since only the owner binds it.
  "Why is this member loaded" is every read whose `alias` is the join's.
- **`shape`**: `paging` (`cursor` at every stage under contract 2: a page is addressed by its
  cursor whatever the shape; a contract 1 explain still says `offset` after an unwind or group),
  `grouped`, `unwound`, `projection` (the kept paths of an inclusion projection; absent
  while none ran), `removed` (the paths an exclusion projection removed and the collections an
  unwind took out of the row, which under contract 2 is every unwound collection and lookup
  alias; absent when there is none), `roots` (each root the row
  carries, `""` the entity itself, pointing to its type: `t:<entity>[#item]`, `u:<alias>`,
  `k:<kind>` for a scalar or a group output, `null` for an alias whose owner did not answer), and
  `rules` (per root, the rule that says where its members stand here; a root without one has its
  type's own flags).
- **`aliases`**, by name: `stage` (the creating stage), `node` (`entity`, `element`, `array`,
  `remote`, `keyed`, `scalar`, `group`), `entities` (the targets it may hold, `entity` or
  `entity#item`), `type`, `heldBy` (the service that holds its rows), `complete` (false when an
  owner that knows it did not answer), `lookupOn` (a lookup may name it as its parent row), and where
  they apply `source` (an element's collection), `kind` (a scalar's), `many` (it holds an array),
  `parentOf` (it is the owning row of that alias), `reference` (the reference a resolve follows, as
  bound, with the `name` the engine derived for its relation where this host's model holds the member), `targets` (each target with its `service`, whether it is `remote`, its `type`, the `owner`
  it is asked at, whether its owner query is `grouped`, and the stages `continued` there or
  `notApplicable` to its rows), `continuedFrom` (`{ alias, target }` of a stage continued at an
  owner; for a union join the alias of the keyed stage and no target), `branches` (only on the
  alias of a union join, `byTarget`: per branch in the order written `{ anchorTarget, path,
  elements?, entities, heldBy, status, types }`: the target of the alias it continues under, its
  path, what it reaches as that target's owner answered (`entities`, and `types` as entries of
  `types`), the service that runs it, and `status` `ok`, `error` (its owner refused it; the error
  names it in `params.owner.target`) or `unanswered`; the alias's own `type` is the union of what
  the branches reach), `outcome` (`{ as?, values }`: `as` the row member that carries the join's outcome, present when the
  stage names one with `outcomeAs`; `values` the data-loss outcomes the join may have), `droppedAt` (the stage whose projection
  took it out of the row) and `becomes` (a later stage holds it differently: an unwound lookup
  alias). A join's alias (a resolve's or lookup's `as`, a `parentAs`, an alias a continued stage adds)
  also says what the join loads: `loads` (the paths fetched under the alias, in ordinal order: for a
  join in the aggregate its key, what stages read and its output set; for a join after the page what
  its owner is asked for beside the member it is matched by), and `shows` (the output set: what the row
  carries under the alias, empty when the row does not carry it). There is no `hint` member: a
  contract 2 join has no `select`. `loads` and `shows` are `null` for an alias of an owner's rows
  kept whole: the owner's own key and display member. For an alias a continued stage adds, the two
  are its owner's answer, absent when the owner did not answer.
  The member a stage names with `outcomeAs` is an alias of its own: `node: "scalar"`, `kind:
  "string"`, `outcomeOf` (the alias of its join), `values` (every outcome the member may hold there:
  an inline resolve's `resolved`, `reference_null`, `not_found`, with `ambiguous` onto a field that
  is not the key and `excluded` with a filter; a keyed or remote one's every outcome but
  `not_applicable`; a continued stage's all eight), `type: "k:string"` and `heldBy` (the service
  that writes it). The stage lists it in `creates`, every shape after it in `roots`, and
  `result.columns` as `{ kind: "string", nullable: false, root: "", present: "always" }`. Its rule
  says what may be done with it: an inline resolve's is compared (`eq`, `neq`, `in`, `nin`) and
  grouped by; one written after the page is projected only; none is sortable.
- **`types`**, by reference. `t:<entity>[#item]` names a concrete type: `entity`, `item` (the item
  collection whose element it is), `service` (the service that owns the entity) and
  `schemaRevision` (the revision of that service's schema document; absent when it publishes none).
  Its members are the entity's property list in that document, walked from the entity: a member
  whose leaf (arrays unwrapped) is an object has that entry's properties as members, unless the
  entry is already on the way from the entity; an item type's members are the paths below its
  collection. The asking organisation's addon definitions (`GET /schema/addons`) are the members of
  the entity's `addon` member. `u:<alias>` is the union type of an alias with several targets:
  `of`, its targets' types. A path under it is the first target's in `of` that has it, as a path
  under a keyed alias binds.
- **Types of another service** are never built here. A remote alias's type is what its owner
  named in its answer to the check of the owner query the run sends, and a continued alias's is
  what its owner bound it to; an owner's answer carries its own owners' types in turn, so an entity
  this host cannot describe is named with its service and revision all the same. For a request
  that does not bind there is no owner query to check, and the owner names its entity itself. An
  owner that does not answer leaves the alias `complete: false` without a type; nothing else stands
  in for it.
- **A member's flags** say what may be done with it at one point: the `operators` a condition
  admits (a member is filterable exactly when it admits one), `sortable`, `groupable`,
  `unwindable`, `projectable`, `folds` (a string comparison folds by default), `underCollection`
  (collections above it that are not unwound) and, on a reference, `follow` (`one`, `elements`,
  `none`). They are a function of the member's descriptor and of where it stands. Let `above` be
  the collections above it (up to the entity) that are not unwound, and `crossed` the arrays its
  path crosses below the root, itself included; a collection is an `array`, or a `dictionary` whose
  `storedAs` is `arrayOfDocuments` or `arrayOfArrays`, and an unwound collection is its element.
  A member that is not stored (`stored: false` on it or above it) is projectable and nothing else.
  Otherwise, with `leaf` the descriptor with arrays unwrapped: it is filterable when the leaf is a
  scalar or an enum not `storedAs: "document"`; then it admits `eq`, `neq`, `in`, `nin`, `exists`,
  the ordered operators on `int`, `long`, `double`, `decimal`, `date`, `dateTime`, `timeSpan`,
  `string` and `enum`, and `contains`, `startsWith`, `endsWith`, `regex` on a `string` not
  `storedAs: "codePoint"`; a stored member that is not filterable admits `exists`. With `above` 0
  an object whose pool entry has `variants` admits `is`, and an array of objects `any`. It is
  sortable when filterable, `above` is 0 and it is no array or dictionary; groupable when `above`
  is 0 and its kind is a scalar or an enum; unwindable when `above` is 0 and it is a collection; it
  folds when filterable and the leaf is a `string`; a member that declares a reference is followed
  as `one` when `crossed` is 0, `elements` when 1, `none` beyond. A flag id, where the answer
  writes one, is these flags packed into a hexadecimal number, the same at every engine: bits 0 to
  14 the operators (`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `in`, `nin`, `contains`, `startsWith`,
  `endsWith`, `exists`, `regex`, `is`, `any`), then `sortable`, `groupable`, `unwindable`,
  `projectable`, `folds`, two bits of `follow` (1 `one`, 2 `elements`, 3 `none`) and, from bit 22,
  `underCollection`.
- **`rules`**, the query's part of the flags: per root and stage (`shape.rules`), never per member.
  `self` is the flag id of the root itself (every named root has it). `under` says the rows are not
  the page's own: `collection`, a lookup's array (`above` and `crossed` are one more); `afterPage`,
  rows joined after the page from local targets (a member is projected and followed, nothing else,
  and counts no collection above it; with `many` the alias holds an array, and `crossed` is one
  more); `owner`, rows another service holds (nothing sorts, groups or unwinds; with `filter` a
  condition on a member travels to the owner as a semi-join, a variant test (`is`) among them,
  which the owner binds like any other; only `any` is refused here, `ANY_NOT_APPLICABLE`; without
  it no condition). `unwound` lists the collections below the root that are unwound here, relative
  to it. `unwoundAbove`, on the element of a nested collection, is the number of collections above
  its own that are unwound here: its members count that many fewer `above` than in their type. `shows` lists the paths the row carries under a join's alias where that is not every
  member (under contract 1, the join's select). A member is not in the row when `shape.removed`
  holds its path or one above it, when `shape.projection` holds neither its path, one above it nor
  one below it, or when `shows` does not. Every flag so derived is the binder's own answer for
  that path at that point (`Shape.Resolve`): the tests rebuild the written-out answer from the
  schema documents and the rules for every reference query.
- **`include: ["types"]`** writes the members out, for a caller without schema documents. Each
  `t:` entry then also carries `variants` (the names `is` accepts), `onlyFor` (the distinct variant
  sets its rows point to), `members` and `truncated`. A member row is
  `[path, kind, nullable, flags, more, onlyFor, reference, addon, description]` and ends at its last
  fact: `path` is dotted below the type, `nullable` is 0 or 1, `flags` the id of the member's own
  flags, `more` the rarer facts (`leafKind`, `displayName`, `stored: false`, `collection` for a
  dictionary stored as one, `enum`, `variants`, `flatten`, `flattenMembers`, `snapshotOf`,
  `deprecated`, `constraints`, and `children` when the member has members the rows do not list),
  `onlyFor` an index into the type's `onlyFor`, `reference` the declared reference (`simple`,
  `keyAs`, `cases`), `addon` the organisation's addon definition, and `description` only with
  `include: ["docs"]`. Rows go level by level to `shape.depth` (default `Explain:DefaultShapeDepth`, 2) and
  stop at `Explain:MaxTypeMembers` (300, then `truncated`); a `catalog` entry with the member's path
  as `prefix` reads what was cut. A `u:` entry then lists `members`, each `[path]`, `[path, have]`
  when not every target has it (`have`: indexes into `of`) and `[path, have, "unknown"]` when the
  targets' kinds disagree. `flagSets` holds each flag set under its id and, under `o:n`, the
  override sets `shape.flags` points to per root: `""` is the root itself, a path is a member that
  differs, `"~"` maps a member's own flags to what it has here, `"*"` is what every member not named
  has, and `null` means the member is not in the row. A reader takes the member's own entry, else
  the `"~"` entry of its own flags, else `"*"`, else its own flags. The rest of the answer is the
  same, rules included.
- **`result`**: `columns`, the final shape's visible members and roots, each with `path`, `kind`,
  `nullable`, the `stage` that created it (absent: the entry shape), its `root`, and `present`: when
  a row carries the key. `always`: on every row, the value may be null. `ifJoined`: absent on a row
  whose join found nothing. `ifVariant`: only on rows of the variants that have the member.
  `ifStored`: absent on a row whose stored record does not hold the member, or whose parent object
  is null. Members of a remote alias are `unknown`: their kind is in the owner's type. Under a
  join's alias the columns are its output set (`aliases.<alias>.shows`), each `ifJoined`, whoever
  runs the join: an alias a continued stage adds lists what its owner answered it shows. Only an
  alias whose output set this host does not know is one column of the alias itself (`kind: "object"`,
  `present: "always"`): an owner's alias kept whole (`shows: null`), or one whose
  owner did not answer; a keyed alias or a remote lookup's that holds an array of rows is one
  `array` column. The
  outcomes a join may have are its alias's (`aliases.<alias>.outcome`).
- **`executable` and `basis`** (for an editor's save rule). Every error, diagnostic and note of an
  explain answer carries `basis`: what would have to change for the request to run. `request`:
  wrong as written, on any service and any data. `model`: against the model of the service that
  binds it, at the revision the answer names (an owner's own error keeps the basis it has at the
  owner). `limits`: above a configured limit of the binding host. `organisation`: the path lies
  under an addon bag and the code (`NOT_FILTERABLE`, `NOT_SORTABLE`, `INVALID_OPERAND`,
  `UNKNOWN_ENUM_MEMBER`) depends on the organisation's definition; the error then carries
  `params.addon { key, state }`, `state` one of `undefined`, `retired`, `object`, `defined`.
  `owner`: a part could not be checked (`RESOLVE_UNAVAILABLE`; the notes `REMOTE_UNCHECKED`,
  `EXPLAIN_LIMIT`, `EXPLAIN_TRIMMED`). `data`: only a run can tell (never an explain error; the
  note `SEMI_JOIN_NOT_BY_KEY` has it).
  `advisory`: the query runs. `RESOLVE_REFUSED` beside the errors it wraps has the strongest of
  theirs. The top-level `executable` is derived: `never` with an error whose basis is `request`,
  `model` or `limits`; `unknown` with no such error and another error or `cache.complete: false`;
  `yes` when the request binds and every part was checked. The table of every code is
  `OxQL.Core.Binding.Basis`; a test holds it complete. A run's refusal carries no `basis`.
- **`identity`** (on `result`, on `entry.shape`, and on the shape of a stage that changes it): what
  one row is and what identifies it. `kind: "entity"`: one record of `entity`, identified by the row
  paths in `key`. `kind: "element"`: one element of a collection of that record; `element` is
  `{ collection, alias, key?, index?, item: { entity, path }, orParent, flatten? }`: `key` the
  element's own key member where it has one (`<alias>.id`), `index` the member the unwind names
  with `includeIndex`, `item` the collection as the schema document names it (its
  `items[].aliases` are there), `orParent` for `preserveNull` (a record without elements is kept
  once, alias and index null). With several unwinds `element` is the last and `elements` lists
  all in order. An unwound lookup alias makes the row one child record: `entity` and `key` are the
  child's (`alias` the root it sits under) and `parent { entity, key }` is the record it was looked
  up from. `kind: "bucket"`: `bucket.keys` are the group's key aliases. `kind: "single"`: a group
  over no key. A join never changes the identity. A stage shape without `identity` has the one of
  the boundary before it.
- **`result.subjects`**: the entities a row holds, for what acts on an entity rather than on a row:
  `{ root, entity | entities, key, nullable, many, remote? }`, the row's own record under root
  `""` and every alias that holds records of an entity (an inline or keyed `resolve`, a `lookup`,
  an owning row `parentAs`, an alias a continued stage adds). `entities` for a union. `key` is the
  row path of the record's key, `null` where only the owner knows its name (read it from that
  service's schema document); the row carries it only when the projection names it. An alias that
  holds elements of an item collection is no subject. Empty after a `group`.
- **`result.order`**: the total order the page is taken in, as the server sorts:
  `[{ path, direction, tieBreak? }]`, the caller's fields first, then what completes them marked
  `tieBreak: true`: the entity key; after an unwind the key and one position per unwind (`path` the
  `includeIndex` member, else `{ positionOf: "<collection>" }`); after a group the keys the sort
  does not name. Empty for one row.
- **`result.capabilities` and a column's `can`**: per column what may be done with it at the last
  boundary, written once per distinct answer: `capabilities["c:n"] = { can: { filter: [operators],
  sort, group, aggregate: [functions], values }, why?: { filter?, sort?, group?, values?,
  aggregate? } }`, and each column carries `can: "c:n"`. `values` says that the distinct values can
  be asked for (a `group` by the path binds). `why` gives one reason per refused use, from a closed
  list: `owner` (another service's rows), `joinedAfterPage`, `underCollection`, `notScalar`,
  `notStored`, `unknownKind`, `addonUndefined`, `notInRow`, `notApplicable` (the fallback: none of
  the others holds, e.g. a scalar that still cannot be used that way); and for another service's
  member, where a condition is allowed and runs as a semi-join at its owner, `filter: "semiJoin"`
  when `can.filter` is what the member's kind admits there (its owner answered the explain's check:
  a guid lists `eq neq in nin exists`, no `contains`), or `filter: "ownerKind"` when no owner
  answered (`remote: "cached"` without a kept answer, an owner out of reach, the owner rate): the
  list is then every operator a condition may carry, **not narrowed to the member's kind**, which
  only the owner's type says; a client narrows it by the owner's schema document or offers it as
  unchecked. An outcome member of a join that runs after the page (`outcomeAs` of a keyed or
  remote resolve) is `joinedAfterPage` for every use. A condition that is written is checked at its
  owner whatever `can.filter` lists (see *remote*, *Conditions at owners*): a run that the owner's
  binder refuses (422 `RESOLVE_REFUSED` with the owner's `INVALID_OPERAND`) is a request explain
  answers `valid: false` with that `INVALID_OPERAND`. `sortVia` on a column of a join
  that cannot order the page names the same member in a copy of the joined record the row embeds
  (`snapshotOf`), when that path sorts; the engine never applies it. **A member that is no result
  column** has the same answer by rule: `can.filter` is its flag set's `operators`, followed by
  `dateTrunc` when the member is a `date` or a `dateTime`, admits `gte` and lies under no collection
  that is not unwound (the bucket test has no flag of its own, so no flag id changed), `can.sort` its
  `sortable`, `can.group` and `can.values` its `groupable` (the flags of its type, with the stage's
  rule or override set), and `can.aggregate` is every function for a number and all but `sum` and
  `avg` otherwise, wherever the member is in the row before the page (under a collection that is
  not unwound, `push` alone) (`ExplainFacts.CanOf`; the tests hold `can` equal to what the binder accepts).
- **`owners`**: each owner service once, in pipeline order, this host among them when it answers a
  keyed stage of its own (`remote: false`): `via` (the owner it was reached through, absent when this
  host asks it itself), `route` (`apiVersion` is the version the remote client routes the service to,
  `IRemoteOwnerInfo.ApiVersionOf`, absent when it does not say), `answered` (`null`: never asked),
  `reason` when it did not (`unsupported`, `unreachable`, `timeout`, `limit`), `calls` (one per round
  it was asked in), `cached`, `ms` and its `engine`; its schema revision is
  `revision.schema[service]`. With `include: ["plan"]` an owner also lists the
  `queries` it is sent, each once: `stage`, `alias`, `target`, `grouped`, the `query` with keys and
  the page limit elided as `"…"`, and the stages `continued` and `notApplicable`.
- **`catalog`**: one answer per `catalog` entry of the request, in order: `id`, `entity`, the `type`
  it points to in `types` (a reference, also for an entity of another service, which its owner
  names), `forwarded` (an owner answered it), `prefix` when the entry named one (it is checked;
  with `include: ["types"]` the `members` below it and `truncated` are listed), with
  `referencing: true` `referencedBy` (per path, the same-host entities whose
  members reference it there) and `remoteLookup`, or an `error` (`{ code, message, params }`). It is
  the one lookup of an entity outside the query.
- **`notes`** (`{ code, message, stage, path, params }`, in stage order, request-wide last) say what
  the engine will do that a reader of the query might not expect: `TEXT_FOLDS`,
  `PATTERN_FOLDS` (a `contains` or `endsWith` folds case, and accents at collation strength 1, as a
  pattern no index bounds; `params` `op`, `locale`, `strength`, `accents`),
  `PATTERN_FOLDS_CASE_ONLY` (contract 1 only), `EXACT_FORCES_EXACT`, `SOME_ELEMENT`, `NEQ_MATCHES_ABSENT`,
  `ONLY_FOR_VARIANTS`, `SNAPSHOT_COPY`, `JOIN_BEFORE_PAGE` (`params` `alias`, `kind`; and for a
  join only the display reads that is kept before an unwind, so that it runs once per parent and
  not once per element, `unwind`, the collection, and `rows`: `parents`, the rows ordered and cut
  before that unwind, or `written`, every row that reaches the join where it stands),
  `JOIN_AFTER_PAGE`, `OWNER_BINDS`,
  `REMOTE_UNCHECKED`, `SELECT_PATH_NOT_ON_TARGET`, `COUNT_CAP` and `OFFSET_PAGING` (contract 1 and
  internal calls only: a contract 2 request has neither a count cap to hit silently nor an offset),
  `LOOKUP_LIMIT`, `SEMI_JOIN_NOT_BY_KEY` (a condition on another service's member is asked of an
  owner whose rows are read by a member that is not their key: a run refuses, 422 `RESOLVE_REFUSED`,
  when a matching row does not carry that member, which only a run can tell; basis `data`, no
  effect on `executable`; `params` `alias`, `target`, `field`, `services`),
  `FILTER_BINDS_EARLIER` (a condition of the request's `filter` runs where its path
  first exists, and a later group gives the name to another thing; `params` `path`, `alias`),
  `PROJECT_AFTER_PAGE` (the projection at this stage runs after the page is taken, on the page's
  rows), `SORT_BEFORE_UNWIND` (the rows are ordered and cut to what the page can reach before the
  unwind at this stage, also when the sort names the element's position after the parents'
  members; `params` `path`, `alias`), `UNWIND_PREFILTER` (what is asked of the element
  after the unwind is asked of the collection before it; `params` `path`, `alias`, `paths`) — the
  three say where the engine places a stage of a contract 2 request (see
  [`oxql-semantics.md`](oxql-semantics.md)); the rows are those of the request as written,
  `MISSING_POLICY` (the effective `onMissing`, `strict`, which outcomes lose data, and with
  `outcomeAs` the member that carries the outcome, `params.outcomeAs`; on a join that refuses its
  missing references the message says that no answered row shows that outcome),
  `REPORT_PAGE`, `INDEX_ADVICE` (only with `include: ["indexes"]`; an index list that cannot be read
  is an `INDEX_ADVICE` note saying so), `REMOTE_LOOKUP`, `EXPLAIN_LIMIT` and `EXPLAIN_TRIMMED`. Codes
  are stable; messages may change. Without `include: "notes"` only the notes about the answer itself
  are answered (`REMOTE_UNCHECKED`, `EXPLAIN_LIMIT`, `EXPLAIN_TRIMMED`). A remote union target's
  `SELECT_PATH_NOT_ON_TARGET` comes from its owner's internal explain (the remote check), which then
  checks that target again without the paths it lacks, as a run asks again, so the stages continued
  at it are checked as the run binds them; a run reports the paths an owner dropped as diagnostics,
  from the cache as well. Under a union join's alias the same note says what one branch does not
  reach: at the union join's stage, with `params { alias, branch, parent: false }` in place of
  `target`. `MISSING_POLICY` names only the outcomes the stage can have: an inline
  resolve has no `owner_unanswered` or `invalid_key`, and `ambiguous` only onto a target field that
  is not the key.
- **Errors** carry `params` where a caller acts on them. A path that does not resolve says why in
  `params.reason`: `notAMember` (`entity`), `projected`, `unwound` (`collection`, `alias`),
  `afterPage` (`alias`), `noTarget` (`alias`, `targets`), `noMembers` (`alias`), `notStored`. An
  owner's error keeps its own `params` and gains `params.owner`. A contract 1 answer carries none.
- **`remote`.** With `check` (the default) the owner queries of every keyed stage are bound by their
  owners' internal explain: an owner error becomes this request's error at the caller's stage
  (`params.owner` as in a refusal) and the stage's status `error`. All owner calls of one explain
  share `Explain:RemoteTimeoutMs` (1 500 ms); a part no owner answered (a client that cannot explain,
  unreachable, timed out, not configured) leaves a `REMOTE_UNCHECKED` note with `params.reason`,
  never an error.
  - *Conditions at owners.* A condition on a member of another service's rows (a semi-join) is bound
    by that owner when the query runs, before the page. Explain has the owner bind the query the run
    sends it first for the condition (the condition relative to the target, the target field
    projected, the first page with its count, at the run's page size), and never run it. What the
    owner refuses is this request's error: the owner's code, message and `params`, with
    `params.owner { service, entity, target, stage, path }`, under the path the caller wrote
    (`recipient.id`), at the `match` stage that carries the condition, whose status is `error`; for
    a condition of the request's `filter` at its entry instead (`filter`: the entry's id, or its
    index; no `stage`; the entry's `status` in `filter[]` is `error`). The basis is the code's, as
    for every owner error. What an owner's binder refuses of such a condition: `INVALID_OPERAND` (the
    operator does not apply to the member's kind, or the operand is not of it), `UNKNOWN_PATH` (no
    such member of the target), `NOT_FILTERABLE` (an object, a collection of objects, a member of
    unknown kind), `UNKNOWN_ENUM_MEMBER`, `OPTION_NOT_APPLICABLE`, `NOT_STORED`,
    `DECIMAL_TEXT_NOT_ORDERABLE`, `UNKNOWN_VARIANT` (all `model`), `INVALID_REGEX`, `OPERAND_NOT_ARRAY`,
    `INVALID_PATH` (`request`) and `REGEX_TOO_LONG` (`limits`); one under the owner's addon bag is `organisation`
    and keeps the owner's `params.addon`. An error at a
    stage this host added to that query (the projection of the target field, the page) is said at
    the condition too, since the run is refused for it all the same. A condition no owner was asked
    A run's refusal carries the same error under its `RESOLVE_REFUSED` head (contract 2), so the
    two answers differ in `basis` alone. The `filter` a remote `resolve` or `lookup` stage carries
    itself is checked the same way with the stage's own owner query: what the owner refuses of it is
    an error at that stage, under the path as the caller wrote it in the filter, with `params.owner`.
    Over several targets explain asks every owner and says an error once per code, stage and path;
    a run stops at the first owner that refuses. A condition no owner was asked
    for is never valid by silence: it leaves a `REMOTE_UNCHECKED` note at the same place, with
    `path` and `params { service, target, reason, condition }` (`reason: "cached"` under the cached
    tier without a kept answer, else `unsupported`, `unreachable`, `timeout`), or the
    `EXPLAIN_LIMIT` note of the limit that left it out (once per limit and service, and once more
    with `filter` for each entry of the request's `filter` a left-out condition belongs to);
    `cache.complete` is then false and
    `executable` is `unknown`, never `yes`. What only a run can tell stays a run's: more matching
    rows than `MaxSemiJoinIds` (`SEMI_JOIN_TOO_LARGE`), an owner that does not answer then, a
    matching owner row without the member the reference names.
  - *Rounds.* The checks go out in rounds. The first carries every owner query of every keyed stage
    and the query of every condition asked of an owner, a query several conditions share once;
    each owner service gets one call with all its checks (`POST internal/oxql/explain`, `checks`), and
    the services are asked at once. A further round asks again what a run asks again: a target whose
    owner refused only paths it lacks, without them (at most two more). What an earlier request (an
    explain or a run) learned a target lacks is dropped before the first round, so a warm explain needs
    one. An owner explains the checks of one call side by side and asks each of its own owners once per
    round for all of them, so a transitive owner costs one call per round as well. A condition adds
    a check to the first round's call of its owner and no call: it costs a call of its own only when
    everything else that owner is asked is kept and the condition's answer is not (another operand
    is another query), and never a further round.
  - *Kept answers.* An owner's answer is kept 30 s by organisation, user, owner service, the revision
    of that owner's schema document and the forwarded body. An answer that names another revision of a
    service retires every answer kept for the old one, also those of other owners that reached it. An
    answer that was not complete is not kept (an owner short of time or calls answers less than it
    would with more), so the next `check` asks that owner again. What is kept is bounded by number
    (1 000 answers) and by size (16 MB of answers; one is charged at least 16 KB). Nothing an explain
    answers depends on what is kept: with nothing kept the owners are asked.
  - *`cached`.* The tier for an edit in progress: the answer is built from the owner answers already
    kept and no owner is asked, not for a check, not for a catalog entry and not for its health. What
    is not kept is left out and said: a `REMOTE_UNCHECKED` note with `params.reason: "cached"`, the
    owner listed with `answered: null`, `reason: "cached"` and no call, the alias `complete: false`
    with `type: null`, `cache.complete: false`. A condition asked of an owner whose answer is not
    kept is unchecked in the same way (the note carries its `path`), and `executable` is `unknown`:
    the kept answers are per forwarded body, so a condition with another operand than the last
    `check` saw is not known. What this host binds itself is all there. Once every
    owner answer is kept, the cached answer is the answer a `check` gives, with the same `etag`.
- **The validator.** `etag` is a hash of the request (but not of `remote`), of who asks (the
  organisation and the user) and of the answer as it is written, less what differs between two
  tellings of one answer: the `etag` itself and what each owner cost this time (`owners[].ms`,
  `calls`, `cached`). So it covers `valid`, every error, note, stage, alias, type, column and
  catalog entry, what each owner answered (`owners[].answered`, `reason`, `engine`), the revisions and
  `cache`: two answers that differ in anything a caller reads never share it, also when an owner
  answers differently under an unchanged revision (an addon defined at the owner, an owner that
  publishes no revision and was redeployed, an index created for `include: "indexes"`). One identity's
  validator never matches another identity's answer. A `cached` answer that is complete is the answer
  its `check` gave and has that `etag`; an incomplete one has another. The body member `etag` and the
  `ETag` header are one value. The route answers it as the `ETag` header (with
  `Cache-Control: private, no-cache`), and a request with `If-None-Match` naming it (weak comparison;
  `*` matches) is 304 without a body. The answer is computed before it is compared, so a 304 saves the
  transfer and the parse, not the bind: 2 to 4 ms warm for the reference queries.
- **Content coding.** The answer is written in the coding the caller accepts (`Accept-Encoding`):
  Brotli, else gzip, at the fastest level, with `Vary: Accept-Encoding`; a body under 1 KB, a refusal
  and a 304 are not coded. The reference queries are 22 to 43 KB as they are, 4.8 to 7.6 KB gzipped
  and 3.9 to 5.8 KB in Brotli.

**Limits of one explain.** Explain is rare, so its limits are tight; none is an error.

- *Before binding*, on the parsed body alone: a body over `Explain:MaxRequestBytes` is 413; more
  than `Explain:MaxStages` stages, more than `MaxCatalogEntries` catalog entries, a `shape.depth`
  above `MaxShapeDepth`, or an unknown `include` or `remote` value is 400 `EXPLAIN_LIMIT`.
- *Rate and concurrency*, on the public route, per organisation and user, before the body is read:
  a token bucket of `Explain:RateBurst` (30) refilled at `RatePerMinute` (120), at most
  `MaxConcurrentPerUser` (2) in flight, and at most `MaxConcurrentPerHost` (8) in flight on the host;
  more is 429 `rate_limited` with `Retry-After` and the code `EXPLAIN_LIMIT` (`params.limit`: `rate`,
  `concurrentPerUser`, `concurrentPerHost`). It is separate from run traffic and nothing waits. The
  internal route admits at most `MaxConcurrentPerCaller` (4) in flight per calling service.
- *What costs is charged when it costs.* The admission bucket is generous because a bind at this
  host is cheap (an editor explains on every settled edit). The calls to owners are what an explain
  costs, and they have a bucket of their own, per organisation and user: `Explain:OwnerRateBurst`
  (5) refilled at `OwnerRatePerMinute` (20), charged once per explain at the moment it is about to
  send its first call to an owner (an answer kept from an earlier check is no call and no charge;
  the owners' health facts a client reads once and keeps are not charged either). An explain that
  finds this bucket empty is **never a 429**: it asks no owner and is answered as `remote:
  "cached"` answers, 200 with what is kept, `cache.complete: false`, `executable: "unknown"` unless
  an error of its own says `never`, and the note `EXPLAIN_LIMIT` (`basis: "owner"`, `params {
  limit: "ownerRate", max, service, target, retryAfter }`, `retryAfter` in seconds). The next
  settled edit after `retryAfter` checks again. An internal explain is its origin's to pay.
- *Cost*, mid-way: at most `Explain:MaxOwnerServices` (4) distinct owner services and
  `MaxOwnerCalls` (8) owner calls in all, transitive ones included, where a call is one request to
  one service in one round (the first ask, and each ask-again of a union target), whatever number of
  checks it carries (at most `MaxBatchChecks`, 64); and `Explain:TimeoutMs` (2 000 ms) of wall time.
  An internal explain carries what is left of the time and the calls (`budget`, internal route only)
  for all its checks together, so an owner asks its own owners only within its origin's budget. The
  calls left are shared out among the owners asked at once that carry continued stages (an owner
  asked only for paths of its own entities asks no owner and gets none), so owners asked in parallel
  cannot together cause more than is left. A part a limit left out is an `EXPLAIN_LIMIT` note
  (`params.limit`: `ownerServices`, `ownerCalls`, `checks`, `time`; `service`, `target`), the answer
  says what is known with `cache.complete: false`, and nothing is retried.
- *Size*: the answer names its types and writes no member, so it is small: the reference queries
  of the report scenarios are 22 to 44 KB (3.4 to 5.5 KB gzipped), of which the types are under
  2 KB. An answer over `Explain:MaxAnswerBytes` (256 KB) keeps the first level of the member rows
  only where it was asked to write them out (`include: ["types"]`), then loses the plan, and says so
  with an `EXPLAIN_TRIMMED` note (`params.dropped`, `bytes`, `max`) and `cache.complete: false`.

**It never executes the query.** Explain binds and compiles; it sends the database no `aggregate`
and no `explain` command. Without `include: ["indexes"]` it reads nothing from the database but, on
a cache miss, the organisation's addon definitions. With it, it reads the
index lists of the collections involved (`listIndexes`, cached 60 s per collection) and matches
them statically against the leading `$match` (scope, cursor predicate and first caller condition,
which the server coalesces), the sort and each `$lookup`'s join field; each advisory line is also an
`INDEX_ADVICE` note. The advisory is absent when the host provides no index source. The page
aggregate fetches `limit + 1` rows; the extra one decides `hasNextPage`. A join that runs after the
page appears after the `$limit`.

**What it reveals.** To any authenticated caller of the organisation: member names and kinds of the
entities the query reaches, and with the plan storage paths, collection names, the emitted Mongo
stages, the collation and the forwarded owner queries, and with the index opt-in the index names
and keys. None of it is more sensitive than what `GET /schema` serves anonymously (storage names,
keys, references, routes), and it concerns the caller's own organisation only: the scope stage
shows the caller's organisation id. No row data, no other organisation's data, no configuration
secret appears.

A host that serves continued stages for other services also answers the internal twin, `POST
internal/oxql/explain` in the Simplic base package, admitted by the internal key under the forwarded
identity. Its body is what one round of an origin's explain asks the owner:
`{ "checks": [ explain envelope, … ], "budget": { "ms", "calls" } }`, answered
`{ "answers": [ … ] }` with one entry per check, in order; an entry is null where a check was
refused before binding or the budget ran out before it. An origin sends it through
`IRemoteQueryClient.ExplainBatchAsync` (`IOxQLQueryService.ExplainBatchAsync` on the owner's side),
for the remote check (the owner queries of the keyed stages and of the conditions asked of the
owner before the page) and for catalog entries of the owner's entities. An owner answers each check
*slim*: what an origin reads and nothing it would compute itself and drop, namely `valid`,
`contract`, `engine`, `revision`, `cache`, `errors`, the notes about the answer itself
(`REMOTE_UNCHECKED`, `EXPLAIN_LIMIT`, `EXPLAIN_TRIMMED`), `stages` with `index`, `kind`, `status`,
`reads` and `creates`, `aliases`, `types`, `owners` and `catalog`; no shapes, placements, rules, flag
sets, result columns, diagnostics, plan or `etag`. That is 32 to 52 % of the whole answer for the
reference queries. The stages continued under a keyed stage of the host itself are checked the same
way in process, as its own owner binds them when the query runs; what continues from there to
another service (every target of a union without `forTarget`) is that owner's remote check, so
explain refuses what the run would refuse.

**A catalog entry of an entity only another owner reaches.** A catalog entry names an entity of
another service; this host asks that service when it knows it (`InternalHosts`). When it does not,
it asks the owner that reached the service, in this explain or an earlier one (the entity of a third
service a continued stage reaches), and that owner asks on: the answer is the entity's owner's, marked
`forwarded`. Without such a route the entry answers `UNKNOWN_ENTITY`. The entries of one owner travel
in one call.

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
- **By condition**, before the page: the semi-join. A condition on a member of a plain remote alias
  asks the owner for the matching target keys page by page, refuses above `MaxSemiJoinIds`, and
  substitutes them as an `$in`. It exists for plain remote resolves only. The first of those
  queries is what explain has the owner bind (`KeyedFetch.ConditionChecks`, the builder and the
  page size a run uses), so explain refuses what the owner's binder refuses.

Both modes share the owner query builder, the batching, the time budgets, the error mapping and the
cache. The owner queries are ordinary OxQL queries, one per target and key chunk, in batches per
owning service, owners in parallel:

| target | owner query |
|---|---|
| an entity by its own key | `match <field> in [keys]`, the filter, the continued stages, the projection, a page of the chunk's size |
| an item, or a non-key field | the internal member `keyedBy: { path, keys, perKey: 2 }`: the owner matches the keys (for an item, unwinds only the elements holding a key as `oxEl`, keeping their position), ranks the records per key by record key (and position), keeps two, then the filter, the continued stages and the projection; a second record means `ambiguous`. String keys compare exactly even inside a collated aggregate. At most `MaxPageSize / 2` keys per query, and no more than the owner's own page holds. A plain resolve onto a non-key field keeps the plain key match while nothing reads its outcomes; in a request that reads them (`strict`, an `onMissing` other than `null`, `outcomeAs`) it is grouped |
| the existence probe | the same without the filter and projecting only the key, for keys the filtered answer lacked, when the stage has a `filter` and an `onMissing` other than `null` |

- `keyedBy` is accepted only on the internal route and in process (`IOxQLQueryService.BatchAsync(batch,
  internalCall: true, …)`, which marks the request context internal); on the public routes it is
  `UNKNOWN_REQUEST_MEMBER`. It is the only new wire vocabulary; no header is added. A plain remote
  resolve onto an entity's key sends the plain key match.
- **Owner facts.** The keyed fetch splits each owner's batch at the owner's `maxBatchQueries`, sizes
  its key chunks by the owner's `maxPageSize`, both read from the owner's
  shallow health by a remote client that implements `IRemoteOwnerInfo`. It refuses nothing by an
  owner's version. Before the first batch of a
  request the fetch asks the client for each owner's facts (`OwnerOfAsync`), which may read the
  owner's shallow health right then, within a tenth of the phase (at most 250 ms); explain reads them
  the same way, within its owner budget. A
  client that does not know them in time leaves the host's own caps (an owner configured with a
  smaller page then refuses `PAGE_SIZE_EXCEEDED` inside `RESOLVE_REFUSED`; the next request, with the
  facts read, succeeds). An owner
  behind a base package whose internal route does not use the internal-call overloads refuses
  `keyedBy` as `UNKNOWN_REQUEST_MEMBER`, which the caller sees as `RESOLVE_REFUSED`: an owner other
  services call needs the matching base package.
- **Owners.** Every owner an origin reaches runs this package: an owner needs the internal
  routes (`internal/oxql/batch`, `internal/oxql/explain`), which only this package has. A service
  still on the version 1 package has no internal route, is not listed in `InternalHosts` and cannot
  be an owner; its entities stay unknown or unavailable to origins (`UNKNOWN_ENTITY`,
  `RESOLVE_UNAVAILABLE`). Listed by mistake, it is not reached: a run reports
  `RESOLVE_UNREACHABLE` (and refuses under `strict`) and explain notes the part `REMOTE_UNCHECKED`. There is no probe and no
  gate.
- **Remote lookups** rank each key's children at the owner in the lookup's sort, then by record key;
  elements of one owning row that tie there come back in no guaranteed order (sort by an element
  member to fix it).
- **Owner faults.** An owner error coded `INTERNAL_ERROR` (and the title of an `internal_error`
  envelope) reaches the caller with a fixed text, whatever the owner's `IncludeErrorDetails`; the
  detail stays in the owner's log.
- **Union paths.** A path asked under an alias that an owner says one target of a union lacks is
  dropped for that target and the query asked again, for every chunk that carried it; what was
  learned is kept in the cache per organisation, service and target, since it is a fact about the
  target: a later request that asks the path does not send it and still reports it, whatever else
  it asks. A path is kept for `Cache:ResolveTtlSeconds` from the moment an owner said it; a request
  (a run or an explain alike) that only reads it does not renew it, so within one TTL the owner is
  asked for the path again and a member it has gained is found. Where the owner's schema revision is
  known (an explain answer named it), what was learned of another revision is not read at all. What a
  branch of a union join does not reach is kept the same way, per target, branch and the stages
  continued before it.
- **An owner answer with `hasNextPage`** means the owner cut rows: the chunk's open keys are
  `owner_unanswered` (`RESOLVE_PARTIAL`), never `not_found`, and are not cached.
- **Time.** See `Execution:ResolveTimeoutMs` and `ChainTimeoutMs` under *Limits*. Split batches of
  one owner share the request's deadline, each under what is left. The owner runs the queries of a
  batch side by side (`Execution:BatchConcurrency` at once), so a batch waits for about
  `ceil(queries / concurrency)` database round trips, not one per query; two queries of one batch
  that would send a further owner the very same query send it once. The batch's `maxTimeMs` is the
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
(the paths asked, filter, continued stages, case), so requests that differ in any of these, or
in their variables, never share an entry. The query hashed is the one sent, so without the paths
dropped for the target: once a drop is forgotten the query asks the path again and is another entry,
and no row fetched without a path answers a request that no longer knows the path was dropped. The paths of the projection are hashed as a set, in ordinal
order: two requests that name the same paths under an alias in another order share their entries. A key the owner answered as not found is kept for
`Cache:NegativeResolveTtlSeconds` (10 s); a strict request reads past such entries. What an owner
reported about the rows it answered for the stages it continued (a tour whose vehicle no record
holds, say) is kept with those rows, cut to them, when the report lists its rows in full and says
what the data says of them (`not_found`, `excluded`, `ambiguous`, `invalid_key`,
`reference_null`): the same page again is answered from the cache with the same diagnostic. Such
an answer is kept for the negative lifetime and is not read by a strict request either. A report
of anything else (an owner that did not answer, a list the owner cut, a truncation that only
counts rows) leaves the chunk unkept, and so do the keys of a cut answer. The semi-join's ids are
keyed by organisation and the first-page owner query.

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
   second call ten seconds later, `reachable: true`; `explain` in `capabilities` unless the host
   switched it off.
2. `GET {api}/schema` → 200; its `limits` equal the corresponding values on `/oxql/health`; the
   entity ids you expect are there.
3. `POST {api}/oxql/query` with `{ "entityType": "{entity}", "pipeline": [ { "page": { "limit": 1 } } ] }`
   and no token → 401 when the host requires authorization; with the token → 200.
4. A filtered, sorted, counted page:
   `[ { "match": { "{member}": { "startsWith": "a" } } }, { "sort": [ { "{member}": "asc" } ] }, { "page": { "limit": 20, "count": true } } ]`
   → 200, rows starting with `a` or `A` (accents folded), `pageInfo.count` with `state` `exact` or `atLeast`.
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
