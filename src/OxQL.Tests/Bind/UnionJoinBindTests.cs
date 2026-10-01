using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The union join at bind time (improvement plan §3.U): <c>resolve { as, byTarget: { target: path } }</c>
/// under a keyed or remote alias with several targets binds as one <see cref="ContinuedStage"/> with a
/// branch per target; what is refused with which code and params; what a stage under its alias inherits;
/// how the splitter sends each target its branch as an ordinary resolve; and the union join under an
/// alias a continued stage added, which travels as written.
/// </summary>
public class UnionJoinBindTests
{
    private const string Invoice = ResolveModel.Invoice;

    /// <summary>Shipment and tour billing lines here, a transport line remote, with the owning row.</summary>
    private const string Union = """{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }""";

    /// <summary>A plain remote resolve onto <c>crm.contact</c>.</summary>
    private const string Contact = """{ "resolve": { "path": "contactId", "as": "r" } }""";

    private const string Driver = """{ "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": { "path": "owner.driverId" } } } }""";

    private static Task<BoundPipeline> BoundAsync(string pipeline, RequestContext? context = null, string? variables = null) =>
        BindHost.BoundAsync(ResolveModel.Model, Invoice, pipeline, context, variables);

    private static Task<QueryValidationError> ErrorAsync(string pipeline, string code, RequestContext? context = null) =>
        BindHost.ErrorAsync(ResolveModel.Model, Invoice, pipeline, code, context);

    private static BoundStage.Resolve Anchor(BoundPipeline bound, string alias) => bound.Stages.OfType<BoundStage.Resolve>().Single(stage => stage.As == alias);

    // ---- what binds -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_union_join_binds_as_one_continued_stage_with_a_branch_per_target_and_one_alias()
    {
        var bound = await BoundAsync($"[{Union}, {Driver}]", BindHost.Context(BindHost.Options(options => options.Limits.MaxContinuedStages = 1)));

        var continued = bound.Stages.OfType<ContinuedStage>().Should().ContainSingle("one stage, however many branches: it counts once under its keyed stage").Subject;
        continued.Anchor.Should().Be("line");
        continued.OriginIndex.Should().Be(1);
        continued.Aliases.Should().Equal("driver");
        continued.ForTarget.Should().BeNull();
        continued.Targets.Should().Equal("rc.shipment", "rc.tour");
        continued.Branches!.Select(branch => (branch.Target, branch.Path, branch.Elements)).Should().Equal(("rc.shipment", "owner.driverId", null), ("rc.tour", "owner.driverId", null));
        continued.Root.Should().Be("owner.driverId");
        continued.AppliesTo("rc.shipment").Should().BeTrue();
        continued.AppliesTo("transport.shipment").Should().BeFalse("a target without a branch is not sent the stage");

        bound.FinalShape.Roots["driver"].Should().BeOfType<ShapeNode.Remote>().Which.Should().Match<ShapeNode.Remote>(node => !node.SemiJoinable && node.TargetOpen);
        bound.Reads.Where(read => read.Stage == 1).Select(read => (read.Path, read.Use, read.Alias)).Should().Equal(
            ("owner.driverId", ReadUse.ResolveKey, "owner"), ("owner.driverId", ReadUse.ResolveKey, "owner"));
    }

    [Fact]
    public async Task The_splitter_sends_each_target_its_branch_as_an_ordinary_resolve_and_a_target_without_a_branch_nothing()
    {
        var bound = await BoundAsync($$"""
            [{{Union}},
             { "resolve": { "as": "driver", "select": ["name"], "onMissing": "report",
                            "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": { "path": "line.code", "elements": "first" } } } }]
            """);
        var anchor = Anchor(bound, "line");
        var continued = Continuation.Of(bound, anchor);

        var shipment = Continuation.For(anchor, "rc.shipment", itemTarget: true, continued).Stages.Should().ContainSingle().Subject.Resolve!;
        var tour = Continuation.For(anchor, "rc.tour", itemTarget: true, continued).Stages.Should().ContainSingle().Subject.Resolve!;

        JsonSerializer.Serialize(shipment, OxQLJson.Wire).Should().Be("""{"path":"driverId","as":"driver","select":["name"],"onMissing":"report"}""",
            "exactly what a forTarget stage sends: no byTarget, no forTarget, the branch's path on the owner's row");
        JsonSerializer.Serialize(tour, OxQLJson.Wire).Should().Be("""{"path":"oxEl.code","as":"driver","select":["name"],"elements":"first","onMissing":"report"}""");
        Continuation.For(anchor, "transport.shipment", itemTarget: true, continued).IsEmpty.Should().BeTrue();
        Continuation.For(anchor, "rc.shipment", true, continued).Aliases.Should().Equal("driver");
    }

