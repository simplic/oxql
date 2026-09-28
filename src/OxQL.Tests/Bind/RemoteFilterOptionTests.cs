using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// A condition option the wire form cannot carry is refused at the origin (RE-8): a misspelt option
/// or one written with a value that is not a boolean in a remote resolve's filter or in a continued
/// stage's filter would reach the owner as no option at all, and the owner would fold silently.
/// </summary>
public class RemoteFilterOptionTests
{
    private const string Invoice = ResolveModel.Invoice;

    [Theory]
    [InlineData("""{ "caseSensitve": true }""", "caseSensitve")]
    [InlineData("""{ "caseSensitive": "true" }""", "caseSensitive")]
    public async Task A_remote_resolve_filter_with_an_option_the_wire_cannot_carry_is_refused(string options, string named)
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice,
            $$"""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"], "filter": { "name": { "eq": "x", "options": {{options}} } } } }]""", Codes.OptionNotApplicable);

        error.Stage.Should().Be(0);
        error.Path.Should().Be("name");
        error.Message.Should().Contain(named);
    }

    [Theory]
    [InlineData("resolve", """{ "resolve": { "path": "r.companyId", "as": "co", "select": ["title"], "filter": { "or": [{ "title": { "eq": "x", "options": { "casesensitive": true } } }] } } }""")]
    [InlineData("lookup", """{ "lookup": { "from": "crm.person", "path": "contactId", "on": "r", "as": "people", "filter": { "name": { "eq": "x", "options": { "ignoreCase": 1 } } } } }""")]
    public async Task A_continued_stage_filter_with_an_option_the_wire_cannot_carry_is_refused(string kind, string stage)
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice,
            $$"""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, {{stage}}]""", Codes.OptionNotApplicable);

        error.Stage.Should().Be(1, kind);
    }

    [Fact]
    public async Task Known_options_travel_to_the_owner()
    {
        await BindHost.BoundAsync(ResolveModel.Model, Invoice, """
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"], "filter": { "name": { "eq": "x", "options": { "caseSensitive": true } } } } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"], "filter": { "title": { "eq": "x", "options": { "ignoreCase": true } } } } }]
            """);
    }
}
