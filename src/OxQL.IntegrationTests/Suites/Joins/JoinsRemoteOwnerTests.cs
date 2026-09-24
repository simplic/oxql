using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.RemoteChaos;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// Area M, the rows about what the caller host asks the owner: the request, its headers and
/// budget, chunking, batching, the resolve cache and the semi-join's paging, count cap and cache.
/// The legacy battery listed these as unreachable, because a real owner keeps no request log; the
/// private fleet's chaos owner does (<see cref="ChaosOwner.BatchesSince"/>).
/// </summary>
[Trait("Category", "Integration")]
public class JoinsRemoteOwnerTests : IClassFixture<OwnerFleet>
{
    private readonly OwnerFleet fleet;

    public JoinsRemoteOwnerTests(OwnerFleet fleet)
    {
        this.fleet = fleet;
        fleet.Owner.Reset();
    }

    private ChaosOwner Owner => fleet.Owner;

    private LabClient Conformance(Org org = Org.A) => fleet.Conformance(org);

    private static IReadOnlyList<Guid> RowIds(Org org = Org.A) => OwnerFleet.Rows(org).Select(row => row.Id).ToList();

    private static IReadOnlyList<string> Keys(Org org = Org.A) => OwnerFleet.Rows(org).Select(row => Corpus.Text(row, "widgetCodeExplicit")!).Distinct().ToList();

    private static IReadOnlyList<string> Strings(JsonNode? array) => (array as JsonArray ?? []).Select(item => item!.GetValue<string>()).ToList();

    [Fact]
    public async Task M22_M32_M33_the_owner_is_asked_the_keys_on_the_target_field_with_a_project_a_page_of_the_key_count_the_budget_and_the_callers_identity()
    {
        var mark = Owner.Mark();
        const string Correlation = "b4-m33";

        var answer = await Conformance().WithHeader(LabIdentity.CorrelationHeader, Correlation).SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-22"));

        answer.ShouldHaveIds(RowIds()).Strings("w.name").Should().Equal(OwnerFleet.WidgetNames());

        var sent = Owner.BatchesSince(mark).Should().ContainSingle().Subject;
        var query = sent.Queries.Should().ContainSingle().Subject;
        query["entityType"]!.GetValue<string>().Should().Be(ChaosOwner.Entity);

        var keys = sent.Condition()!.Value;
        keys.Path.Should().Be("code");
        keys.Operator.Should().Be("in");
        Strings(keys.Operand).Should().BeEquivalentTo(Keys(), "the keys, de-duplicated");

        (sent.Stage("project") as JsonObject)!.Select(pair => pair.Key).Should().BeEquivalentTo(["name", "code"], "the select, and always the target field");
        sent.Stage("page")!["limit"]!.GetValue<int>().Should().Be(Keys().Count, "page.limit is the key count");

        // M32: min(remaining budget, ResolveTimeoutMs) rides on the batch body as the owner's budget.
        sent.Body!["maxTimeMs"]!.GetValue<int>().Should().BeInRange(1_900, 2_000);

        // M33: the caller's identity. The i-api-key scheme and the InternalHosts address belong
        // to the base server's remote client, not to the engine.
        sent.Header(LabIdentity.ContractHeader).Should().Be("2");
        sent.Header(LabIdentity.OrganisationHeader).Should().Be(Org.A.Id().Wire());
        sent.Header(LabIdentity.UserHeader).Should().Be(LabIdentity.User.Wire());
        sent.Header(LabIdentity.CorrelationHeader).Should().Be(Correlation);
    }

