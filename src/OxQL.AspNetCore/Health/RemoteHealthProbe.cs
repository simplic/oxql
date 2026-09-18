using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.AspNetCore.Health;

/// <summary>One service the model references remotely: known to this host, and answering when last asked.</summary>
public sealed record RemoteServiceState(string Service, bool Configured, bool? Reachable);

/// <summary>
/// The reachability behind <c>/oxql/health</c>, measured at most once per
/// <c>OxQL:Cache:HealthProbeTtlSeconds</c> and shared by every caller.
/// <para>
/// The endpoint is anonymous on purpose — it answers "why is my list not working" in a browser —
/// which is exactly why it must not turn a request into a fan-out. Probes run in parallel, one
/// measurement at a time serves everyone waiting for it, and a stale answer is served rather
/// than a fresh cost.
/// </para>
/// </summary>
public sealed class RemoteHealthProbe(OxQLOptions options, ILogger<RemoteHealthProbe>? logger = null)
{
    private static readonly TimeSpan PerService = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger logger = logger ?? (ILogger)NullLogger<RemoteHealthProbe>.Instance;
    private IReadOnlyList<RemoteServiceState>? measured;
    private DateTime takenAt = DateTime.MinValue;

    /// <summary>The state of every remotely referenced service, from the last measurement or a fresh one.</summary>
    public async Task<IReadOnlyList<RemoteServiceState>> StateAsync(EntityModel? model, IRemoteQueryClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Cache.HealthProbeTtlSeconds));

        if (measured is { } fresh && DateTime.UtcNow - takenAt < ttl)
            return fresh;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another caller may have measured while this one waited for the gate.
            if (measured is { } justTaken && DateTime.UtcNow - takenAt < ttl)
                return justTaken;

            var services = model is null ? [] : RemoteReferences.ServicesOf(model);
            var states = await Task.WhenAll(services.Select(service => ProbeAsync(service, client, cancellationToken))).ConfigureAwait(false);

            measured = states;
            takenAt = DateTime.UtcNow;

            return states;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemoteServiceState> ProbeAsync(string service, IRemoteQueryClient client, CancellationToken cancellationToken)
    {
        if (!client.IsConfigured(service))
            return new RemoteServiceState(service, false, null);

        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        probe.CancelAfter(PerService);

        try
        {
            return new RemoteServiceState(service, true, await client.IsReachableAsync(service, probe.Token).ConfigureAwait(false));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "OxQL health probe of {Service} failed", service);

            return new RemoteServiceState(service, true, false);
        }
    }
}
