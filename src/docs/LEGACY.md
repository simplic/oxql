# OxQL contract 1 legacy support (`compat.v1`)

This is temporary code with an intended end. Contract 1 requests are answered by a pipeline ported
from the 1.1 query path, so that existing callers (reports above all) get the answers they got
while they are migrated to contract 2 one by one. When the last of them sends the contract
header, everything described here is deleted. Nothing in it is a base to build on.

Everything that belongs to it is marked `LEGACY(compat.v1)` and lives in three folders named
`Legacy`. `OxQL.Tests/AspNetCore/LegacyIsolationTests` fails when a legacy type is named or
declared anywhere else, when a legacy option is read outside the folders, and when this document
stops listing a legacy file or seam.

## When the legacy pipeline answers

The header `X-OxQL-Contract` decides the contract of a request on a host with
`OxQL:Compat:Enabled` (default on): absent or empty is contract 1, `1` and `2` are themselves,
and any other value is 400 `UNKNOWN_REQUEST_MEMBER` with `params.reason: "contract"`.

The legacy pipeline answers a contract 1 request over `POST /oxql/query`, and each entry of a
contract 1 `POST /oxql/batch`, on a host with `OxQL:Compat:Legacy:Enabled` (default on). It never
answers a contract 2 request, an internal call, an explain, or any request of a host with
`Compat:Enabled` off.

| `OxQL:Compat:Legacy:Always` | Which contract 1 requests | Log `reason` |
|---|---|---|
| `true` (default) | every one | `always` |
| `false` | the compatibility rewrite answers first (the published 2.0 behaviour of contract 1); the legacy pipeline answers only what the rewrite refuses: a `lookup` or `resolve` in the v1 form, a path the model does not declare in a stage that reads it, a stage after the page stage | `lookup`, `resolve`, `path`, `order` |

The rewrite path has its own cursor form, a `totalCount` capped at `Limits:CountCap`, refusals for
stage members and operand kinds contract 2 does not take, the contract 2 time ceiling and paths in
the model's spelling. It stays for a service whose contract 1 callers were written against the 2.0
rewrite, and it stays tested (`Contract1HttpGoldenTests`' rewrite set, `Contract1PipelineTests`,
`LegacyGoldenTests.G02`, and the contract 1 cases of every integration suite outside
`Suites/Legacy`, whose fleet sets `Always` off in `FleetHost.Defaults`).

A host that answers queries through a controller of its own keeps no body for the pipeline to
read: its contract 1 requests are answered by the rewrite, and each is logged at warning level
with `reason=unreadable`.

Every answer of the legacy pipeline writes one line in the log category `OxQL.Compat.Legacy`: the
entity, the reason, the refusal code if any (`INTERNAL_ERROR` for a fault, `CANCELLED` for a
request its caller abandoned), the user, the organisation and the correlation id, then the route
(`query` or `batch`), the status it was answered with, the rows it returned, the time it took and
the database's share of it. Counting those lines per service says who still calls without the
header, and when the pipeline can go.

### Who still sends contract 1

Two sources answer it, and they agree:

- **The log**, per request and with the caller. The line of the category `OxQL.Compat.Legacy`:

  ```
  OxQL.Compat.Legacy contract 1 request for {Entity} answered by the legacy pipeline: reason={Reason}
    refused={Refused} user={UserId} org={OrganisationId} correlation={CorrelationId}
    route={Route} status={Status} rows={Rows} elapsedMs={ElapsedMs} dbMs={DbMs} dbCommands={DbCommands}
  ```

  `UserId` and `OrganisationId` are what the host's scope provider says of the request, so a
  report server's technical user and a person's session are told apart; `CorrelationId` leads to
  the caller's own log. Group the lines by `UserId` and `Entity` to get the list of callers to
  move; `rows` and `elapsedMs` say how heavy each is, `status` and `refused` whether it works at
  all. A contract 1 request whose body the host could not bind never reaches the pipeline and
  is answered by the host; it writes the line `… with a body the host could not bind, answered by
  the host: reason=unbound route={Route} …` in the same category, so it is counted too. With
  `Compat:Legacy:Enabled` off the lines of contract 1 requests are in the category `OxQL.Compat`.
