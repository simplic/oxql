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
  "strict": true,                              // optional (2.1): refuse instead of losing data
  "pipeline": [                                // stages, executed as written
    { "match": { … } },
    { "page": { "limit": 50 } }
  ]
}
```

- A request carries `entityType`, `variables`, `pipeline` and `strict`. Any other top-level member
  is `UNKNOWN_REQUEST_MEMBER` under contract 2 (2.0 ignored it silently). The member `keyedBy`
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
  paths relative to the element (compiled to `$elemMatch`). It is refused on a collection already
  unwound, on a collection reached through another collection, and on collections of scalars,
  where the default "some element" form applies (`ANY_NOT_APPLICABLE`).
- A comparison on a `string` member folds case and accents: `muller` matches `Müller` and
  `MÜLLER`. The request then runs under the host's collation (German, primary strength, unless
  configured otherwise), `eq neq in nin gt gte lt lte` compare under it, `startsWith` is a
  range under it, and `contains` and `endsWith` are patterns, which fold case but not accents.
  A request that compares, sorts and groups no string runs without a collation.
- `options.caseSensitive: true` compares exactly. It applies to `eq neq in nin contains
  startsWith endsWith` on `string` members (`OPTION_NOT_APPLICABLE` elsewhere: an ordered
  comparison orders under the collation of the whole request and cannot leave it, a `regex` is
  the caller's own pattern, and a member without text has nothing to fold). `options.ignoreCase`
  is accepted for one release as the alias with the opposite sense — `ignoreCase: false` is
  `caseSensitive: true`, `ignoreCase: true` restates the default — and the two must not
  disagree. On a member holding a single character, published as `string` and stored as its
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
    "select": ["id", "name", "createdAt"],   // optional hint: what a whole alias shows (see below); id is always kept
    "filter": { "isDeleted": { "eq": false } },  // optional condition on the child
    "limit": 20                               // optional, at most Limits:MaxLookupLimit
} }
```

The child must declare the reference (`LOOKUP_NOT_DECLARED`) and is scoped to the caller's
organisation inside the sub-pipeline. Any other member (`localPath`, `foreignPath`, `convert`,
`parentSelect`) is `UNKNOWN_STAGE_MEMBER`.

