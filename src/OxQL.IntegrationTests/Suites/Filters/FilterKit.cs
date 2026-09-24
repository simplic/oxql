using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// What the filter suites share, and nothing else: the clients of the shared fleet, the raw answer
/// to a hand-written condition (for the cases whose expected outcome is a refusal), the exact list
/// of refusal codes and messages, and the corpus reads the legacy batteries spelled as helpers.
/// Every expectation is still computed from <see cref="Corpus"/> in the case itself.
/// </summary>
internal static class FilterKit
{
    public static Task<LabClient> StaffClient() => Lab.ClientAsync(LabService.Staff);

    public static Task<LabClient> FleetClient() => Lab.ClientAsync(LabService.Fleet);

    public static Task<LabClient> TransportClient() => Lab.ClientAsync(LabService.Transport);

    public static Task<LabClient> LedgerClient() => Lab.ClientAsync(LabService.Ledger);

    public static Task<LabClient> ConformanceClient() => Lab.ClientAsync(LabService.Conformance);

    /// <summary>The raw answer to one condition: <c>match</c>, <c>project id</c>, <c>page 5</c>, as the legacy <c>matchRaw</c> sent it.</summary>
    public static Task<WireAnswer> MatchRawAsync(this LabClient client, string entity, object condition, int limit = 5) =>
        client.SendAsync(entity, $$"""[{ "match": {{Json.Text(condition)}} }, { "project": { "id": 1 } }, { "page": { "limit": {{limit}} } }]""");

    /// <summary>The ids a condition matches, in id order (the shared <see cref="LabClient.MatchIdsAsync"/>).</summary>
    public static Task<IReadOnlyList<Guid>> Ids(this LabClient client, string entity, object condition) => client.MatchIdsAsync(entity, condition);

    /// <summary>The messages of a refusal's coded errors, in order.</summary>
    public static IReadOnlyList<string> Messages(this WireAnswer answer) =>
        answer.Errors.Select(error => error["message"]?.GetValue<string>() ?? "").ToList();

    /// <summary>Asserts a refusal whose coded errors are exactly <paramref name="codes"/>, and returns the first error's message.</summary>
    public static string ShouldRefuseExactly(this WireAnswer answer, params string[] codes)
    {
        answer.ShouldRefuse(codes[0]);
        answer.ErrorCodes.Should().Equal(codes, answer.ToString());

        return answer.Messages()[0];
    }

    /// <summary>Whether a string member holds a real string that satisfies <paramref name="test"/>; null, missing and non-strings never do.</summary>
    public static bool TextIs(CorpusRow row, string path, Func<string, bool> test) => Corpus.Text(row, path) is { } text && test(text);

    /// <summary>The numeric value of a decimal member in either representation (Decimal128 or text), or null.</summary>
    public static decimal? DecimalEither(CorpusRow row, string path) => Corpus.ValueAt(row, path) switch
    {
        BsonString text => decimal.Parse(text.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture),
        { IsNumeric: true } => Corpus.Decimal(row, path),
        _ => null,
    };

    /// <summary>The ids of the named rows, sorted into the engine's root order.</summary>
    public static IReadOnlyList<Guid> Keys(string entity, params string[] keys) =>
        keys.Select(key => Corpus.Row(entity, key)).Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => row.Id).ToList();

    /// <summary>Every id of an entity (organisation A) except the given ones, in id order.</summary>
    public static IReadOnlyList<Guid> AllBut(string entity, IEnumerable<Guid> excluded)
    {
        var set = excluded.ToHashSet();

        return Corpus.AllIds(entity).Where(id => !set.Contains(id)).ToList();
    }
}
