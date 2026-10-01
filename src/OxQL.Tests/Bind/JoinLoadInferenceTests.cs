using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// Select inference at bind time (improvement plan §3.S): a join loads its key, what later stages
/// read under its alias and its output set; the output set is what the projection names under the
/// alias, or, kept whole, the <c>select</c> hint with the key, else key and display. <c>select</c> is
/// a hint and never a bound, and <c>parentSelect</c> is no member any more. The reads are the
/// binder's ledger, written at its read sites with an explicit use.
/// </summary>
public class JoinLoadInferenceTests
{
    private const string Invoice = ResolveModel.Invoice;
    private const string Customer = "rc.customer";

    private static EntityModel Model => ResolveModel.Model;

    private static Task<BoundPipeline> BoundAsync(string pipeline, string entity = Invoice, RequestContext? context = null) =>
        BindHost.BoundAsync(Model, entity, pipeline, context);

    private static List<(string Path, string Use, string? Alias)> Reads(BoundPipeline bound, int stage) =>
        bound.Reads.Where(read => read.Stage == stage).Select(read => (read.Path, read.UseName, read.Alias)).ToList();

    private static IEnumerable<string> Wires(IEnumerable<ResolvedPath> paths) => paths.Select(path => path.Wire);

    // ---- a select is a hint, never a bound -----------------------------------------------------------------

    [Theory]
    [InlineData("""{ "match": { "c.code": { "eq": "x" } } }""", "match")]
    [InlineData("""{ "sort": [{ "c.code": "asc" }] }""", "sort")]
    [InlineData("""{ "project": { "c.code": 1 } }""", "project")]
    [InlineData("""{ "group": { "by": [{ "path": "c.code", "as": "code" }], "fields": { "n": { "count": true } } } }""", "groupKey")]
    [InlineData("""{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "codes": { "push": "c.code" } } } }""", "aggregate")]
    public async Task A_path_under_an_inline_resolve_beyond_its_hint_binds_and_the_join_loads_it(string stage, string use)
    {
        var bound = await BoundAsync($$"""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }, {{stage}}]""");

        Wires(bound.Stages.OfType<BoundStage.Resolve>().Single().Select!).Should().Contain(["id", "code"], "the key and what the later stage reads");
        bound.Loads["c"].Loads.Should().Contain("code");
        bound.Loads["c"].Hint.Should().Equal("name");
        Reads(bound, 1).Should().Contain(("c.code", use, "c"));
    }

