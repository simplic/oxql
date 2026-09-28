using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Chain;

/// <summary>
/// The owner failure modes of the keyed fetch (DESIGN §3.5.2, §3.5.4, §3.5.6, §3.6), each outside
/// strict, under <c>onMissing: "report"</c> and under <c>strict</c>: an owner that cannot be reached
/// or does not answer in time, an owner that cuts its answer (<c>hasNextPage</c>), the request's key
/// budget, a negative cache entry a strict request does not read, an owner batch cap below this
/// host's, the chain ceiling, and what a forwarded query carries. The owner is a
/// <see cref="ScriptedOwner"/> of <c>owner.widget</c>; organisation A's rows name W-1, W-2, W-3.
/// Outside strict an owner failure never fails the page; <c>report</c> lists every row it left
/// without an answer as <c>owner_unanswered</c>; strict refuses with the failure's code.
/// </summary>
[Trait("Category", "Integration")]
public class ChainOwnerFailureTests : IClassFixture<ScriptedOwnerFleet>
{
    private const string Report = """, "onMissing": "report" """;

    private static readonly IReadOnlyList<string> Widgets = ["W-1", "W-2", "W-3"];

    private readonly ScriptedOwnerFleet fleet;

    public ChainOwnerFailureTests(ScriptedOwnerFleet fleet)
    {
        this.fleet = fleet;
        fleet.Owner.Reset();
    }

    private ScriptedOwner Owner => fleet.Owner;

    private static IReadOnlyList<(int Row, string? Key, string? Outcome)> RowsOf(JsonObject diagnostic) =>
        diagnostic["params"]!["rows"]!.AsArray().Select(row => (row!["row"]!.GetValue<int>(), row["key"]?.GetValue<string>(), row["outcome"]?.GetValue<string>())).ToList();

