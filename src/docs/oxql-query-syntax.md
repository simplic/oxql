# OxQL query syntax (contract 2)

The request shape accepted by `POST /oxql/query` and, per entry, by `POST /oxql/batch`.
Clients send `X-OxQL-Contract: 2`; a request without the header is served as contract 1 while
the host's `Compat:Enabled` is true (see the last section).

## Top-level structure

```jsonc
{
  "entityType": "logistics.shipment",         // the entity id, exactly as the schema publishes it
  "variables": { "from": "2026-01-01T00:00:00Z" },   // optional, bound by { "$var": "from" }
  "pipeline": [                                // stages, executed as written
    { "match": { … } },
    { "page": { "limit": 50 } }
  ]
}
```

- `entityType` is matched exactly and case-sensitively. An unknown id is `UNKNOWN_ENTITY`; a
  retired id the host declared is answered as the current entity with an `ENTITY_ID_RETIRED`
  diagnostic whose `params.currentId` names it.
- Each pipeline element is an object with exactly one stage key: `match`, `lookup`, `resolve`,
  `unwind`, `group`, `project`, `sort`, `page`. Two keys in one element is `UNKNOWN_STAGE`.
- The organisation scope (`organizationId eq <caller's organisation>`) is applied by the engine
  at every entry into an entity. It is not a stage a caller can write, place or project away,
  and it does not count toward `MaxPipelineStages`. A request whose context has no
  organisation is refused with 403 `ACCESS_DENIED` before binding.

## Paths

Dot-separated wire names, camelCase, `id` at every depth: `number`, `department.name`,
`items.quantity`. No `$`, no empty segment, no `..` (`INVALID_PATH`). A path that is not in
the entity's shape at the stage where it appears is `UNKNOWN_PATH`; a member the driver does
not store is `NOT_STORED`; an `unknown` member is projectable but `NOT_FILTERABLE` /
`NOT_SORTABLE`.

- A path through a collection in `match` means "some element" (`items.quantity eq 5` matches a
  document with any such item); it is refused in `sort` (`NOT_SORTABLE`). After an `unwind`
  the path means the element. An inner collection needs its outer one unwound first
  (`UNWIND_ORDER`).
- A dictionary member takes any key verbatim: `pricing.EUR.singlePriceNet`.
- The `addon` bag of an extendable entity: `addon.<definition path>`, the definition path
  verbatim (segments may contain spaces). A key the organisation has defined is typed and
  filterable; an undefined, retired or `object` key is `unknown`.
- Aliases from `unwind … as`, `lookup … as` and `resolve … as` are new roots, plain
  identifiers, and must not collide with a member of the current shape (`ALIAS_COLLISION`,
  `INVALID_ALIAS`). `group` aliases replace the shape.

Storage spellings (`_id`, `MatchCode`) are never accepted under contract 2.

## Stage: `match`

```jsonc
{ "match": {
    "number": { "startsWith": "S-", "options": { "ignoreCase": true } },
    "createdAt": { "gte": "2026-01-01T00:00:00Z", "lt": { "$var": "until" } },
    "or": [
      { "status": { "in": ["Open", 2] } },
      { "not": { "isDeleted": { "eq": true } } }
    ],
    "items": { "any": { "quantity": { "gt": 0 }, "article.number": { "eq": "A-1" } } }
} }
```

- Every property that is not `and`, `or`, `not` is a condition on that path; every operator key
  inside the operand object is a condition; several paths and several operators are `and`.
- `and` / `or` take arrays of condition objects, `not` one condition object; they nest freely.
  An empty group is `EMPTY_LOGICAL_GROUP`.
- `any` evaluates the nested condition against one element of a collection of objects, inner
  paths relative to the element (compiled to `$elemMatch`). It is refused on a collection already
  unwound, on a collection reached through another collection, and on collections of scalars,
  where the default "some element" form applies (`ANY_NOT_APPLICABLE`).
- `options.ignoreCase` applies to `eq neq in nin contains startsWith endsWith` on `string`
  members only (`OPTION_NOT_APPLICABLE` elsewhere). It compiles to an anchored case-insensitive
  regex per field, which walks the field's whole index. On a member holding a single character,
  published as `string` and stored as its code point, it folds the operand's case by code point
  instead, for `eq`, `neq`, `in` and `nin`; the three text operators have no text to match
  there and are refused with `INVALID_OPERAND`.

### Operators

Case-sensitive: `eq neq gt gte lt lte in nin contains startsWith endsWith exists regex`.
Anything else is `UNKNOWN_OPERATOR`.

