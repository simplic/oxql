using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.AspNetCore;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// T5, the cheap refusals of an explain (improvement plan §3.E protection): each bound refuses
/// before any model, binder or owner work. The binder reads the model first, so a model provider
/// that counts its reads is the binder's call counter; a remote client that counts its calls is the
/// owners'. Every refusal leaves both at 0; an explain within the bounds reads the model.
/// </summary>
public class ExplainLimitsTests
{
    /// <summary>A model provider that counts how often the model is read: the binder's first act.</summary>
    private sealed class CountingModels(EntityModel model) : IEntityModelProvider
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public EntityModel Model
        {
            get
            {
                Interlocked.Increment(ref reads);
                return model;
            }
        }
    }

    private static string Stages(int count) =>
        "[" + string.Join(", ", Enumerable.Range(0, count).Select(_ => """{ "match": { "number": { "exists": true } } }""")) + "]";

    private static string Envelope(string members, int stages = 1) =>
        $$"""{ "query": { "entityType": "{{ResolveModel.Invoice}}", "pipeline": {{Stages(stages)}} }{{members}} }""";

    private static string Catalog(int count) =>
        ", \"catalog\": [" + string.Join(", ", Enumerable.Range(0, count).Select(index => $$"""{ "entity": "rc.customer", "id": "c{{index}}" }""")) + "]";

    /// <summary>Each bound past its default, and what it names.</summary>
    public static TheoryData<string, string, string> PastTheBounds => new()
    {
        { "31 stages", Envelope("", stages: 31), "stages" },
        { "11 catalog entries", Envelope(Catalog(11)), "catalog" },
        { "shape depth 4", Envelope(""", "shape": { "depth": 4 }"""), "shape.depth" },
        { "an unknown include", Envelope(""", "include": ["executionStats"]"""), "include" },
        { "an unknown remote", Envelope(""", "remote": "always" """), "remote" },
    };

    [Theory]
    [MemberData(nameof(PastTheBounds))]
    public async Task An_explain_past_a_bound_is_EXPLAIN_LIMIT_before_the_model_the_binder_or_an_owner_is_touched(string label, string body, string limit)
    {
        var models = new CountingModels(ResolveModel.Model);
        var owners = new FakeRemoteClient();
        var engine = new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), owners);
        var request = JsonSerializer.Deserialize<ExplainRequest>(body, OxQLJson.Wire)!;

        var outcome = await engine.ExplainAsync(request, BindHost.Context());

        var refusal = outcome.Should().BeOfType<ExplainOutcome.Refused>(label).Subject.Refusal;
        refusal.Status.Should().Be(400, label);
        refusal.Errors!.Should().ContainSingle(label).Which.Should().Match<QueryValidationError>(error => error.Code == Codes.ExplainLimit && error.Path == limit);
        models.Reads.Should().Be(0, $"{label}: the binder never ran");
        owners.ExplainCalls.Should().BeEmpty(label);
        owners.Calls.Should().BeEmpty(label);
    }

    [Fact]
    public async Task An_explain_at_every_bound_binds()
    {
        var models = new CountingModels(ResolveModel.Model);
        var options = BindHost.Options(each => each.Limits.MaxPipelineStages = 30);
        var engine = new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, options, new FakeRemoteClient());
        var request = JsonSerializer.Deserialize<ExplainRequest>(Envelope(Catalog(10) + """, "shape": { "depth": 3 }, "include": ["shape", "notes", "plan", "indexes"], "remote": "cached" """, stages: 30), OxQLJson.Wire)!;

        var outcome = await engine.ExplainAsync(request, BindHost.Context(options));

        var result = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => $"{error.Code} {error.Message}")));
        models.Reads.Should().BeGreaterThan(0, "the binder ran");
    }

    [Fact]
    public void Every_bound_crossed_is_one_error_and_the_bounds_are_options()
    {
        var request = JsonSerializer.Deserialize<ExplainRequest>(Envelope(Catalog(3) + """, "shape": { "depth": 2 }, "include": ["executionStats", "indexes"], "remote": "skip" """, stages: 5), OxQLJson.Wire)!;
        var tight = new ExplainOptions { MaxStages = 4, MaxCatalogEntries = 2, MaxShapeDepth = 1 };

        var refusal = ExplainLimits.Check(request, tight)!;

        refusal.Errors!.Select(error => error.Path).Should().Equal("stages", "catalog", "shape.depth", "remote", "include");
        refusal.Errors!.Should().OnlyContain(error => error.Code == Codes.ExplainLimit);
        refusal.Errors![0].Params!["max"].Should().Be(4);
        refusal.Errors![3].Params!["value"].Should().Be("skip");
        refusal.Errors![4].Params!["value"].Should().Be("executionStats");
        ExplainLimits.Check(request, new ExplainOptions { MaxStages = 5, MaxCatalogEntries = 3, MaxShapeDepth = 2 })!.Errors!.Select(error => error.Path)
            .Should().Equal(["remote", "include"], "only the unknown values are left");
    }

    [Fact]
    public async Task At_the_host_a_body_over_Explain_MaxRequestBytes_is_413_and_one_past_a_bound_400_before_the_model_is_read()
    {
        var models = new CountingModels(BindHost.Probe);
        using var host = new SampleHost(configure: services =>
        {
            services.RemoveAll<IEntityModelProvider>();
            services.AddSingleton<IEntityModelProvider>(models);
        });
        var client = host.Client();

        // A warm host: whatever startup reads is read before the requests counted.
        (await client.GetAsync("/OxQL/health")).EnsureSuccessStatusCode();
        var before = models.Reads;

        var padding = new string(' ', host.Options.Explain.MaxRequestBytes);
        var oversized = await client.PostAsync("/OxQL/explain", new StringContent($$"""{ "entityType": "probe.order", "pipeline": [] {{padding}}}""", Encoding.UTF8, "application/json"));

        oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        ((await SampleHost.Body(oversized))!["errors"]![0]!["code"]!.GetValue<string>()).Should().Be(Codes.RequestTooLarge);

        var query = await client.PostAsync("/OxQL/query", new StringContent($$"""{ "entityType": "probe.order", "pipeline": [] {{padding}}}""", Encoding.UTF8, "application/json"));
        query.StatusCode.Should().NotBe(HttpStatusCode.RequestEntityTooLarge, "the query route keeps Limits:MaxRequestBytes");

        var afterQuery = models.Reads;
        var limited = await client.PostAsync("/OxQL/explain", SampleHost.Json($$"""{ "query": { "entityType": "probe.order", "pipeline": {{Stages(31)}} } }"""));

        limited.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ((await SampleHost.Body(limited))!["errors"]![0]!["code"]!.GetValue<string>()).Should().Be(Codes.ExplainLimit);
        models.Reads.Should().Be(afterQuery, "the explain refusals read no model");
        afterQuery.Should().BeGreaterThan(before, "the query was bound, which reads the model: the counter counts");
        host.Runner.Calls.Should().ContainSingle("only the query ran");
    }
}
