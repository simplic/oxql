# OxQL conformance suite

`src/OxQL.IntegrationTests` checks the engine end to end: real HTTP requests into the engine's
own controller, a real MongoDB server, and a designed data set whose every answer is known
before the request is sent. It complements `src/OxQL.Tests`, which tests binding, compilation
and encoding without a server.

A case states its expected answer first (the row ids, their order, the count, or the refusal
code), computes it from the corpus rather than from the engine, and then asserts it. "The server
answered 200" is not a case.

## Running it

```bash
dotnet test src/oxql.slnx                                   # everything
dotnet test src/oxql.slnx --filter Category!=Integration    # the unit tests only
dotnet test src/OxQL.IntegrationTests                       # the conformance suite only
dotnet test src/OxQL.IntegrationTests --filter "FullyQualifiedName~Suites.Shipment"
dotnet test src/OxQL.IntegrationTests --filter "FullyQualifiedName~SH03"
dotnet test src/OxQL.IntegrationTests --filter "FullyQualifiedName~Suites.Report"   # the report scenarios
```

Every test class carries `[Trait("Category", "Integration")]`.

**MongoDB.** The suite starts its own server; nothing needs to be installed. On first use
EphemeralMongo downloads the official `mongod` binaries for the platform (checked against their
SHA-256 and cached in the user's local application data), then starts a single-node replica set on
a random port. One server serves the whole run and stops at process exit. The first run needs
network access for the download; later runs start the server in well under a second.

| variable | effect |
|---|---|
| `OXQL_TEST_MONGO=<connection string>` | use an existing server and start none; the databases the run created are dropped at exit |
| `OXQL_TEST_MONGO_VERSION=6\|7\|8` | the major version EphemeralMongo starts (default 8, latest patch) |
| `OXQL_TEST_MONGO_BIN=<directory>` | start the `mongod` in that directory instead of a downloaded one (pins an exact version) |

When no server can be provided, every test fails with one message naming these variables.
Nothing is skipped for want of a server: a skipped case is not a passing case. The only skips
are the opt-in fixture exporter (see *The fixture exporter*) and a case parked on a known engine
defect with the maintainer's agreement, whose `Skip` text names the defect.

## How it is built

| folder | what |
|---|---|
| `Fleet/` | the simulated fleet: one in-process host per lab service, the MongoDB fixture, the lab model |
| `Corpus/` (namespace `Fixtures`) | the rows, the seeder, and the oracle every expectation is computed with |
| `Harness/` | the verbs a case speaks: `LabClient`, `WireAnswer`, `CorpusFleet`, `ChaosOwner`, `EngineDirect` |
| `Spike/` | the fleet model's own checks (`FleetModelTests`): entities, variants, references and build findings per service |
| `Suites/<Area>/` | the cases, one folder per area |

**The fleet.** Each lab service is a small ASP.NET Core application on an in-memory test server
that registers the engine exactly as a service does: the options section, the MongoDB engine over
the service's own database, the controller, a scope provider, an addon definition source and a
remote query client. The seams are test implementations: the organisation comes from the
`OrganizationId` header, addon definitions are read from the service's database on every request,
and a remote reference is answered by the owning host (`InMemoryRemoteClient`). The client calls the
owner's query service through its internal overload (`BatchAsync(batch, internalCall: true)`), in a
request scope of the owner carrying the caller's organisation, user and correlation, exactly as the
base package's internal route does: only that path admits the keyed fetch's `keyedBy`. It answers
the internal explain the same way (`ExplainAsync(request, internalCall: true)`), and keeps each
owner's shallow health (engine version, `maxBatchQueries`) as the base package's client does. A
service served by a mounted handler (the chaos owner, a scripted owner) is posted to over HTTP
(`OxQL/batch`). Each host sees only its own entities, so a reference into another service is remote
exactly as between real services, and a chain across services continues at each owner in process.
Hosts are started on first use and shared by the whole run; a *variant* is the same service and
database under changed configuration (`OxQL:Limits:MaxPageSize`, `OxQL:Explain:Enabled`, …).
Explain is on by default, as on a real host.

**The lab model.** Synthetic entities under neutral names; the shapes carry the storage hazards
real services have. Since 2.1 the member names, nesting, polymorphism and reference declarations of
the report entities mirror the real services (ERP, logistics, HR, vehicle, contact), so a scenario
path on the fleet is the path the real service accepts after its migration; only the namespace
differs. The variants' class maps are registered with the storage conventions
(`FleetClassMaps.Register()`), and the transport service's host-side reference declarations
(`FleetReferences.For("transport")`) type the resource ids. The entities marked — hold no rows in
organisations A and B.

| service | entity | collection | rows A / B | what it is for |
|---|---|---|---:|---|
| `staff` | `staff.employee` | `employee` | 26 / 3 | strings: null, missing, empty, duplicates, case, accents, CJK, an astral code point, the Turkish dotted I; collections; the addon bag; since 2.1 `userId`, which a transaction's `createUserId` names |
| `fleet` | `fleet.vehicle` | `vehicle` | 20 / 2 | numbers: decimals stored as Decimal128 and as strings, zero, negative, 25 places, Int32 max; a storage name the derivation does not give (`QRCode`) |
| | `fleet.equipment` | `equipment` | 5 / 1 | a local reference onto vehicles: hit, duplicate, miss, null source |
| | `fleet.department` | `department` | 3 / 1 | the target of the vehicle and shipment departments |
| | `fleet.status` | `status` | 4 / 1 | a small lookup table with a null name |
| `transport` | `transport.shipment` | `shipment` | 40 / 3 | temporals across DST and the UTC day, ISO weeks across a year, enums including absent and unnamed values, nested collections, tags, the bag, and a remote reference onto `fleet.department`; since 2.1 its `billingLines[]` are the item target of typed references, and `tours[].tourId` references `transport.tour` beside a polymorphic `tours[].resource` |
| | `transport.shipment_template` | `shipment_template` | 6 000 / 40 | volume above the offset ceiling; duplicate, null and missing names at scale; a remote reference onto vehicles |
| | `transport.tour` | `tour` | — | billing lines (the shipment's item type), a polymorphic `resource`, `attachedResources[]`, and `actions`, an interface-typed collection of seven variants |
| | `transport.delivery_attempt` | `deliveryAttempt` | — | `shipmentId` onto the shipment, `dateTime`, `status { displayName }`, `text`: "the latest attempt" |
| | `transport.resource` | `resource` | — | an abstract entity of seven variants; the host-side declaration makes a driver's id an employee and a vehicle-like resource's id a vehicle; a carrier references nothing |
| `ledger` | `ledger.transaction` | `transaction` | 25 / 2 | byte enums, prices as Decimal128 and as strings, a decimal beyond `System.Decimal`, a dotted dictionary key, no bag; since 2.1 `items` of seven variants with nested group items, billing-line items referencing `ledger.billing_line`, `references[].referenceId` typed by `dataType` onto shipment or tour (key as guid), the recipient's `address.id` onto `directory.contact`, `createUserId` onto `staff.employee.userId` |
| | `ledger.billing_line` | `billing_line` | — | `sourceBillingLineReference { type, id }`, typed by `type`, onto the billing lines of a shipment or a tour (item targets, a union) |
| `directory` | `directory.contact` | `contact` | — | the contact-like service: `primaryEmailAddress.email`, `primaryPhoneNumber.number`, `address.companyName`, a GeoJSON location (`unknown`) |
| `conformance` | `conformance.entity` | `conformance` | 3 / 1 | every kind the model has: long above 2^53, date, char, binary, long enum, both dictionary forms, unstored members, local and remote references |
| | `conformance.ref` | `conformance_ref` | 2 / 1 | a local target keyed on `code` |
| | `conformance.child` | `conformance_child` | 3 / 1 | lookup children |
| `owner` | `owner.widget` | — | — | served by the chaos owner, not by a host |

Organisation C (`55555555-…`) holds only the 100 001-row bulk volume, written on demand.

**The report fleet.** Organisation R (`77777777-…`, tag `0077`) holds the rows of the report
scenarios (A1–A5 of the studio design) and of their failure modes, and nothing else; no case over A
or B sees them. They are seeded with the corpus (`ReportSeed`, rows in `Corpus/Rows/ReportRows.cs`)
and read through `ReportSeed.Entities`, `Rows(entity)`, `Row(entity, key)` and the ids it names
(`TransactionId`, `ShipmentId`, `ErpLineIds`, …); every oracle helper that takes a row reads them.
`Lab.ClientAsync(service, Org.R)` asks as organisation R.

| entity | rows | what they carry |
|---|---:|---|
| `ledger.transaction` | 6 | the mixed invoice: every item variant, groups two deep, shipment and tour lines, a tariff-only line; one invoice per failure: a deleted source line, a source id held by a shipment and a tour, a clerk user id two employees share, a user id no employee has, a billing line seven groups deep |
| `ledger.billing_line` | 6 | the ERP lines the invoice's items name: shipment freight and waiting time, the tour line, a deleted source, a duplicate source, a tariff-only line |
| `transport.shipment` | 3 | the scenario shipment (two billing lines, weight notes, its tour, three delivery attempts), one without attempts, one holding a billing-line id a tour holds too |
| `transport.tour` | 2 | a tractor tour (driver, trailer attached, one action per variant, a billing line) and a carrier tour |
| `transport.delivery_attempt` | 4 | three on the scenario shipment, the latest stored in the middle; one on the third shipment |
| `transport.resource` | 7 | one per variant; the car, container and equipment name vehicles the fleet does not hold |
| `staff.employee` | 4 | the clerk, two employees sharing one user id, the driver |
| `fleet.vehicle` | 2 | the tractor and the trailer |
| `directory.contact` | 2 | the invoice recipient, and a contact without an e-mail address |

`Suites/Seed/ReportSeedTests` proves these rows against the engine like the corpus.

## The corpus

The rules the data follows, and a case may rely on:

- **Deterministic ids.** Every id is `TTTTTTTT-OOOO-4000-8000-NNNNNNNNNNNN`: the id space
  (`Spaces`), the organisation tag (`0001` A, `0002` B, `0055` C, `0077` R), the version and variant
  nibbles, and the ordinal. `Ids.Of(Spaces.Shipment, Org.A, 7)` is the same value on every
  machine. `00000000` is the dangling space: ids that name nothing, so a join that answers null is
  observable. A case never hard-codes an id; it asks `Corpus.IdOf(Corpus.Shipment, "items-one")`.
- **Every row has a key and a purpose.** The key (`"matchcode-missing"`) is how a case names a
  row; the purpose says which storage state it carries.
- **Organisation B clones A.** B rows copy A rows on the members a case filters and sorts by, so a
  scope that stops firing shows up as extra rows, not as none. Every count is per organisation.
- **Null, missing and empty are three states.** Rows exist for each on the paths that matter.
  Missing is a member not written at all.
- **Storage forms are the driver's.** Rows are model objects serialized by the driver with the
  conventions a service registers (Guids as binary subtype 4, enums as Int32, decimals as
  Decimal128, a TimeSpan as its string, a char as Int32, a DateOnly as a date, an addon bag's
  decimal and nested dictionary wrapped in `{ _t, _v }`). What the driver cannot produce (a member
  left out, a decimal stored as a string, a decimal beyond `System.Decimal`) is written as an
  explicit raw override on the row, with its reason (`RawStorage`).
- **Addon definitions** are stored per organisation in each service's
  `model_definition.addon_definition`. The three business bags declare eleven live keys and one
  retired key; `transport.shipment` in organisation A also stores a retired `weight` definition
  before the live one, deliberately. `conformance.entity` declares five keys in A and none in B.
- **Nothing is soft-deleted.** The engine adds the organisation scope and nothing else; every
  entity has an `isDeleted: true` row that comes back.
- **The shared data is read-only.** It is seeded once per run. A case never writes to it.

**The oracle** (`Corpus`, namespace `OxQL.IntegrationTests.Fixtures`) reads the stored
documents, never the model objects and never the engine:

| helper | answers |
|---|---|
| `Rows`, `Row`, `IdOf`, `IdsOf`, `AllIds`, `Counts` | rows and ids by entity, organisation and key |
| `Where`, `IdsWhere` | the rows a predicate keeps |
| `ValueAt`, `Text`, `Texts`, `Number`, `Decimal`, `Date`, `GuidAt`, `Elements` | a stored member by wire path; `null` is missing, `BsonNull` is null; a path through a collection answers every element |
| `Present`, `Missing`, `Null`, `RowsMissing`, `RowsNull` | the three states |
| `SortedIds`, `Sorted`, `PageOf` | the engine's order: BSON type order, exact numbers, strings under the default collation or byte order, `id` as the tie-breaker |
| `Order.FoldCi`, `Order.CompareCollated`, `Order.CompareUtf8`, `Order.CompareDecimal` | collated equality and order (locale `de`, strength 1), code-point order, exact decimals |
| `Temporal.DateTruncUtc`, `Temporal.IsoWeek`, `Corpus.DateTruncBuckets` | `dateTrunc` buckets in a zone, keys spelled as the engine spells them |
| `ShipmentsWithStatus`, `VehiclesWithMileageBetween`, … | ready-made predicates |

`Suites/Seed` proves the seed and the oracle against the engine on every run: each entity answers
exactly its rows, the raw storage forms are in the database, and `SortedIds` predicts the
engine's order on the hazardous paths in both directions.

## Writing a case

```csharp
[Trait("Category", "Integration")]
public class ShipmentTests
{
    [Fact]
    public async Task SH01_an_eq_on_a_nested_string_returns_exactly_the_rows_the_oracle_picks()
    {
        var expected = Corpus.IdsOf(Corpus.ShipmentsWithStatus("Planned"));
        expected.Should().NotBeEmpty();

        var transport = await Lab.ClientAsync(LabService.Transport);
        var answer = await transport.SendAsync(Corpus.Shipment, """
            [ { "match": { "status.name": { "eq": "Planned" } } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 500, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
    }
}
```

1. Compute the expectation from the oracle. Guard it: an empty expectation, or one that is the
   whole entity, proves little, so assert that it is neither.
2. Send hand-written wire JSON. There is no typed builder: a raw string literal says exactly
   which bytes the engine gets. Any parameter that takes JSON also takes a `JsonNode` or an object.
3. Assert with the answer's verbs. A failure prints the request and the whole answer.

`Suites/Shipment/ShipmentTests.cs` is the worked example.

**The client** (`LabClient`, from `Lab.ClientAsync(service)` or `CorpusFleet.Client`):

| verb | does |
|---|---|
| `SendAsync(entity, pipeline, variables?)` | `POST OxQL/query` |
| `QueryAsync(body)` | the same with a whole request body |
| `BatchAsync(queries, maxTimeMs?)`, `BatchBodyAsync(body)` | `POST OxQL/batch` |
| `ExplainAsync(body)` | `POST OxQL/explain` (a plain query or the envelope); explain is on by default, the explain variant only sets it explicitly |
| `HealthAsync(shallow)`, `GetAsync`, `PostAsync` | health, and raw requests for malformed bodies |
| `As(Org.B)`, `As(guid)`, `Anonymous()`, `Contract(1)`, `Contract(null)`, `WithHeader` | who the request is sent as; contract 2 is the default, `null` sends no contract header |
| `MatchIdsAsync`, `MatchCountAsync`, `PullAsync` | a condition's ids or count; every row as the engine renders it |
| `WalkAsync`, `OffsetWalkAsync` | a cursor or offset walk to the end, every row in arrival order |
| `HostAsync()` | the host, for its captured log lines (`Logs`) |

**The answer** (`WireAnswer`): `Status`, `Body`, `Text`, `Items`, `Ids()`, `Strings(path)`,
`Values(path)`, `PageInfo`, `HasNextPage`, `NextCursor`, `TotalCount`, `TotalCountCapped`,
`Diagnostics`, `DiagnosticCodes`, `Errors`, `ErrorCodes`, `Type`, `IsProblemDetails`, `Results`
(batch entries); assertions `ShouldBeOk`, `ShouldRefuse("CODE", status?)`, `ShouldHaveIds`,
`ShouldHaveIdsInAnyOrder`, `ShouldHaveTotal`, `ShouldHaveDiagnostic`, `ShouldHaveNoDiagnostics`.
`Cursors.Payload` and `Cursors.Guid` read a cursor.

**Data of your own.** Never add rows to the corpus for one case. Either:

- create a private fleet, `await using var fleet = await CorpusFleet.CreateAsync("purpose",
  seed: [LabService.Staff], extra: async lab => { … })`: its own databases (dropped on dispose),
  the corpus of the services named, your rows written in `extra` through
  `lab.DatabaseAsync(service)`, and its own chaos owner; or
- for a model the lab model does not have, use `EngineDirect` (bind, compile, run the stages or
  the whole engine) over `MongoFixture.CreateDatabaseAsync("purpose")`, which is dropped when
  disposed. `Suites/Engine` shows both forms.

Configuration that differs (lower limits, a count cap, explain) is a variant:
`fleet.Variant(LabService.Transport, "cap-1000", new Dictionary<string, string?> { ["OxQL:Limits:CountCap"] = "1000" })`.
A key names one configuration for the fleet's lifetime.

**Volume.** `(await CorpusFleet.SharedAsync()).SeedBulkAsync()` writes the 100 001 bulk rows into
organisation C once per run; query them with `Client(LabService.Transport, Org.C)`.

## The chaos owner

`ChaosOwner` is an in-process owner of `owner.widget`, mounted into a fleet in place of a host.
The widget is keyed on `code`; its member `id` holds a different widget's code, so a join on the
wrong member returns the wrong widget instead of nothing. It applies every `match` stage,
projection (`$default` is `code` and `name`), sort and page, and refuses an unknown path or entity
the way an owner does.

| mode | batch answer |
|---|---|
| `Ok` | correct; `Many = n` answers over n synthetic widgets |
| `Slow` | correct after `Delay` |
| `Hang` | never; the caller's budget ends it |
| `Status` | `Status` with a plain-text body |
| `Garbage`, `Empty`, `WrongShape` | 200 with a body that is not JSON, no body, or `{"results":"nope"}` |
| `Short`, `NullEntry`, `ItemsNotArray` | one result too few, a null first result, `items` not an array |
| `MissingField`, `DuplicateKeys` | rows without `code`; two rows keyed `W-1` |
| `Refuse` | every result a refusal carrying `Code` |
| `Reset` | the head arrives, then reading the body fails |
| `Huge` | a single 64 MB row |
| `Closed` | every call, health included, is a refused connection |

`Times = n` makes a mode reset itself after n batch calls. `SetHealth(Down | Hang)` changes the
health probe independently. `BatchCalls`, `HealthCalls`, `Requests`, `Mark()` and
`BatchesSince(mark)` (with `Stage` and `Condition` on each request) show what the engine sent.
The shared fleet's owner is frozen in `Ok`; a case that changes the mode, or counts owner calls,
uses a private fleet's owner.

**The scripted owner** (`Suites/Chain/ScriptedOwner`, mounted by `ScriptedOwnerFleet`) covers the
keyed fetch's failure modes the chaos owner has no mode for. It answers as the chaos owner does,
then hides widgets (`Hidden`), cuts each answer to `Cut` rows and says a next page exists, answers
late (`Delay`) or with an error `Status`, and reports an engine `Version` and a `MaxBatchQueries`
on its shallow health. Every batch it receives is kept (`Batches`, `Since(mark)`), so a case can
assert what a forwarded query carried.

## The 2.1 suites

| suite | what it proves |
|---|---|
| `Rows/RowsVariantsTests` | polymorphic members on rows and in conditions, `is`, `flatten` |
| `Joins/JoinsLookupMembersTests`, `JoinsResolveMembersTests` | lookup `sort`, `first`, `on`, `LOOKUP_TRUNCATED`; the resolve members and their refusals |
| `Joins/JoinsKeyedFetchTests`, `JoinsOutcomesTests`, `JoinsRemoteVariablesTests` | the keyed fetch by keys, every outcome with `onMissing` and `strict`, variables substituted before sending |
| `Joins/JoinsContinuationTests` | continued stages across services and in process, `forTarget`, `not_applicable`, `OWNER_NOT_CAPABLE` |
| `Refusals/RefusalsRequestMembersTests` | `strict`, unknown top-level members, `keyedBy` refused on the public route |
| `Chain/ChainOwnerFailureTests`, `ChainOwnerCapabilityTests` | against the scripted owner, each outside strict, under `onMissing: "report"` and under `strict`: unreachable and late owners, a cut answer (`hasNextPage`), the key budget, a negative cache entry a strict request reads past, an owner batch cap below this host's, the chain ceiling, what a forwarded query carries; an owner on 2.0 |
| `Explain/ExplainNeverExecutesTests` | a command monitor on the engine's client sees no command without `include`, only `listIndexes` with `include: ["indexes"]`, and the `aggregate` when the same query runs |
| `Explain/ExplainShapeFleetTests` | the fleet's engines explaining for one another in process: every prefix of A1–A5 (what a studio explains while the query is built) binds, is complete and types every root of every stage, the owners' types included; a remote union is one union type; and the reference answers stay within their time budget, and at depth 1 within their size budget (T4) |
| `Explain/ExplainGoldenTests` | the explain answers of the reference set equal their recorded golden answers (`Suites/Explain/Golden`), the engine–studio contract; re-recorded with `OXQL_RECORD_GOLDEN=1`. Beside them, recorded and compared the same way (`Golden/mirror`): the fleet's schema documents (`schema/<service>.json`), the answers of `A1`, `A5` and `EX1-source-chain` with the types written out (`types/<id>.json`), and one answer of hosts that publish revisions, with fixed revisions (`revision/EX1-revisions.json`) |
| `Report/ReportRequestTests` | the studio's scenario requests (`Fixtures/scenarios/<id>.request.json`) equal the design's JSON, structurally |
| `Report/ReportExplainTests`, `ReportQueryTests` | A1–A5 and every prefix of them through `POST /oxql/explain` (valid, complete, the executor, phase and owner of every join, every alias typed, continued parts checked) and through `POST /oxql/query` under `strict`, over organisation R; the contract-1 hint |
| `Report/ReportFailureModesTests`, `ReportInvalidKeyTests` | duplicate keys in one grouped chunk, ambiguity through a plain non-key remote resolve, the existence probe, a flatten cut at its depth, the key budget over an in-process chain, the 5 000-row report page and `PAGE_INCOMPLETE`, `MAX_CONTINUED_STAGES_EXCEEDED`, `invalid_key` |

## The fixture exporter

`Report/ReportFixtureExport` writes the fixtures the Angular studio's acceptance specs replay: every
report scenario's request sent to the fleet as the studio sends it, and the answers as the wire
JSON the hosts answered. It is opt-in and skipped unless `OXQL_EXPORT_FIXTURES` names the fixtures
directory:

```bash
OXQL_EXPORT_FIXTURES=<absolute path>/oxql-studio/fixtures \
  dotnet test src/OxQL.IntegrationTests --filter "FullyQualifiedName~Suites.Report.ReportFixtureExportTests"
```

It reads the scenario requests from that directory, so a re-export follows the studio's current
requests, and writes per scenario `scenarios/<id>.explain.json` (the request explained and every
prefix of its pipeline explained, one answer per stage a studio adds, each `{ status, answer }`) and
`scenarios/<id>.query.json`; beside them `explain-invalid.json` (`valid: false`),
`strict-missing.json`, `strict-ambiguous.json`, `contract1-hint.json` and `export.json` (the files
and the flagged steps). The request files are input and are not rewritten. The output is indented
with two spaces and LF, and holds nothing that changes between runs of the same engine.

## Conventions for adding cases

- One folder per area under `Suites/`, one class per area file, namespace
  `OxQL.IntegrationTests.Suites.<Area>`, `[Trait("Category", "Integration")]` on every class. Do
  not name a namespace `Corpus`: it would hide the `Corpus` class.
- A method name starts with the case id without punctuation and continues as a sentence:
  `D07_an_or_of_two_conditions_matches_either`. A theory carries the id in its first argument.
- The corpus, the oracle, the harness and the lab model are shared and changed only on purpose, in
  their own commit. A suite adds its own files only.
- When a suite replaces older tests, record for each original case whether it was ported (the
  test name), merged into another case, or dropped with a reason.
- Read shared data only; write in a private fleet or an owned database.
- A case that exposes an engine defect is reported with its evidence and never loosened to
  pass; how it is parked until the fix lands is decided with the maintainer.
