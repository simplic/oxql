using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo.Resolve;

namespace OxQL.Mongo.Explain;

/// <summary>
/// What owners' internal explain answered (DESIGN §4.1): each answer kept 30 seconds by organisation,
/// owner service and the hash of the forwarded body with its variables substituted, so the studio's
/// debounced explains of one query do not ask the owner again. The owner's schema revision is not
/// part of the key, since the origin learns it only from the answer; the 30 seconds bound how long a
/// changed owner model is answered from before. Only answers are kept, never a failed call.
/// </summary>
public sealed class ExplainForwardCache : IDisposable
{
    /// <summary>How long an owner's answer is kept.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>The most answers kept.</summary>
    public const int MaxEntries = 1_000;

    private readonly MemoryCache answers;
    private readonly TimeProvider time;

    /// <summary>An empty cache; <paramref name="time"/> is the clock answers expire by.</summary>
    public ExplainForwardCache(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        answers = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });
    }

    /// <summary>The key of one forwarded body.</summary>
    public static string KeyOf(Guid organisation, string service, ExplainRequest request)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, OxQLJson.Wire);

        return $"{organisation:N}|{service}|{Convert.ToHexString(SHA256.HashData(body))}";
    }

    /// <summary>A kept answer, cloned, or null.</summary>
    public JsonObject? Get(string key) =>
        answers.TryGetValue(key, out Entry? kept) && kept is not null && time.GetUtcNow() < kept.Expires ? (JsonObject)kept.Answer.DeepClone() : null;

    /// <summary>Keeps an answer for <see cref="Ttl"/>.</summary>
    public void Set(string key, JsonObject answer) =>
        answers.Set(key, new Entry((JsonObject)answer.DeepClone(), time.GetUtcNow() + Ttl), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });

    /// <inheritdoc/>
    public void Dispose() => answers.Dispose();

    private sealed record Entry(JsonObject Answer, DateTimeOffset Expires);
}

/// <summary>
/// One explain's calls to owners (DESIGN §4.1, §4.3): the describes of their entities and the check
/// of the parts of the query continued at them, over <see cref="IRemoteQueryClient.ExplainAsync"/>,
/// all within <c>Explain.RemoteTimeoutMs</c> together, answers read through the
/// <see cref="ExplainForwardCache"/>. A call that does not answer — no client, a client that cannot
/// explain, unreachable, timed out, or <c>remote: "skip"</c> — is a <c>REMOTE_UNCHECKED</c> note,
/// never an error.
/// </summary>
public sealed class RemoteExplain : IDescribeOwners
{
    private readonly IRemoteQueryClient? client;
    private readonly ExplainForwardCache cache;
    private readonly RequestContext context;
    private readonly bool skip;
    private readonly TimeSpan budget;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>The calls of one explain of <paramref name="request"/> under <paramref name="context"/>.</summary>
    public RemoteExplain(IRemoteQueryClient? client, ExplainForwardCache cache, RequestContext context, ExplainRequest request)
    {
        this.client = client;
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        skip = request?.Remote == ExplainRequest.RemoteSkip;
        budget = TimeSpan.FromMilliseconds(Math.Max(1, context.Options.Explain.RemoteTimeoutMs));
    }

    /// <summary>The <c>reason</c> of a part no owner was asked for because the request said <c>remote: "skip"</c>.</summary>
    public const string Skipped = "skipped";

    /// <summary>The <c>reason</c> of a part whose owner cannot be explained at (no client, or one without internal explain).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The <c>reason</c> of a part whose owner's call failed.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The <c>reason</c> of a part the remote budget ran out before.</summary>
    public const string Timeout = "timeout";

    /// <inheritdoc/>
    public bool Knows(string service) => client?.IsConfigured(service) == true;

