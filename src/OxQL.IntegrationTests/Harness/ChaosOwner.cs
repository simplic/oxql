using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Harness;

/// <summary>What the chaos owner's batch route does.</summary>
public enum ChaosMode
{
    /// <summary>Correct answers: the three widgets, or <see cref="ChaosSettings.Many"/> synthetic ones.</summary>
    Ok,

    /// <summary>Correct answers after <see cref="ChaosSettings.Delay"/>.</summary>
    Slow,

    /// <summary>Accepts the request and never answers; the caller's budget or cancellation ends it.</summary>
    Hang,

    /// <summary>Answers <see cref="ChaosSettings.Status"/> with a plain-text body.</summary>
    Status,

    /// <summary>200, <c>application/json</c>, a body that is not JSON.</summary>
    Garbage,

    /// <summary>200 with a zero-length body.</summary>
    Empty,

    /// <summary>200, <c>{"results":"nope"}</c>.</summary>
    WrongShape,

    /// <summary>200, one result fewer than queries (the last is dropped).</summary>
    Short,

    /// <summary>200, the first result is null, the rest correct.</summary>
    NullEntry,

    /// <summary>200, every result <c>{"items":"x","pageInfo":{}}</c>.</summary>
    ItemsNotArray,

    /// <summary>200, correct rows with the key member <c>code</c> removed from every row.</summary>
    MissingField,

    /// <summary>200, every result holds two rows keyed <c>W-1</c> with different names.</summary>
    DuplicateKeys,

    /// <summary>200, every result a refusal envelope carrying <see cref="ChaosSettings.Code"/>.</summary>
    Refuse,

    /// <summary>The response head arrives, then the connection dies while the body is read.</summary>
    Reset,

    /// <summary>200, a single row whose name is 64 MB long, streamed.</summary>
    Huge,

    /// <summary>Nothing listens: every call, the health probe included, fails as a refused connection.</summary>
    Closed,
}

/// <summary>What the chaos owner's health route does, independently of <see cref="ChaosMode"/>.</summary>
public enum ChaosHealth
{
    Ok,

    /// <summary>503.</summary>
    Down,

    /// <summary>Never answers.</summary>
    Hang,
}

/// <summary>The chaos owner's programmable state. Every member but <see cref="Mode"/> has the legacy default.</summary>
public sealed record ChaosSettings
{
    public ChaosMode Mode { get; init; } = ChaosMode.Ok;

    /// <summary><see cref="ChaosMode.Slow"/>: how long a correct answer takes.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary><see cref="ChaosMode.Status"/>: the status answered.</summary>
    public HttpStatusCode Status { get; init; } = HttpStatusCode.InternalServerError;

    /// <summary><see cref="ChaosMode.Refuse"/>: the refusal code of every result.</summary>
    public string Code { get; init; } = "BATCH_TOO_LARGE";

    /// <summary><see cref="ChaosMode.Ok"/> only: answer over this many synthetic widgets instead of the three.</summary>
    public int? Many { get; init; }

    /// <summary>After this many batch calls the owner resets itself to <see cref="ChaosMode.Ok"/> and the defaults; null is sticky.</summary>
    public int? Times { get; init; }
}

/// <summary>One request the chaos owner received.</summary>
public sealed record ChaosRequest(DateTimeOffset At, string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, JsonNode? Body, ChaosMode? AnsweredBy)
{
    /// <summary>The queries of a batch request, or none.</summary>
    public IReadOnlyList<JsonNode> Queries => Body?["queries"] is JsonArray queries ? queries.Where(query => query is not null).Select(query => query!).ToList() : [];

    /// <summary>A header value, or null.</summary>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The <paramref name="index"/>-th stage of kind <paramref name="kind"/> (<c>match</c>, <c>project</c>, <c>page</c> …) of one query of the batch, or null.</summary>
    public JsonNode? Stage(string kind, int query = 0, int index = 0) =>
        Queries.ElementAtOrDefault(query)?["pipeline"] is JsonArray stages
            ? stages.Select(stage => stage?[kind]).Where(found => found is not null).ElementAtOrDefault(index)
            : null;

