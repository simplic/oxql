# OxQL semantics (contract 2)

What a well-formed request means once it binds: which rows an operator matches when a value is
null, absent or empty, how each kind is matched against the representations storage may hold,
what folds case and accents, how rows are ordered and paged, how projections interact with
joins, sorts and cursors, and how `group` types its output. The grammar is in
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

**Joins.** A `lookup` with no matching child yields `[]`. A `resolve` yields `null` when the
reference member is `null` or absent, when the target does not exist, when it fails the stage's
`filter`, and, for a remote target, when the owner did not answer (with a `RESOLVE_TIMEOUT` or
`RESOLVE_UNREACHABLE` diagnostic).

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
- `POST /oxql/explain` shows the collation a request runs under, and its advisory says which
  fields an index serves.

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
  `Limits:MaxLookupLimit`, 100) in ascending order of the child's key; children beyond the limit
  are not returned and not reported.

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
  no later `match`, `unwind`, `group` or `resolve` reads and that a projection dropped is not run
  at all: a remote owner is not called for it. An alias a stage writes after the projection is
  in the row.
- A projection may name a member below a join alias (`"vehicle.matchCode": 1`); the join then
  stays before the projection and the alias carries the named members only.
- The sort fields, the entity key and the key a remote `resolve` or a late join reads survive
  every projection in storage and are dropped from the row when not asked for, so projecting
  them away never changes the rows, the cursor or the join.
- **Late joins.** A `lookup` or local `resolve` that no later `match`, `sort`, `unwind`, `group`
  or `resolve` reads, that no `group` follows, and whose alias every later `project` passes whole
  runs after the page is taken, on the page's rows alone. A projection that narrows the alias
  keeps the join where it was written; one that drops it drops the join (see above). The rows are the same either way; explain shows the
  order the server runs.
- The reserved key `$default` in an inclusion projection stands for the entity's key and display
  members, the pair a `resolve` without `select` keeps; it is `UNKNOWN_PATH` after a `group` or an
  `unwind`. A remote `resolve` without `select` sends it to the owner, so a remote default is the
  same pair.
- Every value in a projection that is not the number `0` includes the path; a nested object is
  read as dotted paths (`{ "address": { "city": 1 } }` is `address.city`).

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
