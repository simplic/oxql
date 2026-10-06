using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.NonQuery;

/// <summary>
/// The split of a request's time on the wire: every query and batch answer carries
/// <c>Server-Timing</c> (total, the database with its commands and round trips, this host's own
/// share and its parts); a request that asks (<c>"timing": true</c> or <c>X-OxQL-Timing</c>) gets
/// the breakdown beside <c>pageInfo</c>, with every command as the database saw it and every owner
/// a join asked, another service's owner with its own timing.
/// </summary>
[Trait("Category", "Integration")]
public class TimingTests
{
    private const string Templates = """{ "entityType": "transport.shipment_template", "pipeline": [ { "sort": [ { "templateName": "asc" } ] }, { "page": { "limit": 100, "count": true } } ] }""";

    private static async Task<(HttpResponseMessage Response, JsonNode Body)> PostAsync(LabService service, string route, string body, Org org = Org.A, params (string Name, string Value)[] headers)
    {
        var host = await (await CorpusFleet.SharedAsync()).Fleet.HostAsync(service);
        var client = host.Client(org.Id());
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };

        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        var response = await client.SendAsync(request);

        return (response, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    /// <summary>The metrics of a <c>Server-Timing</c> value: name → (duration, description).</summary>
    private static Dictionary<string, (double Duration, string? Description)> Metrics(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Server-Timing", out var values).Should().BeTrue("every query answer says where its time went");

        return string.Join(", ", values!).Split(", ").Select(metric => metric.Split(';')).ToDictionary(
            parts => parts[0],
            parts => (double.Parse(parts.Single(part => part.StartsWith("dur=", StringComparison.Ordinal))[4..], CultureInfo.InvariantCulture),
                parts.FirstOrDefault(part => part.StartsWith("desc=", StringComparison.Ordinal))?[5..].Trim('"')));
    }

    private static double Number(JsonNode? node) => node!.GetValue<double>();

    [Fact]
    public async Task Every_query_answer_carries_Server_Timing_and_the_body_nothing_unless_asked()
    {
        var (response, body) = await PostAsync(LabService.Transport, "OxQL/query", Templates);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.AsObject().Select(member => member.Key).Should().Equal(["items", "pageInfo"], "the breakdown is in the body only on request");

        var metrics = Metrics(response);

        metrics.Keys.Should().StartWith(["total", "db", "ours", "bind", "compile", "convert"]);
        metrics["db"].Description.Should().Be("3 cmd / 3 rt", "the addon definitions' read, then the page and the count beside it, each one round trip");
        metrics["addon"].Description.Should().Be("db", "the fleet's addon source reads its database on every request, and says so");
        metrics["addon"].Duration.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(metrics["db"].Duration);
        (metrics["total"].Duration - metrics["db"].Duration).Should().BeApproximately(metrics["ours"].Duration, 0.011);
        metrics["db"].Duration.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(metrics["total"].Duration);
        metrics.Should().ContainKey("net", "the driver knows its round-trip time to the server");
        response.Headers.Contains("Access-Control-Expose-Headers").Should().BeFalse("no origin asked");

        // A refusal says nothing of time: nothing ran.
        var (refused, _) = await PostAsync(LabService.Transport, "OxQL/query", """{ "entityType": "transport.shipment_template", "pipeline": [ { "match": { "nope": { "eq": 1 } } } ] }""");

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refused.Headers.Contains("Server-Timing").Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_breakdown_comes_beside_pageInfo_when_the_request_or_its_header_asks(bool byHeader)
    {
        var request = byHeader ? Templates : Templates.Replace("\"pipeline\"", "\"timing\": true, \"pipeline\"");
        var (response, body) = byHeader
            ? await PostAsync(LabService.Transport, "OxQL/query", request, Org.A, ("X-OxQL-Timing", "1"))
            : await PostAsync(LabService.Transport, "OxQL/query", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToJsonString());
        body.AsObject().Select(member => member.Key).Should().Equal("items", "pageInfo", "timing");

        var timing = body["timing"]!.AsObject();

        timing.Select(member => member.Key).Should().Equal("totalMs", "oursMs", "parseMs", "bindMs", "addonMs", "compileMs", "db", "resolve", "countWaitMs", "convertMs", "rows", "build");
        timing["rows"]!.GetValue<int>().Should().Be(100);
        (Number(timing["totalMs"]) - Number(timing["db"]!["ms"])).Should().BeApproximately(Number(timing["oursMs"]), 0.011, "the total less the database is this host's own");

        var database = timing["db"]!.AsObject();

        database.Select(member => member.Key).Should().Equal("ms", "commands", "roundTrips", "rttMs", "networkEstimateMs", "list");
        database["commands"]!.GetValue<int>().Should().Be(3);
        database["roundTrips"]!.GetValue<int>().Should().Be(3, "no getMore: the page's batch closes its cursor");

        var list = database["list"]!.AsArray();

        list.Select(command => command!["kind"]!.GetValue<string>()).Should().Equal("addon", "page", "count");

        // The addon definitions' read is the database's time: a command like the others, in the collection the source named.
        var read = list[0]!.AsObject();

        read.Select(member => member.Key).Should().Equal("kind", "collection", "ms", "docs", "parallelWith");
        read["collection"]!.GetValue<string>().Should().Be(TestAddonSource.Collection);
        read["parallelWith"].Should().BeNull();
        Number(read["ms"]).Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(Number(timing["addonMs"]) + 0.011, "the time the source was asked holds its read");
        Number(timing["oursMs"]).Should().BeLessThanOrEqualTo(Number(timing["totalMs"]) - Number(read["ms"]) - Number(list[1]!["ms"]) + 0.021, "neither the read nor the page is this host's own time");

        var page = list[1]!.AsObject();

        page.Select(member => member.Key).Should().Equal("kind", "collection", "ms", "firstBatchMs", "drainMs", "docs", "bytes", "getMores", "parallelWith");
        page["collection"]!.GetValue<string>().Should().Be("transport.shipment_template");
        page["docs"]!.GetValue<int>().Should().Be(101, "the page and the row that tells a next page exists");
        page["bytes"]!.GetValue<long>().Should().BeGreaterThan(101 * 50);
        page["getMores"]!.GetValue<int>().Should().Be(0);
        page["parallelWith"].Should().BeNull();
        (Number(page["firstBatchMs"]) + Number(page["drainMs"])).Should().BeApproximately(Number(page["ms"]), 0.021);
        list[2]!["parallelWith"]!.GetValue<int>().Should().Be(1, "the count left beside the page");
        list[2]!["docs"]!.GetValue<int>().Should().Be(1);

        // The header says the same sums.
        var metrics = Metrics(response);

        metrics["db"].Description.Should().Be("3 cmd / 3 rt");
        metrics["addon"].Duration.Should().BeApproximately(Number(read["ms"]), 0.011);
        metrics["total"].Duration.Should().BeGreaterThanOrEqualTo(Number(timing["totalMs"]) - 0.011, "the header is written after the body's numbers were taken");
    }

    [Fact]
    public async Task A_join_lists_this_hosts_own_owner_commands_and_another_services_owner_with_its_own_timing()
    {
        // A private fleet: nothing is cached, so both owners are asked.
        await using var fleet = await CorpusFleet.CreateAsync("timing_join", [LabService.Transport, LabService.Fleet]);
        var host = await fleet.Fleet.HostAsync(LabService.Transport);
        var client = host.Client();

        async Task<JsonObject> AskAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "OxQL/query")
            {
                Content = new StringContent("""
                    { "entityType": "transport.shipment_template", "timing": true, "pipeline": [
                        { "resolve": { "path": "createUserId", "as": "veh", "onMissing": "null" } },
                        { "project": { "id": 1, "templateName": 1, "veh.matchCode": 1 } },
                        { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 10 } } ] }
                    """, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            var response = await client.SendAsync(request);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToJsonString());

            return body["timing"]!.AsObject();
        }

        var cold = await AskAsync();
        var owners = cold["resolve"]!["owners"]!.AsArray();
        var remote = owners.Single(owner => owner!["remote"]!.GetValue<bool>())!;

        remote["service"]!.GetValue<string>().Should().Be("fleet");
        remote["entity"]!.GetValue<string>().Should().Be("fleet.vehicle");
        remote["keys"]!.GetValue<int>().Should().BeGreaterThan(0);
        Number(remote["ms"]).Should().BeGreaterThan(0);

        // The owner's own timing travels back: the database's share at the owner is known at the origin.
        var theirs = remote["timing"]!.AsArray().Should().ContainSingle().Subject!;

        theirs["db"]!["list"]!.AsArray().Select(command => command!["kind"]!.GetValue<string>()).Should().Contain("page").And.Contain("addon", "the owner read its own organisation's definitions of the vehicle");
        theirs["db"]!["list"]!.AsArray().Should().OnlyContain(command =>
            command!["collection"]!.GetValue<string>() == (command["kind"]!.GetValue<string>() == "addon" ? TestAddonSource.Collection : "fleet.vehicle"));
        Number(cold["resolve"]!["remoteMs"]).Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(Number(cold["resolve"]!["ms"]) + 0.011);
        cold["resolve"]!["calls"]!.GetValue<int>().Should().BeGreaterThan(0);

        // This host's own commands are all this request's: the page, and nothing of the other service's database.
        cold["db"]!["list"]!.AsArray().Where(command => command!["kind"]!.GetValue<string>() != "addon").Should().OnlyContain(command => command!["collection"]!.GetValue<string>().StartsWith("transport.", StringComparison.Ordinal));
        (Number(cold["totalMs"]) - Number(cold["db"]!["ms"])).Should().BeApproximately(Number(cold["oursMs"]), 0.011);

        // Warm, the owner is answered from the cache: no call, the keys counted as hits.
        var warm = await AskAsync();
        var kept = warm["resolve"]!["owners"]!.AsArray().Single(owner => owner!["remote"]!.GetValue<bool>())!;

        kept["keys"]!.GetValue<int>().Should().Be(0);
        kept["cacheHits"]!.GetValue<int>().Should().Be(remote["keys"]!.GetValue<int>());
        kept.AsObject().ContainsKey("timing").Should().BeFalse("nobody was asked");
        Number(warm["resolve"]!["remoteMs"]).Should().Be(0);
    }

    [Fact]
    public async Task A_keyed_join_on_this_host_lists_its_owner_query_as_a_resolve_command()
    {
        await using var fleet = await CorpusFleet.CreateAsync("timing_self", [LabService.Transport, LabService.Fleet]);
        var (_, body) = await PostOnAsync(fleet, LabService.Transport, $$"""
            { "entityType": "transport.shipment", "timing": true, "pipeline": [
                { "resolve": { "path": "tours.tourId", "as": "tour", "elements": "first", "onMissing": "null" } },
                { "project": { "shipmentNumber": 1, "tour.number": 1 } },
                { "sort": [ { "shipmentNumber": "asc" } ] }, { "page": { "limit": 50 } } ] }
            """);
        var timing = body["timing"]!.AsObject();
        var kinds = timing["db"]!["list"]!.AsArray().Select(command => (Kind: command!["kind"]!.GetValue<string>(), Collection: command["collection"]!.GetValue<string>())).ToList();

        kinds[0].Should().Be(("addon", TestAddonSource.Collection), "the shipment's addon definitions are read before anything is sent");

        var sent = kinds.Where(command => command.Kind != "addon").ToList();

        sent[0].Should().Be(("page", "transport.shipment"));
        sent.Skip(1).Should().OnlyContain(command => command.Kind == "resolve", "the owner queries this host runs for itself are this request's commands");
        timing["resolve"]!["owners"]!.AsArray().Should().OnlyContain(owner => !owner!["remote"]!.GetValue<bool>() && owner["service"]!.GetValue<string>() == "transport");
        Number(timing["resolve"]!["remoteMs"]).Should().Be(0);
        (Number(timing["totalMs"]) - Number(timing["db"]!["ms"])).Should().BeApproximately(Number(timing["oursMs"]), 0.011);
    }

    private static async Task<(HttpResponseMessage Response, JsonNode Body)> PostOnAsync(CorpusFleet fleet, LabService service, string body)
    {
        var host = await fleet.Fleet.HostAsync(service);
        var response = await host.Client().PostAsync("OxQL/query", new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")));
        var parsed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.OK, parsed.ToJsonString());

        return (response, parsed);
    }

    [Fact]
    public async Task A_batch_says_the_sum_in_its_header_and_each_query_its_own_breakdown_when_it_asks()
    {
        var asked = Templates.Replace("\"pipeline\"", "\"timing\": true, \"pipeline\"");
        var (response, body) = await PostAsync(LabService.Transport, "OxQL/batch", $$"""{ "queries": [ {{asked}}, {{Templates}} ] }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var metrics = Metrics(response);

        metrics["db"].Description.Should().Be("6 cmd / 6 rt", "two queries, each its addon definitions' read, its page and its count");
        (metrics["total"].Duration - metrics["db"].Duration).Should().BeApproximately(metrics["ours"].Duration, 0.011);

        var results = body["results"]!.AsArray();

        results[0]!.AsObject().ContainsKey("timing").Should().BeTrue();
        results[1]!.AsObject().ContainsKey("timing").Should().BeFalse("each query asks for itself");
        results[0]!["timing"]!["db"]!["commands"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task A_request_from_another_origin_is_told_the_header_is_exposed()
    {
        var (response, _) = await PostAsync(LabService.Transport, "OxQL/query", Templates, Org.A, ("Origin", "https://app.example"));

        response.Headers.GetValues("Access-Control-Expose-Headers").Should().Contain("Server-Timing");
    }
}