    /// <summary>The first condition of the <paramref name="index"/>-th match stage of a query, as path, operator and operand; null when there is none.</summary>
    public (string Path, string Operator, JsonNode? Operand)? Condition(int query = 0, int index = 0) =>
        Stage("match", query, index) is JsonObject match && match.FirstOrDefault() is { Value: JsonObject test } leaf && test.FirstOrDefault(pair => pair.Key != "options") is { Key: not null } op
            ? (leaf.Key, op.Key, op.Value)
            : null;
}

/// <summary>
/// A programmable remote owner of <c>owner.widget</c>, mounted into a fleet with
/// <see cref="LabFleet.Mount"/>: the owner the conformance entity's widget references resolve
/// against. It serves the engine's two remote routes, <c>POST OxQL/batch</c> and
/// <c>GET OxQL/health</c>, in process, and misbehaves on demand (<see cref="ChaosMode"/>).
/// <para>
/// <b>The entity.</b> <c>owner.widget</c> is keyed on <c>code</c>. It also carries a member named
/// <c>id</c> that holds a different widget's code: a join on the wrong member succeeds and returns
/// the wrong widget rather than nothing. Every <c>match</c> stage of a query is applied (leaves with
/// several operators, <c>and</c>/<c>or</c>/<c>not</c>, <c>options.ignoreCase</c>); a path it does not have
/// is refused with <c>UNKNOWN_PATH</c>, an unknown entity with <c>UNKNOWN_ENTITY</c>. Projection
/// (<c>$default</c> expands to the key and display, <c>code</c> and <c>name</c>), sort on
/// <c>code</c>/<c>id</c>/<c>name</c>, and <c>page</c> offset/limit/includeTotalCount are honoured.
/// </para>
/// <para>
/// <b>Control.</b> <see cref="Set(ChaosSettings)"/>, <see cref="SetHealth"/>, <see cref="Reset"/>,
/// the counters, and the request log (<see cref="Requests"/>, <see cref="Mark"/>/<see cref="Since"/>).
/// A <see cref="Frozen"/> owner (the shared fleet's) refuses every change, so no test can make the
/// owner misbehave under another test's feet: chaos tests mount their own owner in a private
/// fleet.
/// </para>
/// </summary>
public sealed class ChaosOwner : HttpMessageHandler
{
    /// <summary>The entity the owner serves.</summary>
    public const string Entity = "owner.widget";

    /// <summary>The members of a widget.</summary>
    public static readonly IReadOnlyList<string> Members = ["code", "id", "name", "organizationId"];

    /// <summary>The three widgets. <c>id</c> is a decoy naming a different widget.</summary>
    public static readonly IReadOnlyList<Widget> Widgets =
    [
        new("W-1", "W-2", "Widget One", LabIdentity.OrganisationA),
        new("W-2", "W-3", "Widget Two", LabIdentity.OrganisationA),
        new("W-3", "W-1", "Widget Three", LabIdentity.OrganisationA),
    ];

    private static readonly JsonObject HealthBody = new()
    {
        ["status"] = "healthy",
        ["service"] = "oxql",
        ["engine"] = new JsonObject { ["version"] = "chaos-owner", ["contract"] = 2 },
        ["capabilities"] = new JsonArray("batch"),
        ["limits"] = new JsonObject(),
        ["remote"] = new JsonArray(),
    };

    private readonly ConcurrentQueue<ChaosRequest> requests = new();
    private readonly object gate = new();
    private ChaosSettings settings = new();
    private ChaosHealth health = ChaosHealth.Ok;
    private int batchCalls;
    private int healthCalls;
    private int? remaining;

    /// <summary>A frozen owner refuses every change of state: the shared fleet's owner, which always answers correctly.</summary>
    public bool Frozen { get; private set; }

    /// <summary>The current settings.</summary>
    public ChaosSettings Settings
    {
        get
        {
            lock (gate)
                return settings;
        }
    }

    public ChaosHealth Health
    {
        get
        {
            lock (gate)
                return health;
        }
    }

