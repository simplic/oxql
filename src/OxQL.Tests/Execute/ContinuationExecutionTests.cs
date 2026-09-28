using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// Remote continuation at run time (DESIGN §3.5.2 steps 3 and 6, §3.5.3, §3.5.4) against fakes: the
/// continued stages ride in the ordinary owner query between the filter and the projection, the
/// aliases they add come back on the owner rows and are lifted to the origin row, <c>forTarget</c>
/// leaves other targets' rows <c>not_applicable</c> without refusing a strict request, an owner's
/// refusal maps back to the caller's stage and path, an owner on an older engine is
/// <c>OWNER_NOT_CAPABLE</c>, a chain runs under the chain ceiling with <c>strict</c> in the body, and
/// a chain through this host's own <see cref="SelfOwner"/> over cyclic data ends.
/// </summary>
public class ContinuationExecutionTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static readonly Guid InvoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ContactId = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");

    /// <summary>Answers each aggregate with the rows of its entity, whatever the pipeline, and records every call.</summary>
    private sealed class EntityRunner : IAggregateRunner
    {
        public Dictionary<string, List<BsonDocument>> Rows { get; } = new(StringComparer.Ordinal);

        public List<(EntityDef Entity, IReadOnlyList<BsonDocument> Stages)> Calls { get; } = [];

        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((entity, stages));

            return Task.FromResult<IReadOnlyList<BsonDocument>>(Rows.TryGetValue(entity.Id, out var rows) ? rows.Select(row => row.DeepClone().AsBsonDocument).ToList() : []);
        }
    }

    private static (MongoQueryEngine Engine, EntityRunner Runner, FakeRemoteClient Client) Host()
    {
        var runner = new EntityRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        return (engine, runner, client);
    }

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonDocument InvoiceRow(Guid id, Action<BsonDocument> fill)
    {
        var row = new BsonDocument { ["_id"] = Id(id), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "RE-" + id.ToString()[^1] };

        fill(row);

        return row;
    }

    private static async Task<QueryOutcome> RunAsync(MongoQueryEngine engine, string pipeline, bool strict = false) =>
        await engine.ExecuteAsync(BindHost.Request(Invoice, pipeline) with { Strict = strict ? true : null }, BindHost.Context());

    private static QueryResult Succeeded(QueryOutcome outcome)
    {
        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static Refusal RefusedWith(QueryOutcome outcome) => outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

    private const string ContactChain = """
        [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
         { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } },
         { "page": { "limit": 10 } }]
        """;

    private static JsonObject ContactRow() =>
        FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice"), ("co", new { title = "ACME" }));

    // ---- the owner query ------------------------------------------------------------------------------

    [Fact]
    public async Task The_continued_stages_ride_in_the_owner_query_between_the_filter_and_the_projection_and_come_back_lifted()
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(ContactRow());

        var result = Succeeded(await RunAsync(engine, ContactChain, strict: true));

        var sent = client.Calls.Should().ContainSingle("one owner query carries the chain").Subject.Request.Queries.Should().ContainSingle().Subject;
        sent.Pipeline.Select(stage => stage.Kind).Should().Equal("match", "resolve", "project", "page");
        sent.Pipeline[1].Resolve!.Path.Should().Be("companyId", "'r.' is the owner's row");
        sent.Pipeline[1].Resolve!.As.Should().Be("co");
        sent.Pipeline[2].Project!.Fields.Keys.Should().BeEquivalentTo(["name", "id", "co"], "the continued alias is projected beside the select and the key");
        sent.Strict.Should().BeTrue("strict travels in the body of a query that carries a chain");
        sent.Variables.Should().BeNull("an owner never receives variables");

        var row = result.Items.Should().ContainSingle().Subject!;
        row["r"]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "name"], "what the continued stage added is not the owner row's");
        row["co"]!["title"]!.GetValue<string>().Should().Be("ACME");
    }

    [Fact]
    public async Task A_query_without_continued_stages_is_sent_as_before_strict_or_not()
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(FakeRemoteClient.Row("id", ContactId.ToString(), ("name", "Alice")));

        Succeeded(await RunAsync(engine, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "page": { "limit": 10 } }]""", strict: true));

        var (_, request, budget) = client.Calls.Should().ContainSingle().Subject;
        request.Queries.Single().Strict.Should().BeNull("without a chain the owner has nothing strict would change");
        budget.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(BindHost.Options().Execution.EffectiveResolveTimeoutMs));
    }

    [Fact]
    public async Task A_batch_carrying_a_chain_runs_under_the_chain_ceiling_less_a_margin_not_the_resolve_one()
    {
        var (engine, runner, client) = Host();
        var options = BindHost.Options();
        runner.Rows[Invoice] = [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(ContactRow());

        Succeeded(await RunAsync(engine, ContactChain));

        var (_, request, budget) = client.Calls.Single();
        budget.Should().BeGreaterThan(TimeSpan.FromMilliseconds(options.Execution.EffectiveResolveTimeoutMs))
            .And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(options.Execution.EffectiveChainTimeoutMs));
        request.MaxTimeMs.Should().Be((int)budget.TotalMilliseconds, "the owner applies it as its ceiling and budgets its own owners from it");
    }

    // ---- forTarget -------------------------------------------------------------------------------------

    private const string ForAlpha = """
        [{ "resolve": { "path": "source.id", "as": "src" } },
         { "lookup": { "from": "ct.note", "path": "alphaId", "on": "src", "forTarget": "ct.alpha", "as": "note", "first": true } },
         { "sort": [{ "number": "asc" }] }]
        """;

    private static void SeedUnion(ChainHost host)
    {
        host.Runner.Rows[ChainModel.Invoice] =
        [
            ChainModel.InvoiceRow(ChainModel.Invoice1, "RE-1", row => row["Source"] = new BsonDocument { ["Type"] = "a", ["_id"] = Id(ChainModel.Alpha1) }),
            ChainModel.InvoiceRow(ChainModel.Invoice2, "RE-2", row => row["Source"] = new BsonDocument { ["Type"] = "b", ["_id"] = Id(ChainModel.Beta1) }),
        ];
        host.Runner.Rows["ct.alpha"] = [ChainModel.Row(ChainModel.Alpha1, "Alpha", note: new BsonDocument { ["_id"] = Id(ChainModel.Note1), ["OrganizationId"] = Id(BindHost.Organisation), ["Text"] = "hello" })];
        host.Runner.Rows["ct.beta"] = [ChainModel.Row(ChainModel.Beta1, "Beta")];
    }

    [Fact]
    public async Task A_forTarget_stage_goes_to_its_targets_owner_only_and_a_row_resolved_to_another_target_is_null_without_refusing_strict()
    {
        var host = ChainHost.Start();
        SeedUnion(host);

        var result = Succeeded(await host.RunAsync(ForAlpha, strict: true));

        result.Items[0]!["src"]!["name"]!.GetValue<string>().Should().Be("Alpha");
        result.Items[0]!["note"]!["text"]!.GetValue<string>().Should().Be("hello");
        result.Items[0]!["src"]!.AsObject().ContainsKey("note").Should().BeFalse("the lifted alias is not the owner row's");
        result.Items[1]!["src"]!["name"]!.GetValue<string>().Should().Be("Beta");
        result.Items[1]!["note"].Should().BeNull("not applicable to a beta row, which loses nothing");
        result.Diagnostics.Should().BeNull();

        host.Runner.Calls.Single(call => call.Entity.Id == "ct.alpha").Stages.Should().Contain(stage => stage.Contains("$lookup"), "the alpha's owner runs the lookup");
        host.Runner.Calls.Single(call => call.Entity.Id == "ct.beta").Stages.Should().NotContain(stage => stage.Contains("$lookup"), "the beta's owner is never sent it");
    }

    [Fact]
    public async Task A_forTarget_row_on_another_target_is_the_outcome_not_applicable_which_nothing_reports_or_refuses()
    {
        var host = ChainHost.Start();
        SeedUnion(host);
        var bound = ((BindOutcome.Bound)await new Binder(ChainModel.Model, BindHost.Cursors).BindAsync(BindHost.Request(ChainModel.Invoice, ForAlpha), BindHost.Context(), CancellationToken.None)).Pipeline;
        var compiled = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));
        var fetch = new KeyedFetch(host.Client, host.Engine, new OwnerFetchCache(BindHost.Options()), BindHost.Options());

        var resolved = await fetch.ByKeysAsync(compiled, host.Runner.Rows[ChainModel.Invoice], BindHost.Context(), TimeSpan.FromSeconds(5), strict: true, CancellationToken.None);

        resolved.Outcomes.Should().ContainSingle().Which.Should().Be(new KeyedRowOutcome(1, "note", 1, null, ChainModel.Beta1.ToString(), KeyedOutcome.NotApplicable));
        OutcomePolicy.WireName(KeyedOutcome.NotApplicable).Should().Be("not_applicable");
        OutcomePolicy.IsMissing(KeyedOutcome.NotApplicable).Should().BeFalse();

        var report = OutcomePolicy.Report(bound, resolved.Outcomes, resolved.Truncations, strict: true, BindHost.Options());
        report.Diagnostics.Should().BeEmpty();
        report.Refusing.Should().BeEmpty();
    }

    // ---- refusals -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_refusal_of_a_continued_stage_maps_back_to_the_callers_stage_and_path_with_where_the_owner_saw_it()
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Refused("UNKNOWN_PATH", "'companyId' is not a path of crm.contact.", Stage: 1, Path: "companyId");

        var refusal = RefusedWith(await RunAsync(engine, ContactChain));

        refusal.Status.Should().Be(422);
        refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveRefused, Codes.UnknownPath);
        refusal.Errors![0].Stage.Should().Be(1, "the head points at the continued stage the owner refused");

        var mapped = refusal.Errors[1];
        mapped.Stage.Should().Be(1);
        mapped.Path.Should().Be("r.companyId");

        var owner = (IReadOnlyDictionary<string, object?>)mapped.Params!["owner"]!;
        owner["service"].Should().Be("crm");
        owner["entity"].Should().Be("crm.contact");
        owner["target"].Should().Be("crm.contact");
        owner["stage"].Should().Be(1);
        owner["path"].Should().Be("companyId");
    }

    [Fact]
    public async Task An_owner_refusal_of_its_own_key_match_stays_at_the_resolve_stage()
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))];
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Refused("UNKNOWN_PATH", "no", Stage: 0, Path: "id");

        var refusal = RefusedWith(await RunAsync(engine, ContactChain));

        refusal.Errors![0].Stage.Should().Be(0);
        refusal.Errors[1].Params.Should().BeNull();
    }

    [Theory]
    [InlineData("2.0.126.924", true)]
    [InlineData("2.0.3-beta", true)]
    [InlineData("2.1.0.0", false)]
    [InlineData("chaos-owner", false)]
    public async Task An_owner_whose_health_reports_an_engine_before_2_1_is_OWNER_NOT_CAPABLE_before_anything_is_sent(string version, bool refused)
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(InvoiceId, row => row["ContactId"] = Id(ContactId))];
        client.Owners["crm"] = new RemoteOwnerInfo(version, 2, null);
        client.Script = (_, _, _) => new FakeRemoteClient.Answer.Rows(ContactRow());

        var outcome = await RunAsync(engine, ContactChain);

        if (!refused)
        {
            Succeeded(outcome);
            client.Calls.Should().ContainSingle("an owner on 2.1, or one whose version cannot be read, is sent the chain");
            return;
        }

        var error = RefusedWith(outcome).Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(Codes.OwnerNotCapable);
        error.Stage.Should().Be(1, "the continued stage is what needs 2.1");
        error.Message.Should().Be($"crm runs OxQL {version}; this stage needs 2.1.");
        client.Calls.Should().BeEmpty();

        // A plain resolve is still sent to that owner: it is 2.0 vocabulary.
        Succeeded(await RunAsync(engine, """[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]"""));
    }

    // ---- termination -----------------------------------------------------------------------------------

    /// <summary>
    /// A chain through this host's own <see cref="SelfOwner"/> over data that cycles (an invoice whose
    /// next invoice is itself): the origin forwards two join stages, the first owner level one, the
    /// second none, so the chain ends after three owner levels whatever the data, and what the deepest
    /// level added is lifted through every level to the origin row.
    /// </summary>
    [Fact]
    public async Task A_chain_over_cyclic_data_ends_because_every_forwarded_query_has_fewer_join_stages()
    {
        var host = ChainHost.Start();
        host.Runner.Rows[ChainModel.Invoice] = [ChainModel.InvoiceRow(ChainModel.Invoice1, "RE-1", row => row["NextKey"] = ChainModel.Invoice1.ToString())];

        var result = Succeeded(await host.RunAsync("""
            [{ "resolve": { "path": "nextKey", "as": "n1", "select": ["number"] } },
             { "resolve": { "path": "n1.nextKey", "as": "n2", "select": ["number"] } },
             { "resolve": { "path": "n2.nextKey", "as": "n3", "select": ["number"] } }]
            """));

        host.Runner.Calls.Should().HaveCount(4, "the origin, then one owner level per keyed stage along the chain, and no more although the data cycles");
        host.Runner.Calls.Select(call => call.Stages.Count(stage => stage.Contains("$lookup"))).Should().OnlyContain(joins => joins == 0, "every level's own join is a keyed fetch, not a $lookup");
        host.Client.Calls.Should().BeEmpty();

        var row = result.Items.Should().ContainSingle().Subject!;
        row["n1"]!.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(["id", "number"], "what continued stages added is not the owner row's");
        row["n2"]!["number"]!.GetValue<string>().Should().Be("RE-1");
        row["n3"]!["number"]!.GetValue<string>().Should().Be("RE-1", "added two levels down and lifted through both");
    }

    /// <summary>A host over <see cref="ChainModel"/>: the engine, a runner answering per entity, and a fake remote client.</summary>
    private sealed record ChainHost(MongoQueryEngine Engine, EntityRunner Runner, FakeRemoteClient Client)
    {
        public static ChainHost Start()
        {
            var runner = new EntityRunner();
            var client = new FakeRemoteClient();
            var options = BindHost.Options();

            return new ChainHost(new MongoQueryEngine(new StaticEntityModelProvider(ChainModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options)), runner, client);
        }

        public async Task<QueryOutcome> RunAsync(string pipeline, bool strict = false) =>
            await Engine.ExecuteAsync(BindHost.Request(ChainModel.Invoice, pipeline) with { Strict = strict ? true : null }, BindHost.Context());
    }
}

/// <summary>
/// The graph of the local-owner continuation cases: an invoice whose next invoice is keyed by a
/// converted key (so it runs through the keyed fetch and this host's <see cref="SelfOwner"/>), and a
/// typed source onto two entity targets, one of which notes reference.
/// </summary>
internal static class ChainModel
{
    public const string Invoice = "ct.invoice";

    public static readonly Guid Invoice1 = Guid.Parse("10000000-0000-0000-0000-0000000000a1");
    public static readonly Guid Invoice2 = Guid.Parse("10000000-0000-0000-0000-0000000000a2");
    public static readonly Guid Alpha1 = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Beta1 = Guid.Parse("be000000-0000-0000-0000-000000000001");
    public static readonly Guid Note1 = Guid.Parse("40000000-0000-0000-0000-000000000001");

    private static readonly Lazy<EntityModel> model = new(() => ClrModelBuilder.Build(
    new EntityDeclaration[]
    {
        new(Invoice, Invoice, typeof(CtInvoice), "invoices", null, false),
        new("ct.alpha", "ct.alpha", typeof(CtAlpha), "alphas", null, false),
        new("ct.beta", "ct.beta", typeof(CtBeta), "betas", null, false),
        new("ct.note", "ct.note", typeof(CtNote), "notes", null, false),
    }, retiredIds: null, new ReferenceDeclarations()));

    public static EntityModel Model => model.Value;

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    public static BsonDocument InvoiceRow(Guid id, string number, Action<BsonDocument> fill)
    {
        var row = new BsonDocument { ["_id"] = Id(id), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = number };

        fill(row);

        return row;
    }

    /// <summary>A target row; <paramref name="note"/> stands for what the owner's lookup of notes left under <c>note</c>.</summary>
    public static BsonDocument Row(Guid id, string name, BsonDocument? note = null)
    {
        var row = new BsonDocument { ["_id"] = Id(id), ["OrganizationId"] = Id(BindHost.Organisation), ["Name"] = name };

        if (note is not null)
            row["note"] = note;

        return row;
    }
}

public class CtInvoice
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = "";

    [OxQLReference("ct.invoice", KeyAs = OxQLKeyAs.Guid)]
    public string? NextKey { get; set; }

    public CtSource? Source { get; set; }
}

public class CtSource
{
    public string Type { get; set; } = "";

    [OxQLReferenceWhen("type", "a", "ct.alpha")]
    [OxQLReferenceWhen("type", "b", "ct.beta")]
    public Guid Id { get; set; }
}

public class CtAlpha
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
}

public class CtBeta
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
}

public class CtNote
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Text { get; set; } = "";

    [OxQLReference("ct.alpha")]
    public Guid AlphaId { get; set; }
}
