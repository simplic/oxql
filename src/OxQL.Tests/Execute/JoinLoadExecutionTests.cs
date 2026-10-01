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
/// Select inference at run time and in explain (improvement plan §3.S) against fakes: the join in the
/// aggregate projects its load set and the row is cut to the output set; a join after the page asks
/// its owner for the output set and the row shows exactly it; a stage continued at an owner travels
/// without an inferred select and the paths projected under its alias travel as paths; the plan hash
/// does not depend on the order paths are named in; and explain answers the ledger and each join's
/// loads, the owner's for what the owner binds.
/// </summary>
public class JoinLoadExecutionTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static readonly Guid InvoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid ContactId = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    /// <summary>An invoice row as the aggregate answers it: the customer joined under <c>c</c> with everything the fake holds, whatever the join projects.</summary>
    private static BsonDocument Joined() => new()
    {
        ["_id"] = Id(InvoiceId),
        ["OrganizationId"] = Id(BindHost.Organisation),
        ["Number"] = "RE-1",
        ["CustomerId"] = Id(CustomerId),
        ["ContactId"] = Id(ContactId),
        ["c"] = new BsonDocument { ["_id"] = Id(CustomerId), ["OrganizationId"] = Id(BindHost.Organisation), ["Name"] = "ACME", ["Code"] = "A-1" },
    };

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner, FakeRemoteClient Client) Host()
    {
        var runner = new FakeAggregateRunner { PageRows = [Joined()] };
        var client = new FakeRemoteClient();
        var options = BindHost.Options();

        return (new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options)), runner, client);
    }

    private static async Task<QueryResult> RunAsync(MongoQueryEngine engine, string pipeline)
    {
        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, pipeline), BindHost.Context());

        return outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "").Subject.Result;
    }

    private static async Task<ExplainResult> ExplainAsync(string pipeline, FakeRemoteClient? client = null, string envelope = "")
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), client);
        var body = $$"""{ "query": { "entityType": "{{Invoice}}", "pipeline": {{pipeline}} }{{envelope}} }""";
        var outcome = await engine.ExplainAsync(ExplainAnswer.Envelope(body), BindHost.Context());

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    private static IEnumerable<string> Keys(JsonNode? node) => node!.AsObject().Select(member => member.Key);

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(value => value!.GetValue<string>()).ToList();

    // ---- a join in the aggregate ---------------------------------------------------------------------------

    [Fact]
    public async Task The_join_projects_its_load_set_and_the_row_is_cut_to_the_output_set()
    {
        var (engine, runner, _) = Host();
        var result = await RunAsync(engine, """
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "match": { "c.code": { "eq": "A-1" } } }]
            """);

        var lookup = runner.Calls.Single().Stages.Single(stage => stage.Contains("$lookup"))["$lookup"]["pipeline"].AsBsonArray;

        lookup.Last()["$project"].AsBsonDocument.Names.Should().BeEquivalentTo(["Code", "_id", "Name"], "the key, the hint the whole alias shows, and what the match reads");
        Keys(result.Items.Single()!["c"]).Should().Equal(["id", "name"], "what the match only read is loaded and not shown");
    }

    [Fact]
    public async Task Under_a_projection_the_alias_shows_the_projected_paths_alone()
    {
        var (engine, runner, _) = Host();
        var result = await RunAsync(engine, """
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "project": { "number": 1, "c.code": 1 } }]
            """);

        runner.Calls.Single().Stages.Single(stage => stage.Contains("$lookup"))["$lookup"]["pipeline"].AsBsonArray.Last()["$project"].AsBsonDocument.Names
            .Should().BeEquivalentTo(["Code", "_id"], "the hint is what a whole alias would show: nobody reads it here");
        Keys(result.Items.Single()!["c"]).Should().Equal("code");
    }

    [Fact]
    public async Task An_alias_joined_after_a_projection_is_whole_though_the_projection_does_not_name_it()
    {
        var (engine, _, _) = Host();
        var result = await RunAsync(engine, """
            [{ "project": { "number": 1, "customerId": 1 } },
             { "resolve": { "path": "customerId", "as": "c", "select": ["code"] } }]
            """);

        Keys(result.Items.Single()!["c"]).Should().Equal(["id", "code"], "a projection cannot name a root a later stage adds");
    }

    // ---- a join after the page -----------------------------------------------------------------------------

    [Fact]
    public async Task A_remote_alias_under_a_projection_asks_its_owner_for_the_projected_paths_and_shows_exactly_them()
    {
        var (engine, _, client) = Host();
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice"), ("phones", new[] { new { number = "1", label = "work" } })));

        var result = await RunAsync(engine, """
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["email"] } },
             { "project": { "number": 1, "r.name": 1, "r.phones.number": 1 } }]
            """);

        client.Calls.Single().Request.Queries.Single().Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys
            .Should().BeEquivalentTo(["name", "phones.number", "id"], "the projected paths and the member the rows are keyed by; the hint is not asked for");
        result.Items.Single()!["r"]!.ToJsonString().Should().Be("""{"name":"Alice","phones":[{"number":"1"}]}""",
            "the key the owner query carries is this host's to match by, and an element is cut like its row");
    }

    [Fact]
    public async Task A_remote_alias_kept_whole_without_a_hint_asks_for_the_owners_default_and_shows_what_it_answers()
    {
        var (engine, _, client) = Host();
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice")));

        var result = await RunAsync(engine, """[{ "resolve": { "path": "contactId", "as": "r" } }]""");

        client.Calls.Single().Request.Queries.Single().Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys.Should().BeEquivalentTo(["$default", "id"]);
        Keys(result.Items.Single()!["r"]).Should().Equal("id", "name");
    }

    [Fact]
    public async Task The_plan_does_not_depend_on_the_order_the_projection_names_its_paths_in()
    {
        var (engine, _, client) = Host();
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice"), ("email", "a@b.example")));

        await RunAsync(engine, """[{ "resolve": { "path": "contactId", "as": "r" } }, { "project": { "number": 1, "r.name": 1, "r.email": 1 } }]""");
        await RunAsync(engine, """[{ "resolve": { "path": "contactId", "as": "r" } }, { "project": { "r.email": 1, "number": 1, "r.name": 1 } }]""");

        client.Calls.Should().ContainSingle("the second request names the same paths: one plan, answered from the cache");
    }

    [Fact]
    public void The_plan_hash_takes_a_projections_paths_as_a_set()
    {
        static QueryRequest Query(params string[] fields) => new()
        {
            EntityType = "crm.contact",
            Pipeline =
            [
                new PipelineStage { Project = new ProjectStage { Fields = fields.ToDictionary(field => field, _ => 1) }, Keys = ["project"] },
                new PipelineStage { Page = new PageStage { Limit = 0 }, Keys = ["page"] },
            ],
        };

        OwnerFetchCache.PlanHashOf(Query("name", "email", "id"), probed: false).Should().Be(OwnerFetchCache.PlanHashOf(Query("id", "email", "name"), probed: false));
        OwnerFetchCache.PlanHashOf(Query("name", "id"), probed: false).Should().NotBe(OwnerFetchCache.PlanHashOf(Query("name", "email", "id"), probed: false));
    }

    // ---- at an owner: the continued stage ------------------------------------------------------------------

    [Fact]
    public async Task The_paths_projected_under_a_continued_alias_travel_as_paths_and_the_stage_keeps_its_hint()
    {
        var (engine, _, client) = Host();
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice"), ("co", new { title = "ACME" })));

        var result = await RunAsync(engine, """
            [{ "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["id"] } },
             { "project": { "number": 1, "r.name": 1, "co.title": 1 } }]
            """);
        var sent = client.Calls.Single().Request.Queries.Single();

        sent.Pipeline.Select(stage => stage.Kind).Should().Equal("match", "resolve", "project", "page");
        sent.Pipeline[1].Resolve!.Select.Should().Equal(["id"], "the hint travels as written: the owner binds the stage and infers what its join loads");
        sent.Pipeline[2].Project!.Fields.Keys.Should().BeEquivalentTo(["name", "id", "co.title"], "the paths under the continued alias, as paths, never the alias itself");
        result.Items.Single()!["co"]!.ToJsonString().Should().Be("""{"title":"ACME"}""");
    }

    [Fact]
    public async Task A_continued_alias_kept_whole_is_asked_whole_and_one_the_row_does_not_carry_is_not_asked()
    {
        var (engine, _, client) = Host();
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice"), ("co", new { title = "ACME" })));

        await RunAsync(engine, """[{ "resolve": { "path": "contactId", "as": "r" } }, { "resolve": { "path": "r.companyId", "as": "co" } }]""");
        await RunAsync(engine, """[{ "resolve": { "path": "contactId", "as": "r" } }, { "resolve": { "path": "r.companyId", "as": "co" } }, { "project": { "number": 1, "r": 1 } }]""");

        client.Calls[0].Request.Queries.Single().Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys.Should().Contain("co");
        client.Calls[1].Request.Queries.Single().Pipeline.Single(stage => stage.Project is not null).Project!.Fields.Keys.Should().NotContain(field => field.StartsWith("co", StringComparison.Ordinal));
    }

    // ---- explain ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Explain_answers_what_each_stage_reads_and_what_each_join_loads_shows_and_was_hinted()
    {
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "match": { "c.code": { "eq": "x" } } },
             { "lookup": { "from": "rc.invoice", "path": "customerCode", "on": "c", "as": "same" } },
             { "project": { "number": 1, "c": 1, "same.number": 1 } }]
            """);

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Stage(0).Reads.Select(read => read.ToJsonString()).Should().Equal("""{"path":"customerId","use":"resolveKey"}""");
        result.Stage(1).Reads.Select(read => read.ToJsonString()).Should().Equal("""{"path":"c.code","use":"match","alias":"c"}""");
        result.Stage(2).Reads.Select(read => read.ToJsonString()).Should().Equal("""{"path":"c.code","use":"lookupOn","alias":"c"}""");
        result.Stage(3).Reads.Select(read => read["path"]!.GetValue<string>()).Should().Equal("number", "c", "same.number");

        Strings(result.Alias("c")["loads"]).Should().Equal("code", "id", "name");
        Strings(result.Alias("c")["shows"]).Should().Equal("id", "name");
        Strings(result.Alias("c")["hint"]).Should().Equal("name");
        Strings(result.Alias("same")["shows"]).Should().Equal("number");
        result.Alias("same")["hint"].Should().BeNull();
        result.Result!.Columns.Where(column => column.Root == "c").Select(column => column.Path).Should().Equal(["c.id", "c.name"], "the columns are the output set: what the match read is no column");
    }

    [Fact]
    public async Task What_explain_asks_of_a_shape_never_writes_the_ledger()
    {
        const string Pipeline = """
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "unwind": { "path": "lines", "as": "line" } },
             { "match": { "c.code": { "eq": "x" } } }]
            """;

        var shaped = await ExplainAsync(Pipeline, envelope: """, "include": ["shape", "notes", "docs"], "shape": { "depth": 3 }, "catalog": [{ "id": "c1", "entity": "rc.customer", "referencing": true }] """);
        var plain = await ExplainAsync(Pipeline, envelope: """, "include": [] """);
        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, Pipeline);

        shaped.Types.Count.Should().BePositive("the flags of every member at every stage were asked of the shapes");
        shaped.Stages.SelectMany(stage => stage.Reads).Select(read => read.ToJsonString())
            .Should().Equal(plain.Stages.SelectMany(stage => stage.Reads).Select(read => read.ToJsonString()), "describing a shape reads nothing");
        shaped.Stages.SelectMany(stage => stage.Reads).Should().HaveCount(bound.Reads.Count, "the reads are the binder's, one per read site");
        Strings(shaped.Alias("c")["loads"]).Should().Equal(bound.Loads["c"].Loads);
    }

    [Fact]
    public async Task A_stage_continued_at_an_owner_answers_the_reads_and_the_loads_its_owner_inferred()
    {
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } },
             { "project": { "number": 1, "r.name": 1, "co": 1 } }]
            """, OwnerFleet.Client());

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Stage(1).Reads.Select(read => read.ToJsonString()).Should().Equal(
            ["""{"path":"r.companyId","use":"resolveKey","alias":"r"}"""], "the owner's read of its row, in the origin row's paths");
        Strings(result.Alias("r")["loads"]).Should().Equal("name");
        Strings(result.Alias("co")["loads"]).Should().Equal(["id", "title"], "the owner's join, as the owner inferred it for the run's query");
        Strings(result.Alias("co")["shows"]).Should().Equal("id", "title");
        Strings(result.Alias("co")["hint"]).Should().Equal("title");
    }

    [Fact]
    public async Task A_path_under_a_continued_alias_that_its_target_lacks_is_UNKNOWN_PATH_at_the_projection()
    {
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "r.companyId", "as": "co" } },
             { "project": { "number": 1, "r.name": 1, "co.title": 1, "co.nope": 1 } }]
            """, OwnerFleet.Client());

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Match<QueryValidationError>(error =>
            error.Code == Codes.UnknownPath && error.Stage == 2 && error.Path == "co.nope", "the owner binds the stage and what lies under its alias");
    }

    [Fact]
    public async Task The_columns_of_an_owners_alias_under_a_projection_are_the_projected_paths_and_an_owning_row_always_names_its_entity()
    {
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "localSource.id", "as": "line", "target": "rc.shipment", "parentAs": "owner", "select": ["code"] } },
             { "project": { "number": 1, "line.amount": 1, "owner.number": 1 } }]
            """);

        result.Result!.Columns.Where(column => column.Root == "line").Select(column => column.Path).Should().Equal(["line.amount"], "neither the hint nor the matched member the owner query carries");
        result.Result.Columns.Where(column => column.Root == "owner").Select(column => column.Path).Should().Equal("owner.entity", "owner.number");
        Strings(result.Alias("owner")["loads"]).Should().Equal("number");
        result.Alias("owner")["hint"].Should().BeNull();
    }
}