| operator | applies to | operand |
|---|---|---|
| `eq`, `neq` | every kind | one value, or `null` (`eq null` = absent or null; `neq null` = present and non-null) |
| `gt`, `gte`, `lt`, `lte` | `int long double decimal date dateTime timeSpan string enum` | one value |
| `in`, `nin` | every scalar kind | an array of values (`OPERAND_NOT_ARRAY` otherwise); `in []` matches nothing, `nin []` everything |
| `contains`, `startsWith`, `endsWith` | `string` | a string; escaped and anchored by the engine |
| `regex` | `string` | a pattern up to `RegexMaxLength` (`REGEX_TOO_LONG`); nested quantifiers and backreferences are `INVALID_REGEX`; an unanchored pattern is diagnosed `REGEX_UNANCHORED` |
| `exists` | every kind | a boolean |

`neq` and `nin` match documents where the member is absent, as Mongo does, except for `null`.

### Operands per kind

An operand is written exactly as the service returns the member; the engine encodes it for
storage. A wrong JSON kind is `INVALID_OPERAND` naming what was expected.

| kind | operand |
|---|---|
| `string` | JSON string |
| `int`, `long` | JSON number (integral) or a string of digits |
| `double` | JSON number or numeric string |
| `decimal` | JSON number or string; matched as Decimal128 and as its canonical string while `DecimalMode` is `tolerant` |
| `bool` | `true`/`false` or the strings `"true"`/`"false"` |
| `guid` | a GUID string |
| `date` | `YYYY-MM-DD` |
| `dateTime` | ISO-8601 with `Z` or an offset; a bare local time is refused |
| `timeSpan` | ISO-8601 duration (`PT1H30M`) |
| `enum` | the member name or its number; an unknown name or number is `UNKNOWN_ENUM_MEMBER` |
| `binary` | base64 |

`{ "$var": "name" }` is the only wrapper: the value comes from `variables` in the same
encoding and is checked at the place of use (`UNBOUND_VARIABLE` when missing;
`INVALID_VARIABLE` when it holds an object). The v1 type hints (`$date`, `$uuid`, `$decimal`,
…) are contract 1 only.

A defined addon key coerces the same way and always matches tolerantly across the
representations the bag may hold; a definition with a closed `values` list admits only its
values on `eq neq in nin` (`UNKNOWN_ENUM_MEMBER` otherwise).

## Stage: `lookup`

A backward join along a declared reference: the child entity's member that references the
current entity.

```jsonc
{ "lookup": {
    "from": "logistics.shipment_document",   // the child entity id (local to this host)
    "path": "shipmentId",                     // the child's member declared to reference this entity
    "as": "documents",                        // alias: an array of the child entity
    "select": ["id", "name", "createdAt"],   // optional wire paths on the child; id is always kept
    "filter": { "isDeleted": { "eq": false } },  // optional condition on the child
    "limit": 20                               // optional, at most Limits:MaxLookupLimit
} }
```

The child must declare the reference (`LOOKUP_NOT_DECLARED`), the result is ordered by the
child's key and scoped to the caller's organisation inside the sub-pipeline. Any other member
(`localPath`, `foreignPath`, `convert`) is `UNKNOWN_STAGE_MEMBER`.

## Stage: `resolve`

A forward join along a declared reference: the id member on the current shape to the entity
it names, one object under the alias, `null` when the target does not exist or fails `filter`.

```jsonc
{ "resolve": {
    "path": "vehicleId",
    "as": "vehicle",
    "select": ["id", "matchCode", "name"],    // optional; default: the target's key and display members
    "filter": { "isDeleted": { "eq": false } }  // optional condition on the target, executed by its owner
} }
```

`path` must carry a declared reference (`RESOLVE_NOT_DECLARED`). A local target compiles to an
indexed `$lookup`; a remote target (an entity of another service) is fetched from its owner
after the page is fixed. Under a remote alias, a `match` is a semi-join (the owner supplies the
matching ids; more than `MaxSemiJoinIds` is 422 `SEMI_JOIN_TOO_LARGE`) and a `sort` is refused
(`RESOLVE_NOT_SORTABLE`). At most `MaxResolveStages` per request.

## Stage: `unwind`

```jsonc
{ "unwind": { "path": "items", "as": "item", "includeIndex": "itemIndex", "preserveNull": true } }
```

`path` must be a collection at the current shape (`NOT_A_COLLECTION`). Afterwards `path`
means the element, `as` is a copy of it and `includeIndex` an `int`. `preserveNull` keeps
documents whose collection is empty or absent.

## Stage: `group`

```jsonc
{ "group": {
    "by": [
      { "path": "status", "as": "status" },
      { "dateTrunc": { "path": "createdAt", "unit": "week", "timezone": "Europe/Berlin", "weekStart": "monday" }, "as": "week" }
    ],
    "fields": {
      "count": { "count": true },
      "total": { "sum": "amount" },
      "avgNet": { "avg": { "multiply": [ { "path": "quantity" }, { "path": "price" } ] } },
      "articles": { "countDistinct": "articleId" }
    }
} }
```

