using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Harness;

/// <summary>
/// The verbs a suite speaks to one lab service with: hand-written wire JSON in, the status and the
/// parsed body out, nothing decoded and nothing thrown on a refusal. There is no typed builder on
/// purpose: a case says exactly which bytes the engine gets.
/// <para>
/// Every body parameter takes a raw JSON string (a C# raw string literal reads best), a
/// <see cref="JsonNode"/>, or any object System.Text.Json serializes (an anonymous object, a
/// dictionary where a key holds a dot). A client is immutable; <see cref="As(Org)"/>,
/// <see cref="Contract"/>, <see cref="Anonymous"/> and <see cref="WithHeader"/> return a changed copy.
/// </para>
/// </summary>
public sealed class LabClient
{
    /// <summary>Who a request is sent as: organisation, user, contract (null sends no contract header) and extra headers.</summary>
    public sealed record Identity(Guid? Organisation, Guid? User, int? Contract, IReadOnlyDictionary<string, string> Headers);

    private readonly Func<Task<FleetHost>> host;

    internal LabClient(CorpusFleet fleet, LabService service, Func<Task<FleetHost>> host, Identity identity)
    {
        Fleet = fleet;
        Service = service;
        Who = identity;
        this.host = host;
    }

    public CorpusFleet Fleet { get; }

    public LabService Service { get; }

    public Identity Who { get; }

    /// <summary>The same client as another corpus organisation.</summary>
    public LabClient As(Org org) => As(org.Id());

    /// <summary>The same client as any organisation id; null sends no organisation (the engine answers 403).</summary>
    public LabClient As(Guid? organisation) => new(Fleet, Service, host, Who with { Organisation = organisation });

    /// <summary>The same client with no identity at all: no organisation, no user.</summary>
    public LabClient Anonymous() => new(Fleet, Service, host, Who with { Organisation = null, User = null });

    /// <summary>The same client under another contract; null sends no contract header.</summary>
    public LabClient Contract(int? contract) => new(Fleet, Service, host, Who with { Contract = contract });

    /// <summary>The same client with one more header on every request.</summary>
    public LabClient WithHeader(string name, string value) =>
        new(Fleet, Service, host, Who with { Headers = new Dictionary<string, string>(Who.Headers) { [name] = value } });

    /// <summary>The host this client talks to.</summary>
    public Task<FleetHost> HostAsync() => host();

    // ── the requests ────────────────────────────────────────────────────────────────────────

    /// <summary><c>POST OxQL/query</c> with a whole request body: <c>{ "entityType": …, "pipeline": [ … ] }</c>.</summary>
    public Task<WireAnswer> QueryAsync(object request) => PostAsync("OxQL/query", Json.Text(request));

    /// <summary><c>POST OxQL/query</c> for <paramref name="entityType"/> with a pipeline (a JSON array) and optional variables.</summary>
    public Task<WireAnswer> SendAsync(string entityType, object pipeline, object? variables = null) =>
        QueryAsync(Json.Request(entityType, pipeline, variables));

    /// <summary><c>POST OxQL/batch</c> with the given queries, each a whole request body.</summary>
    public Task<WireAnswer> BatchAsync(IEnumerable<object> queries, int? maxTimeMs = null)
    {
        var body = new JsonObject { ["queries"] = new JsonArray(queries.Select(query => (JsonNode?)Json.Node(query)).ToArray()) };

        if (maxTimeMs is not null)
            body["maxTimeMs"] = maxTimeMs;

        return PostAsync("OxQL/batch", body.ToJsonString());
    }

    /// <summary><c>POST OxQL/batch</c> with a whole body, for a malformed batch.</summary>
    public Task<WireAnswer> BatchBodyAsync(object body) => PostAsync("OxQL/batch", Json.Text(body));

    /// <summary><c>POST OxQL/explain</c> on the service's explain-enabled variant (explain is off by default).</summary>
    public async Task<WireAnswer> ExplainAsync(object request) =>
        await SendAsync(await Fleet.ExplainHostAsync(Service), HttpMethod.Post, "OxQL/explain", Json.Text(request), "application/json");

    /// <summary><c>POST OxQL/explain</c> on this client's own host: 404 unless it is a variant with explain enabled.</summary>
    public Task<WireAnswer> ExplainHereAsync(object request) => PostAsync("OxQL/explain", Json.Text(request));

    /// <summary><c>GET OxQL/health</c>; <paramref name="shallow"/> leaves the remote services out.</summary>
    public Task<WireAnswer> HealthAsync(bool shallow = false) => GetAsync(shallow ? "OxQL/health?shallow=true" : "OxQL/health");

