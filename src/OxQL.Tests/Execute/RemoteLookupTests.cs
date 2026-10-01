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
/// The remote lookup (DESIGN §3.4.4) against fakes: a <c>lookup</c> whose <c>from</c> is another
/// service's entity binds as a keyed remote stage when this host reaches the owner, sends one grouped
/// owner query per key chunk (<c>keyedBy</c> with <c>references</c>, <c>rows</c>, one row more per key
/// than the limit, the lookup's sort before the projection), assigns each parent its children in the
/// owner's order, reports a truncated parent as <c>LOOKUP_TRUNCATED</c>, leaves an unanswered parent
/// null, and maps an owner's refusal back to the lookup. The owner side: <c>keyedBy.references</c>,
/// <c>keyedBy.rows</c>, the per-key cap and the window ranked by the query's sort.
/// </summary>
public class RemoteLookupTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static readonly Guid Invoice1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Invoice2 = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Invoice3 = Guid.Parse("10000000-0000-0000-0000-000000000003");

    private static string Id(Guid id) => id.ToString("D");

    private static RequestContext Reaching(Func<string, bool>? reachable = null) =>
        BindHost.Context() with { RemoteService = reachable ?? (service => service is "tr" or "crm") };

    private const string Shipments = """{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "shipments", "select": ["number"] } }""";

    private static async Task<BoundPipeline> BoundAsync(string pipeline, RequestContext? context = null) =>
        await BindHost.BoundAsync(ResolveModel.Model, Invoice, pipeline, context ?? Reaching());

    private static async Task<QueryValidationError> ErrorAsync(string pipeline, string code, RequestContext? context = null) =>
        await BindHost.ErrorAsync(ResolveModel.Model, Invoice, pipeline, code, context ?? Reaching());

    // ---- binding ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_lookup_of_another_services_entity_binds_as_a_keyed_remote_stage_keyed_by_the_parents_key()
    {
        var bound = await BoundAsync($"[{Shipments}]");
        var stage = bound.Stages.OfType<BoundStage.Resolve>().Single();

        stage.RemoteLookup.Should().NotBeNull();
        stage.Kind.Should().Be("lookup");
        stage.IsRemote.Should().BeTrue();
        stage.Executor.Should().Be(ResolveExecutor.Keyed);
        stage.NeedsKeyedFetch.Should().BeTrue("a lookup is never the plain 2.0 resolve");
        stage.Reference.Storage.Should().Be("_id", "the keys are the parent's keys");
        stage.RemoteLookup!.Rows.Should().BeTrue("an entity child is answered in whole rows");
        stage.RemoteLookup.PerKey.Should().Be(101, "the default limit, plus one that tells a truncated parent");
        stage.Cases![0].Targets[0].Declared.Should().Be(new ReferenceTarget("tr.shipment", "lines.invoiceId", null, IsRemote: true, FieldIsKey: false));
        bound.FinalShape.Roots["shipments"].Should().BeOfType<ShapeNode.Remote>().Which.SemiJoinable.Should().BeFalse();
    }

    [Fact]
    public async Task It_renders_as_a_lookup_in_the_canonical_form_with_what_the_owner_binds_as_written()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "tr.shipment#lines", "path": "invoiceId", "as": "found", "first": true, "sort": [ { "date": "desc" } ],
                           "filter": { "text": { "eq": "toll" } }, "parentAs": "shipment", "parentSelect": ["number"] } }]
            """);
        var rendered = JsonNode.Parse(bound.Canonical)!["stages"]![0]!["lookup"]!;

        rendered["from"]!.GetValue<string>().Should().Be("tr.shipment#lines");
        rendered["localField"]!.GetValue<string>().Should().Be("_id");
        rendered["remote"]!.GetValue<bool>().Should().BeTrue();
        rendered["first"]!.GetValue<bool>().Should().BeTrue();
        rendered["sort"]![0]!["date"]!.GetValue<string>().Should().Be("desc");
        rendered["filter"]!["text"]!["eq"]!.GetValue<string>().Should().Be("toll");
        rendered["parentAs"]!.GetValue<string>().Should().Be("shipment");
    }

    [Fact]
    public async Task Without_an_owner_this_host_reaches_the_lookup_names_no_entity_and_says_which_service_it_knows_no_owner_for()
    {
        var error = await ErrorAsync($"[{Shipments}]", Codes.UnknownEntity, Reaching(service => service == "crm"));

        error.Message.Should().Be("'tr.shipment' is not an entity of this host, and this host knows no owner for 'tr'.");

        var own = await ErrorAsync("""[{ "lookup": { "from": "rc.nothing", "path": "x", "as": "a" } }]""", Codes.UnknownEntity);
        own.Message.Should().Be("'rc.nothing' is not an entity of this host.");

        var ownItem = await ErrorAsync("""[{ "lookup": { "from": "rc.shipment#billingLines", "path": "x", "as": "a" } }]""", Codes.UnknownEntity);
        ownItem.Message.Should().Contain("an item collection of this host");
    }

    [Theory]
    [InlineData("""{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "s", "parentAs": "p" } }""", "OPTION_NOT_APPLICABLE")]
    [InlineData("""{ "lookup": { "from": "tr.shipment#lines", "path": "invoiceId", "as": "s", "parentSelect": ["number"] } }""", "OPTION_NOT_APPLICABLE")]
    [InlineData("""{ "lookup": { "from": "tr.shipment#lines", "path": "invoiceId", "as": "s", "parentAs": "s" } }""", "ALIAS_COLLISION")]
    [InlineData("""{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "s", "parentAs": "p" } }""", "OPTION_NOT_APPLICABLE")]
    [InlineData("""{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "s", "limit": 101 } }""", "LOOKUP_LIMIT_EXCEEDED")]
    [InlineData("""{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "s", "limit": 3, "first": true } }""", "OPTION_NOT_APPLICABLE")]
    [InlineData("""{ "lookup": { "from": "tr.shipment", "path": "lines..invoiceId", "as": "s" } }""", "INVALID_PATH")]
    [InlineData("""{ "lookup": { "from": "tr.shipment#", "path": "invoiceId", "as": "s" } }""", "UNKNOWN_ENTITY")]
    public async Task What_this_host_can_check_of_a_remote_lookup_is_refused_here(string stage, string code)
    {
        var entity = stage.Contains("\"rc.invoice\"", StringComparison.Ordinal) ? "rc.customer" : Invoice;

        await BindHost.ErrorAsync(ResolveModel.Model, entity, $"[{stage}]", code, Reaching());
    }

    [Fact]
    public async Task A_stage_under_the_children_array_is_not_continuable_and_under_first_it_continues_at_the_owner()
    {
        var array = await ErrorAsync($$"""[{{Shipments}}, { "resolve": { "path": "shipments.customerId", "as": "c" } }]""", Codes.NotContinuable);
        array.Message.Should().Contain("every child the lookup found");

        var bound = await BoundAsync("""
            [{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "latest", "first": true, "sort": [ { "date": "desc" } ] } },
             { "lookup": { "from": "crm.visit", "path": "shipmentId", "on": "latest", "as": "visits" } }]
            """);

        var continued = bound.Stages.OfType<ContinuedStage>().Single();
        continued.Anchor.Should().Be("latest");
        continued.Aliases.Should().Equal("visits");
    }

    [Fact]
    public async Task Under_contract_1_another_services_entity_is_still_no_entity_of_this_host()
    {
        var context = Reaching() with { Contract = 1 };

        (await BindHost.ErrorAsync(ResolveModel.Model, Invoice, $"[{Shipments}]", Codes.UnknownEntity, context)).Message.Should().Be("'tr.shipment' is not an entity of this host.");
    }

    // ---- the owner side: keyedBy ------------------------------------------------------------------

    private static QueryRequest Owner(string keyedBy, string pipeline = """[{ "project": { "number": 1 } }]""") =>
        BindHost.Parse($$"""{ "entityType": "rc.invoice", "keyedBy": {{keyedBy}}, "pipeline": {{pipeline}} }""");

    private static RequestContext Internal() => BindHost.Context() with { Internal = true };

    [Fact]
    public async Task The_owner_checks_that_the_path_declares_a_reference_to_the_asking_hosts_entity()
    {
        var declared = await BindHost.BindAsync(ResolveModel.Model, Owner($$"""{ "path": "customerId", "keys": ["{{Id(Invoice1)}}"], "perKey": 3, "references": "rc.customer", "rows": "entity" }""",
            """[{ "project": { "number": 1, "oxKey": 1 } }]"""), Internal());

        declared.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(declared));

        var undeclared = await BindHost.BindAsync(ResolveModel.Model, Owner($$"""{ "path": "number", "keys": ["x"], "references": "rc.customer", "rows": "entity" }""",
            """[{ "project": { "number": 1, "oxKey": 1 } }]"""), Internal());
        var errors = undeclared.Should().BeOfType<BindOutcome.Failed>().Subject.Refusal.Errors!;

        errors.Should().ContainSingle("the key alias is poisoned, so the projection adds no error of its own")
            .Which.Should().Match<QueryValidationError>(error => error.Code == Codes.LookupNotDeclared && error.Message == "'rc.invoice#number' does not declare a reference to 'rc.customer'.");
    }

    [Theory]
    [InlineData(101, null)]
    [InlineData(102, "LOOKUP_LIMIT_EXCEEDED")]
    public async Task The_owner_caps_the_rows_per_key_at_its_own_lookup_limit_plus_one(int perKey, string? code)
    {
        var outcome = await BindHost.BindAsync(ResolveModel.Model, Owner($$"""{ "path": "customerId", "keys": ["{{Id(Invoice1)}}"], "perKey": {{perKey}} }"""), Internal());

        if (code is null)
            outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));
        else
            outcome.Should().BeOfType<BindOutcome.Failed>().Subject.Refusal.Errors!.Single().Code.Should().Be(code);
    }

    [Fact]
    public async Task A_keyedBy_member_this_engine_does_not_know_is_refused_rather_than_dropped()
    {
        var outcome = await BindHost.BindAsync(ResolveModel.Model, Owner("""{ "path": "number", "keys": ["x"], "someday": true }"""), Internal());

        var error = outcome.Should().BeOfType<BindOutcome.Failed>().Subject.Refusal.Errors!.Single();
        error.Code.Should().Be(Codes.UnknownRequestMember);
        error.Message.Should().Contain("'keyedBy.someday'");
    }

    [Fact]
    public async Task Whole_rows_through_a_collection_carry_each_key_they_hold_once_and_are_ranked_by_the_querys_sort()
    {
        var outcome = await BindHost.BindAsync(ResolveModel.Model, Owner(
            $$"""{ "path": "lines.customerId", "keys": ["{{Id(Invoice1)}}", "{{Id(Invoice2)}}"], "perKey": 2, "rows": "entity" }""",
            """[{ "sort": [ { "number": "desc" } ] }, { "project": { "number": 1, "oxKey": 1 } }, { "page": { "limit": 4 } }]"""), Internal());
        var bound = outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome)).Subject.Pipeline;
        var stages = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)).PageStages;

        bound.KeyedBy!.KeyAlias.Should().Be(BoundKeyedBy.Key);
        bound.FinalShape.Roots[BoundKeyedBy.Key].Should().BeOfType<ShapeNode.Scalar>();

        var set = stages.Single(stage => stage.GetElement(0).Name == "$set" && stage["$set"].AsBsonDocument.Contains("oxKey"));
        set["$set"]["oxKey"]["$filter"]["input"]["$setUnion"][0]["$map"]["input"]["$ifNull"][0].AsString.Should().Be("$Lines");
        stages.Should().Contain(stage => stage.GetElement(0).Name == "$unwind" && stage["$unwind"] == "$oxKey");

        var window = stages.Single(stage => stage.GetElement(0).Name == "$setWindowFields")["$setWindowFields"].AsBsonDocument;
        window["partitionBy"].AsString.Should().Be("$oxKey");
        window["sortBy"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument { ["Number"] = -1, ["_id"] = 1 }, "the query's own sort, completed by the record key");
        window["output"].AsBsonDocument.GetElement(0).Value.AsBsonDocument.Contains("$sum").Should().BeTrue("$documentNumber takes one sort field");

        var names = stages.Select(stage => stage.GetElement(0).Name).ToList();
        names.IndexOf("$setWindowFields").Should().BeLessThan(names.IndexOf("$sort"), "the window runs where the sort is written, after the leading matches");
    }

    [Fact]
    public async Task A_resolves_owner_query_without_a_sort_still_ranks_by_record_key_alone()
    {
        var outcome = await BindHost.BindAsync(ResolveModel.Model, Owner($$"""{ "path": "customerId", "keys": ["{{Id(Invoice1)}}"], "perKey": 2 }"""), Internal());
        var bound = ((BindOutcome.Bound)outcome).Pipeline;
        var window = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)).PageStages.Single(stage => stage.GetElement(0).Name == "$setWindowFields");

        window["$setWindowFields"]["sortBy"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument("_id", 1));
        window["$setWindowFields"]["output"].AsBsonDocument.GetElement(0).Value.AsBsonDocument.Contains("$documentNumber").Should().BeTrue();
    }

    // ---- running it -------------------------------------------------------------------------------

    private sealed class InvoiceRunner : IAggregateRunner
    {
        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BsonDocument>>(new[] { Invoice1, Invoice2, Invoice3 }.Select(id => new BsonDocument
            {
                ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
                ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
                ["Number"] = "RE-" + id.ToString()[^1],
            }).ToList());
    }

    private static MongoQueryEngine Engine(FakeRemoteClient client) =>
        new(new StaticEntityModelProvider(ResolveModel.Model), new InvoiceRunner(), BindHost.Cursors, BindHost.Options(), client, cache: new OwnerFetchCache(BindHost.Options()));

    private static async Task<QueryOutcome> RunAsync(FakeRemoteClient client, string pipeline, bool strict = false) =>
        await Engine(client).ExecuteAsync(BindHost.Request(Invoice, pipeline) with { Strict = strict ? true : null }, BindHost.Context());

    private static JsonObject Shipment(Guid parent, string id, string number) =>
        new() { ["id"] = id, ["number"] = number, ["oxKey"] = Id(parent) };

    [Fact]
    public async Task Each_parent_gets_its_children_in_the_owners_order_one_grouped_query_carries_every_key_and_the_extra_row_is_a_truncation()
    {
        var client = new FakeRemoteClient
        {
            Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(
                Shipment(Invoice1, "s2", "S-2"), Shipment(Invoice3, "s2", "S-2"), Shipment(Invoice1, "s1", "S-1"),
                Shipment(Invoice3, "s3", "S-3"), Shipment(Invoice3, "s4", "S-4")),
        };

        var outcome = await RunAsync(client, """
            [{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "shipments", "select": ["number"], "limit": 2, "sort": [ { "date": "desc" } ] } }]
            """);
        var result = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result;

        Numbers(result.Items[0]!["shipments"]).Should().Equal("S-2", "S-1");
        result.Items[1]!["shipments"]!.AsArray().Should().BeEmpty("the owner answered and had none");
        Numbers(result.Items[2]!["shipments"]).Should().Equal("S-2", "S-3");
        result.Items[0]!["shipments"]![0]!.AsObject().ContainsKey("oxKey").Should().BeFalse("the key the owner grouped by stays here");

        var truncated = result.Diagnostics!.Single(diagnostic => diagnostic.Code == Codes.LookupTruncated);
        truncated.Stage.Should().Be(0);
        truncated.Params!["limit"].Should().Be(2);
        truncated.Params["rows"].Should().Be(1);

        var (service, batch, _) = client.Calls.Should().ContainSingle().Subject;
        service.Should().Be("tr");
        var query = batch.Queries.Should().ContainSingle().Subject;
        query.EntityType.Should().Be("tr.shipment");
        query.KeyedBy!.Path.Should().Be("lines.invoiceId");
        query.KeyedBy.PerKey.Should().Be(3);
        query.KeyedBy.References.Should().Be(Invoice);
        query.KeyedBy.Rows.Should().Be("entity");
        query.KeyedBy.Keys!.Value.GetArrayLength().Should().Be(3);
        query.Pipeline.Select(stage => stage.Kind).Should().Equal("sort", "project", "page");
        query.Pipeline[0].Sort!.Single().Path.Should().Be("date");
        query.Pipeline[1].Project!.Fields.Keys.Should().BeEquivalentTo(["number", "oxKey"]);
        query.Pipeline[2].Page!.Limit.Should().Be(9, "three keys at three rows each");
    }

    private static IReadOnlyList<string?> Numbers(JsonNode? array) => array!.AsArray().Select(item => item?["number"]?.GetValue<string>()).ToList();

    [Fact]
    public async Task Strict_refuses_a_truncated_parent()
    {
        var client = new FakeRemoteClient
        {
            Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(Shipment(Invoice1, "s1", "S-1"), Shipment(Invoice1, "s2", "S-2")),
        };

        var outcome = await RunAsync(client, """[{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "shipments", "limit": 1 } }]""", strict: true);

        outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal.Errors!.Should().Contain(error => error.Code == Codes.LookupTruncated);
    }

    [Fact]
    public async Task First_takes_the_owners_first_child_and_an_element_child_brings_its_owning_row()
    {
        var client = new FakeRemoteClient
        {
            Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(
                new JsonObject { ["id"] = "s1", ["number"] = "S-1", ["oxEl"] = new JsonObject { ["invoiceId"] = Id(Invoice1), ["text"] = "toll" } }),
        };

        var outcome = await RunAsync(client, """
            [{ "lookup": { "from": "tr.shipment#lines", "path": "invoiceId", "as": "line", "first": true, "parentAs": "shipment", "parentSelect": ["number"] } }]
            """);
        var result = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result;

        result.Items[0]!["line"]!.ToJsonString().Should().Be("""{"invoiceId":"10000000-0000-0000-0000-000000000001","text":"toll"}""");
        result.Items[0]!["shipment"]!.ToJsonString().Should().Be("""{"entity":"tr.shipment","id":"s1","number":"S-1"}""");
        result.Items[1]!["line"].Should().BeNull();

        var query = client.Calls.Single().Request.Queries.Single();
        query.KeyedBy!.Path.Should().Be("lines.invoiceId");
        query.KeyedBy.PerKey.Should().Be(1);
        query.KeyedBy.Rows.Should().BeNull("an element child is answered per element");
        query.Pipeline.Select(stage => stage.Kind).Should().Equal("project", "page");
        query.Pipeline[0].Project!.Fields.Keys.Should().BeEquivalentTo(["oxEl", "number"], "the whole element without a select (the key within it), and the owning row's select");
    }

    [Fact]
    public async Task An_owner_that_does_not_answer_leaves_the_parents_null_says_so_and_strict_refuses()
    {
        var client = new FakeRemoteClient();
        client.Unreachable.Add("tr");

        var answered = await RunAsync(client, $"[{Shipments}]");
        var result = answered.Should().BeOfType<QueryOutcome.Success>().Subject.Result;

        result.Items.Should().OnlyContain(item => item!["shipments"] == null, "unanswered is not an empty array");
        result.Diagnostics!.Single().Code.Should().Be(Codes.ResolveUnreachable);

        var strict = await RunAsync(client, $"[{Shipments}]", strict: true);
        strict.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal.Errors!.Should().Contain(error => error.Code == Codes.ResolveUnreachable);
    }

    [Fact]
    public async Task An_owners_refusal_of_what_the_lookup_wrote_is_the_lookups_error_at_its_stage_and_path()
    {
        var client = new FakeRemoteClient { Script = (_, _, _) => new FakeRemoteClient.Answer.Refused(Codes.UnknownPath, "'oxEl.txt' is not a path.", Stage: 0, Path: "oxEl.txt") };

        var outcome = await RunAsync(client, """
            [{ "match": { "number": { "neq": "x" } } }, { "lookup": { "from": "tr.shipment#lines", "path": "invoiceId", "as": "line", "select": ["txt"] } }]
            """);
        var errors = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal.Errors!;

        errors[0].Code.Should().Be(Codes.ResolveRefused);
        errors[0].Stage.Should().Be(1);
        var mapped = errors.Single(error => error.Code == Codes.UnknownPath);
        mapped.Stage.Should().Be(1);
        mapped.Path.Should().Be("txt", "relative to the child, as the lookup wrote it");
        ((Dictionary<string, object?>)mapped.Params!["owner"]!)["service"].Should().Be("tr");
    }

    // ---- explain ----------------------------------------------------------------------------------

    [Fact]
    public async Task Explain_places_it_keyed_remote_after_the_page_without_a_reference_and_notes_the_owners_bounds()
    {
        var client = new FakeRemoteClient();
        var engine = Engine(client);
        var explained = await engine.ExplainAsync(BindHost.Request(Invoice, $"[{Shipments}]").Planned(), BindHost.Context());
        var result = explained.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        var stage = result.Stages.Single();
        stage.Kind.Should().Be("lookup");
        stage.Placement!.Executor.Should().Be("keyed-remote");
        stage.Placement.Phase.Should().Be("afterPage");
        result.Alias(stage.Creates.First()).ContainsKey("reference").Should().BeFalse("a lookup follows no reference of this host");
        result.Owner(0)!["service"]!.GetValue<string>().Should().Be("tr");
        result.Query(0)["keyedBy"]!["references"]!.GetValue<string>().Should().Be(Invoice);

        var bounds = result.Notes.Single(note => note.Code == Notes.RemoteLookup);
        bounds.Params!["perKey"].Should().Be(101);
        bounds.Params["keysPerQuery"].Should().Be(4, "a page of 500 holds four keys at 101 rows each");
        bounds.Params["sorted"].Should().Be(false);
        result.Notes.Should().NotContain(note => note.Code == Notes.MissingPolicy);
        result.Notes.Should().Contain(note => note.Code == Notes.LookupLimit && note.Stage == 0);

        client.Owners["tr"] = new RemoteOwnerInfo("2.1.0.0", 2, null, MaxPageSize: 202);
        var sized = ((ExplainOutcome.Success)await engine.ExplainAsync(BindHost.Request(Invoice, $"[{Shipments}]"), BindHost.Context())).Result;
        sized.Notes.Single(note => note.Code == Notes.RemoteLookup).Params!["keysPerQuery"].Should().Be(2, "sized by the owner's own page");
    }
}
