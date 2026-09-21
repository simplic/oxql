using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.AspNetCore.Health;

/// <summary>
/// One service the model references remotely: known to this host, and answering when last
/// asked. <see cref="Reachable"/> is null for a service that is not configured, and for a
/// configured one that has not been measured yet.
/// </summary>
public sealed record RemoteServiceState(string Service, bool Configured, bool? Reachable);

/// <summary>
/// The reachability behind <c>/oxql/health</c>, measured at most once per
/// <c>OxQL:Cache:HealthProbeTtlSeconds</c> and shared by every caller.
/// <para>
/// The endpoint is anonymous on purpose — it answers "why is my list not working" in a browser —
/// which is exactly why a request must neither wait for a measurement nor be able to cause more
/// of them. <see cref="State"/> answers from the last measurement at once, however old it is;
/// when that measurement is older than the interval it starts one refresh in the background,
/// and no second one while the first is running. The refresh runs under its own timeout and
/// never under a caller's token, so a caller that disconnects neither cancels it nor makes the
/// next caller start it again. Before the first measurement has finished a configured service
/// is reported with an unknown reachability, which does not degrade the status.
/// </para>
/// </summary>
public sealed class RemoteHealthProbe(OxQLOptions options, ILogger<RemoteHealthProbe>? logger = null, TimeProvider? time = null)
{
    private readonly object gate = new();
    private readonly ILogger logger = logger ?? (ILogger)NullLogger<RemoteHealthProbe>.Instance;
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private IReadOnlyList<RemoteServiceState>? measured;
    private long? takenAt;
    private Task? refresh;

    /// <summary>How long one service may take to answer before it counts as not reachable.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The measurement that is running, or a completed task when none is. The request path never awaits it.</summary>
    public Task Refreshing
    {
        get
        {
            lock (gate)
                return refresh ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// The state of every remotely referenced service as last measured, without waiting: a
    /// measurement that is due is started in the background and answers a later call.
    /// </summary>
    public IReadOnlyList<RemoteServiceState> State(EntityModel? model, IRemoteQueryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Cache.HealthProbeTtlSeconds));

        lock (gate)
        {
            var due = takenAt is not { } taken || time.GetElapsedTime(taken) >= ttl;

            if (!due && measured is not null)
                return measured;

            var services = model is null ? [] : RemoteReferences.ServicesOf(model);

            if (due && refresh is null)
                refresh = Task.Run(() => RefreshAsync(services, client));

            return measured ?? services.Select(service => new RemoteServiceState(service, client.IsConfigured(service), null)).ToList();
        }
    }

    private async Task RefreshAsync(IReadOnlyList<string> services, IRemoteQueryClient client)
    {
        IReadOnlyList<RemoteServiceState>? states = null;

        try
        {
            states = await Task.WhenAll(services.Select(service => ProbeAsync(service, client))).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "OxQL health measurement failed; the last known state stays in place");
        }
        finally
        {
            // The interval restarts whether or not the measurement succeeded, so a client that
            // keeps failing is asked once per interval and not once per request.
            lock (gate)
            {
                measured = states ?? measured;
                takenAt = time.GetTimestamp();
                refresh = null;
            }
        }
    }

    private async Task<RemoteServiceState> ProbeAsync(string service, IRemoteQueryClient client)
    {
        if (!client.IsConfigured(service))
            return new RemoteServiceState(service, false, null);

        using var timeout = new CancellationTokenSource(ProbeTimeout);

        try
        {
            // WaitAsync bounds a client that does not observe the token.
            return new RemoteServiceState(service, true, await client.IsReachableAsync(service, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "OxQL health probe of {Service} failed", service);

            return new RemoteServiceState(service, true, false);
        }
    }
}