**`select` is a hint, not a bound.** A join loads what the query reads: its key, every path a later
stage reads under its alias, and what the row shows under it. Every member of the child can be read
under the alias; nobody lists in advance what later stages need. `select` says only what the alias
shows when the row keeps it **whole** (no projection, or `"documents": 1`): these paths and the key;
without `select`, the child's key and display members. A projection that names paths under the
alias (`"documents.name": 1`) decides alone: the alias shows exactly those paths, whatever `select`
names. A `select` path the child does not have is `UNKNOWN_PATH`. What joins load is in
[`oxql-semantics.md`](oxql-semantics.md#what-a-join-loads).

2.1 adds four members:

```jsonc
{ "lookup": {
    "from": "transport.delivery_attempt", "path": "shipmentId", "as": "lastAttempt",
    "on": "shipment",                              // the parent row: an alias instead of the entity itself
    "sort": [ { "dateTime": "desc" } ],            // order of the children
    "first": true,                                 // one child or null instead of an array
    "select": ["dateTime", "status.displayName", "text"]
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
    "select": ["id", "matchCode", "name"],    // optional hint: what a whole alias shows; default: the target's key and display members
    "filter": { "isDeleted": { "eq": false } }  // optional condition on the target, executed by its owner
} }
```

`path` must carry a declared reference (`RESOLVE_NOT_DECLARED`, whose message names the path's
kind). `select` is the same hint as a lookup's: it says what the alias shows kept whole, never what
may be read under it. Under a join alias, a reference whose cases test a sibling (`type`) reads that
sibling; the join that holds the reference loads it with the reference, and explain lists the read
(`use: "caseCondition"`). A join compares its id exactly, whatever the id's kind: a string id never folds. At most
`MaxResolveStages` (8) resolve stages run on this host; stages continued at an owner count there.

Five more members serve references that are typed, point at items, convert their key or sit in a
collection (how such references are declared: [`oxql-semantics.md`](oxql-semantics.md#references)):

```jsonc
{ "resolve": {
    "path": "erpLine.sourceBillingLineReference.id",
    "as": "sourceLine",
    "select": ["id", "status", "quantity.value"],
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
targets a path some target lacks (in `select`, or projected under the alias or its `parentAs`) is
dropped for that target (note `SELECT_PATH_NOT_ON_TARGET`), its member is absent on that target's
rows, and it is refused (`UNKNOWN_PATH`) only when every target lacks it. Contract 1 refuses the seven
members with `LEGACY_STAGE_UNSUPPORTED`.

**The collection guard.** A `path` under a collection that is not unwound names one key per element.
Without `elements` it is `RESOLVE_ON_COLLECTION` ("unwind it first, or set 'elements' to 'first' or
'all'"); a path crossing two such collections is `UNWIND_ORDER`. 2.0 bound such a path and joined an
arbitrary element.

**Where it runs.** A resolve with one unconditional case, one local entity target, no item, no key
conversion and no `elements` runs inline, as an indexed `$lookup` in the aggregate; its alias is an
entity row that later stages may filter and sort. (With a `filter` and outcomes that are read, an
`onMissing` other than `null` or an `outcomeAs`, it stays inline and joins its target a second
time without the filter, to tell an excluded record from a missing one.) Every other resolve runs as a keyed fetch after the page is fixed, at the target's
owner: another service, or this host in process for a local target. Its alias may be projected and
continued; a `match` on it is `RESOLVE_NOT_FILTERABLE` and a `sort` `RESOLVE_NOT_SORTABLE`, since it
is joined after the page is taken.

**Plain remote resolves** (a simple reference into another service) keep 2.0's behaviour: a `match`
on a member of the alias is a semi-join (the owner supplies the matching ids; more than
`MaxSemiJoinIds` is 422 `SEMI_JOIN_TOO_LARGE`), a `match` on the alias itself (`{ "veh": { "eq":
null } }`, `{ "veh": { "exists": true } }`) is refused before the owner is called
(`RESOLVE_NOT_FILTERABLE`), and a `sort` is refused (`RESOLVE_NOT_SORTABLE`). The owner binds a
semi-join condition under its own default; `caseSensitive` or `ignoreCase` travel to it as written.
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
- An owner on an engine older than 2.1 (read from its health) is refused before anything is sent:
  422 `OWNER_NOT_CAPABLE`. Contract 1 refuses continued stages with `NOT_CONTINUABLE`.

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
  collection that is not unwound. `as`, `select`, `filter`, `target` and `onMissing` are the
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
- **Paths under the alias are flat**, as under any union: a path one branch does not reach (in
  `select`, or projected under the alias) is dropped for that branch (`SELECT_PATH_NOT_ON_TARGET`
  with `params.branch`) and absent on its rows; only a path no branch reaches is `UNKNOWN_PATH`.
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

`path` must be a collection at the current shape (`NOT_A_COLLECTION`). Afterwards `path`
means the element, `as` is a copy of it and `includeIndex` an `int`. `preserveNull` keeps
documents whose collection is empty or absent.

```jsonc
{ "unwind": { "path": "items", "as": "item", "keepPath": false } }
```

`keepPath` (2.1, default `true`) set to `false` takes the unwound collection out of the row once
its element is under `as`: the row carries `item` and no `items`, and a later path under `items` is
`UNKNOWN_PATH` naming the alias to read instead. It needs `as` and a member collection (a join alias
unwound without `as` is already replaced by its element); otherwise `OPTION_NOT_APPLICABLE`. It is
also `OPTION_NOT_APPLICABLE` when a join of an earlier stage reads its keys from the collection (a
resolve through `items.*`): join after the unwind through the alias instead. A value other than
`true` or `false` is `INVALID_OPERAND`. Refused under contract 1 (`LEGACY_STAGE_UNSUPPORTED`).
Leaving it out keeps the 2.0 rows.

```jsonc
{ "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } }
```

`flatten` (2.1) unwinds a self-similar tree: it names a member of the element (members only some
variants carry included) that is a collection of the same kind of element, such as a group item's
`items`. The rows are every element and every descendant, in pre-order, each without its nested
collection, down to `MaxFlattenDepth` levels (5; the collection itself is level 1). Rows nested
deeper are left out and the page carries `UNWIND_DEPTH_TRUNCATED` (`params` `path`, `depth`, `rows`);
under `strict` that refuses. A `flatten` that names a member which is not such a collection, or a
`path` that is not a collection of objects, is `FLATTEN_NOT_RECURSIVE`; an unknown member is
`UNKNOWN_PATH`. It counts toward `MaxUnwindStages`, pages by offset, and is refused under contract 1
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
- A string key folds case and accents under the collation: `Open` and `OPEN` are one group,
  reported under the first value met. `countDistinct`, `min` and `max` over a string fold the
  same way.
- After `group` the shape is only the `by` and `fields` aliases (unique among each other, at
  most `MaxGroupFields`); rows carry those names only. Paging after `group` is by offset
  behind the cursor.

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

**Without a `project`** the row is the whole entity row, and each join alias shows its `select` hint
with the key, else the target's key and display members: the same as an alias a projection names
whole (`"vehicle": 1`). To see another member of a join, name it in a projection; a projection then
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
{ "page": { "limit": 50, "includeTotalCount": true } }        // first page, counted up to the host's CountCap
{ "page": { "limit": 50, "includeTotalCount": 10000 } }       // first page, counted up to 10 000
{ "page": { "limit": 50, "cursor": "<nextCursor>" } }           // next page
{ "page": { "limit": 50, "offset": 200 } }                      // a jump, up to Limits:MaxOffset
```

Once and last (`STAGE_AFTER_PAGE`, `MULTIPLE_PAGE_STAGES`). `limit` between 1 and
`MaxPageSize` (`INVALID_PAGE_LIMIT`, `PAGE_SIZE_EXCEEDED`); without a limit, or without a page
stage at all, `DefaultPageSize`. `offset` above `MaxOffset` is `MAX_OFFSET_EXCEEDED`.

Cursors are opaque, signed, and bound to the query: a cursor from another pipeline, another
sort or a tampered one is `CURSOR_INVALID`. Keyset paging is null-aware on the root shape;
after `group` the cursor carries an offset. A 2.0 cursor continues under 2.1: a 2.1 member is
written into the fingerprint only when it changes what the stage does, and `strict` and
`onMissing` never are.

`includeTotalCount` is `true`, `false` (the default) or a positive integer. `true` runs a count
concurrently with the page, up to the host's `CountCap`; a positive integer is the request's own
cap, clamped to the host's (`0`, a negative number or a fraction is `INVALID_PAGE_LIMIT`).
Above the cap in force the count is that cap and `totalCountCapped` is true with a
`TOTAL_COUNT_CAPPED` diagnostic whose `params.cap` names it. The count pipeline carries a
`lookup` or a local `resolve` only when a later `match`, `unwind`, `group` or `resolve` reads its
alias. A `lookup` or a local `resolve` that no later `match`, `sort`, `unwind`, `group` or
`resolve` reads, that no `group` follows and whose alias every later `project` passes whole runs
after the page is taken, on the page's rows alone; a `project` in between keeps the join's
local key in storage for it and the row leaves the key out when it was not asked for, so the
rows are the same either way.

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

A strict request whose `page` has neither `cursor` nor `offset` is a report page: it may ask for a
`limit` up to `MaxReportPageSize` (5 000; `MaxPageSize` when that is larger), and when more rows
match than it holds it is refused with `PAGE_INCOMPLETE` (`params` `limit`, `max`): "narrow the root
or raise the page limit". `offset: 0` counts as paging. Losses visible on the page itself
(`PAGE_INCOMPLETE`, the truncations, a missing inline reference) refuse before any owner is called;
the others after the owners answered, all of them listed. `strict` travels in the query sent to an
owner that runs continued stages, and a strict request never reads a cached "not found".

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
under the alias; looked-up rows the array (`first`: the object or `null`); a `parentAs` alias
`{ "entity": "<entity id>", … }`; an `elements: "all"` alias an array. `totalCount` and
`totalCountCapped` appear only when a count was requested; `diagnostics` only when there are any.

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
construct only contract 2 has (a 2.1 member, `is`, `strict`, a continued stage, `caseSensitive`,
the object form of a sort entry, the number form of `includeTotalCount`) ends its message with "This request was read as contract 1 because it
carries no 'X-OxQL-Contract: 2' header." The codes are unchanged.

### Error codes

The closed list, 72 codes (2.1 added the twelve marked †):

| area | codes |
|---|---|
| entity | `UNKNOWN_ENTITY` |
| request | `UNKNOWN_REQUEST_MEMBER`† (a top-level member a request does not have) |
| path | `INVALID_PATH`, `UNKNOWN_PATH`, `NOT_STORED`, `NOT_FILTERABLE`, `NOT_SORTABLE`, `NOT_A_COLLECTION`, `UNWIND_ORDER`, `ALIAS_COLLISION`, `INVALID_ALIAS` |
| operand | `INVALID_OPERAND`, `UNKNOWN_ENUM_MEMBER`, `OPERAND_NOT_ARRAY`, `DECIMAL_TEXT_NOT_ORDERABLE` (an ordered comparison on a decimal stored as text), `UNBOUND_VARIABLE`, `INVALID_VARIABLE` |
| condition | `UNKNOWN_OPERATOR`, `EMPTY_LOGICAL_GROUP`, `OPTION_NOT_APPLICABLE` (also a stage option where it does not apply), `INVALID_REGEX`, `REGEX_TOO_LONG`, `ANY_NOT_APPLICABLE`, `UNKNOWN_VARIANT`† (`is`) |
| stage | `UNKNOWN_STAGE`, `UNKNOWN_STAGE_MEMBER`, `STAGE_AFTER_PAGE`, `MULTIPLE_PAGE_STAGES`, `MIXED_PROJECTION`, `GROUP_ON_COLLECTION`, `UNKNOWN_AGG_FUNCTION`, `INVALID_AGGREGATE_ARGUMENT`, `INVALID_DATE_TRUNC_UNIT`, `INVALID_TIMEZONE`, `INVALID_SORT_DIRECTION`, `FLATTEN_NOT_RECURSIVE`†, `LOOKUP_NOT_DECLARED`, `LOOKUP_ON_NOT_ENTITY`†, `NOT_CONTINUABLE`†, `UNION_CARDINALITY_MISMATCH`† (`byTarget`), `RESOLVE_NOT_DECLARED`, `RESOLVE_ON_COLLECTION`†, `RESOLVE_TARGET_NOT_DECLARED`†, `RESOLVE_PARENT_NOT_ITEM`†, `RESOLVE_NOT_FILTERABLE`, `RESOLVE_NOT_SORTABLE` |
| limits | `MAX_PIPELINE_STAGES_EXCEEDED`, `MAX_LOOKUP_STAGES_EXCEEDED`, `MAX_UNWIND_STAGES_EXCEEDED`, `MAX_RESOLVE_STAGES_EXCEEDED`, `MAX_CONTINUED_STAGES_EXCEEDED`†, `MAX_GROUP_FIELDS_EXCEEDED`, `MAX_PROJECTION_FIELDS_EXCEEDED`, `MAX_CONDITIONS_EXCEEDED`, `MAX_VARIABLES_EXCEEDED`, `INVALID_PAGE_LIMIT`, `PAGE_SIZE_EXCEEDED`, `LOOKUP_LIMIT_EXCEEDED`, `MAX_OFFSET_EXCEEDED`, `BATCH_TOO_LARGE`, `REQUEST_TOO_LARGE` (413) |
| cursor | `CURSOR_INVALID` |
| access | `ACCESS_DENIED` (403) |
| execution | `RESOLVE_UNAVAILABLE` (422), `RESOLVE_REFUSED` (422, wraps the owner's errors), `OWNER_NOT_CAPABLE`† (422), `SEMI_JOIN_TOO_LARGE` (422), `QUERY_TOO_EXPENSIVE` (422), `QUERY_TIMEOUT` (504), `INTERNAL_ERROR` (500), `PAGE_INCOMPLETE`† (422, strict only) |
| compat | `LEGACY_STAGE_UNSUPPORTED` (a v1 `lookup` or `resolve`, `strict`, or a contract 2 stage member under contract 1) |

Under `strict` (or a stage's `onMissing: "refuse"`) the data-loss diagnostics below are errors of a
422 refusal as well.

### Diagnostic codes

Never a refusal outside `strict`; carried in `diagnostics` with machine-readable `params` where
useful. 13 codes (2.1 added the five marked †):
`ENTITY_ID_RETIRED` (`params.currentId`), `TOTAL_COUNT_CAPPED` (`params.cap`),
`RESOLVE_TIMEOUT`, `RESOLVE_UNREACHABLE`, `RESOLVE_PARTIAL` (keys an owner was not asked for or did
not answer in full), `SORT_ON_ADDON`, `REGEX_UNANCHORED`, `DECIMAL_TEXT_EXCLUDED` (an ordered
comparison on a decimal covered the Decimal128 rows only), `RESOLVE_MISSING`† (references that
resolved to nothing, under `onMissing` `report` or `refuse`), `RESOLVE_AMBIGUOUS`† (a key more than
one record holds), `RESOLVE_TRUNCATED`† (an `elements: "all"` alias over `MaxLookupLimit`),
`LOOKUP_TRUNCATED`† (a lookup over its `limit`), `UNWIND_DEPTH_TRUNCATED`† (a `flatten` over
`MaxFlattenDepth`). Every one but the first two, `SORT_ON_ADDON`, `REGEX_UNANCHORED` and
`DECIMAL_TEXT_EXCLUDED` is data loss and refuses under `strict`. The HTTP status and a typical cause
of every code: [`oxql-operations.md`](oxql-operations.md#codes).

## Explain request

`POST /oxql/explain` takes the body of `POST /oxql/query`, or an envelope around it:

```jsonc
{
  "query": { "entityType": "…", "variables": { … }, "strict": true, "pipeline": [ … ] },   // page.cursor is ignored
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
  `plan`: the bound form, the emitted stages and every owner query. `indexes`: the index advisory,
  the only extra read explain makes.
- `remote`: `check` checks what an owner binds at that owner. `cached` is accepted and answered as
  `check` until owners' answers are served from the cache alone.
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
`page.includeTotalCount`: contract 1 keeps the boolean. Every 2.1 construct is contract 2 only:
`strict` and the new stage members are `LEGACY_STAGE_UNSUPPORTED`, `is` is `UNKNOWN_OPERATOR`, a
continued stage is `NOT_CONTINUABLE`, and unknown top-level members are ignored as before. Each
such refusal says the request was read as contract 1 for want of the header (see *Refusal*). Every
contract 1 request is logged under `OxQL.Compat`. When compatibility is switched off, every request
is contract 2.
