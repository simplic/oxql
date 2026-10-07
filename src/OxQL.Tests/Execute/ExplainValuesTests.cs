using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// What explain says of distinct values (DG2 set filters): of a column, whether a <c>values</c> stage
/// over it binds (<c>can.values</c>), which is the binder's own test and so holds for every path of a
/// model; and of a request that ends in <c>values</c>, what the stage answers, in which order, what
/// it reads, and every refusal with what would have to change for it to run (<c>basis</c>).
/// </summary>
public class ExplainValuesTests
{
    private const string Order = "probe.order";
    private const string Invoice = ResolveModel.Invoice;

    private static async Task<ExplainResult> ExplainAsync(string pipeline, string entity = Order, EntityModel? model = null, bool plan = false, string? filter = null)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(model ?? BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var query = BindHost.Parse($$"""{ "entityType": "{{entity}}", {{(filter is null ? "" : $"\"filter\": {filter},")}} "pipeline": {{pipeline}} }""");
        var outcome = await engine.ExplainAsync(plan ? query.Planned() : query, BindHost.Context());

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    private static JsonObject CanOf(ExplainResult answer, string path) =>
        answer.Result!.Capabilities![answer.Result.Columns.Single(column => column.Path == path).Can!]!.AsObject();

    // ---- can.values ---------------------------------------------------------------------------

    [Fact]
    public async Task A_member_under_a_collection_that_is_not_unwound_has_values_though_no_group_names_it()
    {
        var answer = await ExplainAsync("""[{ "project": { "number": 1, "state": 1 } }]""");

        answer.Valid.Should().BeTrue();
        CanOf(answer, "number")["can"]!["values"]!.GetValue<bool>().Should().BeTrue();
        CanOf(answer, "state")["can"]!["values"]!.GetValue<bool>().Should().BeTrue();
        // A member below a collection is no column of its own; its answer is the same function of the row.
        var shape = (await BindHost.BoundAsync(BindHost.Probe, Order, "[]")).FinalShape;

        ExplainFacts.Can(shape, "shipTo").Can["values"]!.GetValue<bool>().Should().BeFalse();
        ExplainFacts.Can(shape, "shipTo").Why!["values"]!.GetValue<string>().Should().Be("notScalar");
        ExplainFacts.Can(shape, "tags").Can["values"]!.GetValue<bool>().Should().BeFalse("a collection itself is no scalar");
        ExplainFacts.Can(shape, "tags").Why!["values"]!.GetValue<string>().Should().Be("notScalar");

        foreach (var path in new[] { "items.quantity", "items.stops.city" })
        {
            var (can, why) = ExplainFacts.Can(shape, path);

            can["values"]!.GetValue<bool>().Should().BeTrue($"{path}: the values stage unwinds the collections above it for itself");
            can["group"]!.GetValue<bool>().Should().BeFalse(path);
            why!["group"]!.GetValue<string>().Should().Be("underCollection");
            why.ContainsKey("values").Should().BeFalse("nothing is refused");
        }

        // After its unwind the member is a scalar of the row: a group key and a path of values alike.
        var unwound = (await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "unwind": { "path": "items", "as": "item" } }]""")).FinalShape;

        ExplainFacts.Can(unwound, "item.quantity").Can.ToJsonString().Should().Contain("\"group\":true").And.Contain("\"values\":true");
        ExplainFacts.Can(unwound, "items.quantity").Why!["values"]!.GetValue<string>().Should().Be("notInRow", "the collection left the row with its unwind");
    }

    [Fact]
    public async Task A_column_another_service_holds_or_one_joined_after_the_page_has_none()
    {
        var answer = await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c" } },
             { "resolve": { "path": "contactId", "as": "r" } },
             { "resolve": { "path": "billingLineId", "as": "bl" } },
             { "project": { "number": 1, "c.name": 1, "r.name": 1, "bl.amount": 1 } }]
            """, Invoice, ResolveModel.Model);

