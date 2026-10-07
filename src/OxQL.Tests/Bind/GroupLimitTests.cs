using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// How many keys and aggregates one group may carry (DG2): 40 unless the host says otherwise, since a
/// grid derives one aggregate per column it totals. One more is refused before anything runs, with
/// the limit in <c>params</c>, and explain calls it a limit.
/// </summary>
public class GroupLimitTests
{
    private const string Order = "probe.order";

    private static string Group(int keys, int fields) =>
        $$"""[{ "group": { "by": [{{string.Join(", ", Enumerable.Range(0, keys).Select(n => n == 0 ? """{ "path": "state", "as": "k0" }""" : $$"""{ "dateTrunc": { "path": "when", "unit": "day" }, "as": "k{{n}}" }"""))}}], "fields": { {{string.Join(", ", Enumerable.Range(0, fields).Select(n => $$""" "f{{n}}": { "count": true } """))}} } } }]""";

    [Fact]
    public async Task Forty_keys_and_aggregates_bind_by_default_and_one_more_is_refused_with_the_limit()
    {
        new OxQLOptions().Limits.MaxGroupFields.Should().Be(40);

        (await BindHost.BoundAsync(BindHost.Probe, Order, Group(2, 38))).Stages.OfType<BoundStage.Group>().Single().Fields.Should().HaveCount(38);
        (await BindHost.BoundAsync(BindHost.Probe, Order, Group(0, 40))).Stages.OfType<BoundStage.Group>().Single().Keys.Should().BeEmpty();

        foreach (var (keys, fields) in new[] { (2, 39), (0, 41), (1, 40) })
        {
            var error = await BindHost.ErrorAsync(BindHost.Probe, Order, Group(keys, fields), Codes.MaxGroupFieldsExceeded);

            error.Stage.Should().Be(0);
            error.Message.Should().Be("The group has 41 fields; the limit is 40.");
            error.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["limit"] = "maxGroupFields", ["max"] = 40, ["count"] = 41 });
            Basis.OfCode[error.Code].Should().Be(Basis.Limits);
        }
    }

    [Fact]
    public async Task The_host_sets_the_limit_and_contract_1_answers_the_refusal_it_always_answered()
    {
        var two = BindHost.Options(options => options.Limits.MaxGroupFields = 2);

        await BindHost.BoundAsync(BindHost.Probe, Order, Group(1, 1), BindHost.Context(two));
        (await BindHost.ErrorAsync(BindHost.Probe, Order, Group(1, 2), Codes.MaxGroupFieldsExceeded, BindHost.Context(two))).Params!["max"].Should().Be(2);

        var legacy = await BindHost.ErrorAsync(BindHost.Probe, Order, Group(1, 2), Codes.MaxGroupFieldsExceeded, BindHost.Context(two, contract: 1));

        legacy.Message.Should().Be("The group has 3 fields; the limit is 2.");
        legacy.Params.Should().BeNull();
    }
}
