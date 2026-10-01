# OxQL semantics (contract 2)

What a well-formed request means once it binds: which rows an operator matches when a value is
null, absent or empty, how each kind is matched against the representations storage may hold,
what folds case and accents, how rows are ordered and paged, how projections interact with
joins, sorts and cursors, how `group` types its output, and, since 2.1, polymorphic values,
flattened trees, typed and item references, the outcome of every join, strict requests and chains
across services. The grammar is in
[`oxql-query-syntax.md`](oxql-query-syntax.md); status codes, limits and the operational
endpoints are in [`oxql-operations.md`](oxql-operations.md).

## Null, absent and empty

Storage distinguishes a member that holds `null` from a member that is absent from the
document. The operators do not, except `exists`:

| condition | matches `null` | matches absent | notes |
|---|---|---|---|
| `eq null` | yes | yes | the only form that finds both |
| `neq null` | no | no | present and not null |
| `eq <value>` | no | no | |
| `neq <value>` | yes | yes | every row that does not hold the value |
| `in [ … ]` | only when the list holds `null` | only when the list holds `null` | `in []` matches nothing |
| `nin [ … ]` | yes, unless the list holds `null` | yes, unless the list holds `null` | `nin []` matches everything |
| `gt gte lt lte` | no | no | a `null` operand is `INVALID_OPERAND` |
| `contains startsWith endsWith regex` | no | no | the operand must be a string |
| `exists: true` | yes | no | a stored `null` counts as present |
| `exists: false` | no | yes | |
| `not { … }` | complement | complement | compiled to `$nor`: the rows the inner condition does not match, `null` and absent included |

- The empty string is a value like any other: `eq ""` matches the empty string only. A blank
  test on a string is `or: [ { p: { eq: null } }, { p: { eq: "" } } ]`. An empty string on a
  member of another kind (`int`, `date`, `decimal`, …) is `INVALID_OPERAND`, so on those
  members `eq null` alone is the blank test.
- `eq null` on a member no document holds matches every row, and `exists: true` none. A
  misspelled member is refused (`UNKNOWN_PATH`); a declared member that nothing writes is not.
- Ordered comparisons stay inside the value's type: a numeric bound never matches text, a text
  bound never matches a number, and neither matches `null`.
- `exists` is accepted on every stored member, a collection, an object and an `unknown` member
  included; every other operator needs a scalar (`NOT_FILTERABLE`).

**Collections.** A path through a collection in `match` means "some element". On a collection
of scalars, `eq null` matches a row whose collection is absent, not one whose collection is
empty; `exists: true` matches both a populated and an empty collection. `any` needs an element
that satisfies the whole inner condition, so an absent, `null` or empty collection never
matches it and always matches `not { any }`. `unwind` drops a row whose collection is absent,
`null` or empty unless `preserveNull` is `true`, which keeps it once with the element absent
(or `null`).

