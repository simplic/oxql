using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using FleetModels = OxQL.IntegrationTests.Fleet.Models.Fleet;
using Ledger = OxQL.IntegrationTests.Fleet.Models.Ledger;
using Transport = OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Suites.Joins;

/// <summary>
/// The remote lookup on the fleet (DESIGN §3.4.4): a <c>lookup</c> at the ledger whose <c>from</c> is a
/// transport entity, run by the keyed fetch as a remote resolve is — one grouped owner query per key
/// chunk over the internal batch route — with the reference held on the other side: transport billing
/// lines, inside the shipment's <c>billingLines</c>, name the ledger transaction they were billed on.
/// Covered: whole shipments through the collection (a shipment with two lines of one invoice counts
/// once), the elements themselves with their owning shipment (<c>entity#item</c>, <c>parentAs</c>),
/// <c>sort</c> + <c>first</c> ranked at the owner, a filter inside, truncation at the limit, strict,
/// an owner that does not answer, a stage continued at transport that looks up a third service, the
/// limits every owner enforces itself, and explain. The rows are written per fleet (organisation A,
/// ids <c>5e5…</c>).
/// </summary>
[Trait("Category", "Integration")]
public class JoinsRemoteLookupTests(JoinsRemoteLookupTests.Rows rows) : IClassFixture<JoinsRemoteLookupTests.Rows>
{
    private static readonly Guid Invoice = Guid.Parse("5e560001-0000-4000-8000-000000000001");
    private static readonly Guid NoLines = Guid.Parse("5e560001-0000-4000-8000-000000000002");
    private static readonly Guid Busy = Guid.Parse("5e560001-0000-4000-8000-000000000003");

    /// <summary>Five more transactions without lines: parents enough for one owner query to outgrow the owner's page.</summary>
    private static readonly IReadOnlyList<Guid> Quiet = [.. Enumerable.Range(4, 5).Select(n => Guid.Parse($"5e560001-0000-4000-8000-{n:D12}"))];

    private static readonly Guid S1 = Guid.Parse("5e560002-0000-4000-8000-000000000001");
    private static readonly Guid S2 = Guid.Parse("5e560002-0000-4000-8000-000000000002");
    private static readonly Guid S3 = Guid.Parse("5e560002-0000-4000-8000-000000000003");
    private static readonly Guid S4 = Guid.Parse("5e560002-0000-4000-8000-000000000004");

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>
    /// The fleet of this suite: no corpus, only its own rows. The invoice has three lines on two
    /// shipments (S1 twice, S2 once); the busy transaction one line on each of S2, S3 and S4; the
    /// other transactions none. A crane of the fleet service travels with S2. Dropped with the suite.
    /// </summary>
    public sealed class Rows : IAsyncLifetime
    {
        public CorpusFleet Fleet { get; private set; } = null!;

        public async Task InitializeAsync() => Fleet = await CorpusFleet.CreateAsync("remote_lookup", seed: [], extra: SeedAsync);

        public async Task DisposeAsync() => await Fleet.DisposeAsync();
    }

    private Task<CorpusFleet> FleetAsync() => Task.FromResult(rows.Fleet);

