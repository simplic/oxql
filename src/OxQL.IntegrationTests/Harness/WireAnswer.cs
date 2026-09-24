using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace OxQL.IntegrationTests.Harness;

/// <summary>
/// Whatever a host answered, undecoded: the status, the parsed body (null when it was empty or not
/// JSON), the raw text, the headers. Readers for the parts of a page (<see cref="Items"/>,
/// <see cref="Ids"/>, <see cref="PageInfo"/>), a refusal (<see cref="ErrorCodes"/>) and a batch
/// (<see cref="Results"/>), and the assertions a case ends with. Every assertion failure prints the
/// request and the whole answer.
/// </summary>
public sealed class WireAnswer
{
    internal WireAnswer(HttpStatusCode status, JsonNode? body, string text, IReadOnlyDictionary<string, string> headers, TimeSpan duration, string request, bool entry = false)
    {
        Status = status;
        Body = body;
        Text = text;
        Headers = headers;
        Duration = duration;
        Request = request;
        IsBatchEntry = entry;
    }

    public HttpStatusCode Status { get; }

    public int StatusCode => (int)Status;

    public JsonNode? Body { get; }

    public string Text { get; }

    /// <summary>Response and content headers, case-insensitive.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    public TimeSpan Duration { get; }

    /// <summary>What was sent, for failure messages.</summary>
    public string Request { get; }

    /// <summary>Whether this is one entry of a batch answer, which carries no status of its own.</summary>
    public bool IsBatchEntry { get; }

    /// <summary>A success body: a 200 carrying <c>items</c> (for a batch entry, just the <c>items</c>).</summary>
    public bool IsPage => (IsBatchEntry || Status == HttpStatusCode.OK) && Body?["items"] is JsonArray;

    // ── a page ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The rows of a page; fails when the answer is not one.</summary>
    public JsonArray Items => Body?["items"] as JsonArray ?? throw new InvalidOperationException($"The answer carries no items: {this}");

    /// <summary>The <c>id</c> of every row (or the Guid at another path), in answer order.</summary>
    public IReadOnlyList<Guid> Ids(string path = "id") =>
        Items.Select(item => Json.At(item, path) is JsonValue value && Guid.TryParse(value.GetValue<string>(), out var id)
            ? id
            : throw new InvalidOperationException($"A row has no Guid at '{path}': {item?.ToJsonString()}")).ToList();

    /// <summary>The node at a dotted path of every row; null where the row has none.</summary>
    public IReadOnlyList<JsonNode?> Values(string path) => Items.Select(item => Json.At(item, path)).ToList();

    /// <summary>The string at a dotted path of every row; null where it is missing, null or not a string.</summary>
    public IReadOnlyList<string?> Strings(string path) =>
        Values(path).Select(value => value is JsonValue json && json.TryGetValue<string>(out var text) ? text : null).ToList();

    public JsonObject PageInfo => Body?["pageInfo"] as JsonObject ?? throw new InvalidOperationException($"The answer carries no pageInfo: {this}");

    public bool HasNextPage => PageInfo["hasNextPage"]?.GetValue<bool>() ?? false;

    public string? NextCursor => PageInfo["nextCursor"]?.GetValue<string>();

    public long? TotalCount => PageInfo["totalCount"]?.GetValue<long>();

    public bool? TotalCountCapped => PageInfo["totalCountCapped"]?.GetValue<bool>();

