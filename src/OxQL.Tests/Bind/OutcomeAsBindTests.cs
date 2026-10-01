using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The per-row outcome at bind and compile time (improvement plan §3.O): <c>outcomeAs</c> on a resolve
/// names a root that carries what became of the reference on every row. It is an alias like any other;
/// it follows its join (an inline resolve's is written by the aggregate and may be filtered, grouped by
/// and counted; a keyed, remote or continued one is projected only); it never orders a page; a stage
/// that names one reads its outcomes; and it changes the fingerprint.
/// </summary>
public class OutcomeAsBindTests
{
    private const string Invoice = ResolveModel.Invoice;

    /// <summary>An inline resolve onto the key of a local entity.</summary>
    private const string Customer = """{ "resolve": { "path": "customerId", "as": "customer", "outcomeAs": "customerOutcome" } }""";

    /// <summary>An inline resolve onto a member that is not the key: two customers may share a code.</summary>
    private const string ByCode = """{ "resolve": { "path": "customerCode", "as": "customer", "outcomeAs": "customerOutcome" } }""";

    /// <summary>A plain remote resolve.</summary>
    private const string Contact = """{ "resolve": { "path": "contactId", "as": "r", "outcomeAs": "rOutcome" } }""";

    private const string Union = """{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }""";

    private static Task<BoundPipeline> BoundAsync(string pipeline, RequestContext? context = null) => BindHost.BoundAsync(ResolveModel.Model, Invoice, pipeline, context);

    private static Task<QueryValidationError> ErrorAsync(string pipeline, string code, RequestContext? context = null) =>
        BindHost.ErrorAsync(ResolveModel.Model, Invoice, pipeline, code, context);

    private static CompiledQuery Compile(BoundPipeline bound) => MongoCompiler.Compile(bound, new CompileOptions(5_000, null, 10_000));

    // ---- the root ---------------------------------------------------------------------------------------

    [Fact]
    public async Task OutcomeAs_names_a_root_of_the_row_that_follows_its_join()
    {
        var inline = await BoundAsync($"[{Customer}]");

        inline.Stages.OfType<BoundStage.Resolve>().Single().OutcomeAs.Should().Be("customerOutcome");
        inline.FinalShape.Roots["customerOutcome"].Should().Be(new ShapeNode.Outcome("customer", InAggregate: true, "customerOutcome"));

        var remote = await BoundAsync($"[{Contact}]");

        remote.FinalShape.Roots["rOutcome"].Should().Be(new ShapeNode.Outcome("r", InAggregate: false, "rOutcome"));

        var keyed = await BoundAsync("""[{ "resolve": { "path": "billingLineId", "as": "line", "outcomeAs": "lineOutcome" } }]""");

        keyed.FinalShape.Roots["lineOutcome"].Should().Be(new ShapeNode.Outcome("line", InAggregate: false, "lineOutcome"), "an item target is joined after the page");
    }

    [Fact]
    public async Task A_continued_stage_and_a_union_join_add_the_outcome_to_the_aliases_their_owners_answer()
    {
        var bound = await BoundAsync($$"""
            [{{Union}},
             { "resolve": { "path": "owner.driverId", "as": "driver", "forTarget": "rc.tour", "outcomeAs": "driverOutcome" } },
             { "resolve": { "as": "who", "outcomeAs": "whoOutcome", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "owner.driverId" } } }]
            """);
        var continued = bound.Stages.OfType<ContinuedStage>().ToList();

        continued[0].Aliases.Should().Equal("driver", "driverOutcome");
        continued[1].Aliases.Should().Equal("who", "whoOutcome");
        bound.FinalShape.Roots["driverOutcome"].Should().Be(new ShapeNode.Outcome("driver", InAggregate: false, "driverOutcome"));
        bound.FinalShape.Roots["whoOutcome"].Should().Be(new ShapeNode.Outcome("who", InAggregate: false, "whoOutcome"));

        var anchor = bound.Stages.OfType<BoundStage.Resolve>().Single();
        var tour = Continuation.For(anchor, "rc.tour", itemTarget: true, Continuation.Of(bound, anchor));

        tour.Stages.Select(stage => stage.Resolve!.OutcomeAs).Should().Equal(["driverOutcome", "whoOutcome"], "the owner is sent the member, and writes it on the rows it answers");
        tour.Aliases.Should().Equal("driver", "driverOutcome", "who", "whoOutcome");

        // Nothing continues under an outcome: it is a string, not a row.
        (await ErrorAsync($$"""[{{Union}}, { "resolve": { "path": "owner.driverId", "as": "driver", "outcomeAs": "o" } }, { "resolve": { "path": "o.x", "as": "y" } }]""", Codes.UnknownPath)).Stage.Should().Be(2);
    }

