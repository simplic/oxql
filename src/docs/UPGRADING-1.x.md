# Upgrading a host from the 1.x packages

Version 2 is a new engine behind the same routes. Contract 1 requests keep their answers through
the compatibility mode (README, *Contract header and compatibility mode*); the package surface is
not kept. What 2.1 changed against the published 2.0 package is in the README, *Upgrading*.

Who this document is for depends on how a service uses the engine:

- **A host that registers the engine itself** (`AddOxQLCore`, `AddOxQLMongo`, `AddOxQLAspNetCore`
  in its own start-up) does not compile after raising the package versions alone: the entry points
  below are gone or changed. Once it compiles it does not start until the two mandatory
  requirements below are met. This document lists what to change.
- **A service on the Simplic base package** (`Simplic.OxS.Server`) calls none of those entry
  points: the base package registers the engine, the scope provider and the cursor key for it. It
  compiles after raising the versions, and what it has to look at is its model and its schema, not
  this list: the upgrade section of the base package's
  [`OX_SCHEMA.md`](https://github.com/simplic/simplic-oxs/blob/master/OX_SCHEMA.md) (*Upgrading a
  service to this package version*). What follows from *What a caller meets when it moves to
  contract 2* on holds for its callers all the same.

## Packages

`OxQL.Model` is new and holds the entity model; `OxQL.Core` depends on it, so it arrives with
`OxQL.Core` as a transitive reference. The other packages keep their names. `[OxQLType]` now
lives in `OxQL.Model`; its namespace `OxQL.Core.Attributes` is unchanged and `OxQL.Core` forwards
the type, so entity assemblies compile without a change.

## Removed registration entry points

| 1.x | 2.x |
|---|---|
| `endpoints.MapOxQL()` (`EndpointRouteBuilderExtensions`) | removed; the controller is the only surface, mapped by `app.MapControllers()` |
| `AddOxQLEndpoint<T>(…)` | folded into `AddOxQLAspNetCore(…)` |
| `AddOxQLAspNetCore<T>(…)` | kept and marked obsolete; the type argument is ignored. Call `AddOxQLAspNetCore(…)` |
| `AddOxQLQueryFilter<TProvider>()` and the two delegate overloads, `IOxQLQueryFilterProvider`, `OxQLFilterInjectionContext`, `InjectedFilter`, `QueryFilterInjector` | `AddOxQLScope<TProvider>()` / `AddOxQLScope(httpContext => …)` with `IOxQLScopeProvider`; the organisation scope is applied by the engine, not injected as a filter |
| `AddOxQLTypeEnricher<TEnricher>()` and its overloads, `IOxQLTypeEnricher`, `OxQLTypeEnrichmentContext`, `OxQLTypeDescriptor`, `OxQLPropertyDescriptor` | no equivalent; the entity model and the host's `/schema` describe the types |
| `OxQLErrorResponse`, `OxQLFieldError`, `OxQLQueryResult` | `Refusal`, `QueryValidationError`, `QueryResult` / `QueryOutcome` (`OxQL.Core.Engine`) |
| `IExternalResolver`, `ICursorSerializer`, `IQueryAdapter<T>`, `IQueryExecutor<T>`, `IQueryPlanner`, `IQueryPlanCache`, `IQueryRequestNormalizer`, `IQueryValidator` and their implementations | `IQueryEngine`, `IRemoteQueryClient`, `CursorCodec` |
| `OxQLEndpointOptions.RoutePrefix`, `.IncludeErrorDetails`, `.EnableExplain` | the route is `/OxQL` (controller convention); `IncludeErrorDetails` moved to `AddOxQLMongo`; explain is `OxQL:Explain:Enabled` |
| `IOxQLQueryService.ExecuteAsync` returning `OxQLQueryResult`, `ExplainAsync` returning a list | `QueryOutcome` and `ExplainOutcome`; new `ExecuteAsync(request, maxTimeMs, ct)` and `BatchAsync` |
| `OxQLTypeRegistry`, `QueryValidationException`, `SortStage`, the `MatchStage.And` / `Or` / `Not` helpers, `Shape.WithAddons` | removed from the public surface; no replacement is needed by a host |

Beyond the entry points, three `OxQL.Core` binder types are worth naming for a host that
constructs bound pipeline parts itself:

- `Aggregate` takes the argument's kind as its fourth positional member, `Kind ArgumentKind`.
- `ShapeNode.GroupOutput` takes two optional trailing members, `Kind ElementKind` and
  `ShapeDef? ElementShape`, which describe a pushed element.
- `Shape.WithGroup` takes an `IEnumerable<ShapeNode.GroupOutput>`.

## Two new mandatory requirements

- **A scope provider.** Register one with `AddOxQLScope`. Without it the host throws
  `InvalidOperationException` at startup; this replaces the 1.x organisation filter provider.
- **A cursor signing key.** Set `OxQL:Cursor:SigningKey`. Without it `AddOxQLCore` throws at
  startup. The Simplic base package derives it from the auth token.

## Configuration