    private static async Task SeedAsync(LabFleet lab)
    {
        var organisation = Org.A.Id();

        Transport.BillingLine Line(int n, Guid transaction, string text, double total, int day) => new()
        {
            Id = Guid.Parse($"5e560003-0000-4000-8000-{n:D12}"), AssignedTransactionId = transaction, Text = text, TotalPrice = total,
            Date = new DateTime(2026, 5, day, 0, 0, 0, DateTimeKind.Utc),
        };

        Transport.Shipment Shipment(Guid id, string number, int month, params Transport.BillingLine[] lines) => new()
        {
            Id = id, OrganizationId = organisation, ShipmentNumber = number, LoadStart = new DateTime(2026, month, 1, 0, 0, 0, DateTimeKind.Utc), BillingLines = [.. lines],
        };

        var ledger = await lab.DatabaseAsync(LabService.Ledger);
        await ledger.GetCollection<BsonDocument>(LabService.Ledger.Model.Entities["ledger.transaction"].Collection).InsertManyAsync(
        [
            new Ledger.Transaction { Id = Invoice, OrganizationId = organisation, Number = "RL-1" }.ToBsonDocument(),
            new Ledger.Transaction { Id = NoLines, OrganizationId = organisation, Number = "RL-2" }.ToBsonDocument(),
            new Ledger.Transaction { Id = Busy, OrganizationId = organisation, Number = "RL-3" }.ToBsonDocument(),
            .. Quiet.Select((id, n) => new Ledger.Transaction { Id = id, OrganizationId = organisation, Number = $"RL-{n + 4}" }.ToBsonDocument()),
        ]);

        var transport = await lab.DatabaseAsync(LabService.Transport);
        await transport.GetCollection<BsonDocument>(LabService.Transport.Model.Entities["transport.shipment"].Collection).InsertManyAsync(
        [
            Shipment(S1, "S-1", 1, Line(1, Invoice, "freight", 100, 1), Line(2, Invoice, "toll", 20, 2)).ToBsonDocument(),
            Shipment(S2, "S-2", 3, Line(3, Invoice, "freight", 50, 9), Line(4, Busy, "freight", 70, 3)).ToBsonDocument(),
            Shipment(S3, "S-3", 2, Line(5, Busy, "freight", 80, 4)).ToBsonDocument(),
            Shipment(S4, "S-4", 4, Line(6, Busy, "toll", 10, 5)).ToBsonDocument(),
        ]);

        var fleetDb = await lab.DatabaseAsync(LabService.Fleet);
        await fleetDb.GetCollection<BsonDocument>(LabService.Fleet.Model.Entities["fleet.equipment"].Collection).InsertOneAsync(
            new FleetModels.Equipment { Id = Guid.Parse("5e560004-0000-4000-8000-000000000001"), OrganizationId = organisation, Name = "Crane", AssignedShipmentId = S2 }.ToBsonDocument());
    }

    private async Task<LabClient> LedgerAsync() => (await FleetAsync()).Client(LabService.Ledger);

    /// <summary>The three transactions in key order, then the lookup stages and a projection.</summary>
    private static string Over(string stages, string project = "\"number\": 1") => $$"""
        {
          "entityType": "ledger.transaction",
          "pipeline": [
            { "match": { "id": { "in": ["{{Id(Invoice)}}", "{{Id(NoLines)}}", "{{Id(Busy)}}"] } } },
            {{stages}},
            { "project": { {{project}} } },
            { "sort": [ { "id": "asc" } ] }
          ]
        }
        """;

    private static JsonObject Row(WireAnswer answer, Guid id) =>
        answer.Items.OfType<JsonObject>().Single(row => row["id"]!.GetValue<string>() == Id(id));

    private static IReadOnlyList<string?> Strings(JsonNode? array, string member) =>
        (array as JsonArray ?? throw new InvalidOperationException($"not an array: {array?.ToJsonString()}")).Select(item => item?[member]?.GetValue<string>()).ToList();

