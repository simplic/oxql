using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Core;

/// <summary>
/// The splitter of remote continuation (DESIGN §3.5.3, §3.5.4): the continued stages per target in
/// pipeline order, the root rewrites onto the owner's row, the owner-to-origin index map, and the
/// property that ends every chain — a forwarded query carries strictly fewer join stages than the
/// query it came from.
/// </summary>
public class ContinuationSplitterTests
{
    private const string Union = """{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }""";

    private static Task<BoundPipeline> BoundAsync(string pipeline) => BindHost.BoundAsync(ResolveModel.Model, ResolveModel.Invoice, pipeline);

    private static BoundStage.Resolve Anchor(BoundPipeline bound, string alias) => bound.Stages.OfType<BoundStage.Resolve>().Single(stage => stage.As == alias);

    [Fact]
    public async Task On_an_item_target_the_alias_becomes_the_element_and_the_owning_row_the_owners_row()
    {
        var bound = await BoundAsync($$"""
            [{{Union}},
             { "resolve": { "path": "line.code", "as": "a" } },
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "as": "b" } },
             { "resolve": { "path": "owner.number", "as": "d", "forTarget": "rc.shipment" } }]
            """);
        var anchor = Anchor(bound, "line");

        // A lookup on 'line' itself, the element, is refused at the origin (RE-20); the owning row is the entity row.
        var owner = Continuation.For(anchor, "rc.shipment", itemTarget: true, Continuation.Of(bound, anchor));

        owner.Stages.Select(stage => stage.Resolve?.Path ?? $"on:{stage.Lookup!.On ?? "(root)"}").Should().Equal("oxEl.code", "on:(root)", "number");
        owner.Stages.Should().OnlyContain(stage => (stage.Resolve == null || stage.Resolve.ForTarget == null) && (stage.Lookup == null || stage.Lookup.ForTarget == null),
            "the owner does not continue the stage further, so it is not sent forTarget");
        owner.Origins.Select(stage => stage.OriginIndex).Should().Equal(1, 2, 3);
        owner.Aliases.Should().Equal("a", "b", "d");
    }

    [Fact]
    public async Task ForTarget_keeps_a_stage_out_of_every_other_targets_owner_query()
    {
        var bound = await BoundAsync($$"""
            [{{Union}},
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "as": "invoices" } },
             { "resolve": { "path": "invoices.customerId", "as": "c" } },
             { "resolve": { "path": "line.code", "as": "code" } }]
            """);
        var anchor = Anchor(bound, "line");
        var continued = Continuation.Of(bound, anchor);

        Continuation.For(anchor, "rc.shipment", true, continued).Aliases.Should().Equal("invoices", "c", "code");
        Continuation.For(anchor, "rc.tour", true, continued).Aliases.Should().Equal(["code"], "a stage under a forTarget alias inherits its target");
        Continuation.For(anchor, "rc.tour", true, continued).Origins.Single().OriginIndex.Should().Be(3);
    }

    [Fact]
    public async Task On_an_entity_target_the_alias_is_the_owners_row_and_a_continued_alias_keeps_its_name()
    {
        var bound = await BoundAsync("""
            [{ "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "r.customerId", "as": "c" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "as": "l" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "c", "as": "m" } },
             { "resolve": { "path": "c.id", "as": "n" } }]
            """);
        var anchor = Anchor(bound, "r");

        var owner = Continuation.For(anchor, "crm.contact", itemTarget: false, Continuation.Of(bound, anchor));

        owner.Stages.Select(stage => stage.Resolve?.Path ?? $"on:{stage.Lookup!.On ?? "(root)"}").Should().Equal("customerId", "on:(root)", "on:c", "c.id");
    }

    [Theory]
    [InlineData("oxEl.code", "line.code", true)]
    [InlineData("oxEl", "line", true)]
    [InlineData("name", "name", true)]
    [InlineData(null, "line.code", true)]
    public async Task An_owner_error_path_maps_back_to_the_path_the_caller_wrote(string? ownerPath, string expected, bool item)
    {
        var bound = await BoundAsync($$"""[{{Union}}, { "resolve": { "path": "line.code", "as": "a" } }]""");
        var anchor = Anchor(bound, "line");
        var origin = Continuation.Of(bound, anchor).Single();

        Continuation.ToOrigin(ownerPath, origin, anchor, item).Should().Be(expected);
    }

    // ---- termination ---------------------------------------------------------------------------------

    public static TheoryData<string> Chains => new()
    {
        """[{ "resolve": { "path": "contactId", "as": "r" } }, { "resolve": { "path": "r.customerId", "as": "c" } }]""",
        """[{ "resolve": { "path": "customerId", "as": "local" } }, { "resolve": { "path": "contactId", "as": "r" } }, { "resolve": { "path": "r.a", "as": "a" } }, { "resolve": { "path": "a.b", "as": "b" } }, { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "b", "as": "c" } }]""",
        $$"""[{{Union}}, { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "as": "i", "first": true } }, { "resolve": { "path": "i.billingLineId", "as": "l2", "parentAs": "o2" } }, { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "o2", "as": "i2" } }]""",
        """[{ "resolve": { "path": "billingLineId", "as": "x", "parentAs": "p" } }, { "resolve": { "path": "contactId", "as": "y" } }, { "resolve": { "path": "x.code", "as": "xc" } }, { "resolve": { "path": "y.code", "as": "yc" } }]""",
    };

    /// <summary>
    /// The property that ends a chain (DESIGN §3.5.4): the query forwarded for any keyed stage and any
    /// of its targets holds only the stages continued under that stage, never the stage itself or any
    /// before it, so it carries strictly fewer join stages than the query it came from. The owner binds
    /// it as an ordinary query and splits it the same way, so every level of a chain — across services
    /// or in-process — has fewer join stages than the one above it, and a chain ends after at most as
    /// many levels as the first request has join stages (≤ MaxPipelineStages).
    /// </summary>
    [Theory]
    [MemberData(nameof(Chains))]
    public async Task A_forwarded_query_carries_strictly_fewer_join_stages_than_the_query_it_came_from(string pipeline)
    {
        var request = BindHost.Request(ResolveModel.Invoice, pipeline);
        var bound = await BoundAsync(pipeline);
        var joins = Joins(request.Pipeline);
        var forwarded = 0;

        foreach (var anchor in bound.Stages.OfType<BoundStage.Resolve>().Where(stage => stage.Executor == ResolveExecutor.Keyed))
            foreach (var target in anchor.Cases!.SelectMany(bound => bound.Targets))
            {
                var owner = Continuation.For(anchor, target.Declared.Entity, target.Declared.Item is not null, Continuation.Of(bound, anchor));

                Joins(owner.Stages).Should().BeLessThan(joins, $"'{anchor.As}' forwards to {target.Declared.Entity} only what continues under it");
                Joins(owner.Stages).Should().BeLessThanOrEqualTo(joins - 1 - request.Pipeline.Take(bound.Stages.ToList().IndexOf(anchor)).Count(IsJoin),
                    "neither the keyed stage nor any stage before it travels");
                forwarded++;
            }

        forwarded.Should().BePositive();
    }

    private static bool IsJoin(PipelineStage stage) => stage.Resolve is not null || stage.Lookup is not null;

    private static int Joins(IEnumerable<PipelineStage> stages) => stages.Count(IsJoin);
}
