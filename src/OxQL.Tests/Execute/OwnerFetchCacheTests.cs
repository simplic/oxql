using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The semi-join cache across references: several entities of one service reach the same
/// remote entity, each storing its reference member its own way or pointing at another member
/// of the target, and they share one engine and therefore one cache.
/// </summary>
public class OwnerFetchCacheTests
{
    private const string ByMatchCode = """[{ "resolve": { "path": "PATH", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "V-1" } } }, { "page": { "limit": 10 } }]""";

    private static readonly Guid Vehicle = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // Declared by hand rather than by attribute, so the entities stay out of the probe graph.
    private static readonly EntityModel Model = ClrModelBuilder.Build(
    [
        new EntityDeclaration("hh.order", "hh.order", typeof(BinaryReference), "orders", null, false),
        new EntityDeclaration("hh.invoice", "hh.invoice", typeof(TextReference), "invoices", null, false),
        new EntityDeclaration("hh.delivery", "hh.delivery", typeof(NumberReference), "deliveries", null, false),
    ]);

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, FakeRemoteClient Client) Host()
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient
        {
            Script = (_, _, _) => new FakeRemoteClient.Answer.Counted(1, false, false, FakeRemoteClient.Row("id", Vehicle.ToString(), ("number", "N-7"))),
        };
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        return (engine, runner, client);
    }

    private static async Task<BsonArray> IdsSubstitutedAsync(MongoQueryEngine engine, FakeAggregateRunner runner, string entity, string referencePath, string storage)
    {
        var before = runner.Calls.Count;
        var outcome = await engine.ExecuteAsync(BindHost.Request(entity, ByMatchCode.Replace("PATH", referencePath)), BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        var filter = runner.Calls[before].Stages
            .Where(stage => stage.Contains("$match"))
            .Select(stage => stage["$match"].AsBsonDocument)
            .Single(match => match.Contains(storage));

        return filter[storage]["$in"].AsBsonArray;
    }

    [Fact]
    public async Task Two_references_into_one_entity_each_get_the_ids_as_their_own_member_stores_them()
    {
        var (engine, runner, client) = Host();

        var binary = await IdsSubstitutedAsync(engine, runner, "hh.order", "vehicleId", "VehicleId");
        var text = await IdsSubstitutedAsync(engine, runner, "hh.invoice", "vehicleId", "VehicleId");

        binary.Should().Equal(new BsonBinaryData(Vehicle, GuidRepresentation.Standard));
        text.Should().Equal([new BsonString(Vehicle.ToString())], "the second reference stores its member as a string, whatever the first one cached");
        client.Calls.Should().ContainSingle("both references ask the owner the same question, so the answer is shared");
    }

    [Fact]
    public async Task The_order_the_references_ran_in_does_not_decide_the_encoding()
    {
        var (engine, runner, _) = Host();

        var text = await IdsSubstitutedAsync(engine, runner, "hh.invoice", "vehicleId", "VehicleId");
        var binary = await IdsSubstitutedAsync(engine, runner, "hh.order", "vehicleId", "VehicleId");

        text.Should().Equal(new BsonString(Vehicle.ToString()));
        binary.Should().Equal(new BsonBinaryData(Vehicle, GuidRepresentation.Standard));
    }

    [Fact]
    public async Task A_reference_to_another_member_of_the_target_does_not_read_the_first_references_ids()
    {
        var (engine, runner, client) = Host();

        await IdsSubstitutedAsync(engine, runner, "hh.order", "vehicleId", "VehicleId");

        var numbers = await IdsSubstitutedAsync(engine, runner, "hh.delivery", "vehicleNumber", "VehicleNumber");

        numbers.Should().Equal(new BsonString("N-7"));
        client.Calls.Should().HaveCount(2, "another target field is another question to the owner");
        client.Calls[1].Request.Queries.Single().Pipeline[1].Project!.Fields.Keys.Should().Equal("number");
    }

    [Fact]
    public void The_key_separates_organisations_and_everything_the_owner_is_sent()
    {
        var organisation = BindHost.Organisation;
        var query = BindHost.Request("vehicle.vehicle", """[{ "match": { "matchCode": { "eq": "V-1" } } }, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""");

        OwnerFetchCache.KeyOf(organisation, query).Should().Be(OwnerFetchCache.KeyOf(organisation, query with { }));
        OwnerFetchCache.KeyOf(Guid.NewGuid(), query).Should().NotBe(OwnerFetchCache.KeyOf(organisation, query), "another organisation never sees the list");

        foreach (var other in new[]
        {
            """[{ "match": { "matchCode": { "eq": "V-2" } } }, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""",
            """[{ "match": { "matchCode": { "neq": "V-1" } } }, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""",
            """[{ "match": { "name": { "eq": "V-1" } } }, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""",
            """[{ "match": { "matchCode": { "eq": "V-1", "options": { "ignoreCase": true } } } }, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""",
            """[{ "match": { "matchCode": { "eq": "V-1" } } }, { "project": { "number": 1 } }, { "page": { "limit": 500 } }]""",
        })
            OwnerFetchCache.KeyOf(organisation, BindHost.Request("vehicle.vehicle", other)).Should().NotBe(OwnerFetchCache.KeyOf(organisation, query), other);

        OwnerFetchCache.KeyOf(organisation, query with { EntityType = "crm.contact" }).Should().NotBe(OwnerFetchCache.KeyOf(organisation, query));
    }

    [Fact]
    public void Learned_union_drops_never_compete_with_the_answers_for_the_size_budget()
    {
        var options = BindHost.Options();
        options.Cache.OwnerFetchCacheMaxEntries = 1;

        using var cache = new OwnerFetchCache(options);
        var drops = OwnerFetchCache.DropsKeyOf(BindHost.Organisation, "logistics", "plan");
        var row = OwnerFetchCache.KeyOf("logistics.tour", null, "id", BindHost.Organisation, "k1", "plan");

        cache.SetDrops(drops, ["number"], []);
        cache.Set(row, new OwnerAnswer([new System.Text.Json.Nodes.JsonObject { ["id"] = "k1" }]));

        cache.TryGet(row, strict: false, out var answer).Should().BeTrue("the drops take nothing from the answers' budget");
        answer!.Rows.Should().ContainSingle();
        cache.TryGetDrops(drops, out var select, out _).Should().BeTrue();
        select.Should().Equal("number");
    }

    private sealed class BinaryReference
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference("vehicle.vehicle", "id")]
        public Guid VehicleId { get; set; }
    }

    private sealed class TextReference
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference("vehicle.vehicle", "id")]
        [BsonRepresentation(BsonType.String)]
        public Guid VehicleId { get; set; }
    }

    private sealed class NumberReference
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference("vehicle.vehicle", "number")]
        public string? VehicleNumber { get; set; }
    }
}
