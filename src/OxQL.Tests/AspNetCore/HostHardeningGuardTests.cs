using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The boundary that guarantees the shape of an answer: whatever throws below the query service
/// reaches the caller as a coded 500 envelope and the log as one line, both naming the same
/// correlation id, and the caller's own cancellation is not mistaken for a fault.
/// </summary>
public class HostHardeningGuardTests
{
    private const string Correlation = "corr-7f3a";
    private const string Secret = "connection string of the definitions store";
    private const string FaultCategory = "OxQL.AspNetCore.OxQLQueryService";

    private const string ReadsAddons = """{ "entityType": "probe.order", "pipeline": [{ "match": { "addon.weight": { "gte": 1 } } }] }""";
    // An entity without an addon bag: binding it never asks the definitions store.
    private const string Plain = """{ "entityType": "probe.customer", "pipeline": [{ "page": { "limit": 1 } }] }""";

    /// <summary>Organisation and correlation id from headers, as a host's provider reads them.</summary>
    private sealed class HeaderScope : IOxQLScopeProvider
    {
        public ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Guid?>(BindHost.Organisation);

        public string? CorrelationId(HttpContext? httpContext) => Correlation;
    }

    /// <summary>A definitions store that fails, which the binder meets on the first addon path.</summary>
    private sealed class FailingAddons(Func<CancellationToken, Exception> failure) : IAddonDefinitionSource
    {
        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
            throw failure(cancellationToken);
    }

    /// <summary>A model that cannot be read, which the engine meets before it binds.</summary>
    private sealed class FailingModel : IEntityModelProvider
    {
        public EntityModel Model => throw new InvalidDataException(Secret);
    }

    private static SampleHost HostWith(Action<IServiceCollection> faults) => new(
        options => options.Explain.Enabled = true,
        services =>
        {
            services.RemoveAll<IOxQLScopeProvider>();
            services.AddScoped<IOxQLScopeProvider, HeaderScope>();
            faults(services);
        });

    private static void FailingBind(IServiceCollection services) =>
        services.AddSingleton<IAddonDefinitionSource>(new FailingAddons(_ => new InvalidDataException(Secret)));

    private static void AssertFaultLine(SampleHost host, int lines = 1)
    {
        var faults = host.Logs.Entries.Where(entry => entry.Category == FaultCategory && entry.Level == LogLevel.Error).ToList();

        faults.Should().HaveCount(lines);
        faults.Should().OnlyContain(entry => entry.Message.Contains(Correlation), "the line is found by the id the response names");
    }

    private static void AssertEnvelope(System.Text.Json.Nodes.JsonNode? envelope)
    {
        envelope!["type"]!.GetValue<string>().Should().Be("internal_error");
        envelope["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.InternalError);
        envelope["errors"]![0]!["message"]!.GetValue<string>().Should().Contain(Correlation, "the message promises a correlation id, so it carries it");
        envelope.ToJsonString().Should().NotContain(Secret, "the detail stays in the log");
    }

    [Fact]
    public async Task A_fault_while_binding_is_a_coded_500_and_one_log_line_under_the_same_correlation_id()
    {
        using var host = HostWith(FailingBind);

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(ReadsAddons));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertEnvelope(await SampleHost.Body(response));
        AssertFaultLine(host);
        host.Runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fault_in_the_engine_before_it_binds_is_a_coded_500()
    {
        using var host = HostWith(services =>
        {
            services.RemoveAll<IEntityModelProvider>();
            services.AddSingleton<IEntityModelProvider, FailingModel>();
        });

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(Plain));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertEnvelope(await SampleHost.Body(response));
        AssertFaultLine(host);
    }

    [Fact]
    public async Task A_fault_in_the_contract_1_rewrite_is_a_coded_500()
    {
        using var host = HostWith(services =>
        {
            services.RemoveAll<IEntityModelProvider>();
            services.AddSingleton<IEntityModelProvider, FailingModel>();
        });

        var response = await host.Client(contract: null).PostAsync("/OxQL/query", SampleHost.Json(Plain));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertEnvelope(await SampleHost.Body(response));
        AssertFaultLine(host);
    }