    /// <summary>How many batch calls arrived since the counters were last reset.</summary>
    public int BatchCalls => Volatile.Read(ref batchCalls);

    /// <summary>How many health probes arrived since the counters were last reset.</summary>
    public int HealthCalls => Volatile.Read(ref healthCalls);

    /// <summary>Every request received, in arrival order.</summary>
    public IReadOnlyList<ChaosRequest> Requests => requests.ToArray();

    /// <summary>A mark in the request log; <see cref="Since"/> answers what arrived after it.</summary>
    public int Mark() => requests.Count;

    /// <summary>The requests that arrived after <paramref name="mark"/>.</summary>
    public IReadOnlyList<ChaosRequest> Since(int mark) => requests.Skip(mark).ToList();

    /// <summary>The batch requests that arrived after <paramref name="mark"/>.</summary>
    public IReadOnlyList<ChaosRequest> BatchesSince(int mark) => Since(mark).Where(request => request.AnsweredBy is not null).ToList();

    /// <summary>Stops every later change of state.</summary>
    public ChaosOwner Freeze()
    {
        Frozen = true;
        return this;
    }

    /// <summary>Sets the batch behaviour; <see cref="ChaosSettings.Times"/> makes it reset itself after that many batch calls.</summary>
    public void Set(ChaosSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        ThrowIfFrozen();

        lock (gate)
        {
            settings = next;
            remaining = next.Times;
        }
    }

    /// <summary>Sets the batch behaviour to <paramref name="mode"/> with the defaults.</summary>
    public void Set(ChaosMode mode) => Set(new ChaosSettings { Mode = mode });

    public void SetHealth(ChaosHealth next)
    {
        ThrowIfFrozen();

        lock (gate)
            health = next;
    }

    /// <summary>Back to correct answers and a healthy probe; with <paramref name="counters"/> the counters and the log start again.</summary>
    public void Reset(bool counters = true)
    {
        ThrowIfFrozen();

        lock (gate)
        {
            settings = new ChaosSettings();
            health = ChaosHealth.Ok;
            remaining = null;
        }

        if (counters)
        {
            Interlocked.Exchange(ref batchCalls, 0);
            Interlocked.Exchange(ref healthCalls, 0);
            requests.Clear();
        }
    }

    private void ThrowIfFrozen()
    {
        if (Frozen)
            throw new InvalidOperationException("This chaos owner is frozen (it serves the shared fleet); mount an owner of your own in a private fleet to change its behaviour.");
    }

    // ── the transport ───────────────────────────────────────────────────────────────────────

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri?.AbsolutePath ?? "";
        var isBatch = request.Method == HttpMethod.Post && path.EndsWith("/OxQL/batch", StringComparison.OrdinalIgnoreCase);
        var isHealth = request.Method == HttpMethod.Get && path.EndsWith("/OxQL/health", StringComparison.OrdinalIgnoreCase);

        ChaosSettings answering;
        ChaosHealth probing;

        lock (gate)
        {
            answering = settings;
            probing = health;

            if (isBatch && remaining is not null && --remaining <= 0)
            {
                settings = new ChaosSettings();
                remaining = null;
            }
        }

