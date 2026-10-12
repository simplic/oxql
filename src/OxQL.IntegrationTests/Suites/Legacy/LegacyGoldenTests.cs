using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Legacy;

/// <summary>
/// A request without the contract header is answered by the pipeline ported from the 1.1 query path
/// (commit <c>ce52f30</c>: OxQL.Core 1.1.526.813, OxQL.Mongo 1.1.626.818, OxQL.AspNetCore 1.1.726.813),
/// so that existing callers get the answers they got. That is the default of a host with
/// compatibility (<c>Compat:Legacy:Always</c>), and this suite is what holds it.
/// <para>
/// <c>Fixtures/legacy-1.1.json</c> holds the answer to every request of <c>Fixtures/requests</c> over
/// <see cref="LegacyFixture"/>'s documents, recorded from a build of the 1.1 query path with page
/// sizes 500 and 100 against the very database this suite seeds. The five <c>report-*</c> requests
/// have the shape of report queries (the entity ids and collection names are the fleet's, the record
/// id is the fixture's); the <c>consumer-*</c> requests have the shape of a planning client's
/// (shipments and templates of a plan pool, open orders) on the fleet's tours; the rest is what a
/// simple caller sends, one thing per request.
/// </para>
/// <para>
/// <see cref="G02_with_always_off_the_rewrite_answers_what_it_can_express_and_its_rows_are_the_engine_before_s"/>
/// holds the other path, <c>Compat:Legacy:Always=false</c>: the compatibility rewrite first, as in
/// the published 2.0 engine.
/// </para>
/// <para>
/// To record again: build a host around the 1.1 query path that takes <c>&lt;connection&gt; &lt;spec.json&gt;
/// &lt;out.json&gt;</c> and posts each case of the spec to its route, and run this class with
/// <c>OXQL_RECORD_LEGACY=&lt;path to that host's dll&gt;</c>. Only a build of the 1.1 query path is a reference.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class LegacyGoldenTests
{
    private const string RecordVariable = "OXQL_RECORD_LEGACY";

    /// <summary>A recorded request: its file, the service that answers it, and what of the answer is held.</summary>
    /// <param name="Ordered">False where the request's sort leaves rows tied (rows unwound from one document): then the rows are held as a set.</param>
    /// <param name="Rewritten">
    /// True where the compatibility rewrite can express the request: with <c>Compat:Legacy:Always</c> off the contract 2
    /// engine answers it in contract 1 spelling, as it has since 2.0. Its rows are then held against the recorded ones;
    /// its page info (the cursor's form, <c>totalCountCapped</c>) is that path's own and frozen by <c>Contract1HttpGoldenTests</c>.
    /// </param>
    /// <param name="Route">The route the request is posted to.</param>
    /// <param name="Host">True where the host answers before the engine sees a request (a body that is no request); then no path of the engine logs.</param>
    /// <param name="Bound">
    /// True where the recorded answer is the host's 400 for a body it could not bind to its typed stages. This host
    /// refuses it with the same status: when it binds the body, in its own problem details, or when the legacy
    /// pipeline reads it, in the legacy refusal envelope. The words are this host's.
    /// </param>
    /// <param name="Request">The request file of another case, where this one sends the same body another way.</param>
    /// <param name="Browser">
    /// True where the request carries what a newer client behind a browser adds (<c>Accept-Encoding</c>, <c>Origin</c>,
    /// <c>X-OxQL-Timing</c>): the answer is the same bytes under the same headers.
    /// </param>
    public sealed record Case(string Name, string Service = "transport", bool Ordered = true, bool Rewritten = false, string Route = "OxQL/query", bool Host = false, bool Bound = false, string? Request = null, bool Browser = false)
    {
        public override string ToString() => Name;
    }

    /// <summary>What a newer client behind a browser adds to a request.</summary>
    private static readonly IReadOnlyDictionary<string, string> BrowserHeaders = new Dictionary<string, string>
    {
        ["Accept-Encoding"] = "br, gzip, deflate",
        ["Origin"] = "https://grid.example",
        ["X-OxQL-Timing"] = "1",
    };

    /// <summary>Requests shaped as report queries and the rest of the contract 1 surface reports use; also held with <c>Always</c> off.</summary>
    public static readonly IReadOnlyList<Case> Reports =
    [
        // Report queries.
        new("report-invoice", "ledger", Ordered: false),
        new("report-invoice-pricing", "ledger", Ordered: false),
        new("report-invoice-tax-totals", "ledger", Rewritten: true),
        new("report-hr", "staff", Ordered: false),
        new("report-tour-plan", "transport"),
        // The rest of the surface.
        new("lookup-filter", "transport"),
        new("lookup-uuid-to-string", "staff"),
        new("lookup-organisation-as-legacy-uuid", "ledger"),
        new("unwind-index", "ledger"),
        new("resolve-ignored", "ledger"),
        new("page-1", "transport"),
        new("page-2", "transport"),
        new("group", "transport"),
        new("hints", "transport"),
        new("operators", "transport"),
        new("group-date-trunc", "transport"),
        new("decimal-hint", "ledger"),
        new("variables", "transport"),
        new("unmodelled-only", "ledger"),
        new("unwind-in-place-modelled", "ledger", Rewritten: true),
        new("plain-page", "ledger", Rewritten: true),
        new("whole-document", "staff"),
        new("invalid", "transport"),
        new("page-too-large", "transport"),
        new("unknown-type", "ledger"),
        new("cursor-invalid", "transport"),
        new("foreign-root", "ledger"),
    ];

    /// <summary>What a simple caller sends, one thing per request, and requests shaped as a planning client's.</summary>
    public static readonly IReadOnlyList<Case> Everyday =
    [
        // One operator each.
        new("op-eq"),
        new("op-eq-number"),
        new("op-eq-bool"),
        new("op-eq-null"),
        new("op-neq"),
        new("op-gt"),
        new("op-gte"),
        new("op-lt"),
        new("op-lte"),
        new("op-in"),
        new("op-nin"),
        new("op-contains"),
        new("op-contains-ignore-case"),
        new("op-contains-case-differs"),
        new("op-starts-with"),
        new("op-ends-with"),
        new("op-exists-true"),
        new("op-exists-false"),
        new("op-regex"),
        new("op-eq-ignore-case"),
        new("op-neq-ignore-case"),
        new("op-eq-guid-text"),
        new("op-in-guid-text"),
        new("op-unknown"),
        new("op-two-in-one-condition"),
        // and, or, not.
        new("logic-and-or-not"),
        new("logic-or"),
        new("logic-not"),
        // The match stage.
        new("match-two-paths-in-one-object"),
        new("match-empty"),
        new("match-twice"),
        // Paths: the key, a nested key, a spelling the model has and storage has not.
        new("path-id"),
        new("path-nested-underscore-id"),
        new("path-nested-id-as-modelled"),
        new("path-camel-case"),
        new("path-array-member"),
        // An operand of another kind than the stored member.
        new("operand-number-as-text"),
        new("operand-text-as-number"),
        new("operand-bool-as-text"),
        new("operand-date-as-text"),
        new("operand-in-not-an-array"),
        new("operand-contains-number"),
        new("operand-exists-text"),
        // Sort.
        new("sort-asc"),
        new("sort-desc"),
        new("sort-two-keys"),
        new("sort-date-desc"),
        new("sort-direction-upper-case"),
        new("sort-unstored-path"),
        // The page: a limit, an offset (no member of this page), the count.
        new("page-default"),
        new("page-limit"),
        new("page-offset"),
        new("page-count"),
        new("page-count-of-all"),
        new("page-count-number", Bound: true),
        new("page-max"),
        new("page-cursor-null"),
        new("page-empty-stage"),
        new("page-negative"),
        // A cursor round trip over several pages.
        new("cursor-page-1"),
        new("cursor-page-2"),
        new("cursor-page-3"),
        new("cursor-of-another-sort"),
        new("cursor-default-sort-1"),
        new("cursor-default-sort-2"),
        // No row.
        new("empty-result"),
        // No stage.
        new("pipeline-empty"),
        // Projection: inclusion, exclusion, both.
        new("project-include"),
        new("project-exclude"),
        new("project-include-and-exclude"),
        new("project-without-key"),
        new("project-modelled-id"),
        new("project-true"),
        new("project-unstored"),
        // Members and stages the pipeline does not know.
        new("unknown-request-members"),
        new("unknown-stage-members"),
        new("unknown-stage-kind"),
        new("unknown-lookup-form", Bound: true),
        new("unknown-unwind-members"),
        // The entity.
        new("entity-case"),
        new("entity-empty"),
        new("entity-missing", Host: true),
        new("entity-null", Host: true),
        // The pipeline member.
        new("pipeline-missing", Host: true),
        new("pipeline-null", Host: true),
        new("pipeline-not-an-array", Host: true),
        // A variable nothing binds.
        new("variables-unbound"),
        // A planning client's requests.
        new("consumer-plan-pool"),
        new("consumer-plan-pool-page-1"),
        new("consumer-plan-pool-page-2"),
        new("consumer-plan-pool-page-3"),
        new("consumer-plan-pool-filters"),
        new("consumer-templates"),
        new("consumer-cleaning-orders"),
        // A body that is no request: the host answers.
        new("body-truncated", Host: true),
        new("body-not-json", Host: true),
        new("body-empty", Host: true),
        new("body-null", Host: true),
        new("body-array", Host: true),
        new("body-trailing-comma", Host: true),
        new("body-comment", Host: true),
        new("body-member-names-in-another-case", Host: true),
        // A stage, a sort, a page, a match or a projection written as something else; stages written twice; unwind and group on their own.
        new("stage-null"),
        new("stage-null-after-a-stage"),
        new("stage-not-an-object", Bound: true),
        new("stage-match-null"),
        new("stage-sort-null"),
        new("stage-page-null"),
        new("stage-project-null"),
        new("stage-unwind-null"),
        new("stage-group-null"),
        new("stage-unwind-empty", Bound: true),
        new("sort-entry-null"),
        new("sort-entry-with-path-and-direction"),
        new("sort-entry-two-keys"),
        new("sort-not-an-array", Bound: true),
        new("page-limit-text"),
        new("page-limit-fraction", Bound: true),
        new("page-limit-null", Bound: true),
        new("page-cursor-number", Bound: true),
        new("page-cursor-empty"),
        new("page-count-null", Bound: true),
        new("page-count-text", Bound: true),
        new("page-twice"),
        new("sort-twice"),
        new("match-not-an-object", Bound: true),
        new("match-condition-not-an-object"),
        new("match-and-not-an-array", Bound: true),
        new("match-options-other"),
        new("project-text"),
        new("project-two"),
        new("project-empty"),
        new("unwind-plain"),
        new("unwind-as"),
        new("unwind-preserve-null"),
        new("unwind-preserve-null-text", Bound: true),
        new("group-count"),
        new("group-sum-min-max"),
        new("group-count-of-all"),
        new("lookup-collection-without-organisation"),
        new("lookup-collection-nobody-has"),
        // A request as a newer client behind a browser sends it: a page large enough to be content-coded, a count, a refusal.
        new("browser-page", Request: "consumer-plan-pool", Browser: true),
        new("browser-count", Request: "unknown-request-members", Browser: true),
        new("browser-refusal", Request: "page-negative", Browser: true),
    ];

    public static readonly IReadOnlyList<Case> Cases = [.. Reports, .. Everyday];

    public static TheoryData<string> Names() => [.. Cases.Select(entry => entry.Name)];

    public static TheoryData<string> ReportNames() => [.. Reports.Select(entry => entry.Name)];

    /// <summary>A host that answers what the rewrite can express by the rewrite, as the published 2.0 engine did.</summary>
    internal static readonly IReadOnlyDictionary<string, string?> RewriteFirst = new Dictionary<string, string?> { ["OxQL:Compat:Legacy:Always"] = "false" };

    private static string GoldenPath => Path.Combine(LegacyFixture.FixturesDirectory(), "legacy-1.1.json");

    private static readonly Lazy<Task<JsonObject>> Golden = new(LoadAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string RequestText(string name) =>
        File.ReadAllText(Path.Combine(LegacyFixture.FixturesDirectory(), "requests", (Cases.FirstOrDefault(entry => entry.Name == name)?.Request ?? name) + ".json"));

    /// <summary>The recorded answer to a case: <c>{ status, headers, body }</c>.</summary>
    internal static async Task<JsonObject> RecordedAsync(string name) =>
        (await Golden.Value)[name]?.AsObject() ?? throw new InvalidOperationException($"'{name}' is not recorded; record it from a build of the 1.1 query path with {RecordVariable}.");

    /// <summary>The request of a case; a cursor placeholder (<c>@cursor:&lt;case&gt;</c>) takes the next cursor of that case's answer in <paramref name="cursorOf"/>.</summary>
    internal static string Body(string name, Func<string, string?> cursorOf)
    {
        var text = RequestText(name);
        const string Token = "@cursor:";

        for (var at = text.IndexOf(Token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(Token, StringComparison.Ordinal))
        {
            var end = text.IndexOf('"', at);
            var from = text[(at + Token.Length)..end];

            text = text[..at] + (cursorOf(from) ?? throw new InvalidOperationException($"'{from}' answered no next cursor for '{name}'.")) + text[end..];
        }

        return text;
    }

    internal static async Task<WireAnswer> AnswerAsync(string name, LabClient? client = null, string? correlation = null)
    {
        var entry = Cases.First(candidate => candidate.Name == name);
        var recorded = await Golden.Value;

        client ??= await LegacyFixture.ClientAsync(LabService.Of(entry.Service));

        if (correlation is not null)
            client = client.WithHeader(LabIdentity.CorrelationHeader, correlation);

        if (entry.Browser)
            foreach (var (header, value) in BrowserHeaders)
                client = client.WithHeader(header, value);

        // A page after the first continues from the recorded cursor, which the case before it holds equal to this engine's.
        var body = Body(name, from => recorded[from]?["body"]?["pageInfo"]?["nextCursor"]?.GetValue<string>());

        return await client.PostAsync(entry.Route, body);
    }

    private static async Task<bool> LegacyAnsweredAsync(Case entry, string variant, IReadOnlyDictionary<string, string?>? configuration, string correlation)
    {
        var host = await (await LegacyFixture.ClientAsync(LabService.Of(entry.Service), variant, configuration)).HostAsync();

        return host.Logs.Entries.Any(line => line.Category == OxQL.AspNetCore.Legacy.LegacyRoute.LogCategory && line.Message.Contains(correlation, StringComparison.Ordinal));
    }

    /// <summary>
    /// The default: every request without the contract header is answered by the legacy pipeline, and
    /// the answer is the recorded one: the status, the envelope member for member, the page with its
    /// cursor text and its count, every row's JSON text in the recorded order, a refusal's type,
    /// title, status member, codes, messages and paths, and no header beyond the recorded ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task G01_a_contract_1_request_is_answered_as_the_engine_before_contract_2_answered_it(string name)
    {
        var entry = Cases.First(candidate => candidate.Name == name);
        var correlation = $"legacy-golden-{name}-{Guid.NewGuid():N}";
        var answer = await AnswerAsync(name, correlation: correlation);

        if (!entry.Host && !entry.Bound && entry.Route == "OxQL/query")
            (await LegacyAnsweredAsync(entry, "legacy", null, correlation)).Should().BeTrue($"a host with compatibility answers every contract 1 request by the legacy pipeline unless it is told otherwise; '{name}' got {answer}");

        Held(entry, await RecordedAsync(name), answer, whole: true);
    }

    /// <summary>The file of recorded answers holds exactly the cases, in their order.</summary>
    [Fact]
    public async Task G00_the_recorded_answers_are_those_of_the_cases()
    {
        (await Golden.Value).Select(member => member.Key).Should().Equal(Cases.Select(entry => entry.Name));
    }

    /// <summary>
    /// The other path, <c>Compat:Legacy:Always=false</c>: the compatibility rewrite answers what it can
    /// express, as in the published 2.0 engine, and the legacy pipeline the rest. Which of the two
    /// answers each of the report requests is held, and that the rewrite's rows are the recorded
    /// ones too; the rewrite's page info is its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReportNames))]
    public async Task G02_with_always_off_the_rewrite_answers_what_it_can_express_and_its_rows_are_the_engine_before_s(string name)
    {
        var entry = Reports.First(candidate => candidate.Name == name);
        var correlation = $"legacy-rewrite-{name}-{Guid.NewGuid():N}";
        var answer = await AnswerAsync(name, await LegacyFixture.ClientAsync(LabService.Of(entry.Service), "rewrite-first", RewriteFirst), correlation);

        (await LegacyAnsweredAsync(entry, "rewrite-first", RewriteFirst, correlation)).Should().Be(!entry.Rewritten, entry.Rewritten
            ? $"the rewrite can express '{name}', so the contract 2 engine answers it"
            : $"the rewrite cannot express '{name}', so the legacy pipeline answers it");

        Held(entry, await RecordedAsync(name), answer, whole: !entry.Rewritten);
    }

    /// <summary>Holds <paramref name="answer"/> against the recorded one; <paramref name="whole"/> is false where only the rows and the page's facts are held.</summary>
    private static void Held(Case entry, JsonObject recorded, WireAnswer answer, bool whole)
    {
        var name = entry.Name;

        answer.StatusCode.Should().Be(recorded["status"]!.GetValue<int>(), $"the status of '{name}' is the recorded one; got {answer}");

        if (entry.Bound)
        {
            answer.StatusCode.Should().Be(400, $"'{name}' is refused as recorded; got {answer}");

            return;
        }

        if (whole && recorded["headers"] is JsonObject headers)
        {
            // The in-memory test server sends no Date, Server or Transfer-Encoding; what it does send is among the recorded headers.
            answer.Headers.Keys.Should().BeSubsetOf(headers.Select(header => header.Key), $"'{name}' is answered with no header beyond the recorded ones");

            if (headers["Content-Type"] is { } type)
                answer.Headers.GetValueOrDefault("Content-Type").Should().Be(type.GetValue<string>());
        }

        if (recorded["body"] is not JsonObject was)
        {
            answer.Text.Should().Be(recorded["text"]?.GetValue<string>() ?? "", $"'{name}' is answered with the recorded body");

            return;
        }

        var now = answer.Body!.AsObject();

        if (answer.StatusCode != 200)
        {
            was = (JsonObject)was.DeepClone();
            now = (JsonObject)now.DeepClone();

            // The id of the request's trace is the request's own.
            was.Remove("traceId");
            now.Remove("traceId");

            // Of a fault the status, the type, the title and the code are held; its message says where the detail is.
            if (answer.StatusCode == 500)
            {
                foreach (var error in now["errors"]?.AsArray() ?? [])
                    error!.AsObject().Remove("message");
            }

            Canonical(now).Should().Be(Canonical(was), $"the refusal of '{name}' is the recorded one: its type, title, status member, codes, messages and paths");

            // A refusal of the engine (not the host's problem details, which carry the id of the request's trace) is the same bytes.
            if (whole && answer.StatusCode == 400 && recorded["body"]!["traceId"] is null)
                Sha256(answer.Text).Should().Be(recorded["sha256"]!.GetValue<string>(), $"the refusal of '{name}' is the recorded one, byte for byte");

            return;
        }

        now.Select(member => member.Key).Should().Equal(was.Select(member => member.Key), "the envelope has the recorded members, in their order");

        if (!whole)
        {
            now["pageInfo"]!["hasNextPage"]!.GetValue<bool>().Should().Be(was["pageInfo"]!["hasNextPage"]!.GetValue<bool>());
            now["pageInfo"]!["totalCount"]?.GetValue<long>().Should().Be(was["pageInfo"]!["totalCount"]?.GetValue<long>());
        }
        else
        {
            Canonical(now["pageInfo"]).Should().Be(Canonical(was["pageInfo"]), $"the page of '{name}': whether one follows, its cursor, the count");
        }

        var rowsNow = now["items"]!.AsArray().Select(Canonical).ToList();
        var rowsWas = was["items"]!.AsArray().Select(Canonical).ToList();

        if (entry.Ordered)
            rowsNow.Should().Equal(rowsWas, $"the rows of '{name}' are the recorded ones, member for member, in their order");
        else
            rowsNow.Order(StringComparer.Ordinal).Should().Equal(rowsWas.Order(StringComparer.Ordinal), $"the rows of '{name}' are the recorded ones, member for member");

        // And where the rows have one order, the body is the recorded one, byte for byte.
        if (whole && entry.Ordered)
            Sha256(answer.Text).Should().Be(recorded["sha256"]!.GetValue<string>(), $"the body of '{name}' is the recorded one, byte for byte ({recorded["length"]} bytes; this one has {System.Text.Encoding.UTF8.GetByteCount(answer.Text)})");
    }

    private static string Sha256(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>A node as text with its members in the order written and every number as written: equal text is an equal answer.</summary>
    private static string Canonical(JsonNode? node) => node?.ToJsonString() ?? "null";

    // ── the recording ───────────────────────────────────────────────────────────────────────────

    private static async Task<JsonObject> LoadAsync()
    {
        if (Environment.GetEnvironmentVariable(RecordVariable) is { Length: > 0 } recorder)
            await RecordAsync(recorder);

        return JsonNode.Parse(await File.ReadAllTextAsync(GoldenPath))!.AsObject();
    }

    /// <summary>Runs the recorder over this run's databases and writes what it answers.</summary>
    private static async Task RecordAsync(string recorder)
    {
        var fleet = await LegacyFixture.FleetAsync();
        var client = await MongoFixture.ClientAsync();
        var server = client.Settings.Server;
        var hosts = new JsonArray();

        foreach (var service in Cases.Select(entry => entry.Service).Distinct())
        {
            var lab = LabService.Of(service);
            var types = new JsonObject();

            foreach (var entity in lab.Model.Entities.Values)
                types[entity.Id] = entity.Collection;

            hosts.Add(new JsonObject
            {
                ["database"] = (await fleet.Fleet.DatabaseAsync(lab)).DatabaseNamespace.DatabaseName,
                ["organisation"] = LegacyFixture.Organisation.ToString("D"),
                ["types"] = types,
                ["cases"] = new JsonArray(Cases.Where(entry => entry.Service == service).Select(entry => (JsonNode)new JsonObject
                {
                    ["name"] = entry.Name,
                    ["route"] = "/" + entry.Route,
                    ["headers"] = new JsonObject((entry.Browser ? BrowserHeaders : new Dictionary<string, string>()).Select(header => KeyValuePair.Create(header.Key, (JsonNode?)header.Value))),
                    ["body"] = RequestText(entry.Name),
                }).ToArray()),
            });
        }

        var spec = Path.Combine(Path.GetTempPath(), $"oxql-legacy-spec-{Guid.NewGuid():N}.json");
        var output = Path.Combine(Path.GetTempPath(), $"oxql-legacy-out-{Guid.NewGuid():N}.json");

        await File.WriteAllTextAsync(spec, new JsonObject { ["hosts"] = hosts }.ToJsonString());

        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { recorder, $"mongodb://{server.Host}:{server.Port}/?directConnection=true", spec, output },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var errors = process.StandardError.ReadToEndAsync();
            var written = process.StandardOutput.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The recorder ended with {process.ExitCode}: {await errors} {await written}");

            // What the recorder logged while it answered, for whoever records.
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "oxql-legacy-recorder.log"), await written + await errors);

            var recorded = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject();
            var ordered = new JsonObject(Cases.Select(entry => KeyValuePair.Create(entry.Name, recorded[entry.Name]?.DeepClone())));

            // What differs from one recording to the next is no part of the answer: when it was given, and the id of its trace.
            foreach (var (_, answered) in ordered)
            {
                (answered?["headers"] as JsonObject)?.Remove("Date");

                if (answered?["body"] is JsonObject body && body.ContainsKey("traceId"))
                {
                    body["traceId"] = "(the request's)";

                    // The bytes of such a body hold the trace id: their hash is not compared.
                    answered.AsObject().Remove("sha256");
                    answered.AsObject().Remove("length");
                }

                // Of a fault the status, the type, the title and the code are held, and nothing else is kept.
                if (answered?["status"]?.GetValue<int>() == 500)
                {
                    answered.AsObject().Remove("sha256");
                    answered.AsObject().Remove("length");

                    foreach (var error in answered["body"]?["errors"] as JsonArray ?? [])
                        (error as JsonObject)?.Remove("message");
                }
            }

            // The file is LF whatever machine records it.
            await File.WriteAllTextAsync(GoldenPath, ordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ReplaceLineEndings("\n") + "\n");
        }
        finally
        {
            File.Delete(spec);
            File.Delete(output);
        }
    }
}
