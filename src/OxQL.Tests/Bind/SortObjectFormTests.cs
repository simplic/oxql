using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using FluentAssertions;
using OxQL.AspNetCore.Compat;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The sort object form is contract 2 grammar: contract 1 keeps a direction string and refuses
/// the object form as a direction. The object form without a direction says what is missing.
/// The option bag's unknown names are for the binder, not part of the published request shape.
/// </summary>
public class SortObjectFormTests
{
    private const string Order = "probe.order";

    private static readonly CompatBinder Compat = new(BindHost.Probe);

    private static async Task<BindOutcome> BindContract1(string pipeline) =>
        await BindHost.BindAsync(BindHost.Probe, Compat.Rewrite(BindHost.Request(Order, pipeline)).Request, BindHost.Context(contract: 1));

    private static QueryValidationError Refused(BindOutcome outcome, string code)
    {
        outcome.Should().BeOfType<BindOutcome.Failed>(BindHost.Describe(outcome));

        var errors = ((BindOutcome.Failed)outcome).Refusal.Errors ?? [];

        errors.Should().ContainSingle(BindHost.Describe(outcome)).Which.Code.Should().Be(code);

        return errors[0];
    }

    [Theory]
    [InlineData("""{ "Number": { "direction": "desc" } }""")]
    [InlineData("""{ "Number": { "direction": "asc", "caseSensitive": true } }""")]
    [InlineData("""{ "Number": { "direction": "asc", "nulls": "first" } }""")]
    [InlineData("""{ "Number": { } }""")]
    public async Task Contract_1_refuses_the_object_form_as_a_direction(string entry)
    {
        var error = Refused(await BindContract1($$"""[{ "sort": [{{entry}}] }, { "page": { "limit": 5 } }]"""), Codes.InvalidSortDirection);

        error.Path.Should().Be("number");
        error.Message.Should().NotContain("caseSensitive", "contract 1 has no such member to advertise");
    }

    [Fact]
    public async Task Contract_1_still_reads_a_direction_string()
    {
        var outcome = await BindContract1("""[{ "sort": [{ "Number": "DESC" }] }, { "page": { "limit": 5 } }]""");

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));
        ((BindOutcome.Bound)outcome).Pipeline.Sort!.Fields[0].Ascending.Should().BeFalse();
    }

    [Fact]
    public async Task Contract_2_reads_the_object_form_with_only_a_direction()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": { "direction": "desc" } }] }]""");

        bound.Sort!.Fields[0].Ascending.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{ "number": { } }""")]
    [InlineData("""{ "number": { "caseSensitive": true } }""")]
    public async Task The_object_form_without_a_direction_names_the_missing_member(string entry)
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, $$"""[{ "sort": [{{entry}}] }]""", Codes.InvalidSortDirection);

        error.Message.Should().Be("The object form of a sort entry needs a direction, asc or desc.");
        error.Path.Should().Be("number");
    }

    [Fact]
    public async Task An_empty_direction_string_is_still_not_a_direction()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, """[{ "sort": [{ "number": "" }] }]""", Codes.InvalidSortDirection);

        error.Message.Should().Be("'' is not a direction; asc or desc.");
    }

    // ---- the option bag's unknown names ------------------------------------------------------

    [Fact]
    public void The_unknown_option_names_are_not_part_of_the_published_options_shape()
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, typeof(FilterConditionOptions));
        var properties = schema["properties"]!.AsObject().Select(property => property.Key).ToList();

        properties.Should().BeEquivalentTo(["IgnoreCase", "CaseSensitive"]);
        JsonSerializer.Serialize(new FilterConditionOptions { CaseSensitive = true, Unknown = ["nope"] }).Should().NotContain("nope");
    }

    [Fact]
    public async Task An_unknown_option_is_still_carried_to_the_binder_and_refused()
    {
        var request = BindHost.Request(Order, """[{ "match": { "number": { "eq": "x", "options": { "nope": true } } } }]""");

        request.Pipeline[0].Match!.Condition!.Options!.Unknown.Should().Equal("nope");

        var error = await BindHost.ErrorAsync(BindHost.Probe, Order, """[{ "match": { "number": { "eq": "x", "options": { "nope": true } } } }]""", Codes.OptionNotApplicable);

        error.Message.Should().Contain("nope");
        JsonNode.Parse(JsonSerializer.Serialize(request, BindHost.Json))!.ToJsonString().Should().NotContain("nope", "the wire form writes only the options it knows");
    }
}