    [Fact]
    public async Task A_lookup_of_whole_shipments_through_their_billing_lines_answers_each_shipment_once_per_invoice_in_key_order()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "select": ["shipmentNumber"] } }
            """, "\"number\": 1, \"shipments\": 1"));

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();
        Strings(Row(answer, Invoice)["shipments"], "shipmentNumber").Should().Equal(["S-1", "S-2"], "S-1 holds two lines of the invoice and is one shipment");
        Row(answer, NoLines)["shipments"]!.AsArray().Should().BeEmpty("no line names the transaction");
        Strings(Row(answer, Busy)["shipments"], "shipmentNumber").Should().Equal(["S-2", "S-3", "S-4"]);
        Row(answer, Invoice)["shipments"]!.AsArray()[0]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "shipmentNumber"], "the select and the key; the key the owner grouped by stays at the owner");
    }

    [Fact]
    public async Task Sort_and_first_take_the_latest_shipment_of_each_invoice_ranked_at_the_owner_and_null_where_there_is_none()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "latest",
                          "sort": [ { "loadStart": "desc" } ], "first": true, "select": ["shipmentNumber", "loadStart"] } }
            """, "\"number\": 1, \"latest\": 1"));

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();
        Row(answer, Invoice)["latest"]!["shipmentNumber"]!.GetValue<string>().Should().Be("S-2", "March is later than January");
        Row(answer, Busy)["latest"]!["shipmentNumber"]!.GetValue<string>().Should().Be("S-4", "April; S-2 would be first by key");
        Row(answer, NoLines)["latest"].Should().BeNull();
    }

    [Fact]
    public async Task An_element_lookup_answers_the_billing_lines_themselves_with_their_owning_shipment_in_the_sort_order()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment#billingLines", "path": "assignedTransactionId", "as": "lines",
                          "select": ["text", "totalPrice"], "sort": [ { "totalPrice": "desc" } ],
                          "parentAs": "shipments" } }
            """, "\"number\": 1, \"lines\": 1, \"shipments.shipmentNumber\": 1"));

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();
        var invoice = Row(answer, Invoice);

        invoice["lines"]!.AsArray().Select(line => line!["totalPrice"]!.GetValue<double>()).Should().Equal([100d, 50d, 20d]);
        Strings(invoice["lines"], "text").Should().Equal(["freight", "freight", "toll"]);
        Strings(invoice["shipments"], "shipmentNumber").Should().Equal(["S-1", "S-2", "S-1"], "one owning row per line, in the lines' order");
        invoice["shipments"]!.AsArray()[0]!["entity"]!.GetValue<string>().Should().Be("transport.shipment");
        Row(answer, NoLines)["lines"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_filter_inside_the_lookup_is_applied_by_the_owner_before_it_ranks()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment#billingLines", "path": "assignedTransactionId", "as": "tolls",
                          "filter": { "text": { "eq": "toll" } }, "select": ["text", "totalPrice"], "first": true, "sort": [ { "date": "desc" } ] } }
            """, "\"number\": 1, \"tolls\": 1"));

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();
        Row(answer, Invoice)["tolls"]!["totalPrice"]!.GetValue<double>().Should().Be(20, "the invoice's one toll, though a freight line is later");
        Row(answer, Busy)["tolls"]!["totalPrice"]!.GetValue<double>().Should().Be(10);
        Row(answer, NoLines)["tolls"].Should().BeNull();
    }

    [Fact]
    public async Task A_parent_with_more_children_than_the_limit_keeps_the_first_and_is_reported_and_strict_refuses_it()
    {
        const string Stage = """
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "limit": 2,
                          "sort": [ { "loadStart": "asc" } ], "select": ["shipmentNumber"] } }
            """;
        var client = await LedgerAsync();
        var answer = await client.QueryAsync(Over(Stage, "\"number\": 1, \"shipments\": 1"));

        answer.ShouldBeOk();
        Strings(Row(answer, Busy)["shipments"], "shipmentNumber").Should().Equal(["S-3", "S-2"], "February and March; April is cut");
        Strings(Row(answer, Invoice)["shipments"], "shipmentNumber").Should().Equal(["S-1", "S-2"], "exactly the limit is not truncated");
        var truncated = answer.ShouldHaveDiagnostic("LOOKUP_TRUNCATED");
        truncated["stage"]!.GetValue<int>().Should().Be(1);
        truncated["params"]!["alias"]!.GetValue<string>().Should().Be("shipments");
        truncated["params"]!["limit"]!.GetValue<int>().Should().Be(2);
        truncated["params"]!["rows"]!.GetValue<int>().Should().Be(1);

        var strict = await client.QueryAsync(JsonNode.Parse(Over(Stage, "\"number\": 1, \"shipments\": 1"))!.AsObject().Also(body => body["strict"] = true));

        strict.ShouldRefuse("LOOKUP_TRUNCATED", 422);
    }

    [Fact]
    public async Task A_lookup_continued_at_transport_from_the_owning_shipment_looks_up_a_third_service()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment#billingLines", "path": "assignedTransactionId", "as": "lastLine", "first": true,
                          "sort": [ { "date": "desc" } ], "select": ["text", "date"], "parentAs": "ship" } },
            { "lookup": { "from": "fleet.equipment", "path": "assignedShipmentId", "on": "ship", "as": "gear", "first": true, "select": ["name"] } }
            """, "\"number\": 1, \"lastLine\": 1, \"ship.shipmentNumber\": 1, \"gear\": 1"));

        answer.ShouldBeOk().ShouldHaveNoDiagnostics();
        var invoice = Row(answer, Invoice);

        invoice["ship"]!["shipmentNumber"]!.GetValue<string>().Should().Be("S-2", "the invoice's latest line (9 May) is on S-2");
        invoice["gear"]!["name"]!.GetValue<string>().Should().Be("Crane", "looked up at fleet by transport, for the owning shipment");
        Row(answer, Busy)["ship"]!["shipmentNumber"]!.GetValue<string>().Should().Be("S-4");
        Row(answer, Busy)["gear"].Should().BeNull("nothing travels with S-4");
        Row(answer, NoLines)["gear"].Should().BeNull();
    }

    [Fact]
    public async Task A_lookup_continued_from_an_owning_shipment_the_projection_does_not_name_answers_the_same_gear_without_the_shipment()
    {
        const string Stages = """
            { "lookup": { "from": "transport.shipment#billingLines", "path": "assignedTransactionId", "as": "lastLine", "first": true,
                          "sort": [ { "date": "desc" } ], "parentAs": "ship" } },
            { "lookup": { "from": "fleet.equipment", "path": "assignedShipmentId", "on": "ship", "as": "gear", "first": true } }
            """;
        var client = await LedgerAsync();
        var only = (await client.QueryAsync(Over(Stages, "\"number\": 1, \"gear.name\": 1"))).ShouldBeOk();
        var with = (await client.QueryAsync(Over(Stages, "\"number\": 1, \"lastLine.text\": 1, \"ship.shipmentNumber\": 1, \"gear.name\": 1"))).ShouldBeOk();

        var expected = with.Items.Select(row => row!.DeepClone().AsObject()).ToList();

        foreach (var row in expected)
        {
            row.Remove("lastLine").Should().BeTrue();
            row.Remove("ship").Should().BeTrue();
        }

        only.Items.OfType<JsonObject>().Select(row => row.ToJsonString()).Should().Equal(expected.Select(row => row.ToJsonString()),
            "the lookup's rows are fetched for the lookup continued on their owning shipment and cut from the row");
        Row(only, Invoice)["gear"]!["name"]!.GetValue<string>().Should().Be("Crane");
        Row(only, Invoice).Select(member => member.Key).Should().Equal("id", "number", "gear");

        var explained = await client.ExplainHereAsync(Over(Stages, "\"number\": 1, \"gear.name\": 1"));

        explained.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explained.Text);
        explained.Body["aliases"]!["ship"]!["shows"]!.AsArray().Should().BeEmpty();
        explained.Body["aliases"]!["lastLine"]!["shows"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_stage_under_a_lookup_array_is_not_continuable_and_an_element_lookup_names_a_collection()
    {
        var client = await LedgerAsync();

        var array = await client.QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments" } },
            { "lookup": { "from": "fleet.equipment", "path": "assignedShipmentId", "on": "shipments", "as": "gear" } }
            """));

        array.ShouldRefuse("NOT_CONTINUABLE", 400)["stage"]!.GetValue<int>().Should().Be(2);

        var parent = await client.QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "parentAs": "p" } }
            """));

        parent.ShouldRefuse("OPTION_NOT_APPLICABLE", 400);
    }

    [Fact]
    public async Task An_entity_of_a_service_this_host_knows_no_owner_for_is_UNKNOWN_ENTITY_naming_the_service()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "billing.run", "path": "transactionId", "as": "runs" } }
            """));

        var error = answer.ShouldRefuse("UNKNOWN_ENTITY", 400);
        error["message"]!.GetValue<string>().Should().Be("'billing.run' is not an entity of this host, and this host knows no owner for 'billing'.");
    }

    [Fact]
    public async Task A_path_the_owner_does_not_declare_as_a_reference_to_the_parent_is_the_lookups_LOOKUP_NOT_DECLARED()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment", "path": "shipmentNumber", "as": "shipments" } }
            """, "\"number\": 1, \"shipments\": 1"));

        answer.ShouldRefuse("RESOLVE_REFUSED", 422);
        var inner = answer.Errors.Single(error => error["code"]!.GetValue<string>() == "LOOKUP_NOT_DECLARED");
        inner["stage"]!.GetValue<int>().Should().Be(1);
        inner["path"]!.GetValue<string>().Should().Be("shipmentNumber");
        inner["params"]!["owner"]!["service"]!.GetValue<string>().Should().Be("transport");
    }

    [Fact]
    public async Task An_owner_that_does_not_answer_leaves_the_alias_null_says_so_and_strict_refuses()
    {
        await using var own = await CorpusFleet.CreateAsync("remote_lookup_owner", seed: [], extra: SeedAsync);
        own.Owner.Set(new ChaosSettings { Mode = ChaosMode.Status, Status = HttpStatusCode.InternalServerError });

        var body = Over("""{ "lookup": { "from": "owner.widget", "path": "transactionId", "as": "widgets" } }""", "\"number\": 1, \"widgets\": 1");
        var answer = await own.Client(LabService.Ledger).QueryAsync(body);

        answer.ShouldBeOk();
        answer.Values("widgets").Should().AllSatisfy(value => value.Should().BeNull("unanswered is not an empty array"));
        var unreachable = answer.ShouldHaveDiagnostic("RESOLVE_UNREACHABLE");
        unreachable["params"]!["service"]!.GetValue<string>().Should().Be("owner");
        unreachable["params"]!["aliases"]!.AsArray().Select(alias => alias!.GetValue<string>()).Should().Equal(["widgets"]);

        var strict = await own.Client(LabService.Ledger).QueryAsync(JsonNode.Parse(body)!.AsObject().Also(request => request["strict"] = true));

        strict.ShouldRefuse("RESOLVE_UNREACHABLE", 422);
    }

    [Fact]
    public async Task An_owner_that_stalls_past_the_chain_ceiling_is_RESOLVE_TIMEOUT()
    {
        await using var own = await CorpusFleet.CreateAsync("remote_lookup_hang", seed: [], extra: SeedAsync);
        own.Owner.Set(new ChaosSettings { Mode = ChaosMode.Hang });
        var client = own.Variant(LabService.Ledger, "chain-300", new Dictionary<string, string?> { ["OxQL:Execution:ChainTimeoutMs"] = "300" });

        var answer = await client.QueryAsync(Over("""{ "lookup": { "from": "owner.widget", "path": "transactionId", "as": "widgets" } }""", "\"number\": 1, \"widgets\": 1"));

        answer.ShouldBeOk();
        answer.ShouldHaveDiagnostic("RESOLVE_TIMEOUT")["params"]!["aliases"]!.AsArray().Select(alias => alias!.GetValue<string>()).Should().Equal(["widgets"]);
        answer.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5), "the chain ceiling, not the request's own budget, ends the wait");
    }

    // ── the limits: each enforced where it is spent ──────────────────────────────────────────

    [Fact]
    public async Task A_limit_above_this_hosts_cap_is_LOOKUP_LIMIT_EXCEEDED_before_anything_is_sent()
    {
        var answer = await (await LedgerAsync()).QueryAsync(Over("""
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "limit": 101 } }
            """, "\"number\": 1, \"shipments\": 1"));

        answer.ShouldRefuse("LOOKUP_LIMIT_EXCEEDED", 400)["stage"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task The_owner_refuses_more_rows_per_key_than_its_own_lookup_limit_at_run_time_and_in_explain()
    {
        var shared = await FleetAsync();
        var client = shared.Variant(LabService.Ledger, "lookups-200", new Dictionary<string, string?> { ["OxQL:Limits:MaxLookupLimit"] = "200" });
        var body = Over("""
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "limit": 150 } }
            """, "\"number\": 1, \"shipments\": 1");

        var answer = await client.QueryAsync(body);

        answer.ShouldRefuse("RESOLVE_REFUSED", 422);
        var inner = answer.Errors.Single(error => error["code"]!.GetValue<string>() == "LOOKUP_LIMIT_EXCEEDED");
        inner["stage"]!.GetValue<int>().Should().Be(1, "the owner's refusal is the lookup's");
        inner["message"]!.GetValue<string>().Should().Contain("between 1 and 101");

        var explained = await client.ExplainHereAsync(new JsonObject { ["query"] = JsonNode.Parse(body) });

        explained.StatusCode.Should().Be(200, explained.ToString());
        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse("the remote check asks the owner before anything runs");
        explained.ErrorCodes.Should().Contain("LOOKUP_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task The_keys_one_request_asks_owners_for_are_capped_and_the_rest_is_RESOLVE_PARTIAL_which_strict_refuses()
    {
        var shared = await FleetAsync();
        var client = shared.Variant(LabService.Ledger, "keys-2", new Dictionary<string, string?> { ["OxQL:Limits:MaxResolveKeys"] = "2" });
        var body = Over("""
            { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "select": ["shipmentNumber"] } }
            """, "\"number\": 1, \"shipments\": 1");

        var answer = await client.QueryAsync(body);

        answer.ShouldBeOk();
        answer.ShouldHaveDiagnostic("RESOLVE_PARTIAL")["params"]!["max"]!.GetValue<int>().Should().Be(2);
        Row(answer, Busy)["shipments"].Should().BeNull("its key was not asked, so it is unanswered, not empty");
        Strings(Row(answer, Invoice)["shipments"], "shipmentNumber").Should().Equal(["S-1", "S-2"]);

        // A host of its own: the first run cached the keys it asked, and a cached key costs nothing.
        var fresh = shared.Variant(LabService.Ledger, "keys-2-strict", new Dictionary<string, string?> { ["OxQL:Limits:MaxResolveKeys"] = "2" });
        var strict = await fresh.QueryAsync(JsonNode.Parse(body)!.AsObject().Also(request => request["strict"] = true));

        strict.ShouldRefuse("RESOLVE_PARTIAL", 422);
    }

    [Fact]
    public async Task The_owner_refuses_a_page_larger_than_its_own_so_no_request_outgrows_the_owning_service()
    {
        var shared = await FleetAsync();

        // This host pages 2000 rows and, knowing nothing of the owner yet, asks it for all eight
        // parents in one query at 101 rows each: 808 rows, above the owner's page of 500. The owner
        // refuses rather than answer what it is not configured for, and the refusal is the lookup's.
        var client = shared.Variant(LabService.Ledger, "page-2000", new Dictionary<string, string?>
        {
            ["OxQL:Limits:MaxPageSize"] = "2000",
            ["OxQL:Limits:ResolveKeyChunk"] = "2000",
        });
        var ids = string.Join(", ", new[] { Invoice, NoLines, Busy }.Concat(Quiet).Select(id => $"\"{Id(id)}\""));

        var answer = await client.QueryAsync($$"""
            { "entityType": "ledger.transaction", "pipeline": [
                { "match": { "id": { "in": [{{ids}}] } } },
                { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "limit": 100 } } ] }
            """);

        answer.ShouldRefuse("RESOLVE_REFUSED", 422);
        var inner = answer.Errors.Single(error => error["code"]!.GetValue<string>() == "PAGE_SIZE_EXCEEDED");
        inner["stage"]!.GetValue<int>().Should().Be(1);
        inner["params"]!["owner"]!["service"]!.GetValue<string>().Should().Be("transport");

        // Eight parents at 11 rows each fit the owner's page: the same request with a smaller limit runs.
        var smaller = await client.QueryAsync($$"""
            { "entityType": "ledger.transaction", "pipeline": [
                { "match": { "id": { "in": [{{ids}}] } } },
                { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "limit": 10 } } ] }
            """);

        smaller.ShouldBeOk().ShouldHaveNoDiagnostics();
    }

    // ── explain ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Explain_shows_the_keyed_remote_step_its_owner_query_the_remote_check_and_the_notes()
    {
        var answer = await (await LedgerAsync()).ExplainHereAsync(new JsonObject
        {
            ["query"] = JsonNode.Parse(Over("""
                { "lookup": { "from": "transport.shipment#billingLines", "path": "assignedTransactionId", "as": "lines", "select": ["text"],
                              "sort": [ { "totalPrice": "desc" } ], "limit": 5, "parentAs": "shipments" } }
                """, "\"number\": 1, \"lines\": 1, \"shipments\": 1")),
            ["include"] = new JsonArray("shape", "notes", "plan"),
            ["catalog"] = new JsonArray(new JsonObject { ["id"] = "d", ["entity"] = "ledger.transaction", ["referencing"] = true }),
        });

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Body!["valid"]!.GetValue<bool>().Should().BeTrue(answer.ToString());
        answer.Body["engine"]!["capabilities"]!.AsArray().Select(value => value!.GetValue<string>()).Should().Contain("lookup.remote");

        var step = answer.Body["stages"]!.AsArray().Single(each => each!["index"]!.GetValue<int>() == 1)!;
        var owner = answer.Body["owners"]![step["placement"]!["owner"]!.GetValue<int>()]!;
        var lines = answer.Body["aliases"]!["lines"]!;

        step["kind"]!.GetValue<string>().Should().Be("lookup");
        step["placement"]!["executor"]!.GetValue<string>().Should().Be("keyed-remote");
        step["placement"]!["phase"]!.GetValue<string>().Should().Be("afterPage");
        lines.AsObject().ContainsKey("reference").Should().BeFalse("a lookup follows no reference of this host");
        owner["service"]!.GetValue<string>().Should().Be("transport");
        owner["answered"]!.GetValue<bool>().Should().BeTrue();
        lines["targets"]![0]!["target"]!.GetValue<string>().Should().Be("transport.shipment#billingLines");
        lines["targets"]![0]!["grouped"]!.GetValue<bool>().Should().BeTrue();
        lines["many"]!.GetValue<bool>().Should().BeTrue("the alias holds every child the lookup found");
        lines["type"]!.GetValue<string>().Should().Be("t:transport.shipment#billingLines", "the owner's answer to the check types the children");
        answer.Body["aliases"]!["shipments"]!["type"]!.GetValue<string>().Should().Be("t:transport.shipment");

        var query = owner["queries"]!.AsArray().Single(each => each!["stage"]!.GetValue<int>() == 1)!["query"]!;
        query["entityType"]!.GetValue<string>().Should().Be("transport.shipment");
        query["keyedBy"]!["path"]!.GetValue<string>().Should().Be("billingLines.assignedTransactionId");
        query["keyedBy"]!["perKey"]!.GetValue<int>().Should().Be(6, "one more than the limit tells a truncated parent");
        query["keyedBy"]!["references"]!.GetValue<string>().Should().Be("ledger.transaction");
        query["keyedBy"]!["keys"]!.AsArray().Select(key => key!.GetValue<string>()).Should().Equal(["…"]);
        query["pipeline"]!.AsArray().Select(stage => stage!.AsObject().First().Key).Should().Equal(["sort", "project", "page"]);
        query["pipeline"]![0]!["sort"]![0]!["oxEl.totalPrice"]!.GetValue<string>().Should().Be("desc");

        var codes = answer.Body["notes"]!.AsArray().Where(note => note!["stage"]?.GetValue<int>() == 1).Select(note => note!["code"]!.GetValue<string>()).ToList();
        codes.Should().Contain(["OWNER_BINDS", "LOOKUP_LIMIT", "JOIN_AFTER_PAGE", "REMOTE_LOOKUP"]);
        codes.Should().NotContain("MISSING_POLICY", "a lookup has no missing reference");
        codes.Should().NotContain("REMOTE_UNCHECKED", "the owner checked it");

        var bounds = answer.Body["notes"]!.AsArray().Single(note => note!["code"]!.GetValue<string>() == "REMOTE_LOOKUP")!;
        bounds["params"]!["service"]!.GetValue<string>().Should().Be("transport");
        bounds["params"]!["perKey"]!.GetValue<int>().Should().Be(6);
        bounds["params"]!["keysPerQuery"]!.GetValue<int>().Should().Be(83, "the page of 500 holds 83 keys at 6 rows each");
        bounds["params"]!["sorted"]!.GetValue<bool>().Should().BeTrue();

        var creates = step["creates"]!.AsArray().Select(created => created!.GetValue<string>()).ToList();
        creates.Should().Equal(["lines", "shipments"]);

        var columns = answer.Body["result"]!["columns"]!.AsArray();
        columns.Single(column => column!["path"]!.GetValue<string>() == "lines")!["kind"]!.GetValue<string>().Should().Be("array");

        var described = answer.Body["catalog"]!.AsArray().Single()!;
        described["remoteLookup"]!.GetValue<bool>().Should().BeTrue();
        described["type"]!.GetValue<string>().Should().Be("t:ledger.transaction");
    }

    [Fact]
    public async Task Explain_answers_a_select_path_the_owners_child_lacks_as_the_lookups_error_from_the_remote_check()
    {
        var answer = await (await LedgerAsync()).ExplainHereAsync(new JsonObject
        {
            ["query"] = JsonNode.Parse(Over("""
                { "lookup": { "from": "transport.shipment", "path": "billingLines.assignedTransactionId", "as": "shipments", "select": ["shipmentNumbr"] } }
                """, "\"number\": 1, \"shipments\": 1")),
        });

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Body!["valid"]!.GetValue<bool>().Should().BeFalse();
        var error = answer.Errors.First(each => each["code"]!.GetValue<string>() == "UNKNOWN_PATH");
        error["stage"]!.GetValue<int>().Should().Be(1);
        error["path"]!.GetValue<string>().Should().Be("shipmentNumbr");
        error["params"]!["owner"]!["service"]!.GetValue<string>().Should().Be("transport");
    }
}

internal static class JsonObjectAlso
{
    /// <summary>The object after <paramref name="change"/>, for a request changed in one expression.</summary>
    public static JsonObject Also(this JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }
}