    /// <summary>The diagnostics beside the rows; the engine leaves the member out when there are none.</summary>
    public IReadOnlyList<JsonObject> Diagnostics => (Body?["diagnostics"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    public IReadOnlyList<string> DiagnosticCodes => Diagnostics.Select(diagnostic => diagnostic["code"]?.GetValue<string>() ?? "").ToList();

    // ── a refusal ───────────────────────────────────────────────────────────────────────────

    /// <summary>The refusal's <c>type</c>, or null.</summary>
    public string? Type => Body?["type"] is JsonValue type && type.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The coded errors of a refusal envelope; empty for a page or a ProblemDetails (whose <c>errors</c> is an object).</summary>
    public IReadOnlyList<JsonObject> Errors => (Body?["errors"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    public IReadOnlyList<string> ErrorCodes => Errors.Select(error => error["code"]?.GetValue<string>() ?? "").ToList();

    /// <summary>Whether the body is an ASP.NET ProblemDetails rather than an OxQL envelope.</summary>
    public bool IsProblemDetails => Body is JsonObject body && body["errors"] is not JsonArray && body.ContainsKey("title") && !(Type ?? "").StartsWith("validation_", StringComparison.Ordinal);

    // ── a batch ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The entries of a batch answer, each readable and assertable like an answer of its own.</summary>
    public IReadOnlyList<WireAnswer> Results => (Body?["results"] as JsonArray ?? throw new InvalidOperationException($"The answer carries no results: {this}"))
        .Select((entry, index) => new WireAnswer(Status, entry?.DeepClone(), entry?.ToJsonString() ?? "null", Headers, Duration, $"{Request} [entry {index}]", entry: true))
        .ToList();

    // ── assertions ──────────────────────────────────────────────────────────────────────────

    /// <summary>Asserts a page (200 with items) and returns the answer.</summary>
    public WireAnswer ShouldBeOk(string because = "")
    {
        IsPage.Should().BeTrue($"{because} expected a page; got {this}".Trim());
        return this;
    }

    /// <summary>
    /// Asserts a refusal that carries <paramref name="code"/> (and, when given, answers
    /// <paramref name="status"/>), and returns the first error with that code.
    /// </summary>
    public JsonObject ShouldRefuse(string code, int? status = null, string because = "")
    {
        if (!IsBatchEntry)
            StatusCode.Should().BeGreaterThanOrEqualTo(400, $"{because} expected a refusal carrying {code}; got {this}".Trim());

        if (status is not null && !IsBatchEntry)
            StatusCode.Should().Be(status, $"{because} {this}".Trim());

        ErrorCodes.Should().Contain(code, $"{because} {this}".Trim());

        return Errors.First(error => error["code"]?.GetValue<string>() == code);
    }

    /// <summary>Asserts exactly these rows, in exactly this order.</summary>
    public WireAnswer ShouldHaveIds(IEnumerable<Guid> expected, string because = "")
    {
        ShouldBeOk(because);
        Ids().Should().Equal(expected, $"{because} {this}".Trim());
        return this;
    }

    /// <summary>Asserts exactly these rows, in any order.</summary>
    public WireAnswer ShouldHaveIdsInAnyOrder(IEnumerable<Guid> expected, string because = "")
    {
        ShouldBeOk(because);
        Ids().Should().BeEquivalentTo(expected, $"{because} {this}".Trim());
        return this;
    }

    /// <summary>Asserts the total count (and, when given, whether it was capped).</summary>
    public WireAnswer ShouldHaveTotal(long expected, bool? capped = null, string because = "")
    {
        ShouldBeOk(because);
        TotalCount.Should().Be(expected, $"{because} {this}".Trim());

        if (capped is not null)
            (TotalCountCapped ?? false).Should().Be(capped.Value, $"{because} {this}".Trim());

        return this;
    }

    /// <summary>Asserts a diagnostic with this code beside the rows, and returns it.</summary>
    public JsonObject ShouldHaveDiagnostic(string code, string because = "")
    {
        DiagnosticCodes.Should().Contain(code, $"{because} {this}".Trim());
        return Diagnostics.First(diagnostic => diagnostic["code"]?.GetValue<string>() == code);
    }

    /// <summary>Asserts no diagnostics at all.</summary>
    public WireAnswer ShouldHaveNoDiagnostics(string because = "")
    {
        Diagnostics.Should().BeEmpty($"{because} {this}".Trim());
        return this;
    }

    public override string ToString()
    {
        var text = Text.Length > 4000 ? Text[..4000] + " …" : Text;

        return $"{(IsBatchEntry ? "entry" : StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture))} {text}\n    request: {(Request.Length > 2000 ? Request[..2000] + " …" : Request)}";
    }
}