- **The meter**, as a rate without the caller. `oxql.requests` and `oxql.request.duration` of the
  meter `OxQL` carry the tag `contract` (`1` or `2`) beside `route`, `entity` and `outcome` (see
  *What a host measures* in [`oxql-operations.md`](oxql-operations.md#what-a-host-measures)). The
  series with `contract="1"` is the traffic that still depends on this pipeline, per entity; when
  it has been flat at zero for the agreed time on every service, nobody sends contract 1 any more.
  The meter has no user and no organisation on purpose: who it is, the log says.

## What it answers

The pipeline reads stored documents without a model. A request names an entity, which gives a
collection; every stage becomes database stages with the paths as the caller wrote them (the
whole path `id` is `_id`); the stored documents come back as stored.

- **Rows:** member names and nesting as stored, `_id` and `_t` included, in the v1 encoding (the
  encoding the rewrite's rows use too): a UUID as text, a date as `yyyy-MM-ddTHH:mm:ss.fffZ`,
  Decimal128, double, int and long as JSON numbers.
- **Envelope:** `{ "items": [...], "pageInfo": { "hasNextPage", "nextCursor"?, "totalCount"? } }`.
  The count is every matching row. The cursor is base64url JSON of the sort values, so a cursor a
  caller holds from before the upgrade continues after it.
- **Stages:** `match` (and/or/not, the thirteen operators, `options.ignoreCase`, the type hints
  `$uuid`, `$uuid3`, `$date`, `$oid`, `$long`, `$decimal`, `$regex`, `$null`, variables by `$var`
  without regard to case, a GUID text under `eq` / `neq` matching both UUID subtypes and the
  text), `lookup` (`from` a collection name, `localPath`, `foreignPath`, `as`, `convert`,
  `filter`), `resolve` (checked, then left out of the aggregate), `unwind` (`as` optional: without
  it the element takes the collection's place; `preserveNull`, `includeIndex`), `group` (`by` with
  `path` or `dateTrunc`, the nine aggregate functions, arithmetic arguments), `project` (nested
  or dotted, inclusion only), `sort` (a tie-breaker on `id` appended), `page` (`limit`, `cursor`,
  `includeTotalCount`; the default sort and the default page added where the request has none).
- **Paging is by cursor only.** `offset` is no member of this page and is ignored like every
  member the pipeline does not know: `{ "limit": 50, "offset": 100 }` answers the first 50 rows,
  and the next page is the one `pageInfo.nextCursor` leads to.
- **Refusals:** the contract 1 codes and messages (`UNKNOWN_TYPE`, `PAGE_SIZE_EXCEEDED`,
  `INVALID_LOOKUP_CONVERT`, `INVALID_PATH_DOLLAR`, `INVALID_CURSOR`, `REGEX_TOO_LONG`, …) with
  status 400, type `validation_error`, title `Query validation failed.` and the status repeated
  as a member of the body.
- **Faults:** 500, type `internal_error`, code `INTERNAL_ERROR`, the status repeated in the body.
  The message says where the detail is (the service log, under the correlation id) or that the
  request is at fault. A row that holds a legacy (subtype 3) UUID is such a 500.
- **A body the host cannot bind** (no JSON, no `pipeline`, a stage that is a number) is the
  host's 400 problem details.

Behaviour kept as it is, because an answer depends on it:

- `convert` coerces the local value to a UUID for both of its values, in the database's JavaScript
  (`$function`): the MongoDB server needs server-side JavaScript enabled.
- A condition object takes its first operator; a condition with several paths in one object takes
  the first path. A member written twice in one object is read as its last.
- A projection keeps the paths written with `1`; a projection of exclusions only keeps nothing
  and is a fault (500). `Id` is not `_id` (only the whole path `id` is), so `"Id": 1` selects
  nothing, and `_id` comes back because MongoDB includes it.
- A `sort` on `_id` gets the `id` tie-breaker as well; two sort stages apply in the order written,
  two page stages cut twice; a stage with several keys applies them in the order match, lookup,
  unwind, group, project, sort, page.
- `in` and `nin` compare the operand's values as written: a GUID text matches a stored UUID under
  `eq`, and not under `in`.
- A request member, a stage key and a stage member the pipeline does not know are ignored:
  `offset`, `timing`, `filter`, `strict`, a `values` stage.
- `limit` may be written as text (`"2"`).
- An operand its operator cannot take (`in` with a text, `contains` with a number) and a `null`
  where a stage or a sort entry stands are a fault (500).
- An aggregate's argument is handed to the database as written: a path as `$<path>`, a variable
  as the text of its JSON.

Explain has no legacy form: an explain of a contract 1 request is the rewrite's, and a v1 join
explains as `valid: false`, `LEGACY_STAGE_UNSUPPORTED`. The 1.1 `types` route and the query plan
cache are not ported.

## What it guarantees

- **The organisation.** The caller's organisation is matched before any stage of the request, on
  `OxQL:Compat:Legacy:OrganisationPath` (default `OrganizationId`), and in the filter of every
  `lookup`. A document that does not carry it is never read: not as a row, not through a
  `lookup`, whatever the collection and whatever the caller's filter. A request without an
  organisation is `ACCESS_DENIED` (403); so is every request on a host whose `OrganisationPath`
  is empty, holds a `$` or has an empty segment.
- **Paths.** A path with `$` is refused (`INVALID_PATH_DOLLAR`) in `match`, `unwind`, `project`,
  `sort`, a group key, a `lookup`'s paths and a `lookup`'s `filter`. Operands become values, never
  operators.
- **A lookup reads** a collection of the queried entity's database by its name, and of it only
  the documents of the caller's organisation. `from` need not be an entity. That is wider than
  contract 2, where a join follows a reference the model declares; a service that knows which
  collections its reports join lists them in `OxQL:Compat:Legacy:AllowedLookupSources`, and a
  `lookup` from another collection is 400 `DISALLOWED_LOOKUP_SOURCE`.
- **Time.** Every database call carries a time limit: page and count together run under
  `OxQL:Compat:Legacy:MaxTimeMs`. A request over it is `QUERY_TIMEOUT` (504). A request its caller
  abandons is cancelled at the database.
- **Patterns.** A `regex` operand written as text and longer than `Limits:RegexMaxLength` is
  `REGEX_TOO_LONG`. The limit covers that form only: a pattern given by `$var` or by the `$regex`
  hint (also inside `in`) is handed to the database as written, whatever its length.

Held by `LegacyPipelineTests` (S01 to S07, F01 to F04) and the legacy block of `HostTests`.

## What a host can set

| `OxQL:Compat:Legacy:` | default | |
|---|---|---|
| `Enabled` | `true` | off: every contract 1 request is the rewrite's, and what it cannot express is refused |
| `Always` | `true` | off: the rewrite answers first (see above) |
| `OrganisationPath` | `OrganizationId` | the stored member every collection is filtered by |
| `AllowedLookupSources` | empty | the collections a `lookup` may read, without regard to case; empty means any collection of the entity's database |
| `MaxTimeMs` | 600 000 | ten minutes, so a long-running report still answers; not clamped; 0 or less takes the default. It is not the contract 2 engine's `Execution:MaxTimeMs`. An aggregate the database has said nothing of by then, and `Execution:ClientDeadlineMarginMs` (5 000) after it on the host's clock, is given up on and answered `QUERY_TIMEOUT` like the limit's own expiry |

The pipeline checks a request against the host's `OxQL:Limits` (`MaxPageSize`, `DefaultPageSize`,
`MaxPipelineStages`, `MaxLookupStages`, `MaxUnwindStages`, `MaxGroupFields`,
`MaxProjectionFields`, `RegexMaxLength` for a `regex` operand written as text), under the
contract 1 codes.

## Health

`GET /oxql/health` says of a caller's requests what holds for them. A contract 1 caller on a host
where the legacy pipeline answers it first reads that pipeline's capabilities and limits:

```jsonc
{
  "status": "healthy",
  "service": "oxql",
  "engine": { "version": "2.1.x", "contract": 2 },
  "capabilities": ["batch", "compat.v1"],
  "limits": {
    "maxPageSize": 500, "defaultPageSize": 100, "maxPipelineStages": 20, "maxLookupStages": 5,
    "maxUnwindStages": 5, "maxGroupFields": 40, "maxProjectionFields": 500,
    "maxRequestBytes": 262144, "maxBatchQueries": 10, "regexMaxLength": 200
  },
  "remote": [ { "service": "vehicle", "configured": true, "reachable": true } ]
}
```

- `engine` and `remote` say what the host is and are the same for every caller: a host that reads
  another host's shallow health sends no contract header and takes `engine.version`,
  `engine.contract`, `limits.maxBatchQueries` and `limits.maxPageSize` from it. A `remote` entry
  has the three members shown; what a contract 2 caller reads beside them (`contract`,
  `engineVersion`, `usable`, `reason`) is not written here.
- `capabilities` has `batch` and `compat.v1` and nothing else: no `page.offset`, no `any`, no
  `group.page`, none of the remote names, no `explain`.
- `limits` has the ten values the pipeline and its two routes check. `regexMaxLength` is the
  length of a `regex` operand written as text. The time ceiling has no entry.

With `X-OxQL-Contract: 2` the answer is the contract 2 engine's. With `Compat:Legacy:Always=false`
or `Compat:Legacy:Enabled=false` a contract 1 caller reads the rewrite path's list (`batch`,
`group.page`, `page.offset`, `any` ahead of the host's own, and every limit). Held by
`HostTests.Health_lists_for_a_caller_the_legacy_pipeline_answers_what_that_pipeline_honours` and
`LegacyPipelineTests.P09`.

## How it is held

`OxQL.IntegrationTests/Suites/Legacy/LegacyGoldenTests` holds a host that sets nothing against
answers recorded from a build of the 1.1 query path (commit `ce52f30`: OxQL.Core 1.1.526.813,
OxQL.Mongo 1.1.626.818, OxQL.AspNetCore 1.1.726.813; page sizes 500 and 100) over the very
database the suite seeds. `Suites/Legacy/Fixtures/legacy-1.1.json` has one recorded answer for
each of the suite's 171 requests:

- five requests shaped as report queries (`report-*`) and the rest of the surface reports use:
  joins with `filter` and `convert`, `unwind` with an index, `resolve`, groups with `dateTrunc`
  and arithmetic, every type hint, variables, cursors, the refusals;
- what a simple caller sends, one thing per request: each operator, `and` / `or` / `not`, sort,
  the page with and without a count, an `offset`, a cursor round trip over three pages, an empty
  result, projections, operands of another kind than the stored member, paths in the model's
  spelling, members the pipeline does not know, stages written as something else, bodies that
  are no request;
- requests shaped as a planning client's (`consumer-*`): an `and` of `eq`, `or`, `$date` bounds
  and `contains` with `ignoreCase`, a nested `_id` compared with a GUID text, a sort, and a page
  followed through its cursor;
- three of them sent as a client behind a browser sends them (`Accept-Encoding`, `Origin`,
  `X-OxQL-Timing`).

For each request the test compares the status, the headers, the envelope member for member, the
page info with the cursor text and the count, every row's JSON text in the recorded order, a
refusal's type, title, `status` member, codes, messages and paths, and, wherever the rows have one
order, the body byte for byte (its SHA-256). 155 of the 171 are held in all of that. The other 16:

| What | Requests | Held as |
|---|---|---|
| a body the host refuses when it binds it to its typed stages | 13 (`page-limit-fraction`, `page-count-text`, `match-not-an-object`, `stage-unwind-empty`, …) | the status (400); the words are this host's |
| rows unwound from one document, which no sort tells apart | `report-invoice`, `report-invoice-pricing`, `report-hr` | the same rows, as a set |

Of a fault (500) the status, type, title and code are held.

### Recording again

The recorder is not part of the repository. It is a host around a build of `ce52f30` that takes
`<connection> <spec.json> <out.json>` and posts each case of the spec to its query route. Run
`LegacyGoldenTests` with `OXQL_RECORD_LEGACY=<path to the recorder's dll>`: the suite seeds its
database, starts the recorder against it and rewrites `Fixtures/legacy-1.1.json` (LF, without the
response date and the trace id, a fault reduced to what is held). `LegacyGoldenTests.G00` holds
that the file's entries are exactly the cases. An answer of this engine is never recorded into
that file.

## The files

Deleted whole when the legacy support goes:

| File | What it is |
|---|---|
| `OxQL.Core/Legacy/LegacyOptions.cs` | `OxQL:Compat:Legacy` |
| `OxQL.Core/Legacy/ILegacyPipeline.cs` | the contract between the host and the pipeline |
| `OxQL.Mongo/Legacy/LegacyRequest.cs` | the request model and its reader |
| `OxQL.Mongo/Legacy/LegacyValidator.cs` | the validation |
| `OxQL.Mongo/Legacy/LegacyAggregate.cs` | the normaliser and the stage builders, with the organisation first |
| `OxQL.Mongo/Legacy/LegacyCursor.cs` | the cursor |
| `OxQL.Mongo/Legacy/LegacyPipeline.cs` | validate, build, run, answer |
| `OxQL.AspNetCore/Legacy/LegacyRoute.cs` | which contract 1 requests the pipeline answers; the request as written; the body filter |
| `OxQL.AspNetCore/Legacy/LegacyHealth.cs` | the capabilities and limits health lists for a caller the pipeline answers |
| `OxQL.IntegrationTests/Suites/Legacy/` | the fixture, the recorded answers, the suites |
| `OxQL.Tests/AspNetCore/LegacyIsolationTests.cs` | this document's guard |
| `OxQL.Tests/Fixtures/contract1/http.json` | the default path's answers through the Sample host |
| `docs/LEGACY.md` | this document |

Edited (each place carries the marker unless said otherwise; remove the marked lines):

| File | What to take out |
|---|---|
| `OxQL.Core/Models/OxQLOptions.cs` | `CompatOptions.Legacy` |
| `OxQL.Core/Engine/Outcomes.cs` | `Refusal.StatusInBody`, the `status` member only a legacy refusal writes |
| `OxQL.Mongo/ServiceCollectionExtensions.cs` | the registration of `ILegacyPipeline` |
| `OxQL.AspNetCore/ServiceCollectionExtensions.cs` | the registration of `LegacyRoute` |
| `OxQL.AspNetCore/Controllers/OxQLController.cs` | the `LegacyBodyFilter` attribute; in `Health` the marked line and the two `legacy?.… ??` it feeds, so `capabilities` and `limits` are `EngineCapabilities.Of(…)` and `Limits(options)` again; the sentence on the legacy pipeline in `Health`'s summary (no marker: it is an XML doc) |
| `OxQL.AspNetCore/OxQLQueryService.cs` | the `legacy` field and constructor parameter; in the contract 1 branch `return await Rewritten();` remains |
| `OxQL.AspNetCore/Binding/OxQLRequestBodyAttribute.cs` | the marked branch that writes the line of a contract 1 request the host could not bind (`LegacyRoute.UnboundAsync`); a contract 1 request is then left to the host without a line |
| `OxQL.AspNetCore/Compat/CompatBinder.cs` | the wording of `LEGACY_STAGE_UNSUPPORTED` for a join names the legacy pipeline; reword it |

## Removing it

Removing the legacy support ends the support of callers written before contract 2: after it a
contract 1 request is the rewrite's (the published 2.0 behaviour), until contract 1 goes too.

**It is a breaking change of the packages' public API**, to be released as one: `LegacyOptions`,
`ILegacyPipeline`, `LegacyRoute`, `CompatOptions.Legacy` and `Refusal.StatusInBody` disappear and
the constructor of `OxQLQueryService` loses a parameter. A host that names any of them no longer
compiles. `OxQL:Compat:Legacy:*` configuration keys are then ignored without a word.

1. Confirm in each service's log that `OxQL.Compat.Legacy` has had no line for the agreed time, and
   on its metrics that `oxql.requests` with `contract="1"` has stayed at zero
   (see [Who still sends contract 1](#who-still-sends-contract-1)).
   (`OxQL:Compat:Legacy:Enabled=false` on one service is the rehearsal.)
2. Delete the three `Legacy` folders, `OxQL.IntegrationTests/Suites/Legacy`,
   `OxQL.Tests/AspNetCore/LegacyIsolationTests.cs`, `OxQL.Tests/Fixtures/contract1/http.json` and
   this file.
3. `grep -rn "LEGACY(compat.v1)" src` and remove each marked place as the table above says; then
   the unmarked sentence in `OxQLController.Health`'s summary. The build fails on anything that
   was missed.
4. Restore the tests this support changed, which carry the marker too:
   - `OxQL.Tests`: `SampleHost.RewriteFirst` goes, and every `new SampleHost(SampleHost.RewriteFirst)`
     becomes `new SampleHost()`. In `HostTests` the marked block goes and
     `A_v1_join_under_contract_1_is_…` becomes the one test it was (a v1 join under contract 1 is
     `LEGACY_STAGE_UNSUPPORTED`). `RemoteHostTests`, `HostHardeningHealthTests` and
     `HostStrictRequestTests` may ask health without the header again. In
     `Contract1HttpGoldenTests` the default theory and the comparison of the two paths go; the
     rewrite set (`Fixtures/contract1/rewrite/`) moves back one folder up, and its two entries
     that were re-recorded for the legacy pipeline (`query-refused` and the second entry of
     `batch`: a `match` on an undeclared name, 200) are recorded back to the 400 `UNKNOWN_PATH`
     the rewrite answers alone.
   - `OxQL.IntegrationTests`: the entry `OxQL:Compat:Legacy:Always` in `FleetHost.Defaults` goes
     with `LabFleet.Contract1AsShipped` and the `contract1AsShipped` parameter of
     `CorpusFleet.CreateAsync`; the same key set in `Suites/Entity/RetiredIdHost.cs` goes;
     `ContractATests.A04b` and `A32_A33_…` and `Contract1PlannerTests.W3_03` expect the refusals
     again (`UNKNOWN_PATH`, `LEGACY_STAGE_UNSUPPORTED`) and `A32c` is deleted. In
     `Suites/NonQuery/NonQueryTests` the health of the host that sets nothing lists the
     rewrite's capabilities again.
   - Comments that name the legacy pipeline or `Compat:Legacy:Always`: the summaries of
     `Contract1PipelineTests`, `HostHardeningCompatEntityCaseTests` and
     `HostHardeningCompatNullTests`.
5. Remove what the documents say of the legacy pipeline
   (`grep -rniE "legacy pipeline|Compat[:.]Legacy|LEGACY\.md" README.md src/docs` finds every
   place):
   - `oxql-operations.md`, `oxql-conformance.md`, `oxql-query-syntax.md` and the README: the
     sections and table rows on it;
   - `oxql-semantics.md`: the paragraph of the introduction on what answers contract 1 and the
     parenthesis on `_t` under the stored discriminator;
   - `UPGRADING-1.x.md`: the parenthesis on `Compat:Legacy:AllowedLookupSources` and the log
     category `OxQL.Compat.Legacy`.

Contract 1 compatibility itself (the rewrite, `Compat:Enabled`) is not part of this and goes when
contract 1 goes.
