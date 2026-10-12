using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
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

    // ---- contract detection ----------------------------------------------------------

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
        using var host = new SampleHost(SampleHost.RewriteFirst);
        host.Runner.PageRows = [];

        await host.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        await host.Client(contract: 1).PostAsync(Query, SampleHost.Json(WireSpelled));

        var lines = host.Logs.Of(OxQLQueryService.CompatLogCategory).ToList();

        lines.Should().HaveCount(2, "one line per contract 1 request, translated or not");
        lines[0].Should().Contain("probe.order").And.Contain("firstLegacyPath=Number").And.Contain("legacyPaths=1").And.Contain("org=" + BindHost.Organisation).And.Contain("correlation=");
        lines[1].Should().Contain("firstLegacyPath=(null)").And.Contain("legacyPaths=0");
    }

    // LEGACY(compat.v1): the three tests below are one again when the legacy pipeline goes (src/docs/LEGACY.md):
    // a v1 join under contract 1 is refused as a legacy stage.
    private const string V1Join = """{ "entityType": "probe.order", "pipeline": [{ "lookup": { "from": "customers", "localPath": "CustomerId", "foreignPath": "id", "as": "customer" } }] }""";

    [Fact]
    public async Task A_v1_join_under_contract_1_is_refused_as_a_legacy_stage_once_the_legacy_pipeline_is_switched_off()
    {
        using var host = new SampleHost(options => options.Compat.Legacy.Enabled = false);

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(V1Join));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.LegacyStageUnsupported);
        host.Logs.Of(OxQLQueryService.CompatLogCategory).Single().Should().Contain("refused=" + Codes.LegacyStageUnsupported);
        host.Runner.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The aggregate of a v1 join, stage for stage: the organisation matched before any stage of the
    /// request and merged into the request's first match (or a match of its own), the join as a
    /// pipelined lookup that asks the organisation of the joined collection too, the default sort on
    /// the key and one row more than the page.
    /// </summary>
    [Fact]
    public async Task A_v1_join_under_contract_1_is_answered_by_the_legacy_pipeline_with_the_aggregate_the_engine_before_sent()
    {
        // The rewrite first: the join is what the rewrite cannot express, and the log says so. By default the reason is 'always'.
        using var host = new SampleHost(SampleHost.RewriteFirst);
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(V1Join));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!.AsObject().Select(member => member.Key).Should().Equal(["items", "pageInfo"], "the envelope is the 1.1 engine's");
        body["items"]!.AsArray().Single()!["_id"]!.GetValue<string>().Should().Be(Id1.ToString(), "the stored row, under its stored names");
        body["pageInfo"]!.ToJsonString().Should().Be("""{"hasNextPage":false}""");

        var organisation = OrganisationCondition;
        var call = host.Runner.Calls.Single();

        call.Entity.Id.Should().Be("probe.order");
        call.Stages.Select(stage => stage.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson })).Should().Equal(
            $$"""{ "$match" : {{organisation}} }""",
            $$"""{ "$match" : { "$and" : [{{organisation}}] } }""",
            $$"""{ "$lookup" : { "from" : "customers", "let" : { "localValue" : "$CustomerId" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$$localValue", "$_id"] } } }, { "$match" : { "$and" : [{{organisation}}] } }], "as" : "customer" } }""",
            """{ "$sort" : { "_id" : { "$numberInt" : "1" } } }""",
            $$"""{ "$limit" : { "$numberInt" : "{{host.Options.Limits.DefaultPageSize + 1}}" } }""");
        call.Options.MaxTimeMs.Should().BeInRange(OxQL.Core.Legacy.LegacyOptions.DefaultMaxTimeMs - 5_000, OxQL.Core.Legacy.LegacyOptions.DefaultMaxTimeMs, "the time ceiling is the legacy pipeline's own, not the contract 2 engine's");

        host.Logs.Of(OxQL.AspNetCore.Legacy.LegacyRoute.LogCategory).Single().Should().Contain("reason=lookup").And.Contain("probe.order");
    }

    /// <summary>The condition on the caller's organisation as the legacy pipeline sends it: the id as a UUID of either subtype and as text.</summary>
    private static string OrganisationCondition =>
        $$"""{ "$or" : [{ "OrganizationId" : { "$eq" : { "$binary" : { "base64" : "{{Convert.ToBase64String(GuidConverter.ToBytes(BindHost.Organisation, GuidRepresentation.Standard))}}", "subType" : "04" } } } }, { "OrganizationId" : { "$eq" : { "$binary" : { "base64" : "{{Convert.ToBase64String(GuidConverter.ToBytes(BindHost.Organisation, GuidRepresentation.CSharpLegacy))}}", "subType" : "03" } } } }, { "OrganizationId" : { "$eq" : "{{BindHost.Organisation}}" } }] }""";

    [Theory]
    [InlineData("")]
    [InlineData("""{ "group": { "by": [{ "path": "Number", "as": "number" }], "fields": { "total": { "count": true } } } }, { "match": { "number": { "eq": "a" } } }""")]
    [InlineData("""{ "unwind": { "path": "Lines" } }, { "match": { "Number": { "eq": "a" } } }""")]
    [InlineData("""{ "sort": [{ "Number": "asc" }] }, { "page": { "limit": 1, "includeTotalCount": true } }, { "match": { "Number": { "eq": "a" } } }""")]
    [InlineData("""{ "project": { "Number": 1 } }""")]
    [InlineData("""{ "page": { "limit": 2, "includeTotalCount": true } }""")]
    public async Task The_legacy_pipeline_matches_the_callers_organisation_before_any_stage_of_the_request(string stages)
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];
        host.Runner.Count = 1;

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json($$"""{ "entityType": "probe.order", "pipeline": [{{stages}}] }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        host.Runner.Calls.Should().NotBeEmpty();

        // The page and the count alike: nothing is read, grouped or cut to a page before the organisation is matched.
        foreach (var call in host.Runner.Calls)
            call.Stages[0].ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson })
                .Should().Be($$"""{ "$match" : {{OrganisationCondition}} }""");
    }

    [Fact]
    public async Task A_join_in_the_contract_2_form_without_the_header_is_not_the_legacy_pipelines()
    {
        using var host = new SampleHost(SampleHost.RewriteFirst);

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(
            """{ "entityType": "probe.customer", "pipeline": [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }] }"""));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.LegacyStageUnsupported);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().Contain("needs the header 'X-OxQL-Contract: 2'");
        host.Runner.Calls.Should().BeEmpty();
        host.Logs.Of(OxQL.AspNetCore.Legacy.LegacyRoute.LogCategory).Should().BeEmpty();
    }

    // LEGACY(compat.v1): the tests from here to the batch section go with the legacy pipeline.

    /// <summary>
    /// Health says of a caller's requests what holds for them. Where the legacy pipeline answers a
    /// caller without the contract header (a host that sets nothing), the capabilities and the limits
    /// are the ones that pipeline honours: the page is read by cursor only, so no <c>page.offset</c>
    /// and no <c>maxOffset</c>. The engine's version and contract and the two limits another host
    /// sizes its batches by stay. With the header, and on a host that answers contract 1 by the
    /// rewrite first, the lists are the contract 2 engine's.
    /// </summary>
    [Fact]
    public async Task Health_lists_for_a_caller_the_legacy_pipeline_answers_what_that_pipeline_honours()
    {
        string[] honoured =
        [
            "maxPageSize", "defaultPageSize", "maxPipelineStages", "maxLookupStages", "maxUnwindStages", "maxGroupFields",
            "maxProjectionFields", "maxRequestBytes", "maxBatchQueries", "regexMaxLength",
        ];

        static List<string> Names(JsonNode? list) => list!.AsArray().Select(name => name!.GetValue<string>()).ToList();

        using var host = new SampleHost(options => options.Limits.MaxBatchQueries = 7);

        // Always on (the default), no header: the legacy pipeline's list.
        foreach (var route in new[] { "/OxQL/health", "/OxQL/health?shallow=true" })
        {
            var bare = (await SampleHost.Body(await host.Client(contract: null).GetAsync(route)))!.AsObject();

            bare.Select(member => member.Key).Should().Equal(["status", "service", "engine", "capabilities", "limits"], route);
            bare["status"]!.GetValue<string>().Should().Be("healthy");
            bare["service"]!.GetValue<string>().Should().Be("oxql");
            Names(bare["capabilities"]).Should().Equal(["batch", "compat.v1"], "no paging by offset, no any, no remote join and no explain of what this pipeline runs");
            bare["limits"]!.AsObject().Select(member => member.Key).Should().Equal(honoured, "the limits the legacy validation and the two routes check");
            bare["limits"]!["maxBatchQueries"]!.GetValue<int>().Should().Be(7, "the value the host runs under");

            // What a host reads of an owner's shallow health, which it asks without the header.
            var owner = RemoteOwnerInfo.FromShallowHealth(bare)!;

            owner.Contract.Should().Be(2);
            owner.EngineVersion.Should().Be(EngineCapabilities.Version);
            owner.MaxBatchQueries.Should().Be(7);
            owner.MaxPageSize.Should().Be(host.Options.Limits.MaxPageSize);
        }

        // Always on, with the header: the contract 2 engine's.
        var current = (await SampleHost.Body(await host.Client(contract: 2).GetAsync("/OxQL/health")))!.AsObject();

        Names(current["capabilities"]).Should().Equal("explain", "compat.v1");
        current["limits"]!.AsObject().Count.Should().Be(48);

        // Always off: the rewrite answers a caller without the header first, and health lists what it does.
        using var rewrite = new SampleHost(options =>
        {
            SampleHost.RewriteFirst(options);
            options.Limits.MaxBatchQueries = 7;
        });
        var listed = (await SampleHost.Body(await rewrite.Client(contract: null).GetAsync("/OxQL/health")))!.AsObject();

        Names(listed["capabilities"]).Should().Equal("batch", "group.page", "page.offset", "any", "explain", "compat.v1");
        listed["limits"]!.AsObject().Count.Should().Be(48);
        listed["limits"]!.AsObject().ContainsKey("maxOffset").Should().BeTrue();

        var named = (await SampleHost.Body(await rewrite.Client(contract: 2).GetAsync("/OxQL/health")))!.AsObject();

        named.ToJsonString().Should().Be(current.ToJsonString(), "the switch changes nothing for a contract 2 caller");

        // The legacy support off: the rewrite alone answers, and its list stands.
        using var off = new SampleHost(options => options.Compat.Legacy.Enabled = false);

        Names((await SampleHost.Body(await off.Client(contract: null).GetAsync("/OxQL/health")))!["capabilities"]).Should().StartWith(["batch", "group.page", "page.offset", "any"]);
    }

    [Fact]
    public async Task A_host_that_sets_nothing_answers_every_contract_1_request_by_the_legacy_pipeline()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        host.Options.Compat.Legacy.Enabled.Should().BeTrue();
        host.Options.Compat.Legacy.Always.Should().BeTrue("a caller without the contract header gets the legacy pipeline's answer");

        // A request the rewrite could express just as well: storage-spelled, a match and a page.
        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!.AsObject().Select(member => member.Key).Should().Equal("items", "pageInfo");
        body["pageInfo"]!.ToJsonString().Should().Be("""{"hasNextPage":false}""");
        host.Logs.Of(OxQL.AspNetCore.Legacy.LegacyRoute.LogCategory).Single().Should().Contain("reason=always").And.Contain("probe.order").And.Contain("org=" + BindHost.Organisation);
        host.Logs.Of(OxQLQueryService.CompatLogCategory).Should().BeEmpty("the rewrite did not run");

        // The aggregate is the 1.1 engine's: the paths as written, the default sort on the key, one row more than the page.
        var stages = host.Runner.Calls.Single().Stages;

        stages[0]["$match"].AsBsonDocument.Contains("$and").Should().BeTrue("the organisation is merged into the request's first match");
        stages[0]["$match"]["$and"][0].ToJson().Should().Be("""{ "Number" : { "$eq" : "a" } }""");
        stages.Skip(1).Select(stage => stage.ToJson()).Should().Equal("""{ "$sort" : { "_id" : 1 } }""", """{ "$limit" : 6 }""");

        // The contract 2 header, and a host without compatibility, never reach it.
        (await host.Client(contract: 2).PostAsync(Query, SampleHost.Json(WireSpelled))).StatusCode.Should().Be(HttpStatusCode.OK);
        host.Logs.Of(OxQL.AspNetCore.Legacy.LegacyRoute.LogCategory).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_legacy_pipeline_runs_under_a_ceiling_of_its_own_which_the_page_and_the_count_share()
    {
        // The contract 2 ceiling is a second here; a contract 1 request has a ceiling of its own and is not held to it.
        using var host = new SampleHost(options => options.Execution.MaxTimeMs = 1_000);
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];
        host.Runner.Count = 1;

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(
            """{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 5, "includeTotalCount": true } }] }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Runner.Calls.Should().HaveCount(2, "the page, then the count");

        var page = host.Runner.Calls[0].Options.MaxTimeMs;
        var count = host.Runner.Calls[1].Options.MaxTimeMs;

        page.Should().BeInRange(OxQL.Core.Legacy.LegacyOptions.DefaultMaxTimeMs - 5_000, OxQL.Core.Legacy.LegacyOptions.DefaultMaxTimeMs, "ten minutes unless the host says otherwise");
        count.Should().BeLessThanOrEqualTo(page, "the count runs under what the page left of the one ceiling");

        // The host's own value, and 0 for the default.
        using var set = new SampleHost(options => options.Compat.Legacy.MaxTimeMs = 90_000);

        await set.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        set.Runner.Calls.Single().Options.MaxTimeMs.Should().BeInRange(85_000, 90_000);

        using var zero = new SampleHost(options => options.Compat.Legacy.MaxTimeMs = 0);

        await zero.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        zero.Runner.Calls.Single().Options.MaxTimeMs.Should().BeGreaterThan(590_000);
    }

    [Fact]
    public async Task A_legacy_request_over_its_ceiling_is_a_timeout_and_one_its_caller_abandons_is_cancelled()
    {
        using var slow = new SampleHost();
        slow.Runner.Fail = new TimeoutException("maxTimeMS");

        var late = await slow.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        var body = await SampleHost.Body(late);

        late.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout, body?.ToJsonString());
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.QueryTimeout);

        // The caller gives up while the aggregate runs: the database's command is cancelled with the request.
        var waiting = new WaitingRunner();
        using var host = new SampleHost(configure: services =>
        {
            services.RemoveAll<OxQL.Mongo.IAggregateRunner>();
            services.AddSingleton<OxQL.Mongo.IAggregateRunner>(waiting);
        });
        using var caller = new CancellationTokenSource();
        var request = host.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled), caller.Token);

        (await Task.WhenAny(waiting.Started.Task, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(waiting.Started.Task, "the aggregate was sent");
        caller.Cancel();

        (await Task.WhenAny(waiting.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(waiting.Cancelled.Task, "the request's cancellation reaches the aggregate");
        await FluentActions.Awaiting(() => request).Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>A runner whose aggregate ends only when its request is cancelled.</summary>
    private sealed class WaitingRunner : OxQL.Mongo.IAggregateRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(OxQL.Model.EntityDef entity, IReadOnlyList<BsonDocument> stages, OxQL.Mongo.AggregateRunOptions options, CancellationToken cancellationToken)
        {
            Started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return [];
        }
    }

    [Fact]
    public async Task A_legacy_refusal_repeats_the_status_in_its_body_as_the_engine_before_did_and_no_other_refusal_does()
    {
        using var host = new SampleHost();

        var legacy = await host.Client(contract: null).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.nothing", "pipeline": [] }"""));
        var text = await legacy.Content.ReadAsStringAsync();

        legacy.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Be("""{"type":"validation_error","title":"Query validation failed.","status":400,"errors":[{"code":"UNKNOWN_TYPE","message":"No OxQL type registration found for entity type 'probe.nothing'."}]}""");

        var current = await host.Client(contract: 2).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.nothing", "pipeline": [] }"""));

        (await SampleHost.Body(current))!.AsObject().ContainsKey("status").Should().BeFalse("the status of a contract 2 refusal is the response's");

        using var rewrite = new SampleHost(SampleHost.RewriteFirst);
        var rewritten = await rewrite.Client(contract: null).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.nothing", "pipeline": [] }"""));

        (await SampleHost.Body(rewritten))!.AsObject().ContainsKey("status").Should().BeFalse("nor has the rewrite's");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("$expr")]
    [InlineData("Owner..Id")]
    public async Task A_host_that_names_no_member_for_the_organisation_reads_nothing_under_contract_1(string path)
    {
        using var host = new SampleHost(options => options.Compat.Legacy.OrganisationPath = path);
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body?.ToJsonString());
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.AccessDenied);
        host.Runner.Calls.Should().BeEmpty("nothing is sent to the database without the organisation's condition");
    }

    [Fact]
    public async Task The_legacy_line_keeps_a_callers_entity_on_one_bounded_line()
    {
        using var host = new SampleHost();

        await host.Client(contract: null).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.order\r\n2026-01-01 forged line", "pipeline": [] }"""));

        var line = host.Logs.Of(OxQL.AspNetCore.Legacy.LegacyRoute.LogCategory).Single();

        line.Should().NotContain("\r").And.NotContain("\n");
        line.Should().Contain("refused=UNKNOWN_TYPE");
    }

    [Fact]
    public async Task The_legacy_line_is_written_for_a_request_the_pipeline_faults_on_too()
    {
        using var host = new SampleHost();
        host.Runner.Fail = new InvalidDataException("a row no reader takes");

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(StorageSpelled));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        host.Logs.Of(OxQL.AspNetCore.Legacy.LegacyRoute.LogCategory).Single().Should().Contain("reason=always").And.Contain("refused=INTERNAL_ERROR").And.Contain("probe.order");
    }

    [Fact]
    public async Task An_aggregates_argument_under_the_legacy_pipeline_reads_a_member_and_takes_a_variable_as_its_text()
    {
        using var host = new SampleHost();

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(
            """{ "entityType": "probe.order", "variables": { "rate": 2 }, "pipeline": [{ "group": { "by": [{ "path": "Number", "as": "number" }], "fields": { "total": { "sum": { "multiply": [{ "path": "Count" }, { "$var": "rate" }] } } } } }] }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Runner.Calls.Single().Stages.Single(stage => stage.Contains("$group"))["$group"]["total"].ToJson().Should().Be("""{ "$sum" : { "$multiply" : ["$Count", "2"] } }""");
    }

    [Theory]
    [InlineData("""{ "regex": { "$var": "pattern" } }""")]
    [InlineData("""{ "eq": { "$regex": "PATTERN" } }""")]
    [InlineData("""{ "in": ["a", { "$regex": "PATTERN" }] }""")]
    [InlineData("""{ "regex": "PATTERN" }""")]
    public async Task A_pattern_over_the_limit_is_refused_however_a_legacy_request_writes_it(string condition)
    {
        using var host = new SampleHost(options => options.Limits.RegexMaxLength = 8);
        const string Long = "^abcdefgh";

        var request = $$"""{ "entityType": "probe.order", "variables": { "pattern": "{{Long}}" }, "pipeline": [{ "match": { "Number": {{condition.Replace("PATTERN", Long)}} } }] }""";
        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(request));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body?.ToJsonString());
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.RegexTooLong);
        body["errors"]![0]!["message"]!.GetValue<string>().Should().Be("Regex pattern exceeds maximum length of 8.");
        host.Runner.Calls.Should().BeEmpty();

        // A pattern within the limit runs, whichever way it is written.
        var within = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(request.Replace(Long, "^abc")));

        within.StatusCode.Should().Be(HttpStatusCode.OK, await within.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_member_written_twice_is_read_as_its_last_under_the_legacy_pipeline()
    {
        using var host = new SampleHost();

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(
            """{ "entityType": "probe.customer", "EntityType": "probe.order", "pipeline": [{ "page": { "limit": 3, "Limit": 7 } }] }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var call = host.Runner.Calls.Single();

        call.Entity.Id.Should().Be("probe.order");
        call.Stages[^1].ToJson().Should().Be("""{ "$limit" : 8 }""");
    }

    [Fact]
    public async Task The_allowed_lookup_sources_are_compared_without_regard_to_case_whatever_set_the_host_assigns()
    {
        using var host = new SampleHost(options => options.Compat.Legacy.AllowedLookupSources = ["Customers"]);

        async Task<HttpResponseMessage> Joining(string from) => await host.Client(contract: null).PostAsync(Query, SampleHost.Json(
            $$"""{ "entityType": "probe.order", "pipeline": [{ "lookup": { "from": "{{from}}", "localPath": "CustomerId", "foreignPath": "id", "as": "c" } }] }"""));

        (await Joining("customers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SampleHost.Body(await Joining("orders")))!["errors"]![0]!["code"]!.GetValue<string>().Should().Be("DISALLOWED_LOOKUP_SOURCE");
    }

    // ---- batch --------------------------------------------------------------------------

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

    // ---- health, explain -----------------------------------------------------------

    [Fact]
    public async Task Health_publishes_the_contract_and_the_capabilities_of_this_host()
    {
        using var host = new SampleHost();

        // A contract 2 caller; what a caller without the contract header reads is held by Contract1HttpGoldenTests.
        var response = await host.Client(contract: 2).GetAsync("/OxQL/health");
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!["status"]!.GetValue<string>().Should().Be("healthy");
        body["engine"]!["contract"]!.GetValue<int>().Should().Be(2);
        body["engine"]!["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var capabilities = body["capabilities"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();

        capabilities.Should().Equal(["explain", "compat.v1"], "explain is on by default, and only what depends on the host is listed");
        capabilities.Should().NotContain(capability => capability.StartsWith("oxql.", StringComparison.Ordinal), "engine.contract 2 is the marker of this package, not a capability");

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
        limits["explainTimeoutMs"]!.GetValue<int>().Should().Be(2_000);
        limits["explainMaxOwnerServices"]!.GetValue<int>().Should().Be(4);
        limits["explainMaxOwnerCalls"]!.GetValue<int>().Should().Be(8);
        limits["explainMaxBatchChecks"]!.GetValue<int>().Should().Be(64);
        limits["explainMaxAnswerBytes"]!.GetValue<int>().Should().Be(262_144);
        limits["explainMaxTypeMembers"]!.GetValue<int>().Should().Be(300);
        limits["explainDefaultShapeDepth"]!.GetValue<int>().Should().Be(2);
        limits["explainRatePerMinute"]!.GetValue<int>().Should().Be(120);
        limits["explainRateBurst"]!.GetValue<int>().Should().Be(30);
        limits["explainOwnerRatePerMinute"]!.GetValue<int>().Should().Be(20);
        limits["explainOwnerRateBurst"]!.GetValue<int>().Should().Be(5);
        limits["explainMaxConcurrentPerUser"]!.GetValue<int>().Should().Be(2);
        limits["explainMaxConcurrentPerHost"]!.GetValue<int>().Should().Be(8);
        limits["explainMaxConcurrentPerCaller"]!.GetValue<int>().Should().Be(4);
        limits.ContainsKey("maxDescribeChildren").Should().BeFalse("describe is gone");
        limits["negativeResolveTtlSeconds"]!.GetValue<int>().Should().Be(10);
        limits["explainRemoteTimeoutMs"]!.GetValue<int>().Should().Be(1_500);
        limits["explainMaxRequestBytes"]!.GetValue<int>().Should().Be(65_536);
        limits["explainMaxStages"]!.GetValue<int>().Should().Be(30);
        limits["explainMaxCatalogEntries"]!.GetValue<int>().Should().Be(10);
        limits["explainMaxShapeDepth"]!.GetValue<int>().Should().Be(3);
        limits["maxValuesLimit"]!.GetValue<int>().Should().Be(500);
        limits["valuesScanRows"]!.GetValue<int>().Should().Be(50_000);
        limits["valuesDefaultLimit"]!.GetValue<int>().Should().Be(100);
        limits["maxFilterEntries"]!.GetValue<int>().Should().Be(64);
        limits.Count.Should().Be(48,"health publishes every limit the engine enforces, not only the ones the document carries");
        capabilities.Should().NotContain(["resolve.remote", "semiJoin", "resolve.chain"], "the Sample host installs no remote query client");
    }

    [Fact]
    public async Task Explain_is_404_when_switched_off_and_carries_the_advisory_only_when_asked()
    {
        var request = """{ "entityType": "probe.order", "pipeline": [{ "match": { "number": { "eq": "a" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "count": true } }] }""";

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

        plain.AsObject().ContainsKey("plan").Should().BeFalse("the plan is opt-in");

        var enabled = await host.Client().PostAsync("/OxQL/explain", SampleHost.Json($$"""{ "query": {{request}}, "include": ["plan", "indexes"] }"""));
        var body = await SampleHost.Body(enabled);

        enabled.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["plan"]!["bound"]!["entity"]!.GetValue<string>().Should().Be("probe.order");
        body["plan"]!["stages"]!.AsArray().Should().NotBeEmpty();
        body["plan"]!["count"]!.AsArray().Last()!.AsObject().ContainsKey("$count").Should().BeTrue();
        body["advisory"]!["existing"]!.GetValue<string>().Should().Be("known");
        host.Runner.Calls.Should().BeEmpty("explain never executes");
        host.Indexes.Asked.Should().Equal(["probe.order"], "the index list of the entity alone: no join");

        // One index for the request: the organisation, the number it compares and orders by, the key. The
        // collection's index holds the first two without the request's collation and without the key.
        var advised = body["advisory"]!["indexes"]!.AsArray().Single()!.AsObject();

        advised["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"Number":1,"_id":1}""");
        advised["collation"]!.ToJsonString().Should().Be("""{"locale":"de","strength":1}""");
        advised["existing"]!.GetValue<string>().Should().Be("partial");
        advised["used"]!.GetValue<bool>().Should().BeFalse();
        advised["index"]!.GetValue<string>().Should().Be("org_number");
        advised["lacks"]!.ToJsonString().Should().Be("""["equality","sort","collation"]""");
        advised["serves"]!.AsArray().Select(serve => serve!["use"]!.GetValue<string>()).Should().Equal("filter", "sort");

        var note = body["notes"]!.AsArray().Select(node => node!.AsObject()).Single(each => each["code"]!.GetValue<string>() == "INDEX_ADVICE");

        note["basis"]!.GetValue<string>().Should().Be("advisory");
        note["stage"]!.GetValue<int>().Should().Be(0);
        note["path"]!.GetValue<string>().Should().Be("number");
        note["params"]!["id"]!.GetValue<string>().Should().Be(advised["id"]!.GetValue<string>());
    }

    // ---- scope provider ----------------------------------------------------------------

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