    [Fact]
    public async Task What_a_stage_only_reads_is_loaded_and_is_not_in_the_output_set()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "match": { "c.code": { "eq": "x" } } }]
            """);

        bound.Loads["c"].Loads.Should().Equal("code", "id", "name");
        bound.Loads["c"].Shows.Should().Equal(["id", "name"], "kept whole, the alias shows its hint with the key");
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Entity>().Which.Select.Should().Equal("id", "name");
    }

    [Fact]
    public async Task Kept_whole_without_a_hint_an_alias_shows_and_loads_key_and_display()
    {
        var display = Model.Entities[Customer].Display!.Wire;
        var bound = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }]""");

        bound.Loads["c"].Loads.Should().BeEquivalentTo(["id", display]);
        bound.Loads["c"].Shows.Should().Equal("id", display);
        bound.Loads["c"].Hint.Should().BeNull();
    }

    [Fact]
    public async Task The_projection_under_an_alias_decides_alone_and_the_hint_is_neither_shown_nor_loaded()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "project": { "number": 1, "c.code": 1 } }]
            """);

        bound.Loads["c"].Shows.Should().Equal("code");
        bound.Loads["c"].Loads.Should().Equal(["code", "id"], "the key and the projected path; the hint is what a whole alias would show");
    }

    [Fact]
    public async Task An_alias_the_row_does_not_carry_still_loads_what_a_stage_reads()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "match": { "c.code": { "eq": "x" } } },
             { "project": { "number": 1 } }]
            """);

        bound.Loads["c"].Loads.Should().Equal("code", "id");
        bound.Loads["c"].Shows.Should().BeEmpty("the projection dropped the alias");
    }

    [Fact]
    public async Task A_stage_that_reads_the_alias_itself_loads_its_output_set_not_the_targets_whole_row()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["code"] } },
             { "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "customer": { "first": "c" } } } }]
            """);

        bound.Loads["c"].Loads.Should().Equal(["code", "id"], "the group replaced the row, but the first customer of each group is the alias as a whole row shows it");
        bound.Loads["c"].Shows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_load_another_load_covers_is_left_out()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoice", "first": true, "select": ["localSource"] } },
             { "match": { "invoice.localSource.type": { "eq": "x" } } }]
            """, Customer);

        bound.Loads["invoice"].Loads.Should().Equal(["id", "localSource"], "a path and one inside it would collide in the join's projection: the outer one loads both");
    }

    [Fact]
    public async Task A_hint_path_the_target_does_not_have_stays_UNKNOWN_PATH()
    {
        var resolve = await BindHost.ErrorAsync(Model, Invoice, """[{ "resolve": { "path": "customerId", "as": "c", "select": ["nope"] } }]""", Codes.UnknownPath);
        var lookup = await BindHost.ErrorAsync(Model, Customer, """[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "select": ["nope"] } }]""", Codes.UnknownPath);
        var union = await BindHost.ErrorAsync(Model, Invoice, """[{ "resolve": { "path": "localSource.id", "as": "bl", "target": "rc.shipment", "select": ["nope"] } }]""", Codes.UnknownPath);

        new[] { resolve, lookup, union }.Should().OnlyContain(error => error.Stage == 0 && error.Path == "nope", "a hint is checked where it is written: a typo is a typo");
    }

    // ---- each join kind ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_first_lookup_loads_what_a_sort_reads_under_its_alias()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "latest", "first": true, "select": ["number"] } },
             { "sort": [{ "latest.customerCode": "asc" }] }]
            """, Customer);

        Wires(bound.Stages.OfType<BoundStage.Lookup>().Single().Select).Should().Equal("customerCode", "id", "number");
        bound.Loads["latest"].Shows.Should().Equal("id", "number");
    }

    [Fact]
    public async Task A_lookup_on_an_alias_reads_the_member_it_joins_on_and_the_alias_loads_it()
    {
        // rc.invoice#customerCode references rc.customer by its code, which is not the key.
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "lookup": { "from": "rc.invoice", "path": "customerCode", "on": "c", "as": "sameCode" } }]
            """);

        bound.Stages.OfType<BoundStage.Lookup>().Single().ParentKeyStorage.Should().Be("c.Code");
        Reads(bound, 1).Should().Equal(("c.code", "lookupOn", "c"));
        bound.Loads["c"].Loads.Should().Equal("code", "id", "name");
        bound.Loads["c"].Shows.Should().Equal(["id", "name"], "the member the lookup joins on is read, not shown");
    }

    [Fact]
    public async Task An_array_lookup_and_its_unwound_copies_load_through_the_lookup()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "select": ["number"] } },
             { "unwind": { "path": "invoices", "as": "inv" } },
             { "match": { "inv.customerCode": { "eq": "x" } } },
             { "unwind": { "path": "inv.lines", "as": "line" } },
             { "match": { "line.customerId": { "exists": true } } }]
            """, Customer);

        bound.Reads.Where(read => read.Stage >= 2).Select(read => (read.Path, read.Alias, read.Relative)).Should().Equal(
            ("inv.customerCode", "invoices", "customerCode"),
            ("inv.lines", "invoices", "lines"),
            ("line.customerId", "invoices", "lines.customerId"));
        bound.Loads["invoices"].Loads.Should().Equal(["customerCode", "id", "lines", "number"], "an element's member is a member of the collection the lookup loads");
        bound.FinalShape.Roots["inv"].Should().BeOfType<ShapeNode.Entity>().Which.Select.Should().Equal(["id", "number"], "the unwound copy shows what the lookup's alias shows");
    }

    [Fact]
    public async Task A_keyed_local_resolve_asks_its_target_for_the_paths_the_projection_names_under_its_aliases()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "ship" } },
             { "project": { "number": 1, "bl.code": 1, "ship.number": 1 } }]
            """);
        var target = bound.Stages.OfType<BoundStage.Resolve>().Single().Cases!.Single().Targets.Single();

        Wires(target.Select!).Should().Equal(["id", "code"], "the matched member, then the projected path");
        Wires(target.ParentSelect!).Should().Equal("id", "number");
        target.RemoteParentSelect.Should().Equal("number");
        bound.Loads["bl"].Should().BeEquivalentTo(new JoinLoad(["code"], ["code"], null));
        bound.Loads["ship"].Should().BeEquivalentTo(new JoinLoad(["number"], ["number"], null));
    }

    [Fact]
    public async Task A_remote_resolve_asks_its_owner_for_the_output_set_or_for_nothing_when_whole_without_a_hint()
    {
        var whole = await BoundAsync("""[{ "resolve": { "path": "contactId", "as": "r" } }]""");
        var hinted = await BoundAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name", "email"] } }]""");
        var projected = await BoundAsync("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }, { "project": { "number": 1, "r.phone": 1, "r.email": 1 } }]""");

        whole.Stages.OfType<BoundStage.Resolve>().Single().RemoteSelect.Should().BeNull("the owner's own key and display");
        whole.Loads["r"].Should().BeEquivalentTo(new JoinLoad(null, null, null));
        hinted.Stages.OfType<BoundStage.Resolve>().Single().RemoteSelect.Should().Equal("name", "email");
        projected.Stages.OfType<BoundStage.Resolve>().Single().RemoteSelect.Should().Equal(["email", "phone"], "the projection's paths, in ordinal order; the hint is not asked for");
        projected.Loads["r"].Hint.Should().Equal("name");
    }

    [Fact]
    public async Task A_local_union_drops_a_projected_path_for_the_target_that_lacks_it()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "billing.referenceId", "as": "b" } },
             { "project": { "number": 1, "b.number": 1, "b.name": 1 } }]
            """);
        var targets = bound.Stages.OfType<BoundStage.Resolve>().Single().Cases!.SelectMany(selected => selected.Targets).ToList();

        targets.Single(target => target.Declared.Entity == "rc.shipment").DroppedSelect.Should().Equal("name");
        targets.Single(target => target.Declared.Entity == "rc.tour").DroppedSelect.Should().Equal("number");
        Wires(targets.Single(target => target.Declared.Entity == "rc.tour").Select!).Should().Equal("id", "name");

        var none = await BindHost.ErrorAsync(Model, Invoice, """[{ "resolve": { "path": "billing.referenceId", "as": "b" } }, { "project": { "b.nope": 1 } }]""", Codes.UnknownPath);

        none.Should().Match<QueryValidationError>(error => error.Stage == 1 && error.Path == "b.nope", "only a path every target lacks is unknown, where the projection names it");
    }

    // ---- the reads a caller cannot see ---------------------------------------------------------------------

    [Fact]
    public async Task The_member_that_picks_a_references_case_is_read_and_loaded_with_the_reference()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoice", "first": true, "select": ["id"] } },
             { "resolve": { "path": "invoice.localSource.id", "as": "source", "target": "rc.customer" } }]
            """, Customer);

        Reads(bound, 1).Should().Equal(
            ("invoice.localSource.id", "resolveKey", "invoice"),
            ("invoice.localSource.type", "caseCondition", "invoice"));
        bound.Loads["invoice"].Loads.Should().Equal("id", "localSource.id", "localSource.type");
        bound.Loads["invoice"].Shows.Should().Equal(["id"], "neither the reference nor its case member is shown");
    }

    [Fact]
    public async Task A_reference_picked_by_the_variant_of_its_object_reads_the_objects_type_and_loads_only_the_reference()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoice", "first": true } },
             { "resolve": { "path": "invoice.slot.holderId", "as": "holder" } }]
            """, Customer);

        bound.Reads.Where(read => read.Stage == 1).Select(read => (read.Path, read.UseName, read.TypeOnly)).Should().Equal(
            ("invoice.slot.holderId", "resolveKey", false),
            ("invoice.slot", "caseCondition", true));
        bound.Loads["invoice"].Loads.Should().Contain("slot.holderId").And.NotContain("slot", "the discriminator travels with the member the join loads below the object");
    }

    [Fact]
    public async Task The_inner_paths_of_an_any_are_recorded_under_their_collection()
    {
        var root = await BoundAsync("""[{ "match": { "lines": { "any": { "customerId": { "exists": true } } } } }]""");

        Reads(root, 0).Should().Equal(("lines", "match", null), ("lines.customerId", "match", null));

        var joined = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoice", "first": true } },
             { "match": { "invoice.lines": { "any": { "customerId": { "exists": true } } } } }]
            """, Customer);

        joined.Reads.Where(read => read.Stage == 1).Select(read => (read.Path, read.Alias, read.Relative)).Should().Equal(
            ("invoice.lines", "invoice", "lines"),
            ("invoice.lines.customerId", "invoice", "lines.customerId"));
        joined.Loads["invoice"].Loads.Should().Contain("lines");
    }

    // ---- the ledger -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_use_is_recorded_at_its_stage()
    {
        var bound = await BoundAsync("""
            [{ "match": { "number": { "neq": "x" } } },
             { "resolve": { "path": "customerId", "as": "c" } },
             { "lookup": { "from": "rc.invoice", "path": "customerCode", "on": "c", "as": "same", "filter": { "number": { "neq": "y" } }, "sort": [{ "number": "asc" }] } },
             { "unwind": { "path": "lines", "as": "line" } },
             { "sort": [{ "c.name": "asc" }] },
             { "project": { "number": 1, "c.name": 1, "line.customerId": 1 } },
             { "group": { "by": [{ "path": "c.name", "as": "name" }], "fields": { "lines": { "push": "line.customerId" } } } }]
            """);

        bound.Reads.Select(read => (read.Stage, read.Path, read.UseName, read.Alias)).Should().Equal(
            (0, "number", "match", null),
            (1, "customerId", "resolveKey", null),
            (2, "c.code", "lookupOn", "c"),
            (3, "lines", "unwind", null),
            (4, "c.name", "sort", "c"),
            (5, "number", "project", null),
            (5, "c.name", "project", "c"),
            (5, "line.customerId", "project", null),
            (6, "c.name", "groupKey", "c"),
            (6, "line.customerId", "aggregate", null));
    }

    [Fact]
    public async Task What_a_join_binds_against_its_own_target_is_no_read_of_the_row()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "select": ["number"], "filter": { "customerCode": { "eq": "x" } }, "sort": [{ "number": "desc" }] } },
             { "resolve": { "path": "invoices.customerId", "as": "again", "elements": "first", "select": ["name"], "filter": { "code": { "neq": "y" } } } }]
            """, Customer);

        bound.Reads.Select(read => (read.Stage, read.Path, read.UseName)).Should().Equal(
            [(0, "id", "lookupOn"), (1, "invoices.customerId", "resolveKey")],
            "the lookup's path, hint, filter and sort and the resolve's hint and filter are bound against the child and the target");
        bound.Loads["invoices"].Loads.Should().Equal(["customerId", "id", "number"], "the filter and the sort run inside the join and need no load");
    }

    [Fact]
    public async Task An_exclusion_reads_nothing()
    {
        var bound = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name", "code"] } }, { "project": { "c.code": 0, "number": 0 } }]""");

        Reads(bound, 1).Should().BeEmpty("a path an exclusion names is removed, not read");
        bound.Loads["c"].Shows.Should().Equal(["id", "name", "code"], "the alias stays whole; the row leaves the excluded member out");
    }

    [Fact]
    public async Task A_request_that_does_not_bind_keeps_the_reads_of_the_part_that_binds()
    {
        var outcome = await BindHost.BindAsync(Model, BindHost.Request(Invoice, """
            [{ "resolve": { "path": "customerId", "as": "c" } }, { "match": { "c.code": { "eq": "x" } } }, { "match": { "nope": { "eq": 1 } } }]
            """));

        outcome.Should().BeOfType<BindOutcome.Failed>();
        outcome.Trace!.Reads.Select(read => (read.Stage, read.Path)).Should().Equal((0, "customerId"), (1, "c.code"));
    }

    // ---- contract 1 is frozen --------------------------------------------------------------------------------

    [Fact]
    public async Task Under_contract_1_a_join_fetches_its_select_and_nothing_is_inferred()
    {
        var context = BindHost.Context(contract: 1);
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "select": ["number"] } },
             { "match": { "invoices.customerCode": { "eq": "x" } } }]
            """, Customer, context);

        Wires(bound.Stages.OfType<BoundStage.Lookup>().Single().Select).Should().Equal(["id", "number"], "the select as written, key first: what the match reads beyond it is not loaded");
        bound.Loads.Should().BeEmpty();
        bound.FinalShape.Roots["invoices"].Should().BeOfType<ShapeNode.Array>().Which.Select.Should().Equal("id", "number");
    }
}
