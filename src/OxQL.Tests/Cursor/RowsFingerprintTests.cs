using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Cursor;

/// <summary>
/// DG2 M6: a contract 2 cursor is fingerprinted over what decides which rows there are and in which
/// order. The trailing projection, a join nothing but the row's display reads and every join's
/// <c>select</c> are outside it, so showing or hiding a column keeps a cursor valid; whatever a
/// filter, a sort, an unwind or a group reads stays inside. Contract 1 fingerprints the whole form.
/// </summary>
public class RowsFingerprintTests
{
    private const string Order = "probe.order";
    private const string Sort = """{ "sort": [{ "number": "asc" }] }""";
    private const string Join = """{ "resolve": { "path": "customerId", "as": "cust" } }""";

    private static async Task<string> Fingerprint(string pipeline, int contract = 2) =>
        (await BindHost.BoundAsync(BindHost.Probe, Order, pipeline, BindHost.Context(contract: contract))).Fingerprint;

    [Fact]
    public async Task What_only_shapes_the_row_is_outside_the_fingerprint()
    {
        var plain = await Fingerprint($"[{Sort}]");

        var same = new Dictionary<string, string>
        {
            ["a projection"] = $$"""[{{Sort}}, { "project": { "number": 1 } }]""",
            ["another projection"] = $$"""[{{Sort}}, { "project": { "number": 1, "when": 1, "id": 0 } }]""",
            ["an exclusion"] = $$"""[{{Sort}}, { "project": { "note": 0 } }]""",
            ["a projection before the sort"] = $$"""[{ "project": { "number": 1 } }, {{Sort}}]""",
            ["a page"] = $$"""[{{Sort}}, { "page": { "limit": 7, "count": true } }]""",
            ["a display-only join"] = $$"""[{{Join}}, {{Sort}}]""",
            ["a display-only join with a select"] = $$"""[{ "resolve": { "path": "customerId", "as": "cust" } }, {{Sort}}]""",
            ["a display-only join that names its outcome"] = $$"""[{ "resolve": { "path": "customerId", "as": "cust", "outcomeAs": "custOutcome" } }, {{Sort}}]""",
            ["a display-only join under a projection"] = $$"""[{{Join}}, {{Sort}}, { "project": { "number": 1, "cust.name": 1 } }]""",
            ["a display-only join after the sort"] = $$"""[{{Sort}}, {{Join}}, { "project": { "cust.name": 1 } }]""",
        };

        foreach (var (what, pipeline) in same)
            (await Fingerprint(pipeline)).Should().Be(plain, what);
    }

    [Fact]
    public async Task What_decides_the_rows_or_their_order_is_inside_it()
    {
        var plain = await Fingerprint($"[{Sort}]");

        var other = new Dictionary<string, string>
        {
            ["no sort"] = "[]",
            ["another direction"] = """[{ "sort": [{ "number": "desc" }] }]""",
            ["another sort path"] = """[{ "sort": [{ "when": "asc" }] }]""",
            ["a match"] = $$"""[{ "match": { "number": { "eq": "x" } } }, {{Sort}}]""",
            ["another operand"] = $$"""[{ "match": { "number": { "eq": "y" } } }, {{Sort}}]""",
            ["an unwind"] = $$"""[{ "unwind": { "path": "items", "as": "item" } }, {{Sort}}]""",
            ["a group"] = """[{ "group": { "by": [{ "path": "number", "as": "number" }], "fields": { "n": { "count": true } } } }, { "sort": [{ "number": "asc" }] }]""",
            ["a join a match reads"] = $$"""[{{Join}}, { "match": { "cust.name": { "eq": "x" } } }, {{Sort}}]""",
            ["a join a sort reads"] = $$"""[{{Join}}, { "sort": [{ "cust.name": "asc" }] }]""",
            ["a join whose outcome a match reads"] = $$"""[{ "resolve": { "path": "customerId", "as": "cust", "outcomeAs": "custOutcome" } }, { "match": { "custOutcome": { "eq": "resolved" } } }, {{Sort}}]""",
            ["a projection a match follows"] = $$"""[{ "project": { "number": 1 } }, { "match": { "number": { "exists": true } } }, {{Sort}}]""",
        };

        var seen = new HashSet<string> { plain };

        foreach (var (what, pipeline) in other)
            seen.Add(await Fingerprint(pipeline)).Should().BeTrue(what + " is a query of its own");
    }

    [Fact]
    public async Task A_join_that_is_read_keeps_its_filter_inside_and_its_select_outside()
    {
        const string Read = """{ "match": { "cust.name": { "eq": "x" } } }""";
        var read = await Fingerprint($"[{Join}, {Read}, {Sort}]");

        (await Fingerprint($$"""[{ "resolve": { "path": "customerId", "as": "cust" } }, {{Read}}, {{Sort}}]""")).Should().Be(read, "what a join loads decides no row");
        (await Fingerprint($$"""[{ "resolve": { "path": "customerId", "as": "cust", "filter": { "name": { "exists": true } } } }, {{Read}}, {{Sort}}]""")).Should().NotBe(read, "a join's filter decides what a condition on its alias matches");
    }

    [Fact]
    public async Task Contract_1_fingerprints_the_whole_form_and_never_shares_a_cursor_with_contract_2()
    {
        var plain = await Fingerprint($"[{Sort}]", contract: 1);

        (await Fingerprint($$"""[{{Sort}}, { "project": { "number": 1 } }]""", contract: 1)).Should().NotBe(plain, "a contract 1 cursor is bound to its projection, as it always has been");
        (await Fingerprint($"[{Sort}]")).Should().NotBe(plain, "a contract 2 cursor carries the rows served and is cut to what decides the rows");
    }

    [Fact]
    public async Task The_stages_that_only_shape_the_row_are_named_by_the_read_ledger()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{{Join}}, {{Sort}}, { "project": { "number": 1, "cust.name": 1 } }]""");

        BoundCanonical.DisplayOnly(bound.Stages, bound.Reads).Select(stage => stage.GetType().Name).Should().BeEquivalentTo(["Resolve", "Project"]);

        var read = await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{{Join}}, { "sort": [{ "cust.name": "asc" }] }, { "project": { "number": 1 } }]""");

        BoundCanonical.DisplayOnly(read.Stages, read.Reads).Select(stage => stage.GetType().Name).Should().BeEquivalentTo(["Project"]);
    }
}
