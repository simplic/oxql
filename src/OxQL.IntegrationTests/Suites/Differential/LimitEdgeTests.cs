using FluentAssertions;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// Every limit of a host at its edge, on a host whose limits are small: a request at the limit is
/// answered, one above it is refused with the limit's code and <c>params { limit, max, count }</c>,
/// where <c>limit</c> is the name health publishes the value under and <c>max</c> is that value.
/// </summary>
[Trait("Category", "Integration")]
public class LimitEdgeTests
{
    private const string Entity = EdgeCorpus.Entity;

    private static readonly IReadOnlyDictionary<string, string?> Small = new Dictionary<string, string?>
    {
        ["OxQL:Limits:MaxPipelineStages"] = "6", ["OxQL:Limits:MaxConditions"] = "5", ["OxQL:Limits:MaxProjectionFields"] = "4", ["OxQL:Limits:MaxPageSize"] = "7",
        ["OxQL:Limits:DefaultPageSize"] = "7", ["OxQL:Limits:MaxVariables"] = "3", ["OxQL:Limits:MaxFilterEntries"] = "2", ["OxQL:Limits:MaxGroupFields"] = "3",
        ["OxQL:Limits:MaxLookupStages"] = "2", ["OxQL:Limits:MaxResolveStages"] = "2", ["OxQL:Limits:MaxUnwindStages"] = "1", ["OxQL:Limits:MaxLookupLimit"] = "4",
        ["OxQL:Limits:RegexMaxLength"] = "6", ["OxQL:Limits:MaxValuesLimit"] = "5", ["OxQL:Values:ScanRows"] = "9", ["OxQL:Limits:MaxBatchQueries"] = "2", ["OxQL:Limits:MaxReportPageSize"] = "9",
    };

    private static async Task<LabClient> ClientAsync() => (await EdgeCorpus.FleetAsync()).Variant(LabService.Conformance, "limits-small", Small);

    private static string Repeat(int count, Func<int, string> each) => string.Join(", ", Enumerable.Range(0, count).Select(each));

    private static readonly string[] Columns = ["name", "note", "count", "ratio", "amount"];

