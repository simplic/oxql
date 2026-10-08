using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The static index advisory (DG2): for a request as bound and compiled, the index that serves it as
/// a definition to copy (collection, key document in the order equality, sort, range, collation),
/// what of the request it serves by the paths and filter entries the caller wrote, whether an index
/// the collection has serves it, and what the request does while none does. Nothing here reaches a
/// database: the index lists are fixtures.
/// </summary>
public class IndexAdvisorTests
{
    private const string Order = "probe.order";
    private const string Customer = "probe.customer";

    private const string GridFilter = """
        [{ "id": "col:state", "where": { "state": { "eq": "Open" } } },
         { "id": "col:when", "where": { "when": { "gte": "2026-01-01T00:00:00Z" } } },
         { "id": "hide", "where": { "flag": { "neq": true }, "note": { "contains": "a" } } }]
        """;

    private const string GridRows = """[{ "sort": [{ "count": "desc" }] }, { "page": { "limit": 10 } }]""";

    internal static BsonDocument Index(string name, params (string Field, int Direction)[] keys)
    {
        var key = new BsonDocument();

        foreach (var (field, direction) in keys)
            key[field] = direction;

        return new BsonDocument { ["name"] = name, ["key"] = key };
    }

    private static BsonDocument Collated(BsonDocument index, string locale = "de", int strength = 1)
    {
        index["collation"] = new BsonDocument { ["locale"] = locale, ["strength"] = strength };

        return index;
    }

