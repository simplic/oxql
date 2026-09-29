using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// Explain's describe (DESIGN §4.2, §4.5) and the remote check (§4.3): the children of a prefix or
/// exact paths, before a pipeline index or in an entity's entry shape, with the member's facts and
/// the flags <see cref="Shape.Resolve"/> answers for every usage; the describes of remote aliases and
/// entities answered by the owner's internal explain; the parts continued at an owner checked there,
/// its errors mapped back; <c>REMOTE_UNCHECKED</c> where no owner answered; the 30-second forwarding cache.
/// </summary>
public class ExplainDescribeTests
{
    private const string Invoice = ResolveModel.Invoice;

    /// <summary>An unwound line, a local inline resolve, a local keyed item resolve with its owning row, a plain remote resolve.</summary>
    private const string Joins = """
        [{ "unwind": { "path": "lines", "as": "line" } },
         { "resolve": { "path": "customerId", "as": "c" } },
         { "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "sh" } },
         { "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } }]
        """;

    /// <summary>A stage continued at the remote owner of <c>ct</c>.</summary>
    private const string Continued = """
        [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
         { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }]
        """;

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static MongoQueryEngine Engine(FakeRemoteClient? client = null, Action<OxQLOptions>? configure = null, ExplainForwardCache? cache = null) =>
        new(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(configure), client,
            explainCache: cache);

