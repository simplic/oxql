# OxQL semantics (contract 2)

What a well-formed request means once it binds: which rows an operator matches when a value is
null, absent or empty, how each kind is matched against the representations storage may hold,
what folds case and accents, how rows are ordered and paged, how projections interact with
joins, sorts and cursors, how `group` types its output, polymorphic values,
flattened trees, typed and item references, the outcome of every join, strict requests and chains
across services. The grammar is in
[`oxql-query-syntax.md`](oxql-query-syntax.md); status codes, limits and the operational
endpoints are in [`oxql-operations.md`](oxql-operations.md).

Where a rule below says what contract 1 does, it speaks of the compatibility rewrite: a request
without the contract header on a host that answers contract 1 through the contract 2 engine
(`Compat:Legacy:Always=false`). By default such a request never reaches this engine: the legacy
pipeline answers it with the semantics of the engine before contract 2, which
[`LEGACY.md`](LEGACY.md) describes.

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
  member of another kind (`int`, `date`, `decimal`, `guid`, `bool`, …) is `INVALID_OPERAND`
  (`UNKNOWN_ENUM_MEMBER` on an enum), so on those
  members `eq null` alone is the blank test.
- `eq null` on a member no document holds matches every row, and `exists: true` none. A
  misspelled member is refused (`UNKNOWN_PATH`); a declared member that nothing writes is not.
- Ordered comparisons stay inside the value's type: a numeric bound never matches text, a text
  bound never matches a number, and neither matches `null`.
- `exists` is accepted on every stored member, a collection, an object and an `unknown` member
  included; every other operator but `is` needs a scalar (`NOT_FILTERABLE`).

**Collections.** A path through a collection in `match` means "some element". `eq null` (and
`in [null]`) on such a path matches a row in which some element holds no value, and a row whose
collection is absent or `null`, not one whose collection is empty; `exists: false` on a member of
the element matches a row without any element (the collection absent, `null` or empty). On a
collection itself `exists: true` matches both a populated and an empty collection. `any` needs an element
that satisfies the whole inner condition, so an absent, `null` or empty collection never
matches it and always matches `not { any }`. `unwind` drops a row whose collection is absent,
`null` or empty unless `preserveNull` is `true`, which keeps it once with the element absent
(or `null`).

