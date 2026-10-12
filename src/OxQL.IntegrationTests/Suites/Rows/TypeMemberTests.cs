using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Rows;

/// <summary>
/// The model's own type member on a real server: a member of a polymorphic
/// type that is a constant of the class (<c>public override string Type => "load";</c>) is not
/// stored, and REST returns it because the serializer calls the getter. Under contract 2 the engine
/// writes it from the stored discriminator and compares it by the variants that hold a value. The
/// documents are written by the driver from the classes below, so what is stored is what a service
/// stores: no <c>Type</c> element anywhere, and the driver's own discriminators.
/// <para>
/// The fixture holds every form: an interface with a collection and a single member of its variants,
/// two variants that share a value, a member that is a constant in three classes and reads the
/// instance in the fourth (<c>unit</c>: a finding, and no value per variant), an abstract base, a
/// concrete base whose own values are stored without a discriminator, a root class whose
/// discriminator is hierarchical with a class that inherits its parent's value, and a polymorphic
/// type with no such member at all.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class TypeMemberTests
{
    private const string Entity = "rows.job";

    private static readonly Lazy<EntityModel> Model = new(() =>
        ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(Job), "jobs", null, false)]));

    private static readonly IReadOnlyList<Job> Jobs =
    [
        new()
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Title = "a",
            First = new LoadStep { Dock = "D1" },
            Steps = [new LoadStep { Dock = "D2" }, new UnloadStep()],
            Asset = new Truck { Name = "T-1" },
            Note = new Note { Text = "plain" },
            Pet = new Cat { Name = "Tom" },
            Pets = [new Cat { Name = "Tom" }, new Lion { Name = "Leo" }],
            Badge = new GoldBadge { Label = "g", Carat = 18 },
        },
        new()
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000b2"), Title = "b",
            First = new ReloadStep(),
            Steps = [new CheckStep { Notes = "brakes" }],
            Asset = new Trailer { Name = "TR-1" },
            Note = new UrgentNote { Text = "now", Level = 3 },
            Pet = new Lion { Name = "Leo" },
            Pets = [new Lion { Name = "Leo" }],
            Badge = new PaperBadge { Label = "p" },
        },
        new()
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000c3"), Title = "c",
            First = new UnloadStep(),
            Steps = [],
            Asset = new Trailer { Name = "TR-2" },
            Pet = new Tiger { Name = "Khan" },
            Pets = [new Tiger { Name = "Khan" }],
        },
        new()
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000d4"), Title = "d",
            Steps = [new ReloadStep(), new CheckStep()],
            Note = new Note { Text = "later" },
        },
    ];

    private static async Task<(TestDatabase Database, EngineDirect Direct, IQueryEngine Engine)> HostAsync(string purpose)
    {
        var database = await MongoFixture.CreateDatabaseAsync(purpose);
        var jobs = Jobs.Select(job => { job.OrganizationId = EngineDirect.Organisation; return job; }).ToList();

        await database.Database.GetCollection<Job>("jobs").InsertManyAsync(jobs);

        var direct = new EngineDirect(Model.Value);

        return (database, direct, direct.Engine(await MongoFixture.ClientAsync(), database.Name, direct.Options()));
    }

    private static async Task<QueryResult> RunAsync(EngineDirect direct, IQueryEngine engine, string pipeline, int contract = 2)
    {
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, pipeline), direct.Context(contract: contract));

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static async Task<Core.Models.QueryValidationError> RefusedAsync(EngineDirect direct, IQueryEngine engine, string pipeline, string code, int contract = 2)
    {
        var outcome = await engine.ExecuteAsync(EngineDirect.Request(Entity, pipeline), direct.Context(contract: contract));
        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>($"{pipeline} is refused").Subject.Refusal;

        refusal.Status.Should().Be(400, EngineDirect.Describe(refusal));

        return (refusal.Errors ?? []).Should().ContainSingle(error => error.Code == code, EngineDirect.Describe(refusal)).Subject;
    }

    private static async Task<IReadOnlyList<string>> Titles(EngineDirect direct, IQueryEngine engine, string match) =>
        (await RunAsync(direct, engine, $$"""[ { "match": {{match}} }, { "sort": [ { "title": "asc" } ] }, { "project": { "title": 1 } } ]"""))
            .Items.Select(item => item!["title"]!.GetValue<string>()).ToList();

    private static TypeDef TypeOf(string member) => Model.Value.Entities[Entity].Root.Member(member)!.Leaf.Type!;

    // ---- the model -----------------------------------------------------------------------------

    [Fact]
    public void The_model_holds_the_value_of_every_variant_for_a_member_that_is_a_constant_of_the_class()
    {
        var type = TypeOf("first").Member("type")!;

        type.Stored.Should().BeFalse("the driver stores no member without a setter");
        type.Kind.Should().Be(Kind.String);
        type.ByVariant.Should().Equal(new Dictionary<string, string> { ["CheckStep"] = "check", ["LoadStep"] = "load", ["ReloadStep"] = "load", ["UnloadStep"] = "unload" },
            "two variants may hold one value; the names are the variants', ordinally");

        TypeOf("asset").Member("type")!.ByVariant.Should().Equal(new Dictionary<string, string> { ["Trailer"] = "trailer", ["Truck"] = "truck" });
        TypeOf("note").Member("kind")!.ByVariant.Should().Equal(new Dictionary<string, string> { ["Note"] = "note", ["UrgentNote"] = "urgent" },
            "a concrete base is stored itself, under its own name, and answers for itself");
        TypeOf("pet").Member("sound")!.ByVariant.Should().Equal(new Dictionary<string, string> { ["Cat"] = "meow", ["Lion"] = "roar", ["Tiger"] = "meow" },
            "a class that does not override the member answers what the class above it answers");

        // The path is not stored and says where its value is read.
        var path = Model.Value.Entities[Entity].Path("steps.type")!;

        path.Stored.Should().BeFalse();
        path.Derived!.Storage.Should().Be("Steps._t");
        Model.Value.Entities[Entity].Path("pet.sound")!.Derived!.Holder.DiscriminatorForm.Should().Be(DiscriminatorForm.Hierarchical);
    }

    [Fact]
    public void A_member_that_is_a_constant_in_some_classes_only_is_a_finding_and_has_no_value_per_variant()
    {
        TypeOf("first").Member("unit")!.ByVariant.Should().BeNull();

        var finding = Model.Value.Findings.Should().ContainSingle(found => found.Code == BuildCodes.VariantConstantUnresolved).Subject;

        finding.Target.Should().EndWith("#unit");
        finding.Message.Should().Contain("3 of the type's 4 classes").And.Contain("CheckStep").And.Contain("stays in the wire view only");
        finding.Detail.Should().Be("CheckStep: the getter reads the instance");
    }

    [Fact]
    public void A_polymorphic_type_without_such_a_member_and_an_ordinary_computed_member_are_left_as_they_are()
    {
        TypeOf("badge").Members.Should().OnlyContain(member => member.ByVariant == null);
        TypeOf("badge").Member("shout")!.Stored.Should().BeFalse("a computed member of the base, not a constant of any class");
        Model.Value.Findings.Should().NotContain(found => found.Target.Contains("shout", StringComparison.Ordinal), "a member that is a constant in no class is no type member gone wrong");
    }

    // ---- rows ----------------------------------------------------------------------------------

    /// <summary>What the service's REST API shows for the member: the serializer calls the getter.</summary>
    private static string? Rest(object? value, string member) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))![member]!.GetValue<string>();

    [Fact]
    public async Task A_row_shows_the_member_as_REST_shows_it_for_an_object_a_collection_a_bare_base_and_a_hierarchical_discriminator()
    {
        var (database, direct, engine) = await HostAsync("type-member-rows");
        await using var _ = database;

        var rows = (await RunAsync(direct, engine, """[ { "sort": [ { "title": "asc" } ] } ]""")).Items;

        rows.Should().HaveCount(Jobs.Count);

        foreach (var (row, job) in rows.Zip(Jobs))
        {
            var title = job.Title;

            (row!["first"]?["type"]?.GetValue<string>()).Should().Be(Rest(job.First, "type"), $"{title}: first.type");
            (row["asset"]?["type"]?.GetValue<string>()).Should().Be(Rest(job.Asset, "type"), $"{title}: asset.type");
            (row["note"]?["kind"]?.GetValue<string>()).Should().Be(Rest(job.Note, "kind"), $"{title}: note.kind, a base stored without a discriminator included");
            (row["pet"]?["sound"]?.GetValue<string>()).Should().Be(Rest(job.Pet, "sound"), $"{title}: pet.sound");
            row["steps"]!.AsArray().Select(step => step!["type"]!.GetValue<string>()).Should().Equal(job.Steps.Select(step => Rest(step, "type")), $"{title}: steps.type, per element");
            row["pets"]!.AsArray().Select(pet => pet!["sound"]!.GetValue<string>()).Should().Equal(job.Pets.Select(pet => Rest(pet, "sound")), $"{title}: pets.sound");

            // The member that is no constant in one class is in no row, and neither is a computed one.
            (row["first"] as JsonObject)?.ContainsKey("unit").Should().BeFalse();
            (row["badge"] as JsonObject)?.ContainsKey("shout").Should().BeFalse();
        }

        // A member a variant alone has is still read by the variant.
        rows[0]!["first"]!["dock"]!.GetValue<string>().Should().Be("D1");
        rows[1]!["note"]!["level"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task A_projection_of_the_member_alone_answers_it_and_an_exclusion_takes_it_out_of_the_row_only()
    {
        var (database, direct, engine) = await HostAsync("type-member-projection");
        await using var _ = database;

        var only = (await RunAsync(direct, engine, """[ { "sort": [ { "title": "asc" } ] }, { "project": { "id": 0, "first.type": 1, "steps.type": 1, "asset.type": 1, "note.kind": 1, "pet.sound": 1 } } ]""")).Items;

        only.Select(row => row!.ToJsonString()).Should().Equal(
            """{"first":{"type":"load"},"steps":[{"type":"load"},{"type":"unload"}],"asset":{"type":"truck"},"note":{"kind":"note"},"pet":{"sound":"meow"}}""",
            """{"first":{"type":"load"},"steps":[{"type":"check"}],"asset":{"type":"trailer"},"note":{"kind":"urgent"},"pet":{"sound":"roar"}}""",
            """{"first":{"type":"unload"},"steps":[],"asset":{"type":"trailer"},"pet":{"sound":"meow"}}""",
            """{"steps":[{"type":"load"},{"type":"check"}],"note":{"kind":"note"}}""");

        // Beside another member of the object, and of an unwound element.
        var beside = (await RunAsync(direct, engine, """[ { "match": { "title": { "eq": "a" } } }, { "unwind": { "path": "steps", "as": "step" } }, { "project": { "id": 0, "title": 1, "step.type": 1, "first.dock": 1, "first.type": 1 } } ]""")).Items;

        beside.Select(row => row!.ToJsonString()).Should().BeEquivalentTo(
            """{"title":"a","first":{"type":"load","dock":"D1"},"step":{"type":"load"}}""",
            """{"title":"a","first":{"type":"load","dock":"D1"},"step":{"type":"unload"}}""");

        // An exclusion hides the member; the object is still read by its variant.
        var without = (await RunAsync(direct, engine, """[ { "match": { "title": { "eq": "a" } } }, { "project": { "first.type": 0, "steps.type": 0 } } ]""")).Items.Single()!;

        without["first"]!.AsObject().ContainsKey("type").Should().BeFalse();
        without["first"]!["dock"]!.GetValue<string>().Should().Be("D1", "the discriminator stays in the stored row an exclusion reads, so the variant's own member is there");
        without["steps"]!.AsArray().Should().OnlyContain(step => !step!.AsObject().ContainsKey("type"));
        without["asset"]!["type"]!.GetValue<string>().Should().Be("truck", "what the exclusion does not name stays");
    }

    // ---- conditions ------------------------------------------------------------------------------

    [Fact]
    public async Task A_condition_compares_the_member_by_the_variants_that_hold_the_value()
    {
        var (database, direct, engine) = await HostAsync("type-member-filter");
        await using var _ = database;

        Task<IReadOnlyList<string>> Of(string match) => Titles(direct, engine, match);

        // An object: two variants hold "load".
        (await Of("""{ "first.type": { "eq": "load" } }""")).Should().Equal("a", "b");
        (await Of("""{ "first.type": { "neq": "load" } }""")).Should().Equal(["c", "d"], "a row without the object is not 'load', as a missing stored member is not");
        (await Of("""{ "first.type": { "in": ["unload", "check"] } }""")).Should().Equal("c");
        (await Of("""{ "first.type": { "nin": ["unload", "check"] } }""")).Should().Equal("a", "b", "d");
        (await Of("""{ "asset.type": { "eq": "truck" } }""")).Should().Equal("a");

        // Text folds case unless the condition says otherwise, as every string comparison of contract 2 does.
        (await Of("""{ "first.type": { "eq": "LOAD" } }""")).Should().Equal("a", "b");
        (await Of("""{ "first.type": { "eq": "LOAD", "options": { "caseSensitive": true } } }""")).Should().BeEmpty();
        (await Of("""{ "first.type": { "eq": "load", "options": { "caseSensitive": true } } }""")).Should().Equal("a", "b");

        // A value no variant holds selects no row, and every row negated.
        (await Of("""{ "first.type": { "eq": "fly" } }""")).Should().BeEmpty();
        (await Of("""{ "first.type": { "neq": "fly" } }""")).Should().Equal("a", "b", "c", "d");

        // A collection: some element, and negated none, as for a stored member.
        (await Of("""{ "steps.type": { "eq": "check" } }""")).Should().Equal("b", "d");
        (await Of("""{ "steps.type": { "eq": "load" } }""")).Should().Equal("a", "d");
        (await Of("""{ "steps.type": { "neq": "load" } }""")).Should().Equal("b", "c");
        (await Of("""{ "not": { "steps.type": { "in": ["load", "unload"] } } }""")).Should().Equal("b", "c");
        (await Of("""{ "steps": { "any": { "type": { "eq": "unload" } } } }""")).Should().Equal("a");

        // An unwound element.
        var unwound = await RunAsync(direct, engine, """[ { "unwind": { "path": "steps", "as": "step" } }, { "match": { "step.type": { "eq": "check" } } }, { "sort": [ { "title": "asc" } ] }, { "project": { "id": 0, "title": 1, "step.type": 1 } } ]""");

        unwound.Items.Select(row => row!.ToJsonString()).Should().Equal("""{"title":"b","step":{"type":"check"}}""", """{"title":"d","step":{"type":"check"}}""");

        // The request's filter, and a variable.
        var filtered = await engine.ExecuteAsync(
            EngineDirect.Request(Entity, """[ { "sort": [ { "title": "asc" } ] }, { "project": { "title": 1 } } ]""", """{ "wanted": ["unload"] }""") with
            {
                Filter = JsonSerializer.SerializeToElement(JsonNode.Parse("""[ { "id": "t", "where": { "first.type": { "in": { "$var": "wanted" } } } } ]""")),
            }, direct.Context());

        filtered.Should().BeOfType<QueryOutcome.Success>().Subject.Result.Items.Select(row => row!["title"]!.GetValue<string>()).Should().Equal("c");
    }

    [Fact]
    public async Task A_base_stored_without_a_discriminator_and_a_hierarchical_discriminator_are_compared_exactly()
    {
        var (database, direct, engine) = await HostAsync("type-member-forms");
        await using var _ = database;

        Task<IReadOnlyList<string>> Of(string match) => Titles(direct, engine, match);

        // A concrete base: its own values carry no discriminator.
        (await Of("""{ "note.kind": { "eq": "note" } }""")).Should().Equal(["a", "d"], "a row without the object holds no value: c has no note");
        (await Of("""{ "note.kind": { "eq": "urgent" } }""")).Should().Equal("b");
        (await Of("""{ "note.kind": { "neq": "note" } }""")).Should().Equal("b", "c");

        // Hierarchical: a lion is stored as [Animal, Cat, Lion], and is no "meow" for being a cat.
        (await Of("""{ "pet.sound": { "eq": "meow" } }""")).Should().Equal(["a", "c"], "the cat and the tiger, which inherits the cat's value; not the lion");
        (await Of("""{ "pet.sound": { "eq": "roar" } }""")).Should().Equal("b");
        (await Of("""{ "pet.sound": { "neq": "meow" } }""")).Should().Equal("b", "d");
        (await Of("""{ "pets.sound": { "eq": "roar" } }""")).Should().Equal(["a", "b"], "a variant no other variant derives from is told by its own discriminator, in a collection too");

        // Which element is a cat and no lion cannot be said for a collection's elements together.
        var refused = await RefusedAsync(direct, engine, """[ { "match": { "pets.sound": { "eq": "meow" } } } ]""", Codes.InvalidOperand);

        refused.Path.Should().Be("pets.sound");
        refused.Message.Should().Contain("'any' on the collection");
        (await Of("""{ "pets": { "any": { "sound": { "eq": "meow" } } } }""")).Should().Equal("a", "c");
    }

    // ---- what it cannot do, said ------------------------------------------------------------------

    [Fact]
    public async Task What_the_member_cannot_do_is_refused_with_the_reason_and_what_it_can_do()
    {
        var (database, direct, engine) = await HostAsync("type-member-refusals");
        await using var _ = database;

        foreach (var pipeline in new[]
        {
            """[ { "sort": [ { "first.type": "asc" } ] } ]""",
            """[ { "group": { "by": [ { "path": "first.type", "as": "t" } ], "fields": { "n": { "count": true } } } } ]""",
            """[ { "group": { "by": [], "fields": { "t": { "min": "first.type" } } } } ]""",
            """[ { "values": { "path": "first.type" } } ]""",
        })
        {
            var error = await RefusedAsync(direct, engine, pipeline, Codes.NotStored);

            error.Path.Should().Be("first.type", pipeline);
            error.Message.Should().Contain("follows from the variant").And.Contain("eq, neq, in and nin");
            error.Params!["reason"].Should().Be(PathReasons.Derived);
        }

        var ordered = await RefusedAsync(direct, engine, """[ { "match": { "first.type": { "gt": "a" } } } ]""", Codes.InvalidOperand);

        ordered.Params!["reason"].Should().Be(PathReasons.Derived);
        (await RefusedAsync(direct, engine, """[ { "match": { "first.type": { "eq": null } } } ]""", Codes.InvalidOperand)).Message.Should().Contain("'check', 'load', 'unload'");
    }

    [Fact]
    public async Task A_projection_of_a_member_no_row_can_carry_is_refused_where_it_was_answered_without_it()
    {
        var (database, direct, engine) = await HostAsync("type-member-unproduced");
        await using var _ = database;

        // Not a constant in one class: it has no value per variant.
        var unit = await RefusedAsync(direct, engine, """[ { "project": { "title": 1, "first.unit": 1 } } ]""", Codes.NotStored);

        unit.Path.Should().Be("first.unit");
        unit.Stage.Should().Be(0);
        unit.Message.Should().Be("'first.unit' is not stored: the service computes it when it returns a record, and a query reads what is stored, so no row can carry it. Leave it out of the projection.");
        unit.Params!["reason"].Should().Be(PathReasons.NotStored);
        unit.Params["member"].Should().Be("first.unit");

        // A computed member of a type with no type member.
        (await RefusedAsync(direct, engine, """[ { "project": { "badge.shout": 1 } } ]""", Codes.NotStored)).Path.Should().Be("badge.shout");

        // An exclusion asks for nothing; every other use was refused before and still is.
        (await RunAsync(direct, engine, """[ { "project": { "first.unit": 0 } } ]""")).Items.Should().HaveCount(Jobs.Count);
        (await RefusedAsync(direct, engine, """[ { "match": { "first.unit": { "eq": "x" } } } ]""", Codes.NotStored)).Message.Should().Be("'first.unit' is not stored; it is in the wire view only.");
    }

    [Fact]
    public async Task Contract_1_reads_the_member_as_it_always_did()
    {
        var (database, direct, engine) = await HostAsync("type-member-contract-1");
        await using var _ = database;

        // Projected: accepted, and not in the row.
        var rows = (await RunAsync(direct, engine, """[ { "match": { "title": { "eq": "a" } } }, { "project": { "title": 1, "first.type": 1, "first.unit": 1 } } ]""", contract: 1)).Items;

        rows.Single()!.ToJsonString().Should().NotContain("load").And.NotContain("type");

        // Filtered: not stored.
        (await RefusedAsync(direct, engine, """[ { "match": { "first.type": { "eq": "load" } } } ]""", Codes.NotStored, contract: 1)).Message
            .Should().Be("'first.type' is not stored; it is in the wire view only.");
    }

    // ---- explain ---------------------------------------------------------------------------------

    [Fact]
    public async Task Explain_shows_the_member_as_a_string_column_and_says_what_a_condition_can_do_with_it()
    {
        var (database, direct, engine) = await HostAsync("type-member-explain");
        await using var _ = database;

        async Task<ExplainResult> ExplainAsync(string pipeline)
        {
            var outcome = await engine.ExplainAsync(new ExplainRequest { Query = EngineDirect.Request(Entity, pipeline), Include = [ExplainRequest.IncludeShape, ExplainRequest.IncludeTypes], IsEnvelope = true }, direct.Context());

            return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        }

        var answer = await ExplainAsync("""[ { "match": { "first.type": { "eq": "load" } } }, { "project": { "title": 1, "first.type": 1, "asset": 1, "steps.type": 1 } } ]""");

        answer.Valid.Should().BeTrue(string.Join("; ", answer.Errors.Select(error => error.Code + " " + error.Message)));

        var columns = answer.Result!.Columns.ToDictionary(column => column.Path);

        columns["first.type"].Kind.Should().Be("string");
        columns["first.type"].Nullable.Should().BeTrue("the object that holds it may be null");
        columns["first.type"].Present.Should().Be(ExplainColumn.IfStored);
        columns["steps"].Kind.Should().Be("array", "a collection is one column; its elements carry the member");

        // The flags of the member, in the types of the answer: projected and compared, nothing else.
        var flags = Flags(answer, "first.type");

        flags["operators"]!.AsArray().Select(op => op!.GetValue<string>()).Should().Equal("eq", "neq", "in", "nin");
        flags["projectable"]!.GetValue<bool>().Should().BeTrue();
        flags["sortable"]!.GetValue<bool>().Should().BeFalse();
        flags["groupable"]!.GetValue<bool>().Should().BeFalse();
        flags["folds"]!.GetValue<bool>().Should().BeTrue("the comparison folds case unless the condition opts out");

        // The member no row can carry: a member of the type still, and nothing can be done with it.
        var unit = Flags(answer, "first.unit");

        unit["projectable"]!.GetValue<bool>().Should().BeFalse();
        unit["operators"]!.AsArray().Should().BeEmpty();

        // And explain says of a request what a run says.
        var refused = await ExplainAsync("""[ { "project": { "first.unit": 1 } } ]""");

        refused.Valid.Should().BeFalse();
        refused.Errors.Should().ContainSingle(error => error.Code == Codes.NotStored && error.Path == "first.unit");
        (await ExplainAsync("""[ { "sort": [ { "first.type": "asc" } ] } ]""")).Errors.Should().ContainSingle(error => error.Code == Codes.NotStored && (string?)error.Params!["reason"] == PathReasons.Derived);
    }

    /// <summary>The flag set of a member of the entity's type table in an answer with <c>include: "types"</c>.</summary>
    private static JsonObject Flags(ExplainResult answer, string path)
    {
        var table = answer.Types.Single(type => type.Key.EndsWith(Entity, StringComparison.Ordinal)).Value!.AsObject();
        var row = table["members"]!.AsArray().Single(member => member!.AsArray()[0]!.GetValue<string>() == path)!.AsArray();

        return answer.FlagSets![row[3]!.GetValue<string>()]!.AsObject();
    }

    [Fact]
    public async Task Explain_lists_the_values_the_member_can_hold_with_its_type()
    {
        var (database, direct, engine) = await HostAsync("type-member-explain-values");
        await using var _ = database;

        var outcome = await engine.ExplainAsync(new ExplainRequest { Query = EngineDirect.Request(Entity, "[]"), Include = [ExplainRequest.IncludeTypes], IsEnvelope = true }, direct.Context());
        var answer = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        var table = answer.Types.Single(type => type.Key.EndsWith(Entity, StringComparison.Ordinal)).Value!.AsObject();
        var row = table["members"]!.AsArray().Single(member => member!.AsArray()[0]!.GetValue<string>() == "first.type")!.AsArray();

        row[4]!.ToJsonString().Should().Be("""{"stored":false,"values":["check","load","unload"]}""");
    }

    // ---- the fleet, over HTTP ---------------------------------------------------------------------

    /// <summary>
    /// The transport service's resources, as logistics models them: <c>type</c> is a constant of each
    /// resource class. Over HTTP every row shows the value of the class its stored discriminator names,
    /// a condition on it selects exactly the rows stored as the classes that hold the value, and the
    /// count is of those rows.
    /// </summary>
    [Fact]
    public async Task The_fleets_resources_answer_their_type_from_the_stored_discriminator_and_are_filtered_by_it()
    {
        var values = new Dictionary<string, string>
        {
            ["DriverResource"] = "driver", ["TractorUnitResource"] = "tractor_unit", ["TrailerResource"] = "trailer", ["CarResource"] = "car",
            ["ContainerResource"] = "container", ["CarrierResource"] = "carrier", ["EquipmentResource"] = "equipment",
        };
        var stored = ReportSeed.Rows(ReportSeed.Resource).ToDictionary(row => row.Id, row => values[row.Stored["_t"].AsString]);
        var client = await Lab.ClientAsync(LabService.Transport, Org.R);

        stored.Values.Distinct().Count().Should().BeGreaterThan(2, "the corpus holds several kinds of resource");

        var all = await client.SendAsync(ReportSeed.Resource, """[ { "project": { "id": 1, "type": 1 } }, { "page": { "limit": 200 } } ]""");

        all.ShouldBeOk();
        all.Items.Should().HaveCount(stored.Count);
        all.Items.Should().OnlyContain(row => row!["type"]!.GetValue<string>() == stored[row["id"]!.GetValue<Guid>()]);

        var drivers = await client.SendAsync(ReportSeed.Resource, """[ { "match": { "type": { "in": ["driver", "trailer"] } } }, { "page": { "limit": 200, "count": true } } ]""");
        var expected = stored.Where(pair => pair.Value is "driver" or "trailer").Select(pair => pair.Key).ToList();

        drivers.ShouldHaveIdsInAnyOrder(expected);
        drivers.Items.Should().OnlyContain(row => row!["type"]!.GetValue<string>() == "driver" || row["type"]!.GetValue<string>() == "trailer", "the whole row carries the member too");
        drivers.ShouldHaveTotal(expected.Count);
        (await client.SendAsync(ReportSeed.Resource, """[ { "match": { "type": { "nin": ["driver", "trailer"] } } }, { "page": { "limit": 200 } } ]""")).ShouldHaveIdsInAnyOrder(stored.Keys.Except(expected));
    }

    // ---- fixture ---------------------------------------------------------------------------------

    public class Job
    {
        public Guid Id { get; set; }
        public Guid OrganizationId { get; set; }
        public string Title { get; set; } = "";
        public IStep? First { get; set; }
        public List<IStep> Steps { get; set; } = [];
        public Asset? Asset { get; set; }
        public Note? Note { get; set; }
        public Animal? Pet { get; set; }
        public List<Animal> Pets { get; set; } = [];
        public Badge? Badge { get; set; }
    }

    /// <summary>An interface known through its variants, as logistics' tour actions are.</summary>
    public interface IStep
    {
        Guid Id { get; set; }

        string Type { get; }

        string Unit { get; }
    }

    public abstract class StepBase : IStep
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string? Notes { get; set; }

        public abstract string Type { get; }

        public abstract string Unit { get; }
    }

    public class LoadStep : StepBase
    {
        public string? Dock { get; set; }

        public override string Type => "load";

        public override string Unit => "LoadUnit";
    }

    public class UnloadStep : StepBase
    {
        public override string Type => StepTypes.Unload;

        public override string Unit => "UnloadUnit";
    }

    /// <summary>Shares its value with <see cref="LoadStep"/>.</summary>
    public class ReloadStep : StepBase
    {
        public override string Type => "load";

        public override string Unit => "LoadUnit";
    }

    /// <summary>Its <c>Unit</c> reads the instance: no constant of the class.</summary>
    public class CheckStep : StepBase
    {
        public override string Type => "check";

        public override string Unit => Notes ?? "CheckUnit";
    }

    public static class StepTypes
    {
        public const string Unload = "unload";
    }

    public abstract class Asset
    {
        public string Name { get; set; } = "";

        public abstract string Type { get; }
    }

    public class Truck : Asset
    {
        public override string Type => "truck";
    }

    public class Trailer : Asset
    {
        public override string Type => "trailer";
    }

    /// <summary>A concrete base: stored under its own nominal type without a discriminator.</summary>
    public class Note
    {
        public string Text { get; set; } = "";

        public virtual string Kind => "note";
    }

    public class UrgentNote : Note
    {
        public int Level { get; set; }

        public override string Kind => "urgent";
    }

    /// <summary>A root class: every value stores its discriminators from the root down.</summary>
    [BsonDiscriminator(RootClass = true)]
    public abstract class Animal
    {
        public string Name { get; set; } = "";

        public abstract string Sound { get; }
    }

    public class Cat : Animal
    {
        public override string Sound => "meow";
    }

    public class Lion : Cat
    {
        public override string Sound => "roar";
    }

    /// <summary>Does not override: answers what a cat answers.</summary>
    public class Tiger : Cat
    {
    }

    /// <summary>A polymorphic type with no constant member; <c>Shout</c> is computed from the instance.</summary>
    public abstract class Badge
    {
        public string Label { get; set; } = "";

        public string Shout => Label.ToUpperInvariant();
    }

    public class GoldBadge : Badge
    {
        public int Carat { get; set; }
    }

    public class PaperBadge : Badge
    {
    }
}

/// <summary>The class maps of <see cref="TypeMemberTests"/>, registered once when the assembly loads, as a host registers them.</summary>
internal static class TypeMemberRegistrations
{
    [ModuleInitializer]
    internal static void Register()
    {
        BsonClassMap.RegisterClassMap<TypeMemberTests.LoadStep>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.UnloadStep>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.ReloadStep>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.CheckStep>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.Truck>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.Trailer>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.UrgentNote>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.Cat>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.Lion>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.Tiger>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.GoldBadge>();
        BsonClassMap.RegisterClassMap<TypeMemberTests.PaperBadge>();
    }
}