- Keys are scalar paths (not under a collection that is not unwound: `GROUP_ON_COLLECTION`) or
  a `dateTrunc` with `unit` in `year quarter month week day hour minute second`
  (`INVALID_DATE_TRUNC_UNIT`), an optional IANA `timezone` (default UTC, `INVALID_TIMEZONE`)
  and, for `week`, `weekStart` (default `monday`). A truncated key is emitted as the UTC
  instant of the local boundary. A `date` has no time of day and truncates on its calendar day
  in that zone, so the bucket is local midnight of the row's own day; a `dateTime` truncates as
  the instant it holds.
- Aggregates: `count` (`{ "count": true }`), `countDistinct`, `sum`, `avg`, `min`, `max`,
  `first`, `last`, `push` (`UNKNOWN_AGG_FUNCTION`). `sum` and `avg` need a numeric argument
  (`INVALID_AGGREGATE_ARGUMENT`).
- `avg` over a `long` is taken in decimal and comes back as a decimal string, the way a `sum`
  over a long does; over an `int` or a `double` it is a JSON number. A `push` alias carries the
  argument's value per row, each element in the member's wire encoding: a `long` as a string, a
  `date` as `YYYY-MM-DD`, a single character as that character, an object by its members.
- An argument is a path (a string, or `{ "path": "…" }`), `{ "$var": "…" }`, `{ "literal": … }`,
  or an arithmetic expression `add subtract multiply divide coalesce` over an array of
  arguments.
- After `group` the shape is only the `by` and `fields` aliases (unique among each other, at
  most `MaxGroupFields`); rows carry those names only. Paging after `group` is by offset
  behind the cursor.

## Stage: `project`

```jsonc
{ "project": { "id": 1, "number": 1, "department.name": 1 } }   // inclusion: only these
{ "project": { "internalNotes": 0 } }                             // exclusion: everything else
```

All `1` or all `0` (`MIXED_PROJECTION`). `id` is kept in an inclusion unless excluded
explicitly; at most `MaxProjectionFields` paths.

## Stage: `sort`

```jsonc
{ "sort": [ { "createdAt": "desc" }, { "number": "asc" } ] }
```

Each entry is one object of one path and a direction `asc` | `desc` (`INVALID_SORT_DIRECTION`).
Paths must be scalar and sortable in the current shape (not under a collection); after
`group` only keys and aggregates. On a root shape the engine appends `id` as the tie-breaker.

## Stage: `page`

```jsonc
{ "page": { "limit": 50, "includeTotalCount": true } }        // first page
{ "page": { "limit": 50, "cursor": "<nextCursor>" } }           // next page
{ "page": { "limit": 50, "offset": 200 } }                      // a jump, up to Limits:MaxOffset
```

Once and last (`STAGE_AFTER_PAGE`, `MULTIPLE_PAGE_STAGES`). `limit` between 1 and
`MaxPageSize` (`INVALID_PAGE_LIMIT`, `PAGE_SIZE_EXCEEDED`); without a limit, or without a page
stage at all, `DefaultPageSize`. `offset` above `MaxOffset` is `MAX_OFFSET_EXCEEDED`.

Cursors are opaque, signed, and bound to the query: a cursor from another pipeline, another
sort or a tampered one is `CURSOR_INVALID`. Keyset paging is null-aware on the root shape;
after `group` the cursor carries an offset. `includeTotalCount` runs a count concurrently
with the page (lookups included only when a later stage reads them); above `CountCap` the
count is the cap and `totalCountCapped` is true with a `TOTAL_COUNT_CAPPED` diagnostic.

## Response

```jsonc
{
  "items": [ … ],
  "pageInfo": { "hasNextPage": true, "nextCursor": "…", "totalCount": 1234, "totalCountCapped": false },
  "diagnostics": [ { "code": "ENTITY_ID_RETIRED", "message": "…", "stage": null, "path": null, "params": { "currentId": "logistics.shipment" } } ]
}
```

Rows are in the wire encoding at every depth: `id`, camelCase, `long` and `decimal` as
strings, `dateTime` ISO UTC, `guid` string, enum as number, `date` as `YYYY-MM-DD`,
`timeSpan` as an ISO duration, binary as base64. Grouped rows carry the aliases only; unwound
rows the element under the path plus alias and index; resolved rows the object or `null`
under the alias; looked-up rows the array. `totalCount` and `totalCountCapped` appear only
when a count was requested; `diagnostics` only when there are any.

## Refusal

```jsonc
{ "type": "validation_error", "title": "The request could not be bound.",
  "errors": [ { "code": "UNKNOWN_PATH", "message": "…", "stage": 2, "path": "items.quantity" } ] }
```

