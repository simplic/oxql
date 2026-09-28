using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.RemoteChaos;

/// <summary>
/// Remote resolve and semi-join under a misbehaving owner. The conformance entity's
/// <c>widgetCodeExplicit</c> references <c>owner.widget</c>, served by the private fleet's chaos
/// owner; organisation A's three rows name W-1, W-2 and W-3. The rule the cases pin: a failing
/// owner never fails a resolve page (the alias is null and a diagnostic says why), always
/// refuses a semi-join (without the ids the condition cannot be evaluated), and an owner answer
/// the host cannot read is a refusal, never silence.
/// <para>
/// Ported from the legacy <c>final/remote-chaos</c> battery. Its two postures (default
/// limits; lowered limits and switched-off compat) are variants of the conformance host here.
/// Remote calls go through the engine's public batch route; the internal route and its
/// <c>i-api-key</c> scheme are base-server code and are not exercised.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class RemoteChaosTests : IClassFixture<OwnerFleet>
{
    /// <summary>The resolve budget of the variant the timeout legs run on, so they wait half a second, not two.</summary>
    private const int ShortResolveTimeoutMs = 500;

    private static readonly IReadOnlyDictionary<string, string?> ShortTimeout = new Dictionary<string, string?> { ["OxQL:Execution:ResolveTimeoutMs"] = "500" };

    private static readonly IReadOnlyDictionary<string, string?> Lowered = new Dictionary<string, string?>
    {
        ["OxQL:Limits:MaxResolveKeys"] = "2",
        ["OxQL:Limits:ResolveKeyChunk"] = "1",
        ["OxQL:Limits:MaxSemiJoinIds"] = "9000",
        ["OxQL:Limits:CountCap"] = "2",
        ["OxQL:Compat:Enabled"] = "false",
        ["OxQL:Explain:Enabled"] = "true",
    };

    private readonly OwnerFleet fleet;

    public RemoteChaosTests(OwnerFleet fleet)
    {
        this.fleet = fleet;
        fleet.Owner.Reset();
    }

    private ChaosOwner Owner => fleet.Owner;

    private LabClient Conformance(Org org = Org.A) => fleet.Conformance(org);

    private LabClient Fast() => fleet.Conformance(variant: "timeout-500", configuration: ShortTimeout);

    private LabClient LoweredPosture() => fleet.Conformance(variant: "lowered", configuration: Lowered);

    private static readonly int RowCount = OwnerFleet.Rows().Count;

    private static IReadOnlyList<Guid> RowIds() => OwnerFleet.Rows().Select(row => row.Id).ToList();

    /// <summary>The one organisation A row whose widget is W-1: the honest semi-join answer.</summary>
    private static Guid WidgetOneRow() => OwnerFleet.Rows().Single(row => Corpus.Text(row, "widgetCodeExplicit") == "W-1").Id;

    // ── the control ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task C00_a_healthy_owner_resolves_every_row_onto_its_own_widget()
    {
        var expected = OwnerFleet.WidgetNames();
        expected.Should().Equal("Widget One", "Widget Two", "Widget Three");

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("C-00"));

        // The key is code; the owner's decoy member id names a different widget, so pairing on
        // it would read Three/One/Two here.
        answer.ShouldHaveIds(RowIds()).ShouldHaveNoDiagnostics();
        answer.Strings("w.name").Should().Equal(expected);
    }

    [Fact]
    public async Task C01_the_semi_join_keeps_exactly_the_row_the_owner_names_and_the_count_agrees()
    {
        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.SemiJoin("C-01"));

        answer.ShouldHaveIds([WidgetOneRow()]).ShouldHaveTotal(1);
    }

    // ── leg A: resolve under a failing owner degrades, never fails the page ─────────────────

    public static TheoryData<string, ChaosSettings, int?> Unreachable => new()
    {
        { "A-01", new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.InternalServerError }, 500 },
        { "A-02", new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.Unauthorized }, 401 },
        { "A-03", new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.NotFound }, 404 },
        { "A-04", new ChaosSettings { Mode = ChaosMode.Reset }, null },
        { "A-05", new ChaosSettings { Mode = ChaosMode.Closed }, null },
        { "A-06", new ChaosSettings { Mode = ChaosMode.Garbage }, null },
        { "A-07", new ChaosSettings { Mode = ChaosMode.Empty }, null },
        { "A-08", new ChaosSettings { Mode = ChaosMode.WrongShape }, null },
    };

    [Theory]
    [MemberData(nameof(Unreachable))]
    public async Task A01_A08_a_resolve_whose_owner_fails_answers_the_whole_page_with_null_aliases_and_one_RESOLVE_UNREACHABLE(string caseId, ChaosSettings mode, int? status)
    {
        Owner.Set(mode);
        var mark = Owner.Mark();

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve(caseId));

        answer.ShouldHaveIds(RowIds(), $"{caseId}: a failing owner never fails the caller's page");
        answer.Values("w").Should().OnlyContain(alias => alias == null, "never a stale or invented widget");
        answer.DiagnosticCodes.Should().Equal("RESOLVE_UNREACHABLE");

        var diagnostic = answer.Diagnostics[0];
        diagnostic["params"]!["service"]!.GetValue<string>().Should().Be(LabFleet.ExternalOwner);
        diagnostic["params"]!["aliases"]!.AsArray().Select(alias => alias!.GetValue<string>()).Should().Equal("w");

        if (status is not null)
            diagnostic["message"]!.GetValue<string>().Should().Contain($"HTTP {status}", "a reached owner that said no is told apart from a network that is down");

        // A failure known at once must not cost the caller the resolve budget.
        answer.Duration.Should().BeLessThan(TimeSpan.FromMilliseconds(2_000), caseId);
        Owner.BatchesSince(mark).Should().ContainSingle(caseId);
    }

    [Fact]
    public async Task A10_a_slow_owner_beyond_the_resolve_budget_answers_RESOLVE_TIMEOUT_after_the_budget_never_the_query_ceiling()
    {
        Owner.Set(new ChaosSettings { Mode = ChaosMode.Slow, Delay = TimeSpan.FromSeconds(30) });

        var answer = await Fast().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("A-10"));

        answer.ShouldHaveIds(RowIds());
        answer.Values("w").Should().OnlyContain(alias => alias == null);
        answer.DiagnosticCodes.Should().Equal("RESOLVE_TIMEOUT");
        answer.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(ShortResolveTimeoutMs - 50)).And.BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A11_a_hanging_owner_answers_the_same_RESOLVE_TIMEOUT_within_the_same_budget()
    {
        Owner.Set(ChaosMode.Hang);

        var answer = await Fast().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("A-11"));

        answer.ShouldHaveIds(RowIds());
        answer.DiagnosticCodes.Should().Equal("RESOLVE_TIMEOUT");
        answer.Duration.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A12_an_owner_that_refuses_is_a_422_RESOLVE_REFUSED_at_the_resolve_stage_with_the_owners_code_appended()
    {
        Owner.Set(new ChaosSettings { Mode = ChaosMode.Refuse, Code = "UNKNOWN_PATH" });

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("A-12"));

        answer.ShouldRefuse("RESOLVE_REFUSED", 422)["stage"]!.GetValue<int>().Should().Be(0, "the refusal points at the resolve stage");
        answer.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "UNKNOWN_PATH");
    }

    [Theory]
    [InlineData("A-13", ChaosMode.Short)]
    [InlineData("A-14", ChaosMode.NullEntry)]
    [InlineData("A-15", ChaosMode.ItemsNotArray)]
    public async Task A13_A15_an_owner_answer_short_of_a_result_is_a_422_RESOLVE_REFUSED_never_a_silently_short_page(string caseId, ChaosMode mode)
    {
        Owner.Set(mode);

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve(caseId));

        answer.ShouldRefuse("RESOLVE_REFUSED", 422, caseId);
        answer.ErrorCodes.Should().Equal(["RESOLVE_REFUSED"], caseId);
    }

    [Fact]
    public async Task A16_an_owner_whose_rows_omit_the_projected_key_is_a_422_RESOLVE_REFUSED_naming_the_member()
    {
        Owner.Set(ChaosMode.MissingField);

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("A-16"));

        // Rows the host cannot key are a refused answer, never "the owner has no such widget".
        answer.ShouldRefuse("RESOLVE_REFUSED", 422);
        answer.ErrorCodes.Should().Equal("RESOLVE_REFUSED");
        answer.Text.Should().Contain("'code'");
    }

    [Fact]
    public async Task A17_an_owner_that_answers_two_rows_under_one_key_never_pairs_a_widget_onto_another_row()
    {
        Owner.Set(ChaosMode.DuplicateKeys);

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("A-17", """["name", "code"]"""));

        answer.ShouldHaveIds(RowIds());

        foreach (var row in answer.Items)
        {
            if (row!["w"] is JsonObject alias)
                alias["code"]!.GetValue<string>().Should().Be(row["widgetCodeExplicit"]!.GetValue<string>(), answer.ToString());
        }

        answer.Items.Count(row => row!["w"] is not null).Should().Be(1, "only W-1 was answered");
    }

    [Fact]
    public async Task A18_a_64_megabyte_owner_answer_is_bounded_and_the_host_keeps_serving()
    {
        Owner.Set(ChaosMode.Huge);

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("A-18"));

        answer.StatusCode.Should().NotBe(500, answer.ToString());
        answer.ErrorCodes.Should().NotContain("INTERNAL_ERROR");

        Owner.Reset();
        (await Conformance().HealthAsync(shallow: true)).StatusCode.Should().Be(200, "the host survived a 64 MB owner answer");
        (await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("A-18-after"))).ShouldHaveIds(RowIds()).ShouldHaveNoDiagnostics();
    }

    // ── leg B: the same owner under a semi-join refuses, never serves a page ────────────────

    public static TheoryData<string, ChaosSettings> Unavailable => new()
    {
        { "B-01", new ChaosSettings { Mode = ChaosMode.Closed } },
        { "B-02", new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.InternalServerError } },
        { "B-03", new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.NotFound } },
        { "B-04", new ChaosSettings { Mode = ChaosMode.Reset } },
        { "B-05", new ChaosSettings { Mode = ChaosMode.Garbage } },
        { "B-06", new ChaosSettings { Mode = ChaosMode.Empty } },
        { "B-07", new ChaosSettings { Mode = ChaosMode.WrongShape } },
        { "B-08", new ChaosSettings { Mode = ChaosMode.Hang } },
    };

    [Theory]
    [MemberData(nameof(Unavailable))]
    public async Task B01_B08_a_semi_join_whose_owner_fails_is_a_422_RESOLVE_UNAVAILABLE_with_no_rows_and_no_count(string caseId, ChaosSettings mode)
    {
        Owner.Set(mode);

        var answer = await Fast().SendAsync(Corpus.Conformance, OwnerFleet.SemiJoin(caseId));

        answer.ShouldRefuse("RESOLVE_UNAVAILABLE", 422, caseId);
        answer.ErrorCodes.Should().Equal(["RESOLVE_UNAVAILABLE"], caseId);
        answer.Body!["items"].Should().BeNull("no rows may be served");
        answer.Body!["pageInfo"].Should().BeNull("no count may be served either");
        answer.Duration.Should().BeLessThan(TimeSpan.FromSeconds(10), caseId);
    }

    [Fact]
    public async Task B09_a_semi_join_whose_owner_refuses_is_a_422_RESOLVE_REFUSED_at_the_stage_that_carried_the_condition()
    {
        Owner.Set(new ChaosSettings { Mode = ChaosMode.Refuse, Code = "UNKNOWN_PATH" });

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.SemiJoin("B-09"));

        answer.ShouldRefuse("RESOLVE_REFUSED", 422)["stage"]!.GetValue<int>().Should().Be(1, "the match stage carried the condition");
        answer.ErrorCodes.Should().Equal("RESOLVE_REFUSED", "UNKNOWN_PATH");
    }

    [Theory]
    [InlineData("B-10", ChaosMode.Short)]
    [InlineData("B-11", ChaosMode.NullEntry)]
    [InlineData("B-12", ChaosMode.ItemsNotArray)]
    public async Task B10_B12_a_semi_join_owner_answer_short_of_a_result_is_a_422_RESOLVE_REFUSED(string caseId, ChaosMode mode)
    {
        Owner.Set(mode);

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.SemiJoin(caseId));

        answer.ShouldRefuse("RESOLVE_REFUSED", 422, caseId);
        answer.ErrorCodes.Should().Equal(["RESOLVE_REFUSED"], caseId);
    }

    [Fact]
    public async Task B20_a_semi_join_owner_whose_rows_omit_the_projected_key_is_a_422_RESOLVE_REFUSED_never_an_empty_page()
    {
        Owner.Set(ChaosMode.MissingField);

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.SemiJoin("B-20"));

        // The honest answer is the W-1 row; an owner that answered rows the host cannot read has
        // not said "nothing matches".
        answer.ShouldRefuse("RESOLVE_REFUSED", 422);
        answer.ErrorCodes.Should().Equal("RESOLVE_REFUSED");
        answer.Text.Should().Contain("'code'");
    }

    [Fact]
    public async Task B21_a_semi_join_owner_that_answers_two_rows_under_one_key_gives_the_honest_single_row()
    {
        Owner.Set(ChaosMode.DuplicateKeys);

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.SemiJoin("B-21"));

        answer.ShouldHaveIds([WidgetOneRow()]).ShouldHaveTotal(1);
    }

    // ── leg F: what the caller sends the owner ──────────────────────────────────────────────

    [Fact]
    public async Task F01_the_outgoing_batch_carries_the_organisation_the_contract_the_correlation_and_the_keys_on_the_declared_field()
    {
        var mark = Owner.Mark();
        const string Correlation = "b4-f01-correlation";

        var answer = await Conformance().WithHeader(LabIdentity.CorrelationHeader, Correlation).SendAsync(Corpus.Conformance, OwnerFleet.Resolve("F-01"));

        answer.ShouldHaveIds(RowIds()).ShouldHaveNoDiagnostics();
        var batches = Owner.BatchesSince(mark);
        batches.Should().ContainSingle("three keys under a 500-key chunk is exactly one batch");

        var sent = batches[0];
        sent.Path.Should().EndWith("/OxQL/batch");
        sent.Header(LabIdentity.OrganisationHeader).Should().Be(Org.A.Id().Wire());
        sent.Header(LabIdentity.UserHeader).Should().Be(LabIdentity.User.Wire());
        sent.Header(LabIdentity.ContractHeader).Should().Be("2");
        sent.Header(LabIdentity.CorrelationHeader).Should().Be(Correlation, "the correlation id reaches the owner's logs");

        sent.Queries.Should().ContainSingle();
        sent.Queries[0]["entityType"]!.GetValue<string>().Should().Be(ChaosOwner.Entity);
        var keys = sent.Condition()!.Value;
        keys.Path.Should().Be("code", "the keys travel on the declared field, not on the decoy id");
        keys.Operator.Should().Be("in");
        keys.Operand!.AsArray().Select(key => key!.GetValue<string>()).Should().BeEquivalentTo(OwnerFleet.Rows().Select(row => Corpus.Text(row, "widgetCodeExplicit")));
        sent.Condition(index: 1)!.Value.Operand!.GetValue<string>().Should().Be("F-01", "the caller's own resolve filter travels as a second match");
        (sent.Stage("project") as JsonObject)!.Select(pair => pair.Key).Should().Contain("code", "the projection always carries the key");
    }

    [Fact]
    public async Task F02_organisation_B_is_asked_as_organisation_B()
    {
        var mark = Owner.Mark();

        var answer = await Conformance(Org.B).SendAsync(Corpus.Conformance, OwnerFleet.Resolve("F-02"));

        answer.ShouldHaveIds(OwnerFleet.Rows(Org.B).Select(row => row.Id));
        answer.Strings("w.name").Should().Equal(OwnerFleet.WidgetNames(Org.B));
        Owner.BatchesSince(mark).Should().ContainSingle().Which.Header(LabIdentity.OrganisationHeader).Should().Be(Org.B.Id().Wire());
    }

    // ── leg C: the lowered limits ───────────────────────────────────────────────────────────

    [Fact]
    public async Task C10_RESOLVE_PARTIAL_with_two_keys_allowed_and_a_chunk_of_one_two_aliases_resolve_and_the_chunks_travel_in_one_batch()
    {
        var mark = Owner.Mark();

        var answer = await LoweredPosture().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("C-10"));

        answer.ShouldHaveIds(RowIds(), "the page is whole; only the surplus alias is dropped");
        answer.Values("w").Count(alias => alias is not null).Should().Be(2, "exactly MaxResolveKeys aliases are filled");
        answer.DiagnosticCodes.Should().Equal("RESOLVE_PARTIAL");
        var diagnostic = answer.Diagnostics[0]["params"]!;
        diagnostic["alias"]!.GetValue<string>().Should().Be("w");
        diagnostic["keys"]!.GetValue<int>().Should().Be(RowCount);
        diagnostic["max"]!.GetValue<int>().Should().Be(2);

        var batches = Owner.BatchesSince(mark);
        batches.Should().ContainSingle("the chunks travel in one batch, not one request each");
        batches[0].Queries.Should().HaveCount(2);
    }

    [Fact]
    public async Task C11_TOTAL_COUNT_CAPPED_with_a_cap_of_two_a_three_row_count_reports_the_cap_and_says_so()
    {
        RowCount.Should().BeGreaterThan(2);

        var answer = await LoweredPosture().SendAsync(Corpus.Conformance, """[ { "page": { "limit": 10, "includeTotalCount": true } } ]""");

        answer.ShouldHaveTotal(2, capped: true);
        answer.ShouldHaveDiagnostic("TOTAL_COUNT_CAPPED")["params"]!["cap"]!.GetValue<int>().Should().Be(2);
        answer.DiagnosticCodes.Should().Equal("TOTAL_COUNT_CAPPED");
    }

    [Fact]
    public async Task C12_a_MaxSemiJoinIds_above_MaxOffset_is_clamped_to_it_and_health_publishes_the_clamped_value()
    {
        var limits = OwnerFleet.Limits(await LoweredPosture().HealthAsync(shallow: true));

        limits["maxSemiJoinIds"]!.GetValue<int>().Should().Be(limits["maxOffset"]!.GetValue<int>());
        limits["maxSemiJoinIds"]!.GetValue<int>().Should().Be(5_000);
    }

    // ── leg E: the release-train switches ───────────────────────────────────────────────────

    [Fact]
    public async Task E01_the_default_posture_carries_the_contract_1_binder_and_no_explain_route()
    {
        var client = Conformance();

        OwnerFleet.Capabilities(await client.HealthAsync(shallow: true)).Should().Contain("compat.v1");
        (await client.Contract(null).SendAsync(Corpus.Conformance, """[ { "match": { "_id": { "neq": null } } }, { "page": { "limit": 1 } } ]""")).StatusCode
            .Should().Be(200, "a header-less request binds as contract 1, where _id is the key");
        (await client.ExplainHereAsync(Json.Request(Corpus.Conformance, """[ { "page": { "limit": 1 } } ]"""))).StatusCode.Should().Be(404, "explain is off unless a host turns it on");
    }

    [Fact]
    public async Task E02_with_compat_off_a_header_less_request_binds_as_contract_2_and_health_drops_compat_v1()
    {
        var client = LoweredPosture();

        OwnerFleet.Capabilities(await client.HealthAsync(shallow: true)).Should().NotContain("compat.v1");

        var answer = await client.Contract(null).SendAsync(Corpus.Conformance, """[ { "match": { "_id": { "neq": null } } }, { "page": { "limit": 1 } } ]""");
        answer.ShouldRefuse("UNKNOWN_PATH", 400);
        answer.ErrorCodes.Should().Equal("UNKNOWN_PATH");
    }

    [Fact]
    public async Task E03_with_explain_on_the_explain_route_answers_the_stages_and_an_advisory()
    {
        var answer = await LoweredPosture().ExplainHereAsync(Json.Request(Corpus.Conformance, """[ { "match": { "name": { "eq": "Alpha" } } }, { "page": { "limit": 5 } } ]"""));

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Body!.AsObject().Select(pair => pair.Key).Should().Contain(["stages", "advisory"]);
    }

    [Fact]
    public async Task E04_every_limit_a_variant_was_started_with_is_visible_on_health()
    {
        var lowered = OwnerFleet.Limits(await LoweredPosture().HealthAsync(shallow: true));
        var standard = OwnerFleet.Limits(await Conformance().HealthAsync(shallow: true));

        lowered.Count.Should().BeGreaterThan(15, "health publishes the whole limit set");
        lowered["maxResolveKeys"]!.GetValue<int>().Should().Be(2);
        lowered["resolveKeyChunk"]!.GetValue<int>().Should().Be(1);
        lowered["countCap"]!.GetValue<int>().Should().Be(2);
        standard["maxResolveKeys"]!.GetValue<int>().Should().Be(10_000);
        standard["countCap"]!.GetValue<int>().Should().Be(100_000);
    }

    // ── leg G: caller cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task G01_a_caller_that_aborts_while_the_owner_hangs_leaves_the_host_healthy_and_the_next_query_clean()
    {
        Owner.Set(ChaosMode.Hang);
        var host = await Fast().HostAsync();

        using (var client = host.Server.CreateClient())
        using (var request = new HttpRequestMessage(HttpMethod.Post, "OxQL/query"))
        using (var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            request.Content = new StringContent(Json.Request(Corpus.Conformance, OwnerFleet.Resolve("G-01")).ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, Org.A.Id().Wire());
            request.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, "2");

            await FluentActions.Awaiting(() => client.SendAsync(request, abort.Token)).Should().ThrowAsync<OperationCanceledException>("the caller's own abort surfaces as an abort, not as a page");
        }

        Owner.Reset();
        (await Fast().HealthAsync(shallow: true)).StatusCode.Should().Be(200, "an aborted request does not hurt the host");
        (await Fast().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("G-01-next"))).ShouldHaveIds(RowIds()).ShouldHaveNoDiagnostics("no diagnostic leaks from the aborted request");
    }
}
