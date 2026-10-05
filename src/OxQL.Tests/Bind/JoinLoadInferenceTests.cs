using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// Load inference at bind time (improvement plan §3.S): a join loads its key, what later stages
/// read under its alias and its output set; the output set is what the projection names under the
/// alias, or, kept whole, its key and display member. A contract 2 join has no <c>select</c>: the
/// member is refused by name, and nothing but the projection chooses what an alias shows. The reads
/// are the binder's ledger, written at its read sites with an explicit use.
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

    // ---- a join has no select; it loads what is read -------------------------------------------------------

    private const string ResolveMembers = "path, as, filter, elements, target, parentAs, onMissing, forTarget, byTarget, outcomeAs";
    private const string LookupMembers = "from, path, as, filter, limit, sort, first, on, forTarget, parentAs";

    [Theory]
    [InlineData("""[{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } }]""", Invoice, "resolve", ResolveMembers)]
    [InlineData("""[{ "resolve": { "path": "customerId", "as": "c", "select": [] } }]""", Invoice, "resolve", ResolveMembers)]
    [InlineData("""[{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } }]""", Invoice, "resolve", ResolveMembers)]
    [InlineData("""[{ "resolve": { "path": "localSource.id", "as": "bl", "target": "rc.shipment", "select": ["nope"] } }]""", Invoice, "resolve", ResolveMembers)]
    [InlineData("""[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "select": ["number"] } }]""", Customer, "lookup", LookupMembers)]
    public async Task A_select_on_a_contract_2_join_is_refused_by_name_with_the_stage_that_replaces_it(string pipeline, string entity, string stage, string members)
    {
        foreach (var context in new[] { BindHost.Context(), BindHost.Context() with { Internal = true } })
        {
            var error = await BindHost.ErrorAsync(Model, entity, pipeline, Codes.UnknownStageMember, context);

            error.Stage.Should().Be(0);
            error.Message.Should().StartWith($"'select' is not a member of {stage}; a {stage} carries {members}.").And.Contain("project");
            error.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["stage"] = stage, ["member"] = "select", ["reason"] = "removed", ["use"] = "project" },
                "the same on the public route and on an internal call: nothing the engine sends writes a select");
        }
    }

    [Theory]
    [InlineData("""{ "match": { "c.code": { "eq": "x" } } }""", "match")]
    [InlineData("""{ "sort": [{ "c.code": "asc" }] }""", "sort")]
    [InlineData("""{ "project": { "c.code": 1 } }""", "project")]
    [InlineData("""{ "group": { "by": [{ "path": "c.code", "as": "code" }], "fields": { "n": { "count": true } } } }""", "groupKey")]
    [InlineData("""{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "codes": { "push": "c.code" } } } }""", "aggregate")]
    public async Task A_path_under_an_inline_resolve_binds_and_the_join_loads_it(string stage, string use)
    {
        var bound = await BoundAsync($$"""[{ "resolve": { "path": "customerId", "as": "c" } }, {{stage}}]""");

        Wires(bound.Stages.OfType<BoundStage.Resolve>().Single().Select!).Should().Contain(["id", "code"], "the key and what the later stage reads");
        bound.Loads["c"].Loads.Should().Contain("code");
        Reads(bound, 1).Should().Contain(("c.code", use, "c"));
    }

    [Fact]
    public async Task What_a_stage_only_reads_is_loaded_and_is_not_in_the_output_set()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "match": { "c.code": { "eq": "x" } } }]
            """);

        bound.Loads["c"].Loads.Should().Equal("code", "id", "name");
        bound.Loads["c"].Shows.Should().Equal(["id", "name"], "kept whole, the alias shows its key and display member");
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Entity>().Which.Select.Should().Equal("id", "name");
    }

    [Fact]
    public async Task Kept_whole_an_alias_shows_and_loads_key_and_display()
    {
        var display = Model.Entities[Customer].Display!.Wire;
        var bound = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }]""");

        bound.Loads["c"].Loads.Should().BeEquivalentTo(["id", display]);
        bound.Loads["c"].Shows.Should().Equal("id", display);
    }

    [Fact]
    public async Task The_projection_under_an_alias_decides_alone_and_the_display_member_is_neither_shown_nor_loaded()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "project": { "number": 1, "c.code": 1 } }]
            """);

        bound.Loads["c"].Shows.Should().Equal("code");
        bound.Loads["c"].Loads.Should().Equal(["code", "id"], "the key and the projected path; the display member is what a whole alias would show");
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
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "customer": { "first": "c" } } } }]
            """);

        bound.Loads["c"].Loads.Should().Equal(["id", "name"], "the group replaced the row, but the first customer of each group is the alias as a whole row shows it: key and display, never the whole row of the target");
        bound.Loads["c"].Shows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_load_another_load_covers_is_left_out()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoice", "first": true } },
             { "match": { "invoice.localSource.type": { "eq": "x" } } },
             { "project": { "name": 1, "invoice.localSource": 1 } }]
            """, Customer);

        bound.Loads["invoice"].Loads.Should().Equal(["id", "localSource"], "a path and one inside it would collide in the join's projection: the outer one loads both");
    }

    // ---- each join kind ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_first_lookup_loads_what_a_sort_reads_under_its_alias()
    {
        var bound = await BoundAsync("""
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "latest", "first": true } },
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
            [{ "resolve": { "path": "customerId", "as": "c" } },
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
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices" } },
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
        bound.Loads["bl"].Should().BeEquivalentTo(new JoinLoad(["code"], ["code"]));
        bound.Loads["ship"].Should().BeEquivalentTo(new JoinLoad(["number"], ["number"]));
    }

    [Fact]
    public async Task A_remote_resolve_asks_its_owner_for_the_output_set_or_for_nothing_when_whole()
    {
        var whole = await BoundAsync("""[{ "resolve": { "path": "contactId", "as": "r" } }]""");
        var projected = await BoundAsync("""[{ "resolve": { "path": "contactId", "as": "r" } }, { "project": { "number": 1, "r.phone": 1, "r.email": 1 } }]""");

        whole.Stages.OfType<BoundStage.Resolve>().Single().RemoteSelect.Should().BeNull("the owner's own key and display");
        whole.Loads["r"].Should().BeEquivalentTo(new JoinLoad(null, null));
        projected.Stages.OfType<BoundStage.Resolve>().Single().RemoteSelect.Should().Equal(["email", "phone"], "the projection's paths, in ordinal order, and nothing beside them");
        projected.Loads["r"].Should().BeEquivalentTo(new JoinLoad(["email", "phone"], ["email", "phone"]));
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
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoice", "first": true } },
             { "resolve": { "path": "invoice.localSource.id", "as": "source", "target": "rc.customer" } },
             { "project": { "name": 1, "invoice.id": 1, "source": 1 } }]
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
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices", "filter": { "customerCode": { "eq": "x" } }, "sort": [{ "number": "desc" }] } },
             { "resolve": { "path": "invoices.customerId", "as": "again", "elements": "first", "filter": { "code": { "neq": "y" } } } }]
            """, Customer);

        bound.Reads.Select(read => (read.Stage, read.Path, read.UseName)).Should().Equal(
            [(0, "id", "lookupOn"), (1, "invoices.customerId", "resolveKey")],
            "the lookup's path, filter and sort and the resolve's filter are bound against the child and the target");
        bound.Loads["invoices"].Loads.Should().Equal(["customerId", "id", "number"], "the filter and the sort run inside the join and need no load");
    }

    [Fact]
    public async Task An_exclusion_reads_nothing()
    {
        var bound = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "project": { "c.name": 0, "number": 0 } }]""");

        Reads(bound, 1).Should().BeEmpty("a path an exclusion names is removed, not read");
        bound.Loads["c"].Shows.Should().Equal(["id", "name"], "the alias stays whole; the row leaves the excluded member out");
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
