using System.Net;
using FluentAssertions;
using OxQL.AspNetCore.Compat;
using OxQL.Core.Binding;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// A request without the contract header goes through the contract 1 rewrite before the
/// binder. Whatever is null in it is the caller's error and the binder's to refuse: the rewrite
/// hands it on untouched instead of failing on it.
/// </summary>
public class HostHardeningCompatNullTests
{
    public static TheoryData<string> NullShapes => new()
    {
        """[null]""",
        """[{ "match": { "Number": { "eq": "a" } } }, null]""",
        """[{ "match": null }]""",
        """[{ "sort": null }]""",
        """[{ "unwind": null }]""",
        """[{ "group": null }]""",
        """[{ "project": null }]""",
        """[{ "page": null }]""",
        """[{ "sort": [null] }]""",
        """[{ "sort": [{ "path": "Number" }] }]""",
        """[{ "sort": [{ "direction": "asc" }] }]""",
        """[{ "unwind": {} }]""",
        """[{ "group": { "by": [null] } }]""",
        """[{ "group": { "by": [{ "as": "x" }], "fields": { "n": null } } }]""",
        """[{ "project": { "fields": null } }]""",
    };

    [Theory]
    [MemberData(nameof(NullShapes))]
    public void The_rewrite_hands_a_null_on_untouched(string pipeline)
    {
        var request = BindHost.Request("probe.order", pipeline);
        var rewrite = new CompatBinder(BindHost.Probe).Rewrite(request);

        rewrite.Refusal.Should().BeNull("the rewrite only refuses the v1 lookup and resolve stages");
        rewrite.Request.Pipeline.Should().HaveCount(request.Pipeline.Count);

        for (var index = 0; index < request.Pipeline.Count; index++)
            if (request.Pipeline[index] is null)
                rewrite.Request.Pipeline[index].Should().BeNull();
    }

    [Fact]
    public void A_stage_with_a_null_body_keeps_its_key_for_the_binder()
    {
        var rewrite = new CompatBinder(BindHost.Probe).Rewrite(BindHost.Request("probe.order", """[{ "match": null }, { "sort": null }]"""));

        rewrite.Request.Pipeline.Select(stage => stage.Kind).Should().Equal("match", "sort");
        rewrite.Request.Pipeline[0].Match.Should().BeNull();
        rewrite.Request.Pipeline[1].Sort.Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "entityType": "probe.order", "pipeline": [null] }""")]
    [InlineData("""{ "entityType": "probe.order", "pipeline": [{ "match": { "Number": { "eq": "a" } } }, null] }""")]
    public async Task A_null_stage_without_the_contract_header_is_refused_with_a_code(string body)
    {
        using var host = new SampleHost();

        var response = await host.Client(contract: null).PostAsync("/OxQL/query", SampleHost.Json(body));
        var answer = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, answer?.ToJsonString());
        answer!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownStage);
    }

    [Fact]
    public async Task A_null_stage_in_a_batch_without_the_contract_header_is_refused_per_entry()
    {
        using var host = new SampleHost();

        var response = await host.Client(contract: null).PostAsync("/OxQL/batch", SampleHost.Json("""{ "queries": [{ "entityType": "probe.order", "pipeline": [null] }] }"""));
        var answer = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        answer!["results"]![0]!["errors"]![0]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownStage);
    }
}
