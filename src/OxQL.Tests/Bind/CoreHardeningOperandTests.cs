using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// Operands the database would reject at execution are refused at binding: a text operand
/// whose pattern outgrows what the server compiles, and a duration a TimeSpan cannot hold.
/// </summary>
public class CoreHardeningOperandTests
{
    private static EntityModel Model => BindHost.Probe;
    private const string Order = "probe.order";

    /// <summary>The body limit is what bounds an operand before the binder sees it; these tests bind directly.</summary>
    private static readonly string TooLong = new('a', 40_000);

    /// <summary>Every character escapes to two, so half the byte limit is already too long.</summary>
    private static readonly string TooLongEscaped = new('.', 20_000);

    private static async Task<QueryValidationError> Error(string pipeline, string code) =>
        await BindHost.ErrorAsync(Model, Order, pipeline, code);

    [Theory]
    [InlineData("contains")]
    [InlineData("startsWith")]
    [InlineData("endsWith")]
    public async Task A_text_operator_refuses_an_operand_whose_pattern_the_database_cannot_compile(string op)
    {
        var error = await Error($$"""[{ "match": { "number": { "{{op}}": "{{TooLong}}" } } }]""", Codes.InvalidOperand);

        error.Path.Should().Be("number");
    }

    [Fact]
    public async Task The_limit_counts_the_escaped_pattern() =>
        await Error($$"""[{ "match": { "number": { "contains": "{{TooLongEscaped}}" } } }]""", Codes.InvalidOperand);

    [Theory]
    [InlineData("eq", false)]
    [InlineData("neq", false)]
    [InlineData("in", true)]
    [InlineData("nin", true)]
    public async Task An_ignoreCase_comparison_refuses_an_operand_whose_pattern_the_database_cannot_compile(string op, bool set)
    {
        var operand = set ? $"[\"ok\", \"{TooLong}\"]" : $"\"{TooLong}\"";

        // Under contract 1 the fold is a pattern; under contract 2 it runs under the collation and has no pattern limit.
        await BindHost.ErrorAsync(Model, Order, $$"""[{ "match": { "number": { "{{op}}": {{operand}}, "options": { "ignoreCase": true } } } }]""", Codes.InvalidOperand, BindHost.Context(contract: 1));
    }

    [Fact]
    public async Task A_plain_comparison_is_not_a_pattern_and_has_no_pattern_limit() =>
        await BindHost.BoundAsync(Model, Order, $$"""[{ "match": { "number": { "eq": "{{TooLong}}" } } }]""");

    [Fact]
    public async Task A_long_text_operand_under_the_limit_still_binds()
    {
        await BindHost.BoundAsync(Model, Order, $$"""[{ "match": { "number": { "contains": "{{new string('a', 500)}}" } } }]""");
        await BindHost.BoundAsync(Model, Order, $$"""[{ "match": { "number": { "contains": "{{new string('1', 5_000)}}" } } }]""");
        await BindHost.BoundAsync(Model, Order, $$"""[{ "match": { "number": { "contains": "{{new string('a', 5_000)}}", "options": { "caseSensitive": true } } } }]""");

        // A folding contains is the pattern that folds accents: each letter is the class of
        // its equivalents, and it is that pattern the limit holds.
        await BindHost.ErrorAsync(Model, Order, $$"""[{ "match": { "number": { "contains": "{{new string('a', 5_000)}}" } } }]""", Codes.InvalidOperand);
    }

    [Fact]
    public async Task A_regex_longer_than_the_database_compiles_is_refused_whatever_the_host_allows()
    {
        var context = BindHost.Context(BindHost.Options(options => options.Limits.RegexMaxLength = 100_000));

        await BindHost.ErrorAsync(Model, Order, $$"""[{ "match": { "number": { "regex": "^{{TooLong}}" } } }]""", Codes.RegexTooLong, context);
    }

    [Theory]
    [InlineData("P99999999999D")]
    [InlineData("-P99999999999D")]
    public async Task A_duration_a_TimeSpan_cannot_hold_is_an_invalid_operand(string duration) =>
        await Error($$"""[{ "match": { "span": { "eq": "{{duration}}" } } }]""", Codes.InvalidOperand);

    [Theory]
    [InlineData("12,50")]
    [InlineData("1,000")]
    [InlineData("1,2,3")]
    [InlineData(" 7 ")]
    [InlineData("5-")]
    public async Task A_decimal_operand_is_digits_with_one_sign_point_and_exponent_or_refused(string text)
    {
        var error = await Error($$"""[{ "match": { "amount": { "eq": "{{text}}" } } }]""", Codes.InvalidOperand);

        error.Path.Should().Be("amount");
    }

    [Theory]
    [InlineData("12.50")]
    [InlineData("-12.5")]
    [InlineData("1.25e1")]
    public async Task A_decimal_operand_written_as_text_binds(string text) =>
        await BindHost.BoundAsync(Model, Order, $$"""[{ "match": { "amount": { "eq": "{{text}}" } } }]""");

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    public async Task A_member_holding_one_character_refuses_an_operand_that_is_not_one(string text)
    {
        var error = await Error($$"""[{ "match": { "initial": { "eq": "{{text}}", "options": { "caseSensitive": true } } } }]""", Codes.InvalidOperand);

        error.Path.Should().Be("initial");
        error.Message.Should().Contain("a single character");
    }
}
