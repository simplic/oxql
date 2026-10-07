# OxQL query syntax (contract 2)

The request shape accepted by `POST /oxql/query` and, per entry, by `POST /oxql/batch`.
Clients send `X-OxQL-Contract: 2`; a request without the header is served as contract 1 while
the host's `Compat:Enabled` is true (see the last section). What a bound request means is in
[`oxql-semantics.md`](oxql-semantics.md); statuses, limits and the operational endpoints are in
[`oxql-operations.md`](oxql-operations.md).

## Top-level structure

```jsonc
{
  "entityType": "logistics.shipment",         // the entity id, exactly as the schema publishes it
  "variables": { "from": "2026-01-01T00:00:00Z" },   // optional, bound by { "$var": "from" }
  "filter": [ { "id": "open", "where": { … } } ],    // optional: conditions the engine places
  "strict": true,                              // optional: refuse instead of losing data
  "maxTimeMs": 3000,                           // optional: this request's own time ceiling
  "pipeline": [                                // stages, executed as written
    { "match": { … } },
    { "page": { "limit": 50 } }
  ]
}
```

- A request carries `entityType`, `variables`, `filter`, `pipeline`, `strict`, `timing` and `maxTimeMs`. Any other top-level member
  is `UNKNOWN_REQUEST_MEMBER` under contract 2. `maxTimeMs` is a ceiling on this request's time in
  milliseconds, under the host's (`Execution:MaxTimeMs`) and, in a batch, under what the batch
  leaves it: the page, the count and the owners asked end within it, and a request that does not is
  504 `QUERY_TIMEOUT`. A value above the host's ceiling is the host's ceiling, a value below 1 is 1;
  it changes no row and is no part of a cursor, and contract 1 ignores it. A grid sends it with the
  request for its totals, so a total that is slow ends early. `timing: true` asks for the `timing` member of the
  answer (see [Response](#response)); it changes no row and is no part of a cursor. `filter` is described under
  [The request's `filter`](#the-requests-filter). The member `keyedBy`
  exists only on the internal route an owner is called through (see
  [`oxql-operations.md`](oxql-operations.md#keyed-fetch-and-remote-continuation)); on the public
  routes it is `UNKNOWN_REQUEST_MEMBER` like any other.
- `strict: true` turns every loss of data into a 422 refusal and allows a larger single page;
  see [Strict requests](#strict-requests-and-the-report-page). A caller gates on contract 2
  (`engine.contract` on health) before sending it or any other contract 2 construct.
- `entityType` is matched exactly and case-sensitively. An unknown id is `UNKNOWN_ENTITY`; a
  retired id the host declared is answered as the current entity with an `ENTITY_ID_RETIRED`
  diagnostic whose `params.currentId` names it. A retired id in `lookup.from` joins the current
  entity and carries the same diagnostic, with the lookup's `stage`.
- Each pipeline element is an object with exactly one stage key: `match`, `lookup`, `resolve`,
  `unwind`, `group`, `project`, `sort`, `page`, or `values`, which ends a pipeline in place of
  `page` and answers the distinct values of one path instead of rows. Two keys in one element is
  `UNKNOWN_STAGE`.
- The organisation scope (`organizationId eq <caller's organisation>`) is applied by the engine
  at every entry into an entity. It is not a stage a caller can write, place or project away,
  and it does not count toward `MaxPipelineStages`. A request whose context has no
  organisation is refused with 403 `ACCESS_DENIED` before binding.

## The request's `filter`

```jsonc
{ "entityType": "logistics.shipment",
  "filter": [
    { "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } },
    { "id": "preset:heavy",  "where": { "items": { "any": { "quantity.value": { "gt": 5000 } } } } },
    { "id": "col:c9",        "where": { "item.article.name": { "contains": "palette" } } },
    { "id": "col:total",     "where": { "total": { "gt": 1000 } } } ],
  "pipeline": [ { "unwind": { "path": "items", "as": "item" } }, … ] }
```

`filter` is a list of entries, all of which a row must meet. `where` is a condition object as a
`match` stage takes it. `id` is optional: a string of 1 to 64 characters, unique in the list; it
decides no row, and errors, diagnostics, notes and explain name the entry by it.

The engine places the conditions. **A condition is about the thing its path names, and runs at
the first point of the pipeline where that thing is in the row:**

| path in a condition | is in the row | meaning |
|---|---|---|
| an entity member | from the entry | the row's own value |
| `items.quantity` (a collection) | until its unwind | the row has some element with … |
| `items: { any: { … } }` | until its unwind | one element meets all of … |
| `item.quantity` (an unwind's alias), its `includeIndex` | after the unwind | this row's element has … |
| a member of a join's alias | after the join | (under another service's alias: a semi-join, as in a `match`) |
| a group key or an aggregate | after the group | which groups (HAVING) |

- The top-level conjuncts of one `where` (its paths, its operators, and the members of an `and`)
  are placed one by one. An `or`, a `not` and an `any` are each placed as a whole, at the first
  point where every path they read is in the row.
- A group key over a path of the same name (`by: [{ path: "status", as: "status" }]`) is in the
  row from the entry, so a condition on it runs before the group: which rows feed the buckets is
  which buckets there are. A name a group gives to another thing (an aggregate, a key over
  another path) while a member of that name exists earlier binds at the earlier one; explain
  notes it (`FILTER_BINDS_EARLIER`). A `match` stage after the group filters the group's.
- A condition whose paths are never in the row together (a collection's path OR-ed with its
  unwind's alias; a member a group removed OR-ed with an aggregate) is `FILTER_NOT_PLACEABLE`
  (`params.paths`, and `params.births`: per path the point it is first in the row at,
  `{ "path", "before": 0 }` for the entry or `{ "path", "after": i }` for after stage `i`). A
  path that is never in the row is the error it would be in a `match` at the end of the pipeline.
- An error of an entry carries `filter` (its `id`, or its index in the list when it has none) and
  no `stage`; so does a diagnostic. A malformed `filter` is `INVALID_FILTER` with `params.reason`:
  `notAnArray`, `notAnObject`, `unknownMember`, `whereRequired`, `invalidId`, `duplicateId`,
  `malformedWhere`.
- The conditions count toward `MaxConditions`, and they are part of the cursor fingerprint: a
  cursor continues the same filter only. A filter written as `match` stages at the points the
  engine places it at is the same query and shares its cursors.
- The `match` stage stays for a condition that must run at a point of the caller's choosing.
- Contract 1 never had the member and ignores it.

## Paths

Dot-separated wire names, camelCase, `id` at every depth: `number`, `department.name`,
`items.quantity`. No `$`, no empty segment, no `..` (`INVALID_PATH`). A path that is not in
the entity's shape at the stage where it appears is `UNKNOWN_PATH`; a member the driver does
not store is `NOT_STORED`; an `unknown` member is projectable but `NOT_FILTERABLE` /
`NOT_SORTABLE`.

- A path through a collection in `match` means "some element" (`items.quantity eq 5` matches a
  document with any such item); it is refused in `sort` (`NOT_SORTABLE`). An `unwind` takes the
  collection out of the row and puts one element under its alias: after
  `unwind { path: "items", as: "item" }` the element is `item.quantity`, and `items.quantity` is
  `UNKNOWN_PATH`. So a path names one thing along the whole pipeline: the collection before the
  unwind, the alias after it. An inner collection needs its outer one unwound first
  (`UNWIND_ORDER`) and is then reached through the alias (`item.stops`).
- A member only some variants of a polymorphic value carry (`items.billingLineId` when only the
  billing-line item has it) is a path of the base type like any other; it is nullable and may be
  absent on rows of other variants. `is` tests the variant (see *Operators*).
- A dictionary member takes any key verbatim: `pricing.EUR.singlePriceNet`.
- The `addon` bag of an extendable entity: `addon.<definition path>`, the definition path
  verbatim (segments may contain spaces). A key the organisation has defined is typed and
  filterable; an undefined, retired or `object` key is `unknown`.
- Aliases from `unwind … as`, `lookup … as`, `resolve … as` and `resolve … parentAs` are new roots, plain
  identifiers, and must not collide with a member of the current shape (`ALIAS_COLLISION`,
  `INVALID_ALIAS`). `group` aliases replace the shape.

Storage spellings (`_id`, `MatchCode`) are never accepted under contract 2.

## Stage: `match`

```jsonc
{ "match": {
    "number": { "startsWith": "s-" },
    "reference": { "eq": "ABC-1", "options": { "caseSensitive": true } },
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
- `and` / `or` take arrays of condition objects, `not` one condition object; they nest up to 32
  levels deep, `any` included (`MAX_CONDITIONS_EXCEEDED` beyond).
  An empty group is `EMPTY_LOGICAL_GROUP`.
- `any` evaluates the nested condition against one element of a collection of objects, inner
  paths relative to the element (compiled to `$elemMatch`). It is refused on an unwind's alias (one
  element, not a collection), on a collection reached through another collection, and on
  collections of scalars, where the default "some element" form applies (`ANY_NOT_APPLICABLE`);
  on a collection an unwind took out of the row it is `UNKNOWN_PATH` like any path under it.
- A comparison on a `string` member folds case and accents: `muller` matches `Müller` and
  `MÜLLER`. The request then runs under the host's collation (German, primary strength, unless
  configured otherwise), `eq neq in nin gt gte lt lte` compare under it, `startsWith` is a
  range under it, and `contains` and `endsWith` are patterns that fold case and accents the same
  way: each letter of the operand stands for every letter of its primary weight (`u` for
  `u ù ú û ü …`), and a letter the collation weighs as two or three stands for them and they for
  it (`ß` and `ss`, `æ` and `ae`, `œ` and `oe`, `ĳ` and `ij`, the ligatures `ﬁ ﬀ ﬃ ﬆ ǆ ǉ ǌ`), so
  `ull` finds `MÜLLER`, `ÜLL` finds `Muller` and `strasse` finds `Straße`: a pattern finds a
  spelling exactly when `eq` calls it equal. The rule is read from the collation, not listed: what
  the collation tells apart stays apart (`þ` is not `th`, `ä` is not `ae`, `ð` is not `d`).
  Sequences pair from the left (`sss` is `ß` then `s`), and one letter alone does not match inside
  a character that stands for two (`s` does not find the `ß` of `Straße`; `ss` and `ß` do).
  A pattern is not served by an index: `contains` and `endsWith` read every candidate row, while
  `eq` and `startsWith` are bounded by an index of the same collation. An operand whose pattern
  outgrows the server's 32 KB limit is `INVALID_OPERAND` (some 500 letters of the widest class).
  A request that compares, sorts and groups no string runs without a collation.
- `options.caseSensitive: true` compares exactly. It applies to `eq neq in nin contains
  startsWith endsWith` on `string` members (`OPTION_NOT_APPLICABLE` elsewhere: an ordered
  comparison orders under the collation of the whole request and cannot leave it, a `regex` is
  the caller's own pattern, and a member without text has nothing to fold). `caseSensitive` is
  the one option: `options.ignoreCase` is contract 1's and is `OPTION_NOT_APPLICABLE` under
  contract 2, whatever it says (`params` `option`, `reason: "removed"`, `use: "caseSensitive"`).
  On a member holding a single character, published as `string` and stored as its
  code point, `eq neq in nin` fold the operand's case by code point instead; the three text
  operators have no text to match there and are refused with `INVALID_OPERAND`.

### Operators

Case-sensitive: `eq neq gt gte lt lte in nin contains startsWith endsWith exists regex is`.
Anything else is `UNKNOWN_OPERATOR`.

| operator | applies to | operand |
|---|---|---|
| `eq`, `neq` | every kind | one value, or `null` (`eq null` = absent or null; `neq null` = present and non-null) |
| `gt`, `gte`, `lt`, `lte` | `int long double decimal date dateTime timeSpan string enum` | one value |
| `in`, `nin` | every scalar kind | an array of values (`OPERAND_NOT_ARRAY` otherwise); `in []` matches nothing, `nin []` everything |
| `contains`, `startsWith`, `endsWith` | `string` | a string; `startsWith` is a range under the collation, the others a pattern escaped and anchored by the engine |
| `regex` | `string` | a pattern up to `RegexMaxLength` (`REGEX_TOO_LONG`); nested quantifiers and backreferences are `INVALID_REGEX`; an unanchored pattern is diagnosed `REGEX_UNANCHORED` |
| `exists` | every kind | a boolean |
| `is` | an object, or a collection of objects, whose type has variants | a variant name, or a non-empty array of names |

`neq` and `nin` match documents where the member is absent, as Mongo does, except for `null`.

**`is`** tests which variant a polymorphic value is stored as:

```jsonc
{ "match": { "resource": { "is": "DriverResource" } } }
{ "match": { "items": { "is": ["BillingLineTransactionItem", "GroupTransactionItem"] } } }   // some element
{ "match": { "item": { "is": "BillingLineTransactionItem" } } }         // after unwind items as item
```

- A name stands for that variant and every registered variant derived from it. A concrete base
  type may be named as well; it then stands for every variant and for values stored without a
  discriminator. A name that is neither is `UNKNOWN_VARIANT`, whose message lists the variants.
- It applies to an object member, to a collection of objects ("some element", like any condition
  through a collection), to an unwound element, to a join alias, to a member inside `any`, and to
  members under a remote alias, which the owner binds. On a member whose type has no variants it
  is `INVALID_OPERAND`, and so is a path that lies under a collection that is not unwound
  (`items.article` when `items` is a collection): unwind it, or test the element's member inside
  `any`. `options` on it are `OPTION_NOT_APPLICABLE`.
- It compiles to an `$in` over the stored discriminator values (`_t` by default). Contract 1
  refuses it with `UNKNOWN_OPERATOR`.

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
`INVALID_VARIABLE` when it holds an object). A variable name that starts with `$` is reserved
for the engine: a request that supplies one in `variables` is refused with `INVALID_VARIABLE`
(no `stage`; `params` `name`, `reason: "reserved"`), used or not, and a `$var` that names one is
`UNBOUND_VARIABLE`, since no request can bind it. The v1 type hints (`$date`, `$uuid`, `$decimal`,
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
    "filter": { "isDeleted": { "eq": false } },  // optional condition on the child
    "limit": 20                               // optional, at most Limits:MaxLookupLimit
} }
```

The child must declare the reference (`LOOKUP_NOT_DECLARED`) and is scoped to the caller's
organisation inside the sub-pipeline. Any other member (`localPath`, `foreignPath`, `convert`,
`parentSelect`) is `UNKNOWN_STAGE_MEMBER`.

**A join has no `select`.** A join loads what the query reads: its key, every path a later stage
reads under its alias, and what the row shows under it. Every member of the child can be read under
the alias; nobody lists in advance what later stages need. What the alias **shows** is the
projection's to say: a projection that names paths under the alias (`"documents.name": 1`) shows
exactly those paths (the key only when it names it), and an alias the row keeps **whole** (no
projection, or `"documents": 1`) shows the child's key and its display member, nothing else. A
`select` written on a contract 2 `lookup` or `resolve` (also on a union join, and on an internal
call) is `UNKNOWN_STAGE_MEMBER` with `params { stage, member: "select", reason: "removed", use:
"project" }`. Contract 1 keeps its `select`: there a join fetches the paths as written with the key,
and nothing is inferred. What joins load is in
[`oxql-semantics.md`](oxql-semantics.md#what-a-join-loads).

Contract 2 has four more members:

```jsonc
{ "lookup": {
    "from": "transport.delivery_attempt", "path": "shipmentId", "as": "lastAttempt",
    "on": "shipment",                              // the parent row: an alias instead of the entity itself
    "sort": [ { "dateTime": "desc" } ],            // order of the children
    "first": true                                  // one child or null instead of an array
} }
```

| member | default | meaning |
|---|---|---|
| `sort` | the child's key ascending | sort entries as in the `sort` stage, bound against the child; the child's key is appended as the tie-breaker |
| `first` | `false` | the alias holds the first child by `sort`, or `null`; a filterable, sortable entity row, not an array. Together with `limit`: `OPTION_NOT_APPLICABLE` |
| `on` | the entity itself | the alias of the parent row, an entity row of this host: an inline `resolve` alias, a `first` lookup alias, or an unwound lookup alias; the child's reference must target its entity. An array, an unwound element, a scalar or a group output is `LOOKUP_ON_NOT_ENTITY`. A keyed or remote `resolve` alias (or its `parentAs`) makes the lookup a continued stage that runs at that alias's owner (see *Continued stages*) |
| `forTarget` | none | only on a continued lookup (see *Continued stages*); elsewhere `OPTION_NOT_APPLICABLE` |

`limit` defaults to and is capped by `MaxLookupLimit` (`LOOKUP_LIMIT_EXCEEDED`). A parent with more
children keeps the first `limit` by `sort` and the page carries `LOOKUP_TRUNCATED` (`params`
`alias`, `limit`, `rows`: how many rows of the page were cut); under `strict` that refuses. Contract 1
refuses `sort`, `first`, `on` and `forTarget` with `LEGACY_STAGE_UNSUPPORTED`.

## Stage: `resolve`

A forward join along a declared reference: the id member on the current shape to the entity
it names, one object under the alias, `null` when the target does not exist or fails `filter`.

```jsonc
{ "resolve": {
    "path": "vehicleId",
    "as": "vehicle",
    "filter": { "isDeleted": { "eq": false } }  // optional condition on the target, executed by its owner
} }
```

`path` must carry a declared reference (`RESOLVE_NOT_DECLARED`, whose message names the path's
kind). Like a lookup it has no `select`: kept whole the alias shows the target's key and display
member, under a projection the paths it names. Under a join alias, a reference whose cases test a sibling (`type`) reads that
sibling; the join that holds the reference loads it with the reference, and explain lists the read
(`use: "caseCondition"`). A join compares its id exactly, whatever the id's kind: a string id never folds. At most
`MaxResolveStages` (8) resolve stages run on this host; stages continued at an owner count there.

Five more members serve references that are typed, point at items, convert their key or sit in a
collection (how such references are declared: [`oxql-semantics.md`](oxql-semantics.md#references)):

```jsonc
{ "resolve": {
    "path": "erpLine.sourceBillingLineReference.id",
    "as": "sourceLine",
    "target": "transport.shipment",                  // only this target of a typed reference
    "parentAs": "sourceParent",                      // the row that owns the item target
    "onMissing": "report"
} }
{ "resolve": { "path": "references.referenceId", "as": "shipment", "elements": "first" } }
```

| member | values | default | meaning |
|---|---|---|---|
| `elements` | `"first"`, `"all"` | none | required when `path` crosses one collection that is not unwound. `first`: the first element, in stored order, whose case is selected and whose key resolves. `all`: an array of every resolved target, at most `MaxLookupLimit` per row (then `RESOLVE_TRUNCATED`). On a path holding one value per row: `OPTION_NOT_APPLICABLE` |
| `target` | an entity id | none | narrows a typed reference to that target; not a target of any case: `RESOLVE_TARGET_NOT_DECLARED`, whose message lists the targets |
| `parentAs` | an alias | none | for item targets: the row owning the item, as `{ "entity": "<entity id>", <members> }`: kept whole, the owner's key and display members; under a projection that names paths below it (`"sourceParent.shipmentNumber": 1`), `entity` and those paths. On a reference with an entity target: `RESOLVE_PARENT_NOT_ITEM`; equal to `as`: `ALIAS_COLLISION` |
| `onMissing` | `"null"`, `"report"`, `"refuse"` | `"null"`, `"refuse"` under `strict` | what a reference that resolves to nothing does: stay `null` silently, stay `null` with a `RESOLVE_MISSING` diagnostic, or refuse (422) |
| `forTarget` | an entity id | none | only on a continued stage (see *Continued stages*); elsewhere `OPTION_NOT_APPLICABLE` |
| `byTarget` | `{ "<entity id>": "<path>" \| { "path": …, "elements": … }, … }` | none | in place of `path`, on a continued stage: one path per target of the alias it continues under, all filling the one `as` (see *The union join*) |
| `outcomeAs` | an alias | none | a row member that says on every row what became of the reference: `resolved`, `not_found`, … (see *The outcome under a name*) |

There is no `parentSelect`: the owning row shows what the projection names under `parentAs`
(`UNKNOWN_STAGE_MEMBER` when written). The paths under an alias are flat; on a reference with several
targets a path some target lacks (projected under the alias or its `parentAs`) is
dropped for that target (note `SELECT_PATH_NOT_ON_TARGET`), its member is absent on that target's
rows, and it is refused (`UNKNOWN_PATH`) only when every target lacks it. Contract 1 refuses the seven
members with `LEGACY_STAGE_UNSUPPORTED`.

**The collection guard.** A `path` under a collection that is not unwound names one key per element.
Without `elements` it is `RESOLVE_ON_COLLECTION` ("unwind it first, or set 'elements' to 'first' or
'all'"); a path crossing two such collections is `UNWIND_ORDER`.

**Where it runs.** A resolve with one unconditional case, one local entity target, no item, no key
conversion and no `elements` runs inline, as an indexed `$lookup` in the aggregate; its alias is an
entity row that later stages may filter and sort. (With a `filter` and outcomes that are read, an
`onMissing` other than `null` or an `outcomeAs`, it stays inline and joins its target a second
time without the filter, to tell an excluded record from a missing one.) Every other resolve runs as a keyed fetch after the page is fixed, at the target's
owner: another service, or this host in process for a local target. Its alias may be projected and
continued; a `match` on it is `RESOLVE_NOT_FILTERABLE` and a `sort` `RESOLVE_NOT_SORTABLE`, since it
is joined after the page is taken.

**Plain remote resolves** (a simple reference into another service) behave as follows: a `match`
on a member of the alias is a semi-join (the owner supplies the matching ids; more than
`MaxSemiJoinIds` is 422 `SEMI_JOIN_TOO_LARGE`), a `match` on the alias itself (`{ "veh": { "eq":
null } }`, `{ "veh": { "exists": true } }`) is refused before the owner is called
(`RESOLVE_NOT_FILTERABLE`), and a `sort` is refused (`RESOLVE_NOT_SORTABLE`). The owner binds a
semi-join condition under its own default; `caseSensitive` travels to it as written. What the
owner's binder refuses (an operator the member's kind does not take, an operand of another kind, a
member the target does not have, a name that is no member of the enum) is 422 `RESOLVE_REFUSED`
with the owner's error when run, and that error in explain, at the stage or the `filter` entry
that carries the condition; both carry the path as written and `params.owner`. The same holds for
the `filter` member of a `resolve` or `lookup` whose rows another service holds, at that stage.
A typed, item, converted, element-wise or continued remote alias takes no semi-join
(`RESOLVE_NOT_FILTERABLE`).

**The outcome under a name.** A `null` alias does not say why it is `null`. `outcomeAs` names a
member of the row that says it, on every row, for one join:

```jsonc
{ "resolve": { "path": "item.billingLineId", "as": "erpLine", "outcomeAs": "erpLineOutcome" } },
{ "match":   { "erpLineOutcome": { "in": ["not_found", "reference_null"] } } }    // the broken lines only
```

- The value is the join's outcome as the diagnostics spell it
  ([`oxql-semantics.md`](oxql-semantics.md#outcomes-and-data-loss)): `resolved`, `ambiguous`,
  `reference_null`, `excluded`, `not_applicable`, `not_found`, `invalid_key`, `owner_unanswered`.
  It is never `null` and `resolved` is said as well. Under `elements: "first"` it is the outcome of
  the element taken, else the row's fold; under `"all"` it is `resolved` (`ambiguous` when a taken
  element is) as soon as one element resolved, else the same fold.
- The name is an alias like `as` and `parentAs`: a root of the row, a plain identifier, free in the
  row and not one of the stage's own (`ALIAS_COLLISION`, `INVALID_ALIAS`). Nothing carries an
  outcome unless a stage names one; there is no request-wide switch and no reserved member. A
  `lookup` has none (`UNKNOWN_STAGE_MEMBER`): it has no missing reference.
- **It follows its join.** The outcome of an inline resolve is written by the aggregate: a `match`
  may compare it, a `group` may key on it and count it, and such a stage keeps the join before the
  page. The outcome of every other resolve (keyed, remote, continued, a union join) is written
  after the page with the join's rows and can only be projected: a `match` on it is
  `RESOLVE_NOT_FILTERABLE`, a `group` key `UNKNOWN_PATH`. No outcome orders a page
  (`RESOLVE_NOT_SORTABLE`), so none is ever in a cursor. A `group` ends the root like any alias.
- A condition takes `eq`, `neq`, `in`, `nin` (any other operator: `INVALID_OPERAND`) against the
  names above; a name that is no outcome is `UNKNOWN_ENUM_MEMBER` (`params.values` lists them). It
  compares exactly: nothing folds and no option applies (`OPTION_NOT_APPLICABLE`).
- **Projected, it keeps its join alive**: `{ "project": { "number": 1, "erpLineOutcome": 1 } }`
  answers whether the record exists without loading more of it than its key.
- **It reports and refuses nothing.** `onMissing` and `strict` keep their roles: under `onMissing:
  "null"` a missing reference is `not_found` in the member and no diagnostic. Under `strict` a
  row that loses data still refuses the request, so the member shows `not_found` only on a stage
  that says `onMissing: "report"`; that is the pair a report uses to print "missing" instead of
  failing. One thing a stage that names its outcome does differently: it *reads* its outcomes, as
  under an `onMissing` other than `null`. So a key two records hold is looked for and, once seen,
  reported (`RESOLVE_AMBIGUOUS`, as always), and a record its `filter` left out is told from a
  missing one (`excluded`), which costs a filtered keyed stage one more owner query.
- On a continued stage and a union join the owner of each target writes the member for the rows it
  answers and this host for the others: `not_applicable` where the stage does not apply to the
  row's target, `reference_null` where the alias it continues under is `null`.
- The member changes the row's shape, so it enters the cursor fingerprint.

### Continued stages

A `resolve` whose `path` starts at a keyed or remote alias R (or at R's `parentAs`, or at an alias
continued under R), and a `lookup` whose `on` names one of them, cannot run here: R's rows come from
R's owner after the page. Such a stage is **continued**: it rides in the query this host already
sends R's owner, and the owner binds and runs it with its own model. What that means for a caller is
in [`oxql-semantics.md`](oxql-semantics.md#chains-across-services).

```jsonc
{ "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
{ "lookup":  { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "lastAttempt",
               "on": "sourceParent", "forTarget": "transport.shipment",
               "sort": [ { "dateTime": "desc" } ], "first": true } }
```

- `forTarget` names one target of a union alias; the stage goes into that target's owner query only,
  and on rows resolved to another target its alias is `null` (outcome `not_applicable`). Without it
  the stage goes to every target and must bind on each; an owner's refusal comes back as this
  stage's error (`RESOLVE_REFUSED`, see *Refusal*). `forTarget` names a target of the alias the
  stage continues from: a `forTarget` that is not a target of R is `OPTION_NOT_APPLICABLE` here.
  Under an alias another continued stage added (a join the owner binds), it names a target of that
  alias; it travels to the owner, which applies it to its own join and refuses a target that alias
  lacks with `OPTION_NOT_APPLICABLE`, mapped back to this stage.
- Only `resolve` and `lookup` continue. An `unwind` of a path under R, a `group` key under R, and a
  continued stage under an `elements: "all"` alias are `NOT_CONTINUABLE`; a `sort` under R stays
  `RESOLVE_NOT_SORTABLE` and a `match` under R `RESOLVE_NOT_FILTERABLE` (except the semi-join of a
  plain remote resolve). Aggregation over chain data belongs in the report. The `project` paths under
  R are what R's owner is asked for, and those under a continued alias travel to the owner as paths;
  the owner binds the continued stage and infers what its join loads, the member that picks a
  reference's case included. A path under a continued alias that its target lacks is `UNKNOWN_PATH`
  at the projection. A projection need not name R to show an alias continued under it: a continued
  stage reads R, so R (and whatever R in turn reads its key from) is fetched for it and left out of
  the row. `{ "project": { "number": 1, "vehicle.matchCode": 1 } }` after a chain answers `number`
  and `vehicle.matchCode` and none of the aliases in between.
- At most `MaxContinuedStages` (8) stages continue under one alias
  (`MAX_CONTINUED_STAGES_EXCEEDED`). Continued stages do not count toward this host's
  `MaxResolveStages` or `MaxLookupStages`; the owner applies its own.
- Variables in a continued stage are bound here and sent substituted; an unbound one is
  `UNBOUND_VARIABLE` here. The same holds for the `filter` of every remote resolve.
- Contract 1 refuses continued stages with `NOT_CONTINUABLE`.

### The union join

When the alias a stage continues under has several targets (a source line that is a shipment's or a
tour's), the next record often lies at a different path per target. `forTarget` writes that as one
stage and one alias per target. `byTarget` writes it as **one stage and one alias**: a path per
target, each a branch.

```jsonc
{ "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
{ "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "transport.shipment", "elements": "first" } },
{ "resolve": { "as": "vehicle", "onMissing": "report",
               "byTarget": { "transport.shipment": "deliveringTour.resource.id",
                             "transport.tour":     "sourceParent.resource.id" } } },
{ "project": { "number": 1, "vehicle.matchCode": 1 } }
```

- A branch is a path, or `{ "path": …, "elements": "first" | "all" }` where the path crosses a
  collection that is not unwound. `as`, `filter`, `target` and `onMissing` are the
  stage's and apply to every branch. `path`, `elements`, `forTarget` and `parentAs` beside
  `byTarget` are `OPTION_NOT_APPLICABLE` (`params.reason` `withPath`, `withElements`,
  `withForTarget`, `withParentAs`); a `byTarget` that is not an object of such branches is
  `UNKNOWN_STAGE_MEMBER`.
- **At least two branches.** One branch is the plain form and is refused naming it:
  `OPTION_NOT_APPLICABLE`, `params { option: "byTarget", reason: "singleBranch", target, form:
  "forTarget" }`; write `path` with `forTarget`. Both forms stay: `forTarget` says "only for this
  target", `byTarget` "one alias across these targets".
- **The keys** are targets of the alias the branches continue under (R, the keyed or remote alias,
  or its `parentAs`). A key that is not one is `OPTION_NOT_APPLICABLE` (`reason: "notATarget"`,
  `params.targets` lists them); a key written twice `reason: "duplicateTarget"`.
- **Where a branch may start**: at R, at R's `parentAs`, or at an alias continued under them that
  exists on the branch's target (`deliveringTour` above, continued for shipments). A branch that
  starts at an alias of another target is refused (`reason: "otherBranch"`: "… belongs to the rows
  of 'transport.shipment'"); branches under two different keyed aliases `reason: "anchors"`; a
  branch on this host's own row `reason: "notContinued"`.
- **One shape.** Every branch yields one record per row, or every branch an array (`elements:
  "all"`). Mixing them is `UNION_CARDINALITY_MISMATCH`, whose message and `params.branch` name the
  branch to change (`params { alias, branch, elements, expected: "one" | "all" }`).
- **Rows.** The alias holds what the branch of the row's target resolved. A row resolved to a
  target without a branch has `null` there (outcome `not_applicable`, no data loss); a row whose R
  is `null` has `null` (`reference_null`). Which branch answered is `parentAs.entity` of R.
- **Paths under the alias are flat**, as under any union: a path one branch does not reach
  (projected under the alias) is dropped for that branch (`SELECT_PATH_NOT_ON_TARGET`
  with `params.branch`) and absent on its rows; only a path no branch reaches is `UNKNOWN_PATH`.
  The alias is `null` only where its record is not there: a branch that reaches none of the paths
  projected under the alias leaves an empty object (`{}`) on the rows whose record exists.
- **Like any continued alias** it is projected and continued (a stage under it goes to the owners
  of the targets that have a branch), never sorted or filtered here (`RESOLVE_NOT_SORTABLE`,
  `RESOLVE_NOT_FILTERABLE`); under `elements: "all"` nothing continues (`NOT_CONTINUABLE`). The
  stage counts once toward `MaxContinuedStages` and adds no owner call: each branch rides in the
  query its target's owner is sent anyway, as the ordinary resolve a `forTarget` stage sends.
- **Under an alias a continued stage added**, the keys are that alias's targets, which only its
  owner knows: when the branches all start at that one alias and the keys are not targets of R,
  the stage travels as written and that owner checks and splits it.
- An owner that cannot bind a branch refuses it as it refuses any continued stage; the error
  comes back at this stage with the branch's path and `params.owner.target` naming the branch.

## Stage: `unwind`

```jsonc
{ "unwind": { "path": "items", "as": "item", "includeIndex": "itemIndex", "preserveNull": true } }
```

`path` must be a collection at the current shape (`NOT_A_COLLECTION`): a collection member, or
the alias of a `lookup`. `as` is required (`INVALID_ALIAS`, `params` `stage: "unwind"`,
`member: "as"`, `reason: "required"`): each row holds one element under that name, and the
collection leaves the row. The row carries `item` and no `items`, and a later path under `items`
is `UNKNOWN_PATH` (`params` `reason: "unwound"`, `collection`, `alias`) naming the alias to read
instead. The same holds for a lookup's alias: after `unwind { path: "orders", as: "order" }` the
child is `order`, and `orders` is not in the row. `includeIndex` is an `int`, the element's
position in the stored collection. `preserveNull` keeps documents whose collection is empty or
absent, once, with the alias and the index null.

There is nothing to choose: `keepPath` is not a member (`UNKNOWN_STAGE_MEMBER`, `params`
`stage: "unwind"`, `member: "keepPath"`, `reason: "removed"`). An unwind of a collection that a
join of an earlier stage reads its keys from (a resolve through `items.*`) is `UNWIND_ORDER`
(`params` `reason: "joinReadsCollection"`, `join`, `alias`): join after the unwind, through the
alias. Contract 1 unwinds as it always has: in place without `as`, beside the alias with it.

```jsonc
{ "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } }
```

`flatten` unwinds a self-similar tree: it names a member of the element (members only some
variants carry included) that is a collection of the same kind of element, such as a group item's
`items`. The rows are every element and every descendant, in pre-order, each without its nested
collection, down to `MaxFlattenDepth` levels (5; the collection itself is level 1). Rows nested
deeper are left out and the page carries `UNWIND_DEPTH_TRUNCATED` (`params` `path`, `depth`, `rows`);
under `strict` that refuses. A `flatten` that names a member which is not such a collection, or a
`path` that is not a collection of objects, is `FLATTEN_NOT_RECURSIVE`; an unknown member is
`UNKNOWN_PATH`. It counts toward `MaxUnwindStages` and is refused under contract 1
(`LEGACY_STAGE_UNSUPPORTED`).

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
  the instant it holds. `day` and coarser buckets correspond to calendar ranges in the zone;
  sub-day buckets are elapsed-time based. `dateTrunc` is a key only: the rows of a bucket are
  asked for with a range on the member (`gte` its start, `lt` its end) that the caller computes
  (semantics guide, *Grouping*).
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
- **A group over no key** (`"by": []`) is the totals of the rows that reach it: one row with the
  aggregates, no order, no next page. Over no row at all there is no group and so no row: an
  empty `items` is the total of nothing.
- A string key folds case and accents under the collation: `Open` and `OPEN` are one group,
  reported under the first value met. `countDistinct`, `min` and `max` over a string fold the
  same way.
- After `group` the shape is only the `by` and `fields` aliases (unique among each other, at
  most `MaxGroupFields`, 40 unless the host says otherwise; one more is `MAX_GROUP_FIELDS_EXCEEDED`
  with `params { "limit": "maxGroupFields", "max", "count" }`); rows carry those names only. A page after `group` continues from its
  cursor like any other.
- **A `resolve` may follow a group.** A key over a member that declares a reference keeps the
  reference, so the record a key names is joined to the group's row from the key's alias:

  ```jsonc
  { "group": { "by": [ { "path": "customerId", "as": "customer" } ], "fields": { "revenue": { "sum": "amount" } } } },
  { "resolve": { "path": "customer", "as": "c" } },
  { "project": { "customer": 1, "revenue": 1, "c.name": 1 } },
  { "sort": [ { "revenue": "desc" } ] }, { "page": { "limit": 100 } }
  ```

  It is the `resolve` it would be before the group, with every member it has there (`filter`,
  `target`, `parentAs`, `onMissing`, `outcomeAs`) and the same executor: a target of this service
  joins in the aggregate, another service's is fetched for the page's keys. An aggregate
  (`first`, `min`, …) and a key over a member without a reference declare none
  (`RESOLVE_NOT_DECLARED`), and so does a key over a reference that picks its target by another
  member of the row (`params.reason: "conditionalAfterGroup"`: resolve it before the group). A
  `lookup` and an `unwind` still have nothing to work on after a group.

## Stage: `project`

```jsonc
{ "project": { "id": 1, "number": 1, "department.name": 1 } }   // inclusion: only these
{ "project": { "internalNotes": 0 } }                             // exclusion: everything else
```

All `1` or all `0` (`MIXED_PROJECTION`). `id` is kept in an inclusion unless excluded
explicitly; at most `MaxProjectionFields` paths. The reserved key `"$default": 1` includes the
entity's key and display members; it applies to the entity's own shape, not after a `group` or
`unwind`. How projections interact with join aliases, sorts and cursors:
[`oxql-semantics.md`](oxql-semantics.md#projections).

**The projection alone decides the row's shape.** Under a join alias it names the paths the alias
shows (`"vehicle.registrationPlate": 1`): exactly those, without the key unless it names it, and the
join loads them. What a stage only reads (a `match` on `vehicle.status`, the key of a later
`resolve`, the member a `lookup` joins on) is loaded and never shown.

**Without a `project`** the row is the whole entity row, and each join alias shows the target's key
and its display member (an alias of another service's rows: what its owner answers to `$default`,
its own key and display member): the same as an alias a projection names whole (`"vehicle": 1`). To see another member of a join, name it in a projection; a projection then
lists the entity's own members to keep as well.

## Stage: `sort`

```jsonc
{ "sort": [ { "createdAt": "desc" }, { "number": "asc" } ] }
{ "sort": [ { "number": { "direction": "asc", "caseSensitive": true } } ] }   // exact order
```

Each entry is one object of one path and a direction `asc` | `desc` (`INVALID_SORT_DIRECTION`),
or the object form with `direction` and `caseSensitive` (any other member is
`UNKNOWN_STAGE_MEMBER`, a missing `direction` `INVALID_SORT_DIRECTION`). Paths must be scalar and sortable in the current shape (not under a
collection); after `group` only keys and aggregates. On a root shape the engine appends `id`
as the tie-breaker; after `group` it appends every group key the sort does not name, ascending.

A string orders under the collation, case and accents folded, unless the entry says
`caseSensitive: true`. An exact sort runs in a request where every string comparison and key is
exact too; when another comparison, sort or group key of the request folds, it is refused with
`OPTION_NOT_APPLICABLE`, as is `caseSensitive` on a member that is not a string.

## Stage: `page`

```jsonc
{ "page": { "limit": 50 } }                                      // first page
{ "page": { "limit": 50, "count": true } }                       // first page, with one bounded count step beside it
{ "page": { "limit": 50, "count": { "upTo": 10000 } } }          // …that needs to count no further than 10 000
{ "page": { "limit": 50, "cursor": "<nextCursor>" } }            // next page
{ "page": { "limit": 0, "count": { "budgetMs": 800 } } }         // the count alone, refined within 800 ms
{ "page": { "limit": 0, "count": { "budgetMs": 800, "upTo": 50000 } } }
```

Once and last (`STAGE_AFTER_PAGE`, `MULTIPLE_PAGE_STAGES`). A page carries `limit`, `cursor` and
`count`. `limit` between 1 and `MaxPageSize` (`INVALID_PAGE_LIMIT`, `PAGE_SIZE_EXCEEDED`); without
a limit, or without a page stage at all, `DefaultPageSize`.

A page is addressed by its cursor and by nothing else: `offset` is not a member
(`UNKNOWN_STAGE_MEMBER`, `params` `stage: "page"`, `member: "offset"`, `reason: "removed"`,
`use: "cursor"`). Cursors are opaque, signed, and bound to the query: a cursor from another
filter, another sort or a tampered one is `CURSOR_INVALID`. A cursor is bound to what decides
which rows there are and in which order: the entity, the organisation, `filter`, every `match`,
`unwind`, `group` and `sort`, and every join a condition, a sort, an unwind, a group or another
join reads. What only shapes the row is outside it: the trailing `project` (every projection
that only sorts, display-only joins and the page follow), a `lookup` or `resolve` nothing but the
row's display reads (its `filter`, `limit`, `sort`, `outcomeAs` and `onMissing` with it), `limit`,
`count` and `strict`. So showing or hiding a column, with the join that feeds it, keeps a cursor
valid; changing a filter or the sort does not. `limit` may differ from page to page.

### Counts

`count` is `true`, `false` (the default) or `{ "upTo": n, "budgetMs": ms }` (both optional
positive integers; anything else is `INVALID_PAGE_LIMIT`, an unknown member of the object
`UNKNOWN_STAGE_MEMBER`). `includeTotalCount` is contract 1's and is `UNKNOWN_STAGE_MEMBER` under
contract 2 (`params` `member: "includeTotalCount"`, `reason: "removed"`, `use: "count"`).

Every answer says what the engine knows of the number of matching rows under `pageInfo.count`:

```jsonc
{ "state": "exact",   "value": 412 }
{ "state": "atLeast", "value": 1000, "stoppedBy": "step", "ms": 3 }
{ "state": "unknown", "reason": "timeout", "ms": 500 }
```

| member | meaning |
|---|---|
| `state: "exact"` | every matching row was counted, or the list was walked to its end; `value` is the count |
| `state: "atLeast"` | a proven lower bound: `value` rows were found and more exist or may exist |
| `stoppedBy` (with `atLeast`) | `page`: only the rows the pages served, and the one that told a next page exists, are known. `step`: the count beside a page is one step of `Count:StepRows` rows and found more; the count alone goes further. `cap`: the request's `upTo` or the host's `CountCap` was reached; counting on would go no further. `budget`: a further step of the count alone was predicted not to fit the time left and was not sent. `time`: a step ran out of time; the bound is what the step before it proved |
| `state: "unknown"` | the count that was asked for did not finish; `reason` is `timeout` or `failed` |
| `ms` | the time spent counting, when a count step was waited for |

What is known without counting, on every page, asked or not:

- A page without a next one ends the list: `exact`, the rows served so far. On a first page that
  holds every row this is the total, and a count step that was started is not waited for.
- Any other page: `atLeast` the rows served so far plus one, `stoppedBy: "page"`. The cursor
  carries the rows served, so the number grows with the walk and the last page turns it exact.

`count: true` on a page (`limit` 1 or more) adds one count step that runs beside the page: it
counts up to `Count:StepRows` rows (1 000), lowered by `upTo` and by the host's `CountCap`
(100 000), within `Count:BudgetMs` (500; `budgetMs` sets the request's own, at most
`Count:MaxBudgetMs` 5 000). Up to its bound the step is `exact`; above it the answer is `atLeast`
the bound, stopped by `cap` when the bound is the request's `upTo` or the host's cap and by
`step` otherwise. The step never fails the rows and holds them back no longer than its
budget: a step that times out or fails leaves `unknown`, and the rows are answered as they are.
Beside a page of groups the count is sent only when the page comes back full: a page of groups
that is not full is its own exact count.
A bound below what the pages already served gives way to the rows served plus one. No diagnostic
repeats any of this (`TOTAL_COUNT_CAPPED` is contract 1's).

`limit: 0` with `count` is the count alone: no page is read, `items` is empty, `hasNextPage` is
false and there is no cursor (the answer says nothing of pages). It takes no `cursor`, and
`limit: 0` without `count` is `INVALID_PAGE_LIMIT` (`params` `limit: 0`, `needs: "count"`). The
count runs in steps within its budget: a first step of `StepRows` rows, which is `exact` when it
finds no more and proves a lower bound otherwise; then a step sized to what the time left is
predicted to reach, from what the first took, up to `upTo` or the host's `CountCap` (for a count
the database answers quickly that is the ceiling at once: two commands in all). A step that
finds no more rows than its bound is `exact`. A further step is sent only when ten times the last
one is predicted to fit what is left of the budget; otherwise the answer is `atLeast` the last
bound, `stoppedBy: "budget"`. Each step's own time limit is what is left of the budget; a step
that runs out leaves the bound before it, `stoppedBy: "time"`, and a first step that does leaves
`unknown`. A list shows its first page at once and asks for the count alone beside it.

After `unwind` a count counts element rows, after `group` groups. The count pipeline carries the
filters and never the sort or the cursor; it carries a `lookup` or a local `resolve` only when a
later `match`, `unwind`, `group` or `resolve` reads its
alias. A `lookup` or a local `resolve` that no later `match`, `sort`, `unwind`, `group` or
`resolve` reads, that no `group` follows and whose alias every later `project` passes whole runs
after the page is taken, on the page's rows alone; a `project` in between keeps the join's
local key in storage for it and the row leaves the key out when it was not asked for, so the
rows are the same either way. Under contract 2 such a join written before an `unwind` runs before
that unwind, once per parent and not once per element of the page (see *Where the engine places a
stage* in [`oxql-semantics.md`](oxql-semantics.md)).

## Stage: `values`

```jsonc
{ "values": { "path": "customer.address.companyName" } }                       // the values, by value
{ "values": { "path": "status.name", "counts": true } }                        // with the rows that hold each, most first
{ "values": { "path": "customer.address.companyName", "search": "mü", "limit": 200, "counts": true, "scan": 20000 } }
```

The distinct values of one path among the rows that reach the stage: what a set filter lists.
It stands **in place of `page`**: once, last, and nothing follows it, a `page` included
(`STAGE_AFTER_PAGE`). Everything before it is the pipeline as it is (stages and the request's
`filter`); the stage replaces the row by one row per value:

```jsonc
POST query { "entityType": "transport.shipment",
  "filter": [ { "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } } ],
  "pipeline": [ { "values": { "path": "status.name", "counts": true } } ] }
200 { "items": [ { "value": "Open", "count": 24 }, { "value": "Closed", "count": 10 }, { "value": "Planned", "count": 5 } ],
      "pageInfo": { "hasNextPage": false, "count": { "state": "exact", "value": 3 } } }
```

| member | | |
|---|---|---|
| `path` | required | A scalar path a `group` key could name where the stage stands: a member of the entity, of an unwound element, of a record joined in the aggregate, a key or an aggregate of a `group`. **Also a scalar under collections that are not unwound** (`items.status.name`): the stage unwinds them for the values alone |
| `search` | optional string | Keeps the values that start with the text, folded as `startsWith` folds (case and accents under the collation). Only for a path that holds text (`OPTION_NOT_APPLICABLE` otherwise, `params` `option: "search"`, `kind`); at most `RegexMaxLength` characters. Empty or `null`: every value |
| `limit` | optional, 1 to `MaxValuesLimit` (500) | The most values answered; `Values:DefaultLimit` (100) when absent |
| `counts` | optional boolean | Each row also carries `count`, and the values lead by it |
| `scan` | optional, 1 to `Values:ScanRows` (50 000) | The most rows read for the list; the host's bound when absent |

- **The row** is `{ "value": … }`, with `"count": n` under `counts`. The value is written as a row
  writes the member: an enum as its number, a `long` and a `decimal` as strings, a date as
  `YYYY-MM-DD`, a `dateTime` as an ISO instant, a guid as text. `count` is a JSON number.
- **`null` is a value**: the member null or absent (and, under a collection, the row that holds no
  element) is the one row `{ "value": null }`. The empty string is a value of its own.
- **A string folds as a group key folds**: spellings the collation calls equal are one value,
  reported under the first spelling met.
- **Order**: with `counts`, by the count, most first, then by value; without, by value. Values
  order as a `sort` orders the member (a string under the collation), `null` first.
- **`hasNextPage: true`** says more values exist than `limit`. There is **no cursor**: a list is
  narrowed with `search`, never continued. `pageInfo.count` counts what the answer lists, the
  values: `exact` when the list is whole, `atLeast` `limit + 1` (`stoppedBy: "page"`) when it is not.
- **`scan`** bounds the rows the stage reads, whatever the request matches. When that many were
  read the answer carries the diagnostic `VALUES_SCAN_CAPPED` (`stage`, `path`, `params.scan`):
  the list is then of the values among those rows, and a count is a count within them. It is said
  as soon as the bound is reached, so a request that matches exactly `scan` rows carries it too.
  It stays a diagnostic under `strict`.
- **Under a collection that is not unwound** a value is one some element holds, and **`count`
  counts elements**, a row without any once under `null`; `scan` still counts rows.
- **Under an alias whose rows come from an owner after the page** (another service's record, a
  keyed join) the stage is refused: `NOT_CONTINUABLE`. Explain says it beforehand
  (`can.values: false`, `why.values: "owner"` or `"joinedAfterPage"`).
- **A sort before it** that only conditions follow is not run: the values are the same in any
  order of the rows, and ordering them would read every row before `scan` could apply. A
  projection before it stays where it is written; the path has to survive it.
- **Refusals**: a member the stage does not have `UNKNOWN_STAGE_MEMBER`; a member of another JSON
  kind, a `limit` or `scan` below 1 `INVALID_PAGE_LIMIT` (`params` `stage: "values"`, `member`);
  above the host's bounds `VALUES_LIMIT_EXCEEDED` (`params` `limit`: `maxValuesLimit`,
  `valuesScanRows` or `regexMaxLength`, `max`, `value`); a path that is no scalar `NOT_FILTERABLE`,
  a collection itself `GROUP_ON_COLLECTION`, an unknown one `UNKNOWN_PATH`.

**A set filter's list** is this request with every entry of the request's `filter` except the
column's own: the list then follows what the other filters leave, and still offers what the
column's own filter would hide. The engine has no member that excludes an entry; the caller leaves
it out.

What a list holds and how it relates to the group one would write by hand:
[`oxql-semantics.md`](oxql-semantics.md#distinct-values). Contract 1 has no such stage
(`UNKNOWN_STAGE`).

## Strict requests and the report page

`"strict": true` (contract 2; contract 1 refuses it with `LEGACY_STAGE_UNSUPPORTED`) makes a request
refuse with 422 `not_executable` rather than answer rows that lost data. Each refusal carries the
diagnostic it would otherwise have answered, as its error (see *Refusal*):

| code | what was lost |
|---|---|
| `RESOLVE_MISSING` | a reference resolved to nothing (`not_found`, `invalid_key`, `owner_unanswered`); refused by the stage's effective `onMissing`, which is `refuse` under strict unless the stage says otherwise |
| `RESOLVE_AMBIGUOUS` | a key more than one record holds |
| `RESOLVE_TIMEOUT`, `RESOLVE_UNREACHABLE`, `RESOLVE_PARTIAL` | an owner that did not answer, or not in full |
| `LOOKUP_TRUNCATED`, `RESOLVE_TRUNCATED`, `UNWIND_DEPTH_TRUNCATED` | children, targets or nested items cut at a limit |
| `PAGE_INCOMPLETE` | more rows match than the one page holds (below) |

A strict request whose `page` has no `cursor` is a report page: it may ask for a
`limit` up to `MaxReportPageSize` (5 000; `MaxPageSize` when that is larger), and when more rows
match than it holds it is refused with `PAGE_INCOMPLETE` (`params` `limit`, `max`): "narrow the root
or raise the page limit". Losses visible on the page itself
(`PAGE_INCOMPLETE`, the truncations, a missing inline reference) refuse before any owner is called;
the others after the owners answered, all of them listed. `strict` travels in the query sent to an
owner that runs continued stages, and a strict request never reads a cached "not found".

## Response

```jsonc
{
  "items": [ … ],
  "pageInfo": { "hasNextPage": true, "nextCursor": "…", "count": { "state": "atLeast", "value": 51, "stoppedBy": "page" } },
  "diagnostics": [ { "code": "ENTITY_ID_RETIRED", "message": "…", "stage": null, "path": null, "params": { "currentId": "logistics.shipment" } } ]
}
```

Rows are in the wire encoding at every depth: `id`, camelCase, `long` and `decimal` as
strings, `dateTime` ISO UTC, `guid` string, enum as number, `date` as `YYYY-MM-DD`,
`timeSpan` as an ISO duration, binary as base64. Grouped rows carry the aliases only; unwound
rows the element under the alias plus the index, and not the unwound collection; resolved rows the object or `null`
under the alias; looked-up rows the array (`first`: the object or `null`); a `parentAs` alias
`{ "entity": "<entity id>", … }`; an `elements: "all"` alias an array; the rows of a request that
ends in `values` are `{ value, count? }`, one per value. `pageInfo.count` is on
every answer (see *Counts*); `nextCursor` only when a page follows; `diagnostics` only when there
are any. A diagnostic or an error that belongs to an entry of the request's `filter` carries
`filter` (the entry's `id`, or its index in the list when it has none) and no `stage`.

A diagnostic about references names the rows it met, by their index in `items`, at most
`MaxReportedRows` (50) of them:

```jsonc
{ "code": "RESOLVE_MISSING", "stage": 6, "path": "erpLine.sourceBillingLineReference.id",
  "message": "2 rows reference a record that does not exist, by a key that does not convert, or that its owner did not answer ('sourceLine').",
  "params": { "alias": "sourceLine", "count": 2, "truncated": false,
              "rows": [ { "row": 0, "key": "91c0e2aa-…", "outcome": "not_found" },
                        { "row": 3, "element": 1, "key": "SHIP-12", "outcome": "invalid_key" } ] } }
```

`count` is every affected slot, `truncated` says the list was cut, `element` appears only under
`elements`. The outcomes are explained in
[`oxql-semantics.md`](oxql-semantics.md#outcomes-and-data-loss).

### Where the time went

Every answer of `query` and `batch` carries a `Server-Timing` header: the total up to the first
byte of the body, the time the request waited for the database with its commands and round trips,
and the rest, which is the host's own (`ours` = `total` − `db`), with its parts. Durations are
milliseconds.

```
Server-Timing: total;dur=51.3, db;dur=47.6;desc="1 cmd / 1 rt", ours;dur=3.7, bind;dur=0.11, compile;dur=0.05, convert;dur=0.64, net;dur=0.8;desc="est"
```

`addon` (`desc="db"`: the time the request waited for the host's read of addon definitions at
its database; part of `db`), `resolve` (the keyed joins, with their owner calls and cache hits),
`remote` (the time waited for other services' owners) and `count` (the time finished rows waited
for their count) appear when they occurred; `net` is the estimated network part of `db` (round
trips × the driver's measured round-trip time).

A request that asks (`"timing": true`, or the request header `X-OxQL-Timing: 1`) also gets the
breakdown as `timing`, the last member of the answer:

```jsonc
"timing": {
  "totalMs": 50.29, "oursMs": 2.69,              // totalMs − db.ms = oursMs, always
  "parseMs": 0.15,                               // reading and binding the body, before the engine took the request
  "bindMs": 0.11, "addonMs": 0.56, "compileMs": 0.05,
  "db": { "ms": 47.6, "commands": 1, "roundTrips": 1, "rttMs": 0.8, "networkEstimateMs": 0.8,
          "list": [ { "kind": "page", "collection": "transport.shipment", "ms": 47.6, "firstBatchMs": 47.59, "drainMs": 0.01,
                      "docs": 101, "bytes": 45322, "getMores": 0, "parallelWith": null } ] },
  "resolve": { "ms": 0, "calls": 0, "cacheHits": 0, "remoteMs": 0, "owners": [] },
  "countWaitMs": 0, "convertMs": 0.64, "rows": 100, "build": "Release" }
```

- `db.ms` is the time with at least one command in flight: commands sent together count once.
  A command's `ms` is what the driver saw (the server's execution, the network, reading the
  reply); the server alone is found in the database's profiler by the command's `comment`, the
  request's correlation id.
- `db.list[]`: `kind` is `page`, `count`, `countRung` (a step of the count alone), `values` (the
  one aggregate of a request that ends in `values`), `probe` (a
  strict request's truncation check), `resolve` (an owner query this host ran for a join target
  of its own) or `addon` (the host's read of the organisation's addon definitions, where the
  request had to wait for it); `collection` is the entity read (for `addon` the collection as
  the database names it); `docs` the documents returned and `bytes` their size as BSON;
  `getMores` the further round trips; `parallelWith` the index of the first earlier command
  still in flight when this one left, or null. `discarded: true` marks a command whose answer
  was not used (a count the page made needless); `failed: true` one that ended without an
  answer; `running: true` one that had not ended; `shared: true` (an `addon` read only) one
  that another request had sent and this one waited for: its time is in `db.ms`, and it counts
  neither in `db.commands` nor in `db.roundTrips`.
- `addonMs` is the time the host's addon source was asked. Where the source waited for its
  database and says so, that wait is listed under `db.list[]` (`kind: "addon"`) and is database
  time, not `oursMs`; a host that answers from its cache lists nothing. `bindMs` never holds it.
- `resolve.owners[]`: one entry per join target: `service`, `entity`, `remote` (another service's
  owner), `ms` of the calls that carried its keys, `keys` asked, `cacheHits`. An owner of another
  service that was asked answers with its own `timing` per query (`timing: [ … ]`), so the
  database's share at that owner is known here. `resolve.remoteMs` is the time waited for other
  services; it is part of `oursMs`.
- In a batch each query asks for itself and carries its own `timing`; the header is the sum.

## Refusal

```jsonc
{ "type": "validation_error", "title": "The request could not be bound.",
  "errors": [ { "code": "UNKNOWN_PATH", "message": "…", "stage": 2, "path": "items.quantity" } ] }
```

An error belongs to a caller stage (`stage`), to an entry of the request's `filter` (`filter`: the
entry's `id`, or its index in the list when it has none; no `stage`), or to the request as a whole
(neither).

| `type` | HTTP | when |
|---|---|---|
| `validation_error` | 400 (413 for `REQUEST_TOO_LARGE`) | a caller error; every binding error at once |
| `access_denied` | 403 | no organisation in the context |
| `not_executable` | 422 | well-formed but not executable on this host |
| `timeout` | 504 | the aggregate exceeded `Execution:MaxTimeMs` |
| `internal_error` | 500 | an engine fault; details only when the host allows them |

A strict refusal is `not_executable` whose `errors` are the diagnostics that lost data, each with
its `stage`, `path` and `params` (the rows included). An owner's refusal of a continued stage is
`RESOLVE_REFUSED` pointing at the caller's stage; the owner's errors follow, mapped back to the
caller's `stage` and `path`, each with `params.owner`:

```jsonc
{ "code": "UNKNOWN_PATH", "stage": 7, "path": "lastAttempt.statuz", "message": "…",
  "params": { "owner": { "service": "transport", "entity": "transport.shipment",
                         "target": "transport.shipment#billingLines", "stage": 3, "path": "statuz" } } }
```

`owner.service` is `null` when the owner is this host. A contract 1 request refused for a
construct only contract 2 has (a contract 2 stage member, `is`, `strict`, a continued stage, `caseSensitive`,
the object form of a sort entry, the number form of `includeTotalCount`) ends its message with "This request was read as contract 1 because it
carries no 'X-OxQL-Contract: 2' header." The codes are unchanged.

### Error codes

The closed list, 72 codes (contract 2 has the twelve marked †):

| area | codes |
|---|---|
| entity | `UNKNOWN_ENTITY` |
| request | `UNKNOWN_REQUEST_MEMBER`† (a top-level member a request does not have) |
| path | `INVALID_PATH`, `UNKNOWN_PATH`, `NOT_STORED`, `NOT_FILTERABLE`, `NOT_SORTABLE`, `NOT_A_COLLECTION`, `UNWIND_ORDER`, `ALIAS_COLLISION`, `INVALID_ALIAS` |
| operand | `INVALID_OPERAND`, `UNKNOWN_ENUM_MEMBER`, `OPERAND_NOT_ARRAY`, `DECIMAL_TEXT_NOT_ORDERABLE` (an ordered comparison on a decimal stored as text), `UNBOUND_VARIABLE`, `INVALID_VARIABLE` |
| condition | `UNKNOWN_OPERATOR`, `EMPTY_LOGICAL_GROUP`, `OPTION_NOT_APPLICABLE` (also a stage option where it does not apply), `INVALID_REGEX`, `REGEX_TOO_LONG`, `ANY_NOT_APPLICABLE`, `UNKNOWN_VARIANT`† (`is`) |
| stage | `UNKNOWN_STAGE`, `UNKNOWN_STAGE_MEMBER`, `STAGE_AFTER_PAGE`, `MULTIPLE_PAGE_STAGES`, `MIXED_PROJECTION`, `GROUP_ON_COLLECTION`, `UNKNOWN_AGG_FUNCTION`, `INVALID_AGGREGATE_ARGUMENT`, `INVALID_DATE_TRUNC_UNIT`, `INVALID_TIMEZONE`, `INVALID_SORT_DIRECTION`, `FLATTEN_NOT_RECURSIVE`†, `LOOKUP_NOT_DECLARED`, `LOOKUP_ON_NOT_ENTITY`†, `NOT_CONTINUABLE`†, `UNION_CARDINALITY_MISMATCH`† (`byTarget`), `RESOLVE_NOT_DECLARED`, `RESOLVE_ON_COLLECTION`†, `RESOLVE_TARGET_NOT_DECLARED`†, `RESOLVE_PARENT_NOT_ITEM`†, `RESOLVE_NOT_FILTERABLE`, `RESOLVE_NOT_SORTABLE` |
| limits | `MAX_PIPELINE_STAGES_EXCEEDED`, `MAX_LOOKUP_STAGES_EXCEEDED`, `MAX_UNWIND_STAGES_EXCEEDED`, `MAX_RESOLVE_STAGES_EXCEEDED`, `MAX_CONTINUED_STAGES_EXCEEDED`†, `MAX_GROUP_FIELDS_EXCEEDED`, `MAX_PROJECTION_FIELDS_EXCEEDED`, `MAX_CONDITIONS_EXCEEDED`, `MAX_VARIABLES_EXCEEDED`, `INVALID_PAGE_LIMIT`, `PAGE_SIZE_EXCEEDED`, `LOOKUP_LIMIT_EXCEEDED`, `VALUES_LIMIT_EXCEEDED`† (a `values` stage over the host's bounds), `MAX_OFFSET_EXCEEDED`, `BATCH_TOO_LARGE`, `REQUEST_TOO_LARGE` (413) |
| cursor | `CURSOR_INVALID` |
| access | `ACCESS_DENIED` (403) |
| execution | `RESOLVE_UNAVAILABLE` (422), `RESOLVE_REFUSED` (422, wraps the owner's errors), `SEMI_JOIN_TOO_LARGE` (422), `QUERY_TOO_EXPENSIVE` (422), `QUERY_TIMEOUT` (504), `INTERNAL_ERROR` (500), `PAGE_INCOMPLETE`† (422, strict only) |
| compat | `LEGACY_STAGE_UNSUPPORTED` (a v1 `lookup` or `resolve`, `strict`, or a contract 2 stage member under contract 1) |

Under `strict` (or a stage's `onMissing: "refuse"`) the data-loss diagnostics below are errors of a
422 refusal as well.

### Diagnostic codes

Never a refusal outside `strict`; carried in `diagnostics` with machine-readable `params` where
useful. 14 codes (contract 2 has the six marked †):
`ENTITY_ID_RETIRED` (`params.currentId`), `TOTAL_COUNT_CAPPED` (`params.cap`; contract 1 and internal calls only, a contract 2 count says where it stopped under `pageInfo.count`),
`RESOLVE_TIMEOUT`, `RESOLVE_UNREACHABLE`, `RESOLVE_PARTIAL` (keys an owner was not asked for or did
not answer in full), `SORT_ON_ADDON`, `REGEX_UNANCHORED`, `DECIMAL_TEXT_EXCLUDED` (an ordered
comparison on a decimal covered the Decimal128 rows only), `RESOLVE_MISSING`† (references that
resolved to nothing, under `onMissing` `report` or `refuse`), `RESOLVE_AMBIGUOUS`† (a key more than
one record holds), `RESOLVE_TRUNCATED`† (an `elements: "all"` alias over `MaxLookupLimit`),
`LOOKUP_TRUNCATED`† (a lookup over its `limit`), `UNWIND_DEPTH_TRUNCATED`† (a `flatten` over
`MaxFlattenDepth`), `VALUES_SCAN_CAPPED`† (a `values` stage read as many rows as its bound lets
it; `params.scan`). Every one but the first two, `SORT_ON_ADDON`, `REGEX_UNANCHORED`,
`DECIMAL_TEXT_EXCLUDED` and `VALUES_SCAN_CAPPED` is data loss and refuses under `strict`. The HTTP status and a typical cause
of every code: [`oxql-operations.md`](oxql-operations.md#codes).

## Explain request

`POST /oxql/explain` takes the body of `POST /oxql/query`, or an envelope around it:

```jsonc
{
  "query": { "entityType": "…", "variables": { … }, "filter": [ … ], "strict": true, "pipeline": [ … ] },   // page.cursor is ignored
  "include": ["shape", "notes"],   // default; also "types", "docs", "plan", "indexes"
  "shape": { "depth": 2 },         // with "types": levels of member rows below each root, 1 to 3
  "remote": "check",               // "check" (default) or "cached"
  "catalog": [
    { "id": "c1", "entity": "transport.delivery_attempt", "referencing": true },
    { "id": "c2", "entity": "transport.shipment", "prefix": "tours", "depth": 1 }
  ]
}
```

- A body with `entityType` at the top is a plain query, which is the envelope with its defaults:
  `include` `shape` and `notes`, remote `check`, no catalog. An envelope
  member other than these five, or a member of the wrong kind, is a 400 ProblemDetails. More than
  `Explain:MaxStages` stages or `MaxCatalogEntries` catalog entries, a `shape.depth` above
  `MaxShapeDepth`, and an `include` or `remote` value the engine does not know are 400
  `EXPLAIN_LIMIT`, before anything is bound.
- `include` names what the answer carries beyond the verdict, the errors, the stages and the
  aliases; a list replaces the default. `shape`: the row after each stage, the types its roots have
  (by reference: entity, service and the revision of the service's schema document, which describes
  the members) and the rules that say where the members stand. `notes`: the engine-behaviour notes.
  `types` (it implies `shape`): the member rows of every type written out, the flag sets and the
  per-root overrides, for a caller that holds no schema document; `shape.depth` and the row cap
  apply to it alone. `docs`: the descriptions of types, members and enum values in the member rows.
  `plan`: the bound form, the emitted stages and every owner query. `indexes`: the index advisory
  (the definition of each index that serves the request and whether the collection has it), the
  only extra read explain makes.
- `remote`: `check` checks what an owner binds at that owner. `cached` asks no owner: it answers
  from the owner answers a `check` of the last 30 seconds left (per organisation, user and forwarded
  body), and says what it therefore does not know (`cache.complete: false`, a `REMOTE_UNCHECKED` note
  with reason `cached`). It is the tier for an edit in progress: explain with `cached` while the query
  changes and with `check` once it has settled. A complete `cached` answer carries the `etag` of its
  `check`; send it as `If-None-Match` and an unchanged answer is 304. The `etag` covers the whole
  answer and who asked, so a 304 always means the answer the caller holds is the one it would get.
- A catalog entry looks up an entity outside the query (a lookup's `from`, a palette, the entities
  referencing one). It carries `id`, `entity` (an entity id, or `entity#item` for the element of an
  item collection; another service's entity is answered by its owner), an optional `prefix` (a path,
  which is checked; with `include: ["types"]` the answer lists the members below it, which is how a
  member the rows cut is read), an optional `depth` 1–3, and `referencing: true` for the same-host
  entities referencing the entity. A
  malformed entry is answered with an `error` under the usual codes (`UNKNOWN_STAGE_MEMBER`,
  `INVALID_OPERAND`, `INVALID_PATH`, `UNKNOWN_ENTITY`, `UNKNOWN_PATH`), never a refusal.

The answer, the limits of one explain, and what explain reads and reveals are in
[`oxql-operations.md`](oxql-operations.md#post-oxqlexplain).

## Batch

```jsonc
POST /oxql/batch
{ "queries": [ <request>, <request> ], "maxTimeMs": 2000 }
```

Always HTTP 200 with `{ "results": [ … ] }` in order; each entry is a full success body or a
refusal envelope. `maxTimeMs` bounds the whole batch: the queries run one after another, each
under what is left, never above the host's ceiling. More than `MaxBatchQueries` is a 400
`BATCH_TOO_LARGE` refusal of the whole batch.

## Contract 1 (compatibility mode)

While the host's `Compat:Enabled` is true, a request without `X-OxQL-Contract: 2` is bound
by the compatibility binder: the entity id is matched case-insensitively (retired ids too),
paths may be spelled as stored (`MatchCode`, `Department._id`) and are resolved against the
same folded shape, wire spellings work too, the v1 type-hint operands are accepted, operators
and sort directions are read case-insensitively, a string comparison is exact unless it says
`ignoreCase: true` (a pattern; `caseSensitive` is not an option there), sorts and group keys
are exact, a sort entry's direction is a string (the object form is `INVALID_SORT_DIRECTION`),
and rows come back in the v1 encoding. The v1 `lookup` (`localPath`/`foreignPath`) and `resolve`
stages are refused with `LEGACY_STAGE_UNSUPPORTED`, and so is the number form of
`page.includeTotalCount`: contract 1 keeps the boolean. Contract 1 also keeps what contract 2 gave
up: `page.offset` (up to `MaxOffset`, `MAX_OFFSET_EXCEEDED` above it; a page continues from a
cursor or jumps by an offset, not both), `page.includeTotalCount` answered as `totalCount` and
`totalCountCapped` with the `TOTAL_COUNT_CAPPED` diagnostic, an `unwind` without `as` (the element
replaces the collection in place) or with it (the element beside the collection), a cursor
bound to the whole pipeline including its projection, and any variable name. What only contract 2
has is not read under contract 1: `strict` and the new stage members (`page.count` among them) are
`LEGACY_STAGE_UNSUPPORTED`, `is` is `UNKNOWN_OPERATOR`, a `values` stage is `UNKNOWN_STAGE` like any
stage contract 1 never had, a continued stage is `NOT_CONTINUABLE`, and
unknown top-level members, `filter` among them, are ignored as before. Each
such refusal says the request was read as contract 1 for want of the header (see *Refusal*). Every
contract 1 request is logged under `OxQL.Compat`. When compatibility is switched off, every request
is contract 2.