| `type` | HTTP | when |
|---|---|---|
| `validation_error` | 400 (413 for `REQUEST_TOO_LARGE`) | a caller error; every binding error at once |
| `access_denied` | 403 | no organisation in the context |
| `not_executable` | 422 | well-formed but not executable on this host |
| `timeout` | 504 | the aggregate exceeded `Execution:MaxTimeMs` |
| `internal_error` | 500 | an engine fault; details only when the host allows them |

### Error codes

| area | codes |
|---|---|
| entity | `UNKNOWN_ENTITY` |
| path | `INVALID_PATH`, `UNKNOWN_PATH`, `NOT_STORED`, `NOT_FILTERABLE`, `NOT_SORTABLE`, `NOT_A_COLLECTION`, `UNWIND_ORDER`, `ALIAS_COLLISION`, `INVALID_ALIAS` |
| operand | `INVALID_OPERAND`, `UNKNOWN_ENUM_MEMBER`, `OPERAND_NOT_ARRAY`, `UNBOUND_VARIABLE`, `INVALID_VARIABLE` |
| condition | `UNKNOWN_OPERATOR`, `EMPTY_LOGICAL_GROUP`, `OPTION_NOT_APPLICABLE`, `INVALID_REGEX`, `REGEX_TOO_LONG`, `ANY_NOT_APPLICABLE` |
| stage | `UNKNOWN_STAGE`, `UNKNOWN_STAGE_MEMBER`, `STAGE_AFTER_PAGE`, `MULTIPLE_PAGE_STAGES`, `MIXED_PROJECTION`, `GROUP_ON_COLLECTION`, `UNKNOWN_AGG_FUNCTION`, `INVALID_AGGREGATE_ARGUMENT`, `INVALID_DATE_TRUNC_UNIT`, `INVALID_TIMEZONE`, `INVALID_SORT_DIRECTION`, `LOOKUP_NOT_DECLARED`, `RESOLVE_NOT_DECLARED`, `RESOLVE_NOT_FILTERABLE`, `RESOLVE_NOT_SORTABLE` |
| limits | `MAX_PIPELINE_STAGES_EXCEEDED`, `MAX_LOOKUP_STAGES_EXCEEDED`, `MAX_UNWIND_STAGES_EXCEEDED`, `MAX_RESOLVE_STAGES_EXCEEDED`, `MAX_GROUP_FIELDS_EXCEEDED`, `MAX_PROJECTION_FIELDS_EXCEEDED`, `MAX_CONDITIONS_EXCEEDED`, `MAX_VARIABLES_EXCEEDED`, `INVALID_PAGE_LIMIT`, `PAGE_SIZE_EXCEEDED`, `MAX_OFFSET_EXCEEDED`, `BATCH_TOO_LARGE`, `REQUEST_TOO_LARGE` (413) |
| cursor | `CURSOR_INVALID` |
| access | `ACCESS_DENIED` (403) |
| execution | `RESOLVE_UNAVAILABLE` (422), `RESOLVE_REFUSED` (422, wraps the owner's errors), `SEMI_JOIN_TOO_LARGE` (422), `QUERY_TOO_EXPENSIVE` (422), `QUERY_TIMEOUT` (504), `INTERNAL_ERROR` (500) |
| compat | `LEGACY_STAGE_UNSUPPORTED` (a v1 `lookup` or `resolve` under contract 1) |

### Diagnostic codes

Never a refusal; carried in `diagnostics` with machine-readable `params` where useful:
`ENTITY_ID_RETIRED` (`params.currentId`), `TOTAL_COUNT_CAPPED` (`params.cap`),
`RESOLVE_TIMEOUT`, `RESOLVE_UNREACHABLE`, `RESOLVE_PARTIAL` (a chunk beyond `MaxResolveKeys`
was not fetched), `SORT_ON_ADDON`, `REGEX_UNANCHORED`.

## Batch

```jsonc
POST /oxql/batch
{ "queries": [ <request>, <request> ], "maxTimeMs": 2000 }
```

Always HTTP 200 with `{ "results": [ … ] }` in order; each entry is a full success body or a
refusal envelope. `maxTimeMs` caps every query's aggregate under the host's ceiling. More than
`MaxBatchQueries` is a 400 `BATCH_TOO_LARGE` refusal of the whole batch.

## Contract 1 (compatibility mode)

While the host's `Compat:Enabled` is true, a request without `X-OxQL-Contract: 2` is bound
by the compatibility binder: the entity id is matched case-insensitively (retired ids too),
paths may be spelled as stored (`MatchCode`, `Department._id`) and are resolved against the
same folded shape, wire spellings work too, the v1 type-hint operands are accepted, operators
and sort directions are read case-insensitively, and rows come back in the v1 encoding. The v1 `lookup` (`localPath`/`foreignPath`) and `resolve`
stages are refused with `LEGACY_STAGE_UNSUPPORTED`. Every such request is logged under
`OxQL.Compat`. When compatibility is switched off, every request is contract 2.