    /// <summary>The three legs of one failure: plain, <c>onMissing: "report"</c>, <c>strict</c>.</summary>
    private static async Task<(WireAnswer Plain, WireAnswer Reported, WireAnswer Strict)> LegsAsync(FleetHost host, string caseId) =>
        (await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve(caseId + "-plain")),
         await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve(caseId + "-report", extra: Report)),
         await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve(caseId + "-strict", strict: true)));

    [Fact]
    public async Task F01_an_unreachable_owner_nulls_the_alias_reports_every_row_owner_unanswered_and_is_refused_under_strict()
    {
        Owner.Status = HttpStatusCode.ServiceUnavailable;

        var (plain, reported, strict) = await LegsAsync(await fleet.HostAsync(), "F01");

        plain.ShouldBeOk().DiagnosticCodes.Should().Equal("RESOLVE_UNREACHABLE");
        plain.Values("w").Should().OnlyContain(alias => alias == null);

        reported.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_UNREACHABLE", "RESOLVE_MISSING"], reported.ToString());
        RowsOf(reported.ShouldHaveDiagnostic("RESOLVE_MISSING")).Should().Equal((0, "W-1", "owner_unanswered"), (1, "W-2", "owner_unanswered"), (2, "W-3", "owner_unanswered"));

        strict.ShouldRefuse("RESOLVE_UNREACHABLE", 422)["params"]!["service"]!.GetValue<string>().Should().Be(LabFleet.ExternalOwner);
        strict.Type.Should().Be("not_executable");
        strict.ErrorCodes.Should().Equal(["RESOLVE_UNREACHABLE", "RESOLVE_MISSING"]);
    }

    [Fact]
    public async Task F02_an_owner_slower_than_the_resolve_budget_is_RESOLVE_TIMEOUT_with_every_row_owner_unanswered_and_refused_under_strict()
    {
        Owner.Delay = TimeSpan.FromSeconds(30);
        var host = await fleet.HostAsync("resolve-300", new Dictionary<string, string?> { ["OxQL:Execution:ResolveTimeoutMs"] = "300" });

        var (plain, reported, strict) = await LegsAsync(host, "F02");

        plain.ShouldBeOk().DiagnosticCodes.Should().Equal("RESOLVE_TIMEOUT");
        plain.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5), "the resolve budget ends the wait, not the owner");

        reported.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_TIMEOUT", "RESOLVE_MISSING"], reported.ToString());
        RowsOf(reported.ShouldHaveDiagnostic("RESOLVE_MISSING")).Select(row => row.Outcome).Should().Equal("owner_unanswered", "owner_unanswered", "owner_unanswered");

        strict.ShouldRefuse("RESOLVE_TIMEOUT", 422);
        strict.ErrorCodes.Should().Equal(["RESOLVE_TIMEOUT", "RESOLVE_MISSING"], "strict refuses on the failure and, its onMissing being refuse, on the rows it left unanswered");
    }

    [Fact]
    public async Task F03_an_owner_answer_with_a_next_page_is_RESOLVE_PARTIAL_its_cut_keys_owner_unanswered_never_not_found_and_never_cached()
    {
        Owner.Cut = 2;
        var host = await fleet.HostAsync();
        var mark = Owner.Batches.Count;

        var (plain, reported, strict) = await LegsAsync(host, "F03");

        plain.ShouldBeOk().Strings("w.name").Should().Equal("Widget One", "Widget Two", null);
        plain.DiagnosticCodes.Should().Equal("RESOLVE_PARTIAL");
        plain.Diagnostics[0]["params"]!.ToJsonString().Should().Be("""{"alias":"w","keys":3,"unanswered":1,"service":"owner"}""");

        reported.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_PARTIAL", "RESOLVE_MISSING"], reported.ToString());
        RowsOf(reported.ShouldHaveDiagnostic("RESOLVE_MISSING")).Should().Equal((2, "W-3", "owner_unanswered"));

        strict.ShouldRefuse("RESOLVE_PARTIAL", 422)["params"]!["unanswered"]!.GetValue<int>().Should().Be(1);
        strict.ErrorCodes.Should().Equal(["RESOLVE_PARTIAL", "RESOLVE_MISSING"]);

        // A cut chunk is not kept: the same request asks the owner again, and an honest owner now answers it whole.
        Owner.Cut = null;
        var before = Owner.Batches.Count;
        var again = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F03-plain"));

        again.ShouldBeOk().ShouldHaveNoDiagnostics();
        again.Strings("w.name").Should().Equal("Widget One", "Widget Two", "Widget Three");
        Owner.Batches.Count.Should().Be(before + 1, "no key of a cut chunk was served from the cache");
        Owner.Since(mark).Should().NotBeEmpty();
    }

    [Fact]
    public async Task F04_a_page_needing_more_keys_than_MaxResolveKeys_is_RESOLVE_PARTIAL_the_rest_owner_unanswered_and_refused_under_strict()
    {
        var host = await fleet.HostAsync("keys-2", new Dictionary<string, string?> { ["OxQL:Limits:MaxResolveKeys"] = "2" });

        var (plain, reported, strict) = await LegsAsync(host, "F04");

        plain.ShouldBeOk().Values("w").Count(alias => alias is not null).Should().Be(2);
        plain.DiagnosticCodes.Should().Equal("RESOLVE_PARTIAL");
        plain.Diagnostics[0]["params"]!.ToJsonString().Should().Be("""{"alias":"w","keys":3,"max":2}""");

        reported.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_PARTIAL", "RESOLVE_MISSING"], reported.ToString());
        RowsOf(reported.ShouldHaveDiagnostic("RESOLVE_MISSING")).Should().Equal((2, "W-3", "owner_unanswered"));

        strict.ShouldRefuse("RESOLVE_PARTIAL", 422)["params"]!["max"]!.GetValue<int>().Should().Be(2);
        strict.ErrorCodes.Should().Equal(["RESOLVE_PARTIAL", "RESOLVE_MISSING"]);
    }

    [Fact]
    public async Task F05_a_missing_widget_is_null_reported_not_found_and_refused_under_strict_and_strict_never_reads_its_negative_cache_entry()
    {
        var host = await fleet.HostAsync();
        Owner.Hidden["W-2"] = true;

        var plain = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F05-plain"));
        plain.ShouldBeOk().ShouldHaveNoDiagnostics("onMissing null reports only owner failures");
        plain.Strings("w.name").Should().Equal("Widget One", null, "Widget Three");

        var reported = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F05", extra: Report));
        RowsOf(reported.ShouldBeOk().ShouldHaveDiagnostic("RESOLVE_MISSING")).Should().Equal((1, "W-2", "not_found"));

        (await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F05-strict", strict: true))).ShouldRefuse("RESOLVE_MISSING", 422);

        // The widget appears. Outside strict the negative entry answers for its TTL; strict asks the owner.
        Owner.Hidden.Clear();
        var mark = Owner.Batches.Count;

        var cached = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F05", extra: Report));
        RowsOf(cached.ShouldBeOk().ShouldHaveDiagnostic("RESOLVE_MISSING")).Should().Equal((1, "W-2", "not_found"));
        Owner.Batches.Count.Should().Be(mark, "every key of the repeat is in the cache, the missing one as a negative entry");

        var strict = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F05", strict: true, extra: Report));
        strict.ShouldBeOk().ShouldHaveNoDiagnostics();
        strict.Strings("w.name").Should().Equal("Widget One", "Widget Two", "Widget Three");

        var asked = Owner.Since(mark);
        asked.Should().ContainSingle("strict re-asks only the key its negative entry held");
        asked[0]["queries"]!.ToJsonString().Should().Contain("\"W-2\"").And.NotContain("\"W-1\"").And.NotContain("\"W-3\"");
    }

    [Fact]
    public async Task F06_an_owner_whose_health_caps_its_batch_below_this_hosts_cap_is_sent_batches_of_its_own_size()
    {
        Owner.MaxBatchQueries = 1;
        var host = await fleet.HostAsync("chunk-1", new Dictionary<string, string?> { ["OxQL:Limits:ResolveKeyChunk"] = "1" });

        await ScriptedOwnerFleet.LearnOwnerAsync(host);
        var mark = Owner.Batches.Count;

        var answer = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F06", strict: true));

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();
        answer.Strings("w.name").Should().Equal("Widget One", "Widget Two", "Widget Three");

        var batches = Owner.Since(mark);
        batches.Should().HaveCount(3, "three chunks of one key, at the owner's cap of one query per batch (this host's is 10)");
        batches.Should().OnlyContain(batch => batch["queries"]!.AsArray().Count == 1);
    }

    [Fact]
    public async Task F07_a_chain_batch_runs_under_ChainTimeoutMs_not_the_resolve_budget_and_its_timeout_is_refused_under_strict()
    {
        Owner.Delay = TimeSpan.FromSeconds(30);
        var host = await fleet.HostAsync("chain-300", new Dictionary<string, string?>
        {
            ["OxQL:Execution:ChainTimeoutMs"] = "300",
            ["OxQL:Execution:ResolveTimeoutMs"] = "4000",
        });
        const string Continued = """{ "resolve": { "path": "w.code", "as": "again" } },""";

        var chained = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F07", after: Continued));

        chained.ShouldBeOk().DiagnosticCodes.Should().Equal("RESOLVE_TIMEOUT");
        chained.Values("w").Should().OnlyContain(alias => alias == null);
        chained.Duration.Should().BeLessThan(TimeSpan.FromMilliseconds(3_000), "the chain ceiling (300 ms) bounds a batch carrying continued stages, not the resolve budget (4 s)");

        var strict = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F07-strict", strict: true, after: Continued));
        strict.ShouldRefuse("RESOLVE_TIMEOUT", 422);
        strict.Duration.Should().BeLessThan(TimeSpan.FromMilliseconds(3_000));
    }

    [Fact]
    public async Task F07b_the_RESOLVE_TIMEOUT_of_a_chain_batch_names_the_chain_ceiling_it_ran_under()
    {
        Owner.Delay = TimeSpan.FromSeconds(30);
        var host = await fleet.HostAsync("chain-300", new Dictionary<string, string?>
        {
            ["OxQL:Execution:ChainTimeoutMs"] = "300",
            ["OxQL:Execution:ResolveTimeoutMs"] = "4000",
        });

        var chained = await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F07b", after: """{ "resolve": { "path": "w.code", "as": "again" } },"""));

        chained.ShouldHaveDiagnostic("RESOLVE_TIMEOUT")["message"]!.GetValue<string>().Should().Contain("300 ms");
    }

    [Fact]
    public async Task F08_a_forwarded_query_carries_only_the_stages_continued_under_the_alias_so_a_chain_ends_by_construction_and_strict_travels()
    {
        var host = await fleet.HostAsync();
        var mark = Owner.Batches.Count;
        const string Continued = """{ "resolve": { "path": "w.code", "as": "again" } },""";

        await ScriptedOwnerFleet.QueryAsync(host, ScriptedOwnerFleet.Resolve("F08", strict: true, after: Continued));

        var queries = Owner.Since(mark).SelectMany(batch => batch["queries"]!.AsArray()).OfType<JsonObject>().ToList();
        queries.Should().NotBeEmpty();

        foreach (var query in queries)
        {
            var stages = query["pipeline"]!.AsArray().OfType<JsonObject>().ToList();
            var joins = stages.Where(stage => stage.ContainsKey("resolve") || stage.ContainsKey("lookup")).ToList();

            joins.Should().ContainSingle("the origin sent two joins; the owner gets the one continued under 'w' and never 'w' itself");
            joins[0]["resolve"]!["path"]!.GetValue<string>().Should().Be("code", "the continued path is rewritten onto the owner's root");
            query.ToJsonString().Should().NotContain("widgetCodeExplicit");
            query["strict"]?.GetValue<bool>().Should().BeTrue("a query with continued stages carries the origin's strict");
        }
    }

    [Fact]
    public async Task F09_a_healthy_owner_is_the_control_every_widget_resolves_under_every_leg()
    {
        var (plain, reported, strict) = await LegsAsync(await fleet.HostAsync(), "F09");

        foreach (var answer in new[] { plain, reported, strict })
        {
            answer.ShouldBeOk().ShouldHaveNoDiagnostics();
            answer.Strings("widgetCodeExplicit").Should().Equal(Widgets);
            answer.Strings("w.name").Should().Equal("Widget One", "Widget Two", "Widget Three");
        }
    }
}
