using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.CaseInsensitive;

/// <summary>
/// Late joins: a join nothing after it reads (display only) runs after the page, and must answer
/// exactly what the same join pinned before the page answers. The pinned twin reads the alias in
/// an always-true match, so its join cannot move. Ported from the legacy
/// <c>final/case-insensitive</c> battery (LJ-01 … LJ-06) onto <c>conformance.entity</c>, whose
/// children and references are the legacy conformance rows under lab names. The expected
/// joins are read off the corpus rows of the other side, never off the engine.
/// </summary>
[Trait("Category", "Integration")]
public class LateJoinTests
{
    private static Task<LabClient> Conformance() => Lab.ClientAsync(LabService.Conformance);

    /// <summary>An always-true match that reads the alias, so the join before it stays before the page.</summary>
    private static string ReadsAlias(string alias) => $$"""{ "match": { "or": [ { "{{alias}}": { "exists": true } }, { "{{alias}}": { "exists": false } } ] } }""";

    private static IReadOnlyList<CorpusRow> Entities => Corpus.Sorted(Corpus.Conformance, [("id", false)]);

    /// <summary>The names of the organisation A children whose parent is <paramref name="parent"/>, sorted.</summary>
    private static IReadOnlyList<string> ChildrenOf(Guid parent) =>
        Corpus.Rows(Corpus.ConformanceChild).Where(child => Corpus.GuidAt(child, "parentId") == parent).Select(child => Corpus.Text(child, "name")!).Order(StringComparer.Ordinal).ToList();

    /// <summary>The name of the organisation A child a row's <c>childId</c> names, or null.</summary>
    private static string? ChildNameOf(CorpusRow entity) =>
        Corpus.Rows(Corpus.ConformanceChild).Where(child => child.Id == Corpus.GuidAt(entity, "childId")).Select(child => Corpus.Text(child, "name")).SingleOrDefault();

    private static IReadOnlyList<string?> Resolved(WireAnswer answer, string alias, string member) =>
        answer.Items.Select(row => row![alias] is JsonObject target ? target[member]?.GetValue<string>() : null).ToList();

    private static async Task<(WireAnswer Moved, WireAnswer Stayed)> Twins(LabClient client, string join, string after)
    {
        var moved = await client.SendAsync(Corpus.Conformance, $$"""[ {{join}}, {{after}} ]""");
        var stayed = await client.SendAsync(Corpus.Conformance, $$"""[ {{join}}, {{ReadsAlias(AliasOf(join))}}, {{after}} ]""");

        moved.ShouldBeOk("moved");
        stayed.ShouldBeOk("stayed");
        moved.Items.ToJsonString().Should().Be(stayed.Items.ToJsonString(), "a moved join answers the same bytes as its pinned twin");

        return (moved, stayed);
    }

    private static string AliasOf(string join) => JsonNode.Parse(join)!.AsObject().Single().Value!["as"]!.GetValue<string>();

    [Fact]
    public async Task LJ01_a_display_only_lookup_answers_the_same_bytes_as_its_pinned_twin_and_as_the_corpus_says()
    {
        var expected = Corpus.PageOf(Corpus.SortedIds(Corpus.Conformance, "name"), limit: 2);
        var total = Corpus.Counts(Corpus.Conformance).A;
        total.Should().BeGreaterThan(2);

        var (moved, stayed) = await Twins(await Conformance(),
            """{ "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids", "select": ["name"] } }""",
            """{ "sort": [ { "name": "asc" } ] }, { "page": { "limit": 2, "includeTotalCount": true } }""");

        moved.ShouldHaveIds(expected).ShouldHaveTotal(total);
        stayed.TotalCount.Should().Be(moved.TotalCount);
        stayed.HasNextPage.Should().Be(moved.HasNextPage);

        foreach (var parent in moved.Items)
        {
            var id = Guid.Parse(parent!["id"]!.GetValue<string>());
            var kids = (parent["kids"] as JsonArray ?? []).Select(kid => kid!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal);
            kids.Should().Equal(ChildrenOf(id), $"the children of {parent["name"]}");
        }
    }

    [Fact]
    public async Task LJ02_a_display_only_local_resolve_answers_the_same_bytes_as_its_pinned_twin()
    {
        var expected = Entities.Select(ChildNameOf).ToList();
        expected.Should().Contain(name => name == null).And.Contain(name => name != null);

        var (moved, _) = await Twins(await Conformance(),
            """{ "resolve": { "path": "childId", "as": "c" } }""",
            """{ "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5, "includeTotalCount": true } }""");

        moved.ShouldHaveIds(Corpus.IdsOf(Entities)).ShouldHaveTotal(Entities.Count);
        Resolved(moved, "c", "name").Should().Equal(expected);
    }

