using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// <c>$dateTrunc</c> takes an IANA id and nothing else, while the runtime's lookup also finds a
/// zone by its Windows id. Whatever the platform resolves, only an IANA id goes on the wire.
/// </summary>
public class CoreHardeningTimezoneTests
{
    private const string Order = "probe.order";

    private static string Pipeline(string timezone) =>
        $$"""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "day", "timezone": "{{timezone}}" }, "as": "day" }], "fields": { "n": { "count": true } } } }]""";

    [Theory]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("Pacific Standard Time", "America/Los_Angeles")]
    public async Task A_windows_id_is_sent_as_its_iana_id_or_refused_never_verbatim(string windows, string iana)
    {
        var outcome = await BindHost.BindAsync(BindHost.Probe, BindHost.Request(Order, Pipeline(windows)));

        // A platform that maps the id binds the IANA one; a platform that cannot refuses it.
        if (outcome is BindOutcome.Bound bound)
            bound.Pipeline.Stages.OfType<BoundStage.Group>().Single().Keys.Single().Trunc!.Timezone.Should().Be(iana);
        else
            ((BindOutcome.Failed)outcome).Refusal.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.InvalidTimezone);
    }

    [Fact]
    public async Task An_iana_id_is_sent_as_written()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, Pipeline("Europe/Berlin"));

        bound.Stages.OfType<BoundStage.Group>().Single().Keys.Single().Trunc!.Timezone.Should().Be("Europe/Berlin");
    }

    [Fact]
    public async Task A_name_that_is_no_zone_is_refused() =>
        await BindHost.ErrorAsync(BindHost.Probe, Order, Pipeline("Mars/Olympus_Mons"), Codes.InvalidTimezone);
}