    [Fact]
    public async Task A_fault_in_explain_is_a_coded_500()
    {
        using var host = HostWith(FailingBind);

        var response = await host.Client().PostAsync("/OxQL/explain", SampleHost.Json(ReadsAddons));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertEnvelope(await SampleHost.Body(response));
        AssertFaultLine(host);
    }

    [Fact]
    public async Task A_fault_in_one_batch_entry_is_that_entrys_envelope_and_the_others_are_served()
    {
        using var host = HostWith(FailingBind);
        host.Runner.PageRows = [SampleHost.Row(Guid.NewGuid(), "a")];

        var response = await host.Client().PostAsync("/OxQL/batch", SampleHost.Json($$"""{ "queries": [{{ReadsAddons}}, {{Plain}}, {{ReadsAddons}}] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a batch answers per query");
        AssertEnvelope(body!["results"]![0]);
        body["results"]![1]!["items"]!.AsArray().Should().ContainSingle();
        AssertEnvelope(body["results"]![2]);
        AssertFaultLine(host, lines: 2);
    }

    private static (OxQLQueryService Service, LogCapture Logs) Service(IAddonDefinitionSource addons)
    {
        var options = BindHost.Options();
        var models = new StaticEntityModelProvider(BindHost.Probe);
        var engine = new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, options);
        var logs = new LogCapture();

        return (new OxQLQueryService(engine, new HeaderScope(), options, models, addons, null, LoggerFactory.Create(logging => logging.AddProvider(logs))), logs);
    }

    [Fact]
    public async Task The_callers_own_cancellation_travels_on_and_is_not_logged_as_a_fault()
    {
        using var caller = new CancellationTokenSource();

        var (service, logs) = Service(new FailingAddons(token =>
        {
            caller.Cancel();
            return new OperationCanceledException(token);
        }));

        var request = BindHost.Parse(ReadsAddons);

        await FluentActions.Awaiting(() => service.ExecuteAsync(request, caller.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => service.ExplainAsync(request, caller.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => service.BatchAsync(new() { Queries = [request] }, caller.Token)).Should().ThrowAsync<OperationCanceledException>();

        logs.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_is_a_fault_like_any_other()
    {
        var (service, logs) = Service(new FailingAddons(_ => new OperationCanceledException("the definitions store timed out")));

        var outcome = await service.ExecuteAsync(BindHost.Parse(ReadsAddons), CancellationToken.None);

        var refusal = outcome.Should().BeOfType<QueryOutcome.Refused>().Subject.Refusal;

        refusal.Status.Should().Be(500);
        refusal.Errors![0].Code.Should().Be(Codes.InternalError);
        refusal.Errors[0].Message.Should().Contain(Correlation);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error).Which.Message.Should().Contain(Correlation);

        var batch = await service.BatchAsync(new() { Queries = [BindHost.Parse(ReadsAddons)] }, CancellationToken.None);

        batch.Should().BeOfType<BatchOutcome.Success>("the entry carries the envelope; the batch itself is answered");
    }

    [Fact]
    public async Task A_scope_provider_that_fails_still_yields_the_envelope_under_the_trace_identifier()
    {
        using var host = new SampleHost(configure: services =>
        {
            services.RemoveAll<IOxQLScopeProvider>();
            services.AddScoped<IOxQLScopeProvider, BrokenScope>();
        });

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(Plain));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.InternalError);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().NotContain("''", "the trace identifier stands in when the provider cannot name a correlation id");
        host.Logs.Entries.Should().ContainSingle(entry => entry.Category == FaultCategory && entry.Level == LogLevel.Error);
    }

    private sealed class BrokenScope : IOxQLScopeProvider
    {
        public ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken) => throw new InvalidDataException(Secret);

        public string? CorrelationId(HttpContext? httpContext) => throw new InvalidDataException(Secret);
    }
}
