using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// One query asked in several ways that have to agree: in one page that holds every row, walked by
/// cursor in pages of any size, with a count beside every page, and as the count alone. A case
/// writes the stages before the page; what is compared is the rows as the wire writes them.
/// </summary>
internal static class Walks
{
    /// <summary>A request: the entity, the stages before the page (a JSON array), and the request's <c>filter</c> and variables when it has them.</summary>
    internal sealed record Query(string Entity, string Stages, string? Filter = null, string? Variables = null)
    {
        /// <summary>
        /// Whether the rows are compared with case and accents folded: the key of a group of text is
        /// written in the spelling of the first row met, which is any of the group's.
        /// </summary>
        public bool Folded { get; init; }

        public List<string> Rows(WireAnswer answer) => Folded ? [.. Walks.Rows(answer).Select(EdgeOracle.Fold)] : Walks.Rows(answer);

        public JsonObject Body(JsonObject? page)
        {
            var body = new JsonObject { ["entityType"] = Entity };

            if (Variables is not null)
                body["variables"] = JsonNode.Parse(Variables);

            if (Filter is not null)
                body["filter"] = JsonNode.Parse(Filter);

            var pipeline = (JsonArray)JsonNode.Parse(Stages)!;

            if (page is not null)
                pipeline.Add(new JsonObject { ["page"] = page });

            body["pipeline"] = pipeline;

            return body;
        }
    }

    /// <summary>The rows of one page of <paramref name="limit"/> rows, which has to hold them all.</summary>
    public static async Task<List<string>> WholeAsync(LabClient client, Query query, int limit = 500)
    {
        var answer = await client.QueryAsync(query.Body(new JsonObject { ["limit"] = limit }));

        answer.ShouldBeOk("the one page");
        answer.HasNextPage.Should().BeFalse($"the one page holds every row: {answer}");

        return query.Rows(answer);
    }

    public static List<string> Rows(WireAnswer answer) => answer.Items.Select(item => item!.ToJsonString()).ToList();

    /// <summary>Every page of a walk in pages of <paramref name="limit"/>, with a count beside each when asked.</summary>
    public static async Task<List<WireAnswer>> PagesAsync(LabClient client, Query query, int limit, bool count = false, int maxPages = 2_000)
    {
        var pages = new List<WireAnswer>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            var page = new JsonObject { ["limit"] = limit };

            if (cursor is not null)
                page["cursor"] = cursor;

            if (count)
                page["count"] = true;

            var answer = await client.QueryAsync(query.Body(page));

            answer.ShouldBeOk($"page {pages.Count + 1} in pages of {limit}");
            pages.Add(answer);
            cursor = answer.HasNextPage ? answer.NextCursor ?? throw new InvalidOperationException($"hasNextPage without a cursor: {answer}") : null;

            if (cursor is not null && !cursors.Add(cursor))
                throw new InvalidOperationException($"The walk in pages of {limit} was handed the cursor of an earlier page again after {pages.Count} pages.");

            if (pages.Count > maxPages)
                throw new InvalidOperationException($"The walk in pages of {limit} did not end within {maxPages} pages.");
        }
        while (cursor is not null);

        return pages;
    }

    /// <summary>
    /// What a walk has to hold against the one page: the same rows in the same order, every page
    /// full until the rows end, and, with a count beside each page, the number of rows on every page
    /// (the count step reads every row of these corpora) and at the end.
    /// Returns what does not hold; nothing when it all does.
    /// </summary>
    public static async Task<List<string>> DifferencesAsync(LabClient client, Query query, IReadOnlyList<string> whole, IEnumerable<int> limits, bool count = true)
    {
        var differences = new List<string>();

        foreach (var limit in limits.Distinct())
        {
            List<WireAnswer> pages;

            try
            {
                pages = await PagesAsync(client, query, limit, count);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Xunit.Sdk.XunitException)
            {
                differences.Add($"pages of {limit}: {Short(exception.Message)}");
                continue;
            }

            var rows = pages.SelectMany(query.Rows).ToList();

            if (!rows.SequenceEqual(whole))
                differences.Add($"pages of {limit}: {Describe(whole, rows)}");

            var expectedPages = Math.Max(1, (whole.Count + limit - 1) / limit);

            if (pages.Count != expectedPages)
                differences.Add($"pages of {limit}: {pages.Count} pages for {whole.Count} rows, expected {expectedPages}");

            if (!count)
                continue;

            for (var index = 0; index < pages.Count; index++)
            {
                var said = pages[index].Count?.ToJsonString() ?? "(none)";
                var state = pages[index].CountState;
                var value = pages[index].Count?["value"]?.GetValue<long>();

                // A count step that ran out of its time says so and claims nothing; anything it does claim has to be the number.
                if (state == "unknown" && pages[index].Count?["reason"]?.GetValue<string>() == "timeout")
                    continue;

                if (state != "exact" || value != whole.Count)
                {
                    differences.Add($"pages of {limit}: page {index + 1} of {pages.Count} counts {said}, the rows are {whole.Count}");
                    break;
                }
            }
        }

        return differences;
    }

    /// <summary>The count alone: <c>limit: 0</c> with a budget that lets it finish.</summary>
    public static async Task<WireAnswer> CountAsync(LabClient client, Query query, int budgetMs = 5_000) =>
        await client.QueryAsync(query.Body(new JsonObject { ["limit"] = 0, ["count"] = new JsonObject { ["budgetMs"] = budgetMs } }));

    /// <summary>Where two row lists first differ, and how they differ as sets.</summary>
    public static string Describe(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var at = 0;

        while (at < expected.Count && at < actual.Count && expected[at] == actual[at])
            at++;

        var missing = expected.Except(actual).Count();
        var extra = actual.Except(expected).Count();
        var repeated = actual.Count - actual.Distinct().Count();

        return $"{actual.Count} rows for {expected.Count}; first difference at row {at}: expected {Short(at < expected.Count ? expected[at] : "(end)")}, got {Short(at < actual.Count ? actual[at] : "(end)")}; {missing} missing, {extra} not expected, {repeated} repeated";
    }

    public static string Short(string text, int length = 400) => text.Length > length ? text[..length] + " …" : text;
}