**Joins.** A `lookup` with no matching child yields `[]` (`first`: `null`; a remote `lookup` whose
owner did not answer holds `null`, with the owner's diagnostic). A `resolve` yields
`null` when the reference member is `null` or absent, when the target does not exist, when it fails
the stage's `filter`, when no case of a typed reference matches, and, for a remote target, when
the owner did not answer (with a `RESOLVE_TIMEOUT` or `RESOLVE_UNREACHABLE` diagnostic). Which of
these a `null` is, and which of them lose data, is the join's outcome
([Outcomes and data loss](#outcomes-and-data-loss)).

**Rows.** A member stored as `null` is `null` in the row; a member absent from the document is
absent from the row object. A stored double that is `NaN` or infinite is `null`, under a `double`
member and under an `int`, `long` or `decimal` member alike (there it is also logged among the
values that do not fit their kind).

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
| `string` (a single character) | Int32 code point (the driver's default for `char`) | a one-character string; `eq neq in nin` match either case of the character unless `caseSensitive`, ordered comparisons order by code point; `contains`, `startsWith`, `endsWith` and `regex` are `INVALID_OPERAND`; an operand that is not exactly one UTF-16 code unit is `INVALID_OPERAND` | the character |
| `int` | Int32, Int64, Double, Decimal128 | integral JSON number or digit string | number |
| `long` | Int64, Int32, Double | integral JSON number or digit string, exact to 64 bits | JSON number, every digit |
| `double` | Double and the other numeric types | JSON number or numeric string | number; `NaN` and infinities `null` |
| `decimal` | Decimal128, or text written before a migration | JSON number, or text of digits with an optional leading sign, one decimal point and an exponent (no group separator, no white space, no trailing sign; `INVALID_OPERAND` otherwise) | JSON number in canonical digits: no exponent, no trailing fractional zeros, every digit (`125000.00` reads back as `125000`) |
| `bool` | boolean | `true`/`false` or `"true"`/`"false"` | boolean |
| `guid` | binary subtype 4, or what the member declares (subtype 3, string) | GUID string | GUID string |
| `date` | a BSON date at midnight UTC, or a string when the member declares one | `YYYY-MM-DD` | `YYYY-MM-DD` of the stored UTC day |
| `dateTime` | a BSON date | ISO-8601 with `Z` or an offset; milliseconds optional | ISO-8601 UTC, trailing zero fractions dropped (`2026-06-15T23:30:00Z`) |
| `timeSpan` | string in .NET's `c` format (`01:30:00`), or Int64 ticks, or Double milliseconds when the member declares one | ISO-8601 duration (`PT1H30M`, `-PT5M`) | ISO-8601 duration |
| `enum` | Int32, Int64 or the member name as a string, as the member declares | member name or number | number, also where a long backs the enum |
| `binary` | binary | base64 | base64 |

- **Numeric types are one bracket.** Int32, Int64, Double and Decimal128 compare with each other
  by value, so a `long` member written as Int32 on older rows matches and orders correctly.
  Text never compares with a number.
- **`long` and `decimal` are JSON numbers, exact on the wire.** Contract 2 writes a `long`, a
  `decimal` and an enum backed by a long as a JSON number wherever it writes a value: in rows, in
  `group` keys and outputs (`sum` and `avg`, a `min`, `max`, `first` or `last`), inside pushed
  arrays and in the rows of `values`. It is what the services' REST API writes for the same
  members. Every digit is in the body: `9007199254740993` is written `9007199254740993`, and a
  decimal with twenty-five places is written with twenty-five. A decimal is written in canonical
  digits (no exponent, no trailing fractional zeros: the scale a value was stored with is not
  kept, `125000.00` and `125000` are one number), also where the row stores it as text. A sum or
  a mean the database holds with more digits than the runtime's decimal (it computes in 34
  significant digits) is written from the database's own digits, all of them.
  **A reader that parses a JSON number into an IEEE double keeps 15 to 17 significant digits of
  it**: a JavaScript `JSON.parse` reads `9007199254740993` as `…992` and `0.1234567890123456789`
  as `0.12345678901234568`. That is the reader's rounding, as it is with REST; a caller that
  needs every digit reads the number's text (a big-number parser, a reviver over the source
  text). There is no option to get text instead.
  What is no number stays text: a Decimal128 `NaN` or infinity, one whose magnitude no decimal
  holds (`1E+6000`), and text stored under a `decimal` member that spells no number (`"n/a"`).
  The row is answered with that text where the number would be, and the host logs once per
  request which paths held such values, by path and never by value (`returned … stored values
  verbatim because they do not fit their kind`); nothing is refused and no diagnostic is added.
  **A count is an `int`**: `count` and `countDistinct` travel as a JSON number, as
  `pageInfo.count.value` and every count in a diagnostic do. An operand may be a JSON number or
  its digits as text and is read exactly either way; a JavaScript caller that holds a value above
  2^53 as text sends the text. Cursor legs carry the value in extended JSON and are exact; the
  key of a reference is compared as text, the digits of a number. Contract 1 rows are rendered by the
  v1 converter (a number with the stored scale, `60.00`). A member the model does not
  describe (an undefined addon key, an `unknown` member) is written by its stored type: an Int64
  as a number of any size, a Decimal128 as a number in canonical digits.
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
  `"true"`/`"false"`, a GUID as binary or string, and a decimal at `key` and at `key._v` (the bag's serializer writes
  a decimal as `{ _t: "System.Decimal", _v: <value> }`). A row never shows that wrapper: a value
  the driver wrote as `{ _t, _v }` reads back as its `_v`, a decimal as the number every
  `decimal` is (`261.8`), whether the key is defined or not, also below a wrapped dictionary
  or list. `sum`, `avg`, `min` and `max` over a defined `decimal` key read the wrapped value, so
  they aggregate what the rows show. A sort by such a key orders the wrapped values among
  themselves by number; rows that hold the plain number and rows that hold the wrapper are two
  runs (BSON orders by type first), which only matters where one key was written both ways. An
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
cannot do the same by itself: its `i` flag folds case but not accents (the engine writes the
accents into the pattern of `contains` and `endsWith`; see the table), it cannot express an ordered
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
| `contains`, `endsWith` | a pattern with the `i` flag in which each letter of the operand is the class of the letters of its primary weight, and a letter the collation weighs as two or three stands for them and they for it (`ß`/`ss`, `æ`/`ae`, `œ`/`oe`, `ĳ`/`ij`, the Latin ligatures): the classes are read from the collation data over the Latin blocks (Latin-1 Supplement to Latin Extended-B, Latin Extended Additional, the ligatures), and a test asks the server's `eq` the same pairs, so `contains` finds a spelling exactly when `eq` calls it equal; folds case and accents as the collation does at strength 1 (at strength 2 and above the collation tells accents apart, and so does the pattern); `caseSensitive` compares exactly. Never index-served: it reads every candidate row |
| `regex` | the caller's pattern as written, never folded; `caseSensitive` does not apply |
| sort on a string | orders under the collation; `{ "direction": …, "caseSensitive": true }` orders by byte value, only in a request where nothing else folds (`OPTION_NOT_APPLICABLE` otherwise) |
| group key on a string | folds: `Open` and `OPEN` are one group, reported under the first value met |
| `countDistinct`, `min`, `max` over a string | fold; `first`, `last` and `push` return values as stored |
| a single character (Int32 storage) | `eq neq in nin` match both cases of the character; never collated |
| a join key (`lookup`, local `resolve`, the ids of a semi-join over a reference stored as text) | compared byte for byte even inside a collated request; ids never fold |
| a condition under a remote alias | bound by the owner under its own default; `caseSensitive` travels as written |
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
  `include: ["indexes"]`, its advisory gives the index that serves the request as a definition to
  copy: collection, key document in the order equality, sort, range (the organisation first), the
  collation where a text key needs one, what of the request it serves, and whether the collection
  has it (`oxql-operations.md`, *The index advisory*). The engine creates none of them.

## Sorting and paging

| shape at the page | caller wrote no sort | caller's sort | paging |
|---|---|---|---|
| the entity's rows (no `group`, no `unwind`) | ascending by the entity key (`id`) | the caller's fields, then `id` ascending as the tie-breaker | cursor: the last row's sort values and key |
| after `unwind` | ascending by `id`, then by the position of every unwound element | the caller's fields, then `id`, then every unwind's position | cursor: a position in that order |
| after `group` | the group keys, ascending, string keys folded | the caller's fields, then every group key it did not name, ascending | cursor: a position in that order |
| `group` with no key | one row, no order | | |

- The default sort of a grouped or unwound shape is bound like a written one: it appears in
  explain and in the cursor's fingerprint.
- **After `group`, the sort is total.** The group keys are unique per group, so the engine
  appends every key the caller's sort does not name, ascending and in the group's key order:
  `[ { "count": "desc" } ]` pages as `[ { "count": "desc" }, { "status": "asc" } ]`, and
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
  `LOOKUP_TRUNCATED` naming the alias, the limit and how many rows of the page are affected; under
  `strict` it refuses. A strict request also refuses a cut that a later match hides by filtering
  the cut parent out: a second read of the rows up to the lookup, the same rows the page reads,
  those a semi-join selects included, answers 422 `LOOKUP_TRUNCATED` with `params.filtered: true`;
  a `flatten` cut at its depth answers `UNWIND_DEPTH_TRUNCATED` the same way.
- A join loads what is read under its alias (see *What a join loads*). Under the alias of a join
  in the aggregate (a `lookup`, an inline `resolve`) every member of the target can be filtered,
  grouped, projected or joined on, and under an inline `resolve` sorted on; under a keyed or
  remote alias a member can be projected or continued from, and under a plain remote one filtered
  as a semi-join. A contract 2 join has no `select`.
- **Where a condition of the request's `filter` runs.** The binder places each condition at the
  earliest boundary between two stages at which every path it reads is in the row, as a `match`
  of its own there: an entity member at the entry, an unwind's alias after the unwind, a join's
  alias after the join, an aggregate after its group. An unwind takes its collection out of the
  row, so a path never means two things: `items.quantity` is "some element" and exists until the
  unwind, `item.quantity` is "this element" and exists after it. The top-level conjuncts of a
  `where` are placed one by one, an `or`, `not` or `any` as a whole. The placed conditions are
  ordinary stages of the bound pipeline: they are in the cursor fingerprint, in the count
  pipeline and in explain's plan, and a filter written as `match` stages at those points is the
  same bound pipeline.
- `strict` and `onMissing` change what is refused, never what binds: an inline resolve with a
  `filter` stays inline, and the aggregate tells a record the filter excluded from a missing one. An
  inline resolve onto a target field that is not the key joins two records when its outcomes are
  read, so a key two records hold is `ambiguous` (the first by record key is taken).

**Cursors.** A page is addressed by its cursor and by nothing else (contract 1 keeps `offset`).
A cursor is signed with a key derived from `Cursor:SigningKey` and bound to a fingerprint of what
decides which rows there are and in which order:

| inside the fingerprint | outside it |
|---|---|
| the entity and the organisation | the page stage: `limit`, `count`, the cursor itself |
| the request's `filter` and every `match`, with storage paths and typed operands | the trailing `project`: every projection that only sorts, display-only joins and the page follow |
| every `unwind` and `group`, and the sort (the default and the appended keys included) | a `lookup` or `resolve` whose alias, owning row and outcome no stage reads but a projection, with its `filter`, `limit`, `sort` and `outcomeAs` |
| every join a condition, a sort, an unwind, a group or another join reads, with its `filter` | what a join loads; `strict`; the `onMissing` of a resolve this host runs (a stage continued at an owner is fingerprinted as it is sent, its `onMissing` included) |
| a projection a later `match`, `unwind`, `group` or read join follows; a stage continued at an owner | |

So a follow-up page may ask for another `limit` or for a count, and showing or hiding a column,
with the join that feeds it, keeps the cursor: the same walk goes on. A cursor of another filter,
another sort, another organisation, another signing key or a tampered one is `CURSOR_INVALID`.
Every replica of a service needs the same signing key, or a page served by one replica cannot be
continued on another. Contract 1 binds a cursor to the whole bound pipeline, its projection
included, as it always has; a cursor of one contract is never one of the other.

- On the entity's rows the cursor carries the last row's value of every sort field and its `id`,
  typed, so a `long` above 2^53, a decimal or a GUID continue exactly. The engine keeps the sort
  fields and the key in storage through any projection so the cursor can read them; the row
  leaves out what the projection did not ask for.
- After `unwind` the cursor carries the last row's sort values, its `id` and the position of the
  element in each unwound collection; after `group` the last group's sort values, which end in
  the group's keys. The next page starts after them by value, at any depth, and a row or group
  written or deleted before the position between two pages moves nothing. Where the order of the
  groups is not total (a `project` dropped a key before the sort, the only sort stands before the
  group, or one group is all there is) the cursor counts rows
  instead, and a row inserted or deleted before the position shifts the page; the cursor is opaque
  either way. Contract 1 counts rows after `group` and `unwind`.
- A page of groups ordered by a `sum` or an `avg` of floating-point values (kind `double`)
  continues from a value the database computes anew for the next page, in an order of addition
  that is its own, so the last group's value may come out a digit off the one its cursor carries
  (or moved, when a row of that group was written in between) and then lie after its own
  position. The group the position names is therefore also left out by its keys: it was served
  and is never answered again. What cannot be held is another group whose value moves across
  the position between two pages: it is answered twice or not at all, as any row whose sort value
  is rewritten between two pages is. A `count`, a `min`/`max` and a sum of `int`, `long` or
  `decimal` values are exact however often they are computed.
- A cursor's position is sent to the database as the null-aware predicate over every field of the
  order and, where the leading sort field holds a value, as one range on that field beside it
  (`k >= v` ascending; "not above `v`" descending, which keeps the nulls that follow): the rows
  before the position fail one comparison, and an index on the sort is bounded by one range.
- A contract 2 cursor carries the number of rows the pages up to it served. It is what a later
  page's count is known by without counting (below).
- **Sorting on a local join alias.** A keyset cursor's condition is merged into the leading
  scope match, except when the sort names a path under a local `resolve` alias
  (`vehicle.matchCode`): the alias does not exist before the join, so the condition is evaluated
  after the join, right before the sort. Such a cursor continues the order like any other; the
  first stage no longer narrows the scan by the cursor. (A sort under a `lookup` alias is
  `NOT_SORTABLE`; one under a keyed or remote alias, or on a join's outcome, is
  `RESOLVE_NOT_SORTABLE`.)

**Counts.** Every contract 2 answer says what is known under `pageInfo.count`: `exact`, a proven
lower bound (`atLeast`) with why counting stopped there, or `unknown` with why.

- Without counting: a page without a next one is the end of the list, so the rows served so far
  (the cursor's number plus this page's) are the exact count; any other page proves the rows
  served plus one (`stoppedBy: "page"`).
- `count: true` beside a page sends one more aggregate at the same moment as the page: the same
  stages up to the page, joins included only where a later `match`, `unwind`, `group`, `values`,
  `resolve` or `lookup` (`on`) reads their alias or the outcome the join names, sorts and display-only joins left out, then `$limit bound + 1`,
  `$count`. The bound is `Count:StepRows` (1 000) under the request's `upTo` and the host's
  `CountCap`. The step has its own time limit (`Count:BudgetMs`, or the request's `budgetMs`) and
  its own cancellation: the page never waits for it longer than that limit, counted from when
  both were sent, and its timeout or failure is `state: "unknown"`, never a refusal. A page that
  ends the list does not wait for it at all and stops it at once; the database ends a stopped
  command when it next looks, within about half a second, so only a long count is cut short. When
  the request ends, however it ends, the step is stopped. A step that found more than its bound
  answers `atLeast` the bound, `stoppedBy: "step"`: neither the cap nor the time stopped it, and
  the count alone goes further (`stoppedBy: "cap"` when the bound is the request's `upTo` or the
  host's `CountCap`).
- Beside a page of groups the count is not sent with the page: counting the groups computes every
  group, which is what the page does, and a page of groups that is not full is its own exact
  count. The count is sent once the page has come back full, under the same limits.
- `limit: 0` with `count` runs no page. A first step of `StepRows` rows proves a lower bound
  whatever follows and is cheap where matches are dense. The next step is sized to what the time
  left is predicted to reach, from what the first took (a scan is linear in what it reads): for a
  count the database answers quickly that is the ceiling at once, two commands in all. A step
  that finds no more than its bound is exact. A further step is sent only when ten times the last
  one is predicted to fit; otherwise the last bound is the answer (`stoppedBy: "budget"`). A step
  that runs out of time leaves the bound the step before it proved (`stoppedBy: "time"`), or
  `unknown` when none had.
- After `unwind` a count counts element rows, after `group` groups.
- **A step over unwound rows also bounds the parents** (contract 2, one `unwind`, where nothing
  between the unwind and the page reshapes, groups or joins the rows and what filters after the
  unwind can be asked before it): before the unwind it asks what the cut of the parents asks (see
  *Where the engine places a stage*), which keeps exactly the parents that yield a row the count
  counts, then `$limit bound + 1`. As many parents as the step is bounded by rows hold at least
  that many rows, so the answer of the step is the same, and the database stops reading at that
  many parents. Without it a database before 8.0 fills a buffer of rows narrowed to the collection
  before the first is unwound, and that buffer holds most of a collection: the "cheap first step"
  then read nearly everything, and the count alone stopped at `atLeast 1000`, `stoppedBy:
  "budget"` for lists whose exact count took a tenth of the budget. The step beside a page and the
  first step of the count alone keep the bound; a later step, sized to read as much as the time
  allows, leaves it out, and with it the filter where that only asks whether a parent yields a
  row. Measured on the volume below (count alone, default budget, the ladder's own commands):
  MongoDB 7.0, no condition 122 ms in three commands → 73 ms in two; `neq` of the frequent value 91
  → 78 ms; `eq` of the frequent value 145 → 86 ms; two conditions `atLeast 10000` → `exact`; MongoDB
  8.3, which stops by itself: unchanged without a condition and with a positive one, 55 → 71 ms for
  `neq` of the frequent value (what asking exactly costs, see there).
- A count is as expensive as the rows it has to find: cheap where matches are dense, a scan of
  the organisation's rows where they are sparse. That is why the count beside a page is one
  bounded step, and why a list asks for the count alone, in parallel, when it wants more.

Contract 1 and an internal call keep `includeTotalCount`: a second aggregate the answer waits for,
stopping at the cap in force (the host's `CountCap`, or for an internal call the request's own
below it), answered as
`totalCount` and `totalCountCapped`.

## Projections

- A path a projection removed is `UNKNOWN_PATH` ("was removed by the projection") in every later
  stage. An inclusion keeps `id` unless it says `"id": 0`. Projections narrow one another: an
  inclusion after an inclusion keeps what both name, an exclusion takes its paths out of an
  earlier inclusion, and what an exclusion removed stays removed (`params.reason: "projected"`).
- **An inclusion projection hides every alias a later stage writes.** An alias is a new root the
  projection's list cannot name in advance: after `{ "project": { "items.orderNumber": 1 } }`, an
  `unwind` with `as: "item"` or `includeIndex: "at"` binds, but `item.orderNumber` and `at` are
  `UNKNOWN_PATH`. Project after the stage that writes the alias and name the alias in the
  projection.
- **A projection after a join decides whether the row carries its alias.** An inclusion that
  does not name the alias (nor a member below it), or an exclusion that names it, leaves the
  alias out of the row, for a `lookup`, a local and a remote `resolve` alike. A join whose alias
  (and outcome, where it names one) no later `match`, `sort`, `unwind`, `group`, `values`,
  `resolve` or `lookup` reads and that a projection dropped is
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
- **Where the engine places a stage (contract 2).** The rows and their order are what the stages
  as written answer; where a stage runs is the engine's, and explain says it (`plan`, and a note at
  the stage). Contract 1 pipelines are emitted in the order written.
  - *A projection runs after the page is taken* (`PROJECT_AFTER_PAGE`), wherever it was written,
    when every stage after it only filters, orders or cuts the rows or joins after the page. The
    database then orders whole documents and cuts the page's rows to their columns, instead of
    cutting every candidate row first (measured: 42 to 55 % of a sorted block without an index).
    A filter or sort written after a projection reads only what it kept, which holds the same
    values before it. It stays where it was written before an `unwind`, a `group` or a join that
    runs before the page, and for a page above 1 000 rows (the database would hold that many whole
    documents in its sort memory).
  - *The parents are ordered and cut before their unwind* (`SORT_BEFORE_UNWIND`) when the page's
    order is the parents' order (every sort field before the key is a member of the entity, the
    default order included) and every row kept yields a row of the page. The unwind and the page's
    own sort then see a page's worth of rows: `limit + 1` of them, one more after a cursor (the
    row the position lies in may have none left). Each parent yields a row because the rows whose
    unwind answers none are dropped where the unwind would drop them, and what filters after the
    unwind is asked before it as well. **What an unwind answers is asked exactly, whatever the
    member holds**: a row per element of an array, a null element included; one row for a value that
    is no array (a single object or a scalar where the array should be), the value itself as the
    element, at no position; none for an empty array, a null and a member that is not stored. So a
    parent whose array holds a null beside its elements, or nothing but nulls, is kept, and the
    rows, the count and a walk by cursor are those of the stages as written
    (`UnwindIrregularCollectionTests`, on a server; MongoDB 6, 7 and 8).
    Whether a parent yields a row is asked as a plain condition, never an expression (`$expr`):
    "an array that holds anything, or a value that is no array, null or undefined", written
    `$nor [ $and [ { c: { $not: { $elemMatch: { $exists: true } } } }, { c: { $not: { $type: [every type but array, null, undefined] } } } ] ]`.
    It is written as "neither fails" for what it costs: the database asks the conditions of a
    filter in an order of its own, by their kind, and each condition on the collection walks the
    stored row to the member again. Written this way it asks the organisation first, and of a row
    with elements one condition on the collection, as the list did before the cut was made exact;
    a row without elements is asked the second. Measured (table below): what the cut cost as an
    expression is gone.
    The position of the element (`includeIndex`) after the parents' members and before the key
    (`shipmentNumber, position`, or `position` alone) does not prevent it. Within one parent that
    is the order its elements have anyway; the rows of two parents that tie on the members
    interleave by it (all their rows at one position before any at the next), so the parents are
    ordered by the members, then by the first position each still has a row at, then by the key:
    position 0 ascending and the last element descending on a first page, and after a cursor's
    position the position's own for a tying parent whose key comes later, else the next one (a
    parent with no row left is dropped). The first `n` rows of the page then lie in the first `n`
    parents. The one row of a value that is no array stands at no position, which orders before
    position 0: such a parent's row precedes every row at position 0 of the parents it ties with.
    Whether a parent beyond the cut holds such a value is known only by reading it, so the first
    position is computed for every candidate parent on every page, the first ascending one
    included, and no index orders this cut (the parents are still cut before the unwind, which is
    what the measured gain below comes from). It needs every parent's rows to stand at positions
    without a gap, so it
    is not applied with `preserveNull`, with `flatten`, with a condition on the element, or when
    something other than the key follows the position; the rows are then ordered after the unwind
    as before. Measured on 25 000 shipments without an index: `shipmentNumber, position` 121 → 68 ms
    for a first block (what `shipmentNumber` alone takes) and 173 → 158 ms for block 31 (there the
    first position of every candidate parent is computed before they are ordered).
  - *What is asked of the element after an unwind is asked of the collection before it*
    (`UNWIND_PREFILTER`): the conditions on paths under the alias, so a row none of whose unwound
    rows passes them is not unwound, for the page and for the count; and where the page's order is
    the parents', the parents are cut first for every such condition (`SORT_BEFORE_UNWIND`), a
    `neq` or `nin` included. It keeps **exactly** the rows that yield a row after the unwind and
    its conditions, whatever the member holds. The rows of an unwind are of three kinds, and each
    is asked in its own way:
    - *an element that is an object*: "some element passes them all" (`$elemMatch`, the conditions
      read relative to the element);
    - *an element without members* (a null, an undefined or a scalar in the array): after the
      unwind every path under the alias is absent, so it passes or fails the conditions whatever
      it is. It passes `neq`, `nin`, `exists: false`, `eq null` and the `not` of a comparison; it
      fails a comparison with a value, a pattern, `exists: true`, `is` and `any`. Where it passes,
      "the array holds such an element" is asked (`$elemMatch { $type: [every type but object and
      array] }`); where it fails, nothing; where the conditions do not say by themselves (a set
      that holds null), the conditions are
      also read at a member no row holds (`__oxAbsent`), and the database answers what an absent
      member passes;
    - *a value that is no array* (one object, or a scalar, where the array should be): the one
      row its unwind answers is the value, so the conditions as written, read at the member, of a
      member that is no array, no null, no undefined and stored.

    **One comparison of one member that no absent element passes** (`eq`, `gt` …, `in`, a pattern,
    `exists: true`, `is` of named variants) needs none of the three: read through the collection
    (`"Items.Status.Name": "x"`) it holds exactly when an object in the array, or the one object
    in its place, meets it, and an index on that path bounds it (held on a server:
    `IndexAdviceServerTests`). Two comparisons of one member (a range, a folded `startsWith`) are
    not asked that way: two elements could meet one bound each.

    Otherwise the three are the branches of one `$or`:
    `{ $or: [ { c: { $elemMatch: … } }, { c: { $elemMatch: { $type: […] } } }, { $nor: [ { c: { $gte: [] } }, { c: { $in: [null] } }, { $nor: [ …on c ] }, { c: { $type: 6 } } ] } ] }`.
    The last is "nothing rules out that the member is one value that passes": it is an array (only
    an array is at least the empty one), it is null or not stored, a condition fails, it is
    undefined. None of it is an expression. The kinds are chosen for the order the database asks
    them in: `$elemMatch` first, which answers alone for a row an element of which passes. A row
    of the usual form that fails it is asked one more plain condition per further branch (does the
    array hold an element without members; is the member an array). **That is the price of a
    condition an absent element passes**, over the list before the unwind was made exact, which
    asked `$elemMatch` alone and lost the rows of the other two kinds: no single plain condition
    tells an array of objects from both an array that also holds a null and one object in the
    array's place (a type test sees an object in both; an element test sees no value that is no
    array), so two more are asked of the rows that fail, and none of the rows that pass.

    Not before an unwind with `preserveNull` (the row it keeps has no element to pass anything) or
    `flatten`.

    **Not exact: an element that is itself an array, under a condition on the element.** After the
    unwind a condition on a path under the alias reads through that inner array (it holds when an
    object in it meets the condition); before the unwind no plain condition does, so such a row
    can be missing from the list, or a parent kept that yields none. Without a condition on the
    element its rows are exact (`UnwindIrregularCollectionTests`). A collection of collections is
    not what a member of an entity is typed as.

    Measured on 25 000 shipments of about 7 KB without an index (three in four with one to three
    lines, the rest an empty array, a null or no member; first block of 200 rows ordered by the
    parent, the plans of three builds run alternately on one connection, median of 25, ms;
    `UnwindCutBench`), MongoDB 8.3 (6.0 and 7.0 are 2 to 9 ms slower in every column, in the same
    proportions):

    | request | before the unwind was exact | exact by an expression and without the cut | exact by plain conditions |
    |---|---|---|---|
    | no condition on the element | 40.1 | 54.6 | 40.9 |
    | `eq` a value most lines hold | 41.5 | 58.8 | 42.8 |
    | `eq` a value few lines hold | 34.1 | 46.5 | 35.6 |
    | two conditions on one element | 42.3 | 66.1 | 49.3 |
    | `neq` a value few lines hold (most rows pass) | 42.4 | 73.4 | 47.7 |
    | `neq` / `nin` a value most lines hold (most rows fail) | 37.5 | 65.5 | 55.3 |
    | ordered by `number, position` | 41.4 | 79.8 | 70.3 |

    The first three are what they were. A condition an absent element passes costs the rows that
    fail it two more conditions; the list is still faster than without the cut, and its count is
    asked of the parents with a passing row alone again. The position before the key still
    computes each parent's first position (above).

    Where the parents are **not** cut first (the order is the elements', or a condition after the
    unwind cannot be asked before it), the filter only spares the unwind and asks less of the rows
    that fail: `{ $or: [ { c: { $elemMatch: … } }, { c: { $not: { $elemMatch: { $exists: true } } } }, { c: { $type: [every type but object and array] } } ] }`
    (the last branch only where an absent element may pass). It drops an array of objects none of
    which passes; what holds no array of elements it leaves to the unwind, which drops it or
    answers the one value for the conditions after it.
  - *The element is written under its alias after the page is taken*: until then filters and the
    paging sort read it where the unwind left it. The three unwind placements apply to a request
    with one `unwind`; with several, the stages run as written and the page is still by value.
  - *A join through a collection keeps one key per element on the page*, not the elements: the
    keyed fetch reads each element's reference (and what selects its cases); the rest of the
    collection is fetched only when the projection asks for it.