    [Theory]
    [InlineData("""{ "resolve": { "path": "customerId", "as": "c", "outcomeAs": "c" } }""", Codes.AliasCollision)]
    [InlineData("""{ "resolve": { "path": "customerId", "as": "c", "outcomeAs": "number" } }""", Codes.AliasCollision)]
    [InlineData("""{ "resolve": { "path": "billingLineId", "as": "l", "parentAs": "p", "outcomeAs": "p" } }""", Codes.AliasCollision)]
    [InlineData("""{ "resolve": { "path": "customerId", "as": "c", "outcomeAs": "not an alias" } }""", Codes.InvalidAlias)]
    [InlineData("""{ "resolve": { "path": "customerId", "as": "c", "outcomeAs": 1 } }""", Codes.UnknownStageMember)]
    [InlineData("""{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "l", "outcomeAs": "o" } }""", Codes.UnknownStageMember)]
    public async Task The_name_is_an_alias_like_any_other_and_a_lookup_has_no_outcome(string stage, string code)
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, stage.Contains("lookup", StringComparison.Ordinal) ? "rc.customer" : Invoice, $"[{stage}]", code);

        error.Stage.Should().Be(0);
    }

    [Fact]
    public async Task A_second_stage_cannot_take_the_name_and_a_continued_stage_checks_it_at_the_origin()
    {
        (await ErrorAsync($$"""[{{Customer}}, { "resolve": { "path": "contactId", "as": "r", "outcomeAs": "customerOutcome" } }]""", Codes.AliasCollision)).Stage.Should().Be(1);
        (await ErrorAsync($$"""[{{Union}}, { "resolve": { "path": "owner.driverId", "as": "driver", "outcomeAs": "driver" } }]""", Codes.AliasCollision)).Stage.Should().Be(1);
        (await ErrorAsync($$"""[{{Union}}, { "resolve": { "as": "who", "outcomeAs": "line", "byTarget": { "rc.shipment": "owner.driverId", "rc.tour": "owner.driverId" } } }]""", Codes.AliasCollision)).Stage.Should().Be(1);
    }

    [Fact]
    public async Task OutcomeAs_is_contract_2()
    {
        var error = await ErrorAsync($"[{Customer}]", Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));

        error.Message.Should().StartWith("'outcomeAs' is not a member of resolve").And.EndWith(Binder.Contract1Hint);
    }

    // ---- what reads it ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_inline_outcome_is_filtered_grouped_and_counted_and_compares_exactly()
    {
        var matched = await BoundAsync($$"""[{{Customer}}, { "match": { "customerOutcome": { "eq": "not_found" } } }]""");
        var leaf = matched.Stages.OfType<BoundStage.Match>().Single().Condition.Should().BeOfType<BoundCondition.Leaf>().Subject;

        leaf.IgnoreCase.Should().BeFalse("an outcome is one of a closed list of names; nothing folds");
        leaf.Path.Storage.Should().Be("customerOutcome");
        matched.Collated.Should().BeFalse("and it does not turn the aggregate collated");
        matched.Reads.Should().Contain(read => read.Stage == 1 && read.Path == "customerOutcome" && read.Use == ReadUse.Match && read.Alias == null);

        await BoundAsync($$"""[{{Customer}}, { "match": { "customerOutcome": { "in": ["not_found", "reference_null"] } } }]""");
        await BoundAsync($$"""[{{Customer}}, { "match": { "not": { "customerOutcome": { "neq": "resolved" } } } }]""");

        var grouped = await BoundAsync($$"""[{{Customer}}, { "group": { "by": [{ "path": "customerOutcome", "as": "outcome" }], "fields": { "lines": { "count": true } } } }]""");

        grouped.Collated.Should().BeFalse();
        grouped.FinalShape.Roots.Keys.Should().BeEquivalentTo(["outcome", "lines"], "a group ends the root like any alias");
    }

    [Theory]
    [InlineData("""{ "customerOutcome": { "eq": "missing" } }""", Codes.UnknownEnumMember)]
    [InlineData("""{ "customerOutcome": { "in": ["not_found", "gone"] } }""", Codes.UnknownEnumMember)]
    [InlineData("""{ "customerOutcome": { "gt": "not_found" } }""", Codes.InvalidOperand)]
    [InlineData("""{ "customerOutcome": { "contains": "found" } }""", Codes.InvalidOperand)]
    [InlineData("""{ "customerOutcome": { "eq": null } }""", Codes.InvalidOperand)]
    [InlineData("""{ "customerOutcome": { "eq": "not_found", "options": { "caseSensitive": true } } }""", Codes.OptionNotApplicable)]
    [InlineData("""{ "customerOutcome.x": { "eq": "not_found" } }""", Codes.UnknownPath)]
    public async Task A_condition_on_an_outcome_takes_eq_neq_in_nin_and_the_outcomes_as_the_wire_spells_them(string condition, string code)
    {
        var error = await ErrorAsync($$"""[{{Customer}}, { "match": {{condition}} }]""", code);

        error.Stage.Should().Be(1);

        if (code == Codes.UnknownEnumMember)
            ((IEnumerable<string>)error.Params!["values"]!).Should().Equal(Notes.Outcomes);
    }

    [Fact]
    public async Task An_outcome_never_orders_a_page()
    {
        foreach (var first in new[] { Customer, Contact })
        {
            var name = first == Customer ? "customerOutcome" : "rOutcome";
            var error = await ErrorAsync($$"""[{{first}}, { "sort": [{ "{{name}}": "asc" }] }]""", Codes.ResolveNotSortable);

            error.Stage.Should().Be(1);
            error.Message.Should().Contain("does not order the page");
        }
    }

    [Fact]
    public async Task An_outcome_written_after_the_page_is_projected_only()
    {
        (await ErrorAsync($$"""[{{Contact}}, { "match": { "rOutcome": { "eq": "not_found" } } }]""", Codes.ResolveNotFilterable)).Message.Should().Contain("joined after the page");
        (await ErrorAsync($$"""[{{Contact}}, { "group": { "by": [{ "path": "rOutcome", "as": "o" }], "fields": { "n": { "count": true } } } }]""", Codes.UnknownPath)).Stage.Should().Be(1);

        var bound = await BoundAsync($$"""[{{Contact}}, { "project": { "number": 1, "rOutcome": 1 } }]""");

        bound.FinalShape.Carries("rOutcome").Should().BeTrue();
        bound.FinalShape.Carries("r").Should().BeFalse();
        MongoCompiler.KeyedRuns(bound, bound.Stages.OfType<BoundStage.Resolve>().Single()).Should().BeTrue("a projected outcome keeps its join alive");
        bound.Loads["r"].Shows.Should().BeEmpty("with only the key loaded");
        bound.Stages.OfType<BoundStage.Resolve>().Single().Cases!.Single().Targets.Single().RemoteSelect.Should().BeEmpty();
    }

    // ---- the fingerprint ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_outcome_enters_the_canonical_form_and_the_fingerprint()
    {
        var with = await BoundAsync($"[{Customer}]");
        var without = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "customer" } }]""");

        JsonNode.Parse(with.Canonical)!["stages"]![0]!["resolve"]!["outcomeAs"]!.GetValue<string>().Should().Be("customerOutcome");
        JsonNode.Parse(without.Canonical)!["stages"]![0]!["resolve"]!.AsObject().ContainsKey("outcomeAs").Should().BeFalse("a resolve without one renders as it did");
        with.Fingerprint.Should().NotBe(without.Fingerprint, "the row has one more member: a cursor of the other request is not this one's");

        JsonSerializer.Serialize(JsonSerializer.Deserialize<ResolveStage>("""{"path":"p","as":"a","outcomeAs":"o"}""", OxQLJson.Wire), OxQLJson.Wire)
            .Should().Be("""{"path":"p","as":"a","outcomeAs":"o"}""");
    }

    // ---- what the aggregate writes ------------------------------------------------------------------------

    private static BsonDocument SetOf(CompiledQuery compiled, string member) =>
        compiled.PageStages.Single(stage => stage.Contains("$set") && stage["$set"].AsBsonDocument.Contains(member))["$set"].AsBsonDocument;

    private static List<string> Outcomes(BsonDocument set, string member) =>
        [.. set[member]["$switch"]["branches"].AsBsonArray.Select(branch => branch["then"].AsString), set[member]["$switch"]["default"].AsString];

    [Fact]
    public async Task The_aggregate_writes_an_inline_outcome_on_every_row()
    {
        var compiled = Compile(await BoundAsync($"[{Customer}]"));
        var set = SetOf(compiled, "customerOutcome");

        Outcomes(set, "customerOutcome").Should().Equal(["reference_null", "resolved", "not_found"], "a key target has no second record and no filter excludes one");
        set["customerOutcome"]["$switch"]["branches"][0]["case"].ToJson().Should().Contain("\"$CustomerId\"", "a reference that holds nothing is reference_null");
        compiled.InlineProbes.Should().BeEmpty("nothing is reported that was not reported before");
    }

    [Fact]
    public async Task A_target_field_that_is_not_the_key_takes_the_second_record_and_says_ambiguous()
    {
        var compiled = Compile(await BoundAsync($"[{ByCode}]"));

        Outcomes(SetOf(compiled, "customerOutcome"), "customerOutcome").Should().Equal("reference_null", "ambiguous", "resolved", "not_found");
        compiled.PageStages.Single(stage => stage.Contains("$lookup"))["$lookup"]["pipeline"].AsBsonArray.Should().Contain(stage => stage.AsBsonDocument.Contains("$limit") && stage["$limit"] == 2);
        compiled.InlineProbes.Should().ContainSingle("a stage that names its outcome reads its outcomes: the ambiguity it says is reported as well").Which.AmbiguityFlag.Should().NotBeNull();

        var silent = Compile(await BoundAsync("""[{ "resolve": { "path": "customerCode", "as": "customer" } }]"""));

        silent.InlineProbes.Should().BeEmpty("without the member nobody looks for a second record");
    }

    [Fact]
    public async Task A_filter_tells_an_excluded_record_from_a_missing_one_with_the_existence_join()
    {
        var compiled = Compile(await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "customer", "outcomeAs": "customerOutcome", "filter": { "name": { "eq": "Alice" } } } }]"""));

        Outcomes(SetOf(compiled, "customerOutcome"), "customerOutcome").Should().Equal("reference_null", "resolved", "excluded", "not_found");
        compiled.PageStages.Count(stage => stage.Contains("$lookup")).Should().Be(2, "the join, and the same key match without the filter");
        compiled.PageStages.SelectMany(stage => stage.Contains("$unset") ? stage["$unset"] is BsonArray names ? names.Select(name => name.AsString) : [stage["$unset"].AsString] : [])
            .Should().Contain(["customer__arr", "customerHas__arr"], "the temporary fields leave the row");
    }

    [Fact]
    public async Task A_stage_that_reads_the_outcome_keeps_the_join_before_the_page_and_in_the_count()
    {
        var late = await BoundAsync($$"""[{{Customer}}, { "sort": [{ "number": "asc" }] }]""");
        var early = await BoundAsync($$"""[{{Customer}}, { "match": { "customerOutcome": { "eq": "not_found" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 10, "includeTotalCount": true } }]""");

        MongoCompiler.JoinsAfterPage(late.Stages, 0, late.Stages.OfType<BoundStage.Resolve>().Single()).Should().BeTrue("nothing reads it: the join is paid per page row");
        MongoCompiler.JoinsAfterPage(early.Stages, 0, early.Stages.OfType<BoundStage.Resolve>().Single()).Should().BeFalse("a condition on the outcome needs it on every candidate row");

        var compiled = Compile(early);
        var stages = compiled.PageStages.Select(stage => stage.Names.First()).ToList();

        stages.IndexOf("$lookup").Should().BeLessThan(stages.IndexOf("$limit"));
        compiled.CountStages!.Should().Contain(stage => stage.Contains("$lookup"), "the count counts the rows the condition keeps");
    }

    [Fact]
    public async Task A_projected_outcome_keeps_an_inline_join_alive_and_loads_only_the_key()
    {
        var bound = await BoundAsync($$"""[{{Customer}}, { "project": { "number": 1, "customerOutcome": 1 } }]""");
        var compiled = Compile(bound);

        compiled.PageStages.Should().Contain(stage => stage.Contains("$lookup"), "the row shows the outcome, so the join runs although the row drops its alias");
        bound.Loads["customer"].Loads.Should().Equal("id");
        bound.Loads["customer"].Shows.Should().BeEmpty();

        var dropped = Compile(await BoundAsync($$"""[{{Customer}}, { "project": { "number": 1 } }]"""));

        dropped.PageStages.Should().NotContain(stage => stage.Contains("$lookup"), "neither the alias nor its outcome is shown or read: the join is not run");
    }

    // ---- what explain says -------------------------------------------------------------------------------

    [Fact]
    public async Task The_missing_policy_note_names_the_member_and_what_a_refusing_join_leaves_of_it()
    {
        var bound = await BoundAsync($"[{Customer}]");
        var request = BindHost.Request(Invoice, $"[{Customer}]");
        var note = Notes.Of(bound, request, Notes.CallerIndexes(bound, request), strict: false, 2, BindHost.Options()).Single(each => each.Code == Notes.MissingPolicy);

        note.Params!["outcomeAs"].Should().Be("customerOutcome");
        note.Message.Should().EndWith("'customerOutcome' carries the outcome of every row.");

        var strict = Notes.Of(bound, request, Notes.CallerIndexes(bound, request), strict: true, 2, BindHost.Options()).Single(each => each.Code == Notes.MissingPolicy);

        strict.Message.Should().Contain("a row that loses data refuses the request");

        var plain = await BoundAsync("""[{ "resolve": { "path": "customerId", "as": "customer" } }]""");
        var plainRequest = BindHost.Request(Invoice, """[{ "resolve": { "path": "customerId", "as": "customer" } }]""");

        Notes.Of(plain, plainRequest, Notes.CallerIndexes(plain, plainRequest), strict: false, 2, BindHost.Options()).Single(each => each.Code == Notes.MissingPolicy)
            .Params!.ContainsKey("outcomeAs").Should().BeFalse("a join without the member is noted as it was");
    }

    [Fact]
    public void The_outcomes_a_join_can_have_follow_how_it_runs()
    {
        Notes.OutcomesOf(inline: true, continued: false, nonKey: false, filtered: false).Should().Equal("resolved", "reference_null", "not_found");
        Notes.OutcomesOf(inline: true, continued: false, nonKey: true, filtered: true).Should().Equal("resolved", "ambiguous", "reference_null", "excluded", "not_found");
        Notes.OutcomesOf(inline: false, continued: false).Should().Equal("resolved", "ambiguous", "reference_null", "excluded", "not_found", "invalid_key", "owner_unanswered");
        Notes.OutcomesOf(inline: false, continued: true).Should().Equal(Notes.Outcomes);
    }
}