**Joins.** A `lookup` with no matching child yields `[]` (`first`: `null`). A `resolve` yields
`null` when the reference member is `null` or absent, when the target does not exist, when it fails
the stage's `filter`, when no case of a typed reference matches, and, for a remote target, when
the owner did not answer (with a `RESOLVE_TIMEOUT` or `RESOLVE_UNREACHABLE` diagnostic). Which of
these a `null` is, and which of them lose data, is the join's outcome
([Outcomes and data loss](#outcomes-and-data-loss)).

**Rows.** A member stored as `null` is `null` in the row; a member absent from the document is
absent from the row object. A `double` that is `NaN` or infinite is `null`.

**Group and sort.** `null` and absent fall into one group, whose key is `null`, for a single key
and for every key of a composite one. They sort together, first in ascending and last in
descending order, and the keyset cursor walks through them without repeating or dropping a row.
`sum`, `avg`, `min` and `max` skip `null` and absent values; `count` counts rows.

## Storage representations per kind

The engine reads each member's storage representation from the MongoDB driver's serializer
registry (`[BsonRepresentation]`, custom serializers, class maps) and encodes every operand for
that representation. Where a fleet's data is not uniform, the options under
`OxQL:Representation` decide how far matching tolerates it.

| kind | storage it expects | operand | row |
|---|---|---|---|
| `string` | string | JSON string | string |
| `string` (a single character) | Int32 code point (the driver's default for `char`) | a one-character string; `eq neq in nin` match either case of the character unless `caseSensitive`, ordered comparisons order by code point; `contains`, `startsWith`, `endsWith` and `regex` are `INVALID_OPERAND`; an operand of another length binds and matches nothing | the character |
| `int` | Int32, Int64, Double, Decimal128 | integral JSON number or digit string | number |
| `long` | Int64, Int32, Double | integral JSON number or digit string, exact to 64 bits | string of digits |
| `double` | Double and the other numeric types | JSON number or numeric string | number; `NaN` and infinities `null` |
| `decimal` | Decimal128, or text written before a migration | JSON number or string | canonical plain text: no exponent, no trailing fractional zeros (`125000.00` reads back as `"125000"`) |
| `bool` | boolean | `true`/`false` or `"true"`/`"false"` | boolean |
| `guid` | binary subtype 4, or what the member declares (subtype 3, string) | GUID string | GUID string |
| `date` | a BSON date at midnight UTC, or a string when the member declares one | `YYYY-MM-DD` | `YYYY-MM-DD` of the stored UTC day |
| `dateTime` | a BSON date | ISO-8601 with `Z` or an offset; milliseconds optional | ISO-8601 UTC, trailing zero fractions dropped (`2026-06-15T23:30:00Z`) |
| `timeSpan` | string in .NET's `c` format (`01:30:00`), or Int64 ticks, or Double milliseconds when the member declares one | ISO-8601 duration (`PT1H30M`, `-PT5M`) | ISO-8601 duration |
| `enum` | Int32, Int64 or the member name as a string, as the member declares | member name or number | number; above the Int32 range a string of digits |
| `binary` | binary | base64 | base64 |

- **Numeric types are one bracket.** Int32, Int64, Double and Decimal128 compare with each other
  by value, so a `long` member written as Int32 on older rows matches and orders correctly.
  Text never compares with a number.
- **`long` above 2^53.** Contract 2 writes every `long` as a string, in rows, in `group` outputs
  (`count`, `countDistinct`, `sum` and `avg` over a `long`) and inside pushed arrays, so no
  digit is lost in a JavaScript caller. An operand may be a JSON number or a digit string and
  is read exactly either way; a JavaScript caller sends a string, since its own numbers are not
  exact above 2^53. Cursor legs carry the value in extended JSON and are exact. Contract 1 rows
  are rendered by the v1 converter, which writes an Int64 as a JSON number: a v1 client reads
  `9007199254740993` as `…992`. A member the model does not describe (an undefined addon key,
  an `unknown` member) is written as a number up to 2^53 − 1 and as a string above it.
- **Addon bag values** are written by their BSON type, like any member the model does not
  describe, except a key the organisation defined as `date`: it is written as `YYYY-MM-DD`, as
  every `date` is, so the value read back is a valid operand for the key. A retired or undefined
  key holding a BSON date is written as the ISO instant. Contract 1 rows keep the v1 converter's
  instant for every bag value.
- **`decimal`.** With `DecimalMode: tolerant` (the default) `eq`, `neq`, `in` and `nin` match
  both a Decimal128 and the text the driver wrote before the member was migrated, in any scale
  (`125000` matches `"125000"` and `"125000.00"`). Text does not order by value, so `gt`, `gte`,
  `lt` and `lte` compare the Decimal128 rows only and add a `DECIMAL_TEXT_EXCLUDED` diagnostic
  naming the path. A member whose declared representation is text has no numeric rows at all:
  equality matches the text, and an ordered comparison is refused with
  `DECIMAL_TEXT_NOT_ORDERABLE`. A sort over a mixed member puts every numeric row before every
  text row, and orders the text rows by character; `min` and `max` follow the same order.
  `DecimalMode: typed` matches Decimal128 only; a row still holding text is then silently not
  matched, so switch only after the data is migrated.
- **`guid`.** With `GuidTolerant: false` (the default) an operand is encoded as the member
  declares (standard subtype 4 unless the member says otherwise); a legacy subtype 3 value or a
  GUID stored as a string is not matched and nothing says so. `GuidTolerant: true` also matches
  subtype 3 and the string form. Rows read subtype 3 and 4 and strings either way.
- **`date` and `dateTime`** are the same BSON type in storage; only the model's kind tells them
  apart. A `date` operand is midnight UTC of the day; a `dateTime` operand without `Z` or an
  offset is refused, so a local time never binds against an instant. A `dateTime` member stored
  as a document (a `DateTimeOffset` with the document representation) is projectable, not
  filterable, and reads back with its offset.
- **`timeSpan`** stored as text orders by character. That agrees with duration order only while
  every value is non-negative and below one day; from a day on the text gains a day component
  (`1.02:00:00`), and a `gt` or a sort across that boundary, or across a negative value, does not.
- **Addon keys** (`addon.<path>` of a defined key) always match tolerantly, whatever the options:
  a date as a BSON date or an ISO string, a number or a numeric string, a boolean or
  `"true"`/`"false"`, a GUID as binary or string, and a decimal at `key` and at `key._v`. An
  ordered comparison on a defined `decimal` key covers the numeric values only, with the same
  `DECIMAL_TEXT_EXCLUDED` diagnostic.

**Unexpected storage.** A filter compares in the representation the member declares, so a value
of another BSON type (outside the numeric bracket) is not matched: it is neither refused nor
reported. A row renders such a value verbatim by its BSON type (a number in a `guid` member)
instead of failing the page. A value of the right type outside what the kind can hold (a
Decimal128 `NaN`, a date outside the calendar, a duration beyond .NET's `TimeSpan`) is rendered
verbatim too, and the service logs one warning per request naming the paths, never the values.
A sort orders mixed types by BSON type bracket; a sort on an addon key carries the
`SORT_ON_ADDON` diagnostic for that reason.

## Case and accents

Under contract 2 a string comparison, a sort on a string and a string group key fold case and
accents: `muller` matches `Müller` and `MULLER`. The engine runs such a request under the
collation `OxQL:Representation:Collation`, `{ locale: "de", strength: 1 }` unless configured
otherwise; a request that folds nothing runs without a collation, exactly as before.

**Why a collation, and why `de` at strength 1.** People type a search the way they say it,
without the case or the diacritics of the stored value. Primary strength is the one level that
folds both; strength 2 folds case only, 3 and above fold neither. A case-insensitive pattern
cannot do the same: its `i` flag folds case but not accents, it cannot express an ordered
comparison or a sort, and it cannot be bounded by an index, so it walks every key of the index
it uses. A collated comparison, sort and group key fold consistently in one aggregate and can be
served by an index built with the same collation. `de` is the default because German is the
primary language of the data the engine was built for; it orders umlauts with their base letter
(`ä` beside `a`). Any ICU locale can be configured.

| construct | under contract 2 |
|---|---|
| `eq neq in nin` on a string | folds case and accents; `options.caseSensitive: true` compares exactly |
| `gt gte lt lte` on a string | orders under the collation; cannot opt out (`OPTION_NOT_APPLICABLE`) |
| `startsWith` | a range under the collation, folds case and accents; `caseSensitive` compares exactly |
| `contains`, `endsWith` | a pattern with the `i` flag: folds case, not accents; `caseSensitive` compares exactly |
| `regex` | the caller's pattern as written, never folded; `caseSensitive` does not apply |
| sort on a string | orders under the collation; `{ "direction": …, "caseSensitive": true }` orders by byte value, only in a request where nothing else folds (`OPTION_NOT_APPLICABLE` otherwise) |
| group key on a string | folds: `Open` and `OPEN` are one group, reported under the first value met |
| `countDistinct`, `min`, `max` over a string | fold; `first`, `last` and `push` return values as stored |
| a single character (Int32 storage) | `eq neq in nin` match both cases of the character; never collated |
| a join key (`lookup`, local `resolve`) | compared byte for byte even inside a collated request; ids never fold |
| a condition under a remote alias | bound by the owner under its own default; `caseSensitive` or `ignoreCase` travel as written |
| `eq null`, `in []` | compare no text and add no collation |
| every other kind | compares by value; unaffected |

Inside a collated request an exact comparison (`caseSensitive: true`) of `eq`, `neq`, `in`, `nin`
or `startsWith` is compiled to an anchored pattern, which the collation does not reach; its text
must fit the server's 32 KB pattern limit once escaped (`INVALID_OPERAND` otherwise). A request
in which every string comparison, sort and key is exact runs without a collation, with plain
operators. Contract 1 knows no collation: it folds only where a condition says
`ignoreCase: true`, as a case-only pattern.

**Indexes.** The engine never reads or creates indexes; every form is correct on a collection
with no index but `_id`. What changes with the collation is which indexes the server can use:

- A string comparison, sort or group key under the collation is served only by an index built
  with the same collation (`{ locale: "de", strength: 1 }` by default). An ordinary index on a
  string member does not serve it; the server scans, or sorts in memory (spilling to disk while
  `Execution:AllowDiskUse` is true).
- The organisation scope, which is a GUID equality, and comparisons of other kinds are not
  affected by the collation.
- A request that folds nothing, including one where every string comparison says
  `caseSensitive: true`, runs without a collation and uses ordinary indexes.
- A service whose lists filter or sort on a string member at volume adds an index such as
  `{ organizationId: 1, <member>: 1, _id: 1 }` with the engine's collation. When the configured
  collation changes, those indexes are rebuilt with it.
- `POST /oxql/explain` shows the collation a request runs under; asked with
  `include: ["indexes"]`, its advisory says which fields an index serves.

## Sorting and paging

| shape at the page | caller wrote no sort | caller's sort | paging |
|---|---|---|---|
| the entity's rows (no `group`, no `unwind`) | ascending by the entity key (`id`) | the caller's fields, then `id` ascending as the tie-breaker | keyset cursor |
| after `unwind` | ascending by `id`, then by the position of every unwound element | the caller's fields, then `id`, then every unwind's position | offset behind the cursor |
| after `group` | the group keys, ascending, string keys folded | the caller's fields, then every group key it did not name, ascending | offset behind the cursor |
| `group` with no key | one row, no order | | |

- The default sort of a grouped or unwound shape is bound like a written one: it appears in
  explain and in the cursor's fingerprint.
- **After `group`, the sort is total.** The group keys are unique per group, so the engine
  appends every key the caller's sort does not name, ascending and in the group's key order:
  `[ { "count": "desc" } ]` pages as `[ { "count": "desc" }, { "status": "asc" } ]`, and offset
  paging over equal values neither repeats nor skips a group. A key the caller sorts on keeps
  its place and direction. A string key folded into its group under the collation, and the same
  collation orders it, so keys that differ only by case are one group, never two tied rows. The
  appended keys are bound like written ones: they appear in explain and in the cursor's
  fingerprint.
- A key that a `project` between the `group` and the `sort` leaves out cannot complete the
  order; keep every key up to the sort, or sort before projecting.
- An entity key that is a GUID orders by its binary value, not by creation time; a caller that
  wants a stable business order sorts by a member.
- The last `sort` of a pipeline decides the page order; an earlier one only orders what a
  following `group` sees (`first`, `last`, `push`). A sort before a `group` is dropped from
  paging.
- `null` and absent values come first in ascending and last in descending order. An enum sorts
  by its stored value (its number, or its name when stored as text); a `timeSpan` stored as text
  by character; a mixed member by BSON type bracket.
- A `lookup` returns at most `limit` children per parent (default and maximum
  `Limits:MaxLookupLimit`, 100) in the order of its `sort`, then the child's key (without `sort`,
  ascending by the child's key). Children beyond the limit are not returned, and the page carries
  `LOOKUP_TRUNCATED` naming the alias and how many rows were cut; under `strict` it refuses. A
  strict request also refuses a cut that a later match hides by filtering the cut parent out (a
  second read of the rows up to the lookup, `params.filtered: true`); the same holds for a
  `flatten` cut at its depth.
- A join loads what is read under its alias (see *What a join loads*): every member of its target can
  be filtered, sorted, grouped, projected or joined on, whatever the join's `select` names.
- `strict` and `onMissing` change what is refused, never what binds: an inline resolve with a
  `filter` stays inline, and the aggregate tells a record the filter excluded from a missing one. An
  inline resolve onto a target field that is not the key joins two records when its outcomes are
  read, so a key two records hold is `ambiguous` (the first by record key is taken).

**Cursors.** A cursor is signed with a key derived from `Cursor:SigningKey` and bound to a
fingerprint of the bound pipeline: the entity, the organisation, every stage with its storage
paths and typed operands, and the paging mode. The page stage is not part of it, so a follow-up
page may ask for another `limit` or for a count. A cursor from another pipeline, another
organisation, another signing key or a tampered one is `CURSOR_INVALID`; a cursor together with
an `offset` is `INVALID_PAGE_LIMIT`. Every replica of a service needs the same signing key, or a
page served by one replica cannot be continued on another.

- A keyset cursor carries the last row's value of every sort field and its `id`, typed, so a
  `long` above 2^53, a decimal or a GUID continue exactly. The engine keeps the sort fields and
  the key in storage through any projection so the cursor can read them; the row leaves out what
  the projection did not ask for.
- An offset cursor (after `group` or `unwind`) carries the next offset. `Limits:MaxOffset` bounds
  the `offset` a caller writes, not the one a cursor carries, so such a result can be walked past
  it.
- **Sorting on a local join alias.** A keyset cursor's condition is merged into the leading
  scope match, except when the sort names a path under a local `resolve` alias
  (`vehicle.matchCode`): the alias does not exist before the join, so the condition is evaluated
  after the join, right before the sort. Such a cursor continues the order like any other; the
  first stage no longer narrows the scan by the cursor. (A sort under a `lookup` alias is
  `NOT_SORTABLE`; one under a remote alias is `RESOLVE_NOT_SORTABLE`.)

**Counts.** `includeTotalCount` runs a second aggregate, concurrently with the page, over the
same stages up to the page, joins included only where a later `match`, `unwind`, `group` or
`resolve` reads their alias; sorts and joins only the rows' display reads are left out. It
stops at the cap in force (the host's `CountCap`, or the request's own below it). After a
`group` it counts groups.

## Projections

- A path a projection removed is `UNKNOWN_PATH` ("was removed by the projection") in every later
  stage. An inclusion keeps `id` unless it says `"id": 0`.
- **An inclusion projection hides every alias a later stage writes.** An alias is a new root the
  projection's list cannot name in advance: after `{ "project": { "items.orderNumber": 1 } }`, an
  `unwind` with `as: "item"` or `includeIndex: "at"` binds, but `item.orderNumber` and `at` are
  `UNKNOWN_PATH`. Project after the stage that writes the alias and name the alias in the
  projection.
- **A projection after a join decides whether the row carries its alias.** An inclusion that
  does not name the alias (nor a member below it), or an exclusion that names it, leaves the
  alias out of the row, for a `lookup`, a local and a remote `resolve` alike. A join whose alias
  no later `match`, `unwind`, `group`, `resolve` or `lookup` reads and that a projection dropped is
  not run at all: a remote owner is not called for it. A keyed or remote join counts as read while
  the row shows an alias a stage continued under it adds. An alias a stage writes after the projection is
  in the row.
- A projection may name a member below a join alias (`"vehicle.matchCode": 1`); the join then
  stays before the projection and the alias carries the named members only, without its key unless
  the projection names it. Under a keyed or remote alias the named members are what the owner is
  asked for. A projection that names only an alias continued under it is enough: the alias it
  continues under is fetched for the continued stage and is not in the row (see *What a join loads*).
- The sort fields, the entity key and the key a remote `resolve` or a late join reads survive
  every projection in storage and are dropped from the row when not asked for, so projecting
  them away never changes the rows, the cursor or the join.
- **Late joins.** A `lookup` or local `resolve` that no later `match`, `sort`, `unwind`, `group`
  or `resolve` reads, that no `group` follows, and whose alias every later `project` passes whole
  runs after the page is taken, on the page's rows alone. A projection that narrows the alias
  keeps the join where it was written; one that drops it drops the join (see above). The rows are the same either way; explain shows the
  order the server runs.
- The reserved key `$default` in an inclusion projection stands for the entity's key and display
  members, the pair a whole alias of a `resolve` without `select` shows; it is `UNKNOWN_PATH` after a
  `group` or an `unwind`. A remote `resolve` kept whole without `select` sends it to the owner, so a
  remote default is the same pair.
- Every value in a projection that is not the number `0` includes the path; a nested object is
  read as dotted paths (`{ "address": { "city": 1 } }` is `address.city`).

## What a join loads

Nobody tells a join what to load. Once every stage is bound, the engine knows every path the query
reads (each is a literal path; a variable only ever stands for a value) and infers per join:

**load set = its key + what later stages read under its alias + its output set.**

- **The output set** is what the row shows under the alias. Where the last projection names paths
  under the alias, it is exactly those paths. Where the row keeps the alias whole (no projection, or
  the projection names the alias itself), it is the `select` hint with the key, or without a hint
  the target's key and display members. An alias the row does not carry shows nothing.
- **What is only read is not shown.** A `match`, `sort`, `group` key or aggregate under the alias, the
  key of a later `resolve`, the member a `lookup` joins `on`, the sibling or variant that picks a
  reference's case: each is loaded for the stage that reads it and cut from the row. The row's shape
  depends on the projection alone, never on what the query happened to read.
- **A join in the aggregate** (`lookup`, an inline `resolve`) projects the load set inside its
  `$lookup`; an unwound copy of a lookup alias and an element of one of its collections load through
  the lookup. **A join after the page** (keyed or remote) is read by nothing but the projection, so
  its owner is asked for the output set: the paths, or nothing, which the owner query spells as the
  owner's own key and display (`$default`). The key the owner query carries to match the rows by is
  not shown unless the projection names it; an owning row (`parentAs`) always carries `entity`.
- **A join another join continues from is read by it.** A `resolve` or `lookup` continued under a
  keyed or remote alias (or its `parentAs`) reads that alias, as a keyed `resolve` reads the inline
  alias its key lies under, and so on up the chain. The projection need not name any of them: each
  runs while the row shows something that depends on it, loads what the next join reads, and is cut
  from the row. An alias kept alive this way shows nothing; its owner is asked for the key alone
  (no `$default`), runs the continued stages, and answers what the projection names under their
  aliases. The rows are those of the same query with the aliases projected, less those aliases.
  Explain lists the read at the continued stage (`resolveKey`, `lookupOn`), the path in the alias's
  `loads`, and an empty `shows`. Only when the row shows none of a keyed stage's aliases, nor any
  alias continued under them, is its owner not called.
- **`select` is a hint.** It shapes a whole alias and nothing else: it bounds no read, and under a
  projection that names paths below the alias it is neither shown nor loaded. A hint path the target
  does not have is `UNKNOWN_PATH`, since it is a typo and not a load.
- **At an owner.** A stage continued at an owner travels as written, without an inferred `select`;
  the owner binds it and infers its join the same way, the case member included. The paths the
  caller projects under a continued alias travel as paths, never as the alias itself.
- **Unions.** The paths under an alias with several targets are flat. Each target's owner is asked
  for them; a path a target lacks is dropped for it (`SELECT_PATH_NOT_ON_TARGET`; for a remote
  target after its owner said so, once, kept per target) and the member is absent on its rows. Only
  a path every target lacks is `UNKNOWN_PATH`, at the stage that names it.
- **Cost.** The paths asked of an owner count toward its `MaxProjectionFields`. Explain shows per
  alias what it `loads` and `shows`, and per stage what it reads.
- **Contract 1** is frozen: a join fetches its `select` as written (or key and display), nothing is
  inferred, and a path beyond the select has no value.

## Grouping

**`dateTrunc`.**

- `timezone` is an IANA zone id (`Europe/Berlin`); absent or blank is `UTC`. The service also
  accepts a Windows zone id and sends the IANA id it maps to; a zone neither resolves is
  `INVALID_TIMEZONE`. An IANA id must also map to a Windows zone, so the answer is the same
  on every host platform: `CET` and `Europe//Berlin` are refused on Windows and Linux alike.
- The boundary is computed in local time, so a bucket follows daylight saving: the Berlin day
  bucket of 29 March is `2026-03-28T23:00:00Z`, that of 25 October `2026-10-24T22:00:00Z`. The key
  is emitted as the UTC instant of the local boundary and is not a UTC midnight; compare it as an
  instant, never with a date literal.
- `weekStart` applies to `unit: "week"` only and defaults to `monday`; it accepts day names and
  three-letter abbreviations in any case (`INVALID_DATE_TRUNC_UNIT` otherwise). On another unit it
  is ignored.
- A `date` member has no time of day: it truncates on its own calendar day in the zone, so its
  bucket is local midnight of that day, in every zone. A `dateTime` truncates as the instant it
  holds.
- The path must be a `date` or `dateTime` outside any collection that is not unwound.

**Output types.** A group row carries its aliases only, each in the wire encoding of its kind:

| output | kind | on the wire |
|---|---|---|
| key over a path | the member's kind | as the member (`long` as a string, `date` as `YYYY-MM-DD`, enum as its number) |
| key over `dateTrunc` | `dateTime` | ISO-8601 UTC instant |
| `count`, `countDistinct` | `long` | string of digits (`"12"`) |
| `sum` | the argument's kind | `int` and `double` a number, `long` and `decimal` a string; arithmetic over integers and integer literals is a `long`, with a `double` operand a `double`, with a `decimal` operand a `decimal` |
| `avg` | `double` over `int`/`double`, `decimal` over `long`/`decimal` | a number, or a decimal string |
| `min`, `max`, `first`, `last` | the argument's kind | as the member; an object by its members |
| `push` | array of the argument's kind | each element in the member's encoding |
| `coalesce` | unknown | the stored value verbatim |

- An aggregate over a path under a collection that is not unwound is `GROUP_ON_COLLECTION`,
  except `push`, which pushes one array per row.
- After a `group` the shape is the aliases: a later `match`, `sort` or `group` addresses them by
  name, the members of an object returned by `first` or `last` are not addressable, and `lookup`
  and `unwind` have nothing to work on.

## Polymorphic values

A member typed as a base class or an interface may hold one of several variants: the driver
stores the variant's discriminator beside the value (`_t` by default) and the variant's own
members. The model reads the variants from the class maps registered with the driver (and
`[BsonKnownTypes]`), never from an assembly scan, and publishes one type for the member:

- Its members are the base type's own members followed by every member some variant has and the
  base has not, each marked with the variants that carry it (`onlyFor`) and nullable. Paths stay
  the paths of storage: `items.billingLineId`, not a variant-qualified form.
- A member the variants carry with a different kind or storage name becomes `unknown` (build
  finding `polymorphic-member-conflict`). A concrete subclass the scan finds but the driver does not
  know is `polymorphic-subtype-unregistered` and is not a variant.
- An interface-typed member whose implementations are registered is an object of the union of
  their members, all with `onlyFor`; before 2.1 it was `unknown`. A polymorphic entity keeps its
  declared class as the root, merged the same way, once its subclasses are registered.
- A row renders each value with the members of the variant it is stored as: the base members in
  order, then that variant's own. 2.0 rendered the base members only.

On a row of another variant a variant-only member is absent, so `eq null` and `neq <value>` match it
(explain notes `ONLY_FOR_VARIANTS`). `is` tests the variant itself: a name stands for that variant
and every registered descendant; a concrete base name for all of them and for values stored without
a discriminator. The discriminator is compared exactly.

## Flattened trees

`unwind` with `flatten` turns a tree of items of one kind (an invoice's group items holding items)
into one row per item at any level down to `MaxFlattenDepth`:

- Order is pre-order: a group, then its children, then the next sibling. `includeIndex` numbers the
  rows of one document in that order.
- Each row carries the item without its nested collection; the members of its variant are there as
  usual, so a group row and a billing-line row can be told apart with `is`.
- Items below the depth are not in the rows; the page names the path, the depth and how many rows
  lost items (`UNWIND_DEPTH_TRUNCATED`). Under `strict` the request refuses instead.
- `preserveNull` keeps a document with no items once, as for a plain unwind. The rows page by
  offset.

## References

A join follows a reference the model declares; nothing is inferred from names. 2.0 had one form, a
member naming one entity by its key or another field (`[OxQLReference("<entity>", field?)]`, or the
base package's `[ReferenceId]`). 2.1 adds four:

| form | declaration | what the key names |
|---|---|---|
| item target | `[OxQLReference("transport.shipment", "id", Item = "billingLines")]` | an element of the keyed collection `billingLines` of some shipment; the alias holds the element, `parentAs` the shipment |
| key conversion | `[OxQLReference("transport.shipment", "id", KeyAs = OxQLKeyAs.Guid)]` on a string member | the guid the string holds |
| typed reference | one `[OxQLReferenceWhen("type", "logistics", "transport.shipment#billingLines", "transport.tour#billingLines", Field = "id")]` per case | a target chosen by the stored value of a sibling member; several targets in one case are tried in order |
| typed by variant | `[OxQLReferenceWhen(OxQLReferenceWhenAttribute.Variant, "DriverResource", "staff.employee", Field = "id")]` | a target chosen by the variant of the object holding the member |

- A target is `entity` or `entity#itemPath`; the field is the attribute's second argument or `Field`.
  Every `[OxQLReferenceWhen]` of one member with the same `path` is one case of one reference;
  combining them with `[OxQLReference]` on the same member, or with conflicting cases, drops them
  all (`reference-declaration-unresolved`). The condition's values compare exactly with the stored
  string or enum sibling.
- A member the service cannot annotate (inherited, or on a shared type) is declared by the host:
  `new ReferenceDeclarations().For<Resource>("id").When(ReferenceDeclarations.Variant, "DriverResource", "staff.employee", field: "id")`,
  or `.To(target, field?, item?, keyAs?)` for an unconditional one, passed to
  `ClrModelBuilder.Build(assemblies, retiredIds, references)`. It applies to that type and its
  variants wherever they are embedded, and to no other type that inherits the same CLR member.
- **Key conversion.** The stored string is parsed as a guid in any .NET format and sent in lower-case
  `D` form; a value that does not parse is outcome `invalid_key`. A string member referencing a local
  guid key without `KeyAs` is the build finding `reference-key-kind-mismatch`.
- **Item targets.** The element whose field equals the key is the alias; `parentAs` is the owning row
  as `{ "entity": "<entity id>", <members> }`: its key and display members, or the paths the
  projection names under it. Two owning rows holding the same element
  id are `ambiguous`: the first by target order, then by key, is taken.
- The schema publishes a simple reference (one unconditional case, one entity target, no item, no
  conversion) under `references` as before; the other forms only as reference cases, so a reader
  that maps every `references` entry to an entity join never meets one it cannot follow.

`target` narrows a typed reference to one target entity; rows whose case selects another target are
`excluded`. `elements` resolves a reference inside a collection that is not unwound; without it such
a path is refused (`RESOLVE_ON_COLLECTION`), where 2.0 joined an arbitrary element.

## Outcomes and data loss

Every slot of a join (a row, or an element under `elements`) ends in one outcome:

| outcome | when | alias | data loss |
|---|---|---|---|
| `resolved` | the key selects a case and a target record or element exists (and passes `filter`) | the target | no |
| `ambiguous` | as resolved, but an item key or a non-key field matched more than one record | the first by target order, then key | **yes** |
| `reference_null` | the reference is `null` or absent; an earlier hop was `null`; `elements` met no keyed element | `null` | no |
| `excluded` | no case matches the stored value (a tariff reference among shipment references), `target` narrowed the case away, or the target fails `filter` | `null` | no |
| `not_applicable` | a `forTarget` stage on a row resolved to another target; a union join (`byTarget`) on a row resolved to a target it has no branch for | `null` | no |
| `not_found` | the key selects a case and target and no record exists in any target of the case | `null` | **yes** |
| `invalid_key` | a key conversion case whose value does not parse | `null` | **yes** |
| `owner_unanswered` | the owner timed out, was unreachable, cut its answer, or the key was over the key budget | `null` | **yes** |
| lookup with no children | | `[]` / `null` | no |
| lookup over its limit | | the first `limit` | **yes** (`LOOKUP_TRUNCATED`) |
| `elements: "all"` over `MaxLookupLimit` | | the first targets | **yes** (`RESOLVE_TRUNCATED`) |

- A filtered-out target reads as `not_found` unless the stage reads its outcomes (an `onMissing`
  other than `null`, or an `outcomeAs`): then the keyed fetch asks the owner once more for the keys
  the filtered answer lacked, without the filter, and a key present there is `excluded`. An inline
  resolve with a `filter` does the same in the aggregate, with a second join without the filter.
- `elements: "first"` takes the first `resolved` element; failing that the row is `not_found` or
  `invalid_key` if any element was, else `excluded` if any was, else `reference_null`.
- `onMissing` decides what `not_found`, `invalid_key` and `owner_unanswered` do. `null` (the default
  outside `strict`): nothing but an owner failure's own `RESOLVE_TIMEOUT` / `RESOLVE_UNREACHABLE`
  (which name the aliases, not rows). `report`: one `RESOLVE_MISSING` per stage listing the rows.
  `refuse` (the default under `strict`): that diagnostic becomes a 422. Outside `strict`, `refuse`
  refuses on `RESOLVE_MISSING` only; under `strict`, `report` keeps `RESOLVE_MISSING` a diagnostic
  while every other loss still refuses.
- `ambiguous` is reported (`RESOLVE_AMBIGUOUS`) whatever `onMissing` says, and refuses under `strict`.
- Rows an owner reported are mapped back through the key to every row of the page holding it. An
  inline resolve reads its missing references off the page's rows, and only when its effective
  `onMissing` is not `null`.
- **The outcome on the row.** The diagnostics name rows by their index on the page and list at most
  `MaxReportedRows` of them; a report that prints "missing" per line needs the outcome where the line
  is. `outcomeAs` on a resolve names a member that carries it on every row, `resolved` included
  ([`oxql-query-syntax.md`](oxql-query-syntax.md#stage-resolve)). It is opt-in per join and named by
  the caller, so no row gains a member nobody asked for. It is the same outcome the policy above
  reads, written where the join runs: by the aggregate for an inline resolve (which is why such an
  outcome can be filtered, grouped by and counted before the page), after the page for a keyed or
  remote one, by the owner for a stage continued there and lifted to the origin row with its alias,
  and by the origin itself for the rows no owner ran the stage for (`not_applicable`,
  `reference_null`). It never reports or refuses; it only makes a stage read its outcomes.

**Where ambiguity is seen.** The keyed fetch asks an owner for at most two records per key whenever
the target is an item or a non-key field, so a second record is always seen. Two plain forms keep
2.0's single-record query and cannot always see it: a simple local reference onto a non-key field
runs inline and takes the first match; a simple remote reference onto a non-key field sends the
plain key match, so the second record is seen only when the owner's page happens to hold both, and
not when the key is answered from the cache. Declare such a reference onto the target's key, or
expect `ambiguous` to be reported only by the keyed forms. Both plain forms look for the second
record once the request is `strict`, the stage's `onMissing` is not `null` or the stage names its
outcome (`outcomeAs`): the outcome is then read, so it is told.

## Strict requests

`strict: true` says: these rows go into a document, so answer them all or refuse. Every data-loss
outcome and truncation above refuses with 422, carrying the diagnostic it would have answered as its
error; the plain `null` of `reference_null`, `excluded` and `not_applicable` does not. A report page
(no `cursor`, no `offset`) may hold up to `MaxReportPageSize` rows and refuses with
`PAGE_INCOMPLETE` when more match. The request's `strict` is carried in the query sent to an owner
that runs continued stages, and a strict request reads no cached "not found". Without `strict`
nothing about the rows changes: `strict` and `onMissing` refuse or report, they never alter a row,
and neither enters the cursor fingerprint.

## Chains across services

A report often needs a record behind a record another service returned: the invoice line's source
billing line lives in a shipment of the transport service, and the delivery attempt that shipment
had last lives there too. The service answering the query cannot take that second step itself; it
has neither the shipment data nor its model. So it puts the step **into the query it already sends
the transport service** for the keys. That query becomes *match the keys → the continued steps →
the projection*, and the owner answers with rows that already carry the second step's result.

Nothing else changes compared with a plain remote resolve:

- The same batch, over the same internal route (`internal/oxql/batch` in the Simplic base package),
  under the same forwarded user and organisation. The owner applies the organisation scope at every
  entity it enters, exactly as for a direct query; a continued step reads only what that user could
  read with `POST /oxql/query` at the owner. No header is added.
- The owner binds the continued steps with its own model, and infers what their joins load as for
  any query of its own (see *What a join loads*). If a step reaches a third service, the owner does
  the same from its side; a local target of the owner is fetched in process.
- The aliases the continued steps add are lifted back from each owner row to the origin row, so the
  caller sees one row with every hop. An owner's refusal of a step comes back as `RESOLVE_REFUSED`
  at the caller's stage, with the owner's errors mapped to the caller's `stage` and `path` and
  `params.owner` naming the owner's own view; an owner's diagnostics come back mapped the same way.
- **It ends by construction.** A forwarded query carries only the steps continued under the alias it
  answers; the resolve that created the alias and every stage before it stay at the origin. Each
  forwarded query therefore has strictly fewer join stages than the one that produced it, and a
  chain ends after at most as many levels as the original request has join stages. No hop counter is
  needed.
- **Time.** The batch carries `maxTimeMs`, which the owner applies to the whole batch and budgets
  its own owner calls from. A call carrying continued stages, or a typed, item, converted or
  element-wise resolve, gets the origin's remaining time less 50 ms, at most `Execution:ChainTimeoutMs` (6 000); a plain remote
  resolve keeps `min(remaining, ResolveTimeoutMs)`; the batch's `maxTimeMs` is that less a tenth (at
  most 250 ms), and split batches of one owner share one deadline. An owner that runs out answers
  nothing for its keys: `RESOLVE_TIMEOUT`, rows `owner_unanswered`. An owner checking its own
  continued stages at explain is given what is left of its origin's explain (time and owner calls) and
  asks its own owners only within it, so a slow second owner comes back unchecked (`REMOTE_UNCHECKED`,
  or `EXPLAIN_LIMIT` when the explain's own time ran out) and one explain never causes more than
  `Explain:MaxOwnerCalls` owner calls in all.
- **Cost.** Owner queries per level are bounded by stages × key chunks, and keys per level by the
  rows the previous level returned; the remaining limits are `MaxResolveKeys` (per request),
  `MaxContinuedStages`, the owner's `MaxBatchQueries` and the time ceiling.
- **Unions.** On an alias with several targets (shipment or tour billing lines), a continued step
  without `forTarget` goes to every target and must bind on each; with `forTarget` it goes only to
  that target's owner query, and on rows resolved to another target its alias is `null`
  (`not_applicable`).
- **The union join.** A `resolve` with `byTarget` is one continued stage with a branch per target and
  one alias. The origin splits it: each target's owner query carries that target's branch as an
  ordinary resolve under the alias, exactly what a `forTarget` stage sends, and a target without a
  branch is not sent the stage. So it costs what the stages it replaces cost and less of the budget:
  no call beyond the one owner query per target and key chunk, one stage toward
  `MaxContinuedStages`. The origin lifts the alias from whichever target's row answered. What a
  branch reaches is its owner's to say: the alias's type is the union of what the branches reach, a
  path under it that one branch does not reach is dropped for that branch and asked again (the
  drop-and-ask-again of a union target, at most twice, kept per target and branch for the cache's
  lifetime), and only a path no branch reaches refuses. A union join under an alias a continued stage
  added is sent to that alias's owner as written, which splits it the same way.
- **Owner version.** Continued stages and grouped owner queries need the owner on 2.1. An owner whose
  health reports an older engine is refused before anything is sent (`OWNER_NOT_CAPABLE`); an owner
  not yet probed is sent the query, and an old one refuses the unknown members inside
  `RESOLVE_REFUSED`.

Aggregation over chain data (`unwind`, `group` under a chain alias) is refused (`NOT_CONTINUABLE`);
it belongs in the report that reads the rows.

**Variables in chains.** An owner never receives `variables`. The origin binds every `{ "$var": … }`
inside a continued stage and inside the `filter` of every remote resolve and writes the value into
the owner query; an unbound one is `UNBOUND_VARIABLE` at the origin. 2.0 forwarded a remote filter
with its `$var` and no variables, so the owner refused it. Because the cache key is taken over the
substituted query, two requests with different variables never share a cached answer.
