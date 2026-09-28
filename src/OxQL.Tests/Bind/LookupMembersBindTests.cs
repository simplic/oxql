using FluentAssertions;
using OxQL.Core.Binding;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The OxQL 2.1 lookup members (DESIGN §3.4.2): <c>sort</c> bound against the child, <c>first</c>
/// as one entity row or null, <c>on</c> naming an entity alias of the row as the parent, and
/// <c>forTarget</c>, which applies only to a stage continued under a remote union alias. A
/// lookup without them binds and renders as it did under 2.0.
/// </summary>
public class LookupMembersBindTests
{
    private const string Order = "probe.order";
    private const string Customer = "probe.customer";

    private const string CustomerResolve = """{ "resolve": { "path": "customerId", "as": "cust" } }""";

    private static async Task<BoundStage.Lookup> LookupAsync(string entity, string pipeline)
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, entity, pipeline);

        return bound.Stages.OfType<BoundStage.Lookup>().Single();
    }

    [Fact]
    public async Task Sort_binds_against_the_child_and_counts_in_the_collation_rules()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "when": "desc" }, { "number": "asc" }] } }]""");
        var lookup = bound.Stages.OfType<BoundStage.Lookup>().Single();

        lookup.ChildSort!.Select(field => (field.Path.Storage, field.Ascending, field.IgnoreCase)).Should().Equal(("When", false, false), ("Number", true, true));
        bound.Collated.Should().BeTrue("a string sort entry of a lookup folds under the request's collation like any other");
        lookup.First.Should().BeFalse();
        lookup.Limit.Should().Be(100);
        lookup.Stage.Should().Be(0);
    }

    [Fact]
    public async Task A_sort_entry_is_checked_against_the_child_not_the_parent()
    {
        var unknown = await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "name": "asc" }] } }]""", Codes.UnknownPath);

        unknown.Path.Should().Be("name", "the customer's name is not a member of the order");

        await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "tags": "asc" }] } }]""", Codes.NotSortable);
        await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "number": "up" }] } }]""", Codes.InvalidSortDirection);
    }

    [Fact]
    public async Task An_exact_lookup_sort_in_a_request_that_folds_elsewhere_is_refused_like_any_exact_sort()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Customer, """
            [{ "match": { "name": { "eq": "x" } } },
             { "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "number": { "direction": "asc", "caseSensitive": true } }] } }]
            """, Codes.OptionNotApplicable);

        error.Stage.Should().Be(1);
    }

    [Fact]
    public async Task First_places_one_entity_row_whose_members_later_stages_address()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Customer, """
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "latest", "first": true, "sort": [{ "when": "desc" }], "select": ["number", "when"] } },
             { "match": { "latest.number": { "eq": "n-1" } } },
             { "sort": [{ "latest.when": "desc" }] }]
            """);
        var lookup = bound.Stages.OfType<BoundStage.Lookup>().Single();

        lookup.First.Should().BeTrue();
        lookup.Limit.Should().Be(1, "first takes one child");
        bound.FinalShape.Roots["latest"].Should().BeOfType<ShapeNode.Entity>();

        await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "latest", "first": true } }, { "unwind": { "path": "latest" } }]""", Codes.NotACollection);

        // A member the lookup did not fetch has no value: sorting or matching on it is refused (PRE-1).
        (await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "latest", "first": true } }, { "sort": [{ "latest.when": "desc" }] }]""", Codes.UnknownPath))
            .Message.Should().Contain("not in the select of 'latest'");
    }

    [Fact]
    public async Task First_with_a_limit_is_refused_and_first_false_is_the_array()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "latest", "first": true, "limit": 3 } }]""", Codes.OptionNotApplicable);

        error.Message.Should().Contain("'limit'");

        var array = await BindHost.BoundAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "first": false, "limit": 3 } }]""");

        array.FinalShape.Roots["orders"].Should().BeOfType<ShapeNode.Array>();
        array.Stages.OfType<BoundStage.Lookup>().Single().Limit.Should().Be(3);
    }

    [Fact]
    public async Task On_joins_the_children_of_a_resolved_entity_alias()
    {
        var lookup = await LookupAsync(Order,
            $$"""[{{CustomerResolve}}, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "siblings", "first": true, "sort": [{ "when": "desc" }] } }]""");

        lookup.On.Should().Be("cust");
        lookup.ParentKeyStorage.Should().Be("cust._id", "the parent's key lies under the alias");
        lookup.ChildKeyStorage.Should().Be("CustomerId");
        lookup.Stage.Should().Be(1);
    }

    [Fact]
    public async Task On_checks_the_declared_reference_against_the_parent_it_names()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Order,
            $$"""[{{CustomerResolve}}, { "lookup": { "from": "probe.order", "path": "customerId", "as": "mine" } }]""", Codes.LookupNotDeclared);

        error.Message.Should().Contain("'probe.order'", "without on the parent is the order, which the order's customerId does not reference");
    }

    [Fact]
    public async Task On_an_unwound_lookup_alias_joins_under_that_alias()
    {
        var lookup = (await BindHost.BoundAsync(BindHost.Probe, Customer, """
            [{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "select": ["customerId"] } },
             { "unwind": { "path": "orders", "as": "order" } },
             { "resolve": { "path": "order.customerId", "as": "buyer" } },
             { "lookup": { "from": "probe.order", "path": "customerId", "on": "buyer", "as": "buyerOrders", "limit": 2 } }]
            """)).Stages.OfType<BoundStage.Lookup>().Last();

        lookup.ParentKeyStorage.Should().Be("buyer._id");
    }

    [Theory]
    [InlineData("""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }, { "lookup": { "from": "probe.order", "path": "customerId", "on": "orders", "as": "again" } }]""", "probe.customer", "the array of a lookup")]
    [InlineData("""[{ "unwind": { "path": "items", "as": "item" } }, { "lookup": { "from": "probe.order", "path": "customerId", "on": "item", "as": "again" } }]""", "probe.order", "an unwound element")]
    [InlineData("""[{ "unwind": { "path": "items", "includeIndex": "position" } }, { "lookup": { "from": "probe.order", "path": "customerId", "on": "position", "as": "again" } }]""", "probe.order", "a scalar")]
    [InlineData("""[{ "group": { "by": [{ "path": "number", "as": "num" }], "fields": { "n": { "count": true } } } }, { "lookup": { "from": "probe.order", "path": "customerId", "on": "n", "as": "again" } }]""", "probe.order", "a group output")]
    public async Task On_anything_but_an_entity_row_is_LOOKUP_ON_NOT_ENTITY(string pipeline, string entity, string what)
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, entity, pipeline, Codes.LookupOnNotEntity);

        error.Message.Should().Contain(what);
        error.Stage.Should().Be(1);
    }

    [Fact]
    public async Task On_a_remote_alias_continues_at_its_owner()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order,
            """[{ "resolve": { "path": "vehicleId", "as": "veh" } }, { "lookup": { "from": "probe.order", "path": "customerId", "on": "veh", "as": "again" } }]""");

        var continued = bound.Stages.OfType<ContinuedStage>().Should().ContainSingle().Subject;
        continued.Anchor.Should().Be("veh");
        continued.Root.Should().Be("veh");
        bound.Stages.OfType<BoundStage.Lookup>().Should().BeEmpty("the owner runs the lookup, this host does not");
    }

    [Fact]
    public async Task On_an_alias_the_row_does_not_carry_is_UNKNOWN_PATH()
    {
        (await BindHost.ErrorAsync(BindHost.Probe, Order,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "on": "nobody", "as": "again" } }]""", Codes.UnknownPath)).Path.Should().Be("nobody");

        (await BindHost.ErrorAsync(BindHost.Probe, Order,
            $$"""[{{CustomerResolve}}, { "project": { "number": 1 } }, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "again" } }]""", Codes.UnknownPath))
            .Message.Should().Contain("removed by the projection");

        (await BindHost.ErrorAsync(BindHost.Probe, Order,
            $$"""[{{CustomerResolve}}, { "project": { "cust.name": 1 } }, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "again" } }]""", Codes.UnknownPath))
            .Message.Should().Contain("'cust.id' was removed by the projection", "the alias is there but its key is not");
    }

    [Fact]
    public async Task ForTarget_is_not_applicable_to_a_lookup_this_host_binds()
    {
        await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "forTarget": "probe.customer" } }]""", Codes.OptionNotApplicable);
        await BindHost.ErrorAsync(BindHost.Probe, Order,
            $$"""[{{CustomerResolve}}, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "forTarget": "probe.customer", "as": "again" } }]""", Codes.OptionNotApplicable);
    }

    [Theory]
    [InlineData("\"sort\": [{ \"number\": \"asc\" }]", "sort")]
    [InlineData("\"first\": true", "first")]
    [InlineData("\"on\": \"cust\"", "on")]
    [InlineData("\"forTarget\": \"probe.customer\"", "forTarget")]
    public async Task The_new_members_are_contract_2_members(string member, string name)
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Customer,
            $$"""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", {{member}} } }]""", Codes.LegacyStageUnsupported, BindHost.Context(contract: 1));

        error.Message.Should().StartWith($"'{name}' is not a member of lookup");
    }

    [Theory]
    [InlineData("\"first\": \"yes\"", "'first' is true or false")]
    [InlineData("\"sort\": { \"number\": \"asc\" }", "'sort' is an array")]
    [InlineData("\"on\": 3", "'on' is a string")]
    [InlineData("\"forTarget\": null", "'forTarget' is a string")]
    public async Task A_member_of_the_wrong_kind_is_refused_rather_than_defaulted(string member, string message)
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Customer,
            $$"""[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", {{member}} } }]""", Codes.UnknownStageMember);

        error.Message.Should().Contain(message);
    }

    [Fact]
    public async Task An_unknown_member_names_the_new_members_in_the_message()
    {
        var error = await BindHost.ErrorAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "single": true } }]""", Codes.UnknownStageMember);

        error.Message.Should().Contain("sort, first, on, forTarget");
    }

    [Fact]
    public async Task The_canonical_form_writes_the_new_members_only_when_they_differ_from_2_0()
    {
        const string Plain = """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders" } }]""";
        const string Defaults = """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "first": false, "sort": [] } }]""";

        var plain = await BindHost.BoundAsync(BindHost.Probe, Customer, Plain);
        var defaults = await BindHost.BoundAsync(BindHost.Probe, Customer, Defaults);

        plain.Canonical.Should().NotContain("\"sort\"").And.NotContain("\"first\"").And.NotContain("\"on\"");
        defaults.Fingerprint.Should().Be(plain.Fingerprint, "first false and an empty sort are what a 2.0 lookup did");

        var sorted = await BindHost.BoundAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "sort": [{ "when": "desc" }] } }]""");
        var first = await BindHost.BoundAsync(BindHost.Probe, Customer,
            """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "orders", "first": true } }]""");
        var on = await BindHost.BoundAsync(BindHost.Probe, Order,
            $$"""[{{CustomerResolve}}, { "lookup": { "from": "probe.order", "path": "customerId", "on": "cust", "as": "siblings" } }]""");

        sorted.Canonical.Should().Contain("""
            "limit":100,"sort":[{"path":"When","direction":"desc","caseSensitive":true}]}
            """.Trim());
        first.Canonical.Should().Contain("\"limit\":1,\"first\":true}");
        on.Canonical.Should().Contain("\"localField\":\"cust._id\"").And.Contain("\"on\":\"cust\"");
        new[] { plain.Fingerprint, sorted.Fingerprint, first.Fingerprint }.Should().OnlyHaveUniqueItems();
        on.Canonical.Should().NotContain("\"stage\"", "the stage index is bound-only");
    }
}
