using System.Text.Json;
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
/// T1, explain ≡ run parity at the unit level (improvement plan P12): the check query explain builds
/// for an <see cref="OwnerCheck"/> is the owner query the run sends for the same target, modulo the
/// keys (the check carries <see cref="KeyedFetch.CheckKey"/>, the run the page's keys, and the page
/// limit follows their count) and the paths the check projects beside the select so the owner says
/// which it lacks. One case per join kind: a remote resolve, a local keyed resolve with a stage
/// continued under it, a stage continued under a remote alias, a remote lookup, and unions narrowed
/// with <c>forTarget</c> at a local and at a remote target. The run's queries are recorded where
/// they leave: the remote client for an owner, this host's own engine for a local target.
/// </summary>
public class OwnerCheckParityTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static readonly Guid InvoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid ContactId = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");
    private static readonly Guid ShipmentId = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private static readonly Guid TourId = Guid.Parse("70000000-0000-0000-0000-000000000007");

    /// <summary>Answers every aggregate with no rows: the owner queries are what is compared, not their answers.</summary>
    private sealed class EmptyRunner : IAggregateRunner
    {
        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BsonDocument>>([]);
    }

    /// <summary>This host as the owner of its local targets, recording every owner query the keyed fetch sends it.</summary>
    private sealed class RecordingSelf(IQueryEngine inner) : IQueryEngine
    {
        public List<QueryRequest> Sent { get; } = [];

        public Task<QueryOutcome> ExecuteAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken = default)
        {
            if (context.Internal)
                Sent.Add(request);

            return inner.ExecuteAsync(request, context, cancellationToken);
        }

        public Task<ExplainOutcome> ExplainAsync(ExplainRequest request, RequestContext context, CancellationToken cancellationToken = default) =>
            inner.ExplainAsync(request, context, cancellationToken);
    }

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonDocument InvoiceRow(Guid id, Action<BsonDocument> fill)
    {
        var row = new BsonDocument { ["_id"] = Id(id), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "RE-1" };

        fill(row);

        return row;
    }

    private static RequestContext Reaching() => BindHost.Context() with { RemoteService = service => service is "tr" or "crm" or "transport" };

    /// <summary>One join kind: the pipeline, the page rows it runs over, whether it is strict, and per checked target the paths its check projects beside the run's projection.</summary>
    public sealed record Case(string Pipeline, BsonDocument[] Rows, bool Strict, IReadOnlyDictionary<string, string[]> Extras);

    public static TheoryData<string> Kinds => new(Cases.Keys);

    private static readonly IReadOnlyDictionary<string, Case> Cases = new Dictionary<string, Case>(StringComparer.Ordinal)
    {
        ["remote resolve"] = new(
            """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "project": { "number": 1, "r.name": 1, "r.phone": 1 } }]""",
            [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))], Strict: false,
            new Dictionary<string, string[]> { ["crm.contact"] = ["phone"] }),

        ["stage continued under a remote alias"] = new(
            """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }, { "page": { "limit": 10 } }]""",
            [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))], Strict: true,
            new Dictionary<string, string[]> { ["crm.contact"] = [] }),

        ["local keyed resolve with a continued stage"] = new(
            """[{ "resolve": { "path": "billing.referenceId", "as": "b" } }, { "resolve": { "path": "b.driverId", "as": "d" } }]""",
            [
                InvoiceRow(InvoiceId, row => row["Billing"] = new BsonDocument { ["DataType"] = "shipment", ["ReferenceId"] = ShipmentId.ToString() }),
                InvoiceRow(SecondId, row => row["Billing"] = new BsonDocument { ["DataType"] = "tour", ["ReferenceId"] = TourId.ToString() }),
            ], Strict: false,
            new Dictionary<string, string[]> { ["rc.shipment"] = [], ["rc.tour"] = [] }),

        ["local union narrowed with forTarget"] = new(
            """[{ "resolve": { "path": "billing.referenceId", "as": "b" } }, { "resolve": { "path": "b.driverId", "as": "d", "forTarget": "rc.tour" } }, { "project": { "number": 1, "b.name": 1, "d": 1 } }]""",
            [
                InvoiceRow(InvoiceId, row => row["Billing"] = new BsonDocument { ["DataType"] = "shipment", ["ReferenceId"] = ShipmentId.ToString() }),
                InvoiceRow(SecondId, row => row["Billing"] = new BsonDocument { ["DataType"] = "tour", ["ReferenceId"] = TourId.ToString() }),
            ], Strict: false,
            new Dictionary<string, string[]> { ["rc.tour"] = [] }),

        ["remote lookup"] = new(
            """[{ "lookup": { "from": "tr.shipment", "path": "lines.invoiceId", "as": "shipments", "select": ["number"] } }, { "project": { "number": 1, "shipments.number": 1, "shipments.date": 1 } }]""",
            [InvoiceRow(InvoiceId, _ => { })], Strict: false,
            new Dictionary<string, string[]> { ["tr.shipment"] = ["date"] }),

        ["remote union target narrowed with forTarget"] = new(
            """
            [{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner", "select": ["id", "code"], "parentSelect": ["id", "number"] } },
             { "resolve": { "path": "owner.driverId", "as": "drv", "forTarget": "transport.shipment" } },
             { "project": { "number": 1, "line.id": 1, "line.amount": 1, "owner": 1, "drv": 1 } }]
            """,
            [InvoiceRow(InvoiceId, row => row["Source"] = new BsonDocument { ["Type"] = "remote", ["_id"] = Id(ShipmentId) })], Strict: false,
            new Dictionary<string, string[]> { ["transport.shipment"] = ["oxEl.amount"] }),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_check_query_of_an_owner_check_is_the_owner_query_the_run_sends_for_the_same_target(string kind)
    {
        var @case = Cases[kind];
        var options = BindHost.Options();
        var client = new FakeRemoteClient();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new EmptyRunner(), BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));
        var self = new RecordingSelf(engine);
        var context = Reaching();
        var request = BindHost.Request(Invoice, @case.Pipeline) with { Strict = @case.Strict ? true : null };
        var binding = await new Binder(ResolveModel.Model, BindHost.Cursors).BindAsync(request, context, CancellationToken.None);
        var bound = binding.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(binding)).Subject.Pipeline;

        // Explain's side: every owner check of every keyed stage, as RemoteExplain builds them.
        var checks = bound.Stages.OfType<BoundStage.Resolve>()
            .SelectMany(resolve => KeyedFetch.Checks(bound, resolve, @case.Strict, client))
            .ToList();

        checks.Select(check => check.Bound!.Declared.Entity).Should().BeEquivalentTo(@case.Extras.Keys, $"{kind}: the targets explain checks");

        // The run's side: the owner queries the keyed fetch sends over the same page.
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));
        var fetch = new KeyedFetch(client, self, new OwnerFetchCache(options), options);

        await fetch.ByKeysAsync(compiled, @case.Rows, context, TimeSpan.FromSeconds(5), @case.Strict, CancellationToken.None);

        var sent = client.Calls.SelectMany(call => call.Request.Queries.Select(query => (call.Service, Query: query)))
            .Concat(self.Sent.Select(query => (Service: "", Query: query)))
            .ToList();

        foreach (var check in checks)
        {
            var entity = check.Bound!.Declared.Entity;
            var run = sent.Where(each => each.Service == check.Service && each.Query.EntityType == entity).Select(each => each.Query).FirstOrDefault();

            run.Should().NotBeNull($"{kind}: the run sends '{check.Target}' an owner query");

            var checkQuery = Normalised(check.Query);
            var runQuery = Normalised(run!);
            var extras = @case.Extras[entity];
            var checkFields = Projection(checkQuery);
            var runFields = Projection(runQuery);

            checkFields.Except(runFields).Should().BeEquivalentTo(extras, $"{kind}, {check.Target}: the check projects only the paths the owner is asked about beside the run's projection");
            runFields.Except(checkFields).Should().BeEmpty($"{kind}, {check.Target}: the check projects everything the run does");

            foreach (var extra in extras)
                Projected(checkQuery).Remove(extra);

            checkQuery.ToJsonString().Should().Be(runQuery.ToJsonString(), $"{kind}, {check.Target}: explain checks the query the run sends");
        }
    }

    /// <summary>A query as the wire writes it, the keys it carries as one <c>K</c> and its page limit (which follows their count) as <c>N</c>.</summary>
    private static JsonObject Normalised(QueryRequest query)
    {
        var node = JsonSerializer.SerializeToNode(query, OxQLJson.Wire)!.AsObject();

        Keys(node);

        foreach (var stage in node["pipeline"]!.AsArray().OfType<JsonObject>())
            if (stage["page"] is JsonObject page && page.ContainsKey("limit"))
                page["limit"] = "N";

        return node;
    }

    /// <summary>Every array of keys (guids only) becomes <c>["K"]</c>.</summary>
    private static void Keys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (name, value) in members.ToList())
                    if (value is JsonArray array && array.Count > 0 && array.All(IsKey))
                        members[name] = new JsonArray("K");
                    else
                        Keys(value);
                break;

            case JsonArray items:
                foreach (var item in items)
                    Keys(item);
                break;
        }
    }

    private static bool IsKey(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out _);

    private static JsonObject Projected(JsonObject query) =>
        query["pipeline"]!.AsArray().OfType<JsonObject>().Last(stage => stage["project"] is not null)["project"]!.AsObject();

    private static List<string> Projection(JsonObject query) => Projected(query).Select(pair => pair.Key).ToList();
}
