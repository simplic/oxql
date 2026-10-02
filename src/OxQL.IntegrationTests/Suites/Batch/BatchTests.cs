using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.RemoteChaos;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Batch;

/// <summary>
/// Area R: <c>POST OxQL/batch</c>. A batch is the one call whose answer is a list of other
/// answers: the batch itself answers 200 whatever its entries did, each entry is a whole page or
/// a whole refusal envelope (which carries its reason class, never a status), and an over-cap
/// or oversized batch is refused whole.
/// <para>
/// Ported from the legacy <c>batch</c> battery on <c>staff.employee</c>. Its whole-batch client
/// refusals (R9–R13) and the short-results transport failure (R17) are the client's and live
/// with its specs; their engine halves, where there is one, are here.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class BatchTests
{
    private static Task<LabClient> Staff() => Lab.ClientAsync(LabService.Staff);

    private static IReadOnlyList<Guid> All() => Corpus.AllIds(Corpus.Employee);

    private static IReadOnlyList<Guid> Dup() => Corpus.IdsOf(Corpus.EmployeesWithMatchCode("DUP", StringOrder.Binary));

    private static JsonObject Query(string pipeline, object? variables = null) => Json.Request(Corpus.Employee, pipeline, variables);

    private static JsonObject Heavy() => Json.Request(Corpus.Template, """[ { "sort": [ { "templateName": "desc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } } ]""");

    [Fact]
    public async Task R01_R02_R03_one_POST_carrying_queries_answers_200_with_one_full_body_per_entry_in_order()
    {
        var dup = Dup();
        dup.Should().HaveCount(3);

        var answer = await (await Staff()).BatchAsync([
            Query("""[ { "match": { "matchCode": { "eq": "DUP" } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]"""),
            Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 1 } } ]"""),
        ]);

        answer.StatusCode.Should().Be(200);
        answer.Body!.AsObject().Select(pair => pair.Key).Should().Equal("results");
        answer.Results.Should().HaveCount(2);
        answer.Results[0].ShouldHaveIds(dup);
        answer.Results[1].ShouldHaveIds([All()[0]]);
        answer.Results.Should().AllSatisfy(entry => entry.PageInfo.ContainsKey("hasNextPage").Should().BeTrue("each entry is a whole page"));
    }

    [Fact]
    public async Task R04_R05_one_entrys_refusal_costs_the_others_nothing_and_arrives_as_the_whole_envelope_without_a_status()
    {
        var all = All();

        var answer = await (await Staff()).BatchAsync([
            Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 1 } } ]"""),
            Query("""[ { "match": { "nosuchpath": { "eq": 1 } } }, { "page": { "limit": 1 } } ]"""),
            Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 2 } } ]"""),
        ]);

        answer.StatusCode.Should().Be(200, "the batch answers 200 whatever its entries did");
        answer.Results.Should().HaveCount(3);
        answer.Results[0].ShouldHaveIds([all[0]]);
        answer.Results[2].ShouldHaveIds([all[0], all[1]]);
        answer.Results[1].Body!.ToJsonString().Should().Be(new JsonObject
        {
            ["type"] = "validation_error",
            ["title"] = "The request could not be bound.",
            ["errors"] = new JsonArray(new JsonObject { ["code"] = "UNKNOWN_PATH", ["message"] = $"'nosuchpath' is not a path of {Corpus.Employee}.", ["stage"] = 0, ["path"] = "nosuchpath", ["params"] = new JsonObject { ["reason"] = "notAMember", ["entity"] = Corpus.Employee } }),
        }.ToJsonString(), "R3: a refusal entry carries no status member");
    }

    [Theory]
    [InlineData("R10", Corpus.Shipment, """[ { "page": { "limit": 1 } } ]""", "UNKNOWN_ENTITY")]
    [InlineData("R13", Corpus.Employee, """[ { "page": { "limit": 501 } } ]""", "PAGE_SIZE_EXCEEDED")]
    public async Task R10_R13_an_entry_the_host_cannot_serve_is_refused_alone_and_its_neighbour_answers(string caseId, string entity, string pipeline, string code)
    {
        // The client refuses these whole before sending; a raw batch shows what the host does.
        var answer = await (await Staff()).BatchAsync([Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 1 } } ]"""), Json.Request(entity, pipeline)]);

        answer.StatusCode.Should().Be(200, caseId);
        answer.Results[0].ShouldHaveIds([All()[0]], caseId);
        answer.Results[1].ShouldRefuse(code, because: caseId);
        answer.Results[1].ErrorCodes.Should().Equal([code], caseId);
    }

    [Fact]
    public async Task R09_an_empty_batch_answers_200_with_no_results()
    {
        var answer = await (await Staff()).BatchBodyAsync("""{ "queries": [] }""");

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Results.Should().BeEmpty();
    }

    /// <summary>
    /// A client over the 100 001 bulk rows of organisation C, where <see cref="Heavy"/> sorts long
    /// enough to run out of a millisecond every time. The server ends a command at its next interrupt
    /// check after the limit, so a sort of organisation A's few thousand rows gets through a limit of
    /// one millisecond every few runs when its page comes in one reply; measured, 109 of 600.
    /// </summary>
    private static async Task<LabClient> OverBulkAsync()
    {
        var shared = await CorpusFleet.SharedAsync();

        await shared.SeedBulkAsync();

        return shared.Client(LabService.Transport, Org.C);
    }

    [Fact]
    public async Task R05_an_entry_that_times_out_carries_the_timeout_class_and_QUERY_TIMEOUT()
    {
        var answer = await (await OverBulkAsync()).BatchAsync([Heavy()], maxTimeMs: 1);

        answer.StatusCode.Should().Be(200);
        var entry = answer.Results.Should().ContainSingle().Subject;
        entry.Type.Should().Be("timeout", "the class is what a caller reads the status from: a single query answers it 504");
        entry.ErrorCodes.Should().Equal("QUERY_TIMEOUT");
        entry.Body!.AsObject().ContainsKey("status").Should().BeFalse();
    }

    [Fact]
    public async Task R06_each_entry_keeps_its_own_variables()
    {
        var dup = Dup();
        var muller = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Text(row, "matchCode") is { } code && Order.FoldCi(code) == Order.FoldCi("müller"));
        muller.Should().HaveCount(4, "contract 2 folds case and accents: every spelling of müller");
        const string Pipeline = """[ { "match": { "matchCode": { "eq": { "$var": "code" } } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ]""";

        var answer = await (await Staff()).BatchAsync([
            Query(Pipeline, new JsonObject { ["code"] = "DUP" }),
            Query(Pipeline, new JsonObject { ["code"] = "müller" }),
        ]);

        answer.Results[0].ShouldHaveIds(dup);
        answer.Results[1].ShouldHaveIds(muller);
    }

    [Fact]
    public async Task R07_each_entry_may_carry_its_own_cursor_and_count()
    {
        var all = All();
        var client = await Staff();

        var first = await client.BatchAsync([Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 2, "includeTotalCount": true } } ]""")]);
        first.Results[0].ShouldHaveIds([all[0], all[1]]).ShouldHaveTotal(all.Count);
        var cursor = first.Results[0].NextCursor;
        cursor.Should().NotBeNullOrEmpty();

        var second = await client.BatchAsync([
            Query($$"""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 2, "cursor": "{{cursor}}", "includeTotalCount": false } } ]"""),
            Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 2, "includeTotalCount": true } } ]"""),
        ]);

        second.Results[0].ShouldHaveIds([all[2], all[3]]);
        second.Results[0].TotalCount.Should().BeNull();
        second.Results[1].ShouldHaveIds([all[0], all[1]]).ShouldHaveTotal(all.Count);
    }

    [Fact]
    public async Task R08_R07finding_each_entry_answers_its_own_shape_and_an_inclusion_projection_keeps_the_key_for_a_sort()
    {
        var all = All();

        var answer = await (await Staff()).BatchAsync([
            Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 1 } } ]"""),
            Query("""[ { "project": { "id": 1, "matchCode": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 1 } } ]"""),
            Query("""[ { "project": { "matchCode": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 3 } } ]"""),
        ]);

        answer.Results[0].ShouldHaveIds([all[0]]);
        answer.Results[0].Items[0]!.AsObject().Count.Should().BeGreaterThan(2, "the first entry carries the whole row");
        answer.Results[1].ShouldHaveIds([all[0]]).Items[0]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "matchCode"]);
        // Finding R7: the caller projects the displayed columns and sorts by the key; the key is
        // kept through the inclusion projection and the sort is served.
        answer.Results[2].ShouldHaveIds(all.Take(3));
    }

    [Fact]
    public async Task R12_R18_an_over_cap_batch_is_refused_whole_400_BATCH_TOO_LARGE_and_ten_are_accepted()
    {
        var client = await Staff();
        var one = Query("""[ { "page": { "limit": 1 } } ]""");

        var eleven = await client.BatchAsync(Enumerable.Repeat<object>(one, 11));
        eleven.ShouldRefuse("BATCH_TOO_LARGE", 400);
        eleven.Type.Should().Be("validation_error");

        var ten = await client.BatchAsync(Enumerable.Repeat<object>(one, 10));
        ten.StatusCode.Should().Be(200);
        ten.Results.Should().HaveCount(10).And.AllSatisfy(entry => entry.ShouldBeOk());
    }

    [Fact]
    public async Task R14_the_entries_of_one_batch_run_one_after_another()
    {
        // Three entries each wait on a slow owner (a resolve filter of its own per entry, so no
        // entry is served from another's cache entry). Run one after another they cost at least
        // three delays and reach the owner at least one delay apart; run together they would
        // arrive at once. A timing lower bound, not a ratio.
        var delay = TimeSpan.FromMilliseconds(300);
        await using var fleet = await CorpusFleet.CreateAsync("b4_batch_sequential", seed: [LabService.Conformance]);
        fleet.Owner.Set(new ChaosSettings { Mode = ChaosMode.Slow, Delay = delay });
        var mark = fleet.Owner.Mark();

        var answer = await fleet.Client(LabService.Conformance).BatchAsync(
            Enumerable.Range(1, 3).Select(n => (object)Json.Request(Corpus.Conformance, OwnerFleet.Resolve($"R-14-{n}"))));

        answer.StatusCode.Should().Be(200);
        answer.Results.Should().HaveCount(3).And.AllSatisfy(entry => entry.ShouldHaveIds(OwnerFleet.Rows().Select(row => row.Id)).ShouldHaveNoDiagnostics());
        answer.Results.Should().AllSatisfy(entry => entry.Strings("w.name").Should().Equal(OwnerFleet.WidgetNames()));

        var arrivals = fleet.Owner.BatchesSince(mark).Select(request => request.At).ToList();
        arrivals.Should().HaveCount(3);
        arrivals.Zip(arrivals.Skip(1), (earlier, later) => later - earlier).Should().AllSatisfy(gap => gap.Should().BeGreaterThanOrEqualTo(delay - TimeSpan.FromMilliseconds(30)));
        answer.Duration.Should().BeGreaterThanOrEqualTo(3 * delay - TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task R15_maxTimeMs_caps_every_entry_of_the_batch_and_a_generous_budget_lets_the_same_query_through()
    {
        var client = await Lab.ClientAsync(LabService.Transport);

        var tight = await (await OverBulkAsync()).BatchAsync([Heavy(), Heavy()], maxTimeMs: 1);
        tight.StatusCode.Should().Be(200);
        tight.Results.Select(entry => entry.Type).Should().Equal("timeout", "timeout");
        tight.Results.SelectMany(entry => entry.ErrorCodes).Should().Equal("QUERY_TIMEOUT", "QUERY_TIMEOUT");

        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Template, "templateName", descending: true), limit: 500);
        var generous = await client.BatchAsync([Heavy()], maxTimeMs: 30_000);
        generous.Results[0].ShouldHaveIds(expected).ShouldHaveTotal(Corpus.Counts(Corpus.Template).A);
    }

    [Fact]
    public async Task R16_a_batch_body_above_maxRequestBytes_is_refused_413_before_any_query_runs()
    {
        var conditions = string.Join(", ", Enumerable.Range(0, 4_000).Select(n => $$"""{ "matchCode": { "eq": "{{new string('x', 60)}}{{n}}" } }"""));
        var huge = Query($$"""[ { "match": { "or": [ {{conditions}} ] } }, { "page": { "limit": 1 } } ]""");

        var answer = await (await Staff()).BatchAsync([huge]);

        answer.ShouldRefuse("REQUEST_TOO_LARGE", 413);
        answer.Type.Should().Be("validation_error");
    }

    [Fact]
    public async Task R19_a_grid_block_its_count_and_a_dictionary_answer_in_one_round_trip()
    {
        var all = All();

        var answer = await (await Staff()).BatchAsync([
            Query("""[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]"""),
            Query("""[ { "match": { "matchCode": { "eq": "DUP" } } }, { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } } ]"""),
            Query("""[ { "project": { "id": 1, "group.displayKey": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } } ]"""),
        ]);

        answer.Results[0].ShouldHaveIds(all.Take(5));
        answer.Results[1].ShouldHaveTotal(Dup().Count);
        answer.Results[2].ShouldHaveIds(all);
        answer.Results[2].Strings("group.displayKey").Should().Equal(Corpus.Rows(Corpus.Employee).Select(row => Corpus.Text(row, "group.displayKey")));
    }

    [Fact]
    public async Task R_null_entry_is_a_coded_refusal_of_that_entry_not_a_500_for_the_whole_batch()
    {
        var client = await Staff();

        var alone = await client.BatchBodyAsync("""{ "queries": [ null ] }""");
        alone.StatusCode.Should().Be(200, "a malformed entry is a coded refusal of that entry");
        var entry = alone.Results.Should().ContainSingle().Subject;
        entry.Type.Should().Be("validation_error");
        entry.ErrorCodes.Should().Equal("UNKNOWN_STAGE");
        entry.Errors[0]["message"]!.GetValue<string>().Should().Be("The request is empty; a query carries an entityType and a pipeline.");

        var mixed = await client.BatchBodyAsync(new JsonObject { ["queries"] = new JsonArray(Query("""[ { "project": { "id": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 1 } } ]"""), null) });
        mixed.StatusCode.Should().Be(200);
        mixed.Results[0].ShouldHaveIds([All()[0]]);
        mixed.Results[1].ErrorCodes.Should().Equal("UNKNOWN_STAGE");
    }
}
