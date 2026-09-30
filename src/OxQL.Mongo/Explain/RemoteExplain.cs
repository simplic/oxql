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
/// user, owner service and the hash of the forwarded body with its variables substituted, so the
/// studio's debounced explains of one query do not ask the owner again and no user is answered from
/// another's call (an owner may refuse one user what it answers another). The owner's schema revision
/// is not part of the key, since the origin learns it only from the answer; the 30 seconds bound how
/// long a changed owner model is answered from before. Only answers are kept, never a failed call.
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

    /// <summary>The key of one forwarded body, for no user in particular.</summary>
    public static string KeyOf(Guid organisation, string service, ExplainRequest request) => KeyOf(organisation, null, service, request);

    /// <summary>The key of one forwarded body for one user of an organisation.</summary>
    public static string KeyOf(Guid organisation, string? user, string service, ExplainRequest request)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, OxQLJson.Wire);
        var who = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user ?? "")))[..16];

        return $"{organisation:N}|{who}|{service}|{Convert.ToHexString(SHA256.HashData(body))}";
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
    private readonly Func<ExplainRequest, CancellationToken, Task<JsonObject?>>? self;
    private readonly ExplainForwardCache cache;
    private readonly RequestContext context;
    private readonly bool skip;
    private readonly TimeSpan budget;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>The calls of one explain of <paramref name="request"/> under <paramref name="context"/>.</summary>
    public RemoteExplain(IRemoteQueryClient? client, ExplainForwardCache cache, RequestContext context, ExplainRequest request)
        : this(client, cache, context, request, null)
    {
    }

    /// <summary>
    /// The calls of one explain of <paramref name="request"/> under <paramref name="context"/>;
    /// <paramref name="self"/> explains an owner query at this host itself, as its SelfOwner runs
    /// one: the check of the stages continued under a local keyed stage (null: they are noted unchecked).
    /// <paramref name="budget"/> is the time the owners may take together, <c>Explain.RemoteTimeoutMs</c>
    /// when null; an explain nested in another passes what is left of the outer one (<see cref="Remaining"/>).
    /// </summary>
    public RemoteExplain(IRemoteQueryClient? client, ExplainForwardCache cache, RequestContext context, ExplainRequest request,
        Func<ExplainRequest, CancellationToken, Task<JsonObject?>>? self, TimeSpan? budget = null)
    {
        this.client = client;
        this.self = self;
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        skip = request?.Remote == ExplainRequest.RemoteSkip;
        // A nested explain whose outer one spent the budget asks no owner at all.
        this.budget = budget is { } given
            ? (given > TimeSpan.Zero ? given : TimeSpan.Zero)
            : TimeSpan.FromMilliseconds(Math.Max(1, context.Options.Explain.RemoteTimeoutMs));
        spent = this.budget == TimeSpan.Zero;
    }

    /// <summary>
    /// What is left of the owners' budget: an explain this one runs at this host (the check of a local
    /// keyed stage's continued stages) gets it for its own owners, so they cannot stretch the explain
    /// past its budget, while this host's own binding is still checked (RL-8).
    /// </summary>
    public TimeSpan Remaining => spent ? TimeSpan.Zero : budget - clock.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

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

        // A local keyed stage is checked too when stages continue under it: a run sends them to this
        // host's own SelfOwner, which binds them with its model and sends what continues further
        // (a union's every target, a third service) to those owners, whose refusals come back here.
        foreach (var resolve in bound.Stages.OfType<BoundStage.Resolve>().Where(stage => stage.IsRemote || Continuation.Of(bound, stage).Count > 0))
        {
            var checks = KeyedFetch.Checks(bound, resolve, strict, client);
            var misses = new List<Miss>();
            var answered = new HashSet<OwnerCheck>(ReferenceEqualityComparer.Instance);

            foreach (var check in checks)
            {
                var local = check.Bound is { IsRemote: false };

                // "skip" asks no owner; this host's own check asks none either, since it passes "skip" on.
                if (skip && !local)
                {
                    notes.Add(Unchecked(check, Skipped));
                    continue;
                }

                var request = new ExplainRequest { Query = check.Query, Remote = skip ? ExplainRequest.RemoteSkip : ExplainRequest.RemoteCheck, IsEnvelope = true };
                var (answer, reason) = local
                    ? await SelfAsync(request, cancellationToken).ConfigureAwait(false)
                    : await CallAsync(check.Service, request, cancellationToken).ConfigureAwait(false);

                if (answer is null)
                {
                    notes.Add(Unchecked(check, reason ?? Unsupported));
                    continue;
                }

                answered.Add(check);

                OwnerFaults.Scrubbed(answer);

                if (answer["errors"] is JsonArray owned)
                    foreach (var error in owned.OfType<JsonObject>())
                    {
                        if (check.Map(error) is { } mapped)
                        {
                            if (!errors.Any(other => other.Code == mapped.Code && other.Stage == mapped.Stage && other.Path == mapped.Path))
                                errors.Add(mapped);
                        }
                        else if (!local && MissOf(check, error) is { } miss)
                        {
                            misses.Add(miss);
                        }
                    }

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

            Judge(resolve, checks, answered, misses, errors, notes);
        }

        return (errors, notes);
    }

    /// <summary>A path a remote target's owner said the target lacks, at the check query's projection: relative to the alias, or to the owning row.</summary>
    private sealed record Miss(OwnerCheck Check, string Path, bool Parent, string OwnerPath);

    /// <summary>An owner's <c>UNKNOWN_PATH</c> at the check query's projection as a path of the alias or of the owning row; null for any other error.</summary>
    private static Miss? MissOf(OwnerCheck check, JsonObject error)
    {
        if (error["code"]?.ToString() != Codes.UnknownPath || error["stage"] is not JsonValue at || !at.TryGetValue<int>(out var stage) || stage != check.ProjectAt
            || error["path"]?.ToString() is not { Length: > 0 } path)
            return null;

        var element = BoundKeyedBy.Element + ".";

        if (!check.Item)
            return new Miss(check, path, Parent: false, path);

        return path.StartsWith(element, StringComparison.Ordinal)
            ? new Miss(check, path[element.Length..], Parent: false, path)
            : new Miss(check, path, Parent: true, path);
    }

    /// <summary>
    /// What the owners' misses mean for one resolve (DESIGN §3.4.1 flat select, §4.3): a path some
    /// target has is dropped for those that lack it (<c>SELECT_PATH_NOT_ON_TARGET</c>, as a run drops
    /// it); a path no target has is this request's <c>UNKNOWN_PATH</c> where the caller wrote it (the
    /// resolve's select, or the projection after it), with <c>params.owner</c> where the owner saw it.
    /// A local target has what its entity (or item) has; a remote target nobody asked has every path.
    /// </summary>
    private static void Judge(BoundStage.Resolve resolve, IReadOnlyList<OwnerCheck> checks, HashSet<OwnerCheck> answered, List<Miss> misses, List<QueryValidationError> errors, List<Diagnostic> notes)
    {
        var targets = (resolve.Cases is { Count: > 0 } cases ? cases.SelectMany(selected => selected.Targets) : [])
            .DistinctBy(target => (target.Declared.Entity, target.Declared.Field, target.Declared.Item))
            .ToList();

        foreach (var group in misses.GroupBy(miss => (miss.Path, miss.Parent)))
        {
            var (path, parent) = group.Key;
            var lacking = group.Select(miss => miss.Check).ToList();
            var first = group.First();
            var written = parent ? first.Check.Bound?.RemoteParentSelect : first.Check.Bound?.RemoteSelect;
            var stage = written?.Contains(path, StringComparer.Ordinal) == true ? first.Check.Stage : first.Check.ProjectStage ?? first.Check.Stage;
            var alias = parent ? resolve.ParentAs ?? resolve.As : resolve.As;

            bool Has(BoundResolveTarget target)
            {
                if (target.IsRemote)
                {
                    var check = checks.FirstOrDefault(each => each.Bound is { } other
                        && other.Declared.Entity == target.Declared.Entity && other.Declared.Item == target.Declared.Item && other.Declared.Field == target.Declared.Field);

                    return check is null || !answered.Contains(check) || !lacking.Contains(check);
                }

                if (target.Entity is not { } entity)
                    return false;

                var at = Shape.ForEntity(entity);

                if (!parent && target.Declared.Item is { } item && at.Resolve(item, PathUsage.Unwind) is { Succeeded: true } collection)
                    at = at.ForElement(collection.Path!);

                return at.Resolve(path, PathUsage.Project).Succeeded;
            }

            if (targets.Count > 1 && targets.Any(Has))
            {
                foreach (var check in lacking.DistinctBy(check => check.Target))
                    notes.Add(Notes.SelectPathDropped(stage, resolve.As, check.Target, path, parent));

                continue;
            }

            if (errors.Any(other => other.Code == Codes.UnknownPath && other.Stage == stage && other.Path == alias + "." + path))
                continue;

            errors.Add(new QueryValidationError
            {
                Code = Codes.UnknownPath,
                Message = $"'{alias}.{path}' is not a path of {(targets.Count > 1 ? "any target" : "the target")} of '{resolve.As}' ({string.Join(", ", lacking.Select(check => check.Target).Distinct(StringComparer.Ordinal))}).",
                Stage = stage,
                Path = alias + "." + path,
                Params = new Dictionary<string, object?>
                {
                    ["owner"] = new Dictionary<string, object?>
                    {
                        ["service"] = first.Check.Service,
                        ["entity"] = first.Check.Bound?.Declared.Entity,
                        ["target"] = first.Check.Target,
                        ["stage"] = first.Check.ProjectAt,
                        ["path"] = first.OwnerPath,
                    },
                },
            });
        }
    }

    /// <summary>
    /// An owner query explained at this host itself, as its SelfOwner would run it: nothing is
    /// forwarded or cached, and what the explain asks further owners shares nothing with this budget
    /// but the time. Without a way to explain here, the part is noted unchecked.
    /// </summary>
    private async Task<(JsonObject? Answer, string? Reason)> SelfAsync(ExplainRequest request, CancellationToken cancellationToken)
    {
        if (self is null)
            return (null, Unsupported);

        var answer = await self(request, cancellationToken).ConfigureAwait(false);

        return answer is null ? (null, Unsupported) : (answer, null);
    }

    /// <summary>One forwarded body: from the cache, else from the owner within what is left of the budget.</summary>
    private async Task<(JsonObject? Answer, string? Reason)> CallAsync(string service, ExplainRequest request, CancellationToken cancellationToken)
    {
        if (client is null)
            return (null, Unsupported);

        var key = ExplainForwardCache.KeyOf(context.Organisation ?? Guid.Empty, context.UserId, service, request);

        if (cache.Get(key) is { } kept)
            return (kept, null);

        var remaining = budget - clock.Elapsed;

        // A call cut by the budget spent it, whatever the clock reads a moment later (the timer may
        // fire a hair before the elapsed time reaches the budget).
        if (spent || remaining <= TimeSpan.Zero)
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
            spent = true;
            return (null, Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, Unreachable);
        }
    }

    /// <summary>Whether a call already ran out of the shared budget.</summary>
    private bool spent;

    private static Diagnostic Unchecked(OwnerCheck check, string reason) => new()
    {
        Code = Notes.RemoteUnchecked,
        Message = check.Service.Length == 0
            ? $"The stages continued under '{check.Target}' at this host were not checked ({reason}); this host binds them when the query runs."
            : reason == Skipped
            ? $"What '{check.Service}' binds for '{check.Target}' (its select, the paths under its alias, the stages continued there) was not checked: the request asked for remote \"skip\"."
            : $"What '{check.Service}' binds for '{check.Target}' (its select, the paths under its alias, the stages continued there) could not be checked at its owner ({reason}); the owner binds it when the query runs.",
        Stage = check.FirstContinued,
        Params = new Dictionary<string, object?> { ["service"] = check.Service.Length == 0 ? null : check.Service, ["target"] = check.Target, ["reason"] = reason },
    };
}
