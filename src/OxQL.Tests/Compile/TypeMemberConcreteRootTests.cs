using System.Runtime.CompilerServices;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// A condition on the model's own type member where the root class is concrete and its
/// discriminator hierarchical: the root's own rows are stored as <c>_t: "Gear"</c> and every
/// derived row as <c>_t: ["Gear", …]</c>, so the root's discriminator alone finds every row. The
/// value of the root class is then told by taking out every variant that holds another value, as a
/// variant's value is told by taking out the variants derived from it that hold another one.
/// </summary>
public class TypeMemberConcreteRootTests
{
    private const string Entity = "probe.rig";

    private static readonly Lazy<EntityModel> Model = new(() =>
        ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(Rig), "rigs", null, false)]));

    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static async Task<BsonDocument> MatchOf(string condition)
    {
        var bound = await BindHost.BoundAsync(Model.Value, Entity, $$"""[ { "match": {{condition}} } ]""");

        // The filter of the caller's match: the stage after the leading scope match.
        return MongoCompiler.Compile(bound, Options).PageStages[1]["$match"].AsBsonDocument;
    }

    private static BsonDocument Bson(string json) => BsonDocument.Parse(json);

    [Fact]
    public void The_model_holds_the_root_classs_own_value_under_its_name()
    {
        var member = Model.Value.Entities[Entity].Root.Member("main")!.Leaf.Type!.Member("kind")!;

        member.ByVariant.Should().Equal(new Dictionary<string, string>
        {
            ["Crane"] = "crane", ["Gear"] = "gear", ["Spare"] = "gear", ["TowerCrane"] = "crane", ["Winch"] = "winch",
        });
        Model.Value.Entities[Entity].Path("main.kind")!.Derived!.Holder.DiscriminatorForm.Should().Be(DiscriminatorForm.Hierarchical);
    }

    [Fact]
    public async Task The_root_classs_value_takes_out_every_variant_that_holds_another_value()
    {
        // "gear" is the root's own value and Spare's: the root's discriminator finds every row, so the cranes and the winch are taken out.
        (await MatchOf("""{ "main.kind": { "eq": "gear" } }""")).ShouldBeBson(Bson("""
            { "$and": [
                { "$or": [ { "Main._t": { "$in": ["Gear", "Spare"] } }, { "Main": { "$type": "object" }, "Main._t": { "$exists": false } } ] },
                { "$nor": [ { "Main._t": { "$in": ["Crane", "TowerCrane", "Winch"] } } ] } ] }
            """));
    }

    [Fact]
    public async Task The_negation_is_the_negation_of_that_test()
    {
        (await MatchOf("""{ "main.kind": { "neq": "gear" } }""")).ShouldBeBson(Bson("""
            { "$nor": [ { "$and": [
                { "$or": [ { "Main._t": { "$in": ["Gear", "Spare"] } }, { "Main": { "$type": "object" }, "Main._t": { "$exists": false } } ] },
                { "$nor": [ { "Main._t": { "$in": ["Crane", "TowerCrane", "Winch"] } } ] } ] } ] }
            """));
    }

    [Fact]
    public async Task A_derived_value_is_told_by_its_own_discriminators_and_needs_nothing_taken_out()
    {
        (await MatchOf("""{ "main.kind": { "eq": "winch" } }""")).ShouldBeBson(Bson("""{ "Main._t": { "$in": ["Winch"] } }"""));
        // A crane and the tower crane derived from it hold one value: nothing derived from them holds another.
        (await MatchOf("""{ "main.kind": { "eq": "crane" } }""")).ShouldBeBson(Bson("""{ "Main._t": { "$in": ["Crane", "TowerCrane"] } }"""));
    }

    [Fact]
    public async Task A_set_with_the_root_classs_value_takes_out_only_what_holds_none_of_the_values()
    {
        (await MatchOf("""{ "main.kind": { "in": ["gear", "winch"] } }""")).ShouldBeBson(Bson("""
            { "$and": [
                { "$or": [ { "Main._t": { "$in": ["Gear", "Spare", "Winch"] } }, { "Main": { "$type": "object" }, "Main._t": { "$exists": false } } ] },
                { "$nor": [ { "Main._t": { "$in": ["Crane", "TowerCrane"] } } ] } ] }
            """));

        // Every value: nothing is left to take out.
        (await MatchOf("""{ "main.kind": { "nin": ["gear", "winch", "crane"] } }""")).ShouldBeBson(Bson("""
            { "$nor": [ { "$or": [ { "Main._t": { "$in": ["Gear", "Crane", "Spare", "TowerCrane", "Winch"] } }, { "Main": { "$type": "object" }, "Main._t": { "$exists": false } } ] } ] }
            """));
    }

    [Fact]
    public async Task Through_a_collection_the_root_classs_value_is_refused_with_the_way_to_ask_and_an_element_test_is_exact()
    {
        var error = await BindHost.ErrorAsync(Model.Value, Entity, """[ { "match": { "all.kind": { "eq": "gear" } } } ]""", Codes.InvalidOperand);

        error.Path.Should().Be("all.kind");
        error.Message.Should().Contain("'any' on the collection");

        (await MatchOf("""{ "all": { "any": { "kind": { "eq": "gear" } } } }""")).ShouldBeBson(Bson("""
            { "All": { "$elemMatch": { "$and": [
                { "$or": [ { "_t": { "$in": ["Gear", "Spare"] } }, { "_t": { "$exists": false } } ] },
                { "$nor": [ { "_t": { "$in": ["Crane", "TowerCrane", "Winch"] } } ] } ] } } }
            """));

        // A value only variants hold, none of which another variant derives from, is one comparison in a collection too.
        (await MatchOf("""{ "all.kind": { "eq": "winch" } }""")).ShouldBeBson(Bson("""{ "All._t": { "$in": ["Winch"] } }"""));
    }

    public class Rig
    {
        public Guid Id { get; set; }
        public Guid OrganizationId { get; set; }
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