    [Fact]
    public async Task A_stage_under_the_union_joins_alias_goes_where_the_alias_exists()
    {
        var bound = await BoundAsync($$"""[{{Union}}, {{Driver}}, { "resolve": { "path": "driver.companyId", "as": "company" } }]""");
        var anchor = Anchor(bound, "line");
        var continued = Continuation.Of(bound, anchor);

        continued[1].Targets.Should().Equal(["rc.shipment", "rc.tour"], "the alias exists on the rows of the targets the union join has a branch for");
        Continuation.For(anchor, "rc.tour", true, continued).Aliases.Should().Equal("driver", "company");
        Continuation.For(anchor, "transport.shipment", true, continued).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_branch_may_root_at_an_alias_continued_for_its_target()
    {
        var bound = await BoundAsync($$"""
            [{{Union}},
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "first": true, "as": "invoice" } },
             { "resolve": { "as": "who", "byTarget": { "rc.shipment": "invoice.customerId", "rc.tour": "owner.driverId" } } }]
            """);
        var anchor = Anchor(bound, "line");
        var shipment = Continuation.For(anchor, "rc.shipment", true, Continuation.Of(bound, anchor));

        shipment.Stages.Select(stage => stage.Resolve?.Path ?? "lookup").Should().Equal("lookup", "invoice.customerId");
        shipment.Aliases.Should().Equal("invoice", "who");
        Continuation.For(anchor, "rc.tour", true, Continuation.Of(bound, anchor)).Stages.Single().Resolve!.Path.Should().Be("driverId");
    }

    [Fact]
    public async Task Under_an_alias_a_continued_stage_added_the_targets_are_that_aliass_and_the_stage_travels_as_written()
    {
        var bound = await BoundAsync($$"""
            [{{Contact}},
             { "resolve": { "path": "r.partyId", "as": "party" } },
             { "resolve": { "as": "address", "byTarget": { "crm.person": "party.homeId", "crm.company": "party.seatId" } } }]
            """);
        var anchor = Anchor(bound, "r");
        var union = bound.Stages.OfType<ContinuedStage>().Last();

        union.Branches.Should().BeNull("this host cannot split it: only the owner of 'party' knows its targets");
        union.Targets.Should().BeNull();

        var sent = Continuation.For(anchor, "crm.contact", itemTarget: false, Continuation.Of(bound, anchor)).Stages.Last().Resolve!;

        sent.ByTarget!.Select(branch => (branch.Target, branch.Path)).Should().Equal(("crm.person", "party.homeId"), ("crm.company", "party.seatId"));
        sent.Path.Should().BeNull();
    }

    [Fact]
    public async Task The_canonical_form_carries_the_branches_and_the_targets_so_two_union_joins_never_share_a_cursor()
    {
        var bound = await BoundAsync($"[{Union}, {Driver}]");
        var stage = JsonNode.Parse(bound.Canonical)!["stages"]![1]!["continued"]!;

        stage["targets"]!.ToJsonString().Should().Be("""["rc.shipment","rc.tour"]""");
        stage["forTarget"].Should().BeNull();
        stage["stage"]!["resolve"]!["byTarget"]!.ToJsonString().Should().Be("""{"rc.shipment":"owner.driverId","rc.tour":"owner.driverId"}""", "a branch without elements is written as its path");

        var other = await BoundAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "line.code" } } }]""");
        other.Fingerprint.Should().NotBe(bound.Fingerprint);

        // A continued stage that is no union join renders as it did.
        var plain = await BoundAsync($$"""[{{Contact}}, { "resolve": { "path": "r.customerId", "as": "c" } }]""");
        JsonNode.Parse(plain.Canonical)!["stages"]![1]!["continued"]!.AsObject().ContainsKey("targets").Should().BeFalse();
    }

    [Fact]
    public async Task Every_variable_of_a_union_join_is_bound_at_the_origin()
    {
        var bound = await BoundAsync($$"""
            [{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "owner.driverId" }, "filter": { "name": { "eq": { "$var": "who" } } } } }]
            """, variables: """{ "who": "Alice" }""");
        var anchor = Anchor(bound, "line");
        var sent = Continuation.For(anchor, "rc.tour", true, Continuation.Of(bound, anchor)).Stages.Single().Resolve!;

        sent.RawFilter!.Value.GetRawText().Should().Contain("\"Alice\"").And.NotContain("$var");
    }

    [Fact]
    public void ByTarget_round_trips_on_the_wire_a_path_as_a_string_and_a_branch_with_elements_as_an_object()
    {
        const string Written = """{"as":"v","byTarget":{"a.x":"u.p","a.y":{"path":"u.q","elements":"all"}}}""";
        var stage = JsonSerializer.Deserialize<ResolveStage>(Written, OxQLJson.Wire)!;

        stage.ByTarget.Should().Equal(new ResolveBranch("a.x", "u.p"), new ResolveBranch("a.y", "u.q", "all"));
        stage.Malformed.Should().BeEmpty();
        JsonSerializer.Serialize(stage, OxQLJson.Wire).Should().Be(Written);
    }

    // ---- what is refused ------------------------------------------------------------------------------

    [Fact]
    public async Task One_branch_is_the_plain_form_and_is_refused_naming_it()
    {
        var error = await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId" } } }]""", Codes.OptionNotApplicable);

        error.Stage.Should().Be(1);
        error.Path.Should().Be("owner.driverId");
        error.Message.Should().Contain("'path' and 'forTarget'").And.Contain("\"forTarget\": \"rc.shipment\"");
        error.Params.Should().Contain("option", "byTarget").And.Contain("reason", "singleBranch").And.Contain("target", "rc.shipment").And.Contain("form", "forTarget");

        (await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": {} } }]""", Codes.OptionNotApplicable)).Params!["reason"].Should().Be("singleBranch");
    }

    [Theory]
    [InlineData("\"path\": \"owner.driverId\"", "withPath", "path")]
    [InlineData("\"elements\": \"first\"", "withElements", "elements")]
    [InlineData("\"forTarget\": \"rc.shipment\"", "withForTarget", "forTarget")]
    [InlineData("\"parentAs\": \"p\"", "withParentAs", "parentAs")]
    public async Task ByTarget_stands_alone_beside_as_select_filter_target_and_onMissing(string member, string reason, string name)
    {
        var error = await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", {{member}}, "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "owner.driverId" } } }]""", Codes.OptionNotApplicable);

        error.Stage.Should().Be(1);
        error.Params.Should().Contain("reason", reason).And.Contain("member", name);
    }

    [Fact]
    public async Task A_target_the_alias_does_not_have_is_refused_with_the_targets_it_has()
    {
        var error = await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.customer": "owner.driverId" } } }]""", Codes.OptionNotApplicable);

        error.Message.Should().Contain("'rc.customer'").And.Contain("'rc.shipment', 'rc.tour', 'transport.shipment'");
        error.Params.Should().Contain("reason", "notATarget").And.Contain("target", "rc.customer").And.Contain("alias", "line");
        ((IEnumerable<string>)error.Params!["targets"]!).Should().Equal("rc.shipment", "rc.tour", "transport.shipment");
    }

    [Fact]
    public async Task A_target_named_twice_is_refused()
    {
        var error = await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.shipment": "line.code" } } }]""", Codes.OptionNotApplicable);

        error.Params.Should().Contain("reason", "duplicateTarget").And.Contain("target", "rc.shipment");
    }

    [Fact]
    public async Task A_branch_on_this_hosts_row_is_not_a_union_join_and_an_unknown_root_is_an_unknown_path()
    {
        var local = await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "customerId" } } }]""", Codes.OptionNotApplicable);

        local.Params.Should().Contain("reason", "notContinued").And.Contain("target", "rc.tour");
        local.Path.Should().Be("customerId");

        (await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "nobody.x" } } }]""", Codes.UnknownPath)).Path.Should().Be("nobody.x");
    }

    [Fact]
    public async Task The_branches_continue_under_one_keyed_stage()
    {
        var error = await ErrorAsync($$"""[{{Union}}, {{Contact}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "r.customerId" } } }]""", Codes.OptionNotApplicable);

        error.Params.Should().Contain("reason", "anchors");
        ((IEnumerable<string>)error.Params!["aliases"]!).Should().Equal("line", "r");
    }

    [Fact]
    public async Task A_branch_rooted_at_an_alias_of_another_branch_is_refused()
    {
        var error = await ErrorAsync($$"""
            [{{Union}},
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "first": true, "as": "invoice" } },
             { "resolve": { "as": "who", "byTarget": { "rc.shipment": "invoice.customerId", "rc.tour": "invoice.customerId" } } }]
            """, Codes.OptionNotApplicable);

        error.Stage.Should().Be(2);
        error.Message.Should().Contain("belongs to the rows of 'rc.shipment'");
        error.Params.Should().Contain("reason", "otherBranch").And.Contain("target", "rc.tour").And.Contain("alias", "invoice");
    }

    [Theory]
    [InlineData("\"all\"", "\"first\"", "rc.tour", "all")]
    [InlineData("\"first\"", "\"all\"", "rc.tour", "one")]
    public async Task Branches_of_one_and_of_many_records_are_UNION_CARDINALITY_MISMATCH_naming_the_branch_to_change(string first, string second, string branch, string expected)
    {
        var error = await ErrorAsync($$"""
            [{{Union}}, { "resolve": { "as": "driver", "byTarget": { "rc.shipment": { "path": "owner.driverId", "elements": {{first}} }, "rc.tour": { "path": "owner.driverId", "elements": {{second}} } } } }]
            """, Codes.UnionCardinalityMismatch);

        error.Stage.Should().Be(1);
        error.Message.Should().Contain($"Change the branch of '{branch}'");
        error.Params.Should().Contain("alias", "driver").And.Contain("branch", branch).And.Contain("expected", expected);
    }

    [Fact]
    public async Task Every_branch_all_binds_and_nothing_continues_under_the_array()
    {
        const string Many = """{ "resolve": { "as": "drivers", "byTarget": { "rc.shipment": { "path": "owner.driverId", "elements": "all" }, "rc.tour": { "path": "owner.driverId", "elements": "all" } } } }""";

        await BoundAsync($"[{Union}, {Many}]");
        (await ErrorAsync($$"""[{{Union}}, {{Many}}, { "resolve": { "path": "drivers.companyId", "as": "c" } }]""", Codes.NotContinuable)).Stage.Should().Be(2);
    }

    [Fact]
    public async Task The_alias_is_defined_once_and_checked_like_any_alias()
    {
        (await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "owner", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "owner.driverId" } } }]""", Codes.AliasCollision)).Stage.Should().Be(1);
        (await ErrorAsync($$"""[{{Union}}, {{Driver}}, {{Driver}}]""", Codes.AliasCollision)).Stage.Should().Be(2);
    }

    [Fact]
    public async Task The_stage_counts_once_towards_MaxContinuedStages()
    {
        var context = BindHost.Context(BindHost.Options(options => options.Limits.MaxContinuedStages = 1));

        (await ErrorAsync($$"""[{{Union}}, {{Driver}}, { "resolve": { "path": "owner.driverId", "as": "again", "forTarget": "rc.tour" } }]""", Codes.MaxContinuedStagesExceeded, context)).Stage.Should().Be(2);
    }

    [Theory]
    [InlineData("\"owner.driverId\"")]
    [InlineData("""{ "rc.shipment": 1, "rc.tour": "owner.driverId" }""")]
    [InlineData("""{ "rc.shipment": { "elements": "first" }, "rc.tour": "owner.driverId" }""")]
    [InlineData("""{ "rc.shipment": { "path": "owner.driverId", "elements": "some" }, "rc.tour": "owner.driverId" }""")]
    [InlineData("""{ "rc.shipment": { "path": "owner.driverId", "select": ["name"] }, "rc.tour": "owner.driverId" }""")]
    public async Task A_byTarget_that_is_not_an_object_of_paths_is_refused_as_a_malformed_member(string value)
    {
        var error = await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "driver", "byTarget": {{value}} } }]""", Codes.UnknownStageMember);

        error.Message.Should().StartWith("A resolve's 'byTarget' is an object with one member per target");
    }

    [Fact]
    public async Task ByTarget_is_contract_2_and_a_contract_1_request_is_told_so()
    {
        var error = await ErrorAsync($"[{Driver}]", Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));

        error.Message.Should().StartWith("'byTarget' is not a member of resolve").And.EndWith(Binder.Contract1Hint);
    }
}
