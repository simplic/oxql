using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Hosts;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// Per-row join outcomes and their policy on a real server (DESIGN §3.6, §3.4.3): a missing
/// reference is <c>null</c> by default, <c>RESOLVE_MISSING</c> under <c>onMissing: "report"</c> and a
/// 422 under <c>"refuse"</c> or <c>strict</c>; an ambiguous key and a truncated <c>elements: "all"</c>
/// always travel and refuse under strict, as do a truncated lookup, a flatten cut at its depth and
/// a report page that holds fewer rows than match (<c>PAGE_INCOMPLETE</c>). An inline resolve finds
/// its missing rows on the page. The report fleet serves the owner and page cases; a private
/// database holds the dangling, duplicated and over-long references no fleet seed has locally.
/// </summary>
[Trait("Category", "Integration")]
public class JoinsOutcomesTests
{
    private static string Id(Guid id) => id.ToString("D");

    private static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger, Org.R);

    private static JsonObject Strict(string entity, string pipeline)
    {
        var body = Json.Request(entity, JsonNode.Parse(pipeline)!);

        body["strict"] = true;

        return body;
    }

    // ---- the report fleet ---------------------------------------------------------------------

    [Fact]
    public async Task A_clerk_no_employee_has_is_null_by_default_reported_under_report_and_refused_under_strict()
    {
        var client = await LedgerClient();
        var pipeline = $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.MissingClerkTransactionId)}}" } } },
              { "resolve": { "path": "createUserId", "as": "clerk"ONMISSING } } ]
            """;

        var plain = await client.SendAsync(ReportSeed.Transaction, JsonNode.Parse(pipeline.Replace("ONMISSING", ""))!);

        plain.ShouldBeOk().ShouldHaveNoDiagnostics("onMissing null reports only owner failures");
        plain.Items.Single()!["clerk"].Should().BeNull();

        var reported = await client.SendAsync(ReportSeed.Transaction, JsonNode.Parse(pipeline.Replace("ONMISSING", """, "onMissing": "report" """))!);
        var missing = reported.ShouldBeOk().ShouldHaveDiagnostic("RESOLVE_MISSING");

        missing["stage"]!.GetValue<int>().Should().Be(1);
        missing["path"]!.GetValue<string>().Should().Be("createUserId");
        missing["params"]!.ToJsonString().Should().Be(
            $$"""{"alias":"clerk","count":1,"truncated":false,"rows":[{"row":0,"key":"{{Id(ReportSeed.UnknownUserId)}}","outcome":"not_found"}]}""");

        var refused = await client.QueryAsync(Strict(ReportSeed.Transaction, pipeline.Replace("ONMISSING", "")));

        refused.ShouldRefuse("RESOLVE_MISSING", 422);
        refused.Type.Should().Be("not_executable");
        refused.Errors.Single()["params"]!["rows"]![0]!["outcome"]!.GetValue<string>().Should().Be("not_found");
    }

    [Fact]
    public async Task A_strict_lookup_cut_at_its_limit_is_refused_with_LOOKUP_TRUNCATED()
    {
        var client = await Lab.ClientAsync(LabService.Transport, Org.R);
        var pipeline = $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.ShipmentId)}}" } } },
              { "lookup": { "from": "transport.delivery_attempt", "path": "shipmentId", "as": "attempts", "limit": 2, "sort": [ { "dateTime": "desc" } ] } } ]
            """;

        var refused = await client.QueryAsync(Strict(ReportSeed.Shipment, pipeline));

        refused.ShouldRefuse("LOOKUP_TRUNCATED", 422);
        refused.Errors.Single()["params"]!["limit"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task A_strict_flatten_cut_at_its_depth_is_refused_with_UNWIND_DEPTH_TRUNCATED()
    {
        var refused = await (await LedgerClient()).QueryAsync(Strict(ReportSeed.Transaction, $$"""
            [ { "match": { "id": { "eq": "{{Id(ReportSeed.DeepNestingTransactionId)}}" } } },
              { "unwind": { "path": "items", "flatten": "items", "as": "item" } } ]
            """));

        refused.ShouldRefuse("UNWIND_DEPTH_TRUNCATED", 422);
    }

    [Fact]
    public async Task A_strict_report_page_that_holds_fewer_rows_than_match_is_PAGE_INCOMPLETE()
    {
        var client = await LedgerClient();
        const string pipeline = """[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": PAGE } } ]""";

        var refused = await client.QueryAsync(Strict(ReportSeed.BillingLine, pipeline.Replace("PAGE", "2")));
        var error = refused.ShouldRefuse("PAGE_INCOMPLETE", 422);

        error["params"]!.ToJsonString().Should().Be("""{"limit":2,"max":5000}""");
        refused.ErrorCodes.Should().Equal(["PAGE_INCOMPLETE"]);

        (await client.QueryAsync(Strict(ReportSeed.BillingLine, pipeline.Replace("PAGE", "50")))).ShouldBeOk().Items.Should().HaveCount(ReportSeed.ErpLineIds.Count);
        (await client.SendAsync(ReportSeed.BillingLine, JsonNode.Parse(pipeline.Replace("PAGE", "2"))!)).ShouldBeOk().HasNextPage.Should().BeTrue("outside strict a page is a page");
    }

    // ---- a private database: dangling, duplicated and over-long local references ---------------

    public sealed class OcOrder
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Number { get; set; } = "";

        [OxQLReference("oc.customer")]
        public Guid CustomerId { get; set; }

        [OxQLReference("oc.customer")]
        public List<Guid> CustomerIds { get; set; } = [];

        [OxQLReference("oc.depot", "id", Item = "lines")]
        public Guid LineId { get; set; }
    }

    public sealed class OcCustomer
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = "";
    }

    public sealed class OcDepot
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = "";

        public List<OcLine> Lines { get; set; } = [];
    }

    public sealed class OcLine
    {
        public Guid Id { get; set; }

        public string Code { get; set; } = "";
    }

    private static readonly Guid Customer = Ids.Of(Spaces.Dangling, Org.A, 1);
    private static readonly Guid Gone = Ids.Of(Spaces.Dangling, Org.A, 99);
    private static readonly Guid SharedLine = Ids.Of(Spaces.Dangling, Org.A, 11);
    private static readonly Guid OwnLine = Ids.Of(Spaces.Dangling, Org.A, 12);
    private static readonly Guid GoneLine = Ids.Of(Spaces.Dangling, Org.A, 98);

    private static BsonBinaryData Bin(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonDocument Owned(long n, string name) => new() { ["_id"] = Bin(Ids.Of(Spaces.Dangling, Org.A, n)), ["OrganizationId"] = EngineDirect.OrganisationValue, ["Name"] = name };

    /// <summary>
    /// Three orders: the first resolves its customer, a line of one depot and 101 customer ids (one
    /// more than <c>MaxLookupLimit</c>); the second a customer that is gone and a line two depots
    /// hold; the third no customer and a line no depot holds.
    /// </summary>
    private static async Task<(TestDatabase Owned, CustomHost Host)> StartAsync(string name)
    {
        var owned = await MongoFixture.CreateDatabaseAsync(name);

        await owned.Database.GetCollection<BsonDocument>("customers").InsertOneAsync(Owned(1, "Alice"));

        var one = Owned(21, "North");
        one["Lines"] = new BsonArray { new BsonDocument { ["_id"] = Bin(SharedLine), ["Code"] = "N-SHARED" }, new BsonDocument { ["_id"] = Bin(OwnLine), ["Code"] = "N-OWN" } };

        var two = Owned(22, "South");
        two["Lines"] = new BsonArray { new BsonDocument { ["_id"] = Bin(SharedLine), ["Code"] = "S-SHARED" } };

        await owned.Database.GetCollection<BsonDocument>("depots").InsertManyAsync([one, two]);

        BsonDocument Order(long n, Action<BsonDocument> fill)
        {
            var order = new BsonDocument { ["_id"] = Bin(Ids.Of(Spaces.Dangling, Org.A, n)), ["OrganizationId"] = EngineDirect.OrganisationValue, ["Number"] = $"O-{n}" };

            fill(order);

            return order;
        }

        await owned.Database.GetCollection<BsonDocument>("orders").InsertManyAsync(
        [
            Order(31, order => { order["CustomerId"] = Bin(Customer); order["LineId"] = Bin(OwnLine); order["CustomerIds"] = new BsonArray(Enumerable.Repeat(Bin(Customer), 101)); }),
            Order(32, order => { order["CustomerId"] = Bin(Gone); order["LineId"] = Bin(SharedLine); order["CustomerIds"] = new BsonArray { Bin(Customer) }; }),
            Order(33, order => { order["LineId"] = Bin(GoneLine); order["CustomerIds"] = new BsonArray(); }),
        ]);

        var model = ClrModelBuilder.Build(
        [
            new EntityDeclaration("oc.order", "oc.order", typeof(OcOrder), "orders", null, false),
            new EntityDeclaration("oc.customer", "oc.customer", typeof(OcCustomer), "customers", null, false),
            new EntityDeclaration("oc.depot", "oc.depot", typeof(OcDepot), "depots", null, false),
        ]);

        return (owned, await CustomHost.StartAsync(LabService.Staff, owned.Database, new CustomHost.Wiring { Model = model }));
    }

    private static Task<WireAnswer> StrictAsync(CustomHost host, string pipeline) =>
        host.PostAsync("OxQL/query", Strict("oc.order", pipeline).ToJsonString(), Org.A.Id());

    [Fact]
    public async Task An_inline_resolve_finds_its_missing_rows_on_the_page_even_when_the_projection_drops_the_reference()
    {
        var (owned, host) = await StartAsync("e10a_inline_missing");
        await using var _ = owned;
        await using var __ = host;
        const string pipeline = """
            [ { "sort": [ { "number": "asc" } ] },
              { "resolve": { "path": "customerId", "as": "customer", "select": ["name"]ONMISSING } },
              { "project": { "number": 1, "customer": 1 } } ]
            """;

        var plain = await host.SendAsync("oc.order", pipeline.Replace("ONMISSING", ""));

        plain.ShouldBeOk().ShouldHaveNoDiagnostics();

        var reported = await host.SendAsync("oc.order", pipeline.Replace("ONMISSING", """, "onMissing": "report" """));
        var missing = reported.ShouldBeOk().ShouldHaveDiagnostic("RESOLVE_MISSING");

        reported.Items.Select(item => item!["customer"]?["name"]?.GetValue<string>()).Should().Equal("Alice", null, null);
        reported.Items.Should().AllSatisfy(item => item!.AsObject().ContainsKey("customerId").Should().BeFalse("the kept reference never reaches the wire row"));
        missing["params"]!.ToJsonString().Should().Be(
            $$"""{"alias":"customer","count":1,"truncated":false,"rows":[{"row":1,"key":"{{Id(Gone)}}","outcome":"not_found"}]}""", "the third order has no customer: reference_null, not data loss");

        (await StrictAsync(host, pipeline.Replace("ONMISSING", ""))).ShouldRefuse("RESOLVE_MISSING", 422);
    }

    [Fact]
    public async Task A_line_two_depots_hold_is_ambiguous_always_and_a_line_none_holds_is_missing_under_report()
    {
        var (owned, host) = await StartAsync("e10a_keyed_outcomes");
        await using var _ = owned;
        await using var __ = host;
        const string pipeline = """
            [ { "sort": [ { "number": "asc" } ] },
              { "resolve": { "path": "lineId", "as": "line", "select": ["code"]ONMISSING } } ]
            """;

        var plain = await host.SendAsync("oc.order", pipeline.Replace("ONMISSING", ""));

        plain.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_AMBIGUOUS"], plain.ToString());
        plain.Items.Select(item => item!["line"]?["code"]?.GetValue<string>()).Should().Equal("N-OWN", "N-SHARED", null);
        plain.Diagnostics.Single()["params"]!.ToJsonString().Should().Be(
            $$"""{"alias":"line","count":1,"truncated":false,"rows":[{"row":1,"key":"{{Id(SharedLine)}}","outcome":"ambiguous"}]}""");

        var reported = await host.SendAsync("oc.order", pipeline.Replace("ONMISSING", """, "onMissing": "report" """));

        reported.ShouldBeOk().DiagnosticCodes.Should().Equal(["RESOLVE_AMBIGUOUS", "RESOLVE_MISSING"]);
        reported.Diagnostics[1]["params"]!["rows"]!.ToJsonString().Should().Be($$"""[{"row":2,"key":"{{Id(GoneLine)}}","outcome":"not_found"}]""");

        var refused = await StrictAsync(host, pipeline.Replace("ONMISSING", ""));

        refused.StatusCode.Should().Be(422, refused.ToString());
        refused.ErrorCodes.Should().Equal(["RESOLVE_AMBIGUOUS", "RESOLVE_MISSING"], "strict refuses on every loss at once");
    }

    [Fact]
    public async Task Elements_all_over_MaxLookupLimit_is_RESOLVE_TRUNCATED_and_refused_under_strict()
    {
        var (owned, host) = await StartAsync("e10a_resolve_truncated");
        await using var _ = owned;
        await using var __ = host;
        const string pipeline = """
            [ { "sort": [ { "number": "asc" } ] },
              { "resolve": { "path": "customerIds", "as": "customers", "elements": "all", "select": ["name"] } } ]
            """;

        var answer = await host.SendAsync("oc.order", pipeline);
        var truncated = answer.ShouldBeOk().ShouldHaveDiagnostic("RESOLVE_TRUNCATED");

        answer.Items[0]!["customers"]!.AsArray().Should().HaveCount(100);
        truncated["params"]!.ToJsonString().Should().Be("""{"alias":"customers","limit":100,"rows":1}""");

        (await StrictAsync(host, pipeline)).ShouldRefuse("RESOLVE_TRUNCATED", 422);
    }
}
