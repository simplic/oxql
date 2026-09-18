using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The execution half of the conformance fixes: the cursor a projection used to poison (P3),
/// the wire encodings a JavaScript caller could not read (F9, F-ENT-005), and the guard that
/// turns an escaped exception into a coded refusal instead of a bare 500.
/// </summary>
public class ConformanceExecutionTests
{
    private const string Order = "probe.order";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner) Host(Action<FakeAggregateRunner>? configure = null)
    {
        var runner = new FakeAggregateRunner();

        configure?.Invoke(runner);

        return (new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options()), runner);
    }

    private static BsonDocument Row(Guid id, string number) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["Number"] = number,
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
    };

    private static async Task<QueryResult> Success(MongoQueryEngine engine, string pipeline, RequestContext? context = null)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), context ?? BindHost.Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    // ---- P3 · the cursor a projection poisoned -----------------------------------------------

    /// <summary>
    /// P3, the S1 of the paging battery. <c>NextCursor</c> mints each sort leg by reading the
    /// value off the document it hands back, so a projection that removed the sort path left
    /// the leg reading a member that is not there: <c>?? BsonNull</c> turned "absent from the
    /// projection" into the value null, the null-aware ascending predicate re-admitted every
    /// non-null row, and
    /// <c>[{sort:[{number:'asc'}]},{project:{id:1}},{page:{limit:3}}]</c> returned rows 1–3 on
    /// every page with an identical cursor and <c>hasNextPage</c> true for ever. The compiler
    /// keeps the sort path in storage now, the way it already kept the key.
    /// </summary>
    [Fact]
    public async Task P3_a_projection_that_drops_the_sort_key_still_pages()
    {
        var (engine, runner) = Host(runner => runner.PageRows = [Row(Id1, "A"), Row(Id2, "B")]);
        var result = await Success(engine, """[{ "sort": [{ "number": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 1 } }]""");

        // The projection the compiler emitted keeps the sort path, so the row the cursor is
        // minted from carries a value.
        var projection = runner.Calls[0].Stages.Single(stage => stage.Contains("$project"))["$project"].AsBsonDocument;

        projection.Contains("Number").Should().BeTrue("the paging sort still has to read it");
        projection["_id"].Should().Be(BsonValue.Create(1));

        result.PageInfo.NextCursor.Should().NotBeNull();

        var payload = BindHost.Cursors.Decode(result.PageInfo.NextCursor!, FingerprintOf(result.PageInfo.NextCursor!));

        payload.Should().NotBeNull();
        payload!.Fields.Should().HaveCount(2);
        payload.Fields[0].Wire.Should().Be("number");
        payload.Fields[0].Value.Should().Be(new BsonString("A"), "the leg carries the row's real value, not a fabricated null");
    }

    /// <summary>
    /// P3. The two pages of the same walk differ, which is the whole point: the defect's
    /// signature was an identical cursor for every page.
    /// </summary>
    [Fact]
    public async Task P3_two_pages_of_a_projected_walk_carry_different_cursors()
    {
        var (first, _) = Host(runner => runner.PageRows = [Row(Id1, "A"), Row(Id2, "B")]);
        var one = await Success(first, """[{ "sort": [{ "number": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 1 } }]""");

        var (second, _) = Host(runner => runner.PageRows = [Row(Id2, "B")]);
        var two = await Success(second, $$"""[{ "sort": [{ "number": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 1, "cursor": "{{one.PageInfo.NextCursor}}" } }]""");

        two.PageInfo.NextCursor.Should().NotBe(one.PageInfo.NextCursor);
    }

    /// <summary>
    /// P3. A contract 1 row is rendered from the document rather than from the shape, so what
    /// only the paging read is removed again — the rule the key already followed.
    /// </summary>
    [Fact]
    public async Task P3_a_contract_1_row_loses_the_sort_key_it_did_not_ask_for()
    {
        var (engine, _) = Host(runner => runner.PageRows = [Row(Id1, "A")]);
        var result = await Success(engine,
            """[{ "sort": [{ "number": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 5 } }]""",
            BindHost.Context(contract: 1));

        result.Items.Should().ContainSingle();

        var row = result.Items[0]!.AsObject();

        row.ContainsKey("number").Should().BeFalse("the caller projected it away; only the paging read it");
        row.Count.Should().BeGreaterThan(0, "what the caller did ask for is still there");
    }

    /// <summary>P3. A projection that does name the sort key is unchanged, and so is one that names no key.</summary>
    [Fact]
    public async Task P3_a_projection_that_keeps_the_sort_key_is_unchanged()
    {
        var (engine, _) = Host(runner => runner.PageRows = [Row(Id1, "A"), Row(Id2, "B")]);
        var result = await Success(engine, """[{ "sort": [{ "number": "asc" }] }, { "project": { "id": 1, "number": 1 } }, { "page": { "limit": 1 } }]""");

        result.Items[0]!.AsObject().ContainsKey("number").Should().BeTrue();
        result.PageInfo.NextCursor.Should().NotBeNull();
    }

    private static string FingerprintOf(string cursor)
    {
        // The fingerprint is not on the wire; decode with the one the same query produces.
        var bound = BindHost.BindAsync(BindHost.Probe, BindHost.Request(Order,
            """[{ "sort": [{ "number": "asc" }] }, { "project": { "id": 1 } }, { "page": { "limit": 1 } }]""")).GetAwaiter().GetResult();

        return ((BindOutcome.Bound)bound).Pipeline.Fingerprint;
    }

    // ---- F9 / F-ENT-005 · values JSON cannot carry -------------------------------------------

    /// <summary>
    /// F9. The addon bag is a dictionary of <c>unknown</c>, encoded by BSON type alone, so an
    /// addon <c>long</c> holding 9007199254740993 travelled as a JSON number and every
    /// JavaScript caller read it back as ...992 — a value that then matches nothing. Filtering
    /// on it worked, so the value could be found and could not be read.
    /// </summary>
    [Fact]
    public async Task F9_an_addon_long_above_the_safe_range_travels_as_a_string()
    {
        var row = Row(Id1, "A");

        row["Addon"] = new BsonDocument
        {
            ["tourCount"] = new BsonInt64(9007199254740993),
            ["small"] = new BsonInt64(42),
            ["negative"] = new BsonInt64(-9007199254740993),
        };

        var (engine, _) = Host(runner => runner.PageRows = [row]);
        var addon = (await Success(engine, """[{ "project": { "id": 1, "addon": 1 } }]""")).Items[0]!["addon"]!.AsObject();

        addon["tourCount"]!.ToString().Should().Be("9007199254740993");
        addon["tourCount"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.String);
        addon["negative"]!.ToString().Should().Be("-9007199254740993");

        // Inside the range nothing changes, which leaves every addon integer in use today alone.
        addon["small"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.Number);
    }

    /// <summary>
    /// F-ENT-005. <c>Kind.Long</c> stringifies for exactly this reason and an enum did not, so
    /// a <c>long</c>-backed enum member above 2^53 travelled as a number while its sibling
    /// <c>long</c> in the same row travelled as a string.
    /// </summary>
    [Fact]
    public void F_ENT_005_an_enum_value_above_the_safe_range_travels_as_a_string()
    {
        WireEncoder.EncodeScalar(new BsonInt64(9007199254740993), Kind.Enum, null)!.GetValueKind()
            .Should().Be(System.Text.Json.JsonValueKind.String);
        WireEncoder.EncodeScalar(new BsonInt64(9007199254740993), Kind.Enum, null)!.ToString()
            .Should().Be("9007199254740993");

        // Every enum the fleet declares is inside Int32 and is unchanged.
        WireEncoder.EncodeScalar(new BsonInt64(3), Kind.Enum, null)!.GetValueKind()
            .Should().Be(System.Text.Json.JsonValueKind.Number);
        WireEncoder.EncodeScalar(new BsonInt32(3), Kind.Enum, null)!.ToString().Should().Be("3");
    }

    // ---- G2 · a composite group key ----------------------------------------------------------

    /// <summary>
    /// G2. A scalar <c>$group</c> <c>_id</c> normalises a missing member to null and a
    /// subdocument <c>_id</c> omits the field, so one <c>by</c> merged null and missing into
    /// one bucket and two split them — and the row lost the member entirely, arriving
    /// <c>undefined</c> where the type says <c>string | null</c>.
    /// </summary>
    [Fact]
    public async Task G2_every_leg_of_a_composite_group_key_normalises_a_missing_member_to_null()
    {
        var (engine, runner) = Host(runner => runner.PageRows = []);

        await Success(engine, """[{ "group": { "by": [{ "path": "flag", "as": "f" }, { "path": "number", "as": "n" }], "fields": { "total": { "count": true } } } }]""");

        var id = runner.Calls[0].Stages.Single(stage => stage.Contains("$group"))["$group"]["_id"].AsBsonDocument;

        id["f"].AsBsonDocument.Contains("$ifNull").Should().BeTrue();
        id["n"].AsBsonDocument.Contains("$ifNull").Should().BeTrue();

        // A single key was already right, and stays a bare field reference.
        var (single, singleRunner) = Host(runner => runner.PageRows = []);

        await Success(single, """[{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "total": { "count": true } } } }]""");
        singleRunner.Calls[0].Stages.Single(stage => stage.Contains("$group"))["$group"]["_id"].Should().Be(BsonValue.Create("$Number"));
    }
}
