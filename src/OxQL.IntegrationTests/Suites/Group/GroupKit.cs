using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>What the group suites share: the corpus tallies they compute before a request, and readers for grouped rows.</summary>
internal static class GroupKit
{
    /// <summary>Shipments per <c>status.name</c>, in the key order a collated sort gives.</summary>
    public static IReadOnlyList<(string Status, int Count)> ShipmentStatuses(Func<CorpusRow, bool>? filter = null) =>
        Corpus.Rows(Corpus.Shipment)
            .Where(filter ?? (_ => true))
            .GroupBy(row => Corpus.Text(row, "status.name")!)
            .Select(group => (group.Key, group.Count()))
            .Order(Comparer<(string Status, int Count)>.Create((a, b) => Order.CompareCollated(a.Status, b.Status)))
            .ToList();

    /// <summary>The numbers of a status-keyed tally, in its order.</summary>
    public static IReadOnlyList<int> Counts(IEnumerable<(string Status, int Count)> tally) => tally.Select(entry => entry.Count).ToList();

    /// <summary>A grouped count as the wire carries it: a JSON string holding the number.</summary>
    public static IReadOnlyList<string?> CountTexts(WireAnswer answer, string alias) =>
        answer.Values(alias).Select(value => value is JsonValue json && json.GetValueKind() == JsonValueKind.String ? json.GetValue<string>() : null).ToList();

    /// <summary>A grouped value as a number, whether the wire spelled it as a number or as a numeric string.</summary>
    public static decimal? Number(JsonNode? value) => value switch
    {
        JsonValue json when json.GetValueKind() == JsonValueKind.Number => json.GetValue<decimal>(),
        JsonValue json when json.GetValueKind() == JsonValueKind.String => decimal.Parse(json.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Every row's value at <paramref name="alias"/> as a number.</summary>
    public static IReadOnlyList<decimal?> Numbers(WireAnswer answer, string alias) => answer.Values(alias).Select(Number).ToList();

    /// <summary>The JSON kind of every row's value at <paramref name="alias"/>; <c>Undefined</c> where the member is absent.</summary>
    public static IReadOnlyList<JsonValueKind> Kinds(WireAnswer answer, string alias) =>
        answer.Items.Select(item => item is JsonObject row && row.TryGetPropertyValue(alias, out var value) ? value?.GetValueKind() ?? JsonValueKind.Null : JsonValueKind.Undefined).ToList();

    /// <summary>Asserts a refusal carrying exactly one error, <paramref name="code"/>, and returns its message.</summary>
    public static string ShouldRefuseOnly(WireAnswer answer, string code, string because = "")
    {
        var error = answer.ShouldRefuse(code, 400, because);
        answer.ErrorCodes.Should().Equal([code], $"{because} {answer}");
        return error["message"]?.GetValue<string>() ?? "";
    }

    /// <summary>The exact sum of decimal literals, as a decimal literal, however many digits they carry.</summary>
    public static string ExactSum(IEnumerable<string> literals)
    {
        var scale = 0;
        var parsed = new List<(System.Numerics.BigInteger Digits, int Scale)>();

        foreach (var literal in literals)
        {
            var value = decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !literal.Contains('E', StringComparison.OrdinalIgnoreCase) ? literal : throw new FormatException(literal);
            var point = value.IndexOf('.', StringComparison.Ordinal);
            var digits = point < 0 ? value : value.Remove(point, 1);
            var places = point < 0 ? 0 : value.Length - point - 1;
            parsed.Add((System.Numerics.BigInteger.Parse(digits, CultureInfo.InvariantCulture), places));
            scale = Math.Max(scale, places);
        }

        var total = parsed.Aggregate(System.Numerics.BigInteger.Zero, (sum, entry) => sum + (entry.Digits * System.Numerics.BigInteger.Pow(10, scale - entry.Scale)));
        var negative = total.Sign < 0;
        var text = System.Numerics.BigInteger.Abs(total).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        var whole = text[..^scale];
        var fraction = scale == 0 ? "" : text[^scale..].TrimEnd('0');

        return (negative ? "-" : "") + (whole.Length == 0 ? "0" : whole) + (fraction.Length > 0 ? "." + fraction : "");
    }
}