    [Fact]
    public async Task F_ENT_002_M25_a_repeat_of_one_remote_stage_is_answered_from_the_cache_and_a_different_select_asks_again()
    {
        var mark = Owner.Mark();
        var expected = OwnerFleet.WidgetNames();

        var first = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-25"));
        var second = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-25"));

        first.ShouldHaveIds(RowIds()).Strings("w.name").Should().Equal(expected, "the stage joined on the field it declares");
        second.ShouldHaveIds(RowIds()).Strings("w.name").Should().Equal(expected, "the cached repeat answers the same rows");
        Owner.BatchesSince(mark).Should().ContainSingle("the cache answered the repeat");

        var other = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-25", """["name", "id"]"""));
        other.ShouldHaveIds(RowIds()).Strings("w.name").Should().Equal(expected);
        Owner.BatchesSince(mark).Should().HaveCount(2, "a different select is a different cache entry");
        other.Strings("w.id").Should().Equal(OwnerFleet.Rows().Select(row => ChaosOwner.Widgets.First(widget => widget.Code == Corpus.Text(row, "widgetCodeExplicit")).Id), "the decoy member is read, not joined on");
    }

    [Fact]
    public async Task M26_the_resolve_cache_is_keyed_by_organisation_and_the_other_organisation_is_asked_under_its_own()
    {
        var mark = Owner.Mark();

        (await Conformance(Org.A).SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-26"))).ShouldHaveIds(RowIds());
        var otherOrg = await Conformance(Org.B).SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-26"));

        otherOrg.ShouldHaveIds(RowIds(Org.B)).Strings("w.name").Should().Equal(OwnerFleet.WidgetNames(Org.B));
        var batches = Owner.BatchesSince(mark);
        batches.Should().HaveCount(2, "organisation B's key is not served from A's cache entry");
        batches.Select(batch => batch.Header(LabIdentity.OrganisationHeader)).Should().Equal(Org.A.Id().Wire(), Org.B.Id().Wire());
    }

    [Fact]
    public async Task M23_keys_are_chunked_by_ResolveKeyChunk_one_owner_query_per_chunk_batched_per_service()
    {
        var mark = Owner.Mark();
        var client = fleet.Conformance(variant: "chunk-1", configuration: new Dictionary<string, string?> { ["OxQL:Limits:ResolveKeyChunk"] = "1" });

        var answer = await client.SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-23"));

        answer.ShouldHaveIds(RowIds()).ShouldHaveNoDiagnostics().Strings("w.name").Should().Equal(OwnerFleet.WidgetNames());
        var batch = Owner.BatchesSince(mark).Should().ContainSingle().Subject;
        batch.Queries.Should().HaveCount(Keys().Count);
        Enumerable.Range(0, batch.Queries.Count).Select(index => Strings(batch.Condition(index)!.Value.Operand)).Should().AllSatisfy(chunk => chunk.Should().ContainSingle());
    }

    [Fact]
    public async Task M24_more_chunks_than_MaxBatchQueries_go_out_in_several_batches()
    {
        var mark = Owner.Mark();
        var client = fleet.Conformance(variant: "chunk-1-batch-2", configuration: new Dictionary<string, string?> { ["OxQL:Limits:ResolveKeyChunk"] = "1", ["OxQL:Limits:MaxBatchQueries"] = "2" });
        Keys().Count.Should().Be(3);

        var answer = await client.SendAsync(Corpus.Conformance, OwnerFleet.Resolve("M-24"));

        answer.ShouldHaveIds(RowIds()).Strings("w.name").Should().Equal(OwnerFleet.WidgetNames());
        Owner.BatchesSince(mark).Select(batch => batch.Queries.Count).Should().Equal(2, 1);
    }

    [Fact]
    public async Task M34_two_targets_in_two_services_each_get_their_own_batch()
    {
        var mark = Owner.Mark();
        var rows = OwnerFleet.Rows();
        var employees = rows.Select(row => Corpus.GuidAt(row, "employeeId") is { } key && Corpus.Rows(Corpus.Employee).Any(employee => employee.Id == key) ? key : (Guid?)null).ToList();
        employees.Should().Contain(id => id != null);

        var answer = await Conformance().SendAsync(Corpus.Conformance, $$"""
            [ { "resolve": { "path": "employeeId", "as": "e", "select": ["matchCode"] } },
              {{OwnerFleet.ResolveStage("M-34")}},
              { "project": { "id": 1, "e": 1, "w": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 5 } } ]
            """);

        answer.ShouldHaveIds(RowIds()).ShouldHaveNoDiagnostics();
        answer.Items.Select(row => row!["e"] is JsonObject employee ? Guid.Parse(employee["id"]!.GetValue<string>()) : (Guid?)null).Should().Equal(employees);
        answer.Strings("w.name").Should().Equal(OwnerFleet.WidgetNames());

        // The owner saw one batch holding only its own entity: the staff keys went to staff.
        var batch = Owner.BatchesSince(mark).Should().ContainSingle().Subject;
        batch.Queries.Should().ContainSingle().Which["entityType"]!.GetValue<string>().Should().Be(ChaosOwner.Entity);
    }

    [Fact]
    public async Task M37_M41_a_semi_join_asks_the_owner_once_for_the_condition_rebased_onto_the_target_with_the_count()
    {
        var mark = Owner.Mark();

        var answer = await Conformance().SendAsync(Corpus.Conformance, OwnerFleet.SemiJoin("M-37"));

        answer.ShouldHaveIds([OwnerFleet.Rows().Single(row => Corpus.Text(row, "widgetCodeExplicit") == "W-1").Id]).ShouldHaveTotal(1);

        // Two batches: the semi-join before the page, then the resolve after it.
        var batches = Owner.BatchesSince(mark);
        batches.Should().HaveCount(2);
        var semiJoin = batches[0];
        var condition = semiJoin.Condition()!.Value;
        condition.Path.Should().Be("name", "the condition is rebased from w.name onto the target path");
        condition.Operator.Should().Be("in");
        Strings(condition.Operand).Should().Equal("Widget One", "M-37");
        (semiJoin.Stage("project") as JsonObject)!.Select(pair => pair.Key).Should().Equal("code");
        semiJoin.Stage("page")!["includeTotalCount"]!.GetValue<bool>().Should().BeTrue("M41: the first ask carries the count, so the cap is decided in one round trip");
        semiJoin.Stage("page")!["offset"].Should().BeNull();
    }

    [Fact]
    public async Task M38_the_case_rule_of_the_condition_is_forwarded_to_the_owner_and_only_what_the_caller_wrote()
    {
        async Task<JsonNode?> OptionsSent(string caseId, string options)
        {
            var mark = Owner.Mark();
            var answer = await Conformance().SendAsync(Corpus.Conformance, $$"""
                [ {{OwnerFleet.ResolveStage(caseId)}},
                  { "match": { "w.name": { "in": ["Widget One", "{{caseId}}"]{{options}} } } },
                  { "project": { "id": 1 } },
                  { "page": { "limit": 10 } } ]
                """);

            answer.ShouldBeOk(caseId);
            return Owner.BatchesSince(mark)[0].Stage("match")!["name"]!["options"];
        }

        (await OptionsSent("M-38-a", """, "options": { "ignoreCase": true }"""))!["ignoreCase"]!.GetValue<bool>().Should().BeTrue();
        (await OptionsSent("M-38-b", """, "options": { "caseSensitive": true }"""))!["caseSensitive"]!.GetValue<bool>().Should().BeTrue();
        (await OptionsSent("M-38-c", "")).Should().BeNull("the owner binds under its own default when the caller wrote none");
    }

    [Fact]
    public async Task M40_M41_a_semi_join_whose_owner_counts_more_than_MaxSemiJoinIds_is_refused_after_one_call_never_truncated()
    {
        var mark = Owner.Mark();
        var client = fleet.Conformance(variant: "semi-join-2", configuration: new Dictionary<string, string?> { ["OxQL:Limits:MaxSemiJoinIds"] = "2" });

        var answer = await client.SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } },
              { "match": { "w.name": { "startsWith": "Widget" } } },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldRefuse("SEMI_JOIN_TOO_LARGE", 422)["stage"]!.GetValue<int>().Should().Be(1);
        answer.ErrorCodes.Should().Equal("SEMI_JOIN_TOO_LARGE");
        Owner.BatchesSince(mark).Should().ContainSingle("the owner's count decided it in one round trip");
    }

    [Fact]
    public async Task M43_a_semi_join_spanning_several_owner_pages_reads_them_by_offset_after_a_first_ask_with_the_count()
    {
        const int Widgets = 1_200;
        Owner.Set(new ChaosSettings { Many = Widgets });
        var mark = Owner.Mark();

        // The synthetic widgets are coded W-0001…, which no conformance row names: the page is
        // empty, and what is under test is how the ids were read.
        var answer = await Conformance().SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } },
              { "match": { "w.name": { "startsWith": "Widget" } } },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds([]).ShouldHaveTotal(0);
        var batches = Owner.BatchesSince(mark);
        batches.Should().HaveCount(2, "the first page with the count, then every remaining page together");
        batches[0].Queries.Should().ContainSingle();
        batches[0].Stage("page")!["includeTotalCount"]!.GetValue<bool>().Should().BeTrue();
        batches[1].Queries.Select(query => query["pipeline"]!.AsArray().Select(stage => stage?["page"]).First(page => page is not null)!).Should().AllSatisfy(page => page["includeTotalCount"]?.GetValue<bool>().Should().NotBe(true));
        Enumerable.Range(0, batches[1].Queries.Count).Select(index => batches[1].Stage("page", index)!["offset"]!.GetValue<int>()).Should().Equal(500, 1_000);
    }

    [Fact]
    public async Task M44_a_second_page_of_the_same_semi_join_asks_the_owner_nothing()
    {
        var expected = OwnerFleet.Rows().Where(row => Corpus.Text(row, "widgetCodeExplicit") is "W-1" or "W-2").Select(row => row.Id).ToList();
        expected.Should().HaveCount(2);
        var pipeline = """
            [ { "resolve": { "path": "widgetCodeExplicit", "as": "w" } },
              { "match": { "w.name": { "in": ["Widget One", "Widget Two", "M-44"] } } },
              { "project": { "id": 1 } },
              { "sort": [ { "id": "asc" } ] } ]
            """;
        var mark = Owner.Mark();

        var walk = await Conformance().WalkAsync(Corpus.Conformance, pipeline, limit: 1);

        walk.Ids.Should().Equal(expected);
        walk.Pages.Should().Be(2);
        // The resolve after each page asks for its keys; the semi-join, the ask on name, went once.
        Owner.BatchesSince(mark).Where(batch => batch.Condition()?.Path == "name").Should().ContainSingle("the id list is cached per entity, organisation and condition");
    }
}
