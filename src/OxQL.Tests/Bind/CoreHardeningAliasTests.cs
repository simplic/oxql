using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// One alias rule for every stage that takes a name from the caller: a plain identifier that is
/// not the storage key, not a name the compiler reserves, and not a member of the row under
/// either of its spellings.
/// </summary>
public class CoreHardeningAliasTests
{
    private static EntityModel Model => BindHost.Probe;
    private const string Order = "probe.order";
    private const string Customer = "probe.customer";

    private static async Task<QueryValidationError> Error(string entity, string pipeline, string code) =>
        await BindHost.ErrorAsync(Model, entity, pipeline, code);

    /// <summary>Every place a caller names an alias, with <c>{0}</c> where the alias goes.</summary>
    public static TheoryData<string, string> AliasSites => new()
    {
        { Customer, """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "{0}" } }]""" },
        { Order, """[{ "resolve": { "path": "customerId", "as": "{0}" } }]""" },
        { Order, """[{ "resolve": { "path": "vehicleId", "as": "{0}" } }]""" },
        { Order, """[{ "unwind": { "path": "items", "as": "{0}" } }]""" },
        { Order, """[{ "unwind": { "path": "items", "includeIndex": "{0}" } }]""" },
        { Order, """[{ "group": { "by": [{ "path": "number", "as": "{0}" }], "fields": { "n": { "count": true } } } }]""" },
        { Order, """[{ "group": { "by": [{ "path": "number", "as": "k" }], "fields": { "{0}": { "count": true } } } }]""" },
    };

    [Theory]
    [MemberData(nameof(AliasSites))]
    public async Task The_storage_key_is_not_an_alias(string entity, string pipeline) =>
        await Error(entity, pipeline.Replace("{0}", "_id"), Codes.InvalidAlias);

    [Theory]
    [MemberData(nameof(AliasSites))]
    public async Task A_name_with_the_compilers_prefix_is_not_an_alias(string entity, string pipeline) =>
        await Error(entity, pipeline.Replace("{0}", "__oxIx0"), Codes.InvalidAlias);

    [Theory]
    [MemberData(nameof(AliasSites))]
    public async Task A_name_with_the_compilers_suffix_is_not_an_alias(string entity, string pipeline) =>
        await Error(entity, pipeline.Replace("{0}", "cust__arr"), Codes.InvalidAlias);

    [Theory]
    [MemberData(nameof(AliasSites))]
    public async Task An_identifier_ends_where_the_text_ends(string entity, string pipeline) =>
        await Error(entity, pipeline.Replace("{0}", @"x\n"), Codes.InvalidAlias);

    [Theory]
    [MemberData(nameof(AliasSites))]
    public async Task A_plain_identifier_is_an_alias_everywhere(string entity, string pipeline) =>
        await BindHost.BoundAsync(Model, entity, pipeline.Replace("{0}", "plain_Alias1"));

    [Theory]
    [InlineData("""[{ "resolve": { "path": "customerId", "as": "{0}" } }]""")]
    [InlineData("""[{ "unwind": { "path": "items", "as": "{0}" } }]""")]
    [InlineData("""[{ "unwind": { "path": "items", "includeIndex": "{0}" } }]""")]
    public async Task An_alias_collides_with_a_member_under_its_storage_name_too(string pipeline)
    {
        // 'number' is stored as 'Number', 'renamed' as 'x'.
        await Error(Order, pipeline.Replace("{0}", "number"), Codes.AliasCollision);
        await Error(Order, pipeline.Replace("{0}", "Number"), Codes.AliasCollision);
        await Error(Order, pipeline.Replace("{0}", "x"), Codes.AliasCollision);
    }

    [Fact]
    public async Task A_lookup_alias_collides_with_a_parent_member_under_its_storage_name() =>
        await Error(Customer, """[{ "lookup": { "from": "probe.order", "path": "customerId", "as": "MatchCode" } }]""", Codes.AliasCollision);

    [Fact]
    public async Task A_group_replaces_the_row_so_its_aliases_may_reuse_member_names() =>
        await BindHost.BoundAsync(Model, Order, """[{ "group": { "by": [{ "path": "number", "as": "number" }], "fields": { "Count": { "count": true } } } }]""");
}
