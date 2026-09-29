using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The host surface through the Sample host: contract detection, the compat log line, the
/// batch shape, health, explain gating and the advisory, the scope-provider requirement, and
/// the body cap.
/// </summary>
public class HostTests
{
    private const string Query = "/OxQL/query";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string StorageSpelled = """{ "entityType": "probe.order", "pipeline": [{ "match": { "Number": { "eq": "a" } } }, { "page": { "limit": 5 } }] }""";
    private const string WireSpelled = """{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "a" } } }, { "page": { "limit": 5 } }] }""";

    // ---- contract detection (§12) ----------------------------------------------------------

    [Fact]
    public async Task Without_the_header_a_request_is_contract_1_while_compat_is_on()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());

        var row = body!["items"]![0]!.AsObject();

        row.ContainsKey("_id").Should().BeTrue("contract 1 rows carry the storage names");
        row.ContainsKey("Number").Should().BeTrue();
        row.ContainsKey("number").Should().BeFalse();
        row["_id"]!.GetValue<string>().Should().Be(Id1.ToString());
    }

    [Fact]
    public async Task With_the_header_a_request_is_contract_2_and_storage_spelling_is_unknown()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        var refused = await host.Client(contract: 2).PostAsync(Query, SampleHost.Json(StorageSpelled));
        var refusal = await SampleHost.Body(refused);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refusal!["type"]!.GetValue<string>().Should().Be("validation_error");
        refusal["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownPath);
        refusal["errors"]![0]!["path"]!.GetValue<string>().Should().Be("Number");
        refusal.AsObject().ContainsKey("status").Should().BeFalse("the envelope carries no status member");

        var response = await host.Client(contract: 2).PostAsync(Query, SampleHost.Json(WireSpelled));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());

        var row = body!["items"]![0]!.AsObject();

        row["id"]!.GetValue<string>().Should().Be(Id1.ToString());
        row["number"]!.GetValue<string>().Should().Be("a");
        row.ContainsKey("_id").Should().BeFalse();
    }

    [Fact]
    public async Task With_compat_off_every_request_is_contract_2_header_or_not()
    {
        using var host = new SampleHost(options => options.Compat.Enabled = false);
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        foreach (var contract in new int?[] { null, 1 })
        {
            var refused = await host.Client(contract).PostAsync(Query, SampleHost.Json(StorageSpelled));

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"contract header {contract?.ToString() ?? "absent"}");
        }

        host.Logs.Of(OxQLQueryService.CompatLogCategory).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_compat_request_is_logged_with_the_first_legacy_path_and_the_caller()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [];

        await host.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        await host.Client(contract: 1).PostAsync(Query, SampleHost.Json(WireSpelled));

        var lines = host.Logs.Of(OxQLQueryService.CompatLogCategory).ToList();

        lines.Should().HaveCount(2, "one line per contract 1 request, translated or not");
        lines[0].Should().Contain("probe.order").And.Contain("firstLegacyPath=Number").And.Contain("legacyPaths=1").And.Contain("org=" + BindHost.Organisation).And.Contain("correlation=");
        lines[1].Should().Contain("firstLegacyPath=(null)").And.Contain("legacyPaths=0");
    }

    [Fact]
    public async Task A_v1_join_under_contract_1_is_refused_as_a_legacy_stage()
    {
        using var host = new SampleHost();

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(
            """{ "entityType": "probe.order", "pipeline": [{ "lookup": { "from": "customers", "localPath": "CustomerId", "foreignPath": "id", "as": "customer" } }] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.LegacyStageUnsupported);
        host.Logs.Of(OxQLQueryService.CompatLogCategory).Single().Should().Contain("refused=" + Codes.LegacyStageUnsupported);
    }

    // ---- batch (§7) --------------------------------------------------------------------------

    [Fact]
    public async Task A_batch_answers_every_query_in_order_under_the_batch_time_ceiling()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a"), SampleHost.Row(Id2, "b")];

        var response = await host.Client().PostAsync("/OxQL/batch", SampleHost.Json("""
            {
              "maxTimeMs": 250,
              "queries": [
                { "entityType": "probe.order", "pipeline": [{ "page": { "limit": 5 } }] },
                { "entityType": "probe.order", "pipeline": [{ "match": { "nope": { "eq": 1 } } }] },
                { "entityType": "probe.nope", "pipeline": [] }
              ]
            }
            """));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());

        var results = body!["results"]!.AsArray();

        results.Should().HaveCount(3);
        results[0]!["items"]!.AsArray().Should().HaveCount(2);
        results[0]!["pageInfo"]!["hasNextPage"]!.GetValue<bool>().Should().BeFalse();
        results[1]!["type"]!.GetValue<string>().Should().Be("validation_error");
        results[1]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownPath);
        results[2]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownEntity);
        host.Runner.Calls.Should().ContainSingle().Which.Options.MaxTimeMs.Should().Be(250, "the batch ceiling caps every aggregate");
    }

    [Fact]
    public async Task A_batch_beyond_the_cap_is_refused_whole()
    {
        using var host = new SampleHost(options => options.Limits.MaxBatchQueries = 1);

        var response = await host.Client().PostAsync("/OxQL/batch", SampleHost.Json(
            """{ "queries": [{ "entityType": "probe.order", "pipeline": [] }, { "entityType": "probe.order", "pipeline": [] }] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.BatchTooLarge);
        host.Runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_body_above_the_cap_is_413_before_it_is_read()
    {
        using var host = new SampleHost(options => options.Limits.MaxRequestBytes = 64);

        var response = await host.Client().PostAsync(Query, SampleHost.Json(WireSpelled + new string(' ', 200)));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.RequestTooLarge);
        host.Runner.Calls.Should().BeEmpty();
    }

    // ---- health, explain (§9, §12) -----------------------------------------------------------

    [Fact]
    public async Task Health_publishes_the_contract_and_the_capabilities_of_this_host()
    {
        using var host = new SampleHost();

        var response = await host.CreateClient().GetAsync("/OxQL/health");
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!["status"]!.GetValue<string>().Should().Be("healthy");
        body["engine"]!["contract"]!.GetValue<int>().Should().Be(2);
        body["engine"]!["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var capabilities = body["capabilities"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();

        capabilities.Should().Contain(["batch", "group.page", "page.offset", "any", "oxql.2.1", "unwind.keepPath", "explain", "compat.v1"], "explain is on by default and 2.1 is announced");

        var limits = body["limits"]!.AsObject();

        limits["maxOffset"]!.GetValue<int>().Should().Be(5_000);
        limits["countCap"]!.GetValue<int>().Should().Be(100_000);
        limits["maxSemiJoinIds"]!.GetValue<int>().Should().Be(5_000, "the cap stays within the offset range so every page of ids is reachable");
        limits["maxLookupLimit"]!.GetValue<int>().Should().Be(100);
        limits["maxResolveStages"]!.GetValue<int>().Should().Be(8);
        limits["maxResolveKeys"]!.GetValue<int>().Should().Be(10_000);
        limits["maxFlattenDepth"]!.GetValue<int>().Should().Be(5);
        limits["maxContinuedStages"]!.GetValue<int>().Should().Be(8);
        limits["maxReportPageSize"]!.GetValue<int>().Should().Be(5_000);
        limits["maxReportedRows"]!.GetValue<int>().Should().Be(50);
        limits["chainTimeoutMs"]!.GetValue<int>().Should().Be(6_000);
        limits["negativeResolveTtlSeconds"]!.GetValue<int>().Should().Be(10);
        limits["explainRemoteTimeoutMs"]!.GetValue<int>().Should().Be(1_500);
        limits["maxDescribeChildren"]!.GetValue<int>().Should().Be(500);
        limits["maxDescribeRequests"]!.GetValue<int>().Should().Be(10);
        limits.Count.Should().Be(28, "health publishes every limit the engine enforces, not only the ones the document carries");
        capabilities.Should().NotContain(["resolve.remote", "semiJoin", "resolve.chain"], "the Sample host installs no remote query client");
    }

    [Fact]
    public async Task Explain_is_404_when_switched_off_and_carries_the_advisory_only_when_asked()
    {
        var request = """{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "a" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }] }""";

        using (var disabled = new SampleHost(options => options.Explain.Enabled = false))
        {
            var response = await disabled.Client().PostAsync("/OxQL/explain", SampleHost.Json(request));

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            disabled.Indexes.IndexCalls.Should().Be(0);
        }

        using var host = new SampleHost();
        host.Indexes.IndexDocuments =
        [
            new BsonDocument { ["name"] = "_id_", ["key"] = new BsonDocument("_id", 1) },
            new BsonDocument { ["name"] = "org_number", ["key"] = new BsonDocument { ["OrganizationId"] = 1, ["Number"] = 1 } },
        ];

        var plain = await SampleHost.Body(await host.Client().PostAsync("/OxQL/explain", SampleHost.Json(request)));

        plain!.AsObject().ContainsKey("advisory").Should().BeFalse("the advisory is opt-in");
        host.Indexes.IndexCalls.Should().Be(0);

        var enabled = await host.Client().PostAsync("/OxQL/explain", SampleHost.Json($$"""{ "query": {{request}}, "include": ["indexes"] }"""));
        var body = await SampleHost.Body(enabled);

        enabled.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["bound"]!["entity"]!.GetValue<string>().Should().Be("probe.order");
        body["stages"]!.AsArray().Should().NotBeEmpty();
        body["count"]!.AsArray().Last()!.AsObject().ContainsKey("$count").Should().BeTrue();
        host.Runner.Calls.Should().BeEmpty("explain never executes");
        host.Indexes.Asked.Should().Equal(["probe.order"], "the index list of the entity alone: no join");

        var advisory = body["advisory"]!.AsArray().Select(node => node!.AsObject()).ToList();

        advisory.Single(entry => entry["field"]!.GetValue<string>() == "OrganizationId")["used"]!.GetValue<bool>().Should().BeTrue();
        advisory.Single(entry => entry["field"]!.GetValue<string>() == "Number")["index"]!.GetValue<string>().Should().Be("org_number");
        advisory.Single(entry => entry["field"]!.GetValue<string>().StartsWith("sort:"))["note"]!.GetValue<string>().Should().Contain("tie-breaker");
    }

    // ---- scope provider (§10) ----------------------------------------------------------------

    [Fact]
    public void A_host_without_a_scope_provider_refuses_to_start()
    {
        using var host = new SampleHost(configure: services => services.RemoveAll<IOxQLScopeProvider>());

        var act = () => host.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*IOxQLScopeProvider*");
    }

    [Fact]
    public async Task The_scope_provider_decides_the_organisation_of_every_query()
    {
        using var host = new SampleHost();
        var other = Guid.NewGuid();

        await host.Client(organisation: other).PostAsync(Query, SampleHost.Json(WireSpelled));

        var scope = host.Runner.Calls.Single().Stages[0]["$match"].AsBsonDocument;

        scope["OrganizationId"].Should().Be(new BsonBinaryData(other, GuidRepresentation.Standard));
    }

    // ---- the request boundary: nothing leaves as a bare 500 ---------------------------------

    /// <summary>
    /// Four batteries each provoked a bare HTTP 500 from a malformed or unusual request, in
    /// four different areas, while <c>INTERNAL_ERROR</c> was published, switched on by the
    /// client and never emitted by the engine. The four causes are fixed at their sites; this
    /// is the guard that covers the fifth, because a 500 with no envelope is reported by the
    /// client as "the service could not be reached", which sends people to the wrong layer.
    /// </summary>
    [Fact]
    public async Task An_escaped_exception_is_a_coded_internal_error_refusal_not_a_bare_500()
    {
        using var host = new SampleHost();
        host.Runner.Fail = new InvalidOperationException("boom");

        var response = await host.Client().PostAsync(Query, SampleHost.Json(WireSpelled));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        body!["type"]!.GetValue<string>().Should().Be("internal_error");
        body["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.InternalError);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().NotBeEmpty();
        host.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("internal_error", StringComparison.Ordinal),
            "the fault is logged whether or not the detail travels");
    }

    /// <summary>
    /// R5. <c>{"queries":[null]}</c> answered 500 through <c>ArgumentNullException</c>, while
    /// every neighbouring malformed shape correctly answered a coded 400.
    /// </summary>
    [Fact]
    public async Task R5_a_null_query_in_a_batch_is_refused_with_a_code()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/batch", SampleHost.Json("""{ "queries": [null] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a batch answers per query");
        body!["results"]![0]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownStage);
        body["results"]![0]!["type"]!.GetValue<string>().Should().Be("validation_error");
    }

    /// <summary>R5. A null pipeline element is a caller error too, not a fault.</summary>
    [Fact]
    public async Task R5_a_null_pipeline_element_is_refused_with_a_code()
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.order", "pipeline": [null] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body?.ToJsonString());
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownStage);
    }
}