        CanOf(answer, "c.name")["can"]!["values"]!.GetValue<bool>().Should().BeTrue("a record joined in the aggregate is in the row the stage reads");
        CanOf(answer, "r.name")["can"]!["values"]!.GetValue<bool>().Should().BeFalse();
        CanOf(answer, "r.name")["why"]!["values"]!.GetValue<string>().Should().Be("owner");
        CanOf(answer, "bl.amount")["why"]!["values"]!.GetValue<string>().Should().Be("joinedAfterPage");
    }

    [Theory]
    [InlineData(Order, false)]
    [InlineData(Invoice, true)]
    public async Task It_says_exactly_what_a_values_stage_binds_for_every_path_of_a_model(string entity, bool resolve)
    {
        var model = resolve ? ResolveModel.Model : BindHost.Probe;
        var shape = (await BindHost.BoundAsync(model, entity, "[]")).FinalShape;
        var paths = model.Entities[entity].PathIndex.Keys.Where(path => !path.Contains('*')).Order(StringComparer.Ordinal).ToList();
        var said = 0;

        paths.Count.Should().BeGreaterThan(20);

        foreach (var path in paths)
        {
            var binds = await BindHost.BindAsync(model, BindHost.Request(entity, $$"""[{ "values": { "path": "{{path}}" } }]""")) is BindOutcome.Bound;
            var (can, why) = ExplainFacts.Can(shape, path);

            can["values"]!.GetValue<bool>().Should().Be(binds, $"{path}: explain says what the binder does");
            ExplainFacts.CanValues(shape, path).Should().Be(binds, path);
            (why?.ContainsKey("values") ?? false).Should().Be(!binds, $"{path}: a reason exactly where it is refused");

            // A reader that holds only the member's flags and its kind derives the same answer.
            var flags = ExplainTypes.FlagsAt(shape, path);
            var kind = shape.Resolve(path, PathUsage.Project).Path?.Kind ?? Kind.Unknown;
            var inRow = shape.Resolve(path, PathUsage.Aggregate).Succeeded;
            var derived = flags.Groupable || (inRow && flags.UnderCollection > 0 && Kinds.IsScalar(kind) && kind != Kind.Array);

            derived.Should().Be(binds, $"{path}: values = groupable, or a scalar only the collections above it keep from being a group key");
            said += binds ? 1 : 0;
        }

        said.Should().BeGreaterThan(5).And.BeLessThan(paths.Count, "some paths have values and some have none");
        paths.Should().Contain(path => ExplainTypes.FlagsAt(shape, path).UnderCollection > 0 && ExplainFacts.CanValues(shape, path), "a member under a collection among them");
    }

    // ---- a request that ends in values --------------------------------------------------------

    [Fact]
    public async Task Its_stage_creates_value_and_count_and_the_row_is_a_bucket_keyed_by_the_value()
    {
        var answer = await ExplainAsync("""[{ "unwind": { "path": "items", "as": "item" } }, { "values": { "path": "number", "counts": true, "search": "a", "limit": 30, "scan": 400 } }]""",
            plan: true, filter: """[{ "id": "flagged", "where": { "flag": { "eq": true } } }, { "id": "many", "where": { "item.quantity": { "gt": 1 } } }]""");

        answer.Valid.Should().BeTrue(string.Join("; ", answer.Errors.Select(error => error.Message)));
        answer.Executable.Should().Be(Basis.Yes);

        var stage = answer.Stage(1);

        stage.Kind.Should().Be("values");
        stage.Status.Should().Be("ok");
        stage.Creates.Should().Equal("value", "count");
        stage.Reads.Select(read => read.ToJsonString()).Should().Equal("""{"path":"number","use":"groupKey"}""");
        stage.Shape!.Grouped.Should().BeTrue();
        stage.Shape.Identity!.ToJsonString().Should().Be("""{"kind":"bucket","bucket":{"keys":["value"]}}""");

        answer.Result!.Identity!.ToJsonString().Should().Be("""{"kind":"bucket","bucket":{"keys":["value"]}}""");
        answer.Result.Order!.ToJsonString().Should().Be("""[{"path":"count","direction":"desc"},{"path":"value","direction":"asc","tieBreak":true}]""", "the order the stage emits");
        answer.Result.Subjects!.Should().BeEmpty();
        answer.Result.Columns.Select(column => (column.Path, column.Kind)).Should().Equal(("value", "string"), ("count", "int"));
        answer.Alias("value")["node"]!.GetValue<string>().Should().Be("group");
        answer.Alias("value")["stage"]!.GetValue<int>().Should().Be(1);

        // The conditions of the filter stand before the stage, each where its path is in the row.
        answer.Filter!.Select(entry => (entry.Id, entry.Status)).Should().Equal(("flagged", "ok"), ("many", "ok"));
        JsonSerializer.Serialize(answer.Filter![0].Placed).Should().Be("""[{"before":0,"paths":["flag"]}]""");
        JsonSerializer.Serialize(answer.Filter[1].Placed).Should().Be("""[{"after":0,"paths":["item.quantity"]}]""");

        // The plan is the aggregate a run sends.
        var names = answer.Plan!.Stages.Select(emitted => emitted.AsObject().First().Key).ToList();

        names.Should().ContainInOrder("$unwind", "$limit", "$group", "$facet");
        names[^1].Should().Be("$facet");
        answer.Plan.Count.Should().BeNull();
        answer.Plan.Bound["stages"]!.AsArray()[^1]!.ToJsonString().Should().StartWith("""{"values":{"path":"Number","unwind":[],"search":{"path":"_id","op":"startsWith","operand":"a",""");
        answer.Plan.Bound.AsObject().ContainsKey("page").Should().BeFalse();

        // Without counts the list is ordered by value alone.
        (await ExplainAsync("""[{ "values": { "path": "number" } }]""")).Result!.Order!.ToJsonString().Should().Be("""[{"path":"value","direction":"asc"}]""");
    }

    [Fact]
    public async Task It_notes_what_the_stage_reads_and_that_a_strings_values_fold()
    {
        var plain = await ExplainAsync("""[{ "values": { "path": "number", "counts": true } }]""");
        var scans = plain.Notes.Should().ContainSingle(note => note.Code == Notes.ValuesScans).Subject;

        scans.Stage.Should().Be(0);
        scans.Path.Should().Be("number");
        scans.Basis.Should().Be(Basis.Advisory);
        scans.Message.Should().Be("The values are read off at most 50000 of the rows that reach the stage, whatever the request matches: when that many were read the answer says so (VALUES_SCAN_CAPPED) and lists the values among them."
            + " An index that holds the organisation, the path and what the conditions before the stage read, built without a collation, answers it without reading a document.");
        JsonSerializer.Serialize(scans.Params).Should().Be("""{"scan":50000,"limit":100,"counts":true,"unwinds":0}""");
        Basis.OfNote[Notes.ValuesScans].Should().Be(Basis.Advisory);

        var folds = plain.Notes.Should().ContainSingle(note => note.Code == Notes.TextFolds).Subject;

        folds.Stage.Should().Be(0);
        folds.Path.Should().Be("number");
        folds.Params!["op"].Should().Be("values");

        // Under collections it says that a count counts elements; a path that holds no text folds nothing.
        var under = await ExplainAsync("""[{ "values": { "path": "items.stops.latitude", "counts": true, "scan": 20 } }]""");
        var note = under.Notes.Single(each => each.Code == Notes.ValuesScans);

        note.Message.Should().Contain("at most 20 of the rows").And.Contain("'items.stops.latitude' lies under 2 collections the stage unwinds for the values: a value is one some element holds, and a count counts elements, a row without any once under null.");
        JsonSerializer.Serialize(note.Params).Should().Be("""{"scan":20,"limit":100,"counts":true,"unwinds":2}""");
        under.Notes.Should().NotContain(each => each.Code == Notes.TextFolds);
        (await ExplainAsync("""[{ "values": { "path": "items.quantity" } }]""")).Notes.Single(each => each.Code == Notes.ValuesScans).Message
            .Should().Contain("lies under a collection the stage unwinds for the values: a value is one some element holds.").And.NotContain("a count counts");

        // A group before it still says what a group reads; a request without the stage says nothing of values.
        var grouped = await ExplainAsync("""[{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "values": { "path": "n" } }]""");

        grouped.Notes.Select(each => each.Code).Should().Contain([Notes.GroupScans, Notes.ValuesScans]);
        (await ExplainAsync("""[{ "sort": [{ "number": "asc" }] }]""")).Notes.Should().NotContain(each => each.Code == Notes.ValuesScans);
    }

    [Theory]
    [InlineData(""" "path": "number", "limit": 501 """, Codes.ValuesLimitExceeded, Basis.Limits)]
    [InlineData(""" "path": "number", "scan": 50001 """, Codes.ValuesLimitExceeded, Basis.Limits)]
    [InlineData(""" "path": "number", "limit": 0 """, Codes.InvalidPageLimit, Basis.Request)]
    [InlineData(""" "path": "number", "counts": "yes" """, Codes.InvalidPageLimit, Basis.Request)]
    [InlineData(""" "path": "number", "top": 3 """, Codes.UnknownStageMember, Basis.Request)]
    [InlineData(""" "path": "nope" """, Codes.UnknownPath, Basis.Model)]
    [InlineData(""" "path": "shipTo" """, Codes.NotFilterable, Basis.Model)]
    [InlineData(""" "path": "tags" """, Codes.GroupOnCollection, Basis.Model)]
    [InlineData(""" "path": "count", "search": "1" """, Codes.OptionNotApplicable, Basis.Model)]
    public async Task What_it_refuses_carries_the_basis_of_its_code_and_can_never_run_as_written(string members, string code, string basis)
    {
        var answer = await ExplainAsync($$"""[{ "values": { {{members}} } }]""");

        answer.Valid.Should().BeFalse();
        answer.Executable.Should().Be(Basis.Never);

        var error = answer.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(code);
        error.Stage.Should().Be(0);
        error.Basis.Should().Be(basis);
        answer.Stage(0).Status.Should().Be("error");
        answer.Stage(0).Kind.Should().Be("values");
        answer.Stage(0).Creates.Should().BeEmpty();
    }

    [Fact]
    public async Task Under_another_services_alias_it_is_refused_by_the_model_and_a_stage_after_it_by_the_request()
    {
        var remote = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "r" } }, { "values": { "path": "r.name" } }]""", Invoice, ResolveModel.Model);
        var refused = remote.Errors.Should().ContainSingle().Subject;

        refused.Code.Should().Be(Codes.NotContinuable);
        refused.Basis.Should().Be(Basis.Model);
        refused.Stage.Should().Be(1);
        remote.Executable.Should().Be(Basis.Never);

        var after = await ExplainAsync("""[{ "values": { "path": "number" } }, { "page": { "limit": 5 } }]""");

        after.Errors.Should().ContainSingle().Which.Should().Match<OxQL.Core.Models.QueryValidationError>(error => error.Code == Codes.StageAfterPage && error.Basis == Basis.Request && error.Stage == 1);
        after.Stage(0).Status.Should().Be("ok");
        after.Stage(1).Status.Should().Be("error");
    }

    [Fact]
    public async Task Contract_1_explains_it_as_the_unknown_stage_it_is_there()
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var legacy = (await engine.ExplainAsync(BindHost.Request(Order, """[{ "values": { "path": "number" } }]"""), BindHost.Context(contract: 1))).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        legacy.Valid.Should().BeFalse();
        legacy.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.UnknownStage);
        legacy.Notes.Should().NotContain(note => note.Code == Notes.ValuesScans);
    }
}
