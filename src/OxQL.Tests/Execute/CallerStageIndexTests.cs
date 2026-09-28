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
/// A caller stage that binds to no bound stage (an empty <c>match</c>) does not shift the stage
/// index that diagnostics, refusals and explain name (RE-2): every one of them names the caller's
/// stage, inline or keyed.
/// </summary>
public class CallerStageIndexTests
{
    private const string Invoice = ResolveModel.Invoice;
    private static readonly Guid InvoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Customer1 = Guid.Parse("c0000000-0000-0000-0000-000000000001");

    private const string Inline = """[{ "match": {} }, { "resolve": { "path": "customerId", "as": "c", "onMissing": "report" } }, { "page": { "limit": 10 } }]""";
    private const string Keyed = """[{ "match": {} }, { "resolve": { "path": "customerIds", "as": "c", "elements": "first", "onMissing": "report" } }, { "page": { "limit": 10 } }]""";

    private static (MongoQueryEngine Engine, FakeAggregateRunner Runner) Host()
    {
        var runner = new FakeAggregateRunner();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, new FakeRemoteClient(), cache: new OwnerFetchCache(options));

        runner.PageRows =
        [
            new BsonDocument
            {
                ["_id"] = new BsonBinaryData(InvoiceId, GuidRepresentation.Standard),
                ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard),
                ["CustomerId"] = new BsonBinaryData(Customer1, GuidRepresentation.Standard),
                ["CustomerIds"] = new BsonArray { new BsonBinaryData(Customer1, GuidRepresentation.Standard) },
            },
        ];

        return (engine, runner);
    }

    [Fact]
    public async Task The_binder_records_the_callers_index_of_every_bound_stage()
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, Inline);

        bound.Stages.Should().HaveCount(2, "the empty match binds to nothing");
        bound.CallerIndexOf(bound.Stages[0]).Should().Be(1);
        bound.CallerIndexOf(bound.Stages[1]).Should().Be(2);
    }

    [Theory]
    [InlineData(Inline)]
    [InlineData(Keyed)]
    public async Task RESOLVE_MISSING_names_the_callers_stage_after_an_empty_match(string pipeline)
    {
        var (engine, _) = Host();

        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, pipeline), BindHost.Context());

        var result = outcome.Should().BeOfType<QueryOutcome.Success>().Subject.Result;
        result.Diagnostics!.Single(diagnostic => diagnostic.Code == Codes.ResolveMissing).Stage.Should().Be(1);
    }

    [Theory]
    [InlineData(Inline)]
    [InlineData(Keyed)]
    public async Task A_refusal_names_the_callers_stage_after_an_empty_match(string pipeline)
    {
        var (engine, _) = Host();

        var outcome = await engine.ExecuteAsync(BindHost.Request(Invoice, pipeline.Replace("\"report\"", "\"refuse\"")), BindHost.Context());

        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;
        refusal.Errors!.Single(error => error.Code == Codes.ResolveMissing).Stage.Should().Be(1);
    }

    [Theory]
    [InlineData(Inline, "inline")]
    [InlineData(Keyed, "keyed-local")]
    public async Task Explain_places_the_join_at_the_callers_stage_after_an_empty_match(string pipeline, string executor)
    {
        var (engine, _) = Host();

        var explained = await engine.ExplainAsync(BindHost.Request(Invoice, pipeline), BindHost.Context());

        var result = explained.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        result.Steps.Single(step => step.Index == 1).Executor.Should().Be(executor);
        result.Steps.Single(step => step.Index == 0).Executor.Should().BeNull();
        result.Notes!.Where(note => note.Code is Notes.JoinBeforePage or Notes.JoinAfterPage).Should().OnlyContain(note => note.Stage == 1);
    }
}
