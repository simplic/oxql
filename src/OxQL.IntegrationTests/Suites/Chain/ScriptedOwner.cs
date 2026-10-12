using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Chain;

/// <summary>
/// An owner of <c>owner.widget</c> for the failure-mode cases the chaos owner has no mode for: it
/// answers every query as <see cref="ChaosOwner.Answer"/> does, then hides widgets
/// (<see cref="Hidden"/>), cuts each answer to <see cref="Cut"/> rows and says a next page exists,
/// answers late (<see cref="Delay"/>) or with an error status (<see cref="Status"/>), and reports
/// an engine version and a batch cap on its shallow health (<see cref="Version"/>,
/// <see cref="MaxBatchQueries"/>). Every batch it receives is kept (<see cref="Batches"/>).
/// </summary>
public sealed class ScriptedOwner : HttpMessageHandler
{
    private readonly ConcurrentQueue<JsonNode> batches = new();

    /// <summary>The widget codes the owner answers as if they did not exist.</summary>
    public ConcurrentDictionary<string, bool> Hidden { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every answer holds at most this many rows and says <c>hasNextPage: true</c>.</summary>
    public int? Cut { get; set; }

    /// <summary>How long a batch answer waits; the caller's budget cancels it.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>When set, every batch is answered with this status and a plain-text body.</summary>
    public HttpStatusCode? Status { get; set; }

    /// <summary>The engine version the shallow health reports.</summary>
    public string Version { get; set; } = "9.9.9";

    /// <summary>The <c>limits.maxBatchQueries</c> the shallow health reports; none when null.</summary>
    public int? MaxBatchQueries { get; set; }

    /// <summary>Every batch body received, in arrival order.</summary>
    public IReadOnlyList<JsonNode> Batches => batches.ToArray();

    /// <summary>Back to honest, immediate answers; the batch log is kept.</summary>
    public void Reset()
    {
        Hidden.Clear();
        Cut = null;
        Delay = TimeSpan.Zero;
        Status = null;
        Version = "9.9.9";
        MaxBatchQueries = null;
    }

    /// <summary>The batches received after the first <paramref name="mark"/>.</summary>
    public IReadOnlyList<JsonNode> Since(int mark) => Batches.Skip(mark).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";

        if (request.Method == HttpMethod.Get && path.EndsWith("/OxQL/health", StringComparison.OrdinalIgnoreCase))
        {
            var limits = new JsonObject();

            if (MaxBatchQueries is { } cap)
                limits["maxBatchQueries"] = cap;

            return Json(new JsonObject
            {
                ["status"] = "healthy",
                ["service"] = "oxql",
                ["engine"] = new JsonObject { ["version"] = Version, ["contract"] = 2 },
                ["capabilities"] = new JsonArray("batch"),
                ["limits"] = limits,
                ["remote"] = new JsonArray(),
            });
        }

        if (request.Method != HttpMethod.Post || !path.EndsWith("/OxQL/batch", StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)) ?? new JsonObject();
        batches.Enqueue(body.DeepClone());

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        if (Status is { } status)
            return new HttpResponseMessage(status) { Content = new StringContent($"scripted: status {(int)status}", Encoding.UTF8, "text/plain") };

        var results = new JsonArray();

        foreach (var query in body["queries"] as JsonArray ?? [])
        {
            var answer = ChaosOwner.Answer(query);

            if (answer["items"] is JsonArray items)
            {
                foreach (var hidden in items.OfType<JsonObject>().Where(item => item["code"] is JsonValue code && Hidden.ContainsKey(code.GetValue<string>())).ToList())
                    items.Remove(hidden);

                if (Cut is { } cut)
                {
                    while (items.Count > cut)
                        items.RemoveAt(items.Count - 1);

                    answer["pageInfo"]!["hasNextPage"] = true;
                }
            }

            results.Add(answer);
        }

        return Json(new JsonObject { ["results"] = results });
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