    /// <summary>A raw POST: any route, any body text, any content type. For malformed and oversized requests.</summary>
    public async Task<WireAnswer> PostAsync(string route, string body, string contentType = "application/json") =>
        await SendAsync(await host(), HttpMethod.Post, route, body, contentType);

    /// <summary>A raw GET.</summary>
    public async Task<WireAnswer> GetAsync(string route) => await SendAsync(await host(), HttpMethod.Get, route, null, null);

    private async Task<WireAnswer> SendAsync(FleetHost target, HttpMethod method, string route, string? body, string? contentType)
    {
        using var client = target.Server.CreateClient();
        using var request = new HttpRequestMessage(method, route);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        }

        if (Who.Contract is { } contract)
            request.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, contract.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (Who.Organisation is { } organisation)
            request.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, organisation.ToString("D"));

        if (Who.User is { } user)
            request.Headers.TryAddWithoutValidation(LabIdentity.UserHeader, user.ToString("D"));

        foreach (var (name, value) in Who.Headers)
            request.Headers.TryAddWithoutValidation(name, value);

        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

        return new WireAnswer(response.StatusCode, Json.TryParse(text), text, headers, watch.Elapsed, $"{method} {route} {body}");
    }

    // ── what a case computes before it asserts ──────────────────────────────────────────────

    /// <summary>
    /// Every id of <paramref name="entityType"/> that matches a hand-written condition, in id order:
    /// one page above the set with a total, so a set that outgrew the page fails instead of
    /// validating against a slice of itself.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> MatchIdsAsync(string entityType, object condition, int limit = 500)
    {
        var answer = await SendAsync(entityType, $$"""[{ "match": {{Json.Text(condition)}} }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": {{limit}}, "includeTotalCount": true } }]""");

        answer.ShouldBeOk();
        answer.HasNextPage.Should().BeFalse($"MatchIdsAsync needs the whole set in one page: {answer}");

        return answer.Ids();
    }

    /// <summary>The number of rows a condition matches, read off <c>includeTotalCount</c>.</summary>
    public async Task<long> MatchCountAsync(string entityType, object condition)
    {
        var answer = await SendAsync(entityType, $$"""[{ "match": {{Json.Text(condition)}} }, { "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");

        answer.ShouldBeOk();

        return answer.TotalCount ?? throw new InvalidOperationException($"The engine answered no totalCount: {answer}");
    }

    /// <summary>
    /// Every row of an entity as the engine holds it, in id order, optionally projected, walked
    /// by cursor: the seed as the engine renders it, for a case that compares against the wire
    /// encoding rather than against storage.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> PullAsync(string entityType, object? project = null, int limit = 500)
    {
        var stages = new JsonArray();

        if (project is not null)
            stages.Add(new JsonObject { ["project"] = Json.Node(project) });

        stages.Add(JsonNode.Parse("""{ "sort": [{ "id": "asc" }] }"""));

        return (await WalkAsync(entityType, stages, limit)).Items;
    }

    /// <summary>
    /// Walks a query's cursor to the end and returns every row in the order it arrived.
    /// <paramref name="stages"/> is everything before the <c>page</c> stage (a JSON array), so the
    /// case writes the sort it is testing. A repeated cursor, or more than <paramref name="maxPages"/>
    /// pages, fails the walk.
    /// </summary>
    public async Task<Walk> WalkAsync(string entityType, object stages, int limit, int maxPages = 500, object? variables = null)
    {
        var before = Json.Node(stages) as JsonArray ?? throw new ArgumentException("The stages are a JSON array.", nameof(stages));
        var items = new List<JsonObject>();
        var cursors = new List<string>();
        string? cursor = null;
        WireAnswer last;

        while (true)
        {
            var pipeline = (JsonArray)before.DeepClone();
            var page = new JsonObject { ["limit"] = limit };

            if (cursor is not null)
                page["cursor"] = cursor;

            pipeline.Add(new JsonObject { ["page"] = page });
            last = await SendAsync(entityType, pipeline, variables);
            last.ShouldBeOk($"page {cursors.Count + 1} of the walk");
            items.AddRange(last.Items.OfType<JsonObject>());

            if (!last.HasNextPage)
                break;

            cursor = last.NextCursor ?? throw new InvalidOperationException($"hasNextPage was true and no cursor came with it: {last}");
            cursors.Should().NotContain(cursor, "the engine must not repeat a cursor");
            cursors.Add(cursor);
            cursors.Count.Should().BeLessThan(maxPages, "the walk did not end");
        }

        return new Walk(items, cursors.Count + 1, cursors, last);
    }

    /// <summary>Walks the same query by offset instead, <paramref name="rows"/> rows in pages of <paramref name="limit"/>.</summary>
    public async Task<Walk> OffsetWalkAsync(string entityType, object stages, int limit, int rows)
    {
        var before = Json.Node(stages) as JsonArray ?? throw new ArgumentException("The stages are a JSON array.", nameof(stages));
        var items = new List<JsonObject>();
        var pages = 0;
        WireAnswer? last = null;

        for (var offset = 0; offset < rows; offset += limit)
        {
            var pipeline = (JsonArray)before.DeepClone();
            var page = new JsonObject { ["limit"] = limit };

            if (offset > 0)
                page["offset"] = offset;

            pipeline.Add(new JsonObject { ["page"] = page });
            last = await SendAsync(entityType, pipeline);
            last.ShouldBeOk($"the offset walk at {offset}");
            items.AddRange(last.Items.OfType<JsonObject>());
            pages++;
        }

        return new Walk(items, pages, [], last ?? throw new ArgumentOutOfRangeException(nameof(rows)));
    }

    public override string ToString() => $"{Service}{(Who.Organisation is { } organisation ? " as " + organisation : "")} (contract {Who.Contract?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"})";
}

/// <summary>What a walk saw: every row in arrival order, how many requests it took, every cursor, and the last answer.</summary>
public sealed record Walk(IReadOnlyList<JsonObject> Items, int Pages, IReadOnlyList<string> Cursors, WireAnswer Last)
{
    /// <summary>The ids of the rows, in arrival order.</summary>
    public IReadOnlyList<Guid> Ids => Items.Select(item => Guid.Parse(item["id"]!.GetValue<string>())).ToList();
}

/// <summary>Wire JSON from whatever a case wrote it as.</summary>
public static class Json
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>A node from a raw JSON string, a node, or any serializable object.</summary>
    public static JsonNode? Node(object? value) => value switch
    {
        null => null,
        string text => JsonNode.Parse(text),
        JsonNode node => node.DeepClone(),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), Options),
    };

    /// <summary>The JSON text of a raw JSON string, a node, or any serializable object.</summary>
    public static string Text(object? value) => value switch
    {
        null => "null",
        string text => text,
        JsonNode node => node.ToJsonString(),
        _ => JsonSerializer.Serialize(value, value.GetType(), Options),
    };

    /// <summary>A whole request body: entity, pipeline, and variables when given.</summary>
    public static JsonObject Request(string entityType, object pipeline, object? variables = null)
    {
        var request = new JsonObject { ["entityType"] = entityType };

        if (variables is not null)
            request["variables"] = Node(variables);

        request["pipeline"] = Node(pipeline);

        return request;
    }

    /// <summary>A JSON array of ids, for an <c>in</c> operand: <c>Json.Ids(expected)</c>.</summary>
    public static string Ids(IEnumerable<Guid> ids) => new JsonArray(ids.Select(id => (JsonNode?)id.ToString("D")).ToArray()).ToJsonString();

    /// <summary>Parses text as JSON, or null when it is empty or not JSON.</summary>
    public static JsonNode? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The node at a dotted path inside a node; null when any step is missing.</summary>
    public static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject item || !item.TryGetPropertyValue(segment, out node))
                return null;
        }

        return node;
    }

    /// <summary>Whether a dotted path exists inside a node, null or not.</summary>
    public static bool Has(JsonNode? node, string path)
    {
        var segments = path.Split('.');

        foreach (var segment in segments[..^1])
        {
            if (node is not JsonObject item || !item.TryGetPropertyValue(segment, out node))
                return false;
        }

        return node is JsonObject last && last.ContainsKey(segments[^1]);
    }
}

/// <summary>Reads the payload of a cursor, which is <c>base64url(json).signature</c>; nothing is re-signed.</summary>
public static class Cursors
{
    public static JsonNode Payload(string cursor)
    {
        var encoded = cursor.Split('.')[0].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');

        return JsonNode.Parse(Convert.FromBase64String(encoded))!;
    }

    /// <summary>
    /// The Guid a cursor leg carries. The engine writes each sort value as canonical extended
    /// JSON, so a binary id arrives as <c>{ "$binary": { "base64": …, "subType": "04" } }</c>.
    /// </summary>
    public static System.Guid Guid(string extendedJson)
    {
        var binary = JsonNode.Parse(extendedJson)?["$binary"] ?? throw new FormatException($"Not a binary cursor leg: {extendedJson}");

        return new System.Guid(Convert.FromBase64String(binary["base64"]!.GetValue<string>()), bigEndian: true);
    }
}
