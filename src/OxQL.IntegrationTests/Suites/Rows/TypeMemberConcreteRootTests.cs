using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Rows;

/// <summary>
/// The model's own type member on a real server where the root class is concrete and its
/// discriminator hierarchical. The driver stores the root class's own values as <c>_t: "Gear"</c>
/// and every derived value as <c>_t: ["Gear", …]</c>, so the root's discriminator stands in every
/// row: a condition on the root class's value has to take out every variant that holds another
/// value, or it answers every row. The documents are written by the driver from the classes below.
/// </summary>
[Trait("Category", "Integration")]
public class TypeMemberConcreteRootTests
{
    private const string Entity = "rows.rig";

    private static readonly Lazy<EntityModel> Model = new(() =>
        ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(Rig), "rigs", null, false)]));

    private static readonly IReadOnlyList<Rig> Rigs =
    [
        new() { Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Title = "a", Main = new Gear { Name = "plain" }, All = [new Gear { Name = "g" }, new Winch { Name = "w" }] },
        new() { Id = Guid.Parse("00000000-0000-0000-0000-0000000000b2"), Title = "b", Main = new Winch { Name = "w" }, All = [new Winch { Name = "w" }] },
        new() { Id = Guid.Parse("00000000-0000-0000-0000-0000000000c3"), Title = "c", Main = new Crane { Name = "c" }, All = [new Crane { Name = "c" }, new TowerCrane { Name = "t" }] },
        new() { Id = Guid.Parse("00000000-0000-0000-0000-0000000000d4"), Title = "d", Main = new TowerCrane { Name = "t" }, All = [new Spare { Name = "s" }] },
        new() { Id = Guid.Parse("00000000-0000-0000-0000-0000000000e5"), Title = "e", Main = new Spare { Name = "s" }, All = [] },
        new() { Id = Guid.Parse("00000000-0000-0000-0000-0000000000f6"), Title = "f" },
    ];

    private static async Task<(TestDatabase Database, EngineDirect Direct, IQueryEngine Engine)> HostAsync(string purpose)
    {
        var database = await MongoFixture.CreateDatabaseAsync(purpose);
        var rigs = Rigs.Select(rig => { rig.OrganizationId = EngineDirect.Organisation; return rig; }).ToList();

        await database.Database.GetCollection<Rig>("rigs").InsertManyAsync(rigs);

        var direct = new EngineDirect(Model.Value);

        return (database, direct, direct.Engine(await MongoFixture.ClientAsync(), database.Name, direct.Options()));
    }

    private static async Task<QueryResult> RunAsync(EngineDirect direct, IQueryEngine engine, string pipeline)
    {
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, pipeline), direct.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static async Task<IReadOnlyList<string>> Titles(EngineDirect direct, IQueryEngine engine, string match) =>
        (await RunAsync(direct, engine, $$"""[ { "match": {{match}} }, { "sort": [ { "title": "asc" } ] }, { "project": { "title": 1 } } ]"""))
            .Items.Select(item => item!["title"]!.GetValue<string>()).ToList();

    /// <summary>What the service's REST API shows for the member: the serializer calls the getter.</summary>
    private static string? Rest(Gear? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!["kind"]!.GetValue<string>();

    [Fact]
    public async Task The_driver_stores_the_root_classs_discriminator_in_every_row()
    {
        var (database, _, _) = await HostAsync("type-member-root-stored");
        await using var _1 = database;

        var stored = await database.Database.GetCollection<BsonDocument>("rigs").Find(FilterDefinition<BsonDocument>.Empty).Sort("{ Title: 1 }").ToListAsync();

        stored.Select(row => row.GetValue("Main", BsonNull.Value) is BsonDocument main ? main["_t"].ToJson() : "null").Should().Equal(
            "\"Gear\"", "[\"Gear\", \"Winch\"]", "[\"Gear\", \"Crane\"]", "[\"Gear\", \"Crane\", \"TowerCrane\"]", "[\"Gear\", \"Spare\"]", "null");
    }

    [Fact]
    public async Task A_row_shows_the_member_as_REST_shows_it_for_the_root_class_and_every_class_derived_from_it()
    {
        var (database, direct, engine) = await HostAsync("type-member-root-rows");
        await using var _ = database;

        var rows = (await RunAsync(direct, engine, """[ { "sort": [ { "title": "asc" } ] } ]""")).Items;

        rows.Should().HaveCount(Rigs.Count);

        foreach (var (row, rig) in rows.Zip(Rigs))
        {
            (row!["main"]?["kind"]?.GetValue<string>()).Should().Be(Rest(rig.Main), $"{rig.Title}: main.kind");
            row["all"]!.AsArray().Select(gear => gear!["kind"]!.GetValue<string>()).Should().Equal(rig.All.Select(Rest), $"{rig.Title}: all.kind, per element");
        }
    }

    [Fact]
    public async Task A_condition_on_the_root_classs_value_answers_the_rows_that_hold_it_and_no_derived_row_that_holds_another()
    {
        var (database, direct, engine) = await HostAsync("type-member-root-filter");
        await using var _ = database;

        Task<IReadOnlyList<string>> Of(string match) => Titles(direct, engine, match);

        // Every answer is what the value the service returns for the member says, row by row.
        IEnumerable<string> Holding(Func<string?, bool> held) => Rigs.Where(rig => held(Rest(rig.Main))).Select(rig => rig.Title);

        // The root class's own value, which one derived class shares.
        (await Of("""{ "main.kind": { "eq": "gear" } }""")).Should().Equal(Holding(kind => kind == "gear")).And.Equal("a", "e");
        (await Of("""{ "main.kind": { "neq": "gear" } }""")).Should().Equal(Holding(kind => kind != "gear")).And.Equal(["b", "c", "d", "f"], "a row without the object is not 'gear', as a missing stored member is not");
        (await Of("""{ "main.kind": { "in": ["gear"] } }""")).Should().Equal("a", "e");
        (await Of("""{ "main.kind": { "nin": ["gear"] } }""")).Should().Equal("b", "c", "d", "f");

        // A derived value, and one a class and the class derived from it share.
        (await Of("""{ "main.kind": { "eq": "winch" } }""")).Should().Equal(Holding(kind => kind == "winch")).And.Equal("b");
        (await Of("""{ "main.kind": { "neq": "winch" } }""")).Should().Equal("a", "c", "d", "e", "f");
        (await Of("""{ "main.kind": { "eq": "crane" } }""")).Should().Equal(Holding(kind => kind == "crane")).And.Equal("c", "d");
        (await Of("""{ "main.kind": { "nin": ["crane", "winch"] } }""")).Should().Equal("a", "e", "f");

        // The root class's value beside a derived one.
        (await Of("""{ "main.kind": { "in": ["gear", "winch"] } }""")).Should().Equal(Holding(kind => kind is "gear" or "winch")).And.Equal("a", "b", "e");
        (await Of("""{ "main.kind": { "nin": ["gear", "winch"] } }""")).Should().Equal("c", "d", "f");
        (await Of("""{ "main.kind": { "in": ["gear", "winch", "crane"] } }""")).Should().Equal("a", "b", "c", "d", "e");
        (await Of("""{ "main.kind": { "nin": ["gear", "winch", "crane"] } }""")).Should().Equal("f");

        // Folded and exact, as every string comparison of contract 2.
        (await Of("""{ "main.kind": { "eq": "GEAR" } }""")).Should().Equal("a", "e");
        (await Of("""{ "main.kind": { "eq": "GEAR", "options": { "caseSensitive": true } } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task Through_a_collection_the_root_classs_value_is_asked_of_one_element_at_a_time()
    {
        var (database, direct, engine) = await HostAsync("type-member-root-collection");
        await using var _ = database;

        Task<IReadOnlyList<string>> Of(string match) => Titles(direct, engine, match);

        IEnumerable<string> Some(Func<string?, bool> held) => Rigs.Where(rig => rig.All.Any(gear => held(Rest(gear)))).Select(rig => rig.Title);

        // Which element is plain gear and no winch cannot be said for a collection's elements together.
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, """[ { "match": { "all.kind": { "eq": "gear" } } } ]"""), direct.Context());
        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Errors.Should().ContainSingle(error => error.Code == Codes.InvalidOperand && error.Path == "all.kind").Which.Message.Should().Contain("'any' on the collection");

        // Asked of one element it is exact: the plain gear and the spare, not the winch beside the gear nor the cranes.
        (await Of("""{ "all": { "any": { "kind": { "eq": "gear" } } } }""")).Should().Equal(Some(kind => kind == "gear")).And.Equal("a", "d");
        (await Of("""{ "all": { "any": { "kind": { "neq": "gear" } } } }""")).Should().Equal(Some(kind => kind != "gear")).And.Equal("a", "b", "c");
        (await Of("""{ "all": { "any": { "kind": { "in": ["gear", "crane"] } } } }""")).Should().Equal("a", "c", "d");
        (await Of("""{ "all": { "any": { "kind": { "nin": ["gear", "crane"] } } } }""")).Should().Equal("a", "b");

        // A value only classes hold that nothing else derives from is one comparison in a collection too.
        (await Of("""{ "all.kind": { "eq": "winch" } }""")).Should().Equal(Some(kind => kind == "winch")).And.Equal("a", "b");
        (await Of("""{ "all.kind": { "eq": "crane" } }""")).Should().Equal("c");

        // An unwound element is one object.
        var unwound = await RunAsync(direct, engine, """[ { "unwind": { "path": "all", "as": "gear" } }, { "match": { "gear.kind": { "eq": "gear" } } }, { "sort": [ { "title": "asc" } ] }, { "project": { "id": 0, "title": 1, "gear.kind": 1, "gear.name": 1 } } ]""");

        unwound.Items.Select(row => row!.ToJsonString()).Should().Equal(
            """{"title":"a","gear":{"name":"g","kind":"gear"}}""",
            """{"title":"d","gear":{"name":"s","kind":"gear"}}""");

        var others = await RunAsync(direct, engine, """[ { "unwind": { "path": "all", "as": "gear" } }, { "match": { "gear.kind": { "neq": "gear" } } }, { "sort": [ { "title": "asc" }, { "gear.name": "asc" } ] }, { "project": { "id": 0, "title": 1, "gear.name": 1 } } ]""");

        others.Items.Select(row => row!["title"]!.GetValue<string>() + ":" + row["gear"]!["name"]!.GetValue<string>()).Should().Equal("a:w", "b:w", "c:c", "c:t");
    }

    // ---- fixture ---------------------------------------------------------------------------------

    public class Rig
    {
        public Guid Id { get; set; }
        public Guid OrganizationId { get; set; }
        public string Title { get; set; } = "";
        public Gear? Main { get; set; }
        public List<Gear> All { get; set; } = [];
    }

    /// <summary>A concrete root class: its own values are stored as <c>_t: "Gear"</c>, a derived one as <c>_t: ["Gear", …]</c>.</summary>
    [BsonDiscriminator(RootClass = true)]
    public class Gear
    {
        public string Name { get; set; } = "";

        public virtual string Kind => "gear";
    }

    public class Winch : Gear
    {
        public override string Kind => "winch";
    }

    public class Crane : Gear
    {
        public override string Kind => "crane";
    }

    /// <summary>Does not override: answers what a crane answers.</summary>
    public class TowerCrane : Crane
    {
    }

    /// <summary>Shares the root class's value.</summary>
    public class Spare : Gear
    {
        public override string Kind => "gear";
    }
}

/// <summary>The class maps of <see cref="TypeMemberConcreteRootTests"/>, registered once when the assembly loads, as a host registers them.</summary>
internal static class TypeMemberConcreteRootRegistrations
{
    [ModuleInitializer]
    internal static void Register()
    {
        BsonClassMap.RegisterClassMap<TypeMemberConcreteRootTests.Winch>();
        BsonClassMap.RegisterClassMap<TypeMemberConcreteRootTests.Crane>();
        BsonClassMap.RegisterClassMap<TypeMemberConcreteRootTests.TowerCrane>();
        BsonClassMap.RegisterClassMap<TypeMemberConcreteRootTests.Spare>();
    }
}