    /// <inheritdoc/>
    /// <remarks>A describe is not a check: it is forwarded under <c>remote: "skip"</c> too, since nothing else can answer it.</remarks>
    public async Task<(JsonObject? Answer, string? Reason)> DescribeAsync(string service, JsonObject entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var entity = entry["entity"]?.GetValue<string>() ?? "";
        var hash = entity.IndexOf('#', StringComparison.Ordinal);
        var request = new ExplainRequest
        {
            Query = new QueryRequest { EntityType = hash < 0 ? entity : entity[..hash], Pipeline = [] },
            Describe = [(JsonObject)entry.DeepClone()],
            Remote = ExplainRequest.RemoteSkip,
            IsEnvelope = true,
        };

        var (answer, reason) = await CallAsync(service, request, cancellationToken).ConfigureAwait(false);

        return answer?["describe"] is JsonArray { Count: > 0 } described && described[0] is JsonObject first
            ? ((JsonObject)first.DeepClone(), null)
            : (null, reason ?? Unsupported);
    }

    /// <summary>
    /// The remote check of a bound request (DESIGN §4.3): every remote keyed stage's owner queries
    /// carrying continued stages are explained at their owners; an owner error at a continued stage is
    /// this request's error at the caller's stage, and an owner's own <c>REMOTE_UNCHECKED</c> notes are
    /// passed on at the first continued stage. What was not checked is noted.
    /// </summary>
    public async Task<(List<QueryValidationError> Errors, List<Diagnostic> Notes)> CheckAsync(BoundPipeline bound, bool strict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bound);

        var errors = new List<QueryValidationError>();
        var notes = new List<Diagnostic>();

        foreach (var resolve in bound.Stages.OfType<BoundStage.Resolve>().Where(stage => stage.IsRemote))
            foreach (var check in KeyedFetch.Checks(bound, resolve, strict))
            {
                if (skip)
                {
                    notes.Add(Unchecked(check, Skipped));
                    continue;
                }

                var request = new ExplainRequest { Query = check.Query, Remote = ExplainRequest.RemoteCheck, IsEnvelope = true };
                var (answer, reason) = await CallAsync(check.Service, request, cancellationToken).ConfigureAwait(false);

                if (answer is null)
                {
                    notes.Add(Unchecked(check, reason ?? Unsupported));
                    continue;
                }

                if (answer["errors"] is JsonArray owned)
                    foreach (var error in owned.OfType<JsonObject>())
                        if (check.Map(error) is { } mapped && !errors.Any(other => other.Code == mapped.Code && other.Stage == mapped.Stage && other.Path == mapped.Path))
                            errors.Add(mapped);

                if (answer["notes"] is JsonArray ownerNotes)
                    foreach (var note in ownerNotes.OfType<JsonObject>().Where(note => note["code"]?.GetValue<string>() == Notes.RemoteUnchecked))
                        notes.Add(new Diagnostic
                        {
                            Code = Notes.RemoteUnchecked,
                            Message = note["message"]?.GetValue<string>() ?? "",
                            Stage = check.FirstContinued,
                            Params = note["params"] is JsonObject parameters
                                ? parameters.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone(), StringComparer.Ordinal)
                                : null,
                        });
            }

        return (errors, notes);
    }

    /// <summary>One forwarded body: from the cache, else from the owner within what is left of the budget.</summary>
    private async Task<(JsonObject? Answer, string? Reason)> CallAsync(string service, ExplainRequest request, CancellationToken cancellationToken)
    {
        if (client is null)
            return (null, Unsupported);

        var key = ExplainForwardCache.KeyOf(context.Organisation ?? Guid.Empty, service, request);

        if (cache.Get(key) is { } kept)
            return (kept, null);

        var remaining = budget - clock.Elapsed;

        if (remaining <= TimeSpan.Zero)
            return (null, Timeout);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(remaining);

        try
        {
            var answer = await client.ExplainAsync(service, request, remaining, timeout.Token).ConfigureAwait(false);

            if (answer is null)
                return (null, Unsupported);

            cache.Set(key, answer);

            return (answer, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, Unreachable);
        }
    }

    private static Diagnostic Unchecked(OwnerCheck check, string reason) => new()
    {
        Code = Notes.RemoteUnchecked,
        Message = reason == Skipped
            ? $"The stages continued at '{check.Service}' for '{check.Target}' were not checked: the request asked for remote \"skip\"."
            : $"The stages continued at '{check.Service}' for '{check.Target}' could not be checked at their owner ({reason}); the owner binds them when the query runs.",
        Stage = check.FirstContinued,
        Params = new Dictionary<string, object?> { ["service"] = check.Service, ["target"] = check.Target, ["reason"] = reason },
    };
}