    internal static async Task<JsonObject> AdviseAsync(string pipeline, string filter = "[]", IReadOnlyList<BsonDocument>? indexes = null,
        Dictionary<string, IReadOnlyList<BsonDocument>>? joined = null, string entity = Order, EntityModel? model = null)
    {
        var request = BindHost.Parse($$"""{ "entityType": "{{entity}}", "filter": {{filter}}, "pipeline": {{pipeline}} }""");
        var outcome = await BindHost.BindAsync(model ?? BindHost.Probe, request);

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));

        var bound = ((BindOutcome.Bound)outcome).Pipeline;

        return IndexAdvisor.Advise(bound, MongoCompiler.Compile(bound, new CompileOptions(10_000, null, 100_000)), indexes, joined);
    }

    private static JsonObject Only(JsonObject advisory) => advisory["indexes"]!.AsArray().Single()!.AsObject();

    private static JsonObject Of(JsonObject advisory, string purpose) =>
        advisory["indexes"]!.AsArray().Select(index => index!.AsObject()).Single(index => index["for"]!.GetValue<string>() == purpose);

    private static string Key(JsonObject index) => index["key"]!.ToJsonString();

    private static List<string> Roles(JsonObject index) =>
        index["keys"]!.AsArray().Select(key => $"{key!["field"]!.GetValue<string>()}:{key["role"]!.GetValue<string>()}").ToList();

    private static List<string> Texts(JsonNode? list) => list?.AsArray().Select(each => each!.GetValue<string>()).ToList() ?? [];

    // ---- the definition ---------------------------------------------------------------------

    [Fact]
    public async Task The_key_is_the_organisation_then_equality_then_the_sort_with_its_key_then_range()
    {
        var index = Only(await AdviseAsync(GridRows, GridFilter));

        index["for"]!.GetValue<string>().Should().Be("rows");
        index["collection"]!.GetValue<string>().Should().Be("orders");
        index["entity"]!.GetValue<string>().Should().Be(Order);
        Key(index).Should().Be("""{"OrganizationId":1,"State":1,"Count":-1,"_id":1,"When":1}""", "equality, sort in the request's directions with the key that completes it, range");
        Roles(index).Should().Equal("OrganizationId:scope", "State:equality", "Count:sort", "_id:sort", "When:range");
        index["keys"]![1]!["path"]!.GetValue<string>().Should().Be("state", "the path as the caller wrote it beside the stored name");
        index["keys"]![0]!.AsObject().ContainsKey("path").Should().BeFalse("the organisation is the engine's own condition");
        index.ContainsKey("collation").Should().BeFalse("no text is compared or ordered");
        index["command"]!.GetValue<string>().Should().Be("""db.getCollection("orders").createIndex({ "OrganizationId": 1, "State": 1, "Count": -1, "_id": 1, "When": 1 })""");

        // What of the request it serves: the filter entries by their ids, the sort by its stage.
        index["serves"]!.ToJsonString().Should().Be(
            """[{"use":"filter","role":"equality","path":"state","op":"eq","filter":"col:state"},"""
            + """{"use":"sort","path":"count","direction":"desc","stage":0},"""
            + """{"use":"filter","role":"range","path":"when","op":"gte","filter":"col:when"}]""");
    }

    [Fact]
    public async Task What_no_key_bounds_is_listed_with_its_reason_and_is_no_key()
    {
        var advisory = await AdviseAsync("""
            [{ "match": { "or": [{ "count": { "eq": 1 } }, { "big": { "eq": 2 } }] } },
             { "resolve": { "path": "customerId", "as": "c" } },
             { "match": { "c.name": { "eq": "x" } } }]
            """, GridFilter);

        Key(Only(advisory)).Should().Be("""{"OrganizationId":1,"State":1,"_id":1,"When":1}""");

        var unbounded = advisory["unbounded"]!.AsArray().Select(each => each!.AsObject()).ToDictionary(each => each["path"]!.GetValue<string>());

        unbounded["flag"]["reason"]!.GetValue<string>().Should().Be("negation");
        unbounded["flag"]["filter"]!.GetValue<string>().Should().Be("hide");
        unbounded["note"]["reason"]!.GetValue<string>().Should().Be("pattern");
        unbounded["count"]["reason"]!.GetValue<string>().Should().Be("or");
        unbounded["count"]["stage"]!.GetValue<int>().Should().Be(0);
        unbounded["c.name"]["reason"]!.GetValue<string>().Should().Be("joined");
    }

    [Fact]
    public async Task A_text_that_folds_needs_the_collation_and_one_compared_exactly_under_it_is_a_pattern()
    {
        var folded = Only(await AdviseAsync("""[{ "match": { "number": { "eq": "x" } } }, { "sort": [{ "shipTo.city": "asc" }] }]"""));

        Key(folded).Should().Be("""{"OrganizationId":1,"Number":1,"ShipTo.City":1,"_id":1}""");
        folded["collation"]!.ToJsonString().Should().Be("""{"locale":"de","strength":1}""");
        folded["command"]!.GetValue<string>().Should().EndWith("""}, { collation: { locale: "de", strength: 1 } })""");

        // Compared exactly, with nothing else that folds, the aggregate has no collation and the index none.
        var exact = Only(await AdviseAsync("""[{ "match": { "number": { "eq": "x", "options": { "caseSensitive": true } } } }, { "sort": [{ "count": "asc" }] }]"""));

        Key(exact).Should().Be("""{"OrganizationId":1,"Number":1,"Count":1,"_id":1}""");
        exact.ContainsKey("collation").Should().BeFalse();

        // Exactly beside a text that folds: the comparison is an anchored pattern, which no key bounds.
        var mixed = await AdviseAsync("""[{ "match": { "number": { "eq": "x", "options": { "caseSensitive": true } } } }, { "sort": [{ "shipTo.city": "asc" }] }]""");

        Key(Only(mixed)).Should().Be("""{"OrganizationId":1,"ShipTo.City":1,"_id":1}""");
        mixed["unbounded"]!.AsArray().Single()!["reason"]!.GetValue<string>().Should().Be("exactUnderCollation");
    }

    [Fact]
    public async Task A_member_held_to_one_value_is_no_sort_key_and_a_set_on_a_sorted_member_is_no_equality()
    {
        var held = Only(await AdviseAsync("""[{ "match": { "count": { "eq": 3 } } }, { "sort": [{ "count": "desc" }, { "when": "asc" }] }]"""));

        Key(held).Should().Be("""{"OrganizationId":1,"Count":1,"When":1,"_id":1}""");

        var set = Only(await AdviseAsync("""[{ "match": { "count": { "in": [1, 2] }, "flag": { "eq": true } } }, { "sort": [{ "count": "desc" }] }]"""));

        Key(set).Should().Be("""{"OrganizationId":1,"Flag":1,"Count":-1,"_id":1}""");
        Roles(set).Should().Contain("Count:sort");
    }

    [Fact]
    public async Task The_same_index_has_the_same_id_whatever_request_asks_for_it()
    {
        var first = Only(await AdviseAsync(GridRows, GridFilter));
        var again = Only(await AdviseAsync("""[{ "sort": [{ "count": "desc" }] }, { "page": { "limit": 50 } }]""",
            """[{ "id": "other", "where": { "state": { "in": ["Shipped"] }, "when": { "lt": "2027-01-01T00:00:00Z" } } }]"""));
        var other = Only(await AdviseAsync("""[{ "sort": [{ "count": "asc" }] }]""", GridFilter));

        first["id"]!.GetValue<string>().Should().MatchRegex("^ix:[0-9a-f]{12}$");
        again["id"]!.GetValue<string>().Should().Be(first["id"]!.GetValue<string>(), "the same collection, keys and collation");
        other["id"]!.GetValue<string>().Should().NotBe(first["id"]!.GetValue<string>(), "another direction is another index");
    }

    // ---- existing indexes -------------------------------------------------------------------

    [Fact]
    public async Task Without_an_index_list_nothing_is_called_missing()
    {
        var advisory = await AdviseAsync(GridRows, GridFilter, indexes: null);
        var index = Only(advisory);

        advisory["existing"]!.GetValue<string>().Should().Be("unknown");
        index["existing"]!.GetValue<string>().Should().Be("unknown");
        index["used"].Should().BeNull();
        index.ContainsKey("without").Should().BeFalse();
        index.ContainsKey("lacks").Should().BeFalse();
        IndexAdvisor.NotesOf(advisory).Single().Message.Should().Contain("is not known").And.Contain("""{ "OrganizationId": 1, "State": 1, "Count": -1, "_id": 1, "When": 1 }""");
    }

    [Fact]
    public async Task An_index_with_the_keys_in_the_requests_directions_or_all_reversed_serves_it()
    {
        var exact = Only(await AdviseAsync(GridRows, GridFilter, [Index("_id_", ("_id", 1)), Index("grid", ("OrganizationId", 1), ("State", 1), ("Count", -1), ("_id", 1), ("When", 1))]));

        exact["existing"]!.GetValue<string>().Should().Be("served");
        exact["used"]!.GetValue<bool>().Should().BeTrue();
        exact["index"]!.GetValue<string>().Should().Be("grid");
        exact.ContainsKey("without").Should().BeFalse();

        // The sort keys all reversed order the rows the same way read backwards; the range key stands behind them, in either direction.
        var reversed = Only(await AdviseAsync(GridRows, GridFilter, [Index("back", ("State", 1), ("OrganizationId", 1), ("Count", 1), ("_id", -1), ("When", -1))]));

        reversed["existing"]!.GetValue<string>().Should().Be("served", "equality keys in any order, then the sort, then the range");
        reversed["index"]!.GetValue<string>().Should().Be("back");

        var half = Only(await AdviseAsync(GridRows, GridFilter, [Index("half", ("OrganizationId", 1), ("State", 1), ("Count", -1), ("_id", -1))]));

        half["existing"]!.GetValue<string>().Should().Be("partial", "one sort key reversed and the other not orders nothing");
        Texts(half["lacks"]).Should().Equal(["sort", "range"], "and what does not order the rows holds no range key behind the order");
        Texts(half["without"]).Should().Equal("sortsInMemory", "scansOrganisation");
    }

    [Fact]
    public async Task What_an_index_lacks_says_what_the_request_does_without_the_advised_one()
    {
        var scoped = Only(await AdviseAsync(GridRows, GridFilter, [Index("_id_", ("_id", 1)), Index("org", ("OrganizationId", 1))]));

        scoped["existing"]!.GetValue<string>().Should().Be("partial");
        scoped["used"]!.GetValue<bool>().Should().BeFalse();
        scoped["index"]!.GetValue<string>().Should().Be("org");
        Texts(scoped["lacks"]).Should().Equal("equality", "sort", "range");
        Texts(scoped["without"]).Should().Equal("sortsInMemory", "scansOrganisation");

        // An index that orders the rows but does not hold the equality: no sort in memory, the organisation's rows read.
        var ordered = Only(await AdviseAsync(GridRows, GridFilter, [Index("org_count", ("OrganizationId", 1), ("Count", -1), ("_id", 1))]));

        Texts(ordered["lacks"]).Should().Equal("equality", "range");
        Texts(ordered["without"]).Should().Equal("scansOrganisation");

        // Nothing leads with the organisation: every row of the collection is a candidate.
        var bare = Only(await AdviseAsync(GridRows, GridFilter, [Index("_id_", ("_id", 1))]));

        bare["existing"]!.GetValue<string>().Should().Be("missing");
        bare.ContainsKey("index").Should().BeFalse();
        Texts(bare["without"]).Should().Equal("sortsInMemory", "scansCollection");

        var note = IndexAdvisor.NotesOf(await AdviseAsync(GridRows, GridFilter, [Index("_id_", ("_id", 1))])).Single();

        note.Code.Should().Be(Notes.IndexAdvice);
        note.Message.Should().StartWith("No index of 'orders' serves the request's filter and order;").And.Contain("orders its rows in memory");
        note.Filter.Should().Be("col:state", "the note stands at the first part of the request the index serves");
        note.Path.Should().Be("state");
        note.Params!["existing"].Should().Be("missing");
        note.Params["used"].Should().Be(false);
        note.Params["id"].Should().Be(bare["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_index_of_another_collation_serves_no_text_and_one_over_part_of_the_rows_nothing()
    {
        const string pipeline = """[{ "match": { "number": { "eq": "x" } } }]""";
        var keys = new[] { ("OrganizationId", 1), ("Number", 1), ("_id", 1) };

        Only(await AdviseAsync(pipeline, indexes: [Collated(Index("de1", keys))]))["existing"]!.GetValue<string>().Should().Be("served");

        var plain = Only(await AdviseAsync(pipeline, indexes: [Index("plain", keys)]));

        plain["existing"]!.GetValue<string>().Should().Be("partial", "it leads with the organisation and bounds no text of a folding comparison");
        Texts(plain["lacks"]).Should().Contain("collation").And.Contain("equality");

        Texts(Only(await AdviseAsync(pipeline, indexes: [Collated(Index("en2", keys), "en", 2)]))["lacks"]).Should().Contain("collation");

        var partial = Collated(Index("some", keys));

        partial["partialFilterExpression"] = new BsonDocument("IsDeleted", false);

        var undecided = Only(await AdviseAsync(pipeline, indexes: [partial]));

        undecided["existing"]!.GetValue<string>().Should().NotBe("served", "an index over part of the rows cannot be counted on");
    }

    [Fact]
    public async Task An_index_over_part_of_the_rows_or_a_sparse_one_is_named_and_not_called_missing_and_a_hidden_one_is_named_beside_the_verdict()
    {
        const string pipeline = """[{ "match": { "number": { "eq": "x" } } }]""";
        var keys = new[] { ("OrganizationId", 1), ("Number", 1), ("_id", 1) };

        BsonDocument With(string name, string member, BsonValue value)
        {
            var index = Collated(Index(name, keys));

            index[member] = value;

            return index;
        }

        var partial = With("active_only", "partialFilterExpression", new BsonDocument("IsDeleted", false));
        var sparse = With("numbered", "sparse", true);
        var hidden = With("parked", "hidden", true);

        // The only index that reaches the keys holds part of the rows: whether it serves this request the list does not say.
        foreach (var (index, kind) in new[] { (partial, "partialFilter"), (sparse, "sparse") })
        {
            var advisory = await AdviseAsync(pipeline, indexes: [Index("_id_", ("_id", 1)), index]);
            var advised = Only(advisory);

            advised["existing"]!.GetValue<string>().Should().Be("unknown", $"{kind}: neither served nor missing is known");
            advised["used"].Should().BeNull();
            advised.ContainsKey("lacks").Should().BeFalse();
            advised.ContainsKey("without").Should().BeFalse("no consequence is claimed of an index that may serve the request");
            advised.ContainsKey("index").Should().BeFalse();
            advised["conditional"]!.ToJsonString().Should().Be($$"""[{"index":"{{index["name"]}}","kind":"{{kind}}"}]""");
            advisory["existing"]!.GetValue<string>().Should().Be("known", "the index list was read");

            var note = IndexAdvisor.NotesOf(advisory).Should().ContainSingle().Subject;

            note.Message.Should().StartWith($"Whether an index of 'orders' serves the request's filter and order is not known: the index '{index["name"]}' reaches its keys and holds ")
                .And.Contain(kind == "sparse" ? "(sparse)" : "(partialFilterExpression)").And.NotContain("read no index list").And.NotContain("No index of");
            note.Params.Should().Contain("existing", "unknown").And.Contain("used", null);
        }

        // A hidden index serves nothing, so the verdict stands; that it exists is said, since unhiding it is all that is needed.
        var parked = await AdviseAsync(pipeline, indexes: [hidden]);

        Only(parked)["existing"]!.GetValue<string>().Should().Be("missing");
        Only(parked)["used"]!.GetValue<bool>().Should().BeFalse();
        Only(parked)["conditional"]!.ToJsonString().Should().Be("""[{"index":"parked","kind":"hidden"}]""");
        IndexAdvisor.NotesOf(parked).Should().ContainSingle().Which.Message.Should().StartWith("No index of 'orders' serves").And.EndWith(" Not counted: the index 'parked' reaches its keys and is hidden from the planner.");

        // An index that is counted and serves it ends the matter: nothing else is named.
        var served = Only(await AdviseAsync(pipeline, indexes: [partial, Collated(Index("all", keys))]));

        served["existing"]!.GetValue<string>().Should().Be("served");
        served.ContainsKey("conditional").Should().BeFalse();

        // Beside an index that is counted and goes as far, the verdict is that index's; the other is named.
        var beside = Only(await AdviseAsync(pipeline, indexes: [sparse, Index("org", ("OrganizationId", 1), ("Number", 1))]));

        beside["existing"]!.GetValue<string>().Should().Be("unknown", "the sparse index reaches the text under the collation, the counted one does not");

        var level = Only(await AdviseAsync("""[{ "match": { "count": { "eq": 3 } } }]""", indexes: [new BsonDocument { ["name"] = "sparse_count", ["key"] = new BsonDocument { ["OrganizationId"] = 1, ["Count"] = 1, ["_id"] = 1 }, ["sparse"] = true }, Index("org", ("OrganizationId", 1))]));

        level["existing"]!.GetValue<string>().Should().Be("unknown", "the sparse index goes further than the counted one");

        var same = Only(await AdviseAsync("""[{ "match": { "count": { "eq": 3 } } }]""", indexes: [new BsonDocument { ["name"] = "sparse_org", ["key"] = new BsonDocument { ["OrganizationId"] = 1 }, ["sparse"] = true }, Index("org", ("OrganizationId", 1))]));

        same["existing"]!.GetValue<string>().Should().Be("partial", "an index that is counted goes as far: its verdict stands");
        same["index"]!.GetValue<string>().Should().Be("org");
        same["conditional"]!.ToJsonString().Should().Be("""[{"index":"sparse_org","kind":"sparse"}]""");
    }

    [Fact]
    public async Task Behind_a_sort_an_index_that_does_not_hold_the_range_key_serves_the_order_and_not_all_of_it()
    {
        // equality, sort, range: the advised index ends in the range key.
        const string filter = """[{ "id": "col:state", "where": { "state": { "eq": 1 } } }, { "id": "col:when", "where": { "when": { "gte": "2026-01-01T00:00:00Z" } } }]""";
        const string pipeline = """[{ "sort": [{ "count": "desc" }] }, { "page": { "limit": 10 } }]""";

        var advisedKey = Key(Only(await AdviseAsync(pipeline, filter)));

        advisedKey.Should().Be("""{"OrganizationId":1,"State":1,"Count":-1,"_id":1,"When":1}""");

        // Without the range key the index orders the rows, and every document it walks is read to compare the date.
        var ordered = Only(await AdviseAsync(pipeline, filter, indexes: [Index("ordered", ("OrganizationId", 1), ("State", 1), ("Count", -1), ("_id", 1))]));

        ordered["existing"]!.GetValue<string>().Should().Be("partial", "it lacks the key the range is read off");
        ordered["used"]!.GetValue<bool>().Should().BeFalse();
        ordered["index"]!.GetValue<string>().Should().Be("ordered");
        Texts(ordered["lacks"]).Should().Equal("range");
        Texts(ordered["without"]).Should().Equal(["scansOrganisation"], "nothing is ordered in memory: the rows are read in order to find those in the range");

        // With it, anywhere behind the sort keys, the index serves all of it.
        foreach (var keys in new[] { new[] { ("OrganizationId", 1), ("State", 1), ("Count", -1), ("_id", 1), ("When", 1) }, [("OrganizationId", 1), ("State", 1), ("Count", 1), ("_id", -1), ("Flag", 1), ("When", -1)] })
            Only(await AdviseAsync(pipeline, filter, indexes: [Index("full", keys)]))["existing"]!.GetValue<string>().Should().Be("served");

        // The range key before the sort keys bounds the range and no longer orders: the sort is what it lacks.
        Texts(Only(await AdviseAsync(pipeline, filter, indexes: [Index("ranged", ("OrganizationId", 1), ("State", 1), ("When", 1), ("Count", -1), ("_id", 1))]))["lacks"]).Should().Contain("sort");
    }

    // ---- values, groups, joins, unwound rows ------------------------------------------------

    [Fact]
    public async Task A_list_of_values_gets_an_index_of_its_own_without_a_collation_that_holds_all_it_reads()
    {
        const string filter = """[{ "id": "hide", "where": { "flag": { "neq": true } } }, { "id": "col:when", "where": { "when": { "gte": "2026-01-01T00:00:00Z" } } }, { "id": "col:number", "where": { "number": { "eq": "x" } } }]""";
        var advisory = await AdviseAsync("""[{ "values": { "path": "shipTo.city", "counts": true } }]""", filter);
        var index = Only(advisory);

        index["for"]!.GetValue<string>().Should().Be("values");
        Key(index).Should().Be("""{"OrganizationId":1,"When":1,"ShipTo.City":1,"Flag":1,"Number":1}""", "what bounds the rows, the path, then what else the conditions read");
        Roles(index).Should().Equal("OrganizationId:scope", "When:range", "ShipTo.City:values", "Flag:covered", "Number:covered");
        index["covers"]!.GetValue<bool>().Should().BeTrue();
        index.ContainsKey("collation").Should().BeFalse("a collated index makes the database read every document for the values");
        index["note"]!.GetValue<string>().Should().Contain("without a collation").And.Contain("prefix");
        index["serves"]!.AsArray().Select(serve => serve!["use"]!.GetValue<string>()).Should().Contain("values");

        // The text the request compares under the collation is held, not bounded, and says so.
        advisory["unbounded"]!.AsArray().Select(each => $"{each!["path"]!.GetValue<string>()}:{each["reason"]!.GetValue<string>()}").Should().BeEquivalentTo("flag:negation", "number:heldNotBounded");

        // An index of the same keys built with the collation does not stand for the documents.
        var keys = new[] { ("OrganizationId", 1), ("When", 1), ("ShipTo.City", 1), ("Flag", 1), ("Number", 1) };
        var collated = Only(await AdviseAsync("""[{ "values": { "path": "shipTo.city" } }]""", filter, [Collated(Index("de1", keys))]));

        collated["existing"]!.GetValue<string>().Should().Be("partial");
        Texts(collated["lacks"]).Should().Equal("covering", "collation");
        Texts(collated["without"]).Should().Equal("readsDocuments");
        Only(await AdviseAsync("""[{ "values": { "path": "shipTo.city" } }]""", filter, [Index("plain", keys)]))["existing"]!.GetValue<string>().Should().Be("served");
    }

    [Fact]
    public async Task A_list_of_values_under_a_collection_is_read_off_the_documents_and_says_so()
    {
        var index = Only(await AdviseAsync("""[{ "values": { "path": "items.quantity" } }]""", """[{ "id": "col:count", "where": { "count": { "eq": 2 } } }]"""));

        Key(index).Should().Be("""{"OrganizationId":1,"Count":1}""", "the conditions still bound the rows read");
        index.ContainsKey("covers").Should().BeFalse();
        index["note"]!.GetValue<string>().Should().Contain("under a collection");
    }

    [Fact]
    public async Task A_group_gets_an_index_that_bounds_its_rows_and_holds_its_keys_and_what_its_aggregates_read()
    {
        var advisory = await AdviseAsync("""
            [{ "match": { "flag": { "eq": true }, "when": { "gte": "2026-01-01T00:00:00Z" } } },
             { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true }, "total": { "sum": "amount" } } } },
             { "match": { "n": { "gt": 1 } } }, { "sort": [{ "n": "desc" }] }]
            """);
        var index = Only(advisory);

        index["for"]!.GetValue<string>().Should().Be("group");
        Key(index).Should().Be("""{"OrganizationId":1,"Flag":1,"When":1,"State":1,"Amount":1}""");
        Roles(index).Should().Equal("OrganizationId:scope", "Flag:equality", "When:range", "State:group", "Amount:covered");
        index["covers"]!.GetValue<bool>().Should().BeTrue();
        index["serves"]!.AsArray().Select(serve => serve!["use"]!.GetValue<string>()).Should().Equal("filter", "filter", "group", "aggregate");
        advisory["unbounded"]!.AsArray().Single()!.ToJsonString().Should().Be("""{"use":"filter","path":"n","op":"gt","reason":"afterGroup","stage":2}""", "a condition on the group's row and its order are no index's");

        // A text the collation bounds needs the collation, which keeps the documents read: bounding comes first.
        var text = Only(await AdviseAsync("""[{ "match": { "number": { "eq": "x" } } }, { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }]"""));

        Key(text).Should().Be("""{"OrganizationId":1,"Number":1}""");
        text["collation"]!.ToJsonString().Should().Be("""{"locale":"de","strength":1}""");
        text.ContainsKey("covers").Should().BeFalse();
    }

    [Fact]
    public async Task A_join_onto_a_member_that_is_not_the_key_gets_an_index_of_the_joined_collection()
    {
        const string pipeline = """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "when": "desc" }] } }, { "match": { "orders.number": { "eq": "x" } } }]""";
        var advisory = await AdviseAsync(pipeline, entity: Customer, indexes: [Index("_id_", ("_id", 1))]);
        var join = Of(advisory, "join");

        join["collection"]!.GetValue<string>().Should().Be("orders");
        join["entity"]!.GetValue<string>().Should().Be(Order);
        Key(join).Should().Be("""{"OrganizationId":1,"CustomerId":1,"When":-1,"_id":1}""", "the joined collection's organisation, the member joined on, the order of the children");
        Roles(join).Should().Equal("OrganizationId:scope", "CustomerId:join", "When:sort", "_id:sort");
        join["serves"]!.ToJsonString().Should().Be("""[{"use":"join","alias":"orders","path":"customerId","stage":0}]""");
        join["existing"]!.GetValue<string>().Should().Be("unknown", "the index list of the joined collection was not read");
        Of(advisory, "rows")["existing"]!.GetValue<string>().Should().Be("partial", "the entity's own list was");

        var listed = new Dictionary<string, IReadOnlyList<BsonDocument>> { ["orders"] = [Index("_id_", ("_id", 1))] };
        var scanned = Of(await AdviseAsync(pipeline, entity: Customer, indexes: [], joined: listed), "join");

        scanned["existing"]!.GetValue<string>().Should().Be("missing");
        Texts(scanned["without"]).Should().Equal("scansPerRow");

        // An index that leads with the organisation and stops there: the organisation's rows of the joined collection are read per row, not all of them.
        listed["orders"] = [Index("org", ("OrganizationId", 1))];

        var scoped = await AdviseAsync(pipeline, entity: Customer, indexes: [], joined: listed);

        Of(scoped, "join")["existing"]!.GetValue<string>().Should().Be("partial");
        Texts(Of(scoped, "join")["without"]).Should().Equal("scansOrganisationPerRow");
        IndexAdvisor.NotesOf(scoped).Single(note => note.Message.Contains("the join 'orders'")).Message
            .Should().Contain("reads the organisation's rows of the joined collection once for every row that joins").And.NotContain("every row of the joined collection");

        listed["orders"] = [Index("customer", ("CustomerId", 1))];

        var reached = Of(await AdviseAsync(pipeline, entity: Customer, indexes: [], joined: listed), "join");

        reached["existing"]!.GetValue<string>().Should().Be("partial", "the member joined on is reached; the children are ordered in memory");
        Texts(reached["lacks"]).Should().Equal("sort");

        listed["orders"] = [Index("full", ("OrganizationId", 1), ("CustomerId", 1), ("When", -1), ("_id", 1))];
        Of(await AdviseAsync(pipeline, entity: Customer, indexes: [], joined: listed), "join")["existing"]!.GetValue<string>().Should().Be("served");

        IndexAdvisor.NotesOf(await AdviseAsync(pipeline, entity: Customer, indexes: [], joined: listed)).Should().Contain(note => note.Stage == 0 && note.Path == "customerId" && note.Message.Contains("the join 'orders'"));

        // A join onto the key has the key's own index: nothing is advised for it.
        (await AdviseAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "match": { "c.name": { "eq": "x" } } }]"""))["indexes"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Unwound_rows_ordered_by_their_parents_get_the_parents_index_and_those_ordered_by_an_element_none_for_the_order()
    {
        var parents = await AdviseAsync("""
            [{ "unwind": { "path": "items", "as": "it", "includeIndex": "i" } },
             { "match": { "it.quantity": { "gt": 1 }, "count": { "eq": 2 } } }, { "sort": [{ "when": "desc" }] }]
            """);
        var index = Only(parents);

        Key(index).Should().Be("""{"OrganizationId":1,"Count":1,"When":-1,"_id":1,"Items.Quantity":1}""");
        index["multikey"]!.GetValue<bool>().Should().BeTrue("a condition on the element is asked of the collection before the unwind");
        index["keys"]![4]!["path"]!.GetValue<string>().Should().Be("it.quantity", "as the caller wrote it, under the alias");

        var element = await AdviseAsync("""[{ "unwind": { "path": "items", "as": "it", "includeIndex": "i" } }, { "sort": [{ "it.quantity": "desc" }] }]""");

        Key(Only(element)).Should().Be("""{"OrganizationId":1}""");
        element["unbounded"]!.AsArray().Should().Contain(each => each!["use"]!.GetValue<string>() == "sort" && each["path"]!.GetValue<string>() == "it.quantity" && each["reason"]!.GetValue<string>() == "afterUnwind");
    }
}
