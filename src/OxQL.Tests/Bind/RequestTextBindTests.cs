using FluentAssertions;
using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// A request handed to the binder as a document, without a host having read its body first: a
/// name or a text in it that is no Unicode text (half of a surrogate pair written as an escape)
/// is the caller's mistake, refused with <c>INVALID_REQUEST_BODY</c> and <c>reason: "encoding"</c>
/// wherever the binder comes to read it.
/// </summary>
public class RequestTextBindTests
{
    private const string Half = "\\ud800";

    private const string Head = """ "entityType": "probe.order" """;

    public static TheoryData<string, string, bool> Places => new()
    {
        // what, the request, whether a contract 1 request has the member (what it does not have it reads past)
        { "an operand", $$"""{ {{Head}}, "pipeline": [{ "match": { "number": { "eq": "a{{Half}}" } } }] }""", true },
        { "a bare operand", $$"""{ {{Head}}, "pipeline": [{ "match": { "number": "{{Half}}" } }] }""", true },
        { "an operand of a set", $$"""{ {{Head}}, "pipeline": [{ "match": { "number": { "in": ["a", "{{Half}}"] } } }] }""", true },
        { "a pattern", $$"""{ {{Head}}, "pipeline": [{ "match": { "number": { "contains": "\udc00" } } }] }""", true },
        { "a variable value", $$"""{ {{Head}}, "variables": { "v": "{{Half}}" }, "pipeline": [{ "match": { "number": { "eq": { "$var": "v" } } } }] }""", true },
        { "a filter id", $$"""{ {{Head}}, "filter": [{ "id": "{{Half}}", "where": { "number": "x" } }], "pipeline": [] }""", false },
        { "a path in a filter", $$"""{ {{Head}}, "filter": [{ "id": "a", "where": { "num{{Half}}": "x" } }], "pipeline": [] }""", false },
        { "an operand in a filter", $$"""{ {{Head}}, "filter": [{ "id": "a", "where": { "number": "{{Half}}" } }], "pipeline": [] }""", false },
        { "a member of a filter entry", $$"""{ {{Head}}, "filter": [{ "id": "a", "where": { "number": "x" }, "m{{Half}}": 1 }], "pipeline": [] }""", false },
        { "the policy of a missing value", $$"""{ {{Head}}, "onMissing": "{{Half}}", "pipeline": [] }""", false },
    };

    [Theory]
    [MemberData(nameof(Places))]
    public async Task A_text_that_is_no_Unicode_text_is_refused_where_the_binder_reads_it(string what, string body, bool contract1)
    {
        foreach (var contract in contract1 ? new[] { 2, 1 } : [2])
        {
            var outcome = await BindHost.BindAsync(BindHost.Probe, BindHost.Parse(body), BindHost.Context(contract: contract));
            var refusal = outcome.Should().BeOfType<BindOutcome.Failed>($"{what} under contract {contract}").Subject.Refusal;

            refusal.Status.Should().Be(400, what);

            var error = refusal.Errors.Should().ContainSingle(what).Subject;

            error.Code.Should().Be(Codes.InvalidRequestBody, what);
            error.Params!["reason"].Should().Be("encoding", what);
        }
    }

    [Fact]
    public async Task A_whole_pair_binds()
    {
        var outcome = await BindHost.BindAsync(BindHost.Probe, BindHost.Parse($$"""{ {{Head}}, "pipeline": [{ "match": { "number": { "eq": "😀" } } }] }"""));

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));
    }
}
