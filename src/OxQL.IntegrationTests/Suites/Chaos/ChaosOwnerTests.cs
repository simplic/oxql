using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Chaos;

/// <summary>
/// The chaos owner's self-test: every mode once, straight through the handler (no engine), then
/// once mounted in a fleet so a remote resolve reaches it. What the engine makes of each
/// misbehaviour is the chaos suite's subject, not this one's.
/// </summary>
[Trait("Category", "Integration")]
public class ChaosOwnerTests
{
    private static JsonObject OwnerQuery(params string[] keys) => new()
    {
        ["entityType"] = ChaosOwner.Entity,
        ["pipeline"] = JsonNode.Parse($$"""
            [ { "match": { "code": { "in": {{new JsonArray(keys.Select(key => (JsonNode?)key).ToArray()).ToJsonString()}} } } },
              { "project": { "$default": 1, "code": 1 } },
              { "page": { "limit": {{keys.Length}} } } ]
            """),
    };

    private static readonly JsonObject Two = new() { ["queries"] = new JsonArray(OwnerQuery("W-1", "W-2"), OwnerQuery("W-3")) };

    private static async Task<(HttpStatusCode Status, string Text)> Batch(ChaosOwner owner, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(owner, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        using var content = new StringContent((body ?? Two).ToJsonString(), Encoding.UTF8, "application/json");
        content.Headers.Add("X-Probe", "self-test");
        using var response = await client.PostAsync("OxQL/batch", content, cancellationToken);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<JsonNode> Parsed(ChaosOwner owner, JsonNode? body = null) => JsonNode.Parse((await Batch(owner, body)).Text)!;

    private static async Task<HttpStatusCode> Health(ChaosOwner owner, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(owner, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        using var response = await client.GetAsync("OxQL/health?shallow=true", cancellationToken);

        return response.StatusCode;
    }

    [Fact]
    public async Task Ok_answers_one_result_per_query_as_a_faithful_owner()
    {
        var owner = new ChaosOwner();

        (await Health(owner)).Should().Be(HttpStatusCode.OK);

        var answer = await Parsed(owner);

        answer["results"]!.AsArray().Should().HaveCount(2);
        answer["results"]![0]!["items"]!.ToJsonString().Should().Be("""[{"code":"W-1","name":"Widget One"},{"code":"W-2","name":"Widget Two"}]""", "$default expands to the key and the display, nothing else");
        answer["results"]![1]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Every_match_stage_applies_with_combinators_and_folding_and_the_decoy_id_answers_another_widget()
    {
        var owner = new ChaosOwner();
        var both = OwnerQuery("W-1", "W-2", "W-3");
        both["pipeline"]!.AsArray().Insert(1, JsonNode.Parse("""{ "match": { "and": [ { "name": { "contains": "widget t", "options": { "ignoreCase": true } } }, { "not": { "code": { "eq": "W-3" } } } ] } }"""));

        var filtered = await Parsed(owner, new JsonObject { ["queries"] = new JsonArray(both) });
        var decoy = await Parsed(owner, JsonNode.Parse("""{ "queries": [ { "entityType": "owner.widget", "pipeline": [ { "match": { "id": { "in": ["W-1"] } } } ] } ] }"""));
        var refused = await Parsed(owner, JsonNode.Parse("""{ "queries": [ { "entityType": "owner.widget", "pipeline": [ { "match": { "nope": { "eq": 1 } } } ] }, { "entityType": "x.y", "pipeline": [] } ] }"""));

        filtered["results"]![0]!["items"]!.AsArray().Select(item => item!["code"]!.GetValue<string>()).Should().Equal("W-2");
        decoy["results"]![0]!["items"]![0]!["code"]!.GetValue<string>().Should().Be("W-3");
        refused["results"]![0]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be("UNKNOWN_PATH");
        refused["results"]![1]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be("UNKNOWN_ENTITY");
    }

    [Fact]
    public async Task Many_answers_over_synthetic_widgets_with_honest_paging()
    {
        var owner = new ChaosOwner();
        owner.Set(new ChaosSettings { Many = 1200 });

        var paged = await Parsed(owner, JsonNode.Parse("""{ "queries": [ { "entityType": "owner.widget", "pipeline": [ { "match": { "name": { "startsWith": "Widget" } } }, { "project": { "code": 1 } }, { "page": { "limit": 500, "offset": 1000, "includeTotalCount": true } } ] } ] }"""));
        var first = await Parsed(owner, JsonNode.Parse("""{ "queries": [ { "entityType": "owner.widget", "pipeline": [ { "project": { "code": 1 } }, { "page": { "limit": 500 } } ] } ] }"""));

        paged["results"]![0]!["items"]!.AsArray().Should().HaveCount(200);
        paged["results"]![0]!["items"]![0]!["code"]!.GetValue<string>().Should().Be("W-1001");
        paged["results"]![0]!["pageInfo"]!["totalCount"]!.GetValue<int>().Should().Be(1200);
        paged["results"]![0]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>().Should().BeFalse();
        first["results"]![0]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>().Should().BeTrue();
        first["results"]![0]!["pageInfo"]!.AsObject().ContainsKey("totalCount").Should().BeFalse();
    }

    [Fact]
    public async Task Slow_answers_correctly_after_the_delay_and_hang_never_answers()
    {
        var owner = new ChaosOwner();
        owner.Set(new ChaosSettings { Mode = ChaosMode.Slow, Delay = TimeSpan.FromMilliseconds(300) });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var slow = await Batch(owner);

        watch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(280);
        JsonNode.Parse(slow.Text)!["results"]!.AsArray().Should().HaveCount(2);

        owner.Set(ChaosMode.Hang);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await FluentActions.Awaiting(() => Batch(owner, cancellationToken: cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(ChaosMode.Garbage, "not json at all")]
    [InlineData(ChaosMode.Empty, "")]
    [InlineData(ChaosMode.WrongShape, """{"results":"nope"}""")]
    public async Task A_malformed_body_arrives_as_200(ChaosMode mode, string body)
    {
        var owner = new ChaosOwner();
        owner.Set(mode);

        var answer = await Batch(owner);

        answer.Should().Be((HttpStatusCode.OK, body));
    }

    [Fact]
    public async Task Status_answers_the_status_with_a_plain_text_body()
    {
        var owner = new ChaosOwner();
        owner.Set(new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.Unauthorized });

        (await Batch(owner)).Should().Be((HttpStatusCode.Unauthorized, "chaos: status 401"));
    }

    [Fact]
    public async Task The_shape_breaking_modes_break_exactly_one_thing()
    {
        var owner = new ChaosOwner();

        owner.Set(ChaosMode.Short);
        (await Parsed(owner))["results"]!.AsArray().Should().HaveCount(1);

        owner.Set(ChaosMode.NullEntry);
        var nullEntry = await Parsed(owner);
        nullEntry["results"]![0].Should().BeNull();
        nullEntry["results"]![1]!["items"]![0]!["code"]!.GetValue<string>().Should().Be("W-3");

        owner.Set(ChaosMode.ItemsNotArray);
        (await Parsed(owner))["results"]![0]!["items"]!.GetValue<string>().Should().Be("x");

        owner.Set(ChaosMode.MissingField);
        var missing = (await Parsed(owner))["results"]![0]!["items"]!.AsArray();
        missing.Should().HaveCount(2);
        missing.Should().OnlyContain(item => !item!.AsObject().ContainsKey("code") && item.AsObject().ContainsKey("name"));

        owner.Set(ChaosMode.DuplicateKeys);
        var duplicates = (await Parsed(owner))["results"]![0]!["items"]!.AsArray();
        duplicates.Select(item => item!["code"]!.GetValue<string>()).Should().Equal("W-1", "W-1");
        duplicates[0]!["name"]!.GetValue<string>().Should().NotBe(duplicates[1]!["name"]!.GetValue<string>());

        owner.Set(new ChaosSettings { Mode = ChaosMode.Refuse, Code = "PAGE_SIZE_EXCEEDED" });
        var refused = (await Parsed(owner))["results"]!.AsArray();
        refused.Should().HaveCount(2);
        refused.Should().OnlyContain(result => result!["type"]!.GetValue<string>() == "validation_error" && result["errors"]![0]!["code"]!.GetValue<string>() == "PAGE_SIZE_EXCEEDED");
    }

    [Fact]
    public async Task Reset_fails_while_the_body_is_read_and_huge_streams_more_than_64_megabytes()
    {
        var owner = new ChaosOwner();
        owner.Set(ChaosMode.Reset);

        // Read whole, the reset surfaces the way a real one does: a failed copy with the IOException inside.
        await FluentActions.Awaiting(() => Batch(owner)).Should().ThrowAsync<HttpRequestException>().WithInnerException(typeof(IOException));

        owner.Set(ChaosMode.Huge);
        using var client = new HttpClient(owner, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        using var response = await client.PostAsync("OxQL/batch", new StringContent(Two.ToJsonString(), Encoding.UTF8, "application/json"));
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer)) > 0)
            total += read;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        total.Should().BeGreaterThan(64L * 1024 * 1024);
    }

    [Fact]
    public async Task Times_resets_the_owner_by_itself_and_the_counters_count()
    {
        var owner = new ChaosOwner();
        owner.Set(new ChaosSettings { Mode = ChaosMode.Status, Times = 2 });

        var statuses = new[] { (await Batch(owner)).Status, (await Batch(owner)).Status, (await Batch(owner)).Status };

        statuses.Should().Equal(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        owner.Settings.Mode.Should().Be(ChaosMode.Ok);
        owner.BatchCalls.Should().Be(3);
    }

    [Fact]
    public async Task Closed_refuses_every_connection_until_another_mode_reopens_it()
    {
        var owner = new ChaosOwner();
        owner.Set(ChaosMode.Closed);

        await FluentActions.Awaiting(() => Batch(owner)).Should().ThrowAsync<HttpRequestException>().Where(error => error.HttpRequestError == HttpRequestError.ConnectionError);
        await FluentActions.Awaiting(() => Health(owner)).Should().ThrowAsync<HttpRequestException>();

        owner.Set(ChaosMode.Ok);
        (await Batch(owner)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_health_switch_is_independent_of_the_mode()
    {
        var owner = new ChaosOwner();
        owner.SetHealth(ChaosHealth.Down);

        (await Health(owner)).Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Batch(owner)).Status.Should().Be(HttpStatusCode.OK);

        owner.SetHealth(ChaosHealth.Hang);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await FluentActions.Awaiting(() => Health(owner, cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        owner.HealthCalls.Should().Be(2);
    }

    [Fact]
    public async Task The_log_records_every_request_with_headers_body_and_the_mode_that_answered()
    {
        var owner = new ChaosOwner();
        await Batch(owner);
        var mark = owner.Mark();
        owner.Set(ChaosMode.Garbage);
        await Batch(owner);

        var since = owner.BatchesSince(mark);

        since.Should().ContainSingle();
        since[0].AnsweredBy.Should().Be(ChaosMode.Garbage);
        since[0].Header("X-Probe").Should().Be("self-test");
        since[0].Queries.Should().HaveCount(2);
        owner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void A_frozen_owner_refuses_every_change()
    {
        var owner = new ChaosOwner().Freeze();

        FluentActions.Invoking(() => owner.Set(ChaosMode.Hang)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => owner.SetHealth(ChaosHealth.Down)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => owner.Reset()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Mounted_in_a_private_fleet_it_answers_the_engine_and_sees_the_callers_organisation()
    {
        await using var fleet = await CorpusFleet.CreateAsync("chaos-self-test", [LabService.Conformance]);
        var client = fleet.Client(LabService.Conformance);
        var mark = fleet.Owner.Mark();

        var answer = await client.SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "widgetCodeExplicit", "as": "w", "select": ["name"] } },
              { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1, "w": 1 } } ]
            """);

        answer.ShouldHaveIds(Corpus.AllIds(Corpus.Conformance));
        answer.Strings("w.name").Should().Equal("Widget One", "Widget Two", "Widget Three");

        var batches = fleet.Owner.BatchesSince(mark);

        batches.Should().ContainSingle("one batch per resolve stage and page");
        batches[0].Header(LabIdentity.OrganisationHeader).Should().Be(LabIdentity.OrganisationA.ToString());
        batches[0].Queries.Should().OnlyContain(query => query["entityType"]!.GetValue<string>() == ChaosOwner.Entity);
    }

    [Fact]
    public async Task The_shared_fleet_owner_is_frozen_and_answers_correctly()
    {
        var fleet = await CorpusFleet.SharedAsync();

        fleet.Owner.Frozen.Should().BeTrue();

        var answer = await fleet.Client(LabService.Conformance).SendAsync(Corpus.Conformance, """
            [ { "match": { "name": { "eq": "Alpha" } } },
              { "resolve": { "path": "widgetCodeExplicit", "as": "w", "select": ["name"] } } ]
            """);

        answer.ShouldHaveIds([Corpus.IdOf(Corpus.Conformance, "c-alpha")]);
        answer.Strings("w.name").Should().Equal("Widget One");
    }
}