- **Late joins.** A `lookup` or local `resolve` that no later `match`, `sort`, `unwind`, `group`
  or `resolve` reads (nor a `lookup` joined `on` it that is itself read, nor a stage reading the
  outcome it names), that no `group` or `values` follows, and whose alias every later `project` passes on
  runs after the page is taken, on the page's rows alone. A projection passes the alias on when it
  keeps it whole (`"vehicle": 1`, or an exclusion that names nothing under it) or, under contract 2,
  names paths under it (`"vehicle.matchCode": 1`): the join then loads exactly those paths with its
  key and the row shows them alone, so choosing what an alias shows never moves its join before the
  page. An exclusion that names a path under the alias keeps the join where it was written (and so
  does, under contract 1, an inclusion that narrows the alias); a projection that drops the alias
  drops the join (see above). The rows are the same either way; explain shows the order the server
  runs (`JOIN_BEFORE_PAGE`, `JOIN_AFTER_PAGE`).
- **A join is never moved to where it runs more often than where it was written.** After the page
  it runs once per row of the page, which is no more than it would have read where it stood as
  long as nothing in between yields more rows than it reads. An `unwind` does: the page then holds
  one row per element. So under contract 2 a join only the display reads that is written **before
  an `unwind`** stays before it and runs once per parent, never once per element: on the parents
  ordered and cut before the unwind where the page is ordered by them (`SORT_BEFORE_UNWIND`; a
  page's worth of parents, `JOIN_BEFORE_PAGE` with `params.rows: "parents"`), else where it was
  written, once per row that reaches it (`params.rows: "written"`; `params.unwind` names the
  collection). Written **after** the unwind it was asked once per element, and runs after the
  page. Measured on 25 000 shipments without an index, one record with five elements and a lookup
  of its latest child (a scan of 20 000 rows each time): 66 ms after the page, 9 ms before the
  unwind. A join before the unwind of a list ordered by the element runs for every parent the
  filters leave, as written: where that is many and the page is small, write the join after the
  unwind. Contract 1 keeps its rule (after the page whenever only the display reads the alias).
- The reserved key `$default` in an inclusion projection stands for the entity's key and display
  members, the pair a whole alias of a `resolve` shows; it is `UNKNOWN_PATH` after a
  `group` or an `unwind`. A remote `resolve` kept whole sends it to the owner, so a
  remote default is the same pair.
- A projection keeps a path with `1` or `true` and removes it with `0` or `false` (any other
  number keeps it); a nested object is read as dotted paths (`{ "address": { "city": 1 } }` is
  `address.city`). A value that is neither a number nor a boolean (a string, `null`, an array) is
  `MIXED_PROJECTION`, as are an inclusion mixed with an exclusion and an empty projection. `id`
  stands aside from that rule: `"id": 0` may stand in an inclusion, and `"id": 1` beside removed
  paths is the inclusion of `id` alone.

## What a join loads

Nobody tells a join what to load. Once every stage is bound, the engine knows every path the query
reads (each is a literal path; a variable only ever stands for a value) and infers per join:

**load set = its key + what later stages read under its alias + its output set.**

- **The output set** is what the row shows under the alias. Where the last projection names paths
  under the alias, it is exactly those paths (the key only when it is named). Where the row keeps
  the alias whole (no projection, or the projection names the alias itself), it is the default
  output: for an entity target the entity's key and its display member (whichever of the two the
  entity declares), for an item target of a `resolve` the member the reference matches, for the
  elements a remote `lookup` of `entity#item` finds the whole element, and for an owning row
  (`parentAs`) `entity` with the owner's key and display member. An alias of another service's rows
  kept whole shows what its owner answers to `$default`, the same pair read from the owner's model
  (explain says `loads: null`, `shows: null` there). An alias the row does not carry shows nothing.
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
- **There is no `select`.** A contract 2 `lookup` or `resolve` (a union join included) that writes
  one is refused: `UNKNOWN_STAGE_MEMBER`, `params { stage, member: "select", reason: "removed",
  use: "project" }`. To show other members of a join than its default output, name them in a
  `project` stage (`"vehicle.matchCode": 1`); a path the target does not have is `UNKNOWN_PATH` at
  that projection, also under a remote `lookup`'s alias. A member the target has but does not
  store is `NOT_STORED` at that projection, under a keyed alias as on the entity itself; an
  exclusion that names one is accepted.
