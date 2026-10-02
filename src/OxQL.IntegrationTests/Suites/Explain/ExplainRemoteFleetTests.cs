using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Report;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// The remote side of explain over the fleet's real hosts (improvement plan §3.E "engine", §3.T T4, T5):
/// a round asks each owner once, the owners answer slim, a warm explain and the cached tier ask nobody,
/// the validator answers 304, and the limits hold under a flood. Each case explains from a ledger host
/// of its own (a variant), so what the host keeps of its owners' answers is the case's alone; the owners
/// are the fleet's standard hosts. The cases run one after another (<see cref="TimedCollection"/>):
/// they count calls and measure time.
/// </summary>
[Trait("Category", "Integration")]
[Collection(TimedCollection.Name)]
public class ExplainRemoteFleetTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyDictionary<string, string?> Unlimited = new Dictionary<string, string?>();

    /// <summary>The limits an engine has when a host configures none: what T5 is about.</summary>
    private static readonly IReadOnlyDictionary<string, string?> DefaultLimits = new Dictionary<string, string?>
    {
        ["OxQL:Explain:RatePerMinute"] = "20",
        ["OxQL:Explain:RateBurst"] = "5",
        ["OxQL:Explain:MaxConcurrentPerUser"] = "2",
        ["OxQL:Explain:MaxConcurrentPerHost"] = "8",
    };

    private static async Task<(LabClient Client, InMemoryRemoteClient Remote)> OriginAsync(string variant, IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var fleet = await CorpusFleet.SharedAsync();
        var client = fleet.Variant(LabService.Ledger, variant, configuration ?? Unlimited, Org.R);
        var host = await client.HostAsync();

        return (client, (InMemoryRemoteClient)host.Services.GetRequiredService<IRemoteQueryClient>());
    }

    private static JsonObject Request(string id) => ExplainGolden.Cases[id]();

    private static JsonObject Envelope(JsonObject query, string remote) => new() { ["query"] = query.DeepClone(), ["remote"] = remote };

    private static int Packed(string text, bool brotli)
    {
        using var packed = new MemoryStream();

        using (Stream coder = brotli ? new BrotliStream(packed, CompressionLevel.Fastest, leaveOpen: true) : new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            coder.Write(Encoding.UTF8.GetBytes(text));

        return (int)packed.Length;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToList();

        return ordered[ordered.Count / 2];
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToList();

        return ordered.Count == 0 ? 0 : ordered[Math.Min(ordered.Count - 1, (int)Math.Ceiling(ordered.Count * percentile) - 1)];
    }

    // ---- one call per owner and round ------------------------------------------------------------------

    [Theory]
    [InlineData("EX1-source-chain")]
    [InlineData("A4")]
    [InlineData("A5")]
    [InlineData("EX3-union-join")]
    public async Task E01_a_round_asks_each_owner_once_and_a_cold_explain_stays_within_eight_owner_calls(string id)
    {
        var (client, remote) = await OriginAsync($"p4-rounds-{id}");
        // A user nobody explained as: every owner along the way is asked.
        var user = Guid.NewGuid();
        var answer = await client.AsUser(user).ExplainHereAsync(Request(id));

        answer.Status.Should().Be(HttpStatusCode.OK, answer.Text);
        answer.Body!["valid"]!.GetValue<bool>().Should().BeTrue(answer.Text);
        answer.Body["cache"]!["complete"]!.GetValue<bool>().Should().BeTrue(answer.Text);

        var owners = answer.Body["owners"]!.AsArray().Select(owner => owner!.AsObject()).ToList();
        var direct = owners.Where(owner => owner["remote"]!.GetValue<bool>() && owner["via"] is null).ToList();
        var calls = remote.ExplainCalls.ToList();

        output.WriteLine($"{id}: owner calls of this host: {string.Join(", ", calls.Select(call => $"{call.Service}×{call.Batch.Checks.Count}"))}");
        output.WriteLine($"{id}: owners: {string.Join(", ", owners.Select(owner => $"{owner["service"]!.GetValue<string>()}{(owner["via"] is { } via ? "<" + via.GetValue<string>() : "")}:{owner["calls"]!.GetValue<int>()}"))}");

        // What the answer says a direct owner cost is what was sent to it, one call per round.
        foreach (var owner in direct)
        {
            var service = owner["service"]!.GetValue<string>();

            calls.Count(call => call.Service == service).Should().Be(owner["calls"]!.GetValue<int>(), $"'{service}' is asked once per round, whatever the round asks it");
            owner["calls"]!.GetValue<int>().Should().BeLessThanOrEqualTo(3, "a first ask and at most two ask-agains");
        }

        calls.Should().OnlyContain(call => call.Batch.Budget != null && call.Batch.Checks.All(check => check.Budget == null));
        owners.Where(owner => owner["remote"]!.GetValue<bool>()).Sum(owner => owner["calls"]!.GetValue<int>())
            .Should().BeLessThanOrEqualTo(8, "one explain causes at most eight owner calls, transitive ones included");

        // A call carries several checks, each a bind at its owner: the calls alone do not bound the owners'
        // work. A round asks an owner at most once per remote target of the request, so what one explain
        // has its owners bind is bounded by its remote targets and the three rounds, not by 8 calls x 64 checks.
        var remoteTargets = answer.Body["aliases"]!.AsObject()
            .Where(alias => alias.Value?["parentOf"] is null)
            .SelectMany(alias => alias.Value?["targets"]?.AsArray().OfType<JsonObject>() ?? [])
            .Count(target => target["remote"]!.GetValue<bool>());
        var binds = calls.Sum(call => call.Batch.Checks.Count);

        output.WriteLine($"{id}: {binds} checks bound at owners by this host in {calls.Count} calls, for {remoteTargets} remote targets");
        calls.Should().OnlyContain(call => call.Batch.Checks.Count <= remoteTargets, "a round asks an owner at most one check per remote target");
        binds.Should().BeInRange(1, 3 * remoteTargets, "one explain has its owners bind each remote target's query at most once per round");

        // The owners answered slim: what this host reads, and nothing it computes itself.
        var slim = calls.Sum(call => call.Bytes);
        var whole = 0;

        foreach (var call in calls)
            whole += await remote.WholeAnswerBytesAsync(call.Service, call.Batch, Org.R.Id(), user);

        output.WriteLine($"{id}: owner answers {slim} B slim, {whole} B written whole ({100.0 * slim / Math.Max(1, whole):F0} %)");
        slim.Should().BeLessThan(whole * 3 / 4, "an owner answers what its origin reads");
    }

    [Fact]
    public async Task E02_a_warm_explain_and_the_cached_tier_ask_no_owner_and_answer_what_the_check_answered()
    {
        var (origin, remote) = await OriginAsync("p4-warm");
        var client = origin.AsUser(Guid.NewGuid());
        var request = Request("A5");

        var check = await client.ExplainHereAsync(Envelope(request, "check"));
        var asked = remote.ExplainCalls.Count;

        check.Body!["cache"]!["complete"]!.GetValue<bool>().Should().BeTrue(check.Text);
        asked.Should().BeGreaterThan(0);

        var warm = await client.ExplainHereAsync(Envelope(request, "check"));
        var cached = await client.ExplainHereAsync(Envelope(request, "cached"));

        remote.ExplainCalls.Count.Should().Be(asked, "what the owners answered is kept: neither a second check nor the cached tier asks them");
        warm.Body!["owners"]!.AsArray().Where(owner => owner!["remote"]!.GetValue<bool>()).Should().OnlyContain(owner => owner!["calls"]!.GetValue<int>() == 0 && owner["cached"]!.GetValue<bool>());
        cached.Body!["cache"]!["complete"]!.GetValue<bool>().Should().BeTrue();
        cached.Body["etag"]!.GetValue<string>().Should().Be(check.Body["etag"]!.GetValue<string>());
        ExplainGolden.Normalise(cached.Body.DeepClone()).ToJsonString().Should().Be(ExplainGolden.Normalise(check.Body.DeepClone()).ToJsonString(), "the cached tier answers what the check answered");

        // Another user holds nothing: the cached tier says what it does not know and asks nobody.
        var stranger = await origin.AsUser(Guid.NewGuid()).ExplainHereAsync(Envelope(request, "cached"));

        remote.ExplainCalls.Count.Should().Be(asked);
        stranger.Status.Should().Be(HttpStatusCode.OK);
        stranger.Body!["valid"]!.GetValue<bool>().Should().BeTrue(stranger.Text);
        stranger.Body["cache"]!["complete"]!.GetValue<bool>().Should().BeFalse();
        stranger.Body["notes"]!.AsArray().Where(note => note!["code"]!.GetValue<string>() == "REMOTE_UNCHECKED").Should().NotBeEmpty()
            .And.OnlyContain(note => note!["params"]!["reason"]!.GetValue<string>() == "cached");
        stranger.Body["owners"]!.AsArray().Where(owner => owner!["remote"]!.GetValue<bool>()).Should().OnlyContain(owner => owner!["answered"] == null && owner["calls"]!.GetValue<int>() == 0);
        stranger.Body["etag"]!.GetValue<string>().Should().NotBe(check.Body["etag"]!.GetValue<string>(), "an answer that is not complete is another answer");
        stranger.Body["stages"]!.AsArray().Should().HaveCount(request["pipeline"]!.AsArray().Count, "what this host binds itself is all there");
    }

    [Fact]
    public async Task E03_a_run_that_learned_what_a_union_target_lacks_spares_the_next_explain_its_second_round()
    {
        var (origin, remote) = await OriginAsync("p4-drops");
        var request = Request("A1");

        var cold = await origin.AsUser(Guid.NewGuid()).ExplainHereAsync(request);

        cold.Body!["valid"]!.GetValue<bool>().Should().BeTrue(cold.Text);

        var transport = cold.Body["owners"]!.AsArray().Single(owner => owner!["service"]!.GetValue<string>() == "transport" && owner["via"] is null)!;
        var rounds = transport["calls"]!.GetValue<int>();
        var notes = cold.Body["notes"]!.AsArray().Where(note => note!["code"]!.GetValue<string>() == "SELECT_PATH_NOT_ON_TARGET").Select(note => note!.ToJsonString()).ToList();

        output.WriteLine($"A1 cold: transport asked in {rounds} round(s); {notes.Count} dropped path note(s)");

        if (notes.Count == 0)
            return;

        rounds.Should().Be(2, "the union's targets lack each other's paths, so the first explain asks again without them");

        // What the first explain learned is kept as a run keeps it: another user's explain (nothing kept of
        // the owners' answers for it) asks once and says the same.
        var warm = await origin.AsUser(Guid.NewGuid()).ExplainHereAsync(request);
        var again = warm.Body!["owners"]!.AsArray().Single(owner => owner!["service"]!.GetValue<string>() == "transport" && owner["via"] is null)!;

        again["calls"]!.GetValue<int>().Should().Be(1, "the paths a target lacks are dropped before the first round");
        warm.Body["notes"]!.AsArray().Where(note => note!["code"]!.GetValue<string>() == "SELECT_PATH_NOT_ON_TARGET").Select(note => note!.ToJsonString())
            .Should().BeEquivalentTo(notes, "what a target lacks is said whether it was learned now or before");
        ExplainGolden.Normalise(warm.Body.DeepClone()).ToJsonString().Should().Be(ExplainGolden.Normalise(cold.Body.DeepClone()).ToJsonString(), "the answer does not depend on what is kept");
    }

    // ---- the numbers of the tiers ------------------------------------------------------------------------

    [Theory]
    [InlineData("EX1-source-chain")]
    [InlineData("A5")]
    public async Task E04_the_tiers_measured_cold_check_warm_check_cached_and_not_modified(string id)
    {
        var (origin, remote) = await OriginAsync($"p4-tiers-{id}");
        var request = Request(id);
        var host = await origin.HostAsync();

        async Task<(double Ms, HttpResponseMessage Response, byte[] Body)> SendAsync(Guid user, string remoteTier, string? ifNoneMatch = null, string? acceptEncoding = null)
        {
            using var http = host.Server.CreateClient();
            using var message = new HttpRequestMessage(HttpMethod.Post, "OxQL/explain") { Content = new StringContent(Envelope(request, remoteTier).ToJsonString(), Encoding.UTF8, "application/json") };

            message.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, "2");
            message.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, Org.R.Id().ToString("D"));
            message.Headers.TryAddWithoutValidation(LabIdentity.UserHeader, user.ToString("D"));

            if (ifNoneMatch is not null)
                message.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

            if (acceptEncoding is not null)
                message.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

            var watch = Stopwatch.StartNew();
            var response = await http.SendAsync(message);
            var body = await response.Content.ReadAsByteArrayAsync();

            return (watch.Elapsed.TotalMilliseconds, response, body);
        }

        // Warm the code paths, as a host that has answered before.
        for (var warmup = 0; warmup < 10; warmup++)
            await SendAsync(Guid.NewGuid(), "check");

        // Cold check: a user nothing is kept for, so every owner is asked.
        var cold = new List<double>();
        var calls = remote.ExplainCalls.Count;

        for (var run = 0; run < 15; run++)
            cold.Add((await SendAsync(Guid.NewGuid(), "check")).Ms);

        var perCold = (remote.ExplainCalls.Count - calls) / 15.0;

        // Cached tier with nothing kept: no owner is asked.
        var bare = new List<double>();
        var bareBytes = 0;

        calls = remote.ExplainCalls.Count;

        for (var run = 0; run < 15; run++)
        {
            var sent = await SendAsync(Guid.NewGuid(), "cached");

            bare.Add(sent.Ms);
            bareBytes = sent.Body.Length;
        }

        remote.ExplainCalls.Count.Should().Be(calls, "the cached tier asks no owner");

        // One user: the check once, then warm checks, the cached tier, and the validator.
        var user = Guid.NewGuid();
        var first = await SendAsync(user, "check");
        var etag = first.Response.Headers.ETag!.ToString();
        var warm = new List<double>();
        var cached = new List<double>();
        var notModified = new List<double>();

        calls = remote.ExplainCalls.Count;

        for (var run = 0; run < 25; run++)
        {
            warm.Add((await SendAsync(user, "check")).Ms);
            cached.Add((await SendAsync(user, "cached")).Ms);

            var revalidated = await SendAsync(user, "cached", ifNoneMatch: etag);

            revalidated.Response.StatusCode.Should().Be(HttpStatusCode.NotModified);
            revalidated.Body.Should().BeEmpty();
            notModified.Add(revalidated.Ms);
        }

        remote.ExplainCalls.Count.Should().Be(calls, "a kept answer is not asked for again");

        // The validator is one identity's: another user who sends it for the same request is answered, not told 304.
        var foreign = await SendAsync(Guid.NewGuid(), "check", ifNoneMatch: etag);

        foreign.Response.StatusCode.Should().Be(HttpStatusCode.OK, "an owner may answer another user differently, so one user's validator never stands for another's answer");
        foreign.Response.Headers.ETag!.ToString().Should().NotBe(etag);

        var brotli = await SendAsync(user, "cached", acceptEncoding: "br");
        var gzip = await SendAsync(user, "cached", acceptEncoding: "gzip");

        brotli.Response.Content.Headers.ContentEncoding.Should().Equal("br");
        gzip.Response.Content.Headers.ContentEncoding.Should().Equal("gzip");

        output.WriteLine($"{id} ({request["pipeline"]!.AsArray().Count} stages), median ms over HTTP in process:");
        output.WriteLine($"  check, cold (owners asked, {perCold:F1} owner calls each): {Median(cold):F2} ms");
        output.WriteLine($"  check, warm (owner answers kept):                {Median(warm):F2} ms");
        output.WriteLine($"  cached, nothing kept (incomplete):               {Median(bare):F2} ms, {bareBytes} B");
        output.WriteLine($"  cached, kept (complete):                         {Median(cached):F2} ms");
        output.WriteLine($"  cached + If-None-Match (304):                    {Median(notModified):F2} ms, 0 B");
        output.WriteLine($"  answer: {first.Body.Length} B raw, {gzip.Body.Length} B gzip, {brotli.Body.Length} B br (fastest level)");

        Median(bare).Should().BeLessThan(Median(cold), "the cached tier asks no owner, so it answers before a cold check does");
        brotli.Body.Length.Should().BeLessThan(first.Body.Length / 4);
        gzip.Body.Length.Should().BeLessThan(first.Body.Length / 4);
    }

    // ---- T5: the limits under a flood --------------------------------------------------------------------

    /// <summary>
    /// T5 (improvement plan §3.T), as far as one process can show it: clients of five users flood one
    /// host with valid explains near the largest body, oversized ones and ones past the stage bound,
    /// while queries run beside them.
    /// <para>
    /// <b>What this test asserts</b> is what holds whatever the machine does:
    /// every explain is answered 200, 429, 413 or 400 and each kind of body gets its own refusal; a 429
    /// carries <c>EXPLAIN_LIMIT</c> and <c>Retry-After</c>; no user is admitted more than its burst and
    /// its rate; no explain causes more than eight owner calls, and the flood's explains have the owners
    /// bind no more checks each than one cold explain of the request does; every query beside the flood
    /// is answered 200; and what the hosts keep because of explains stays within its bounds (the kept
    /// owner answers by number and bytes, the kept owner rows by number), so nothing grows with the
    /// number of explains sent.
    /// </para>
    /// <para>
    /// <b>What this test does not show</b> are the plan's latency and memory criteria: refusals p99
    /// under 1 ms, the queries' p95 within +10 % of idle, the working set flat within 10 %. The flood's
    /// clients run in this process on the same cores as the host, so the times measure their contention
    /// and the memory is the test's as much as the host's. The figures are printed, not asserted; those
    /// criteria need separate load generators against a deployed host (a lab run) and are open until one
    /// is done. The managed-memory assertion here is a tripwire against a leak per explain, far above
    /// what a run measures, not the plan's bound.
    /// </para>
    /// The flood lasts <c>OXQL_LOAD_SECONDS</c> seconds (3 by default) with <c>OXQL_LOAD_CLIENTS</c> clients (50).
    /// </summary>
    [Fact]
    public async Task T5_a_flood_of_explains_is_refused_at_the_limits_and_takes_nothing_else_down()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("OXQL_LOAD_SECONDS"), out var configured) ? configured : 3;
        var clients = int.TryParse(Environment.GetEnvironmentVariable("OXQL_LOAD_CLIENTS"), out var count) ? count : 50;
        var users = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var (origin, remote) = await OriginAsync("p4-load", DefaultLimits);
        var host = await origin.HostAsync();
        var fleet = await CorpusFleet.SharedAsync();
        var owners = new List<InMemoryRemoteClient>();
        var engines = new List<MongoQueryEngine> { (MongoQueryEngine)host.Services.GetRequiredService<IQueryEngine>() };

        foreach (var service in LabService.All)
        {
            var owner = await fleet.Fleet.HostAsync(service);

            owners.Add((InMemoryRemoteClient)owner.Services.GetRequiredService<IRemoteQueryClient>());
            engines.Add((MongoQueryEngine)owner.Services.GetRequiredService<IQueryEngine>());
        }

        // The checks bound at owners, wherever they are sent from: this host's and the owners' own.
        int OwnerCalls() => owners.Sum(owner => owner.ExplainCalls.Count) + remote.ExplainCalls.Count;
        int OwnerBinds() => owners.Sum(owner => owner.ExplainCalls.Sum(call => call.Batch.Checks.Count)) + remote.ExplainCalls.Sum(call => call.Batch.Checks.Count);

        // A valid explain near the largest body: the reference chain with a long list in its first condition.
        var valid = Request("A5");
        var padding = new JsonArray(Enumerable.Range(0, 1_500).Select(index => (JsonNode)Guid.NewGuid().ToString("D")).ToArray());

        valid["pipeline"]!.AsArray().Insert(1, new JsonObject { ["match"] = new JsonObject { ["id"] = new JsonObject { ["in"] = padding } } });

        var validBody = valid.ToJsonString();
        var oversized = new JsonObject { ["entityType"] = "ledger.transaction", ["pipeline"] = new JsonArray(), ["variables"] = new JsonObject { ["pad"] = new string('x', 70_000) } }.ToJsonString();
        var tooLong = new JsonObject
        {
            ["entityType"] = "ledger.transaction",
            ["pipeline"] = new JsonArray(Enumerable.Range(0, 31).Select(_ => (JsonNode)new JsonObject { ["match"] = new JsonObject { ["number"] = new JsonObject { ["eq"] = "a" } } }).ToArray()),
        }.ToJsonString();

        validBody.Length.Should().BeInRange(50_000, 65_000, "a valid body near the 64 KB an explain may carry");
        oversized.Length.Should().BeGreaterThan(65_536);

        async Task<(HttpStatusCode Status, double Ms, JsonObject? Body, string? RetryAfter)> ExplainAsync(Guid user, string body)
        {
            using var http = host.Server.CreateClient();
            using var message = new HttpRequestMessage(HttpMethod.Post, "OxQL/explain") { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            message.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, "2");
            message.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, Org.R.Id().ToString("D"));
            message.Headers.TryAddWithoutValidation(LabIdentity.UserHeader, user.ToString("D"));

            var watch = Stopwatch.StartNew();
            using var response = await http.SendAsync(message);
            var text = await response.Content.ReadAsStringAsync();
            var ms = watch.Elapsed.TotalMilliseconds;

            return (response.StatusCode, ms, Json.TryParse(text) as JsonObject, response.Headers.TryGetValues("Retry-After", out var retry) ? retry.First() : null);
        }

        // The queries beside the flood: a report scenario on the service's standard host, one after another.
        var runner = await ReportScenarios.ClientAsync(Request("A1"));

        async Task<List<double>> RunAsync(CancellationToken until, int? fixedRuns = null)
        {
            var times = new List<double>();

            while (fixedRuns is null ? !until.IsCancellationRequested : times.Count < fixedRuns)
            {
                var watch = Stopwatch.StartNew();
                var answer = await runner.QueryAsync(Request("A1"));

                answer.Status.Should().Be(HttpStatusCode.OK, answer.Text);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            return times;
        }

        // Precondition: the valid body is a valid explain, and the host and the queries are warm. It is a
        // cold explain (a user nothing is kept for): what it has the owners bind is the most one explain of it does.
        var bindsBeforeProbe = OwnerBinds();
        var probe = await ExplainAsync(Guid.NewGuid(), validBody);
        var coldBinds = OwnerBinds() - bindsBeforeProbe;

        probe.Status.Should().Be(HttpStatusCode.OK, probe.Body?.ToJsonString());
        probe.Body!["valid"]!.GetValue<bool>().Should().BeTrue(probe.Body.ToJsonString());
        coldBinds.Should().BeGreaterThan(0, "the valid explain reaches owners");
        await RunAsync(CancellationToken.None, 20);

        var idle = await RunAsync(CancellationToken.None, 60);

        host.Logs.Entries.Clear();

        foreach (var service in LabService.All)
            (await fleet.Fleet.HostAsync(service)).Logs.Entries.Clear();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var ownerCallsBefore = OwnerCalls();
        var ownerBindsBefore = OwnerBinds();
        using var flood = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var beside = RunAsync(flood.Token);

        // Each client counts what it gets as it gets it: nothing is kept per explain, so what the memory
        // holds after the flood is the hosts', not this test's.
        var tallies = Enumerable.Range(0, clients).Select(index => new Tally(users[index % users.Count])).ToList();

        await Task.WhenAll(tallies.Select(async (tally, index) =>
        {
            // Two of three send the valid body; the others an oversized one or one past the stage bound.
            for (var turn = 0; !flood.IsCancellationRequested; turn++)
            {
                var kind = ((index + turn) % 6) switch { 0 => "oversized", 1 => "tooLong", _ => "valid" };
                var sent = await ExplainAsync(tally.User, kind switch { "oversized" => oversized, "tooLong" => tooLong, _ => validBody });

                tally.Count(kind, sent.Status, sent.Ms, sent.Body, sent.RetryAfter is not null);
            }
        }));

        var busy = await beside;
        var ownerCalls = OwnerCalls() - ownerCallsBefore;
        var ownerBinds = OwnerBinds() - ownerBindsBefore;
        var seen = Tally.Sum(tallies);

        // The hosts of this suite keep every log line for the cases that read them; that is the harness's, not the engine's.
        var logged = host.Logs.Entries.Count;

        host.Logs.Entries.Clear();

        foreach (var service in LabService.All)
            (await fleet.Fleet.HostAsync(service)).Logs.Entries.Clear();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var allowance = 5 + (int)Math.Ceiling(20 * (seconds + 1) / 60.0);

        output.WriteLine($"T5: {clients} clients, {users.Count} users, {seconds} s: {seen.Total} explains ({seen.Total / (double)seconds:F0}/s); {string.Join(", ", seen.ByStatus.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}: {pair.Value}"))}");
        output.WriteLine($"  429 by limit: {string.Join(", ", seen.ByLimit.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}"))}");
        output.WriteLine($"  answered 200: {seen.Answered} ({string.Join(", ", users.Select(user => tallies.Where(tally => tally.User == user).Sum(tally => tally.Answered)))} per user); owner calls caused: {ownerCalls} ({(seen.Answered == 0 ? 0 : ownerCalls / (double)seen.Answered):F2} per answered explain, at most {seen.MostOwnerCalls} by one); checks bound at owners: {ownerBinds} (a cold explain: {coldBinds})");
        output.WriteLine($"  NOT ASSERTED (in process, the flood shares the cores): refusals p50 {seen.Refusals.Percentile(0.5):F2} ms, p99 {seen.Refusals.Percentile(0.99):F2} ms; 413 alone: p50 {seen.Large.Percentile(0.5):F2} ms, p99 {seen.Large.Percentile(0.99):F2} ms");
        output.WriteLine($"  NOT ASSERTED: queries beside the flood: {busy.Count} runs, p50 {Percentile(busy, 0.5):F2} ms, p95 {Percentile(busy, 0.95):F2} ms; idle: p50 {Percentile(idle, 0.5):F2} ms, p95 {Percentile(idle, 0.95):F2} ms");
        output.WriteLine($"  kept by the {engines.Count} hosts: owner answers {string.Join(", ", engines.Select(engine => $"{engine.ExplainCache.Count}/{engine.ExplainCache.Bytes / 1024} KB"))} (at most {ExplainForwardCache.MaxEntries}/{ExplainForwardCache.MaxBytes / 1024} KB each); owner rows and drops {string.Join(", ", engines.Select(engine => engine.OwnerCache.Count))}");

        var admitted = users.Select(user => tallies.Where(tally => tally.User == user).Sum(tally => tally.Admitted)).ToList();

        output.WriteLine($"  admitted per user: {string.Join(", ", admitted)} (allowance {allowance})");
        output.WriteLine($"  managed memory: {memoryBefore / 1_048_576.0:F1} MB before, {memoryAfter / 1_048_576.0:F1} MB after ({100.0 * (memoryAfter - memoryBefore) / memoryBefore:+0.0;-0.0} %); {logged} log lines of the flooded host dropped first");

        // Every explain got one of the four answers, and each kind its own.
        seen.ByStatus.Keys.Should().BeSubsetOf([200, 400, 413, 429], "an explain is answered 200, 429, 413 or 400");
        seen.Misplaced.Should().Be(0, "an oversized body is 413 and one past the stage bound 400 EXPLAIN_LIMIT, unless the limiter refused it before it was looked at");
        seen.ByStatus.GetValueOrDefault(429).Should().BeGreaterThan(0, "the flood exceeds what five users may send");
        seen.LimitedWithoutRetryAfter.Should().Be(0, "a refused explain says when to come back, with the code EXPLAIN_LIMIT");

        // The rate: per user the burst and what the bucket refilled in the time, whatever was sent
        // (every kind of body takes a token: the limiter admits before the body is read).
        admitted.Should().OnlyContain(each => each <= allowance, "a user is admitted its burst and its rate, no more");

        // No amplification: what the admitted explains caused at the owners, in calls and in the checks the calls carry.
        seen.MostOwnerCalls.Should().BeLessThanOrEqualTo(8);
        ownerCalls.Should().BeLessThanOrEqualTo(8 * seen.Answered, "an explain causes at most eight owner calls, counted where they are sent");
        ownerBinds.Should().BeLessThanOrEqualTo(coldBinds * seen.Answered, "an explain has the owners bind no more than a cold explain of the request does, counted where the checks are sent");

        // The queries beside the flood were all answered (each run asserts its 200).
        busy.Should().NotBeEmpty("queries ran beside the flood");

        // Nothing a host keeps because of explains grows with the number of explains: each store is
        // within its own bound after the flood, whatever was sent.
        foreach (var engine in engines)
        {
            engine.ExplainCache.Count.Should().BeLessThanOrEqualTo(ExplainForwardCache.MaxEntries);
            engine.ExplainCache.Bytes.Should().BeLessThanOrEqualTo(ExplainForwardCache.MaxBytes);
            engine.OwnerCache.Count.Should().BeLessThanOrEqualTo(3 * Math.Max(1, new OxQL.Core.Models.OxQLOptions().Cache.OwnerFetchCacheMaxEntries), "rows, key lists and drops each within the configured entries");
        }

        // A tripwire, not the plan's bound: a leak per explain sent would show as hundreds of megabytes
        // (the flood sends some 50 000 explains a second); what a run measures is a tenth of this.
        (memoryAfter - memoryBefore).Should().BeLessThan(256L * 1_048_576, "nothing is kept per explain sent");
    }

    /// <summary>Times in buckets of a twentieth of a millisecond up to a second: enough for a percentile, and of a fixed size.</summary>
    private sealed class Times
    {
        private readonly int[] buckets = new int[20_001];
        private int count;

        public void Add(double ms)
        {
            buckets[(int)Math.Clamp(ms * 20, 0, buckets.Length - 1)]++;
            count++;
        }

        public void Add(Times other)
        {
            for (var index = 0; index < buckets.Length; index++)
                buckets[index] += other.buckets[index];

            count += other.count;
        }

        /// <summary>The upper edge of the bucket the percentile falls in, in milliseconds; 0 when nothing was added.</summary>
        public double Percentile(double percentile)
        {
            var rank = (int)Math.Ceiling(count * percentile);

            for (int index = 0, below = 0; index < buckets.Length; index++)
                if ((below += buckets[index]) >= rank && rank > 0)
                    return (index + 1) / 20.0;

            return 0;
        }
    }

    /// <summary>What one client of a flood got, counted as it came: nothing refers to an answer.</summary>
    private sealed class Tally(Guid user)
    {
        public Guid User { get; } = user;

        public int Total { get; private set; }

        public Dictionary<int, int> ByStatus { get; } = [];

        public Dictionary<string, int> ByLimit { get; } = new(StringComparer.Ordinal);

        /// <summary>The explains answered 200.</summary>
        public int Answered { get; private set; }

        /// <summary>The explains the limiter let through, whatever became of them.</summary>
        public int Admitted { get; private set; }

        public int MostOwnerCalls { get; private set; }

        public Times Refusals { get; } = new();

        public Times Large { get; } = new();

        /// <summary>The bodies answered with another refusal than their own: an oversized one not 413, one past the stage bound not 400 <c>EXPLAIN_LIMIT</c>.</summary>
        public int Misplaced { get; private set; }

        public int LimitedWithoutRetryAfter { get; private set; }

        public void Count(string kind, HttpStatusCode status, double ms, JsonObject? body, bool retryAfter)
        {
            var error = (body?["errors"] as JsonArray)?.FirstOrDefault();
            var code = error?["code"]?.GetValue<string>();

            Total++;
            ByStatus[(int)status] = ByStatus.GetValueOrDefault((int)status) + 1;

            if (status != HttpStatusCode.TooManyRequests)
                Admitted++;

            switch (status)
            {
                case HttpStatusCode.OK:
                    Answered++;
                    MostOwnerCalls = Math.Max(MostOwnerCalls, body!["owners"]!.AsArray().Where(owner => owner!["remote"]!.GetValue<bool>()).Sum(owner => owner!["calls"]!.GetValue<int>()));
                    return;

                case HttpStatusCode.TooManyRequests:
                    var limit = error?["params"]?["limit"]?.GetValue<string>() ?? "?";

                    ByLimit[limit] = ByLimit.GetValueOrDefault(limit) + 1;

                    if (!retryAfter || code != "EXPLAIN_LIMIT")
                        LimitedWithoutRetryAfter++;
                    break;

                case HttpStatusCode.RequestEntityTooLarge:
                    Large.Add(ms);

                    if (kind != "oversized")
                        Misplaced++;
                    break;

                case HttpStatusCode.BadRequest:
                    if (kind != "tooLong" || code != "EXPLAIN_LIMIT")
                        Misplaced++;
                    break;
            }

            if ((kind == "oversized" && status is not (HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.TooManyRequests))
                || (kind == "tooLong" && status is not (HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests)))
                Misplaced++;

            Refusals.Add(ms);
        }

        /// <summary>The clients' counts together.</summary>
        public static Tally Sum(IEnumerable<Tally> tallies)
        {
            var sum = new Tally(Guid.Empty);

            foreach (var tally in tallies)
            {
                sum.Total += tally.Total;
                sum.Answered += tally.Answered;
                sum.Admitted += tally.Admitted;
                sum.MostOwnerCalls = Math.Max(sum.MostOwnerCalls, tally.MostOwnerCalls);
                sum.Misplaced += tally.Misplaced;
                sum.LimitedWithoutRetryAfter += tally.LimitedWithoutRetryAfter;
                sum.Refusals.Add(tally.Refusals);
                sum.Large.Add(tally.Large);

                foreach (var (status, count) in tally.ByStatus)
                    sum.ByStatus[status] = sum.ByStatus.GetValueOrDefault(status) + count;

                foreach (var (limit, count) in tally.ByLimit)
                    sum.ByLimit[limit] = sum.ByLimit.GetValueOrDefault(limit) + count;
            }

            return sum;
        }
    }
}
