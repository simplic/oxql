using OxQL.Core.Models;

namespace OxQL.Core.Engine;

/// <summary>
/// Why an owner is not asked right now: what the last call to it ended in
/// (<see cref="OwnerAvailability.Unreachable"/>, <see cref="OwnerAvailability.Timeout"/> or
/// <see cref="OwnerAvailability.Route"/>), the HTTP status when it answered with one, and how long
/// until it is asked again.
/// </summary>
public sealed record OwnerOutage(string Reason, int? Status, TimeSpan RetryAfter);

/// <summary>
/// What a host remembers between requests about owners that did not answer: one record per host,
/// written by the keyed fetch (a call that failed or succeeded) and by the health probe (a
/// measurement that answered, or one that could make no connection), read by the keyed fetch
/// before it calls.
/// <para>
/// After a failure the owner is not called for <c>OxQL:Cache:OwnerUnavailableTtlSeconds</c>: a
/// request that needs it gets the answer the failed call gave (the alias <c>null</c> with that
/// call's <c>RESOLVE_UNREACHABLE</c> or <c>RESOLVE_TIMEOUT</c>, a semi-join refused) at once, instead of waiting for the call's
/// budget again on every page. When the time is over nothing is remembered any more: every caller
/// asks the owner again (<see cref="TryEnter"/> answers true to all of them), so the queries that
/// one page sends side by side are all answered once the owner is back, and none of them from
/// memory. A call that fails then starts the time again. A success forgets the owner.
/// </para>
/// <para>
/// A failure is a call that got no answer (no connection, no answer in time) or an owner whose host
/// has no internal query route (HTTP 404). Any other HTTP error says something about the request
/// that was sent, not about whether the owner answers, and is not remembered; nor is an answer the
/// host's client could not read.
/// </para>
/// </summary>
public sealed class OwnerAvailability
{
    /// <summary>The reason of an owner no connection reached.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The reason of an owner that did not answer within the call's budget.</summary>
    public const string Timeout = "timeout";

    /// <summary>The reason of an owner whose host answered 404: no internal query route under the address this host calls.</summary>
    public const string Route = "route";

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan> ttl;
    private readonly TimeProvider time;

    /// <summary>The record of a host with <paramref name="options"/>, whose <c>Cache:OwnerUnavailableTtlSeconds</c> is read on every failure.</summary>
    public OwnerAvailability(OxQLOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        ttl = () => TimeSpan.FromSeconds(Math.Max(0, options.Cache.OwnerUnavailableTtlSeconds));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>A record that remembers a failure for <paramref name="ttl"/>; zero or less remembers nothing.</summary>
    public OwnerAvailability(TimeSpan ttl, TimeProvider? time = null)
    {
        this.ttl = () => ttl;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether <paramref name="service"/> may be called now. False with the remembered
    /// <paramref name="outage"/> while its time runs; true for an owner nothing is remembered of, and
    /// for every caller once the time is over.
    /// </summary>
    public bool TryEnter(string service, out OwnerOutage? outage)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (gate)
        {
            if (!entries.TryGetValue(service, out var entry))
            {
                outage = null;
                return true;
            }

            var left = entry.Until - time.GetUtcNow();

            if (left > TimeSpan.Zero)
            {
                outage = new OwnerOutage(entry.Reason, entry.Status, left);
                return false;
            }

            // The time is over, for everyone: callers that arrive together all ask, so what they are
            // parts of (the chunks of one page) is not answered half by the owner and half from memory.
            entries.Remove(service);

            outage = null;
            return true;
        }
    }

    /// <summary>What is remembered of <paramref name="service"/> right now, or null: a read that lets nobody in.</summary>
    public OwnerOutage? OutageOf(string service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (gate)
        {
            if (!entries.TryGetValue(service, out var entry))
                return null;

            var left = entry.Until - time.GetUtcNow();

            return left > TimeSpan.Zero ? new OwnerOutage(entry.Reason, entry.Status, left) : null;
        }
    }

    /// <summary>A call to <paramref name="service"/> failed for <paramref name="reason"/>: it is not called again until the time is over.</summary>
    public void Failed(string service, string reason, int? status = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(reason);

        var kept = ttl();

        if (kept <= TimeSpan.Zero)
            return;

        lock (gate)
            entries[service] = new Entry(reason, status, time.GetUtcNow() + kept);
    }

    /// <summary>
    /// <paramref name="service"/> answered: nothing is remembered of it any more. With
    /// <paramref name="keepRoute"/> a remembered <see cref="Route"/> stays, for a caller whose answer
    /// says the owner's host is up and nothing about its internal query route (a health measurement).
    /// </summary>
    public void Succeeded(string service, bool keepRoute = false)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (gate)
        {
            if (keepRoute && entries.TryGetValue(service, out var entry) && entry.Reason == Route)
                return;

            entries.Remove(service);
        }
    }

    private sealed record Entry(string Reason, int? Status, DateTimeOffset Until);
}