- **At an owner.** An owner query carries what the origin needs of an alias as its own `project`
  stage (the output set, or `$default`), never as a `select`; a stage continued at an owner travels
  as the caller wrote it, and the owner binds it and infers its join the same way, the case member
  included. So the internal wire has no `select` either, and an internal call that writes one is
  refused like a public one. The paths the caller projects under a continued alias travel as paths,
  never as the alias itself.
- **Unions.** The paths under an alias with several targets are flat. Each target's owner is asked
  for them; a path a target lacks is dropped for it (`SELECT_PATH_NOT_ON_TARGET`; for a remote
  target after its owner said so, once, kept per target) and the member is absent on its rows. Only
  a path every target lacks is refused: `UNKNOWN_PATH` at the projection where every target is
  local; where a remote owner has to say it, a run answers 422 `RESOLVE_REFUSED` at the join's
  stage, carrying `UNKNOWN_PATH` for the path.
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
- **A key is always a date.** A bucket starts at a local boundary, so the bucket of a row at the
  very start of the calendar can start before it: the instant `0001-01-01T00:00:00Z` (what a .NET
  service stores for a date that was never set) lies in the year before in every zone east of
  Greenwich, in the month before west of it, and in the week before when weeks start on another day
  than Monday. That bucket is keyed `0001-01-01T00:00:00Z`, the first instant a date on the wire
  can name; a bucket past the end of year 9999 is keyed with the last instant. The key is clamped
  in the group itself: the rows of every bucket outside the calendar are one group at each end, the
  keys of a level are distinct, and a sort and a cursor over the groups read the key the row shows.
  The range for such a group's rows starts at its key, as for every other.
