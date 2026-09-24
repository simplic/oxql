using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// Caller input the binder answers with a coded refusal rather than a fault: a null where a
/// stage, a stage member or a list element belongs.
/// </summary>
public class CoreHardeningBinderTests
{
    private static EntityModel Model => BindHost.Probe;
    private const string Order = "probe.order";

    private static async Task<QueryValidationError> Error(string pipeline, string code, RequestContext? context = null) =>
        await BindHost.ErrorAsync(Model, Order, pipeline, code, context);

    [Theory]
    [InlineData("match")]
    [InlineData("lookup")]
    [InlineData("resolve")]
    [InlineData("unwind")]
    [InlineData("group")]
    [InlineData("project")]
    [InlineData("sort")]
    [InlineData("page")]
    public async Task A_null_stage_member_is_refused_with_a_code(string stage)
    {
        var error = await Error($$"""[{ "{{stage}}": null }]""", Codes.UnknownStageMember);

        error.Stage.Should().Be(0);
        error.Message.Should().Contain($"'{stage}' is null");
    }

    [Fact]
    public async Task A_null_stage_member_is_refused_at_its_own_index()
    {
        var error = await Error("""[{ "match": { "number": "A" } }, { "page": null }]""", Codes.UnknownStageMember);

        error.Stage.Should().Be(1);
    }

    [Fact]
    public async Task A_null_sort_entry_is_refused_with_a_code()
    {
        var error = await Error("""[{ "sort": [{ "number": "asc" }, null] }]""", Codes.UnknownStageMember);

        error.Stage.Should().Be(0);
    }

    [Fact]
    public async Task A_null_group_key_is_refused_with_a_code() =>
        await Error("""[{ "group": { "by": [null], "fields": { "n": { "count": true } } } }]""", Codes.UnknownStageMember);

    [Fact]
    public async Task A_null_aggregate_is_refused_with_a_code() =>
        await Error("""[{ "group": { "by": [{ "path": "number", "as": "k" }], "fields": { "n": null } } }]""", Codes.UnknownStageMember);
}
