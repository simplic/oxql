using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Mongo;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// A <c>resolve</c> after a <c>group</c>: a key over a member that declares a reference keeps
/// that reference in the group's output, so the stage binds from the key's alias with the executor it
/// would have before the group. An aggregate, and a key over a member without a reference, carry
/// none; a reference that picks its target by another member of the row cannot be followed from a row
/// that holds only the key.
/// </summary>
public class GroupResolveBindTests
{
    private const string Invoice = ResolveModel.Invoice;
    private static readonly CompileOptions Options = new(10_000, null, 100_000);

    private static string Grouped(string path, string alias = "key") =>
        $$"""{ "group": { "by": [{ "path": "{{path}}", "as": "{{alias}}" }], "fields": { "n": { "count": true }, "first": { "first": "{{path}}" } } } }""";

    private static Task<BoundPipeline> Bound(string pipeline) => BindHost.BoundAsync(ResolveModel.Model, Invoice, pipeline);

    [Fact]
    public async Task A_key_over_a_local_reference_joins_in_the_aggregate_after_the_group_on_the_keys_alias()
    {
        var bound = await Bound($$"""[{{Grouped("customerId")}}, { "resolve": { "path": "key", "as": "c" } }, { "project": { "key": 1, "n": 1, "c.name": 1 } }, { "sort": [{ "n": "desc" }] }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.Executor.Should().Be(ResolveExecutor.Inline);
        resolve.IsRemote.Should().BeFalse();
        resolve.Reference.Storage.Should().Be("key", "the group's row stores the key under its alias");
        resolve.Reference.Root.Should().BeOfType<ShapeNode.GroupOutput>().Which.KeyOf!.Wire.Should().Be("customerId");
        resolve.TargetEntity.Should().Be("rc.customer");
        bound.FinalShape.Grouped.Should().BeTrue();
        bound.FinalShape.Roots["c"].Should().BeOfType<ShapeNode.Entity>().Which.Def.Id.Should().Be("rc.customer");
        bound.PagingMode.Should().Be(PagingMode.Keyset, "the groups still page by value");

        // The join stands after the page: only the display reads it. Its local field is the key's alias.
        var stages = MongoCompiler.Compile(bound, Options).PageStages;
        var names = stages.Select(stage => stage.GetElement(0).Name).ToList();
        var lookup = stages.Single(stage => stage.Contains("$lookup"))["$lookup"].AsBsonDocument;

        names.IndexOf("$lookup").Should().BeGreaterThan(names.IndexOf("$limit"), string.Join(",", names));
        names.IndexOf("$group").Should().BeLessThan(names.IndexOf("$limit"));
        lookup["from"].AsString.Should().Be("customers");
        lookup["localField"].AsString.Should().Be("key");
        lookup.ToJson().Should().Contain("OrganizationId", "the target is scoped like every entry into an entity");
    }

    [Fact]
    public async Task What_reads_the_record_before_the_page_takes_the_join_before_it()
    {
        var sorted = await Bound($$"""[{{Grouped("customerId")}}, { "resolve": { "path": "key", "as": "c" } }, { "sort": [{ "c.name": "asc" }] }]""");
        var names = MongoCompiler.Compile(sorted, Options).PageStages.Select(stage => stage.GetElement(0).Name).ToList();

        names.IndexOf("$lookup").Should().BeGreaterThan(names.IndexOf("$group")).And.BeLessThan(names.IndexOf("$sort"));
        sorted.Sort!.Fields.Select(field => field.Path.Wire).Should().Equal(["c.name", "key"], "the group's key completes the order");

        var having = await Bound($$"""[{{Grouped("customerId")}}, { "resolve": { "path": "key", "as": "c" } }, { "match": { "c.name": { "startsWith": "A" } } }, { "page": { "limit": 5, "count": true } }]""");
        var compiled = MongoCompiler.Compile(having, Options);

        compiled.PageStages.Select(stage => stage.GetElement(0).Name).Should().ContainInOrder("$group", "$lookup", "$match", "$limit");
        compiled.CountStages!.Select(stage => stage.GetElement(0).Name).Should().Contain("$lookup", "the count reads the record the condition reads");
    }

    [Fact]
    public async Task A_key_over_another_services_reference_is_fetched_after_the_page_and_a_condition_on_its_record_is_a_semi_join_on_the_key()
    {
        var bound = await Bound($$"""[{{Grouped("contactId")}}, { "resolve": { "path": "key", "as": "r" } }, { "project": { "key": 1, "n": 1, "r.name": 1 } }]""");
        var resolve = bound.Stages.OfType<BoundStage.Resolve>().Single();

        resolve.IsRemote.Should().BeTrue();
        resolve.Executor.Should().Be(ResolveExecutor.Keyed);
        resolve.TargetEntity.Should().Be("crm.contact");
        bound.FinalShape.Roots["r"].Should().BeOfType<ShapeNode.Remote>();

        var compiled = MongoCompiler.Compile(bound, Options);

        compiled.KeyedResolves.Should().ContainSingle().Which.Reference.Storage.Should().Be("key");
        compiled.PageStages.Should().NotContain(stage => stage.Contains("$lookup"));

        var filtered = await Bound($$"""[{{Grouped("contactId")}}, { "resolve": { "path": "key", "as": "r" } }, { "match": { "r.name": { "eq": "Ada" } } }]""");
        var semi = MongoCompiler.Compile(filtered, Options);

        filtered.HasSemiJoin.Should().BeTrue();
        semi.SemiJoins.Should().ContainSingle();
        semi.PageStages.Select(stage => stage.ToJson()).Should().Contain(stage => stage.Contains("\"key\" : { \"$in\""), "the owner's ids narrow the groups by their key");
    }

    [Fact]
    public async Task A_resolve_only_the_display_reads_does_not_bind_the_levels_cursor()
    {
        var plain = await Bound($$"""[{{Grouped("customerId")}}, { "sort": [{ "n": "desc" }] }]""");
        var shown = await Bound($$"""[{{Grouped("customerId")}}, { "resolve": { "path": "key", "as": "c" } }, { "project": { "key": 1, "n": 1, "c.name": 1 } }, { "sort": [{ "n": "desc" }] }]""");
        var remote = await Bound($$"""[{{Grouped("contactId")}}, { "resolve": { "path": "key", "as": "r" } }, { "sort": [{ "n": "desc" }] }]""");
        var remotePlain = await Bound($$"""[{{Grouped("contactId")}}, { "sort": [{ "n": "desc" }] }]""");
        var ordered = await Bound($$"""[{{Grouped("customerId")}}, { "resolve": { "path": "key", "as": "c" } }, { "sort": [{ "c.name": "asc" }] }]""");

        shown.Fingerprint.Should().Be(plain.Fingerprint, "showing the record's name is a column, not another list");
        remote.Fingerprint.Should().Be(remotePlain.Fingerprint);
        ordered.Fingerprint.Should().NotBe(plain.Fingerprint, "ordered by the record it is another list");
    }

    [Theory]
    [InlineData("customerCode", ResolveExecutor.Inline, "a member of the target that is not its key")]
    [InlineData("billingLineId", ResolveExecutor.Keyed, "an item of the target")]
    [InlineData("shipmentKey", ResolveExecutor.Keyed, "a key converted on the way")]
    public async Task Every_unconditional_reference_binds_with_the_executor_it_has_before_a_group(string member, ResolveExecutor executor, string what)
    {
        var after = await Bound($$"""[{{Grouped(member)}}, { "resolve": { "path": "key", "as": "t" } }]""");
        var before = await Bound($$"""[{ "resolve": { "path": "{{member}}", "as": "t" } }]""");

        after.Stages.OfType<BoundStage.Resolve>().Single().Executor.Should().Be(executor, what);
        before.Stages.OfType<BoundStage.Resolve>().Single().Executor.Should().Be(executor, what);
        after.Stages.OfType<BoundStage.Resolve>().Single().TargetEntity.Should().Be(before.Stages.OfType<BoundStage.Resolve>().Single().TargetEntity);
    }

    [Fact]
    public async Task A_key_over_a_key_keeps_the_reference_and_an_aggregate_or_a_plain_key_has_none()
    {
        var twice = await Bound($$"""
            [{{Grouped("customerId")}},
             { "group": { "by": [{ "path": "key", "as": "again" }], "fields": { "groups": { "count": true } } } },
             { "resolve": { "path": "again", "as": "c" } }]
            """);

        twice.Stages.OfType<BoundStage.Resolve>().Single().TargetEntity.Should().Be("rc.customer");

        var aggregate = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, $$"""[{{Grouped("customerId")}}, { "resolve": { "path": "first", "as": "c" } }]""", Codes.ResolveNotDeclared);

        aggregate.Message.Should().StartWith("'first' declares no reference; it is a guid.");
        aggregate.Stage.Should().Be(1);

        var plain = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, $$"""[{{Grouped("number")}}, { "resolve": { "path": "key", "as": "c" } }]""", Codes.ResolveNotDeclared);

        plain.Message.Should().StartWith("'key' declares no reference; it is a string.");
    }

    [Theory]
    [InlineData("source.id")]
    [InlineData("localSource.id")]
    [InlineData("billing.referenceId")]
    public async Task A_reference_that_picks_its_target_by_another_member_of_the_row_cannot_be_followed_from_the_key(string member)
    {
        // Before the group it binds: the row holds the member that picks.
        await Bound($$"""[{ "resolve": { "path": "{{member}}", "as": "t" } }]""");

        var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, $$"""[{{Grouped(member)}}, { "resolve": { "path": "key", "as": "t" } }]""", Codes.ResolveNotDeclared);

        error.Stage.Should().Be(1);
        error.Path.Should().Be("key");
        error.Params!["reason"].Should().Be("conditionalAfterGroup");
        error.Message.Should().Contain("before the group");
        Basis.OfCode[error.Code].Should().Be(Basis.Model);
    }

    [Fact]
    public async Task Contract_1_keeps_its_group_rows_without_a_reference()
    {
        var error = await BindHost.ErrorAsync(ResolveModel.Model, Invoice, $$"""[{{Grouped("customerId")}}, { "resolve": { "path": "key", "as": "c" } }]""", Codes.ResolveNotDeclared, BindHost.Context(contract: 1));

        error.Message.Should().StartWith("'key' declares no reference; it is a guid.");
    }

    [Fact]
    public async Task Explain_says_the_key_can_be_followed_and_that_the_row_holds_the_record_once_it_is()
    {
        var grouped = await Bound($"[{Grouped("customerId")}]");

        ExplainTypes.FlagsAt(grouped.FinalShape, "key").ToJson()["follow"]!.GetValue<string>().Should().Be("one");
        ExplainTypes.FlagsAt(grouped.FinalShape, "first").ToJson().ContainsKey("follow").Should().BeFalse("an aggregate carries no reference");
        ExplainTypes.FlagsAt(grouped.FinalShape, "n").ToJson().ContainsKey("follow").Should().BeFalse();
        ExplainFacts.Subjects(grouped).Should().BeEmpty("a group's row is no record");

        var joined = await Bound($$"""[{{Grouped("customerId")}}, { "resolve": { "path": "key", "as": "c" } }, { "resolve": { "path": "key", "as": "again" } }]""");

        ExplainFacts.Subjects(joined).ToJsonString().Should().Be(
            """[{"root":"c","entity":"rc.customer","key":"c.id","nullable":true,"many":false},{"root":"again","entity":"rc.customer","key":"again.id","nullable":true,"many":false}]""");

        var remote = await Bound($$"""[{{Grouped("contactId")}}, { "resolve": { "path": "key", "as": "r" } }]""");

        ExplainFacts.Subjects(remote).ToJsonString().Should().Be("""[{"root":"r","entity":"crm.contact","key":"r.id","nullable":true,"many":false,"remote":true}]""");
    }
}
