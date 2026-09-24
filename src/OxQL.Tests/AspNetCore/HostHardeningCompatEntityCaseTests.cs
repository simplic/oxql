using System.Net;
using FluentAssertions;
using OxQL.AspNetCore.Compat;
using OxQL.Core.Binding;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// A contract 1 caller may spell the entity id in any case: the compat rewrite folds it to the
/// lower-case form the model holds, retired ids included, before anything resolves it. Under
/// contract 2 the id is matched exactly, as the query syntax states.
/// </summary>
public class HostHardeningCompatEntityCaseTests
{
    private const string Query = "/OxQL/query";

    private const string Order = "probe.order";

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string MixedCase = """{ "entityType": "PROBE.Order", "pipeline": [{ "match": { "Number": { "eq": "a" } } }, { "page": { "limit": 5 } }] }""";
    private const string RetiredMixedCase = """{ "entityType": "Probe.ORDERS_OLD", "pipeline": [{ "page": { "limit": 5 } }] }""";

    [Fact]
    public async Task A_mixed_case_id_without_the_contract_header_answers_the_entity()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(MixedCase));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["items"]!.AsArray().Should().HaveCount(1);
        body["items"]![0]!["Number"]!.GetValue<string>().Should().Be("a", "the rows come back in the v1 encoding");
        host.Runner.Calls.Should().ContainSingle().Which.Entity.Id.Should().Be(Order);
    }

    [Fact]
    public async Task A_mixed_case_id_under_contract_2_is_unknown()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        var response = await host.Client(contract: 2).PostAsync(Query, SampleHost.Json(MixedCase));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body?.ToJsonString());
        body!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownEntity);
        host.Runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mixed_case_retired_id_without_the_contract_header_answers_the_current_entity()
    {
        using var host = new SampleHost();
        host.Runner.PageRows = [SampleHost.Row(Id1, "a")];

        var response = await host.Client(contract: null).PostAsync(Query, SampleHost.Json(RetiredMixedCase));
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        body!["items"]!.AsArray().Should().HaveCount(1);
        host.Runner.Calls.Should().ContainSingle().Which.Entity.Id.Should().Be(Order);

        var diagnostic = body["diagnostics"]!.AsArray().Should().ContainSingle().Which!;

        diagnostic["code"]!.GetValue<string>().Should().Be(Codes.EntityIdRetired);
        diagnostic["params"]!["currentId"]!.GetValue<string>().Should().Be(Order);
    }

    [Fact]
    public void The_rewrite_folds_the_id_and_translates_paths_against_the_entity_it_names()
    {
        var rewrite = new CompatBinder(BindHost.Probe).Rewrite(BindHost.Request("PROBE.Order", """[{ "match": { "Number": { "eq": "a" } } }]"""));

        rewrite.Refusal.Should().BeNull();
        rewrite.Request.EntityType.Should().Be(Order);
        rewrite.Request.Pipeline[0].Match!.Condition!.Path.Should().Be("number", "the root resolves, so a storage spelling translates");
        rewrite.LegacyPaths.Should().Be(1);
    }

    [Fact]
    public void A_lower_case_id_is_handed_on_as_written()
    {
        var rewrite = new CompatBinder(BindHost.Probe).Rewrite(BindHost.Request(Order, "[]"));

        rewrite.Request.EntityType.Should().Be(Order);
    }
}