- **`day` and coarser buckets are calendar ranges in the zone.** A `day`, `week`, `month`,
  `quarter` or `year` bucket holds the instants from the start of its first day in the zone to the
  start of the first day of the next bucket, where the start of a day is the first instant from
  which the zone's clock shows that day. So a day is 23, 24 or 25 hours long where the clock
  changes, and never a fixed length from its key. Where a zone skips a midnight, the day starts
  when the clock next shows a time. Where a zone shows a midnight twice (a change of the clock at
  midnight, `America/Havana` in autumn), the bucket starts at the first of the two and **its key
  is the second**, one hour into the bucket: there, and only there, a key is not its bucket's
  first instant.
- **Sub-day buckets are elapsed time.** `hour`, `minute` and `second` are counted in whole units
  of elapsed time from midnight of 2000-01-01 in the zone, not read off the clock: a bucket is the
  unit that follows its key. In a zone whose offset changes by whole hours an hour bucket is an
  hour on the clock, but the hour a clock shows twice in autumn is two buckets, with two keys. In a
  zone at `:30` or `:45` the hours start at `:30` or `:45` UTC. In a zone that changes its clock by
  half an hour (`Australia/Lord_Howe`) the hours start on the clock's hour for one part of the year
  and at half past for the other.
- **The rows of a bucket are asked for with a range.** There is no condition that names a bucket
  (`dateTrunc` is a group key, not an operator: in a condition it is `UNKNOWN_OPERATOR`). A caller
  sends the half-open range on the member, `{ "<path>": { "gte": <start>, "lt": <end> } }`, and
  computes its two ends itself:
  - `day` and coarser: the calendar bounds of the bucket in the zone, each at the start of its day
    (the first of two midnights). That range answers exactly the rows the group counted, in every
    zone. Compute it from the calendar day the key lies in, not by adding a length to the key.
  - below a day: from the key to the key plus one unit of elapsed time.
  - a `date` member (a day, no time): the bucket's days, written as days:
    `{ "gte": "2026-03-01", "lt": "2026-04-01" }`.
  - the rows without a date (the group keyed `null`, the member null or absent):
    `{ "<path>": { "eq": null } }`.

  A range on the stored date is bounded by an index that leads to the member.