    private static async Task<ExplainResult> ExplainAsync(string pipeline, string describe, FakeRemoteClient? client = null, Action<OxQLOptions>? configure = null,
        string entity = Invoice, string? remote = null, MongoQueryEngine? engine = null)
    {
        var body = $$"""{ "query": { "entityType": "{{entity}}", "pipeline": {{pipeline}} }, "describe": {{describe}}{{(remote is null ? "" : $", \"remote\": \"{remote}\"")}} }""";
        var request = JsonSerializer.Deserialize<ExplainRequest>(body, OxQLJson.Wire)!;
        var outcome = await (engine ?? Engine(client, configure)).ExplainAsync(request, BindHost.Context(BindHost.Options(configure)));

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    private static JsonObject Answer(ExplainResult result, string id) =>
        result.Describe.Select(node => node.AsObject()).Single(entry => entry["id"]!.GetValue<string>() == id);

    private static List<JsonObject> Children(JsonObject answer) => answer["children"]!.AsArray().Select(node => node!.AsObject()).ToList();

    private static JsonObject ChildAt(JsonObject answer, string path) => Children(answer).Single(child => child["path"]!.GetValue<string>() == path);

    private static List<string> Strings(JsonNode? array) => array!.AsArray().Select(node => node!.GetValue<string>()).ToList();

    // ---- the children index ---------------------------------------------------------------------------

    [Fact]
    public void The_children_index_lists_each_paths_children_one_segment_below_in_walk_order()
    {
        var invoice = ResolveModel.Model.Entities[Invoice];

        invoice.ChildrenOf("").Select(path => path.Wire).Should().Equal(invoice.Root.Members.Select(member => member.WireName));
        invoice.ChildrenOf("lines").Select(path => path.Wire).Should().Equal("lines.id", "lines.customerId", "lines.parts");
        invoice.ChildrenOf("lines.parts").Select(path => path.Wire).Should().Equal("lines.parts.customerId");
        invoice.ChildrenOf("number").Should().BeEmpty();
        invoice.Children.Values.Sum(list => list.Count).Should().Be(invoice.Paths.Count, "every path is the child of exactly one parent");
    }

    // ---- flags = Shape.Resolve over paths × usages -------------------------------------------------------

    public static TheoryData<string> Usages() => new(Describe.Usages);

    [Theory]
    [MemberData(nameof(Usages))]
    public async Task Every_childs_flags_and_error_are_what_Shape_Resolve_answers_for_each_usage(string usage)
    {
        var result = await ExplainAsync("[]", $$"""[{ "id": "all", "at": 0, "prefix": "", "usage": "{{usage}}", "depth": 3 }]""");
        var shape = Shape.ForEntity(ResolveModel.Model.Entities[Invoice]);
        var asked = usage switch
        {
            "match" or "resolve" => PathUsage.Match,
            "sort" => PathUsage.Sort,
            "unwind" => PathUsage.Unwind,
            "groupKey" => PathUsage.GroupKey,
            "aggregate" => PathUsage.Aggregate,
            "select" => PathUsage.Select,
            _ => PathUsage.Project,
        };
        var children = Children(Answer(result, "all"));

        children.Select(child => child["path"]!.GetValue<string>()).Should().Contain(["number", "lines", "lines.parts", "lines.parts.customerId", "slot.holderId"]);

        foreach (var child in children)
        {
            var path = child["path"]!.GetValue<string>();
            var match = shape.Resolve(path, PathUsage.Match);
            var sort = shape.Resolve(path, PathUsage.Sort);
            var unwind = shape.Resolve(path, PathUsage.Unwind);
            var group = shape.Resolve(path, PathUsage.GroupKey);
            var expectedError = usage == "lookupOn" ? Codes.LookupOnNotEntity : shape.Resolve(path, asked).Code;

            child["filterable"]!.GetValue<bool>().Should().Be(match.Path?.Filterable == true, path);
            child["sortable"]!.GetValue<bool>().Should().Be(sort.Path?.Sortable == true, path);
            child["projectable"]!.GetValue<bool>().Should().Be(shape.Resolve(path, PathUsage.Project).Succeeded, path);
            child["unwindable"]!.GetValue<bool>().Should().Be(unwind.Path is { Kind: Kind.Array, CollectionAncestors: 0, Storage: not null }, path);
            child["groupable"]!.GetValue<bool>().Should().Be(group.Path is { CollectionAncestors: 0 } key && key.Kind != Kind.Array && Kinds.IsScalar(key.Kind), path);
            child["underCollection"]!.GetValue<int>().Should().Be(shape.Resolve(path, PathUsage.Project).Path!.CollectionAncestors, path);
            var code = child["error"]?["code"]?.GetValue<string>();

            code.Should().Be(expectedError, path);
        }
    }

    // ---- children, roots and facts --------------------------------------------------------------------

    [Fact]
    public async Task A_describe_at_a_stage_lists_the_members_then_the_aliases_and_a_prefix_its_children_to_the_depth_asked()
    {
        var result = await ExplainAsync(Joins, """
            [{ "id": "roots", "at": 4, "prefix": "", "usage": "project" },
             { "id": "line", "at": 4, "prefix": "line", "usage": "match", "depth": 2 },
             { "id": "entry", "at": 0, "prefix": "", "usage": "project" }]
            """);
        var roots = Answer(result, "roots");

        roots["root"]!.ToJsonString().Should().Be("""{"node":"entity","entity":"rc.invoice"}""");
        roots["forwarded"]!.GetValue<bool>().Should().BeFalse();
        Children(roots).Select(child => child["path"]!.GetValue<string>()).TakeLast(5).Should().Equal("line", "c", "bl", "sh", "ct");
        Children(Answer(result, "entry")).Select(child => child["name"]!.GetValue<string>()).Should().NotContain("line", "the alias is created later");

        var line = Answer(result, "line");
        line["root"]!.ToJsonString().Should().Be("""{"node":"element","entity":"rc.invoice","source":"lines"}""");
        Children(line).Select(child => child["path"]!.GetValue<string>()).Should().Equal("line.id", "line.customerId", "line.parts", "line.parts.customerId");
        ChildAt(line, "line.parts.customerId")["notes"]!.ToJsonString().Should().Be("""["SOME_ELEMENT"]""");
        ChildAt(line, "line.parts.customerId")["underCollection"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task A_child_carries_its_kind_operators_folding_variants_and_member_facts()
    {
        var answer = Answer(await ExplainAsync("[]", """[{ "id": "d", "at": 0, "paths": ["number", "id", "slot", "lines", "customerIds", "slot.licence"], "usage": "match" }]"""), "d");

        var number = ChildAt(answer, "number");
        number["kind"]!.GetValue<string>().Should().Be("string");
        number["caseFolding"]!.GetValue<string>().Should().Be(Describe.Folds);
        Strings(number["operators"]).Should().Equal("eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex");
        Strings(number["notes"]).Should().Equal(Notes.TextFolds);

        var id = ChildAt(answer, "id");
        id["caseFolding"]!.GetValue<string>().Should().Be(Describe.NoFolding);
        Strings(id["operators"]).Should().Equal("eq", "neq", "in", "nin", "exists");
        id["nullable"]!.GetValue<bool>().Should().BeFalse();
        id["stored"]!.GetValue<bool>().Should().BeTrue();

        var slot = ChildAt(answer, "slot");
        Strings(slot["operators"]).Should().Equal("exists", "is");
        Strings(slot["variants"]).Should().Equal("RcSlot", "RcDriverSlot", "RcVehicleSlot");
        slot["hasChildren"]!.GetValue<bool>().Should().BeTrue();

        Strings(ChildAt(answer, "lines")["operators"]).Should().Equal("exists", "any");
        ChildAt(answer, "lines")["collection"]!.GetValue<bool>().Should().BeTrue();
        ChildAt(answer, "customerIds")["leafKind"]!.GetValue<string>().Should().Be("guid");

        var licence = ChildAt(answer, "slot.licence");
        Strings(licence["onlyFor"]).Should().Equal("RcDriverSlot");
        Strings(licence["notes"]).Should().Contain(Notes.OnlyForVariants);
    }

    [Fact]
    public async Task A_reference_lists_every_case_and_how_a_resolve_may_follow_it_here()
    {
        var answer = Answer(await ExplainAsync("[]", """[{ "id": "d", "at": 0, "paths": ["source.id", "customerIds", "shipmentKey", "number"], "usage": "resolve" }]"""), "d");

        var source = ChildAt(answer, "source.id")["reference"]!;
        source["simple"]!.GetValue<bool>().Should().BeFalse();
        source["cases"]![0]!.ToJsonString().Should().Be(
            """{"when":{"path":"type","equals":["logistics"]},"keyAs":null,"targets":[{"entity":"rc.shipment","field":"id","item":"billingLines","remote":false},{"entity":"rc.tour","field":"id","item":"billingLines","remote":false}]}""");
        source["cases"]![1]!["targets"]![0]!["remote"]!.GetValue<bool>().Should().BeTrue();
        source["followable"]!.ToJsonString().Should().Be("""{"one":true,"elements":null}""");

        ChildAt(answer, "customerIds")["reference"]!["followable"]!.ToJsonString().Should().Be("""{"one":false,"elements":["first","all"]}""");
        ChildAt(answer, "shipmentKey")["reference"]!["keyAs"]!.GetValue<string>().Should().Be("guid");
        ChildAt(answer, "number")["reference"].Should().BeNull();
    }

    [Fact]
    public async Task An_entity_describe_answers_its_entry_shape_an_item_target_its_element_and_referencing_the_same_host_references()
    {
        var result = await ExplainAsync("[]", """
            [{ "id": "item", "entity": "rc.shipment#billingLines", "prefix": "", "usage": "match" },
             { "id": "referenced", "entity": "rc.customer", "prefix": "", "usage": "project", "referencing": true },
             { "id": "plain", "entity": "rc.customer", "prefix": "", "usage": "project" }]
            """);

        var item = Answer(result, "item");
        item["root"]!.ToJsonString().Should().Be("""{"node":"element","entity":"rc.shipment","source":"billingLines"}""");
        Children(item).Select(child => child["path"]!.GetValue<string>()).Should().Equal("id", "code", "amount");

        var referenced = Answer(result, "referenced");
        ChildAt(referenced, "id")["referencedBy"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>())
            .Should().Contain(["customerId", "customerIds", "lines.customerId", "localSource.id", "slot.holderId"]);
        ChildAt(referenced, "code")["referencedBy"]!.ToJsonString().Should().Be("""[{"entity":"rc.invoice","path":"customerCode","service":"rc","remote":false,"lookup":true}]""");
        ChildAt(Answer(result, "plain"), "id")["referencedBy"].Should().BeNull("only referencing: true asks for them");
    }

    [Fact]
    public async Task Under_a_keyed_alias_the_targets_members_are_described_with_what_the_origin_allows_after_the_page()
    {
        var result = await ExplainAsync(Joins, """
            [{ "id": "bl", "at": 4, "prefix": "bl", "usage": "match" },
             { "id": "roots", "at": 4, "prefix": "", "usage": "match" }]
            """);
        var keyed = Answer(result, "bl");

        keyed["root"]!.ToJsonString().Should().Be("""{"node":"keyed","entities":["rc.shipment#billingLines"]}""");
        Children(keyed).Select(child => child["path"]!.GetValue<string>()).Should().Equal("bl.id", "bl.code", "bl.amount");
        Children(keyed).Should().OnlyContain(child => !child["filterable"]!.GetValue<bool>() && !child["sortable"]!.GetValue<bool>() && child["projectable"]!.GetValue<bool>());
        ChildAt(keyed, "bl.code")["error"]!["code"]!.GetValue<string>().Should().Be(Codes.ResolveNotFilterable);
        ChildAt(keyed, "bl.code")["kind"]!.GetValue<string>().Should().Be("string", "the target's facts");
        ChildAt(Answer(result, "roots"), "sh")["error"]!["code"]!.GetValue<string>().Should().Be(Codes.ResolveNotFilterable);
    }

    [Fact]
    public async Task A_request_that_does_not_bind_is_still_described_up_to_where_it_binds()
    {
        var result = await ExplainAsync("""[{ "unwind": { "path": "lines", "as": "line" } }, { "match": { "line.nope": { "eq": 1 } } }]""",
            """[{ "id": "d", "at": 1, "prefix": "line", "usage": "match" }]""");

        result.Valid.Should().BeFalse();
        Children(Answer(result, "d")).Select(child => child["path"]!.GetValue<string>()).Should().Equal("line.id", "line.customerId", "line.parts");
    }

    // ---- forwarded describes -------------------------------------------------------------------------

    private static JsonObject OwnerDescribe(ExplainRequest request, params JsonObject[] children)
    {
        var entry = request.Describe.Single();

        return new JsonObject
        {
            ["valid"] = true,
            ["describe"] = new JsonArray(new JsonObject
            {
                ["id"] = entry["id"]!.DeepClone(),
                ["entity"] = entry["entity"]!.DeepClone(),
                ["prefix"] = "",
                ["usage"] = entry["usage"]!.DeepClone(),
                ["root"] = new JsonObject { ["node"] = "entity", ["entity"] = entry["entity"]!.DeepClone() },
                ["forwarded"] = false,
                ["truncated"] = false,
                ["children"] = new JsonArray(children),
            }),
        };
    }

    private static JsonObject OwnerChild(string path, string kind) => new()
    {
        ["name"] = path,
        ["path"] = path,
        ["kind"] = kind,
        ["filterable"] = true,
        ["sortable"] = true,
        ["unwindable"] = false,
        ["groupable"] = true,
        ["operators"] = new JsonArray("eq", "neq", "in", "nin", "contains", "exists"),
        ["caseFolding"] = Describe.Folds,
        ["notes"] = new JsonArray(),
        ["error"] = null,
    };

    [Fact]
    public async Task A_remote_alias_and_a_remote_entity_are_described_by_the_owners_internal_explain_and_the_flags_are_the_origins()
    {
        var client = new FakeRemoteClient { Explains = (_, request) => OwnerDescribe(request, OwnerChild("name", "string")) };

        var result = await ExplainAsync(Joins, """
            [{ "id": "ct", "at": 4, "prefix": "ct", "usage": "sort" },
             { "id": "entity", "entity": "crm.contact", "prefix": "", "usage": "match" }]
            """, client);

        var alias = Answer(result, "ct");
        alias["forwarded"]!.GetValue<bool>().Should().BeTrue();
        alias["root"]!.ToJsonString().Should().Be("""{"node":"remote","entities":["crm.contact"]}""");

        var name = ChildAt(alias, "ct.name");
        name["filterable"]!.GetValue<bool>().Should().BeTrue("a member of a plain remote resolve filters as a semi-join");
        name["sortable"]!.GetValue<bool>().Should().BeFalse("the owner's rows cannot order this host's page");
        name["groupable"]!.GetValue<bool>().Should().BeFalse();
        name["error"]!["code"]!.GetValue<string>().Should().Be(Codes.ResolveNotSortable);
        Strings(name["notes"]).Should().Contain(Notes.OwnerBinds);

        var entity = Answer(result, "entity");
        entity["forwarded"]!.GetValue<bool>().Should().BeTrue();
        entity["id"]!.GetValue<string>().Should().Be("entity");
        ChildAt(entity, "name")["sortable"]!.GetValue<bool>().Should().BeTrue("an entity describe is the owner's answer as it stands");

        var describes = client.ExplainCalls.Where(call => call.Request.Remote == ExplainRequest.RemoteSkip).ToList();
        describes.Should().HaveCount(2);
        describes.Should().OnlyContain(call => call.Service == "crm" && call.Budget <= TimeSpan.FromMilliseconds(1_500));
        describes[0].Request.Query.EntityType.Should().Be("crm.contact");
        client.ExplainCalls.Should().ContainSingle(call => call.Request.Remote == ExplainRequest.RemoteCheck, "ct's written select is checked at its owner as well");
    }

    [Fact]
    public async Task An_owner_that_does_not_answer_a_describe_leaves_an_error_on_the_entry_and_a_REMOTE_UNCHECKED_note()
    {
        var client = new FakeRemoteClient();
        client.Unreachable.Add("crm");

        var result = await ExplainAsync("[]", """[{ "id": "e", "entity": "crm.contact", "prefix": "", "usage": "match" }, { "id": "x", "entity": "nowhere.thing", "prefix": "", "usage": "match" }]""",
            new FakeRemoteClient { Configured = ["crm"], Explains = null });
        var unreachable = await ExplainAsync("[]", """[{ "id": "e", "entity": "crm.contact", "prefix": "", "usage": "match" }]""", client);

        Answer(result, "e")["error"]!["code"]!.GetValue<string>().Should().Be(Codes.ResolveUnreachable);
        Answer(result, "x")["error"]!["code"]!.GetValue<string>().Should().Be(Codes.UnknownEntity, "no owner is configured for 'nowhere'");
        result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Which.Params!["reason"].Should().Be(RemoteExplain.Unsupported);
        unreachable.Notes.Single(note => note.Code == Notes.RemoteUnchecked).Params!["reason"].Should().Be(RemoteExplain.Unreachable);
    }

    [Fact]
    public async Task The_owner_calls_share_the_remote_budget_and_a_silent_owner_times_out_into_a_note()
    {
        var client = new FakeRemoteClient();
        client.Silent.Add("crm");

        var result = await ExplainAsync("[]", """[{ "id": "e", "entity": "crm.contact", "prefix": "", "usage": "match" }, { "id": "f", "entity": "crm.company", "prefix": "", "usage": "match" }]""",
            client, options => options.Explain.RemoteTimeoutMs = 50);

        result.Notes.Where(note => note.Code == Notes.RemoteUnchecked).Select(note => note.Params!["reason"]).Should().Equal(RemoteExplain.Timeout, RemoteExplain.Timeout);
        client.ExplainCalls.Should().ContainSingle("the second describe finds the budget spent");
    }

    // ---- entries and caps ------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "id": "d", "at": 0, "usage": "match", "colour": 1 }""", Codes.UnknownStageMember)]
    [InlineData("""{ "id": "d", "at": 0 }""", Codes.UnknownStageMember)]
    [InlineData("""{ "id": "d", "at": 0, "usage": "filter" }""", Codes.UnknownStageMember)]
    [InlineData("""{ "id": "d", "at": 0, "entity": "rc.customer", "usage": "match" }""", Codes.UnknownStageMember)]
    [InlineData("""{ "id": "d", "usage": "match" }""", Codes.UnknownStageMember)]
    [InlineData("""{ "id": "d", "at": 0, "prefix": "", "paths": ["id"], "usage": "match" }""", Codes.UnknownStageMember)]
    [InlineData("""{ "id": "d", "at": 0, "prefix": "", "usage": "match", "depth": 4 }""", Codes.InvalidOperand)]
    [InlineData("""{ "id": "d", "at": 3, "prefix": "", "usage": "match" }""", Codes.InvalidOperand)]
    [InlineData("""{ "id": "d", "at": 0, "prefix": "", "usage": "match", "referencing": true }""", Codes.OptionNotApplicable)]
    [InlineData("""{ "id": "d", "at": 0, "prefix": "nope", "usage": "match" }""", Codes.UnknownPath)]
    [InlineData("""{ "id": "d", "entity": "rc.shipment#number", "usage": "match" }""", Codes.UnknownPath)]
    public async Task A_malformed_describe_entry_is_answered_with_the_code_of_what_is_wrong_and_the_others_still_are(string entry, string code)
    {
        var result = await ExplainAsync("""[{ "match": { "number": { "eq": "a" } } }]""", $$"""[{{entry}}, { "id": "ok", "at": 0, "prefix": "", "usage": "match" }]""");

        result.Describe[0]!["error"]!["code"]!.GetValue<string>().Should().Be(code);
        result.Describe[0]!["children"]!.AsArray().Should().BeEmpty();
        Answer(result, "ok")["error"].Should().BeNull();
    }

    [Fact]
    public async Task The_describe_requests_past_the_cap_are_refused_one_by_one_and_the_children_past_the_cap_truncate_the_answer()
    {
        var result = await ExplainAsync("[]", """
            [{ "id": "a", "at": 0, "prefix": "", "usage": "match" },
             { "id": "b", "at": 0, "prefix": "", "usage": "match" },
             { "id": "c", "at": 0, "prefix": "", "usage": "match" }]
            """, configure: options => { options.Explain.MaxDescribeRequests = 2; options.Explain.MaxDescribeChildren = 3; });

        Answer(result, "a")["truncated"]!.GetValue<bool>().Should().BeTrue();
        Children(Answer(result, "a")).Should().HaveCount(3);
        Answer(result, "c")["error"]!["code"]!.GetValue<string>().Should().Be(Codes.RequestTooLarge);
    }

    // ---- the remote check -----------------------------------------------------------------------------

    [Fact]
    public async Task A_continued_stage_the_owner_refuses_is_an_error_at_the_callers_stage_and_the_answer_is_not_valid()
    {
        var client = new FakeRemoteClient
        {
            Explains = (_, _) => new JsonObject
            {
                ["valid"] = false,
                ["errors"] = new JsonArray(
                    new JsonObject { ["code"] = "UNKNOWN_PATH", ["message"] = "'companyId' is not a path of crm.contact.", ["stage"] = 1, ["path"] = "companyId" },
                    new JsonObject { ["code"] = "INVALID_OPERAND", ["message"] = "the key", ["stage"] = 0, ["path"] = "id" }),
            },
        };

        var result = await ExplainAsync(Continued, "[]", client);

        result.Valid.Should().BeFalse();
        result.Bound.Should().BeNull();
        result.Stages.Should().BeNull();
        var error = result.Errors.Should().ContainSingle("an error at the owner query's own stages is this host's check key, not the caller's").Subject;
        error.Code.Should().Be("UNKNOWN_PATH");
        error.Stage.Should().Be(1);
        error.Path.Should().Be("r.companyId");
        JsonSerializer.SerializeToNode(error.Params!["owner"], OxQLJson.Wire)!.ToJsonString().Should().Be(
            """{"service":"crm","entity":"crm.contact","target":"crm.contact","stage":1,"path":"companyId"}""");
        result.Steps[1].Status.Should().Be("error");

        var sent = client.ExplainCalls.Single();
        sent.Service.Should().Be("crm");
        sent.Request.Remote.Should().Be(ExplainRequest.RemoteCheck, "the owner checks what it continues further");
        JsonSerializer.Serialize(sent.Request.Query.Pipeline[0], OxQLJson.Wire).Should().Contain(KeyedFetch.CheckKey);
        sent.Request.Query.Pipeline[1].Resolve!.Path.Should().Be("companyId");
    }

    [Fact]
    public async Task An_owner_that_binds_the_continued_stage_leaves_the_answer_valid_and_passes_its_own_unchecked_parts_on()
    {
        var client = new FakeRemoteClient
        {
            Explains = (_, _) => new JsonObject
            {
                ["valid"] = true,
                ["errors"] = new JsonArray(),
                ["notes"] = new JsonArray(new JsonObject { ["code"] = Notes.RemoteUnchecked, ["message"] = "further", ["stage"] = 1, ["params"] = new JsonObject { ["service"] = "hr" } }),
            },
        };

        var result = await ExplainAsync(Continued, "[]", client);

        result.Valid.Should().BeTrue();
        result.Bound.Should().NotBeNull();
        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Subject;
        note.Stage.Should().Be(1);
        note.Params!["service"]!.ToString().Should().Contain("hr");
    }

    [Theory]
    [InlineData("skip", null, "skipped", 0)]
    [InlineData(null, "none", "unsupported", 1)]
    [InlineData(null, "throw", "unreachable", 1)]
    public async Task A_continued_part_no_owner_checked_is_a_REMOTE_UNCHECKED_note_and_no_error(string? remote, string? owner, string reason, int calls)
    {
        var client = new FakeRemoteClient();

        if (owner == "throw")
            client.Unreachable.Add("crm");

        var result = await ExplainAsync(Continued, "[]", client, remote: remote);

        result.Valid.Should().BeTrue();
        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Subject;
        note.Stage.Should().Be(1);
        note.Params!["reason"].Should().Be(reason);
        note.Params!["service"].Should().Be("crm");
        client.ExplainCalls.Should().HaveCount(calls);
    }

    [Fact]
    public async Task A_query_without_continued_parts_or_paths_an_owner_binds_asks_no_owner()
    {
        var client = new FakeRemoteClient();

        var result = await ExplainAsync("""[{ "unwind": { "path": "lines", "as": "line" } }, { "resolve": { "path": "contactId", "as": "ct" } }]""", "[]", client);

        result.Valid.Should().BeTrue();
        client.ExplainCalls.Should().BeEmpty("the owner's default select names nothing the caller wrote");
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
    }

    // ---- the owner side: the internal explain overload -----------------------------------------------

    private sealed class Scope : IOxQLScopeProvider
    {
        public ValueTask<Guid?> OrganisationAsync(Microsoft.AspNetCore.Http.HttpContext? httpContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Guid?>(BindHost.Organisation);
    }

    [Fact]
    public async Task Only_the_internal_explain_overload_binds_the_keyedBy_an_origin_forwards_for_its_check()
    {
        var options = BindHost.Options(configure => configure.Compat.Enabled = false);
        var models = new StaticEntityModelProvider(ResolveModel.Model);
        var service = new OxQLQueryService(new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, options), new Scope(), options, models);
        var request = JsonSerializer.Deserialize<ExplainRequest>(
            """{ "query": { "entityType": "rc.customer", "keyedBy": { "path": "code", "keys": ["A"] }, "pipeline": [] }, "remote": "check" }""", OxQLJson.Wire)!;

        var internalCall = (await service.ExplainAsync(request, internalCall: true)).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
        var publicCall = (await service.ExplainAsync(request)).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        internalCall.Valid.Should().BeTrue(string.Join("; ", internalCall.Errors.Select(error => error.Code)));
        publicCall.Valid.Should().BeFalse();
        publicCall.Errors.Select(error => error.Code).Should().Equal(Codes.UnknownRequestMember);
    }

    // ---- the forwarding cache --------------------------------------------------------------------------

    [Fact]
    public async Task An_owners_answer_is_kept_30_seconds_per_organisation_owner_and_body()
    {
        var time = new ManualTime();
        var client = new FakeRemoteClient { Explains = (_, _) => new JsonObject { ["valid"] = true, ["errors"] = new JsonArray() } };
        var engine = Engine(client, cache: new ExplainForwardCache(time));

        await ExplainAsync(Continued, "[]", engine: engine);
        await ExplainAsync(Continued, "[]", engine: engine);
        client.ExplainCalls.Should().HaveCount(1, "the second explain is answered from the cache");

        await ExplainAsync(Continued.Replace("\"title\"", "\"name\""), "[]", engine: engine);
        client.ExplainCalls.Should().HaveCount(2, "another body is another entry");

        time.Now += ExplainForwardCache.Ttl;
        await ExplainAsync(Continued, "[]", engine: engine);
        client.ExplainCalls.Should().HaveCount(3, "an entry lives 30 seconds");
    }

    [Fact]
    public async Task A_failed_owner_call_is_not_kept()
    {
        var client = new FakeRemoteClient();
        client.Unreachable.Add("crm");
        var engine = Engine(client);

        await ExplainAsync(Continued, "[]", engine: engine);
        await ExplainAsync(Continued, "[]", engine: engine);

        client.ExplainCalls.Should().HaveCount(2);
    }

    [Fact]
    public void The_cache_key_holds_the_organisation_the_user_the_service_and_the_bodys_hash()
    {
        var request = new ExplainRequest { Query = BindHost.Request("crm.contact", "[]") };
        var key = ExplainForwardCache.KeyOf(BindHost.Organisation, "crm", request);

        key.Should().StartWith(BindHost.Organisation.ToString("N") + "|").And.Contain("|crm|");
        ExplainForwardCache.KeyOf(BindHost.Organisation, "user-a", "crm", request).Should().NotBe(ExplainForwardCache.KeyOf(BindHost.Organisation, "user-b", "crm", request),
            "an owner may refuse one user what it answers another (RE-11)");
        ExplainForwardCache.KeyOf(Guid.NewGuid(), "crm", request).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, "hr", request).Should().NotBe(key);
        ExplainForwardCache.KeyOf(BindHost.Organisation, "crm", request with { Remote = ExplainRequest.RemoteSkip }).Should().NotBe(key);
    }

    // ---- explain quality (RE-25) ------------------------------------------------------------------

    [Fact]
    public async Task Paths_under_one_remote_alias_go_to_its_owner_in_one_describe_under_an_id_of_its_own()
    {
        var client = new FakeRemoteClient { Explains = (_, request) => OwnerDescribe(request, OwnerChild("name", "string"), OwnerChild("title", "string")) };

        var result = await ExplainAsync(Joins, """
            [{ "id": "p1", "at": 4, "paths": ["ct.name", "ct.title"], "usage": "project" },
             { "id": "p2", "at": 4, "paths": ["ct.name", "ct.title"], "usage": "project" }]
            """, client);

        var describes = client.ExplainCalls.Where(call => call.Request.Remote == ExplainRequest.RemoteSkip).ToList();

        describes.Should().ContainSingle("both paths go in one call, and the second entry is the first's forwarded body, answered from the cache");
        Strings(describes[0].Request.Describe.Single()["paths"]).Should().Equal("name", "title");
        describes[0].Request.Describe.Single()["id"]!.GetValue<string>().Should().Be("forwarded", "the caller's id would make every entry a cache entry of its own");
        Children(Answer(result, "p1")).Select(child => child["path"]!.GetValue<string>()).Should().Equal("ct.name", "ct.title");
        Children(Answer(result, "p2")).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_join_the_compiler_leaves_out_is_placed_nowhere()
    {
        var result = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c" } }, { "project": { "number": 1 } }]""", "[]");

        var step = result.Steps.Single(each => each.Index == 0);
        step.Executor.Should().BeNull("nothing reads 'c' and the row does not show it, so it is not joined");
        step.Phase.Should().BeNull();
        result.Notes!.Should().NotContain(note => note.Code == Notes.JoinBeforePage || note.Code == Notes.JoinAfterPage);
    }

    private sealed class FailingIndexes : IIndexSource
    {
        public Task<IReadOnlyList<MongoDB.Bson.BsonDocument>> IndexesAsync(EntityDef entity, CancellationToken cancellationToken) =>
            throw new MongoDB.Driver.MongoException("listIndexes failed");
    }

    [Fact]
    public async Task Index_lists_that_cannot_be_read_are_a_note_not_a_failed_explain()
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), indexes: new FailingIndexes());
        var request = JsonSerializer.Deserialize<ExplainRequest>($$"""{ "query": { "entityType": "{{Invoice}}", "pipeline": [] }, "include": ["indexes"] }""", OxQLJson.Wire)!;

        var result = (await engine.ExplainAsync(request, BindHost.Context())).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        result.Valid.Should().BeTrue();
        result.Notes!.Should().Contain(note => note.Code == Notes.IndexAdvice && note.Message.Contains("could not be read"));
    }

    [Fact]
    public async Task An_inline_resolve_names_only_the_outcomes_it_can_have()
    {
        var result = await ExplainAsync("""[{ "resolve": { "path": "customerId", "as": "c", "onMissing": "report" } }, { "resolve": { "path": "customerCode", "as": "k", "onMissing": "report" } }]""", "[]");

        var policies = result.Notes!.Where(note => note.Code == Notes.MissingPolicy).ToDictionary(note => note.Stage!.Value);
        ((IEnumerable<string>)policies[0].Params!["dataLoss"]!).Should().Equal("not_found");
        ((IEnumerable<string>)policies[1].Params!["dataLoss"]!).Should().Equal("ambiguous", "not_found");
        policies[0].Message.Should().NotContain("owner");
        policies[1].Message.Should().Contain("RESOLVE_AMBIGUOUS");
    }
}
