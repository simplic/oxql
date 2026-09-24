using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The segments below an addon bag and the key of a dictionary are the caller's text and end up
/// in a field name in storage, so a path is held to what the database accepts as one.
/// </summary>
public class CoreHardeningPathTests
{
    private const string Order = "probe.order";

    private static readonly string LongSegment = new('k', 257);

    private static readonly string DeepPath = "addon." + string.Join('.', Enumerable.Repeat("a", 64));

    private static Task Refused(string pipeline) =>
        BindHost.ErrorAsync(BindHost.Probe, Order, pipeline, Codes.InvalidPath);

    [Theory]
    [InlineData("""[{ "match": { "addon.colour\u0000": { "exists": true } } }]""")]
    [InlineData("""[{ "match": { "addon.a\u0001b.c": { "exists": true } } }]""")]
    [InlineData("""[{ "project": { "addon.line\nbreak": 1 } }]""")]
    [InlineData("""[{ "project": { "addon.c1\u0085": 1 } }]""")]
    public Task An_addon_path_with_a_control_character_is_refused(string pipeline) => Refused(pipeline);

    [Theory]
    [InlineData("""[{ "match": { "prices.k\u0000ey.net": { "exists": true } } }]""")]
    [InlineData("""[{ "project": { "prices.tab\tkey.currency": 1 } }]""")]
    public Task A_dictionary_key_with_a_control_character_is_refused(string pipeline) => Refused(pipeline);

    [Fact]
    public Task A_segment_longer_than_a_field_name_has_any_business_being_is_refused() =>
        Refused($$"""[{ "project": { "prices.{{LongSegment}}.net": 1 } }]""");

    [Fact]
    public Task A_path_deeper_than_the_database_nests_is_refused() =>
        Refused($$"""[{ "project": { "{{DeepPath}}": 1 } }]""");

    [Theory]
    [InlineData("""[{ "match": { "addon.colour": { "exists": true } } }]""")]
    [InlineData("""[{ "project": { "addon.dimensions.width": 1, "prices.list price.net": 1 } }]""")]
    [InlineData("""[{ "match": { "prices.Größe-1.net": { "exists": true } } }]""")]
    public async Task Ordinary_addon_paths_and_dictionary_keys_still_bind(string pipeline) =>
        await BindHost.BoundAsync(BindHost.Probe, Order, pipeline);
}
