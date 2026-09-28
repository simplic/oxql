using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The OxQL 2.1 resolve binding (DESIGN §3.4.1 steps 1–7 without continuation, §3.8, §3.10): the
/// new members, the collection guard (<c>RESOLVE_ON_COLLECTION</c>), the declared cases with
/// <c>target</c> and <c>parentAs</c>, the inline or keyed executor, the shape of a keyed alias,
/// poisoned aliases and the canonical render. How a keyed resolve executes is
/// <c>Execute/KeyedFetchByKeysTests</c>.
/// </summary>
public class ResolveBindTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static EntityModel Model => ResolveModel.Model;

    private static Task<BoundPipeline> BoundAsync(string pipeline, RequestContext? context = null) =>
        BindHost.BoundAsync(Model, Invoice, pipeline, context);

    private static async Task<BoundStage.Resolve> ResolveAsync(string pipeline) =>
        (await BoundAsync(pipeline)).Stages.OfType<BoundStage.Resolve>().Last();

    private static Task<QueryValidationError> ErrorAsync(string pipeline, string code, RequestContext? context = null) =>
        BindHost.ErrorAsync(Model, Invoice, pipeline, code, context);

    private static async Task<IReadOnlyList<string>> CodesAsync(string pipeline) =>
        (await BindHost.RefusedAsync(Model, Invoice, pipeline)).Errors!.Select(error => error.Code).ToList();

    private static JsonObject RenderedResolve(BoundPipeline bound) =>
        JsonNode.Parse(bound.Canonical)!["stages"]!.AsArray().Select(stage => stage!["resolve"]).Last(stage => stage is not null)!.AsObject();

    // ---- step 1: members ---------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_member_lists_the_2_1_members_and_contract_1_refuses_them_as_legacy()
    {
        var unknown = await ErrorAsync("""[{ "resolve": { "path": "customerId", "as": "c", "source": "x" } }]""", Codes.UnknownStageMember);

        unknown.Message.Should().Contain("path, as, select, filter, elements, target, parentAs, parentSelect, onMissing, forTarget");

        var legacy = await ErrorAsync("""[{ "resolve": { "path": "customerId", "as": "c", "elements": "first" } }]""", Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));

        legacy.Message.Should().Contain("'elements'").And.Contain("path, as, select, filter.");
    }

    [Theory]
    [InlineData("""{ "elements": "some" }""", "\"first\" or \"all\"")]
    [InlineData("""{ "elements": 1 }""", "\"first\" or \"all\"")]
    [InlineData("""{ "onMissing": "ignore" }""", "\"null\", \"report\" or \"refuse\"")]
    [InlineData("""{ "target": 5 }""", "'target' is a string")]
    [InlineData("""{ "parentSelect": 5 }""", "an array of paths")]
    public async Task A_member_of_the_wrong_kind_or_value_is_refused_by_name(string member, string message)
    {
        var node = JsonNode.Parse("""{ "path": "customerId", "as": "c" }""")!.AsObject();

        foreach (var (name, value) in JsonNode.Parse(member)!.AsObject())
            node[name] = value?.DeepClone();

        var error = await ErrorAsync($$"""[{ "resolve": {{node.ToJsonString()}} }]""", Codes.UnknownStageMember);

        error.Message.Should().Contain(message);
    }

    [Fact]
    public async Task ForTarget_outside_a_continued_stage_is_not_applicable()
    {
        var error = await ErrorAsync("""[{ "resolve": { "path": "customerId", "as": "c", "forTarget": "rc.customer" } }]""", Codes.OptionNotApplicable);

        error.Message.Should().Contain("'forTarget'");
    }

    // ---- step 2: the collection guard -----------------------------------------------------------

    [Theory]
    [InlineData("lines.customerId", "lines")]
    [InlineData("customerIds", "customerIds")]
    public async Task A_path_through_a_collection_that_is_not_unwound_is_RESOLVE_ON_COLLECTION(string path, string collection)
    {
        var error = await ErrorAsync($$"""[{ "resolve": { "path": "{{path}}", "as": "c" } }]""", Codes.ResolveOnCollection);

        error.Message.Should().Be($"'{path}' lies under the collection '{collection}', which is not unwound here; unwind it first, or set 'elements' to 'first' or 'all'.");
        error.Stage.Should().Be(0);
        error.Path.Should().Be(path);
    }

    [Fact]
    public async Task A_path_through_a_lookup_array_is_RESOLVE_ON_COLLECTION_which_used_to_join_silently()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, "probe.customer", """
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["customerId"] } },
             { "resolve": { "path": "orders.customerId", "as": "again" } }]
            """, Codes.ResolveOnCollection);

        error.Message.Should().Contain("the collection 'orders'");
    }

    [Fact]
    public async Task Two_collections_that_are_not_unwound_are_UNWIND_ORDER_and_one_unwound_leaves_one()
    {
        var error = await ErrorAsync("""[{ "resolve": { "path": "lines.parts.customerId", "as": "c", "elements": "first" } }]""", Codes.UnwindOrder);

        error.Message.Should().Contain("'lines'").And.Contain("'lines.parts'");

        var unwound = await ResolveAsync("""[{ "unwind": { "path": "lines" } }, { "resolve": { "path": "lines.parts.customerId", "as": "c", "elements": "all" } }]""");

        unwound.Elements.Should().Be(ResolveElements.All);
        unwound.CollectionStorage.Should().Be("Lines.Parts");
    }

    [Fact]
    public async Task An_unwound_collection_resolves_inline_as_before()
    {
        var bound = await BoundAsync("""[{ "unwind": { "path": "lines", "as": "line" } }, { "resolve": { "path": "line.customerId", "as": "c" } }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.Executor.Should().Be(ResolveExecutor.Inline);
        resolve.Elements.Should().BeNull();
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Entity>();
    }

    [Theory]
    [InlineData("first", ResolveElements.First, false)]
    [InlineData("all", ResolveElements.All, true)]
    public async Task Elements_resolves_through_one_collection_with_the_keyed_fetch(string elements, ResolveElements expected, bool many)
    {
        var bound = await BoundAsync($$"""[{ "resolve": { "path": "lines.customerId", "as": "c", "elements": "{{elements}}" } }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.Elements.Should().Be(expected);
        resolve.CollectionStorage.Should().Be("Lines");
        resolve.Executor.Should().Be(ResolveExecutor.Keyed);
        resolve.NeedsKeyedFetch.Should().BeTrue();
        resolve.IsPlain.Should().BeFalse();
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Keyed>().Which.Many.Should().Be(many);
    }

    [Fact]
    public async Task Elements_on_a_single_value_is_not_applicable()
    {
        var error = await ErrorAsync("""[{ "resolve": { "path": "customerId", "as": "c", "elements": "first" } }]""", Codes.OptionNotApplicable);

        error.Message.Should().Contain("'customerId' holds one value per row");
    }

    // ---- step 4: declared cases, target, parentAs --------------------------------------------

    [Fact]
    public async Task A_path_without_a_reference_names_its_kind()
    {
        var error = await ErrorAsync("""[{ "resolve": { "path": "number", "as": "n" } }]""", Codes.ResolveNotDeclared);

        error.Message.Should().StartWith("'number' declares no reference; it is a");
    }

    [Fact]
    public async Task Target_narrows_a_union_to_one_entity_and_must_be_one_of_its_targets()
    {
        var narrowed = await ResolveAsync("""[{ "resolve": { "path": "localSource.id", "as": "bl", "target": "rc.tour" } }]""");

        narrowed.NarrowedTo.Should().Be("rc.tour");
        narrowed.Cases!.Should().ContainSingle().Which.Targets.Should().ContainSingle().Which.Declared.Should().Be(new ReferenceTarget("rc.tour", "id", "billingLines", false, true));
        narrowed.TargetEntity.Should().Be("rc.tour");

        var error = await ErrorAsync("""[{ "resolve": { "path": "localSource.id", "as": "bl", "target": "rc.invoice" } }]""", Codes.ResolveTargetNotDeclared);

        error.Message.Should().Be("'rc.invoice' is not a target of 'localSource.id'; its targets are 'rc.shipment', 'rc.tour', 'rc.customer'.");
    }

    [Fact]
    public async Task Target_naming_the_only_target_narrows_nothing_and_stays_inline()
    {
        var resolve = await ResolveAsync("""[{ "resolve": { "path": "customerId", "as": "c", "target": "rc.customer" } }]""");

        resolve.NarrowedTo.Should().BeNull();
        resolve.Executor.Should().Be(ResolveExecutor.Inline);
        resolve.IsPlain.Should().BeTrue();
    }

    [Fact]
    public async Task ParentAs_places_the_owning_row_of_an_item_target_with_its_key_and_display_by_default()
    {
        var bound = await BoundAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "ship" } }, { "project": { "bl.code": 1, "ship.number": 1 } }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();
        var target = resolve.Cases!.Single().Targets.Single();

        resolve.ParentAs.Should().Be("ship");
        resolve.Executor.Should().Be(ResolveExecutor.Keyed);
        target.ItemStorage.Should().Be("BillingLines");
        target.FieldStorage.Should().Be("_id", "the field is read on the element");
        target.Select!.Select(path => path.Storage).Should().Equal("_id");
        target.ParentSelect!.Select(path => path.Storage).Should().Equal("_id", "Number");
        bound.FinalShape.Roots["bl"].Should().BeOfType<ShapeNode.Keyed>().Which.Targets.Single().Item!.Wire.Should().Be("billingLines");
        bound.FinalShape.Roots["ship"].Should().BeOfType<ShapeNode.Keyed>().Which.Targets.Single().Item.Should().BeNull();

        var selected = await ResolveAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl", "select": ["code", "amount"], "parentAs": "ship", "parentSelect": ["number"] } }]""");

        selected.Cases![0].Targets[0].Select!.Select(path => path.Storage).Should().Equal("_id", "Code", "Amount");
        selected.Cases[0].Targets[0].ParentSelect!.Select(path => path.Storage).Should().Equal("_id", "Number");
    }

    [Fact]
    public async Task ParentAs_on_an_entity_target_is_RESOLVE_PARENT_NOT_ITEM()
    {
        var error = await ErrorAsync("""[{ "resolve": { "path": "customerId", "as": "c", "parentAs": "p" } }]""", Codes.ResolveParentNotItem);

        error.Message.Should().Contain("the entity 'rc.customer' itself");

        // A union with an entity case: narrowing it away admits parentAs.
        await ErrorAsync("""[{ "resolve": { "path": "localSource.id", "as": "bl", "parentAs": "p" } }]""", Codes.ResolveParentNotItem);
        (await ResolveAsync("""[{ "resolve": { "path": "localSource.id", "as": "bl", "target": "rc.shipment", "parentAs": "p" } }]""")).ParentAs.Should().Be("p");
    }

    [Fact]
    public async Task ParentSelect_needs_parentAs_and_parentAs_needs_a_name_of_its_own()
    {
        await ErrorAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl", "parentSelect": ["number"] } }]""", Codes.OptionNotApplicable);
        await ErrorAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "bl" } }]""", Codes.AliasCollision);
        await ErrorAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "number" } }]""", Codes.AliasCollision);
    }

    // ---- cases ---------------------------------------------------------------------------------

    [Fact]
    public async Task Typed_cases_bind_their_condition_in_stored_form_and_every_target()
    {
        var resolve = await ResolveAsync("""[{ "resolve": { "path": "localSource.id", "as": "src" } }]""");

        resolve.Cases!.Should().HaveCount(2);
        resolve.Cases[0].When!.Storage.Should().Be("LocalSource.Type");
        resolve.Cases[0].When!.Values.Should().Equal(new BsonString("logistics"));
        resolve.Cases[0].When!.IsVariant.Should().BeFalse();
        resolve.Cases[0].Targets.Select(target => target.Declared.ToString()).Should().Equal("rc.shipment#billingLines", "rc.tour#billingLines");
        resolve.Cases[1].When!.Values.Should().Equal(new BsonString("customer"));
        resolve.Cases[1].Targets.Single().Select!.Select(path => path.Storage).Should().Equal("_id", "Name");
        resolve.Executor.Should().Be(ResolveExecutor.Keyed);
        resolve.IsRemote.Should().BeFalse();
    }

    [Fact]
    public async Task A_converted_key_binds_keyed_with_its_conversion()
    {
        var single = await ResolveAsync("""[{ "resolve": { "path": "shipmentKey", "as": "s" } }]""");

        single.Cases!.Single().KeyAs.Should().Be(KeyAs.Guid);
        single.Executor.Should().Be(ResolveExecutor.Keyed);

        var typed = await ResolveAsync("""[{ "resolve": { "path": "billing.referenceId", "as": "b" } }]""");

        typed.Cases!.Select(bound => (bound.KeyAs, bound.When!.Storage, bound.Targets.Single().Declared.Entity))
            .Should().Equal((KeyAs.Guid, "Billing.DataType", "rc.shipment"), (KeyAs.Guid, "Billing.DataType", "rc.tour"));
    }

    [Fact]
    public async Task A_variant_case_tests_the_discriminator_of_the_holding_object()
    {
        var resolve = await ResolveAsync("""[{ "resolve": { "path": "slot.holderId", "as": "holder" } }]""");
        var when = resolve.Cases!.Single().When!;

        when.IsVariant.Should().BeTrue();
        when.Storage.Should().Be("Slot._t");
        when.Values.Should().Contain(new BsonString("RcDriverSlot"));
    }

    [Fact]
    public async Task A_case_with_a_remote_target_is_remote_and_not_a_semi_join()
    {
        var bound = await BoundAsync("""[{ "resolve": { "path": "source.id", "as": "src", "select": ["code"] } }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.IsRemote.Should().BeTrue();
        resolve.NeedsKeyedFetch.Should().BeTrue();
        resolve.Cases![1].Targets.Single().RemoteSelect.Should().Equal("code");
        bound.FinalShape.Roots["src"].Should().BeOfType<ShapeNode.Remote>().Which.SemiJoinable.Should().BeFalse();

        var error = await ErrorAsync("""[{ "resolve": { "path": "source.id", "as": "src" } }, { "match": { "src.code": { "eq": "x" } } }]""", Codes.ResolveNotFilterable);

        error.Message.Should().Contain("joined after the page");
    }

    [Fact]
    public async Task A_select_path_one_target_of_a_union_lacks_is_dropped_for_it_and_one_no_target_has_is_refused()
    {
        var resolve = await ResolveAsync("""[{ "resolve": { "path": "localSource.id", "as": "src", "select": ["code", "name"] } }]""");

        resolve.Cases![0].Targets[0].Select!.Select(path => path.Storage).Should().Equal("_id", "Code");
        resolve.Cases[0].Targets[0].DroppedSelect.Should().Equal("name");
        resolve.Cases[1].Targets[0].Select!.Select(path => path.Storage).Should().Equal("_id", "Code", "Name");

        var error = await ErrorAsync("""[{ "resolve": { "path": "localSource.id", "as": "src", "select": ["nope"] } }]""", Codes.UnknownPath);

        error.Path.Should().Be("nope");
    }

    // ---- step 5: the executor ---------------------------------------------------------------------

    [Fact]
    public async Task A_simple_local_reference_is_inline_also_onto_a_member_that_is_not_the_key()
    {
        var bound = await BoundAsync("""[{ "resolve": { "path": "customerCode", "as": "c" } }, { "match": { "c.name": { "eq": "x" } } }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.Executor.Should().Be(ResolveExecutor.Inline);
        resolve.TargetFieldStorage.Should().Be("Code");
        resolve.NeedsKeyedFetch.Should().BeFalse();
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Entity>();
    }

    [Fact]
    public async Task A_plain_remote_reference_is_keyed_but_runs_as_before()
    {
        var bound = await BoundAsync("""[{ "resolve": { "path": "contactId", "as": "c" } }, { "match": { "c.name": { "eq": "x" } } }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.Executor.Should().Be(ResolveExecutor.Keyed);
        resolve.IsRemote.Should().BeTrue();
        resolve.IsPlain.Should().BeTrue();
        resolve.NeedsKeyedFetch.Should().BeFalse("the remote resolver runs a plain remote resolve");
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Remote>().Which.SemiJoinable.Should().BeTrue();
        bound.HasSemiJoin.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{ "path": "customerId", "as": "c", "filter": { "name": { "eq": "x" } }, "onMissing": "report" }""", ResolveExecutor.Inline, ResolveOnMissing.Report)]
    [InlineData("""{ "path": "customerId", "as": "c", "filter": { "name": { "eq": "x" } }, "onMissing": "null" }""", ResolveExecutor.Inline, ResolveOnMissing.Null)]
    [InlineData("""{ "path": "customerId", "as": "c", "onMissing": "refuse" }""", ResolveExecutor.Inline, ResolveOnMissing.Refuse)]
    [InlineData("""{ "path": "customerId", "as": "c", "filter": { "name": { "eq": "x" } } }""", ResolveExecutor.Inline, ResolveOnMissing.Null)]
    public async Task A_filter_under_any_onMissing_keeps_the_inline_executor(string stage, ResolveExecutor executor, ResolveOnMissing effective)
    {
        var resolve = await ResolveAsync($$"""[{ "resolve": {{stage}} }]""");

        resolve.Executor.Should().Be(executor);
        resolve.EffectiveOnMissing.Should().Be(effective);
    }

    // ---- step 6: the keyed alias -------------------------------------------------------------------

    [Fact]
    public async Task A_keyed_alias_is_projected_never_filtered_or_sorted_and_its_paths_are_checked_against_the_targets()
    {
        var bound = await BoundAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl" } }, { "project": { "number": 1, "bl.code": 1 } }]""");

        bound.FinalShape.Carries("bl").Should().BeTrue();

        (await ErrorAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl" } }, { "match": { "bl.code": { "eq": "x" } } }]""", Codes.ResolveNotFilterable))
            .Message.Should().Contain("joined after the page is taken");
        await ErrorAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl" } }, { "sort": [{ "bl.code": "asc" }] }]""", Codes.ResolveNotSortable);
        (await ErrorAsync("""[{ "resolve": { "path": "billingLineId", "as": "bl" } }, { "project": { "bl.number": 1 } }]""", Codes.UnknownPath))
            .Message.Should().Contain("rc.shipment#billingLines");
    }

    // Step 3, a resolve or lookup under a keyed or remote alias, is ContinuationBindTests.

    // ---- §3.8: poisoned aliases ------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_resolve_poisons_its_alias_so_later_uses_add_no_errors()
    {
        var codes = await CodesAsync("""
            [{ "resolve": { "path": "lines.customerId", "as": "c" } },
             { "match": { "c.name": { "eq": "x" } } },
             { "sort": [{ "c.name": "asc" }] },
             { "resolve": { "path": "c.customerId", "as": "cc" } },
             { "project": { "c.name": 1, "cc.name": 1 } }]
            """);

        codes.Should().Equal([Codes.ResolveOnCollection], "every later stage only reads the alias that failed, or one that failed because of it");
    }

    [Fact]
    public async Task A_failed_lookup_or_unwind_poisons_its_aliases_and_an_independent_mistake_is_still_reported()
    {
        var codes = await BindHost.RefusedAsync(BindHost.Probe, "probe.customer", """
            [{ "lookup": { "from": "probe.order", "path": "nope", "as": "orders" } },
             { "unwind": { "path": "orders", "as": "order", "includeIndex": "i" } },
             { "match": { "order.number": { "eq": "x" } } },
             { "match": { "i": { "eq": 1 } } },
             { "lookup": { "from": "probe.order", "path": "customerId", "on": "order", "as": "more" } },
             { "match": { "missing": { "eq": 1 } } }]
            """);

        codes.Errors!.Select(error => (error.Code, error.Stage)).Should().Equal((Codes.UnknownPath, 0), (Codes.UnknownPath, 5));
    }

    [Fact]
    public async Task An_alias_that_failed_its_own_check_is_not_poisoned()
    {
        var codes = await CodesAsync("""
            [{ "resolve": { "path": "customerId", "as": "number" } },
             { "match": { "number": { "eq": "x" } } }]
            """);

        codes.Should().Equal([Codes.AliasCollision], "the name stays the member's, and the match on the member binds");
    }

    // ---- render ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_2_0_resolve_renders_without_the_new_members_and_onMissing_never_changes_the_render()
    {
        var plain = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "c", "filter": { "name": { "eq": "x" } } } }]""");
        var reported = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "c", "filter": { "name": { "eq": "x" } }, "onMissing": "report" } }]""");
        var nulled = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "c", "filter": { "name": { "eq": "x" } }, "onMissing": "null" } }]""");

        RenderedResolve(plain).Select(member => member.Key).Should().Equal("path", "as", "target", "targetField", "remote", "select", "filter");
        reported.Fingerprint.Should().Be(plain.Fingerprint, "onMissing reports or refuses, it never changes rows");
        nulled.Fingerprint.Should().Be(plain.Fingerprint);
        reported.Canonical.Should().Be(plain.Canonical);
    }

    [Fact]
    public async Task The_new_forms_render_their_elements_narrowing_owning_row_and_cases()
    {
        var elements = RenderedResolve(await BoundAsync("""[{ "resolve": { "path": "lines.customerId", "as": "c", "elements": "all" } }]"""));

        elements["elements"]!.GetValue<string>().Should().Be("all");
        elements["collection"]!.GetValue<string>().Should().Be("Lines");
        elements.ContainsKey("cases").Should().BeFalse("one simple case renders as the 2.0 members do");

        var narrowed = RenderedResolve(await BoundAsync("""[{ "resolve": { "path": "localSource.id", "as": "bl", "target": "rc.shipment", "parentAs": "ship" } }]"""));

        narrowed["narrowedTo"]!.GetValue<string>().Should().Be("rc.shipment");
        narrowed["parentAs"]!.GetValue<string>().Should().Be("ship");

        var target = narrowed["cases"]!.AsArray().Single()!["targets"]!.AsArray().Single()!;

        narrowed["cases"]![0]!["when"]!["path"]!.GetValue<string>().Should().Be("LocalSource.Type");
        target["item"]!.GetValue<string>().Should().Be("BillingLines");
        target["field"]!.GetValue<string>().Should().Be("_id");
        target["parentSelect"]!.AsArray().Select(path => path!.GetValue<string>()).Should().Equal("_id", "Number");

        var remote = RenderedResolve(await BoundAsync("""[{ "resolve": { "path": "source.id", "as": "src", "select": ["code"] } }]"""));

        remote["cases"]![1]!["targets"]![0]!["remote"]!.GetValue<bool>().Should().BeTrue();
        remote["cases"]![1]!["targets"]![0]!["select"]!.AsArray().Select(path => path!.GetValue<string>()).Should().Equal("code");
    }
}