**Output types.** A group row carries its aliases only, each in the wire encoding of its kind:

| output | kind | on the wire |
|---|---|---|
| key over a path | the member's kind | as the member (`long` and `decimal` as a number, `date` as `YYYY-MM-DD`, enum as its number) |
| key over `dateTrunc` | `dateTime` | ISO-8601 UTC instant |
| `count`, `countDistinct` | `int` | a JSON number (`12`); a `sum`, `min` or `max` over such an alias in a later `group` is an `int` and a number too |
| `sum` | the argument's kind | a number: `int` and `double` as they are, `long` and `decimal` with every digit (a `decimal` sums in Decimal128 and reads back exactly; a `double` sums in binary floating point, so `0.1 + 0.2` is `0.30000000000000004`: the engine rounds nothing, the member's kind says which it is); arithmetic over integers and integer literals is a `long`, with a `double` operand a `double`, with a `decimal` operand a `decimal` |
| `avg` | `double` over `int`/`double`, `decimal` over `long`/`decimal` | a number; a decimal mean with every digit the database holds (up to 34) |
| `min`, `max`, `first`, `last` | the argument's kind | as the member; an object by its members |
| `push` | array of the argument's kind | each element in the member's encoding |
| `coalesce` | unknown | the stored value verbatim |

- An aggregate over a path under a collection that is not unwound is `GROUP_ON_COLLECTION`,
  except `push`, which pushes one array per row.
- After a `group` the shape is the aliases: a later `match`, `sort` or `group` addresses them by
  name, the members of an object returned by `first` or `last` are not addressable, and `lookup`
  and `unwind` have nothing to work on.