    [Theory]
    [InlineData("LJ03", """{ "project": { "id": 1, "c": 1, "childId": 1 } }""")]
    [InlineData("LJ03", """{ "project": { "id": 1, "c": 1 } }""")]
    [InlineData("LJ03", """{ "project": { "childId": 0 } }""")]
    public async Task LJ03_a_display_only_resolve_behind_a_projection_that_keeps_the_alias_but_drops_the_reference_member_still_resolves(string id, string project)
    {
        var expected = Entities.Select(ChildNameOf).ToList();

        var answer = await (await Conformance()).SendAsync(Corpus.Conformance, $$"""
            [ { "resolve": { "path": "childId", "as": "c" } }, {{project}}, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]
            """);

        answer.ShouldHaveIds(Corpus.IdsOf(Entities), id);
        Resolved(answer, "c", "name").Should().Equal(expected, project);
    }

    [Fact]
    public async Task LJ04_a_lookup_a_later_match_filters_still_filters_the_page_and_the_count_under_the_collation()
    {
        var parents = Corpus.Rows(Corpus.ConformanceChild)
            .Where(child => Order.EqualsCi(Corpus.Text(child, "name")!, "CHILD ONE"))
            .Select(child => Corpus.GuidAt(child, "parentId")!.Value)
            .Distinct()
            .Order(Comparer<Guid>.Create((a, b) => Order.CompareUtf8(a.ToString("D"), b.ToString("D"))))
            .ToList();
        parents.Should().ContainSingle();

        var answer = await (await Conformance()).SendAsync(Corpus.Conformance, """
            [ { "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids", "select": ["name"] } },
              { "match": { "kids.name": { "eq": "CHILD ONE" } } },
              { "project": { "id": 1 } },
              { "sort": [ { "id": "asc" } ] },
              { "page": { "limit": 10, "includeTotalCount": true } } ]
            """);

        answer.ShouldHaveIds(parents).ShouldHaveTotal(parents.Count);
    }

    [Fact]
    public async Task LJ05_a_local_resolve_filter_folds_like_any_condition()
    {
        var targets = Corpus.Rows(Corpus.ConformanceRef).Where(target => Order.EqualsCi(Corpus.Text(target, "name")!, "ALPHA REFERENCE")).Select(target => target.WireId).ToList();
        targets.Should().Equal("REF-A");
        var expected = Entities.Select(row => Corpus.Text(row, "refCode") is { } code && targets.Contains(code) ? code : null).ToList();
        expected.Should().Contain("REF-A").And.Contain((string?)null);

        var answer = await (await Conformance()).SendAsync(Corpus.Conformance, """
            [ { "resolve": { "path": "refCode", "as": "r", "filter": { "name": { "eq": "ALPHA REFERENCE" } } } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } } ]
            """);

        answer.ShouldHaveIds(Corpus.IdsOf(Entities));
        Resolved(answer, "r", "code").Should().Equal(expected);
    }

    [Theory]
    [InlineData("LJ06", "inclusion without the key", """{ "resolve": { "path": "childId", "as": "c" } }""", """{ "project": { "id": 1, "c": 1 } }""", "childId", "c")]
    [InlineData("LJ06", "exclusion of the key", """{ "resolve": { "path": "childId", "as": "c" } }""", """{ "project": { "childId": 0 } }""", "childId", "c")]
    [InlineData("LJ06", "string key (code)", """{ "resolve": { "path": "refCode", "as": "r" } }""", """{ "project": { "id": 1, "name": 1, "r": 1 } }""", "refCode", "r")]
    public async Task LJ06_a_projection_that_drops_the_join_key_answers_the_same_bytes_as_its_pinned_twin_and_the_key_stays_out_of_the_row(
        string id, string form, string resolve, string project, string key, string alias)
    {
        var (moved, _) = await Twins(await Conformance(), resolve, $$"""{{project}}, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 5 } }""");

        moved.ShouldHaveIds(Corpus.IdsOf(Entities), $"{id} {form}");
        moved.Items.Should().Contain(row => row![alias] is JsonObject, $"{form}: at least one row resolves");

        foreach (var row in moved.Items)
            row!.AsObject().ContainsKey(key).Should().BeFalse($"{form}: the dropped key is not on the wire row {row.ToJsonString()}");
    }
}
