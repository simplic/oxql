using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The <c>values</c> stage: the distinct values of one scalar path among the rows
/// that reach it, in place of the page, under contract 2. It reads its path as a group key does,
/// except that collections above it that are not unwound are unwound for the values alone. It
/// replaces the row by <c>value</c> (and <c>count</c>), ends the pipeline, and is bounded twice: the
/// values it answers (<c>limit</c>) and the rows it reads (<c>scan</c>). Contract 1 has no such stage.
/// </summary>
public class ValuesStageBindTests
{
    private const string Order = "probe.order";
    private const string Invoice = ResolveModel.Invoice;

    private static string Values(string members) => $$"""[{ "values": { {{members}} } }]""";

    private static async Task<BoundStage.Values> Stage(string pipeline, RequestContext? context = null) =>
        (await BindHost.BoundAsync(BindHost.Probe, Order, pipeline, context)).Values!;

    private static Task<QueryValidationError> Error(string pipeline, string code, RequestContext? context = null) =>
        BindHost.ErrorAsync(BindHost.Probe, Order, pipeline, code, context);

    // ---- what binds ---------------------------------------------------------------------------

    [Fact]
    public async Task It_binds_a_scalar_member_with_the_hosts_bounds_and_replaces_the_row_by_its_value()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, Values(""" "path": "number" """));
        var values = bound.Values!;

        values.Path.Wire.Should().Be("number");
        values.Path.Storage.Should().Be("Number");
        values.Collections.Should().BeEmpty();
        values.Search.Should().BeNull();
        values.Limit.Should().Be(100, "Values:DefaultLimit");
        values.Scan.Should().Be(50_000, "Values:ScanRows");
        values.Counts.Should().BeFalse();

        bound.Stages.Select(stage => stage.GetType().Name).Should().Equal("Values", "Page");
        bound.CallerIndexOf(values).Should().Be(0);
        bound.CallerIndexOf(bound.Page).Should().BeNull("the page is the engine's: it only carries the limit");
        bound.Page.Limit.Should().Be(100);
        bound.Page.Count.Should().BeNull();
        bound.Sort.Should().BeNull("the stage orders its values itself");
        bound.Collated.Should().BeTrue("a string's values fold into one another as a group's keys do");
        bound.FinalShape.Grouped.Should().BeTrue();
        bound.FinalShape.Roots.Keys.Should().Equal("value");
        bound.FinalShape.Roots["value"].Should().BeOfType<ShapeNode.GroupOutput>().Which.Kind.Should().Be(Kind.String);
        bound.Canonical.Should().Contain("\"values\":{\"path\":\"Number\",\"unwind\":[],\"search\":null,\"limit\":100,\"counts\":false,\"scan\":50000}")
            .And.NotContain("\"page\"", "a request that ends in values has no page");
    }

    [Fact]
    public async Task With_counts_the_row_also_carries_the_number_of_rows_as_an_int()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, Values(""" "path": "state", "counts": true, "limit": 7, "scan": 300 """));

        bound.Values!.Counts.Should().BeTrue();
        bound.Values.Limit.Should().Be(7);
        bound.Values.Scan.Should().Be(300);
        bound.Page.Limit.Should().Be(7);
        bound.FinalShape.Roots.Keys.Should().Equal("value", "count");
        bound.FinalShape.Roots["value"].Should().BeOfType<ShapeNode.GroupOutput>().Which.Kind.Should().Be(Kind.Enum, "an enum's value is written as a row writes the member");
        bound.FinalShape.Roots["count"].Should().BeOfType<ShapeNode.GroupOutput>().Which.Kind.Should().Be(Kind.Int);
        bound.Collated.Should().BeFalse("nothing folds: the path holds no text");
    }

    [Theory]
    [InlineData("count", Kind.Int)]
    [InlineData("big", Kind.Long)]
    [InlineData("amount", Kind.Decimal)]
    [InlineData("flag", Kind.Bool)]
    [InlineData("day", Kind.Date)]
    [InlineData("when", Kind.DateTime)]
    [InlineData("customerId", Kind.Guid)]
    [InlineData("shipTo.city", Kind.String)]
    [InlineData("maybeState", Kind.Enum)]
    public async Task Every_scalar_a_group_key_takes_is_a_path_of_values(string path, Kind kind)
    {
        var values = await Stage(Values($$""" "path": "{{path}}" """));

        values.Path.Kind.Should().Be(kind);
        values.Collections.Should().BeEmpty();

        // The same path is a group key: values asks nothing a group could not.
        await BindHost.BoundAsync(BindHost.Probe, Order, $$"""[{ "group": { "by": [{ "path": "{{path}}", "as": "k" }], "fields": {} } }]""");
    }

    [Fact]
    public async Task A_member_under_a_collection_that_is_not_unwound_is_read_through_it()
    {
        var one = await Stage(Values(""" "path": "items.quantity", "counts": true """));

        one.Path.Storage.Should().Be("Items.Quantity");
        one.Collections.Should().Equal(["Items"], "the collection above the path is unwound for the values alone");

        var two = await Stage(Values(""" "path": "items.stops.city" """));

        two.Collections.Should().Equal(["Items", "Items.Stops"], "outermost first");
        two.Path.Kind.Should().Be(Kind.String);

        // A group refuses the same path: only values unwinds for itself.
        (await Error("""[{ "group": { "by": [{ "path": "items.quantity", "as": "k" }], "fields": {} } }]""", Codes.GroupOnCollection)).Stage.Should().Be(0);
    }

    [Fact]
    public async Task After_an_unwind_the_alias_member_is_a_scalar_of_the_row_and_nothing_is_unwound_for_it()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "unwind": { "path": "items", "as": "item" } }, { "values": { "path": "item.quantity" } }]""");

        bound.Values!.Collections.Should().BeEmpty();
        bound.Values.Path.Storage.Should().Be("item.Quantity");

        // The collection the unwind left under the alias is still one above a member below it.
        (await Stage("""[{ "unwind": { "path": "items", "as": "item" } }, { "values": { "path": "item.stops.city" } }]""")).Collections.Should().Equal("item.Stops");

        // The unwound collection itself left the row.
        var gone = await Error("""[{ "unwind": { "path": "items", "as": "item" } }, { "values": { "path": "items.quantity" } }]""", Codes.UnknownPath);

        gone.Stage.Should().Be(1);
        gone.Params!["reason"].Should().Be("unwound");
    }

    [Fact]
    public async Task After_a_group_a_key_or_an_aggregate_is_a_path_of_values()
    {
        const string Group = """{ "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true }, "top": { "max": "number" } } } }""";

        (await Stage($$"""[{{Group}}, { "values": { "path": "st" } }]""")).Path.Kind.Should().Be(Kind.Enum);
        (await Stage($$"""[{{Group}}, { "values": { "path": "n", "counts": true } }]""")).Path.Kind.Should().Be(Kind.Int);
        (await Stage($$"""[{{Group}}, { "values": { "path": "top", "search": "a" } }]""")).Search.Should().NotBeNull();
        (await Error($$"""[{{Group}}, { "values": { "path": "number" } }]""", Codes.UnknownPath)).Message.Should().Contain("not an output of the group stage");
    }

    [Fact]
    public async Task A_member_of_a_record_joined_in_the_aggregate_is_a_path_of_values()
    {
        var bound = await BindHost.BoundAsync(ResolveModel.Model, Invoice, """[{ "resolve": { "path": "customerId", "as": "c" } }, { "values": { "path": "c.name", "counts": true } }]""");

        bound.Values!.Path.Storage.Should().Be("c.Name");
        bound.Stages.OfType<BoundStage.Resolve>().Single().Executor.Should().Be(ResolveExecutor.Inline);
        bound.Loads["c"].Loads.Should().Contain("name", "the join loads what the values read");
    }

    // ---- search -------------------------------------------------------------------------------

    [Fact]
    public async Task Search_is_a_prefix_of_the_value_that_folds_as_the_values_fold()
    {
        var search = (await Stage(Values(""" "path": "number", "search": "Mü" """))).Search!;

        search.Op.Should().Be("startsWith");
        search.IgnoreCase.Should().BeTrue();
        search.IsSemiJoin.Should().BeFalse();
        search.Path.Storage.Should().Be("_id", "it runs on the values, each the key of its group, not on the rows");
        search.Operand.Should().BeOfType<BoundOperand.Single>().Which.Value.Should().Be(new BsonString("Mü"));

        (await Stage(Values(""" "path": "number", "search": "" """))).Search.Should().BeNull("an empty text keeps every value");
        (await Stage(Values(""" "path": "number", "search": null """))).Search.Should().BeNull();
        (await Stage(Values(""" "path": "items.stops.city", "search": "ber" """))).Search!.Path.Storage.Should().Be("_id");
    }

    [Theory]
    [InlineData("count", "int")]
    [InlineData("state", "enum")]
    [InlineData("when", "dateTime")]
    [InlineData("initial", "string")]
    public async Task Search_on_a_path_that_holds_no_text_is_refused(string path, string kind)
    {
        var error = await Error(Values($$""" "path": "{{path}}", "search": "a" """), Codes.OptionNotApplicable);

        error.Stage.Should().Be(0);
        error.Path.Should().Be(path);
        error.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["option"] = "search", ["kind"] = kind });
        Basis.OfCode[error.Code].Should().Be(Basis.Model);
    }

    // ---- refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task A_path_that_is_no_scalar_of_the_row_is_refused_as_a_group_key_is()
    {
        (await Error(Values(""" "path": "nope" """), Codes.UnknownPath)).Params!["reason"].Should().Be("notAMember");
        (await Error(Values(""" "path": "shipTo" """), Codes.NotFilterable)).Message.Should().Be("'shipTo' is an object; values needs a scalar.");
        (await Error(Values(""" "path": "items" """), Codes.GroupOnCollection)).Message.Should().Be("'items' is a collection; values takes a scalar member, also one of the elements of a collection.");
        (await Error(Values(""" "path": "tags" """), Codes.GroupOnCollection)).Stage.Should().Be(0);
        (await Error(Values(""" "path": "computed" """), Codes.NotStored)).Stage.Should().Be(0);
        (await Error(Values(""" "path": "priceList.a.net" """), Codes.GroupOnCollection)).Message.Should().Contain("lies under a collection the values cannot be read through");
        (await Error(Values(""" "counts": true """), Codes.UnknownPath)).Message.Should().Be("A values stage names the member whose values it answers under 'path'.");
    }

    [Fact]
    public async Task Under_an_alias_whose_rows_come_from_an_owner_after_the_page_it_is_NOT_CONTINUABLE()
    {
        foreach (var join in new[] { """{ "resolve": { "path": "contactId", "as": "r" } }""", """{ "resolve": { "path": "billingLineId", "as": "e", "parentAs": "r" } }""" })
        {
            var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, $$"""[{{join}}, { "values": { "path": "r.name" } }]""", Codes.NotContinuable);

            error.Stage.Should().Be(1);
            error.Path.Should().Be("r.name");
            error.Message.Should().StartWith("'values' cannot run on 'r', which comes from its owner after the page");
            Basis.OfCode[error.Code].Should().Be(Basis.Model);
        }
    }

    [Fact]
    public async Task A_member_it_does_not_have_or_one_of_another_json_kind_is_refused()
    {
        var unknown = await Error(Values(""" "path": "number", "prefix": "a", "max": 3 """), Codes.UnknownStageMember);

        unknown.Message.Should().Be("'prefix, max' is not a member of values; values carries path, search, limit, counts, scan.");
        unknown.Stage.Should().Be(0);

        foreach (var (members, member, what) in new[]
        {
            (""" "path": 5 """, "path", "a string"),
            (""" "path": "number", "search": 5 """, "search", "a string"),
            (""" "path": "number", "limit": "ten" """, "limit", "a whole number"),
            (""" "path": "number", "limit": 2.5 """, "limit", "a whole number"),
            (""" "path": "number", "scan": true """, "scan", "a whole number"),
            (""" "path": "number", "counts": "yes" """, "counts", "true or false"),
            (""" "path": "number", "counts": 1 """, "counts", "true or false"),
        })
        {
            var error = await Error(Values(members), Codes.InvalidPageLimit);

            error.Message.Should().Be($"'{member}' of values is {what}.");
            error.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["stage"] = "values", ["member"] = member });
            Basis.OfCode[error.Code].Should().Be(Basis.Request);
        }

        (await Error("""[{ "values": "number" }]""", Codes.InvalidPageLimit)).Message.Should().Be("A values stage is an object carrying path, search, limit, counts, scan.");
        (await Error("""[{ "values": null }]""", Codes.UnknownStageMember)).Message.Should().Be("'values' is null; a values stage carries an object.");
    }

    [Fact]
    public async Task The_limit_and_the_scan_are_positive_and_under_the_hosts_bounds()
    {
        (await Stage(Values(""" "path": "number", "limit": 500, "scan": 50000 """))).Limit.Should().Be(500);
        (await Stage(Values(""" "path": "number", "limit": 1, "scan": 1 """))).Scan.Should().Be(1);

        var limit = await Error(Values(""" "path": "number", "limit": 501 """), Codes.ValuesLimitExceeded);

        limit.Message.Should().Be("The values limit 501 exceeds the maximum of 500; narrow the values with 'search' instead.");
        limit.Stage.Should().Be(0);
        limit.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["limit"] = "maxValuesLimit", ["max"] = 500, ["value"] = 501, ["count"] = 501 });

        var scan = await Error(Values(""" "path": "number", "scan": 50001 """), Codes.ValuesLimitExceeded);

        scan.Message.Should().Be("The values scan 50001 exceeds the 50000 rows this host reads for a list of values.");
        scan.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["limit"] = "valuesScanRows", ["max"] = 50_000, ["value"] = 50_001, ["count"] = 50_001 });

        var search = await Error(Values($$""" "path": "number", "search": "{{new string('a', 201)}}" """), Codes.ValuesLimitExceeded);

        search.Params.Should().BeEquivalentTo(new Dictionary<string, object?> { ["limit"] = "regexMaxLength", ["max"] = 200, ["value"] = 201, ["count"] = 201 });
        Basis.OfCode[Codes.ValuesLimitExceeded].Should().Be(Basis.Limits);

        foreach (var (members, member, text) in new[]
        {
            (""" "path": "number", "limit": 0 """, "limit", "The values limit 0 is not positive."),
            (""" "path": "number", "limit": -3 """, "limit", "The values limit -3 is not positive."),
            (""" "path": "number", "scan": 0 """, "scan", "The values scan 0 is not positive."),
        })
        {
            var error = await Error(Values(members), Codes.InvalidPageLimit);

            error.Message.Should().Be(text);
            error.Params!["member"].Should().Be(member);
        }

        // Every bound that is broken is said at once.
        var both = await BindHost.RefusedAsync(BindHost.Probe, Order, Values(""" "path": "number", "limit": 9999, "scan": 99999999999 """));

        both.Errors!.Select(error => error.Params!["limit"]).Should().Equal("maxValuesLimit", "valuesScanRows");
    }

    [Fact]
    public async Task The_host_sets_the_bounds_and_the_default_limit_stays_under_the_maximum()
    {
        var small = BindHost.Context(BindHost.Options(options =>
        {
            options.Limits.MaxValuesLimit = 20;
            options.Values.ScanRows = 1_000;
        }));

        var values = await Stage(Values(""" "path": "number" """), small);

        values.Limit.Should().Be(20, "the default is cut to the host's maximum");
        values.Scan.Should().Be(1_000);
        (await Error(Values(""" "path": "number", "limit": 21 """), Codes.ValuesLimitExceeded, small)).Params!["max"].Should().Be(20);
        (await Error(Values(""" "path": "number", "scan": 1001 """), Codes.ValuesLimitExceeded, small)).Params!["max"].Should().Be(1_000);

        var defaults = new OxQLOptions();

        defaults.Limits.MaxValuesLimit.Should().Be(500);
        defaults.Values.ScanRows.Should().Be(50_000);
        defaults.Values.DefaultLimit.Should().Be(100);

        var odd = new OxQLOptions { Limits = { MaxValuesLimit = 0 }, Values = { ScanRows = -1, DefaultLimit = 700 } };

        odd.Normalise().Should().HaveCount(3);
        (odd.Limits.MaxValuesLimit, odd.Values.ScanRows, odd.Values.DefaultLimit).Should().Be((1, 1, 1));
    }

    [Fact]
    public async Task It_ends_the_pipeline_in_place_of_the_page()
    {
        var after = await Error("""[{ "values": { "path": "number" } }, { "page": { "limit": 5 } }]""", Codes.StageAfterPage);

        after.Stage.Should().Be(1);
        after.Message.Should().Be("No stage may follow the values stage: it ends the pipeline in place of a page.");

        (await Error("""[{ "values": { "path": "number" } }, { "match": { "count": { "gt": 1 } } }]""", Codes.StageAfterPage)).Stage.Should().Be(1);
        (await Error("""[{ "values": { "path": "number" } }, { "values": { "path": "state" } }]""", Codes.StageAfterPage)).Stage.Should().Be(1);
        (await Error("""[{ "page": { "limit": 5 } }, { "values": { "path": "number" } }]""", Codes.StageAfterPage)).Message.Should().Be("No stage may follow the page stage.");

        // A values stage that failed still ends the pipeline: one error per mistake.
        var failed = await BindHost.RefusedAsync(BindHost.Probe, Order, """[{ "values": { "path": "nope" } }, { "page": { "limit": 5 } }]""");

        failed.Errors!.Select(error => error.Code).Should().Equal(Codes.UnknownPath, Codes.StageAfterPage);
    }

    // ---- the request's filter -----------------------------------------------------------------

    [Fact]
    public async Task The_conditions_of_the_filter_run_before_it_and_none_reads_what_it_answers()
    {
        var request = BindHost.Parse($$"""
            { "entityType": "{{Order}}",
              "filter": [ { "id": "a", "where": { "flag": { "eq": true } } }, { "id": "b", "where": { "item.quantity": { "gt": 1 } } } ],
              "pipeline": [ { "unwind": { "path": "items", "as": "item" } }, { "values": { "path": "number", "counts": true } } ] }
            """);
        var bound = (await BindHost.BindAsync(BindHost.Probe, request)).Should().BeOfType<BindOutcome.Bound>().Subject.Pipeline;

        bound.Stages.Select(stage => bound.FilterOf(stage) is { } label ? "filter:" + label : stage.GetType().Name).Should().Equal("filter:a", "Unwind", "filter:b", "Values", "Page");
        bound.Filters[1].Placed.Single().Boundary.Should().Be(1, "the boundary before the values stage is the last a condition can run at");

        // 'value' and 'count' are what the stage answers, never something a condition of the filter reads:
        // 'count' is the entity's own member here and runs at the entry, 'value' is no path at all.
        var own = BindHost.Parse($$"""{ "entityType": "{{Order}}", "filter": [ { "where": { "count": { "gt": 1 } } } ], "pipeline": [ { "values": { "path": "number", "counts": true } } ] }""");

        (await BindHost.BindAsync(BindHost.Probe, own)).Should().BeOfType<BindOutcome.Bound>().Subject.Pipeline.Filters[0].Placed.Single().Boundary.Should().Be(0);

        var none = BindHost.Parse($$"""{ "entityType": "{{Order}}", "filter": [ { "id": "v", "where": { "value": { "eq": "a" } } } ], "pipeline": [ { "values": { "path": "number" } } ] }""");
        var refused = (await BindHost.BindAsync(BindHost.Probe, none)).Should().BeOfType<BindOutcome.Failed>().Subject.Refusal.Errors!.Single();

        refused.Code.Should().Be(Codes.UnknownPath);
        refused.Filter.Should().Be("v");
    }

    // ---- contract 1 ---------------------------------------------------------------------------

    [Theory]
    [InlineData("""[{ "values": { "path": "number" } }]""")]
    [InlineData("""[{ "values": null }]""")]
    [InlineData("""[{ "values": 5 }]""")]
    public async Task Contract_1_knows_no_such_stage_and_answers_what_it_answers_for_any_unknown_one(string pipeline)
    {
        var legacy = BindHost.Context(contract: 1);
        var error = await Error(pipeline, Codes.UnknownStage, legacy);
        var other = await Error("""[{ "facet": { "path": "number" } }]""", Codes.UnknownStage, legacy);

        error.Stage.Should().Be(0);
        error.Message.Should().Be("'values' is not a stage; a stage object carries exactly one of match, lookup, resolve, unwind, group, project, sort, page.");
        error.Message.Should().Be(other.Message.Replace("facet", "values", StringComparison.Ordinal));
        error.Params.Should().BeNull();

        // Contract 2 names the stage among the ones it has.
        (await Error("""[{ "facet": {} }]""", Codes.UnknownStage)).Message.Should().EndWith("project, sort, page, values.");
    }
}
