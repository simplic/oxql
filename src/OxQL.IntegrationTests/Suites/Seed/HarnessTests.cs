using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Seed;

/// <summary>The harness verbs do what they say: identity switching, contract headers, batch entries, explain, health and cursor reading.</summary>
[Trait("Category", "Integration")]
public class HarnessTests
{
    [Fact]
    public async Task Switching_the_organisation_switches_the_rows_and_no_organisation_is_refused()
    {
        var staff = await Lab.ClientAsync(LabService.Staff);

        (await staff.As(Org.B).SendAsync(Corpus.Employee, """[{ "sort": [{ "id": "asc" }] }]""")).ShouldHaveIds(Corpus.AllIds(Corpus.Employee, Org.B));
        (await staff.Anonymous().SendAsync(Corpus.Employee, "[]")).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_batch_answers_one_entry_per_query_each_assertable_on_its_own()
    {
        var staff = await Lab.ClientAsync(LabService.Staff);

        var answer = await staff.BatchAsync([
            Json.Request(Corpus.Employee, """[{ "match": { "matchCode": { "eq": "DUP" } } }, { "sort": [{ "id": "asc" }] }]"""),
            Json.Request(Corpus.Employee, """[{ "match": { "nope": { "eq": 1 } } }]"""),
        ]);

        answer.Status.Should().Be(HttpStatusCode.OK, answer.ToString());
        answer.Results[0].ShouldHaveIds(Corpus.IdsOf(Corpus.Employee, "dup-a", "dup-b", "dup-c"));
        answer.Results[1].ShouldRefuse("UNKNOWN_PATH");
    }

    [Fact]
    public async Task Explain_runs_on_the_standard_host_and_the_explain_variant_and_health_reports_the_contract()
    {
        var staff = await Lab.ClientAsync(LabService.Staff);

        (await staff.ExplainHereAsync(Json.Request(Corpus.Employee, "[]"))).Status.Should().Be(HttpStatusCode.OK, "explain is on by default");
        (await staff.ExplainAsync(Json.Request(Corpus.Employee, "[]"))).Status.Should().Be(HttpStatusCode.OK);
        (await staff.HealthAsync(shallow: true)).Body!["engine"]!["contract"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task A_cursor_names_the_last_row_of_its_page()
    {
        var transport = await Lab.ClientAsync(LabService.Transport);
        var page = await transport.SendAsync(Corpus.Shipment, """[{ "sort": [{ "id": "asc" }] }, { "page": { "limit": 7 } }]""");

        var payload = Cursors.Payload(page.ShouldBeOk().NextCursor!);
        var legs = Strings(payload).Where(text => text.Contains("$binary", StringComparison.Ordinal)).Select(Cursors.Guid).ToList();

        legs.Should().Contain(Corpus.AllIds(Corpus.Shipment)[6]);

        static IEnumerable<string> Strings(JsonNode? node) => node switch
        {
            JsonObject item => item.SelectMany(pair => Strings(pair.Value)),
            JsonArray array => array.SelectMany(Strings),
            JsonValue value when value.TryGetValue<string>(out var text) => [text],
            _ => [],
        };
    }

    [Fact]
    public async Task The_owner_log_reads_the_condition_a_resolve_sent()
    {
        await using var fleet = await CorpusFleet.CreateAsync("harness-owner", [LabService.Conformance]);
        var mark = fleet.Owner.Mark();

        (await fleet.Client(LabService.Conformance).SendAsync(Corpus.Conformance, """[{ "resolve": { "path": "widgetCodeExplicit", "as": "w" } }]""")).ShouldBeOk();

        var condition = fleet.Owner.BatchesSince(mark).Single().Condition();

        condition.Should().NotBeNull();
        condition!.Value.Path.Should().Be("code", "the declared target field is the key the owner is asked for");
        condition.Value.Operator.Should().Be("in");
    }
}
