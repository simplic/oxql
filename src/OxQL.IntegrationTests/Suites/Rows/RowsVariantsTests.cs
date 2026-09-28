using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Rows;

/// <summary>
/// Rows of polymorphic and self-similar members on a real server (OxQL 2.1): <c>unwind.flatten</c>
/// over three levels of nesting in pre-order, each row without its nested collection, and the
/// <c>UNWIND_DEPTH_TRUNCATED</c> diagnostic when the host descends fewer levels than are stored
/// (also through a group); <c>is</c> over a hierarchical discriminator on an object, a collection
/// and an unwound element; and every row encoded by its variant, so a member the variants store
/// under different names is in the row. The documents are written by the driver from the classes
/// below, so the stored discriminators are the driver's own.
/// </summary>
[Trait("Category", "Integration")]
public class RowsVariantsTests
{
    private const string Entity = "rows.outline";

    private static readonly Lazy<EntityModel> Model = new(() =>
        ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(Outline), "outlines", null, false)]));

    /// <summary>Three levels: a > (a1 > a1x), a2; then b. Pre-order: a, a1, a1x, a2, b.</summary>
    private static Outline Nested() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-00000000000a"),
        OrganizationId = EngineDirect.Organisation,
        Title = "nested",
        Items =
        [
            new OutlineItem { Label = "a", Items = [new OutlineItem { Label = "a1", Items = [new OutlineItem { Label = "a1x" }] }, new OutlineItem { Label = "a2" }] },
            new OutlineItem { Label = "b" },
        ],
        Lead = new Shark { Name = "Bruce", Code = "S-1", Teeth = 300 },
        Creatures = [new Bird { Name = "Tweety", Code = "B-1", Flies = true }, new Shark { Name = "Jaws", Code = "S-2", Teeth = 280 }],
    };

    /// <summary>One level only: no truncation at any depth.</summary>
    private static Outline Flat() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-00000000000b"),
        OrganizationId = EngineDirect.Organisation,
        Title = "flat",
        Items = [new OutlineItem { Label = "x" }, new OutlineItem { Label = "y" }],
        Lead = new Bird { Name = "Polly", Code = "B-2", Flies = false },
        Creatures = [new Fish { Name = "Nemo", Code = "F-1", Fins = 5 }],
    };

    private static async Task<(TestDatabase Database, EngineDirect Direct, IQueryEngine Engine)> HostAsync(string purpose, int depth)
    {
        var database = await MongoFixture.CreateDatabaseAsync(purpose);
        await database.Database.GetCollection<Outline>("outlines").InsertManyAsync([Nested(), Flat()]);

        var direct = new EngineDirect(Model.Value);
        var engine = direct.Engine(await MongoFixture.ClientAsync(), database.Name, direct.Options(options => options.Limits.MaxFlattenDepth = depth));

        return (database, direct, engine);
    }

    private static async Task<QueryResult> RunAsync(EngineDirect direct, IQueryEngine engine, string pipeline, int depth)
    {
        var context = direct.Context(direct.Options(options => options.Limits.MaxFlattenDepth = depth));
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, pipeline), context);

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private const string FlattenNested = """
        [ { "match": { "title": { "eq": "nested" } } },
          { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
          { "project": { "item": 1, "position": 1 } } ]
        """;

    [Fact]
    public async Task Flatten_yields_every_item_of_three_levels_in_pre_order_each_without_its_nested_collection()
    {
        var (database, direct, engine) = await HostAsync("rows-flatten-3", depth: 5);
        await using var _ = database;

        var result = await RunAsync(direct, engine, FlattenNested, depth: 5);
        var rows = result.Items.Select(item => item!.AsObject()).ToList();

        rows.Select(row => row["item"]!["label"]!.GetValue<string>()).Should().Equal("a", "a1", "a1x", "a2", "b");
        rows.Select(row => row["position"]!.GetValue<int>()).Should().Equal(0, 1, 2, 3, 4);
        rows.Should().AllSatisfy(row => row["item"]!.AsObject().ContainsKey("items").Should().BeFalse("a flattened copy has its nested collection removed"));
        rows.Should().AllSatisfy(row => row.Select(pair => pair.Key).Should().NotContain(key => key.StartsWith("__", StringComparison.Ordinal)));
        result.Diagnostics.Should().BeNull("three levels are within the default depth");
    }

    [Fact]
    public async Task A_host_that_descends_fewer_levels_than_are_stored_leaves_the_deeper_items_out_and_says_so()
    {
        var (database, direct, engine) = await HostAsync("rows-flatten-truncated", depth: 2);
        await using var _ = database;

        var result = await RunAsync(direct, engine, FlattenNested, depth: 2);

        result.Items.Select(item => item!["item"]!["label"]!.GetValue<string>()).Should().Equal("a", "a1", "a2", "b");

        var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
        diagnostic.Code.Should().Be(Codes.UnwindDepthTruncated);
        diagnostic.Stage.Should().Be(1);
        diagnostic.Path.Should().Be("items");
        diagnostic.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["path"] = "items", ["depth"] = 2, ["rows"] = 4 });
    }

    [Fact]
    public async Task The_truncation_reaches_the_group_whose_rows_it_touched_and_no_other()
    {
        var (database, direct, engine) = await HostAsync("rows-flatten-group", depth: 2);
        await using var _ = database;

        var result = await RunAsync(direct, engine, """
            [ { "unwind": { "path": "items", "flatten": "items", "as": "item" } },
              { "group": { "by": [ { "path": "title", "as": "title" } ], "fields": { "n": { "count": true } } } },
              { "sort": [ { "title": "asc" } ] } ]
            """, depth: 2);

        result.Items.Select(item => (item!["title"]!.GetValue<string>(), item["n"]!.ToString())).Should().Equal(("flat", "2"), ("nested", "4"));
        result.Diagnostics.Should().ContainSingle().Which.Params!["rows"].Should().Be(1);
    }

    [Fact]
    public async Task Is_selects_by_the_variant_and_its_descendants_on_an_object_a_collection_and_an_unwound_element()
    {
        var (database, direct, engine) = await HostAsync("rows-is", depth: 5);
        await using var _ = database;

        async Task<IReadOnlyList<string>> Titles(string match) =>
            (await RunAsync(direct, engine, $$"""[ { "match": {{match}} }, { "sort": [ { "title": "asc" } ] } ]""", depth: 5))
                .Items.Select(item => item!["title"]!.GetValue<string>()).ToList();

        (await Titles("""{ "lead": { "is": "Fish" } }""")).Should().Equal("nested");
        (await Titles("""{ "lead": { "is": ["Bird", "Shark"] } }""")).Should().Equal("flat", "nested");
        (await Titles("""{ "creatures": { "is": "Shark" } }""")).Should().Equal("nested");
        (await Titles("""{ "creatures": { "is": "Fish" } }""")).Should().Equal("flat", "nested");
        (await Titles("""{ "not": { "creatures": { "is": "Bird" } } }""")).Should().Equal("flat");

        var fish = await RunAsync(direct, engine, """
            [ { "unwind": { "path": "creatures", "as": "creature" } },
              { "match": { "creature": { "is": "Fish" } } },
              { "project": { "creature": 1 } } ]
            """, depth: 5);

        fish.Items.Select(item => item!["creature"]!["name"]!.GetValue<string>()).Should().BeEquivalentTo(["Jaws", "Nemo"]);
    }

    [Fact]
    public async Task Every_row_is_encoded_by_its_variant_so_a_member_the_variants_store_apart_is_in_the_row()
    {
        var (database, direct, engine) = await HostAsync("rows-variant-encoding", depth: 5);
        await using var _ = database;

        var result = await RunAsync(direct, engine, """[ { "match": { "title": { "eq": "nested" } } }, { "project": { "lead": 1, "creatures": 1 } } ]""", depth: 5);
        var row = result.Items.Single()!.AsObject();

        ShouldEqual(row["lead"], """{"name":"Bruce","code":"S-1","fins":0,"teeth":300}""");
        ShouldEqual(row["creatures"], """[{"name":"Tweety","code":"B-1","flies":true},{"name":"Jaws","code":"S-2","fins":0,"teeth":280}]""");

        // Read by the merged type alone, code is unstored (the variants disagree on its element) and dropped.
        Model.Value.Entities[Entity].Root.Member("lead")!.Type!.Member("code")!.Stored.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_variant_and_a_flatten_over_a_member_that_does_not_nest_the_items_are_refused()
    {
        var (database, direct, engine) = await HostAsync("rows-variant-refusals", depth: 5);
        await using var _ = database;

        async Task<Core.Models.QueryValidationError> Refused(string pipeline, string code)
        {
            var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, pipeline), direct.Context());
            var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

            refusal.Status.Should().Be(400, EngineDirect.Describe(refusal));

            return (refusal.Errors ?? []).Should().ContainSingle(error => error.Code == code, EngineDirect.Describe(refusal)).Subject;
        }

        (await Refused("""[ { "match": { "lead": { "is": "Whale" } } } ]""", Codes.UnknownVariant)).Message
            .Should().Be("'Whale' is not a variant of 'lead'; its variants are Bird, Fish, Shark.");
        (await Refused("""[ { "unwind": { "path": "creatures", "flatten": "name" } } ]""", Codes.FlattenNotRecursive)).Path
            .Should().Be("creatures.name");
    }

    /// <summary>The same members and values; the order is the merged type's, which the model decides.</summary>
    private static void ShouldEqual(JsonNode? actual, string expected) =>
        JsonNode.DeepEquals(actual, JsonNode.Parse(expected)).Should().BeTrue($"{actual?.ToJsonString()} should equal {expected}");

    // ---- fixture ------------------------------------------------------------------------------

    public class Outline
    {
        public Guid Id { get; set; }
        public Guid OrganizationId { get; set; }
        public string Title { get; set; } = "";
        public List<OutlineItem> Items { get; set; } = [];
        public Creature? Lead { get; set; }
        public List<Creature> Creatures { get; set; } = [];
    }

    public class OutlineItem
    {
        public string Label { get; set; } = "";
        public List<OutlineItem> Items { get; set; } = [];
    }

    [BsonDiscriminator(RootClass = true)]
    public abstract class Creature
    {
        public string? Name { get; set; }
    }

    public class Bird : Creature
    {
        [BsonElement("bc")]
        public string Code { get; set; } = "";

        public bool Flies { get; set; }
    }

    public class Fish : Creature
    {
        [BsonElement("fc")]
        public string Code { get; set; } = "";

        public int Fins { get; set; }
    }

    [BsonDiscriminator("shark")]
    public class Shark : Fish
    {
        public int Teeth { get; set; }
    }
}

/// <summary>
/// The class maps of <see cref="RowsVariantsTests"/>, registered as a host registers them: once,
/// when the assembly loads, before any model build looks one up (a registration made while another
/// build is inside its tracked lookup would be counted as that build's own and never a variant).
/// </summary>
internal static class RowsVariantsRegistrations
{
    [ModuleInitializer]
    internal static void Register()
    {
        BsonClassMap.RegisterClassMap<RowsVariantsTests.Bird>();
        BsonClassMap.RegisterClassMap<RowsVariantsTests.Fish>();
        BsonClassMap.RegisterClassMap<RowsVariantsTests.Shark>();
    }
}
