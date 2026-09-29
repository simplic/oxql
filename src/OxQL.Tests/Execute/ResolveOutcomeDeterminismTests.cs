using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// What strict and <c>onMissing</c> detect never depends on the page, the cache or the executor
/// (PRE-2, PRE-2b, RE-4): an inline resolve onto a member that is not the target's key joins two
/// records and flags the second; a filtered inline resolve tells an excluded record from a missing
/// one in the aggregate, so strict binds what a lenient request binds; a remote resolve onto a
/// member that is not the key is grouped per key once it reads its outcomes, and the cache keeps
/// the second row it saw.
/// </summary>
public class ResolveOutcomeDeterminismTests
{
    private const string Invoice = ResolveModel.Invoice;
    private static readonly Guid InvoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static BsonDocument InvoiceRow(Action<BsonDocument> fill)
    {
        var row = new BsonDocument
        {
            ["_id"] = new BsonBinaryData(InvoiceId, GuidRepresentation.Standard),
            ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
            ["Number"] = "RE-1",
        };

        fill(row);

        return row;
    }

    private static async Task<CompiledQuery> CompileAsync(string pipeline, bool strict = false)
    {
        var request = BindHost.Request(Invoice, pipeline) with { Strict = strict ? true : null };
        var bound = ((BindOutcome.Bound)await BindHost.BindAsync(ResolveModel.Model, request)).Pipeline;

        return MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));
    }

    private static BsonDocument LookupOf(CompiledQuery compiled, string alias) =>
        compiled.PageStages.First(stage => stage.Contains("$lookup") && stage["$lookup"]["as"].AsString.StartsWith(alias, StringComparison.Ordinal))["$lookup"].AsBsonDocument;

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner) Engine()
    {
        var runner = new FakeAggregateRunner();
        var options = BindHost.Options();

        return (new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, new FakeRemoteClient(), cache: new OwnerFetchCache(options)), runner);
    }

    // ---- inline onto a member that is not the key (PRE-2) --------------------------------------

    [Fact]
    public async Task An_inline_resolve_onto_a_non_key_member_reading_its_outcomes_joins_two_records_first_by_key_and_flags_the_second()
    {
        var compiled = await CompileAsync("""[{ "resolve": { "path": "customerCode", "as": "c", "onMissing": "report" } }]""");
        var join = LookupOf(compiled, "c")["pipeline"].AsBsonArray.Select(stage => stage.AsBsonDocument).ToList();

        join.Should().Contain(stage => stage.Contains("$sort") && stage["$sort"].AsBsonDocument == new BsonDocument("_id", 1));
        join.Single(stage => stage.Contains("$limit"))["$limit"].AsInt32.Should().Be(2);

        var probe = compiled.InlineProbes.Should().ContainSingle().Subject;
        probe.AmbiguityFlag.Should().NotBeNull();
        compiled.PageStages.Should().Contain(stage => stage.Contains("$set") && stage["$set"].AsBsonDocument.Contains(probe.AmbiguityFlag!));
    }

    [Theory]
    [InlineData("""[{ "resolve": { "path": "customerId", "as": "c", "onMissing": "report" } }]""", false)]
    [InlineData("""[{ "resolve": { "path": "customerCode", "as": "c" } }]""", false)]
    [InlineData("""[{ "resolve": { "path": "customerCode", "as": "c", "onMissing": "null" } }]""", true)]
    public async Task A_key_target_or_a_request_that_reads_no_outcome_joins_one_record_as_2_0_did(string pipeline, bool strict)
    {
        var compiled = await CompileAsync(pipeline, strict: false);

        LookupOf(compiled, "c")["pipeline"].AsBsonArray.Single(stage => stage.AsBsonDocument.Contains("$limit"))["$limit"].AsInt32.Should().Be(1);
        compiled.InlineProbes.Where(probe => probe.AmbiguityFlag is not null).Should().BeEmpty();

        if (strict)
            (await CompileAsync(pipeline, strict: true)).InlineProbes.Should().ContainSingle("a strict request refuses an ambiguous key under onMissing null too")
                .Which.AmbiguityFlag.Should().NotBeNull();
    }

    [Fact]
    public async Task A_flagged_row_is_RESOLVE_AMBIGUOUS_and_refused_under_strict_and_the_flag_never_reaches_the_row()
    {
        var (engine, runner) = Engine();
        var compiled = await CompileAsync("""[{ "resolve": { "path": "customerCode", "as": "c", "onMissing": "report" } }]""");
        var flag = compiled.InlineProbes.Single().AmbiguityFlag!;

        runner.PageRows =
        [
            InvoiceRow(row =>
            {
                row["CustomerCode"] = "ACME";
                row["c"] = new BsonDocument { ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard), ["Name"] = "Acme 1" };
                row[flag] = true;
            }),
        ];

        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "customerCode", "as": "c", "onMissing": "report" } }]"""), BindHost.Context());
        var result = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result;

        var ambiguous = result.Diagnostics!.Should().ContainSingle().Subject;
        ambiguous.Code.Should().Be(Codes.ResolveAmbiguous);
        ambiguous.Stage.Should().Be(0);
        result.Items[0]!.AsObject().Select(member => member.Key).Should().NotContain(key => key.StartsWith(Aliases.ReservedPrefix, StringComparison.Ordinal));

        runner.PageRows[0][flag] = true;
        var strict = await engine.ExecuteAsync(BindHost.Request(Invoice, """[{ "resolve": { "path": "customerCode", "as": "c", "onMissing": "report" } }]""") with { Strict = true }, BindHost.Context());

        strict.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors!.Select(error => error.Code).Should().Contain(Codes.ResolveAmbiguous);
    }

    // ---- a filtered inline resolve under onMissing (RE-4) --------------------------------------

    private const string Filtered = """[{ "resolve": { "path": "customerId", "as": "c", "filter": { "name": { "eq": "Alice" } }, "onMissing": "report" } }, { "sort": [{ "c.name": "asc" }] }]""";

    [Fact]
    public async Task Strict_or_onMissing_leave_a_filtered_local_resolve_inline_filterable_and_sortable_with_the_same_fingerprint()
    {
        var lenient = await BindHost.BoundAsync(ResolveModel.Model, Invoice, """[{ "resolve": { "path": "customerId", "as": "c", "filter": { "name": { "eq": "Alice" } } } }, { "sort": [{ "c.name": "asc" }] }]""");
        var strict = ((BindOutcome.Bound)await BindHost.BindAsync(ResolveModel.Model,
            BindHost.Request(Invoice, """[{ "resolve": { "path": "customerId", "as": "c", "filter": { "name": { "eq": "Alice" } } } }, { "sort": [{ "c.name": "asc" }] }]""") with { Strict = true })).Pipeline;
        var reported = await BindHost.BoundAsync(ResolveModel.Model, Invoice, Filtered);

        foreach (var bound in new[] { lenient, strict, reported })
            bound.Stages.OfType<BoundStage.Resolve>().Single().Executor.Should().Be(ResolveExecutor.Inline);

        strict.Fingerprint.Should().Be(lenient.Fingerprint, "strict changes what refuses, never what binds (DESIGN §3.0)");
    }

    [Fact]
    public async Task A_filtered_inline_resolve_reading_its_outcomes_joins_the_target_again_without_the_filter()
    {
        var compiled = await CompileAsync(Filtered);
        var probe = compiled.InlineProbes.Should().ContainSingle().Subject;

        probe.ExistsFlag.Should().NotBeNull();
        probe.AmbiguityFlag.Should().BeNull("the target field is the key");

        var joins = compiled.PageStages.Where(stage => stage.Contains("$lookup")).Select(stage => stage["$lookup"].AsBsonDocument).ToList();
        joins.Should().HaveCount(2);
        joins[1]["pipeline"].AsBsonArray.Should().NotContain(stage => stage.AsBsonDocument.Contains("$match") && stage.AsBsonDocument["$match"].AsBsonDocument.Contains("Name"), "the existence join has no filter");
        compiled.PageStages.Should().Contain(stage => stage.Contains("$set") && stage["$set"].AsBsonDocument.Contains(probe.ExistsFlag!));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_record_the_filter_excluded_is_not_missing_and_one_that_does_not_exist_is(bool exists, bool missing)
    {
        var (engine, runner) = Engine();
        var flag = (await CompileAsync(Filtered)).InlineProbes.Single().ExistsFlag!;

        runner.PageRows = [InvoiceRow(row =>
        {
            row["CustomerId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
            row[flag] = exists;
        })];

        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, Filtered), BindHost.Context());
        var result = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result;

        (result.Diagnostics?.Any(diagnostic => diagnostic.Code == Codes.ResolveMissing) ?? false).Should().Be(missing);
    }

    // ---- remote onto a member that is not the key (PRE-2b) --------------------------------------

    private static readonly Guid Order1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Order2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static BsonDocument OrderRow(Guid id, string contact) => new()
    {
        ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
        ["Number"] = "o",
        ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
        ["ContactNumber"] = contact,
        ["VehicleId"] = BsonNull.Value,
    };

    [Theory]
    [InlineData(""", "onMissing": "report" """, false)]
    [InlineData("", true)]
    public async Task A_remote_resolve_onto_a_non_key_member_reading_its_outcomes_is_grouped_and_its_ambiguity_is_the_same_cold_and_warm(string onMissing, bool strict)
    {
        var runner = new FakeAggregateRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        // The owner holds two contacts under c1 and one under c2; a page of one row per key would
        // cut one of them, a grouped query answers two rows under c1.
        runner.PageRows = [OrderRow(Order1, "c1"), OrderRow(Order2, "c2")];
        client.Script = (_, query, _) => new FakeRemoteClient.Answer.Rows(
            FakeRemoteClient.Row("number", "c1", ("name", "First")),
            FakeRemoteClient.Row("number", "c1", ("name", "Second")),
            FakeRemoteClient.Row("number", "c2", ("name", "Other")));

        var pipeline = $$"""[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"]{{onMissing}} } }, { "page": { "limit": 10 } }]""";

        async Task<QueryOutcome> Run() => await engine.ExecuteAsync(BindHost.Request("probe.order", pipeline) with { Strict = strict ? true : null }, BindHost.Context());

        var cold = await Run();
        var warm = await Run();

        client.Calls.Should().ContainSingle("the warm request is answered from the cache");
        var sent = client.Calls[0].Request.Queries.Single();
        sent.KeyedBy.Should().NotBeNull("the query is grouped per key");
        sent.KeyedBy!.PerKey.Should().Be(KeyedFetch.PerKey);

        foreach (var outcome in new[] { cold, warm })
        {
            if (strict)
            {
                outcome.Should().BeOfType<QueryOutcome.Refused>().Which.Refusal.Errors!.Select(error => error.Code).Should().Contain(Codes.ResolveAmbiguous);
                continue;
            }

            var result = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result;
            result.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Equal(Codes.ResolveAmbiguous);
            result.Items[0]!["contact"]!["name"]!.GetValue<string>().Should().Be("First");
        }
    }

    [Fact]
    public async Task An_owner_before_2_1_is_asked_the_plain_query_with_two_rows_per_key_instead_of_keyedBy()
    {
        var runner = new FakeAggregateRunner { PageRows = [OrderRow(Order1, "c1"), OrderRow(Order2, "c2")] };
        var client = new FakeRemoteClient();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        client.Owners["crm"] = new RemoteOwnerInfo("2.0.126.924", 2, null);
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(
            FakeRemoteClient.Row("number", "c1", ("name", "First")),
            FakeRemoteClient.Row("number", "c1", ("name", "Second")),
            FakeRemoteClient.Row("number", "c2", ("name", "Other")));

        var outcome = await engine.ExecuteAsync(BindHost.Request("probe.order", """[{ "resolve": { "path": "contactNumber", "as": "contact", "select": ["name"], "onMissing": "report" } }]"""), BindHost.Context());

        var sent = client.Calls.Single().Request.Queries.Single();
        sent.KeyedBy.Should().BeNull("an owner before 2.1 takes no keyedBy");
        sent.Pipeline.Single(stage => stage.Page is not null).Page!.Limit.Should().Be(4, "two rows per key, so a second row arrives or the answer has a next page");
        outcome.Should().BeOfType<QueryOutcome.Success>().Which.Result.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Equal(Codes.ResolveAmbiguous);
    }

    [Fact]
    public async Task Explain_shows_the_plain_query_a_run_sends_an_owner_before_2_1()
    {
        var client = new FakeRemoteClient();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        client.Owners["crm"] = new RemoteOwnerInfo("2.0.126.924", 2, null);

        var explained = await engine.ExplainAsync(BindHost.Request("probe.order", """[{ "resolve": { "path": "contactNumber", "as": "contact", "onMissing": "report" } }]"""), BindHost.Context());

        var owner = explained.Should().BeOfType<ExplainOutcome.Success>().Subject.Result.Steps.Single(step => step.Index == 0).Owner!;
        owner["targets"]![0]!["grouped"]!.GetValue<bool>().Should().BeFalse("the run asks a 2.0 owner the plain query, and explain shows what the run sends");
        owner["query"]!.AsObject().ContainsKey("keyedBy").Should().BeFalse();
    }

    [Fact]
    public async Task A_plain_2_0_resolve_onto_a_non_key_member_keeps_the_plain_query()
    {
        var runner = new FakeAggregateRunner { PageRows = [OrderRow(Order1, "c1")] };
        var client = new FakeRemoteClient();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        await engine.ExecuteAsync(BindHost.Request("probe.order", """[{ "resolve": { "path": "contactNumber", "as": "contact" } }]"""), BindHost.Context());

        client.Calls.Single().Request.Queries.Single().KeyedBy.Should().BeNull("a 2.0 request reads no outcome, and its owner may be on 2.0");
    }
}
