using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// Remote continuation at bind time (DESIGN §3.4.1 step 3, §3.4.2 <c>on</c>, §3.5.3–§3.5.5): a
/// resolve under a keyed or remote alias, and a lookup on one, bind as a <see cref="ContinuedStage"/>
/// of the keyed stage whose owner query carries them; <c>forTarget</c>, <c>MaxContinuedStages</c>, the
/// variables, and what stays <c>NOT_CONTINUABLE</c>. How the owner query carries them is
/// <c>Core/ContinuationSplitterTests</c> and <c>Execute/ContinuationExecutionTests</c>.
/// </summary>
public class ContinuationBindTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static EntityModel Model => ResolveModel.Model;

    /// <summary>A plain remote resolve onto <c>crm.contact</c>.</summary>
    private const string Contact = """{ "resolve": { "path": "contactId", "as": "r" } }""";

    /// <summary>A keyed local resolve onto an item: the shipment's billing line.</summary>
    private const string Line = """{ "resolve": { "path": "billingLineId", "as": "r" } }""";

    /// <summary>A union of item targets with the owning row: shipment and tour billing lines, locally.</summary>
    private const string Source = """{ "resolve": { "path": "localSource.id", "as": "line", "target": "rc.shipment", "parentAs": "owner" } }""";

    /// <summary>The ERP source reference: shipment and tour lines here, a transport line remote; every target an item.</summary>
    private const string Union = """{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }""";

    private static Task<BoundPipeline> BoundAsync(string pipeline, RequestContext? context = null, string? variables = null) =>
        BindHost.BoundAsync(Model, Invoice, pipeline, context, variables);

    private static Task<QueryValidationError> ErrorAsync(string pipeline, string code, RequestContext? context = null, string? variables = null) =>
        BindHost.ErrorAsync(Model, Invoice, pipeline, code, context, variables);

    private static RequestContext With(Action<OxQLOptions> configure) => BindHost.Context(BindHost.Options(configure));

    // ---- what continues ---------------------------------------------------------------------------

    [Theory]
    [InlineData(Contact)]
    [InlineData(Line)]
    public async Task A_resolve_under_a_keyed_or_remote_alias_binds_as_a_stage_continued_at_its_owner(string first)
    {
        var bound = await BoundAsync($$"""[{{first}}, { "resolve": { "path": "r.customerId", "as": "c", "select": ["name"] } }]""");

        var continued = bound.Stages.OfType<ContinuedStage>().Should().ContainSingle().Subject;
        continued.Anchor.Should().Be("r");
        continued.OriginIndex.Should().Be(1);
        continued.Kind.Should().Be("resolve");
        continued.Root.Should().Be("r.customerId");
        continued.Aliases.Should().Equal("c");
        continued.ForTarget.Should().BeNull();
        continued.Stage.Resolve!.Select.Should().Equal("name");

        bound.Stages.ToList().IndexOf(continued).Should().Be(1, "the bound stages stay the caller's in order");
        bound.FinalShape.Carries("c").Should().BeTrue();
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Remote>().Which.SemiJoinable.Should().BeFalse();
    }

    [Theory]
    [InlineData(Contact)]
    [InlineData(Line)]
    public async Task A_lookup_on_a_keyed_or_remote_alias_binds_as_a_stage_continued_at_its_owner(string first)
    {
        var bound = await BoundAsync($$"""[{{first}}, { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "as": "l", "first": true } }]""");

        var continued = bound.Stages.OfType<ContinuedStage>().Should().ContainSingle().Subject;
        continued.Kind.Should().Be("lookup");
        continued.Root.Should().Be("r");
        continued.Aliases.Should().Equal("l");
    }

    [Fact]
    public async Task A_continued_alias_is_continued_under_again_with_the_same_anchor_and_neither_counts_as_this_hosts_stage()
    {
        var context = With(options => { options.Limits.MaxResolveStages = 1; options.Limits.MaxLookupStages = 1; });

        var bound = await BoundAsync($$"""
            [{{Contact}},
             { "resolve": { "path": "r.customerId", "as": "c" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "c", "as": "l" } },
             { "resolve": { "path": "c.id", "as": "cc", "parentAs": "p" } }]
            """, context);

        bound.Stages.OfType<ContinuedStage>().Select(stage => (stage.Anchor, stage.OriginIndex)).Should().Equal(("r", 1), ("r", 2), ("r", 3));
        bound.Stages.OfType<ContinuedStage>().Last().Aliases.Should().Equal("cc", "p");
    }

    [Fact]
    public async Task Every_variable_of_a_continued_stage_is_bound_at_the_origin_since_the_owner_is_never_sent_variables()
    {
        var bound = await BoundAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c", "filter": { "name": { "eq": { "$var": "who" } } } } }]""",
            variables: """{ "who": "Alice" }""");

        var sent = bound.Stages.OfType<ContinuedStage>().Single().Stage.Resolve!;
        sent.RawFilter!.Value.GetRawText().Should().Contain("\"Alice\"").And.NotContain("$var");

        var unbound = await ErrorAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c", "filter": { "name": { "eq": { "$var": "nobody" } } } } }]""", Codes.UnboundVariable);
        unbound.Stage.Should().Be(1);
    }

    [Fact]
    public async Task A_continued_alias_is_registered_at_the_origin_so_it_never_collides_with_one_of_this_host()
    {
        (await ErrorAsync($$"""[{{Contact}}, { "resolve": { "path": "customerId", "as": "c" } }, { "resolve": { "path": "r.customerId", "as": "c" } }]""", Codes.AliasCollision)).Stage.Should().Be(2);
        (await ErrorAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "number" } }]""", Codes.AliasCollision)).Stage.Should().Be(1);
        (await ErrorAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c", "parentAs": "c" } }]""", Codes.AliasCollision)).Stage.Should().Be(1);
    }

    // ---- forTarget ------------------------------------------------------------------------------------

    [Fact]
    public async Task ForTarget_names_one_target_of_a_union_and_a_stage_under_its_alias_inherits_it()
    {
        var bound = await BoundAsync($$"""
            [{{Union}},
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "as": "invoices" } },
             { "resolve": { "path": "invoices.customerId", "as": "c" } }]
            """);

        bound.Stages.OfType<ContinuedStage>().Select(stage => stage.ForTarget).Should().Equal("rc.shipment", "rc.shipment");
    }

    [Fact]
    public async Task ForTarget_naming_no_target_of_the_alias_or_contradicting_the_inherited_one_is_not_applicable()
    {
        (await ErrorAsync($$"""[{{Union}}, { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.customer", "as": "l" } }]""", Codes.OptionNotApplicable))
            .Message.Should().Contain("'rc.shipment', 'rc.tour', 'transport.shipment'");

        (await ErrorAsync($$"""
            [{{Union}},
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "as": "invoices" } },
             { "resolve": { "path": "invoices.customerId", "as": "c", "forTarget": "rc.tour" } }]
            """, Codes.OptionNotApplicable)).Message.Should().Contain("exists only on rows resolved to 'rc.shipment'");
    }

    [Fact]
    public async Task ForTarget_outside_a_continued_stage_stays_not_applicable()
    {
        await ErrorAsync("""[{ "resolve": { "path": "customerId", "as": "c", "forTarget": "rc.customer" } }]""", Codes.OptionNotApplicable);
        await ErrorAsync($$"""[{{Source}}, { "lookup": { "from": "rc.invoice", "path": "customerId", "forTarget": "rc.shipment", "as": "l" } }]""", Codes.OptionNotApplicable);
    }

    // ---- a flat owning-row select ------------------------------------------------------------------

    [Fact]
    public async Task A_flat_parentSelect_drops_per_local_target_what_its_row_lacks_and_sends_each_only_what_it_has()
    {
        var bound = await BoundAsync($$"""[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner", "parentSelect": ["id", "number", "name", "nope"] } }]""");

        var targets = bound.Stages.OfType<BoundStage.Resolve>().Single().Cases!.SelectMany(bound => bound.Targets).ToList();
        var shipment = targets.Single(target => target.Declared.Entity == "rc.shipment");
        var tour = targets.Single(target => target.Declared.Entity == "rc.tour");

        shipment.DroppedParentSelect.Should().Equal("name", "nope");
        shipment.RemoteParentSelect.Should().Equal("id", "number");
        tour.DroppedParentSelect.Should().Equal("number", "nope");
        tour.RemoteParentSelect.Should().Equal("id", "name");
        targets.Single(target => target.IsRemote).RemoteParentSelect.Should().Equal(["id", "number", "name", "nope"], "a remote target's owner decides what its row has");
    }

    [Fact]
    public async Task An_owning_row_select_path_no_local_target_has_is_UNKNOWN_PATH()
    {
        (await ErrorAsync("""[{ "resolve": { "path": "billingLineId", "as": "line", "parentAs": "owner", "parentSelect": ["number", "nope"] } }]""", Codes.UnknownPath))
            .Path.Should().Be("nope");
    }

    // ---- limits and refusals -------------------------------------------------------------------------

    [Fact]
    public async Task More_continued_stages_under_one_keyed_stage_than_MaxContinuedStages_is_refused()
    {
        var context = With(options => options.Limits.MaxContinuedStages = 1);
        var pipeline = $$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c" } }, { "resolve": { "path": "r.customerId", "as": "d" } }]""";

        var error = await ErrorAsync(pipeline, Codes.MaxContinuedStagesExceeded, context);

        error.Stage.Should().Be(2);
        await BoundAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c" } }]""", context);
    }

    [Fact]
    public async Task A_continuation_under_an_elements_all_alias_is_NOT_CONTINUABLE()
    {
        var resolve = await ErrorAsync("""[{ "resolve": { "path": "customerIds", "as": "cs", "elements": "all" } }, { "resolve": { "path": "cs.id", "as": "c" } }]""", Codes.NotContinuable);

        resolve.Message.Should().Contain("elements: \"all\"");
        resolve.Stage.Should().Be(1);

        await ErrorAsync("""[{ "resolve": { "path": "customerIds", "as": "cs", "elements": "all" } }, { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "cs", "as": "l" } }]""", Codes.NotContinuable);
    }

    [Theory]
    [InlineData("""{ "unwind": { "path": "r.lines" } }""", "'unwind'")]
    [InlineData("""{ "group": { "by": [{ "path": "r.name", "as": "n" }], "fields": { "k": { "count": true } } } }""", "'group'")]
    public async Task An_unwind_or_a_group_under_a_keyed_or_remote_alias_is_NOT_CONTINUABLE(string stage, string kind)
    {
        foreach (var first in new[] { Contact, Line })
        {
            var error = await ErrorAsync($$"""[{{first}}, {{stage}}]""", Codes.NotContinuable);

            error.Message.Should().Contain(kind).And.Contain("only resolve and lookup continue");
            error.Stage.Should().Be(1);
        }
    }

    [Fact]
    public async Task A_projection_that_keeps_a_continued_alias_but_drops_the_alias_it_continues_under_is_NOT_CONTINUABLE()
    {
        var error = await ErrorAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c" } }, { "project": { "number": 1, "c": 1 } }]""", Codes.NotContinuable);

        error.Stage.Should().Be(1);
        error.Message.Should().Contain("keep 'r'");

        await BoundAsync($$"""[{{Union}}, { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "as": "l" } }, { "project": { "owner": 1, "l": 1 } }]""");
    }

    [Fact]
    public async Task A_continuation_is_contract_2_and_a_contract_1_request_is_told_so()
    {
        var error = await ErrorAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c" } }]""", Codes.NotContinuable, BindHost.Context(contract: 1));

        error.Message.Should().EndWith(Binder.Contract1Hint);
    }

    [Fact]
    public async Task A_continued_stage_under_an_alias_the_projection_removed_is_UNKNOWN_PATH()
    {
        (await ErrorAsync($$"""[{{Contact}}, { "project": { "number": 1 } }, { "resolve": { "path": "r.customerId", "as": "c" } }]""", Codes.UnknownPath))
            .Message.Should().Contain("removed by the projection");
    }
}