The `OxQL` section is now nested (`Limits`, `Execution`, `Compat`, `Explain`, `Representation`,
`Cache`, `Cursor`); the eight flat 1.x limit keys are still read (README, *Configuration*). Five 1.x
keys are no longer read and are silently ignored by the binder: `AllowedLookupSources`,
`AllowedResolveSources`, `AllowedPathPrefixes`, `QueryPlanCacheTtl`, `QueryPlanCacheMaxEntries`.
A contract 2 join follows a reference the model declares, and there is no plan cache to size.
(The legacy pipeline's own list is `Compat:Legacy:AllowedLookupSources`.)

`DefaultPageSize` changed from 50 to 100: a request without `page.limit` gets twice the rows.

`Execution:AllowDiskUse` defaults to `true`: a sort or group that outgrows the server's memory
limit spills to disk and finishes slowly instead of failing with `QUERY_TOO_EXPENSIVE`, whatever
the server's own default. Set it to `false` to refuse such queries.

`Execution:SlowQueryMs` is new (default 1 000): a request slower than that is logged once more,
at warning level, with what shaped it. Set it to `0` to turn the line off.

## What a caller meets when it moves to contract 2

A request without the contract header is contract 1 and keeps its answers while `Compat:Enabled`
is true (the default); each is logged under `OxQL.Compat.Legacy` (or `OxQL.Compat` on the rewrite
path) with the user, the organisation and the correlation id, so a host sees who still needs
migrating. What follows holds for a caller once it sends `X-OxQL-Contract: 2`.

**The header comes with the first contract 2 body.** The first thing a migrating caller meets is
its own request sent without the header: the host reads it as contract 1, where paths are the
stored names, an operand is written in that contract's forms and the members contract 2 added do
not exist. Such a request is not refused for being contract 2: it is answered as the contract 1
request it reads as, which for a condition on a contract 2 path or operand is 200 with no rows.
A caller that gets an empty page it does not expect checks the header first. The same holds for
explain: without the header it explains the request as contract 1 (`contract: 1` in the answer),
which is not how a contract 2 run reads it. `GET /OxQL/health` with the header lists what
contract 2 offers; without it, what contract 1 does.

- Under contract 2 an entity id is matched case-sensitively and has to be spelled as the
  schema publishes it (`UNKNOWN_ENTITY` otherwise); contract 1 requests keep v1's
  case-insensitive matching, retired ids included.
- `GET /OxQL/types` is not part of this package; the host's `/schema` document describes the
  entities.
- A contract 2 refusal envelope carries no `status` member; the HTTP status is the status.
- `dateTrunc` truncates in the given `timezone` (default UTC) with weeks starting on Monday. A
  contract 1 request groups weeks from Sunday, so a ported grouping query gets other week buckets
  unless it names `weekStart: "sunday"`. A `date` member has
  no time of day, so it truncates on its calendar day in that zone: the bucket is local
  midnight of the row's own day. A `dateTime` truncates as the instant it holds.
- `avg` over a `long` is taken in decimal, the way a `sum` over a long is, since a double cannot
  hold the mean of 64-bit integers exactly. A `long` and a `decimal` travel as JSON numbers with
  every digit.
- The elements of a pushed array arrive in the member's wire encoding: a `long` as a number,
  a `date` as `YYYY-MM-DD`, a single character as that character, an object by its members.
- An addon key defined as `date` is a timestamp in both contracts: it takes a date and time with
  `Z` or an offset (a bare `YYYY-MM-DD` is `INVALID_OPERAND`), and a row shows the instant stored.
- Under contract 2 string comparisons, sorts and group keys are case- and accent-insensitive by
  default (`muller` matches `Müller`), under a collation the host configures
  (`Representation:Collation`, German at primary strength unless changed). `options.caseSensitive:
  true` on a condition and `{ "path": { "direction": "asc", "caseSensitive": true } }` on a sort
  entry opt out; an exact sort needs every string comparison of the request to be exact too.
  `ignoreCase` is no option under contract 2 (`OPTION_NOT_APPLICABLE`, `params.use: "caseSensitive"`).
  An ordered comparison on a string orders
  under the collation and cannot opt out. Contract 1 folds only with `ignoreCase: true` and knows
  no `caseSensitive`.
- A member holding a single character folds the operand's case by code point, so `eq`, `neq`,
  `in` and `nin` match either case unless `caseSensitive`; the character is stored as its code
  point, which neither a pattern nor a collation reaches.
- Two `sort` stages do not compose: the later one replaces the earlier one.
- A `lookup` or a local `resolve` that no later `match`, `sort`, `unwind`, `group` or `resolve`
  reads runs after the page is taken, on the page's rows alone; the rows are the same, the
  join is paid per page row, and the sort stays next to the limit. Under contract 2 a join written
  before an `unwind` runs before it, once per parent and never once per element of the page (on
  the parents cut before the unwind where the page is ordered by them). A join a later stage reads,
  a join before a `group` that reads it, and a join whose alias a projection narrows stay where
  they were written. A join, local or remote, whose alias a projection drops and no later stage
  reads is not run, and the row does not carry the alias. The count pipeline never carries a
  join only the rows' display reads; explain shows the order the server runs.
- A count is asked with `page.count` and answered under `pageInfo.count`; `page.offset` and
  `page.includeTotalCount` are contract 1's and refused by name.
- A `lookup` or `resolve` follows a reference the model declares and has no `select`: `project`
  the paths under the alias.
- Refusals under codes contract 1 has too: `INVALID_ALIAS` for an alias `_id`, starting with
  `__` or ending in `__arr`; `ALIAS_COLLISION` for an alias equal to a member's wire or storage
  name; `INVALID_PATH` for a path with a control character, more than 64 segments or a segment
  over 256 characters; `INVALID_OPERAND` for a text operand of `contains`, `startsWith`,
  `endsWith`, a contract 1 `ignoreCase` comparison or a `caseSensitive` comparison inside a
  request that folds elsewhere, when it no longer fits the server's 32 KB pattern limit once
  escaped; `OPTION_NOT_APPLICABLE` for `caseSensitive` where it does not apply, for a
  `caseSensitive` sort inside a request that folds, and for `caseSensitive` and `ignoreCase`
  disagreeing on one condition; and `INVALID_REGEX` / `QUERY_TOO_EXPENSIVE` may also come
  back from execution, when the server rejects a pattern or exceeds its memory limit.
