using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The top-level <c>filter</c> of a contract 2 request (DG2 M1): a list of <c>{ id?, where }</c>, all
/// of which a row must meet, each condition placed by the engine at the earliest point of the
/// pipeline where the paths it reads are in the row. A condition is about the thing its path names:
/// an entity member at the entry, an unwind's alias after the unwind, a join's alias after the join,
/// an aggregate after its group. The top-level conjuncts of a <c>where</c> are placed one by one; an
/// <c>or</c>, <c>not</c> or <c>any</c> group as a whole.
/// </summary>
public class FilterPlacementTests
{
    private const string Order = "probe.order";
    private const string Customer = "probe.customer";
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static QueryRequest Request(string filter, string pipeline, string entity = Order) =>
        BindHost.Parse($$"""{ "entityType": "{{entity}}", "filter": {{filter}}, "pipeline": {{pipeline}} }""");

    private static async Task<BoundPipeline> Bound(string filter, string pipeline, string entity = Order, RequestContext? context = null)
    {
        var outcome = await BindHost.BindAsync(BindHost.Probe, Request(filter, pipeline, entity), context);

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));

        return ((BindOutcome.Bound)outcome).Pipeline;
    }

    private static async Task<IReadOnlyList<QueryValidationError>> Errors(string filter, string pipeline, string entity = Order)
    {
        var outcome = await BindHost.BindAsync(BindHost.Probe, Request(filter, pipeline, entity));

        return outcome.Should().BeOfType<BindOutcome.Failed>().Subject.Refusal.Errors!;
    }

    /// <summary>The bound stages by kind, a placed filter condition as <c>filter:&lt;label&gt;</c>.</summary>
    private static List<string> Kinds(BoundPipeline bound) =>
        bound.Stages.Select(stage => bound.FilterOf(stage) is { } label ? "filter:" + label : stage.GetType().Name.ToLowerInvariant()).ToList();

    private static string Placed(BoundPipeline bound, int entry = 0) =>
        string.Join(" | ", bound.Filters[entry].Placed.Select(placement => $"{placement.Boundary}:{string.Join(",", placement.Paths)}{(placement.Having ? ":having" : "")}"));

    private const string UnwindItems = """{ "unwind": { "path": "items", "as": "item" } }""";
    private const string GroupByNumber = """{ "group": { "by": [{ "path": "number", "as": "number" }], "fields": { "total": { "count": true } } } }""";

    // ---- where a condition runs --------------------------------------------------------------

    [Fact]
    public async Task A_condition_on_an_entity_member_runs_at_the_entry_whatever_follows()
    {
        var bound = await Bound("""[{ "id": "open", "where": { "number": { "eq": "x" } } }]""", $$"""[{{UnwindItems}}, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5 } }]""");

        Kinds(bound).Should().Equal("filter:open", "unwind", "sort", "page");
        Placed(bound).Should().Be("0:number");
        bound.CallerIndexOf(0).Should().BeNull("the engine supplied the stage");
        bound.CallerIndexOf(1).Should().Be(0, "the caller's stages keep their own indexes");
    }

    [Fact]
    public async Task A_path_names_one_thing_the_collection_before_the_unwind_and_the_alias_after_it()
    {
        var bound = await Bound("""
            [{ "id": "some", "where": { "items.quantity": { "gt": 5 } } },
             { "id": "this", "where": { "item.quantity": { "gt": 5 } } },
             { "id": "one", "where": { "items": { "any": { "quantity": { "gt": 5, "lt": 9 } } } } },
             { "id": "at", "where": { "position": { "eq": 0 } } }]
            """, """[{ "match": { "number": { "exists": true } } }, { "unwind": { "path": "items", "as": "item", "includeIndex": "position" } }]""");

        Kinds(bound).Should().Equal("filter:some", "filter:one", "match", "unwind", "filter:this", "filter:at", "sort", "page");
        Placed(bound, 0).Should().Be("0:items.quantity", "some element of the shipment");
        Placed(bound, 1).Should().Be("2:item.quantity", "this row's element");
        Placed(bound, 2).Should().Be("0:items");
        Placed(bound, 3).Should().Be("2:position");
    }

    [Fact]
    public async Task A_join_alias_is_filtered_right_after_its_join()
    {
        var bound = await Bound("""[{ "where": { "cust.name": { "eq": "x" }, "number": { "eq": "n" } } }]""",
            """[{ "match": { "number": { "exists": true } } }, { "resolve": { "path": "customerId", "as": "cust" } }, { "sort": [{ "number": "asc" }] }]""");

        Kinds(bound).Should().Equal("filter:0", "match", "resolve", "filter:0", "sort", "page");
        Placed(bound).Should().Be("0:number | 2:cust.name", "the conjuncts of one where are placed one by one");
    }

    [Fact]
    public async Task Around_a_group_a_source_path_filters_the_rows_and_an_aggregate_the_groups()
    {
        var bound = await Bound("""
            [{ "id": "rows", "where": { "when": { "gte": "2026-01-01T00:00:00Z" } } },
             { "id": "key", "where": { "number": { "startsWith": "a" } } },
             { "id": "having", "where": { "total": { "gt": 10 } } }]
            """, $$"""[{{GroupByNumber}}, { "sort": [{ "total": "desc" }] }]""");

        Kinds(bound).Should().Equal("filter:rows", "filter:key", "group", "filter:having", "sort", "page");
        Placed(bound, 0).Should().Be("0:when");
        Placed(bound, 1).Should().Be("0:number", "a key over the path of the same name: which buckets is which rows feed them");
        Placed(bound, 2).Should().Be("1:total:having");
        bound.Filters.SelectMany(filter => filter.BindsEarlier).Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_a_group_gives_to_another_thing_binds_at_its_earlier_birth_and_is_noted()
    {
        const string Regrouped = """{ "group": { "by": [{ "path": "state", "as": "number" }], "fields": { "amount": { "count": true } } } }""";
        var bound = await Bound("""[{ "id": "early", "where": { "number": { "eq": "x" }, "amount": { "gt": 1 } } }]""", $"[{Regrouped}]");

        Placed(bound).Should().Be("0:number,amount");
        bound.Filters[0].BindsEarlier.Should().Equal(("number", "number", 0), ("amount", "amount", 0));

        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options());
        var explained = ((ExplainOutcome.Success)await engine.ExplainAsync(Request("""[{ "id": "early", "where": { "number": { "eq": "x" } } }]""", $"[{Regrouped}]"), BindHost.Context())).Result;
        var note = explained.Notes.Should().ContainSingle(each => each.Code == Notes.FilterBindsEarlier).Subject;

        note.Message.Should().StartWith("The condition on 'number' runs before the group of stage 0");
        JsonSerializer.Serialize(note with { Message = "" }, BindHost.Json).Should().Be("""{"code":"FILTER_BINDS_EARLIER","message":"","stage":0,"path":"number","filter":"early","params":{"path":"number","alias":"number"},"basis":"advisory"}""");
    }

    [Fact]
    public async Task A_run_whose_filter_condition_ran_before_a_group_that_rebinds_its_name_says_so_in_its_diagnostics()
    {
        // The natural grid case: a level sums 'amount' and the column filter on 'amount' means the rows, not the sums.
        const string Level = """{ "group": { "by": [{ "path": "state", "as": "state" }], "fields": { "amount": { "sum": "amount" } } } }""";
        var runner = new FakeAggregateRunner { PageRows = [new BsonDocument { ["state"] = 1, ["amount"] = 5 }] };
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), runner, BindHost.Cursors, BindHost.Options());

        async Task<QueryResult> Run(string filter, string pipeline) =>
            (await engine.ExecuteAsync(Request(filter, pipeline), BindHost.Context())).Should().BeOfType<QueryOutcome.Success>().Subject.Result;

        var rows = await Run("""[{ "id": "col:amount", "where": { "amount": { "gt": 1000 } } }]""", $"[{Level}, {{ \"page\": {{ \"limit\": 5 }} }}]");
        var said = rows.Diagnostics.Should().ContainSingle(each => each.Code == Notes.FilterBindsEarlier).Subject;

        JsonSerializer.Serialize(said with { Message = "" }, BindHost.Json).Should().Be("""{"code":"FILTER_BINDS_EARLIER","message":"","stage":0,"path":"amount","filter":"col:amount","params":{"path":"amount","alias":"amount"}}""");
        said.Message.Should().Be("The condition on 'amount' runs before the group of stage 0, on the rows that feed it; the group's 'amount' is another thing of the same name. A match stage after the group filters that one.");

        // It is what explain says of the same request, word for word.
        var explained = ((ExplainOutcome.Success)await engine.ExplainAsync(Request("""[{ "id": "col:amount", "where": { "amount": { "gt": 1000 } } }]""", $"[{Level}]"), BindHost.Context())).Result;

        explained.Notes.Should().ContainSingle(each => each.Code == Notes.FilterBindsEarlier).Which.Message.Should().Be(said.Message);

        // The count alone and a list of values answer the same rows and say the same.
        (await Run("""[{ "id": "col:amount", "where": { "amount": { "gt": 1000 } } }]""", $"[{Level}, {{ \"page\": {{ \"limit\": 0, \"count\": true }} }}]"))
            .Diagnostics.Should().ContainSingle(each => each.Code == Notes.FilterBindsEarlier);
        (await Run("""[{ "id": "col:amount", "where": { "amount": { "gt": 1000 } } }]""", $"[{Level}, {{ \"values\": {{ \"path\": \"state\" }} }}]"))
            .Diagnostics.Should().ContainSingle(each => each.Code == Notes.FilterBindsEarlier);

        // A key over the member of the same name is the same thing, and a condition after the group is where it reads: neither is said.
        (await Run("""[{ "where": { "state": { "eq": 1 } } }]""", $"[{Level}, {{ \"page\": {{ \"limit\": 5 }} }}]")).Diagnostics.Should().BeNull();
        (await Run("[]", $"[{Level}, {{ \"match\": {{ \"amount\": {{ \"gt\": 1000 }} }} }}, {{ \"page\": {{ \"limit\": 5 }} }}]")).Diagnostics.Should().NotContain(each => each.Code == Notes.FilterBindsEarlier);
    }

    [Fact]
    public async Task An_or_group_is_one_unit_at_the_latest_birth_of_its_paths()
    {
        var bound = await Bound("""[{ "id": "mixed", "where": { "or": [{ "number": { "eq": "x" } }, { "item.quantity": { "gt": 5 } }] } }, { "id": "not", "where": { "not": { "item.quantity": { "gt": 1 } } } }]""",
            $$"""[{{UnwindItems}}]""");

        Kinds(bound).Should().Equal("unwind", "filter:mixed", "filter:not", "sort", "page");
        Placed(bound, 0).Should().Be("1:number,item.quantity");
    }

    [Fact]
    public async Task Paths_that_are_never_in_the_row_together_are_FILTER_NOT_PLACEABLE()
    {
        var error = (await Errors("""[{ "id": "split", "where": { "or": [{ "items.quantity": { "gt": 5 } }, { "item.quantity": { "gt": 1 } }] } }]""", $$"""[{{UnwindItems}}]""")).Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.FilterNotPlaceable);
        error.Stage.Should().BeNull();
        error.Filter.Should().Be("split");
        JsonSerializer.Serialize(error.Params).Should().Be("""{"paths":["items.quantity","item.quantity"],"births":[{"path":"items.quantity","before":0},{"path":"item.quantity","after":0}]}""");

        // A member the group removed, OR-ed with an aggregate.
        (await Errors("""[{ "where": { "or": [{ "when": { "exists": true } }, { "total": { "gt": 1 } }] } }]""", $"[{GroupByNumber}]")).Should().ContainSingle()
            .Which.Should().Match<QueryValidationError>(each => each.Code == Codes.FilterNotPlaceable && Equals(each.Filter, 0));
    }

    [Fact]
    public async Task A_path_that_is_never_in_the_row_is_the_error_it_would_be_in_a_match_and_names_its_entry()
    {
        var errors = await Errors("""[{ "where": { "number": { "eq": "x" } } }, { "id": "typo", "where": { "itm.quantity": { "gt": 5 }, "number": { "eq": 5 } } }, { "where": { "note": { "contains": 5 } } }]""", $$"""[{{UnwindItems}}]""");

        errors.Select(error => (error.Code, error.Stage, error.Filter, error.Path)).Should().BeEquivalentTo(new (string, int?, object?, string?)[]
        {
            (Codes.InvalidOperand, null, "typo", "number"),
            (Codes.UnknownPath, null, "typo", "itm.quantity"),
            (Codes.InvalidOperand, null, 2, "note"),
        });

        JsonSerializer.Serialize(errors.First(error => error.Code == Codes.UnknownPath), BindHost.Json).Should().Contain("\"filter\":\"typo\"").And.NotContain("\"stage\"");
        JsonSerializer.Serialize(errors.Single(error => Equals(error.Filter, 2)), BindHost.Json).Should().Contain("\"filter\":2");
    }

    [Fact]
    public async Task A_diagnostic_of_a_condition_names_its_entry()
    {
        var bound = await Bound("""[{ "id": "loose", "where": { "number": { "regex": "abc" } } }]""", "[]");

        bound.Diagnostics.Should().ContainSingle().Which.Should().Match<Diagnostic>(diagnostic => diagnostic.Code == Codes.RegexUnanchored && diagnostic.Stage == null && Equals(diagnostic.Filter, "loose"));
    }

    // ---- the same query, written as match stages ---------------------------------------------

    [Theory]
    [InlineData("""[{ "where": { "number": { "eq": "x" } } }]""", """[{ "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "count": true } }]""",
        """[{ "match": { "number": { "eq": "x" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "count": true } }]""")]
    [InlineData("""[{ "where": { "number": { "eq": "x" }, "item.quantity": { "gt": 1 } } }]""", """[{ "unwind": { "path": "items", "as": "item" } }, { "project": { "number": 1 } }]""",
        """[{ "match": { "number": { "eq": "x" } } }, { "unwind": { "path": "items", "as": "item" } }, { "match": { "item.quantity": { "gt": 1 } } }, { "project": { "number": 1 } }]""")]
    [InlineData("""[{ "where": { "when": { "exists": true } } }, { "where": { "total": { "gt": 3 } } }]""",
        """[{ "group": { "by": [{ "path": "number", "as": "number" }], "fields": { "total": { "count": true } } } }, { "sort": [{ "total": "desc" }] }]""",
        """[{ "match": { "when": { "exists": true } } }, { "group": { "by": [{ "path": "number", "as": "number" }], "fields": { "total": { "count": true } } } }, { "match": { "total": { "gt": 3 } } }, { "sort": [{ "total": "desc" }] }]""")]
    [InlineData("""[{ "where": { "cust.name": { "eq": "x" } } }]""", """[{ "resolve": { "path": "customerId", "as": "cust" } }]""",
        """[{ "resolve": { "path": "customerId", "as": "cust" } }, { "match": { "cust.name": { "eq": "x" } } }]""")]
    public async Task The_filter_compiles_to_what_the_same_conditions_compile_to_as_match_stages_where_it_placed_them(string filter, string pipeline, string asMatches)
    {
        var placed = await Bound(filter, pipeline);
        var written = await BindHost.BoundAsync(BindHost.Probe, Order, asMatches);
        var compiled = MongoCompiler.Compile(placed, Options);
        var expected = MongoCompiler.Compile(written, Options);

        compiled.PageStages.Select(stage => stage.ToJson()).Should().Equal(expected.PageStages.Select(stage => stage.ToJson()));
        (compiled.CountStages ?? []).Select(stage => stage.ToJson()).Should().Equal((expected.CountStages ?? []).Select(stage => stage.ToJson()));
        placed.Fingerprint.Should().Be(written.Fingerprint, "a cursor is bound to which rows there are, however the conditions were written");
    }

    [Fact]
    public async Task The_filter_is_part_of_the_cursor_fingerprint()
    {
        var plain = (await BindHost.BoundAsync(BindHost.Probe, Order, "[]")).Fingerprint;
        var one = (await Bound("""[{ "where": { "number": { "eq": "x" } } }]""", "[]")).Fingerprint;
        var other = (await Bound("""[{ "where": { "number": { "eq": "y" } } }]""", "[]")).Fingerprint;

        new[] { plain, one, other }.Should().OnlyHaveUniqueItems();
        (await Bound("""[{ "id": "named", "where": { "number": { "eq": "x" } } }]""", "[]")).Fingerprint.Should().Be(one, "an id names an entry and decides no row");
        (await Bound("""[{ "where": {} }]""", "[]")).Fingerprint.Should().Be(plain, "an empty where matches everything and places nothing");
    }

    // ---- the member itself --------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "number": { "eq": "x" } }""", "notAnArray", null)]
    [InlineData("""["number"]""", "notAnObject", 0)]
    [InlineData("""[{ "id": "a", "where": {} }, { "id": "b" }]""", "whereRequired", "b")]
    [InlineData("""[{ "where": [] }]""", "whereRequired", 0)]
    [InlineData("""[{ "where": {}, "when": {} }]""", "unknownMember", 0)]
    [InlineData("""[{ "id": 5, "where": {} }]""", "invalidId", 0)]
    [InlineData("""[{ "id": "", "where": {} }]""", "invalidId", 0)]
    [InlineData("""[{ "id": "a", "where": {} }, { "id": "a", "where": {} }]""", "duplicateId", 1)]
    [InlineData("""[{ "id": "bad", "where": { "and": 5 } }]""", "malformedWhere", "bad")]
    public async Task A_malformed_filter_is_INVALID_FILTER_with_the_reason_and_the_entry(string filter, string reason, object? entry)
    {
        var error = (await Errors(filter, "[]")).Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.InvalidFilter);
        error.Stage.Should().BeNull();
        error.Filter.Should().Be(entry);
        JsonSerializer.Serialize(error.Params).Should().Be($$"""{"reason":"{{reason}}"}""");
    }

    [Fact]
    public async Task An_id_is_at_most_64_characters_and_null_or_an_empty_list_is_no_filter()
    {
        (await Errors($$"""[{ "id": "{{new string('x', 65)}}", "where": {} }]""", "[]")).Should().ContainSingle().Which.Code.Should().Be(Codes.InvalidFilter);
        (await Bound($$"""[{ "id": "{{new string('x', 64)}}", "where": {} }]""", "[]")).Filters.Should().ContainSingle().Which.Placed.Should().BeEmpty();
        (await Bound("[]", "[]")).Filters.Should().BeEmpty();
        (await Bound("null", "[]")).Filters.Should().BeEmpty();
    }

    [Fact]
    public async Task More_entries_than_MaxFilterEntries_is_one_refusal_before_an_entry_is_read()
    {
        new OxQLOptions().Limits.MaxFilterEntries.Should().Be(64);

        static string Entries(int count) => "[" + string.Join(",", Enumerable.Range(0, count).Select(index => $$"""{ "id": "e{{index}}", "where": { "number": { "eq": "a" } } }""")) + "]";

        (await Bound(Entries(64), "[]")).Filters.Should().HaveCount(64);

        // One error whatever the entries hold: none is read (the second would be malformed, the rest unknown paths).
        var over = "[" + string.Join(",", Enumerable.Repeat("""{ "where": { "nope": { "eq": 1 } } }""", 64).Prepend("5")) + "]";
        var error = (await Errors(over, "[]")).Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.MaxFilterEntriesExceeded);
        error.Message.Should().Be("The request's filter has 65 entries; the limit is 64.");
        error.Stage.Should().BeNull();
        error.Filter.Should().BeNull();
        JsonSerializer.Serialize(error.Params).Should().Be("""{"limit":"maxFilterEntries","max":64,"count":65}""");

        // The host's own bound holds.
        var two = BindHost.Context(BindHost.Options(options => options.Limits.MaxFilterEntries = 2));

        (await BindHost.BindAsync(BindHost.Probe, Request(Entries(3), "[]"), two)).Should().BeOfType<BindOutcome.Failed>()
            .Subject.Refusal.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.MaxFilterEntriesExceeded);
        (await BindHost.BindAsync(BindHost.Probe, Request(Entries(2), "[]"), two)).Should().BeOfType<BindOutcome.Bound>();
    }

    [Fact]
    public async Task More_conditions_in_the_filter_than_MaxConditions_is_one_refusal_before_any_is_placed_or_bound()
    {
        // One entry of thousands of conjuncts on a path that does not exist: bound, each would be an error of its own.
        var where = "{ \"and\": [" + string.Join(",", Enumerable.Repeat("""{ "nope": { "eq": 1 } }""", 5_000)) + "] }";
        var error = (await Errors($$"""[{ "id": "ok", "where": { "number": { "eq": "a" } } }, { "id": "many", "where": {{where}} }, { "id": "later", "where": { "nope": { "eq": 2 } } }]""", $"[{UnwindItems}, {GroupByNumber}]"))
            .Should().ContainSingle("nothing of the filter is placed once it is over the bound").Subject;

        error.Code.Should().Be(Codes.MaxConditionsExceeded);
        error.Message.Should().Be("The request's filter has more than 200 conditions; the limit is 200.");
        error.Filter.Should().Be("many");
        JsonSerializer.Serialize(error.Params).Should().Be("""{"limit":"maxConditions","max":200}""");

        // Conditions inside or, not and any count as they do in a match.
        var nested = "{ \"or\": [" + string.Join(",", Enumerable.Repeat("""{ "not": { "items": { "any": { "quantity": { "eq": 2 }, "and": [{ "quantity": { "gt": 1 } }] } } } }""", 101)) + "] }";

        (await Errors($$"""[{ "where": {{nested}} }]""", "[]")).Should().ContainSingle().Which.Code.Should().Be(Codes.MaxConditionsExceeded);

        // Exactly the bound binds.
        var full = "{ \"and\": [" + string.Join(",", Enumerable.Repeat("""{ "number": { "neq": "a" } }""", 200)) + "] }";

        (await Bound($$"""[{ "where": {{full}} }]""", "[]")).Filters.Should().ContainSingle();
    }

    [Fact]
    public async Task Placement_looks_at_a_condition_until_it_is_placed_and_never_again()
    {
        // The largest filter a host admits over the longest pipeline: every boundary sees only what is still unplaced.
        var options = BindHost.Options(configure =>
        {
            configure.Limits.MaxFilterEntries = 20_000;
            configure.Limits.MaxConditions = 100_000;
            configure.Limits.MaxPipelineStages = 64;
        });
        var entries = "[" + string.Join(",", Enumerable.Range(0, 20_000).Select(index => $$"""{ "id": "e{{index}}", "where": { "number": { "neq": "n{{index}}" }, "{{(index % 2 == 0 ? "count" : "item.quantity")}}": { "neq": 7 } } }""")) + "]";
        var pipeline = "[" + string.Join(",", Enumerable.Repeat("""{ "sort": [{ "number": "asc" }] }""", 40).Append(UnwindItems).Concat(Enumerable.Repeat("""{ "sort": [{ "number": "asc" }] }""", 20))) + "]";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var bound = await Bound(entries, pipeline, context: BindHost.Context(options));

        timer.Stop();

        bound.Filters.Should().HaveCount(20_000);
        Placed(bound, 0).Should().Be("0:number,count");
        Placed(bound, 1).Should().Be("0:number | 41:item.quantity", "the entity member at the entry, the element's right after the unwind");
        bound.Stages.Count(stage => bound.FilterOf(stage) is not null).Should().Be(30_000, "one match per entry and boundary that placed something");

        // Not a bound on the machine: a placement that scans every condition for every entry at every boundary does
        // 20 000 x 40 000 x 62 steps here (minutes); one that looks at what is still unplaced binds 40 000 conditions.
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task The_conditions_count_toward_MaxConditions_and_an_empty_group_is_refused_as_in_a_match()
    {
        var two = BindHost.Context(BindHost.Options(options => options.Limits.MaxConditions = 2));
        var outcome = await BindHost.BindAsync(BindHost.Probe, Request("""[{ "where": { "number": { "eq": "a" }, "note": { "eq": "b" } } }]""", """[{ "match": { "note": { "eq": "c" } } }]"""), two);

        outcome.Should().BeOfType<BindOutcome.Failed>().Subject.Refusal.Errors.Should().ContainSingle().Which.Code.Should().Be(Codes.MaxConditionsExceeded);

        (await Errors("""[{ "id": "empty", "where": { "or": [] } }]""", "[]")).Should().ContainSingle()
            .Which.Should().Match<QueryValidationError>(error => error.Code == Codes.EmptyLogicalGroup && Equals(error.Filter, "empty") && error.Stage == null);
    }

    [Fact]
    public async Task Contract_1_never_had_the_member_and_ignores_it_whatever_it_holds()
    {
        foreach (var filter in new[] { """[{ "where": { "number": { "eq": "x" } } }]""", "5", """[{ "id": 1 }]""" })
        {
            var bound = await Bound(filter, "[]", context: BindHost.Context(contract: 1));

            bound.Stages.Should().NotContain(stage => stage is BoundStage.Match);
            bound.Filters.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_condition_under_a_lookup_alias_follows_the_alias_through_its_unwind()
    {
        var bound = await Bound("""[{ "id": "any", "where": { "orders.number": { "eq": "x" } } }, { "id": "this", "where": { "order.number": { "eq": "x" } } }]""",
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "unwind": { "path": "orders", "as": "order" } }]""", Customer);

        Kinds(bound).Take(4).Should().Equal("lookup", "filter:any", "unwind", "filter:this");
        Placed(bound, 0).Should().Be("1:orders.number", "some child, while the alias holds them all");
        Placed(bound, 1).Should().Be("2:order.number", "this row's child");
    }
}
