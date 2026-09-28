using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Compile;

/// <summary>
/// The internal request member <c>keyedBy</c> (DESIGN §3.5.2 step 3): refused on the public route
/// as a member a request does not have, accepted on an internal call (the internal batch overload
/// of the query service, and this host's own SelfOwner), and compiled to the grouped form — the
/// keys matched first, an item's elements unwound under <c>oxEl</c>, the caller's leading matches,
/// then the window that keeps at most <c>perKey</c> rows per key.
/// </summary>
public class KeyedByCompileTests
{
    private static RequestContext Internal() => BindHost.Context() with { Internal = true };

    private static QueryRequest Request(string entity, string keyedBy, string pipeline) =>
        BindHost.Parse($$"""{ "entityType": "{{entity}}", "keyedBy": {{keyedBy}}, "pipeline": {{pipeline}} }""");

    private static async Task<BoundPipeline> BoundAsync(QueryRequest request, RequestContext context)
    {
        var outcome = await BindHost.BindAsync(ResolveModel.Model, request, context);

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));

        return ((BindOutcome.Bound)outcome).Pipeline;
    }

    private static List<string> StagesOf(BoundPipeline bound) =>
        MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)).PageStages.Select(stage => stage.GetElement(0).Name).ToList();

    [Fact]
    public async Task KeyedBy_on_a_public_request_is_a_member_a_request_does_not_have()
    {
        var refusal = await BindHost.BindAsync(ResolveModel.Model, Request("rc.customer", """{ "path": "code", "keys": ["A"] }""", "[]"));

        var error = refusal.Should().BeOfType<BindOutcome.Failed>().Subject.Refusal.Errors!.Single();

        error.Code.Should().Be(Codes.UnknownRequestMember);
        error.Message.Should().Contain("'keyedBy'").And.Contain(Binder.RequestMembers).And.NotContain("strict, keyedBy");
    }

    [Fact]
    public async Task An_entity_member_is_matched_first_and_numbered_per_key_after_the_callers_filter()
    {
        var bound = await BoundAsync(Request("rc.customer", """{ "path": "code", "keys": ["A", "B"], "perKey": 2 }""",
            """[{ "match": { "name": { "eq": "x" } } }, { "project": { "name": 1, "code": 1 } }, { "page": { "limit": 4 } }]"""), Internal());

        StagesOf(bound).Should().Equal("$match", "$match", "$match", "$setWindowFields", "$match", "$unset", "$project", "$sort", "$limit");

        var stages = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)).PageStages;

        stages[1]["$match"]["Code"]["$in"].AsBsonArray.Select(key => key.AsString).Should().Equal("A", "B");
        stages[3]["$setWindowFields"]["partitionBy"].AsString.Should().Be("$Code");
        stages[3]["$setWindowFields"]["sortBy"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument("_id", 1), "the first row of a key is the one with the lowest record key");
        stages[4]["$match"]["__oxRank"]["$lte"].AsInt32.Should().Be(2);
        JsonNode.Parse(bound.Canonical)!["keyedBy"]!["path"]!.GetValue<string>().Should().Be("Code");
    }

    [Fact]
    public async Task An_item_member_unwinds_the_matching_elements_under_oxEl_where_the_filter_and_the_projection_read_them()
    {
        var bound = await BoundAsync(Request("rc.shipment", """{ "path": "billingLines.id", "keys": ["b0000000-0000-0000-0000-000000000001"] }""",
            """[{ "match": { "oxEl.amount": { "gt": 1 } } }, { "project": { "oxEl.code": 1, "oxEl.id": 1, "$default": 1 } }]"""), Internal());

        var stages = MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000)).PageStages;

        stages[1]["$match"].AsBsonDocument.Names.Should().Equal("BillingLines._id");
        stages[2]["$set"]["oxEl"].AsString.Should().Be("$BillingLines");
        stages[3]["$unwind"].AsString.Should().Be("$oxEl");
        stages[4]["$match"].AsBsonDocument.Names.Should().Equal("oxEl._id");
        stages[5]["$match"].AsBsonDocument.Names.Should().Equal("oxEl.Amount");
        stages[6]["$setWindowFields"]["partitionBy"].AsString.Should().Be("$oxEl._id");
        bound.FinalShape.Roots["oxEl"].Should().BeOfType<ShapeNode.Element>();
        bound.KeyedBy!.PerKey.Should().Be(2, "two rows tell one from several");
    }

    [Theory]
    [InlineData("""{ "path": "billingLines", "keys": ["x"] }""", Codes.InvalidPath)]
    [InlineData("""{ "path": "number", "keys": [] }""", Codes.InvalidOperand)]
    [InlineData("""{ "path": "number", "keys": ["a"], "perKey": 0 }""", Codes.InvalidOperand)]
    [InlineData("""{ "path": "nope", "keys": ["a"] }""", Codes.UnknownPath)]
    public async Task A_keyedBy_that_does_not_name_a_stored_member_and_its_keys_is_refused(string keyedBy, string code)
    {
        var outcome = await BindHost.BindAsync(ResolveModel.Model, Request("rc.shipment", keyedBy, "[]"), Internal());

        outcome.Should().BeOfType<BindOutcome.Failed>().Which.Refusal.Errors!.Select(error => error.Code).Should().Contain(code);
    }

    [Fact]
    public async Task A_request_without_keyedBy_renders_and_fingerprints_as_before()
    {
        var plain = await BoundAsync(BindHost.Request("rc.customer", """[{ "match": { "code": { "eq": "A" } } }]"""), Internal());
        var again = await BoundAsync(BindHost.Request("rc.customer", """[{ "match": { "code": { "eq": "A" } } }]"""), BindHost.Context());

        JsonNode.Parse(plain.Canonical)!.AsObject().ContainsKey("keyedBy").Should().BeFalse();
        plain.Fingerprint.Should().Be(again.Fingerprint, "an internal call alone changes nothing a cursor is bound to");
    }

    // ---- the query service: the internal-call overload ----------------------------------------

    private sealed class Scope : IOxQLScopeProvider
    {
        public ValueTask<Guid?> OrganisationAsync(Microsoft.AspNetCore.Http.HttpContext? httpContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Guid?>(BindHost.Organisation);
    }

    [Fact]
    public async Task Only_the_internal_batch_overload_accepts_keyedBy()
    {
        var options = BindHost.Options(configure => configure.Compat.Enabled = false);
        var models = new StaticEntityModelProvider(ResolveModel.Model);
        var service = new OxQLQueryService(new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, options), new Scope(), options, models);
        var batch = new BatchRequest { Queries = [Request("rc.customer", """{ "path": "code", "keys": ["A"] }""", "[]")] };

        var internalCall = (await service.BatchAsync(batch, internalCall: true)).Should().BeOfType<BatchOutcome.Success>().Subject.Response.Results.Single()!;
        var publicCall = (await service.BatchAsync(batch)).Should().BeOfType<BatchOutcome.Success>().Subject.Response.Results.Single()!;

        internalCall["items"].Should().NotBeNull(internalCall.ToJsonString());
        publicCall["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownRequestMember);
    }
}