- **A `resolve` after a `group`** follows the reference of the member a key was taken over (a key
  over such a key in a later group keeps it). The group's row then holds the record under the
  resolve's alias, null where the key is null or names no record; the groups, their counts and
  their order are those of the request without the stage.
  - *Where it runs.* A target of this service is one `$lookup` in the page's aggregate: after the
    page is taken when only the display reads the alias (at most `limit` keys, `JOIN_AFTER_PAGE`),
    before it when a `sort` or a `match` after the resolve reads the record (every group, then
    ordered or cut). It adds no command. A target of another service is one owner query for the
    keys of the page's rows, kept for `Cache:ResolveTtlSeconds` like any keyed fetch: one command
    more for a block the first time, none while the answer is kept.
  - *Conditions and order.* A condition on a member of a local record is a condition on the
    groups; on another service's record it is a semi-join on the key (`key in <owner's ids>`),
    with the limits of every semi-join. A sort by a member of a local record orders the groups
    and is completed by the group's keys, so the page still continues by value; another
    service's record orders nothing (`RESOLVE_NOT_SORTABLE`).
  - *What it cannot follow.* A reference that picks its target by a sibling member or by the
    variant of the object that held it needs the row the group replaced: resolve it before the
    group and group by a member of the record instead. Contract 1 has no such stage.

**Groups for a grid.** The engine has no tree of groups; a level of a tree is a request, and the
requests of a tree add up because each is the same rows cut another way:

| the grid shows | it sends | the engine guarantees |
|---|---|---|
| a level: the groups of one key | the leaf pipeline without `project`, `sort`, `page`, then `group` by that key with a `count` and one aggregate per totalled column, `sort`, `page` | the groups page by value in any order that a `sort` after the group gives, an aggregate included (the order is completed by the key); `page.count` counts groups |
| the rows of one group | the leaf pipeline with one more `filter` entry: `{ "<path>": { "eq": <key> } }` for a key over a path, `{ "<path>": { "gte": <start>, "lt": <end> } }` with the bucket's bounds for a `dateTrunc` key (see *`dateTrunc`* above) | the entry runs before everything that does not need it; the rows are the rows the group counted; `eq null` is the group keyed `null` (the member null or absent), for a `dateTrunc` key too |
| a level under a group | the same `group` request with the parents' keys as `filter` entries | the counts of the level sum to the parent's count |
| the totals | the leaf pipeline with `group` over no key, and `maxTimeMs` | one row; its `count` is the number of rows the filter leaves; over no row, no row |

- **A key over a path is sent back as it was answered.** A string key folds case and accents, and
  so does `eq`: the value the group reported (the first met) finds every spelling in it. An enum
  key is its number, a guid its text, a boolean itself. A `dateTrunc` key is an instant that names
  a bucket; its rows are a range the caller computes, never `eq` on the key.
- **A stored enum number the enum does not declare** is a group of its own (the stored value is
  the key), and no condition can name it: an operand off the enum is `UNKNOWN_ENUM_MEMBER`, here
  as everywhere. Such a group's rows are reached through its other members only.
- **A filter entry is about the thing its path names from the entry on.** A key under an alias
  that is also a member of the entity (`{ path: "status.name", as: "status" }` where `status` is an
  object) leaves `status` in a filter entry meaning the member, not the key: the entry runs at
  the entry of the pipeline, on the member (`NOT_FILTERABLE` for an object; explain notes
  `FILTER_BINDS_EARLIER` where it binds, and the run's answer says the same in `diagnostics[]`).
  The same holds for an aggregate named like a member it reads (`"weight": { "sum": "weight" }`):
  a filter entry on `weight` filters the rows that feed the groups. A key that a condition should reach after the group
  has an alias no member has; a key over the member of the same name is the same thing either way.
- **`countDistinct`** counts the distinct values the rows store: a stored `null` is one of them,
  a member that is absent is none.

## Distinct values

A request that ends in `values` (see [`oxql-query-syntax.md`](oxql-query-syntax.md#stage-values))
answers the distinct values of one path among the rows that reach the stage. It is the group one
would write by hand, and tests hold the two equal on the same rows, values, counts and order:

```jsonc
{ "values": { "path": "status.name", "counts": true } }
// = { "group": { "by": [ { "path": "status.name", "as": "value" } ], "fields": { "count": { "count": true } } } },
//   { "sort": [ { "count": "desc" }, { "value": "asc" } ] }, { "page": { … } }
```

What the stage adds to that group:

- **It reads through collections.** For `items.status.name` it unwinds `items` (and, for
  `items.weightNotes.number`, both collections) for the values alone. A value is then one that
  **some element** holds, which is what a condition on the same path matches
  (`{ "items.status.name": { "in": […] } }` is "some item has one of these"), and `count` counts
  elements, not rows. After an `unwind` of the collection the member is a scalar of the row
  (`item.status.name`): a row is an element, and a row without elements is no row.
- **`null` under a collection is what a comparison with `null` selects.** `{ "items.status.name":
  { "in": [null] } }` (and `eq null`) selects a row in which some element holds no name, and a row
  whose collection is not there at all (the member absent or `null`); it does not select a row
  whose collection is there and empty, since no element is there to hold anything. The list
  follows the condition: `null` counts the elements without a value and, once each, the rows
  without the collection; a row with an empty collection is counted under no value (it is still
  one of the rows `scan` bounds). So every entry of the list, `null` included, sent back as `in`
  finds the rows it was counted from. The rows without any element are a question of their own,
  asked with `exists: false` on a member every element has (`{ "items.id": { "exists": false } }`:
  the collection absent, `null` or empty), or with `not` around an `any`.
- **It bounds the rows it reads** (`scan`, at most `Values:ScanRows`, 50 000): the bound stands
  before the unwinds and the group, so the cost of a list never grows with what the request
  matches. Reaching it is said (`VALUES_SCAN_CAPPED`): the list is then "the values among the
  first *n* rows". Which rows those are is the database's choice (by the index it reads them
  through; with an index on the path, the lowest values), so a capped list is a sample, never a
  ranking, and a `count` in it is a count within the sample. A filter that leaves fewer rows than
  the bound gives the whole list.
- **`search` narrows the values, not the rows**: the rows read are counted whatever the search
  keeps, so "not found among the first 50 000 rows" and "no such value" are told apart by the
  diagnostic. It is a prefix under the collation, the comparison `startsWith` makes: `mul` finds
  `Müller`, `MÜLLER` and `muller`, which are one value.
- **It has no cursor.** `hasNextPage` says the list is cut at `limit`; the next step is a longer
  prefix.

**Null, folding, kinds.** As for a group key: null and absent are one value (`null`), the empty
string another; a string's spellings fold into one value under the collation, reported under the
first one met (which spelling that is depends on the order the rows are read in); an enum is its
stored number, one the enum does not declare included. A value sent back as `eq` or in `in` finds
the rows the list counted, as a group's key does, under a collection too (`null` included, see
above), with the one exception grouping has too: an undeclared enum number cannot be named in a
condition (`UNKNOWN_ENUM_MEMBER`).

**The request's `filter`.** Every entry runs before the stage, at the point its paths are in the
row; none reads `value` or `count`, which exist only in the answer. A set filter asks for its list
with **every entry but its own**: the entry `col:status` is left out of the request that lists the
statuses. Sent with it, the list would hold only what is already chosen. The engine offers no
`except` for this on purpose: a request says what it runs, and explain, cursors and the count of the
grid's other requests stay those of the entries actually sent.

**What it costs, and the index that answers it.** One aggregate, one round trip, whatever the list:

| the rows are read | when | examined |
|---|---|---|
| from an index alone | an index holds the organisation member and the path (and whatever the conditions before the stage read), **built without a collation** | `scan` keys at most, no document |
| as documents | no such index; an index built with a collation (it holds sort keys, and the database then reads the document whatever the path holds); a condition on a member the index lacks; a path under a collection (an index over an array's members never stands for the documents) | `scan` documents at most |

`{ OrganizationId: 1, <storage path>: 1 }` is the index of a list that is asked often; it is the
host's to declare, the engine creates none. Without the bound either form would read every row that
matches (measured in process on 50 000 matching rows of about 450 bytes: 22 ms from the index, 28 ms
as documents; bounded at 20 000 rows 8 ms and 12 ms). The bound is what keeps a list on a table of millions at the cost of
50 000 rows. A `group` before the stage is computed over every row that reaches it first, as
always: the bound then limits the groups read, not the rows.

**Not offered.** A list under a keyed or remote alias (`NOT_CONTINUABLE`: its rows come from an
owner after the page); a `contains` search; a cursor; a count of rows rather than elements under a
collection; an answer kept between requests (every request reads the database).

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
  their members, all with `onlyFor`. A polymorphic entity keeps its
  declared class as the root, merged the same way, once its subclasses are registered.
- A row renders each value with the members of the variant it is stored as: the base members in
  order, then that variant's own.

On a row of another variant a variant-only member is absent, so `eq null` and `neq <value>` match it
(explain notes `ONLY_FOR_VARIANTS`). `is` tests the variant itself: a name stands for that variant
and every registered descendant; a concrete base name for all of them and for values stored without
a discriminator. The discriminator is compared exactly.

The stored discriminator itself is no member under contract 2: a path that ends in it (`items._t`)
is `UNKNOWN_PATH` with `params.reason: "notAMember"` and `params.discriminator`, and the message
says so. Which variant a row holds is what the model publishes for the member and what `is` tests.
(A contract 1 request reads `_t` as the stored member it is: the legacy pipeline answers it; see
[`LEGACY.md`](LEGACY.md).)

## Members that hold their own kind

A type may hold values of its own kind: a group item holds items, an operation item holds the
items it was applied to. The model's path index walks a type once per branch and stops where it
meets the type again, so it stays finite; the paths below that point are described from the place
the index does describe the type, under the spelling and the storage of the path that was asked
for. So every level is a path like any other:

- after `unwind item.items as sub`, `sub.text` and `sub.items` are paths, and `sub.items` unwinds
  again;
- without an unwind, `items.items.text` is a path through two collections, with the rules of any
  path through collections (a condition means "some element", a projection keeps the nesting);
- a name the type does not have is `UNKNOWN_PATH` at every level; a path is described down to 32
  segments, the depth the index itself stops at.

Explain lists the members the index holds; a deeper level is typed by the same type, which the
answer names once.

## Flattened trees

`unwind` with `flatten` turns a tree of items of one kind (an invoice's group items holding items)
into one row per item at any level down to `MaxFlattenDepth`:

- Order is pre-order: a group, then its children, then the next sibling. `includeIndex` numbers the
  rows of one document in that order.
- Each row carries the item without its nested collection; the members of its variant are there as
  usual, so a group row and a billing-line row can be told apart with `is`.
- Items below the depth are not in the rows; the page names the path, the depth and how many rows
  lost items (`UNWIND_DEPTH_TRUNCATED`). Under `strict` the request refuses instead.
- `preserveNull` keeps a document with no items once, as for a plain unwind.

## References

A join follows a reference the model declares; nothing is inferred from names. The simple form is a
member naming one entity by its key or another field (`[OxQLReference("<entity>", field?)]`, or the
base package's `[ReferenceId]`). There are four more:

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
- **Whose entity a target is** is read off its id. An id that is an entity of this host is local.
  Any other id is another service's only when it carries that service's prefix
  (`<service>.<entity>`, the prefix being the host's `InternalHosts` key): the reference is then
  remote and the declaration names the target field. An id of this host's own prefix that is no
  entity is the build finding `reference-target-unknown` (`reference-case-target-unknown` for a
  case), and an id without any prefix that is no entity of this host is
  `reference-target-unprefixed`: it names no owning service, so no reference is emitted (a case is
  dropped) and nothing asks the host to configure a service named after an entity. A build
  finding never stops a host from starting; the member simply has no join.
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

**Relation names.** Every reference has a name, derived by the engine from the model alone:
nothing declares one, no start, build or schema load checks one, and no query names one. A name
labels a declared reference for tools (a relation list, the default alias of a join along it); it
never creates a reference. The schema document publishes it as `relation` and explain echoes it as
`aliases.<alias>.reference.name`. The rule, by wire names:

1. A reference member whose id member a navigation property names (the base package's
   `[ReferenceId("StartAddressId")]` on `StartAddress`) takes the navigation property's name:
   `startAddress`.
2. A reference on the key member of an embedded object, or of the objects of a collection, is named
   at the *slot*, the member that holds the object or the collection: the slot's name without a
   trailing `Reference` or `Ref`. `sourceBillingLineReference.id` is `sourceBillingLine`,
   `resources[].id` is `resources`. The key member is `id`, else `referenceId`; where both carry a
   reference the slot names `id` and `referenceId` keeps its own name.
3. Any other reference member is named by its own name without a trailing `Ids` (the plural `s`
   stays), `Id`, `Number` or `Key`: `tourId` is `tour`, `vehicleIds` is `vehicles`,
   `contactNumber` is `contact`.
4. Where none of those ends the name, without a trailing `Reference` or `Ref` (`ownerRef` is
   `owner`), else the name as it is (`reference`).

A suffix is stripped only when something is left (`id` stays `id`), one suffix only, and the result
starts in lower case. Names are unique among the relation names of one type: two members that derive
the same name both take their own wire name instead (`driverId` and `driverRef` stay as they are),
and so does a member whose derived name is the wire name another one fell back to. A stored member
of the same name (`updateUser` beside `updateUserId`) is no collision: relation names are a
namespace of their own. An alias, unlike a name, must be free in the row, so a tool that takes the
name as an alias makes it unique against the row's roots itself.

`target` narrows a typed reference to one target entity; rows whose case selects another target are
`excluded`. `elements` resolves a reference inside a collection that is not unwound; without it such
a path is refused (`RESOLVE_ON_COLLECTION`).

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
- `elements: "first"` takes the first element that resolves (`resolved` or `ambiguous`); failing
  that the row is `owner_unanswered` if any element was, else `not_found`, else `invalid_key`,
  else `excluded`, else `reference_null`.
- The `onMissing` of a stage is its own word; without one, the request's top-level `onMissing`;
  without that, `refuse` under `strict` and `null` otherwise. The request's word is bound onto
  each resolve as if the stage had written it, so a resolve continued at an owner carries it on
  the stage and the owner binds it like any other.
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
the target is an item or a non-key field, so a second record is always seen. Two plain forms ask a
single-record query and cannot always see it: a simple local reference onto a non-key field
runs inline and takes the first match; a simple remote reference onto a non-key field sends the
plain key match, so the second record is seen only when the owner's page happens to hold both (an
answer kept in the cache repeats what the first answer saw). Declare such a reference onto the target's key, or
expect `ambiguous` to be reported only by the keyed forms. Both plain forms look for the second
record once the request is `strict`, the stage's `onMissing` is not `null` or the stage names its
outcome (`outcomeAs`): the outcome is then read, so it is told.

## Strict requests

`strict: true` says: these rows go into a document, so answer them all or refuse. Every
truncation, every `ambiguous`, every owner failure (`RESOLVE_TIMEOUT`, `RESOLVE_UNREACHABLE`,
`RESOLVE_PARTIAL`) and, unless the stage's or the request's `onMissing` says `null` or `report`,
every missing reference refuses with 422, carrying the diagnostic it would have answered as its
error; the plain `null` of `reference_null`, `excluded` and `not_applicable` does not. A report page
(no `cursor`) may hold up to `MaxReportPageSize` rows and refuses with
`PAGE_INCOMPLETE` when more match. The request's `strict` is carried in the query sent to an owner
that runs continued stages, and a strict request reads no cached "not found". Without `strict`
nothing about the rows changes: `strict` and `onMissing` refuse or report, they never alter a row.
`strict` enters no cursor fingerprint; `onMissing` enters it only through a stage continued at an
owner.

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
  `Explain:MaxOwnerCalls` owner calls in all. The conditions a run asks owners before the page
  (the semi-joins) are checked in the same calls and within the same budget: a condition adds a
  check to its owner's call, never a round, and one that was not asked is said to be unchecked.
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
  lifetime), and only a path no branch reaches refuses. A branch that reaches none of the paths the
  row shows under the alias is asked for the alias itself, so its owner still joins: the alias is an
  empty object where the record exists and `null` only where it does not. A union join under an alias a continued stage
  added is sent to that alias's owner as written, which splits it the same way.
- **Owners.** An owner is a service that runs this package (it needs the internal routes
  `internal/oxql/batch` and `internal/oxql/explain`) and is listed in `InternalHosts`; any other
  service's entities stay unknown or unavailable to origins (`UNKNOWN_ENTITY`,
  `RESOLVE_UNAVAILABLE`). A service listed by mistake is not reached: a run answers the rows with
  the alias `null` and `RESOLVE_UNREACHABLE` (a refusal under `strict`), a condition asked of it
  (a semi-join) refuses with `RESOLVE_UNAVAILABLE`, and explain notes the part `REMOTE_UNCHECKED`.
  There is no probe and no gate.

Aggregation over chain data (`unwind`, `group` under a chain alias) is refused (`NOT_CONTINUABLE`);
it belongs in the report that reads the rows.

**Variables in chains.** An owner never receives `variables`. The origin binds every `{ "$var": … }`
inside a continued stage and inside the `filter` of every remote resolve and writes the value into
the owner query; an unbound one is `UNBOUND_VARIABLE` at the origin. Because the cache key is taken over the
substituted query, two requests with different variables never share a cached answer.
