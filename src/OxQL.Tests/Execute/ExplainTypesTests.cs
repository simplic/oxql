using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The shape side of an explain answer (improvement plan §3.E): the shared <c>types</c> table with each
/// member described once (variants, references, addons, the cut by depth and by the member cap), the
/// union type of an alias with several targets, the flag sets and where a stage's row differs from a
/// type's own flags, the types of another service as its owner answered them, and the <c>catalog</c>.
/// </summary>
public class ExplainTypesTests
{
    private const string Invoice = ResolveModel.Invoice;
    private const string Order = "probe.order";

    /// <summary>An unwound line, a local inline resolve, a local keyed item resolve with its owning row, a plain remote resolve.</summary>
    private const string Joins = """
        [{ "unwind": { "path": "lines", "as": "line" } },
         { "resolve": { "path": "customerId", "as": "c" } },
         { "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "sh" } },
         { "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } }]
        """;

    private static MongoQueryEngine Engine(FakeRemoteClient? client = null, Action<OxQLOptions>? configure = null, EntityModel? model = null) =>
        new(new StaticEntityModelProvider(model ?? ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(configure), client);

    private static async Task<ExplainResult> ExplainAsync(string pipeline, string? envelope = null, FakeRemoteClient? client = null, Action<OxQLOptions>? configure = null,
        string entity = Invoice, EntityModel? model = null, IAddonDefinitionSource? addons = null)
    {
        var body = $$"""{ "query": { "entityType": "{{entity}}", "pipeline": {{pipeline}} }{{(envelope is null ? "" : ", " + envelope)}} }""";
        var outcome = await Engine(client, configure, model).ExplainAsync(ExplainAnswer.Envelope(body), BindHost.Context(BindHost.Options(configure), addons: addons));

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    // ---- the type table --------------------------------------------------------------------------------

    [Fact]
    public async Task A_type_lists_its_members_level_by_level_to_the_depth_asked_and_says_what_lies_below()
    {
        var two = await ExplainAsync("[]");
        var three = await ExplainAsync("[]", """ "shape": { "depth": 3 } """);
        var one = await ExplainAsync("[]", """ "shape": { "depth": 1 } """);
        var invoice = ResolveModel.Model.Entities[Invoice];

        two.Entry!.Shape.Roots.ToJsonString().Should().Be("""{"":"t:rc.invoice"}""");
        two.Type("t:rc.invoice")["entity"]!.GetValue<string>().Should().Be(Invoice);
        two.Paths("t:rc.invoice").Take(invoice.Root.Members.Count).Should().Equal(invoice.Root.Members.Select(member => member.WireName), "the first level first, in the schema's order");
        two.Paths("t:rc.invoice").Should().Contain(["lines.id", "lines.parts", "slot.holderId"]).And.NotContain("lines.parts.customerId", "the default is two levels");
        two.Member("t:rc.invoice", "lines.parts")!.More()["children"]!.GetValue<bool>().Should().BeTrue("what the depth cut is said, and a catalog entry reads it");
        three.Paths("t:rc.invoice").Should().Contain("lines.parts.customerId");
        one.Paths("t:rc.invoice").Should().Equal(invoice.Root.Members.Select(member => member.WireName));
        two.Type("t:rc.invoice").ContainsKey("truncated").Should().BeFalse();
        one.Member("t:rc.invoice", "lines")!.More()["children"]!.GetValue<bool>().Should().BeTrue();
        one.Member("t:rc.invoice", "number")!.More().ContainsKey("children").Should().BeFalse();
    }

    [Fact]
    public async Task Every_members_own_flags_are_what_Shape_Resolve_answers_for_each_usage()
    {
        var result = await ExplainAsync("[]", """ "shape": { "depth": 3 } """);
        var shape = Shape.ForEntity(ResolveModel.Model.Entities[Invoice]);

        result.Paths("t:rc.invoice").Should().Contain(["number", "lines", "lines.parts", "lines.parts.customerId", "slot.holderId"]);

        foreach (var path in result.Paths("t:rc.invoice"))
        {
            var flags = result.OwnFlags("t:rc.invoice", path);
            var match = shape.Resolve(path, PathUsage.Match);
            var sort = shape.Resolve(path, PathUsage.Sort);
            var unwind = shape.Resolve(path, PathUsage.Unwind);
            var group = shape.Resolve(path, PathUsage.GroupKey);
            var operators = flags.Operators();

            (operators.Count > 0).Should().Be(match.Succeeded && (match.Path!.Filterable || operators.All(op => op is "exists" or "is" or "any")), $"{path}: filterable is having an operator");

            if (match.Path?.Filterable == true)
                operators.Should().Contain("exists", $"{path} compares values");

            flags["sortable"]!.GetValue<bool>().Should().Be(sort.Path?.Sortable == true, path);
            flags["projectable"]!.GetValue<bool>().Should().Be(shape.Resolve(path, PathUsage.Project).Succeeded, path);
            flags["unwindable"]!.GetValue<bool>().Should().Be(unwind.Path is { Kind: Kind.Array, CollectionAncestors: 0, Storage: not null }, path);
            flags["groupable"]!.GetValue<bool>().Should().Be(group.Path is { CollectionAncestors: 0 } key && key.Kind != Kind.Array && Kinds.IsScalar(key.Kind), path);
            flags["underCollection"]!.GetValue<int>().Should().Be(shape.Resolve(path, PathUsage.Project).Path!.CollectionAncestors, path);
        }
    }

    [Fact]
    public async Task A_member_row_carries_its_kind_nullability_variants_and_facts_and_its_flags_the_operators_and_folding()
    {
        var result = await ExplainAsync("[]");
        const string type = "t:rc.invoice";

        var number = result.Member(type, "number")!;
        number[ExplainTypes.Row.Kind]!.GetValue<string>().Should().Be("string");
        number[ExplainTypes.Row.Nullable]!.GetValue<int>().Should().Be(0);
        result.OwnFlags(type, "number")["folds"]!.GetValue<bool>().Should().BeTrue();
        result.OwnFlags(type, "number").Operators().Should().Equal("eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex");
        number.ToJsonString().Should().Be($$"""["number","string",0,"{{number[ExplainTypes.Row.Flags]!.GetValue<string>()}}"]""", "a row ends at its last fact");
        MemberFlags.FromId(number[ExplainTypes.Row.Flags]!.GetValue<string>())!.Value.ToJson().ToJsonString().Should().Be(result.OwnFlags(type, "number").ToJsonString(), "the id of a flag set is the flags themselves");

        result.OwnFlags(type, "id")["folds"]!.GetValue<bool>().Should().BeFalse();
        result.OwnFlags(type, "id").Operators().Should().Equal("eq", "neq", "in", "nin", "exists");

        var slot = result.Member(type, "slot")!;
        result.OwnFlags(type, "slot").Operators().Should().Equal(["exists", "is"], "a member whose operators list 'is' is filterable: one source for both (UX-S)");
        slot.More()["variants"].Strings().Should().Equal("RcSlot", "RcDriverSlot", "RcVehicleSlot");
        slot.More().ContainsKey("children").Should().BeFalse("its members are rows of the table; only a member whose members are cut says so");
        slot[ExplainTypes.Row.Nullable]!.GetValue<int>().Should().Be(1);

        result.OwnFlags(type, "lines").Operators().Should().Equal("exists", "any");
        result.OwnFlags(type, "lines")["unwindable"]!.GetValue<bool>().Should().BeTrue();
        result.Member(type, "lines")![ExplainTypes.Row.Kind]!.GetValue<string>().Should().Be("array", "an array is a collection by its kind");
        result.Member(type, "customerIds")!.More()["leafKind"]!.GetValue<string>().Should().Be("guid");
        result.OwnFlags(type, "lines.customerId")["underCollection"]!.GetValue<int>().Should().Be(1);
        result.OwnFlags(type, "lines.customerId")["sortable"]!.GetValue<bool>().Should().BeFalse("under a collection that is not unwound");

        result.OnlyFor(type, "slot.licence").Should().Equal("RcDriverSlot");
        result.OnlyFor(type, "slot.plate").Should().Equal("RcVehicleSlot");
        result.Type(type)["onlyFor"]!.ToJsonString().Should().Be("""[["RcDriverSlot"],["RcVehicleSlot"]]""", "each set of variants is named once and pointed to");
        result.OnlyFor(type, "slot.name").Should().BeNull();
        result.Member(type, "slot.name")!.Fact(ExplainTypes.Row.Description).Should().BeNull("descriptions come with include docs only");
    }

    [Fact]
    public async Task A_reference_lists_every_case_and_its_flags_say_how_a_resolve_may_follow_it()
    {
        var result = await ExplainAsync("[]");
        const string type = "t:rc.invoice";

        var source = result.Member(type, "source.id")![ExplainTypes.Row.Reference]!;
        source.AsObject().ContainsKey("simple").Should().BeFalse();
        source["cases"]![0]!.ToJsonString().Should().Be(
            """{"when":{"path":"type","equals":["logistics"]},"targets":[{"entity":"rc.shipment","field":"id","item":"billingLines"},{"entity":"rc.tour","field":"id","item":"billingLines"}]}""");
        source["cases"]![1]!["targets"]![0]!["remote"]!.GetValue<bool>().Should().BeTrue();
        result.OwnFlags(type, "source.id")["follow"]!.GetValue<string>().Should().Be("one");

        result.Member(type, "customerId")![ExplainTypes.Row.Reference]!["simple"]!.GetValue<bool>().Should().BeTrue();
        result.OwnFlags(type, "customerIds")["follow"]!.GetValue<string>().Should().Be("elements", "the member is the collection: first or all");
        result.OwnFlags(type, "lines.customerId")["follow"]!.GetValue<string>().Should().Be("elements");
        result.Member(type, "shipmentKey")![ExplainTypes.Row.Reference]!["keyAs"]!.GetValue<string>().Should().Be("guid");
        result.Member(type, "number")!.Fact(ExplainTypes.Row.Reference).Should().BeNull();
        result.OwnFlags(type, "number").ContainsKey("follow").Should().BeFalse();
    }

    [Fact]
    public async Task A_type_past_the_member_cap_is_truncated_and_so_is_a_union_over_it()
    {
        var result = await ExplainAsync("""[{ "resolve": { "path": "billing.referenceId", "as": "b" } }]""", configure: options => options.Explain.MaxTypeMembers = 3);

        result.Members("t:rc.invoice").Should().HaveCount(3);
        result.Type("t:rc.invoice")["truncated"]!.GetValue<bool>().Should().BeTrue();

        // The cap falls in the second level: what it cut is said on the members it cut below.
        var second = await ExplainAsync("[]", configure: options => options.Explain.MaxTypeMembers = 16);

        second.Paths("t:rc.invoice").Should().HaveCount(16).And.Contain("lines.id").And.NotContain("source.id");
        second.Member("t:rc.invoice", "source")!.More()["children"]!.GetValue<bool>().Should().BeTrue("the cap cut its members: a catalog entry with the prefix reads them");
        second.Member("t:rc.invoice", "number")!.More().ContainsKey("children").Should().BeFalse();
        result.Type("u:b")["truncated"]!.GetValue<bool>().Should().BeTrue("a union knows no more than its targets say");
    }

    // ---- the row at each stage -------------------------------------------------------------------------

    [Fact]
    public async Task Each_stage_says_the_roots_of_the_row_after_it_with_their_types()
    {
        var result = await ExplainAsync(Joins, client: OwnerFleet.Client());

        result.Entry!.Shape.Roots.Select(pair => pair.Key).Should().Equal("");
        result.ShapeAt(0).Roots.ToJsonString().Should().Be("""{"":"t:rc.invoice","line":"t:rc.invoice#lines"}""");
        result.ShapeAt(3).Roots.ToJsonString().Should().Be(
            """{"":"t:rc.invoice","line":"t:rc.invoice#lines","c":"t:rc.customer","bl":"t:rc.shipment#billingLines","sh":"t:rc.shipment","ct":"t:crm.contact"}""");
        result.ShapeAt(0).Unwound.Should().Equal("lines");
        result.ShapeAt(0).Paging.Should().Be("offset");
        result.Entry.Shape.Paging.Should().Be("cursor");

        result.Type("t:rc.invoice#lines").Should().Match<JsonObject>(type => type["entity"]!.GetValue<string>() == Invoice && type["item"]!.GetValue<string>() == "lines");
        result.Paths("t:rc.invoice#lines").Should().Equal("id", "customerId", "parts", "parts.customerId");
        result.Paths("t:rc.shipment#billingLines").Should().Equal("id", "code", "amount");
        result.Alias("line")["type"]!.GetValue<string>().Should().Be("t:rc.invoice#lines");
        result.Alias("line")["source"]!.GetValue<string>().Should().Be("lines");
    }

    [Fact]
    public async Task A_stage_says_only_where_a_member_differs_from_its_types_own_flags()
    {
        var result = await ExplainAsync(Joins, client: OwnerFleet.Client());

        result.Entry!.Shape.Flags!.Count.Should().Be(0, "at the entry every member has its type's own flags");
        result.OwnFlags("t:rc.invoice", "lines.customerId")["sortable"]!.GetValue<bool>().Should().BeFalse();
        result.FlagsAt(0, "", "lines.customerId")!["sortable"]!.GetValue<bool>().Should().BeTrue("the collection is unwound: one element per row");
        result.FlagsAt(0, "", "lines.customerId")!["underCollection"]!.GetValue<int>().Should().Be(0);
        result.FlagsAt(0, "", "number").Should().BeEquivalentTo(result.OwnFlags("t:rc.invoice", "number"), "a member the unwind does not touch keeps its own");
        result.ShapeAt(1).Flags![""]!.GetValue<string>().Should().Be(result.ShapeAt(0).Flags![""]!.GetValue<string>(), "the same overrides are one set, said once");

        // A keyed alias is joined after the page: its members can be projected, never filtered or sorted.
        foreach (var path in result.Paths("t:rc.shipment#billingLines"))
            result.FlagsAt(3, "bl", path).Should().Match<JsonObject>(flags => flags.Operators().Count == 0 && !flags["sortable"]!.GetValue<bool>() && flags["projectable"]!.GetValue<bool>());

        result.Overrides(3, "bl")!.Select(pair => pair.Key).Should().Equal(["", "*"], "what every member shares is said once");
        result.OwnFlags("t:rc.shipment#billingLines", "code").Operators().Should().Contain("eq", "the type's own flags are the owner entity's");

        // An inline join's members keep their own flags; an element alias's are the element's.
        result.ShapeAt(1).Flags!["c"].Should().NotBeNull("the alias itself has flags");
        result.FlagsAt(1, "c", "name").Should().BeEquivalentTo(result.OwnFlags("t:rc.customer", "name"));
        result.FlagsAt(0, "line", "customerId")!["sortable"]!.GetValue<bool>().Should().BeTrue();
        result.FlagsAt(0, "line", "parts.customerId")!["underCollection"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task A_projection_takes_members_out_of_the_row_and_a_joins_hint_takes_none()
    {
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "project": { "number": 1, "lines.id": 1, "c.name": 1 } },
             { "sort": [{ "number": "asc" }] }]
            """);

        result.FlagsAt(0, "c", "code").Should().NotBeNull("every member of the target can be read under the alias: the join loads what is read, whatever its hint names");
        result.FlagsAt(0, "c", "name").Should().NotBeNull();
        result.FlagsAt(1, "c", "code").Should().BeNull("the projection kept only the name under the alias");
        result.FlagsAt(1, "c", "name").Should().NotBeNull();
        result.FlagsAt(1, "", "customerId").Should().BeNull("the projection removed it");
        result.FlagsAt(1, "", "number").Should().BeEquivalentTo(result.OwnFlags("t:rc.invoice", "number"));
        result.FlagsAt(1, "", "lines").Should().NotBeNull("the parent of a kept path stays in the row");
        result.FlagsAt(1, "", "lines.id").Should().NotBeNull();
        result.FlagsAt(1, "", "lines.customerId").Should().BeNull();
        result.Overrides(1, "")!.TryGetPropertyValue("*", out var every).Should().BeTrue("most members are gone: said once");
        every.Should().BeNull();
        result.ShapeAt(1).Projection.Should().Equal("c.name", "id", "lines.id", "number");
        result.ShapeAt(2).Flags![""]!.GetValue<string>().Should().Be(result.ShapeAt(1).Flags![""]!.GetValue<string>());
        result.ShapeAt(0).Projection.Should().BeNull();
    }

    [Fact]
    public async Task After_a_group_the_roots_are_the_outputs_each_a_kind()
    {
        var result = await ExplainAsync("""[{ "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "count": { "count": true } } } }]""");

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.ShapeAt(0).Roots.ToJsonString().Should().Be("""{"n":"k:string","count":"k:long"}""");
        result.ShapeAt(0).Grouped.Should().BeTrue();
        result.FlagsAt(0, "n", "")!.Operators().Should().Contain("eq");
        result.Alias("n")["node"]!.GetValue<string>().Should().Be("group");
    }

    // ---- unions ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_alias_with_several_targets_points_to_one_union_type_built_from_the_targets_rows()
    {
        var result = await ExplainAsync("""[{ "resolve": { "path": "billing.referenceId", "as": "b" } }]""");

        result.RootType(0, "b").Should().Be("u:b");
        result.Alias("b")["type"]!.GetValue<string>().Should().Be("u:b");

        var union = result.Type("u:b");
        union["of"].Strings().Should().Equal("t:rc.shipment", "t:rc.tour");

        var members = union["members"]!.AsArray().ToDictionary(row => row![0]!.GetValue<string>(), row => row!.ToJsonString());
        members["id"].Should().Be("""["id"]""", "every target has it, with one kind");
        members["number"].Should().Be("""["number",[0]]""", "only the shipment has a number");
        members["name"].Should().Be("""["name",[1]]""", "only the tour has a name");
        members["driverId"].Should().Be("""["driverId"]""");
        result.OwnFlags("u:b", "number").ToJsonString().Should().Be(result.OwnFlags("t:rc.shipment", "number").ToJsonString(), "a union's member has the facts of the first target that has it");
        result.FlagsAt(0, "b", "number")!.Operators().Should().BeEmpty("joined after the page");
        result.Alias("b")["targets"]!.AsArray().Select(target => target!["type"]!.GetValue<string>()).Should().Equal("t:rc.shipment", "t:rc.tour");
    }

    [Fact]
    public void A_member_the_targets_of_a_union_disagree_on_is_unknown()
    {
        var types = new ExplainTypes(ResolveModel.Model, BindHost.Context(), null, 2);

        types.Types["t:a.one"] = new JsonObject { ["members"] = new JsonArray(new JsonArray("id", "guid", 0, "0"), new JsonArray("code", "string", 0, "0")) };
        types.Types["t:a.two"] = new JsonObject { ["members"] = new JsonArray(new JsonArray("code", "int", 0, "0"), new JsonArray("id", "guid", 0, "0"), new JsonArray("extra", "bool", 0, "0")) };

        types.Union("x", ["t:a.one", "t:a.two"]).Should().Be("u:x");
        types.Types["u:x"]!["members"]!.ToJsonString().Should().Be("""[["id"],["code",[0,1],"unknown"],["extra",[1]]]""");
    }

    // ---- addons ----------------------------------------------------------------------------------------

    private sealed class Definitions : IAddonDefinitionSource
    {
        public static readonly Guid Price = Guid.Parse("11111111-2222-3333-4444-555555555555");

        public int Reads { get; private set; }

        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken)
        {
            Reads++;

            return ValueTask.FromResult<IReadOnlyList<AddonDefinition>>(entity == Order
                ? [
                    new AddonDefinition { Id = Price, Entity = Order, Path = "Preis", Kind = AddonKind.Decimal, DisplayName = "Price", Description = "The agreed price." },
                    new AddonDefinition { Id = Guid.NewGuid(), Entity = Order, Path = "Status", Kind = AddonKind.String, Values = [new AddonValue("open", "Open"), new AddonValue("done", null)] },
                ]
                : []);
        }
    }

    [Fact]
    public async Task The_addon_members_of_the_asking_organisation_are_members_of_the_entitys_type()
    {
        var result = await ExplainAsync("[]", entity: Order, model: BindHost.Probe, addons: new Definitions());
        var bag = result.Paths("t:probe.order").Single(path => result.Paths("t:probe.order").Contains(path + ".Preis"));
        var price = result.Member("t:probe.order", bag + ".Preis")!;

        price[ExplainTypes.Row.Kind]!.GetValue<string>().Should().Be("decimal");
        price[ExplainTypes.Row.Nullable]!.GetValue<int>().Should().Be(1);
        price[ExplainTypes.Row.Addon]!["id"]!.GetValue<string>().Should().Be(Definitions.Price.ToString("D"));
        price[ExplainTypes.Row.Addon]!["kind"]!.GetValue<string>().Should().Be("decimal");
        price.Fact(ExplainTypes.Row.Description).Should().BeNull("descriptions come with include docs only");
        price.More()["displayName"]!.GetValue<string>().Should().Be("Price");

        var documented = await ExplainAsync("[]", """ "include": ["shape", "notes", "types", "docs"] """, entity: Order, model: BindHost.Probe, addons: new Definitions());

        documented.Member("t:probe.order", bag + ".Preis")![ExplainTypes.Row.Description]!.GetValue<string>().Should().Be("The agreed price.");
        result.OwnFlags("t:probe.order", bag + ".Preis").Operators().Should().Contain("gt");

        var status = result.Member("t:probe.order", bag + ".Status")!;
        status.More()["enum"]!.AsArray().Select(value => value!["name"]!.GetValue<string>()).Should().Equal("open", "done");

        result.Revision.Addons.Should().NotBeNull("the etag covers the organisation's addons");
        (await ExplainAsync("[]", entity: Order, model: BindHost.Probe)).Revision.Addons.Should().BeNull("no definition was read");
        (await ExplainAsync("[]", entity: Order, model: BindHost.Probe)).Etag.Should().NotBe(result.Etag);
    }

    // ---- the types of another service ------------------------------------------------------------------

    [Fact]
    public async Task A_remote_alias_points_to_its_owners_type_as_the_owner_answered_the_check_and_the_flags_are_the_origins()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync(Joins, client: client);

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Alias("ct")["type"]!.GetValue<string>().Should().Be("t:crm.contact");
        result.Alias("ct")["complete"]!.GetValue<bool>().Should().BeTrue();
        result.Paths("t:crm.contact").Should().Equal("id", "organizationId", "name", "email", "companyId", "phones", "phones.number", "phones.label");
        result.Type("t:crm.contact")["entity"]!.GetValue<string>().Should().Be("crm.contact");
        result.Member("t:crm.contact", "companyId")![ExplainTypes.Row.Reference]!["cases"]![0]!["targets"]![0]!["entity"]!.GetValue<string>().Should().Be("crm.company");

        // The type's own flags are the owner's; under the alias the origin's shape decides.
        result.OwnFlags("t:crm.contact", "name")["sortable"]!.GetValue<bool>().Should().BeTrue("at its owner a name sorts");
        result.OwnFlags("t:crm.contact", "phones").Operators().Should().Equal("exists", "any");

        var name = result.FlagsAt(3, "ct", "name")!;
        name.Operators().Should().Contain(["eq", "contains"], "a member of a plain remote resolve filters as a semi-join");
        name["sortable"]!.GetValue<bool>().Should().BeFalse("the owner's rows cannot order this host's page");
        name["groupable"]!.GetValue<bool>().Should().BeFalse();
        name["projectable"]!.GetValue<bool>().Should().BeTrue();
        result.FlagsAt(3, "ct", "phones")!.Operators().Should().Equal(["exists"], "a semi-join compares values, never 'any'");
        result.FlagsAt(3, "ct", "phones.number")!["underCollection"]!.GetValue<int>().Should().Be(1, "the collections below the alias are the owner's");
        result.FlagsAt(3, "ct", "companyId")!["follow"]!.GetValue<string>().Should().Be("one");

        client.ExplainCalls.Should().ContainSingle("one check of the owner query the run sends answers the type").Which.Service.Should().Be("crm");
        client.ExplainCalls[0].Request.Query.EntityType.Should().Be("crm.contact");
        result.OwnerOf("crm").Should().Match<JsonObject>(owner => owner["answered"]!.GetValue<bool>() && owner["calls"]!.GetValue<int>() == 1 && !owner["cached"]!.GetValue<bool>());
        result.Cache.DependsOn.Should().Equal("crm");
        result.Cache.Complete.Should().BeTrue();
        result.Revision.Schema.Should().ContainKey("crm");
    }

    [Fact]
    public async Task A_remote_resolve_the_caller_wrote_nothing_under_is_checked_too_since_its_type_is_its_owners_to_say()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "ct" } }]""", client: client);

        client.ExplainCalls.Should().ContainSingle();
        result.Alias("ct")["type"]!.GetValue<string>().Should().Be("t:crm.contact");

        // Without the shape nothing is asked that the caller did not write.
        var bare = OwnerFleet.Client();
        await ExplainAsync("""[{ "resolve": { "path": "contactId", "as": "ct" } }]""", """ "include": ["notes"] """, client: bare);

        bare.ExplainCalls.Should().BeEmpty("the owner's default select names nothing the caller wrote");
    }

    [Fact]
    public async Task A_remote_union_alias_is_the_union_of_this_hosts_types_and_its_owners_and_its_owning_row_of_the_owning_entities()
    {
        var result = await ExplainAsync("""[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }]""", client: OwnerFleet.Client());

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Alias("line")["type"]!.GetValue<string>().Should().Be("u:line");
        result.Type("u:line")["of"].Strings().Should().Equal("t:rc.shipment#billingLines", "t:rc.tour#billingLines", "t:transport.shipment#billingLines");
        result.Paths("t:transport.shipment#billingLines").Should().Equal("id", "code", "weight");
        result.Type("u:line")["members"]!.AsArray().Select(row => row!.ToJsonString()).Should().Equal(
            """["id"]""", """["code"]""", """["amount",[0,1]]""", """["weight",[2]]""");
        result.Alias("owner")["type"]!.GetValue<string>().Should().Be("u:owner");
        result.Type("u:owner")["of"].Strings().Should().Equal("t:rc.shipment", "t:rc.tour", "t:transport.shipment");
        result.Target("line", "transport.shipment#billingLines")["type"]!.GetValue<string>().Should().Be("t:transport.shipment#billingLines");
        result.Alias("line")["complete"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_union_target_whose_owner_does_not_answer_leaves_the_alias_incomplete_with_what_is_known()
    {
        var client = OwnerFleet.Client();
        client.Unreachable.Add("transport");

        var result = await ExplainAsync("""[{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } }]""", client: client);

        result.Alias("line")["complete"]!.GetValue<bool>().Should().BeFalse();
        result.Type("u:line")["of"].Strings().Should().Equal("t:rc.shipment#billingLines", "t:rc.tour#billingLines");
        result.Target("line", "transport.shipment#billingLines")["type"].Should().BeNull();
        result.Types.ContainsKey("t:transport.shipment#billingLines").Should().BeFalse("nothing stands in for the owner's answer");
        result.Cache.Complete.Should().BeFalse();
        result.OwnerOf("transport")["reason"]!.GetValue<string>().Should().Be("unreachable");
    }

    [Fact]
    public async Task A_continued_alias_points_to_the_type_its_owner_bound_it_to()
    {
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }]
            """, client: OwnerFleet.Client());

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Alias("co")["entities"].Strings().Should().Equal("crm.company");
        result.Alias("co")["type"]!.GetValue<string>().Should().Be("t:crm.company");
        result.Alias("co")["complete"]!.GetValue<bool>().Should().BeTrue();
        result.RootType(1, "co").Should().Be("t:crm.company");
        result.Paths("t:crm.company").Should().Equal("id", "organizationId", "title");
        result.FlagsAt(1, "co", "title")!.Operators().Should().BeEmpty("a continued alias is the owner's row under this row: projected, never filtered here");
        result.FlagsAt(1, "co", "title")!["projectable"]!.GetValue<bool>().Should().BeTrue();
    }

    /// <summary>The billing reference's local union (a shipment or a tour), and each one's driver continued under it.</summary>
    private static string DriverUnder(string? forTarget) => $$"""
        [{ "resolve": { "path": "billing.referenceId", "as": "b" } },
         { "resolve": { "path": "b.driverId", "as": "d"{{(forTarget is null ? "" : $", \"forTarget\": \"{forTarget}\"")}} } }]
        """;

    [Fact]
    public async Task A_resolve_continued_under_a_union_alias_holds_the_target_of_the_reference_on_its_forTarget_not_the_unions_first()
    {
        var tour = await ExplainAsync(DriverUnder("rc.tour"), client: OwnerFleet.Client());

        tour.Valid.Should().BeTrue(string.Join("; ", tour.Errors.Select(error => error.Message)));
        tour.Alias("d")["entities"].Strings().Should().Equal(["rc.customer"], "the tour's driverId references a customer; the shipment, the union's first target, is not followed");
        tour.Alias("d")["type"]!.GetValue<string>().Should().Be("t:rc.customer");
        tour.Alias("d")["continuedFrom"]!.ToJsonString().Should().Be("""{"alias":"b","target":"rc.tour"}""");

        var shipment = await ExplainAsync(DriverUnder("rc.shipment"), client: OwnerFleet.Client());

        shipment.Alias("d")["entities"].Strings().Should().Equal("crm.contact");
        shipment.Alias("d")["type"]!.GetValue<string>().Should().Be("t:crm.contact", "this host's own check answers what its owner said in turn");
        shipment.Owners.Select(owner => owner["service"]!.GetValue<string>()).Should().Equal("rc", "crm");
    }

    [Fact]
    public async Task A_resolve_continued_under_a_union_alias_without_forTarget_holds_the_union_of_what_every_target_reaches()
    {
        var result = await ExplainAsync(DriverUnder(null), client: OwnerFleet.Client());

        result.Alias("d")["entities"].Strings().Should().Equal(["crm.contact", "rc.customer"], "each target of the union follows its own driverId");
        result.Alias("d")["type"]!.GetValue<string>().Should().Be("u:d");
        result.Type("u:d")["of"].Strings().Should().Equal("t:crm.contact", "t:rc.customer");
        result.Type("u:d")["members"]!.AsArray().Select(row => row![0]!.GetValue<string>()).Should().Contain(["name", "email", "code"]);
    }

    [Fact]
    public async Task A_continued_alias_no_owner_answered_for_has_no_entity_and_no_type_rather_than_the_anchors()
    {
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "r", "select": ["name"] } },
             { "resolve": { "path": "r.companyId", "as": "co", "select": ["title"] } }]
            """, client: new FakeRemoteClient());

        result.Alias("co")["node"]!.GetValue<string>().Should().Be("remote");
        result.Alias("co")["entities"]!.AsArray().Should().BeEmpty("the reference lies in crm's model, which did not answer; 'crm.contact' is the anchor's target, not co's");
        result.Alias("co")["type"].Should().BeNull();
        result.Alias("co")["complete"]!.GetValue<bool>().Should().BeFalse();
        result.RootType(1, "co").Should().BeNull();
    }

    [Fact]
    public async Task A_request_that_does_not_bind_still_has_its_remote_aliases_typed_by_their_owners_own_description()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync("""
            [{ "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } },
             { "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } },
             { "match": { "nothing": { "eq": 1 } } }]
            """, client: client);

        result.Valid.Should().BeFalse();
        result.Alias("ct")["type"]!.GetValue<string>().Should().Be("t:crm.contact", "there is no run plan to check, so the owner describes its entity");
        result.Paths("t:crm.contact").Should().Contain("email");
        result.Alias("line")["type"]!.GetValue<string>().Should().Be("u:line");
        result.Type("u:line")["of"].Strings().Should().Equal("t:rc.shipment#billingLines", "t:rc.tour#billingLines", "t:transport.shipment#billingLines");
        result.Alias("owner")["type"]!.GetValue<string>().Should().Be("u:owner");
        client.ExplainCalls.Should().OnlyContain(call => call.Request.Catalog.Count == 1 && call.Request.Query.Pipeline.Count == 0);
    }

    // ---- the catalog -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_catalog_entry_points_to_the_type_of_an_entity_outside_the_query_and_lists_what_references_it()
    {
        var result = await ExplainAsync("[]", """
            "catalog": [{ "id": "item", "entity": "rc.shipment#billingLines" },
                        { "id": "referenced", "entity": "rc.customer", "referencing": true },
                        { "id": "plain", "entity": "rc.customer" }]
            """);

        result.Catalog.Select(answer => answer["id"]!.GetValue<string>()).Should().Equal("item", "referenced", "plain");
        result.Catalog[0]["type"]!.GetValue<string>().Should().Be("t:rc.shipment#billingLines");
        result.Catalog[0]["forwarded"]!.GetValue<bool>().Should().BeFalse();
        result.Paths("t:rc.shipment#billingLines").Should().Equal("id", "code", "amount");

        var referencedBy = result.Catalog[1]["referencedBy"]!.AsObject();
        referencedBy["id"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>()).Should().Contain(["customerId", "customerIds", "lines.customerId", "localSource.id", "slot.holderId"]);
        referencedBy["code"]!.ToJsonString().Should().Be("""[{"entity":"rc.invoice","path":"customerCode","service":"rc","remote":false,"lookup":true}]""");
        result.Catalog[1]["remoteLookup"]!.GetValue<bool>().Should().BeFalse("this host has no remote query client");
        result.Catalog[2].AsObject().ContainsKey("referencedBy").Should().BeFalse("only referencing: true asks for them");
        result.Catalog[2]["type"]!.GetValue<string>().Should().Be("t:rc.customer");
    }

    [Fact]
    public async Task A_catalog_entry_with_a_prefix_reads_the_members_the_table_cut()
    {
        var result = await ExplainAsync("[]", """ "catalog": [{ "id": "deep", "entity": "rc.invoice", "prefix": "lines.parts" }, { "id": "one", "entity": "rc.invoice", "prefix": "lines", "depth": 1 }] """);

        result.Catalog[0]["members"]!.AsArray().Select(row => row![0]!.GetValue<string>()).Should().Equal("lines.parts.customerId");
        result.Catalog[0]["truncated"]!.GetValue<bool>().Should().BeFalse();
        result.Catalog[1]["members"]!.AsArray().Select(row => row![0]!.GetValue<string>()).Should().Equal("lines.id", "lines.customerId", "lines.parts");
        result.FlagSets!.ContainsKey(result.Catalog[0]["members"]![0]![ExplainTypes.Row.Flags]!.GetValue<string>()).Should().BeTrue("the rows point to the answer's flag sets");

        var variants = await ExplainAsync("[]", """ "catalog": [{ "id": "slot", "entity": "rc.invoice", "prefix": "slot" }] """);

        variants.Catalog[0]["members"]!.AsArray().Single(row => row![0]!.GetValue<string>() == "slot.licence")![ExplainTypes.Row.OnlyFor]!.ToJsonString()
            .Should().Be("""["RcDriverSlot"]""", "the rows of a lookup stand alone: they name their variants themselves");
    }

    [Theory]
    [InlineData("""{ "id": "d", "entity": "rc.customer", "colour": 1 }""", Codes.UnknownStageMember)]
    [InlineData("""{ "entity": "rc.customer" }""", Codes.UnknownStageMember)]
    [InlineData("""{ "id": "d" }""", Codes.UnknownEntity)]
    [InlineData("""{ "id": "d", "entity": "rc.customer", "depth": 4 }""", Codes.InvalidOperand)]
    [InlineData("""{ "id": "d", "entity": "rc.customer", "prefix": "nope" }""", Codes.UnknownPath)]
    [InlineData("""{ "id": "d", "entity": "rc.shipment#number" }""", Codes.UnknownPath)]
    [InlineData("""{ "id": "d", "entity": "nowhere.thing" }""", Codes.UnknownEntity)]
    public async Task A_malformed_catalog_entry_is_answered_with_the_code_of_what_is_wrong_and_the_others_still_are(string entry, string code)
    {
        var result = await ExplainAsync("""[{ "match": { "number": { "eq": "a" } } }]""", $$""" "catalog": [{{entry}}, { "id": "ok", "entity": "rc.customer" }] """, client: new FakeRemoteClient { Configured = ["crm"] });

        result.Valid.Should().BeTrue("a catalog entry is no part of the query");
        result.Catalog[0]["error"]!["code"]!.GetValue<string>().Should().Be(code);
        result.Catalog[0]["type"].Should().BeNull();
        result.Catalog[1].AsObject().ContainsKey("error").Should().BeFalse();
    }

    [Fact]
    public async Task A_catalog_entry_of_another_services_entity_is_answered_by_its_owner_and_marked_forwarded()
    {
        var client = OwnerFleet.Client();
        var result = await ExplainAsync("[]", """ "catalog": [{ "id": "contact", "entity": "crm.contact" }, { "id": "phones", "entity": "crm.contact", "prefix": "phones" }] """, client: client);

        result.Catalog[0]["forwarded"]!.GetValue<bool>().Should().BeTrue();
        result.Catalog[0]["id"]!.GetValue<string>().Should().Be("contact");
        result.Catalog[0]["type"]!.GetValue<string>().Should().Be("t:crm.contact");
        result.Paths("t:crm.contact").Should().Contain("email");
        result.OwnFlags("t:crm.contact", "name")["sortable"]!.GetValue<bool>().Should().BeTrue("an entity's own flags are its owner's answer as it stands");
        result.Catalog[1]["members"]!.AsArray().Select(row => row![0]!.GetValue<string>()).Should().Equal("phones.number", "phones.label");
        result.FlagSets!.ContainsKey(result.Catalog[1]["members"]![0]![ExplainTypes.Row.Flags]!.GetValue<string>()).Should().BeTrue();

        client.ExplainCalls.Should().HaveCount(2);
        client.ExplainCalls.Should().OnlyContain(call => call.Service == "crm" && call.Budget <= TimeSpan.FromMilliseconds(1_500) && call.Request.Catalog.Single()["id"]!.GetValue<string>() == "forwarded");
        client.ExplainCalls[0].Request.Include.Should().Equal(["types"], "the owner answers the lookup with the member rows this answer was asked for, not the shape of an empty query");
    }

    [Fact]
    public async Task An_owner_that_does_not_answer_a_catalog_entry_leaves_an_error_on_it_and_the_answer_incomplete()
    {
        var client = new FakeRemoteClient();
        client.Unreachable.Add("crm");

        var unsupported = await ExplainAsync("[]", """ "catalog": [{ "id": "e", "entity": "crm.contact" }] """, client: new FakeRemoteClient { Configured = ["crm"], Explains = null });
        var unreachable = await ExplainAsync("[]", """ "catalog": [{ "id": "e", "entity": "crm.contact" }] """, client: client);

        unsupported.Catalog[0]["error"]!["code"]!.GetValue<string>().Should().Be(Codes.ResolveUnreachable);
        unsupported.Catalog[0]["error"]!["params"]!["reason"]!.GetValue<string>().Should().Be("unsupported");
        unreachable.Catalog[0]["error"]!["params"]!["reason"]!.GetValue<string>().Should().Be("unreachable");
        unreachable.Cache.Complete.Should().BeFalse();
        unreachable.Valid.Should().BeTrue();
    }

    // ---- the envelope, the etag and the size -----------------------------------------------------------

    [Fact]
    public async Task Without_the_shape_the_answer_has_its_stages_and_aliases_but_no_types()
    {
        var result = await ExplainAsync(Joins, """ "include": ["notes"] """, client: OwnerFleet.Client());

        result.Stages.Should().OnlyContain(stage => stage.Shape == null);
        result.Entry.Should().BeNull();
        result.Types.Count.Should().Be(0);
        result.FlagSets.Should().BeNull();
        result.Alias("c")["node"]!.GetValue<string>().Should().Be("entity");
        result.Alias("c").ContainsKey("type").Should().BeFalse();
        result.Notes.Should().NotBeEmpty();
        JsonSerializer.SerializeToNode(result, OxQLJson.Wire)!.AsObject().ContainsKey("entry").Should().BeFalse();
    }

    [Fact]
    public async Task Without_the_notes_only_what_the_answer_lacks_is_noted()
    {
        var result = await ExplainAsync(Joins, """ "include": ["shape"] """, client: new FakeRemoteClient());

        result.Notes.Select(note => note.Code).Should().Equal([Notes.RemoteUnchecked], "that an owner did not answer is about the answer itself");
        (await ExplainAsync(Joins, client: new FakeRemoteClient())).Notes.Should().Contain(note => note.Code == Notes.JoinAfterPage);
    }

    [Fact]
    public async Task The_etag_is_one_for_one_request_and_changes_with_it()
    {
        var first = await ExplainAsync(Joins, client: OwnerFleet.Client());
        var again = await ExplainAsync(Joins, client: OwnerFleet.Client());
        var other = await ExplainAsync(Joins, """ "shape": { "depth": 3 } """, client: OwnerFleet.Client());
        var incomplete = await ExplainAsync(Joins, client: new FakeRemoteClient());

        first.Etag.Should().StartWith("W/\"x3:").And.Be(again.Etag);
        other.Etag.Should().NotBe(first.Etag);
        incomplete.Etag.Should().NotBe(first.Etag, "an incomplete answer is never taken for the complete one");
        first.Cache.MaxAge.Should().Be(30);
    }

    [Fact]
    public async Task An_answer_over_MaxAnswerBytes_keeps_the_first_level_of_its_types_then_drops_the_plan_and_says_so()
    {
        var whole = await ExplainAsync(Joins, """ "include": ["shape", "notes", "types", "plan"] """, client: OwnerFleet.Client());
        var size = JsonSerializer.SerializeToUtf8Bytes(whole, OxQLJson.Wire).Length;

        // The etag is written after the size is taken, so the cap lies a little below the whole answer.
        var trimmed = await ExplainAsync(Joins, """ "include": ["shape", "notes", "types", "plan"] """, client: OwnerFleet.Client(), configure: options => options.Explain.MaxAnswerBytes = size - 200);

        trimmed.Paths("t:rc.invoice").Should().OnlyContain(path => !path.Contains('.'));
        trimmed.Type("t:rc.invoice")["truncated"]!.GetValue<bool>().Should().BeTrue();
        trimmed.Cache.Complete.Should().BeFalse();
        trimmed.Plan.Should().NotBeNull("the types alone brought it under the cap");

        var note = trimmed.Notes.Single(note => note.Code == Notes.ExplainTrimmed);
        ((IEnumerable<string>)note.Params!["dropped"]!).Should().Equal("types");
        note.Params["bytes"].Should().BeOfType<int>().Which.Should().BeInRange(size - 200, size);
        note.Params["max"].Should().Be(size - 200);

        var smallest = await ExplainAsync(Joins, """ "include": ["shape", "notes", "types", "plan"] """, client: OwnerFleet.Client(), configure: options => options.Explain.MaxAnswerBytes = 1_024);

        smallest.Plan.Should().BeNull();
        ((IEnumerable<string>)smallest.Notes.Single(note => note.Code == Notes.ExplainTrimmed).Params!["dropped"]!).Should().Equal("types", "plan");
        whole.Notes.Should().NotContain(note => note.Code == Notes.ExplainTrimmed);
        whole.Cache.Complete.Should().BeTrue();
    }
}