    /// <summary>The request body with <c>n</c> of what a limit counts, by the limit's name on health.</summary>
    private static string Body(string limit, int n) => limit switch
    {
        "maxPipelineStages" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{{Repeat(n, _ => """{ "sort": [{ "id": "asc" }] }""")}}] }""",
        "maxConditions" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "and": [{{Repeat(n, i => $$"""{ "count": { "neq": {{i + 10}} } }""")}}] } }] }""",
        "maxProjectionFields" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "project": { {{Repeat(n, i => $"\"{Columns[i]}\": 1")}} } }] }""",
        "maxPageSize" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "page": { "limit": {{n}} } }] }""",
        "maxReportPageSize" => $$"""{ "entityType": "{{Entity}}", "strict": true, "pipeline": [{ "match": { "count": 99 } }, { "page": { "limit": {{n}} } }] }""",
        "maxVariables" => $$"""{ "entityType": "{{Entity}}", "variables": { {{Repeat(n, i => $"\"v{i}\": {i}")}} }, "pipeline": [] }""",
        "maxFilterEntries" => $$"""{ "entityType": "{{Entity}}", "filter": [{{Repeat(n, i => $$"""{ "where": { "count": { "neq": {{i + 10}} } } }""")}}], "pipeline": [] }""",
        "maxGroupFields" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "group": { "by": [{ "path": "count", "as": "k" }], "fields": { {{Repeat(n - 1, i => $$""" "f{{i}}": { "count": true } """)}} } } }] }""",
        "maxLookupStages" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{{Repeat(n, i => $$"""{ "lookup": { "from": "conformance.child", "path": "parentId", "as": "k{{i}}", "limit": 1 } }""")}}] }""",
        "maxResolveStages" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{{Repeat(n, i => $$"""{ "resolve": { "path": "refCode", "as": "r{{i}}" } }""")}}] }""",
        "maxUnwindStages" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{{Repeat(n, i => i == 0 ? """{ "unwind": { "path": "items", "as": "item" } }""" : """{ "unwind": { "path": "item.parts", "as": "part" } }""")}}] }""",
        "maxLookupLimit" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "lookup": { "from": "conformance.child", "path": "parentId", "as": "kids", "limit": {{n}} } }] }""",
        "regexMaxLength" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "match": { "name": { "regex": "^{{new string('a', n - 1)}}" } } }] }""",
        "maxValuesLimit" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "values": { "path": "note", "limit": {{n}} } }] }""",
        "valuesScanRows" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "values": { "path": "note", "scan": {{n}} } }] }""",
        "search" => $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "values": { "path": "note", "search": "{{new string('n', n)}}" } }] }""",
        _ => throw new ArgumentException(limit),
    };

    [Theory]
    [InlineData("MAX_PIPELINE_STAGES_EXCEEDED", "maxPipelineStages", 6)]
    [InlineData("MAX_CONDITIONS_EXCEEDED", "maxConditions", 5)]
    [InlineData("MAX_PROJECTION_FIELDS_EXCEEDED", "maxProjectionFields", 4)]
    [InlineData("PAGE_SIZE_EXCEEDED", "maxPageSize", 7)]
    [InlineData("PAGE_SIZE_EXCEEDED", "maxReportPageSize", 9)]
    [InlineData("MAX_VARIABLES_EXCEEDED", "maxVariables", 3)]
    [InlineData("MAX_FILTER_ENTRIES_EXCEEDED", "maxFilterEntries", 2)]
    [InlineData("MAX_GROUP_FIELDS_EXCEEDED", "maxGroupFields", 3)]
    [InlineData("MAX_LOOKUP_STAGES_EXCEEDED", "maxLookupStages", 2)]
    [InlineData("MAX_RESOLVE_STAGES_EXCEEDED", "maxResolveStages", 2)]
    [InlineData("MAX_UNWIND_STAGES_EXCEEDED", "maxUnwindStages", 1)]
    [InlineData("LOOKUP_LIMIT_EXCEEDED", "maxLookupLimit", 4)]
    [InlineData("REGEX_TOO_LONG", "regexMaxLength", 6)]
    [InlineData("VALUES_LIMIT_EXCEEDED", "maxValuesLimit", 5)]
    [InlineData("VALUES_LIMIT_EXCEEDED", "valuesScanRows", 9)]
    [InlineData("VALUES_LIMIT_EXCEEDED", "search", 6)]
    public async Task A_request_at_a_limit_is_answered_and_one_above_it_is_refused_with_the_limits_name_and_value(string code, string what, int max)
    {
        var client = await ClientAsync();
        var limit = what == "search" ? "regexMaxLength" : what;
        var health = await client.HealthAsync(shallow: true);

        health.Body!["limits"]![limit]!.GetValue<int>().Should().Be(max, "health publishes the value in force under the name a refusal carries");

        var at = await client.PostAsync("OxQL/query", Body(what, max));

        at.ShouldBeOk($"{limit} = {max}");

        var over = await client.PostAsync("OxQL/query", Body(what, max + 1));
        var error = over.ShouldRefuse(code, 400);

        error["params"]!["limit"]!.GetValue<string>().Should().Be(limit);
        error["params"]!["max"]!.GetValue<int>().Should().Be(max);
        (error["params"]!["count"] ?? error["params"]!["value"])!.GetValue<int>().Should().Be(max + 1);

        // Explain says the same of both.
        (await client.ExplainHereAsync(Body(what, max))).Body!["valid"]!.GetValue<bool>().Should().BeTrue();

        var explained = await client.ExplainHereAsync(Body(what, max + 1));

        explained.Body!["valid"]!.GetValue<bool>().Should().BeFalse(explained.Text);
        explained.Body["errors"]!.AsArray().Select(each => each!["code"]!.GetValue<string>()).Should().Contain(code);
    }

    [Fact]
    public async Task A_batch_at_its_limit_is_answered_and_one_above_it_is_refused_whole()
    {
        var client = await ClientAsync();
        var one = $$"""{ "entityType": "{{Entity}}", "pipeline": [{ "page": { "limit": 1 } }] }""";

        (await client.PostAsync("OxQL/batch", $$"""{ "queries": [{{one}}, {{one}}] }""")).Results.Should().HaveCount(2).And.OnlyContain(entry => entry.IsPage);

        var over = await client.PostAsync("OxQL/batch", $$"""{ "queries": [{{one}}, {{one}}, {{one}}] }""");

        over.ShouldRefuse("BATCH_TOO_LARGE", 400)["params"]!.ToJsonString().Should().Be("""{"limit":"maxBatchQueries","max":2,"count":3}""");
    }

    /// <summary>Every limit health publishes is the value in force: the ones this host set as it set them.</summary>
    [Fact]
    public async Task Health_publishes_the_limits_in_force()
    {
        var limits = (await (await ClientAsync()).HealthAsync(shallow: true)).Body!["limits"]!.AsObject();

        foreach (var (key, value) in Small)
        {
            var name = key.Split(':')[^1];
            var published = key.StartsWith("OxQL:Values:", StringComparison.Ordinal) ? "values" + name : char.ToLowerInvariant(name[0]) + name[1..];

            limits[published]!.GetValue<int>().Should().Be(int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture), published);
        }

        limits.Count.Should().BeGreaterThanOrEqualTo(48);
    }
}
