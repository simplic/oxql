using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The keyed fetch <i>by keys</i> of OxQL 2.1 (DESIGN §3.5.2 steps 1, 3–5, §3.3.3, §3.3.4) against
/// fakes: typed cases selected per row, <c>elements</c> first and all, the <c>KeyAs</c> conversion,
/// item targets with their owning row, the grouped owner query (<c>keyedBy</c>) with ambiguity,
/// and <see cref="SelfOwner"/>, this host answering its own targets through its own engine as an
/// internal call. A local target's owner query runs through the fake runner, which answers per
/// entity; a remote one through the fake client.
/// </summary>
public class KeyedFetchByKeysTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static readonly Guid InvoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Customer1 = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Customer2 = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid Missing = Guid.Parse("c0000000-0000-0000-0000-0000000000ff");
    private static readonly Guid Shipment1 = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid Shipment2 = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid Tour1 = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid Line1 = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Line2 = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    /// <summary>Answers each aggregate with the rows of its entity and records every call; the owner's rows are what its pipeline would leave.</summary>
    private sealed class EntityRunner : IAggregateRunner
    {
        public Dictionary<string, List<BsonDocument>> Rows { get; } = new(StringComparer.Ordinal);

        public List<(EntityDef Entity, IReadOnlyList<BsonDocument> Stages)> Calls { get; } = [];

        public IReadOnlyList<IReadOnlyList<BsonDocument>> StagesOf(string entity) =>
            Calls.Where(call => call.Entity.Id == entity).Select(call => call.Stages).ToList();

        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((entity, stages));

            return Task.FromResult<IReadOnlyList<BsonDocument>>(Rows.TryGetValue(entity.Id, out var rows) ? rows.Select(row => row.DeepClone().AsBsonDocument).ToList() : []);
        }
    }

    private static (MongoQueryEngine Engine, EntityRunner Runner, FakeRemoteClient Client) Host(Action<OxQLOptions>? configure = null)
    {
        var runner = new EntityRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options(configure);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        return (engine, runner, client);
    }

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonDocument InvoiceRow(Action<BsonDocument> fill)
    {
        var row = new BsonDocument { ["_id"] = Id(InvoiceId), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "RE-1" };

        fill(row);

        return row;
    }

    private static BsonDocument CustomerRow(Guid id, string name) =>
        new() { ["_id"] = Id(id), ["OrganizationId"] = Id(BindHost.Organisation), ["Name"] = name, ["Code"] = name.ToUpperInvariant() };

    /// <summary>An owner row of an item target as the grouped query leaves it: the parent with its element under <c>oxEl</c>.</summary>
    private static BsonDocument ItemRow(Guid parent, string number, Guid line, string code, string display = "Number") => new()
    {
        ["_id"] = Id(parent),
        ["OrganizationId"] = Id(BindHost.Organisation),
        [display] = number,
        ["oxEl"] = new BsonDocument { ["_id"] = Id(line), ["Code"] = code, ["Amount"] = new BsonDecimal128(5m) },
    };

    private static async Task<QueryResult> RunAsync(MongoQueryEngine engine, string pipeline)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, pipeline), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static async Task<ResolveResult> FetchAsync(MongoQueryEngine engine, EntityRunner runner, FakeRemoteClient client, string pipeline, IReadOnlyList<BsonDocument> page)
    {
        var bound = ((BindOutcome.Bound)await new Binder(ResolveModel.Model, BindHost.Cursors).BindAsync(BindHost.Request(Invoice, pipeline), BindHost.Context(), CancellationToken.None)).Pipeline;
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));
        var fetch = new KeyedFetch(client, engine, new OwnerFetchCache(BindHost.Options()), BindHost.Options());

        return await fetch.ByKeysAsync(compiled, page, BindHost.Context(), TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    // ---- SelfOwner: a local keyed target ------------------------------------------------------

    [Fact]
    public async Task A_keyed_local_resolve_runs_through_this_hosts_own_engine_as_an_internal_call_and_explains()
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Customer2), Id(Missing), Id(Customer1) })];
        runner.Rows["rc.customer"] = [CustomerRow(Customer1, "Alice"), CustomerRow(Customer2, "Bob")];

        var result = await RunAsync(engine, """[{ "resolve": { "path": "customerIds", "as": "customer", "elements": "first" } }]""");

        result.Items[0]!["customer"]!["name"]!.GetValue<string>().Should().Be("Bob", "the first element whose key resolves, in stored order");
        client.Calls.Should().BeEmpty("a local target never goes to a remote owner");

        var owner = runner.StagesOf("rc.customer").Should().ContainSingle().Subject;

        owner[0]["$match"].AsBsonDocument.Contains("OrganizationId").Should().BeTrue("the owner query is scoped like any query");
        owner.Should().Contain(stage => stage.Contains("$match") && stage["$match"].AsBsonDocument.Contains("_id"), "an entity keyed by its own key is asked with a plain key match");
        owner.Should().NotContain(stage => stage.Contains("$setWindowFields"), "no key can have two rows");

        var explained = await engine.ExplainAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "customerIds", "as": "customer", "elements": "first" } }]"""), BindHost.Context());

        explained.Should().BeOfType<ExplainOutcome.Success>("the keyed fetch exists, so a keyed resolve is no longer refused at explain");
    }

    [Fact]
    public async Task Elements_all_collects_every_resolved_target_and_reports_the_rest_per_element()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument> { InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Customer1), Id(Missing), Id(Customer2) }) };
        runner.Rows["rc.customer"] = [CustomerRow(Customer1, "Alice"), CustomerRow(Customer2, "Bob")];

        var result = await FetchAsync(engine, runner, client, """[{ "resolve": { "path": "customerIds", "as": "customers", "elements": "all" } }]""", page);

        result.Rows[0]["customers"]!.AsArray().Select(node => node!["name"]!.GetValue<string>()).Should().Equal("Alice", "Bob");
        result.Outcomes.Should().ContainSingle().Which.Should().Be(new KeyedRowOutcome(0, "customers", 0, 1, Missing.ToString("D"), KeyedOutcome.NotFound));
        result.Calls.Should().Be(0, "this host is not a remote owner");
    }

    [Fact]
    public async Task Elements_all_holds_at_most_MaxLookupLimit_targets_and_says_so()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument> { InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Customer1), Id(Customer2), Id(Customer1) }) };
        runner.Rows["rc.customer"] = [CustomerRow(Customer1, "Alice"), CustomerRow(Customer2, "Bob")];

        var bound = ((BindOutcome.Bound)await new Binder(ResolveModel.Model, BindHost.Cursors).BindAsync(
            BindHost.Request(Invoice, """[{ "resolve": { "path": "customerIds", "as": "customers", "elements": "all" } }]"""), BindHost.Context(), CancellationToken.None)).Pipeline;
        var options = BindHost.Options(configure => configure.Limits.MaxLookupLimit = 2);
        var fetch = new KeyedFetch(client, engine, new OwnerFetchCache(options), options);

        var result = await fetch.ByKeysAsync(MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)), page, BindHost.Context(), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Rows[0]["customers"]!.AsArray().Should().HaveCount(2);
        result.Truncations.Should().ContainSingle().Which.Should().Be(new KeyedTruncation(0, "customers", 0, 3));
    }

    [Fact]
    public async Task Elements_first_folds_a_row_without_a_resolving_element_to_its_strongest_outcome()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument>
        {
            InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Missing) }),
            InvoiceRow(row => row["CustomerIds"] = new BsonArray()),
            InvoiceRow(_ => { }),
        };

        var result = await FetchAsync(engine, runner, client, """[{ "resolve": { "path": "customerIds", "as": "customer", "elements": "first" } }]""", page);

        result.Rows.Select(row => row["customer"]).Should().AllSatisfy(value => value.Should().BeNull());
        result.Outcomes.Select(outcome => (outcome.Row, outcome.Outcome)).Should().Equal(
            (0, KeyedOutcome.NotFound), (1, KeyedOutcome.ReferenceNull), (2, KeyedOutcome.ReferenceNull));
    }

    // ---- KeyAs and cases ------------------------------------------------------------------------

    [Fact]
    public async Task A_converted_key_is_sent_normalised_and_a_value_that_does_not_convert_is_an_invalid_key()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument>
        {
            InvoiceRow(row => row["ShipmentKey"] = Shipment1.ToString("D").ToUpperInvariant()),
            InvoiceRow(row => row["ShipmentKey"] = "SHIP-12"),
            InvoiceRow(row => row["ShipmentKey"] = BsonNull.Value),
        };
        runner.Rows["rc.shipment"] = [new BsonDocument { ["_id"] = Id(Shipment1), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "SN-1" }];

        var result = await FetchAsync(engine, runner, client, """[{ "resolve": { "path": "shipmentKey", "as": "shipment" } }]""", page);

        result.Rows[0]["shipment"]!["id"]!.GetValue<string>().Should().Be(Shipment1.ToString("D"), "any guid format converts");
        result.Outcomes.Select(outcome => (outcome.Row, outcome.Key, outcome.Outcome)).Should().Equal(
            (1, "SHIP-12", KeyedOutcome.InvalidKey), (2, (string?)null, KeyedOutcome.ReferenceNull));

        var keys = runner.StagesOf("rc.shipment").Single().Select(stage => stage.ToJson()).Where(stage => stage.Contains("$in")).ToList();

        keys.Should().ContainSingle("only the key that converts is asked for");
    }

    [Fact]
    public async Task Each_row_asks_the_targets_of_the_case_its_stored_value_selects_and_a_value_no_case_selects_is_excluded()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument>
        {
            InvoiceRow(row => row["Billing"] = new BsonDocument { ["DataType"] = "shipment", ["ReferenceId"] = Shipment1.ToString("D") }),
            InvoiceRow(row => row["Billing"] = new BsonDocument { ["DataType"] = "tour", ["ReferenceId"] = Tour1.ToString("D").ToUpperInvariant() }),
            InvoiceRow(row => row["Billing"] = new BsonDocument { ["DataType"] = "Tariff", ["ReferenceId"] = "TARIFF-2026" }),
        };
        runner.Rows["rc.shipment"] = [new BsonDocument { ["_id"] = Id(Shipment1), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "SN-1" }];
        runner.Rows["rc.tour"] = [new BsonDocument { ["_id"] = Id(Tour1), ["OrganizationId"] = Id(BindHost.Organisation), ["Name"] = "Tour 1" }];

        var result = await FetchAsync(engine, runner, client, """[{ "resolve": { "path": "billing.referenceId", "as": "ref" } }]""", page);

        result.Rows[0]["ref"]!["id"]!.GetValue<string>().Should().Be(Shipment1.ToString("D"));
        result.Rows[1]["ref"]!["id"]!.GetValue<string>().Should().Be(Tour1.ToString("D"));
        result.Rows[2]["ref"].Should().BeNull();
        result.Outcomes.Should().ContainSingle().Which.Outcome.Should().Be(KeyedOutcome.Excluded, "a tariff selects no case: not data loss");
        runner.StagesOf("rc.shipment").Should().ContainSingle("the shipment case asks the shipment owner once");
        runner.StagesOf("rc.tour").Should().ContainSingle();
    }

    [Fact]
    public async Task A_variant_case_selects_by_the_discriminator_of_the_holding_object()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument>
        {
            InvoiceRow(row => row["Slot"] = new BsonDocument { ["_t"] = "RcDriverSlot", ["HolderId"] = Id(Customer1) }),
            InvoiceRow(row => row["Slot"] = new BsonDocument { ["_t"] = "RcVehicleSlot", ["HolderId"] = Id(Customer2) }),
        };
        runner.Rows["rc.customer"] = [CustomerRow(Customer1, "Alice"), CustomerRow(Customer2, "Bob")];

        var result = await FetchAsync(engine, runner, client, """[{ "resolve": { "path": "slot.holderId", "as": "holder" } }]""", page);

        result.Rows[0]["holder"]!["name"]!.GetValue<string>().Should().Be("Alice");
        result.Rows[1]["holder"].Should().BeNull("the vehicle slot's reference applies to no case");
        result.Outcomes.Should().ContainSingle().Which.Should().Be(new KeyedRowOutcome(0, "holder", 1, null, null, KeyedOutcome.Excluded));
    }

    // ---- item targets, grouped owner queries, ambiguity -----------------------------------------

    [Fact]
    public async Task An_item_target_is_asked_grouped_per_key_and_resolves_to_the_element_with_its_owning_row()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument> { InvoiceRow(row => row["BillingLineId"] = Id(Line1)) };
        runner.Rows["rc.shipment"] = [ItemRow(Shipment1, "SN-1", Line1, "FREIGHT")];

        var result = await FetchAsync(engine, runner, client,
            """[{ "resolve": { "path": "billingLineId", "as": "line", "select": ["code"], "parentAs": "shipment" } }]""", page);

        var line = result.Rows[0]["line"]!.AsObject();

        line["code"]!.GetValue<string>().Should().Be("FREIGHT");
        line["id"]!.GetValue<string>().Should().Be(Line1.ToString("D"), "the matched member always travels");
        result.Rows[0]["shipment"]!["entity"]!.GetValue<string>().Should().Be("rc.shipment");
        result.Rows[0]["shipment"]!["id"]!.GetValue<string>().Should().Be(Shipment1.ToString("D"));
        result.Outcomes.Should().BeEmpty();

        var owner = runner.StagesOf("rc.shipment").Single().Select(stage => stage.ToJson()).ToList();

        owner.Should().Contain(stage => stage.Contains("\"$unwind\" : \"$oxEl\""), "each matching element is a row of its own");
        owner.Should().Contain(stage => stage.Contains("$setWindowFields") && stage.Contains("\"partitionBy\" : \"$oxEl._id\""));
        owner.Should().Contain(stage => stage.Contains("\"$lte\" : 2"), "at most two rows per key");
    }

    [Fact]
    public async Task A_key_two_owner_rows_hold_is_ambiguous_and_resolves_to_the_first_by_target_order_then_record_key()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument>
        {
            InvoiceRow(row => row["LocalSource"] = new BsonDocument { ["Type"] = "logistics", ["_id"] = Id(Line1) }),
            InvoiceRow(row => row["LocalSource"] = new BsonDocument { ["Type"] = "logistics", ["_id"] = Id(Line2) }),
        };
        runner.Rows["rc.shipment"] = [ItemRow(Shipment1, "SN-1", Line1, "S1")];
        runner.Rows["rc.tour"] = [ItemRow(Tour1, "Tour 1", Line1, "T1", display: "Name"), ItemRow(Tour1, "Tour 1", Line2, "T2", display: "Name")];

        var result = await FetchAsync(engine, runner, client,
            """[{ "resolve": { "path": "localSource.id", "as": "line", "select": ["code"] } }]""", page);

        result.Rows[0]["line"]!["code"]!.GetValue<string>().Should().Be("S1", "the shipment is the first target of the case");
        result.Rows[1]["line"]!["code"]!.GetValue<string>().Should().Be("T2", "the tour alone holds the second key");
        result.Outcomes.Should().ContainSingle().Which.Should().Be(new KeyedRowOutcome(0, "line", 0, null, Line1.ToString("D"), KeyedOutcome.Ambiguous));
    }

    [Fact]
    public async Task A_remote_item_target_sends_keyedBy_with_the_select_rebased_onto_the_element_and_mixes_with_local_targets()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument>
        {
            InvoiceRow(row => row["Source"] = new BsonDocument { ["Type"] = "remote", ["_id"] = Id(Line1) }),
            InvoiceRow(row => row["Source"] = new BsonDocument { ["Type"] = "logistics", ["_id"] = Id(Line2) }),
        };
        runner.Rows["rc.tour"] = [ItemRow(Tour1, "Tour 1", Line2, "LOCAL", display: "Name")];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(
            new JsonObject { ["id"] = Shipment2.ToString("D"), ["oxEl"] = new JsonObject { ["id"] = Line1.ToString("D"), ["code"] = "REMOTE" } });

        var result = await FetchAsync(engine, runner, client,
            """[{ "resolve": { "path": "source.id", "as": "line", "select": ["code"], "filter": { "amount": { "gt": 1 } } } }]""", page);

        result.Rows[0]["line"]!["code"]!.GetValue<string>().Should().Be("REMOTE");
        result.Rows[1]["line"]!["code"]!.GetValue<string>().Should().Be("LOCAL");
        result.Calls.Should().Be(1);

        var (service, request, _) = client.Calls.Should().ContainSingle().Subject;
        var query = request.Queries.Should().ContainSingle().Subject;

        service.Should().Be("transport");
        query.EntityType.Should().Be("transport.shipment");
        query.KeyedBy!.Path.Should().Be("billingLines.id");
        query.KeyedBy.PerKey.Should().Be(2);
        query.KeyedBy.Keys!.Value.EnumerateArray().Select(key => key.GetString()).Should().Equal(Line1.ToString("D"));
        query.Pipeline[0].Match!.Condition!.Path.Should().Be("oxEl.amount", "the filter on the item is rebased onto the element");
        query.Pipeline[1].Project!.Fields.Keys.Should().BeEquivalentTo(["oxEl.code", "oxEl.id"]);
        query.Pipeline[2].Page!.Limit.Should().Be(2, "two rows per key");
    }

    [Fact]
    public async Task A_grouped_chunk_holds_at_most_half_the_owners_page()
    {
        var (engine, runner, client) = Host();
        var lines = Enumerable.Range(1, 5).Select(n => Guid.Parse($"b0000000-0000-0000-0000-00000000010{n}")).ToList();
        var page = lines.Select(line => InvoiceRow(row => row["Source"] = new BsonDocument { ["Type"] = "remote", ["_id"] = Id(line) })).ToList();

        var bound = ((BindOutcome.Bound)await new Binder(ResolveModel.Model, BindHost.Cursors).BindAsync(
            BindHost.Request(Invoice, """[{ "resolve": { "path": "source.id", "as": "line" } }]"""), BindHost.Context(), CancellationToken.None)).Pipeline;
        var options = BindHost.Options(configure => configure.Limits.MaxPageSize = 4);
        var fetch = new KeyedFetch(client, engine, new OwnerFetchCache(options), options);

        var result = await fetch.ByKeysAsync(MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)), page, BindHost.Context(), TimeSpan.FromSeconds(5), CancellationToken.None);

        client.Calls.SelectMany(call => call.Request.Queries).Select(query => query.KeyedBy!.Keys!.Value.GetArrayLength()).Should().Equal(2, 2, 1);
        result.Outcomes.Should().HaveCount(5).And.AllSatisfy(outcome => outcome.Outcome.Should().Be(KeyedOutcome.NotFound));
    }

    [Fact]
    public async Task An_owner_that_does_not_answer_leaves_its_keys_owner_unanswered()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument> { InvoiceRow(row => row["Source"] = new BsonDocument { ["Type"] = "remote", ["_id"] = Id(Line1) }) };
        client.Unreachable.Add("transport");

        var result = await FetchAsync(engine, runner, client, """[{ "resolve": { "path": "source.id", "as": "line" } }]""", page);

        result.Rows[0]["line"].Should().BeNull();
        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Codes.ResolveUnreachable);
        result.Outcomes.Should().ContainSingle().Which.Outcome.Should().Be(KeyedOutcome.OwnerUnanswered);
    }

    [Fact]
    public async Task A_grouped_answer_is_cached_per_key_with_its_ambiguity()
    {
        var (engine, runner, client) = Host();
        var page = new List<BsonDocument> { InvoiceRow(row => row["Source"] = new BsonDocument { ["Type"] = "remote", ["_id"] = Id(Line1) }) };
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(
            new JsonObject { ["id"] = Shipment1.ToString("D"), ["oxEl"] = new JsonObject { ["id"] = Line1.ToString("D") } },
            new JsonObject { ["id"] = Shipment2.ToString("D"), ["oxEl"] = new JsonObject { ["id"] = Line1.ToString("D") } });

        var bound = ((BindOutcome.Bound)await new Binder(ResolveModel.Model, BindHost.Cursors).BindAsync(
            BindHost.Request(Invoice, """[{ "resolve": { "path": "source.id", "as": "line" } }]"""), BindHost.Context(), CancellationToken.None)).Pipeline;
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));
        var fetch = new KeyedFetch(client, engine, new OwnerFetchCache(BindHost.Options()), BindHost.Options());

        var first = await fetch.ByKeysAsync(compiled, page, BindHost.Context(), TimeSpan.FromSeconds(5), CancellationToken.None);
        var again = await fetch.ByKeysAsync(compiled, page, BindHost.Context(), TimeSpan.FromSeconds(5), CancellationToken.None);

        client.Calls.Should().ContainSingle("the second page is answered from the cache");
        first.Outcomes.Single().Outcome.Should().Be(KeyedOutcome.Ambiguous);
        again.Outcomes.Single().Outcome.Should().Be(KeyedOutcome.Ambiguous, "the cache keeps both rows of the key");
        again.CacheHits.Should().Be(1);
    }

    // ---- the engine without a remote client ---------------------------------------------------

    [Fact]
    public async Task A_host_without_a_remote_client_runs_a_keyed_local_resolve_and_refuses_a_remote_target()
    {
        var runner = new EntityRunner();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, BindHost.Options());
        runner.Rows[Invoice] = [InvoiceRow(row => row["ShipmentKey"] = Shipment1.ToString("D"))];

        var local = await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "shipmentKey", "as": "shipment" } }]"""), BindHost.Context());

        local.Should().BeOfType<QueryOutcome.Success>();

        var remote = await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "source.id", "as": "line" } }]"""), BindHost.Context());

        remote.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors!.Single().Code.Should().Be(Codes.ResolveUnavailable);
    }
}