        requests.Enqueue(new ChaosRequest(
            DateTimeOffset.UtcNow,
            request.Method.Method,
            path,
            request.RequestUri?.Query ?? "",
            request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase),
            Parse(text),
            isBatch ? answering.Mode : null));

        if (answering.Mode == ChaosMode.Closed && (isBatch || isHealth))
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (the chaos owner is closed).", new SocketException((int)SocketError.ConnectionRefused));

        if (isHealth)
        {
            Interlocked.Increment(ref healthCalls);

            return probing switch
            {
                ChaosHealth.Down => Json(HttpStatusCode.ServiceUnavailable, new JsonObject { ["status"] = "unhealthy" }),
                ChaosHealth.Hang => await HangAsync(cancellationToken),
                _ => Json(HttpStatusCode.OK, HealthBody.DeepClone()),
            };
        }

        if (!isBatch)
            return Json(HttpStatusCode.NotFound, new JsonObject { ["title"] = "Not Found", ["status"] = 404 });

        Interlocked.Increment(ref batchCalls);

        return await BatchAsync(answering, Parse(text), cancellationToken);
    }

    private async Task<HttpResponseMessage> BatchAsync(ChaosSettings answering, JsonNode? body, CancellationToken cancellationToken)
    {
        var queries = body?["queries"] as JsonArray ?? [];
        JsonArray Correct() => new(queries.Select(query => (JsonNode?)Answer(query, answering.Many)).ToArray());

        switch (answering.Mode)
        {
            case ChaosMode.Slow:
                await Task.Delay(answering.Delay, cancellationToken);
                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = Correct() });

            case ChaosMode.Hang:
                return await HangAsync(cancellationToken);

            case ChaosMode.Status:
                return new HttpResponseMessage(answering.Status)
                {
                    Content = new StringContent($"chaos: status {(int)answering.Status}", Encoding.UTF8, "text/plain"),
                };

            case ChaosMode.Garbage:
                return Raw(HttpStatusCode.OK, "not json at all");

            case ChaosMode.Empty:
                return Raw(HttpStatusCode.OK, "");

            case ChaosMode.WrongShape:
                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = "nope" });

            case ChaosMode.Short:
            {
                var results = Correct();

                if (results.Count > 0)
                    results.RemoveAt(results.Count - 1);

                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = results });
            }

            case ChaosMode.NullEntry:
            {
                var results = Correct();

                if (results.Count > 0)
                    results[0] = null;

                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = results });
            }

            case ChaosMode.ItemsNotArray:
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["results"] = new JsonArray(queries.Select(_ => (JsonNode?)new JsonObject { ["items"] = "x", ["pageInfo"] = new JsonObject() }).ToArray()),
                });

            case ChaosMode.MissingField:
            {
                var results = Correct();

                foreach (var result in results)
                {
                    if (result?["items"] is JsonArray items)
                    {
                        foreach (var item in items.OfType<JsonObject>())
                            item.Remove("code");
                    }
                }

                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = results });
            }

            case ChaosMode.DuplicateKeys:
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["results"] = new JsonArray(queries.Select(_ => (JsonNode?)new JsonObject
                    {
                        ["items"] = new JsonArray(
                            new JsonObject { ["code"] = "W-1", ["id"] = "W-2", ["name"] = "Widget One" },
                            new JsonObject { ["code"] = "W-1", ["id"] = "W-2", ["name"] = "Widget One (duplicate)" }),
                        ["pageInfo"] = new JsonObject { ["hasNextPage"] = false },
                    }).ToArray()),
                });

            case ChaosMode.Refuse:
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["results"] = new JsonArray(queries.Select(_ => (JsonNode?)Refusal(answering.Code, $"chaos: refused with {answering.Code}.")).ToArray()),
                });

            case ChaosMode.Reset:
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ResettingStream()) { Headers = { { "Content-Type", "application/json" } } } };

            case ChaosMode.Huge:
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HugeStream()) { Headers = { { "Content-Type", "application/json" } } } };

            default:
                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = Correct() });
        }
    }

    private static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new OperationCanceledException(cancellationToken);
    }

    // ── the entity ──────────────────────────────────────────────────────────────────────────

    /// <summary>One widget.</summary>
    public sealed record Widget(string Code, string Id, string Name, Guid OrganizationId)
    {
        public JsonNode? Member(string name) => name switch
        {
            "code" => Code,
            "id" => Id,
            "name" => Name,
            "organizationId" => OrganizationId.ToString("D"),
            _ => null,
        };
    }

    private static IReadOnlyList<Widget> WidgetsOf(int? many)
    {
        if (many is not { } count)
            return Widgets;

        var width = Math.Max(4, count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        string Label(int n) => "W-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');

        return Enumerable.Range(1, count).Select(n => new Widget(Label(n), Label((n % count) + 1), "Widget " + n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0'), LabIdentity.OrganisationA)).ToList();
    }

    /// <summary>Answers one query the way a faithful owner of <c>owner.widget</c> would.</summary>
    public static JsonObject Answer(JsonNode? query, int? many = null)
    {
        if (query?["entityType"]?.GetValue<string>() != Entity)
            return Refusal("UNKNOWN_ENTITY", $"'{query?["entityType"]}' is not an entity of owner.");

        var stages = query["pipeline"] as JsonArray ?? [];
        var page = stages.Select(stage => stage?["page"]).FirstOrDefault(found => found is not null) as JsonObject ?? [];
        var sort = stages.Select(stage => stage?["sort"]).FirstOrDefault(found => found is not null) as JsonArray ?? [];
        var projectStage = stages.Select(stage => stage?["project"]).FirstOrDefault(found => found is not null) as JsonObject;
        var fields = projectStage?["fields"] as JsonObject ?? projectStage;

        IEnumerable<Widget> rows;

        try
        {
            var matches = stages.Select(stage => stage?["match"]).Where(match => match is not null).ToList();
            rows = WidgetsOf(many).Where(row => matches.All(match => Matches(row, match!))).ToList();
        }
        catch (RefusedException refused)
        {
            return Refusal(refused.Code, refused.Message, refused.Path);
        }

        // A grouped query (the internal keyedBy): the rows holding a key, at most perKey per key
        // by record key, as an owner's window leaves them.
        if (query["keyedBy"] is JsonObject keyedBy)
        {
            var path = keyedBy["path"]?.GetValue<string>() ?? "";

            if (!Members.Contains(path))
                return Refusal("UNKNOWN_PATH", $"'{path}' is not a path of owner.widget.", path);

            var keys = (keyedBy["keys"] as JsonArray ?? []).Select(key => key?.ToString()).ToHashSet(StringComparer.Ordinal);
            var perKey = keyedBy["perKey"]?.GetValue<int>() ?? 2;

            rows = rows
                .Where(row => keys.Contains(row.Member(path)?.ToString()))
                .GroupBy(row => row.Member(path)?.ToString(), StringComparer.Ordinal)
                .SelectMany(group => group.OrderBy(row => row.Id, StringComparer.Ordinal).Take(perKey))
                .ToList();
        }

        foreach (var entry in sort.Reverse().OfType<JsonObject>())
        {
            var (path, direction) = entry.Select(pair => (pair.Key, pair.Value?.GetValue<string>())).FirstOrDefault();

            if (!Members.Contains(path))
                return Refusal("UNKNOWN_PATH", $"'{path}' is not a path of owner.widget.", path);

            rows = direction == "desc"
                ? rows.OrderByDescending(row => row.Member(path)?.GetValue<string>(), StringComparer.Ordinal)
                : rows.OrderBy(row => row.Member(path)?.GetValue<string>(), StringComparer.Ordinal);
        }

        var all = rows.ToList();
        var offset = page["offset"]?.GetValue<int>() ?? 0;
        var limit = page["limit"]?.GetValue<int>() ?? 100;
        var slice = all.Skip(offset).Take(limit).ToList();

        var wanted = fields is null
            ? Members.ToHashSet()
            : fields.Select(pair => pair.Key).SelectMany(field => field == "$default" ? ["code", "name"] : new[] { field }).ToHashSet();

        var items = new JsonArray(slice.Select(row => (JsonNode?)new JsonObject(Members
            .Where(wanted.Contains)
            .Select(member => KeyValuePair.Create(member, row.Member(member))))).ToArray());

        var pageInfo = new JsonObject { ["hasNextPage"] = offset + limit < all.Count };

        if (page["includeTotalCount"]?.GetValue<bool>() == true)
        {
            pageInfo["totalCount"] = all.Count;
            pageInfo["totalCountCapped"] = false;
        }

        return new JsonObject { ["items"] = items, ["pageInfo"] = pageInfo };
    }

    private static bool Matches(Widget row, JsonNode condition)
    {
        if (condition is not JsonObject node)
            return true;

        if (node["and"] is JsonArray all)
            return all.All(inner => inner is null || Matches(row, inner));

        if (node["or"] is JsonArray any)
            return any.Any(inner => inner is not null && Matches(row, inner));

        if (node["not"] is JsonObject not)
            return !Matches(row, not);

        return node.All(pair => Test(row, pair.Key, pair.Value as JsonObject));
    }

    private static bool Test(Widget row, string path, JsonObject? operators)
    {
        if (!Members.Contains(path))
            throw new RefusedException("UNKNOWN_PATH", $"'{path}' is not a path of owner.widget.", path);

        var ignoreCase = operators?["options"]?["ignoreCase"]?.GetValue<bool>() == true;
        string? Fold(string? value) => ignoreCase ? value?.ToLowerInvariant() : value;
        var actual = Fold(row.Member(path)?.GetValue<string>());

        foreach (var (op, raw) in operators ?? [])
        {
            if (op == "options")
                continue;

            string? Value() => Fold(raw?.GetValue<string>());
            IReadOnlyList<string?> Values() => raw is JsonArray list ? list.Select(item => Fold(item?.GetValue<string>())).ToList() : [];

            var pass = op switch
            {
                "eq" => actual == Value(),
                "neq" => actual != Value(),
                "in" => Values().Contains(actual),
                "nin" => !Values().Contains(actual),
                "gt" => string.CompareOrdinal(actual, Value()) > 0,
                "gte" => string.CompareOrdinal(actual, Value()) >= 0,
                "lt" => string.CompareOrdinal(actual, Value()) < 0,
                "lte" => string.CompareOrdinal(actual, Value()) <= 0,
                "contains" => (actual ?? "").Contains(Value() ?? "", StringComparison.Ordinal),
                "startsWith" => (actual ?? "").StartsWith(Value() ?? "", StringComparison.Ordinal),
                "endsWith" => (actual ?? "").EndsWith(Value() ?? "", StringComparison.Ordinal),
                "exists" => (actual is not null) == (raw?.GetValue<bool>() == true),
                _ => throw new RefusedException("UNKNOWN_OPERATOR", $"'{op}' is not an operator.", path),
            };

            if (!pass)
                return false;
        }

        return true;
    }

    private static JsonObject Refusal(string code, string message, string? path = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };

        if (path is not null)
            error["path"] = path;

        return new JsonObject { ["type"] = "validation_error", ["title"] = "The request could not be bound.", ["errors"] = new JsonArray(error) };
    }

    private sealed class RefusedException(string code, string message, string? path) : Exception(message)
    {
        public string Code { get; } = code;

        public string? Path { get; } = path;
    }

    // ── responses ───────────────────────────────────────────────────────────────────────────

    private static JsonNode? Parse(string text)
    {
        if (text.Length == 0)
            return null;

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) => Raw(status, body.ToJsonString());

    private static HttpResponseMessage Raw(HttpStatusCode status, string text) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    /// <summary>A body whose first read fails the way a connection reset mid-response does.</summary>
    private sealed class ResettingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The chaos owner reset the connection.", new SocketException((int)SocketError.ConnectionReset));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A valid batch body whose one row carries a 64 MB name, produced as it is read.</summary>
    private sealed class HugeStream : Stream
    {
        public const int Megabytes = 64;

        private static readonly byte[] Head = Encoding.UTF8.GetBytes("""{"results":[{"items":[{"code":"W-1","name":" """.TrimEnd());
        private static readonly byte[] Tail = Encoding.UTF8.GetBytes("""}],"pageInfo":{"hasNextPage":false}}]}""");

        private long position;

        private static long Total => Head.Length + (Megabytes * 1024L * 1024L) + 1 + Tail.Length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => Total;

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var written = 0;
            var fillEnd = Head.Length + (Megabytes * 1024L * 1024L);

            while (written < count && position < Total)
            {
                byte next;

                if (position < Head.Length)
                    next = Head[position];
                else if (position < fillEnd)
                    next = (byte)'x';
                else if (position == fillEnd)
                    next = (byte)'"';
                else
                    next = Tail[position - fillEnd - 1];

                buffer[offset + written] = next;
                written++;
                position++;
            }

            return written;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
